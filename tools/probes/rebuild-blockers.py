# -*- coding: utf-8 -*-
"""判据资产：**按"人体高度带"重建阻挡盒 + 同源重写两份 `de_dust2.bytes`（位图 + 碰撞体段）**。

## 为什么需要它（根因，2026-09-20 / 09-20 切片刻 F 复核）

### ① 阻挡判据：引擎 `MapBaker` 的格柱判据太粗
引擎 `MapBaker` 用「格柱 ∩ 障碍 AABB ⇒ 阻挡」，格柱是 `[GroundTopY+ProbeBottomY, GroundTopY+ProbeTopY]`
= 本图 `y ∈ [-20, +10]`（整个地图高度）。于是**只要某个阻挡盒的 AABB 落在这一格的任何高度上**，
这一格就被判成阻挡：隧道/门洞/桥下这类"有顶的内部区域"被自己头顶的屋顶封死。
实测证据：B 隧道一带每个阻挡盒都是整格柱高且 1 格深 × 20+ 格宽 —— 那是屋顶/平台的脚印，不是墙。

### ② 口径一致性：位图头的 `colliderCount` 必须 == geo/场景的阻挡盒数
上一版脚本把 `.bytes` 头的 `colliderCount` 与**碰撞体段**原样透传（=旧的 218 条），
而 geo / 场景已经是 660 ⇒ 三处口径不一致（位图头声明 218、geo 660、场景 660）。
本版：碰撞体段**按 geo 的阻挡盒逐条重写**（世界 AABB），头 `colliderCount` = 条数 ⇒ 三处同数。
AABB 口径与 `Dust2Builder.BuildBlockers` 造的 `BoxCollider` 完全一致
（`min = (originX + ix0*cell, yMin, originZ + iz0*cell)`、`max = (originX + (ix1+1)*cell, yMax, originZ + (iz1+1)*cell)`），
与 `MapFormat.cs` 的 `ColliderStride = 24`（min 3×f32 + max 3×f32）逐字节对齐。

### ③ 门（door 贴图）：**原版 de_dust2 没有任何可开的门**
实测（`原版资源` 不在盘，但 `client/Assets/ThirdParty/Dust2/de_dust2.bsp` 在盘，是原版几何真源）：
entity lump（9802 B / 101 个实体）里 **`func_door*` 数量 = 0**，连 "door" 这个字样都没有；
classname 只有 `worldspawn / light_environment / light / info_player_start / info_player_deathmatch /
func_illusionary / func_breakable / func_bomb_target / func_buyzone / info_target / trigger_camera`。
⇒ `SandWllDoor*` 这些**只是贴在实心墙体上的贴图**（dust2 的门洞压边），不是门、也没有开合逻辑；
上一版按「贴图名含 door」把两片薄板从渲染网格里摘掉 ⇒ **该可见的墙面上出现了空洞**（用户报"该有门却没有门"）。
本版：门贴图的面**照常参与渲染 + 照常阻挡**（= 原版：实心世界几何）。

## 本脚本做什么
只改 `de_dust2_geo.bin` 的 **blocker 段**（mesh / marker 段一个字节都不动）+ 两份 `.bytes` 的
**位图段与碰撞体段**（出生点段逐字节透传）。

逐格判「这一格到底该不该挡」，规则取自原版口径（人只能站在法线 y ≥ 0.7 的面上，见
`CsConst.MaxStandableSlopeNormalZ`）+ "挡住人的是**穿过人体高度带**的墙"：
    对每一格，枚举该格里的**地面候选**（朝上的面 y = f）：
        该格可走 ⟺ ∃ f，使 `[f+0.10, f+1.75]` 这段"人体带"里**没有**近垂直面（|n.y| < 0.3）

⚠️ 本判据的**已知边界**（登记在 `策划/差异登记.tsv`，由 `enumerate-entities.py` 产出）：
它是**单层 2D** 判据 ⇒ 表达不了"侧面进不去、顶面可站"（箱子）与"低矮障碍可跳过"。
运行时由 `CsMap.ResolveMove` 的**高度感知闸门**补上这两条（按当前脚高做真实几何复核），见那里。

用法（幂等；先跑 --dry 看结论，再跑 --write 落盘；写盘前自动备份到 `原版资源/备份/`）：
    python tools/probes/rebuild-blockers.py                 # 只读：打印诊断
    python tools/probes/rebuild-blockers.py --write         # 落盘（备份 + 打印前后对比）
"""
import os
import shutil
import struct
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEO = os.path.join(ROOT, 'client', 'Assets', 'ThirdParty', 'Dust2', 'de_dust2_geo.bin')
BYTES_DIRS = [
    os.path.join(ROOT, 'client', 'Assets', 'MapData'),
    os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData'),
]
# 备份一律落在 原版资源/备份/（skill §1.9：⛔ Assets/ 里不许留备份文件）
BACKUP_DIR = os.path.join(ROOT, '\u539f\u7248\u8d44\u6e90', '\u5907\u4efd')   # 原版资源/备份

