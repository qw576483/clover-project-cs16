# -*- coding: utf-8 -*-
"""判据资产（切片BJ）之二：把「AStar 无可达路径」的那一对格里**目标格所在的孤立分量**查清楚。

上游数字（tools/probes/astar-adj-diag.py）：
    from=(79,96) world(16.5,24.5) walkable=True comp#2 size=4393   ← 主分量（CT/T 出生点、A/B 包点都在 #2）
    to  =(80,93) world(17.5,21.5) walkable=True comp#24 size=19     ← **孤立 19 格小口袋**
    往返自检 OK、两格都可走 ⇒ 不是换算错、不是"起终点不可走"，而是**(c) 位图真不连通**。

本探针回答"那 19 格是什么"：
    E1 该分量逐格列出（格号 + 世界格心 + geo.bin 里的地面高度）
    E2 口袋高度 vs 四周主分量的地面高度差 —— 判"它是不是一块**高台**（单层 2D 位图 + 楼梯 ⇒ 2D 不连通但物理可达）"
    E3 有哪些**运行时标记点 / 路线路点**落在这个口袋里（Bot 目标真源 = de_dust2_markers.bytes）
       ⇒ 这决定"取点侧"能不能修（路线点落在孤立分量 = 生成侧漏了连通性检查）
    E4 口袋与主分量之间那层"墙"的厚度（逐格打印），看是**真墙**还是**被阻挡盒封住的门洞**

载体（只读）：client/Assets/Resources/MapData/de_dust2.bytes
              client/Assets/Resources/MapData/de_dust2_markers.bytes（运行时标记表真源）
              client/Assets/ThirdParty/Dust2/de_dust2_geo.bin（**只作对照**：地面高度）
用法：python tools/probes/astar-pocket-diag.py [seedX seedZ]
"""
import math
import os
import struct
import sys
from collections import deque

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
MARKERS = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2_markers.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
NEIGHBORS = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))


def read_map(path):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CLVM'
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    w, dep, cc, sc, nlen = struct.unpack_from('<5I', d, 32)
    o = 64 + nlen
    return dict(cell=cell, origin=(ox, oy, oz), w=w, d=dep, bits=d[o:o + (w * dep + 7) // 8])


def read_geo_floor_tris(path):
    """只取"朝上面"的三角（口径同 bot-path-check.py:198-203），用来算每格地面高度（对照行）。"""
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:4] == b'CD2G'
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]
        o[0] += 4
        return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]
        o[0] += 4
        return v

    u32(); f32(); f32(); f32(); f32(); f32(); f32(); f32(); u32(); u32()
    for _ in range(6):
        f32()
    gc = u32()
    tri = []
    for _ in range(gc):
        o[0] += 48
        vc = u32(); ic = u32()
        verts = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(vc)]
        o[0] += 12 * vc + 8 * vc + 12 * vc
        idx = struct.unpack_from('<%dI' % ic, data, o[0])
        o[0] += 4 * ic
        for k in range(0, len(idx), 3):
            a, b, c = verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]
            ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
            vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            ln = (nx * nx + ny * ny + nz * nz) ** 0.5
            if ln < 1e-9 or ny / ln <= 0.3:
                continue
            tri.append(((a[0] + b[0] + c[0]) / 3.0, (a[1] + b[1] + c[1]) / 3.0, (a[2] + b[2] + c[2]) / 3.0))
    return tri


def read_marker_table(path):
    out = {}
    with open(path, 'r', encoding='utf-8') as fh:
        for ln in fh:
            ln = ln.strip()
            if not ln or ln.startswith('#'):
                continue
            p = ln.split()
            out.setdefault(p[0], []).append(tuple(float(v) for v in p[1:]))
    return out


M = read_map(MAP)
W, D = M['w'], M['d']
CELL = M['cell']
OX, OY, OZ = M['origin']


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


def cell_of(x, z):
    return int(math.floor((x - OX) / CELL)), int(math.floor((z - OZ) / CELL))


def center(ix, iz):
    return (OX + (ix + 0.5) * CELL, OZ + (iz + 0.5) * CELL)


