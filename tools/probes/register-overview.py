#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
register-overview.py -- try to register the ORIGINAL CS 1.6 radar underlay
                        (`cstrike/overviews/de_dust2.bmp`, 1024x768 8bpp, 787510 B)
                        against this project's own copy of the ORIGINAL geometry
                        (`client/Assets/ThirdParty/Dust2/de_dust2_geo.bin`), and report
                        the window + residual -- or a quantified "does not register".

WHY THIS EXISTS (slice cs16-AN)
-------------------------------
Slice AM measured the carrier: its non-key-colour ink box is 808x764 px while this
project's geometry bbox is 4864x5568 units, so the naive "ink box == geometry box" fit
implies 6.020 units/px on one axis and 7.288 on the other (ANISOTROPIC).  The carrier's
own `.txt` carries no world->pixel formula, so this probe *solves* for the window by
maximising the intersection-over-union between

  A) the BMP's own ink mask (pixels != the key index; the map is drawn on a flat key colour)
  B) the projected footprint of the ORIGINAL geometry (same file the scene builder uses)

over: one uniform scale (units per carrier pixel -- a GoldSrc overview is one view with
one scale, so the SAME scale must serve both image axes), a translation, which world axis
feeds which image axis (u<->X / u<->Z), and the two image mirrors.  Nothing is fitted per
axis and nothing is hand-drawn.

The translation is found exactly (not by hill climbing) with an FFT cross-correlation of
the two binary masks, so the only searched dimensions are scale + axis mapping + mirror.

Source of the model (no invented API / no invented formula)
-----------------------------------------------------------
* `原版资源/cs16src/cstrike/overviews/de_dust2.txt` (162 B, verbatim):
      global { ZOOM 1.50  ORIGIN -223 1097 -192  ROTATED 0 }
      layer  { IMAGE "overviews/de_dust2.bmp"  HEIGHT -192 }
  => exactly four numbers, no formula.  ORIGIN's third component (-192) is a height (it
  repeats the layer's `HEIGHT -192`); ORIGIN's first two components are the two HORIZONTAL
  GoldSrc world axes (GoldSrc: X east, Y north, Z up).  The printed window-span / ZOOM
  ratio is the offset-free cross-check against those numbers.
* The BMP itself is the second source: header (1024x768, 8bpp, palette, key index) and ink
  mask are *measured*, never assumed.

WHAT SLICE AP ADDED (2026-09-22) -- the window is a JUDGED DELIVERABLE, not an IoU
-------------------------------------------------------------------------------
The radar needs "the world rectangle the underlay image covers" as a NUMBER
({minX, maxX, minZ, maxZ} + pixels per metre), so this probe now emits exactly that, and it
judges a window the only way that matters for a radar underlay:

    ** the geometry footprint must not spill outside the carrier's ink box **

IoU is kept as a secondary number only, because an IoU is NOT containment: the original code
sampled the footprint on the carrier's own grid and set every out-of-image sample to
"background", so a footprint that is TOO BIG to fit was silently clipped and still scored
0.84.  A window whose footprint spills out draws dots off the underlay, and clipping hides it.

Two windows are solved and both are reported:
  * `silhouette` -- the uniform-scale fit maximising IoU (the original search);
  * `landmarks`  -- the carrier's OWN scale, from the two red site glyphs burned into the BMP
    plus this project's marker table (same measurement as locate-overview-letters.py, called
    from here so the rule has one home).  No search involved.
Both are printed as a world rectangle, with per-edge spill of the footprint out of the ink
box (px and world units) and the uniform scale that containment would need.

Measured on this machine (2026-09-22): neither window contains the footprint --
the carrier's own landmarks pin its scale at 5.195 units/px while containment needs 6.574,
i.e. 26.5%, and the carrier's ink box and this geometry's footprint differ in ASPECT by 12.1%
(1.0576 vs 1.1857), which no uniform scale can reconcile.  Verdict = DOES NOT REGISTER, and
the carrier must NOT be swapped in.

Usage
-----
    python tools/probes/register-overview.py [--dump <png>] [--json <json>] [--tw 256]
