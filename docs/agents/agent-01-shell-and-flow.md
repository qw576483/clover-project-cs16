# agent-01：客户端骨架 + 启动/菜单全链路

## 0. 技能（开工必做，不许跳）

```
(1) 拿到 skill：工具集里有 use_skill 就用它加载 clover-engine + unity-cli；
    没有就直接读文件（命中即用，不必读完）：
      · c:/Work/Server/full-dev/clover-project-cs16/tools/ai-skill/SKILL.md   ← 项目级，首选
      · c:/Work/Server/full-dev/clover-tools/ai-skill/SKILL.md                ← 仓库源
      · ~/.codebuddy/skills/ai-skill/SKILL.md                                 ← 安装副本
    （unity-cli 同理，目录名换成 unity-cli/）
    都找不到 → 回报调用方要路径，**不许凭记忆写代码**。
(2) 然后按 skill 的「混合模式找依据」查代码（用户 > 引擎 > 联网/自创），**不许编 API**。
```

**必读**（按序）：
- `c:/Work/Server/full-dev/clover-project-cs16/docs/步骤文档.md` §3（关键契约速查）
- `clover-tools/ai-skill/patterns/client/app-flow.md`（本任务的主范式，**照抄结构**）
- `clover-tools/ai-skill/reference/architecture.md`（分层铁律）
- `clover-tools/ai-skill/reference/engine-mental-model.md` §1/§2（模块谁挂、生命周期）
- `clover-tools/ai-skill/patterns/client/config.md`、`ui.md`
- 契约文件（**只读，不许改**）：`Assets/Scripts/Core/*.cs`、`Assets/Scripts/Module/Match/ICsMatch.cs`

## 1. 目标

让工程"打开点 Play 就能走完整启动链路"：启动画面 → 主菜单（New Game / Find Servers / Options / Quit）→ New Game 配置 → 读条 → 进 StageDust2；游戏内 ESC 暂停 → 回主菜单 → **能再进一次**。单机版：**不调 `CloverNet.Init`**。

## 2. 任务边界

**只做**（这些路径只有你能写）：
- `Assets/Scripts/App/**`
- `Assets/Scripts/Module/Flow/**`
- `Assets/Scripts/UI/Flow/**`
- `Assets/Editor/Flow/**`
- `Assets/Scenes/Boot.unity`、`Assets/Scenes/Menu.unity`
- `Assets/Resources/UI/{BootPanel,MainMenuPanel,ServerListPanel,NewGamePanel,OptionsPanel,LoadingPanel,TeamSelectPanel,PausePanel}.prefab`

**绝不做**：
- 不许改 `Assets/Scripts/Core/**`、`Assets/Scripts/Module/Match/**`、`Assets/Scripts/Module/Map/**` 里已存在的契约文件；
- 不许写 HUD / 买枪菜单 / H 菜单 / 记分板 / 回合结算（agent-06 的活）；
- 不许写 de_dust2 场景（agent-02 的活）；
- 不许碰 `Assets/Scripts/Module/{Player,CameraRig,Combat,Bot,View,Audio}/**`；
- **不许开子 agent**。

## 3. 前置依赖（已就绪）

**契约文件**（已在磁盘上，直接 using）：
- `Cs16.Core`：`CsTeam` / `CsRoundPhase` / `CsBotDifficulty` / `CsConst` / `CsWeapons` / `CsMatchConfig` / `CsPlayerSettings` / `Events` / `SceneNames` / `PhysicsLayers`
- `Cs16.Module.Match`：`ICsMatch`（**比赛门面**）、`CsActor`、`CsBotIntent`、`CsHitboxProxy`

**兄弟模块的约定类型**（并行开发中，你**可以引用它们**，按此确切名字写装配代码，编译由主 agent 在全部完成后统一验证）：

