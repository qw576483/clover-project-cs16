using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// H 菜单（规格 G12，**用户点名的菜单**）：机器人管理 / 比赛控制 / 换阵营 / 换图 / 关闭。
    ///
    /// <para><b>它是纯"发令台"</b>：所有动作都只 <c>Game.Event.Emit(...)</c>，
    /// 由比赛模块（<c>MatchModule</c> 已接好线）真正执行。UI 不碰任何业务对象。</para>
    ///
    /// <para><b>数值回显的诚实边界</b>：<c>CsHudSnapshot</c> 里没有机器人数量 / 当前难度
    /// （看它的字段清单），所以本面板**不编数字**：显示的是本面板刚发出的操作回执，
    /// 并把"真实数量要看控制台日志；需要主 agent 在快照里补 BotCount / BotDifficulty"直接写在界面上。
    /// 快照补上之后，只需改 <see cref="RefreshEcho"/> 那两行。</para>
    ///
    /// <para><b>换图</b>：本工程只有 de_dust2。点箭头**不发 <c>Events.SwitchMap</c>** ——
    /// 该事件目前在比赛模块里只会打一条"换图需要 Flow 重载场景，已忽略"的 Warn；
    /// 发一个注定被忽略的事件，不如老实告诉玩家该怎么换。</para>
    /// </summary>
    public class HMenuPanel : CsPanelBase
    {
        private const float DialogWidth = 960f;
        private const float DialogHeight = 720f;
        private const float BtnW = 200f;
        private const float BtnH = 44f;
        private const float Small = 92f;

        [SerializeField] private Text _statusText;
        [SerializeField] private Text _botEchoText;
        [SerializeField] private Text _aliveText;
        [SerializeField] private Text _mapValueText;

        [SerializeField] private Button _addEasyButton;
        [SerializeField] private Button _addNormalButton;
        [SerializeField] private Button _addHardButton;
        [SerializeField] private Button _kickBotButton;
        [SerializeField] private Button _restartRoundButton;
        [SerializeField] private Button _restartMatchButton;
        [SerializeField] private Button _togglePauseButton;
        [SerializeField] private Button _teamCTButton;
        [SerializeField] private Button _teamTButton;
        [SerializeField] private Button _teamSpecButton;
        [SerializeField] private Button _mapPrevButton;
        [SerializeField] private Button _mapNextButton;
        [SerializeField] private Button _closeButton;

        /// <summary>本工程唯一可玩地图（换图选择器只有这一项）。</summary>
        private static readonly string[] Maps = { CsConst.MapDust2 };

        private int _mapIndex;
        private string _lastBotAction = "（还没有操作）";
        private CsBotDifficulty _lastDifficulty = CsBotDifficulty.Normal;
        private bool _difficultyKnown;
        private bool _warnedRefs;

        public override UILayer Layer => UILayer.Popup;

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.5f), true);
            UIFactory.Stretch(backdrop.rectTransform);

            var box = UIFactory.CreatePanel("Dialog", root, CsUiStyle.Box, true);
            CsHudTheme.PlaceCenter(box.rectTransform, Vector2.zero, new Vector2(DialogWidth, DialogHeight));
            var boxRt = box.rectTransform;

            var title = CsHudTheme.CreateText("Title", boxRt, "H MENU", 36, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(title.rectTransform, new Vector2(28f, -16f), new Vector2(600f, 46f));

            var warn = CsHudTheme.CreateText("Warn", boxRt,
                "机器人数量 / 当前难度：CsHudSnapshot 未提供（快照无 BotCount 字段）" +
                " —— 以本页回执与控制台（/ 键）的 [Match] 日志为准",
                17, TextAnchor.MiddleLeft, CsHudTheme.Warn);
            CsHudTheme.PlaceTopLeft(warn.rectTransform, new Vector2(28f, -60f), new Vector2(900f, 26f));

            // ─────────── 机器人 ───────────
            SectionHeader(boxRt, "机器人 / BOTS", -100f);

            var addLabel = CsHudTheme.CreateText("AddLabel", boxRt, "添加机器人", 22, TextAnchor.MiddleLeft,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopLeft(addLabel.rectTransform, new Vector2(28f, -140f), new Vector2(200f, BtnH));

            _addEasyButton = CsHudTheme.CreateButton("Btn_AddEasy", boxRt, "Easy", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(220f, -140f), new Vector2(Small, BtnH), null);
            _addNormalButton = CsHudTheme.CreateButton("Btn_AddNormal", boxRt, "Normal", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(220f + Small + 8f, -140f), new Vector2(Small, BtnH), null);
            _addHardButton = CsHudTheme.CreateButton("Btn_AddHard", boxRt, "Hard", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(220f + (Small + 8f) * 2f, -140f), new Vector2(Small, BtnH), null);

            _kickBotButton = CsHudTheme.CreateButton("Btn_KickBot", boxRt, "踢出机器人", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(28f, -196f), new Vector2(BtnW, BtnH), null);

            _botEchoText = CsHudTheme.CreateText("BotEcho", boxRt, "最近操作：（还没有操作）", 20,
                TextAnchor.MiddleLeft, CsHudTheme.TextDim);
            CsHudTheme.PlaceTopLeft(_botEchoText.rectTransform, new Vector2(244f, -196f),
                new Vector2(700f, BtnH));

            // ─────────── 比赛 ───────────
            SectionHeader(boxRt, "比赛 / MATCH", -256f);

            _restartRoundButton = CsHudTheme.CreateButton("Btn_RestartRound", boxRt, "重开本回合",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(28f, -296f), new Vector2(BtnW, BtnH), null);
            _restartMatchButton = CsHudTheme.CreateButton("Btn_RestartMatch", boxRt, "重开比赛",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(240f, -296f), new Vector2(BtnW, BtnH), null);
            _togglePauseButton = CsHudTheme.CreateButton("Btn_TogglePause", boxRt, "暂停 / 继续",
                new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(452f, -296f), new Vector2(BtnW, BtnH), null);

            var teamLabel = CsHudTheme.CreateText("TeamLabel", boxRt, "换阵营", 22, TextAnchor.MiddleLeft,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopLeft(teamLabel.rectTransform, new Vector2(28f, -352f), new Vector2(160f, BtnH));

            _teamCTButton = CsHudTheme.CreateButton("Btn_TeamCT", boxRt, "CT", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(180f, -352f), new Vector2(Small, BtnH), null);
            _teamTButton = CsHudTheme.CreateButton("Btn_TeamT", boxRt, "T", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(180f + Small + 8f, -352f), new Vector2(Small, BtnH), null);
            _teamSpecButton = CsHudTheme.CreateButton("Btn_TeamSpec", boxRt, "观察者", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(180f + (Small + 8f) * 2f, -352f), new Vector2(Small, BtnH), null);

            var mapLabel = CsHudTheme.CreateText("MapLabel", boxRt, "换地图", 22, TextAnchor.MiddleLeft,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopLeft(mapLabel.rectTransform, new Vector2(28f, -408f), new Vector2(160f, BtnH));

            _mapPrevButton = CsHudTheme.CreateButton("Btn_MapPrev", boxRt, "\u25C0", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(180f, -408f), new Vector2(44f, BtnH), null);
            _mapValueText = CsHudTheme.CreateText("MapValue", boxRt, CsConst.MapDust2, 22,
                TextAnchor.MiddleCenter, CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(_mapValueText.rectTransform, new Vector2(232f, -408f),
                new Vector2(240f, BtnH));
            _mapNextButton = CsHudTheme.CreateButton("Btn_MapNext", boxRt, "\u25B6", new Vector2(0f, 1f),
                new Vector2(0f, 1f), new Vector2(480f, -408f), new Vector2(44f, BtnH), null);

            var mapHint = CsHudTheme.CreateText("MapHint", boxRt,
                $"本工程只有 {CsConst.MapDust2} 一张图；换图请回主菜单 → New Game（局内换图需要重载 {SceneNames.StageDust2} 场景）",
                18, TextAnchor.MiddleLeft, CsHudTheme.TextDim);
            CsHudTheme.PlaceTopLeft(mapHint.rectTransform, new Vector2(28f, -456f), new Vector2(900f, 26f));

            _aliveText = CsHudTheme.CreateText("Alive", boxRt, string.Empty, 20, TextAnchor.MiddleLeft,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceTopLeft(_aliveText.rectTransform, new Vector2(28f, -492f), new Vector2(900f, 26f));

            _statusText = CsHudTheme.CreateText("Status", boxRt, string.Empty, 20, TextAnchor.MiddleLeft,
                CsHudTheme.Message);
            CsHudTheme.PlaceTopLeft(_statusText.rectTransform, new Vector2(28f, -524f), new Vector2(900f, 26f));

            _closeButton = CsHudTheme.CreateButton("Btn_Close", boxRt, "Close", new Vector2(0f, 0f),
                new Vector2(0f, 0f), new Vector2(28f, 20f), new Vector2(BtnW, 52f), null, accent: true);
        }

        private static void SectionHeader(RectTransform parent, string text, float y)
        {
            var header = CsHudTheme.CreateText($"Section_{text}", parent, text, 22, TextAnchor.MiddleLeft,
                CsUiStyle.Accent);
            CsHudTheme.PlaceTopLeft(header.rectTransform, new Vector2(28f, y), new Vector2(700f, 30f));
        }

        public override void OnOpen(object param)
        {
            if (!_warnedRefs)
            {
                _warnedRefs = true;
                if (_addEasyButton == null || _kickBotButton == null || _closeButton == null)
                    Game.Logger?.Error(Tag, "HMenuPanel 关键按钮引用缺失（预制体未由 UiBuilder 生成或已被改动）");
            }

            // 按钮的 onClick 是运行期监听器，存不进预制体 → 必须在每次 OnOpen 重新绑
            Bind(_addEasyButton, () => AddBot(CsBotDifficulty.Easy), "添加机器人 Easy");
            Bind(_addNormalButton, () => AddBot(CsBotDifficulty.Normal), "添加机器人 Normal");
            Bind(_addHardButton, () => AddBot(CsBotDifficulty.Hard), "添加机器人 Hard");
            Bind(_kickBotButton, KickBot, "踢出机器人");
            Bind(_restartRoundButton, () => Emit(Events.RestartRound, "重开本回合"), "重开本回合");
            Bind(_restartMatchButton, () => Emit(Events.RestartMatch, "重开比赛"), "重开比赛");
            Bind(_togglePauseButton, OnTogglePause, "暂停/继续");
            Bind(_teamCTButton, () => ChangeTeam(CsTeam.CT), "换阵营 CT");
            Bind(_teamTButton, () => ChangeTeam(CsTeam.T), "换阵营 T");
            Bind(_teamSpecButton, () => ChangeTeam(CsTeam.Spectator), "换阵营 观察者");
            Bind(_mapPrevButton, () => CycleMap(-1), "换图 ◀");
            Bind(_mapNextButton, () => CycleMap(1), "换图 ▶");
            Bind(_closeButton, Close, "Close");

            CsIngameCursor.UnlockForMenu(nameof(HMenuPanel));

            RefreshMapValue();
            RefreshEcho();
            Game.Logger?.Info(Tag, "H 菜单已打开（机器人 / 比赛 / 换阵营 / 换图）");
        }

        public override void OnClose()
        {
            CsIngameCursor.RelockIfIdle(nameof(HMenuPanel), CsIngameCursor.AnyMenuOpen());
        }

        public override void OnUpdate(float dt)
        {
            var input = Game.Input;
            if (input == null) return;

            if (input.GetKeyDown(GameKey.H) || input.GetKeyDown(GameKey.Escape))
            {
                Close();
                return;
            }

            RefreshEcho();
        }

        private void Close()
        {
            Game.Logger?.Info(Tag, "H 菜单关闭");
            Game.UI.Close<HMenuPanel>();
        }

        // ═══════════════════════ 机器人 ═══════════════════════

        private void AddBot(CsBotDifficulty difficulty)
        {
            _lastDifficulty = difficulty;
            _difficultyKnown = true;
            _lastBotAction = $"添加机器人（{difficulty}）";
            SetStatus($"{_lastBotAction} —— 已发送（加到人少的一队，由比赛模块判定成败）");

            Game.Logger?.Info(Tag, $"H 菜单：添加机器人（{difficulty}），发 {Events.AddBot}");
            Game.Event.Emit(Events.AddBot, difficulty);
            HudPanel.Notify($"已发送：添加机器人（{difficulty}）");
            RefreshEcho();
        }

        private void KickBot()
        {
            _lastBotAction = "踢出机器人";
            SetStatus($"{_lastBotAction} —— 已发送（踢出最后加入的那个，由比赛模块判定成败）");

            Game.Logger?.Info(Tag, $"H 菜单：踢出机器人，发 {Events.KickBot}");
            Game.Event.Emit(Events.KickBot);
            HudPanel.Notify("已发送：踢出机器人");
            RefreshEcho();
        }

        // ═══════════════════════ 比赛 ═══════════════════════

        private void Emit(string eventName, string what)
        {
            SetStatus($"{what} —— 已发送（{eventName}）");
            Game.Logger?.Info(Tag, $"H 菜单：{what}（发 {eventName}）");
            Game.Event.Emit(eventName);
            HudPanel.Notify($"已发送：{what}");
        }

        private void OnTogglePause()
        {
            // 这是"比赛模拟暂停"，不是 ESC 游戏菜单 —— CS 里两者也是分开的（mp_pause vs 菜单）
            SetStatus("暂停 / 继续 —— 已发送（冻结/恢复比赛模拟；ESC 才是游戏菜单）");
            Game.Logger?.Info(Tag, $"H 菜单：暂停/继续比赛模拟（发 {Events.TogglePause}）");
            Game.Event.Emit(Events.TogglePause);
            HudPanel.Notify("已发送：暂停/继续比赛");
        }

        private void ChangeTeam(CsTeam team)
        {
            SetStatus($"换阵营 → {team} —— 已发送");
            Game.Logger?.Info(Tag, $"H 菜单：换阵营 → {team}（发 {Events.ChangeTeam}）");
            Game.Event.Emit(Events.ChangeTeam, team);
            HudPanel.Notify($"已发送：换阵营（{team}）");
        }

        private void CycleMap(int delta)
        {
            if (Maps.Length <= 1)
            {
                SetStatus($"只有 {Maps[0]} 一张图：换图请回主菜单 → New Game（局内换图需要重载场景）");
                HudPanel.Notify($"本工程只有 {Maps[0]}，换图请回主菜单");
                return;
            }

            _mapIndex = (_mapIndex + delta + Maps.Length) % Maps.Length;
            RefreshMapValue();
            SetStatus($"已选择地图 {Maps[_mapIndex]}（发 {Events.SwitchMap}）");
            Game.Event.Emit(Events.SwitchMap, Maps[_mapIndex]);
        }

        private void RefreshMapValue()
        {
            if (_mapValueText != null) _mapValueText.text = Maps[Mathf.Clamp(_mapIndex, 0, Maps.Length - 1)];
        }

        // ═══════════════════════ 回显 ═══════════════════════

        private void SetStatus(string text)
        {
            if (_statusText != null) _statusText.text = text;
        }

        private void RefreshEcho()
        {
            if (_botEchoText != null)
            {
                var difficulty = _difficultyKnown ? _lastDifficulty.ToString() : "未知";
                _botEchoText.text = $"最近操作：{_lastBotAction}　难度档：{difficulty}";
            }

            if (_aliveText == null) return;

            // 存活人数取自雷达点（快照里唯一"逐人"的信息）—— 措辞明确写"本帧可见"，
            // 不把它冒充成"队伍总人数"，因为不可见的敌人根本不在列表里
            var t = 0;
            var ct = 0;
            var radar = CsHudSnapshot.Radar;
            for (var i = 0; i < radar.Count; i++)
            {
                var dot = radar[i];
                if (dot.IsBomb || dot.IsBombsite || dot.IsSelf) continue;
                if (dot.Team == CsTeam.T) t++;
                else if (dot.Team == CsTeam.CT) ct++;
            }

            _aliveText.text = $"本帧雷达可见的其他存活者：T={t} / CT={ct}（不含自己；快照不提供队伍总人数）";
        }
    }
}
