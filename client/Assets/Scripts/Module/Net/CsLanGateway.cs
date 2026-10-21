using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;

namespace Cs16.Module.Net
{
    /// <summary>
    /// 局域网**对局网关（主机侧）**—— 本项目新增，差异 #88「必须支持局域网联机」的**能开局**侧第一段。
    ///
    /// <para><b>它负责什么</b>：主机权威的**世界状态**——每 <see cref="SnapInterval"/> 秒把
    /// <see cref="ICsMatch"/> 读成一行 <c>CS16-LAN-SNAP/1</c> 推给每个客机，并把客机发来的
    /// <c>CS16-LAN-INPUT/1</c> 应用到主机模拟；加入 / 断开时的身体增删也在这里落地。</para>
    ///
    /// <para><b>传输层归引擎</b>：TCP 监听、逐连接拆行、握手、连接上限、断线感知全部由
    /// <see cref="ILanHost"/>（<c>CloverLan.CreateHost()</c>）承担 —— 本类只跟
    /// <see cref="ILanHost.Broadcast"/> / <see cref="ILanHost.SendTo"/> /
    /// <see cref="ILanHost.OnClientJoined"/> / <see cref="ILanHost.OnClientInput"/> /
    /// <see cref="ILanHost.OnClientLeft"/> 打交道，不再自建 socket 与线程。</para>
    ///
    /// <para><b>业务线格式（不臆造，逐字定死在这里；两端必须同值）</b>：一律 UTF-8，
    /// 形如 <c>&lt;MAGIC&gt;|&lt;json&gt;</c>，作为**一条业务载荷**交给引擎（引擎再套自己的
    /// <c>CLOVER-LAN-DOWN/1</c> 信封，用 base64 原样承载这些字节）：
    /// <list type="bullet">
    /// <item>客户端 → 主机：<c>CS16-LAN-JOIN/1|{{"name":"…","proto":1}}</c></item>
    /// <item>客户端 → 主机（每 0.05 s）：<c>CS16-LAN-INPUT/1|{{"id":&lt;actorId&gt;,"Move":{{"x":…,"y":…}},"Jump":…,"Crouch":…,"Walk":…,"Fire":…,"Zoom":…,"Attack2":…,"Yaw":…,"Pitch":…}}</c>
    /// —— 键名**逐字** = <c>CsInputState</c> 的成员名（<c>Move</c> 是 <c>{{x,y}}</c>），
    /// 多一个 <c>id</c> 指明这是谁的输入。</item>
    /// <item>主机 → 客户端：<c>CS16-LAN-WELCOME/1|{{"proto":1,"name":…,"host":…,"maxPlayers":…,"snapHz":…,"actorId":…,"playerName":…,"team":…}}</c>
    /// —— <c>actorId</c> 是主机为这个客户端建出的身体，客机收到它就不必再靠同名从快照里认领自己。</item>
    /// <item>主机 → 客户端（每 <see cref="SnapInterval"/> 秒）：<c>CS16-LAN-SNAP/1|{{…"actors":[…]}}</c></item>
    /// <item>任一方可发：<c>CS16-LAN-BYE/1|{{}}</c></item>
    /// </list>
    /// 引擎的下行信封有单行上限 ⇒ 一条业务载荷最多 <see cref="MaxPayloadBytes"/> 字节，超了不发并留痕。</para>
    ///
    /// <para><b>线程模型（关键：模拟只有一个写入者 = 主线程）</b>：
    /// <list type="number">
    /// <item><b>主线程</b>：每帧调 <see cref="Pump"/> —— 落地远端身体的增删、按
    /// <see cref="SnapInterval"/> 组一行快照并推出去、把待转交的远端输入交给模拟。</item>
    /// <item><b>引擎回调</b>（<c>OnClientJoined</c> / <c>OnClientInput</c> / <c>OnClientLeft</c>）：
    /// 引擎经 <c>Game.Dispatcher</c> 收敛到主线程；派发器不可用时会在引擎的后台线程上同步回调。
    /// 因此这些回调**只登记**（待建表 / 待删表 / 待转交输入），从不碰 <see cref="ICsMatch"/>。</item>
    /// </list></para>
    ///
    /// <para><b>本类到哪一步为止</b>：能在主机模拟里为加入者建出身体（WELCOME 回 <c>actorId</c>）/
    /// 能持续推出世界快照 / 能把客户端的输入应用到主机模拟（断开时移除该身体）。
    /// <c>Module/View/CsLanRemoteView</c> 把 <c>actors[]</c> 画成场上的远端角色。</para>
    /// </summary>
    public static class CsLanGateway
    {
        private const string Tag = "LanGateway";

        public const string JoinMagic = "CS16-LAN-JOIN/1";
        public const string WelcomeMagic = "CS16-LAN-WELCOME/1";
        public const string SnapMagic = "CS16-LAN-SNAP/1";
        public const string InputMagic = "CS16-LAN-INPUT/1";
        public const string ByeMagic = "CS16-LAN-BYE/1";

        /// <summary>协议版本（两端必须同值；客户端带了 <c>proto</c> 就回显它）。</summary>
        public const int ProtocolVersion = 1;

        /// <summary>业务单行上限（与客机同值）：超过它的业务行算非法。</summary>
        public const int MaxLineBytes = 4096;

        /// <summary>
        /// 一条业务载荷的上限：引擎下行行 <c>CLOVER-LAN-DOWN/1|{{"id":N,"b64":"…"}}</c> 的内容上限是
        /// <see cref="MaxLineBytes"/>，去掉信封（约 37 字节）后按 base64 的 4/3 膨胀折算 ⇒ 约 3042；
        /// 这里取 3040。超出的载荷发出去也会被对端按"超长行"丢弃，所以本类直接不发并留痕。
        /// </summary>
        public const int MaxPayloadBytes = 3040;

        /// <summary>最大同时连接数（局域网小局，够用即可）。</summary>
        public const int MaxClients = 16;

        /// <summary>快照间隔（秒）⇒ 10 Hz。</summary>
        public const float SnapInterval = 0.1f;

        /// <summary>首选端口被别的进程占着时，最多向后顺延几个端口再放弃。</summary>
        public const int PortFallbackTries = 8;

        /// <summary>
        /// 已连上但这么久没有任何业务信令 / 输入 ⇒ 视为静默断开（TCP 半开不会给出 read=0）。
        /// 客户端在收到 WELCOME 之前只发过一次 JOIN，所以这个值必须明显大于"加入 → 收到第一帧快照"的时间。
        /// </summary>
        private const int PeerIdleTimeoutMs = 30000;

