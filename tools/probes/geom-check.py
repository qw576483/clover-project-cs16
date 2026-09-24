# -*- coding: utf-8 -*-
"""判据资产：几何 / 阻挡盒口径 / 箱子脚印 / 矮障碍可达性 / 角色间推开 的**离线断言**。

本片（cs16-切片F）用它出「修前 / 修后同口径数字」。它同时是一个**可导入模块**
（`tools/probes/enumerate-entities.py` 直接 import 它取实测），所以口径只有一份。


| 断言 | 判据 | 为什么 |
|---|---|---|
| A1 阻挡盒口径三方一致 | `.bytes` 头 `colliders` == 尾部碰撞体段条数 == `geo.bin` 阻挡盒数 == 场景 `Blocker_*` 数，且**逐条 AABB 等价** | 位图是**可走性**的唯一事实；三处口径不同 = "可走性按旧盒子算"（实测曾是 218 vs 660） |
| A2 几何组上场景 | 每个 `tris>0` 的 geo 组在场景里都有一个 `MeshFilter` 节点 | 组里几何存在但没上场景 = 画面上缺一块 |
| A3 门组非空 | 6 个 `*Door*.png` 组的 `tris > 0` | 用户报"该有门却没有门"（门贴图面被从渲染网格里摘掉过） |
| A4 箱子挡人 | ①箱子脚印内的**位图可走格**必须都在"有朝上面的格"里；②箱子比来路地面高出一整步时，从外部**等级地面高度**走进脚印 ⇒ **9 点身体高度带闸门必须全挡**；③可走格在**格中心真正的最高朝上面**高度上要站得住 | 用户报"箱子能穿"。位图是单层 2D，"箱子顶面"会被判成可走 ⇒ 只查位图时人能走进箱子 |
| A5 矮障碍可跳过 | 地图上所有"顶面高差 ∈ (一步台阶, 跳跃可达高度]"且位图判挡的格：**地面高度挡住**、**跳起高度可通过** | 用户报"匪家楼梯扶手跳不过去"。位图一格一位表达不了高度 ⇒ 矮障碍变成隐形高墙 |
| A6 角色间不重合 | `tools/probes/check-actor-separation.cs` 的离线断言全过（读它写的报告） | 用户报"人物和人物能重合" |

## 口径来源（不写死数字，全部从工程常量/盘上数据取）
* `CsConst.PlayerRadius` / `StandHeight` / `StepUpHeight` / `GroundCheckDistance` / `JumpSpeed` / `Gravity`
  —— 本脚本按**正则从 `Core/CsConst.cs` 读回**，与运行时同一份声明（常量改了断言跟着改）。
* `BodyHeightClearAt` 的纯几何复刻：从头顶上方往下打一根射线，**只看第一个朝上的交点**
  （Unity `Physics.Raycast` 默认 `queriesHitBackfaces=false` ⇒ 只命中朝向射线的面 ⇒ 就是"朝上"的面）：
  第一交点在脚面以下 ⇒ 身高带是空的；在身高带之内 ⇒ 被挡。与 `CsMap.BodyHeightClearAt` 同口径。

用法（只读，不改盘）：
    python tools/probes/geom-check.py            # 出报告，全过退出 0，有 FAIL 退出 1
    python tools/probes/geom-check.py --quiet    # 只出 PASS/FAIL 汇总行
"""
import os
import re
import struct
import sys
from collections import defaultdict

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
ASSETS = os.path.join(ROOT, 'client', 'Assets')
GEO = os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
BSP = os.path.join(ASSETS, 'ThirdParty', 'Dust2', 'de_dust2.bsp')
SCENE = os.path.join(ASSETS, 'Scenes', 'StageDust2.unity')
BYTES_FILES = [
    os.path.join(ASSETS, 'MapData', 'de_dust2.bytes'),
    os.path.join(ASSETS, 'Resources', 'MapData', 'de_dust2.bytes'),
]
BITMAP = BYTES_FILES[1]                       # 运行期真正读的那份
CSCONST = os.path.join(ASSETS, 'Scripts', 'Core', 'CsConst.cs')
SEPARATION_REPORT = os.path.join(HERE, 'actor-separation-check.txt')

# ---- 采样口径：与 tools/probes/rebuild-blockers.py 一致（同一份判据的两半必须同采样）----
SAMPLE = 0.25
WALL_MAX_NY = 0.30
FLOOR_MIN_NY = 0.70
CRATE_GROUPS = ('box.png', 'box_x.png')

# ============================================================================
#  0. 工程常量（从 CsConst.cs 读回，不写死）
# ============================================================================


