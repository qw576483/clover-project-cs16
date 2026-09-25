# agent-30：修「一进游戏从天上掉 + 出生点错 + 穿透障碍掉出地图」

> 项目：`clover-project-cs16`。**纯项目侧**（不动引擎）。
> 用户实测报告：「一进游戏就从天上往下掉；出生在 A2；往下掉的时候穿透所有障碍掉出地图外」。

## 0. 根因（已由只读调研钉死，⛔ 别再重查，直接按 §1 修）

**主因 A —— 出生点探测口径错（`CsMatch.cs:1654-1671`）**
`SnapSpawnToGround` 写死 `probe = (spawn.x, spawn.y + SpawnGroundProbeUp(100f), spawn.z)`，再 `_map.SampleGround(probe, SpawnGroundProbeDrop(200f))`。
`SampleGround`（`CsMap.cs:360-370`）是**单根向下射线取第一个命中面** ⇒ 从 `标记y+100` 出发，命中的是**该柱子里最高的那块楼板/墙顶**，不是出生点脚下。

实测证据：BSP 真值 CT 出生点脚底 = **-3.15 m**（`info_player_start` z=-88 ⇒ `(−88−36)×0.0254`），而运行时日志 `[Match] 本地玩家复活于 (18.50, 2.44, 34.50)` / `(18.50, 3.47, 34.50)` —— **Y 高了 5.6~6.6 m**；XZ `(18.5,34.5)` 正是 `Spawn_CT` 第 [1] 点（`de_dust2_markers.bytes:23`），**标记点本身没摆到 A 点**；相隔 2 m 的 `Spawn_CT`[2] `(16.5,34.5)` 命中的却是正确地面 `-3.25`（日志里 `已拉回出生点 (16.50, -3.25, 34.50)`）。⇒ 用户看到的"A2"是**落点被抬到 A2 上方横板**的误认。
`FindSpawnPoint` 用 `a.Id % 点数`（`CsMatch.cs:1375`，确定性）⇒ 本地玩家**每回合固定落回同一块横板**。

**主因 B —— 竖直方向没有碰撞（"穿透一切"）**
`CsMap.ResolveMove`（`CsMap.cs:284-308`）只解 X/Z，结尾 `return new Vector3(cur.x, to.y, cur.z);`（`:307`）—— **Y 原样透传**。竖直方向唯一刹车是 `CsMatch.StepActorPhysics` 里那一根 `SampleGround(resolved, GroundProbeDrop=15)`（`CsMatch.cs:1618`）。一旦柱子 15 m 内无世界面 ⇒ 返回 `-Inf` ⇒ `OnGround=false` ⇒ **每帧继续加速**。

**兜底为何无效（`CsMatch.cs:1643-1651`）**
`FallRecoverY = -15f`（`:44`）而合法地面最低约 `-3.25` ⇒ 要再掉 ~12 m 才触发；且拉回用 `SnapSpawnToGround(a, FindSpawnPoint(a))` = **同一个坏点** ⇒ "掉→拉回横板→再掉"循环（重复日志被 `LogRateEvery=1000` 掩盖）。

## 1. 要做的（三处，都在项目侧）

### 1.1 出生点：**相信标记 Y**，只做小幅校正（改 `CsMatch.cs`）

- `SnapSpawnToGround`（`:1654-1671`）不再从 `+100m` 取"第一个面"。改为**两段式**：
  1. 从 `spawn.y + 2f` 向下探 **4 m**（`SampleGround(probe, 4f)`）；
  2. 命中且 `|groundY − spawn.y| <= 2f` ⇒ 用 `groundY`；
  3. 否则（探不到 / 差太远）⇒ **就用 `spawn.y`**（标记 Y 本身就是 BSP 脚底，见 §0 实测），并 `RateWarn("spawn.noground", ...)` 说明"沿用标记高度"。
- 常量调整（`CsMatch.cs:35/39`）：`SpawnGroundProbeUp` `100f → 2f`、`SpawnGroundProbeDrop` `200f → 4f`。
  注释里写清**为什么**：标记 Y = BSP 实体脚底（`dust2_build.py:657-665` 的 `origin.z − 36` 再 `to_u`），是可信真值；从 +100 探会抓到头顶横板（本次 bug 的成因）。
- ⛔ 不许改 `FindSpawnPoint` 的取点策略（`a.Id % 点数` 是有意的确定性），但**可以在日志里打出落点**便于取证。

### 1.2 竖直方向：**下落必须能刹住**（改 `CsMatch.StepActorPhysics` + 必要时 `CsMap`）

目标：即使角色被放到悬空处，也**不允许无限加速下落**、**不允许穿过地板**。

