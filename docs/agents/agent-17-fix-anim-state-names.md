# agent-17：修动画状态名不匹配（R5/R6 未通过）（clover-project-cs16）

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> **这是 bug 修复**：动画资产已生成（615 `.anim` / 38 `.controller`），但**状态机切不动**。

## 0. 现象与证据（上一棒实机测出来的，别重复采）

```
ctl=player_CT_gsg9  HasState('idle1')=False  HasState('player_CT_gsg9_idle1')=True
                    cur==hash('player_CT_gsg9_idle1')? True   normTime=432.8  len=4.067
日志 37 条（2026-09-19 运行日志）：[View] …找不到状态 jump / idle1 / idle（36 条）/ death2 / death3 / death1（1 条）
```

**根因**：`client/Assets/Editor/Views/AnimSetup.cs:403` 附近用 `clips[i].name`（= **带皮肤前缀**的 clip 资产名，如 `player_CT_gsg9_idle1` / `vm_glock18_idle`）
作为 AnimatorController 的 **state 名**；
而运行时的候选表来自 `CsViewTuning.PState*` / `VmState*`（**无前缀**，如 `idle1` / `run` / `idle`），
见 `Module/View/ActorView.cs:213-230` 的 `ResolveState` 与 `Module/View/ViewModelRig.cs:307-320` 的 `ResolveState`，候选定义在 `Module/View/CsViewTuning.cs:174/197`。
⇒ `Animator.HasState(...)` 恒 `False` ⇒ `CrossFade/Play` 从不执行 ⇒ 只有 controller 的 `defaultState` 在自动循环。

## 1. 只做这一件（但要做对）

**让"运行时候选名"与"controller 状态名"对齐**，两选一（选完在代码注释里写清**为什么选它**）：

- **方案 A（推荐）**：改生成器 —— `AnimSetup` 生成 state 时用**去前缀的状态名**（与 `CsViewTuning` 的候选一致）。
  必须处理**同名冲突**：同一个 controller 里若出现两个同名状态（例如同 controller 内含多套 clip），要么按语义合并、要么就用你选定的确定性规则去重；
  `.anim` **资产文件名可以不变**（只改 state 名），避免牵动其它引用。
- **方案 B**：改运行时 —— `ResolveState` 按当前皮肤/武器补前缀。
  ⚠️ 选它必须说清"运行时怎么知道前缀"，且 `CsViewTuning` 的候选表要相应扩展（⛔ 不许把前缀硬编码进业务）。

**硬要求**：

- ⛔ 不许只改 `HasState` 的调用点把错误掩盖过去（例如"查不到就 Play(0)"）—— 那会让状态切换仍然不发生；
- ⛔ 不许改契约签名；
- 状态名、序列名、帧率**仍然只能来自原版 mdl 实测**（`原版资源/cs16src/cs16_anim.py`），⛔ 不许自己起名；
- 改动后每条"找不到状态"的路径都要有日志兜底（保留现有的 Warn），并在**修好后确认它不再触发**。

## 2. 验证（必须实机，⛔ 不许只离线断言）

用户的编辑器开着（PID 34268，`unity status` **在 `client` 目录内**才连得上；当前它可能还在 Play —— 先 `unity command editor_stop`，改完 `unity command recompile` 等编译完成，再跑生成器，最后 `editor_play`）。

1. **生成器**：跑 `Cs16.EditorTools.ArtSetup.Generate()`（长任务，用 `--detach` / job 轮询，见 `clover-tools/ai-skill/reference/pipeline-and-unity-cli.md`；⛔ 不许只凭"命令返回 true"就说成功，**要验证磁盘上的 state 名真的变了**——可直接读 `.controller` 资产或用 `eval` 断言）；
2. **实机断言**（用 `unity command eval`，代码里不要写字符串字面量 —— PowerShell 会吃掉引号）：
   - `Animator.HasState(0, Animator.StringToHash("<候选名>"))` 对 **idle1 / run / walk / crouch_idle / jump / death1** 逐个为 **True**；
   - 进 Play 后驱动角色状态变化（走 / 跑 / 蹲 / 死），断言 **当前 state hash 真的变了**（不是一直停在 defaultState）；
   - `unity command console --tail 300 --level warn` ⇒ **不再出现 `[View] …找不到状态`**；
3. **重采受影响的证据**（skill §1.13 第 6 条：**只重采受影响的行**，⛔ 不许全量重采）：
   与角色/武器动画有关的行（验收表 `R5` / `R6`，以及依赖"角色在动"的 `G4` / `B7` 等你自己判断），
   把新图放 `client/Assets/Screenshots/`（文件名沿用或新增），并把验收表对应行的证据列更新；
4. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` ⇒ 除 `HUMAN-ONLY` 外无 FAIL。

## 3. 顺手更正一处**差异措辞**（在 `策划/验收表.md`，允许你改这一处）

「允许的差异」#12 现在写的是"死亡序列 3 选 1 且播完才隐藏" —— 实测并不成立（`ResolveState` 失败后是**立即** `SetShown(false)`）。
改成如实描述（死亡序列按 actorId 取一条；**在状态名修复后**是否播完才隐藏，以你这次的实机观察为准）。

## 4. 不许

- ⛔ 不许改 `策划/策划案/**`、`tools/ai-skill/SKILL.md`、任何全局 skill（`策划/验收表.md` 只允许动上面那一处 + 你自己的行证据）；
- ⛔ 不许编造实机结论（读不到图/断言不过 ⇒ BLOCKED）；⛔ 不许拿离线断言当实机证据；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`，用完删；证据图放 `client/Assets/Screenshots/`；不许产出交接/进度类 md。

## 5. 回报格式

```
产出物：<文件绝对路径清单>
自检：<编译 + 生成器产物核对 + HasState 断言原文 + 状态切换断言 + 无"找不到状态"日志 + 重采的图名>
未决：无 / <具体条目>
```
