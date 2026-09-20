using System;
using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// Options —— **逐字段重建自原版 7 个子页的 `.res`**：
    /// `原版资源/cs16src/cs16game/app/valve/resource/optionssub{audio,video,mouse,keyboard,multiplayer,voice,advanced}.res`。
    ///
    /// <para>
    /// 每个控件的 `xpos/ypos/wide/tall` **按原版值写进下面的表**，建件时统一乘
    /// <see cref="CsUiStyle.ResScale"/>（= 1080/480）—— 换算只有一处，代码里的数与 `.res` 一一对应。
    /// 文案逐字取自 `valve/resource/gameui_english.txt` 的 `#GameUI_*`（原文与行号见各行注释）。
    /// </para>
    ///
    /// <para>
    /// ⛔ <b>页签条（7 个页签的位置）在载体里拿不到</b>：本包缺 GameUI 的 `OptionsDialog.res`
    /// （它在 GameUI 静态库里，不在两张 ISO 的资源目录里）⇒ 页签条是**按原版配色 + 原版字体 + 就近对齐
    /// 重建**的，**逐条登记为「允许的差异（本项目新增：tab 条坐标无载体出处）」**。
    /// 子页整体落点（<see cref="PageLeftX"/> / <see cref="PageTopY"/>）同理。
    /// </para>
    ///
    /// <para>
    /// 与 CS 1.6 一致的三个对话框动作：<b>Apply</b>（保存并留在面板）、<b>Cancel</b>（丢弃本次改动并关闭）、
    /// <b>OK</b>（保存并关闭）。文案用原版 token `#GameUI_Apply` / `#GameUI_Cancel` / `#GameUI_OK`
    /// （`gameui_english.txt:29/28/42`），三个按钮的坐标来自 `optionssubmultiplayer.res:3-68`
    /// —— 原版把这三个按钮就写在该子页里（对话框级按钮），本片只建一次、跨页可见。
    /// </para>
    ///
    /// <para>
    /// <b>为什么事件绑定全在 <see cref="OnOpen"/> 里做</b>：预制体只存"节点 + 引用"，
    /// <c>onClick.AddListener</c> 挂的运行时监听器**不会**进预制体（<c>CsPanelBase.Bind</c> 的注释）⇒
    /// 这里在 <see cref="OnOpen"/> 里**按节点名走一遍树**，逐控件绑事件（<see cref="WireAll"/>）。
    /// </para>
    ///
    /// <para>层级给 <see cref="UILayer.Top"/>：暂停菜单是 Popup，从暂停里打开 Options 时它必须盖在暂停菜单之上。</para>
    /// </summary>
    public class OptionsPanel : CsPanelBase
    {
        // ═══════════════════ 页签条（**本项目新增：无载体出处**）═══════════════════
        /// <summary>页签名 = 原版 7 页（`gameui_english.txt`：Audio:99 / Video:100 / Mouse:98 / Keyboard:97 / Multiplayer:41 / Voice:101 / Advanced(`GameUI_AdvancedNoEllipsis`):44）。</summary>
        private static readonly string[] TabNames = { "Audio", "Video", "Mouse", "Keyboard", "Multiplayer", "Voice", "Advanced" };

        /// <summary>页签条左上角（画布坐标）。⛔ 无出处 —— 见类注释。</summary>
        private const float TabLeftX = 171f, TabTopY = -84f;
        /// <summary>单个页签的尺寸 / 步距（画布坐标）。⛔ 无出处 —— 见类注释。</summary>
        private const float TabWidth = 200f, TabHeight = 48f, TabStep = 208f;

        /// <summary>子页内容原点（画布坐标）。⛔ 无出处：子页在缺失的 `OptionsDialog.res` 里的落点未知；取与 `teammenu.res` 同一个 Frame 左边距（76 设计 px → 171 画布 px）"就近对齐"。</summary>
        private const float PageLeftX = 171f, PageTopY = -152f;

        /// <summary>面板标题文案 = `#GameUI_Options`（`gameui_english.txt:96` → `Options`）。</summary>
        private const string TitleText = "Options";

        // ═══════════════════ 各页控件表（坐标 = 原版 `.res` 的设计空间值，单位 = 设计像素）═══════════════════

        private enum Kind { Label, Slider, Combo, Check, Button, Box, Entry }

        /// <summary>一行 = `.res` 里的一个控件。`X/Y/W/H` **逐字照搬 `.res`**，建件时统一 ×2.25。</summary>
        private sealed class ResCtrl
        {
            public string Name;       // .res 的 fieldName
            public Kind Kind;
            public float X, Y, W, H;  // .res 的 xpos/ypos/wide/tall
            public string Text;       // 文案（英文原文）/ 控件的初值
            public bool Dull;         // .res 的 dulltext == 1 ⇒ 用 LabelDimText（原版 = 说明文字色）
            public bool LogOnly;      // 原版有、本工程没有对应实现 ⇒ 只显示并留一条日志
            public string[] Options;  // Combo 的档位
            public float Min, Max;    // Slider 的取值范围
            public bool Whole;        // Slider 取整
            public int MaxChars = 8;  // Entry 的最大长度

            public ResCtrl(string name, Kind kind, float x, float y, float w, float h, string text = null)
            {
                Name = name;
                Kind = kind;
                X = x;
                Y = y;
                W = w;
                H = h;
                Text = text;
            }
        }

        // ── audio（optionssubaudio.res）──
        private static readonly ResCtrl[] AudioCtrls =
        {
            new ResCtrl("SFX Slider", Kind.Slider, 40f, 57f, 160f, 36f) { Min = 0f, Max = 1f },
            new ResCtrl("MP3 Volume", Kind.Slider, 40f, 118f, 160f, 36f) { Min = 0f, Max = 1f },
            new ResCtrl("Suit Slider", Kind.Slider, 40f, 183f, 160f, 36f) { Min = 0f, Max = 1f },
            new ResCtrl("sfx label", Kind.Label, 42f, 34f, 160f, 24f, "Sound effects volume"),   // #GameUI_SoundEffectVolume :59
            new ResCtrl("mp3 label", Kind.Label, 40f, 96f, 140f, 24f, "MP3 volume *"),           // #GameUI_MP3Volume :61
            new ResCtrl("suit label", Kind.Label, 40f, 160f, 140f, 24f, "HEV suit volume"),      // #GameUI_HEVSuitVolume :60
            new ResCtrl("MilesAudioLabel", Kind.Label, 43f, 230f, 410f, 72f, "* Miles sound system sucks")
                { Dull = true },                                                                  // #GameUI_Miles_Audio :161
        };

        // ── video（optionssubvideo.res）──
        private static readonly ResCtrl[] VideoCtrls =
        {
            new ResCtrl("Renderer", Kind.Combo, 40f, 52f, 160f, 24f)
                { Options = new[] { "Software", "OpenGL", "D3D" }, LogOnly = true },              // :73-75
            new ResCtrl("AspectRatio", Kind.Combo, 40f, 120f, 160f, 24f)
                { Options = new[] { "Normal", "Widescreen" }, LogOnly = true },                   // :136-137
            new ResCtrl("Resolution", Kind.Combo, 248f, 52f, 160f, 24f)
                { Options = new[] { "1920 x 1080" }, LogOnly = true },
            new ResCtrl("ColorDepth", Kind.Combo, 248f, 120f, 160f, 24f)
                { Options = new[] { "Medium (16 bit)", "Highest (32 bit)" }, LogOnly = true },    // :215-216
            new ResCtrl("Windowed", Kind.Check, 33f, 160f, 165f, 24f, "Run in a window") { LogOnly = true },   // :71
            new ResCtrl("DetailTextures", Kind.Check, 241f, 160f, 160f, 24f, "Use high quality models")
                { LogOnly = true },                                                               // #GameUI_DetailTextures :48 同串
            new ResCtrl("Brightness", Kind.Slider, 40f, 217f, 160f, 50f) { Min = 0f, Max = 1f, LogOnly = true },
            new ResCtrl("Gamma", Kind.Slider, 248f, 217f, 160f, 50f) { Min = 0f, Max = 1f, LogOnly = true },
            new ResCtrl("brightness label", Kind.Label, 40f, 195f, 140f, 24f, "Brightness"),      // #GameUI_Brightness :76
            new ResCtrl("Gamma label", Kind.Label, 248f, 195f, 140f, 24f, "Gamma"),               // #GameUI_Gamma :77
            new ResCtrl("Label2", Kind.Label, 40f, 28f, 160f, 24f, "Renderer"),                   // #GameUI_Renderer :72
            new ResCtrl("Label1", Kind.Label, 248f, 28f, 160f, 24f, "Resolution"),                // #GameUI_Resolution :78
            new ResCtrl("Label4", Kind.Label, 40f, 96f, 160f, 24f, "Display Mode"),               // #GameUI_DisplayMode :135
            new ResCtrl("Label3", Kind.Label, 248f, 96f, 160f, 24f, "Color Quality"),             // #GameUI_ColorQuality :142
            new ResCtrl("Label5", Kind.Label, 40f, 265f, 427f, 36f,
                "Note: changing video options will cause the game to exit and restart.") { Dull = true },  // #GameUI_VideoRestart :79
        };

        // ── mouse（optionssubmouse.res）──
        private static readonly ResCtrl[] MouseCtrls =
        {
            new ResCtrl("ReverseMouse", Kind.Check, 36f, 32f, 140f, 28f, "Reverse mouse"),        // :6
            new ResCtrl("Reverse Mouse label", Kind.Label, 184f, 35f, 300f, 24f, "Reverse mouse up-down axis") { Dull = true }, // :7
            new ResCtrl("MouseLook", Kind.Check, 36f, 54f, 140f, 28f, "Mouse look") { LogOnly = true },        // :8
            new ResCtrl("MouseLookLabel", Kind.Label, 184f, 57f, 300f, 24f, "Use the mouse to look around") { Dull = true },     // :9
            new ResCtrl("MouseFilter", Kind.Check, 36f, 76f, 140f, 28f, "Mouse filter") { LogOnly = true },    // :10
            new ResCtrl("MouseFilterLabel", Kind.Label, 184f, 79f, 300f, 24f, "Smooth out mouse movement") { Dull = true },      // :11
            new ResCtrl("Joystick", Kind.Check, 36f, 98f, 140f, 28f, "Joystick") { LogOnly = true },           // :13
            new ResCtrl("JoystickLabel", Kind.Label, 184f, 101f, 300f, 24f, "Enable the joystick") { Dull = true },              // :14
            new ResCtrl("JoystickLook", Kind.Check, 36f, 120f, 140f, 28f, "Joystick look") { LogOnly = true }, // :15
            new ResCtrl("Label2", Kind.Label, 184f, 123f, 300f, 24f, "Use the joystick to look around") { Dull = true },         // :16
            new ResCtrl("Auto-Aim", Kind.Check, 36f, 142f, 140f, 24f, "Auto-Aim") { LogOnly = true },           // :17
            new ResCtrl("AutoaimLabel", Kind.Label, 184f, 145f, 300f, 24f, "Aims at enemies automatically.") { Dull = true },    // :18
            new ResCtrl("Label3", Kind.Label, 40f, 180f, 164f, 24f, "Mouse sensitivity"),          // #GameUI_MouseSensitivity :12
            new ResCtrl("Slider", Kind.Slider, 40f, 202f, 272f, 40f)
                { Min = CsPlayerSettingsStore.MinSensitivity, Max = CsPlayerSettingsStore.MaxSensitivity },
            new ResCtrl("SensitivityLabel", Kind.Entry, 320f, 202f, 48f, 24f, "") { MaxChars = 8 },
        };

        // ── keyboard（optionssubkeyboard.res）──
        private static readonly ResCtrl[] KeyboardCtrls =
        {
            new ResCtrl("listpanel_keybindlist", Kind.Box, 12f, 12f, 480f, 258f),                  // ControlName ListPanel
            new ResCtrl("Defaults", Kind.Button, 12f, 278f, 90f, 24f, "Use Defaults") { LogOnly = true },     // :65
            new ResCtrl("ChangeKeyButton", Kind.Button, 310f, 278f, 84f, 24f, "Edit key") { LogOnly = true }, // :66
            new ResCtrl("ClearKeyButton", Kind.Button, 408f, 278f, 84f, 24f, "Clear Key") { LogOnly = true }, // :67
        };

        // ── multiplayer（optionssubmultiplayer.res）──
        private static readonly ResCtrl[] MultiplayerCtrls =
        {
            new ResCtrl("NameLabel", Kind.Label, 20f, 30f, 96f, 24f, "Player name"),              // #GameUI_PlayerName :45
            new ResCtrl("NameEntry", Kind.Entry, 20f, 54f, 140f, 24f, CsPlayerSettingsStore.DefaultPlayerName)
                { MaxChars = CsPlayerSettingsStore.MaxNameLength },  // 原版 maxchars 63；本项目契约 MaxNameLength
            new ResCtrl("Label1", Kind.Label, 20f, 96f, 152f, 24f, "Player model"),                // #GameUI_PlayerModel :49
            new ResCtrl("Player model", Kind.Combo, 20f, 120f, 140f, 24f)
                { Options = new[] { "urban", "gsg9", "sas", "gign", "leet", "arctic", "guerilla" }, LogOnly = true },
            new ResCtrl("ModelImage", Kind.Box, 176f, 78f, 164f, 200f),
            new ResCtrl("Colors", Kind.Label, 20f, 164f, 88f, 24f, "Colors"),                      // #GameUI_ColorSliders :141
            new ResCtrl("Primary Color Slider", Kind.Slider, 20f, 188f, 140f, 39f) { Min = 0f, Max = 1f, LogOnly = true },
            new ResCtrl("Secondary Color Slider", Kind.Slider, 20f, 223f, 140f, 42f) { Min = 0f, Max = 1f, LogOnly = true },
            new ResCtrl("Label2", Kind.Label, 356f, 30f, 124f, 24f, "Spraypaint image"),           // #GameUI_SpraypaintImage :50
            new ResCtrl("SpraypaintList", Kind.Combo, 356f, 54f, 124f, 24f) { Options = new[] { "No spray" }, LogOnly = true },
            new ResCtrl("LogoImage", Kind.Box, 356f, 86f, 64f, 64f),
            new ResCtrl("SpraypaintColor", Kind.Combo, 356f, 169f, 124f, 24f) { Options = new[] { "No spray" }, LogOnly = true },
            new ResCtrl("High Quality Models", Kind.Check, 173f, 280f, 174f, 24f, "Use high quality models")
                { LogOnly = true },                                                                // #GameUI_HighModels :48
            new ResCtrl("Advanced", Kind.Button, 389f, 266f, 90f, 24f, "Advanced..."),            // #GameUI_AdvancedEllipsis :43
        };

        // ── voice（optionssubvoice.res）──
        private static readonly ResCtrl[] VoiceCtrls =
        {
            new ResCtrl("voice_modenable", Kind.Check, 33f, 32f, 300f, 28f, "Enable voice in this game") { LogOnly = true },  // :80
            new ResCtrl("Microphonelabel", Kind.Label, 42f, 70f, 180f, 24f, "Voice transmit volume *") { Dull = true },        // :87
            new ResCtrl("MicMeter", Kind.Box, 40f, 148f, 158f, 32f),
            new ResCtrl("#GameUI_MicrophoneVolume", Kind.Slider, 40f, 94f, 160f, 26f) { Min = 0f, Max = 1f, LogOnly = true },
            new ResCtrl("ReceiveLabel", Kind.Label, 246f, 70f, 180f, 24f, "Voice receive volume *") { Dull = true },          // :86
            new ResCtrl("VoiceReceive", Kind.Slider, 246f, 94f, 160f, 42f) { Min = 0f, Max = 1f, LogOnly = true },
            new ResCtrl("TestMicrophone", Kind.Button, 40f, 176f, 160f, 24f, "Test Microphone") { LogOnly = true },            // :84
            new ResCtrl("MicBoost", Kind.Check, 33f, 212f, 250f, 28f, "Boost microphone gain") { LogOnly = true },              // :81
            new ResCtrl("MilesVoiceLabel", Kind.Label, 43f, 250f, 350f, 48f, "* Miles sound system sucks") { Dull = true },    // :160
        };

        // ── advanced（optionssubadvanced.res + 本项目新增的两项）──
        private static readonly ResCtrl[] AdvancedCtrls =
        {
            new ResCtrl("ContentlockButton", Kind.Button, 40f, 42f, 110f, 24f, "Content lock") { LogOnly = true },  // #GameUI_ContentLock :19
            new ResCtrl("ContentlockLabel", Kind.Label, 162f, 34f, 300f, 60f,
                "Press this button and enter password to disable\nvisuals inappropriate for younger players.") { Dull = true }, // :20
            // ★ 本项目新增（原版视频页**没有** FOV、也**没有**"显示 FPS"）：按任务书放在 Advanced 页，
            //   文案里显式标注「本项目新增」，供验收逐条对账。坐标沿用原版该页的左右两列（x 40 / 248）与
            //   原版 label/slider 的尺寸（label 160×24、slider 160×50），不是新的排布口径。
            new ResCtrl("FovLabel", Kind.Label, 40f, 120f, 160f, 24f, "Field of view（本项目新增）"),
            new ResCtrl("Fov", Kind.Slider, 40f, 142f, 160f, 50f)
                { Min = CsPlayerSettingsStore.MinFov, Max = CsPlayerSettingsStore.MaxFov, Whole = true },
            new ResCtrl("ShowFps", Kind.Check, 40f, 220f, 165f, 24f, "Display FPS（本项目新增）"),
        };

        // ── 对话框三个按钮（原版写在 optionssubmultiplayer.res 里的对话框级按钮）──
        private static readonly ResCtrl[] DialogButtonCtrls =
        {
            new ResCtrl("Ok", Kind.Button, 308f, 322f, 64f, 24f, "OK"),            // #GameUI_OK :42
            new ResCtrl("Cancel", Kind.Button, 378f, 322f, 64f, 24f, "Cancel"),    // #GameUI_Cancel :28
            new ResCtrl("Apply", Kind.Button, 448f, 322f, 64f, 24f, "Apply"),      // #GameUI_Apply :29
        };

        private static readonly ResCtrl[][] PageTables =
        {
            AudioCtrls, VideoCtrls, MouseCtrls, KeyboardCtrls, MultiplayerCtrls, VoiceCtrls, AdvancedCtrls,
        };

        /// <summary>键盘页的按键表（本工程项目侧真实按键；原版该处是一个 `ListPanel`）。</summary>
        private const string KeybindText =
            "Action                Key\n" +
            "Move                  W A S D\n" +
            "Jump                  Space\n" +
            "Duck                  Left Ctrl\n" +
            "Walk (silent)         Left Shift\n" +
            "Attack                Mouse Left\n" +
            "Zoom (AWP / Scout)    Mouse Right\n" +
            "Reload                R\n" +
            "Drop weapon           G\n" +
            "Switch weapon         1 ~ 5\n" +
            "Use / plant / defuse  E (hold)\n" +
            "Buy menu              B\n" +
            "Scoreboard            Tab (hold)\n" +
            "Bot / quick menu      H\n" +
            "Radio                 Z / X / C\n" +
            "Console               / or Num0\n" +
            "Pause menu            Esc";

        // ═══════════════════ 序列化引用（进预制体）═══════════════════

        [SerializeField] private Text _titleLabel;
        [SerializeField] private Text _keybindText;
        [SerializeField] private Button[] _tabButtons;
        [SerializeField] private RectTransform[] _pages;

        // ═══════════════════ 运行时句柄（不序列化，每次 OnOpen 重建）═══════════════════

        private sealed class Handle
        {
            public Slider Slider;
            public InputField Entry;
            public CsUiStyle.CheckRow Check;
        }

        private readonly Dictionary<string, Handle> _handles = new Dictionary<string, Handle>();
        private readonly Dictionary<string, Action<float>> _realSliders = new Dictionary<string, Action<float>>();
        private readonly Dictionary<string, Action<string>> _realEntries = new Dictionary<string, Action<string>>();
        private readonly Dictionary<string, Action<bool>> _realChecks = new Dictionary<string, Action<bool>>();

        private CsPlayerSettings _working;

        /// <summary>盖在暂停菜单（Popup）之上。</summary>
        public override UILayer Layer => UILayer.Top;

        public override void BuildLayout(RectTransform root)
        {
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);

            _titleLabel = CsUiStyle.CreateLabel("Title", root, TitleText, CsUiStyle.OriginalTitleFontSize,
                new Vector2(TabLeftX, -24f), new Vector2(700f, 48f), TextAnchor.MiddleLeft, CsUiStyle.Text);

            // ── 页签条（本项目新增：无载体出处，见类注释）──
            _tabButtons = new Button[TabNames.Length];
            for (var i = 0; i < TabNames.Length; i++)
            {
                _tabButtons[i] = CsUiStyle.CreateOriginalButton("Tab_" + TabNames[i], root, TabNames[i],
                    new Vector2(TabLeftX + TabStep * i, TabTopY), new Vector2(TabWidth, TabHeight), null);
            }

            // ── 7 个子页：每个页一个容器节点，其子控件用 `.res` 的页内坐标 ──
            var pageOrigin = new Vector2(PageLeftX, PageTopY);
            _pages = new RectTransform[TabNames.Length];
            for (var i = 0; i < TabNames.Length; i++)
            {
                var page = UIFactory.CreateNode("Page_" + TabNames[i], root);
                CsUiStyle.AnchoredTopLeft(page, pageOrigin, new Vector2(1740f, 820f));
                _pages[i] = page;
                BuildCtrls(page, PageTables[i]);
            }

            // 键盘页的 ListPanel 里放本工程真实的按键表（原版该处就是按键列表控件）
            var listRect = FindChild(_pages[3], "listpanel_keybindlist");
            if (listRect != null)
            {
                _keybindText = CsUiStyle.CreateLabel("KeybindText", listRect, KeybindText,
                    CsUiStyle.OriginalFontSize, new Vector2(8f, -8f),
                    ResSize(480f, 258f) - new Vector2(16f, 16f), TextAnchor.UpperLeft, CsUiStyle.Text);
            }

            // ── 对话框按钮（跨页可见；坐标取自 optionssubmultiplayer.res 的页内坐标）──
            BuildCtrls(root, DialogButtonCtrls);

            // 生成期也设一次字体（理由见 CsUiStyle.ApplyOriginalFonts 的注释）
            CsUiStyle.ApplyOriginalFonts(root);
            CsUiStyle.ApplyTitleFont(_titleLabel);
        }

        /// <summary>把一页的控件全部建出来（**不绑事件**：事件一律在 <see cref="OnOpen"/> 的走树里绑）。</summary>
        private void BuildCtrls(RectTransform parent, ResCtrl[] ctrls)
        {
            for (var i = 0; i < ctrls.Length; i++)
            {
                var c = ctrls[i];
                var pos = ResPos(c.X, c.Y);
                var size = ResSize(c.W, c.H);

                switch (c.Kind)
                {
                    case Kind.Label:
                        CsUiStyle.CreateLabel(c.Name, parent, c.Text, CsUiStyle.OriginalFontSize,
                            pos, size, TextAnchor.MiddleLeft, c.Dull ? CsUiStyle.LabelDimText : CsUiStyle.Text);
                        break;

                    case Kind.Slider:
                        var slider = CsUiStyle.CreateSlider(c.Name, parent, pos, size, c.Min, c.Max, c.Min, null);
                        if (slider == null)
                        {
                            Game.Logger.Warn(Tag, $"Options 控件「{c.Name}」的滑块没建出来（CreateSlider 返回 null）");
                            break;
                        }
                        slider.wholeNumbers = c.Whole;
                        break;

                    case Kind.Combo:
                        CsUiStyle.CreateComboRow(c.Name, parent, pos, size, c.Options, 0, null);
                        break;

                    case Kind.Check:
                        CsUiStyle.CreateCheckButton(c.Name, parent, c.Text, pos, size, false, null);
                        break;

                    case Kind.Button:
                        CsUiStyle.CreateOriginalButton(c.Name, parent, c.Text, pos, size, null);
                        break;

                    case Kind.Box:
                        CsUiStyle.CreateBoxRect(c.Name, parent, pos, size, CsUiStyle.ListBg);
                        break;

                    case Kind.Entry:
                        CsUiStyle.CreateInputField(c.Name, parent, pos, size, c.Text, c.MaxChars);
                        break;

                    default:
                        Game.Logger.Warn(Tag, $"Options 控件「{c.Name}」的类型 {c.Kind} 没有建件分支，已跳过");
                        break;
                }
            }
        }

        // ═══════════════════ 事件绑定（每次 OnOpen 走树重绑）═══════════════════

        /// <summary>真设置的绑定表；表里没有的控件 = 原版有、本工程无对应实现 ⇒ 绑"只记日志"。</summary>
        private void FillRealBindings()
        {
            _realSliders.Clear();
            _realEntries.Clear();
            _realChecks.Clear();

            // 音量三项（原版 audio 页三支 `CCvarSlider`）
            _realSliders["audio/SFX Slider"] = v =>
            {
                if (_working == null) return;
                _working.SfxVolume = v;
                RefreshValueLabels();
            };
            _realSliders["audio/MP3 Volume"] = v =>
            {
                if (_working == null) return;
                _working.BgmVolume = v;
                RefreshValueLabels();
            };
            // ⚠️ 原版 `Suit Slider` = HEV 护甲音量；CS 里没有 HEV 护甲语音 ⇒ 本项目用它承载既有的
            //    "主音量"设置（登记在验收表「允许的差异」：本项目新增映射）。
            _realSliders["audio/Suit Slider"] = v =>
            {
                if (_working == null) return;
                _working.MasterVolume = v;
                RefreshValueLabels();
            };

            // 鼠标灵敏度（原版 mouse 页的 `Slider` + `SensitivityLabel`）
            _realSliders["mouse/Slider"] = v =>
            {
                if (_working == null) return;
                _working.MouseSensitivity = v;
                RefreshValueLabels();
            };
            _realEntries["mouse/SensitivityLabel"] = text =>
            {
                if (_working == null) return;
                if (!float.TryParse(text, out var parsed))
                {
                    Game.Logger.Warn(Tag, $"鼠标灵敏度输入「{text}」不是数字，忽略");
                    RefreshValueLabels();
                    return;
                }
                _working.MouseSensitivity = Mathf.Clamp(parsed, CsPlayerSettingsStore.MinSensitivity,
                    CsPlayerSettingsStore.MaxSensitivity);
                RefreshValueLabels();
            };

            // 反转鼠标 Y（原版 `#GameUI_ReverseMouse`）
            _realChecks["mouse/ReverseMouse"] = on =>
            {
                if (_working == null) return;
                _working.InvertMouseY = on;
                RefreshValueLabels();
            };
            // ★ 本项目新增（advanced 页）：FOV 与"显示 FPS"
            _realSliders["advanced/Fov"] = v =>
            {
                if (_working == null) return;
                _working.Fov = Mathf.RoundToInt(v);
                RefreshValueLabels();
            };
            _realChecks["advanced/ShowFps"] = on =>
            {
                if (_working == null) return;
                _working.ShowFps = on;
                RefreshValueLabels();
            };

            // 玩家名（原版 multiplayer 页的 `NameEntry`）
            _realEntries["multiplayer/NameEntry"] = text =>
            {
                if (_working == null) return;
                _working.PlayerName = text;
                RefreshValueLabels();
            };
        }

        /// <summary>按节点名走一遍树，逐控件绑事件；同时把句柄收进 <see cref="_handles"/>。</summary>
        private void WireAll()
        {
            FillRealBindings();
            _handles.Clear();

            for (var i = 0; i < TabNames.Length; i++)
            {
                if (_pages[i] == null) continue;
                WireCtrls(_pages[i], TabNames[i], PageTables[i]);
            }
            WireCtrls(transform as RectTransform, string.Empty, DialogButtonCtrls);

            // 页签：选中态 + 切页
            if (_tabButtons != null)
            {
                for (var i = 0; i < _tabButtons.Length; i++)
                {
                    var index = i;
                    Bind(_tabButtons[i], () => SelectTab(index), $"{TabNames[index]} 页签");
                }
            }
        }

        private void WireCtrls(RectTransform parent, string page, ResCtrl[] ctrls)
        {
            if (parent == null) return;

            for (var i = 0; i < ctrls.Length; i++)
            {
                var c = ctrls[i];
                var key = KeyOf(page, c.Name);
                var node = FindChild(parent, c.Name);
                if (node == null)
                {
                    Game.Logger.Warn(Tag, $"Options 找不到控件节点「{key}」（预制体与控件表不一致？），该项不可用");
                    continue;
                }

                switch (c.Kind)
                {
                    case Kind.Button:
                        {
                            var btn = node.GetComponent<Button>();
                            if (btn == null)
                            {
                                Game.Logger.Warn(Tag, $"Options 控件「{key}」上找不到 Button 组件");
                                break;
                            }
                            btn.onClick.RemoveAllListeners();
                            AddClickSfx(btn);       // 本分支自己 RemoveAllListeners ⇒ 点击音要显式补（见 CsPanelBase.AddClickSfx）
                            if (IsDialogButton(c.Name))
                            {
                                // Ok / Cancel / Apply 由 BindDialogButtons 单独绑（同一个 OnOpen 里）
                            }
                            else if (c.Name == "Advanced")
                            {
                                // 原版打开 "Multiplayer Advanced" 对话框；本工程没有该对话框 ⇒ 切到 Advanced 页
                                Bind(btn, () => SelectTab(6), "Advanced...");
                            }
                            else
                            {
                                btn.onClick.AddListener(() => LogLimit(key, "clicked"));
                            }
                        }
                        break;

                    case Kind.Combo:
                        {
                            var btn = node.GetComponent<Button>();
                            var text = node.GetComponentInChildren<Text>();
                            if (btn == null)
                            {
                                Game.Logger.Warn(Tag, $"Options 控件「{key}」上找不到 Button 组件（下拉替身）");
                                break;
                            }
                            var options = c.Options;
                            var index = 0;
                            btn.onClick.RemoveAllListeners();
                            AddClickSfx(btn);       // 同上：自绑按钮要显式补点击音
                            btn.onClick.AddListener(() =>
                            {
                                if (options == null || options.Length == 0) return;
                                index = (index + 1) % options.Length;
                                if (text != null) text.text = options[index];
                                LogLimit(key, options[index]);
                            });
                        }
                        break;

                    case Kind.Slider:
                        {
                            var slider = node.GetComponent<Slider>();
                            if (slider == null)
                            {
                                Game.Logger.Warn(Tag, $"Options 控件「{key}」上找不到 Slider 组件");
                                break;
                            }
                            slider.onValueChanged.RemoveAllListeners();
                            Action<float> real;
                            if (_realSliders.TryGetValue(key, out real))
                            {
                                var handler = real;
                                slider.onValueChanged.AddListener(v => handler(v));
                            }
                            else
                            {
                                slider.onValueChanged.AddListener(v => LogLimit(key, v.ToString("0.##")));
                            }
                            _handles[key] = new Handle { Slider = slider };
                        }
                        break;

                    case Kind.Entry:
                        {
                            var entry = node.GetComponent<InputField>();
                            if (entry == null)
                            {
                                Game.Logger.Warn(Tag, $"Options 控件「{key}」上找不到 InputField 组件");
                                break;
                            }
                            entry.onValueChanged.RemoveAllListeners();
                            Action<string> real;
                            if (_realEntries.TryGetValue(key, out real))
                            {
                                var handler = real;
                                entry.onValueChanged.AddListener(v => handler(v));
                            }
                            else
                            {
                                entry.onValueChanged.AddListener(v => LogLimit(key, v));
                            }
                            _handles[key] = new Handle { Entry = entry };
                        }
                        break;

                    case Kind.Check:
                        {
                            var btn = node.GetComponent<Button>();
                            var mark = FindChild(node, "Mark");
                            var label = FindChild(node, "Label");
                            if (btn == null)
                            {
                                Game.Logger.Warn(Tag, $"Options 控件「{key}」上找不到 Button 组件（勾选框）");
                                break;
                            }
                            var row = new CsUiStyle.CheckRow
                            {
                                Box = node,
                                Mark = mark != null ? mark.GetComponent<Image>() : null,
                                Label = label != null ? label.GetComponent<Text>() : null,
                                Btn = btn,
                            };
                            if (row.Mark == null)
                                Game.Logger.Warn(Tag, $"Options 勾选框「{key}」找不到 Mark 子节点，勾选态不会显示");

                            Action<bool> real;
                            var hasReal = _realChecks.TryGetValue(key, out real);
                            var handler2 = real;
                            btn.onClick.RemoveAllListeners();
                            AddClickSfx(btn);       // 同上：自绑按钮要显式补点击音
                            btn.onClick.AddListener(() =>
                            {
                                row.SetState(!row.State);
                                if (hasReal && handler2 != null) handler2(row.State);
                                else LogLimit(key, row.State ? "On" : "Off");
                            });
                            _handles[key] = new Handle { Check = row };
                        }
                        break;

                    case Kind.Label:
                    case Kind.Box:
                        break;

                    default:
                        Game.Logger.Warn(Tag, $"Options 控件「{key}」的类型 {c.Kind} 没有绑定分支，已跳过");
                        break;
                }
            }
        }

        /// <summary>对话框三按钮（`optionssubmultiplayer.res` 里就写着的 Ok / Cancel / Apply）。</summary>
        private void BindDialogButtons()
        {
            Bind(FindButton(string.Empty, "Ok"), OnOk, "OK");
            Bind(FindButton(string.Empty, "Cancel"), OnCancel, "Cancel");
            Bind(FindButton(string.Empty, "Apply"), OnApply, "Apply");
        }

        private static bool IsDialogButton(string name)
        {
            return name == "Ok" || name == "Cancel" || name == "Apply";
        }

        /// <summary>
        /// 句柄键 = `页名/控件名`，**页名一律小写**（与 `.res` 文件名 `optionssub<page>.res` 一致）。
        /// ⚠️ 大小写必须统一：真设置表（<see cref="FillRealBindings"/>）与各 `Get/Set*` 助手用的都是
        /// 小写键；这里若按 `TabNames` 的原样大写拼键，句柄会"查不到" —— 表现为滑杆拿不到当前值、
        /// 数值框不回显（实测踩过：`handles=25` 但 `audio/SFX Slider handle=MISSING`）。
        /// </summary>
        private static string KeyOf(string page, string name)
        {
            return string.IsNullOrEmpty(page) ? name : page.ToLowerInvariant() + "/" + name;
        }

        /// <summary>原版有、本工程没有对应实现的控件：一律留一条日志（不许静默变成"点了没反应"）。</summary>
        private void LogLimit(string key, string value)
        {
            Game.Logger.Info(Tag, $"原版选项「{key}」= {value}：本工程无对应实现，仅复刻控件（见验收表「允许的差异」）");
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_titleLabel, "标题");

            // 预制体里存不下动态字体（见 CsUiStyle.ApplyOriginalFonts）⇒ 每次打开都重设一遍
            CsUiStyle.ApplyOriginalFonts(transform);
            CsUiStyle.ApplyTitleFont(_titleLabel);

            // 工作副本先就位：绑定的回调会读它（Apply / OK 才写盘，Cancel 直接丢弃）
            _working = CsPlayerSettingsStore.Load();

            WireAll();
            BindDialogButtons();
            RefreshFromWorking();
            SelectTab(0);

            Game.Logger.Info(Tag, $"Options 打开（原版 7 页）：{_working.PlayerName}");
        }

        public override void OnClose()
        {
            _working = null;
        }

        // ─────────────────────────── 页签 ───────────────────────────

        private void SelectTab(int index)
        {
            if (_pages == null || index < 0 || index >= _pages.Length)
            {
                Game.Logger.Warn(Tag,
                    $"页签序号 {index} 越界（共 7 页：{string.Join("/", TabNames)}），忽略");
                return;
            }

            for (var i = 0; i < _pages.Length; i++)
            {
                var page = _pages[i];
                if (page != null) page.gameObject.SetActive(i == index);
            }

            RefreshTabColors(index);
        }

        private void RefreshTabColors(int active)
        {
            if (_tabButtons == null) return;
            for (var i = 0; i < _tabButtons.Length; i++)
            {
                var button = _tabButtons[i];
                if (button == null) continue;

                var colors = button.colors;
                colors.normalColor = i == active ? CsUiStyle.AccentDim : CsUiStyle.ButtonBg;
                colors.selectedColor = colors.normalColor;
                colors.highlightedColor = CsUiStyle.AccentDim;
                colors.pressedColor = CsUiStyle.ButtonBg;
                colors.colorMultiplier = 1f;
                button.colors = colors;
            }
        }

        // ─────────────────────────── 按钮 ───────────────────────────

        private void OnApply()
        {
            if (_working == null)
            {
                Game.Logger.Error(Tag, "Options Apply 时没有工作副本（OnOpen 未跑？），已忽略");
                return;
            }

            CsPlayerSettingsStore.Save(_working);
            CsPlayerSettingsStore.ApplyToEngine(_working);
            Game.UI.Toast("设置已保存", 1.5f);
        }

        private void OnCancel()
        {
            // CS 1.6 的 Cancel = 丢弃本次改动。工作副本从没写盘，这里只需把引擎侧音量恢复成盘上的值。
            var saved = CsPlayerSettingsStore.Load();
            CsPlayerSettingsStore.ApplyToEngine(saved);
            _working = null;
            Game.Logger.Info(Tag, "Options Cancel：丢弃本次改动并关闭");
            Game.UI.Close<OptionsPanel>();
        }

        /// <summary>原版 `#GameUI_OK`（`optionssubmultiplayer.res:39`）= 保存并关闭。</summary>
        private void OnOk()
        {
            OnApply();
            Game.UI.Close<OptionsPanel>();
        }

        // ─────────────────────────── 显示 ───────────────────────────

        private void RefreshFromWorking()
        {
            if (_working == null) return;

            SetSlider("audio/SFX Slider", _working.SfxVolume);
            SetSlider("audio/MP3 Volume", _working.BgmVolume);
            SetSlider("audio/Suit Slider", _working.MasterVolume);
            SetSlider("mouse/Slider", _working.MouseSensitivity);
            SetSlider("advanced/Fov", _working.Fov);

            SetEntry("multiplayer/NameEntry", _working.PlayerName);
            SetCheck("mouse/ReverseMouse", _working.InvertMouseY);
            SetCheck("advanced/ShowFps", _working.ShowFps);

            RefreshValueLabels();
        }

        /// <summary>
        /// 数值回显：原版把"当前值"画在滑杆自己身上（`CCvarSlider` 自带数值文本），
        /// 本工程用的是引擎滑杆（无内建数值文本）⇒ 只回显到**原版就有的那个数值控件**
        /// （`optionssubmouse.res` 的 `SensitivityLabel`，ControlName = `TextEntry`）上。
        /// </summary>
        private void RefreshValueLabels()
        {
            if (_working == null) return;

            Handle h;
            if (_handles.TryGetValue("mouse/SensitivityLabel", out h) && h.Entry != null && !h.Entry.isFocused)
                h.Entry.SetTextWithoutNotify(_working.MouseSensitivity.ToString("0.##"));
        }

        // ─────────────────────────── 句柄小工具 ───────────────────────────

        private void SetSlider(string key, float value)
        {
            Handle h;
            if (_handles.TryGetValue(key, out h) && h.Slider != null) h.Slider.SetValueWithoutNotify(value);
        }

        private void SetEntry(string key, string value)
        {
            Handle h;
            if (_handles.TryGetValue(key, out h) && h.Entry != null) h.Entry.SetTextWithoutNotify(value);
        }

        private void SetCheck(string key, bool state)
        {
            Handle h;
            if (_handles.TryGetValue(key, out h) && h.Check != null) h.Check.SetState(state);
        }

        private Button FindButton(string page, string name)
        {
            var parent = string.IsNullOrEmpty(page) ? transform as RectTransform : _pages[Index(page)];
            var node = FindChild(parent, name);
            if (node == null)
            {
                Game.Logger.Warn(Tag, $"Options 找不到按钮节点「{name}」");
                return null;
            }
            var btn = node.GetComponent<Button>();
            if (btn == null) Game.Logger.Warn(Tag, $"Options 按钮「{name}」上找不到 Button 组件");
            return btn;
        }

        private static int Index(string tabName)
        {
            for (var i = 0; i < TabNames.Length; i++)
            {
                if (TabNames[i] == tabName) return i;
            }
            return 0;
        }

        private static RectTransform FindChild(RectTransform parent, string name)
        {
            if (parent == null) return null;
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i) as RectTransform;
                if (child == null) continue;
                if (child.name == name) return child;
                var deeper = FindChild(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>
        /// 原版设计空间坐标 → 本工程画布坐标（唯一换算处，见 <see cref="CsUiStyle.ResScale"/>）。
        /// ⚠️ `.res` 的 `ypos` 向下为正，而 <see cref="CsUiStyle.AnchoredTopLeft"/> 的 y 向上为正 ⇒ **y 取负**。
        /// </summary>
        private static Vector2 ResPos(float x, float y)
        {
            return new Vector2(x * CsUiStyle.ResScale, -y * CsUiStyle.ResScale);
        }

        /// <summary>原版设计空间尺寸 → 本工程画布尺寸（同一比值）。</summary>
        private static Vector2 ResSize(float w, float h)
        {
            return new Vector2(w * CsUiStyle.ResScale, h * CsUiStyle.ResScale);
        }
    }
}