Exit 0 = a window CONTAINS the footprint, 3 = no window registers (residuals reported),
2 = bad input.
"""

import argparse
import json
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
DEFAULT_BMP = os.path.join(PROJECT_ROOT, "原版资源", "cs16src", "cstrike",
                           "cstrike__overviews__de_dust2.bmp")
DEFAULT_GEO = os.path.join(PROJECT_ROOT, "client", "Assets", "ThirdParty", "Dust2",
                           "de_dust2_geo.bin")
HL_UNIT = 0.0254                       # GoldSrc: 1 unit = 1 inch

TXT_ZOOM = 1.50                        # `overviews/de_dust2.txt`, verbatim
TXT_ORIGIN = (-223.0, 1097.0, -192.0)
TXT_ROTATED = 0

RASTER_N = 2048                        # footprint raster resolution


def read_bmp(path):
    with open(path, "rb") as fh:
        d = fh.read()
    if d[:2] != b"BM":
        raise ValueError("not a BMP (signature %r)" % d[:2])
    data_off = struct.unpack_from("<I", d, 10)[0]
    dib, w, h, planes, bpp, comp, imgsz = struct.unpack_from("<IiiHHII", d, 14)
    if bpp != 8 or comp != 0 or dib < 40:
        raise ValueError("expected uncompressed 8bpp paletted BMP, got bpp=%d comp=%d" % (bpp, comp))
    abs_h = abs(h)
    row = (w + 3) // 4 * 4
    raw = np.frombuffer(d, dtype=np.uint8, count=row * abs_h, offset=data_off)
    raw = raw.reshape(abs_h, row)[:, :w]
    if h > 0:
        raw = raw[::-1]                                # bottom-up BMP -> top-down rows
    return {"path": path, "w": w, "h": abs_h, "bytes": len(d), "data_off": data_off,
            "row": row, "img": raw}


def read_geo(path):
    """vertex/index/bbox reader, layout taken from Editor/MapGen/Dust2GeoData.cs."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[0:4] != b"CD2G":
        raise ValueError("%s: bad magic" % path)
    o = 8 + 28
    o += 8                                          # width, depth
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
    return {"wmin": wmin, "wmax": wmax, "tri": np.concatenate(tris, axis=0), "file": path}


def raster_footprint(tri, half, n=RASTER_N):
    """Binary footprint of every original triangle projected on the (x, z) plane.

    Raster pixel (ix, iy) centre = world x = -half + (ix+0.5)*mpp, world z = +half -
    (iy+0.5)*mpp (row 0 = the north-most z).  mpp in metres.
    """
    mpp = 2.0 * half / n
    xs = (tri[:, :, 0] + half) / mpp
    zs = (half - tri[:, :, 2]) / mpp
    img = Image.new("1", (n, n), 0)
    dr = ImageDraw.Draw(img)
    for k in range(len(xs)):
        dr.polygon([(float(xs[k, i]), float(zs[k, i])) for i in range(3)], fill=1)
    return np.array(img, dtype=bool), mpp


def iou(a, b):
    uni = np.count_nonzero(a | b)
    return (np.count_nonzero(a & b) / uni) if uni else 0.0


def bbox_of(mask):
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return None
    return int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())


def vertex_bbox(tri):
    """The geometry's REAL extent in world (X, Z) metres -- the header bbox is stale.

    What the header carries: `CD2G` version 1 writes `worldMin/worldMax` computed by the
    generator; on this file that box is X +-2432 / Z +-2784 units while the vertices only
    reach X[-2176..2304] / Z[-2656..2656].  The aspect line below used to be computed from
    the header, i.e. from a box 385 units wider and 256 units deeper than the geometry the
    footprint is rasterised from -- measured 2026-09-22, slice AP.  Both are printed now.
    """
    p = tri.reshape(-1, 3)
    return {"X": (float(p[:, 0].min()), float(p[:, 0].max())),
            "Z": (float(p[:, 2].min()), float(p[:, 2].max()))}


def px_rect_to_world(rect_px, swap, fu, fv, A, B, sm):
    """Map a carrier-pixel rect to the world (X, Z) metres it covers under one window.

    Same parameterisation as `sample()` above: world u axis = A - u*sm (fu=1) / A + u*sm
    (fu=0), world v axis = B + v*sm (fv=1) / B - v*sm (fv=0); with `swap` the u axis is world
    Z and the v axis is world X (the axis pair slice AO settled).
    """
    x0, x1, y0, y1 = rect_px

    def U(u):
        return A - u * sm if fu else A + u * sm

    def V(v):
        return B + v * sm if fv else B - v * sm

    ua, ub = sorted((U(x0), U(x1)))
    va, vb = sorted((V(y0), V(y1)))
    return {"X": (va, vb), "Z": (ua, ub)} if swap else {"X": (ua, ub), "Z": (va, vb)}


