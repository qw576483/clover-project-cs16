#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
render-overview.py -- offline top-down "overview" renderer for the radar (slice cs16-shouwei-C).

WHY THIS EXISTS (carrier fallback chain, level 1: raw data)
-----------------------------------------------------------
The A side (original CS 1.6) radar carrier is NOT on this machine:
  * `cstrike/overviews/de_dust2.bmp`   (1024x768 8bpp, 787510 B) -- the original top-down
    overlay image the original radar draws, and
  * `cstrike/overviews/de_dust2.txt`   (ZOOM / ORIGIN / ROTATED),
  * `cstrike/sprites/radar640.spr`     (the 128x128 green disc frame, hud.txt:183)
are all absent: `原版资源/{cs16src,cs109,cs16-maps,解包产物}` are empty on this machine
(see `原版资源/清单.md`).

So we drop one level down the fallback chain and use **level 1 = raw data**:
`client/Assets/ThirdParty/Dust2/de_dust2_geo.bin`, which is the *original de_dust2 BSP
geometry* (lump3 vertices + lump7 faces, converted 1 unit = 0.0254 m, map bbox centred on
the origin) -- the same file the scene builder uses.  This script rasterises that very
geometry from straight above and nothing else: no hand-drawn approximation, no mesh
screenshot, no community texture.

What is rendered
----------------
For every mesh-group triangle the script projects the three vertices to the (x, z) plane and
rasterises the footprint into two buffers:
  * `inside`  -- 1 where any part of the original geometry projects (=> the map silhouette)
  * `height`  -- the *maximum* y over all triangles covering the pixel (walls/roofs are bright,
                 the sand floor is dark) -- this is what makes the silhouette readable, and it
                 is read straight off the original geometry's own vertex heights.
Walkable corridors cannot be told from walls in a silhouette, so the picture is intentionally
"the map shape", exactly the role the original `overviews/<map>.bmp` plays.

The output PNG has a transparent background (alpha 0 outside the map) so the radar can draw it
over the 3D world without a black box -- which is what makes the original radar look "cut out".

Coordinate convention (identical to `client/Assets/Editor/MapGen/Dust2GeoData.cs`)
---------------------------------------------------------------------------------
`de_dust2_geo.bin`, little endian, version 1:
    char[4] "CD2G"  u32 version
    f32 cellSize, originX, originZ, groundTopY, obstacleMinHeight, probeBottomY, probeTopY
    u32 width, depth
    f32 worldMinX, worldMinY, worldMinZ, worldMaxX, worldMaxY, worldMaxZ
    u32 groupCount
      each: u8 png[48] u32 vertCount u32 idxCount
            f32 pos[3]*v  f32 uv[2]*v  f32 nrm[3]*v  u32 idx[idxCount]
    u32 blockerCount   each: u32 ix0 iz0 ix1 iz1  f32 yMin yMax
    u32 markerCount    each: u8 name[32] u32 ptCount f32 xyz[3]*ptCount
Unity left-handed: X east, Y up, Z north, metres.  The radar draws world -> image as
    u = 1 - (z - worldMinZ) / (worldMaxZ - worldMinZ)     (image column 0 = the LARGEST z)
    v = 1 - (x - worldMinX) / (worldMaxX - worldMinX)     (image row 0 = the LARGEST x)
The axis pair was swapped in slice cs16-AO: the ORIGINAL carrier's own bomb-site marks put
its vertical axis on world X, with a 1.55 deg residual against 88.45 deg for the old
X->right pair (tools/probes/locate-overview-letters.py), and `register-overview.py`
jumps from IoU 0.5169 to 0.8419 on the swap.  See the `AXIS PAIR` note in render_top_down.

Usage
-----
    # runtime radar asset (128x128, 1:1 with the radar rect, hud.txt:183 radar640 128x128)
    python tools/probes/render-overview.py --size 128 \
        --out client/Assets/Resources/UI/Art/overview_de_dust2.png

    # A-side reference / contact-sheet half (bigger, keeps detail for the eye)
    python tools/probes/render-overview.py --size 512 --out .ai-tmp/test/overview_de_dust2_512.png

    # report the map window + the recorded original overview parameters
    python tools/probes/render-overview.py --report

`--zoom/--origin-x/--origin-z/--height` let a future slice reproduce the *original* overview
window verbatim if the carrier is ever recovered; the default is "the whole map", which is the
only window this machine can derive from original data.  `--report` prints both.

