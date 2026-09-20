# agent-28：第一人称相机 / 视角晃动 / look 累加器下沉（S5+S6）

> ⛔ **本片已于 2026-09-19 21:37 派发并完成，产物登记在引擎 `修复记录.md` 的 `E-core-18` 节。**
> **本文档是 2026-09-20 07:16 主 agent 基于过期认知的重复改写版（原版被覆盖）。保留仅为留痕，⛔ 不要再照它派一遍活。**
> 实证（主 agent 已亲自核验）：引擎 `Runtime/Presentation/{ViewBob,CameraMath,LookAccumulator}.cs` 均已存在；项目 `Module/CameraRig/ViewBob.cs` 已删除；`FirstPersonCamera.cs` / `PlayerMotor.cs` 已改用引擎件（注释明写 `E-core-18 下沉`）。

> **跨仓库片**：引擎 `c:\Work\Server\full-dev\clover-client-unity-engine` ＋ 项目 `c:\Work\Server\full-dev\clover-project-cs16`
> **本片串行执行**（agent-27 已核验通过后才派本片）。⛔ 不要假设有别的片在同时改文件。
> 串行原因：`compile-check-client.ps1` 的产物落在**共享目录** `csc-out\`，两片同时跑会互相覆盖 `CloverEngine.*.dll`。
> **E 号已由主 agent 预分配：本片占用 `E-core-18`**（⛔ 别自己取号、⛔ 别顺延）。
> 用户没开 Unity：**不要求跑编辑器/PlayMode**，但**离线编译 + 离线断言必须跑**。

## 0. 先读

- 引擎 `结构规则.md` §2.1（模块只引 `Core`）/ §3.5（表现域允许放入：含 `Camera`、`Animation`）/ §4.3「**薄模块要谨慎**：只有 1~2 个文件、且没有清晰扩展方向的目录，优先合并」/ §4.4（已有能力不准再起第二套；孤儿代码要清理）/ §6。
- 引擎 `Runtime/Presentation/CloverThirdPersonCamera.cs` —— **已有**的第三人称独立组件（遮挡避障 / 贴脸隐藏），是本片新组件的**形态样板**。
- 项目 `client/Assets/Scripts/Module/CameraRig/FirstPersonCamera.cs`（774 行）、`ViewBob.cs`（77 行）、`Module/Player/PlayerMotor.cs`（look 部分）。

## 1. 现状（已核实，别重查）

| 位置 | 内容 | 通用性 |
|---|---|---|
| 项目 `ViewBob.cs`（77 行） | 视点晃动 + 落地沉降：`Offset` / `Roll` / `Reset()` / `Tick(dt, speedXZ, onGround)`。**零 `Cs*` 类型引用**，只用 7 个数值常量（`CsConst.ViewBobAmount/SpeedRifle/ViewBobSpeed` + `CsCombatTuning.BobBlendSpeed/LandDipAmount/LandDipTau/BobRollDegrees`） | **几乎全通用**（就是数值是业务的） |
| 项目 `FirstPersonCamera.cs:735` | `public static float FovYFromFovX(float fovXDegrees, float aspect)` | **纯函数，全通用** |
| 项目 `FirstPersonCamera.cs:746` | `internal static Vector3 ComputeAimDirection(float yaw, float pitch)` | **纯函数，全通用** |
| 项目 `FirstPersonCamera.cs:760/767` | `FollowRecoil(current,target,dt)` / `Follow(current,target,dt,tau)`（指数平滑跟随） | **纯函数，全通用** |
| 项目 `PlayerMotor.cs:126` 附近 | 鼠标位移 → yaw/pitch 累加（含 `CsCombatTuning.DegreesPerMouseCount`、`:68/130` 的 `PitchLimit` 限位、反转） | **逻辑全通用**（灵敏度/限位是数值） |
| 项目 `FirstPersonCamera.cs` 其余 ~700 行 | 眼位跟随、开镜 FOV 过渡、受击抖动、观战第三人称机位、隐藏自身身体 —— 但**强绑** `ICsMatch` / `PlayerMotor` / `CsActor` / `CsHitboxProxy` / `CsCombatTuning` / `CsViewTuning` | **只有骨架通用**，见 §2.3 |

## 2. 要做的

### 2.1 【必须】三个纯件下沉到 `Runtime/Presentation/`

**① `ViewBob.cs`（新文件）** —— 从项目**整体**搬，只把 7 个常量换成配置结构体：

```csharp
public struct ViewBobConfig
{
    public float FullAmplitudeSpeed;  // 满幅基准速度（米/秒）
    public float Amount;              // 幅度（米）
    public float Speed;               // 频率系数
    public float BlendSpeed;          // 振幅平滑速度（每秒）
    public float LandDipAmount;       // 落地沉降幅度（米）
    public float LandDipTau;          // 沉降衰减时间常数
    public float RollDegrees;         // 横滚角（度）
}

