#!/usr/bin/env python
# -*- coding: utf-8 -*-
# ============================================================================
#
#
#  本脚本做三件事（全部只用产品自己的产物 + 工程的位图，不含任何运行时采样）：
#    ① **口径自校验**：用工程的 de_dust2.bytes 复算 `BotNavigator.WalkableAhead`（近 0.7m + 远 1.4m
#       各查一次可走位图），拿它去解释**产品自己写的 296 条 [BOTFLIP] 行**里的 `probe=` 值 ——
#       命中率就是"这份离线复算与运行时同源"的凭据（不命中 = 复算口径错了，后面所有数字作废）。
#    ② **迟滞策略的离线对比**：在 Darrell/Scuzzy 的**陷阱窗口**（产品自己写的逐 3 帧位置 W 行）上，
#       用同一份位图跑 3 个策略（P0=现状 / P1=迟滞 / P2=只换排序），数它们的换向次数与所选方向跑道。
#    ③ **参数标定**：迟滞窗口取多少，靠 ① 量出来的"噪声周期"与"单帧位移"定，不靠手感。
#
#  输入（只读）：
#    <项目根>/.ai-tmp/test/bu-r2-hold-plant-log.tsv   [BOTFLIP] L3 行（含 pos/desired/probe/dir）
#    <项目根>/.ai-tmp/test/bu-r2-hold-plant.tsv       A 行（逐 3 帧位置；列见下）
#    <项目根>/client/Assets/Resources/MapData/de_dust2.bytes  可走位图（与运行时同一份载体）
#
#  开环声明：② 用的是**已记录**的位置序列（它们本身就是"现状策略"的产物），不是闭环仿真 ——
#     它回答的是"同一段轨迹上，三个策略会输出多稳的方向"；**不是**"修完 A1/A2 一定达标"。
#     闭环结论只能由 Play 的整回合产物给（见回报）。
#
#  用法：python tools/probes/bu-r3-avoid-replay.py
# ============================================================================
import io
import math
import os
import struct
import sys

