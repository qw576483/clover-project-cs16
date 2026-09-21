# -*- coding: utf-8 -*-
"""判据资产：de_dust2.bsp 的 miptex 清单 + 「内嵌像素能否当原版像素出处」的可用性判据。

⚠️ 结论先说（2026-09-21 · 切片Y 实测）：**本 BSP 的内嵌 miptex 不能当原版像素出处** ——
   44 项里 mip 链自洽的只有 15 项，把它们按「索引图 + 768B 调色板」解出来**全是噪声**
   （相邻像素平均通道差 ≈85；`djCredit2` 解成纯灰 `(95,95,95)`）。
   木箱/地面那几张（`MltryCrteSd2` / `MltryCrteTp` / `SandRoad` / `SandCCrete` / `-0Sand` / `SandTrim`）
   连自洽标记都没有（stub）。⇒ 原版像素载体只能是 `cs_dust.wad`，而它**不在盘**（见 `策划/对照表.md` §Y / BLOCKED-Y1）。
   本脚本保留 = 让这条**否证**可复跑，⛔ 不是让你拿它当"原版值"。

口径（GoldSource BSP v30 · lump[2]）：
  int32 nummiptex；随后 nummiptex × int32 offset（相对 lump 起点，-1 = 该项无名）
  每项 miptex = char name[16] + uint32 w + uint32 h + uint32 mipofs[4]（相对 miptex 起点；内嵌时 mipofs[0]==40）
  内嵌数据 = width*height 字节索引 + 768 字节 24bpp 调色板（BGR）
  自洽判据 = mips[0]==40 且 mips[1]==40+w*h 且 mips[3]==40+w*h+w*h//4+w*h//16
  噪声判据 = 相邻像素平均通道差（相干贴图应远小于噪声 ≈85）

用法：
  python tools/probes/bsp-miptex-extract.py                          # 清单 + 自洽/噪声指标
  python tools/probes/bsp-miptex-extract.py MltryCrteSd2 out.png     # 解出该 miptex（若自洽）
只读 BSP；输出 PNG 写到参数指定路径（默认落 .ai-tmp/test/）。
"""
import os
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BSP = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2.bsp')

data = open(BSP, 'rb').read()
assert struct.unpack_from('<i', data, 0)[0] == 30, 'not BSP30'
lumps = [struct.unpack_from('<ii', data, 4 + 8 * i) for i in range(15)]
o2, l2 = lumps[2]
n = struct.unpack_from('<i', data, o2)[0]

items = []
for i in range(n):
    mo = struct.unpack_from('<i', data, o2 + 4 + 4 * i)[0]
    if mo < 0:
        items.append((None, 0, 0, None))
        continue
    b = o2 + mo
    nm = data[b:b + 16].split(b'\0')[0].decode('latin-1')
    w, h = struct.unpack_from('<2I', data, b + 16)
    mips = struct.unpack_from('<4i', data, b + 24)
    items.append((nm, w, h, (mips, b)))


def decode(item):
    """-> (img, self_consistent)。self_consistent = mip 链自洽（≠ 像素可信）。"""
    nm, w, h, (mips, b) = item
    ok = (mips[0] == 40 and mips[1] == 40 + w * h
          and mips[3] == 40 + w * h + w * h // 4 + w * h // 16)
    if not ok:
        return None, False
    px = data[b + mips[0]:b + mips[0] + w * h]
    pal = data[b + mips[0] + w * h:b + mips[0] + w * h + 768]
    from PIL import Image
    return Image.frombytes('RGB', (w, h), bytes(
        v for p in px for v in (pal[3 * p + 2], pal[3 * p + 1], pal[3 * p]))), True


def neighbour_delta(im):
    """相邻像素平均通道差：相干贴图小（通常 <30），调色板噪声 ≈85。"""
    px = im.load()
    w, h = im.size
    tot = cnt = 0
    for y in range(0, h, 2):
        for x in range(0, w - 1, 2):
            a, b_ = px[x, y], px[x + 1, y]
            tot += abs(a[0] - b_[0]) + abs(a[1] - b_[1]) + abs(a[2] - b_[2])
            cnt += 3
    return tot / float(max(1, cnt))


if len(sys.argv) == 1:
    print('BSP=%s  miptex=%d' % (os.path.relpath(BSP, ROOT).replace('\\', '/'), n))
    print('%-20s %5s %5s  %-14s %s' % ('name', 'w', 'h', 'mip-chain', 'neighbourDelta(=噪声指标)'))
    sc = 0
    for it in items:
        nm, w, h, extra = it
        if nm is None:
            print('%-20s %5s %5s  -' % ('(unnamed)', '-', '-'))
            continue
        im, ok = decode(it)
        if ok:
            sc += 1
            print('%-20s %5d %5d  %-14s %.1f  %s' % (nm, w, h, 'self-consistent',
                                                     neighbour_delta(im),
                                                     '<-- 噪声，不可当原版像素' if neighbour_delta(im) > 50 else ''))
        else:
            print('%-20s %5d %5d  %-14s %s' % (nm, w, h, 'stub', 'mips=%s' % (extra[0],)))
    print('self-consistent %d / %d  ⇒  能被当成原版像素的：**0 张**（见文件头警告）' % (sc, n))
    sys.exit(0)

name = sys.argv[1]
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    ROOT, '.ai-tmp', 'test', '%s.png' % name)
for it in items:
    if it[0] != name:
        continue
    im, ok = decode(it)
    if not ok:
        print('ERROR: %s 是 stub（本 BSP 没内嵌它的像素）⇒ 载体不在盘' % name)
        sys.exit(2)
    im.save(out)
    print('OK: %s %dx%d -> %s   neighbourDelta=%.1f（>50 = 噪声，⛔ 别当原版像素）'
          % (name, im.size[0], im.size[1], out, neighbour_delta(im)))
    sys.exit(0)
print('ERROR: 没有名为 %s 的 miptex' % name)
sys.exit(3)
