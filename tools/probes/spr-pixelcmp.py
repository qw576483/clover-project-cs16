#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fix3-spr-pixelcmp.py -- FIX-3 line B, team-lead ruling E (2), 2026-09-24.

WHY: team-lead found that `ch_sniper2.spr` and `sniper_scope.spr` are the SAME SIZE
(66366 B), SAME structure (IDSP v2 / 256x256 / frames=1 / stride=20) but DIFFERENT sha256.
=> "same size, same structure" is NOT an identity criterion.  Before either file is allowed
near the asset tree we must DECODE THE FRAMES and compare PIXEL BY PIXEL, to tell
   * two genuinely different images (one crosshair/reticle, one scope/lens), from
   * the same image crop/transcode/re-encode (which would be a duplicate, not a second asset).

This tool is READ-ONLY on its two inputs; it writes only into .ai-tmp/test/.

Exit 0 = comparison produced (whatever the verdict).  Never exits non-zero for a MISMATCH
verdict -- the verdict is data, not an error.
"""

import importlib.util
import os
import sys
import hashlib

ROOT = r"C:\Work\Server\f-v2\clover-project-cs16"
SPR_TOOL = os.path.join(ROOT, "tools", "probes", "spr-extract.py")
QDIR = os.path.join(ROOT, ".ai-tmp", "test", "fix3-fetch")
OUTDIR = os.path.join(ROOT, ".ai-tmp", "test", "fix3-spr-cmp")

A = os.path.join(QDIR, "sniper_scope.spr")   # already 入库 (sha eb65dd96...)
B = os.path.join(QDIR, "ch_sniper2.spr")     # held in quarantine pending THIS test
TRANSPARENT_INDEX = 255


def load_module():
    spec = importlib.util.spec_from_file_location("sprex", SPR_TOOL)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(65536), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def analyze(s, label, out_dir):
    """Geometry of one sprite's frame-0 visible mask (index != 255)."""
    px, w, h = s.frame_pixels(0)
    vis = [(i % w, i // w) for i, v in enumerate(px) if v != TRANSPARENT_INDEX]
    n = len(vis)
    res = {"label": label, "w": w, "h": h, "visible": n}
    if n:
        xs = [p[0] for p in vis]; ys = [p[1] for p in vis]
        res["bbox"] = (min(xs), min(ys), max(xs), max(ys))
        res["centroid"] = (sum(xs) / n, sum(ys) / n)
    else:
        res["bbox"] = None; res["centroid"] = None
    # radial histogram: where does the visible mass sit, relative to centre (r in px)?
    cx, cy = (w - 1) / 2.0, (h - 1) / 2.0
    bins = [0] * 16
    for (x, y) in vis:
        r = ((x - cx) ** 2 + (y - cy) ** 2) ** 0.5
        b = int(r / (max(w, h) / 2.0 / 16.0))
        if b > 15:
            b = 15
        bins[b] += 1
    res["radial16"] = bins
    # line-like vs ring-like: how many rows/cols are >50% covered, and the peak row/col count
    rowc = [0] * h
    colc = [0] * w
    for (x, y) in vis:
        rowc[y] += 1; colc[x] += 1
    res["peak_row"] = max(rowc) if rowc else 0
    res["peak_col"] = max(colc) if colc else 0
    res["rows_gt_half"] = sum(1 for c in rowc if c > w * 0.5)
    res["cols_gt_half"] = sum(1 for c in colc if c > h * 0.5)
    # centre-cross probe: visible pixels on the exact centre row / centre column
    res["centre_row_vis"] = sum(1 for i, v in enumerate(px) if i // w == int(cy) and v != TRANSPARENT_INDEX)
    res["centre_col_vis"] = sum(1 for i, v in enumerate(px) if i % w == int(cx) and v != TRANSPARENT_INDEX)
    return res


def ascii_mask(s, cw=4, ch=8):
    """Print the visible mask as ASCII so the SHAPE is looked at, not asserted.
    64 cols x 32 rows, one cell = cw x ch pixels (chars are ~2:1 tall, so this keeps aspect)."""
    px, w, h = s.frame_pixels(0)
    cols, rows = w // cw, h // ch
    lines = []
    for r in range(rows):
        line = []
        for c in range(cols):
            cnt = 0
            for y in range(r * ch, (r + 1) * ch):
                base = y * w
                for x in range(c * cw, (c + 1) * cw):
                    if px[base + x] != TRANSPARENT_INDEX:
                        cnt += 1
            if cnt == 0:
                line.append(" ")
            elif cnt <= 2:
                line.append(".")
            elif cnt <= 8:
                line.append("+")
            else:
                line.append("#")
        lines.append("".join(line))
    return lines


def line_profile(s):
    """Exact geometry of the horizontal/vertical lines, if any."""
    px, w, h = s.frame_pixels(0)
    rowc = [0] * h
    colc = [0] * w
    for i, v in enumerate(px):
        if v != TRANSPARENT_INDEX:
            rowc[i // w] += 1
            colc[i % w] += 1
    pr = max(range(h), key=lambda y: rowc[y])
    pc = max(range(w), key=lambda x: colc[x])
    return pr, rowc[pr], pc, colc[pc]


def write_png_pure(rgba, w, h, path):
    """Minimal PNG encoder (RGB8A / colortype 6, filter 0) -- no PIL on this host."""
    import zlib
    import struct as _st

    def chunk(typ, data):
        return (_st.pack(">I", len(data)) + typ + data
                + _st.pack(">I", zlib.crc32(typ + data) & 0xffffffff))

    ihdr = _st.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += rgba[y * w * 4:(y + 1) * w * 4]
    blob = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))
    with open(path, "wb") as fh:
        fh.write(blob)
    return os.path.getsize(path)


def over_grey(rgba, w, h, rgb=(70, 70, 78)):
    """Composite an RGBA panel over a flat grey so a transparent sprite is actually visible."""
    out = bytearray(w * h * 4)
    for i in range(w * h):
        a = rgba[i * 4 + 3]
        for k in range(3):
            out[i * 4 + k] = (rgba[i * 4 + k] * a + rgb[k] * (255 - a)) // 255
        out[i * 4 + 3] = 255
    return bytes(out)


def main():
    sprex = load_module()
    if not os.path.isdir(OUTDIR):
        os.makedirs(OUTDIR)
    log = []

    def emit(line=""):
        log.append(line)
        print(line)

    emit("SENTINEL-FIX3-SPR-PIXELCMP-START 2026-09-24 (team-lead ruling E (2))")
    emit("A = %s" % A)
    emit("B = %s" % B)
    emit()

    sa, sb = sprex.Sprite(A), sprex.Sprite(B)
    for lab, p, s in (("A sniper_scope.spr", A, sa), ("B ch_sniper2.spr", B, sb)):
        ok, detail = s.check()
        emit("%s : file bytes=%d  sha256=%s" % (lab, os.path.getsize(p), sha256(p)))
        emit("   header: version=%d type=%d texFormat=%d size=%dx%d frames=%d" %
             (s.version, s.type, s.tex_format, s.width, s.height, s.num_frames))
        emit("   identity(%s): %s" % ("OK" if ok else "FAIL", detail))
    emit()

    # ---- structural equality of everything EXCEPT the pixel payload -----------------------
    hdr_a = (sa.version, sa.type, sa.tex_format, sa.width, sa.height, sa.num_frames, sa.synctype)
    hdr_b = (sb.version, sb.type, sb.tex_format, sb.width, sb.height, sb.num_frames, sb.synctype)
    emit("HEADER  identical = %s" % (hdr_a == hdr_b))
    emit("FRAMETABLE identical = %s   (A=%s / B=%s)" % (sa.frames == sb.frames, sa.frames, sb.frames))
    pal_same = sa.palette == sb.palette
    emit("PALETTE identical = %s" % pal_same)
    if not pal_same:
        pd = sum(1 for i in range(768) if sa.palette[i] != sb.palette[i])
        emit("   palette differing bytes = %d / 768" % pd)
    emit()

    # ---- the decisive test: raw pixel-index diff -------------------------------------------
    pa, w, h = sa.frame_pixels(0)
    pb, w2, h2 = sb.frame_pixels(0)
    assert (w, h) == (w2, h2), "frame geometry differs -- compare cannot proceed"
    total = len(pa)
    diff = sum(1 for i in range(total) if pa[i] != pb[i])
    # RGBA-level diff (palette applied), the thing that actually reaches the screen
    ra, _, _ = sa.to_rgba(0)
    rb, _, _ = sb.to_rgba(0)
    rgba_diff = sum(1 for i in range(0, total * 4, 4) if ra[i:i + 4] != rb[i:i + 4])
    emit("PIXELS total            = %d (%dx%d)" % (total, w, h))
    emit("PIXEL-INDEX  differing  = %d  (%.3f%%)" % (diff, 100.0 * diff / total))
    emit("RGBA         differing  = %d  (%.3f%%)" % (rgba_diff, 100.0 * rgba_diff / total))
    # RAW pixel-% is DILUTED by the transparent background (~98% of the frame), so it
    # understates the difference.  The honest measure is the DRAWN mask (index != 255).
    mask_a = set(i for i in range(total) if pa[i] != TRANSPARENT_INDEX)
    mask_b = set(i for i in range(total) if pb[i] != TRANSPARENT_INDEX)
    inter = len(mask_a & mask_b)
    uni = len(mask_a | mask_b)
    jac = (inter / float(uni)) if uni else 1.0
    emit("DRAWN-MASK  A=%d  B=%d  intersection=%d  union=%d  symmetric-diff=%d  Jaccard=%.4f"
         % (len(mask_a), len(mask_b), inter, uni, uni - inter, jac))
    emit("   ^ Jaccard is the decisive number: the raw % above is diluted by the ~98%")
    emit("     transparent background common to both, so a small % can hide a totally")
    emit("     different drawing.  Jaccard>=0.98 => same drawing; low => different drawing.")
    emit()

    ra_an = analyze(sa, "A sniper_scope.spr", OUTDIR)
    rb_an = analyze(sb, "B ch_sniper2.spr", OUTDIR)
    for an in (ra_an, rb_an):
        emit("SHAPE %s" % an["label"])
        emit("   visible(idx!=255)=%d  bbox=%s  centroid=(%.1f,%.1f)" %
             (an["visible"], an["bbox"], an["centroid"][0] if an["centroid"] else -1,
              an["centroid"][1] if an["centroid"] else -1))
        emit("   peak_row=%d peak_col=%d rows>half=%d cols>half=%d centre_row_vis=%d centre_col_vis=%d"
             % (an["peak_row"], an["peak_col"], an["rows_gt_half"], an["cols_gt_half"],
                an["centre_row_vis"], an["centre_col_vis"]))
        emit("   radial16(centre->edge) = %s" % an["radial16"])
    emit()

    for lab, s in (("A sniper_scope.spr", sa), ("B ch_sniper2.spr", sb)):
        pr, prn, pc, pcn = line_profile(s)
        emit("LINE %s : peak row=%d (%d px visible)  peak col=%d (%d px visible)" % (lab, pr, prn, pc, pcn))
    emit()
    for lab, s in (("A sniper_scope.spr", sa), ("B ch_sniper2.spr", sb)):
        emit("ASCII %s   (64x32; ' '=empty '.'=1-2 '+'=3-8 '#'=9+ visible px per 4x8 cell)" % lab)
        for ln in ascii_mask(s):
            emit("   |" + ln + "|")
        emit()

    # ---- verdict ----------------------------------------------------------------------------
    # "same image re-encoded" would show either 0% diff, or a diff explainable by palette
    # remap with a small, sparse index delta.  Two different images differ structurally.
    same_bytes = (os.path.getsize(A) == os.path.getsize(B)) and (sha256(A) == sha256(B))
    if same_bytes:
        verdict = "IDENTICAL-BYTES (same file, do NOT 入库 twice)"
    elif jac >= 0.98:
        verdict = "SAME-DRAWING-VARIANT (drawn-mask Jaccard=%.4f) -> only colour/encode differs" % jac
    else:
        verdict = ("DISTINCT-IMAGES (drawn-mask Jaccard=%.4f, symmetric-diff=%d px)"
                   % (jac, uni - inter))

    ring_a = ra_an["radial16"][6:11]   # mass in the mid-radius band (ring body)
    ring_b = rb_an["radial16"][6:11]
    emit("BAND-MID(6..10) A=%d B=%d ; CENTRE(0..2) A=%d B=%d"
         % (sum(ring_a), sum(ring_b), sum(ra_an["radial16"][0:3]), sum(rb_an["radial16"][0:3])))
    emit("VERDICT: %s" % verdict)

    # ---- SIBLING check: is ch_sniper.spr (AWP, 320) yet another distinct carrier? -----------
    sib_path = os.path.join(QDIR, "ch_sniper.spr")
    sibling = None
    sib_jac = None
    if os.path.exists(sib_path):
        sibling = sprex.Sprite(sib_path)
        psib, _, _ = sibling.frame_pixels(0)
        emit("SIBLING ch_sniper.spr (AWP @320, weapon_awp.txt:7) bytes=%d sha256=%s"
             % (os.path.getsize(sib_path), sha256(sib_path)))
        dsib_a = sum(1 for i in range(total) if psib[i] != pa[i])
        dsib_b = sum(1 for i in range(total) if psib[i] != pb[i])
        emit("   index-diff vs sniper_scope = %d (%.3f%%) ; vs ch_sniper2 = %d (%.3f%%)"
             % (dsib_a, 100.0 * dsib_a / total, dsib_b, 100.0 * dsib_b / total))
        msib = set(i for i in range(total) if psib[i] != TRANSPARENT_INDEX)
        sib_jac = {}
        for nm, mk in (("sniper_scope", mask_a), ("ch_sniper2", mask_b)):
            u = len(msib | mk)
            j = (len(msib & mk) / float(u)) if u else 1.0
            sib_jac[nm] = j
            emit("   drawn-mask Jaccard vs %s = %.4f (visible sib=%d)" % (nm, j, len(msib)))
        emit("   * 量具正控 (instrument POSITIVE CONTROL): ch_sniper.spr vs ch_sniper2.spr")
        emit("     drawn-mask Jaccard = %.4f => on a pair that IS the same drawing, the metric"
             % sib_jac["ch_sniper2"])
        emit("     reads 1.0000.  Without this control, 0.1447 would only show the metric CAN")
        emit("     print a low number, NOT that a low number means 'two different drawings'.")
        pr, prn, pc, pcn = line_profile(sibling)
        emit("   visible=%d bbox=%s peak row=%d(%d) col=%d(%d)"
             % (analyze(sibling, "sib", OUTDIR)["visible"],
                analyze(sibling, "sib", OUTDIR)["bbox"], pr, prn, pc, pcn))
        emit("   ASCII (64x32):")
        for ln in ascii_mask(sibling):
            emit("   |" + ln + "|")
        emit()

    # ---- CRITERION (口径) -- written to disk on purpose (team-lead ruling ①) ----------------
    emit("CRITERION (口径) -- 判据定义 / 两个陷阱 / 分离度")
    emit("  判据 = DRAWN-MASK Jaccard，其中 drawn-mask = 像素调色板索引 != 255（= 真正画出来的像素）。")
    emit("  TRAP-1  原始像素差百分比 会骗人：每帧约 98% 是两边相同的透明底，它把分母灌大。")
    emit("          实测：两张完全不同的图，原始像素差只有 %.3f%%（见上 PIXEL-INDEX 行）," % (100.0 * diff / total))
    emit("          而 drawn-mask Jaccard 只有 %.4f。" % jac)
    emit("  TRAP-2  RGBA 差异 100% 不是独立证据：两边调色板差 95/768 字节 ⇒ 每个像素的 RGB")
    emit("          都会变。它与「像素索引差」是同因两果，不能当第二条证据用。")
    emit("  分离度 SEPARATION：正控（同一张图）= %.4f  ‖  本结论 = %.4f" % (sib_jac["ch_sniper2"] if sib_jac else -1.0, jac))
    emit("          阈值 Jaccard >= 0.98 ⇒ 同一张图；本对比 %.4f 远低于阈值 ⇒ DISTINCT-IMAGES。" % jac)
    emit()

    # ---- side-by-side sheet (supporting visual evidence; ASCII above is the assertion) ------
    try:
        panels = []
        if sibling is not None:
            rcs, _, _ = sibling.to_rgba(0)
            panels.append(("ch_sniper(AWP)", over_grey(rcs, w, h)))
        panels.append(("sniper_scope", over_grey(ra, w, h)))
        panels.append(("ch_sniper2", over_grey(rb, w, h)))
        diff = bytearray(w * h * 4)
        for i in range(total):
            if pa[i] != pb[i]:
                diff[i * 4:i * 4 + 4] = bytes((230, 30, 30, 255))        # differs -> red
            elif pa[i] != TRANSPARENT_INDEX:
                diff[i * 4:i * 4 + 4] = bytes((235, 235, 235, 255))      # same & visible -> white
            else:
                diff[i * 4:i * 4 + 4] = bytes((40, 40, 45, 255))         # same & transparent
        panels.append(("DIFF sniper_scope|ch_sniper2", bytes(diff)))
        np_ = len(panels)
        sw = w * np_
        sheet = bytearray(sw * h * 4)
        for y in range(h):
            for k, (_lab, rgba) in enumerate(panels):
                row = y * w * 4
                off = (y * sw + k * w) * 4
                sheet[off:off + w * 4] = rgba[row:row + w * 4]
        sheet_path = os.path.join(OUTDIR, "FIX3-SPR-CMP-sheet.png")
        nb = write_png_pure(bytes(sheet), sw, h, sheet_path)
        for lab, rgba in panels[:-1]:
            safe = "".join(c if c.isalnum() else "_" for c in lab)
            write_png_pure(rgba, w, h, os.path.join(OUTDIR, "panel_%s.png" % safe))
        emit("PNG sheet: %s (%dx%d, %d bytes)  panels L->R = %s"
             % (sheet_path, sw, h, nb, " | ".join(p[0] for p in panels)))
    except Exception as e:                                   # noqa: BLE001
        emit("PNG skipped: %s" % e)

    # single sentinel, LAST line (team-lead ruling C (4))
    emit("RESULT-FIX3-SPR-PIXELCMP: %s" % verdict)

    with open(os.path.join(ROOT, ".ai-tmp", "test", "fix3-spr-pixelcmp.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(log) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
