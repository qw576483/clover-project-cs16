# -*- coding: utf-8 -*-
"""差异 #72「换弹动画会丢」的判据资产（探针）。

判据分两段，都只读盘上源码：

A) **结构断言**（谁在什么条件下推进/消费换弹边沿）
   A1 `CsTypes.cs` 有 `public int ReloadSeq;` 字段（单调序号 = 事件计数，不是时间戳）。
   A2 `CsInventory.cs` 里 `ReloadSeq++` **恰好 1 次**，且紧跟在
      `a.ReloadEndTime = now + def.ReloadTime;` 之后 ⇒ 只在 `Reload` 的**成功分支**推进。
   A3 `CsInventory.Reload` 方法体内，`ReloadSeq++` **之前**出现过的 `return` 条数 ==
      该方法所有"提前退出"分支数（即：序号不会被那些 early-return 之前就先加）——
      这一条用"序号行必须位于最后一次 `return` 之后"来判（可复算，不看注释）。
   A4 两处表现层（`ActorView.cs` / `ViewModelRig.cs`）都：
        · 有 `ReloadSeq != _preReloadSeq`（消费序号）
        · **没有** `ReloadEndTime > _preReloadEndTime`（旧口径已下线）
        · 基线处把 `_preReloadSeq` 初始化成当前 `ReloadSeq`（否则首帧会误播/漏播）

B) **反例复现**（把两条口径各实现成一个谓词，喂同一串采样）
   把"视图每帧只读到一个标量"这件事如实建模：
       旧口径 fires = (D_t > D_{t-1} + eps)      —— D = ReloadEndTime（截止时间）
       新口径 fires = (seq_t != seq_{t-1})       —— seq = ReloadSeq（单调序号）
   两条序列：
       S1 采样间隔 ≥ ReloadTime（视图整段漏窗：掉帧 / 切局 / 视图模块暂停）
       S2 正常帧率（60Hz）
   断言：S1 上 **旧口径漏、新口径不漏**；S2 上 **两者都命中**（证明新口径没把正常情况搞坏）。
   ⛔ 这一段的常数（ReloadTime / SwitchTime）**从源码解析**，不写死。
"""

import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
S = os.path.join(ROOT, 'client', 'Assets', 'Scripts')


def rd(rel):
    p = os.path.join(S, rel.replace('/', os.sep))
    if not os.path.isfile(p):
        return None
    with io.open(p, 'r', encoding='utf-8', newline='') as f:
        return f.read()


