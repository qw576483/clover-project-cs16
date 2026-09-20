# agent-06：游戏内 UI（HUD / 买枪菜单 / H 菜单 / 记分板 / 结算 / 无线电 / 控制台 / 观战）

## 0. 技能（开工必做，不许跳）

```
(1) 拿到 skill：工具集里有 use_skill 就用它加载 clover-engine + unity-cli；
    没有就直接读文件（命中即用）：
      · c:/Work/Server/full-dev/clover-project-cs16/tools/ai-skill/SKILL.md   ← 项目级，首选
      · c:/Work/Server/full-dev/clover-tools/ai-skill/SKILL.md                ← 仓库源
      · ~/.codebuddy/skills/ai-skill/SKILL.md                                 ← 安装副本
    （unity-cli 同理）
    都找不到 → 回报调用方要路径，**不许凭记忆写代码**。
(2) 然后按 skill 的「混合模式找依据」查代码（用户 > 引擎 > 联网/自创），**不许编 API**。
```

**必读**：
- `c:/Work/Server/full-dev/clover-project-cs16/docs/步骤文档.md` §3
- `clover-tools/ai-skill/patterns/client/ui.md`、`patterns/client/app-flow.md`（面板范式）
- `clover-tools/ai-skill/reference/architecture.md`（**UI 不许 using Module**）
- `clover-tools/ai-skill/reference/engine-mental-model.md` §4（`UIFactory` 全套方法）
- `策划/策划案/CS1.6单机参考规格.md` §2.1/§2.2（HUD 清单 H1~H14 + G11/G12/G13）
- 契约：`Assets/Scripts/Core/*.cs`、`Assets/Scripts/Module/Match/ICsMatch.cs`

## 1. 目标

做齐**全部游戏内界面**，每个都能开能关、数值真实刷新：HUD（血量/护甲/金钱/弹药/回合时间/比分/雷达/准星/击杀信息/命中标记/买枪区/观战）、买枪菜单（B）、H 菜单（H，机器人管理）、记分板（TAB）、回合结算、比赛结束、无线电（Z/X/C）、控制台（~）。

## 2. 任务边界

**只做**（这些路径只有你能写）：
- `Assets/Scripts/UI/InGame/**`
- `Assets/Editor/UiGenInGame/**`
- `Assets/Resources/UI/{HudPanel,BuyMenuPanel,HMenuPanel,ScoreboardPanel,RoundEndPanel,MatchEndPanel,RadioMenuPanel,ConsolePanel}.prefab`

**绝不做**：
- 不许改契约文件（`Core/**`、`Module/Match/**`、`Module/Map/**`）；
- 不许碰菜单类面板（`UI/Flow/**`，agent-01 的）、`App/**`、`Module/**`；
- **UI 里绝对不许 `using Cs16.Module.*`** —— 只能经 `Game.Event` + 一个只读数据快照类；
- **不许开子 agent**。

## 3. 前置依赖与"UI 怎么拿数据"（关键约束）

`architecture.md` 要求 UI 不引用业务模块。因此**约定一个只读快照**：

> **由 agent-03 提供**（若尚未存在 → **回报主 agent**，不许自己跑去改 Match 模块）：
> `Cs16.Core.CsHudSnapshot`（`public static class`，**纯数据**，放 `Assets/Scripts/Core/`，由主 agent 维护）
> ```csharp
> public static class CsHudSnapshot
> {
>     public static bool Valid;                 // 是否有比赛在跑
>     public static CsTeam Team; public bool IsSpectating;
>     public static int Health, Armor, Money, Mag, Reserve;
>     public static string WeaponName; public string WeaponId;
>     public static int RoundNumber; public float PhaseTimeLeft; public CsRoundPhase Phase;
>     public static int ScoreT, ScoreCT; public bool BombPlanted; public float BombTimeLeft;
>     public static bool InBuyZone; public bool CanBuy; public bool IsZoomed;
>     public static float CrosshairSpread;      // 由 agent-04 写
>     public static float HitMarkerTime; public static bool HitMarkerHeadshot;   // 由 agent-04 写
>     public static float FlashAlpha;           // 由 agent-04 写（闪光弹全屏白）
>     public static readonly System.Collections.Generic.List<CsKillFeedItem> KillFeed = new();
>     public static readonly System.Collections.Generic.List<CsRadarDot> Radar = new();
> }
> ```
> **agent-03 / agent-04 负责填它**（每帧刷新）；**你（agent-06）只读**。
> 若该文件在派活时还不存在，**先按上面签名创建它**（它是 UI 与业务之间的契约，允许你创建，但不许改字段名 —— 改前回报主 agent）。

