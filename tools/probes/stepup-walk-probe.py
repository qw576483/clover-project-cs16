# -*- coding: utf-8 -*-
"""「≤ StepUpHeight 的台阶必须能走上去、且不卡不顿」离线用例（机制级、零依赖、可复算）。

模型范围
--------
复刻一帧的**完整**竖直链路，而不只是 `ResolveMove`：
  `ResolveMoveCore`（分段扫掠 + 深度闸门 + 抬腿第二遍）
  → 撞墙清速度（`CsMatch.StepActorPhysics` 的 `WallBlockVelocityRatio`）
  → 陡坡闸门 → 「常规贴地探测落空 ⇒ 抬一个台阶再探」→ 贴地吸附 / 软地板
几何 = **实体**（顶面 + 侧面，y 从 0 到顶面），台阶/楼梯就是真实几何。

三个变体
--------
  `flat`   关掉抬腿第二遍（对照：没有台阶能力的解算）
  `before` 抬腿第二遍但**只采纳水平位移**、y 仍照契约用 `to.y`
  `after`  抬腿第二遍**采纳落点脚高 gp.y**，并带 onground 闸门（原版 `PM_StepMove:2620`）

判据
----
每例报三个量（都是用户可见的）：
  走通  = 终点水平位移 > 起点（0 = 卡住不动）
  卡顿  = 「 wish > 0 但净位移 = 0」的帧数（= 用户说的「卡一下」）
  陷脚  = 「水平已经上了台阶、但脚高还停在下面」的帧数（= 用户说的「要跳一下」）
期望写在每例后面：`before` 是修前形态、`after` 是修后形态。

用例
----
  A 平地对照                       MOVING / 卡顿0 / 陷脚0（两侧同）
  B 0.406 m 单级（B 通实机那处）   before 卡死 ⇒ after MOVING/卡顿0/陷脚0
  C 0.406 → 0.813 两级连续         after 必须两级都上得去
  D 0.2 m 小台阶 ×5 级             after 必须一路平滑（用户报的「小台阶」）
  E 0.4572 m 正好一个台阶          after 可走（贴边不许被四舍五入挡掉）
  ── 负控 ──
  F 0.813 m 台沿（> StepUpHeight） 两侧都必须 STUCK（⛔ 不许神抬腿）
  G 0.4575 m 台沿（阈值上 0.3mm）   两侧都必须 STUCK（阈值没被放宽）
  H 空中（脚离地 0.5 m）推 0.406   两侧都必须 STUCK 且脚高不变（⛔ 不许跳跃中拔高）
"""
import math

U = 0.0254
STEP = 0.4572                 # CsConst.StepUpHeight = sv_stepsize 18 × 0.0254
RADIUS = 0.36                 # CsConst.PlayerRadius
BODY = 1.8                    # CsConst.StandHeight
SEG = 0.25                    # CsMap.MaxSweepStep
GROUNDePS = 0.12              # CsConst.GroundCheckDistance
DROPF = 8.0                   # TrySampleGround 默认 maxDrop
WALL_RATIO = 0.5              # CsMatchConst.WallBlockVelocityRatio
ACCEL = 0.14                  # sv_accelerate 5.5u/帧 × 60fps ÷ 60 ⇒ 每帧 Δv（量级）
VMAX = 0.35                   # 满速（≈ 250u = 6.35 m/s ÷ 60fps 的量级）

X0, X1 = -35.52, -30.64       # B 通走廊 x 范围（BSP 逐点读数）


# ── 几何（按实体建模：每段一个 (z0, z1, 顶面y)，侧面天然存在）────────────────
def _mk(levels):
    return [dict(x0=X0, x1=X1, z0=z0, z1=z1, top=top) for (z0, z1, top) in levels]


GEOM_FLAT = [(-40.0, 40.0, 0.0)]
GEOM_0406 = [(-40.0, -15.4, 0.0), (-15.4, 40.0, 0.406)]
GEOM_STAIRS = [(-40.0, -15.4, 0.0), (-15.4, -14.6, 0.406), (-14.6, 40.0, 0.813)]
GEOM_HIGHLIP = [(-40.0, -15.4, 0.0), (-15.4, 40.0, 0.813)]
GEOM_04572 = [(-40.0, -15.4, 0.0), (-15.4, 40.0, STEP)]
GEOM_04575 = [(-40.0, -15.4, 0.0), (-15.4, 40.0, STEP + 0.0003)]
# 0.2 m 小台阶 ×5 级（每级 2 m 深），用户报的「小台阶」
GEOM_SMALL = [(-40.0, -12.0, 0.0), (-12.0, -10.0, 0.2), (-10.0, -8.0, 0.4),
              (-8.0, -6.0, 0.6), (-6.0, -4.0, 0.8), (-4.0, 40.0, 1.0)]

