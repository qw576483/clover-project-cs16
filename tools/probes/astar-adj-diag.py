# -*- coding: utf-8 -*-
"""判据资产（切片BJ）：离线诊断「AStar 报『无可达路径』的那一对格为什么连不上」。

运行时证据（切片BI，`.ai-tmp/test/bh-hold-plant-log.tsv`）：
    [Warn] [AStar] Find: 无可达路径 from=(79, 96) to=(80, 93)（地图连通性问题？），返回 null   × 21
出处：引擎 `Runtime/Core/AStar.cs:130`（`astar.nopath`，`LogThrottle` 降频）。
本探针**不进 Play**，只用与运行时同源的载体复算同一件事（skill §4.3 能离线判的不许进 Play）：

载体（与运行时逐字同源，⛔ 本探针只读）：
    client/Assets/Resources/MapData/de_dust2.bytes
      头部布局出处 = 引擎 `Runtime/Presentation/MapFormat.cs:56-75`（CLVM v1，HeaderSize=64）：
        [0:4) magic 'CLVM' / [4:6) version u16 / [6:8) flags u16 / [8:16) sceneId u64
        [16:20) cellSize f32 / [20:32) origin(3×f32) / [32:36) width u32 / [36:40) depth u32
        [40:44) colliderCount u32 / [44:48) spawnCount u32 / [48:52) nameLen u32 / [52:64) 保留
        位图起点 = 64 + nameLen，长度 = ceil(W*D/8)
    格 ↔ 世界（`MapFormat.cs:243-252`）：
        ix = FloorToInt((x-Origin.x)/CellSize)   ← ★ 必须 floor（负数向零截断会把图外判成第 0 格）
        格心 = Origin + (i+0.5)*CellSize        （与 `Assets/Editor/MapGen/Dust2GeoData.cs:201-203` 同口径）

判据（每条都给数字）：
    D1 头部：cellSize / origin / W×D / 可走格数
    D2 两格的世界坐标（格心）与 `WalkableAt` 的返回值 —— 用**世界坐标**查（运行时就这么查）
    D3 往返自检：cell_of(center(c)) == c 是否成立（不成立 ⇒ 换算/取整问题）
    D4 两格周边可走矩阵（打印成方格图）
    D5 (79,96) → (80,93) 的 8 邻接**逐步**走：直走逼近 + 对角要求两侧正交格都可走，
       报"第几步断、断在哪个条件"
    D6 连通分量：两格各属哪个分量、分量大小；顺带打印 CT/T 出生点、Bot 目标 (17.5,21.5) 的分量
    D7 结论：(a) 换算错 / (b) 起终点本身不可走 / (c) 位图真不连通 —— 用上面的数字三者取一

用法（幂等，只读）：python tools/probes/astar-adj-diag.py [fromX fromZ toX toZ]
"""
import os
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
MARKERS = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2_markers.bytes')

NEIGHBORS = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))


