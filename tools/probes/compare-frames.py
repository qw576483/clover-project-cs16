#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""判据资产（tools/probes/）：差异 #89 的**像素判据** —— 把"枪口火焰帧"与"同状态无火焰帧"逐像素比。

为什么要它：几何探针只能证明"落点算对了"，不能证明"屏幕上真的多出一块像素"。人眼看两张 1920x1080
的截图里一个 0.3 米大的十字，容易看不出差别，也容易自欺。本脚本把这个差别**变成可断言的数**：
两张图必须在**冻结同一帧状态**下拍（timeScale=0），否则世界一动，差异就淹没在噪声里。

⛔ **口径踩过的两个坑（都留在这里，别重复踩）**：
 坑① 第一版要求"差异块外接框 < 40% 屏"（假设火焰只是屏幕上一小块）⇒ 实测 **FAIL**。
      因为 `CombatEffects.MuzzleFlash` 除了贴精灵，还在同一点挂了 **Point Light(range=8/intensity=4)**，
      它把玩家面前**整面墙**照亮 ⇒ 96.3% 的像素变了、外接框 = 全屏。那不是"火焰没出来"，是判据写错了。
 坑② 第二版改成"火焰投影窗内平均亮度上升 ≥ +40"⇒ 也 **FAIL（+39.98）**，而且这个量**根本没有区分度**：
      点光溢出下，**屏幕任何位置**的窗口平均增量都有 +125（实测另一处 230x230 窗口 dl 均值 +125.5）。
  ⇒ 真正有区分度、且只可能由"精灵画上去了"解释的量是**变暗**：点光**只能把像素变亮**，
    唯一能让墙变暗的东西就是 **精灵自己的不透明像素**（`fx_muzzleflash3` 的深色轮廓）。
    实测：火焰窗内 **18.53%** 的像素被压暗 ≥60（其中原亮度 111.6 → 20.6），
    而**全屏**变暗像素只占 **1.59%**。这条既好断言，又不可能被点光伪造。

判定（写死在脚本里，⛔ 不靠人眼看）：
  PASS ⇔ ① 差异像素数 >= --min-pixels（默认 150）
          ② **变亮方向占绝对多数**：brighter / (brighter + darker) >= --bright-share（默认 0.90）
             —— "多了一团光"是清一色变亮；换场景 / 换机位会同时出现大块变暗
          ③ 给了 --sprite-xy 时：**火焰窗内出现成规模的变暗簇** ——
             窗内 `dl < -60` 的像素占比 >= --dark-ratio（默认 0.05，实测 0.1853），
             且这些像素的压暗幅度 >= --dark-dlum（默认 30，实测 111.6-20.6 = 91.0）
  报告项（⛔ 不参与判定）：全屏平均亮度增量（点光 spill 的必然结果）。

用法：
  python tools/probes/compare-frames.py <control.png> <flash.png> \
      --sprite-xy 1133,380 --y-from-unity \
      [--sheet out_2up.png] [--json out.json] [--min-pixels 150] [--thresh 12] [--bright-share 0.90]

  ⚠️ `--sprite-xy` 按**图像坐标（左上原点）**解释。几何探针给的是 Unity 的
     `Camera.WorldToScreenPoint`（**左下原点**）⇒ 传原值并加 `--y-from-unity`，本脚本做 `y = h - y`。
