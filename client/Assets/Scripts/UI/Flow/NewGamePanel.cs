using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// New Game 配置面板（对照 CS 1.6 的 "New Game"：左边选地图、右边设规则，底部开打）。
    ///
    /// <para>可配项：地图 / 每队 bot 数 / bot 难度（3 档）/ 回合数 / 起始金钱 / 回合时间 /
    /// 友军伤害 / 玩家名。点 Start 发 <see cref="Events.LaunchMatch"/>（参数 <see cref="CsMatchConfig"/>），
    /// 由 Flow 接着走"读条 → 选阵营 → 进图"。</para>
    ///
    /// <para>选项一律用 CS 1.6 那种"◀ 值 ▶"箭头（bot 难度在原版里就是箭头切的），不用下拉框 ——
    /// 空 sprite 的下拉框模板在代码生成场景里极易做坏且难查。</para>
    /// </summary>
    public class NewGamePanel : CsPanelBase
    {
        // ---- 可选项（真源仍是 CsConst；这里只补 CsConst 未覆盖的档位，且都带名字、不留裸数字）----
        /// <summary>每队 bot 数上限：CS 1.6 maxplayers = 10，扣掉玩家本人。</summary>
        private const int MaxBotsPerTeam = 9;
        /// <summary>官服常见的中等起始金钱档（mp_startmoney 变体）。</summary>
        private const int StartMoneyMid = 1600;
        private const float RoundTimeMediumFactor = 0.75f;
        private const float RoundTimeShortFactor = 0.5f;

        private const float SelectorX = 800f;
        private const float SelectorTopY = -230f;
        private const float SelectorStep = 62f;
        private const float SelectorLabelWidth = 250f;
        private const float SelectorArrowWidth = 44f;
        private const float SelectorValueWidth = 180f;

        private const float MapListX = 90f;
        private const float MapListY = -215f;
        private const float MapListWidth = 640f;
        private const float MapListHeight = 420f;
        private const float MapRowStep = 56f;
        private const float MapRowHeight = 48f;

        // 选择行序号（供 CycleSelector 分派；用常量而不是字符串，避免拼错键名还编译得过）
        private const int SelectorBots = 0;
        private const int SelectorDifficulty = 1;
        private const int SelectorRounds = 2;
        private const int SelectorMoney = 3;
        private const int SelectorRoundTime = 4;
        private const int SelectorFriendlyFire = 5;

        private static readonly int[] BotCounts = BuildRange(0, MaxBotsPerTeam);
        private static readonly string[] Maps = { CsConst.MapDust2 };
        private static readonly int[] RoundsPerHalfOptions = { 5, 10, CsConst.RoundsPerHalf };
        private static readonly int[] StartMoneyOptions = { CsConst.StartMoney, StartMoneyMid, CsConst.MaxMoney };
        private static readonly float[] RoundTimeOptions =
        {
            CsConst.RoundTime,
            CsConst.RoundTime * RoundTimeMediumFactor,
            CsConst.RoundTime * RoundTimeShortFactor,
        };
        private static readonly CsBotDifficulty[] DifficultyOptions =
        {
            CsBotDifficulty.Easy, CsBotDifficulty.Normal, CsBotDifficulty.Hard,
        };
        private static readonly bool[] FriendlyFireOptions = { false, true };

        [SerializeField] private Text _mapHint;
        [SerializeField] private RectTransform _mapListRoot;
        [SerializeField] private Selector _bots;
        [SerializeField] private Selector _difficulty;
        [SerializeField] private Selector _rounds;
        [SerializeField] private Selector _money;
        [SerializeField] private Selector _roundTime;
        [SerializeField] private Selector _friendlyFire;
        [SerializeField] private InputField _nameInput;
        [SerializeField] private Button _startButton;
        [SerializeField] private Button _backButton;

        private readonly List<GameObject> _mapRows = new List<GameObject>();
        private readonly List<Button> _mapButtons = new List<Button>();

        private int _mapIndex;
        private int _botsIndex;
        private int _difficultyIndex;
        private int _roundsIndex;
        private int _moneyIndex;
        private int _roundTimeIndex;
        private int _friendlyFireIndex;

        public override void BuildLayout(RectTransform root)
        {
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);

            CsUiStyle.CreateLabel("Title", root, "NEW GAME", 52, new Vector2(90f, -70f),
                new Vector2(1000f, 68f), TextAnchor.MiddleLeft, CsUiStyle.Text);
            CsUiStyle.CreateBoxRect("TitleBar", root, new Vector2(92f, -142f), new Vector2(520f, 4f), CsUiStyle.Accent);

            CsUiStyle.CreateLabel("MapSection", root, "地图 / MAPS", 22, new Vector2(92f, -178f),
                new Vector2(600f, 32f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);
            CsUiStyle.CreateBoxRect("MapListBox", root, new Vector2(MapListX, MapListY),
                new Vector2(MapListWidth, MapListHeight), CsUiStyle.Box);

            var listNode = UIFactory.CreateNode("MapRows", root);
            CsUiStyle.AnchoredTopLeft(listNode, new Vector2(MapListX + 20f, MapListY - 20f),
                new Vector2(MapListWidth - 40f, MapListHeight - 40f));
            _mapListRoot = listNode;

            _mapHint = CsUiStyle.CreateLabel("MapHint", root, string.Empty, 18,
                new Vector2(92f, MapListY - MapListHeight - 12f), new Vector2(700f, 30f),
                TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            CsUiStyle.CreateLabel("GameSection", root, "游戏设置 / GAME SETTINGS", 22,
                new Vector2(SelectorX, -178f), new Vector2(700f, 32f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            var y = SelectorTopY;
            _bots = CsUiStyle.CreateSelector("Sel_Bots", root, "机器人（每队）", new Vector2(SelectorX, y),
                SelectorLabelWidth, SelectorArrowWidth, SelectorValueWidth, null, null);
            y -= SelectorStep;
            _difficulty = CsUiStyle.CreateSelector("Sel_Difficulty", root, "机器人难度", new Vector2(SelectorX, y),
                SelectorLabelWidth, SelectorArrowWidth, SelectorValueWidth, null, null);
            y -= SelectorStep;
            _rounds = CsUiStyle.CreateSelector("Sel_Rounds", root, "回合数（每半场）", new Vector2(SelectorX, y),
                SelectorLabelWidth, SelectorArrowWidth, SelectorValueWidth, null, null);
            y -= SelectorStep;
            _money = CsUiStyle.CreateSelector("Sel_Money", root, "起始金钱", new Vector2(SelectorX, y),
                SelectorLabelWidth, SelectorArrowWidth, SelectorValueWidth, null, null);
            y -= SelectorStep;
            _roundTime = CsUiStyle.CreateSelector("Sel_RoundTime", root, "回合时间", new Vector2(SelectorX, y),
                SelectorLabelWidth, SelectorArrowWidth, SelectorValueWidth, null, null);
            y -= SelectorStep;
            _friendlyFire = CsUiStyle.CreateSelector("Sel_FriendlyFire", root, "友军伤害", new Vector2(SelectorX, y),
                SelectorLabelWidth, SelectorArrowWidth, SelectorValueWidth, null, null);

            CsUiStyle.CreateLabel("NameLabel", root, "玩家名", 22, new Vector2(SelectorX, -608f),
                new Vector2(SelectorLabelWidth, CsUiStyle.RowHeight), TextAnchor.MiddleLeft, CsUiStyle.TextDim);
            _nameInput = CsUiStyle.CreateInputField("Input_PlayerName", root,
                new Vector2(SelectorX + SelectorLabelWidth, -608f), new Vector2(320f, CsUiStyle.RowHeight),
                "Player", CsPlayerSettingsStore.MaxNameLength);

            _startButton = CsUiStyle.CreateButton("Btn_Start", root, "Start", new Vector2(SelectorX, -690f),
                new Vector2(240f, 58f), null, accent: true);
            _backButton = CsUiStyle.CreateButton("Btn_Back", root, "Back", new Vector2(SelectorX + 260f, -690f),
                new Vector2(200f, 58f), null);

            CsUiStyle.CreateLabel("BottomHint", root,
                "Start 之后会读条加载 de_dust2，然后在图内选阵营。", 18,
                new Vector2(60f, -1035f), new Vector2(1200f, 30f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_mapListRoot, "地图列表容器");
            WarnIfNull(_nameInput, "玩家名输入框");

            Bind(_startButton, OnStart, "Start");
            Bind(_backButton, OnBack, "Back");
            BindSelector(_bots, SelectorBots, "机器人数量");
            BindSelector(_difficulty, SelectorDifficulty, "机器人难度");
            BindSelector(_rounds, SelectorRounds, "回合数");
            BindSelector(_money, SelectorMoney, "起始金钱");
            BindSelector(_roundTime, SelectorRoundTime, "回合时间");
            BindSelector(_friendlyFire, SelectorFriendlyFire, "友军伤害");

            ResetToDefaults();
            BuildMapRows();

            if (_nameInput != null)
            {
                var settings = CsPlayerSettingsStore.Load();
                _nameInput.text = settings.PlayerName;
            }

            RefreshLabels();
        }

        public override void OnClose()
        {
            ClearMapRows();
        }

        // ─────────────────────────── 交互 ───────────────────────────

        private void OnBack()
        {
            // 关掉自己即可：AppFlow 的 NewGame 站点 onTick 检测到面板已关，会自动退回主菜单站点。
            Game.UI.Close<NewGamePanel>();
        }

        private void OnStart()
        {
            var cfg = BuildConfig();
            Game.Logger.Info(Tag,
                $"New Game 提交: map={cfg.MapName} bots/队={cfg.BotsPerTeam} diff={cfg.BotDifficulty} " +
                $"rounds/半场={cfg.RoundsPerHalf} roundTime={cfg.RoundTime:0}s money={cfg.StartMoney} " +
                $"ff={cfg.FriendlyFire} name={cfg.PlayerName}");

            // 玩家名顺手记进设置，下次进 New Game / 主菜单显示的是刚填的名字
            var settings = CsPlayerSettingsStore.Load();
            settings.PlayerName = cfg.PlayerName;
            CsPlayerSettingsStore.Save(settings);

            Game.Event.Emit(Events.LaunchMatch, cfg);
        }

        private void BindSelector(Selector selector, int selectorIndex, string what)
        {
            if (selector == null)
            {
                Game.Logger.Warn(Tag, $"{GetType().Name} 缺少选择行「{what}」引用，该项不可调");
                return;
            }

            Bind(selector.Prev, () => CycleSelector(selectorIndex, -1), $"{what} ◀");
            Bind(selector.Next, () => CycleSelector(selectorIndex, 1), $"{what} ▶");
        }

        private void CycleSelector(int selectorIndex, int delta)
        {
            switch (selectorIndex)
            {
                case SelectorBots: _botsIndex = Cycle(_botsIndex, delta, BotCounts.Length); break;
                case SelectorDifficulty: _difficultyIndex = Cycle(_difficultyIndex, delta, DifficultyOptions.Length); break;
                case SelectorRounds: _roundsIndex = Cycle(_roundsIndex, delta, RoundsPerHalfOptions.Length); break;
                case SelectorMoney: _moneyIndex = Cycle(_moneyIndex, delta, StartMoneyOptions.Length); break;
                case SelectorRoundTime: _roundTimeIndex = Cycle(_roundTimeIndex, delta, RoundTimeOptions.Length); break;
                case SelectorFriendlyFire: _friendlyFireIndex = Cycle(_friendlyFireIndex, delta, FriendlyFireOptions.Length); break;
                default:
                    Game.Logger.Warn(Tag, $"{GetType().Name} 选择行序号 {selectorIndex} 越界，忽略");
                    return;
            }
            RefreshLabels();
        }

        // ─────────────────────────── 显示 ───────────────────────────

        private void ResetToDefaults()
        {
            // 默认值全部取自契约里的 CsMatchConfig（改契约即改默认，不在这里抄第二遍）
            var defaults = new CsMatchConfig();
            _mapIndex = IndexOfString(Maps, defaults.MapName);
            _botsIndex = IndexOfInt(BotCounts, defaults.BotsPerTeam);
            _difficultyIndex = IndexOfDifficulty(defaults.BotDifficulty);
            _roundsIndex = IndexOfInt(RoundsPerHalfOptions, defaults.RoundsPerHalf);
            _moneyIndex = IndexOfInt(StartMoneyOptions, defaults.StartMoney);
            _roundTimeIndex = IndexOfFloat(RoundTimeOptions, defaults.RoundTime);
            _friendlyFireIndex = IndexOfBool(FriendlyFireOptions, defaults.FriendlyFire);
        }

        private void RefreshLabels()
        {
            if (_bots != null) _bots.SetText(BotCounts[_botsIndex].ToString());
            if (_difficulty != null) _difficulty.SetText(DifficultyOptions[_difficultyIndex].ToString());
            if (_rounds != null) _rounds.SetText(RoundsPerHalfOptions[_roundsIndex].ToString());
            if (_money != null) _money.SetText($"${StartMoneyOptions[_moneyIndex]}");
            if (_roundTime != null) _roundTime.SetText(FormatClock(RoundTimeOptions[_roundTimeIndex]));
            if (_friendlyFire != null) _friendlyFire.SetText(FriendlyFireOptions[_friendlyFireIndex] ? "On" : "Off");

            RefreshMapSelection();
        }

        private void BuildMapRows()
        {
            ClearMapRows();
            if (_mapListRoot == null) return;

            for (var i = 0; i < Maps.Length; i++)
            {
                var index = i;
                var button = CsUiStyle.CreateButton($"Map_{Maps[i]}", _mapListRoot, Maps[i],
                    new Vector2(0f, -index * MapRowStep), new Vector2(MapListWidth - 40f, MapRowHeight),
                    () => SelectMap(index));
                if (button == null) continue;
                _mapRows.Add(button.gameObject);
                _mapButtons.Add(button);
            }

            if (_mapHint != null)
                _mapHint.text = $"{Maps.Length} 张地图可选（本工程首张图 = {CsConst.MapDust2}）";

            Game.Logger.Info(Tag, $"New Game 地图列表: {string.Join(", ", Maps)}，当前选中 {Maps[_mapIndex]}");
        }

        private void SelectMap(int index)
        {
            if (index < 0 || index >= Maps.Length)
            {
                Game.Logger.Warn(Tag, $"选择地图序号 {index} 越界（共 {Maps.Length} 张），忽略");
                return;
            }
            _mapIndex = index;
            Game.Logger.Info(Tag, $"New Game 选中地图: {Maps[_mapIndex]}");
            RefreshMapSelection();
        }

        private void RefreshMapSelection()
        {
            for (var i = 0; i < _mapButtons.Count; i++)
            {
                var button = _mapButtons[i];
                if (button == null) continue;

                var selected = i == _mapIndex;
                var colors = button.colors;
                colors.normalColor = selected ? CsUiStyle.AccentDim : CsUiStyle.Field;
                colors.selectedColor = colors.normalColor;
                colors.highlightedColor = CsUiStyle.Accent;
                colors.pressedColor = CsUiStyle.AccentDim;
                colors.colorMultiplier = 1f;
                button.colors = colors;
            }
        }

        private void ClearMapRows()
        {
            for (var i = 0; i < _mapRows.Count; i++)
            {
                if (_mapRows[i] != null) Object.Destroy(_mapRows[i]);
            }
            _mapRows.Clear();
            _mapButtons.Clear();
        }

        // ─────────────────────────── 组装配置 ───────────────────────────

        private CsMatchConfig BuildConfig()
        {
            var settings = CsPlayerSettingsStore.Load();
            var playerName = _nameInput != null ? _nameInput.text : null;

            if (string.IsNullOrWhiteSpace(playerName))
            {
                Game.Logger.Warn(Tag, "New Game 玩家名为空，回退到设置里的名字");
                playerName = settings.PlayerName;
            }
            if (string.IsNullOrWhiteSpace(playerName))
            {
                Game.Logger.Warn(Tag, "New Game 设置里也没有玩家名，回退默认名");
                playerName = CsPlayerSettingsStore.DefaultPlayerName;
            }

            return new CsMatchConfig
            {
                MapName = Maps[Mathf.Clamp(_mapIndex, 0, Maps.Length - 1)],
                BotsPerTeam = BotCounts[Mathf.Clamp(_botsIndex, 0, BotCounts.Length - 1)],
                BotDifficulty = DifficultyOptions[Mathf.Clamp(_difficultyIndex, 0, DifficultyOptions.Length - 1)],
                RoundsPerHalf = RoundsPerHalfOptions[Mathf.Clamp(_roundsIndex, 0, RoundsPerHalfOptions.Length - 1)],
                RoundTime = RoundTimeOptions[Mathf.Clamp(_roundTimeIndex, 0, RoundTimeOptions.Length - 1)],
                FreezeTime = CsConst.FreezeTime,
                StartMoney = StartMoneyOptions[Mathf.Clamp(_moneyIndex, 0, StartMoneyOptions.Length - 1)],
                FriendlyFire = FriendlyFireOptions[Mathf.Clamp(_friendlyFireIndex, 0, FriendlyFireOptions.Length - 1)],
                PlayerName = playerName.Trim(),
                PlayerTeam = CsTeam.CT,     // 真实阵营在选阵营面板里定（Events.TeamChosen）
                HalfTimeSwap = true,
            };
        }

        // ─────────────────────────── 小工具 ───────────────────────────

        private static int[] BuildRange(int from, int to)
        {
            var count = Mathf.Max(0, to - from + 1);
            var result = new int[count];
            for (var i = 0; i < count; i++) result[i] = from + i;
            return result;
        }

        private static int Cycle(int index, int delta, int count)
        {
            if (count <= 0) return 0;
            var next = (index + delta) % count;
            if (next < 0) next += count;
            return next;
        }

        private static string FormatClock(float seconds)
        {
            var total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        private static int IndexOfString(string[] values, string value)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] == value) return i;
            return 0;
        }

        private static int IndexOfInt(int[] values, int value)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] == value) return i;
            return 0;
        }

        private static int IndexOfFloat(float[] values, float value)
        {
            for (var i = 0; i < values.Length; i++)
                if (Mathf.Abs(values[i] - value) < 0.001f) return i;
            return 0;
        }

        private static int IndexOfBool(bool[] values, bool value)
        {
            for (var i = 0; i < values.Length; i++)
                if (values[i] == value) return i;
            return 0;
        }

        private static int IndexOfDifficulty(CsBotDifficulty value)
        {
            for (var i = 0; i < DifficultyOptions.Length; i++)
                if (DifficultyOptions[i] == value) return i;
            return 0;
        }
    }
}
