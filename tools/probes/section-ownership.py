# -*- coding: utf-8 -*-
"""section-ownership.py -- 段属权：`策划/验收表.md` 的每一段归谁所有（生成器重建 / 手工 / 混合）。

为什么要有这份清单（2026-09-23 片BW-S）
---------------------------------------
三类实测事故，全都源于"属权没写明"：
  ① **丢活**：手写内容写进 `COVERAGE-BEGIN`…`COVERAGE-END` 标记之间 ⇒ 下一次
     `enumerate-entities.py --inject` 静默抹掉（实测：片BU-R6 写进 D10 行的一段 `A5 转 PASS`
     1 次注入就没了，靠备份才恢复）。
  ② **藏缺陷**：反过来，标记之间的一行结论被**手改**过（D9 行 `不一致` -> `一致`）⇒
     生成器再跑一次才发现；谁改的、为什么改，没有任何记录。
  ③ **同一件事手改三处**：`策划/差异登记.tsv`（由脚本里的 `DIF` 生成）+ 手写的「允许的差异」段 +
     该段的聚合数 ⇒ 三处各自漂移，没人能看见。

⇒ 本文件 = 段属权的**单一真源**：
  * `SECTION_OWNERSHIP` = 机列清单（段名锚 -> 属权 -> 实现位置）。`enumerate-entities.py`
    导入它（并把它作为本模块的唯一定义，见该脚本顶部的绑定注释）。
  * `--list`  = 从盘上产品**机切**段区间（跳过 ``` 代码栅栏），逐段解析属权；
  * `--check` = 两条硬判据：
        ① **双向覆盖**：盘上每个段都被清单命中 ≥1 次，且清单里没有"匹配不到任何段"的陈旧规则
           （只查一个方向会得到假绿：漏一个段 / 多一条废规则都看不出来）；
        ② **块内没有非生成器来源的行**：`COVERAGE-BEGIN`…`COVERAGE-END` 之间**逐字节等于**
           生成器的重建输出（`策划/覆盖矩阵判定.fragment.md`）⇒ 手写进块内、或手改块内结论，
           当场测红（这一条正是事故 ①② 的可机械化形态）。

用法：
    python tools/probes/section-ownership.py --list
    python tools/probes/section-ownership.py --check [--product <验收表.md>]
                                                    [--fragment <覆盖矩阵判定.fragment.md>]
    python tools/probes/section-ownership.py --check --rebuild
退出码：0 = PASS；1 = FAIL。

`--rebuild`（片BW-S-R）：**不读** `策划/*.fragment.md` —— 改在**临时目录**里跑一遍
    `enumerate-entities.py --out-dir=<tmp>`，拿**这次刚生成**的沙箱片段与盘上块逐字节比。
    为什么必须这样：读盘上的片段等于"相信生成器刚跑过"；片段陈旧（生成器尚未跑 / 被人手改）
    时，盘上块与陈旧片段**一起错**⇒ 检查**假绿**。`--rebuild` 把判据的输入换成"当下重算的产物"，
    于是"块里被塞了手写行 / 块内结论被手改 / 生成器输出不可复现"三种形态当场测红。
    同一次重建的 `差异登记.fragment.md` 也拿来比盘上的「允许的差异」段（该段 2026-09-23 起由
    `DIF` 同源渲染、`--inject-diffs` 单向覆盖）⇒ 「差异段被手改」也当场测红（check 2b）。

⛔ **自检只许喂副本**（`--product` 可覆盖；`--rebuild` 的产物只落临时目录）：本脚本**只读**
   共享产物（`策划/**`）—— 对它一个字节都不许动（skill `reference/anti-gaming.md` §五 第 3 条）。
"""
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
PLAN = os.path.join(ROOT, '\u7b56\u5212')                              # 策划
DEFAULT_PRODUCT = os.path.join(PLAN, '\u9a8c\u6536\u8868.md')          # 验收表.md
DEFAULT_FRAGMENT = os.path.join(PLAN, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md')

# 标记：只用**纯名字**（不带 `<!-- -->`），见 HEADER_WARNING 的说明。
MK_B = 'COVERAGE-BEGIN'
MK_E = 'COVERAGE-END'

# 属权码：
#   GEN    = 整段由生成器重建（--inject 覆盖 ⇒ 手写会丢）
#   MANUAL = 整段手工维护
#   MIXED  = 同一段里既有生成器写的行、也有手工行
OWN_GEN, OWN_MANUAL, OWN_MIXED = 'GEN', 'MANUAL', 'MIXED'

# ============================================================================
#  SECTION_OWNERSHIP -- 段属权的单一真源
#  每项 = (段名锚, 属权, 实现位置 / 备注)
#  锚 = 该段**首行**里必须出现的子串；`--check` 要求「每锚命中恰好 1 段」+「每段被恰好 1 锚命中」。
# ============================================================================
SECTION_OWNERSHIP = [
    ('# \u9a8c\u6536\u8868\uff08\u4ea4\u4ed8\u95f8\u95e8', OWN_MIXED,
     'H1 + 规则块 = 手工；**表头第 2 行由 `enumerate-entities.py --inject` 注入**（段属权警告，见 '
     '`HEADER_WARNING_PREFIX`）⇒ 这一行属生成器所有（占位方式见 `header_warning()` 的说明）'),
    ('## \u72b6\u6001\u56fe\u4f8b', OWN_MANUAL, '-'),
    ('## A. \u542f\u52a8\u4e0e\u83dc\u5355\u94fe\u8def', OWN_MANUAL, 'A 段验收行（M1~M10）'),
    ('### \u5207\u7247I', OWN_MANUAL, 'A 段重采台账（手工）'),
    ('## B. \u6e38\u620f\u5185 HUD', OWN_MANUAL, 'B 段验收行（H1~H15）'),
    ('## C. \u73a9\u6cd5\u7cfb\u7edf', OWN_MANUAL, 'C 段验收行（G1~G17）'),
    ('## D. \u673a\u5668\u4eba 3 \u6863\u96be\u5ea6', OWN_MANUAL, 'D 段验收行（P1~P4）'),
    ('## E. de_dust2 \u5730\u56fe', OWN_MANUAL, 'E 段验收行（R1~R8）'),
    ('## F. \u8d44\u6e90', OWN_MANUAL, 'F 段验收行（资源对账）'),
    ('## \u5141\u8bb8\u7684\u5dee\u5f02', OWN_MIXED,
     '段内散文块 = 手工；`| <id> | …` **判定行 = 生成器**（`DIF` 同源渲染，'
     '`enumerate-entities.py --inject-diffs` **逐行原地替换**、行数不变）⇒ 手写请写进 `DIF` 源，'
     '⛔ 不要直接改盘上那几行（会被下一次注入覆盖）。判据 = `--check --rebuild` 的 check 2b。'),
    ('## \u6536\u5c3e\u81ea\u68c0', OWN_MANUAL, '§1.11 八条自检（含第 3/4 条的聚合数）'),
    ('### \u8868\u4f53\u8ba1\u6570', OWN_MANUAL, '表体计数 + 分节表'),
    ('### \u5c1a\u672a\u8fdb\u8868\u7684\u89c4\u683c\u9879', OWN_MANUAL, '缺规则的指针（不许自己补）'),
    ('## \u8054\u7edc\u56fe\u7d22\u5f15', OWN_MANUAL, '表现类行的格号 -> 联络图'),
    ('## H. \u95f8\u95e8\u6761\u76ee\u4e0e\u6a21\u677f\u5bf9\u8d26', OWN_MANUAL, '模板 GATE-ITEMS 对账'),
    ('## G. \u8986\u76d6\u77e9\u9635\u5224\u5b9a', OWN_MIXED,
     '标题 + 2 行来源说明 = 手工；判定行块 = 生成器（见下一项）'),
    ('<!-- ' + MK_B + ' -->', OWN_GEN,
     '`enumerate-entities.py` 的 `_brand_block`（`ENT` -> 判定行 + 表头）；`--inject` 整块替换'),
    ('## \u7247T\uff08', OWN_MANUAL, '片T 外观域判定（手工）'),
    ('## \u00a7W.', OWN_MANUAL, '§W 外观可量化复核（手工）'),
    ('## \u00a7BV.', OWN_MANUAL, '§BV 编辑器图标叠加层泄漏（手工）'),
    ('### \u00a7BV-R \u4fee\u8ba2\u7248', OWN_MANUAL, '§BV 修订版（手工，冲突处以 BVR 为准）'),
]

# 表头警告行的前缀（生成器按它定位/替换自己写过的那一行 ⇒ 幂等）。
HEADER_WARNING_PREFIX = '> \u26a0\ufe0f \u6bb5\u5c5e\u6743\uff08\u673a\u5217'


def header_warning():
    """生成器要注入表头的那一行（**必须是单行**）。

    ⛔ 两条硬约束（都踩过）：
      * **不许含 `<!-- COVERAGE-BEGIN -->` 这种字面标记**：`--inject` 用 `find()` 定位标记，
        警告里出现同样的字面量会让它**从警告行开始替换**，把 §A~§F 整段吞掉。这里只写纯名字。
      * **不许新增行**：`策划/对照表.md` 有 11 处 `策划/验收表.md:NNN` 行号引用，而 `对照表.md`
        **不由任何生成器所有**（不许手改）⇒ 在表头**插行**会把那 11 处全部顶错位。故本行
        **占用表头里已有的一个空行**（行数不变），由 `--inject` 写、也由它自己识别替换。
    """
    return (HEADER_WARNING_PREFIX +
            '\uff08\u5355\u4e00\u771f\u6e90 = `tools/probes/section-ownership.py`\uff0c`--check` \u628a\u5b88\uff09\uff1a'
            '\u6807\u8bb0 ' + MK_B + ' \u2026 ' + MK_E + ' \u4e4b\u95f4\u7684\u8986\u76d6\u77e9\u9635\u5224\u5b9a\u884c\u5757'
            '\uff08\u00a7G\uff09**\u7531 `tools/probes/enumerate-entities.py` \u91cd\u5efa** \u21d2 `--inject` \u4f1a\u6574\u5757\u8986\u76d6\u5b83\uff1a'
            '\u624b\u5199\u8fdb\u5757\u5185\u7684\u5185\u5bb9\u4f1a\u4e22\uff0c\u5757\u5185\u7ed3\u8bba\u88ab\u624b\u6539\u4e5f\u4f1a\u88ab\u8986\u76d6\u3002'
            '\u5176\u4f59\u5404\u6bb5\uff08A~F / \u5141\u8bb8\u7684\u5dee\u5f02 / \u6536\u5c3e\u81ea\u68c0 / \u8054\u7edc\u56fe\u7d22\u5f15 / \u00a7H / \u7247T / \u00a7W / \u00a7BV\uff09'
            '\u5747\u4e3a**\u624b\u5de5\u6bb5**\uff1b\u624b\u5199\u8bf7\u5199\u5230\u5757\u5916\uff0c\u6216\u6539\u751f\u6210\u5668\u6e90\u3002')


def read(path):
    with io.open(path, encoding='utf-8', errors='replace') as f:
        return f.read()


DIFF_SEC = '\u5141\u8bb8\u7684\u5dee\u5f02'      # 允许的差异


def diff_rows(text, need_section=True):
    """判定行：id -> 整行文本。
    `need_section=True`（盘上 `验收表.md`）= 只取 `## 允许的差异…` 段（口径 =
    `enumerate-entities.py` 的 `_collect_section_diffs`：段到下一个 `## ` / `### ` 标题为止）；
    `need_section=False`（`差异登记.fragment.md`）= **整份就是那张表**（该片段不带标题行）。
    """
    lines = text.split('\n')
    if need_section:
        s0 = next((k for k, l in enumerate(lines)
                   if l.startswith('## ') and DIFF_SEC in l), -1)
        if s0 < 0:
            return {}
        lines = lines[s0 + 1:]
    out = {}
    for l in lines:
        l = l.rstrip('\r')
        if need_section and (l.startswith('## ') or l.startswith('### ')):
            break
        m = re.match(r'^\|\s*(\d+)\s*\|', l)
        if m:
            out[m.group(1)] = l
    return out


HEAD_RE = re.compile(r'^#{1,6}\s')
MK_RE = re.compile(r'^<!--\s*(?:COVERAGE|DIFFS|OWNERSHIP)-(?:BEGIN|END)\s*-->')


def regions_of(text):
    """机切段：[(首行idx, 末行idx, 首行文本)]，0-based、含端点；跳过 ``` 代码栅栏内的 `#` 行。"""
    lines = text.split('\n')
    starts = []
    fence = False
    i = 0
    while i < len(lines):
        s = lines[i].rstrip('\r')
        if s.lstrip().startswith('```'):
            fence = not fence
            i += 1
            continue
        if not fence:
            if re.match(r'^<!--\s*' + MK_B + r'\s*-->$', s):
                j = i
                while j < len(lines) and not re.match(r'^<!--\s*' + MK_E + r'\s*-->$', lines[j].rstrip('\r')):
                    j += 1
                starts.append((i, min(j, len(lines) - 1)))
                i = j + 1
                continue
            if HEAD_RE.match(s) or MK_RE.match(s):
                starts.append((i, -1))
        i += 1
    out = []
    for k, (i0, i1) in enumerate(starts):
        if i1 < 0:
            i1 = (starts[k + 1][0] - 1) if k + 1 < len(starts) else len(lines) - 1
        out.append((i0, i1, lines[i0].rstrip('\r')))
    return lines, out


def resolve(title):
    hits = [r for r in SECTION_OWNERSHIP if r[0] in title]
    return hits


def main():
    argv = sys.argv[1:]
    product = DEFAULT_PRODUCT
    fragment = DEFAULT_FRAGMENT
    if '--product' in argv:
        product = argv[argv.index('--product') + 1]
    if '--fragment' in argv:
        fragment = argv[argv.index('--fragment') + 1]
    do_list = '--list' in argv
    do_check = '--check' in argv
    if not (do_list or do_check):
        do_list = do_check = True

    # --- --rebuild：判据的输入换成"当下重算的产物"（不读 策划/*.fragment.md）----------
    pre_fails = []
    do_rebuild = '--rebuild' in argv
    gen_ok = True
    diff_frag = None
    sandbox = None
    if do_rebuild:
        sandbox = tempfile.mkdtemp(prefix='so-rebuild-')
        gen = os.path.join(ROOT, 'tools', 'probes', 'enumerate-entities.py')
        gr = subprocess.run([sys.executable, gen, '--out-dir=' + sandbox], cwd=ROOT,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        fragment = os.path.join(sandbox, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md')
        diff_frag = os.path.join(sandbox, '\u5dee\u5f02\u767b\u8bb0.fragment.md')
        gen_ok = (gr.returncode == 0)
        print('REBUILD generator rc=%d  sandbox=%s  (fragment = %s)'
              % (gr.returncode, sandbox, fragment))
        if not gen_ok:
            tail = gr.stderr.decode('utf-8', 'replace').strip().split('\n')[-3:]
            pre_fails.append('generator rebuild failed (rc=%d): %s'
                             % (gr.returncode, ' | '.join(x.strip() for x in tail)))

    text = read(product)
    lines, regs = regions_of(text)
    print('INPUT product  = ' + os.path.abspath(product))
    print('INPUT fragment = ' + os.path.abspath(fragment))
    print('sections found = %d  (lines = %d)' % (len(regs), len(lines)))
    print('%-4s %-9s %-42s %s' % ('#', 'lines', 'first line (truncated)', 'ownership'))
    used = {}
    unknown = []
    for n, (i0, i1, title) in enumerate(regs, 1):
        hits = resolve(title)
        if len(hits) == 1:
            own = hits[0][1]
            used[hits[0][0]] = used.get(hits[0][0], 0) + 1
        elif not hits:
            own = 'UNKNOWN'
            unknown.append((n, title))
        else:
            own = 'AMBIGUOUS(%d)' % len(hits)
            unknown.append((n, title))
        print('%-4d %-9s %-42s %s' % (n, '%d-%d' % (i0 + 1, i1 + 1), title[:42].replace('\t', ' '), own))
    stale = [r[0] for r in SECTION_OWNERSHIP if used.get(r[0], 0) == 0]
    multi = [r[0] for r in SECTION_OWNERSHIP if used.get(r[0], 0) > 1]

    fails = list(pre_fails)
    print('')
    print('--- check 1: 段属权双向覆盖 ---')
    print('rules = %d   regions = %d   matched rules = %d' % (len(SECTION_OWNERSHIP), len(regs), len(used)))
    if unknown:
        fails.append('unclassified region(s): ' + '; '.join('%d:%s' % (n, t[:40]) for n, t in unknown))
        for n, t in unknown:
            print('  FAIL region %d has no (single) rule: %s' % (n, t[:70]))
    if stale:
        fails.append('stale rule(s) matching no region: ' + '; '.join(s[:40] for s in stale))
        for s in stale:
            print('  FAIL rule matches no region: ' + s)
    if multi:
        fails.append('rule(s) matching >1 region: ' + '; '.join(m[:40] for m in multi))
    if not (unknown or stale or multi):
        print('  OK   every region classified by exactly one rule; no stale rule')

    print('')
    print('--- check 2: 块内没有非生成器来源的行（COVERAGE 块 == 生成器重建输出）---')
    bi = None
    for k, (i0, i1, title) in enumerate(regs):
        if re.match(r'^<!--\s*' + MK_B + r'\s*-->$', title):
            bi = (i0, i1)
    if bi is None:
        fails.append('COVERAGE block not found in ' + product)
        print('  FAIL: no ' + MK_B + ' region found')
    elif not gen_ok:
        print('  SKIP: the generator rebuild failed (see the REBUILD line above)')
    elif not os.path.exists(fragment):
        fails.append('generator rebuild output missing: ' + fragment)
        print('  FAIL: missing ' + fragment)
    else:
        on_disk = '\n'.join(lines[bi[0]:bi[1] + 1]).rstrip('\r')
        rebuilt = read(fragment).rstrip('\n')
        if on_disk == rebuilt:
            print('  OK   block == generator rebuild output (%d bytes, %d lines)'
                  % (len(rebuilt.encode('utf-8')), rebuilt.count('\n') + 1))
        else:
            fails.append('COVERAGE block differs from the generator rebuild output')
            d = [k for k in range(min(len(on_disk), len(rebuilt))) if on_disk[k] != rebuilt[k]]
            at = d[0] if d else min(len(on_disk), len(rebuilt))
            print('  FAIL block != rebuild: len %d vs %d, first diff at char %d' % (len(on_disk), len(rebuilt), at))
            print('        on-disk : ...%s' % on_disk[max(0, at - 60):at + 60].replace('\n', '\\n'))
            print('        rebuild : ...%s' % rebuilt[max(0, at - 60):at + 60].replace('\n', '\\n'))
            al, bl = on_disk.split('\n'), rebuilt.split('\n')
            extra = [x for x in al if x not in bl][:5]
            for x in extra:
                print('        line only on disk: %s' % x[:120])

    print('')
    print('--- check 2b: 「允许的差异」段 == 同一次重建的 DIF 渲染（段内判定行属生成器）---')
    #  不是盘上那份（读盘上那份 = "相信生成器刚跑过"，片段陈旧时两边一起错 ⇒ 假绿）。
    if not do_rebuild:
        print('  SKIP check 2b: needs --rebuild (a fresh DIF render)')
    elif not gen_ok:
        print('  SKIP check 2b: the generator rebuild failed')
    else:
        disk_rows = diff_rows(text, True)
        sbx_rows = diff_rows(read(diff_frag), False) if os.path.exists(diff_frag) else {}
        only_d = sorted((k for k in disk_rows if k not in sbx_rows), key=int)
        only_s = sorted((k for k in sbx_rows if k not in disk_rows), key=int)
        drift = sorted((k for k in disk_rows if k in sbx_rows and disk_rows[k] != sbx_rows[k]), key=int)
        print('  on-disk rows = %d ; rebuilt rows = %d ; drift = %d ; only-disk = %d ; only-rebuild = %d'
              % (len(disk_rows), len(sbx_rows), len(drift), len(only_d), len(only_s)))
        if drift or only_d or only_s:
            fails.append('the 允许的差异 section drifted from the fresh DIF render '
                         '(drift=%d only-disk=%d only-rebuild=%d)'
                         % (len(drift), len(only_d), len(only_s)))
            for k in (drift + only_d + only_s)[:5]:
                a, b = disk_rows.get(k, ''), sbx_rows.get(k, '')
                d = [x for x in range(min(len(a), len(b))) if a[x] != b[x]]
                at = d[0] if d else min(len(a), len(b))
                print('  FAIL row %s: len %d vs %d, first diff at char %d' % (k, len(a), len(b), at))
                print('        on-disk : ...%s' % a[max(0, at - 60):at + 60])
                print('        rebuild : ...%s' % b[max(0, at - 60):at + 60])
        else:
            print('  OK   every one of the %d rows is byte-identical to the fresh `DIF` render'
                  % len(disk_rows))

    print('')
    print('--- check 3: 表头段属权警告 == 生成器文本（丢了 / 被手改都会测红）---')
    warns = [k for k, l in enumerate(lines) if l.startswith(HEADER_WARNING_PREFIX)]
    if len(warns) != 1:
        fails.append('header ownership warning line: found %d, want exactly 1' % len(warns))
        print('  FAIL warning line count = %d (want 1)' % len(warns))
    elif lines[warns[0]].rstrip('\r') != header_warning():
        fails.append('header ownership warning text differs from the generator text')
        print('  FAIL warning text != generator text')
        print('        on-disk : %s' % lines[warns[0]].rstrip('\r')[:100])
        print('        expected: %s' % header_warning()[:100])
    else:
        print('  OK   warning line present (L%d) and equal to the generator text (%d chars)'
              % (warns[0] + 1, len(header_warning())))

    print('')
    if sandbox:
        shutil.rmtree(sandbox, ignore_errors=True)
        print('REBUILD sandbox removed: ' + sandbox)
    if fails:
        print('RESULT: FAIL (%d)' % len(fails))
        for f in fails:
            print('  - ' + f)
        return 1
    print('RESULT: PASS -- section ownership closed both ways; block is pure generator output'
          + ('; 差异段 == fresh DIF render' if (do_rebuild and gen_ok) else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
