# -*- coding: utf-8 -*-
"""bwg-block-align.py -- row-aware alignment of the coverage block: "did a generator run lose
anything that was in the product?"

WHY IT EXISTS / WHY IT WAS REWRITTEN (slice BW-G-R, 2026-09-23)
---------------------------------------------------------------
The first version (`.ai-tmp/test/bwg-block-align.py`, slice BW-G) compared PHYSICAL lines that
start with `|`.  A cell may itself contain a newline (the slices appended whole paragraphs into
one cell), and those continuation lines do not start with `|`, so they were never compared.
Measured: row `D10 | 机器人战术行为（守点 / 下包 / 突破 / 寻路）` carries a 3159-char second
physical line (the 片BU-R6 paragraph) that `--inject` deletes, and the line-based version reported
0 differences for that row -- i.e. the tool could not see the exact class of loss it exists for.

This version:
  * accumulates continuation lines into the owning row (`| ...` starts a row);
  * aligns by (dim, entity), NEVER by the `#` column -- a rebuild legitimately adds rows, so an
    index key produces a meaningless cascade (measured: 715 bogus differences);
  * undoes the retired `.png` -> ` .png` mangling before comparing;
  * classifies every difference by the cell it lands in: cell 1 (`#`) = COSMETIC (renumbering),
    cells 2..6 = REAL (must be reported / ported);
  * prints, for every REAL difference, the differing hunks with 60 chars of context, so a
    paragraph deletion and a sentence insertion are listed separately.

Usage:
  python tools/probes/bwg-block-align.py <older 验收表.md> <newer 验收表.md> [out.txt]
Exit code: 0 when there is no REAL difference (cosmetic renumbering and added/removed rows are
reported but do not fail), 1 otherwise.
"""
import difflib
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')

B_E = '<!-- COVERAGE-BEGIN -->'
E_E = '<!-- COVERAGE-END -->'


def rows_of(path):
    """[(key, raw row text with its continuation lines folded back in)]"""
    t = io.open(path, encoding='utf-8', errors='replace').read()
    i, j = t.find(B_E), t.find(E_E)
    out, cur, buf = {}, None, []
    if i < 0 or j < 0:
        return out
    for ln in t[i:j].split('\n'):
        ln = ln.rstrip('\r')
        if ln.startswith('|'):
            if cur is not None:
                out[cur] = '\n'.join(buf)
            cur = None
            c = ln.strip().strip('|').split('|')
            if len(c) >= 3 and re.match(r'^\d+$', c[0].strip()):
                cur = (c[1].strip(), c[2].strip().replace(' .png', '.png'))
                buf = [ln]
            else:
                buf = []
            continue
        if cur is not None:
            buf.append(ln)
    if cur is not None:
        out[cur] = '\n'.join(buf)
    return out


def demangle(s):
    # `_s()` used to rewrite every '.png' to ' .png' (retired in batch B).  The product may still
    # carry the old mangled text; undo it or every .png row shows a 1-char phantom difference
    # (measured: 352 of them).
    return re.sub(r' \.png', '.png', s)


def numcell(row):
    """the `#` cell = text between bar 1 and bar 2"""
    p = [m.start() for m in re.finditer(r'\|', row)]
    if len(p) < 2:
        return None
    return row[p[0] + 1:p[1]].strip()


def norm_num(row):
    """replace the `#` cell by a placeholder so 'did anything else change?' is a plain compare"""
    p = [m.start() for m in re.finditer(r'\|', row)]
    if len(p) < 2:
        return row
    return row[:p[0] + 1] + '#' + row[p[1]:]


def hunks(a, b, ctx=60):
    sm = difflib.SequenceMatcher(None, a, b, autojunk=False)
    out = []
    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == 'equal':
            continue
        out.append('  --%s-- A[%d:%d] B[%d:%d]' % (tag.upper(), i1, i2, j1, j2))
        out.append('     ctx  : ...%s' % a[max(0, i1 - ctx):i1].replace('\n', '\\n'))
        if i2 > i1:
            out.append('     A-old: %s' % a[i1:i2].replace('\n', '\\n'))
        if j2 > j1:
            out.append('     B-new: %s' % b[j1:j2].replace('\n', '\\n'))
        out.append('     after: %s...' % b[j2:j2 + ctx].replace('\n', '\\n'))
    return out


def main():
    A = {k: demangle(v) for k, v in rows_of(sys.argv[1]).items()}
    B = {k: demangle(v) for k, v in rows_of(sys.argv[2]).items()}
    lines = ['A (older) rows = %d   B (newer) rows = %d' % (len(A), len(B))]
    onlya = [k for k in A if k not in B]
    onlyb = [k for k in B if k not in A]
    lines.append('only in A (B DROPS) = %d' % len(onlya))
    for k in onlya:
        lines.append('   DROP %s | %s' % (k[0], k[1][:80]))
    lines.append('only in B (B ADDS)  = %d' % len(onlyb))
    for k in onlyb:
        lines.append('   ADD  %s | %s' % (k[0], k[1][:80]))

    cosmetic, real = [], []
    for k in A:
        if k not in B or A[k] == B[k]:
            continue
        a, b = A[k], B[k]
        na, nb = numcell(a), numcell(b)
        if na and nb and norm_num(a) == norm_num(b):
            cosmetic.append((k, na, nb))
            continue
        real.append(k)

    lines.append('COSMETIC rows (only the `#` cell changed) = %d' % len(cosmetic))
    for k, na, nb in cosmetic[:15]:
        lines.append('   %-4s %-4s -> %-4s  %s' % (k[0], na, nb, k[1][:60]))
    if len(cosmetic) > 15:
        lines.append('   ... and %d more' % (len(cosmetic) - 15))
    lines.append('REAL rows (a cell other than `#` changed) = %d' % len(real))
    for k in real:
        a, b = A[k], B[k]
        lines.append('')
        lines.append('=== %s | %s   A-len=%d B-len=%d' % (k[0], k[1], len(a), len(b)))
        lines += hunks(a, b)

    lines.append('')
    lines.append('RESULT: real=%d  cosmetic=%d  onlyA=%d  onlyB=%d' % (len(real), len(cosmetic), len(onlya), len(onlyb)))
    txt = '\n'.join(lines) + '\n'
    print(txt)
    if len(sys.argv) > 3:
        with io.open(sys.argv[3], 'w', encoding='utf-8', newline='\n') as f:
            f.write(txt)
    sys.exit(1 if real else 0)


main()
