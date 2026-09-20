# agent-15：实机取证（第 ④ 拍）—— 跑生成器 + 进 Play + 采联络图 + 重采截图

> 项目根：`clover-project-cs16`
> **用户的 Unity 编辑器已经开着**（这是本片的前提，已确认）。
> 本片是交付前的**唯一实机取证**，按 skill §1.13 第 ④ 拍走：**一条驱动链路按序跑完，只采一次**。

## 0. 已确认的现场事实（**别重复踩坑**）

| 事实 | 证据 |
|---|---|
| 编辑器在跑 | 进程 PID **34268**，标题 `Administrator: client - Boot - … Unity 6.6 (6000.6.0f1)` |
| Pipeline 已就绪 | `unity status`（**必须在 `client` 目录内执行**）⇒ `7802 / ready / …\client / 6000.6.0f1 / 34268` |
| ⚠️ **cwd 决定连谁** | 在项目根执行 `unity status` 会得到**空表**（那不是没开编辑器）。**所有 unity 命令都先 `cd client`**（或用 `--project-path`） |
| 编辑器编译正常 | `unity command console_status` ⇒ `compilationFailed:false, compiling:false, consoleErrors:0` |
| 面板生成器**已成功跑过一次** | `unity command eval --code 'Cs16.EditorTools.UiBuilder.Generate(); return 1;' --timeout 300` ⇒ `success:true, result:1`（可重跑） |
| ⚠️ **`eval` 有主线程 5 s 上限** | `Cs16.EditorTools.ArtSetup.Generate()` ⇒ `Main thread operation timed out after 5000ms`（它要生成 615 个 clip + 68 个 prefab）⇒ **必须用后台/分离方式** |
| 中文/引号会被 PowerShell 吃掉 | `--code` 里**不要写字符串字面量**（`return "x";` 会被传成 `return x;`）。要返回就 `return 1;` |

> **长任务的正确打法先查**：`clover-tools/ai-skill/reference/pipeline-and-unity-cli.md`（P-0~P-5，含 `eval_file` 形态、
> `--detach` / job 轮询、域重载、失焦不 tick、截图限制）。**先读它再试**，别自己发明。

## 1. 开工必做

1. `use_skill("clover-engine")` + `use_skill("unity-cli")`。
2. 读 `策划/验收表.md`（**它的「联络图索引」小节就是你的证据分格表**：联络图 1/2/3 的格号范围与覆盖行）。
3. 读 `tools/ai-skill/SKILL.md` + `constraints.md`（本项目的静默失败清单：**改了 UI 不重跑生成器就看不到变化**）。

## 2. 只做这五件

### 2.1 把生成器跑完（`ArtSetup` 是重点）

- `Clover/CS16/生成游戏内面板`（`Cs16.EditorTools.UiBuilder.Generate()`）—— 已成功，**再跑一次**确保含秒表图标的最新版本落地；
- `Clover/CS16/整理视图与音效资源`（`Cs16.EditorTools.ArtSetup.Generate()`）—— **必须成功**：
  它要产出骨骼层级 + `SkinnedMeshRenderer` + `AnimationClip`(615) + `AnimatorController` + 68 个预制体。
  主线程 5 s 上限的解法按 `pipeline-and-unity-cli.md` 来（`--detach` + job 轮询 / `eval_file` / 分批 + `EditorApplication.delayCall`）。
  **完成后必须验证产物真的在磁盘上**（`Assets/Resources/Art/**`、`Assets/Editor/Views/ModelData/*.cs16anim` 的消费结果、
  `.anim` / `.controller` 的数量），**⛔ 不许只凭"命令返回 true"就算成功**。
- 生成器报错 ⇒ 先看 `unity command console --tail 200 --level error`；**编译/逻辑问题你可以在 `Assets/Editor/**` 里修**（那是你的授权范围），改完重新编译并重跑。

### 2.2 进 Play，按分格表跑完一条链路

- `unity command editor_play`（进 Play；注意 skill 的坑：**失焦不 tick**、第二次进 Play 全蓝）；
- 用 Pipeline 的 `eval` 驱动既有逻辑（**不要自己写新的业务代码**）走完：
  启动画面 → 主菜单 → New Game → 读条 → 选阵营 → 进图 → 买枪 → 交战 → 下包/拆包 → 回合结算 → 半场 → 比赛结束 → 回主菜单；
  以及 ESC 暂停 / TAB 记分板 / H 菜单 / 无线电 / 控制台。
  项目里已有大量可复用的驱动脚本在 `.ai-tmp/test/`（**历史遗留、可读可跑**，如 `click-newgame.cs` / `plant-c4.cs` / `open-buy.cs`…），**优先复用**。
