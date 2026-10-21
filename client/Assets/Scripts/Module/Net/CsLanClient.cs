using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// 已经把「能被发现 → 能连上 → 能收到世界快照」做完了；本类负责把收到的业务行
    /// <c>CS16-LAN-SNAP/1</c> 解析成一组 <see cref="CsLanRemoteActor"/>（最新一帧挂在
    /// <see cref="TryGetActors"/> 上），由 <see cref="Cs16.Module.View.CsLanRemoteView"/> 画成场上的远端角色；
    /// 同时每 <see cref="InputSendIntervalMs"/> ms 把本地输入发给主机。</para>
    ///
    /// <para><b>传输层归引擎</b>：连主机、JOIN/WELCOME 握手、拆行、断线感知全部由
    /// <see cref="ILanClient"/>（<c>CloverLan.CreateClient()</c>）承担。本类只订阅
    /// <see cref="ILanClient.OnConnected"/> / <see cref="ILanClient.OnDown"/> /
    /// <see cref="ILanClient.OnClosed"/>，并用 <see cref="ILanClient.Send"/> 发业务行。</para>
    ///
    /// <para><b>两段握手（引擎一段、业务一段）</b>：
    /// <list type="number">
    /// <item>引擎：TCP 连上 → 引擎 JOIN → 引擎 WELCOME → <see cref="ILanClient.OnConnected"/>；</item>
    /// <item>业务：本类在这里发 <c>CS16-LAN-JOIN/1|{{"name":…,"proto":1}}</c>，主机建出身体后回
    /// <c>CS16-LAN-WELCOME/1|{{…,"actorId":…}}</c> ⇒ <see cref="Joined"/> 为 true，之后才发 INPUT。</item>
    /// </list></para>
    ///
    /// <para><b>业务线格式（不臆造；逐字同 <see cref="CsLanGateway"/>，两端必须同值）</b>：
    /// 一律 UTF-8，形如 <c>&lt;MAGIC&gt;|&lt;json&gt;</c>，作为一条业务载荷交给引擎（引擎再套自己的
    /// <c>CLOVER-LAN-UP/1</c> / <c>CLOVER-LAN-DOWN/1</c> 信封，用 base64 原样承载这些字节）：
    /// <list type="bullet">
    /// <item>客户端 → 主机：<c>CS16-LAN-JOIN/1|{{"name":"…","proto":1}}</c></item>
    /// <item>客户端 → 主机（每 <see cref="InputSendIntervalMs"/> ms）：<c>CS16-LAN-INPUT/1|{{"id":…,"Move":{{"x":…,"y":…}},"Jump":…, …}}</c>
    /// —— 键名逐字 = <c>CsInputState</c> 的成员名，<c>id</c> = **WELCOME 里主机分配给我的那具身体**
    /// （主机没带 <c>actorId</c> 时才退回"快照里与我同名的那具"）。</item>
    /// <item>主机 → 客户端：<c>CS16-LAN-WELCOME/1|{{"proto":1,…,"actorId":…,"playerName":…,"team":…}}</c></item>
    /// <item>主机 → 客户端（每 0.1 s）：<c>CS16-LAN-SNAP/1|{{…"actors":[{{…}}]}}</c></item>
    /// <item>任一方可发：<c>CS16-LAN-BYE/1|{{…}}</c></item>
    /// </list></para>
    ///
    /// <para><b>线程模型</b>：引擎的收发在后台线程，三处事件经 <c>Game.Dispatcher</c> 收敛到主线程
    /// （派发器不可用时在引擎的后台线程上同步回调）。因此事件回调里**只改字段与解析**，
    /// 停连接一律交给主线程的 <see cref="Pump"/>（在回调里 Dispose 会 Join 自己那条线程）。
    /// 主线程每帧调 <see cref="Pump"/>：查握手 / 首帧快照的超时，按
    /// <see cref="InputSendIntervalMs"/> 发本地输入。</para>
    ///
    /// <para><b>本类到哪一步为止</b>：能连上 / 两段握手 / 能持续消费快照 / 能把最新一帧交给视图层 /
    /// 能把本地输入发给主机（主机侧按 <c>id</c> 应用到模拟，见 <see cref="CsLanGateway.ApplyRemoteInputs"/>）。
    /// **未做**："远端角色的预测/插值"（快照直接落到位置上，见 <c>CsLanRemoteView</c>）。</para>
    /// </summary>
    public static class CsLanClient
    {
        private const string Tag = "LanClient";

        /// <summary>业务单行上限（**必须与主机 <see cref="CsLanGateway.MaxLineBytes"/> 同值**）。</summary>
        public const int MaxLineBytes = CsLanGateway.MaxLineBytes;

        /// <summary>连接主机 + 等引擎 WELCOME 的等待上限（毫秒）—— 主机刚起/ARP 未就绪时给一点余地，但不无限等。</summary>
        public const int ConnectTimeoutMs = 5000;

        /// <summary>
        /// 等业务 <c>CS16-LAN-WELCOME/1</c> 的上限（毫秒）：引擎握手成功但主机没在比赛里
        /// （建不出身体）⇒ 判失败并收尾，界面不会永远停在 "Connecting..."。
        /// </summary>
        public const int JoinTimeoutMs = 10000;

        /// <summary>
        /// 握手完成后等**第一条快照**的上限（毫秒）：主机对局网关在跑、但主机没在比赛里
        /// （<see cref="CsLanGateway.Pump"/> 只在 <c>match.IsRunning</c> 时出快照）⇒ 判失败并给出原因。
        /// </summary>
        public const int FirstSnapshotTimeoutMs = 15000;

        /// <summary>发 INPUT 的间隔（毫秒）⇒ 20 Hz；与主机 10 Hz 的快照频率错开，主机每次取到的是最新意图。</summary>
        public const int InputSendIntervalMs = 50;

        /// <summary>
        /// 快照断流的判定阈值（毫秒）：超过这么久没有新快照 ⇒ <see cref="LinkStale"/> 为 true，
        /// HUD 标"连接中断"，不静默继续跑旧比分。
        /// </summary>
        public const int LinkTimeoutMs = 2000;

        /// <summary>引擎局域网客户端（传输层：连接 / 握手 / 拆行 / 断线感知）。</summary>
        private static ILanClient _client;

        /// <summary>本端是否处于"加入中 / 已加入"（<see cref="Stop"/> 与引擎断开时置 false）。</summary>
        private static volatile bool _running;

        /// <summary>业务握手完成（收到主机回的 <c>CS16-LAN-WELCOME/1</c>）。</summary>
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
        /// 主机在业务 <c>WELCOME</c> 里分配给我的 actorId；0 = 主机没带这个字段（退回按玩家名识别）。
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

        /// <summary>本次 <see cref="Start"/> 的时刻（等业务 WELCOME 的计时起点，毫秒）。</summary>
        private static int _startedAt;

        /// <summary>收到业务 WELCOME 的时刻（等第一条快照的计时起点，毫秒）；0 = 还没握手。</summary>
        private static int _joinedAt;

        /// <summary>业务 <c>BYE</c> 已到：由主线程的 <see cref="Pump"/> 收尾（回调里不 Dispose 自己）。</summary>
        private static volatile bool _closeRequested;

        /// <summary>最新一帧的远端角色（事件回调写、主线程读 ⇒ 一切访问都在 <see cref="LatestLock"/> 下）。</summary>
        private static CsLanRemoteActor[] _latest = new CsLanRemoteActor[0];

        /// <summary>最新一帧快照里的回合状态（与 <see cref="_latest"/> 同锁）。</summary>
        private static CsLanServerState _serverState;

        /// <summary>最后一条快照的到达时刻（<see cref="Environment.TickCount"/>，毫秒）；0 = 还没收到。</summary>
        private static volatile int _lastSnapshotAt;

        private static readonly object LatestLock = new object();

        /// <summary>"派发器不可用期间丢掉的日志条数"（见 <see cref="PostLog"/>）：主线程下一次
        /// <see cref="Pump"/> 报出来 —— 不静默丢日志。</summary>
        private static int _droppedLogs;

        // ---------------------------------------------------------------- 尽力而为路径的限频留痕
        //   自检脚本会把本区间整段剔除后再比对 ⇒
        //   本区间内只许放"闸门本身"，任何控制流改动都必须挪到区间外，否则自检会失去意义。

        /// <summary>尽力而为路径失败时**必须留下现场**；每处最多每 <see cref="BestEffortLogIntervalMs"/> ms 一条。</summary>
        private const int BestEffortLogIntervalMs = 5000;

        private static int _nextSendFailLogAt;      // Send 返回 false（未连接 / 超限）
        private static int _nextStopLogAt;          // Stop：引擎 Close / Dispose 抛错

        /// <summary>
        /// 尽力而为路径的**限频闸门**（**线程安全**）：返回 <c>true</c> = 这一条该报。
        /// <c>Interlocked.CompareExchange</c> 抢"上报资格"，所以引擎回调与主线程可以共用同一个闸门。
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

        /// <summary>是否已收到主机的业务 <c>CS16-LAN-WELCOME/1</c>（业务握手完成）。</summary>
        public static bool Joined { get { return _joined; } }

        /// <summary>主机是否已发业务 <c>CS16-LAN-BYE/1</c>（对端主动断开）。</summary>
        public static bool RemoteClosed { get { return _remoteClosed; } }

        /// <summary>连的是哪台主机（<c>host:port</c>）；未启动时为 <c>-</c>。</summary>
        public static string Endpoint { get { return _endpoint; } }

        /// <summary>最近一次失败原因（<see cref="Start"/> 的同步失败，或引擎断开的异步原因）；无失败为 null。</summary>
        public static string LastError { get { return _lastError; } }

        /// <summary>收到的业务 WELCOME 条数（判据用：&gt;0 = 真的握上手了）。</summary>
        public static long Welcomes { get { return Interlocked.Read(ref _welcomes); } }

        /// <summary>收到的快照条数（判据用：&gt;=N = 快照是**连续**的，而不只是"回了一包就走"）。</summary>
        public static long Snapshots { get { return Interlocked.Read(ref _snapshots); } }

        /// <summary>丢弃的非法业务行数（不是本协议 / 超长 / JSON 解析失败）+ 引擎丢弃的信封行数。</summary>
        public static long Malformed
        {
            get
            {
                var client = _client;
                return Interlocked.Read(ref _malformed) + (client != null ? client.DroppedCount : 0L);
            }
        }

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
        /// 本机是不是"跟着主机状态跑"的一方（业务握手完成 = 客机）。
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
        /// 加入一台主机的对局网关（<paramref name="hostEndpoint"/> 形如 <c>192.168.1.7:8002</c>；
        /// 直接喂 <c>LanHostInfo.Address</c> 即可）。
        ///
        /// <para><b>返回值只管"起没起"</b>：引擎的 <c>Connect</c> 是**非阻塞**的 —— 真正的连接/握手在后台发生，
        /// 成功与否看 <see cref="Joined"/> / <see cref="LastError"/> / <see cref="Describe"/>。
        /// <paramref name="error"/> 只覆盖**同步**就能发现的失败（地址不合法 / 平台不支持 / 参数非法）。</para>
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
            _closeRequested = false;
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
            lock (LatestLock)
            {
                _latest = new CsLanRemoteActor[0];
                _serverState = default(CsLanServerState);
            }

            var client = CloverLan.CreateClient();
            client.OnConnected += HandleEngineConnected;
            client.OnDown += HandleEngineDown;
            client.OnClosed += HandleEngineClosed;
            _client = client;
            _running = true;

            var self = LanHostInfo.Create(host, port, 0, null, null, 0, 0, null, null);
            var options = new LanClientOptions
            {
                TimeoutMs = ConnectTimeoutMs,
                MaxLineBytes = MaxLineBytes,
                Name = _playerName,
            };

            if (!client.Connect(self, options))
            {
                // 平台不支持 / 端点非法：引擎不抛，原因在 LastError。
                error = client.LastError;
                _running = false;
                _client = null;
                _phase = "未启动";
                _lastError = error;
                client.OnConnected -= HandleEngineConnected;
                client.OnDown -= HandleEngineDown;
                client.OnClosed -= HandleEngineClosed;
                client.Dispose();
                Game.Logger.Error(Tag, "发起加入失败：" + error);
                return false;
            }

            Game.Logger.Info(Tag,
                "局域网客户端启动：连 " + _endpoint + " 玩家名=\"" + _playerName + "\"（引擎握手 → " +
                CsLanGateway.JoinMagic + " → " + CsLanGateway.WelcomeMagic + " → 持续收 " +
                CsLanGateway.SnapMagic + "；连接在后台，结果见 Joined / LastError）");
            return true;
        }

        /// <summary>停下（幂等）。引擎 Close 会先给主机发 BYE 再关 socket。</summary>
        public static void Stop()
        {
            var wasRunning = _running;
            _running = false;
            _joined = false;
            _closeRequested = false;

            var client = _client;
            _client = null;
            if (client != null)
            {
                client.OnConnected -= HandleEngineConnected;
                client.OnDown -= HandleEngineDown;
                client.OnClosed -= HandleEngineClosed;
                try { client.Close(); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextStopLogAt))
                    {
                        Game.Logger.Warn(Tag, "停止时关闭局域网客户端失败（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
                try { client.Dispose(); }
                catch (Exception ex)
                {
                    if (ShouldLogBestEffort(ref _nextStopLogAt))
                    {
                        Game.Logger.Warn(Tag, "停止时释放局域网客户端失败（尽力而为路径，不影响开局）：" +
                            ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            _phase = "未启动";
            _myActorId = -1;
            _assignedActorId = 0;
            _assignedPlayerName = null;
            _selfTeamRaw = (int)CsTeam.Spectator;
            _lastSnapshotAt = 0;
            lock (LatestLock) { _serverState = default(CsLanServerState); }
            ReportDroppedLogs();
            if (wasRunning)
            {
                Game.Logger.Info(Tag, "局域网客户端已停止：" + Describe());
            }
        }

        // ---------------------------------------------------------------- 主线程：每帧泵

        /// <summary>
        /// 主线程每帧调一次。
        /// <para>它**不解析任何报文、不碰 socket**（那是引擎的事）：只
        /// ① 查"引擎握手完成但业务 WELCOME 迟迟不来"与"握手后没有第一帧快照"两个有界等待；
        /// ② 按 <see cref="InputSendIntervalMs"/> 把本地输入发给主机；③ 落地业务 BYE 的收尾。
        /// 未启动时是一次 bool 判断。</para>
        /// </summary>
        public static void Pump()
        {
            if (!_running)
            {
                ReportDroppedLogs();          // 收尾：Stop() 之后可能还有最后几条
                return;
            }

            var now = Environment.TickCount;

            // 引擎握手成功、业务 WELCOME 迟迟不来 ⇒ 判失败，界面不会永远停在 "Connecting..."。
            if (!_joined && unchecked(now - _startedAt) > JoinTimeoutMs)
            {
                Fail("连上主机 " + _endpoint + " 后 " + JoinTimeoutMs + " ms 内没收到 " +
                     CsLanGateway.WelcomeMagic + "（主机没在比赛里 / 两队满员？）⇒ 放弃本次加入");
                Stop();
                return;
            }

            // 业务握手成功却一条快照都没有 ⇒ 主机对局网关在跑，但主机侧没有在比赛里出快照。
            if (_joined && Interlocked.Read(ref _snapshots) == 0 && unchecked(now - _joinedAt) > FirstSnapshotTimeoutMs)
            {
                Fail("已握手但 " + FirstSnapshotTimeoutMs + " ms 内没收到任何 " + CsLanGateway.SnapMagic +
                     "：主机没在比赛里（对局网关只在主机 match.IsRunning 时出快照）" +
                     "⇒ 先在主机上 New Game 开局，再从这台机器加入");
                Stop();
                return;
            }

            SendLocalInput();

            if (_closeRequested)
            {
                _closeRequested = false;
                Stop();
                return;
            }

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

        /// <summary>
        /// 主线程：按 <see cref="InputSendIntervalMs"/> 把本地输入发给主机。
        ///
        /// <para>三道闸门：业务握手完成 / 有人喂过输入 / 已在主机快照里认出自己（<see cref="_myActorId"/>）。
        /// 任一没满足都不发 —— 不知道"这具身体是谁"时乱编一个 id 只会落到别人的 actor 上。</para>
        /// </summary>
        private static void SendLocalInput()
        {
            if (!_joined || !_haveLocalInput || _myActorId <= 0) return;

            var client = _client;
            if (client == null) return;

            var now = Environment.TickCount;
            if (unchecked(now - _lastInputSentAt) < InputSendIntervalMs) return;
            _lastInputSentAt = now;

            var bytes = Encoding.UTF8.GetBytes(BuildInputLine(_myActorId, _localInput));
            if (client.Send(bytes))
            {
                Interlocked.Increment(ref _inputsSent);
                return;
            }

            if (ShouldLogBestEffort(ref _nextSendFailLogAt))
            {
                Game.Logger.Warn(Tag,
                    "本地输入没发出去（未连接 / 单行超限）：actor=" + _myActorId + "，" + Describe());
            }
        }

        // ---------------------------------------------------------------- 引擎回调

        /// <summary>
        /// 引擎握手完成（引擎 WELCOME 已到）：立刻发**业务首帧** <see cref="CsLanGateway.JoinMagic"/>。
        /// <para>这是两段握手的第二段 —— 主机的身体要等这条到了才建，所以它必须紧跟引擎握手。</para>
        /// </summary>
        private static void HandleEngineConnected()
        {
            if (!_running) return;

            var client = _client;
            if (client == null) return;

            _phase = "已连接，发 " + CsLanGateway.JoinMagic;

            var join = CsLanGateway.JoinMagic + "|{\"name\":\"" + JsonEscape(_playerName) +
                       "\",\"proto\":" + CsLanGateway.ProtocolVersion + "}";
            if (!client.Send(Encoding.UTF8.GetBytes(join)))
            {
                if (ShouldLogBestEffort(ref _nextSendFailLogAt))
                {
                    Game.Logger.Warn(Tag, "引擎已握手但 " + CsLanGateway.JoinMagic + " 没发出去：" + Describe());
                }
                return;
            }

            PostLog(false, "已连上主机 " + _endpoint + "（引擎握手完成），发 " + CsLanGateway.JoinMagic);
        }

        /// <summary>一条业务下行载荷（引擎已拆行、已解 base64）：按魔数分流。</summary>
        private static void HandleEngineDown(byte[] payload)
        {
            if (!_running || payload == null) return;

            string text;
            try
            {
                text = Encoding.UTF8.GetString(payload);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _malformed);
                PostLog(true, "下行载荷不是合法 UTF-8（" + ex.GetType().Name + "）⇒ 丢弃");
                return;
            }

            if (text.Length == 0) return;
            HandleLine(text);
        }

        /// <summary>
        /// 引擎侧断开（对端 BYE / 读写出错 / 本端 Close）。状态在这里收敛；
        /// 本端主动 <see cref="Stop"/> 时 <see cref="_running"/> 已是 false，不覆盖用户可见状态。
        /// </summary>
        private static void HandleEngineClosed(string reason)
        {
            var wasRunning = _running;
            var wasJoined = _joined;

            _joined = false;
            _running = false;

            if (!wasRunning) return;      // 本端 Stop()/Dispose() 的收尾：不改状态、不报错

            _remoteClosed = true;

            if (string.IsNullOrEmpty(_lastError)) _lastError = ClosedReasonText(reason);
            _phase = wasJoined ? "主机已断开" : "连接失败";
            PostLog(true, "局域网连接结束（reason=\"" + (reason ?? "-") + "\"）：" + _lastError);
        }

        /// <summary>引擎断线原因 → 给玩家看的说明。</summary>
        private static string ClosedReasonText(string reason)
        {
            switch (reason)
            {
                case "full":
                    return "主机已满员（握手阶段被拒）——请等有人退出后重试";
                case "timeout":
                    return "连接主机超时（主机没起 / 被防火墙丢弃）";
                case "malformed":
                    return "对端报文不合规（握手协议版本或行格式不一致）";
                case "closed":
                    return "主机已关闭这条连接";
                default:
                    return "局域网连接结束（reason=" + (string.IsNullOrEmpty(reason) ? "-" : reason) + "）";
            }
        }

        /// <summary>处理一条业务报文（可能在引擎回调线程上执行；除日志入队外不做任何主线程相关的事）。</summary>
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
                    _closeRequested = true;    // 由主线程 Pump 收尾
                    return;
                }

                Interlocked.Increment(ref _welcomes);
                _welcomePayload = json;
                _joined = true;
                _joinedAt = Environment.TickCount;
                _lastSnapshotAt = _joinedAt;      // 断流判定从握手算起（握手后一直没快照也算断流）
                _phase = "已握手，收快照中";

                // WELCOME 可选带 "actorId"：主机若直接告诉我"你的身体是哪个 id"就用它，
                // 不带由 RecognizeSelf 按玩家名回落识别。
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
                    // 未握手就被拒 = 主机两队都满（引擎满员走引擎自己的 BYE，不会到这里）。
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
                _closeRequested = true;    // 由主线程 Pump 收尾
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
        /// **引擎回调线程**：确定"我在这台主机上的身体"（<see cref="_myActorId"/>）。
        ///
        /// <para><b>两条来源</b>：① 主机在业务 <c>WELCOME</c> 里给了 <c>actorId</c> ⇒ **权威**，只用它
        /// （快照里同名的那具可能是别人的：两台机器都叫 Player 时就是主机玩家，认成自己会让主机拒收
        /// 我的输入）；② 主机没给 <c>actorId</c> ⇒ 退回按 JOIN 时的玩家名识别。</para>
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
                // 主机没给 actorId：换局 / 重生后快照里换了 id ⇒ 重新认一次
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
                    "主机没给 actorId：已在快照里按名字认出自己 playerName=\"" + _playerName + "\" ⇒ actor=" + a.Id +
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
        /// 业务 WELCOME 里可选的 <c>actorId</c>（主机直接指定"你的身体是哪个 id"）。
        /// 缺字段 / 解析失败返回 0 = 主机没这个字段，交给 <see cref="RecognizeSelf"/> 按玩家名识别。
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
        /// 快照里自己那具的阵营 → <see cref="AssignedTeam"/>（半场换边后本地跟着换）。
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

        /// <summary>业务 BYE 的 <c>reason</c> → 给玩家看的原因（状态行 / Toast 共用）。</summary>
        private static string ByeReasonText(string reason)
        {
            if (reason == "full") return "主机已满员（两队都满，未分配 actorId）——请等有人退出后重试";
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

        /// <summary>"phase 串不认识"只报一次（回调线程独占，不加锁）。</summary>
        private static bool _unknownPhaseLogged;

        /// <summary>WELCOME 里的 <c>proto</c>（缺字段 / 解析失败返回 0 = 没报版本，按同版本处理）。</summary>
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
        /// 引擎回调打日志：**投给引擎的后台→主线程派发器**（<c>Game.Dispatcher.Post</c>，由 <c>Game.Tick</c> 排空）。
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
