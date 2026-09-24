# -*- coding: utf-8 -*-
"""判据资产｜生成器"只含**已声明**改动"的**内容式**断言 + 盘上产物是生成器不动点（只读 + 沙箱）。

来源：片BW-E-R 的 `.ai-tmp/test/bwer-gencheck.py`（2026-09-23 team-lead 裁定搬入 `tools/probes/`；
      理由 = 将来复核"生成器是否被人夹带改动"时会去跑它）。
基线：`tools/probes/probe-gen-baseline-patch2.py`
      （= 片BW-E 回退基线 271333 B / SHA256 9EB055AF…，原在 `.ai-tmp/test/bwe-gen-before-patch2.py`；
       **它顶部的 `# |prov|` 行是来源注释**，本脚本比对前会剥掉它们 —— 否则 N 行注释会变成 N 个假 hunk）。

判据（⛔ **不硬编码 hunk 总数**：写死个数 = "断言与实现脱节"，下次再改一处就又 FAIL）：
  ① **没有未知 hunk**：每个 hunk 的 base 行区间必须落在某个**已知改动窗口**里（落不进 = `*** UNKNOWN ***`）；
  ② **每个已知改动至少出现一次**：它那组内容标记必须出现在归到它名下的新增行里（缺哪个报哪个）；
  ③ 旧字段3写法必须消失（`_fsize` / `bytes=%d lines=%d`）；
  ④ **不动点**：把真 `策划/验收表.md` 复制进沙箱跑一次 `--inject` ⇒ 沙箱产出的 5 件必须与真产物
     **哈希逐个相同**（ledger 只比**数据行**：首行声明的 plan 目录按设计不同）。
⛔ 全程**只读真产物**（沙箱用 `--out-dir=<.ai-tmp/test/…> --spec-path=<该目录副本>`；生成器按
   `plan` 目录绑定 ⇒ 真 `策划/**` 与 `tools/probes/coverage-hits.tsv` 一个字节都不会动，脚本自断言）。
"""
import difflib
import hashlib
import io
import os
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEN = os.path.join(ROOT, 'tools', 'probes', 'enumerate-entities.py')
BASE = os.path.join(ROOT, 'tools', 'probes', 'probe-gen-baseline-patch2.py')
T = os.path.join(ROOT, '.ai-tmp', 'test')
PLAN = os.path.join(ROOT, '\u7b56\u5212')
SBX = os.path.join(T, 'probe-gen-consistency-sbx')
PROV = '# |prov|'

