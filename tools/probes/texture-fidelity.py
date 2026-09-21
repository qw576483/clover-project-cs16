#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片W 判据资产：把"贴图看着粗糙"变成两个数字。

① Point(Nearest) vs Bilinear 采样差异：把原图按 8x 放大（模拟角色在屏幕上被放大到
   每个纹素占多个屏幕像素时的情形），分别用最近邻/双线性重采样，量"逐像素差"。
   ⇒ 差越大 ⇒ Point 造成的"马赛克块感"越强（原版 GoldSrc 默认是双线性+mip）。
② BC1/DXT1 往返误差：对原图做 4x4 块 565 端点 + 4 色插值的编码再解码，量 PSNR 与最大误差。
   ⇒ 有损压缩在皮肤渐变上引入的误差量级。

用法: python tools/probes/texture-fidelity.py <png...>
"""
import sys
from PIL import Image

BLOCK = 8


def nearest(im, s):
    w, h = im.size
    return im.resize((w * s, h * s), Image.NEAREST)


def bilinear(im, s):
    w, h = im.size
    return im.resize((w * s, h * s), Image.BILINEAR)


def diff_stats(a, b):
    pa, pb = a.load(), b.load()
    w, h = a.size
    n = 0
    s = 0
    mx = 0
    gt8 = 0
    for y in range(h):
        for x in range(w):
            q = pa[x, y]
            r = pb[x, y]
            d = abs(q[0] - r[0]) + abs(q[1] - r[1]) + abs(q[2] - r[2])
            d //= 3
            n += 1
            s += d
            if d > mx:
                mx = d
            if d > 8:
                gt8 += 1
    return n, s / n, mx, 100.0 * gt8 / n


def rgb565(c):
    return ((c[0] >> 3) << 11) | ((c[1] >> 2) << 5) | (c[2] >> 3)


def unpack565(v):
    r = (v >> 11) & 0x1F
    g = (v >> 5) & 0x3F
    b = v & 0x1F
    return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2))


def bc1_encode_decode(im):
    """极简 BC1（每块 2 端点 + 4 色插值），不解色索引优化——只求误差量级。"""
    w, h = im.size
    px = im.load()
    out = Image.new("RGB", (w, h))
    po = out.load()
    for by in range(0, h, 4):
        for bx in range(0, w, 4):
            cols = []
            for y in range(by, min(by + 4, h)):
                for x in range(bx, min(bx + 4, w)):
                    cols.append(px[x, y])
            lo = min(cols, key=lambda c: c[0] + c[1] + c[2])
            hi = max(cols, key=lambda c: c[0] + c[1] + c[2])
            v0, v1 = rgb565(hi), rgb565(lo)
            c0, c1 = unpack565(v0), unpack565(v1)
            if v0 <= v1:              # 退化块：BC1 只给 3 色
                c2 = tuple((2 * c0[i] + c1[i]) // 3 for i in range(3))
                c3 = tuple((c0[i] + 2 * c1[i]) // 3 for i in range(3))
            else:
                c2 = tuple((2 * c0[i] + c1[i]) // 3 for i in range(3))
                c3 = tuple((c0[i] + 2 * c1[i]) // 3 for i in range(3))
            pal = [c0, c1, c2, c3]
            for y in range(by, min(by + 4, h)):
                for x in range(bx, min(bx + 4, w)):
                    p = px[x, y]
                    best = min(pal, key=lambda c: (c[0] - p[0]) ** 2 + (c[1] - p[1]) ** 2 + (c[2] - p[2]) ** 2)
                    po[x, y] = best
    return out


def psnr(a, b):
    pa, pb = a.load(), b.load()
    w, h = a.size
    se = 0
    mx = 0
    for y in range(h):
        for x in range(w):
            q, r = pa[x, y], pb[x, y]
            for i in range(3):
                d = q[i] - r[i]
                se += d * d
                ax = abs(d)
                if ax > mx:
                    mx = ax
    mse = se / (w * h * 3)
    if mse <= 0:
        return 99.0, 0
    import math
    return 10.0 * math.log10(255.0 * 255.0 / mse), mx


def main():
    print("file\twxh\tuniqueCols\tPoint-vs-Bilinear@%dx meanAbs\tmax\t%%px>8\tBC1 PSNR(dB)\tBC1 maxΔ" % BLOCK)
    for p in sys.argv[1:]:
        im = Image.open(p).convert("RGB")
        w, h = im.size
        colors = len(im.getcolors(maxcolors=1 << 24) or [])
        n, mean_d, mx, gt8 = diff_stats(nearest(im, BLOCK), bilinear(im, BLOCK))
        d = bc1_encode_decode(im)
        ps, bmx = psnr(im, d)
        print("%s\t%dx%d\t%d\t%.2f\t%d\t%.2f\t%.2f\t%d" % (
            p.replace("\\", "/").split("/")[-1], w, h, colors, mean_d, mx, gt8, ps, bmx))


if __name__ == "__main__":
    main()