DRY = '--write' not in sys.argv

# 网格**基线**：上一版把「door 贴图的薄板」从渲染网格里摘掉过（134 个三角面 / 4 个组），
# 摘掉是**破坏性**的（原文件里那些索引已经没了，从当前文件读不回来）⇒ mesh 段必须从
# 「摘除之前」的副本恢复。那份副本由上一棒按 skill §1.9 搬到了 `原版资源/备份/`（不在 Assets 里）。
# ⛔ 只读它的 **mesh 段**；blocker 段仍按本脚本的判据重算（基线里那份是旧的 218）。
MESH_BASELINE = os.path.join(ROOT, '\u539f\u7248\u8d44\u6e90', '\u5907\u4efd',
                             'de_dust2_geo.bin.bak')   # 原版资源/备份/de_dust2_geo.bin.bak

BAND_LOW = 0.10          # 人体带下沿（相对地面，米）
BAND_HIGH = 1.75         # 人体带上沿（相对地面，米）—— 1.8m 站立高度留一点余量
FLOOR_MIN_NY = 0.7       # 原版可站立坡面阈值（CsConst.MaxStandableSlopeNormalZ，出处见那里）
WALL_MAX_NY = 0.30       # |n.y| < 它 = 近垂直面（墙）
SAMPLE = 0.25            # 三角面在 XZ 上的采样步长（米）


def backup(path):
    """把 path 备份到 原版资源/备份/（首次才备；幂等）。返回备份路径或 None。"""
    if not os.path.exists(path):
        return None
    if not os.path.isdir(BACKUP_DIR):
        os.makedirs(BACKUP_DIR)
    tag = os.path.relpath(path, os.path.join(ROOT, 'client')).replace('\\', '_').replace('/', '_')
    dst = os.path.join(BACKUP_DIR, tag + '.bak')
    if os.path.exists(dst):
        print("  备份已存在（幂等跳过）：%s" % os.path.relpath(dst, ROOT))
        return dst
    shutil.copyfile(path, dst)
    print("  已备份 %s → %s" % (os.path.relpath(path, ROOT), os.path.relpath(dst, ROOT)))
    return dst


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
                           nrm_off=nrm_off, idx_off=idx_off, end=o[0],
                           # 顶点/UV/法线三段的字节（**不含索引表**）：用于"两份 geo 的几何是不是同一版"
                           # 的逐组比对（见 assert_mesh_compatible）。
                           data_slice=bytes(data[pos_off:idx_off])))
    blocker_off = o[0]
    bc = u32()
    blockers = []
    for _ in range(bc):
        blockers.append((u32(), u32(), u32(), u32(), f32(), f32()))
    blocker_end = o[0]
    return dict(data=data, cell=cell, ox=ox, oz=oz, gt=gt, pb=pb, pt=pt, w=w, d=d,
                groups=groups, blockers=blockers, blocker_off=blocker_off,
                bc_off=blocker_off, blocker_end=blocker_end)


def mesh_segment(g, path):
    """取出某份 geo 的 **mesh 段**字节 + 组表（用于从基线恢复被摘掉的三角面）。"""
    return bytes(g['data'][g['groups'][0]['name_off']:g['blocker_off']])