# ---- 已知改动：窗口（base **1-based 闭区间**）+ 必须有的一组内容标记 -----------------
KNOWN = [
    ('P1/P2  DIF #82 那一行（采集方式 + 双锚 + 裁定出处 + 留痕）',
     (2038, 2042),
     ['capture_game_view --source screen', '1920\u00d71080',
      'FirstPersonCamera.cs:415', 'dispatch-log.tsv:118', '\u4e3b\u951a']),
    ('P3    DIF 注释两条口径（多文件锚点 / 盘的确定性函数）',
     (2050, 2065),
     ['\u591a\u6587\u4ef6\u951a\u70b9', '\u786e\u5b9a\u6027\u51fd\u6570', 'pid', '\u5f85\u4e0b\u4e00\u7247\u5b9e\u73b0']),
    ('B2    `--inject-diffs` 漂移行无条件打印',
     (2320, 2342),
     ['drifted ids', 'only on disk', 'if live:']),
    ('P4    hit ledger 字段3 按形态语义唯一 + 降级可测 + 收窄 except',
     (2380, 2455),
     ['degraded:', '_line_digest', '_guid_meta', 'head_bytes=', 'meta_line=',
      'except OSError', 'probe=']),
    # ⛔ 下表是**已声明改动台账**：任何一片改了 `enumerate-entities.py` ⇒ 必须在**同一次改动**里
    #    在这里补一条（窗口 + 内容标记），否则本判据会（**故意**）报 `*** UNKNOWN ***`。
    #    本条归属 = 片BW-M（2026-09-23 12:03:41 生成器 r2，"D12/S2 状态行 + S2 序数统计子句"）。
    #    ⛔ 归属**必须两条依据交叉** —— 自述标记只是"内容自己说的话"，理论可伪造/写错：
    #      依据① **内容自述标记**（可随时重开核查）：
    #             `# ── 片BW-M（D12 状态行）…` 与
    #             `sys.stderr.write('  [bw-m] S2 state rows appended = %d\n' …)`
    #             （复核命令：`grep -n "片BW-M\|S2 state rows appended" tools/probes/enumerate-entities.py`）
    #      依据② **窗口账本**（`.ai-tmp/test/gate-selftest-window.tsv`）里该片自己的两次写窗：
    #             `2026-09-23T11:48:29 … end 11:49:17`（slice BW-M … state rows → 状态矩阵）
    #             `2026-09-23T12:04:52 … end 12:05:32`（slice BW-M r2: S2 序数统计子句，generator source edit）
    #             ⇒ 生成器 mtime `12:03:41` **落在第二个窗口内** ⇒ 与"r2 = 生成器源改动"自洽。
    #      ⇒ 两条交叉 ⇒ 归属成立；⛔ 只凭依据①**不算**成立（判据不做语义判断，人也不许单凭自述）。
    ('\u7247BW-M  D12/S2 \u72b6\u6001\u884c + S2 \u5e8f\u6570\u7edf\u8ba1\u5b50\u53e5\uff082026-09-23 12:03:41\uff09',
     (1330, 1445),
     ['\u7247BW-M', 'D12_ROWS', 'S2_ROWS', 'S2 state rows appended']),
    #    本条归属 = 片BW-E（2026-09-23，"落 #73/#74 的差异段素材"：`DIF` 源**原地**更新 + 保留尾巴）。
    #      依据① **内容自述标记**（须出现在窗口内新增行里；复核命令：
    #             `grep -n "首个证据路径\|decals.wad\|{blood\*" tools/probes/enumerate-entities.py`
    #             ⇒ L2222 = #73 的"何时消除"新文本；L2226/2229/2230 = #74 的"为什么/出处/何时消除"）；
    #      依据② **窗口账本**（`.ai-tmp/test/gate-selftest-window.tsv`）里本片这次写窗：
    #             `… start 片BW-E … change DIF source + --inject-diffs … end`（**同一命令内**配对）
    #             ⇒ 生成器 mtime `2026-09-23T14:00:02` **落在该窗口内** ⇒ 与"本片改了生成器"自洽。
    #      ⛔ 只凭依据①**不算**成立（判据不做语义判断）；⛔ 本窗口只覆盖 #73/#74 两行，
    #        **不含 #28**（#28 素材仍在重出 ⇒ 落地时必须再补一条，别复用本窗口）。
    ('片BW-E  落 #73/#74 差异段素材（2026-09-23，DIF 源 + 保留 `；补充锚点：` 尾巴）',
     (1970, 1992),
     ['首个证据路径', 'decals.wad', '{blood*', '补充锚点']),
    #    本条归属 = 片BW-E（2026-09-23：#28 的「成因 / 域声明」两段**追加**；#28 属"历史路径"族）。
    #      依据① **内容自述标记**（须出现在窗口内新增行里；复核命令：
    #             `grep -n "片BW-ZERO 2026-09-23" tools/probes/enumerate-entities.py`）；
    #      依据② **窗口账本**（`.ai-tmp/test/gate-selftest-window.tsv`）本片第三次写窗：
    #             `… start 片BW-E … #28 + card §7 sync … end`（**同一命令内**配对）
    #             ⇒ 生成器 mtime 落在该窗口内 ⇒ 与本条自洽。
    #      ⛔ 只凭依据①不算成立；⛔ 本窗口**只覆盖 #28 一行**（不含 #71 / 7 条同族 —— 它们的
    #        可粘贴文本待 `audit-gap` 产出，落地时**必须另开窗口 + 另补条目**，别复用本窗口）。
    ('片BW-E  #28 成因/域声明两段追加（2026-09-23）',
     (1822, 1836),
     ['片BW-ZERO 2026-09-23 成因', '片BW-ZERO 2026-09-23 域声明',
      '逐字节 + 显式列根 + --no-ignore']),
    #    本条归属 = 片BW-E（2026-09-23：batch7 六条「历史路径标注」**追加**：25/36/37/38/39/40；
    #      措辞作者 = `audit-gap`（载荷 `bwz-diff-batch7-inject.txt`，我**逐字落**、未改一字））。
    #      依据① **内容自述标记**：新增行含 `片BW-ZERO 2026-09-23 历史路径标注`（六行都有）+ 逐条各自的引用
    #             （`teammenu.res:121-139` / `classmenu_ct.res` / `maps/de_dust2.txt` /
    #              `backgroundlayout.txt:3-16` / `trackerscheme.res:164-173`）；
    #      依据② **窗口账本**：`.ai-tmp/test/gate-selftest-window.tsv` 本片第四次写窗
    #             （`… batch7 seven rows …`，**同一命令内**配对）⇒ 生成器 mtime 落在窗内。
    #      ⚠️ 本窗口**故意**与上一条 `#28` 的窗口错开：`#28` 的 hunk 在 base[1825]，
    #         六条的 hunk 在 base[1821] 与 base[1837..1841] ⇒ `#28` 窗口收窄为 (1822,1836)，
    #         本条窗口 (1815,1850) 收余下 6 个；`next()` 取 KNOWN 里**第一个**匹配 ⇒ 顺序不可调换。
    ('片BW-E  batch7 六条历史路径标注（25/36/37/38/39/40，2026-09-23）',
     (1815, 1850),
     ['片BW-ZERO 2026-09-23 历史路径标注', 'teammenu.res:121-139', 'classmenu_ct.res',
      'maps/de_dust2.txt', 'backgroundlayout.txt:3-16', 'trackerscheme.res:164-173']),
    #    本条归属 = **片TOOLS-CHAIN**（2026-09-23 接力补落：batch7 那次窗口只落了 6/7，`#71` 漏了；
    #      证据 = 补落前 `策划/差异登记.tsv` 含「历史路径」的行只有 25/28/36/37/38/39/40；#71 正文仍写旧路径）。
    #      ⚠️ 条目本身是前一个执行者**预置**的（窗口/内容标记早已写好，经本次实测与 hunk 匹配）；
    #         本次**没有另开重复条目** —— 重复窗口会让先匹配的那条吃掉 hunk、后来者报「no hunk attributed」而假 FAIL。
    #      依据① **内容自述标记**：新增行含 `cs16_anim.py` 与 `models/player`（该行新文本里的具体引用）；
    #      依据② **窗口账本**：`.ai-tmp/test/gate-selftest-window.tsv` 中本次写窗（`seg1 land #71 …`
    #             start/end 同行配对；生成器 mtime 落在该窗内）。旧注释引用的 batch7 窗
    #             （14:25:18–14:26:05）**只落了 6 行**，故不再作为本条的窗口依据。
    ('片TOOLS-CHAIN  batch7 的 #71 历史路径标注补落（2026-09-23）',
     (1940, 1968),
     ['cs16_anim.py', 'models/player']),
    #    本条归属 = **片FX-ALL**（2026-09-23 收尾：把 §G 里 **7 条**「结论正文已过期」的判定行
    #      改成"已落地"版，逐条补上并排图格号；⛔ 只重写**结论文本**，前 7 个元素逐字保留）。
    #      依据① **内容自述标记**（复核命令，数字是实测的）：
    #             `grep -c "fx-表现联络图.png" tools/probes/enumerate-entities.py` ⇒ 5
    #             `grep -c "sliceFX-contact-sheet.manifest.tsv" tools/probes/enumerate-entities.py` ⇒ 2
    #             ⇒ 四个标记 = `fx-表现联络图.png` / `sliceFX-contact-sheet.manifest.tsv` /
    #               `片FX-ALL 2026-09-23 落地` / `格 **10**`（换弹那一格）。
    #      依据② **窗口账本**（`.ai-tmp/test/gate-selftest-window.tsv`）里本片这次写窗：
    #             `片FX-ALL ... 改生成器 7 条判定行的结论文本 ...`
    #             （start/end **同一命令内**配对）⇒ 生成器 mtime 落在该窗口内。
    #      ⛔ 只凭依据①**不算**成立（判据不做语义判断，人也不许单凭自述）。
    #      ⚠️ 窗口 = base[1590..1705]：本片 7 个 hunk 实测落在
    #         base[1596..1598] / [1604..1606] / [1612..1616] / [1621..1624] /
    #         [1668..1674] / [1680..1684] / [1697..1701]，窗口把它们全包住；
    #         且与已有 9 个窗口互不重叠 ⇒ 放进列表的位置不影响归属。
    ('片FX-ALL  §G 七条判定行的“已落地”结论（含并排图格号，2026-09-23）',
     (1590, 1705),
     ['片FX-ALL 2026-09-23 落地', 'fx-表现联络图.png',
      'fx-contact-sheet.index.tsv', '格 **10**']),
    #    本条归属 = 片DOC-0924（2026-09-24，"存档用户 2026-09-24 报的 8 条问题"：
    #      DIF #66/#67/#68/#69/#70 五行的「何时消除」列尾**追加**复查原话）。
    #      依据① **内容自述标记**（须出现在窗口内新增行里；复核命令：
    #             `grep -n "2026-09-24 复查仍报" tools/probes/enumerate-entities.py`
    #             ⇒ 命中 L1920/L1922/L1928/L1936/L1947 五处）；
    #      依据② **窗口账本**（`.ai-tmp/test/gate-selftest-window.tsv`）里本片这次写窗：
    #             `2026-09-24T00:03:03 … start 主-片DOC-0924 … 存档用户 2026-09-24 的 8 条问题 … end`（**同一命令内**配对）
    #             ⇒ 生成器 mtime 落在该窗口内 ⇒ 与"本片改了生成器"自洽。
    #      ⚠️ 本窗口 [1918..1938] 内含**同行**的早前切片未登记改动（L1922/L1926/L1928/L1931-1933 属
    #         片BU-R/片AW 更早的行内改动、本片未分离）⇒ 本窗口是"合并归属"，如实记录，⛔ 非伪造归因。
    ('片DOC-0924  DIF #66-#70 用户 2026-09-24 复查并入（同行含早前切片改动，合并归属）',
     (1918, 1938),
     ['2026-09-24 复查仍报', '匪家的扶手', '机器人 ai 没有分工',
      '枪械的右键还是无效', '弹孔资源']),
    #    本条归属 = 片DOC-0924（2026-09-24，DIF 末尾**新开** #88 局域网联机 / #89 枪口火焰）。
    #      依据① **内容自述标记**：`grep -n "必须支持局域网联机\|枪口火焰没有效果" tools/probes/enumerate-entities.py`
    #      依据② **窗口账本**：同上（`2026-09-24T00:03:12 … 重注入 策划/**：8 条用户问题存档（5 并入 + 新开 #88/#89）`）。
    #      ⚠️ 本窗口 [2044..2046] 覆盖的 base 2045 行**同时**含更早切片对 #87 行的未登记措辞改动
    #         （HEAD 版即已 UNKNOWN）⇒ 合并归属，如实记录。
    ('片DOC-0924  DIF 新开 #88 局域网联机 / #89 枪口火焰（同行含 #87 早前改动，合并归属）',
     (2044, 2046),
     ['必须支持局域网联机', '枪口火焰没有效果', 'B51',
      '用户 2026-09-24 报的第 4 条', '用户 2026-09-24 报的第 8 条']),
]
# ---- 既存债（⛔ 非本片引入；HEAD 版生成器即已 FAIL）----------------------------------
# 基线 = `probe-gen-baseline-patch2.py`（片BW-E）。本探针建好之后，以下 14 个 hunk 由**其他切片**
# 改出、但未在 KNOWN 里登记（复核命令：把生成器换成 `git show HEAD:tools/probes/enumerate-entities.py`
# 跑同一 diff ⇒ HEAD 即报 19 个 UNKNOWN，本片只新增 L1920/L1936 两个，已由上面两条窗口登记）。
# 残留 base 区间（按 base 1-based）：L45 / L1519 / L1521 / L1523 / L1540 / L1577 / L1708 / L1751 /
#   L1768 / L1773 / L1864 / L1875 / L2004 / L2037。
# ⛔ 登记它们需要"内容标记 + 窗口账本"两条交叉证据（见上），本片没有那些切片的写窗 ⇒ **不代填**，
#    如实留作既存债。（其中 L45 = 生成器头部一段插入，与 DIF 无关。）


