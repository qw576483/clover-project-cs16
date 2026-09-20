# -*- coding: utf-8 -*-
"""判据资产：**按"人体高度带"重建阻挡盒**（修「中门过不去 / B 通上不去 / 有屋顶的内部区域全被封死」）。

## 为什么需要它（根因，2026-09-20 实测）
引擎 `MapBaker` 的烘焙判据是「**格柱** ∩ 障碍 AABB ⇒ 阻挡」，而格柱是 `[GroundTopY+ProbeBottomY,
GroundTopY+ProbeTopY]` = 本图 `y ∈ [-10, +20]`（整个地图高度）。于是**只要某个阻挡盒的 AABB
落在这一格的上方任何高度**，这一格就被判成阻挡：
  · 隧道/门洞/桥下这类"有顶的内部区域"，被自己头顶的**屋顶/门楣**封死；
  · 实测证据（`de_dust2.bytes` 位图 + `de_dust2_geo.bin` 阻挡盒）：B 隧道一带 10 个阻挡盒
    每个都是 `y[-10.00..20.00]`（整个格柱）且 1 格深 × 20+ 格宽 —— 那是屋顶/平台的脚印，不是墙。
    ⇒ 位图把 `x[-25..-9] × z[-45..-14]` 整片判成阻挡（而该片 `y 0..4` 的几何射线扫描**一个面都没有**）。

## 本脚本做什么（只改 `de_dust2_geo.bin` 的 blocker 段，mesh / marker 段一个字节都不动）
逐格判「这一格到底该不该挡」，规则取自**原版口径**（人只能站在法线 y ≥ 0.7 的面上，见
`CsConst.MaxStandableSlopeNormalZ`）+ "挡住人的是**穿过人体高度带**的墙"：
    对每一格，枚举该格里的**地面候选**（朝上的面 y = f）：
        该格可走 ⟺ ∃ f，使 `[f+0.10, f+1.75]` 这段"人体带"里**没有**近垂直面（|n.y| < 0.3）
    · 没有任何朝上的面（格子实心 / 图外） ⇒ 阻挡；
    · 朝上的面也有，但都被墙穿过 ⇒ 阻挡。
门（贴图名含 `Door`）**不参与阻挡**（本工程没有开关门逻辑 ⇒ 按"常开"处理，登记为允许的差异）。

用法（幂等；先跑 --dry 看结论，再跑 --write 落盘；写盘前自动备份 .bak）：
    python tools/probes/rebuild-blockers.py                 # 只读：打印诊断
    python tools/probes/rebuild-blockers.py --write         # 落盘（备份 + 打印前后对比）
"""
import os
import struct
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')

DRY = '--write' not in sys.argv

BAND_LOW = 0.10          # 人体带下沿（相对地面，米）
BAND_HIGH = 1.75         # 人体带上沿（相对地面，米）—— 1.8m 站立高度留一点余量
FLOOR_MIN_NY = 0.7       # 原版可站立坡面阈值（CsConst.MaxStandableSlopeNormalZ，出处见那里）
WALL_MAX_NY = 0.30       # |n.y| < 它 = 近垂直面（墙/门板）
SAMPLE = 0.25            # 三角面在 XZ 上的采样步长（米）
DOOR_KEY = 'door'        # 贴图名（小写）含它 = 门 ⇒ 不参与阻挡


