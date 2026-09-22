#!/usr/bin/env python
# ============================================================================
# 判据资产 - slice BK: turn the per-frame physics probe (tools/probes/bot-phys.cs,
# product of ONE Play session) into NUMBERS that decide slice BJ's hypothesis:
#
#   "single-layer 2D bitmap + multi-layer geometry => a layer gap makes the bots
#    physically unable to walk."
#
# The probe is the authority for the columns (see its header); this script only
# counts. Rerunnable, read-only, pure ASCII output.
#
# Verdict logic (explicit, so it cannot be "interpreted"):
#   * walled-in  = a sampled row with dirsMovable == 0   (no 1 m step resolves)
#   * steep      = normalY < CsConst.MaxStandableSlopeNormalZ (0.70)
#   * floating   = |pos.y - SampleGround(pos).y| > 0.05 m
#   * unstanding  = CanStand(pos) == 0
#   A bot that is stuck while NONE of the above fires at its cell is NOT blocked
#   by the map: whatever stops it is upstream of the physics query.
# ============================================================================
import io
import os
import sys
from collections import Counter, OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TSV = os.path.join(PROJECT, ".ai-tmp", "test", "bk-bot-phys.tsv")

MAX_STANDABLE_SLOPE_NORMAL_Y = 0.70   # CsConst.MaxStandableSlopeNormalZ
FOOT_EPS = 0.05                       # "float gap" threshold (m)


def f(v):
    try:
        return float(v)
    except Exception:
        return float("nan")


