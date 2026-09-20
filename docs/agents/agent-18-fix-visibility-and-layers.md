# agent-18：修「角色/武器不可见」+「actor 上飘 + 物理层缺失」（交付级缺陷）

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> 上一棒（agent-17）修好了动画状态机（`HasState` 全 True、状态真的切换），
> 但实机核对时发现**两个更早存在、更严重的缺陷** —— 本片修它们。

## 0. 三个症状与已实测证据（别重复采，直接用）

### A. 物理层没建 ⇒ 贴地/射线层判断全错 ⇒ actor 持续上飘

- **实测**：`client/ProjectSettings/TagManager.asset` 的 `layers:` 里只有 `Default / TransparentFX / Ignore Raycast / Water / UI`，
  **⛔ 没有 `CsWorld` / `CsPlayer` / `CsBot`**（而 `Core/CsConst.cs` 的 `PhysicsLayers` 定义了这三个名字与索引 8/9/10）；
- **后果**：`LayerMask.NameToLayer("CsWorld")` = **-1** ⇒ `CsMap.GroundMask()` 之类退化成 `~0` ⇒ **贴地射线打到角色自己的命中盒** ⇒
  自反馈抬升（实测：同一 actor 的 `y` 从 -3 一路涨到 500+，每 0.05 s 被抬一次）。
- ⚠️ 步骤文档说过"物理层由 Editor 脚本保证" —— **先去找那个脚本**（`Assets/Editor/**` 里 grep `TagManager` / `NameToLayer` / `layers`），
  看它是否本该设置这些层、为什么没生效（没跑？被覆盖？）。

### B. 角色模型在画面上**看不见**（只有名牌）

- **实测**（`74_visible_probe.png` 已由主 agent 亲眼核对：画面里只有名牌，没有身体；地图/天空盒/HUD 渲染正常）；
- 钉到 bot 正后方同帧：`dist=3.6 vp=(0.508,0.404,z=+3.123) smrEnabled=True isVisible=True forceRenderingOff=False tri0=1248 mat=player_CT_vip_vip`
  —— 几何在视口正中、渲染器自称可见、有三角形有材质，**但画面没有身体**；
- `SkinnedMeshRenderer.BakeMesh` 得到的包围盒 = `0.50×1.18×0.73`，而绑定姿态 = `1.25×2.05×1.96`
  ⇒ **被压扁（比例约 0.4/0.58/0.37，非均匀）且整体下沉约 1.2 m**。

### C. 第一人称武器（viewmodel）不在画面里

- 实测：`vp=(-0.865,0.994,0.064) dist=0.51` —— **在相机左侧视口外、且几乎贴在相机平面上**；5 帧采样（`72_vm_idle_now.png` / `72_vm_fire_0..3.png`）**都没有枪**。

### 为什么必须修（不是"差不多"）

旧交付把 `08_deagle.png`（当作 R2/R6 证据）与 `66_char_model.png`（R1/C-07 证据）当成"模型 OK"，
**实测这些图里同样没有模型** ⇒ 这几行**从来没有真正通过过**。
按 §2：表现类行必须"看得见"，数字/日志对不算数。

## 1. 只做这三件

1. **修 A（物理层）**：把 `CsWorld` / `CsPlayer` / `CsBot` 写进工程层配置（走既有 Editor 脚本或直接改 `ProjectSettings/TagManager.asset`，**二选一，说清选了哪个与为什么**）；
   同时在"层名解析失败"的路径上**补日志**（`Game.Logger.Error`，带上层名与 `NameToLayer` 返回值）—— ⛔ 不许静默退化成 `~0`；
   修完必须断言：`LayerMask.NameToLayer("CsWorld") > 0` 且 actor 的 `y` **不再单调上飘**（给出 5 s 采样）。
2. **修 B（角色不可见）**：定位"压扁 + 下沉"的根因，在
   `原版资源/cs16src/cs16_anim.py`（导出空间/绑定姿态）、`Assets/Editor/Views/AnimSetup.cs`（骨骼层级 / `rootBone` / 绑定姿态 / prefab scale）、
   `Module/View/ActorView.cs`（实例摆放 / 脚底抬升）之间找到**真正的**那一环，并给出**前后对比的数值证据**（bake 包围盒 vs 绑定包围盒 vs `CsActor.Position`）。
   ⛔ 不许用"放大 prefab / 手动挪位置"把现象盖住 —— 必须让 bake 结果与绑定姿态**同量级、脚底对齐**。
3. **修 C（viewmodel 不可见）**：按 `CsViewTuning.ViewModelLocalPosition/Scale` 与相机 FOV 的关系定位，
   修到**画面右下角能看到枪与手臂**；同样给前后对比。

> 三者可能有共同根因（例如"骨骼空间搞了两层"），**先统一排查再动手**；⛔ 不许只修一个就当完事。

## 2. 验证（必须实机，编辑器的用法见下）

现场：编辑器 PID 34268，`unity status` **在 `client` 目录内执行**才连得上；`unity command eval` 有 **5 s 主线程上限**（长任务用 `--detach`/job 轮询，见 `clover-tools/ai-skill/reference/pipeline-and-unity-cli.md`）；`--code` 里**不要写字符串字面量**（PowerShell 吃引号）。

1. **编译**：`recompile` + `recompile_status`（`compilationFailed:false`）；离线 `compile-check.ps1` ⇒ `csc exit=0`；
2. **实机断言**（逐条给原文）：
   - `LayerMask.NameToLayer("CsWorld") > 0`；actor `y` 在 5 s 内稳定（给采样序列）；
   - `BakeMesh` 包围盒与绑定姿态同量级、脚底 ≈ 0；
   - 角色在**第三人称视角**下可见（`capture_game_view --source screen`），枪在**第一人称**下可见；
3. **图**：修前/修后**同机位**各一张（放 `client/Assets/Screenshots/`，命名能看出是本次修复的前后对比）；
4. **重采受影响的行**（skill §1.13 第 6 条：只重采受影响的行，⛔ 不许全量重采）：`R1` / `R2` / `R5` / `R6`，以及联络图 3 里承载它们的格（`C-07` / `C-08`）；
   联络图重排只许动这几格；把 `策划/验收表.md` 对应行的**证据列与状态**更新；
5. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` ⇒ 除 `HUMAN-ONLY` 外无 FAIL。

## 3. 不许

- ⛔ 不许改 `策划/策划案/**`、`tools/ai-skill/SKILL.md`、任何全局 skill（`策划/验收表.md` 只许动 §2-4 说的那些行）；
- ⛔ 不许编造"已可见"（读不到图 ⇒ BLOCKED）；⛔ 不许拿"渲染器自称 isVisible"当"画面上看得见"；
- ⛔ 不许动与这三个症状无关的功能（**范围只许收不许放**）；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`，用完删；证据图放 `client/Assets/Screenshots/`；不许产出交接/进度类 md。

## 4. 回报格式

```
产出物：<文件绝对路径清单>
自检：<编译 + A/B/C 各自的根因与前后数值 + 图名 + verify.ps1 逐行>
未决：无 / <具体条目>
```
