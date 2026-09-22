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

Usage
-----
    python tools/probes/register-overview.py [--dump <png>] [--json <json>] [--tw 256]
Exit 0 = register (IoU >= --min-iou), 3 = does not register (residual reported), 2 = bad input.
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


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--bmp", default=DEFAULT_BMP)
    ap.add_argument("--geo", default=DEFAULT_GEO)
    ap.add_argument("--dump")
    ap.add_argument("--json")
    ap.add_argument("--tw", type=int, default=256)
    ap.add_argument("--th", type=int, default=192)
    ap.add_argument("--min-iou", type=float, default=0.80)
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
    ink_ar = (bx1 - bx0 + 1) / float(by1 - by0 + 1)          # image horizontal : vertical
    for lbl, num, den in (("u<->geo X", span_x_u, span_z_u), ("u<->geo Z", span_z_u, span_x_u)):
        ar = num / den
        print("aspect  : ink box %.4f (h:v)  vs  geometry %-10s %.4f  ->  mismatch %+.1f%%"
              % (ink_ar, lbl, ar, 100.0 * (ar / ink_ar - 1.0)))
    print("          (no window can alter this -> whichever row is ~0% names the true axis")
    print("           mapping; the other one is rejected without any search)")

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

    ok = v0 >= args.min_iou
    print("verdict : %s" % ("REGISTERS (uniform scale, IoU=%.4f)" % v0 if ok else
                            "DOES NOT REGISTER (best uniform-scale IoU=%.4f < %.2f) -- "
                            "carrier must NOT be swapped in" % (v0, args.min_iou)))

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

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({"registers": bool(ok), "iou": v0, "scaleUnitsPerCarrierPx": s0,
                       "uAxisWorld": "Z" if sw_ else "X", "mirrorU": bool(fu_), "mirrorV": bool(fv_),
                       "windowXUnits": [xr[0] / HL_UNIT, xr[1] / HL_UNIT],
                       "windowZUnits": [zr[0] / HL_UNIT, zr[1] / HL_UNIT],
                       "inkBBox": [bx0, bx1, by0, by1], "keyIndex": key,
                       "maxEdgeErrorUnits": e * s0,
                       "txt": {"zoom": TXT_ZOOM, "origin": list(TXT_ORIGIN), "rotated": TXT_ROTATED}},
                      fh, indent=1)
        print("json    : %s" % args.json)
    return 0 if ok else 3


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
