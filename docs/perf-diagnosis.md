# cs16 掉帧排查报告（2026-09-20 实测）

> 结论先行：**掉帧根因确实是"显卡没启用"——但它不是"重启就能好"的那类问题。**
> 重启无效 ≠ 显卡没问题。这台机器上，**重启根本不会把被禁用的设备启用回来**。

---

## 1. 一条命令的判据（Unity 自己说的）

```
SystemInfo.graphicsDeviceName  = Microsoft Basic Render Driver
SystemInfo.graphicsDeviceType  = Direct3D12        ← D3D12 的 WARP 后端
SystemInfo.graphicsMemorySize  = 16300 MB          ← 共享内存（不是显存）
Screen.currentResolution       = 3440x1440
```

**`Microsoft Basic Render Driver` = Windows 的 CPU 软件光栅化器（WARP）。**
即：Unity 现在**一个像素都不是显卡画的**，全是 12 个 CPU 核在软件模拟 GPU。

对照（同一台机器、同一张卡，历史锚点）：

| 时间 | 来源 | 渲染设备 | 显存 | 类型 |
|---|---|---|---|---|
| 9/15 20:09 | 本项目构建版 `Player.log` | **AMD Radeon RX 5700 XT** | 8151 MB | Discrete |
| 9/17 10:15 | 炉石 `Player.log` | **AMD Radeon RX 5700 XT** | 8151 MB | Discrete |
| **9/20 现在** | Unity 编辑器（Edit + Play） | **Microsoft Basic Render Driver** | 16300 MB | 共享内存 |

> 9/15、9/17 这张卡还在正常工作。变化发生在这之后。

---

## 2. 实测帧率（Play 模式，99 帧 / 10.04 秒）

```
实测：     99 帧 / 10.04 s  ≈  9.9 fps   平均 101 ms/帧
unscaledDeltaTime        ≈ 81 ~ 118 ms
FrameTimingManager:
  gpuFrameTime           ≈ 80 ~ 106 ms   ← 大头
  cpuMainThreadFrameTime ≈ 87 ~ 113 ms
  cpuRenderThreadFrameTime ≈ 0.5 ~ 1.8 ms ← 渲染线程几乎不干活
```

**怎么读这组数**：
- 瓶颈在 GPU 侧（100 ms），**不在渲染线程**（1 ms），也不在游戏脚本逻辑。
- 「GPU 时间巨大 + 渲染线程时间极小」正是**软件光栅化**的指纹：光栅化跑在 CPU 模拟层里，不占渲染线程。
- 3440x1440 ≈ 495 万像素，靠 CPU 逐像素画 → 每秒约 10 帧，算力上完全说得通。

⚠️ **这不是 cs16 代码写崩了**。同样一份代码换回真卡，这个数字会完全不同。
（依据：`cpuRenderThread = 1 ms` 说明提交给 GPU 的绘制指令量很小，慢的是像素填充。）

---

## 3. 系统侧的硬证据

```
pnputil /enum-devices：
  Instance ID: PCI\VEN_1002&DEV_731F&...&00000008
  Device Description: AMD Radeon RX 5700 XT
  Status: Disabled                     ← 设备被禁用

Get-PnpDevice：
  Status  : Error
  Problem : CM_PROB_DISABLED
  ProblemDescription : This device is disabled. (Code 22).
```

同一台机器上的其它显示适配器：

| 设备 | 状态 |
|---|---|
| **AMD Radeon RX 5700 XT** | **Error / Code 22（被禁用）** |
| OrayIddDriver Device（向日葵虚拟显示器） | OK —— 但它是 IDD，**只出画面、不做 3D 加速** |
| NVIDIA GTX 750 Ti / 1050 Ti | Code 45（phantom，历史残留，与本次无关） |

⚠️ 关键点：**`OrayIddDriver` 是 Status OK 的**。它让"设备管理器看起来有显示适配器在正常工作"，
但它**不具备 D3D 硬件加速能力**。真正的 3D 加速设备（AMD 卡）被禁用后，
D3D12 找不到硬件适配器 → 只能回落到 WARP。

---

## 4. 为什么"重启"没用（回应质疑）

这是**这次和上次结论不同的地方** —— 上次说"启用设备 + 重启就好"，**这一步在这台机器上不成立**：

```
HKLM\SYSTEM\CurrentControlSet\Enum\PCI\VEN_1002&...\ConfigFlags = 0x0   ← 禁用标志已是 0
DEVPKEY_Device_LastArrivalDate = 2026/9/20 15:08:51                     ← 今天重启后设备确实重新枚举过
但 pnputil 仍报 Status: Disabled / Problem: Code 22
Kernel-PnP 事件：该设备最后一次记录是 2026/7/20 22:05 的 Event 411（启动失败）
                今天 15:08 重启之后 —— 一条记录都没有
```

**说明**：今天 15:08 那次重启，PnP **根本没有去启动这张卡**（没启动就不会有配置/启动事件）。
禁用状态是**持久**的，重启只是重新枚举硬件，**不会自动把被禁用的设备启用回来**。

所以正确的因果是：

- ❌「重启就能好」—— 错。重启只是让状态生效的手段，前提是**先**把设备启用。
- ❌「重启了还卡，所以不是显卡问题」—— 推理不成立。重启没改变禁用状态，卡当然还是没启用。
- ✅ 「卡被禁用 → Unity 落到 WARP → 10 fps」—— 每一步都有实测支撑。

### 这台机器上还可能"反复把卡写禁用"的几个软件（都在运行）

