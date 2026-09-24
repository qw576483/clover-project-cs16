using System;
using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// CS 1.6 风格配色 + 流程面板的公共控件工厂（**薄**封装引擎的 <see cref="UIFactory"/>）。
    ///
    /// <para>
    /// 为什么要有这一层：8 个流程面板全是"深色底 + 橙色强调 + 白字"，
    /// 颜色 / 按钮交互态 / 左上角定位若各写一遍，必然出现 8 份略微不同的取值。
    /// 这里只保留 CS 1.6 主菜单的取色（背景 <c>#1B1B1B</c>、强调 <c>#E8A33D</c>、正文白）
    /// 与"左上角锚点"这一种布局约定。
    /// </para>
    ///
    /// <para>
    /// <b>本类不含任何控件构造实现</b>：Slider / InputField / Selector / ToggleRow 与布局助手全部在
    /// 引擎 <c>UIFactory</c>（<c>Runtime/Presentation/UIWidgetControls.cs</c>），这里只把 CS 的
    /// 配色 / 字号 / 文案打包成 <c>Widget*Style</c> 传进去 —— <c>结构规则.md</c> §4.4：同一种能力
    /// 只允许一个实现，不准平行再起一套。本文件只留 CS 业务取值（配色常量、字号档位、RowHeight）
    /// 与 <see cref="CreateButton"/>（悬浮橙色高亮是 CS 主菜单特有的）这两类。
    /// </para>
    ///
    /// <para>
    /// 布局约定：面板根节点铺满父节点（引擎 UIManager 的层级节点 = 1920x1080 参考分辨率），
    /// 所有控件用 <see cref="AnchoredTopLeft"/> 以"面板左上角"为原点摆放（y 为负 = 向下）。
    /// </para>
    ///
    /// <para>零美术依赖：全部走纯色 Image（不设 sprite）。引擎 <c>Image</c> 在 sprite 为空时
    /// 走 <c>Graphic.OnPopulateMesh</c> 的实心四边形分支，因此照样能看见，不需要占位图资源。</para>
    /// </summary>
    public static class CsUiStyle
    {
        // ---- 取色（CS 1.6 主菜单）----
        /// <summary>面板底色 #1B1B1B。</summary>
        public static readonly Color Backdrop = new Color32(0x1B, 0x1B, 0x1B, 0xFF);
        /// <summary>半透明底色（选阵营那类"盖在战场上"的面板用）。</summary>
        public static readonly Color BackdropSoft = new Color32(0x1B, 0x1B, 0x1B, 0xD0);
        /// <summary>内容框底色。</summary>
        public static readonly Color Box = new Color32(0x22, 0x22, 0x22, 0xF2);
        /// <summary>控件 / 输入框底色。</summary>
        public static readonly Color Field = new Color32(0x2C, 0x2C, 0x2C, 0xFF);
        /// <summary>强调色 = 原版 `BaseText/BrightBaseText/ControlText`「255 176 0 255」（`clientscheme.res:24-30`）。</summary>
        public static readonly Color Accent = new Color32(0xFF, 0xB0, 0x00, 0xFF);
        /// <summary>选中项常驻底色 = 原版 `SelectionBG`「255 176 0 100」（`clientscheme.res:43`）。</summary>
        public static readonly Color AccentDim = new Color32(0xFF, 0xB0, 0x00, 0x64);
        /// <summary>正文 = 原版 `ControlText`「255 176 0 255」（`clientscheme.res:29`）。</summary>
        public static readonly Color Text = new Color32(0xFF, 0xB0, 0x00, 0xFF);
        /// <summary>次要文字 = 原版 `DimBaseText`「255 176 0 255」（`clientscheme.res:27`）。</summary>
        public static readonly Color TextDim = new Color32(0xFF, 0xB0, 0x00, 0xFF);
        /// <summary>分隔线。</summary>
        public static readonly Color Line = new Color32(0x44, 0x44, 0x44, 0xFF);
        /// <summary>深色字（压在强调色底上时用）。</summary>
        public static readonly Color TextDark = new Color32(0x1B, 0x1B, 0x1B, 0xFF);

        /// <summary>一行控件的标准高度。</summary>
        public const float RowHeight = 44f;

        /// <summary>滑块手柄宽度。</summary>
        public const float HandleWidth = 14f;

        /// <summary>输入框 / 滑块的文字字号。</summary>
        public const int InputFontSize = 22;

        /// <summary>面板根节点必须铺满父节点 —— 否则点击区与视觉都只有一小块（"面板没铺满"是经典坑）。</summary>
        public static void StretchRoot(RectTransform root)
        {
            if (root == null) return;
            UIFactory.Stretch(root);
        }

        /// <summary>把矩形固定为"以父节点左上角为原点"的定位方式（pos.y 为负 = 向下）。</summary>
        public static void AnchoredTopLeft(RectTransform rt, Vector2 pos, Vector2 size)
        {
            UIFactory.AnchoredTopLeft(rt, pos, size);
        }

        /// <summary>
        /// 把矩形固定为"以父节点**底部**为原点"的定位方式（<paramref name="pos"/>.y 为**正** = 向上）。
        ///
        /// <para>用途：署名 / 版权这类"必须贴在底部"的元素。**不要**用 <see cref="AnchoredTopLeft"/> + 一个大负 y
        /// 去放底部元素 —— CanvasScaler 是 <c>match=0.5</c>，真实画布高度随窗口变化（实测 1600x900 时只有约 972），
        /// y 一超过画布高度就**整体掉到屏幕外**（元素 active、文本正确，但一个像素都看不见）。</para>
        /// </summary>
        /// <param name="anchor">决定贴左 / 居中 / 贴右（用 <see cref="TextAnchor"/> 的 Lower* 三种）。</param>
        public static void AnchoredBottom(RectTransform rt, Vector2 pos, Vector2 size,
            TextAnchor anchor = TextAnchor.LowerCenter)
        {
            UIFactory.AnchoredBottom(rt, pos, size, anchor);
        }

        /// <summary>
        /// **引擎署名行**（逐字 <c>by clover-engine</c>）在本工程的**唯一入口**。
        ///
        /// <para><b>为什么收成一个方法</b>（改前是主菜单 / 选阵营两处各写一遍
        /// <c>CreateBottomLabel("Signature", root, "by clover-engine", 22, new Vector2(0f, 56f), new Vector2(600f, 32f))</c>）：
        /// 品牌行的判据是"**画面上那一行**"—— 文案大小写、字体是否有小写字形、有没有贴到屏幕底部，
        /// 三样里任何一样写错都只有实机截图才看得出来。两处各写一遍 = 两个都可能写错的地方，
        /// 且"贴底"极易写错（左上锚点 + 大负 y 会随画布高度整体掉出屏幕，本工程实测踩过）。</para>
        ///
        /// <para><b>转发到引擎 <c>UIFactory.CreateCreditLabel</c></b>（不另起一套定位 / 建文本）：
        /// 文案用**引擎默认值**（<c>by clover-engine</c>，逐字、首字母小写）—— 本工程不再自持该字面量；
        /// 贴底锚点、字号、颜色由引擎给。</para>
        ///
        /// <para><b>字体一律走本工程的原版字体链</b>（<see cref="OriginalFont"/> = 系统 Verdana + CJK 回退族，
        /// 取不到时回退引擎内置字体并 <c>Warn</c> 一次）：⛔ **不传 null** —— 引擎在 <c>font == null</c> 时会
        /// 报"未指定字体、已回落内置字体（像素字体可能把小写渲染成全大写）"的 Warn，那是给"真没字体"的项目看的，
        /// 本工程有原版字体链，传 null 只会把它变成假告警。</para>
        /// </summary>
        /// <param name="parent">宿主节点（面板根）。</param>
        /// <param name="fontSize">字号（默认 22 = 改前两处用的值）。</param>
        /// <param name="bottomOffset">离父节点底边的距离（默认 56 = 改前两处用的值）。</param>
        public static Text CreateCreditLabel(Transform parent, int fontSize = 22, float bottomOffset = 56f)
        {
            return UIFactory.CreateCreditLabel(parent, OriginalFont, fontSize, bottomOffset);
        }

        /// <summary>全屏纯色底（面板的第一层）。<paramref name="raycast"/> = true 时挡住下层点击。</summary>
        public static Image CreateFullScreen(string name, Transform parent, Color color, bool raycast = true)
        {
            // 引擎的 CreatePanel 就是"铺满父节点的纯色 Image" ⇒ 直接转发，不另起一套（§4.4）。
            return UIFactory.CreatePanel(name, parent, color, raycast);
        }

        /// <summary>左上角定位的纯色块（内容框 / 输入框底 / 分隔条）。</summary>
        public static Image CreateBoxRect(string name, Transform parent, Vector2 pos, Vector2 size, Color color,
            bool raycast = false)
        {
            return UIFactory.CreateBoxRect(name, parent, pos, size, color, raycast);
        }

        /// <summary>左上角定位的文本。</summary>
        public static Text CreateLabel(string name, Transform parent, string content, int fontSize, Vector2 pos,
            Vector2 size, TextAnchor anchor = TextAnchor.MiddleLeft, Color? color = null)
        {
            return UIFactory.CreateLabel(name, parent, content, fontSize, pos, size, anchor, color ?? Text);
        }

        /// <summary>左上角定位的按钮（带 CS 1.6 风格的悬停橙色高亮）。</summary>
        public static Button CreateButton(string name, Transform parent, string label, Vector2 pos, Vector2 size,
            Action onClick, bool accent = false)
        {
            var img = UIFactory.CreateButton(name, parent, label, size, Vector2.zero,
                accent ? Accent : Field, onClick);
            AnchoredTopLeft(img.rectTransform, pos, size);

            var btn = img.GetComponent<Button>();
            if (btn != null)
            {
                var colors = btn.colors;
                colors.normalColor = accent ? Accent : Field;
                colors.highlightedColor = accent ? Accent : Accent;
                colors.pressedColor = AccentDim;
                colors.selectedColor = accent ? Accent : Field;
                colors.disabledColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.08f;
                btn.colors = colors;
            }

            var labelText = img.GetComponentInChildren<Text>();
            if (labelText != null)
            {
                labelText.fontSize = 24;
                // 强调按钮（New Game / Start）用深色字，对比度更高，也更接近 CS 1.6 的高亮项
                labelText.color = accent ? TextDark : Text;
            }

            return btn;
        }

        /// <summary>
        /// "◀ 值 ▶" 选择行 —— CS 1.6 的 bot 难度 / 选项就是这个形态（左右箭头夹住当前值）。
        /// 返回的 <see cref="Selector"/> 持有值文本，切档时由调用方改它的 <c>text</c>。
        /// </summary>
        public static Selector CreateSelector(string name, Transform parent, string label, Vector2 pos,
            float labelWidth, float arrowWidth, float valueWidth, Action onPrev, Action onNext,
            Color? labelColor = null)
        {
            var style = new WidgetSelectorStyle
            {
                RowHeight = RowHeight,
                LabelFontSize = 22,
                LabelColor = labelColor ?? TextDim,
                ValueFontSize = 22,
                ValueColor = Text,
                ValueText = "-",
                PrevText = "\u25C0",
                NextText = "\u25B6",
                ArrowButton = NonAccentButton,
            };
            return UIFactory.CreateSelector(name, parent, label, pos, labelWidth, arrowWidth, valueWidth,
                onPrev, onNext, style);
        }

        /// <summary>把 0~1 的进度写进"按锚点宽度"的进度条（别用空 sprite 的 Filled Image，fillAmount 会静默失效）。</summary>
        public static void SetBarWidth(RectTransform fill, float progress01)
        {
            UIFactory.SetBarWidth(fill, progress01);
        }

        /// <summary>
        /// 水平滑块：轨道 + 已填（橙）+ 手柄（白）。
        /// 构造在引擎 <see cref="UIFactory.CreateSlider"/>（手搭而不用 <c>DefaultControls.CreateSlider</c>
        /// 的原因见那里的注释），这里只给 CS 的配色与手柄宽度。
        /// </summary>
        public static Slider CreateSlider(string name, Transform parent, Vector2 pos, Vector2 size,
            float min, float max, float value, Action<float> onValueChanged)
        {
            var style = new WidgetSliderStyle
            {
                TrackColor = SliderTrack,
                FillColor = Accent,
                HandleColor = Text,
                HandleColors = new ColorBlock
                {
                    normalColor = Text,
                    highlightedColor = Accent,
                    pressedColor = AccentDim,
                    selectedColor = Text,
                    disabledColor = new Color(0.4f, 0.4f, 0.4f, 1f),
                    colorMultiplier = 1f,
                    fadeDuration = 0f,
                },
                HandleWidth = HandleWidth,
            };
            return UIFactory.CreateSlider(name, parent, pos, size, min, max, value, onValueChanged, style);
        }

        /// <summary>
        /// 单行文本输入框。
        /// 构造在引擎 <see cref="UIFactory.CreateInputField"/> —— 那里写清了"为什么必须用
        /// <c>DefaultControls.CreateInputField</c>、以及 placeholder 是 <c>Graphic</c> 必须 <c>as Text</c>"
        /// （本项目旧注释曾把后者记成"本方法编译不过"，已在引擎侧按正确写法落定，见该处注释）。
        /// </summary>
        public static InputField CreateInputField(string name, Transform parent, Vector2 pos, Vector2 size,
            string placeholderText, int characterLimit)
        {
            return UIFactory.CreateInputField(name, parent, new Vector2(0f, 1f), new Vector2(0f, 1f),
                pos, size, placeholderText, characterLimit, InputFieldStyle);
        }

        /// <summary>
        /// 「标签 + 值按钮」的开关行（On / Off）：点值按钮就地切换。
        /// CS 1.6 的选项里布尔项就是一个点了会变的按钮，不是复选框。
        /// </summary>
        public static ToggleRow CreateToggleRow(string name, Transform parent, string label, Vector2 pos,
            float labelWidth, float buttonWidth)
        {
            var style = new WidgetToggleRowStyle
            {
                RowHeight = RowHeight,
                LabelFontSize = 22,
                LabelColor = TextDim,
                ButtonText = "-",
                Button = NonAccentButton,
            };
            return UIFactory.CreateToggleRow(name, parent, label, pos, labelWidth, buttonWidth, style);
        }

        // ═══════════════ CS 取值 → 引擎 Style（本类里唯一的"打包"处）═══════════════

        /// <summary>非强调按钮（选择行箭头 / 开关行值按钮）的 CS 配色：常态 = 输入框底，悬停 = 强调橙。</summary>
        private static WidgetButtonStyle NonAccentButton => new WidgetButtonStyle
        {
            Background = Field,
            Highlighted = Accent,
            Pressed = AccentDim,
            Disabled = new Color(0.25f, 0.25f, 0.25f, 1f),
            FadeDuration = 0.08f,
            TextColor = Text,
            TextFontSize = 24,
        };

        /// <summary>输入框的 CS 配色与字号（底色 = Field，光标 / 选区 = 强调橙）。</summary>
        private static WidgetInputFieldStyle InputFieldStyle => new WidgetInputFieldStyle
        {
            Background = Field,
            Colors = new ColorBlock
            {
                normalColor = Field,
                highlightedColor = Field,
                pressedColor = Field,
                selectedColor = Field,
                disabledColor = new Color(0.3f, 0.3f, 0.3f, 1f),
                colorMultiplier = 1f,
                fadeDuration = 0f,
            },
            TextColor = Text,
            TextFontSize = InputFontSize,
            PlaceholderColor = new Color(TextDim.r, TextDim.g, TextDim.b, 0.65f),
            PlaceholderFontSize = InputFontSize,
            CaretColor = Accent,
            SelectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.4f),
        };

        // ═══════════════════════ 原版 .res 重建用的取色 / 字体 / 控件工厂 ═══════════════════════
        //
        // 出处一律 = `原版资源/cs16src/cs16game/app/cstrike/resource/clientscheme.res`（下称 scheme）。
        // ⛔ 这里**只做搬运**：每个常量后面的行号就是它的出处，不许"看起来接近"就改数。

        /// <summary>
        /// `.res` 的设计空间（640×480）→ 本项目画布（1920×1080）的换算比。
        ///
        /// <para>
        /// 依据：VGUI 的 <c>GetScaledValue</c> 口径 = 屏幕高 ÷ 设计高；本工程画布高 1080、`.res` 设计高 480
        /// ⇒ 1080 / 480 = <b>2.25</b>（两轴同用这一个比值 —— VGUI 的横轴也按屏幕高缩放）。
        /// </para>
        ///
        /// <para>
        /// ⚠️ <b>口径来源如实标注</b>：载体里**没有**任何一行写着"设计空间 = 640×480"。
        /// 这是**由数据自洽性推断**（`teammenu.res` 的 `xpos 76 + wide 552 = 628 ≤ 640`；设置各子页的
        /// 控件也全部落在 640×480 内）+ 与实机图对照后定下的口径，⛔ 不是"原版写明的"。
        /// </para>
        ///
        /// <para>
        /// ⛔ <b>字号不乘这个比值</b>：`scheme` 的 `Fonts` 块里 <c>tall</c> 给的是**实屏像素**
        /// （按 <c>yres</c> 分档，见 <see cref="OriginalFontSize"/>），换算成画布单位时是 1:1。
        /// </para>
        /// </summary>
        public const float ResScale = 2.25f;

        /// <summary>
        /// 大多数控件 / **Frame 自身的底色** = 原版 `ControlBG "0 0 0 0"`（scheme:38）—— **全透明**。
        ///
        /// <para>出处链：`BaseSettings` 的 `BgColor "ControlBG"`（scheme:101）是每个控件的默认底色，
        /// 所以 `Frame`（选阵营的 `TeamMenu`、各设置子页的框）**默认就是没有底板的**。
        /// ⛔ 别拿 <see cref="WindowBg"/>（`WindowBG "0 0 0 200"`，scheme:41）当 Frame 底 ——
        /// 那条是**文本编辑框 / 聊天窗**的底色（scheme:41 行尾注释即写明），不是 Frame 的。</para>
        /// </summary>
        public static readonly Color ControlBg = new Color32(0x00, 0x00, 0x00, 0x00);

        /// <summary>按钮底色 = 原版 `ButtonBG "0 0 0 64"`（scheme:39）。</summary>
        public static readonly Color ButtonBg = new Color32(0x00, 0x00, 0x00, 0x40);
        /// <summary>文本编辑 / 面板底的深色 = 原版 `WindowBG "0 0 0 200"`（scheme:41）。</summary>
        public static readonly Color WindowBg = new Color32(0x00, 0x00, 0x00, 0xC8);
        /// <summary>更暗的底（滚动条底一类）= 原版 `ControlDarkBG "0 0 0 128"`（scheme:40）。</summary>
        public static readonly Color ControlDarkBg = new Color32(0x00, 0x00, 0x00, 0x80);
        /// <summary>列表 / 记分板底 = 原版 `ListBG "0 0 0 128"`（scheme:45）。</summary>
        public static readonly Color ListBg = new Color32(0x00, 0x00, 0x00, 0x80);
        /// <summary>滑块轨道 = 原版 `SliderTrackColor "31 31 31 255"`（scheme:73）。</summary>
        public static readonly Color SliderTrack = new Color32(0x1F, 0x1F, 0x1F, 0xFF);
        /// <summary>键盘焦点虚线 / 勾选框亮边 = 原版 `ButtonFocusBorder "64 48 0 255"`（scheme:35）。</summary>
        public static readonly Color FocusBorder = new Color32(0x40, 0x30, 0x00, 0xFF);
        /// <summary>勾选框暗边 = 原版 `CheckButtonBorder1` → `BorderDark "188 112 0 128"`（scheme:77）。</summary>
        public static readonly Color BorderDark = new Color32(0xBC, 0x70, 0x00, 0x80);
        /// <summary>勾选框亮边 = 原版 `CheckButtonBorder2` → `BorderBright "188 112 0 128"`（scheme:76）。</summary>
        public static readonly Color BorderBright = new Color32(0xBC, 0x70, 0x00, 0x80);
        /// <summary>勾选态的勾 = 原版 `CheckButtonCheck` → `BrightControlText "255 176 0 255"`（scheme:30/179）。</summary>
        public static readonly Color CheckMark = new Color32(0xFF, 0xB0, 0x00, 0xFF);
        /// <summary>纯说明文字（`.res` 里 `dulltext 1` 的那些）= 原版 `LabelDimText "255 176 0 164"`（scheme:28）。</summary>
        public static readonly Color LabelDimText = new Color32(0xFF, 0xB0, 0x00, 0xA4);
        /// <summary>禁用文字 = 原版 `DisabledText1 "80 48 0 255"`（scheme:31）。</summary>
        public static readonly Color DisabledText = new Color32(0x50, 0x30, 0x00, 0xFF);

        /// <summary>
        /// 正文字号 = 原版 `Fonts → Default` 在 <c>yres</c> 1080 档的 <c>tall</c>。
        ///
        /// <para>
        /// 出处：scheme:225-265 的 `Default` 块 —— 按 <c>yres</c> 分 5 档
        /// （`480 599`→12 / `600 767`→13 / `768 1023`→14 / **`1024 1199`→20** / `1200 6000`→24）；
        /// 本工程画布高 1080 落在第 4 档 ⇒ **20**。
        /// </para>
        ///
        /// <para>⛔ 这个数是**实屏像素**，不乘 <see cref="ResScale"/>（见该常量的说明）。</para>
        /// </summary>
        public const int OriginalFontSize = 20;

        /// <summary>标题字号 = 原版 `Fonts → Title`「Verdana Bold, tall 18, weight 500」（scheme:389-403）。</summary>
        public const int OriginalTitleFontSize = 18;

        private static Font _originalFont;
        private static bool _fontResolved;
        private static bool _fontWarned;

        /// <summary>
        /// 原版正文字体 = **Verdana**（scheme:229 的 `"name" "Verdana"`；原版没有字体文件，取的是系统字体）。
        ///
        /// <para>
        /// 用 <see cref="Font.CreateDynamicFontFromOSFont(string[], int)"/> 并带上 CJK 回退族 ——
        /// 原版 menu 全英文、没有中文，而本工程的面板里有中文（如"（本项目新增）"），
        /// 只给 Verdana 会让中文变成方框。⛔ CJK 回退族是**本项目新增**（登记在验收表「允许的差异」）。
        /// </para>
        /// <para>取不到时回退引擎内置字体并只 <c>Warn</c> 一次（不许静默变成另一种字体）。</para>
        /// </summary>
        public static Font OriginalFont
        {
            get
            {
                if (_fontResolved) return _originalFont;
                _fontResolved = true;
                try
                {
                    _originalFont = Font.CreateDynamicFontFromOSFont(
                        new[] { "Verdana", "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC" },
                        OriginalFontSize);
                }
                catch (Exception ex)
                {
                    _originalFont = null;
                    if (!_fontWarned)
                    {
                        _fontWarned = true;
                        Game.Logger?.Warn("UI", $"取系统字体 Verdana 抛异常（回退引擎内置字体）：{ex.Message}");
                    }
                }

                if (_originalFont == null)
                {
                    if (!_fontWarned)
                    {
                        _fontWarned = true;
                        Game.Logger?.Warn("UI",
                            "未取到系统字体 Verdana（scheme:229 声明的原版正文族），回退引擎内置字体 —— 字形与原版不一致");
                    }
                    _originalFont = UIFactory.DefaultFont();
                }
                return _originalFont;
            }
        }

        /// <summary>
        /// 把一棵子树里**所有** <see cref="Text"/> 的字体换成原版 Verdana（字号 / 颜色 / 对齐不动）。
        ///
        /// <para>
        /// 为什么必须在 <c>OnOpen</c> 里再跑一次：<see cref="Font.CreateDynamicFontFromOSFont"/> 返回的是
        /// **运行期对象**（不是工程里的字体资产），因此 <c>FlowSetup</c> 生成预制体时写不进 `.prefab`
        /// —— 从预制体实例化出来的 <c>Text.font</c> 是 <c>null</c>（文本不显示）。生成期调用是为了
        /// 运行期兜底搭布局的那条路径也拿到字体，实例化后的那一次才真正生效。
        /// </para>
        /// </summary>
        public static void ApplyOriginalFonts(Transform root)
        {
            if (root == null) return;
            var font = OriginalFont;
            if (font == null) return;
            var texts = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] != null) texts[i].font = font;
            }
        }

        /// <summary>
        /// 把一条 <see cref="Text"/> 标成原版 `Title` 字体（Verdana **Bold** 18）—— 用完
        /// <see cref="ApplyOriginalFonts"/> 之后再调（后者会把字体覆盖回常规体）。
        /// </summary>
        public static void ApplyTitleFont(Text text)
        {
            if (text == null) return;
            text.font = OriginalFont;
            text.fontStyle = FontStyle.Bold;
            text.fontSize = OriginalTitleFontSize;
        }

        /// <summary>
        /// 原版形态的按钮：**纯文本左对齐**（`.res` 的 `textAlignment west`）+ `ButtonBG` 半透明黑底
        /// + 文字 `ControlText` 橙；鼠标悬停换 `SelectionBG`（原版 `ButtonArmedBgColor`，scheme:184）。
        ///
        /// <para>⛔ 与 <see cref="CreateButton"/>（强调=橙底深字）**不是同一个形态**：
        /// `.res` 里的按钮一律是"黑底 + 橙字"，橙色在原版是**文字色**不是底色。</para>
        /// </summary>
        public static Button CreateOriginalButton(string name, Transform parent, string label, Vector2 pos,
            Vector2 size, Action onClick)
        {
            // ⛔ **底板必须是纯白**，配色全交给下面的 `Button.colors`。
            //
            // 为什么（实测根因，用户报的"鼠标划过没有反应"就是它）：
            // uGUI 的 `Selectable.Transition.ColorTint` **不是替换底色，而是乘上去** ——
            // 悬停时 `Graphic.CrossFadeColor` 只改 **CanvasRenderer 颜色**（见 uGUI `Graphic.cs`
            // 的 `CrossFadeColor`：`canvasRenderer.SetColor` 那一行），`Graphic.color` 不动，
            // 最终上屏 = `Image.color` × `CanvasRenderer.color`。
            // 于是底板若为 `ButtonBG "0 0 0 64"`（纯黑），任何状态色乘上去都还是黑：
            // normal (0,0,0,0.251) → highlighted 也仍是 (0,0,0,0.098) —— 只是更淡，肉眼完全看不出，
            // "划过有变"就成了空话（`currentSelectionState` 确实变了，但画面没变）。
            // 底板给白之后，上屏色 = 状态色本身，正好等于原版 VGUI 的语义：
            // armed 时**底色换成** `ButtonArmedBgColor`（scheme:184）而不是"在常态色上提亮"。
            var img = UIFactory.CreateButton(name, parent, label, size, Vector2.zero, Color.white, onClick);
            AnchoredTopLeft(img.rectTransform, pos, size);

            var btn = img.GetComponent<Button>();
            if (btn != null)
            {
                var colors = btn.colors;
                colors.normalColor = ButtonBg;         // 原版 ButtonBgColor = ButtonBG "0 0 0 64"（scheme:39/102）
                colors.highlightedColor = AccentDim;   // 原版 armed bg = SelectionBG "255 176 0 100"（scheme:184/43）
                colors.pressedColor = ButtonBg;        // 原版 ButtonDepressedBgColor 未定义 ⇒ 与常态同（scheme:185-186 注释掉）
                colors.selectedColor = ButtonBg;
                colors.disabledColor = ControlDarkBg;
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0f;
                btn.colors = colors;
            }
            else
            {
                Game.Logger?.Warn("UI", $"原版形态按钮 {name} 没有 Button 组件，悬停配色不生效");
            }

            var text = img.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.font = OriginalFont;
                text.fontSize = OriginalFontSize;
                text.color = Text;
                text.alignment = TextAnchor.MiddleLeft;   // .res 的 textAlignment west
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
            }
            else
            {
                Game.Logger?.Warn("UI", $"原版形态按钮 {name} 没有文字子节点，按钮文案不会显示");
            }

            return btn;
        }

        // ═══════════════ 原版主菜单项（GameMenu）的取值与控件 ═══════════════
        //
        // 出处一律 = `原版资源/cs16src/cs16game/app/platform/resource/trackerscheme.res` 的
        // `InGameDesktop` 块（:164-173）。这几个键就是 CS 1.6 自己的 GameUI
        // （`原版资源/cs16src/cs16game/app/cstrike/cl_dlls/gameui.dll`）在读的那几个：
        // `MenuItemHeight` @0x1002bf13、`MenuColor` @0x1002c087、`ArmedMenuColor` @0x1002c112、
        // `DepressedMenuColor` @0x1002c147、`GameMenuInset` @0x1002cedf、
        // `Resource/GameMenu.res` @0x1002c4aa —— 即"解析 gamemenu.res 的那个菜单"用的就是这几个键。

        /// <summary>菜单项常态文字色 = 原版 `InGameDesktop/MenuColor "200 200 200 255"`（trackerscheme.res:166）。</summary>
        public static readonly Color MenuItemText = new Color32(0xC8, 0xC8, 0xC8, 0xFF);

        /// <summary>菜单项悬停（armed）文字色 = 原版 `InGameDesktop/ArmedMenuColor "255 255 255 255"`（trackerscheme.res:167）。</summary>
        public static readonly Color MenuItemArmedText = new Color32(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>菜单项按下文字色 = 原版 `InGameDesktop/DepressedMenuColor "192 186 80 255"`（trackerscheme.res:168）。</summary>
        public static readonly Color MenuItemPressedText = new Color32(0xC0, 0xBA, 0x50, 0xFF);

        /// <summary>
        /// 原版形态的菜单项：**只有文字**（原版 `cstrike/resource/gamemenu.res` 的项只有 `label` + `command`，
        /// 没有 `image`）⇒ 不画底图；常态 / 悬停 / 按下三个文字色全出自 `trackerscheme.res:166-168`。
        ///
        /// <para>
        /// 悬停反馈用 uGUI 的 <see cref="Selectable.Transition.ColorTint"/> 直接作用在**文字**上
        /// （<c>targetGraphic</c> 指向 <see cref="Text"/>）⇒ 不需要自定义组件。因此 <c>Text.color</c>
        /// 必须是**白**（最终上屏色 = 文字色 × 状态色），点击区 = 文字自身的矩形（<paramref name="size"/>）。
        /// </para>
        /// </summary>
        public static Button CreateMenuItem(string name, Transform parent, string label, Vector2 pos,
            Vector2 size, Action onClick)
        {
            // ⛔ 文字色必须是**原版正文橙**（ControlText 255 176 0，clientscheme.res:24-33）：
            // 下面的 Button.colors 只是**再乘一层 tint**（常态 MenuItemText = 200/255，trackerscheme.res:166）
            // ⇒ 最终显示 = 橙 × 200/255。若这里给白，tint 乘完就成了"灰白字"，与原版不符（实测踩过）。
            var text = UIFactory.CreateText(name, parent, label, OriginalFontSize,
                TextAnchor.MiddleLeft, Text, true);
            if (text == null)
            {
                Game.Logger?.Warn("UI", $"菜单项 {name} 的 Text 建不出来，该项不会显示");
                return null;
            }

            AnchoredTopLeft(text.rectTransform, pos, size);
            text.font = OriginalFont;
            text.alignment = TextAnchor.MiddleLeft;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var btn = text.gameObject.AddComponent<Button>();
            btn.targetGraphic = text;
            btn.transition = Selectable.Transition.ColorTint;
            var colors = btn.colors;
            colors.normalColor = MenuItemText;
            colors.highlightedColor = MenuItemArmedText;
            colors.pressedColor = MenuItemPressedText;
            colors.selectedColor = MenuItemText;
            colors.disabledColor = DisabledText;
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0f;      // 原版 VGUI 没有淡入淡出
            btn.colors = colors;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        /// <summary>
        /// 原版形态的"贴图按钮"：**常态 / 悬停两张 sprite**（原版 `game_menu.tga` /
        /// `game_menu_mouseover.tga` —— 两张画布同为 207×32、后者带 `_mouseover` 后缀，且 `gameui.dll`
        /// 把二者同时加载进同一 ImagePanel 的 image / mouseover image 字段（@0x1002cf16 / @0x1002cf01）
        /// ⇒ 它们是**同一元素的两种状态**）。
        ///
        /// <para>交换走 uGUI 的 <see cref="Selectable.Transition.SpriteSwap"/>，无自定义组件。
        /// sprite 由编辑器生成器绑（运行期没有 AssetDatabase）；<paramref name="normal"/> 为空时
        /// <b>先不显示</b> —— 没有 sprite 的 uGUI <c>Image</c> 会被画成一块实心白块。</para>
        /// </summary>
        public static Image CreateSpriteButton(string name, Transform parent, Vector2 pos, Vector2 size,
            Sprite normal, Sprite hover, Action onClick)
        {
            var img = UIFactory.CreatePanel(name, parent, Color.white, true);
            AnchoredTopLeft(img.rectTransform, pos, size);

            var btn = img.gameObject.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.SpriteSwap;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            ApplySpriteStates(img, normal, hover);
            return img;
        }

        /// <summary>
        /// 把常态 / 悬停两张 sprite 写到按钮上（生成期与运行期兜底都走这里，保证两条路径一致）。
        /// 返回"常态图是否就绪"；未就绪时该 Image 被关掉（绝不画成实心白块）。
        /// </summary>
        public static bool ApplySpriteStates(Image img, Sprite normal, Sprite hover)
        {
            if (img == null) return false;

            var btn = img.GetComponent<Button>();
            if (btn != null)
            {
                var st = btn.spriteState;
                st.highlightedSprite = hover;
                st.pressedSprite = hover;          // 原版只有常态/悬停两张，无单独的按下贴图 ⇒ 与悬停同
                st.selectedSprite = normal;
                st.disabledSprite = normal;
                btn.spriteState = st;
            }

            img.sprite = normal;
            img.color = Color.white;
            img.type = Image.Type.Simple;
            var ready = normal != null;
            img.enabled = ready;
            return ready;
        }

        // ═══════════ 原版勾选框的"勾"字形（**预渲染贴图**，不再是纯色方块）═══════════
        //
        // 出处链：原版 `clientscheme.res:483-492` 声明的符号字体 **Marlett**；勾 = 该字体 **gid 12**
        //（可达码位 `U+F061`，判据资产 `tools/probes/marlett-glyphs.py` 定案）。载体 =
        // `原版资源\cs16src\marlett.ttf`（27,724 B）⟶ 由 `tools/probes/make-check-glyph.py` **同口径**
        // 渲成带 alpha 的 PNG = `ResPaths.MenuCheckGlyph`（`Resources/UI/Art/menu_check.png`，132×140）。
        // 判据数字（`--size 300`）：`ink=7523 / bbox=132x140 / ratio=0.943 / comps=1 / vx=0.352 / arm=0.264`。
        //
        // ⛔ **为什么用 `Image` 而不是 `Text`**：① `ApplyOriginalFonts()` 会把整棵子树的 `Text.font`
        //    刷成 Verdana ⇒ 用 `Text` 画勾会被覆写；② 该载体是 Windows **符号字体**（`cmap` 只有
        //    (1,0) Mac-Roman + (3,0) MS-Symbol、**没有 (3,1) Unicode**）⇒ Unity 侧根本取不到它自己的
        //    字形（切片AU 实测：`U+F061` 是空字形 ink=0，`U+0029` 拿到的是系统 fallback 的 ")"）；
        //    ③ 贴图自带原版勾色（`CheckButtonCheck` → `BrightControlText "255 176 0 255"`，
        //    `clientscheme.res:30/179`），`Image.color` 只做白 tint。

        private static Sprite _checkMarkSprite;
        private static bool _checkMarkRequested;
        private static bool _checkMarkWarned;
        /// <summary>已建出、待贴（或需重刷）的勾标记。数量 = 面板上的勾选框数，**有界**。</summary>
        private static readonly List<Image> CheckMarkTargets = new List<Image>();

        /// <summary>
        /// 把原版勾字形贴到一个勾标记 <see cref="Image"/> 上（<see cref="CheckRow.Mark"/>）。
        ///
        /// <para>预制体里**存不下**这张贴图（生成器建预制体时 <c>Game.Res</c> 还没起来）⇒ 运行期由
        /// <c>OptionsPanel</c> 走树时逐格调本方法；贴图只请求一次，到手后统一刷所有已登记的目标。
        /// ⛔ 在手之前**不要**给勾标记留一个没有 sprite 的 `Image` —— uGUI 会把它画成**实心白块**
        /// （`Graphic.OnPopulateMesh` 的实心分支）。所以建件时的底色仍是 <see cref="CheckMark"/>
        /// （万一贴图取不到，退化成的正是改造前那块**原色**小方块，而不是白块）。</para>
        /// </summary>
        public static void ApplyCheckMarkSprite(Image mark)
        {
            if (mark == null) return;
            if (_checkMarkSprite != null)
            {
                ApplyCheckMarkSpriteNow(mark, _checkMarkSprite);
                return;
            }
            if (!CheckMarkTargets.Contains(mark)) CheckMarkTargets.Add(mark);
            RequestCheckMarkSprite();
        }

        /// <summary>取一次原版勾字形贴图（`Resources/UI/Art/menu_check`）；取不到只 `Warn` 一次。</summary>
        private static void RequestCheckMarkSprite()
        {
            if (_checkMarkRequested) return;

            var res = Game.Res;
            if (res == null)
            {
                WarnCheckMarkOnce("Game.Res 为 null（CloverRes.Init 未执行？），原版勾字形贴图加载不了");
                return;
            }

            _checkMarkRequested = true;
            res.LoadAsset<Sprite>(ResPaths.MenuCheckGlyph, s =>
            {
                if (s == null)
                {
                    _checkMarkRequested = false;      // 允许下次再试（比如资源后补上）
                    WarnCheckMarkOnce($"原版勾字形贴图加载失败（sprite 为空）：Resources/{ResPaths.MenuCheckGlyph}");
                    return;
                }

                _checkMarkSprite = s;
                var applied = 0;
                for (var i = CheckMarkTargets.Count - 1; i >= 0; i--)
                {
                    var target = CheckMarkTargets[i];
                    if (target == null) { CheckMarkTargets.RemoveAt(i); continue; }   // 已销毁（Unity 假 null）
                    ApplyCheckMarkSpriteNow(target, s);
                    applied++;
                }
                Game.Logger?.Info("UI",
                    $"原版勾字形贴图就绪：{s.name}({s.rect.width}×{s.rect.height})（Marlett gid 12 / U+F061），" +
                    $"已贴到 {applied} 个勾标记上");
            });
        }

        /// <summary>
        /// 把贴图写到勾标记上：**白 tint**（贴图 RGB 就是原版勾色 `255 176 0`）+ 保持长宽比
        /// （字形 132×140 不是方的，拉伸会把勾压歪）。
        /// </summary>
        private static void ApplyCheckMarkSpriteNow(Image mark, Sprite sprite)
        {
            mark.sprite = sprite;
            mark.color = Color.white;
            mark.type = Image.Type.Simple;
            mark.preserveAspect = true;
            mark.enabled = true;
        }

        private static void WarnCheckMarkOnce(string message)
        {
            if (_checkMarkWarned) return;
            _checkMarkWarned = true;
            Game.Logger?.Warn("UI", message);
        }

        /// <summary>
        /// 原版 `CheckButton` 的复刻件：一个带边的方框 + 右侧文案，点整行切换。
        ///
        /// <para>方框边色 = 原版 `CheckButtonBorder1/2`（→ `BorderDark`/`BorderBright`，scheme:177-178），
        /// 勾选标记色 = `CheckButtonCheck`（→ `BrightControlText`，scheme:179）。</para>
        ///
        /// <para>勾选框里的"勾" = 原版 **Marlett** 字体字形（scheme:483-492）的**预渲染贴图**
        /// （<see cref="ResPaths.MenuCheckGlyph"/>，勾 = 该字体 gid 12 / 可达码位 `U+F061`；
        /// 渲染器 = 判据资产 `tools/probes/make-check-glyph.py`）。⛔ 不再是一块纯色方块。</para>
        ///
        /// <para>方框边长（<see cref="CheckBoxSize"/>）与文案起点（<see cref="CheckTextIndent"/>）仍是
        /// **本项目新增**的量（原版由 Marlett 字模决定，无像素值可引）。</para>
        /// </summary>
        public static CheckRow CreateCheckButton(string name, Transform parent, string label, Vector2 pos,
            Vector2 size, bool initial, Action<bool> onToggle)
        {
            var holder = UIFactory.CreateNode(name, parent);
            AnchoredTopLeft(holder, pos, size);

            // ⛔ 勾选框图形在**行内垂直居中**：AnchoredTopLeft 的口径是「y 为负 = 向下」，
            // 所以"从行顶往下让出半个余量"必须**取负**。早先写成正数 ⇒ 框/边/勾整体跑到行容器**上方**
            // （与文字错位、越出本行），实测由全界面几何体检抓出。
            var boxTop = -(size.y - CheckBoxSize) * 0.5f;
            var box = CreateBoxRect("Box", holder, new Vector2(0f, boxTop),
                new Vector2(CheckBoxSize, CheckBoxSize), ControlDarkBg, true);
            CreateBoxRect("Border1", holder, new Vector2(0f, boxTop),
                new Vector2(2f, CheckBoxSize), BorderDark);
            CreateBoxRect("Border2", holder, new Vector2(CheckBoxSize - 2f, boxTop),
                new Vector2(2f, CheckBoxSize), BorderBright);
            // 上下两条横边：原版边框由 VGUI 内部绘制（`clientscheme.res:177-179` 只给 `CheckButtonBorder1/2` 两个颜色），
            // 只画左右两条会退化成一竖条、看不出是勾选框。四边同色凑成方框（外观属"本项目新增"，见差异 #33）。
            CreateBoxRect("BorderTop", holder, new Vector2(0f, boxTop),
                new Vector2(CheckBoxSize, 2f), BorderDark);
            CreateBoxRect("BorderBottom", holder, new Vector2(0f, boxTop - (CheckBoxSize - 2f)),
                new Vector2(CheckBoxSize, 2f), BorderBright);
            // 勾 = 原版 Marlett gid 12 的**预渲染贴图**（`ResPaths.MenuCheckGlyph`，渲染器 =
            // 判据资产 `tools/probes/make-check-glyph.py`，出处见本类「勾字形」段）。
            // ⛔ 位置 / 尺寸**沿用原值** `4f / boxTop-4f` + `CheckBoxSize-8f`：动它会波及 options
            //    面板的全部几何证据（8×8 的画框，贴图按 preserveAspect 缩进去画）。
            var mark = CreateBoxRect("Mark", holder,
                new Vector2(4f, boxTop - 4f),
                new Vector2(CheckBoxSize - 8f, CheckBoxSize - 8f), CheckMark);
            mark.gameObject.SetActive(initial);
            // 生成期 `Game.Res` 通常还没起来 ⇒ 这里往往落空；运行期由 `OptionsPanel` 走树时
            // 再调一次（`ApplyCheckMarkSprite`），贴图到手前保持旧行为 = 一块**原色**小方块。
            ApplyCheckMarkSprite(mark);

            var text = CreateLabel("Label", holder, label, OriginalFontSize,
                new Vector2(CheckTextIndent, 0f), new Vector2(size.x - CheckTextIndent, size.y),
                TextAnchor.MiddleLeft, Text);

            var btn = holder.gameObject.AddComponent<Button>();
            btn.targetGraphic = box;
            btn.transition = Selectable.Transition.None;
            var row = new CheckRow { Box = box.rectTransform, Mark = mark, Label = text, Btn = btn, State = initial };
            btn.onClick.AddListener(() =>
            {
                row.State = !row.State;
                row.Mark.gameObject.SetActive(row.State);
                if (onToggle != null) onToggle(row.State);
            });
            return row;
        }

        /// <summary>勾选框方框边长（本项目新增：原版由 Marlett 字模决定，无像素值可引）。</summary>
        public const float CheckBoxSize = 16f;

        /// <summary>勾选框文案起点（本项目新增，理由同 <see cref="CheckBoxSize"/>）。</summary>
        public const float CheckTextIndent = 24f;

        /// <summary>
        /// 原版 `ComboBox` / `CLabeledCommandComboBox` 的复刻件：一块左对齐文本的按钮，**点击循环**档位。
        ///
        /// <para>⚠️ 引擎没有下拉控件（VGUI `ComboBox` 的弹出列表在本工程没有对应实现）⇒ 本件用"点击循环"
        /// 代替，**登记为允许的差异**。位置 / 尺寸仍逐字取自 `.res`。</para>
        /// </summary>
        public static ComboRow CreateComboRow(string name, Transform parent, Vector2 pos, Vector2 size,
            string[] options, int index, Action<int> onChange)
        {
            var btn = CreateOriginalButton(name, parent, options != null && options.Length > 0 ? options[index] : "-",
                pos, size, null);
            var row = new ComboRow { Button = btn, Options = options, Index = index, OnChange = onChange };
            if (btn != null)
            {
                var text = btn.GetComponentInChildren<Text>();
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() =>
                {
                    if (row.Options == null || row.Options.Length == 0) return;
                    row.Index = (row.Index + 1) % row.Options.Length;
                    if (text != null) text.text = row.Options[row.Index];
                    if (row.OnChange != null) row.OnChange(row.Index);
                });
            }
            return row;
        }

        /// <summary>`CheckButton` 的句柄（可序列化，能存进预制体）。</summary>
        [Serializable]
        public sealed class CheckRow
        {
            public RectTransform Box;
            public Image Mark;
            public Text Label;
            public Button Btn;
            public bool State;

            /// <summary>就地设置勾选态（不触发回调，用于从设置刷新 UI）。</summary>
            public void SetState(bool state)
            {
                State = state;
                if (Mark != null) Mark.gameObject.SetActive(state);
            }
        }

        /// <summary>`ComboBox` 的句柄（可序列化，能存进预制体）。</summary>
        [Serializable]
        public sealed class ComboRow
        {
            public Button Button;
            public string[] Options;
            public int Index;
            [NonSerialized] public Action<int> OnChange;

            public string Current => Options != null && Options.Length > 0 ? Options[Index] : "-";
        }
    }
}
