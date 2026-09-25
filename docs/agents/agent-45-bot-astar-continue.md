# agent-45 · 机器人寻路接 A* 与 T 侧下包（差异 #67 续做）

## 背景：**前人做过一半但卡死了**，你要先核实再补完
- 前序成员（同名 `bot-astar`）自 03:01:47 起再无写入、未回报 ⇒ 已终止。**它留下这些未验证的改动**：
  - `client/Assets/Scripts/Module/Bot/BotNavigator.cs`（99,653 B，03:01:31）
  - `client/Assets/Scripts/Module/Bot/CsBotBrain.cs`（130,052 B，03:01:47）
  - `client/Assets/Scripts/Module/Bot/CsBotConst.cs`（36,579 B，03:01:37）
  - `client/Assets/Scripts/Module/Bot/CsBotPlans.cs`（33,175 B，02:32:52）
  - `.ai-tmp/test/astar-bak/de_dust2.bytes`（22,318 B 的备份）
- **第一步 = 读它们、判断前人做到了哪一步**（是否已接 `AStar.FindSmoothed`、是否已加 T 下包逻辑、能否编译）。
  ⛔ 不要推倒重来；前人做对的部分保留，做错/没做完的补上。

## 目标（差异 #67 = 用户投诉过三次的机器人 AI）
1. **寻路层**：`BotNavigator` 从"路线标记点最近邻的直线推进"换成**引擎格子 A***
   （`client/Packages/com.clover.unity-engine/Runtime/Core/AStar.cs` 的 `FindSmoothed`，**引擎有，⛔ 不改引擎**），
   走工程内已烘好的可走位图（`client/Assets/Resources/MapData/**`，自己列一遍拿真名）。
2. **T 侧下包必须真的发生** —— 判据 A5 历史实测**恒为 0**，这是这条投诉最直接的病灶。
3. 收尾数字：**A5 ≥ 1、A1/A2 达标 ≥ 6/8、A6/A7 不回退**（⛔ 不许下调阈值）。

## 硬约束
1. ⛔ 不改引擎源码（有缺口写 `引擎问题.md`，`## 引擎缺口` / `## 引擎 bug` / `## skill 问题` 三段，并用绕法做完）。
2. 写入范围：`client/Assets/Scripts/Module/Bot/**`、`.ai-tmp/**`、`策划/差异登记.tsv` **第 68 行**（只改这一行）。
   ⛔ 不写 `Module/Map/**`、`Core/**`、`UI/**`、`Module/Combat/**`、`Module/Net/**`、`Module/View/**`、`client/Assets/Editor/**`（别的片在用）。
3. **Play 是独占资源**：离线部分（读位图、接 A*、写离线断言）做在前面；进 Play 前先看 `.ai-tmp/test/play-log.tsv` 最近 2 分钟有无 `OCCUPY` 未释放，有 ⇒ 等或错峰，⛔ 别同时开第二个编辑器会话。
4. 注释按 skill §3 第 3b 条：只写「现在是什么」——⛔ 不写批次 / 本轮 / 任务书 / 用户报的 / 日期 / 别的工程名。
5. 判据只要**一条命令 + 几行原始输出**（完整回合的五项前后数字）：⛔ 不填验收表、⛔ 不建覆盖矩阵、⛔ 不建台账。
6. 命令一律显式超时；超过 12 分钟把进度落盘 + 给 `main` 一条回报（**第一行写你的模型标识**）。

## 回报（`send_message` 给 `main`）
① 前人做到哪一步（逐文件一句话，含"可用/需修"）；② 你补了什么；③ A1/A2/A5/A6/A7 前后数字；
④ 寻路层换掉的证据（日志行）；⑤ 没做到的逐条 BLOCKED 原因。⛔ 不要长解释。
