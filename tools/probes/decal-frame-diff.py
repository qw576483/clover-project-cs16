#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""差异 #69 的**离线像素判据**：拿 probe-decal-center.cs 报出的弹痕屏坐标，
在 decal_before.png / decal_after.png 两张帧上做同理裁切 → 逐像素差分。

回答两个问题（都是"白点事故"的直接判据）：
  ① 该处像素**真的变了**吗？（变了 ⇒ 弹痕真画上去了，不是"以为画了"）
  ② 变的那块**比墙面更暗**，还是**更亮**？（原版弹孔 = 暗芯 ⇒ 必须更暗；若是"白点"必然更亮）

用法：
  python tools/probes/decal-frame-diff.py \
      --report .ai-tmp/test/r3-decalcenter.txt \
      --before .ai-tmp/screenshots/decal_before.png \
      --after  .ai-tmp/screenshots/decal_after.png \
      --out    .ai-tmp/test/z-decal-pair.png
"""
import argparse
import io
import os
import re
import sys

sys.stdout.reconfigure(encoding='utf-8')

from PIL import Image, ImageDraw  # noqa: E402


def parse_screen_points(report_path):
    """从 eval_file 的 tsv 结果里抠出 probe 打印的三处弹痕屏坐标。"""
    raw = io.open(report_path, encoding='utf-8', errors='replace').read()
    raw = raw.replace('\\n', '\n')          # eval_file 的 result 是转义成 \n 的一行
    pts = []
    for m in re.finditer(r'\[C\] #(\d+)[^\n]*?屏坐标=\((-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)\)', raw):
        pts.append((int(m.group(1)), float(m.group(2)), float(m.group(3))))
    pts.sort()
    return [(x, y) for _, x, y in pts]


def crop(img, sx, sy, pad):
    """Unity 屏坐标 y **自底部**起算 ⇒ 先翻成图像行。"""
    W, H = img.size
    px, py = int(round(sx)), int(round(H - sy))
    box = (max(0, px - pad), max(0, py - pad), min(W, px + pad), min(H, py + pad))
    return img.crop(box), box, (px, py)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--report', required=True)
    ap.add_argument('--before', required=True)
    ap.add_argument('--after', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--pad', type=int, default=26, help='裁切半径（像素）')
    a = ap.parse_args()

    pts = parse_screen_points(a.report)
    if not pts:
        print('RESULT-DECALPAIR: FAIL\n  口径：报告里没抠到 [C] 的屏坐标')
        return 2
    b = Image.open(a.before).convert('RGB')
    f = Image.open(a.after).convert('RGB')
    if b.size != f.size:
        print('RESULT-DECALPAIR: FAIL\n  口径：两帧尺寸不同 %s vs %s' % (b.size, f.size))
        return 2

    rows = []
    tiles = []
    zoom = 4
    for i, (sx, sy) in enumerate(pts):
        cb, box, (px, py) = crop(b, sx, sy, a.pad)
        ca, _, _ = crop(f, sx, sy, a.pad)
        pb, pa = cb.load(), ca.load()
        w, h = cb.size
        n_changed = 0
        # 变化像素里"变暗 / 变亮"的累计亮度差
        sum_dark_delta = 0.0
        sum_light_delta = 0.0
        n_dark = n_light = 0
        worst = None
        wall_sum = 0.0
        for y in range(h):
            for x in range(w):
                r0, g0, b0 = pb[x, y]
                r1, g1, b1 = pa[x, y]
                l0 = 0.299 * r0 + 0.587 * g0 + 0.114 * b0
                l1 = 0.299 * r1 + 0.587 * g1 + 0.114 * b1
                wall_sum += l0
                d = l1 - l0
                if abs(r1 - r0) + abs(g1 - g0) + abs(b1 - b0) >= 12:
                    n_changed += 1
                    if d < 0:
                        n_dark += 1
                        sum_dark_delta += -d
                    elif d > 0:
                        n_light += 1
                        sum_light_delta += d
                    if worst is None or abs(d) > abs(worst[0]):
                        worst = (d, (r0, g0, b0), (r1, g1, b1), (box[0] + x, box[1] + y))
        wall_mean = wall_sum / float(max(1, w * h))
        # 变化像素的"绝对亮度"（用来判白点：白点 ⇒ 变化像素亮度很高）
        ch_sum = 0.0
        n_all = 0
        for y in range(h):
            for x in range(w):
                r0, g0, b0 = pb[x, y]
                r1, g1, b1 = pa[x, y]
                if abs(r1 - r0) + abs(g1 - g0) + abs(b1 - b0) >= 12:
                    ch_sum += 0.299 * r1 + 0.587 * g1 + 0.114 * b1
                    n_all += 1
        ch_mean = ch_sum / float(n_all) if n_all else float('nan')
        rows.append(dict(idx=i, sx=sx, sy=sy, img=(px, py), changed=n_changed,
                         dark=n_dark, light=n_light, box=box,
                         dark_delta=sum_dark_delta, light_delta=sum_light_delta,
                         wall_mean=wall_mean, ch_mean=ch_mean, worst=worst))
        # 拼图：上=前、下=后，横排三处
        tiles.append((cb.resize((w * zoom, h * zoom), Image.NEAREST),
                      ca.resize((w * zoom, h * zoom), Image.NEAREST), (i, sx, sy)))

    tw, th = tiles[0][0].size
    gap = 8
    sheet = Image.new('RGB', (tw * len(tiles) + gap * (len(tiles) - 1), th * 2 + gap), (24, 24, 24))
    d = ImageDraw.Draw(sheet)
    for i, (tb, ta, _) in enumerate(tiles):
        x = i * (tw + gap)
        sheet.paste(tb, (x, 0))
        sheet.paste(ta, (x, th + gap))
        d.text((x + 4, 4), 'BEFORE #%d' % i, fill=(255, 255, 0))
        d.text((x + 4, th + gap + 4), 'AFTER #%d' % i, fill=(255, 255, 0))
    sheet.save(a.out)

    print('[A] 报告屏坐标 %d 处：%s' % (len(pts), pts))
    for r in rows:
        print('[B] #%d 屏=(%g,%g) 图像=(%d,%d) 变化像素=%d 变暗=%d 变亮=%d'
              % (r['idx'], r['sx'], r['sy'], r['img'][0], r['img'][1],
                 r['changed'], r['dark'], r['light']))
        print('      墙均值亮度=%.1f 变化像素均值亮度=%.1f 变暗累计Δ=%.1f 变亮累计Δ=%.1f'
              % (r['wall_mean'], r['ch_mean'], r['dark_delta'], r['light_delta']))
        if r['worst']:
            print('      最大单像素变化 ΔL=%.1f 前RGB=%s 后RGB=%s 图像坐标=%s'
                  % (r['worst'][0], r['worst'][1], r['worst'][2], r['worst'][3]))
    print('[C] 拼图已落盘 %s（上=BEFORE / 下=AFTER，%d 倍放大）' % (a.out, zoom))

    # 阈值说明：弹痕是**半透明**的（贴花灰阶不透明度，白=透明），且本帧命中距离 4.9 m
    # ⇒ 屏幕上直径约 15 px、真正改动的只有"暗芯"那几十个像素（实测 16~22 px）。
    # ⇒ 判据取"每处 ≥8 px 且**一个变亮的都没有**"，比"改了很多像素"更抗噪。
    ok_render = all(r['changed'] >= 8 for r in rows)
    ok_dark = all(r['dark'] > r['light'] and r['ch_mean'] < r['wall_mean'] for r in rows)
    ok_hole = not any(r['ch_mean'] >= 200 for r in rows)     # 变化像素没到近白 ⇒ 不是白点
    print('RESULT-DECALPAIR: %s' % ('PASS' if (ok_render and ok_dark and ok_hole) else 'FAIL'))
    print('  口径：三处都真的改了像素（每处≥8px）=%s 变化像素比墙更暗且暗多于亮=%s 变化像素非近白=%s'
          % (ok_render, ok_dark, ok_hole))
    return 0 if (ok_render and ok_dark and ok_hole) else 1


if __name__ == '__main__':
    sys.exit(main())
