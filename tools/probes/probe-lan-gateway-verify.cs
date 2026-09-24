// 判据资产（tools/probes/）：差异 #88「能开局」取证 —— 第二半（读网关计数）。
//
// 用法（须在跑过 probe-lan-gateway.cs、且 Python 客户端已连过之后）：
//   unity command eval_file --file tools/probes/probe-lan-gateway-verify.cs
//
// 判定口径（⛔ 写死在探针里，不靠人眼看）：
//   PASS ⇔ ① Gateway.Connections ≥ 1（**另一个进程确实连上来了**，不是只有本进程）
//          ② Gateway.Joins ≥ 1（收到过 JOIN 且回了 WELCOME）
//          ③ Gateway.Snapshots ≥ 1（主机真的按帧把世界推出去了）
//          ④ Gateway.Malformed == 0（线格式没谈崩）
// ⛔ 不含"客户端已把远端角色画出来"这一条 —— 那是"能开局"的下一段（见 #88 登记）。
var sb = new System.Text.StringBuilder();

sb.Append("[A] ").Append(Cs16.Module.Net.CsLanGateway.Describe());

var conn = Cs16.Module.Net.CsLanGateway.Connections;
var joins = Cs16.Module.Net.CsLanGateway.Joins;
var snaps = Cs16.Module.Net.CsLanGateway.Snapshots;
var bad = Cs16.Module.Net.CsLanGateway.Malformed;
var inputs = Cs16.Module.Net.CsLanGateway.Inputs;

sb.Append("\n[B] 连接=").Append(conn)
  .Append(" 收到 JOIN=").Append(joins)
  .Append(" 推出快照=").Append(snaps)
  .Append(" 收到 INPUT=").Append(inputs)
  .Append(" 非法行=").Append(bad)
  .Append(" 已握手=").Append(Cs16.Module.Net.CsLanGateway.WelcomedCount);

var pass = conn >= 1 && joins >= 1 && snaps >= 1 && bad == 0;
sb.Append("\nRESULT-LANGW: ").Append(pass ? "PASS" : "FAIL");
sb.Append("\n  口径：连接≥1（=").Append(conn).Append("）收到JOIN≥1（=").Append(joins)
  .Append("）推出快照≥1（=").Append(snaps).Append("）非法行=0（=").Append(bad).Append("）");
sb.Append("\n⛔ 不含「客户端已把远端角色画出来」这一条 —— 那是下一段");

// 收尾：停主机（幂等）。⛔ 不停的话下一轮扫描会一直有应答，负例测不出来。
Cs16.Module.Net.CsLanHost.Stop();
sb.Append("\n收尾：").Append(Cs16.Module.Net.CsLanGateway.Describe());
return sb.ToString();
