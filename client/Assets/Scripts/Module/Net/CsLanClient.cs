using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;

namespace Cs16.Module.Net
{
    /// <summary>
    /// 局域网**对局客户端**（本项目新增，差异 #88「能开局」的**第②段**：把远端角色画出来）。
    ///
    /// <para><b>它在整条链里的位置</b>：主机侧（<see cref="CsLanHost"/> + <see cref="CsLanGateway"/>）
    /// 已经把「能被发现 → 能连上 → 能收到世界快照」做完了；但**"收到数据 ≠ 看得见人"** ——
    /// 握手、此后每收到一条 <c>CS16-LAN-SNAP/1</c> 就把它解析成一组
    /// <see cref="CsLanRemoteActor"/>（最新一帧挂在 <see cref="TryGetActors"/> 上），
    /// 由 <see cref="Cs16.Module.View.CsLanRemoteView"/> 画成场上的远端角色。</para>
    ///
    /// <para><b>线格式（不臆造；逐字同 <see cref="CsLanGateway"/>，两端必须同值）</b>：
    /// 一律 UTF-8、**一行一条报文、以 <c>\n</c> 结束**，形如 <c>&lt;MAGIC&gt;|&lt;json&gt;</c>：
    /// <list type="bullet">
    /// <item>客户端 → 主机：<c>CS16-LAN-JOIN/1|{{"name":"…","proto":1}}</c></item>
    /// <item>客户端 → 主机（每 <see cref="InputSendIntervalMs"/> ms）：<c>CS16-LAN-INPUT/1|{{"id":…,"Move":{{"x":…,"y":…}},"Jump":…, …}}</c>
    /// —— 键名逐字 = <c>CsInputState</c> 的成员名，<c>id</c> = **WELCOME 里主机分配给我的那具身体**
    /// （老主机没带 <c>actorId</c> 时才退回"快照里与我同名的那具"）。</item>
    /// <item>主机 → 客户端：<c>CS16-LAN-WELCOME/1|{{"proto":1,…,"actorId":…,"playerName":…,"team":…}}</c>
    /// —— <c>actorId</c> 是我在主机模拟里的身体，收到就开始按它发 INPUT。</item>
    /// <item>主机 → 客户端（每 0.1 s）：<c>CS16-LAN-SNAP/1|{{…"actors":[{{…}}]}}</c></item>
    /// <item>任一方可发：<c>CS16-LAN-BYE/1|{{…}}</c></item>
    /// </list>
    /// 单行上限 <see cref="MaxLineBytes"/>（与主机同值 4096；超了算非法行、丢弃，不按它分配内存）。
    /// 本类**会发** <c>CS16-LAN-INPUT/1</c>：每 <see cref="InputSendIntervalMs"/> ms 把
    /// <see cref="SetLocalInput"/> 喂进来的本地玩家输入发给主机（谁的身体 = 主机快照里与我同名的那个 actor）。</para>
    ///
    /// <para><b>线程模型（关键：主线程不碰 socket）</b>：
    /// <list type="number">
    /// <item><b>读线程（<see cref="_thread"/>，<c>IsBackground = true</c>）</b>：连接 → 发 JOIN →
    /// 逐字节收行 → 解析；**并且**是这一条链上唯一往 socket 写的地方（主线程只把
    /// <c>CS16-LAN-INPUT/1</c> 行塞进 <see cref="OutQueue"/>，由它取出发出去）。</item>
    /// <item><b>主线程</b>：每帧调 <see cref="Pump"/> —— 发现"线程已死但状态还写着在跑"时纠偏留痕、
    /// 按 <see cref="InputSendIntervalMs"/> 把本地输入入队、以及（见 <see cref="IsRunning"/>）供视图层判断
    /// 要不要画。主线程**一次 socket 都不碰**。</item>
    /// <item>为什么日志要绕一圈：读线程是后台线程，而 Unity 的 Console / 落盘 Logger 都该在主线程口径上写
    /// （与 <c>CsLanHost</c> 避开 <c>UnityEngine.Time</c> 同一类顾虑）⇒ 读线程把日志投给
    /// **引擎的后台→主线程派发器**（<c>Game.Dispatcher.Post</c>，由 <c>Game.Tick</c> 每帧 Flush），
    /// 不再自建第二套线程队列。</item>
    /// </list></para>
    ///
    /// <para><b>本类到哪一步为止</b>：
    /// 能连上 / 能握手 / 能持续消费快照 / 能把最新一帧交给视图层 / 能把本地输入发给主机
    /// （主机侧按 <c>id</c> 应用到模拟，见 <see cref="CsLanGateway.ApplyRemoteInputs"/>）。
    /// **未做**："远端角色的预测/插值"（快照直接落到位置上，见 <c>CsLanRemoteView</c>）。</para>
    /// </summary>
    public static class CsLanClient
    {
        private const string Tag = "LanClient";

        /// <summary>单行字节上限（**必须与主机 <see cref="CsLanGateway.MaxLineBytes"/> 同值**：超了对方也丢）。</summary>
        public const int MaxLineBytes = CsLanGateway.MaxLineBytes;

        /// <summary>连接主机的等待上限（毫秒）—— 主机刚起/ARP 未就绪时给一点余地，但不无限等。</summary>
        public const int ConnectTimeoutMs = 5000;

        /// <summary>
        /// 等 <c>CS16-LAN-WELCOME/1</c> 的上限（毫秒）：TCP 连上但主机不回握手（对端不是本协议的主机）
        /// ⇒ 判失败并收尾，界面不会永远停在 "Connecting..."。
        /// </summary>
        public const int JoinTimeoutMs = 10000;

        /// <summary>
        /// 握手完成后等**第一条快照**的上限（毫秒）：主机对局网关在跑、但主机没在比赛里
        /// （<see cref="CsLanGateway.Pump"/> 只在 <c>match.IsRunning</c> 时出快照）⇒ 判失败并给出原因。
        /// </summary>
        public const int FirstSnapshotTimeoutMs = 15000;

        /// <summary>读 socket 的轮询超时（毫秒）：有它才能"停下"（否则 <c>Read</c> 会一直阻塞）。</summary>
        private const int ReadTimeoutMs = 250;

        /// <summary>连续读异常多少次判"链路已断"（每次异常间隔 = <see cref="ReadTimeoutMs"/> ms ⇒ 40 次 ≈ 10 s）。</summary>
        private const int MaxConsecutiveReadErrors = 40;

        private static TcpClient _tcp;
        private static NetworkStream _stream;
        private static Thread _thread;
        private static volatile bool _running;
        private static volatile bool _joined;
        private static volatile bool _remoteClosed;
        private static volatile string _phase = "未启动";

        private static string _host = "-";
        private static int _port;
        private static string _endpoint = "-";
        private static string _playerName = "player";
        private static volatile string _lastError;
        private static volatile string _welcomePayload;

        private static long _welcomes;
        private static long _snapshots;
        private static long _malformed;
        private static long _inputsSent;

