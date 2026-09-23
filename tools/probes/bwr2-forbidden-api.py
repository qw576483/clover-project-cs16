# -*- coding: utf-8 -*-
"""bwr2-forbidden-api.py -- complete hit table for the DISALLOWED APIs (SKILL section 3 item 4).

The rule: `PlayerPrefs` / bare `Input` / `GameObject.Find` / `FindObjectOfType` /
`Instantiate(` / `Resources.Load` are gated by tools/verify.ps1.  A legal exception must be
registered row-by-row in the differences registry (plan/差异登记.tsv); writing it only in a
code comment is NOT a registration (SKILL section 3 item 4).

This probe enumerates EVERY raw hit in the business tree (comments excluded), and for each hit
reports whether the registry mentions the file (basename) or the exact `file:line`, so
"registered" is a mechanical read of the registry rather than a feeling.

Additional runtime-creation forms (`new GameObject` / `CreatePrimitive` / `DontDestroyOnLoad`)
are reported too, because they are the C-class scan of slice BW-R2 and share the same
"must be registered" obligation.

Run:
  python tools/probes/bwr2-forbidden-api.py [-o <dir>]
Writes (default dir = <root>/.ai-tmp/test/):
  bwr2-forbidden-api.tsv
Exit code: 1 when at least one raw hit is mentioned nowhere in the registry.
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
REGISTRY = os.path.join(ROOT, '策划', '差异登记.tsv')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test')

# name -> regex.  Order matters: first match wins per (file, line).
PATTERNS = [
    ('PlayerPrefs', re.compile(r'\bPlayerPrefs\b')),
    ('GameObject.Find', re.compile(r'\bGameObject\s*\.\s*Find\b')),
    ('FindObjectOfType', re.compile(r'\bFind(?:Object|Objects|FirstObject|AnyObject)[A-Za-z]*OfType\b')),
    ('Resources.Load', re.compile(r'\bResources\s*\.\s*Load\b')),
    ('Instantiate(', re.compile(r'(?<![\w])Instantiate\s*\(')),
    ('bare Input', re.compile(r'(?<![\w.])Input\s*\.\s*[A-Z]')),
    ('new GameObject', re.compile(r'\bnew\s+GameObject\s*\(')),
    ('CreatePrimitive', re.compile(r'\bCreatePrimitive\s*\(')),
    ('DontDestroyOnLoad', re.compile(r'\bDontDestroyOnLoad\s*\(')),
]


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


def strip_comments(text):
    """Replace comment bodies with spaces; keep line count identical.

    Handles //-to-eol and /* ... */ blocks while respecting string/char literals (so a URL in a
    string is not mistaken for a comment).
    """
    out = []
    i = 0
    n = len(text)
    state = None  # None | 'line' | 'block' | 'str' | 'char' | 'verbatim'
    while i < n:
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ''
        if state is None:
            if c == '/' and nxt == '/':
                state = 'line'
                out.append('  ')
                i += 2
                continue
            if c == '/' and nxt == '*':
                state = 'block'
                out.append('  ')
                i += 2
                continue
            if c == '@' and nxt == '"':
                state = 'verbatim'
                out.append('@')
                out.append('"')
                i += 2
                continue
            if c == '"':
                state = 'str'
            out.append(c)
            i += 1
            continue
        if state == 'line':
            if c == '\n':
                state = None
                out.append('\n')
            else:
                out.append(' ')
            i += 1
            continue
        if state == 'block':
            if c == '*' and nxt == '/':
                state = None
                out.append('  ')
                i += 2
                continue
            out.append('\n' if c == '\n' else ' ')
            i += 1
            continue
        if state == 'str':
            if c == '\\':
                out.append(' ')
                out.append('\n' if nxt == '\n' else ' ')
                i += 2
                continue
            if c == '"':
                state = None
            out.append('\n' if c == '\n' else c)
            i += 1
            continue
        if state == 'verbatim':
            if c == '"' and nxt == '"':
                out.append('  ')
                i += 2
                continue
            if c == '"':
                state = None
            out.append('\n' if c == '\n' else c)
            i += 1
            continue
    return ''.join(out)


def main(argv):
    out_dir = DEFAULT_OUT
    if '-o' in argv:
        out_dir = argv[argv.index('-o') + 1]

    registry_text = read(REGISTRY) if os.path.isfile(REGISTRY) else ''

    hits = []          # (api, file, line, code)
    for path in cs_files(BUSINESS):
        raw = read(path)
        code = strip_comments(raw)
        rel = os.path.relpath(path, ROOT).replace('\\', '/')
        raw_lines = raw.split('\n')
        code_lines = code.split('\n')
        for idx, cl in enumerate(code_lines, start=1):
            if not cl.strip():
                continue
            for api, rx in PATTERNS:
                if rx.search(cl):
                    src = raw_lines[idx - 1].strip() if idx - 1 < len(raw_lines) else ''
                    hits.append((api, rel, idx, src))
                    break

    rows = []
    unregistered = 0
    by_api = {}
    for api, rel, line, src in hits:
        basename = os.path.basename(rel)
        if ('%s:%d' % (rel, line)) in registry_text or ('%s:%d' % (basename, line)) in registry_text:
            mention = 'file:line'
        elif basename in registry_text:
            mention = 'file-basename'
        else:
            mention = 'NONE'
            unregistered += 1
        by_api[api] = by_api.get(api, 0) + 1
        rows.append((api, rel, line, mention, src))

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    out_path = os.path.join(out_dir, 'bwr2-forbidden-api.tsv')
    with io.open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(u'# api\tfile\tline\tregistry-mention\tcode\n')
        for api, rel, line, mention, src in rows:
            f.write(u'%s\t%s\t%d\t%s\t%s\n' % (api, rel, line, mention, src))

    print('scanned business .cs files = %d' % len(cs_files(BUSINESS)))
    print('raw hits (comments excluded) = %d' % len(rows))
    for api in sorted(by_api):
        print('  %-18s %d' % (api, by_api[api]))
    print('hits mentioned nowhere in the registry = %d' % unregistered)
    for api, rel, line, mention, src in rows:
        if mention == 'NONE':
            print('  UNREGISTERED  %s:%d  [%s]' % (rel, line, api))
    print('written: %s' % os.path.relpath(out_path, ROOT).replace('\\', '/'))
    return 1 if unregistered else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
