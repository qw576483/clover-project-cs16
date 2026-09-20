# agent-20：收掉最后一条过期证据（P1）+ 刷新验收表口径文本

> 项目根：`c:\Work\Server\full-dev\clover-project-cs16`
> 闸门现在只剩一条 FAIL，且是**真阳性**（不是口径问题）。

## 0. 现状（闸门原文）

```
PASS ...（其余全绿，含 acceptance-table 64 rows / row-category / impl-by-executor 18/18 / no-escaped-artifacts）
FAIL        evidence-freshness  1 of 43 (row,shot) pair(s) are stale => only those rows are void
            row P1: shot 05_ingame_ct.png (09-19 12:50) is older than that row implementation file Dust2Builder.cs (09-19 15:01)
HUMAN-ONLY  engine-credit
```

**为什么 P1 是真阳性**（同机实测 mtime）：
```
client/Assets/Editor/MapGen/Dust2Builder.cs    09-19 15:01:44   ← P1 所述实现文件（这次改动过）
client/Assets/Scenes/StageDust2.unity          09-19 15:15:35   ← 15:01 之后地图**真的重烘过**
client/Assets/ThirdParty/Dust2/Materials/*.mat 09-19 15:15:2x~3x ← 整套材质同批重写
client/Assets/Screenshots/05_ingame_ct.png     09-19 12:50:43   ← P1 现在引用的图（重烘之前那一帧）
```

## 1. 只做这三件

### 1.1 重采 `P1`（几何与贴图）的证据

- 进 Play 到 `StageDust2` 里，采**至少一帧能看清地图几何与贴图**的图（第三人称/第一人称均可，要求能看到沙色石墙、拱窗/木箱之类的原版贴图特征）——
  **mtime 必须 ≥ `StageDust2.unity` 的 15:15:35**（即重烘之后）；
- 图放 `client/Assets/Screenshots/`（**沿用 `05_ingame_ct.png` 覆盖**，或新增一个更贴切的名字并同步改验收表引用——二选一，改完保持表内引用可达）；
- 更新 `策划/验收表.md` 的 `P1` 行证据列；
- 编辑器用法（**都在 `client` 目录内执行**）：`unity status` 确认 `ready`；`unity command editor_play` 进 Play；
  用既有驱动脚本（`.ai-tmp/test/` 里的历史脚本可读可复用）走到游戏内；`unity command capture_game_view --source screen --save_path <绝对路径>`
  （⚠️ **必须 `source=screen`**，`camera` 会漏掉 overlay UI）；采完 `editor_stop`。
  ⚠️ `unity command eval` 有 **5 s 主线程上限**，长任务用 `--detach`/job 轮询（见 `clover-tools/ai-skill/reference/pipeline-and-unity-cli.md`）；
  `--code` 里**不要写字符串字面量**（PowerShell 会吃引号）。

### 1.2 刷新验收表里两处**旧口径文本**（`策划/验收表.md`）

「收尾自检」小节里：
- 第 5 行写的是 `47 screenshot refs`（现为 **52**）；
- 第 6 行还写着旧口径的 `all 48 shots are newer than the last code edit (09-19 12:08)` ——
  改成**逐行口径**的如实描述（格式：`N acceptance rows / M (row,shot) pairs compared per row: every cited shot is at least as new as its own row implementation file`，
  并写明"N 行无截图 / K 行解析不到实现文件 ⇒ 按 HUMAN-ONLY 计"的真实数字，**数字以你跑闸门时读到的为准**）。

### 1.3 顺手把项目级 skill 补一行（主 agent 授权的唯一 skill 改动）

`tools/ai-skill/constraints.md` 的静默失败清单里"截图证据可能比代码旧"那一行，补上**粒度**说明：
证据新鲜度是**逐行判**（每行的图 ≥ 该行自己的实现文件 mtime），不是"全工程最后一次代码改动"。

## 2. 判据（自己跑，原始输出贴进回报）

1. `powershell -NoProfile -ExecutionPolicy Bypass -File tools\verify.ps1` ⇒ **除 `HUMAN-ONLY` 外无 FAIL**（贴逐行，特别看第 6 条）；
2. 新采图的**绝对路径 + mtime + 尺寸**（并证明它晚于 `StageDust2.unity` 的 mtime）；
3. 新图你**自己读过**（读图通道要先自检：读不到 ⇒ `BLOCKED：图像通道不可用`，⛔ 不许编画面描述），写一句"图上看到了什么"；
4. `策划/验收表.md` 里 `待重采` 计数 = 0，且第 5/6 行的数字与闸门实读一致。

## 3. 不许

- ⛔ 不许改任何业务代码（`client/Assets/Scripts/**`、`Editor/**`、`ProjectSettings/**`）；
- ⛔ 不许改 `策划/策划案/**`、`tools/ai-skill/SKILL.md`、任何全局 skill（`constraints.md` 只许动 §1.3 那一行；`策划/验收表.md` 只许动 §1.1/§1.2 说的那些行）；
- ⛔ 不许改文件 mtime 让闸门变绿（图的 mtime 必须是真采出来的）；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；
- 一次性脚本只放 `.ai-tmp/test/`，用完删；证据图放 `client/Assets/Screenshots/`；不许产出交接/进度类 md。

## 4. 回报格式

```
产出物：<文件绝对路径清单>
自检：<verify.ps1 逐行 + 新图路径/mtime/尺寸 + 读图结论 + 待重采计数>
未决：无 / <具体条目>
```
