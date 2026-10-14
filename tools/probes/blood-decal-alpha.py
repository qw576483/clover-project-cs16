#!/usr/bin/env python3
"""#158 offline evidence: what does `clip(c.a - 0.25)` actually remove from each decal family?

Target  = `{blood1..6` (fx_blood1..6)   -- the family whose material assignment #158 is about.
Control = `{shot1..5`  (fx_shot1..5)    -- already clipped since the shot fix; must NOT change.

Reads the same PNGs that ship in the build (`Resources/UI/Art/*.png`), so the numbers are the
runtime alpha values (sprite tint is white -> IN.color.a == 1 -> clip sees the texel alpha).

PNG decoder copied verbatim from the verified `.ai-tmp/test/roundfx-decal-measure.py`
(kept standalone so that artifact stays untouched).
"""
import os
import struct
import sys
import zlib

# PROVENANCE (2026-10-14, team ruling "promote the OFFLINE criterion, keep the Play executor in
# .ai-tmp"): promoted from `.ai-tmp/test/animfix158-alpha.py`
#   sha256 C095C1F39BCAB160216F70ED611089BF46C6C98CD9949EC2020899FE1BA0D247 (8460 B)
# The body below is that file byte-for-byte EXCEPT (a) this header/resolver block and (b) the
# missing-texture guard after TARGET/CONTROL.  It reads ONLY repo content
# (client/Assets/Resources/UI/Art/fx_blood*.png + fx_shot*.png), i.e. layer-1 "re-derivable from
# files in the repository" -- no .ai-tmp, no Play, no editor.


def _resolve():
    """project root -> texture dir.

    Order: $CLOVER_PROJECT_ROOT (explicit override; the negative control uses it) -> the absolute
    path this slice ran with -> walk up from this file.  If none of them holds
    client/Assets/Resources/UI/Art we FAIL LOUDLY: silently skipping would read as
    "checked, looks fine", which is exactly the failure mode this rule exists to stop.
    """
    cands = []
    env = os.environ.get("CLOVER_PROJECT_ROOT")
    if env:
        cands.append(env)
    else:
        cands.append(r"C:\Work\Server\f-v2\clover-project-cs16")
        d = os.path.dirname(os.path.abspath(__file__))
        while True:
            cands.append(d)
            nd = os.path.dirname(d)
            if nd == d:
                break
            d = nd
    for c in cands:
        art = os.path.join(c, "client", "Assets", "Resources", "UI", "Art")
        if os.path.isdir(art):
            return c, art
    sys.stderr.write(
        "FATAL: cannot locate client/Assets/Resources/UI/Art.\n"
        "  tried: %s\n"
        "  this probe reads textures that ship with the project:\n"
        "    <project>/client/Assets/Resources/UI/Art/fx_blood1..6.png   (target, #158 subject)\n"
        "    <project>/client/Assets/Resources/UI/Art/fx_shot1..5.png    (control)\n"
        "  set CLOVER_PROJECT_ROOT=<project root> or run from inside the repository.\n"
        % "; ".join(cands))
    return None, None


ROOT, ART = _resolve()
if ART is None:
    sys.exit(2)
CUT = 0.25
THR = int(CUT * 255 + 0.5)          # 64: clip keeps a > 0.25 -> a >= 64

TARGET = ["fx_blood%d" % i for i in range(1, 7)]
CONTROL = ["fx_shot%d" % i for i in range(1, 6)]

# explicit missing-texture guard (unified patch 2026-10-14: a criterion that reads a file it cannot
# find must say so and point at where it lives -- never continue with a silently smaller set).
_missing = [n + ".png" for n in TARGET + CONTROL
            if not os.path.isfile(os.path.join(ART, n + ".png"))]
if _missing:
    sys.stderr.write("FATAL: %d of %d textures missing under %s: %s\n"
                     % (len(_missing), len(TARGET) + len(CONTROL), ART, ", ".join(_missing)))
    sys.stderr.write("  they are project content (Resources/UI/Art), not build output -- check "
                     "that the repository is complete.\n")
    sys.exit(3)


