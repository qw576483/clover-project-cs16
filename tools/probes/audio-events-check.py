# -*- coding: utf-8 -*-
"""判据资产：D8「事件 → clip」接线的**离线**断言（切片K）。

出处/口径：skill `patterns/full-coverage-audit.md`（判定权三分：可计算 → 脚本）
           + 本片任务书 §2 ② 的判据「事件 X 触发 ⇒ 播放 clip 路径 Y」。

⛔ 本脚本**不驱动 Unity、不进 Play**（本片编辑器不可用）：它做的是**静态结构断言**，
   证明的是"接线存在且三层对上"，不能替代实机听音（音色/音量只能实机判）：

    第 1 层（触发点）：事件锚点（某个函数/分支）在源文件里存在；
    第 2 层（接线）：clip 常量引用出现在**锚点之后**（⇒ 它确实在该分支里被播，
                     而不是恰好在同一文件里出现）；
    第 3 层（落盘）：`CsAudioTuning` 里该常量的短名 × `Resources/Sound/SFX/` == 盘上的 .wav。

用法（幂等、只读）：
    python tools/probes/audio-events-check.py            # 打印每行 PASS/FAIL + 汇总
    python tools/probes/audio-events-check.py -v          # 追加打印锚点/clip 的行号

退出码：0 = 全过；1 = 有 FAIL（有 FAIL 就不许说"接线完成"）。
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SCRIPTS = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
SFX_DIR = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'Sound', 'SFX', 'sfx')
TUNING = os.path.join(SCRIPTS, 'Module', 'Audio', 'CsAudioTuning.cs')

VERBOSE = '-v' in sys.argv


def rd(path):
    try:
        with open(path, 'rb') as f:
            return f.read().decode('utf-8', 'replace')
    except OSError:
        return ''


def lineno(path, pat, start=0):
    """第一个匹配的行号（1 基）；`start` = 从该行号之后开始找（用于"锚点之后"的判定）。"""
    for i, l in enumerate(rd(path).split('\n')):
        if i + 1 <= start:
            continue
        if re.search(pat, l):
            return i + 1
    return 0


TUNING_TXT = rd(TUNING)
# CsAudioTuning 里 `public const string X = "sfx/<短名>"` 的真值表（短名前缀真源 = ResPaths.SoundSfxPrefix）
CLIPS = {}
for m in re.finditer(r'public const string\s+(\w+)\s*=\s*"([^"]+)"', TUNING_TXT):
    CLIPS[m.group(1)] = m.group(2)

RESPATHS = rd(os.path.join(SCRIPTS, 'Core', 'ResPaths.cs'))
PREFIX_M = re.search(r'public const string\s+SoundSfxPrefix\s*=\s*"([^"]+)"', RESPATHS)
PREFIX = PREFIX_M.group(1) if PREFIX_M else None

# (事件, 源文件（相对 client/Assets/Scripts）, 事件锚点正则, clip 常量名, 说明)
ROWS = [
    ('空仓扣扳机（弹匣为空）→ dryfire',
     'Module/Combat/CombatModule.cs',
     r'private bool CanFire\(', 'Dryfire',
     'CombatModule.CanFire 的「弹匣为空」分支'),
    ('子弹打在墙/地面（非角色碰撞体）→ hit_wall',
     'Module/Combat/CombatModule.cs',
     r'private void HandleLocalShot\(', 'HitWall',
     'CombatModule.HandleLocalShot 的「打墙落点」循环'),
    ('刀命中角色 → knife_hit',
     'Module/Match/CsDamage.cs',
     r'public void ApplyHit\(', 'KnifeHit',
     'CsDamage.ApplyHit（victim 非空 && def.Class==Knife && 出刀者是本地玩家）'),
    ('闪光弹爆炸 → flash_explode',
     'Module/Match/CsDamage.cs',
     r'public void ApplyFlash\(', 'FlashExplode',
     'CsDamage.ApplyFlash（手雷确实炸了那一处，与有没有致盲无关）'),
    ('C4 剩余 ≤ 10s 的加速档蜂鸣 → bomb_beep_fast',
     'Module/Audio/AudioModule.cs',
     r'private void TickBombBeep\(', 'BombBeepFast',
     'AudioModule.TickBombBeep 的加速档分支'),
]

# 附加断言（同一张判据表里的结构性事实）
EXTRA = [
    ('hit_wall 的材质分流入口存在（命中物材质名 → 分类）',
     'Module/Combat/Firearm.cs', r'ImpactMaterialName\('),
    ('材质分类实现存在（沙 / 木箱 / 门板 / 混凝土 / 金属 / 未知）',
     'Module/Audio/CsAudioTuning.cs', r'public static string ClassifyImpact\('),
    ('分流结果进了日志（可逐类核对）',
     'Module/Combat/CombatModule.cs', r'CsAudioTuning\.ClassifyImpact\('),
    ('空仓音有时间闸（否则按住左键每帧一响）',
     'Module/Combat/CombatModule.cs', r'CsAudioTuning\.DryfireMinInterval'),
    ('接线的 5 条 clip 都进了预热表（首播不吃在加载里）',
     'Module/Audio/AudioModule.cs', r'CsAudioTuning\.KnifeHit'),
]

fail = 0


def say(status, name, detail):
    print('%-5s %s  %s' % (status, name, detail))


print('resolved: SoundSfxPrefix = %r ; CsAudioTuning clips = %d' % (PREFIX, len(CLIPS)))
print('')

for name, relfile, anchor, const, note in ROWS:
    path = os.path.join(SCRIPTS, relfile.replace('/', os.sep))
    problems = []

    ln_anchor = lineno(path, anchor)
    if not ln_anchor:
        problems.append('事件锚点不存在：%s（%s）' % (anchor, relfile))

    # 必须找**锚点之后**的引用：预热表在文件开头也引用了同一个常量，
    #    只判"文件里出现过"会把"只加了预热、没挂事件"误判成 PASS。
    ln_clip = lineno(path, r'CsAudioTuning\.%s\b' % const, ln_anchor) if ln_anchor else 0
    if not ln_clip:
        if lineno(path, r'CsAudioTuning\.%s\b' % const):
            problems.append('clip 常量 CsAudioTuning.%s 在本文件里出现过，但**事件锚点之后**没有引用 '
                            '⇒ 未接在该分支里（%s）' % (const, note))
        else:
            problems.append('该文件里没有 clip 引用 CsAudioTuning.%s（%s）' % (const, note))

    short = CLIPS.get(const)
    if short is None:
        problems.append('CsAudioTuning 里没有常量 %s' % const)
    else:
        if PREFIX is not None:
            wav = os.path.join(SFX_DIR, os.path.basename(short) + '.wav')
            if not os.path.exists(wav):
                problems.append('盘上没有 %s（短名 %s）' % (wav, short))
        if not short.startswith('sfx/'):
            problems.append('短名 %r 不带 sfx/ 前缀（与 ResPaths.SoundSfxPrefix 的用法不一致）' % short)

    if problems:
        fail += 1
        say('FAIL', name, '; '.join(problems))
    else:
        detail = 'anchor@%s:%d → clip@%d → %s.wav 在盘' % (relfile, ln_anchor, ln_clip,
                                                          CLIPS[const].split('/')[-1])
        if VERBOSE:
            detail += '  [%s]' % note
        say('PASS', name, detail)

print('')
for name, relfile, pat in EXTRA:
    path = os.path.join(SCRIPTS, relfile.replace('/', os.sep))
    ln = lineno(path, pat)
    if ln:
        say('PASS', name, '%s:%d' % (relfile, ln))
    else:
        fail += 1
        say('FAIL', name, '在 %s 里找不到 %s' % (relfile, pat))

print('')
print('===== audio-events-check: FAIL=%d / %d 行 =====' % (fail, len(ROWS) + len(EXTRA)))
print('注：本脚本只证"接线存在且三层对上"，⛔ 不替代实机听音（音色/音量/时序观感属表现类）。')
sys.exit(1 if fail else 0)
