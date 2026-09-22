#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
import-original-overview.py -- copy the ORIGINAL GoldSrc overview bitmap into the project as a
PNG, pixel-exactly (no redraw, no resample, no "approximation").

WHY THIS PROBE EXISTS (slice cs16-AS)
-------------------------------------
The radar underlay used by this project was, until now, a picture PROCEDURALLY RASTERISED from
our own geometry (`tools/probes/render-overview.py`), because the original carrier was believed
to be missing (差异登记 #51). Slice AR fetched the real carrier from a public repack:

    source   : 原版资源/cs16src/cstrike/cstrike__overviews__de_dust2.bmp
               1024x768 8bpp paletted, 787510 B, palette index 110 = rgb(0,255,0) = key colour
    companion: 原版资源/cs16src/cstrike/overviews/de_dust2.txt
               (ZOOM 1.50 / ORIGIN -223 1097 -192 / ROTATED 0) -- the numbers
               `tools/probes/overview-window.py` turns into the world window the radar uses.

This probe performs ONLY the container conversion BMP -> PNG:

  * palette index -> RGB is a verbatim table copy (`struct` read of the palette that follows the
    BITMAPINFOHEADER);
  * rows are bottom-up in BMP and flipped top-down for PNG -- a flip, not a resample;
  * the key colour (palette index 110) becomes alpha 0, which is exactly what GoldSrc does with
    it (the overview is drawn with the key colour transparent); the RGB of those pixels is left
    verbatim, nothing is "cleaned up";
  * **every** other pixel keeps its exact RGB triple and alpha 255.

So the result carries the carrier's colour information byte for byte; it is a re-containerisation,
not a redraw. Re-decoding the produced PNG and comparing it against the BMP must yield
`rgb_mismatch=0 / alpha_mismatch=0` -- that assertion is printed and is the point of the probe
(exit 1 if it ever fails).

Usage
-----
    python tools/probes/import-original-overview.py            # convert + self-check
    python tools/probes/import-original-overview.py --check     # verify only (no write)
Exit 0 = written and pixel-exact; 1 = mismatch; 2 = carrier/destination missing.
"""

import argparse
import hashlib
import os
import struct
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.dirname(os.path.dirname(HERE))

SRC = os.path.join(PROJECT_ROOT, "原版资源", "cs16src", "cstrike",
                   "cstrike__overviews__de_dust2.bmp")
DST = os.path.join(PROJECT_ROOT, "client", "Assets", "Resources", "UI", "Art",
                   "overview_de_dust2.png")


def read_bmp8(path):
    """Return (rgba uint8 HxWx4 top-down, indices uint8 HxW, key_index, raw bytes)."""
    d = open(path, "rb").read()
    if d[:2] != b"BM":
        raise ValueError("not a BMP: %s" % path)
    (data_off,) = struct.unpack_from("<I", d, 10)
    dib, w, h, _planes, bpp, comp, _imgsz = struct.unpack_from("<IiiHHII", d, 14)
    if bpp != 8 or comp != 0:
        raise ValueError("expected 8bpp uncompressed, got bpp=%d comp=%d" % (bpp, comp))
    pal_off = 14 + dib
    pal = np.frombuffer(d, dtype=np.uint8, count=256 * 4, offset=pal_off).reshape(256, 4)
    ah = abs(h)
    stride = (w + 3) // 4 * 4
    idx = np.frombuffer(d, dtype=np.uint8, count=stride * ah,
                        offset=data_off).reshape(ah, stride)[:, :w].copy()
    if h > 0:                                    # positive height = bottom-up rows
        idx = idx[::-1]
    key = int(np.argmax(np.bincount(idx.ravel(), minlength=256)))
    rgba = np.empty((ah, w, 4), dtype=np.uint8)
    rgba[..., 0] = pal[idx, 2]                   # palette quads are B,G,R,A
    rgba[..., 1] = pal[idx, 1]
    rgba[..., 2] = pal[idx, 0]
    rgba[..., 3] = np.where(idx == key, 0, 255).astype(np.uint8)
    return rgba, idx, key, d


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="verify the PNG against the BMP; do not write")
    args = ap.parse_args(argv)

    if not os.path.isfile(SRC):
        print("carrier missing: %s" % SRC)
        return 2

    rgba, idx, key, raw = read_bmp8(SRC)
    h, w = idx.shape
    print("carrier : %s" % os.path.relpath(SRC, PROJECT_ROOT))
    print("          %dx%d 8bpp %d B  sha256 %s" %
          (w, h, len(raw), hashlib.sha256(raw).hexdigest()))
    keyed = int((idx == key).sum())
    print("key idx : %d  rgb(%d,%d,%d)  %d px (%.1f%%) -> alpha 0 ; RGB left verbatim" %
          (key, rgba[0, 0, 0], rgba[0, 0, 1], rgba[0, 0, 2], keyed, 100.0 * keyed / (w * h)))

    if not args.check:
        os.makedirs(os.path.dirname(DST), exist_ok=True)
        Image.fromarray(rgba).save(DST, optimize=False)
        print("wrote   : %s (%d B)" % (os.path.relpath(DST, PROJECT_ROOT), os.path.getsize(DST)))

    if not os.path.isfile(DST):
        print("dst missing: %s" % DST)
        return 2

    back = np.asarray(Image.open(DST).convert("RGBA"), dtype=np.uint8)
    if back.shape != rgba.shape:
        print("FAIL: png shape %s != bmp shape %s" % (back.shape, rgba.shape))
        return 1
    rgb_bad = int((back[..., :3] != rgba[..., :3]).any(axis=-1).sum())
    a_bad = int((back[..., 3] != rgba[..., 3]).sum())
    print("verify  : png %dx%d  rgb_mismatch=%d  alpha_mismatch=%d  (of %d px)" %
          (back.shape[1], back.shape[0], rgb_bad, a_bad, w * h))
    if rgb_bad or a_bad:
        print("verdict : FAIL -- the PNG is not a verbatim copy of the carrier")
        return 1
    print("verdict : PASS -- PNG is a byte-exact re-containerisation of the original .bmp")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
