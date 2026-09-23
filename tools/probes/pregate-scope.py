# -*- coding: utf-8 -*-
"""pregate-scope.py -- 缺席断言的「范围声明」判据：**gap == 0**（真口径，覆盖关系而非计数阈值）。

判据（**单一真源**；⛔ 只此一份）
================================
被测对象 = `策划/覆盖矩阵判定.fragment.md`（经 `--inject` 进入 `策划/验收表.md` 的判定行）的
**当下重算**产物（用生成器自带的 `--out-dir=` 沙箱 seam 跑出来，⛔ 真 `策划/**` 与
`tools/probes/coverage-hits.tsv` 0 触碰）。

口径：**每一条含缺席关键词的判定行，都必须带 canonical 形态的范围声明**
    范围={ 根:[…] <sep> 式:[…] <sep> 工具默认:… <sep> 扫描文件数:{合计:N, <根>:n, 读失败:f} <sep> 排除:[…] }
    （`<sep>` = `|` 或 `/`：生成器渲染 markdown 表时会把 `|` 打成 `/` ⇒ 正则必须收两种，
      只认一种会**假红或假绿**，本片两次都踩过）
⇒ 通过条件 = **gap == 0**，gap = 「含缺席关键词、却没有 canonical 范围=」的判定行数。
⛔ **不是** "带 `范围=` 的行数 >= 26" —— 26 是**过期快照**的数（判定行里只有 9 条缺席行），
   按计数阈值落地**必然假红**，而"凑够 26"通常就是造假（规矩：门限用**覆盖关系**、不用计数阈值）。

非空性（防空锁）
================
`gap == 0` 在"一条缺席行都没有"时**恒真** ⇒ 判据会退化成**永不触发的空锁**。
⇒ 本文件在 gap == 0 之外**另加**一条：`absence > 0`，并自带一个**对照**证明该条能红
  （见运行输出里的 `--- 非空性对照 ---`：空片段必须被本文件判红、缺陷片段必须被判出 gap）。

为什么要落成**脚本文件**、⛔ 不是一行式（实测教训，逐字保留）
==========================================================
用 `python -c "…"` 数 `范围=` 得到 **0**，据此差点得出"设计稿的 26 行不可达"这个**假结论**；
换成与它同一工具的干净读法后，**沙箱产物与盘上产物都是 `canonical=9`**、字节数同为 699634
⇒ 那个 0 是**度量错**（嵌套引号/转义），⛔ 不是产物的事。
⇒ 规矩：**凡是"数"都要落成脚本**；一行式 + 嵌套引号的静默错数会被当成产物缺陷。

计数口径（别踩的坑，都实测过）
==============================
* 判定行的证据列里**可以有真的换行符**（多行单元格）⇒ 朴素按行切会把**续行**当成新行，
  报出一条 `(no-numeric-id)` 的**假缺口**（实测：它其实是 3336 那一行的续行）。
  本文件的归块规则：`| <数字> |` 开头 = 新行，其余非空行 = 上一行的续行。
* `范围=` **子串** ≠ canonical 形态：盘上还曾有 9 处**早期形态**（`; ` 分隔、无 `排除:`）；
  生成器落源前 canonical = **0**（这就是"正样本必须等落地产出"的原因）。两个数**分开报**。
* `读失败:N` **不是**"一个根"，⛔ 不参与"各根之和"；`读失败 > 0 ⇒ 直接 FAIL` 这条自洽判据在
  **闸门侧** `bwzr-p3-selftest.py`（两向自检），⛔ 本文件不复制它的规则（规矩 `R-消费不复制`）。

落地说明（2026-09-23 片BW-ZERO-P3：把准备件 `.ai-tmp/test/bwzr-p3-pregate.py` 搬到这里）
=======================================================================================
搬动原因：`.ai-tmp/**` 收尾会清 ⇒ 判据的**单一真源**必须落在 `tools/probes/`。
只改**两处**，⛔ 口径（`SCOPE_RE` / `ABS_KW` / `census` 的归块规则）**一字未改**：
  ① 沙箱目录改到 `<ROOT>/.ai-tmp/test/`：脚本的家在 `tools/probes/`，原地造沙箱会往 `tools/**`
     写一次性产物（skill 3.5：一次性产物只放 `.ai-tmp/test/`）；
  ② 删掉准备件里的 **B 段（"落源后预演"）**：那一段要造"落源后"的生成器副本，依赖同目录的准备件
     `bwzr-p3-apply.py`（⛔ 不随本文件落地）。落源之后，A 段（真生成器 + 沙箱）**就是**"落源后
     产物"⇒ B 段与 A 段是同一件事 ⇒ 删除，不留指向 `.ai-tmp/**` 的死引用。
  ⚠️ 记档：该 B 段在准备件里**只被"落源前"的干跑走过**；而 `bwzr-p3-apply.py` 把本文件放在
     **写盘之后**调用 ⇒ 落源后 `make_rootsim()` 的锚点校验必然 0 命中、打印 `RESULT: FAIL`
     （"只有落源后才可达的路径从未跑过"又一次）。当次回报已如实记入，⛔ **未**改成
     "锚点不匹配也 PASS"（那是放宽判据）。

用法：
    python tools/probes/pregate-scope.py            # 真生成器 -> 沙箱 -> 计数 -> RESULT: PASS/FAIL
退出码：0 = PASS（gap == 0 且 absence > 0）；1 = FAIL。
"""
import hashlib
import io
import os
import re
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ROOT = r'C:\Work\Server\f-v2\clover-project-cs16'
TEST = os.path.join(ROOT, '.ai-tmp', 'test')
GEN = os.path.join(ROOT, 'tools', 'probes', 'enumerate-entities.py')
LEDGER = os.path.join(ROOT, 'tools', 'probes', 'coverage-hits.tsv')
FRAG_DISK = os.path.join(ROOT, '\u7b56\u5212', '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md')
FRAG_NAME = '\u8986\u76d6\u77e9\u9635\u5224\u5b9a.fragment.md'
SB = os.path.join(TEST, 'pregate-scope-sandbox')            # ① 一次性产物只放 .ai-tmp/test/
SB_SELF = os.path.join(TEST, 'pregate-scope-selftest-sbx')  # 非空性对照用（跑完即删）

