# -*- coding: utf-8 -*-
"""判据资产：**碰撞-几何逐面一致性** —— 把"模型能穿 / 撞空气 / 可钻缝"穷举出来。


`tools/probes/rebuild-blockers.py` 决定位图（可走性唯一事实），运行时 `CsMap.ResolveMove`
的快速分支 = `BitmapClear && GroundWithinStep`（**不做八向几何射线**）⇒ **位图说可走的地方，
人就真的走过去**。所以"模型能穿"只可能是**位图漏判**。

2026-09-21（切片 S）实测：位图判有两处会漏，一处已修、一处是本脚本的常规检查对象：

1. **`∃f` 规则（已修，见 rebuild-blockers 头部 ④）**：位图是**单层 2D**，而地图是多层。
   旧规则"存在某层地面，其人体带里没有墙 ⇒ 整格可走"会让**箱顶 / 楼板 / 岩顶**那一层
   把整格柱判成可走，而玩家实际走在下面那层 ⇒ **穿进去**。现役位图实测 **582 格**如此。
   修法 = 只看**基层**（最低地面候选 ± 一个台阶，`CsConst.StepUpHeight`）。
2. **采样归属盲区（已修）**：旧版按三角面**包围盒**铺点 `(i+0.5)/n`（点不保证在面内），
   XZ 上退化成线的**垂直墙面/薄板**会整片漏采；地面取**整面 min y**（横跨多格的大斜地面，
   每格都拿到同一个"最低点"）。修法 = 按格铺点（含边界带、归属按点坐标）+ 线段光栅化 + 局部 y。

**本脚本的两件事**：① 用**几何**独立重算一遍判据，与**盘上位图**对账（能抓"改了判据但
`.bytes` 没重写"这类漂移）；② 出清单（逐格可点），用户点名的地方必须被指名。

## 判什么（三类）

| 类 | 判据 | 为什么 |
|---|---|---|
| **a 可穿面** | 位图判**可走**的格，其**最低地面候选 f0** 的人体带 `[f0+0.10, f0+1.75]` 里有渲染几何穿过，**且该面顶面高过 `f0+0.45`**（高过一个台阶 ⇒ 迈不过去，只能穿） | 该处有实心几何、位图却说能走 ⇒ 人从那里穿过去 |
| **a′ 上层带被墙穿** | 位图判**可走**的格，其**非基层**（f > f0+0.45）的人体带里有面穿过 | ⛔ **不是缺陷**：单层 2D 位图表达不了多层，属固有限制（头顶楼板/女儿墙与脚下那层无关）；列出来只为数字透明 |
| **b 撞空气** | 位图判**阻挡**的格，**没有任何渲染几何**落在它的 XZ 足迹里（且在图内、挨着可走格） | 撞到看不见的墙（模型与碰撞不符的另一种） |
| **c 缝隙** | 两个**不同的**阻挡盒之间在 XZ 上的净间隙 `PlayerRadius < gap ≤ 1 格`，且间隙里存在**可走格** | 相邻阻挡盒之间的缝够身子钻过去 |

## 口径来源（不写死数字，全从盘上/工程常量取）
* **采样与规则**：直接 **import `tools/probes/rebuild-blockers.py`** 复用
  （`sample_geometry` / `STEP_UP` / `BAND_LOW` / `BAND_HIGH` / `FLOOR_MIN_NY`）——
  判据的两半**必须同源**，否则 diff 无意义（旧版是两份各写一遍，已经漂移过一次）。
* **人体半径**（缝隙判据下界）读自 `client/Assets/Scripts/Core/CsConst.cs` 的 `PlayerRadius`。
* **台阶高度**读自同一个文件的 `StepUpHeight`（脚本里从 CsConst 解析，⛔ 不写死）。

用法（只读，不改盘）：
    python tools/probes/collision-mesh-gap.py                     # 出 TSV + 汇总到 stdout
    python tools/probes/collision-mesh-gap.py --out <path>        # 指定 TSV 落点
    python tools/probes/collision-mesh-gap.py --quiet             # 只出汇总
默认 TSV 落 `.ai-tmp/test/collision-mesh-gap.tsv`（一次性产物；脚本本身是判据资产）。
"""
import hashlib
import importlib.util
import os
import re
import struct
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
ASSETS = os.path.join(ROOT, 'client', 'Assets')
GEO = os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
BYTES_FILES = [
    os.path.join(ASSETS, 'MapData', 'de_dust2.bytes'),
    os.path.join(ASSETS, 'Resources', 'MapData', 'de_dust2.bytes'),
]
BITMAP = BYTES_FILES[1]                      # 运行期真正读的那份
SCENE = os.path.join(ASSETS, 'Scenes', 'StageDust2.unity')
CSCONST = os.path.join(ASSETS, 'Scripts', 'Core', 'CsConst.cs')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test', 'collision-mesh-gap.tsv')
MARKERS = os.path.join(ASSETS, 'Resources', 'MapData', 'de_dust2_markers.bytes')