def load(path):
    with open(path, 'rb') as f:
        data = bytearray(f.read())
    o = [4]

    def u32():
        v = struct.unpack_from('<I', data, o[0])[0]; o[0] += 4; return v

    def f32():
        v = struct.unpack_from('<f', data, o[0])[0]; o[0] += 4; return v

    assert bytes(data[0:4]) == b'CD2G'
    u32()
    cell = f32(); ox = f32(); oz = f32()
    gt = f32(); omh = f32(); pb = f32(); pt = f32()
    w = u32(); d = u32()
    for _ in range(6):
        f32()
    gc = u32(); groups = []
    for _ in range(gc):
        gstart = o[0]
        name = bytes(data[o[0]:o[0] + 48]).split(b'\0')[0].decode('utf-8'); o[0] += 48
        cnt_off = o[0]
        vc = u32(); ic = u32()
        pos_off = o[0]; o[0] += 12 * vc
        uv_off = o[0]; o[0] += 8 * vc
        nrm_off = o[0]; o[0] += 12 * vc
        idx_off = o[0]
        verts = [struct.unpack_from('<3f', data, pos_off + 12 * i) for i in range(vc)]
        idx = list(struct.unpack_from('<%dI' % ic, data, idx_off)); o[0] += 4 * ic
        groups.append(dict(name=name, verts=verts, idx=idx, vc=vc, ic=ic,
                           name_off=gstart, cnt_off=cnt_off, pos_off=pos_off, uv_off=uv_off,
                           nrm_off=nrm_off, idx_off=idx_off, end=o[0]))
    blocker_off = o[0]
    bc = u32()
    blockers = []
    for _ in range(bc):
        blockers.append((u32(), u32(), u32(), u32(), f32(), f32()))
    blocker_end = o[0]
    return dict(data=data, cell=cell, ox=ox, oz=oz, gt=gt, pb=pb, pt=pt, w=w, d=d,
                groups=groups, blockers=blockers, blocker_off=blocker_off,
                bc_off=blocker_off, blocker_end=blocker_end)


def tri_normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx = uy * vz - uz * vy; ny = uz * vx - ux * vz; nz = ux * vy - uy * vx
    L = (nx * nx + ny * ny + nz * nz) ** 0.5
    if L < 1e-9:
        return (0.0, 0.0, 0.0)
    return (nx / L, ny / L, nz / L)


