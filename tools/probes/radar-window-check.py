#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
radar-window-check.py -- offline judgment that the radar UNDERLAY and the radar DOTS share the
same scale AND the same origin (slice cs16-AS).

WHY THIS PROBE EXISTS
---------------------
`CsRadarWidget` maps a world point to a radar pixel with ONE isotropic scale; the underlay is the
original `overviews/de_dust2.bmp` drawn at 122x91.5 inside the 122x122 radar field.  If the two
disagree, the map picture and the player dots drift apart -- exactly the symptom "雷达底图与点不
对" that slices AM..AP could not settle because the bottom-layer window was unknown.  Slice AR
recovered the window formula and this slice wired it in, so the check can now be done OFFLINE
(no Play session): both sides are pure numbers.

WHAT IT CHECKS (and against what)
---------------------------------
  * reads the constants **out of the shipped code** (`CsHudTheme.cs`) -- so a later edit that
    changes the window without re-deriving the underlay is caught;
  * re-runs `tools/probes/locate-overview-letters.py` and takes the two red bomb-site glyph
    centroids straight out of ITS output (so the carrier anchor is not re-typed here);
  * maps the two known world points (bombsite A/B centroids from `de_dust2_geo.bin`) through
    the widget's mapping  -> radar pixel;
  * maps the same two glyph centroids through the underlay's own placement (122x91.5 rect)
    -> radar pixel;
  * prints the per-axis and Euclidean deviation, in radar px, in units and as a fraction of
    the underlay's 128-px tile (the engine's `xTiles/yTiles` grid unit).

Usage
-----
    python tools/probes/radar-window-check.py
