# -*- coding: utf-8 -*-
"""判据资产（切片BU-R）：**"这个目标点，机器人走得到吗？"**——离线的可达性判据。

为什么需要它（本片根因）：
    片BU 的 L3 证据 `.ai-tmp/test/bs-goal-report.txt` 显示 T 侧 bot 的换目标 100% 由
    `stuck-escalate` 触发（Minh 70 次 / 91.4s），而每个新目标都在 86.68m 外、一次都没走近；
    同时 `CsBotBrain` 选目标用的是 `TryPickFarthestWalkable`（**最远**可走点）且**不判可达**。
    "可走"（位图说这一格能站）与"走得到"（从当前位置求得出路径）是两件事——
    本探针把两者分开算，给出**每个候选目标点是否真的走得到**的数字。

判据口径（与运行时同源，⛔ 不另立一套）：
    1) 移动规则 = 引擎 `AStar` 的规则：8 邻接、**对角要求两侧正交格都可走**
       （`clover-client-unity-engine/Runtime/Core/AStar.cs:114-119`）。
    2) 高度一致性层 = `BotNavigator.BuildHeightReach` 的规则：
       相邻格 `h(下一格) - h(当前格) > CsConst.StepUpHeight(0.45)` ⇒ 这条边不通；
       下降不设限；探不到落脚面 ⇒ 边不通（`BotNavigator.cs:858-889`）。
       离线高度 = `de_dust2_geo.bin` 里该格**最低的可站立三角面** y（与 marker-connectivity.py 的 FLOOR 同源）。
    3) 起点 = T/CT 出生点格（运行时实测位置，见下方 SPAWNS 的出处）。

载体（只读）：de_dust2.bytes / de_dust2_markers.bytes / de_dust2_geo.bin
用法：python tools/probes/bu-goal-reachability.py
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
STEP_UP = 0.45            # CsConst.StepUpHeight
FOUR = ((1, 0), (-1, 0), (0, 1), (0, -1))
EIGHT = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))

# T/CT 出生点格：出处 = 片BR 的运行时 L3 日志（.ai-tmp/test/br-hold-plant-log.tsv 的
# "高度一致性层：从格 (54, 20)…" / "从格 (75, 106)…" 行），即机器人真实站的那一格。
SPAWNS = (('Spawn_T_Minh', 'T', (54, 20)), ('Spawn_T_ZBot', 'T', (53, 22)),
          ('Spawn_CT_Cliffe', 'CT', (75, 106)), ('Spawn_CT_Darrell', 'CT', (67, 97)))


def out(s=''):
    sys.stdout.write(s + '\n')


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


def wp(c):
    """格元组版可走查询（AStar 的 walkable 回调形状）。"""
    return walkable(c[0], c[1])


def center(ix, iz):
    return (OX + (ix + 0.5) * CELL, OZ + (iz + 0.5) * CELL)


FLOOR = {}
for (tx, ty, tz) in G['tri']:
    k = (int((tx - OX) // CELL), int((tz - OZ) // CELL))
    if k not in FLOOR or ty < FLOOR[k]:
        FLOOR[k] = ty


def diag_ok(a, b, pred):
    dx, dz = b[0] - a[0], b[1] - a[1]
    if dx and dz and not (pred((a[0] + dx, a[1])) and pred((a[0], a[1] + dz))):
        return False
    return True


def a_star_component(seed, pred):
    """引擎 AStar 规则下的连通分量（含 seed 的那一块）。"""
    seen = {seed}
    q = deque([seed])
    while q:
        a = q.popleft()
        for dx, dz in EIGHT:
            b = (a[0] + dx, a[1] + dz)
            if b in seen or not pred(b):
                continue
            if not diag_ok(a, b, pred):
                continue
            seen.add(b)
            q.append(b)
    return seen


def height_reach(seed):
    """BotNavigator.BuildHeightReach 的规则（8 邻接 + 抬升 ≤ 0.45 + 下降不限）。"""
    h0 = FLOOR.get(seed)
    if h0 is None:
        return None
    reach = {seed}
    height = {seed: h0}
    q = deque([seed])
    while q:
        cur = q.popleft()
        hc = height[cur]
        for dx, dz in EIGHT:
            n = (cur[0] + dx, cur[1] + dz)
            if n in reach or not walkable(*n):
                continue
            hn = FLOOR.get(n)
            if hn is None or hn - hc > STEP_UP:
                continue
            reach.add(n)
            height[n] = hn
            q.append(n)
    return reach


def main():
    out('== bu-goal-reachability (offline, read-only) ==')
    out('bitmap %dx%d cell=%.3f origin=(%.1f,%.1f)' % (W, D, CELL, OX, OZ))
    total = sum(1 for z in range(D) for x in range(W) if walkable(x, z))
    out('walkable cells    = %d   floor cells = %d' % (total, len(FLOOR)))

    # ── 1. 位图（只判可走，引擎 A* 规则）下的分量 ──
    comp = {}
    sizes = []
    for z in range(D):
        for x in range(W):
            if (x, z) in comp or not walkable(x, z):
                continue
            cid = len(sizes)
            blk = a_star_component((x, z), wp)
            sizes.append(len(blk))
            for c in blk:
                comp[c] = cid
    main = max(range(len(sizes)), key=lambda k: sizes[k])
    out('')
    out('-- 1. bitmap components under the ENGINE A* move rule (8-neigh, diagonal needs both orthogonals) --')
    out('   components = %d   main = #%d size = %d' % (len(sizes), main, sizes[main]))

    # ── 2. 高度一致性层（BotNavigator 规则）下的分量 ──
    hcomp = {}
    hsizes = []
    for seed in list(FLOOR.keys()):
        if seed in hcomp:
            continue
        r = height_reach(seed)
        if r is None:
            continue
        cid = len(hsizes)
        hsizes.append(len(r))
        for c in r:
            hcomp[c] = cid
    hmain = max(range(len(hsizes)), key=lambda k: hsizes[k]) if hsizes else -1
    out('')
    out('-- 2. same, but with the HEIGHT-CONSISTENCY layer of BotNavigator.BuildHeightReach --')
    out('   components = %d   main = #%d size = %d' % (len(hsizes), hmain, hsizes[hmain] if hsizes else 0))

    # ── 3. 出生点 / 各标记点在两个口径下分别是哪一块 ──
    out('')
    out('-- 3. spawn cells: reachable set under each口径 --')
    out('   %-16s %-4s %-9s %-6s %-9s %-6s' % ('spawn', 'team', 'cell', 'bitmap', 'height', 'height#'))
    for nm, team, c in SPAWNS:
        bc = main if comp.get(c) == main else comp.get(c)
        out('   %-16s %-4s (%3d,%3d) %-6s %-9s %-6s' % (
            nm, team, c[0], c[1], 'main' if comp.get(c) == main else 'island',
            len(height_reach(c)) if height_reach(c) else 0,
            'main' if hcomp.get(c) == hmain else 'island'))

    # ── 4. 每个标记点：可走？在哪个位图分量？高度层可达吗？ ──
    out('')
    out('-- 4. marker points: (a) walkable (b) bitmap component (c) height-reachable from each spawn --')
    names = sorted(G['markers'].keys())
    for nm in names:
        pts = G['markers'][nm]
        hit_main = 0
        islands = []
        for p in pts:
            c = cell_of(p[0], p[2])
            if walkable(*c) and comp.get(c) == main:
                hit_main += 1
            else:
                islands.append((c, comp.get(c)))
        out('   %-16s n=%-3d bitmap-main=%-3d island=%s' % (nm, len(pts), hit_main, islands if islands else '-'))

    # ── 5. 本片的关键一问：T 出生点能走到包点 A / B 吗 ──
    out('')
    out('-- 5. THE question of this slice: can a T bot at spawn reach a bombsite? --')
    for nm, team, seed in SPAWNS:
        bitmap_set = a_star_component(seed, wp)
        reach_h = height_reach(seed)
        # 运行时口径 = 高度层 ∩ 位图（A* 规则）；两格都得在这两个口径里
        ok = lambda c: walkable(*c) and c in bitmap_set and (reach_h is None or c in reach_h)
        out('   from %s %s:' % (nm, seed))
        for site in ('Bombsite_A', 'Bombsite_B', 'Route_T_To_A', 'Route_T_To_B', 'Route_CT_To_A', 'Route_CT_To_B'):
            pts = G['markers'].get(site)
            if not pts:
                continue
            good = [cell_of(p[0], p[2]) for p in pts if ok(cell_of(p[0], p[2]))]
            dist = min(math.dist((p[0], p[2]), center(*seed)) for p in pts) if pts else -1
            out('     %-14s pts=%-2d reachable=%-2d  nearest-to-spawn=%.1fm' % (site, len(pts), len(good), dist))
    out('')
    out('RESULT: OK (report produced)')


main()
