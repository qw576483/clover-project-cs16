# agent-25：日志能力下沉（S1 降频计数口径 + S2 运行时日志缓冲）

> **跨仓库片**：引擎 `clover-client-unity-engine` ＋ 项目 `clover-project-cs16`
> 本片是「能把项目里通用能力下沉到引擎」的第 1 片（共 2 片：25 = 日志，26 = UI 控件 + 音效）。
> 用户已明确：**本机没开 Unity，不要求跑编辑器/PlayMode**；但**离线编译必须跑**（引擎结构规则 §7 第 8 条）。

## 0. 引擎侧的硬规则（先读，别自己发明）

- `clover-client-unity-engine\结构规则.md`：§2.1 依赖方向（各模块只引用 `Core`；`Core` 的 asmdef 引用必须为空）、§2.2 新增模块判定五问、§3.1 Core 允许/禁止、§4.4 复用规则（**已有同类能力不准再起第二套**）、§6 文档规则（结构改了文档必须同步，否则视为未完成）。
- `clover-client-unity-engine\clover-client-unity-engine-index.md`：§3.4 基础域速查（要同步改）、§4 Game 门面。
- 下沉的**注释规范**照抄现成样板：`Runtime/Core/LogThrottle.cs:1-26` —— 顶部块注释必须写清「出处 / 没下沉什么 / 为什么下沉 / 语义约束（改一条 = 语义漂移）」。

---

## S1. 把 `CsModuleLog` 的**计数口径**下沉进 `LogThrottle`

### 现状（已核实，别重查）

- 引擎 `Runtime/Core/LogThrottle.cs` 已有**时间间隔**口径：`ShouldLog(key, intervalSeconds)` / `WarnThrottled` / `ErrorThrottled` / `WarnOnce` / `ErrorOnce` / `Reset()`。
- 项目 `client/Assets/Scripts/Module/Player/CsModuleLog.cs`（78 行）是**次数计数**口径，被 **12 个文件**使用：
  口径 = 每 key 计数，**第 1 次必打**，之后**每 N 次打一条**（`N = CsCombatTuning.LogRateEvery`），第 1 次之后的行尾加 `（同类第 N 次）`；空 key 归并为 `"default"`。
- ⚠️ **两种口径语义不同，不能互相替换** —— 所以是「把计数口径补进引擎」，不是「让项目改用时间口径」。

### 要做的

**① 引擎 `Runtime/Core/LogThrottle.cs` 新增计数口径**（⛔ 不许改动任何既有方法的语义）：

```csharp
// 计数闸门：每 key 独立计数；count==1 恒 true；之后 count % everyN == 0 才 true
public static bool ShouldLogEvery(string key, int everyN = 50);

// 下面三个：命中则输出且返回 true（行尾自动补 “（同类第 N 次）”，count==1 时不补）
public static bool InfoCounted(string tag, string key, string message, int everyN = 50);
public static bool WarnCounted(string tag, string key, string message, int everyN = 50);
public static bool ErrorCounted(string tag, string key, string message, int everyN = 50);
```

语义与参数约束（写进 XML 注释，作为长期契约）：
- `everyN <= 1` ⇒ 视为 1（= 每次都打，**不许**除零/死循环）；
- `key` 为空/null ⇒ **归并为 `"default"`**（与 S1 原有 `ShouldLog` 的空 key 语义**刻意不同**：那个是"恒 false + 只报一次"，**不许改它**）；
- `Suppress == true` ⇒ 三个 Counted 一律 false（与既有 `ShouldLog` 一致）；
- 后缀格式逐字：`（同类第 {count} 次）`（**全角括号**，与项目现值一致，否则日志检索脚本会对不上）；
- 非线程安全（主线程使用），与既有注释口径一致。
- **`Reset()` 要连带清空计数表**（语义 = "清空所有限频记录"），并把这条写进 `Reset()` 的注释。

出处注释：按 `LogThrottle.cs:1-26` 的版式，在本文件顶部块注释里补一段
`S1 计数口径出处：clover-project-cs16 的 client/Assets/Scripts/Module/Player/CsModuleLog.cs（78 行）`，
并写明「未下沉」的是 `Always`（不降频直发，引擎已有 `Game.Logger.Info`，不重复造）。

