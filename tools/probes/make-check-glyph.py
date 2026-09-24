#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""make-check-glyph.py -- 用**原版载体**渲出 VGUI 勾选框里那个"勾"的带 alpha PNG。

为什么有这个脚本（切片AV，差异 #33 的收口）
------------------------------------------
`CsUiStyle.CreateCheckButton` 原先用一块**同色实心小方块**表达"已勾选"，因为
"原版勾是 Marlett 字形而本工程没有该字体"。载体其实在盘（`原版资源/cs16src/marlett.ttf`），
字形也已定案（`tools/probes/marlett-glyphs.py`：勾 = **gid 12**，可达码位 = `U+F061`）——
缺的只是"把字形从字体里搬成一张贴图"这一步。

⛔ 不走 uGUI 字符路：该载体是 Windows **符号字体**（cmap 只有 (1,0) Mac-Roman + (3,0) MS-Symbol，
**没有 (3,1) Unicode**）⇒ Unity 取不到它自己的字形（切片AU 实测：图集块 2x2 / ink=0 / advance=0；
`U+0029` 拿到的是系统 fallback 拉丁字体的 ")"）。⇒ 走"预渲染贴图"路，天然不被 `ApplyOriginalFonts`
（只刷 `Text`）覆写。

口径
----
与 `marlett-glyphs.py` **逐项同口径**：同一个载体、同一个"给符号字体补一张 (3,1) cmap"的补丁
（复用它的 `Ttf` / `write_render_font`，⛔ 不另写一份解析）、同一个 `--size`（默认 300）、
同一个 ink 阈值（32）、同一个 `ink_stats` 量法。

像素出处（⛔ 不许拉伸 / 不许自定尺寸）
------------------------------------
画布 = 字形**紧致 ink 外接框**（裁掉四周空白，即"去水印"），因此 PNG 尺寸 = 判据给出的
`bbox`。--size 300 时 = **132×140**（`ink=7523 / ratio=0.943 / comps=1 / vx=0.352 / arm=0.264`）。

颜色
----
RGB 逐像素 = 原版 `CheckButtonCheck` → `BrightControlText "255 176 0 255"`
（`clientscheme.res:30/179`，本工程常量 `CsUiStyle.CheckMark`），A = 字形覆盖率。
⇒ 面板上 `Image.color` 给**白**即得原版橙色勾；换色只需改 `Image.color`，不必重渲贴图。

用法
----
    python tools/probes/make-check-glyph.py \\
        --ttf "原版资源/cs16src/marlett.ttf" \\
        --out "client/Assets/Resources/UI/Art/menu_check.png"

