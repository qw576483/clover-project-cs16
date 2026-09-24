#!/usr/bin/env python
# -*- coding: utf-8 -*-
# ============================================================================
#
#  为什么需要它：
#    已有脚本（analyze-bot-phys.py / analyze-bot-goal.py）只报汇总数字，**判不出抖动来自哪一层**。
#    本脚本把两件产品自己写的 L3 产物按"每一帧在干什么"摊开，用数字回答三问：
#      ① 人真的在动吗？（逐采样步长 / 静止窗口 / 最大步 = 是否发生了传送/重开）
#      ② 动得"有方向"吗？（相邻步的夹角 >120° 的比例 = rev%）
#      ③ 抖动来自哪一层？（rwp 游标 / goal 是否在翻 / state 是否在翻 / 探针 reason 分布）
#
#  输入（产品 L3 产物，不读画面）：
#    B 行  <项目根>/.ai-tmp/test/<probe>-bot-phys.tsv   （bot-phys.cs，order=201，每 15 帧一采样）
#      列：0=B 1=frame 2=t 3=Name 4=Id 5=Team 6=state 7..9=pos 10..12=goal 13=hasGoal
#          14=distXZ 15=goalDy 16=canStand 17=groundY 18=normalY 19=pos-ground 20=dirsMovable
#          21=goalStepLen 22=goalStepLenYFollow 23..26=want* 27=reason 28=routeEndY
#          29=rwp(剩余路点数) 30=planRoute 31=holdSite 32=holdSlot
#    A 行  <项目根>/.ai-tmp/test/<probe>-hold-plant.tsv （bot-hold-plant.cs，每 3 帧一采样）
#      列：0=A 1=frame 2=t 3=round 4=phase 5=Name 6=Id 7=Team 8=IsBot 9=IsAlive 10=Health
#          11..13=pos 14=Yaw 15=HasBomb 16=UseProgress 17=dA 18=dB 19=siteLabel 20=planted
#
#  用法：
#    python tools/probes/bu-r2-motion-diag.py
#    python tools/probes/bu-r2-motion-diag.py --phys ..\..\.ai-tmp\test\bu-r-bot-phys.tsv --rows ...\bu-r-hold-plant.tsv
# ============================================================================
import io
import math
import os
import sys

try:
    # 与其它探针脚本同口径：把 stdout 固定成 UTF-8，否则重定向到文件时 GBK 控制台会抛
    # UnicodeEncodeError（'-'/'⇒' 这类字符），整段输出丢掉（实测代价：本次第 5 节整节丢失）。
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TMP = os.path.join(PROJECT, '.ai-tmp', 'test')

REV_ANGLE = 120.0      # 相邻两步夹角 > 120° 记一次"掉头"
STALL_WIN = 5.0        # 静止窗口（秒）：窗口内位移 < STALL_EPS 记一段静止
STALL_EPS = 1.0
TELEPORT_M = 3.0       # 单步 > 3m 视为"非行走位移"（重开/传送），单独列出


def F(v, d=1):
    return ('%.' + str(d) + 'f') % v


def parse(path, tag):
    rows = []
    if not os.path.exists(path):
        print('MISSING input: %s' % path)
        return rows
    with io.open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.rstrip('\n').rstrip('\r').split('\t')
            if p and p[0] == tag:
                rows.append(p)
    return rows


def dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def angle_between(u, v):
    nu = math.hypot(u[0], u[1])
    nv = math.hypot(v[0], v[1])
    if nu < 1e-6 or nv < 1e-6:
        return None
    c = (u[0] * v[0] + u[1] * v[1]) / (nu * nv)
    c = max(-1.0, min(1.0, c))
    return math.degrees(math.acos(c))


def series_from_B(rows):
    """每 actor：[(t,x,z,state,rwp,goalx,goalz,distXZ,movable,reason)]（按 t 升序）"""
    out = {}
    for p in rows:
        if len(p) < 30:
            continue
        try:
            rec = (float(p[2]), float(p[7]), float(p[9]), p[6], int(float(p[29])),
                   float(p[10]), float(p[12]), float(p[14]), int(float(p[20])), p[27])
        except Exception:
            continue
        out.setdefault(p[3], []).append(rec)
    for k in out:
        out[k].sort(key=lambda r: r[0])
    return out


def series_from_A(rows):
    out = {}
    for p in rows:
        if len(p) < 20 or p[9] != '1':
            continue
        try:
            # (t, x, z, frame, round, phase) —— frame 必须留着：第 5 节的"翻转周期(帧)"要用它
            rec = (float(p[2]), float(p[11]), float(p[13]), float(p[1]), p[3], p[4])
        except Exception:
            continue
        out.setdefault(p[5], []).append(rec)
    for k in out:
        out[k].sort(key=lambda r: r[0])
    return out


