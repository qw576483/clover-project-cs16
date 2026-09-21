# -*- coding: utf-8 -*-
"""派活留痕写入器 —— **判据资产**（删了就不能可靠地补 dispatch-log 记录，见 skill §8 判据资产定义）。

为什么要有它：
- `dispatch-log.tsv` 的 scope 必须是**项目根相对**路径（如 `client/Assets/**,tools/**`）。
  写成 `clover-project-cs16/client/Assets/**` 会与闸门第 15 条的 `$rel -like ($scope + '*')`
  **全部对不上** ⇒ 整片文件被判"无派活留痕"（2026-09-21 实测踩过）。
- 时间戳必须取**系统当前时间**且**早于**执行者改动文件（闸门要求 派活时间 ≤ 文件 mtime），
  所以留痕必须在派活**之前**写。

用法：
  1) 写 `.ai-tmp/test/current-slice.txt`（UTF-8）：
        第 1 行 = 片名（人可读，含一句话目标）
        第 2 行 = scope（逗号分隔的项目根相对路径）
  2) 在项目根跑：`python tools/probes/append-dispatch.py`
"""
import datetime
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LOG = os.path.join(ROOT, '.ai-tmp', 'test', 'dispatch-log.tsv')
SRC = os.path.join(ROOT, '.ai-tmp', 'test', 'current-slice.txt')


def main():
    with open(SRC, encoding='utf-8') as f:
        lines = [l.rstrip('\n') for l in f if l.strip()]
    task = lines[0].strip()
    scope = lines[1].strip()
    ts = datetime.datetime.now().strftime('%Y-%m-%d %H:%M')
    row = '\t'.join([ts, 'clover-impl', task, scope])
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(row + '\n')
    print(row)


if __name__ == '__main__':
    main()