SOLIDS = []


def set_geom(levels):
    global SOLIDS
    SOLIDS = _mk(levels)


CASES = [
    # (名字, 起点z, 帧数, 起点y, 几何, 越过z判"走通"的门槛, 期望old, 期望after)
    ("A 平地对照（z=-20）", -20.0, 12, 0.0, GEOM_FLAT, -19.0, "CLIMBED", "CLIMBED 卡顿0 陷脚0"),
    ("B 0.406m 单级（实机那处）", -16.20, 16, 0.0, GEOM_0406, -14.0, "STUCK", "CLIMBED 卡顿0 陷脚0"),
    ("C 0.406→0.813 两级连续", -16.20, 20, 0.0, GEOM_STAIRS, -10.0, "STUCK", "CLIMBED 卡顿0 陷脚0"),
    ("D 0.2m 小台阶 ×5 级", -14.00, 40, 0.0, GEOM_SMALL, -4.0, "STUCK", "CLIMBED 卡顿0 陷脚0"),
    ("E 0.4572m 正好一个台阶", -16.20, 16, 0.0, GEOM_04572, -14.0, "STUCK", "CLIMBED 卡顿0 陷脚0"),
    ("F 负控 0.813m 台沿", -16.20, 16, 0.0, GEOM_HIGHLIP, -14.0, "STUCK", "STUCK"),
    ("G 负控 0.4575m 台沿", -16.20, 16, 0.0, GEOM_04575, -14.0, "STUCK", "STUCK"),
    ("H 负控 空中推 0.406 台阶", -16.20, 16, 0.20, GEOM_0406, -14.0, "STUCK", "STUCK"),
]

MODES = (("old", "修前(中心零宽射线落点)"), ("after", "修后(包围盒落点+采纳脚高)"))


def inside_volume(x, feet, z):
    """点是否落在某个实体内部（> 底面 0、< 顶面）—— 决定 PhysX 有没有正面交点。"""
    for s in SOLIDS:
        if s["x0"] <= x <= s["x1"] and s["z0"] <= z <= s["z1"] and 0.0 < feet < s["top"] - 1e-6:
            return True
    return False


def ground_at(x, feet, z):
    """`CsMap.TrySampleGround`：从 feet+GroundCheckDistance 朝下打；点落在实体内 ⇒ 无交点（-inf）。"""
    if inside_volume(x, feet, z):
        return None
    origin = feet + GROUNDePS
    best = None
    for s in SOLIDS:
        if s["x0"] <= x <= s["x1"] and s["z0"] <= z <= s["z1"]:
            if s["top"] <= origin and (best is None or s["top"] > best):
                best = s["top"]
    return best


def ground_within_step(x, feet, z):
    """`CsMap.GroundWithinStep`：探不到 ⇒ 不否决；否则 point.y - feet ≤ StepUpHeight。"""
    g = ground_at(x, feet, z)
    return True if g is None else (g - feet <= STEP)


def bitmap_clear(x, z):
    """可走位图：走廊内全通（实机 walkHere=1）。"""
    return X0 - 0.5 <= x <= X1 + 0.5


def body_height_clear(x, feet, z):
    """`CsMap.BodyHeightClear`：身体体积带（feet+0.12 .. feet+BODY）与实体是否重叠。"""
    lo, hi = feet + GROUNDePS, feet + BODY
    for s in SOLIDS:
        if s["top"] <= lo:
            continue
        nx = min(max(x, s["x0"]), s["x1"])
        nz = min(max(z, s["z0"]), s["z1"])
        if math.hypot(x - nx, z - nz) < RADIUS and s["top"] > lo:
            return False
    return hi > lo


def penetration_depth(x, feet, z):
    """`CsMap.PenetrationDepth`（体积重叠的水平压入深度）。"""
    lo = feet + GROUNDePS
    d = 0.0
    for s in SOLIDS:
        if s["top"] <= lo:
            continue
        nx = min(max(x, s["x0"]), s["x1"])
        nz = min(max(z, s["z0"]), s["z1"])
        dist = math.hypot(x - nx, z - nz)
        if dist < RADIUS:
            d = max(d, RADIUS - dist)
    return d


def can_stand(x, feet, z):
    """`CsMap.CanStand`：位图 ∧ 落点在一步台阶内 ⇒ 通；否则看身体高度带。"""
    if bitmap_clear(x, z) and ground_within_step(x, feet, z):
        return True
    return body_height_clear(x, feet, z)


