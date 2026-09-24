# 设施登记簿（clover-project-cs16）

## 网络消息号

**无** —— 本项目形态 = **单机**，不走 Clover 服务端，没有 C2S / 回包 / 推送消息号，也没有 `Def/MsgDef.cs`。
⛔ 别为了"对齐团队约定"去建 `Def/` 目录或消息号表（本项目没有联网需求）。

## 模块（`client/Assets/Scripts/Module/**`）

| 模块 | 类 | 门面 | 职责 |
| --- | --- | --- | --- |
| Flow | `AppFlow` | `IAppFlow` | 启动 → 主菜单 → New Game → 读条 → 进图 → 暂停 → 回主菜单 |
| Match | `MatchModule` | `ICsMatch` | 回合 / 经济 / 买枪 / 伤害 / 炸弹 / 死亡复活 / 比分（核心契约） |
| Map | `CsMapModule` | `ICsMap` | `de_dust2` 位图 / 阻挡 / 标记点 |
| Player | `PlayerModule` | — | 第一人称移动 + 相机 rig（自驱动） |
| Combat | `CombatModule` | — | 开火 / 换弹 / 后坐力 / 命中判定 |
| Bot | `BotModule` | — | 机器人 AI（巡逻/寻敌/交战/买枪/下包拆包） |
| View | `ViewModule` | — | 角色视图 / 名字牌 / 第一人称武器视图 |
| Audio | `AudioModule` | — | 脚步 / 枪声 / 回合播报 / 炸弹音效 |

启动装配：`client/Assets/Scripts/App/Bootstrap.cs`（`Game.Launch` → `CloverRes.Init` → `CloverInput.Init`）。

## 面板（`client/Assets/Scripts/UI/**`，预制体在 `Assets/Resources/UI/{类名}`）

| 面板 | 文件 | 预制体 | 职责 |
| --- | --- | --- | --- |
| `BootPanel` | `UI/Flow/BootPanel.cs` | ✅ | 启动画面 |
| `MainMenuPanel` | `UI/Flow/MainMenuPanel.cs` | ✅ | 主菜单（含底部逐字署名 `by clover-engine`） |
| `ServerListPanel` | `UI/Flow/ServerListPanel.cs` | ✅ | Find Servers（单机下空列表） |
| `NewGamePanel` | `UI/Flow/NewGamePanel.cs` | ✅ | 地图 / 规则 / bot 设置 |
| `OptionsPanel` | `UI/Flow/OptionsPanel.cs` | ✅ | Audio/Video/Mouse/Keyboard/Multiplayer |
| `LoadingPanel` | `UI/Flow/LoadingPanel.cs` | ✅ | 读条 |
| `TeamSelectPanel` | `UI/Flow/TeamSelectPanel.cs` | ✅ | 选阵营 |
| `PausePanel` | `UI/Flow/PausePanel.cs` | ✅ | ESC 菜单 |
| `HudPanel` | `UI/InGame/HudPanel.cs` | ✅ | 血量/护甲/金钱/弹药/**右上比分与计时**/买枪区提示 |
| `BuyMenuPanel` | `UI/InGame/BuyMenuPanel.cs` | ✅ | B 键买枪 |
| `HMenuPanel` | `UI/InGame/HMenuPanel.cs` | ✅ | H 菜单（机器人管理 / 回合设置） |
| `ScoreboardPanel` | `UI/InGame/ScoreboardPanel.cs` | ✅ | TAB 记分板 |
| `RoundEndPanel` | `UI/InGame/RoundEndPanel.cs` | ✅ | 回合结算 |
| `MatchEndPanel` | `UI/InGame/MatchEndPanel.cs` | ✅ | 比赛结束 |
| `RadioMenuPanel` | `UI/InGame/RadioMenuPanel.cs` | ✅ | 无线电 |
| `ConsolePanel` | `UI/InGame/ConsolePanel.cs` | ✅ | 控制台（`/` 触发，引擎无 `BackQuote`） |

> 子组件（非独立面板）：`CsCrosshairWidget` / `CsRadarWidget` / `CsKillFeedWidget` / `CsSpectatorWidget` / `CsDamageIndicatorWidget`
> / `CsHudTheme`（配色与字号真源）/ `CsUiStyle`（流程面板样式）/ `CsIngameStats` / `CsIngameCursor` / `CsLogBuffer`。

## 生成器入口（`client/Assets/Editor/**`，菜单 + 命令行双入口）

