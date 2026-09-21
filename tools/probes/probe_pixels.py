#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片T 像素探针：① 视模型屏幕占比 ② 角色(皮肤)区域平均色/方差。
用法: python probe_pixels.py <png> [--crop x0,y0,x1,y1] [--ref x0,y0,x1,y1]"""
import sys, os
from PIL import Image


def lum(p):
    return 0.2126 * p[0] + 0.7152 * p[1] + 0.0722 * p[2]


def stats(im, box):
    x0, y0, x1, y1 = box
    px = im.load()
    n = 0; sr = sg = sb = 0; mn = 999; mx = -1; dark = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            p = px[x, y]; n += 1
            sr += p[0]; sg += p[1]; sb += p[2]
            l = lum(p)
            mn = min(mn, l); mx = max(mx, l)
            if l < 30: dark += 1
    if n == 0: return None
    mr, mg, mb = sr / n, sg / n, sb / n
    # 方差
    v = 0.0
    for y in range(y0, y1):
        for x in range(x0, x1):
            p = px[x, y]; l = lum(p); v += (l - (0.2126*mr + 0.7152*mg + 0.0722*mb)) ** 2
    return dict(n=n, mean=(round(mr), round(mg), round(mb)), lumin_mean=round(0.2126*mr+0.7152*mg+0.0722*mb, 2),
                min=round(mn, 1), max=round(mx, 1), var=round(v/n, 1), dark_pct=round(100.0*dark/n, 2))


def viewmodel_bbox(im, thresh=95):
    """右下象限里"暗(非沙墙/地面)"像素的 bbox + 占比。"""
    w, h = im.size
    px = im.load()
    x0, y0 = int(w * 0.42), int(h * 0.42)
    minx, miny, maxx, maxy = w, h, -1, -1
    cnt = 0
    for y in range(y0, h):
        for x in range(x0, w):
            p = px[x, y]
            if lum(p) < thresh:
                cnt += 1
                minx = min(minx, x); maxx = max(maxx, x)
                miny = min(miny, y); maxy = max(maxy, y)
    if maxx < 0: return None
    bw = maxx - minx + 1; bh = maxy - miny + 1
    return dict(bbox=(minx, miny, maxx, maxy), w_pct=round(100.0 * bw / w, 1), h_pct=round(100.0 * bh / h, 1),
                area_pct=round(100.0 * cnt / (w * h), 2), screen=(w, h), thresh=thresh)


def main():
    path = sys.argv[1]
    im = Image.open(path).convert('RGB')
    print(f"# {path}  size={im.size}")
    print(f"  全屏: {stats(im, (0, 0, im.size[0], im.size[1]))}")
    print(f"  视模型(右下暗像素): {viewmodel_bbox(im)}")
    args = sys.argv[2:]
    i = 0
    while i < len(args):
        if args[i] == '--crop':
            b = tuple(int(v) for v in args[i+1].split(','))
            print(f"  crop {b}: {stats(im, b)}")
            im.crop(b).save(os.path.join(os.path.dirname(path), '_crop_' + os.path.basename(path)))
            i += 2
        elif args[i] == '--ref':
            b = tuple(int(v) for v in args[i+1].split(','))
            print(f"  ref  {b}: {stats(im, b)}")
            i += 2
        else:
            i += 1


if __name__ == '__main__':
    main()