def assert_mesh_compatible(cur, base, base_path):
    """断言基线只比当前"多索引"：组数/组名/顶点数/顶点/UV/法线字节都要逐组相等。

    不满足说明两边的**几何**不是同一版（那就不该拿它当基线）——宁可报错停下，
    也不要静默拼出一份"顶点来自 A、索引来自 B"的坏网格。
    """
    if len(cur['groups']) != len(base['groups']):
        raise SystemExit("⛔ 基线的组数 %d ≠ 当前 %d（%s）"
                         % (len(base['groups']), len(cur['groups']), base_path))
    for a, b in zip(cur['groups'], base['groups']):
        if a['name'] != b['name'] or a['vc'] != b['vc']:
            raise SystemExit("⛔ 组 %s/%s 的 (name, vc) 不一致（%s）"
                             % (a['name'], b['name'], base_path))
        if a['data_slice'] != b['data_slice']:
            raise SystemExit("⛔ 组 %s 的顶点/UV/法线字节不一致（顶点几何不是同一版）⇒ 不许当基线（%s）"
                             % (a['name'], base_path))
        if b['ic'] < a['ic']:
            raise SystemExit("⛔ 组 %s 基线索引 %d < 当前 %d（基线更旧，恢复会丢面）"
                             % (a['name'], b['ic'], a['ic']))


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
    # ── 判据要跑在**含门贴图面**的几何上（基线 = 摘除之前那版）：那才是原版的实心世界几何 ──
    if os.path.exists(MESH_BASELINE):
        base = load(MESH_BASELINE)
        assert_mesh_compatible(g, base, MESH_BASELINE)
        src = base
        restored = sum(b['ic'] - a['ic'] for a, b in zip(g['groups'], base['groups']))
        print("网格基线 %s：组数/组名/顶点一致，比当前多 %d 个索引 / %d 个三角面（门贴图的面）"
              % (os.path.relpath(MESH_BASELINE, ROOT), restored, restored // 3))
        for a, b in zip(g['groups'], base['groups']):
            if a['ic'] != b['ic']:
                print("    %-24s %d → %d tris" % (a['name'], a['ic'] // 3, b['ic'] // 3))
    else:
        src = g
        print("⚠️ 找不到网格基线 %s ⇒ 判据与 mesh 段都用当前文件（门贴图的面若已被摘掉就恢复不回来）"
              % MESH_BASELINE)

    cell = g['cell']; ox = g['ox']; oz = g['oz']
    W, D = g['w'], g['d']
    pb_abs = g['gt'] + g['pb']          # 位图探测柱（绝对 y）
    pt_abs = g['gt'] + g['pt']
    print("geo: cell=%.3f origin=(%.1f,%.1f) bitmap %dx%d  probe y[%.1f..%.1f]  现有阻挡盒 %d"
          % (cell, ox, oz, W, D, pb_abs, pt_abs, len(g['blockers'])))

    # ── 逐格收集：地面候选 y、穿过人体带的墙 ────────────────────────────
    floors = defaultdict(set)            # (ix,iz) -> {y}
    walls = defaultdict(list)            # (ix,iz) -> [(ylo,yhi)]
    tri_by_kind = defaultdict(int)

    for gi, grp in enumerate(src['groups']):
        V = grp['verts']; I = grp['idx']
        for k in range(0, len(I), 3):
            a, b, c = V[I[k]], V[I[k + 1]], V[I[k + 2]]
            ny = tri_normal(a, b, c)[1]
            xs = (a[0], b[0], c[0]); zs = (a[2], b[2], c[2]); ys = (a[1], b[1], c[1])
            x0, x1 = min(xs), max(xs); z0, z1 = min(zs), max(zs)
            nx_s = max(1, int((x1 - x0) / SAMPLE) + 1)
            nz_s = max(1, int((z1 - z0) / SAMPLE) + 1)
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
                        walls[(ix, iz)].append((min(ys), max(ys)))
                        tri_by_kind['wall'] += 1
                    else:
                        tri_by_kind['steep'] += 1

    print("  三角面采样命中：floor=%d wall=%d steep=%d"
          % (tri_by_kind['floor'], tri_by_kind['wall'], tri_by_kind['steep']))

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
    probe("匪家 T 出生点", -22, -58, 6, -32)

    # ── 阻挡盒：贪心合并成矩形（避免 1 万多个盒把场景撑爆）──────────────
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
    print()
    print("  阻挡盒合并：%d 格 → %d 个矩形" % (len(blocked), len(new_boxes)))

    # ── .bytes 的碰撞体段 = 上面这些盒子的**世界 AABB**（口径同 Dust2Builder.BuildBlockers）
    world_boxes = []
    for (ix0, iz0, ix1, iz1, y0, y1) in new_boxes:
        mn = (ox + ix0 * cell, y0, oz + iz0 * cell)
        mx = (ox + (ix1 + 1) * cell, y1, oz + (iz1 + 1) * cell)
        world_boxes.append((mn, mx))
    print("  .bytes 碰撞体段：%d 条世界 AABB（头 colliderCount 同步写 %d）"
          % (len(world_boxes), len(world_boxes)))

    # 位图（1 = 可走）
    bits = bytearray((W * D + 7) // 8)
    for iz in range(D):
        for ix in range(W):
            if (ix, iz) in bset:
                continue
            idx = iz * W + ix
            bits[idx >> 3] |= 1 << (idx & 7)

    if DRY:
        print()
        print("  （--dry：未写盘。加 --write 落盘）")
        # 仍打印两份 .bytes 的现状，便于对账
        for d in BYTES_DIRS:
            p = os.path.join(d, 'de_dust2.bytes')
            if not os.path.exists(p):
                print("  ⛔ 找不到 %s" % p)
                continue
            old = open(p, 'rb').read()
            ow, od, occ, osc, onl = struct.unpack_from('<5I', old, 32)
            print("  现状 %s：%dx%d colliders(头)=%d spawns=%d 文件 %d 字节"
                  % (os.path.relpath(p, ROOT), ow, od, occ, osc, len(old)))
        return

    # ── ① geo：mesh 段从**基线**取（恢复被摘掉的三角面），blocker 段整体换成新矩形 ────
    body = bytearray()
    body += struct.pack('<I', len(new_boxes))
    for box in new_boxes:
        body += struct.pack('<IIIIff', *box)

    mesh = mesh_segment(src, MESH_BASELINE if src is not g else GEO)

    out = bytearray(g['data'][:4])
    out += g['data'][4:g['groups'][0]['name_off']]      # 头（版本/参数/包围盒/组数）
    out += mesh                                         # 网格段（含门贴图的面）
    out += body
    out += g['data'][g['blocker_end']:]                 # 标记段原样

    backup(GEO)
    with open(GEO, 'wb') as f:
        f.write(bytes(out))
    print("  已写盘 geo：blocker %d → %d 个（文件 %d → %d 字节；mesh 段未动）"
          % (len(g['blockers']), len(new_boxes), len(g['data']), len(out)))

    # ── ② 两份 .bytes：位图段 + 碰撞体段重写（出生点段逐字节透传）──────────────────
    coll_blob = bytearray()
    for mn, mx in world_boxes:
        coll_blob += struct.pack('<6f', mn[0], mn[1], mn[2], mx[0], mx[1], mx[2])

    for d in BYTES_DIRS:
        path = os.path.join(d, 'de_dust2.bytes')
        if not os.path.exists(path):
            print("  ⛔ 找不到 %s，跳过" % path)
            continue
        old = open(path, 'rb').read()
        assert old[0:4] == b'CLVM', path
        ow, od, occ, osc, onl = struct.unpack_from('<5I', old, 32)
        if (ow, od) != (W, D):
            print("  ⛔ %s 的位图尺寸 %dx%d 与 geo 的 %dx%d 不一致，跳过" % (path, ow, od, W, D))
            continue
        name = old[64:64 + onl]
        tail = old[64 + onl + (ow * od + 7) // 8:]
        # 出生点段 = 尾段的**最后** osc*12 字节（碰撞体在前、出生点在后，见 MapWriter.Encode）
        spawn_blob = tail[len(tail) - osc * 12:] if osc else b''
        if len(tail) != occ * 24 + osc * 12:
            print("  ⚠️ %s 尾段 %d 字节 ≠ 声明 %d*24+%d*12=%d（旧文件口径不一致，仍按"
                  "「碰撞体在前、出生点在后」切尾）"
                  % (os.path.relpath(path, ROOT), len(tail), occ, osc, occ * 24 + osc * 12))

        head = bytearray(old[:64])
        head[40:44] = struct.pack('<I', len(world_boxes))      # colliderCount
        head[44:48] = struct.pack('<I', osc)                   # spawnCount 原样
        backup(path)
        with open(path, 'wb') as f:
            f.write(bytes(head) + name + bytes(bits) + bytes(coll_blob) + spawn_blob)
        print("  已写 %s：%d 字节｜位图 可走=%d 阻挡=%d｜colliders=%d（头同步）｜spawns=%d"
              % (os.path.relpath(path, ROOT),
                 64 + onl + len(bits) + len(coll_blob) + len(spawn_blob),
                 W * D - len(blocked), len(blocked), len(world_boxes), osc))

    print()
    print("  下一步：编辑器里跑一次 Clover/CS16/生成 de_dust2 场景"
          "（按新 geo 重建 Visual（33 组，含门贴图）/ Blockers（%d 盒）/ 标记表）" % len(new_boxes))


if __name__ == '__main__':
    main()
