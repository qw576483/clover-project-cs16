# -*- coding: utf-8 -*-
"""判据资产（切片BA）：离线判「机器人 A* 路径是否全程可走」。

为什么要这个探针（差异 #77 的判据侧）：
    机器人移动已改为「在可走位图上用引擎 AStar 求路径再沿路径走」。**运行时**没有"路径每一格都站得住"
    这条判据（只有"到没到 / 卡没卡"），而本工程要的是"用数字判、不进 Play"（skill §4.3）。
    所以这里把同一条**契约**独立实现一遍，对若干条代表性 bot 目标求解路径并逐格断言 ——
    它是**判据资产**（独立复算），不是导航实现：运行时那一半在 C# 里走引擎
    `clover-client-unity-engine/Runtime/Core/AStar.cs`（⛔ 业务侧不许自建 A*）。

契约（与引擎 AStar 逐条对齐，出处 `Runtime/Core/AStar.cs:37-42/114-127/236-244`）：
    8 邻接；直走代价 10、斜走 14；**对角要求两侧正交格都可走**；启发式 = octile（与代价同量纲）；
    展开节点上限 20000；不可达 / 入参非法 → None。
    平滑（`FindSmoothed` / `Smooth` / `HasLineOfSight`，出处 `:138-219`）：Bresenham 视线拉直，
    对角步同样要求两侧格可走。

判据（每条路径都要过）：
    A1 path[0]/path[-1] == 起点格/终点格（snap 之后）
    A2 路径**每一格** WalkableAt == true
    A3 相邻两格**步长合规**：切比雪夫距离 == 1；对角步的两侧正交格可走（**逐格路径**判）
    A4 代价 / 长度可打印（不是只有"成功"两个字）
    A5 **平滑后的路径**：相邻两个拐点之间 HasLineOfSight 为真（Engine `Smooth` 的契约：不许穿墙）

端点口径（与运行时逐字一致）：
    引擎 `AStar.Find` 对"起点/终点不可走"直接返回 None ⇒ 运行时 `BotNavigator.EnsurePath` 会先
    `SnapToWalkable`（半径 `CsBotConst.PathSnapRadiusCells` = 2）再求解。本探针走同一口径，
    并把"snap 了几格"打进表里 —— 标记点落在位图阻挡格是**已知的地图标记/单层位图问题**（见下表
    的 marker point walkability 段），不许靠"换一个点"掩盖。

数据来源（都是工程内既有载体，不需要 Unity）：
    client/Assets/Resources/MapData/de_dust2.bytes       可行走位图（CloverMap v1，与服务端同源）
    client/Assets/Resources/MapData/de_dust2_markers.bytes   **运行时**标记表（判定用真源）
    client/Assets/ThirdParty/Dust2/de_dust2_geo.bin      原始采样表（**对照行**）+ 几何（"B 点旋转楼梯"）

标记点真源口径（切片BC 改，差异 #2/#67/#76/#77 的判据侧）：
    运行时 `CsMap.Points(marker)` 读的是 **`Resources/MapData/de_dust2_markers.bytes`**
    （由 `Dust2Builder.ExportMarkerResource` 生成，文本每行 `标记名 x y z`），**不是** geo.bin。
    所以本探针的"marker point walkability"与全部用例端点都改读运行时表 —— 否则判据与被判对象不同源，
    生成侧修好了探针也看不见（这正是切片BB 的原状）。geo.bin 的原始采样表**保留为对照行**：
    左列 = 运行时表（吸附后）/ 右列 = geo.bin（吸附前），一眼能看出生成侧吸附生效了多少点。
    ⛔ geo.bin 是只读采样源，本探针不改它、也不改任何坐标。

用法（幂等，只读）：
    python tools/probes/bot-path-check.py
退出码：0 = 全部断言 PASS；1 = 有 FAIL（每条 FAIL 都给出原因）。
"""

