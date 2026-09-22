# -*- coding: utf-8 -*-
"""判据资产（切片BJ）之三：**标记点 / 路线路点的连通性**（判据侧，A6 的兄弟）。

为什么缺这条（本片根因）：
    生成侧 `Dust2Builder.SnapMarkerToWalkable` 只保证"点所在格**可走**"（判据 = `bot-path-check.py` 的 A6），
    **没有**保证"点跟地图主连通分量**连得上**"。于是路线路点可以落在**被位图封死的小口袋**里：
    点可走（A6 绿）但走不到（A* 返回 null）——
    运行时表现就是 `[AStar] 无可达路径 from=(79,96) to=(80,93)`（切片BI 实测 ×21）。
    这是"判据只判可走、不判连通"的**判据缺口**，本探针把它补上（A7）。

判据（只读，幂等）：
    A7a 每个标记点所在格的**连通分量**（宽口径 8 邻接）与分量大小 —— 除主分量外都列出来
    A7b 每条 `Route_*` 的**相邻两个路点**是否**严格可达**（引擎 A* 的移动规则：对角要求两侧正交格可走）
        ⇒ 这就是"路线在地图数据层面断在哪一段"的数字
    A7c 主分量 = 含 CT/T 出生点的那个分量；落在**非主分量**里的点逐条列出（点名 + 格号 + 分量大小）
    A7d 地面高度剖面（geo.bin 对照）：判口袋是不是"高台/房间"（单层 2D 位图 + 楼梯 ⇒ 2D 不连通）

载体（只读）：de_dust2.bytes / de_dust2_markers.bytes / de_dust2_geo.bin
用法：python tools/probes/marker-connectivity.py
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
    bc = u32()
    boxes = []
    for _ in range(bc):
        boxes.append(struct.unpack_from('<6f', data, o[0]))
        o[0] += 24
    mc = u32()
    markers = {}
    for _ in range(mc):
        name = data[o[0]:o[0] + 32].split(b'\0')[0].decode('utf-8')
        o[0] += 32
        pc = u32()
        markers[name] = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(pc)]
        o[0] += 12 * pc
    return dict(tri=tri, boxes=boxes, markers=markers)


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

# ── 连通分量（宽口径：8 邻接，对角只要求目标格可走）─────────────────────────────
COMP = {}
SIZES = []
for iz in range(D):
    for ix in range(W):
        if (ix, iz) in COMP or not walkable(ix, iz):
            continue
        cid = len(SIZES)
        stack = [(ix, iz)]
        COMP[(ix, iz)] = cid
        size = 0
        while stack:
            c = stack.pop()
            size += 1
            for (sx, sz) in NEIGHBORS:
                nb = (c[0] + sx, c[1] + sz)
                if nb in COMP or not walkable(*nb):
                    continue
                COMP[nb] = cid
                stack.append(nb)
        SIZES.append(size)


def strict_reachable(src, dst, limit=400000):
    """引擎 A* 移动规则下的可达性（对角要求两侧正交格都可走）。"""
    if src == dst:
        return True
    seen = {src}
    q = deque([src])
    n = 0
    while q:
        cur = q.popleft()
        n += 1
        if n > limit:
            return None
        for (sx, sz) in NEIGHBORS:
            nb = (cur[0] + sx, cur[1] + sz)
            if nb in seen or not walkable(*nb):
                continue
            if sx != 0 and sz != 0:
                if not walkable(cur[0] + sx, cur[1]) or not walkable(cur[0], cur[1] + sz):
                    continue
            if nb == dst:
                return True
            seen.add(nb)
            q.append(nb)
    return False


def comp_path_distance(src, dst, limit=400000):
    """严格规则下 BFS 的最短格步数（不可达 = None）。"""
    if src == dst:
        return 0
    dist = {src: 0}
    q = deque([src])
    n = 0
    while q:
        cur = q.popleft()
        n += 1
        if n > limit:
            return None
        d0 = dist[cur]
        for (sx, sz) in NEIGHBORS:
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


def main():
    print('== marker-connectivity (offline, read-only) ==')
    RT = read_marker_table(MARKERS)
    nw = sum(1 for i in range(W * D) if (M['bits'][i >> 3] & (1 << (i & 7))))
    print('bitmap %dx%d cell=%.3f origin=(%.1f,%.1f) walkable=%d components=%d'
          % (W, D, CELL, OX, OZ, nw, len(SIZES)))
    order = sorted(range(len(SIZES)), key=lambda i: -SIZES[i])
    print('top components: %s' % ', '.join('#%d:%d' % (i, SIZES[i]) for i in order[:8]))
    print('')

    def pt_comp(p):
        c = cell_of(p[0], p[2])
        return c, COMP.get(c)

    # 主分量 = 含 Spawn_CT / Spawn_T 的分量
    main_ids = set()
    for nm in ('Spawn_CT', 'Spawn_T'):
        for p in RT.get(nm, []):
            c, cid = pt_comp(p)
            if cid is not None:
                main_ids.add(cid)
    print('A7c spawns -> components: %s' % ', '.join(
        '%s[%d] cell=%s comp=#%s size=%s' % (nm, i, cell_of(p[0], p[2]), COMP.get(cell_of(p[0], p[2])),
                                             SIZES[COMP[cell_of(p[0], p[2])]] if cell_of(p[0], p[2]) in COMP else '-')
        for nm in ('Spawn_CT', 'Spawn_T') for i, p in enumerate(RT.get(nm, [])[:2])))
    main = max(main_ids, key=lambda i: SIZES[i]) if main_ids else order[0]
    print('main component = #%d size=%d' % (main, SIZES[main]))
    print('')

    print('A7a/A7c marker points NOT in the main component:')
    bad = []
    for name in sorted(RT):
        for i, p in enumerate(RT[name]):
            c, cid = pt_comp(p)
            if cid != main:
                bad.append((name, i, c, cid, p))
    print('   %-18s %-4s %-11s %-8s %-6s %s' % ('marker', 'idx', 'cell', 'comp', 'size', 'world'))
    for (name, i, c, cid, p) in bad:
        print('   %-18s %-4d %-11s #%-7s %-6s (%.2f, %.2f, %.2f)'
              % (name, i, c, cid, SIZES[cid] if cid is not None else '-', p[0], p[1], p[2]))
    npts = sum(len(v) for v in RT.values())
    print('   total points in non-main components: %d / %d' % (len(bad), npts))
    print('')

    print('A7b route legs (adjacent waypoints must be strictly reachable under the engine A* rule):')
    nleg_fail = 0
    nleg = 0
    for name in sorted(RT):
        if not name.startswith('Route_'):
            continue
        pts = RT[name]
        fails = []
        for i in range(len(pts) - 1):
            a = cell_of(pts[i][0], pts[i][2])
            b = cell_of(pts[i + 1][0], pts[i + 1][2])
            nleg += 1
            ok = strict_reachable(a, b) if (walkable(*a) and walkable(*b)) else False
            if not ok:
                nleg_fail += 1
                fails.append((i, a, b, walkable(*a), walkable(*b),
                              COMP.get(a), COMP.get(b),
                              SIZES[COMP[a]] if a in COMP else '-', SIZES[COMP[b]] if b in COMP else '-'))
        print('   %-18s points=%-3d legs=%-3d FAIL=%d' % (name, len(pts), max(0, len(pts) - 1), len(fails)))
        for (i, a, b, wa, wb, ca, cb, sa, sb) in fails:
            print('      leg %d: %s(walkable=%s comp=#%s size=%s) -> %s(walkable=%s comp=#%s size=%s)'
                  % (i, a, wa, ca, sa, b, wb, cb, sb))
    print('   route legs total=%d broken=%d' % (nleg, nleg_fail))
    print('')

    print('A7d ground-height profile (geo.bin, 对照) through the pocket x=80..84 / z=86..99:')
    print('        ' + ''.join('%8d' % x for x in range(78, 87)))
    for iz in range(86, 100):
        row = ''
        for ix in range(78, 87):
            row += '%8s' % (('%.2f' % FLOOR[(ix, iz)]) if (ix, iz) in FLOOR else '-')
        mark = ' walkable' if any(walkable(ix, iz) for ix in range(78, 87)) else ' blocked '
        print('   z%3d %s %s' % (iz, row, mark))
    return 1 if (len(bad) or nleg_fail) else 0


if __name__ == '__main__':
    sys.exit(main())
