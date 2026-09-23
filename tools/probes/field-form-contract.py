# -*- coding: utf-8 -*-
"""field-form-contract.py -- 卡 #3 的【字段形态契约】判据资产：**消费者**（⛔ 不复制规则）。

落点名 / 为什么（⛔ 这段是交付物的一部分，落地时照抄进目标文件头）：
    落点 = `tools/probes/field-form-contract.py`（2026-09-23 已落）
    为什么：
      ① 它是**判据资产**（不是驱动、不是一次性探针）⇒ 按 skill §3 第 5 条落 `tools/probes/`；
      ② 名字⛔ **不含 `hits`** —— `verify.ps1` item 40 的 carrier 条件里"文件名含 hits"是**唯一承重的一层**
         （规矩 `R-单点防线`）⇒ 名字带 hits 会被误当 ledger 载体；
      ③ `field-form-contract` = 卡 #3 的标题"`unresolved` 字段兼职两种语义"的**契约面**命名，
         ⛔ 不带"card3/待修"这类会过期的字眼（卡 #3 的性质已从"待修项"改成"未来场景前置卡"）。

⛔ **不重复判据**（规矩 `R-消费不复制` / "判据只能有一个来源"）：
    本轮**实测**：`tools/probes/audit-verdict-rows.py` **已经实现**了卡 #3 的核心规则 ——
      * L88  `ANCHOR_NONE = '--'  # the only legal anchor for an unresolved=1 line`
      * L240 `classify_anchor(ev)`（锚点形态分类：line / meta_line / file_bytes / bytes / `--` …）
      * L287-328 `hit_quality(dim, line)`：`unresolved = re.search(...)`；`lying = unresolved and anchor != '--'`
      * L510-528 / L550-552 汇总并打印 `unresolved-lying` / `unresolved-only`
    ⇒ 本资产**只做消费者**：用 `importlib` 载入它、**直接调它的 `hit_quality`**，
      ⛔ 绝不在这里重写一份 `unresolved` 规则（重写 = 判据各自漂移）。

卡 #3 的四条验收判据（逐字自 `.ai-tmp/test/bwe-todo-card-03.md` §5）→ 本资产的可跑断言：
    1. 正样本：全新盘 + 全新源码跑 `--inject` 两次 ⇒ 六件产物 + ledger **逐字节相同**（幂等）
       ⇒ `--idempotence`（默认**不跑**：它要跑两遍生成器；需要时显式给）
    2. 负样本：喂一个"**有锚点却写 `unresolved=1`**"的 ledger 样本 ⇒ 判据必须 **FAIL**
       ⇒ `--selftest` 的内联反样本（走**真** `hit_quality`）
    3. 覆盖数：必须打印**各形态的计数**（`line= / meta_line= / file_bytes= / bytes= / unresolved=`）
       与**非 `--` 的 `unresolved` 行数 = 0** ⇒ 默认模式即打印
    4. 现行规则：**`unresolved=1` 只许出现在 `--` 行** ⇒ `lying == 0` 才算 PASS

用法（落地后）：
    python tools/probes/field-form-contract.py                 # 查真 ledger（只读、只打印）
    python tools/probes/field-form-contract.py --selftest      # 两向自检（正/反样本）
    python tools/probes/field-form-contract.py --crit           # 回显真判据自己的形态计数
    python tools/probes/field-form-contract.py --strip-provs <file>   # 剥掉 `# |prov| ` 来源块
⛔ `--idempotence` **未实现**：`main()` 里没有该分支 ⇒ 传它会**静默落进默认模式**（做的是另一件事、
   不报错）。出处 = 2026-09-23 落地时现取 `main()` 的分支表。**待办**，见落地回报。
"""
import importlib.util
import io
import os
import re
import sys

sys.dont_write_bytecode = True
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