**② 项目 `Module/Player/CsModuleLog.cs` 改成薄适配**（⛔ 类名 / 命名空间 / 公开签名 / tag 实例这些**调用面一律不动**，12 个调用文件一行都不用改）：

- 删掉自己的 `_counters` 表与 `switch`，`Info/Warn/Error` 转发到 `LogThrottle.InfoCounted/WarnCounted/ErrorCounted`，`Always` 转发到 `Game.Logger.Info`，`Reset()` 转发 `LogThrottle.Reset()`；
- `LogRateEvery` 常量**保留**（它是业务侧数值，属 `CsCombatTuning`），继续作为默认 `everyN` 传入；
- 文件头注释改写为「本类已降级为引擎 `LogThrottle` 的**薄适配**（per-tag 转发）；计数口径唯一真相包在引擎」+ 出处/理由，⛔ 不许再自称"本项目新增的高频路径降频器"。

### 判据

- `CsModuleLog.cs` 里 **`_counters` 归零命中**（`Select-String -Pattern '_counters'` 无输出）；
- `LogThrottle.cs` 新增 4 个方法 + `Reset()` 注释已改；
- 12 个调用 `CsModuleLog` 的文件**一行未改**（用 `git status` / mtime 证明）；
- 离线编译通过（见 §3）。

---

## S2. 把运行时**日志环形缓冲**下沉成引擎 `Runtime/Core/LogBuffer.cs`

### 现状

- 项目 `client/Assets/Scripts/UI/InGame/CsLogBuffer.cs`：**零 `Cs*` 引用**（只依赖 `CloverEngine` + Unity），自述"引擎 `ILogger` 只提供写、没有读取/订阅接口"。
- 引擎 `Runtime/Core/Logger.cs:52-95` 的 `ILogger` 确实只有 `Debug/Info/Warn/Error/Fatal`，**无读取/订阅**（已搜 `event|Subscribe|OnLog|Read|Lines` 无命中）。
- 项目调用点只有 4 处，都在 `UI/InGame/ConsolePanel.cs`（`:102,121,149,152`）。

### 要做的

**① 引擎新增 `Runtime/Core/LogBuffer.cs`**（`namespace CloverEngine`，程序集 `CloverEngine.Core`，⛔ **纯新增，不许碰 `Logger.cs`**）：

```csharp
public static class LogBuffer
{
    public const int DefaultCapacity = 400;
    public static int Capacity { get; }              // Install 时确定
    public static int Version { get; }               // 每次内容变化自增（供 UI 判“要不要重画”）
    public static IReadOnlyList<string> Lines { get; } // 只读视图，G8：不许把可变表交出去
    public static bool Installed { get; }

    public static void Install(int capacity = DefaultCapacity); // 幂等；挂 Application.logMessageReceivedThreaded
    public static void Uninstall();                              // 解挂；可重复调用
    public static int Drain();                                   // 返回“自上次 Drain 起新增的行数”，并把新增计数归零
    public static void Push(string text);                        // 本地回显（控制台里输入的那一行），也计入 Version
    public static void Clear();
}
```

语义约束（写进 XML 注释，作为长期契约）：
- **线程**：`Application.logMessageReceivedThreaded` 在**任意线程**触发 ⇒ 入队必须线程安全（`lock` 或 `ConcurrentQueue`），**但 `Version` / `Lines` / `Drain` 只保证主线程读**（与引擎 `Dispatcher` 的口径一致）；
- 环形覆盖：超过 `Capacity` 丢最旧；`Capacity <= 0` 视为 `DefaultCapacity`；
- `Install` 幂等（重复调不重复挂）；`Uninstall` 后再 `Install` 要能重新工作；
- `Push(null/空)` ⇒ 忽略（不许把空行塞进面板）；
- ⛔ **不许**做成 `ILogger` 的第二套实现（它不是 logger，是"最近 N 行"的只读缓冲窗口）；
- 出处注释：`出处：clover-project-cs16 的 client/Assets/Scripts/UI/InGame/CsLogBuffer.cs`；
  并写明「未下沉」的是"面板怎么画"（那是表现/业务）。

**② 项目侧：删 `CsLogBuffer.cs`，4 处调用改 `LogBuffer`**

