#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
make-fix4c-sheet.py —— 片FIX-4 线C 弹痕的**联络图**（AI 只读这一张）。

为什么要有它：`capture_game_view` 落的是 1280x720，而弹痕的"可见核心"只有几十像素宽，
在一整帧里肉眼分辨不出来。本脚本把 before/after 两帧**裁到弹着那一片区域**（准星周围）
并按整数倍放大，再并排放一张"差分放大图"（弹痕是黑洞 ⇒ 差分里该区域应有成片的变暗像素）。

⛔ 这是**观测性**证据，不是通过条件：整帧里还有会动的 bot / HUD 数字（弹药 0/40 就在变），
   所以"有差异"本身永远成立、判不了红。**通过条件是 tools/probes/probe-fix4c-verdict.py 的
   J-D1**（与距离无关的可见核心宽 ≥ 0.03125 m），本图只用来"看一眼到底有没有那几点黑"。

用法：
  python tools/probes/make-fix4c-sheet.py --before <png> --after <png> --out <png>
"""
import argparse
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, "..", ".."))


def read_png(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a png: %s" % path)
    i, w, h, ct, bd, idat = 8, 0, 0, 0, 8, b""
    while i < len(data):
        ln = struct.unpack(">I", data[i:i + 4])[0]
        typ = data[i + 4:i + 8]
        body = data[i + 8:i + 8 + ln]
        i += 12 + ln
        if typ == b"IHDR":
            w, h, bd, ct = struct.unpack(">IIBB", body[:10])
        elif typ == b"IDAT":
            idat += body
        elif typ == b"IEND":
            break
    raw = zlib.decompress(idat)
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
    stride = w * ch
    rows, prev, o = [], bytearray(stride), 0
    for _y in range(h):
        f = raw[o]; o += 1
        line = bytearray(raw[o:o + stride]); o += stride
        if f == 1:
            for x in range(ch, stride):
                line[x] = (line[x] + line[x - ch]) & 255
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - ch] if x >= ch else 0
                b = prev[x]
                c = prev[x - ch] if x >= ch else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        rows.append(bytes(line)); prev = line
    return w, h, ch, rows


def rgb(w, h, ch, rows, x, y):
    r = rows[y]
    if ch >= 3:
        return r[x * ch], r[x * ch + 1], r[x * ch + 2]
    v = r[x * ch]
    return v, v, v


def write_png(path, w, h, pix):
    """pix = list of rows, each row a bytearray of RGB triples."""
    raw = b"".join(b"\x00" + bytes(r) for r in pix)

    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    out = b"\x89PNG\r\n\x1a\n"
    out += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    out += chunk(b"IDAT", zlib.compress(raw, 6))
    out += chunk(b"IEND", b"")
    open(path, "wb").write(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--before", default=os.path.join(PROJ, ".ai-tmp", "screenshots", "fix4d_before.png"))
    ap.add_argument("--after", default=os.path.join(PROJ, ".ai-tmp", "screenshots", "fix4d_after.png"))
    ap.add_argument("--out", default=os.path.join(PROJ, ".ai-tmp", "screenshots", "fix4c-decal-sheet.png"))
    ap.add_argument("--cw", type=int, default=420, help="裁剪宽（原像素）")
    ap.add_argument("--chh", type=int, default=300, help="裁剪高（原像素）")
    ap.add_argument("--zoom", type=int, default=2)
    ap.add_argument("--cy-frac", type=float, default=0.55, help="裁剪中心的行位置（占整帧高比例）")
    args = ap.parse_args()

    w0, h0, c0, r0 = read_png(args.before)
    w1, h1, c1, r1 = read_png(args.after)
    if (w0, h0) != (w1, h1):
        print("分辨率不同"); return 1
    cw = min(args.cw, w0); chh = min(args.chh, h0)
    x0 = (w0 - cw) // 2
    y0 = max(0, min(h0 - chh, int(h0 * args.cy_frac) - chh // 2))
    z = args.zoom

    panels = []
    for tag, (cc, rr) in (("before", (c0, r0)), ("after", (c1, r1))):
        panel = []
        for y in range(y0, y0 + chh):
            row = bytearray()
            for x in range(x0, x0 + cw):
                R, G, B = rgb(w0, h0, cc, rr, x, y)
                row += bytes((R, G, B)) * z
            for _ in range(z):
                panel.append(bytes(row))
        panels.append(panel)

    # 差分放大：把 |after-before| 的亮度差放大 6 倍，变暗画红、变亮画绿
    dpanel = []
    for y in range(y0, y0 + chh):
        row = bytearray()
        for x in range(x0, x0 + cw):
            a = sum(rgb(w0, h0, c0, r0, x, y)) // 3
            b = sum(rgb(w0, h0, c1, r1, x, y)) // 3
            d = b - a
            mag = min(255, abs(d) * 6)
            if d <= -8:
                px = (mag, 0, 0)          # 变暗 = 红（弹痕=黑洞）
            elif d >= 8:
                px = (0, mag, 0)          # 变亮 = 绿
            else:
                px = (0, 0, 0)
            row += bytes(px) * z
        for _ in range(z):
            dpanel.append(bytes(row))
    panels.append(dpanel)

    W = (cw * z) * 3 + 8
    H = chh * z
    out = [bytearray(b"\x20" * (W * 3)) for _ in range(H)]
    for pi, panel in enumerate(panels):
        ox = pi * (cw * z + 4)
        for y in range(H):
            out[y][ox * 3:(ox + cw * z) * 3] = panel[y]
    write_png(args.out, W, H, out)
    print("sheet -> %s  %dx%d（左=before 中=after 右=差分x6 红=变暗）" % (args.out, W, H))
    print("裁剪区域：x[%d,%d) y[%d,%d) 放大 x%d" % (x0, x0 + cw, y0, y0 + chh, z))
    return 0


if __name__ == "__main__":
    sys.exit(main())
