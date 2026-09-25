# agent-41 · 把差异 #68 的数值接进代码（消音 / 连发）

> 数值出处**已经拿到**（另一片交付，逐字节可核）：`策划/武器右键数值出处.md`（22 KB，含 §0 速查 / §3 伤害 / §4 散布射程 / §5 连发 / §6 改动点 / §7 可信度与 BLOCKED / §8 字段建议 / §9 复核命令）。
> 载体 = `原版资源/cs16src/cstrike/dlls/mp.dll`（基址 `0x10000000`）。
> **本片只做"接进代码 + 断言"**，⛔ 不重做取证、⛔ 不改出处文件。

## 一、要接的数值（⛔ 逐条照出处文件，别自己改数）

| 项 | 值 |
|---|---|
| USP 伤害 | 未装 **34** / 装消音 **30** |
| M4A1 伤害 | 未装 **32** / 装消音 **33** |
| Famas 伤害 | 普通 **30** / 连发 **34** |
| 射程修正 | USP 0.79/0.79 · M4A1 0.97/**0.95** · Famas 0.96 · Glock18 0.75（全距离 8192） |
| M4A1 散布（静止站） | 未装 0.02×acc / 装消音 **0.025×acc**（其余档两态相同） |
| Glock18 连发 | **3 发**，首发/续发间隔均 **0.1 s**，连发循环 **0.5 s**（半自动 0.15） |
| Famas 连发 | **3 发**，首发 **0.05 s**、续发 **0.1 s**，连发循环 **0.55 s**（普通 0.0825） |
| 校验锚 | AK47 伤害 **36**（用来证明那对字段就是伤害） |
| 状态位 | `weapon+0x128`：`0x04`=已装消音（USP/M4A1）、`0x02`=连发（Glock18）、`0x10`=连发（Famas） |

**⛔ 无出处的武器/字段一律不接**（保持现有值或留空），⛔ 不许"顺手补齐看起来合理的数"。

## 二、落地位置

1. `client/Assets/Scripts/Core/CsWeapons.cs` 的 `CsWeaponDef`：新增
   `DamageSilenced` / `SpreadSilenced` / `RangeModifier` / `RangeModifierSilenced` /
   `BurstShots` / `BurstIntervalFirst` / `BurstInterval` / `BurstCycleTime`（命名自定，但要与出处文件的字段建议对得上，写进回报）。
2. **消费点**（自己找齐，逐处写 `文件:行`）：
   - 伤害：按 `CsActor.Silenced` / `BurstMode` 取对应值（`Module/Combat/**` / `Module/Match/**`）。
   - 散布 / 射程修正：命中判定与衰减链。
   - 连发：`BurstMode` 下**一次扣扳机打 3 发**、按上表间隔；连发循环时间与普通射速各自取对。
3. **断言**（这是本片的判据）：扩展现有 `client/Assets/Scripts/Module/Combat/CombatSelfTest.cs` 里
   「差异 #68」那一段 —— 至少断言：
   - 逐武器「装 / 不装消音」取到的伤害与射程修正 **== 上表**；
   - Glock18 / Famas 连发**恰好 3 发**，首发/续发间隔与上表一致；
   - 状态位为假时（未切模式）取的仍是原值；
   - ⛔ 用一个**负控**（故意把某个值改错）证明断言真的会 FAIL。

## 三、判据（一条命令 + 原始输出）

- 离线：`CombatSelfTest` 的 `RESULT: PASS` + 逐条断言输出（含负控 FAIL）+ 一条复核命令（`策划/武器右键数值出处.md` §9 那种）。
- 可选实机：一次 Play（**错峰**：进 Play 前看 `.ai-tmp/test/play-driver-log.tsv` 最后时间戳，别抢），
  打一梭子连发 / 切一次消音，贴运行时日志行。

## 四、边界

- 写入范围：`client/Assets/Scripts/Core/CsWeapons.cs`、`client/Assets/Scripts/Module/Combat/**`、
  `client/Assets/Scripts/Module/Match/**`、`.ai-tmp/**`、`策划/差异登记.tsv` 第 69 行（编号 #68，只改那一行）。
- ⛔ 不改引擎源码、⛔ 不改 skill、⛔ 不写 `Module/Net/**`、`Module/View/**`、`Module/Bot/**`、`Module/Map/**`、
  `UI/**`、`策划/武器右键数值出处.md`。
- 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。
- 命令一律显式超时；超过 12 分钟把进度落盘 + 给 `main` 一条回报。
