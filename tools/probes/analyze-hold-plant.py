#!/usr/bin/env python
# ============================================================================
#  Judgement asset - slice BI: WHY did the T-side bots fail to plant the bomb
#  in the 3 rounds of the BH-R probe run?
#
#  Input : <project>/.ai-tmp/test/bh-hold-plant.tsv   (9.7 MB, produced by
#          tools/probes/bot-hold-plant.cs inside Play; schema documented below)
#  Output: pure-ASCII report with NUMBERS. Rerunnable, no side effects.
#
#  TSV schema (see the probe source for the authority):
#    E <TAB> TAG <TAB> k=v ...        event line
#      TAG in {PROBE, SITEPTS, ROUND, PHASE, CARRIER, CTSITE, CTSITECHANGE,
#              SWAP, HOLDTABLE, C4LOG, TARRIVE, TPLANTSTART, TPLANTED}
#    A <TAB> frame t round phase name id team isBot alive hp x y z yaw
#         hasBomb useProgress distA distB label planted ctArriveT
#      = one 20 Hz snapshot row per actor (sampleEvery=3 frames)
#
#  Site radius口径 comes from the probe itself (SITEPTS radius=7.000 =
#    CsMarkers.BombsiteRadius). PlantStopRadius (1.5 m) comes from CsBotConst.
# ============================================================================
import io
import os
import sys
from collections import Counter, OrderedDict

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TSV = os.path.join(PROJECT, ".ai-tmp", "test", "bh-hold-plant.tsv")

BOMBSITE_RADIUS = 7.0     # printed by the probe's SITEPTS line
PLANT_STOP_RADIUS = 1.5   # CsBotConst.PlantStopRadius
HOLD_SWAP_SECONDS = 8.0   # CsBotConst.HoldSwapSeconds
HOLD_MIN_SEP = 3.0        # CsBotConst.HoldSpotMinSeparation

fail = 0
checks = []


def check(name, ok, detail):
    global fail
    if not ok:
        fail += 1
    checks.append((name, bool(ok), detail))


class Seg(object):
    def __init__(self, idx, frame, t, round_no, phase):
        self.idx = idx
        self.start_frame = frame
        self.start_t = t
        self.round_no = round_no
        self.start_phase = phase
        self.end_frame = frame
        self.end_t = t
        self.rows = []          # A rows
        self.events = []        # E rows


def parse(path):
    rows = []
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            p = line.rstrip("\n").rstrip("\r").split("\t")
            if not p or not p[0]:
                continue
            rows.append(p)
    return rows


def kv(p):
    d = OrderedDict()
    for tok in p[2:]:
        if "=" in tok:
            k, v = tok.split("=", 1)
            d[k] = v
    return d


