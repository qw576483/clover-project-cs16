#!/usr/bin/env python
# -*- coding: utf-8 -*-
# ============================================================================
#
#  已知错误的样本必须 FAIL**，否则"只会报红 / 只会报绿"的检查项不许上线。
#
#  本脚本生成两份**合成 L3 产物**（列序与 bot-hold-plant.cs 完全一致）：
#    .ai-tmp/test/bu-r4-fixture-pass.tsv : 两个 CT **原地不动**但连续 41 采样都在包点 7m 内
#         ⇒ A1/A2 必须 **PASS**（"守得对就该静止"）。
#    .ai-tmp/test/bu-r4-fixture-fail.tsv : 一个 T **持包原地不动**、离包点 ~84m（site='-'）、
#         日志里 0 次下包 ⇒ A1/A2 必须 **FAIL** 且 A9 必须 **FAIL**。
#
#  这两份夹具不放进 Play、不改工程；它们是"判据本身"的证据。
#
#  用法：
#    python tools/probes/bot-goal-gate-fixtures.py
#    python tools/probes/bot-goal-gate.py --log <TMP>/bu-r4-fixture-pass-log.tsv --rows <TMP>/bu-r4-fixture-pass.tsv
#    python tools/probes/bot-goal-gate.py --log <TMP>/bu-r4-fixture-fail-log.tsv --rows <TMP>/bu-r4-fixture-fail.tsv
# ============================================================================
import io
import os

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(os.path.dirname(HERE))
TMP = os.path.join(PROJECT, '.ai-tmp', 'test')
SAMPLES = 41


def row(t, phase, nm, team, alive, x, z, bomb, dA, dB, site, planted):
    """A 行（列序 = tools/probes/bot-hold-plant.cs：0=A 1=frame 2=t 3=round 4=phase 5=Name
    6=Id 7=Team 8=IsBot 9=IsAlive 10=Health 11..13=pos 14=Yaw 15=HasBomb 16=UseProgress
    17=dA 18=dB 19=siteLabel 20=planted）。"""
    p = [''] * 21
    p[0], p[1], p[2], p[3], p[4] = 'A', '0', '%.3f' % t, '1', phase
    p[5], p[6], p[7], p[8], p[9], p[10] = nm, '1', team, '1', alive, '100'
    p[11], p[12], p[13], p[14] = '%.3f' % x, '0.000', '%.3f' % z, '0.000'
    p[15], p[16] = bomb, '-1.000'
    p[17], p[18], p[19], p[20] = '%.3f' % dA, '%.3f' % dB, site, planted
    return '\t'.join(p)


def main():
    if not os.path.isdir(TMP):
        os.makedirs(TMP)

    lines = []
    for k in range(SAMPLES):
        t = float(k)
        lines.append(row(t, 'Live', 'DwellA', 'CT', '1', 34.9, 29.5, '0', 1.2, 8.0, 'A', '0'))
        lines.append(row(t, 'Live', 'DwellB', 'CT', '1', 35.8, 38.4, '0', 2.0, 7.5, 'A', '0'))
    io.open(os.path.join(TMP, 'bu-r4-fixture-pass.tsv'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    io.open(os.path.join(TMP, 'bu-r4-fixture-pass-log.tsv'), 'w', encoding='utf-8').write(
        '0\t0.000\tLog\tfixture-pass: CT 只在包点内驻留，没有任何下包行（A5 预期 FAIL，A1/A2 与 A9 预期 PASS）\n')

    lines = []
    for k in range(SAMPLES):
        t = float(k)
        lines.append(row(t, 'Live', 'StillT', 'T', '1', -8.5, -48.5, '1', 89.0, 83.7, '-', '0'))
    io.open(os.path.join(TMP, 'bu-r4-fixture-fail.tsv'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    io.open(os.path.join(TMP, 'bu-r4-fixture-fail-log.tsv'), 'w', encoding='utf-8').write(
        '0\t0.000\tLog\tfixture-fail: 持包 bot 原地不动 40s 且离包点 84m（A1/A2 与 A9 预期 FAIL）\n')

    print('wrote:')
    print('  ' + os.path.join(TMP, 'bu-r4-fixture-pass.tsv'))
    print('  ' + os.path.join(TMP, 'bu-r4-fixture-fail.tsv'))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
