#!/usr/bin/env python
# ============================================================================
#  Judgement asset - slice BS: WHY do the T bots never reach a bombsite, and
#  why do the CT bots never enter one?
#
#  Both inputs come from the SAME Play run (slice BR), so every number below is
#  part of one and the same causal chain:
#    .ai-tmp/test/br-hold-plant-log.tsv : frame \t t \t level \t text
#        = L3 anchors. These are the product's OWN Game.Logger lines, tapped by
#          tools/probes/bot-hold-plant.cs during the run. Nothing is inferred
#          from screen text: the goal / route / stuck / ground witnesses are the
#          business code's own statements.
#    .ai-tmp/test/br-hold-plant.tsv : (A rows) one per-actor snapshot per 3
#        frames => the position time series used for net/total displacement.
#
#  Output: report with NUMBERS. Rerunnable, read-only, no side effects.
#
#  Why a separate asset (not analyze-hold-plant.py / analyze-bot-ai-log.py):
#    * analyze-hold-plant.py answers "did anyone arrive / plant" (the outcome)
#      and has no goal-identity, route-result or jitter columns at all.
#    * analyze-bot-ai-log.py counts 9 fixed keyword families over a wall-clock
#      window; it cannot join a goal change to a position, nor to the next one.
#  So neither can produce the per-actor time series this slice rules on.
# ============================================================================
import io
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TMP = os.path.join(PROJECT, ".ai-tmp", "test")

try:
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
except Exception:
    pass


def out(s=""):
    sys.stdout.write(str(s) + "\n")


# ---- the product's own message shapes (parsed, never guessed) --------------
RE_REPLAN = re.compile(
    u"^(.+?)\uff08(T|CT)/(\\w+)\uff09\u91cd\u65b0\u9009\u76ee\u6807\uff1a(.*?) "
    u"\u2192 \u8def\u7ebf=(\\S+) \u76ee\u6807=\\(([^)]*)\\) \u7ad9\u70b9=(True|False) "
    u"\u8def\u70b9=(-?\\d+) \u5b88\u70b9\u65f6\u957f=([\\d.]+)s\uff08\u7b2c (\\d+) "
    u"\u6b21\u6362\u76ee\u6807\uff09")
RE_REPLANFAIL = re.compile(u"^(.+?) \u91cd\u65b0\u9009\u76ee\u6807\u5931\u8d25")
RE_PLAN = re.compile(
    u"^(.+?)\uff08(T|CT)/(\\w+)\uff09\u7b2c (\\d+) \u56de\u5408\u8ba1\u5212\uff1a"
    u"\u8def\u7ebf=(\\S+) \u8def\u70b9=(-?\\d+) \u76ee\u6807=\\(([^)]*)\\) "
    u"\u7ad9\u70b9=(True|False) \u5b88\u70b9\u65f6\u957f=([\\d.]+)s "
    u"\u91cd\u5bfb\u8def\u95f4\u9694=([\\d.]+)s")
RE_C4PUSH = re.compile(
    u"^\\[C4\\] (.+?) \u643a\u5e26 C4 \u51b2\u5411\u5305\u70b9\uff1a\u76ee\u6807="
    u"\\(([^)]*)\\) \u8ddd\u6700\u8fd1\u5305\u70b9\u6807\u8bb0 ([\\d.]+)m")
RE_C4IN = re.compile(
    u"^\\[C4\\] (.+?) \u5728\u5305\u70b9 (\\S+) \u5185\uff08\u8ddd\u6700\u8fd1"
    u"\u5305\u70b9\u6807\u8bb0 ([\\d.]+)m")
RE_ROUTEFAIL = re.compile(u"^(.+?) \u6c42\u8def\u5f84\u5931\u8d25")
RE_UNREACH = re.compile(u"^(.+?) \u76ee\u6807\u683c \\((\\d+), (\\d+)\\)")
RE_WPUNREACH = re.compile(
    u"^(.+?) \u8def\u7ebf '(\\S+)' \u7b2c (\\d+)/(\\d+) \u4e2a\u8def\u70b9")