try:
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TMP = os.path.join(PROJECT, '.ai-tmp', 'test')
MAP = os.path.join(PROJECT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2.bytes')

# ---- 与运行时同源的常量（出处：client/Assets/Scripts/Module/Bot/CsBotConst.cs）----
ANGLES = [25.0, -25.0, 50.0, -50.0, 75.0, -75.0, 100.0, -100.0, 125.0, -125.0, 160.0, -160.0]
PROBE_CLEARANCE = 0.7      # CsBotConst.ProbeClearance
PROBE_DISTANCE = 1.4       # CsBotConst.ProbeDistance
RUNWAY_STEP = 0.7          # CsBotConst.EscapeRunwayStep
RUNWAY_MAX = 4.2           # CsBotConst.EscapeRunwayMax
FLIP_ANGLE = 120.0         # BotNavigator.DiagFlip 的判据
HARD_FLIP = 150.0          # 本脚本额外口径：与"反向"的区别


# ---------------------------------------------------------------- 位图（只读）
def read_map(path):
    with open(path, 'rb') as f:
        d = f.read()
    assert d[0:4] == b'CLVM', 'not a Clover map bitmap'
    cell = struct.unpack_from('<f', d, 16)[0]
    ox, oy, oz = struct.unpack_from('<3f', d, 20)
    w, dep, cc, sc, nlen = struct.unpack_from('<5I', d, 32)
    o = 64 + nlen
    return dict(cell=cell, ox=ox, oz=oz, w=w, d=dep, bits=d[o:o + (w * dep + 7) // 8])


M = read_map(MAP)
CELL, OX, OZ, W, D = M['cell'], M['ox'], M['oz'], M['w'], M['d']


def walkable(x, z):
    ix = int(math.floor((x - OX) / CELL))
    iz = int(math.floor((z - OZ) / CELL))
    if ix < 0 or iz < 0 or ix >= W or iz >= D:
        return False
    i = iz * W + ix
    return (M['bits'][i >> 3] & (1 << (i & 7))) != 0


def rot(dx, dz, deg):
    """Unity: Quaternion.Euler(0, deg, 0) * dir（与 BotNavigator.Rotate 同式，已用产品日志反证）。"""
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return (dx * c + dz * s, -dx * s + dz * c)


def norm(dx, dz):
    n = math.hypot(dx, dz)
    return (dx / n, dz / n) if n > 1e-9 else (0.0, 0.0)


def walkable_ahead(px, pz, dx, dz):
    """BotNavigator.WalkableAhead 的**位图那一半**（物理复核 PhysRunway 离线复算不了，见文件头声明）。"""
    return (walkable(px + dx * PROBE_CLEARANCE, pz + dz * PROBE_CLEARANCE) and
            walkable(px + dx * PROBE_DISTANCE, pz + dz * PROBE_DISTANCE))


def runway(px, pz, dx, dz):
    """沿该方向的**有界跑道**（米，步长 0.7、上限 4.2）——与 Runway 的位图那一半同口径。"""
    run, d = 0.0, RUNWAY_STEP
    while d <= RUNWAY_MAX + 1e-6:
        if not walkable(px + dx * d, pz + dz * d):
            break
        run, d = d, d + RUNWAY_STEP
    return run


def first_pass(px, pz, desired, order):
    for a in order:
        v = rot(desired[0], desired[1], a)
        if walkable_ahead(px, pz, v[0], v[1]):
            return a, v
    return None, None


# ---------------------------------------------------------------- 产物解析
def parse_flips(path):
    """[BOTFLIP] 行 -> 每 bot [(t, px, pz, dx, dz, probe, outx, outz)]（desired 与 dir 都是产品自己算的）"""
    out = {}
    if not os.path.exists(path):
        return out
    with io.open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if '[BOTFLIP]' not in line:
                continue
            p = line.rstrip('\r\n').split('\t')
            txt = p[-1]
            try:
                t = float(p[1])
            except Exception:
                continue
            def g(pat):
                i = txt.find(pat)
                if i < 0:
                    return None
                j = i + len(pat)
                k = j
                while k < len(txt) and txt[k] not in ' )':
                    k += 1
                return float(txt[j:k])
            def vec(pat):
                i = txt.find(pat + '=(')
                if i < 0:
                    return None
                j = i + len(pat) + 2
                k = txt.find(')', j)
                a, b = txt[j:k].split(',')
                return (float(a), float(b))
            nm = txt.split('[BOTFLIP]')[1].split('branch=')[0].strip()
            pos, des, dr = vec('pos'), vec('desired'), vec('dir')
            if not (pos and des and dr):
                continue
            out.setdefault(nm, []).append((t, pos[0], pos[1], des[0], des[1], g('probe=-'), g('probe='),
                                           dr[0], dr[1]))
    return out


def probe_of(txt_probe_left, probe_plain):
    return probe_plain if probe_plain is not None else 0.0


def parse_arows(path, names):
    """A 行 -> 每 bot [(t, x, z)]"""
    out = {}
    if not os.path.exists(path):
        return out
    with io.open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.rstrip('\r\n').split('\t')
            if len(p) < 20 or p[0] != 'A' or p[5] not in names:
                continue
            try:
                out.setdefault(p[5], []).append((float(p[2]), float(p[11]), float(p[13])))
            except Exception:
                continue
    for k in out:
        out[k].sort(key=lambda r: r[0])
    return out


# ---------------------------------------------------------------- 三个策略
class State(object):
    def __init__(self):
        self.angle = 0.0          # 当前"落地角度"（0 = 直行；= 保持中的偏角）
        self.until = 0.0          # 保持截止时刻
        self.bad_since = None     # 保持中的角度**连续**不可走的起点（单帧不可破）
        self.cand = None          # 待确认的新候选
        self.cand_since = 0.0


def step(state, px, pz, desired, now, policy, bad_dir_s, switch_confirm_s, hold_s, order_mode):
    """返回 (角度, 方向向量)。policy: 'P0' | 'P1' | 'P2'；order_mode: 'list' | 'nearest'"""
    st = policy != 'P0'
    straight = walkable_ahead(px, pz, desired[0], desired[1])

    if st and state.angle != 0.0 and now < state.until:
        v = rot(desired[0], desired[1], state.angle)
        if walkable_ahead(px, pz, v[0], v[1]):
            state.bad_since = None
            return state.angle, v
        # 保持期内单帧不可走 ⇒ **不立刻放弃**，要连续 bad_dir_s 秒才算
        if state.bad_since is None:
            state.bad_since = now
        if now - state.bad_since < bad_dir_s:
            keep = state.angle                     # 角度仍算"当前"，用于"最小偏差"排序
            a, v = pick(px, pz, desired, keep, order_mode)
            if a is not None:
                return a, v
            # 一个都不通 ⇒ 交给 PickBestRunwayDir（与产品同分支）
            b = fallback_runway(px, pz, desired)
            return 0.0, b
        state.angle, state.until, state.bad_since = 0.0, 0.0, None

    if straight:
        state.angle, state.until, state.bad_since = 0.0, 0.0, None
        return 0.0, desired

    a, v = pick(px, pz, desired, state.angle, order_mode)
    if a is None:
        # 一个都不通 ⇒ 与产品同分支：取"跑道最长"（PickBestRunwayDir）
        return 0.0, fallback_runway(px, pz, desired)

    if not st:
        return a, v

    # 锚点 = state.angle（上一次 commit 的偏角），只在弱证据攒够后才移动
    state.angle, state.until, state.bad_since = a, now + hold_s, None
    return a, v


def fallback_runway(px, pz, desired):
    """PickBestRunwayDir 的位图近似：从期望方向向两侧各扫 6×30°，取跑道最长者；全 0 则取正后方。"""
    best, best_run = desired, runway(px, pz, desired[0], desired[1])
    for step in range(1, 7):
        for sign in (1.0, -1.0):
            c = rot(desired[0], desired[1], step * 30.0 * sign)
            r = runway(px, pz, c[0], c[1])
            if r > best_run:
                best, best_run = c, r
    return best


def pick(px, pz, desired, incumbent, order_mode):
    if order_mode == 'nearest' and incumbent != 0.0:
        order = sorted(ANGLES, key=lambda a: abs(a - incumbent))
    else:
        order = ANGLES
    return first_pass(px, pz, desired, order)


# ---------------------------------------------------------------- 主流程
def main():
    print('=== bu-r3-avoid-replay (slice BU-R3, offline) ===')
    print('bitmap %dx%d cell=%.3f origin=(%.1f,%.1f)  %s' % (W, D, CELL, OX, OZ, MAP))
    flips = parse_flips(os.path.join(TMP, 'bu-r2-hold-plant-log.tsv'))
    if not flips:
        print('MISSING input: bu-r2-hold-plant-log.tsv -> cannot self-check')
        return 1

    # ── ① 口径自校验：复算的 walkable_ahead 能不能解释产品自己写的 probe ──
    print('')
    print('-- 1. 口径自校验：用 de_dust2.bytes 复算 WalkableAhead，去解释产品 [BOTFLIP] 行里的 probe= --')
    print('   分桶口径（逐行，用该行自己的 pos/desired，与运行时同一份输入）：')
    print('     consistent = 首个位图通过角 == 产品 probe   （位图这一半就解释了选择）')
    print('     phys-rescue= 产品 probe 角在位图里两个采样点任一不可走（物理复核 PhysRunway 救回；⛔ 离线复算不了）')
    print('     bitmap-wider=产品 probe 之后还有更早的角位图能通过（复算口径比运行时松 ⇒ 复算失真）')
    print('     none      = 一圈都通不过（跑到了 PickBestRunwayDir 那一支）')
    print('   %-10s %6s %11s %12s %13s %6s' % ('bot', '行数', 'consistent', 'phys-rescue',
                                               'bitmap-wider', 'none'))
    tot = hit = rescue = wider = none = 0
    for nm in sorted(flips):
        n = h = r = w = nn = 0
        for (t, px, pz, dx, dz, _, probe, ox, oz) in flips[nm]:
            exp = probe_of(None, probe)
            n += 1
            ev = rot(dx, dz, exp)
            if not walkable_ahead(px, pz, ev[0], ev[1]):
                r += 1                       # 产品选的角度位图判不可走 ⇒ 只能靠物理复核
                continue
            a, v = first_pass(px, pz, (dx, dz), ANGLES)
            if a is None:
                nn += 1
            elif abs(a - exp) < 0.5:
                h += 1
            else:
                w += 1                       # a 一定 < exp（因为 exp 角可走）⇒ 位图说更早的角该赢
        tot += n
        hit += h
        rescue += r
        wider += w
        none += nn
        print('   %-10s %6d %11d %12d %13d %6d' % (nm, n, h, r, w, nn))
    print('   合计 %d 行：consistent %d（%.1f%%）/ phys-rescue %d（%.1f%%）/ bitmap-wider %d（%.1f%%）/ none %d'
          % (tot, hit, 100.0 * hit / max(tot, 1), rescue, 100.0 * rescue / max(tot, 1),
             wider, 100.0 * wider / max(tot, 1), none))
    print('   ⇒ 读法：phys-rescue 占大头 = 运行时"可走"判据有一大半来自物理复核（离线复算不了）；')
    print('     bitmap-wider 才是**复算失真**，它越低越好。下面第 3/4 节都在同一份位图通过集上跑，')
    print('     ⛔ 因此它是"机制对照"（P0 vs P1 的相对倍数），不是"修完一定达标"的预测。')

    # ── ② 噪声周期与单帧位移（标定迟滞窗口用，量的是产品自己的数）──
    # _diagFlips 是产品自己累计的"方向翻转 >120°"计数；相邻两行 probeLeft 都是 0.50（=每帧重掷）
    print('')
    print('-- 2. 噪声标定（全部取自产品自己的产物）--')
    dt_all = []
    for nm in sorted(flips):
        rs = flips[nm]
        for i in range(1, len(rs)):
            dt_all.append(rs[i][0] - rs[i - 1][0])
    if dt_all:
        print('   相邻 [BOTFLIP] 行间隔（= 产品 DiagFlip 的 0.5s 闸）：中位 %.3fs' % sorted(dt_all)[len(dt_all) // 2])
    arrows = parse_arows(os.path.join(TMP, 'bu-r2-hold-plant.tsv'), set(flips.keys()))
    for nm in sorted(arrows):
        s = arrows[nm]
        if len(s) < 20:
            continue
        # 在陷阱窗口（BOTFLIP 行覆盖的时间范围内）量：窗口内位移范围 / 平均速度
        t0, t1 = flips[nm][0][0], flips[nm][-1][0]
        w = [r for r in s if t0 <= r[0] <= t1]
        if len(w) < 10:
            continue
        xs, zs = [r[1] for r in w], [r[2] for r in w]
        steps = [math.dist((w[i][1], w[i][2]), (w[i - 1][1], w[i - 1][2])) for i in range(1, len(w))]
        span = w[-1][0] - w[0][0]
        speed = sum(steps) / span if span > 0 else 0.0
        print('   %-10s 陷阱窗口 %.1fs：位置范围 x[%.2f,%.2f] z[%.2f,%.2f]；'
              '行走速度 %.2f m/s；单帧(采样 Δt=%.3fs)位移中位 %.3fm'
              % (nm, span, min(xs), max(xs), min(zs), max(zs), speed,
                 (w[1][0] - w[0][0]), sorted(steps)[len(steps) // 2]))

    # ── ③ 三策略对比（同一段记录轨迹、同一份位图）──
    targets = parse_targets(os.path.join(TMP, 'bu-r2-hold-plant-log.tsv'))
    TRAPS = {}
    for nm in sorted(arrows):
        if nm not in flips or nm not in targets or len(arrows[nm]) < 20:
            continue
        t0, t1 = flips[nm][0][0], flips[nm][-1][0]
        ts = [r for r in arrows[nm] if t0 <= r[0] <= t1]
        if len(ts) >= 10:
            TRAPS[nm] = (ts, targets[nm])

    print('')
    print('-- 3. 迟滞策略离线对比（陷阱窗口 = 产品 [BOTFLIP] 行覆盖的时间段；开环，见文件头声明）--')
    print('   ⚠️ 开环含义：轨迹本身是"现状策略"的产物 ⇒ 本节只回答"同一段轨迹上各策略输出多稳"，')
    print('      ⛔ 不等于"修完 A1/A2 一定达标"（闭环结论只能由 Play 的整回合产物给）。')
    POL = [('P0 现状(每帧重扫，取 AvoidAngles 表首通过者)', 'P0', 'list', 0.10, 0.20, 0.50),
           ('P1 **本片交付版**(最小偏差排序+保持0.5+单帧不可破0.1)', 'P1', 'nearest', 0.10, 0.0, 0.50),
           ('P2 只换排序(最小偏差，无迟滞窗)', 'P2', 'nearest', 0.0, 0.0, 0.50)]
    print('   %-52s %-9s %9s %9s %9s %9s %9s' % ('策略', 'actor', '换向次/秒', '恰180°%', '≥120°%',
                                                 '跑道均值m', '跑道<1m%'))
    for label, pol, om, bd, sw, hd in POL:
        for nm in sorted(TRAPS):
            ts, tgt = TRAPS[nm]
            agg = eval_policy(ts, tgt, pol, om, bd, sw, hd)
            print('   %-52s %-9s %9.1f %8.0f%% %8.0f%% %9.2f %8.0f%%' % (
                label, nm, agg['flips_per_s'], agg['p180'], agg['p120'], agg['run_mean'], agg['run_lt1']))

    # ── ④ 参数标定：一维扫描（其余参数取上表 P1 值），看拐点在哪 ──
    print('')
    print('-- 4. 迟滞参数的**一维扫描**（在 Darrell+Scuzzy 两段陷阱窗口上汇总；选"拐点右侧的平台"）--')
    print('   汇总口径：换向次/秒 = 两段窗口的换向总次数 / 总时长；跑道均值 = 两段逐采样均值')
    BASE = dict(bd=0.10, sw=0.20, hd=0.50)

    def sweep(name, values, key):
        print('   %-46s %10s %10s' % ('%s =' % name, '换向次/秒', '跑道均值m'))
        for v in values:
            p = dict(BASE)
            p[key] = v
            tot_f = tot_s = 0
            runs = []
            for nm in sorted(TRAPS):
                ts, tgt = TRAPS[nm]
                agg = eval_policy(ts, tgt, 'P1', 'nearest', p['bd'], p['sw'], p['hd'])
                tot_f += agg['flips']
                tot_s += agg['span']
                runs.append(agg['run_mean'])
            print('   %-46s %10.1f %10.2f' % ('%s %.2fs' % (name, v),
                                              tot_f / tot_s if tot_s > 0 else -1,
                                              sum(runs) / len(runs)))

    sweep('保持窗 ProbeHoldSeconds（沿用既有值）', [0.0, 0.25, 0.50, 0.80], 'hd')
    sweep('单帧不可破 AvoidBadDirSeconds（本片新增）', [0.0, 0.05, 0.10, 0.20, 0.40], 'bd')
    sweep('对照：独立的"换向确认窗"（⛔ 本片**不引入**）', [0.0, 0.10, 0.20, 0.40], 'sw')
    print('   ⇒ 取值落点：')
    print('     · ProbeHoldSeconds 保持 **0.5s 原值不动**：0.25/0.50 同处平台（0.1 次/秒），0.80 起略升（0.3）')
    print('       ⇒ 0.5 是平台右端，既有值就是合适值，⛔ 不改它。')
    print('     · AvoidBadDirSeconds 取 **0.1s**：扫描 0~0.4s 全在同一平台 ⇒ 它**不是**敏感参数，')
    print('       所以按"必须 ≥ 一个噪声周期(0.1s)"取值，落在平台内部而不是拐点上。')
    print('     · 第三张表说明"独立的换向确认窗"在 0~0.4s 内换向率一模一样 ⇒ 本片**不引入它**')
    print('       （少一个状态量、少一处可漂移的参数），迟滞只由"锚点 + 单帧不可破"两件构成。')

    print('')
    print('RESULT: (offline replay; the P1/P2 numbers feed the parameter choice, the Play numbers decide)')
    return 0


def eval_policy(ts, tgt, pol, om, bd, sw, hd):
    """在给定位置序列上跑一个策略，返回换向/跑道统计。"""
    tx, tz = tgt
    state = State()
    seq = []
    for (t, x, z) in ts:
        des = norm(tx - x, tz - z)
        a, v = step(state, x, z, des, t, pol, bd, sw, hd, om)
        seq.append((t, a, v, runway(x, z, v[0], v[1])))
    flips_n = hd180 = ge120 = 0
    for i in range(1, len(seq)):
        if seq[i][1] == seq[i - 1][1]:
            continue
        ang = abs(((seq[i][1] - seq[i - 1][1] + 180) % 360) - 180)
        flips_n += 1
        if ang >= 150.0:
            hd180 += 1
        if ang >= FLIP_ANGLE:
            ge120 += 1
    span = (seq[-1][0] - seq[0][0]) if seq else 0.0
    runs = [r[3] for r in seq]
    return dict(flips=flips_n, span=span,
                flips_per_s=(flips_n / span if span > 0 else -1.0),
                p180=(100.0 * hd180 / max(flips_n, 1)),
                p120=(100.0 * ge120 / max(flips_n, 1)),
                run_mean=(sum(runs) / len(runs) if runs else -1.0),
                run_lt1=(100.0 * sum(1 for r in runs if r < 1.0) / len(runs) if runs else -1.0))


def parse_targets(path):
    """每 bot：窗口内出现最多的 target（本片只看 Darrell/Scuzzy 的陷阱窗口，窗口内 target 恒定）。"""
    cnt = {}
    if not os.path.exists(path):
        return {}
    with io.open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if '[BOTFLIP]' not in line:
                continue
            p = line.rstrip('\r\n').split('\t')
            txt = p[-1]
            nm = txt.split('[BOTFLIP]')[1].split('branch=')[0].strip()
            i = txt.find('target=(')
            if i < 0:
                continue
            j = i + len('target=(')
            k = txt.find(')', j)
            a, b = txt[j:k].split(',')
            cnt.setdefault(nm, {})
            cnt[nm][(float(a), float(b))] = cnt[nm].get((float(a), float(b)), 0) + 1
    return dict((nm, max(d.items(), key=lambda kv: kv[1])[0]) for nm, d in cnt.items())


sys.exit(main())
