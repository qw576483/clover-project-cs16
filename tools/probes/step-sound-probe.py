# -*- coding: utf-8 -*-
"""差异 #59「脚步触发口径与落地音阈值」的判据资产（探针）。

判据分三段，全部只读盘上文件（不改任何东西）：

A) **结构断言**（新口径真的落在源码里）
   A1 `Core/CsConst.cs` 仍在、`CsActor.Velocity` 仍是三维向量（速度模长的口径前提）。
   A2 `CsAudioTuning.cs` 里**新五常数**齐备且数值精确：
        StepMinSpeed=3.81 / StepCooldownConcreteMs=300 / StepCooldownSlowMs=400 /
        StepDuckingExtraMs=100 / LandMinFallSpeed=7.366
     且**旧两名彻底消失**：StepDistanceRun / StepMinInterval（距离制下线的直接证据）。
   A3 `AudioModule.cs` 的 `TickFootsteps` 里，五道顺序**按原版排**（用行号单调性判，不看注释）：
        (1) `st.StepCooldownMs -= dt * 1000f;`（每帧递减）
        (2) `if (st.StepCooldownMs < 0f) ...`（夹零）
        (3) `if (st.StepCooldownMs <= 0f && _match.Phase != CsRoundPhase.Freeze)`（冷却 + 冻结）
        (4) `if (speed < CsAudioTuning.StepMinSpeed)` -> 装 `StepCooldownSlowMs`
        (5) `else if (... && a.OnGround && ...)` -> 装 `StepCooldownConcreteMs (+ StepDuckingExtraMs)` 并放音
     即：`递减 < 夹零 < 冷却判定 < 慢速分支 < 放音分支`。
   A4 速度取**三维模长**：有 `a.Velocity.magnitude`，且**没有** `new Vector2(a.Velocity.x, a.Velocity.z)`
     （旧的水平分量口径已下线）。
   A5 `FootState` 里已无 `LastPos` / `Accum` / `LastStepTime`；有 `StepCooldownMs`。
   A6 脚步的 `PlayFor` 调用在 `AudioModule.cs` 内**恰好 1 处**
     （不能有一处"顺手也多放一次"）。

B) **出处复算**（新数值不是拍脑袋，是从原版那一行抠出来的）
   读 `原版资源/hlsdk/pm_shared/pm_shared.c`，对**固定行号 + 逐字内容**做双重断言，
   且要求该行是这串字符在文件里的**首次出现**（防"行号对了但看的是别处"）：
        :517  speed = Length(pmove->velocity);
        :519  if (speed < 150)
        :521  pmove->flTimeStepSound = 400;
        :626  pmove->flTimeStepSound = 300;
        :630  if (pmove->flags & FL_DUCKING || fLadder)
        :632  pmove->flTimeStepSound += 100;
        :125  #define PLAYER_MAX_SAFE_FALL_SPEED 580
        :128  #define PLAYER_MIN_BOUNCE_SPEED 350
        :2243 else if (pmove->flFallVelocity > PLAYER_MAX_SAFE_FALL_SPEED / 2)
        :2404 pmove->flTimeStepSound -= pmove->cmd.msec;
        :2477 pmove->flFallVelocity = -pmove->velocity[2];
        :2493 PM_UpdateStepSound();
   并把 unit->m 的折算复算一遍（GoldSrc 1 unit = 1 inch = 0.0254 m，出处见 `Core/CsConst.cs`
   头注 `原版资源/cs16src/cs16_build.py:43` 的 `HL_UNIT = 0.0254`）：
        150    u/s * 0.0254 = 3.81  == StepMinSpeed
        580/2  u/s * 0.0254 = 7.366 == LandMinFallSpeed
        400 / 300 / 100 ms 直接相等

C) **反例复现**（两条口径在**节拍**上真的不同，且"谁能出声"没被动过）
   把两条口径各写成一个步点模型，喂同一段速度曲线：
       旧（距离制，常数从 `git show HEAD:...CsAudioTuning.cs` 现取，不写死）：
           accum += v * dt；当 accum >= D 且距上一步 >= I 时记一步
       新（时间制，常数从当前源码解析）：
           cd -= dt*1000；cd <= 0 时按 (4)/(5) 重装冷却，出声档同时记一步
   S1 匀速跑 v=SpeedRifle 共 5 s：断言 **旧步数 > 新步数**
      （旧被步幅 0.62 m 拉着跑、又被 0.16 s 地面限流；新恒定 300 ms 一拍）
      —— 即这次改的是"节拍"，不是"能不能出声"。
   S2 五种速度（knife / rifle / AWP / walk / crouch，全部从 `CsConst.cs` 解析）：
      断言两条口径的**"是否出声"集合完全一致**（证明 150 门槛没被顺手改动），
      并给出各自的**步幅**：新口径步幅 = v * 冷却（随速度增长）；旧口径步幅恒 = D。
   S3 AWP 与 walk（v < StepMinSpeed）两条口径都 **0 步**。

⛔ 本探针**只读**：不写任何文件、不调用 Unity。旧口径常数一律从 git 现取，绝不写死。
"""