ROOT = r'C:\Work\Server\f-v2\clover-project-cs16'
CRIT = os.path.join(ROOT, 'tools', 'probes', 'audit-verdict-rows.py')
LEDGER = os.path.join(ROOT, 'tools', 'probes', 'coverage-hits.tsv')
GEN = os.path.join(ROOT, 'tools', 'probes', 'enumerate-entities.py')
PROV_MARK = '# |prov| '

# ⛔ 只用"能重算的东西"表达标识（规矩 `R-归档不用易失标识`）：以下是**跑本文件时的观测**，
#    凡报给别人的数都带观测时刻；哈希只作**脚注**、⛔ 不当"现行版本"标识。
#    最近一次刷新 = 2026-09-23 15:50（片BW-ZERO-P3 落 `scope_note()` + 删本文件三个准备模式之后**现取**）；
#    ⛔ 上一版记的 `enumerate-entities.py 317745B / 219F93EEAF2F6EB3 / _sha1_8 @2706` 已被本片改动取代。
OBSERVED = {
    'coverage-hits.tsv sha256[:16]': '65A99757D5F0309B',
    'coverage-hits.tsv bytes': 330405,
    'coverage-hits.tsv mtime': '2026-09-23 14:34:40',
    'enumerate-entities.py sha256[:16]': '86769CC4236F2D9D',   # 落在 scope_note 之后现取
    'enumerate-entities.py bytes': 327660,
    'def _sha1_8() 所在行': 2864,   # 嵌套 def（缩进 4 空格）；落源后由 2706 位移至此
    '旧记（已失效，⛔ 不得当现行）': '92772B812F08818E / 5DEB1688 / DAA2C032',
}

ANCHOR_NONE = '--'
LINE_RE = re.compile(r'^(\d+)\t([^\t]*)\t(.*)$')

# --- 内联反样本（自足：⛔ 不读任何 .ai-tmp 产物）--------------------------------------
#   NEGATIVE = "有锚点却写 unresolved=1" —— 卡 #3 §2.2 的**真实**缺陷形态（在途版本 70 行），
#   这里逐字复刻其中一行（F3/F4 "裸文件名" 形态）。
#   ⚠️ 形态必须与**真 ledger** 同形：`<row id>\t<锚点字段>\t<实测字段...>`（TAB 分隔）——
#   实测教训：本草案第一版把 fixture 写成"空格分隔、无 row id"，`hit_quality` 的
#   `line.split('\t')[1:]` 取到空 ⇒ 反样本**没打到判据**（`lying=False`）⇒ 自检当场判 FAIL。
#   这正是卡 #3 §5 第 2 条要防的"永不触发的阈值"形态，留作注释。
FIXTURE_LYING = ('1891\tF1:client/Assets/Scripts/Core/CsConst.cs:113\t'
                 'probe=enumerate-entities(static) measured=bytes=0 lines=0 unresolved=1')
#   （`--` 行的实测字段必须让 `unresolved=1` 的**前一个字符不是 `=`**，否则正则
#    `(?<![\w=])` 直接不认它 ⇒ 该正样本会退化成 `why='ok'` 的"没打到判据"形态。）
FIXTURE_OK_DASH = '3176\t--\tprobe=none measured=-- unresolved=1'
FIXTURE_OK_HIT = ('1\tF2:guid=0123456789abcdef0123456789abcdef\t'
                  'probe=enumerate-entities(static) measured=meta_line=1180')


