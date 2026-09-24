# -*- coding: utf-8 -*-
"""差异 #68「attack2（右键）链路缺失」的判据资产（探针）。

三段判据，全部只读盘上的源码 / 原版可执行：

A) **结构断言**（attack2 这条链的每一节都必须在盘上，且语义正确）
   A1 `CsWeapons.cs`：`CsWeaponDef` 有 `public bool CanSilence;` / `public bool CanBurst;`
   A2 `CsWeapons.cs`：`MarkAttack2Capabilities()` 里**恰好**这 4 条标记
      （Usp/M4A1 = silence、Glock18/Famas = burst）—— 多一条 / 少一条都判失败。
   A3 `CsTypes.cs`：`CsActor` 有 `Silenced` / `BurstMode`（可观测的状态位）。
   A4 `ICsMatch.cs`：`CsInputState` 有 `Attack2`（输入通道存在）。
   A5 `CombatModule.cs`：`Attack2` 由 **`GetKeyDown`** 填（按下沿），且**没有**用 `GetKey`（按住）填它。
      ⛔ 这一条是"切换型 vs 电平型"的**唯一静态可证伪点**：用 `GetKey` 会在每一帧翻转一次。
   A6 `CsMatch.cs`：**模拟侧也判一次沿** —— `inp.Attack2 && !_preAttack2` 出现，且 `_preAttack2 = inp.Attack2;`
      紧跟其后；`_preAttack2` 在重置路径里被清回 false。
   A7 `CsInventory.cs`：`ToggleWeaponMode` 里 `Silenced` / `BurstMode` 各翻转一次；且该方法**不**被
      `now < a.SwitchEndTime` 拦住（原文不同 = 切枪期间照样能切）。

B) **出处复算**（原版 `client.dll` 的固定文件偏移处逐字重取）
   7 条串必须**逐字节**等于登记值，**且它们在文件里的首次出现偏移必须等于登记偏移**
   （否则"换一个 dll 也能过"，判据就退化成"串里含有它"）。
   附带取 `+attack2` / `-attack2` 的首次出现偏移（attack2 是原版真输入通道）。

C) **反例复现**（"判沿"这件事必须能被证伪）
   把两条口径建成谓词，喂同一串采样：
     电平型 `level(...)`  = 每帧都切      —— 即 A5/A6 缺了「沿」的写法
     判沿型 `edge(...)`   = 只在 false→true 处切
   S1 按住不放（全 true）⇒ 电平型切 N 次、判沿型切 1 次（**这就是 bug 现场**）
   S2 正常点按（true/false 交替）⇒ 两者都 == 点按次数（证明判沿没把正常情况搞坏）
"""

import io
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
S = os.path.join(ROOT, 'client', 'Assets', 'Scripts')
DLL = os.path.join(ROOT, '\u539f\u7248\u8d44\u6e90', 'cs16src', 'cstrike', 'cl_dlls', 'client.dll')

# ---- B) 登记的文件偏移（真源 = Core/CsWeapons.cs 的 MarkAttack2Capabilities 注释）----
# 格式: (文件偏移, 期望串)。串里的 / 是 ASCII，与 dll 里的 C 串一致（无结尾 NUL）。
DLL_STRINGS = [
    (0x0e3804, 'weapons/usp_silencer_off.wav'),
    (0x0e3824, 'weapons/usp_silencer_on.wav'),
    (0x0e308c, 'weapons/m4a1_silencer_off.wav'),
    (0x0e30ac, 'weapons/m4a1_silencer_on.wav'),
    (0x0e26f4, 'weapons/famas-burst.wav'),
    (0x0e27d4, '#Switch_To_BurstFire'),
    (0x0e6af4, '#Cstrike_TitlesTXT_M4A1_Short'),
]
DLL_INPUT_TOKENS = ['+attack2', '-attack2']


def rd(rel):
    p = os.path.join(S, rel.replace('/', os.sep))
    if not os.path.isfile(p):
        return None
    with io.open(p, 'r', encoding='utf-8', newline='') as f:
        return f.read()


