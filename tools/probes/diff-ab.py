#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片BV 判据资产：A/B 差分（"编辑器叠加层"到底贡献了哪些像素）。

为什么需要它：
  用户报的"喇叭 / 太阳 图标"**只在 Unity 编辑器的 Game view 叠加层里**，游戏自己的后缓冲
  （capture_game_view --source camera|screen）里根本没有 ⇒ 只能对**同一冻结帧**采两张屏幕级
  截图（Gizmos 开 / Gizmos 关），两者的差集就是叠加层。差集必须给**数字**，不能只给两张图让人猜。

口径：
  * 逐像素按通道最大绝对差，> --thr（默认 8/255）算"不同"（低于它的是压缩/抖动噪声）。
  * 输出：不同像素数 / 占比 / 平均绝对差 / 最大绝对差 / 差集包围盒。
  * 差集按 --cell（默认 16px）分块做四连通聚类，按像素数排序列出每簇包围盒（= 每个图标的位置）。
  * --out 写一张放大 4 倍的差集可视化 PNG（红=有差）。

用法：
  python tools/probes/diff-ab.py A.png B.png --tag teamselect --out diff.png [--thr 8] [--cell 16]
"""
import sys
from PIL import Image, ImageDraw

Image.MAX_IMAGE_PIXELS = None


def load(p):
    return Image.open(p).convert("RGB")


def diff(a, b, thr):
    if a.size != b.size:
        raise SystemExit("size mismatch %s vs %s" % (a.size, b.size))
    w, h = a.size
    pa, pb = a.load(), b.load()
    mask = [[False] * w for _ in range(h)]
    n = 0
    s = 0
    mx = 0
    x0, y0, x1, y1 = w, h, -1, -1
    for y in range(h):
        row = mask[y]
        for x in range(w):
            ca, cb = pa[x, y], pb[x, y]
            d = max(abs(ca[0] - cb[0]), abs(ca[1] - cb[1]), abs(ca[2] - cb[2]))
            if d > mx:
                mx = d
            s += d
            if d > thr:
                row[x] = True
                n += 1
                if x < x0: x0 = x
                if y < y0: y0 = y
                if x > x1: x1 = x
                if y > y1: y1 = y
    return mask, n, s / float(w * h), mx, (x0, y0, x1, y1) if n else None, w, h


def clusters(mask, w, h, cell):
    """按 cell 分块 + 四连通 BFS：回每簇的像素数与包围盒。"""
    gw, gh = (w + cell - 1) // cell, (h + cell - 1) // cell
    blocks = [[0] * gw for _ in range(gh)]
    for y in range(h):
        row = mask[y]
        by = y // cell
        for x in range(w):
            if row[x]:
                blocks[by][x // cell] += 1
    seen = [[False] * gw for _ in range(gh)]
    out = []
    for by in range(gh):
        for bx in range(gw):
            if blocks[by][bx] == 0 or seen[by][bx]:
                continue
            stack = [(bx, by)]
            seen[by][bx] = True
            npx = 0
            minx = miny = 10 ** 9
            maxx = maxy = -1
            while stack:
                cx, cy = stack.pop()
                npx += blocks[cy][cx]
                minx = min(minx, cx * cell)
                miny = min(miny, cy * cell)
                maxx = max(maxx, min(w - 1, cx * cell + cell - 1))
                maxy = max(maxy, min(h - 1, cy * cell + cell - 1))
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = cx + dx, cy + dy
                    if 0 <= nx < gw and 0 <= ny < gh and not seen[ny][nx] and blocks[ny][nx] > 0:
                        seen[ny][nx] = True
                        stack.append((nx, ny))
            out.append((npx, (minx, miny, maxx, maxy)))
    out.sort(key=lambda t: -t[0])
    return out


def main():
    a = sys.argv[1:]
    thr = 8
    cell = 16
    tag = ""
    out = None
    files = []
    i = 0
    while i < len(a):
        if a[i] == "--thr":
            thr = int(a[i + 1]); i += 2
        elif a[i] == "--cell":
            cell = int(a[i + 1]); i += 2
        elif a[i] == "--tag":
            tag = a[i + 1]; i += 2
        elif a[i] == "--out":
            out = a[i + 1]; i += 2
        else:
            files.append(a[i]); i += 1
    if len(files) != 2:
        raise SystemExit(__doc__)
    ia, ib = load(files[0]), load(files[1])
    mask, n, mad, mx, bb, w, h = diff(ia, ib, thr)
    print("tag\t%s" % (tag or ""))
    print("A\t%s" % files[0])
    print("B\t%s" % files[1])
    print("size\t%dx%d" % (w, h))
    print("thr\t%d" % thr)
    print("diffPixels\t%d" % n)
    print("diffRatio\t%.5f" % (n / float(w * h)))
    print("meanAbsDiff\t%.4f" % mad)
    print("maxAbsDiff\t%d" % mx)
    if bb:
        print("diffBBox\tx[%d..%d] y[%d..%d] = %dx%d" % (bb[0], bb[2], bb[1], bb[3], bb[2] - bb[0] + 1, bb[3] - bb[1] + 1))
    else:
        print("diffBBox\tnone")
    cl = clusters(mask, w, h, cell)
    print("clusters\t%d" % len(cl))
    for k, (npx, box) in enumerate(cl[:24]):
        print("  cluster[%d]\tpixels=%d\tx[%d..%d] y[%d..%d] = %dx%d" % (
            k, npx, box[0], box[2], box[1], box[3], box[2] - box[0] + 1, box[3] - box[1] + 1))
    if out:
        vis = Image.new("RGB", (w * 2, h * 2), (255, 255, 255))
        vis.paste(ia.resize((w * 2, h * 2), Image.NEAREST), (0, 0))
        d = Image.new("RGB", (w, h), (0, 0, 0))
        dp = d.load()
        for y in range(h):
            row = mask[y]
            for x in range(w):
                if row[x]:
                    dp[x, y] = (255, 40, 40)
        vis.paste(d.resize((w * 2, h * 2), Image.NEAREST), (0, h * 2))
        vis.save(out)
        print("wrote\t%s" % out)


if __name__ == "__main__":
    main()
