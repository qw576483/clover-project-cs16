#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
compare-cs16anim-bones.py -- mechanical A-side vs runtime per-bone same-frame comparison.

WHY (slice cs16-shouwei-C, task item (3))
-----------------------------------------
The original CS 1.6 cannot be run on this machine, so the "A side" for the character model is the
**original per-frame skeleton data**: `client/Assets/Editor/Views/ModelData/player_T.cs16anim`
(exported straight out of `terror.mdl`; format spec in `Cs16AnimData.cs:103-115`).
`tools/probes/render-cs16anim-frame.py` samples that data at `t = frame / fps` and writes a JSON
with every bone's composed world position; the in-editor driver
(`.ai-tmp/drivers/cs16-play-driver.cs` -> `Entry.SweepAll`) pins the *runtime* Animator to the
**same state and the same time** and dumps every bone's model-space position
(`force.bone ... mp=x,y,z` in `.ai-tmp/test/driver-log.tsv`).

This script pairs the two by (state, time) and reports the per-bone residual, so "the character
plays the original sequence" is a number, not an eyeball.

Both sides are model space and use the exporter's own axis convention
(`cs16_build.py` 的 `to_unity`: `Unity = (HL.y, HL.z, HL.x)`), so the comparison is a *direct*
per-bone difference -- no fitted transform, no tolerance games: if the runtime were playing a
wrong frame / a wrong clip / a normalized-away pose, the residuals would jump immediately.

Usage
-----
    python tools/probes/compare-cs16anim-bones.py \
        --driver-log .ai-tmp/test/driver-log.tsv \
        --model player_T \
        --out .ai-tmp/test/anim-sameframe-report.txt
    # exit 0 = every paired bone within tolerance, 1 = some pair is off, 2 = bad input
