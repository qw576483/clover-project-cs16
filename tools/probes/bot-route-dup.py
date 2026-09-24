#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""判据资产（tools/probes/）：机器人**路线重复率**分析器 —— 差异 #67 后半。

用户 2026-09-24 第三次投诉：「为什么每个机器人的操作，路线都是相同的。你这什么行为树，什么 ai 啊？？
为什么没有分工？？？」

本脚本把"路线都是相同的"变成一个**可断言的数字**：
   对每一个「阵营-回合」样本，取同队各 bot 的 (路线标记, 目标点[, 整条路线序列])，
   统计两两之间"相同"的对数；PASS ⇔ 三类都 **0 对相同**（且样本里至少有 1 对可比）。

⚠️ **判据口径（⛔ 与探针同一份，改这里就必须改 tools/probes/probe-bot-routes.cs）**：
  硬判据 = 路线标记不同 + 目标点不同 [+ 整条路线序列不同（该字段存在时）]
  ⛔ **不用"首段路点"当硬判据**：本工程 `Resources/MapData/de_dust2_markers.bytes` 里
     Route_T_To_A ∩ Route_T_Mid ∩ Route_T_To_B 共用同一个岔口点 `(-7.5, 3.251, -47.5)`
     （它离 T 出生点最近）⇒ 最近邻排序后三条路的第 0 段**必然相同** —— 那是素材层的公共点，
    任何代码改动都做不到"T 队首段路点不同"。首段路点只作为**观测列**打印。

两种输入（**同一套口径**，before / after 由同一条代码路径算出来）：
  ① --log   <client/Logs/YYYY-MM-DD.log>  ：解析 `[Bot] X（T/Normal…）第 N 回合计划：…` 行（实机真相）
     旧格式没有 `首段路点=`/`槽位=` 字段 ⇒ 首段列记 NONE（不影响硬判据）。
  ② --probe <.ai-tmp/test/fix4-route-*.txt>：解析 tools/probes/probe-bot-routes.cs 的表（实机探针）

过滤（把"修前"与"修后"的样本机械分开，⛔ 不靠比对时间戳）：
  --only-new ：只取带 `槽位=` 的行（本次改动的产物）
  --only-old ：只取不含 `槽位=` 的行（改动前的历史样本）

负控（判据必须能失败）：--corrupt 把每个样本里第 2 只 bot 的 (路线, 序列, 目标) 改成跟第 1 只一样
（= 逐字复现旧实现的撞车），此时必须输出 FAIL，且 `RESULT-DUP-NEGCTL: PASS` 才算"负控没打空"。

用法：
  python tools/probes/bot-route-dup.py --log   <log>   --only-old
  python tools/probes/bot-route-dup.py --log   <log>   --only-new
  python tools/probes/bot-route-dup.py --probe <.ai-tmp/test/fix4-route-A.txt>
  python tools/probes/bot-route-dup.py --probe <.ai-tmp/test/fix4-route-A.txt> --corrupt
