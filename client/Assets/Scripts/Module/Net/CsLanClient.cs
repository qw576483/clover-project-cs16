using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CloverEngine;

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
    /// <item>主机 → 客户端：<c>CS16-LAN-WELCOME/1|{{…}}</c></item>
    /// <item>主机 → 客户端（每 0.1 s）：<c>CS16-LAN-SNAP/1|{{…"actors":[{{…}}]}}</c></item>
    /// <item>任一方可发：<c>CS16-LAN-BYE/1|{{…}}</c></item>
    /// </list>
    /// 单行上限 <see cref="MaxLineBytes"/>（与主机同值 4096；超了算非法行、丢弃，不按它分配内存）。
    /// 本片**不发** <c>CS16-LAN-INPUT/1</c>：把远端输入接到主机模拟是"能开局"的下一段（见类末声明）。</para>
    ///
    /// <para><b>线程模型（关键：主线程不碰 socket）</b>：
    /// <list type="number">
    /// <item><b>读线程（<see cref="_thread"/>，<c>IsBackground = true</c>）</b>：连接 → 发 JOIN →
    /// 逐字节收行 → 解析。它就是本类**唯一**碰 socket 的地方。</item>
    /// <item><b>主线程</b>：每帧调 <see cref="Pump"/> —— 只做两件事：发现"线程已死但状态还写着在跑"时纠偏留痕、
    /// 以及（见 <see cref="IsRunning"/>）供视图层判断要不要画。主线程**一次 socket 都不读**。</item>
    /// <item>为什么日志要绕一圈：读线程是后台线程，而 Unity 的 Console / 落盘 Logger 都该在主线程口径上写
    /// （与 <c>CsLanHost</c> 避开 <c>UnityEngine.Time</c> 同一类顾虑）⇒ 读线程把日志投给
    /// **引擎的后台→主线程派发器**（<c>Game.Dispatcher.Post</c>，由 <c>Game.Tick</c> 每帧 Flush），
    /// 不再自建第二套线程队列。</item>
    /// </list></para>
    ///
    /// <para><b>本类到哪一步为止（登记在差异 #88，别当它没发生）</b>：
    /// 能连上 / 能握手 / 能持续消费快照 / 能把最新一帧交给视图层。
    /// **未做**：把客户端的输入（<c>CS16-LAN-INPUT/1</c>）发给主机、驱动主机侧模拟；
    /// 以及"远端角色的预测/插值"（本片快照直接落到位置上，见 <c>CsLanRemoteView</c>）。</para>
    /// </summary>
    public static class CsLanClient
    {
        private const string Tag = "LanClient";

        /// <summary>单行字节上限（**必须与主机 <see cref="CsLanGateway.MaxLineBytes"/> 同值**：超了对方也丢）。</summary>
        public const int MaxLineBytes = CsLanGateway.MaxLineBytes;

        /// <summary>连接主机的等待上限（毫秒）—— 主机刚起/ARP 未就绪时给一点余地，但不无限等。</summary>
        public const int ConnectTimeoutMs = 5000;

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

        /// <summary>最新一帧的远端角色（读线程写、主线程读 ⇒ 一切访问都在 <see cref="LatestLock"/> 下）。</summary>
        private static CsLanRemoteActor[] _latest = new CsLanRemoteActor[0];

        private static readonly object LatestLock = new object();
        private static readonly object StreamLock = new object();

        /// <summary>"派发器不可用期间丢掉的日志条数"（见 <see cref="PostLog"/>）：主线程下一次
        /// <see cref="Pump"/> 报出来 —— 不静默丢日志。</summary>
        private static int _droppedLogs;

        // ---------------------------------------------------------------- 尽力而为路径的限频留痕
        //   `.ai-tmp/test/sink4-sink-net-catch-selfcheck.ps1` 会把本区间整段剔除后再比对 ⇒
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
        /// ⇒ **后台线程直呼是安全的**（<c>LogThrottle</c> 则明确"主线程使用、不加锁"，本片刻意不用它）。</para>
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
            return "局域网客户端 endpoint=" + _endpoint + " 状态=" + _phase +
                   " 收到WELCOME=" + Welcomes + " 快照=" + Snapshots + " 非法行=" + Malformed +
                   " 远端角色=" + RemoteActorCount;
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
            Interlocked.Exchange(ref _welcomes, 0);
            Interlocked.Exchange(ref _snapshots, 0);
            Interlocked.Exchange(ref _malformed, 0);
            lock (LatestLock) { _latest = new CsLanRemoteActor[0]; }

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
                    _phase = "主机已断开";
                    PostLog(false, "主机发了 " + CsLanGateway.ByeMagic + "：对端主动断开，客户端收尾");
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
                Interlocked.Increment(ref _welcomes);
                _welcomePayload = json;
                _joined = true;
                _phase = "已握手，收快照中";
                PostLog(false, "收到 " + CsLanGateway.WelcomeMagic + "：" + Truncate(json, 200));
                return;
            }

            if (magic == CsLanGateway.SnapMagic)
            {
                CsLanRemoteActor[] frame;
                if (!TryParseSnapshot(json, out frame))
                {
                    Interlocked.Increment(ref _malformed);
                    if (Malformed <= 3)
                    {
                        PostLog(true, "快照解析失败（第 " + Malformed + " 条非法行）：" + Truncate(json, 160));
                    }
                    return;
                }

                lock (LatestLock) { _latest = frame; }
                Interlocked.Increment(ref _snapshots);
                return;
            }

            if (magic == CsLanGateway.ByeMagic)
            {
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

        // ---------------------------------------------------------------- 解析

        /// <summary>
        /// 解析一行快照 JSON 的 <c>actors[]</c>。
        /// 走引擎自己的 <c>MiniJson</c>（<c>Runtime/Core/Json.cs</c>）—— 不手写 JSON 解析器：
        /// 手写的转义/数字/嵌套处理是"看起来能跑、边界一碰就错"的典型。
        /// </summary>
        private static bool TryParseSnapshot(string json, out CsLanRemoteActor[] frame)
        {
            frame = null;
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
                    Weapon = Str(d, "w"),
                };
                list.Add(a);
            }

            frame = list.ToArray();
            return true;
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
