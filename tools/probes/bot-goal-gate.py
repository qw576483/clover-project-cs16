#!/usr/bin/env python
# -*- coding: utf-8 -*-
# ============================================================================
#  判据资产 - 切片 BU-R：**bot 目标决策的验收闸门**（同一批断言，修前 / 修后各跑一次）
#
#  为什么需要它（本片根因）：
#    片BS 的分析脚本（analyze-bot-goal.py / analyze-hold-plant.py）只**报数字**，没有阈值 ——
#    于是"修完了没有"要靠读报告的人自己拍板。本脚本把验收表里那几行写成**可机械判红的断言**：
#       A1 每个 bot 的净位移 ≥ 阈值              （不许"站着不动 / 原地踱步"）
#       A2 每个 bot 的 net/total ≥ 阈值          （推进要**有方向**，不是来回蹭）
#       A3 换目标次数 ≤ 阈值（全体 + 单机）      （刹住"stuck-escalate 风暴"）
#       A4 求路径失败 ≤ 阈值                     （"可走但走不到"的选目标必须已被门禁挡住）
#       A5 T 侧真的把 C4 下在包点里              （TPLANTED / '在包点' L3 行）
#       A6 CT 侧真的在包点里驻留                 （到最近包点标记 ≤ SiteRadius 的连续采样）
#       A7 守点换位 ≥ 阈值                       （守位表建好了还必须**真的换位**）
#       A8 三档难度参数逐字段对照规格             （Easy/Normal/Hard 的反应时间与瞄准误差）
#
#  输入（全部是**产品自己写的** L3 产物，⛔ 不读画面文本）：
#    <项目根>/.ai-tmp/test/<产品>-hold-plant-log.tsv : frame TAB t TAB level TAB text
#    <项目根>/.ai-tmp/test/<产品>-hold-plant.tsv     : A 行（每 3 帧一采样）
#    <项目根>/client/Assets/Scripts/Module/Match/CsTypes.cs  : 三档参数真源（静态检查 A8）
#
#  用法：
#    python tools/probes/bot-goal-gate.py                                  # 默认判 bu-r 产品
#    python tools/probes/bot-goal-gate.py --log .. --rows ..               # 判别的 Play 产物
#
#  判据出处（⛔ 阈值不许"看起来合理"）：
#    * 净位移 / net-total 的口径 = tools/probes/analyze-bot-goal.py 第 4 节（同一份 A 行口径）。
#    * SiteRadius / 包点判定 = Module/Map/ICsMap.cs:83 的 CsMarkers.BombsiteRadius = 7m
#      （与 CsBomb.IsInBombsite 同口径）。
#    * 三档参数真源 = 策划/策划案/CS1.6单机参考规格.md:117-131 的 Easy 0.5~0.8s/±6°、
#      Normal 0.25~0.4s/±3°、Hard 0.1~0.2s/±1.2°，落在 CsTypes.cs 的 CsBotProfile.For。
# ============================================================================
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TMP = os.path.join(PROJECT, '.ai-tmp', 'test')

BOMBSITE_RADIUS = 7.0        # Module/Map/ICsMap.cs:83
DWELL_SAMPLES = 5            # "驻留" = 连续 5 个采样点都在包点内（20Hz/每3帧 ≈ 每 0.15s 一点）

# 验收阈值
MIN_NET_METERS = 15.0        # A1：一个整回合里，bot 的净位移至少 15m（不到 = 没在推进）
MIN_NET_OVER_TOTAL = 0.15    # A2：推进要有方向

# ---- 片BU-R4：A1/A2 的**有条件**口径 + 新增 A9（⛔ 上面两个阈值一个字都没动）----
# 为什么改：A1/A2 的原口径等于"所有 bot 都该推进"，对**守点/驻留**型 bot 是错的 ——
#   守得对就该静止（A6/A7 已证明"CT 在包点内驻留 / 守点换位"是正确行为）。
# 改法（只加"合格静止"这一个出口，且出口的证据与 A6 **同一列**、⛔ 不新增量法）：
#   一个 bot 的采样里，凡是属于"连续 >= DWELL_SAMPLES 个采样落在包点 BOMBSITE_RADIUS 内"的采样，
#   记为**合格驻留采样**（判据列 = 产品自己写的 A 行 dA/dB）；
#   当合格驻留采样 >= DWELL_SAMPLES 且占该 bot 存活采样 >= DWELL_QUAL_MIN_RATIO 时，
#   A1/A2 对**这个 bot** 记通过（= 它的"不动"由合格驻留解释）。
#   ⛔ 没有这份证据的静止（例如"持包 bot 全程 0 位移"）照旧判红 —— 由 A9 直接点名。
DWELL_QUAL_MIN_RATIO = 0.5
# A9：持包 bot 在 Live 阶段连续静止 > PLANT_STILL_SECONDS 而全场一次都没下包 ⇒ FAIL。
#     片BU-R4 的直接病灶：T 持包者 Move 恒 0（intent 按值传递丢失）⇒ 站整回合、永不下包。
PLANT_STILL_SECONDS = 10.0
STILL_EPS_METERS = 0.15

