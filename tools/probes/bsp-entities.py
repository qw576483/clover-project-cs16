# -*- coding: utf-8 -*-
"""一次性诊断：解析 de_dust2.bsp 的 entity lump + miptex 名 + 各贴图面数，
用来回答「用户看到的匪家楼梯扶手到底是什么」与「哪些门是可开的 func_door」。
只读，不改盘。
"""
import os, re, struct, sys
from collections import Counter, OrderedDict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BSP = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2.bsp')
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')

data = open(BSP, 'rb').read()
ver = struct.unpack_from('<i', data, 0)[0]
lumps = [struct.unpack_from('<ii', data, 4 + 8 * i) for i in range(15)]
print("BSP ver=%d lumps=%s" % (ver, lumps))

# ---- lump 0: entities ----
off0, len0 = lumps[0]
ent_text = data[off0:off0 + len0].split(b'\0')[0].decode('latin-1')
# 拆成 { ... } 块
blocks = re.findall(r'\{(.*?)\}', ent_text, re.S)
print("实体块数=%d" % len(blocks))


def parse_block(b):
    kv = OrderedDict()
    for m in re.finditer(r'"([^"]*)"\s+"([^"]*)"', b):
        kv[m.group(1)] = m.group(2)
    return kv


ents = [parse_block(b) for b in blocks]
kinds = Counter(e.get('classname', '?') for e in ents)
print("classname 统计：%s" % dict(kinds.most_common()))

# 门 / 可推 / 可破坏 / 暂时物
for e in ents:
    cn = e.get('classname', '')
    if cn.startswith('func_door') or cn in ('func_breakable', 'func_pushable', 'func_wall',
                                            'func_illusionary', 'func_button', 'func_rotating'):
        print("  %-22s origin=%-22s angles=%-8s targetname=%-14s model=%-8s texture=%s spin=%s speed=%s"
              % (cn, e.get('origin', '-'), e.get('angles', '-'), e.get('targetname', '-'),
                 e.get('model', '-'), e.get('texture', '-'), e.get('spawnflags', '-'), e.get('speed', '-')))
    # 所有关于门的键
for e in ents:
    for k in e:
        if 'door' in k.lower() or 'door' in str(e[k]).lower():
            print("  [door-key] %s :: %s=%s" % (e.get('classname'), k, e[k]))

# 记录 func_door 的 brush model -> origin
doors = [e for e in ents if e.get('classname', '').startswith('func_door')]
print("\nfunc_door* 数量=%d" % len(doors))
for e in doors:
    print("   door model=%s origin=%s spd=%s dist=%s flg=%s tname=%s" %
          (e.get('model'), e.get('origin'), e.get('speed'), e.get('lip'), e.get('spawnflags'), e.get('targetname')))

# ---- lump 14: models（brush 模型的 face 范围）----
off14, len14 = lumps[14]
n14 = struct.unpack_from('<i', data, off14)[0]
nmodel = n14 if (n14 * 64 + 4 <= len14 + 8) else len14 // 64
mb = off14 + 4 if (n14 * 64 + 4 <= len14 + 8) else off14
models = []
for i in range(nmodel):
    base = mb + 64 * i
    fmin = struct.unpack_from('<3f', data, base)
    fmax = struct.unpack_from('<3f', data, base + 12)
    origin = struct.unpack_from('<3f', data, base + 24)
    headn = struct.unpack_from('<4i', data, base + 36)
    nface, = struct.unpack_from('<i', data, base + 44)
    models.append(dict(mins=fmin, maxs=fmax, origin=origin, headnode=headn, numfaces=nface))
print("\nmodels=%d" % len(models))
for i, m in enumerate(models):
    print("  model[%d] mins=(%.0f,%.0f,%.0f) maxs=(%.0f,%.0f,%.0f) origin=(%.0f,%.0f,%.0f) nfaces=%d"
          % (i, *m['mins'], *m['maxs'], *m['origin'], m['numfaces']))

# ---- lump 2 / 6 / 7：贴图名 + 面数 ----
off2, len2 = lumps[2]
nname = struct.unpack_from('<i', data, off2)[0]
names = []
for i in range(nname):
    mo = struct.unpack_from('<i', data, off2 + 4 + 4 * i)[0]
    if mo < 0:
        names.append(None); continue
    names.append(data[off2 + mo:off2 + mo + 16].split(b'\0')[0].decode('latin-1'))
off6, len6 = lumps[6]
n4 = struct.unpack_from('<i', data, off6)[0]
ninfo, tbase = (n4, off6 + 4) if n4 * 40 + 4 <= len6 + 8 else (len6 // 40, off6)
texinfo = [struct.unpack_from('<i', data, tbase + 40 * i + 32)[0] for i in range(ninfo)]
off7, len7 = lumps[7]
nf4 = struct.unpack_from('<i', data, off7)[0]
nface, fbase = (nf4, off7 + 4) if nf4 * 20 + 4 <= len7 + 8 else (len7 // 20, off7)
faces = Counter()
for i in range(nface):
    ti = struct.unpack_from('<H', data, fbase + 20 * i + 10)[0]
    if ti < len(texinfo) and 0 <= texinfo[ti] < len(names) and names[texinfo[ti]]:
        faces[names[texinfo[ti]]] += 1
print("\nmiptex 条目=%d（有名 %d）；有面的贴图=%d" % (len(names), sum(1 for x in names if x), len(faces)))
for nm, c in faces.most_common():
    print("   %-24s faces=%d" % (nm, c))

# geo 组名
def geo_groups(path):
    d = open(path, 'rb').read()
    o = [4]
    def u32():
        v = struct.unpack_from('<I', d, o[0])[0]; o[0] += 4; return v
    def f32():
        v = struct.unpack_from('<f', d, o[0])[0]; o[0] += 4; return v
    u32(); f32(); f32(); f32(); f32(); f32(); f32(); f32()
    w = u32(); dd = u32()
    for _ in range(6):
        f32()
    gc = u32(); gs = []
    for _ in range(gc):
        nm = d[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8', 'replace'); o[0] += 48
        vc = u32(); ic = u32()
        o[0] += 12 * vc + 8 * vc + 12 * vc + 4 * ic
        gs.append((nm, vc, ic // 3))
    return gs

gs = geo_groups(GEO)
print("\ngeo 组 %d 个：" % len(gs))
for nm, vc, t in gs:
    print("   %-24s verts=%-6d tris=%d" % (nm, vc, t))
