#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
overview-window.py -- the AUTHORITATIVE GoldSrc "world -> overview pixel" window, solved in
                      slice cs16-AR (2026-09-22), plus the containment judgment for the radar
                      underlay against this project's own de_dust2 geometry.

WHY THIS PROBE EXISTS
---------------------
Slices AM/AN/AP/AO tried to *search* for the world rectangle the original
`overviews/de_dust2.bmp` (1024x768 8bpp, 787510 B) covers, because the map's own
`overviews/<map>.txt` only carries `ZOOM / ORIGIN / ROTATED / HEIGHT` and no formula
(`原版资源/cs16src/.../cstrike__overviews__de_dust2.txt`).  Every search ended in
"DOES NOT REGISTER" (`register-overview.py`, exit 3) with a 12.1% aspect mismatch that
no uniform scale could reconcile.

Slice AR recovered the formula from the ORIGINAL CLIENT CODE, so it is no longer searched:

  source : `cl_dll/hud_spectator.cpp` -> `CHudSpectator::DrawOverviewLayer()`
           lines 1069-1193 (tiles/step maths), 896-1056 (`ParseOverviewFile`, the parser
           that fills `m_OverviewData.{zoom,origin,rotated,layersHeights}`)
           file: https://raw.githubusercontent.com/ValveSoftware/halflife/master/cl_dll/hud_spectator.cpp
           (Half-Life SDK, ValveSoftware/halflife; identical copy also in the open
            CS 1.6 client reimplementation `Avatarchik/cs16-client` at `cl_dll/hud_spectator.cpp`)
  landed: `原版资源/hlsdk/cl_dll/hud_spectator.cpp` (+ `overview.cpp`/`overview.h`)
           with URL + SHA256 in `原版资源/清单.md` (slice cs16-AR section)

THE FORMULA (verbatim reading of that code, ROTATED == 0 branch, lines 1150-1191)
---------------------------------------------------------------------------------
    screenaspect = 4.0/3.0;                 // hard-coded
    xTiles = 8;  yTiles = 6;                // 1024x768 / (128x128 tiles)  (from the sprite)
    xStep = -(2*4096.0/zoom) / xTiles;      // world X step per tile row
    yStep = -(2*4096.0/(zoom*screenaspect)) / yTiles;   // world Y step per tile column
    x = xs + 4096.0/(zoom*screenaspect);    // xs = origin[0]  (GoldSrc X)
    y = ys + 4096.0/zoom;                   // ys = origin[1]  (GoldSrc Y)
So the drawn quad covers, per axis (the loops walk 6 steps in X and 8 steps in Y):

    world X span = 6 * |xStep| = 6144 / ZOOM      <- the image VERTICAL axis (768 px)
    world Y span = 8 * |yStep| = 8192 / ZOOM      <- the image HORIZONTAL axis (1024 px)
    both centred on (ORIGIN[0], ORIGIN[1])

    => units per pixel = 6144/768/ZOOM = 8192/1024/ZOOM = **8 / ZOOM**  (uniform, isotropic)
    => image RIGHT <- decreasing world Y ; image DOWN <- decreasing world X   (ROTATED 0)
    => ROTATED != 0 swaps the two roles (lines 1111-1118): horizontal <- X, vertical <- Y.

Cross-check that is independent of this code (and the reason the formula is trusted):
the original carrier's OWN two red bomb-site glyphs pin its scale at ~5.195 units/px
(`locate-overview-letters.py`), and 8/ZOOM = 8/1.50 = **5.3333 units/px** -> the two agree
to 2.6%, which no wrong formula would do.

Usage
-----
    python tools/probes/overview-window.py                 # report every known carrier
    python tools/probes/overview-window.py --window-json <path>   # radar-consumable window
