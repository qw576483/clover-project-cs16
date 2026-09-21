# -*- coding: utf-8 -*-
"""一次性诊断：T 出生点（匪家）一带的几何构成 —— 回答「用户看到的楼梯扶手是什么」。
只读。用法：python tools/probes/probe-tspawn.py [cx cz half]
"""
import os, struct, sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')

CX = float(sys.argv[1]) if len(sys.argv) > 1 else -11.5
CZ = float(sys.argv[2]) if len(sys.argv) > 2 else -46.5
HALF = float(sys.argv[3]) if len(sys.argv) > 3 else 14.0

data = open(GEO, 'rb').read()
o = [4]


def u32():
    v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v


def f32():
    v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v


u32()
cell, ox, oz, gt, omh, pb, pt = (f32() for _ in range(7))
W, D = u32(), u32()
wminx, wminy, wminz, wmaxx, wmaxy, wmaxz = (f32() for _ in range(6))
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

print("geo: cell=%.2f origin=(%.1f,%.1f) bitmap %dx%d  world x[%.1f..%.1f] z[%.1f..%.1f] y[%.1f..%.1f]"
      % (cell, ox, oz, W, D, wminx, wmaxx, wminz, wmaxz, wminy, wmaxy))
print("T 出生点 Unity = (%.1f, %.1f)；观察窗 ±%.0fm" % (CX, CZ, HALF))
print()

# 逐组统计窗口内的三角面
print("%-24s %6s %8s %8s  %s" % ("组名", "tris", "y范围", "法线y", "水平延展(size)"))
hits = []
for name, verts, idx in groups:
    n = 0
    ys = []
    nys = []
    xs = []
    zs = []
    for k in range(0, len(idx), 3):
        a, b, c = verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]
        cx = (a[0] + b[0] + c[0]) / 3
        cz = (a[2] + b[2] + c[2]) / 3
        if abs(cx - CX) > HALF or abs(cz - CZ) > HALF:
            continue
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
        L = (nx * nx + ny * ny + nz * nz) ** 0.5 or 1.0
        n += 1
        ys += [a[1], b[1], c[1]]
        nys.append(abs(ny / L))
        xs += [a[0], b[0], c[0]]
        zs += [a[2], b[2], c[2]]
    if n == 0:
        continue
    hits.append((name, n, min(ys), max(ys), min(nys), max(nys), min(xs), max(xs), min(zs), max(zs)))
    print("%-24s %6d %4.2f..%4.2f  %.2f..%.2f  x[%6.1f..%6.1f] z[%6.1f..%6.1f]"
          % (name, n, min(ys), max(ys), min(nys), max(nys), min(xs), max(xs), min(zs), max(zs)))

print()
print("窗口内三角面总计 = %d" % sum(h[1] for h in hits))

# ---- 找"矮墙/栏杆"候选：面法线近水平、顶面高度在 0.3~1.6m 之间、水平方向细长 ----
print()
print("== 矮墙/栏杆候选（近垂直面，高 0.3~1.8m，水平细长）==")
# 先建一个粗略的"地面高度场"（每组窗口内朝上面的最高 y，按 0.5m 格）
grid = {}
for name, verts, idx in groups:
    for k in range(0, len(idx), 3):
        a, b, c = verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]
        uy = (b[1] - a[1], c[1] - a[1])
        ux, uz = b[0] - a[0], b[2] - a[2]
        vx, vz = c[0] - a[0], c[2] - a[2]
        ny = uz * vx - ux * vz
        if ny <= 0:
            continue
        cx = (a[0] + b[0] + c[0]) / 3; cz = (a[2] + b[2] + c[2]) / 3
        gy = max(a[1], b[1], c[1])
        key = (int(round(cx)), int(round(cz)))
        if key not in grid or grid[key] < gy:
            grid[key] = gy


def floor_near(x, z):
    best = None
    for dx in range(-2, 3):
        for dz in range(-2, 3):
            key = (int(round(x)) + dx, int(round(z)) + dz)
            if key in grid:
                if best is None or (grid[key] > best):
                    best = grid[key]
    return best


for name, verts, idx in groups:
    for k in range(0, len(idx), 3):
        a, b, c = verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]
        cx = (a[0] + b[0] + c[0]) / 3
        cz = (a[2] + b[2] + c[2]) / 3
        if abs(cx - CX) > HALF or abs(cz - CZ) > HALF:
            continue
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
        L = (nx * nx + ny * ny + nz * nz) ** 0.5 or 1.0
        if abs(ny / L) >= 0.3:
            continue
        ylo = min(a[1], b[1], c[1]); yhi = max(a[1], b[1], c[1])
        f = floor_near(cx, cz)
        if f is None:
            continue
        h = yhi - f
        if not (0.25 <= h <= 1.9):
            continue
        span = max(max(a[0], b[0], c[0]) - min(a[0], b[0], c[0]),
                   max(a[2], b[2], c[2]) - min(a[2], b[2], c[2]))
        print("  %-22s tri@(%6.1f,%6.1f) y[%5.2f..%5.2f] 地上高=%4.2f 水平长=%4.2f n=(%.2f,%.2f,%.2f)"
              % (name, cx, cz, ylo, yhi, h, span, nx / L, ny / L, nz / L))
