# -*- coding: utf-8 -*-
"""一次性探针 v5：查"过不去"的三类成因，全部只读。
① 薄竖直板（门/挡板 = 原版 func_door 之类的实体刷，被烘成静态几何/阻挡盒了吗？）
② 走廊里被位图判成阻挡的格（"中门过不去"）
③ 可走格之间"地面高差 > 台阶(0.457m)"或"坡面陡于 45.573°(normal.y < 0.7)"的边（"B通上不去 / 有门槛"）
"""
import struct
import os
from collections import defaultdict, deque

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
    mc = u32(); markers = {}
    for _ in range(mc):
        name = data[o[0]:o[0] + 32].split(b'\0')[0].decode('utf-8'); o[0] += 32
        pc = u32()
        pts = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(pc)]; o[0] += 12 * pc
        markers[name] = pts
    return dict(cell=cell, ox=ox, oz=oz, w=w, d=d, groups=groups, blockers=blockers,
                markers=markers, pb=pb, pt=pt)


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


# ── ① 薄竖直板：竖直三角面里，成对的"薄板"（门刷最典型）──────────────────────────────
# 判据：把竖面三角按"面片所在平面"聚类太复杂 ⇒ 改成按格统计：某格内竖面三角的最高/最低 y，
# 以及该格内竖面三角的 XZ 跨度很小（薄）。
verts_vertical = []           # (name, x, y, z) 竖面三角的三个顶点 + 所属贴图
for (name, V, I) in g['groups']:
    for k in range(0, len(I), 3):
        a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
        nx, ny, nz, area = tri(a, b, c)
        if abs(ny) < 0.12 and area > 0.05:
            verts_vertical.append((name, a, b, c, area))

print("=== ① 竖直三角面总数：%d（面积>0.05）===" % len(verts_vertical))

# 找"薄"竖直结构：先按格聚合竖向面积，再找竖向面积大但水平占地极小的格（= 门板/栅栏）
cell_varea = defaultdict(float)
cell_vspan = {}
for (name, a, b, c, area) in verts_vertical:
    xs = [a[0], b[0], c[0]]; zs = [a[2], b[2], c[2]]; ys = [a[1], b[1], c[1]]
    ix, iz = cell_of((min(xs) + max(xs)) / 2, (min(zs) + max(zs)) / 2)
    cell_varea[(ix, iz)] += area
    span = (max(xs) - min(xs)) + (max(zs) - min(zs))
    key = (ix, iz)
    if key not in cell_vspan:
        cell_vspan[key] = [span, min(ys), max(ys), name]
    else:
        cell_vspan[key][0] = max(cell_vspan[key][0], span)
        cell_vspan[key][1] = min(cell_vspan[key][1], min(ys))
        cell_vspan[key][2] = max(cell_vspan[key][2], max(ys))

# 门板候选：竖向面积 ≥ 2.5 m²、高度 ≥ 1.6m、且水平跨度 ≤ 1.6m（薄）且该格位图**可走**
doors = []
for key, va in cell_varea.items():
    span, ylo, yhi, name = cell_vspan[key]
    if va >= 2.5 and (yhi - ylo) >= 1.6 and span <= 1.6 and walkable(*key):
        doors.append((key, va, ylo, yhi, name))
doors.sort(key=lambda r: -r[1])
print("=== ② 门板候选（竖向面积≥2.5m²、高≥1.6m、水平跨度≤1.6m、位图却说可走）：%d 格 ===" % len(doors))
for (k, va, ylo, yhi, name) in doors[:25]:
    print("   cell(%3d,%3d) world(%+6.1f,%+6.1f) 竖面积=%.1f y=%.2f..%.2f tex=%s"
          % (k[0], k[1], world(*k)[0], world(*k)[1], va, ylo, yhi, name))

# ── ③ 走廊/通道：按格统计"最上面朝上的面"和"最下面朝上的面"，找高差与陡坡 ──────────────
ups = defaultdict(list)
for (name, V, I) in g['groups']:
    for k in range(0, len(I), 3):
        a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
        nx, ny, nz, area = tri(a, b, c)
        if ny <= 0.3:
            continue
        ys = (a[1] + b[1] + c[1]) / 3
        ix, iz = cell_of((a[0] + b[0] + c[0]) / 3, (a[2] + b[2] + c[2]) / 3)
        ups[(ix, iz)].append(ys)
for key in ups:
    ups[key].sort()


def levels(ix, iz):
    return ups.get((ix, iz), [])