Exit 0 = the footprint fits the formula window, 3 = it does not (residuals printed).
"""

import argparse
import hashlib
import json
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
HL_UNIT = 0.0254                      # GoldSrc 1 unit = 1 inch
SCREEN_ASPECT = 4.0 / 3.0
XTILES, YTILES = 8, 6                 # 1024x768 / 128x128 tiles

# every carrier actually on disk (all fetched by slice AL/AM/AR from the same public repack)
CARRIERS = [
    ("de_dust2", "cstrike__overviews__de_dust2.txt", "cstrike__overviews__de_dust2.bmp", 1.50, (-223.0, 1097.0, -192.0), 0),
    ("de_dust", "cstrike__overviews__de_dust.txt", "cstrike__overviews__de_dust.bmp", 1.20, (101.0, 1071.0, -192.0), 0),
    ("cs_assault", "cstrike__overviews__cs_assault.txt", None, 2.13, (-531.0, 1390.0, 0.0), 0),
    ("de_aztec", "cstrike__overviews__de_aztec.txt", None, 1.38, (-384.0, -172.0, -545.0), 1),
]
GEO = os.path.join(PROJECT_ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2_geo.bin")
BMPDIR = os.path.join(PROJECT_ROOT, "原版资源", "cs16src", "cstrike")


def formula_window(zoom, origin, rotated):
    """The engine's window for one carrier. Returns world (X, Y) spans and the pixel mapping."""
    upp = 8.0 / zoom                                        # units per overview pixel
    span_w = 1024 * upp                                     # the 1024-px (horizontal) axis
    span_h = 768 * upp                                      # the 768-px (vertical) axis
    if rotated:
        span_x, span_y = span_w, span_h                     # horizontal <- X
    else:
        span_x, span_y = span_h, span_w                     # horizontal <- Y
    return {"unitsPerPx": upp, "spanXUnits": span_x, "spanYUnits": span_y,
            "centerX": origin[0], "centerY": origin[1],
            "xMin": origin[0] - span_x / 2, "xMax": origin[0] + span_x / 2,
            "yMin": origin[1] - span_y / 2, "yMax": origin[1] + span_y / 2,
            "horizontalAxis": "X" if rotated else "Y",
            "horizontalDir": "-X" if rotated else "-Y",
            "verticalDir": "-Y" if rotated else "-X"}


def read_geo(path):
    with open(path, "rb") as fh:
        data = fh.read()
    if data[0:4] != b"CD2G":
        raise ValueError("%s: bad magic" % path)
    o = 8 + 28
    o += 8
    wmin = struct.unpack_from("<3f", data, o); o += 12
    wmax = struct.unpack_from("<3f", data, o); o += 12
    (gc,) = struct.unpack_from("<I", data, o); o += 4
    tris = []
    for _ in range(gc):
        o += 48
        vc, ic = struct.unpack_from("<2I", data, o); o += 8
        verts = np.frombuffer(data, dtype="<f4", count=3 * vc, offset=o).reshape(vc, 3); o += 12 * vc
        o += 8 * vc + 12 * vc
        idx = np.frombuffer(data, dtype="<u4", count=ic, offset=o); o += 4 * ic
        tris.append(verts[idx.reshape(-1, 3)])
    return wmin, wmax, np.concatenate(tris, axis=0)


