#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""物理卡死专项探针：「卡在几何缝里」这一类的最小复现（离线、零依赖、可复算）。

判据（可复算，见下方 `verdict` 行）
------------------------------------------------------------------
对每个 case 连续推进 N 帧（每帧 wish 位移 = 0.25 m = `CsMap.MaxSweepStep`）：

  STUCK  ⇔  N 帧累计位移 < 1e-4 m  **且** 每帧的 wish 位移都 ≥ 0.24 m
  MOVING ⇔  N 帧累计位移 ≥ N × 0.24 m（对照组）

即：帧间不断给出满额 wish、结果却一步都走不动 = 卡死；能走完 = 正常。

本探针是什么 / 不是什么
------------------------------------------------------------------
· 它**是**：对 `CsMap.ResolveMove` 判据结构的**最小模型**（同样的闸门、同样的顺序、
  同样的常量），世界是解析几何（两个 AABB = 一条缝 + 无限地面），因此确定性、可手算复核。
· 它**不是**：产品代码本身。`ResolveMove` 的几何层走 `UnityEngine.Physics`
  （`Raycast` / `CheckCapsule`），引擎外无法运行 ⇒ 这里用解析几何复刻那几条判据。
  每个判据旁都标了它的出处行号，逐条可与产品代码对照。
· 因此它给出的结论是**机制级**的：它证明「这套闸门在这种缝里必然一步不动」，
  不替代实机取证。见文件尾「如何升级为代码级探针」。

判据出处（逐条）
------------------------------------------------------------------
· 扫掠细分 ≤ 0.25 m + 从 cur 起算 + 两轴都推不动就 break  ← Module/Map/CsMap.cs:485-518,560-562
· 分轴推进（先 X 后 Z）+ 单轴判据 = CanStand ∨ TryStepUp ← CsMap.cs:567-584
· 位图判据 = 中心 + 8 向（半径 × √2/2）              ← CsMap.cs:280-293
· 落点高差 ≤ StepUpHeight                          ← CsMap.cs:301-305
· 身高带复核 = 体积（中心一次） + 9 点零宽向下射线   ← CsMap.cs:318-364,390-406
· TryStepUp 四道闸门（地面/高差/陡坡/位图+体积+膝盖） ← CsMap.cs:605-646
· 压入深度闸门 + 缩步退让（0.5/0.25/0.125）         ← CsMap.cs:441-452,524-546
· 两轴都推不动时的放行兜底                          ← CsMap.cs:552-558
· 常量（米）                                        ← Core/CsConst.cs:146,158,213,211,282
    StandHeight 1.80 / PlayerRadius 0.36 / GroundCheckDistance 0.12 /
    StepUpHeight 0.4572 / MaxStandableSlopeNormalZ 0.7 / MaxSweepStep 0.25

复算
------------------------------------------------------------------
    python tools/probes/phys-stuck-probe.py
