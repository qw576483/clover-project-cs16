# agent-05：机器人 AI（3 档难度，用户点名必须可辨）

## 0. 技能（开工必做，不许跳）

```
(1) 拿到 skill：工具集里有 use_skill 就用它加载 clover-engine + unity-cli；
    没有就直接读文件（命中即用）：
      · tools/ai-skill/SKILL.md   ← 项目级，首选
      · clover-tools/ai-skill/SKILL.md                ← 仓库源
      · ~/.codebuddy/skills/ai-skill/SKILL.md                                 ← 安装副本
    （unity-cli 同理）
    都找不到 → 回报调用方要路径，**不许凭记忆写代码**。
(2) 然后按 skill 的「混合模式找依据」查代码（用户 > 引擎 > 联网/自创），**不许编 API**。
```

**必读**：
- `docs/步骤文档.md`（§3 契约）
- `策划/策划案/CS1.6单机参考规格.md` §2.4（**3 档难度参数表 = 你的验收合同**）
- `clover-tools/ai-skill/patterns/client/3d-mmo-basics.md` §0（"活靶子没有巡逻范围/不还手"等真因）
- 契约：`Assets/Scripts/Module/Match/CsTypes.cs`（`CsBotProfile` / `CsBotIntent` / `CsBotState` / `CsActor`）、
  `ICsMatch.cs`、`Assets/Scripts/Module/Map/ICsMap.cs`（`CsMarkers`）

## 1. 目标

机器人要**真的像个 CS bot**：沿地图推进、发现敌人、停下瞄准开枪、会买枪、T 会下包、CT 会拆包；
**3 档难度（Easy/Normal/Hard）行为差异明显可测**（反应时间、瞄准误差、命中率）。

## 2. 任务边界

**只做**（这些路径只有你能写）：`Assets/Scripts/Module/Bot/**`
（`BotModule.cs` / `CsBotBrain.cs` / `BotNavigator.cs` / `BotBuyLogic.cs`）

**绝不做**：
- **不许改** `Module/Match/**`（含 `CsTypes.cs` / `ICsMatch.cs`）与 `Module/Map/ICsMap.cs` —— 需要新契约 → 回报主 agent；
- **不许自己执行移动/开枪**（那是 agent-03 的 `SubmitBotIntent` 的职责）—— 你只**决策**并提交意图；
- 不许碰 `UI/**`、`Module/{Player,CameraRig,Combat,View,Audio}/**`、`App/**`、`Core/**`；
- **不许开子 agent**。

## 3. 前置依赖（已就绪）

```csharp
void ICsMatch.SubmitBotIntent(long actorId, in CsBotIntent intent);   // 唯一输出口

IReadOnlyList<CsActor> Actors { get; }   CsActor Find(long actorId);   int AliveCount(CsTeam team);
bool HasLineOfSight(Vector3 from, Vector3 to, float maxDistance = 100f);
CsRoundPhase Phase { get; }   bool BombPlanted { get; }   Vector3 BombPosition { get; }
float BombTimeLeft { get; }   bool BombCarrierIs(long actorId);
bool CanBuyNow { get; }   bool TryBuy(string weaponId, out string reason);
bool IsInBuyZone(Vector3 position, CsTeam team);   void SwitchWeapon(string weaponId);

CsBotProfile CsBotProfile.For(CsBotDifficulty d);      // 三档参数已给，不许另编

Vector3[] ICsMap.Points(string marker);   bool ICsMap.TryGetPoint(string marker, out Vector3 p);
Vector3 ICsMap.ResolveMove(Vector3 from, Vector3 to, float radius);
bool ICsMap.WalkableAt(float x, float z);   Vector3 ICsMap.SampleGround(Vector3 pos, float maxDrop);
```
> bot actor 由 `ICsMatch.AddBot(team, diff)` 创建（New Game / H 菜单触发），**你不创建 actor** ——
> 你要做的是"发现新出现的 bot actor → 为它建 brain；actor 消失 → 销毁 brain"。

## 4. 产出物与要求

### 4.1 `BotModule : MonoBehaviour`（`Cs16.Module.Bot`）
- `Start()`：`GetComponent<MatchModule>()` 拿 `ICsMatch`、`GetComponent<CsMapModule>()` 拿 `ICsMap`
  （拿不到 → `Game.Logger.Error("Bot", ...)` + `enabled=false`，**不许静默**）；
- `Update()`：按 `CsConst.BotTickInterval`（10Hz）节流，遍历全部 `IsBot` 的 actor，调 `brain.Think()` → `SubmitBotIntent`；
- **brain 集合同步**：每 tick 对比 `_match.Actors` 与本地 brain 表；新增建 brain、消失销毁（Info 日志）；
- 每个 bot 建 brain 时打印一次：名字 / 阵营 / 难度 / 反应时间 / 瞄准误差（**这是"3 档"的验收证据**）。