MAX_REPLANS_TOTAL = 20       # A3：全体换目标次数
MAX_REPLANS_PER_ACTOR = 12   # A3
MAX_PATH_FAILS = 10          # A4
MIN_HOLD_SWAPS = 1           # A7

# A8：规格三档（下界, 上界）——反应时间秒 / 瞄准误差度
SPEC_REACTION = {'Easy': (0.5, 0.8), 'Normal': (0.25, 0.4), 'Hard': (0.1, 0.2)}
SPEC_AIMERR = {'Easy': (6.0, 6.0), 'Normal': (3.0, 3.0), 'Hard': (1.2, 1.2)}

try:
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
except Exception:
    pass

fail = 0
checks = []


def check(name, ok, detail):
    global fail
    if not ok:
        fail += 1
    checks.append((name, ok, detail))
    print('  %-4s %-34s %s' % ('PASS' if ok else 'FAIL', name, detail))


def parse_log(path):
    rows = []
    with io.open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.rstrip('\n').rstrip('\r').split('\t')
            if len(p) >= 4:
                rows.append((p[1], p[2], p[3], p[4] if len(p) > 4 else ''))
    return rows


def parse_rows(path):
    out = []
    with io.open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            p = line.rstrip('\n').rstrip('\r').split('\t')
            if p and p[0] == 'A':
                out.append(p)
    return out


def qualified_dwell_samples(rows, nm):
    """片BU-R4：A1/A2 的"合格驻留"证据（与 A6 同一列 dA/dB ⇒ ⛔ 不新增量法）。

    返回 (合格驻留采样数, 该 bot 存活采样数)。合格 = 属于"连续 >= DWELL_SAMPLES 个采样
    都落在包点 BOMBSITE_RADIUS 内"的那一段（产品自己写的 A 行给出的距离）。
    """
    alive = 0
    flags = []
    for p in rows:
        if p[5] != nm or p[9] != '1':
            continue
        alive += 1
        try:
            d = min(float(p[17]), float(p[18]))
        except Exception:
            flags.append(False)
            continue
        flags.append(d <= BOMBSITE_RADIUS)
    run = 0
    rruns = [0] * len(flags)
    for i, f in enumerate(flags):
        run = run + 1 if f else 0
        rruns[i] = run
    # 反向把"段尾之前的点"也补进同一段（只有 >= DWELL_SAMPLES 的段才算合格）
    ok = [False] * len(flags)
    i = 0
    while i < len(flags):
        if rruns[i] >= DWELL_SAMPLES:
            j = i
            while j + 1 < len(flags) and rruns[j + 1] > rruns[j]:
                j += 1
            if (j - i + 1) >= DWELL_SAMPLES:
                for k in range(i, j + 1):
                    ok[k] = True
            i = j + 1
        else:
            i += 1
    return sum(1 for x in ok if x), alive


def plant_still_runs(rows, nm):
    """片BU-R4 · A9 的量法：持包 bot 在 Live 阶段"连续静止 > PLANT_STILL_SECONDS"的段。

    输入 = 产品自己写的 A 行（HasBomb=1 / IsAlive=1 / phase=Live），位置列 11..13。
    返回 [(t0, t1, 时长秒)]，只列 >= PLANT_STILL_SECONDS 的段。
    """
    pts = []
    for p in rows:
        if p[5] != nm or p[15] != '1' or p[9] != '1' or p[4] != 'Live':
            continue
        try:
            pts.append((float(p[2]), float(p[11]), float(p[13])))
        except Exception:
            continue
    out = []
    i = 0
    while i < len(pts):
        j = i
        while j + 1 < len(pts):
            d = ((pts[j + 1][1] - pts[i][1]) ** 2 + (pts[j + 1][2] - pts[i][2]) ** 2) ** 0.5
            if d >= STILL_EPS_METERS:
                break
            j += 1
        dur = pts[j][0] - pts[i][0]
        if dur >= PLANT_STILL_SECONDS:
            out.append((pts[i][0], pts[j][0], dur))
        i = j + 1
    return out