| 模块 | 类型全名 | 门面属性 |
|---|---|---|
| 比赛 | `Cs16.Module.Match.MatchModule : MonoBehaviour` | `public ICsMatch Match { get; }` |
| 地图 | `Cs16.Module.Map.CsMapModule : MonoBehaviour` | `public ICsMap Map { get; }` |
| 玩家 | `Cs16.Module.Player.PlayerModule : MonoBehaviour` | 无（自驱动） |
| 机器人 | `Cs16.Module.Bot.BotModule : MonoBehaviour` | 无（自驱动） |
| 视图 | `Cs16.Module.View.ViewModule : MonoBehaviour` | 无（自驱动） |
| 音频 | `Cs16.Module.Audio.AudioModule : MonoBehaviour` | 无（自驱动） |

**引擎真实 API**（已核对源码）：见 `docs/步骤文档.md` §3.1。特别注意：
- `Game.UI.Open<T>(param)` / `Close<T>()` / `CloseAll()` / `Confirm(...)` / `Toast(text,dur)`；
- 面板预制体必须在 `Assets/Resources/UI/{类名}`，`UIPanel` 覆写 `OnOpen(object param)`；
- `Game.LanBrowser.Scan()` + `Hosts` + `OnHostFound` / `OnScanFinished`（**单机扫不到是正常真实行为**）；
- `CloverEngine.UIFactory` 用于代码搭 UI（`CreateNode/Stretch/CreateCentered/CreatePanel/CreateText/CreateButton`）。

## 4. 产出物（绝对路径）

1. `Assets/Scripts/App/Bootstrap.cs` —— 唯一组装点，**≤200 行**。必须：
   - `Awake()`：`Application.runInBackground = true;` + 单实例守卫（`FindObjectsByType<Bootstrap>()` 多于 1 个则自毁）；
   - `Start()`：`Game.Launch(new GameConfig{ LogDir="logs", ResourceRoot="Assets/Resources" })` → `CloverRes.Init("Assets/Resources")` → `CloverInput.Init()`（**不调 CloverNet.Init**）；
   - 装配兄弟模块（按 §3 的确切类型名，全部 `gameObject.AddComponent<T>()`）；
   - `new AppFlow(...)` 注入依赖后 `flow.Enter()`；
   - 订阅 `CloverEvents.Net.OnKicked` 之类**只有在联网时才有意义**的事件 → 单机可不订阅；
   - 加载 `CsPlayerSettings`（`Game.Setting`）。
2. `Assets/Scripts/Module/Flow/AppFlow.cs` —— `IAppFlow { void Enter(); string CurrentState { get; } void GoStage(CsMatchConfig cfg); void GoMainMenu(); }`：
   - `Game.Fsm` 站点：`Boot / MainMenu / ServerList / NewGame / Options / TeamSelect / Loading / Stage / Pause`（+ 转换 trigger）；
   - 场景切换：Boot/Menu 用 `Game.Scene.Load(SceneNames.Menu)`，进图用 `Game.Scene.Load(SceneNames.StageDust2, progress, onDone)`；
   - **读条真进度**：`LoadingPanel.SetProgress(p)` 由 `onProgress` 驱动；
   - `GoStage(cfg)`：读条完成 → `_match.Start(cfg)` → `Game.Fsm` 进 `Stage` + 打开 `HudPanel`（agent-06 提供，用 `Game.UI.Open<HudPanel>()`，类名固定为 `Cs16.UI.HudPanel`）；
   - `GoMainMenu()`：执行清场清单（`Game.UI.CloseAll()` / `Game.Entity.ClearAll()` / `Game.Pool.ClearAll()` / `Game.Timer.StopScope("stage")` / `Game.Sound.StopAll()` / `_match.Stop()`），再 `Game.Scene.Unload(SceneNames.StageDust2, ...)` + `Load(SceneNames.Menu)`；
   - 订阅 `Events.LaunchMatch` / `BackToMain` / `Resume` / `RequestPause` / `Disconnect` / `QuitGame`（**具名方法**，不许匿名 lambda，否则 Off 不掉）；
   - 订阅 `Events.TeamChosen` → 记入 `CsMatchConfig.PlayerTeam`。
