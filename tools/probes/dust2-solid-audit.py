# -*- coding: utf-8 -*-
"""de_dust2 实体（solid）审计 —— #220 判定 A / B 的离线取证（只读；不启 Unity、不进 Play）。

入库件（原为本片探针；同族：g39-map-regression.py / dust2_breakables.py / bsp-unity-crosscheck.py）。

回答两件事：
  A. 该在哪一层补 —— 先量出"位图 / 高度场 / 真几何 / 原版 leaf"四层各自能看到什么。
  B. 反据还在不在 —— 我们的 .bytes / .bin 里有没有 leaf/solid 残留。

数据源（全部在盘、只读）：
  client/Assets/Resources/MapData/de_dust2.bytes   （引擎 CloverMap：位图 + 高度场 + 标记）
  client/Assets/ThirdParty/Dust2/de_dust2_geo.bin  （CD2G：渲染网格 + 阻挡盒 + 标记）
  client/Assets/ThirdParty/Dust2/de_dust2.bsp      （原版 BSP v30：渲染节点树 -> leaf，leaf 0 = solid）

落 leaf 实现逐字来自 _cs16-evidence/stuckfix3-2026-10-13/stuckfix3-bsp-hull.py
（变换 Unity.x = U*HL.x + 9.7536 ; Unity.z = +U*HL.y - 28.4480 ; Unity.y = U*HL.z，U = 0.0254）。
"""
import os, struct, sys, math

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# 自算根自检：`tools/probes/` 与本片原址 `.ai-tmp/test/` 都是"项目根下两级" ⇒ dirname^3 均为项目根。
# 路径口径错时**立刻退出**，别让它静默读错文件把结论做歪。
if not (os.path.isdir(os.path.join(ROOT, ".ai-tmp")) and os.path.isdir(os.path.join(ROOT, "client"))):
    sys.stderr.write("ROOT 自检失败（不是项目根）：%s\n" % ROOT)
    sys.exit(9)
BYTES = os.path.join(ROOT, "client", "Assets", "Resources", "MapData", "de_dust2.bytes")
GEO = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2_geo.bin")
BSP = os.path.join(ROOT, "client", "Assets", "ThirdParty", "Dust2", "de_dust2.bsp")

U = 0.0254
TOL = 0.12  # GroundCheckDistance
STEP = 0.457

PTS = [("P1", -38.642, 33.335), ("L1", -39.960, 32.780), ("CT-spawn", 18.500, 34.500)]


# ---------------------------------------------------------------- .bytes
def read_bytes(path):
    b = open(path, "rb").read()
    assert b[:4] == b"CLVM", b[:4]
    ver, flags = struct.unpack_from("<HH", b, 4)
    cell = struct.unpack_from("<f", b, 16)[0]
    ox, oy, oz = struct.unpack_from("<fff", b, 20)
    W, D, ncol, nspawn, nlen = struct.unpack_from("<IIIII", b, 32)
    off = 64 + nlen
    bits_len = (W * D + 7) // 8
    bits = b[off:off + bits_len]
    off += bits_len
    off += ncol * 24 + nspawn * 12
    markers = []
    if flags & 4:
        n = struct.unpack_from("<I", b, off)[0]; off += 4
        for _ in range(n):
            ln = struct.unpack_from("<I", b, off)[0]; off += 4
            nm = b[off:off + ln].decode("utf-8"); off += ln
            x, y, z = struct.unpack_from("<fff", b, off); off += 12
            markers.append((nm, (x, y, z)))
    surf = {}
    if flags & 2:
        cnt = struct.unpack_from("<I", b, off)[0]; off += 4
        for _ in range(cnt):
            cx, cz, y = struct.unpack_from("<IIf", b, off); off += 12
            surf.setdefault((cx, cz), []).append(y)
    assert off == len(b), (off, len(b))
    return dict(ver=ver, flags=flags, cell=cell, origin=(ox, oy, oz), W=W, D=D,
                ncol=ncol, nspawn=nspawn, bits=bits, markers=markers, surf=surf)