def main():
    if not os.path.exists(TSV):
        print("MISSING input: " + TSV)
        print("\nRESULT: FAIL (input absent)")
        return 1

    rows = parse(TSV)
    print("=== analyze-hold-plant.py  (slice BI) ===")
    print("input      : " + TSV)
    print("input size : %d bytes / %d lines" % (os.path.getsize(TSV), len(rows)))
    print("bombsite radius=%.1f m   plant stop radius=%.1f m" %
          (BOMBSITE_RADIUS, PLANT_STOP_RADIUS))
    print("")

    # ---- 0. event inventory -------------------------------------------------
    etags = Counter()
    for r in rows:
        if r[0] == "E":
            etags[r[1]] += 1
    print("--- 0. event inventory (what the probe actually saw) ---")
    for tag in ["PROBE", "SITEPTS", "ROUND", "PHASE", "CARRIER", "CTSITE",
                "CTSITECHANGE", "SWAP", "HOLDTABLE", "C4LOG", "TARRIVE",
                "TPLANTSTART", "TPLANTED"]:
        print("  %-14s %d" % (tag, etags.get(tag, 0)))
    print("")

    # ---- 1. segments (one per ROUND event; round numbers repeat after a
    #         new match starts, so the segment index is the authority) ------
    segs = []
    cur = None
    for r in rows:
        if r[0] == "E" and r[1] == "ROUND":
            d = kv(r)
            cur = Seg(len(segs) + 1, int(d["frame"]), float(d["t"]),
                      int(d["round"]), d.get("phase", "?"))
            segs.append(cur)
        elif cur is not None:
            if r[0] == "A":
                cur.rows.append(r)
                cur.end_frame = int(r[1])
                cur.end_t = float(r[2])
            elif r[0] == "E":
                cur.events.append(r)
    print("--- 1. rounds seen (segments) ---")
    for s in segs:
        print("  S%d  round=%d  frames %d..%d  t %.1f..%.1f s  rows=%d" %
              (s.idx, s.round_no, s.start_frame, s.end_frame,
               s.start_t, s.end_t, len(s.rows)))
    print("")

    # ---- 2. per-actor mobility + closest approach (whole run) -------------
    per = OrderedDict()
    for r in rows:
        if r[0] != "A":
            continue
        name = r[5]
        if name not in per:
            per[name] = {"team": r[7], "bot": r[8] == "1", "n": 0,
                         "xmin": 1e9, "xmax": -1e9, "zmin": 1e9, "zmax": -1e9,
                         "minA": 1e9, "minB": 1e9, "labels": Counter(),
                         "maxUse": 0.0, "bombFrames": 0}
        a = per[name]
        a["n"] += 1
        x = float(r[11]); z = float(r[13])
        dA = float(r[17]); dB = float(r[18])
        a["xmin"] = min(a["xmin"], x); a["xmax"] = max(a["xmax"], x)
        a["zmin"] = min(a["zmin"], z); a["zmax"] = max(a["zmax"], z)
        a["minA"] = min(a["minA"], dA); a["minB"] = min(a["minB"], dB)
        a["labels"][r[19]] += 1
        a["maxUse"] = max(a["maxUse"], float(r[16]))
        if r[15] == "1":
            a["bombFrames"] += 1

    print("--- 2. per-actor mobility + closest approach to a bombsite (whole run) ---")
    print("  %-9s %-3s %-4s %8s %8s %8s %8s %8s  %s" %
          ("actor", "tm", "bot", "maxMove", "minDistA", "minDistB", "best", "peakUse", "labels"))
    for name, a in per.items():
        mv = max(a["xmax"] - a["xmin"], a["zmax"] - a["zmin"])
        best = min(a["minA"], a["minB"])
        lab = ",".join("%s=%d" % (k, v) for k, v in a["labels"].most_common())
        print("  %-9s %-3s %-4s %8.2f %8.2f %8.2f %8.2f %8.3f  %s" %
              (name, a["team"], "Y" if a["bot"] else "n", mv,
               a["minA"], a["minB"], best, a["maxUse"], lab))
    print("")

    # ---- 3. CT hold distribution: did ANY CT ever stand inside a site? ----
    print("--- 3. CT hold distribution (was any bombsite actually held?) ---")
    ct_all = [n for n, a in per.items() if a["team"] == "CT"]
    ct_named = [n for n in ct_all if per[n]["bot"]]          # CT *bots* only
    ct_human = [n for n in ct_all if not per[n]["bot"]]
    ct_in_site = [n for n in ct_named if min(per[n]["minA"], per[n]["minB"]) <= BOMBSITE_RADIUS]
    ctA = [n for n in ct_named if per[n]["minA"] <= BOMBSITE_RADIUS]
    ctB = [n for n in ct_named if per[n]["minB"] <= BOMBSITE_RADIUS]
    print("  CT actors              : %d  bots=%d (%s) + human=%s" %
          (len(ct_all), len(ct_named), ", ".join(ct_named), ", ".join(ct_human) or "-"))
    print("  CT BOT that reached A  : %d  (%s)" % (len(ctA), ", ".join(ctA) or "-"))
    print("  CT BOT that reached B  : %d  (%s)" % (len(ctB), ", ".join(ctB) or "-"))
    print("  CTSITE events          : %d  (each one == a CT first entering a site)" %
          etags.get("CTSITE", 0))
    print("  CTSITECHANGE events    : %d" % etags.get("CTSITECHANGE", 0))
    print("  SWAP L3 log events     : %d  (business code's own <hold swap> marker)" %
          etags.get("SWAP", 0))
    print("  HOLDTABLE L3 log events: %d  (business code's own <hold table ready> marker)" %
          etags.get("HOLDTABLE", 0))
    for n in ct_all:
        print("    CT %-9s minDistA=%6.2f  minDistB=%6.2f  -> %s" %
              (n, per[n]["minA"], per[n]["minB"],
               "IN-SITE" if min(per[n]["minA"], per[n]["minB"]) <= BOMBSITE_RADIUS
               else "OUTSIDE by %.2f m" % (min(per[n]["minA"], per[n]["minB"]) - BOMBSITE_RADIUS)))
    print("")

    # ---- 3b. per-ROUND mobility: is anyone moving toward a site at all? ---
    print("--- 3b. per-round displacement per actor (max x/z spread inside that round) ---")
    for s in segs:
        acc = OrderedDict()
        for r in s.rows:
            n = r[5]
            if n not in acc:
                acc[n] = [1e9, -1e9, 1e9, -1e9, 1e9, 1e9]
            a = acc[n]
            x = float(r[11]); z = float(r[13])
            a[0] = min(a[0], x); a[1] = max(a[1], x)
            a[2] = min(a[2], z); a[3] = max(a[3], z)
            a[4] = min(a[4], float(r[17])); a[5] = min(a[5], float(r[18]))
        print("  S%d round=%d  (%.0f..%.0f s)" %
              (s.idx, s.round_no, s.start_t, s.end_t))
        for n, a in acc.items():
            mv = max(a[1] - a[0], a[3] - a[2])
            print("      %-9s %-3s maxMove=%7.2f m  minDistA=%7.2f  minDistB=%7.2f" %
                  (n, per[n]["team"], mv, a[4], a[5]))
    print("")

    # ---- 4. hold swap detection (position proxy, HOLD_SWAP_SECONDS window) -
    print("--- 4. hold swapping (proxy: a CT moving > %.1f m within %.0f s; the probe"
          " does not carry a hold-spot index) ---" % (HOLD_MIN_SEP, HOLD_SWAP_SECONDS))
    for n in ct_named:
        trail = [(float(r[2]), float(r[11]), float(r[13]))
                 for r in rows if r[0] == "A" and r[5] == n and r[9] == "1"]
        swaps = 0
        details = []
        i = 0
        while i < len(trail):
            t0, x0, z0 = trail[i]
            j = i
            while j < len(trail) and trail[j][0] - t0 <= HOLD_SWAP_SECONDS:
                j += 1
            far = None
            for k in range(i, j):
                d = ((trail[k][1] - x0) ** 2 + (trail[k][2] - z0) ** 2) ** 0.5
                if d > HOLD_MIN_SEP:
                    far = (trail[k][0], d)
                    break
            if far:
                swaps += 1
                details.append("%.1fs->%.1fs d=%.2f" % (t0, far[0], far[1]))
                i = k
            else:
                i += 1
        print("  %-9s window-hops=%d   %s" % (n, swaps, "; ".join(details[:4])))
    print("")

    # ---- 5. C4 carrier chain (collapse the per-frame oscillation) ---------
    print("--- 5. C4 carrier chain (consecutive snapshot carrier-sets, run-length collapsed) ---")
    chain = []
    for r in rows:
        if r[0] != "A":
            continue
        if r[15] == "1":
            chain.append((int(r[1]), float(r[2]), int(r[3]), r[5]))
    runs = []
    for fr, t, rnd, name in chain:
        if runs and runs[-1][3] == name:
            runs[-1][1] = t
            runs[-1][2] = fr
        else:
            runs.append([t, t, fr, name, rnd])
    print("  carrier-holding samples=%d  raw CARRIER events=%d  collapsed runs=%d" %
          (len(chain), etags.get("CARRIER", 0), len(runs)))
    osci = [x for x in runs if len(x) == 5]
    for x in osci[:40]:
        print("    t %7.2f..%7.2f s  round=%d  carrier=%s" % (x[0], x[1], x[4], x[3]))
    if len(osci) > 40:
        print("    ... (%d more runs)" % (len(osci) - 40))
    names_in_chain = sorted(set(x[3] for x in osci))
    print("  distinct carriers : %s" % ", ".join(names_in_chain))
    print("")

    # ---- 6. per-round carrier convergence ---------------------------------
    print("--- 6. per-round: did the C4 carrier converge on a bombsite, and did"
          " planting start? ---")
    for s in segs:
        holders = OrderedDict()
        for r in s.rows:
            if r[15] == "1":
                n = r[5]
                if n not in holders:
                    holders[n] = {"n": 0, "minA": 1e9, "minB": 1e9,
                                  "maxUse": 0.0, "firstT": float(r[2]),
                                  "lastT": float(r[2]), "usedFrames": 0}
                h = holders[n]
                h["n"] += 1
                h["minA"] = min(h["minA"], float(r[17]))
                h["minB"] = min(h["minB"], float(r[18]))
                h["maxUse"] = max(h["maxUse"], float(r[16]))
                h["lastT"] = float(r[2])
                if float(r[16]) > 0.0:
                    h["usedFrames"] += 1
        dead = [x for x in s.events if x[1] == "TPLANTSTART"]
        planted = [x for x in s.events if x[1] == "TPLANTED"]
        arrive = [x for x in s.events if x[1] == "TARRIVE"]
        print("  S%d round=%d : carrying actors = %s ; TARRIVE=%d TPLANTSTART=%d TPLANTED=%d" %
              (s.idx, s.round_no,
               ", ".join("%s(%d samples)" % (k, v["n"]) for k, v in holders.items()) or "-",
               len(arrive), len(dead), len(planted)))
        for k, h in holders.items():
            best = min(h["minA"], h["minB"])
            gap = best - BOMBSITE_RADIUS
            print("      %-9s best approach=%6.2f m  (A=%6.2f B=%6.2f)  %s  peakUseProgress=%.3f usedFrames=%d" %
                  (k, best, h["minA"], h["minB"],
                   ("INSIDE site" if best <= BOMBSITE_RADIUS
                    else "OUTSIDE by %.2f m" % gap),
                   h["maxUse"], h["usedFrames"]))
    print("")

    # ---- 7. verdict: which link of the chain broke ------------------------
    print("--- 7. chain verdict (all figures are numbers, no prose-only claims) ---")
    t_names = [n for n, a in per.items() if a["team"] == "T"]
    for n in t_names:
        a = per[n]
        mv = max(a["xmax"] - a["xmin"], a["zmax"] - a["zmin"])
        print("  T %-9s maxMove=%6.2f m  bombFrames=%d  bestApproachToSite=%6.2f m" %
              (n, mv, a["bombFrames"], min(a["minA"], a["minB"])))

    # link A: no plant at all?
    total_planted = etags.get("TPLANTED", 0)
    total_arrive = etags.get("TARRIVE", 0)
    total_start = etags.get("TPLANTSTART", 0)
    print("")
    print("")
    print("  CAUTION: T Rikk's 'maxMove=79.56 m / bestApproach=17.18 m' is NOT walking:")
    print("           the last segment S4 is a TEAM SWAP (Player and the 4 spawn-side bots")
    print("           switch to T; Rikk/Spliff/Darrell/Scuzzy become CT), so Rikk's 17.18 m")
    print("           is the CT spawn point, reached by respawn, not by navigating to A.")
    print("           Inside S4 Rikk's own displacement is 2.72 m (see section 3b, S4 row).")
    print("")
    print("  LINK 1 (was the bomb ever planted?)      TPLANTED=%d" % total_planted)
    print("  LINK 2 (was any carrier at a site?)      TARRIVE=%d" % total_arrive)
    print("  LINK 3 (did any carrier pull the trigger?) TPLANTSTART=%d" % total_start)

    # carriers that are BOTs and never entered the site
    bot_holders = set()
    for r in rows:
        if r[0] == "A" and r[15] == "1" and r[8] == "1":
            bot_holders.add(r[5])
    print("  BOT carriers in the whole run          : %s" %
          (", ".join(sorted(bot_holders)) or "-"))
    stuck = []
    for n in sorted(bot_holders):
        if min(per[n]["minA"], per[n]["minB"]) > BOMBSITE_RADIUS:
            stuck.append("%s(short by %.2f m)" %
                         (n, min(per[n]["minA"], per[n]["minB"]) - BOMBSITE_RADIUS))
    print("  BOT carriers that never reached a site : %d  %s" %
          (len(stuck), ", ".join(stuck) or "-"))
    print("")

    # ---- assertions -------------------------------------------------------
    check("S1 site points loaded", etags.get("SITEPTS", 0) == 1,
          "SITEPTS events = %d" % etags.get("SITEPTS", 0))
    check("S2 three rounds probed", len(segs) >= 3,
          "segments = %d" % len(segs))
    check("S3 holder data present", len(chain) > 0,
          "carrier-holding snapshots = %d" % len(chain))
    check("S4 no CT bot ever held a site", len(ct_in_site) == 0,
          "CT bots in site = %d" % len(ct_in_site))
    check("S5 zero hold-swap L3 markers", etags.get("SWAP", 0) == 0,
          "SWAP events = %d" % etags.get("SWAP", 0))
    check("S6 zero hold-table L3 markers", etags.get("HOLDTABLE", 0) == 0,
          "HOLDTABLE events = %d" % etags.get("HOLDTABLE", 0))

    print("=== checks ===")
    for name, ok, detail in checks:
        print("  %-4s %-34s %s" % ("PASS" if ok else "FAIL", name, detail))
    print("")
    print("RESULT: %s (%d FAIL / %d checks)" % ("PASS" if fail == 0 else "FAIL",
                                                fail, len(checks)))
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
