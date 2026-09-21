# Clover × Counter-Strike 1.6

用 [Clover 客户端引擎](https://github.com/qw576483/clover-client-unity-engine) **1:1 复刻《反恐精英 1.6》单机模式**的工程。

地图 **de_dust2**；复刻范围按六类系统：**A 启动菜单 · B HUD · C 玩法 · D 机器人 · E 地图 · F 资源**。

> **本工程尚未完成**：仍在开发中，功能与画面都在变 —— 下面的截图、范围与说明只反映当前进度，不等于最终形态。

## 实机画面

| 主菜单 | 队伍选择 |
|---|---|
| ![主菜单](docs/images/33_mainmenu.png) | ![队伍选择](docs/images/33_teamselect.png) |

| 游戏内（A 点） | HUD（1920×1080） |
|---|---|
| ![A 点](docs/images/60_bombsite_a.png) | ![HUD](docs/images/23_hud_1920.png) |

> 截图取自本工程复刻版的**实际运行画面**（非原版素材）。

## 怎么玩

1. Unity **6000.x** 打开 `client/`；
2. 进 Play：主菜单 → 新游戏 → 选阵营 → 进 de_dust2；
3. 引擎包 `com.clover.unity-engine` 由 UPM 自动从 [clover-client-unity-engine](https://github.com/qw576483/clover-client-unity-engine) 拉取（首次打开联网克隆，之后走本地缓存）—— **需本机已装 Git 且在 `PATH` 里**。

## 工程结构

| 路径 | 内容 |
|---|---|
| `client/` | Unity 工程（复刻本体；`Library/` `Temp/` `Logs/` 等生成物不入库） |
| `策划/` | 复刻规格（`策划案/CS1.6单机参考规格.md`）、对照表、验收表、原版基线图（dust2 自由视角 / HUD / 天盒贴图） |
| `tools/` | 判据与工具：`verify.ps1`、探针与量法脚本 |
| `docs/` | 步骤文档与并行任务书（`agents/`） |

## 声明

本项目**仅供技术交流与学习**，禁止用于任何商业用途。

## 相关仓库

| 仓库 | 说明 |
|---|---|
| [clover-client-unity-engine](https://github.com/qw576483/clover-client-unity-engine) | 客户端引擎（本工程的运行底座） |
| [clover-doc](https://github.com/qw576483/clover-doc) | 框架文档 |
| [clover-tools](https://github.com/qw576483/clover-tools) | 打表工具 |
| [clover-ai-skill](https://github.com/qw576483/clover-ai-skill) | AI 交付 skill（本工程按它的规范做） |
