# -*- coding: utf-8 -*-
"""reinject-verdict-entities.py -- align the verdict block's ENTITY-NAME column with the entity list.

WHY THIS EXISTS
---------------
Slice BW-R.  `tools/probes/audit-coverage-reconcile.py` (the judgement asset that the
`coverage-rows` gate now calls) judges the COVERAGE RELATION between the entity list and
the verdict block: (1) every listed entity needs >= 1 verdict row, (2) every verdict row
must point at a listed entity.  Measured on the real plan on 2026-09-22: unjudged=350
phantom=350 -- the verdict block carried `MltryCrteSd .png` while the entity list carried
`MltryCrteSd.png` (one extra space before the extension), so those 350 rows matched
nothing at all and the old row-count gate hid it.

WHAT IT DOES (and what it refuses to do)
----------------------------------------
* rewrites ONLY the entity-name cell (cell 3) of the rows whose name does not match the
  entity list; every other cell (dimension / criterion type / conclusion / evidence) is
  copied byte-for-byte, and that is ASSERTED per line (cells_after == cells_before except
  cell 3) before anything is written;
* a rename is applied only when the mapping is unambiguous: the whitespace-stripped
  phantom name must match exactly ONE whitespace-stripped listed entity, and no two
  phantoms may collapse onto the same entity.  Anything ambiguous is reported and left
  alone -- guessing a name is how a gate gets gamed (reference/anti-gaming.md);
* idempotent: a second run finds nothing to rename and leaves the file byte-identical;
* keeps a ONE-TIME backup under <root>/.ai-tmp/test/ before the first write (rollback);
* writes a per-line trace of every rename plus an "is the evidence cell really anchored?"
  ledger covering every renamed row, so "the names now match" can never be mistaken for
  "these rows were really judged".

It never invents a verdict and never touches the entity list / the acceptance table /
the state matrix (those belong to other slices; re-running the generator is forbidden
while they are being edited).

Rollback: copy the backup over `plan/<verdict fragment>.md`.

Run:   python tools/probes/reinject-verdict-entities.py [--dry-run]
Exit 0 = aligned (or already aligned); 1 = nothing written (missing input / no unsafe guess).
ASCII-only stdout (cp936 console safe).
"""

import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')                                 # plan/
TMP = os.path.join(ROOT, '.ai-tmp', 'test')
F_ENT = '\u5b9e\u4f53\u6e05\u5355.tsv'                                    # entity-list.tsv
F_FRG = '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'                # verdict fragment
BAK = F_FRG + '.bak'
TRACE = '\u7247BW-R-\u540d\u5b57\u5bf9\u9f50\u7559\u75d5.tsv'              # per-line rename trace
LEDGER = '\u7247BW-R-\u672a\u771f\u5224\u884c.tsv'                         # rows whose evidence is not anchored
COV_BEGIN = '<!-- COVERAGE-BEGIN -->'
COV_END = '<!-- COVERAGE-END -->'

ROW_RE = re.compile(r'^\|\s*(\d+)\s*\|')
# an evidence cell counts as ANCHORED when it carries something a third party can re-run or
# re-open: a `file:line`, a `guid=<hash>` asset identity, a path/name with a real extension,
# or a measurement of the shape `key=<number>` (verts=144 tris=72 ...).
RE_LINE = re.compile(r':\d+')
RE_FILE = re.compile(r'[A-Za-z0-9_\-./\\]+\.(png|jpg|jpeg|bmp|tsv|cs|py|ps1|md|txt|json|'
                     r'mdl|wav|prefab|bytes|controller|mat|asset|shader|log|cfg|scr|res|bmp)\b',
                     re.I)
RE_GUID = re.compile(r'guid=[0-9a-f]{8,}', re.I)
RE_NUM = re.compile(r'[A-Za-z_]{2,}=<?[-+]?\d')

DRY = '--dry-run' in sys.argv


def ascii_(s):
    return str(s).encode('ascii', 'backslashreplace').decode('ascii')


def read_bytes(path):
    with open(path, 'rb') as f:
        return f.read()


def rd(path):
    return read_bytes(path).decode('utf-8', 'replace')


def cells(line):
    return [c.strip() for c in line.split('|')]


def tsv_rows(text):
    out = []
    for ln in text.split('\n'):
        if ln.strip() and not ln.startswith('#'):
            out.append(ln)
    return out


