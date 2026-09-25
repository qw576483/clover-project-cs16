# -*- coding: utf-8 -*-
"""de_dust2 可走位图的离线回归量法（只读；不启 Unity、不进 Play）。

用途：比较两份 `de_dust2.bytes`（改前 / 改后），逐项打印同一口径下的数字。
数据来源：`client/Assets/Resources/MapData/de_dust2.bytes`（可走位图 + 标记点段）
          `client/Assets/ThirdParty/Dust2/de_dust2_geo.bin`（真几何：转换脚本从原版 BSP 导出）

口径（脚本自己的定义，写死在这里，前后两列必须用同一口径）：
  · 可走格 = 位图位为 1 的格（<bytes> 的 FlagWalkable 位图，1 = 可走）。
  · 格心地面 = 从该格中心 (x,z) 向下打一根射线，取**最高的朝上面**（三角面法线 y>0）的世界 y；
    打不到 ⇒ None（`CsMap.TrySampleGround` 在同一处也会探不到 ⇒ 走软地板分支）。
  · 连通分量：宽口径 = 4 邻；严格口径 = 8 邻但对角要求两侧正交格都可走（引擎 A* 的移动规则）。
  · 标记点可达 = 标记点段的每个点：落格在不在位图内 / 可走 / 是否落在主分量。
  · 低矮障碍候选 = 位图判**挡**、且存在一个 4 邻可走格 n，使
    `格心地面(c) - 格心地面(n) ∈ (0, JumpReach]`（JumpReach = 1.1445 m，跳跃峰值）；
    候选必须**地面挡**（定义已含）。`顶面可站` = 在 (c 格心, 格心地面(c)) 之上
    [ +0.12, +1.75 ] m 内没有任何三角面（≈ `CsMap.BodyHeightClearAt`）。

用法：
  python tools/probes/g39-map-regression.py --before <旧 .bytes> --after <新 .bytes> [--geo <geo.bin>]
  只给 --before：单列输出。
"""
import argparse, math, os, struct, sys
from collections import defaultdict, deque

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_MAP = os.path.join(ROOT, "client/Assets/Resources/MapData/de_dust2.bytes")
DEFAULT_GEO = os.path.join(ROOT, "client/Assets/ThirdParty/Dust2/de_dust2_geo.bin")

JUMP_REACH = 1.1445   # 跳跃峰值高度（m）
STAND_H = 1.75        # 身高带（m），与 CsConst.StandHeight 同口径
FOOT_TOL = 0.12       # GroundCheckDistance（m）


# ------------------------------------------------------------------ 读入
def read_map(path):
    b = open(path, "rb").read()
    assert b[:4] == b"CLVM", (path, b[:4])
    ver, flags = struct.unpack_from("<HH", b, 4)
    cell = struct.unpack_from("<f", b, 16)[0]
    ox, oy, oz = struct.unpack_from("<fff", b, 20)
    W, D, ncol, nspawn, nlen = struct.unpack_from("<IIIII", b, 32)
    off = 64 + nlen
    bits_len = (W * D + 7) // 8
    bits = b[off:off + bits_len]
    off += bits_len + ncol * 24 + nspawn * 12
    markers = []
    if flags & 4:
        n = struct.unpack_from("<I", b, off)[0]; off += 4
        for _ in range(n):
            ln = struct.unpack_from("<I", b, off)[0]; off += 4
            name = b[off:off + ln].decode("utf-8"); off += ln
            x, y, z = struct.unpack_from("<fff", b, off); off += 12
            markers.append((name, x, y, z))
    return dict(path=path, ver=ver, flags=flags, cell=cell, ox=ox, oy=oy, oz=oz,
                W=W, D=D, bits=bits, markers=markers, size=len(b))


def read_geo(path):
    b = open(path, "rb").read()
    assert b[:4] == b"CD2G", b[:4]
    off = 8
    cell, ox, oz, gty, omh, pby, pty = struct.unpack_from("<7f", b, off); off += 28
    W, D = struct.unpack_from("<II", b, off); off += 8
    off += 24
    ng = struct.unpack_from("<I", b, off)[0]; off += 4
    groups = []
    for _ in range(ng):
        png = b[off:off + 48].split(b"\0")[0].decode("utf-8", "replace"); off += 48
        vc, ic = struct.unpack_from("<II", b, off); off += 8
        pos = struct.unpack_from("<%df" % (vc * 3), b, off); off += vc * 12
        off += vc * 8
        off += vc * 12
        idx = struct.unpack_from("<%dI" % ic, b, off); off += ic * 4
        groups.append((png, pos, idx))
    return dict(cell=cell, ox=ox, oz=oz, W=W, D=D, pby=pby, pty=pty, groups=groups)


def triangles(geo):
    """产出 (A,B,C) 三元组（世界米）。"""
    out = []
    for png, pos, idx in geo["groups"]:
        for t in range(0, len(idx), 3):
            a, b, c = idx[t], idx[t + 1], idx[t + 2]
            out.append(((pos[a * 3], pos[a * 3 + 1], pos[a * 3 + 2]),
                        (pos[b * 3], pos[b * 3 + 1], pos[b * 3 + 2]),
                        (pos[c * 3], pos[c * 3 + 1], pos[c * 3 + 2])))
    return out


