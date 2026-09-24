#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
wad3-extract.py -- decode a GoldSrc `WAD3` container (`cstrike/decals.wad`) and export the
                    `miptex` lumps (bullet-hole decals) back to PNG.

WHY THIS EXISTS (slice cs16-AW, difference #52 / the fx stand-ins)
-----------------------------------------------------------------
`原版资源/cs16src/cstrike/decals.wad` (960,012 B) was re-fetched by slice AL but never
decoded.  The in-project `client/Assets/Resources/UI/Art/fx_bullethole.png` is a stand-in
that must be replaced by the *original* decal pixels, at the original size.

FORMAT PROVENANCE -- READ THIS BEFORE TRUSTING THE OFFSETS
----------------------------------------------------------
The layout is taken from the GoldSrc SDK shipped with the carriers, not from memory:

  * `utils/common/wadlib.c` (OGS Engine SDK / HLSDK lineage)  -- `W_OpenWad()`:
        if (strncmp(header.identification,"WAD2",4) &&
            strncmp(header.identification,"WAD3",4)) Error(...);
        header.numlumps    = LittleLong(header.numlumps);
        header.infotableofs= LittleLong(header.infotableofs);
        fseek(wadhandle, header.infotableofs, SEEK_SET);
        SafeRead(wadhandle, lumpinfo, numlumps*sizeof(lumpinfo_t));
        lump_p->filepos = LittleLong(...);  lump_p->size = LittleLong(...);
  * `public/wadtypes.h` (same SDK) -- structs used verbatim below:
        wadinfo_t { char identification[4]; int numlumps; int infotableofs; }
        lumpinfo_t{ int filepos; int disksize; int size; char type; char compression;
                    char pad1, pad2; char name[16]; }
        TYP_MIPTEX == 67  (== TYP_LUMPY(64) + grab-command index 3, cf. qlumpy commands[])
  * `utils/qlumpy/quakegrb.c` `GrabMip()` -- miptex_t { char name[16]; unsigned width,height;
        unsigned offsets[4]; } and `offsets[i] = lump_p - (byte*)qtex`, i.e. offsets are
        relative to the *start of the lump*.  Mip level k is (w>>k) x (h>>k), 1 byte/texel.

WAD3 vs WAD2 -- the palette
---------------------------
WAD3 appends a palette block to each miptex lump.  The widely-cited formula
`palette = lump + offsets[3] + (w>>3)*(h>>3)` does NOT fit this carrier: measured over all
225 lumps of `decals.wad`, the block actually is

        +0   uint16  colour count, == 256 for 225/225 lumps
        +2   byte[768]  RGB palette (256 entries)
        +770 uint16  == 0x0000 filler

i.e. with e3 = offsets[3] + (width>>3)*(height>>3)  (end of the 1/8 mip):
        lump_size == e3 + 2 + 768 + 2       holds exactly for 225/225 lumps
        u16@e3    == 256                    holds exactly for 225/225 lumps
This is ASSERTED below for every miptex lump -- it is the self-proof that the parse is right,
instead of an unchecked constant.  (Q: could the layout be [mips][pad][count][palette]?  No:
then u16@e3 would be 0, but it is 256 in every lump.)

Decals vs masked textures  (READ BEFORE TRUSTING THE ALPHA)
----------------------------------------------------------
Every lump in `decals.wad` carries a `{` prefix, but a *decal* `{` texture is NOT the same
thing as a *masked* `{` texture (railings / ladders, where palette index 255 is the
see-through colour).  The decal convention is:

  * the image is a **grayscale opacity mask** -- a DARK `palette[index]` means "more opaque",
    WHITE means "transparent" (the palette ramps black<->white; "palette index == opacity");
  * `palette[255]` is the **base colour of the whole decal** (bullet holes = black, blood =
    dark red, yellow blood = ochre) and it **must not appear in the pixel data**;
  * the background is index 0 == pure white == opacity 0, i.e. exactly "white == invisible".

Provenance (three independent sources, mutually consistent):
  * TWHL wiki `Texture` -- "{ (decal) textures uses palette index #255 (last index) as base
    colour of the whole decal, which are otherwise monochromatic.  The colour index #255 must
    not be used in the image itself.";  and `Tutorial: Decals: All You Need To Know` -- "the
    palette runs from black to white defining the opacity, except the last index defines the
    base colour ... what matters is the palette index == opacity".
  * robmikh, "Finished bullet holes" (a GoldSrc re-implementation dev log) -- "Each pixel is
    really a grayscale pixel ... The last color in the palette is the real color that should
    be used for the decal."
  * GameBanana, "Creating your own decals" -- "the darker parts will be more solid than the
    lighter parts (White = invisible)."
Carrier self-consistency, ASSERTED for every `{` lump below: index 255 never occurs in the
pixels, and the corner/background index maps to opacity 0.

The pre-2026-09-24 revision mapped decals with the *masked-texture* rule instead (every
non-background texel -> alpha 255, RGB taken from its own palette entry).  That turns the
WHITE end of the opacity ramp into **opaque white ink**; the user-visible symptom was the
bullet mark rendering as a white blob -- "弹痕还是一个白点" (difference #69, 2026-09-24).

Usage
-----
    # list every lump + the self-check totals
    python tools/probes/wad3-extract.py --in <decals.wad> --info

    # export one lump (mip0) to PNG
    python tools/probes/wad3-extract.py --in <decals.wad> --name "{shot1" --out shot1.png

    # export every lump whose name starts with a prefix
    python tools/probes/wad3-extract.py --in <decals.wad> --prefix "{shot" --outdir out/

Exit 0 = ok, 2 = unreadable input / a self-check identity does NOT hold.
"""

import argparse
import os
import struct
import sys
import zlib

WADINFO_LEN = 12
LUMPINFO_LEN = 32
TYP_MIPTEX = 67
MIPTEX_HDR_LEN = 40          # name[16] + width + height + offsets[4]
PALETTE_ENTRIES = 256
PALETTE_LEN = PALETTE_ENTRIES * 3


def read_wad(path):
    """Return (raw_bytes, header_dict, [lump_dict, ...])."""
    raw = open(path, "rb").read()
    ident, numlumps, infotableofs = struct.unpack_from("<4sii", raw, 0)
    if ident not in (b"WAD2", b"WAD3"):
        raise ValueError("not a WAD2/WAD3 container: id=%r" % (ident,))
    if infotableofs + numlumps * LUMPINFO_LEN > len(raw):
        raise ValueError("infotableofs+numlumps*32 (%d) exceeds filesize (%d)"
                         % (infotableofs + numlumps * LUMPINFO_LEN, len(raw)))
    lumps = []
    for i in range(numlumps):
        base = infotableofs + i * LUMPINFO_LEN
        filepos, disksize, size, typ, comp, pad1, pad2 = struct.unpack_from(
            "<iiiBBBB", raw, base)
        name = raw[base + 16:base + 32].split(b"\x00")[0].decode("latin-1")
        lumps.append(dict(idx=i, filepos=filepos, disksize=disksize, size=size,
                          type=typ, compression=comp, name=name))
    return raw, dict(ident=ident.decode("latin-1"), numlumps=numlumps,
                     infotableofs=infotableofs), lumps


def parse_miptex(raw, lump):
    """Return dict with miptex fields + the palette; raises if a self-check fails."""
    off = lump["filepos"]
    name = raw[off:off + 16].split(b"\x00")[0].decode("latin-1")
    width, height = struct.unpack_from("<ii", raw, off + 16)
    offsets = list(struct.unpack_from("<4i", raw, off + 24))
    mip_sizes = [(width >> k) * (height >> k) for k in range(4)]
    # mip k must start exactly after mip k-1 (that is how GrabMip() writes them)
    for k in range(1, 4):
        if offsets[k] != offsets[k - 1] + mip_sizes[k - 1]:
            raise ValueError("lump %d %r: mip offset %d (=%d) not contiguous after mip %d (end %d)"
                             % (lump["idx"], name, k, offsets[k], k - 1,
                                offsets[k - 1] + mip_sizes[k - 1]))
    pal_hdr = offsets[3] + mip_sizes[3]          # end of the 1/8 mip == uint16 count
    pal_ofs = pal_hdr + 2                        # first palette byte
    if pal_ofs + PALETTE_LEN + 2 != lump["size"]:
        raise ValueError("lump %d %r: pal_ofs+768+2 (%d) != lump size (%d)"
                         % (lump["idx"], name, pal_ofs + PALETTE_LEN + 2, lump["size"]))
    count = struct.unpack_from("<H", raw, off + pal_hdr)[0]
    if count != PALETTE_ENTRIES:
        raise ValueError("lump %d %r: palette count %d != 256" % (lump["idx"], name, count))
    if raw[off + pal_ofs + PALETTE_LEN: off + pal_ofs + PALETTE_LEN + 2] != b"\x00\x00":
        raise ValueError("lump %d %r: trailing 2 bytes are not 0x0000" % (lump["idx"], name))
    palette = raw[off + pal_ofs: off + pal_ofs + PALETTE_LEN]
    return dict(name=name, width=width, height=height, offsets=offsets,
                mip_sizes=mip_sizes, pal_ofs=pal_ofs, palette=palette, base=off)


def _opacity(pal, idx):
    """Opacity of one palette index = how DARK it is (255 = white = transparent).

    The 0..254 ramp of every `decals.wad` lump measured here is strictly descending
    (palette[0] == (255,255,255), palette[254] == (1,1,1) or (0,0,0)), so the value equals the
    index; computing it from the palette keeps the rule correct if a ramp is ever flipped.
    """
    return 255 - max(pal[idx * 3], pal[idx * 3 + 1], pal[idx * 3 + 2])


def decal_base_colour(src, w, h, pal, name):
    """Self-check the decal convention and return `palette[255]` (the base colour).

    Hard identities (this is the proof the parse is right, not an unchecked constant):
      1. all four corner pixels agree (one flat background);
      2. that background index is pure white -> opacity 0 ("white == invisible");
      3. palette index 255 does not occur in the pixel data (it is the base colour).
    """
    corners = [src[0], src[w - 1], src[(h - 1) * w], src[w * h - 1]]
    if len(set(corners)) != 1:
        raise ValueError("lump %r: decal corners disagree %r" % (name, corners))
    bg = corners[0]
    if _opacity(pal, bg) != 0:
        raise ValueError("lump %r: background index %d is not pure white (opacity %d != 0): %r"
                         % (name, bg, _opacity(pal, bg), tuple(pal[bg * 3:bg * 3 + 3])))
    if 255 in src:
        raise ValueError("lump %r: palette index 255 is the base colour and must not occur "
                         "in the pixel data, but it does" % (name,))
    return (pal[255 * 3], pal[255 * 3 + 1], pal[255 * 3 + 2])


def mip0_rgba(raw, mi):
    """mip0 -> RGBA, under the GoldSrc `decals.wad` convention (see the module docstring):

      RGB   = palette[255]        (the single base colour of the whole decal)
      alpha = 255 - palette[idx]  (darkness; the pure-white background lands on 0)
    """
    w, h = mi["width"], mi["height"]
    src = raw[mi["base"] + mi["offsets"][0]: mi["base"] + mi["offsets"][0] + w * h]
    pal = mi["palette"]
    out = bytearray(w * h * 4)
    if mi["name"].startswith("{"):
        br, bg, bb = decal_base_colour(src, w, h, pal, mi["name"])
        for i, idx in enumerate(src):
            out[i * 4 + 0] = br
            out[i * 4 + 1] = bg
            out[i * 4 + 2] = bb
            out[i * 4 + 3] = _opacity(pal, idx)
        return bytes(out)
    # Not a `{` lump (decals.wad has none): fall back to plain per-index RGB, fully opaque.
    for i, idx in enumerate(src):
        out[i * 4 + 0] = pal[idx * 3 + 0]
        out[i * 4 + 1] = pal[idx * 3 + 1]
        out[i * 4 + 2] = pal[idx * 3 + 2]
        out[i * 4 + 3] = 255
    return bytes(out)


def write_png(path, width, height, rgba):
    """Minimal RGBA PNG writer (filter 0 on every scanline)."""
    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))
    raw = bytearray()
    stride = width * 4
    for y in range(height):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]
    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
           + chunk(b"IEND", b""))
    open(path, "wb").write(png)
    return len(png)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="src", required=True)
    ap.add_argument("--info", action="store_true")
    ap.add_argument("--name")
    ap.add_argument("--prefix")
    ap.add_argument("--out")
    ap.add_argument("--outdir")
    args = ap.parse_args()

    raw, header, lumps = read_wad(args.src)
    print("container : %s  id=%s  numlumps=%d  infotableofs=%d  filesize=%d"
          % (args.src, header["ident"], header["numlumps"], header["infotableofs"], len(raw)))

    # --- container self-checks -------------------------------------------------
    prev_end = WADINFO_LEN
    overlap = 0
    for lu in sorted(lumps, key=lambda x: x["filepos"]):
        if lu["filepos"] < prev_end:
            overlap += 1
        if lu["filepos"] + lu["disksize"] > len(raw):
            raise ValueError("lump %d %r runs past EOF" % (lu["idx"], lu["name"]))
        prev_end = lu["filepos"] + lu["disksize"]
    tail = len(raw) - (header["infotableofs"] + header["numlumps"] * LUMPINFO_LEN)
    print("selfcheck : overlaps=%d  tail_after_dir=%d bytes" % (overlap, tail))

    miptex = [lu for lu in lumps if lu["type"] == TYP_MIPTEX]
    print("miptex    : %d of %d lumps are type %d" % (len(miptex), len(lumps), TYP_MIPTEX))
    if not miptex:
        print("FAIL: no miptex lumps", file=sys.stderr)
        return 2

    decoded = []
    for lu in miptex:
        mi = parse_miptex(raw, lu)      # raises on any broken identity
        decoded.append(mi)
    print("palette   : all %d miptex pass  mip-contiguity + pal_ofs+2+768==size + count==256"
          % len(decoded))
    wh = sorted({(m["width"], m["height"]) for m in decoded})
    print("sizes     : %s" % (wh,))

    if args.info:
        for m in decoded:
            print("  %-14s %4dx%-4d mip0@%-6d mip3@%-6d pal@%-6d"
                  % (m["name"], m["width"], m["height"], m["offsets"][0],
                     m["offsets"][3], m["pal_ofs"]))
        return 0

    targets = []
    if args.name:
        targets = [m for m in decoded if m["name"] == args.name]
        if not targets:
            print("FAIL: no lump named %r" % args.name, file=sys.stderr)
            return 2
    elif args.prefix:
        targets = [m for m in decoded if m["name"].startswith(args.prefix)]
        if not targets:
            print("FAIL: no lump with prefix %r" % args.prefix, file=sys.stderr)
            return 2

    for m in targets:
        rgba = mip0_rgba(raw, m)
        if args.out and len(targets) == 1:
            dst = args.out
        else:
            if not args.outdir:
                print("need --out (single) or --outdir (many)", file=sys.stderr)
                return 2
            safe = m["name"].replace("{", "_LB_").replace("/", "_").replace("\\", "_")
            os.makedirs(args.outdir, exist_ok=True)
            dst = os.path.join(args.outdir, safe + ".png")
        n = write_png(dst, m["width"], m["height"], rgba)
        print("wrote %-46s %dx%d  %d bytes" % (dst, m["width"], m["height"], n))
    return 0


if __name__ == "__main__":
    sys.exit(main())
