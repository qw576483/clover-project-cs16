# -*- coding: utf-8 -*-
"""判据资产：**碰撞-几何逐面一致性** —— 把"模型能穿 / 撞空气 / 可钻缝"穷举出来。

## 为什么需要它（根因）

`tools/probes/rebuild-blockers.py` 决定位图（可走性唯一事实），运行时 `CsMap.ResolveMove`
的快速分支 = `BitmapClear && GroundWithinStep`（**不做几何射线**）⇒ **位图说可走的地方，
人就真的走过去**。所以"模型能穿"只可能是**位图漏判**。

而 rebuild-blockers 的判据有两个面级盲区（本脚本就是要逐个面地把它们数出来）：
1. **陡面盲区**：它只把 `|n.y| < 0.30`（近垂直）的面当墙。`0.30 ≤ |n.y| < 0.70` 的面被归成
   `steep`，**既不当地面也不当墙** ⇒ 表面上"穿过人体高度带"的斜墙/沙坡/台阶前沿**不被计入阻挡**，
   只要该格里还有另一个朝上的面当"地面候选"，这一格就被判成**可走** ⇒ 贴着斜墙走进去。
2. **采样归属盲区**：它的采样网格是按三角面**包围盒**铺的（`(i+0.5)/n`），点**不保证落在三角面内**，
   且**不保证每格都有点**。细长/贴格边的面会整格漏采。

本脚本换口径：**沿三角面内部按重心坐标铺点**（点必然在面上），逐点落格，**每格再补格内 3×3 + 边界带**，
然后按"该格位图可走 + 该点在**人体高度带**内"判 **可穿面**。

## 判什么（三类）

| 类 | 判据 | 为什么 |
|---|---|---|
| **a 可穿面** | 近垂直（`|n.y|<0.30`）或陡（`0.30..0.70`）的渲染三角面，其**面内采样点**落在**位图可走的格**里，且该点 y 落在该格某个地面候选的**人体带** `[f+0.10, f+1.75]` 内 | 该处有实心几何、位图却说能走 ⇒ 人从那里穿过去 |
| **b 撞空气** | 位图判**阻挡**的格，**没有任何渲染几何**覆盖它（且它在图内、挨着可走格） | 撞到看不见的墙（模型与碰撞不符的另一种） |
| **c 缝隙** | 两个**不同的**阻挡盒之间在 XZ 上的净间隙 `0.4m < gap ≤ 1.0m`，且间隙里存在**可走格** | 相邻阻挡盒之间的缝够身子钻过去 |

## 口径来源（⛔ 不写死数字，全从盘上/工程常量取）
* 采样/带宽口径与 `rebuild-blockers.py` **同源**（`SAMPLE` / `WALL_MAX_NY` / `FLOOR_MIN_NY` /
  `BAND_LOW` / `BAND_HIGH`）—— 判据的两半必须同采样，否则 diff 无意义。
* `人体半径`（= 缝隙判据的 0.4m）读自 `client/Assets/Scripts/Core/CsConst.cs` 的 `PlayerRadius`。

用法（只读，不改盘）：
    python tools/probes/collision-mesh-gap.py                     # 出 TSV + 汇总到 stdout
    python tools/probes/collision-mesh-gap.py --out <path>        # 指定 TSV 落点
    python tools/probes/collision-mesh-gap.py --quiet             # 只出汇总
默认 TSV 落 `.ai-tmp/test/collision-mesh-gap.tsv`（一次性产物；脚本本身是判据资产）。
"""
import hashlib
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
MESH_BASELINE = os.path.join(ROOT, '\u539f\u7248\u8d44\u6e90', '\u5907\u4efd',
                             'de_dust2_geo.bin.bak')
DEFAULT_OUT = os.path.join(ROOT, '.ai-tmp', 'test', 'collision-mesh-gap.tsv')

# ---- 采样口径：与 rebuild-blockers.py 逐字一致（同一份判据的两半必须同采样）----
SAMPLE = 0.25
WALL_MAX_NY = 0.30
FLOOR_MIN_NY = 0.70
BAND_LOW = 0.10
BAND_HIGH = 1.75
# 缝隙判据的上下界（米）：> 人体半径才"身子进得去"，≤ 1 格宽（cell）才是"缝"而不是"走廊"
GAP_MIN = None            # 运行时从 CsConst.PlayerRadius 读回
GAP_MAX_CELLS = 1.0       # 单位：格


