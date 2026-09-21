#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片T 只读探针：量 .cs16anim / .cs16mdl / 原版 .mdl 的几何尺寸与贴图名。
一次性产物，位于 <项目根>/.ai-tmp/test/。"""
import struct, sys, os, glob

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
MD = os.path.join(ROOT, 'client', 'Assets', 'Editor', 'Views', 'ModelData')


def rd_str(b, o):
    (n,) = struct.unpack_from('<I', b, o); o += 4
    s = b[o:o + n].decode('utf-8', 'replace'); o += n
    return s, o


def read_c16anim(path):
    b = open(path, 'rb').read()
    o = 0
    magic = b[0:4].decode('ascii'); o = 4
    (ver,) = struct.unpack_from('<I', b, o); o += 4
    key, o = rd_str(b, o)
    scale, ox, oy, oz = struct.unpack_from('<4f', b, o); o += 16
    (nb,) = struct.unpack_from('<I', b, o); o += 4
    bones = []
    for _ in range(nb):
        nm, o = rd_str(b, o)
        (par,) = struct.unpack_from('<i', b, o); o += 4
        px, py, pz, qx, qy, qz, qw = struct.unpack_from('<7f', b, o); o += 28
        bones.append((nm, par, px, py, pz))
    (nc,) = struct.unpack_from('<I', b, o); o += 4
    caps = []
    for _ in range(nc):
        z, o = rd_str(b, o)
        (bone,) = struct.unpack_from('<i', b, o); o += 4
        cy, ch, cr = struct.unpack_from('<3f', b, o); o += 12
        caps.append((z, bone, cy, ch, cr))
    (ns,) = struct.unpack_from('<I', b, o); o += 4
    subs = []
    for _ in range(ns):
        tex, o = rd_str(b, o)
        (flags,) = struct.unpack_from('<I', b, o); o += 4
        vc, tc = struct.unpack_from('<2I', b, o); o += 8
        verts_off = o
        o += vc * 12
        o += vc * 8   # uv
        o += vc * 12  # norm
        o += vc       # boneidx
        o += tc * 3 * 4
        subs.append((tex, flags, vc, tc, verts_off))
    (nk,) = struct.unpack_from('<I', b, o); o += 4
    clipnames = []
    for _ in range(nk):
        nm, o = rd_str(b, o)
        fps, loop, nb2 = struct.unpack_from('<fII', b, o); o += 12
        clipnames.append((nm, fps, loop))
        for _b in range(nb2):
            (kc,) = struct.unpack_from('<I', b, o); o += 4
            o += kc * 32
    return dict(key=key, scale=scale, off=(ox, oy, oz), bones=bones, caps=caps,
                subs=subs, clips=clipnames, buf=b)


def bbox_of_verts(b, off, vc):
    lo = [1e9] * 3; hi = [-1e9] * 3
    for i in range(vc):
        x, y, z = struct.unpack_from('<3f', b, off + i * 12)
        for k, v in enumerate((x, y, z)):
            if v < lo[k]: lo[k] = v
            if v > hi[k]: hi[k] = v
    return lo, hi


def read_mdl(path):
    b = open(path, 'rb').read()
    (mid, ver) = struct.unpack_from('<2i', b, 0)
    name = b[8:8 + 64].split(b'\x00')[0].decode('ascii', 'replace')
    (length,) = struct.unpack_from('<i', b, 72)
    eyepos = struct.unpack_from('<3f', b, 76)
    mn = struct.unpack_from('<3f', b, 88)
    mx = struct.unpack_from('<3f', b, 100)
    bbmin = struct.unpack_from('<3f', b, 112)
    bbmax = struct.unpack_from('<3f', b, 124)
    (flags,) = struct.unpack_from('<i', b, 136)
    (numbones,) = struct.unpack_from('<i', b, 140)
    (boneindex,) = struct.unpack_from('<i', b, 144)
    (numhitboxes,) = struct.unpack_from('<i', b, 156)
    (numseq,) = struct.unpack_from('<i', b, 164)
    (numtextures,) = struct.unpack_from('<i', b, 180)
    (textureindex,) = struct.unpack_from('<i', b, 184)
    (numskinref, numskinfamilies, skinindex) = struct.unpack_from('<3i', b, 192)
    (numbodyparts,) = struct.unpack_from('<i', b, 204)
    (numattachments,) = struct.unpack_from('<i', b, 212)
    # textures
    texts = []
    for i in range(numtextures):
        o = textureindex + i * 64
        tn = b[o:o + 64].split(b'\x00')[0].decode('ascii', 'replace')
        (tf,) = struct.unpack_from('<i', b, o + 64)
        texts.append((tn, tf))
    return dict(name=name, id=hex(mid), ver=ver, length=length, eyepos=eyepos,
                min=mn, max=mx, bbmin=bbmin, bbmax=bbmax, numbones=numbones,
                numhitboxes=numhitboxes, numseq=numseq, numtextures=numtextures,
                texts=texts, numskinref=numskinref, numskinfamilies=numskinfamilies,
                numbodyparts=numbodyparts, numattachments=numattachments, size=len(b))


def main():
    targets = sys.argv[1:] or ['vm_ak47', 'vm_knife', 'vm_glock18', 'player_T']
    for t in targets:
        p = os.path.join(MD, t + '.cs16anim')
        if not os.path.exists(p):
            print('!! missing', p); continue
        d = read_c16anim(p)
        allmax = [-1e9] * 3; allmin = [1e9] * 3
        tvc = 0; ttc = 0; uv_ok = True
        for (tex, flags, vc, tc, off) in d['subs']:
            lo, hi = bbox_of_verts(d['buf'], off, vc)
            for k in range(3):
                allmin[k] = min(allmin[k], lo[k]); allmax[k] = max(allmax[k], hi[k])
            tvc += vc; ttc += tc
        print('=' * 70)
        print(f"[{t}] key={d['key']} scale={d['scale']} offset={d['off']}  fileSize={os.path.getsize(p)}")
        print(f"  bones={len(d['bones'])}  caps={len(d['caps'])}  subs={len(d['subs'])}  clips={len(d['clips'])}")
        print(f"  totalVerts={tvc}  totalTris={ttc}  (tris 已按 cs16_build 解出的三角数，未×2)")
        print(f"  vert bbox(min)={[round(v,4) for v in allmin]}")
        print(f"  vert bbox(max)={[round(v,4) for v in allmax]}")
        print(f"  vert size     ={[round(allmax[k]-allmin[k],4) for k in range(3)]}")
        print(f"  subs detail : " + ", ".join(f"{tex}(v{vc},t{tc},fl{flags:02x})" for tex, flags, vc, tc, _ in d['subs'][:8])
              + (' ...' if len(d['subs']) > 8 else ''))
        print(f"  first bone : {d['bones'][0][0]} parent={d['bones'][0][1]} pos={[round(v,4) for v in d['bones'][0][2:5]]}")
        print(f"  caps       : " + ", ".join(f"{z}@b{b} cy={cy:.3f} h={ch:.3f} r={cr:.3f}" for z, b, cy, ch, cr in d['caps']))

    print()
    print('#' * 70)
    print('# 原版 mdl')
    SRC = os.path.join(ROOT, '原版资源', 'cs16src', 'cs16game', 'app', 'cstrike', 'models')
    for rel in [('v_ak47.mdl',), ('v_knife.mdl',), ('v_glock18.mdl',), ('player/terror/terror.mdl',)]:
        p = os.path.join(SRC, *rel)
        if not os.path.exists(p):
            print('!! missing', p); continue
        m = read_mdl(p)
        print(f"[{rel[-1]}] id={m['id']} ver={m['ver']} size={m['size']} numbones={m['numbones']} numseq={m['numseq']} "
              f"numtextures={m['numtextures']} numhitboxes={m['numhitboxes']} skinref={m['numskinref']} skinfam={m['numskinfamilies']} bodyparts={m['numbodyparts']}")
        print(f"    min={[round(v,3) for v in m['min']]} max={[round(v,3) for v in m['max']]}")
        print(f"    size(units)={[round(m['max'][k]-m['min'][k],3) for k in range(3)]}  "
              f"= {[round((m['max'][k]-m['min'][k])*0.0254,4) for k in range(3)]} m")
        print(f"    bbmin={[round(v,3) for v in m['bbmin']]} bbmax={[round(v,3) for v in m['bbmax']]}")

    print()
    # .cs16mdl (C16M) 对照
    for t in ['vm_ak47', 'player_T']:
        p = os.path.join(MD, t + '.cs16mdl')
        if not os.path.exists(p):
            continue
        b = open(p, 'rb').read()
        o = 4
        (ver,) = struct.unpack_from('<I', b, o); o += 4
        key, o = rd_str(b, o)
        (pc,) = struct.unpack_from('<I', b, o); o += 4
        nh, sc = struct.unpack_from('<2f', b, o); o += 8
        bmin = struct.unpack_from('<3f', b, o); o += 12
        bmax = struct.unpack_from('<3f', b, o); o += 12
        print(f"[{t}.cs16mdl] ver={ver} key={key} parts={pc} nativeHeight={nh} scale={sc} bounds={[round(v,4) for v in bmin]}..{[round(v,4) for v in bmax]} size={len(b)}")


if __name__ == '__main__':
    main()