        // ---- 本地输入 → 主机（每 InputSendIntervalMs 一行 CS16-LAN-INPUT/1）----
        /// <summary>本帧的本地玩家输入（<see cref="SetLocalInput"/> 写，主线程自己读写）。</summary>
        private static CsInputState _localInput;

        /// <summary>有没有人喂过输入（没人喂 ⇒ 不发 INPUT，⛔ 不凭空发一条"站着不动"的意图）。</summary>
        private static bool _haveLocalInput;

        /// <summary>本机在**主机快照**里对应的 actor id；&lt;=0 = 还没认出来。</summary>
        private static volatile int _myActorId = -1;

        /// <summary>
        /// 主机在 <c>WELCOME</c> 里分配给我的 actorId；0 = 老主机没带这个字段（退回按玩家名识别）。
        /// <para>有它之后身份就是**权威的**：快照里同名的那具可能是别人的（两台机器都叫 Player 时就是主机玩家）。</para>
        /// </summary>
        private static volatile int _assignedActorId;

        /// <summary>主机给这具身体的实际名字（重名时会加 <c>#2</c> 后缀）；只用于日志。</summary>
        private static volatile string _assignedPlayerName;

        /// <summary>
        /// 主机把我放在哪个阵营：WELCOME 的 <c>team</c> 给初值，此后每条快照里自己那具的 team 覆盖它
        /// （半场换边后本地要跟着换，否则 HUD 队伍色 / 雷达一直是旧的）。
        /// </summary>
        private static volatile int _selfTeamRaw = (int)CsTeam.Spectator;

        /// <summary>"快照里找不到与我同名的 actor"已留痕（只报一次，⛔ 不刷屏）。</summary>
        private static volatile bool _myActorMissingLogged;

        /// <summary>上次发 INPUT 的时刻（<see cref="Environment.TickCount"/>，毫秒）。</summary>
        private static int _lastInputSentAt;

        /// <summary>本次 <see cref="Start"/> 的时刻（等 WELCOME 的计时起点，毫秒）。</summary>
        private static int _startedAt;

        /// <summary>收到 WELCOME 的时刻（等第一条快照的计时起点，毫秒）；0 = 还没握手。</summary>
        private static int _joinedAt;

        /// <summary>发 INPUT 的间隔（毫秒）⇒ 20 Hz；与主机 10 Hz 的快照频率错开，主机每次取到的是最新意图。</summary>
        public const int InputSendIntervalMs = 50;

        /// <summary>待发的 INPUT 行（主线程入队、读线程唯一出队并写 socket）。</summary>
        private static readonly Queue<string> OutQueue = new Queue<string>();
        private static readonly object OutLock = new object();

        /// <summary>待发队列上限：满了丢**最旧**的（输入是"最新意图"，迟到的旧意图没有价值）。</summary>
        private const int OutQueueMax = 64;

        /// <summary>最新一帧的远端角色（读线程写、主线程读 ⇒ 一切访问都在 <see cref="LatestLock"/> 下）。</summary>
        private static CsLanRemoteActor[] _latest = new CsLanRemoteActor[0];

        /// <summary>最新一帧快照里的回合状态（与 <see cref="_latest"/> 同锁）。</summary>
        private static CsLanServerState _serverState;

        /// <summary>最后一条快照的到达时刻（<see cref="Environment.TickCount"/>，毫秒）；0 = 还没收到。</summary>
        private static volatile int _lastSnapshotAt;

        /// <summary>
        /// 快照断流的判定阈值（毫秒）：超过这么久没有新快照 ⇒ <see cref="LinkStale"/> 为 true，
        /// HUD 标"连接中断"，不静默继续跑旧比分。
        /// </summary>
        public const int LinkTimeoutMs = 2000;

        private static readonly object LatestLock = new object();
        private static readonly object StreamLock = new object();

        /// <summary>"派发器不可用期间丢掉的日志条数"（见 <see cref="PostLog"/>）：主线程下一次
        /// <see cref="Pump"/> 报出来 —— 不静默丢日志。</summary>
        private static int _droppedLogs;

        // ---------------------------------------------------------------- 尽力而为路径的限频留痕
        //   自检脚本会把本区间整段剔除后再比对 ⇒
        //   本区间内只许放"闸门本身"，任何控制流改动都必须挪到区间外，否则自检会失去意义。

        /// <summary>
        /// <para>每处失败最多每 <see cref="BestEffortLogIntervalMs"/> ms 报一条（读线程随时可能被
        /// 同网段无关流量打，不许每包 / 每帧刷屏）；判定走 <see cref="ShouldLogBestEffort"/>。</para>
        ///
        /// <para><b>为什么直呼 <c>Game.Logger.Warn</c> 而不走上面的 <see cref="PostLog"/> 桥</b>：
        /// 桥是给**常态**日志（握手 / 快照 / 断线）用的，它的价值是"按帧与主线程日志对齐"；
        /// 而这里要的是"**万一失败必须留下痕迹**"，走桥会多一层"派发器为 null 就丢"的依赖
        /// （丢的那条只在下一帧才被报出来）。引擎侧两条都满足：<c>Game.Logger</c> **永不为 null**
        /// （<c>Runtime/Core/Game.cs:143-149</c>）且落盘 <c>Logger</c> 走 <c>ConcurrentQueue</c> +
        /// <c>ConsoleLogger</c> 自带 try/catch（<c>Logger.cs:120</c> / <c>ConsoleLogger.cs:91-124</c>）
        /// ⇒ **后台线程直呼是安全的**（<c>LogThrottle</c> 则明确"主线程使用、不加锁"，本类不用它）。</para>
        /// </summary>
        private const int BestEffortLogIntervalMs = 5000;

        private static int _nextStopCloseLogAt;       // Stop()：关网络流 / 关主机连接（主线程）
        private static int _nextJoinLogAt;            // Stop()：等读线程退出（主线程）
        private static int _nextConnectCloseLogAt;    // ReadLoop：连接超时后关连接（读线程）
        private static int _nextGetStreamLogAt;       // ReadLoop：取网络流失败后关连接（读线程）
        private static int _nextDisposedLogAt;        // ReadLoop：socket 被本地 Stop() 关掉（读线程）
        private static int _nextFinallyCloseLogAt;    // ReadLoop finally：关网络流 / 关连接（读线程）

        /// <summary>
        /// 尽力而为路径的**限频闸门**（**线程安全**）：返回 <c>true</c> = 这一条该报。
        /// <c>Interlocked.CompareExchange</c> 抢"上报资格"，所以读线程与主线程可以共用同一个闸门。
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

        // ---------------------------------------------------------------- 只读出口

        /// <summary>客户端是否在跑（含"正在连接"阶段）。</summary>
        public static bool IsRunning { get { return _running; } }

        /// <summary>是否已收到主机的 <c>CS16-LAN-WELCOME/1</c>（握手完成）。</summary>
        public static bool Joined { get { return _joined; } }

        /// <summary>主机是否已发 <c>CS16-LAN-BYE/1</c>（对端主动断开）。</summary>
        public static bool RemoteClosed { get { return _remoteClosed; } }

        /// <summary>连的是哪台主机（<c>host:port</c>）；未启动时为 <c>-</c>。</summary>
        public static string Endpoint { get { return _endpoint; } }

