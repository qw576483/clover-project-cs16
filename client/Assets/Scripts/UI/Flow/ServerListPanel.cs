using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Net;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 局域网服务器列表（对应 CS 1.6 主菜单的 "Find Servers"）：真实调 <see cref="ILanBrowser"/>。
    ///
    /// <para>
    /// <list type="number">
    /// <item><b>问的一端</b>：引擎 <c>Game.LanBrowser</c>（扫描 / 收应答 / 出列表）；</item>
    /// <item><b>答的一端</b>：由本工程 <see cref="Cs16.Module.Net.CsLanHost"/> 应答
    /// （引擎里只有测试用的回环应答器），并给了面板上一个 <c>Host LAN Game</c> 开关；</item>
    /// <item><b>点主机</b>：起本工程的 <see cref="CsLanClient"/>（<c>CS16-LAN-JOIN/1</c> →
    /// <c>CS16-LAN-WELCOME/1</c> → 持续收 <c>CS16-LAN-SNAP/1</c>）；握手成功才发出
    /// <c>Events.LaunchMatch</c> 进图，失败 / 超时把原因写到状态行，不留"点了没反应"。</item>
    /// <item><b>自己的房</b>：本机应答端也会应答本机发出的寻服查询 ⇒ 列表里按
    /// <see cref="CsLanHost.IsSelfHost"/> 剔除自己那一条；点它也会被拒绝（连回自己会看到自己的角色）。</item>
    /// </list>
    /// </para>
    ///
    /// <para>远端角色由 <c>Module/View/CsLanRemoteView</c> 按快照画到场上（"收到数据 ≠ 看得见人"那一半），
    /// 本机走位经 <c>CS16-LAN-INPUT/1</c> 发给主机。<b>当前边界</b>：远端角色在本机**只到"看得见"这一层**
    /// （不参与碰撞 / 命中 / 音频，见 <c>CsLanRemoteView</c> 的类注释），主机侧不会为加入者新建身体
    /// （加入者的输入只对主机上同名的 actor 生效，主机自己的玩家拒绝远端输入）。</para>
    ///
    /// <para>平台不支持时（WebGL 无 BSD socket）显示 <see cref="ILanBrowser.UnsupportedReason"/>。</para>
    /// </summary>
    public class ServerListPanel : CsPanelBase
    {
        private const float ListX = 90f;
        private const float ListY = -240f;
        private const float ListWidth = 1740f;
        private const float ListHeight = 580f;
        private const float RowStep = 50f;
        private const float RowWidth = 1700f;
        private const float RowHeight = 44f;

        [SerializeField] private Text _status;
        [SerializeField] private RectTransform _listRoot;
        [SerializeField] private Button _refreshButton;
        [SerializeField] private Button _backButton;
        [SerializeField] private Button _hostButton;

        /// <summary>底注文案（**public const**：预制体补丁脚本按它写进预制体的 <c>Footer</c> 节点，
        /// 不许在补丁脚本里另抄一份字面量 —— 抄了就会和这里漂移）。</summary>
        /// <remarks>里面那个 <c>47777</c> 是**面向玩家的展示文案**，对应引擎真源
        /// <c>LanProtocol.DefaultPort</c>（<c>Runtime/Network/Lan/LanProtocol.cs:48</c>）。
        /// 它**不能**写成常量拼接：C# 的 <c>const string</c> 只接受「string + string」，
        /// <c>"…" + LanProtocol.DefaultPort</c>（int）不是常量表达式 ⇒ 只能保持字面量，
        /// ⇒ 若引擎改了默认端口，**这里必须同步改**（判据：本类里出现的端口字面量只有这一处）。</remarks>
        public const string FooterText =
            "Refresh = run a real LAN scan (UDP 47777); Host LAN Game = answer server queries from this machine.";

        public const string HostButtonIdleText = "Host LAN Game";

        /// <summary>应答端在跑时的按钮文案。</summary>
        public const string HostButtonRunningText = "Stop LAN Host";

        public const string HostButtonName = "Btn_HostLan";

        /// <summary>广播出去的主机名（含机器名，便于同网段两台机器区分）。</summary>
        public static string HostName
        {
            get { return $"{CsLanHost.DefaultName} @ {System.Environment.MachineName}"; }
        }

        private readonly List<GameObject> _rows = new List<GameObject>();
        private bool _subscribed;

        /// <summary>正在加入的主机（握手在后台线程，本面板每帧看结果）；null = 没有待加入的。</summary>
        private LanHostInfo _joiningHost;

        /// <summary>"本机自己的房间被剔除"只记一次（每次打开面板重置）。</summary>
        private bool _selfHostLogged;

        public override void BuildLayout(RectTransform root)
        {
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);

            CsUiStyle.CreateLabel("Title", root, "FIND SERVERS", 52, new Vector2(90f, -70f),
                new Vector2(1000f, 68f), TextAnchor.MiddleLeft, CsUiStyle.Text);
            CsUiStyle.CreateBoxRect("TitleBar", root, new Vector2(92f, -142f), new Vector2(520f, 4f), CsUiStyle.Accent);
            _status = CsUiStyle.CreateLabel("Status", root, "Scanning LAN servers...", 24,
                new Vector2(92f, -158f), new Vector2(1500f, 40f), TextAnchor.MiddleLeft, CsUiStyle.Accent);

            CsUiStyle.CreateBoxRect("ListBox", root, new Vector2(ListX, ListY),
                new Vector2(ListWidth, ListHeight), CsUiStyle.ListBg);
            CsUiStyle.CreateLabel("ListHeader", root, "LAN SERVERS", 20,
                new Vector2(ListX + 20f, ListY + 6f), new Vector2(700f, 30f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            var listNode = UIFactory.CreateNode("Rows", root);
            CsUiStyle.AnchoredTopLeft(listNode, new Vector2(ListX + 20f, ListY - 40f),
                new Vector2(RowWidth, ListHeight - 60f));
            _listRoot = listNode;

            CsUiStyle.CreateLabel("Footer", root, FooterText, 18,
                new Vector2(ListX, ListY - ListHeight - 10f), new Vector2(1500f, 30f),
                TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            // 点击回调在 OnOpen 里绑（BuildLayout 里挂的运行时监听器不进预制体，见 CsPanelBase.Bind）
            _refreshButton = CsUiStyle.CreateButton("Btn_Refresh", root, "Refresh",
                new Vector2(ListX, -900f), new Vector2(220f, 56f), null);
            _backButton = CsUiStyle.CreateButton("Btn_Back", root, "Back",
                new Vector2(ListX + 240f, -900f), new Vector2(220f, 56f), null);

            //   它只负责"应答寻服查询"（见 CsLanHost 的类注释），不是"起一个游戏服务端"。
            _hostButton = CsUiStyle.CreateButton(HostButtonName, root, HostButtonIdleText,
                new Vector2(ListX + 480f, -900f), new Vector2(300f, 56f), null);
        }

        public override void OnOpen(object param)
        {
            // 预制体里存不下动态字体（见 CsUiStyle.ApplyOriginalFonts）⇒ 每次打开都重设一遍。
            CsUiStyle.ApplyOriginalFonts(transform);

            WarnIfNull(_status, "状态文本");
            WarnIfNull(_listRoot, "列表容器");
            Bind(_refreshButton, OnRefresh, "Refresh");
            Bind(_backButton, OnBack, "Back");

            // 差异 #88：预制体（`Assets/Resources/UI/ServerListPanel.prefab`）由 FlowSetup 生成，
            //   爆炸半径远超本需求）。预制体里没有 Btn_HostLan 时在这里补一个**与 BuildLayout 同款**的按钮
            //   （同一个 CsUiStyle.CreateButton、同一个节点名/文案/坐标/尺寸）⇒ 下次重新生成时两者收敛。
            if (_hostButton == null)
            {
                _hostButton = CsUiStyle.CreateButton(HostButtonName, transform, HostButtonIdleText,
                    new Vector2(ListX + 480f, -900f), new Vector2(300f, 56f), null);
                if (_hostButton != null)
                    Game.Logger.Info(Tag, "预制体里没有 Host LAN 按钮 → 已按 BuildLayout 同款在运行期补建（差异 #88）");
            }
            Bind(_hostButton, OnHostToggle, "Host LAN Game");
            UpdateHostButton();

            ClearRows();
            _selfHostLogged = false;
            _joiningHost = null;

            // 差异 #88：底注文案的唯一真源是 `FooterText`（<see cref="BuildLayout"/> 也用它）。
            //   所以每次打开按常量覆写一次，两者收敛。
            //   根本不会被调用 ⇒ 屏上会出现"按钮写着 Host LAN Game、底注还写着『单机版不连接任何服务器』"
            var footer = transform.Find("Footer");
            if (footer == null)
            {
                CsUiStyle.CreateLabel("Footer", transform, FooterText, 18,
                    new Vector2(ListX, ListY - ListHeight - 10f), new Vector2(1500f, 30f),
                    TextAnchor.MiddleLeft, CsUiStyle.TextDim);
            }
            else
            {
                var ft = footer.GetComponent<Text>();
                if (ft != null && ft.text != FooterText) ft.text = FooterText;
            }

            var browser = Game.LanBrowser;
            if (browser == null)
            {
                SetStatus("LAN browsing unavailable (Game.LanBrowser is null)");
                Game.Logger.Error(Tag, "Game.LanBrowser 为 null：引擎的局域网寻服未挂接（CloverLan 的 Launch 钩子没跑？）");
                return;
            }

            if (!browser.IsSupported)
            {
                SetStatus($"LAN browsing is not supported on this platform: {browser.UnsupportedReason}");
                Game.Logger.Warn(Tag, $"当前平台不支持寻服：{browser.UnsupportedReason}");
                return;
            }

            browser.OnHostFound += OnHostFound;
            browser.OnScanFinished += OnScanFinished;
            _subscribed = true;

            OnRefresh();
        }

        /// <summary>
        /// 开关本机的局域网应答端（差异 #88）。
        ///
        /// <para>它**不是**"起一个游戏服务端"：只让同网段其他人的 <c>Find Servers</c> 能发现本机。
        /// 广播出去的 <c>gateway</c> = 本工程 <see cref="CsLanGateway"/> 监听的对局网关口（8002），
        /// 该网关只在**本机已经开局**（<c>CsMatch.IsRunning</c>）时才推世界快照。</para>
        /// </summary>
        private void OnHostToggle()
        {
            if (CsLanHost.IsRunning)
            {
                CsLanHost.Stop();
                UpdateHostButton();
                SetStatus("LAN broadcast stopped (this machine is no longer discoverable).");
                return;
            }

            string error;
            var ok = CsLanHost.Start(out error,
                gatewayPort: CsLanHost.DefaultGatewayPort,
                udpPort: CsLanHost.DefaultUdpPort,
                name: HostName,
                players: 1,
                maxPlayers: 10);

            UpdateHostButton();

            if (!ok)
            {
                SetStatus($"Failed to host: {error}");
                Game.UI.Toast($"Failed to host: {error}", 3f);
                return;
            }

            SetStatus($"Broadcasting: {CsLanHost.Describe()} (Refresh on the same subnet should find it)");
            Game.UI.Toast("LAN broadcast started: Find Servers on the same subnet can see this machine.", 3f);
        }

        private void UpdateHostButton()
        {
            if (_hostButton == null) return;
            var label = _hostButton.GetComponentInChildren<Text>();
            if (label == null) return;
            label.text = CsLanHost.IsRunning ? HostButtonRunningText : HostButtonIdleText;
        }

        public override void OnClose()
        {
            var browser = Game.LanBrowser;
            if (_subscribed && browser != null)
            {
                browser.OnHostFound -= OnHostFound;
                browser.OnScanFinished -= OnScanFinished;
            }
            _subscribed = false;

            // 面板关掉时握手还没成 ⇒ 取消这次加入（否则后台读线程会一直连着）。
            if (_joiningHost != null)
            {
                Game.Logger.Info(Tag, "离开服务器列表：取消未完成的加入（" + CsLanClient.Describe() + "）");
                CsLanClient.Stop();
                _joiningHost = null;
            }

            ClearRows();
        }

        private void OnBack()
        {
            // 关掉自己即可：AppFlow 的 ServerList 站点 onTick 检测到面板已关，会自动退回主菜单站点。
            Game.UI.Close<ServerListPanel>();
        }

        private void OnRefresh()
        {
            var browser = Game.LanBrowser;
            if (browser == null || !browser.IsSupported)
            {
                Game.Logger.Warn(Tag, "Refresh 被点击，但局域网寻服不可用，忽略");
                return;
            }

            ClearRows();
            _selfHostLogged = false;
            SetStatus("Scanning LAN servers...");
            Game.Logger.Info(Tag, "发起局域网扫描（LanBrowser.Scan）");
            browser.Scan();
        }

        private void OnHostFound(LanHostInfo host)
        {
            if (host == null)
            {
                Game.Logger.Warn(Tag, "OnHostFound 收到 null 主机，忽略");
                return;
            }

            // 寻服查询会发到 127.0.0.1 与本网段广播，本机应答端也会答 ⇒ 不做这一步时
            // 列表里一定出现"我自己开的房"，点进去还会连回自己。
            if (CsLanHost.IsSelfHost(host))
            {
                if (!_selfHostLogged)
                {
                    _selfHostLogged = true;
                    Game.Logger.Info(Tag,
                        $"发现的主机 {host.Address} 就是本机自己开的（gateway={CsLanHost.GatewayPort}）⇒ 已从列表剔除；" +
                        "要从另一台机器加入才是真的联机");
                }
                return;
            }

            if (_listRoot == null)
            {
                Game.Logger.Warn(Tag, $"列表容器缺失，主机 {host.Address} 未能显示");
                return;
            }

            // 本机自己开的房也会收到自己的广播（引擎发现端不做自我过滤）⇒ 列表里会冒出一条
            // 指向自己的房，点进去等于连回自己。这里按地址剔除。
            if (IsLocalHost(host))
            {
                Game.Logger.Info(Tag, $"忽略本机自己的广播：{host.Address}（列表不显示自己的房间）");
                return;
            }

            var index = _rows.Count;
            var label = string.IsNullOrEmpty(host.Name) ? host.Host : host.Name;
            var players = host.MaxPlayers > 0 ? $"{host.Players}/{host.MaxPlayers}" : host.Players.ToString();
            var rowText = $"{label}          {host.Address}          players {players}";

            var button = CsUiStyle.CreateButton($"Host_{index}", _listRoot, rowText,
                new Vector2(0f, -index * RowStep), new Vector2(RowWidth, RowHeight),
                () => OnHostClicked(host));
            if (button != null) _rows.Add(button.gameObject);

            Game.Logger.Info(Tag,
                $"发现局域网主机: address={host.Address} name={host.Name} players={host.Players}/{host.MaxPlayers}");
        }

        /// <summary>这条广播是不是本机自己发的（回环地址或本机任一 IPv4）。</summary>
        private static bool IsLocalHost(LanHostInfo host)
        {
            var addr = host.Address ?? string.Empty;
            var colon = addr.LastIndexOf(':');
            var ip = colon > 0 ? addr.Substring(0, colon) : addr;
            if (ip == "127.0.0.1" || ip == "::1" || ip == "localhost") return true;

            try
            {
                var local = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName());
                for (var i = 0; i < local.Length; i++)
                {
                    if (local[i].ToString() == ip) return true;
                }
            }
            catch (System.Exception ex)
            {
                Game.Logger.Warn(Tag, $"取本机 IP 失败，无法过滤自己的房间：{ex.Message}");
            }
            return false;
        }

        private void OnScanFinished()
        {
            var browser = Game.LanBrowser;
            var answered = browser?.Hosts?.Count ?? 0;
            var count = _rows.Count;      // 已剔除"本机自己"的可点行数
            if (count == 0)
            {
                if (answered > 0)
                {
                    SetStatus("Only this machine's own LAN host answered — start a second instance on another machine");
                    Game.Logger.Info(Tag,
                        $"局域网扫描结束：收到 {answered} 条应答，但全部是本机自己的房间 ⇒ 列表为空");
                    return;
                }

                SetStatus("No LAN servers found");
                Game.Logger.Info(Tag,
                    "局域网扫描结束：0 台主机。⛔ 这不是 bug 也不是降级 —— 是**本轮真实没有应答**：" +
                    "同网段要有人开了应答端（本面板的 Host LAN Game，或与引擎 LanProtocol 同协议的其它主机）才扫得到。" +
                    // 协议魔数与端口**取自引擎真源**（不写字面量）：`LanProtocol.QueryMagic` / `ReplyMagic` /
                    // `DefaultPort`（`Runtime/Network/Lan/LanProtocol.cs`）。`CsLanHost.DiscoveryPort` 本身即
                    // 该常量，故端口这一句天然同源；此处只把两个魔数也换成常量引用（渲染文本逐字不变）。
                    "协议/端口：" + LanProtocol.QueryMagic + " + " + LanProtocol.ReplyMagic +
                    "，UDP " + CsLanHost.DiscoveryPort);
            }
            else
            {
                SetStatus($"Found {count} server(s)");
                Game.Logger.Info(Tag, $"局域网扫描结束：{count} 台主机（应答 {answered} 条，已剔除本机自己）");
            }
        }

        private void OnHostClicked(LanHostInfo host)
        {
            if (host == null)
            {
                Game.Logger.Warn(Tag, "OnHostClicked 收到 null 主机，忽略");
                return;
            }

            if (CsLanHost.IsSelfHost(host))
            {
                SetStatus($"This is this machine's own LAN host ({host.Address}) — join it from another machine");
                Game.Logger.Warn(Tag, $"点到了本机自己开的房（{host.Address}）⇒ 不连接：本机既是主机又是客机会看到自己的角色");
                return;
            }

            // 加入只走本工程的局域网对局客户端（CS16-LAN-JOIN/1 → WELCOME → SNAP）。
            // ⛔ 不调 CloverNet.Init：那是引擎的通用 TCP 网络模块（服务端网关协议），
            //    与本协议不是一套；它会在这台网关上多开一条永远不握手的连接。
            var playerName = CsPlayerSettingsStore.Load().PlayerName;
            string lanErr;
            Game.Logger.Info(Tag,
                $"加入局域网主机：name=\"{host.Name}\" addr={host.Address} player=\"{playerName}\" → {CsLanGateway.JoinMagic}");
            SetStatus($"Joining {host.Address} as \"{playerName}\" (waiting for {CsLanGateway.WelcomeMagic})...");

            var lanOk = CsLanClient.Start(host.Address, playerName, out lanErr);
            if (!lanOk)
            {
                // 非预期分支：必须留痕 + 把原因摆到状态行上（不许"点了没反应"）
                Game.Logger.Error(Tag, $"局域网客户端启动失败（{host.Address}）：{lanErr}");
                SetStatus($"Join failed: {lanErr}");
                Game.UI.Toast($"Join failed: {lanErr}", 3f);
                return;
            }

            _joiningHost = host;
            Game.UI.Toast($"Connecting to {host.Address}...", 3f);
        }

        /// <summary>
        /// 每帧看一眼待加入的握手结果：**真的握手成功**（<see cref="CsLanClient.Joined"/>）才进图，
        /// 失败 / 超时就把原因摆到状态行（<see cref="CsLanClient"/> 自己也会在 JoinTimeoutMs 收尾）。
        /// </summary>
        public override void OnUpdate(float dt)
        {
            if (_joiningHost == null) return;

            if (CsLanClient.Joined)
            {
                var host = _joiningHost;
                _joiningHost = null;
                EnterHostMatch(host);
                return;
            }

            if (!CsLanClient.IsRunning)
            {
                var err = CsLanClient.LastError;
                if (string.IsNullOrEmpty(err)) err = "连接已关闭（主机拒绝 / 网络中断）";
                _joiningHost = null;
                SetStatus($"Join failed: {err}");
                Game.Logger.Error(Tag, $"加入 {CsLanClient.Endpoint} 失败：{err}");
                Game.UI.Toast($"Join failed: {err}", 3f);
            }
        }

        /// <summary>
        /// 握手完成后进图：客户端本地开一局（**bots=0**，主机推来的 actors 由远端视图画），
        /// 这样 <c>MatchModule.Update</c> 才会跑 <c>CsLanClient.Pump</c> / <c>CsLanRemoteView.SyncAll</c>。
        /// </summary>
        private void EnterHostMatch(LanHostInfo host)
        {
            var settings = CsPlayerSettingsStore.Load();
            var cfg = new CsMatchConfig();
            cfg.BotsPerTeam = 0;                 // 主机上的机器人与玩家都从快照来，客户端不再生成自己的
            cfg.PlayerName = settings.PlayerName;
            cfg.PlayerTeam = CsTeam.CT;          // 真实阵营由选阵营面板定（Events.TeamChosen）
            cfg.ClientMode = CsLanClient.ServerDriven;   // 已握手 ⇒ 本机是客机：回合/比分跟主机快照

            SetStatus($"Joined {host.Address} — loading {cfg.MapName}");
            Game.Logger.Info(Tag,
                $"已加入 {host.Address}（{CsLanClient.Describe()}）⇒ 进图 {cfg.MapName}，" +
                $"本机 bots={cfg.BotsPerTeam}，客机模式={cfg.ClientMode}，远端角色由 {CsLanClient.Snapshots} 条快照驱动");
            Game.Event.Emit(Events.LaunchMatch, cfg);
        }

        private void SetStatus(string text)
        {
            if (_status == null) return;
            _status.text = text;
        }

        private void ClearRows()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] != null) Object.Destroy(_rows[i]);
            }
            _rows.Clear();
        }
    }
}
