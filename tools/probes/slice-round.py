#!/usr/bin/env python
# -*- coding: utf-8 -*-
# ============================================================================
#  判据资产 · 片BU-R5：把一次 Play 采集**按回合切片**，供 bot-goal-gate.py 逐回合判。
#
#  为什么需要它（片BU-R5 的口径问题）：
#    bot-goal-gate.py 的 A1..A9 是**整份产物**的聚合（它自己把 A1/A2 标成 "one whole round"）。
#    bu-r4 的驱动在第一个 RoundEnd 就停了采集（round 1 完整 + round 2 只 3.6s）；
#    bu-r5 的驱动改成采满 round 2 ⇒ 产物里有两整回合。
#    于是"修前 vs 修后"若直接比整份产物，就**不是同一个窗口**（2 回合 vs 1.7 回合）：
#    回合切换会把所有人传送回出生点 ⇒ net 位移坍缩、A1/A2 假性变红。
#    本脚本只做**切片**（不重解释、不改列、不补数据）：按 A 行的 round 列 / 日志行的 t 列
#    把同一份产物分成单回合文件，再喂给**同一个** bot-goal-gate.py ⇒ 逐回合口径可比。
#
#  用法：
#    python tools/probes/slice-round.py --rows <A行文件> --log <L3文件> --round 1 --out-prefix <前缀>
#  产物：
#    <前缀>-rows.tsv / <前缀>-log.tsv
# ============================================================================
import io
import os
import sys


def main():
    args = sys.argv[1:]
    rows_p = log_p = None
    rnd = None
    prefix = None
    for i, a in enumerate(args):
        if a == '--rows' and i + 1 < len(args):
            rows_p = args[i + 1]
        elif a == '--log' and i + 1 < len(args):
            log_p = args[i + 1]
        elif a == '--round' and i + 1 < len(args):
            rnd = args[i + 1]
        elif a == '--out-prefix' and i + 1 < len(args):
            prefix = args[i + 1]
    if not (rows_p and log_p and rnd and prefix):
        print('usage: --rows R --log L --round N --out-prefix P')
        return 2

    rows_out = prefix + '-rows.tsv'
    log_out = prefix + '-log.tsv'

    # ---- A 行：按第 3 列（round）切 ----
    n = 0
    t0 = t1 = None
    with io.open(rows_out, 'w', encoding='utf-8') as f:
        for line in io.open(rows_p, 'r', encoding='utf-8', errors='replace'):
            q = line.rstrip('\n').split('\t')
            if q[0] != 'A' or len(q) < 4:
                continue
            if q[3] != rnd:
                continue
            f.write(line if line.endswith('\n') else line + '\n')
            n += 1
            try:
                t = float(q[2])
            except Exception:
                continue
            t0 = t if t0 is None else min(t0, t)
            t1 = t if t1 is None else max(t1, t)

    # ---- L3 行：按 t 列落在该回合 A 行的 [t0,t1] 区间切（区间由同一份产物自己给出）----
    m = 0
    if t0 is not None:
        with io.open(log_out, 'w', encoding='utf-8') as f:
            for line in io.open(log_p, 'r', encoding='utf-8', errors='replace'):
                q = line.rstrip('\n').split('\t')
                if len(q) < 4:
                    continue
                try:
                    t = float(q[1])
                except Exception:
                    continue
                if t0 <= t <= t1:
                    f.write(line if line.endswith('\n') else line + '\n')
                    m += 1

    print('round=%s  rows=%d (%s..%s)  log=%d' % (rnd, n, t0, t1, m))
    print('  ' + os.path.abspath(rows_out))
    print('  ' + os.path.abspath(log_out))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
