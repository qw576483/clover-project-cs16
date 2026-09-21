# -*- coding: utf-8 -*-
"""一次性探针 v6：
① 列出 geo.bin 的 33 个材质组（名字 / 三角数 / 世界包围盒）—— 找"门/挡板"这种小范围组；
② 打印"中门 / B通"两片区域里，每个被位图判阻挡的格**是被哪些阻挡盒盖住的**（阻挡盒的世界范围）；
③ 打印这两片区域里的竖直面的贴图名与 Y 范围（看有没有薄门板）。
"""
import struct
import os
from collections import defaultdict

ROOT = r"c:\Work\Server\f-v2\clover-project-cs16"
GEO = os.path.join(ROOT, r"client\Assets\ThirdParty\Dust2\de_dust2_geo.bin")
MAP = os.path.join(ROOT, r"client\Assets\Resources\MapData\de_dust2.bytes")


def read_geo(path):
    with open(path, 'rb') as f:
        data = f.read()
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v

    assert data[0:4] == b'CD2G'
    u32()
    cell = f32(); ox = f32(); oz = f32()
    gt = f32(); omh = f32(); pb = f32(); pt = f32()
    w = u32(); d = u32()
    for _ in range(6):
        f32()
    gc = u32(); groups = []
    for _ in range(gc):
        name = data[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8'); o[0] += 48
        vc = u32(); ic = u32()
        verts = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(vc)]; o[0] += 12 * vc
        o[0] += 8 * vc + 12 * vc
        idx = list(struct.unpack_from('<%dI' % ic, data, o[0])); o[0] += 4 * ic
        groups.append((name, verts, idx))
    bc = u32(); blockers = []
    for _ in range(bc):
        ix0 = u32(); iz0 = u32(); ix1 = u32(); iz1 = u32(); ymn = f32(); ymx = f32()
        blockers.append((ix0, iz0, ix1, iz1, ymn, ymx))
    return dict(cell=cell, ox=ox, oz=oz, w=w, d=d, groups=groups, blockers=blockers,
                pb=pb, pt=pt)


def read_map(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:4] == b'CLVM'
    cell = struct.unpack_from('<f', data, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', data, 20)
    w, d, cc, sc, nlen = struct.unpack_from('<5I', data, 32)
    o = 64 + nlen
    bits = data[o:o + (w * d + 7) // 8]
    return dict(cell=cell, origin=(ox, oy, oz), w=w, d=d, bits=bits)


g = read_geo(GEO)
m = read_map(MAP)
W, D = m['w'], m['d']


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (m['bits'][i >> 3] & (1 << (i & 7))) != 0


def cell_of(x, z):
    return int((x - m['origin'][0]) // m['cell']), int((z - m['origin'][2]) // m['cell'])


def world(ix, iz):
    return m['origin'][0] + ix * m['cell'], m['origin'][2] + iz * m['cell']


def tri(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
    L = (nx * nx + ny * ny + nz * nz) ** 0.5
    if L < 1e-9:
        return (0, 0, 0, 0)
    return (nx / L, ny / L, nz / L, L / 2)


print("=== ① geo.bin 的材质组（名字 / 三角数 / 世界包围盒）===")
for i, (name, V, I) in enumerate(g['groups']):
    if not V:
        continue
    xs = [v[0] for v in V]; ys = [v[1] for v in V]; zs = [v[2] for v in V]
    print("  #%2d %-22s tris=%5d  x[%+7.1f,%+7.1f] y[%+6.1f,%+6.1f] z[%+7.1f,%+7.1f]"
          % (i, name, len(I) // 3, min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))

print()
print("=== ② 阻挡盒总量与【薄/小】盒（体积小 = 可能就是门/栏杆刷）===")
small = []
for b in g['blockers']:
    ix0, iz0, ix1, iz1, ymn, ymx = b
    wid = (ix1 - ix0 + 1)
    dep = (iz1 - iz0 + 1)
    hgt = ymx - ymn
    if hgt <= 2.6 and (wid <= 2 or dep <= 2) and hgt >= 1.5 and max(wid, dep) >= 2:
        small.append(b)
print("  共 %d 个阻挡盒；门形候选（高 1.5~2.6m 且有一边 <=2 格）%d 个：" % (len(g['blockers']), len(small)))
for b in small[:30]:
    ix0, iz0, ix1, iz1, ymn, ymx = b
    print("    cell x[%d..%d] z[%d..%d] world x[%+.1f..%+.1f] z[%+.1f..%+.1f]  y[%.2f..%.2f]"
          % (ix0, ix1, iz0, iz1, world(ix0, 0)[0], world(ix1 + 1, 0)[0],
             world(0, iz0)[1], world(0, iz1 + 1)[1], ymn, ymx))


def report_region(title, wx0, wz0, wx1, wz1):
    print()
    print("=== %s  world x[%+.0f..%+.0f] z[%+.0f..%+.0f] ===" % (title, wx0, wx1, wz0, wz1))
    ix0, iz0 = cell_of(wx0, wz0)
    ix1, iz1 = cell_of(wx1, wz1)
    print("  ① 该区域内的阻挡盒（会把这些格烘成不可走）：")
    hit = 0
    for b in g['blockers']:
        bx0, bz0, bx1, bz1, ymn, ymx = b
        if bx1 < ix0 or bx0 > ix1 or bz1 < iz0 or bz0 > iz1:
            continue
        hit += 1
        print("     cells x[%d..%d] z[%d..%d]  world x[%+.1f..%+.1f] z[%+.1f..%+.1f] y[%.2f..%.2f]"
              % (bx0, bx1, bz0, bz1, world(bx0, 0)[0], world(bx1 + 1, 0)[0],
                 world(0, bz0)[1], world(0, bz1 + 1)[1], ymn, ymx))
    if hit == 0:
        print("     （无）")
    print("  ② 该区域竖直面的贴图与 Y 范围（按格聚合）：")
    vtex = defaultdict(lambda: [0, 1e9, -1e9, 1e9, -1e9])
    for (name, V, I) in g['groups']:
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            nx, ny, nz, area = tri(a, b, c)
            if abs(ny) > 0.12 or area < 0.05:
                continue
            cx = (a[0] + b[0] + c[0]) / 3; cz = (a[2] + b[2] + c[2]) / 3
            ix, iz = cell_of(cx, cz)
            if ix < ix0 or ix > ix1 or iz < iz0 or iz > iz1:
                continue
            ys = [a[1], b[1], c[1]]
            rec = vtex[name]
            rec[0] += 1
            rec[1] = min(rec[1], min(ys)); rec[2] = max(rec[2], max(ys))
            rec[3] = min(rec[3], area); rec[4] = max(rec[4], area)
    for name in sorted(vtex, key=lambda n: -vtex[n][0]):
        rec = vtex[name]
        print("     %-22s 竖面 %3d 个  y[%6.2f..%6.2f]  单面面积[%.2f..%.2f]"
              % (name, rec[0], rec[1], rec[2], rec[3], rec[4]))
    print("  ③ 位图：该区域阻挡格（#）分布（每格 1m，右 = +x，上 = +z）")
    for iz in range(iz1, iz0 - 1, -1):
        print("     z%+6.1f %s" % (world(0, iz)[1],
              ''.join('#' if not walkable(ix, iz) else '.' for ix in range(ix0, ix1 + 1))))


report_region("中门（mid 门洞一带）", -6.0, 4.0, 14.0, 16.0)
report_region("B 通（B 隧道一带）", -26.0, -28.0, -6.0, -8.0)
