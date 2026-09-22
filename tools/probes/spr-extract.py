#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
spr-extract.py -- decode a GoldSrc `.spr` sprite (indexed, 8bpp) and cut a frame region
                   out of it, exactly as `sprites/hud.txt` declares it.

WHY THIS EXISTS (slice cs16-AM, differences #15/#16/#52)
--------------------------------------------------------
`client/Assets/Resources/UI/Art/*.png` (HUD icons, and the FX stand-ins `fx_*.png`) were
derived from the original carriers back when `原版资源/cs16src/**` was still on disk.
Slice AL re-fetched the carriers (`640hud*.spr`, `muzzleflash*.spr`, `radar*.spr`,
`decals.wad`, `hud.txt`) into `原版资源/cs16src/cstrike/`.  This tool re-decodes them so the
in-project PNGs can be refreshed from (or checked against) the original pixels.

FORMAT PROVENANCE -- READ THIS BEFORE TRUSTING THE OFFSETS
----------------------------------------------------------
An external spec could NOT be obtained in this slice (tried, all 404/403, logged in
`.ai-tmp/test/spr-ref.tsv`):
  * `raw.githubusercontent.com/ValveSoftware/halflife/master/{utils/common,common,engine}/sprite.h`
  * `raw.githubusercontent.com/FWGS/xash3d-fwgs/master/engine/{client,common}/sprite.h`
  * GitHub tree API for FWGS/xash3d-fwgs, ValveSoftware/halflife, Solokiller/hlsdk-portable
So the layout below is taken **from the carriers themselves** and is asserted by the files:

  header (40 bytes, little endian)
    +0   char[4] "IDSP"      +4  int version (2)     +8  int type        +12 int texFormat
    +16  float boundingRadius                        +20 int width       +24 int height
    +28  int numFrames       +32 float beamLength    +36 int synctype
  +40   2 bytes, constant 00 01 in every carrier examined (value not used by this tool)
  +42   palette: 256 * RGB (768 bytes)  -- for `640hud7.spr` this is exactly
        (0,0,0),(1,1,1),(2,2,2)...(255,255,255), i.e. the additive grey mask the project
        already documents for the HUD icons (`策划/差异登记.tsv` #22)
  +810  frame table: numFrames * 20 bytes; each entry read as 5 int32, observed as
        (0, -W/2, +W/2, W, H)   e.g. 640hud7 -> (0,-128,128,256,256), radar640 -> (0,-64,64,128,128)
  +810 + 20*numFrames   pixel data: 1 byte per pixel, frames back to back,
        sum(w*h) bytes, ending **exactly** at EOF

  ASSERTED, not assumed: for all five carriers examined
  (`640hud7`, `radar640`, `radar320`, `muzzleflash1`, `muzzleflash2`) the size identity
      filesize == 42 + 768 + 20*numFrames + sum(frame w*h)
  holds **exactly**, and `boundingRadius == sqrt((w/2)^2 + (h/2)^2)` holds for each.

  Transparency: pixel index 255 is the transparent colour (this is what the muzzle flash
  carrier uses for its surrounding pixels -- its first 40 pixel bytes are 0xFF), so by
  default index 255 -> alpha 0.  `--additive` additionally turns the luminance into alpha,
  which is what the *additive* (texFormat 1) HUD masks need to look right under a plain
  alpha-blended UI sprite; pass it only when comparing/refreshing those.

Usage
-----
    # report the header + frame table of a carrier
    python tools/probes/spr-extract.py --in <carrier.spr> --info

    # cut `stopwatch 640 640hud7 144 72 24 24` (hud.txt:127) out into a PNG
    python tools/probes/spr-extract.py --in <640hud7.spr> --rect 144,72,24,24 --out stopwatch.png

    # a whole frame (frame 0) of a multi-frame FX sprite
    python tools/probes/spr-extract.py --in <muzzleflash1.spr> --frame 0 --out fx_muzzleflash.png

Exit 0 = ok, 2 = unreadable input / the size identity above does not hold.
"""

import argparse
import os
import struct
import sys

SPR_MAGIC = b"IDSP"
HEADER_LEN = 40
PALETTE_OFF = 42
PALETTE_LEN = 768
FRAMES_OFF = PALETTE_OFF + PALETTE_LEN          # 810
FRAME_STRIDE = 20
TRANSPARENT_INDEX = 255


class Sprite(object):
    def __init__(self, path):
        with open(path, "rb") as fh:
            self.data = fh.read()
        self.path = path
        d = self.data
        if len(d) < FRAMES_OFF or d[:4] != SPR_MAGIC:
            raise ValueError("%s: not a GoldSrc sprite (magic %r)" % (path, d[:4]))
        (self.ident, self.version, self.type, self.tex_format, self.bounding_radius,
         self.width, self.height, self.num_frames, self.beam_length,
         self.synctype) = struct.unpack_from("<iiiifiiifi", d, 0)
        self.palette = d[PALETTE_OFF:PALETTE_OFF + PALETTE_LEN]
        self.frames = []
        for i in range(self.num_frames):
            off = FRAMES_OFF + FRAME_STRIDE * i
            self.frames.append(struct.unpack_from("<5i", d, off))
        self.pixel_off = FRAMES_OFF + FRAME_STRIDE * self.num_frames
        # frame 0 carries the size; trailing slots of a multi-frame carrier may be unwritten
        ref = self.frames[0]
        self.frame_w, self.frame_h = ref[3], ref[4]
        for i in range(1, self.num_frames):
            if self.frames[i][3] <= 0 or self.frames[i][4] <= 0:
                self.frames[i] = (self.frames[i][0], self.frames[i][1], self.frames[i][2],
                                  self.frame_w, self.frame_h)
        self.pixel_len = sum(f[3] * f[4] for f in self.frames)

    def check(self):
        """Return (ok, detail) for the two identities this tool relies on."""
        want = self.pixel_off + self.pixel_len
        r_want = (self.width * 0.5) ** 2 + (self.height * 0.5) ** 2
        r_got = self.bounding_radius ** 2
        ok_size = (want == len(self.data))
        ok_radius = (abs(r_got - r_want) <= 1e-3 * max(1.0, r_want))
        detail = ("size: %d + %d + %d*%d + %d = %d (file %d)%s | "
                  "radius: %.4f vs sqrt((w/2)^2+(h/2)^2)=%.4f%s"
                  % (PALETTE_OFF, PALETTE_LEN, FRAME_STRIDE, self.num_frames, self.pixel_len,
                     want, len(self.data), "" if ok_size else "  << MISMATCH",
                     self.bounding_radius, r_want ** 0.5, "" if ok_radius else "  << MISMATCH"))
        return (ok_size and ok_radius), detail

    def frame_pixels(self, frame):
        """1 byte per pixel for `frame`, row-major, top-left first."""
        off = self.pixel_off
        for i in range(frame):
            off += self.frames[i][3] * self.frames[i][4]
        w, h = self.frames[frame][3], self.frames[frame][4]
        return self.data[off:off + w * h], w, h

    def to_rgba(self, frame=0, additive=False, transparent_index=TRANSPARENT_INDEX,
                white_mask=False):
        """(bytes RGBA, w, h) for one whole frame.

        `white_mask` reproduces the convention the project already uses for HUD icons
        (`client/Assets/Resources/UI/Art/hud_cross.png` and friends): the grey of the
        additive mask is carried in the ALPHA channel and the RGB is left pure white, so the
        runtime tint (`CsHudTheme.HudIconTint`) decides the colour.  See 策划/差异登记.tsv #22.
        """
        px, w, h = self.frame_pixels(frame)
        out = bytearray(w * h * 4)
        for i, idx in enumerate(px):
            r, g, b = self.palette[idx * 3:idx * 3 + 3]
            a = 0 if idx == transparent_index else 255
            if additive and a:
                a = max(r, g, b)
            if white_mask:
                r = g = b = 255
            out[i * 4:i * 4 + 4] = bytes((r, g, b, a))
        return bytes(out), w, h


def cut_rect(sprite, x, y, w, h, frame=0, additive=False, transparent_index=TRANSPARENT_INDEX,
             white_mask=False):
    """Cut `w x h` at (x, y) out of the whole-sprite atlas in `frame`."""
    rgba, fw, fh = sprite.to_rgba(frame=frame, additive=additive,
                                  transparent_index=transparent_index, white_mask=white_mask)
    if x + w > fw or y + h > fh:
        raise ValueError("rect %d,%d,%d,%d outside frame %dx%d" % (x, y, w, h, fw, fh))
    out = bytearray(w * h * 4)
    for row in range(h):
        src = ((y + row) * fw + x) * 4
        out[row * w * 4:(row + 1) * w * 4] = rgba[src:src + w * 4]
    return bytes(out), w, h


def write_png(rgba, w, h, out_path):
    from PIL import Image
    Image.frombytes("RGBA", (w, h), rgba).save(out_path)
    return out_path


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="src", required=True, help="carrier .spr")
    ap.add_argument("--out", help="output PNG (omit with --info)")
    ap.add_argument("--info", action="store_true", help="print header + frame table only")
    ap.add_argument("--rect", help="x,y,w,h inside the frame (hud.txt source rect)")
    ap.add_argument("--frame", type=int, default=0, help="frame index (default 0)")
    ap.add_argument("--additive", action="store_true",
                    help="texFormat 1: use luminance as alpha (HUD masks)")
    ap.add_argument("--keep-255", action="store_true",
                    help="do NOT treat palette index 255 as transparent")
    ap.add_argument("--white-mask", action="store_true",
                    help="grey -> alpha, RGB -> pure white (the project's HUD-icon convention)")
    args = ap.parse_args(argv)

    try:
        s = Sprite(args.src)
    except (OSError, ValueError) as e:
        sys.stderr.write("cannot read sprite: %s\n" % e)
        return 2

    ok, detail = s.check()
    print("carrier: %s" % args.src)
    print("header : ident=%r version=%d type=%d texFormat=%d radius=%.4f size=%dx%d frames=%d sync=%d"
          % (s.ident.to_bytes(4, "little"), s.version, s.type, s.tex_format,
             s.bounding_radius, s.width, s.height, s.num_frames, s.synctype))
    print("layout : palette@%d(%dB) frames@%d stride=%d pixels@%d len=%d  file=%d"
          % (PALETTE_OFF, PALETTE_LEN, FRAMES_OFF, FRAME_STRIDE, s.pixel_off,
             s.pixel_len, len(s.data)))
    print("identity: %s" % detail)
    print("checks : %s" % ("OK" if ok else "FAIL"))
    for i, f in enumerate(s.frames):
        print("  frame[%d] = (0?=%d, %d, %d, w=%d, h=%d)" % ((i,) + f))
    if args.info:
        return 0 if ok else 2
    if not args.out:
        sys.stderr.write("--out is required unless --info is given\n")
        return 2

    ti = 999 if args.keep_255 else TRANSPARENT_INDEX
    try:
        if args.rect:
            x, y, w, h = (int(v) for v in args.rect.split(","))
            rgba, w, h = cut_rect(s, x, y, w, h, frame=args.frame,
                                 additive=args.additive, transparent_index=ti,
                                 white_mask=args.white_mask)
        else:
            rgba, w, h = s.to_rgba(frame=args.frame, additive=args.additive,
                                   transparent_index=ti, white_mask=args.white_mask)
    except ValueError as e:
        sys.stderr.write("cannot extract: %s\n" % e)
        return 2

    write_png(rgba, w, h, args.out)
    print("wrote  : %s (%dx%d%s)" % (args.out, w, h, ", additive" if args.additive else ""))
    return 0 if ok else 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
