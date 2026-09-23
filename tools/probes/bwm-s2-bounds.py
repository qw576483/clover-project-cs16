# -*- coding: utf-8 -*-
"""bwm-s2-bounds.py -- 从**原始产物**读出 S2「帧时间」的四个次序统计量（判据资产 / 量法脚本）。

为什么要有这份脚本（片BW-M，2026-09-23）
----------------------------------------
`策划/状态矩阵.tsv` 的 S2 帧时间四行（0 负载 / 标称 / 上限 / 上限+1）里写着 min / p50 / p95 / max
四个数。为了不让那四个数变成"执行者手写的常量"（skill 铁律 3：写不出出处的量不许进工程），
这里把它们定义成**输入的确定性函数**：输入 = Play 探针 `tools/probes/measure-play-frametime.cs`
落盘的逐帧样本（`.ai-tmp/test/perf-result.txt`），输出 = 四个分位。

口径（写死，避免"换了算法数值就变"）
------------------------------------
* 只取逗号分隔的第 1~5 列（unscaledDeltaMs / cpuFrameTimeMs / gpuFrameTimeMs /
  cpuMainThreadMs / cpuRenderThreadMs）；`unscaledDeltaMs` 表头行与 `frames=N elapsedSec=...`
  汇总行不是数据行，跳过。
* 分位数 = **线性插值**（`numpy.percentile` 的默认口径）：`k=(n-1)*q`，在 `sorted[k]` 与
  `sorted[k+1]` 之间插值。p50/p95 因此可复算（本工程 n=197 ⇒ p50 恰为第 99 个样本）。
* ⛔ 只读：不写任何共享产物；输出到 stdout。
* ⛔ 输入用**绝对路径**（`ROOT` 由本文件位置推）—— 相对路径会随 CWD 读到别处（实测代价见
  skill `reference/anti-gaming.md` 第 5 条第 4 项）。

用法：
    python tools/probes/bwm-s2-bounds.py
退出码：0 = 读到并打印；1 = 原始产物不在盘（此时状态矩阵那四行的数字**不可复核**）。
"""
import io
import os
import sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')
HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SRC = os.path.join(ROOT, '.ai-tmp', 'test', 'perf-result.txt')
COLUMNS = ['unscaledDeltaMs', 'cpuFrameTimeMs', 'gpuFrameTimeMs', 'cpuMainThreadMs',
           'cpuRenderThreadMs']


def pct(values, q):
    """线性插值分位（与 numpy.percentile 默认口径一致）。"""
    v = sorted(values)
    if not v:
        return float('nan')
    k = (len(v) - 1) * q
    f = int(k)
    c = min(f + 1, len(v) - 1)
    return v[f] + (v[c] - v[f]) * (k - f)


def load(path):
    rows = []
    for ln in io.open(path, encoding='utf-8', errors='replace').read().split('\n'):
        ln = ln.strip()
        if not ln or ln.startswith('unscaled') or ln.startswith('frames='):
            continue
        cells = ln.split(',')
        if len(cells) < 5:
            continue
        try:
            rows.append([float(x) for x in cells[:5]])
        except ValueError:
            continue
    return rows


def main():
    print('INPUT ' + SRC)
    if not os.path.exists(SRC):
        print('MISSING: the raw frame-time product is not on disk -> the four S2 numbers '
              'cannot be re-checked')
        return 1
    rows = load(SRC)
    if not rows:
        print('MISSING: no parsable sample line in ' + SRC)
        return 1
    print('samples = %d' % len(rows))
    for i, nm in enumerate(COLUMNS):
        v = [r[i] for r in rows]
        print('%-18s n=%3d min=%8.3f p50=%8.3f p95=%8.3f max=%9.3f mean=%8.3f'
              % (nm, len(v), min(v), pct(v, .5), pct(v, .95), max(v), sum(v) / len(v)))
    tail = [r[1] for r in rows][1:]
    if tail:
        print('cpuFrameTimeMs excluding the first sample: max=%.3f (first sample=%.3f)'
              % (max(tail), rows[0][1]))
    return 0


if __name__ == '__main__':
    sys.exit(main())
