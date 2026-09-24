# -*- coding: utf-8 -*-
"""check-acceptance-sums.py -- turns gate item 3 (`acceptance-table`) into a computable check.

WHY THIS EXISTS
---------------
`tools/verify.ps1` item 3 used to print

    "N acceptance rows (of M digit-keyed rows); the summary numbers inside that file
     must equal the acceptance count - human cross-check"

i.e. a HUMAN-ONLY verdict with nothing behind it.  A rule that cannot be tested red is not
a gate (clover-engine SKILL section 0.6: "a system prompt is a request, a hook is a
guarantee").  This script is that hook:

    ground truth =  the acceptance table's own BODY, counted row by row
                    (sections A..F: rows, per-section counts, status tally,
                     and the "allowed differences" row count)
    claims       =  every aggregate number the file states about itself
    verdict      =  PASS only when every claim equals the ground truth

Where the claims are read from (all inside the acceptance table):
  * the counting-criterion sentence:  "acceptance rows = **64**" / "table total (**72**)"
  * the per-section count table:      "| A ... | 10 |" ... "| A-E subtotal | **64** |" ... "| total | **72** |"
  * the status composition:           "OK 72 rows (A-E 64 + F 8), FAIL 0 rows, BLOCKED 0 rows"
  * self-check row 3:                 "this version **72 = 72 ok + 0 fail + 0 blocked**"
  * self-check row 4:                 "**46**" / "`46 rows, each with ...`"

Historical numbers (e.g. "agent-17 measured 72 = 69 + 0 + 3") are NOT claims and are
ignored: only statements marked as the CURRENT version are compared.

Exit 0 = PASS, 1 = FAIL.  Output is ASCII-only on purpose (the gate pipeline runs on a
cp936 console, where non-ASCII output can raise UnicodeEncodeError - measured in slice N).

Run:  python tools/probes/check-acceptance-sums.py
"""

import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))

# --- non-ASCII tokens from code points (keeps this file ASCII-only) --------------------
PLAN_DIR = '\u7b56\u5212'                       # plan/design dir
SPEC_NAME = '\u9a8c\u6536\u8868' + '.md'        # acceptance table (.md)
C_AE_VERDICT = '\u9a8c\u6536\u884c'             # "acceptance rows"
C_ROWS = '\u884c'                               # "rows"
C_SEC = '\u6bb5'                                # "section"
C_TOTAL = '\u8868\u4f53\u5408\u8ba1'            # "table body total"
C_SUBTOTAL = 'A\u2013E \u5c0f\u8ba1'            # "A-E subtotal" (en dash U+2013)
C_SUMMARY_SEC = '\u8868\u4f53\u8ba1\u6570'      # "table body count" summary section
C_THIS_VER = '\u672c\u7248'                     # "this version"
C_NOTE = '\u6761'                               # measure word for difference rows
C_DIFF_SEC = '\u5141\u8bb8\u7684\u5dee\u5f02'   # "allowed differences" heading
OK_MARK = '\u2705'
BAD_MARK = '\u274c'
BLOCK_MARK = '\u26d4'
TESTED_MARK = '\U0001f9ea'
TODO_MARK = '\u2b1c'
WIP_MARK = '\U0001f504'

STATUS_MARKS = (OK_MARK, BAD_MARK, BLOCK_MARK, TESTED_MARK, TODO_MARK, WIP_MARK)
PENDING = (TESTED_MARK, TODO_MARK, WIP_MARK)

SEC_RE = re.compile(r'^##\s+([A-F])\.')
ROW_RE = re.compile(r'^\|\s*([A-Z]?\d+)\s*\|')
TITLE_RE = re.compile(r'^#{1,6}\s')
NUM_COL = re.compile(r'\*{0,2}(\d+)\*{0,2}$')


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def sections_of(lines):
    """[(letter, [digit-keyed row lines])] for the A..F sections, in file order."""
    out = []
    cur = None
    for ln in lines:
        if ln.startswith('## '):
            m = SEC_RE.match(ln)
            cur = m.group(1) if m else None
            if cur is not None:
                out.append((cur, []))
            continue
        if cur is not None and ROW_RE.match(ln):
            out[-1][1].append(ln)
    return out