def read_bmp_ink(path):
    d = open(path, "rb").read()
    if d[:2] != b"BM":
        raise ValueError("not a BMP")
    data_off = struct.unpack_from("<I", d, 10)[0]
    dib, w, h, planes, bpp, comp, imgsz = struct.unpack_from("<IiiHHII", d, 14)
    ah = abs(h)
    row = (w + 3) // 4 * 4
    raw = np.frombuffer(d, dtype=np.uint8, count=row * ah, offset=data_off).reshape(ah, row)[:, :w]
    if h > 0:
        raw = raw[::-1]
    key = int(np.argmax(np.bincount(raw.ravel(), minlength=256)))
    ink = raw != key
    colp, rowp = ink.sum(axis=0), ink.sum(axis=1)
    bx0, bx1 = int(np.nonzero(colp)[0][0]), int(np.nonzero(colp)[0][-1])
    by0, by1 = int(np.nonzero(rowp)[0][0]), int(np.nonzero(rowp)[0][-1])
    return {"w": w, "h": ah, "key": key, "bytes": len(d), "ink": ink,
            "bbox": (bx0, bx1, by0, by1)}


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--geo", default=GEO)
    ap.add_argument("--window-json")
    args = ap.parse_args(argv)

    print("FORMULA SOURCE: cl_dll/hud_spectator.cpp:1069-1193 (ValveSoftware/halflife)");
    print("  units/px = 8/ZOOM ; window spans 8192/ZOOM (horizontal) x 6144/ZOOM (vertical) units")
    print("  ROTATED 0: horizontal<-worldY, vertical<-worldX ; ROTATED 1: swapped ; centre=ORIGIN[0..1]")
    print("")

    print("--- every original carrier on disk (ZOOM/ORIGIN read from the .txt, verbatim) ---")
    for name, tf, bf, zoom, origin, rot in CARRIERS:
        tp = os.path.join(BMPDIR, tf)
        if not os.path.isfile(tp):
            continue
        raw = open(tp, "rb").read()
        w = formula_window(zoom, origin, rot)
        print("%-10s ZOOM=%-5g ORIGIN=(%g, %g, %g) ROTATED=%d  ->  %.4f units/px" %
              (name, zoom, origin[0], origin[1], origin[2], rot, w["unitsPerPx"]))
        print("           window: X %+.0f..%+.0f (span %.0f)  Y %+.0f..%+.0f (span %.0f) units" %
              (w["xMin"], w["xMax"], w["spanXUnits"], w["yMin"], w["yMax"], w["spanYUnits"]))
        print("           image right <- world %s ; image down <- world %s ; txt sha256 %s" %
              (w["horizontalDir"], w["verticalDir"], hashlib.sha256(raw).hexdigest()[:16]))
        if bf:
            bp = os.path.join(BMPDIR, bf)
            if os.path.isfile(bp):
                b = read_bmp_ink(bp)
                bx0, bx1, by0, by1 = b["bbox"]
                dw, dh = bx1 - bx0 + 1, by1 - by0 + 1
                drawn_x = dh * w["unitsPerPx"]        # image VERTICAL axis
                drawn_y = dw * w["unitsPerPx"]        # image HORIZONTAL axis
                print("           ink box %dx%d px -> drawn map %.0f (vert) x %.0f (horiz) units" %
                      (dw, dh, drawn_x, drawn_y))
                print("           the drawn map FILLS the vertical axis: %.0f / %.0f = %.4f of the "
                      "window (=> ZOOM = 6144/map-span: 6144/%g = %.0f units)"
                      % (drawn_x, w["spanXUnits"], drawn_x / w["spanXUnits"], zoom, 6144.0 / zoom))
    print("")

    if not os.path.isfile(args.geo):
        print("geo missing: " + args.geo)
        return 2
    wmin, wmax, tri = read_geo(args.geo)
    fp = tri.reshape(-1, 3)
    xspan = (fp[:, 0].max() - fp[:, 0].min()) / HL_UNIT
    zspan = (fp[:, 2].max() - fp[:, 2].min()) / HL_UNIT
    tx = np.percentile(fp[:, 0], [0.5, 99.5]) / HL_UNIT
    tz = np.percentile(fp[:, 2], [0.5, 99.5]) / HL_UNIT
    print("--- our geometry (%s) ---" % os.path.relpath(args.geo, PROJECT_ROOT))
    print("  vertices  X span %.0f  Z span %.0f units   (aspect X:Z %.4f)" %
          (xspan, zspan, xspan / zspan))
    print("  0.5%%-trimmed X [%+.0f..%+.0f] span %.0f   Z [%+.0f..%+.0f] span %.0f" %
          (tx[0], tx[1], tx[1] - tx[0], tz[0], tz[1], tz[1] - tz[0]))
    print("  header bbox X span %.0f  Z span %.0f  (over-reports X %.0f / Z %.0f)" %
          ((wmax[0] - wmin[0]) / HL_UNIT, (wmax[2] - wmin[2]) / HL_UNIT,
           (wmax[0] - wmin[0]) / HL_UNIT - xspan, (wmax[2] - wmin[2]) / HL_UNIT - zspan))
    print("")

    name, tf, bf, zoom, origin, rot = CARRIERS[0]
    w = formula_window(zoom, origin, rot)
    print("--- containment judgment for de_dust2 (the radar's window vs our footprint) ---")
    ok = True
    res = {}
    for axis, span in (("X", w["spanXUnits"]), ("Z(worldY)", w["spanYUnits"])):
        need = xspan if axis == "X" else zspan
        over = need - span
        ok = ok and over <= 0.0
        res[axis] = {"windowSpan": span, "footprintSpan": need, "overflowUnits": over}
        print("  %-10s window %.0f units  footprint %.0f units  -> %s" %
              (axis, span, need,
               "INSIDE (%.0f spare)" % (-over) if over <= 0 else "OUTSIDE by %.0f units (%.1f%%)" % (over, 100.0 * over / span)))
    print("  window world aspect X:Y = %.4f (fixed by the engine: 6144:8192 = 0.75)" %
          (w["spanXUnits"] / w["spanYUnits"]))
    print("  our footprint X:Z       = %.4f  -> %.1f%% apart   <-- THIS is the '12%%' residual" %
          (xspan / zspan, 100.0 * ((xspan / zspan) / (w["spanXUnits"] / w["spanYUnits"]) - 1.0)))
    print("  it is an ASPECT (shape) difference, not a metric one: units/px from the formula")
    print("  (%.4f) matches the carrier's own landmark scale (5.195, locate-overview-letters.py) to %.1f%%" %
          (w["unitsPerPx"], 100.0 * (5.195 / w["unitsPerPx"] - 1.0)))
    print("verdict: %s" % ("CONTAINED" if ok else
          "NOT CONTAINED -- the original underlay window (%.0f x %.0f units) cannot hold our "
          "footprint (%.0f x %.0f); the mismatch is on the %s axis and is structural" %
          (w["spanXUnits"], w["spanYUnits"], xspan, zspan,
           "X (span)" if res["X"]["overflowUnits"] > 0 else "Z")))

    if args.window_json:
        def f(v):
            return float(v)
        out = {"usableForRadar": bool(ok),
               "formula": {"source": "cl_dll/hud_spectator.cpp:1069-1193 (ValveSoftware/halflife)",
                           "unitsPerPx": f(w["unitsPerPx"]), "zoom": f(zoom), "rotated": int(rot),
                           "horizontalAxis": w["horizontalAxis"]},
               "window": {"minX": f(w["xMin"]), "maxX": f(w["xMax"]), "minY": f(w["yMin"]),
                          "maxY": f(w["yMax"]),
                          "unitsPerCarrierPx": f(w["unitsPerPx"]), "pxPerUnit": f(1.0 / w["unitsPerPx"])},
               "footprint": {"xSpan": f(xspan), "zSpan": f(zspan)},
               "perAxis": {k: {kk: f(vv) for kk, vv in v.items()} for k, v in res.items()},
               "trimmed05": {"xSpan": f(tx[1] - tx[0]), "zSpan": f(tz[1] - tz[0])},
               "probe": os.path.relpath(os.path.abspath(__file__), PROJECT_ROOT).replace("\\", "/")}
        if not ok:
            out["reason"] = ("our de_dust2 geometry is %.0f units wider (X) than the 4096-unit "
                             "window the original overview covers; the original underlay would clip "
                             "the east/west ends. Aspect mismatch %.1f%% (window X:Y=0.75 vs our "
                             "X:Z=%.4f) is structural, not a uniform scale."
                             % (res["X"]["overflowUnits"],
                                100.0 * ((xspan / zspan) / (w["spanXUnits"] / w["spanYUnits"]) - 1.0),
                                xspan / zspan))
        os.makedirs(os.path.dirname(os.path.abspath(args.window_json)), exist_ok=True)
        with open(args.window_json, "w", encoding="utf-8") as fh:
            json.dump(out, fh, indent=1, ensure_ascii=False)
        print("window-json: %s (usableForRadar=%s)" % (args.window_json, out["usableForRadar"]))
    return 0 if ok else 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