def main():
    if not os.path.exists(TSV):
        print("MISSING input: " + TSV)
        print("\nRESULT: FAIL (input absent)")
        return 1
    print("=== analyze-bot-phys.py  (slice BK) ===")
    print("input      : " + TSV)
    print("input size : %d bytes" % os.path.getsize(TSV))
    print("")

    brows = []
    stuck = []
    events = Counter()
    with io.open(TSV, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            p = line.rstrip("\n").rstrip("\r").split("\t")
            if not p or not p[0]:
                continue
            if p[0] == "B":
                brows.append(p)
            elif p[0] == "E":
                events[p[1]] += 1
                if p[1] == "STUCK":
                    stuck.append(p)

    if not brows:
        print("no B rows -> the probe never sampled a bot")
        print("\nRESULT: FAIL (no samples)")
        return 1

    # ---- per actor ---------------------------------------------------------
    per = OrderedDict()
    for p in brows:
        n = p[3]
        a = per.setdefault(n, {
            "team": p[5], "id": p[4], "n": 0, "fmin": 1 << 60, "fmax": -1,
            "xmin": 1e9, "xmax": -1e9, "zmin": 1e9, "zmax": -1e9,
            "ymin": 1e9, "ymax": -1e9, "dirmax": -1, "dirmin": 99,
            "gapmax": 0.0, "normmin": 9.9, "canstand0": 0, "steep": 0,
            "moved0": 0, "goalvalid": 0, "pos": Counter()})
        fr = int(p[1])
        a["n"] += 1
        a["fmin"] = min(a["fmin"], fr)
        a["fmax"] = max(a["fmax"], fr)
        x, y, z = f(p[7]), f(p[8]), f(p[9])
        a["xmin"] = min(a["xmin"], x); a["xmax"] = max(a["xmax"], x)
        a["zmin"] = min(a["zmin"], z); a["zmax"] = max(a["zmax"], z)
        a["ymin"] = min(a["ymin"], y); a["ymax"] = max(a["ymax"], y)
        d = int(p[20]); a["dirmax"] = max(a["dirmax"], d); a["dirmin"] = min(a["dirmin"], d)
        if d == 0:
            a["moved0"] += 1
        a["gapmax"] = max(a["gapmax"], abs(f(p[19])))
        ny = f(p[18])
        if ny == ny:
            a["normmin"] = min(a["normmin"], ny)
            if ny < MAX_STANDABLE_SLOPE_NORMAL_Y:
                a["steep"] += 1
        if p[16] == "0":
            a["canstand0"] += 1
        if p[13] == "1":
            a["goalvalid"] += 1
        a["pos"]["%.3f,%.3f,%.3f" % (x, y, z)] += 1

    print("--- 1. event inventory ---")
    for k in ["PROBE", "ROUND", "STUCK"]:
        print("  %-8s %d" % (k, events.get(k, 0)))
    print("  B rows   %d over %d actor(s)" % (len(brows), len(per)))
    print("")

    print("--- 2. per actor: is it blocked BY THE MAP? (1 m horizon, 8 directions) ---")
    print("  %-9s %-3s %7s %8s %8s %7s %7s %8s %7s %6s %7s" %
          ("actor", "tm", "samples", "moveXZ", "dirsMin", "dirsMax", "gapMax",
           "normMin", "steep", "cs0", "dirs0"))
    for n, a in per.items():
        mv = max(a["xmax"] - a["xmin"], a["zmax"] - a["zmin"])
        print("  %-9s %-3s %7d %8.3f %8d %7d %7.3f %8.3f %7d %6d %7d" %
              (n, a["team"], a["n"], mv, a["dirmin"], a["dirmax"], a["gapmax"],
               a["normmin"] if a["normmin"] < 9 else float("nan"),
               a["steep"], a["canstand0"], a["moved0"]))
    print("")

    print("--- 3. verdict inputs (whole run, every sampled bot row) ---")
    walled = [p for p in brows if int(p[20]) == 0]
    steep = [p for p in brows if f(p[18]) == f(p[18]) and f(p[18]) < MAX_STANDABLE_SLOPE_NORMAL_Y]
    floatg = [p for p in brows if abs(f(p[19])) > FOOT_EPS]
    nostand = [p for p in brows if p[16] == "0"]
    print("  rows with dirsMovable == 0 (walled in)                  : %d / %d" % (len(walled), len(brows)))
    print("  rows with ground normal.y < %.2f (steep)                : %d / %d" %
          (MAX_STANDABLE_SLOPE_NORMAL_Y, len(steep), len(brows)))
    print("  rows with |pos.y - groundY| > %.2f m (floating/embedded) : %d / %d" %
          (FOOT_EPS, len(floatg), len(brows)))
    print("  rows with CanStand(pos) == 0 (not standable)            : %d / %d" % (len(nostand), len(brows)))
    print("  rows where the goal hook worked (goalValid == 1)        : %d / %d" %
          (len([p for p in brows if p[13] == "1"]), len(brows)))
    print("")

    # ---- 3b. ResolveMove step toward the goal + the probe's reason histogram ----
    # column map (tools/probes/bot-phys.cs:371-379): 21 goalStepLen, 22 goalStepLenYFollow,
    # 27 reason. reason is the probe's own verdict for "why the 1 m step toward the goal
    # went the way it did" -- it separates "the bitmap says no" (nothing in the product's
    # log any more) from "the physical landing spot has no ground" (want-no-ground).
    print("--- 3b. ResolveMove step toward the goal (column 21) + reason histogram (column 27) ---")
    steps = [f(p[21]) for p in brows]
    steps = [s for s in steps if s == s]
    steps.sort()
    if steps:
        n = len(steps)
        print("  goalStepLen  min=%.3f  p50=%.3f  p90=%.3f  max=%.3f  (requested = min(1.000, distXZ))" %
              (steps[0], steps[n // 2], steps[(n * 9) // 10], steps[-1]))
        print("  goalStepLen < 0.99 m (partly/fully blocked)     : %d / %d" %
              (len([s for s in steps if s < 0.99]), n))
        print("  goalStepLen <= 0.001 m (not moved at all)       : %d / %d" %
              (len([s for s in steps if s <= 0.001]), n))
    rc = Counter(p[27] for p in brows)
    for k, v in rc.most_common():
        print("  reason %-20s : %d" % (k, v))
    for team in ("CT", "T"):
        rt = Counter(p[27] for p in brows if p[5] == team)
        print("  reason[%s] %s" % (team, ", ".join("%s=%d" % kv for kv in rt.most_common())))
    print("")

    print("--- 4. the cells the bots were stuck on (first STUCK row per actor/cell) ---")
    seen = set()
    for p in stuck:
        d = OrderedDict(t.split("=", 1) for t in p[2:] if "=" in t)
        key = (d.get("name", "?"), d.get("pos", "?"))
        if key in seen:
            continue
        seen.add(key)
        print("  f%-6s %-9s %-3s pos=%-30s groundY=%-8s normalY=%-7s gap=%-7s dirs=%-3s canStand=%s reason=%s" %
              (d.get("frame", "?"), d.get("name", "?"), d.get("team", "?"), d.get("pos", "?"),
               d.get("groundY", "?"), d.get("normalY", "?"), d.get("posMinusGround", "?"),
               d.get("dirsMovable", "?"), d.get("canStand", "?"), d.get("reason", "?")))
    print("")

    # ---- 5. layer gap: distinct standing heights across actors -------------
    print("--- 5. layer gap check: the y each actor actually stood at ---")
    allY = sorted(set(round(f(p[8]), 2) for p in brows))
    print("  distinct standing heights (rounded 0.01 m): %s" % ", ".join("%.2f" % y for y in allY[:20]))
    for n, a in per.items():
        top = ", ".join("%s x%d" % (k.split(",")[1], v) for k, v in a["pos"].most_common(4))
        print("  %-9s y %.3f..%.3f   most-common y: %s" % (n, a["ymin"], a["ymax"], top))
    print("")

    fail = 0
    if len(walled) > 0:
        fail += 1
    print("=== checks ===")
    print("  %-4s %-38s %s" % ("PASS" if len(walled) == 0 else "FAIL",
                              "no sampled bot row is walled in",
                              "dirsMovable==0 rows = %d" % len(walled)))
    print("")
    print("RESULT: %s (%d FAIL / 1 check)" % ("PASS" if fail == 0 else "FAIL", fail))
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
