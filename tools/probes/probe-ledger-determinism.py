# -*- coding: utf-8 -*-
r"""判据资产｜hit ledger 的**逐行确定性**（"同一份盘连跑两次 ⇒ 逐行相同"，比整文件哈希更细）。

来源：片BW-E-R 的 `.ai-tmp/test/bwer-p4-det.py`（2026-09-23 team-lead 裁定搬入 `tools/probes/`），
并**去掉对 `bwer-p4-run.py` / `bwer-p4-patched.py` 的依赖** —— 那两个是**一次性补丁现场**
（补丁版生成器全文 + 跑补丁版的 runner），⛔ 判据不许依赖它们。现在直接跑**真生成器**。

判据：
  ① **正样本（端到端）**：用真生成器 `tools/probes/enumerate-entities.py` 跑**两遍**，分别落
     **两个独立沙箱**，两遍 ledger 的**数据行逐行相同**（行级 ⇒ 能指出"哪一行不稳"）。
  ② **负样本（纯内存）**：进程内构造两组行列表、第二组故意改 2 行 ⇒ 比较核心**必须恰好报出**
     那 2 行、且**不得判 PASS**（否则它可能就是"恒 True 的空判据"）。
     ⛔ 纯内存（无文件形态）：被 kill 的运行不会走 `finally`，"建→查→删"会留残骸；
     ⛔ 只有"真需要喂给外部进程"时才允许文件形态。

⛔ 沙箱是**不可免的**（生成器必须写产物才能被比）：用**固定前缀** + **开跑前先清上一轮残留** +
   跑完自清，且**判据本身不依赖沙箱内容**（只比两遍的数据行）。

本判据的**真实反例（双锚记录，逐字保留；team-lead 2026-09-23 裁定）**：
  ① 那一行的原文（A/B 两次运行，逐字）：
       A: 3790 <TAB> F4:client/Logs/Editor.log <TAB> file_bytes=187955794 file_sha1=074b64d5
       B: 3790 <TAB> F4:client/Logs/Editor.log <TAB> file_bytes=187956442 file_sha1=60ddd6e7
     判据输出原文：differing rows = 1
  ② 命令（当时的原路径，现已改名/搬移）：`python .ai-tmp/test/bwer-p4-det.py`（cwd = 项目根；
     它把 P4-v2 补丁版生成器源码跑两遍、分别落两个独立沙盒，再逐行 diff 两边 ledger 的数据行）
  ③ 时间：2026-09-23 ≈11:14:0x–11:16（第一跑耗时 97 s）；硬证据（下界）= 该脚本创建于 11:14:01，
     修复后复跑落盘 11:18:48，心跳 11:22:36 记 "det rows diff=0"；⚠️ 精确到秒**不可复原**。
  ④ **原产物已被后续运行覆盖**：现存 `.ai-tmp/test/bwer-p4-det-out.txt` 仅 82 字节
     （内容 = "rows: 3800 vs 3800 / differing rows = 0"），原始那份含两行各 ~150 字符的 A/B 明细
     ⇒ 只此一处、**不可复原** ⇒ 故本文件把 ①~③ 逐字写在这里。
  ⑤ **永久侧锚点**（⇒ 后人在 `.ai-tmp/**` 清空后照样能核）：同一组数值 + 由它导出的修复设计都在
     `tools/probes/enumerate-entities.py`（**永久文件**）：
       L2686  `#   \`file_bytes\` = 187955794 → 187956442（\`file_sha1\` 也变）⇒ 产物逐次变 ⇒ 幂等判据当场归零。`
       L2750  同值（引用 `client/Logs/Editor.log`）
       同段还有 `LIVE_ROOTS = ('client/Logs/',)` / `LIVE_HEAD = 8192` / `return 'head_bytes=%d head_sha1=%s' …`
     ⚠️ `L2686 / L2750` 是**永久侧锚点**；若将来该文件行号变动，请按**同段注释文本**检索，⛔ 不要只认行号。
  ⇒ 该反例因此是**双锚**：本文件文字 + 永久文件注释 ⇒ 不依赖任何 `.ai-tmp` 产物。
"""
import hashlib
import io
import os
import shutil
import subprocess
import sys

