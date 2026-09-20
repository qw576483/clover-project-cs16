using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 局域网服务器列表（对应 CS 1.6 主菜单的 "Find Servers"）：真实调 <see cref="ILanBrowser"/>。
    ///
    /// <para>
    /// **单机版说明（重要，别当成 bug）**：本工程按设计不调用 <c>CloverNet 的网络初始化</c>
    /// （见 <c>docs/步骤文档.md</c> §3.1），所以扫描通常得到 0 台主机 —— 这是**真实的扫描结果**，
    /// 不是"假数据/降级"。点主机也不会去连（连了就违背单机的契约），但会给明确反馈 + 日志，不留"点了没反应"。
    /// </para>
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

            CsUiStyle.CreateLabel("Footer", root,
                "刷新 = 真实发起一轮局域网扫描；单机版不连接任何服务器（不调 CloverNet 的网络初始化）。", 18,
                new Vector2(ListX, ListY - ListHeight - 10f), new Vector2(1500f, 30f),
                TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            // 点击回调在 OnOpen 里绑（BuildLayout 里挂的运行时监听器不进预制体，见 CsPanelBase.Bind）
            _refreshButton = CsUiStyle.CreateButton("Btn_Refresh", root, "Refresh",
                new Vector2(ListX, -900f), new Vector2(220f, 56f), null, accent: true);
            _backButton = CsUiStyle.CreateButton("Btn_Back", root, "Back",
                new Vector2(ListX + 240f, -900f), new Vector2(220f, 56f), null);
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_status, "状态文本");
            WarnIfNull(_listRoot, "列表容器");
            Bind(_refreshButton, OnRefresh, "Refresh");
            Bind(_backButton, OnBack, "Back");

            ClearRows();

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
                Game.Logger.Info(Tag, "局域网扫描结束：0 台主机（单机版属预期真实结果）");
            }
            else
            {
                SetStatus($"找到 {count} 台主机");
                Game.Logger.Info(Tag, $"局域网扫描结束：{count} 台主机");
            }
        }

        private void OnHostClicked(LanHostInfo host)
        {
            // 不静默：点了要有反馈。单机版不调 CloverNet 的网络初始化（contract R3），所以不能真去连。
            Game.Logger.Warn(Tag, $"单机版不支持加入局域网主机 {host.Address}（按设计不调用 CloverNet 的网络初始化）");
            Game.UI.Toast($"单机版不支持加入服务器（{host.Address}）", 2.5f);
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
