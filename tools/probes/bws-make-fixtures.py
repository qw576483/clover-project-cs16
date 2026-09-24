# -*- coding: utf-8 -*-
"""bws-make-fixtures.py -- 为 section-ownership 的双向自检造**副本夹具**（片BW-S）。

⛔ 铁律（skill `reference/anti-gaming.md` §五 第 3 条）：自检**只许在自己的副本上做手脚**。
   本脚本只读真 `策划/验收表.md` 与 `策划/差异登记.fragment.md`，把夹具写到
   `<项目根>/.ai-tmp/test/bws-st/`，**不碰任何共享产物**（不 append / 不覆盖 / 不 Move / 不改 mtime）。

产出（全部在 `.ai-tmp/test/bws-st/`）：
  prod-drift.md          真盘面的副本（差异段仍是手写版 = 有漂移）
  prod-synced.md         差异段 81 行已替换成 `DIF` 渲染结果（护栏的"应注入"样本）
  prod-handedit.md       COVERAGE 块内插一行手写行（块纯度判据的"应测红"样本）
  prod-extrahead.md      文末多一个未登记进 SECTION_OWNERSHIP 的段
  prod-warn-edited.md    表头段属权警告被手改
  prod-warn-dropped.md   表头段属权警告被删空
  frag-ok.md             真重建输出的副本
  frag-render.md         真重建输出的副本（diffs-align 的"已知正确样本"）
  frag-defect.md         第 67 行多一句（diffs-align 的"注入缺陷"样本）
  bws-st-stale.py        section-ownership 的"--stale-rule"夹具驱动（多一条匹配不到段的规则）

用法：python tools/probes/bws-make-fixtures.py
"""
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')
ST = os.path.join(ROOT, '.ai-tmp', 'test', 'bws-st')
WARN_PREFIX = '> \u26a0\ufe0f \u6bb5\u5c5e\u6743\uff08\u673a\u5217'
DIFF_SEC = '\u5141\u8bb8\u7684\u5dee\u5f02'


def rm(path, mut=None):
    L = io.open(path, encoding='utf-8').read().split('\n')
    if mut:
        mut(L)
    return L


def wr(name, L):
    io.open(os.path.join(ST, name), 'w', encoding='utf-8', newline='\n').write('\n'.join(L))


def sec_range(L):
    s0 = next(k for k, l in enumerate(L) if l.startswith('## ') and DIFF_SEC in l)
    s1 = next(k for k in range(s0 + 1, len(L))
              if L[k].startswith('## ') or L[k].startswith('### '))
    return s0, s1


