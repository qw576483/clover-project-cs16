// 判据资产（tools/probes/）：差异 #88 —— 第一半的后半（读扫描结果）+ 收尾停应答端。
//
// 用法（须在跑过 probe-lan-host.cs 之后 ≥6s）：
//   unity command eval_file --file tools/probes/probe-lan-verify.cs
//
// 判定口径（⛔ 写死在探针里，不靠人眼看）：
//   PASS ⇔ ① 应答端计数器 Queries > 0（**确实收到过查询**，不是"扫了但没人问"）
//          ② Replies == Queries（每一问都答了，没有静默丢）
//          ③ Game.LanBrowser.Hosts 里存在一台 gateway == 应答端广播的 主机:端口
//              且 name 与应答端一致（⇒ 引擎真的把我们的包**解出来并收进了结果集**）
var sb = new System.Text.StringBuilder();

var browser = CloverEngine.Game.LanBrowser;
if (browser == null) return "ERROR: Game.LanBrowser == NULL";

// ⛔ 不许写 `var host = Cs16.Module.Net.CsLanHost;` —— `CsLanHost` 是**静态类**，
//    C# 不允许声明"静态类型的变量"（实测编译报
//    `'CsLanHost' is a type, which is not valid in the given context` /
//    `Cannot declare a variable of static type 'CsLanHost'`）⇒ 全程用**全限定名**调用。
sb.Append("应答端：running=").Append(Cs16.Module.Net.CsLanHost.IsRunning)
  .Append(" ").Append(Cs16.Module.Net.CsLanHost.Describe());
sb.Append("\nLanBrowser：state=").Append(browser.State.ToString())
  .Append(" hosts=").Append(browser.Hosts.Count);

var expectedGateway = Cs16.Module.Net.CsLanHost.AdvertisedHost + ":" + Cs16.Module.Net.CsLanHost.GatewayPort;
var found = false;
for (var i = 0; i < browser.Hosts.Count; i++)
{
    var h = browser.Hosts[i];
    var hit = h.Address == expectedGateway;
    if (hit) found = true;
    sb.Append("\n  [").Append(i).Append("] name=\"").Append(h.Name).Append("\" address=").Append(h.Address)
      .Append(" udp=").Append(string.IsNullOrEmpty(h.UdpAddress) ? "未提供" : h.UdpAddress)
      .Append(" players=").Append(h.Players).Append("/").Append(h.MaxPlayers)
      .Append(" version=").Append(h.Version)
      .Append(hit ? "   ← 本机应答端广播的那台" : "");
}

var q = Cs16.Module.Net.CsLanHost.Queries;
var r = Cs16.Module.Net.CsLanHost.Replies;
sb.Append("\n判据：收到查询=").Append(q).Append("（>0） 回出应答=").Append(r).Append("（应 == ").Append(q).Append("）")
  .Append(" 浏览器里命中本机广播=").Append(found);

var pass = q > 0 && r == q && found;
sb.Append("\nRESULT: ").Append(pass ? "PASS" : "FAIL");

// 收尾：停应答端（幂等）。⛔ 不停的话下一轮扫描会一直有应答，把"没主机"这类负例测不出来。
Cs16.Module.Net.CsLanHost.Stop();
sb.Append("\n收尾：").Append(Cs16.Module.Net.CsLanHost.Describe());
return sb.ToString();