def main():
    fails = []
    notes = []

    wep = rd('Core/CsWeapons.cs')
    types = rd('Module/Match/CsTypes.cs')
    icm = rd('Module/Match/ICsMatch.cs')
    cm = rd('Module/Combat/CombatModule.cs')
    mt = rd('Module/Match/CsMatch.cs')
    inv = rd('Module/Match/CsInventory.cs')
    for name, txt in (('CsWeapons.cs', wep), ('CsTypes.cs', types), ('ICsMatch.cs', icm),
                      ('CombatModule.cs', cm), ('CsMatch.cs', mt), ('CsInventory.cs', inv)):
        if txt is None:
            fails.append('读不到 %s（路径变了？）' % name)
    if fails:
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL')
        return 2

    # ---------------- A1 / A2 ----------------
    if not re.search(r'public\s+bool\s+CanSilence\s*;', wep):
        fails.append('A1 CsWeapons.cs 没有 `public bool CanSilence;`')
    if not re.search(r'public\s+bool\s+CanBurst\s*;', wep):
        fails.append('A1 CsWeapons.cs 没有 `public bool CanBurst;`')
    if not fails:
        notes.append('A1 CsWeaponDef 的 CanSilence / CanBurst 字段在盘')

    m = re.search(r'private\s+static\s+void\s+MarkAttack2Capabilities\s*\(\s*\)\s*\{(.*?)\n\s{8}\}',
                  wep, re.S)
    if not m:
        fails.append('A2 找不到 `MarkAttack2Capabilities()` 的方法体（解析不到就无法判逐武器标记）')
    else:
        body = m.group(1)
        got = dict()
        for mm in re.finditer(r'Set\(\s*([A-Za-z_]\w*)\s*,\s*silence\s*:\s*(true|false)\s*,'
                              r'\s*burst\s*:\s*(true|false)\s*\)', body):
            got[mm.group(1)] = (mm.group(2) == 'true', mm.group(3) == 'true')
        want = {'Usp': (True, False), 'M4A1': (True, False),
                'Glock18': (False, True), 'Famas': (False, True)}
        if got != want:
            fails.append('A2 MarkAttack2Capabilities 的标记表与期望不符：实测 %s，期望 %s' % (got, want))
        else:
            notes.append('A2 MarkAttack2Capabilities 恰好 4 条：Usp/M4A1=silence、Glock18/Famas=burst')

    # ---------------- A3 / A4 ----------------
    for fld in ('Silenced', 'BurstMode'):
        if not re.search(r'public\s+bool\s+%s\s*;' % fld, types):
            fails.append('A3 CsTypes.cs 没有 `public bool %s;`（状态不可观测）' % fld)
    if not any(f.startswith('A3') for f in fails):
        notes.append('A3 CsActor 的 Silenced / BurstMode 状态位在盘')

    # Attack2 必须在 CsInputState 里（不能是别的 struct 里的同名字段）
    ms = re.search(r'struct\s+CsInputState\s*\{(.*?)\n\s{4}\}', icm, re.S)
    if not ms:
        fails.append('A4 ICsMatch.cs 解析不到 `struct CsInputState`')
    elif not re.search(r'public\s+bool\s+Attack2\s*;', ms.group(1)):
        fails.append('A4 CsInputState 里没有 `public bool Attack2;`')
    else:
        notes.append('A4 CsInputState.Attack2 在盘')

    # ---------------- A5 ----------------
    if not re.search(r'cmd\.Attack2\s*=\s*input\.GetKeyDown\s*\(\s*GameKey\.MouseRight\s*\)\s*;', cm):
        fails.append('A5 CombatModule.cs 没有 `cmd.Attack2 = input.GetKeyDown(GameKey.MouseRight);`'
                     '（attack2 必须取按下沿）')
    if re.search(r'cmd\.Attack2\s*=\s*input\.GetKey\s*\(', cm):
        fails.append('A5 CombatModule.cs 用 `input.GetKey(...)`（按住）填 Attack2 —— 会每帧翻转一次')
    if not any(f.startswith('A5') for f in fails):
        notes.append('A5 CombatModule 用 GetKeyDown 填 Attack2（按下沿），未用 GetKey')

    # ---------------- A6 ----------------
    # 正则**不许**再吃一行：第一版写成 `...[^\n]*\n([^\n]*\n)?`，那个可选组正好把
    #    `_preAttack2 = inp.Attack2;` 那一行吞掉，于是 A6 永远报"没紧跟"（判据自己写错的假红）。
    medge = re.search(r'if\s*\(\s*inp\.Attack2\s*&&\s*!\s*_preAttack2\s*\)[^\n]*\n', mt)
    if not medge:
        fails.append('A6 CsMatch.cs 没有 `if (inp.Attack2 && !_preAttack2)` —— 模拟侧没判沿'
                     '（`_localInput` 是黏的，按住型调用方会每帧翻转一次）')
    else:
        tail = mt[medge.end():medge.end() + 200]
        if not re.search(r'_preAttack2\s*=\s*inp\.Attack2\s*;', tail):
            fails.append('A6 `_preAttack2 = inp.Attack2;` 没有紧跟在判沿之后（下一帧的基线没更新）')
        else:
            notes.append('A6 CsMatch 判沿：`inp.Attack2 && !_preAttack2` 且紧跟基线更新')
    if not re.search(r'_preAttack2\s*=\s*false\s*;', mt):
        fails.append('A6 `_preAttack2` 没有在重置路径里清回 false（上一局按着不放会吃掉新局第一次按下）')

    # ---------------- A7 ----------------
    mtog = re.search(r'public\s+void\s+ToggleWeaponMode\s*\(', inv)
    if not mtog:
        fails.append('A7 CsInventory.cs 找不到 `public void ToggleWeaponMode(`')
    else:
        tail = inv[mtog.start():]
        nxt = re.search(r'\n\s{8}(?:public|internal|private|protected)\s', tail[10:])
        body = tail[:nxt.start() + 10] if nxt else tail
        if not re.search(r'\.Silenced\s*=\s*!\s*a\.Silenced\s*;', body):
            fails.append('A7 ToggleWeaponMode 里没有 `a.Silenced = !a.Silenced;`')
        if not re.search(r'\.BurstMode\s*=\s*!\s*a\.BurstMode\s*;', body):
            fails.append('A7 ToggleWeaponMode 里没有 `a.BurstMode = !a.BurstMode;`')
        if re.search(r'SwitchEndTime', body):
            fails.append('A7 ToggleWeaponMode 里出现了 `SwitchEndTime` —— attack2 不该被切枪拦住'
                         '（原版的消音器拆装不受切枪影响）')
        if not any(f.startswith('A7') for f in fails):
            notes.append('A7 ToggleWeaponMode 翻转 Silenced/BurstMode，且不拦切枪期')

    # ---------------- B) 出处复算 ----------------
    if not os.path.isfile(DLL):
        fails.append('B 载体不在盘：%s（降级链第 2 级的载体没了）' % DLL)
    else:
        with open(DLL, 'rb') as f:
            blob = f.read()
        notes.append('B 载体 client.dll = %d 字节' % len(blob))
        for off, s in DLL_STRINGS:
            raw = s.encode('ascii')
            at = blob[off:off + len(raw)]
            if at != raw:
                fails.append('B 偏移 0x%06x 处的字节不是 %r（实测 %r）' % (off, s, at))
                continue
            first = blob.find(raw)
            if first != off:
                fails.append('B %r 在 dll 里的**首次出现**偏移是 0x%06x，登记的是 0x%06x '
                             '⇒ 登记值不是可复算的唯一锚点' % (s, first, off))
        for tok in DLL_INPUT_TOKENS:
            raw = tok.encode('ascii')
            first = blob.find(raw)
            if first < 0:
                fails.append('B 载体里找不到原版输入通道 %r' % tok)
            else:
                notes.append('B 原版输入通道 %r 首次出现 @ 0x%06x（出现 %d 次）'
                             % (tok, first, blob.count(raw)))
        if not any(f.startswith('B ') for f in fails):
            notes.append('B 7 条串在该文件偏移处逐字节相等，且都是首次出现')

    # ---------------- C) 反例复现 ----------------
    def level(samples):
        """电平型口径：只要这一帧是按下就切一次（= 少了「沿」）。"""
        return sum(1 for s in samples if s)

    def edge(samples):
        """判沿口径：只在 false -> true 处切一次。"""
        n = 0
        prev = False
        for s in samples:
            if s and not prev:
                n += 1
            prev = s
        return n

    s1 = [True] * 8                                  # 按住不放
    s2 = [True, False, True, False, True, False]     # 正常点按 3 次
    s3 = [False, True, True, True, False, True]      # 长按 + 再点一次
    l1, e1 = level(s1), edge(s1)
    l2, e2 = level(s2), edge(s2)
    l3, e3 = level(s3), edge(s3)

    print('  A) 结构断言：%d 组通过' % len(notes))
    for mm in notes:
        print('     %s' % mm)
    print('  C) 反例复现（口径 = 按下沿）：')
    print('     S1 按住 8 帧          : 电平型切 %d 次, 判沿型切 %d 次' % (l1, e1))
    print('     S2 点按 3 次（交替）  : 电平型切 %d 次, 判沿型切 %d 次' % (l2, e2))
    print('     S3 长按 + 再点 1 次   : 电平型切 %d 次, 判沿型切 %d 次' % (l3, e3))
    if not (l1 >= 2 and e1 == 1):
        fails.append('C S1（按住不放）没有复现出「电平型每帧翻转 / 判沿型只切一次」（level=%d edge=%d）'
                     % (l1, e1))
    if not (l2 == 3 and e2 == 3):
        fails.append('C S2（正常点按）两者都该 == 3（level=%d edge=%d）—— 判沿把正常情况搞坏了'
                     % (l2, e2))
    if not (e3 == 2 and l3 > e3):
        fails.append('C S3（长按 + 再点）判沿型该 == 2、电平型该更多（level=%d edge=%d）' % (l3, e3))

    if fails:
        for mm in fails:
            print('  FAIL %s' % mm)
        print('RESULT: FAIL (%d) -- 差异 #68 判据未满足' % len(fails))
        return 2
    print('RESULT: PASS -- 差异 #68：attack2 链路存在、逐武器能力表有出处（dll 偏移可复算）、'
          '且「按下沿」语义在按住序列上可复现出电平型的重复翻转')
    return 0


if __name__ == '__main__':
    sys.exit(main())