def read_profiles():
    """从 CsTypes.cs 的 CsBotProfile.For 抽三档参数（纯文本解析，⛔ 不执行 C#）。"""
    src = os.path.join(PROJECT, 'client', 'Assets', 'Scripts', 'Module', 'Match', 'CsTypes.cs')
    txt = io.open(src, 'r', encoding='utf-8', errors='replace').read()
    i = txt.find('CsBotProfile For(')
    if i < 0:
        return None
    seg = txt[i:i + 4000]
    out = {}
    cur = None
    for line in seg.splitlines():
        ls = line.strip()
        if ls.startswith('case CsBotDifficulty.'):
            cur = ls.split('.')[1].split(':')[0].strip()
            out[cur] = {}
        elif 'default:' in ls:
            cur = 'Normal'
            out.setdefault(cur, {})
        elif cur and 'ReactionTime =' in ls:
            out[cur]['reaction'] = float(ls.split('=')[1].split('f')[0])
        elif cur and 'AimErrorDegrees =' in ls:
            out[cur]['aimerr'] = float(ls.split('=')[1].split('f')[0])
    return out


def main():
    logp = os.path.join(TMP, 'bu-r-hold-plant-log.tsv')
    rowp = os.path.join(TMP, 'bu-r-hold-plant.tsv')
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--log' and i + 1 < len(args):
            logp = args[i + 1]
        if a == '--rows' and i + 1 < len(args):
            rowp = args[i + 1]

    print('=== bot-goal-gate (slice BU-R) ===')
    print('log  : %s' % logp)
    print('rows : %s' % rowp)
    if not os.path.exists(logp) or not os.path.exists(rowp):
        print('MISSING input -> RESULT: FAIL (input absent)')
        return 1

    log = parse_log(logp)
    rows = parse_rows(rowp)
    print('L3 lines = %d   A rows = %d' % (len(log), len(rows)))

    # ---- 位置时间序列（与 analyze-bot-goal.py 第 4 节同口径：只算相邻采样、只算活着的行）----
    series = {}
    order = []
    for p in rows:
        nm = p[5]
        if p[9] != '1':
            continue
        if nm not in series:
            series[nm] = []
            order.append(nm)
        try:
            series[nm].append((float(p[2]), float(p[11]), float(p[13])))
        except Exception:
            continue

    print('')
    print('-- A1/A2 net displacement per bot (one whole round) [BU-R4 有条件口径] --')
    print('  %-10s %-6s %8s %8s %8s %9s %8s' % ('actor', 'team', 'total-m', 'net-m', 'net/tot',
                                               'dwell%', 'ok-by'))
    team_of = {}
    for p in rows:
        team_of.setdefault(p[5], p[7])
    bots = [nm for nm in order if nm != 'Player']
    n_ok_move = 0
    n_ok_dwell = 0
    for nm in bots:
        s = series[nm]
        total = 0.0
        for i in range(1, len(s)):
            total += ((s[i][1] - s[i - 1][1]) ** 2 + (s[i][2] - s[i - 1][2]) ** 2) ** 0.5
        net = ((s[-1][1] - s[0][1]) ** 2 + (s[-1][2] - s[0][2]) ** 2) ** 0.5
        ratio = (net / total) if total > 0 else -1.0
        ok_move = net >= MIN_NET_METERS and ratio >= MIN_NET_OVER_TOTAL
        # 片BU-R4：合格驻留出口（证据 = A 行 dA/dB，与 A6 同一列；⛔ 两个阈值未动）
        dw, alive_n = qualified_dwell_samples(rows, nm)
        dw_ratio = (float(dw) / alive_n) if alive_n > 0 else 0.0
        ok_dwell = (dw >= DWELL_SAMPLES) and (dw_ratio >= DWELL_QUAL_MIN_RATIO)
        ok = ok_move or ok_dwell
        if ok_move:
            n_ok_move += 1
        if (not ok_move) and ok_dwell:
            n_ok_dwell += 1
        by = 'net' if ok_move else ('dwell' if ok_dwell else '-')
        print('  %-10s %-6s %8.1f %8.1f %8.3f %8.3f %8s' % (nm, team_of.get(nm, '?'), total, net, ratio,
                                                            dw_ratio, by))
    n_ok = n_ok_move + n_ok_dwell
    check('A1/A2 每个 bot 真的在推进（或合格驻留）', n_ok == len(bots),
          '%d/%d bots 合格（净位移>=%.0fm 且 net/total>=%.2f 的 %d 个 + 合格驻留 %d 个）'
          '；驻留出口的证据 = A 行 dA/dB 连续>=%d 采样在包点 %.0fm 内且占比>=%.2f'
          % (n_ok, len(bots), MIN_NET_METERS, MIN_NET_OVER_TOTAL, n_ok_move, n_ok_dwell,
             DWELL_SAMPLES, BOMBSITE_RADIUS, DWELL_QUAL_MIN_RATIO))

    # ---- A3 换目标次数（L3：'重新选目标'）----
    replans = {}
    for r in log:
        if '重新选目标' in r[2]:
            for nm in order:
                if ('%s（' % nm) in r[2] or ('%s ' % nm) in r[2][:12]:
                    replans[nm] = replans.get(nm, 0) + 1
                    break
    tot = sum(replans.values())
    worst = max(replans.values()) if replans else 0
    check('A3 换目标次数（全体/单机）', tot <= MAX_REPLANS_TOTAL and worst <= MAX_REPLANS_PER_ACTOR,
          '总 %d（<=%d）/ 最大单机 %d（<=%d）；分布=%s' % (tot, MAX_REPLANS_TOTAL, worst,
                                                        MAX_REPLANS_PER_ACTOR, replans if replans else '{}'))

    # ---- A4 求路径失败（引擎 badgoal/nopath + 业务侧"不连通"）----
    pf = 0
    for r in log:
        if ('终点不可走' in r[2]) or ('无可达路径' in r[2]) or ('与当前位置不连通' in r[2]):
            pf += 1
    check('A4 求路径失败次数', pf <= MAX_PATH_FAILS,
          '%d（<=%d）；两类必须分开看：终点不可走=高度一致性层拒绝、无可达路径=真位图孤岛' % (pf, MAX_PATH_FAILS))

    # ---- A5 T 侧下包 ----
    # ★ 片BU-R6 口径补全（⛔ 只补"产品自己写的下包行"，不放松任何阈值）：
    #   旧口径只认两种形态 ——
    #     ① `TPLANTED`：那是**探针写进 rows 流**（`<产品>-hold-plant.tsv` 的 `E TPLANTED`）的 token，
    #        而本项只扫 `log` 流（`<产品>-hold-plant-log.tsv`）⇒ **这一半永远不可能命中**；
    #     ② `在包点…下包决策`：AI 侧那条日志受 `CsBotConst.StateLogMinInterval` 速率限制，
    #        被前一条（`冲向包点`）吃掉时不会出现。
    #   实测（片BU-R6）：round1 产品真的下了包（rows 里 `E TPLANTED round=1`、
    #   log 里 `★ Gooseman 安放 C4 于 (-23.75, 0.00, 26.84)（35s 倒计时开始）`），而旧口径报 **0 条**。
    #   补全的第三条形态 = 产品自己的下包 L3：`CsBomb` 的 `开始安放 C4` / `★ …安放 C4 于 …` /
    #   `成功安放 C4`（`Module/Match/CsBomb.cs`）。
    #   ⛔ 两次自检：已知正确样本仍 PASS、已知错误样本（`br` / `bu-r4-prefix` 产物）仍 FAIL（它们 `安放 C4` = 0）。
    planted = (sum(1 for r in log if 'TPLANTED' in r[2])
               + sum(1 for r in log if '在包点' in r[2] and '下包决策' in r[2])
               + sum(1 for r in log if '安放 C4' in r[2]))
    check('A5 T 侧在包点内下包', planted >= 1,
          'L3 证据 %d 条（TPLANTED / “在包点 X 内…下包决策” / “安放 C4”）' % planted)

    # ---- A6 CT 侧包点驻留 ----
    dwell_actor = None
    for nm in order:
        s = series[nm]
        run = 0
        for p in rows:
            if p[5] != nm or p[9] != '1':
                continue
            try:
                d = min(float(p[17]), float(p[18]))
            except Exception:
                continue
            run = run + 1 if d <= BOMBSITE_RADIUS else 0
            if run >= DWELL_SAMPLES and team_of.get(nm) == 'CT':
                dwell_actor = nm
                break
        if dwell_actor:
            break
    check('A6 CT 在包点内驻留', dwell_actor is not None,
          ('%s 连续 %d 个采样在包点内（<=%.0fm）' % (dwell_actor, DWELL_SAMPLES, BOMBSITE_RADIUS))
          if dwell_actor else '没有任何 CT 连续 %d 个采样落在包点 %.0fm 内' % (DWELL_SAMPLES, BOMBSITE_RADIUS))

    # ---- A7 守点换位 ----
    swaps = sum(1 for r in log if '守点换位' in r[2]) + sum(1 for r in log if r[2].strip() == 'E\tSWAP')
    check('A7 守点换位次数', swaps >= MIN_HOLD_SWAPS, '%d（>=%d）' % (swaps, MIN_HOLD_SWAPS))

    # ---- A8 三档参数对照规格 ----
    prof = read_profiles()
    if not prof:
        check('A8 三档难度参数对照规格', False, '读不到 CsBotProfile.For（CsTypes.cs 结构变了？）')
    else:
        bad = []
        for d in ('Easy', 'Normal', 'Hard'):
            if d not in prof:
                bad.append(d + ':missing')
                continue
            lo, hi = SPEC_REACTION[d]
            if not (lo - 1e-6 <= prof[d].get('reaction', -1) <= hi + 1e-6):
                bad.append('%s reaction=%s not in [%s,%s]' % (d, prof[d].get('reaction'), lo, hi))
            lo, hi = SPEC_AIMERR[d]
            if not (lo - 1e-6 <= prof[d].get('aimerr', -1) <= hi + 1e-6):
                bad.append('%s aimerr=%s != %s' % (d, prof[d].get('aimerr'), lo))
        check('A8 三档难度参数对照规格', not bad,
              'Easy r=%s a=%s / Normal r=%s a=%s / Hard r=%s a=%s（规格 0.5~0.8/±6、0.25~0.4/±3、0.1~0.2/±1.2）'
              % (prof.get('Easy', {}).get('reaction'), prof.get('Easy', {}).get('aimerr'),
                 prof.get('Normal', {}).get('reaction'), prof.get('Normal', {}).get('aimerr'),
                 prof.get('Hard', {}).get('reaction'), prof.get('Hard', {}).get('aimerr'))
              + ('' if not bad else ' | ' + '; '.join(bad)))

    # ---- A9 持包 bot 在 Plant 态"连续静止 > 10s 而没下包"（片BU-R4 新增）----
    # 判的是**过程**：整段日志里有没有一次真的下包（L3：TPLANTED / "安放 C4"）；没有的话，
    # 任何"持包 bot 在 Live 连续静止 >= 10s"的段都点名 —— 这正是片BU-R4 的病灶形态
    # （T 持包者 Move 恒 0 ⇒ 站整回合、永不下包）。⛔ 不是"看结果凑数"：下包一旦发生，本项自动过。
    confirmed = sum(1 for r in log if 'TPLANTED' in r[2]) + sum(1 for r in log if '安放 C4' in r[2])
    still_bad = []
    for nm in order:
        if team_of.get(nm) != 'T':
            continue
        for (t0, t1, dur) in plant_still_runs(rows, nm):
            still_bad.append('%s t=%.1f..%.1f（%.1fs）' % (nm, t0, t1, dur))
    check('A9 持包 bot 不许"静止 > 10s 且没下包"', confirmed >= 1 or not still_bad,
          ('已下包 %d 次（TPLANTED / 安放 C4）' % confirmed) if confirmed >= 1
          else ('全场 0 次下包，且持包 bot 有 %d 段 Live 连续静止 >= %.0fs：%s'
                % (len(still_bad), PLANT_STILL_SECONDS, '; '.join(still_bad[:4]))))

    print('')
    print('RESULT: %s (%d FAIL / %d checks)' % ('PASS' if fail == 0 else 'FAIL', fail, len(checks)))
    return 1 if fail else 0


sys.exit(main())
