#!/usr/bin/env python
# -*- coding: utf-8 -*-
# ============================================================================
#
#  为什么需要它：
#    tools/probes/bu-r2-motion-diag.py 已经从逐帧位置证明"Move 方向每 ~30 帧精确翻 180°"，
#    但那只回答"现象"；本脚本读 BotNavigator.DiagFlip 在每个翻转现场记下的分支/游标/夹角，
#    回答"是**避障/逃逸**把方向反过来的，还是**路径游标**钉在'自己脚下那一格'上"。
#
#  输入：任一含产品 L3 的 dump（`.ai-tmp/test/<slice>-hold-plant-log.tsv`，bot-hold-plant 的 console dump）
#        行格式：<frame> \t <t> \t <level> \t <text>
#  用法：python tools/probes/bu-r2-flip-attrib.py [--log <path>]
# ============================================================================
import io
import os
import re
import sys

try:
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', errors='replace')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TMP = os.path.join(PROJECT, '.ai-tmp', 'test')

FIELDS = ('angDesiredVsDir', 'angDirVsTarget', 'angDirVsGoal', 'pathIdx', 'routeIdx', 'ownCell',
          'esc', 'probe', 'probeLeft', 'flips')


def num(text, key):
    m = re.search(key + r'=(-?[\d.]+)', text)
    return float(m.group(1)) if m else None


def main():
    logp = os.path.join(TMP, 'bu-r2-hold-plant-log.tsv')
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--log' and i + 1 < len(args):
            logp = args[i + 1]

    print('=== bu-r2-flip-attrib (slice BU-R2) ===')
    print('log : %s' % logp)
    if not os.path.exists(logp):
        print('MISSING input -> cannot attribute')
        return 1

    rows = []
    with io.open(logp, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            if '[BOTFLIP]' not in line:
                continue
            p = line.rstrip('\n').rstrip('\r').split('\t')
            rows.append((float(p[1]) if len(p) > 1 else 0.0, p[-1] if p else line))
    print('[BOTFLIP] lines = %d' % len(rows))
    if not rows:
        print('RESULT: no flip line -> either the jitter is gone, or the instrumentation never ran')
        return 0

    byname = {}
    for t, txt in rows:
        m = re.search(r'\[BOTFLIP\]\s+(\S+)\s+branch=(\S+)', txt)
        if not m:
            continue
        byname.setdefault(m.group(1), []).append((t, m.group(2), txt))

    print('')
    print('-- 1. 每 bot：翻转次数 / 分支分布 / 关键归因列 --')
    print('  %-10s %6s %-34s %10s %8s %8s' % ('actor', '翻转行', 'branch 分布', 'desired反向', 'ownCell', 'pathIdx0'))
    for nm in sorted(byname):
        rs = byname[nm]
        br = {}
        a180 = own = p0 = 0
        for t, b, txt in rs:
            br[b] = br.get(b, 0) + 1
            a = num(txt, 'angDesiredVsDir')
            if a is not None and a >= 150.0:
                a180 += 1
            if num(txt, 'ownCell') == 1:
                own += 1
            pi = re.search(r'pathIdx=(\d+)/(\d+)', txt)
            if pi and pi.group(1) == '0':
                p0 += 1
        n = len(rs)
        print('  %-10s %6d %-34s %8.1f%% %7.1f%% %7.1f%%' % (
            nm, n, ','.join('%s:%d' % kv for kv in sorted(br.items(), key=lambda kv: -kv[1])),
            100.0 * a180 / n, 100.0 * own / n, 100.0 * p0 / n))

    print('')
    print('-- 2. 全体汇总 --')
    allr = [r for nm in byname for r in byname[nm]]
    br = {}
    for _, b, _ in allr:
        br[b] = br.get(b, 0) + 1
    n = len(allr)
    a180 = sum(1 for _, _, txt in allr if (num(txt, 'angDesiredVsDir') or 0) >= 150.0)
    own = sum(1 for _, _, txt in allr if num(txt, 'ownCell') == 1)
    esc = sum(1 for _, _, txt in allr if num(txt, 'esc') == 1)
    print('  branch       : %s' % ', '.join('%s=%d(%.1f%%)' % (k, v, 100.0 * v / n)
                                            for k, v in sorted(br.items(), key=lambda kv: -kv[1])))
    print('  dir 与 desired 反向(>=150°) : %d/%d = %.1f%%' % (a180, n, 100.0 * a180 / n))
    print('  追的点 == 自己脚下那一格     : %d/%d = %.1f%%' % (own, n, 100.0 * own / n))
    print('  逃逸中(esc=1)               : %d/%d = %.1f%%' % (esc, n, 100.0 * esc / n))
    print('')
    print('RESULT: (attribution only; read the branch column to name the layer)')
    return 0


sys.exit(main())
