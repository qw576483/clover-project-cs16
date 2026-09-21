#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片W 判据资产：像素级对照统计（与原版基线图同一套口径）。

逐张给：尺寸 / 平均亮度 / 亮度分位 p05,p50,p95 / 对比度(RMS) / 平均饱和度 /
"远景带"（画面上 20% 水平带）的 RMS 对比度 —— 后者当"雾感"代理：雾越重 ⇒ 远景对比越低。

用法: python tools/probes/pixel-compare.py <图...> [--tag 前缀]
"""
import sys
from PIL import Image


def lum(p):
    return 0.2126 * p[0] + 0.7152 * p[1] + 0.0722 * p[2]


def sat(p):
    mx = max(p)
    mn = min(p)
    return 0.0 if mx == 0 else (mx - mn) / float(mx)


def stats(im, name):
    w, h = im.size
    px = im.load()
    n = w * h
    hist = [0] * 256
    s = 0.0
    s2 = 0.0
    ss = 0.0
    for y in range(h):
        for x in range(w):
            p = px[x, y]
            l = lum(p)
            s += l
            s2 += l * l
            ss += sat(p)
            hist[min(255, int(l))] += 1
    mean = s / n
    var = max(0.0, s2 / n - mean * mean)
    rms = var ** 0.5

    def pct(q):
        tgt = q * n
        acc = 0
        for i in range(256):
            acc += hist[i]
            if acc >= tgt:
                return i
        return 255

    # 远景带（上 20%）
    y1 = max(1, h // 5)
    fs = 0.0
    fs2 = 0.0
    fn = 0
    for y in range(y1):
        for x in range(w):
            l = lum(px[x, y])
            fs += l
            fs2 += l * l
            fn += 1
    fm = fs / fn
    frms = max(0.0, fs2 / fn - fm * fm) ** 0.5
    print("%s\t%dx%d\t%.1f\t%d\t%d\t%d\t%.1f\t%.3f\t%.1f" % (
        name, w, h, mean, pct(0.05), pct(0.50), pct(0.95), rms, ss / n, frms))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    print("file\twxh\tmeanLum\tp05\tp50\tp95\tRMS对比度\t平均饱和度\t远景带RMS")
    for p in args:
        im = Image.open(p).convert("RGB")
        n = p.replace("\\", "/").split("/")[-1]
        stats(im, n)


if __name__ == "__main__":
    main()