def read_map(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:4] == b'CLVM', 'not a CloverMap file: %s' % path
    ver, flags = struct.unpack_from('<HH', data, 4)
    cell = struct.unpack_from('<f', data, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', data, 20)
    w, d, cc, sc, nlen = struct.unpack_from('<5I', data, 32)
    o = 64 + nlen
    bits = data[o:o + (w * d + 7) // 8]
    return dict(ver=ver, flags=flags, cell=cell, origin=(ox, oy, oz), w=w, d=d,
                cc=cc, sc=sc, nlen=nlen, bits=bits, bytes=len(data), name=data[64:64 + nlen])


def read_marker_table(path):
    out = {}
    with open(path, 'r', encoding='utf-8') as fh:
        for ln in fh:
            ln = ln.strip()
            if not ln or ln.startswith('#'):
                continue
            p = ln.split()
            if len(p) != 4:
                raise ValueError('%s: bad marker row %r' % (path, ln))
            out.setdefault(p[0], []).append(tuple(float(v) for v in p[1:]))
    return out


M = read_map(MAP)
W, D = M['w'], M['d']
CELL = M['cell']
OX, OY, OZ = M['origin']


def walkable(ix, iz):
    """位图可走查询（idx = iz*W + ix；出处 MapFormat.cs:249-251）。"""
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


def cell_of(x, z):
    """世界 → 格（与 MapFormat.cs:247-248 同口径：floor）。"""
    import math
    return int(math.floor((x - OX) / CELL)), int(math.floor((z - OZ) / CELL))


def center(ix, iz):
    return (OX + (ix + 0.5) * CELL, OZ + (iz + 0.5) * CELL)


def fmt(x, z):
    return '(%.3f, %.3f)' % (x, z)


def walk_dir(a, b):
    """从 a 朝 b 走一步（8 邻接的贪婪步）；返回 (下一步格, 失败原因)。"""
    dx = (b[0] > a[0]) - (b[0] < a[0])
    dz = (b[1] > a[1]) - (b[1] < a[1])
    if dx == 0 and dz == 0:
        return a, ''
    nxt = (a[0] + dx, a[1] + dz)
    if not walkable(*nxt):
        return None, '目标步 %s 本身不可走' % (nxt,)
    if dx != 0 and dz != 0:
        if not walkable(a[0] + dx, a[1]):
            return None, '对角步 %s->%s 的正交侧 %s 不可走' % (a, nxt, (a[0] + dx, a[1]))
        if not walkable(a[0], a[1] + dz):
            return None, '对角步 %s->%s 的正交侧 %s 不可走' % (a, nxt, (a[0], a[1] + dz))
    return nxt, ''


def components():
    """8 邻接连通分量（含对角两侧契约？—— 这里用「对角仅要求目标格可走」的宽口径，
    给出**上界**：若这个宽口径下都不连通，那么 A*（更严）必然不连通）。"""
    comp = {}
    n = 0
    sizes = []
    for iz in range(D):
        for ix in range(W):
            if (ix, iz) in comp or not walkable(ix, iz):
                continue
            stack = [(ix, iz)]
            comp[(ix, iz)] = n
            size = 0
            while stack:
                c = stack.pop()
                size += 1
                for (sx, sz) in NEIGHBORS:
                    nb = (c[0] + sx, c[1] + sz)
                    if nb in comp or not walkable(*nb):
                        continue
                    comp[nb] = n
                    stack.append(nb)
            sizes.append(size)
            n += 1
    return comp, sizes


def strict_reachable(src, dst, limit=200000):
    """按**引擎 A* 的严格移动规则**（对角要求两侧正交格都可走）BFS，判 src 能否到达 dst。
    返回 (可达?, 访问节点数)。"""
    from collections import deque
    seen = {src}
    q = deque([src])
    visited = 0
    while q:
        cur = q.popleft()
        visited += 1
        if cur == dst:
            return True, visited
        if visited > limit:
            return None, visited
        for (sx, sz) in NEIGHBORS:
            nb = (cur[0] + sx, cur[1] + sz)
            if nb in seen or not walkable(*nb):
                continue
            if sx != 0 and sz != 0:
                if not walkable(cur[0] + sx, cur[1]) or not walkable(cur[0], cur[1] + sz):
                    continue
            seen.add(nb)
            q.append(nb)
    return False, visited


def main():
    args = [int(v) for v in sys.argv[1:]]
    if len(args) == 4:
        frm, to = (args[0], args[1]), (args[2], args[3])
    else:
        frm, to = (79, 96), (80, 93)

    print('== astar-adj-diag (offline, read-only) ==')
    print('map file   : %s (%d bytes)' % (os.path.relpath(MAP, ROOT).replace('\\', '/'), M['bytes']))
    print('D1 header  : CLVM v%d flags=0x%04X name=%r nlen=%d colliders=%d spawns=%d'
          % (M['ver'], M['flags'], M['name'].decode('utf-8', 'replace'), M['nlen'], M['cc'], M['sc']))
    nw = sum(1 for iz in range(D) for ix in range(W) if walkable(ix, iz))
    print('D1 bitmap  : %dx%d cell=%.4f m origin=(%.3f, %.3f, %.3f) walkable=%d/%d'
          % (W, D, CELL, OX, OY, OZ, nw, W * D))
    print('')

    print('D2 cells   : from=%s to=%s  (chebyshev=%d)'
          % (frm, to, max(abs(frm[0] - to[0]), abs(frm[1] - to[1]))))
    for tag, c in (('from', frm), ('to', to)):
        cx, cz = center(*c)
        print('   %-4s cell %-9s center world %s  WalkableAt(world)=%s  walkable(cell)=%s'
              % (tag, c, fmt(cx, cz), walkable(*cell_of(cx, cz)), walkable(*c)))
    print('')

    print('D3 roundtrip cell_of(center(c)) == c :')
    bad = []
    for iz in range(0, D, max(1, D // 23)):
        for ix in range(0, W, max(1, W // 23)):
            if cell_of(*center(ix, iz)) != (ix, iz):
                bad.append(((ix, iz), cell_of(*center(ix, iz))))
    for c in (frm, to):
        ok = cell_of(*center(*c)) == c
        print('   %-9s -> %-9s  %s' % (c, cell_of(*center(*c)), 'OK' if ok else 'MISMATCH'))
    print('   grid sweep 24x24 采样: mismatch %d' % len(bad))
    for (c, r) in bad[:6]:
        print('      %s -> %s' % (c, r))
    print('')

    print('D4 walkable matrix around from=%s ("."=walkable "#"=blocked, column=x, row=z):' % (frm,))
    x0, x1 = frm[0] - 5, frm[0] + 6
    z0, z1 = min(frm[1], to[1]) - 3, max(frm[1], to[1]) + 4
    hdr = '      ' + ''.join('%4d' % x for x in range(x0, x1))
    print(hdr)
    for iz in range(z0, z1):
        row = ''.join('   %s' % ('.' if walkable(ix, iz) else '#') for ix in range(x0, x1))
        mark = ''
        if iz == frm[1]:
            mark += ' <- from z'
        if iz == to[1]:
            mark += ' <- to z'
        print('  z%3d%s%s' % (iz, row, mark))
    print('')

    print('D5 8-neighbour greedy walk %s -> %s (engine rule: diagonal needs both side cells walkable):' % (frm, to))
    cur = frm
    step = 0
    while cur != to:
        step += 1
        if step > 32:
            print('   step %2d: 超过 32 步仍未到（异常，已停）' % step)
            break
        nxt, why = walk_dir(cur, to)
        if nxt is None:
            print('   step %2d: %s -> 断在这里：%s' % (step, cur, why))
            break
        print('   step %2d: %s -> %s  (walkable=%s)'
              % (step, cur, nxt, walkable(*nxt)))
        cur = nxt
    if cur == to:
        print('   => 这条贪婪路线**全程可走**（断点不在"这一对格之间的逐步走"，见 D6）')
    print('')

    print('D6 connectivity (8-neighbour):')
    ok_strict, visited = strict_reachable(frm, to)
    print('   strict engine rule BFS %s -> %s : %s (visited %d cells in from-component)'
          % (frm, to, {True: 'REACHABLE', False: 'NOT reachable'}.get(ok_strict, 'budget exceeded'), visited))
    comp, sizes = components()
    cf = comp.get(frm)
    ct = comp.get(to)
    print('   loose (diagonal needs target only) components: total=%d' % len(sizes))
    print('   from %-9s component #%s size=%s' % (frm, cf, sizes[cf] if cf is not None else '-'))
    print('   to   %-9s component #%s size=%s' % (to, ct, sizes[ct] if ct is not None else '-'))
    print('   same component: %s' % (cf == ct))
    top = sorted(range(len(sizes)), key=lambda i: -sizes[i])[:6]
    print('   largest components (id:size): %s' % ', '.join('%d:%d' % (i, sizes[i]) for i in top))
    print('')

    print('D6b reference points (cell -> component -> size):')
    extra = []
    for (name, wpt) in (('bot target (17.5,21.5)', (17.5, 21.5)),
                        ('world (79.5,96.5)', center(*frm)),
                        ('world (80.5,93.5)', center(*to))):
        extra.append((name, cell_of(wpt[0], wpt[1])))
    try:
        mk = read_marker_table(MARKERS)
        for nm in ('Spawn_CT', 'Spawn_T', 'Bombsite_A', 'Bombsite_B'):
            for i, p in enumerate(mk.get(nm, [])[:1]):
                extra.append(('%s[%d]' % (nm, i), cell_of(p[0], p[2])))
    except Exception as e:      # 标记表不在也只是少几行对照
        print('   (markers skipped: %s)' % e)
    for (name, c) in extra:
        cid = comp.get(c)
        print('   %-24s cell %-10s comp #%-4s size=%-8s walkable=%s'
              % (name, c, cid, sizes[cid] if cid is not None else '-', walkable(*c)))
    print('')

    print('D7 conclusion inputs:')
    print('   walkable(from)=%s  walkable(to)=%s  roundtrip ok=%s  same-comp(loose)=%s  strict-reachable=%s'
          % (walkable(*frm), walkable(*to), all(cell_of(*center(*c)) == c for c in (frm, to)),
             cf == ct, ok_strict))
    return 0


if __name__ == '__main__':
    sys.exit(main())
