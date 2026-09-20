# agent-03：比赛核心模拟（单机版的"服务器"）

## 0. 技能（开工必做，不许跳）

```
(1) 拿到 skill：工具集里有 use_skill 就用它加载 clover-engine + unity-cli；
    没有就直接读文件（命中即用，不必读完）：
      · tools/ai-skill/SKILL.md   ← 项目级，首选
      · clover-ai-skill/SKILL.md                ← 仓库源
      · ~/.codebuddy/skills/ai-skill/SKILL.md                                 ← 安装副本
    （unity-cli 同理）
    都找不到 → 回报调用方要路径，**不许凭记忆写代码**。
(2) 然后按 skill 的「混合模式找依据」查代码（用户 > 引擎 > 联网/自创），**不许编 API**。
```

**必读**：
- `docs/步骤文档.md`（全文，尤其 §3）
- `策划/策划案/CS1.6单机参考规格.md` §2.3（玩法系统清单，你的验收）
- `clover-ai-skill/reference/architecture.md`（分层）
- `clover-ai-skill/reference/game-delivery.md` §5（行为→表现规格）
- 契约文件：`Assets/Scripts/Core/*.cs`、`Assets/Scripts/Module/Match/CsTypes.cs`、`ICsMatch.cs`

## 1. 目标

实现 `CsMatch`：**本地权威的比赛模拟**。一场完整比赛可跑通：Freeze（买枪）→ Live → RoundEnd → 下一回合 → 半场换边 → MatchEnd。包含经济、买枪、武器/弹药、射击结算、伤害、炸弹（下包/拆包/爆炸）、死亡/复活、比分、机器人意图执行。

**这是全工程的核心**：其余 6 个 agent 都依赖你实现的 `ICsMatch` 行为（接口已定，你只实现）。

## 2. 任务边界

**只做**（这些路径只有你能写）：
- `Assets/Scripts/Module/Match/CsMatch.cs`（实现 `ICsMatch`）
- `Assets/Scripts/Module/Match/MatchModule.cs`（`Cs16.Module.Match.MatchModule : MonoBehaviour`，暴露 `public ICsMatch Match { get; }`，在 `Update()` 里 `Tick(Time.deltaTime)`）
- `Assets/Scripts/Module/Match/CsEconomy.cs`、`CsBomb.cs`、`CsRound.cs`、`CsDamage.cs`、`CsInventory.cs`

**绝不做**：
- **不许改** `CsTypes.cs` / `ICsMatch.cs`（契约）；需要新增成员 → **回报主 agent**；
- 不许碰 `Assets/Scripts/Module/{Player,CameraRig,Combat,Bot,View,Audio}/**`、`UI/**`、`App/**`、`Assets/Scripts/Core/**`；
- 不许写地图几何（用 `ICsMap` 接口，由 agent-02 实现注入）；
- **不许开子 agent**。

## 3. 前置依赖（已就绪）

**契约（逐条照用，签名已定死）**：
```csharp
// 你要实现这个接口的全部成员
public interface ICsMatch { /* 见 Assets/Scripts/Module/Match/ICsMatch.cs，逐条实现 */ }

// 关键数据类型（已在 CsTypes.cs 定义，直接使用）
public sealed class CsActor { /* Id/Name/Team/IsBot/Difficulty/Position/Velocity/Yaw/Pitch/OnGround/
    IsCrouching/IsWalking/IsAlive/Health/Armor/HasHelmet/HasDefuser/Money/
    PrimaryWeapon/SecondaryWeapon/KnifeWeapon/ActiveWeapon/HasBomb/Ammo/
    Kills/Deaths/Assists/RoundKills/Score/NextFireTime/ReloadEndTime/SwitchEndTime/
    ConsecutiveShots/RecoilPitch/RecoilYaw/FlashEndTime/UseProgress/InBuyZone */ }
public struct CsBotIntent { Vector3 Move; bool Jump/Crouch/Walk/Fire; Vector3 AimPoint; bool Reload; string SwitchTo; bool Use; CsBotState State; }
public struct CsHitInfo { long ShooterId; long VictimId; string WeaponId; CsHitbox Hitbox; Vector3 Point; Vector3 Normal; float Distance; bool ThroughWall; }
public struct CsBotProfile { /* CsBotProfile.For(difficulty) 已给三档参数 */ }
```
**注入**：`MatchModule` 需要 `ICsMap`（由 Bootstrap 在装配后注入，或 `MatchModule` 自己 `GetComponent<CsMapModule>()` —— **优先用 App 注入**，若做不到就 `GetComponent`，但要打日志）。

