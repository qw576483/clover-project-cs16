// 判据资产（tools/probes/）：差异 #88「必须支持局域网联机」的**能开局侧**进 Play 取证 —— 第一半。
//
// 与 probe-lan-host.cs 的关系：那一支证"能被发现"（UDP 47777 问答）。本支证**接下来那一步**：
// 主机把广播出去的那个 gateway 端口（TCP 8002）**真的开起来** —— 在此之前它只是个参数，
// 没有任何进程在监听 ⇒ 对方在列表里看得到、点加入却接不上。
//
// 为什么要配一个**独立进程**的客户端：只用同一个 Unity 进程自问自答证明不了"能开局"
// （那是引擎里那个回环应答器的做法）。所以判据的客户端是 `.ai-tmp/test/lan-gateway-client.py` —— 
// 一个真正的**第二个进程**，走 loopback 连上来。
//
// 用法（编辑器须在 Play，且已开局）：
//   unity command eval_file --file tools/probes/probe-lan-gateway.cs
var sb = new System.Text.StringBuilder();

// 先把上一次可能残留的应答端/网关停掉（幂等）。
Cs16.Module.Net.CsLanHost.Stop();

// **本机默认网关口 8002 已被另一个进程占着**（净判据：`netstat -ano` 里
//    `TCP 127.0.0.1:8002 LISTENING` + `UDP 127.0.0.1:8003`，同一个 PID，且另有一个 Unity 编辑器连着它）
//    ⇒ 取证改用空闲端口 8012。
//    端口是**参数**（`CsLanHost.Start` 的 `gatewayPort`，也就是广播报文里 `gateway` 那个字段），
//    协议本身与端口无关 ⇒ 换端口不影响本判据的效力。
//    顺带记一条工程事实：`CsLanGateway.Start` 现在会**先探测端口上是否已有别人在听**，
//    有人听就拒绝启动（否则 SO_REUSEADDR 会让我们 bind 成功却一个连接都收不到 —— 实测踩过）。
const int Port = 8012;

string err;
var ok = Cs16.Module.Net.CsLanHost.Start(out err,
    gatewayPort: Port,
    udpPort: Cs16.Module.Net.CsLanHost.DefaultUdpPort,
    name: "probe-lan-gateway @ probe",
    players: 1,
    maxPlayers: 10);

sb.Append("[A] CsLanHost.Start=").Append(ok).Append(" err=").Append(err ?? "-");
if (!ok) return sb.Append("\nERROR: 应答端起不来，网关也不会起来").ToString();

sb.Append("\n[A] ").Append(Cs16.Module.Net.CsLanHost.Describe());
sb.Append("\n[B] ").Append(Cs16.Module.Net.CsLanGateway.Describe());

// 归档口径：把"加入"要用的地址逐字打出来，给下一步的 Python 客户端用。
var ep = Cs16.Module.Net.CsLanHost.AdvertisedHost + ":" + Cs16.Module.Net.CsLanHost.GatewayPort;
sb.Append("\n[C] 加入地址（客户端应连这个）=").Append(ep);
sb.Append("\n[C] 线格式=").Append(Cs16.Module.Net.CsLanGateway.JoinMagic)
  .Append("|<json>  ⇒  ").Append(Cs16.Module.Net.CsLanGateway.WelcomeMagic)
  .Append("|<json>  ⇒  每 ").Append(Cs16.Module.Net.CsLanGateway.SnapInterval)
  .Append("s 一条 ").Append(Cs16.Module.Net.CsLanGateway.SnapMagic).Append("|<json>");

var gwUp = Cs16.Module.Net.CsLanGateway.IsRunning;
sb.Append("\nRESULT-LANGW: ").Append(gwUp ? "PASS" : "FAIL");
sb.Append("\n  口径：应答端在跑=True（UDP 47777） 对局网关在跑=").Append(gwUp)
  .Append("（TCP ").Append(Cs16.Module.Net.CsLanGateway.Port).Append("）");
sb.Append("\n下一步：跑 .ai-tmp/test/lan-gateway-client.py（**另一个进程**）连上来，再跑 verify 探针读计数");
return sb.ToString();
