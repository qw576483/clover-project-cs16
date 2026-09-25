# agent-36 · 差异 #70（买枪界面）按原版载体重建

> 用户原话：「买枪界面ui不对， 选人界面ui不对。」（选人/选阵营那条已另片落地，⛔ 本片不管选阵营）。
> 本片只做**买枪界面**：把 `client/Assets/Scripts/UI/InGame/BuyMenuPanel.cs` 里**自建的布局常量**
> （`DialogWidth 1020 / DialogHeight 640 / CategoryY −104 / RowHeight 46 / …`）换成**原版载体**的几何。

## 一、第一步：查证原版载体（⛔ 不许跳过、⛔ 不许猜）

在盘的原版载体（都在 `原版资源/cs16src/cstrike/`）：
- `sprites/weapon_*.txt`（31 份）—— 逐字给出 320/640 两档下 `weapon` / `weapon_s` / `ammo` / `crosshair` / `autoaim`
  部件取自**哪张 HUD 精灵** + **源矩形**（格式示例：`weapon 640 640hud1 0 90 170 45`）。
- `cstrike__sprites__640hud1..20.spr`（20 张 256×256 图集）+ `cstrike__sprites__hud.txt`（元素清单）。
- `cl_dlls/client.dll`（1,093,128 B）—— CS 1.6 的买枪菜单**本体**（面板布局与文字串很可能硬编码在这里）。
- `cstrike__resource__ClientScheme.res` / `GameMenu.res` / `OptionsSubMultiplayer.res`。

**要回答的问题**：「原版买枪菜单的**面板几何**（宽高 / 行高 / 列位置 / 分类位置 / 图标尺寸）的载体在哪？」
- 若在 `hud.txt` 或 `weapon_*.txt` 里 ⇒ 逐字段搬运，写 `文件:行`。
- 若只在 `client.dll` 里 ⇒ 按降级链（可执行里的常量）把它取出来；取不出来的那部分**如实标 BLOCKED**
  并写清"试过什么、缺什么"，⛔ **不许继续拿自建常量冒充原版**（那正是本行要消除的东西）。

## 二、第二步：按载体改（改了哪几处，逐处写 `文件:行`）

- 至少要把**武器图标**换成原版精灵（`640hud*.spr` 的源矩形来自 `weapon_*.txt`），
  ⛔ 不许用文字／纯色块／自画图形代替原版图标。
- 分类 / 行 / 列 / 面板尺寸：有载体就照搬运；无载体 ⇒ BLOCKED 并保留自建值 + 在 `策划/差异登记.tsv` 的 #70 行写清"这部分仍无出处"。

## 三、判据（一条命令 + 原始输出 / 一张图；⛔ 不填表）

1. **数值类**：Play 里打印面板与每一行的**上屏矩形**（`RectTransform` 世界角点或屏幕矩形），
   与搬运来源的 `.res` / `hud.txt` 数值**逐项对齐**（差值 0 或写明换算比）。
2. **表现类**：同一帧截图落 `.ai-tmp/screenshots/`（1920×1080，`canvas scaleFactor=1`），
   并按项目既有做法拼进联络图（`contact-sheet-2-ingame.png` 的买枪格）。
3. 驱动复用项目既有 Play 链（`.ai-tmp/drivers/bz-ui.sh` 那条）。

## 四、边界

- 写入范围：`client/Assets/Scripts/UI/InGame/BuyMenuPanel.cs`、
  `client/Assets/Resources/UI/BuyMenuPanel.prefab`（若需按新几何重烘，走 `PrefabUtility` 定向补丁，⛔ 不重跑整个 `FlowSetup`）、
  `client/Assets/Resources/**`（新增原版图标）、`.ai-tmp/**`、`策划/差异登记.tsv` 的 #70 行、`docs/agents/agent-36-*.md`。
- ⛔ 不改引擎源码、⛔ 不改 skill、⛔ 不动选阵营 / 主菜单 / Options 面板。
- **另一条线正在清理以下文件的注释** ⇒ 本片不要写它们：
  `Core/CsWeapons.cs`、`Core/ResPaths.cs`、`UI/InGame/CsHudTheme.cs`、`UI/InGame/CsRadarWidget.cs`、`UI/Flow/CsUiStyle.cs`、
  `Module/Map/CsMap.cs`、`Module/Combat/*`、`Module/Bot/*`、`Module/Audio/*`、`Editor/VisualLeakGuard.cs`。
  （图标与色值**读**这些文件可以，⛔ 不要写；需要改色值 ⇒ 回报给我，别自己动。）
- 注释按 skill §3 第 3b 条：只写「现在是什么」，⛔ 不写批次 / 本轮 / 用户报的 / 日期 / 别的工程名。