def bucket(tris, geo):
    cell = geo["cell"]
    bk = defaultdict(list)
    for (A, B, C) in tris:
        minx = min(A[0], B[0], C[0]); maxx = max(A[0], B[0], C[0])
        minz = min(A[2], B[2], C[2]); maxz = max(A[2], B[2], C[2])
        ix0 = int(math.floor((minx - geo["ox"]) / cell)); ix1 = int(math.floor((maxx - geo["ox"]) / cell))
        iz0 = int(math.floor((minz - geo["oz"]) / cell)); iz1 = int(math.floor((maxz - geo["oz"]) / cell))
        for iz in range(max(0, iz0), min(geo["D"] - 1, iz1) + 1):
            for ix in range(max(0, ix0), min(geo["W"] - 1, ix1) + 1):
                bk[(ix, iz)].append((A, B, C))
    return bk


def tri_y_at(A, B, C, x, z):
    """(x,z) 处的三角面高度；不在三角内返回 None。"""
    x1, z1 = A[0], A[2]; x2, z2 = B[0], B[2]; x3, z3 = C[0], C[2]
    d = (z2 - z3) * (x1 - x3) + (x3 - x2) * (z1 - z3)
    if abs(d) < 1e-12:
        return None
    l1 = ((z2 - z3) * (x - x3) + (x3 - x2) * (z - z3)) / d
    l2 = ((z3 - z1) * (x - x3) + (x1 - x3) * (z - z3)) / d
    l3 = 1.0 - l1 - l2
    if l1 < -1e-6 or l2 < -1e-6 or l3 < -1e-6:
        return None
    return l1 * A[1] + l2 * B[1] + l3 * C[1]


def ny_of(A, B, C):
    ux, uy, uz = B[0] - A[0], B[1] - A[1], B[2] - A[2]
    vx, vy, vz = C[0] - A[0], C[1] - A[1], C[2] - A[2]
    nx = uy * vz - uz * vy
    ny = uz * vx - ux * vz
    nz = ux * vy - uy * vx
    n = math.sqrt(nx * nx + ny * ny + nz * nz)
    return 0.0 if n == 0 else ny / n


