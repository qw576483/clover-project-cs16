# -*- coding: utf-8 -*-
"""bwe-block-cols.py -- 判定块「逐列不变量」判据（片BW-E）。

WHY
---
`verify.ps1` item 38 要求覆盖矩阵判定块 `<!-- COVERAGE-BEGIN -->`…`<!-- COVERAGE-END -->`
之间**逐字节等于**生成器重建的结果；而片BW-E 只允许改**证据列**（让每个自述式证据变成
可从盘上重开核对的可复核锚点）。

⇒ 一次改动必须能机械回答：**"其它列有没有被顺手改到？"**
   按**行 id 对齐**逐格比（⛔ 不按行号 —— 重建块一旦多/少行，按行号比会产出大量假差异，
   本项目 2026-09-23 实测过）。

用法
----
    python tools/probes/bwe-block-cols.py <before.md> <after.md> [--json <out.json>]

判据
----
  * 两侧行 id 集合必须一致（不一致 ⇒ 直接 FAIL：那是"行被删/加"，比列变更严重）；
  * 逐列统计"改了几格"，并**指名**改动的列；样例打印前若干 id；
  * 退出码：0 = 只有**最后一列（证据列）**变；1 = 其它列也变了（= 本片越界，必须停/回滚）。
ASCII-only stdout（cp936 控制台安全）。
"""
import io
import json
import os
import re
import sys

sys.dont_write_bytecode = True

ROW_RE = re.compile(r'^\|\s*(\d+)\s*\|')


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def rows_of(path):
    """-> (header_cells, {id: [cells...]}, order)。只取数据行 `| <digits> | … |`。"""
    out = {}
    order = []
    header = None
    for ln in read(path).split('\n'):
        if not ln.startswith('|'):
            continue
        cells = [c.strip() for c in ln.strip().strip('|').split('|')]
        if not cells:
            continue
        if header is None and not cells[0].isdigit():
            header = cells
            continue
        if not cells[0].isdigit():
            continue
        out[cells[0]] = cells
        order.append(cells[0])
    return (header, out, order)


def main():
    if len(sys.argv) < 3:
        print('usage: bwe-block-cols.py <before.md> <after.md> [--json out.json]')
        return 2
    a, b = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
    ha, ra, oa = rows_of(a)
    hb, rb, ob = rows_of(b)
    print('before = %s  (%d data rows)' % (a, len(ra)))
    print('after  = %s  (%d data rows)' % (b, len(rb)))
    print('header before = %s' % (' | '.join(ha) if ha else '(none)'))
    print('header after  = %s' % (' | '.join(hb) if hb else '(none)'))
    only_a = sorted(set(ra) - set(rb), key=int)
    only_b = sorted(set(rb) - set(ra), key=int)
    print('rows only in before = %d %s' % (len(only_a), ','.join(only_a[:12])))
    print('rows only in after  = %d %s' % (len(only_b), ','.join(only_b[:12])))
    if only_a or only_b:
        print('FAIL: the row id sets differ -- that is a row add/remove, not a column change')

    ncol = max([len(v) for v in list(ra.values()) + list(rb.values())] or [0])
    names = []
    for k in range(ncol):
        nm = (hb[k] if (hb and k < len(hb)) else 'col%d' % k)
        names.append(nm or 'col%d' % k)
    changed = [0] * ncol
    ids_changed = [[] for _ in range(ncol)]
    for rid in ob:
        if rid not in rb:
            continue
        ca, cb = ra[rid], rb[rid]
        for k in range(ncol):
            va = ca[k] if k < len(ca) else ''
            vb = cb[k] if k < len(cb) else ''
            if va != vb:
                changed[k] += 1
                if len(ids_changed[k]) < 8:
                    ids_changed[k].append(rid)
    print('--- per-column changed cells (aligned by row id, %d cols) ---' % ncol)
    for k in range(ncol):
        print('  col%-2d %-10s changed = %-6d %s'
              % (k, names[k], changed[k], ','.join(ids_changed[k])))
    tail = ncol - 1
    others = sum(changed[k] for k in range(ncol) if k != tail)
    out = {'before': a, 'after': b, 'rows': len(ra), 'cols': names,
           'changed': changed, 'changed_ids': ids_changed,
           'only_evidence_changed': (others == 0), 'other_cols_changed': others,
           'rows_only_before': only_a, 'rows_only_after': only_b}
    if '--json' in sys.argv:
        with io.open(sys.argv[sys.argv.index('--json') + 1], 'w', encoding='utf-8',
                     newline='\n') as f:
            f.write(json.dumps(out, ensure_ascii=False, indent=1))
    if only_a or only_b or others:
        print('RESULT: FAIL -- %d cell(s) outside the evidence column changed '
              '(rows added/removed: %d)' % (others, len(only_a) + len(only_b)))
        return 1
    print('RESULT: PASS -- only the evidence column changed (%d cell(s)); '
          'every other column is cell-identical' % changed[tail])
    return 0


if __name__ == '__main__':
    sys.exit(main())