def sha1(path):
    h = hashlib.sha1()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()[:12]


# ============================================================================
#  读盘
# ============================================================================
def load_geo(path=GEO):
    with open(path, 'rb') as f:
        data = bytearray(f.read())
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v

    assert bytes(data[0:4]) == b'CD2G', path
    u32()
    cell = f32(); ox = f32(); oz = f32()
    gt = f32(); omh = f32(); pb = f32(); pt = f32()
    w = u32(); d = u32()
    for _ in range(6):
        f32()
    gc = u32()
    groups = []
    for _ in range(gc):
        name = bytes(data[o[0]:o[0] + 48]).split(b'\0')[0].decode('utf-8', 'replace')
        o[0] += 48
        vc = u32(); ic = u32()
        po = o[0]
        verts = [struct.unpack_from('<3f', data, po + 12 * i) for i in range(vc)]
        o[0] += 12 * vc + 8 * vc + 12 * vc
        idx = list(struct.unpack_from('<%dI' % ic, data, o[0])); o[0] += 4 * ic
        groups.append(dict(name=name, verts=verts, idx=idx, vc=vc, ic=ic))
    bc = u32()
    blockers = []
    for _ in range(bc):
        blockers.append((u32(), u32(), u32(), u32(), f32(), f32()))
    return dict(cell=cell, ox=ox, oz=oz, gt=gt, pb=pb, pt=pt, w=w, d=d,
                groups=groups, blockers=blockers)


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


def read_player_radius():
    try:
        with open(CSCONST, 'rb') as f:
            txt = f.read().decode('utf-8', 'replace')
    except OSError:
        return None
    m = re.search(r'const\s+float\s+PlayerRadius\s*=\s*([0-9.]+)f?\s*;', txt)
    return float(m.group(1)) if m else None


MARKERS = os.path.join(ASSETS, 'Resources', 'MapData', 'de_dust2_markers.bytes')


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
    mesh_nodes = set()
    for cid, fid, body in docs:
        if cid == '64':
            m = re.search(r'm_GameObject:\s*\{fileID:\s*(\d+)\}', body)
            if m and m.group(1) in name_by_id:
                mesh_nodes.add(name_by_id[m.group(1)])
    return dict(mesh_nodes=mesh_nodes,
                blockers=len([n for n in name_by_id.values() if n.startswith('Blocker_')]))


# ============================================================================
#  三角面工具
# ============================================================================
def tri_normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
    L = (nx * nx + ny * ny + nz * nz) ** 0.5
    if L < 1e-9:
        return (0.0, 0.0, 0.0, 0.0)
    return (nx / L, ny / L, nz / L, 0.5 * L)


def tri_samples(a, b, c):
    """沿三角面内部的重心坐标铺点：点**必然**在面上（不同于按包围盒铺点）。"""
    def dist(p, q):
        return ((p[0] - q[0]) ** 2 + (p[1] - q[1]) ** 2 + (p[2] - q[2]) ** 2) ** 0.5
    e = max(dist(a, b), dist(b, c), dist(c, a))
    n = int(e / SAMPLE) + 1
    if n < 2:
        n = 2
    elif n > 96:
        n = 96                       # 上限：极大面（整片地/长墙）按 96 细分即可覆盖每格
    inv = 1.0 / n
    ax, ay, az = a; bx, by, bz = b; cx, cy, cz = c
    ux, uy, uz = bx - ax, by - ay, bz - az
    vx, vy, vz = cx - ax, cy - ay, cz - az
    out = []
    for i in range(n + 1):
        u = i * inv
        for j in range(n + 1 - i):
            v = j * inv
            out.append((ax + u * ux + v * vx, ay + u * uy + v * vy, az + u * uz + v * vz))
    return out


