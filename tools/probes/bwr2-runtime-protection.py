# -*- coding: utf-8 -*-
"""bwr2-runtime-protection.py -- every runtime-created Unity object, and whether it is protected.

`audit-facade-and-runtime-objects.py` reports "sites whose creating FILE never mentions
hideFlags".  That predicate is too weak in one direction: a file that sets hideFlags once
(CombatEffects.cs) makes ALL of its sites look protected, while only some of them actually are.
This probe closes that hole at the per-site level.

Two caliber fixes over the first version of this probe (both reported by bu-v, both reworked here):

[A] COMMENTS ARE NO LONGER SITES.
    v1 matched the raw text, so XML doc comments that merely *mention* `AddComponent` were
    counted as runtime-creation sites (false positives).  Fix: the site match runs on
    comment-stripped code (the `strip_comments()` implemented in `bwr2-forbidden-api.py` is
    reused -- string / char / verbatim literals are respected, so a URL inside a string is not
    mistaken for a comment).  Line numbers still come from the original line index and the
    `code` column still shows the raw line, so the output shape is unchanged.
    Reproduced by: Module/Map/CsMapModule.cs:16, Module/Match/CsHitboxProxy.cs:12.

[B] "PROTECTED" IS A CONTAINMENT TEST, NOT A LINE WINDOW.
    v1 asked "is there a `hideFlags =` within -30/+5 lines?".  That misses a site whose
    assignment sits in the SAME method body but more than 30 lines away (real case:
    CombatEffects.cs:394/395/402 vs the assignment at :435 -- one method `Create(...)`,
    40+ lines apart -> was wrongly labelled same-file).  Fix: brace pairing gives every site its
    enclosing block ranges; a `hideFlags =` line inside ANY enclosing non-type block (i.e. the
    member body, not the class) counts as containment.

    The -30/+5 window is deliberately KEPT AS A SECOND, INDEPENDENT EVIDENCE PATH rather than
    demoted to a "block parsing failed" fallback, because a pure containment rule regresses the
    acceptance baseline `gizmo = 0`:
      FirstPersonCamera.cs:441 `_camera.gameObject.AddComponent<AudioListener>()` lives in
      `EnsureAudioListener()`, which sets no flags -- but it attaches to the very GameObject
      that `EnsureCamera()` protected at :415.  Containment alone cannot see that, the window
      can.  `--strict-containment` reproduces the regression (gizmo 0 -> 1) for review.
    A site with no resolvable enclosing block falls back to the window.

    ACCEPTED COST OF THE OR RULE (stated, not hidden): the window path is positional, so it can
    label a site `same-block` when the nearby assignment protects a DIFFERENT object.  Fixture
    `WindowNeighbour.cs` in `bwr2-selftest.py` pins exactly that case: a site whose enclosing
    method sets nothing, with a foreign object's assignment ~5 lines above.  The OR rule calls it
    `same-block` (loose by design); `--strict-containment` calls it `same-file`.  We accept the
    looseness to keep `gizmo = 0`, whose cause is a genuine cross-method same-object relation.

    WHY THE COST IS PRINTED ON EVERY RUN, NOT JUST DOCUMENTED: the two calibers fail in opposite
    directions -- strict produces a FALSE GAP (a real protection reported as missing, e.g. :441),
    OR produces a MISSED GAP (a real gap reported as protected, e.g. WindowNeighbour).  For a probe
    whose whole job is finding gaps, a missed gap is the dangerous direction, so the rows where the
    two calibers disagree are printed as `strict-only differences` on every default run.  The block
    always prints, including the literal `= 0 (no OR-only judgement)` case, so "nothing printed"
    can never be confused with "there were none".

KNOWN NOT DONE (this slice; do not read as a requirement):
  * Receiver matching.  The precise fix for the OR looseness is to require the assignment's
    receiver to be the variable the site assigns.  Comparing bare names is NOT enough: at
    FirstPersonCamera.cs:441 the assignment's receiver is the field path `_camera.gameObject`
    while the site creates the local `go` -- they are the same GameObject only through the alias
    chain `_camera = go.AddComponent<Camera>()` (FirstPersonCamera.cs:417).  Any real fix needs
    that alias chain, which is why it is left for its own slice.
  * String literals.  `strip_comments()` deliberately preserves string contents, so a literal
    such as `"hideFlags = x"` would count as one assignment line.  It affects only the
    `hideFlags assignment lines` counter, never a site's state; fixable in the same slice.

State legend:
  same-block  a hideFlags assignment is inside an enclosing member body or within the window
  same-file   the file mentions hideFlags, but not for this site (looks protected, is not)
  never       the file never mentions hideFlags at all (a component gizmo icon is expected)
Residue risk = (same-file + never) sites on components that Unity draws a gizmo for.

Caliber notes:
  * `hideFlags |=` IS counted (a real assignment; bu-v's new FirstPersonCamera.cs:554 uses it, and
    missing it would have made the probe blind to the protection just added).  The comparison
    `hideFlags ==` is NOT counted.  The printed line says `(includes |=)` so the number is not
    silently different from the earlier one (`4` -> `5`); bu-v was told to update its note.
  * `gizmo` and `sites NOT protected` stay the acceptance baseline reported by bu-v.

Run:
  python tools/probes/bwr2-runtime-protection.py [-o <dir>] [--strict-containment]
Writes (default dir = <root>/.ai-tmp/test/):
  bwr2-runtime-protection.tsv
Exit code: always 0 (measurement).
ASCII-only stdout.
"""