"""
import argparse
import json
import sys

try:
    import numpy as np
    from PIL import Image, ImageDraw
except Exception as ex:  # pragma: no cover
    print("ERROR: 需要 numpy + Pillow（本机用 system python 3.12 跑，例：/c/Python312/python.exe）:", ex)
    sys.exit(2)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("control")
    ap.add_argument("flash")
    ap.add_argument("--sheet", default=None, help="可选：输出 2-up 对照图（含火焰投影窗）")
    ap.add_argument("--json", dest="json_out", default=None)
    ap.add_argument("--min-pixels", type=int, default=150)
    ap.add_argument("--thresh", type=int, default=12)
    ap.add_argument("--bright-share", type=float, default=0.90)
    ap.add_argument("--sprite-xy", default=None, help="几何探针给的火焰投影点，形如 1133,380")
    ap.add_argument("--y-from-unity", action="store_true",
                    help="--sprite-xy 的 y 是 Unity 左下原点（WorldToScreenPoint），先翻成图像坐标")
    ap.add_argument("--sprite-window", type=float, default=0.06, help="窗口半径 = 该比例 x 屏宽（默认 0.06）")
    ap.add_argument("--dark-thresh", type=int, default=60, help="判「变暗」的幅度门限（默认 60）")
    ap.add_argument("--dark-ratio", type=float, default=0.05)
    ap.add_argument("--dark-dlum", type=float, default=30.0)
    a = ap.parse_args()

    ctl = Image.open(a.control).convert("RGB")
    fla = Image.open(a.flash).convert("RGB")
    if ctl.size != fla.size:
        print("ERROR: 两图尺寸不同 %s vs %s（不是同一分辨率，没法逐像素比）" % (ctl.size, fla.size))
        sys.exit(2)

    w, h = ctl.size
    c = np.asarray(ctl, dtype=np.int16)
    f = np.asarray(fla, dtype=np.int16)
    lc = c.mean(axis=2)
    lf = f.mean(axis=2)
    dl = lf - lc                                    # 有符号亮度差（+ = 火焰帧更亮）

    changed = np.abs(dl) > a.thresh
    n = int(changed.sum())
    brighter = int((changed & (dl > 0)).sum())
    darker = int((changed & (dl < 0)).sum())
    denom = brighter + darker
    share = (brighter / float(denom)) if denom else 0.0
    full_dlum = float(dl.mean())

    win = None
    ok_dark = True
    if a.sprite_xy:
        sx, sy = [int(v) for v in a.sprite_xy.split(",")]
        sy_img = (h - sy) if a.y_from_unity else sy
        r = int(a.sprite_window * w)
        y0, y1 = max(0, sy_img - r), min(h, sy_img + r)
        x0, x1 = max(0, sx - r), min(w, sx + r)
        W = dl[y0:y1, x0:x1]
        LC = lc[y0:y1, x0:x1]
        LF = lf[y0:y1, x0:x1]
        dm = W < -a.dark_thresh
        dn = int(dm.sum())
        ratio = (dn / float(W.size)) if W.size else 0.0
        dd = (float(LC[dm].mean()) - float(LF[dm].mean())) if dn else 0.0
        win = {
            "center_probe": [sx, sy], "center_image": [sx, sy_img],
            "radius_px": r, "rect": [x0, y0, x1, y1], "pixels": int(W.size),
            "dark_px": dn, "dark_ratio": round(ratio, 4),
            "dark_lum_control": round(float(LC[dm].mean()), 2) if dn else None,
            "dark_lum_flash": round(float(LF[dm].mean()), 2) if dn else None,
            "dark_dlum": round(dd, 2),
            "mean_dlum": round(float(W.mean()), 2),
        }
        ok_dark = ratio >= a.dark_ratio and dd >= a.dark_dlum

    ok_n = n >= a.min_pixels
    ok_share = share >= a.bright_share
    ok = ok_n and ok_share and ok_dark

    ys, xs = np.nonzero(changed)
    bbox = [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())] if n else [-1, -1, -1, -1]

    d = {
        "control": a.control, "flash": a.flash, "size": [w, h], "thresh": a.thresh,
        "changed_pixels": n, "changed_ratio": round(n / float(w * h), 5),
        "brighter": brighter, "darker": darker, "brighter_share": round(share, 4),
        "full_frame_dlum": round(full_dlum, 2), "changed_bbox": bbox,
        "sprite_window": win, "verdict": "PASS" if ok else "FAIL",
        "criteria": {
            "changed_pixels>=%d" % a.min_pixels: ok_n,
            "brighter_share>=%.2f" % a.bright_share: ok_share,
            "sprite_dark_cluster(ratio>=%.2f and dlum>=%.0f)" % (a.dark_ratio, a.dark_dlum): ok_dark,
        },
        "note": ("火焰自带 Point Light(range=8,intensity=4) 会把面前整面墙照亮 ⇒ 全屏平均亮度上升、"
                 "差异近满屏；因此**不用**「差异块必须局部」，也不用「窗内平均亮度」"
                 "（点光下屏幕任意窗口的平均增量都很大、没有区分度）。"
                 "用的量是**变暗簇**：点光只能变亮，能压暗的只有精灵自己的不透明像素。"),
    }

    print("control=%s" % a.control)
    print("flash  =%s" % a.flash)
    print("size=%dx%d thresh=%d" % (w, h, a.thresh))
    print("changed_pixels=%d (%.3f%% of screen) bbox=%s" % (n, 100.0 * n / (w * h), bbox))
    print("brighter=%d (%.3f%%)  darker=%d (%.3f%%)  brighter_share=%.4f"
          % (brighter, 100.0 * brighter / (w * h), darker, 100.0 * darker / (w * h), share))
    print("full_frame_dlum=%+.2f (只报告，不作判据)" % full_dlum)
    if win:
        print("sprite_window center(probe)=%s center(image)=%s r=%dpx rect=%s"
              % (win["center_probe"], win["center_image"], win["radius_px"], win["rect"]))
        print("  窗内变暗簇：dl<-%d 的像素=%d (%.2f%% of window)  压暗幅度=%.2f (%.2f -> %.2f)"
              % (a.dark_thresh, win["dark_px"], 100.0 * win["dark_ratio"],
                 win["dark_dlum"], win["dark_lum_control"] or 0.0, win["dark_lum_flash"] or 0.0))
    else:
        print("sprite_window=(未给 --sprite-xy，跳过第 ③ 条)")
    print("RESULT-DIFF: %s" % d["verdict"])
    print("  口径： 差异像素>=%d=%s 变亮占比>=%.2f=%s 火焰窗变暗簇(占比>=%.2f 且 幅度>=%.0f)=%s"
          % (a.min_pixels, ok_n, a.bright_share, ok_share, a.dark_ratio, a.dark_dlum, ok_dark))
    print("  ⚠️ %s" % d["note"])

    if a.json_out:
        with open(a.json_out, "w", encoding="utf-8") as fh:
            json.dump(d, fh, ensure_ascii=False, indent=2)

    if a.sheet:
        pad, lab = 8, 34
        sheet = Image.new("RGB", (w, h * 2 + pad * 3 + lab * 2), (24, 24, 28))
        dr = ImageDraw.Draw(sheet)
        dr.text((10, 8), "A  control (timeScale=0, no muzzle flash)", fill=(230, 230, 230))
        sheet.paste(ctl, (0, lab + pad))
        dr.text((10, lab + pad + h + pad), "B  same frozen frame + CombatEffects.MuzzleFlash()",
                fill=(230, 230, 230))
        sheet.paste(fla, (0, lab * 2 + pad * 2 + h))
        if win:
            x0, y0, x1, y1 = win["rect"]
            for top in (lab + pad, lab * 2 + pad * 2 + h):
                dr.rectangle([x0, top + y0, x1, top + y1], outline=(255, 64, 64), width=3)
        sheet.save(a.sheet)
        print("sheet=%s" % a.sheet)

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
