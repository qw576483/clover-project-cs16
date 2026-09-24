using System.Collections.Generic;
using System.Text;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 控制台面板：命令输入与回显（<c>bot_add</c> / <c>bot_kick</c> / <c>bot_difficulty</c>）。
    ///
    /// <para><b>开关键</b>：<c>GameKey</c> 枚举里**没有 BackQuote（`）**（契约缺口：引擎键盘枚举未提供该键），
    /// 因此这里与 <see cref="HudPanel"/> 一致：<c>/</c>（<see cref="GameKey.Slash"/>）或
    /// <c>0</c>（<see cref="GameKey.Num0"/>）都能开关控制台。</para>
    ///
    /// <para><b>支持的命令</b>：<c>bot_add [easy|normal|hard]</c>、<c>bot_kick</c>、
    /// <c>bot_difficulty &lt;easy|normal|hard&gt;</c>；其余一律回显"未知命令"
    /// （不静默、不猜）。命令最终都只发 <c>Events.*</c> 事件，由比赛模块执行。</para>
    ///
    /// <para><b>层</b>：<see cref="UILayer.Top"/>。控制台要能盖在任何弹窗（买枪 / H 菜单）之上，
    /// 而且它不该参与 <c>Popup</c> 的互斥（开控制台把买枪菜单顶掉是错的）。</para>
    /// </summary>
    public class ConsolePanel : CsPanelBase
    {
        /// <summary>控制台里同时显示多少行（从最旧的可见行到最新一行）。</summary>
        public const int VisibleLines = 18;

        private const float BoxWidth = 1400f;
        private const float BoxHeight = 640f;
        private const int MaxInputLength = 64;

        private static readonly string[] CommandList =
        {
            "bot_add [easy|normal|hard]",
            "bot_kick",
            "bot_difficulty <easy|normal|hard>",
        };

        [SerializeField] private Text _logText;
        [SerializeField] private Text _hintText;
        [SerializeField] private InputField _input;
        [SerializeField] private Text _statusText;

        private readonly StringBuilder _builder = new StringBuilder(4096);
        private int _renderedVersion = -1;
        private bool _warnedRefs;

        public override UILayer Layer => UILayer.Top;

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.9f), true);
            UIFactory.Stretch(backdrop.rectTransform);

            var box = UIFactory.CreatePanel("Box", root, new Color(0.06f, 0.06f, 0.06f, 0.95f), true);
            CsHudTheme.PlaceCenter(box.rectTransform, Vector2.zero, new Vector2(BoxWidth, BoxHeight));
            var boxRt = box.rectTransform;

            var title = CsHudTheme.CreateText("Title", boxRt, "CONSOLE", 26, TextAnchor.MiddleLeft,
                CsHudTheme.Message);
            CsHudTheme.PlaceTopLeft(title.rectTransform, new Vector2(20f, -12f), new Vector2(700f, 32f));

            var shortcut = CsHudTheme.CreateText("Shortcut", boxRt,
                "GameKey 无 BackQuote → 用 / 或 0 开关；ESC 关闭", 16, TextAnchor.MiddleRight,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceTopRight(shortcut.rectTransform, new Vector2(-20f, -12f), new Vector2(700f, 32f));

            _logText = CsHudTheme.CreateText("Log", boxRt, string.Empty, 16, TextAnchor.LowerLeft,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopLeft(_logText.rectTransform, new Vector2(20f, -52f),
                new Vector2(BoxWidth - 40f, VisibleLines * 20f));
            _logText.verticalOverflow = VerticalWrapMode.Truncate;

            _hintText = CsHudTheme.CreateText("Hint", boxRt,
                "命令：" + string.Join("　·　", CommandList), 16, TextAnchor.MiddleLeft, CsHudTheme.TextDim);
            CsHudTheme.PlaceBottomLeft(_hintText.rectTransform, new Vector2(20f, 96f),
                new Vector2(BoxWidth - 40f, 24f));

            // 输入框的构造已收敛到引擎（通用件只做一份），这里直接调它，只把 HUD 的配色 / 字号档
            // （CsHudTheme.InputFieldStyle）传进去。
            // InputField.placeholder 声明类型为 Graphic，必须 `as Text` 才能设字体 / 字号 / 文案（CS1061）；
            // 那条坑的说明与正确写法都在 UIFactory.CreateInputField 的注释里。
            _input = UIFactory.CreateInputField("Input", boxRt, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(20f, 52f), new Vector2(BoxWidth - 40f, 36f),
                "输入命令后回车（命令见上一行）", MaxInputLength, CsHudTheme.InputFieldStyle);

            _statusText = CsHudTheme.CreateText("Status", boxRt, string.Empty, 17, TextAnchor.MiddleLeft,
                CsHudTheme.Warn);
            CsHudTheme.PlaceBottomLeft(_statusText.rectTransform, new Vector2(20f, 22f),
                new Vector2(BoxWidth - 40f, 24f));
        }

        public override void OnOpen(object param)
        {
            if (!_warnedRefs)
            {
                _warnedRefs = true;
                if (_logText == null || _input == null)
                    Game.Logger?.Error(Tag, "ConsolePanel 引用缺失（预制体未由 UiBuilder 生成或已被改动），控制台不可用");
            }

            CsIngameCursor.UnlockForMenu(nameof(ConsolePanel));

            LogBuffer.Drain();
            Render(true);

            if (_input != null)
            {
                _input.text = string.Empty;
                _input.ActivateInputField();
            }

            Game.Logger?.Info(Tag, "控制台已打开（显示最近日志；命令：bot_add / bot_kick / bot_difficulty）");
        }

        public override void OnClose()
        {
            CsIngameCursor.RelockIfIdle(nameof(ConsolePanel), CsIngameCursor.AnyMenuOpen());
        }

        public override void OnUpdate(float dt)
        {
            LogBuffer.Drain();
            Render(false);

            var input = Game.Input;
            if (input == null) return;

            if (input.GetKeyDown(GameKey.Escape))
            {
                Game.UI.Close<ConsolePanel>();
                return;
            }

            // 开关控制台键只在输入框没聚焦时才生效：不然想在命令里打 "/" 会直接被收起
            var typing = _input != null && _input.isFocused;
            if (!typing && (input.GetKeyDown(GameKey.Slash) || input.GetKeyDown(GameKey.Num0)))
            {
                Game.UI.Close<ConsolePanel>();
                return;
            }

            if (input.GetKeyDown(GameKey.Enter) || input.GetKeyDown(GameKey.KeypadEnter)) Submit();
        }

        // ═══════════════════════ 显示 ═══════════════════════

        private void Render(bool force)
        {
            if (_logText == null) return;
            if (!force && _renderedVersion == LogBuffer.Version) return;
            _renderedVersion = LogBuffer.Version;

            var lines = LogBuffer.Lines;
            var from = Mathf.Max(0, lines.Count - VisibleLines);

            _builder.Length = 0;
            for (var i = from; i < lines.Count; i++)
            {
                if (_builder.Length > 0) _builder.Append('\n');
                _builder.Append(lines[i]);
            }
            _logText.text = _builder.ToString();
        }

        private void SetStatus(string text, bool error)
        {
            if (_statusText == null) return;
            _statusText.text = text;
            _statusText.color = error ? CsHudTheme.Danger : CsHudTheme.Message;
        }

        // ═══════════════════════ 命令 ═══════════════════════

        private void Submit()
        {
            var raw = _input != null ? _input.text : null;
            if (string.IsNullOrWhiteSpace(raw)) return;

            raw = raw.Trim();
            if (_input != null)
            {
                _input.text = string.Empty;
                _input.ActivateInputField();
            }

            LogBuffer.Push(raw);

            var tokens = raw.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            var command = tokens[0].ToLowerInvariant();

            switch (command)
            {
                case "bot_add":
                    BotAdd(tokens);
                    break;

                case "bot_kick":
                    Game.Logger?.Info(Tag, $"控制台命令 {raw} → 发 {Events.KickBot}");
                    Game.Event.Emit(Events.KickBot);
                    SetStatus("已发送：踢出机器人", false);
                    LogBuffer.Push("bot_kick → 已发送（事由比赛模块执行，结果见 [Match] 日志）");
                    break;

                case "bot_difficulty":
                    BotDifficulty(tokens);
                    break;

                default:
                    Game.Logger?.Info(Tag, $"控制台收到未知命令：{raw}");
                    SetStatus($"未知命令：{command}", true);
                    LogBuffer.Push(
                        $"未知命令：{command}（可用：" + string.Join(" / ", CommandList) + "）");
                    break;
            }
        }

        private void BotAdd(string[] tokens)
        {
            var difficulty = CsBotDifficulty.Normal;
            if (tokens.Length > 1 && !TryParseDifficulty(tokens[1], out difficulty))
            {
                Game.Logger?.Warn(Tag, $"控制台 bot_add 的难度参数无法识别：{tokens[1]}");
                SetStatus($"无法识别的难度：{tokens[1]}（可用 easy / normal / hard）", true);
                LogBuffer.Push($"bot_add {tokens[1]} → 无法识别的难度（可用 easy / normal / hard）");
                return;
            }

            Game.Logger?.Info(Tag, $"控制台命令 bot_add → 添加机器人（{difficulty}）");
            Game.Event.Emit(Events.AddBot, difficulty);
            SetStatus($"已发送：添加机器人（{difficulty}）", false);
            LogBuffer.Push($"bot_add → 已发送（难度 {difficulty}）；结果见 [Match] 日志");
        }

        private void BotDifficulty(string[] tokens)
        {
            if (tokens.Length < 2 || !TryParseDifficulty(tokens[1], out var difficulty))
            {
                Game.Logger?.Warn(Tag, $"控制台 bot_difficulty 参数缺失或无法识别：{string.Join(" ", tokens)}");
                SetStatus("用法：bot_difficulty <easy|normal|hard>", true);
                LogBuffer.Push("bot_difficulty → 用法：bot_difficulty <easy|normal|hard>");
                return;
            }

            Game.Logger?.Info(Tag, $"控制台命令 bot_difficulty → {difficulty}");
            Game.Event.Emit(Events.SetBotDifficulty, difficulty);
            SetStatus($"已发送：机器人难度 → {difficulty}", false);
            LogBuffer.Push($"bot_difficulty {difficulty} → 已发送；结果见 [Match] 日志");
        }

        private static bool TryParseDifficulty(string text, out CsBotDifficulty difficulty)
        {
            switch (text?.ToLowerInvariant())
            {
                case "easy":
                case "0":
                    difficulty = CsBotDifficulty.Easy;
                    return true;
                case "normal":
                case "1":
                    difficulty = CsBotDifficulty.Normal;
                    return true;
                case "hard":
                case "2":
                    difficulty = CsBotDifficulty.Hard;
                    return true;
                default:
                    difficulty = CsBotDifficulty.Normal;
                    return false;
            }
        }
    }
}