import importlib.util
import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get('BWR2_ROOT') or os.path.dirname(os.path.dirname(HERE))
BUSINESS = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test')

SITE_PATTERNS = [
    ('new GameObject', re.compile(r'\bnew\s+GameObject\s*\(')),
    ('CreatePrimitive', re.compile(r'\bGameObject\s*\.\s*CreatePrimitive\s*\(')),
    ('AddComponent', re.compile(r'\bAddComponent\s*<')),
    ('DontDestroyOnLoad', re.compile(r'\bDontDestroyOnLoad\s*\(')),
]
# Unity draws an editor gizmo icon for these component types.
GIZMO_RE = re.compile(r'AddComponent\s*<\s*(Light|Camera|AudioListener|AudioSource|'
                      r'BoxCollider|SphereCollider|CapsuleCollider|MeshCollider|Rigidbody)\s*>')
# Matches `hideFlags =` AND the compound `hideFlags |=` (a real assignment -- bu-v's own new
# protection line FirstPersonCamera.cs:554 uses it), but NOT the comparison `hideFlags ==`.
HIDEFLAGS_RE = re.compile(r'\bhideFlags\s*(?:\|)?=(?!=)')
TYPE_DECL_RE = re.compile(r'\b(?:class|struct|interface|enum|namespace)\b')
WINDOW_BACK = 30
WINDOW_FWD = 5


def load_strip_comments():
    """Reuse `strip_comments()` from the sibling probe instead of writing a second one."""
    path = os.path.join(HERE, 'bwr2-forbidden-api.py')
    spec = importlib.util.spec_from_file_location('bwr2_forbidden_api', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod.strip_comments


strip_comments = load_strip_comments()


def read(p):
    with io.open(p, encoding='utf-8', errors='replace') as f:
        return f.read()


def cs_files(root):
    out = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in ('Library', 'Temp', 'obj', 'bin')]
        for fn in filenames:
            if fn.endswith('.cs'):
                out.append(os.path.join(dirpath, fn))
    out.sort()
    return out


def brace_index(code_lines):
    """Pair every brace with its matching one and record the enclosing-block stack per line.

    -> (pairs, enclosing, type_block)
       pairs      {open_line: close_line}
       enclosing  {line: (open_line, ...)} outermost first, innermost last, BEFORE that line
       type_block {open_line: True} for class / struct / interface / enum / namespace bodies
    Comment-stripped text is fed in, so braces inside comments or strings never count.
    """
    pairs = {}
    enclosing = {}
    type_block = {}
    stack = []
    for idx, ln in enumerate(code_lines, start=1):
        enclosing[idx] = tuple(stack)
        for ch in ln:
            if ch == '{':
                # The block "belongs to" a type when its header names one.  The header is the
                # text before the brace on this line; when the brace sits alone (K&R style) the
                # immediately preceding non-empty line is the header -- deliberately NOT
                # walking further back, or a method right under `class Foo` would inherit the
                # `class` keyword and be skipped as a type block.
                head = ln[:ln.index('{')]
                if not head.strip():
                    j = idx - 2
                    while j >= 0 and not code_lines[j].strip():
                        j -= 1
                    if j >= 0:
                        head = code_lines[j]
                if TYPE_DECL_RE.search(head):
                    type_block[idx] = True
                stack.append(idx)
            elif ch == '}':
                if stack:
                    pairs[stack.pop()] = idx
    return pairs, enclosing, type_block


