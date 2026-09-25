using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
    /// <para><b>为什么需要它</b>：<see cref="CsLanHost"/> 只解决了"能被发现"（有人问、有人答）——
    /// 它广播出去的那个 <c>gateway=host:8002</c> **本来没有任何进程在监听**（实测：点"加入"之后接不上）。
    /// 本类就是把那个端口真正**开起来**：监听 TCP、接受加入、回开局信息、此后按固定频率把
    /// **主机权威的世界状态**推给每个已握手的客户端。</para>
    ///
    /// <para><b>线格式（不臆造，逐字定死在这里；两端必须同值）</b>：一律 UTF-8、
    /// **一行一条报文、以 <c>\n</c> 结束**，形如 <c>&lt;MAGIC&gt;|&lt;json&gt;</c>。
    /// <list type="bullet">
    /// <item>客户端 → 主机：<c>CS16-LAN-JOIN/1|{{"name":"…","proto":1}}</c></item>
    /// <item>客户端 → 主机（每 0.05 s）：<c>CS16-LAN-INPUT/1|{{"id":&lt;actorId&gt;,"Move":{{"x":…,"y":…}},"Jump":…,"Crouch":…,"Walk":…,"Fire":…,"Zoom":…,"Attack2":…,"Yaw":…,"Pitch":…}}</c>
    /// —— 键名**逐字** = <c>CsInputState</c> 的成员名（<c>Move</c> 是 <c>{{x,y}}</c>），
    /// 多一个 <c>id</c> 指明这是谁的输入。</item>
    /// <item>主机 → 客户端：<c>CS16-LAN-WELCOME/1|{{"proto":1,"map":…,"host":…,"round":…,"phase":…,"players":…,"maxPlayers":…}}</c></item>
    /// <item>主机 → 客户端（每 <see cref="SnapInterval"/> 秒）：<c>CS16-LAN-SNAP/1|{{…"actors":[…]}}</c></item>
    /// <item>任一方可发：<c>CS16-LAN-BYE/1|{{}}</c></item>
    /// </list>
    /// 单行上限 <see cref="MaxLineBytes"/>，超了算非法行并丢弃（不按它分配内存）。</para>
    ///
    /// <para><b>线程模型（关键：Unity 的东西只在主线程碰）</b>：
    /// <list type="number">
    /// <item><b>主线程</b>：每帧调 <see cref="Pump"/> —— 攒够 <see cref="SnapInterval"/> 就把
    /// <see cref="ICsMatch"/> 读成一行 JSON 文本，**入队**到每个已握手客户端的发送队列。
    /// 主线程**不碰任何 socket**。</item>
    /// <item><b>每客户端一个后台线程</b>：既读（<c>DataAvailable</c> + 逐字节收行）又写
    /// （把队列里的行 <c>Write</c> 出去）。空转时 <c>Sleep(<see cref="PollSleepMs"/>)</c>，
    /// 所以既不会忙等、也能在 <see cref="Stop"/> 后 ≤ 一个轮询周期退出。</item>
    /// <item>线程都是 <c>IsBackground = true</c> ⇒ 不拦编辑器关闭。</item>
    /// </list></para>
    ///
    /// <para><b>本类到哪一步为止</b>：能连上 / 能握手 / 能拿到开局信息 / 能持续收到世界快照（主机权威）/
    /// 能收到客户端的 <c>CS16-LAN-INPUT/1</c> 并**应用到主机模拟**（远端 actor 由对端的输入驱动）。
    /// <c>Module/View/CsLanRemoteView</c> 把 <c>actors[]</c> 画成场上的远端角色。</para>
    ///
    /// <para><b>远端输入的线程/时机</b>：解析在**客户端会话线程**上做，只把结果写进待转交表；
    /// 主线程每帧 <see cref="Pump"/> 出快照之前调一次 <see cref="ApplyRemoteInputs"/> 把它交给模拟 ——
    /// 所以模拟只有主线程一个写入者，网络线程永远不碰 <see cref="ICsMatch"/>。</para>
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

        /// <summary>单行字节上限（超了算非法行、丢弃）。</summary>
        public const int MaxLineBytes = 4096;

        /// <summary>最大同时连接数（局域网小局，够用即可）。</summary>
        public const int MaxClients = 16;

        /// <summary>快照间隔（秒）⇒ 10 Hz。不用 <c>Time.deltaTime</c> 之外的时间源，便于驱动里控制。</summary>
        public const float SnapInterval = 0.1f;

        /// <summary>客户端线程的空转睡眠（毫秒）：决定 <see cref="Stop"/> 后线程退出的最坏时延。</summary>
        private const int PollSleepMs = 4;

        /// <summary>「端口上是否已有别人在听」的探测超时（毫秒）。</summary>
        private const int ProbeTimeoutMs = 250;

        private sealed class Peer
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public Thread Thread;
            public string Endpoint = "-";
            public string Name = "-";
            public volatile bool Welcomed;
            public volatile bool Closed;
            public readonly Queue<string> Out = new Queue<string>();
            public readonly object Lock = new object();
            public long Sent;
            public long Received;
            public long BadLines;
        }

        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _port;
        private static string _hostName = CsLanHost.DefaultName;
        private static int _maxPlayers = 10;

        private static readonly List<Peer> Peers = new List<Peer>();
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

        /// <summary>一条待转交的远端输入（客户端会话线程写、主线程取走）。</summary>
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
        /// 线程外（抛出去会改变行为：AcceptLoop / PeerLoop 会因此退出，客户端表现为"网关还在跑、
        /// 但这台机器永远连不上"）；但**吞掉必须留下现场**，否则只剩"连不上"这个现象、没有原因。
        ///
        /// <para>每处失败最多每 <see cref="BestEffortLogIntervalMs"/> ms 一条 —— 局域网应答端会被同网段
        /// 无关流量打，不许每包 / 每连接刷屏；判定走 <see cref="ShouldLogBestEffort"/>。</para>
        ///
        /// <para><b>为什么新留痕写 <c>Game.Logger.Warn</c>（不带 <c>?.</c>）</b>：<c>Game.Logger</c>
        /// 由引擎保证**永不为 null**（<c>Runtime/Core/Game.cs:143-149</c>），且落盘 <c>Logger</c> 走
        /// <c>ConcurrentQueue</c> + <c>ConsoleLogger</c> 自带 try/catch ⇒ 后台线程直呼安全。
        /// <c>?.</c> 会把"日志打没打"变成不可判（本文件既有行的 <c>?.</c> 保持原样、未动）。</para>
        /// </summary>
        private const int BestEffortLogIntervalMs = 5000;

        private static int _nextListenerStopLogAt;    // Stop()：关监听
        private static int _nextProbeCloseLogAt;      // 端口探测结束时关探测连接
        private static int _nextRejectLogAt;          // 满员：回 BYE / 关连接
        private static int _nextEndpointLogAt;        // 取对端地址失败
        private static int _nextPeerLoopLogAt;        // 客户端会话线程异常收尾
        private static int _nextClosePeerLogAt;       // 收尾时关流 / 关连接

        /// <summary>
        /// 尽力而为路径的**限频闸门**（**线程安全**）：返回 <c>true</c> = 这一条该报。
        /// AcceptLoop / PeerLoop 跑在后台线程、<see cref="Stop"/> 跑在主线程，靠
        /// <c>Interlocked.CompareExchange</c> 抢"上报资格"共用闸门。
        /// <para>刻意**不用**引擎 <c>LogThrottle</c>：它的语义约束写明"非线程安全：主线程使用"，
        /// 而本类大量调用点就在后台线程里（见类注释的线程模型）。</para>
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

        public static bool IsRunning { get { return _running; } }
        public static int Port { get { return _port; } }
        public static long Connections { get { return Interlocked.Read(ref _connections); } }
        public static long Joins { get { return Interlocked.Read(ref _joins); } }
        public static long Inputs { get { return Interlocked.Read(ref _inputs); } }
        public static long Snapshots { get { return Interlocked.Read(ref _snapshots); } }
        public static long Malformed { get { return Interlocked.Read(ref _malformed); } }
        public static long Rejected { get { return Interlocked.Read(ref _rejected); } }

        /// <summary>字段缺失/格式不对而被丢掉的 <see cref="InputMagic"/> 条数（键名必须与 <c>CsInputState</c> 逐字一致）。</summary>
        public static long InputBadFields { get { return Interlocked.Read(ref _inputBadFields); } }

        /// <summary>已经转交给模拟的远端输入次数（&gt;0 且 actor 的坐标在动 = 这条链真的通了）。</summary>
        public static long AppliedInputs { get { return Interlocked.Read(ref _appliedInputs); } }

        /// <summary>已握手完成的客户端数。</summary>
        public static int WelcomedCount
        {
            get
            {
                var n = 0;
                lock (PeersLock)
                {
                    for (var i = 0; i < Peers.Count; i++) if (Peers[i].Welcomed) n++;
                }
                return n;
            }
        }

        /// <summary>当前连着的 socket 数（含未握手）。</summary>
        public static int ConnectionCount
        {
            get { lock (PeersLock) return Peers.Count; }
        }

        /// <summary>一行状态（日志 / 探针 / 面板共用，避免多处文案各写一套）。</summary>
        public static string Describe()
        {
            if (!_running) return "对局网关未启动";
            return "对局网关 port=" + _port + " 连接=" + Connections + " 已握手=" + WelcomedCount +
                   "/" + ConnectionCount + " 收到 JOIN=" + Joins + " 收到 INPUT=" + Inputs +
                   " 已转交模拟=" + AppliedInputs + " INPUT字段非法=" + InputBadFields +
                   " 推出快照=" + Snapshots + " 非法行=" + Malformed + " 拒绝=" + Rejected;
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
                Game.Logger?.Error(Tag, error);
                return false;
            }

            if (_running)
            {
                _hostName = string.IsNullOrEmpty(hostName) ? CsLanHost.DefaultName : hostName;
                _maxPlayers = maxPlayers < 0 ? 0 : maxPlayers;
                Game.Logger?.Info(Tag, "对局网关已在跑，仅更新参数：" + Describe());
                return true;
            }

            // **静默共存是本类最危险的失败模式**（实测踩到过）：
            //    Windows 上 SO_REUSEADDR 会让"端口已被别人监听"时我们的 bind **照样成功**，
            //    但内核把新连接交给**绑定更具体**的那一方 —— 实测本机 `127.0.0.1:8002` 被另一个进程
            //    占着（netstat：LISTENING + UDP 8003，同一个 PID），我们绑 `0.0.0.0:8002` 成功、
            //    客户端 TCP **连得上**，可 accept 永不返回、`Connections` 恒为 0
            //    —— 从外面看就像"网关起来了但没人能进来"，极难查。
            //    ⇒ 起之前先探一次：**能连上就说明有人在听** ⇒ 直接拒绝启动，不静默共存。
            string who;
            if (ProbeForeignListener(port, out who))
            {
                error = "端口 " + port + " 上已经有别的进程在监听（探测到能连上" + who + "）" +
                        "⇒ 拒绝启动，避免 SO_REUSEADDR 静默共存（连进来的会是对面，我们收不到）。" +
                        "换一个空闲端口（CsLanHost.Start 的 gatewayPort 参数）再试。";
                Game.Logger?.Error(Tag, error);
                return false;
            }

            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                // ReuseAddress：否则"刚 Stop 又 Start"（或上一个实例的后台线程还没退干净）会 AddressAlreadyInUse。
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _listener = listener;
            }
            catch (Exception ex)
            {
                error = "绑定 TCP " + port + " 失败：" + ex.GetType().Name + ": " + ex.Message +
                        "（同一台机器上只能有一个主机；另一个实例在跑？）";
                Game.Logger?.Error(Tag, error);
                _listener = null;
                return false;
            }

            _port = port;
            _hostName = string.IsNullOrEmpty(hostName) ? CsLanHost.DefaultName : hostName;
            _maxPlayers = maxPlayers < 0 ? 0 : maxPlayers;
            _accum = 0f;
            Interlocked.Exchange(ref _connections, 0);
            Interlocked.Exchange(ref _joins, 0);
            Interlocked.Exchange(ref _inputs, 0);
            Interlocked.Exchange(ref _snapshots, 0);
            Interlocked.Exchange(ref _malformed, 0);
            Interlocked.Exchange(ref _rejected, 0);
            Interlocked.Exchange(ref _inputBadFields, 0);
            Interlocked.Exchange(ref _appliedInputs, 0);
            lock (InputLock) PendingInputs.Clear();

            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "CsLanGatewayAccept";
            _acceptThread.Start();

            Game.Logger?.Info(Tag,
                "对局网关已启动：TCP " + port + "（" + JoinMagic + " → " + WelcomeMagic +
                "，此后每 " + SnapInterval.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                "s 一条 " + SnapMagic + "）");
            return true;
        }

        /// <summary>关网关（幂等）。后台线程在 ≤ 一个轮询周期内退出，不 Join（可能从主线程调用）。</summary>
        public static void Stop()
        {
            var wasRunning = _running;
            _running = false;

            // 尽力而为：listener 已关 / 已被回收 ⇒ 收尾照旧（语义不变：不抛、下面照旧置 null）。
            try { if (_listener != null) _listener.Stop(); }
            catch (Exception ex)
            {
                if (ShouldLogBestEffort(ref _nextListenerStopLogAt))
                {
                    Game.Logger.Warn(Tag, "停止监听时 listener.Stop() 失败（尽力而为路径，不影响开局）：" +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
            _listener = null;
            _acceptThread = null;

            List<Peer> peers;
            lock (PeersLock)
            {
                peers = new List<Peer>(Peers);
                Peers.Clear();
            }
            for (var i = 0; i < peers.Count; i++) ClosePeer(peers[i]);

            if (wasRunning)
            {
                Game.Logger?.Info(Tag, "对局网关已停止：" + Describe());
            }
        }

        // ---------------------------------------------------------------- 主线程：每帧泵

        /// <summary>
        /// 主线程每帧调一次。<paramref name="dt"/> 累计到 <see cref="SnapInterval"/> 才真正出快照。
        /// <para>主线程只做"读模拟 + 拼字符串 + 入队"，不碰 socket ⇒ 不会被慢客户端卡住渲染。</para>
        /// </summary>
        public static void Pump(ICsMatch match, float dt)
        {
            if (!_running) return;

            _accum += dt;
            if (_accum < SnapInterval) return;
            _accum = 0f;

            if (match == null || !match.IsRunning) return;

            // 出快照之前先把客户端的输入交给模拟：同一帧里"远端输入已生效"与"快照已反映它"对齐，
            // 客户端看到的位移不会凭空晚一帧。
            ApplyRemoteInputs(match);

            var line = BuildSnapshot(match);
            Interlocked.Increment(ref _snapshots);

            lock (PeersLock)
            {
                for (var i = 0; i < Peers.Count; i++)
                {
                    var p = Peers[i];
                    if (p.Welcomed && !p.Closed) Enqueue(p, line);
                }
            }
        }

        /// <summary>
        /// **主线程**：把客户端会话线程收到的 <see cref="InputMagic"/> 交给模拟（每帧最多一次，
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

            for (var i = 0; i < batch.Count; i++)
            {
                match.SetRemoteInput(batch[i].ActorId, batch[i].Input);
                Interlocked.Increment(ref _appliedInputs);
            }
        }

        /// <summary>测试入口：不依赖 <see cref="Pump"/> 的计时，立刻推一条快照（探针用）。</summary>
        public static string PushSnapshotForTest(ICsMatch match)
        {
            if (match == null) return null;
            var line = BuildSnapshot(match);
            Interlocked.Increment(ref _snapshots);
            lock (PeersLock)
            {
                for (var i = 0; i < Peers.Count; i++)
                {
                    var p = Peers[i];
                    if (p.Welcomed && !p.Closed) Enqueue(p, line);
                }
            }
            return line;
        }

        // ---------------------------------------------------------------- 收连接

        /// <summary>
        /// 探测 <paramref name="port"/> 上是否**已经有别的进程在监听**（= 本机回环能连上）。
        /// <para>为什么要这个：见 <see cref="Start"/> 里那段"静默共存"注释 —— 少了它，
        /// 端口被占时我们的 bind 会成功、却永远收不到连接，表现为"网关起来了但没人能进来"。</para>
        /// <para>超时 <see cref="ProbeTimeoutMs"/> ms：没人听时 Windows 立刻拒绝（ConnectionRefused），
        /// 所以这个探测是"毫秒级"的；只有防火墙丢包之类才会走到超时分支（按"没人听"处理）。</para>
        /// </summary>
        private static bool ProbeForeignListener(int port, out string who)
        {
            who = null;
            TcpClient probe = null;
            try
            {
                probe = new TcpClient();
                var ar = probe.BeginConnect(IPAddress.Loopback, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ProbeTimeoutMs)) return false;
                try { probe.EndConnect(ar); }
                catch (Exception) { return false; }          // 明确被拒（或握手失败）⇒ 没人听
                who = " 127.0.0.1:" + port;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                // 尽力而为：探测连接关不掉不影响结论（语义不变：返回值已在上面定好）。
                try { if (probe != null) probe.Close(); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextProbeCloseLogAt))
                    {
                        Game.Logger.Warn(Tag, "端口探测结束时关闭探测连接失败（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                var listener = _listener;
                if (listener == null) break;

                TcpClient tcp;
                try
                {
                    tcp = listener.AcceptTcpClient();
                }
                catch (SocketException ex)
                {
                    if (_running) Game.Logger?.Warn(Tag, "accept 失败：" + ex.SocketErrorCode);
                    break;                                  // listener 被 Stop() 关掉
                }
                catch (ObjectDisposedException)
                {
                    // 正常路径：listener 被 Stop() 关掉（AcceptTcpClient 在已 Dispose 的 listener 上抛）。
                    // 收尾留痕由 Stop() 自己那条 Info 承担（"对局网关已停止：…"）⇒ 这里刻意不另打，
                    // 否则每次正常 Stop() 都多一条与故障无关的噪音。
                    break;
                }
                catch (Exception ex)
                {
                    if (_running) Game.Logger?.Warn(Tag, "accept 异常：" + ex.GetType().Name + ": " + ex.Message);
                    break;
                }

                if (tcp == null) continue;

                if (ConnectionCount >= MaxClients)
                {
                    Interlocked.Increment(ref _rejected);
                    try
                    {
                        SendRaw(tcp, ByeMagic + "|{\"reason\":\"full\"}");
                        tcp.Close();
                    }
                    catch (Exception ex)
                    {
                        // 尽力而为：满员照样要把他挡在门外（语义不变：下面仍然 continue，连接已被丢弃）。
                        if (ShouldLogBestEffort(ref _nextRejectLogAt))
                        {
                            Game.Logger.Warn(Tag, "满员时回 " + ByeMagic + " / 关连接失败（尽力而为路径，不影响开局）：" +
                                ex.GetType().Name + ": " + ex.Message +
                                "（本机累计拒绝 " + Rejected + " 次）");
                        }
                    }
                    continue;
                }

                var peer = new Peer();
                peer.Tcp = tcp;
                peer.Stream = tcp.GetStream();
                try { peer.Endpoint = tcp.Client.RemoteEndPoint != null ? tcp.Client.RemoteEndPoint.ToString() : "-"; }
                catch (Exception ex)
                {
                    peer.Endpoint = "-";                        // ⛔ 语义不变：取不到地址就按 "-" 记
                    if (ShouldLogBestEffort(ref _nextEndpointLogAt))
                    {
                        Game.Logger.Warn(Tag, "取不到新连接的对端地址，本次按 \"-\" 记（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }

                lock (PeersLock) Peers.Add(peer);
                Interlocked.Increment(ref _connections);

                var th = new Thread(PeerLoop);
                th.IsBackground = true;
                th.Name = "CsLanGatewayPeer";
                peer.Thread = th;
                th.Start(peer);

                Game.Logger?.Info(Tag, "客户端连接：" + peer.Endpoint + "（等它发 " + JoinMagic + "）");
            }
        }

        private static void PeerLoop(object state)
        {
            var c = (Peer)state;
            var one = new byte[1];
            var line = new StringBuilder();

            try
            {
                while (!c.Closed && _running)
                {
                    var did = false;

                    // ---- 先把出队的东西写完（主线程只入队，真正的 Write 在这里）----
                    while (true)
                    {
                        string text = null;
                        lock (c.Lock) { if (c.Out.Count > 0) text = c.Out.Dequeue(); }
                        if (text == null) break;
                        var bytes = Encoding.UTF8.GetBytes(text + "\n");
                        c.Stream.Write(bytes, 0, bytes.Length);
                        c.Stream.Flush();
                        c.Sent++;
                        did = true;
                    }

                    // ---- 再读（逐字节收行；行以 \n 结束）----
                    if (c.Stream.DataAvailable)
                    {
                        var n = c.Stream.Read(one, 0, 1);
                        if (n <= 0) break;                       // 对端关了
                        var ch = (char)one[0];
                        c.Received++;
                        if (ch == '\n')
                        {
                            var s = line.ToString();
                            line.Length = 0;
                            if (s.Length > 0) HandleLine(c, s);
                        }
                        else if (ch != '\r')
                        {
                            line.Append(ch);
                            if (line.Length > MaxLineBytes)
                            {
                                c.BadLines++;
                                Interlocked.Increment(ref _malformed);
                                line.Length = 0;                 // ⛔ 不按超长行分配内存
                            }
                        }
                        did = true;
                    }

                    if (!did) Thread.Sleep(PollSleepMs);
                }
            }
            catch (Exception ex)
            {
                // 断线 / socket 被关：走 finally 收尾，不把异常抛到线程外（抛出去会改变行为）。
                // 但每个客户端线程只有这一处现场，且"某台机器一加入就断"最需要原因 ⇒ 限频留痕。
                if (ShouldLogBestEffort(ref _nextPeerLoopLogAt))
                {
                    Game.Logger.Warn(Tag, "客户端 " + c.Endpoint + " 的会话线程异常收尾（尽力而为路径，不影响开局）：" +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
            finally
            {
                ClosePeer(c);
            }
        }

        private static void HandleLine(Peer c, string line)
        {
            var bar = line.IndexOf('|');
            if (bar <= 0)
            {
                c.BadLines++;
                Interlocked.Increment(ref _malformed);
                return;
            }

            var magic = line.Substring(0, bar);
            var json = line.Substring(bar + 1);

            if (magic == JoinMagic)
            {
                c.Name = ReadString(json, "name", "-");
                c.Welcomed = true;
                Interlocked.Increment(ref _joins);
                Enqueue(c, BuildWelcome());
                Game.Logger?.Info(Tag,
                    "客户端加入：" + c.Endpoint + " name=\"" + c.Name + "\" ⇒ 已回 " + WelcomeMagic);
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

            c.BadLines++;
            Interlocked.Increment(ref _malformed);
        }

        // ---------------------------------------------------------------- 组报文

        private static string BuildWelcome()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder(256);
            sb.Append(WelcomeMagic).Append('|');
            sb.Append("{\"proto\":").Append(ProtocolVersion);
            sb.Append(",\"name\":\"").Append(Escape(_hostName)).Append('"');
            sb.Append(",\"host\":\"").Append(Escape(AdvertisedEndpoint())).Append('"');
            sb.Append(",\"maxPlayers\":").Append(_maxPlayers);
            sb.Append(",\"snapHz\":").Append((1f / SnapInterval).ToString("F1", inv));
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

        private static void Enqueue(Peer c, string line)
        {
            if (c == null || line == null || c.Closed) return;
            lock (c.Lock)
            {
                if (c.Out.Count > 512) return;      // 慢客户端：丢新帧而不是无限涨内存
                c.Out.Enqueue(line);
            }
        }

        private static void SendRaw(TcpClient tcp, string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            var s = tcp.GetStream();
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        private static void ClosePeer(Peer c)
        {
            if (c == null) return;
            var already = c.Closed;
            c.Closed = true;
            // 尽力而为：收尾关不掉也继续（语义不变：不抛，下面照旧摘 Peers 表 + 打收尾 Info）。
            try { if (c.Stream != null) c.Stream.Close(); }
            catch (Exception ex)
            {
                if (ShouldLogBestEffort(ref _nextClosePeerLogAt))
                {
                    Game.Logger.Warn(Tag, "收尾时关闭 " + c.Endpoint + " 的网络流失败（尽力而为路径，不影响开局）：" +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
            try { if (c.Tcp != null) c.Tcp.Close(); }
            catch (Exception ex)
            {
                if (ShouldLogBestEffort(ref _nextClosePeerLogAt))
                {
                    Game.Logger.Warn(Tag, "收尾时关闭 " + c.Endpoint + " 的连接失败（尽力而为路径，不影响开局）：" +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (!already)
            {
                lock (PeersLock) Peers.Remove(c);
                Game.Logger?.Info(Tag, "客户端断开：" + c.Endpoint + " name=\"" + c.Name +
                    "\" 收字节=" + c.Received + " 出报文=" + c.Sent + "（非法行 " + c.BadLines + "）");
            }
        }
    }
}
