"""量实机截帧里 HUD 那排的落点（画布坐标口径，参考分辨率 1920x1080 / match=0.5）。

用法: python measure-shot-hud.py <png> [--label 名称]
"""
import sys
import numpy as np
from PIL import Image

path = sys.argv[1]
img = Image.open(path).convert("RGB")
a = np.asarray(img).astype(np.int16)
h, w, _ = a.shape
# CanvasScaler: match=0.5 + 参考 1920x1080 → 两轴对数相等 ⇒ 单一 scale
scale = (w / 1920.0 + h / 1080.0) / 2.0 if abs(w / 1920.0 - h / 1080.0) < 1e-6 else min(w / 1920.0, h / 1080.0)
print(f"size={w}x{h} canvasScale={scale:.4f}")

r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
ink = (r - b > 40) & (r > 120)          # HUD 橙（#FFB000 系）
band0 = int(h * 0.55)
band = ink[band0:h, :]
colcount = band.sum(axis=0)

runs = []
inrun = False
start = 0
for x in range(w):
    if colcount[x] > 0 and not inrun:
        start = x
        inrun = True
    elif colcount[x] == 0 and inrun:
        if x - start >= 3:
            runs.append((start, x - 1))
        inrun = False
if inrun and w - start >= 3:
    runs.append((start, w - 1))

print("列簇（左上→右上）:")
for (x0, x1) in runs:
    sub = ink[band0:h, x0:x1 + 1]
    rows = np.where(sub.any(axis=1))[0]
    ytop = band0 + rows.min()
    ybot = band0 + rows.max()
    print(f"  x=[{x0},{x1}] y=[{ytop},{ybot}] | 画布: 距左={x0/scale:.1f} 距右={(w-1-x1)/scale:.1f} "
          f"距底={(h-1-ybot)/scale:.1f} 高={(ybot-ytop+1)/scale:.1f}")