# 缺席关键词（与闸门侧 `bwzr-p3-selftest.py` 的 `ABSENCE` 同形；⛔ 两处要一起改）
ABS_KW = ('\u5168\u4ed3', '\u96f6\u8c03\u7528', '\u4e0d\u5b58\u5728', '=0 \u547d\u4e2d',
          '\u7f3a\u5e2d', '\u547d\u4e2d 0', '\u547d\u4e2d **0**')
# ⛔ 分隔符必须是 `[|/]`（实测第 6 个缺陷）：生成器渲染 markdown 表时把 `|` 打成 `/`。
SCOPE_RE = re.compile(
    r'\u8303\u56f4=\{ \u6839:\[[^\]]*\]\s*[|/]\s*'
    r'\u5f0f:\[[^\]]*\]\s*[|/]\s*'
    r'\u5de5\u5177\u9ed8\u8ba4:.*?[|/]\s*'
    r'\u626b\u63cf\u6587\u4ef6\u6570:\{[^}]*\}\s*[|/]\s*'
    r'\u6392\u9664:\[[^\]]*\]\s*\}', re.S)


def sha16(p):
    if not os.path.isfile(p):
        return '(missing)'
    return hashlib.sha256(open(p, 'rb').read()).hexdigest().upper()[:16]


def census(frag_path, tag):
    if not os.path.isfile(frag_path):
        print('%s: MISSING %s' % (tag, frag_path))
        return None
    text = io.open(frag_path, encoding='utf-8', errors='replace').read()
    rows = text.split('\n')
    # ⛔ **必须把"多行单元格的续行"归回它所属的那一行**（本片干跑实测的口径错）：
    #   判定行的证据列里有真的换行符（`\n` 落在字符串里）⇒ 朴素按行切会把一条续行当成"一行"
    #   ⇒ 报出一条 `(no-numeric-id)` 的**假缺口**（实测：它其实是 3336 那行的续行）。
    #   判据：`| <数字> |` 开头的 = 新行，其余非空行 = 上一行的续行。
    blocks, cur = [], None
    for r in rows:
        if re.match(r'^\|\s*\d+\s*\|', r):
            cur = [r]
            blocks.append(cur)
        elif cur is not None and r.strip() and not r.startswith('#'):
            cur.append(r)
    scope = [b[0] for b in blocks if any(SCOPE_RE.search(x) for x in b)]
    absent = ['\n'.join(b) for b in blocks if any(any(k in x for k in ABS_KW) for x in b)]
    gap = [b for b in blocks if any(any(k in x for k in ABS_KW) for x in b)
           and not any(SCOPE_RE.search(x) for x in b)]
    ids = [re.match(r'^\|\s*(\d+)\s*\|', b[0]).group(1) for b in gap]
    # ⚠️ 两个数**必须分开报**（本片自己踩过：把"含 `范围=` 子串"当成"canonical 形态"⇒ 得出假结论）：
    #   `范围=` 子串 = 盘上还有 **9** 处**早期形态**（`; ` 分隔、无 `排除:`）⇒ 闸门**不认**；
    #   canonical（`范围={ 根:[…]`）= 落地前 **0** ⇒ 这正好印证"正样本必须等 ③ 产出"。
    print('%s: bytes=%d rows=%d canonical=%d ("\u8303\u56f4=" substring=%d) absence=%d GAP=%d'
          % (tag, os.path.getsize(frag_path), len(rows), len(scope),
             text.count('\u8303\u56f4='), len(absent), len(gap)))
    if ids:
        print('    gap ids = %s' % ', '.join(ids))
    return {'scope': len(scope), 'absent': len(absent), 'gap': len(gap), 'ids': ids,
            'substr': text.count('\u8303\u56f4=')}