- `ConsolePanel.cs` 的 4 处引用改指向引擎 `LogBuffer`（`using CloverEngine;` 已在该文件可用则不必再加）；`Capacity/Version/Lines/Drain/PushLocal/Clear` 一一对应（`PushLocal` → `Push`）；
- 删掉 `UI/InGame/CsLogBuffer.cs` 及其 `.meta`；
- 若 `ConsolePanel` 里 `Install` 的时机原本在别处（如 `AppFlow`），一并改；**装/卸的时机与原来一致**（不许顺手改启动顺序）。

### 判据

- `CsLogBuffer.cs` 与 `CsLogBuffer.cs.meta` 均已不存在；
- 全项目 `Select-String -Pattern 'CsLogBuffer'` ⇒ **0 命中**；
- `LogBuffer.cs` 存在且离线编译通过。

---

## 3. 离线编译验证（**必跑**，不需要 Unity）

闸门：`.codebuddy\doc-audit\tools\compile-check-client.ps1`

- 先读该脚本头部注释确认用法（按 asmdef 边界分步编译）；
- **引擎侧**改动要能编过（尤其 `Core` 的 asmdef 引用列表**必须仍为空** —— 新增文件不许引入任何依赖）；
- **项目侧**要能编过；
- 回报里贴**原始输出末尾**（成功/失败行 + 退出码）。
- ⛔ 若脚本当前不吃「只编引擎」，**不许**为了过闸门去改脚本或 asmdef；改成手动 `csc` 也行，但要在回报里说明用了什么命令、为什么。

## 4. 文档与注释同步（**本片必须一起做**，引擎 §6：不同步 = 未完成）

1. `clover-client-unity-engine\clover-client-unity-engine-index.md`：
   - §3.4 基础域表格里给 `LogThrottle`（若有行）补「时间 + **计数**两种口径」；**新增 `LogBuffer` 一行**（"运行时日志环形缓冲：最近 N 行只读窗口 + Version + 线程安全入队；补 `ILogger` 只写不读的缺口"）；
   - §0 若列了"必查能力清单"之类，同步加 `LogBuffer`。
2. `clover-client-unity-engine\结构规则.md`：§4.4 的**必查能力清单**（`Event · Timer · Fsm · Dispatcher · Logger · Json · Setting · …`）里补 `LogThrottle`（含计数口径）与 `LogBuffer`。
3. 两个新/改文件的**顶部块注释**（出处/未下沉/为什么/语义约束）必须齐全。
4. ⛔ **不许**新增 README、不许产出交接/进度类 md；`Runtime/**` 的规矩是"实现注释即文档"。

## 5. skill 同步（本片只做**登记**，内容我主 agent 收尾统一核）

- 若 `clover-tools\ai-skill\**` 里存在"引擎已有能力清单 / 必查清单"这类**列表**（用 `Select-String` 搜 `LogThrottle`、`必查能力`、`Event`+`Timer`+`Fsm` 同段出现），把 `LogThrottle`/`LogBuffer` 补进**同一处**；
- ⛔ 找不到就不许新建小节、不许改 skill 的规则句；把"搜了什么、结果如何"写进回报即可（我收尾时决定）。

## 6. 不许

- ⛔ 不许改任何既有方法的**语义**（`ShouldLog` / `WarnThrottled` / `WarnOnce` / `ErrorOnce` / `Logger` / `ILogger` 全部只读，除非本任务书写明）；
- ⛔ 不许让 `CloverEngine.Core` 的 asmdef 引用任何其他程序集（§2.1 铁律，改它 = 直接判错）；
- ⛔ 不许改 `clover-project-cs16` 的 `策划/**`、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；
- ⛔ 不许读工作区里别的 `clover-project-*` 工程；
- ⛔ 不许开子 agent；不许新增 README/交接 md；
- 临时脚本只放 `.ai-tmp\test\`，用完删。

## 7. 回报格式

```
产出物：<引擎侧文件 + 项目侧文件，绝对路径>
S1 判据：_counters 命中数 / 12 个调用文件是否未改 / 新方法签名
S2 判据：CsLogBuffer 残留命中数 / LogBuffer 方法签名
编译：<命令 + 原始输出末尾 + 退出码>
文档：index §3.4 与 结构规则 §4.4 的改动后原文（逐段贴）
skill：搜了什么 / 有没有可登记处 / 结果
未决：无 / <具体条目>
```
