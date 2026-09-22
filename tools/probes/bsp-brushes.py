# -*- coding: utf-8 -*-
"""判据资产 · 切片BE：解析原版 `de_dust2.bsp`（GoldSource BSP v30）的 **平面 / 面 / brush 形态**，
回答「原版 B 点上下两段到底是多级台阶，还是一整片连续斜面」。

## 为什么需要它（差异 #66 的处置依据）
片BD 已把**工程侧**的 B 点楼梯量清楚（`tools/probes/bstairs-walkline.txt`）：
  T 侧自 `(-11.5, 29.5)` 起 **10 格 × 1.000 m 连续抬升、每格 +0.333 m、
  `bstairs-walkline.cs` 量出的地面法线 y 恒 0.949（≈18.4°）** ⇒ 工程侧是**一整片斜楔（连续斜面）**。
但「原版长什么样」一直没有载体：`tools/probes/` 里只有 `bsp-entities.py`（实体表）与
`bsp-miptex-*.py`（贴图），**没有 planes/faces 级解析器** ⇒ 差异 #66 只能写"表征不同"。
本脚本补上这一层：把原版那两段的 **面形态数字**（踏面级数 / 每级高宽 / 总高 或 斜面倾角）
与工程侧那 10 格 × 0.333 m 并排成表。

## 格式依据（出处，⛔ 不是"看着像"）
* lump 表：`de_dust2.bsp` 偏移 0 = int32 `ver=30`，其后 15 个 `(offset:i32, len:i32)` 对。
  lump 顺序 = `entities, planes, miptex, vertexes, visibility, nodes, texinfo, faces,
  lighting, clipnodes, leafs, marksurfaces, edges, surfedges, models`。
  同族做法出处：既有判据资产 `tools/probes/bsp-entities.py:13-16`、`bsp-miptex-extract.py:30-46`。
* 各 lump 逐结构（Quake/GoldSrc BSP30 规范；与 HLSDK 的几何口径同一套）：
  * lump[1] `planes`   : 20 B/条 = `normal(3×f32) + dist(f32) + type(i32)`；平面方程 `n·p = dist`，
    **凸体内部 = `n·p ≤ dist`**（这是本脚本做 brush 凸体还原的唯一几何公理）。
  * lump[3] `vertexes`: 12 B/条 = `pos(3×f32)`。
  * lump[6] `texinfo` : 40 B/条 = `vecs[2][4](8×f32) + miptex(i32 @+32) + flags(i32)`。
  * lump[7] `faces`   : 20 B/条 = `planenum(u16) + side(u16) + firstedge(i32) + numedges(u16)
                        + texinfo(u16) + styles[4](4×u8) + lightofs(i32)`。
  * lump[12] `edges`    : 4 B/条 = `v[2](2×u16)`。
  * lump[13] `surfedges`: 4 B/条 = `i32`；`se ≥ 0 ⇒ edges[se].v[0]`，`se < 0 ⇒ edges[-se].v[1]`
    （BSP 用"负数索引 = 反向边走法"表示面环方向）。
  * **面法线**：`side == 0 ⇒ n = planes[planenum].normal`；`side == 1 ⇒ n = -normal`
    （面在平面的**另一侧**，磨点 `dist` 同步取反）。面顶点按 surfedge 环顺序取，天然是**逆时针**环。
  * lump[2] `miptex` 名表：`i32 n` + `n × i32 offset`（相对 lump 起点；`-1` = 无名），
    同名解析见既有 `bsp-miptex-extract.py:34-46`。
* 坐标换算（原版 unit → 本工程 m）：`our_x = (orig_x + 384) × 0.0254`、
  `our_z = (orig_y − 1120) × 0.0254`、`our_y = orig_z × 0.0254`。
  出处：`策划/对照表.md` G-02/G-03（世界包围盒 X/Z 跨度逐 unit 一致、仅原点平移）+ 同表
  §单位换算 `GoldSrc 1 unit = 1 inch = 0.0254 m`（`原版资源/cs16src/cs16_build.py:40` `HL_UNIT`）。
  自检：本脚本把它套在**工程侧已知的 10 格斜楔**上，反算出的原版区间应恰好落在 B 点隧道口
  （`x ≈ -1211 … -817`、`y ≈ 2281`），见 `--region` 输出行。

## 判据口径（形状怎么判，别"看着像"）
1. **水平踏面**：面法线 `|n.z| ≥ 0.999` ⇒ 水平；按 `z`（高度）聚类成"级"。
   * 若干个**不同高度的水平面 + 它们之间的竖直踢面** = **多级台阶**；
   * 判据数字：级数、每级高度 `Δz`、每级踏面尺寸（沿行走方向的进深 × 垂直方向的宽度）、总高。
2. **斜面**：`0.70 ≤ n.z ≤ 0.999` ⇒ 可站立斜面（本工程 `CsConst.MaxStandableSlopeNormalZ` 同量级），
   倾角 = `acos(n.z)`。整段只有一个平面（同 planenum/side 的合并面积 ≈ 该段全部踏面面积）= **连续斜面**。
3. **面板段分类**（每个面，`n.z = 法线的竖直分量`）：
   `地面/踏面 |n.z|≥0.999` · `斜面 0.7≤n.z<0.999` · `天花板 n.z≤-0.999` · `竖直墙 |n.z|≤0.01` · `其它`。
4. **brush 还原**（要"每级是一个 brush"这种结论时必须做）：GoldSrc 的 brush = 若干平面的**凸交集**。
   做法 = 从种子面出发，把与当前凸体顶点共享顶点的面的平面不断并进来，直到半空间交集**有界**；
   然后**剪掉冗余平面**（在该凸体上不足 3 个顶点的平面），最后**逐面复核**（每个声称属于该 brush 的面
   的每个顶点都得满足全部平面 `n·p ≤ dist + eps`）。复核通过 ⇒ 该平面集**确实是**一个凸 brush
   （这是可证明的，不是启发式；不通过就报 `--`，不猜）。⛔ 不连续/不凸的组一律不报成 brush。

## 用法（只读 BSP，不改盘）
```
python tools/probes/bsp-brushes.py                       # 默认：B 点 T 侧 + CT 侧两个窗口
python tools/probes/bsp-brushes.py --region T --region CT
python tools/probes/bsp-brushes.py --box -1400 -700 2100 2500 -220 80   # 自定义原版坐标窗口
python tools/probes/bsp-brushes.py --scan               # 全图：所有斜面补丁 + 所有"台阶梯"普查
python tools/probes/bsp-brushes.py --map                # 打印坐标换算自检（工程点 -> 原版点）
```
"""
import math
import os
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BSP = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2.bsp')
OUT_TXT = os.path.join(ROOT, 'tools', 'probes', 'bsp-brushes.txt')


