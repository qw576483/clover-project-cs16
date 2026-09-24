using CloverEngine;

namespace Cs16.Module.Net
{
    /// <summary>
    /// 局域网**主机广播（应答端）**—— 差异 #88「必须支持局域网联机」的发现侧落地。
    ///
    /// <para><b>实现方式：调引擎的 LAN 应答端能力</b>（本类不再复制协议、不再自己收发 UDP）。
    /// 真源全在引擎侧，逐条给出处（<c>clover-client-unity-engine</c>）：</para>
    /// <list type="bullet">
    /// <item>工厂：<c>CloverLan.CreateResponder()</c>（<c>Runtime/Network/Lan/CloverLan.cs:113</c>）→
    /// <see cref="ILanResponder"/>（契约见 <c>Runtime/Core/LanContracts.cs:245</c>）。</item>
    /// <item>协议与线格式：<c>Runtime/Network/Lan/LanProtocol.cs</c>（<c>QueryMagic=CLOVER-LAN-QUERY/1</c>
    /// /<c>ReplyMagic=CLOVER-LAN-REPLY/1</c>/<c>DefaultPort=47777</c>/<c>MaxDatagramBytes=512</c>，
    /// 该校验口径 <c>:135-175</c>）；收发层 <c>LanResponder.cs</c>（复用 <c>LanSocket</c>）。
    /// <b>协议只有一份实现</b>。</item>
    /// <item>四条边界都不抛（调用方的"开主机"流程不该被环境差异打断，但必须留痕，
    /// 见 <c>LanContracts.cs:236-243</c>）：平台不支持 / 端口被占用 ⇒ <c>Start</c> 返回 false 且
    /// <see cref="ILanResponder.LastError"/> 给出原因；重复 <c>Start</c> ⇒ 幂等（只更新广播参数）；
    /// <c>Stop</c> 未启动 ⇒ 空操作。本类把这份 <c>bool</c>+<c>LastError</c> 原样转成
    /// <see cref="Start"/> 的 <c>error</c> 出口（面板靠它显示失败原因，不许静默当成功）。</item>
    /// <item>地址解析：<c>LanHostInfo.Host</c> 留空 ⇒ 引擎取本机 IPv4（<c>LanResponder.cs:397</c> 的
    /// <paramref name="gatewayHost"/> 非空时按给定字面量广播（多网卡场景）。</item>
    /// <item>日志：收包线程的查询日志（限频）由引擎 <c>LanResponder</c> 负责；本类只在
    /// <b>第一条</b>查询时补一条工程级面包屑（见 <see cref="OnFirstQuery"/>），不重复刷屏。</item>
    /// </list>
    ///
    /// <para><b>本类只管"能被发现"</b>：真正的对局同步仍要一台可连的网关
    /// （<c>CloverNet.Init(host.Address, host.UdpAddress)</c> 的目标）。
    /// 广播出去的 <c>gateway</c> 端口就是那个目标 —— 它是**参数**，不是本类自己起的服务；
    /// 本工程把它真的开起来的动作在 <see cref="CsLanGateway"/>（差异 #88 的"能开局"侧）。</para>
    /// </summary>
    public static class CsLanHost
    {
        private const string Tag = "LanHost";

        /// <summary>寻服专用端口（= 引擎 <c>LanProtocol.DefaultPort</c>，不再自己写 47777 字面量）。</summary>
        public const int DiscoveryPort = LanProtocol.DefaultPort;

        /// <summary>默认主机名（客户端显示的就是它）。</summary>
        public const string DefaultName = "clover-cs16 LAN host";

        /// <summary>默认网关 TCP 口（与引擎结构规则里示例的 <c>ResponderGatewayPort</c> 同源：8002）。</summary>
        public const int DefaultGatewayPort = 8002;

        /// <summary>默认裸 UDP 口（同上：8003）；≤0 表示不提供不可靠通道。</summary>
        public const int DefaultUdpPort = 8003;

        /// <summary>版本串（只用于显示；取工程名而不是 Unity 版本，免得对方把 Unity 版本当协议版本）。</summary>
        private const string VersionTag = "clover-cs16/1";