def try_step_up(x, feet, z):
    """`CsMap.TryStepUp`：抬腿的**判**（放行这一次水平推进，不抬 y）。"""
    g = ground_at(x, feet, z)
    if g is not None and g - feet > STEP:
        return False
    if not bitmap_clear(x, z):
        if g is None:
            return False
        if g - feet < 0.0:
            return False
        if body_height_clear(x, g, z) is False:      # 落点体积被占（位图说挡时）
            return False
    return True


def axis_passable(x, feet, z):
    return can_stand(x, feet, z) or try_step_up(x, feet, z)


def resolve_core(x, feet, z, tx, tz, depth0=0.0):
    """`CsMap.ResolveMoveCore` 的分段扫掠（不含抬腿第二遍）。返回 (x, y, z)。"""
    dx, dz = tx - x, tz - z
    dist = max(abs(dx), abs(dz))
    steps = max(1, int(math.ceil(dist / SEG)))
    cur_x, cur_z = x, z
    cur_depth = depth0
    for _ in range(steps):
        remx, remz = tx - cur_x, tz - cur_z
        remain = max(abs(remx), abs(remz))
        if remain <= 1e-4:
            break
        t = min(1.0, SEG / remain)
        want_x, want_z = cur_x + remx * t, cur_z + remz * t
        prev_x, prev_z = cur_x, cur_z
        px, pz = cur_x, cur_z                                   # StepOnce：先 X 后 Z
        if axis_passable(want_x, feet, pz):
            px = want_x
        if axis_passable(px, feet, want_z):
            pz = want_z
        advanced = (px != prev_x or pz != prev_z)
        if advanced:
            depth = penetration_depth(px, feet, pz)
            if depth > cur_depth + 1e-4:
                advanced = False                                 # 深度闸门：撤销该段
                f = 0.5
                for _h in range(3):
                    sx = prev_x + (want_x - prev_x) * f
                    sz = prev_z + (want_z - prev_z) * f
                    f *= 0.5
                    cx = sx if axis_passable(sx, feet, prev_z) else prev_x
                    cz = sz if axis_passable(cx, feet, sz) else prev_z
                    if cx == prev_x and cz == prev_z:
                        break
                    d = penetration_depth(cx, feet, cz)
                    if d > cur_depth + 1e-4:
                        continue
                    px, pz, cur_depth = cx, cz, d
                    advanced = True
                    break
            else:
                cur_depth = depth
        if not advanced:
            if (ground_at(want_x, feet, want_z) is not None
                    and body_height_clear(want_x, feet, want_z)
                    and ground_within_step(want_x, feet, want_z)):
                px, pz, cur_depth, advanced = want_x, want_z, 0.0, True   # 几何放行
        if not advanced:
            break
        cur_x, cur_z = px, pz
    return cur_x, feet, cur_z


def ground_under_body(x, feet, z):
    """`CsMap.TrySampleGroundUnderBody`：身体覆盖范围（中心 + 8 向半径采样）内取**最高**面。
    原版的下探是包围盒射线（VEC_HULL ±36 ⇒ 72 unit 盒）⇒ 身体覆盖到那一面就算落在那一面上。"""
    d = RADIUS * 0.70710678
    offs = [(0.0, 0.0),
            (1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0),
            (1.0, 1.0), (1.0, -1.0), (-1.0, 1.0), (-1.0, -1.0)]
    best = None
    for (ox, oz) in offs:
        r = RADIUS if abs(ox) + abs(oz) == 1 else d
        g = ground_at(x + ox * r, feet, z + oz * r)
        if g is not None and (best is None or g > best):
            best = g
    return best


def resolve_move(x, feet, z, tx, tz, mode, on_ground=True):
    """`CsMap.ResolveMove` = 贴地那遍 + （被钳住且 onground 时）抬腿第二遍。"""
    rx, ry, rz = resolve_core(x, feet, z, tx, tz)
    if mode == "flat":
        return rx, feet, rz
    want = math.hypot(tx - x, tz - z)
    got = math.hypot(rx - x, rz - z)
    if want <= 1e-8 or got >= want - 1e-6:
        return rx, feet, rz                                   # 没被钳住 ⇒ 原样返回
    if not on_ground:
        return rx, feet, rz                                   # 原版：离地走 PM_AirMove
    lift = feet + STEP
    if not body_height_clear(x, lift, z):
        return rx, feet, rz                                   # 原版：抬头被挡 ⇒ 不抬
    ux, _uy, uz = resolve_core(x, lift, z, tx, tz)
    up = math.hypot(ux - x, uz - z)
    if up <= got + 1e-6:
        return rx, feet, rz
    # 落点探测：old = 身体中心零宽射线；before/after = 身体覆盖范围取最高面（原版包围盒射线口径）
    gp = ground_at(ux, lift, uz) if mode == "old" else ground_under_body(ux, lift, uz)
    if gp is None or gp < feet - 1e-4 or gp - feet > STEP:
        return rx, feet, rz                                   # 落点不在 [脚面, 脚面+StepUpHeight]
    if body_height_clear(ux, gp, uz) is False:
        return rx, feet, rz
    # old / before = 只采纳水平（y 仍照契约 to.y）；after = 连脚高一起采纳（原版把 up 那一遍的 origin[2] 采纳）
    return (ux, gp, uz) if mode == "after" else (ux, feet, uz)


