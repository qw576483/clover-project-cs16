# -*- coding: utf-8 -*-
"""判据资产：把一次取证的多张截图拼成**一张联络图**（skill reference/visual-loop.md 第八节）。
为什么必须拼成一张：判定权在"只有眼睛能判"的那一类时，读图的人（AI 或人）**只读汇总图**，
不逐张开图；每格左上角带**格号**，图下方带「格号 → 文件 → 看什么」的清单，
这样一条结论能对回具体的取证文件（可复核）。

用法：
    python tools/probes/compose-contact-sheet.py --out <输出png> --cols 4 \
        --cell "01=.ai-tmp/screenshots/f01-door-SandWllDoor2.png|看什么" ...
或（本片用）：--manifest <每行 `格号<TAB>文件<TAB>看什么` 的清单>
每格缩放到统一尺寸并留标签条；标题写环境基线（渲染设备 / 帧时间 / 分辨率）。
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# ============================================================================
#  CJK 字体（⛔ 只从系统字体里取，⛔ 不下载）
# ----------------------------------------------------------------------------
#  为什么必须有它：PIL 的默认位图字体（ImageFont.load_default()）只含 ASCII ⇒ 图内**中文
#  渲染成方块**（实测切片Q 的联络图6：格号 ASCII 可读、中文标签全是 □）。判定权在"只有眼睛
#  能判"那一类时，读图的人只读这张汇总图 ⇒ 中文标签必须是字，不能是方块。
#  取第一个存在的：微软雅黑 → 黑体 → 宋体；都没有才退回 PIL 默认字体（会成方块，但不崩）。
# ============================================================================
CJK_FONT_CANDIDATES = (
    r'C:\Windows\Fonts\msyh.ttc',    # 微软雅黑（Win7+）
    r'C:\Windows\Fonts\simhei.ttf',  # 黑体
    r'C:\Windows\Fonts\simsun.ttc',  # 宋体
)


def load_font(size):
    for path in CJK_FONT_CANDIDATES:
        if os.path.exists(path):
            try:
                return ImageFont.truetype(path, size)
            except Exception:
                continue
    return ImageFont.load_default()


def fit_text(draw, text, font, max_w):
    """把标签裁到 max_w 内（用 … 收尾）—— 只影响文字，⛔ 不改图尺寸。"""
    if draw.textlength(text, font=font) <= max_w:
        return text
    while text and draw.textlength(text + '\u2026', font=font) > max_w:
        text = text[:-1]
    return text + '\u2026'


def main():
    args = sys.argv[1:]
    out = None
    cols = 4
    cell_w, cell_h = 480, 320
    title = ''
    items = []
    i = 0
    while i < len(args):
        a = args[i]
        if a == '--out':
            out = args[i + 1]; i += 2
        elif a == '--cols':
            cols = int(args[i + 1]); i += 2
        elif a == '--cell-size':
            cell_w, cell_h = (int(x) for x in args[i + 1].split('x')); i += 2
        elif a == '--title':
            title = args[i + 1]; i += 2
        elif a == '--manifest':
            with open(args[i + 1], 'rb') as f:
                for line in f.read().decode('utf-8').replace('\r\n', '\n').split('\n'):
                    if not line.strip() or line.lstrip().startswith('#'):
                        continue
                    p = line.split('\t')
                    p += [''] * (3 - len(p))
                    items.append((p[0].strip(), p[1].strip(), p[2].strip()))
            i += 2
        else:
            i += 1
    if out is None or not items:
        print('用法：--out <png> [--cols N] [--cell-size WxH] [--title T] --manifest <清单>')
        return 2

    rows = (len(items) + cols - 1) // cols
    label_h = 26
    head_h = 30 if title else 0
    W = cols * cell_w
    H = head_h + rows * (cell_h + label_h)
    sheet = Image.new('RGB', (W, H), (20, 20, 24))
    d = ImageDraw.Draw(sheet)
    title_font = load_font(16)
    label_font = load_font(14)
    num_font = load_font(13)
    if title:
        d.text((6, 6), fit_text(d, title, title_font, W - 12), fill=(235, 235, 240), font=title_font)

    for k, (num, path, what) in enumerate(items):
        r, c = divmod(k, cols)
        x = c * cell_w
        y = head_h + r * (cell_h + label_h)
        full = path if os.path.isabs(path) else os.path.join(ROOT, path.replace('/', os.sep))
        if os.path.exists(full):
            im = Image.open(full).convert('RGB')
            im.thumbnail((cell_w, cell_h))
            sheet.paste(im, (x + (cell_w - im.width) // 2, y + (cell_h - im.height) // 2))
        else:
            d.text((x + 8, y + 8), fit_text(d, '<missing ' + path + '>', label_font, cell_w - 16),
                   fill=(255, 90, 90), font=label_font)
        d.rectangle([x, y, x + cell_w - 1, y + cell_h - 1], outline=(70, 70, 80))
        # 格号画在左上角（白底黑字，保证在任意画面上都读得出来）
        nw = 8 + 7 * len(num)
        d.rectangle([x + 2, y + 2, x + 2 + nw, y + 18], fill=(255, 255, 255))
        d.text((x + 6, y + 3), num, fill=(0, 0, 0), font=num_font)
        d.text((x + 4, y + cell_h + 5), fit_text(d, num + '  ' + what, label_font, cell_w - 8),
               fill=(210, 210, 220), font=label_font)

    sheet.save(out)
    print('contact sheet -> %s  (%dx%d, %d cells)' % (out, W, H, len(items)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