        /// <summary>一条引擎连接在本类这边的镜像（身体 / 握手状态只在这里维护）。</summary>
        private sealed class Peer
        {
            /// <summary>引擎连接 id（<see cref="LanPeerInfo.Id"/>，不回收）—— 下行一律按它寻址。</summary>
            public int Id;
            public string Endpoint = "-";
            public string Name = "-";
            /// <summary>JOIN 里带来的原始名字（重名时建身体时会被加后缀；重建时仍按这个原名请求）。</summary>
            public string RequestedName = "-";
            /// <summary>主机侧身体的实际名字（可能带 <c>#2</c> 后缀）。</summary>
            public string AssignedName = "-";
            /// <summary>这个 peer 在主机模拟里的 actorId；0 = 还没有身体。</summary>
            public volatile int ActorId;
            /// <summary>JOIN 已入待建队列（主线程建身体）—— 挡住同一连接重复 JOIN。</summary>
            public bool JoinQueued;
            /// <summary>引擎握手完成的时刻（<see cref="Environment.TickCount"/>），用于业务 JOIN 静默超时。</summary>
            public int ConnectedAt;
            public volatile bool Welcomed;
            public volatile bool Closed;
            /// <summary>以下三个计数可能被引擎回调与主线程同时写 ⇒ 一律 Interlocked。</summary>
            public long Received;
            public long Sent;
            public long BadLines;
        }

        /// <summary>一条待主线程处理的 JOIN（身体只能在主线程建：模拟只有一个写入者）。</summary>
        private sealed class PendingJoin
        {
            public Peer Peer;
            public string Name;
        }

        /// <summary>
        /// 待主线程落地的远端身体创建 / 移除。
        /// <para>JOIN 与断线发生在引擎回调上，而 <see cref="ICsMatch"/> 只允许主线程写 ⇒
        /// 两边都只入队，由 <see cref="Pump"/> 在每帧开头统一处理。</para>
        /// </summary>
        private static readonly List<PendingJoin> PendingJoins = new List<PendingJoin>();
        private static readonly object JoinLock = new object();
        private static readonly List<int> PendingRemovals = new List<int>();
        private static readonly object RemoveLock = new object();

        /// <summary>主线程逐帧复查 peer → 身体的临时表（复用，不在每帧路径上分配）。</summary>
        private static readonly List<Peer> _peerScratch = new List<Peer>(MaxClients);

        /// <summary>最近一次 <see cref="Pump"/> 拿到的模拟（供 <see cref="Stop"/> 在主线程上收尾远端身体）。</summary>
        private static ICsMatch _match;

        /// <summary>引擎局域网主机（传输层：监听 / 拆行 / 握手 / 连接上限）。</summary>
        private static ILanHost _host;

        private static volatile bool _running;
        private static int _port;
        private static string _hostName = CsLanHost.DefaultName;
        private static int _maxPlayers = 10;

        private static readonly Dictionary<int, Peer> Peers = new Dictionary<int, Peer>();
        private static readonly object PeersLock = new object();

        private static float _accum;

        private static long _connections;
        private static long _joins;
        private static long _inputs;
        private static long _snapshots;
        private static long _malformed;
        private static long _rejected;
        private static long _inputBadFields;
        private static long _appliedInputs;
        private static long _rejectedOwnBodyInputs;
        private static long _rejectedForeignBodyInputs;
        private static long _rejectedStaleBodyInputs;
        private static long _inputsBeforeBody;
        private static long _remotePlayersAdded;
        private static long _remotePlayersRemoved;
        private static long _oversizePayloads;

        /// <summary>一条待转交的远端输入（引擎回调写、主线程取走）。</summary>
        private sealed class PendingInput
        {
            public int ActorId;
            public CsInputState Input;
            public string From = "-";
        }

        /// <summary>
        /// 待转交给模拟的远端输入（键 = actorId，**同一 actor 只留最后一条**）。
        /// <para>为什么按 actor 覆盖而不是排队：输入是"最新意图"，排队会让对端早先的意图在主机上迟到生效
        /// （两个驾驶员抢同一具身体的效果）。</para>
        /// </summary>
        private static readonly Dictionary<int, PendingInput> PendingInputs = new Dictionary<int, PendingInput>();
        private static readonly object InputLock = new object();

        // ---------------------------------------------------------------- 尽力而为路径的限频留痕
        //   自检脚本会把本区间整段剔除后再比对 ⇒
        //   本区间内只许放"闸门本身"，任何控制流改动都必须挪到区间外，否则自检会失去意义。

        /// <summary>
        /// 尽力而为路径（关 host / 发不出去）失败时**必须留下现场**，否则只剩"连不上 / 不动了"这个现象。
        /// 每处失败最多每 <see cref="BestEffortLogIntervalMs"/> ms 一条 —— 局域网里会被同网段无关流量打，
        /// 不许每包 / 每连接刷屏；判定走 <see cref="ShouldLogBestEffort"/>。
        /// </summary>
        private const int BestEffortLogIntervalMs = 5000;

        private static int _nextStopLogAt;            // Stop()：停引擎主机
        private static int _nextSendFailLogAt;        // SendTo：载荷发不出去
        private static int _nextOversizeLogAt;        // 快照超过业务载荷上限

        /// <summary>
        /// 尽力而为路径的**限频闸门**（**线程安全**）：返回 <c>true</c> = 这一条该报。
        /// 引擎回调可能在后台线程上同步触发，<see cref="Stop"/> 跑在主线程，靠
        /// <c>Interlocked.CompareExchange</c> 抢"上报资格"共用闸门。
        /// <para>刻意**不用**引擎 <c>LogThrottle</c>：它的语义约束写明"非线程安全：主线程使用"。</para>
        /// <para>时钟用 <see cref="Environment.TickCount"/>（int，约 24.8 天回绕）⇒ 判定一律走
        /// <c>unchecked(now - due) &lt; 0</c> 的**有符号差**（回绕当天既不会"永不到期"也不会"永远到期"）。</para>
        /// </summary>
        private static bool ShouldLogBestEffort(ref int dueAt)
        {
            var now = Environment.TickCount;
            var due = Volatile.Read(ref dueAt);
            if (unchecked(now - due) < 0) return false;              // 未到期：不报（⛔ 不刷屏）
            return Interlocked.CompareExchange(ref dueAt, unchecked(now + BestEffortLogIntervalMs), due) == due;
        }
        // [sink4-best-effort-end]

        // ---------------------------------------------------------------- 只读出口（日志 / 探针用）

        /// <summary>网关是否在监听（引擎主机在跑）。</summary>
        public static bool IsRunning
        {
            get
            {
                var host = _host;
                return _running && host != null && host.IsRunning;
            }
        }

        /// <summary>实际监听端口；未启动为 0。</summary>
        public static int Port { get { return _port; } }

