# -*- coding: utf-8 -*-
"""判据资产（切片BJ）之四：2D 路径的**地面高度剖面** —— 位图是单层 2D，路径可能跨层。

背景（切片BJ 的实测链条）：
    ① 修前：`[AStar] 无可达路径 from=(79,96) to=(80,93)` ×21 + `[Bot] 求路径失败` ×30
       ⇒ 目标点落在**位图孤立分量**里（离线：46 个连通分量，主分量 4393/5312 格）。
    ② 本片修法（BotNavigator 跳过/排掉"可走但走不到"的点）之后，一次 Play 实测：
       `求路径失败` 从 30 → **0**、`可走但走不到`（新 Warn）219 条 ⇒ A* 这一层不再失败。
    ③ 但机器人**仍然几乎不动**：每回合内位移 ≤ ~5.8m，CT 进点仍 0。
    ⇒ 说明阻塞点下沉到了下一层：**引擎位图是单层 2D**（`CsMap` 注释：水平阻挡走位图、高度走 MeshCollider），
      2D 上连通的路径在**真实几何**上可能要爬 5.7m 的落差（T 家/CT 家/高台互差 ~5.7m）⇒ 走不上去。

本探针用**离线**数字判这一层：对若干条"出生点 → 包点"的 2D 最短路径，逐格取 geo.bin 的地面高度
（口径同 bot-path-check.py：朝上三角面的最小 y），报**相邻两步的高度差**、最大台阶、以及超过
`CsConst` 一步台阶上限的格数。判据：若最大高度差 ≫ 一步台阶上限 ⇒ 2D 路径确实跨层（地图数据/单层位图问题，
⛔ 本片不改地图数据，只登记 + 给方案）。

用法：python tools/probes/path-floor-profile.py
"""
import math
import os
import struct
import sys
from heapq import heappush, heappop

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
MARKERS = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2_markers.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
NEIGHBORS = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
COST_STRAIGHT, COST_DIAGONAL, MAX_NODES, SNAP_R = 10, 14, 20000, 2
STEP_LIMIT_M = 0.5     # 对照：一步台阶上限（CsConst 量级；本探针只做"跨层"的粗判，不用它下结论）