其他可用的只读入口：
- `Game.Event`：`Events.RoundEnded` / `MatchEnded` / `GameMessage` / `KillFeed` 等（已定义在 `Core/Events.cs`）；
- `Game.UI.Open<T>(param)` / `Close<T>()` / `Confirm(...)` / `FloatText(pos,text,color,dur)` / `Toast(text,dur)`；
- `Game.Input.GetKeyDown(GameKey.Tab)` / `GameKey.B` / `GameKey.H` / `GameKey.Escape` / `GameKey.BackQuote`（若枚举无 BackQuote 用 `GameKey.Slash`/`Num0` 兜底并在回报里说明）；
- `CloverEngine.UIFactory`：`CreateNode/Stretch/CreateCentered/CreatePanel/CreateText/CreateButton/DefaultFont`。

**输入 → 事件**（UI 只发事件，不直接调业务）：
| UI 动作 | 发出的事件（`Core/Events.cs`） |
|---|---|
| 买枪菜单点某把枪 | `Events.BuyWeapon`（参数 string weaponId） |
| H 菜单：加 bot | `Events.AddBot`（参数 `CsBotDifficulty`） |
| H 菜单：踢 bot | `Events.KickBot` |
| H 菜单：改难度 | `Events.SetBotDifficulty`（参数 `CsBotDifficulty`） |
| H 菜单：重开回合 / 重开比赛 / 暂停 / 换阵营 | `Events.RestartRound` / `RestartMatch` / `TogglePause` / `ChangeTeam` |
| 暂停面板：回主菜单 | `Events.BackToMain` |
| 无线电 | `Events.RadioCommand`（参数 string） |
| 控制台命令 | `Events.GameMessage`（仅显示；命令走 `Events.*` 同名事件） |

## 4. 产出物与要求（逐面板，一个不许少）

1. **`HudPanel`**（`Layer => UILayer.Normal`，常驻不关）：
   - 左下：血量（`♥ 100`）+ 护甲（`🛡 100`，有头盔显示不同）；颜色随血量变化（<25 红）；
   - 左下角上方：金钱 `$800`（`CanBuy` 时高亮绿）；
   - 右下：弹药 `30 / 90`；旁边武器名；
   - 顶部中央：回合时间 `1:45`（Freeze 期显示 `买枪中`）；
   - 顶部：比分 `CT 3 - 2 T`（左 CT 右 T，配色区分）+ 回合数 `第 5 回合`；
   - 左上：**雷达**（圆形/方形，画自己/队友/可见敌人/包点/炸弹）——用 `Image` + 每帧设置 `anchoredPosition`；
   - 中央：**准星**（4 条线 + 中心点，间距随 `CrosshairSpread` 变化；命中标记 X 在中心闪 0.25s，爆头用红色）；
   - 右上：**击杀信息**（滚动最多 `CsConst.MaxKillFeedEntries` 条：`A [AK-47] B`，爆头加图标）；
   - 底部中央：`Buy Zone` 提示（`InBuyZone` 时显示）+ 下包/拆包进度条（`UseProgress`）；
   - 观战时：显示 `观战中 - [名字]` + `[空格] 切换`；
   - **闪光弹**：`FlashAlpha > 0` 时全屏白色 Image（alpha = FlashAlpha）；
   - 受伤：`OnDamaged` 是自己时屏幕边缘红色渐隐 + `Game.UI.FloatText` 飘伤害数字。