def load_crit():
    """按 `R-消费不复制`：importlib 载真判据，⛔ 不复制它的规则。"""
    spec = importlib.util.spec_from_file_location('audit_verdict_rows', CRIT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def check_ledger(crit, path):
    """查一个 ledger；返回 (ok, lines, counts)。只读、只打印。"""
    out, counts = [], {}
    lying, unres_non_dash, rows = [], [], 0
    for ln in io.open(path, encoding='utf-8', errors='replace').read().split('\n'):
        if not ln.strip() or ln.startswith('#'):
            continue
        m = LINE_RE.match(ln)
        if not m:
            out.append('UNPARSEABLE: %s' % ln[:120])
            continue
        rows += 1
        rid, anchor, rest = m.group(1), m.group(2), m.group(3)
        # 形态普查 = **纯句法**（取锚点字段的前缀），⛔ 不用 `classify_anchor` 重算形态。
        #   为什么（本草案实测）：`classify_anchor(ev)` 的入参是**整行证据串**（它内部先
        #   `PATHLINE_RE` 找 `路径:行`、再 GUID、再 bare path，并用 `resolve()` 判可达），
        #   ⛔ 不是"锚点字段"。第一版拿锚点字段喂它 ⇒ 得到 `F1=3 / F2=1180 / F4=2612`，
        #   与卡 #3 §0 的 `F1=1002 / F2=1180 / F4=1089 / F3=524` **不一致** —— 那是**输入契约用错**，
        #   不是判据变了。⇒ **权威的各形态计数只由真判据 `audit-verdict-rows.py` 自己打印**
        #   （本资产用 `--crit` 去调它并回显，⛔ 不在这里造第二份）。
        form = anchor if anchor == ANCHOR_NONE else (
            anchor.split(':', 1)[0] if ':' in anchor else '(bare)')
        counts[form] = counts.get(form, 0) + 1
        ok, why, is_lying = crit.hit_quality('?', ln)
        if is_lying:
            lying.append(rid)
        if why == 'unresolved' and anchor != ANCHOR_NONE:
            unres_non_dash.append(rid)
    out.append('rows total = %d' % rows)
    out.append('anchor-field prefix census (pure syntax) = %s'
               % ', '.join('%s=%d' % kv for kv in sorted(counts.items(), key=lambda kv: str(kv[0]))))
    out.append('non-dash unresolved rows = %d  %s'
               % (len(unres_non_dash), unres_non_dash[:20]))
    out.append('unresolved-lying = %d  %s' % (len(lying), lying[:20]))
    ok = (not lying) and (not unres_non_dash)
    out.append('VERDICT: %s (%s)' % ('PASS' if ok else 'FAIL',
                                     'unresolved=1 only on `--` rows' if ok else 'rule violated'))
    return ok, out, counts


def selftest(crit):
    bad = []
    print('--- 正样本（必须 PASS）---')
    # `want_why` = 该正样本**必须**走到的判据分支；对不上 ⇒ 判 FAIL（防线：防"静默测零"，
    #   同族 = `audit-gate` 终局自检 `unmet=1`：样本没生效与规则不会红是两件事）。
    for name, fixture, want_why in (('P_real-ledger', None, None),
                                    ('P_legal-dash', FIXTURE_OK_DASH, 'unresolved'),
                                    ('P_legal-hit', FIXTURE_OK_HIT, 'ok')):
        if fixture is None:
            ok, lines, _c = check_ledger(crit, LEDGER)
            for l in lines:
                print('    %s' % l)
            print('  %-14s %s' % (name, lines[-1]))
            if not ok:
                bad.append(name + ': ' + lines[-1])
            continue
        _ok, why, lying = crit.hit_quality('?', fixture)
        good = (not lying) and (why == want_why)
        print('  %-14s %s  (why=%s want=%s lying=%s)'
              % ('ok' if good else 'FAIL', name, why, want_why, lying))
        if not good:
            bad.append('%s: why=%r want=%r lying=%s' % (name, why, want_why, lying))
    print('--- 反样本（必须 FAIL）---')
    for name, fixture in (('N_real-lying (卡#3 §2.2 真实形态)', FIXTURE_LYING),):
        _ok, why, lying = crit.hit_quality('?', fixture)
        good = bool(lying)
        print('  %-14s %s  (why=%s lying=%s)' % ('ok' if good else 'FAIL', name, why, lying))
        if not good:
            bad.append('%s: 真判据未判出 lying ⇒ 该反样本测了零个东西' % name)
    # 反样本 2（构造）：把 lying 样本塞进一份**内存 ledger**，走 check_ledger 的**汇总**口径
    tmp = os.path.join(os.path.dirname(os.path.abspath(__file__)), '_bwzr-card3-neg.tmp')
    io.open(tmp, 'w', encoding='utf-8', newline='\n').write(
        '# probe-hits plan=%s\n%s\n%s\n' % (ROOT + r'\策划', FIXTURE_LYING, FIXTURE_OK_DASH))
    ok, lines, _c = check_ledger(crit, tmp)
    os.remove(tmp)
    print('  N2 synthetic ledger  -> %s' % lines[-1])
    if ok:
        bad.append('N2 合成 ledger 竟 PASS（未判出 lying）')
    print('--- 口径 ---')
    print('# fixture 形态\t走**真** `audit-verdict-rows.py: hit_quality`（importlib 载入，⛔ 不复制规则）')
    print('# 未匹配/被丢弃样本数\t0')
    print('RESULT: %s (%d failure(s))' % ('PASS' if not bad else 'FAIL', len(bad)))
    for b in bad:
        print('   ' + b)
    return 1 if bad else 0


def strip_provs_text(text):
    kept = [l for l in text.split('\n') if not l.startswith(PROV_MARK)]
    return ('\n'.join(kept)).rstrip('\n') + '\n'


def main():
    if '--strip-provs' in sys.argv:
        p = sys.argv[sys.argv.index('--strip-provs') + 1]
        code = strip_provs_text(io.open(p, encoding='utf-8', errors='replace',
                                        newline='').read())
        io.open(p, 'w', encoding='utf-8', newline='\n').write(code)
        print('STRIPPED %s -> %d chars (prov block removed)' % (p, len(code)))
        return 0

    crit = load_crit()
    if '--selftest' in sys.argv:
        return selftest(crit)
    if '--crit' in sys.argv:
        # 权威的各形态计数 / unresolved-lying **只由真判据自己打印**（⛔ 本资产不造第二份）
        import subprocess
        p = subprocess.run([sys.executable, CRIT], cwd=ROOT, stdout=subprocess.PIPE,
                           stderr=subprocess.STDOUT, timeout=1800)
        keys = ('unresolved', 'file_bytes=', 'meta_line=', 'line=', 'bytes=', 'form')
        for l in p.stdout.decode('utf-8', 'replace').split('\n'):
            if any(k in l for k in keys):
                print('CRIT| ' + l.rstrip())
        return 0 if p.returncode == 0 else 1
    ok, lines, _c = check_ledger(crit, LEDGER)
    for l in lines:
        print(l)
    print('# 权威形态计数：`python tools/probes/field-form-contract.py --crit`'
          '（= 回显真判据 audit-verdict-rows.py 自己的打印；⛔ 本节上面的 census 是纯句法普查）')
    for k in sorted(OBSERVED):
        print('# observed %-38s : %s' % (k, OBSERVED[k]))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())