def read_png(path):
    data = open(path, "rb").read()
    pos, idat = 8, b""
    w = h = None
    while pos < len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        chunk = data[pos + 8:pos + 8 + ln]
        if typ == b"IHDR":
            w, h, _, colortype = struct.unpack(">IIBB", chunk[:10])
        elif typ == b"IDAT":
            idat += chunk
        elif typ == b"IEND":
            break
        pos += 12 + ln
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[colortype]
    raw = zlib.decompress(idat)
    stride = w * ch
    out = bytearray()
    prev = bytearray(stride)
    off = 0
    for _ in range(h):
        f = raw[off]
        off += 1
        line = bytearray(raw[off:off + stride])
        off += stride
        if f == 1:
            for i in range(ch, stride):
                line[i] = (line[i] + line[i - ch]) & 0xFF
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = line[i - ch] if i >= ch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = line[i - ch] if i >= ch else 0
                b = prev[i]
                c = prev[i - ch] if i >= ch else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out += line
        prev = line
    return w, h, ch, out


def stats(name):
    w, h, ch, buf = read_png(os.path.join(ART, name + ".png"))
    have_a = ch in (2, 4)
    n = w * h
    zero = halo = kept = solid = 0
    sum_a = 0
    half = max(1, (n + 1) // 2)          # >=half of full alpha = "visual core"
    sfmin, sfmax = [10 ** 9], [-1]
    kmin, kmax = [10 ** 9], [-1]
    osum = 0
    for y in range(h):
        row = y * w * ch
        for x in range(w):
            a = buf[row + x * ch + (ch - 1)] if have_a else 255
            osum += buf[row + x * ch] if ch >= 3 else 0
            sum_a += a
            if a == 0:
                zero += 1
                continue
            if a <= 63:
                halo += 1
            else:
                kept += 1
            if a >= 128:
                solid += 1
            if a >= 128:
                if x < sfmin[0]:
                    sfmin[0] = x
                if x > sfmax[0]:
                    sfmax[0] = x
            if a >= 64:
                if x < kmin[0]:
                    kmin[0] = x
                if x > kmax[0]:
                    kmax[0] = x
    span = lambda lo, hi: (hi[0] - lo[0] + 1) if hi[0] >= 0 else 0
    return dict(
        name=name, w=w, h=h, ch=ch, n=n,
        zero=zero, halo=halo, kept=kept, solid=solid,
        halo_pct=100.0 * halo / n, kept_pct=100.0 * kept / n, solid_pct=100.0 * solid / n,
        mean_a=sum_a / n,
        mean_a_ink=(sum_a / max(1, n - zero)),
        ink_span_any=(span([0], [w - 1]) if kept + halo else 0),
        span_kept=span(kmin, kmax), span_solid=span(sfmin, sfmax),
        rgbmean=(osum / (n * 3)) if ch >= 3 else None,
        cut=THR,
    )


def show(title, names):
    print("== %s ==" % title)
    print("%-14s %4s %4s %5s %7s %7s %7s %7s %8s %6s %6s %6s" % (
        "file", "w", "h", "px", "ink%", "halo%", "keep%", "solid%", "meanA/ink",
        "spanK", "spanS", "rgbA"))
    rows = []
    for nm in names:
        try:
            s = stats(nm)
        except FileNotFoundError:
            print("%-14s MISSING" % nm)
            continue
        rows.append(s)
        print("%-14s %4d %4d %5d %7.1f %7.1f %7.1f %7.1f %8.1f %6d %6d %6.1f" % (
            s["name"], s["w"], s["h"], s["n"],
            100.0 * (s["halo"] + s["kept"]) / s["n"],
            s["halo_pct"], s["kept_pct"], s["solid_pct"],
            s["mean_a_ink"], s["span_kept"], s["span_solid"],
            (s["rgbmean"] or -1)))
    if rows:
        n = len(rows)
        avg = lambda k: sum(r[k] for r in rows) / n
        _ink = sum(r["halo"] + r["kept"] for r in rows)
        print("  -> avg(ink%%)=%.1f  avg(halo%%)=%.1f  avg(kept%%)=%.1f  avg(meanA/ink)=%.1f  "
              "halo-share-of-ink=%.1f%%" % (
                  100.0 * _ink / sum(r["n"] for r in rows),
                  avg("halo_pct"), avg("kept_pct"), avg("mean_a_ink"),
                  100.0 * sum(r["halo"] for r in rows) / max(1, _ink)))
    return rows


def predict(names, wall=128):
    """Pre-registered prediction of the in-Play A/B pixel counts, from the shipped PNGs alone.

    Blend (same expression the shader uses: rgb *= a, Blend One OneMinusSrcAlpha) means a texel
    over a wall of value `wall` produces  out = tex*a + wall*(1-a),  so
        dmax(texel) = a * max_channel|tex - wall|.
    Projection: the probe camera is 3.6 m from the wall at fov 42 with a 960x540 target
        px_per_m  = (540/2) / (3.6 * tan(21deg))      px_per_texel = px_per_m * 0.0254
    => one texel covers ~px_per_texel^2 screen pixels, and a frame counts as "changed" where
    dmax > 8/255 (>3% of full scale).
    """
    import math
    ppm = (540.0 / 2.0) / (3.6 * math.tan(math.radians(21.0)))
    ppt = ppm * 0.0254
    px_per_texel = ppt * ppt
    totals = dict(base=0.0, clip=0.0, noclip=0.0, halo=0.0, kept=0.0)
    for nm in names:
        w, h, ch, buf = read_png(os.path.join(ART, nm + ".png"))
        have_a = ch in (2, 4)
        for i in range(w * h):
            o = i * ch
            a = buf[o + ch - 1] if have_a else 255
            r = buf[o]
            g = buf[o + 1] if ch >= 3 else r
            b2 = buf[o + 2] if ch >= 3 else r
            af = a / 255.0
            d = max(abs(r - wall), abs(g - wall), abs(b2 - wall)) * af
            if d <= 8:
                continue
            if a > 63:
                totals["clip"] += px_per_texel
                totals["kept"] += px_per_texel
            else:
                totals["noclip"] += px_per_texel
                if a > 0:
                    totals["halo"] += px_per_texel
    return ppm, ppt, px_per_texel, totals


for wallval in (96, 128, 160):
    ppm, ppt, pptx, tot = predict(TARGET, wallval)
    print("PREDICT wall=%3d : px/m=%.1f px/texel=%.2f px/texel^2=%.1f | A(clip)~%.0f px  "
          "B(noclip)~%.0f px  -> A-vs-B changed~%.0f px  (halo-only, the pixels the clip removes)"
          % (wallval, ppm, ppt, pptx, tot["clip"], tot["clip"] + tot["noclip"], tot["halo"]))

t = show("TARGET  {blood1..6  (fx_blood*)  -- #158 subject", TARGET)
c = show("CONTROL {shot1..5   (fx_shot*)   -- already clipped, must not change", CONTROL)

print()
print("cutoff = %.2f -> alpha <= %d is discarded (clip(c.a - _Cutoff))" % (CUT, THR - 1))
if t:
    ink = sum(r["halo"] + r["kept"] for r in t)
    halo = sum(r["halo"] for r in t)
    print("blood: %d ink texels over %d canvases -> %d (%.1f%%) are pure halo and get clipped; "
          "%d (%.1f%%) survive" % (ink, len(t), halo, 100.0 * halo / max(1, ink),
                                   ink - halo, 100.0 * (ink - halo) / max(1, ink)))
    print("blood alphachannel present: %s" % ("yes" if t[0]["ch"] in (2, 4) else "NO"))