def walkable(m, ix, iz):
    if ix < 0 or iz < 0 or ix >= m["W"] or iz >= m["D"]:
        return False
    idx = iz * m["W"] + ix
    return (m["bits"][idx >> 3] >> (idx & 7)) & 1 == 1


# ---------------------------------------------------------------- .bin (CD2G)
def read_geo(path):
    b = open(path, "rb").read()
    assert b[:4] == b"CD2G", b[:4]
    off = 0
    off += 4
    ver = struct.unpack_from("<I", b, off)[0]; off += 4
    cell, ox, oz, gtop, omin, pb, pt = struct.unpack_from("<7f", b, off); off += 28
    W, D = struct.unpack_from("<II", b, off); off += 8
    wmin = struct.unpack_from("<3f", b, off); off += 12
    wmax = struct.unpack_from("<3f", b, off); off += 12
    gc = struct.unpack_from("<I", b, off)[0]; off += 4
    groups = []
    for _ in range(gc):
        png = b[off:off + 48].split(b"\0")[0].decode("utf-8"); off += 48
        vc, ic = struct.unpack_from("<II", b, off); off += 8
        verts = struct.unpack_from("<%df" % (vc * 3), b, off); off += vc * 12
        off += vc * 8      # uv
        off += vc * 12     # nrm
        idx = struct.unpack_from("<%dI" % ic, b, off); off += ic * 4
        groups.append((png, verts, idx))
    bc = struct.unpack_from("<I", b, off)[0]; off += 4
    blockers = []
    for _ in range(bc):
        ix0, iz0, ix1, iz1, ymin, ymax = struct.unpack_from("<IIIIff", b, off); off += 24
        blockers.append((ix0, iz0, ix1, iz1, ymin, ymax))
    mc = struct.unpack_from("<I", b, off)[0]; off += 4
    markers = {}
    for _ in range(mc):
        nm = b[off:off + 32].split(b"\0")[0].decode("utf-8"); off += 32
        pc = struct.unpack_from("<I", b, off)[0]; off += 4
        pts = [struct.unpack_from("<3f", b, off + 12 * k) for k in range(pc)]; off += pc * 12
        markers[nm] = pts
    assert off == len(b), (off, len(b))
    return dict(ver=ver, cell=cell, ox=ox, oz=oz, W=W, D=D, groups=groups,
                blockers=blockers, markers=markers)


def geo_top_faces(g, wx, wz, rad=0.5):
    """某一格 1 m^2 内、朝上（ny>0）三角面的世界 y（去重 3 位）。"""
    ys = set()
    for png, verts, idx in g["groups"]:
        for t in range(0, len(idx), 3):
            a = idx[t] * 3
            b1 = idx[t + 1] * 3
            c = idx[t + 2] * 3
            ax, ay, az = verts[a:a + 3]
            bx, by, bz = verts[b1:b1 + 3]
            cx, cy, cz = verts[c:c + 3]
            ux, uy, uz = bx - ax, by - ay, bz - az
            vx, vy, vz = cx - ax, cy - ay, cz - az
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            ln = math.sqrt(nx * nx + ny * ny + nz * nz)
            if ln < 1e-9:
                continue
            ny /= ln
            if ny <= 0.1:
                continue
            if not (min(ax, bx, cx) <= wx + rad and max(ax, bx, cx) >= wx - rad
                    and min(az, bz, cz) <= wz + rad and max(az, bz, cz) >= wz - rad):
                continue
            ys.add(round((ay + by + cy) / 3.0, 3))
    return sorted(ys)


