#!/usr/bin/env python
# -*- coding: utf-8 -*-
# Slice BN -- the EIGHT numbers the task book lists, measured from ONE Play session's probes.
# Offline, seconds, read-only. Re-runnable on any later run (same columns).
#
# Inputs (both written by the probes with an EXPLICIT output path -- slice BN fix 1):
#   <probe>.tsv       bot-phys.cs       B rows: 0 B / 1 frame / 2 t / 3 name / 4 id / 5 team /
#                                          6 state / 7..9 pos / 16 canStand / 17 groundY /
#                                          18 normalY / 19 posMinusGround / 20 dirsMovable /
#                                          21 goalStepLen / 27 reason / 33..38 wantTop/High*
#   <hold>.tsv        bot-hold-plant.cs A rows: 0 A / 1 frame / 2 t / 3 round / 4 phase / 5 name /
#                                          6 id / 7 team / 8 isBot / 9 isAlive / 11..13 pos /
#                                          17 distA / 18 distB / 19 label / 21 ctArriveT
#                     E rows: ROUND / PHASE / CTSITE / CTSITECHANGE / CARRIER / TARRIVE /
#                             TPLANTSTART / TPLANTED / SWAP / HOLDTABLE / C4LOG
#
# The "movable actor" yardstick is the one slice BI used (displacement > 5.8 m) and the task book
# warns it swings between runs -- so it is reported under TWO readings (per round / whole session).
import io
import os
import sys
from collections import Counter, defaultdict

PROJ = r"c:\Work\Server\f-v2\clover-project-cs16\T"
MOVED_M = 5.8          # slice BI yardstick, quoted by the task book
CT_SITE_ENUM = ("E\tCTSITE\t",)


def load(path, kinds):
    rows = []
    if not os.path.exists(path):
        return rows
    with io.open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            p = line.rstrip("\r\n").split("\t")
            if p and p[0] in kinds:
                rows.append(p)
    return rows


def fnum(v):
    try:
        return float(v)
    except Exception:
        return None


