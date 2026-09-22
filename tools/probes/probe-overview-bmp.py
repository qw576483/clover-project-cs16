#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
probe-overview-bmp.py -- read the ORIGINAL CS 1.6 radar overview carrier and report,
                         with no guessing, the numbers needed to register it against the map.

WHY THIS EXISTS (slice cs16-AM)
-------------------------------
Slice AL fetched the original carrier `cstrike/overviews/de_dust2.bmp` (787,510 B) into
`原版资源/cs16src/cstrike/cstrike__overviews__de_dust2.bmp`, and slice AM fetched
`cstrike/overviews/de_dust2.txt` (162 B).  Before the BMP can be swapped in as the radar
underlay (`Core/ResPaths.cs` -> `UI/Art/overview_de_dust2`), one thing must be known:
**which world window does that image cover?**  `CsRadarWidget.Refresh` maps world -> radar
pixel with "the map bounding box, uniform scale, centred", so a mismatch would draw every
radar dot at the wrong place.

The `.txt` gives `ZOOM 1.50 / ORIGIN -223 1097 -192 / ROTATED 0 / HEIGHT -192` -- the
overview parameters, but NOT the world->pixel formula (that lives in the GoldSrc engine's
overview drawing code).  This probe therefore measures the image itself:

  * the exact BMP header (size / bpp / data offset),
  * the palette entry that fills the background (the map is drawn on a flat key colour),
  * the bounding box of "non background" pixels, i.e. the map's extent **inside the image**,
  * the implied units-per-pixel on each axis if that box were fitted linearly to the
    geometry's bounding box -- equal values mean "a uniform-scale window", very different
    values mean the image is NOT a uniform fit of the geometry bbox.

Output is a plain report on stdout; exit 0 = read fine, 2 = carrier missing/unreadable.
Nothing is written, nothing is changed -- this is a *measuring* script only.

Usage
-----
    python tools/probes/probe-overview-bmp.py
    python tools/probes/probe-overview-bmp.py --bmp <path> --geo <de_dust2_geo.bin>
