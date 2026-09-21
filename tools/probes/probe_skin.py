#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片T 探针7（决定性）：用 .cs16anim 自己的 idle 轨道把网格蒙皮到 t=0，与 .cs16mdl 的同一模型对比。
若两者形状一致（只是姿态差）⇒ 轴映射没问题；若差一个 90° 旋转 ⇒ .cs16anim 的轴映射有误。"""
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


def inv(m):
    a = [[m[r][c] for c in range(3)] for r in range(3)]
    det = (a[0][0]*(a[1][1]*a[2][2]-a[1][2]*a[2][1]) - a[0][1]*(a[1][0]*a[2][2]-a[1][2]*a[2][0])
           + a[0][2]*(a[1][0]*a[2][1]-a[1][1]*a[2][0]))
    ia = [[0.0]*3 for _ in range(3)]
    ia[0][0] = (a[1][1]*a[2][2]-a[1][2]*a[2][1])/det; ia[0][1] = (a[0][2]*a[2][1]-a[0][1]*a[2][2])/det; ia[0][2] = (a[0][1]*a[1][2]-a[0][2]*a[1][1])/det
    ia[1][0] = (a[1][2]*a[2][0]-a[1][0]*a[2][2])/det; ia[1][1] = (a[0][0]*a[2][2]-a[0][2]*a[2][0])/det; ia[1][2] = (a[0][2]*a[1][0]-a[0][0]*a[1][2])/det
    ia[2][0] = (a[1][0]*a[2][1]-a[1][1]*a[2][0])/det; ia[2][1] = (a[0][1]*a[2][0]-a[0][0]*a[2][1])/det; ia[2][2] = (a[0][0]*a[1][1]-a[0][1]*a[1][0])/det
    it = [-sum(ia[r][k]*m[k][3] for k in range(3)) for r in range(3)]
    return [ia[0]+[it[0]], ia[1]+[it[1]], ia[2]+[it[2]]]


def xf(m, p):
    return [sum(m[r][k]*p[k] for k in range(3)) + m[r][3] for r in range(3)]


def read_anim(t):
    p = os.path.join(MD, t + '.cs16anim')
    b = open(p, 'rb').read(); o = 4
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
        (fl,) = struct.unpack_from('<I', b, o); o += 4
        vc, tc = struct.unpack_from('<2I', b, o); o += 8
        vo = o; o += vc*12
        uo = o; o += vc*8
        no = o; o += vc*12
        bo = o; o += vc
        to = o; o += tc*3*4
        subs.append((tex, vc, tc, vo, uo, no, bo, to))
    (nk,) = struct.unpack_from('<I', b, o); o += 4
    clips = {}
    for _ in range(nk):
        nm, o = rd_str(b, o)
        fps, loop, nb2 = struct.unpack_from('<fII', b, o); o += 12
        tracks = []
        for _b in range(nb2):
            (kc,) = struct.unpack_from('<I', b, o); o += 4
            tr = []
            for k in range(kc):
                tt, x, y, z, qx, qy, qz, qw = struct.unpack_from('<8f', b, o); o += 32
                tr.append((tt, (x, y, z), (qx, qy, qz, qw)))
            tracks.append(tr)
        clips[nm] = (fps, loop, tracks)
    return key, bones, subs, clips, b


def bbox(vs):
    lo = [min(v[k] for v in vs) for k in range(3)]
    hi = [max(v[k] for v in vs) for k in range(3)]
    return lo, hi


for t, clipname in ((sys.argv[1:2] or ['vm_ak47']) + (['idle'] if len(sys.argv) < 3 else [sys.argv[2]]),):
    key, bones, subs, clips, b = read_anim(t)
    # bind world
    BW = []
    for i, (nm, par, pp, q) in enumerate(bones):
        m = qmat(*q); m = [m[0]+[pp[0]], m[1]+[pp[1]], m[2]+[pp[2]]]
        BW.append(mul(BW[par], m) if 0 <= par < i else m)
    if clipname not in clips:
        print(f"!! no clip {clipname}; have {list(clips)}"); sys.exit(1)
    fps, loop, tracks = clips[clipname]
    # 取每根骨骼 t=0（第一帧）的 local = 轨道第 0 键；没有轨道的骨骼用 bind local
    LW = []
    for i, (nm, par, pp, q) in enumerate(bones):
        loc = pp; qq = q
        if i < len(tracks) and len(tracks[i]) > 0:
            _, loc, qq = tracks[i][0]
        m = qmat(*qq); m = [m[0]+[loc[0]], m[1]+[loc[1]], m[2]+[loc[2]]]
        LW.append(mul(LW[par], m) if 0 <= par < i else m)
    # 蒙皮
    allv = []
    for (tex, vc, tc, vo, uo, no, bo, to) in subs:
        for i in range(vc):
            x, y, z = struct.unpack_from('<3f', b, vo + i*12)
            bi = b[bo+i]
            if bi >= len(BW): bi = 0
            v = xf(mul(LW[bi], inv(BW[bi])), (x, y, z))
            allv.append(v)
    lo, hi = bbox(allv)
    print(f"[{t}] clip={clipname} fps={fps} verts={len(allv)}")
    print(f"   bind-pose bbox  = " + ' '.join(f"{c}[{min(struct.unpack_from('<3f', b, s[3]+i*12)[k] for i in range(s[1])):+.3f}..]" for k, c in enumerate('xyz') for s in [subs[0]]))
    print(f"   skinned@t0 bbox x[{lo[0]:+.3f},{hi[0]:+.3f}] y[{lo[1]:+.3f},{hi[1]:+.3f}] z[{lo[2]:+.3f},{hi[2]:+.3f}]"
          f"  span=({hi[0]-lo[0]:.3f},{hi[1]-lo[1]:.3f},{hi[2]-lo[2]:.3f})")
