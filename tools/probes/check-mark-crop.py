# -*- coding: utf-8 -*-
"""check-mark-crop.py -- 勾选框的"勾"是不是**原版字形**：把每个勾标记按探针给的**上屏矩形**裁出来、
放大成一张联络图，并顺带量出"改造前 / 改造后"两帧的**逐像素差**（证明改动只落在勾标记那一格）。

为什么必须裁 + 放大（判据链）：
  * 勾标记上屏只有 **8x8 画布像素**（`CsUiStyle.CheckBoxSize - 8f` 的方形画框，位置/尺寸本片未动），
    在 1920x1080 的整屏帧里肉眼读不出"是勾还是方块" ⇒ 逐格放大 8 倍（nearest，不引入插值假象）；
  * 矩形**不是估的**：来自同一 Play 会话的只读探针 `tools/probes/probe-check-mark.cs`
    （`MARK<TAB>行名<TAB>页名<TAB>self=..<TAB>sprite=..<TAB>rect=x0,y0,x1,y1`，y 从下往上）；
  * 逐像素差 = 旁证"这一格之外什么都没变"（画面是表现类判据，方框几何若被牵动，差区会溢出那 8x8）。

用法：
    python tools/probes/check-mark-crop.py \
        --marks <项目根>/.ai-tmp/test/av-marks-final.txt \
        --shot-dir <项目根>/.ai-tmp/screenshots \
        --before-dir <项目根>/.ai-tmp/test --before-prefix av-before- \
        --out <项目根>/.ai-tmp/screenshots/31_options_checkmarks-8x.png --scale 8 --margin 6
"""

import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))


def page_to_key(page):
    # Page_Video -> video ; Page_Multiplayer -> multiplayer
    return page.replace('Page_', '').lower()