### 4.2 `CsBotBrain`
状态机：`Idle → Patrol → Engage → (Plant | Defuse | Camp)`。

- **感知**：
  - 看见 = 距离 ≤ `profile.VisionRange` **且** 夹角 ≤ `CsConst.BotFovDegrees/2` **且** `HasLineOfSight`；
  - 听觉 = `CsConst.BotHearRadius` 内敌人开枪/跑动 → 记住大致位置；
  - 目标记忆 2 秒（丢了不立刻忘）；**只打敌对阵营**（`target.Team != self.Team` 且非 Spectator）。
- **难度落地（决定验收，逐条实现）**：
  - `ReactionTime`：首次看见 → 开火之间的延迟（记 `_firstSeenTime`）；
  - `AimErrorDegrees`：`AimPoint` = 目标眼睛位置 + 按误差角随机偏移（带阻尼，避免抖动）；
  - `AimSpeedDegrees`：把 `AimPoint` 向"当前已瞄点"插值，限制每秒转动角度；
  - 连发/停顿：`FireBurstMin..Max` 开火、`FirePauseMin..Max` 停顿；
  - `HeadshotChance`：决定瞄头/瞄胸；瞄头时误差角 ×1.4（更难）；
  - `PreferredRange`：距离 > 该值 → `Crouch=true`（提高精度）+ 点射；距离 < 3m → 倾向换手枪（近身）。
- **导航（`BotNavigator`）**：
  - 目标点按阵营与阶段：
    - T：带包者 → `CsMarkers.BombsiteA/B`（随机）；其余 → `Route_T_To_A/B` 或 `Route_T_Mid`；
    - CT：分散 → `Route_CT_To_A` / `Route_CT_To_B` / `Route_CT_Mid`；**已下包 → 全部冲向 `BombPosition`（必须会拆包）**；
  - 走法：取"当前最近的下一个路点 → 逐步推进"，用 `ICsMap.WalkableAt` 做简单避障；
    卡住 0.5s → 重新取点 + Warn；**不许用 Unity NavMesh**（工程未烘焙，用了会静默不动）；
  - `Patrol` 无敌人时在 `Route_Patrol` 里随机巡点；
  - 路点缺失（`Points()` 返回空）→ Error 日志 + 退化到"朝目标点直线走"（不许站着不动）。
- **买枪（`BotBuyLogic`）**：每回合 `Phase == Freeze && CanBuyNow` 时按 `BuyBudgetTier` 决策：
  - tier 0（Easy）：优先 `deagle`→不够则 `glock18/usp` + `vest`；tier 1（Normal）：`mp5`/`galil`/`famas` + `vest`；
    tier 2（Hard）：`ak47`（T）/`m4a1`（CT）/`awp`（30% 概率）+ `vesthelm` + `hegrenade`；
  - 买不起就买便宜的（**失败要打 Warn 并降级**，不许静默）；买完 `SwitchWeapon` 到主武器。
- **下包/拆包**：
  - T 持包者到包点半径内 → `Use=true`（按住）；CT 在 `BombPosition` 2m 内 → `Use=true` 拆包；
  - `Use` 期间不移动（移动会中断，与 agent-03 的行为一致）。

## 5. 验收标准

- [ ] 三档难度**参数与行为都对得上** `CsBotProfile.For()`（不许在 bot 代码里另写一套数值）
- [ ] 日志证据（必须能贴到验收表）：
  - 每个 bot 初始化日志含难度与参数；
  - **命中率统计**：`BotModule` 内按难度累计 `shots / hits`，每 30 秒打一条 `Game.Logger.Info("Bot", "difficulty=Hard shots=.. hits=.. acc=..")`；
  - Hard 的 `acc` 显著高于 Normal，Normal 高于 Easy（同一场景跑 60 秒对比）
- [ ] bot 会走（连续 30 秒无卡死；卡住有 Warn 日志）
- [ ] bot 会发现并交火（日志有 `Engage` 状态 + 造成伤害）
- [ ] T bot 会下包、CT bot 会去拆包（各至少一次，日志 + 画面）
- [ ] `Assets/Editor/BotSelfTest.cs`：Editor 探针，模拟 4v4（三档各跑一轮）打印上述统计
- [ ] 与 CS 1.6 PodBot 对照：Easy 明显"打不中人"、Hard 明显"见面就秒"，写进回报

## 6. 约束

- 所有非预期分支必须打日志；高频路径（感知/射击判定）**必须降频**（首次 + 每 N 次或按秒）。
- 完成后回报：已完成 / 未完成 / 下一棒从哪个文件接着做 + 产出物路径 + 未决问题。