| 项 | 状态 | 为什么可疑 |
|---|---|---|
| **AMD Crash Defender Service**（`amdfendrsr.exe`） | Running / Auto | AMD 驱动的崩溃保护，**驱动一崩就把设备禁掉并回落基本显示驱动** |
| **360 全家桶**（`ZhuDongFangYu` 主动防御、`360DesktopLiteApp`、`360FileService` 等） | 全部 Running，开机 15:09 自启 | 安全软件会拦截/回滚显示驱动加载 |
| **OrayIddDriver**（向日葵虚拟显示器驱动） | OK | 虚拟显示器软件会写设备禁用标志 |

---

## 5. 修复步骤（需要**管理员** PowerShell）

> 当前会话不是管理员（`IsAdmin: False`），我无法直接执行设备启用。

```powershell
# ① 启用设备
$id = 'PCI\VEN_1002&DEV_731F&SUBSYS_E4091DA2&REV_C1\6&3227393A&0&00000008'
Enable-PnpDevice -InstanceId $id -Confirm:$false

# ② 重新扫描硬件
pnputil /scan-devices

# ③ 立刻复查（不要只看命令有没有报错）
Get-PnpDevice -InstanceId $id | Select-Object Status,Problem,ProblemDescription
```

**判读**：
- 变成 `Status: OK` → 直接开 Unity，进 Play，看 `SystemInfo.graphicsDeviceName` 是否变成
  `AMD Radeon RX 5700 XT`。变了就说明修好了，不用重启。
- 仍是 `Code 22` → 重启一次，再复查。
- 重启后**还是** `Code 22` 或变成 `Code 10/43` → **那是驱动本身起不来**，按下面处理：
  1. 停掉 `AMD Crash Defender Service`（服务管理器里设成"禁用"）
  2. 临时关掉 360 主动防御
  3. 用 AMD 官方安装包**重装显卡驱动**（干净安装 / 恢复出厂设置）
  4. 若中途用向日葵远程，先断开或用本地显示器操作

---

## 6. 修好之后怎么判定"真的好了"

同一个 Unity 工程、同一个分辨率，重采一次：

```
SystemInfo.graphicsDeviceName == "AMD Radeon RX 5700 XT"     ← 必须
FrameTimingManager.gpuFrameTime 应从 ~100 ms 掉到个位数       ← 必须
实测 fps 应从 ~10 升到 60+（编辑器 Play 模式）
```

探针已归档（删了就再也判不了同一件事，所以不算临时文件）：
- 读渲染设备（`unity command eval_file --file <路径>`）
- Play 模式采帧时间 + CPU/GPU 拆分

---

## 7. 对 skill 的修正建议（待用户确认）

`experience/perf-triage.md` 里「设备名 ≠ 真 GPU ⇒ 停，先修环境」**方向是对的**（本次再次命中），
但**修复路径那一步不完整**：原文写「`Enable-PnpDevice` → 重启重新枚举 → 若仍 10/43 则重装驱动」，
隐含着「重启就能生效」。本次实测反例：

- `ConfigFlags` 已归零、设备也重新枚举过（`LastArrivalDate` / 今天重启），**但 PnP 压根没尝试启动它**，
  重启后依旧 `Code 22`，且**没有任何启动事件**可查。
- 机器上同时有 `AMD Crash Defender` + 360 主动防御 + 向日葵虚拟显示器——都会写设备状态。

⇒ 建议把该节改成：**「启用 → `pnputil /scan-devices` → 当场复查状态；不 OK 才重启；重启后仍不 OK ⇒ 先停掉
Crash Defender / 安全软件，再重装驱动」**，并明确写「**重启不会自动启用被禁用的设备**」。

---

## 8. 修复结果（2026-09-20 后续，同一场景 / 同一分辨率 3440x1440）

**实际执行**（两条命令，全程**没有重装驱动、没有重启电脑**）：

```
① Enable-PnpDevice -InstanceId <AMD 卡 id>   → Code 22(禁用) 解除
                                              → 暴露 Code 31(驱动加载失败)
② pnputil /restart-device /instanceid <id>   → Status=OK / CM_PROB_NONE
```

**前后对比**：

| 指标 | 修复前 | 修复后 |
|---|---|---|
| 渲染设备 | `Microsoft Basic Render Driver`（CPU 软渲染）| **AMD Radeon RX 5700 XT** |
| 帧率 | 8.9 fps（90 帧 / 10.08 s）| **~328 fps**（197 帧 / 0.60 s，触探针 200 帧上限）|
| `gpuFrameTime` | ~90–100 ms | ~0.5–2 ms |
| `cpuMainThreadFrameTime` | ~87–113 ms | ~0.6–1.0 ms |
| `unscaledDeltaTime` | ~101 ms | ~2.5–5 ms |
| 设备状态 | `Code 22` → 解除后 `Code 31` | **OK / CM_PROB_NONE** |

**帧时间约 34 倍改善**，且修复后 `gpuFrameTime` 与 `cpuMainThread` 都回到个位数毫秒 ——
与"瓶颈在软渲染像素填充"的判断完全吻合，诊断闭环。

### 8.1 两个写进经验的点

1. **`restart-device` 才是这例的关键一步，不是重启电脑。**
   解除禁用后设备会停在 `Code 31`（驱动加载失败）；直接 `pnputil /restart-device` 当场就恢复了，
   **不需要重启、也不需要重装驱动**。
2. **Unity 编辑器会自己重新挑渲染设备，不必重启编辑器。**
   本次设备恢复后未重启编辑器，`SystemInfo.graphicsDeviceName` 已自行从 WARP 变回
   `AMD Radeon RX 5700 XT` —— 原判断"必须重启 Unity"是**过于保守**的，实际不用。