public sealed class ViewBob            // ⛔ 不是 MonoBehaviour（纯逻辑更好测、可离线断言）
{
    public ViewBobConfig Config { get; set; }
    public Vector3 Offset { get; private set; }   // 摄像机**局部空间**偏移（右/上/前）
    public float Roll { get; private set; }       // 横滚角（度，绕视线轴）
    public void Reset();
    public void Tick(float dt, float speedXZ, bool onGround);
}
```

**语义逐字照搬**（⛔ 一个分支都不许改）：起停用**同一个**平滑速度（`MoveTowards`）；`_phase` 只在有幅度时推进、超 `2π` 回绕；落地只在 `onGround` 由假变真时触发；`_landDip < 0.0005f` 归零；`Offset = (cos(phase)*amount, sin(phase*2)*amount - landDip, 0)`；`Roll = cos(phase)*rollDegrees*amplitude`。
原类注释里那条**"只输出偏移，不碰相机"**的契约（bob 不影响射线起点，否则子弹随脚步摆）必须搬进引擎注释。

**② `CameraMath.cs`（新文件）** —— 三个纯函数逐字照搬：

```csharp
public static class CameraMath
{
    public static float FovYFromFovX(float fovXDegrees, float aspect);   // 照搬 :735
    public static Vector3 AimDirection(float yawDegrees, float pitchDegrees); // 照搬 :746（注意原式的符号/角度→弧度口径）
    public static float Follow(float current, float target, float dt, float tau); // 照搬 :767
}
```
`FollowRecoil`（`:760`）若与 `Follow` 只差参数 ⇒ **合并成一个** `Follow`（§4.4 不留两份），并在注释里写明合并口径。

**③ `LookAccumulator.cs`（新文件）** —— 鼠标位移 → yaw/pitch 累加器（纯类）：

```csharp
public sealed class LookAccumulator
{
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }
    public void Reset(float yawDegrees, float pitchDegrees);
    public void Add(float mouseDeltaX, float mouseDeltaY, float degreesPerCount, bool invertY, float pitchLimitDegrees);
}
```
**语义逐字照搬 `PlayerMotor` 现值**（含：哪个轴乘正负、`Pitch` 是否夹在 `±limit`、灵敏度是"每 count 多少度"还是"乘数"、是否先乘 `Time` 相关量 —— **照抄，不要"顺手修正"**，因为手感是已验收过的）。回报里贴出你抄的那几行原文，供对照。

### 2.2 【必须】项目侧改用引擎件（⛔ 不许留平行实现）

- 项目 `ViewBob.cs`：**删除**（含 `.meta`），`FirstPersonCamera` 改为持有引擎 `ViewBob` 实例并传入项目自己的 `ViewBobConfig`（7 个数值**逐字用原常量**，⛔ 不许改数值）。若别处还引用项目 `ViewBob`，改那几处；`Init(...)` 是 `internal`，调用点应很少。
- `FirstPersonCamera` 的 `FovYFromFovX` / `ComputeAimDirection` / `Follow`(含 `FollowRecoil`) 改为调 `CameraMath`（⛔ 项目侧不许保留同名私有副本）。
- `PlayerMotor` 的 look 累加改为用 `LookAccumulator`（灵敏度/限位仍从 `CsCombatTuning` 传入）。
- 判据：全项目 `Select-String` 搜 `FovYFromFovX|ComputeAimDirection|FollowRecoil` ⇒ 命中只应出现在**调用处**（`CameraMath.Xxx(...)`），不许有 `private static` 的本地定义。

### 2.3 【评估后决定，可选】整组件 `CloverFirstPersonCamera`

先算一笔账：把 `FirstPersonCamera.cs` 里"**不引用任何 `Cs*` 类型**"的行数占比算出来（给命令与结果）。

- 若该占比 **≥ 60%** 且能用一个干净接口承接位姿（例如 `IFpsRigTarget { Vector3 Position; float EyeHeight; float Yaw; float Pitch; float RecoilPitch; float RecoilYaw; bool ThirdPerson; }`）⇒ **下沉一个 `CloverFirstPersonCamera`**（形态照 `CloverThirdPersonCamera.cs`：独立组件、业务自行挂、不经 `Game.Camera` 门面），并让项目 `FirstPersonCamera` **改用**它（业务耦合部分留在项目，经接口注入）。
- 否则 ⇒ **不下沉整组件**，只交付 §2.1 的三个纯件，并把"为什么不下沉"（引用行数占比 / 接口不干净在哪）写进回报与 `修复记录.md` 该节。
- ⛔ 无论选哪条，**都不许造一个没人用的空壳组件**（§4.3 + §4.4：孤儿代码要清理）。

## 3. 判据（自己跑，原始输出贴回报）

1. 三个纯件的**公开签名**清单 + 项目侧调用点行号；
2. 项目侧同名私有副本归零（贴搜索模式与命中数）；
3. **离线断言**（临时宿主放 `.ai-tmp/test/`，用完删；引擎 `Tools~` 下已有同类宿主可参照）：
   - `ViewBob`：静止时 `Offset==zero` / 起停平滑（不跳变）/ 落地只触发一次且指数衰减到 <0.0005 归零 / `Roll` 随相位符号正确；
   - `CameraMath`：`FovYFromFovX(90, 16f/9f)` ≈ 58.7（与本项目已验收的 90° 水平口径一致）、`AimDirection` 与项目原实现**逐位相同**（用同一组输入对拍）、`Follow` 在 `dt→0` 时趋近 `target`；
   - `LookAccumulator`：给定一串 `mouseDelta` 后 yaw/pitch 与**项目改前**的手算结果逐位一致（这是"手感没变"的硬判据）。
4. 引擎闸门 `compile-check-client.ps1` ⇒ `RESULT: ALL PASS` / `GATE_EXITCODE=0`；项目侧按 `Cs16.asmdef` 边界 Roslyn ⇒ `EXITCODE=0`（参考集用**闸门刚产出的** `csc-out\CloverEngine.*.dll`，⛔ 不要用 `Library\ScriptAssemblies\` 那份旧的）。
5. ⛔ **不许动项目已验收的表现**（相机手感 / FOV / bob 数值一律不变）。

## 4. 文档 / skill / 注释（**只追加自己的条目**）

1. 引擎 `clover-client-unity-engine-index.md` §3.2 的 **Camera 行**（`Game.Camera` + `CloverThirdPersonCamera` 那段）：追加 `ViewBob` / `CameraMath` / `LookAccumulator`（或整组件，若 §2.3 选了它），说明"不经 `Game.Camera` 门面、业务自挂/自持"。
2. 引擎 `结构规则.md` §3.5「允许放入 · 相机」处：追加你的条目（⛔ 只追加）。
3. 引擎 `修复记录.md`：用 **`Add-Content` 追加**一节 `E-core-18`（⛔ 别插在中间、⛔ 别改号），格式仿 `E-core-13`。
4. skill：`clover-tools\ai-skill\reference\engine-mental-model.md` §4 能力表的 `Game.Camera` 行 —— **追加**你的条目（⛔ 不新建小节、不改规则句、⛔ 不整行替换）；E 号写 `E-core-18`。
5. **改完必须同步宿主副本**：`Copy-Item <repo>\reference\engine-mental-model.md "$env:USERPROFILE\.codebuddy\skills\ai-skill\reference\engine-mental-model.md" -Force`（两副本不一致会被 `skill-health.ps1` 判 FAIL）。

## 5. 不许

- ⛔ 不许改 `FirstPersonCamera` 的**行为**（只许把纯函数/ViewBob/look 换成引擎件，数值与调用顺序逐字保留）；
- ⛔ 不许改项目 `CsCombatTuning` / `CsConst` / `CsViewTuning` 的任何数值；⛔ 不许改 `策划/**`；
- ⛔ 不许让模块间互相引用 asmdef；⛔ 不许把依赖 UnityEngine 的类型塞进 `Core`；
- ⛔ 不许改 `docs/**` 里别的片的任务书；⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；⛔ 不许新增 README / 交接类 md；临时脚本只放 `.ai-tmp/test/` 用完删。

## 6. 回报格式

```
产出物：<引擎 + 项目 文件清单>
2.1 判据：三个纯件签名 + 项目调用点
2.2 判据：项目同名副本归零命中数 / ViewBob 删除后调用点改法
2.3 结论：通用行占比 = ? / 下沉整组件 or 不下沉（理由）
离线断言：<命令 + 原始输出（含对拍结果）>
编译：<两条命令原始输出末尾与退出码>
文档/skill：改动位置与改动后原文
E 号：你实际占用哪个
未决：无 / <具体条目>
```