        /// <summary>累计引擎握手成功的连接数（含还没发业务 JOIN 的）。</summary>
        public static long Connections { get { return Interlocked.Read(ref _connections); } }

        /// <summary>累计收到的业务 <see cref="JoinMagic"/> 条数。</summary>
        public static long Joins { get { return Interlocked.Read(ref _joins); } }

        public static long Inputs { get { return Interlocked.Read(ref _inputs); } }
        public static long Snapshots { get { return Interlocked.Read(ref _snapshots); } }

        /// <summary>非法业务行数（本类丢弃的 + 引擎按信封规则丢弃的）。</summary>
        public static long Malformed
        {
            get
            {
                var host = _host;
                return Interlocked.Read(ref _malformed) + (host != null ? host.DroppedCount : 0L);
            }
        }

        /// <summary>被拒绝的连接数（引擎满员拒绝的 + 两队都满而拒绝入局的）。</summary>
        public static long Rejected
        {
            get
            {
                var host = _host;
                return Interlocked.Read(ref _rejected) + (host != null ? host.RejectedCount : 0L);
            }
        }

        /// <summary>字段缺失/格式不对而被丢掉的 <see cref="InputMagic"/> 条数（键名必须与 <c>CsInputState</c> 逐字一致）。</summary>
        public static long InputBadFields { get { return Interlocked.Read(ref _inputBadFields); } }

        /// <summary>已经转交给模拟的远端输入次数（&gt;0 且 actor 的坐标在动 = 这条链真的通了）。</summary>
        public static long AppliedInputs { get { return Interlocked.Read(ref _appliedInputs); } }

        /// <summary>
        /// 被拒绝的远端输入条数：目标是**主机自己的玩家**（<c>LocalPlayer</c>）。
        /// <para>主机自己的身体只由本机输入驱动 —— 两个驾驶员会让它来回抢。</para>
        /// </summary>
        public static long RejectedOwnBodyInputs { get { return Interlocked.Read(ref _rejectedOwnBodyInputs); } }

        /// <summary>
        /// 被拒绝的远端输入条数：发送者尝试驱动**不属于自己的** actor（一台 peer 只能驱动自己那具身体）。
        /// </summary>
        public static long RejectedForeignBodyInputs { get { return Interlocked.Read(ref _rejectedForeignBodyInputs); } }

        /// <summary>身体还没建好就收到 INPUT 的条数（客机过早开始发输入）。</summary>
        public static long InputsBeforeBody { get { return Interlocked.Read(ref _inputsBeforeBody); } }

        /// <summary>
        /// 被丢弃的"旧局残留 actorId"输入条数：换局后客机还没拿到新 actorId 时，
        /// 旧 id 可能落到机器人身上 —— 这些输入绝不能驱动机器人。
        /// </summary>
        public static long RejectedStaleBodyInputs { get { return Interlocked.Read(ref _rejectedStaleBodyInputs); } }

        /// <summary>累计建出的远端玩家身体数。</summary>
        public static long RemotePlayersAdded { get { return Interlocked.Read(ref _remotePlayersAdded); } }

        /// <summary>累计移除的远端玩家身体数。</summary>
        public static long RemotePlayersRemoved { get { return Interlocked.Read(ref _remotePlayersRemoved); } }

        /// <summary>因超过 <see cref="MaxPayloadBytes"/> 而没有发出的业务载荷数（快照塞不下时这里会涨）。</summary>
        public static long OversizePayloads { get { return Interlocked.Read(ref _oversizePayloads); } }

        /// <summary>当前已建出身体、在快照里的远端玩家数。</summary>
        public static int RemotePlayerCount
        {
            get
            {
                var n = 0;
                lock (PeersLock)
                {
                    foreach (var kv in Peers) if (kv.Value.ActorId > 0) n++;
                }
                return n;
            }
        }

        /// <summary>已握手完成（回过 WELCOME）的客户端数。</summary>
        public static int WelcomedCount
        {
            get
            {
                var n = 0;
                lock (PeersLock)
                {
                    foreach (var kv in Peers) if (kv.Value.Welcomed && !kv.Value.Closed) n++;
                }
                return n;
            }
        }

        /// <summary>当前引擎侧已 JOIN 的连接数（含还没发业务 JOIN 的）。</summary>
        public static int ConnectionCount
        {
            get
            {
                var host = _host;
                return host != null ? host.ClientCount : 0;
            }
        }

        /// <summary>一行状态（日志 / 探针 / 面板共用，避免多处文案各写一套）。</summary>
        public static string Describe()
        {
            if (!IsRunning) return "对局网关未启动";
            return "对局网关 port=" + _port + " 连接=" + Connections + " 已握手=" + WelcomedCount +
                   "/" + ConnectionCount + " 收到 JOIN=" + Joins + " 收到 INPUT=" + Inputs +
                   " 已转交模拟=" + AppliedInputs + " INPUT字段非法=" + InputBadFields +
                   " 远端玩家=" + RemotePlayerCount + "（累计建/删=" + RemotePlayersAdded + "/" + RemotePlayersRemoved + "）" +
                   " 拒绝别人身体=" + RejectedForeignBodyInputs + " 旧局残留=" + RejectedStaleBodyInputs +
                   " 早于身体=" + InputsBeforeBody +
                   " 拒绝驱动主机自己=" + RejectedOwnBodyInputs +
                   " 推出快照=" + Snapshots + " 超限载荷=" + OversizePayloads +
                   " 非法行=" + Malformed + " 拒绝=" + Rejected;
        }

        // ---------------------------------------------------------------- 启停

        /// <summary>开网关。幂等：已在跑就只更新名字/人数。</summary>
        public static bool Start(out string error, int port = CsLanHost.DefaultGatewayPort,
                                string hostName = null, int maxPlayers = 10)
        {
            error = null;

            if (port <= 0 || port > 65535)
            {
                error = "对局网关端口非法：" + port + "（须在 1~65535）";
                Game.Logger.Error(Tag, error);
                return false;
            }

            var name = string.IsNullOrEmpty(hostName) ? CsLanHost.DefaultName : hostName;
            var max = maxPlayers < 0 ? 0 : maxPlayers;

            if (IsRunning)
            {
                // 引擎 Start 幂等：socket / 端口 / 线程都不动，只更新对外快照。
                _hostName = name;
                _maxPlayers = max;
                _host.Start(BuildSelfInfo(_port), BuildHostOptions(_port));
                Game.Logger.Info(Tag, "对局网关已在跑，仅更新参数：" + Describe());
                return true;
            }

            ResetState();
            _hostName = name;
            _maxPlayers = max;

            var host = CloverLan.CreateHost();
            host.OnClientJoined += HandleClientJoined;
            host.OnClientInput += HandleClientInput;
            host.OnClientLeft += HandleClientLeft;

            if (!host.Start(BuildSelfInfo(port), BuildHostOptions(port)))
            {
                // 引擎不抛：端口被占 / 平台不支持都在 LastError 里。
                error = host.LastError;
                Game.Logger.Error(Tag, "对局网关启动失败：" + error);
                host.Dispose();
                return false;
            }

            _host = host;
            _port = host.ListeningPort;
            _running = true;

            Game.Logger.Info(Tag,
                "对局网关已启动：TCP " + _port + "（" + JoinMagic + " → " + WelcomeMagic +
                "，此后每 " + SnapInterval.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                "s 一条 " + SnapMagic + "；传输层 = 引擎 ILanHost）");
            return true;
        }