def contained_by_member(line_no, hide_lines, pairs, enclosing, type_block):
    """True when a hideFlags assignment sits inside any enclosing NON-type block of this site."""
    for open_line in enclosing.get(line_no, ()):
        if type_block.get(open_line):
            continue
        close_line = pairs.get(open_line)
        if close_line is None:
            continue
        if any(open_line <= h <= close_line for h in hide_lines):
            return True
    return False


def main(argv):
    out_dir = DEFAULT_OUT
    if '-o' in argv:
        out_dir = argv[argv.index('-o') + 1]
    strict = '--strict-containment' in argv

    rows = []
    total_hideflags_lines = 0
    for path in cs_files(BUSINESS):
        raw_text = read(path)
        code_text = strip_comments(raw_text)
        raw_lines = raw_text.split('\n')
        code_lines = code_text.split('\n')
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        file_has = bool(HIDEFLAGS_RE.search(code_text))
        hide_lines = [i for i, ln in enumerate(code_lines, start=1) if HIDEFLAGS_RE.search(ln)]
        total_hideflags_lines += len(hide_lines)
        pairs, enclosing, type_block = brace_index(code_lines)
        for idx, ln in enumerate(code_lines, start=1):
            for kind, rx in SITE_PATTERNS:
                if not rx.search(ln):
                    continue
                in_member = contained_by_member(idx, hide_lines, pairs, enclosing, type_block)
                lo = max(1, idx - WINDOW_BACK)
                hi = min(len(code_lines), idx + WINDOW_FWD)
                in_window = any(HIDEFLAGS_RE.search(code_lines[j - 1]) for j in range(lo, hi + 1))
                fallback = 'same-file' if file_has else 'never'
                state_or = 'same-block' if (in_member or in_window) else fallback
                state_strict = 'same-block' if in_member else fallback
                state = state_strict if strict else state_or
                gizmo = 'gizmo' if GIZMO_RE.search(ln) else '-'
                src = raw_lines[idx - 1].strip() if idx - 1 < len(raw_lines) else ''
                rows.append((rel, idx, kind, state, gizmo, src, state_or, state_strict))
                break

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    out_path = os.path.join(out_dir, 'bwr2-runtime-protection.tsv')
    with io.open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(u'# file\tline\tkind\tprotection\tgizmo\tcode\n')
        for r in rows:
            f.write(u'%s\t%d\t%s\t%s\t%s\t%s\n' % r[:6])

    by_state = {}
    for r in rows:
        by_state[r[3]] = by_state.get(r[3], 0) + 1
    unprotected = sum(1 for r in rows if r[3] != 'same-block')
    gizmo_unprotected = sum(1 for r in rows if r[3] != 'same-block' and r[4] == 'gizmo')
    print('mode = %s' % ('strict-containment (window demoted to fallback)' if strict
                         else 'containment OR window'))
    print('runtime-created sites = %d' % len(rows))
    for s in ('same-block', 'same-file', 'never'):
        print('  %-11s %d' % (s, by_state.get(s, 0)))
    print('hideFlags assignment lines = %d (includes |=)' % total_hideflags_lines)
    print('sites NOT protected = %d   of which gizmo-drawing components = %d'
          % (unprotected, gizmo_unprotected))
    for r in rows:
        if r[3] != 'same-block' and r[4] == 'gizmo':
            print('  GIZMO-UNPROTECTED  %s:%d  %s' % (r[0], r[1], r[5]))

    # Rows where the two calibers disagree = the rows the OR rule may be covering up.  Printed
    # unconditionally, and the zero case says so explicitly, so a silent probe cannot be mistaken
    # for a clean one.
    diffs = [r for r in rows if r[6] != r[7]]
    print('strict-only differences = %d%s' % (
        len(diffs), ' (no OR-only judgement)' if not diffs else
        '   <- rows OR calls protected but strict-containment does not; each one is a possible '
        'MISSED GAP, check by hand'))
    for r in diffs:
        print('  - %s:%d  %s  (OR: %s / strict: %s)' % (r[0], r[1], r[5], r[6], r[7]))

    print('written: %s' % os.path.relpath(out_path, ROOT).replace('\\', '/'))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
