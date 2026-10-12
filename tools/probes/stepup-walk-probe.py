# -*- coding: utf-8 -*-
"""「≤ StepUpHeight 的台沿必须能走上去」离线用例（机制级、零依赖、可复算）。

判据
----
对 B 通那道**真实台阶**（原版 BSP 与本工程 geo.bin 逐点一致：0.000 / 0.406 / 0.813，
见 .ai-tmp/test/botstuck-vs.txt），从下台阶前 0.45 m 处连续朝北给满额 wish（每帧 0.25 m）：

  WALK  ⇔ 累计位移 ≥ 走到台阶顶（y 升到 0.406）且水平推进正常
  STUCK ⇔ 累计位移 < 1e-3 m 而每帧 wish 都 ≥ 0.24 m     ← 复现用户报的"卡在去 B 洞的路上"

四个用例：
  A 平地（对照组）            期望：现方案 MOVING，补"抬着走"后 MOVING
  B 0.406 m 台阶（实机那处）  期望：现方案 STUCK（复现缺陷），补后 WALK（判据绿）
  C 0.813 m 台沿（> StepUpHeight）期望：**两者都必须 STUCK**（⛔ 不许"神抬腿"）
  D 0.406 → 0.813 两级连续     期望：补后能走到 0.813 层

它是什么 / 不是什么
------------------
· **是**：对 `CsMap.ResolveMoveCore` 的判据结构（同样的闸门、顺序、常量）的解析几何最小模型 ——
  和仓内既有的 `tools/probes/phys-stuck-probe.py` 同一路数，每个判据都标出处行号。
· **不是**：产品代码本身（几何层走 UnityEngine.Physics，引擎外跑不了）⇒ 结论是**机制级**的，
  不替代实机取证。修完代码要同时跑它（离线回归）+ 一次实机（≥3 回合 0 卡住）。
"""
import math

U = 0.0254
STEP = 0.4572                 # CsConst.StepUpHeight
RADIUS = 0.36                 # CsConst.PlayerRadius
BODY = 1.8                    # 身高带（CsConst.StandHeight 量级）
SEG = 0.25                    # CsMap.MaxSweepStep
GROUNDePS = 0.12              # CsConst.GroundCheckDistance
DROP = 8.0                    # TrySampleGround 默认 maxDrop

# ---- 真实几何（B 通台阶，x 整条走廊同形；z 边界取 botstuck-vs.txt 的读数）----
X0, X1 = -35.52, -30.64       # 台阶/台地的 x 范围（BSP 读出的面范围）
Z_STEP_LO, Z_STEP_HI = -15.4, -14.6     # 0.406 那一级（世界 z）
Z_UP_HI = -5.0                          # 0.813 那一级往北到哪（只用于本用例）
Y_LOW, Y_STEP, Y_UP = 0.0, 0.406, 0.813


def _mk(levels):
    """平台/台阶按**实体**建模（顶面 + 侧面），y 从 0 到顶面（= 场景里的 MeshCollider 体积）。
    levels = [(z0, z1, top), …]"""
    return [dict(x0=X0, x1=X1, z0=z0, z1=z1, top=top) for (z0, z1, top) in levels]


# 三段真实几何（B 通台阶，botstuck-vs.txt 逐点读数）
GEOM_STAIRS = [(-40.0, Z_STEP_LO, Y_LOW), (Z_STEP_LO, Z_STEP_HI, Y_STEP), (Z_STEP_HI, Z_UP_HI, Y_UP)]
GEOM_FLAT = [(-40.0, 40.0, Y_LOW)]
GEOM_HIGHLIP = [(-40.0, Z_STEP_LO, Y_LOW), (Z_STEP_LO, 40.0, Y_UP)]     # 只有一级 0.813（假想"真缺级"）
SOLIDS = _mk(GEOM_STAIRS)


def set_geom(levels):
    global SOLIDS
    SOLIDS = _mk(levels)


def solids():
    return SOLIDS


def inside_volume(x, feet, z):
    """点是否落在某个实体**内部**（> 底面 0、< 顶面）。"""
    for s in solids():
        if s["x0"] <= x <= s["x1"] and s["z0"] <= z <= s["z1"] and 0.0 < feet < s["top"] - 1e-6:
            return True
    return False


