#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
marlett-glyphs.py -- tell WHICH codepoint of the original `marlett.ttf` carries the VGUI
                     "check" glyph, by rendering every reachable codepoint and measuring
                     the shape (the numbers gate the visual read, they do not replace it).

WHY THIS EXISTS (slice cs16-AT, difference #33)
-----------------------------------------------
`client/Assets/Scripts/UI/Flow/CsUiStyle.cs` -> `CreateCheckButton` draws the checked state as
a solid square, because "the original check is a Marlett glyph (scheme:483-492) and this
project does not have that font".  The font carrier IS on disk (`原版资源/cs16src/marlett.ttf`,
27724 B) and the scheme only DECLARES the family:

  原版资源/cs16src/cstrike/cstrike__resource__ClientScheme.res:483-492
      // this is the symbol font
      "Marlett" { "1" { "name" "Marlett"  "tall" "11"  "weight" "0"  "symbol" "1" } }

The .res gives the family and the fact that it is a SYMBOL font; it never names the character a
CheckButton paints, and the scheme colours (`CheckButtonBorder1/2`, `CheckButtonCheck`,
:177-179) say nothing about the codepoint either.  So the codepoint must be recovered from the
carrier itself.  This tool does that; its output is the provenance recorded next to the
codepoint in `CsUiStyle.cs`.

THE TRAP THIS TOOL EXISTS TO AVOID (measured, not guessed)
----------------------------------------------------------
The carrier is a **Windows symbol font**: its `cmap` contains exactly two subtables --
(1,0) format 0 (Macintosh Roman) and (3,0) format 4 (Microsoft Symbol) -- and **no (3,1)
Microsoft Unicode**.  Pillow/FreeType can only select a UNICODE charmap; with none present
every character resolves to glyph 0 (`.notdef`) and *every codepoint renders the identical
shape*.  A naive "render and eyeball" loop therefore concludes "all 38 glyphs look the same"
and finds no check at all (this is what this slice hit first, measured with a sha1 of the
rendered mask: 351/351 codepoints byte-identical).

Fix: `--render-font` writes a patched COPY of the carrier in which the `cmap` table is
replaced by a single (3,1) format-4 subtable mapping every codepoint the carrier itself
declares to the glyph id the carrier itself declares.  Nothing else is touched (the new cmap
is appended at EOF and only the `cmap` directory entry's offset/length are rewritten, so no
existing table moves).  The same FreeType rasteriser then draws the real outlines.
Self-check printed as `distinct ink signatures` vs `distinct mapped glyph ids`: if the patch
had not worked these two would be 1 vs 38.

WHAT IT PRINTS / WRITES
-----------------------
1. `cmap` inventory: every subtable, every mapped codepoint -> glyph id, plus the glyph's
   `post` name (that is where `uniF081`-style names come from).
2. Per codepoint, measured on the rendered mask (256-level):
       ink    -- pixels with coverage >= threshold
       bbox   -- tight ink box, w, h, w/h ratio
       cover  -- ink / (bbox_w * bbox_h)      (filled box -> ~1.0, a stroke -> small)
       comps  -- 8-connected ink components  (a check mark is 1)
       thin   -- ink / (bbox_w + bbox_h)     (rough stroke width in px)
3. An ASCII-range and a PUA-range CONTACT SHEET (glyph + cell number + codepoint printed on
   every cell) -> `.ai-tmp/test/marlett-glyphs-{ascii,pua}.png`.
4. `# CHECK-LIKE SHORTLIST`: comps==1 AND cover<=0.40 AND 0.6<=w/h<=1.8 AND ink>20 -- one
   connected thin stroke in a roughly square box.  This is a FILTER; the answer is read off
   the contact sheet and that read is recorded in the slice report.

RESULT (slice cs16-AT, measured 2026-09-22 with this tool)
---------------------------------------------------------
The carrier `原版资源/cs16src/marlett.ttf` (27724 B) is a byte-identical copy of this
machine's `C:\Windows\Fonts\marlett.ttf` (documented in `原版资源/清单.md:83`).  It holds
41 glyphs, 38 of them reachable: raw bytes U+0020..U+0045 (Macintosh-Roman subtable) and,
through the Microsoft-Symbol subtable, U+F030..U+F039 / U+F057 / U+F061..U+F079 /
U+F0A1..U+F0A3 -- i.e. the Windows symbol codes 0x30-0x39, 0x57, 0x61-0x79, 0xA1-0xA3.

THE CHECK IS gid 12 ("a"), reached as U+F061 (symbol code 0x61 = 'a') and as U+0029
(raw byte 0x29).  It is the only asymmetric tick in the font; gid 13 ("b", U+F062 / U+002A)
is a near-twin (same tick geometry, different raster).  Measured at --size 300:
    gid 12  ink=7523  bbox=132x140  ratio=0.943  cover=0.407  comps=1
            vertex at 0.352 of the bbox width  /  right arm 0.264 of bbox height higher
    gid 13  ink=7523  bbox=132x140  ratio=0.943  cover=0.407  comps=1  (same vx/arm)