# ---- 判据口径：**同源**取 rebuild-blockers.py（同一份判据的两半必须同采样同规则）----
_spec = importlib.util.spec_from_file_location(
    'clover_rebuild_blockers', os.path.join(HERE, 'rebuild-blockers.py'))
RB = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(RB)

BAND_LOW = RB.BAND_LOW
BAND_HIGH = RB.BAND_HIGH
FLOOR_MIN_NY = RB.FLOOR_MIN_NY
WALL_MAX_NY = RB.WALL_MAX_NY
STEP_UP = RB.STEP_UP
GRID = RB.GRID

GAP_MAX_CELLS = 1.0          # 单位：格（≤ 1 格宽才算"缝"而不是"走廊"）


def sha1(path):
    h = hashlib.sha1()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()[:12]


def load_bits(path=BITMAP):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CLVM', path
    w, dep, cc, sc, n = struct.unpack_from('<5I', d, 32)
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    o = 64 + n
    bits = d[o:o + (w * dep + 7) // 8]
    coll = [struct.unpack_from('<6f', d, o + len(bits) + 24 * i) for i in range(cc)]
    return dict(w=w, d=dep, cell=cell, ox=ox, oz=oz, colliders=cc, spawns=sc,
                bits=bits, colliders_aabb=coll, file=path, size=len(d))


def read_const(name, default=None):
    """从 Core/CsConst.cs 读一个 `const float X = <num>;`（⛔ 不写死口径数字）。"""
    try:
        with open(CSCONST, 'rb') as f:
            txt = f.read().decode('utf-8', 'replace')
    except OSError:
        return default
    m = re.search(r'const\s+float\s+%s\s*=\s*([0-9.]+)f?\s*;' % name, txt)
    return float(m.group(1)) if m else default


def load_markers(path=MARKERS):
    """运行时标记表（文本：每行 `标记名 x y z`）—— 用来把清单行点回用户点名的地方。"""
    out = defaultdict(list)
    try:
        with open(path, 'rb') as f:
            txt = f.read().decode('utf-8', 'replace')
    except OSError:
        return out
    for line in txt.split('\n'):
        p = line.split()
        if len(p) < 4 or line.startswith('#'):
            continue
        try:
            out[p[0]].append((float(p[1]), float(p[2]), float(p[3])))
        except ValueError:
            continue
    return out


def parse_scene(path=SCENE):
    with open(path, 'rb') as f:
        txt = f.read().decode('utf-8', 'replace')
    docs = re.findall(r'--- !u!(\d+) &(\d+)(?: stripped)?\s*\n(.*?)(?=\n--- !u!|\Z)', txt, re.S)
    name_by_id = {}
    for cid, fid, body in docs:
        if cid == '1':
            m = re.search(r'm_Name:\s*(.*)', body)
            if m:
                name_by_id[fid] = m.group(1).strip()
    return dict(blockers=len([n for n in name_by_id.values() if n.startswith('Blocker_')]))


def main():
    quiet = '--quiet' in sys.argv
    out_path = DEFAULT_OUT
    if '--out' in sys.argv:
        out_path = sys.argv[sys.argv.index('--out') + 1]

    radius = read_const('PlayerRadius')
    if radius is None:
        print('FAIL 从 Core/CsConst.cs 读 PlayerRadius 失败（缝隙判据口径拿不到 ⇒ 不许猜）')
        return 1
    gap_min = radius

    geo = RB.load(GEO)
    cell = geo['cell']; ox = geo['ox']; oz = geo['oz']
    W, D = geo['w'], geo['d']
    bm = load_bits(BITMAP)
    scene = parse_scene()

    def walkable(ix, iz):
        if ix < 0 or iz < 0 or ix >= W or iz >= D:
            return False
        i = iz * W + ix
        return (bm['bits'][i >> 3] & (1 << (i & 7))) != 0

    print('# collision-mesh-gap：输入指纹 geo=%s  bitmap=%s  scene_Blocker_=%d'
          % (sha1(GEO), sha1(BITMAP), scene['blockers']))
    print('# 口径（同源 rebuild-blockers）：每格 %dx%d 含边界 + 线段光栅化 '
          'band=[%.2f,%.2f] StepUp=%.2f PlayerRadius=%.3f cell=%.3f bitmap %dx%d origin=(%.1f,%.1f) 阻挡盒 %d'
          % (GRID + 1, GRID + 1, BAND_LOW, BAND_HIGH, STEP_UP, radius, cell, W, D,
             ox, oz, len(geo['blockers'])))

    # ---- ① 用**几何**独立重算（与 rebuild-blockers 同一采样）----
    floors, walls, kinds = RB.sample_geometry(geo, ox, oz, cell, W, D)

    # ---- ② a 可穿面 / a′ 上层带被墙穿 ----
    a_cells = {}                       # cell -> (group, faces, span)
    a2_cells = {}
    for key, ws in walls.items():
        if not walkable(*key):
            continue                   # 位图判挡 ⇒ 已被覆盖
        fs = floors.get(key)
        if not fs:
            continue
        f0 = min(fs)
        lo0, hi0 = f0 + BAND_LOW, f0 + BAND_HIGH
        for (w0, w1, ytop, name) in ws:
            # 基层：最低地面候选那一层的人体带
            if w0 < hi0 and w1 > lo0 and ytop > f0 + STEP_UP:
                span = min(ytop, hi0) - max(w0, lo0)
                prev = a_cells.get(key)
                if prev is None or span > prev[2]:
                    a_cells[key] = (name, (prev[1] if prev else 0) + 1, span)
                else:
                    a_cells[key] = (prev[0], prev[1] + 1, prev[2])
                continue
            for f in fs:
                if f <= f0 + STEP_UP:
                    continue
                lo, hi = f + BAND_LOW, f + BAND_HIGH
                if w0 < hi and w1 > lo and ytop > f + STEP_UP:
                    a2_cells[key] = a2_cells.get(key, 0) + 1
                    break

    # ---- ③ b 撞空气 ----
    geom_cells = set()
    for key in floors:
        geom_cells.add(key)
    for key in walls:
        geom_cells.add(key)
    b_cells = []
    for iz in range(1, D - 1):
        for ix in range(1, W - 1):
            if walkable(ix, iz) or (ix, iz) in geom_cells:
                continue
            if not any(walkable(ix + dx, iz + dz) for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                continue                   # 不挨着可走格 ⇒ 图外实心，不是"撞空气"
            b_cells.append((ix, iz))
    b_set = set(b_cells)

    def _adj_wall(key):
        """该格的 8 邻里有没有"带墙"的格 —— 有 ⇒ 这一格是**量化的产物**
        （墙面的足迹落在共享边上，被判给了邻格），不是"撞空气"的缺陷。"""
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                if dx == 0 and dz == 0:
                    continue
                if (key[0] + dx, key[1] + dz) in walls:
                    return True
        return False

    b_rects = []
    seen = set()
    for c0 in sorted(b_set):
        if c0 in seen:
            continue
        stack = [c0]; seen.add(c0); comp = []
        while stack:
            c = stack.pop(); comp.append(c)
            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (c[0] + dx, c[1] + dz)
                if n in b_set and n not in seen:
                    seen.add(n); stack.append(n)
        xs = [c[0] for c in comp]; zs = [c[1] for c in comp]
        adj = sum(1 for c in comp if _adj_wall(c))
        cls = 'wall-adjacent' if adj == len(comp) else ('mixed' if adj else 'isolated')
        b_rects.append((min(xs), min(zs), max(xs), max(zs), len(comp), cls))

    # ---- ④ c 缝隙（相邻阻挡盒之间的净间隙）----
    boxes = [(x0, z0, x1, z1) for (x0, z0, x1, z1, _, _) in geo['blockers']]
    gap_max = cell * GAP_MAX_CELLS
    c_rows = []
    n = len(boxes)
    for i in range(n):
        ax0, az0, ax1, az1 = boxes[i]
        for j in range(i + 1, n):
            bx0, bz0, bx1, bz1 = boxes[j]
            gxx = max(ax0, bx0) - min(ax1, bx1) - 1
            gzz = max(az0, bz0) - min(az1, bz1) - 1
            if gxx < 0 and gzz < 0:
                continue
            if gxx >= 0 and gzz < 0:
                gap_cells, axis = gxx, 'x'
            elif gzz >= 0 and gxx < 0:
                gap_cells, axis = gzz, 'z'
            else:
                continue
            gap = gap_cells * cell
            if not (gap_min < gap <= gap_max):
                continue
            if axis == 'x':
                ix0 = min(ax1, bx1) + 1
                ix1 = max(ax0, bx0) - 1
                iz0 = max(min(az0, bz0), min(az1, bz1))
                iz1 = min(max(az0, bz0), max(az1, bz1))
                cells = [(ix, iz) for ix in range(ix0, ix1 + 1) for iz in range(iz0, iz1 + 1)]
            else:
                iz0 = min(az1, bz1) + 1
                iz1 = max(az0, bz0) - 1
                ix0 = max(min(ax0, bx0), min(ax1, bx1))
                ix1 = min(max(ax0, bx0), max(ax1, bx1))
                cells = [(ix, iz) for iz in range(iz0, iz1 + 1) for ix in range(ix0, ix1 + 1)]
            wk = [(ix, iz) for (ix, iz) in cells if walkable(ix, iz)]
            if not wk:
                continue
            # 要么格内压根没有渲染几何（= b 类）。两者都不是"缝"，是位图与模型不符。
            defect = any(k in a_cells or k not in geom_cells for k in wk)
            c_rows.append((gap, i, j, cells[0][0], cells[0][1], len(cells), defect))

    # ---- 输出 TSV（稳定排序）----
    def pos(ix, iz):
        return 'cell(%d,%d) x=%+.1f z=%+.1f' % (ix, iz, ox + (ix + 0.5) * cell, oz + (iz + 0.5) * cell)

    a_rows = [('a', name, pos(ix, iz), faces, '%.2f' % span)
              for (ix, iz), (name, faces, span) in a_cells.items()]
    a2_rows = [('a2', '（另一层）', pos(ix, iz), n2, '0.00') for (ix, iz), n2 in a2_cells.items()]
    b_rows = [('b', 'Blockers/%s' % cls, 'cell(%d..%d,%d..%d) x=%+.1f z=%+.1f'
               % (ix0, ix1, iz0, iz1, ox + (ix0 + ix1 + 1) / 2 * cell, oz + (iz0 + iz1 + 1) / 2 * cell),
               cnt, '%.2f' % (cnt * cell * cell)) for (ix0, iz0, ix1, iz1, cnt, cls) in b_rects]
    c_rows_t = [('c' if defect else 'c-info', 'Blocker_%04d|Blocker_%04d' % (i, j), pos(cx0, cz0),
                 cnt, '%.3f' % gap) for (gap, i, j, cx0, cz0, cnt, defect) in c_rows]
    rows = (sorted(a_rows, key=lambda r: r[2]) + sorted(a2_rows, key=lambda r: r[2]) +
            sorted(b_rows, key=lambda r: r[2]) + sorted(c_rows_t, key=lambda r: r[4]))

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# 碰撞-几何逐面一致性清单（由 tools/probes/collision-mesh-gap.py 生成，可 diff）\n')
        f.write('# 输入：geo=%s  bitmap=%s  scene_Blocker_=%d\n'
                % (sha1(GEO), sha1(BITMAP), scene['blockers']))
        f.write('# 口径：每格 %dx%d 含边界 + 线段光栅化 band=[%.2f,%.2f] StepUp=%.2f PlayerRadius=%.3f\n'
                % (GRID + 1, GRID + 1, BAND_LOW, BAND_HIGH, STEP_UP, radius))
        f.write('# 件名\t位置\t面数\t缺口尺寸\t分类\n')
        f.write('# 分类：a=可穿面(基层，缺陷)；a2=另一层人体带被墙穿(单层2D位图固有限制，非缺陷)；'
                'b=撞空气格(件名带 wall-adjacent/mixed/isolated)；c=缝隙(缺陷形)；'
                'c-info=1 格宽开口(几何为空，非缺陷)\n')
        f.write('# 缺口尺寸：a=基层人体带里被实心几何挡住的竖直跨度(m)；b=格数*cell^2(m2)；c=缝净宽(m)\n')
        for (cls, name, p, faces, size) in rows:
            f.write('%s\t%s\t%d\t%s\t%s\n' % (name, p, faces, size, cls))

    # ---- 汇总 ----
    print('')
    n_b_adj = sum(cnt for (_, _, _, _, cnt, cls) in b_rects if cls == 'wall-adjacent')
    n_b_mix = sum(cnt for (_, _, _, _, cnt, cls) in b_rects if cls == 'mixed')
    n_b_iso = sum(cnt for (_, _, _, _, cnt, cls) in b_rects if cls == 'isolated')
    print('# a  可穿面（基层，**缺陷**）        ：%d 格' % len(a_rows))
    print('# a2 另一层人体带被墙穿（非缺陷）    ：%d 格' % len(a2_rows))
    print('# b  撞空气                          ：%d 格 / %d 个连通块'
          '（紧贴墙面=量化产物 %d 格 / 混合 %d / 孤立 %d）'
          % (len(b_cells), len(b_rects), n_b_adj, n_b_mix, n_b_iso))
    n_c_def = sum(1 for r in c_rows if r[6])
    print('# c  缝隙（**缺陷形**：缝里可走格的基层被挡 or 无几何）：%d 对' % n_c_def)
    print('# c′ 缝隙（信息：缝里是真实 1 格宽开口，几何为空 ⇒ 非缺陷）：%d 对（gap ∈ (%.3f, %.3f]）'
          % (len(c_rows) - n_c_def, gap_min, gap_max))
    print('# TSV → %s（%d 行）' % (os.path.relpath(out_path, ROOT), len(rows)))
    if quiet:
        return 0

    print('')
    print('## a 可穿面 Top 20（按被挡竖直跨度降序）')
    for r in sorted(a_rows, key=lambda r: -float(r[4]))[:20]:
        print('  %-22s %-30s 面=%-4d 挡高=%.2f m' % (r[1], r[2], r[3], float(r[4])))
    print('')
    print('## b 撞空气 Top 10（按格数降序）')
    for r in sorted(b_rows, key=lambda r: -r[3])[:10]:
        print('  %-30s 格数=%-4d 面积=%.2f m2' % (r[2], r[3], float(r[4])))
    print('')
    print('## c 缝隙 Top 10（按缝宽降序）')
    for r in sorted(c_rows_t, key=lambda r: -float(r[4]))[:10]:
        print('  %-30s 缝宽=%.3f m' % (r[1], float(r[4])))

    # 用户点名的两处必须在清单里被指名（判据 = TSV 里按位置/组名能点到）
    print('')
    print('## 用户点名处定位（清单里点名）')
    markers = load_markers()
    a_pos = []
    for r in a_rows:
        m = re.search(r'x=([-+0-9.]+) z=([-+0-9.]+)', r[2])
        if m:
            a_pos.append((float(m.group(1)), float(m.group(2)), r))
    spots = [('警家 A 入口（BombsiteA/CTDefendA/Spawn_CT 一带）',
              ['BombsiteA', 'Bombsite_A', 'CTDefendA', 'Route_CT_To_A', 'Spawn_CT']),
             ('箱子族（box 组）', None)]
    for title, keys in spots:
        if keys:
            hit = []
            for k in keys:
                for (mx, my, mz) in markers.get(k, []):
                    for (ax, az, r) in a_pos:
                        if abs(ax - mx) <= 12.0 and abs(az - mz) <= 12.0:
                            hit.append((k, mx, mz, abs(ax - mx) + abs(az - mz), r))
            hit.sort(key=lambda h: h[3])
            print('  [%s] 12m 内可穿面 %d 条' % (title, len(hit)))
            for (k, mx, mz, d, r) in hit[:6]:
                print('     ← %s(%.1f,%.1f) d=%.1fm  %s %s 面=%d 挡高=%s'
                      % (k, mx, mz, d, r[1], r[2], r[3], r[4]))
        else:
            cr = [r for r in a_rows if 'box' in r[1].lower()]
            print('  [%s] 清单内可穿面 %d 条' % (title, len(cr)))
            for r in cr[:6]:
                print('     %s %s 面=%d 挡高=%s' % (r[1], r[2], r[3], r[4]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