**引擎真实 API**：`Game.Logger.Info/Warn/Error(tag,msg)`；`Physics.Raycast` / `Physics.OverlapSphere`（Unity 原生，射击与爆炸用）；`Game.Map.WalkableAt` 经 `ICsMap`。

**地图标记**：`CsMarkers.*`（`Cs16.Module.Map` 命名空间）给出 `Bombsite_A` / `Bombsite_B` / `Spawn_T` 等标记名，用 `ICsMap.Points(marker)` 取坐标。

## 4. 产出物与要求（逐条实现，一条不许少）

### 4.1 回合系统（`CsRound`）
- 阶段：`Freeze`（`cfg.FreezeTime`）→ `Live`（`cfg.RoundTime`）→ `RoundEnd`（`CsConst.RoundEndTime`）→ 下一回合；
- 回合开始：全部 actor 复活（满血/满甲按购买保留规则）、回到本队出生点、随机朝向、重置弹药与后坐力；
- 胜负判定（`CsRoundEndReason` 四种全部要实现）：
  - `BombExploded`（T 胜）/ `BombDefused`（CT 胜）/ `AllTargetsEliminated`（剩下一方胜）/ `TimeExpired`（CT 胜）；
  - 炸弹已下包后时间到**不结束**（继续到爆炸或拆包）；
- 半场交换：`RoundNumber > cfg.RoundsPerHalf` 且 `cfg.HalfTimeSwap` → 交换双方阵营与比分显示（**打印日志**）；
- 比赛结束：任一方达到 `RoundsPerHalf + 1`（16 胜）或总回合达 `MaxRounds` → `MatchEnd`。

### 4.2 经济（`CsEconomy`）
- 起始 `cfg.StartMoney`；上限 `CsConst.MaxMoney`；
- 回合胜负奖励按 `CsConst.RewardRoundWin*`；连败奖励 `RewardLossBase + step*n` 封顶 `RewardLossMax`；
- 击杀奖励按武器 `CsWeaponDef.KillReward`；下包 +`RewardBombPlant`；拆包 +`RewardBombDefuse`；
- 每笔变动打一条 `Game.Logger.Info("Match", $"...")`（金额 + 原因）—— 这是验收证据。

### 4.3 买枪（`TryBuy` / `CanBuyWeapon` / `CanBuyNow`）
- 条件：`Phase == Freeze`（或 Live 但在买枪区内 —— 按 `CsConst` 与官方一致，取 Freeze 期 + 买枪区）、`InBuyZone`、钱够、阵营允许（`CsWeaponDef.TeamLimit`）、槽位规则（主武器替换旧的、手雷上限）；
- 成功：扣钱 + 给武器 + 填满弹药 + 记日志；失败：返回 `reason`（"不在买枪时间" / "必须在买枪区" / "金钱不足" / "该武器限阵营"）+ **打 Warn 日志**；
- 生成默认装备：T = Glock18，CT = USP；近战刀恒有；T 随机一人持 C4（`HasBomb`）。

### 4.4 射击与伤害（`CsDamage`）
- 射击由射击模块（agent-04）做射线检测后调 `ReportHit(in CsHitInfo)`；**但机器人由你内部执行射线**（`SubmitBotIntent` 里的 `Fire` → 你自己用 `Physics.Raycast` 从 bot 眼睛沿朝向打）；
- 伤害公式：`base = def.Damage`；部位倍率 `CsConst.HitHead/HitChest/HitStomach/HitLeg`；距离衰减 `1 - def.FalloffPerMeter * dist`；护甲吸收 `CsConst.ArmorAbsorbRatio`（有甲时：`damage * (1 - absorb*(1-armorPen))`，护甲损耗 `damage * ArmorDamageRatio`）；头盔只减免头部；
- 致死：`Health <= 0` → 记 `Deaths`、发 `OnKill`（含 `CsKillEvent`，**爆头标记要做**）、给击杀者加钱与统计、清仇恨；T 持包者死亡 → 包掉落（`BombDropped`）；
- `Events` / `OnDamaged` 事件要发（HUD 飘字与命中标记用）。
- **日志降频**：伤害日志按"首次 + 每 50 次"打，防刷屏。

### 4.5 武器与弹药（`CsInventory`）
- 槽位切换（`SwitchSlot(1..5)`）：受 `SwitchEndTime`（`CsConst.SwitchTime*`）约束，切换中不能开火；
- 开火：检查 `NextFireTime`（按 `def.SecondsPerShot`）、弹药、`ReloadEndTime`；每发扣 1 弹药、累加后坐力（`ConsecutiveShots`、`RecoilPitch/Yaw`，射击模块会读它做镜头抬升）；
- 换弹（`RequestReload`）：按 `def.ReloadTime`，从备弹补满；备弹为 0 打 Warn；
- `ConsumeShotFired(out weaponId)`：本帧是否有射击（表现层播枪声/枪口火焰）；
- 手雷（HE/Flash/Smoke）：简化为"投掷 → 落地/引信到点 → 半径伤害/致盲/烟雾"；HE 用 `Physics.OverlapSphere` + 距离衰减（`CsConst.GrenadeHeRadius/MaxDamage`）；闪光用视线遮挡判定（`HasLineOfSight`）+ `FlashEndTime`。