def main():
    os.makedirs(ST, exist_ok=True)
    prod = os.path.join(PLAN, '\u9a8c\u6536\u8868.md')
    # 两个片段不是同一份：覆盖矩阵片段给 section-ownership --check，差异片段给 diffs-align。
    covfrag = os.path.join(PLAN, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md')
    difffrag = os.path.join(PLAN, '\u5dee\u5f02\u767b\u8bb0.fragment.md')
    src = rm(prod)
    rend = {}
    for ln in io.open(difffrag, encoding='utf-8').read().split('\n'):
        m = re.match(r'^\|\s*(\d+)\s*\|', ln)
        if m:
            rend[m.group(1)] = ln.rstrip('\r')

    wr('prod-drift.md', list(src))
    wr('cov-ok.md', rm(covfrag))
    wr('diffs-render.md', rm(difffrag))

    a = list(src)
    s0, s1 = sec_range(a)
    n = 0
    for k in range(s0, s1):
        m = re.match(r'^\|\s*(\d+)\s*\|', a[k].rstrip('\r'))
        if m and m.group(1) in rend:
            a[k] = rend[m.group(1)]
            n += 1
    for k in range(s0, s1):
        c = a[k].strip().strip('|').split('|')
        if len(c) > 1 and c[1].strip() == '#':
            a[k] = ('| # | \u5dee\u5f02 | \u4e3a\u4ec0\u4e48\u5fc5\u987b\u8fd9\u6837\uff08\u5b9e\u6d4b\u8bc1\u636e\uff09 '
                    '| \u51fa\u5904 | \u4f55\u65f6\u80fd\u6d88\u9664 |')
            break
    for k in range(s0, s1):
        if re.match(r'^\|[\s\-:|]+\|$', a[k].strip()):
            a[k] = '|---|---|---|---|---|'
            break
    wr('prod-synced.md', a)

    def handed(L):
        b = next(k for k, l in enumerate(L) if l.strip() == '<!-- COVERAGE-BEGIN -->')
        L[b + 5] = ('| 9999 | D9 | \u624b\u5199\u7ed3\u8bba | \u811a\u672c\u65ad\u8a00 | \u4e00\u81f4 '
                    '| \u624b\u6539\u5757\u5185\u7ed3\u8bba\uff08\u81ea\u68c0\u7528\uff09 |')

    wr('prod-handedit.md', rm(prod, handed))

    def extra(L):
        L += ['', '## ZZ. \u65b0\u6bb5\uff08\u6ca1\u767b\u8bb0\u8fdb SECTION_OWNERSHIP\uff09', '']

    wr('prod-extrahead.md', rm(prod, extra))

    def warn_edit(L):
        k = next(i for i, l in enumerate(L) if l.startswith(WARN_PREFIX))
        L[k] = L[k].replace('\u5176\u4f59\u5404\u6bb5', '\u5176\u4f59\u5404\u6bb5\uff08\u624b\u6539\uff09')

    def warn_drop(L):
        k = next(i for i, l in enumerate(L) if l.startswith(WARN_PREFIX))
        L[k] = ''

    wr('prod-warn-edited.md', rm(prod, warn_edit))
    wr('prod-warn-dropped.md', rm(prod, warn_drop))

    x = rm(difffrag)
    for k, ln in enumerate(x):
        m = re.match(r'^\|\s*(\d+)\s*\|', ln)
        if m and m.group(1) == '67':
            x[k] = ln.rstrip('\r')[:-1] + ' \uff08\u81ea\u68c0\u6ce8\u5165\u7684\u7f3a\u9677\uff09 |'
            break
    wr('diffs-defect.md', x)

    io.open(os.path.join(ST, 'bws-st-stale.py'), 'w', encoding='utf-8', newline='\n').write(
        '# -*- coding: utf-8 -*-\n'
        '"""self-test fixture: a rule that matches no section must be reported as stale."""\n'
        'import importlib.util, os, sys\n'
        'ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(\n'
        '    os.path.abspath(__file__)))))\n'
        'spec = importlib.util.spec_from_file_location(\n'
        '    "so", os.path.join(ROOT, "tools", "probes", "section-ownership.py"))\n'
        'm = importlib.util.module_from_spec(spec)\n'
        'spec.loader.exec_module(m)\n'
        'm.SECTION_OWNERSHIP.append(("## \\u4e0d\\u5b58\\u5728\\u7684\\u6bb5\\uff08\\u81ea\\u68c0\\u5939'
        '\\u5177\\uff09", m.OWN_MANUAL, "stale-rule fixture"))\n'
        'sys.argv = ["so", "--check", "--product",\n'
        '            os.path.join(ROOT, ".ai-tmp", "test", "bws-st", "prod-drift.md"),\n'
        '            "--fragment", os.path.join(ROOT, ".ai-tmp", "test", "bws-st", "frag-ok.md")]\n'
        'sys.exit(m.main())\n')

    print('fixtures in ' + ST)
    print('rows replaced in prod-synced.md = %d' % n)
    for f in sorted(os.listdir(ST)):
        print('  %-24s %8d B' % (f, os.path.getsize(os.path.join(ST, f))))


if __name__ == '__main__':
    main()