def ground_at(x, feet, z):
    """TrySampleGround 口径（CsMap.cs:671-682）：从 feet+0.12 朝下打 DROP。
       点落在实体内 ⇒ PhysX(queriesHitBackfaces=false) 无交点 ⇒ None（= -inf）。"""
    if inside_volume(x, feet, z):
        return None
    origin = feet + GROUNDePS
    best = None
    for s in solids():
        if s["x0"] <= x <= s["x1"] and s["z0"] <= z <= s["z1"]:
            if s["top"] <= origin and (best is None or s["top"] > best):
                best = s["top"]
    return best


def ground_within_step(x, feet, z):
    """CsMap.GroundWithinStep（CsMap.cs:296-305）：探不到 ⇒ true；否则 point.y-feet <= StepUpHeight。"""
    g = ground_at(x, feet, z)
    if g is None:
        return True
    return g - feet <= STEP


def bitmap_clear(x, z, radius):
    """位图口径：本用例里走廊内所有格可走（实机 walkHere=1，见 bot-stuck.tsv）。"""
    return X0 - 0.5 <= x <= X1 + 0.5


def body_height_clear(x, feet, z, radius):
    """CsMap.BodyHeightClear（CsMap.cs:325-348）：身高带被实体占 ⇒ false。"""
    for s in solids():
        if s["top"] <= feet + 1e-6:
            continue
        nx = min(max(x, s["x0"]), s["x1"])
        nz = min(max(z, s["z0"]), s["z1"])
        dist = math.hypot(x - nx, z - nz)
        if dist < radius:
            if feet < s["top"] and feet + BODY > 0.0:
                return False
    return True


def penetration_depth(x, feet, z, radius):
    """CsMap.PenetrationDepth（CsMap.cs:515-553 的闸门量）：胶囊与实体的水平重叠深度。"""
    d = 0.0
    for s in solids():
        if s["top"] <= feet + 1e-6:
            continue
        nx = min(max(x, s["x0"]), s["x1"])
        nz = min(max(z, s["z0"]), s["z1"])
        dist = math.hypot(x - nx, z - nz)
        if dist < radius:
            d = max(d, radius - dist)
    return d


def can_stand(x, feet, z, radius):
    """CsMap.CanStand（CsMap.cs:254-276）。"""
    if bitmap_clear(x, z, radius) and ground_within_step(x, feet, z):
        return True
    return body_height_clear(x, feet, z, radius)


def try_step_up(x, feet, z, radius, dirz):
    """CsMap.TryStepUp（CsMap.cs:619-659）：抬腿判据 —— **只判"能不能迈"，不抬 y**。"""
    g = ground_at(x, feet, z)
    if g is not None and g - feet > STEP:
        return False
    if g is not None and g - feet < 0.0 and not bitmap_clear(x, z, radius):
        return False
    if body_height_clear(x, feet, z, radius):
        return True
    return True       # 膝盖射线在解析模型里按通畅处理（台阶侧面高 0.406 < 膝盖 0.457）


def axis_passable(x, feet, z, radius, dz):
    return can_stand(x, feet, z, radius) or try_step_up(x, feet, z, radius, dz)