def dist(vals):
    v = sorted(x for x in vals if x is not None)
    if not v:
        return "no finite value"
    m = len(v)
    return "n=%d min=%.3f p50=%.3f p90=%.3f max=%.3f" % (m, v[0], v[m // 2], v[(m * 9) // 10], v[-1])


def main():
    try:                       # a GBK console cannot print the CJK we forward from the game log
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    tmp = sys.argv[1] if len(sys.argv) > 1 else os.path.join(r"c:\Work\Server\f-v2\clover-project-cs16", ".ai-tmp", "test")
    physF = os.path.join(tmp, sys.argv[2] if len(sys.argv) > 2 else "bn-bot-phys.tsv")
    holdF = os.path.join(tmp, sys.argv[3] if len(sys.argv) > 3 else "bn-hold-plant.tsv")
    logF = os.path.join(tmp, sys.argv[4] if len(sys.argv) > 4 else "bn-hold-plant-log.tsv")

    B = load(physF, ("B",))
    A = load(holdF, ("A",))
    E = load(holdF, ("E",))
    print("=== slice BN: the 8 numbers (post-change run) ===")
    print("phys: %s  B rows=%d" % (physF, len(B)))
    print("hold: %s  A rows=%d  E rows=%d" % (holdF, len(A), len(E)))
    print("")

    # ---------------- 1. movable actors (two readings) ----------------
    print("--- 1. movable actors (displacement > %.1f m; task book: this yardstick swings) ---" % MOVED_M)
    if A:
        rounds = sorted(set(p[3] for p in A))
        per_round = defaultdict(float)     # (actor, round) -> path length
        per_sess = defaultdict(float)
        last = {}
        for p in A:
            aid = p[6]
            x, z = fnum(p[11]), fnum(p[13])
            if x is None or z is None:
                continue
            key = (aid, p[3])
            if key in last:
                x0, z0 = last[key]
                d = ((x - x0) ** 2 + (z - z0) ** 2) ** 0.5
                per_round[key] += d
                per_sess[aid] += d
            last[key] = (x, z)
        by_round = defaultdict(int)
        for (aid, r), d in per_round.items():
            if d > MOVED_M:
                by_round[r] += 1
        print("  rounds seen                : %s" % (rounds,))
        print("  per-round count (>%.1fm)    : %s   (task book: was 0)" % (MOVED_M, dict(sorted(by_round.items()))))
        print("  whole-session count        : %d of %d actors moved >%.1f m   (task book: was 1, once 6)"
              % (len([1 for d in per_sess.values() if d > MOVED_M]), len(per_sess), MOVED_M))
        print("  whole-session displacement : %s" % dist(list(per_sess.values())))
    else:
        print("  no A rows -> NOT MEASURED")
    print("")

    # ---------------- 2/3/7. site / plant / holdtable events ----------------
    print("--- 2. CT entering a bombsite / 3. bot planting / 7. HOLDTABLE ---")
    ct_site = [p for p in E if p[1] == "CTSITE"]
    print("  E CTSITE rows              : %d   distinct CT actors = %d   (task book: was 0)"
          % (len(ct_site), len(set(p[6] for p in ct_site))))
    for tag in ("TARRIVE", "TPLANTSTART", "TPLANTED", "CARRIER", "SWAP", "HOLDTABLE", "CTSITECHANGE"):
        n = len([p for p in E if p[1] == tag])
        print("  E %-12s rows        : %d" % (tag, n))
    # how many of the 8 numbers are additionally visible in the raw log (L3, business-written)
    if os.path.exists(logF):
        txt2 = io.open(logF, "r", encoding="utf-8", errors="replace").read()
        for probe in (u"\u53ef\u8fbe\u96c6", u"\u88ab\u9ad8\u5ea6\u5224\u6b7b"):
            hits = [ln for ln in txt2.splitlines() if probe in ln]
            print("  log lines with %-6s : %d" % ("reach-set" if probe == u"\u53ef\u8fbe\u96c6" else "blocked", len(hits)))
            for ln in hits[:3]:
                print("    | " + ln.strip()[:260].encode("ascii", "replace").decode("ascii"))
    if os.path.exists(logF):
        txt = io.open(logF, "r", encoding="utf-8", errors="replace").read()
        print("  log lines mentioning 守位表就绪 : %d   (L3: written by the business code itself)"
              % txt.count(u"\u5b88\u4f4d\u8868\u5c31\u7eea"))
        print("  log lines mentioning 高度一致性层 : %d" % txt.count(u"\u9ad8\u5ea6\u4e00\u81f4\u6027\u5c42"))
        hl = [ln for ln in txt.splitlines() if u"\u9ad8\u5ea6\u4e00\u81f4\u6027\u5c42" in ln or "StepUpHeight" in ln]
        for ln in hl[:4]:
            print("    | " + ln.strip()[:300])
    print("")

    # ---------------- 4. did T leave the spawn area ----------------
    print("--- 4. has the T side left its spawn area (nearest bombsite marker, metres) ---")
    if A:
        td = [(fnum(p[17]), fnum(p[18]), p[5]) for p in A if p[7] == "T"]
        if td:
            near = [min(dA, dB) for dA, dB, _ in td if dA is not None and dB is not None]
            print("  T actors sampled           : %d   nearest-site distance : %s" % (len(td), dist(near)))
            print("  (task book: before the change T stayed 85.9..90.7 m away in the worst run)")
            per = defaultdict(list)
            for dA, dB, nm in td:
                if dA is not None and dB is not None:
                    per[nm].append(min(dA, dB))
            for nm in sorted(per):
                print("    %-12s nearest-site min = %.1f m" % (nm, min(per[nm])))
        else:
            print("  no T rows")
    else:
        print("  no A rows -> NOT MEASURED")
    print("")

    # ---------------- 5/6/8. per-bot A* numbers from the phys probe ----------------
    print("--- 5. goalStepLen (1 m request) / 6. want-no-ground / 8. foot gap + ground normal ---")
    if B:
        reasons = Counter(p[27].split("+")[0] for p in B)
        wng = [p for p in B if p[27].startswith("want-no-ground")]
        print("  reason histogram           : %s" % dict(reasons.most_common(8)))
        print("  want-no-ground rows        : %d / %d   (task book: was 1592 runtime / 754 offline)"
              % (len(wng), len(B)))
        step = [fnum(p[21]) for p in wng]
        print("  (those rows) goalStepLen   : %s   (task book: was 0.289..0.560)" % dist(step))
        print("  all rows goalStepLen       : %s" % dist([fnum(p[21]) for p in B]))
        gap = [fnum(p[19]) for p in B]
        bump = [g for g in gap if g is not None and g > 0.05]
        print("  pos - groundY (foot gap)   : %s" % dist(gap))
        print("  rows with gap > 0.05 m     : %d / %d   (sinking/burial check; task book: 3/18952 before)"
              % (len(bump), len(B)))
        print("  ground normalY             : %s   (>=0.70 = standable; task book: 0 rows below)"
              % dist([fnum(p[18]) for p in B]))
        print("  dirsMovable (0..8)         : %s" % dict(Counter(p[20] for p in B).most_common(9)))
        print("  canStand at own position   : %s" % dict(Counter(p[16] for p in B)))
    else:
        print("  no B rows -> NOT MEASURED (probe never sampled a bot actor?)")
    print("")
    return 0


if __name__ == "__main__":
    sys.exit(main())
