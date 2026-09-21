# -*- coding: utf-8 -*-
"""一次性诊断：把可走位图（.bytes）+ geo 地面高度打成 ASCII 图，看清 T 出生点一带的
"楼梯 / 扶手 / 阻挡" 到底是什么。只读。
用法: python tools/probes/probe-bm.py <x0> <z0> <x1> <z1>
"""
import os, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BM = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')

X0 = float(sys.argv[1]); Z0 = float(sys.argv[2]); X1 = float(sys.argv[3]); Z1 = float(sys.argv[4])

d = open(BM, 'rb').read()
w, dd, cc, sc, n = struct.unpack_from('<5I', d, 32)
cell = struct.unpack_from('<f', d, 16)[0]
ox, oy, oz = struct.unpack_from('<3f', d, 20)
bits = d[64 + n:64 + n + (w * dd + 7) // 8]


def walk(ix, iz):
    if ix < 0 or iz < 0 or ix >= w or iz >= dd:
        return None
    i = iz * w + ix
    return (bits[i >> 3] & (1 << (i & 7))) != 0


# geo 地面高度：逐格取"朝上面"的最高 y
data = open(GEO, 'rb').read()
o = [4]


def u32():
    v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v


def f32():
    v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v


u32()
gcell, gox, goz, gt, omh, pb, pt = (f32() for _ in range(7))
GW, GD = u32(), u32()
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

floor = {}
wallhi = {}
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
        ns = max(2, int(max(x1 - x0, z1 - z0) / 0.25) + 1)
        for i in range(ns):
            for j in range(ns):
                px = x0 + (x1 - x0) * (i + 0.5) / ns
                pz = z0 + (z1 - z0) * (j + 0.5) / ns
                ix = int((px - ox) // cell); iz = int((pz - oz) // cell)
                if ix < 0 or iz < 0 or ix >= w or iz >= dd:
                    continue
                if ny >= 0.7:
                    key = (ix, iz)
                    y = min(ys)
                    if key not in floor or floor[key] < y:
                        floor[key] = y
                elif abs(ny) < 0.3:
                    key = (ix, iz)
                    y = max(ys)
                    if key not in wallhi or wallhi[key] < y:
                        wallhi[key] = y

ix0 = int((X0 - ox) // cell); ix1 = int((X1 - ox) // cell)
iz0 = int((Z0 - oz) // cell); iz1 = int((Z1 - oz) // cell)
print("bitmap %dx%d cell=%.2f origin=(%.1f,%.1f)  colliders(header)=%d" % (w, dd, cell, ox, oz, cc))
print("窗口 x[%.0f..%.0f] z[%.0f..%.0f] → ix[%d..%d] iz[%d..%d]" % (X0, X1, Z0, Z1, ix0, ix1, iz0, iz1))
print()
print("图例： . 可走   # 阻挡   ·  图外；每种符号后跟地面高度（十进制，向上取整）")
print("     x轴向右，z 轴向下（z=%d 在上）" % Z1)
hdr = "        " + "".join("%-4d" % (ix % 100) for ix in range(ix0, ix1 + 1))
print(hdr)
for iz in range(iz1, iz0 - 1, -1):
    row = []
    for ix in range(ix0, ix1 + 1):
        wk = walk(ix, iz)
        f = floor.get((ix, iz))
        c = '.' if wk is True else ('#' if wk is False else ' ')
        row.append("%s%-3s" % (c, ('%.0f' % f) if f is not None else '-'))
    print("z%+6.1f %s" % (oz + iz * cell, ''.join(row)))
print()
print("== 每格近垂直面的最高 y（扶手候选：比地面高 0.3~1.8m）==")
for iz in range(iz1, iz0 - 1, -1):
    row = []
    for ix in range(ix0, ix1 + 1):
        f = floor.get((ix, iz)); wh = wallhi.get((ix, iz))
        if wh is None or f is None:
            row.append('  .  ')
        else:
            h = wh - f
            row.append('%4.1f ' % h)
    print("z%+6.1f %s" % (oz + iz * cell, ''.join('%-5s' % s for s in row)))
