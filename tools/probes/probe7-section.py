# -*- coding: utf-8 -*-
"""一次性探针 v7：用 geo.bin 的三角面做**离线射线扫描**，回答「门洞到底有没有留」。
原理：在某个剖面线上，对每个 (x,y) 沿指定轴打一条射线；命中近处三角面 = 该处被几何挡住。
输出 ASCII 剖面（# = 有几何挡住，. = 空），于是"墙 / 门板 / 门洞"一眼可辨。
"""
import struct
import os
from collections import defaultdict

ROOT = r"c:\Work\Server\f-v2\clover-project-cs16"
GEO = os.path.join(ROOT, r"client\Assets\ThirdParty\Dust2\de_dust2_geo.bin")
MAP = os.path.join(ROOT, r"client\Assets\Resources\MapData\de_dust2.bytes")

with open(GEO, 'rb') as f:
    data = f.read()
o = [4]


def u32():
    v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v


def f32():
    v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v


assert data[0:4] == b'CD2G'
u32()
cell = f32(); OX = f32(); OZ = f32()
for _ in range(4):
    f32()
W = u32(); D = u32()
for _ in range(6):
    f32()
gc = u32()
groups = []
for _ in range(gc):
    name = data[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8'); o[0] += 48
    vc = u32(); ic = u32()
    verts = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(vc)]; o[0] += 12 * vc
    o[0] += 8 * vc + 12 * vc
    idx = list(struct.unpack_from('<%dI' % ic, data, o[0])); o[0] += 4 * ic
    groups.append((name, verts, idx))

with open(MAP, 'rb') as f:
    mb = f.read()
mcell = struct.unpack_from('<f', mb, 16)[0]
mox, moy, moz = struct.unpack_from('<3f', mb, 20)
MW, MD, cc, sc, nlen = struct.unpack_from('<5I', mb, 32)
bits = mb[64 + nlen:64 + nlen + (MW * MD + 7) // 8]


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= MW or iz >= MD:
        return False
    i = iz * MW + ix
    return (bits[i >> 3] & (1 << (i & 7))) != 0


def cell_of(x, z):
    return int((x - mox) // mcell), int((z - moz) // mcell)


# ── 射线-三角面求交（Moller-Trumbore）──────────────────────────────────────────
def ray_tri(orig, dirv, a, b, c):
    e1 = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
    e2 = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
    p = (dirv[1] * e2[2] - dirv[2] * e2[1],
         dirv[2] * e2[0] - dirv[0] * e2[2],
         dirv[0] * e2[1] - dirv[1] * e2[0])
    det = e1[0] * p[0] + e1[1] * p[1] + e1[2] * p[2]
    if -1e-9 < det < 1e-9:
        return None
    inv = 1.0 / det
    t0 = (orig[0] - a[0], orig[1] - a[1], orig[2] - a[2])
    u = (t0[0] * p[0] + t0[1] * p[1] + t0[2] * p[2]) * inv
    if u < -1e-6 or u > 1 + 1e-6:
        return None
    q = (t0[1] * e1[2] - t0[2] * e1[1],
         t0[2] * e1[0] - t0[0] * e1[2],
         t0[0] * e1[1] - t0[1] * e1[0])
    v = (dirv[0] * q[0] + dirv[1] * q[1] + dirv[2] * q[2]) * inv
    if v < -1e-6 or u + v > 1 + 1e-6:
        return None
    t = (e2[0] * q[0] + e2[1] * q[1] + e2[2] * q[2]) * inv
    if t <= 1e-4:
        return None
    return t


# 空间索引：按格分桶（每格存落在该格及其邻格的面）
bucket = defaultdict(list)
for gi, (name, V, I) in enumerate(groups):
    for k in range(0, len(I), 3):
        a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
        xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2])
        ix0, iz0 = cell_of(min(xs), min(zs)); ix1, iz1 = cell_of(max(xs), max(zs))
        for iz in range(iz0, iz1 + 1):
            for ix in range(ix0, ix1 + 1):
                bucket[(ix, iz)].append((name, a, b, c))