import os
import struct
import sys
from heapq import heappush, heappop

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
MARKERS = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2_markers.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')

MAX_NODES = 20000        # = 引擎 AStar.DefaultMaxNodes（Runtime/Core/AStar.cs:32）
SNAP_R = 2               # = CsBotConst.PathSnapRadiusCells（Module/Bot/CsBotConst.cs）
COST_STRAIGHT = 10
COST_DIAGONAL = 14
NEIGHBORS = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))


# ── 载体解码 ─────────────────────────────────────────────────────────────────
def read_map(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:4] == b'CLVM', 'not a CloverMap file: %s' % path
    cell = struct.unpack_from('<f', data, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', data, 20)
    w, d, cc, sc, nlen = struct.unpack_from('<5I', data, 32)
    o = 64 + nlen
    bits = data[o:o + (w * d + 7) // 8]
    return dict(cell=cell, origin=(ox, oy, oz), w=w, d=d, bits=bits)


def read_geo(path):
    """读 CD2G：标记点表 + 朝上面（用于算每格地面高度）。"""
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:4] == b'CD2G', 'not a Dust2GeoData file: %s' % path
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]
        o[0] += 4
        return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]
        o[0] += 4
        return v

    u32()
    f32(); f32(); f32()          # cellSize / originX / originZ（位图口径以 de_dust2.bytes 为准）
    f32(); f32(); f32(); f32()   # groundTopY / obstacleMinHeight / probeBottomY / probeTopY
    u32(); u32()                 # width / depth
    for _ in range(6):
        f32()
    gc = u32()
    tri_up = []
    for _ in range(gc):
        o[0] += 48
        vc = u32()
        ic = u32()
        verts = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(vc)]
        o[0] += 12 * vc + 8 * vc + 12 * vc
        idx = struct.unpack_from('<%dI' % ic, data, o[0])
        o[0] += 4 * ic
        for k in range(0, len(idx), 3):
            a, b, c = verts[idx[k]], verts[idx[k + 1]], verts[idx[k + 2]]
            ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
            vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
            nx = uy * vz - uz * vy
            ny = uz * vx - ux * vz
            nz = ux * vy - uy * vx
            ln = (nx * nx + ny * ny + nz * nz) ** 0.5
            if ln < 1e-9 or ny / ln <= 0.3:
                continue
            tri_up.append(((a[0] + b[0] + c[0]) / 3.0, (a[1] + b[1] + c[1]) / 3.0,
                           (a[2] + b[2] + c[2]) / 3.0))
    bc = u32()
    o[0] += 24 * bc
    mc = u32()
    markers = {}
    for _ in range(mc):
        name = data[o[0]:o[0] + 32].split(b'\0')[0].decode('utf-8')
        o[0] += 32
        pc = u32()
        markers[name] = [struct.unpack_from('<3f', data, o[0] + 12 * i) for i in range(pc)]
        o[0] += 12 * pc
    return dict(markers=markers, tri_up=tri_up)


def read_marker_table(path):
    """读**运行时**标记表 `de_dust2_markers.bytes`（文本：每行 `标记名 x y z`）。

    格式真源 = `Editor/MapGen/Dust2Builder.cs` 的 `ExportMarkerResource`
    （首行 `# clover cs16 markers v1 (marker x y z) ...`；`F3` 定点 + 不变文化 ⇒ 小数点就是 '.'）。
    解析口径同 `tools/probes/locate-overview-letters.py` 的几何尾部标记段（同名聚成一组）。
    """
    out = {}
    with open(path, 'r', encoding='utf-8') as fh:
        for ln in fh:
            ln = ln.strip()
            if not ln or ln.startswith('#'):
                continue
            parts = ln.split()
            if len(parts) != 4:
                raise ValueError('%s: bad marker row %r' % (path, ln))
            out.setdefault(parts[0], []).append(tuple(float(v) for v in parts[1:]))
    return out


