// 判据资产（tools/probes/）：差异 #88「必须支持局域网联机」的**发现侧**进 Play 取证 —— 第一半。
//
// 为什么要拆成两个探针：`ILanBrowser.Scan` 是**异步**的（窗口到点后由收包线程收尾，结果经
// Dispatcher.Post 收敛到主线程）。一次 eval 调用里等不到它 —— 所以：
//   ① 本探针：起应答端 + 发起一轮扫描（窗口 5s），立刻返回；
//   ② `probe-lan-verify.cs`：隔一段时间（bash 里 sleep）再读 `Game.LanBrowser.Hosts` 断言。
// ⛔ 两个探针都是只读业务状态；唯一写入 = 起/停 UDP 应答端（收尾用 probe-lan-verify 的 --stop 版）。
//
// 用法（编辑器须在 Play；Game.Launch 已跑过 ⇒ Game.LanBrowser 已挂接）：
//   unity command eval_file --file tools/probes/probe-lan-host.cs
var sb = new System.Text.StringBuilder();

var browser = CloverEngine.Game.LanBrowser;
sb.Append("LanBrowser=").Append(browser == null ? "NULL" : browser.GetType().Name)
  .Append(" supported=").Append(browser != null && browser.IsSupported)
  .Append(" state=").Append(browser != null ? browser.State.ToString() : "-")
  .Append(" hosts=").Append(browser != null ? browser.Hosts.Count : -1);
if (browser == null)
    return sb.Append("\nERROR: Game.LanBrowser == NULL —— 引擎的局域网寻服没挂上（CloverLan 的 Launch 钩子没跑？）").ToString();
if (!browser.IsSupported)
    return sb.Append("\nERROR: 本平台不支持寻服：").Append(browser.UnsupportedReason).ToString();

// ---------- 起应答端 ----------
string err;
var started = Cs16.Module.Net.CsLanHost.Start(out err,
    gatewayPort: Cs16.Module.Net.CsLanHost.DefaultGatewayPort,
    udpPort: Cs16.Module.Net.CsLanHost.DefaultUdpPort,
    name: "probe-lan-host @ probe",
    players: 1,
    maxPlayers: 10);

sb.Append("\nCsLanHost.Start=").Append(started).Append(" err=").Append(err ?? "-");
if (!started) return sb.Append("\nERROR: 应答端起不来，后面的扫描没有意义").ToString();

sb.Append("\n").Append(Cs16.Module.Net.CsLanHost.Describe());

// ---------- 发起一轮扫描 ----------
// 窗口给 5000ms：bash 侧要跨进程往返（CLI 每次调用 10~60s 量级），给短了会在读到结果前就收尾清零。
var opts = new CloverEngine.LanScanOptions
{
    DurationMs = 5000,
    IncludeBroadcast = true,
    IncludeSubnetBroadcast = true,
    IncludeLoopback = true,
};

browser.Scan(opts);
sb.Append("\n已发起 Scan（窗口 5000ms）：state=").Append(browser.State.ToString())
  .Append(" 目标=广播+各网卡子网广播+回环+应答端所在端口 ").Append(Cs16.Module.Net.CsLanHost.DiscoveryPort);

sb.Append("\n下一步：等 ≥6s 后跑 tools/probes/probe-lan-verify.cs 读结果");
return sb.ToString();