# ---------------------------------------------------------------- BSP leaf
def read_bsp(path):
    data = open(path, "rb").read()
    ver = struct.unpack_from("<i", data, 0)[0]
    lumps = [struct.unpack_from("<ii", data, 4 + 8 * i) for i in range(15)]
    pl = lumps[1]
    planes = [struct.unpack_from("<4fi", data, pl[0] + 20 * i) for i in range(pl[1] // 20)]
    nd = lumps[5]
    nodes = []
    for i in range(nd[1] // 24):
        pn, c0, c1 = struct.unpack_from("<ihh", data, nd[0] + 24 * i)
        nodes.append((pn, c0, c1))
    return data, ver, planes, nodes


def leaf_at(planes, nodes, hx, hy, hz):
    node = 0
    for _ in range(64):
        pn, c0, c1 = nodes[node]
        p = planes[pn]
        d = p[0] * hx + p[1] * hy + p[2] * hz - p[3]
        child = c0 if d >= 0 else c1
        if child < 0:
            return -(child + 1)
        node = child
    raise RuntimeError("too deep")


def to_hl(ux, uz):
    return ((ux - 9.7536) / U, (uz + 28.4480) / U)


def main():
    m = read_bytes(BYTES)
    g = read_geo(GEO)
    data, bver, planes, nodes = read_bsp(BSP)

    print("== .bytes ==")
    print("  magic=CLVM ver=%d flags=0x%04X (Walkable=%d HeightField=%d Markers=%d)"
          % (m["ver"], m["flags"], m["flags"] & 1, (m["flags"] >> 1) & 1, (m["flags"] >> 2) & 1))
    print("  bitmap %dx%d cell=%.4f origin=(%.3f,%.3f,%.3f) colliders=%d spawns=%d markers=%d"
          % (m["W"], m["D"], m["cell"], *m["origin"], m["ncol"], m["nspawn"], len(m["markers"])))
    print("  heightfield cells=%d surfaces=%d" % (len(m["surf"]), sum(len(v) for v in m["surf"].values())))

    print("\n== .bin (CD2G) ==")
    print("  ver=%d grid %dx%d cell=%.3f origin=(%.3f,%.3f)"
          % (g["ver"], g["W"], g["D"], g["cell"], g["ox"], g["oz"]))
    print("  groups=%d tris=%d blockers=%d markers=%d"
          % (len(g["groups"]), sum(len(x[2]) // 3 for x in g["groups"]),
             len(g["blockers"]), len(g["markers"])))
    print("  sections present: header / mesh-groups / blockers / markers  (NO leaf, NO solid, NO face-index)")
    print("\n== .bsp ==")
    print("  ver=%d planes=%d nodes=%d" % (bver, len(planes), len(nodes)))

    ox, oz, cell = m["origin"][0], m["origin"][2], m["cell"]
    wcount = 0
    nosurf = 0
    both = []
    for iz in range(m["D"]):
        for ix in range(m["W"]):
            if not walkable(m, ix, iz):
                continue
            wcount += 1
            if (ix, iz) not in m["surf"]:
                nosurf += 1
                both.append((ix, iz))
    print("\n== 位图/高度场 全局 ==")
    print("  walkable=%d / %d" % (wcount, m["W"] * m["D"]))
    print("  walkable AND no height-field surface = %d  %s" % (nosurf, both[:20]))

    print("\n== 三个点名坐标（点层面）==")
    hdr = "  %-9s %-10s %-8s %-22s %-26s %-22s"
    print(hdr % ("点", "格", "位图", "高度场(可站面)", "geo.bin 朝上面(1m^2)", "BSP leaf 翻转点(m)"))
    for name, ux, uz in PTS:
        ix = math.floor((ux - ox) / cell)
        iz = math.floor((uz - oz) / cell)
        w = walkable(m, ix, iz)
        hs = m["surf"].get((ix, iz))
        hss = ",".join("%.3f" % v for v in hs) if hs else "-"
        gys = geo_top_faces(g, ox + (ix + 0.5) * cell, oz + (iz + 0.5) * cell)
        gy = ",".join("%.3f" % v for v in gys[-3:]) if gys else "-"
        hx, hy = to_hl(ux, uz)
        prev = None
        trans = []
        for uy in [3.0, 2.0, 1.5, 1.0, 0.9, 0.813, 0.8, 0.5, 0.406, 0.4, 0.0, -0.5,
                   -1.04, -1.5, -2.0, -3.25, -4.0, -6.0, -12.0]:
            solid = leaf_at(planes, nodes, hx, hy, uy / U) == 0
            if prev is not None and solid != prev:
                trans.append(round(uy, 3))
            prev = solid
        print(hdr % (name, "(%d,%d)" % (ix, iz), str(w), hss or "-", gy, trans))

    print("\n== 判定 B：反据残留 ==")
    print("  .bytes flags 只有 Walkable/HeightField/Markers 三位 -> 无 leaf/solid 段")
    print("  .bin 只有 4 段（header/网格/阻挡盒/标记）-> 无 leaf/solid 段；阻挡盒 %d 条"
          % len(g["blockers"]))
    print("  .bsp 在盘（%d B）-> leaf/solid 可离线重建（本脚本已在用）" % os.path.getsize(BSP))

    # ---- 用 geo.bin 的朝上面光栅化出"每格顶面 y"（与位图同格坐标口径）----
    top = {}
    r25 = cell * 0.5
    for png, verts, idx in g["groups"]:
        for t in range(0, len(idx), 3):
            a, b1, c = idx[t] * 3, idx[t + 1] * 3, idx[t + 2] * 3
            ax, ay, az = verts[a:a + 3]
            bx, by, bz = verts[b1:b1 + 3]
            cx, cy, cz = verts[c:c + 3]
            ux, uy, uz = bx - ax, by - ay, bz - az
            vx, vy, vz = cx - ax, cy - ay, cz - az
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            ln = math.sqrt(nx * nx + ny * ny + nz * nz)
            if ln < 1e-9 or ny / ln <= 0.1:
                continue
            minx, maxx = min(ax, bx, cx), max(ax, bx, cx)
            minz, maxz = min(az, bz, cz), max(az, bz, cz)
            for iz in range(int(math.floor((minz - oz) / cell)), int(math.floor((maxz - oz) / cell)) + 1):
                for ix in range(int(math.floor((minx - ox) / cell)), int(math.floor((maxx - ox) / cell)) + 1):
                    px, pz = ox + (ix + 0.5) * cell, oz + (iz + 0.5) * cell
                    d0 = (bx - ax) * (pz - az) - (bz - az) * (px - ax)
                    d1 = (cx - bx) * (pz - bz) - (cz - bz) * (px - bx)
                    d2 = (ax - cx) * (pz - cz) - (az - cz) * (px - cx)
                    neg = d0 < 0 or d1 < 0 or d2 < 0
                    pos = d0 > 0 or d1 > 0 or d2 > 0
                    if neg and pos:
                        continue
                    den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
                    if abs(den) < 1e-12:
                        continue
                    w0 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / den
                    w1 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / den
                    y = w0 * ay + w1 * by + (1.0 - w0 - w1) * cy
                    k = (ix, iz)
                    if k not in top or y > top[k]:
                        top[k] = y
    for k in list(top):
        top[k] = round(top[k], 3)

    voids = []
    atop_solid = []
    floor_in_solid = []
    for iz in range(m["D"]):
        for ix in range(m["W"]):
            if not walkable(m, ix, iz):
                continue
            wx, wz = ox + (ix + 0.5) * cell, oz + (iz + 0.5) * cell
            hx, hy = to_hl(wx, wz)
            if (ix, iz) not in top:
                voids.append((ix, iz))
                continue
            ty = top[(ix, iz)]
            if leaf_at(planes, nodes, hx, hy, (ty + 0.3) / U) == 0:
                floor_in_solid.append((ix, iz, ty))
            if leaf_at(planes, nodes, hx, hy, (ty - 0.3) / U) == 0:
                atop_solid.append((ix, iz, ty))

    print("\n== 交叉（geo.bin 顶面光栅化 × 原版 leaf）==")
    print("  可走格 %d：有地板 %d / 无地板(格心无朝上面) %d"
          % (wcount, wcount - len(voids), len(voids)))
    print("  无地板格（= 真虚空）: %s" % voids[:30])
    print("  地板之上是 solid（不该可走，应=0）: %d  %s" % (len(floor_in_solid), floor_in_solid[:10]))
    print("  地板之下是 solid（P1 类「地板即实心顶面」）: %d" % len(atop_solid))


main()