def resolve_move_core(x, feet, z, tx, tz, radius, lifted):
    """CsMap.ResolveMoveCore 的分段扫掠（CsMap.cs:485-577）。
       lifted=False = 现状；lifted=True = 提案（钳住时把起点整体抬 StepUpHeight 再解算，采纳抬高那遍的水平位移）。
       返回 (x, feet, z, moved)。"""
    if lifted:
        straight = resolve_move_core(x, feet, z, tx, tz, radius, False)
        same_h = math.hypot(straight[0] - x, straight[2] - z)
        want_h = math.hypot(tx - x, tz - z)
        if same_h >= want_h - 1e-6:
            return straight
        rx, rf, rz = x, feet + STEP, z                       # 起点整体抬起（试算）
        up = resolve_move_core(rx, rf, rz, tx, tz, radius, False)
        up_h = math.hypot(up[0] - x, up[2] - z)
        if up_h <= same_h + 1e-6:
            return straight
        g = ground_at(up[0], rf, up[2])                      # 落点地面必须落在 [脚面, 脚面+StepUpHeight]
        if g is None or g - feet < 0.0 or g - feet > STEP:
            return straight
        if not body_height_clear(up[0], g, up[2], radius):
            return straight
        return (up[0], g, up[2], True)                       # y 由贴地探测给出，⛔ 不直接抬 y

    dx, dz = tx - x, tz - z
    dist = max(abs(dx), abs(dz))
    steps = max(1, int(math.ceil(dist / SEG)))
    cur_x, cur_f, cur_z = x, feet, z
    cur_depth = penetration_depth(x, feet, z, radius)
    for _ in range(steps):
        remx, remz = tx - cur_x, tz - cur_z
        remain = max(abs(remx), abs(remz))
        if remain <= 1e-4:
            break
        t = min(1.0, SEG / remain)
        want_x, want_z = cur_x + remx * t, cur_z + remz * t
        prev = (cur_x, cur_z)
        # StepOnce：先 X 后 Z（CsMap.cs:581-591）
        px, pz = cur_x, cur_z
        if axis_passable(want_x, cur_f, pz, radius, 0.0):
            px = want_x
        if axis_passable(px, cur_f, want_z, radius, want_z - pz):
            pz = want_z
        advanced = (px != prev[0] or pz != prev[1])
        if advanced:
            depth = penetration_depth(px, cur_f, pz, radius)
            if depth > cur_depth + 1e-4:
                advanced = False                     # 深度闸门：撤销这一段（并按更短位移重试 → 解析模型里直接放弃）
            else:
                cur_depth = depth
        if not advanced:
            probe_x, probe_z = want_x, want_z
            if (ground_at(probe_x, cur_f, probe_z) is not None
                    and body_height_clear(probe_x, cur_f, probe_z, radius)
                    and ground_within_step(probe_x, cur_f, probe_z)):
                px, pz = probe_x, probe_z
                cur_depth = 0.0
                advanced = True
        if not advanced:
            break
        cur_x, cur_z = px, pz
    return (cur_x, cur_f, cur_z, math.hypot(cur_x - x, cur_z - z) > 1e-3)


def run(name, start_z, frames, expect_now, expect_new, x=-34.5, levels=None):
    set_geom(levels if levels is not None else GEOM_STAIRS)
    out = []
    for lifted, tag in ((False, "现状"), (True, "补'抬着走'")):
        cx, cf, cz = x, 0.0, start_z
        for _ in range(frames):
            g = ground_at(cx, cf, cz)
            if g is not None:
                cf = g
            cx, cf, cz, _ = resolve_move_core(cx, cf, cz, cx, cz + SEG, RADIUS, lifted)
        out.append((tag, round(cx, 3), round(cf, 3), round(cz, 3)))
    now_ok = "MOVING" if out[0][3] > start_z + 1e-3 else "STUCK"
    new_ok = "MOVING" if out[1][3] > start_z + 1e-3 else "STUCK"
    verdict = "OK" if (now_ok == expect_now and new_ok == expect_new) else "RED"
    print("%-34s 现状=%s %s  补后=%s %s   期望(现状=%s/补后=%s)  => %s"
          % (name, out[0][1:], now_ok, out[1][1:], new_ok, expect_now, expect_new, verdict))
    return verdict


print("=== ≤StepUpHeight 台沿「必须能走上去」离线用例（机制级；常量与出处见文件头）===")
print("geometry: 0.0 (z ≤ -15.4) / 0.406 (-15.4..-14.6) / 0.813 (z ≥ -14.6), x∈[-35.52,-30.64]")
print()
res = []
res.append(run("A 平地对照（z=-20）", -20.0, 12, "MOVING", "MOVING", levels=GEOM_FLAT))
res.append(run("B 0.406m 台阶（实机那处 z=-15.45）", -15.45, 12, "STUCK", "MOVING", levels=GEOM_STAIRS))
res.append(run("C 0.813m 台沿（> StepUp，假想缺级）", -15.45, 12, "STUCK", "STUCK", levels=GEOM_HIGHLIP))
res.append(run("D 两级连续 0.406→0.813（z=-15.45）", -15.45, 24, "STUCK", "MOVING", levels=GEOM_STAIRS))
print()
print("summary: %s" % ("FAILED=%d" % res.count("RED") if "RED" in res else "FAILED=0"))
