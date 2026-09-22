# -*- coding: utf-8 -*-
"""片BQ 一次性判据：**官方生成路径（Editor/MapGen → 引擎 MapBaker）会烘出哪张位图？**

口径逐字取自：
  client/Assets/Editor/MapGen/Dust2GeoData.cs:213-247  Dust2GeoData.BuildBlockedBitmap()
  client/Assets/Editor/MapGen/MapBakeRunner.cs:90-109   MapBakeOptions（CellSize/Origin/ProbeBottomY/ProbeTopY 全来自 geo）
  client/Assets/Editor/MapGen/Dust2Builder.cs:251-279   Level/Blockers = geo.Blockers（每格一个 BoxCollider）
即"格柱 y∈[GroundTopY+ProbeBottomY, GroundTopY+ProbeTopY] 与障碍 AABB 相交 ⇒ 阻挡"（含 ±1 格的 AABB 外扩判定）。

用法：python .ai-tmp/test/bq-bakepath.py
"""
import os
import struct

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MAP = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')

d = open(MAP, 'rb').read()
cell = struct.unpack_from('<f', d, 16)[0]
ox, oy, oz = struct.unpack_from('<3f', d, 20)
W, D, cc, sc, nlen = struct.unpack_from('<5I', d, 32)
o = 64 + nlen
BITS = d[o:o + (W * D + 7) // 8]
print('bytes: %dx%d cell=%.3f origin=(%.2f,%.2f,%.2f) colliderCount=%d spawnCount=%d' % (W, D, cell, ox, oy, oz, cc, sc))

g = open(GEO, 'rb').read()
p = [4]


def u32():
    v = struct.unpack_from('<I', g, p[0])[0]; p[0] += 4; return v


def f32():
    v = struct.unpack_from('<f', g, p[0])[0]; p[0] += 4; return v


u32()
gcell = f32(); gox = f32(); goz = f32(); gt = f32(); omh = f32(); pb = f32(); pt = f32()
gw = u32(); gd = u32()
for _ in range(6):
    f32()
gc = u32()
for _ in range(gc):
    p[0] += 48
    vc = u32(); ic = u32()
    p[0] += 12 * vc + 8 * vc + 12 * vc + 4 * ic
bc = u32()
BL = []
for _ in range(bc):
    BL.append(struct.unpack_from('<4I2f', g, p[0]))
    p[0] += 24
print('geo: %dx%d cell=%.3f origin=(%.2f,%.2f) probe[%.2f,%.2f] gt=%.2f blockers=%d'
      % (gw, gd, gcell, gox, goz, pb, pt, gt, len(BL)))


def actual(ix, iz):
    i = iz * W + ix
    return (BITS[i >> 3] & (1 << (i & 7))) != 0


# 官方引擎口径：格柱 ∩ 障碍 AABB（Dust2GeoData.BuildBlockedBitmap 逐字）
halfx = gcell * 0.5
halfy = (pt - pb) * 0.5
halfz = gcell * 0.5
probeCY = gt + (pt + pb) * 0.5
eng = [False] * (W * D)
for (ix0, iz0, ix1, iz1, ymin, ymax) in BL:
    mnx = gox + ix0 * gcell; mxx = gox + (ix1 + 1) * gcell
    mnz = goz + iz0 * gcell; mxz = goz + (iz1 + 1) * gcell
    mny = gt + ymin; mxy = gt + ymax
    cx = (mnx + mxx) * 0.5; ex = (mxx - mnx) * 0.5
    cy = (mny + mxy) * 0.5; ey = (mxy - mny) * 0.5
    cz = (mnz + mxz) * 0.5; ez = (mxz - mnz) * 0.5
    for iz in range(max(0, iz0 - 1), min(D - 1, iz1 + 1) + 1):
        for ix in range(max(0, ix0 - 1), min(W - 1, ix1 + 1) + 1):
            px = gox + (ix + 0.5) * gcell
            pz = goz + (iz + 0.5) * gcell
            if (abs(px - cx) < halfx + ex and abs(probeCY - cy) < halfy + ey
                    and abs(pz - cz) < halfz + ez):
                eng[iz * W + ix] = True

nw_act = sum(1 for i in range(W * D) if (BITS[i >> 3] & (1 << (i & 7))))
nw_eng = sum(1 for i in range(W * D) if eng[i])
print('\n当前 .bytes 位图 : 可走=%d 阻挡=%d' % (nw_act, W * D - nw_act))
print('官方生成口径位图   : 可走=%d 阻挡=%d' % (nw_eng, W * D - nw_eng))
diff = [i for i in range(W * D) if eng[i] != (not (BITS[i >> 3] & (1 << (i & 7))))]
print('逐格不一致 = %d 格（%.2f%% of %d）' % (len(diff), 100.0 * len(diff) / (W * D), W * D))
print('  其中 官方说阻挡 / 现状说可走 = %d 格（这些格会被"重烘"封死）'
      % sum(1 for i in diff if eng[i]))
print('  其中 官方说可走 / 现状说阻挡 = %d 格' % sum(1 for i in diff if not eng[i]))


def comps(pred):
    seen = set(); sizes = []; cells = []
    NB = ((1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1))
    for iz in range(D):
        for ix in range(W):
            if (ix, iz) in seen or not pred(ix, iz):
                continue
            st = [(ix, iz)]; seen.add((ix, iz)); acc = []
            while st:
                c = st.pop(); acc.append(c)
                for (sx, sz) in NB:
                    nb = (c[0] + sx, c[1] + sz)
                    if nb in seen or nb[0] < 0 or nb[1] < 0 or nb[0] >= W or nb[1] >= D:
                        continue
                    if not pred(*nb):
                        continue
                    seen.add(nb); st.append(nb)
            sizes.append(len(acc)); cells.append(acc)
    return sizes, cells


for label, pred in (('现状位图', actual), ('官方生成口径', lambda ix, iz: eng[iz * W + ix])):
    sz, cl = comps(pred)
    sz2 = sorted(sz, reverse=True)
    nw = sum(sz)
    print('  %s：分量=%d 主分量=%d 格（%.1f%%） 非主=%d 格' % (label, len(sz), sz2[0], 100.0 * sz2[0] / nw, nw - sz2[0]))
for i in diff[:24]:
    ix, iz = i % W, i // W
    print('   diff cell (%d,%d) 现状可走=%s 官方阻挡=%s' % (ix, iz, actual(ix, iz), eng[i]))