退出码：0 = 成功；2 = 载体读不出 / 字形是空的 / 尺寸与判据口径不符（**拒绝产出**）。
"""

import argparse
import hashlib
import importlib.util
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

# 判据数字（`tools/probes/marlett-glyphs.py` 在 --size 300 下量到的 gid 12）
EXPECT_CODEPOINT = 0xF061        # 勾（符号码 0x61 = 'a' ⟶ gid 12）
EXPECT_INK = 7523
EXPECT_W, EXPECT_H = 132, 140

# 原版勾色 = clientscheme.res:30/179 的 BrightControlText（= CsUiStyle.CheckMark）
CHECK_RGB = (255, 176, 0)


def load_marlett():
    """按路径导入同目录的 marlett-glyphs.py（复用它的 Ttf / write_render_font / ink_stats）。"""
    path = os.path.join(HERE, "marlett-glyphs.py")
    spec = importlib.util.spec_from_file_location("marlett_glyphs", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("--ttf", required=True, help="原版 marlett.ttf 载体")
    ap.add_argument("--out", required=True, help="输出的 RGBA PNG")
    ap.add_argument("--size", type=int, default=300, help="渲染像素尺寸（判据口径 = 300）")
    ap.add_argument("--thresh", type=int, default=32, help="ink 阈值（与判据同）")
    ap.add_argument("--codepoint", default="U+F061", help="要渲的码位（判据定案 = U+F061/gid12）")
    ap.add_argument("--render-font", help="补丁字体的落盘路径（默认 <项目根>/.ai-tmp/test/menu_check-render.ttf）")
    args = ap.parse_args(argv)

    mg = load_marlett()
    cp = int(args.codepoint.replace("U+", ""), 16)

    try:
        ttf = mg.Ttf(args.ttf)
    except (OSError, mg.TtfError) as e:
        sys.stderr.write("cannot read carrier: %s\n" % e)
        return 2

    cm = ttf.cmap()
    if cp not in cm:
        sys.stderr.write("carrier does not map %s\n" % args.codepoint)
        return 2
    gid = cm[cp]

    #    绝不能落在输出目录（`Assets/` 下一张 .ttf 会被 Unity 当字体资产导入）。
    ROOT = os.path.dirname(os.path.dirname(HERE))
    rf_path = args.render_font or os.path.join(ROOT, ".ai-tmp", "test", "menu_check-render.ttf")
    os.makedirs(os.path.dirname(os.path.abspath(rf_path)), exist_ok=True)
    _p, _runs, trip_ok = mg.write_render_font(ttf, cm, rf_path)
    if not trip_ok:
        sys.stderr.write("render font cmap does not round trip -- refusing to render\n")
        return 2

    from PIL import Image, ImageFont
    font = ImageFont.truetype(rf_path, args.size)
    mask = mg.render_mask(font, chr(cp), args.size)      # 与判据同一个渲染函数（同一画布/边距）
    st = mg.ink_stats(mask, args.thresh)

    print("carrier  : %s (%d B)" % (os.path.basename(args.ttf), ttf.size))
    print("codepoint: %s  gid=%d  postName=%s" % (args.codepoint, gid, ttf.post_names().get(gid, "-")))
    print("renderFnt: %s" % rf_path)
    print("size     : %d" % args.size)
    print("measured : ink=%s bbox=%s w=%s h=%s ratio=%s cover=%s comps=%s vx=%s arm=%s tick=%s"
          % (st["ink"], st["bbox"], st["w"], st["h"], st["ratio"], st["cover"], st["comps"],
             st.get("vx"), st.get("arm"), "Y" if st.get("tick") else "n"))

    if st["ink"] == 0 or st["bbox"] is None:
        sys.stderr.write("glyph rasterises empty -- refusing to write a blank sprite\n")
        return 2

    x0, y0, x1, y1 = st["bbox"]
    glyph = mask.crop((x0, y0, x1 + 1, y1 + 1))          # 去水印 = 裁到紧致 ink 框
    gw, gh = glyph.size
    if gw != st["w"] or gh != st["h"]:
        sys.stderr.write("crop size %dx%d != measured bbox %dx%d -- measurement bug\n"
                         % (gw, gh, st["w"], st["h"]))
        return 2

    rgba = Image.new("RGBA", (gw, gh), (CHECK_RGB[0], CHECK_RGB[1], CHECK_RGB[2], 0))
    rgba.putalpha(glyph)
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    rgba.save(args.out, "PNG")

    with open(args.out, "rb") as f:
        raw = f.read()
    print("written  : %s  %dx%d RGBA  %d B  sha256=%s"
          % (args.out, gw, gh, len(raw), hashlib.sha256(raw).hexdigest()[:16]))
    print("alpha    : ink(alpha>=%d)=%d  == measured ink=%d  -> %s"
          % (args.thresh, sum(1 for v in glyph.getdata() if v >= args.thresh), st["ink"],
             "OK" if sum(1 for v in glyph.getdata() if v >= args.thresh) == st["ink"] else "MISMATCH"))

    # 口径守卫：尺寸 / ink / 码位必须与判据一致（判据数字被改动时**拒绝静默产出歪贴图**）
    bad = []
    if cp != EXPECT_CODEPOINT:
        bad.append("codepoint %s != 判据 %04X" % (args.codepoint, EXPECT_CODEPOINT))
    if args.size == 300:
        if st["ink"] != EXPECT_INK:
            bad.append("ink %s != 判据 %d" % (st["ink"], EXPECT_INK))
        if (gw, gh) != (EXPECT_W, EXPECT_H):
            bad.append("size %dx%d != 判据 %dx%d" % (gw, gh, EXPECT_W, EXPECT_H))
    if bad:
        sys.stderr.write("OUT OF CONTRACT: %s\n" % "; ".join(bad))
        return 2
    print("contract : gid %d / %s / ink %d / %dx%d matches the judgement numbers -- OK"
          % (gid, args.codepoint, st["ink"], gw, gh))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