def anchored(ev):
    """Classify an evidence cell. Returns (True, reason) / (False, reason)."""
    if not ev:
        return False, 'empty evidence cell'
    if RE_LINE.search(ev):
        return True, 'file:line'
    if RE_GUID.search(ev):
        return True, 'guid identity'
    m = RE_FILE.search(ev)
    if m:
        tok = m.group(0).replace('\\', '/')
        for cand in (os.path.join(ROOT, tok), os.path.join(ROOT, 'client', tok)):
            if os.path.exists(cand):
                return True, 'carrier on disk'
        return False, 'file token not on disk'
    if RE_NUM.search(ev):
        return True, 'measurement'
    return False, 'prose only (no path / guid / measurement)'


def main():
    ent_p = os.path.join(PLAN, F_ENT)
    frg_p = os.path.join(PLAN, F_FRG)
    for p in (ent_p, frg_p):
        if not os.path.exists(p):
            print('FAIL: missing ' + ascii_(p))
            return 1

    ent_names = []
    for ln in tsv_rows(rd(ent_p)):
        cs = [c.strip() for c in ln.split('\t')]
        if len(cs) >= 3:
            ent_names.append(cs[1])
    ent_set = set(ent_names)

    raw = read_bytes(frg_p).decode('utf-8', 'replace')
    eol = '\r\n' if '\r\n' in raw else '\n'
    lines = raw.split(eol)

    judge_idx = [i for i, ln in enumerate(lines) if ROW_RE.match(ln)]
    judge_names = [cells(lines[i])[3] if len(cells(lines[i])) >= 4 else '' for i in judge_idx]
    jset = set(n for n in judge_names if n)

    unjudged = sorted(ent_set - jset)      # in the list, no verdict row
    phantom = sorted(jset - ent_set)       # verdict row points at a name not in the list
    print('entity list names = %d   verdict rows = %d   unjudged = %d   phantom = %d'
          % (len(ent_set), len(judge_names), len(unjudged), len(phantom)))
    if not phantom and not unjudged:
        print('RESULT: already aligned -- nothing to rewrite (idempotent no-op)')
        return 0

    # ---- build the rename map (unambiguous only) ---------------------------
    def norm(s):
        return re.sub(r'\s+', '', s)

    by_norm = {}
    for n in unjudged:
        by_norm.setdefault(norm(n), []).append(n)

    rename = {}
    unresolved = []
    for p in phantom:
        cands = by_norm.get(norm(p), [])
        if len(cands) == 1:
            rename[p] = cands[0]
        else:
            unresolved.append((p, cands))
    for p in unresolved:
        print('  UNRESOLVED phantom (no unique listed entity after whitespace strip): ' + ascii_(p[0]))
    # injectivity: two phantoms collapsing onto one entity would leave the other entity unjudged
    rev = {}
    for p, n in rename.items():
        rev.setdefault(n, []).append(p)
    collide = {n: ps for n, ps in rev.items() if len(ps) > 1}
    for n in collide:
        for p in collide[n]:
            rename.pop(p, None)
            unresolved.append((p, ['ambiguous: also ' + ascii_(c) for c in collide[n] if c != p]))
    if unresolved:
        for p, why in unresolved:
            print('  AMBIGUOUS  ' + ascii_(p[0]) + '  -> ' + ascii_(str(why)))
        print('RESULT: FAIL -- %d drifted name(s) cannot be mapped unambiguously; nothing written'
              % len(unresolved))
        return 1
    print('rename map = %d drifted name(s), all 1:1 onto listed entities' % len(rename))

    # ---- rewrite the entity-name cell ONLY (assert the rest is untouched) --
    changed = []
    new_lines = list(lines)
    for i in judge_idx:
        line = lines[i]
        cs = cells(line)
        if len(cs) < 4 or cs[3] not in rename:
            continue
        old, new = cs[3], rename[cs[3]]
        # replace inside the raw cell between the 3rd and 4th '|' so all spacing/other cells survive
        a = -1
        for k in range(3):
            a = line.find('|', a + 1)
        b = line.find('|', a + 1)
        if a < 0 or b < 0:
            print('FAIL: cannot locate the entity cell on line %d' % (i + 1))
            return 1
        cell_raw = line[a:b + 1]
        if cell_raw.count(old) != 1:
            print('FAIL: entity cell on line %d does not contain the drifted name exactly once' % (i + 1))
            return 1
        nline = line[:a] + cell_raw.replace(old, new, 1) + line[b + 1:]
        if cells(nline) != cs[:3] + [new] + cs[4:]:
            print('FAIL: line %d would change a cell other than the entity name -- aborting' % (i + 1))
            return 1
        new_lines[i] = nline
        # cs = ['', '#', dim, entity, criterion type, conclusion, evidence, ''] -> index by cell
        changed.append((i + 1, old, new, cs[2], cs[4], cs[5], cs[6]))
    print('rows to rewrite = %d' % len(changed))
    if not changed:
        print('RESULT: FAIL -- drifted names found but no row matched them; nothing written')
        return 1

    # ---- backup (one-time) + write (unless --dry-run) ----------------------
    if not os.path.isdir(TMP):
        os.makedirs(TMP)
    bak = os.path.join(TMP, BAK)
    if not os.path.exists(bak):
        with open(bak, 'wb') as f:
            f.write(raw.encode('utf-8'))
        print('backup written  ' + ascii_(bak))
    else:
        print('backup kept     ' + ascii_(bak) + ' (already existed; the pre-change state is never overwritten)')
    out = eol.join(new_lines)
    if DRY:
        print('dry-run: file NOT written')
    else:
        with open(frg_p, 'wb') as f:
            f.write(out.encode('utf-8'))
        print('written         ' + ascii_(frg_p))

    # ---- re-load and re-judge (self-check: the relation must now hold) -----
    after = rd(frg_p) if not DRY else out
    alines = [ln for ln in after.split(eol) if ROW_RE.match(ln)]
    an = set(cells(ln)[3] for ln in alines if len(cells(ln)) >= 4)
    left_u = sorted(ent_set - an)
    left_p = sorted(an - ent_set)
    print('after: verdict rows = %d  unjudged = %d  phantom = %d' % (len(alines), len(left_u), len(left_p)))
    if left_u or left_p:
        for n in left_u[:10]:
            print('    still unjudged: ' + ascii_(n))
        for n in left_p[:10]:
            print('    still phantom : ' + ascii_(n))
        if not DRY:
            print('RESULT: FAIL -- relation still broken; restore with: copy ' + ascii_(bak) + ' over the fragment')
        else:
            print('RESULT: FAIL (dry-run) -- see above')
        return 1

    # ---- per-line trace ---------------------------------------------------
    tpath = os.path.join(TMP, TRACE)
    with io.open(tpath, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# \u884c\u53f7\t\u65e7\u5b9e\u4f53\u540d\t\u65b0\u5b9e\u4f53\u540d\t\u7ef4\u5ea6\t\u7ed3\u8bba\n')
        for ln, old, new, dim, ctype, verdict, ev in changed:
            f.write('%d\t%s\t%s\t%s\t%s\n' % (ln, old, new, dim, verdict))
    print('trace written   ' + ascii_(tpath) + ' (%d rows)' % len(changed))

    # ---- honest ledger: name alignment is NOT the same as "was judged" ----
    bad = []
    for ln, old, new, dim, ctype, verdict, ev in changed:
        ok, why = anchored(ev)
        if not ok:
            bad.append((ln, dim, new, ctype, verdict, ev, why))
    lpath = os.path.join(TMP, LEDGER)
    with io.open(lpath, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# \u884c\u53f7\t\u7ef4\u5ea6\t\u5b9e\u4f53(\u5bf9\u9f50\u540e)\t\u5224\u636e\u7c7b\u578b\t\u7ed3\u8bba\t\u8bc1\u636e\t\u5224\u5b9a\t\u539f\u56e0\n')
        for ln, dim, new, ctype, verdict, ev, why in bad:
            f.write('%d\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n'
                    % (ln, dim, new, ctype, verdict, ev, '\u672a\u771f\u5224', why))
    print('ledger written  ' + ascii_(lpath) + ' (%d of %d renamed row(s) have NO anchored evidence => '
          'name alignment alone does not judge them)' % (len(bad), len(changed)))
    print('RESULT: PASS -- %d name(s) aligned; unjudged=0 phantom=0; %d row(s) still need real evidence'
          % (len(changed), len(bad)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
