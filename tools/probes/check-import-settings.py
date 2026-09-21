#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""贴图导入设置复核（片W 判据资产）：扫 <root>/**/*.{png,jpg,tga} 的同名 .meta，
打印引擎实际使用的 textureType / maxTextureSize / textureCompression / mipmapEnabled /
filterMode / anisoLevel / sRGB / npotScale / alphaIsTransparency，以及平台覆盖（PlatformSettings）
和 PNG 文件头里的真实像素尺寸。

用法: python tools/probes/check-import-settings.py <Assets/Resources/Art 或任意目录> [--csv out.tsv]
"""
import os, re, struct, sys, glob

KEYS = ["textureType", "maxTextureSize", "textureCompression", "mipmapEnabled",
        "filterMode", "anisoLevel", "sRGBTexture", "npotScale", "alphaIsTransparency",
        "textureFormat", "overridden"]
EXT = (".png", ".jpg", ".jpeg", ".tga", ".bmp")


def png_size(path):
    try:
        with open(path, "rb") as f:
            head = f.read(33)
        if head[:8] != b"\x89PNG\r\n\x1a\n":
            return None
        w, h = struct.unpack(">II", head[16:24])
        bit = head[24]
        ct = head[25]
        return (w, h, bit, ct)
    except Exception:
        return None


def bmp_size(path):
    try:
        with open(path, "rb") as f:
            head = f.read(26)
        if head[:2] != b"BM":
            return None
        w, h = struct.unpack("<ii", head[18:26])
        return (abs(w), abs(h), 24, None)
    except Exception:
        return None


def parse_meta(text):
    def val(key, src=text):
        m = re.search(r"^\s*%s:\s*(\S+)\s*$" % re.escape(key), src, re.M)
        return m.group(1) if m else "-"
    d = {k: val(k) for k in KEYS}
    # Unity 把"是否生成 mip"写在 mipmaps.enableMipMap 里（不是 mipmapEnabled）
    m = re.search(r"^\s*enableMipMap:\s*(\S+)\s*$", text, re.M)
    d["mipmapEnabled"] = m.group(1) if m else "-"
    # 平台覆盖块（PlatformSettings: 后面缩进的键值）
    d["platforms"] = []
    for m in re.finditer(r"PlatformSettings:\s*\n((?:\s+.*\n)+)", text):
        blk = m.group(1)
        name = re.search(r"^\s*buildTarget:\s*(\S+)", blk, re.M)
        ps = {"buildTarget": name.group(1) if name else "?"}
        for k in ("maxTextureSize", "textureCompression", "filterMode", "overridden",
                  "textureFormat", "mipmaps", "anisoLevel", "sRGBTexture", "crunchedCompression"):
            mm = re.search(r"^\s*%s:\s*(\S+)\s*$" % k, blk, re.M)
            if mm:
                ps[k] = mm.group(1)
        if ps.get("overridden") == "1":
            d["platforms"].append(ps)
    return d


def main():
    root = sys.argv[1]
    csv = None
    if "--csv" in sys.argv:
        csv = sys.argv[sys.argv.index("--csv") + 1]
    rows = []
    files = []
    for dirpath, _dirs, fnames in os.walk(root):
        for fn in fnames:
            if fn.lower().endswith(EXT) and not fn.startswith("."):
                files.append(os.path.join(dirpath, fn))
    for p in sorted(files):
        mp = p + ".meta"
        w = h = None
        if p.lower().endswith(".png"):
            s = png_size(p)
            if s:
                w, h = s[0], s[1]
        elif p.lower().endswith(".bmp"):
            s = bmp_size(p)
            if s:
                w, h = s[0], s[1]
        rec = {"file": os.path.relpath(p, root), "wxh": ("%dx%d" % (w, h)) if w else "?"}
        if os.path.exists(mp):
            with open(mp, "r", encoding="utf-8", errors="replace") as f:
                rec.update(parse_meta(f.read()))
        else:
            rec.update({k: "NO-META" for k in KEYS})
            rec["platforms"] = []
        rows.append(rec)

    hdr = ["file", "wxh", "textureType", "maxTextureSize", "textureCompression", "mipmapEnabled",
           "filterMode", "anisoLevel", "sRGBTexture", "npotScale", "alphaIsTransparency", "platforms"]
    print("\t".join(hdr))
    for r in rows:
        pl = ";".join("%s(max=%s,comp=%s,filt=%s,fmt=%s)" % (x.get("buildTarget"), x.get("maxTextureSize"),
                      x.get("textureCompression"), x.get("filterMode"), x.get("textureFormat"))
                      for x in r.get("platforms", []))
        print("\t".join([r["file"], r["wxh"]] + [str(r.get(k, "-")) for k in hdr[2:-1]] + [pl]))
    if csv:
        with open(csv, "w", encoding="utf-8") as f:
            f.write("\t".join(hdr) + "\n")
            for r in rows:
                f.write("\t".join([r["file"], r["wxh"]] + [str(r.get(k, "-")) for k in hdr[2:-1]] + [""]) + "\n")
    # 汇总
    from collections import Counter
    c = Counter((r.get("textureType"), r.get("maxTextureSize"), r.get("textureCompression"),
                 r.get("mipmapEnabled"), r.get("filterMode"), r.get("anisoLevel"), r.get("sRGBTexture"))
                for r in rows)
    print("\n# 汇总(共 %d 张贴图) textureType/maxSize/compression/mipmap/filter/aniso/sRGB -> 张数" % len(rows))
    for k, n in c.most_common():
        print("  %s -> %d" % (" / ".join(str(x) for x in k), n))


if __name__ == "__main__":
    main()
