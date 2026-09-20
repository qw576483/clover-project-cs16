# agent-33：把工程内 Resources 路径收敛到 `Core/ResPaths.cs`

> 项目：`clover-project-cs16`。**纯项目侧、纯重构**（值一字不改）。
> 背景：skill §8「原版资源」硬约定 —— 工程内素材必须复制进 `Assets/Resources/**`，**路径收敛到 `Core/ResPaths.cs`**。现状是**这个文件不存在**，路径常量散落在 5+ 个文件里。

## 0. 现状（已核实，别重查）

`client/Assets/Scripts/Core/` 下只有 `CsConst.cs` / `CsEnums.cs` / `CsHudSnapshot.cs` / `CsMatchConfig.cs` / `CsWeapons.cs` / `Events.cs`，**没有 `ResPaths.cs`**。

散落的路径常量：

| 位置 | 常量 | 值 |
|---|---|---|
| `Core/CsConst.cs:219` | `MapDataResourcePath` | `"MapData/de_dust2"` |
| `Module/Map/CsMap.cs:39` | `MarkersResourcePath` | `"MapData/de_dust2_markers"` |
| `Module/Audio/SfxService.cs:36` | `SoundRoot` | `"Sound/SFX/"` |
| `Module/Audio/CombatAudio.cs:29` | `SoundRoot` | `"Sound/SFX/"` ← **同一个值与名字重复定义两遍** |
| `UI/InGame/CsHudTheme.cs:99` | `HealthIconPath` | `"UI/Art/hud_cross"` |
| `UI/InGame/CsHudTheme.cs`（另一处） | `StopwatchIconPath` | `"UI/Art/stopwatch"` |
| `UI/Flow/MainMenuPanel.cs:59/62` | `LogoNormalPath` / `LogoHoverPath` | `"UI/Art/game_menu"` / `"UI/Art/game_menu_mouseover"` |

工程内实际素材（`client/Assets/Resources/UI/Art/`）：`game_menu.tga`、`game_menu_mouseover.tga`、`logo_game.tga`、`hud_cross.png`、`hud_suit_full.png`、`hud_suithelmet_full.png`、`stopwatch.png`。

## 1. 要做的

### 1.1 新建 `client/Assets/Scripts/Core/ResPaths.cs`

- `namespace Cs16.Core`，`public static class ResPaths`（**类的存在理由写进 XML 注释**：skill §8 要求路径收敛，改路径只改这一处）。
- 把上表的值**逐字搬过来**（⛔ 一个字符都不许改，包括斜杠的有无 —— `"Sound/SFX/"` 带尾斜杠、`"UI/Art/game_menu"` 不带扩展名、`"MapData/de_dust2"` 不带扩展名）。
- **按用途分组并写清每组的注释**（例如：地图数据 / 音效 / HUD 图标 / 菜单贴图），每组注明"该路径在 `Resources/` 下的哪个目录"。
- 对 `SoundRoot` 这种"前缀 + 名字"的用法，**保留一个前缀常量**并说明用法（例如 `SoundSfxPrefix = "Sound/SFX/"`），⛔ 不要为了"看起来整齐"把它拆成目录 + 名字两段（那会改变调用点语义）。

### 1.2 把上表的引用点改成 `ResPaths.X`

- 逐个改**引用点**（不是只改定义处）；
- 原常量若被**别的文件**引用（`public const`），改成 `ResPaths.X` 并在回报里列出每个改动点（`文件:行`）；
- ⛔ **行为零变化**：拼出来的最终路径字符串必须与改前**逐字相同** —— 回报里要给出"改前 / 改后"的**实际路径样本对照**（至少：地图、标记表、一个音效、一个 HUD 图标、两张菜单贴图），证明字符串逐字一致。
- ⛔ 不许顺手改任何数值/尺寸/颜色/文案。

### 1.3 顺手清掉重复定义

`SfxService.SoundRoot` 与 `CombatAudio.SoundRoot` 是**同值同名两份** ⇒ 都改成 `ResPaths.SoundSfxPrefix`（这是 §4.4「已有能力不准再起第二套」的同类问题）。

## 2. 判据（自己跑，原始输出贴回报）

1. `Test-Path` 证明 `Core/ResPaths.cs` 存在；
2. `Select-String` 全项目搜 `"MapData/|"Sound/SFX/|"UI/Art/` ⇒ 命中**只应出现在 `ResPaths.cs`**（注释里提路径的除外，要逐条列明）；
3. **路径字符串对照表**（改前 vs 改后，证明逐字相同）；
4. 编译：`.ai-tmp/test/compile-check.ps1`（本工程既有配方）⇒ `exit=0`；
5. `tools/verify.ps1` ⇒ **FAIL=0**（若因改动让某行证据过期，只重采受影响的行并在回报里写明第几次重采；⛔ **本片预计不涉及表现类证据**，因为纯重构不动视觉）。

## 3. 不许

- ⛔ 不许改引擎 `clover-client-unity-engine/**`；⛔ 不许改 `策划/**`、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；
- ⛔ 不许改任何 `Assets/Resources/**` 下的素材文件与 `.meta`；⛔ 不许改任何 asmdef；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；⛔ 不许新增 README/交接类 md；临时脚本只放 `.ai-tmp/test/` 用完删；
- ⛔ 不许"顺手优化"（本次只搬路径，别的一律不动）。

## 4. 回报格式

```
产出物：<文件清单>
1.1：ResPaths.cs 的常量清单（名 / 值 / 用途注释）
1.2：引用点改动清单（文件:行，逐条）+ 路径字符串改前/改后对照
1.3：SoundRoot 重复的两处改法
2.2 判据：残留命中（应只在 ResPaths.cs 内）
编译：<命令 + 末尾 + 退出码>
闸门：<verify.ps1 逐行>
未决：无 / <具体条目>
```
