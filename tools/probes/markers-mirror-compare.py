# -*- coding: utf-8 -*-
"""Compare the marker mirror table (text) with the FlagMarkers section inside de_dust2.bytes.

Both files are in client/Assets/Resources/MapData/.  The text table is
`# clover cs16 markers v1 (marker x y z)` followed by `<name> <x> <y> <z>` lines;
the binary .bytes carries the same rows in its CloverMap FlagMarkers section
(layout: u32 count, then per row u32 name_len + utf8 name + 3 f32).
Read-only: prints a per-name diff and a nonzero exit code when the two disagree.
"""
import io
import os
import struct
import sys

ROOT = r'C:\Work\Server\f-v2\clover-project-cs16'
MDIR = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData')
TXT = os.path.join(MDIR, 'de_dust2_markers.bytes')
BIN = os.path.join(MDIR, 'de_dust2.bytes')

HEADER_SIZE = 64
COLLIDER_STRIDE = 24
SPAWN_STRIDE = 12


def read_header(data):
    if data[0:4] != b'CLVM':
        raise SystemExit('magic mismatch in %s' % BIN)
    version, flags = struct.unpack_from('<HH', data, 4)
    cell = struct.unpack_from('<f', data, 16)[0]
    ox, oy, oz = struct.unpack_from('<fff', data, 20)
    width, depth, coll, spawn, name_len = struct.unpack_from('<IIIII', data, 32)
    return dict(version=version, flags=flags, cell=cell, origin=(ox, oy, oz),
                width=width, depth=depth, colliders=coll, spawns=spawn, name_len=name_len)


def decode_markers(data, h):
    bits_len = (h['width'] * h['depth'] + 7) // 8
    pos = HEADER_SIZE + h['name_len'] + bits_len \
        + h['colliders'] * COLLIDER_STRIDE + h['spawns'] * SPAWN_STRIDE
    if not (h['flags'] & (1 << 2)):
        raise SystemExit('FlagMarkers not set in %s' % BIN)
    count = struct.unpack_from('<I', data, pos)[0]
    pos += 4
    out = []
    for _ in range(count):
        nlen = struct.unpack_from('<I', data, pos)[0]
        pos += 4
        name = data[pos:pos + nlen].decode('utf-8')
        pos += nlen
        x, y, z = struct.unpack_from('<fff', data, pos)
        pos += 12
        out.append((name, x, y, z))
    tail = len(data) - pos
    return out, tail


def parse_text(path):
    rows = []
    for raw in io.open(path, encoding='utf-8-sig').read().splitlines():
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        parts = line.replace('\t', ' ').split()
        if len(parts) != 4:
            raise SystemExit('bad marker row: %r' % line)
        rows.append((parts[0], float(parts[1]), float(parts[2]), float(parts[3])))
    return rows


def main():
    bin_rows, tail = decode_markers(bytearray(io.open(BIN, 'rb').read()), read_header(bytearray(io.open(BIN, 'rb').read())))
    txt_rows = parse_text(TXT)
    print('binary FlagMarkers=%d tail=%d ; text table=%d' % (len(bin_rows), tail, len(txt_rows)))
    n = max(len(bin_rows), len(txt_rows))
    bad = 0
    max_delta = 0.0
    # 文本镜像表按 F3 打印 ⇒ 半 ULP = 5e-4。容差取 1e-3：判"是不是同一行"够用，
    # 又能抓住任何真实位移（实测旧的脱节行差 2~3 m）。
    tol = 1e-3
    for i in range(n):
        b = bin_rows[i] if i < len(bin_rows) else None
        t = txt_rows[i] if i < len(txt_rows) else None
        if b is not None and t is not None and b[0] == t[0]:
            for k in range(3):
                max_delta = max(max_delta, abs(b[k + 1] - t[k + 1]))
        same = b is not None and t is not None and b[0] == t[0] and \
            all(abs(b[k + 1] - t[k + 1]) <= tol for k in range(3))
        if not same:
            bad += 1
            print('DIFF #%d  bin=%s  txt=%s' % (i, b, t))
    counts_bin, counts_txt = {}, {}
    for r in bin_rows:
        counts_bin[r[0]] = counts_bin.get(r[0], 0) + 1
    for r in txt_rows:
        counts_txt[r[0]] = counts_txt.get(r[0], 0) + 1
    for name in sorted(set(counts_bin) | set(counts_txt)):
        if counts_bin.get(name, 0) != counts_txt.get(name, 0):
            print('COUNT DIFF %-14s bin=%d txt=%d' % (name, counts_bin.get(name, 0), counts_txt.get(name, 0)))
            bad += 1
    print('max|delta|=%.6f m (容差 %.0e)' % (max_delta, tol))
    print('MISMATCHES=%d' % bad)
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
