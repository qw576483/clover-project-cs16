using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 记分板（规格 G11，按住 <c>TAB</c> 显示）：按阵营分组，列出
    /// <c>玩家名 | 击杀 | 死亡 | 存活 | 金钱 | 延迟 | 状态</c>，自己那行高亮、死亡者变灰，
    /// 底部显示比分与回合数。
    ///
    /// <para><b>层</b>：<see cref="UILayer.Normal"/> —— 它是"按住 TAB 看一眼"的信息板，
    /// 不该抢输入（<c>Popup</c> 会把开着的买枪菜单顶掉，也会让 ESC 误判成"有遮挡"）。</para>
    ///
    /// <para><b>数据源</b>：逐人统计来自常驻 HUD 上的 <see cref="CsIngameStats"/>
    /// （<c>Game.UI.Get&lt;HudPanel&gt;().BuildScoreRows(...)</c>），它的口径与比赛模块的击杀事件同源。
    /// 快照里没有花名册这点，界面底部有一行明确说明 —— 不假装数据是完整的。</para>
    ///
    /// <para><b>延迟列</b>：单机版本地模拟，没有网络往返 ⇒ 自己显示 <c>0</c>，机器人显示 <c>—</c>
    /// （机器人不是网络实体，编一个 ping 值才是造假）。</para>
    /// </summary>
    public class ScoreboardPanel : CsPanelBase
    {
        private const float BoxWidth = 1120f;
        private const float BoxHeight = 660f;
        private const float RowHeight = 30f;
        private const float HeaderHeight = 28f;
        private const int MaxRows = 24;

        /// <summary>行区域相对对话框左边缘的偏移（表头与数据行必须共用它才能对齐）。</summary>
        private const float RowRootX = 24f;

        // 列（相对行左边缘的 x）
        private const float ColName = 20f;
        private const float ColKills = 380f;
        private const float ColDeaths = 470f;
        private const float ColAlive = 560f;
        private const float ColMoney = 660f;
        private const float ColPing = 800f;
        private const float ColStatus = 890f;
        private const float ColWidth = 88f;

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
            public Text Kills;
            public Text Deaths;
            public Text Alive;
            public Text Money;
            public Text Ping;
            public Text Status;
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
            CsHudTheme.PlaceTopLeft(_titleText.rectTransform, new Vector2(24f, -14f), new Vector2(500f, 38f));

            _roundText = CsHudTheme.CreateText("Round", boxRt, "第 1 回合", 22, TextAnchor.MiddleRight,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceTopRight(_roundText.rectTransform, new Vector2(-24f, -14f), new Vector2(300f, 38f));

            _scoreText = CsHudTheme.CreateText("Score", boxRt, "CT 0 : 0 T", 32, TextAnchor.MiddleCenter,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopCenter(_scoreText.rectTransform, new Vector2(0f, -56f), new Vector2(500f, 40f));

            // 表头：每列单独一个 Text，x 偏移与数据行完全一致（比例字体下用空格对齐是假的）
            var headers = new[] { "玩家名", "击杀", "死亡", "存活", "金钱", "延迟", "状态" };
            var xs = new[] { ColName, ColKills, ColDeaths, ColAlive, ColMoney, ColPing, ColStatus };
            var widths = new[] { 300f, ColWidth, ColWidth, ColWidth, ColWidth + 40f, ColWidth, 200f };
            var anchors = new[]
            {
                TextAnchor.MiddleLeft, TextAnchor.MiddleCenter, TextAnchor.MiddleCenter,
                TextAnchor.MiddleCenter, TextAnchor.MiddleRight, TextAnchor.MiddleCenter, TextAnchor.MiddleLeft,
            };

            _headerCells = new Text[headers.Length];
            for (var i = 0; i < headers.Length; i++)
            {
                var cell = CsHudTheme.CreateText($"Head{i}", boxRt, headers[i], 19, anchors[i],
                    CsHudTheme.TextDim);
                CsHudTheme.PlaceTopLeft(cell.rectTransform,
                    new Vector2(RowRootX + xs[i], -100f), new Vector2(widths[i], 26f));
                _headerCells[i] = cell;
            }

            var rowRoot = UIFactory.CreateNode("Rows", boxRt);
            CsHudTheme.PlaceTopLeft(rowRoot, new Vector2(RowRootX, -132f),
                new Vector2(BoxWidth - RowRootX * 2f, BoxHeight - 190f));
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
            if (hud == null)
            {
                // HUD 不在 = 不在局内（或 HUD 被异常关掉）。这里必须留痕，并且明确显示"拿不到数据"
                if (!_noHudWarned)
                {
                    _noHudWarned = true;
                    Game.Logger?.Error(Tag,
                        "记分板拿不到 HudPanel（它承载逐人统计）—— 只有在比赛进行中 HUD 常驻时才有数据");
                }
                _rows.Clear();
            }
            else
            {
                _noHudWarned = false;
                hud.BuildScoreRows(_rows);
            }

            if (_scoreText != null) _scoreText.text = $"CT {CsHudSnapshot.ScoreCT}  :  {CsHudSnapshot.ScoreT} T";
            if (_roundText != null) _roundText.text = $"第 {CsHudSnapshot.RoundNumber} 回合　·　{TABHint()}";
            if (_titleText != null) _titleText.color = CsHudTheme.TeamColor(CsHudSnapshot.Team);

            if (_footerText != null)
            {
                _footerText.text = _rows.Count <= 1
                    ? "本场尚无击杀记录。记分板名册由快照的击杀信息累计得出（CsHudSnapshot 不提供花名册：\n" +
                      "未参与过击杀的玩家不会出现，金钱只有自己知道 —— 需要完整名册请让比赛模块把花名册写进快照）。"
                    : "统计口径：与比赛模块的击杀事件同源累计；金钱仅本地玩家已知（其余显示 —）；延迟在单机版为 0（机器人 —）。";
            }

            LayoutRows();
        }

        private bool _noHudWarned;

        private static string TABHint() => "松开 TAB 关闭";

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
            ui.Kills.text = row.Kills.ToString();
            ui.Kills.color = valueColor;
            ui.Deaths.text = row.Deaths.ToString();
            ui.Deaths.color = valueColor;
            ui.Alive.text = row.Alive ? "存活" : "死亡";
            ui.Alive.color = row.Alive ? CsHudTheme.HealthOk : CsHudTheme.Disabled;
            ui.Money.text = row.Money == CsScoreRow.UnknownMoney ? "—" : $"${row.Money}";
            ui.Money.color = row.Money == CsScoreRow.UnknownMoney ? CsHudTheme.Disabled : CsHudTheme.MoneyNormal;

            // 单机版：只有本地玩家走"本机回环"（0），机器人不是网络实体 → 不编 ping
            ui.Ping.text = row.IsSelf ? "0" : "—";
            ui.Ping.color = CsHudTheme.TextDim;

            ui.Status.text = row.IsSelf ? (row.Alive ? "你 · 存活" : "你 · 死亡")
                : (row.Alive ? "在场" : "阵亡");
            ui.Status.color = dead ? CsHudTheme.Disabled : CsHudTheme.TextDim;

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
                Name = Cell(bg.rectTransform, "Name", ColName, 300f, TextAnchor.MiddleLeft),
                Kills = Cell(bg.rectTransform, "Kills", ColKills, ColWidth, TextAnchor.MiddleCenter),
                Deaths = Cell(bg.rectTransform, "Deaths", ColDeaths, ColWidth, TextAnchor.MiddleCenter),
                Alive = Cell(bg.rectTransform, "Alive", ColAlive, ColWidth, TextAnchor.MiddleCenter),
                Money = Cell(bg.rectTransform, "Money", ColMoney, ColWidth + 40f, TextAnchor.MiddleRight),
                Ping = Cell(bg.rectTransform, "Ping", ColPing, ColWidth, TextAnchor.MiddleCenter),
                Status = Cell(bg.rectTransform, "Status", ColStatus, 200f, TextAnchor.MiddleLeft),
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