2. **`BuyMenuPanel`**（`B` 键开；`Layer => UILayer.Popup`）：
   - 分类：`1` 手枪 / `2` 冲锋 / `3` 步枪 / `4` 机枪 / `5` 霰弹 / `6` 装备 / `7` 手雷 / `8` 关闭；
   - 列表项：武器名 + 价格（买不起变灰且不可点）；底部显示当前金钱与当前持有；
   - 点选 → `Game.Event.Emit(Events.BuyWeapon, weaponId)`；买成功/失败由 HUD 提示；
   - 不在买枪时间/不在买枪区时打开 → 顶部红字提示"必须在买枪区内"。
3. **`HMenuPanel`**（`H` 键开，**用户点名的菜单**）：
   - **机器人**：`添加机器人（Easy/Normal/Hard 三选）`、`踢出机器人`、`当前机器人数量`、`当前难度`（可切，切完立即生效）；
   - **比赛**：`重开本回合`、`重开比赛`、`暂停/继续`、`换阵营（CT/T/观察者）`、`换地图（仅 de_dust2，下拉）`；
   - 底部：`Close`；
   - 每个操作都要有反馈（成功 Toast / 失败原因）。
4. **`ScoreboardPanel`**（按住 `TAB` 显示）：
   - 两组（T / CT）各一行表头：`玩家名 | 击杀 | 死亡 | 存活 | 金钱 | 状态`；
   - 自己那行高亮；死亡者名字变灰；底部显示比分与回合数。
5. **`RoundEndPanel`**（订阅 `Events.RoundEnded`）：中央大字 `Counter-Terrorists Win` / `Terrorists Win` + 原因（炸弹爆炸 / 拆包成功 / 全歼 / 时间到）+ 比分；
6. **`MatchEndPanel`**（订阅 `Events.MatchEnded`）：`Counter-Terrorists Win!` / `Terrorists Win!` + 最终比分 + `回主菜单` / `再来一局` 按钮；
7. **`RadioMenuPanel`**（`Z`/`X`/`C` 三组菜单，数字键选择 → `Events.RadioCommand`，并把文本显示到 HUD 消息栏）；
8. **`ConsolePanel`**（`~` 开关）：显示 `Game.Logger` 最近 N 行（可订阅一个日志回调）+ 输入框支持 3 个命令：`bot_add`、`bot_kick`、`bot_difficulty <easy|normal|hard>`，其余命令回显"未知命令"；
9. **`Assets/Editor/UiGenInGame/UiBuilder.cs`**：Editor 脚本一键生成以上 8 个预制体到 `Assets/Resources/UI/`，菜单 `Clover/CS16/生成游戏内面板`；
   - 样式参照 CS 1.6：半透明黑底、黄白字、等宽感、无圆角；
   - **进度条/血条不要用无 sprite 的 `Image.Type.Filled`**（`fillAmount` 会静默失效）——用锚点宽度或给 1×1 白 sprite。

## 5. 验收标准

- [ ] 8 个面板全部能在 Play 里打开与关闭，且**预制体名 = 类名**
- [ ] 数值真实刷新（血量掉、弹药减、金钱变、时间走、比分动）
- [ ] 准星随移动/射击扩散、命中标记可见；雷达有自己与队友
- [ ] H 菜单三个功能（加 bot / 踢 bot / 切难度）**真的生效**（配合 agent-05 的日志验证）
- [ ] 买枪：钱不够点不动；买了之后 HUD 弹药与武器名变化
- [ ] TAB 记分板与真实数据一致（与 agent-03 的 `Kills/Deaths` 对齐）
- [ ] 自检：`Select-String -Path Assets/Scripts/UI/InGame/*.cs -Pattern "using Cs16.Module"` **无命中**
- [ ] 截图：HUD / 买枪菜单 / H 菜单 / 记分板 / 回合结算 / 比赛结束 各一张，与 CS 1.6 对照

## 6. 约束

- 面板 `OnOpen(param)` 里不许读业务对象（`param` 在 `Awake` 之后才到，且 UI 不引 Module）；
- 每个非预期分支（缺引用/数据无效/面板找不到）必须打 `Game.Logger.Error("UI", ...)`；
- 完成后回报：已完成 / 未完成 / 下一棒从哪个文件接着做 + 产出物路径 + 未决问题。