class _Tee(object):
    """控制台 + 判据资产 txt 双写（控制台是 GBK，非 ASCII 会抛；txt 恒 UTF-8 无 BOM）。"""

    def __init__(self, path):
        self.f = open(path, 'w', encoding='utf-8', newline='\n')

    def write(self, s):
        try:
            _ORIG.write(s)
        except Exception:
            _ORIG.write(s.encode('ascii', 'replace').decode('ascii'))
        self.f.write(s)

    def flush(self):
        self.f.flush()


_ORIG = sys.stdout
sys.stdout = _Tee(OUT_TXT)

HL_UNIT = 0.0254            # 出处：策划/对照表.md §单位换算（原版资源/cs16src/cs16_build.py:40 HL_UNIT）
OX, OY, OZ = 384.0, 0.0, 1120.0   # 出处：策划/对照表.md G-02/G-03（原点平移量，unit）
EPS = 2.0e-3

# 工程侧数字（出处：tools/probes/bstairs-walkline.txt 的 (D) 段，片BD）
OURS_T = dict(cells=10, step=1.000, dz=0.333, nz=0.949, slant_deg=18.4,
              run_lo=(-20.5, 29.5), run_hi=(-11.5, 29.5), bottom=(-10.5, -2.523, 31.5),
              top=(-29.5, 0.0, 39.5))
OURS_CT = dict(cells=24, step=1.000, dz=0.024, nz=0.942, slant_deg=7.0,
               bottom=(-19.5, -0.127, 33.5), top=(-29.5, 0.0, 39.5))


def our_to_orig(x, y, z):
    """工程 m -> 原版 unit。我们的 x=(ox+384)*.0254、z=(oy-1120)*.0254、y=oz*.0254。"""
    return (x / HL_UNIT - OX, z / HL_UNIT + OZ, y / HL_UNIT)


def orig_to_our(x, y, z):
    return ((x + OX) * HL_UNIT, z * HL_UNIT, (y - OZ) * HL_UNIT)


def F(v):
    return ('%.3f' % v).rstrip('0').rstrip('.') if abs(v) < 1e6 else str(v)


# ─────────────────────────────── 解析 ───────────────────────────────
class Plane(object):
    __slots__ = ('n', 'd', 't')

    def __init__(self, n, d, t):
        self.n, self.d, self.t = n, d, t


class Face(object):
    __slots__ = ('idx', 'planenum', 'side', 'firstedge', 'numedges', 'texinfo', 'tex',
                 'n', 'd', 'verts', 'area', 'centroid', 'bb_min', 'bb_max')