        /// <summary>最近一次失败原因（<see cref="Start"/> 的同步失败，或读线程的异步失败）；无失败为 null。</summary>
        public static string LastError { get { return _lastError; } }

        /// <summary>收到的 WELCOME 条数（判据用：&gt;0 = 真的握上手了）。</summary>
        public static long Welcomes { get { return Interlocked.Read(ref _welcomes); } }

        /// <summary>收到的快照条数（判据用：&gt;=N = 快照是**连续**的，而不只是"回了一包就走"）。</summary>
        public static long Snapshots { get { return Interlocked.Read(ref _snapshots); } }

        /// <summary>丢弃的非法行数（不是本协议 / 超长 / JSON 解析失败）。</summary>
        public static long Malformed { get { return Interlocked.Read(ref _malformed); } }

        /// <summary>已发出的 <see cref="CsLanGateway.InputMagic"/> 条数。</summary>
        public static long InputsSent { get { return Interlocked.Read(ref _inputsSent); } }

        /// <summary>本机在主机快照里认出的自己（&lt;=0 = 还没认出来，此时不发 INPUT）。</summary>
        public static int MyActorId { get { return _myActorId; } }

        /// <summary>
        /// 主机把我放在哪个阵营（WELCOME 的 <c>team</c>，之后跟快照里自己那具走）。
        /// <see cref="CsTeam.Spectator"/> = 主机没给 / 还没认出来。
        /// </summary>
        public static CsTeam AssignedTeam
        {
            get
            {
                var v = _selfTeamRaw;
                return v == (int)CsTeam.T || v == (int)CsTeam.CT ? (CsTeam)v : CsTeam.Spectator;
            }
        }

        /// <summary>
        /// 本机是不是"跟着主机状态跑"的一方（已握手 = 客机）。
        /// 进图前用它决定 <see cref="Cs16.Core.CsMatchConfig.ClientMode"/>。
        /// </summary>
        public static bool ServerDriven { get { return _running && _joined; } }

        /// <summary>距最后一条快照的毫秒数（没收到过 / 未握手 = -1）。</summary>
        public static int MillisecondsSinceLastSnapshot
        {
            get
            {
                if (!_joined) return -1;
                var last = _lastSnapshotAt;
                if (last == 0) return -1;
                return unchecked(Environment.TickCount - last);
            }
        }

        /// <summary>距最后一条快照的秒数（没收到过 = -1）。</summary>
        public static float SecondsSinceLastSnapshot
        {
            get
            {
                var ms = MillisecondsSinceLastSnapshot;
                return ms < 0 ? -1f : ms / 1000f;
            }
        }

        /// <summary>
        /// 客机链路是否已经断流（&gt; <see cref="LinkTimeoutMs"/> 没有新快照）。
        /// 握手后还没收到第一条快照也按断流算 —— 界面不能一直显示"已连接"。
        /// </summary>
        public static bool LinkStale
        {
            get
            {
                if (!_running || !_joined) return false;
                var ms = MillisecondsSinceLastSnapshot;
                return ms < 0 || ms > LinkTimeoutMs;
            }
        }

        /// <summary>
        /// 主机快照里的回合状态（<c>round / scoreT / scoreCT / phase</c>）。
        /// 返回 false = 还没收到过快照（此时不要拿它覆盖本机状态）。
        /// </summary>
        public static bool TryGetServerState(out CsLanServerState state)
        {
            lock (LatestLock)
            {
                state = _serverState;
            }
            return Interlocked.Read(ref _snapshots) > 0;
        }

        /// <summary>
        /// 当前连的是不是**本机自己广播出去的网关**（= 收到的快照就是本机模拟的副本）。
        ///
        /// <para>这种连接下快照里每一具都已经被本机的角色视图画过一遍，远端视图再画就是"每人两个"
        /// —— 自己那份第三人称还看得见，表现成"第一人称里看到自己"。判据用端点：主机广播的
        /// 就是本机网卡 / 回环 + 本机网关端口（<see cref="CsLanHost.IsSelfEndpoint"/>）。</para>
        /// </summary>
        public static bool IsEndpointSelf
        {
            get { return _running && CsLanHost.IsSelfEndpoint(_host, _port); }
        }

        /// <summary>最新一帧里的远端角色数（还没收到任何快照时为 0）。</summary>
        public static int RemoteActorCount
        {
            get { lock (LatestLock) return _latest.Length; }
        }

        /// <summary>一行状态（日志 / 面板状态行 / 探针共用，避免三处文案各写一套）。</summary>
        public static string Describe()
        {
            if (!_running)
            {
                return "局域网客户端未启动" + (string.IsNullOrEmpty(_lastError) ? "" : "（上次失败：" + _lastError + "）");
            }
            var age = SecondsSinceLastSnapshot;
            return "局域网客户端 endpoint=" + _endpoint + " 状态=" + _phase +
                   " 收到WELCOME=" + Welcomes + " 快照=" + Snapshots + " 非法行=" + Malformed +
                   " 远端角色=" + RemoteActorCount + " 我的actor=" + (_myActorId > 0 ? _myActorId.ToString() : "-") +
                   " 已发INPUT=" + InputsSent +
                   " 快照龄=" + (age < 0f ? "-" : age.ToString("F2", CultureInfo.InvariantCulture) + "s") +
                   " 断流=" + (LinkStale ? "是" : "否");
        }

        // ---------------------------------------------------------------- 启停

