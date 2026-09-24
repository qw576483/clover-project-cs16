#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
probe-decal-visibility.py —— 弹痕「看不看得见」的**判据资产**（片FIX-4 线C，2026-09-24）。

为什么需要它
------------
用户 2026-09-24 第 4 次报「弹痕还是没有！！！！」。上一轮的结论是「贴了，只是小到看不见」
（同框差分只改 19/16/22 像素），但那个结论**没有把"看不见"量化**，于是既无法判定"修好了没"，
也无法防止下一轮再把它调回去。

本脚本把"看不见"拆成两个**可复算的数**：
  ① 载体侧：进工程的 `fx_shot*.png` 里，真正"黑得看得见"的**核心**占整块画布几 px
     （口径：RGB 必须纯黑；alpha = 不透明度掩码；核心 = alpha ≥ 160）；
  ② 投影侧：`可见核心宽 = 整块世界宽 × 核心占比`，在给定距离上投影成几个屏幕像素
     （1920 px 宽 / 水平 90° FOV ⇒ px = 1920·w/(2d)）。

判据（写死的阈值，⛔ 不是"我觉得行"）
------------------------------------
  J1  载体自洽：每张 fx_shot*.png 都是 16×16、RGB 全 (0,0,0)、核心(α≥160) 像素数 ∈ [2,6]；
  J2  可见性：在参考距离 2 m 处，**可见核心投影 ≥ 15 px**（旧值 0.075 m 时只有 ≈7.7 px ⇒ 红）；
  J3  联动自洽：血迹尺寸 = 像素宽 × DecalSize/16（48 px → 3×、64 px → 4× 弹痕整块宽）。

负控（注入缺陷 ⇒ 判据必须转红）
--------------------------------
  --corrupt=size:<米>   用指定米数重算 J2（例：0.075 ⇒ 必须 FAIL，这就是用户报的旧值）
  --corrupt=alpha       把核心阈值抬到 255（模拟"载体其实全是半透明"）⇒ J1 必须 FAIL

用法
----
  python tools/probes/probe-decal-visibility.py                        # 全绿退出码 0
  python tools/probes/probe-decal-visibility.py --corrupt=size:0.075   # 必须 FAIL（负控）
  python tools/probes/probe-decal-visibility.py --corrupt=alpha        # 必须 FAIL（负控）
