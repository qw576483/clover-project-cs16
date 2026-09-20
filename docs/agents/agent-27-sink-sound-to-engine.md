# agent-27：音效闸门下沉（S4）

> **本片已于 2026-09-19 21:37 首派并完成（`E-core-17`，主体见引擎 `修复记录.md:1260`）。**
> **本文档是 2026-09-20 07:16 主 agent 基于过期认知的重复改写版；当日复派实际做的是「核验 + 收口」**：补掉首轮遗留的 2 处裸 `Warn`（`PlayBGM` 缺失分支 / 池满告警，只换发射通道）、补文档与 skill、重出全部证据（30 PASS）。复派不涉及项目侧文件改动。

> **跨仓库片**：引擎 `clover-client-unity-engine` ＋ 项目 `clover-project-cs16`
> **本片串行执行**（agent-28 在本片核验通过后才派）。⛔ 不要假设有别的片在同时改文件。
> 串行原因：`compile-check-client.ps1` 的产物落在**共享目录** `csc-out\`，两片同时跑会互相覆盖 `CloverEngine.*.dll`（项目侧要拿"刚产出的那份"编，会被污染成假失败/假通过）。
> **E 号已由主 agent 预分配：本片占用 `E-core-17`**（⛔ 别自己取号、⛔ 别顺延）。
> 用户没开 Unity：**不要求跑编辑器/PlayMode**，但**离线编译必须跑**。

## 0. 先读

- 引擎 `结构规则.md` §2.1 / §3.5 / §4.4（已有能力不准再起第二套）/ §6（文档不同步 = 未完成）。
- 下沉注释版式样板：`Runtime/Core/LogThrottle.cs:1-26`。
- ⚠️ 本片会用到 `LogThrottle.WarnOnce`（E-core-06，已存在）与它的**计数口径**（E-core-14，刚下沉，见 `Runtime/Core/LogThrottle.cs`）。

## 1. 现状（已核实）

引擎 `Runtime/Presentation/Sound.cs`：

- `:196 PlaySFX` / `:214 PlaySFXAt` / `:233 PlayVoice` —— 三处在 `clip == null` 时**每次调用**都 `Game.Logger?.Warn("Sound", ...)` ⇒ **高频缺失路径会把日志刷爆**；
- `:255 TakeSource(path, clip, group)` —— 取空闲音源，池满/回调晚到都**静默 return**；
- 全类**没有**任何"每秒/每帧/同 clip 并发"的闸门，也没有"探测一次、缺失只告警一次"的缓存。

项目 `client/Assets/Scripts/Module/Audio/SfxService.cs`（201 行）全在补这些短板：
`Prewarm(params)` / `Play(clip)` / `PlayAt(clip,pos)` / `PlayRandom(clips,spatial,pos)` / `ReadyCount` / `DroppedCount`，
闸门常量在 `CsAudioTuning.MaxPlaysPerFrame / ClipWindow / MaxConcurrentPerClip / LogRateEvery`（`:104,118-119,173,192,196` 附近）。

## 2. 要做的

### 2.1 引擎：**扩展** `Runtime/Presentation/Sound.cs`（⛔ 不新建第二套 Sound）

1. **缺失音效只报一次**：三处 `clip == null` 的分支改用 `LogThrottle.WarnOnce("Sound", "missing:" + path, msg)`
   —— key 用 `"missing:<path>"`（同一路径整个进程只报一次），保留原有 message 文案与 `path` 变量；
2. **两个可配闸门**（挂在 `SoundManager` 上，或经 `ISoundManager` 暴露 —— 看现有形态择一，别破坏 G1「只暴露接口」）：
   - `MaxPlaysPerFrame`：单帧最多真正播放几次，**默认 `0` = 不限**；
   - `MaxConcurrentPerClip`：同一 clip 路径同时播放的上限，**默认 `0` = 不限**；
   - 超限 ⇒ **丢弃该次播放**（不排队），并**限频告警**（用 `LogThrottle.WarnThrottled`，⛔ 不许裸 Warn）。
3. ⛔ **默认值必须完全保持现有行为**（`0` = 不限 = 与今天逐字一致）——否则会悄悄改掉别的项目的表现。
4. 语义约束写进 XML 注释 + 顶部块注释（出处 / 未下沉什么 / 为什么 / 语义约束）。
   **未下沉**：音效名表、分组音量值、`cs.*` 键 —— 那些是业务。

### 2.2 项目侧：`SfxService` 不许再是"第二套闸门"

- 目标是**唯一真相包在引擎**：项目侧要么**删掉**（调用点改调 `Game.Sound` + 引擎闸门），要么降为**薄转发**（不做任何自己的计数）。
- 二选一由你定，但**判据必须满足**：`SfxService.cs` 里**不再出现**自己的计数表 / 帧计数 / 并发表 / 探测缓存（这些逻辑只能有一份，在引擎）。
- 调用点（`AudioModule` / `CombatAudio` / `GrenadeThrower` 等）**尽量不动**；若必须改，逐个在回报里列出行号与理由。
- ⛔ 不许改任何音效名/音量/业务语义。

## 3. 判据（自己跑，原始输出贴回报）

1. `Sound.cs` 里 `Game.Logger?.Warn("Sound"` 的**裸调**归零（改为 `LogThrottle.*`）；
2. 引擎新增两个属性的签名 + 默认值（`0`）；
3. 项目侧：`SfxService.cs` 内自建闸门残留 = 0（贴出你搜的模式与命中数）；若删除，贴出调用点改法；
4. **离线断言**（不需要 Unity，可建临时宿主 `.ai-tmp/test/`，用完删）：默认 `0` 时"行为与改前一致"（不限流）、设成 `N` 时第 N+1 次被丢弃且**只**限频告警、`MaxConcurrentPerClip` 生效口径；
5. 引擎闸门 `compile-check-client.ps1` ⇒ `RESULT: ALL PASS` / `GATE_EXITCODE=0`；项目侧按 `Cs16.asmdef` 边界 Roslyn ⇒ `EXITCODE=0`（参考集用**闸门刚产出的** `csc-out\CloverEngine.*.dll`，⛔ 不要用 `Library\ScriptAssemblies\` 那份旧的）。

## 4. 文档 / skill / 注释（**只追加自己的条目**）

1. 引擎 `clover-client-unity-engine-index.md` §3.2 的 **Sound 行**：补两个闸门 + 缺失只报一次；把"设置持久化未实现"那条边界保留原样。
2. 引擎 `结构规则.md` §4.4 必查能力清单的 `Sound` 处：补一句"音效闸门（单帧上限 / 同 clip 并发上限 / 缺失只报一次）"。
3. 引擎 `修复记录.md`：**只在文件末尾追加**一节 `E-core-17`（⛔ 别插在中间、⛔ 别改号），格式仿 `E-core-13`（性质/编号/新增/能力/缺陷本质/判据/文档同步表）。
4. skill：`clover-tools\ai-skill\reference\engine-mental-model.md` §4 能力表的 **Sound 行**（若无 Sound 行，就在 `Game.Sound` 那行上**追加**内容，⛔ 不新建小节、不改规则句），E 号写 `E-core-17`。
   **改完必须同步宿主副本**：`Copy-Item <repo>\reference\engine-mental-model.md "$env:USERPROFILE\.codebuddy\skills\ai-skill\reference\engine-mental-model.md" -Force`（两副本不一致会被 `skill-health.ps1` 判 FAIL）。

## 5. 不许

- ⛔ 不许改 `Sound.cs` 除上述三点外的任何行为（分组音量、BGM 淡入淡出、`TakeSource` 的池逻辑、`Dispose` 语义一律不动）；
- ⛔ 不许改 `Logger.cs` / `ILogger` / `LogThrottle` 既有方法语义；⛔ 不许让模块间互相引用 asmdef；
- ⛔ 不许改项目 `策划/**`（除你按 §1.12 必须登记的过期证据，但本片预计不涉及）、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；⛔ 不许新增 README / 交接类 md；临时脚本只放 `.ai-tmp/test/` 用完删；
- ⛔ 不许自行改 E 编号（本片固定 `E-core-17`）。

## 6. 回报格式

```
产出物：<引擎 + 项目 文件清单>
2.1 判据：裸 Warn 归零命中 / 两个新属性签名与默认值 / 三处 clip==null 改后原文
2.2 判据：SfxService 自建闸门残留命中数 / 调用点改动清单（或"未动"）
离线断言：<命令 + 原始输出>
编译：<引擎 + 项目 两条命令的原始输出末尾与退出码>
文档/skill：改动位置与改动后原文（逐段贴）
E 号：你实际占用哪个
未决：无 / <具体条目>
```