- **判据**：每一步都要拿到**运行时日志行**（`数值类` 的证据）或者**画面**（`表现类`）。

### 2.3 采联络图（**只采 3 张**，AI 只读汇总图）

按 `策划/验收表.md` 的「联络图索引」：

| 联络图 | 格号 | 覆盖 |
|---|---|---|
| 1 · 菜单链路 | `A-01`~`A-09` | M1 / M2 / M3 / M4 / M5 / M6 / M7 / M8 / M10 |
| 2 · 游戏内表现 | `B-01`~`B-16` | H1~H5 / H7~H15 / G3 / G4 |
| 3 · 地图与资源 | `C-01`~`C-09` | P1 / D3 / D5~D8 / R1 / R2 / R5 / R6 / R7 / R8 |

- 截图用 `unity command capture_game_view --source screen --save_path <path>`（**`source=screen` 才含 overlay UI**；`camera` 会漏掉 HUD）；
- 拼成联络图时**每格烧上「格号 + 状态 + 关键数值」**（字体要能被 OCR/肉眼读清）；
- 联络图与单帧证据都放 `client/Assets/Screenshots/`（**⛔ 不许放 `.ai-tmp/`**——验收表引用它们）；
- **`M2` 的署名判据是"渲染出来的东西"**：必须在**主菜单那一格**能**逐字**读出 `by clover-engine`（`by` / 连字符 / **大小写全对**；⛔ grep 源码不算）。
  你读图确认后，把结论写进回报（⛔ 读不到图/图被省略 ⇒ 立即报 `BLOCKED：图像通道不可用`，**不许编画面描述**）。

### 2.4 重采过期的表现类单帧

`client/Assets/Screenshots/` 里 31 张旧图**全部早于最后一次代码改动 ⇒ 已作废**。
按验收表「证据」列逐个重采（文件名沿用，便于对照）。**采完每张都要满足 mtime 晚于最后一次代码改动**。

### 2.5 回填验收表（**只填证据，不改判据**）

- 把每行的「证据」列更新成**新采的图名**与**真实日志行原文**；
- `R5` / `R6`（动画）与 `H6`（雷达 BLOCKED）按实际情况改状态；
- 「收尾自检」第 6 条（证据新鲜度）改成本次实测结论；
- ⛔ **不许为了让表好看而改判据、改类别、删行**；⛔ 拿不到证据的行**如实留 BLOCKED**。

## 3. 判据（自己跑，原始输出贴进回报）

1. **编译**：`powershell -NoProfile -ExecutionPolicy Bypass -File .ai-tmp\test\compile-check.ps1` ⇒ `csc exit=0`；
2. **闸门**：`powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` ⇒ **`evidence-freshness` 转 PASS**（除 `HUMAN-ONLY` 外无 FAIL）；
3. **生成器产物**：`.anim` / `.controller` / 预制体的**实际文件数与路径**（`Get-ChildItem` 计数，不是"命令返回 true"）；
4. **联络图**：3 张图的文件名 + 每张覆盖的格号 + 你逐格读出的结论（一致 / 差在哪）；
5. **`M2` 署名**：你读图看到的**逐字文本**（含大小写）；
6. **运行时日志**：每条数值类判据的**日志原文**（编辑器 Console 或 `client/Logs/*.log`）。

## 4. 不许

- ⛔ 不许改 `策划/验收表.md` 的**判据 / 类别 / 行**（只许更新证据列与状态）；不许改 `策划/策划案/**`、`tools/ai-skill/**`、任何 skill；
- ⛔ 不许编造画面结论（读不到图 ⇒ BLOCKED）；⛔ 不许拿"命令返回 true"当"资产已生成"；⛔ 不许拿离线断言当实机证据；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`（自检宿主 `.ai-tmp/hosts/`），**证据图必须放 `client/Assets/Screenshots/`**；
- ⛔ 不许产出交接/进度类 md。

## 5. 回报格式

```
产出物：<文件绝对路径清单>
自检：<compile-check 原文 + verify.ps1 逐行 + 生成器产物计数 + 联络图逐格结论 + 署名逐字 + 关键日志原文>
未决：无 / <具体条目>
```
