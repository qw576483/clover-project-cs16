# -*- coding: utf-8 -*-
"""从 CS 1.6 官方 de_dust2.bsp 解出 10 个 `func_breakable` 木箱刷实体，并导出：

  1) 文本表   client/Assets/ThirdParty/Dust2/breakables.tsv
     （跑 `Dust2Builder` 时由 `Cs16.EditorTools.Dust2Breakables` 读取 —— 生成器据此把箱面
      从「按贴图合并的大网格」里拆成独立箱体）

原版事实（ENTITY + MODELS + FACES/TEXINFO/TEXTURES 四个 lump）
--------------------------------------------------------------
`ENTITY` lump 里 `classname == "func_breakable"` 且带刷模型（`model "*N"`）的实体共 10 个：
  5 个 `targetname tgt_b`（B 点那堆）+ 5 个 `tgt_a`（A 点那堆），
  `material` 全 2 / `health` 全 1 / `gibmodel` 全 `models/mil_crategibs.mdl` / `spawnflags 1`。
箱体 AABB = `MODELS` lump 第 N 条（64 B/条）的 `mins`/`maxs`（GoldSrc 单位，1 unit = 1 inch）。
箱面贴图 = `MODELS.firstface/numfaces` → `FACES` → `TEXINFO` → `TEXTURES`；**逐箱不同**
（7 箱是 `MltryCrteSd2`+`MltryCrteTp`，`brush36` 是 `SandCrtSmSd`+`SandCrtSmTp`），
所以表里逐箱给一份贴图集合 —— 生成器只认"该箱自己的贴图 + 落在该箱 AABB 内"的三角面。

其中 3 箱（brush 3 / 38 / 40）是**绕竖轴转了 45° 的菱形箱**：MODELS 的 mins/maxs 是菱形
外接轴对齐盒（实测箱面顶点就落在盒的 4 个角上）-> 生成器必须按 AABB 取面、拿真几何当碰撞体，
不能用轴对齐 BoxCollider 代替。

坐标换算（本工程 Unity 米）
---------------------------
换算脚本（`dust2_build.py`）本机不在盘，故这里**从 de_dust2_geo.bin 自己拟合**，拟合过程可复跑：
  1. 轴映射：unity = (hl.x, hl.z, hl.y)（由地图包围盒逐轴尺寸对上：三个方向 4864/5600/960 unit）；
  2. 平移：逐轴一维扫描，只认"该轴 20 个箱面坐标全部落在网格顶点坐标平面上（容差 1e-3 m）"的平移；
     x / z 各只有唯一解；y 有 3 个候选解（箱高与网格平面间距撞了）-> 用**判据 3** 挑；
  3. 判据 3：变换后"箱体自己的贴图三角面整落在这个箱 AABB 内"的个数 —— 正解给 108 个
     （= 10 箱 × 6 面 × 2 三角），错解给 ~30 个。
拟合结果：unityX = hl.x·0.0254 + 9.7536 ｜ unityY = hl.z·0.0254 ｜ unityZ = hl.y·0.0254 - 28.448。

自洽断言（跑不过就 exit 2，绝不输出半份表）
------------------------------------------
  1. BSP 版本 30；四个 lump 的尺寸是各自记录长度的整数倍；`*N` 的 N < models 条数；
  2. `func_breakable` 刷实体恰好 10 个；每箱 mins 严格小于 maxs 且落在地图包围盒内；
  3. 10/10 箱的 `material=2` / `health=1` / `gibmodel=models/mil_crategibs.mdl`；
  4. 逐轴平移拟合：x / z 唯一满分，y 的候选按"箱面三角面个数"挑出唯一最优；
  5. 最终变换下每箱都能取到 **12 个自己的贴图三角面**（合计 108）。

用法
----
  python tools/probes/dust2_breakables.py                 # 打印逐箱 + 汇总
  python tools/probes/dust2_breakables.py --out <tsv 路径>
只读 BSP 与 geo.bin；只有 `--out` 会在给定路径写文本。
"""

import argparse
import bisect
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BSP = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2.bsp")
GEO = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2_geo.bin")
TEXDIR = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "Textures")
OUT = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "breakables.tsv")