def window_report(tag, swap, fu, fv, A, B, sm, W, H, bmp_rect, fp_bbox):
    """Print one window as the radar needs it, and judge it by CONTAINMENT (not IoU)."""
    win = px_rect_to_world((0, W, 0, H), swap, fu, fv, A, B, sm)
    ink = px_rect_to_world(bmp_rect, swap, fu, fv, A, B, sm)
    u_per_px = sm / HL_UNIT
    px_per_m = 1.0 / sm
    print("window[%s]: world X [%+.0f .. %+.0f]  Z [%+.0f .. %+.0f] units   (u<-%s mirror_u=%d "
          "mirror_v=%d)" % (tag, win["X"][0] / HL_UNIT, win["X"][1] / HL_UNIT,
                            win["Z"][0] / HL_UNIT, win["Z"][1] / HL_UNIT,
                            "Z" if swap else "X", fu, fv))
    print("            scale %.4f units/px = %.5f m/px = %.3f px/m  (%.0f px covers %.0f x %.0f m)"
          % (u_per_px, sm, px_per_m, W, W * sm, H * sm))
    print("            ink box  (the DRAWN map in the carrier) world X [%+.0f .. %+.0f] = %.0f  "
          "Z [%+.0f .. %+.0f] = %.0f units"
          % (ink["X"][0] / HL_UNIT, ink["X"][1] / HL_UNIT, (ink["X"][1] - ink["X"][0]) / HL_UNIT,
             ink["Z"][0] / HL_UNIT, ink["Z"][1] / HL_UNIT, (ink["Z"][1] - ink["Z"][0]) / HL_UNIT))
    print("            footprint (all geometry)        world X [%+.0f .. %+.0f] = %.0f  "
          "Z [%+.0f .. %+.0f] = %.0f units"
          % (fp_bbox["X"][0] / HL_UNIT, fp_bbox["X"][1] / HL_UNIT,
             (fp_bbox["X"][1] - fp_bbox["X"][0]) / HL_UNIT,
             fp_bbox["Z"][0] / HL_UNIT, fp_bbox["Z"][1] / HL_UNIT,
             (fp_bbox["Z"][1] - fp_bbox["Z"][0]) / HL_UNIT))
    contained = True
    spill = {}
    for axis in ("X", "Z"):
        lo = ink[axis][0] - fp_bbox[axis][0]
        hi = fp_bbox[axis][1] - ink[axis][1]
        over = max(lo, hi)
        contained = contained and over <= 0.0
        if over > 0.0:
            spill[axis] = over
        print("            spill    %s: low %+8.0f units (%+7.1f px)  high %+8.0f units "
              "(%+7.1f px)  -> %s"
              % (axis, lo / HL_UNIT, lo / sm, hi / HL_UNIT, hi / sm,
                 "OUTSIDE" if over > 0 else "inside"))
    print("            aspect   ink %.4f (Z/X) vs footprint %.4f -> window-independent mismatch "
          "%+.1f%%"
          % ((ink["Z"][1] - ink["Z"][0]) / (ink["X"][1] - ink["X"][0]),
             (fp_bbox["Z"][1] - fp_bbox["Z"][0]) / (fp_bbox["X"][1] - fp_bbox["X"][0]),
             100.0 * (((fp_bbox["Z"][1] - fp_bbox["Z"][0]) / (fp_bbox["X"][1] - fp_bbox["X"][0])) /
                      ((ink["Z"][1] - ink["Z"][0]) / (ink["X"][1] - ink["X"][0])) - 1.0)))
    print("            verdict  %s" % ("CONTAINED (footprint inside the drawn map)"
                                       if contained else
                                       "NOT CONTAINED (footprint spills out of the drawn map "
                                       "by %.0f units = %.0f px max)"
                                       % (max(spill.values()) / HL_UNIT, max(spill.values()) / sm)))
    return {"windowXUnits": [win["X"][0] / HL_UNIT, win["X"][1] / HL_UNIT],
            "windowZUnits": [win["Z"][0] / HL_UNIT, win["Z"][1] / HL_UNIT],
            "metresPerCarrierPx": sm, "pxPerMetre": px_per_m, "unitsPerCarrierPx": u_per_px,
            "inkBoxWorldUnits": {k: [v[0] / HL_UNIT, v[1] / HL_UNIT] for k, v in ink.items()},
            "footprintBoxWorldUnits": {k: [v[0] / HL_UNIT, v[1] / HL_UNIT] for k, v in fp_bbox.items()},
            "spillUnitsMax": (max(spill.values()) / HL_UNIT if spill else 0.0),
            "contained": bool(contained)}


def containment_scale(fp_bbox, bmp_rect):
    """The uniform units/px that WOULD contain the footprint in the ink box (per-axis need)."""
    w_u = float(bmp_rect[1] - bmp_rect[0])          # ink columns  (the u axis)
    h_v = float(bmp_rect[3] - bmp_rect[2])          # ink rows     (the v axis)
    need_u = (fp_bbox["Z"][1] - fp_bbox["Z"][0]) / HL_UNIT / w_u        # u <- Z
    need_v = (fp_bbox["X"][1] - fp_bbox["X"][0]) / HL_UNIT / h_v        # v <- X
    return need_u, need_v