        /// <summary>
        /// 开网关并返回**真正在监听**的端口：<paramref name="preferredPort"/> 起、逐个 +1 试，
        /// 跳过 <paramref name="reservedUdpPort"/>（同一台主机上裸 UDP 口不与应用口重合）。
        ///
        /// <para>为什么需要顺延：本机 8002 常被别的进程（另一个工程的服务端 / 上一次没退干净的实例）
        /// 占着，而引擎绑不上就返回失败 ⇒ 主机直接起不来。广播出去的必须是实际监听的那个端口。</para>
        /// </summary>
        public static bool StartOnFreePort(out int actualPort, int preferredPort, int reservedUdpPort,
            string hostName, int maxPlayers, out string error)
        {
            error = null;
            actualPort = 0;
            if (IsRunning)
            {
                actualPort = _port;
                return true;
            }

            var first = (preferredPort > 0 && preferredPort <= 65535) ? preferredPort : CsLanHost.DefaultGatewayPort;
            for (var i = 0; i <= PortFallbackTries; i++)
            {
                var candidate = first + i;
                if (candidate > 65535) break;
                if (reservedUdpPort > 0 && candidate == reservedUdpPort) continue;

                string err;
                if (!Start(out err, candidate, hostName, maxPlayers))
                {
                    error = err;
                    if (i == 0) Game.Logger.Warn(Tag, "网关口 " + candidate + " 起不来 ⇒ 试相邻端口：" + err);
                    continue;
                }

                actualPort = candidate;
                if (candidate != first)
                {
                    Game.Logger.Warn(Tag,
                        "网关口 " + first + " 被别的进程占着 ⇒ 已改用 " + candidate +
                        "（广播出去的是这个口，加入方会自动用它）");
                }
                return true;
            }

            if (error == null) error = "网关口从 " + first + " 起到 " + (first + PortFallbackTries) + " 全部不可用";
            return false;
        }