def parse_marks(text):
    """-> [(row, page_key, self_on, sprite, rect(x0,y0,x1,y1))]"""
    out = []
    for line in text.replace('\\t', '\t').replace('\\n', '\n').split('\n'):
        if not line.startswith('MARK\t'):
            continue
        f = line.split('\t')
        if len(f) < 10:
            continue
        m = re.search(r'rect=(-?\d+),(-?\d+),(-?\d+),(-?\d+)', line)
        if not m:
            continue
        rect = tuple(int(v) for v in m.groups())
        self_on = f[3] == 'self=True'
        sprite = f[5].split('=', 1)[1]
        out.append((f[1], page_to_key(f[2]), self_on, sprite, rect))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--marks', required=True)
    ap.add_argument('--shot-dir', required=True)
    ap.add_argument('--before-dir', default='')
    ap.add_argument('--before-prefix', default='av-before-')
    ap.add_argument('--out', required=True)
    ap.add_argument('--scale', type=int, default=8)
    ap.add_argument('--margin', type=int, default=6)
    ap.add_argument('--shot-prefix', default='31_options_')
    args = ap.parse_args()

    with open(args.marks, 'r', encoding='utf-8', errors='replace') as f:
        marks = parse_marks(f.read())

    # 只判"勾选态 = 显示"的那些（self=True）：一页挑第一个，页面顺序按 key 排
    picked = {}
    for row, key, on, sprite, rect in marks:
        if not on:
            continue
        picked.setdefault(key, (row, sprite, rect))
    if not picked:
        sys.stderr.write('no checked mark in the probe dump -- nothing to crop\n')
        return 2

    from PIL import Image, ImageDraw, ImageFont

    def crop(path, rect, margin):
        im = Image.open(path).convert('RGB')
        w, h = im.size
        x0, y0, x1, y1 = rect
        box = (max(0, x0 - margin), max(0, h - y1 - margin),
               min(w, x1 + margin), min(h, h - y0 + margin))
        return im.crop(box)

    def orange(im, margin):
        """勾色 = 原版 CheckButtonCheck 255,176,0（在深色框内的覆盖像素）：r-b 大且 r 亮。
        只在**画框那 8x8 格**里数（crop 四周的 margin 是背景，不能算进去）。"""
        inner = im.crop((margin, margin, margin + 8, margin + 8))
        return sum(1 for (r, g, b) in inner.getdata() if (r - b) > 60 and r > 120)

    keys = sorted(picked)
    before_imgs, after_imgs, labels = [], [], []
    notes = []
    print('# per-page pixel diff (before -> after), restricted to the whole 1920x1080 frame')
    print('# crop = probe on-screen rect + %d px margin -> %dx%d px per cell'
          % (args.margin, 8 + 2 * args.margin, 8 + 2 * args.margin))
    for key in keys:
        row, sprite, rect = picked[key]
        after_p = os.path.join(args.shot_dir, args.shot_prefix + key + '.png')
        before_p = os.path.join(args.before_dir, args.before_prefix + args.shot_prefix + key + '.png') \
            if args.before_dir else ''
        if not os.path.exists(after_p):
            sys.stderr.write('missing after frame: %s\n' % after_p)
            return 2
        a = Image.open(after_p).convert('RGB')
        ca = crop(after_p, rect, args.margin)
        after_imgs.append(ca)
        oa = orange(ca, args.margin)
        note = ''
        before_note = 'n/a'
        if before_p and os.path.exists(before_p):
            b = Image.open(before_p).convert('RGB')
            cb = crop(before_p, rect, args.margin)
            before_imgs.append(cb)
            ob = orange(cb, args.margin)
            # 旧代码的勾选态 = 一块**实心**方块 ⇒ 8x8 那格里 64 个像素全是勾色；
            # 新代码 = 一个**稀疏**勾形 ⇒ 只有笔画覆盖的那些像素是勾色。两个数一比就知道换了形态。
            kind = (u'实心方块' if ob >= 60 else (u'稀疏勾形' if ob > 0 else u'空框(当时未勾选)'))
            before_note = ('orange=%2d/64 (%s)' % (ob, kind))
            if b.size == a.size:
                from PIL import ImageChops
                diff = ImageChops.difference(a, b).convert('L')
                bbox = diff.getbbox()
                n = sum(1 for v in diff.getdata() if v != 0)
                # 位置用"屏幕坐标（y 向上）"报，和探针同一口径
                sbb = ''
                if bbox:
                    sbb = ('screen x[%d..%d] y[%d..%d]'
                           % (bbox[0], bbox[2] - 1, a.size[1] - bbox[3], a.size[1] - bbox[1] - 1))
                note = ('   diff px=%d  bbox=%s  %s  before_%s' % (n, bbox, sbb, before_note))
                if not sbb:
                    note += '   (identical frames)'
                # 差区必须落在 8x8 那一格内（改动没有牵动任何别的像素）
                x0, y0, x1, y1 = rect
                inside = (bbox is None) or (bbox[0] >= x0 - 1 and bbox[1] >= (a.size[1] - y1 - 1)
                                            and bbox[2] <= x1 + 1 and bbox[3] <= (a.size[1] - rect[1] + 1))
                if not inside:
                    sys.stderr.write('WARNING: diff bbox %s escapes the mark rect %s on %s\n'
                                     % (bbox, rect, key))
            else:
                note = '   size mismatch before=%s after=%s' % (b.size, a.size)
        else:
            note = '   (no before frame)'
        print('  %-12s %-24s self=True sprite=%s rect=%s  after_orange=%d/64%s'
              % (key, row, sprite, rect, oa, note))
        labels.append(u'%s / %s' % (key, row))
        notes.append((u'BEFORE ' + (kind if before_note != 'n/a' else 'n/a'),
                      u'AFTER sprite=menu_check %d/64' % oa))

    sw, sh = after_imgs[0].size
    scale = args.scale
    cw, ch = sw * scale, sh * scale
    cols = len(keys)
    rows = 2 if before_imgs else 1
    lab_h = 40
    head_h = 30
    W = cols * cw
    H = head_h + rows * (ch + lab_h)
    sheet = Image.new('RGB', (W, H), (20, 20, 24))
    d = ImageDraw.Draw(sheet)

    def font(size):
        for p in (r'C:\Windows\Fonts\msyh.ttc', r'C:\Windows\Fonts\simhei.ttf'):
            if os.path.exists(p):
                try:
                    return ImageFont.truetype(p, size)
                except Exception:
                    continue
        return ImageFont.load_default()

    ft = font(14)
    d.text((6, 8), u'勾选框的勾（画框仍是 8x8，未动布局）：上排 = 改造前的旧帧 / 下排 = 改造后（原版 Marlett '
                   u'gid 12 预渲染贴图）；每格按只读探针给的**上屏矩形**裁切 + %dx nearest 放大，' % scale,
           fill=(235, 235, 240), font=ft)
    for c, key in enumerate(keys):
        x = c * cw
        rows_im = [before_imgs[c], after_imgs[c]] if before_imgs else [after_imgs[c]]
        for r, im in enumerate(rows_im):
            y = head_h + r * (ch + lab_h)
            sheet.paste(im.resize((cw, ch), Image.NEAREST), (x, y))
            d.rectangle([x, y, x + cw - 1, y + ch - 1], outline=(70, 70, 80))
            tag = (notes[c][r] if before_imgs else notes[c][1])
            d.text((x + 4, y + ch + 3), tag, fill=(235, 235, 240), font=ft)
            d.text((x + 4, y + ch + 19), labels[c], fill=(180, 180, 195), font=ft)

    sheet.save(args.out)
    print('sheet -> %s  (%dx%d, %d page(s), cell %dx%d, x%d)' % (args.out, W, H, cols, sw, sh, scale))
    return 0


if __name__ == '__main__':
    sys.exit(main())