| 菜单 | 类 | 命令行入口 | 产出 |
| --- | --- | --- | --- |
| `Clover/CS16/生成流程场景与面板` | `Flow/FlowSetup.cs` | `FlowSetup.GenerateFromCommandLine` | Boot/Menu 场景 + 8 个流程面板预制体 |
| `Clover/CS16/生成游戏内面板` | `UiGenInGame/UiBuilder.cs` | `UiBuilder.GenerateFromCommandLine` | 8 个游戏内面板预制体（**含右上比分块与秒表图标**） |
| `Clover/CS16/整理视图与音效资源` | `Views/ArtSetup.cs` | `ArtSetup.GenerateFromCommandLine` | 角色/视模型预制体（骨骼 + SkinnedMeshRenderer + AnimationClip + AnimatorController） |
| `Clover/CS16/生成 de_dust2 场景` | `MapGen/Dust2Builder.cs` | `Dust2Builder.GenerateFromCommandLine` | `StageDust2` 场景几何（21 材质组） |
| `Clover/CS16/烘焙 de_dust2（导出 .bytes）` | `MapGen/MapBakeRunner.cs` | — | `Assets/MapData` + `Resources/MapData/de_dust2.bytes` |
| `Clover/CS16/地图连通性自证（de_dust2）` | `MapGen/MapConnectivityProbe.cs` | `RunProbeFromCommandLine` | 连通性日志（只读） |
| `Clover/CS16/校验流程产出（只读）` / `校验游戏内面板（只读）` / `校验视图与音效资源（只读）` | 同上 | — | 只读自检 |
| `Clover/自检/比赛核心模拟（快进 90s）` | `MatchSelfTest.cs` | — | 比赛逻辑离线自检 |

## Editor 横切设施（`client/Assets/Editor/**`，非生成器；切片BV 2026-09-22 新增）

| 设施 | 文件 | 触发 | 职责 |
| --- | --- | --- | --- |
| Game view 图标叠加层守卫 | `VisualLeakGuard.cs`（`[InitializeOnLoad]` + `playModeStateChanged`，131 行） | 进 Play 自动；另有菜单 `Clover/CS16/关闭 Game view 图标叠加层` | 把 Game view 的 **`m_Gizmos` + `showGizmos` + `drawGizmos` 三个成员一起**写 `false` ⇒ 消除 Unity **编辑器叠加**在画面上的组件图标（喇叭 = `AudioSource`、太阳 = `Light`）与 `OnDrawGizmos` 线框。⛔ **只写 `showGizmos` 不生效**（实测 A/B 两张 PNG 的 MD5 完全相同 ⇒ 画面 0 像素变化），必须三个一起写。⛔ 它是**编辑器层兜底**：打包版没有这层叠加；用户手动再点开 Gizmos 仍会看到图标（要彻底消除需给图标宿主设 `hideFlags`，见 `策划/差异登记.tsv` #78）。 |

> **取证注意（切片BV 实测）**：`unity command capture_game_view --source camera|screen` 采的是**游戏自己的后缓冲**，**编辑器叠加层不在里面** —— 所以"用户看得见、截图里从来没有"。
> 要看编辑器叠加必须走屏幕级采集：`tools/probes/capture-editor-screen.cs`（**必须在 Unity 进程内**跑：本机编辑器以管理员启动，非提权进程 `SetWindowPos` 返回 False/UIPI）+ `tools/probes/diff-ab.py`（A/B 差集数字 + 可视化）。

## Play 驱动约定（`.ai-tmp/drivers/`；切片BU-R2 2026-09-22 实测教训）

| 约束 | 为什么 | 出处 |
| --- | --- | --- |
| ⛔ 驱动**不得无条件**调 `Cs16Drv.Entry.StartBots()` / `EmitLaunch` | New Game 面板**默认已 4v4**（L3 `bots/队=4`）⇒ 二次 `LaunchMatch` 走 `CsMatch.Start` 的"重复调用 = 先 Stop 再 Start"契约 ⇒ **掐掉正在跑的回合**（实测该回合只活 **22.5 s**，而配置 ≥114 s = `FreezeTime 4` + `RoundTime 105` + `RoundEndTime 5`）⇒ 所有"回合内位移 / 下包 / 驻留"数字**全部失真** | `.ai-tmp/test/bu-r2-round-truth.tsv`；`client/Assets/Scripts/Module/Match/CsMatch.cs` 的 `Start` 契约 |
| 用 `Cs16Drv.Entry.StartBotsIfNeeded`（`BotCount ≥ 8` 就不补 Launch） | 同上 | `.ai-tmp/drivers/bu-r2-play.ps1` |
| 采集窗口以"探针检到 `phase=RoundEnd`"为准（硬上限 165 s），⛔ 不用固定秒数 | 固定 93 s 窗口会把"回合被掐"误读成"回合时长 ≈31 s"（93/3 的算术平均），进而误导出"要下调验收阈值"的错误结论 | 同上 |
| 进 Play 后断言 `Application.isPlaying == true`；采到空数据（`no local/no match`）**立刻作废该帧** | 同机并发改 `.cs` ⇒ Unity 域重载 ⇒ 掐掉别人正在跑的 Play | `patterns/multi-agent.md`（"同机并发撞车"） |
| ⛔ 不许为了过判据去改 `RoundTime` / 时间缩放 / 验收阈值 | 那是迎合判据（`reference/anti-gaming.md` 点名的作弊形态） | 主 agent 裁决 2026-09-22 |

## 数值表（本项目的"配表"形态）