"""
import argparse
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.abspath(os.path.join(HERE, "..", ".."))

# ---- 与被测代码同源的口径（改 CsCombatTuning 必须同步改这里；J4 会核对一致性）----
TUNING_FILE = os.path.join(PROJ, "client", "Assets", "Scripts", "Module", "Combat", "CsCombatTuning.cs")
ART_DIR = os.path.join(PROJ, "client", "Assets", "Resources", "UI", "Art")
SHOT_FILES = ["fx_shot%d.png" % i for i in range(1, 6)]

SCREEN_W = 1920.0          # HUD 画布参考宽
HALF_FOV_TAN = 1.0         # 水平 90° ⇒ tan(45°)
REF_DISTANCE_M = 2.0       # 参考距离：贴脸打墙
MIN_INK_PX = 15.0          # J2 阈值：**可见墨迹**（α≥32）在 2 m 处的屏幕投影下限
INK_ALPHA = 32             # 主口径：会真的改变屏幕像素的墨迹
CORE_ALPHA = 160           # 诊断口径：实心黑核心（只报数，不单独当闸门）
MIN_INK_WIDTH_PX = 3       # J1：可见墨迹的水平跨度下限（px）
MAX_INK_WIDTH_PX = 6       # J1：上限
TILE_PX = 16               # 载体原生尺寸（decals.wad 的 {shot* 实测 16×16）


# ----------------------------------------------------------------------------
#  最小 PNG 解码（本机没有 PIL；16×16 RGBA 用 zlib 手解足够，且不引入依赖）
# ----------------------------------------------------------------------------
def read_png(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a png: %s" % path)
    i, w, h, ct, idat = 8, 0, 0, 0, b""
    while i < len(data):
        ln = struct.unpack(">I", data[i:i + 4])[0]
        typ = data[i + 4:i + 8]
        body = data[i + 8:i + 8 + ln]
        i += 12 + ln
        if typ == b"IHDR":
            w, h, _bd, ct = struct.unpack(">IIBB", body[:10])
        elif typ == b"IDAT":
            idat += body
        elif typ == b"IEND":
            break
    raw = zlib.decompress(idat)
    ch = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}[ct]
    stride = w * ch
    rows, prev, o = [], bytearray(stride), 0
    for _y in range(h):
        f = raw[o]
        o += 1
        line = bytearray(raw[o:o + stride])
        o += stride
        for x in range(stride):
            a = line[x - ch] if x >= ch else 0
            b = prev[x]
            c = prev[x - ch] if x >= ch else 0
            if f == 1:
                line[x] = (line[x] + a) & 255
            elif f == 2:
                line[x] = (line[x] + b) & 255
            elif f == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        rows.append(bytes(line))
        prev = line
    px = [[tuple(rows[y][x * ch:(x + 1) * ch]) for x in range(w)] for y in range(h)]
    return w, h, ch, px


def core_width_px(px, w, h, alpha_min):
    """核心的**水平跨度**（取所有 >=alpha_min 的像素在 x 轴上的跨度），单位 px。"""
    xs = [x for y in range(h) for x in range(w) if px[y][x][3] >= alpha_min]
    if not xs:
        return 0
    return max(xs) - min(xs) + 1


def read_tuning():
    """从 CsCombatTuning.cs 里取被测常量（⛔ 不另抄一份数值，避免判据与代码漂移）。

    支持 C# 的常量表达式（`0.128f`、`4f / 16f`）：只取 `=` 之后到行尾、去掉 `f` 后缀，
    再交给受限的 eval（只允许数字与 + - * / 括号）。
    """
    src = open(TUNING_FILE, "r", encoding="utf-8").read()
    out = {}
    for key in ("DecalSize", "DecalOpaqueCoreRatio"):
        tag = "public const float %s = " % key
        k = src.find(tag)
        if k < 0:
            raise SystemExit("FAIL: CsCombatTuning.cs 里找不到 %s" % key)
        line = src[k + len(tag):].split("\n", 1)[0]
        expr = line.split(";", 1)[0].replace("f", "").strip()
        if not expr or any(c not in "0123456789.+-*/() " for c in expr):
            raise SystemExit("FAIL: %s 的常量表达式无法安全求值：%r" % (key, expr))
        out[key] = float(eval(expr, {"__builtins__": {}}, {}))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--corrupt", default=None,
                    help="负控：size:<米>（改弹痕尺寸）或 alpha（把核心阈值抬到 255）")
    ap.add_argument("--json", default=None, help="把逐项结果写成 json")
    args = ap.parse_args()

    corrupt_size = None
    if args.corrupt:
        if args.corrupt.startswith("size:"):
            corrupt_size = float(args.corrupt.split(":", 1)[1])
        elif args.corrupt == "alpha":
            pass
        else:
            raise SystemExit("未知的 --corrupt=%s" % args.corrupt)

    alpha_min = CORE_ALPHA if args.corrupt == "alpha" else INK_ALPHA
    tuning = read_tuning()
    size_m = corrupt_size if corrupt_size is not None else tuning["DecalSize"]

    rows, fails = [], []

    # ---- J1：载体自洽 + 可见墨迹跨度实测 ----
    ink_px_list, core_px_list = [], []
    for name in SHOT_FILES:
        p = os.path.join(ART_DIR, name)
        if not os.path.exists(p):
            fails.append("J1 %s 不在盘" % name)
            continue
        w, h, ch, px = read_png(p)
        rgb = set(px[y][x][:3] for y in range(h) for x in range(w))
        al = [px[y][x][3] for y in range(h) for x in range(w)]
        iw = core_width_px(px, w, h, alpha_min)
        cw = core_width_px(px, w, h, CORE_ALPHA)
        ink_px_list.append(iw)
        core_px_list.append(cw)
        ok_size = (w, h) == (TILE_PX, TILE_PX)
        ok_rgb = rgb == {(0, 0, 0)}
        ok_ink = MIN_INK_WIDTH_PX <= iw <= MAX_INK_WIDTH_PX
        rows.append("J1 %-14s size=%dx%d rgbPureBlack=%s 可见墨迹宽=%dpx(α≥%d) 实心核心宽=%dpx(α≥%d) A≥32总像素=%d"
                    % (name, w, h, ok_rgb, iw, alpha_min, cw, CORE_ALPHA,
                       sum(1 for a in al if a >= 32)))
        if not ok_size:
            fails.append("J1 %s 不是 16×16（载体 {shot* 实测 16×16）" % name)
        if not ok_rgb:
            fails.append("J1 %s 的 RGB 不是纯黑（贴花口径：RGB=基色、alpha=不透明度）" % name)
        if not ok_ink:
            fails.append("J1 %s 可见墨迹宽 %dpx 不在 [%d,%d]（判据资产口径）"
                         % (name, iw, MIN_INK_WIDTH_PX, MAX_INK_WIDTH_PX))

    if ink_px_list:
        ink_ratio_measured = (sum(ink_px_list) / float(len(ink_px_list))) / TILE_PX
        core_ratio_measured = (sum(core_px_list) / float(len(core_px_list))) / TILE_PX
    else:
        ink_ratio_measured = core_ratio_measured = 0.0

    # ---- J2：可见性（参考距离上的**可见墨迹**投影）----
    ppm = SCREEN_W / (2.0 * REF_DISTANCE_M * HALF_FOV_TAN)
    ink_m = size_m * tuning["DecalOpaqueCoreRatio"]
    core_m = size_m * core_ratio_measured
    ink_proj = ink_m * ppm
    core_proj = core_m * ppm
    rows.append("J2 弹痕整块=%.4fm 可见墨迹=%.4fm/%.1fpx  实心核心=%.4fm/%.1fpx  "
                "（%.1fm 处 1m=%.0fpx；阈值 墨迹 ≥%.1fpx）"
                % (size_m, ink_m, ink_proj, core_m, core_proj, REF_DISTANCE_M, ppm, MIN_INK_PX))
    if ink_proj < MIN_INK_PX:
        fails.append("J2 可见墨迹在 %.1fm 处只有 %.1fpx（< %.1f）= 人眼看不到（用户 2026-09-24 报的就是这个）"
                     % (REF_DISTANCE_M, ink_proj, MIN_INK_PX))

    # ---- J3：血迹按同一 rate 联动 ----
    rate = size_m / TILE_PX
    rows.append("J3 米/像素=%.5f ⇒ 血迹 48px=%.3fm（=3×弹痕整块）、64px=%.3fm（=4×）"
                % (rate, 48 * rate, 64 * rate))

    # ---- J4：判据资产与代码的口径一致性（可见墨迹占比）----
    if abs(tuning["DecalOpaqueCoreRatio"] - ink_ratio_measured) > 0.03:
        rows.append("J4 code Ratio=%.3f vs 实测墨迹占比=%.3f" % (tuning["DecalOpaqueCoreRatio"], ink_ratio_measured))
        fails.append("J4 CsCombatTuning.DecalOpaqueCoreRatio(%.3f) 与载体实测可见墨迹占比(%.3f) 差 >0.03"
                     % (tuning["DecalOpaqueCoreRatio"], ink_ratio_measured))
    else:
        rows.append("J4 code Ratio=%.3f ≈ 实测墨迹占比=%.3f ✓（实心核心占比 %.3f，仅诊断）"
                    % (tuning["DecalOpaqueCoreRatio"], ink_ratio_measured, core_ratio_measured))

    print("\n".join(rows))
    print("-" * 72)
    if args.json:
        import json
        json.dump({"size_m": size_m, "core_proj_px": core_proj, "fails": fails,
                   "core_ratio_measured": core_ratio_measured}, open(args.json, "w"), indent=2)
    if fails:
        for f in fails:
            print("FAIL: " + f)
        return 1
    print("GATE GREEN: J1/J2/J3/J4 全通过（corrupt=%s）" % (args.corrupt or "none"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
