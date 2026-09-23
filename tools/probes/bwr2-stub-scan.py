# -*- coding: utf-8 -*-
"""bwr2-stub-scan.py -- every "not implemented / fallback / placeholder" trace in the business tree.

Silent failure usually hides behind one of these markers: a TODO, a NotImplementedException, a
`throw new NotSupportedException`, a method that returns a hard-coded default, or a comment that
says the real thing is coming later ("占位" / "兜底" / "后续" / "暂时").  This probe lists every
occurrence with its context so each one can be adjudicated as "reachable silent failure" or
"benign".

Two collectors:
  MARKERS  -- keyword scan over BOTH code and comments (a promise in a comment is still a trace).
  DEFAULTS -- `return <literal/default>` inside a method whose name suggests a query
              (Get/Has/Is/Try/Find/Can/Should) -- heuristic, listed only when the return is a
              bare default (null / false / true / 0 / -1 / empty string / new empty collection).

Run:
  python tools/probes/bwr2-stub-scan.py [-o <dir>]
Writes (default dir = <root>/.ai-tmp/test/):
  bwr2-stub-scan.tsv
Exit code: always 0 (this is a measurement, not a gate).
ASCII-only stdout.
"""

import io
import os
import re
import sys

sys.dont_write_bytecode = True

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get('BWR2_ROOT') or os.path.dirname(os.path.dirname(HERE))
BUSINESS = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test')

MARKERS = [
    ('TODO', re.compile(r'\bTODO\b')),
    ('FIXME', re.compile(r'\bFIXME\b')),
    ('XXX', re.compile(r'\bXXX\b')),
    ('HACK', re.compile(r'\bHACK\b')),
    ('NotImplemented', re.compile(r'NotImplementedException')),
    ('NotSupported', re.compile(r'throw\s+new\s+[A-Za-z\.]*NotSupported')),
    ('placeholder', re.compile(u'占位')),
    ('fallback', re.compile(u'兜底')),
    ('later', re.compile(u'后续')),
    ('temporary', re.compile(u'暂时|临时')),
    ('unfinished', re.compile(u'未实现|尚未|暂不|留钩子|待补|待做|待接')),
    ('TBD', re.compile(r'\bTBD\b')),
]

QUERY_RE = re.compile(r'\b(?:public|private|protected|internal|static|\s)+[\w<>\[\],\.\?]+\s+'
                      r'(?:Get|Has|Is|Try|Find|Can|Should|Query)\w*\s*\(')
RETURN_DEFAULT_RE = re.compile(r'^\s*return\s+(null|false|true|-1|0|0f|0\.0f|""|string\.Empty'
                               r'|new\s+[A-Za-z_][\w\.<>]*\[\s*\]|default\([^)]*\))\s*;')


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


def main(argv):
    out_dir = DEFAULT_OUT
    if '-o' in argv:
        out_dir = argv[argv.index('-o') + 1]

    rows = []
    for path in cs_files(BUSINESS):
        text = read(path)
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        lines = text.split('\n')
        in_method = False
        for idx, ln in enumerate(lines, start=1):
            for name, rx in MARKERS:
                if rx.search(ln):
                    rows.append(('marker', name, rel, idx, ln.strip()))
                    break
            if QUERY_RE.search(ln):
                in_method = True
            if in_method and RETURN_DEFAULT_RE.match(ln):
                rows.append(('default-return', 'query-returns-default', rel, idx, ln.strip()))
            if re.match(r'^\s*\}\s*$', ln):
                in_method = False

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    out_path = os.path.join(out_dir, 'bwr2-stub-scan.tsv')
    with io.open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(u'# kind\tmarker\tfile\tline\tcode\n')
        for kind, name, rel, idx, src in rows:
            f.write(u'%s\t%s\t%s\t%d\t%s\n' % (kind, name, rel, idx, src))

    by_marker = {}
    for kind, name, rel, idx, src in rows:
        by_marker[name] = by_marker.get(name, 0) + 1
    print('scanned business .cs files = %d' % len(cs_files(BUSINESS)))
    print('traces = %d' % len(rows))
    for name in sorted(by_marker):
        print('  %-22s %d' % (name, by_marker[name]))
    print('files touched = %d' % len(set(r[2] for r in rows)))
    print('written: %s' % os.path.relpath(out_path, ROOT).replace('\\', '/'))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
