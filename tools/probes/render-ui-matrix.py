# -*- coding: utf-8 -*-
"""判据资产（tools/probes/）：把 `check-ui-flow-matrix.py` 的矩阵 TSV 画成一张格子图
（供联络图当一格用 —— "只有眼睛能判"的那一类要能在一张汇总图上看清"哪一格不对劲"）。

颜色口径（⛔ 不改数字，只做可视化；数字仍在 TSV 里）：
  绿 = OK · 灰 = 未采到（窗口没赶上 / 面板未实例化）· 红 = 不该显示却显示 · 橙 = 该显示却没显示
用法：python tools/probes/render-ui-matrix.py [--tsv ...] [--out ...]
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CJK = (r'C:\Windows\Fonts\msyh.ttc', r'C:\Windows\Fonts\simhei.ttf', r'C:\Windows\Fonts\simsun.ttc')


def font(size):
    for p in CJK:
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, size)
            except Exception:
                pass
    return ImageFont.load_default()


def main():
    args = sys.argv[1:]
    tsv = os.path.join(ROOT, '.ai-tmp', 'test', 'bvr-ui-flow-matrix.tsv')
    out = os.path.join(ROOT, '.ai-tmp', 'screenshots', 'bv-r', 'ui-flow-matrix.png')
    i = 0
    while i < len(args):
        if args[i] == '--tsv':
            tsv = args[i + 1]; i += 2
        elif args[i] == '--out':
            out = args[i + 1]; i += 2
        else:
            i += 1

    with open(tsv, 'rb') as f:
        lines = f.read().decode('utf-8', 'replace').replace('\r\n', '\n').strip().split('\n')
    head = lines[0].split('\t')
    rows = [dict(zip(head, ln.split('\t'))) for ln in lines[1:] if ln.strip()]

    states = []
    panels = []
    for r in rows:
        if r['态'] not in states:
            states.append(r['态'])
        if r['面板'] not in panels:
            panels.append(r['面板'])
    cell_w, cell_h = 46, 26
    lab_w, head_h = 170, 96
    short = {p: p.replace('Panel', '') for p in panels}
    W = lab_w + cell_w * len(panels) + 8
    H = head_h + cell_h * len(states) + 60
    im = Image.new('RGB', (W, H), (18, 18, 22))
    d = ImageDraw.Draw(im)
    f_small = font(13)
    f_cell = font(11)
    f_title = font(16)
    d.text((6, 6), '16 流程态 x 16 面板 : activeInHierarchy 期望/实际对账 (片BV-R)', fill=(235, 235, 240), font=f_title)
    d.text((6, 28), '绿=OK  灰=未采到(窗口没赶上/面板未实例化)  红=不该显示却显示  橙=该显示却没显示',
           fill=(170, 170, 180), font=f_small)
    d.text((6, 46), 'TSV: .ai-tmp/test/bvr-ui-flow-matrix.tsv   运行时节点树: .ai-tmp/test/bvr-ui-visible.tsv',
           fill=(140, 140, 150), font=f_small)

    for c, p in enumerate(panels):
        d.text((lab_w + c * cell_w + 2, head_h - 22), short[p][:6], fill=(200, 200, 210), font=f_cell)
    COL = {'OK': (40, 130, 60), '不该显示却显示': (200, 40, 40), '该显示却没显示': (215, 130, 20)}
    idx = {(r['态'], r['面板']): r for r in rows}
    for r_i, st in enumerate(states):
        y = head_h + r_i * cell_h
        d.text((4, y + 5), st, fill=(220, 220, 230), font=f_small)
        for c, p in enumerate(panels):
            r = idx.get((st, p))
            x = lab_w + c * cell_w
            if r is None:
                col = (60, 60, 66)
            elif r['判定'].startswith('未采到'):
                col = (95, 95, 100)
            else:
                col = COL.get(r['判定'], (40, 130, 60))
            d.rectangle([x + 1, y + 1, x + cell_w - 3, y + cell_h - 3], fill=col)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    im.save(out)
    print('ui matrix sheet -> %s (%dx%d, %d states x %d panels)' % (out, W, H, len(states), len(panels)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