        /// <summary>关网关（幂等）。引擎会给全部连接发 BYE 再关 socket。</summary>
        public static void Stop()
        {
            var wasRunning = _running;
            _running = false;

            var host = _host;
            _host = null;
            if (host != null)
            {
                try { host.Stop(); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextStopLogAt))
                    {
                        Game.Logger.Warn(Tag, "停止局域网主机失败（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                host.Dispose();
            }

            _port = 0;

            List<Peer> peers;
            lock (PeersLock)
            {
                peers = new List<Peer>(Peers.Values);
                Peers.Clear();
            }
            for (var i = 0; i < peers.Count; i++)
            {
                var p = peers[i];
                p.Closed = true;
                var actorId = p.ActorId;
                if (actorId <= 0) continue;
                p.ActorId = 0;
                lock (RemoveLock) PendingRemovals.Add(actorId);
            }

            // Stop() 之后 Pump 不再跑 ⇒ 待移除的远端身体必须在这里落地（本方法由主线程调）。
            DrainPendingRemovals(_match);
            lock (JoinLock) PendingJoins.Clear();
            lock (RemoveLock) PendingRemovals.Clear();
            lock (InputLock) PendingInputs.Clear();
            _match = null;

            if (wasRunning)
            {
                Game.Logger.Info(Tag, "对局网关已停止：" + Describe());
            }
        }

        // ---------------------------------------------------------------- 主线程：每帧泵

        /// <summary>
        /// 主线程每帧调一次。<paramref name="dt"/> 累计到 <see cref="SnapInterval"/> 才真正出快照。
        /// </summary>
        public static void Pump(ICsMatch match, float dt)
        {
            if (!_running) return;

            // 先落地远端玩家的建 / 删（引擎回调只入队，模拟只有一个写入者 = 主线程）。
            ProcessPendingPlayers(match);

            _accum += dt;
            if (_accum < SnapInterval) return;
            _accum = 0f;

            if (match == null || !match.IsRunning) return;

            // 出快照之前先把客户端的输入交给模拟：同一帧里"远端输入已生效"与"快照已反映它"对齐，
            // 客户端看到的位移不会凭空晚一帧。
            ApplyRemoteInputs(match);

            PushSnapshot(BuildSnapshot(match));
        }

        /// <summary>
        /// **主线程**：把引擎回调排队的"外部玩家建身体 / 移除身体"落到模拟上，建好后回 WELCOME。
        /// <para>顺序：先移除（腾出人数名额）→ 再重建整场重开后丢失的身体 → 最后处理新加入。</para>
        /// </summary>
        private static void ProcessPendingPlayers(ICsMatch match)
        {
            _match = match;
            if (match == null) return;

            DrainPendingRemovals(match);
            if (!match.IsRunning) return;

            lock (PeersLock)
            {
                _peerScratch.Clear();
                _peerScratch.AddRange(Peers.Values);
            }
            for (var i = 0; i < _peerScratch.Count; i++)
            {
                var p = _peerScratch[i];
                if (p.Closed) continue;

                if (p.ActorId <= 0)
                {
                    // 引擎握了手却一直没发业务 JOIN：不再占着名额，回 BYE 让对端收尾。
                    if (unchecked(Environment.TickCount - p.ConnectedAt) > PeerIdleTimeoutMs)
                    {
                        // 先发 BYE 再标记关闭：SendToPeer 对已关闭的 peer 直接拒发。
                        SendToPeer(p, ByeMagic + "|{\"reason\":\"timeout\"}");
                        p.Closed = true;
                        Game.Logger.Warn(Tag,
                            "客户端 " + p.Endpoint + " 已在引擎层连上 " + (PeerIdleTimeoutMs / 1000) +
                            "s 但没发 " + JoinMagic + " ⇒ 回 " + ByeMagic + " 并放弃这条连接");
                    }
                    continue;
                }

                // 整场重开（CsMatch.Start）会清空全部 actor ⇒ 已登记的远端身体不见了，按原名字重建。
                var body = match.Find(p.ActorId);
                if (body != null && !body.IsBot &&
                    string.Equals(body.Name, p.AssignedName, StringComparison.Ordinal)) continue;

                Game.Logger.Warn(Tag,
                    "客户端 " + p.Endpoint + " 的身体 actor=" + p.ActorId + " 不在当前比赛里（整场重开？）" +
                    "⇒ 按原名字 \"" + p.RequestedName + "\" 重建");
                p.ActorId = 0;
                lock (JoinLock) PendingJoins.Add(new PendingJoin { Peer = p, Name = p.RequestedName });
            }

            List<PendingJoin> joins;
            lock (JoinLock)
            {
                if (PendingJoins.Count == 0) return;
                joins = new List<PendingJoin>(PendingJoins);
                PendingJoins.Clear();
            }

            for (var i = 0; i < joins.Count; i++)
            {
                var p = joins[i].Peer;
                if (p == null || p.Closed) continue;
                if (p.ActorId > 0) continue;                 // 已经有身体：不建第二具

                // 阵营：优先 CT，满了再 T（AddRemotePlayer 自带满员检查）。
                var id = match.AddRemotePlayer(joins[i].Name, CsTeam.CT);
                if (id <= 0) id = match.AddRemotePlayer(joins[i].Name, CsTeam.T);
                if (id <= 0)
                {
                    Interlocked.Increment(ref _rejected);
                    p.JoinQueued = false;
                    Game.Logger.Warn(Tag,
                        "客户端 " + p.Endpoint + " 加入失败：CT / T 两队都满员 ⇒ 回 " + ByeMagic);
                    SendToPeer(p, ByeMagic + "|{\"reason\":\"full\"}");
                    continue;
                }

                var actor = match.Find(id);
                p.ActorId = id;
                p.AssignedName = actor != null ? actor.Name : joins[i].Name;
                p.Name = p.AssignedName;
                p.JoinQueued = false;
                var team = actor != null ? actor.Team : CsTeam.CT;

                Interlocked.Increment(ref _remotePlayersAdded);
                SendToPeer(p, BuildWelcome(p, id, team));
                p.Welcomed = true;
                Game.Logger.Info(Tag,
                    "客户端入局：" + p.Endpoint + " name=\"" + p.AssignedName + "\" ⇒ actor=" + id +
                    "（" + team + "），已回 " + WelcomeMagic + "（含 actorId）");
            }
        }

        /// <summary>主线程：落地排队中的远端身体移除（断开 / 超时 / 网关停止）。</summary>
        private static void DrainPendingRemovals(ICsMatch match)
        {
            if (match == null || !match.IsRunning) return;

            List<int> removals;
            lock (RemoveLock)
            {
                if (PendingRemovals.Count == 0) return;
                removals = new List<int>(PendingRemovals);
                PendingRemovals.Clear();
            }

            for (var i = 0; i < removals.Count; i++)
            {
                match.RemoveRemotePlayer(removals[i]);
                Interlocked.Increment(ref _remotePlayersRemoved);
            }
        }

        /// <summary>
        /// **主线程**：把引擎回调收到的 <see cref="InputMagic"/> 交给模拟（每帧最多一次，
        /// 由 <see cref="Pump"/> 在出快照之前调）。
        ///
        /// <para>同一 actor 只转交**最后一条**（见 <see cref="PendingInputs"/>）：主线程 10 Hz 出快照、
        /// 客户端 20 Hz 发输入 ⇒ 每次转交都是"这段窗口里的最新意图"。</para>
        /// </summary>
        public static void ApplyRemoteInputs(ICsMatch match)
        {
            if (match == null) return;

            List<PendingInput> batch;
            lock (InputLock)
            {
                if (PendingInputs.Count == 0) return;
                batch = new List<PendingInput>(PendingInputs.Count);
                foreach (var kv in PendingInputs) batch.Add(kv.Value);
                PendingInputs.Clear();
            }

            var local = match.LocalPlayer;
            for (var i = 0; i < batch.Count; i++)
            {
                // 主机自己的玩家不接受远端输入：它是主机本机输入的驾驶员，再来一个就是两个驾驶抢一具身体。
                if (local != null && batch[i].ActorId == local.Id)
                {
                    var rejected = Interlocked.Increment(ref _rejectedOwnBodyInputs);
                    if (rejected == 1 || rejected % 200 == 0)
                    {
                        Game.Logger.Warn(Tag,
                            "丢弃一条远端 " + InputMagic + "：目标是主机自己的玩家（actor=" + batch[i].ActorId +
                            "，来自 " + batch[i].From + "，累计丢弃 " + rejected +
                            " 条）——客户端把同名的主机玩家误认成了自己的腿");
                    }
                    continue;
                }

                // 只驱动"真人身体"：换局后残留的旧 actorId 可能落在机器人身上，绝不能让它代替 bot 行动。
                var body = match.Find(batch[i].ActorId);
                if (body == null || body.IsBot)
                {
                    var stale = Interlocked.Increment(ref _rejectedStaleBodyInputs);
                    if (stale <= 3)
                    {
                        Game.Logger.Warn(Tag,
                            "丢弃一条远端 " + InputMagic + "：actor=" + batch[i].ActorId +
                            "（来自 " + batch[i].From + "）在当前比赛里不是真人身体（换局后还没拿到新 actorId），" +
                            "累计丢弃 " + stale + " 条");
                    }
                    continue;
                }

                match.SetRemoteInput(batch[i].ActorId, batch[i].Input);
                Interlocked.Increment(ref _appliedInputs);
            }
        }

        /// <summary>测试入口：不依赖 <see cref="Pump"/> 的计时，立刻推一条快照（探针用）。</summary>
        public static string PushSnapshotForTest(ICsMatch match)
        {
            if (match == null) return null;
            var line = BuildSnapshot(match);
            PushSnapshot(line);
            return line;
        }

        // ---------------------------------------------------------------- 引擎回调

        /// <summary>
        /// 引擎握手完成（<c>CLOVER-LAN-WELCOME/1</c> 已发）。此处**只登记**：身体要等客机的业务
        /// <see cref="JoinMagic"/> 到齐后由主线程建（客机可能还没选好角色，且模拟只允许主线程写）。
        /// </summary>
        private static void HandleClientJoined(LanPeerInfo info)
        {
            if (info == null) return;

            var peer = new Peer
            {
                Id = info.Id,
                Endpoint = string.IsNullOrEmpty(info.Address) ? "-" : info.Address,
                Name = info.Name ?? "-",
                RequestedName = info.Name ?? "-",
                ConnectedAt = Environment.TickCount,
            };

            lock (PeersLock) Peers[info.Id] = peer;
            Interlocked.Increment(ref _connections);

            Game.Logger.Info(Tag,
                "客户端已接入引擎层：" + peer.Endpoint + " peerId=" + info.Id + "（等它发 " + JoinMagic + "）");
        }

        /// <summary>
        /// 收到一条合法上行（引擎已拆行、已解 base64）。载荷就是业务行文本，这里按魔数分流。
        /// <para>不碰模拟：只用待建 / 待删 / 待转交表登记。</para>
        /// </summary>
        private static void HandleClientInput(LanPeerInfo info, byte[] payload)
        {
            if (info == null || payload == null) return;

            Peer peer;
            lock (PeersLock) Peers.TryGetValue(info.Id, out peer);
            if (peer == null || peer.Closed) return;

            Interlocked.Add(ref peer.Received, payload.Length);

            string line;
            try
            {
                line = Encoding.UTF8.GetString(payload);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref peer.BadLines);
                Interlocked.Increment(ref _malformed);
                Game.Logger.Warn(Tag,
                    "客户端 " + peer.Endpoint + " 的载荷不是合法 UTF-8（" + ex.GetType().Name + "）⇒ 丢弃");
                return;
            }

            HandleLine(peer, line);
        }

