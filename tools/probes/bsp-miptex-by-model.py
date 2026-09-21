# -*- coding: utf-8 -*-
"""判据资产：按 brush model 列出 de_dust2.bsp 每个实体模型所用 miptex（贴图名）与面积。

用途：B-02「木箱材质」根因判定 —— 10 个 `func_breakable` 木箱各自绑哪个 `*N` brush model，
      该 model 的面上挂的是哪个 miptex（= 原版贴图名），从而判断我方 geo 组（组名 = PNG 名）
      有没有按这个名字搬运原版像素。

口径（GoldSource BSP v30）：
  lump[2] TEXTURES : int32 nummiptex + nummiptex × int32 offset；每项 = name[16] + int32 w + int32 h + int32 mipofs[4]
  lump[3] VERTICES : n = len/12（3 float）
  lump[6] TEXINFO  : 无计数前缀，n = len/40；miptex 索引在 +32
  lump[7] FACES    : 无计数前缀，n = len/20；planenum+0 / side+2 / firstedge+4 / numedges+8 / texinfo+10
  lump[12] EDGES   : n = len/4（2×u16 顶点索引）
  lump[13] SURFEDGES: n = len/4（int32：>=0 用 edge.v[0]，<0 用 edge.v[1]）
  lump[14] MODELS  : 无计数前缀，n = len/64；mins(12) maxs(12) origin(12) headnode(4) visleafs(4) firstface(4) numfaces(4)
只读，不改盘。
"""
import os
import re
import struct
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BSP = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2.bsp')

data = open(BSP, 'rb').read()
assert struct.unpack_from('<i', data, 0)[0] == 30, 'not BSP30'
lumps = [struct.unpack_from('<ii', data, 4 + 8 * i) for i in range(15)]

# ---- lump 2: miptex 名 ----
o2, l2 = lumps[2]
n_name = struct.unpack_from('<i', data, o2)[0]
names = []
for i in range(n_name):
    mo = struct.unpack_from('<i', data, o2 + 4 + 4 * i)[0]
    names.append(data[o2 + mo:o2 + mo + 16].split(b'\0')[0].decode('latin-1') if mo >= 0 else '')

# ---- lump 6: texinfo ----
o6, l6 = lumps[6]
n_ti = l6 // 40
ti_miptex = [struct.unpack_from('<i', data, o6 + 40 * i + 32)[0] for i in range(n_ti)]

# ---- lump 7: faces ----
o7, l7 = lumps[7]
n_face = l7 // 20
face_firstedge = [struct.unpack_from('<i', data, o7 + 20 * i + 4)[0] for i in range(n_face)]
face_nedge = [struct.unpack_from('<H', data, o7 + 20 * i + 8)[0] for i in range(n_face)]
face_ti = [struct.unpack_from('<H', data, o7 + 20 * i + 10)[0] for i in range(n_face)]

# ---- lump 3 / 12 / 13 ----
o3, l3 = lumps[3]
verts = [struct.unpack_from('<3f', data, o3 + 12 * i) for i in range(l3 // 12)]
o12, l12 = lumps[12]
edges = [struct.unpack_from('<HH', data, o12 + 4 * i) for i in range(l12 // 4)]
o13, l13 = lumps[13]
surfedges = [struct.unpack_from('<i', data, o13 + 4 * i)[0] for i in range(l13 // 4)]


def face_verts(f):
    fe, ne = face_firstedge[f], face_nedge[f]
    vs = []
    for k in range(ne):
        se = surfedges[fe + k]
        vs.append(verts[edges[se][0] if se >= 0 else edges[-se][1]])
    return vs


def poly_area(vs):
    """Newell 法向的模 × 0.5 = 多边形面积（不依赖面朝向）。"""
    if len(vs) < 3:
        return 0.0
    ax = ay = az = 0.0
    for i in range(len(vs)):
        p, q = vs[i], vs[(i + 1) % len(vs)]
        ax += p[1] * q[2] - p[2] * q[1]
        ay += p[2] * q[0] - p[0] * q[2]
        az += p[0] * q[1] - p[1] * q[0]
    return 0.5 * (ax * ax + ay * ay + az * az) ** 0.5


# ---- lump 14: models ----
o14, l14 = lumps[14]
n_model = l14 // 64
models = []
for i in range(n_model):
    b = o14 + 64 * i
    models.append((struct.unpack_from('<3f', data, b),
                   struct.unpack_from('<3f', data, b + 12),
                   struct.unpack_from('<3f', data, b + 24),
                   struct.unpack_from('<2i', data, b + 56)))  # firstface+56 / numfaces+60

# ---- entities ----
o0, l0 = lumps[0]
ent = data[o0:o0 + l0].split(b'\0')[0].decode('latin-1')
ents = [dict(re.findall(r'"([^"]*)"\s+"([^"]*)"', blk))
        for blk in re.findall(r'\{(.*?)\}', ent, re.S)]

print('BSP=%s' % os.path.relpath(BSP, ROOT).replace('\\', '/'))
print('miptex n=%d （有名 %d）；models n=%d；texinfo n=%d；faces n=%d'
      % (n_name, sum(1 for x in names if x), n_model, n_ti, n_face))
print('miptex 名表：%s' % ' '.join(x for x in names if x))
print()

WANT = ('func_breakable', 'func_bomb_target', 'func_buyzone')
print('== 实体 → brush model → 面所用 miptex（按面积降序）==')
for e in ents:
    cn = e.get('classname', '')
    if cn not in WANT:
        continue
    mdl = e.get('model', '')
    if not mdl.startswith('*'):
        continue
    mi = int(mdl[1:])
    mins, maxs, org, (ff, nf) = models[mi]
    area, cnt = Counter(), Counter()
    for f in range(ff, ff + nf):
        tm = ti_miptex[face_ti[f]]
        nm = names[tm] if 0 <= tm < len(names) else '?'
        area[nm] += poly_area(face_verts(f))
        cnt[nm] += 1
    print('%-17s %-5s span=(%.0f,%.0f,%.0f) nfaces=%d  tgt=%s'
          % (cn, mdl, maxs[0] - mins[0], maxs[1] - mins[1], maxs[2] - mins[2], nf,
             e.get('targetname', e.get('target', '-'))))
    for nm, a in area.most_common():
        print('        %-24s area=%-10.0f u2 faces=%d' % (nm, a, cnt[nm]))