def cast(orig, dirv, maxdist):
    """返回 (距离, 贴图名) 或 None。"""
    ix, iz = cell_of(orig[0], orig[2])
    best = None
    seen = set()
    for dx in (-1, 0, 1):
        for dz in (-1, 0, 1):
            for rec in bucket.get((ix + dx, iz + dz), ()):
                key = id(rec)
                if key in seen:
                    continue
                seen.add(key)
                name, a, b, c = rec
                t = ray_tri(orig, dirv, a, b, c)
                if t is not None and t <= maxdist and (best is None or t < best[0]):
                    best = (t, name)
    return best


def section(title, axis, fixed, from_, to_, y0, y1, maxdist=1.2, step_y=0.25, step_h=0.5):
    print()
    print("=== %s ===" % title)
    print("  轴=%s 固定=%s  扫描范围 %s ∈ [%.1f, %.1f]  y ∈ [%.1f, %.1f]  最大命中距离 %.2fm"
          % (axis, ('z' if axis.startswith('x') else 'x'), ('x' if axis.startswith('x') else 'z'),
             from_, to_, y0, y1, maxdist))
    dirv = (1.0, 0.0, 0.0) if axis == 'xz' else (0.0, 0.0, 1.0)
    hs = []
    h = from_
    while h <= to_ + 1e-6:
        hs.append(round(h, 2)); h += step_h
    ys = []
    y = y1
    while y >= y0 - 1e-6:
        ys.append(round(y, 2)); y -= step_y
    hdr = '        ' + ''.join(('%-2.0f' % v if (int(v) == v and int(v) % 5 == 0) else '  ') for v in hs)
    print(hdr)
    for y in ys:
        row = []
        for h in hs:
            orig = (h, y, fixed) if axis == 'xz' else (fixed, y, h)
            hit = cast(orig, dirv, maxdist)
            row.append('#' if hit else '.')
        print("  y%+5.2f %s" % (y, ' '.join(row)))


# ── ① 中门所在的 z 剖面（沿 ±z 打射线：能挡住南北通行的是横墙/门板）──
section("中门剖面 z=+12.0（沿 +z 打射线，1.2m 内命中=挡住南北通行）", 'xz', 12.0, -8.0, 16.0, 0.0, 4.0)
section("中门剖面 z=+11.0（同上）", 'xz', 11.0, -8.0, 16.0, 0.0, 4.0)
section("中门剖面 z=+13.0（同上）", 'xz', 13.0, -8.0, 16.0, 0.0, 4.0)

# ── ② B 通：沿 ±x 打射线，看这条南北向走廊的"两侧墙 / 中间挡板"──
section("B通剖面 x=-18.0（沿 +x 打射线，1.2m 内命中=挡住东西通行）", 'zx', -18.0, -40.0, 2.0, 0.0, 4.0, maxdist=1.2, step_h=1.0)
section("B通剖面 x=-14.0（同上）", 'zx', -14.0, -40.0, 2.0, 0.0, 4.0, maxdist=1.2, step_h=1.0)

# ── ③ 可走位图 ASCII（1 格 1 字符，看走廊被切在哪）──
print()
print("=== 可走位图（1 格 = 1m；右=+x 上=+z；# 阻挡 . 可走）x[-42..+2] z[-50..+4] ===")
ix0, iz0 = cell_of(-42.0, -50.0)
ix1, iz1 = cell_of(2.0, 4.0)
print("      " + ''.join(str((i // 10) % 10) if (i % 10 == 0) else ' ' for i in range(ix0, ix1 + 1)))
for iz in range(iz1, iz0 - 1, -1):
    print("z%+5.0f %s" % (moz + iz * mcell, ''.join('#' if not walkable(ix, iz) else '.' for ix in range(ix0, ix1 + 1))))