class Probe:
    def __init__(self, m, geo, bk):
        self.m, self.geo, self.bk = m, geo, bk
        self._g = {}

    def walkable(self, ix, iz):
        if ix < 0 or iz < 0 or ix >= self.m["W"] or iz >= self.m["D"]:
            return False
        idx = iz * self.m["W"] + ix
        return (self.m["bits"][idx >> 3] & (1 << (idx & 7))) != 0

    def center(self, ix, iz):
        c = self.geo["cell"]
        return self.geo["ox"] + (ix + 0.5) * c, self.geo["oz"] + (iz + 0.5) * c

    def ground(self, ix, iz):
        """格心最高的朝上面 y（None = 探不到）。"""
        key = (ix, iz)
        if key in self._g:
            return self._g[key]
        x, z = self.center(ix, iz)
        best = None
        for (A, B, C) in self.bk.get(key, ()):
            if ny_of(A, B, C) <= 0:
                continue
            y = tri_y_at(A, B, C, x, z)
            if y is not None and (best is None or y > best):
                best = y
        self._g[key] = best
        return best

    def clear_above(self, ix, iz, y0):
        """(格心, y0) 之上 [y0+FOOT_TOL, y0+STAND_H] 内有没有三角面。"""
        x, z = self.center(ix, iz)
        for (A, B, C) in self.bk.get((ix, iz), ()):
            y = tri_y_at(A, B, C, x, z)
            if y is not None and y0 + FOOT_TOL < y <= y0 + STAND_H:
                return False
        return True

    def components(self, strict):
        m = self.m
        seen = {}
        sizes = []
        for iz in range(m["D"]):
            for ix in range(m["W"]):
                if not self.walkable(ix, iz) or (ix, iz) in seen:
                    continue
                cid = len(sizes)
                q = deque([(ix, iz)])
                seen[(ix, iz)] = cid
                n = 0
                while q:
                    cx, cz = q.popleft()
                    n += 1
                    for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                        self._push(q, seen, cid, cx + dx, cz + dz)
                    if strict:
                        for dx, dz in ((1, 1), (1, -1), (-1, 1), (-1, -1)):
                            if self.walkable(cx + dx, cz) and self.walkable(cx, cz + dz):
                                self._push(q, seen, cid, cx + dx, cz + dz)
                sizes.append(n)
        return seen, sizes

    def _push(self, q, seen, cid, ix, iz):
        if self.walkable(ix, iz) and (ix, iz) not in seen:
            seen[(ix, iz)] = cid
            q.append((ix, iz))

    def report(self, label):
        m = self.m
        total = m["W"] * m["D"]
        walk = sum(1 for iz in range(m["D"]) for ix in range(m["W"]) if self.walkable(ix, iz))
        seen_w, sizes_w = self.components(False)
        seen_s, sizes_s = self.components(True)
        main_w = max(range(len(sizes_w)), key=lambda c: sizes_w[c])
        main_s = max(range(len(sizes_s)), key=lambda c: sizes_s[c])

        nog = [(ix, iz) for iz in range(m["D"]) for ix in range(m["W"])
               if self.walkable(ix, iz) and self.ground(ix, iz) is None]

        # 低矮障碍候选
        cands = []
        for iz in range(m["D"]):
            for ix in range(m["W"]):
                if self.walkable(ix, iz):
                    continue
                top = self.ground(ix, iz)
                if top is None:
                    continue
                for dx, dz in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, nz = ix + dx, iz + dz
                    if not self.walkable(nx, nz):
                        continue
                    g = self.ground(nx, nz)
                    if g is None:
                        continue
                    h = top - g
                    if 0.0 < h <= JUMP_REACH:
                        cands.append((ix, iz, h, self.clear_above(ix, iz, top)))
                        break
        low = [c for c in cands if 0.7 <= c[2] <= 1.0]

        print("---- %s ----" % label)
        print("  文件            : %s (%d 字节, ver=%d flags=0x%04X)" % (os.path.basename(m["path"]), m["size"], m["ver"], m["flags"]))
        print("  位图            : %dx%d cell=%.4f origin=(%.1f,%.1f) 格数=%d" % (m["W"], m["D"], m["cell"], m["ox"], m["oz"], total))
        print("  R1 可走格       : %d    阻挡格 = %d" % (walk, total - walk))
        print("  R2 连通分量     : 宽口径 %d 个（主分量 %d）｜严格口径 %d 个（主分量 %d）"
              % (len(sizes_w), sizes_w[main_w], len(sizes_s), sizes_s[main_s]))
        print("  R3 标记点       : %d 个｜落格可走 %d｜落主分量(宽) %d"
              % (len(m["markers"]),
                 sum(1 for (_, x, y, z) in m["markers"] if self.walkable(int(math.floor((x - m["ox"]) / m["cell"])), int(math.floor((z - m["oz"]) / m["cell"])))),
                 sum(1 for (_, x, y, z) in m["markers"]
                     if self.walkable(int(math.floor((x - m["ox"]) / m["cell"])), int(math.floor((z - m["oz"]) / m["cell"])))
                     and seen_w.get((int(math.floor((x - m["ox"]) / m["cell"])), int(math.floor((z - m["oz"]) / m["cell"])))) == main_w)))
        print("  R4 低矮障碍候选 : %d 个｜顶面可站 %d/%d｜h∈[0.70,1.00] 子集 %d 个"
              % (len(cands), sum(1 for c in cands if c[3]), len(cands), len(low)))
        print("  R5 可走但无地面 : %d 个 %s" % (len(nog), sorted(nog)[:20] + (["…"] if len(nog) > 20 else [])))
        return dict(walk=walk, blocked=total - walk, comps=(len(sizes_w), sizes_w[main_w], len(sizes_s), sizes_s[main_s]),
                    markers=(len(m["markers"]),), nog=set(nog), cands=set((c[0], c[1]) for c in cands),
                    low=set((c[0], c[1]) for c in low))


TARGETS = [(27, 63), (9, 102), (10, 102), (10, 109)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--before", required=True)
    ap.add_argument("--after")
    ap.add_argument("--geo", default=DEFAULT_GEO)
    a = ap.parse_args()

    geo = read_geo(a.geo)
    bk = bucket(triangles(geo), geo)
    pa = Probe(read_map(a.before), geo, bk)
    ra = pa.report("改前 " + a.before)
    rb = None
    if a.after:
        pb = Probe(read_map(a.after), geo, bk)
        rb = pb.report("改后 " + a.after)

        print("\n---- 差异 ----")
        print("  目标 4 格（位图可走 = W / 阻挡 = B）:")
        for (ix, iz) in TARGETS:
            print("     cell(%3d,%3d) 改前=%s 改后=%s" %
                  (ix, iz, "W" if pa.walkable(ix, iz) else "B", "W" if pb.walkable(ix, iz) else "B"))
        diff = sorted(rb["nog"] ^ ra["nog"])
        print("  可走但无地面：改前 %d → 改后 %d ；对称差 %d 格 %s"
              % (len(ra["nog"]), len(rb["nog"]), len(diff), diff[:40]))
        print("  低矮障碍候选：改前 %d → 改后 %d ；对称差 %d 格"
              % (len(ra["cands"]), len(rb["cands"]), len(ra["cands"] ^ rb["cands"])))
        print("  h∈[0.70,1.00] 子集：改前 %d → 改后 %d ；对称差 %d 格"
              % (len(ra["low"]), len(rb["low"]), len(ra["low"] ^ rb["low"])))
        same = (ra["comps"] == rb["comps"] and len(ra["cands"]) == len(rb["cands"])
                and len(ra["cands"] ^ rb["cands"]) == 0 and len(ra["low"] ^ rb["low"]) == 0
                and ra["walk"] - 4 == rb["walk"])
        print("  >>> RESULT-MAP: %s" % ("PASS" if same else "FAIL"))
        return 0 if same else 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