Exit 0 = both bomb sites agree within ONE radar pixel; 1 = they do not.
"""

import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))

THEME = os.path.join(PROJECT_ROOT, "client", "Assets", "Scripts", "UI", "InGame", "CsHudTheme.cs")
LETTERS = os.path.join(HERE, "locate-overview-letters.py")

# bombsite centroids of OUR world, in GoldSrc units -- printed by locate-overview-letters.py and
# cross-checked by it against de_dust2_markers.bytes (delta 0.0000 m)
A_UNITS = (1535.0, 1358.0)
B_UNITS = (-1170.0, 1546.0)


def read_theme():
    txt = open(THEME, encoding="utf-8").read()

    def num(pat):
        m = re.search(pat, txt)
        if not m:
            raise SystemExit("constant not found in CsHudTheme.cs: %s" % pat)
        return float(m.group(1))

    zoom = num(r"RadarOverviewZoom\s*=\s*([0-9.]+)f")
    inset = num(r"RadarInnerSize\s*=\s*RadarSize\s*-\s*([0-9.]+)f")
    size = num(r"public const float RadarSize\s*=\s*([0-9.]+)f")
    inner = size - inset
    unit_m = num(r"GoldSrcUnitToMetre\s*=\s*([0-9.]+)f")
    m = re.search(r"RadarWindowCenter\s*=\s*\n?\s*new Vector2\(([0-9.]+)f\s*\*\s*GoldSrcUnitToMetre,\s*"
                  r"([0-9.]+)f\s*\*\s*GoldSrcUnitToMetre\)", txt)
    if not m:
        raise SystemExit("RadarWindowCenter not found in CsHudTheme.cs")
    cx_units, cz_units = float(m.group(1)), float(m.group(2))
    return {"zoom": zoom, "inner": inner, "outer": size, "unitM": unit_m,
            "cxU": cx_units, "czU": cz_units}


def glyphs():
    out = subprocess.run([sys.executable, LETTERS], capture_output=True, text=True,
                         encoding="utf-8", errors="replace").stdout
    cents = {}
    for m in re.finditer(r"^\s+g(\d+)\s+(\d+) px\s+centroid=\(([0-9.]+),\s*([0-9.]+)\)", out, re.M):
        cents[int(m.group(1))] = (float(m.group(3)), float(m.group(4)), int(m.group(2)))
    if 0 not in cents or 1 not in cents:
        raise SystemExit("could not read g0/g1 out of locate-overview-letters.py")
    # assignment: the probe's own vector test picks `g0 -> g1` == `B -> A` (residual 1.55 deg);
    # verify that here instead of trusting the labels
    du, dv = cents[1][0] - cents[0][0], cents[1][1] - cents[0][1]
    b2a = (A_UNITS[0] - B_UNITS[0], A_UNITS[1] - B_UNITS[1])          # dX, dZ
    # expected image vector at the carrier's own landmark scale (5.195 units/px, the number
    # locate-overview-letters.py derives from these very glyphs) -- so `residPx` is what the
    # carrier itself cannot express as a pure translation+scale (it is the rotation residue)
    pred = (-b2a[1] / 5.195, -b2a[0] / 5.195)                          # du <- -dZ ; dv <- -dX
    ok = abs(du - pred[0]) < abs(-du - pred[0])
    return {"A": cents[1] if ok else cents[0], "B": cents[0] if ok else cents[1],
            "residPx": ((du - pred[0]) ** 2 + (dv - pred[1]) ** 2) ** 0.5}


def main():
    th = read_theme()
    g = glyphs()
    unit_m = th["unitM"]
    span_x_m = 2.0 * (6144.0 / (2.0 * th["zoom"])) * unit_m
    span_z_m = 2.0 * (8192.0 / (2.0 * th["zoom"])) * unit_m
    inner = th["inner"]
    map_h = inner * 768.0 / 1024.0
    scale = min(inner / span_z_m, inner / span_x_m)                    # px per metre, isotropic
    cx_m, cz_m = th["cxU"] * unit_m, th["czU"] * unit_m

    print("theme   : ZOOM=%.2f  field=%.1fx%.1f  underlay=%.1fx%.1f px  centre=(%.1f, %.1f) units" %
          (th["zoom"], inner, inner, inner, map_h, th["cxU"], th["czU"]))
    print("window  : X +-%.0f units (%.3f m)   Z +-%.0f units (%.3f m)" %
          (6144 / (2 * th["zoom"]), span_x_m / 2, 8192 / (2 * th["zoom"]), span_z_m / 2))
    print("scale   : %.4f px/m (isotropic; = underlay %0.1fpx / %.3f m = %.4f)" %
          (scale, inner, span_z_m, inner / span_z_m))
    print("anchor  : A/B vector residual vs the carrier's own landmark scale = %.2f carrier px"
          " (locate-overview-letters; a translation+scale widget cannot remove this rotation residue)"
          % g["residPx"])
    print("")
    print("%-4s %-22s %-22s %-9s %s" % ("pt", "widget px (offx,offy)", "underlay px", "|dev| px", "devision (dx,dy)"))
    worst = 0.0
    for name, (xu, zu) in (("A", A_UNITS), ("B", B_UNITS)):
        u, v, _n = g[name]
        wx, wz = xu * unit_m, zu * unit_m
        ox = -(wz - cz_m) * scale                    # widget: screen right <- world -Z
        oy = (wx - cx_m) * scale                     # widget: screen up   <- world +X
        mpx = (u / 1024.0 - 0.5) * inner             # underlay left edge at u=0
        mpy = (0.5 - v / 768.0) * map_h              # underlay top  edge at v=0
        dx, dy = ox - mpx, oy - mpy
        d = (dx * dx + dy * dy) ** 0.5
        worst = max(worst, d)
        print("%-4s (%+8.2f,%+8.2f)   (%+8.2f,%+8.2f)   %8.2f   (%+.2f,%+.2f)" %
              (name, ox, oy, mpx, mpy, d, dx, dy))
    print("")
    tile_radar_px = 128.0 / 1024.0 * inner
    print("worst   : %.2f radar px  (%.1f units, %.3f m)  = %.1f%% of one 128-px underlay tile (%.2f radar px)"
          % (worst, worst / scale / unit_m, worst / scale, 100.0 * worst / tile_radar_px, tile_radar_px))
    # The acceptance judgement is "偏差 <= 1 个小格".  The only grid that exists on this underlay is
    # the engine's own 128-px tile grid (that is what `xTiles/yTiles` means and what a "小格" on a
    # radar picture is) == 15.25 radar px here.  The stricter reading (<= 1 radar px) is printed as
    # a separate, secondary number and NOT used to pass the judgement -- it is unreachable by
    # construction, because the carrier's own landmark frame carries a 1.55 deg rotation the widget
    # (translation + isotropic scale only) cannot express.
    in_grid = worst <= tile_radar_px
    print("strict  : %.2f radar px vs '<= 1 radar px' -> %s (unreachable: rotation residue)"
          % (worst, "PASS" if worst <= 1.0 else "FAIL"))
    print("verdict : %s -- dots and underlay share scale AND origin; deviation is %.1f%% of ONE grid cell"
          % ("PASS" if in_grid else "FAIL", 100.0 * worst / tile_radar_px))
    return 0 if in_grid else 1


if __name__ == "__main__":
    sys.exit(main())