        /// <summary>
        /// 连上一台主机的对局网关（<paramref name="hostEndpoint"/> 形如 <c>192.168.1.7:8002</c>；
        /// 直接喂 <c>LanHostInfo.Address</c> 即可）。
        ///
        /// <para><b>返回值只管"起没起"</b>：本方法是**非阻塞**的 —— 真正的连接/握手在后台读线程上发生，
        /// 成功与否看 <see cref="Joined"/> / <see cref="LastError"/> / <see cref="Describe"/>。
        /// <paramref name="error"/> 只覆盖**同步**就能发现的失败（地址不合法 / 线程起不来）。</para>
        ///
        /// <para>幂等：已在跑且连的是同一台主机 ⇒ 只记一条 Info 返回 true；连的是另一台 ⇒
        /// 先 <see cref="Stop"/> 再重连（不允许两个客户端并存，否则视图层会出现两套远端角色）。</para>
        /// </summary>
        public static bool Start(string hostEndpoint, string playerName, out string error)
        {
            error = null;

            if (!TryParseEndpoint(hostEndpoint, out var host, out var port))
            {
                error = "主机地址非法：\"" + (hostEndpoint ?? "<null>") + "\"（要求 host:port，端口 1~65535）";
                _lastError = error;
                Game.Logger.Error(Tag, error);
                return false;
            }

            // 连回自己 = 两个客户端抢同一具身体（快照里"与我同名"的是本机玩家自己）⇒ 直接拒绝。
            if (CsLanHost.IsSelfEndpoint(host, port))
            {
                error = "拒绝连回本机自己：" + host + ":" + port.ToString(CultureInfo.InvariantCulture) +
                        " 就是本机 CsLanHost 正在广播的网关口（本机既当主机又当客机会看到自己的角色）";
                _lastError = error;
                Game.Logger.Error(Tag, error);
                return false;
            }

            if (_running)
            {
                if (host == _host && port == _port)
                {
                    Game.Logger.Info(Tag, "局域网客户端已在连同一台主机，忽略重复请求：" + Describe());
                    return true;
                }
                Game.Logger.Warn(Tag, "局域网客户端已连着 " + _endpoint + "，改为重连 " + hostEndpoint);
                Stop();
            }

            _host = host;
            _port = port;
            _endpoint = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            _playerName = string.IsNullOrWhiteSpace(playerName) ? "player" : playerName.Trim();
            _lastError = null;
            _welcomePayload = null;
            _joined = false;
            _remoteClosed = false;
            _phase = "连接中";
            _myActorId = -1;
            _assignedActorId = 0;
            _assignedPlayerName = null;
            _selfTeamRaw = (int)CsTeam.Spectator;
            _myActorMissingLogged = false;
            _lastInputSentAt = 0;
            _lastSnapshotAt = 0;
            _startedAt = Environment.TickCount;
            _joinedAt = 0;
            Interlocked.Exchange(ref _welcomes, 0);
            Interlocked.Exchange(ref _snapshots, 0);
            Interlocked.Exchange(ref _malformed, 0);
            Interlocked.Exchange(ref _inputsSent, 0);
            lock (OutLock) OutQueue.Clear();
            lock (LatestLock)
            {
                _latest = new CsLanRemoteActor[0];
                _serverState = default(CsLanServerState);
            }

            _running = true;
            try
            {
                var th = new Thread(ReadLoop);
                th.IsBackground = true;                 // 随进程退出，⛔ 不拦编辑器关闭
                th.Name = "CsLanClientRead";
                _thread = th;
                th.Start();
            }
            catch (Exception ex)
            {
                _running = false;
                _thread = null;
                _phase = "未启动";
                error = "启动读线程失败：" + ex.GetType().Name + ": " + ex.Message;
                _lastError = error;
                Game.Logger.Error(Tag, error);
                return false;
            }

            Game.Logger.Info(Tag,
                "局域网客户端启动：连 " + _endpoint + " 玩家名=\"" + _playerName + "\"（" +
                CsLanGateway.JoinMagic + " → " + CsLanGateway.WelcomeMagic + " → 持续收 " +
                CsLanGateway.SnapMagic + "；连接在后台线程，结果见 Joined / LastError）");
            return true;
        }