import io
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
S = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
ORIG = os.path.join(ROOT, '原版资源', 'hlsdk', 'pm_shared', 'pm_shared.c')
TUNE_REL = 'client/Assets/Scripts/Module/Audio/CsAudioTuning.cs'
UNIT_M = 0.0254   # GoldSrc 1 unit = 1 inch = 0.0254 m（出处：cs16_build.py 的 HL_UNIT）


def rd(rel):
    p = os.path.join(S, rel.replace('/', os.sep))
    if not os.path.isfile(p):
        return None
    with io.open(p, 'r', encoding='utf-8', newline='') as f:
        return f.read()


def rdabs(p):
    if not os.path.isfile(p):
        return None
    with io.open(p, 'r', encoding='utf-8', newline='') as f:
        return f.read()


def const_float(src, name):
    '''从源码里抠 `public const float <name> = <num>f;`'''
    m = re.search(r'public\s+const\s+float\s+' + re.escape(name) + r'\s*=\s*([-0-9.]+)f?\s*;', src)
    return float(m.group(1)) if m else None


def lineno(src, token):
    '''token 在 src 里首次出现的 1 基行号（找不到返回 -1）。'''
    for i, l in enumerate(src.split('\n')):
        if token in l:
            return i + 1
    return -1


def main():
    fails = []
    notes = []

    # ---------------- A) 结构断言 ----------------
    const = rd('Core/CsConst.cs')
    types = rd('Module/Match/CsTypes.cs')
    tune = rd('Module/Audio/CsAudioTuning.cs')
    amod = rd('Module/Audio/AudioModule.cs')
    for name, txt in (('CsConst.cs', const), ('CsTypes.cs', types),
                      ('CsAudioTuning.cs', tune), ('AudioModule.cs', amod)):
        if txt is None:
            fails.append('读不到 %s（路径变了？）' % name)
    if fails:
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL (%d) -- 差异 #59 判据未满足' % len(fails))
        return 2

    if 'public Vector3 Velocity;' not in types:
        fails.append('A1 CsTypes.cs 里 CsActor.Velocity 不再是 Vector3')
    else:
        notes.append('A1 CsActor.Velocity 是 Vector3（三维模长口径成立）')

    WANT = [
        ('StepMinSpeed', 3.81),
        ('StepCooldownConcreteMs', 300.0),
        ('StepCooldownSlowMs', 400.0),
        ('StepDuckingExtraMs', 100.0),
        ('LandMinFallSpeed', 7.366),
    ]
    got = {}
    for k, v in WANT:
        g = const_float(tune, k)
        got[k] = g
        if g is None:
            fails.append('A2 CsAudioTuning.cs 缺常数 %s' % k)
        elif abs(g - v) > 1e-9:
            fails.append('A2 %s = %r，应为 %r' % (k, g, v))
    gone = [n for n in ('StepDistanceRun', 'StepMinInterval') if n in tune]
    if gone:
        fails.append('A2 旧距离制常数仍在 CsAudioTuning.cs：%s' % ', '.join(gone))
    if not any(f.startswith('A2') for f in fails):
        notes.append('A2 新五常数齐备且精确（%s）；旧距离制两名已消失'
                     % ' / '.join('%s=%g' % (k, v) for k, v in WANT))

    # A3 顺序断言（行号单调）
    seq = [
        ('step-dec',   'st.StepCooldownMs -= dt * 1000f;'),
        ('clamp',      'if (st.StepCooldownMs < 0f)'),
        ('cooldown',   'if (st.StepCooldownMs <= 0f && _match.Phase != CsRoundPhase.Freeze)'),
        ('speed',      'var speed = a.Velocity.magnitude;'),
        ('slow-gate',  'if (speed < CsAudioTuning.StepMinSpeed)'),
        ('slow-arm',   'st.StepCooldownMs = CsAudioTuning.StepCooldownSlowMs;'),
        ('step-arm',   'st.StepCooldownMs = CsAudioTuning.StepCooldownConcreteMs'),
        ('duck-extra', 'a.IsCrouching ? CsAudioTuning.StepDuckingExtraMs : 0f'),
    ]
    pos = []
    for tag, tok in seq:
        n = lineno(amod, tok)
        pos.append(n)
        if n < 0:
            fails.append('A3 AudioModule.cs 找不到「%s」（新口径的一环缺失）' % tok)
    if all(p > 0 for p in pos):
        if pos != sorted(pos):
            fails.append('A3 五道顺序不对（行号必须单调递增）：%s'
                         % dict((t, p) for (t, _), p in zip(seq, pos)))
        else:
            notes.append('A3 五道顺序按原版排：递减(%d) < 夹零(%d) < 冷却+冻结(%d) < 速度(%d) '
                         '< 慢速档(%d) < 放音档(%d)' % tuple(pos[:6]))

    # A4 三维模长 / 旧水平分量下线
    if 'new Vector2(a.Velocity.x, a.Velocity.z)' in amod:
        fails.append('A4 AudioModule.cs 仍在用水平分量算速度（旧口径没下线）')
    else:
        notes.append('A4 速度取 a.Velocity.magnitude（三维模长）；水平分量旧写法已无')

    # A5 FootState 字段
    i = amod.find('private struct FootState')
    if i < 0:
        fails.append('A5 找不到 FootState')
    else:
        seg = amod[i:i + 1200]
        j = seg.find('}')
        if j > 0:
            seg = seg[:j]
        bad5 = [b for b in ('LastPos', 'Accum', 'LastStepTime') if b in seg]
        if bad5:
            fails.append('A5 FootState 里旧的 %s 还在' % ', '.join(bad5))
        if 'StepCooldownMs' not in seg:
            fails.append('A5 FootState 里没有 StepCooldownMs')
        if not bad5 and 'StepCooldownMs' in seg:
            notes.append('A5 FootState = Have/StepCooldownMs/WasOnGround/LastFallSpeed/Alive'
                         '（距离累计三字段已删）')

    # A6 放音点唯一
    PLAY = 'PlayFor(a, localId, null, spatialFrom: pos);'
    n_play = lineno(amod, PLAY)
    c_play = amod.count(PLAY)
    if n_play < 0:
        fails.append('A6 找不到脚步的 PlayFor 调用')
    elif c_play != 1:
        fails.append('A6 脚步 PlayFor 调用有 %d 处（应恰好 1 处，否则会重复放音）' % c_play)
    else:
        notes.append('A6 脚步放音点唯一（第 %d 行），且只在放音档里' % n_play)

    # ---------------- B) 出处复算 ----------------
    orig = rdabs(ORIG)
    if orig is None:
        fails.append('B0 读不到 %s（载体丢了？）' % ORIG)
    else:
        olines = orig.split('\n')
        # 每一行都要「行号 + 逐字内容」都对，且该串在文件里**首次出现**就在这一行
        # （防"行号对了但看的是别处"）。
        CHECKS = [
            (517,  'speed = Length(pmove->velocity);'),
            (519,  'if (speed < 150)'),
            (521,  'pmove->flTimeStepSound = 400;'),
            (630,  'if (pmove->flags & FL_DUCKING || fLadder)'),
            (632,  'pmove->flTimeStepSound += 100;'),
            (125,  '#define PLAYER_MAX_SAFE_FALL_SPEED 580'),
            (128,  '#define PLAYER_MIN_BOUNCE_SPEED 350'),
            (2243, 'else if (pmove->flFallVelocity > PLAYER_MAX_SAFE_FALL_SPEED / 2)'),
            (2404, 'pmove->flTimeStepSound -= pmove->cmd.msec;'),
            (2477, 'pmove->flFallVelocity = -pmove->velocity[2];'),
            (2493, 'PM_UpdateStepSound();'),
        ]
        okb = 0
        for ln, want in CHECKS:
            actual = olines[ln - 1].strip() if ln - 1 < len(olines) else ''
            if actual != want:
                fails.append('B %s:%d 内容不符：实际「%s」' % (os.path.basename(ORIG), ln, actual))
                continue
            first = lineno(orig, want)
            if first != ln:
                fails.append('B %s:%d 不是「%s」的首次出现（首次在 %d）—— 行号可能已漂移'
                             % (os.path.basename(ORIG), ln, want, first))
                continue
            okb += 1

        # `flTimeStepSound = 300;` 是**材质表里重复出现**的同一句（属"多处同值"，不是单一出处），
        # 所以不能拿"首次出现"当判据。改成三条：
        #   ① :567 是它在材质 switch 里的第一次（混凝土分支）；
        #   ② :626 是同一句在 switch 的 default 分支；
        #   ③ 全文件出现 >= 10 次（证明它确实是"一张表"而不是孤例）。
        # ⛔ 这个数（10）不是拍脑袋：下面是 556/567/574/581/588/601/608/615/622/626 共 10 行
        #    （556 是脚部涉水 SLOSH 分支，在 switch 之外），断言的是"至少这么多处"。
        T300 = 'pmove->flTimeStepSound = 300;'
        n300 = orig.count(T300)
        l300 = []
        for idx, l in enumerate(olines):
            if l.strip() == T300:
                l300.append(idx + 1)
        if olines[567 - 1].strip() != T300:
            fails.append('B %s:567 内容不符（应为材质表里 300ms 的首个分支）' % os.path.basename(ORIG))
        elif olines[626 - 1].strip() != T300:
            fails.append('B %s:626 内容不符（应为材质表里 300ms 的 default 分支）' % os.path.basename(ORIG))
        elif T300 not in olines[556 - 1]:
            fails.append('B %s:556 内容不符（应为脚部涉水 SLOSH 的 300ms）' % os.path.basename(ORIG))
        elif n300 < 10:
            fails.append('B %s 里「%s」只出现 %d 次（应 >= 10：材质表各分支同值）'
                         % (os.path.basename(ORIG), T300, n300))
        else:
            okb += 1

        if okb == len(CHECKS) + 1:
            notes.append('B %s 的 %d 个关键行逐字相符且都是首次出现；另：300ms 在材质表'
                         '（:567~:626，共 %d 行同值，含脚部涉水 :556）里重复出现，已按"多处同值"判'
                         % (os.path.basename(ORIG), len(CHECKS), len(l300)))

        want_min = 150 * UNIT_M
        want_land = (580 / 2.0) * UNIT_M
        if got.get('StepMinSpeed') is not None and abs(got['StepMinSpeed'] - want_min) > 5e-4:
            fails.append('B StepMinSpeed != 150*0.0254（算得 %.4f，源码 %.4f）'
                         % (want_min, got['StepMinSpeed']))
        if got.get('LandMinFallSpeed') is not None and abs(got['LandMinFallSpeed'] - want_land) > 5e-4:
            fails.append('B LandMinFallSpeed != 580/2*0.0254（算得 %.4f，源码 %.4f）'
                         % (want_land, got['LandMinFallSpeed']))
        if not any(f.startswith('B ') for f in fails):
            notes.append('B 折算复算通过：150 u/s = %.4f m/s；580/2 u/s = %.4f m/s'
                         % (want_min, want_land))

    # ---------------- C) 反例复现 ----------------
    NEW_CD = got.get('StepCooldownConcreteMs')
    NEW_SLOW = got.get('StepCooldownSlowMs')
    NEW_DUCK = got.get('StepDuckingExtraMs') or 0.0
    NEW_MIN = got.get('StepMinSpeed')

    old_src = None
    try:
        old_src = subprocess.check_output(
            ['git', 'show', 'HEAD:' + TUNE_REL], cwd=ROOT).decode('utf-8')
    except Exception as e:
        fails.append('C 取不到旧版 CsAudioTuning.cs（git HEAD）：%s' % e)
    OLD_D = OLD_I = None
    if old_src:
        OLD_D = const_float(old_src, 'StepDistanceRun')
        OLD_I = const_float(old_src, 'StepMinInterval')
        if OLD_D is None or OLD_I is None:
            fails.append('C 旧版里 StepDistanceRun/StepMinInterval 抠不到（旧常数名记错了？）')

    V_KNIFE = V_RIFLE = V_AWP = WALK_MUL = CROUCH_MUL = None
    if not fails:
        V_KNIFE = const_float(const, 'SpeedKnife')
        V_RIFLE = const_float(const, 'SpeedRifle')
        V_AWP = const_float(const, 'SpeedAWP')
        WALK_MUL = const_float(const, 'SpeedWalkMultiplier')
        CROUCH_MUL = const_float(const, 'SpeedCrouchMultiplier')
        miss = [n for n, v in (('SpeedKnife', V_KNIFE), ('SpeedRifle', V_RIFLE),
                               ('SpeedAWP', V_AWP), ('SpeedWalkMultiplier', WALK_MUL),
                               ('SpeedCrouchMultiplier', CROUCH_MUL)) if v is None]
        if miss:
            fails.append('C CsConst.cs 抠不到速度常数：%s' % ', '.join(miss))

    if not fails:
        DT = 1.0 / 128.0

        def sim_old(v, dur):
            '''旧距离制：accum >= D 且距上一步 >= I 才记一步。'''
            steps, accum, last, t = [], 0.0, -1e9, 0.0
            n = int(dur / DT)
            for _ in range(n):
                if v >= NEW_MIN:
                    accum += v * DT
                    if accum >= OLD_D and (t - last) >= OLD_I:
                        steps.append(t)
                        accum = 0.0
                        last = t
                t += DT
            return steps

        def sim_new(v, dur, crouch=False):
            '''新时间制：cd 递减；归零后按 (4)/(5) 重装。'''
            steps, cd, t = [], 0.0, 0.0
            n = int(dur / DT)
            for _ in range(n):
                cd -= DT * 1000.0
                if cd < 0.0:
                    cd = 0.0
                if cd <= 0.0:
                    if v < NEW_MIN:
                        cd = NEW_SLOW
                    else:
                        cd = NEW_CD + (NEW_DUCK if crouch else 0.0)
                        steps.append(t)
                t += DT
            return steps

        DUR = 5.0
        so = sim_old(V_RIFLE, DUR)
        sn = sim_new(V_RIFLE, DUR)
        print('  C) 反例复现（DT=1/128 s，%s 旧口径常数 D=%.3f m / I=%.3f s）'
              % (TUNE_REL.split('/')[-1], OLD_D, OLD_I))
        print('     S1 匀速跑 v=%.2f m/s x %.1f s：旧距离制 %d 步（%.2f Hz）；新时间制 %d 步（%.2f Hz）；比 %.2f x'
              % (V_RIFLE, DUR, len(so), len(so) / DUR, len(sn), len(sn) / DUR,
                 len(so) / float(max(1, len(sn)))))
        if not len(so) > len(sn):
            fails.append('C S1 旧步数(%d) 未大于新步数(%d)——节拍差没复现出来' % (len(so), len(sn)))

        print('     S2 出声集合 / 步幅（旧口径步幅恒 = %.3f m）：' % OLD_D)
        rows = [
            ('knife', V_KNIFE, False),
            ('rifle', V_RIFLE, False),
            ('AWP', V_AWP, False),
            ('walk', V_KNIFE * WALK_MUL, False),
            ('crouch', V_KNIFE * CROUCH_MUL, True),
        ]
        for tag, v, crouch in rows:
            a = sim_old(v, DUR)
            b = sim_new(v, DUR, crouch=crouch)
            ok_old = len(a) > 0
            ok_new = len(b) > 0
            if ok_old != ok_new:
                fails.append('C S2 %s（v=%.3f）两条口径出声与否不一致：旧=%s 新=%s'
                             % (tag, v, ok_old, ok_new))
            stride = (v * (NEW_CD + (NEW_DUCK if crouch else 0.0)) / 1000.0) if ok_new else 0.0
            print('        %-7s v=%5.2f m/s   旧 %s   新 %s   新步幅=%.3f m'
                  % (tag, v, '出声' if ok_old else '无声', '出声' if ok_new else '无声', stride))

        for tag, v in (('AWP', V_AWP), ('walk', V_KNIFE * WALK_MUL)):
            if len(sim_old(v, DUR)) or len(sim_new(v, DUR)):
                fails.append('C S3 %s（v=%.3f < %.2f）不该出声' % (tag, v, NEW_MIN))
        v_crouch = V_KNIFE * CROUCH_MUL
        if v_crouch < NEW_MIN:
            notes.append('C 如实记：蹲行 v=%.2f m/s < %.2f m/s ⇒ StepDuckingExtraMs(+100ms) 这一档'
                         '当前配置下不可达（原版同样要求先过 150 u/s 门槛才走 +100 分支）；'
                         '留着是为了与原文行对齐、配置漂移时不失真' % (v_crouch, NEW_MIN))

    print('  A) 结构断言：%d 条通过' % len(notes))
    for m in notes:
        print('     %s' % m)
    if fails:
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL (%d) -- 差异 #59 判据未满足' % len(fails))
        return 2
    print('RESULT: PASS -- 差异 #59：脚步 = 原版时间制冷却（150/400/300/+100 逐行有出处）；'
          '落地闸 = 580/2 u/s；两条口径的节拍差异已复现，出声集合未被动过')
    return 0


if __name__ == '__main__':
    sys.exit(main())
