# -*- coding: utf-8 -*-
"""audit-facade-and-runtime-objects.py -- two silent-failure classes, enumerated offline.

CLASS 1 ("facility exists but is never used" -- the pattern the user reported for the engine
behaviour-tree / bot AI): every member the engine facade exposes on `Game` (parsed out of
`client/Packages/com.clover.unity-engine/Runtime/Core/Game.cs`) is counted in the BUSINESS
assembly (`client/Assets/Scripts/**`).  A member with zero call sites is either genuinely
unused or a missing integration -- either way it must be listed with a number, not a feeling.

CLASS 2 ("something that must not be on screen": Unity's component gizmo icons / residue such
as the speaker or sun glyph the user saw): every RUNTIME-CREATED object is enumerated --
`new GameObject(`, `GameObject.CreatePrimitive(`, `AddComponent<`, `DontDestroyOnLoad(` --
together with whether the creating file ever touches `hideFlags`.  An editor gizmo icon needs
BOTH a component AND `hideFlags` left at default; so "creates components, never sets
hideFlags" is the mechanical predicate for this class.

Both enumerations are stable-sorted so the TSV can be diffed across runs.

Run:
  python tools/probes/audit-facade-and-runtime-objects.py [-o <dir>]
Writes (default dir = <root>/.ai-tmp/test/):
  bw-facade-usage.tsv
  bw-runtime-objects.tsv
ASCII-only stdout.
"""

import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
ENGINE = os.path.join(ROOT, 'client', 'Packages', 'com.clover.unity-engine', 'Runtime', 'Core', 'Game.cs')
BUSINESS = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test')

PROP_RE = re.compile(r'^\s*public static\s+[A-Za-z_][\w<>\.\[\]]*\s+(\w+)\s*\{')
NEW_GO_RE = re.compile(r'\bnew\s+GameObject\s*\(')
PRIM_RE = re.compile(r'\bGameObject\s*\.\s*CreatePrimitive\s*\(')
ADD_RE = re.compile(r'\bAddComponent\s*<')
DDOL_RE = re.compile(r'\bDontDestroyOnLoad\s*\(')
CLASS_RE = re.compile(r'\b(?:class|struct)\s+(\w+)')
HIDEFLAGS_RE = re.compile(r'\bhideFlags\b')


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
    return sorted(out)


def rel(p):
    return os.path.relpath(p, ROOT).replace('\\', '/')


def write_tsv(path, header, rows):
    with io.open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# ' + header + '\n')
        for r in rows:
            f.write('\t'.join(str(x).replace('\t', ' ').replace('\n', ' ') for x in r) + '\n')


def main():
    outdir = DEFAULT_OUT
    if '-o' in sys.argv:
        outdir = sys.argv[sys.argv.index('-o') + 1]
    os.makedirs(outdir, exist_ok=True)

    if not os.path.exists(ENGINE):
        print('FAIL: engine facade not found at ' + rel(ENGINE))
        return 1

    members = []
    for ln in read(ENGINE).split('\n'):
        m = PROP_RE.match(ln)
        if m and m.group(1) not in members:
            members.append(m.group(1))

    files = cs_files(BUSINESS)
    texts = {}
    for p in files:
        texts[p] = read(p)

    print('engine facade members = %d' % len(members))
    print('business .cs files    = %d' % len(files))
    print('--- facade member usage in business (call sites) ---')

    usage = []
    for name in members:
        pat = re.compile(r'\bGame\s*\.\s*' + re.escape(name) + r'\b')
        sites = []
        for p in files:
            for i, ln in enumerate(texts[p].split('\n'), 1):
                if pat.search(ln):
                    sites.append((rel(p), i))
        usage.append((name, len(sites), sites))
    usage.sort(key=lambda r: (r[1], r[0]))

    for name, n, sites in usage:
        flag = 'ZERO ' if n == 0 else '     '
        print('%s%-14s %3d' % (flag, name, n))

    write_tsv(os.path.join(outdir, 'bw-facade-usage.tsv'),
              'member\tcall-sites-in-business\tfirst-call-sites',
              [(n, c, ';'.join('%s:%d' % s for s in sites[:6])) for n, c, sites in usage])

    zero = [n for n, c, _ in usage if c == 0]
    print('facade members with ZERO business call site = %d : %s'
          % (len(zero), ','.join(zero) if zero else '-'))

    # ---------------------------------------------------------------- class 2
    rows = []
    for p in files:
        txt = texts[p]
        lines = txt.split('\n')
        cls = '-'
        flags_in_file = 1 if HIDEFLAGS_RE.search(txt) else 0
        for i, ln in enumerate(lines, 1):
            m = CLASS_RE.search(ln)
            if m:
                cls = m.group(1)
            kind = None
            if NEW_GO_RE.search(ln):
                kind = 'new GameObject'
            elif PRIM_RE.search(ln):
                kind = 'CreatePrimitive'
            elif ADD_RE.search(ln):
                kind = 'AddComponent'
            elif DDOL_RE.search(ln):
                kind = 'DontDestroyOnLoad'
            if kind is None:
                continue
            # crude locality: hideFlags within +-6 lines counts as "set here"
            near = any(HIDEFLAGS_RE.search(x) for x in lines[max(0, i - 7):i + 6])
            rows.append((rel(p), i, cls, kind, 'near' if near else ('file-only' if flags_in_file else 'never'),
                         ln.strip()[:120]))
    rows.sort(key=lambda r: (r[3], r[0], r[1]))

    import collections
    cnt = collections.Counter(r[3] for r in rows)
    never = sum(1 for r in rows if r[4] == 'never')
    print('--- runtime-created objects ---')
    print('total sites = %d  %s' % (len(rows), ' '.join('%s=%d' % (k, cnt[k]) for k in sorted(cnt))))
    print('sites whose creating FILE never mentions hideFlags = %d' % never)

    write_tsv(os.path.join(outdir, 'bw-runtime-objects.tsv'),
              'file\tline\tclass\tkind\thideFlags-proximity\tsource',
              rows)
    print('written: ' + rel(os.path.join(outdir, 'bw-facade-usage.tsv')))
    print('written: ' + rel(os.path.join(outdir, 'bw-runtime-objects.tsv')))
    return 0


if __name__ == '__main__':
    sys.exit(main())