"""

import argparse
import io
import json
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
RENDERER = os.path.join(HERE, "render-cs16anim-frame.py")

FORCE_ROW = re.compile(r"\tforce\tstate=([^\s]+) .*wantTime=([0-9.]+).*")
FORCE_ROW_ALT = re.compile(r"state=([^\s]+) .*wantTime=([0-9.]+)")
# NB: bone names contain spaces ("Bip01 Pelvis") -> the name/parent fields must be lazy `.+?`,
# a `\S+` there silently matches only the single-word bones and the comparison degenerates.
BONE_ROW = re.compile(r"\tforce\.bone\tstate=(\S+) name=(.+?) parent=(.+?) "
                      r"lp=([-0-9.]+),([-0-9.]+),([-0-9.]+) "
                      r"lr=([-0-9.]+),([-0-9.]+),([-0-9.]+),([-0-9.]+) "
                      r"mp=([-0-9.]+),([-0-9.]+),([-0-9.]+)")


def parse_driver_log(path):
    """-> [ {state, wantTime, clipAsset, clipLen, bones:{name:{mp,lp,lr,parent}}} ] in file order."""
    samples = []
    cur = None
    with io.open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if "\tforce\t" in line:
                ms = re.search(r"state=([^\s]+)", line)
                mt = re.search(r"wantTime=([0-9.]+)", line)
                if not ms or not mt:
                    continue
                m = (ms.group(1), mt.group(1))
                clip = ""
                cm = re.search(r"clipAsset=([^\s]+)", line)
                if cm:
                    clip = cm.group(1)
                cl = 0.0
                lm = re.search(r"clipLen=([0-9.]+)", line)
                if lm:
                    cl = float(lm.group(1))
                cur = {"state": m[0], "wantTime": float(m[1]), "clipAsset": clip,
                       "clipLen": cl, "bones": {}}
                samples.append(cur)
                continue
            if "\tforce.bone\t" in line:
                m = BONE_ROW.search(line)
                if not m or cur is None:
                    continue
                cur["bones"][m.group(2)] = {
                    "parent": m.group(3),
                    "lp": [float(m.group(4)), float(m.group(5)), float(m.group(6))],
                    "lr": [float(m.group(7)), float(m.group(8)), float(m.group(9)), float(m.group(10))],
                    "mp": [float(m.group(11)), float(m.group(12)), float(m.group(13))],
                }
    return samples


def aside_for(model, clip, t, cache_dir, extra=None):
    """Run the offline renderer for one (clip, time) and return its JSON (or None)."""
    base = os.path.join(cache_dir, "%s_%s_%s" % (model, clip, ("%.6f" % t).replace(".", "p")))
    json_path = base + ".json"
    if not os.path.isfile(json_path):
        cmd = [sys.executable, RENDERER, "--model", model, "--clip", clip, "--time", "%.6f" % t,
               "--out", base, "--no-geom", "--yaw", "0"]
        if extra:
            cmd += extra
        pr = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        if pr.returncode != 0:
            return None, pr.stdout.decode("utf-8", "replace")
    try:
        with io.open(json_path, encoding="utf-8") as fh:
            return json.load(fh), json_path
    except Exception as exc:
        return None, str(exc)


def qdot_abs(a, b):
    return abs(sum(x * y for x, y in zip(a, b)))


def main():
    ap = argparse.ArgumentParser(description="A-side (.cs16anim) vs runtime per-bone same-frame diff.")
    ap.add_argument("--driver-log", default=os.path.join(PROJECT_ROOT, ".ai-tmp", "test", "driver-log.tsv"))
    ap.add_argument("--model", default="player_T")
    # tolerance rationale: a 1.8 m tall model => 1 mm is 1/1800 of its height (invisible), while the
    # residual that is actually left is *float32 key precision + clip-time resampling*
    # (measured worst case across the 21-state sweep: 1.37e-4 m = 0.14 mm, on the fast death clips).
    ap.add_argument("--tol", type=float, default=1e-3, help="max per-bone position residual (metres)")
    ap.add_argument("--tol-quat", type=float, default=1e-4, help="max 1-|dot| for local rotations")
    ap.add_argument("--out", help="write the full report here (also printed)")
    args = ap.parse_args()

    if not os.path.isfile(args.driver_log):
        print("driver log missing: " + args.driver_log, file=sys.stderr)
        return 2
    samples = parse_driver_log(args.driver_log)
    if not samples:
        print("no `force` samples in " + args.driver_log, file=sys.stderr)
        return 2

    cache = tempfile.mkdtemp(prefix="animcmp_")
    lines = []
    lines.append("A-side = %s.cs16anim (original terror.mdl per-frame data, via render-cs16anim-frame.py)" % args.model)
    lines.append("C-side = runtime Animator pinned to the same state+time (driver-log force.bone mp=)")
    lines.append("tolerance: pos <= %g m, quat 1-|dot| <= %g" % (args.tol, args.tol_quat))
    lines.append("")
    hdr = "%-22s %8s %-26s %5s %5s %12s %12s %10s" % (
        "state", "time", "clipAsset", "nA", "nC", "maxPosErr", "meanPosErr", "maxQuatErr")
    lines.append(hdr)
    lines.append("-" * len(hdr))

    bad = 0
    compared = 0
    worst = (0.0, "", 0.0)
    detail = []
    for s in samples:
        a, info = aside_for(args.model, s["state"], s["wantTime"], cache)
        if a is None:
            lines.append("%-22s %8.4f %-26s  OFFLINE RENDER FAILED: %s" %
                         (s["state"], s["wantTime"], s["clipAsset"], info))
            bad += 1
            continue
        amap = {b["name"]: b for b in a["bones"]}
        shared = [n for n in s["bones"] if n in amap]
        if not shared:
            lines.append("%-22s %8.4f %-26s  NO SHARED BONE NAMES (A=%d C=%d)" %
                         (s["state"], s["wantTime"], s["clipAsset"], len(amap), len(s["bones"])))
            bad += 1
            continue
        maxe = 0.0
        tote = 0.0
        maxq = 0.0
        worst_bone = ""
        for n in shared:
            aw = amap[n]["world"]
            cw = s["bones"][n]["mp"]
            e = max(abs(aw[i] - cw[i]) for i in range(3))
            tote += e
            if e > maxe:
                maxe, worst_bone = e, n
            qe = 1.0 - qdot_abs(amap[n]["localQuat"], s["bones"][n]["lr"])
            if qe > maxq:
                maxq = qe
        meane = tote / len(shared)
        compared += 1
        flag = "" if (maxe <= args.tol and maxq <= args.tol_quat) else "  <== OFF"
        if flag:
            bad += 1
        if maxe > worst[0]:
            worst = (maxe, s["state"], s["wantTime"])
        lines.append("%-22s %8.4f %-26s %5d %5d %12.3e %12.3e %10.3e%s" %
                     (s["state"], s["wantTime"], s["clipAsset"], len(amap), len(s["bones"]),
                      maxe, meane, maxq, flag))
        if flag:
            detail.append("  %s @ t=%.4f worst bone=%s err=%.6g" %
                          (s["state"], s["wantTime"], worst_bone, maxe))

    lines.append("")
    lines.append("compared samples: %d   failing: %d   worst: %s @ t=%.4f -> %.3e m" %
                 (compared, bad, worst[1], worst[2], worst[0]))
    lines += detail
    lines.append("VERDICT: " + ("PASS - the runtime plays the original per-frame skeleton data "
                               "(every paired bone within tolerance)" if bad == 0 and compared > 0 else
                               "FAIL - see the <== OFF rows above"))
    report = "\n".join(lines) + "\n"
    print(report)
    if args.out:
        os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
        with io.open(args.out, "w", encoding="utf-8") as fh:
            fh.write(report)
        print("report: " + args.out)
    return 0 if (bad == 0 and compared > 0) else 1


if __name__ == "__main__":
    sys.exit(main())