        /// <summary>引擎判定断开（对端 BYE / 读写出错 / 主机 Stop）。摘掉身体、留痕。</summary>
        private static void HandleClientLeft(LanPeerInfo info, string reason)
        {
            if (info == null) return;

            Peer peer;
            lock (PeersLock) Peers.TryGetValue(info.Id, out peer);
            if (peer == null) return;

            ClosePeer(peer, string.IsNullOrEmpty(reason) ? "-" : reason);
        }

        // ---------------------------------------------------------------- 业务分流

        private static void HandleLine(Peer c, string line)
        {
            var bar = line.IndexOf('|');
            if (bar <= 0)
            {
                Interlocked.Increment(ref c.BadLines);
                Interlocked.Increment(ref _malformed);
                return;
            }

            var magic = line.Substring(0, bar);
            var json = line.Substring(bar + 1);

            if (magic == JoinMagic)
            {
                if (c.Welcomed || c.JoinQueued)
                {
                    Game.Logger.Warn(Tag,
                        "重复 " + JoinMagic + "（" + c.Endpoint + "，name=\"" + c.Name + "\"）⇒ 忽略，不建第二具身体");
                    return;
                }

                // 建身体必须走主线程（模拟只有一个写入者）⇒ 这里只登记，Pump 里建完再回 WELCOME。
                c.RequestedName = ReadString(json, "name", "-");
                c.Name = c.RequestedName;
                c.JoinQueued = true;
                Interlocked.Increment(ref _joins);
                lock (JoinLock) PendingJoins.Add(new PendingJoin { Peer = c, Name = c.RequestedName });
                Game.Logger.Info(Tag,
                    "客户端加入请求：" + c.Endpoint + " name=\"" + c.RequestedName +
                    "\" ⇒ 等主线程建身体后回 " + WelcomeMagic + "（含 actorId）");
                return;
            }

            if (magic == InputMagic)
            {
                var n = Interlocked.Increment(ref _inputs);
                int actorId;
                CsInputState input;
                if (!TryParseInput(json, out actorId, out input))
                {
                    // 非预期分支：线格式两端同值，解不出来就是有一端写错了 ⇒ 计数 + 前几条留痕（不静默）。
                    var bad = Interlocked.Increment(ref _inputBadFields);
                    if (bad <= 3)
                    {
                        Game.Logger.Warn(Tag,
                            "收到 " + InputMagic + " 但字段解不出来（第 " + bad + " 条）：" + Truncate(json, 200) +
                            "；键名必须与 CsInputState 的成员逐字一致，且 id / Move / Yaw / Pitch 与 6 个布尔键都要在");
                    }
                    return;
                }

                // 一台客机只能驱动自己的那具身体：还没身体 / 指向别人的 actor 一律丢弃。
                var owner = c.ActorId;
                if (owner <= 0)
                {
                    var early = Interlocked.Increment(ref _inputsBeforeBody);
                    if (early <= 3)
                    {
                        Game.Logger.Warn(Tag,
                            "收到 " + InputMagic + " 但该连接还没有身体（actor=" + actorId + "，来自 " + c.Endpoint +
                            "）⇒ 丢弃（第 " + early + " 条；客机应在收到 WELCOME 的 actorId 之后再发输入）");
                    }
                    return;
                }
                if (actorId != owner)
                {
                    var foreign = Interlocked.Increment(ref _rejectedForeignBodyInputs);
                    if (foreign <= 3 || foreign % 200 == 0)
                    {
                        Game.Logger.Warn(Tag,
                            "丢弃一条远端 " + InputMagic + "：actor=" + actorId + " 不属于这台客机（它的身体是 actor=" +
                            owner + "，来自 " + c.Endpoint + "，累计丢弃 " + foreign + " 条）");
                    }
                    return;
                }

                // 只登记意图：真正的"应用到模拟"在主线程 ApplyRemoteInputs 里做（线程模型见类注释）。
                var e = new PendingInput { ActorId = actorId, Input = input, From = c.Endpoint };
                lock (InputLock) PendingInputs[actorId] = e;

                if (n == 1 || n % 200 == 0)
                {
                    Game.Logger.Info(Tag,
                        "收到 " + InputMagic + "（第 " + n + " 条，来自 " + c.Endpoint + "）：actor=" + actorId +
                        " move=(" + input.Move.x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                        "," + input.Move.y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                        ") yaw=" + input.Yaw.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                        " jump=" + input.Jump + " fire=" + input.Fire);
                }
                return;
            }

            if (magic == ByeMagic)
            {
                c.Closed = true;
                return;
            }

            Interlocked.Increment(ref c.BadLines);
            Interlocked.Increment(ref _malformed);
        }

        // ---------------------------------------------------------------- 组报文

        /// <summary>
        /// 回给客机的开局信息。<c>actorId</c> / <c>playerName</c> / <c>team</c> 让客机不必再靠"同名"
        /// 从快照里认领自己。
        /// </summary>
        private static string BuildWelcome(Peer c, int actorId, CsTeam team)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append(WelcomeMagic).Append('|');
            sb.Append("{\"proto\":").Append(ProtocolVersion);
            sb.Append(",\"name\":\"").Append(Escape(_hostName)).Append('"');
            sb.Append(",\"host\":\"").Append(Escape(AdvertisedEndpoint())).Append('"');
            sb.Append(",\"maxPlayers\":").Append(_maxPlayers);
            sb.Append(",\"snapHz\":").Append((1f / SnapInterval).ToString("F1", inv));
            sb.Append(",\"actorId\":").Append(actorId);
            sb.Append(",\"playerName\":\"").Append(Escape(c != null ? c.AssignedName : "-")).Append('"');
            sb.Append(",\"team\":\"").Append(team.ToString()).Append('"');
            sb.Append('}');
            return sb.ToString();
        }