def run_gen(sb, tag):
    """真生成器 + `--out-dir=` 沙箱（⛔ 真产物 0 触碰）。"""
    if os.path.isdir(sb):
        shutil.rmtree(sb, ignore_errors=True)
    os.makedirs(sb, exist_ok=True)
    p = subprocess.run([sys.executable, GEN, '--out-dir=' + sb], cwd=ROOT,
                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=1800)
    io.open(os.path.join(sb, 'stdout.txt'), 'w', encoding='utf-8', newline='\n').write(
        p.stdout.decode('utf-8', 'replace'))
    io.open(os.path.join(sb, 'stderr.txt'), 'w', encoding='utf-8', newline='\n').write(
        p.stderr.decode('utf-8', 'replace'))
    print('%s: rc=%d out=%s' % (tag, p.returncode, sb))
    if p.returncode != 0:
        for l in p.stderr.decode('utf-8', 'replace').strip().split('\n')[-6:]:
            print('    ERR| ' + l)
    return p.returncode


# ---- 非空性对照（⛔ 证明 absence>0 这条不是永不触发的空锁）-----------------------------
_VACUOUS_FRAG = ('| id | judgement | evidence |\n|---|---|---|\n'
                 '| 1 | \u706b\u7130\u5b58\u5728 | client/Assets/x.cs:1 |\n')
_DEFECT_FRAG = ('| id | judgement | evidence |\n|---|---|---|\n'
                '| 2 | \u73b0\u5b58 `client/Assets/**` \u5185 `AStar` \u547d\u4e2d 0 | no scope here |\n')


def nonvacuity_check():
    """返回 (ok, lines)。空片段 ⇒ 必须判红（absence==0）；缺陷片段 ⇒ 必须判出 gap>0。"""
    out, ok = [], True
    if os.path.isdir(SB_SELF):
        shutil.rmtree(SB_SELF, ignore_errors=True)
    os.makedirs(SB_SELF, exist_ok=True)
    try:
        for name, txt in (('vacuous(no-absence-row)', _VACUOUS_FRAG),
                          ('defect(absence-row-no-scope)', _DEFECT_FRAG)):
            p = os.path.join(SB_SELF, 'x.md')
            io.open(p, 'w', encoding='utf-8', newline='\n').write(txt)
            c = census(p, 'self-check/' + name)
            if name.startswith('vacuous'):
                good = (c['gap'] == 0 and c['absent'] == 0)     # ⇒ 命中本文件的非空性判据
            else:
                good = (c['gap'] == 1)
            ok = ok and good
            out.append('  %-6s %s -> %s' % ('ok' if good else 'FAIL', name,
                                            'gap=%d absence=%d' % (c['gap'], c['absent'])))
    finally:
        shutil.rmtree(SB_SELF, ignore_errors=True)
    return ok, out


def main():
    print('=== A) 真生成器 + 沙箱输出（当下重算的产物；⛔ 真 策划/** 与 ledger 0 触碰）===')
    print('generator = %s' % GEN)
    print('  bytes=%d sha256[:16]=%s' % (os.path.getsize(GEN), sha16(GEN)))
    print('  落源标记 `def scope_note(` 在位 = %s'
          % ('def scope_note(' in io.open(GEN, encoding='utf-8', errors='replace').read()))
    h0, f0 = sha16(LEDGER), sha16(FRAG_DISK)
    print('before: ledger=%s frag=%s' % (h0, f0))
    rc = run_gen(SB, 'A-run')
    a = census(os.path.join(SB, FRAG_NAME), 'A sandbox(fresh)')
    d = census(FRAG_DISK, 'A on-disk      ')
    h1, f1 = sha16(LEDGER), sha16(FRAG_DISK)
    print('after : ledger=%s frag=%s  (同一次运行内对比 ⇒ 真实产物 0 触碰 = %s)'
          % (h1, f1, (h0 == h1 and f0 == f1)))

    print('--- \u975e\u7a7a\u6027\u5bf9\u7167（防空锁）---')
    nv_ok, nv_lines = nonvacuity_check()
    for l in nv_lines:
        print(l)

    print('=== 结论（真口径）===')
    if rc != 0 or a is None or d is None:
        print('RESULT: FAIL (generator rc=%s / census incomplete)' % rc)
        return 1
    print('canonical 范围= 行：fresh 沙箱 %d / 盘上 %d ；含缺席关键词的判定行 = %d'
          % (a['scope'], d['scope'], a['absent']))
    if a['gap'] != d['gap'] or a['scope'] != d['scope']:
        print('NOTE 盘上与"当下重算"不一致（fresh scope=%d gap=%d vs disk scope=%d gap=%d）——'
              '本判据只判 fresh 产物；盘上漂移由 `tools/probes/section-ownership.py --check --rebuild` 把守'
              % (a['scope'], a['gap'], d['scope'], d['gap']))
    ok = (a['gap'] == 0) and (a['absent'] > 0) and nv_ok
    print('口径 = 【每一条含缺席关键词的判定行都必须带 canonical 范围=】= gap == 0，'
          '且缺席行数 > 0（否则锁是空的：没有缺席行时 gap==0 恒真）')
    print('RESULT: %s' % ('PASS' if ok else 'FAIL'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
