# -*- coding: utf-8 -*-
"""片BQ 一次性探针：量化 (56,20)<->(45,15) 的阻隔格 + 逐格"为什么不可走"。

只读载体：
  client/Assets/Resources/MapData/de_dust2.bytes       （位图真源）
  client/Assets/ThirdParty/Dust2/de_dust2_geo.bin      （几何：面 y / 法线 / blocker）
用法：python .ai-tmp/test/bq-blockers.py [sx sz tx tz]
"""
import math
import os
import struct
import sys
from collections import deque

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
NB = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))


def read_map(path):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CLVM'
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    w, dep, cc, sc, nlen = struct.unpack_from('<5I', d, 32)
    o = 64 + nlen
    return dict(cell=cell, origin=(ox, oy, oz), w=w, d=dep,
                bits=d[o:o + (w * dep + 7) // 8])


def read_geo(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:4] == b'CD2G'
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v

    u32()
    cell = f32(); ox = f32(); oz = f32(); gt = f32(); omh = f32(); pb = f32(); pt = f32()
    w = u32(); d = u32()
    for _ in range(6):
        f32()
    gc = u32()
    tris = []
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
            tris.append((a, b, c, (0.0, 0.0, 0.0) if ln < 1e-9 else (nx / ln, ny / ln, nz / ln)))
    bc = u32()
    blockers = []
    for _ in range(bc):
        ix0, iz0, ix1, iz1, ymin, ymax = struct.unpack_from('<4I2f', data, o[0])
        o[0] += 24
        blockers.append((ix0, iz0, ix1, iz1, ymin, ymax))
    return dict(cell=cell, ox=ox, oz=oz, gt=gt, omh=omh, pb=pb, pt=pt, w=w, d=d,
                tris=tris, blockers=blockers)


M = read_map(MAP)
G = read_geo(GEO)
W, D, CELL = M['w'], M['d'], M['cell']
OX, OY, OZ = M['origin']
print('bitmap %dx%d cell=%.3f origin=(%.1f,%.1f)  geo probe=[%.2f,%.2f] gt=%.2f omh=%.2f'
      % (W, D, CELL, OX, OZ, G['pb'], G['pt'], G['gt'], G['omh']))
print('geo blockers=%d' % len(G['blockers']))


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


# cell -> 三角面（按质心归格）
TRIS = {}
for (a, b, c, n) in G['tris']:
    tx, ty, tz = (a[0] + b[0] + c[0]) / 3.0, (a[1] + b[1] + c[1]) / 3.0, (a[2] + b[2] + c[2]) / 3.0
    k = (int((tx - OX) // CELL), int((tz - OZ) // CELL))
    TRIS.setdefault(k, []).append((ty, n[1], n[0], n[2]))


def comp_of(seed):
    seen = {seed}
    q = deque([seed])
    out = []
    while q:
        c = q.popleft()
        out.append(c)
        for (sx, sz) in NB:
            nb = (c[0] + sx, c[1] + sz)
            if nb in seen or not walkable(*nb):
                continue
            seen.add(nb)
            q.append(nb)
    return out


def strict_bfs(src, dst):
    """引擎 A* 移动规则下的最短格步数（不可达=None）。"""
    if src == dst:
        return 0
    dist = {src: 0}
    q = deque([src])
    while q:
        cur = q.popleft()
        d0 = dist[cur]
        for (sx, sz) in NB:
            nb = (cur[0] + sx, cur[1] + sz)
            if nb in dist or not walkable(*nb):
                continue
            if sx != 0 and sz != 0:
                if not walkable(cur[0] + sx, cur[1]) or not walkable(cur[0], cur[1] + sz):
                    continue
            if nb == dst:
                return d0 + 1
            dist[nb] = d0 + 1
            q.append(nb)
    return None


args = [int(v) for v in sys.argv[1:]]
S = (args[0], args[1]) if len(args) == 4 else (56, 20)
T = (args[2], args[3]) if len(args) == 4 else (45, 15)
print('\n== S=%s walkable=%s   T=%s walkable=%s ==' % (S, walkable(*S), T, walkable(*T)))
cs = comp_of(S)
ct = comp_of(T)
print('comp(S) size=%d bbox_x=[%d,%d] bbox_z=[%d,%d] world x=[%.1f,%.1f] z=[%.1f,%.1f]'
      % (len(cs), min(c[0] for c in cs), max(c[0] for c in cs), min(c[1] for c in cs), max(c[1] for c in cs),
         OX + min(c[0] for c in cs) * CELL, OX + (max(c[0] for c in cs) + 1) * CELL,
         OZ + min(c[1] for c in cs) * CELL, OZ + (max(c[1] for c in cs) + 1) * CELL))
print('comp(T) size=%d bbox_x=[%d,%d] bbox_z=[%d,%d] world x=[%.1f,%.1f] z=[%.1f,%.1f]'
      % (len(ct), min(c[0] for c in ct), max(c[0] for c in ct), min(c[1] for c in ct), max(c[1] for c in ct),
         OX + min(c[0] for c in ct) * CELL, OX + (max(c[0] for c in ct) + 1) * CELL,
         OZ + min(c[1] for c in ct) * CELL, OZ + (max(c[1] for c in ct) + 1) * CELL))
print('same component = %s' % (set(cs) == set(ct)))
print('strict-BFS dist(S->T) = %s' % strict_bfs(S, T))
print('\ncomp(S) full cell list: %s' % sorted(cs, key=lambda c: (c[1], c[0])))

# 阻隔带：comp(S) 与 comp(T) 之间的阻挡格（8 邻接、非可走）
wall = set()
for c in cs:
    for (sx, sz) in NB:
        nb = (c[0] + sx, c[1] + sz)
        if nb not in cs and not walkable(*nb):
            wall.add(nb)
if wall:
    print('\n== blocked cells 8-adjacent to comp(S): %d ==' % len(wall))
    for c in sorted(wall, key=lambda c: (c[1], c[0])):
        faces = sorted(TRIS.get(c, []), key=lambda f: f[0])
        wxs = sorted(set(b[0] for b in G['blockers'] if b[0] <= c[0] <= b[2] and b[1] <= c[1] <= b[3]))
        print('  cell %-9s world(%7.2f,%7.2f)  faces=%d  blocker_hits=%d' % (c, OX + (c[0] + 0.5) * CELL, OZ + (c[1] + 0.5) * CELL, len(faces), len(wxs)))
        for (ty, ny, nx, nz) in faces:
            print('        y=%7.2f  n=(%.2f,%.2f,%.2f)%s' % (ty, nx, ny, nz, '  <WALL>' if abs(ny) < 0.30 else ('  <FLOOR>' if ny > 0.7 else '')))
        for w in wxs[:6]:
            print('        blocker ix[%d,%d] iz[%d,%d] y=[%.2f,%.2f]' % w)
