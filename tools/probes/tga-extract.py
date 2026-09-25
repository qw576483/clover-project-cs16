#!/usr/bin/env python3
"""TGA (uncompressed true-color) -> PNG, 逐像素搬运，不缩放、不裁剪、不合成。

用法：
    python tools/probes/tga-extract.py --config tools/probes/tga-extract-buymenu.json
    python tools/probes/tga-extract.py --src <tga 目录> --dst <png 目录> --names <清单文件> [--prefix <前缀>]

买枪菜单那一批（30 张条目图标）的现成参数就在 `tga-extract-buymenu.json`（清单 = `tga-extract-buymenu-names.txt`），
载体 = `原版资源/cs16src/cstrike/gfx/vgui/*.tga`、输出 = `client/Assets/Resources/UI/Art/buy_*.png`。

载体：原版 CS 1.6 客户端的 `cstrike/gfx/vgui/*.tga`（买枪菜单条目图标，32bpp 带 alpha）。
本脚本只做**容器转换**：TGA 的 BGRA 像素原样写成 PNG 的 RGBA，画布尺寸原样保留
（256x64 或 256x128），既不裁到 ink 框也不按任何比例缩放 —— 于是"工程内那张 PNG"
与"原版那张 TGA"逐像素相等，可以随时用 SHA/像素比对复核。

自检（不通过就非 0 退出，绝不"转换完就算对"）：
  1) TGA 头 image type == 2（未压缩真彩）、pixel depth == 32；
  2) 输出 PNG 尺寸 == TGA 尺寸；
  3) 输出 PNG 的 RGBA 逐像素 == TGA 解码后的 RGBA（同一解码器，只验往返）；
  4) html 报告里每个文件的 (w,h,alpha 非零像素数)。
"""

import argparse
import json
import os
import sys

from PIL import Image


def decode(path):
    img = Image.open(path)
    if img.format != "TGA":
        raise ValueError(f"{path}: 不是 TGA（PIL format={img.format}）")
    raw = open(path, "rb").read()
    img_type = raw[2]
    depth = raw[16]
    if img_type != 2:
        raise ValueError(f"{path}: image type={img_type}（只接受 2 = 未压缩真彩）")
    if depth != 32:
        raise ValueError(f"{path}: pixel depth={depth}（只接受 32bpp 带 alpha）")
    rgba = img.convert("RGBA")
    expect = (int.from_bytes(raw[12:14], "little"), int.from_bytes(raw[14:16], "little"))
    if rgba.size != expect:
        raise ValueError(f"{path}: 解出尺寸 {rgba.size} != 头部声明 {expect}")
    return rgba


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", help="UTF-8 JSON，提供 src/dst/names/prefix（命令行同名参数覆盖它）；"
                                     "含非 ASCII 路径时**必须走它** —— Windows 上命令行参数会在 "
                                     "PowerShell 侧被按控制台代码页重编码，中文路径会变成乱码")
    ap.add_argument("--src", help="TGA 目录")
    ap.add_argument("--dst", help="PNG 输出目录")
    ap.add_argument("--names", help="名称清单文件（每行一个，不带扩展名，# 开头为注释）")
    ap.add_argument("--prefix", default=None, help="输出文件名前缀")
    args = ap.parse_args()

    if args.config:
        cfg = json.load(open(args.config, encoding="utf-8"))
        for key in ("src", "dst", "names", "prefix"):
            if getattr(args, key) is None and key in cfg:
                setattr(args, key, cfg[key])
        cfg_dir = os.path.dirname(os.path.abspath(args.config))
        if not os.path.isabs(args.names):
            args.names = os.path.join(cfg_dir, args.names)
    if args.prefix is None:
        args.prefix = ""
    for key in ("src", "dst", "names"):
        if getattr(args, key) is None:
            ap.error(f"缺 {key}（命令行或 --config 都要给一个）")

    names = []
    for line in open(args.names, encoding="utf-8"):
        line = line.strip()
        if line and not line.startswith("#"):
            names.append(line)

    os.makedirs(args.dst, exist_ok=True)
    rows = []
    for name in names:
        src = os.path.join(args.src, name + ".tga")
        dst = os.path.join(args.dst, args.prefix + name + ".png")
        rgba = decode(src)
        rgba.save(dst, "PNG")
        again = Image.open(dst).convert("RGBA")
        if again.size != rgba.size:
            raise ValueError(f"{dst}: 往返尺寸不一致 {again.size} != {rgba.size}")
        if again.tobytes() != rgba.tobytes():
            raise ValueError(f"{dst}: 往返像素不一致")
        alpha = rgba.getchannel("A")
        hist = alpha.histogram()
        opaque = sum(hist[1:])
        w, h = rgba.size
        # ink 外接框（alpha>0），只作报告，不用于裁剪
        bbox = alpha.getbbox() or (0, 0, 0, 0)
        rows.append((name, w, h, opaque, bbox))

    print("name\tw\th\talpha_nonzero\tink_bbox")
    for name, w, h, opaque, bbox in rows:
        print(f"{name}\t{w}\t{h}\t{opaque}\t{bbox}")
    print(f"OK files={len(rows)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
