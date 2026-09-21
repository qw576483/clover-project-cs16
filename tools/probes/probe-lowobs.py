# -*- coding: utf-8 -*-
"""一次性诊断：全图普查「被低矮障碍（顶面 0.3~1.8m）判成阻挡」的格子 —— 这就是
用户说的"扶手/矮墙跳不过去"的候选。同时报出该格近垂直面来自哪个 geo 组。
只读。
"""
import os, struct
from collections import defaultdict, Counter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
BM = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')

data = open(GEO, 'rb').read()
o = [4]


def u32():
    v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v


def f32():
    v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v


u32()
cell, ox, oz, gt, omh, pb, pt = (f32() for _ in range(7))
W, D = u32(), u32()
for _ in range(6):
    f32()
gc = u32()
groups = []
for _ in range(gc):
    name = data[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8', 'replace'); o[0] += 48
    vc, ic = u32(), u32()
    po = o[0]
    verts = [struct.unpack_from('<3f', data, po + 12 * i) for i in range(vc)]
    o[0] += 12 * vc + 8 * vc + 12 * vc
    io = o[0]
    idx = list(struct.unpack_from('<%dI' % ic, data, io)) if ic else []
    o[0] += 4 * ic
    groups.append((name, verts, idx))

d = open(BM, 'rb').read()
w, dd, cc, sc, n = struct.unpack_from('<5I', d, 32)
bits = d[64 + n:64 + n + (w * dd + 7) // 8]


def walk(ix, iz):
    if ix < 0 or iz < 0 or ix >= w or iz >= dd:
        return False
    i = iz * w + ix
    return (bits[i >> 3] & (1 << (i & 7))) != 0


SAMPLE = 0.25
floor = {}
walls = defaultdict(list)   # (ix,iz) -> [(ylo,yhi,group)]
for name, verts, idx in groups:
    for k in range(0, len(idx), 3):
        a, b, c = verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
        L = (nx * nx + ny * ny + nz * nz) ** 0.5 or 1.0
        ny /= L
        xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2]); ys = (a[1], b[1], c[1])
        x0, x1 = min(xs), max(xs); z0, z1 = min(zs), max(zs)
        ns = max(1, int(max(x1 - x0, z1 - z0) / SAMPLE) + 1)
        up = ny >= 0.7
        near = abs(ny) < 0.3
        if not (up or near):
            continue
        for i in range(ns):
            for j in range(ns):
                px = x0 + (x1 - x0) * (i + 0.5) / ns
                pz = z0 + (z1 - z0) * (j + 0.5) / ns
                ix = int((px - ox) // cell); iz = int((pz - oz) // cell)
                if ix < 0 or iz < 0 or ix >= W or iz >= D:
                    continue
                if up:
                    key = (ix, iz); y = min(ys)
                    if key not in floor or floor[key] < y:
                        floor[key] = y
                else:
                    walls[(ix, iz)].append((min(ys), max(ys), name))

# 「低矮障碍」格：被阻挡、且存在一个地面候选 f 使 [f+0.1,f+1.75] 里只有顶面 <= f+1.8 的墙
low = {}
for iz in range(D):
    for ix in range(W):
        if walk(ix, iz):
            continue
        fs = floor.get((ix, iz))
        if not fs:
            continue
        ws = walls.get((ix, iz)) or []
        for f in [fs]:
            lo, hi = f + 0.10, f + 1.75
            hits = [(y1, g) for (y0, y1, g) in ws if y0 < hi and y1 > lo]
            if hits and max(y1 for y1, _ in hits) <= f + 1.8:
                low[(ix, iz)] = (f, max(y1 for y1, _ in hits), hits)
                break

print("全图被阻挡格数=%d；其中「只被顶面 <= 地面+1.8m 的障碍挡住」的格数=%d"
      % (sum(1 for iz in range(D) for ix in range(W) if not walk(ix, iz)), len(low)))

# 按贴图组统计
gc_count = Counter()
for (ix, iz), (f, top, hits) in low.items():
    for _, g in hits:
        gc_count[g] += 1
print("\n按 geo 组统计（该组的面参与挡住了这些格）：")
for g, c in gc_count.most_common():
    print("   %-24s %d" % (g, c))

# 聚类（8 邻域）后报告每簇 bbox
seen = set()
clusters = []
for cellk in low:
    if cellk in seen:
        continue
    stack = [cellk]; seen.add(cellk); members = []
    while stack:
        cur = stack.pop(); members.append(cur)
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                nb = (cur[0] + dx, cur[1] + dz)
                if nb in low and nb not in seen:
                    seen.add(nb); stack.append(nb)
    xs = [m[0] for m in members]; zs = [m[1] for m in members]
    groupset = Counter(g for m in members for _, g in low[m][2])
    heights = [low[m][1] - low[m][0] for m in members]
    clusters.append(dict(n=len(members),
                         x0=ox + min(xs) * cell, x1=ox + (max(xs) + 1) * cell,
                         z0=oz + min(zs) * cell, z1=oz + (max(zs) + 1) * cell,
                         groups=groupset, hmin=min(heights), hmax=max(heights)))

clusters.sort(key=lambda c: -c['n'])
print("\n低矮障碍簇（按格数降序，前 25）：")
for c in clusters[:25]:
    print("  n=%-4d x[%6.1f..%6.1f] z[%6.1f..%6.1f]  障碍高%4.2f..%4.2f  组=%s"
          % (c['n'], c['x0'], c['x1'], c['z0'], c['z1'], c['hmin'], c['hmax'],
             dict(c['groups'].most_common(3))))

# T 出生点附近
print("\nT 出生点 (-11.5, -46.5) 15m 内的低矮障碍格：")
n = 0
for (ix, iz), (f, top, hits) in sorted(low.items()):
    px = ox + (ix + 0.5) * cell; pz = oz + (iz + 0.5) * cell
    if abs(px + 11.5) <= 15 and abs(pz + 46.5) <= 15:
        n += 1
        print("   (%6.1f,%6.1f) 地面 y=%5.2f 障碍顶 y=%5.2f 高=%4.2f 组=%s"
              % (px, pz, f, top, top - f, [g for _, _, g in hits]))
if n == 0:
    print("   （无）")
