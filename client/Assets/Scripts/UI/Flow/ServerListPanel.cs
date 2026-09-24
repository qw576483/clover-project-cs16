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
    /// <b>2026-09-24 起不再是"恒 0 台"</b>（用户本轮第 4 条：<i>「我们是一定要支持局域网联机！」</i>，
    /// 差异 #88）：
    /// <list type="number">
    /// <item><b>问的一端</b>：引擎 <c>Game.LanBrowser</c> 一直可用（扫描/收应答/出列表）—— 这部分没动过；</item>
    /// <item><b>答的一端</b>：旧实现**根本没有**（引擎里只有测试用的回环应答器，Go 服务端 <c>CLOVER-LAN</c> 零命中）
    /// ⇒ <c>Find Servers</c> 恒为 0 台的真实原因是**没人应答**，不是"没扫到"。本轮补上本工程的应答端
    /// <see cref="Cs16.Module.Net.CsLanHost"/>，并给了面板上一个 <c>Host LAN Game</c> 开关；</item>
    /// <item><b>点主机</b>：旧实现明确"单机版不去连"（只 Warn + Toast）；现在改为**真的**走
    /// <c>CloverNet.Init(host.Address, host.UdpAddress)</c>（非阻塞，连接结果由网络模块回调反馈），
    /// 并保留一条 Info 日志 + 面板状态行，⛔ 不留"点了没反应"。</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>2026-09-24 第②段（片LAN-D）</b>：点主机时除了 <c>CloverNet.Init</c>，**另外**起本工程自己的
    /// <see cref="CsLanClient"/> —— 它消费主机推来的 <c>CS16-LAN-SNAP/1</c> 世界快照，
    /// 由 <c>Module/View/CsLanRemoteView</c> 把远端角色画到场上（"收到数据 ≠ 看得见人"那一半）。
    /// 启动失败会 <c>Game.Logger.Error</c> 留痕并把原因写到状态行（⛔ 不留"点了没反应"）。</para>
    ///
    /// <para><b>仍未消除</b>（如实标注，⛔ 不吹）：<b>远端输入还没驱动主机模拟</b> ——
    /// 本机自己的走位/开枪还没有经 <c>CS16-LAN-INPUT/1</c> 交给主机（那是"能开局"的第③段，另开片）；
    /// 远端角色在本机**只到"看得见"这一层**（不参与碰撞 / 命中 / 音频，见 <c>CsLanRemoteView</c> 的类注释）。
    /// 所以现在能证明的是"能连上 + 能看见主机的世界在动"，不是"两个人已经能对打"。</para>
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
        /// ⛔ 不许在补丁脚本里另抄一份字面量 —— 抄了就会和这里漂移）。</summary>
        /// <remarks>⚠️ 里面那个 <c>47777</c> 是**面向玩家的展示文案**，对应引擎真源
        /// <c>LanProtocol.DefaultPort</c>（<c>Runtime/Network/Lan/LanProtocol.cs:48</c>）。
        /// 它**不能**写成常量拼接：C# 的 <c>const string</c> 只接受「string + string」，
        /// <c>"…" + LanProtocol.DefaultPort</c>（int）不是常量表达式 ⇒ 只能保持字面量，
        /// 或把本字段降级成 <c>static readonly</c>（那会改动预制体补丁脚本的契约，代价更大）。
        /// ⇒ 若引擎改了默认端口，**这里必须同步改**（判据：本类里出现的端口字面量只有这一处）。</remarks>
        public const string FooterText =
            "刷新 = 真实发起一轮局域网扫描（UDP 47777）；Host LAN Game = 本机对外应答同网段的寻服查询。";

        /// <summary>「开主机」按钮的两种文案（同上：public const 供预制体补丁脚本使用）。</summary>
        public const string HostButtonIdleText = "Host LAN Game";

        /// <summary>应答端在跑时的按钮文案。</summary>
        public const string HostButtonRunningText = "Stop LAN Host";

        /// <summary>「开主机」按钮的节点名（补丁脚本与 <see cref="BuildLayout"/> 必须一致）。</summary>
        public const string HostButtonName = "Btn_HostLan";

        /// <summary>广播出去的主机名（含机器名，便于同网段两台机器区分）。</summary>
        public static string HostName
        {
            get { return $"{CsLanHost.DefaultName} @ {System.Environment.MachineName}"; }
        }

        private readonly List<GameObject> _rows = new List<GameObject>();
        private bool _subscribed;

        public override void BuildLayout(RectTransform root)
        {
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);

            CsUiStyle.CreateLabel("Title", root, "FIND SERVERS", 52, new Vector2(90f, -70f),
                new Vector2(1000f, 68f), TextAnchor.MiddleLeft, CsUiStyle.Text);
            CsUiStyle.CreateBoxRect("TitleBar", root, new Vector2(92f, -142f), new Vector2(520f, 4f), CsUiStyle.Accent);
            _status = CsUiStyle.CreateLabel("Status", root, "正在扫描局域网服务器…", 24,
                new Vector2(92f, -158f), new Vector2(1500f, 40f), TextAnchor.MiddleLeft, CsUiStyle.Accent);

            CsUiStyle.CreateBoxRect("ListBox", root, new Vector2(ListX, ListY),
                new Vector2(ListWidth, ListHeight), CsUiStyle.Box);
            CsUiStyle.CreateLabel("ListHeader", root, "局域网服务器 / LAN Servers", 20,
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
                new Vector2(ListX, -900f), new Vector2(220f, 56f), null, accent: true);
            _backButton = CsUiStyle.CreateButton("Btn_Back", root, "Back",
                new Vector2(ListX + 240f, -900f), new Vector2(220f, 56f), null);

            // ★ 差异 #88（用户 2026-09-24：「我们是一定要支持局域网联机！」）：本机开一台**可被同网段发现**的主机。
            //   它只负责"应答寻服查询"（见 CsLanHost 的类注释），⛔ 不是"起一个游戏服务端"。
            _hostButton = CsUiStyle.CreateButton(HostButtonName, root, HostButtonIdleText,
                new Vector2(ListX + 480f, -900f), new Vector2(300f, 56f), null);
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_status, "状态文本");
            WarnIfNull(_listRoot, "列表容器");
            Bind(_refreshButton, OnRefresh, "Refresh");
            Bind(_backButton, OnBack, "Back");

            // ★ 差异 #88：预制体（`Assets/Resources/UI/ServerListPanel.prefab`）由 FlowSetup 生成，
            //   本轮**不重跑生成器**（那会连带重写 8 个预制体 + Boot/Menu 两个场景 + Build Settings，
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

            // ★ 差异 #88：底注文案的唯一真源是 `FooterText`（<see cref="BuildLayout"/> 也用它）。
            //   预制体里那一份是 **FlowSetup 生成时烘进去的旧串** —— 本轮不重跑生成器（不重写预制体），
            //   所以每次打开按常量覆写一次，两者收敛。
            //   ⚠️ 踩过的坑（本片实测截到）：只改 `BuildLayout` **不够** —— 预制体已存在时 `BuildLayout`
            //   根本不会被调用 ⇒ 屏上会出现"按钮写着 Host LAN Game、底注还写着『单机版不连接任何服务器』"
            //   这种自相矛盾（截图 `22_serverlist.png` 就是它）。⛔ 别把修复只留在 BuildLayout 里。
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
                SetStatus("局域网寻服不可用（Game.LanBrowser 为 null）");
                Game.Logger.Error(Tag, "Game.LanBrowser 为 null：引擎的局域网寻服未挂接（CloverLan 的 Launch 钩子没跑？）");
                return;
            }

            if (!browser.IsSupported)
            {
                SetStatus($"本平台不支持局域网寻服：{browser.UnsupportedReason}");
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
        /// <para>⛔ 它**不是**"起一个游戏服务端"：只让同网段其他人的 <c>Find Servers</c> 能发现本机。
        /// 真正能连上的前提是 <c>gateway</c> 指向一台在监听的真实网关（<c>CloverNet.Init</c> 的目标）。</para>
        /// </summary>
        private void OnHostToggle()
        {
            if (CsLanHost.IsRunning)
            {
                CsLanHost.Stop();
                UpdateHostButton();
                SetStatus("已停止局域网广播（本机不再被同网段发现）");
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
                SetStatus($"开主机失败：{error}");
                Game.UI.Toast($"开主机失败：{error}", 3f);
                return;
            }

            SetStatus($"本机已广播：{CsLanHost.Describe()}（同网段其他玩家点 Refresh 应能发现）");
            Game.UI.Toast("已开启局域网广播：同网段的 Find Servers 应能发现本机", 3f);
        }

        /// <summary>把按钮文案同步成应答端的真实状态（⛔ 不靠一个本地 bool 记状态，两个来源会漂移）。</summary>
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
            SetStatus("正在扫描局域网服务器…");
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

            if (_listRoot == null)
            {
                Game.Logger.Warn(Tag, $"列表容器缺失，主机 {host.Address} 未能显示");
                return;
            }

            var index = _rows.Count;
            var label = string.IsNullOrEmpty(host.Name) ? host.Host : host.Name;
            var players = host.MaxPlayers > 0 ? $"{host.Players}/{host.MaxPlayers}" : host.Players.ToString();
            var rowText = $"{label}          {host.Address}          玩家 {players}";

            var button = CsUiStyle.CreateButton($"Host_{index}", _listRoot, rowText,
                new Vector2(0f, -index * RowStep), new Vector2(RowWidth, RowHeight),
                () => OnHostClicked(host));
            if (button != null) _rows.Add(button.gameObject);

            Game.Logger.Info(Tag,
                $"发现局域网主机: address={host.Address} name={host.Name} players={host.Players}/{host.MaxPlayers}");
        }

        private void OnScanFinished()
        {
            var browser = Game.LanBrowser;
            var count = browser?.Hosts?.Count ?? 0;
            if (count == 0)
            {
                SetStatus("未找到局域网服务器");
                Game.Logger.Info(Tag,
                    "局域网扫描结束：0 台主机。⛔ 这不是 bug 也不是降级 —— 是**本轮真实没有应答**：" +
                    "同网段要有人开了应答端（本面板的 Host LAN Game，或与引擎 LanProtocol 同协议的其它主机）才扫得到。" +
                    // 协议魔数与端口**取自引擎真源**（⛔ 不写字面量）：`LanProtocol.QueryMagic` / `ReplyMagic` /
                    // `DefaultPort`（`Runtime/Network/Lan/LanProtocol.cs`）。`CsLanHost.DiscoveryPort` 本身即
                    // 该常量，故端口这一句天然同源；此处只把两个魔数也换成常量引用（渲染文本逐字不变）。
                    "协议/端口：" + LanProtocol.QueryMagic + " + " + LanProtocol.ReplyMagic +
                    "，UDP " + CsLanHost.DiscoveryPort);
            }
            else
            {
                SetStatus($"找到 {count} 台主机");
                Game.Logger.Info(Tag, $"局域网扫描结束：{count} 台主机");
            }
        }

        private void OnHostClicked(LanHostInfo host)
        {
            // ★ 差异 #88（用户 2026-09-24：「我们是一定要支持局域网联机！」）：旧实现到这里只 Warn + Toast
            //   （"单机版不连接任何服务器"）；现在**真的**发起连接 —— 走引擎的官方入口
            //   `CloverNet.Init(host.Address, host.UdpAddress)`（非阻塞：连上/失败由 Net.OnConnected /
            //   OnConnectFailed 反馈，见 Contracts.cs 的 INetwork）。地址直接用 LanHostInfo 的两个属性，
            //   ⛔ 不在这里自己拼串（那两个属性就是为了"能直接喂给 CloverNet.Init"而存在的）。
            var udp = string.IsNullOrEmpty(host.UdpAddress) ? null : host.UdpAddress;

            Game.Logger.Info(Tag,
                $"加入局域网主机：name=\"{host.Name}\" addr={host.Address} udp={udp ?? "未提供"} → CloverNet.Init（非阻塞）");
            SetStatus($"正在连接局域网服务器 {host.Address}…");

            CloverNet.Init(host.Address, udp);

            var attached = Game.Net != null;
            Game.Logger.Info(Tag,
                attached
                    ? "网络模块已挂接（Game.Net != null）：连接结果由 Net.OnConnected / Net.OnConnectFailed 反馈"
                    : "CloverNet.Init 没有挂上网络模块（Game.Net 仍为 null）—— 看同批日志里 Network 域的 Error 行");

            // ★ 差异 #88「能开局」第②段（片LAN-D）：**另外**起本工程自己的局域网客户端。
            //   两件事互不替代：CloverNet 是引擎的通用网络连接（本工程还没有"联网比赛模式"），
            //   而 CsLanClient 消费主机推来的 CS16-LAN-SNAP/1 世界快照、把远端角色画到场上
            //   （那是"能开局"看得见的那一半）。地址直接用 host.Address（= "host:gatewayPort"，
            //   ⛔ 不在这里自己拼串 —— 那个属性就是为"能直接喂给连接入口"而存在的）。
            string lanErr;
            var playerName = CsPlayerSettingsStore.Load().PlayerName;
            var lanOk = CsLanClient.Start(host.Address, playerName, out lanErr);
            if (!lanOk)
            {
                // 非预期分支：必须留痕 + 把原因摆到状态行上（⛔ 不许"点了没反应"）
                Game.Logger.Error(Tag, $"局域网客户端启动失败（{host.Address}）：{lanErr}");
                SetStatus($"加入失败：{lanErr}");
                Game.UI.Toast($"加入失败：{lanErr}", 3f);
                return;
            }

            SetStatus($"已连上 {host.Address}（玩家名 \"{playerName}\"）：{CsLanClient.Describe()}");
            Game.UI.Toast(attached ? $"正在连接 {host.Address}…" : $"无法连接 {host.Address}（网络模块未挂接）", 3f);
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