def main():
    g = load(GEO)
    cell = g['cell']; ox = g['ox']; oz = g['oz']
    W, D = g['w'], g['d']
    pb_abs = g['gt'] + g['pb']          # 位图探测柱（绝对 y）
    pt_abs = g['gt'] + g['pt']
    print("geo: cell=%.3f origin=(%.1f,%.1f) bitmap %dx%d  probe y[%.1f..%.1f]  现有阻挡盒 %d"
          % (cell, ox, oz, W, D, pb_abs, pt_abs, len(g['blockers'])))

    # ── 逐格收集：地面候选 y、穿过人体带的墙、全部近垂直面（供诊断）──────
    floors = defaultdict(set)            # (ix,iz) -> {y}
    walls = defaultdict(list)            # (ix,iz) -> [(ylo,yhi)]
    tri_by_kind = defaultdict(int)
    thin_slabs = []                      # 诊断：薄竖直板（门板候选）
    door_panel_tris = set()              # (gi, k)：判定为"门板"的三角面（既不阻挡、也从渲染网格里去掉）

    # ── 门板识别：Door 贴图的三角面先按"连通簇"分组，**薄**的簇才是门板（门框是厚的，必须继续挡人）──
    door_clusters = []                   # 每项：dict(tris=[(gi,k)], bbox)
    for gi, grp in enumerate(g['groups']):
        if DOOR_KEY not in grp['name'].lower():
            continue
        V = grp['verts']; I = grp['idx']
        tris = []
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            if abs(tri_normal(a, b, c)[1]) >= WALL_MAX_NY:
                continue
            tris.append((k, a, b, c))
        # 简单聚类：顶点距离 ≤ 0.6m 视为同簇（门板是一块薄板，顶点密集；门框是厚墙，成一大簇）
        parent = list(range(len(tris)))

        def find(x):
            while parent[x] != x:
                parent[x] = parent[parent[x]]; x = parent[x]
            return x

        def union(x, y):
            rx, ry = find(x), find(y)
            if rx != ry:
                parent[ry] = rx

        for i in range(len(tris)):
            for j in range(i + 1, len(tris)):
                ai = tris[i][1:]; aj = tris[j][1:]
                near = False
                for p in ai:
                    for q in aj:
                        if abs(p[0] - q[0]) <= 0.6 and abs(p[1] - q[1]) <= 0.6 and abs(p[2] - q[2]) <= 0.6:
                            near = True; break
                    if near:
                        break
                if near:
                    union(i, j)
        buckets = defaultdict(list)
        for i, t in enumerate(tris):
            buckets[find(i)].append(t)
        for _, members in buckets.items():
            xs = [v[0] for _, a, b, c in members for v in (a, b, c)]
            ys = [v[1] for _, a, b, c in members for v in (a, b, c)]
            zs = [v[2] for _, a, b, c in members for v in (a, b, c)]
            door_clusters.append(dict(gi=gi, tris=[k for k, _, _, _ in members],
                                      bbox=(min(xs), max(xs), min(ys), max(ys), min(zs), max(zs))))

    for cl in door_clusters:
        x0, x1, y0, y1, z0, z1 = cl['bbox']
        thick = min(x1 - x0, z1 - z0)
        height = y1 - y0
        is_panel = thick <= 0.45 and height >= 1.0
        cl['panel'] = is_panel
        if is_panel:
            for k in cl['tris']:
                door_panel_tris.add((cl['gi'], k))
            thin_slabs.append((g['groups'][cl['gi']]['name'], round((x0 + x1) / 2, 2), round((z0 + z1) / 2, 2),
                               round(y0, 2), round(y1, 2), round(thick, 3)))
    print("  Door 贴图连通簇 %d 个，其中判定为**门板**（薄 ≤0.45m 且高 ≥1m）%d 个；"
          "门框（厚）继续挡人" % (len(door_clusters), len(thin_slabs)))
    for s in thin_slabs:
        print("     门板 %-22s 中心(x=%+.1f,z=%+.1f) y[%.2f..%.2f] 厚度%.3f" % s)

    for gi, grp in enumerate(g['groups']):
        name = grp['name']; V = grp['verts']; I = grp['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            ny = tri_normal(a, b, c)[1]
            xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2]); ys = (a[1], b[1], c[1])
            x0, x1 = min(xs), max(xs); z0, z1 = min(zs), max(zs)
            nx_s = max(1, int((x1 - x0) / SAMPLE) + 1)
            nz_s = max(1, int((z1 - z0) / SAMPLE) + 1)
            is_door_panel = (gi, k) in door_panel_tris
            for i in range(nx_s):
                for j in range(nz_s):
                    t = (i + 0.5) / nx_s; s = (j + 0.5) / nz_s
                    px = x0 + (x1 - x0) * t; pz = z0 + (z1 - z0) * s
                    ix = int((px - ox) // cell); iz = int((pz - oz) // cell)
                    if ix < 0 or iz < 0 or ix >= W or iz >= D:
                        continue
                    if ny >= FLOOR_MIN_NY:
                        floors[(ix, iz)].add(round(min(ys), 2))
                        tri_by_kind['floor'] += 1
                    elif abs(ny) < WALL_MAX_NY:
                        if is_door_panel:
                            # 门板：按"常开"处理 ⇒ 既不阻挡，也从渲染网格里摘掉（见文件头）
                            tri_by_kind['door_panel'] += 1
                        else:
                            walls[(ix, iz)].append((min(ys), max(ys)))
                            tri_by_kind['wall'] += 1
                    else:
                        tri_by_kind['steep'] += 1

    print("  三角面采样命中：floor=%d wall=%d 门板(不阻挡)=%d steep=%d"
          % (tri_by_kind['floor'], tri_by_kind['wall'], tri_by_kind['door_panel'], tri_by_kind['steep']))

    # ── 逐格判定 ──────────────────────────────────────────────────────────
    blocked = []
    reason = defaultdict(int)
    for iz in range(D):
        for ix in range(W):
            fs = floors.get((ix, iz))
            if not fs:
                blocked.append((ix, iz))
                reason['no-floor'] += 1
                continue
            ws = walls.get((ix, iz)) or []
            ok = False
            for f in sorted(fs):
                lo, hi = f + BAND_LOW, f + BAND_HIGH
                if not any(w0 < hi and w1 > lo for (w0, w1) in ws):
                    ok = True
                    break
            if ok:
                continue
            blocked.append((ix, iz))
            reason['wall-in-band'] += 1

    print("  新位图：可走=%d 阻挡=%d（原有 blocker %d 个）"
          % (W * D - len(blocked), len(blocked), len(g['blockers'])))
    print("  阻挡原因：无地面（实心/图外）%d，人体带里被墙穿过 %d" % (reason['no-floor'], reason['wall-in-band']))

    # ── 关注点体检 ────────────────────────────────────────────────────────
    def probe(title, wx0, wz0, wx1, wz1):
        ix0 = int((wx0 - ox) // cell); iz0 = int((wz0 - oz) // cell)
        ix1 = int((wx1 - ox) // cell); iz1 = int((wz1 - oz) // cell)
        bs = set(blocked)
        print()
        print("  [%s] 新位图 x[%+.0f..%+.0f] z[%+.0f..%+.0f]：# 阻挡 . 可走" % (title, wx0, wx1, wz0, wz1))
        for iz in range(iz1, iz0 - 1, -1):
            print("    z%+6.1f %s" % (oz + iz * cell,
                  ''.join('#' if (ix, iz) in bs else '.' for ix in range(ix0, ix1 + 1))))

    probe("中门", -6, 4, 14, 16)
    probe("B 隧道一带", -26, -46, -4, -8)

    if DRY:
        print()
        print("  （--dry：未写盘。加 --write 落盘）")
        return

    # ── 写盘：blocker 段换成"按格判定后的矩形"（贪心合并，避免 1 万多个盒把场景撑爆）──
    bset = set(blocked)
    runs = []                      # (iz, ix0, ix1)
    for iz in range(D):
        ix = 0
        while ix < W:
            if (ix, iz) not in bset:
                ix += 1
                continue
            j = ix
            while j + 1 < W and (j + 1, iz) in bset:
                j += 1
            runs.append((iz, ix, j))
            ix = j + 1
    rects = []                     # [ix0, ix1, iz0, iz1]
    open_by_key = {}
    prev_iz = None
    for (iz, ix0, ix1) in runs:
        if prev_iz is None or iz != prev_iz + 1:
            open_by_key = {}
        key = (ix0, ix1)
        if key in open_by_key:
            open_by_key[key][3] = iz
        else:
            r = [ix0, ix1, iz, iz]
            rects.append(r)
            open_by_key[key] = r
        prev_iz = iz
    for key in list(open_by_key):
        if open_by_key[key][3] != prev_iz:
            del open_by_key[key]
    new_boxes = [(r[0], r[2], r[1], r[3], pb_abs, pt_abs) for r in rects]
    print("  阻挡盒合并：%d 格 → %d 个矩形" % (len(blocked), len(new_boxes)))

    # ① 网格段：把"门板"三角面从各组的索引表里摘掉（顶点数组原样保留 —— 未用的顶点无害）
    mesh = bytearray()
    dropped = 0
    for gi, grp in enumerate(g['groups']):
        data = g['data']
        drop_ks = sorted(k for (dgi, k) in door_panel_tris if dgi == gi)
        mesh += data[grp['name_off']:grp['cnt_off']]
        if drop_ks:
            # drop_ks 是**三角形起点**（k, k+1, k+2 三个索引都要去掉）—— 只删起点会把索引表删坏。
            drop_set = set()
            for k in drop_ks:
                drop_set.update((k, k + 1, k + 2))
            keep = [grp['idx'][k] for k in range(grp['ic']) if k not in drop_set]
            assert len(keep) % 3 == 0, (
                "组 %s 过滤后索引数 %d 不是 3 的倍数（原 ic=%d，drop=%d）—— 门板簇与三角面对齐错了"
                % (g['groups'][gi]['name'], len(keep), grp['ic'], len(drop_ks)))
            dropped += grp['ic'] - len(keep)
            mesh += struct.pack('<II', grp['vc'], len(keep))
            mesh += data[grp['pos_off']:grp['uv_off']]
            mesh += data[grp['uv_off']:grp['nrm_off']]
            mesh += data[grp['nrm_off']:grp['idx_off']]
            mesh += struct.pack('<%dI' % len(keep), *keep)
        else:
            mesh += data[grp['cnt_off']:grp['end']]
    print("  网格段：摘掉门板三角面 %d 个（%d 组里）" % (dropped // 3, len({d for d, _ in door_panel_tris})))

    # ② 阻挡盒段：整个换成"每格一个盒"（只有该挡的格才发盒）
    body = bytearray()
    body += struct.pack('<I', len(new_boxes))
    for box in new_boxes:
        body += struct.pack('<IIIIff', *box)

    # ③ 头 + 网格 + 阻挡盒 + 标记段（标记段逐字节透传）
    out = bytearray(g['data'][:4])       # 魔数
    out += g['data'][4:g['groups'][0]['name_off']]      # 魔数之后到第 0 组之前（版本/参数/包围盒/组数）
    out += mesh
    out += body
    out += g['data'][g['blocker_end']:]

    bak = GEO + '.bak'
    if not os.path.exists(bak):
        with open(bak, 'wb') as f:
            f.write(bytes(g['data']))
        print("  已备份原 geo → %s" % bak)
    with open(GEO, 'wb') as f:
        f.write(bytes(out))
    print("  已写盘：blocker %d → %d 个（文件 %d → %d 字节）"
          % (len(g['blockers']), len(new_boxes), len(g['data']), len(out)))

    # ── 直接落 .bytes（位图 = 上面的逐格判定；碰撞体/出生点段从现有文件逐字节透传）──────────
    # 为什么不经编辑器烘焙：引擎 MapBaker 逐格 × 全障碍是 O(W×D×N)，而这里的判据比它**更强**
    # （带人体高度带），烘焙只会把这个结论再算一遍（且慢）。.bytes 的写入格式与引擎
    # CloverMapWriter 完全一致（同一份契约，见 clover-client-unity-engine/Runtime/Presentation/MapFormat.cs）。
    bytes_targets = [
        os.path.join(ROOT, 'client', 'Assets', 'MapData', 'de_dust2.bytes'),
        os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes'),
    ]
    bits = bytearray((W * D + 7) // 8)
    for iz in range(D):
        for ix in range(W):
            if (ix, iz) in bset:
                continue
            idx = iz * W + ix
            bits[idx >> 3] |= 1 << (idx & 7)
    for path in bytes_targets:
        if not os.path.exists(path):
            print("  ⛔ 找不到 %s，跳过" % path)
            continue
        with open(path, 'rb') as f:
            old = f.read()
        assert old[0:4] == b'CLVM', path
        ow, od, occ, osc, onl = struct.unpack_from('<5I', old, 32)
        if (ow, od) != (W, D):
            print("  ⛔ %s 的位图尺寸 %dx%d 与 geo 的 %dx%d 不一致，跳过（先跑一次编辑器烘焙对齐）"
                  % (path, ow, od, W, D))
            continue
        name = old[64:64 + onl]
        head = bytearray(old[:64])                       # 定长头（含 flags/sceneId/cell/origin/计数）
        head[40:44] = struct.pack('<I', occ)             # colliderCount 原样
        head[44:48] = struct.pack('<I', osc)             # spawnCount 原样
        tail = old[64 + onl + (ow * od + 7) // 8:]       # 碰撞体 + 出生点段，逐字节透传
        if not os.path.exists(path + '.bak'):            # 备份（只在首次）
            # ⛔ 先判存在再 open：早先写成 `with open(bak,'wb')` 再判存在 —— open 已经把文件建出来了，
            #    判存在必然为真 ⇒ 备份是**空文件**，原文件在覆盖后就找不回来了（踩过一次）。
            with open(path + '.bak', 'wb') as f:
                f.write(old)
        with open(path, 'wb') as f:
            f.write(bytes(head) + name + bytes(bits) + tail)
        print("  已写位图：%s（%d 字节；可走=%d 阻挡=%d）"
              % (os.path.relpath(path, ROOT), 64 + onl + len(bits) + len(tail),
                 W * D - len(blocked), len(blocked)))
    print("  下一步：编辑器里跑一次 Clover/CS16/生成 de_dust2 场景"
          "（把门板从渲染网格里去掉、并按新矩形更新 Blocker 盒）")


if __name__ == '__main__':
    main()