def physics_frame(st, wish, mode, on_ground=True):
    """一帧 `CsMatch.StepActorPhysics` 的竖直链路（重力尾段/贴地/软地板）。返回事件计数。"""
    x, feet, z = st["x"], st["y"], st["z"]
    rx, ry, rz = resolve_move(x, feet, z, x, z + wish, mode, on_ground)
    got = math.hypot(rx - x, rz - z)
    ev = {"moved": got > 1e-3, "lag": False, "zero": False}
    if got < wish * WALL_RATIO and wish > 1e-6:
        st["v"] = 0.0                                   # 撞墙清速度（x/z）
        ev["zero"] = True
    # ── 陡坡闸门（本模型里所有面法线 y=1，永不触发）──
    # ── 贴地 / 「抬一个台阶再探」/ 软地板 ──
    gp = ground_at(rx, ry, rz)
    if gp is None and on_ground:
        up = ground_at(rx, ry + STEP, rz)               # CsMatch 的抬升探测
        if up is not None and ry - 1e-6 <= up <= ry + STEP:
            gp = up
    if gp is not None and ry <= gp + GROUNDePS:
        st["soft"] = gp
        st["y"] = gp
        on_ground = True
    else:
        soft = st["soft"]
        if soft is not None and ry <= soft + GROUNDePS:
            st["y"] = soft
            on_ground = True
        else:
            on_ground = False
    ev["lag"] = (gp is not None and st["y"] < gp - 0.02 and got > 1e-3)
    st["x"], st["z"] = rx, rz
    return ev, on_ground


def main():
    print("=== ≤StepUpHeight 台阶「走得上、不卡顿」离线用例（机制级）===")
    print("几何 = B 通实测：z<=-15.4 顶面0.0 / -15.4..-14.6 顶面0.406 / z>=-14.6 顶面0.813（x∈[-35.52,-30.64]）")
    print("列: 终点z / 脚高y / 形态-卡顿帧-陷脚帧")
    print()
    fails = 0
    for name, sz, frames, sy, geom, z_pass, exp_old, exp_after in CASES:
        set_geom(geom)
        rows = []
        for mode, _label in MODES:
            st = {"x": -34.5, "y": sy, "z": sz, "v": 0.0, "soft": ground_at(-34.5, sy, sz)}
            stuck = lag = 0
            airborne = name.startswith("H")
            for _ in range(frames):
                st["v"] = min(VMAX, st["v"] + ACCEL)
                ev, _ = physics_frame(st, st["v"], mode, not airborne)
                if not ev["moved"]:
                    stuck += 1
                if ev["lag"]:
                    lag += 1
            rows.append((st["z"], st["y"], stuck, lag))
        (oz, oy, ost, olg), (az, ay, ast, alg) = rows
        got_old = "%s 卡顿%d 陷脚%d" % ("CLIMBED" if oz > z_pass else "STUCK", ost, olg)
        got_after = "%s 卡顿%d 陷脚%d" % ("CLIMBED" if az > z_pass else "STUCK", ast, alg)
        ok = exp_old.split()[0] == got_old.split()[0] and exp_after.split()[0] == got_after.split()[0]
        # 期望里的帧数也要对：期望「卡顿0 陷脚0」时帧数必须真的为 0
        if ok and exp_after.startswith("CLIMBED 卡顿0") and (ast or alg):
            ok = False
        if not ok:
            fails += 1
        print("%-26s %s z=%7.2f y=%5.2f  %-20s  (期望 %s)" % (name, MODES[0][1], oz, oy, got_old, exp_old))
        print("%-26s %s z=%7.2f y=%5.2f  %-20s  (期望 %s)  => %s"
              % ("", MODES[1][1], az, ay, got_after, exp_after, "OK" if ok else "RED"))
    print()
    print("summary: FAILED=%d" % fails)
    return fails


if __name__ == "__main__":
    raise SystemExit(1 if main() else 0)
