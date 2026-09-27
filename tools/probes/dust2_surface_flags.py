# -*- coding: utf-8 -*-
"""从 CS 1.6 官方 de_dust2.bsp 解出「贴图 -> 弹痕族判据（flags）+ 面数」，并导出：
  1) 文本表   client/Assets/ThirdParty/Dust2/surface_flags.tsv
  2) C# 数据表（打印到 stdout，粘进 Module/Combat/CsCombatTuning.cs 的 SurfaceDecals 表）

原版判据（client.dll，本次逐条反汇编复核过，地址可复跑）
-------------------------------------------------------
`client.dll` RVA 0x20B5-0x2127（在函数 0x20B0 内；同逻辑另有一份 0x1E00-0x1E73）：
    cmp [obj+0x8C], 1   ->  "{break%i"（RandomInt(0,2)+1，木/玻璃碎裂族，载体 48x48 texel）
    cmp [obj+0x68], 0   != 0 -> "{bproof1"（防弹族，16x16）
    否则                 ->  "{shot%i"（RandomInt(0,4)+1，普通弹痕，16x16）
外加门禁 `cmp [obj+0x60], 4`；`obj` 由 `[[0x1A24DB0]+0x24](index)` 取回（`index` 来自命中结构 +0x30）。
族名串在 .data：RVA 0xE154C="{shot%i" / 0xE1554="{bproof1" / 0xE1560="{break%i"。

为什么判据不能只读 TEXINFO.flags（本次实测结论）
------------------------------------------------
BSP v30 的 `texinfo_t`（40 B：`vecs[2][4] + int miptex + int flags`）里 `flags` 取值只有 0 / 1，
且 ==1 的**只有** `sky`(1356 面) / `aaatrigger`(25 面) / `clip`(0 面) —— 即 TEX_SPECIAL（天空/触发/夹层），
**不是**"这块面可碎裂"。⇒ 单靠 TEXINFO.flags 会把天空判成碎裂族。
BSP 里真正能把"可碎裂"这一位判出来的载体是 **ENTITY lump**：
地图作者用 `func_breakable` 刷实体声明了哪些刷面可碎裂（de_dust2 共 10 个，全部 `material 2`、
gibmodel `models/mil_crategibs.mdl`）；它们的刷面贴图 = 本表 `break` 类。
`func_breakable` 的刷面 -> 贴图 的路径 = `entity.model`（"*N"）-> MODELS lump（64 B/条，firstface/numfaces）
-> FACES -> TEXINFO -> miptex 名。

本脚本另外导出 TEXINFO.flags 原值（`texinfo_flags` 列）与 `surf68` 列（恒 0：dust2 里没有任何
载体声明"防弹面"，该分支在本图上不可达 —— 如实记 0，不猜）。

自洽断言（跑不过就 exit 2，绝不输出半份表）
------------------------------------------
  1. BSP 版本 == 30；所有 lump 落在文件内；
  2. texinfo lump 尺寸 == 条数 x 40；faces lump 尺寸 == 条数 x 20；models lump 尺寸 == 条数 x 64；
  3. 每个 face 的 texinfo 索引 < texinfo 条数；每个 texinfo 的 miptex 索引 < miptex 条数；
  4. 归并面数总和 == face 条数；
  5. entity lump 里每个 "*N" 的 N < models 条数。

用法
----
  python tools/probes/dust2_surface_flags.py                    # 逐条 + 汇总 + C# 表
  python tools/probes/dust2_surface_flags.py --out <tsv 路径>
  python tools/probes/dust2_surface_flags.py --csharp           # 只打印 C# 表块
只读 BSP 与磁盘上的贴图目录名；--out 只在给定路径写文本。
"""

import argparse
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BSP = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2.bsp")
TEXDIR = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "Textures")

LUMP_ENTITIES = 0
LUMP_TEXTURES = 2
LUMP_TEXINFO = 6
LUMP_FACES = 7
LUMP_MODELS = 14
TEXINFO_LEN = 40
FACE_LEN = 20
MODEL_LEN = 64

BREAKABLE_CLASS = "func_breakable"


def load_lumps(data):
    version = struct.unpack_from("<i", data, 0)[0]
    if version != 30:
        raise ValueError("not a GoldSource BSP v30 (version=%d)" % version)
    lumps = [struct.unpack_from("<ii", data, 4 + 8 * i) for i in range(15)]
    for i, (off, size) in enumerate(lumps):
        if size <= 0:
            continue
        if off < 0 or off + size > len(data):
            raise ValueError("lump %d [%d,%d) runs outside the file (%d bytes)"
                             % (i, off, off + size, len(data)))
    return version, lumps