def read_cs_const():
    try:
        with open(CSCONST, 'rb') as f:
            txt = f.read().decode('utf-8', 'replace')
    except OSError:
        return None
    want = ['PlayerRadius', 'StandHeight', 'StepUpHeight', 'GroundCheckDistance',
            'JumpSpeed', 'Gravity', 'MaxStandableSlopeNormalZ', 'SpeedKnife']
    out = {}
    for name in want:
        m = re.search(r'const\s+float\s+' + name + r'\s*=\s*([0-9.]+)f?\s*;', txt)
        if not m:
            return None
        out[name] = float(m.group(1))
    return out


def const_or_die():
    c = read_cs_const()
    if c is None:
        print('FAIL A0 从 Core/CsConst.cs 读常数失败（判据口径拿不到 ⇒ 不许猜）')
        sys.exit(1)
    return c


# ============================================================================
#  1. 读盘
# ============================================================================


def load_geo(path=GEO):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CD2G', path
    o = [4]

    def u32():
        v = struct.unpack_from('<I', d, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', d, o[0])[0]; o[0] += 4; return v

    u32()
    cell = f32(); ox = f32(); oz = f32()
    gt = f32(); omh = f32(); pb = f32(); pt = f32()
    w = u32(); dep = u32()
    for _ in range(6):
        f32()
    gc = u32()
    groups = []
    for _ in range(gc):
        name = d[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8', 'replace'); o[0] += 48
        vc = u32(); ic = u32()
        po = o[0]
        verts = [struct.unpack_from('<3f', d, po + 12 * i) for i in range(vc)]
        o[0] += 12 * vc + 8 * vc + 12 * vc
        idx = list(struct.unpack_from('<%dI' % ic, d, o[0])); o[0] += 4 * ic
        groups.append(dict(name=name, verts=verts, idx=idx))
    bc = u32()
    blockers = []
    for _ in range(bc):
        blockers.append((u32(), u32(), u32(), u32(), f32(), f32()))
    return dict(cell=cell, ox=ox, oz=oz, w=w, d=dep, groups=groups, blockers=blockers)


def load_bits(path=BITMAP):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CLVM', path
    w, dep, cc, sc, n = struct.unpack_from('<5I', d, 32)
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    o = 64 + n
    bits = d[o:o + (w * dep + 7) // 8]
    blob = d[o + len(bits):]                 # 碰撞体段(cc*24) + 出生点段(sc*12)
    coll = [struct.unpack_from('<6f', blob, 24 * i) for i in range(cc)]
    return dict(w=w, d=dep, cell=cell, ox=ox, oz=oz, colliders=cc, spawns=sc,
                bits=bits, blob_len=len(blob), colliders_aabb=coll, file=path, size=len(d))


def parse_scene(path=SCENE):
    """场景 YAML：① 有 MeshCollider 的 GameObject 名集合（= 上了场景的材质组）② Blocker_* 子物件数。"""
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
        if cid == '64':          # MeshCollider
            m = re.search(r'm_GameObject:\s*\{fileID:\s*(\d+)\}', body)
            if m and m.group(1) in name_by_id:
                mesh_nodes.add(name_by_id[m.group(1)])
    blocker_count = len([n for n in name_by_id.values() if n.startswith('Blocker_')])
    return dict(mesh_nodes=mesh_nodes, blockers=blocker_count, path=path)


# ============================================================================
#  2. 三角面 + 按格的索引（没有索引的话逐格查询是 O(格数×面数)，跑不动）
# ============================================================================


def tri_normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
    L = (nx * nx + ny * ny + nz * nz) ** 0.5
    return (0.0, 0.0, 0.0) if L < 1e-9 else (nx / L, ny / L, nz / L)


class Tri:
    __slots__ = ('ax', 'az', 'bx', 'bz', 'cx', 'cz', 'ny', 'nx', 'nz', 'd', 'y0', 'y1',
                 'group', 'yx', 'yy', 'yz')

    def __init__(self, a, b, c, group):
        n = tri_normal(a, b, c)
        self.ny, self.nx, self.nz = n[1], n[0], n[2]
        self.group = group
        self.ax, self.az = a[0], a[2]
        self.bx, self.bz = b[0], b[2]
        self.cx, self.cz = c[0], c[2]
        self.yx, self.yy, self.yz = a[1], b[1], c[1]
        ys = (a[1], b[1], c[1])
        self.y0, self.y1 = min(ys), max(ys)
        self.d = -(n[0] * a[0] + n[1] * a[1] + n[2] * a[2])

    def y_at(self, x, z):
        if abs(self.ny) < 1e-9:
            return None
        return -(self.nx * x + self.nz * z + self.d) / self.ny

    def contains_xz(self, x, z, tol=1e-4):
        v0x, v0z = self.cx - self.ax, self.cz - self.az
        v1x, v1z = self.bx - self.ax, self.bz - self.az
        v2x, v2z = x - self.ax, z - self.az
        den = v0x * v1z - v1x * v0z
        if abs(den) < 1e-12:
            return False
        u = (v2x * v1z - v1x * v2z) / den
        v = (v0x * v2z - v2x * v0z) / den
        return u >= -tol and v >= -tol and (u + v) <= 1.0 + tol


class Geom:
    """一组三角面 + 按格的索引。`first_up` 只在该格的桶里找 ⇒ 与格数无关地快。"""

    def __init__(self, geo, groups=None):
        self.geo = geo
        self.tris = []
        for g in geo['groups']:
            if groups is not None and g['name'] not in groups:
                continue
            V, I = g['verts'], g['idx']
            for k in range(0, len(I), 3):
                self.tris.append(Tri(V[I[k]], V[I[k + 1]], V[I[k + 2]], g['name']))
        self.bucket = defaultdict(list)
        for t in self.tris:
            ix0, iz0 = bm_cell(geo, min(t.ax, t.bx, t.cx), min(t.az, t.bz, t.cz))
            ix1, iz1 = bm_cell(geo, max(t.ax, t.bx, t.cx), max(t.az, t.bz, t.cz))
            for ix in range(ix0, ix1 + 1):
                for iz in range(iz0, iz1 + 1):
                    self.bucket[(ix, iz)].append(t)

    def bucket_at(self, x, z):
        return self.bucket.get(bm_cell(self.geo, x, z), ())

    def first_up_face_y(self, x, z, probe_top):
        """从 probe_top 向下**第一处朝上的面**（Unity 默认 queriesHitBackfaces=false ⇒ 只命中朝射线的面）。
        返回 (y, group)；打不到返回 (None, None)。"""
        best, best_g = None, None
        for t in self.bucket_at(x, z):
            if t.ny <= 0.0:
                continue
            if t.y1 > probe_top + 1e-9:
                continue
            if not t.contains_xz(x, z):
                continue
            y = t.y_at(x, z)
            if y is None or y > probe_top + 1e-9:
                continue
            if best is None or y > best:
                best, best_g = y, t.group
        return best, best_g


def bm_cell(geo, x, z):
    return int((x - geo['ox']) // geo['cell']), int((z - geo['oz']) // geo['cell'])


def bm_walk(bm, ix, iz):
    if ix < 0 or iz < 0 or ix >= bm['w'] or iz >= bm['d']:
        return False
    i = iz * bm['w'] + ix
    return (bm['bits'][i >> 3] & (1 << (i & 7))) != 0


def cell_center(geo, ix, iz):
    return geo['ox'] + (ix + 0.5) * geo['cell'], geo['oz'] + (iz + 0.5) * geo['cell']


# ============================================================================
#  3. 运行时闸门的纯几何复刻（与 CsMap.BodyHeightClearAt / BodyHeightClear 同口径）
# ============================================================================
class Gate:
    def __init__(self, const, geom):
        self.c = const
        self.g = geom
        self.lift = const['GroundCheckDistance']
        self.drop = 8.0

    def clear_at(self, x, z, feet_y):
        probe_top = feet_y + self.c['StandHeight'] + self.lift
        y, _ = self.g.first_up_face_y(x, z, probe_top)
        if y is None:
            return False                     # 这一列没有世界面 ⇒ 保守判挡（与 C# 一致）
        if y < probe_top - (self.c['StandHeight'] + self.lift + self.drop):
            return False                     # 超出探测深度（与射线长度口径一致）
        return y <= feet_y + self.c['GroundCheckDistance']

    def clear_9(self, x, z, feet_y, radius=None):
        r = self.c['PlayerRadius'] if radius is None else radius
        d = r * 0.70710678
        pts = [(x, z), (x + r, z), (x - r, z), (x, z + r), (x, z - r),
               (x + d, z + d), (x + d, z - d), (x - d, z + d), (x - d, z - d)]
        for (px, pz) in pts:
            if not self.clear_at(px, pz, feet_y):
                return False
        return True


# ============================================================================
#  4. 各判据的实现（返回结构化数字，供报告与覆盖矩阵共用）
# ============================================================================


def colliders_consistency(geo, scene, bms):
    cell, ox, oz = geo['cell'], geo['ox'], geo['oz']
    geo_aabb = []
    for (ix0, iz0, ix1, iz1, y0, y1) in geo['blockers']:
        geo_aabb.append((ox + ix0 * cell, y0, oz + iz0 * cell,
                         ox + (ix1 + 1) * cell, y1, oz + (iz1 + 1) * cell))
    n_geo = len(geo['blockers'])
    ok = (scene['blockers'] == n_geo)
    per = []
    for b in bms:
        seg_ok = (b['blob_len'] == b['colliders'] * 24 + b['spawns'] * 12)
        head_ok = (b['colliders'] == n_geo)
        aabb_ok = (len(b['colliders_aabb']) == n_geo and
                   all(all(abs(a - e) < 1e-3 for a, e in zip(b['colliders_aabb'][i], geo_aabb[i]))
                       for i in range(n_geo)))
        ok = ok and seg_ok and head_ok and aabb_ok
        per.append(dict(file=os.path.relpath(b['file'], ROOT), head=b['colliders'], seg=b['blob_len'],
                        spawns=b['spawns'], aabb_ok=aabb_ok, ok=seg_ok and head_ok and aabb_ok))
    return dict(ok=ok, geo=n_geo, scene=scene['blockers'], per=per)


def scene_groups(geo, scene):
    have = [g['name'] for g in geo['groups'] if len(g['idx']) > 0]
    missing = [n for n in have if n not in scene['mesh_nodes']]
    return dict(ok=not missing, geo_groups_with_tris=len(have),
                scene_mesh_nodes=len(scene['mesh_nodes']), missing=missing)


def door_groups(geo):
    d = {g['name']: len(g['idx']) // 3 for g in geo['groups'] if 'door' in g['name'].lower()}
    empty = sorted(n for n, t in d.items() if t <= 0)
    return dict(ok=not empty and len(d) > 0, tris=d, empty=empty)


def rasterize_group_cells(geo, group):
    cells = set()
    for g in geo['groups']:
        if g['name'] != group:
            continue
        V, I = g['verts'], g['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2])
            x0, x1 = min(xs), max(xs); z0, z1 = min(zs), max(zs)
            nx_s = max(1, int((x1 - x0) / SAMPLE) + 1)
            nz_s = max(1, int((z1 - z0) / SAMPLE) + 1)
            for i in range(nx_s):
                for j in range(nz_s):
                    px = x0 + (x1 - x0) * ((i + 0.5) / nx_s)
                    pz = z0 + (z1 - z0) * ((j + 0.5) / nz_s)
                    cells.add(bm_cell(geo, px, pz))
    return cells


def up_face_by_cell(geo):
    """每格最高的朝上面高度（= 人能站到的那一层）—— 与 rebuild-blockers.py 同采样。"""
    out = {}
    for g in geo['groups']:
        V, I = g['verts'], g['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            if tri_normal(a, b, c)[1] < FLOOR_MIN_NY:
                continue
            xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2]); ys = (a[1], b[1], c[1])
            x0, x1 = min(xs), max(xs); z0, z1 = min(zs), max(zs)
            yy = max(ys)
            nx_s = max(1, int((x1 - x0) / SAMPLE) + 1)
            nz_s = max(1, int((z1 - z0) / SAMPLE) + 1)
            for i in range(nx_s):
                for j in range(nz_s):
                    px = x0 + (x1 - x0) * ((i + 0.5) / nx_s)
                    pz = z0 + (z1 - z0) * ((j + 0.5) / nz_s)
                    key = bm_cell(geo, px, pz)
                    if key not in out or out[key] < yy:
                        out[key] = yy
    return out


def box_stats(geo, bm, gate, geom_all, const):
    """箱子（box.png / box_x.png）三段子判据的实测数字。"""
    up_all = up_face_by_cell(geo)
    out = {}
    for grp in CRATE_GROUPS:
        cells = sorted(rasterize_group_cells(geo, grp))
        walk = [c for c in cells if bm_walk(bm, *c)]
        not_top = [c for c in walk if c not in up_all]
        pairs = 0
        pairs_all = 0
        old_allowed = 0
        bad = []
        for (ix, iz) in cells:
            cx, cz = cell_center(geo, ix, iz)
            top = up_all.get((ix, iz))
            for (jx, jz) in ((ix + 1, iz), (ix - 1, iz), (ix, iz + 1), (ix, iz - 1)):
                if (jx, jz) in cells or not bm_walk(bm, jx, jz):
                    continue
                gx, gz = cell_center(geo, jx, jz)
                gy, _ = geom_all.first_up_face_y(gx, gz, 100.0)
                if gy is None:
                    continue
                pairs_all += 1
                # ── 修前口径（只查位图：CsMap.CanStand 的老实现 / 本工程 ResolveMove 的快速分支）──
                #    目标格位图可走 ⇒ 直接从等级地面走进去（这就是"箱子能穿"）
                if bm_walk(bm, ix, iz):
                    old_allowed += 1
                if top is not None and top <= gy + const['StepUpHeight']:
                    continue          # 箱顶与来路地面同层 ⇒ 走上去合法，不算"穿过"
                pairs += 1
                if gate.clear_9(cx, cz, gy):
                    bad.append(((ix, iz), (jx, jz), round(gy, 2)))
        not_standable = []
        no_floor = []
        for c in walk:
            cx, cz = cell_center(geo, *c)
            ct, cg = geom_all.first_up_face_y(cx, cz, 100.0)
            if ct is None:
                no_floor.append(c)
                continue
            if not gate.clear_at(cx, cz, ct):
                not_standable.append((c, round(ct, 2), cg))
        out[grp] = dict(footprint=len(cells), walkable=len(walk), not_top=len(not_top),
                        pairs=pairs, pairs_all=pairs_all, old_allowed=old_allowed,
                        bad=bad, not_standable=not_standable,
                        no_floor=len(no_floor),
                        ok=(not not_top) and (not bad) and (not not_standable))
    return out


def probe_kind(geom, const, x, z, feet_y):
    """单点身体带复核的**分类版**（数值口径与 <see cref="Gate.clear_at"/> 逐字相同，
    只是把"为什么判挡"说出来）：'ok' / 'solid'（第一交点在身高带里 ⇒ **真有实体**挡路）/
    'void'（该点所在子区域**没有任何世界几何** ⇒ 按保守口径判挡，**不是**有实体）/ 'deep'。

    为什么要分类：`clear_at` 把"外侧是图外虚空"与"上面压着箱子"都返回 False，
    而这两件事的性质完全不同 —— 前者是本工程运行时的**保守口径**（切片U/S 定案，登记为差异即可），
    后者是**真的上不去**（原版也上不去）。不分类就会把两种东西混成一条红行。
    """
    lift = const['GroundCheckDistance']; drop = 8.0
    probe_top = feet_y + const['StandHeight'] + lift
    y, _ = geom.first_up_face_y(x, z, probe_top)
    if y is None:
        y2, _ = geom.first_up_face_y(x, z, 1.0e9)
        return 'void' if y2 is None else 'above'
    if y < probe_top - (const['StandHeight'] + lift + drop):
        return 'deep'
    return 'ok' if y <= feet_y + const['GroundCheckDistance'] else 'solid'


def probe_kind_9(geom, const, x, z, feet_y):
    """中心 + 8 向半径的 9 点分类（与 <see cref="Gate.clear_9"/> 同 9 点）：
    有实体挡路 ⇒ 'solid'（优先报）；否则只要有一点是 void/deep ⇒ 'void'；全通 ⇒ 'ok'。"""
    r = const['PlayerRadius']; d = r * 0.70710678
    pts = [(x, z), (x + r, z), (x - r, z), (x, z + r), (x, z - r),
           (x + d, z + d), (x + d, z - d), (x - d, z + d), (x - d, z - d)]
    kinds = [probe_kind(geom, const, px, pz, feet_y) for (px, pz) in pts]
    if all(k == 'ok' for k in kinds):
        return 'ok'
    if any(k == 'solid' for k in kinds):
        return 'solid'
    return 'void'


def low_obstacle_cells(geo, bm, gate, geom_world, const):
    """**位图判挡、且顶面高差 ∈ (一步台阶, 跳跃可达高度]** 的格 ⇒ 原版能跳上去/站上去。

    ⛔ 2026-09-21（切片AB 定稿）三条口径，**全部用运行时同一套世界几何** `geom_world`
    （= 全部渲染 MeshCollider；`Level/Blockers/Blocker_*` 是 trigger，已被 <see cref="Gate"/> 排除）：

    ① **来路地面 g** = 该格 4 邻居里**位图可走**的格的最高地面（人从哪儿来）；
    ② **顶面 top** = 中心点向下、**只取 g+可达高度+0.5 以内的那一层**（跳起来够得着的那个面）；
    ③ **诊断（⛔ 不参与判定、不剔除候选）**：另算一格"本格真顶面 real"（该格足迹内最高的朝上面，
       <see cref="up_face_by_cell"/> 栅格化，含箱子、不限高）。`real - g > 可达高度` 的那些格
       是"格内还有更高的东西"（箱堆 / 叠箱 / 压条），**疑似不是矮障碍** —— 但这只是**线索**，
       本片（切片AB）**没有**拿它改候选集：判据一律保持原样严格，这些格照旧计入候选、照旧要过
       "来路判挡 / 顶面可站 / 横跨可达"三条。逐格数字交主 agent 裁决口径（见 `策划/差异登记.tsv`）。

    为什么值得记这条线索（切片AB 的残留红格就在这批里）：
      · cell(82,86)   真顶面 2.44 m（沙子混凝土台 + 一个箱子压在上面）—— ② 读成 0.81 m 的矮墙；
      · cell(23,123)  真顶面 5.28 m（军械箱上又叠一个箱，来路 3.25 m ⇒ 高差 2.03 m）；
      · cell(51,108)  真顶面 -0.27 m（军械箱顶再叠箱，来路 -1.74 m ⇒ 高差 1.47 m）—— ② 读成 1.13 m；
      · cell(40,110)  真顶面 4.06 m（矮墙上方还有一道压条，来路 2.84 m ⇒ 高差 1.22 m）；
      这 4 格在 9 点身体带复核里判挡的原因是**真有实体**（不是"外侧虚空"），原版同样上不去。
    ⛔ ③ 用 <see cref="up_face_by_cell"/>（格足迹栅格化），⛔ **不是**玩家 9 点探针圈：
      探针圈会外溢到邻格，把邻格更高的地面/上一级踏板算成本格顶面
      （切片AB 实测：会让 9 格候选的高差从 0.81 抬到 0.90，凭空多出 9 处"跳不过去"的假红）。

    返回 `(候选列表, 疑似非矮障碍的候选列表)`；候选元组 = (ix, iz, cx, cz, g, top, h, grp)。
    """
    apex = const['JumpSpeed'] ** 2 / (2.0 * const['Gravity'])
    real_all = up_face_by_cell(geo)      # ③：全几何、逐格足迹最高朝上面（与"箱子能不能穿"同一次栅格化口径）
    out, suspicious = [], []
    for iz in range(bm['d']):
        for ix in range(bm['w']):
            if bm_walk(bm, ix, iz):
                continue
            cx, cz = cell_center(geo, ix, iz)
            g = None
            for (jx, jz) in ((ix, iz), (ix + 1, iz), (ix - 1, iz), (ix, iz + 1), (ix, iz - 1)):
                if not bm_walk(bm, jx, jz):
                    continue
                y, _ = geom_world.first_up_face_y(*cell_center(geo, jx, jz), 100.0)
                if y is not None and (g is None or y > g):
                    g = y
            if g is None:
                continue
            top, grp = geom_world.first_up_face_y(cx, cz, g + apex + 0.5)
            if top is None:
                continue
            h = top - g
            if not (const['StepUpHeight'] < h <= apex):
                continue
            real = real_all.get((ix, iz))
            if real is not None and real - g > apex + 1e-6:
                # 只**记录**、不剔除：判据保持原样严格（不许为了让数字变绿而放宽候选集）。
                suspicious.append((ix, iz, round(cx, 2), round(cz, 2), round(g, 2), round(top, 2),
                                   round(real, 2), round(real - g, 2)))
            out.append((ix, iz, cx, cz, g, top, h, grp))
    return out, suspicious


def jump_window(const, h):
    """从**站立**起跳后，脚面高于 h 的时间窗（秒）+ 窗内能水平走多远（米）。

    <para>物理解析解，用的都是工程里已有出处的常量：
    <c>CsConst.JumpSpeed</c>（= sqrt(2*800*45.0)*0.0254，出处 pm_shared.c:2596）、
    <c>CsConst.Gravity</c>（= sv_gravity 800*0.0254，出处 hw.dll.orig:0x189e90）、
    水平速度取 <c>CsConst.SpeedKnife</c>（原版持械里最快的移动速度 —— 这是**最有利**的假设，
    用它判"跳得过去"，跳不过去才是真跳不过去）。</para>
    返回 (Δt, t1, t2, reach)。h 高于跳跃峰值时 Δt=0。</summary>
    """
    vy = const['JumpSpeed']; g = const['Gravity']
    disc = vy * vy - 2 * g * h
    if disc <= 0:
        return 0.0, 0.0, 0.0, 0.0
    r = disc ** 0.5
    t1 = (vy - r) / g
    t2 = (vy + r) / g
    return (t2 - t1), t1, t2, (t2 - t1) * const['SpeedKnife']


def low_obstacle_stats(geo, bm, gate, geom_world, const):
    low, suspicious = low_obstacle_cells(geo, bm, gate, geom_world, const)
    blocked = [c for c in low if not gate.clear_9(c[2], c[3], c[4])]
    cleared = [c for c in low if gate.clear_9(c[2], c[3], c[5] + 0.02)]
    #    'solid' = 身高带里真有实体 ⇒ **原版也上不去**（不该算成差异，是候选分类问题）；
    #    'void'  = 有探针点所在子区域没有任何世界几何 ⇒ 运行时**保守口径**判挡（登记为差异）。
    kinds = {c[:2]: probe_kind_9(geom_world, const, c[2], c[3], c[5] + 0.02) for c in low}
    standable = [c for c in low if kinds[c[:2]] == 'ok']
    solid_blocked = [c for c in low if kinds[c[:2]] == 'solid']
    void_blocked = [c for c in low if kinds[c[:2]] == 'void']
    # ── 轨迹断言（"A 点起跳能不能落到 B 点"的直接判据）──────────────────────────
    #    需要跨过的距离 = 障碍宽（1 格）+ 两侧各一个角色半径（身体完全过去才算过去）。
    need = geo['cell'] + 2 * const['PlayerRadius']
    jumps = []
    for c in low:
        dt, t1, t2, reach = jump_window(const, c[6])
        jumps.append(dict(cell=(c[0], c[1]), h=c[6], dt=dt, reach=reach, need=need,
                          ok=(reach >= need)))
    worst = min(jumps, key=lambda j: j['reach'] - j['need']) if jumps else None
    jump_ok = bool(jumps) and all(j['ok'] for j in jumps)
    return dict(count=len(low), blocked_at_grade=len(blocked), clear_at_jump=len(cleared),
                candidates=low, jumps=jumps, need=need,
                worst=worst, jump_ok=jump_ok,
                suspicious=suspicious, standable=standable,
                solid_blocked=solid_blocked, void_blocked=void_blocked,
                h_max=max([c[6] for c in low]) if low else 0.0,
                #    候选必须① 在来路高度真被挡、② 全都跳起来站得住、③ 都能一次跳过去。
                #    残留不达标的那几格走 `策划/差异登记.tsv` 登记，不在这里放行。
                ok=(len(low) > 0 and len(blocked) == len(low) and len(cleared) == len(low)
                    and jump_ok))


def separation_summary(path=SEPARATION_REPORT):
    try:
        with open(path, 'rb') as f:
            txt = f.read().decode('utf-8', 'replace')
    except OSError:
        return dict(ok=False, passed=0, failed=0, path=os.path.relpath(path, ROOT))
    m = re.search(r'SUMMARY PASS=(\d+) FAIL=(\d+)', txt)
    if not m:
        return dict(ok=False, passed=0, failed=0, path=os.path.relpath(path, ROOT))
    p, f = int(m.group(1)), int(m.group(2))
    return dict(ok=(f == 0 and p > 0), passed=p, failed=f, path=os.path.relpath(path, ROOT))


# ============================================================================
#  5. CLI 报告
# ============================================================================
def main():
    quiet = '--quiet' in sys.argv
    const = const_or_die()
    geo = load_geo()
    scene = parse_scene()
    bms = [load_bits(p) for p in BYTES_FILES]
    bm = load_bits(BITMAP)
    geom_all = Geom(geo)
    gate = Gate(const, geom_all)
    ape = const['JumpSpeed'] ** 2 / (2.0 * const['Gravity'])

    results = []

    def say(s=''):
        if not quiet:
            print(s)

    say('# geom-check 报告（判据资产，只读）')
    say('# 常量（读自 Core/CsConst.cs）：PlayerRadius=%.4f StandHeight=%.2f StepUpHeight=%.2f '
        'GroundCheckDistance=%.2f JumpSpeed=%.2f Gravity=%.2f ⇒ 跳跃可达高度=%.4f m'
        % (const['PlayerRadius'], const['StandHeight'], const['StepUpHeight'],
           const['GroundCheckDistance'], const['JumpSpeed'], const['Gravity'], ape))
    say('')

    a1 = colliders_consistency(geo, scene, bms)
    results.append((a1['ok'], 'A1 阻挡盒口径三方一致（geo == .bytes 头 == .bytes 段逐条 AABB == 场景）',
                    'geo blockers=%d | scene Blocker_*=%d | %s'
                    % (a1['geo'], a1['scene'],
                       ' | '.join('%s: head=%d seg=%dB AABB逐条等价=%s'
                                  % (p['file'], p['head'], p['seg'], p['aabb_ok']) for p in a1['per']))))
    say(results[-1][2])

    a2 = scene_groups(geo, scene)
    results.append((a2['ok'], 'A2 有几何的组都上场景',
                    'geo 组(有面)=%d 场景 MeshFilter 节点=%d 缺=%s'
                    % (a2['geo_groups_with_tris'], a2['scene_mesh_nodes'], a2['missing'] or '-')))
    say('A2 ' + results[-1][2])

    a3 = door_groups(geo)
    results.append((a3['ok'], 'A3 门组几何非空',
                    '%d 组：%s' % (len(a3['tris']),
                                   ', '.join('%s(%d tris)' % kv for kv in sorted(a3['tris'].items())))))
    say('A3 门组 %d 个：%s' % (len(a3['tris']),
                              ', '.join('%s(%d)' % kv for kv in sorted(a3['tris'].items()))))

    a4 = box_stats(geo, bm, gate, geom_all, const)
    boxes_ok = all(v['ok'] for v in a4.values())
    for grp, v in a4.items():
        say('A4 %-11s 脚印格=%-4d 位图可走=%-4d 非顶面可走=%-3d | 外→内对总数=%-3d '
            '修前(只查位图)可走对=%-3d 修后(两层判据)可走对=%-3d（高于来路地面的对 %d 中）'
            '顶面站不住=%-2d 中心无地面格=%d'
            % (grp, v['footprint'], v['walkable'], v['not_top'], v['pairs_all'],
               v['old_allowed'], len(v['bad']), v['pairs'],
               len(v['not_standable']), v['no_floor']))
        for ex in v['not_standable'][:5]:
            say('   反例(站不住) cell=%s 中心地面=%s 来自=%s' % ex)
    results.append((boxes_ok, 'A4 箱子挡人（可走格 ⊆ 顶面格；高于来路地面时从等级地面走进箱子全挡；顶面站得住）',
                    '；'.join('%s: 脚印%d 位图可走%d｜外→内对%d（修前可走%d / 修后可走%d）'
                             % (g, v['footprint'], v['walkable'], v['pairs_all'],
                                v['old_allowed'], len(v['bad']))
                             for g, v in sorted(a4.items()))))

    a5 = low_obstacle_stats(geo, bm, gate, geom_all, const)
    w = a5['worst']
    results.append((a5['ok'], 'A5 矮障碍（顶面高差 ∈ (台阶, 跳跃可达]）地面挡 / 跳起通 / 起跳落点可达',
                    '候选=%d 地面挡=%d 跳起通=%d 实体挡=%d 外侧虚空=%d '
                    '最高高差=%.2f m｜轨迹：最紧一处 脚面高于顶面 %.3f s ⇒ 水平可走 %.2f m ≥ 需跨 %.2f m'
                    % (a5['count'], a5['blocked_at_grade'], a5['clear_at_jump'],
                       len(a5['solid_blocked']), len(a5['void_blocked']),
                       a5['h_max'], w['dt'] if w else 0.0, w['reach'] if w else 0.0, a5['need'])))
    say('A5 矮障碍候选 %d 个：地面高度挡住 %d / 跳起高度可通过 %d（最高高差 %.2f m ≤ %.4f m）'
        '｜跳起站不住：真有实体挡 %d / 外侧图外虚空 %d'
        % (a5['count'], a5['blocked_at_grade'], a5['clear_at_jump'], a5['h_max'], ape,
           len(a5['solid_blocked']), len(a5['void_blocked'])))
    for c in a5['void_blocked']:
        say('   跳起站不住（外侧图外虚空 ⇒ 运行时保守口径，登记为差异）cell=(%3d,%3d) 来路=%.2f 顶面=%.2f 组=%s'
            % (c[0], c[1], c[4], c[5], c[7]))
    for c in a5['solid_blocked']:
        say('   跳起站不住（身高带里**真有实体** ⇒ 那上面本来就不是可站面）'
            'cell=(%3d,%3d) 来路=%.2f 顶面=%.2f 组=%s' % (c[0], c[1], c[4], c[5], c[7]))
    if a5['suspicious']:
        say('   疑似非矮障碍（本格足迹内还有更高的面 ⇒ 高差已超可达高度；⛔ 仅线索，未改候选集）：')
        for (ix, iz, cx, cz, g, top, real, hh) in a5['suspicious']:
            say('     cell=(%3d,%3d) xz=(%8.2f,%8.2f) 来路=%6.2f 顶面(够得着)=%6.2f 格内真顶面=%6.2f'
                '（高差 %.2f > %.4f）' % (ix, iz, cx, cz, g, top, real, hh, ape))
    say('   A 点起跳落点可达（解析解，常量出处 Core/CsConst.cs）：需跨 %.2f m（格宽+2×半径）；'
        '最紧一处 h=%.2f m ⇒ 脚面高于顶面的时间窗 %.3f s × SpeedKnife %.1f m/s = %.2f m %s'
        % (a5['need'], w['h'] if w else 0.0, w['dt'] if w else 0.0, const['SpeedKnife'],
           w['reach'] if w else 0.0, 'OK' if a5['jump_ok'] else 'FAIL'))
    for j in a5['jumps']:
        say('     cell=(%3d,%3d) h=%.2f 时间窗=%.3f s 可达=%.2f m 需=%.2f m %s'
            % (j['cell'][0], j['cell'][1], j['h'], j['dt'], j['reach'], j['need'],
               'OK' if j['ok'] else 'FAIL'))
    for (ix, iz, cx, cz, g, top, h, grp) in a5['candidates']:
        say('   cell=(%3d,%3d) xz=(%8.2f,%8.2f) 地面=%6.2f 顶面=%6.2f 高差=%.2f 组=%s'
            % (ix, iz, cx, cz, g, top, h, grp))

    a6 = separation_summary()
    results.append((a6['ok'], 'A6 角色间推开（同格 ⇒ 被推到间距 ≥ 2×PlayerRadius）',
                    'PASS=%d FAIL=%d（%s）' % (a6['passed'], a6['failed'], a6['path'])))

    print('')
    for ok, name, detail in results:
        print('%s %s%s' % ('PASS' if ok else 'FAIL', name, ('' if quiet else '  ' + detail)))
    nfail = sum(1 for ok, _, _ in results if not ok)
    print('===== geom-check: PASS=%d FAIL=%d =====' % (len(results) - nfail, nfail))
    return 1 if nfail else 0


if __name__ == '__main__':
    sys.exit(main())
