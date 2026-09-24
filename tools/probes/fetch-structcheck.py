#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fetch-structcheck.py -- structural check of a fetched-asset quarantine dir.  HTTP 200 + a byte
count only proves "some bytes arrived"; these checks prove the bytes are the RIGHT KIND of
container (a WAV is a RIFF/WAVE, a TGA has a TGA header, a SPR is IDSP -- not, say, a 404 HTML
page that happened to arrive with a 200).

Provenance: written for the FIX-3 line-B fetch batch; moved here from .ai-tmp/test on
team-lead ruling ①.  The default dir is an ABSOLUTE constant on purpose -- the previous version
derived it from __file__, which would have silently pointed at tools/probes/fix3-fetch once the
script moved, produced an EMPTY table, and still exited 0.

Usage:
    python tools/probes/fetch-structcheck.py                      # default quarantine dir
    python tools/probes/fetch-structcheck.py --dir <d> --out <f>
    python tools/probes/fetch-structcheck.py --files a.spr,b.wav  # override the batch list

Default output = .ai-tmp/test/fix3-fetch/struct-check3.txt  (a NEW name on purpose, team-lead
ruling ②: struct-check2.txt is the evidence cited by .ai-tmp/test/fix3-consumer-audit.md and must
never be overwritten by a re-run).
"""
import argparse
import os
import struct

ROOT = r"C:\Work\Server\f-v2\clover-project-cs16"
DEFAULT_DIR = os.path.join(ROOT, ".ai-tmp", "test", "fix3-fetch")
DEFAULT_OUT = os.path.join(DEFAULT_DIR, "struct-check3.txt")

FILES = [
    "sniper_scope.spr", "ch_sniper.spr", "ch_sniper2.spr",
    "scope_arc_sw.tga", "scope_arc_ne.tga", "scope_arc_nw.tga", "scope_arc.tga",
    "usp_silencer_on.wav", "usp_silencer_off.wav",
    "_control-hud.txt", "_tailcontrol-hud.txt",
]

_ap = argparse.ArgumentParser(description="container-signature check for a fetched-asset dir")
_ap.add_argument("--dir", default=DEFAULT_DIR, help="quarantine dir to check")
_ap.add_argument("--out", default=DEFAULT_OUT, help="report path (default: a NEW file)")
_ap.add_argument("--files", default="", help="comma-separated override of the default batch list")
_args = _ap.parse_args()
QDIR = _args.dir
OUT = _args.out
if _args.files:
    FILES = [s.strip() for s in _args.files.split(",") if s.strip()]


log = []


def emit(s=""):
    log.append(s)
    print(s)


def tga_desc(path):
    with open(path, "rb") as fh:
        h = fh.read(18)
    idlen, cmap, imgtype = h[0], h[1], h[2]
    w, hgt, bpp = struct.unpack_from("<HHB", h, 12)
    return "type=%d cmap=%d idlen=%d %dx%d bpp=%d" % (imgtype, cmap, idlen, w, hgt, bpp)


emit("SENTINEL-FIX3-STRUCTCHECK-START 2026-09-24")
emit("dir = %s" % QDIR)
emit()
ok = 0
bad = 0
for name in FILES:
    p = os.path.join(QDIR, name)
    if not os.path.exists(p):
        emit("%-24s ABSENT" % name)
        bad += 1
        continue
    size = os.path.getsize(p)
    with open(p, "rb") as fh:
        head = fh.read(12)
    verdict = "?"
    if name.endswith(".spr"):
        verdict = "IDSP" if head[:4] == b"IDSP" else "NOT-IDSP(%r)" % head[:4]
    elif name.endswith(".wav"):
        if head[:4] == b"RIFF" and head[8:12] == b"WAVE":
            fmt = struct.unpack_from("<HH", open(p, "rb").read(24), 20)
            verdict = "RIFF/WAVE fmt=%d ch=%d" % fmt
        else:
            verdict = "NOT-WAVE(%r/%r)" % (head[:4], head[8:12])
    elif name.endswith(".tga"):
        verdict = tga_desc(p)
    elif name.endswith(".txt"):
        verdict = "text" if all(32 <= b < 127 or b in (9, 10, 13) for b in head) else "binary(%r)" % head
    good = not verdict.startswith("NOT-") and "?" != verdict
    emit("%-24s %7d B  %s" % (name, size, verdict))
    ok += 1 if good else 0
    bad += 0 if good else 1
emit()
emit("STRUCTCHECK: ok=%d bad=%d" % (ok, bad))
emit("RESULT-FIX3-STRUCTCHECK: %s" % ("PASS" if bad == 0 else "FAIL(%d)" % bad))

with open(OUT, "w", encoding="utf-8") as fh:
    fh.write("\n".join(log) + "\n")