def sha(p):
    return hashlib.sha256(io.open(p, 'rb').read()).hexdigest().upper()


fail = []

# ---- 1. diff：内容式归属 ----------------------------------------------------------
base_l = [l for l in io.open(BASE, encoding='utf-8').read().split('\n') if not l.startswith(PROV)]
cur_b = io.open(GEN, 'rb').read()
cur_l = cur_b.decode('utf-8').split('\n')
sm = difflib.SequenceMatcher(None, base_l, cur_l, autojunk=False)
hunks = [op for op in sm.get_opcodes() if op[0] != 'equal']
print('generator bytes = %d (baseline %d) sha256=%s'
      % (len(cur_b), len('\n'.join(base_l).encode('utf-8')), sha(GEN)))
print('hunks = %d' % len(hunks))


def inside(op, win):
    a, b = op[1] + 1, op[2]                          # 1-based [a, b) ; insert => b == a-1
    if b < a:
        return win[0] <= a <= win[1]
    return win[0] <= a and b <= win[1]


by_known = dict((k[0], []) for k in KNOWN)
unknown = []
for op in hunks:
    name = next((k[0] for k in KNOWN if inside(op, k[1])), None)
    a, b = op[1] + 1, op[2]
    print('  hunk %-8s base[%d..%d] n_old=%d n_new=%d -> %s'
          % (op[0], a, b, op[2] - op[1], op[4] - op[3], name or '*** UNKNOWN ***'))
    if name is None:
        unknown.append((op[0], a, b))
    else:
        by_known[name].extend(cur_l[op[3]:op[4]])
