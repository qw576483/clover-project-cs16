# agent-57 · 火焰落点/尺寸随武器 + 走蹲悬空（代码侧）

## 病灶（巡检片实测，只记录了没改）
1. **火焰落点恒定**：全部武器恒为相机局部 `(右 0.13, 下 0.12, 前 0.72 m)`（`CsCombatTuning` 的 `MuzzleOffsetRight/Up/Forward = 0.13 / −0.12 / 0.72`）⇒ 与**枪管尖实际位置差 17~38 cm**（巡检片实测枪口轴向：`glock18 0.435 / p228 0.343 / deagle 0.390 / usp 0.552 / mp5 0.509 / m249 0.545 / m3 0.548 / xm1014 0.581`）⇒ **火焰悬在枪口前方空中、与枪身脱开**。
2. **尺寸不随武器**：恒 `MuzzleFlashSize=0.30 m`（m249 十字 0.4167）⇒ 原版不同枪不同尺寸。
3. **贴图映射只接了 m249**：`CombatEffects.PickMuzzleFlash` 其余 24 把无映射（巡检片已另派片修透明键，本片**只管几何与时长**）。
4. **走/蹲悬空**：走路中有一段**双脚同时离地 2~3.5 cm**（`walk t=0.75`）、蹲静止悬空 0.8~1.1 cm（`crouch_idle t=0.5`）；根因指向 `Module/View/ActorView.cs` 的 `Measure()` —— `_modelLift` **只在 Bind 的 idle 姿态上算一次**（+ `Module/View/CsViewTuning.cs`）。

## 目标
1. **火焰落点按武器**：用**已实测的枪口轴向距离**（上面那 8 个数，都来自本机的枪/模型，有出处）驱动偏移；能逐武器取到就逐武器，取不到的武器**给一个可解释的规则**（例如按武器类别），并写清哪些是实测、哪些是推断。⛔ 不许拍"看起来差不多"的统一值。
2. **尺寸与时长**：原版不同枪不同尺寸/时长 ⇒ 有出处就按出处；没有就**按武器类别给可解释的差异**，并如实标注出处等级。
3. **悬空修掉**：`_modelLift` 必须**随姿态**（idle/walk/crouch）取，而不是只按 Bind 时的 idle；改完给逐姿态的 `minMesh − rootY` 数字（idle / walk / crouch 各自 |值| ≤ 1cm 量级）。
4. ⛔ **不要靠"整体抬高/压低模型"来凑贴地** —— 那会让某个姿态穿地。

## 硬约束
1. ⛔ 不改引擎、⛔ 不改 skill。
2. 写入范围：`client/Assets/Scripts/Module/Combat/**`（`CsCombatTuning.cs`、`CombatEffects.cs` 的几何/时长部分）、`client/Assets/Scripts/Module/View/**`（`ActorView.cs` 的 `Measure`、`CsViewTuning.cs`）、`.ai-tmp/**`、`策划/差异登记.tsv` **#89（行号 90）**。
   ⛔ **不写贴图/资产**（另一片 `transparency-key` 同时在做火焰贴图透明与角色贴图）、`Core/**`、`UI/**`、`client/Assets/Editor/**`。
3. **Play 是独占资源**：`transparency-key` 可能同时跑 ⇒ 进前看 `.ai-tmp/test/play-log.tsv` 末行是否 `RELEASE` 且 `play-driver-log.tsv` 近 90 秒无新行；`OCCUPY`/`RELEASE` 配对、格式 `2026-09-25 HH:mm`；⛔ 别写共用 `state.txt`；进局链路**必须补 `Clk 'urban'`**。
4. **判据**：① 逐武器火焰近景（重点：`glock18`/`usp`/`m249` 三把差异明显的）+ 探针行里的 `flashAxial` 不再恒定；② 逐姿态 `minMesh − rootY` 数字表（idle/walk/crouch）；③ 编译 `errors=0`。⛔ 不填验收表、⛔ 不建矩阵。
5. 注释按 skill §3 第 3b 条：只写「现在是什么」。命令显式超时；超 12 分钟落盘 + 回报（第一行写模型标识）。

## 回报（`send_message` 给 `main`）
① 改了哪些文件 + 每处一句话；② 逐武器落点/尺寸的**出处等级**（实测 / 类别推断 / 自定）；③ 修前/修后探针行（`flashAxial` 不再恒定）+ 逐姿态贴地数字；④ 编译证据；⑤ 没做到的逐条 BLOCKED。⛔ 不要长解释。
