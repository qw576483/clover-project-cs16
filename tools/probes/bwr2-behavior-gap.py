# -*- coding: utf-8 -*-
"""bwr2-behavior-gap.py -- behaviour-class dimensions vs their verdict rows (thin-dimension probe).

SKILL section 6 item 2: a game is a COMPOSITE product -- "geometry right + collision wrong => you
can walk through crates", "animation data present + never played => a wooden man".  Resource and
animation dimensions are enumerated straight off the disk, so they dominate the row count; the
behaviour dimensions (effects / music / collision / flow / performance / settings) are only as
large as somebody bothered to write down.

This probe counts, for each behaviour dimension, the entities that ACTUALLY exist in code (regex
evidence over the business tree) and prints them next to the number of verdict rows that dimension
owns, so the gap is a number instead of an opinion.

Run:
  python tools/probes/bwr2-behavior-gap.py [-o <dir>]
Writes (default dir = <root>/.ai-tmp/test/):
  bwr2-behavior-gap.tsv
Exit code: always 0 (measurement).
ASCII-only stdout.
"""

import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
BUSINESS = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
FRAGMENT = os.path.join(ROOT, '策划', '覆盖矩阵判定.fragment.md')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test')

EFFECTS = os.path.join(BUSINESS, 'Module', 'Combat', 'CombatEffects.cs')
RESPATHS = os.path.join(BUSINESS, 'Core', 'ResPaths.cs')
APPFLOW = os.path.join(BUSINESS, 'Module', 'Flow', 'AppFlow.cs')
OPTIONS = os.path.join(BUSINESS, 'UI', 'Flow', 'OptionsPanel.cs')
MATCH = os.path.join(BUSINESS, 'Module', 'Match')

SHAPE_ENUM_RE = re.compile(r'enum\s+Shape\s*\{([^}]*)\}')
STATE_USE_RE = re.compile(r'\bState\s*\.\s*([A-Z]\w+)')
PHASE_USE_RE = re.compile(r'\b(?:CsRoundPhase|Phase)\s*\.\s*([A-Z]\w+)')
REASON_USE_RE = re.compile(r'\b(?:CsRoundEndReason|RoundEndReason)\s*\.\s*([A-Z]\w+)')


def distinct_after(path, rx):
    """Distinct identifiers matched after a prefix, across a single file or a whole directory."""
    if os.path.isdir(path):
        text = '\n'.join(read(os.path.join(dp, fn))
                         for dp, dn, fns in os.walk(path) for fn in fns if fn.endswith('.cs'))
        return len(set(rx.findall(text)))
    if not os.path.isfile(path):
        return -1
    return len(set(rx.findall(read(path))))

ROW_RE = re.compile(r'^\|\s*\d+\s*\|\s*(D\d+|S\d)\s*\|')


def read(p):
    with io.open(p, encoding='utf-8', errors='replace') as f:
        return f.read()


def count_dim_rows():
    counts = {}
    if not os.path.isfile(FRAGMENT):
        return counts
    for ln in read(FRAGMENT).split('\n'):
        m = ROW_RE.match(ln)
        if m:
            counts[m.group(1)] = counts.get(m.group(1), 0) + 1
    return counts


def count_in(path, rx):
    if os.path.isdir(path):
        total = 0
        for dirpath, dirnames, filenames in os.walk(path):
            dirnames[:] = [d for d in dirnames if d not in ('Library', 'Temp', 'obj', 'bin')]
            for fn in filenames:
                if fn.endswith('.cs'):
                    total += len(rx.findall(read(os.path.join(dirpath, fn))))
        return total
    if not os.path.isfile(path):
        return -1
    return len(rx.findall(read(path)))


def build_counts():
    """-> list of (dim, what, in-code count).  Every entry is a state-space size, not a file count."""
    effects_text = read(EFFECTS) if os.path.isfile(EFFECTS) else ''
    shapes = 0
    m = SHAPE_ENUM_RE.search(effects_text)
    if m:
        shapes = len([x for x in m.group(1).split(',') if x.strip()])
    return [
        ('D6', 'effect shapes (enum Shape members in CombatEffects.cs)', shapes),
        ('D6', 'ResPaths.Fx* asset constants', count_in(RESPATHS, re.compile(r'public const string Fx\w+'))),
        ('D7', 'ResPaths.Bgm* asset constants', count_in(RESPATHS, re.compile(r'public const string Bgm\w+'))),
        ('D8', 'ResPaths Sound/Sfx asset constants (events are judged elsewhere)',
         count_in(RESPATHS, re.compile(r'public const string (?:Sfx|Sound)\w+'))),
        ('D9', 'distinct collider types referenced in Module/**',
         len(set(re.findall(r'\b(BoxCollider|SphereCollider|CapsuleCollider|MeshCollider|'
                            r'CharacterController|Rigidbody)\b',
                            '\n'.join(read(os.path.join(dp, fn)) for dp, dn, fns in os.walk(BUSINESS)
                                          for fn in fns if fn.endswith('.cs')))))),
        ('D9', 'collider-typed identifiers in Module/Match + Module/Map',
         count_in(MATCH, re.compile(r'\bCollider\b')) + count_in(os.path.join(BUSINESS, 'Module', 'Map'),
                                                                re.compile(r'\bCollider\b'))),
        ('D10', 'distinct round phases referenced', distinct_after(MATCH, PHASE_USE_RE)),
        ('D10', 'distinct round-end reasons referenced', distinct_after(MATCH, REASON_USE_RE)),
        ('D11', 'GameKey reads in business',
         count_in(BUSINESS, re.compile(r'\bGetKey(?:Down|Up)?\s*\(\s*GameKey\.'))),
        ('D12', 'distinct flow states referenced in AppFlow.cs', distinct_after(APPFLOW, STATE_USE_RE)),
        ('S1', 'public const tuning values under Core/**', count_in(os.path.join(BUSINESS, 'Core'),
                                                                   re.compile(r'public const\s'))),
        ('S2', 'perf probes under tools/probes', count_in(os.path.join(ROOT, 'tools', 'probes'),
                                                          re.compile(r'frametime|performance|gpu|cpu',
                                                                     re.IGNORECASE))),
        ('S3', 'Options controls (new ResCtrl)', count_in(OPTIONS, re.compile(r'new\s+ResCtrl\s*\('))),
    ]


def main(argv):
    out_dir = DEFAULT_OUT
    if '-o' in argv:
        out_dir = argv[argv.index('-o') + 1]

    rows = count_dim_rows()
    out = []
    for dim, what, code_n in build_counts():
        out.append((dim, what, code_n, rows.get(dim, 0)))

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    out_path = os.path.join(out_dir, 'bwr2-behavior-gap.tsv')
    with io.open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(u'# dim\tin-code\tverdict-rows\tsource\n')
        for dim, what, code_n, row_n in out:
            f.write(u'%s\t%d\t%d\t%s\n' % (dim, code_n, row_n, what))

    print('%-5s %8s %8s  %s' % ('dim', 'in-code', 'rows', 'what'))
    for dim, what, code_n, row_n in out:
        print('%-5s %8d %8d  %s' % (dim, code_n, row_n, what))
    print('verdict rows per dimension (parsed from fragment):')
    for dim in sorted(rows):
        print('  %-4s %d' % (dim, rows[dim]))
    print('written: %s' % os.path.relpath(out_path, ROOT).replace('\\', '/'))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
