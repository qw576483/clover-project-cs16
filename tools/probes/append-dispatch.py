# -*- coding: utf-8 -*-
"""派活留痕写入器 —— **判据资产**（删了就不能可靠地补 dispatch-log 记录，见 skill §8 判据资产定义）。

为什么要有它：
- `dispatch-log.tsv` 的 scope 必须是**项目根相对**路径（如 `client/Assets/**,tools/**`）。
  写成 `clover-project-cs16/client/Assets/**` 会与闸门第 15 条的 `$rel -like ($scope + '*')`
  **全部对不上** ⇒ 整片文件被判"无派活留痕"（2026-09-21 实测踩过）。
- 时间戳必须取**系统当前时间**且**早于**执行者改动文件（闸门要求 派活时间 ≤ 文件 mtime），
  所以留痕必须在派活**之前**写。

列数（2026-09-23 修正 —— 本条曾是真缺陷）
----------------------------------------
闸门第 33 条 `no-sync-subagents` 对 marker 行 `# team-member-required-below-this-line`
**以下**的每一行要求 **≥6 个 tab 字段且第 5/6 列（team / member）非空**；本脚本原先只写
**4 列**（时间 / 谁 / 片名 / scope）⇒ 用它留痕的每一条派活都会被闸门判 FAIL
（2026-09-23 实测：三行派活整片 FAIL，只能事后逐行补列）。现在本脚本**默认写出 6 列**：

    时间  谁  片名  scope  team  member

用例（team/member 的来源，按优先级）：
  1) 命令行 `--team` / `--member`
  2) `current-slice.txt` 的第 3 行 / 第 4 行（可选）
  3) 默认 `cs16-fix` / `team-lead`（主 agent 自持 / 接手时用；**不许**留空 —— 空列即闸门 FAIL）

用法：
  1) 写 `.ai-tmp/test/current-slice.txt`（UTF-8）：
        第 1 行 = 片名（人可读，含一句话目标）
        第 2 行 = scope（逗号分隔的项目根相对路径）
        第 3 行 = team（可选）
        第 4 行 = member（可选）
  2) 在项目根跑：`python tools/probes/append-dispatch.py`
"""
import argparse
import datetime
import os

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
LOG = os.path.join(ROOT, '.ai-tmp', 'test', 'dispatch-log.tsv')
SRC = os.path.join(ROOT, '.ai-tmp', 'test', 'current-slice.txt')

DEFAULT_TEAM = 'cs16-fix'
DEFAULT_MEMBER = 'team-lead'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--team', default=None)
    ap.add_argument('--member', default=None)
    args = ap.parse_args()

    with open(SRC, encoding='utf-8') as f:
        lines = [l.rstrip('\n') for l in f if l.strip()]
    task = lines[0].strip()
    scope = lines[1].strip()
    team = (args.team or (lines[2].strip() if len(lines) > 2 else '') or DEFAULT_TEAM)
    member = (args.member or (lines[3].strip() if len(lines) > 3 else '') or DEFAULT_MEMBER)

    ts = datetime.datetime.now().strftime('%Y-%m-%d %H:%M')
    row = '\t'.join([ts, 'clover-impl', task, scope, team, member])
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    with open(LOG, 'a', encoding='utf-8') as f:
        f.write(row + '\n')

    # 回读核对：闸门第 33 条要求 >=6 列且第 5/6 列非空，这里当场自证（写歪了立刻看得见）
    with open(LOG, encoding='utf-8') as f:
        back = [l.rstrip('\n') for l in f if l.strip()]
    cols = back[-1].split('\t')
    ok = len(cols) >= 6 and cols[4].strip() and cols[5].strip()
    print(row)
    print('cols=%d team=[%s] member=[%s] gate33=%s'
          % (len(cols), cols[4] if len(cols) > 4 else '', cols[5] if len(cols) > 5 else '',
             'PASS' if ok else 'FAIL'))


if __name__ == '__main__':
    main()
