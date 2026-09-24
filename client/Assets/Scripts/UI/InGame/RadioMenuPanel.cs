using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>无线电菜单组（CS 1.6 的三组：Z / X / C）。</summary>
    public enum CsRadioGroup
    {
        A = 0,   // Z
        B = 1,   // X
        C = 2,   // C
    }

    /// <summary>
    /// 数字键 1~5 选一句 → 发 <c>Events.RadioCommand</c>。
    ///
    /// <para><b>文本去哪了</b>：<c>MatchModule</c> 收到 <c>RadioCommand</c> 后会调
    /// <c>CsMatch.PublishMessage</c>，那条消息同时进 <see cref="CsHudSnapshot.LastMessage"/>
    /// 与 <c>Events.GameMessage</c> —— HUD 消息栏订阅后者，所以无线电文本会自动出现在 HUD 上
    /// （规格要求"并把文本显示到 HUD 消息栏"，这一环不需要本面板自己画）。</para>
    ///
    /// <para><b>交互</b>：与 CS 1.6 一致 —— 按住 Z/X/C 打开，按数字选中，**松开开关键就收起**。
    /// 选择只走键盘（游戏内光标是锁定状态），所以本面板不解锁鼠标。</para>
    /// </summary>
    public class RadioMenuPanel : CsPanelBase
    {
        private const float BoxWidth = 620f;
        private const float BoxHeight = 300f;
        private const float RowHeight = 32f;
        private const int EntriesPerGroup = 5;

        /// <summary>CS 1.6 的 radio1 / radio2 / radio3 三组命令（原版文本）。</summary>
        private static readonly string[][] RadioTexts =
        {
            new[]
            {
                "Go, go, go!",
                "Team, fall back!",
                "Stick together, team!",
                "Follow me!",
                "Taking fire, need assistance!",
            },
            new[]
            {
                "Cover me!",
                "You take the point!",
                "Hold this position!",
                "Regroup team!",
                "Sector clear!",
            },
            new[]
            {
                "Enemy spotted!",
                "Need backup!",
                "I'm in position!",
                "Reporting in!",
                "Get out of there, it's gonna blow!",
            },
        };

        private static readonly string[] GroupTitles = { "RADIO A（Z）", "RADIO B（X）", "RADIO C（C）" };
        private static readonly GameKey[] GroupKeys = { GameKey.Z, GameKey.X, GameKey.C };

        [SerializeField] private Text _titleText;
        [SerializeField] private Text[] _rowTexts;
        [SerializeField] private Text _hintText;

        private CsRadioGroup _group = CsRadioGroup.A;
        private bool _warnedRefs;

        public override UILayer Layer => UILayer.Popup;

        public override void BuildLayout(RectTransform root)
        {
            // 无线电菜单是小面板，不铺全屏遮罩（CS 1.6 也是直接压在画面上）
            var box = UIFactory.CreatePanel("Dialog", root, new Color(0f, 0f, 0f, 0.72f), true);
            CsHudTheme.PlaceBottomCenter(box.rectTransform, new Vector2(0f, 40f), new Vector2(BoxWidth, BoxHeight));
            var boxRt = box.rectTransform;

            _titleText = CsHudTheme.CreateText("Title", boxRt, GroupTitles[0], 24, TextAnchor.MiddleLeft,
                CsHudTheme.Message);
            CsHudTheme.PlaceTopLeft(_titleText.rectTransform, new Vector2(20f, -12f), new Vector2(560f, 32f));

            _rowTexts = new Text[EntriesPerGroup];
            for (var i = 0; i < _rowTexts.Length; i++)
            {
                var text = CsHudTheme.CreateText($"Row{i}", boxRt, string.Empty, 20, TextAnchor.MiddleLeft,
                    CsHudTheme.TextHud);
                CsHudTheme.PlaceTopLeft(text.rectTransform,
                    new Vector2(24f, -52f - i * RowHeight), new Vector2(560f, RowHeight));
                _rowTexts[i] = text;
            }

            _hintText = CsHudTheme.CreateText("Hint", boxRt, string.Empty, 17, TextAnchor.MiddleLeft,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceBottomLeft(_hintText.rectTransform, new Vector2(20f, 10f), new Vector2(580f, 26f));
        }

        public override void OnOpen(object param)
        {
            if (!_warnedRefs)
            {
                _warnedRefs = true;
                if (_titleText == null || _rowTexts == null || _rowTexts.Length != EntriesPerGroup)
                    Game.Logger?.Error(Tag, "RadioMenuPanel 文本引用不完整（预制体未由 UiBuilder 生成？），条目可能显示不出来");
            }

            if (param is CsRadioGroup group) _group = group;
            else
            {
                // 被别处手工 Open（没给组）时不能静默 —— 默认 A 组，并留一条 Warn
                Game.Logger?.Warn(Tag,
                    $"RadioMenuPanel 没有拿到 {nameof(CsRadioGroup)} 参数（收到 {param?.GetType().Name ?? "null"}），默认打开 A 组");
                _group = CsRadioGroup.A;
            }

            RefreshLabels();
            Game.Logger?.Info(Tag, $"无线电菜单已打开：{GroupTitles[(int)_group]}");
        }

        public override void OnUpdate(float dt)
        {
            var input = Game.Input;
            if (input == null) return;

            var groupKey = GroupKeys[(int)_group];

            // 松开开关键 = 收起（CS 1.6 的行为）
            if (input.GetKeyUp(groupKey) || input.GetKeyDown(GameKey.Escape))
            {
                Close();
                return;
            }

            // 1~5 选择
            for (var i = 0; i < EntriesPerGroup; i++)
            {
                if (!input.GetKeyDown(NumberKey(i))) continue;
                Send(i);
                return;
            }

            // 按了别的组键 → 直接切到那一组（不用先松开再按）
            for (var g = 0; g < GroupKeys.Length; g++)
            {
                if (g == (int)_group) continue;
                if (input.GetKeyDown(GroupKeys[g]))
                {
                    _group = (CsRadioGroup)g;
                    RefreshLabels();
                    Game.Logger?.Info(Tag, $"无线电菜单切组：{GroupTitles[g]}");
                    return;
                }
            }
        }

        private static GameKey NumberKey(int index)
        {
            switch (index)
            {
                case 0: return GameKey.Num1;
                case 1: return GameKey.Num2;
                case 2: return GameKey.Num3;
                case 3: return GameKey.Num4;
                default: return GameKey.Num5;
            }
        }

        private void RefreshLabels()
        {
            var index = Mathf.Clamp((int)_group, 0, GroupTitles.Length - 1);
            if (_titleText != null) _titleText.text = GroupTitles[index];

            var texts = RadioTexts[index];
            for (var i = 0; i < _rowTexts.Length; i++)
            {
                if (_rowTexts[i] == null) continue;
                _rowTexts[i].text = i < texts.Length ? $"{i + 1}. {texts[i]}" : string.Empty;
            }

            if (_hintText != null)
                _hintText.text = "1~5 选择　·　松开开关键（Z/X/C）或 ESC 收起";
        }

        private void Send(int index)
        {
            var texts = RadioTexts[Mathf.Clamp((int)_group, 0, RadioTexts.Length - 1)];
            if (index < 0 || index >= texts.Length)
            {
                Game.Logger?.Warn(Tag, $"无线电选择序号 {index} 越界（本组共 {texts.Length} 条），已忽略");
                return;
            }

            var text = texts[index];
            Game.Logger?.Info(Tag, $"无线电发送（{GroupTitles[(int)_group]}）：{text}");
            Game.Event.Emit(Events.RadioCommand, text);

            // HUD 消息栏同时会收到比赛模块回推的 GameMessage；这里再补一条本地回执，
            // 保证"HUD 不在也能看到自己发了什么"
            HudPanel.Notify($"无线电：{text}");
            Close();
        }

        private void Close()
        {
            Game.UI.Close<RadioMenuPanel>();
        }
    }
}
