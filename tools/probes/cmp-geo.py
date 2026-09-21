# -*- coding: utf-8 -*-
"""对比 当前 geo 与 原版资源/备份 里的 geo 副本：组表 / 参数 / 阻挡盒数。只读。"""
import os, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load(path):
    data = open(path, 'rb').read()
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v
    assert data[0:4] == b'CD2G', path
    u32()
    cell, ox, oz, gt, omh, pb, pt = (f32() for _ in range(7))
    w, d = u32(), u32()
    bbox = [f32() for _ in range(6)]
    gc = u32()
    gs = []
    for gi in range(gc):
        name = data[o[0]:o[0] + 48].split(b'\0')[0].decode('utf-8', 'replace'); o[0] += 48
        vc, ic = u32(), u32()
        gs.append((name, vc, ic, o[0]))
        o[0] += 12 * vc + 8 * vc + 12 * vc + 4 * ic
    bc = u32()
    return dict(path=path, size=len(data), cell=cell, ox=ox, oz=oz, w=w, d=d, bbox=bbox,
                groups=gs, blockers=bc, data=data)


a = load(os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin'))
bakdir = os.path.join(ROOT, '\u539f\u7248\u8d44\u6e90', '\u5907\u4efd')
cands = [f for f in os.listdir(bakdir) if f.endswith('.bak') and 'geo' in f]
print("备份候选：", cands)
for f in cands:
    b = load(os.path.join(bakdir, f))
    print("\n== %s ==" % f)
    for tag, g in (('cur', a), ('bak', b)):
        print("  %-4s size=%-8d cell=%.3f origin=(%.1f,%.1f) %dx%d 组=%d 阻挡盒=%d"
              % (tag, g['size'], g['cell'], g['ox'], g['oz'], g['w'], g['d'], len(g['groups']), g['blockers']))
    names_a = [n for n, _, _, _ in a['groups']]
    names_b = [n for n, _, _, _ in b['groups']]
    print("  组名集合相同：%s" % (sorted(names_a) == sorted(names_b)))
    only_a = set(names_a) - set(names_b)
    only_b = set(names_b) - set(names_a)
    if only_a or only_b:
        print("  仅 cur: %s ；仅 bak: %s" % (sorted(only_a), sorted(only_b)))
    ma = {n: (vc, ic) for n, vc, ic, _ in a['groups']}
    mb = {n: (vc, ic) for n, vc, ic, _ in b['groups']}
    diff = [n for n in names_a if n in mb and ma[n] != mb[n]]
    print("  (vc,ic) 不同的组 %d 个：" % len(diff))
    for n in sorted(diff):
        print("     %-24s cur vc=%-6d ic=%-6d | bak vc=%-6d ic=%-6d (tris %d → %d)"
              % (n, ma[n][0], ma[n][1], mb[n][0], mb[n][1], ma[n][1] // 3, mb[n][1] // 3))
