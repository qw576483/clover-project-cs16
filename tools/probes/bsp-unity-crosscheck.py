#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BSP ↔ 工程几何 交叉对照（只读；不启 Unity、不进 Play）。

用途：回答「原版 `de_dust2.bsp` 在某个世界位置到底有没有**水平可站面**、离相邻地面多高」
这类问题（判据侧取证），并顺带把 BSP 的原始面高与工程几何（`de_dust2_geo.bin`）逐格对照。

为什么需要它：`de_dust2_geo.bin` 是**转换产物**（转换脚本本机不在盘），一旦两边数字对不上，
就必须回到唯一真源 `de_dust2.bsp` 去问。本脚本把这条链固定下来。

坐标变换**不猜、不写死**（GoldSrc 是 z-up、Unity 是 y-up，且转换器做过居中）：
  · 轴映射 = 用 BSP 顶点外接框的 span 去匹配 `de_dust2_geo.bin` 头部自带的世界包围盒；
  · 偏移   = 两个包围盒中心之差；
  · z 符号 = 用 BSP entity lump 的 `${CT/T}` 出生点集合与 geo.bin 的 `Spawn_CT/Spawn_T` 标记
             做集合匹配，取残差为 0 的那个符号。
残差上限 = 标记点吸附噪声（`Dust2Builder.SnapMarkerToWalkable` 把标记挪到格心）≤ 0.7071 m
（0.5 m/轴 的 Chebyshev 上限）；超过它才说明变换本身错 ⇒ exit 2。

自检（跑不过就 exit 2，绝不输出半份结论）：
  1. BSP 版本 == 30；所有 lump 落在文件内；
  2. `sum(face.numedges) == len(surfedges)`（面→边链完整）；
  3. 轴映射对得上（两包围盒 span 差 < 40 u）；
  4. CT 出生点集合匹配残差 ≤ 0.7071 m，且 T 出生点独立复核同样过。

用法
----
    # 逐格：格心穿过哪些面（含法线/miptex）+ 该格 1m² 内的朝上面裁剪面积
    python tools/probes/bsp-unity-crosscheck.py --cell 118,52 --cell 117,55

    # 逐矩形：世界 Unity 米制矩形内朝上面的裁剪面积汇总
    python tools/probes/bsp-unity-crosscheck.py --rect 55,-21,56,-2

    # 只做变换拟合 + 自检，不做区域查询
    python tools/probes/bsp-unity-crosscheck.py

