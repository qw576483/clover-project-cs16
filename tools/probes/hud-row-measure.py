"""量原版 HUD 底部那排（血量/护甲/金钱/弹药）在 640x480 实机帧里的落点。

载体：原版资源/参照物/viewmodel/viewmodel_deagle_640x480.png（640x480，4:3，第一人称 + HUD）
判据口径：原版 HUD 元素按屏幕边缘定位 ⇒ 用「距屏左 / 距屏底」的像素值 × (1920/640 = 3) 换算到本工程画布。
"""
import numpy as np
from PIL import Image

SRC = r"C:\Work\Server\f-v2\clover-project-cs16\原版资源\参照物\viewmodel\viewmodel_deagle_640x480.png"
OUT = r"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test"

img = Image.open(SRC).convert("RGB")
a = np.asarray(img).astype(np.int16)
h, w, _ = a.shape
print("size:", w, "x", h, " scale to 1920 =", 1920.0 / w)

r, g, b = a[:, :, 0], a[:, :, 1], a[:, :, 2]
# HUD = 橙黄系（r 明显大于 b）且不算暗
ink = (r - b > 25) & (r > 85)

band0 = h - 100
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
        if x - start >= 2:
            runs.append((start, x - 1))
        inrun = False
if inrun and w - start >= 2:
    runs.append((start, w - 1))

print("列簇 (x0,x1) 宽度>=2:")
for (x0, x1) in runs:
    sub = ink[band0:h, x0:x1 + 1]
    rows = np.where(sub.any(axis=1))[0]
    ytop = band0 + rows.min()
    ybot = band0 + rows.max()
    print(f"  x=[{x0},{x1}] w={x1-x0+1} y=[{ytop},{ybot}] h={ybot-ytop+1} "
          f"| 距左={x0} 距右={w-1-x1} 距底={h-1-ybot} "
          f"| ×3: 距左={x0*3} 宽={(x1-x0+1)*3} 距底={(h-1-ybot)*3} 高={(ybot-ytop+1)*3}")

crop = img.crop((0, band0, 640, h)).resize((640 * 3, 100 * 3), Image.NEAREST)
crop.save(OUT + r"\orig-hudrow-3x.png")
print("saved", OUT + r"\orig-hudrow-3x.png")
