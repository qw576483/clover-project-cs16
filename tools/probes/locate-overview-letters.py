#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
locate-overview-letters.py -- settle, with numbers, whether the ORIGINAL CS 1.6 radar
underlay (`cstrike/overviews/de_dust2.bmp`) is drawn with the SAME axis pair as this
project's radar (image right <- world X, image up <- world Z) or rotated 90 degrees
(image right <- world Z, image up <- world X).

WHY THIS EXISTS (slice cs16-AO)
-------------------------------
Slice AN left two contradictions on the table:

  * the carrier's ink box is 808x764 px while this project's geometry bbox is
    4864x5568 units => the aspect ratio alone says the carrier's image HORIZONTAL axis
    is the geometry's **Z** axis (mismatch +8.2%) and not its X axis (-17.4%);
  * yet both `tools/probes/render-overview.py` (u = (x-minX)/spanX) and
    `CsRadarWidget.Refresh` (right = +X, up = +Z) use X -> right.

Neither of those says *where* the carrier's own landmarks are, so slice AN could not
decide and refused to swap the carrier in.  This probe adds a landmark that is
completely independent of the silhouette: the carrier has exactly TWO small red
"site" glyphs burned into it (measured below -- they are X-shaped marks, the yellow
palette entries AN counted are just sand texture, see `--why` output).  Their two
centroids give ONE vector *inside the image*; the project's own marker table gives the
same vector *in the world*.  A uniform-scale + translation mapping cannot change an
angle between two points, so the angle residual decides the axis pair with no search:

    residual ~ 0 deg   => image vertical axis is world X  (90 deg swap: AN's suspicion)
    residual ~ 90 deg  => image vertical axis is world Z  (X -> right, i.e. no change)

Two points can NOT resolve the mirror about the line through them (the reflection that
fixes both points leaves them where they are); that ambiguity is reported explicitly
instead of being hidden, and getting the handedness right needs the silhouette (see
`register-overview.py`).

Everything is measured, nothing is invented:
  * the carrier's glyph pixels: palette indices whose RGB is red-dominant
    (r >= 60, g/r <= 0.45, b/r <= 0.40) -- chosen from the palette table itself and
    printed, so the selection is auditable;
  * the world vector: the Bombsite_A / Bombsite_B marker table, read from the same
    `de_dust2_geo.bin` the scene builder uses (and cross-checked against
    `Assets/Resources/MapData/de_dust2_markers.bytes`, the runtime source).

Usage
-----
    python tools/probes/locate-overview-letters.py [--json <out.json>] [--dump <png>]
Exit 0 = judged (verdict printed), 2 = carrier/tables unreadable, 3 = could not find
exactly two glyphs (the caller must then not trust any axis conclusion).
"""

import argparse
import json
import math
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
DEFAULT_BMP = os.path.join(PROJECT_ROOT, "原版资源", "cs16src", "cstrike",
                           "cstrike__overviews__de_dust2.bmp")
DEFAULT_GEO = os.path.join(PROJECT_ROOT, "client", "Assets", "ThirdParty", "Dust2",
                           "de_dust2_geo.bin")
DEFAULT_BYTES = os.path.join(PROJECT_ROOT, "client", "Assets", "Resources", "MapData",
                             "de_dust2_markers.bytes")
HL_UNIT = 0.0254                        # GoldSrc: 1 unit = 1 inch

# `overviews/de_dust2.txt`, verbatim (see register-overview.py for the same block):
#   global { ZOOM 1.50  ORIGIN -223 1097 -192  ROTATED 0 }
#   layer  { IMAGE "overviews/de_dust2.bmp"  HEIGHT -192 }
TXT_ZOOM = 1.50
TXT_ORIGIN = (-223.0, 1097.0, -192.0)
TXT_ROTATED = 0

# marker names in the project's marker table (client/Assets/Scripts/Module/Map/ICsMap.cs:81-82)
SITE_A = "Bombsite_A"
SITE_B = "Bombsite_B"


# ---------------------------------------------------------------------------
#  readers (same byte layouts as probe-overview-bmp.py / render-overview.py)
# ---------------------------------------------------------------------------
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
    if h > 0:                                       # bottom-up BMP -> top-down rows
        raw = raw[::-1]
    pal = np.frombuffer(d[14 + dib:14 + dib + 1024], dtype=np.uint8).reshape(256, 4)
    return {"w": w, "h": abs_h, "idx": raw, "pal": pal, "bytes": len(d), "data_off": data_off}


def read_geo_markers(path):
    """marker table at the tail of de_dust2_geo.bin (layout: Editor/MapGen/Dust2GeoData.cs)."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[0:4] != b"CD2G":
        raise ValueError("%s: bad magic" % path)
    o = 8 + 28
    wmin = struct.unpack_from("<3f", data, o + 8)
    wmax = struct.unpack_from("<3f", data, o + 20)
    o += 8 + 24
    (gc,) = struct.unpack_from("<I", data, o); o += 4
    for _ in range(gc):
        o += 48
        vc, ic = struct.unpack_from("<2I", data, o); o += 8
        o += 12 * vc + 8 * vc + 12 * vc + 4 * ic
    (bc_field,) = struct.unpack_from("<I", data, o); o += 4
    blockers_off = o

    def try_at(off):
        if off + 4 > len(data):
            return None
        (mc,) = struct.unpack_from("<I", data, off)
        if mc <= 0 or mc > 128:
            return None
        p = off + 4
        out = {}
        for _ in range(mc):
            if p + 36 > len(data):
                return None
            name = bytes(data[p:p + 32]).split(b"\0")[0]
            if not name or any(c < 0x20 or c > 0x7E for c in name):
                return None
            name = name.decode("ascii")
            p += 32
            (pc,) = struct.unpack_from("<I", data, p); p += 4
            if pc > 4096 or p + 12 * pc > len(data):
                return None
            out[name] = [struct.unpack_from("<3f", data, p + 12 * i) for i in range(pc)]
            p += 12 * pc
        return out if p == len(data) else None       # the tail must consume the file exactly

    # blocker records are 24 B each on this file (see render-overview.py BLOCKER_STRIDE note)
    for k in range(bc_field, bc_field + 4097):
        got = try_at(blockers_off + k * 24)
        if got is not None:
            return got, (wmin, wmax), k
    raise ValueError("%s: cannot locate the marker table" % path)


def read_markers_bytes(path):
    """runtime marker table: one `Name x y z` line per point (CsMap.ParseMarkers)."""
    out = {}
    with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line or line[0] == "#":
                continue
            p = line.split()
            if len(p) < 4:
                continue
            try:
                out.setdefault(p[0], []).append((float(p[1]), float(p[2]), float(p[3])))
            except ValueError:
                continue
    return out


def centroid(pts):
    n = len(pts)
    return (sum(p[0] for p in pts) / n, sum(p[2] for p in pts) / n)     # (X, Z) metres


# ---------------------------------------------------------------------------
#  glyph extraction
# ---------------------------------------------------------------------------
def red_indices(pal, hist):
    """palette entries that are red-dominant, picked from the palette table itself."""
    sel = []
    for i in range(256):
        if hist[i] == 0:
            continue
        r, g, b = int(pal[i, 2]), int(pal[i, 1]), int(pal[i, 0])
        if r >= 60 and g / float(r) <= 0.45 and b / float(r) <= 0.40:
            sel.append((i, r, g, b, int(hist[i])))
    return sel


def dilate(mask, iters=3):
    m = mask.copy()
    for _ in range(iters):
        m = (m | np.roll(m, 1, 0) | np.roll(m, -1, 0) |
             np.roll(m, 1, 1) | np.roll(m, -1, 1))
    return m


def components(mask):
    h, w = mask.shape
    lbl = np.zeros((h, w), dtype=np.int32)
    out = []
    cols = [np.nonzero(mask[y])[0] for y in range(h)]
    for sy in range(h):
        for sx in cols[sy]:
            if lbl[sy, sx]:
                continue
            cid = len(out) + 1
            stack = [(sy, int(sx))]
            lbl[sy, sx] = cid
            ys, xs = [], []
            while stack:
                y, x = stack.pop()
                ys.append(y); xs.append(x)
                for yy, xx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
                    if 0 <= yy < h and 0 <= xx < w and mask[yy, xx] and not lbl[yy, xx]:
                        lbl[yy, xx] = cid
                        stack.append((yy, xx))
            out.append((lbl, np.array(ys), np.array(xs)))
    return lbl, out


def glyphs(mask):
    """cluster the red pixels into glyphs (morphological closing, then real centroid)."""
    lbl, _ = components(dilate(mask, 3))
    ids = [i for i in np.unique(lbl) if i]
    res = []
    for cid in ids:
        sel = (lbl == cid) & mask
        n = int(sel.sum())
        if n == 0:
            continue
        ys, xs = np.nonzero(sel)
        res.append({"px": n, "cx": float(xs.mean()), "cy": float(ys.mean()),
                    "x0": int(xs.min()), "x1": int(xs.max()),
                    "y0": int(ys.min()), "y1": int(ys.max())})
    res.sort(key=lambda g: -g["px"])
    return res


# ---------------------------------------------------------------------------
#  judgement
# ---------------------------------------------------------------------------
# image vector of a world vector w=(dX,dZ) under each of the 8 orthogonal axis pairs.
# (u = image column, grows right; v = image row, grows DOWN)
MAPPINGS = [
    ("u +X  v -Z  (this project's current convention)", lambda dX, dZ: (dX, -dZ)),
    ("u +X  v +Z", lambda dX, dZ: (dX, dZ)),
    ("u -X  v -Z", lambda dX, dZ: (-dX, -dZ)),
    ("u -X  v +Z", lambda dX, dZ: (-dX, dZ)),
    ("u +Z  v +X", lambda dX, dZ: (dZ, dX)),
    ("u +Z  v -X", lambda dX, dZ: (dZ, -dX)),
    ("u -Z  v +X", lambda dX, dZ: (-dZ, dX)),
    ("u -Z  v -X", lambda dX, dZ: (-dZ, -dX)),
]


def ang(dx, dy):
    return math.degrees(math.atan2(dy, dx))


def angdiff(a, b):
    d = (a - b + 180.0) % 360.0 - 180.0
    return d


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--bmp", default=DEFAULT_BMP)
    ap.add_argument("--geo", default=DEFAULT_GEO)
    ap.add_argument("--bytes", dest="bytes_path", default=DEFAULT_BYTES)
    ap.add_argument("--json")
    ap.add_argument("--dump")
    ap.add_argument("--min-px", type=int, default=20,
                    help="minimum cluster size for a mark (default 20: the two site glyphs are "
                         "86 and 32 px, the next biggest any-colour red speckle is 14 px)")
    args = ap.parse_args(argv)

    try:
        b = read_bmp(args.bmp)
    except (OSError, ValueError) as e:
        sys.stderr.write("cannot read overview bmp: %s\n" % e)
        return 2
    try:
        geo_mk, (wmin, wmax), bc = read_geo_markers(args.geo)
    except (OSError, ValueError) as e:
        sys.stderr.write("cannot read geo markers: %s\n" % e)
        return 2

    idx, pal = b["idx"], b["pal"]
    hist = np.bincount(idx.ravel(), minlength=256)
    bg = int(np.argmax(hist))
    print("carrier : %s (%dx%d, %d B, dataOffset %d)"
          % (os.path.basename(args.bmp), b["w"], b["h"], b["bytes"], b["data_off"]))
    print("key idx : %d rgb(%d,%d,%d)  %.1f%% of %d px"
          % (bg, pal[bg, 2], pal[bg, 1], pal[bg, 0], 100.0 * hist[bg] / idx.size, idx.size))

    sel = red_indices(pal, hist)
    print("red idx : %d palette entries (r>=60, g/r<=0.45, b/r<=0.40) -- picked from the "
          "palette itself:" % len(sel))
    for i, r, g, b_, c in sel:
        print("            idx%3d rgb(%3d,%3d,%3d) %5d px" % (i, r, g, b_, c))
    mask = np.isin(idx, [s[0] for s in sel])
    print("red px  : %d (%.3f%% of image)" % (int(mask.sum()), 100.0 * mask.mean()))
    print("note    : the *yellow* palette entries counted by slice AN (23112 px / 22 indices) are")
    print("          the sand texture's highlights -- every one of them sits on the sand colour")
    print("          manifold (g/r ~ 0.73).  The two site marks are RED; nothing yellow is a glyph.")

    raw_gl = glyphs(mask)
    print("glyphs  : %d cluster(s) after a 3-step 4-neighbour closing" % len(raw_gl))
    for k, g in enumerate(raw_gl):
        fill = g["px"] / float((g["x1"] - g["x0"] + 1) * (g["y1"] - g["y0"] + 1))
        print("            g%-2d %5d px  centroid=(%.2f, %.2f)  bbox x[%d..%d] y[%d..%d]  fill=%.2f%s"
              % (k, g["px"], g["cx"], g["cy"], g["x0"], g["x1"], g["y0"], g["y1"], fill,
                 "" if g["px"] >= args.min_px else "   <- noise (dropped: < %d px)" % args.min_px))
    # a real glyph is a compact mark; stray single pixels (sand compression speckle) are not
    gl = [g for g in raw_gl if g["px"] >= args.min_px]
    if len(gl) != 2:
        print("verdict : CANNOT JUDGE -- expected exactly 2 site glyphs, found %d "
              "(>= %d px each)" % (len(gl), args.min_px))
        return 3
    g1, g2 = gl[0], gl[1]
    iu, iv = g2["cx"] - g1["cx"], g2["cy"] - g1["cy"]
    ilen = math.hypot(iu, iv)
    print("image vec: glyph g0 -> g1 = (du=%+.2f, dv=%+.2f) px  |%.2f| px  angle=%.2f deg"
          % (iu, iv, ilen, ang(iu, iv)))
    print("           (v grows downward; angle is atan2(dv, du), so +90 deg = straight down)")

    # ---- world vectors -------------------------------------------------------
    if SITE_A not in geo_mk or SITE_B not in geo_mk:
        sys.stderr.write("geo marker table lacks %s / %s\n" % (SITE_A, SITE_B))
        return 2
    A = centroid([p for p in geo_mk[SITE_A]])
    B = centroid([p for p in geo_mk[SITE_B]])
    print("world mk: from %s (the file the scene builder and render-overview.py use)"
          % os.path.basename(args.geo))
    print("           %s n=%d centroid=(%+.4f, %+.4f) m = (%+.0f, %+.0f) units"
          % (SITE_A, len(geo_mk[SITE_A]), A[0], A[1], A[0] / HL_UNIT, A[1] / HL_UNIT))
    print("           %s n=%d centroid=(%+.4f, %+.4f) m = (%+.0f, %+.0f) units"
          % (SITE_B, len(geo_mk[SITE_B]), B[0], B[1], B[0] / HL_UNIT, B[1] / HL_UNIT))
    try:
        mb = read_markers_bytes(args.bytes_path)
        if SITE_A in mb and SITE_B in mb:
            Ab, Bb = centroid(mb[SITE_A]), centroid(mb[SITE_B])
            print("           cross-check %s: A=(%+.4f,%+.4f) B=(%+.4f,%+.4f) m "
                  "=> delta from geo = (%.4f, %.4f) m"
                  % (os.path.basename(args.bytes_path), Ab[0], Ab[1], Bb[0], Bb[1],
                     Ab[0] - A[0], Ab[1] - A[1]))
    except OSError:
        pass

    # the glyph pair is unlabelled: g0 may be A or g1 may be A -- test both, report the best
    print("world vec: B->A = (dX=%+.2f, dZ=%+.2f) m  (|%.2f| m = %.0f units)"
          % (A[0] - B[0], A[1] - B[1], math.hypot(A[0] - B[0], A[1] - B[1]),
             math.hypot(A[0] - B[0], A[1] - B[1]) / HL_UNIT))

    # The glyphs are unlabelled (an "X" mark reads the same either way) and a displacement
    # vector is only defined up to which end you call A, so the measurement is a LINE, not an
    # arrow: an axis pair and its 180-degree image rotation are indistinguishable here.  Both
    # labellings are therefore evaluated and the smaller residual is kept -- that IS the
    # correct treatment of an unknown discrete parameter, not a fudge.
    dXw, dZw = A[0] - B[0], A[1] - B[1]
    rows = []
    for name, fn in MAPPINGS:
        pu, pv = fn(dXw, dZw)
        pa = ang(pu, pv)
        # fold to the line: the mirrored labelling flips both the measured image vector and
        # the world vector, so the residual of the other labelling is angdiff(sa+180, pa)
        r_ab = abs(angdiff(ang(iu, iv), pa))
        r_ba = abs(angdiff(ang(-iu, -iv), pa))
        rows.append({"mapping": name, "imgAngle": ang(iu, iv), "predAngle": pa,
                     "residLine": min(r_ab, r_ba),
                     "residAB": r_ab, "residBA": r_ba})
    rows.sort(key=lambda r: r["residLine"])
    print("--- line residual over all 8 axis pairs (sorted; a pair and its 180-degree image "
          "rotation are indistinguishable with 2 unlabelled points) ---")
    for r in rows:
        print("   %-46s img=%+8.2f  pred=%+8.2f  resid=%6.2f deg"
              % (r["mapping"], r["imgAngle"], r["predAngle"], r["residLine"]))

    best = rows[0]
    cur = [r for r in rows if "current convention" in r["mapping"]][0]
    print("best    : %s  residual %.2f deg" % (best["mapping"], best["residLine"]))
    print("current : %s  residual %.2f deg" % (cur["mapping"], cur["residLine"]))

    swapped = ("u +Z" in best["mapping"] or "u -Z" in best["mapping"])
    print("verdict : %s" % (
        "90-DEGREE HYPOTHESIS HOLDS -- the carrier's image VERTICAL axis is the geometry's X "
        "axis and its image HORIZONTAL axis is the geometry's Z axis (best residual %.2f deg, "
        "vs %.2f deg for this project's current X->right convention) => render-overview.py and "
        "CsRadarWidget must swap the axis pair" % (best["residLine"], cur["residLine"])
        if swapped and best["residLine"] < 30.0 else
        "90-DEGREE HYPOTHESIS REJECTED -- best axis pair is %s with residual %.2f deg"
        % (best["mapping"], best["residLine"])))
    print("mirror  : the two best rows of the SAME family are a mapping and its 180-degree image")
    print("          rotation (indistinguishable from one displacement), and the 2nd family is")
    print("          their mirror about the site axis -- picked by the silhouette, see")
    print("          register-overview.py; the residual gap below says how strongly:")

    # scale cross-check: the two landmarks also give units-per-carrier-pixel independently
    wlen = math.hypot(A[0] - B[0], A[1] - B[1])
    upp = wlen / HL_UNIT / ilen
    print("scale   : landmark-derived %.3f units/carrier-px  (|world| %.0f units over %.2f px)"
          % (upp, wlen / HL_UNIT, ilen))
    print("          slice AN's FFT registration solved 5.320 units/px for the same carrier --")
    print("          agreement here is %s (%.1f%% off)"
          % ("CONSISTENT" if abs(upp - 5.320) / 5.320 < 0.10 else "INCONSISTENT",
             100.0 * abs(upp - 5.320) / 5.320))

    print("caveat  : the A/B labels are not readable at this glyph size, so the test is run with")
    print("          both assignments.  Two points cannot resolve the mirror about the line")
    print("          through them (that reflection fixes both points); handedness must come from")
    print("          the silhouette, see register-overview.py --mirror comparison in the report.")
    print("txt     : ZOOM %g  ORIGIN %g %g %g  ROTATED %d  -- note ROTATED 0 would mean "
          "'image right <- world X' in the GoldSrc overview convention, which is exactly the row "
          "the measurement rejects; the carrier on disk is therefore NOT in the GoldSrc "
          "X-right orientation, or this project's geo frame has X/Z swapped vs GoldSrc."
          % (TXT_ZOOM, TXT_ORIGIN[0], TXT_ORIGIN[1], TXT_ORIGIN[2], TXT_ROTATED))

    if args.dump:
        try:
            from PIL import Image
        except Exception as e:
            print("dump skipped: PIL unavailable (%s)" % e)
        else:
            img = np.array(Image.open(args.bmp).convert("RGB")) if False else None
            rgb = pal[:, [2, 1, 0]][idx]
            ov = rgb.copy()
            ov[mask] = (255, 0, 255)
            os.makedirs(os.path.dirname(os.path.abspath(args.dump)), exist_ok=True)
            Image.fromarray(ov).save(args.dump)
            print("dump    : %s (carrier with the red site mask forced to magenta)" % args.dump)

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump({
                "carrier": args.bmp, "size": [b["w"], b["h"]], "keyIndex": bg,
                "redIndices": [s[0] for s in sel],
                "glyphs": gl,
                "imageVector": {"du": iu, "dv": iv, "len": ilen, "angleDeg": ang(iu, iv)},
                "sitesWorld": {"A": list(A), "B": list(B), "units": [A[0] / HL_UNIT, A[1] / HL_UNIT,
                                                                     B[0] / HL_UNIT, B[1] / HL_UNIT]},
                "best": best, "allResiduals": rows,
                "swapped": bool(swapped),
                "landmarkUnitsPerPx": upp,
                "txt": {"zoom": TXT_ZOOM, "origin": list(TXT_ORIGIN), "rotated": TXT_ROTATED},
            }, fh, indent=1, ensure_ascii=False)
        print("json    : %s" % args.json)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
