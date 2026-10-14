using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 记分板（规格 G11，按住 <c>TAB</c> 显示）：按阵营分组，列出
    /// <c>玩家名 | HP | Money | Score | Deaths | Latency</c>，自己那行高亮、死亡者变灰，
    /// 底部显示比分与回合数。
    ///
    /// <para><b>层</b>：<see cref="UILayer.Normal"/> —— 它是"按住 TAB 看一眼"的信息板，
    /// 不该抢输入（<c>Popup</c> 会把开着的买枪菜单顶掉，也会让 ESC 误判成"有遮挡"）。</para>
    ///
    /// <para><b>数据源</b>：<see cref="CsHudSnapshot.Roster"/>（比赛模块每帧填的逐人花名册：
    /// 名字 / 阵营 / 击杀 / 死亡 / 存活 / 金钱 / 血量 / 是否机器人）。花名册为空时退回
    /// <see cref="CsIngameStats"/> 的击杀累计（<c>Game.UI.Get&lt;HudPanel&gt;().BuildScoreRows(...)</c>）。</para>
    ///
    /// <para><b>列落点（原版实测）</b>：载体 <c>原版资源/参照物/hud/hud_scoreboard_tab.png</c>（640×360）。
    /// 外框 = 左右边框 x=42 / x=596、上边框 y=47、下边框 y=307 ⇒ 555×260 @360 ⇒ <b>1665×780 @1080</b>；
    /// 数据行行距 15.7 @360 ⇒ <b>47 @1080</b>。数值列一律"值右缘 = 表头右缘"：
    /// HP 右缘 x=371、Money x=431、Score x=479、Deaths x=529、Latency x=581 @640
    /// ⇒ 下表常量 = (原版 x − 42) × 3（再减 <see cref="RowRootX"/> 换成行内坐标）。
    /// 死亡者在 HP 那一格显示左对齐的 <c>Dead</c>（原版 x[271..293]），活的显示右对齐的血量数字。</para>
    ///
    /// <para><b>延迟列</b>：单机版本地模拟，没有网络往返 ⇒ 自己显示 <c>0</c>，机器人显示 <c>BOT</c>
    /// （机器人不是网络实体，编一个 ping 值才是造假）。</para>
    /// </summary>
    public class ScoreboardPanel : CsPanelBase
    {
        // 外框：原版 555×260 @360（x[42..596] / y[47..307]）
        private const float BoxWidth = 1665f;
        private const float BoxHeight = 780f;
        private const float RowHeight = 47f;      // 原版数据行行距 15.7 @360
        private const float HeaderHeight = 47f;   // 组标题占一整行
        private const int MaxRows = 24;

        /// <summary>行区域相对对话框左边缘的偏移（表头与数据行必须共用它才能对齐）。</summary>
        private const float RowRootX = 24f;

        // 列（相对行左边缘的 x；数值列右对齐 ⇒ 值右缘 = 表头右缘 = x + ColNumWidth）
        private const float ColName = 72f;       // 名字左缘 = 面板左缘 + 96 @1080（原版 x=74）
        private const float ColHp = 743f;        // 数值右缘 963 行内（= 面板左缘 + 987，原版 HP 右缘 x=371）
        private const float ColHpDead = 663f;    // Dead 左对齐（= 面板左缘 + 687，原版 x=271）
        private const float ColMoney = 923f;     // 右缘 1143（原版 x=431）
        private const float ColScore = 1067f;    // 右缘 1287（原版 x=479）
        private const float ColDeaths = 1217f;   // 右缘 1437（原版 x=529）
        private const float ColPing = 1373f;     // 右缘 1593（原版 x=581）
        private const float ColNumWidth = 220f;
        private const float ColNameWidth = 520f;

        [SerializeField] private Text _titleText;
        [SerializeField] private Text _scoreText;
        [SerializeField] private Text _roundText;
        [SerializeField] private Text _footerText;
        [SerializeField] private RectTransform _rowRoot;
        /// <summary>表头各列（与数据行用**同一套 x 偏移**，否则比例字体下"表头对不上列"）。</summary>
        [SerializeField] private Text[] _headerCells;

        private readonly List<CsScoreRow> _rows = new List<CsScoreRow>(MaxRows);
        private readonly List<Row> _pool = new List<Row>(MaxRows);
        private readonly List<GroupHeader> _groupPool = new List<GroupHeader>(3);
        private bool _warnedRefs;

        private sealed class Row
        {
            public RectTransform Rt;
            public Image Bg;
            public Text Name;
            public Text Hp;
            public Text HpDead;
            public Text Money;
            public Text Score;
            public Text Deaths;
            public Text Ping;
        }

        private sealed class GroupHeader
        {
            public RectTransform Rt;
            public Text Label;
        }

        public override UILayer Layer => UILayer.Normal;

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.35f), false);
            UIFactory.Stretch(backdrop.rectTransform);

            var box = UIFactory.CreatePanel("Dialog", root, CsUiStyle.Box, false);
            CsHudTheme.PlaceCenter(box.rectTransform, Vector2.zero, new Vector2(BoxWidth, BoxHeight));
            var boxRt = box.rectTransform;

            _titleText = CsHudTheme.CreateText("Title", boxRt, "SCOREBOARD", 30, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(_titleText.rectTransform, new Vector2(24f, -16f), new Vector2(700f, 40f));

            _roundText = CsHudTheme.CreateText("Round", boxRt, "第 1 回合", 22, TextAnchor.MiddleRight,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceTopRight(_roundText.rectTransform, new Vector2(-24f, -16f), new Vector2(420f, 40f));

            _scoreText = CsHudTheme.CreateText("Score", boxRt, "CT 0 : 0 T", 32, TextAnchor.MiddleCenter,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopCenter(_scoreText.rectTransform, new Vector2(0f, -64f), new Vector2(600f, 44f));

            // 表头：每列单独一个 Text，x 偏移与数据行完全一致（比例字体下用空格对齐是假的）
            var headers = new[] { "玩家名", "HP", "Money", "Score", "Deaths", "Latency" };
            var xs = new[] { ColName, ColHp, ColMoney, ColScore, ColDeaths, ColPing };
            var widths = new[] { ColNameWidth, ColNumWidth, ColNumWidth, ColNumWidth, ColNumWidth, ColNumWidth };
            var anchors = new[]
            {
                TextAnchor.MiddleLeft, TextAnchor.MiddleRight, TextAnchor.MiddleRight,
                TextAnchor.MiddleRight, TextAnchor.MiddleRight, TextAnchor.MiddleRight,
            };

            _headerCells = new Text[headers.Length];
            for (var i = 0; i < headers.Length; i++)
            {
                var cell = CsHudTheme.CreateText($"Head{i}", boxRt, headers[i], 19, anchors[i],
                    CsHudTheme.TextDim);
                CsHudTheme.PlaceTopLeft(cell.rectTransform,
                    new Vector2(RowRootX + xs[i], -116f), new Vector2(widths[i], 40f));
                _headerCells[i] = cell;
            }

            var rowRoot = UIFactory.CreateNode("Rows", boxRt);
            CsHudTheme.PlaceTopLeft(rowRoot, new Vector2(RowRootX, -160f),
                new Vector2(BoxWidth - RowRootX * 2f, BoxHeight - 216f));
            _rowRoot = rowRoot;

            _footerText = CsHudTheme.CreateText("Footer", boxRt, string.Empty, 18, TextAnchor.MiddleLeft,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceBottomLeft(_footerText.rectTransform, new Vector2(24f, 14f),
                new Vector2(BoxWidth - 48f, 44f));
        }

        public override void OnOpen(object param)
        {
            if (!_warnedRefs)
            {
                _warnedRefs = true;
                if (_rowRoot == null || _scoreText == null)
                    Game.Logger?.Error(Tag, "ScoreboardPanel 引用缺失（预制体未由 UiBuilder 生成或已被改动），记分板可能空白");
            }
            Refresh();
        }

        public override void OnUpdate(float dt)
        {
            Refresh();
        }

        // ═══════════════════════ 刷新 ═══════════════════════

        private void Refresh()
        {
            var hud = Game.UI?.Get<HudPanel>();
            var roster = CsHudSnapshot.Roster;

            if (roster.Count == 0)
            {
                // 花名册没填（不在局内，或比赛模块尚未发布）⇒ 退回 HUD 侧的击杀累计
                if (hud == null)
                {
                    // HUD 不在 = 不在局内（或 HUD 被异常关掉）。这里必须留痕，并且明确显示"拿不到数据"
                    if (!_noHudWarned)
                    {
                        _noHudWarned = true;
                        Game.Logger?.Error(Tag,
                            "记分板拿不到花名册也拿不到 HudPanel —— 只有在比赛进行中才有数据");
                    }
                    _rows.Clear();
                }
                else
                {
                    _noHudWarned = false;
                    hud.BuildScoreRows(_rows);
                }
            }
            else
            {
                _noHudWarned = false;
                BuildRowsFromRoster(roster);
            }

            if (_scoreText != null) _scoreText.text = $"CT {CsHudSnapshot.ScoreCT}  :  {CsHudSnapshot.ScoreT} T";
            if (_roundText != null) _roundText.text = $"第 {CsHudSnapshot.RoundNumber} 回合　·　{TABHint()}";
            if (_titleText != null) _titleText.color = CsHudTheme.TeamColor(CsHudSnapshot.Team);

            if (_footerText != null)
            {
                _footerText.text = _rows.Count == 0
                    ? "本场还没有花名册（比赛未开始或已结束）。"
                    : "统计口径：与比赛模块同源的花名册（含机器人与阵亡者）；延迟在单机版为自己 0、机器人 BOT。";
            }

            LayoutRows();
        }

        private bool _noHudWarned;

        private static string TABHint() => "松开 TAB 关闭";

        /// <summary>把快照花名册搬成界面行（自己那行永远排本队第一）。</summary>
        private void BuildRowsFromRoster(List<CsScoreRosterItem> roster)
        {
            _rows.Clear();
            for (var i = 0; i < roster.Count; i++)
            {
                var item = roster[i];
                _rows.Add(new CsScoreRow
                {
                    Name = item.Name,
                    Team = item.Team,
                    Kills = item.Kills,
                    Deaths = item.Deaths,
                    Alive = item.Alive,
                    Money = item.Money,
                    IsSelf = item.IsSelf,
                    Health = item.Health,
                    IsBot = item.IsBot,
                    Latency = item.Latency,
                });
            }
            _rows.Sort(Compare);
        }

        private static int Compare(CsScoreRow a, CsScoreRow b)
        {
            // **先按阵营分组**（记分板要求"两组各一行表头"，自己那行插在队里，不能把队拆开）
            // 组序：CT → T → 观察者（与 HUD 顶部比分的 CT 在左一致）
            var rankA = TeamRank(a.Team);
            var rankB = TeamRank(b.Team);
            if (rankA != rankB) return rankA.CompareTo(rankB);

            // 组内：自己在前 → 击杀多在前 → 死亡少在前 → 名字
            if (a.IsSelf != b.IsSelf) return a.IsSelf ? -1 : 1;
            if (a.Kills != b.Kills) return b.Kills.CompareTo(a.Kills);
            if (a.Deaths != b.Deaths) return a.Deaths.CompareTo(b.Deaths);
            return string.CompareOrdinal(a.Name, b.Name);
        }

        private static int TeamRank(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.CT: return 0;
                case CsTeam.T: return 1;
                default: return 2;
            }
        }

        private void LayoutRows()
        {
            if (_rowRoot == null) return;

            var slot = 0;         // 屏幕上的第几行（组标题也占一行）
            var rowUsed = 0;      // 用了池里几个数据行
            var groupUsed = 0;    // 用了池里几个组标题
            CsTeam? currentGroup = null;

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];

                // 阵营变了 → 插一条组标题（记分板要求"两组各一行表头"）
                if (currentGroup == null || currentGroup.Value != row.Team)
                {
                    currentGroup = row.Team;
                    var header = GetGroupHeader(groupUsed);
                    if (header != null)
                    {
                        groupUsed++;
                        header.Rt.gameObject.SetActive(true);
                        CsHudTheme.PlaceTopLeft(header.Rt, new Vector2(0f, -slot * RowHeight),
                            new Vector2(BoxWidth - 48f, HeaderHeight));
                        header.Label.text = $"—— {GroupTitle(row.Team)} ——";
                        header.Label.color = CsHudTheme.TeamColor(row.Team);
                    }
                    slot++;
                }

                var ui = GetRow(rowUsed);
                if (ui != null)
                {
                    rowUsed++;
                    ui.Rt.gameObject.SetActive(true);
                    CsHudTheme.PlaceTopLeft(ui.Rt, new Vector2(0f, -slot * RowHeight),
                        new Vector2(BoxWidth - 48f, RowHeight));
                    ApplyRow(ui, row);
                }
                slot++;
            }

            // 池子复用：本帧没用到的行/组标题收起来（不销毁，避免每帧新建）
            for (var i = rowUsed; i < _pool.Count; i++)
            {
                if (_pool[i].Rt.gameObject.activeSelf) _pool[i].Rt.gameObject.SetActive(false);
            }
            for (var i = groupUsed; i < _groupPool.Count; i++)
            {
                if (_groupPool[i].Rt.gameObject.activeSelf) _groupPool[i].Rt.gameObject.SetActive(false);
            }
        }

        private static string GroupTitle(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.CT: return "Counter-Terrorists（CT）";
                case CsTeam.T: return "Terrorists（T）";
                default: return "Spectators（观察者）";
            }
        }

        private void ApplyRow(Row ui, in CsScoreRow row)
        {
            var dead = !row.Alive;
            var nameColor = dead ? CsHudTheme.Disabled : CsHudTheme.TeamColor(row.Team);
            if (row.IsSelf)
            {
                ui.Name.text = $"{row.Name}（你）";
                ui.Name.color = dead ? CsHudTheme.Disabled : CsHudTheme.TextMain;
            }
            else
            {
                ui.Name.text = row.Name;
                ui.Name.color = nameColor;
            }

            var valueColor = dead ? CsHudTheme.Disabled : CsHudTheme.TextHud;

            // HP：活的显示右对齐血量数字；死的把数字收起来，只留原版那个左对齐的 Dead
            var showHp = row.Alive && row.Health != CsScoreRow.UnknownHealth;
            if (ui.Hp.gameObject.activeSelf != showHp) ui.Hp.gameObject.SetActive(showHp);
            if (showHp) ui.Hp.text = row.Health.ToString();

            var showDead = !row.Alive;
            if (ui.HpDead.gameObject.activeSelf != showDead) ui.HpDead.gameObject.SetActive(showDead);
            if (showDead) ui.HpDead.text = "Dead";

            ui.Money.text = row.Money == CsScoreRow.UnknownMoney ? "—" : $"${row.Money}";
            ui.Money.color = row.Money == CsScoreRow.UnknownMoney ? CsHudTheme.Disabled : CsHudTheme.MoneyNormal;
            ui.Score.text = row.Kills.ToString();
            ui.Score.color = valueColor;
            ui.Deaths.text = row.Deaths.ToString();
            ui.Deaths.color = valueColor;

            // 单机版：只有本地玩家走"本机回环"（0），机器人不是网络实体 → 不编 ping，照原版显示 BOT
            var bot = row.IsBot || row.Latency == CsScoreRow.UnknownLatency;
            ui.Ping.text = bot ? "BOT" : row.Latency.ToString();
            ui.Ping.color = CsHudTheme.TextDim;

            // 自己那行高亮（规格：自己那行高亮）
            if (ui.Bg != null)
                ui.Bg.color = row.IsSelf ? CsUiStyle.AccentDim : new Color(0f, 0f, 0f, 0.15f);
        }

        // ═══════════════════════ 池 ═══════════════════════

        private Row GetRow(int index)
        {
            while (_pool.Count <= index && _pool.Count < MaxRows) _pool.Add(CreateRow(_pool.Count));
            return index < _pool.Count ? _pool[index] : null;
        }

        private GroupHeader GetGroupHeader(int index)
        {
            while (_groupPool.Count <= index && _groupPool.Count < 4) _groupPool.Add(CreateGroupHeader(_groupPool.Count));
            return index < _groupPool.Count ? _groupPool[index] : null;
        }

        private Row CreateRow(int index)
        {
            var bg = UIFactory.CreatePanel($"Row{index}", _rowRoot, new Color(0f, 0f, 0f, 0.15f), false);
            CsHudTheme.PlaceTopLeft(bg.rectTransform, Vector2.zero, new Vector2(BoxWidth - 48f, RowHeight));

            var row = new Row
            {
                Rt = bg.rectTransform,
                Bg = bg,
                Name = Cell(bg.rectTransform, "Name", ColName, ColNameWidth, TextAnchor.MiddleLeft),
                Hp = Cell(bg.rectTransform, "Hp", ColHp, ColNumWidth, TextAnchor.MiddleRight),
                HpDead = Cell(bg.rectTransform, "HpDead", ColHpDead, ColNumWidth, TextAnchor.MiddleLeft),
                Money = Cell(bg.rectTransform, "Money", ColMoney, ColNumWidth, TextAnchor.MiddleRight),
                Score = Cell(bg.rectTransform, "Score", ColScore, ColNumWidth, TextAnchor.MiddleRight),
                Deaths = Cell(bg.rectTransform, "Deaths", ColDeaths, ColNumWidth, TextAnchor.MiddleRight),
                Ping = Cell(bg.rectTransform, "Ping", ColPing, ColNumWidth, TextAnchor.MiddleRight),
            };
            return row;
        }

        private static Text Cell(RectTransform parent, string name, float x, float width, TextAnchor anchor)
        {
            var text = CsHudTheme.CreateText(name, parent, string.Empty, 20, anchor, CsHudTheme.TextHud);
            CsHudTheme.PlaceTopLeft(text.rectTransform, new Vector2(x, 0f), new Vector2(width, RowHeight));
            return text;
        }

        private GroupHeader CreateGroupHeader(int index)
        {
            var root = UIFactory.CreateNode($"Group{index}", _rowRoot);
            CsHudTheme.PlaceTopLeft(root, Vector2.zero, new Vector2(BoxWidth - 48f, HeaderHeight));
            var label = CsHudTheme.CreateText("Label", root, string.Empty, 21, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(label.rectTransform, new Vector2(6f, 0f), new Vector2(BoxWidth - 60f, HeaderHeight));
            return new GroupHeader { Rt = root, Label = label };
        }
    }
}
