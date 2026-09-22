#!/usr/bin/env python
# -*- coding: utf-8 -*-
# Slice BN, offline (seconds, read-only): predict -- from the runtime probe's own columns -- what
# the new **height-consistency** walkability judge (BotNavigator.BuildHeightReach) will do, BEFORE
# and AFTER the change. This is the "offline prediction" the task book asks for; it needs no Play
# session, and it is re-run on the fresh probe tsv after the Play chain (same script, same numbers).
#
# Input : the `bot-phys.cs` runtime probe tsv (39 columns, see tools/probes/bot-phys.cs file header).
#         0-based columns used: 8 posY / 23 wantGroundY / 26 wantStepDy / 27 reason /
#                               33 wantTopGroundY / 35 wantTopDy / 36 wantHighGroundY / 38 wantHighDy
#
# The judge under test (source: Module/Map/CsMap.cs:514-523 `TryStepUp`,
#   `if (hasGround && point.y - from.y > CsConst.StepUpHeight) return false;`):
#
#   edge (cur -> next) is DEAD  iff  the landing face of `next` is higher than the foot by more than
#                                   one step (CsConst.StepUpHeight = 0.45, Core/CsConst.cs:113)
#
#   * rows where the probe said `want-no-ground` are the MISJUDGED cells (the 2D bitmap calls them
#     walkable, the landing face is really a 0.8..3.2 m ledge). Prediction: the judge kills 100% of
#     them when their facedy > 0.45.
#   * rows the probe called ok (CONTROL) are legal landings. Prediction: the judge kills 0% of them.
#     If the control loses rows, the judge is over-blocking -- and every number below is void.
#   * `highDy` (= ray from 24 m above the foot) is what a naive "sample the topmost face" judge would
#     use; the control column shows how many LEGAL cells such a judge would wrongly kill (roofs).
import io
import os
import sys
from collections import Counter

STEP_UP = 0.45  # CsConst.StepUpHeight (Core/CsConst.cs:113)
DEFAULT_SRC = r"c:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\bk-bot-phys.tsv"

C_POSY, C_REASON = 8, 27
C_WANT_GY, C_WANT_STEPDY = 23, 26
C_TOP_GY, C_TOP_DY = 33, 35
C_HIGH_GY, C_HIGH_DY = 36, 38


def f(v):
    if v == "na":
        return None
    try:
        return float(v)
    except Exception:
        return None


def dist(vals):
    v = sorted(x for x in vals if x is not None)
    if not v:
        return "n=0"
    m = len(v)
    return "n=%d min=%.3f p50=%.3f p90=%.3f max=%.3f" % (m, v[0], v[m // 2], v[(m * 9) // 10], v[-1])


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SRC
    if not os.path.exists(src):
        print("missing probe tsv: %s" % src)
        return 2

    rows = []
    with io.open(src, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            p = line.rstrip("\r\n").split("\t")
            if not p or p[0] != "B" or len(p) < 39:
                continue
            rows.append(p)

    print("=== slice BN offline prediction: height-consistency walkable judge ===")
    print("probe tsv : %s" % src)
    print("B rows    : %d  (columns >= 39 -> sliceBM probe layout)" % len(rows))
    if not rows:
        print("no B rows: nothing to predict (wrong file?)")
        return 2
    print("judge     : edge dead iff landing face - foot > %.2f m  (CsMap.cs:514-523 TryStepUp)" % STEP_UP)
    print("")

    mis = [p for p in rows if p[C_REASON].startswith("want-no-ground")]
    ctl = [p for p in rows if p[C_REASON].startswith("ok")]
    other = len(rows) - len(mis) - len(ctl)

    print("--- A. MISJUDGED cells (probe reason = want-no-ground) = the 2D bitmap lies ---")
    print("  rows                                 : %d" % len(mis))
    killed = [p for p in mis if (f(p[C_HIGH_DY]) or -9e9) > STEP_UP]
    print("  killed by the new judge (highDy>%.2f) : %d / %d = %.1f%%"
          % (STEP_UP, len(killed), len(mis), 100.0 * len(killed) / max(1, len(mis))))
    print("  highDy distribution                  : %s" % dist([f(p[C_HIGH_DY]) for p in mis]))
    print("  top3Dy (>3 m ray) killed             : %d / %d"
          % (len([p for p in mis if (f(p[C_TOP_DY]) or -9e9) > STEP_UP]), len(mis)))
    print("  distinct highDy values               : %s"
          % ", ".join(sorted(set("%.3f" % f(p[C_HIGH_DY]) for p in mis if f(p[C_HIGH_DY]) is not None))[:20]))
    print("  (a) real holes (highY == na)         : %d   (c) face below foot : %d"
          % (len([p for p in mis if p[C_HIGH_GY] == "na"]),
             len([p for p in mis if p[C_HIGH_GY] != "na" and (f(p[C_HIGH_DY]) or 0) <= 0])))
    print("  per team                             : %s" % dict(Counter(p[5] for p in mis)))
    print("")

    print("--- B. CONTROL: cells the probe called ok = legal landings (must NOT be killed) ---")
    print("  rows                                 : %d" % len(ctl))
    ok_keep = [p for p in ctl if (f(p[C_WANT_STEPDY]) if f(p[C_WANT_STEPDY]) is not None else -9e9) <= STEP_UP]
    print("  landing step (wantStepDy) distribution: %s" % dist([f(p[C_WANT_STEPDY]) for p in ctl]))
    print("  kept by the new judge                : %d / %d = %.1f%%  (wantStepDy <= %.2f)"
          % (len(ok_keep), len(ctl), 100.0 * len(ok_keep) / max(1, len(ctl)), STEP_UP))
    wrongly = [p for p in ctl if (f(p[C_WANT_STEPDY]) if f(p[C_WANT_STEPDY]) is not None else -9e9) > STEP_UP]
    print("  WRONGLY killed (false positives)      : %d  %s"
          % (len(wrongly), "(none)" if not wrongly else [p[2] + "@y" + p[C_POSY] for p in wrongly[:5]]))
    print("  -- why the ray must start at FOOT+one step, not above the head --")
    roof = [p for p in ctl if (f(p[C_HIGH_DY]) or -9e9) > STEP_UP]
    print("  a 'topmost face' judge would kill     : %d / %d = %.1f%% of LEGAL cells (roofs/overhangs)"
          % (len(roof), len(ctl), 100.0 * len(roof) / max(1, len(ctl))))
    print("    control highDy distribution         : %s" % dist([f(p[C_HIGH_DY]) for p in ctl]))
    print("")

    print("--- C. summary ---")
    print("  rows: B=%d  misjudged=%d  control=%d  other=%d" % (len(rows), len(mis), len(ctl), other))
    print("  misjudged killed : %d/%d (%.1f%%)" % (len(killed), len(mis), 100.0 * len(killed) / max(1, len(mis))))
    print("  control kept     : %d/%d (%.1f%%)" % (len(ok_keep), len(ctl), 100.0 * len(ok_keep) / max(1, len(ctl))))
    print("  => the judge separates the two groups cleanly iff killed==mis and kept==ctl")
    return 0


if __name__ == "__main__":
    sys.exit(main())