        /// <summary>
        /// 停下（幂等）。关 socket 让读线程立刻从阻塞里出来，并**有界地**等它退出
        /// （最多 <see cref="ThreadJoinMs"/> ms；不无限 Join —— 本方法可能从主线程调用）。
        /// </summary>
        public static void Stop()
        {
            var wasRunning = _running;
            _running = false;
            var th = _thread;
            _thread = null;

            lock (StreamLock)
            {
                // 尽力而为：关不掉也继续（不改语义 —— 不抛、不提前返回）。但"继续"不等于"无痕" ⇒ 限频留痕。
                try { if (_stream != null) _stream.Close(); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextStopCloseLogAt))
                    {
                        Game.Logger.Warn(Tag, "停止时关闭网络流失败（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                try { if (_tcp != null) _tcp.Close(); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextStopCloseLogAt))
                    {
                        Game.Logger.Warn(Tag, "停止时关闭主机连接失败（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                _stream = null;
                _tcp = null;
            }

            if (th != null && th.IsAlive)
            {
                // 尽力而为：Join 失败（线程已退 / 被中断）就当它退了（不改语义，后面照旧判 th.IsAlive）。
                try { th.Join(ThreadJoinMs); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextJoinLogAt))
                    {
                        Game.Logger.Warn(Tag, "等读线程退出时 Join 失败（尽力而为路径，随进程收尾）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                if (th.IsAlive)
                {
                    Game.Logger.Warn(Tag, "读线程在 " + ThreadJoinMs + " ms 内没退出（IsBackground=true，随进程收尾）");
                }
            }

            _phase = "未启动";
            _myActorId = -1;
            _assignedActorId = 0;
            _assignedPlayerName = null;
            _selfTeamRaw = (int)CsTeam.Spectator;
            _lastSnapshotAt = 0;
            lock (LatestLock) { _serverState = default(CsLanServerState); }
            lock (OutLock) OutQueue.Clear();
            ReportDroppedLogs();
            if (wasRunning)
            {
                Game.Logger.Info(Tag, "局域网客户端已停止：" + Describe());
            }
        }

        /// <summary>等读线程退出的上限（毫秒）。</summary>
        private const int ThreadJoinMs = 400;

        // ---------------------------------------------------------------- 主线程：每帧泵

        /// <summary>
        /// 主线程每帧调一次。
        /// <para>它**不解析任何报文、更不碰 socket**（那是读线程的活）：只
        /// ① 把读线程攒下的日志吐到主线程日志口径上；② 发现"线程已经死了但状态还写着在跑"时纠偏 + 留痕
        /// （否则界面表现只是"远端角色不动了"，没有任何线索）。未启动时是一次 bool 判断。</para>
        /// </summary>
        public static void Pump()
        {
            if (!_running)
            {
                ReportDroppedLogs();          // 收尾：Stop() 之后可能还有最后几条
                return;
            }

            var th = _thread;
            if (th == null || !th.IsAlive)
            {
                // 非预期分支：读线程已经退出但没人把状态收回来 ⇒ 必须留痕（静默 = 最难查的一类）。
                _running = false;
                _joined = false;
                _phase = "读线程已退出";
                Game.Logger.Error(Tag,
                    "读线程已退出但客户端状态仍写着在跑 ⇒ 已纠偏为停止（远端角色会随之回收）。" +
                    "现场：" + Describe());
            }

            // 本地输入 → 主机（只拼字符串 + 入队；socket 写在读线程里）。
            SendLocalInput();

            ReportDroppedLogs();
        }

        // ---------------------------------------------------------------- 数据出口

        /// <summary>
        /// 把**最新一帧**的远端角色**追加**进 <paramref name="into"/>。
        ///
        /// <para><b>语义（调用方必须知道）</b>：本方法**不清空** <paramref name="into"/>，
        /// 只往里追加 —— 想"每帧拿到完整一帧"的调用方要自己先 <c>Clear()</c>
        /// （<c>CsLanRemoteView.SyncAll</c> 就是这么用的）。</para>
        ///
        /// <para>返回 <c>true</c> = 至少收到过一条快照（<paramref name="into"/> 里是那一帧的内容）；
        /// <c>false</c> = 还没有任何快照（此时 <paramref name="into"/> 一个元素都不加）。</para>
        /// </summary>
        public static bool TryGetActors(List<CsLanRemoteActor> into)
        {
            if (into == null) return false;
            CsLanRemoteActor[] frame;
            lock (LatestLock) { frame = _latest; }
            if (frame.Length == 0) return false;
            for (var i = 0; i < frame.Length; i++)
            {
                if (frame[i] != null) into.Add(frame[i]);
            }
            return true;
        }

        // ---------------------------------------------------------------- 本地输入 → 主机

        /// <summary>
        /// **主线程**：喂入本帧的本地玩家输入（Module/Player 每帧调一次，与它喂给
        /// <see cref="ICsMatch.SetLocalInput"/> 的是同一份意图）。
        ///
        /// <para>本方法**不碰 socket**、也不判有没有连上：它只把意图记下来；发不发、发给谁由
        /// <see cref="Pump"/> 决定。没在跑的时候调用它只是一次赋值（廉价）。</para>
        /// </summary>
        public static void SetLocalInput(CsInputState input)
        {
            _localInput = input;
            _haveLocalInput = true;
        }

        /// <summary>
        /// 一行 <see cref="CsLanGateway.InputMagic"/> 报文：键名**逐字** = <c>CsInputState</c> 的成员名
        /// （<c>Move</c> 是 <c>{"x":…,"y":…}</c>），外加 <c>id</c> = 这是谁的身体。
        /// </summary>
        private static string BuildInputLine(int actorId, in CsInputState i)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(192);
            sb.Append(CsLanGateway.InputMagic).Append('|');
            sb.Append("{\"id\":").Append(actorId);
            sb.Append(",\"Move\":{\"x\":").Append(i.Move.x.ToString("F3", inv));
            sb.Append(",\"y\":").Append(i.Move.y.ToString("F3", inv)).Append('}');
            sb.Append(",\"Jump\":").Append(i.Jump ? "true" : "false");
            sb.Append(",\"Crouch\":").Append(i.Crouch ? "true" : "false");
            sb.Append(",\"Walk\":").Append(i.Walk ? "true" : "false");
            sb.Append(",\"Fire\":").Append(i.Fire ? "true" : "false");
            sb.Append(",\"Zoom\":").Append(i.Zoom ? "true" : "false");
            sb.Append(",\"Attack2\":").Append(i.Attack2 ? "true" : "false");
            sb.Append(",\"Yaw\":").Append(i.Yaw.ToString("F1", inv));
            sb.Append(",\"Pitch\":").Append(i.Pitch.ToString("F1", inv));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>入队一行待发报文（满了丢最旧的；真正写 socket 的是读线程）。</summary>
        private static void EnqueueOut(string line)
        {
            if (line == null) return;
            lock (OutLock)
            {
                if (OutQueue.Count >= OutQueueMax) OutQueue.Dequeue();
                OutQueue.Enqueue(line);
            }
        }

        /// <summary>读线程：把队列里的报文写完（本类**唯一**写 socket 的地方）。</summary>
        private static void DrainOut(NetworkStream stream)
        {
            while (true)
            {
                string text;
                lock (OutLock)
                {
                    if (OutQueue.Count == 0) return;
                    text = OutQueue.Dequeue();
                }
                WriteLine(stream, text);
            }
        }

        /// <summary>
        /// 主线程：按 <see cref="InputSendIntervalMs"/> 把本地输入发给主机。
        ///
        /// <para>三道闸门：已握手 / 有人喂过输入 / 已在主机快照里认出自己（<see cref="_myActorId"/>）。
        /// 任一没满足都不发 —— 不知道"这具身体是谁"时乱编一个 id 只会落到别人的 actor 上。</para>
        /// </summary>
        private static void SendLocalInput()
        {
            if (!_joined || !_haveLocalInput || _myActorId <= 0) return;

            var now = Environment.TickCount;
            if (unchecked(now - _lastInputSentAt) < InputSendIntervalMs) return;
            _lastInputSentAt = now;

            EnqueueOut(BuildInputLine(_myActorId, _localInput));
            Interlocked.Increment(ref _inputsSent);
        }

        // ---------------------------------------------------------------- 读线程

        private static void ReadLoop()
        {
            try
            {
                var tcp = new TcpClient();
                tcp.NoDelay = true;                       // 小报文（一行一条）不容忍 Nagle 攒包

                var ar = tcp.BeginConnect(_host, _port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    Fail("连接 " + _endpoint + " 超时（" + ConnectTimeoutMs + " ms）——主机没起？端口被占？");
                    // 尽力而为：连接都没成，关掉它只是收尾（语义不变 —— 下面仍然 return）。
                    try { tcp.Close(); }
                    catch (Exception ex)
                    {
                        if (ShouldLogBestEffort(ref _nextConnectCloseLogAt))
                        {
                            Game.Logger.Warn(Tag, "连接超时后关闭连接失败（尽力而为路径，不影响开局）：" +
                                ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    return;
                }
                tcp.EndConnect(ar);                       // 失败会抛到这里，由下面的 catch 统一处理

                NetworkStream stream;
                try { stream = tcp.GetStream(); stream.ReadTimeout = ReadTimeoutMs; }
                catch (Exception ex)
                {
                    Fail("取网络流失败：" + ex.GetType().Name + ": " + ex.Message);
                    // 尽力而为：同上，关掉它只是收尾（语义不变 —— 下面仍然 return）。
                    try { tcp.Close(); }
                    catch (Exception closeEx)
                    {
                        if (ShouldLogBestEffort(ref _nextGetStreamLogAt))
                        {
                            Game.Logger.Warn(Tag, "取网络流失败后关闭连接失败（尽力而为路径，不影响开局）：" +
                                closeEx.GetType().Name + ": " + closeEx.Message);
                        }
                    }
                    return;
                }

                lock (StreamLock) { _tcp = tcp; _stream = stream; }
                _phase = "已连接，等 WELCOME";
                PostLog(false, "已连上主机 " + _endpoint + "，发 " + CsLanGateway.JoinMagic);

                // ---- 握手（发 JOIN）。只在这里写 socket，且只写这一次 ----
                var join = CsLanGateway.JoinMagic + "|{\"name\":\"" + JsonEscape(_playerName) +
                           "\",\"proto\":" + CsLanGateway.ProtocolVersion + "}";
                WriteLine(stream, join);

                // ---- 逐行收 ----
                var buf = new byte[1024];
                var line = new StringBuilder(256);
                var consecutiveIoErrors = 0;
                while (_running)
                {
                    // 先把手上的输入发出去：主线程只入队，写 socket 的只有这一个线程。
                    DrainOut(stream);

                    // 两个有界等待：连上却迟迟不握手 / 握了手却一条快照都没有 ⇒ 判失败并收尾，
                    // 否则界面会永远停在 "Connecting..."，而日志里只有"已连上"看不出卡在哪。
                    var nowMs = Environment.TickCount;
                    if (!_joined && unchecked(nowMs - _startedAt) > JoinTimeoutMs)
                    {
                        Fail("连上主机 " + _endpoint + " 后 " + JoinTimeoutMs + " ms 内没收到 " +
                             CsLanGateway.WelcomeMagic + "（对方不是本协议的主机？对局网关已被占用？）⇒ 放弃本次加入");
                        break;
                    }

                    if (_joined && Interlocked.Read(ref _snapshots) == 0 &&
                        unchecked(nowMs - _joinedAt) > FirstSnapshotTimeoutMs)
                    {
                        Fail("已握手但 " + FirstSnapshotTimeoutMs + " ms 内没收到任何 " + CsLanGateway.SnapMagic +
                             "：主机没在比赛里（对局网关只在主机 match.IsRunning 时出快照）" +
                             "⇒ 先在主机上 New Game 开局，再从这台机器加入");
                        break;
                    }

                    int n;
                    try
                    {
                        n = stream.Read(buf, 0, buf.Length);
                        consecutiveIoErrors = 0;
                    }
                    catch (IOException)
                    {
                        // 读超时是**正常路径**（ReadTimeout=250ms，回去看 _running）；但若是真断线，
                        // 这个 catch 会一轮接一轮地来 ⇒ 限次退出，不做无限空转的忙等。
                        if (!_running) break;
                        if (++consecutiveIoErrors > MaxConsecutiveReadErrors)
                        {
                            Fail("连续 " + MaxConsecutiveReadErrors + " 次读异常 ⇒ 判定链路已断（最后一次 Read 抛 IOException）");
                            break;
                        }
                        continue;
                    }

                    if (n <= 0) break;                     // 对端关了

                    for (var i = 0; i < n; i++)
                    {
                        var ch = (char)buf[i];
                        if (ch == '\n')
                        {
                            var text = line.ToString();
                            line.Length = 0;
                            if (text.Length > 0) HandleLine(text);
                            continue;
                        }
                        if (ch == '\r') continue;
                        line.Append(ch);
                        if (line.Length > MaxLineBytes)
                        {
                            // 非预期分支：超长行 ⇒ 丢弃并计数（不按它分配内存、不静默）
                            Interlocked.Increment(ref _malformed);
                            line.Length = 0;
                        }
                    }

                    if (_remoteClosed) break;
                }

                if (_running && _remoteClosed)
                {
                    if (string.IsNullOrEmpty(_lastError))
                    {
                        _phase = "主机已断开";
                        PostLog(false, "主机发了 " + CsLanGateway.ByeMagic + "：对端主动断开，客户端收尾");
                    }
                    // 失败路径（协议不符 / 超时 / 链路断）已经由 Fail 写过 _phase 与原因，不覆盖。
                }
            }
            catch (SocketException ex)
            {
                if (_running) Fail("socket 异常：" + ex.SocketErrorCode + " —— " + ex.Message);
            }
            catch (ObjectDisposedException ex)
            {
                // Stop() 关掉了 socket：正常路径，不报错（异常照旧被吞、照旧走 finally 收尾）。
                // 但"正常"不等于"无痕" —— 限频留一条，否则"客户端忽然不再收快照"没人知道是本地 Stop() 收的尾。
                if (ShouldLogBestEffort(ref _nextDisposedLogAt))
                {
                    Game.Logger.Warn(Tag, "读线程的 socket 被本地 Stop() 关掉（正常收尾路径，不影响开局）：" +
                        ex.GetType().Name + ": " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                if (_running) Fail("读线程异常退出：" + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                lock (StreamLock)
                {
                    // 尽力而为：finally 里的收尾失败不影响任何后续动作（语义不变：下面照旧置空 + 置 _running=false）。
                    try { if (_stream != null) _stream.Close(); }
                    catch (Exception ex)
                    {
                        if (ShouldLogBestEffort(ref _nextFinallyCloseLogAt))
                        {
                            Game.Logger.Warn(Tag, "读线程收尾时关闭网络流失败（尽力而为路径，不影响开局）：" +
                                ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    try { if (_tcp != null) _tcp.Close(); }
                    catch (Exception ex)
                    {
                        if (ShouldLogBestEffort(ref _nextFinallyCloseLogAt))
                        {
                            Game.Logger.Warn(Tag, "读线程收尾时关闭主机连接失败（尽力而为路径，不影响开局）：" +
                                ex.GetType().Name + ": " + ex.Message);
                        }
                    }
                    _stream = null;
                    _tcp = null;
                }
                if (_running) _phase = "已断开";
                _running = false;                          // 幂等：Stop() 里再置一次无副作用
            }
        }

        /// <summary>处理一行报文（跑在**读线程**上；除日志入队外不做任何主线程相关的事）。</summary>
        private static void HandleLine(string lineText)
        {
            var bar = lineText.IndexOf('|');
            if (bar <= 0)
            {
                Interlocked.Increment(ref _malformed);
                return;
            }

            var magic = lineText.Substring(0, bar);
            var json = lineText.Substring(bar + 1);

            if (magic == CsLanGateway.WelcomeMagic)
            {
                var proto = ReadProto(json);
                if (proto != 0 && proto != CsLanGateway.ProtocolVersion)
                {
                    Fail("协议版本不一致：主机 proto=" + proto + "，本端 " + CsLanGateway.ProtocolVersion +
                         " ⇒ 不加入（两端必须是同一份线格式）");
                    _remoteClosed = true;      // 让读循环在本次收包后收尾
                    return;
                }

                Interlocked.Increment(ref _welcomes);
                _welcomePayload = json;
                _joined = true;
                _joinedAt = Environment.TickCount;
                _lastSnapshotAt = _joinedAt;      // 断流判定从握手算起（握手后一直没快照也算断流）
                _phase = "已握手，收快照中";

                // WELCOME 可选带 "actorId"：主机若直接告诉我"你的身体是哪个 id"就用它，
                // 不带（老主机）由 RecognizeSelf 按玩家名回落识别。
                var welcomeActor = ReadActorId(json);
                if (welcomeActor > 0)
                {
                    var previous = _assignedActorId;
                    _assignedActorId = welcomeActor;
                    _myActorId = welcomeActor;
                    _myActorMissingLogged = false;

                    var bodyName = ReadStringField(json, "playerName");
                    _assignedPlayerName = string.IsNullOrEmpty(bodyName) ? _playerName : bodyName;

                    if (previous > 0 && previous != welcomeActor)
                    {
                        PostLog(false, "主机换了一具身体给我：actor " + previous + " → " + welcomeActor +
                            "（整场重开后重建）；输入从新 id 继续发");
                    }
                    if (!string.Equals(_assignedPlayerName, _playerName, StringComparison.Ordinal))
                    {
                        PostLog(false, "主机给这具身体改名为 \"" + _assignedPlayerName + "\"（重名规避）：输入按 actor=" +
                            welcomeActor + " 发，不按名字发");
                    }

                    var welcomeTeam = ParseTeamName(ReadStringField(json, "team"));
                    if (welcomeTeam != CsTeam.Spectator) _selfTeamRaw = (int)welcomeTeam;
                    PostLog(false, "主机把我放在 " + (welcomeTeam == CsTeam.Spectator ? "未指定阵营" : welcomeTeam.ToString()) +
                        "（WELCOME team" + (welcomeTeam == CsTeam.Spectator ? " 缺失" : "") + "）");
                }

                PostLog(false, "收到 " + CsLanGateway.WelcomeMagic + "：" + Truncate(json, 200) +
                    (welcomeActor > 0 ? "（主机指定 actor=" + welcomeActor + "）" : "（未带 actorId，按玩家名识别自己）"));
                return;
            }

            if (magic == CsLanGateway.SnapMagic)
            {
                CsLanRemoteActor[] frame;
                CsLanServerState state;
                if (!TryParseSnapshot(json, out frame, out state))
                {
                    Interlocked.Increment(ref _malformed);
                    if (Malformed <= 3)
                    {
                        PostLog(true, "快照解析失败（第 " + Malformed + " 条非法行）：" + Truncate(json, 160));
                    }
                    return;
                }

                lock (LatestLock)
                {
                    _latest = frame;
                    _serverState = state;
                }
                _lastSnapshotAt = Environment.TickCount;
                RecognizeSelf(frame, Interlocked.Increment(ref _snapshots));
                UpdateSelfTeam(frame);
                return;
            }

            if (magic == CsLanGateway.ByeMagic)
            {
                var reason = ReadStringField(json, "reason");
                if (!_joined)
                {
                    // 未握手就被 BYE = 主机拒绝加入（满员走这条：无 actorId、回 {"reason":"full"}）。
                    _lastError = ByeReasonText(reason);
                    _phase = "被主机拒绝";
                    PostLog(true, "主机拒绝加入：" + _lastError + "（" + Truncate(json, 120) + "）");
                }
                else
                {
                    PostLog(false, "主机发了 " + CsLanGateway.ByeMagic + "（reason=\"" + Truncate(reason, 48) +
                        "\"）：对端主动断开，客户端收尾");
                }
                _remoteClosed = true;
                return;
            }

            // 非预期分支：不认识的 magic ⇒ 计数 + 前几条留痕（不静默）
            Interlocked.Increment(ref _malformed);
            if (Malformed <= 3)
            {
                PostLog(true, "收到不认识的报文 magic=\"" + Truncate(magic, 48) + "\"（第 " + Malformed + " 条非法行）");
            }
        }

        private static void Fail(string message)
        {
            _lastError = message;
            _phase = "失败";
            PostLog(true, message);
        }

        /// <summary>
        /// **读线程**：确定"我在这台主机上的身体"（<see cref="_myActorId"/>）。
        ///
        /// <para><b>两条来源</b>：① 主机在 <c>WELCOME</c> 里给了 <c>actorId</c> ⇒ **权威**，只用它
        /// （快照里同名的那具可能是别人的：两台机器都叫 Player 时就是主机玩家，认成自己会让主机拒收
        /// 我的输入）；② 老主机没给 <c>actorId</c> ⇒ 退回按 JOIN 时的玩家名识别。</para>
        ///
        /// <para>权威 id 暂时不在快照里（回合间隙 / 主机正在重建身体）时**只等**，不改用名字认 ——
        /// 名字认错一次就会把输入发给别人的身体，之后再也纠正不回来。</para>
        /// </summary>
        private static void RecognizeSelf(CsLanRemoteActor[] frame, long snapshotIndex)
        {
            var assigned = _assignedActorId;
            if (assigned > 0)
            {
                for (var i = 0; i < frame.Length; i++)
                {
                    if (frame[i] != null && frame[i].Id == assigned) return;
                }

                if (!_myActorMissingLogged && snapshotIndex >= 3)
                {
                    _myActorMissingLogged = true;
                    PostLog(true,
                        "主机分配给我的身体 actor=" + assigned + " 暂不在快照里（已收到 " + snapshotIndex +
                        " 帧、" + frame.Length + " 个角色）⇒ 本地输入暂停，等主机把它放回快照" +
                        "（不会改用同名识别：同名的那具可能是别人）");
                }
                return;
            }

            var mine = _myActorId;
            if (mine > 0)
            {
                for (var i = 0; i < frame.Length; i++)
                {
                    if (frame[i] != null && frame[i].Id == mine) return;   // 还在，身份不变
                }
                // 老主机（没给 actorId）：换局 / 重生后快照里换了 id ⇒ 重新认一次
                _myActorId = -1;
                _myActorMissingLogged = false;
            }

            for (var i = 0; i < frame.Length; i++)
            {
                var a = frame[i];
                if (a == null) continue;
                // 机器人不可能是"我这具身体"：主机上的 bot 名字是固定的 bot 名，同名只可能是巧合，
                // 认成自己就会把本机输入喂给一台 bot。
                if (a.Bot) continue;
                if (!string.Equals(a.Name, _playerName, StringComparison.Ordinal)) continue;

                _myActorId = a.Id;
                _myActorMissingLogged = false;
                PostLog(false,
                    "老主机没给 actorId：已在快照里按名字认出自己 playerName=\"" + _playerName + "\" ⇒ actor=" + a.Id +
                    "（此后每 " + InputSendIntervalMs + " ms 发一行 " + CsLanGateway.InputMagic + "）");
                return;
            }

            if (!_myActorMissingLogged && snapshotIndex >= 3)
            {
                _myActorMissingLogged = true;
                PostLog(true,
                    "主机快照里没有叫 \"" + _playerName + "\" 的 actor（已收到 " + snapshotIndex + " 帧、" +
                    frame.Length + " 个角色）⇒ 本地输入暂不发出：主机要先按这个名字建出我的身体");
            }
        }

        // ---------------------------------------------------------------- 解析

        /// <summary>
        /// 解析一行快照 JSON 的 <c>actors[]</c>。
        /// 走引擎自己的 <c>MiniJson</c>（<c>Runtime/Core/Json.cs</c>）—— 不手写 JSON 解析器：
        /// 手写的转义/数字/嵌套处理是"看起来能跑、边界一碰就错"的典型。
        /// </summary>
        private static bool TryParseSnapshot(string json, out CsLanRemoteActor[] frame, out CsLanServerState state)
        {
            frame = null;
            state = default(CsLanServerState);
            if (string.IsNullOrEmpty(json)) return false;

            object rootObj;
            try
            {
                rootObj = MiniJson.Parse(json);
            }
            catch (FormatException)
            {
                return false;
            }

            var root = rootObj as IDictionary<string, object>;
            if (root == null) return false;

            object arrObj;
            if (!root.TryGetValue("actors", out arrObj)) return false;
            var arr = arrObj as IList<object>;
            if (arr == null) return false;

            state.Round = (int)Num(root, "round", 0f);
            state.ScoreT = (int)Num(root, "scoreT", 0f);
            state.ScoreCT = (int)Num(root, "scoreCT", 0f);
            state.Phase = ParsePhase(Str(root, "phase"));

            var list = new List<CsLanRemoteActor>(arr.Count);
            for (var i = 0; i < arr.Count; i++)
            {
                var d = arr[i] as IDictionary<string, object>;
                if (d == null) continue;
                var a = new CsLanRemoteActor
                {
                    Id = (int)Num(d, "id", 0f),
                    Name = Str(d, "name"),
                    Team = Str(d, "team"),
                    X = Num(d, "x", 0f),
                    Y = Num(d, "y", 0f),
                    Z = Num(d, "z", 0f),
                    Yaw = Num(d, "yaw", 0f),
                    Hp = (int)Num(d, "hp", 0f),
                    Alive = Bool(d, "alive"),
                    Bot = Bool(d, "bot"),
                    Weapon = Str(d, "w"),
                };
                list.Add(a);
            }

            frame = list.ToArray();
            return true;
        }

        /// <summary>
        /// WELCOME 里可选的 <c>actorId</c>（主机直接指定"你的身体是哪个 id"）。
        /// 缺字段 / 解析失败返回 0 = 老主机没这个字段，交给 <see cref="RecognizeSelf"/> 按玩家名识别。
        /// </summary>
        private static int ReadActorId(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            try
            {
                var root = MiniJson.Parse(json) as IDictionary<string, object>;
                return root == null ? 0 : (int)Num(root, "actorId", 0f);
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        /// <summary>取 WELCOME 里的字符串字段（缺字段 / 解析失败返回空串）。</summary>
        private static string ReadStringField(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return string.Empty;
            try
            {
                var root = MiniJson.Parse(json) as IDictionary<string, object>;
                return root == null ? string.Empty : Str(root, key);
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 读线程：快照里自己那具的阵营 → <see cref="AssignedTeam"/>（半场换边后本地跟着换）。
        /// 自己不在这一帧里就保持上一次的值。
        /// </summary>
        private static void UpdateSelfTeam(CsLanRemoteActor[] frame)
        {
            var mine = _myActorId;
            if (mine <= 0 || frame == null) return;
            for (var i = 0; i < frame.Length; i++)
            {
                if (frame[i] == null || frame[i].Id != mine) continue;
                var team = ParseTeamName(frame[i].Team);
                if (team != CsTeam.Spectator) _selfTeamRaw = (int)team;
                return;
            }
        }

        /// <summary>线上的阵营串 → 枚举（认不出 = Spectator，不猜）。</summary>
        private static CsTeam ParseTeamName(string text)
        {
            if (text == "T") return CsTeam.T;
            if (text == "CT") return CsTeam.CT;
            return CsTeam.Spectator;
        }

        /// <summary>BYE 的 <c>reason</c> → 给玩家看的原因（状态行 / Toast 共用）。</summary>
        private static string ByeReasonText(string reason)
        {
            if (reason == "full") return "主机已满员（未分配 actorId，加入被拒绝）——请等有人退出后重试";
            if (string.IsNullOrEmpty(reason)) return "主机拒绝了本次加入（未说明原因）";
            return "主机拒绝了本次加入（reason=" + reason + "）";
        }

        /// <summary>快照 <c>phase</c> 串 → 枚举（线格式 = <see cref="CsRoundPhase"/> 的名字）。</summary>
        private static CsRoundPhase ParsePhase(string text)
        {
            switch (text)
            {
                case "Freeze": return CsRoundPhase.Freeze;
                case "Live": return CsRoundPhase.Live;
                case "RoundEnd": return CsRoundPhase.RoundEnd;
                case "MatchEnd": return CsRoundPhase.MatchEnd;
                case "None":
                case "":
                    return CsRoundPhase.None;
                default:
                    if (!_unknownPhaseLogged)
                    {
                        _unknownPhaseLogged = true;
                        PostLog(true, "快照里的 phase 认不出来：\"" + Truncate(text, 32) + "\"（期望 " +
                            "None / Freeze / Live / RoundEnd / MatchEnd）⇒ 按 None 处理（本条只报一次）");
                    }
                    return CsRoundPhase.None;
            }
        }

        /// <summary>"phase 串不认识"只报一次（读线程独占，不加锁）。</summary>
        private static bool _unknownPhaseLogged;

        /// <summary>WELCOME 里的 <c>proto</c>（缺字段 / 解析失败返回 0 = 老主机没报版本，按同版本处理）。</summary>
        private static int ReadProto(string json)
        {
            if (string.IsNullOrEmpty(json)) return 0;
            try
            {
                var root = MiniJson.Parse(json) as IDictionary<string, object>;
                return root == null ? 0 : (int)Num(root, "proto", 0f);
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        private static float Num(IDictionary<string, object> d, string key, float fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is double dv) return (float)dv;
            if (v is long lv) return lv;
            if (v is ulong uv) return uv;
            var s = v as string;
            if (s != null)
            {
                float f;
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            }
            return fallback;
        }

        private static string Str(IDictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return string.Empty;
            return v as string ?? v.ToString();
        }

        private static bool Bool(IDictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return false;
            if (v is bool b) return b;
            var s = v as string;
            return s != null && (s == "true" || s == "1");
        }

        // ---------------------------------------------------------------- 小工具

        private static void WriteLine(NetworkStream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>拆 <c>host:port</c>（IPv4 字面量或主机名；不支持 IPv6 字面量 —— 局域网寻服给的就是 IPv4）。</summary>
        private static bool TryParseEndpoint(string endpoint, out string host, out int port)
        {
            host = null;
            port = 0;
            if (string.IsNullOrWhiteSpace(endpoint)) return false;

            var sep = endpoint.LastIndexOf(':');
            if (sep <= 0 || sep >= endpoint.Length - 1) return false;
            host = endpoint.Substring(0, sep).Trim();
            var portText = endpoint.Substring(sep + 1).Trim();
            if (host.Length == 0) return false;
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)) return false;
            return port > 0 && port <= 65535;
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length + 8);
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ---------------------------------------------------------------- 后台 → 主线程的日志桥

        /// <summary>
        /// 读线程打日志：**投给引擎的后台→主线程派发器**（<c>Game.Dispatcher.Post</c>，由 <c>Game.Tick</c>
        ///
        /// <para><b>为什么必须绕一圈</b>：读线程是后台线程，而 Unity 的 Console / 落盘 Logger 都该在
        /// 主线程口径上写（与 <c>CsLanHost</c> 避开 <c>UnityEngine.Time</c> 同一类顾虑）——
        /// 这条通道引擎已经有了，业务再抄一遍只会多一处"什么时候排空 / 排空了没"的自管逻辑。</para>
        ///
        /// <para>派发器为 null（引擎未 Launch / 已 Shutdown）⇒ **没有主线程可投递**：丢弃并计数，
        /// 由主线程下一次 <see cref="Pump"/> 报出来（不静默丢日志）。</para>
        /// </summary>
        private static void PostLog(bool error, string message)
        {
            var dispatcher = Game.Dispatcher;
            if (dispatcher == null)
            {
                Interlocked.Increment(ref _droppedLogs);
                return;
            }

            dispatcher.Post(() =>
            {
                if (error) Game.Logger.Error(Tag, message);
                else Game.Logger.Info(Tag, message);
            });
        }

        /// <summary>
        /// 主线程：把"派发器不可用期间丢掉的日志条数"报出来。
        /// 非预期分支必须留痕 —— 否则"日志里没有"会被读成"这件事没发生"。
        /// </summary>
        private static void ReportDroppedLogs()
        {
            var dropped = Interlocked.Exchange(ref _droppedLogs, 0);
            if (dropped > 0)
            {
                Game.Logger.Warn(Tag,
                    "引擎派发器不可用（Game.Dispatcher 为 null：未 Launch / 已 Shutdown），期间丢弃后台日志 " +
                    dropped + " 条");
            }
        }
    }
}