        private static string AdvertisedEndpoint()
        {
            var host = CsLanHost.AdvertisedHost;
            if (string.IsNullOrEmpty(host)) host = "0.0.0.0";
            return host + ":" + _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把主机权威的世界读成一行 JSON。字段一律**扁平 + 定序**，便于对端逐字段核对
        /// （不用 <c>JsonUtility</c>：它只认 <c>Serializable</c> 类，且对"逐帧拼一行"这件事更重）。
        /// </summary>
        private static string BuildSnapshot(ICsMatch m)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(1024);
            sb.Append(SnapMagic).Append('|');
            sb.Append("{\"round\":").Append(m.RoundNumber);
            sb.Append(",\"phase\":\"").Append(m.Phase.ToString()).Append('"');
            sb.Append(",\"scoreT\":").Append(m.ScoreT);
            sb.Append(",\"scoreCT\":").Append(m.ScoreCT);
            sb.Append(",\"aliveT\":").Append(m.AliveCount(CsTeam.T));
            sb.Append(",\"aliveCT\":").Append(m.AliveCount(CsTeam.CT));
            sb.Append(",\"dropped\":").Append(m.DroppedWeapons != null ? m.DroppedWeapons.Count : 0);
            sb.Append(",\"actors\":[");

            var actors = m.Actors;
            var first = true;
            if (actors != null)
            {
                for (var i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(a.Id);
                    sb.Append(",\"name\":\"").Append(Escape(a.Name)).Append('"');
                    sb.Append(",\"team\":\"").Append(a.Team.ToString()).Append('"');
                    sb.Append(",\"x\":").Append(a.Position.x.ToString("F3", inv));
                    sb.Append(",\"y\":").Append(a.Position.y.ToString("F3", inv));
                    sb.Append(",\"z\":").Append(a.Position.z.ToString("F3", inv));
                    sb.Append(",\"yaw\":").Append(a.Yaw.ToString("F1", inv));
                    sb.Append(",\"hp\":").Append(a.Health);
                    sb.Append(",\"alive\":").Append(a.IsAlive ? "true" : "false");
                    sb.Append(",\"bot\":").Append(a.IsBot ? "true" : "false");
                    sb.Append(",\"w\":\"").Append(Escape(a.ActiveWeapon)).Append('"');
                    sb.Append('}');
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 解一行 <see cref="InputMagic"/> 的 JSON。键名**逐字** = <c>CsInputState</c> 的成员名
        /// （<c>Move</c> 是 <c>{"x":…,"y":…}</c>），外加 <c>id</c>。
        /// <para>缺任何一个键都算非法：线格式两端同值、字段是定死的 —— 静默按默认值补会让"客户端写错了"
        /// 表现成"远端输入没反应"，而那种现象查不出原因。</para>
        /// </summary>
        private static bool TryParseInput(string json, out int actorId, out CsInputState input)
        {
            actorId = 0;
            input = default(CsInputState);
            if (string.IsNullOrEmpty(json)) return false;

            float id;
            if (!TryReadNumber(json, "id", out id)) return false;
            actorId = (int)id;

            var move = ReadObjectBody(json, "Move");
            if (move == null) return false;
            float mx, my;
            if (!TryReadNumber(move, "x", out mx)) return false;
            if (!TryReadNumber(move, "y", out my)) return false;

            bool jump, crouch, walk, fire, zoom, attack2;
            if (!TryReadBool(json, "Jump", out jump)) return false;
            if (!TryReadBool(json, "Crouch", out crouch)) return false;
            if (!TryReadBool(json, "Walk", out walk)) return false;
            if (!TryReadBool(json, "Fire", out fire)) return false;
            if (!TryReadBool(json, "Zoom", out zoom)) return false;
            if (!TryReadBool(json, "Attack2", out attack2)) return false;

            float yaw, pitch;
            if (!TryReadNumber(json, "Yaw", out yaw)) return false;
            if (!TryReadNumber(json, "Pitch", out pitch)) return false;

            // 本文件不引 UnityEngine（其余部分与引擎无关）⇒ 这里显式写全名（Vector2 是纯结构体，后台线程可造）。
            input.Move = new UnityEngine.Vector2(mx, my);
            input.Jump = jump;
            input.Crouch = crouch;
            input.Walk = walk;
            input.Fire = fire;
            input.Zoom = zoom;
            input.Attack2 = attack2;
            input.Yaw = yaw;
            input.Pitch = pitch;
            return true;
        }

        /// <summary>找 <c>"key"</c> 后 <c>:</c> 之后的第一个非空白字符的位置；没有这个键返回 -1。</summary>
        private static int ValueStart(string json, string key)
        {
            var k = "\"" + key + "\"";
            var at = json.IndexOf(k, StringComparison.Ordinal);
            if (at < 0) return -1;
            var colon = json.IndexOf(':', at + k.Length);
            if (colon < 0) return -1;
            var p = colon + 1;
            while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
            return p < json.Length ? p : -1;
        }

        /// <summary>读数字（整数 / 小数 / 负号；不认 NaN / Infinity —— 那些不是本协议的合法值）。</summary>
        private static bool TryReadNumber(string json, string key, out float value)
        {
            value = 0f;
            var start = ValueStart(json, key);
            if (start < 0) return false;

            var p = start;
            if (json[p] == '-' || json[p] == '+') p++;
            var digits = false;
            while (p < json.Length)
            {
                var ch = json[p];
                if (ch >= '0' && ch <= '9') { digits = true; p++; continue; }
                if (ch == '.' || ch == 'e' || ch == 'E' || ch == '-' || ch == '+') { p++; continue; }
                break;
            }
            if (!digits) return false;

            return float.TryParse(json.Substring(start, p - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>读布尔（只认 <c>true</c> / <c>false</c>；<c>1</c>/<c>0</c> 不算 —— 本协议两端同值）。</summary>
        private static bool TryReadBool(string json, string key, out bool value)
        {
            value = false;
            var start = ValueStart(json, key);
            if (start < 0) return false;
            if (json.Length - start >= 4 && string.CompareOrdinal(json, start, "true", 0, 4) == 0) { value = true; return true; }
            if (json.Length - start >= 5 && string.CompareOrdinal(json, start, "false", 0, 5) == 0) { value = false; return true; }
            return false;
        }

        /// <summary>取 <c>"key"</c> 对应的**对象字面量内部**文本（<c>{…}</c> 里那一截）；不是对象返回 null。</summary>
        private static string ReadObjectBody(string json, string key)
        {
            var start = ValueStart(json, key);
            if (start < 0 || json[start] != '{') return null;

            var depth = 0;
            for (var i = start; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start + 1, i - start - 1);
                }
            }
            return null;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string ReadString(string json, string key, string fallback)
        {
            var k = "\"" + key + "\"";
            var at = json.IndexOf(k, StringComparison.Ordinal);
            if (at < 0) return fallback;
            var colon = json.IndexOf(':', at + k.Length);
            if (colon < 0) return fallback;
            var open = json.IndexOf('"', colon + 1);
            if (open < 0) return fallback;
            var close = json.IndexOf('"', open + 1);
            if (close < 0) return fallback;
            return json.Substring(open + 1, close - open - 1);
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 小工具

        private static LanHostInfo BuildSelfInfo(int port)
        {
            var advertised = CsLanHost.AdvertisedHost;
            return LanHostInfo.Create(
                string.IsNullOrEmpty(advertised) ? "0.0.0.0" : advertised,
                port, 0, null, _hostName,
                WelcomedCount, _maxPlayers, null, null);
        }

        /// <summary>
        /// 引擎主机的启动参数。<c>MaxLineBytes</c> 与客机的业务行上限同值 ——
        /// 它是引擎信封行的上限，业务载荷上限见 <see cref="MaxPayloadBytes"/>。
        /// </summary>
        private static LanHostOptions BuildHostOptions(int port)
        {
            return new LanHostOptions
            {
                Port = port,
                MaxClients = MaxClients,
                MaxLineBytes = MaxLineBytes,
            };
        }

        private static void ResetState()
        {
            _accum = 0f;
            Interlocked.Exchange(ref _connections, 0);
            Interlocked.Exchange(ref _joins, 0);
            Interlocked.Exchange(ref _inputs, 0);
            Interlocked.Exchange(ref _snapshots, 0);
            Interlocked.Exchange(ref _malformed, 0);
            Interlocked.Exchange(ref _rejected, 0);
            Interlocked.Exchange(ref _inputBadFields, 0);
            Interlocked.Exchange(ref _appliedInputs, 0);
            Interlocked.Exchange(ref _rejectedOwnBodyInputs, 0);
            Interlocked.Exchange(ref _rejectedForeignBodyInputs, 0);
            Interlocked.Exchange(ref _rejectedStaleBodyInputs, 0);
            Interlocked.Exchange(ref _inputsBeforeBody, 0);
            Interlocked.Exchange(ref _remotePlayersAdded, 0);
            Interlocked.Exchange(ref _remotePlayersRemoved, 0);
            Interlocked.Exchange(ref _oversizePayloads, 0);
            lock (InputLock) PendingInputs.Clear();
            lock (JoinLock) PendingJoins.Clear();
            lock (RemoveLock) PendingRemovals.Clear();
            lock (PeersLock) Peers.Clear();
        }

        /// <summary>
        /// 推一条快照给已握手的客机。
        /// <para>正常情况（全部连接都已握手）走一次 <see cref="ILanHost.Broadcast"/>；还有连接没握手时
        /// 逐条 <see cref="ILanHost.SendTo"/> 只发给已握手的 —— 客机不能先收到快照再收到 WELCOME。</para>
        /// </summary>
        private static void PushSnapshot(string line)
        {
            var host = _host;
            if (host == null || !_running || line == null) return;

            var bytes = Encoding.UTF8.GetBytes(line);
            if (bytes.Length > MaxPayloadBytes)
            {
                var over = Interlocked.Increment(ref _oversizePayloads);
                if (over == 1 || over % 100 == 0)
                {
                    Game.Logger.Warn(Tag,
                        "快照 " + bytes.Length + " 字节超过引擎单行能承载的业务载荷上限 " + MaxPayloadBytes +
                        " ⇒ 本条不发（对端会按超长行丢弃）；累计 " + over +
                        " 条。角色越多快照越大：可减少 bots 或人数");
                }
                return;
            }

            Interlocked.Increment(ref _snapshots);

            var total = ConnectionCount;
            var welcomed = WelcomedCount;
            if (welcomed > 0 && welcomed == total)
            {
                host.Broadcast(bytes);
                return;
            }

            lock (PeersLock)
            {
                _peerScratch.Clear();
                foreach (var kv in Peers)
                {
                    var p = kv.Value;
                    if (p.Welcomed && !p.Closed) _peerScratch.Add(p);
                }
            }
            for (var i = 0; i < _peerScratch.Count; i++) SendToPeerBytes(_peerScratch[i], bytes);
        }

        private static bool SendToPeer(Peer peer, string line)
        {
            if (peer == null || line == null) return false;
            return SendToPeerBytes(peer, Encoding.UTF8.GetBytes(line));
        }

        private static bool SendToPeerBytes(Peer peer, byte[] bytes)
        {
            var host = _host;
            if (host == null || peer == null || peer.Closed || bytes == null) return false;

            if (bytes.Length > MaxPayloadBytes)
            {
                var over = Interlocked.Increment(ref _oversizePayloads);
                if (over == 1 || over % 100 == 0)
                {
                    Game.Logger.Warn(Tag,
                        "下行载荷 " + bytes.Length + " 字节超过引擎单行能承载的上限 " + MaxPayloadBytes +
                        " ⇒ 未发送（累计 " + over + " 条）");
                }
                return false;
            }

            if (host.SendTo(peer.Id, bytes))
            {
                Interlocked.Increment(ref peer.Sent);
                return true;
            }

            if (ShouldLogBestEffort(ref _nextSendFailLogAt))
            {
                Game.Logger.Warn(Tag,
                    "下行发给 " + peer.Endpoint + "（peerId=" + peer.Id + "）失败（尽力而为路径，不影响开局）");
            }
            return false;
        }

        private static void ClosePeer(Peer c, string reason)
        {
            if (c == null) return;
            var already = c.Closed;
            c.Closed = true;

            lock (PeersLock) Peers.Remove(c.Id);

            // 断开：这具身体要摘掉。引擎回调不碰模拟 ⇒ 由主线程 Pump 落地。
            var actorId = c.ActorId;
            if (actorId > 0)
            {
                c.ActorId = 0;
                lock (RemoveLock) PendingRemovals.Add(actorId);
            }

            if (!already)
            {
                Game.Logger.Info(Tag, "客户端断开：" + c.Endpoint + " name=\"" + c.Name +
                    "\" actor=" + (actorId > 0 ? actorId.ToString() : "-") +
                    " 收字节=" + c.Received + " 出报文=" + c.Sent + "（非法行 " + c.BadLines +
                    "，原因 " + reason + "）");
            }
        }
    }
}
