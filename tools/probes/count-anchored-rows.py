# -*- coding: utf-8 -*-
"""count-anchored-rows.py -- how far is this project from gate item `evidence-anchor`?

WHY THIS EXISTS
---------------
Template item `evidence-anchor` (reference/verify-template.md:110) requires *every*
verdict row to anchor to a position inside a **machine-produced artifact** -- not to
md prose, not to a report, not to a code comment (reference/anti-gaming.md section 3,
"evidence anchors").  The project has NOT wired that item into tools/verify.ps1 yet;
this script only measures the cost of doing so.  It is a 判据资产 (tools/probes/),
NOT a gate.

WHAT IT SCANS
-------------
The acceptance table (plan/acceptance .md), two row families:
  * verdict rows      = the digit-keyed rows of sections A..F (the acceptance body)
  * allowed-diff rows = the rows of the "allowed differences" table
(these are 72 and 64 on the current revision -> 136 rows total.)

ANCHOR SYNTAX (accepted forms)
------------------------------
A row is ANCHORED iff it carries a backticked token of the form
    `anchor:<path>:<line>`      (preferred; explicit)
  or
    `<path>:<line>`             (the bare form used in verify-template.md:52)
where <path> has an extension, resolves to an existing file under the project
root, the line number is within that file, and <path> is NOT a narrative file
(.md) -- anti-gaming.md section 3 item 3: an anchor onto md/report/comment is no
anchor at all.

Output is ASCII-only on purpose (the gate pipeline runs on a cp936 console, where
non-ASCII output can raise UnicodeEncodeError -- see check-acceptance-sums.py).

Run:  python tools/probes/count-anchored-rows.py
Exit 0 always (this is a measurement, not a gate verdict).
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

PLAN_DIR = '\u7b56\u5212'                        # plan/design dir
SPEC_NAME = '\u9a8c\u6536\u8868' + '.md'         # acceptance table (.md)
C_DIFF_SEC = '\u5141\u8bb8\u7684\u5dee\u5f02'    # "allowed differences"

SEC_RE = re.compile(r'^##\s+([A-F])\.')
ROW_RE = re.compile(r'^\|\s*([A-Z]?\d+)\s*\|')
TITLE_RE = re.compile(r'^#{1,6}\s')
TOKEN_RE = re.compile(r'`([^`]+)`')
ANCHOR_RE = re.compile(r'^(?:anchor:)?(.+):(\d+)$')


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def verdict_rows(lines):
    """[(row_id, line)] for the digit-keyed rows of sections A..F, in file order."""
    out = []
    cur = None
    for ln in lines:
        if ln.startswith('## '):
            m = SEC_RE.match(ln)
            cur = m.group(1) if m else None
            continue
        if cur is not None:
            m = ROW_RE.match(ln)
            if m:
                out.append((m.group(1), ln))
    return out


def diff_rows(lines):
    """[(row_id, line)] for the 'allowed differences' table rows."""
    out = []
    inside = False
    for ln in lines:
        if TITLE_RE.match(ln):
            inside = (C_DIFF_SEC in ln)
            continue
        if not inside:
            continue
        s = ln.strip()
        if not s.startswith('|'):
            continue
        if re.match(r'^\|[\s\-:|]+\|$', s):
            continue
        cells = [c.strip() for c in ln.split('|')]
        if len(cells) > 2 and cells[1] == '#':
            continue
        rid = cells[1] if len(cells) > 1 else '?'
        out.append((rid, ln))
    return out


def classify(line):
    """-> ('ok'|'missing'|'narrative'|'badline', token) for the first anchor token found."""
    for tok in TOKEN_RE.findall(line):
        m = ANCHOR_RE.match(tok.strip())
        if not m:
            continue
        path, lineno = m.group(1).strip(), int(m.group(2))
        if '.' not in os.path.basename(path):
            continue                                   # not a file path (e.g. "E:3")
        norm = path.replace('\\', '/').lstrip('./')
        if norm.lower().endswith('.md'):
            return ('narrative', tok)
        full = os.path.join(ROOT, norm.replace('/', os.sep))
        if not os.path.isfile(full):
            return ('missing', tok)
        with io.open(full, 'rb') as f:
            n = sum(1 for _ in f)
        if lineno < 1 or lineno > n:
            return ('badline', tok)
        return ('ok', tok)
    return (None, None)


def main():
    spec = os.path.join(ROOT, PLAN_DIR, SPEC_NAME)
    if not os.path.exists(spec):
        print('FAIL: missing ' + spec)
        return 1

    lines = read(spec).split('\n')
    vr = verdict_rows(lines)
    dr = diff_rows(lines)

    stats = {'ok': 0, 'missing': 0, 'narrative': 0, 'badline': 0, 'none': 0}
    un_v, un_d = [], []
    for rid, ln in vr:
        kind, _tok = classify(ln)
        if kind == 'ok':
            stats['ok'] += 1
        else:
            stats[kind if kind else 'none'] += 1
            un_v.append(rid)
    dok = 0
    for rid, ln in dr:
        kind, _tok = classify(ln)
        if kind == 'ok':
            dok += 1
        else:
            un_d.append(rid)

    total = len(vr) + len(dr)
    anchored = stats['ok'] + dok

    print('evidence-anchor distance scan (template item evidence-anchor, still planned)')
    print('  acceptance verdict rows (A-F) : ' + str(len(vr)))
    print('  allowed-difference rows       : ' + str(len(dr)))
    print('  total rows scanned            : ' + str(total))
    print('  rows WITH a parseable anchor  : ' + str(anchored))
    print('  rows WITHOUT an anchor        : ' + str(total - anchored))
    print('    breakdown (verdict rows)    : file-missing=' + str(stats['missing']) +
          ' md-narrative=' + str(stats['narrative']) +
          ' line-out-of-range=' + str(stats['badline']) +
          ' no-token=' + str(stats['none']))
    print('  accepted forms: `anchor:<path>:<line>` or `<path>:<line>`'
          ' (file exists, line in range, target is not .md)')
    print('  unanchored verdict rows       : ' + (' '.join(un_v) if un_v else '(none)'))
    print('  unanchored allowed-diff rows  : ' + (' '.join(un_d) if un_d else '(none)'))
    print('  NOTE: measurement only -- this project has NOT wired evidence-anchor'
          ' into tools/verify.ps1')
    return 0


if __name__ == '__main__':
    sys.exit(main())
