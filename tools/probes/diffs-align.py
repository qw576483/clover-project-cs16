# -*- coding: utf-8 -*-
"""diffs-align.py -- 「允许的差异」段的逐格对齐判据（`bwg-block-align.py` 的同族口径）。

为什么要有它（2026-09-23 片BW-S）
---------------------------------
「允许的差异」段被**三处**各自维护：脚本里的 `DIF`（-> `策划/差异登记.tsv`）、
`策划/验收表.md` 里手写的同名片、以及该段的聚合数。闸门只比较**编号集合**
（`verify.ps1` item 24 `differences-source-of-truth`）⇒ 编号一样、**内容漂移 41 行**也全绿。

本工具把"同源渲染结果 vs 盘上段"逐格比出来（口径同 `bwg-block-align.py`）：
  * 按 **id** 配对（⛔ 不按行号、不按文本 —— 文本一改就没法配对了）；
  * 每行是一张管道表；`#` 列 = 身份列，其余 4 列 = 内容列；
  * 三档判定（每档都给数字，不许只报一个）：
      `cosmetic`  = 仅**格式归一**（去掉 `**` / 反引号 / 空白）后逐字相同；
      `real_raw`  = 原样逐字不同；
      `real_norm` = **连格式归一后仍不同** ⇒ 这才是"内容不一致"；
  * 每个 real_norm 行都打印两侧的差异片段（60 字符上下文），并把原始输出落盘。

用法：
    python tools/probes/diffs-align.py <盘上 验收表.md> <渲染 差异登记.fragment.md> [out.txt]
退出码：0 = real_norm == 0；1 = 有内容不一致（或有一侧独有的 id）。
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')

PLAN = '\u7b56\u5212'                      # 策划
DIFF_SEC = '\u5141\u8bb8\u7684\u5dee\u5f02'   # 允许的差异


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


def section_lines(text, marker):
    """`## <marker>…` 段的行（到下一个 ## / ### 标题为止）。"""
    lines = text.split('\n')
    s0 = next((k for k, l in enumerate(lines) if l.startswith('## ') and marker in l), -1)
    if s0 < 0:
        return []
    for k in range(s0 + 1, len(lines)):
        if lines[k].startswith('## ') or lines[k].startswith('### '):
            return lines[s0:k]
    return lines[s0:]


def rows_of(lines):
    """id -> 行文本（只取 `| <digits> | …` 行）。"""
    out = {}
    for ln in lines:
        m = re.match(r'^\|\s*(\d+)\s*\|', ln.rstrip('\r'))
        if m:
            out[m.group(1)] = ln.rstrip('\r')
    return out


def norm(s):
    return re.sub(r'\s+', '', s.replace('**', '').replace('`', ''))


def hunks(a, b, ctx=60):
    import difflib
    out = []
    for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(None, a, b, autojunk=False).get_opcodes():
        if tag == 'equal':
            continue
        out.append('    --%s-- A[%d:%d] B[%d:%d]' % (tag.upper(), i1, i2, j1, j2))
        out.append('       ctx  : ...%s' % a[max(0, i1 - ctx):i1])
        if i2 > i1:
            out.append('       on-disk: %s' % a[i1:i2])
        if j2 > j1:
            out.append('       render : %s' % b[j1:j2])
        out.append('       after  : %s...' % b[j2:j2 + ctx])
    return out


def main():
    # 两侧都可以是"盘上 验收表.md"或"渲染片段"：有 `## 允许的差异` 段就按段取，没有就整份当表。
    ta = read(sys.argv[1])
    la = section_lines(ta, DIFF_SEC) or ta.split('\n')
    A = rows_of(la)
    B = rows_of(read(sys.argv[2]).split('\n'))
    lines = ['A (on disk 验收表.md) rows = %d' % len(A),
             'B (DIF render fragment) rows = %d' % len(B)]
    only_a = sorted((k for k in A if k not in B), key=int)
    only_b = sorted((k for k in B if k not in A), key=int)
    lines.append('only in A (render DROPS) = %d %s' % (len(only_a), only_a))
    lines.append('only in B (render ADDS)  = %d %s' % (len(only_b), only_b))

    same_raw, cosmetic, real = [], [], []
    for k in sorted(A, key=int):
        if k not in B:
            continue
        if A[k] == B[k]:
            same_raw.append(k)
        elif norm(A[k]) == norm(B[k]):
            cosmetic.append(k)
        else:
            real.append(k)
    lines.append('identical (byte-for-byte)        = %d' % len(same_raw))
    lines.append('cosmetic (equal only after 归一) = %d %s' % (len(cosmetic), cosmetic))
    lines.append('REAL (differs after 归一)        = %d %s' % (len(real), real))
    for k in real:
        lines.append('')
        lines.append('=== id %s   A-len=%d B-len=%d' % (k, len(A[k]), len(B[k])))
        lines += hunks(norm(A[k]), norm(B[k]))

    lines.append('')
    lines.append('RESULT: real_raw=%d  cosmetic=%d  real_norm=%d  identical=%d  onlyA=%d  onlyB=%d'
                 % (len([k for k in A if k in B and A[k] != B[k]]), len(cosmetic), len(real),
                    len(same_raw), len(only_a), len(only_b)))
    txt = '\n'.join(lines) + '\n'
    print(txt)
    if len(sys.argv) > 3:
        with io.open(sys.argv[3], 'w', encoding='utf-8', newline='\n') as f:
            f.write(txt)
    return 1 if (real or only_a or only_b) else 0


if __name__ == '__main__':
    sys.exit(main())