"""

import argparse
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
DEFAULT_BMP = os.path.join(PROJECT_ROOT, "原版资源", "cs16src", "cstrike",
                           "cstrike__overviews__de_dust2.bmp")
DEFAULT_GEO = os.path.join(PROJECT_ROOT, "client", "Assets", "ThirdParty", "Dust2",
                           "de_dust2_geo.bin")
HL_UNIT = 0.0254                       # GoldSrc: 1 unit = 1 inch


def read_bmp(path):
    with open(path, "rb") as fh:
        d = fh.read()
    if d[:2] != b"BM":
        raise ValueError("not a BMP (signature %r)" % d[:2])
    data_off = struct.unpack_from("<I", d, 10)[0]
    dib, w, h, planes, bpp, comp, imgsz = struct.unpack_from("<IiiHHII", d, 14)
    if bpp != 8 or comp != 0 or dib < 40:
        raise ValueError("expected an uncompressed 8bpp paletted BMP, got bpp=%d comp=%d" % (bpp, comp))
    pal = d[14 + dib:14 + dib + 256 * 4]
    row = (w * bpp // 8 + 3) // 4 * 4
    return {"raw": d, "off": data_off, "w": w, "h": h, "bpp": bpp,
            "row": row, "pal": pal, "abs_h": abs(h), "bottom_up": h > 0}


def palette_rgb(pal, i):
    b, g, r, _a = pal[i * 4:i * 4 + 4]
    return (r, g, b)


def geo_bbox(path):
    """world min/max x,z in GoldSrc units, from the CD2G file header (see render-overview.py)."""
    with open(path, "rb") as fh:
        d = fh.read(4096)
    if d[:4] != b"CD2G":
        raise ValueError("geo file: bad magic %r" % d[:4])
    # offsets are taken verbatim from the reader in
    # client/Assets/Editor/MapGen/Dust2GeoData.cs (ReadFrom ... d.WorldMin / d.WorldMax):
    #   0 magic / 4 version / 8..35 seven f32 / 36 width / 40 depth / 44..67 WorldMin+WorldMax
    wmin = struct.unpack_from("<3f", d, 44)
    wmax = struct.unpack_from("<3f", d, 56)
    return wmin, wmax


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--bmp", default=DEFAULT_BMP)
    ap.add_argument("--geo", default=DEFAULT_GEO)
    args = ap.parse_args(argv)

    try:
        b = read_bmp(args.bmp)
    except (OSError, ValueError) as e:
        sys.stderr.write("cannot read overview bmp: %s\n" % e)
        return 2

    w, h, row, off, abs_h, pal = b["w"], b["h"], b["row"], b["off"], b["abs_h"], b["pal"]
    print("carrier : %s" % args.bmp)
    print("header  : %dx%d  %dbpp  dataOffset=%d  rowBytes=%d  %s"
          % (w, h, b["bpp"], off, row, "bottom-up" if b["bottom_up"] else "top-down"))

    # palette histogram (index usage)
    hist = {}
    for fy in range(abs_h):
        base = off + fy * row
        for v in b["raw"][base:base + w]:
            hist[v] = hist.get(v, 0) + 1
    top = sorted(hist.items(), key=lambda kv: -kv[1])
    print("palette : %d distinct indices over %d px" % (len(hist), w * abs_h))
    for idx, c in top[:6]:
        print("          idx %3d  %8d px (%5.1f%%)  rgb%s"
              % (idx, c, 100.0 * c / (w * abs_h), palette_rgb(pal, idx)))

    bg = top[0][0]
    print("background index (the map is drawn on it) = %d  rgb%s" % (bg, palette_rgb(pal, bg)))

    # bounding box of "not background", in top-down image coordinates
    minx, maxx, miny, maxy, n_ng = 10**9, -1, 10**9, -1, 0
    for fy in range(abs_h):
        iy = abs_h - 1 - fy if b["bottom_up"] else fy
        base = off + fy * row
        line = b["raw"][base:base + w]
        for x, v in enumerate(line):
            if v == bg:
                continue
            n_ng += 1
            if x < minx: minx = x
            if x > maxx: maxx = x
            if iy < miny: miny = iy
            if iy > maxy: maxy = iy
    if n_ng == 0:
        sys.stderr.write("image is a single flat colour -- not an overview\n")
        return 2
    bw, bh = maxx - minx + 1, maxy - miny + 1
    print("map ink : %d px (%.1f%%)   bbox x[%d..%d] w=%d   y[%d..%d] h=%d   aspect=%.4f"
          % (n_ng, 100.0 * n_ng / (w * abs_h), minx, maxx, bw, miny, maxy, bh,
             float(bw) / bh))

    try:
        wmin, wmax = geo_bbox(args.geo)
    except (OSError, ValueError) as e:
        print("geometry: not readable (%s) -- cannot compare windows" % e)
        return 0
    span_x = (wmax[0] - wmin[0]) / HL_UNIT
    span_z = (wmax[2] - wmin[2]) / HL_UNIT
    print("geometry: x[%.4f..%.4f] z[%.4f..%.4f] m  => %.0f x %.0f units  aspect=%.4f"
          % (wmin[0], wmax[0], wmin[2], wmax[2], span_x, span_z, span_x / span_z))
    print("implied : if the ink bbox were fitted linearly to the geometry bbox,")
    print("          x = %.3f units/px   z = %.3f units/px   (equal => uniform window;"
          " this carrier: %s)"
          % (span_x / bw, span_z / bh,
             "UNIFORM" if abs(span_x / bw - span_z / bh) < 0.02 * (span_x / bw) else "ANISOTROPIC"))
    print("verdict : the original BMP's world window is NOT given by the .txt alone --")
    print("          register it (scale+offset search against the geometry silhouette) before")
    print("          swapping it in for UI/Art/overview_de_dust2, see 原版资源/清单.md (切片AM).")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
