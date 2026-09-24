#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""判据资产（tools/probes/）：**离线**断言 4 槽位计划表在**真实地图标记数据**上真的把路线岔开了
—— 差异 #67 后半（用户 2026-09-24「为什么每个机器人的操作，路线都是相同的…为什么没有分工？？？」）。

输入：
  ① `--plan` = `.ai-tmp/test/fix4-plan-offline.txt`（= `tools/probes/plan-check-offline.cs` 的产物，
     里面每个槽位有一行 `PLANROW team=T slot=0 route=Route_T_To_A goal=Bombsite_A goalord=0 role=Breaker`）
     ⛔ 表只在 C#（`client/Assets/Scripts/Module/Bot/CsBotPlans.cs`）里写一份，本脚本**不复制**它。
  ② `--markers` = `client/Assets/Resources/MapData/de_dust2_markers.bytes`（各标记的真实世界坐标）

判据（⛔ 硬判据不含"首段路点"，理由见下）：
  A. 同队 4 个槽位的**路线标记两两不同**
  B. 同队 4 个槽位的**(目标标记, 目标序号) 两两不同**
  C. 同队 4 个槽位在**每一个**本方出生点下的"最近邻排序全序列"两两不同
     （即：无论 4 只 bot 分到哪几个出生点，它们走的都不是同一条路）
  D.（观测列）首段路点相同 —— **T 队必然相同**：`Route_T_To_A ∩ Route_T_Mid ∩ Route_T_To_B
      = {(-7.5, 3.251, -47.5)}`（该点离 T 出生点最近）⇒ 三条路最近邻排序第 0 段都是它。
     ⛔ 这是**素材层**的公共点，不是缺陷；所以"首段不同"在 T 队不可能通过，硬判据换成整条序列不同。

负控（判据必须能失败）：`--corrupt` 把槽位 3 的路线改成与槽位 0 相同（= 旧实现的实际行为），必须 FAIL。

用法：
  python tools/probes/bot-route-sequence-check.py --plan .ai-tmp/test/fix4-plan-offline.txt \
      --markers client/Assets/Resources/MapData/de_dust2_markers.bytes
  ... --corrupt
"""
import argparse
import re
import sys
from collections import OrderedDict

PLANROW = re.compile(
    r"^PLANROW team=(?P<team>T|CT) slot=(?P<slot>\d+) route=(?P<route>\S+) "
    r"goal=(?P<goal>\S+) goalord=(?P<ord>\d+) role=(?P<role>\w+)$")

TEAM_SPAWN = {"T": "Spawn_T", "CT": "Spawn_CT"}


def load_markers(path):
    g = OrderedDict()
    with open(path, encoding="utf-8") as fh:
        for ln in fh:
            ln = ln.strip()
            if not ln or ln.startswith("#"):
                continue
            p = ln.split()
            if len(p) < 4:
                continue
            g.setdefault(p[0], []).append((float(p[1]), float(p[2]), float(p[3])))
    return g


def load_plan(path):
    """只取**正控段**的 PLANROW（报告里正控 + 负控两段都打 PLANROW；负控段被注入过缺陷，⛔ 不能混进来）。"""
    plan = OrderedDict()
    with open(path, encoding="utf-8", errors="replace") as fh:
        for ln in fh:
            if ln.strip().startswith("----") and "负控" in ln:
                break                      # 正控段结束（负控段的行不进表）
            m = PLANROW.match(ln.strip())
            if not m:
                continue
            plan.setdefault(m.group("team"), {})[int(m.group("slot"))] = (
                m.group("route"), m.group("goal"), int(m.group("ord")), m.group("role"))
    return plan


def nearest_first_seq(points, start):
    rem = list(points)
    out = []
    cur = start
    while rem:
        i = min(range(len(rem)), key=lambda k: (rem[k][0] - cur[0]) ** 2 + (rem[k][2] - cur[2]) ** 2)
        cur = rem.pop(i)
        out.append(cur)
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--plan", required=True)
    ap.add_argument("--markers", required=True)
    ap.add_argument("--corrupt", action="store_true")
    a = ap.parse_args()

    markers = load_markers(a.markers)
    plan = load_plan(a.plan)
    if not plan:
        print("FAIL: 计划表为空 —— %s 里没有 PLANROW 行（先跑 tools/probes/plan-check-offline.cs）" % a.plan)
        return 2

    failures = 0
    print("plan   = %s" % a.plan)
    print("markers= %s" % a.markers)
    print("negative ctl = %s" % ("ON（槽位3 路线 = 槽位0 路线）" if a.corrupt else "off"))

    for team, slots in plan.items():
        order = sorted(slots)
        if a.corrupt and 3 in slots:
            r0 = slots[0][0]
            slots[3] = (r0, slots[0][1], slots[0][2], slots[3][3])

        routes = [slots[s][0] for s in order]
        goals = [(slots[s][1], slots[s][2]) for s in order]
        print("\n[%s] 槽位 → (路线, 目标, 序号, 角色)" % team)
        for s in order:
            print("   %d -> (%s, %s, %d, %s)" % (s, slots[s][0], slots[s][1], slots[s][2], slots[s][3]))

        # A. 路线标记两两不同
        if len(set(routes)) != len(routes):
            failures += 1
            print("   ✗ A 失败：路线标记有重复 %s" % routes)
        # B. 目标 (标记, 序号) 两两不同
        if len(set(goals)) != len(goals):
            failures += 1
            print("   ✗ B 失败：目标点（标记+序号）有重复 %s" % goals)

        # C. 在每一个本方出生点下，最近邻序列两两不同
        spawns = markers.get(TEAM_SPAWN[team], [])
        if not spawns:
            print("   ! 注意：标记文件里没有 %s，跳过 C" % TEAM_SPAWN[team])
        bad_spawns = 0
        first_dup_spawns = 0
        for sp in spawns:
            seqs = []
            for r in routes:
                pts = markers.get(r, [])
                seqs.append(tuple(nearest_first_seq(pts, sp)) if pts else ())
            if len(set(seqs)) != len(seqs):
                bad_spawns += 1
            firsts = [s[0] for s in seqs if s]
            if len(set(firsts)) != len(firsts):
                first_dup_spawns += 1
        if bad_spawns:
            failures += 1
            print("   ✗ C 失败：%d/%d 个出生点下 4 条最近邻序列有重复" % (bad_spawns, len(spawns)))
        else:
            print("   ✓ C 通过：%d/%d 个出生点下 4 条最近邻序列两两不同" % (len(spawns), len(spawns)))
        print("   · 观测列：%d/%d 个出生点下「首段路点」有重复（T 队素材层公共点，非缺陷）"
              % (first_dup_spawns, len(spawns)))

    print("\nRESULT-SEQ: %s（失败 %d 条）" % ("PASS" if failures == 0 else "FAIL", failures))
    if a.corrupt:
        print("RESULT-SEQ-NEGCTL: %s（注入缺陷后必须 FAIL；实际 %s）"
              % ("PASS" if failures else "FAIL", "FAIL" if failures else "PASS"))
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
