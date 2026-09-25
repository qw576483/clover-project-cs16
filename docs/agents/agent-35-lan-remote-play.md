# agent-35 · 差异 #88「能开局」剩下的两段（局域网联机）

> 用户 2026-09-24 明确要求「**必须支持局域网联机**」（非可选）。差异 #88 已在盘的部分：
> 「能被发现」（`CsLanHost` 广播应答）+「连得上 / 进得去房间 / 看得见世界数据」（`CsLanGateway` 主机 TCP 网关推快照、
> 独立进程客户端收 `WELCOME` / `SNAP` 并解析出角色）。
> **本片只做剩下的两段**，⛔ 不重做已完成的部分。

## 一、要做完的两件（都做完才算完）

1. **客户端的输入真的驱动主机模拟**。现状：主机收到 `CS16-LAN-INPUT/1` 只 `_inputs++` 就丢
   （`client/Assets/Scripts/Module/Net/CsLanGateway.cs` 的 `HandleLine` 里 `if (magic == InputMagic)` 分支），
   客户端也没在持续发。
2. **客户端把快照画成场上远端角色**。现状：客户端收得到快照（实测 117 条 / 解析 9 个角色）但画面上没有人。

## 二、契约（⛔ 两端必须同值；改一处必改两处）

- 线格式**不改**（`Module/Net/CsLanGateway.cs` 类注释里逐字定死的那四条 + `INPUT`）。
- `CS16-LAN-INPUT/1|<json>` 的字段名**必须与 `CsInputState` 的成员逐字对应**
  （先读 `client/Assets/Scripts/Module/Match/ICsMatch.cs` 拿真实字段名；JSON 里加一个 `"id":<actorId>` 指明这是谁的输入）。
  字段名一旦定下，**回报里逐字列出**（我要写进差异 #88）。
- 新增的公开入口（由主 agent 定名，实现自便）：
  - `Module/Net/CsLanGateway.cs`：`public static void ApplyRemoteInputs(ICsMatch match)` —— **只在主线程调**（`Pump` 里出快照之前调一次）。
  - `Module/Match/**`：`ICsMatch` 增加 `void SetRemoteInput(int actorId, CsInputState input)`，实现落在 `CsMatch`。
  - 客户端：`Module/Net/CsLanClient.cs` 按固定频率（建议 20 Hz）把本地玩家输入发出去（入队 → 后台线程写，⛔ 主线程不碰 socket）。

## 三、判据（只要一条命令 + 原始输出；⛔ 不填表、⛔ 不建矩阵）

1. **输入驱动**（数值类）：同一次 Play，客户端持续发「往前走」≥1 s ⇒ 主机侧该 actor 世界坐标净位移 **> 0.5 m**；
   日志要能读到主机侧的位移原文。⛔ 不许用"探针自己改主机内存"冒充 —— 必须走「客户端发 → 主机收 → 应用到模拟」这条链。
2. **远端角色可见**（表现类）：客户端侧场上存在远端角色对象（运行时节点名可查，如 `LanRemote*`），
   其 `position` 与同帧快照里的 `x/y/z` 一致（±0.01 m）；`alive=false` 的角色不可见。
   留一张截图（落 `.ai-tmp/screenshots/`）。
3. 驱动复用项目既有 Play 链（`.ai-tmp/drivers/bz-lan2.sh` 那条；端口用**空闲端口 8012**，
   `127.0.0.1:8002` 被别的进程占着 —— 这是已知环境事实，⛔ 别再用 8002 取证）。

## 四、边界

- 写入范围：`client/Assets/Scripts/Module/Net/**`、`client/Assets/Scripts/Module/View/**`（新增文件）、
  `client/Assets/Scripts/Module/Match/**`（只加 `SetRemoteInput` 一条链）、`.ai-tmp/**`、`docs/agents/agent-35-*.md`。
- ⛔ 不改引擎源码（`client/Packages/com.clover.unity-engine/**`）、⛔ 不改 skill。
- **另一条线正在清理 `client/Assets/Scripts/**` 里若干文件的注释**（`Core/CsWeapons.cs`、`Core/ResPaths.cs`、
  `UI/InGame/CsHudTheme.cs`、`UI/Flow/CsUiStyle.cs`、`Module/Map/CsMap.cs`、`Module/Combat/*`、`Module/Bot/*`、`Module/Audio/*`）
  ⇒ 本片**不要写这些文件**。若你发现 `CsMatch.cs` 在 10 分钟内被别人改过 ⇒ 先停下回报。
- 注释按 skill §3 第 3b 条：只写「现在是什么」，⛔ 不写批次 / 本轮 / 用户报的 / 日期。
