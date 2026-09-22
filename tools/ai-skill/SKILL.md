# clover-project-cs16 · 项目级 skill

> **动本项目代码前先读这里**。本目录只记**本项目特有**的东西；通用规则看全局 `clover-engine` skill，
> 引擎权威说明看 `clover-doc/`。
> ⛔ **全局 skill 的规则层（其 `SKILL.md` §0~§7）不可被本文件覆盖**：本文件只能在
> 「全局没写 / 全局明说可自选」的地方定约定（技术选型、命名、目录细分）。
> 凡全局标了 `⛔` / **硬规则** / **硬闸门** 的，**本文件只许加严、不许放宽**；
> 冲突时照全局做，并把本文件的冲突行改掉。完整层级见全局 `SKILL.md` §1.10。

本项目：**Counter-Strike 1.6（单机版）的 1:1 复刻** —— Unity 6 客户端在 `client/`，**形态 = 单机**（没有 `server/`，不调 `CloverNet.Init`，`Game.Net` 恒为 null）。
参考游戏 A = **CS 1.6**（Valve 2003）；规格真源 = `策划/策划案/CS1.6单机参考规格.md`；验收 = `策划/验收表.md`；原版值对照 = `策划/对照表.md`。

**怎么跑**：① Unity Hub 打开 `client/` → 点 Play；② 离线编译检查（不启编辑器，几十秒，**判据资产**）：`powershell -NoProfile -ExecutionPolicy Bypass -File clover-project-cs16\tools\probes\compile-check.ps1` —— ⚠️ **`-File` 必须带 `clover-project-cs16\` 前缀，而 cwd 仍须在工作区根（`clover-project-cs16` 的父目录）**：脚本里 `$projRoot = 'clover-project-cs16'` 是**相对路径**（相对 cwd），而**工作区根本身没有 `tools/`**（实测 2026-09-22：工作区根下只有并排的 `clover-*` 仓库）⇒ 旧写法 `-File tools\probes\compile-check.ps1` 会直接报"找不到路径"；③ 一键复检闸门：`powershell -NoProfile -ExecutionPolicy Bypass -File clover-project-cs16\tools\verify.ps1`（同口径：前缀 + cwd 在工作区根）；④ 派活留痕：把片名与 scope 写进 `.ai-tmp/test/current-slice.txt` 后跑 `python tools/probes/append-dispatch.py`（scope 必须是**项目根相对**路径，否则闸门第 15 条对不上）。

## 索引

| 想找什么 | 去哪 |
| --- | --- |
| 单机形态 / 数值口径 / 命名 / 目录边界 | `conventions.md` |
| 面板 / 模块 / 生成器入口 / 数值表 / 原版载体 | `registry.md` |
| 引擎与平台约束、静默失败风险、未决问题 | `constraints.md` |
| 原版值怎么解析出来的（带偏移） | `原版资源/解包产物/原版数值表.md`、`原版资源/解包产物/原版HUD布局.md` |

## 四条硬规则

1. **新设施先登记再写代码**：新增面板 / 模块 / 生成器入口 / 数值常量 → **同步更新 `registry.md`**。
2. **只记本项目特有的东西**，通用规则**不许**抄进来（抄了必然两边漂移）。
3. ⛔ **本文件只许对全局 skill "加严"，不许"放宽"**：冲突时**以全局为准**，并把本文件的冲突行改掉。
   ⛔ 特别不许"项目化"放宽的几条：临时文件位置（全局 §1.8：一律 `<项目根>/.ai-tmp/`，⛔ 不许 `client/_dev/`、项目根、`tools/`）、
   交付闸门（§2）、收尾机械自检（§1.11）、引擎能力优先（§6）、**禁写条款**（§0.5：原版已有的东西只能解析搬运，写不出出处不许进工程）。
4. **子 agent 不会自动继承 skill**：派子 agent 时任务书里必须写明「怎么拿到本 skill」——
   首选让它读 `<项目根>/tools/ai-skill/SKILL.md`，再给全局 `clover-engine` 与 `unity-cli` 的入口路径。