TRI = read_geo_floor_tris(GEO)
FLOOR = {}
for (tx, ty, tz) in TRI:
    k = (int((tx - OX) // CELL), int((tz - OZ) // CELL))
    if k not in FLOOR or ty < FLOOR[k]:
        FLOOR[k] = ty


def comp_of(seed):
    """宽口径 8 邻接分量（对角只要求目标格可走）——与 astar-adj-diag.py D6 同一口径。"""
    seen = {seed}
    q = deque([seed])
    out = []
    while q:
        c = q.popleft()
        out.append(c)
        for (sx, sz) in NEIGHBORS:
            nb = (c[0] + sx, c[1] + sz)
            if nb in seen or not walkable(*nb):
                continue
            seen.add(nb)
            q.append(nb)
    return out


def main():
    args = [int(v) for v in sys.argv[1:]]
    seed = (args[0], args[1]) if len(args) == 2 else (80, 93)
    print('== astar-pocket-diag (offline, read-only) ==')
    print('seed cell %s world (%.2f, %.2f) walkable=%s' % (seed, center(*seed)[0], center(*seed)[1], walkable(*seed)))
    pocket = comp_of(seed)
    print('E1 component of seed: %d cells' % len(pocket))
    xs = [c[0] for c in pocket]
    zs = [c[1] for c in pocket]
    print('   bbox cells x=[%d,%d] z=[%d,%d]  world x=[%.1f,%.1f] z=[%.1f,%.1f]'
          % (min(xs), max(xs), min(zs), max(zs),
             OX + min(xs) * CELL, OX + (max(xs) + 1) * CELL,
             OZ + min(zs) * CELL, OZ + (max(zs) + 1) * CELL))
    ys = [FLOOR.get(c) for c in pocket if c in FLOOR]
    print('   geo.bin ground y of these cells: %s (min=%.2f max=%.2f, %d/%d cells have floor tri)'
          % (sorted(set('%.2f' % v for v in ys))[:12],
             min(ys) if ys else float('nan'), max(ys) if ys else float('nan'), len(ys), len(pocket)))
    for c in sorted(pocket, key=lambda c: (c[1], c[0])):
        print('      cell %-9s world(%7.2f,%7.2f)  floor_y=%s'
              % (c, center(*c)[0], center(*c)[1], ('%.2f' % FLOOR[c]) if c in FLOOR else 'n/a'))
    print('')

    print('E2 pocket heights vs the ring around it (main component cells adjacent to pocket):')
    ring = set()
    for c in pocket:
        for (sx, sz) in NEIGHBORS:
            nb = (c[0] + sx, c[1] + sz)
            if nb not in pocket and walkable(*nb):
                ring.add(nb)
    ry = [FLOOR[n] for n in ring if n in FLOOR]
    py = [FLOOR[c] for c in pocket if c in FLOOR]
    if py and ry:
        print('   pocket y: min=%.2f max=%.2f avg=%.2f  (n=%d)'
              % (min(py), max(py), sum(py) / len(py), len(py)))
        print('   ring   y: min=%.2f max=%.2f avg=%.2f  (n=%d)'
              % (min(ry), max(ry), sum(ry) / len(ry), len(ry)))
        print('   => height delta (pocket - ring) = %+.2f m' % (sum(py) / len(py) - sum(ry) / len(ry)))
    else:
        print('   (no floor tri on one side: pocket n=%d ring n=%d)' % (len(py), len(ry)))
    print('   ring cells (walkable, in main comp, 8-adjacent to pocket): %d -> %s'
          % (len(ring), sorted(ring)[:24]))
    print('')

    print('E3 marker points / route waypoints inside this component (source: %s):'
          % os.path.relpath(MARKERS, ROOT).replace('\\', '/'))
    mk = read_marker_table(MARKERS)
    hit = []
    for name in sorted(mk):
        for i, p in enumerate(mk[name]):
            c = cell_of(p[0], p[2])
            if c in pocket:
                hit.append((name, i, p, c))
    print('   points inside pocket: %d' % len(hit))
    for (name, i, p, c) in hit:
        print('      %-18s [%d] world(%.3f,%.3f,%.3f) cell %s' % (name, i, p[0], p[1], p[2], c))
    print('   (对照) all marker names sorted: %s' % ', '.join(sorted(mk)))
    print('')

    print('E4 the "wall" between pocket and main component (blocked cells touching the pocket):')
    wall = set()
    for c in pocket:
        for (sx, sz) in NEIGHBORS:
            nb = (c[0] + sx, c[1] + sz)
            if nb not in pocket and not walkable(*nb):
                wall.add(nb)
    print('   blocked cells 8-adjacent to pocket: %d' % len(wall))
    wx = sorted(set(c[0] for c in wall))
    wz = sorted(set(c[1] for c in wall))
    print('   wall x range %s ; z range %s' % (wx, wz))
    return 0


if __name__ == '__main__':
    sys.exit(main())