| 表 | 文件 | 内容 | 出处标注 |
| --- | --- | --- | --- |
| `CsConst` | `Core/CsConst.cs` | 全部玩法常量（经济/回合/炸弹/伤害/玩家/相机/武器切换/手雷/机器人） | 逐条带 `server.cfg:行` 或 `mp.dll:偏移` 或 `hw.dll.orig:偏移` |
| `CsWeapons` | `Core/CsWeapons.cs` | 32 条武器定义（价格/伤害/射速/弹匣/备弹/后坐力/击杀奖励…） | 逐行带 `mp.dll:0x10f7xx`（`WeaponInfo[]`） |
| `CsMatchConfig` | `Core/CsMatchConfig.cs` | 一局比赛的运行参数（回合数 / 阵营 / 友好伤害…） | — |

## Core 横切设施（`client/Assets/Scripts/Core/**`；切片 sink4 2026-09-24 新增）

| 设施 | 文件 | 形态 | 契约 / 职责 |
| --- | --- | --- | --- |
| `CsClock` | `Core/CsClock.cs` | 静态可注入时钟 | **玩法时间的唯一入口**：`Now`/`Delta` 默认为**引擎 Tick 注入的 dt**（`EngineRunner.cs:207`），宿主在 `MatchModule.Update` 调 `Drive()`；离线/自检用 `Inject(now,delta)`（可 `Restore`）；`NowSource`/`DeltaSource` 可查当前来源。⛔ 玩法路径**不许**直接读 `Time.time`/`Time.deltaTime`（表现层白名单见切片 `sink-combat-clock` 的 TSV）。判定入口：玩法文件里墙钟命中必须为 0（白名单 = 只有 `CsClock.cs` 自己） |
| `CsSettingsKeys` | `Core/CsSettingsKeys.cs` | 常量类（唯一真源） | **设置键名的唯一真源**：`UI/Flow/CsPlayerSettingsStore`（18 处）、`Module/Player/PlayerModule`（5 处）、`Module/Audio/CsAudioTuning`（2 处）一律引用它；⛔ 任何地方**不许再写字面量键名**（两处维护 ⇒ 改一处漏一处，静默丢设置） |
| `CsRng` | `Core/CsRng.cs` | 静态唯一入口 + 可注入种子 | 本局随机源的**唯一入口**：`BeginMatch(seed)` 定种子并清各子流、`InjectSeed(seed)` 注入且**黏**（不随后续 `BeginMatch` 漂移）；13 路 `CsRngStream` **一路一 salt**，bot 再按 `actor id` `Derive`；未定种子时首次取流**显式**走引擎 `Rng.FromTime` 并打含 seed 的 `Info` 留痕 |

- ⛔ **业务代码不得直接调 `UnityEngine.Random`**（`value` / `Range` / `insideUnitSphere` / `ColorHSV` 等）—— 玩法随机（出生朝向、散布、后坐力、bot 决策）一律经 `CsRng` 的对应子流；自检/测试用固定种子（如 `BotSelfTest` = `20260924`）。理由：裸 `Random` 取全局时间种子 ⇒ **同一局不可复现**，任何"同 seed 同结果"的判据都无法成立。
- 判定入口：`Scripts` 树内**非注释**的 `Random.` 命中必须为 **0**（仓内自检 `sink4-cs16-random-selfcheck.ps1` 持此判据，并含负控）。

## 原版载体（`原版资源/**`，⛔ 只读；不进 git、不进交付）

| 子目录 | 是什么 | 用途 |
| --- | --- | --- |
| `cs16src/` | CS 1.6 客户端解包产物（`cs16game/app/**`）+ 解包脚本 + ISO + HLSDK | 模型/贴图/音效/`mp.dll`/`client.dll`/`hw.dll` 的源头；`cs16_build.py`、`cs16_anim.py`、`cs16_asset_extract.py` |
| `cs109/` | `de_dust2.bsp` 等地图 | 几何 / 实体 / miptex 解析 |
| `cs16-maps/` | 地图合集（含 31 张 1920×1080 原版截图） | HUD / 地图参考 |
| `cs_16_maps/` | 社区地图集 | 参考 |
| `de_dust2/`、`de_dust2_scene/` | 沙漠贴图 / 场景贴图集 | 回落贴图来源 |
| `cs-docker/` | 社区 docker 配置（⛔ 不算原版，不采信其 cvar） | — |
| `解包产物/` | 本项目的解析/导出产物（数值表 / HUD 布局 / 天空盒 / HUD 图标 / 贴图） | 供工程消费 |

## 分析产物（原版值的落盘处）

| 文件 | 内容 |
| --- | --- |
| `策划/对照表.md` | **元素 \| 原版值（含出处）\| 我们的值 \| 差值 \| 类别**（G/M/N/P/T/U/W 七块 + BLOCKED 节） |
| `原版资源/解包产物/原版数值表.md` | `mp.dll` cvar 默认值 + 武器表 + 引擎 `sv_*` + 复现脚本 |
| `原版资源/解包产物/原版HUD布局.md` | `client.dll` 的 HUD cvar + 原版截图像素量化 + BLOCKED-D1~D6 |
| `策划/素材调研.md` | 素材获取历程（来源 / 格式 / 为什么可用） |
| `原版资源/清单.md` | 8 个子目录的来源 / 解包命令 / 导出内容 / 复制目标（换机可复现） |