def read_map(path):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CLVM'
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    w, dep, cc, sc, nlen = struct.unpack_from('<5I', d, 32)
    o = 64 + nlen
    return dict(cell=cell, origin=(ox, oy, oz), w=w, d=dep, bits=d[o:o + (w * dep + 7) // 8])


def read_geo(path):
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

    u32(); cs = f32(); ox = f32(); oz = f32()
    ground = f32(); obst = f32(); pb = f32(); pt = f32()
    u32(); u32()
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
    return dict(tri=tri, cell=cs, ox=ox, oz=oz, ground=ground, probe=(pb, pt))


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
G = read_geo(GEO)
W, D = M['w'], M['d']
CELL = M['cell']
OX, OY, OZ = M['origin']
print('== path-floor-profile (offline, read-only) ==')
print('bitmap %dx%d cell=%.3f origin=(%.1f,%.1f) ; geo.bin cell=%.3f origin=(%.1f,%.1f) probe=(%.2f..%.2f) ground=%.2f'
      % (W, D, CELL, OX, OZ, G['cell'], G['ox'], G['oz'], G['probe'][0], G['probe'][1], G['ground']))


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


def cell_of(x, z):
    return int(math.floor((x - OX) / CELL)), int(math.floor((z - OZ) / CELL))


def center(ix, iz):
    return (OX + (ix + 0.5) * CELL, OZ + (iz + 0.5) * CELL)


FLOOR = {}
for (tx, ty, tz) in G['tri']:
    k = (int((tx - OX) // CELL), int((tz - OZ) // CELL))
    if k not in FLOOR or ty < FLOOR[k]:
        FLOOR[k] = ty


def snap(cell):
    if walkable(*cell):
        return cell
    for r in range(1, SNAP_R + 1):
        for dz in range(-r, r + 1):
            for dx in range(-r, r + 1):
                if abs(dx) != r and abs(dz) != r:
                    continue
                c = (cell[0] + dx, cell[1] + dz)
                if walkable(*c):
                    return c
    return None


def heuristic(a, b):
    dx = abs(a[0] - b[0]); dy = abs(a[1] - b[1])
    return COST_STRAIGHT * max(dx, dy) + (COST_DIAGONAL - COST_STRAIGHT) * min(dx, dy)


def find(frm, to):
    if not walkable(*frm) or not walkable(*to):
        return None
    if frm == to:
        return [frm]
    g = {frm: 0}; came = {}; closed = set(); q = []
    heappush(q, (heuristic(frm, to), 0, frm))
    n = 0
    while q:
        _, _, cur = heappop(q)
        if cur in closed:
            continue
        closed.add(cur)
        if cur == to:
            path = [to]
            while path[-1] != frm:
                path.append(came[path[-1]])
            path.reverse()
            return path
        n += 1
        if n > MAX_NODES:
            return None
        for (sx, sy) in NEIGHBORS:
            nb = (cur[0] + sx, cur[1] + sy)
            if nb in closed or not walkable(*nb):
                continue
            if sx != 0 and sy != 0:
                if not walkable(cur[0] + sx, cur[1]) or not walkable(cur[0], cur[1] + sy):
                    continue
            t = g[cur] + (COST_DIAGONAL if (sx and sy) else COST_STRAIGHT)
            if nb in g and t >= g[nb]:
                continue
            g[nb] = t
            came[nb] = cur
            heappush(q, (t + heuristic(nb, to), t, nb))
    return None


RT = read_marker_table(MARKERS)


def first(name):
    pts = RT.get(name, [])
    return pts[0] if pts else None


def nearest(name, to_world):
    pts = RT.get(name, [])
    if not pts:
        return None
    return min(pts, key=lambda p: (p[0] - to_world[0]) ** 2 + (p[2] - to_world[1]) ** 2)


cases = []
for (a, b, lbl) in (('Spawn_CT', 'Bombsite_A', 'CT spawn -> A site'),
                    ('Spawn_CT', 'Bombsite_B', 'CT spawn -> B site'),
                    ('Spawn_T', 'Bombsite_A', 'T spawn -> A site'),
                    ('Spawn_CT', 'Bombsite_A', 'CT spawn -> nearest A pt')):
    pa = first(a)
    if pa is None:
        continue
    pb = nearest(b, (pa[0], pa[2])) if lbl.endswith('nearest A pt') else first(b)
    if pb is None:
        continue
    ca, cb = snap(cell_of(pa[0], pa[2])), snap(cell_of(pb[0], pb[2]))
    cases.append((lbl, a, b, ca, cb))

for (lbl, a, b, ca, cb) in cases:
    if ca is None or cb is None:
        print('%-26s [SKIP] endpoint not snap-able' % lbl)
        continue
    path = find(ca, cb)
    print('')
    print('%-26s %s %s -> %s %s   cells=%s' % (lbl, a, ca, b, cb, len(path) if path else 'NO PATH'))
    if not path:
        continue
    ys = [FLOOR.get(c) for c in path]
    have = [(c, y) for c, y in zip(path, ys) if y is not None]
    steps = []
    for i in range(1, len(path)):
        y0 = FLOOR.get(path[i - 1]); y1 = FLOOR.get(path[i])
        if y0 is None or y1 is None:
            continue
        steps.append((abs(y1 - y0), path[i - 1], path[i], y0, y1))
    steps.sort(reverse=True)
    if not steps:
        print('   (no floor tri along path: geo.bin 没有覆盖这条 2D 路径 ⇒ 无法用高度判)')
        continue
    print('   floor y on path: min=%.2f max=%.2f  span=%.2f m  (cells with floor tri %d/%d)'
          % (min(s[4] for s in steps), max(s[4] for s in steps),
             max(s[4] for s in steps) - min(s[4] for s in steps), len(have), len(path)))
    print('   top 5 height steps (|dy|, from, to, y_from, y_to):')
    for (dy, c0, c1, y0, y1) in steps[:5]:
        print('      %6.2f m  %s(y=%.2f) -> %s(y=%.2f)' % (dy, c0, y0, c1, y1))
    big = [s for s in steps if s[0] > STEP_LIMIT_M]
    _ = big
    print('   steps > %.2f m (step height limit): %d / %d' % (STEP_LIMIT_M, len(big), len(steps)))
print('')
print('marker y vs geo.bin floor y at the same cell (对"目标点在不在可走的那一层"给数字):')
print('   %-18s %-4s %-11s %9s %9s %9s' % ('marker', 'idx', 'cell', 'marker_y', 'floor_y', 'delta'))
for name in ('Spawn_CT', 'Spawn_T', 'Bombsite_A', 'Bombsite_B', 'Route_CT_Mid', 'Route_CT_To_A'):
    for i, p in enumerate(RT.get(name, [])):
        c = cell_of(p[0], p[2])
        fy = FLOOR.get(c)
        print('   %-18s %-4d %-11s %9.2f %9s %9s'
              % (name, i, c, p[1], ('%.2f' % fy) if fy is not None else 'n/a',
                 ('%+.2f' % (p[1] - fy)) if fy is not None else 'n/a'))
    if big:
        print('   => 2D 路径上存在 %d 处超过一步台阶的高度跳变（最大 %.2f m）—— 单层 2D 位图把多层次压平后，'
              '路径会穿过"物理上爬不上去"的层间落差' % (len(big), big[0][0]))