def parse(path):
    d = open(path, 'rb').read()
    ver = struct.unpack_from('<i', d, 0)[0]
    assert ver == 30, 'not GoldSource BSP30 (ver=%d)' % ver
    L = [struct.unpack_from('<ii', d, 4 + 8 * i) for i in range(15)]

    # lump[1] planes
    o, ln = L[1]
    planes = []
    for i in range(ln // 20):
        nx, ny, nz, dd, ty = struct.unpack_from('<4fi', d, o + 20 * i)
        planes.append(Plane((nx, ny, nz), dd, ty))

    # lump[3] vertexes
    o, ln = L[3]
    verts = [struct.unpack_from('<3f', d, o + 12 * i) for i in range(ln // 12)]

    # lump[12] edges / lump[13] surfedges
    o, ln = L[12]
    edges = [struct.unpack_from('<2H', d, o + 4 * i) for i in range(ln // 4)]
    o, ln = L[13]
    surfedges = [struct.unpack_from('<i', d, o + 4 * i)[0] for i in range(ln // 4)]

    # lump[2] miptex 名
    o, ln = L[2]
    ntex = struct.unpack_from('<i', d, o)[0]
    names = []
    for i in range(ntex):
        mo = struct.unpack_from('<i', d, o + 4 + 4 * i)[0]
        names.append(None if mo < 0 else d[o + mo:o + mo + 16].split(b'\0')[0].decode('latin-1'))

    # lump[6] texinfo -> miptex 名
    o, ln = L[6]
    texname = []
    for i in range(ln // 40):
        mi = struct.unpack_from('<i', d, o + 40 * i + 32)[0]
        texname.append(names[mi] if 0 <= mi < len(names) else None)

    # lump[7] faces
    o, ln = L[7]
    faces = []
    for i in range(ln // 20):
        pn, side, fe, ne, ti = struct.unpack_from('<HHiHH', d, o + 20 * i)
        f = Face()
        f.idx, f.planenum, f.side, f.firstedge, f.numedges = i, pn, side, fe, ne
        f.texinfo = ti
        f.tex = texname[ti] if ti < len(texname) else None
        p = planes[pn]
        f.n = p.n if side == 0 else (-p.n[0], -p.n[1], -p.n[2])
        f.d = p.d if side == 0 else -p.d
        vs = []
        for k in range(ne):
            se = surfedges[fe + k]
            v = edges[se][0] if se >= 0 else edges[-se][1]
            vs.append(verts[v])
        f.verts = vs
        f.centroid = (sum(v[0] for v in vs) / len(vs), sum(v[1] for v in vs) / len(vs),
                      sum(v[2] for v in vs) / len(vs))
        f.bb_min = (min(v[0] for v in vs), min(v[1] for v in vs), min(v[2] for v in vs))
        f.bb_max = (max(v[0] for v in vs), max(v[1] for v in vs), max(v[2] for v in vs))
        # 面积（三角扇）
        a = 0.0
        for k in range(1, len(vs) - 1):
            u = [vs[k][j] - vs[0][j] for j in range(3)]
            w = [vs[k + 1][j] - vs[0][j] for j in range(3)]
            cx = u[1] * w[2] - u[2] * w[1]
            cy = u[2] * w[0] - u[0] * w[2]
            cz = u[0] * w[1] - u[1] * w[0]
            a += 0.5 * math.sqrt(cx * cx + cy * cy + cz * cz)
        f.area = a
        faces.append(f)
    return dict(ver=ver, lumps=L, planes=planes, verts=verts, edges=edges,
                surfedges=surfedges, names=names, texname=texname, faces=faces)


M = parse(BSP)


def in_box(p, box):
    (x0, x1, y0, y1, z0, z1) = box
    return x0 <= p[0] <= x1 and y0 <= p[1] <= y1 and z0 <= p[2] <= z1


def kind(f):
    nz = f.n[2]
    if nz >= 0.999:
        return 'TREAD'      # 水平踏面（朝上）
    if nz <= -0.999:
        return 'CEIL'
    if 0.70 <= nz < 0.999:
        return 'SLOPE'      # 可站立斜面
    if abs(nz) <= 0.01:
        return 'WALL'       # 竖直面（墙 / 踢面）
    return 'OTHER'


def kkey(f):
    return (f.planenum, f.side)


def dot3(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def order_faces(fs):
    """面索引排序后隔 4 项省 1 行（仅打印用；行首标注 [fi] 数字为跳过的面索引）。"""
    return sorted(fs, key=lambda f: -f.area)


def region_faces(box, skip_sky=True):
    out = []
    for f in M['faces']:
        if not in_box(f.centroid, box):
            continue
        if skip_sky and f.tex and (f.tex.startswith('sky') or f.tex in ('clip', 'aaatrigger')):
            continue
        out.append(f)
    return out


# ─────────────────── brush 还原（半空间交集 + 可证明复核）───────────────────
def solve3(ps):
    """三个平面 -> 交点；行列式太小返回 None。"""
    (n1, d1), (n2, d2), (n3, d3) = ps
    a = ((n1[0], n1[1], n1[2], d1), (n2[0], n2[1], n2[2], d2), (n3[0], n3[1], n3[2], d3))
    det = (a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1])
           - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0])
           + a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]))
    if abs(det) < 1e-9:
        return None
    x = []
    for c in range(3):
        b = [a[r][3] for r in range(3)]
        mm = [[a[r][k] if k != c else b[r] for k in range(3)] for r in range(3)]
        dd = (mm[0][0] * (mm[1][1] * mm[2][2] - mm[1][2] * mm[2][1])
              - mm[0][1] * (mm[1][0] * mm[2][2] - mm[1][2] * mm[2][0])
              + mm[0][2] * (mm[1][0] * mm[2][1] - mm[1][1] * mm[2][0]))
        x.append(dd / det)
    return tuple(x)


def polyhedron(pls):
    """半空间交集 {p : n_i·p ≤ d_i} 的顶点 + 是否有界。"""
    pts = []
    if len(pls) < 4:            # 少于此数不可能围出有界体（锥/楔必开放）
        return pts, True
    for i in range(len(pls)):
        for j in range(i + 1, len(pls)):
            for k in range(j + 1, len(pls)):
                p = solve3((pls[i], pls[j], pls[k]))
                if p is None:
                    continue
                if all(dot3(n, p) <= dd + EPS for (n, dd) in pls):
                    if not any(abs(p[0] - q[0]) < 1e-3 and abs(p[1] - q[1]) < 1e-3
                               and abs(p[2] - q[2]) < 1e-3 for q in pts):
                        pts.append(p)
    unbounded = False
    for i in range(len(pls)):
        for j in range(i + 1, len(pls)):
            n1, n2 = pls[i][0], pls[j][0]
            r = (n1[1] * n2[2] - n1[2] * n2[1], n1[2] * n2[0] - n1[0] * n2[2],
                 n1[0] * n2[1] - n1[1] * n2[0])
            for s in (r, (-r[0], -r[1], -r[2])):
                if max(abs(s[0]), abs(s[1]), abs(s[2])) < 1e-9:
                    continue
                if all(dot3(n, s) <= 1e-6 for (n, _) in pls):
                    unbounded = True
                    break
            if unbounded:
                break
        if unbounded:
            break
    return pts, unbounded


def on_plane(p, pl, tol=2.0e-3):
    return dot3(pl[0], p) <= pl[1] + tol


def neighbourhood(seed, all_faces, maxn=600):
    """与 seed **共享至少一个顶点** 的面（= 可能是同一个 brush 的面）。BSP 顶点是焊接过的，可直接比坐标。"""
    key = set((round(v[0], 2), round(v[1], 2), round(v[2], 2)) for v in seed.verts)
    out = [seed]
    for f in all_faces:
        if f.idx == seed.idx:
            continue
        if any((round(v[0], 2), round(v[1], 2), round(v[2], 2)) in key for v in f.verts):
            out.append(f)
            if len(out) >= maxn:
                break
    return out


def is_empty(pls):
    """半空间交集是否**可证为空**（找不到满足全部平面的三元交点）。"""
    if len(pls) < 3:
        return False
    for i in range(len(pls)):
        for j in range(i + 1, len(pls)):
            for k in range(j + 1, len(pls)):
                p = solve3((pls[i], pls[j], pls[k]))
                if p is None:
                    continue
                if all(dot3(n, p) <= dd + EPS for (n, dd) in pls):
                    return False
    return True


def grow_brush(seed, pool):
    """从 seed 面出发，**逐个**并入"与当前凸体共享顶点"的平面（并后交集必须非空），
    直到有界；再剪冗余 + 逐面复核。

    ⛔ 不许一次并一堆：并多了交集会变空 ⇒ 半空间判定会把"空集"误判成"开放"（实测踩过）。
    """
    keys = [kkey(seed)]
    pls = [((seed.n), seed.d)]
    while True:
        pts, unb = polyhedron(pls)
        if not unb:
            break
        verts = set((round(p[0], 2), round(p[1], 2), round(p[2], 2)) for p in pts)
        verts |= set((round(v[0], 2), round(v[1], 2), round(v[2], 2)) for v in seed.verts)
        cand = [f for f in pool if kkey(f) not in keys
                and any((round(v[0], 2), round(v[1], 2), round(v[2], 2)) in verts for v in f.verts)]
        if not cand:
            return None, 'unbounded(open)'
        # 按"并进来后凸体体积最大"优先，避免先并一个把 brush 切掉的平面
        cand.sort(key=lambda f: -f.area)
        took = False
        for f in cand:
            # 接受条件（两条缺一不可）：
            #   ① 并进来后**不切掉种子面**（真 brush 的每个平面都以整个 brush 为内侧 ⇒ 种子面必在内）
            #   ② 并进来后交集**不是空的**（并多了会把 brush 切没，此时半空间会退化成"开放"的假象）
            if not all(on_plane(v, (f.n, f.d)) for v in seed.verts):
                continue
            trial = pls + [(f.n, f.d)]
            if is_empty(trial):
                continue
            pls = trial
            keys.append(kkey(f))
            took = True
            break
        if not took:
            return None, 'unbounded(open)'
        if len(pls) > 40:
            return None, 'unbounded(too-many-planes)'
    # 剪冗余平面
    keep = [pl for pl in pls if sum(1 for p in pts if abs(dot3(pl[0], p) - pl[1]) < 2e-3) >= 3]
    if len(keep) < 4:
        return None, 'degenerate'
    kpts, kunb = polyhedron(keep)
    if kunb or not kpts:
        return None, 'unbounded(pruned)'
    # 复核：池里每个平面键落在 keep 里的面，其全部顶点必须满足 keep 的所有平面
    members = []
    for f in pool:
        if (f.n, f.d) not in keep:
            continue
        ok = all(on_plane(v, pl) for v in f.verts for pl in keep)
        if not ok:
            return None, 'face-outside'
        members.append(f)
    if not any(f.idx == seed.idx for f in members):
        return None, 'seed-lost'
    return (keep, kpts, members), 'ok'


def brush_dims(keep, kpts):
    mn = [min(p[i] for p in kpts) for i in range(3)]
    mx = [max(p[i] for p in kpts) for i in range(3)]
    return mn, mx, tuple(mx[i] - mn[i] for i in range(3))


# ─────────────── 逐米剖面：原版地面 y vs 我们的地面 y（同一条线）───────────────
def z_on_face(f, x, y):
    """面 f 上 (x,y) 对应的 z；不在该面平面足迹内返回 None（面是凸多边形，叉积同号即在内）。"""
    n = f.n
    if abs(n[2]) < 1e-6:
        return None
    z = (f.d - n[0] * x - n[1] * y) / n[2]
    pts = [(v[0], v[1]) for v in f.verts]
    sgn = 0
    for i in range(len(pts)):
        ax, ay = pts[i]
        bx, by = pts[(i + 1) % len(pts)]
        cr = (bx - ax) * (y - ay) - (by - ay) * (x - ax)
        if abs(cr) < 1e-4:
            continue
        s = 1 if cr > 0 else -1
        if sgn == 0:
            sgn = s
        elif s != sgn:
            return None
    return z


def profile_line(label, pts, ours_pts, step=0.5):
    """沿工程坐标折线逐 0.5 m 采样：原版几何里该点上方所有 n.z>=0.7 的面 + 与我们的对照。

    pts      = [(工程 x, 工程 z), ...] 折线（采样点从 pts[0] 走到 pts[-1]，按弧长等距）
    ours_pts = [(沿线的 s(m), 我们的地面 y(m)), ...] 或 None
    """
    xs = [p[0] for p in pts]; zs = [p[1] for p in pts]
    box = (min(xs) - 2, max(xs) + 2, min(zs) - 2, max(zs) + 2)   # 工程 m 口径
    cands = []
    for f in M['faces']:
        if f.n[2] < 0.70 or (f.tex and f.tex.startswith('sky')):
            continue
        fx0 = f.bb_min[0] * HL_UNIT + OX * HL_UNIT     # 原版 unit -> 工程 m（x）
        fx1 = f.bb_max[0] * HL_UNIT + OX * HL_UNIT
        fz0 = f.bb_min[1] * HL_UNIT - OZ * HL_UNIT     # 原版 unit -> 工程 m（z）
        fz1 = f.bb_max[1] * HL_UNIT - OZ * HL_UNIT
        if fx1 < box[0] or fx0 > box[1] or fz1 < box[2] or fz0 > box[3]:
            continue
        cands.append(f)
    # 折线总长
    segs = []
    total = 0.0
    for a, b in zip(pts, pts[1:]):
        L = math.hypot(b[0] - a[0], b[1] - a[1])
        segs.append((a, b, L)); total += L
    print('')
    print('  -- 逐米剖面 %s（沿线 %d 点，步长 %.2f m，总长 %.2f m）--'
          % (label, int(total / step) + 1, step, total))
    print('   %-8s %-9s %-8s %-30s %-26s %-10s %s'
          % ('s(m)', 'x(our)', 'z(our)', '原版可站面 y(unit) [面索引]', '匹配面 n.z/倾角', '原版 y(m)', '我们 y(m) / Δ(m)'))
    s = 0.0
    while s <= total + 1e-6:
        # 折线上 s 处的点
        acc = 0.0; px = pz = 0.0
        for a, b, L in segs:
            if s <= acc + L + 1e-9:
                t = 0.0 if L < 1e-9 else (s - acc) / L
                px = a[0] + (b[0] - a[0]) * t; pz = a[1] + (b[1] - a[1]) * t
                break
            acc += L
        ox, oy = px / HL_UNIT - OX, pz / HL_UNIT + OZ
        hits = []
        for f in cands:
            z = z_on_face(f, ox, oy)
            if z is not None:
                hits.append((z, f))
        hits.sort(key=lambda t: t[0])
        ours = None
        if ours_pts:
            for (ss, yy) in ours_pts:
                if abs(ss - s) < step * 0.51:
                    ours = yy
        best = None
        if ours is not None:
            tgt = ours / HL_UNIT
            best = min(hits, key=lambda t: abs(t[0] - tgt)) if hits else None
        ztxt = ' '.join('%.1f[%d]' % (z, f.idx) for z, f in hits) if hits else '-'
        stxt = ('plane[%d] n.z=%.4f/%.2f deg' % (best[1].planenum, best[1].n[2],
                math.degrees(math.acos(max(-1.0, min(1.0, best[1].n[2])))))) if best else '-'
        ytxt = '%.3f' % (best[0] * HL_UNIT) if best else '-'
        dtxt = '%.3f / %+.3f' % (ours, ours - best[0] * HL_UNIT) if (ours is not None and best) else '-'
        print('   %-8.2f %-9.2f %-8.2f %-30s %-26s %-10s %s' % (s, px, pz, ztxt, stxt, ytxt, dtxt))
        s += step
    return cands


# ─────────────────────────────── 报告 ───────────────────────────────
def report_region(name, box, ours, prof=None, ours_prof=None, prof_label='', prof_step=0.5):
    fs = region_faces(box)
    print('')
    print('=== 区域 %s  原版窗口 x[%d,%d] y[%d,%d] z[%d,%d] (unit) ==='
          % (name, box[0], box[1], box[2], box[3], box[4], box[5]))
    print('  工程对照：我们的值 = %s' % (
        ('%d 格 × %s m 连续抬升 / 每格 +%s m / 地面法线 y=%s（≈%s°）'
         % (ours['cells'], F(ours['step']), F(ours['dz']), F(ours['nz']), F(ours['slant_deg'])))
        if ours else '-'))
    if not fs:
        print('  ⚠ 该窗口内无面（窗口写错了？）')
        return []
    by = {}
    for f in fs:
        by.setdefault(kind(f), []).append(f)
    print('  面数=%d  %s' % (len(fs), '  '.join('%s=%d' % (k, len(v)) for k, v in sorted(by.items()))))

    # ── 水平踏面按高度聚类 = "级" ──
    treads = by.get('TREAD', [])
    levels = {}
    for f in treads:
        z = round(f.bb_min[2], 1)          # 0.1 unit ≈ 2.5 mm ≈ 工程 2.5 mm
        levels.setdefault(z, []).append(f)
    print('')
    print('  -- (1) 水平踏面按高度聚类（%d 级候选）--' % len(levels))
    print('   %-4s %-9s %-9s %-8s %-22s %-22s %-8s %s'
          % ('级', 'z(unit)', 'y(m)', '面数', 'x 跨度(unit)', 'y 跨度(unit)', '面积', '贴图'))
    order = sorted(levels.items())
    prev = None
    for i, (z, fl) in enumerate(order):
        x0 = min(f.bb_min[0] for f in fl); x1 = max(f.bb_max[0] for f in fl)
        y0 = min(f.bb_min[1] for f in fl); y1 = max(f.bb_max[1] for f in fl)
        dz = '-' if prev is None else '%+.1f unit / %+.3f m' % (z - prev, (z - prev) * HL_UNIT)
        texs = sorted(set(f.tex or '?' for f in fl))
        print('   %-4d %-9.1f %-9.4f %-8d [%.0f,%.0f]           [%.0f,%.0f]           %-8.1f %s  Δ=%s'
              % (i + 1, z, z * HL_UNIT, len(fl), x0, x1, y0, y1,
                 sum(f.area for f in fl), ','.join(texs[:2]), dz))
        prev = z
    if order:
        print('   级数=%d  总高=%.1f unit = %.3f m（最低 %.1f -> 最高 %.1f unit）'
              % (len(order), order[-1][0] - order[0][0], (order[-1][0] - order[0][0]) * HL_UNIT,
                 order[0][0], order[-1][0]))

    # ── 斜面 ──
    slopes = by.get('SLOPE', [])
    print('')
    print('  -- (2) 可站立斜面（0.70 ≤ n.z < 0.999）--')
    if not slopes:
        print('   （无）')
    pl_groups = {}
    for f in slopes:
        pl_groups.setdefault(kkey(f), []).append(f)
    for (pn, side), fl in sorted(pl_groups.items(), key=lambda kv: -sum(f.area for f in kv[1])):
        n = fl[0].n
        ang = math.degrees(math.acos(max(-1.0, min(1.0, n[2]))))
        x0 = min(f.bb_min[0] for f in fl); x1 = max(f.bb_max[0] for f in fl)
        y0 = min(f.bb_min[1] for f in fl); y1 = max(f.bb_max[1] for f in fl)
        z0 = min(f.bb_min[2] for f in fl); z1 = max(f.bb_max[2] for f in fl)
        dx = (x1 - x0) * HL_UNIT; dy = (y1 - y0) * HL_UNIT; dz = (z1 - z0) * HL_UNIT
        print('   plane[%d]side=%d  n=(%.4f,%.4f,%.4f)  n.z=%.4f  => 倾角 %.2f deg  面积 %.0f unit^2  面数 %d 贴图 %s'
              % (pn, side, n[0], n[1], n[2], n[2], ang, sum(f.area for f in fl), len(fl),
                 ','.join(sorted(set(f.tex or '?' for f in fl))[:2])))
        print('        足迹 x[%.1f,%.1f] y[%.1f,%.1f] unit  =  %.3f × %.3f m ；高差 %.1f unit = %.3f m'
              % (x0, x1, y0, y1, dx, dy, z1 - z0, dz))

    # ── 竖直面（踢面 / 墙）里"朝行走方向"的那些 ──
    walls = by.get('WALL', [])
    print('')
    print('  -- (3) 竖直面（踢面 / 墙）--  共 %d 个面，取面积前 12 --' % len(walls))
    for f in order_faces(walls)[:12]:
        print('   face[%d] plane[%d]s=%d n=(%.2f,%.2f,%.2f) 面积 %.0f 足迹 x[%.0f,%.0f] y[%.0f,%.0f] z[%.0f,%.0f] %s'
              % (f.idx, f.planenum, f.side, f.n[0], f.n[1], f.n[2], f.area,
                 f.bb_min[0], f.bb_max[0], f.bb_min[1], f.bb_max[1], f.bb_min[2], f.bb_max[2], f.tex))

    # ── brush 还原 ──
    print('')
    print('  -- (4) brush 还原（半空间交集，逐面可证明复核）--')
    all_faces = [f for f in M['faces']
                 if not (f.tex and (f.tex.startswith('sky') or f.tex in ('clip', 'aaatrigger')))]
    done = set()
    brushes = []
    for seed in sorted(fs, key=lambda f: -f.area):
        if seed.idx in done:
            continue
        pool = neighbourhood(seed, all_faces)
        res, why = grow_brush(seed, pool)
        if res is None:
            print('   seed face[%d] (%s) ⇒ 无法闭合成凸体：%s（**不报成 brush**）'
                  % (seed.idx, kind(seed), why))
            done.add(seed.idx)
            continue
        keep, kpts, members = res
        mn, mx, dim = brush_dims(keep, kpts)
        nz = max(p[0][2] for p in keep)
        typ = '台阶(盒)' if nz >= 0.999 and len(keep) <= 8 else ('斜面楔' if 0.70 <= nz < 0.999 else '体')
        brushes.append((keep, mn, mx, dim, members, typ))
        for m in members:
            done.add(m.idx)
        print('   brush %-2d 平面数=%-2d 面数=%-2d 类型=%-6s 尺寸(x,y,z)=%.1f×%.1f×%.1f unit = %.3f×%.3f×%.3f m'
              % (len(brushes), len(keep), len(members), typ,
                 dim[0], dim[1], dim[2], dim[0] * HL_UNIT, dim[1] * HL_UNIT, dim[2] * HL_UNIT))
        print('           世界盒 x[%.0f,%.0f] y[%.0f,%.0f] z[%.0f,%.0f] unit  ⇒ y[%.3f,%.3f] m'
              % (mn[0], mx[0], mn[1], mx[1], mn[2], mx[2], mn[2] * HL_UNIT, mx[2] * HL_UNIT))
        print('           面: %s' % ' '.join('f[%d]%s' % (m.idx, kind(m)) for m in sorted(members, key=lambda x: x.idx)))
    if brushes:
        nzlist = sorted(set(round(b[3][2] * HL_UNIT, 3) for b in brushes))
        print('   ⇒ 区域 %s：brush 数=%d；逐个 brush 的竖直尺寸(m)=%s' % (name, len(brushes), nzlist))

    # ── 逐米剖面（本脚本的主证据：原版 vs 我们，同一条线）──
    if prof:
        profile_line(prof_label or name, prof, ours_prof, step=prof_step)

    # ── 结论 ──
    print('')
    print('  -- (5) 形态结论 --')
    lv = sorted(levels.items())
    gaps = [lv[i + 1][0] - lv[i][0] for i in range(len(lv) - 1)]
    print('   水平踏面高度层数=%d；相邻层高差(unit)=%s' % (len(lv), [round(g, 1) for g in gaps]))
    if len(lv) >= 3 and all(2.0 <= g <= 26.0 for g in gaps):
        print('   ⇒ **多级台阶**（%d 级；每级 %.1f unit = %.3f m；总高 %.1f unit = %.3f m）'
              % (len(lv), sum(gaps) / len(gaps), sum(gaps) / len(gaps) * HL_UNIT,
                 lv[-1][0] - lv[0][0], (lv[-1][0] - lv[0][0]) * HL_UNIT))
    else:
        print('   ⇒ **不是多级台阶**：高度层只有 %d 个（多级台阶要求 >=3 个离散高度层且相邻高差 0.05~0.66 m）'
              % len(lv))
    sl = by.get('SLOPE', [])
    if sl:
        big = max(sl, key=lambda f: f.area)
        ang = math.degrees(math.acos(max(-1.0, min(1.0, big.n[2]))))
        tot = sum(f.area for f in sl)
        print('   ⇒ **存在连续斜面**：最大斜面 plane[%d] 面积 %.0f unit^2（占区域内可站面 %.0f%%），'
              'n.z=%.4f ⇒ 倾角 %.2f deg'
              % (big.planenum, big.area, 100.0 * big.area / max(1e-9, tot), big.n[2], ang))
        if ours:
            print('      与工程侧对照：我们 n.y=%s（%s deg），原版 n.z=%.4f（%.2f deg），**法线差 %.4f / 角度差 %.2f deg**'
                  % (F(ours['nz']), F(ours['slant_deg']), big.n[2], ang, abs(ours['nz'] - big.n[2]),
                     abs(ours['slant_deg'] - ang)))
    return brushes


def map_selfcheck():
    print('=== 坐标换算自检（出处：策划/对照表.md G-02/G-03 + §单位换算 HL_UNIT=0.0254）===')
    for tag, p in (('T 侧楼梯底', (-10.5, -2.523, 31.5)), ('T 侧楼梯顶', (-29.5, 0.0, 39.5)),
                   ('CT 侧楼梯底', (-19.5, -0.127, 33.5))):
        ox, oy, oz = our_to_orig(*p)
        bx, by, bz = orig_to_our(ox, oy, oz)
        print('  %-10s 工程(%.3f,%.3f,%.3f) -> 原版(%.1f,%.1f,%.1f) unit -> 回算(%.3f,%.3f,%.3f) 残差 %.2e'
              % (tag, p[0], p[1], p[2], ox, oy, oz, bx, by, bz,
                 max(abs(bx - p[0]), abs(by - p[1]), abs(bz - p[2]))))


def scan():
    """全图普查：① 所有可站立斜面（按平面合并）② 所有"台阶梯"（水平踏面按高度聚成 2 级以上的簇）。"""
    fs = [f for f in M['faces'] if not (f.tex and (f.tex.startswith('sky') or f.tex in ('clip', 'aaatrigger')))]
    print('=== 全图普查：面 %d（已剔除 sky/clip/aaatrigger）===' % len(fs))
    slopes = {}
    treads = []
    for f in fs:
        k = kind(f)
        if k == 'SLOPE':
            slopes.setdefault(kkey(f), []).append(f)
        elif k == 'TREAD':
            treads.append(f)
    print('')
    print('-- (A) 所有可站立斜面（n.z ≥ 0.70），按平面合并 --')
    print('  %-16s %-8s %-8s %-11s %-11s' % ('plane', 'n.z', '倾角deg', '面积unit^2', '足迹 x*y (m)'))
    for (pn, side), fl in sorted(slopes.items(), key=lambda kv: -sum(f.area for f in kv[1])):
        n = fl[0].n
        ang = math.degrees(math.acos(max(-1.0, min(1.0, n[2]))))
        x0 = min(f.bb_min[0] for f in fl); x1 = max(f.bb_max[0] for f in fl)
        y0 = min(f.bb_min[1] for f in fl); y1 = max(f.bb_max[1] for f in fl)
        print('  plane[%-4d]s=%d  %-8.4f %-8.2f %-11.0f %.2f × %.2f   工程 x/ z: %.2f..%.2f / %.2f..%.2f'
              % (pn, side, n[2], ang, sum(f.area for f in fl),
                 (x1 - x0) * HL_UNIT, (y1 - y0) * HL_UNIT,
                 (x0 + OX) * HL_UNIT, (x1 + OX) * HL_UNIT, (y0 - OZ) * HL_UNIT, (y1 - OZ) * HL_UNIT))
    print('')
    print('-- (B) 台阶梯普查：水平踏面按 (z, 邻近) 聚类，找"连续 2 级以上 + 级距 0.05..0.6 m"的簇 --')
    print('  （判据：同一 x/y 邻域里出现 ≥2 个不同高度的水平踏面，且相邻高差在 0.05..0.6 m）')
    # 粗聚类：按 1 m 网格 (x,y) 分箱，每箱内按 z 去重
    bins = {}
    for f in treads:
        cx = int(math.floor(f.centroid[0] / 38.0)); cy = int(math.floor(f.centroid[1] / 38.0))
        bins.setdefault((cx, cy), {})[round(f.centroid[2])] = True
    ladders = []
    for (cx, cy), zs in bins.items():
        z = sorted(zs)
        run = [z[0]]
        for a, b in zip(z, z[1:]):
            if 2.0 <= (b - a) <= 24.0:      # 2..24 unit = 5..61 cm
                run.append(b)
            else:
                if len(run) >= 2:
                    ladders.append((cx, cy, run))
                run = [b]
        if len(run) >= 2:
            ladders.append((cx, cy, run))
    for cx, cy, run in sorted(ladders, key=lambda t: -len(t[2]))[:25]:
        ox = (cx + 0.5) * 38.0 + 0.0; oy = (cy + 0.5) * 38.0
        print('   格(unit bin ~38) x≈%.0f y≈%.0f  工程 x≈%.2f z≈%.2f  级数=%-3d  z=%s unit  级高=%s unit'
              % (ox, oy, (ox + OX) * HL_UNIT, (oy - OZ) * HL_UNIT, len(run), run,
                 [round((b - a), 1) for a, b in zip(run, run[1:])]))
    print('  （共 %d 个候选台阶簇；上表按级数取前 25）' % len(ladders))
    print('')
    print('-- (C) 汇总 --')
    print('  水平踏面 %d 个 / 可站立斜面 %d 个平面 / 台阶簇 %d 个'
          % (len(treads), len(slopes), len(ladders)))


def main():
    argv = sys.argv[1:]
    if '--scan' in argv:
        # 全图普查另存一份，别覆盖区域报告（两份都是判据资产）
        sys.stdout.f.close()
        sys.stdout.f = open(os.path.join(ROOT, 'tools', 'probes', 'bsp-brushes-scan.txt'),
                            'w', encoding='utf-8', newline='\n')
    if '--map' in argv:
        map_selfcheck()
        if len(argv) == 1:
            return
    if '--scan' in argv:
        scan()
        return

    regions = []
    box = None
    i = 0
    while i < len(argv):
        if argv[i] == '--box':
            box = tuple(float(x) for x in argv[i + 1:i + 7]); i += 7; continue
        if argv[i] == '--region':
            regions.append(argv[i + 1]); i += 2; continue
        i += 1
    if box:
        regions = ['自定义']
    if not regions:
        regions = ['T', 'CT']

    # 窗口 = 工程侧 (D) 段那两段走廊的**原版反算区间 + 2 m 余量**（出处见文件头「坐标换算」）
    def win(cx0, cx1, cz0, cz1, cy0, cy1):
        x0 = cx0 / HL_UNIT - OX; x1 = cx1 / HL_UNIT - OX
        y0 = cz0 / HL_UNIT + OZ; y1 = cz1 / HL_UNIT + OZ
        z0 = cy0 / HL_UNIT; z1 = cy1 / HL_UNIT
        return (math.floor(x0), math.ceil(x1), math.floor(y0), math.ceil(y1),
                math.floor(z0), math.ceil(z1))

    map_selfcheck()
    if box:
        report_region('自定义', box, None)
    else:
        for r in regions:
            if r == 'T':
                b = win(-21.5, -10.5, 26.5, 32.5, -3.6, 0.6)
                # 剖面线 = 工程 (D) 段那条爬坡行（z=29.5 恒定，x -10.5 -> -20.5，1 m/点）
                prof_pts = [(-10.5 - i, 29.5) for i in range(0, 11)]
                ours_prof = [(float(i), v) for i, v in enumerate(
                    [-3.002, -2.669, -2.336, -2.002, -1.669, -1.336, -1.002, -0.669,
                     -0.336, -0.002, 0.000])]
                report_region('B 点 T 侧（工程 (D) 段那段 10 格斜楔）', b, OURS_T,
                              prof=prof_pts, ours_prof=ours_prof, prof_step=1.0,
                              prof_label='T 侧（工程 z=29.5 那一行，x -10.5 -> -20.5，沿 x 每 1 m）')
            elif r == 'CT':
                b = win(-29.0, -18.0, 27.0, 35.0, -0.6, 1.6)
                # 剖面线 = 底 -> 顶直线（与 bstairs-walkline.cs 的 (B)/(C) 同口径）
                prof_pts = [(-19.5 + (-29.5 + 19.5) * (i / 20.0),
                             33.5 + (39.5 - 33.5) * (i / 20.0)) for i in range(21)]
                report_region('B 点 CT 侧（工程 (D) 段 CT 走廊）', b, OURS_CT,
                              prof=prof_pts, prof_label='CT 侧（工程 底(-19.5,33.5) -> 顶(-29.5,39.5) 直线每 0.5 m）')
    print('')
    print('BSP=%s  (%d B, %d faces / %d planes / %d vertexes)'
          % (os.path.relpath(BSP, ROOT).replace('\\', '/'), os.path.getsize(BSP),
             len(M['faces']), len(M['planes']), len(M['verts'])))


if __name__ == '__main__':
    main()
