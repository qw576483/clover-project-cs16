#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片T 探针6：读 .cs16mdl（C16M，由 cs16_build.py 导出，映射 = M-18 的 (HL.y,HL.z,HL.x)）
逐 part/sub 顶点 bbox + 全局 bbox —— 用来交叉验证 .cs16anim 的轴映射。"""
import struct, os, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
MD = os.path.join(ROOT, 'client', 'Assets', 'Editor', 'Views', 'ModelData')


def rd_str(b, o):
    (n,) = struct.unpack_from('<I', b, o); o += 4
    return b[o:o+n].decode('utf-8', 'replace'), o+n


for t in (sys.argv[1:] or ['player_T', 'vm_ak47']):
    p = os.path.join(MD, t + '.cs16mdl')
    if not os.path.exists(p):
        print('missing', p); continue
    b = open(p, 'rb').read()
    assert b[0:4] == b'C16M', b[0:4]
    o = 4
    (ver,) = struct.unpack_from('<I', b, o); o += 4
    key, o = rd_str(b, o)
    (pc,) = struct.unpack_from('<I', b, o); o += 4
    nh, sc = struct.unpack_from('<2f', b, o); o += 8
    bmin = struct.unpack_from('<3f', b, o); o += 12
    bmax = struct.unpack_from('<3f', b, o); o += 12
    print('=' * 80)
    print(f"[{t}.cs16mdl] key={key} parts={pc} nativeHeight={nh:.3f} scale={sc:.6f} declaredBounds={[round(v,4) for v in bmin]}..{[round(v,4) for v in bmax]}")
    gmin = [1e9]*3; gmax = [-1e9]*3; tv = 0; tt = 0
    for pi in range(pc):
        zone, o = rd_str(b, o)
        cy, ch, cr = struct.unpack_from('<3f', b, o); o += 12
        (nsub,) = struct.unpack_from('<I', b, o); o += 4
        pmn = [1e9]*3; pmx = [-1e9]*3
        for si in range(nsub):
            tex, o = rd_str(b, o)
            (flags,) = struct.unpack_from('<I', b, o); o += 4
            vc, tc = struct.unpack_from('<2I', b, o); o += 8
            for i in range(vc):
                x, y, z = struct.unpack_from('<3f', b, o + i*12)
                # 顶点相对本块 centerY
                wy = y + cy
                for k, v in enumerate((x, wy, z)):
                    if v < pmn[k]: pmn[k] = v
                    if v > pmx[k]: pmx[k] = v
                    if v < gmin[k]: gmin[k] = v
                    if v > gmax[k]: gmax[k] = v
            tv += vc; tt += tc
            o += vc*12 + vc*8 + vc*12
            o += tc*3*4
        print(f"  part[{pi}] zone={zone:10s} cy={cy:+.4f} capH={ch:.3f} capR={cr:.3f} subs={nsub}"
              f"  bbox x[{pmn[0]:+.3f},{pmx[0]:+.3f}] y[{pmn[1]:+.3f},{pmx[1]:+.3f}] z[{pmn[2]:+.3f},{pmx[2]:+.3f}]"
              f"  span=({pmx[0]-pmn[0]:.3f},{pmx[1]-pmn[1]:.3f},{pmx[2]-pmn[2]:.3f})")
    print(f"  TOTAL verts={tv} tris={tt}")
    print(f"  GLOBAL bbox x[{gmin[0]:+.3f},{gmax[0]:+.3f}] y[{gmin[1]:+.3f},{gmax[1]:+.3f}] z[{gmin[2]:+.3f},{gmax[2]:+.3f}]")
    print(f"  GLOBAL span = ({gmax[0]-gmin[0]:.3f}, {gmax[1]-gmin[1]:.3f}, {gmax[2]-gmin[2]:.3f})")
