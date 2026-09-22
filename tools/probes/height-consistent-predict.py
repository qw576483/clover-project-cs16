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


# ---------------------------------------------------------------------------
# slice BO: the SAME instrument, second mode -- the TWO-SIDED verdict.
#
# Why it exists: slice BN's mode above judges the *frozen probe columns* (a prediction). Slice BO had to
# judge the real geometry, and the real de_dust2 colliders only exist while a map session is live, so the
# raycasts were done by .ai-tmp/test/bo-reach.cs (same CsWorld mask, same CsMap.TrySampleGround
# semantics, same StepUpHeight; that script also validates ITSELF against the frozen probe's own columns:
# `prodY == recWantGY` on every control row and `highY == recHighGY`).
# This mode reads those two products and prints both halves of the two-sided criterion, side by side:
#   ① reach set from the spawn cells (must NOT collapse: old -> new)
#   ② the 1592 misjudged rows must STAY dead, and the 12581 control rows must STAY alive
# usage: python height-consistent-predict.py <dir with bo-want-verdict.tsv + bo-reach.tsv>
# ---------------------------------------------------------------------------
def two_sided(d):
    wf = os.path.join(d, "bp-want-verdict.tsv")
    if not os.path.exists(wf):
        wf = os.path.join(d, "bo-want-verdict.tsv")
    # slice BP: the reach instrument is re-run by slice BP too (it adds the PROD-BEFORE mode, i.e. the rule
    # the product actually applied before the fix) -> prefer the fresh bp-reach.tsv, fall back to slice BO's.
    rf = os.path.join(d, "bp-reach.tsv")
    if not os.path.exists(rf):
        rf = os.path.join(d, "bo-reach.tsv")
    if not os.path.exists(wf):
        print("missing %s" % wf)
        return 2
    rows = []
    with io.open(wf, "r", encoding="utf-8", errors="replace") as fh:
        for ln in fh:
            p = ln.rstrip("\r\n").split("\t")
            if len(p) < 16 or p[0].startswith("#"):
                continue
            rows.append(p)
    print("=== slice BO two-sided criterion (measured on the live de_dust2 geometry) ===")
    print("verdict rows : %d" % len(rows))
    # instrument self-validation (the raycasts are only trusted if they reproduce the frozen columns)
    pm = len([r for r in rows if r[7] == "ok"])
    pmAll = len([r for r in rows if r[7] in ("ok", "MISMATCH")])
    hm = len([r for r in rows if r[10] == "ok"])
    hmAll = len([r for r in rows if r[10] in ("ok", "MISMATCH")])
    print("self-check   : product ground probe reproduced %d/%d ; +24 m ray reproduced %d/%d"
          % (pm, pmAll, hm, hmAll))
    print("")
    for group, want_dead in (("want-no-ground", True), ("ok", False), ("want-not-standable", None)):
        g = [r for r in rows if r[0] == group]
        if not g:
            continue
        old_dead = [r for r in g if r[12] == "1"]
        new_dead = [r for r in g if r[15] == "1"]
        # slice BP: PROD-BEFORE = the rule the product ACTUALLY ran before the fix -- column 11 is `oldY`,
        # the hit of the same single ray (origin foot+StepUp, CsWorld mask). Slice-BN's code counted a hit
        # with y < 0 as "no ground" (BotNavigator.cs:838 `if (hN < 0f)`), so PROD-BEFORE is dead iff
        # (no hit) OR (hit y < 0). This is the semantics the Play oracle caught in the live product.
        prod_dead = [r for r in g if r[11] == "na" or (f(r[11]) is not None and f(r[11]) < 0.0)]
        inband = [r for r in g if r[14] != "na"]
        print("--- %-18s rows=%d ---" % (group, len(g)))
        print("  PROD-BEFORE (BN code: hN < 0 counts as no-ground) : dead %d/%d = %.1f%%"
              % (len(prod_dead), len(g), 100.0 * len(prod_dead) / len(g)))
        print("  OLD sampling (slice BN GroundYAbove) : dead %d/%d = %.1f%%"
              % (len(old_dead), len(g), 100.0 * len(old_dead) / len(g)))
        print("  NEW sampling (highest in-band face)  : dead %d/%d = %.1f%%"
              % (len(new_dead), len(g), 100.0 * len(new_dead) / len(g)))
        print("  rows with a face inside [foot-8, foot+0.45] : %d / %d = %.1f%%"
              % (len(inband), len(g), 100.0 * len(inband) / len(g)))
        if want_dead is not None:
            tag = "MUST stay dead" if want_dead else "MUST stay alive"
            print("  -> %s: OLD %s ; NEW %s   (PROD-BEFORE %s -- only the sentinel differs)"
                  % (tag, "OK" if (len(old_dead) == len(g)) == want_dead else "VIOLATED",
                     "OK" if (len(new_dead) == len(g)) == want_dead else "VIOLATED",
                     "dead %d (this is the bug: a negative but REAL floor counts as no-ground)"
                     % len(prod_dead)))
    print("")
    if os.path.exists(rf):
        print("--- ① reach set from the spawn cells (old / new / PROD-BEFORE) ---")
        spawn_rows = []
        with io.open(rf, "r", encoding="utf-8", errors="replace") as fh:
            for ln in fh:
                p = ln.rstrip("\r\n").split("\t")
                if len(p) < 9 or p[0].startswith("#"):
                    continue
                if not p[4].isdigit():        # header row / skipped neighbour rows carry no BFS result
                    continue
                prod = p[9] if len(p) > 9 else "na"
                prodb = p[10] if len(p) > 10 else "na"
                print("  %-10s cell (%s,%s) footY=%s : OLD reach %s (blocked %s) -> NEW reach %s (blocked %s)"
                      " ; PROD-BEFORE reach %s (blocked %s)"
                      % (p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], prod, prodb))
                if p[0].endswith("spawn"):
                    spawn_rows.append((p[0], int(p[4]), int(p[6]), prod))
        for lbl, o, n, pr in spawn_rows:
            # ① must be "the reach set is restored", i.e. OLD/NEW (the fixed semantics) >> 1 cell while the
            # product's pre-fix rule collapsed it to the start cell.
            print("  -> ① %s : OLD %d / NEW %d (restored, not 1) ; PROD-BEFORE %s (the collapse)"
                  % (lbl, o, n, pr))
    return 0


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_SRC
    if os.path.isdir(src):
        return two_sided(src)
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