### 4.6 炸弹（`CsBomb`）
- T 持包者在 `CsMarkers.Bombsite_A/B` 半径 `CsConst.BombsiteRadius` 内按住 E（`SetUseHeld(true)`）→ 进度累计 `PlantTime` → 下包（`BombPlanted=true`，发事件）；
- 已下包：`BombTimeLeft` 从 `CsConst.BombTimer` 递减；≤0 → 爆炸（T 胜 + 范围伤害 + 发 `BombExploded`）；
- CT 在包旁按住 E → 进度累计（有 `HasDefuser` 用 `DefuseTimeWithKit`，否则 `DefuseTime`）→ 拆包成功（CT 胜）；
- 中断条件：松开 E / 移动 / 离开范围 → 进度清零 + 打 Info 日志；
- `UseProgress` 对外可读（HUD 显示进度）。
- 音效触发点：蜂鸣间隔按 `CsConst.BombBeepInterval*`，通过 `OnBombStateChanged` 让音频模块播。

### 4.7 本地玩家动作
- `SetLocalInput(in CsInputState)`：移动解算（`ICsMap.ResolveMove` 分轴滑墙）+ 跳跃（`CsConst.JumpSpeed/Gravity`）+ 蹲（变高度）+ Shift 慢走（`SpeedWalkMultiplier`，且 `IsWalking=true` 时**不产生脚步声**）；速度按当前武器（`SpeedKnife/Pistol/Rifle/AWP`）；
- **Freeze 期禁止移动**（官方行为）；
- `SetUseHeld(bool)`、`DropActiveWeapon()`、`IsZoomed`（右键，AWP/Scout）；
- `ReportHit` 由 agent-04 调用。

### 4.8 机器人执行
- `SubmitBotIntent(long actorId, in CsBotIntent)`：对 `IsBot` 的 actor 执行：
  - `Move`（相对其自身朝向 → 世界方向）、`Jump/Crouch/Walk`、`AimPoint`（换算 yaw/pitch，**并施加该 bot 难度的 `AimErrorDegrees` 随机误差**）、`Fire`（自行射线）、`Reload`、`SwitchTo`、`Use`（下包/拆包）；
  - 对**真人** actor 调用 → 忽略 + 打 Warn（只报一次）。
- `AddBot(team, diff)`：生成名字（名字池 20 个，见 `CsConst.BotNamePoolSize`，如 `Cliffe`/`Minh`/`Gooseman`/`ZBot`…）、初始金钱、默认手枪；`KickBot()` 踢最后一个；`SetBotDifficulty(diff)` **对已存在 bot 即时生效**（更新其 `Difficulty`，并打日志）。

### 4.9 观战 / 暂停 / 控制
- `SpectateNext(dir)`：本地玩家死亡后可在存活 actor 之间切换；
- `SetPaused(bool)`：暂停时 `Tick` 内不推进时间（但仍要响应 UI）；
- `RestartRound()` / `RestartMatch()` / `ForceEndRound(winner, reason)` / `ChangeTeam(team)`（换阵营要重置该 actor 的装备与金钱规则并打日志）。

## 5. 验收标准

- [ ] 所有 `ICsMatch` 成员都实现（无 `NotImplementedException`、无空方法体）
- [ ] 自检：写一个临时探针（`Assets/Editor/MatchSelfTest.cs` 或 PlayMode 用例），模拟
      `Start(cfg)` → 加 4v4 bot → 跑 60 秒模拟 → 断言：回合推进过 ≥1 次、有击杀发生、比分变化、无异常日志
- [ ] 日志证据：经济变动、回合开始/结束、下包/拆包、击杀、升级回合都有 `Game.Logger` 行
- [ ] `CsConst` 里的数值**一个都没有在业务代码里被写成裸数字**
- [ ] 与官方对照：买枪时间/回合时间/金钱/伤害倍率/炸弹时间与 `策划/策划案/CS1.6单机参考规格.md` 一致

## 6. 约束

- 所有非预期分支必须打日志；高频路径（伤害/移动/视线）**必须降频**（首次 + 每 N 次）。
- **完成后回报**：已完成 / 未完成 / 下一棒从哪个文件接着做 + 产出物路径 + 未决问题。
