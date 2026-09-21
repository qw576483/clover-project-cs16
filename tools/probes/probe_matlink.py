#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""片T 探针3：逐模型核对 贴图名→Tex/*.png→Mat/*.mat 引用链（guid 一致性）+ 贴图平均色。"""
import struct, os, re, glob
from PIL import Image

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
MD = os.path.join(ROOT, 'client', 'Assets', 'Editor', 'Views', 'ModelData')
TEX = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'Art', 'Tex')
MAT = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'Art', 'Mat')


def rd_str(b, o):
    (n,) = struct.unpack_from('<I', b, o); o += 4
    return b[o:o + n].decode('utf-8', 'replace'), o + n


def subs_of(path):
    b = open(path, 'rb').read(); o = 4
    o += 4
    _, o = rd_str(b, o)
    o += 16
    (nb,) = struct.unpack_from('<I', b, o); o += 4
    for _ in range(nb):
        _, o = rd_str(b, o); o += 4 + 28
    (nc,) = struct.unpack_from('<I', b, o); o += 4
    for _ in range(nc):
        _, o = rd_str(b, o); o += 16
    (ns,) = struct.unpack_from('<I', b, o); o += 4
    out = []
    for _ in range(ns):
        tex, o = rd_str(b, o)
        (flags,) = struct.unpack_from('<I', b, o); o += 4
        vc, tc = struct.unpack_from('<2I', b, o); o += 8
        out.append((tex, flags, vc, tc))
        o += vc*12 + vc*8 + vc*12 + vc + tc*3*4
    return out


def tex_guid(name):
    m = os.path.join(TEX, name + '.png.meta')
    if not os.path.exists(m): return None
    for line in open(m, encoding='utf-8', errors='replace'):
        mt = re.match(r'^guid:\s*([0-9a-f]{32})', line)
        if mt: return mt.group(1)
    return None


def mat_maintex(name):
    p = os.path.join(MAT, name + '.mat')
    if not os.path.exists(p): return None, None
    shader = None; guid = None
    cur = None
    for line in open(p, encoding='utf-8', errors='replace'):
        if 'm_Shader:' in line:
            mt = re.search(r'guid:\s*([0-9a-f]{32})', line)
            if mt: shader = mt.group(1)
        if '- _MainTex:' in line: cur = '_MainTex'
        elif re.match(r'\s*- _\w+:', line): cur = None
        elif cur == '_MainTex':
            mt = re.search(r'm_Texture:\s*\{fileID:\s*\d+,\s*guid:\s*([0-9a-f]{32})', line)
            if mt: guid = mt.group(1)
    return shader, guid


SHADERS = {'0000000000000000f000000000000000': 'Standard'}

print(f"{'model':22s} {'submesh tex':30s} {'png':4s} {'mat':4s} {'guid?':5s} {'tex_avg':16s} {'size':11s} {'flags':5s}")
print('-' * 110)
models = sorted(glob.glob(os.path.join(MD, '*.cs16anim')))
bad = []
for mp in models:
    key = os.path.basename(mp)[:-9]
    for (tex, flags, vc, tc) in subs_of(mp):
        png = os.path.exists(os.path.join(TEX, tex + '.png'))
        mat = os.path.exists(os.path.join(MAT, tex + '.mat'))
        tg = tex_guid(tex)
        sh, mg = mat_maintex(tex)
        ok = (tg is not None and mg is not None and tg == mg)
        avg = ''; size = ''
        if png:
            try:
                im = Image.open(os.path.join(TEX, tex + '.png')).convert('RGB')
                size = f"{im.size[0]}x{im.size[1]}"
                st = im.resize((16, 16))
                pxs = list(st.getdata())
                avg = "(%d,%d,%d)" % tuple(sum(c[i] for c in pxs) // len(pxs) for i in range(3))
            except Exception as e:
                avg = 'ERR'
        tag = '' if (png and mat and ok) else '  <== BAD'
        if tag: bad.append((key, tex, png, mat, ok))
        print(f"{key:22s} {tex:30s} {str(png):5s} {str(mat):4s} {str(ok):5s} {avg:16s} {size:11s} {flags:02x}{tag}")
print()
print('shader:', SHADERS)
print('BAD 行数 =', len(bad))
for b in bad: print('  BAD', b)