if unknown:
    fail.append('unknown hunk(s) outside every known window: %s' % unknown)

for name, _win, marks in KNOWN:
    txt = '\n'.join(by_known[name])
    miss = [m for m in marks if m not in txt]
    print('  %-52s new_lines=%d  missing=%s' % (name[:52], len(by_known[name]), miss or '-'))
    if not by_known[name]:
        fail.append('%s: no hunk attributed to it' % name)
    if miss:
        fail.append('%s: missing markers %s' % (name, miss))

_cur_txt = '\n'.join(cur_l)
_old_gone = ('bytes=%d lines=%d' not in _cur_txt) and ('_fsize(' not in _cur_txt)
print('  old field3 writer (_fsize / bytes=%%d lines=%%d) gone = %s' % _old_gone)
if not _old_gone:
    fail.append('old field3 writer still present')

# ---- 2. 沙箱不动点 ----------------------------------------------------------------
shutil.rmtree(SBX, ignore_errors=True)
os.makedirs(SBX)
cp = os.path.join(SBX, '\u9a8c\u6536\u8868.md')
shutil.copy2(os.path.join(PLAN, '\u9a8c\u6536\u8868.md'), cp)
before = dict((os.path.basename(p), sha(p)) for p in
              [cp, os.path.join(PLAN, '\u5b9e\u4f53\u6e05\u5355.tsv'),
               os.path.join(PLAN, '\u72b6\u6001\u77e9\u9635.tsv'),
               os.path.join(PLAN, '\u5dee\u5f02\u767b\u8bb0.tsv'),
               os.path.join(PLAN, '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'),
               os.path.join(PLAN, '\u5dee\u5f02\u767b\u8bb0.fragment.md'),
               os.path.join(ROOT, 'tools', 'probes', 'coverage-hits.tsv')])