Every other glyph fails the tick test (`!--tick` column): frames, corner braces, solid
triangles/arrows, symmetric down-chevrons (vx~0.50, arm~0.00), circles with a play triangle,
an X, a "?", diagonal stripes, narrow bars, and gid 40 = the Windows flag (symbol 'W').
The glyph ids that the previous slice flagged by NAME (`uniF081`/`uniF082`/`uniF083` = gid
37/38/39, `uniF057` = gid 40) are narrow bars and the Windows flag -- they are NOT the check,
which is why a name-driven search dead-ends.

Usage
-----
    python tools/probes/marlett-glyphs.py \
        --ttf    "原版资源/cs16src/marlett.ttf" \
        --out    "<项目根>/.ai-tmp/test/marlett-glyphs.png" \
        --tsv    "<项目根>/.ai-tmp/test/marlett-glyphs.tsv"

`--out` must live under `<项目根>/.ai-tmp/test/`; the patched render font is written next to
it as `<out stem>-render.ttf`.  Exit 0 = ok, 2 = carrier unreadable / nothing mapped.
"""

import argparse
import hashlib
import math
import os
import struct
import sys

# ---------------------------------------------------------------------------
#  TrueType parsing (only what this tool needs: cmap / maxp / post / head)
# ---------------------------------------------------------------------------

# The 258 standard Macintosh glyph names that `post` format 2.0 indexes into.
MAC_GLYPH_NAMES = (
    ".notdef .null nonmarkingreturn space exclam quotedbl numbersign dollar percent "
    "ampersand quotesingle parenleft parenright asterisk plus comma hyphen period slash "
    "zero one two three four five six seven eight nine colon semicolon less equal greater "
    "question at A B C D E F G H I J K L M N O P Q R S T U V W X Y Z bracketleft "
    "backslash bracketright asciicircum underscore grave a b c d e f g h i j k l m n o p "
    "q r s t u v w x y z braceleft bar braceright asciitilde Adieresis Aring Ccedilla "
    "Eacute Ntilde Odieresis Udieresis aacute agrave acircumflex adieresis atilde aring "
    "ccedilla eacute egrave ecircumflex edieresis iacute igrave icircumflex idieresis "
    "ntilde oacute ograve ocircumflex odieresis otilde uacute ugrave ucircumflex udieresis "
    "dagger degree cent sterling section bullet paragraph germandbls registered copyright "
    "trademark acute dieresis notequal AE Oslash infinity plusminus lessequal greaterequal "
    "yen mu partialdiff summation product pi integral ordfeminine ordmasculine Omega ae "
    "oslash questiondown exclamdown logicalnot radical florin approxequal Delta guillemotleft "
    "guillemotright ellipsis nonbreakingspace Agrave Atilde Otilde OE oe endash emdash "
    "quotedblleft quotedblright quoteleft quoteright divide lozenge ydieresis Ydieresis "
    "fraction currency guilsinglleft guilsinglright fi fl daggerdbl periodcentered "
    "quotesinglbase quotedblbase perthousand Acircumflex Ecircumflex Aacute Edieresis Egrave "
    "Iacute Icircumflex Idieresis Igrave Oacute Ocircumflex apple Ograve Uacute "
    "Ucircumflex Ugrave dotlessi circumflex tilde macron breve dotaccent ring cedilla "
    "hungarumlaut ogonek caron Lslash lslash Scaron scaron Zcaron zcaron brokenbar Eth eth "
    "Yacute yacute Thorn thorn minus multiply onesuperior twosuperior threesuperior onehalf "
    "onequarter threequarters franc Gbreve gbreve Idotaccent Scedilla scedilla Cacute cacute "
    "Ccaron ccaron dcroat"
).split()


class TtfError(Exception):
    pass


def utf16be_name(raw):
    """Decode a big-endian UTF-16 string, tolerating the odd lone surrogate."""
    try:
        return raw.decode("utf-16-be", "replace").replace("\x00", "")
    except Exception:                                            # noqa: BLE001
        return ""


class Ttf(object):
    def __init__(self, path):
        with open(path, "rb") as fh:
            self.data = fh.read()
        self.path = path
        self.size = len(self.data)
        d = self.data
        if len(d) < 12:
            raise TtfError("%s: file too short (%d B)" % (path, len(d)))
        self.sfnt_version = d[:4]
        (num_tables,) = struct.unpack_from(">H", d, 4)
        self.tables = {}
        self.dir_index = {}
        for i in range(num_tables):
            off = 12 + 16 * i
            tag, _checksum, toff, tlen = struct.unpack_from(">4sIII", d, off)
            tag = tag.decode("latin-1")
            self.tables[tag] = (toff, tlen)
            self.dir_index[tag] = off
        self.num_glyphs = None
        if "maxp" in self.tables:
            (self.num_glyphs,) = struct.unpack_from(">H", d, self.tables["maxp"][0] + 4)
        self.units_per_em = 0
        if "head" in self.tables:
            (self.units_per_em,) = struct.unpack_from(">H", d, self.tables["head"][0] + 18)
        self._cmap = None
        self._post_names = None
        self.cmap_subtables = []

    # -- cmap ---------------------------------------------------------------
    def cmap(self):
        """{codepoint: glyph_id}, union over every cmap subtable."""
        if self._cmap is not None:
            return self._cmap
        out = {}
        if "cmap" not in self.tables:
            self._cmap = out
            return out
        off, _len = self.tables["cmap"]
        d = self.data
        (n,) = struct.unpack_from(">H", d, off + 2)
        for i in range(n):
            plat, enc, sub_off = struct.unpack_from(">HHI", d, off + 4 + i * 8)
            base = off + sub_off
            (fmt,) = struct.unpack_from(">H", d, base)
            self.cmap_subtables.append((plat, enc, fmt, sub_off, base))
            try:
                out.update(self._cmap_sub(base, fmt))
            except (struct.error, TtfError):
                pass
        self._cmap = out
        return out

    def _cmap_sub(self, base, fmt):
        d = self.data
        m = {}
        if fmt == 0:
            for cp in range(256):
                gid = d[base + 6 + cp]
                if gid:
                    m[cp] = gid
        elif fmt == 4:
            (segx2,) = struct.unpack_from(">H", d, base + 6)
            seg = segx2 // 2
            ends = struct.unpack_from(">%dH" % seg, d, base + 14)
            starts = struct.unpack_from(">%dH" % seg, d, base + 16 + segx2)
            deltas = struct.unpack_from(">%dh" % seg, d, base + 16 + 2 * segx2)
            ro_base = base + 16 + 3 * segx2
            ros = struct.unpack_from(">%dH" % seg, d, ro_base)
            for i in range(seg):
                if starts[i] == 0xFFFF:
                    continue
                for cp in range(starts[i], ends[i] + 1):
                    if ros[i] == 0:
                        gid = (cp + deltas[i]) & 0xFFFF
                    else:
                        nxt = ro_base + 2 * i + ros[i] + 2 * (cp - starts[i])
                        if nxt + 2 > len(d):
                            continue
                        (gid,) = struct.unpack_from(">H", d, nxt)
                        if gid:
                            gid = (gid + deltas[i]) & 0xFFFF
                    if gid:
                        m[cp] = gid
        elif fmt == 6:
            first, count = struct.unpack_from(">HH", d, base + 6)
            for k in range(count):
                gid = d[base + 10 + k]
                if gid:
                    m[first + k] = gid
        elif fmt == 12:
            (ngroups,) = struct.unpack_from(">I", d, base + 12)
            for gi in range(ngroups):
                s, e, gid0 = struct.unpack_from(">III", d, base + 16 + gi * 12)
                for cp in range(s, e + 1):
                    m[cp] = gid0 + (cp - s)
        return m

    # -- post ---------------------------------------------------------------
    def post_names(self):
        """{glyph_id: name} for `post` format 2.0 (empty for other formats)."""
        if self._post_names is not None:
            return self._post_names
        out = {}
        self.post_format = None
        if "post" not in self.tables:
            self._post_names = out
            return out
        off, tlen = self.tables["post"]
        d = self.data
        (ver,) = struct.unpack_from(">I", d, off)
        self.post_format = ver
        if ver != 0x00020000:
            self._post_names = out
            return out
        (ng,) = struct.unpack_from(">H", d, off + 32)
        idx = struct.unpack_from(">%dH" % ng, d, off + 34)
        p = off + 34 + ng * 2
        extra = []
        while p < off + tlen:
            ln = d[p]
            extra.append(d[p + 1:p + 1 + ln].decode("latin-1"))
            p += 1 + ln
        for gid in range(ng):
            i = idx[gid]
            if i < 258:
                out[gid] = MAC_GLYPH_NAMES[i] if i < len(MAC_GLYPH_NAMES) else "std%d" % i
            elif i - 258 < len(extra):
                out[gid] = extra[i - 258]
            else:
                out[gid] = "idx%d" % i
        self._post_names = out
        return out

    # -- name (for the readable family / subfamily, and any glyph-name records) ----
    def name_strings(self):
        res = []
        if "name" not in self.tables:
            return res
        off, tlen = self.tables["name"]
        d = self.data
        try:
            (fmt,) = struct.unpack_from(">H", d, off)
            (cnt, str_off) = struct.unpack_from(">HH", d, off + 2)
            for i in range(cnt):
                rec = off + 6 + i * 12
                pid, eid, lid, nid, ln, soff = struct.unpack_from(">HHHHHH", d, rec)
                raw = d[off + str_off + soff:off + str_off + soff + ln]
                if pid == 3 or (pid == 0):
                    s = utf16be_name(raw)
                else:
                    s = raw.decode("latin-1", "replace")
                res.append((pid, eid, lid, nid, s))
        except struct.error:
            pass
        return res


# ---------------------------------------------------------------------------
#  Symbol-font workaround: rebuild a (3,1) unicode cmap and point the directory at it
# ---------------------------------------------------------------------------

def build_cmap_format4(mapping):
    """A (3,1) `cmap` table whose format-4 subtable maps {cp: gid}.

    Runs of consecutive codepoints that map to consecutive glyph ids are merged into one
    segment with a constant `idDelta` (the standard construction; the carrier itself uses
    it too).  Layout is the one the spec gives for format 4:

        +0  uint16 format(4)  +2 length  +4 language  +6 segCountX2  +8 searchRange
        +10 entrySelector     +12 rangeShift
        +14 endCode[segCount] +16+2*segCount reservedPad
            startCode[segCount] idDelta[segCount] idRangeOffset[segCount] glyphIdArray[]

    (An earlier revision of this function emitted the four `segCountX2/searchRange/
    entrySelector/rangeShift` words TWICE -- once in the 14-byte header and once again in
    front of `endCode` -- which shifted every array by 8 bytes.  The table still had a
    self-consistent `length`, so it parsed as "valid" for the first few segments and every
    other codepoint silently fell back to `.notdef`; the round-trip check below is what
    catches that class of bug.)
    """
    cps = sorted(c for c in mapping if c != 0xFFFF)
    runs = []                                              # [start, end, delta]
    for cp in cps:
        gid = mapping[cp]
        if runs and cp == runs[-1][1] + 1 and gid == (runs[-1][1] + runs[-1][2]) + 1:
            runs[-1][1] = cp
        else:
            runs.append([cp, cp, gid - cp])
    seg = len(runs) + 1
    ends = [r[1] for r in runs] + [0xFFFF]
    starts = [r[0] for r in runs] + [0xFFFF]
    deltas = [(r[2] & 0xFFFF) for r in runs] + [1]
    ros = [0] * seg
    segx2 = seg * 2
    sr = 2 * (2 ** int(math.log(seg, 2)))
    es = int(math.log(sr // 2, 2))
    rs = segx2 - sr
    body = b"".join(struct.pack(">H", v) for v in ends)
    body += struct.pack(">H", 0)                       # reservedPad
    body += b"".join(struct.pack(">H", v) for v in starts)
    body += b"".join(struct.pack(">H", v) for v in deltas)
    body += b"".join(struct.pack(">H", v) for v in ros)
    sub = struct.pack(">HHHHHHH", 4, 14 + len(body), 0, segx2, sr, es, rs) + body
    return struct.pack(">HHHHI", 0, 1, 3, 1, 12) + sub, len(runs)


def write_render_font(ttf, mapping, dst_path):
    """Copy the carrier with `cmap` replaced by a (3,1) unicode table for `mapping`.

    Appends the new table at EOF and rewrites only the `cmap` directory entry's
    offset/length (+ its checksum), so no existing table moves.  Returns
    (dst_path, n_runs, roundtrip_ok) -- the round trip is re-parsed from disk and compared
    against `mapping`, because a malformed format-4 array silently loses codepoints.
    """
    d = bytearray(ttf.data)
    while len(d) % 4:
        d += b"\x00"
    new_off = len(d)
    new_cmap, n_runs = build_cmap_format4(mapping)
    d += new_cmap
    ent = ttf.dir_index["cmap"]
    # table checksum = sum of big-endian uint32 over the zero-padded table data
    pad = new_cmap + b"\x00" * ((4 - len(new_cmap) % 4) % 4)
    csum = 0
    for i in range(0, len(pad), 4):
        csum = (csum + struct.unpack_from(">I", pad, i)[0]) & 0xFFFFFFFF
    struct.pack_into(">II", d, ent + 4, csum, new_off)
    struct.pack_into(">I", d, ent + 12, len(new_cmap))
    with open(dst_path, "wb") as f:
        f.write(bytes(d))
    # A cmap entry that resolves to glyph 0 means "no glyph" in TrueType, so a reader
    # legitimately drops it (this tool's own parser does).  Compare only the entries that
    # name a real glyph, and require that nothing extra appeared.
    want = dict((c, g) for c, g in mapping.items() if g)
    back = Ttf(dst_path).cmap()
    ok = len(back) == len(want) and all(back.get(c) == g for c, g in want.items())
    return dst_path, n_runs, ok


# ---------------------------------------------------------------------------
#  Rendering + shape measures
# ---------------------------------------------------------------------------

def render_mask(font, char, size):
    """Draw one glyph, big enough that nothing is clipped.

    ⚠️ The canvas MUST scale with `size`.  A fixed canvas silently CROPS the glyph once
    `size` exceeds it (measured: `--size 420` on a 256 px canvas produced bboxes pinned to
    the canvas edge, `cover` up to 1.0, and shapes sliced in half on the contact sheet) --
    i.e. exactly the artefact that would make a reader "not see" a glyph that is there.
    Em box = `size`, so 2x plus margins always contains ascenders/descenders/overhangs.
    """
    from PIL import Image, ImageDraw
    canvas = int(size * 2)
    pad = int(size * 0.45)
    img = Image.new("L", (canvas, canvas), 0)
    ImageDraw.Draw(img).text((pad, pad), char, font=font, fill=255)
    return img


def ink_stats(img, thresh=32):
    """(ink, bbox, w, h, ratio, cover, comps, thin) for one rendered glyph."""
    px = img.load()
    w_img, h_img = img.size
    ink = 0
    x0, y0, x1, y1 = w_img, h_img, -1, -1
    for y in range(h_img):
        for x in range(w_img):
            if px[x, y] >= thresh:
                ink += 1
                if x < x0:
                    x0 = x
                if x > x1:
                    x1 = x
                if y < y0:
                    y0 = y
                if y > y1:
                    y1 = y
    if ink == 0:
        return dict(ink=0, bbox=None, w=0, h=0, ratio=0.0, cover=0.0, comps=0, thin=0.0)
    bw = x1 - x0 + 1
    bh = y1 - y0 + 1
    comps = 0
    seen = bytearray(w_img * h_img)
    for sy in range(y0, y1 + 1):
        for sx in range(x0, x1 + 1):
            if px[sx, sy] < thresh or seen[sy * w_img + sx]:
                continue
            comps += 1
            stack = [(sx, sy)]
            seen[sy * w_img + sx] = 1
            while stack:
                cx, cy = stack.pop()
                for dy in (-1, 0, 1):
                    for dx in (-1, 0, 1):
                        nx, ny = cx + dx, cy + dy
                        if nx < 0 or ny < 0 or nx >= w_img or ny >= h_img:
                            continue
                        k = ny * w_img + nx
                        if seen[k] or px[nx, ny] < thresh:
                            continue
                        seen[k] = 1
                        stack.append((nx, ny))
    # --- features that separate a CHECK from other one-component strokes ------------
    # A check "v/tick" is an asymmetric V: the vertex sits LEFT of the box centre and the
    # RIGHT arm reaches higher than the left one.  A symmetric chevron (Marlett's down-arrow)
    # has vertex_x ~ centre and both arms at (nearly) the same height.
    bot = None
    for y in range(y1, y0 - 1, -1):
        xs = [x for x in range(x0, x1 + 1) if px[x, y] >= thresh]
        if xs:
            bot = (y, sum(xs) / float(len(xs)))
            break
    band = max(1, int(round(bw * 0.15)))
    topl = None
    for y in range(y0, y1 + 1):
        if any(px[x, y] >= thresh for x in range(x0, x0 + band)):
            topl = y
            break
    topr = None
    for y in range(y0, y1 + 1):
        if any(px[x, y] >= thresh for x in range(x1 - band + 1, x1 + 1)):
            topr = y
            break
    vx_frac = round((bot[1] - x0) / float(bw), 3) if bot else -1.0
    arm = round(((topl - topr) / float(bh)), 3) if (topl is not None and topr is not None) \
        else 0.0                     # > 0 == right arm reaches higher than the left arm
    is_tick = bool(comps == 1 and ink > 20 and bot is not None
                   and vx_frac <= 0.48 and arm >= 0.10)
    return dict(ink=ink, bbox=(x0, y0, x1, y1), w=bw, h=bh,
                ratio=round(float(bw) / float(bh), 3),
                cover=round(float(ink) / float(bw * bh), 4),
                comps=comps, thin=round(float(ink) / float(bw + bh), 2),
                vx=vx_frac, arm=arm, tick=is_tick)


# ---------------------------------------------------------------------------
#  Contact sheet
# ---------------------------------------------------------------------------

CELL_W, CELL_H = 80, 100
COLS = 16
LABEL_H = 22


def write_sheet(entries, out_path, title, cell_w=CELL_W, cell_h=CELL_H, cols=COLS):
    """entries = [(cell_no, cp, mask), ...] sorted by cell_no; grid with labels."""
    from PIL import Image, ImageDraw, ImageFont
    CELL_W, CELL_H, COLS = cell_w, cell_h, cols
    try:
        lab = ImageFont.truetype("consola.ttf", 13)
    except Exception:                                            # noqa: BLE001
        try:
            lab = ImageFont.truetype("arial.ttf", 13)
        except Exception:                                        # noqa: BLE001
            lab = ImageFont.load_default()
    rows = (len(entries) + COLS - 1) // COLS
    img = Image.new("RGB", (COLS * CELL_W, rows * CELL_H + LABEL_H), (255, 255, 255))
    dr = ImageDraw.Draw(img)
    dr.text((4, 4), title, fill=(0, 0, 0), font=lab)
    for cell_no, cp, mask in entries:
        r, c = divmod(cell_no, COLS)
        ox, oy = c * CELL_W, LABEL_H + r * CELL_H
        dr.rectangle([ox, oy, ox + CELL_W - 1, oy + CELL_H - 1], outline=(190, 190, 190))
        bbox = mask.getbbox()
        if bbox:
            g = mask.crop(bbox)
            gw, gh = g.size
            # fit inside BOTH dimensions: scaling by height alone overflows a wide glyph
            # into its neighbours, which obscures exactly the shape being judged
            s = min((CELL_W - 10.0) / gw, (CELL_H - 26.0) / gh)
            if abs(s - 1.0) > 1e-6:
                g = g.resize((max(1, int(round(gw * s))), max(1, int(round(gh * s)))))
            gw, gh = g.size
            img.paste((0, 0, 0), (ox + (CELL_W - gw) // 2, oy + (CELL_H - 22 - gh) // 2), g)
        dr.text((ox + 3, oy + CELL_H - 20), "#%d" % cell_no, fill=(180, 0, 0), font=lab)
        dr.text((ox + 3, oy + CELL_H - 9), "U+%04X" % cp, fill=(0, 0, 170), font=lab)
    img.save(out_path)
    return out_path, img.size


# ---------------------------------------------------------------------------

def parse_ranges(spec):
    out = []
    for part in spec.split(","):
        part = part.strip()
        if not part:
            continue
        if "-" in part:
            a, b = part.split("-", 1)
            out.append((int(a, 16), int(b, 16)))
        else:
            v = int(part, 16)
            out.append((v, v))
    return out


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--ttf", required=True, help="marlett.ttf carrier")
    ap.add_argument("--out", required=True, help="contact sheet PNG stem")
    ap.add_argument("--tsv", help="also write the per-codepoint report as TSV")
    ap.add_argument("--render-font", help="patched render-font path "
                                         "(default: <out stem>-render.ttf)")
    ap.add_argument("--ranges", default="20-7E,F000-F0FF",
                    help="hex ranges to scan (default: ASCII printable + PUA F000-F0FF)")
    ap.add_argument("--size", type=int, default=150, help="render pixel size")
    ap.add_argument("--thresh", type=int, default=32, help="ink threshold 0..255")
    ap.add_argument("--probe-gids",
                    help="comma-separated glyph ids the carrier's cmap does NOT reach; "
                         "each is aliased to U+E000+i so it can be rendered and measured "
                         "(the report shows the real glyph id in its `gid` column)")
    ap.add_argument("--cell", type=int, default=80, help="contact-sheet cell width (default 80)")
    ap.add_argument("--cols", type=int, default=16, help="contact-sheet columns (default 16)")
    args = ap.parse_args(argv)

    try:
        ttf = Ttf(args.ttf)
    except (OSError, TtfError) as e:
        sys.stderr.write("cannot read ttf: %s\n" % e)
        return 2

    cm = ttf.cmap()
    names = ttf.post_names()
    print("carrier  : %s (%d B)" % (os.path.basename(args.ttf), ttf.size))
    print("sfnt     : %r tables=%d numGlyphs=%s unitsPerEm=%d"
          % (ttf.sfnt_version, len(ttf.tables), ttf.num_glyphs, ttf.units_per_em))
    print("tables   : %s" % " ".join("%s@%d+%d" % (t, ttf.tables[t][0], ttf.tables[t][1])
                                     for t in sorted(ttf.tables)))
    print("cmap     : %d subtable(s)" % len(ttf.cmap_subtables))
    for (plat, enc, fmt, sub_off, _base) in ttf.cmap_subtables:
        kind = {(1, 0): "Macintosh Roman", (3, 0): "Microsoft SYMBOL",
                (3, 1): "Microsoft Unicode", (0, 0): "Unicode"}.get((plat, enc), "?")
        print("           platform=%d encoding=%d %-18s format=%d off=%d"
              % (plat, enc, kind, fmt, sub_off))
    print("           -> %d distinct mapped codepoints" % len(cm))
    print("unicode  : %s"
          % ("PRESENT" if any(p == 3 and e == 1 for p, e, _f, _o, _b in ttf.cmap_subtables)
             else "ABSENT  <<< Pillow/FreeType cannot select a charmap on this carrier"))
    print("post     : format=%s" % (("0x%08X" % ttf.post_format) if ttf.post_format else "-"))
    nm = [s for (_p, _e, _l, nid, s) in ttf.name_strings() if nid == 1][:1]
    if nm:
        print("name(1)  : %s" % nm[0])

    print("")
    print("# glyph inventory (codepoint -> glyph id -> post name), sorted by glyph id")
    print("#   %-6s %-6s %-8s %s" % ("gid", "cp", "code", "postName"))
    by_gid = {}
    for cp in sorted(cm):
        by_gid.setdefault(cm[cp], []).append(cp)
    for gid in sorted(by_gid):
        cps = by_gid[gid]
        shown = ", ".join("U+%04X" % c for c in cps)
        print("#   %-6d %-6s %-8s %s"
              % (gid, "U+%04X" % cps[0] if len(cps) == 1 else "x%d" % len(cps),
                 "sym" if all(c >= 0xF000 for c in cps) else "raw", names.get(gid, "-")))
        if len(cps) > 1:
            print("#          also reachable as: %s" % shown)

    if not cm:
        sys.stderr.write("no codepoint is mapped by this carrier\n")
        return 2

    # glyph ids that no cmap reaches are still renderable; alias them onto spare PUA
    # codepoints so the same rasteriser can draw them (the aliases are part of the patched
    # render font, never of the carrier).
    alias = {}
    if args.probe_gids:
        for i, g in enumerate(int(x) for x in args.probe_gids.split(",")):
            alias[0xE000 + i] = g
            cm[0xE000 + i] = g
        print("")
        print("# GLYPH-ID PROBE (not reachable through the carrier's own cmap):")
        for cp in sorted(alias):
            print("#   U+%04X is an ALIAS for gid %d (postName=%s)"
                  % (cp, alias[cp], names.get(alias[cp], "-")))
        print("#   >>> their rows below have `mapped=Y` and show the REAL gid in the gid column")

    # ---- patched render font ---------------------------------------------
    from PIL import ImageFont
    stem, ext = os.path.splitext(args.out)
    rf_path = args.render_font or (stem + "-render.ttf")
    try:
        _p, n_runs, trip_ok = write_render_font(ttf, cm, rf_path)
    except OSError as e:
        sys.stderr.write("cannot write render font: %s\n" % e)
        return 2
    print("")
    print("renderFnt: %s" % rf_path)
    print("           cmap -> one (3,1) format-4 table: %d codepoints in %d segment(s)"
          % (len(cm), n_runs))
    print("           round trip: re-parsed from disk gives %s mapping (%s)"
          % ("the SAME" if trip_ok else "a DIFFERENT", "OK" if trip_ok else "MISMATCH"))
    if not trip_ok:
        sys.stderr.write("render font cmap does not round trip -- refusing to measure\n")
        return 2
    font = ImageFont.truetype(rf_path, args.size)

    want = set()
    for a, b in parse_ranges(args.ranges):
        want.update(range(a, b + 1))
    want.update(alias)
    cps = sorted(want)

    def sheet_group(cp):
        return 1 if cp >= 0x2000 else 0

    rows = []
    entries = []
    next_cell = {0: 0, 1: 0}
    sigs = {}
    for cp in cps:
        gi = sheet_group(cp)
        cell = next_cell[gi]
        next_cell[gi] += 1
        mapped = cp in cm
        try:
            mask = render_mask(font, chr(cp), args.size)
            st = ink_stats(mask, args.thresh)
        except Exception as e:                                   # noqa: BLE001
            mask = None
            st = dict(ink=0, bbox=None, w=0, h=0, ratio=0.0, cover=0.0, comps=0, thin=0.0)
            sys.stderr.write("render failed for U+%04X: %s\n" % (cp, e))
        st["sig"] = hashlib.sha1(mask.tobytes()).hexdigest()[:10] if \
            (mask is not None and st["ink"] > 0) else "-"
        if st["sig"] != "-":
            sigs.setdefault(st["sig"], []).append(cp)
        rows.append((cell, cp, mapped, cm.get(cp, -1), names.get(cm.get(cp, -1), "-"), st))
        if mask is not None and st["ink"] > 0:
            entries.append((cell, cp, mask))

    # self-check: the whole point of the render font is that glyphs differ
    gids_present = set(cm.get(cp, -1) for (_c, cp, m, _g, _n, s) in rows
                       if m and s["ink"] > 0)
    # NOTE: sigs < gids is NOT necessarily a bug -- this carrier has glyph pairs that are
    # drawn identically (measured: gid7==gid31 and gid8==gid32).  What would indicate the
    # symbol-font trap is sigs == 1 (everything is .notdef).  So list the sharing instead of
    # crying wolf, and hard-fail only on the "everything identical" case.
    shared = 0
    for (cell_no, cp, m, gid, _n, st) in rows:
        if m and st["ink"] > 0 and len(sigs.get(st["sig"], [])) > 1:
            shared += 1
    print("selfcheck: distinct ink signatures=%d  distinct mapped gids rendered=%d  "
          "cells sharing a signature=%d"
          % (len(sigs), len(gids_present), shared))
    if len(sigs) <= 1:
        sys.stderr.write("every codepoint rasterises identically -- the render font is not "
                         "being used; refusing to report shapes\n")
        return 2

    # ---- per-codepoint report -------------------------------------------
    hdr = ["cell", "cp", "mapped", "gid", "postName", "ink", "bbox",
           "w", "h", "ratio", "cover", "comps", "thin", "vx", "arm", "tick", "sig"]
    lines = ["\t".join(hdr)]
    for (cell_no, cp, mapped, gid, name, st) in rows:
        bb = "-" if st["bbox"] is None else "%d,%d,%d,%d" % st["bbox"]
        lines.append("\t".join([str(cell_no), "U+%04X" % cp, "Y" if mapped else "n",
                                str(gid), name, str(st["ink"]), bb, str(st["w"]),
                                str(st["h"]), str(st["ratio"]), str(st["cover"]),
                                str(st["comps"]), str(st["thin"]), str(st.get("vx", -1)),
                                str(st.get("arm", 0)), "Y" if st.get("tick") else "n",
                                st["sig"]]))
    report = "\n".join(lines)
    print("")
    print(report)
    if args.tsv:
        with open(args.tsv, "w", encoding="ascii") as f:
            f.write(report + "\n")
        print("")
        print("tsv      : %s" % args.tsv)

    # ---- distinct shapes (numeric proof of which cells are identical) -----
    mapped_rows = [r for r in rows if r[2]]
    groups = {}
    for (cell_no, cp, _m, gid, _n, st) in mapped_rows:
        groups.setdefault(st["sig"], []).append((cell_no, cp, gid))
    print("")
    print("# DISTINCT SHAPES over the %d mapped cells: %d unique ink signature(s)"
          % (len(mapped_rows), len(groups)))
    print("#   cells listed together are BYTE-IDENTICAL renders (same sha1 of the mask)")
    for sig in sorted(groups, key=lambda s: min(g[0] for g in groups[s])):
        gs = sorted(groups[sig])
        print("  %s  x%-3d %s" % (sig, len(gs),
                                  " ".join("#%d/U+%04X/g%d" % g for g in gs)))

    # ---- tick test (numbers, not eyeballing) -----------------------------
    ticks = [r for r in rows if r[2] and r[5].get("tick")]
    print("")
    print("# TICK TEST (one connected stroke, vertex left of centre [vx<=0.48], right arm")
    print("#   higher than the left [arm>=0.10 of bbox height]) -> %d hit(s)" % len(ticks))
    if not ticks:
        print("#   NONE.  Every mapped glyph fails at least one of those three conditions,")
        print("#   i.e. this carrier contains no asymmetric tick.  Compare the near misses")
        print("#   below: a symmetric down-chevron has vx~0.5 and arm~0.0.")
    for (cell_no, cp, _m, gid, name, st) in ticks:
        print("  cell %-4d U+%04X gid=%-4d %-12s ink=%-6d bbox=%dx%d vx=%-6s arm=%s"
              % (cell_no, cp, gid, name, st["ink"], st["w"], st["h"], st["vx"], st["arm"]))

    one_comp = sorted([r for r in rows if r[2] and r[5]["comps"] == 1 and r[5]["ink"] > 20
                       and 0.6 <= r[5]["ratio"] <= 1.8],
                      key=lambda r: abs(r[5]["vx"] - 0.5))
    print("#   nearest 'V-shaped' one-component glyphs (sorted by |vx-0.5|):")
    for (cell_no, cp, _m, gid, name, st) in one_comp[:8]:
        print("  cell %-4d U+%04X gid=%-4d %-12s vx=%-6s arm=%-7s ratio=%-6s cover=%-6s comps=%d"
              % (cell_no, cp, gid, name, st["vx"], st["arm"], st["ratio"], st["cover"],
                 st["comps"]))

    # ---- check-like shortlist -------------------------------------------
    cand = [r for r in rows
            if r[2] and r[5]["comps"] == 1 and r[5]["cover"] <= 0.40
            and 0.6 <= r[5]["ratio"] <= 1.8 and r[5]["ink"] > 20]
    cand.sort(key=lambda r: (r[5]["cover"], r[5]["thin"]))
    print("")
    print("# CHECK-LIKE SHORTLIST (mapped, comps==1, cover<=0.40, 0.6<=w/h<=1.8, ink>20)"
          " -> %d hit(s)" % len(cand))
    print("#   a CHECK is ONE thin stroke: expect comps=1, low cover, low 'thin'")
    for (cell_no, cp, _m, gid, name, st) in cand[:40]:
        print("  cell %-4d U+%04X gid=%-4d %-12s ink=%-6d bbox=%dx%d ratio=%-6s"
              " cover=%-6s comps=%d thin=%s"
              % (cell_no, cp, gid, name, st["ink"], st["w"], st["h"], st["ratio"],
                 st["cover"], st["comps"], st["thin"]))

    # ---- contact sheets --------------------------------------------------
    written = []
    for gname, gi in (("ascii", 0), ("pua", 1)):
        sel = sorted([e for e in entries if sheet_group(e[1]) == gi], key=lambda e: e[0])
        if not sel:
            continue
        path = "%s-%s%s" % (stem, gname, ext)
        p, size = write_sheet(sel, path, "%s  (%d cells)" % (gname, len(sel)),
                              cell_w=args.cell, cell_h=int(args.cell * 1.25),
                              cols=args.cols)
        written.append((p, size, len(sel), sel[0][1], sel[-1][1]))
    print("")
    print("# CONTACT SHEETS (cell number and codepoint are printed ON each cell)")
    for (p, size, n, lo, hi) in written:
        print("  %s  %dx%d  %d cells  U+%04X..U+%04X" % (p, size[0], size[1], n, lo, hi))
    print("# read the sheet: the VGUI check glyph is the cell drawn as a check mark")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