# 陡坡格（0.3 < ny < 0.7 ⇒ 45.6°..72.5° 之间的"上不去"面）
steep_cells = set()
for (name, V, I) in g['groups']:
    for k in range(0, len(I), 3):
        a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
        nx, ny, nz, area = tri(a, b, c)
        if 0.3 < ny < 0.7:
            ix, iz = cell_of((a[0] + b[0] + c[0]) / 3, (a[2] + b[2] + c[2]) / 3)
            steep_cells.add((ix, iz))
print()
print("=== ③ 陡于 45.573°（0.3<normal.y<0.7）的面覆盖的格：%d 格 ===" % len(steep_cells))

STEP = 0.457
print()
print("=== ④ 走廊体检：从 T 出生点到 B 点做位图 BFS ===")
spawnT = g['markers'].get('Spawn_T', [])
siteB = g['markers'].get('Bombsite_B', [])
mid = g['markers'].get('Route_T_Mid', [])
if spawnT and siteB:
    sx, sy, sz = spawnT[0]
    start = cell_of(sx, sz)

    def floor_of(key):
        ls = levels(*key)
        return ls[0] if ls else None

    prev = {start: None}
    start_y = floor_of(start)
    q = deque([start])
    target = None
    goal = set()
    for p in siteB:
        goal.add(cell_of(p[0], p[2]))
    while q:
        cur = q.popleft()
        if cur in goal:
            target = cur
            break
        cy = floor_of(cur)
        if cy is None:
            continue
        for dxy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            nk = (cur[0] + dxy[0], cur[1] + dxy[1])
            if nk in prev or not walkable(*nk):
                continue
            ny_ = floor_of(nk)
            if ny_ is None:
                continue
            # 只允许"台阶内"或"下台阶"的相邻（模拟最保守的走法）
            if ny_ - cy > STEP:
                continue
            prev[nk] = cur
            q.append(nk)
    if target is None:
        print("   [X] 用「相邻高差<=0.457m」的规则，从 T 出生点走不到 B 点 -> 通道里存在高于台阶的坎（原版走不上去）")
        # 打印所有"可达前沿"里被高差挡住的边
        blocked_edges = []
        for key in prev:
            cy = floor_of(key)
            for dxy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                nk = (key[0] + dxy[0], key[1] + dxy[1])
                if not walkable(*nk) or nk in prev:
                    continue
                ny_ = floor_of(nk)
                if ny_ is None:
                    continue
                if ny_ - cy > STEP:
                    blocked_edges.append((key, nk, cy, ny_, ny_ - cy))
        blocked_edges.sort(key=lambda e: -e[4])
        print("  被高差挡住的边 %d 条，最高的 15 条：" % len(blocked_edges))
        for (k1, k2, y1, y2, dy) in blocked_edges[:15]:
            print("    world(%+6.1f,%+6.1f)→(%+6.1f,%+6.1f)  dy=%+.2f  y %.2f→%.2f"
                  % (world(*k1)[0], world(*k1)[1], world(*k2)[0], world(*k2)[1], dy, y1, y2))
    else:
        path = []
        cur = target
        while cur:
            path.append(cur); cur = prev[cur]
        path.reverse()
        print("   ✅ 走到 B 点，路径 %d 格；沿途高差 > 0 的跳变（>0.1m）与前 40 格剖面：" % len(path))
        lasty = None
        for i, key in enumerate(path[:40]):
            y = floor_of(key)
            flag = ''
            if lasty is not None and abs(y - lasty) > 0.1:
                flag = '  <-- dy=%+.2f' % (y - lasty)
            if (key[0], key[1]) in steep_cells:
                flag += '  [该格有陡坡面]'
            print("    %2d world(%+6.1f,%+6.1f) floor=%6.2f%s" % (i, world(*key)[0], world(*key)[1], y, flag))
            lasty = y

print()
print("=== ⑤ 中门区域剖面（x -6..+14, z -8..+14）：每格打印 位图/最低地面/最高地面/陡坡面 ===")
ix0, iz0 = cell_of(-6.0, -8.0)
ix1, iz1 = cell_of(14.0, 14.0)
for iz in range(iz1, iz0 - 1, -1):
    row = []
    for ix in range(ix0, ix1 + 1):
        ls = levels(ix, iz)
        w = walkable(ix, iz)
        s = (ix, iz) in steep_cells
        if not ls:
            row.append('  ..   ')
        else:
            row.append('%s%4.1f%4.1f%s' % ('.' if w else '#', ls[0], ls[-1], 'S' if s else ' '))
    print("  z%+6.1f %s" % (world(0, iz)[1], '|'.join(row)))
print("  x 从 %.1f 到 %.1f（每列 1m；格式 = 位图标记.可走#阻挡 + 最低面 + 最高面 + S陡坡面）"
      % (world(ix0, 0)[0], world(ix1, 0)[0]))
