using System.Collections.Generic;
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
    /// 数字键 1~9 选一句 → 发 <c>Events.RadioCommand</c>。
    ///
    /// <para><b>条目来源</b>：三组的内容、分组与顺序直接取自 <see cref="CsRadio"/> ——
    /// 原版 <c>radio1</c> / <c>radio2</c> / <c>radio3</c> 的 6 + 6 + 9 条，按原版枚举
    /// （<c>GameEvent.h</c> 的 <c>EVENT_RADIO_*</c>）顺序、槽位号即菜单数字键。
    /// 面板不再自带一份文本副本，两处各改各的会在"哪一组是哪几条"上分叉。</para>
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
        private const float RowHeight = 32f;

        /// <summary>原版三组里最大的一组 = radio3 的九条；行控件按它建数，短组留空行。</summary>
        private const int MaxEntries = 9;

        /// <summary>标题区高度：标题在 y=-12，第一行在 y=-52。</summary>
        private const float HeaderHeight = 52f;

        /// <summary>底部提示带高度：提示条在 y=10、高 26。</summary>
        private const float HintBand = 36f;

        private const float BoxHeight = HeaderHeight + MaxEntries * RowHeight + HintBand;

        private static readonly string[] GroupTitles = { "RADIO A（Z）", "RADIO B（X）", "RADIO C（C）" };
        private static readonly GameKey[] GroupKeys = { GameKey.Z, GameKey.X, GameKey.C };

        private static readonly GameKey[] NumberKeys =
        {
            GameKey.Num1, GameKey.Num2, GameKey.Num3, GameKey.Num4, GameKey.Num5,
            GameKey.Num6, GameKey.Num7, GameKey.Num8, GameKey.Num9,
        };

        [SerializeField] private Text _titleText;
        [SerializeField] private Text[] _rowTexts;
        [SerializeField] private Text _hintText;

        private CsRadioGroup _group = CsRadioGroup.A;
        private List<CsRadioLine> _entries;
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

            _rowTexts = new Text[MaxEntries];
            for (var i = 0; i < _rowTexts.Length; i++)
            {
                var text = CsHudTheme.CreateText($"Row{i}", boxRt, string.Empty, 20, TextAnchor.MiddleLeft,
                    CsHudTheme.TextHud);
                CsHudTheme.PlaceTopLeft(text.rectTransform,
                    new Vector2(24f, -HeaderHeight - i * RowHeight), new Vector2(560f, RowHeight));
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
                if (_titleText == null || _rowTexts == null || _rowTexts.Length < MaxEntries)
                    Game.Logger?.Error(Tag,
                        $"RadioMenuPanel 文本引用不完整（预制体未由 UiBuilder 生成？），条目可能显示不出来");
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
            Game.Logger?.Info(Tag, $"无线电菜单已打开：{GroupTitles[(int)_group]}（{_entries.Count} 条）");
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

            // 1~9 选择（只覆盖本组实际条数）
            var count = _entries?.Count ?? 0;
            for (var i = 0; i < count && i < NumberKeys.Length; i++)
            {
                if (!input.GetKeyDown(NumberKeys[i])) continue;
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

        /// <summary>菜单数字键 → 原版 <c>radio1</c>/<c>radio2</c>/<c>radio3</c> 三组之一。</summary>
        private static CsRadioSet SetOf(CsRadioGroup group)
        {
            switch (group)
            {
                case CsRadioGroup.A: return CsRadioSet.Radio1;
                case CsRadioGroup.B: return CsRadioSet.Radio2;
                case CsRadioGroup.C: return CsRadioSet.Radio3;
                default:
                    Game.Logger?.Warn(Tag, $"RadioMenuPanel 收到未知分组 {(int)group}，按 radio1 处理");
                    return CsRadioSet.Radio1;
            }
        }

        private void RefreshLabels()
        {
            var index = Mathf.Clamp((int)_group, 0, GroupTitles.Length - 1);
            if (_titleText != null) _titleText.text = GroupTitles[index];

            _entries = CsRadio.Of(SetOf(_group));
            if (_entries.Count > _rowTexts.Length)
                Game.Logger?.Error(Tag,
                    $"无线电组 {GroupTitles[index]} 有 {_entries.Count} 条，超过行控件 {_rowTexts.Length} 个，超出的条目显示不出来");

            for (var i = 0; i < _rowTexts.Length; i++)
            {
                if (_rowTexts[i] == null) continue;
                _rowTexts[i].text = i < _entries.Count ? $"{i + 1}. {_entries[i].Text}" : string.Empty;
            }

            if (_hintText != null)
                _hintText.text = $"1~{_entries.Count} 选择　·　松开开关键（Z/X/C）或 ESC 收起";
        }

        private void Send(int index)
        {
            if (_entries == null || index < 0 || index >= _entries.Count)
            {
                Game.Logger?.Warn(Tag,
                    $"无线电选择序号 {index} 越界（本组共 {_entries?.Count ?? 0} 条），已忽略");
                return;
            }

            var text = _entries[index].Text;
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
