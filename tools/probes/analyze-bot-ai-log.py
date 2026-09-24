#!/usr/bin/env python
# ============================================================================
# 判据资产 - slice BL-R2: count the BOT-AI business log lines that live ONLY in
# the running editor's own log file (client/Logs/<date>.log), inside an explicit
# time window. Those lines are L3 anchors (the product's own code wrote them);
# the Play probe tsvs do NOT contain them.
#
# Why a window: client/Logs/<date>.log accumulates EVERY session of the day, so a
# whole-file count mixes runs (and mixes pre/post-fix code). Every line is
# prefixed "[YYYY-MM-DD HH:MM:SS.mmm]" by the client logger, so the window is
# exact to the millisecond.
#
# rule for scripts; a BOM-less non-ASCII .ps1/.py trips sampler-selfcheck).
#
# Read-only, rerunnable, ASCII-only output.
# ============================================================================
import io
import os
import sys
import argparse

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))

# label -> regex fragment (unicode-escaped Chinese)
PATTERNS = [
    ("route-fail      ", "\\u6c42\\u8def\\u5f84\\u5931\\u8d25"),          # 求路径失败
    ("bitmap-unwalkable", "\\u4e0d\\u53ef\\u8d70\\u7684\\u8def\\u70b9"),   # 不可走的路点
    ("hold-spot       ", "\\u5b88\\u70b9"),                                # 守点
    ("plant           ", "\\u4e0b\\u5305"),                                # 下包
    ("defuse          ", "\\u62c6\\u5305"),                                # 拆包
    ("no-ground       ", "\\u63a2\\u4e0d\\u5230\\u5730\\u9762"),            # 探不到地面 (soft-floor trigger)
    ("soft-floor      ", "\\u8f6f\\u5730\\u677f"),                          # 软地板
    ("hold-table-ready", "\\u5c31\\u4f4d"),                                # 就位 (hold table ready)
    ("hold-swap       ", "\\u6362\\u4f4d"),                                # 换位
]


def ts_of(line):
    if not line.startswith("["):
        return None
    end = line.find("]")
    if end < 0:
        return None
    t = line[1:end].strip()
    if len(t) < 19 or t[4] != "-" or t[13] != ":":
        return None
    return t


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default=os.path.join(PROJECT, "client", "Logs", "2026-09-22.log"))
    ap.add_argument("--since", required=True, help="'YYYY-MM-DD HH:MM:SS' window start (inclusive)")
    ap.add_argument("--until", default="9999-12-31 23:59:59", help="window end (inclusive)")
    ap.add_argument("--samples", type=int, default=3, help="sample lines to print per pattern")
    a = ap.parse_args()

    if not os.path.exists(a.log):
        print("MISSING input: " + a.log)
        print("\nRESULT: FAIL (input absent)")
        return 1

    print("=== analyze-bot-ai-log.py  (slice BL-R2) ===")
    print("input : " + a.log)
    print("size  : %d bytes" % os.path.getsize(a.log))
    print("window: [%s .. %s]" % (a.since, a.until))
    print("")

    counts = dict((lbl, 0) for lbl, _ in PATTERNS)
    samples = dict((lbl, []) for lbl, _ in PATTERNS)
    compiled = [(lbl, p.encode("ascii").decode("unicode_escape")) for lbl, p in PATTERNS]
    total = 0
    inwin = 0

    with io.open(a.log, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            total += 1
            t = ts_of(line)
            if t is None:
                continue
            if t < a.since or t > a.until:
                continue
            inwin += 1
            for lbl, pat in compiled:
                if pat in line:
                    counts[lbl] += 1
                    if len(samples[lbl]) < a.samples:
                        samples[lbl].append(line.rstrip("\r\n"))

    print("lines read            : %d" % total)
    print("lines inside window   : %d" % inwin)
    print("")
    print("--- counts inside the window (L3 anchors: the product wrote them) ---")
    for lbl, _ in PATTERNS:
        print("  %s : %d" % (lbl, counts[lbl]))
    print("")
    print("--- sample lines (first %d per pattern) ---" % a.samples)
    for lbl, _ in PATTERNS:
        if not samples[lbl]:
            continue
        print("  [%s]" % lbl.strip())
        for s in samples[lbl]:
            print("    " + s[:400])
    print("")
    print("RESULT: OK (window counted)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
