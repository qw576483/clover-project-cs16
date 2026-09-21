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


def region_stats(im, box):
    """区域取色（切片Z 加）：给一个像素框，回**平均 RGB** + 平均亮度 + RMS。

    用途：整图统计会被天空/墙/箱子摊平，判「地面石板偏不偏土黄」必须只看地面那一块。
    参数形如 --region slab:260,880,660,1050（切片Z 的机位 w10-final-A 里该框 = 前景石板路）。
    """
    x0, y0, x1, y1 = box
    x0 = max(0, min(im.size[0], x0)); x1 = max(0, min(im.size[0], x1))
    y0 = max(0, min(im.size[1], y0)); y1 = max(0, min(im.size[1], y1))
    px = im.load()
    n = 0; sr = sg = sb = 0; s = 0.0; s2 = 0.0
    for y in range(y0, y1):
        for x in range(x0, x1):
            p = px[x, y]
            l = lum(p)
            sr += p[0]; sg += p[1]; sb += p[2]
            s += l; s2 += l * l
            n += 1
    if n == 0:
        return None
    mr, mg, mb = sr / n, sg / n, sb / n
    mean = s / n
    rms = max(0.0, s2 / n - mean * mean) ** 0.5
    return mr, mg, mb, mean, rms


def main():
    regions = []
    args = []
    a = sys.argv[1:]
    i = 0
    while i < len(a):
        if a[i] == "--region":
            spec = a[i + 1]
            name, coords = spec.split(":", 1)
            regions.append((name, tuple(int(v) for v in coords.split(","))))
            i += 2
        elif a[i].startswith("--"):
            i += 1
        else:
            args.append(a[i]); i += 1
    hdr = "file\twxh\tmeanLum\tp05\tp50\tp95\tRMS对比度\t平均饱和度\t远景带RMS"
    for name, _ in regions:
        hdr += "\t[%s]R\t[%s]G\t[%s]B\t[%s]Lum\t[%s]RMS" % (name, name, name, name, name)
    print(hdr)
    for p in args:
        im = Image.open(p).convert("RGB")
        n = p.replace("\\", "/").split("/")[-1]
        row = []
        import io as _io
        buf = _io.StringIO()
        _stdout = sys.stdout
        sys.stdout = buf
        stats(im, n)
        sys.stdout = _stdout
        row.append(buf.getvalue().rstrip("\n"))
        for _, box in regions:
            r = region_stats(im, box)
            row.append("" if r is None else "%.0f\t%.0f\t%.0f\t%.1f\t%.1f" % r)
        print("\t".join(row))


if __name__ == "__main__":
    main()