"""
import argparse
import re
import sys
from collections import OrderedDict

LOG_PLAN = re.compile(
    # 团队段用 `.*?` 而不是 `[^）]*`：产品日志里的角色串本身带括号
    # （`（T/Normal/角色=支援（守点×1.0 交火×1.00））第 3 回合计划：…`），
    # 非贪婪的 `.*?` 会一直扩到「`）第` 真正成立」的那个右括号 ⇒ 嵌套括号不再吃掉整行。
    r"(?P<name>[^\s（）(]+)（(?P<team>T|CT)/.*?）第\s*(?P<round>\d+)\s*回合计划："
    r"(?:槽位=(?P<slot>\d+)/队内序号=(?P<ord>\d+)(?:\(持C4\))?\s*)?"
    r"路线=(?P<route>\S+?)(?:（[^）]*）)?\s+"
    r"(?:首段路点=(?P<first>\([^)]*\)|无)\s+)?"
    r"路点=\d+\s+目标=(?P<goal>\([^)]*\)|无)"
)

# probe-bot-routes.cs 表格行（制表符）：# bot id team slot ordinal role route waypoints first goal isSite pos signature
PROBE_ROW = re.compile(
    r"^\d+\t(?P<name>[^\t]+)\t(?P<id>\d+)\t(?P<team>T|CT)\t(?P<slot>-?\d+)\t(?P<ord>-?\d+)\t"
    r"(?P<role>[^\t]*)\t(?P<route>[^\t]+)\t(?P<wp>\d+)\t(?P<first>[^\t]+)\t(?P<goal>[^\t]+)\t"
    r"(?P<site>\w+)\t(?P<pos>[^\t]+)\t(?P<sig>[^\t]+)$")


def new_rec(route, sig, first, goal):
    return {"route": route, "sig": sig, "first": first, "goal": goal}


def parse_log(path, only_new, only_old):
    groups = OrderedDict()
    last_round, session = {}, {}
    seen = unknown = 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if "回合计划：" not in line:
                continue
            m = LOG_PLAN.search(line)
            if not m:
                unknown += 1
                continue
            has_slot = "槽位=" in line
            if only_new and not has_slot:
                continue
            if only_old and has_slot:
                continue
            team, rnd = m.group("team"), int(m.group("round"))
            if rnd < last_round.get(team, -1):
                session[team] = session.get(team, 0) + 1
            last_round[team] = rnd
            key = (team, session.get(team, 0), rnd)
            groups.setdefault(key, OrderedDict())[m.group("name")] = new_rec(
                m.group("route"), "NONE", m.group("first") or "NONE", m.group("goal"))
            seen += 1
    return groups, seen, unknown


def parse_probe(path):
    groups = OrderedDict()
    seen = 0
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = PROBE_ROW.match(line.rstrip("\n"))
            if not m:
                continue
            groups.setdefault((m.group("team"), 0, 0), OrderedDict())[m.group("name")] = new_rec(
                m.group("route"), m.group("sig"), m.group("first"), m.group("goal"))
            seen += 1
    return groups, seen


def score(groups, corrupt=False):
    total = dr = dsig = dgoal = dfirst = 0
    detail = []
    for key, bots in groups.items():
        if len(bots) < 2:
            continue
        items = list(bots.items())
        if corrupt and len(items) >= 2:
            items[1] = (items[1][0], items[0][1])
        for i in range(len(items)):
            for j in range(i + 1, len(items)):
                total += 1
                a, b = items[i][1], items[j][1]
                sr = a["route"] == b["route"]
                ss = (a["sig"] == b["sig"] and a["sig"] != "NONE")
                sg = a["goal"] == b["goal"]
                sf = (a["first"] == b["first"] and a["first"] != "NONE")
                dr += sr
                dsig += ss
                dgoal += sg
                dfirst += sf
                if sr or ss or sg:
                    detail.append((key, items[i][0], items[j][0], sr, ss, sg, sf, a))
    return total, dr, dsig, dgoal, dfirst, detail


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log")
    ap.add_argument("--probe")
    ap.add_argument("--only-new", action="store_true")
    ap.add_argument("--only-old", action="store_true")
    ap.add_argument("--corrupt", action="store_true")
    a = ap.parse_args()

    if a.log:
        groups, seen, unknown = parse_log(a.log, a.only_new, a.only_old)
        src = a.log
    elif a.probe:
        groups, seen = parse_probe(a.probe)
        unknown, src = 0, a.probe
    else:
        print("need --log or --probe")
        return 2

    print("source          = %s" % src)
    print("filter          = %s" % ("only-new(带槽位)" if a.only_new else
                                     "only-old(无槽位)" if a.only_old else "all"))
    print("negative ctl    = %s" % ("ON（注入：第2只 = 第1只）" if a.corrupt else "off"))
    print("plan lines      = %d（不可解析 %d）" % (seen, unknown))
    print("team-round 样本 = %d（其中 >=2 只 bot 的 = %d）"
          % (len(groups), sum(1 for g in groups.values() if len(g) >= 2)))

    total, dr, dsig, dgoal, dfirst, detail = score(groups, a.corrupt)
    print("同队两两对数    = %d" % total)
    print("  [硬判据] 路线标记相同 = %d" % dr)
    print("  [硬判据] 路线序列相同 = %d（该字段缺失时恒 0）" % dsig)
    print("  [硬判据] 目标点相同   = %d" % dgoal)
    print("  [观测列] 首段路点相同 = %d（T 队素材层公共点，见文件头 ⚠️）" % dfirst)

    if detail:
        print("\n撞车明细（前 12 条）：")
        for (key, na, nb, sr, ss, sg, sf, v) in detail[:12]:
            flags = "".join(["[路线]" if sr else "", "[序列]" if ss else "",
                             "[目标]" if sg else "", "[首段]" if sf else ""])
            print("  %s×第%s回合 %s vs %s %s route=%s goal=%s sig=%s"
                  % (key[0], key[2], na, nb, flags, v["route"], v["goal"], v["sig"][:60]))
    else:
        print("\n撞车明细：无（全部两两互不相同）")

    for key, bots in groups.items():
        if len(bots) < 2:
            continue
        print("  样本 %s×第%s回合: %d 只 bot -> 路线 %d 种 / 目标点 %d 种 / 首段路点 %d 种"
              % (key[0], key[2], len(bots),
                 len({v["route"] for v in bots.values()}),
                 len({v["goal"] for v in bots.values()}),
                 len({v["first"] for v in bots.values() if v["first"] != "NONE"})))

    ok = (dr == 0 and dsig == 0 and dgoal == 0 and total > 0)
    print("\nRESULT-DUP: %s" % ("PASS" if ok else "FAIL"))

    if a.corrupt:
        print("RESULT-DUP-NEGCTL: %s（注入缺陷后必须 FAIL；实际 %s）"
              % ("PASS" if not ok else "FAIL", "FAIL" if not ok else "PASS"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