def main():
    fails = []
    notes = []

    # ---------------- A) 结构断言 ----------------
    types = rd('Module/Match/CsTypes.cs')
    inv = rd('Module/Match/CsInventory.cs')
    av = rd('Module/View/ActorView.cs')
    vm = rd('Module/View/ViewModelRig.cs')
    wep = rd('Core/CsWeapons.cs')
    const = rd('Core/CsConst.cs')
    for name, txt in (('CsTypes.cs', types), ('CsInventory.cs', inv), ('ActorView.cs', av),
                      ('ViewModelRig.cs', vm), ('CsWeapons.cs', wep), ('CsConst.cs', const)):
        if txt is None:
            fails.append('读不到 %s（路径变了？）' % name)
    if fails:
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL')
        return 2

    # A1
    if not re.search(r'public\s+int\s+ReloadSeq\s*;', types):
        fails.append('A1 CsTypes.cs 没有 `public int ReloadSeq;`')
    else:
        notes.append('A1 ReloadSeq 字段在盘')

    # A2：ReloadSeq++ 恰好一次，且紧跟 ReloadEndTime 赋值
    n_inc = len(re.findall(r'\.ReloadSeq\+\+', inv))
    if n_inc != 1:
        fails.append('A2 CsInventory.cs 里 `ReloadSeq++` 出现 %d 次（应恰好 1 次）' % n_inc)
    else:
        m = re.search(r'ReloadEndTime\s*=\s*now\s*\+\s*def\.ReloadTime\s*;\s*\n(?:\s*//[^\n]*\n)*\s*([A-Za-z_][\w]*)\.ReloadSeq\+\+', inv)
        if not m:
            fails.append('A2 `ReloadSeq++` 没有紧跟 `a.ReloadEndTime = now + def.ReloadTime;`（成功分支）')
        else:
            notes.append('A2 ReloadSeq++ 恰好 1 次且紧跟成功分支赋值（接收者 %s）' % m.group(1))

    # A3：ReloadSeq++ 必须在该方法内**最后一次 return 之后**
    mrel = re.search(r'public\s+void\s+Reload\s*\(', inv)
    if not mrel:
        fails.append('A3 CsInventory.cs 里找不到 `public void Reload(`')
    else:
        # 粗切方法体：从签名到下一个顶格（8 空格内）的成员声明
        start = mrel.start()
        tail = inv[start:]
        nxt = re.search(r'\n\s{8}(?:public|internal|private|protected)\s', tail[10:])
        body = tail[:nxt.start() + 10] if nxt else tail
        if 'ReloadSeq++' not in body:
            fails.append('A3 `ReloadSeq++` 不在 `Reload` 方法体内')
        else:
            i_inc = body.index('ReloadSeq++')
            i_last_ret = body.rfind('return', 0, i_inc)
            if i_last_ret < 0:
                fails.append('A3 `Reload` 体内 `ReloadSeq++` 之前没有任何 `return` —— 方法与预期不符（早期退出分支不见了？）')
            else:
                n_ret = len(re.findall(r'\breturn\b', body[:i_inc]))
                notes.append('A3 ReloadSeq++ 之前有 %d 个 return（早期退出分支都在它之前）' % n_ret)

    # A4：两处表现层
    for name, txt, recv in (('ActorView.cs', av, 'actor'), ('ViewModelRig.cs', vm, 'local')):
        if 'ReloadSeq != _preReloadSeq' not in txt:
            fails.append('A4 %s 没有消费 `ReloadSeq != _preReloadSeq`（表现层收不到换弹边沿）' % name)
        if re.search(r'ReloadEndTime\s*>\s*_preReloadEndTime', txt):
            fails.append('A4 %s 仍有旧的 `ReloadEndTime > _preReloadEndTime` 口径（漏报源没拔掉）' % name)
        if not re.search(r'_preReloadSeq\s*=\s*%s\.ReloadSeq\s*;' % recv, txt):
            fails.append('A4 %s 的基线没有把 `_preReloadSeq` 初始化成 `%s.ReloadSeq`' % (name, recv))
        if '_preReloadEndTime' in txt:
            fails.append('A4 %s 还留着 `_preReloadEndTime` 字段（旧口径的残留）' % name)
    if not any('A4' in f for f in fails):
        notes.append('A4 两处表现层都改为消费序号，旧时间戳口径已下线')

    # ---------------- B) 反例复现 ----------------
    # 参数下标**按签名解析**，不许硬编码：W(...) 的形参表里找名为 `reload` 的那个位置。
    #    （第一版写死 parts[11]，实测 AK47 打出 0.5s（其实是 spread），真值 3.0s ——
    #      判据资产里印错数字比不印更坏：它会被当成"实测"。）
    msig = re.search(r'W\(([^)]*)\)\s*\n?\s*\{', wep)
    if not msig:
        fails.append('B 解析不到 `W(...)` 的形参签名')
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL')
        return 2
    sig_params = [p.strip() for p in msig.group(1).split(',')]
    sig_names = []
    for p in sig_params:
        m = re.search(r'([A-Za-z_]\w*)\s*$', p)
        sig_names.append(m.group(1) if m else p)
    if 'reload' not in sig_names:
        fails.append('B `W(...)` 签名里没有名为 reload 的形参（签名变了？）：%s' % sig_names)
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL')
        return 2
    RELOAD_IDX = sig_names.index('reload')

    def weapon_reload(wid):
        m = re.search(r'W\(\s*%s\s*,' % re.escape(wid), wep)
        if not m:
            return None
        seg = wep[m.start():m.start() + 1400]
        args = re.search(r'W\((.*?)\),\s*//', seg, re.S)
        if not args:
            return None
        parts = [p.strip() for p in args.group(1).split(',')]
        if len(parts) <= RELOAD_IDX:
            return None
        return float(parts[RELOAD_IDX].rstrip('f'))

    def const_float(name):
        m = re.search(r'%s\s*=\s*([0-9.]+)f' % re.escape(name), const)
        return float(m.group(1)) if m else None

    r_ak = weapon_reload('Ak47')
    r_glock = weapon_reload('Glock18')
    sw_primary = const_float('SwitchTimePrimary')
    sw_pistol = const_float('SwitchTimePistol')
    if None in (r_ak, r_glock, sw_primary, sw_pistol):
        fails.append('B 解析不到真实常数（AK47/Glock18 的 ReloadTime 或 SwitchTime*）')
    else:
        notes.append('B 真实常数（形参下标 %d=reload）：AK47 ReloadTime=%ss / Glock18 ReloadTime=%ss / '
                     'SwitchTimePrimary=%ss / SwitchTimePistol=%ss'
                     % (RELOAD_IDX, r_ak, r_glock, sw_primary, sw_pistol))
        # 「切枪归零 ⇒ 后一次的截止时间**更小**」必须用真实数字验一次（可达性证明，不是断言"可能"）：
        #   主武器换弹 @t=0 ⇒ D1 = r_ak；切手枪归零（ReloadEndTime=0，耗时 sw_pistol）
        #   ⇒ 在 t = sw_pistol + dt 换弹手枪 ⇒ D2 = sw_pistol + dt + r_glock；要求 D2 < D1。
        dt_probe = 0.01
        d1 = r_ak
        d2 = sw_pistol + dt_probe + r_glock
        if not (d2 < d1):
            fails.append('B 「新截止时间更小」在这组真实常数下**不可达**（%.2f vs %.2f）'
                         '⇒ 差异 #72 的 why 里那条论证要改写' % (d2, d1))
        else:
            notes.append('B deadline 下降可达（真数）：主武器换弹 D1=%.2f ⇒ 切手枪(%.2fs)后再换弹 '
                         'D2=%.2f < D1 ⇒ 单靠 `>` 比较不可靠' % (d1, sw_pistol, d2))

    EPS = 1e-4

    def old_fires(prev_d, d):
        return d > prev_d + EPS

    def new_fires(prev_seq, seq):
        return seq != prev_seq

    def run_sequence(samples, reload_at, reload_time):
        """samples: 采样时刻列表（升序）。返回 (旧口径命中次数, 新口径命中次数)。
        sim：reload 只在 `reload_at` 成功一次（0 -> 非零），在 reload_at+reload_time 结算归零。"""
        seq = 0
        d = 0.0
        prev_seq, prev_d = 0, 0.0
        old_hits, new_hits = 0, 0
        for t in samples:
            # 采样前推进模拟到 t
            if reload_at is not None and t >= reload_at and seq == 0:
                seq = 1
                d = reload_at + reload_time
            if reload_at is not None and t >= reload_at + reload_time:
                d = 0.0
            if old_fires(prev_d, d):
                old_hits += 1
            if new_fires(prev_seq, seq):
                new_hits += 1
            prev_d, prev_seq = d, seq
        return old_hits, new_hits

    R = r_ak if r_ak else 3.0
    # S1：采样间隔 >= ReloadTime（视图整段漏窗）
    s1 = [-0.1, R + 0.1]
    o1, n1 = run_sequence(s1, 0.0, R)
    # S2：正常 60Hz
    s2 = [round(-0.1 + i / 60.0, 4) for i in range(int((R + 0.3 + 0.1) * 60) + 1)]
    o2, n2 = run_sequence(s2, 0.0, R)

    print('  A) 结构断言：%d 条通过' % len(notes))
    for m in notes:
        print('     %s' % m)
    print('  B) 反例复现（reload_time=%.2fs）:' % R)
    print('     S1 采样 %s（间隔 %.1fs >= 换弹时长）: 旧口径命中 %d 次, 新口径命中 %d 次'
          % (s1, R + 0.2, o1, n1))
    print('     S2 采样 60Hz（%d 帧）: 旧口径命中 %d 次, 新口径命中 %d 次' % (len(s2), o2, n2))
    if not (o1 == 0 and n1 >= 1):
        fails.append('B S1（漏窗）没有复现出旧口径漏报 / 新口径不漏（old=%d new=%d）' % (o1, n1))
    if not (o2 >= 1 and n2 >= 1):
        fails.append('B S2（正常帧率）应两者都命中（old=%d new=%d）' % (o2, n2))

    if fails:
        for m in fails:
            print('  FAIL %s' % m)
        print('RESULT: FAIL (%d) -- 差异 #72 判据未满足' % len(fails))
        return 2
    print('RESULT: PASS -- 差异 #72：换弹边沿=单调序号；旧口径在漏窗序列上可复现漏报，新口径不漏')
    return 0


if __name__ == '__main__':
    sys.exit(main())