        /// <summary>透传字段（协议里的 <c>extra</c> 原样带出）。</summary>
        private const string ExtraTag = "cs16-single-map de_dust2";

        /// <summary>引擎应答端实例（进程内一个；<see cref="Stop"/> 可反复启停 —— 由调用方持有，引擎不代管）。</summary>
        private static ILanResponder _responder;

        /// <summary>当前对外广播的主机快照（= 引擎 <c>responder.Self</c>；未启动时为 null）。</summary>
        private static LanHostInfo _self;

        private static string _name = DefaultName;
        private static int _gatewayPort = DefaultGatewayPort;
        private static int _udpPort = DefaultUdpPort;
        private static int _players;
        private static int _maxPlayers;

        /// <summary>最早那条查询只记一次（工程级面包屑；引擎自己按 1s 限频打查询日志，不重复）。</summary>
        private static bool _firstQueryLogged;

        /// <summary>应答端是否在跑。</summary>
        public static bool IsRunning
        {
            get { return _responder != null && _responder.IsRunning; }
        }

        /// <summary>广播出去的地址里用的**本机地址**（IPv4 字面量）；未启动时为空串。
        /// 它是"网卡地址"，不是"来源 IP" —— 应答里必须带它（对方不信任来源 IP）。</summary>
        public static string AdvertisedHost
        {
            get { return _self != null ? _self.Host : string.Empty; }
        }

        /// <summary>广播出去的网关口。</summary>
        public static int GatewayPort
        {
            get { return _gatewayPort; }
        }

        /// <summary>广播出去的裸 UDP 口（0 = 不提供）。</summary>
        public static int UdpPort
        {
            get { return _udpPort; }
        }

        /// <summary>收到的查询总数（判据用：>0 说明"确实有人在找服"）。</summary>
        public static long Queries
        {
            get { return _responder != null ? _responder.QueryCount : 0L; }
        }

        /// <summary>回出去的应答总数（判据用：应等于 <see cref="Queries"/>）。</summary>
        public static long Replies
        {
            get { return _responder != null ? _responder.ReplyCount : 0L; }
        }

        /// <summary>被丢弃的非法包数（不是查询 / 超长 / 空）—— 留痕，不静默。</summary>
        public static long Malformed
        {
            get { return _responder != null ? _responder.DroppedCount : 0L; }
        }

