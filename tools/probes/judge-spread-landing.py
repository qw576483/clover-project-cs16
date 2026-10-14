# 差异 #94 / A3 落值的离线判据（长期件：已从 .ai-tmp 提升入库到 tools/probes/）。
#   判据 1（硬正控）：落码前快照里**每一行** Set/SetInv/SetGround 必须仍在源码里逐字出现
#                     ⇒ 既有 16 把的值未被改动。
#   判据 2：本轮新落的狙击 / USP / Glock18 逐档值 == 原版实参（原版实参来自 report-feel2.md §A3.6 的
#           逐站点反汇编：`fld [const]` / `fldz` / `fmul qword [const]` + `fcomp` 阈值）。
#   判据 3：形态与阈值字段（AWP 140+10 / Scout 170 / G3SG1 0 / USP·Glock18 0）逐格相符。
#   判据 4：SG550 按登记**未落值**（它的 acc 模型还没补，落值会 10x 偏差）。
#
# 复检所需最小材料（本判据**不需要**编辑器，也**不需要** .ai-tmp 里的任何东西）：
#   本件 + feel2-spread-values-before.txt（正控数据 = 落码前的值快照，与本件同目录）
#   + client/Assets/Scripts/Core/CsWeapons.cs（被测源码）。
# 用法：py -3 tools/probes/judge-spread-landing.py  （Windows 控制台先 set PYTHONIOENCODING=utf-8）
#   只读：不写任何文件；exit 0 = 全过，exit 1 = 有 FAIL。
import io
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# tools/probes -> tools -> repo root
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC = os.path.join(ROOT, "client", "Assets", "Scripts", "Core", "CsWeapons.cs")
BEFORE = os.path.join(HERE, "feel2-spread-values-before.txt")

# 材料缺失时**明确报错并说明去哪找**（main 规范：⛔ 不许静默跳过 —— 静默跳过会让人以为"跑过了没问题"）。
for _label, _path in (("被测源码", SRC), ("正控数据", BEFORE)):
    if not os.path.isfile(_path):
        sys.stderr.write(
            "缺失" + _label + "：\n  " + _path + "\n"
            "  本判据的材料要求（① 层：结论应能只靠库内文件重新导出）：\n"
            "    - tools/probes/judge-spread-landing.py（本件）\n"
            "    - tools/probes/feel2-spread-values-before.txt（正控数据，本该与本件同目录）\n"
            "    - <仓库根>/client/Assets/Scripts/Core/CsWeapons.cs（被测源码）\n"
            "  ⇒ 请确认本判据仍在其原位置 tools/probes/（或修正上方路径）；exit 2 = 材料缺失（≠ 1 = 判据 FAIL）。\n")
        sys.exit(2)

src = io.open(SRC, encoding="utf-8").read()
before = io.open(BEFORE, encoding="utf-8").read().splitlines()
fails = []

# 每次运行都亮出"我读的是哪份材料" —— 防"跑的对象不对"这类假通过（main 的口径：读数要带来源）。
print("== 材料 ==")
print("  被测源码 : " + SRC)
print("  正控数据 : " + BEFORE)

print("== 判据 1：既有 16 把的值逐字不变（正控）==")
kept = 0
for line in before:
    if not line.strip():
        continue
    if line in src:
        kept += 1
    else:
        fails.append("正控失败：快照行已不在源码里 -> " + line.strip())
print("  快照行数 = %d，逐字仍在 = %d，缺失 = %d" % (len([l for l in before if l.strip()]), kept,
                                                 len([l for l in before if l.strip()]) - kept))
for line in before:
    if line.strip():
        print("    OK  " + line.strip())


def nums(text):
    return [float(x.rstrip("f")) for x in re.findall(r"-?\d+(?:\.\d+)?f?", text)]


def find(pat):
    m = re.search(pat, src)
    return nums(m.group(1)) if m else None


# 判据 2/3：本轮新落的值（原版实参表来自 §A3.6）
EXPECT = [
    # (正则, 说明, 期望值)
    (r"SetAbs\(Awp,([^;]*)\);", "AWP", [0.85, 0.25, 0.1, 0.0, 0.001, 140, 10]),
    # 槽位顺序 = (空中, 移动, 慢移, 蹲, 站, 移动阈值, 慢移阈值)；Scout / G3SG1 没有慢移档 ⇒ 该槽 0
    (r"SetAbs\(Scout,([^;]*)\);", "Scout", [0.2, 0.075, 0.0, 0.0, 0.007, 170, 0]),
    (r"SetAbs\(G3sg1,([^;]*)\);", "G3SG1", [0.45, 0.15, 0.0, 0.035, 0.055, 0, 0]),
    (r"SetInv\(Usp,([^;]*)\);", "USP 未装消音", [1.2, 0.225, 0.08, 0.1, 0]),
    (r"SetInvAlt\(Usp,([^;]*)\);", "USP 装消音", [1.3, 0.25, 0.125, 0.15]),
    (r"SetInv\(Glock18,([^;]*)\);", "Glock18 半自动", [1.0, 0.165, 0.075, 0.1, 0]),
    (r"SetInvAlt\(Glock18,([^;]*)\);", "Glock18 连发", [1.2, 0.185, 0.095, 0.3]),
    # 霰弹（lead 本轮批准）：原版固定锥体 ⇒ 四档同值、阈值 0；消费端折 atan 后 = 3.86 / 4.15 度
    (r"SetAbs\(M3,([^;]*)\);", "M3 霰弹锥体", [0.0675, 0.0675, 0.0675, 0.0675, 0.0675, 0, 0]),
    (r"SetAbs\(Xm1014,([^;]*)\);", "XM1014 霰弹锥体", [0.0725, 0.0725, 0.0725, 0.0725, 0.0725, 0, 0]),
]

print("== 判据 2/3：本轮新落逐档 == 原版实参 ==")
for pat, name, exp in EXPECT:
    got = find(pat)
    if got is None:
        fails.append("未落值：" + name)
        print("  FAIL %-16s 源码里找不到落值语句" % name)
        continue
    if len(got) != len(exp) or any(abs(a - b) > 1e-6 for a, b in zip(got, exp)):
        fails.append("%s 落值 %s != 原版 %s" % (name, got, exp))
        print("  FAIL %-16s 落值 %s != 原版实参 %s" % (name, got, exp))
    else:
        print("  OK   %-16s %s" % (name, got))

print("== 判据 4：SG550 按登记**未落值**（acc 模型缺，落值会 10x 偏差）==")
if re.search(r"SetInv\(Sg550", src) or re.search(r"SetAbs\(Sg550", src):
    fails.append("SG550 竟然落值了 —— 它的 acc 模型还没补")
    print("  FAIL SG550 被落值")
else:
    print("  OK   SG550 未落值（已在源码里登记原因）")

print("== 参考：绝对档族折角后的本工程散布（度）= atan(原版实参) ==")
for name, vals in (("AWP", [0.85, 0.25, 0.1, 0.001]), ("Scout", [0.2, 0.075, 0.007, 0.0]),
                   ("G3SG1", [0.45, 0.15, 0.035, 0.055]),
                   ("M3", [0.0675, 0.0675, 0.0675, 0.0675]), ("XM1014", [0.0725] * 4)):
    print("  %-6s %s" % (name, " ".join("%.2f" % (math.degrees(math.atan(v))) for v in vals)))

print("== 结果 ==")
if fails:
    for f in fails:
        print("  FAIL " + f)
    sys.exit(1)
print("  全部通过（正控 %d 行 + 新落 7 组逐档相符）" % kept)