def cells_of(geo, x, z):
    return int((x - geo['ox']) // geo['cell']), int((z - geo['oz']) // geo['cell'])


# ============================================================================
#  主流程
# ============================================================================
def main():
    quiet = '--quiet' in sys.argv
    out_path = DEFAULT_OUT
    if '--out' in sys.argv:
        out_path = sys.argv[sys.argv.index('--out') + 1]

    radius = read_player_radius()
    if radius is None:
        print('FAIL 从 Core/CsConst.cs 读 PlayerRadius 失败（缝隙判据口径拿不到 ⇒ 不许猜）')
        return 1
    gap_min = radius

    geo = load_geo()
    src = geo
    if os.path.exists(MESH_BASELINE):
        base = load_geo(MESH_BASELINE)
        same = (len(base['groups']) == len(geo['groups']) and
                all(a['name'] == b['name'] and a['ic'] == b['ic'] and a['verts'] == b['verts']
                    for a, b in zip(geo['groups'], base['groups'])))
        if same:
            print('# 网格基线 == 当前 geo（门贴图面已在文件里）')
        else:
            print('⚠️ 网格基线 != 当前 geo：判据用当前文件（差异：%d 组）'
                  % sum(1 for a, b in zip(geo['groups'], base['groups']) if a['ic'] != b['ic']))
    bm = load_bits(BITMAP)
    scene = parse_scene()
    W, D = bm['w'], bm['d']
    cell = bm['cell']

    def walkable(ix, iz):
        if ix < 0 or iz < 0 or ix >= W or iz >= D:
            return False
        i = iz * W + ix
        return (bm['bits'][i >> 3] & (1 << (i & 7))) != 0

    # ---- 汇总头（判据可从盘上复算：把输入指纹也写进去）----
    print('# collision-mesh-gap：输入指纹 geo=%s  bitmap=%s  scene_Blocker_=%d'
          % (sha1(GEO), sha1(BITMAP), scene['blockers']))
    print('# 口径：SAMPLE=%.2f WALL_MAX_NY=%.2f FLOOR_MIN_NY=%.2f band=[%.2f,%.2f] '
          'PlayerRadius=%.3f cell=%.3f bitmap %dx%d origin=(%.1f,%.1f)  阻挡盒 %d'
          % (SAMPLE, WALL_MAX_NY, FLOOR_MIN_NY, BAND_LOW, BAND_HIGH, radius, cell, W, D,
             geo['ox'], geo['oz'], len(geo['blockers'])))

    # ---- ① 收集地面候选（与 rebuild-blockers 同口径：朝上面取 min y）----
    floors = defaultdict(set)
    for g in src['groups']:
        V, I = g['verts'], g['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            ny = tri_normal(a, b, c)[1]
            if ny < FLOOR_MIN_NY:
                continue
            fy = min(a[1], b[1], c[1])
            for (px, py, pz) in tri_samples(a, b, c):
                floors[cells_of(geo, px, pz)].add(round(fy, 2))

    # ---- ② 逐面判 a 可穿面 ----
    # 每格：{ 组名: [三角面数, 采样点数, 最低y, 最高y] }
    a_cell = defaultdict(lambda: defaultdict(lambda: [0, 0, 1e9, -1e9]))
    a_tri_ids = defaultdict(set)           # (cell, group) -> set(tri_id) 避免重复计面
    a_kind = defaultdict(int)              # 'wall' / 'steep'
    tri_id = 0
    for g in src['groups']:
        name = g['name']; V = g['verts']; I = g['idx']
        for k in range(0, len(I), 3):
            tri_id += 1
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            nx, ny, nz, area = tri_normal(a, b, c)
            if area <= 0.0:
                continue
            any_ = abs(ny)
            if any_ >= FLOOR_MIN_NY:
                continue                       # 地板：不是"可穿面"
            kind = 'wall' if any_ < WALL_MAX_NY else 'steep'
            for (px, py, pz) in tri_samples(a, b, c):
                key = cells_of(geo, px, pz)
                if not walkable(*key):
                    continue                   # 位图判挡 ⇒ 已被覆盖
                fs = floors.get(key)
                if not fs:
                    continue                   # 该格没有地面候选（位图也不会判可走；双保险）
                in_band = False
                for f in fs:
                    if f + BAND_LOW <= py <= f + BAND_HIGH:
                        in_band = True
                        break
                if not in_band:
                    continue                   # 在人体带之外（头顶横梁 / 脚下深处）⇒ 不算可穿
                rec = a_cell[key][name]
                rec[1] += 1
                if py < rec[2]:
                    rec[2] = py
                if py > rec[3]:
                    rec[3] = py
                a_tri_ids[(key, name)].add(tri_id)
                a_kind[kind] += 1
    for (key, name), ids in a_tri_ids.items():
        a_cell[key][name][0] = len(ids)

    # ---- ③ 逐格判 b 撞空气 ----
    # 需要：位图阻挡格、图内、挨着可走格、且没有任何渲染几何落在它的 XZ 足迹里
    geom_cells = set()
    for g in src['groups']:
        V, I = g['verts'], g['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            if tri_normal(a, b, c)[3] <= 0.0:
                continue
            for (px, py, pz) in tri_samples(a, b, c):
                geom_cells.add(cells_of(geo, px, pz))
    b_cells = []
    for iz in range(1, D - 1):
        for ix in range(1, W - 1):
            if walkable(ix, iz):
                continue
            if (ix, iz) in geom_cells:
                continue
            if not any(walkable(ix + dx, iz + dz) for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1))):
                continue                       # 不挨着可走格 ⇒ 图外实心，不是"撞空气"
            b_cells.append((ix, iz))
    # 4 连通合并成矩形（可读 + 稳定）
    b_set = set(b_cells)
    b_rects = []
    seen = set()
    for c0 in sorted(b_set):
        if c0 in seen:
            continue
        stack = [c0]; seen.add(c0)
        comp = []
        while stack:
            c = stack.pop(); comp.append(c)
            for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                n = (c[0] + dx, c[1] + dz)
                if n in b_set and n not in seen:
                    seen.add(n); stack.append(n)
        xs = [c[0] for c in comp]; zs = [c[1] for c in comp]
        b_rects.append((min(xs), min(zs), max(xs), max(zs), len(comp)))

    # ---- ④ 判 c 缝隙（相邻阻挡盒之间的净间隙）----
    boxes = [(x0, z0, x1, z1) for (x0, z0, x1, z1, _, _) in geo['blockers']]
    gap_max = cell * GAP_MAX_CELLS
    c_rows = []
    n = len(boxes)
    for i in range(n):
        ax0, az0, ax1, az1 = boxes[i]                   # 格索引闭区间
        for j in range(i + 1, n):
            bx0, bz0, bx1, bz1 = boxes[j]
            # 两盒 XZ 之间的「格数间隙」：重叠 = -1；紧邻 = 0；隔一格 = 1
            gxx = max(ax0, bx0) - min(ax1, bx1) - 1
            gzz = max(az0, bz0) - min(az1, bz1) - 1
            if gxx < 0 and gzz < 0:
                continue                                # 两盒在 XZ 上都重叠 ⇒ 同一片，忽略
            if gxx >= 0 and gzz < 0:
                gap_cells, axis = gxx, 'x'
            elif gzz >= 0 and gxx < 0:
                gap_cells, axis = gzz, 'z'
            else:
                continue                                # 对角分离 ⇒ 不算"相邻缝"
            gap = gap_cells * cell
            if not (gap_min < gap <= gap_max):
                continue
            # 缝里必须真的有可走格（否则是被墙隔开的实心，不是"缝"）
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
            if not any(walkable(ix, iz) for (ix, iz) in cells):
                continue
            c_rows.append((gap, i, j, cells[0][0], cells[0][1], len(cells)))

    # ---- 输出 TSV（稳定排序）----
    rows = []
    for key, per_group in a_cell.items():
        ix, iz = key
        for name, rec in per_group.items():
            faces, samples, ylo, yhi = rec
            rows.append(('a', name,
                         'cell(%d,%d) x=%+.1f z=%+.1f' % (ix, iz,
                                                          geo['ox'] + (ix + 0.5) * cell,
                                                          geo['oz'] + (iz + 0.5) * cell),
                         faces, '%.2f' % (yhi - ylo), samples))
    for (ix0, iz0, ix1, iz1, cnt) in b_rects:
        rows.append(('b', 'Blockers',
                     'cell(%d..%d,%d..%d) x=%+.1f z=%+.1f' % (ix0, ix1, iz0, iz1,
                                                              geo['ox'] + (ix0 + ix1 + 1) / 2 * cell,
                                                              geo['oz'] + (iz0 + iz1 + 1) / 2 * cell),
                     cnt, '%.2f' % (cnt * cell * cell), 0))
    for (gap, i, j, cx0, cz0, cnt) in c_rows:
        rows.append(('c', 'Blocker_%04d|Blocker_%04d' % (i, j),
                     'cell(%d,%d) x=%+.1f z=%+.1f' % (cx0, cz0,
                                                      geo['ox'] + (cx0 + 0.5) * cell,
                                                      geo['oz'] + (cz0 + 0.5) * cell),
                     cnt, '%.3f' % gap, 0))

    def sort_key(r):
        return (r[0], r[2], r[1], r[3])

    # 稳定排序：a 先按位置，b 按格，c 按缝宽
    a_rows = sorted([r for r in rows if r[0] == 'a'], key=lambda r: (r[2], r[1]))
    b_rows = sorted([r for r in rows if r[0] == 'b'], key=lambda r: r[2])
    c_rows_s = sorted([r for r in rows if r[0] == 'c'], key=lambda r: r[4])
    rows = a_rows + b_rows + c_rows_s

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# 碰撞-几何逐面一致性清单（由 tools/probes/collision-mesh-gap.py 生成，可 diff）\n')
        f.write('# 输入：geo=%s  bitmap=%s  scene_Blocker_=%d\n'
                % (sha1(GEO), sha1(BITMAP), scene['blockers']))
        f.write('# 口径：SAMPLE=%.2f WALL_MAX_NY=%.2f FLOOR_MIN_NY=%.2f band=[%.2f,%.2f] '
                'PlayerRadius=%.3f\n' % (SAMPLE, WALL_MAX_NY, FLOOR_MIN_NY, BAND_LOW, BAND_HIGH, radius))
        f.write('# 件名\t位置\t面数\t缺口尺寸\t分类\n')
        f.write('# 缺口尺寸：a=未覆盖结构在格内的竖直跨度(m)；b=撞空气格数；c=缝净宽(m)\n')
        for (cls, name, pos, faces, size, samples) in rows:
            f.write('%s\t%s\t%d\t%s\t%s\n' % (name, pos, faces, size, cls))

    # ---- 汇总 ----
    print('')
    print('# a 可穿面：%d 格（wall 采样 %d / steep 采样 %d）'
          % (len(a_rows), a_kind['wall'], a_kind['steep']))
    wall_cells = sum(1 for r in a_rows if True)
    print('# b 撞空气：%d 格 / %d 个连通块' % (len(b_cells), len(b_rects)))
    print('# c 缝隙  ：%d 对相邻阻挡盒（gap ∈ (%.3f, %.3f]）' % (len(c_rows), gap_min, gap_max))
    print('# TSV → %s（%d 行）' % (os.path.relpath(out_path, ROOT), len(rows)))
    if not quiet:
        print('')
        print('## a 可穿面 Top 20（按格内最高未覆盖面降序）')
        top = sorted(a_rows, key=lambda r: -float(r[4]))[:20]
        for r in top:
            print('  %-22s %-30s 面=%-4d 竖直跨度=%.2f m' % (r[1], r[2], r[3], float(r[4])))
        print('')
        print('## b 撞空气 Top 10（按格数降序）')
        for r in sorted(b_rows, key=lambda r: -r[3])[:10]:
            print('  %-30s 格数=%-4d 面积=%.2f m2' % (r[2], r[3], float(r[4])))
        print('')
        print('## c 缝隙 Top 10（按缝宽降序）')
        for r in sorted(c_rows_s, key=lambda r: -float(r[4]))[:10]:
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
        spots = [('警家 A 入口（BombsiteA/CTDefendA 一带）', ['BombsiteA', 'CTDefendA', 'Spawn_CT']),
                 ('箱子族（box 组）', None)]
        for title, keys in spots:
            hit = []
            if keys:
                for k in keys:
                    for (mx, my, mz) in markers.get(k, []):
                        for (ax, az, r) in a_pos:
                            if abs(ax - mx) <= 12.0 and abs(az - mz) <= 12.0:
                                hit.append((k, mx, mz, abs(ax - mx) + abs(az - mz), r))
                hit.sort(key=lambda h: h[3])
                print('  [%s] 12m 内可穿面 %d 条（marker=%s）' % (title, len(hit), keys))
                for (k, mx, mz, d, r) in hit[:6]:
                    print('     ← %s(%.1f,%.1f) d=%.1fm  %s %s 面=%d 跨度=%s'
                          % (k, mx, mz, d, r[1], r[2], r[3], r[4]))
            else:
                cr = [r for r in a_rows if 'box' in r[1].lower()]
                print('  [%s] 清单内可穿面 %d 条' % (title, len(cr)))
                for r in cr[:6]:
                    print('     %s %s 面=%d 跨度=%s' % (r[1], r[2], r[3], r[4]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