sys.dont_write_bytecode = True
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEN = os.path.join(ROOT, 'tools', 'probes', 'enumerate-entities.py')
T = os.path.join(ROOT, '.ai-tmp', 'test')
PLAN = os.path.join(ROOT, '\u7b56\u5212')
ACC = os.path.join(PLAN, '\u9a8c\u6536\u8868.md')
PREFIX = 'probe-ledger-determinism-'

fail = []


def diff_rows(a, b):
    n = min(len(a), len(b))
    d = [(i + 1, a[i], b[i]) for i in range(n) if a[i] != b[i]]
    if len(a) != len(b):
        d.append(('len', len(a), len(b)))
    return d


# ---- ② 负样本（纯内存，先跑：毫秒级、零文件） --------------------------------------
_sa = ['1\tF1:client/Assets/Scripts/Core/ResPaths.cs:187\tline=187 hash=1db8a15f',
       '2\t--\tunresolved=1',
       '3\tF4:tools/probes/bstairs-walkline.txt\tfile_bytes=178637 file_sha1=62d4cd9d',
       '4\tF2:guid=c205e4f01e9c95b4ba5b632a9a5d6fef\tmeta_line=2 hash=016f4355']
_sb = list(_sa)
_sb[2] = _sb[2].replace('178637', '178638')
_sb[3] = _sb[3].replace('016f4355', '016f4356')
_neg = diff_rows(_sa, _sb)
_rows_neg = [x[0] for x in _neg]
print('NEGATIVE (in-memory: 2 rows altered) -> reported rows = %s ; expected [3, 4] ; %s'
      % (_rows_neg, 'OK' if _rows_neg == [3, 4] else '*** BROKEN ***'))
if _rows_neg != [3, 4]:
    fail.append('negative sample not detected (comparator may be a constant TRUE)')
# 反向也验：两组**完全相同**时必须报 0 行
if diff_rows(_sa, list(_sa)) != []:
    fail.append('identical lists reported as different')

# ---- ① 正样本（端到端：真生成器两遍 → 两个独立沙箱） --------------------------------
def run(tag):
    sbx = os.path.join(T, PREFIX + tag)
    shutil.rmtree(sbx, ignore_errors=True)      # 开跑前先清上一轮残留（被 kill 的run不会走 finally）
    os.makedirs(sbx)
    cp = os.path.join(sbx, '\u9a8c\u6536\u8868.md')
    shutil.copy2(ACC, cp)
    r = subprocess.run([sys.executable, GEN, '--out-dir=' + sbx, '--inject', '--spec-path=' + cp],
                       cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=900)
    assert r.returncode == 0, r.stderr.decode('utf-8', 'replace')[-600:]
    p = os.path.join(sbx, 'coverage-hits.tsv')
    print('  run[%s] sbx=%s ledger sha256=%s'
          % (tag, os.path.relpath(sbx, ROOT).replace('\\', '/'),
             hashlib.sha256(io.open(p, 'rb').read()).hexdigest().upper()))
    return io.open(p, encoding='utf-8').read().rstrip('\n').split('\n')[1:]


_a = run('a')
_b = run('b')
_pos = diff_rows(_a, _b)
print('POSITIVE (end-to-end: real generator twice, data rows) -> rows=%d/%d ; differing rows = %d %s'
      % (len(_a), len(_b), len(_pos), [x[0] for x in _pos][:10]))
for i, x, y in _pos[:6]:
    print('    row %s\n      A: %s\n      B: %s' % (i, str(x)[:120], str(y)[:120]))
if _pos:
    fail.append('ledger NOT row-by-row identical across two runs: %s' % [x[0] for x in _pos][:20])
if len(_a) != len(_b):
    fail.append('row count differs: %d vs %d' % (len(_a), len(_b)))

for tag in ('a', 'b'):
    shutil.rmtree(os.path.join(T, PREFIX + tag), ignore_errors=True)    # 跑完自清
print('PROBE-LEDGER-DETERMINISM RESULT: %s' % ('PASS' if not fail else 'FAIL ' + ' | '.join(fail)))
sys.exit(0 if not fail else 1)