RE_GROUNDLESS = re.compile(
    u"^(.+?) \u9ad8\u5ea6\u4e00\u81f4\u6027\u5c42\u672c\u5e27\\*\\*\u672a\u751f\u6548"
    u"\\*\\*\uff08\u539f\u56e0\uff1a(.+?)\uff09")
RE_STUCK = re.compile(
    u"^(.+?) \u5361\u4f4f\uff1a([\\d.]+)s \u5185\u4f4d\u79fb ([\\d.]+)m < ([\\d.]+)m"
    u"\uff08\u8def\u7ebf '(\\S+)'\uff0c\u5269\u4f59\u8def\u70b9 (-?\\d+)\uff0c"
    u"\u539f\u56e0 (.+?)\uff09")
RE_NOPROG = re.compile(
    u"^(.+?) \u5728 ([\\d.]+)s \u5185\u671d\u76ee\u6807\u6ca1\u6709\u8fdb\u5c55"
    u"\uff08\u8ddd\u76ee\u6807 ([\\d.]+)m\uff0c\u8def\u7ebf '(\\S+)'\uff0c"
    u"\u91cd\u6392\u524d\u5269\u4f59\u8def\u70b9 (\\d+)\uff09")
RE_STATE = re.compile(
    u"^(.+?)\uff08(\\w+)\uff09\u72b6\u6001 (\\w+) \u2192 (\\w+)")
RE_SOFT = re.compile(u"^(.+?) \u811a\u4e0b\u63a2\u4e0d\u5230\u5730\u9762")
RE_HOLD = re.compile(
    u"^\u5b88\u4f4d\u8868\u5c31\u7eea\uff1a(.+?)\uff08CT\uff09\u5305\u70b9 (\\S+) "
    u"\u5b88\u4f4d (\\d+) \u4e2a")
RE_SWAP = re.compile(
    u"^\u5b88\u70b9\u6362\u4f4d\uff1a(.+?)\uff08CT\uff09\u5305\u70b9 (\\S+) "
    u"\u7b2c (\\d+) \u6b21\u6362\u4f4d")

# reason classifiers: the free-text "why" is mapped to an ASCII code so the
# report stays machine-comparable across runs.
REASON_STUCK = u"\u8fde\u7eed\u5361\u4f4f"          # consecutive stuck reports
REASON_HOLD = u"\u5b88\u70b9"                        # hold timer elapsed
REASON_BLOCKED = u"\u8def\u4e0a\u88ab\u6321"         # blocked on the way


def reason_code(why):
    if REASON_STUCK in why:
        return "stuck-escalate"
    if REASON_HOLD in why:
        return "hold-elapsed"
    return "other"


def parse_log(path):
    """-> list of (frame, t, level, tag, body)"""
    rows = []
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            p = line.rstrip("\n").rstrip("\r").split("\t")
            if len(p) < 4:
                continue
            txt = p[3]
            m = re.search(r"\] \[(\w+)\] \[(\w+)\] (.*)$", txt)
            if not m:
                continue
            try:
                fr = int(p[0])
                t = float(p[1])
            except ValueError:
                continue
            rows.append((fr, t, m.group(1), m.group(2), m.group(3)))
    return rows


def parse_rows(path):
    """A rows -> list of dicts (per-actor snapshots)."""
    rows = []
    with io.open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            p = line.rstrip("\n").rstrip("\r").split("\t")
            if len(p) < 22 or p[0] != "A":
                continue
            try:
                rows.append({
                    "frame": int(p[1]), "t": float(p[2]), "round": p[3],
                    "phase": p[4], "name": p[5], "id": p[6], "team": p[7],
                    "bot": p[8] == "1", "alive": p[9] == "1",
                    "hp": int(p[10]), "x": float(p[11]), "y": float(p[12]),
                    "z": float(p[13]), "yaw": float(p[14]),
                    "bomb": p[15] == "1", "use": float(p[16]),
                    "dA": float(p[17]), "dB": float(p[18]), "label": p[19],
                })
            except ValueError:
                continue
    return rows