def region_of(lines, marker):
    """[(lineno, line)] for the section whose heading contains `marker`."""
    out = []
    inside = False
    for i, ln in enumerate(lines, 1):
        if TITLE_RE.match(ln):
            inside = (marker in ln)
            continue
        if inside:
            out.append((i, ln))
    return out


def tally(row_lines):
    """Status cell = column 6 of `| # | item | kind | criterion | impl | status | evidence |`."""
    stat = {m: 0 for m in STATUS_MARKS}
    broken = []
    for ln in row_lines:
        cells = [c.strip() for c in ln.split('|')]
        if len(cells) < 7:
            broken.append(ln[:70])
            continue
        hits = [m for m in STATUS_MARKS if m in cells[6]]
        if len(hits) != 1:
            broken.append(ln[:70])
            continue
        stat[hits[0]] += 1
    return stat, broken


def count_difference_rows(lines):
    n = 0
    for _, ln in region_of(lines, C_DIFF_SEC):
        if not ln.lstrip().startswith('|'):
            continue
        if re.match(r'^\|[\s\-:|]+\|$', ln.strip()):
            continue
        cells = [c.strip() for c in ln.split('|')]
        if len(cells) > 2 and cells[1] == '#':
            continue
        n += 1
    return n


def num(s):
    return int(re.sub(r'[^0-9]', '', s))