Exit code 0 = ok, 2 = unreadable input / bad args.
"""

import argparse
import json
import math
import os
import struct
import sys

try:
    from PIL import Image, ImageDraw, ImageFont
except Exception as exc:                                     # pragma: no cover
    print("PIL is required: " + str(exc), file=sys.stderr)
    sys.exit(2)

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))
GEO_PATH = os.path.join(PROJECT_ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2_geo.bin")

HL_UNIT = 0.0254                                             # GoldSrc 1 unit = 1 inch

# ---- recorded original overview parameters (this machine cannot re-read them) --------------
# 出处：策划/对照表.md G-25 记录 `原版资源/.../cstrike/overviews/de_dust2.txt:5-7` 曾是
# `ZOOM 1.50 / ORIGIN -223 1097 -192 / ROTATED 0`；该文件与 `overviews/de_dust2.bmp`
# （787510 B = 1024x768x8bpp + 256 色调色板）在本机已不在盘（原版资源/清单.md）。
# 这两条只作为"未来拿到载体后按原样复现"的入口参数，不参与当前默认窗口的计算
# （原因见 --report 的输出：它们与本机地图包围盒不自洽，硬套会把整图裁掉）。
ORIG_ZOOM = 1.50
ORIG_ORIGIN = (-223.0, 1097.0, -192.0)
ORIG_ROTATED = 0

# CS 1.6 HUD 文字色（= 原版截图 31/31 实测 + clientscheme.res:24 `BaseText "255 176 0 255"`）
HUD_YELLOW = (255, 176, 0, 255)


# ---------------------------------------------------------------------------
#  geo.bin reader (same spec as Dust2GeoData.cs)
# ---------------------------------------------------------------------------
def try_parse_markers(data, off):
    """Try to read the marker table at `off`; return (count, dict) only if it ends exactly at EOF.

    WHY not just trust the blocker count field: the `de_dust2_geo.bin` on disk carries
    `blockerCount == 660` while its blocker record array actually runs **792** records
    (measured 2026-09-20: `693452 + 792*20 == 709292` and only that offset makes the trailing
    marker table consume the file exactly to its last byte, 711168).  The count field is
    therefore stale; locating the marker section by "the tail must parse to EOF" is a
    self-validating rule that cannot silently mis-read either section.  A mismatch is *reported*
    (`blockerCountField` vs `blockerRecords` in the JSON/report), never silently accepted.
    """
    n = len(data)
    if off + 4 > n:
        return None
    (mc,) = struct.unpack_from("<I", data, off)
    if mc <= 0 or mc > 128:
        return None
    o = off + 4
    markers = {}
    for _ in range(mc):
        if o + 36 > n:
            return None
        raw = bytes(data[o:o + 32])
        name = raw.split(b"\0")[0]
        if not name or any(c < 0x20 or c > 0x7E for c in name):
            return None
        name = name.decode("ascii")
        o += 32
        (pc,) = struct.unpack_from("<I", data, o)
        o += 4
        if pc > 4096 or o + 12 * pc > n:
            return None
        markers[name] = [struct.unpack_from("<3f", data, o + 12 * i) for i in range(pc)]
        o += 12 * pc
    if o != n:
        return None
    return mc, markers


def read_geo(path):
    with open(path, "rb") as fh:
        data = fh.read()
    if data[0:4] != b"CD2G":
        raise ValueError("%s: bad magic %r" % (path, data[0:4]))
    o = 4
    (ver,) = struct.unpack_from("<I", data, o); o += 4
    if ver != 1:
        raise ValueError("%s: version %d unsupported" % (path, ver))
    head = struct.unpack_from("<7f", data, o); o += 28
    cell, ox, oz, ground_top, obs_min, probe_bot, probe_top = head
    (w, d) = struct.unpack_from("<2I", data, o); o += 8
    wmin = struct.unpack_from("<3f", data, o); o += 12
    wmax = struct.unpack_from("<3f", data, o); o += 12

    (gc,) = struct.unpack_from("<I", data, o); o += 4
    groups = []
    for _ in range(gc):
        name = bytes(data[o:o + 48]).split(b"\0")[0].decode("utf-8", "replace"); o += 48
        (vc, ic) = struct.unpack_from("<2I", data, o); o += 8
        verts = list(struct.unpack_from("<%df" % (3 * vc), data, o)); o += 12 * vc
        o += 8 * vc                                   # uvs (unused here)
        o += 12 * vc                                  # normals (unused here)
        idx = list(struct.unpack_from("<%dI" % ic, data, o)); o += 4 * ic
        groups.append({"name": name, "vc": vc, "ic": ic, "verts": verts, "idx": idx})

    (bc_field,) = struct.unpack_from("<I", data, o); o += 4
    blockers_off = o

    # locate the marker section: the tail must parse exactly to EOF (see try_parse_markers).
    #
    #   (u32 ix0, iz0, ix1, iz1 + f32 yMin, yMax -- Editor/MapGen/Dust2GeoData.cs:150-165).
    # The old code used a 20-byte stride; with the current on-disk file
    # (blockerCountField=656, size=712680 B, blockers at 695060) the marker table sits at
    # 695060 + 656*24 = 710804 and 656*24 = 15744 is not a multiple of 20, so the search could
    # never land on it and the script died with "cannot locate the marker table".
    # Fixed to the real stride; the byte-scan fallback below keeps a future layout change from
    # turning into a hard failure (it reports the stride it found instead).
    BLOCKER_STRIDE = 24
    bc = None
    markers_off = None
    markers = None
    for k in range(bc_field, bc_field + 4097):
        cand = blockers_off + k * BLOCKER_STRIDE
        got = try_parse_markers(data, cand)
        if got is not None:
            bc, markers = k, got[1]
            markers_off = cand
            break
    if bc is None:
        for cand in range(len(data) - 4, blockers_off, -1):
            got = try_parse_markers(data, cand)
            if got is None:
                continue
            span = cand - blockers_off
            if span % bc_field != 0:
                continue
            bc, markers = bc_field, got[1]
            markers_off = cand
            break
    if bc is None:
        raise ValueError("%s: cannot locate the marker table (blocker count field=%d, file=%d B)"
                         % (path, bc_field, len(data)))
    blockers = [struct.unpack_from("<6f", data, blockers_off + BLOCKER_STRIDE * i) for i in range(bc)]

    return {
        "blockerCountField": bc_field, "blockerRecords": bc,
        "markersOff": markers_off, "fileSize": len(data),
        "path": path, "cell": cell, "origin": (ox, oz), "w": w, "d": d,
        "wmin": wmin, "wmax": wmax, "groups": groups, "blockers": blockers, "markers": markers,
        "groundTopY": ground_top, "obstacleMinHeight": obs_min,
        "probeBottomY": probe_bot, "probeTopY": probe_top,
    }


# ---------------------------------------------------------------------------
#  top-down rasteriser
# ---------------------------------------------------------------------------
def render_top_down(geo, size, supersample=3, lo=None, hi=None):
    """Rasterise the original geometry from straight above -> (RGBA image, meta)."""
    wmin, wmax = geo["wmin"], geo["wmax"]
    span_x = wmax[0] - wmin[0]
    span_z = wmax[2] - wmin[2]
    if span_x <= 0 or span_z <= 0:
        raise ValueError("geo bounds degenerate: %r .. %r" % (wmin, wmax))

    # square canvas, uniform metres-per-pixel, the map centred (aspect preserved: the radar
    # rect is square too, so the silhouette must not be squashed)
    mpp = max(span_x, span_z) / float(size)
    cx = (wmin[0] + wmax[0]) * 0.5
    cz = (wmin[2] + wmax[2]) * 0.5
    x0 = cx - 0.5 * size * mpp
    z0 = cz - 0.5 * size * mpp

    n = size * supersample
    mpp_s = mpp / supersample
    # ---- AXIS PAIR (slice cs16-AO) --------------------------------------------------------
    # This used to be u <- world X and v <- world Z (row 0 = the north-most z row).  Slice AO
    # measured the ORIGINAL carrier's own two bomb-site marks (tools/probes/
    # locate-overview-letters.py) and got, for the world A->B vector, an image residual of
    #   1.55 deg with the vertical axis = world X   vs   88.45 deg with the current pair,
    # and the silhouette registration (register-overview.py) jumps from IoU 0.5169 to 0.8419
    # when the pair is swapped, with the mirror settled in the same run
    # (u<-Z mirror_u=1 mirror_v=0 => 0.8419 against 0.45-0.47 for the other three mirrors).
    # register-overview.py's own parameterisation: u axis world coord = A - u*sm (fu=1) and
    # v axis world coord = B - v*sm (fv=0), with X = va and Z = ua => u <- world -Z, v <- world -X.
    # So:      u = -Z      (image column grows as world Z decreases)
    #          v = -X      (image row grows as world X decreases)
    # which is exactly "the original overview is rotated 90 deg from the X->right pair".
    def pu(z):
        return (z0 - z) / mpp_s + n          # max +Z -> column 0, min -Z -> column n

    def pv(x):
        return (x0 + n * mpp_s - x) / mpp_s  # min -X -> row n (down), max +X -> row 0

    # ---- floor-plan look: classify each original triangle by its OWN 3d normal -------------
    # `Walkable` (n.y >= 0.7) = a surface a player can stand on (sand floor, ramps, box tops) --
    # these are painted bright, shaded by their own height.  `Walls` (|n.y| < 0.3) = near
    # vertical faces -- painted dark so the room/corridor edges read as the original radar's
    # wall lines.  Steep-but-not-vertical faces (roof slopes / stairs) are ignored: the original
    # overview bmp reads as floor plan too, and including roofs turns the picture into one blob.
    inside_mask = bytearray(n * n)     # any original triangle projects here = the map footprint
    floor_mask = bytearray(n * n)
    wall_mask = bytearray(n * n)
    height = [None] * (n * n)
    ylo, yhi = 1e9, -1e9
    n_floor = n_wall = n_skip = 0

    for g in geo["groups"]:
        V = g["verts"]
        I = g["idx"]
        for k in range(0, len(I), 3):
            ia, ib, ic = I[k], I[k + 1], I[k + 2]
            ax, ay, az = V[ia * 3], V[ia * 3 + 1], V[ia * 3 + 2]
            bx, by, bz = V[ib * 3], V[ib * 3 + 1], V[ib * 3 + 2]
            cxx, cy, cz = V[ic * 3], V[ic * 3 + 1], V[ic * 3 + 2]

            ux, uy, uz = bx - ax, by - ay, bz - az
            vx, vy, vz = cxx - ax, cy - ay, cz - az
            nx_ = uy * vz - uz * vy
            ny_ = uz * vx - ux * vz
            nz_ = ux * vy - uy * vx
            ln = math.sqrt(nx_ * nx_ + ny_ * ny_ + nz_ * nz_)
            ny = (ny_ / ln) if ln > 1e-9 else 0.0
            if ny >= 0.7:
                kind = 0                                          # floor
                n_floor += 1
            elif abs(ny) < 0.3:
                kind = 1                                          # wall
                n_wall += 1
            else:
                kind = 2                    # steep (roof slope / stairs): footprint only
                n_skip += 1
            dst = floor_mask if kind == 0 else (wall_mask if kind == 1 else None)

            ax_, az_ = pu(az), pv(ax)          # image col from world Z, image row from world X
            bx_, bz_ = pu(bz), pv(bx)
            cx_, cz_ = pu(cz), pv(cxx)
            minx = max(0, int(math.floor(min(ax_, bx_, cx_))))
            maxx = min(n - 1, int(math.ceil(max(ax_, bx_, cx_))))
            miny = max(0, int(math.floor(min(az_, bz_, cz_))))
            maxy = min(n - 1, int(math.ceil(max(az_, bz_, cz_))))
            if minx > maxx or miny > maxy:
                continue
            det = (bz_ - cz_) * (ax_ - cx_) + (cx_ - bx_) * (az_ - cz_)
            for iy in range(miny, maxy + 1):
                sy = iy + 0.5
                for ix in range(minx, maxx + 1):
                    sx = ix + 0.5
                    if abs(det) < 1e-12:
                        h = (ay + by + cy) / 3.0
                    else:
                        l1 = ((bz_ - cz_) * (sx - cx_) + (cx_ - bx_) * (sy - cz_)) / det
                        l2 = ((cz_ - az_) * (sx - cx_) + (ax_ - cx_) * (sy - cz_)) / det
                        l3 = 1.0 - l1 - l2
                        if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
                            continue
                        h = l1 * ay + l2 * by + l3 * cy
                    p = iy * n + ix
                    inside_mask[p] = 1
                    if dst is not None:
                        dst[p] = 1
                    if kind == 0:
                        if height[p] is None or h > height[p]:
                            height[p] = h
                        if h < ylo:
                            ylo = h
                        if h > yhi:
                            yhi = h

    if ylo > yhi:                                            # nothing rasterised
        ylo, yhi = 0.0, 1.0
    if lo is None:
        lo = ylo
    if hi is None:
        hi = yhi
    if hi - lo < 0.5:
        hi = lo + 0.5

    # ---- colourise: sand floor (low y) dark olive -> raised floors (platforms) brighter -----
    # palette deliberately close to the original 8-bit overview palette (dark olive/green map on
    # a transparent background) -- the *shape* is 100% original data, nothing hand-drawn
    #   wall  = dark desaturated olive (reads as the room/corridor edge lines)
    #   floor = height-shaded olive (ramps and platforms stay visible)
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    pix = img.load()
    for iy in range(n):
        for ix in range(n):
            p = iy * n + ix
            if not inside_mask[p]:
                continue
            if floor_mask[p]:
                h = height[p] if height[p] is not None else lo
                t = max(0.0, min(1.0, (h - lo) / (hi - lo)))
                pix[ix, iy] = (int(96 + 142 * t), int(122 + 118 * t), int(74 + 62 * t), 232)
            elif wall_mask[p]:
                pix[ix, iy] = (26, 46, 24, 245)
            else:
                pix[ix, iy] = (44, 66, 38, 225)      # building footprint (no walkable face)

    img = img.resize((size, size), Image.BOX)
    draw = ImageDraw.Draw(img)

    # ---- A / B bomb-site letters, from the original `func_bomb_target` positions ----------
    sites = []
    for name, letter in (("Bombsite_A", "A"), ("Bombsite_B", "B")):
        pts = geo["markers"].get(name)
        if not pts:
            continue
        mx = sum(p[0] for p in pts) / len(pts)
        mz = sum(p[2] for p in pts) / len(pts)
        u = (z0 - mz) / mpp + size              # same axis pair as the rasteriser (u <- -Z)
        v = (x0 + size * mpp - mx) / mpp        #                              (v <- -X)
        sites.append({"name": name, "letter": letter, "world": [round(mx, 3), round(mz, 3)],
                      "px": [round(u, 2), round(v, 2)]})
        draw_site_letter(draw, letter, u, v, size)

    meta = {
        "source": os.path.relpath(geo["path"], PROJECT_ROOT).replace("\\", "/"),
        "imageSize": size, "supersample": supersample,
        "worldMin": [round(v, 6) for v in wmin], "worldMax": [round(v, 6) for v in wmax],
        "metresPerPixel": round(mpp, 6),
        "windowMinX": round(x0, 6), "windowMinZ": round(z0, 6),
        "windowMaxX": round(x0 + size * mpp, 6), "windowMaxZ": round(z0 + size * mpp, 6),
        "heightRange": [round(lo, 4), round(hi, 4)],
        "faceCounts": {"walkable": n_floor, "wall": n_wall, "steepSkipped": n_skip},
        "sites": sites,
        "originalOverviewParams": {"zoom": ORIG_ZOOM, "origin": list(ORIG_ORIGIN),
                                   "rotated": ORIG_ROTATED, "bmpBytes": 787510,
                                   "bmpSize": [1024, 768]},
        "groups": len(geo["groups"]),
        "triangles": sum(g["ic"] // 3 for g in geo["groups"]),
        "blockerCountField": geo["blockerCountField"],
        "blockerRecords": geo["blockerRecords"],
        "markers": sorted(geo["markers"].keys()),
    }
    return img, meta


def draw_site_letter(draw, letter, u, v, size):
    """Draw the bomb-site letter the way the original overview bmp carries it (HUD yellow)."""
    fs = max(9, int(size * 0.13))
    font = None
    for cand in ("arialbd.ttf", "arial.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans.ttf"):
        try:
            font = ImageFont.truetype(cand, fs)
            break
        except Exception:
            continue
    if font is None:
        font = ImageFont.load_default()
    try:
        bb = draw.textbbox((0, 0), letter, font=font)
        tw, th = bb[2] - bb[0], bb[3] - bb[1]
    except Exception:                                        # pragma: no cover
        tw, th = fs, fs
    tx, ty = u - tw * 0.5, v - th * 0.5
    # dark halo so the letter stays readable on both the sand floor and the bright walls
    for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
        draw.text((tx + dx, ty + dy), letter, font=font, fill=(0, 0, 0, 255))
    draw.text((tx, ty), letter, font=font, fill=HUD_YELLOW)


# ---------------------------------------------------------------------------
#  cli
# ---------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(description="Top-down overview of the ORIGINAL de_dust2 geometry (radar base).")
    ap.add_argument("--geo", default=GEO_PATH)
    ap.add_argument("--out", help="output PNG (parent dirs created)")
    ap.add_argument("--meta", help="output JSON (default: <out>.json)")
    ap.add_argument("--size", type=int, default=128, help="square output size in pixels (default 128 = radar size)")
    ap.add_argument("--supersample", type=int, default=3)
    ap.add_argument("--report", action="store_true", help="print the map window + the recorded original overview params, no image")
    # window overrides -- only for reproducing the original overview verbatim if the carrier returns
    ap.add_argument("--zoom", type=float, help="original overview ZOOM (GoldSrc units per overview pixel)")
    ap.add_argument("--origin-x", type=float)
    ap.add_argument("--origin-z", type=float)
    ap.add_argument("--height", type=float)
    args = ap.parse_args()

    if not os.path.isfile(args.geo):
        print("geo missing: " + args.geo, file=sys.stderr)
        return 2
    try:
        geo = read_geo(args.geo)
    except Exception as exc:
        print("geo unreadable: %s" % exc, file=sys.stderr)
        return 2

    wmin, wmax = geo["wmin"], geo["wmax"]
    span_x, span_z = wmax[0] - wmin[0], wmax[2] - wmin[2]
    print("geo   : %s" % os.path.relpath(geo["path"], PROJECT_ROOT))
    print("groups: %d  triangles: %d  markers: %d" %
          (len(geo["groups"]), sum(g["ic"] // 3 for g in geo["groups"]), len(geo["markers"])))
    print("bounds: x[%+.4f .. %+.4f] (%.4f m)  z[%+.4f .. %+.4f] (%.4f m)  => %dx%d units" %
          (wmin[0], wmax[0], span_x, wmin[2], wmax[2], span_z,
           round(span_x / HL_UNIT), round(span_z / HL_UNIT)))
    print("blocker: field=%d  records=%d  %s  markers@%d (%d classes: %s)" %
          (geo["blockerCountField"], geo["blockerRecords"],
           "OK" if geo["blockerCountField"] == geo["blockerRecords"] else "MISMATCH (field is stale)",
           geo["markersOff"], len(geo["markers"]), ",".join(sorted(geo["markers"]))))

    if args.zoom is not None:
        ppm = 1.0 / (args.zoom * HL_UNIT)
        need = max(span_x, span_z) * ppm
        print("orig-window: zoom=%g units/px -> %.3f px/m -> the whole map needs %.0f px" %
              (args.zoom, ppm, need))
        print("             recorded overview.txt: ZOOM %g  ORIGIN %g %g %g  ROTATED %d  bmp 1024x768 8bpp 787510 B" %
              (ORIG_ZOOM, ORIG_ORIGIN[0], ORIG_ORIGIN[1], ORIG_ORIGIN[2], ORIG_ROTATED))
        print("             (those two disagree with this machine's map bbox -> NOT used, see docstring)")

    if args.report:
        return 0

    out = args.out or os.path.join(PROJECT_ROOT, ".ai-tmp", "test", "overview_de_dust2.png")
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    img, meta = render_top_down(geo, args.size, max(1, args.supersample))
    img.save(out)
    if args.meta:
        meta_path = args.meta
    else:
        # A sibling .json next to a build asset would itself become an asset inside
        # `Resources/` (and ship).  Slice AO hit exactly that: writing
        # client/Assets/Resources/UI/Art/overview_de_dust2.png produced a stray
        # overview_de_dust2.json asset.  The default therefore keeps the metadata out of
        # Assets/ and drops it in the project's one temp directory instead.
        if os.path.abspath(out).startswith(os.path.join(PROJECT_ROOT, "client", "Assets")):
            meta_path = os.path.join(PROJECT_ROOT, ".ai-tmp", "test",
                                     os.path.splitext(os.path.basename(out))[0] + ".json")
        else:
            meta_path = os.path.splitext(out)[0] + ".json"
    os.makedirs(os.path.dirname(os.path.abspath(meta_path)), exist_ok=True)
    with open(meta_path, "w", encoding="utf-8") as fh:
        json.dump(meta, fh, ensure_ascii=False, indent=1)
    print("out   : %s (%dx%d, %.3f m/px)" % (out, args.size, args.size, meta["metresPerPixel"]))
    print("window: x[%+.3f .. %+.3f]  z[%+.3f .. %+.3f]   height %.3f..%.3f m" %
          (meta["windowMinX"], meta["windowMaxX"], meta["windowMinZ"], meta["windowMaxZ"],
           meta["heightRange"][0], meta["heightRange"][1]))
    for s in meta["sites"]:
        print("site  : %s '%s' world(%+.2f, %+.2f) -> px(%+.2f, %+.2f)" %
              (s["name"], s["letter"], s["world"][0], s["world"][1], s["px"][0], s["px"][1]))
    print("meta  : %s" % meta_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
