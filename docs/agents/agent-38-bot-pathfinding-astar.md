# agent-38 · 差异 #67 主体剩余：寻路层接引擎 A* + T 侧下包闭环

> 用户为这条投诉过**三次**（原话摘录在 `策划/差异登记.tsv` 第 68 行）：
> 「人机的ai太傻逼了。一直在原地踱步…警不去守点，匪不去下包 不去突破」
> 「机器人 ai 没有分工吗？感觉行为方式都是一样的。机器人有点太笨了」
> 「为什么每个机器人的操作，路线都是相同的。你这什么行为树，什么 ai 啊？？为什么没有分工？？？」
> **已落地**（⛔ 别重做）：4 槽位计划表（同队 4 只恒占 4 条互异路线 + 互异目标点）、角色表
> （`CsBotRoles`：Breaker/Support/Scout/Anchor，按 `(阵营, Id%4)` 纯函数）、守点倍率与「Anchor 守到底」、
> 换向迟滞、起点节点修掉、回合内向序号缓存。
> **本片做主体剩余**：寻路层还是"标记点最近邻"，且**T 侧下包实测恒为 0**。

## 一、要做完的三件

1. **寻路层换成引擎格子 A***。现状：`BotNavigator` 沿 `Resources/MapData/de_dust2_markers.bytes` 的
   路线标记点做最近邻直线推进（`BotNavigator` 的 `EnsurePath` / `AdvancePath` / `MaybeRepath` / `Avoid`）。
   引擎**已有**：`client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs`（`FindSmoothed`，契约见其 `16-19` 行）。
   ⇒ 用工程内已烘好的可走位图（`Resources/MapData/**`，自己查真实文件名）走引擎 `AStar.FindSmoothed`。
   ⛔ **不改引擎源码**（引擎有缺口就写 `<项目根>/引擎问题.md` 并用绕法做完）。
2. **T 侧下包闭环**：判据 A5（"回合内 T 至少下包 1 次"）历史实测 **恒为 0**。
   已知病灶（历史定案）：T 侧持包 bot 卡在 `Plant` 态不动、位移恒 0 ⇒ 本片要让它真的走到包点并按 use 完成下包。
3. **A1/A2 不回退且推进**：历史最好 = A1/A2 达标 **3/8**（"净位移 / 净位移占比"口径）、A6（CT 包点驻留）/A7（守点换位）已 PASS。
   ⇒ 本片收尾要求：**A5 ≥ 1**、A1/A2 达标 **≥ 6/8**、**A6/A7 不回退**。⛔ 不许下调阈值。

## 二、载体（都在工程内；⛔ 别去找不在盘的东西）

- 几何：`client/Assets/ThirdParty/Dust2/de_dust2_geo.bin`
- 位图 / 标记点：`client/Assets/Resources/MapData/**`（自己列一遍，按真实文件名用）
- ⛔ `原版资源/cs109/maps/de_dust2.bsp`、`原版资源/cs16game/app/**` 都是**历史路径、不在盘**，别去翻
- 判据资产：`BotSelfTest`（菜单 `Clover/自检/机器人 战术分工表（差异 #67）`）+ `.ai-tmp/drivers/bz-round2.sh` 那条完整回合采集链

## 三、判据（原始输出，⛔ 不填表 / ⛔ 不建矩阵）

1. 一个**完整回合**（8 bot，窗口覆盖 冻结 4s + 回合 105s + 结束）的实机采集：
   贴出 A1/A2/A5/A6/A7 五项的前后数字（同一脚本、同一口径）。要求：**A5 ≥ 1**、A1/A2 ≥ 6/8、A6/A7 不回退。
2. 寻路层的存在性证据：日志要能读到"这条移动是 A* 给出的路径"（例如路径点数 / 重寻路次数 / 被 A* 判为不可达而换目标的行），
   ⛔ 不许只贴"bot 移动了"。
3. 若某项做不到 ⇒ **如实 BLOCKED**（缺什么 / 试过什么），⛔ 不许把阈值调低后宣称通过。

## 四、边界

- 写入范围：`client/Assets/Scripts/Module/Bot/**`、`client/Assets/Resources/MapData/**`（若位图需要重烘）、
  `.ai-tmp/**`、`策划/差异登记.tsv` 的第 68 行（编号 #67，只改那一行）。
- ⛔ 不改引擎源码、⛔ 不改 skill、⛔ 不写 `Module/Map/CsMap.cs`（那是 agent-39 的地盘）、
  ⛔ 不写 `client/Assets/Scripts/Core/**` / `UI/**`（别的片在用）。
- **Play 是独占资源**：进 Play 前先看一眼 `.ai-tmp/test/play-driver-log.tsv` 或 `.ai-tmp/drivers/` 最近 2 分钟有没有别人在跑；
  有 ⇒ 等或错峰（⛔ 不要同时开第二个编辑器会话）。
- 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。
- 命令一律显式超时；超过 12 分钟把进度落盘 + 给 `main` 一条回报。