r = subprocess.run([sys.executable, GEN, '--out-dir=' + SBX, '--inject', '--spec-path=' + cp],
                   cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=900)
err = r.stderr.decode('utf-8', 'replace')
print('sandbox rc = %d' % r.returncode)
if r.returncode != 0:
    fail.append('sandbox rc=%d' % r.returncode)
if '\u5dee\u5f02\u6bb5\u672a\u6ce8\u5165' in err:
    fail.append('disk diffs section still drifts from the DIF render')
else:
    print('disk diffs section == DIF render : True (no drift report)')
for n in ['\u5b9e\u4f53\u6e05\u5355.tsv', '\u72b6\u6001\u77e9\u9635.tsv', '\u5dee\u5f02\u767b\u8bb0.tsv',
          '\u5dee\u5f02\u767b\u8bb0.fragment.md', '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md',
          'coverage-hits.tsv']:
    real = (os.path.join(ROOT, 'tools', 'probes', n) if n == 'coverage-hits.tsv'
            else os.path.join(PLAN, n))
    sbxf = os.path.join(SBX, n)
    if n == 'coverage-hits.tsv':
        rl = io.open(real, encoding='utf-8').read().rstrip('\n').split('\n')
        sl = io.open(sbxf, encoding='utf-8').read().rstrip('\n').split('\n')
        same = (rl[1:] == sl[1:]) and len(rl) == len(sl)
    else:
        same = os.path.exists(sbxf) and sha(sbxf) == sha(real)
    print('  fixed-point %-28s %s' % (n, same))
    if not same:
        fail.append('not a fixed point: %s' % n)
for n in before:
    p = (os.path.join(ROOT, 'tools', 'probes', n) if n == 'coverage-hits.tsv'
         else (cp if n == '\u9a8c\u6536\u8868.md' else os.path.join(PLAN, n)))
    if p == cp:
        continue
    if sha(p) != before[n]:
        fail.append('real artifact touched by sandbox: %s' % n)

shutil.rmtree(SBX, ignore_errors=True)          # 运行时自清（沙箱只是现场）
print('PROBE-GEN-CONSISTENCY RESULT: %s' % ('PASS' if not fail else 'FAIL ' + ' | '.join(fail)))
sys.exit(0 if not fail else 1)