def landmark_window(bmp_path, geo_path):
    """Solve the carrier's OWN window from its two red site glyphs + this project's markers.

    Imported by file path: `locate-overview-letters.py` owns the glyph rule (red palette
    entries picked from the palette table, 3-step closing, >= 20 px clusters), so the rule has
    exactly one home.  Returns (params, info) for the u<-Z / v<-X parameterisation, or None.
    """
    import importlib.util
    path = os.path.join(HERE, "locate-overview-letters.py")
    spec = importlib.util.spec_from_file_location("locate_overview_letters", path)
    lm = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(lm)

    b = lm.read_bmp(bmp_path)
    hist = np.bincount(b["idx"].ravel(), minlength=256)
    sel = lm.red_indices(b["pal"], hist)
    mask = np.isin(b["idx"], [s[0] for s in sel])
    gl = [g for g in lm.glyphs(mask) if g["px"] >= 20]
    if len(gl) != 2:
        print("landmark: CANNOT solve -- expected the carrier's 2 red site glyphs, found %d"
              % len(gl))
        return None
    geo_mk, _, _ = lm.read_geo_markers(geo_path)
    if "Bombsite_A" not in geo_mk or "Bombsite_B" not in geo_mk:
        print("landmark: CANNOT solve -- marker table lacks Bombsite_A / Bombsite_B")
        return None
    pts = {}
    for nm in ("Bombsite_A", "Bombsite_B"):
        c = lm.centroid(geo_mk[nm])
        pts[nm] = (c[0] / HL_UNIT, c[1] / HL_UNIT)          # (X, Z) in units

    img = np.array([[gl[0]["cx"], gl[0]["cy"]], [gl[1]["cx"], gl[1]["cy"]]])
    wor = np.array([pts["Bombsite_A"], pts["Bombsite_B"]])
    dX = wor[0, 0] - wor[1, 0]
    dZ = wor[0, 1] - wor[1, 1]
    k = float(np.hypot(dX, dZ) / np.hypot(img[0, 0] - img[1, 0], img[0, 1] - img[1, 1]))
    best = None
    for swap_labels in (False, True):
        I = img[::-1] if swap_labels else img
        Z0 = float(np.mean([wor[i, 1] + I[i, 0] * k for i in range(2)]))     # u = (Z0 - Z)/k
        X0 = float(np.mean([wor[i, 0] + I[i, 1] * k for i in range(2)]))     # v = (X0 - X)/k
        res = [float(np.hypot(I[i, 0] - (Z0 - wor[i, 1]) / k, I[i, 1] - (X0 - wor[i, 0]) / k))
               for i in range(2)]
        rec = {"k": k, "Z0": Z0, "X0": X0, "residPx": res, "labelsSwapped": swap_labels}
        if best is None or max(res) < max(best["residPx"]):
            best = rec
    gl_c = [{"px": g["px"], "cx": round(g["cx"], 2), "cy": round(g["cy"], 2)} for g in gl]
    print("landmark: carrier's own 2 red site glyphs %s" % gl_c)
    print("          + this project's marker table: A(%+.0f, %+.0f) B(%+.0f, %+.0f) units "
          "|A->B| = %.0f units over %.2f px"
          % (pts["Bombsite_A"][0], pts["Bombsite_A"][1], pts["Bombsite_B"][0],
             pts["Bombsite_B"][1], np.hypot(dX, dZ),
             np.hypot(img[0, 0] - img[1, 0], img[0, 1] - img[1, 1])))
    print("          scale %.4f units/px (landmark-derived, no search); anchor residual "
          "%.2f / %.2f px" % (best["k"], best["residPx"][0], best["residPx"][1]))
    best["glyphs"] = gl_c
    best["sites"] = {k2: [round(v, 1) for v in pts[k2]] for k2 in pts}
    return best


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--bmp", default=DEFAULT_BMP)
    ap.add_argument("--geo", default=DEFAULT_GEO)
    ap.add_argument("--dump")
    ap.add_argument("--json")
    ap.add_argument("--window-json",
                    help="write the deliverable window (minX/maxX/minZ/maxZ + px per metre) for "
                         "a consumer (the radar); carries usableForRadar=false plus the rejected "
                         "window and its spill when nothing registers")
    ap.add_argument("--tw", type=int, default=256)
    ap.add_argument("--th", type=int, default=192)
    ap.add_argument("--min-iou", type=float, default=0.80,
                    help="reported as a secondary metric; the VERDICT is containment (see header)")
    # --axis restricts the search to ONE axis family, so the same judgment can report the
    # residual under "image u <- world X" (this project's convention) and under
    # "image u <- world Z" (what tools/probes/locate-overview-letters.py measures).  The
    # mirror inside the family is still searched.  Default "auto" = the original behaviour
    # (search all 8: swap x mirror_u x mirror_v).
    ap.add_argument("--axis", choices=("auto", "uX", "uZ"), default="auto",
                    help="restrict the axis pair: uX = image u <- world X (current convention), "
                         "uZ = image u <- world Z (slice AO's landmark verdict)")
    args = ap.parse_args(argv)

    try:
        b = read_bmp(args.bmp)
    except (OSError, ValueError) as e:
        sys.stderr.write("cannot read overview bmp: %s\n" % e)
        return 2
    try:
        geo = read_geo(args.geo)
    except (OSError, ValueError) as e:
        sys.stderr.write("cannot read geo: %s\n" % e)
        return 2

    tw, th = args.tw, args.th
    bmp, W, H = b["img"], b["w"], b["h"]
    print("carrier : %s (%dx%d, %d B, dataOffset %d, rowBytes %d)"
          % (os.path.basename(args.bmp), W, H, b["bytes"], b["data_off"], b["row"]))

    hist = np.bincount(bmp.ravel(), minlength=256)
    key = int(np.argmax(hist))
    ink = bmp != key
    print("key idx : %d (%.1f%% of %d px)   ink %.1f%%"
          % (key, 100.0 * hist[key] / bmp.size, bmp.size, 100.0 * ink.mean()))
    colp, rowp = ink.sum(axis=0), ink.sum(axis=1)
    bx0, bx1 = int(np.nonzero(colp)[0][0]), int(np.nonzero(colp)[0][-1])
    by0, by1 = int(np.nonzero(rowp)[0][0]), int(np.nonzero(rowp)[0][-1])
    print("ink bbox: x[%d..%d] w=%d  y[%d..%d] h=%d" % (bx0, bx1, bx1 - bx0 + 1, by0, by1, by1 - by0 + 1))

    half = float(max(abs(geo["wmin"][0]), abs(geo["wmax"][0]),
                     abs(geo["wmin"][2]), abs(geo["wmax"][2])) * 1.15 + 200.0 * HL_UNIT)
    hi, hi_mpp = raster_footprint(geo["tri"], half)
    span_x_u = (geo["wmax"][0] - geo["wmin"][0]) / HL_UNIT
    span_z_u = (geo["wmax"][2] - geo["wmin"][2]) / HL_UNIT
    print("geometry: x[%.4f..%.4f] z[%.4f..%.4f] m => %.0f x %.0f units  aspect=%.4f"
          % (geo["wmin"][0], geo["wmax"][0], geo["wmin"][2], geo["wmax"][2],
             span_x_u, span_z_u, span_x_u / span_z_u))
    print("footprint: half=%.1f m, %d px, %.4f units/px, ink=%.1f%%"
          % (half, RASTER_N, hi_mpp / HL_UNIT, 100.0 * hi.mean()))

    # ---- window-independent discriminator ------------------------------------------------
    # A uniform-scale window CANNOT change an aspect ratio.  So the carrier's ink box and the
    # geometry box must agree in aspect once you decide which geometry axis feeds which image
    # axis -- and that decision is settled here, with no search involved at all.
    fp_bbox = vertex_bbox(geo["tri"])
    vspan_x_u = (fp_bbox["X"][1] - fp_bbox["X"][0]) / HL_UNIT
    vspan_z_u = (fp_bbox["Z"][1] - fp_bbox["Z"][0]) / HL_UNIT
    print("vertices: x[%+.1f..%+.1f] z[%+.1f..%+.1f] units => %.0f x %.0f (the real extent)"
          % (fp_bbox["X"][0] / HL_UNIT, fp_bbox["X"][1] / HL_UNIT,
             fp_bbox["Z"][0] / HL_UNIT, fp_bbox["Z"][1] / HL_UNIT, vspan_x_u, vspan_z_u))
    print("          header bbox is STALE: it over-reports by X %.0f units and Z %.0f units "
          "(see vertex_bbox docstring)" % ((span_x_u - vspan_x_u), (span_z_u - vspan_z_u)))
    ink_ar = (bx1 - bx0 + 1) / float(by1 - by0 + 1)          # image horizontal : vertical
    for lbl, num, den in (("u<->geo X", span_x_u, span_z_u), ("u<->geo Z", span_z_u, span_x_u),
                          ("u<->geo X (vertices)", vspan_x_u, vspan_z_u),
                          ("u<->geo Z (vertices)", vspan_z_u, vspan_x_u)):
        ar = num / den
        print("aspect  : ink box %.4f (h:v)  vs  geometry %-20s %.4f  ->  mismatch %+.1f%%"
              % (ink_ar, lbl, ar, 100.0 * (ar / ink_ar - 1.0)))
    print("          (no window can alter this -> whichever row is ~0% names the true axis")
    print("           mapping; the other one is rejected without any search)")
    need_u, need_v = containment_scale(fp_bbox, (bx0, bx1 + 1, by0, by1 + 1))
    print("contain : to hold the whole footprint inside the ink box a window needs "
          "u<-Z %.3f and v<-X %.3f units/px => uniform %.3f (%.1f%% anisotropic)"
          % (need_u, need_v, max(need_u, need_v),
             100.0 * (max(need_u, need_v) / min(need_u, need_v) - 1.0)))

    ys, xs = np.nonzero(hi)
    sil_c = (float((-half + (xs + 0.5) * hi_mpp).mean()),
             float((half - (ys + 0.5) * hi_mpp).mean()))
    print("footprint centroid world=(%.3f, %.3f) m" % sil_c)

    ink_small = (ink.reshape(th, H // th, tw, W // tw).mean(axis=(1, 3)) >= 0.5)
    cu = (np.nonzero(ink_small)[1].mean() + 0.5) * (W / float(tw))
    cv = (np.nonzero(ink_small)[0].mean() + 0.5) * (H / float(th))
    print("ink %dx%d: %.1f%%  centroid=(%.2f, %.2f) carrier px"
          % (tw, th, 100.0 * ink_small.mean(), cu, cv))

    tx, ty = W / float(tw), H / float(th)
    uu = (np.arange(tw) + 0.5) * tx
    vv = (np.arange(th) + 0.5) * ty

    def sample(A, B, sm, fu, fv, swap):
        """A/B = world metres on the u/v axis at carrier pixel 0; fu/fv mirror that axis."""
        ua = A + (-uu if fu else uu) * sm
        va = B + (vv if fv else -vv) * sm
        X, Z = (va, ua) if swap else (ua, va)
        fxc = np.round((X + half) / hi_mpp - 0.5).astype(np.int32)      # along geo X
        fyc = np.round((half - Z) / hi_mpp - 0.5).astype(np.int32)      # along geo Z
        okx = (fxc >= 0) & (fxc < hi.shape[1])
        okz = (fyc >= 0) & (fyc < hi.shape[0])
        # out of the rasterised grid == no geometry there == background (NOT a clamped smear)
        M = np.where(okz[:, None] & okx[None, :],
                     hi[np.ix_(np.clip(fyc, 0, hi.shape[0] - 1), np.clip(fxc, 0, hi.shape[1] - 1))],
                     False)
        return M.T if swap else M          # with the axes swapped the raw index is transposed

    def anchor(sm, fu, fv, swap):
        c_u = sil_c[1] if swap else sil_c[0]
        c_v = sil_c[0] if swap else sil_c[1]
        A = c_u + cu * sm if fu else c_u - cu * sm
        B = c_v - cv * sm if fv else c_v + cv * sm
        return A, B

    def best_shift(F):
        sh = F.shape
        C = np.fft.irfft2(np.fft.rfft2(ink_small.astype(np.float32), sh) *
                          np.conj(np.fft.rfft2(F.astype(np.float32), sh)), sh)
        cand = np.argpartition(C.ravel(), -min(8, C.size))[-min(8, C.size):]
        out, seen = (0.0, 0, 0), set()
        for i in cand:
            dy, dx = np.unravel_index(int(i), sh)
            if (dy, dx) in seen:
                continue
            seen.add((dy, dx))
            v = iou(np.roll(np.roll(F, dy, axis=0), dx, axis=1), ink_small)
            if v > out[0]:
                out = (v, int(dy), int(dx))
        return out

    swaps = (0,) if args.axis == "uX" else ((1,) if args.axis == "uZ" else (0, 1))
    best = None
    per_combo = []                     # best IoU per (swap, fu, fv) -- decides the MIRROR
    for swap in swaps:
        for fu in (0, 1):
            for fv in (0, 1):
                cb = None
                for s_u in np.arange(4.00, 11.001, 0.04):
                    sm = s_u * HL_UNIT
                    A, B = anchor(sm, fu, fv, swap)
                    v, dy, dx = best_shift(sample(A, B, sm, fu, fv, swap))
                    if cb is None or v > cb[0]:
                        cb = (v, s_u)
                    if best is None or v > best[0]:
                        # a circular roll by k == a roll by (k - N): take the signed one
                        sdx = dx - tw if dx > tw // 2 else dx
                        sdy = dy - th if dy > th // 2 else dy
                        best = (v, s_u, A - sdx * tx * sm, B - sdy * ty * sm, fu, fv, swap)
                per_combo.append((cb[0], cb[1], fu, fv, swap))
    per_combo.sort(reverse=True)
    print("mirrors : best IoU per (axis pair, mirror_u, mirror_v) -- the mirror is NOT decided by")
    print("          a landmark pair, only by the silhouette, so this is the deciding number:")
    for v_, s_, fu_, fv_, sw_ in per_combo:
        print("            u<-%s  mirror_u=%d mirror_v=%d   IoU=%.4f  scale=%.3f u/px"
              % ("Z" if sw_ else "X", fu_, fv_, v_, s_))
    v0, s0, A_, B_, fu_, fv_, sw_ = best
    sm0 = s0 * HL_UNIT
    print("solved  : IoU=%.4f  scale=%.3f units/carrier-px  u<->%s  mirror_u=%d mirror_v=%d"
          % (v0, s0, "Z" if sw_ else "X", fu_, fv_))

    step = tx * sm0
    for _ in range(6):
        moved = False
        for dA in (-step, 0.0, step):
            for dB in (-step, 0.0, step):
                if dA == 0.0 and dB == 0.0:
                    continue
                v = iou(sample(A_ + dA, B_ + dB, sm0, fu_, fv_, sw_), ink_small)
                if v > v0:
                    v0, A_, B_, moved = v, A_ + dA, B_ + dB, True
        if not moved:
            break
        step *= 0.5
    print("refined : IoU=%.4f  scale=%.3f units/px" % (v0, s0))

    m = sample(A_, B_, sm0, fu_, fv_, sw_)
    fb = bbox_of(m)
    if fb is None:
        print("residual: footprint mask EMPTY -- window does not overlap the map at all")
        return 3
    px0, px1 = (fb[0] + 0.5) * tx, (fb[1] + 0.5) * tx
    py0, py1 = (fb[2] + 0.5) * ty, (fb[3] + 0.5) * ty
    print("residual: footprint bbox in carrier px x[%d..%d] w=%d  y[%d..%d] h=%d"
          % (round(px0), round(px1), round(px1 - px0), round(py0), round(py1), round(py1 - py0)))
    print("          ink       bbox in carrier px x[%d..%d] w=%d  y[%d..%d] h=%d"
          % (bx0, bx1, bx1 - bx0 + 1, by0, by1, by1 - by0 + 1))
    for nm, dv, iv in (("left", px0, bx0), ("right", px1, bx1), ("top", py0, by0), ("bottom", py1, by1)):
        print("          %-6s edge error = %+7.1f px = %+7.0f units" % (nm, dv - iv, (dv - iv) * s0))
    e = max(abs(px0 - bx0), abs(px1 - bx1), abs(py0 - by0), abs(py1 - by1))
    print("          max |edge error| = %.1f px = %.0f units" % (e, e * s0))
    u_units = span_z_u if sw_ else span_x_u
    v_units = span_x_u if sw_ else span_z_u
    print("aniso   : scale each image axis alone would need = horizontal %.3f  vertical %.3f "
          "units/px (ratio %.3f, solved %.3f)"
          % (u_units / (px1 - px0), v_units / (py1 - py0),
             (u_units / (px1 - px0)) / (v_units / (py1 - py0)), s0))

    # window in world units: u axis spans [A, A + W*sm] (sign of the mirror is irrelevant
    # for a span), v axis spans [B, B + H*sm]
    urange = sorted((A_, A_ + W * sm0))
    vrange = sorted((B_, B_ + H * sm0))
    xr, zr = (vrange, urange) if sw_ else (urange, vrange)
    print("window  : world X [%+.0f .. %+.0f] = %.0f units   world Z [%+.0f .. %+.0f] = %.0f units"
          % (xr[0] / HL_UNIT, xr[1] / HL_UNIT, (xr[1] - xr[0]) / HL_UNIT,
             zr[0] / HL_UNIT, zr[1] / HL_UNIT, (zr[1] - zr[0]) / HL_UNIT))
    print("txt     : ZOOM %g  ORIGIN %g %g %g  ROTATED %d   (this project's geo frame is the "
          "GoldSrc bbox re-centred on its own centre => only offset-free quantities compare)"
          % (TXT_ZOOM, TXT_ORIGIN[0], TXT_ORIGIN[1], TXT_ORIGIN[2], TXT_ROTATED))
    print("          window span / ZOOM = %.1f x %.1f units (X x Z); units per ZOOM unit = %.3f"
          % ((xr[1] - xr[0]) / HL_UNIT / TXT_ZOOM, (zr[1] - zr[0]) / HL_UNIT / TXT_ZOOM,
             s0 / TXT_ZOOM))

    # ---- the two windows, as the radar needs them (slice AP) -------------------------------
    # 1) the silhouette fit just solved;  2) the carrier's OWN scale from its site glyphs.
    windows = {}
    print("")
    print("--- window A: silhouette fit (IoU-optimal uniform scale) ---")
    windows["silhouette"] = window_report("silhouette", sw_, fu_, fv_, A_, B_, sm0, W, H,
                                         (bx0, bx1 + 1, by0, by1 + 1), fp_bbox)
    print("--- window B: carrier's own landmarks (its 2 site glyphs + the marker table) ---")
    lm = landmark_window(args.bmp, args.geo)
    if lm is None or args.axis == "uX":
        print("window[landmarks]: not solved%s -- only the silhouette window is reported"
              % ("" if lm is None else " (--axis uX contradicts the landmark axis pair)"))
    else:
        # landmark_window returns GoldSrc UNITS; window_report works in metres
        windows["landmarks"] = window_report("landmarks", True, 1, 0,
                                            lm["Z0"] * HL_UNIT, lm["X0"] * HL_UNIT,
                                            lm["k"] * HL_UNIT, W, H,
                                            (bx0, bx1 + 1, by0, by1 + 1), fp_bbox)
        windows["landmarks"]["landmarkAnchor"] = {k2: lm[k2] for k2 in
                                                  ("k", "residPx", "labelsSwapped", "glyphs",
                                                   "sites")}

    # Robustness: is the spill only outlier ("sliver") geometry?  A trimmed footprint can
    # always be translated, so the fair test is SPAN vs SPAN: if the outer 0.5% of vertices
    # per axis are dropped and the spill still does not vanish, the mismatch is structural.
    p = geo["tri"].reshape(-1, 3)
    tX = np.percentile(p[:, 0], [0.5, 99.5]) / HL_UNIT
    tZ = np.percentile(p[:, 2], [0.5, 99.5]) / HL_UNIT
    print("robust  : 0.5%%-trimmed footprint X [%+.0f..%+.0f] span %.0f   Z [%+.0f..%+.0f] span %.0f"
          % (tX[0], tX[1], tX[1] - tX[0], tZ[0], tZ[1], tZ[1] - tZ[0]))
    for nm, w in windows.items():
        ix = w["inkBoxWorldUnits"]["X"]
        iz = w["inkBoxWorldUnits"]["Z"]
        sX, sZ = (tX[1] - tX[0]) - (ix[1] - ix[0]), (tZ[1] - tZ[0]) - (iz[1] - iz[0])
        print("          %-11s trimmed span minus ink span: X %+8.0f units  Z %+8.0f units -> %s"
              % (nm, sX, sZ, "still spills" if max(sX, sZ) > 0 else "fits"))

    # ---- verdict: containment decides (an IoU is not a containment) -----------------------
    ok_any = [nm for nm, w in windows.items() if w["contained"]]
    ok = bool(ok_any)
    print("")
    print("iou     : best uniform-scale IoU=%.4f (secondary metric ONLY: the sampler clips the "
          "footprint at the carrier's edge, so this cannot prove the window is right)" % v0)
    print("verdict : %s" % (
        "REGISTERS -- window '%s' contains the whole footprint (spill<=0 on all 4 edges)"
        % ",".join(ok_any) if ok else
        "DOES NOT REGISTER -- no uniform-scale window both matches the carrier's own landmarks "
        "and holds the geometry footprint inside the carrier's ink box (spill up to %.0f units); "
        "the carrier must NOT be swapped in, and the radar keeps its generated underlay"
        % max(w["spillUnitsMax"] for w in windows.values())))

    if args.dump:
        os.makedirs(os.path.dirname(os.path.abspath(args.dump)), exist_ok=True)
        a = Image.fromarray(np.where(ink_small, 60, 0).astype(np.uint8)).convert("RGB")
        bb = Image.fromarray(np.where(m, 200, 0).astype(np.uint8)).convert("RGB")
        ov = np.zeros((th, tw, 3), dtype=np.uint8)
        ov[..., 0] = np.where(ink_small, 255, 0)
        ov[..., 1] = np.where(m, 255, 0)
        ov[np.logical_and(ink_small, m)] = (255, 255, 255)
        comp = Image.new("RGB", (tw * 3 + 8, th), (0, 0, 0))
        comp.paste(a, (0, 0)); comp.paste(bb, (tw + 4, 0)); comp.paste(Image.fromarray(ov), (2 * tw + 8, 0))
        comp.save(args.dump)
        print("dump    : %s  (left = carrier ink, mid = geometry footprint, right = overlay "
              "R = ink only / G = geometry only / white = both)" % args.dump)

    report = {"registers": bool(ok), "contained": bool(ok), "iou": v0,
              "scaleUnitsPerCarrierPx": s0,
              "uAxisWorld": "Z" if sw_ else "X", "mirrorU": bool(fu_), "mirrorV": bool(fv_),
              "windowXUnits": [xr[0] / HL_UNIT, xr[1] / HL_UNIT],
              "windowZUnits": [zr[0] / HL_UNIT, zr[1] / HL_UNIT],
              "inkBBox": [bx0, bx1, by0, by1], "keyIndex": key,
              "maxEdgeErrorUnits": e * s0,
              "containmentScaleUnitsPerCarrierPx": [need_u, need_v],
              "geometryVertexBoxUnits": {k2: [v2 / HL_UNIT for v2 in v] for k2, v in fp_bbox.items()},
              "headerBoxUnits": {"X": [geo["wmin"][0] / HL_UNIT, geo["wmax"][0] / HL_UNIT],
                                 "Z": [geo["wmin"][2] / HL_UNIT, geo["wmax"][2] / HL_UNIT]},
              "windows": windows,
              "txt": {"zoom": TXT_ZOOM, "origin": list(TXT_ORIGIN), "rotated": TXT_ROTATED}}

    if args.window_json:
        # the deliverable form ①: the world rectangle + pixels per metre, ready to consume
        use = windows.get(ok_any[0]) if ok_any else None
        out = {"usableForRadar": bool(ok_any)}
        if use is None:
            lm2 = windows.get("landmarks")
            src = lm2 or windows["silhouette"]
            out["reason"] = ("no uniform-scale window both matches the carrier's own landmarks "
                             "and holds the geometry footprint inside the carrier's ink box "
                             "(best rejected window '%s' spills %.0f units out)"
                             % ("landmarks" if lm2 else "silhouette", src["spillUnitsMax"]))
            out["bestRejectedWindow"] = {"minX": src["windowXUnits"][0], "maxX": src["windowXUnits"][1],
                                         "minZ": src["windowZUnits"][0], "maxZ": src["windowZUnits"][1],
                                         "unitsPerCarrierPx": src["unitsPerCarrierPx"],
                                         "metresPerCarrierPx": src["metresPerCarrierPx"],
                                         "pxPerMetre": src["pxPerMetre"], "contained": False}
            for axis in ("X", "Z"):
                out["bestRejectedWindow"]["ink" + axis] = src["inkBoxWorldUnits"][axis]
                out["bestRejectedWindow"]["footprint" + axis] = src["footprintBoxWorldUnits"][axis]
        else:
            out["window"] = {"minX": use["windowXUnits"][0], "maxX": use["windowXUnits"][1],
                             "minZ": use["windowZUnits"][0], "maxZ": use["windowZUnits"][1],
                             "unitsPerCarrierPx": use["unitsPerCarrierPx"],
                             "metresPerCarrierPx": use["metresPerCarrierPx"],
                             "pxPerMetre": use["pxPerMetre"]}
        out["source"] = {"silhouette": "uniform-scale IoU fit of the geometry footprint",
                         "landmarks": "carrier's 2 red site glyphs + project marker table"}
        out["probe"] = os.path.relpath(os.path.abspath(__file__), PROJECT_ROOT).replace("\\", "/")
        os.makedirs(os.path.dirname(os.path.abspath(args.window_json)), exist_ok=True)
        with open(args.window_json, "w", encoding="utf-8") as fh:
            json.dump(out, fh, indent=1)
        print("window  : %s (usableForRadar=%s)" % (args.window_json, out["usableForRadar"]))

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(report, fh, indent=1)
        print("json    : %s" % args.json)
    return 0 if ok else 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