def main():
    logp = os.path.join(TMP, "br-hold-plant-log.tsv")
    rowp = os.path.join(TMP, "br-hold-plant.tsv")
    logf = sys.argv[1] if len(sys.argv) > 1 else logp
    rowf = sys.argv[2] if len(sys.argv) > 2 else rowp
    if not os.path.exists(logf):
        out("MISSING input: " + logf)
        out("RESULT: FAIL (input absent)")
        return 1

    log = parse_log(logf)
    snap = parse_rows(rowf)
    out("=== analyze-bot-goal.py  (slice BS) ===")
    out("log  : %s  (%d bytes, %d lines)" % (logf, os.path.getsize(logf), len(log)))
    out("rows : %s  (%d bytes, %d A rows)" % (rowf, os.path.getsize(rowf), len(snap)))
    if log:
        out("window: t %.3f .. %.3f s" % (log[0][1], log[-1][1]))
    out("")

    # ---- 1. goal timeline (L3: the business code's own goal assignments) ----
    events = []          # (t, name, team, diff, kind, route, goal, isSite, wp, k, code)
    for fr, t, lev, tag, body in log:
        m = RE_REPLAN.match(body)
        if m:
            events.append({"t": t, "name": m.group(1), "team": m.group(2),
                           "kind": "replan", "why": m.group(4),
                           "route": m.group(5), "goal": m.group(6),
                           "site": m.group(7), "wp": int(m.group(8)),
                           "k": int(m.group(10)),
                           "code": reason_code(m.group(4))})
            continue
        m = RE_PLAN.match(body)
        if m:
            events.append({"t": t, "name": m.group(1), "team": m.group(2),
                           "kind": "plan", "why": "round-start",
                           "route": m.group(5), "goal": m.group(7),
                           "site": m.group(8), "wp": int(m.group(6)),
                           "k": 0, "code": "round-start"})
    events.sort(key=lambda e: e["t"])

    out("--- 1. goal assignments over time (L3: '%s' / '%s') ---" %
        ("replan", "plan"))
    by_name = {}
    for e in events:
        by_name.setdefault(e["name"], []).append(e)
    out("  %-10s %-3s %5s %6s %6s %7s %8s %8s  %s" %
        ("actor", "tm", "goals", "t0", "t1", "span-s", "per-sec", "dwell-p50", "route sequence"))
    for name, evs in sorted(by_name.items()):
        span = evs[-1]["t"] - evs[0]["t"]
        dwells = sorted(evs[i + 1]["t"] - evs[i]["t"] for i in range(len(evs) - 1))
        p50 = dwells[len(dwells) // 2] if dwells else 0.0
        seq = []
        for e in evs:
            if not seq or seq[-1] != e["route"]:
                seq.append(e["route"])
        out("  %-10s %-3s %5d %6.1f %6.1f %7.1f %8.2f %8.2f  %s" %
            (name, evs[0]["team"], len(evs), evs[0]["t"], evs[-1]["t"], span,
             (len(evs) - 1) / span if span > 0 else -1.0, p50,
             ">".join(seq[:10]) + ("...(more)" if len(seq) > 10 else "")))
    out("")

    out("--- 1b. replan trigger histogram (ASCII code -> count, per actor) ---")
    for name, evs in sorted(by_name.items()):
        c = {}
        for e in evs:
            if e["kind"] != "replan":
                continue
            c[e["code"]] = c.get(e["code"], 0) + 1
        if c:
            out("  %-10s %s" % (name, ", ".join("%s=%d" % kv for kv in sorted(c.items()))))
    out("")

    out("--- 1c. first 24 replan events (t, actor, why-code, route -> goal, k) ---")
    n = 0
    for e in events:
        if e["kind"] != "replan":
            continue
        out("  %7.2fs  %-10s %-14s -> %-14s goal=(%s) site=%-5s k=%d" %
            (e["t"], e["name"], e["code"], e["route"], e["goal"].replace(" ", ""),
             e["site"], e["k"]))
        n += 1
        if n >= 24:
            break
    out("")

    # ---- 2. route results / failures ---------------------------------------
    counters = {}
    uniq_unreach = {}
    wp_unreach = {}
    soft = {}
    groundless = {}
    noprog = {}
    stuck = {}
    state_tr = {}
    hold = {}
    swap = {}
    c4push = {}
    c4in = {}
    routefail = {}
    planfail = {}

    def bump(d, k):
        d[k] = d.get(k, 0) + 1

    for fr, t, lev, tag, body in log:
        m = RE_ROUTEFAIL.match(body)
        if m:
            bump(routefail, m.group(1)); continue
        m = RE_REPLANFAIL.match(body)
        if m:
            bump(planfail, m.group(1)); continue
        m = RE_UNREACH.match(body)
        if m:
            uniq_unreach.setdefault(m.group(1), set()).add((int(m.group(2)), int(m.group(3))))
            continue
        m = RE_WPUNREACH.match(body)
        if m:
            bump(wp_unreach, m.group(1)); continue
        m = RE_GROUNDLESS.match(body)
        if m:
            d = groundless.setdefault(m.group(1), {})
            d[m.group(2)] = d.get(m.group(2), 0) + 1
            continue
        m = RE_STUCK.match(body)
        if m:
            d = stuck.setdefault(m.group(1), {"n": 0, "moved": [], "route": set(), "why": set()})
            d["n"] += 1; d["moved"].append(float(m.group(3)))
            d["route"].add(m.group(5)); d["why"].add(m.group(7))
            continue
        m = RE_NOPROG.match(body)
        if m:
            d = noprog.setdefault(m.group(1), {"n": 0, "dist": []})
            d["n"] += 1; d["dist"].append(float(m.group(3)))
            continue
        m = RE_STATE.match(body)
        if m:
            bump(state_tr, "%s:%s->%s" % (m.group(1), m.group(3), m.group(4))); continue
        m = RE_SOFT.match(body)
        if m:
            bump(soft, m.group(1)); continue
        m = RE_C4PUSH.match(body)
        if m:
            d = c4push.setdefault(m.group(1), {"n": 0, "d": []})
            d["n"] += 1; d["d"].append(float(m.group(3))); continue
        m = RE_C4IN.match(body)
        if m:
            bump(c4in, m.group(1)); continue
        m = RE_HOLD.match(body)
        if m:
            d = hold.setdefault(m.group(1), {"n": 0, "site": m.group(2), "spots": int(m.group(3))})
            d["n"] += 1; continue
        m = RE_SWAP.match(body)
        if m:
            bump(swap, m.group(1)); continue

    out("--- 2. route results (L3 counts) ---")
    out("  route-fail ('%s')            : %s" % ("req path failed",
        ", ".join("%s=%d" % kv for kv in sorted(routefail.items())) or "none"))
    out("  replan-fail ('%s')           : %s" % ("replan failed",
        ", ".join("%s=%d" % kv for kv in sorted(planfail.items())) or "none"))
    out("  waypoint 'walkable-but-unreachable' : %s" % (
        ", ".join("%s=%d" % kv for kv in sorted(wp_unreach.items())) or "none"))
    out("  distinct goals reported 'not connected to my position' :")
    for name, cells in sorted(uniq_unreach.items()):
        out("    %-10s cells=%s (n=%d)" % (name, sorted(cells), len(cells)))
    out("  ground witness lost ('own cell has no ground => height layer off') :")
    for name, d in sorted(groundless.items()):
        out("    %-10s %s" % (name, ", ".join("n=%d" % v for v in d.values())))
    out("  '%s' (soft floor)       : %s" % ("no ground under foot",
        ", ".join("%s=%d" % kv for kv in sorted(soft.items())) or "none"))
    out("  stuck reports               : %s" % (
        ", ".join("%s=%d" % (k, v["n"]) for k, v in sorted(stuck.items())) or "none"))
    for name, v in sorted(stuck.items()):
        out("      %-10s moved-in-window=%s routes=%s why=%s" %
            (name, v["moved"], sorted(v["route"]), sorted(v["why"])))
    out("  'no progress in X s -> repath here' : %s" % (
        ", ".join("%s=%d" % (k, v["n"]) for k, v in sorted(noprog.items())) or "none"))
    out(
        "  hold-table ready            : %s" % (
        ", ".join("%s(spots=%d)" % (k, v["spots"]) for k, v in sorted(hold.items())) or "none"))
    out("  hold slot swaps             : %s" % (
        ", ".join("%s=%d" % kv for kv in sorted(swap.items())) or "none"))
    out("  C4 push lines (still >7m from a site) : %s" % (
        ", ".join("%s n=%d d=%.2f..%.2f" % (k, v["n"], min(v["d"]), max(v["d"]))
                  for k, v in sorted(c4push.items())) or "none"))
    out("  C4 'inside a site, planting' lines    : %s" % (
        ", ".join("%s=%d" % kv for kv in sorted(c4in.items())) or "none"))
    out("  state transitions (first 20 keys) :")
    for k, v in sorted(state_tr.items(), key=lambda kv: -kv[1])[:20]:
        out("    %-34s %d" % (k, v))
    out("")

    # ---- 3. replan frequency: is it frame-driven or timer-driven? ----------
    out("--- 3. replan frequency (L3 event spacing) ---")
    for name, evs in sorted(by_name.items()):
        ts = [e["t"] for e in evs if e["kind"] == "replan"]
        if len(ts) < 2:
            continue
        gaps = sorted(ts[i + 1] - ts[i] for i in range(len(ts) - 1))
        out("  %-10s replans=%3d  gaps(min/p50/max)=%.2f/%.2f/%.2fs" %
            (name, len(ts), gaps[0], gaps[len(gaps) // 2], gaps[-1]))
    out("  note: 'stuck' escalation needs N consecutive stuck reports at a"
        " fixed check interval, so a truly motionless bot replans on a TIMER,"
        " not every frame (see section 5 for the interval witnesses).")
    out("")

    # ---- 4. jitter: net displacement / total path, per actor --------------
    out("--- 4. jitter: net displacement vs total path, per actor per round ---")
    out("  (from the A rows; only alive samples, consecutive-sample steps only)")
    out("  %-10s %-4s %6s %9s %9s %8s %7s %7s" %
        ("actor", "rd", "samples", "total-m", "net-m", "net/total", "rev%", "site-min"))
    per = {}
    for r in snap:
        key = (r["round"], r["name"])
        per.setdefault(key, []).append(r)
    agg = {}
    for (rd, name), rs in sorted(per.items()):
        rs.sort(key=lambda r: r["t"])
        total = 0.0
        rev = 0
        steps = 0
        prev = None
        prevdir = None
        alive = [r for r in rs if r["alive"]]
        for r in alive:
            if prev is not None:
                dx = r["x"] - prev["x"]; dz = r["z"] - prev["z"]
                d = (dx * dx + dz * dz) ** 0.5
                total += d
                if d > 1e-6:
                    dirv = (dx / d, dz / d)
                    if prevdir is not None:
                        dot = dirv[0] * prevdir[0] + dirv[1] * prevdir[1]
                        steps += 1
                        if dot < -0.5:
                            rev += 1
                    prevdir = dirv
            prev = r
        net = 0.0
        if len(alive) >= 2:
            net = ((alive[-1]["x"] - alive[0]["x"]) ** 2 +
                   (alive[-1]["z"] - alive[0]["z"]) ** 2) ** 0.5
        smin = min(min(r["dA"], r["dB"]) for r in alive) if alive else -1.0
        ratio = (net / total) if total > 1e-6 else -1.0
        out("  %-10s %-4s %6d %9.1f %9.1f %8.3f %7.1f %7.2f" %
            (name, rd, len(alive), total, net, ratio,
             100.0 * rev / steps if steps else -1.0, smin))
        a = agg.setdefault(name, {"total": 0.0, "net": 0.0, "rev": 0, "steps": 0,
                                  "smin": 1e9, "xr": [1e9, -1e9], "zr": [1e9, -1e9],
                                  "team": rs[0]["team"], "bot": rs[0]["bot"]})
        a["total"] += total; a["net"] += net; a["rev"] += rev; a["steps"] += steps
        a["smin"] = min(a["smin"], smin)
        for r in rs:
            a["xr"][0] = min(a["xr"][0], r["x"]); a["xr"][1] = max(a["xr"][1], r["x"])
            a["zr"][0] = min(a["zr"][0], r["z"]); a["zr"][1] = max(a["zr"][1], r["z"])
    out("")
    out("--- 4b. whole session per actor ---")
    out("  %-10s %-3s %4s %9s %9s %8s %7s %8s %20s" %
        ("actor", "tm", "bot", "total-m", "net-m", "net/total", "rev%", "site-min", "world x/z range"))
    for name, a in sorted(agg.items(), key=lambda kv: -kv[1]["total"]):
        out("  %-10s %-3s %4s %9.1f %9.1f %8.3f %7.1f %8.2f  x[%.0f..%.0f] z[%.0f..%.0f]" %
            (name, a["team"], "Y" if a["bot"] else "n", a["total"], a["net"],
             (a["net"] / a["total"]) if a["total"] > 1e-6 else -1.0,
             100.0 * a["rev"] / a["steps"] if a["steps"] else -1.0, a["smin"],
             a["xr"][0], a["xr"][1], a["zr"][0], a["zr"][1]))
    out("")

    # ---- 5. nearest-bombsite distance over time: converging or oscillating? -
    out("--- 5. distance to nearest bombsite marker over time (A rows) ---")
    out("  %-10s %-4s %7s %8s %8s %8s %9s" %
        ("actor", "rd", "samples", "first", "min", "last", "min@t"))
    for (rd, name), rs in sorted(per.items()):
        alive = [r for r in rs if r["alive"]]
        if not alive:
            continue
        ds = [min(r["dA"], r["dB"]) for r in alive]
        i = ds.index(min(ds))
        out("  %-10s %-4s %7d %8.2f %8.2f %8.2f %9.1f" %
            (name, rd, len(alive), ds[0], min(ds), ds[-1], alive[i]["t"]))
    out("")

    # ---- 6. team-level: did any T ever close on a site? -------------------
    out("--- 6. the yardstick: is anyone converging on a site at all? ---")
    for team in ("T", "CT"):
        vals = []
        for name, a in agg.items():
            if a["team"] == team:
                vals.append(a["smin"])
        if vals:
            vals.sort()
            out("  %-3s actors=%d  best-approach min=%.2f  p50=%.2f  max=%.2f (m)" %
                (team, len(vals), vals[0], vals[len(vals) // 2], vals[-1]))
    out("")

    # ---- 7. verdict -------------------------------------------------------
    out("--- 7. verdict inputs (all figures above, no prose-only claims) ---")
    t_replan = sum(1 for e in events if e["kind"] == "replan" and e["team"] == "T")
    t_stuck = sum(1 for e in events if e["kind"] == "replan" and e["code"] == "stuck-escalate")
    out("  T replan events            : %d (of which stuck-escalated %d)" % (t_replan, t_stuck))
    out("  distinct unreachable goal cells : %d" %
        sum(len(v) for v in uniq_unreach.values()))
    out("  route-fail events          : %d" % sum(routefail.values()))
    out("  waypoint-unreachable events: %d" % sum(wp_unreach.values()))
    out("  C4 'inside site' events    : %d" % sum(c4in.values()))
    out("  hold-slot swaps            : %d" % sum(swap.values()))
    out("")
    out("RESULT: OK (report produced)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
