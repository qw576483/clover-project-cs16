#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片T 探针4（决定性）：顶点 → 其所属骨骼(绑定世界位置) 的距离分布。
判据：若导出坐标系一致，每个顶点都应靠近自己的骨骼（四肢 <0.45m）；
      若骨骼与网格用了不同坐标系，距离会系统性偏大（>1m）。"""
import struct, os, sys, math

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
MD = os.path.join(ROOT, 'client', 'Assets', 'Editor', 'Views', 'ModelData')


def rd_str(b, o):
    (n,) = struct.unpack_from('<I', b, o); o += 4
    return b[o:o+n].decode('utf-8', 'replace'), o+n


def qmat(x, y, z, w):
    return [[1-2*y*y-2*z*z, 2*x*y-2*w*z, 2*x*z+2*w*y],
            [2*x*y+2*w*z, 1-2*x*x-2*z*z, 2*y*z-2*w*x],
            [2*x*z-2*w*y, 2*y*z+2*w*x, 1-2*x*x-2*y*y]]


def mul(a, b):
    o = [[0.0]*3 for _ in range(3)]; t = [0.0]*3
    for r in range(3):
        for c in range(3):
            o[r][c] = sum(a[r][k]*b[k][c] for k in range(3))
        t[r] = sum(a[r][k]*b[k][3] for k in range(3)) + a[r][3]
    return [o[0]+[t[0]], o[1]+[t[1]], o[2]+[t[2]]]


def load(path):
    b = open(path, 'rb').read(); o = 4
    o += 4
    key, o = rd_str(b, o)
    scale, ox, oy, oz = struct.unpack_from('<4f', b, o); o += 16
    (nb,) = struct.unpack_from('<I', b, o); o += 4
    bones = []
    for _ in range(nb):
        nm, o = rd_str(b, o)
        (par,) = struct.unpack_from('<i', b, o); o += 4
        px, py, pz, qx, qy, qz, qw = struct.unpack_from('<7f', b, o); o += 28
        bones.append((nm, par, (px, py, pz), (qx, qy, qz, qw)))
    (nc,) = struct.unpack_from('<I', b, o); o += 4
    for _ in range(nc):
        _, o = rd_str(b, o); o += 16
    (ns,) = struct.unpack_from('<I', b, o); o += 4
    subs = []
    for _ in range(ns):
        tex, o = rd_str(b, o)
        (flags,) = struct.unpack_from('<I', b, o); o += 4
        vc, tc = struct.unpack_from('<2I', b, o); o += 8
        vo = o; o += vc*12
        o += vc*8; o += vc*12
        bo = o; o += vc
        o += tc*3*4
        subs.append((tex, vc, vo, bo))
    W = []
    for i, (nm, par, p, q) in enumerate(bones):
        m = qmat(*q)
        m = [m[0]+[p[0]], m[1]+[p[1]], m[2]+[p[2]]]
        W.append(mul(W[par], m) if 0 <= par < i else m)
    return key, scale, bones, W, subs, b


for t in (sys.argv[1:] or ['player_T', 'player_T_arctic', 'vm_ak47', 'vm_usp']):
    p = os.path.join(MD, t + '.cs16anim')
    if not os.path.exists(p): print('missing', p); continue
    key, scale, bones, W, subs, b = load(p)
    print('=' * 78)
    print(f"[{t}] bones={len(bones)} scale={scale}")
    allmax = 0.0
    for (tex, vc, vo, bo) in subs:
        ds = []
        for i in range(vc):
            x, y, z = struct.unpack_from('<3f', b, vo + i*12)
            bi = b[bo + i]
            if bi >= len(W): bi = 0
            bm = W[bi]
            bx, by, bz = bm[0][3], bm[1][3], bm[2][3]
            ds.append(math.sqrt((x-bx)**2 + (y-by)**2 + (z-bz)**2))
        ds.sort()
        allmax = max(allmax, ds[-1])
        print(f"  sub {tex:28s} v={vc:4d}  dist_to_own_bone  med={ds[len(ds)//2]:.3f} p90={ds[int(len(ds)*0.9)]:.3f} max={ds[-1]:.3f}")
    print(f"  => MAX dist to own bone = {allmax:.3f} m   (人形四肢应 < 0.45)")