def load_miptex_names(data, off, size):
    if size < 4:
        raise ValueError("textures lump too small (%d)" % size)
    n = struct.unpack_from("<i", data, off)[0]
    if n <= 0 or n > 8192:
        raise ValueError("implausible miptex count %d" % n)
    if 4 + 4 * n > size:
        raise ValueError("miptex offset table (%d) exceeds lump size (%d)" % (4 + 4 * n, size))
    names = []
    for i in range(n):
        mo = struct.unpack_from("<i", data, off + 4 + 4 * i)[0]
        if mo < 0:
            names.append(None)
            continue
        if mo + 40 > size:
            raise ValueError("miptex %d header at %d exceeds lump size %d" % (i, mo, size))
        names.append(data[off + mo:off + mo + 16].split(b"\x00")[0].decode("latin-1"))
    return names


def load_texinfos(data, off, size, miptex_count):
    if size % TEXINFO_LEN != 0:
        raise ValueError("texinfo lump size %d is not a multiple of %d" % (size, TEXINFO_LEN))
    out = []
    for i in range(size // TEXINFO_LEN):
        b = off + i * TEXINFO_LEN
        vecs = struct.unpack_from("<8f", data, b)
        miptex, flags = struct.unpack_from("<ii", data, b + 32)
        if miptex < 0 or miptex >= miptex_count:
            raise ValueError("texinfo %d: miptex index %d out of range (%d)"
                             % (i, miptex, miptex_count))
        out.append(dict(idx=i, vecs=vecs, miptex=miptex, flags=flags))
    return out


def load_faces(data, off, size, texinfo_count):
    if size % FACE_LEN != 0:
        raise ValueError("faces lump size %d is not a multiple of %d" % (size, FACE_LEN))
    out = []
    for i in range(size // FACE_LEN):
        b = off + i * FACE_LEN
        firstedge, = struct.unpack_from("<i", data, b + 4)
        numedges, texinfo = struct.unpack_from("<HH", data, b + 8)
        if texinfo >= texinfo_count:
            raise ValueError("face %d: texinfo index %d out of range (%d)"
                             % (i, texinfo, texinfo_count))
        out.append(dict(idx=i, texinfo=texinfo, firstedge=firstedge, numedges=numedges))
    return out


def load_models(data, off, size, face_count):
    if size % MODEL_LEN != 0:
        raise ValueError("models lump size %d is not a multiple of %d" % (size, MODEL_LEN))
    out = []
    for i in range(size // MODEL_LEN):
        b = off + i * MODEL_LEN
        mins = struct.unpack_from("<3f", data, b)
        maxs = struct.unpack_from("<3f", data, b + 12)
        firstface, numfaces = struct.unpack_from("<ii", data, b + 56)
        if firstface < 0 or numfaces < 0 or firstface + numfaces > face_count:
            raise ValueError("model %d: face range [%d,%d) out of range (%d)"
                             % (i, firstface, firstface + numfaces, face_count))
        out.append(dict(idx=i, mins=mins, maxs=maxs, firstface=firstface, numfaces=numfaces))
    return out


def load_entities(data, off, size, model_count):
    """-> [dict(model_index=..., classname=..., keys={}) ...]（只保留带刷模型的实体）。"""
    text = data[off:off + size].decode("latin-1")
    out = []
    for body in re.findall(r"\{([^}]*)\}", text):
        kv = dict(re.findall(r'"([^"]+)"\s*"([^"]*)"', body))
        m = kv.get("model", "")
        if not m.startswith("*"):
            continue
        mi = int(m[1:])
        if mi >= model_count:
            raise ValueError("entity %r: model index %d out of range (%d)"
                             % (kv.get("classname"), mi, model_count))
        out.append(dict(model_index=mi, classname=kv.get("classname", "?"), keys=kv))
    return out


def unity_slug(name):
    """BSP miptex 名 -> 工程侧贴图/材质名（与 ThirdParty/Dust2/Textures/*.png 同名去扩展名）。

    改名规则**以盘上文件为准**：先试原名，再试首字符 '-' -> '_'（Unity 侧不接受以 '-' 开头的资产名）。
    两者都不在盘上则返回 None —— ⛔ 不猜（例如 `+0~fifties_lgt2` / `generic011` 在盘上被回落到
    `white` / `box_x`，那是像素回落，不是这一份 flags 表能推出的映射）。"""
    if name is None:
        return None
    if os.path.isfile(os.path.join(TEXDIR, name + ".png")):
        return name
    alt = ("_" + name[1:]) if name.startswith("-") else None
    if alt and os.path.isfile(os.path.join(TEXDIR, alt + ".png")):
        return alt
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--bsp", default=BSP)
    ap.add_argument("--out")
    ap.add_argument("--csharp", action="store_true")
    a = ap.parse_args()

    data = open(a.bsp, "rb").read()
    version, lumps = load_lumps(data)

    names = load_miptex_names(data, *lumps[LUMP_TEXTURES])
    texinfos = load_texinfos(data, *lumps[LUMP_TEXINFO], len(names))
    faces = load_faces(data, *lumps[LUMP_FACES], len(texinfos))
    models = load_models(data, *lumps[LUMP_MODELS], len(faces))
    entities = load_entities(data, *lumps[LUMP_ENTITIES], len(models))

    # ---- 逐 texinfo 的面数 ----
    faces_per_texinfo = [0] * len(texinfos)
    for f in faces:
        faces_per_texinfo[f["texinfo"]] += 1

    # ---- 逐贴图：texinfo 条数 / 面数 / texinfo.flags 取值集 ----
    per_texture = {}
    for ti in texinfos:
        nm = names[ti["miptex"]]
        e = per_texture.setdefault(nm, dict(texinfo=0, faces=0, flagset=set()))
        e["texinfo"] += 1
        e["faces"] += faces_per_texinfo[ti["idx"]]
        e["flagset"].add(ti["flags"])

    # ---- 逐贴图：哪些实体类用了它（经 brush model -> faces -> texinfo）+ func_breakable 面数 ----
    classes_per_texture = {}
    breakable_faces = {}
    for ent in entities:
        md = models[ent["model_index"]]
        for fi in range(md["firstface"], md["firstface"] + md["numfaces"]):
            nm = names[texinfos[faces[fi]["texinfo"]]["miptex"]]
            classes_per_texture.setdefault(nm, set()).add(ent["classname"])
            if ent["classname"] == BREAKABLE_CLASS:
                breakable_faces[nm] = breakable_faces.get(nm, 0) + 1

    # ---- 族判据：surf8C（可碎裂）/ surf68（防弹）----
    # surf8C = 1 当且仅当**该贴图过半数的面**位于 func_breakable 刷实体的刷面上。
    #   为什么要过半而不是"至少一面"：本工程的几何是**每张贴图合并成一个 Mesh**（一个 MeshCollider、
    #   一个材质），运行时只拿得到材质名 ⇒ 只能按贴图粒度判。用"至少一面"会把只是**顺带**出现在
    #   某个可碎裂刷上的贴图（`SandCrtSmSd` 只有 4/99 面、`SandCrtSmTp` 2/40 面）整片判成可碎裂 ⇒
    #   95 面普通几何跟着换成碎裂族。过半这一档是**本项目新增的判定口径**（原版按表面判，不按贴图），
    #   阈值与逐贴图分子分母都写进本表，可复核。
    # surf68 = 0：dust2 的载体里没有任何「防弹面」声明（如实记 0，不猜）。
    rows = []
    for nm, e in per_texture.items():
        cls = sorted(classes_per_texture.get(nm, ()))
        brk = breakable_faces.get(nm, 0)
        rows.append(dict(
            name=nm,
            texinfo_flags=sorted(e["flagset"]),
            surf8C=1 if (brk > 0 and brk * 2 >= e["faces"]) else 0,
            surf68=0,
            texinfo=e["texinfo"],
            faces=e["faces"],
            brk_faces=brk,
            classes=cls,
            slug=unity_slug(nm),
        ))
    rows.sort(key=lambda r: (-r["faces"], r["name"] or ""))

    total_faces = sum(r["faces"] for r in rows)
    if total_faces != len(faces):
        raise ValueError("face-count mismatch: grouped %d != lump %d" % (total_faces, len(faces)))

    if not a.csharp:
        print("BSP        : %s  v%d  %d bytes" % (os.path.relpath(a.bsp, ROOT).replace("\\", "/"),
                                                  version, len(data)))
        print("counts     : miptex=%d  texinfo=%d  faces=%d  models=%d  entities(*N)=%d"
              % (len(names), len(texinfos), len(faces), len(models), len(entities)))
        print("selfcheck  : OK (lump bounds / 40B / 20B / 64B / index ranges / face total %d)"
              % total_faces)
        print()
        print("%-22s %-8s %6s %6s %6s %6s  %s"
              % ("texture", "texflg", "surf8C", "surf68", "brk/fac", "faces", "entities"))
        print("-" * 104)
        for r in rows:
            print("%-22s %-8s %6d %6d %3d/%-3d %6d  %s"
                  % (r["name"], ",".join(str(x) for x in r["texinfo_flags"]), r["surf8C"],
                     r["surf68"], r["brk_faces"], r["faces"], r["faces"],
                     "、".join(r["classes"]) or "-"))
        print()
        print("== 汇总 ==")
        print("texinfo.flags 取值分布：%s"
              % ", ".join("%d(%d 张贴图)" % (fl, sum(1 for r in rows if fl in r["texinfo_flags"]))
                          for fl in sorted({fl for r in rows for fl in r["texinfo_flags"]})))
        brk = [r for r in rows if r["surf8C"] == 1]
        print("surf8C=1（过半数面在 func_breakable 刷上，%d 张）：%s"
              % (len(brk), "、".join("%s(%d/%d面)" % (r["name"], r["brk_faces"], r["faces"])
                                     for r in brk)))
        print("surf68!=0（防弹面）：0 张 —— dust2 载体里没有该声明（如实记 0）")
        print("其余 %d 张 -> {shot*" % (len(rows) - len(brk)))
        print()
        print("== 点名核对：func_breakable 的刷面贴图（含未过半的） ==")
        for r in rows:
            if r["brk_faces"] > 0:
                print("  %-14s surf8C=%d  surf68=%d  func_breakable 面 %d/%d  texinfo=%d  实体=%s"
                      % (r["name"], r["surf8C"], r["surf68"], r["brk_faces"], r["faces"],
                         r["texinfo"], "、".join(r["classes"])))
        print()
        print("== 点名核对：box / box_x（工程侧 PNG 名，**不是** BSP miptex 名） ==")
        for want in ("box", "box_x"):
            hit = [r for r in rows if r["name"] == want or r["slug"] == want]
            print("  %-8s : %s" % (want, "BSP texinfo 里 0 命中（它是工程侧回落的 PNG 名，不是原版贴图名）"
                                   if not hit else str(hit)))

    if a.out:
        with open(a.out, "w", encoding="utf-8", newline="\n") as f:
            f.write("# de_dust2.bsp（GoldSource BSP v30）逐贴图的弹痕族判据表\n")
            f.write("# 生成器 = tools/probes/dust2_surface_flags.py（可重跑）\n")
            f.write("# BSP = client/Assets/ThirdParty/Dust2/de_dust2.bsp（%d 字节 / v%d）\n"
                    % (len(data), version))
            f.write("# 原版判据出处 = client.dll RVA 0x20B5-0x2127（函数 0x20B0；同逻辑 0x1E00-0x1E73）：\n")
            f.write("#   [obj+0x8C]==1 -> {break%i ; 否则 [obj+0x68]!=0 -> {bproof1 ; 否则 {shot%i\n")
            f.write("#   （族名串 .data：{shot%i@0xE154C / {bproof1@0xE1554 / {break%i@0xE1560）\n")
            f.write("# surf8C 的来源 = ENTITY lump：该贴图是否**过半数**的面在 func_breakable 刷实体的刷面上\n")
            f.write("#   （de_dust2 共 10 个 func_breakable，全部 material 2 / gibmodel models/mil_crategibs.mdl）\n")
            f.write("#   「过半」是**本项目新增的判定口径**（原版按表面判、不按贴图）：本工程每张贴图合并成一个\n")
            f.write("#   Mesh / 一个 MeshCollider / 一个材质 ⇒ 运行时只拿得到材质名，只能按贴图粒度判。\n")
            f.write("# ⛔ texinfo.flags 不是这一位：本图 flags 只有 0/1，==1 的是 sky/aaatrigger/clip（TEX_SPECIAL）\n")
            f.write("# surf68 = 0：dust2 的载体里没有任何「防弹面」声明（如实记 0，不猜）\n")
            f.write("# slug = 运行时材质名（= ThirdParty/Dust2/Textures/<slug>.png 去扩展名）；空 = 工程侧另有回落名\n")
            f.write("model\ttexinfo_flags\tsurf8C\tsurf68\tbreakable_faces\ttexinfo_count\tfaces\tslug\tentities\n")
            for r in rows:
                f.write("%s\t%s\t%d\t%d\t%d\t%d\t%d\t%s\t%s\n"
                        % (r["name"], "|".join(str(x) for x in r["texinfo_flags"]), r["surf8C"],
                           r["surf68"], r["brk_faces"], r["texinfo"], r["faces"], r["slug"] or "",
                           "|".join(r["classes"])))
        print()
        print("wrote %s  (%d rows)" % (a.out, len(rows)))

    if a.csharp or not a.out:
        print()
        print("== C# 数据表（Module/Combat/CsCombatTuning.cs 的 SurfaceDecals 表） ==")
        for r in rows:
            keys = [r["name"]] + ([r["slug"]] if r["slug"] and r["slug"] != r["name"] else [])
            for k in keys:
                print('            new SurfaceRow("%s", %d, %d),'
                      % (k, r["surf8C"], r["surf68"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