def main():
    # verify.ps1 called this script with no arguments, so it ALWAYS read the REAL plan dir -- measured:
    # with a -PlanDir sandbox whose acceptance table had been replaced by ONE broken line, this item
    # still printed "PASS -- all 21 aggregate claims equal the table body" (had it read the sandbox it
    # would have said "no A..F acceptance sections found").  Any sample built on that seam would have
    # been a FALSE TEST PASS: the test believed it measured A while it measured B.
    spec_dir = os.path.join(ROOT, PLAN_DIR)
    if '--plan-dir' in sys.argv:
        spec_dir = os.path.abspath(sys.argv[sys.argv.index('--plan-dir') + 1])
    spec = os.path.join(spec_dir, SPEC_NAME)
    if not os.path.exists(spec):
        print('FAIL: missing ' + spec)
        return 1

    lines = read(spec).split('\n')
    body = sections_of(lines)
    if not body:
        print('FAIL: no A..F acceptance sections found in ' + spec)
        return 1

    per_sec = {k: len(v) for k, v in body}
    ae_rows = sum(n for k, n in per_sec.items() if k in 'ABCDE')
    total_rows = sum(per_sec.values())
    stat = {m: 0 for m in STATUS_MARKS}
    sec_ok = {}
    broken = []
    for key, rows in body:
        s, b = tally(rows)
        for m in STATUS_MARKS:
            stat[m] += s[m]
        sec_ok[key] = s[OK_MARK]
        broken += b
    ae_ok = sum(v for k, v in sec_ok.items() if k in 'ABCDE')
    f_ok = sec_ok.get('F', -1)
    diff_rows = count_difference_rows(lines)

    print('acceptance body: rows ' + ' '.join(k + '=' + str(per_sec[k]) for k, _ in body) +
          ' (A-E=' + str(ae_rows) + ', total=' + str(total_rows) + ')' +
          ' | ok=' + str(stat[OK_MARK]) + ' fail=' + str(stat[BAD_MARK]) +
          ' blocked=' + str(stat[BLOCK_MARK]) +
          ' other=' + str(sum(stat[m] for m in PENDING)) +
          ' | allowed-differences=' + str(diff_rows))

    claims = []

    def add(label, claimed, actual, line_no=None):
        claims.append((line_no, label, claimed, actual))

    def cols_with_number(line):
        return [num(c) for c in (x.strip() for x in line.split('|')) if NUM_COL.match(c)]

    # --- 1) the counting-criterion sentence ------------------------------------------
    for i, ln in enumerate(lines, 1):
        m = re.search(C_AE_VERDICT + '\u300d\\s*=\\s*\\*{0,2}\\s*(\\d+)\\s*' + C_ROWS, ln)
        if m:
            add('body.ae-verdict-rows', num(m.group(1)), ae_rows, i)
            break
    for i, ln in enumerate(lines, 1):
        m = re.search('\u5408\u8ba1\uff08\\*{0,2}(\\d+)\\s*' + C_ROWS, ln)
        if m:
            add('body.total-rows', num(m.group(1)), total_rows, i)
            break

    # --- 2) the per-section count table (inside the summary section) ------------------
    for i, ln in region_of(lines, C_SUMMARY_SEC):
        s = ln.strip()
        if not s.startswith('|'):
            continue
        m = re.match(r'^\|\s*\*{0,2}([A-F])\s', s)
        if m:
            cand = cols_with_number(ln)
            if cand:
                add('body.section-' + m.group(1), cand[0], per_sec.get(m.group(1), -1), i)
        if C_SUBTOTAL in ln:
            cand = cols_with_number(ln)
            if cand:
                add('body.ae-subtotal', max(cand), ae_rows, i)
        if C_TOTAL in ln:
            cand = cols_with_number(ln)
            if cand:
                add('body.total-rows-table', max(cand), total_rows, i)

    # --- 3) status composition --------------------------------------------------------
    pat3 = (OK_MARK + r'\s*\*{0,2}(\d+)\s*' + C_ROWS + r'\uff08A\u2013E\s*' + C_SEC + r'\s*(\d+)\s*\+'
            r'\s*F\s*' + C_SEC + r'\s*(\d+)\uff09\u3001' + BAD_MARK + r'\s*\*{0,2}(\d+)\s*' + C_ROWS +
            r'\u3001' + BLOCK_MARK + r'\s*\w*\s*\*{0,2}(\d+)\s*' + C_ROWS)
    for i, ln in enumerate(lines, 1):
        m = re.search(pat3, ln)
        if m:
            add('status.ok', num(m.group(1)), stat[OK_MARK], i)
            add('status.ok-ae', num(m.group(2)), ae_ok, i)
            add('status.ok-f', num(m.group(3)), f_ok, i)
            add('status.fail', num(m.group(4)), stat[BAD_MARK], i)
            add('status.blocked', num(m.group(5)), stat[BLOCK_MARK], i)
            break

    # --- 4) self-check row 3: "this version N = a ok + b fail + c blocked" ------------
    pat4 = (C_THIS_VER + r'\s*\*{0,2}\s*(\d+)\s*=\s*(\d+)\s*' + OK_MARK + r'\s*\+\s*(\d+)\s*' + BAD_MARK +
            r'\s*\+\s*(\d+)\s*' + BLOCK_MARK)
    for i, ln in enumerate(lines, 1):
        m = re.search(pat4, ln)
        if m:
            add('selfcheck3.total', num(m.group(1)), total_rows, i)
            add('selfcheck3.ok', num(m.group(2)), stat[OK_MARK], i)
            add('selfcheck3.fail', num(m.group(3)), stat[BAD_MARK], i)
            add('selfcheck3.blocked', num(m.group(4)), stat[BLOCK_MARK], i)
            break

    # --- 5) self-check row 4: allowed-difference count --------------------------------
    for i, ln in enumerate(lines, 1):
        if 'rows, each with' not in ln:
            continue
        m = re.search(r'\*\*(\d+)\s*' + C_NOTE + r'\*\*', ln)
        if m:
            add('selfcheck4.diffs', num(m.group(1)), diff_rows, i)
        m2 = re.search(r'(\d+)\s*rows, each with', ln)
        if m2:
            add('selfcheck4.diffs-quoted', num(m2.group(1)), diff_rows, i)
        if m or m2:
            break

    if not claims:
        print('FAIL: no aggregate claim could be parsed (did the summary section move?)')
        return 1

    bad = []
    for line_no, label, claimed, actual in claims:
        where = ('L' + str(line_no)) if line_no else '-'
        if claimed == actual:
            print('  OK   ' + where.ljust(6) + ' ' + label.ljust(26) + ' = ' + str(actual))
        else:
            line = ('  BAD  ' + where.ljust(6) + ' ' + label.ljust(26) + ' claims ' + str(claimed) +
                    ' actual ' + str(actual))
            print(line)
            bad.append(line)

    if broken:
        print('  WARN ' + str(len(broken)) + ' acceptance row(s) carry no single status mark (not scored here)')

    if bad:
        print('RESULT: FAIL -- ' + str(len(bad)) + ' of ' + str(len(claims)) +
              ' aggregate claim(s) disagree with the table body')
        return 1
    print('RESULT: PASS -- all ' + str(len(claims)) + ' aggregate claims equal the table body')
    return 0


if __name__ == '__main__':
    sys.exit(main())