M = read_map(MAP)
RT = read_marker_table(MARKERS)      # ★ 判定真源：运行时表（CsMap.Points 读的就是它）
G = read_geo(GEO)                    # 对照 + 几何：geo.bin（吸附前的原始采样表）
W, D = M['w'], M['d']
CELL = M['cell']
ORIGIN = M['origin']


def walkable(ix, iz):
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


def cell_of(x, z):
    return int((x - ORIGIN[0]) // CELL), int((z - ORIGIN[2]) // CELL)


def center(ix, iz):
    return (ORIGIN[0] + (ix + 0.5) * CELL, ORIGIN[2] + (iz + 0.5) * CELL)


def fmt(v):
    return '-' if v is None else '%.2f' % v


def snap(cell, radius=SNAP_R):
    """把格挪到最近的可走格（逐环扩张；返回 (cell, 挪了几格)）。口径同 BotNavigator.SnapToWalkable。"""
    if walkable(*cell):
        return cell, 0
    for r in range(1, radius + 1):
        for dz in range(-r, r + 1):
            for dx in range(-r, r + 1):
                if abs(dx) != r and abs(dz) != r:
                    continue
                c = (cell[0] + dx, cell[1] + dz)
                if walkable(*c):
                    return c, r
    return None, -1


# 每格地面高度 = 该格内朝上三角面的最小 y（口径同 tools/probes/passage-probe.py 的 levels[0]）
FLOOR = {}
for (tx, ty, tz) in G['tri_up']:
    k = (int((tx - ORIGIN[0]) // CELL), int((tz - ORIGIN[2]) // CELL))
    if k not in FLOOR or ty < FLOOR[k]:
        FLOOR[k] = ty


# ── A*（契约同引擎 CloverEngine.AStar）────────────────────────────────────────
def heuristic(a, b):
    dx = abs(a[0] - b[0])
    dy = abs(a[1] - b[1])
    lo = min(dx, dy)
    hi = max(dx, dy)
    return COST_STRAIGHT * hi + (COST_DIAGONAL - COST_STRAIGHT) * lo


def find(frm, to, max_nodes=MAX_NODES, blocked_why=None):
    """返回 (path, why)；why != '' 表示为什么求不出来。"""
    if not walkable(*frm):
        return None, 'start cell not walkable %s' % (frm,)
    if not walkable(*to):
        return None, 'goal cell not walkable %s' % (to,)
    if frm == to:
        return [frm], ''

    g = {frm: 0}
    came = {}
    closed = set()
    openq = []
    heappush(openq, (heuristic(frm, to), 0, frm))
    expanded = 0
    while openq:
        _, _, cur = heappop(openq)
        if cur in closed:
            continue
        closed.add(cur)
        if cur == to:
            path = [to]
            while path[-1] != frm:
                path.append(came[path[-1]])
            path.reverse()
            return path, ''
        expanded += 1
        if expanded > max_nodes:
            return None, 'expanded > maxNodes=%d' % max_nodes
        gc = g[cur]
        for (sx, sy) in NEIGHBORS:
            nxt = (cur[0] + sx, cur[1] + sy)
            if nxt in closed or not walkable(*nxt):
                continue
            if sx != 0 and sy != 0:
                if not walkable(cur[0] + sx, cur[1]):
                    continue
                if not walkable(cur[0], cur[1] + sy):
                    continue
            t = gc + (COST_DIAGONAL if (sx != 0 and sy != 0) else COST_STRAIGHT)
            if nxt in g and t >= g[nxt]:
                continue
            g[nxt] = t
            came[nxt] = cur
            heappush(openq, (t + heuristic(nxt, to), t, nxt))
    return None, 'no path (connectivity)'


def has_line_of_sight(a, b):
    x, y = a
    dx = abs(b[0] - a[0])
    dy = abs(b[1] - a[1])
    sx = 1 if a[0] < b[0] else -1
    sy = 1 if a[1] < b[1] else -1
    err = dx - dy
    guard = dx + dy + 8
    while guard > 0:
        guard -= 1
        if not walkable(x, y):
            return False
        if (x, y) == b:
            return True
        e2 = 2 * err
        stepx = stepy = False
        if e2 > -dy:
            err -= dy
            x += sx
            stepx = True
        if e2 < dx:
            err += dx
            y += sy
            stepy = True
        if stepx and stepy:
            if not walkable(x - sx, y):
                return False
            if not walkable(x, y - sy):
                return False
    return False


def smooth(path):
    if len(path) <= 2:
        return list(path)
    out = [path[0]]
    anchor = 0
    for i in range(2, len(path)):
        if has_line_of_sight(path[anchor], path[i]):
            continue
        out.append(path[i - 1])
        anchor = i - 1
    if out[-1] != path[-1]:
        out.append(path[-1])
    return out


# ── 断言 ─────────────────────────────────────────────────────────────────────
def check_path(path, frm, to):
    """返回 (fails, cost, meters)。fails = 失败原因列表（空 = 全过）。"""
    fails = []
    if path is None:
        return ['no path'], 0, 0.0
    if path[0] != frm:
        fails.append('A1 start mismatch %s != %s' % (path[0], frm))
    if path[-1] != to:
        fails.append('A1 end mismatch %s != %s' % (path[-1], to))

    cost = 0
    meters = 0.0
    for i, c in enumerate(path):
        if not walkable(*c):
            fails.append('A2 cell %d %s not walkable' % (i, c))
        if i == 0:
            continue
        p = path[i - 1]
        dx = abs(c[0] - p[0])
        dy = abs(c[1] - p[1])
        if max(dx, dy) != 1:
            fails.append('A3 cell %d step %s->%s not 8-neighbour' % (i, p, c))
            continue
        sx = c[0] - p[0]
        sy = c[1] - p[1]
        diag = sx != 0 and sy != 0
        if diag:
            # 与引擎 AStar.cs:114-119 同一口径：**带符号的**两个正交邻格都必须在
            # （用 abs 会把方向丢掉，去查反方向的格子 —— 那会假 FAIL）
            if not walkable(p[0] + sx, p[1]):
                fails.append('A3 cell %d diagonal %s->%s side (%d,%d) blocked'
                             % (i, p, c, p[0] + sx, p[1]))
            if not walkable(p[0], p[1] + sy):
                fails.append('A3 cell %d diagonal %s->%s side (%d,%d) blocked'
                             % (i, p, c, p[0], p[1] + sy))
        cost += COST_DIAGONAL if diag else COST_STRAIGHT
        meters += (2.0 ** 0.5 if diag else 1.0) * CELL
    return fails, cost, meters


def check_smoothed(path, frm, to):
    """平滑路径的判据：端点对得上 + 每格可走 + 相邻两个拐点之间**可直视**（不许穿墙）。"""
    fails = []
    if path[0] != frm:
        fails.append('A1 start mismatch %s != %s' % (path[0], frm))
    if path[-1] != to:
        fails.append('A1 end mismatch %s != %s' % (path[-1], to))
    for i, c in enumerate(path):
        if not walkable(*c):
            fails.append('A2 cell %d %s not walkable' % (i, c))
    for i in range(len(path) - 1):
        if not has_line_of_sight(path[i], path[i + 1]):
            fails.append('A5 no line of sight %s -> %s' % (path[i], path[i + 1]))
    return fails


# ── 用例 ─────────────────────────────────────────────────────────────────────
def first(marker):
    """用例端点 = 运行时口径：读**运行时标记表**（`CsMap.Points` 的真源），不是 geo.bin。"""
    pts = RT.get(marker, [])
    return pts[0] if pts else None


def stair_cases():
    """B 点旋转楼梯的"下 / 上"两端：**从路线标记点自己的高度取**，不猜坐标。

    口径（与本工程 #66 的登记口径一致）：「从 CsMarkers 的路线路点逐格推进到 B 点平台」——
    取每条到 B 的路线**最后一段**：低处那个路点（标记自带 y）= 楼梯下，路线终点（= B 平台上的点，可走）
    = 楼梯上。两个端点的**标记高度差**原样打印（差异 #66 记的"是整片斜楔还是多级台阶"是几何形态问题，
    由 `tools/probes/geom-check.py` / Editor 几何段负责；本探针判的是**这一段能不能走上去**）。
    """
    out = []
    for (name, tag) in (('Route_T_To_B', 'T side'), ('Route_CT_To_B', 'CT side')):
        pts = RT.get(name, [])
        if len(pts) < 2:
            print('[SKIP] %s (marker missing)' % name)
            continue
        lo_p = pts[-2]
        hi_p = pts[-1]
        lo, d0 = snap(cell_of(lo_p[0], lo_p[2]))
        hi, d1 = snap(cell_of(hi_p[0], hi_p[2]))
        if lo is None or hi is None:
            print('[SKIP] %s stairs (endpoint not snap-able within %d cells)' % (tag, SNAP_R))
            continue
        print('  %-7s bottom cell %-8s marker y=%+5.2f  ->  top cell %-8s marker y=%+5.2f'
              % (tag, lo, lo_p[1], hi, hi_p[1]))
        out.append(('B stairs %s up' % tag, lo, hi, 's%d/e%d' % (d0, d1)))
        out.append(('B stairs %s down' % tag, hi, lo, 's%d/e%d' % (d1, d0)))
    return out


def main():
    print('== bot-path-check (offline, read-only) ==')
    print('map: %s' % os.path.relpath(MAP, ROOT).replace('\\', '/'))
    print('bitmap: %dx%d cell=%.3f m origin=(%.1f,%.1f) walkable=%d'
          % (W, D, CELL, ORIGIN[0], ORIGIN[2], sum(1 for i in range(W * D)
             if (M['bits'][i >> 3] & (1 << (i & 7))) != 0)))
    print('')

    print('marker point walkability (points whose cell is walkable / total):')
    print('  runtime = %s' % os.path.relpath(MARKERS, ROOT).replace('\\', '/'))
    print('  geo.bin = %s   (对照：吸附前的原始采样表)' % os.path.relpath(GEO, ROOT).replace('\\', '/'))
    print('  %-16s %-9s %-9s %s' % ('marker', 'runtime', 'geo.bin', 'delta'))
    rt_ok = rt_all = 0
    geo_ok = geo_all = 0
    for name in sorted(set(RT) | set(G['markers'])):
        rp = RT.get(name, [])
        gp = G['markers'].get(name, [])
        r_ok = sum(1 for p in rp if walkable(*cell_of(p[0], p[2])))
        g_ok = sum(1 for p in gp if walkable(*cell_of(p[0], p[2])))
        rt_ok += r_ok
        rt_all += len(rp)
        geo_ok += g_ok
        geo_all += len(gp)
        flag = 'OK' if len(rp) and r_ok == len(rp) else ('FAIL' if len(rp) else 'MISSING')
        print('  %-16s %2d/%-6d %2d/%-6d %+d  %s'
              % (name, r_ok, len(rp), g_ok, len(gp), r_ok - g_ok, flag))
    print('  %-16s %2d/%-6d %2d/%-6d %+d'
          % ('TOTAL', rt_ok, rt_all, geo_ok, geo_all, rt_ok - geo_ok))
    print('')

    # 口径：生成侧 `Dust2Builder.SnapMarkerToWalkable` 的目标就是"表里不再有落阻挡格的点"；
    # 这一格可不可走的判据与运行时同源（这里用烘焙位图，生成侧用 geo 阻挡盒复算的位图 ——
    # 两者同语义，见 Dust2GeoData.BuildBlockedBitmap 的注释）。
    # geo.bin 对照行**不参与**本断言：它是吸附前的原始采样源，本来就允许有落阻挡格的点。
    a6_bad = [(n, i) for n in sorted(RT) for i, p in enumerate(RT[n])
              if not walkable(*cell_of(p[0], p[2]))]
    if a6_bad:
        print('A6 FAIL: runtime marker table has %d point(s) whose cell is blocked:' % len(a6_bad))
        for (n, i) in a6_bad[:24]:
            c = cell_of(RT[n][i][0], RT[n][i][2])
            print('    %-16s [%2d] (%.3f,%.3f,%.3f) cell %s' % (n, i, RT[n][i][0], RT[n][i][1], RT[n][i][2], c))
    else:
        print('A6 PASS: every runtime marker point sits on a walkable cell (%d points)' % rt_all)
    print('')

    cases = []

    def mk(label, m0, m1):
        """用例端点口径 = 运行时口径：标记点 pts[0] → SnapToWalkable(半径 SNAP_R)。"""
        p0 = first(m0)
        p1 = first(m1)
        if p0 is None or p1 is None:
            print('[SKIP] %-20s (marker missing)' % label)
            return
        c0, d0 = snap(cell_of(p0[0], p0[2]))
        c1, d1 = snap(cell_of(p1[0], p1[2]))
        if c0 is None or c1 is None:
            print('[SKIP] %-20s (endpoint not snap-able within %d cells)' % (label, SNAP_R))
            return
        cases.append((label, c0, c1, 's%d/e%d' % (d0, d1)))

    mk('T spawn -> A site', 'Spawn_T', 'Bombsite_A')
    mk('T spawn -> B site', 'Spawn_T', 'Bombsite_B')
    mk('CT spawn -> A site', 'Spawn_CT', 'Bombsite_A')
    mk('CT spawn -> B site', 'Spawn_CT', 'Bombsite_B')
    mk('T spawn -> CT spawn', 'Spawn_T', 'Spawn_CT')

    print('B stairs (B site approach, ends taken from the route markers own altitudes):')
    cases += stair_cases()
    print('')

    print('%-20s %-11s %-11s %5s %5s %8s %7s %-4s %s'
          % ('case', 'from', 'to', 'cells', 'cost', 'meters', 'snap', 'ok', 'reason'))
    print('-' * 104)

    n_fail = len(a6_bad)          # A6（运行时标记表无落阻挡格点）计入总失败数
    for (label, frm, to, sn) in cases:
        raw, why = find(frm, to)
        if raw is None:
            n_fail += 1
            print('%-20s %-11s %-11s %5s %5s %8s %7s %-4s %s'
                  % (label, frm, to, '-', '-', '-', sn, 'FAIL', why))
            continue
        fails, cost, meters = check_path(raw, frm, to)          # 逐格路径：A1/A2/A3 + 代价
        smooth_path = smooth(raw)
        fails += check_smoothed(smooth_path, frm, to)           # 平滑路径：A1/A2/A5
        if fails:
            n_fail += 1
            print('%-20s %-11s %-11s %5d %5d %8.1f %7s %-4s %s'
                  % (label, frm, to, len(raw), cost, meters, sn, 'FAIL', fails[0]))
            for f in fails[1:4]:
                print('%-20s %s' % ('', f))
        else:
            print('%-20s %-11s %-11s %5d %5d %8.1f %7s %-4s %s'
                  % (label, frm, to, len(raw), cost, meters, sn, 'OK',
                     'all cells walkable; smoothed=%d anchors (LOS ok)' % len(smooth_path)))

    print('')
    print('cases=%d fail=%d' % (len(cases), n_fail))
    print('RESULT: %s' % ('PASS' if n_fail == 0 else 'FAIL'))
    return 1 if n_fail else 0


if __name__ == '__main__':
    sys.exit(main())