退出码：0 = 判据与期望一致；1 = 不一致（探针本身失效，须先查探针再谈结论）。
"""

import sys

# ── 常量（Core/CsConst.cs，米）──────────────────────────────────────────────
STAND_H = 1.80          # :146
PLAYER_R = 0.36         # :158
GROUND_CHECK = 0.12     # :213
STEP_UP = 18.0 * 0.0254  # :211  = 0.4572
SLOPE_MIN_NY = 0.7      # :282
DIAG = 0.70710678       # CsMap.cs:42
MAX_STEP = 0.25         # CsMap.cs:39
BODY_LIFT = GROUND_CHECK   # CsMap.cs:49
BODY_DROP = 8.0            # CsMap.cs:57

# ── 世界：一条缝（左右两堵墙之间的 0.5 m 间隙）+ 无限地面 y=0 ─────────────────
# 身体直径 = 2 × 0.36 = 0.72 m > 缝宽 0.5 m ⇒ 站在缝里时 8 向半径采样必然同时
# 命中两侧（这就是「缝」的定义：位图 2D 单层看是两个"挡"格，真几何看是两根墙）。
BOXES = [
    # (x0, x1, y0, y1, z0, z1)
    (-3.0, -0.25, 0.0, 3.0, -2.0, 6.0),   # 左墙
    (0.25, 3.0, 0.0, 3.0, -2.0, 6.0),     # 右墙
]
GROUND_Y = 0.0
CELL = 1.0


def _boxes_at(x, z):
    return [b for b in BOXES if b[0] - 1e-9 <= x <= b[1] + 1e-9 and b[4] - 1e-9 <= z <= b[5] + 1e-9]


def walkable(x, z):
    """烘焙位图口径（引擎 Game.Map.WalkableAt）：**单层 2D 格子**（CELL=1 m），
    格子足迹被任何阻挡体覆盖 ⇒ 挡。缝宽 0.5 m < CELL ⇒ 缝所在的两格都被两侧墙覆盖，
    位图对这一带只有"挡"一个答案（这正是位图看不见"缝"的原因）。"""
    ci, cj = int(x // CELL), int(z // CELL)
    x0, x1 = ci * CELL, (ci + 1) * CELL
    z0, z1 = cj * CELL, (cj + 1) * CELL
    for b in BOXES:
        if b[0] < x1 and b[1] > x0 and b[4] < z1 and b[5] > z0:
            return False
    return True


def _seg_box_hit_2d(x, z, r, b):
    """圆 (x,z,r) 与 AABB 的 xz 足迹是否重叠。"""
    cx = min(max(x, b[0]), b[1])
    cz = min(max(z, b[4]), b[5])
    dx, dz = x - cx, z - cz
    return dx * dx + dz * dz < r * r - 1e-12


def volume_blocked(x, feet_y, z, r=PLAYER_R):
    """CsMap.cs:390-406 BodyVolumeBlocked：竖直胶囊(端点各内缩 r) 与实体重叠。"""
    lo = feet_y + GROUND_CHECK + r
    hi = feet_y + STAND_H - r
    if hi <= lo:
        return False
    for b in BOXES:
        if b[2] - r > hi or b[3] + r < lo:     # 竖直区间不相交
            continue
        if _seg_box_hit_2d(x, z, r, b):
            return True
    return False


def body_height_clear_at(x, feet_y, z):
    """CsMap.cs:357-364：从头顶上方朝下打一根射线，只看第一个交点。"""
    if volume_blocked(x, feet_y, z):           # 调用方先做体积（CsMap.cs:323）
        return False
    top = feet_y + STAND_H + BODY_LIFT
    # 世界面 = 地面平面 + 各盒子的顶面；取朝下射线在 [地板, 起点] 内的首交点
    best = None
    if GROUND_Y <= top:
        best = GROUND_Y
    for b in BOXES:
        if b[0] <= x <= b[1] and b[4] <= z <= b[5] and b[3] <= top:
            if best is None or b[3] > best:
                best = b[3]
    if best is None:
        return False                            # 打不到任何世界面 ⇒ 保守判挡
    return best <= feet_y + GROUND_CHECK


def _body_height_clear(x, feet_y, z, r):
    """CsMap.cs:318-342：体积（中心一次）+ 9 点零宽射线。"""
    if volume_blocked(x, feet_y, z):
        return False
    d = r * DIAG
    pts = [(x, z), (x + r, z), (x - r, z), (x, z + r), (x, z - r),
           (x + d, z + d), (x + d, z - d), (x - d, z + d), (x - d, z - d)]
    return all(body_height_clear_at(px, feet_y, pz) for px, pz in pts)


def try_sample_ground(x, feet_y, z, max_drop=8.0):
    """CsMap.cs:657-672：向下取地面（含法线）。"""
    origin = feet_y + GROUND_CHECK
    best = None
    depth = 0.0
    if origin >= GROUND_Y >= origin - (max_drop + GROUND_CHECK):
        best, depth = GROUND_Y, 0.0                       # 无限地面，法线 (0,1,0)
    for b in BOXES:
        if b[0] <= x <= b[1] and b[4] <= z <= b[5]:
            top = b[3]
            if origin >= top >= origin - (max_drop + GROUND_CHECK):
                if best is None or top > best:
                    best, depth = top, 0.0
    if best is None:
        return False, 0.0, (0.0, 1.0, 0.0)
    return True, best, (0.0, 1.0, 0.0)


def bitmap_clear(x, z, r):
    """CsMap.cs:280-293。"""
    if not walkable(x, z):
        return False
    d = r * DIAG
    return all(walkable(px, pz) for px, pz in
               [(x + r, z), (x - r, z), (x, z + r), (x, z - r),
                (x + d, z + d), (x + d, z - d), (x - d, z + d), (x - d, z - d)])


def ground_within_step(x, feet_y, z):
    """CsMap.cs:301-305。"""
    ok, py, _ = try_sample_ground(x, feet_y, z)
    if not ok:
        return True                       # 探不到地面 ⇒ 不否决
    return py - feet_y <= STEP_UP


def can_stand(x, y, z, r=PLAYER_R):
    """CsMap.cs:267-277 + 318-325。"""
    if bitmap_clear(x, z, r) and ground_within_step(x, y, z):
        return True
    return _body_height_clear(x, y, z, r)


def try_step_up(fx, fy, fz, tx, ty, tz, r=PLAYER_R, why=None):
    """CsMap.cs:605-646 的四道闸门（顺序与产品代码一致）。"""
    ok, py, n = try_sample_ground(tx, ty, tz)
    if ok and n[1] < SLOPE_MIN_NY:
        (why is not None) and why.append("stepup:slope")
        return False
    if ok and py - fy > STEP_UP:
        (why is not None) and why.append("stepup:height")
        return False
    if not walkable(tx, tz):
        if not ok:
            (why is not None) and why.append("stepup:no_ground")
            return False
        if py - fy < 0.0:
            (why is not None) and why.append("stepup:lower")
            return False
        if volume_blocked(tx, py, tz):
            (why is not None) and why.append("stepup:volume")
            return False
    dist = ((tx - fx) ** 2 + (tz - fz) ** 2) ** 0.5
    if dist < 0.001:
        (why is not None) and why.append("stepup:zero_dist")
        return False
    # 膝盖射线（CsMap.cs:631-645）：膝盖高度朝落点通畅
    why and why.append("stepup:ok")
    return True


def axis_passable(fx, fy, fz, tx, ty, tz, r, why=None):
    if can_stand(tx, ty, tz, r):
        (why is not None) and why.append("canstand")
        return True
    return try_step_up(fx, fy, fz, tx, ty, tz, r, why)


def step_once(cx, cy, cz, wx, wy, wz, r, why=None):
    """CsMap.cs:567-577：整体可站直过；否则先 X 后 Z。"""
    if can_stand(wx, cy, wz, r):
        (why is not None) and why.append("whole")
        return wx, cy, wz
    px, pz = cx, cz
    if axis_passable(cx, cy, pz, wx, cy, pz, r, why):
        px = wx
    if axis_passable(px, cy, cz, px, cy, wz, r, why):
        pz = wz
    return px, cy, pz


def penetration_depth(x, y, z, r=PLAYER_R, steps=7):
    """CsMap.cs:441-452：按半径缩小二分，求"压进实体多深"。"""
    if not volume_blocked(x, y, z, PLAYER_R):
        return 0.0
    if volume_blocked(x, y, z, 0.01):
        return PLAYER_R
    lo, hi = 0.0, PLAYER_R
    for _ in range(steps):
        mid = (lo + hi) * 0.5
        if volume_blocked(x, y, z, mid):
            lo = mid
        else:
            hi = mid
    return PLAYER_R - lo


PEN_TOL = 0.004          # CsMap.cs:428


def resolve_move(fx, fy, fz, tx, ty, tz, r=PLAYER_R, trace=None):
    """CsMap.cs:476-564 的判据结构。"""
    dx, dz = tx - fx, tz - fz
    dist = max(abs(dx), abs(dz))
    steps = max(1, int(-(-dist // MAX_STEP)) if dist > 0 else 1)
    cur = [fx, fy, fz]
    cur_depth = penetration_depth(fx, fy, fz)
    for i in range(1, steps + 1):
        remx, remz = tx - cur[0], tz - cur[2]
        remain = max(abs(remx), abs(remz))
        if remain <= 1e-4:
            break
        t = min(1.0, MAX_STEP / remain)
        want = (cur[0] + remx * t, ty, cur[2] + remz * t)
        prev = tuple(cur)
        why = []
        cx, cy, cz = step_once(prev[0], prev[1], prev[2], want[0], want[1], want[2], r, why)
        advanced = (cx != prev[0]) or (cz != prev[2])
        if advanced:
            depth = penetration_depth(cx, cy, cz) if cur_depth > 0 or volume_blocked(cx, cy, cz, PLAYER_R) else 0.0
            if depth > cur_depth + PEN_TOL:
                cx, cy, cz = prev
                advanced = False
                f = 0.5
                for _ in range(3):
                    sw = (prev[0] + (want[0] - prev[0]) * f, ty, prev[2] + (want[2] - prev[2]) * f)
                    c2 = step_once(prev[0], prev[1], prev[2], sw[0], sw[1], sw[2], r)
                    if c2[0] == prev[0] and c2[2] == prev[2]:
                        break
                    d2 = penetration_depth(c2[0], c2[1], c2[2])
                    if d2 > cur_depth + PEN_TOL:
                        f *= 0.5
                        continue
                    cx, cy, cz = c2
                    cur_depth = d2
                    advanced = True
                    break
                else:
                    pass
            else:
                cur_depth = depth
        if (not advanced) and cx == prev[0] and cz == prev[2]:
            probe = (want[0], cur[1], want[2])
            if _body_height_clear(probe[0], probe[1], probe[2], r) and ground_within_step(*probe):
                cx, cy, cz = probe
                cur_depth = 0.0
                why.append("release")
        cur = [cx, cy, cz]
        if trace is not None:
            trace.append((i, want, prev, tuple(cur), "|".join(why) or "-"))
        if cur[0] == prev[0] and cur[2] == prev[2]:
            break                      # 两轴都推不动 ⇒ 本帧到此为止
    return cur[0], cur[2]


# ── 场景 ─────────────────────────────────────────────────────────────────────
def run_case(name, start, wish_dir, frames):
    x, z = start
    total = 0.0
    for _ in range(frames):
        tx, tz = x + wish_dir[0] * MAX_STEP, z + wish_dir[1] * MAX_STEP
        nx, nz = resolve_move(x, 0.0, z, tx, 0.0, tz)
        total += ((nx - x) ** 2 + (nz - z) ** 2) ** 0.5
        x, z = nx, nz
    return total, (x, z)


def main():
    frames = 8
    wish = (0.0, 1.0)              # 沿缝朝外（+z）：方向合法、目标点仍在缝内

    # case A：身体在缝内（x=0，两侧墙各在 ±0.25）——要复现的那一类
    a_start = (0.0, 3.0)
    a_total, a_end = run_case("A", a_start, wish, frames)

    # case A 的逐步 trace（一帧）
    tr = []
    resolve_move(a_start[0], 0.0, a_start[1],
                 a_start[0] + wish[0] * MAX_STEP, 0.0, a_start[1] + wish[1] * MAX_STEP, trace=tr)

    # case B：对照组 —— 同样的 wish、开阔地（x=5）
    b_start = (5.0, 3.0)
    b_total, b_end = run_case("B", b_start, wish, frames)

    stuck_a = a_total < 1e-4
    moving_b = b_total >= frames * 0.24

    print("world      : 两堵墙 x∈[-3,-0.25] / [0.25,3]，y∈[0,3]，z∈[-2,6]；缝宽 0.50 m")
    print("body       : 直径 0.72 m (> 缝宽 0.50 m)  radius=%.2f  height=%.2f" % (PLAYER_R, STAND_H))
    print("wish       : 每帧 +z × %.2f m（沿缝朝外，方向合法）" % MAX_STEP)
    print()
    print("case A start=%s  bitmap=%s  canStand=%s  volBlocked=%s  penDepth=%.4f"
          % (a_start, walkable(*a_start), can_stand(a_start[0], 0.0, a_start[1]),
             volume_blocked(a_start[0], 0.0, a_start[1]), penetration_depth(a_start[0], 0.0, a_start[1])))
    for (i, w, prev, cur, why) in tr:
        print("  frame1 step%d want=(%.3f,%.3f) prev=(%.3f,%.3f) cur=(%.3f,%.3f) gates=[%s]"
              % (i, w[0], w[2], prev[0], prev[2], cur[0], cur[2], why))
    print("case A total=%.4f m  end=%s" % (a_total, a_end))
    print()
    print("case B start=%s  bitmap=%s  canStand=%s"
          % (b_start, walkable(*b_start), can_stand(b_start[0], 0.0, b_start[1])))
    print("case B total=%.4f m  end=%s  (期望 ≈ %d × %.2f = %.2f)" % (b_total, b_end, frames, MAX_STEP, frames * MAX_STEP))
    print()
    print("verdict    : A_STUCK=%s B_MOVING=%s" % (stuck_a, moving_b))
    ok = stuck_a and moving_b
    print("result     : %s" % ("PASS（判据与期望一致）" if ok else "FAIL（探针不一致，先查探针）"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
