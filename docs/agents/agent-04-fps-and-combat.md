# agent-04：第一人称操作与射击

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
- `clover-tools/ai-skill/patterns/client/3d-mmo-basics.md` §0/§1/§2/§3（症状→真因表、预测、本地碰撞、相机六条）
- `clover-tools/ai-skill/reference/engine-mental-model.md` §4/§5（能力边界 + 主角操作三条铁律）
- `策划/策划案/CS1.6单机参考规格.md` §2.2/§2.3（HUD 与射击手感）
- 契约文件：`Assets/Scripts/Core/*.cs`、`Assets/Scripts/Module/Match/ICsMatch.cs`、`Assets/Scripts/Module/Map/ICsMap.cs`

## 1. 目标

让玩家能用**第一人称**在 de_dust2 里跑动、跳、蹲、转视角、开枪、换弹、切枪、买枪，并且**手感像 CS**（移动速度、加速度、后坐力、准星扩散、脚步）。

## 2. 任务边界

**只做**（这些路径只有你能写）：
- `Assets/Scripts/Module/Player/**`（`PlayerModule.cs` / `PlayerMotor.cs`）
- `Assets/Scripts/Module/CameraRig/**`（`FirstPersonCamera.cs` / `ViewBob.cs`）
- `Assets/Scripts/Module/Combat/**`（`CombatModule.cs` / `Firearm.cs` / `CrosshairState.cs` / `GrenadeThrower.cs`）

**绝不做**：
- 不许改任何契约文件（`Core/**`、`Module/Match/CsTypes.cs`、`ICsMatch.cs`、`Module/Map/ICsMap.cs`）；
- 不许实现比赛规则（伤害/经济/回合归 agent-03）——你只**上报命中**与**读取状态**；
- 不许碰 `UI/**`（agent-06）、`Module/Bot/**`（agent-05）、`Module/View/**`（agent-07）；
- **不许开子 agent**。

## 3. 前置依赖（已就绪）

- `ICsMatch`（agent-03 实现，**接口已定死**）：`SetLocalInput(in CsInputState)` / `ReportHit(in CsHitInfo)` /
  `RequestReload()` / `SwitchSlot(int)` / `TryBuy(string, out string)` / `SetUseHeld(bool)` /
  `LocalPlayer`（`CsActor`，含 `Position/Velocity/Yaw/Pitch/IsAlive/IsCrouching/Health/Armor/Money/
  ActiveWeapon/RecoilPitch/RecoilYaw/ConsecutiveShots/NextFireTime/ReloadEndTime/SwitchEndTime/FlashEndTime`）、
  `Phase`（`CsRoundPhase`）、`IsZoomed`、`ConsumeShotFired(out string)`、`CanBuyNow`、`PhysicsLayers.*`。
- `ICsMap`（agent-02 实现）：`ResolveMove(from, to, radius)` / `CanStand(pos, r)` / `SampleGround(pos)` / `IsLoaded`。
- `CsHitboxProxy`（契约，挂在角色受击体上，含 `ActorId` + `Hitbox`）——**由 agent-07 挂载**；你做射线时用它反查。
- 引擎：`Game.Input`（`GameKey` 枚举含 `A`-`Z`、`Num1`-`Num9`、`Space/Escape/Tab/LeftShift/LeftCtrl`、
  `MouseLeft/MouseRight/MouseMiddle`；`GetKey/GetKeyDown/GetMouseButton(0..2)/GetMouseButtonDown/MouseDelta/Lock()/Unlock()`）。

## 4. 产出物与要求

### 4.1 `PlayerModule : MonoBehaviour`（`Cs16.Module.Player`）
- 持有 `PlayerMotor` + `FirstPersonCamera` + `CombatModule` 的实例（同 GameObject 或子对象）；
- 装配由 `Bootstrap`（agent-01）用 `gameObject.AddComponent<PlayerModule>()` 完成；你需要在 `Start()` 里
  `GetComponent<MatchModule>()` 拿 `ICsMatch`（**拿不到必须打 Error 并禁用自己**，不许静默）；
- `Update()`：采集 `Game.Input` → 调 `_match.SetLocalInput(cmd)`；`LateUpdate()`：驱动相机。

### 4.2 `PlayerMotor`
- **不使用 `Game.Camera.Follow`**（那是锁 Z 的简单跟随）；
- **位置解算**：本地意图（`Game.Input.State.MoveDirection` / `GameKey.W/A/S/D`）→ 速度（按当前武器取
  `CsConst.Speed*`，Shift 乘 `SpeedWalkMultiplier`，蹲乘 `SpeedCrouchMultiplier`）→ **交给 `ICsMap.ResolveMove`** 做碰撞；
- **不要自己改 `CsActor.Position`**（那是模拟的权威状态）——你的职责是**填 `CsInputState`**，
  由 agent-03 执行移动；移动结果从 `_match.LocalPlayer.Position` 读（本地模拟 = 零延迟，不存在"被服务端覆盖"问题）；
- 视角：鼠标 `MouseDelta` × `PlayerSettings.MouseSensitivity` → yaw/pitch，pitch 限制 **-89~89**；
  把 yaw/pitch 填进 `CsInputState.Yaw/Pitch`；
- 光标：进图 `Game.Input.Lock()`；打开菜单面板时 `Unlock()`（订阅 `Events.RequestPause`/`Resume` 或用
  `Game.UI.IsOpen<PausePanel>()` 判断，**不要每帧 Lock/Unlock 抖动**）；
