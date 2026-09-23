# -*- coding: utf-8 -*-
"""bwr2-settings-effect.py -- S3 causal pair: does a settings control actually take effect?

The failure class is "the setting exists, the row says 一致, but flipping it changes nothing".
Three mechanical predicates, all offline:

  1. LOG-ONLY CONTROLS: every `new ResCtrl(...)` in UI/Flow/OptionsPanel.cs carrying
     `LogOnly = true` -- the file itself documents LogOnly as "原版有、本工程没有对应实现 ⇒ 只显示并
     留一条日志".  Each such control is a control the user can flip with no effect.
  2. REGISTRATION: is any of that mentioned in the differences registry (plan/差异登记.tsv)?
     SKILL section 3 item 4: a code comment is not a registration.
  3. PERSISTED-BUT-UNCONSUMED: every `cs.player.*` key written by CsPlayerSettingsStore is
     looked up again outside the store/panel -- a key that nobody reads is a setting that cannot
     take effect no matter how many times you re-enter Play.

Run:
  python tools/probes/bwr2-settings-effect.py [-o <dir>]
Writes (default dir = <root>/.ai-tmp/test/):
  bwr2-settings-effect.tsv
Exit code: 1 when at least one log-only control is unregistered, or a persisted key has no reader.
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
OPTIONS = os.path.join(BUSINESS, 'UI', 'Flow', 'OptionsPanel.cs')
STORE = os.path.join(BUSINESS, 'UI', 'Flow', 'CsPlayerSettingsStore.cs')
REGISTRY = os.path.join(ROOT, '策划', '差异登记.tsv')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test')

CTRL_RE = re.compile(r'new\s+ResCtrl\s*\(\s*"([^"]*)"\s*,\s*Kind\.(\w+)')
ARRAY_RE = re.compile(r'private static readonly ResCtrl\[\]\s+(\w+)\s*=')
WIRE_RE = re.compile(r'_(real\w+)\[\s*"([^"]+)"\s*\]')
KEY_RE = re.compile(r'KeyPrefix\s*\+\s*"(\w+)"')


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


def parse_controls(text):
    """-> list of dict(page, name, kind, log_only)."""
    lines = text.split('\n')
    page = '?'
    out = []
    cur = None
    for i, ln in enumerate(lines):
        m = ARRAY_RE.search(ln)
        if m:
            page = m.group(1)
        m = CTRL_RE.search(ln)
        if m:
            if cur:
                out.append(cur)
            # The initializer may wrap: keep the next lines only while they do not open a NEW
            # control (otherwise the neighbour's `LogOnly` leaks into this row).
            window = [ln]
            for j in range(i + 1, min(len(lines), i + 3)):
                if CTRL_RE.search(lines[j]) or ARRAY_RE.search(lines[j]):
                    break
                window.append(lines[j])
            cur = {'page': page, 'name': m.group(1), 'kind': m.group(2),
                   'log_only': 'LogOnly = true' in '\n'.join(window)}
        elif cur and 'LogOnly = true' in ln:
            cur['log_only'] = True
    if cur:
        out.append(cur)
    return out


def main(argv):
    out_dir = DEFAULT_OUT
    if '-o' in argv:
        out_dir = argv[argv.index('-o') + 1]

    options_text = read(OPTIONS)
    store_text = read(STORE)
    registry_text = read(REGISTRY) if os.path.isfile(REGISTRY) else ''

    controls = parse_controls(options_text)
    log_only = [c for c in controls if c['log_only']]

    wired = set()
    for kind, name in WIRE_RE.findall(options_text):
        wired.add(name)

    keys = sorted(set(KEY_RE.findall(store_text)))
    all_files = cs_files(BUSINESS)
    others = [(p, read(p)) for p in all_files
              if p not in (STORE, OPTIONS)]

    key_rows = []
    for k in keys:
        raw_key = 'cs.player.' + k
        raw_hits = []
        field_hits = []
        for p, t in others:
            rel = os.path.relpath(p, ROOT).replace('\\', '/')
            if raw_key in t:
                raw_hits.append(rel)
            if re.search(r'\.%s\b' % re.escape(k), t):
                field_hits.append(rel)
        key_rows.append((k, raw_hits, field_hits))

    reg_lines = registry_text.split('\n')

    def is_registered(name):
        """A log-only control counts as registered only when the registry has a row that
        mentions BOTH the control and OptionsPanel (a bare word like 'Resolution' elsewhere in
        the registry is not a registration), or explicitly names the LogOnly mechanism."""
        if 'LogOnly' in registry_text:
            return True
        return any(('OptionsPanel' in ln) and (name in ln) for ln in reg_lines)

    rows = []
    unregistered = 0
    for c in controls:
        registered = is_registered(c['name'])
        if c['log_only'] and not registered:
            unregistered += 1
        rows.append(('control', c['page'], c['name'], c['kind'],
                     'log-only' if c['log_only'] else
                     ('wired' if c['name'] in wired else 'unwired'),
                     'registered' if registered else 'NONE'))

    unconsumed = 0
    for k, raw_hits, field_hits in key_rows:
        consumed = bool(raw_hits) or bool(field_hits)
        if not consumed:
            unconsumed += 1
        rows.append(('key', 'cs.player.' + k, '', '',
                     'consumers=%d' % (len(raw_hits) + len(field_hits)),
                     ('raw=' + ','.join(raw_hits[:2]) + ';field=' + ','.join(field_hits[:2]))
                     if consumed else 'NONE'))

    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    out_path = os.path.join(out_dir, 'bwr2-settings-effect.tsv')
    with io.open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(u'# kind\tpage-or-key\tname\tcontrol-kind\teffect\tregistration\n')
        for r in rows:
            f.write(u'\t'.join(r) + u'\n')

    print('Options controls = %d   of which LogOnly = %d' % (len(controls), len(log_only)))
    pages = {}
    for c in log_only:
        pages[c['page']] = pages.get(c['page'], 0) + 1
    for p in sorted(pages):
        print('  log-only on %-20s %d' % (p, pages[p]))
    print('log-only controls NOT registered in 差异登记.tsv = %d' % unregistered)
    print('persisted keys = %d   of which with NO reader outside store/panel = %d'
          % (len(keys), unconsumed))
    for k, raw_hits, field_hits in key_rows:
        if not raw_hits and not field_hits:
            print('  UNCONSUMED  cs.player.%s' % k)
    print('written: %s' % os.path.relpath(out_path, ROOT).replace('\\', '/'))
    return 1 if (unregistered or unconsumed) else 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