- 在 `StepActorPhysics`（`CsMatch.cs:1589-1652`）里给下落加**下限保障**：
  - 记住**最近一次成功探测到的地面 Y**（`_lastGroundY`，按 actor 或按本地玩家；地面丢失时不立即清零，先保留若干帧）；
  - 下落时若本帧 `SampleGround` 返回 `-Inf`，就用 `_lastGroundY` 作为**软地板**（落到它就 `OnGround=true`、清 Y 速度），并 `RateWarn` 报一次；
  - 给下落速度**封顶**（终端速度；GoldSrc 的 `sv_maxvelocity` 默认 2000 u/s ≈ **50.8 m/s**，出处 `原版资源/解包产物/原版数值表.md` 里 `sv_maxvelocity` 那条；⛔ 数值要从那里抄，不许自编）。
- ⛔ **不许动** `ResolveMove` 的水平逻辑（位图滑墙 / 台阶 / `TryStepUp`）——它是对的，只许在**必要时**给它加"竖直兜底"（若你判断必须改 `CsMap`，只许新增方法/参数，不许改既有语义，并在回报里说明）。
- ⛔ 不许改 `GroundCheckDistance` / `PhysicsLayers` / 层掩码口径（`CsMap.GroundMask()` 现状正确）。

### 1.3 兜底：阈值抬到地面之上 + **换一个落点**

- `FallRecoverY`（`CsMatch.cs:44`）`-15f` ⇒ 改成**合法地面之下一点点**（用地图已知最低地面，例如 CT 买枪区 `-3.251`，或直接 `-6f`；给出你采用的依据）。
- 拉回逻辑（`:1643-1651`）：**不许复用同一个点**——按 `a.Id + n` 轮换候选出生点，且**先验证**该点 `SnapSpawnToGround` 结果不是"探不到"（§1.1 改完后正常点都会返回可信 Y）；拉回后打一条 Info 记录"从哪个 y 拉回到哪个点"。

## 2. 判据（自己跑，原始输出贴回报）

1. **离线断言**（放 `.ai-tmp/hosts/` 或 `.ai-tmp/test/`，用完删）：把 `SnapSpawnToGround` 的两段式逻辑用几个代表性输入跑一遍（**标记 y 有横板在上方** / 无横板 / 探不到），断言：
   - 有横板时**不再**被抬高（返回值 ≈ 标记 y）；
   - 探不到时**沿用标记 y**而不是 `-Inf`/原样传回坏值；
2. **一条 Play**（记账 `play-log.tsv`，≤1 次）：`probe_launch.cs` → `choose-ct` → 等 5 秒 → 打印本地玩家 `Position` 与 `OnGround`，**同日志里抓 3 秒内的 `掉出地图` Warn 数**。判据：
   - 本地玩家 y ≈ **-3.15 ± 0.3**（CT，`Spawn_CT` 真脚底）且 `OnGround=True`；
   - **0 条** `fall.recover` / `掉出地图`；
   - ⛔ 不许编造：把探针原始输出（含 y 与 Warn 计数）贴进回报。
3. 编译：`tools/probes/compile-check.ps1`（或等价 Roslyn，参考集用闸门最新 `csc-out\CloverEngine.*.dll`）⇒ `exit=0`。
4. `tools/verify.ps1` ⇒ 不许引入新 FAIL（若你的改动让某行证据过期，只重采**受影响那几行**并在回报里写明）。

## 3. 文档 / 注释

- `计划/对照表.md` 若已有"出生点/重力/掉落"相关行，按实际值更新（⛔ 不许改别的行）。
- 改动处的注释写清**为什么**（标记 Y = BSP 脚底；+100 探针的坑），并引 `E-` 无关（本片是项目侧 bug 修复，不占引擎 E 号）。
- ⛔ 不许新增 README / 交接类 md。

## 4. 不许

- ⛔ 不许改 `client/Assets/Scripts` 里与出生/物理**无关**的文件；⛔ 不许改引擎 `clover-client-unity-engine/**`；
- ⛔ 不许改 `策划/**`（除 §3 说的对照表那一行）、`docs/**`、`tools/**`、`.ai-tmp/test` 历史文件；
- ⛔ 不许读别的 `clover-project-*` 工程；⛔ 不许开子 agent；临时脚本只放 `.ai-tmp/`（test/hosts），用完删；
- ⛔ 不许调数值"凑好看"（终端速度等一律从 `原版数值表.md` 抄并写出处）。

## 5. 回报格式

```
根因复核：<你复读到的两处代码行号，确认与 §0 一致>
1.1 改法：<改后原文 + 常量新值>
1.2 改法：<改后原文 + 你选的终端速度及其出处>
1.3 改法：<阈值依据 + 轮换逻辑原文>
离线断言：<命令 + 原始输出>
Play 取证：<探针原始输出：位置 y / OnGround / fall.recover 计数>
编译：<命令 + 末尾 + 退出码>
闸门：<verify.ps1 逐行>
未决：无 / <具体条目>
```