3. `Assets/Scripts/UI/Flow/*.cs`（8 个面板，全部 `: UIPanel`）：
   - `BootPanel`：Logo/标题/版权字，`Game.Timer.AfterUnscaled(2f, ...)` 后发 `Events.StartNewGame`；任意键可跳过（`Game.Input.GetKeyDown(GameKey.Space)` 等）；
   - `MainMenuPanel`：4 个按钮（New Game / Find Servers / Options / Quit）；标题显示 `COUNTER-STRIKE 1.6`；显示玩家名（来自 `CsPlayerSettings`）；
   - `ServerListPanel`：调 `Game.LanBrowser.Scan()`，`OnHostFound` 填充列表，`OnScanFinished` 显示"未找到局域网服务器"（真实行为）；有 Refresh / 返回按钮；若 `!IsSupported` 显示 `UnsupportedReason`；
   - `NewGamePanel`：地图下拉（只有 `de_dust2`）+ Bot 数量（0~9，T/CT 各一列或总数）+ Bot 难度（Easy/Normal/Hard 三选）+ 回合数 + 起始金钱 + 回合时间 + 友军伤害开关 + 玩家名输入 → Start；发 `Events.LaunchMatch`（参数 `CsMatchConfig`）；
   - `OptionsPanel`：5 个分页（Audio 三个音量 / Video FOV+显示FPS / Mouse 灵敏度+反转 / Keyboard 说明 / Multiplayer 玩家名）→ 改完 `Game.Setting.Save()`，重进仍在；
   - `LoadingPanel`：`Layer => UILayer.System`；进度条（**给足 sprite 或用锚点宽度**，不要留空 sprite 的 Filled Image）+ 地图名 + 提示文案；
   - `TeamSelectPanel`：CT / T / 随机 / 观察者 四个按钮 → 发 `Events.TeamChosen`；
   - `PausePanel`：Resume / Options / Back to Main Menu（二次确认用 `Game.UI.Confirm`）/ Quit。
4. `Assets/Editor/Flow/FlowSetup.cs` —— Editor 脚本，一键生成：
   - `Assets/Scenes/Boot.unity`（一个 Camera + `Bootstrap` 挂载点）、`Assets/Scenes/Menu.unity`（Camera + Canvas + EventSystem）；
   - 8 个面板预制体到 `Assets/Resources/UI/`（用 `CloverEngine.UIFactory` 搭，含真实按钮/文本/进度条，样式可用深色 CS 风格：背景 `#1B1B1B`、强调色 `#E8A33D`、文字白色）；
   - 把两个场景加入 Build Settings；
   - 菜单里加 `Clover/CS16/生成流程场景与面板`。
5. `Assets/Scripts/Module/Flow/IAppFlow.cs`。

## 5. 验收标准

- [ ] 代码符合 §2 边界；引用的引擎 API 全部能在 `clover-client-unity-engine/Runtime/**` 找到出处
- [ ] `Game.UI.Open<T>` 的面板类名与预制体名严格一一对应（`Resources/UI/{类名}`）
- [ ] 无裸事件字符串（全部走 `Events.*`）、无裸场景名（全走 `SceneNames.*`）、无裸数字（走 `CsConst.*`）
- [ ] UI 面板里**没有** `using Cs16.Module.*`
- [ ] 每个非预期分支都有 `Game.Logger.Warn/Error` 日志
- [ ] 自检命令（能跑就跑，跑不了说明原因）：
      `Select-String -Path Assets/Scripts/App/*.cs -Pattern "CloverNet.Init"` 应**无命中**（单机）；
      `Select-String -Path Assets/Scripts/UI/Flow/*.cs -Pattern "using Cs16.Module"` 应**无命中**
- [ ] 与 CS 1.6 主菜单同角度对照：布局（左上标题 + 左侧竖排按钮）、文案、配色**一致**

## 6. 约束

- 完成度要求：**这是"完整成品"的第一段，不许交骨架**（按钮必须真能用、读条必须真在动）。
- 所有非预期分支必须打日志（见 `docs/步骤文档.md` §6）。
- 完成后回报：**已完成 / 未完成 / 下一棒从哪个文件接着做** + 产出物路径 + 未决问题。