UNIT = 0.0254            # 出处：CsConst.UnitToMeter（GoldSrc unit -> 米）
LUMP_ENTITIES = 0
LUMP_TEXTURES = 2
LUMP_TEXINFO = 6
LUMP_FACES = 7
LUMP_MODELS = 14
MODEL_LEN = 64
TEXINFO_LEN = 40
FACE_LEN = 20
BREAKABLE_CLASS = "func_breakable"
MAP_AXIS = (0, 2, 1)     # unity 轴 u <- HL 轴 MAP_AXIS[u]
PLANE_TOL = 1e-3
BOX_EPS = 0.0015
EXPECT_PER_CRATE_TRIS = 12


def die(msg):
    sys.stderr.write("[dust2_breakables] FAIL: %s\n" % msg)
    sys.exit(2)


# ======================================================================
#  BSP
# ======================================================================
def load_lumps(data):
    if struct.unpack_from("<i", data, 0)[0] != 30:
        die("not a GoldSource BSP v30")
    lumps = [struct.unpack_from("<ii", data, 4 + 8 * i) for i in range(15)]
    for i, (off, size) in enumerate(lumps):
        if size > 0 and (off < 0 or off + size > len(data)):
            die("lump %d [%d,%d) runs outside the file (%d bytes)" % (i, off, off + size, len(data)))
    return lumps


def load_miptex_names(data, off, size):
    n = struct.unpack_from("<i", data, off)[0]
    if n <= 0 or 4 + 4 * n > size:
        die("textures lump: implausible miptex count %d" % n)
    names = []
    for i in range(n):
        mo = struct.unpack_from("<i", data, off + 4 + 4 * i)[0]
        names.append(None if mo < 0 else
                     data[off + mo:off + mo + 16].split(b"\x00")[0].decode("latin-1"))
    return names