- Freeze 期（`_match.Phase == CsRoundPhase.Freeze`）不产生移动输入。

### 4.3 `FirstPersonCamera`（**业务自写**，引擎无此能力）
- 挂在 Camera 上；每帧把相机放到 `LocalPlayer.EyePosition`；
- 朝向 = `LocalPlayer.Yaw/Pitch` **+ 后坐力抬升**（读 `LocalPlayer.RecoilPitch/RecoilYaw`，用指数回正）；
- **视角 bob**：跑动时按速度轻微上下/左右晃动（`CsConst.ViewBobAmount/ViewBobSpeed`），停止时回正；
- **受击晃动**：血量下降时短暂抖动（订阅 `ICsMatch.OnDamaged` 判断是不是自己）；
- **AWP/Scout 开镜**：右键（`GameKey.MouseRight`）→ `Fov` 从 `CsConst.DefaultFov` 插值到 `CsConst.ZoomFov`，
  并把 `CsInputState.Zoom` 置 true（模拟侧限速）；
- **闪光弹致盲**：`LocalPlayer.FlashEndTime > now` 时全屏白（走 `Game.UI` 的一个纯色遮罩面板或
  `Game.UI.Toast` 之外自建 Overlay；**推荐用 `UIFactory` 建一个全屏 Image 面板**，由你在 `UI/` 外自己管理？→
  不许！**UI 归 agent-06** —— 你只订阅并暴露一个 `public float FlashAlpha { get; }`，由 agent-06 的 HUD 读它绘制）；
- 死亡后：切换观战相机（读 `_match.SpectateTarget`，站到目标眼睛位置，跟随其朝向）。

### 4.4 `CombatModule` / `Firearm`
- 输入：左键按住 = 开火（`Game.Input.GetMouseButton(0)`）；`GameKey.R` = 换弹；`GameKey.Num1..Num5` = 切槽；
- **开火门槛**（全部要判，任一不满足就不开火并打**降频**日志）：
  `LocalPlayer.IsAlive` / `NextFireTime <= now` / `ReloadEndTime <= now` / `SwitchEndTime <= now` / 有弹药 / 非 Freeze；
- **射线检测**（引擎不提供，用 Unity `Physics.Raycast`）：
  - 从 `EyePosition` 沿视线方向，射程 `def.Range`；每发按 `Pellets`（霰弹 9 颗）分多次；
  - **散布**：`spread = def.Spread + (移动 ? def.MoveSpread : 0) + 后坐力累积惩罚`，按散布角随机偏移方向；
    瞄准（Zoom）时 `spread *= 0.25`；
  - 命中 `CsHitboxProxy` → 组 `CsHitInfo{ShooterId=自己, VictimId=proxy.ActorId, WeaponId, Hitbox=proxy.Hitbox,
    Point=hit.point, Normal=hit.normal, Distance}` → `_match.ReportHit(info)`；
  - 穿墙：命中 World 层时，若武器 `ArmorPenetration > 0.6` 则继续向前再打一段（`ThroughWall=true`），最多 2 层；
  - **命中反馈三件套**：命中标记 + 音效 + 目标血条下降 —— 前两项你负责触发
    （命中标记：写一个线程安全的 `LastHitMarkerTime`/`LastHitWasHeadshot` 供 agent-06 读；音效用 `Game.Sound.PlaySFX`）；
- **枪口火焰/弹道**：你负责触发（`Game.Pool` 生成一个短暂 Quad/LineRenderer + `Game.Timer.AfterUnscaled` 回收）；
- **准星扩散状态**：暴露 `public float CrosshairSpread { get; }`（0~1，由移动速度 + 连发数驱动）供 HUD 读；
- **手雷**（`GrenadeThrower`）：数字键 4 切到手雷 + 左键投掷 → 生成一个物理球（`Rigidbody`）→
  引信到点（`CsConst.GrenadeFuse`）→ 交给 `ICsMatch` 结算（**爆炸伤害归 agent-03**，你只负责飞行体与视觉）。

## 5. 验收标准

- [ ] WASD 真的能移动（不是被覆盖），Shift 慢走、Ctrl 蹲、Space 跳；跳跃落地有判定
- [ ] 鼠标能转视角（左右 ±360°、上下 -89~89°），**不是上帝视角**
- [ ] 开枪：有射速限制、有弹匣消耗、R 能换弹、1-5 能切槽、后坐力让准星抬升并自动回正
- [ ] 命中 bot 时：`Game.Logger` 有 `Hit` 日志、准星闪命中标记、目标掉血
- [ ] AWP 右键开镜（FOV 变化可见）
- [ ] 自证：写 `Assets/Editor/CombatSelfTest.cs`，Editor 里调用一个静态方法断言
      "散布计算在移动时 > 静止"、"后坐力递增"、"射线能命中 `CsHitboxProxy`"
- [ ] 与 CS 1.6 手感对照：移动速度（步枪 ~4.4m/s）、后坐力模式（AK 前 3 发稳、之后上飘）、
      AWP 开镜延迟 —— 写进回报

## 6. 约束

- **所有"没按预期走"的分支必须打日志**；射线未命中/无弹药/切枪被打断等**高频**日志必须降频（首次 + 每 50 次）。
- 不许直连 `UnityEngine.Input` / `Keyboard.current`；一律走 `Game.Input`。
- 完成后回报：已完成 / 未完成 / 下一棒从哪个文件接着做 + 产出物路径 + 未决问题。