        /// <summary>
        /// 开始应答局域网查询（幂等：已在跑时只更新广播参数并返回 true）。
        /// </summary>
        /// <param name="error">失败原因（成功为 null）—— 取自 <see cref="ILanResponder.LastError"/></param>
        /// <param name="gatewayPort">对外广播的网关 TCP 口（要有一台真的服务在监听，见 <see cref="CsLanGateway"/>）</param>
        /// <param name="udpPort">对外广播的裸 UDP 口；≤0 表示不提供</param>
        /// <param name="name">主机显示名</param>
        /// <param name="players">当前人数（只用于显示）</param>
        /// <param name="maxPlayers">人数上限（0 = 未知）</param>
        /// <param name="gatewayHost">覆盖广播地址里的 host；传 null/空 = 引擎自动取本机网卡 IPv4</param>
        /// <returns>是否处于"在跑"状态</returns>
        public static bool Start(out string error, int gatewayPort = DefaultGatewayPort, int udpPort = DefaultUdpPort,
            string name = null, int players = 1, int maxPlayers = 10, string gatewayHost = null)
        {
            error = null;

            var responder = EnsureResponder();
            if (!responder.IsSupported)
            {
                // 平台不支持是**环境事实**不是调用错误：明确留痕、返回给调用方显示（同 ILanBrowser 口径）。
                error = responder.UnsupportedReason;
                Game.Logger?.Warn(Tag, $"不启动局域网应答端：{error}");
                return false;
            }

            _name = string.IsNullOrEmpty(name) ? DefaultName : name;
            _gatewayPort = gatewayPort;
            _udpPort = udpPort > 0 ? udpPort : 0;
            _players = players < 0 ? 0 : players;
            _maxPlayers = maxPlayers < 0 ? 0 : maxPlayers;

            var wasRunning = responder.IsRunning;
            var self = LanHostInfo.Create(
                string.IsNullOrEmpty(gatewayHost) ? string.Empty : gatewayHost,   // 空 = 引擎取本机 IPv4
                _gatewayPort, _udpPort, null, _name, _players, _maxPlayers, VersionTag, ExtraTag);

            if (!responder.Start(self))
            {
                // 引擎不抛，原因在 LastError（端口被占 / 取不到本机地址 / 端口非法）—— 原样转出去。
                error = responder.LastError;
                Game.Logger?.Error(Tag, $"局域网应答端启动失败：{error}");
                return false;
            }

            _self = responder.Self ?? self;

            if (wasRunning)
            {
                Game.Logger?.Info(Tag, $"局域网应答端已在跑，仅更新广播参数：{Describe()}");

                // 已在跑但网关没跑（上一次异常路径留下的）：补起，否则广播出去的门没人接。
                if (!CsLanGateway.IsRunning)
                {
                    string gwErr2;
                    CsLanGateway.Start(out gwErr2, gatewayPort, _name, _maxPlayers);
                    if (gwErr2 != null) Game.Logger?.Warn(Tag, $"补起对局网关失败：{gwErr2}");
                }
            }
            else
            {
                Game.Logger?.Info(Tag,
                    $"局域网应答端已启动：{Describe()}（端口 {DiscoveryPort}，协议由引擎 LanProtocol 承载）");

                // ---- 差异 #88「能开局」：把广播出去的那个 gateway 端口**真的开起来** ----
                // 在此之前它只是个**参数** —— 没有任何进程在监听它，所以对方
                // "在列表里看得到、点加入却接不上"。本行补上那个"答的一半"（见 CsLanGateway）。
                string gwErr;
                if (!CsLanGateway.Start(out gwErr, gatewayPort, _name, _maxPlayers))
                {
                    // 不把 Start 整个判失败：**被发现**的能力仍然有效（应答端已在跑），
                    //    两个失败原因必须分开报，别让调用方以为"整个 LAN 主机都没起来"。
                    Game.Logger?.Warn(Tag,
                        $"局域网应答端已起来，但对局网关没起来（这台主机暂时**不能被加入**）：{gwErr}");
                }
            }

            return true;
        }

        /// <summary>停止应答（幂等；未在跑时是空操作）。收包线程在 ≤ <c>LanSocket.ReceiveTimeoutMs</c> 内退出。</summary>
        public static void Stop()
        {
            var responder = _responder;
            if (responder != null && responder.IsRunning)
            {
                // 引擎 Stop() 自己会打一条含计数的收尾日志（查询/应答/丢弃），这里只说工程语义。
                responder.Stop();
                Game.Logger?.Info(Tag, "局域网应答端已停止（本机不再被同网段发现）");
            }

            _self = null;
            _firstQueryLogged = false;

            // 差异 #88：应答端没在跑也要确保网关停掉（可能是上一次的异常路径留下的）。
            CsLanGateway.Stop();
        }

        /// <summary>一行状态（日志 / 探针 / 面板都用它，避免三处文案各写一套）。
        /// 运行中时直接取引擎 <see cref="ILanResponder.Describe"/>（它已含 name/gateway/udp/players 与三个计数）。</summary>
        public static string Describe()
        {
            var responder = _responder;
            if (responder == null || !responder.IsRunning) return "局域网应答端未启动";
            return responder.Describe();
        }

        /// <summary>
        /// 惰性创建应答端（<c>CloverLan.CreateResponder()</c>）。实例由本类持有并**跨 Stop/Start 复用**
        /// </summary>
        private static ILanResponder EnsureResponder()
        {
            if (_responder != null) return _responder;

            _responder = CloverLan.CreateResponder();
            _responder.OnQuery += OnFirstQuery;   // 引擎已按 1s 限频打查询日志，这里只补"第一条"的面包屑
            return _responder;
        }

        /// <summary>第一条合法查询：证明"确实有人在找服"（之后由引擎的限频日志继续记）。</summary>
        private static void OnFirstQuery(string from)
        {
            if (_firstQueryLogged) return;
            _firstQueryLogged = true;
            Game.Logger?.Info(Tag,
                $"收到第一条局域网查询（from={from}）—— 应答端工作正常，本机可被同网段发现");
        }
    }
}