# |prov| 来源块：前置条件卡 #3（`unresolved` 字段兼职两种语义）
# |prov| 源 = `.ai-tmp/test/bwe-todo-card-03.md`
# |prov|   bytes=8796  sha256[:16]=899A58D753601A36  mtime=2026-09-23 14:19:20
# |prov|   已与 899A58D7 版（≈14:1x）核对，差异仅 L65（§7 同步，不涉数字）
# |prov| 形态 = 整块 `# |prov| ` 前缀 ⇒ 可机械剥离；剥离后必须与本文件**代码部分逐字节相同**
# |prov| 剥离入口 = `python tools/probes/field-form-contract.py --strip-provs <file>`
# |prov| ⛔ 本块是**来源/依据**，不是可执行代码；⛔ 不改判据、不参与运行
# |prov| 剥离校验（放在落地后的资产里，或直接跑命令）：
# |prov|   python tools/probes/field-form-contract.py --strip-provs tools/probes/field-form-contract.py
# |prov|   python .ai-tmp/test/bwzr-card3-asset-draft.py --strip-check <副本>   # 本轮用的预演命令
# |prov|   ⇒ 断言 = strip_provs_text(landed) == strip_provs_text(code_only)，必须逐字节相同；
# |prov|   ⇒ ⛔ 必须**先落盘再读回**再校验（⛔ 不许只校验内存里那份）。本轮实测 = IDENTICAL。
# |prov| ---- 以下为卡 #3 原文（逐行前缀化，一字未改）----
# |prov| **本卡不是当前缺陷（现行 `unresolved` 非 `--` = 0）；它是「重启字段3 升级」时的前置条件。**
# |prov| > **性质变更（team-lead 裁决）**：由"待修项卡"改为**前置条件卡** —— ① 在途版本证据、② 四条验收判据（尤其负样本）、③ 归一化定义，三样**全部保留**；只把「是否当前缺陷」这一问按实测收口（= 不成立）。
# |prov| # 前置条件卡 #3（原「待修项卡 #3」）：`unresolved` 字段兼职两种语义
# |prov| > ⛔ **先读第 0 节** —— 我在回报里说过"现在这版盘上产物有同类缺陷"，**那句是推断、不是实测，我撤回**。
# |prov| > 本文件在 `.ai-tmp/test/`（`.gitignore:4`）⇒ **收尾会清**；**权威副本在回报正文**（`bw-m` 固化时请照抄，并**保留第 7 节归属标注**）。
# |prov| 
# |prov| ## 0 结论（先给判定，再给证据）
# |prov| | 版本 | 该缺陷是否成立 | 实测 |
# |prov| |---|---|---|
# |prov| | **现行盘上版本**（`tools/probes/coverage-hits.tsv`，2026-09-23 复测） | ❌ **不成立** | 生成器侧：`unresolved rows = 5 ; by anchor form = {'--': 5} ; 非 '--' = 0`；**闸门侧**（新落的那版机检，`audit-verdict-rows.py`）：`unresolved-lying = 0` ⇒ **两侧同判** |
# |prov| | 我在途的"字段3 升级"版本（P1–P4 打上、**已撤回**；⚠️ 盘上正式版**已落盘且不撒谎**，单列见下） | ✅ 成立 | `unresolved rows = 75`（其中 70 非 `--`），行号清单见下 |
# |prov| | **盘上正式版实现**（`tools/probes/enumerate-entities.py:2663-2833`，`_sha1_8` @2705） | ❌ **不成立** | 真产物 3800 行实测：形态 `F1=1002 F2=1180 F4=1089 F3=524 '--'=5`；摘要位数 **8 hex × 3795 行（无一行 16 位）**；**撒谎行 = 0**；`unresolved=1` 共 5 行 ⇒ **全部为 `--`** |
# |prov| ⇒ **处置建议**：**本案不必单开一片**。要保留的是**规则**（一个字段一个含义）+ **两向自证要求**；只有当"字段3 升级"重新启动时才需要一并做"口径对齐"。
# |prov| > ⚠️ **哈希会失 ⇒ 本卡不引用哈希**：归档时记的 `coverage-hits.tsv DAA2C032…`，2026-09-23 复测已是 `5DEB1688…`（ledger 每被任一执行片重注入一次就整体变）⇒ 拿产物哈希当"现行版本"标识 = **指向会消失的东西**（与 #82 同一形态）。
# |prov| > 本卡只依托两样**不随重注入失效**的东西：**行 id 清单**（= 判定表行号）+ **两侧度量**。（前提：判定表行序不变；plan 变了就要重测 ⇒ 这不是"永久的锚"，是"可复算的锚"。）
# |prov| 
# |prov| ## 1 现象
# |prov| `unresolved=1` 这一个取值**同时**被用来表示两件不同的事：
# |prov| ① 该行**确实没有**可复核物（`--` 行）；② 探针**算不出**锚点的摘要（实现未覆盖该形态）。
# |prov| ⇒ 后者的输出**不报错**，只会让"锚点明明存在、摘要算不出"的行**静默说成"没找到"**。
# |prov| 
# |prov| ## 2 实测证据（可单独复核）
# |prov| **2.1 现行版本（盘上真产物）—— 缺陷不成立**
# |prov| ```
# |prov| $ python -c "...读 tools/probes/coverage-hits.tsv..."
# |prov| rows total = 3800
# |prov| unresolved rows = 5
# |prov| by anchor form = {'--': 5}
# |prov| NON-dash (illegitimate) count = 0
# |prov| dash (legit) ids = 3176,3268,3302,3373,3753      ← 恰是 5 行 CROSS（允许差异 #87）
# |prov| ```
# |prov| **2.2 在途版本（已撤回）—— 缺陷成立，行号清单（75 行）**
# |prov| ```
# |prov| 非 --（= 不应写 unresolved）共 70 行：
# |prov| 1891,1892,3298,3299,3300,3378,3379,3380,3381,3382,3383,3384,3385,3386,3387,3388,3389,3390,3391,
# |prov| 3392,3393,3394,3395,3396,3397,3398,3399,3400,3754,3755,3756,3757,3758,3759,3760,3761,3762,3763,
# |prov| 3764,3765,3766,3767,3768,3769,3770,3771,3772,3773,3774,3775,3776,3777,3778,3779,3780,3781,3782,
# |prov| 3783,3784,3785,3791,3792,3793,3794,3795,3796,3797,3798,3799,3800
# |prov| --（= 合法）5 行：3176,3268,3302,3373,3753
# |prov| ```
# |prov| 来源：`bwe-patch3.py`（`.ai-tmp/test/bwe-patch3.py`，2026-09-23 复测仍在盘）+ 其沙箱 stdout（`.ai-tmp/test/bwe-patch3-out.txt`，同）⇒ 现均可复核。⚠️ 但两者都在 `.ai-tmp/**`（收尾必清）⇒ **行号清单的权威副本 = 回报正文**。
# |prov| 
# |prov| ## 3 root cause（**只在做"字段3 升级"时才会撞上**）
# |prov| 三种形态在"生成器"与"判据资产"两侧的**解析口径不同**：
# |prov| 1. **F2**：生成器 `GUID2PATH` 只覆盖 `ASSET_ROOT_LIST + Assets/Editor`；判据资产 `guid_index()` 走**全 `client/**`**（排除 Library/Temp/obj/bin）⇒ 索引域不等；
# |prov| 2. **F1**：token 可能是**裸文件名**（判据资产用 `BASENAME_ROOTS` 解析成功，按 `ROOT` 相对拼路径则失败）；
# |prov| 3. **F3/F4**：token 形态与 `_resolve_any()`（`_AV.resolve(_AV.norm_path(tok))`）存在差异。
# |prov| ⇒ 现行版本之所以没暴露：它对 F2 走 `GUID2PATH`、对 F1/F3/F4 走 `ROOT` 相对 `os.path.getsize`，**两条都成功**，所以只写 `bytes=/lines=`（真实测量），不会落到 `unresolved`。
# |prov| 
# |prov| ## 4 修法（若重启字段3 升级）
# |prov| - **方案 β（团队已裁）**：字段3 按形态语义唯一 —— F1 → `line=<n> hash=<该行归一化 sha1[:8]>`；F2 → `meta_line=<n> hash=<…>`；F3/F4 → `file_bytes=<n> sha1=<原始字节 sha1[:8]>`；**只有 `--` 行写 `unresolved=1`**。
# |prov| - **前置**：先做"口径对齐"（至少把 F2 的索引域与判据资产对齐，或明确"F2 摘要拿不到时写另一形态而非 unresolved"）。
# |prov| - **归一化定义**（必须逐字写进注释，否则"同盘同源逐字节相同"不可被第三方复算）：`取该行不含行终止符的原文 → strip() → UTF-8 → sha1().hexdigest()[:8]（⛔ **位数的唯一来源** = `tools/probes/enumerate-entities.py:2705` 的 `_sha1_8()`；它是**实现事实**，**不是"团队裁定"** —— 消费方照抄/复用这一行，⛔ 不许按记忆写。本片曾把归档用的 **`sha256[:16]`** 串到 sha1 语境上，已在 §7 记为待核/已更正）`。
# |prov| 
# |prov| ## 5 验收判据（**两向自证**，缺一不可）
# |prov| 1. **正样本**：全新盘 + 全新源码跑 `--inject` 两次 ⇒ 六件产物 + ledger **逐字节相同**（幂等）；
# |prov| 2. **负样本**：喂一个"**有锚点却写 `unresolved=1`**"的 ledger 样本 ⇒ 判据必须 **FAIL**（⛔ 否则这条机检就是"永不触发的阈值"，等于 TABLE-ECHO v1）；
# |prov| 3. **覆盖数**：输出必须打印**各形态的计数**（`line= / meta_line= / file_bytes= / bytes= / unresolved=`）与**非 `--` 的 `unresolved` 行数 = 0**；
# |prov| 4. 现行规则：**`unresolved=1` 只许出现在 `--` 行**。
# |prov| 
# |prov| ## 6 一般化结论
# |prov| **一个判据字段只能有一个含义**；需要表达 N 种情况就用 N 个明确形态。兼职字段的错**不报错**，只让某类行静默说假话 —— 而"静默"意味着任何靠"看有没有报错"的把关都抓不到它。
# |prov| 
# |prov| ## 7 归属标注（固化时必须一起保留）
# |prov| - **本片实测**：§0 两侧度量、§2.1 现行版度量、§2.2 在途版行号清单、§3 root cause（F1/F2/F3/F4 由**读两侧源码**得出，**未**逐行跑"形态对齐"验证）、§4 归一化定义中的**操作序列**（`[:8]` 的位数 = 实现事实，来源 `enumerate-entities.py:2705` 的 `_sha1_8()`）。
# |prov| - **转述、未复核**：`TABLE-ECHO v1` 那个"永不触发的阈值"一例（来自 `slice BB`，本片未复核其原始证据）；`sha1[:16]` 这个写法（= 我把归档用的 sha256[:16] 串到了 sha1 语境；见本节末"收口"条，**以 `[:8]` 为准**）。
# |prov| - **非本片产出**：§4 方案 β 的形态划分（F1→`line=` / F2→`meta_line=` / F3F4→`file_bytes=`）= **团队已裁**，本片仅记录。
# |prov| - **收口（"同一件事两个数"的正解）**：`sha1[:8]` vs `[:16]` —— 盘上**唯一**实现 = `enumerate-entities.py:2705` `_sha1_8()`，用 **`[:8]`**；真产物 **3795 行摘要全为 8 hex、0 行 16 位**；`[:16]` 的真实来源 = **sha256 语境**（我的归档/报告写 `sha256[:16]`；`tools/probes/overview-window.py:166`、`make-check-glyph.py:137`、`render-cs16anim-frame.py:461` 三处 `[:16]` **全是 sha256**）⇒ **两个位数属两个不同算法，本就不是同一件事**；"[:8] 是团队裁的位数"这个表述**无盘上出处，撤回**。
# |prov| 
# |prov| ## 8 复测记录（2026-09-23，写本卡时顺手做的第三项）
# |prov| | 项 | 归档时（team-lead §③） | 复测当下 |
# |prov| |---|---|---|
# |prov| | `coverage-hits.tsv` sha256[:8] | `DAA2C032` | `5DEB1688` |
# |prov| | `evidence anchors` | 未复测 | `3795/3800` |
# |prov| | `probe hits` | 未复测 | `3671/3800`（`hit carriers = 1`） |
# |prov| | `unresolved-lying` | 未复测 | **`0`** |
# |prov| | 其它闸门指标 | — | `echo-only rows 0` / `behaviour-no-probe 124` |
# |prov| ⇒ 两点：① **`unresolved-lying = 0` 是"闸门侧"对新本卡结论的独立确认**（不依赖我的度量 ⇒ 比我自测更硬）；② **归档哈希已失效** ⇒ 若别处拿 `DAA2C032…` 当"现行版本"用 = **过期引用**，需一并更正。