def load_texinfo_miptex(data, off, size, miptex_count):
    if size % TEXINFO_LEN:
        die("texinfo lump size %d not a multiple of %d" % (size, TEXINFO_LEN))
    out = []
    for i in range(size // TEXINFO_LEN):
        miptex, flags = struct.unpack_from("<ii", data, off + i * TEXINFO_LEN + 32)
        if miptex < 0 or miptex >= miptex_count:
            die("texinfo %d: miptex %d out of range" % (i, miptex))
        out.append((miptex, flags))
    return out


def load_faces(data, off, size, texinfo_count):
    if size % FACE_LEN:
        die("faces lump size %d not a multiple of %d" % (size, FACE_LEN))
    out = []
    for i in range(size // FACE_LEN):
        texinfo = struct.unpack_from("<H", data, off + i * FACE_LEN + 10)[0]
        if texinfo >= texinfo_count:
            die("face %d: texinfo %d out of range" % (i, texinfo))
        out.append(texinfo)
    return out


def load_models(data, off, size, face_count):
    if size % MODEL_LEN:
        die("models lump size %d not a multiple of %d" % (size, MODEL_LEN))
    out = []
    for i in range(size // MODEL_LEN):
        b = off + i * MODEL_LEN
        mins = struct.unpack_from("<3f", data, b)
        maxs = struct.unpack_from("<3f", data, b + 12)
        firstface, numfaces = struct.unpack_from("<ii", data, b + 56)
        if firstface < 0 or numfaces < 0 or firstface + numfaces > face_count:
            die("model %d: face range [%d,%d) out of range" % (i, firstface, firstface + numfaces))
        out.append(dict(idx=i, mins=mins, maxs=maxs, firstface=firstface, numfaces=numfaces))
    return out


def load_brush_entities(data, off, size, model_count):
    text = data[off:off + size].decode("latin-1")
    out = []
    for body in re.findall(r"\{([^}]*)\}", text):
        kv = dict(re.findall(r'"([^"]+)"\s*"([^"]*)"', body))
        m = kv.get("model", "")
        if not m.startswith("*"):
            continue
        mi = int(m[1:])
        if mi >= model_count:
            die("entity %r: brush model %d out of range" % (kv.get("classname"), mi))
        out.append(dict(model_index=mi, classname=kv.get("classname", "?"), keys=kv))
    return out


def unity_slug(name):
    """BSP miptex 名 -> 工程侧贴图/材质名（= ThirdParty/Dust2/Textures/<slug>.png 去扩展名）。
    改名规则**以盘上文件为准**：先试原名，再试首字符 '-' -> '_'（Unity 侧不接受以 '-' 开头的资产名）；
    两者都不在盘 -> 返回原名（生成器那边会明确报"缺贴图"，⛔ 这里不静默替换）。"""
    if name is None:
        return None
    if os.path.isfile(os.path.join(TEXDIR, name + ".png")):
        return name
    alt = ("_" + name[1:]) if name.startswith("-") else None
    if alt and os.path.isfile(os.path.join(TEXDIR, alt + ".png")):
        return alt
    return name


# ======================================================================
#  geo.bin（头部包围盒 + 每个材质组的顶点与三角面）
# ======================================================================
def read_geo(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    if raw[:4] != b"CD2G":
        die("%s 魔数不符（期望 CD2G）" % path)
    if struct.unpack_from("<i", raw, 4)[0] != 1:
        die("%s 版本不是 1" % path)
    wmin = struct.unpack_from("<3f", raw, 44)
    wmax = struct.unpack_from("<3f", raw, 56)
    gc = struct.unpack_from("<I", raw, 68)[0]
    o = 72
    groups = []
    for _ in range(gc):
        nm = raw[o:o + 48].split(b"\x00")[0].decode("utf-8")
        o += 48
        vc, ic = struct.unpack_from("<II", raw, o)
        o += 8
        vs = []
        for _v in range(vc):
            vs.append(struct.unpack_from("<3f", raw, o))
            o += 12
        o += 8 * vc + 12 * vc
        idx = []
        for _i in range(ic):
            idx.append(struct.unpack_from("<I", raw, o)[0])
            o += 4
        groups.append(dict(name=nm, verts=vs, idx=idx, tris=ic // 3))
    if o > len(raw):
        die("%s 解析越过文件尾" % path)
    return dict(wmin=wmin, wmax=wmax, groups=groups)


# ======================================================================
#  变换拟合
# ======================================================================
def to_unity(hl, t, signs):
    return [signs[u] * hl[MAP_AXIS[u]] * UNIT + t[u] for u in range(3)]


def build_coord_sets(geo):
    sets = [set(), set(), set()]
    for g in geo["groups"]:
        for v in g["verts"]:
            for u in range(3):
                sets[u].add(round(v[u] / PLANE_TOL) * PLANE_TOL)
    return [sorted(s) for s in sets]


def plane_hits(coords, values, t):
    n = 0
    for val in values:
        v = val + t
        k = bisect.bisect_left(coords, v)
        if min((abs(coords[j] - v) for j in (k - 1, k, k + 1) if 0 <= j < len(coords)), default=9e9) <= PLANE_TOL:
            n += 1
    return n


def crate_texture_tris(geo, crates, t, signs):
    """变换后：每个箱 AABB 内整落的「该箱自己贴图」的三角面个数（逐箱）。"""
    per = []
    for c in crates:
        lo = to_unity([min(c["mins"][i], c["maxs"][i]) for i in range(3)], t, signs)
        hi = to_unity([max(c["mins"][i], c["maxs"][i]) for i in range(3)], t, signs)
        bl = [min(lo[i], hi[i]) for i in range(3)]
        bh = [max(lo[i], hi[i]) for i in range(3)]
        n = 0
        for g in geo["groups"]:
            if os.path.splitext(g["name"])[0] not in c["textures"]:
                continue
            for ti in range(g["tris"]):
                ok = True
                for k in range(3):
                    v = g["verts"][g["idx"][3 * ti + k]]
                    for i in range(3):
                        if not (bl[i] - BOX_EPS <= v[i] <= bh[i] + BOX_EPS):
                            ok = False
                            break
                    if not ok:
                        break
                if ok:
                    n += 1
        per.append(n)
    return per


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bsp", default=BSP)
    ap.add_argument("--geo", default=GEO)
    ap.add_argument("--out")
    a = ap.parse_args()

    data = open(a.bsp, "rb").read()
    lumps = load_lumps(data)
    names = load_miptex_names(data, *lumps[LUMP_TEXTURES])
    texinfos = load_texinfo_miptex(data, *lumps[LUMP_TEXINFO], len(names))
    faces = load_faces(data, *lumps[LUMP_FACES], len(texinfos))
    models = load_models(data, *lumps[LUMP_MODELS], len(faces))
    ents = load_brush_entities(data, *lumps[LUMP_ENTITIES], len(models))

    brk = sorted([e for e in ents if e["classname"] == BREAKABLE_CLASS], key=lambda e: e["model_index"])
    if len(brk) != 10:
        die("func_breakable 刷实体应为 10 个，实际 %d 个" % len(brk))

    crates = []
    for e in brk:
        md = models[e["model_index"]]
        for i in range(3):
            if not md["mins"][i] < md["maxs"][i]:
                die("model %d: mins 不小于 maxs（轴 %d）" % (md["idx"], i))
        slugs = []
        for fi in range(md["firstface"], md["firstface"] + md["numfaces"]):
            s = unity_slug(names[texinfos[faces[fi]][0]])
            if s and s not in slugs:
                slugs.append(s)
        if not slugs:
            die("model %d: 一个箱面贴图都取不到" % md["idx"])
        crates.append(dict(model=md["idx"], mins=md["mins"], maxs=md["maxs"],
                           textures=slugs, keys=e["keys"]))

    geo = read_geo(a.geo)
    coords = build_coord_sets(geo)
    hm_min = [models[0]["mins"][i] * UNIT for i in range(3)]
    hm_max = [models[0]["maxs"][i] * UNIT for i in range(3)]
    for c in crates:
        for i in range(3):
            lo, hi = min(c["mins"][i], c["maxs"][i]) * UNIT, max(c["mins"][i], c["maxs"][i]) * UNIT
            if lo < hm_min[i] - 1e-3 or hi > hm_max[i] + 1e-3:
                die("箱 %d 的轴 %d 超出世界包围盒" % (c["model"], i))

    # ---- 逐轴扫描平移：只保留让该轴 20 个箱面全中的候选 ----
    signs = (1, 1, 1)
    values = [[], [], []]
    for c in crates:
        for i in range(3):
            values[MAP_AXIS.index(i)].append(min(c["mins"][i], c["maxs"][i]) * UNIT)
            values[MAP_AXIS.index(i)].append(max(c["mins"][i], c["maxs"][i]) * UNIT)
    cands = []
    for u in range(3):
        full, t = [], -40.0
        while t <= 40.0:
            if plane_hits(coords[u], values[u], t) == len(values[u]):
                full.append(round(t, 4))
            t += 0.002
        merged = []
        for v in full:
            if merged and abs(v - merged[-1][-1]) <= 0.0021:
                merged[-1].append(v)
            else:
                merged.append([v])
        if not merged:
            die("unity 轴 %d 找不到让 20 个箱面全中的平移" % u)
        cands.append([sum(g) / len(g) for g in merged])

    best, second = None, None
    for tx in cands[0]:
        for ty in cands[1]:
            for tz in cands[2]:
                t = [tx, ty, tz]
                per = crate_texture_tris(geo, crates, t, signs)
                score = sum(per)
                if best is None or score > best[0]:
                    second = best
                    best = (score, t, per)
                elif second is None or score > second[0]:
                    second = (score, t, per)
    if best[0] < 100 or (second and second[0] >= best[0]):
        die("变换拟合不成立：最优 %.0f / 次优 %.0f（期望 108，且必须唯一最优）"
            % (best[0], second[0] if second else -1))
    if any(n != EXPECT_PER_CRATE_TRIS for n in best[2]):
        die("逐箱箱面三角面数不是 12 个：%s（AABB 口径或轴映射有问题）" % best[2])

    t, per = best[1], best[2]
    # 规范化平移到 unit 的整数倍（0.0254 的整数倍）—— 拟合出的值应当如此
    t = [round(v / UNIT) * UNIT for v in t]
    per = crate_texture_tris(geo, crates, t, signs)
    if any(n != EXPECT_PER_CRATE_TRIS for n in per):
        die("平移规范化到 0.0254 整数倍后不成立：%s" % per)

    rows = []
    for c in crates:
        lo = to_unity([min(c["mins"][i], c["maxs"][i]) for i in range(3)], t, signs)
        hi = to_unity([max(c["mins"][i], c["maxs"][i]) for i in range(3)], t, signs)
        rows.append(dict(
            model=c["model"], textures=c["textures"],
            min=[min(lo[i], hi[i]) for i in range(3)], max=[max(lo[i], hi[i]) for i in range(3)],
            material=c["keys"].get("material", ""), health=c["keys"].get("health", ""),
            gib=c["keys"].get("gibmodel", ""), target=c["keys"].get("targetname", ""),
            spawnflags=c["keys"].get("spawnflags", "")))

    mats = sorted(set(r["material"] for r in rows))
    hp = sorted(set(r["health"] for r in rows))
    gib = sorted(set(r["gib"] for r in rows))
    sf = sorted(set(r["spawnflags"] for r in rows))
    if mats != ["2"] or hp != ["1"] or gib != ["models/mil_crategibs.mdl"]:
        die("原版键值与既定口径不符：material=%s health=%s gibmodel=%s" % (mats, hp, gib))
    if sf != ["1"]:
        die("spawnflags 不是全 1：%s（本表口径依赖 SF_BREAK_TRIGGER_ONLY）" % sf)

    print("BSP       : %s  v30  %d bytes" % (os.path.relpath(a.bsp, ROOT).replace("\\", "/"), len(data)))
    print("geo       : %d 个材质组 / %d 三角面" % (len(geo["groups"]), sum(g["tris"] for g in geo["groups"])))
    print("轴映射    : unity = (hl.x, hl.z, hl.y)  平移候选 %s" % [["%.4f" % v for v in c] for c in cands])
    print("拟合结果  : 平移 %s -> 逐箱箱面三角面 %s（合计 %d，次优 %d）"
          % (["%.4f" % v for v in t], per, sum(per), second[0] if second else -1))
    print("原版键值  : %d 箱  material=%s  health=%s  gibmodel=%s  spawnflags=%s"
          % (len(rows), mats, hp, gib, sf))
    print()
    print("%-6s %-9s %-9s %-9s %-9s %-9s %-9s %-5s %-22s %s"
          % ("brush", "minX", "minY", "minZ", "maxX", "maxY", "maxZ", "unit", "textures", "target"))
    print("-" * 118)
    for r in rows:
        size = [round((r["max"][i] - r["min"][i]) / UNIT) for i in range(3)]
        print("%-6d %-9.4f %-9.4f %-9.4f %-9.4f %-9.4f %-9.4f %-5s %-22s %s"
              % (r["model"], r["min"][0], r["min"][1], r["min"][2],
                 r["max"][0], r["max"][1], r["max"][2],
                 "x".join(str(x) for x in size), ",".join(r["textures"]), r["target"]))

    if not a.out:
        print("\n（未给 --out，未写盘）")
        return

    lines = [
        "# de_dust2 的 10 个 func_breakable 箱体（AABB 已换算成工程 Unity 米）",
        "# 生成器 tools/probes/dust2_breakables.py：读 %s 的 ENTITY/MODELS/FACES/TEXINFO/TEXTURES lump"
        % os.path.basename(a.bsp),
        "# 换算：GoldSrc unit × %.4f（CsConst.UnitToMeter）；轴映射 unity=(hl.x, hl.z, hl.y)，"
        % UNIT,
        "#       平移 %s 由 de_dust2_geo.bin 的网格平面拟合（逐轴一维扫描 + 逐箱箱面三角面数判优，"
        % ["%.4f" % v for v in t],
        "#       拟合得 %s，次优 %d）" % (per, second[0] if second else -1),
        "# 原版键值：material 2 / health 1 / gibmodel models/mil_crategibs.mdl / spawnflags 1（10/10）",
        "# spawnflags 1 = SF_BREAK_TRIGGER_ONLY（HLSDK dlls/util.h:441）⇒ 原版这 10 个箱子",
        "#   takedamage = DAMAGE_NO、且 SetTouch(NULL)（dlls/func_break.cpp:148-151 / :168-169）",
        "#   ⇒ 子弹 / 手雷 / 撞击都打不碎，只有 trigger 触发才会碎。",
        "# 列：brushModel\\tminX\\tminY\\tminZ\\tmaxX\\tmaxY\\tmaxZ\\ttextures(逗号分隔的材质组名)\\tmaterial\\thealth\\tgibmodel\\tspawnflags",
    ]
    for r in rows:
        lines.append("%d\t%.6f\t%.6f\t%.6f\t%.6f\t%.6f\t%.6f\t%s\t%s\t%s\t%s\t%s"
                     % (r["model"], r["min"][0], r["min"][1], r["min"][2],
                        r["max"][0], r["max"][1], r["max"][2], ",".join(r["textures"]),
                        r["material"], r["health"], r["gib"], r["spawnflags"]))
    with open(a.out, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")
    print("\n已写出 %s（%d 行数据 + %d 行头）"
          % (os.path.relpath(a.out, ROOT).replace("\\", "/"), len(rows), len(lines) - len(rows)))


if __name__ == "__main__":
    main()