坐标口径：`--cell ix,iz` = 引擎位图格（格心 = `(-63,-72) + (ix+0.5, iz+0.5)`，cell 1.0 m）；
`--rect x0,z0,x1,z1` = Unity 世界米制矩形（XZ）。1 HL unit = 0.0254 m。
"""
import argparse
import importlib.util
import math
import os
import re
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
BSP = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2.bsp")
GEO = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2_geo.bin")
BYTES = os.path.join(ROOT, "client", "Assets", "Resources", "MapData", "de_dust2.bytes")

U = 0.0254            # 1 HL unit（米）
ORIGIN_X, ORIGIN_Z = -63.0, -72.0   # 位图原点（de_dust2.bytes 头部）
SNAP_MAX = 0.7071     # 标记点吸附残差上限（m）

L_ENT, L_PLANE, L_TEX, L_VERT, L_TEXINFO, L_FACE, L_EDGE, L_SURF = 0, 1, 2, 3, 6, 7, 12, 13


def load_module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main():
    """入口包装：把「输入件存在但损坏 / 格式不符」变成**显式判决**，而不是裸 traceback。

    为什么：裸 traceback 与"非零退出"是两回事 —— 判据侧要能一眼读出这是**被拦下的**，
    而不是"脚本崩了"（否则会把"输入不可用"误读成"工具坏了"）。只捕输入/格式类的异常；
    本脚本自身逻辑的 bug（如 TypeError）仍然照常抛 traceback，不被这里吞掉。
    """
    try:
        return _main()
    except (OSError, ValueError, struct.error, IndexError, KeyError, AssertionError) as e:
        print("FAIL: 输入件不可用或格式不符（%s: %s）—— 检查 --bsp/--geo/--bytes 是否为本工程的产物、是否被截断"
              % (type(e).__name__, e), file=sys.stderr)
        return 2


def _main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bsp", default=BSP)
    ap.add_argument("--geo", default=GEO)
    ap.add_argument("--bytes", dest="bytes_path", default=BYTES)
    ap.add_argument("--cell", action="append", help="ix,iz（引擎位图格）")
    ap.add_argument("--rect", action="append", help="x0,z0,x1,z1（Unity 米，XZ）")
    ap.add_argument("--min-area", type=float, default=1e-9, help="面裁剪面积下限（m²），默认 1e-9")
    # 异常路径绝不能反过来吞掉诊断：本机 stdout 默认是 GBK，FAIL 分支里任何 `⇒/⇔` 都会把真实
    # 失败原因换成 UnicodeEncodeError（实测：先看到"拟合失败"的壳，其实是 --rect 传参被当成选项）。
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    a = ap.parse_args()

    # ---------------- 输入存在性（自检 0） ----------------
    for label, p in (("--bsp", a.bsp), ("--geo", a.geo), ("--bytes", a.bytes_path)):
        if not os.path.isfile(p):
            print("FAIL: %s 路径不存在：%s" % (label, p), file=sys.stderr)
            return 2

    # ---------------- BSP 载入 + 自检 1 ----------------
    data = open(a.bsp, "rb").read()
    ver = struct.unpack_from("<i", data, 0)[0]
    if ver != 30:
        print("FAIL: 不是 GoldSource BSP v30（version=%d）" % ver, file=sys.stderr)
        return 2
    lumps = [struct.unpack_from("<ii", data, 4 + 8 * i) for i in range(15)]
    for i, (o, n) in enumerate(lumps):
        if o < 0 or n < 0 or o + n > len(data):
            print("FAIL: lump %d [%d,%d) 越出文件（%d B）" % (i, o, o + n, len(data)), file=sys.stderr)
            return 2

    # ---------------- entity：出生点（CT/T） ----------------
    etext = data[lumps[L_ENT][0]:lumps[L_ENT][0] + lumps[L_ENT][1]].decode("latin-1")
    spawn_ct, spawn_t = [], []
    for blk in etext.split("}"):
        if "{" not in blk:
            continue
        ent = {}
        for m in re.finditer(r'"([^"]*)"\s+"([^"]*)"', blk[blk.index("{"):]):
            ent[m.group(1).lower()] = m.group(2)
        if "origin" not in ent:
            continue
        o = tuple(float(v) for v in ent["origin"].split())
        if ent.get("classname") == "info_player_start":
            spawn_ct.append(o)
        elif ent.get("classname") == "info_player_deathmatch":
            spawn_t.append(o)
    print("BSP: info_player_start(CT)=%d  info_player_deathmatch(T)=%d" % (len(spawn_ct), len(spawn_t)))
    if not spawn_ct or not spawn_t:
        print("FAIL: 出生点没解析出来（entity lump 口径变了？）", file=sys.stderr)
        return 2

    # ---------------- geo.bin：标记点 + 头部世界盒 ----------------
    g39 = load_module("g39", os.path.join(HERE, "g39-map-regression.py"))
    geo = g39.read_geo(a.geo)
    raw = open(a.geo, "rb").read()
    gwmin = struct.unpack_from("<3f", raw, 8 + 28 + 8)
    gwend = struct.unpack_from("<3f", raw, 8 + 28 + 8 + 12)
    geo_x = (gwmin[0], gwend[0])
    geo_z = (gwmin[2], gwend[2])
    print("geo.bin 头部世界盒(m): X[%.3f..%.3f] Z[%.3f..%.3f]" % (geo_x[0], geo_x[1], geo_z[0], geo_z[1]))

    o = 8 + 7 * 4 + 8 + 24                      # 跳到 group 段之后
    ng = struct.unpack_from("<I", raw, o)[0]
    o += 4
    for _ in range(ng):
        o += 48
        vc, ic = struct.unpack_from("<II", raw, o)
        o += 8 + vc * 12 + vc * 8 + vc * 12 + ic * 4
    nb = struct.unpack_from("<I", raw, o)[0]
    o += 4 + nb * 24
    nm = struct.unpack_from("<I", raw, o)[0]
    o += 4
    mk = {}
    for _ in range(nm):
        name = raw[o:o + 32].split(b"\0")[0].decode("utf-8")
        o += 32
        pc = struct.unpack_from("<I", raw, o)[0]
        o += 4
        mk[name] = [struct.unpack_from("<fff", raw, o + 12 * k) for k in range(pc)]
        o += 12 * pc
    if "Spawn_CT" not in mk or "Spawn_T" not in mk:
        print("FAIL: geo.bin 里没有 Spawn_CT/Spawn_T 标记段", file=sys.stderr)
        return 2
    ct_u, t_u = mk["Spawn_CT"], mk["Spawn_T"]
    if len(ct_u) != len(spawn_ct) or len(t_u) != len(spawn_t):
        print("FAIL: 出生点数不等（BSP %d/%d vs geo %d/%d）"
              % (len(spawn_ct), len(spawn_t), len(ct_u), len(t_u)), file=sys.stderr)
        return 2

    # ---------------- 轴映射（span 匹配）+ 偏移（包围盒中心） ----------------
    vtl = lumps[L_VERT]
    if vtl[1] % 12:
        print("FAIL: vertex lump 尺寸 %d 不是 12 的倍数" % vtl[1], file=sys.stderr)
        return 2
    allv = [struct.unpack_from("<3f", data, vtl[0] + 12 * i) for i in range(vtl[1] // 12)]
    bb = [(min(v[k] for v in allv), max(v[k] for v in allv)) for k in range(3)]
    spans = {0: bb[0][1] - bb[0][0], 1: bb[1][1] - bb[1][0]}
    want_x = (geo_x[1] - geo_x[0]) / U
    want_z = (geo_z[1] - geo_z[0]) / U
    axx = min(spans, key=lambda k: abs(spans[k] - want_x))
    axz = min(spans, key=lambda k: abs(spans[k] - want_z) if k != axx else 1e9)
    print("BSP 顶点外接框(HL u): x[%.0f..%.0f] span=%.0f | y[%.0f..%.0f] span=%.0f | z[%.0f..%.0f] span=%.0f"
          % (bb[0][0], bb[0][1], bb[0][1] - bb[0][0], bb[1][0], bb[1][1], bb[1][1] - bb[1][0],
             bb[2][0], bb[2][1], bb[2][1] - bb[2][0]))
    print("unity.x span 需 %.0f u ⇒ HL 轴 %s（%.0f u）；unity.z span 需 %.0f u ⇒ HL 轴 %s（%.0f u）"
          % (want_x, "xyz"[axx], spans[axx], want_z, "xyz"[axz], spans[axz]))
    if abs(spans[axx] - want_x) >= 40 or abs(spans[axz] - want_z) >= 40:   # 自检 3
        print("FAIL: 轴映射对不上（span 差 ≥ 40 u）", file=sys.stderr)
        return 2
    bx = (geo_x[0] + geo_x[1]) / 2 - U * (bb[axx][0] + bb[axx][1]) / 2
    bz0 = (geo_z[0] + geo_z[1]) / 2 - U * (bb[axz][0] + bb[axz][1]) / 2

    def spawn_resid(sgnz, pts, ref):
        used = [False] * len(ref)
        worst = 0.0
        for p in pts:
            tx = U * p[axx] + bx
            tz = sgnz * U * p[axz] + bz0
            best = None
            for j, r in enumerate(ref):
                if used[j]:
                    continue
                d = math.hypot(tx - r[0], tz - r[2])
                if best is None or d < best[0]:
                    best = (d, j)
            used[best[1]] = True
            worst = max(worst, best[0])
        return worst

    sgnz = None
    for s in (1.0, -1.0):
        r = spawn_resid(s, spawn_ct, ct_u)
        print("  sgnz=%+.0f : CT 出生点集合匹配最大残差 = %.6f m（上限 %.4f）" % (s, r, SNAP_MAX))
        if r < SNAP_MAX + 1e-3 and sgnz is None:
            sgnz = s
    if sgnz is None:                                                      # 自检 4a
        print("FAIL: 变换拟合不上（出生点集合残差 > %.4f）" % SNAP_MAX, file=sys.stderr)
        return 2
    bz = bz0
    r_t = spawn_resid(sgnz, spawn_t, t_u)
    print("独立复核（T 出生点集合匹配最大残差）= %.6f m（≤ %.4f = 吸附上限）" % (r_t, SNAP_MAX))
    if r_t >= SNAP_MAX + 1e-3:                                            # 自检 4b
        print("FAIL: T 出生点复核不过", file=sys.stderr)
        return 2
    print("transform: Unity.x = %.6f*HL.%s + %.4f ; Unity.z = %+.0f*%.6f*HL.%s + %.4f ; Unity.y = %.6f*HL.z"
          % (U, "xyz"[axx], bx, sgnz, U, "xyz"[axz], bz, U))

    # ---------------- 面 / 边 / 面名 ----------------
    pl = lumps[L_PLANE]
    planes = [struct.unpack_from("<4f2i", data, pl[0] + 20 * i) for i in range(pl[1] // 20)]
    ed = lumps[L_EDGE]
    edges = [struct.unpack_from("<2H", data, ed[0] + 4 * i) for i in range(ed[1] // 4)]
    su = lumps[L_SURF]
    surf = [struct.unpack_from("<i", data, su[0] + 4 * i)[0] for i in range(su[1] // 4)]
    fa = lumps[L_FACE]
    faces, tot = [], 0
    for i in range(fa[1] // 20):
        pn, side, fe, ne, ti, _st, _lo = struct.unpack_from("<hhihh4si", data, fa[0] + 20 * i)
        faces.append((pn, side, fe, ne, ti))
        tot += ne
    sf = load_module("sf", os.path.join(HERE, "dust2_surface_flags.py"))
    mi_names = sf.load_miptex_names(data, *lumps[L_TEX])
    ti_l = lumps[L_TEXINFO]
    texinfo = [struct.unpack_from("<ii", data, ti_l[0] + 40 * i + 32)[0] for i in range(ti_l[1] // 40)]
    print("lumps: planes=%d verts=%d edges=%d surfedges=%d faces=%d (sum numedges=%d)"
          % (len(planes), len(allv), len(edges), len(surf), len(faces), tot))
    if tot != len(surf):                                                  # 自检 2
        print("FAIL: sum(numedges)=%d != len(surfedges)=%d" % (tot, len(surf)), file=sys.stderr)
        return 2


    def name_of(ti):
        return mi_names[texinfo[ti]] if 0 <= texinfo[ti] < len(mi_names) else "?"


    def face_poly(fe, ne):
        out = []
        for k in range(ne):
            se = surf[fe + k]
            out.append(allv[edges[se][0] if se >= 0 else edges[-se][1]])
        return out

    def to_hl(x, z):
        return ((x - bx) / U, (z - bz) / (sgnz * U))

    def clip(poly2, rect):
        x0, y0, x1, y1 = rect
        out = poly2
        for (nx, ny), d in (((1, 0), x0), ((-1, 0), -x1), ((0, 1), y0), ((0, -1), -y1)):
            if not out:
                return []
            inp, out = out, []
            for i in range(len(inp)):
                A, B = inp[i], inp[(i + 1) % len(inp)]
                da, db = nx * A[0] + ny * A[1] - d, nx * B[0] + ny * B[1] - d
                if da >= 0:
                    out.append(A)
                if (da >= 0) != (db >= 0):
                    t = da / (da - db)
                    out.append((A[0] + t * (B[0] - A[0]), A[1] + t * (B[1] - A[1])))
        return out

    def area2(poly2):
        s = 0.0
        for i in range(len(poly2)):
            x1, y1 = poly2[i]
            x2, y2 = poly2[(i + 1) % len(poly2)]
            s += x1 * y2 - x2 * y1
        return abs(s) / 2.0

    gp = g39.Probe(g39.read_map(a.bytes_path), geo, g39.bucket(g39.triangles(geo), geo))

    # ---------------- 逐格 ----------------
    for spec in (a.cell or []):
        cix, ciz = (int(v) for v in spec.split(","))
        ux = ORIGIN_X + (cix + 0.5)
        uz = ORIGIN_Z + (ciz + 0.5)
        g = "None" if gp.ground(cix, ciz) is None else "%.3f" % gp.ground(cix, ciz)
        tt = "None" if gp.true_top(cix, ciz) is None else "%.3f" % gp.true_top(cix, ciz)
        print("\n== cell(%d,%d) 位图=%s  geo格心顶面=%s  geo真顶面=%s =="
              % (cix, ciz, "W" if gp.walkable(cix, ciz) else "B", g, tt))
        # 格心点查询：穿过格心的所有面
        hx, hy = to_hl(ux, uz)
        through = []
        for (pn, side, fe, ne, ti) in faces:
            poly = face_poly(fe, ne)
            inside = False
            for k in range(ne):
                x1, y1, _ = poly[k]
                x2, y2, _ = poly[(k + 1) % ne]
                if (y1 > hy) != (y2 > hy) and hx < x1 + (hy - y1) / (y2 - y1) * (x2 - x1):
                    inside = not inside
            if inside:
                n = planes[pn][:3]
                through.append((poly[0][2] * U, n[2] if side == 0 else -n[2], name_of(ti)))
        print("   格心穿过 %d 张面（按 y 升序）：" % len(through))
        for (y, nz, nm) in sorted(through)[:12]:
            print("      y %7.3f | nz %+0.3f | %s" % (y, nz, nm))
        # 该 1m² 格内的朝上面裁剪面积
        hx0, hy0 = to_hl(ux - 0.5, uz - 0.5)
        hx2, hy2 = to_hl(ux + 0.5, uz + 0.5)
        rect = (min(hx0, hx2), min(hy0, hy2), max(hx0, hx2), max(hy0, hy2))
        ups = []
        for (pn, side, fe, ne, ti) in faces:
            n = planes[pn][:3]
            if (n[2] if side == 0 else -n[2]) < 0.86:
                continue
            poly = face_poly(fe, ne)
            p2 = [(v[0], v[1]) for v in poly]
            b2 = (min(p[0] for p in p2), min(p[1] for p in p2), max(p[0] for p in p2), max(p[1] for p in p2))
            if b2[2] < rect[0] or b2[0] > rect[2] or b2[3] < rect[1] or b2[1] > rect[3]:
                continue
            ar = area2(clip(p2, rect)) * U * U
            if ar > a.min_area:
                ups.append((ar, poly[0][2] * U, name_of(ti)))
        ups.sort(key=lambda t: (-t[0], -t[1]))
        print("   1m² 内朝上面(nz≥0.86) %d 张，合计 %.3f m²：" % (len(ups), sum(t[0] for t in ups)))
        for (ar, y, nm) in ups[:8]:
            print("      面积 %6.3f m² | y %7.3f | %s" % (ar, y, nm))

    # ---------------- 逐矩形 ----------------
    for spec in (a.rect or []):
        x0, z0, x1, z1 = (float(v) for v in spec.split(","))
        hx0, hy0 = to_hl(x0, z0)
        hx1, hy1 = to_hl(x1, z1)
        rect = (min(hx0, hx1), min(hy0, hy1), max(hx0, hx1), max(hy0, hy1))
        ups = []
        for (pn, side, fe, ne, ti) in faces:
            n = planes[pn][:3]
            if (n[2] if side == 0 else -n[2]) < 0.86:
                continue
            poly = face_poly(fe, ne)
            p2 = [(v[0], v[1]) for v in poly]
            b2 = (min(p[0] for p in p2), min(p[1] for p in p2), max(p[0] for p in p2), max(p[1] for p in p2))
            if b2[2] < rect[0] or b2[0] > rect[2] or b2[3] < rect[1] or b2[1] > rect[3]:
                continue
            ar = area2(clip(p2, rect)) * U * U
            if ar > a.min_area:
                ups.append((ar, poly[0][2] * U, name_of(ti)))
        ups.sort(key=lambda t: (-t[0], -t[1]))
        print("\n== rect x[%.1f..%.1f] z[%.1f..%.1f]：朝上面 %d 张，合计 %.3f m²（矩形 %.1f m²）=="
              % (x0, x1, z0, z1, len(ups), sum(t[0] for t in ups), (x1 - x0) * (z1 - z0)))
        for (ar, y, nm) in ups[:12]:
            print("   面积 %6.3f m² | y %7.3f | %s" % (ar, y, nm))
        print("   面高集合（面积 ≥0.05 m²）= %s"
              % sorted({round(y, 3) for (ar, y, nm) in ups if ar >= 0.05}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