def walk_stats(s, idx_x=1, idx_z=2):
    """s = [(t,x,z,...)]；返回 (总路径, 净位移, 掉头比例, 单步统计)"""
    total = 0.0
    teleports = []
    steps = []          # (i, len, dx, dz)
    for i in range(1, len(s)):
        dx = s[i][idx_x] - s[i - 1][idx_x]
        dz = s[i][idx_z] - s[i - 1][idx_z]
        L = math.hypot(dx, dz)
        if L > TELEPORT_M:
            teleports.append((s[i - 1][0], s[i][0], L))
            continue
        total += L
        steps.append((i, L, dx, dz))
    net = dist((s[0][idx_x], s[0][idx_z]), (s[-1][idx_x], s[-1][idx_z])) if s else 0.0
    rev = 0
    cmp_n = 0
    for k in range(1, len(steps)):
        a = angle_between((steps[k - 1][2], steps[k - 1][3]), (steps[k][2], steps[k][3]))
        if a is None:
            continue
        cmp_n += 1
        if a > REV_ANGLE:
            rev += 1
    return total, net, (rev, cmp_n), steps, teleports


def stall_runs(s, idx_x=1, idx_z=2):
    """滑动窗口静止段：返回 [(t0, t1, 时长, 窗口内位移)]"""
    runs = []
    n = len(s)
    i = 0
    while i < n:
        j = i
        while j + 1 < n and s[j + 1][0] - s[i][0] < STALL_WIN:
            j += 1
        if j > i and s[j][0] - s[i][0] >= STALL_WIN * 0.8:
            d = dist((s[i][idx_x], s[i][idx_z]), (s[j][idx_x], s[j][idx_z]))
            if d < STALL_EPS:
                t0 = s[i][0]
                # 尽可能往后延伸
                k = j
                while k + 1 < n:
                    d2 = dist((s[i][idx_x], s[i][idx_z]), (s[k + 1][idx_x], s[k + 1][idx_z]))
                    if d2 < STALL_EPS:
                        k += 1
                    else:
                        break
                runs.append((t0, s[k][0], s[k][0] - t0, d))
                i = k + 1
                continue
        i += 1
    return runs


