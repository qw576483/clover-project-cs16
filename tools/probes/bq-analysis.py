# -*- coding: utf-8 -*-
"""片BQ 一次性分析：位图连通分量 + 标记点落点 + (56,20)<->(45,15) 的真相 + 逐格几何。

只读载体：
  client/Assets/Resources/MapData/de_dust2.bytes        位图真源
  client/Assets/Resources/MapData/de_dust2_markers.bytes 运行时标记表
  client/Assets/ThirdParty/Dust2/de_dust2_geo.bin        几何（面 y / 法线）
用法：python .ai-tmp/test/bq-analysis.py
"""
import math
import os
import struct
import sys
from collections import deque

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
MK = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2_markers.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
NB = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
STEEP = 0.7      # CsConst.MaxStandableSlopeNormalZ
WALL_NY = 0.30   # 近垂直面


def read_map(p):
    d = open(p, 'rb').read()
    assert d[0:4] == b'CLVM'
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    w, dep, cc, sc, nlen = struct.unpack_from('<5I', d, 32)
    o = 64 + nlen
    return dict(cell=cell, ox=ox, oz=oz, w=w, d=dep, bits=d[o:o + (w * dep + 7) // 8])


def read_geo(p):
    data = open(p, 'rb').read()
    assert data[0:4] == b'CD2G'
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v

    u32()
    f32(); f32(); f32(); f32(); f32(); f32(); f32()
    u32(); u32()
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
            tris.append((verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]))
    return tris


def read_markers(p):
    out = {}
    for ln in open(p, 'r', encoding='utf-8'):
        ln = ln.strip()
        if not ln or ln.startswith('#'):
            continue
        q = ln.split()
        out.setdefault(q[0], []).append(tuple(float(v) for v in q[1:]))
    return out


M = read_map(MAP)
W, D, CELL, OX, OZ = M['w'], M['d'], M['cell'], M['ox'], M['oz']
TRI = read_geo(GEO)


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


# ── 三角面预先归格（用 bbox 归格，保证不丢跨格的三角形）──────────────────────
CELLT = {}
for (a, b, c) in TRI:
    x0 = min(a[0], b[0], c[0]); x1 = max(a[0], b[0], c[0])
    z0 = min(a[2], b[2], c[2]); z1 = max(a[2], b[2], c[2])
    ix0 = int(math.floor((x0 - OX) / CELL)); ix1 = int(math.floor((x1 - OX) / CELL))
    iz0 = int(math.floor((z0 - OZ) / CELL)); iz1 = int(math.floor((z1 - OZ) / CELL))
    for iz in range(iz0, iz1 + 1):
        for ix in range(ix0, ix1 + 1):
            CELLT.setdefault((ix, iz), []).append((a, b, c))


def y_at(t, x, z):
    """三角形在 (x,z) 处的平面高度；|ny| 太小/点不在投影内 ⇒ None。"""
    a, b, c = t
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
    ln = (nx * nx + ny * ny + nz * nz) ** 0.5
    if ln < 1e-9:
        return None, None
    nx, ny, nz = nx / ln, ny / ln, nz / ln
    dx, dz = x - a[0], z - a[2]
    # 重心坐标（XZ 投影）：解 s*u + t*v = d
    det = ux * vz - uz * vx
    if abs(det) < 1e-12:
        return None, None            # XZ 上退化（竖直薄片）
    s = (dx * vz - dz * vx) / det
    t = (ux * dz - uz * dx) / det
    if s < -1e-6 or t < -1e-6 or s + t > 1 + 1e-6:
        return None, None
    if abs(ny) < 1e-6:
        return None, ny
    return a[1] - (nx * dx + nz * dz) / ny, ny


def surfaces(ix, iz, samples=None):
    """该格内各采样点的"面 y 列表"；返回 [(x,z,[(y,ny)...])]。"""
    pts = []
    if samples is None:
        samples = [(0.5, 0.5)]
    for (fx, fz) in samples:
        x = OX + (ix + fx) * CELL
        z = OZ + (iz + fz) * CELL
        ys = []
        for t in CELLT.get((ix, iz), []):
            y, ny = y_at(t, x, z)
            if y is None:
                continue
            ys.append((round(y, 3), round(ny, 3)))
        ys.sort()
        pts.append((x, z, ys))
    return pts


def comp_of(seed, rule='strict'):
    seen = {seed}
    q = deque([seed])
    out = []
    while q:
        cur = q.popleft()
        out.append(cur)
        for (sx, sz) in NB:
            nb = (cur[0] + sx, cur[1] + sz)
            if nb in seen or not walkable(*nb):
                continue
            if rule == 'strict' and sx != 0 and sz != 0:
                if not walkable(cur[0] + sx, cur[1]) or not walkable(cur[0], cur[1] + sz):
                    continue
            seen.add(nb)
            q.append(nb)
    return out


print('== bitmap %dx%d cell=%.3f origin=(%.1f,%.1f) ==' % (W, D, CELL, OX, OZ))
nw = sum(1 for i in range(W * D) if (M['bits'][i >> 3] & (1 << (i & 7))))
print('walkable=%d' % nw)

# ── 1. 严格口径连通分量（引擎 A* 规则）────────────────────────────────────────
COMP = {}
SIZES = []
CELLS = []
for iz in range(D):
    for ix in range(W):
        if (ix, iz) in COMP or not walkable(ix, iz):
            continue
        cid = len(SIZES)
        st = [(ix, iz)]
        COMP[(ix, iz)] = cid
        acc = []
        while st:
            c = st.pop()
            acc.append(c)
            for (sx, sz) in NB:
                nb = (c[0] + sx, c[1] + sz)
                if nb in COMP or not walkable(*nb):
                    continue
                if sx != 0 and sz != 0:
                    if not walkable(c[0] + sx, c[1]) or not walkable(c[0], c[1] + sz):
                        continue
                COMP[nb] = cid
                st.append(nb)
        SIZES.append(len(acc))
        CELLS.append(acc)
order = sorted(range(len(SIZES)), key=lambda i: -SIZES[i])
print('components(strict A* rule)=%d  main=#%d size=%d (%.1f%%)  non-main cells=%d'
      % (len(SIZES), order[0], SIZES[order[0]], 100.0 * SIZES[order[0]] / nw, nw - SIZES[order[0]]))
print('comp sizes (desc): %s' % ', '.join('#%d:%d' % (i, SIZES[i]) for i in order[:14]))
MAIN = order[0]

# ── 2. 关键地点落哪个分量 ────────────────────────────────────────────────────
MKP = read_markers(MK)


def cell_of(x, z):
    return int(math.floor((x - OX) / CELL)), int(math.floor((z - OZ) / CELL))


print('\n== key locations (runtime marker table) ==')
for nm in sorted(MKP):
    for i, p in enumerate(MKP[nm]):
        c = cell_of(p[0], p[2])
        cid = COMP.get(c)
        tag = '' if cid == MAIN else '   <<< NON-MAIN'
        if nm in ('Bombsite_A', 'Bombsite_B', 'Spawn_CT', 'Spawn_T') or cid != MAIN:
            print('  %-16s[%-2d] cell=%-9s comp=#%-3s size=%-5s world=(%.2f,%.2f,%.2f)%s'
                  % (nm, i, c, cid, SIZES[cid] if cid is not None else '-', p[0], p[1], p[2], tag))

# ── 3. 每个非主分量的 bbox / 含哪些标记点 ─────────────────────────────────────
print('\n== non-main components (size>=2) ==')
for cid in order[1:]:
    if SIZES[cid] < 2:
        continue
    cc = CELLS[cid]
    xs = [c[0] for c in cc]; zs = [c[1] for c in cc]
    names = []
    for nm in sorted(MKP):
        for i, p in enumerate(MKP[nm]):
            if cell_of(p[0], p[2]) in COMP and COMP[cell_of(p[0], p[2])] == cid:
                names.append('%s[%d]' % (nm, i))
    print('  #%-3d size=%-4d cell_x=[%d,%d] cell_z=[%d,%d] world x=[%.1f,%.1f] z=[%.1f,%.1f] markers=%s'
          % (cid, SIZES[cid], min(xs), max(xs), min(zs), max(zs),
             OX + min(xs) * CELL, OX + (max(xs) + 1) * CELL, OZ + min(zs) * CELL, OZ + (max(zs) + 1) * CELL,
             ','.join(names) if names else '-'))

# ── 4. (56,20) <-> (45,15) 真相 ─────────────────────────────────────────────
print('\n== (56,20) <-> (45,15) ==')
A, B = (56, 20), (45, 15)
for c in (A, B):
    print('  %s walkable=%s comp=#%s size=%s world=(%.1f,%.1f)'
          % (c, walkable(*c), COMP.get(c), SIZES[COMP[c]] if c in COMP else '-',
             OX + (c[0] + 0.5) * CELL, OZ + (c[1] + 0.5) * CELL))
print('  same component = %s' % (COMP.get(A) == COMP.get(B) and A in COMP))
if COMP.get(A) == COMP.get(B) and A in COMP:
    print('  => 原始位图上【没有】任何阻隔格：两格同属 #%d' % COMP[A])
    print('  => 运行时 A* 失败必然发生在"位图之上"的那一层（高度一致性层 / BotNavigator.BuildHeightReach）')
    # 严格 BFS 距离
    dist = {A: 0}; q = deque([A]); found = None
    while q:
        cur = q.popleft()
        d0 = dist[cur]
        if cur == B:
            found = d0; break
        for (sx, sz) in NB:
            nb = (cur[0] + sx, cur[1] + sz)
            if nb in dist or not walkable(*nb):
                continue
            if sx != 0 and sz != 0:
                if not walkable(cur[0] + sx, cur[1]) or not walkable(cur[0], cur[1] + sz):
                    continue
            dist[nb] = d0 + 1
            q.append(nb)
    print('  strict A* rule BFS dist = %s 格' % found)
    path = []
    c = B
    while c != A:
        path.append(c)
        for (sx, sz) in NB:
            nb = (c[0] + sx, c[1] + sz)
            if nb in dist and dist[nb] == dist[c] - 1:
                c = nb
                break
        else:
            break
    path.append(A)
    print('  one shortest cell path (B->A): %s' % path[::-1])

# ── 5. 几何逐格证据：(45,15) 及其主分量邻居的面 y / 净空 ──────────────────────
print('\n== per-cell geometry: cell (ix,iz) -> surfaces at center (y, ny) ==')
SAMPLES = [(0.1, 0.1), (0.5, 0.5), (0.9, 0.9), (0.1, 0.9), (0.9, 0.1)]


def report(cells, title):
    print('  [%s]' % title)
    for c in cells:
        print('    cell %-9s w=%-5s comp=#%s' % (c, walkable(*c), COMP.get(c)))
        for (x, z, ys) in surfaces(c[0], c[1], SAMPLES):
            stand = [y for (y, ny) in ys if ny >= STEEP]
            f0 = min(stand) if stand else None
            nxt = None
            if f0 is not None:
                above = [y for (y, ny) in ys if y > f0 + 1e-3]
                nxt = min(above) if above else None
            print('      (%.2f,%.2f) faces=%d  y/ny=%s  floor_min=%s  next_above=%s  clearance=%s'
                  % (x, z, len(ys), ys, ('%.2f' % f0) if f0 is not None else '-',
                     ('%.2f' % nxt) if nxt is not None else '-',
                     ('%.2f' % (nxt - f0)) if (f0 is not None and nxt is not None) else '-'))


report([(44, 15), (45, 15), (46, 15), (45, 14), (45, 16), (49, 17), (49, 18), (50, 18)],
       '(45,15) 与其主分量邻居 / 那个 1 格窄口')

# ── 6. 离线复算"高度一致性可达集"（模型 = BotNavigator.GroundYAbove + BuildHeightReach）──
#   GroundYAbove(cell, fromY)：起点 (格心, fromY + StepUpHeight) 向下射 8m ⇒ 命中面 y；无命中 ⇒ NaN
#   边通 ⟺ hN 非 NaN 且 hN − hCur ≤ StepUpHeight(0.45)（Module/Map/CsMap.cs:514-523 同一式）
STEP = 0.45
MAXDROP = 8.0


def ground(cell, fromY):
    x = OX + (cell[0] + 0.5) * CELL
    z = OZ + (cell[1] + 0.5) * CELL
    top = fromY + STEP
    best = None
    for t in CELLT.get(cell, []):
        if t[0][0] == t[1][0] == t[2][0] and t[0][2] == t[1][2] == t[2][2]:
            continue
        y, ny = y_at(t, x, z)
        if y is None:
            continue
        if y <= top + 1e-4 and y >= top - MAXDROP:
            if best is None or y > best:
                best = y
    return best


def height_reach(seed, baseY):
    reached = {}
    q = deque()
    if not walkable(*seed):
        return reached, 0
    reached[seed] = baseY
    q.append(seed)
    blocked = 0
    while q:
        cur = q.popleft()
        hc = reached[cur]
        for (sx, sz) in NB:
            n = (cur[0] + sx, cur[1] + sz)
            if n in reached or not walkable(*n):
                continue
            hN = ground(n, hc)
            if hN is None or hN - hc > STEP:
                blocked += 1
                continue
            reached[n] = hN
            q.append(n)
    return reached, blocked


print('\n== 离线复算高度一致性可达集（模型口径，与 BuildHeightReach 同式）==')
for seedcell, baseY, label in [((56, 20), 3.96, 'Spawn_T[6] 格 (56,20) baseY=3.96'),
                               ((84, 106), -3.15, 'Spawn_CT[0] 格 (84,106) baseY=-3.15')]:
    rr, bl = height_reach(seedcell, baseY)
    print('  from %-38s 可达=%d 格，被高度判死 %d 格；主分量(位图)=4369'
          % (label, len(rr), bl))
    for c in [(45, 15), (45, 14), (49, 17), (49, 18), (50, 18), (97, 101), (29, 105)]:
        print('      %-9s 在可达集内=%s  该格地面=%s' % (c, c in rr, rr.get(c)))

# ── 7. 每个非主分量：地面高度 vs 四周主分量 + 边界阻挡格"为什么不可走" ─────────
FIVE = [(i / 4.0, j / 4.0) for i in range(5) for j in range(5)]


def cell_floor(ix, iz):
    """该格"最低可站立面"（ny ≥ 0.7），取 5x5 采样点里的最小值；无 ⇒ None。"""
    lo = None
    for (x, z, ys) in surfaces(ix, iz, FIVE):
        for (y, ny) in ys:
            if ny >= STEEP and (lo is None or y < lo):
                lo = y
    return lo


def cell_why_blocked(ix, iz):
    """这一格为什么被判阻挡（按生产侧同一条人体高度带规则复算）。"""
    lo = cell_floor(ix, iz)
    if lo is None:
        return '无地面(实心/图外)'
    band_lo, band_hi = lo + 0.10, lo + 1.75
    for (x, z, ys) in surfaces(ix, iz, FIVE):
        for (y, ny) in ys:
            if abs(ny) <= WALL_NY and band_lo <= y <= band_hi:
                return '人体带[%.2f,%.2f]被墙穿过(面 y=%.2f ny=%.2f)' % (band_lo, band_hi, y, ny)
    return '规则说不清(需 Play 内真值)'


print('\n== 非主分量：地面高度 vs 四周主分量 + 边界阻挡格原因 ==')
for cid in order[1:]:
    if SIZES[cid] < 15:
        continue
    cc = CELLS[cid]
    pf = [cell_floor(*c) for c in cc]
    pf = [v for v in pf if v is not None]
    ring = set()
    for c in cc:
        for (sx, sz) in NB:
            nb = (c[0] + sx, c[1] + sz)
            if (nb not in COMP or COMP[nb] != cid) and walkable(*nb):
                ring.add(nb)
    rf = [cell_floor(*n) for n in ring]
    rf = [v for v in rf if v is not None]
    wall = set()
    for c in cc:
        for (sx, sz) in NB:
            nb = (c[0] + sx, c[1] + sz)
            if (nb not in COMP or COMP[nb] != cid) and not walkable(*nb) \
                    and 0 <= nb[0] < W and 0 <= nb[1] < D:
                wall.add(nb)
    print('  #%-3d size=%-4d  分量地面 y=[%s..%s] avg=%s (n=%d)   环上主分量地面 y=[%s..%s] avg=%s (n=%d)'
          % (cid, SIZES[cid],
             ('%.2f' % min(pf)) if pf else '-', ('%.2f' % max(pf)) if pf else '-',
             ('%.2f' % (sum(pf) / len(pf))) if pf else '-', len(pf),
             ('%.2f' % min(rf)) if rf else '-', ('%.2f' % max(rf)) if rf else '-',
             ('%.2f' % (sum(rf) / len(rf))) if rf else '-', len(rf)))
    print('       分量与环的地面高度差 = %s m（Δ>0.45 ⇒ 台阶以上落差，高度层必然判不通）'
          % (('%.2f' % ((sum(pf) / len(pf)) - (sum(rf) / len(rf)))) if (pf and rf) else '-'))
    from collections import Counter
    cnt = Counter()
    samples = []
    for nb in sorted(wall)[:40]:
        w = cell_why_blocked(*nb)
        cnt[w.split('(')[0]] += 1
        samples.append('%s:%s' % (nb, w))
    print('       边界阻挡格 %d 个；原因分布=%s' % (len(wall), dict(cnt)))
    for s in samples[:8]:
        print('          %s' % s)

# ── 8. 决定性判据：**忽略位图**，只用"台阶 ≤0.45 m"规则做可达性 ────────────────
#   若高台在"只有高度规则"下与出生点连通 ⇒ 位图把一片物理可达区切成了岛（甲）；
#   若在高度规则下仍不连通 ⇒ 是真实落差/断崖（乙）。
print('\n== 决定性判据：忽略位图、只按"抬升 ≤ StepUpHeight"扩张 ==')
ALL = [(ix, iz) for iz in range(D) for ix in range(W)]


def height_only_reach(seed, baseY):
    reached = {seed: baseY}
    q = deque([seed])
    while q:
        cur = q.popleft()
        hc = reached[cur]
        for (sx, sz) in NB:
            n = (cur[0] + sx, cur[1] + sz)
            if n in reached or not (0 <= n[0] < W and 0 <= n[1] < D):
                continue
            hN = ground(n, hc)
            if hN is None or hN - hc > STEP:
                continue
            reached[n] = hN
            q.append(n)
    return reached


ho = height_only_reach((56, 20), 3.96)
print('  从 T 出生点格 (56,20)（baseY=3.96）忽略位图扩张 ⇒ 可达 %d 格（地图共 %d 格）' % (len(ho), W * D))
for label, c in [('(45,15) 高台', (45, 15)), ('(49,18) 窄口', (49, 18)), ('(49,17) 窄口', (49, 17)),
                 ('Bombsite_B[5] (36,110)', (36, 110)), ('Bombsite_B[8] (39,116)', (39, 116)),
                 ('BuyZone_CT[2] (81,93)', (81, 93)), ('Spawn_CT[0] (84,106)', (84, 106)),
                 ('Bombsite_A[0] (97,101)', (97, 101))]:
    print('    %-26s 忽略位图可达=%-5s 该格地面=%s' % (label, c in ho,
                                                       ('%.3f' % ho[c]) if c in ho else '-'))
# 非主分量里有多少格在"忽略位图"口径下可达
for cid in order[1:]:
    if SIZES[cid] < 15:
        continue
    hit = sum(1 for c in CELLS[cid] if c in ho)
    print('    分量 #%-3d size=%-4d 忽略位图后可达 %d 格（%.0f%%）' % (cid, SIZES[cid], hit, 100.0 * hit / SIZES[cid]))
# 直接复现"从主分量主路径爬上去"要经过哪些"被位图判死"的格
print('\n  (45,15) 在"忽略位图"口径下可达=%s ⇒ %s'
      % ((45, 15) in ho,
         '位图把它切成了岛（甲方向）' if (45, 15) in ho else '连忽略位图也到不了 ⇒ 真实落差（乙），位图无责'))

# 单独解释 (45,15)：从 (56,20) 那条 BFS 路径上逐格的"落脚高度"
print('\n== (45,15) 为什么被高度层判死（沿位图最短路径逐格算落脚高度）==')
chain = [(56, 20), (55, 19), (54, 18), (53, 18), (52, 18), (51, 18), (50, 18), (49, 18), (49, 17), (49, 16), (48, 15), (47, 15), (46, 15), (45, 15)]
h = 3.96
for i, c in enumerate(chain):
    if i == 0:
        print('  %-9s 起始落脚 y=%.3f' % (c, h))
        continue
    hN = ground(c, h)
    verdict = 'OK' if (hN is not None and hN - h <= STEP) else '边不通'
    print('  %-9s 落脚 y=%-8s Δ=%s  %s' % (c, ('%.3f' % hN) if hN is not None else 'NaN',
                                           ('%+.3f' % (hN - h)) if hN is not None else '-', verdict))
    if hN is None or hN - h > STEP:
        break
    h = hN