def main():
    physp = os.path.join(TMP, 'bu-r-bot-phys.tsv')
    rowp = os.path.join(TMP, 'bu-r-hold-plant.tsv')
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--phys' and i + 1 < len(args):
            physp = args[i + 1]
        if a == '--rows' and i + 1 < len(args):
            rowp = args[i + 1]

    print('=== bu-r2-motion-diag (slice BU-R2) ===')
    print('phys : %s' % physp)
    print('rows : %s' % rowp)
    B = parse(physp, 'B')
    A = parse(rowp, 'A')
    print('B rows = %d   A rows = %d' % (len(B), len(A)))
    if not B and not A:
        print('RESULT: FAIL (no input)')
        return 1

    sb = series_from_B(B)
    sa = series_from_A(A)

    print('')
    print('-- 1. 每 actor 的行走统计（B 行 / bot-phys，产品自己写）--')
    print('  %-10s %6s %8s %7s %8s %8s %8s %9s' % ('actor', 'n', 'span-s', 'total-m', 'net-m', 'net/tot',
                                                   'rev%', 'stall-s'))
    for nm in sorted(sb.keys()):
        s = sb[nm]
        total, net, (rev, cmp_n), steps, tp = walk_stats(s)
        span = s[-1][0] - s[0][0]
        runs = stall_runs(s)
        stallsum = sum(r[2] for r in runs)
        print('  %-10s %6d %8.1f %7.1f %8.1f %8.3f %7.1f%% %9.1f' % (
            nm, len(s), span, total, net, (net / total if total > 0 else -1),
            100.0 * rev / cmp_n if cmp_n else -1, stallsum))
        for r in runs:
            if r[2] >= 8.0:
                print('       stall  t=%s..%s (%ss, 位移 %sm)' % (F(r[0]), F(r[1]), F(r[2]), F(r[3], 2)))
        for t0, t1, L in tp:
            print('       JUMP   t=%s..%s  单步 %sm（非行走位移：重开/传送）' % (F(t0), F(t1), F(L, 1)))

    print('')
    print('-- 2. 抖动源头：state / rwp / goal 在候选窗口里的翻动次数（B 行）--')
    print('  %-10s %-22s %8s %8s %8s %10s' % ('actor', 'state 分布', 'state翻', 'rwp翻', 'goal翻', 'distXZ缩'))
    for nm in sorted(sb.keys()):
        s = sb[nm]
        states = {}
        st_flip = rwp_flip = goal_flip = 0
        for i, r in enumerate(s):
            states[r[3]] = states.get(r[3], 0) + 1
            if i:
                if s[i][3] != s[i - 1][3]:
                    st_flip += 1
                if s[i][4] != s[i - 1][4]:
                    rwp_flip += 1
                if abs(s[i][5] - s[i - 1][5]) > 0.5 or abs(s[i][6] - s[i - 1][6]) > 0.5:
                    goal_flip += 1
        d0, d1 = s[0][7], s[-1][7]
        print('  %-10s %-22s %8d %8d %8d   %s -> %s' % (
            nm, ','.join('%s:%d' % kv for kv in sorted(states.items(), key=lambda kv: -kv[1]))[:22],
            st_flip, rwp_flip, goal_flip, F(d0), F(d1)))

    print('')
    print('-- 3. 逐 actor 的"目标/游标/距离"轨迹变化点（只列变化行）--')
    for nm in sorted(sb.keys()):
        s = sb[nm]
        lines = 0
        prev = None
        for r in s:
            key = (r[3], r[4], round(r[5], 1), round(r[6], 1))
            if key != prev:
                lines += 1
                prev = key
        print('  %-10s 变化点 %d 个 / %d 采样' % (nm, lines, len(s)))
        shown = 0
        prev = None
        for r in s:
            key = (r[3], r[4], round(r[5], 1), round(r[6], 1))
            if key != prev:
                prev = key
                if shown < 14:
                    print('       t=%7s state=%-10s rwp=%-4d goal=(%s,%s) distXZ=%s movable=%d reason=%s' % (
                        F(r[0]), r[3], r[4], F(r[5]), F(r[6]), F(r[7]), r[8], r[9]))
                    shown += 1

    print('')
    print('-- 4. A 行（bot-hold-plant，回合口径）每 actor：断点与静止段 --')
    print('  %-10s %6s %8s %7s %8s %8s %7s' % ('actor', 'n', 'span-s', 'total-m', 'net-m', 'net/tot', 'rev%'))
    for nm in sorted(sa.keys()):
        s = sa[nm]
        total, net, (rev, cmp_n), steps, tp = walk_stats(s)
        print('  %-10s %6d %8.1f %7.1f %8.1f %8.3f %6.1f%%' % (
            nm, len(s), s[-1][0] - s[0][0], total, net, (net / total if total > 0 else -1),
            100.0 * rev / cmp_n if cmp_n else -1))
        for t0, t1, L in tp:
            print('       JUMP t=%s..%s 单步 %sm' % (F(t0), F(t1), F(L, 1)))
        for r in stall_runs(s):
            if r[2] >= 8.0:
                print('       stall  t=%s..%s (%ss, 位移 %sm)' % (F(r[0]), F(r[1]), F(r[2]), F(r[3], 2)))

    print('')
    print('-- 5. 方向翻转的量化（A 行 / bot-hold-plant，3 帧一采样 ⇒ 够看清 ~30 帧的翻转）--')
    print('  %-10s %8s %10s %10s %10s %10s' % ('actor', 'fps', '翻转次数', '平均周期帧', '平均周期s', '相邻段夹角180°占比'))
    for nm in sorted(sa.keys()):
        s = sa[nm]
        seq = []
        for i in range(1, len(s)):
            seq.append((s[i][3], s[i][0], s[i][1] - s[i - 1][1], s[i][2] - s[i - 1][2]))
        if len(seq) < 10:
            continue
        fps = (float(seq[-1][0]) - float(seq[0][0])) / (seq[-1][1] - seq[0][1]) if seq[-1][1] > seq[0][1] else -1
        segs = []
        cur = []
        lastang = None
        for f, t, dx, dz in seq:
            L = math.hypot(dx, dz)
            if L < 0.01:
                continue
            a = math.degrees(math.atan2(dz, dx))
            if lastang is None or abs(((a - lastang + 180) % 360) - 180) > 60:
                if cur:
                    segs.append(cur)
                cur = []
            cur.append((int(float(f)), a))
            lastang = a
        if cur:
            segs.append(cur)
        if len(segs) < 3:
            continue
        spans = [segs[i][0][0] - segs[i - 1][0][0] for i in range(1, len(segs))]
        meanang = [sum(x[1] for x in sg) / len(sg) for sg in segs]
        dd = [abs(((meanang[i] - meanang[i - 1] + 180) % 360) - 180) for i in range(1, len(segs))]
        p180 = 100.0 * sum(1 for x in dd if x >= 175) / len(dd)
        print('  %-10s %8.1f %10d %10.1f %10.3f %9.1f%%' % (
            nm, fps, len(segs) - 1, sum(spans) / len(spans),
            (sum(spans) / len(spans)) / fps if fps > 0 else -1, p180))

    print('')
    print('RESULT: (offline diagnostic; no threshold -- numbers feed the fix)')
    return 0


sys.exit(main())
