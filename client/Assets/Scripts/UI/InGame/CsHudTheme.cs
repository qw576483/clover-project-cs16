using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 游戏内 HUD 的配色与控件工厂（**薄**封装 <see cref="UIFactory"/> / <see cref="CsUiStyle"/>）。
    ///
    /// <para>
    /// 静态界面，而 HUD 是"压在 3D 画面上、必须一眼可读"的动态界面 —— 它要的是
    /// 描边/阴影、按阵营取色、按血量取色、贴边定位这几件事。两套取色混在一起，
    /// 迟早出现"某个数字在两种底色下都看不清"。
    /// </para>
    ///
    /// <para>
    /// 布局约定：HUD 根节点铺满 1920x1080 参考分辨率（引擎 <c>UIManager</c> 的层级节点），
    /// 各控件用 <see cref="Place"/> 以"哪个角 + 哪个方向偏移"定位，不写死绝对坐标轴。
    /// </para>
    ///
    /// <para>
    /// 数值真源一律来自 <see cref="CsConst"/>（血量上限 / 击杀信息条数 / 伤害提示时长…），
    /// 本文件只放**布局与配色**，不放玩法数值。
    /// </para>
    /// </summary>
    public static class CsHudTheme
    {
        // ─────────────── 文字 ───────────────
        public static readonly Color TextMain = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Color TextDim = new Color32(0xC0, 0xC0, 0xC0, 0xFF);
        /// <summary>
        /// CS 1.6 HUD 主体文字色 = <c>(255,176,0)</c> = <b><c>#FFB000</c></b>（金橙）。
        ///
        /// <para><b>出处</b>：① 原版 1920×1080 实机截图逐像素实测 —— 掩膜 = 精确色 <c>(255,176,0)±2</c>，
        /// <c>原版资源/cs16-maps/screenshots_to_conv/*.bmp</c> 31/31 张一致（量测口径与原样可跑的脚本见
        /// <c>原版资源/cs16src/cs16game/app/cstrike/resource/clientscheme.res:24</c>
        /// 的 <c>BaseText "255 176 0 255"</c> 逐通道一致。</para>
        ///
        /// </summary>
        public static readonly Color TextHud = new Color32(0xFF, 0xB0, 0x00, 0xFF);

        // ─────────────── 状态色 ───────────────
        /// <summary>血量健康（> 60）。</summary>
        public static readonly Color HealthOk = new Color32(0x9C, 0xFF, 0x9C, 0xFF);
        /// <summary>血量中等（25 ~ 60）。</summary>
        public static readonly Color HealthMid = new Color32(0xFF, 0xE1, 0x64, 0xFF);
        /// <summary>血量危险（&lt; 25）—— 规格要求 &lt;25 变红。</summary>
        public static readonly Color HealthLow = new Color32(0xFF, 0x4B, 0x3C, 0xFF);
        public static readonly Color ArmorColor = new Color32(0x9C, 0xD8, 0xFF, 0xFF);
        /// <summary>金钱常态（灰绿，CS 1.6 的钱是偏暗的绿）。</summary>
        public static readonly Color MoneyNormal = new Color32(0x86, 0xD8, 0x86, 0xFF);
        /// <summary>可以买东西时高亮（规格：<c>CanBuy</c> 时高亮绿）。</summary>
        public static readonly Color MoneyCanBuy = new Color32(0x7C, 0xFF, 0x64, 0xFF);
        public static readonly Color Danger = new Color32(0xFF, 0x44, 0x33, 0xFF);
        public static readonly Color Warn = new Color32(0xFF, 0xC0, 0x40, 0xFF);
        /// <summary>击杀信息 / 消息栏（CS 1.6 的黄色系统字）。</summary>
        public static readonly Color Message = new Color32(0xFF, 0xE0, 0x80, 0xFF);
        /// <summary>买不起 / 不可用（灰）。</summary>
        public static readonly Color Disabled = new Color32(0x8A, 0x8A, 0x8A, 0xFF);

        // ─────────────── 阵营色（CT 蓝 / T 橙红，与 CsUiStyle 的主菜单橙区分开）───────────────
        public static readonly Color TeamCT = new Color32(0x6C, 0xA8, 0xFF, 0xFF);
        public static readonly Color TeamT = new Color32(0xFF, 0xA8, 0x50, 0xFF);
        public static readonly Color TeamSpec = new Color32(0xB0, 0xB0, 0xB0, 0xFF);

        // ─────────────── 底板 / 控件 ───────────────
        public static readonly Color PanelBg = new Color32(0x00, 0x00, 0x00, 0x99);
        public static readonly Color PanelBgSoft = new Color32(0x00, 0x00, 0x00, 0x66);
        public static readonly Color BarBack = new Color32(0x00, 0x00, 0x00, 0x8C);
        /// <summary>
        /// 准星色 = <c>cl_crosshair_color</c> 的默认值 <c>50 250 50</c>（R/G/B 十进制）。
        ///
        /// <para><b>出处</b>：<c>原版资源/cs16src/cs16game/app/cstrike/cl_dlls/client.dll:0x0e6e3c</c>
        /// （默认值串；名串 <c>client.dll:0x0e6e28</c>、注册点 <c>client.dll:0x041174</c>）。</para>
        /// </summary>
        public static readonly Color Crosshair = new Color32(0x32, 0xFA, 0x32, 0xFF);

        // ─────────────── HUD 图标（**原版位图**，不是字体字形）───────────────
        //
        // 原版 HUD 的血量/护甲图标是**位图精灵**，出处
        // `原版资源/cs16src/cs16game/app/cstrike/sprites/hud.txt` 的 640 档行：
        //   :121 `cross            640 640hud7  48  25 24 24`   血量
        //   :135 `suit_full        640 640hud7   0  25 24 24`   护甲（无头盔）
        //   :137 `suithelmet_full  640 640hud7   0 124 24 24`   护甲（有头盔）
        // 像素由 `原版资源/解包产物/cs16_asset_extract.py` 的第 ③ 段从本体 `640hud7.spr` 解出，
        // 落到 `Resources/UI/Art/hud_{cross,suit_full,suithelmet_full}.png`（24×24 RGBA）。
        // 编码与秒表图标同一套（**RGB = 白 + alpha = 精灵索引**；☑ 理由见 StopwatchTint 的注释：
        // 这些精灵是 GoldSrc 的加性灰阶遮罩，索引即亮度 ⇒ 屏上像素 = (索引/255) × 着色）。
        //
        // 三张图标的**加载路径真源在 `Core/ResPaths.cs`**（本类只留尺寸 / 间距，不再放路径字面量）：
        //   ResPaths.HudHealthIcon / ResPaths.HudArmorIcon / ResPaths.HudArmorHelmetIcon。

        /// <summary>
        /// 图标绘制尺寸 = <c>hud.txt</c> 640 档的源矩形 <b>24×24</b>（1:1 绘制，与秒表图标同一口径）。
        /// </summary>
        public const float HudIconSizePx = 24f;

        /// <summary>
        /// 图标与它右侧数字之间的间距（px）。
        ///
        /// <para><b>本项目新增</b>：原版这个间距在 <c>cstrike/cl_dlls/client.dll</c> 里
        /// （那一排 HUD 的绝对坐标尚未解出，见对照表 F-04 / 验收表差异 #16）⇒ 它**不是**原版值，
        /// 只负责"图标在数字左边、两者不重叠"。不要把它当成原版对齐的依据。</para>
        /// </summary>
        public const float HudIconNumberGapPx = 6f;

        /// <summary>
        /// 图标着色 = <b>纯白</b>（= 不加颜色调制，直接显示精灵自己的灰阶）。
        ///
        /// <para><b>为什么是白</b>：这三张精灵是 GoldSrc 的加性灰阶遮罩（调色板逐项 <c>(i,i,i)</c>），
        /// 本身不带颜色；原版屏上颜色由渲染时的调制决定，而那个调制值**本工程拿不到**
        /// —— 原版 HUD 那一排的坐标与颜色都写死在 <c>cstrike/cl_dlls/client.dll</c>，
        /// 且项目内 31 张原版 1920×1080 截图全是旁观机位（没有这排图标）
        /// ⇒ 见对照表 F-04 / 验收表差异 #16。故这里**不编**一个颜色，取"无调制"（白）。
        /// 等拿到一张第一人称、HUD 打开的原版截图后再定色。</para>
        /// </summary>
        public static readonly Color HudIconTint = Color.white;

        /// <summary>血量低于该值显示红色（规格 H1）。</summary>
        public const int HealthLowThreshold = 25;
        /// <summary>血量低于该值显示黄色。</summary>
        public const int HealthMidThreshold = 60;

        /// <summary>
        /// 系统消息 / 击杀条在屏幕上停留的秒数 = <c>hud_deathnotice_time</c> 默认 <b><c>6</c></b> 秒。
        ///
        /// <para><b>出处</b>：<c>原版资源/cs16src/cs16game/app/cstrike/cl_dlls/client.dll:0x0e77f8</c>
        /// （默认值串 "6"；名串 <c>client.dll:0x0e77e0</c>、注册点 <c>client.dll:0x045d46</c>）。</para>
        /// </summary>
        public const float MessageLifetime = 6f;

        /// <summary>
        /// 雷达边长 = <b>128 px</b>（参考分辨率 1920×1080，1:1 绘制）。
        ///
        /// <para><b>取值与出处</b>：原版 <c>sprites/hud.txt:183</c>
        /// 的行是 <c>radar 640 radar640 0 0 128 128</c> —— 名字 <c>radar</c>、**分辨率档 `640`**、
        /// 精灵 <c>radar640</c>、源矩形 <c>0 0 128 128</c>。<c>hud.txt</c> 的"分辨率档"列就是
        /// GoldSrc 选精灵表的判据（屏幕宽 &gt; 640 就用 `640` 那一档），所以 1920×1080 下用的仍是
        /// <c>radar640.spr</c> 的 <b>原始 128×128</b> 像素。</para>
        ///
        /// <para><b>"要不要按分辨率放大"= 有实测判据</b>：同一批原版 1920×1080 截图里
        /// 秒表元素的 ink 实测 <b>23×25</b>（<c>策划/验收表.md</c> H15 / 对照表 U-34；
        /// 载体重测口径见 <c>CsHudTheme</c> 秒表一节），而 <c>hud.txt:127</c> 给它的源矩形是
        /// <b>24×24</b> ⇒ **640 档精灵在 1080p 上就是 1:1、不按屏幕缩放**。
        /// 右上角那条 <b>2×67</b> 竖线**不是** <c>hud.txt</c> 的 <c>divider</c>
        /// （那把是血量/护甲之间的分隔符，不在右上角），而是另一个元素（对照表 U-35，色 <c>(96,58,2)</c>）
        /// —— 拿它去反推缩放比率会得出与 stopwatch 矛盾的结果。</para>
        ///
        /// <para>因此雷达边长 = <b>128</b>（原版源矩形的原生像素，不换算）。
        /// 对照表 U-06 的"差 +72"、验收表差异 #14 随之消除。</para>
        ///
        /// <para>**仍无载体可证的两项**（登记在验收表「允许的差异」）：① 雷达**在屏幕上的落点**
        /// （原版写在 <c>cl_dlls/client.dll</c> 里，未反汇编）；② 原版 <c>radar640.spr</c> 与
        /// <c>overviews/de_dust2.bmp</c> **本机不在盘** ⇒ 底图由原版几何离线生成
        /// （<c>Resources/UI/Art/overview_de_dust2.png</c>），
        /// 圆盘底框与 <c>cl_radartype</c> 两型未复刻。</para>
        /// </summary>
        public const float RadarSize = 128f;

        /// <summary>
        /// 雷达**内区边长**（px）= <see cref="RadarSize"/> 减去每边 3px 的边框 = 122。
        /// <see cref="CsRadarWidget.Build"/> 里的内区就是按 3px 内缩建的；写成常量是为了让底图的
        /// 尺寸/比例能在**建节点时**（布局尚未跑）就定下来。
        /// </summary>
        public const float RadarInnerSize = RadarSize - 6f;

        /// <summary>GoldSrc 世界单位 → 米（1 unit = 1 inch = 0.0254 m；引擎地图与地形同此口径）。</summary>
        public const float GoldSrcUnitToMetre = 0.0254f;

        /// <summary>
        /// 原版 overview 的 <c>ZOOM</c> —— 逐字取自 <c>原版资源/cs16src/cstrike/overviews/de_dust2.txt:5</c>
        /// 的 <c>ZOOM 1.50</c>（同文件 <c>:6</c> 是 <c>ORIGIN -223 1097 -192</c>、<c>:7</c> 是 <c>ROTATED 0</c>）。
        /// </summary>
        public const float RadarOverviewZoom = 1.50f;

        /// <summary>
        /// 原版 overview 覆盖的世界窗口**半宽**（GoldSrc 单位）—— X 轴 = 6144/ZOOM/2 = <b>2048</b>。
        ///
        /// <para>出处（不是估的）：<c>原版资源/hlsdk/cl_dll/hud_spectator.cpp:1069-1193</c>
        /// （Half-Life SDK <c>CHudSpectator::DrawOverviewLayer()</c>，文件头 SHA256 见 <c>原版资源/清单.md</c>
        /// ⇒ 世界 X 跨度 = 6 × 2×4096/1.5/8 = <b>6144/ZOOM = 4096</b> 单位，对应原图**竖直** 768px。</para>
        /// </summary>
        public const float RadarWindowHalfXUnits = 6144f / (2f * RadarOverviewZoom);

        /// <summary>
        /// 原版 overview 覆盖的世界窗口**半宽**（GoldSrc 单位）—— 世界 Y（本工程 Z 轴）=
        /// 8192/ZOOM/2 = <b>2730.6667</b>。
        ///
        /// <para>出处同 <see cref="RadarWindowHalfXUnits"/>：<c>yStep = -(2*4096/(zoom*aspect))/yTiles</c>、
        /// <c>screenaspect = 4/3</c>、Y 方向走 **8** 步 ⇒ 跨度 = 8 × 2×4096/(1.5×4/3)/6 = <b>8192/ZOOM = 5461.3333</b>
        /// 单位，对应原图**水平** 1024px。⇒ 每像素 = 8/ZOOM = 5.3333 单位（各向同性）。</para>
        /// </summary>
        public const float RadarWindowHalfZUnits = 8192f / (2f * RadarOverviewZoom);

        /// <summary>
        /// 原版 overview 底图的像素比例（1024×768 = 4:3）—— 底图必须按这个比例绘制，
        /// 不许拉成正方形（否则点与底图在水平/竖直上尺度不同，"同尺度同原点"这条判据直接不成立）。
        /// 出处：载体 <c>cstrike__overviews__de_dust2.bmp</c> 的 DIB 头 = 1024×768。
        /// </summary>
        public const float RadarMapPixelAspect = 1024f / 768f;

        /// <summary>
        /// 雷达世界窗口的**中心**（本工程世界坐标，米）= 原版 <c>ORIGIN</c> 经**地标配准**换算到本坐标系。
        ///
        /// <para><b>推导（每个数字都是实测，不是估的）</b>：</para>
        /// <list type="number">
        /// <item>载体 <c>cstrike__overviews__de_dust2.bmp</c> 里两个红包点字形的质心像素
        /// A=(264.81, 124.66)、B=(214.53, 644.27)。</item>
        /// <item>本工程包点世界质心（<c>de_dust2_geo.bin</c>，与 <c>de_dust2_markers.bytes</c> 交叉核对 delta=0）：
        /// A=(+1535, +1358)、B=(−1170, +1546) 单位。</item>
        /// <item>世界→原图像素的映射公式：
        /// <c>u ← −Z</c>、<c>v ← −X</c>、每像素 8/ZOOM = 5.3333 单位。</item>
        /// <item>把 A、B 各自锚到原图窗口中心像素 (511.5, 383.5) 再取平均 ⇒
        /// 中心 = (X 187.5, Z 2.2) 单位 = (4.7625, 0.05588) m。</item>
        /// </list>
        ///
        /// <para><b>已登记的不确定度</b>：两处锚点各自反推的中心互差约 80 单位（≈2.0 m），
        /// 来源是地标向量的 <b>1.55°</b> 旋转残差（同一探针；它是"两点定标"能给出的全部信息）。
        /// 已如实登记进 <c>策划/差异登记.tsv</c>，没有把它伪装成 0。</para>
        /// </summary>
        public static readonly Vector2 RadarWindowCenter =
            new Vector2(187.5f * GoldSrcUnitToMetre, 2.2f * GoldSrcUnitToMetre);

        /// <summary>
        /// 雷达框左上角距屏幕左/上边缘的偏移（px）。
        ///
        /// <para><b>本项目取值</b>：原版雷达在屏幕上的绝对落点写在 <c>cstrike/cl_dlls/client.dll</c>
        /// 里（与 HUD 那一排同样未反汇编，见对照表 F-04 / BLOCKED-1），且项目内原版截图全是
        /// 旁观机位、画面里没有雷达 ⇒ 无像素可量。本值只保证"在左上角、不贴边"，不是原版值。</para>
        /// </summary>
        public static readonly Vector2 RadarTopLeftOffset = new Vector2(24f, -14f);

        /// <summary>
        /// 雷达**底图**的着色（含整体透明度）。
        ///
        /// <para>原版雷达是把俯视图**半透明**压在 3D 画面上（点/图都能透出后面的世界）；
        /// 具体 alpha 无载体可证 ⇒ 取 0.75，只调"能看清地图"这一件事。不谎称原版值。</para>
        /// </summary>
        public static readonly Color RadarMapTint = new Color(1f, 1f, 1f, 0.75f);

        //
        // 绿这一支还有第二个旁证：原版 `radar640.spr` 的调色板是 `(0, G, 0)`（纯绿族，
        // G 取值 1..228，见上面 RadarSize 注释）⇒ 绿是原版雷达自己的颜色。
        // **逐通道 RGB 本机不可证**（原版点色写在 `client.dll` 里，未反汇编；项目内原版截图没有雷达）
        // ⇒ 这三支色按上面那句判据取"绿/黄/蓝"三色，登记在验收表「允许的差异」。

        public static readonly Color RadarSelf = new Color32(0x28, 0xE6, 0x28, 0xFF);

        public static readonly Color RadarMate = new Color32(0xFF, 0xB0, 0x00, 0xFF);

        public static readonly Color RadarOther = new Color32(0x50, 0x9B, 0xFF, 0xFF);

        /// <summary>已安放的炸弹 = 红点（原版雷达上炸弹会闪；闪烁周期见 <c>CsRadarWidget.BombBlinkPeriod</c>）。</summary>
        public static readonly Color RadarBomb = new Color32(0xFF, 0x38, 0x28, 0xFF);

        // 点尺寸（px）。**本项目取值**（原版点尺寸写在 client.dll / overview bmp 里，本机不可证）；
        // 按 128 px 雷达的可读性给，比例 = 自己 &gt; 队友 &gt; 观察者。
        /// <summary>自己那一颗的边长（px）。</summary>
        public const float RadarSelfDotPx = 7f;
        /// <summary>队友/敌人那一颗的边长（px）。</summary>
        public const float RadarMateDotPx = 5f;
        /// <summary>观察者那一颗的边长（px）。</summary>
        public const float RadarSpecDotPx = 4f;
        /// <summary>炸弹那一颗的边长（px）。</summary>
        public const float RadarBombDotPx = 7f;

        // ═══════════════════════ 右上角比分 / 回合计时块（对照表 V-01~V-03）═══════════════════════
        //
        // 全部坐标 = **原版 1920×1080 实机截图量出来的 ink bbox**，本单位参考分辨率**也是 1920×1080**
        // （`client/Assets/Scenes/Menu.unity` 的 CanvasScaler `m_ReferenceResolution: {x: 1920, y: 1080}`；
        // 引擎 UIManager 的层级节点铺满它）⇒ **同一基准，直接写原版像素、不换算**。
        //
        // 出处：`原版资源/cs16-maps/screenshots_to_conv/*.bmp`（31 张 1920×1080）；
        //   比分行 1（Counter-Terrorists : 0） ink y[38..49] x[1543..1750]（31/31 一致）
        //   比分行 2（Terrorists : 0）         ink y[65..76] x[1626..1750]
        //   回合计时（4:42）                   ink y[65..76] x[1827..1869]（右边缘随数字宽度在 1868~1870 变 ⇒ 右对齐）
        //   竖分隔线                           x[1776..1777] y[27..93] = 2×67 px，暗背景下实测色 ≈ (96,58,2)
        // 两行的数字右边缘都落在 x=1750 ⇒ 两行都用**右对齐**文本；行内 "标签 : 数字" 是**一整串**文本
        // （token 分段实测：标签右边缘 1715 / 冒号 1727..1729 / 数字 1741..1750，两行这三段都对齐）。

        /// <summary>比分行 1 的上边缘（= 原版 ink y 38）。</summary>
        public const float ScoreCTTopPx = 38f;
        /// <summary>比分行 2 / 回合计时的上边缘（= 原版 ink y 65）。</summary>
        public const float ScoreTTopPx = 65f;
        /// <summary>两行文字的 ink 高（原版实测 12 px，31/31 一致）；文本框高就取它，文本垂直居中。</summary>
        public const float ScoreLineHeightPx = 12f;
        /// <summary>比分两行数字的右边缘距屏幕右边的距离 = 1920 − 1750（原版数字右边缘 x=1750）。</summary>
        public const float ScoreRightInsetPx = 170f;
        /// <summary>回合计时的右边缘距屏幕右边的距离 = 1920 − 1869（原版计时右边缘 x=1869）。</summary>
        public const float ClockRightInsetPx = 51f;
        /// <summary>
        /// 竖分隔线距屏幕右边的距离 = 1920 − 1778 = <b>142</b>。
        /// 说明：定位用 <see cref="PlaceTopRight"/>（锚点/轴心都取右上角），所以"距屏右 142"指**块的右边缘**在 x=1778；
        /// 块宽 2 px ⇒ 实际覆盖 <b>x=1776..1777</b>，与原版实测 bbox 逐像素一致。
        /// </summary>
        public const float ScoreDividerInsetPx = 142f;
        /// <summary>比分/计时文本框宽（仅作容器，文本右对齐 ⇒ 宽度不影响落点；取原版 ink 宽 208 px + 余量）。</summary>
        public const float ScoreBoxWidthPx = 240f;
        /// <summary>回合计时文本框宽（同上；原版 ink 宽 43~44 px）。</summary>
        public const float ClockBoxWidthPx = 64f;

        /// <summary>
        /// 比分 / 计时行的字号。**由原版 ink 高反推**：原版这两行的 ink 高实测 12 px（31/31 一致），
        /// 而拉丁字体数字/大写高度约为字号的 0.7 倍 ⇒ 12 ÷ 0.7 ≈ 16.5，取 <b>16</b>：
        /// 文本框高 = 12 px 且文本垂直居中 ⇒ 渲染出的 ink 落在盒中心，与原版 bbox 的偏差 ≤ 1 px。
        ///
        /// <para>原版这两行的**字体族**仍不可证（<c>clientscheme.res:381-394</c> 写的是 Verdana，
        /// 而 HUD 数字在别处走位图字形）⇒ 见对照表 U-24：本行只对齐"位置 + 颜色 + 字号量级"，
        /// 字形本身不是 1:1。</para>
        /// </summary>
        public const int ScoreFontSize = 16;

        /// <summary>比分/计时之间的竖分隔线尺寸 = 原版实测 2 × 67 px（x[1776..1777] y[27..93]，5/5 一致）。</summary>
        public const float ScoreDividerWidthPx = 2f;
        /// <summary>见 <see cref="ScoreDividerWidthPx"/>（原版实测高 67 px）。</summary>
        public const float ScoreDividerHeightPx = 67f;
        /// <summary>竖分隔线的上边缘（= 原版 y 27；与比分块无关，单独按截图写）。</summary>
        public const float ScoreDividerTopPx = 27f;

        /// <summary>
        /// 竖分隔线颜色：原版截图暗背景图（<c>fy_quake_night0000.bmp</c> / <c>cs_paintball0000.bmp</c> /
        /// <c>de_okna0000.bmp</c>）上该线同色像素实测 RGB ≈ 95~97 / 57~59 / 0~2（取中值 <c>(96,58,2)</c>）。
        /// </summary>
        public static readonly Color ScoreDivider = new Color32(0x60, 0x3A, 0x02, 0xFF);

        // ═══════════════════════ 秒表图标（对照表 U-34）═══════════════════════
        //
        // **形状/像素来自原版本体的精灵表**（不是通用图标、不是自画）：
        //   `原版资源/cs16src/cs16game/app/cstrike/sprites/hud.txt:127`
        //     `stopwatch  640  640hud7  144  72  24  24`
        //   ⇒ 源矩形 = `640hud7.spr` 的 (144,72,24,24)；该 sprite 画布 256×256、
        //     调色板是 0..255 的**纯灰阶**（同一帧逐帧索引像素；解码器与核对逻辑见
        //     `原版资源/解包产物/cs16_asset_extract.py`，它会断言 consumed == 文件长度）。
        //     解码后的 PNG 落在 `client/Assets/Resources/UI/Art/stopwatch.png`（24×24 RGBA）。
        //
        // **屏幕落点来自原版 1920×1080 实机截图的像素量化**：
        //   ink bbox = `x[1795..1817] y[58..82]`，同一批里 5/5 暗背景图逐张一致
        //
        // **两张载体不是同一版贴图，此处如实登记（不许当成一致）**：
        //   ① 墨迹尺寸：本体精灵（亮度阈值 ≥128）ink = 17×22 px，截图 ink = 23×25 px（≈1.2~1.35 倍）；
        //   ② 圆盘内部：截图那一只在图标中心行是**空心**的（y=70 那行内圈像素 ≈ 背景 2），
        //      而本体精灵同一行内圈索引 ≈ 146（实心玻璃盘）⇒ 逐像素比对**不可能**一致。
        //   （全盘搜过 `640hud7.spr`：只有本体 `cstrike/sprites/` 那一份）⇒ 拿不到"同一版"的精灵表。
        //   把 24×24 的精灵框**中心对齐**到截图 ink 的中心 (1806, 70) ⇒ 框 = x[1794..1817] y[58..81]，
        //   其中**右边缘 1817 与上边缘 58 与实测 ink 逐像素相同**（其余两边 ≤1 px）。
        //   需要社区包自己那份 `640hud7.spr`，或由人确认以本体精灵为准**。

        // ═══════════════════════ `Map: <地图名>` 行（F-01）═══════════════════════
        //
        // 原版**有**这一行，位置 = **竖分隔线右侧、与比分行 1 同高**（计时行在它下面一行）。
        // 出处 = `策划/基线图/original/de_dust2_freecam_A_00.jpg`（1280×1024）右上角实测：
        //   比分行 1 ink y[35..50] · 比分行 2 ink y[62..73] · 竖分隔线 x[1047..1049]（跨两行）
        //   `Map: de_dust2` ink x[1113..1252] y[35..50]   ← 与比分行 1 **同一行高**
        //   秒表 + `4:50`   ink x[1060..1184] y[62..73]   ← 与比分行 2 同一行高
        //   项目内另一张同族载体 `hud_1920x1080_gg_dust2_aim_trainning.bmp`（1920×1080）**没有**这一行
        //   —— 两张载体的 HUD 比例并不一致，见下面对 x 的说明。）
        //
        // **y = 与比分行 1 同高**（[硬]）：直接用 <see cref="ScoreCTTopPx"/>（38）。
        // **字号 = 与比分同一号**（[硬]）：freecam 里 `Map:` 的 ink 高 16 px = 比分行 ink 高 16 px
        //   ⇒ 取 <see cref="ScoreFontSize"/>（我们这边 ink 高 12 px，比例一致）。
        //
        // **x 只能给到"结构位置"（分隔线右侧），具体右缘是推导值、不是 1080p 实测**：
        //   · 唯一带这一行的载体是 1280×1024 的 freecam，而它与 1920×1080 载体的 HUD
        //     **比例/锚定都不一致**（比分行 ink 高 16 vs 12，分隔线却 56 高 vs 67 高 ⇒ 不是同一尺度），
        //   · 能站得住的推导只有"**同图内两个右缘缩进之比**"（比例无量纲、与尺度无关）：
        //     freecam 里 `Map:` 行右缘距屏右 1280−1252 = 28 px、计时行右缘距屏右 1280−1184 = 96 px，
        //     比值 28/96 = 0.2917；套到我们已经实测的 <see cref="ClockRightInsetPx"/>（51 px）
        //     ⇒ 51 × 0.2917 ≈ **15 px**。
        //   · 这个值同时是**唯一可行**的：竖分隔线右边到屏右只剩 1920−1778 = 142 px，
        //     而 `Map: de_dust2` 在本工程字号下约 124 px 宽 ⇒ 右缘缩进不可能大于 ~18 px。
        //   ⇒ 残余（绝对 x 的 1080p 真值）已登记在 `策划/验收表.md` 的「允许的差异」里。

        /// <summary>
        /// `Map: <地图名>` 行的**右缘**距屏幕右边的距离（px）—— 推导值
        /// <c>ClockRightInsetPx × (28 / 96) ≈ 15</c>（28/96 = freecam 里 Map 行与计时行的右缘缩进之比）。
        /// 推导过程与"为什么只能推导"见上面的整段注释。
        /// </summary>
        public const float MapRightInsetPx = 15f;

        /// <summary>`Map: <地图名>` 行的上边缘（= 与比分行 1 同高，取 <see cref="ScoreCTTopPx"/>）。
        /// 出处：原版截图 `策划/基线图/original/de_dust2_freecam_A_00.jpg` 的 `Map: de_dust2` 行
        /// ink y[35..50]（与比分行 1 **同一行高**，取证实录见上面那一整段）；
        /// 原版**有**这一行这一事实 = `策划/对照表.md` §9-F01。</summary>
        public const float MapTopPx = ScoreCTTopPx;

        /// <summary>`Map: <地图名>` 行文本的固定前缀（原版 freecam 实测就是 <c>"Map: "</c> + 地图名）。</summary>
        public const string MapLabelPrefix = "Map: ";

        // 秒表图标的加载路径真源 = `Core/ResPaths.cs` 的 ResPaths.HudStopwatchIcon（本类不再放路径字面量）。

        /// <summary>图标绘制尺寸 = 原版 <c>hud.txt:127</c> 的 640 档源矩形 <b>24×24</b>（1:1 绘制，不缩放）。</summary>
        public const float StopwatchSizePx = 24f;

        /// <summary>图标框上边缘（= 截图实测 ink 上边缘 y 58）。</summary>
        public const float StopwatchTopPx = 58f;

        /// <summary>图标框右边缘距屏幕右边 = 1920 − 1818 = <b>102</b>（= 截图实测 ink 右边缘 x 1817）。</summary>
        public const float StopwatchRightInsetPx = 102f;

        /// <summary>
        /// 图标着色 = **原版截图里该图标实测的像素色**。
        ///
        /// <para><b>为什么 PNG 是"白 RGB + alpha = 精灵索引"</b>：该 sprite 是金源引擎的**加性灰阶遮罩**
        /// （调色板 256/256 项都在灰阶线上：<c>pal[i] == (i,i,i)</c>），索引本身承载全部信息。
        /// 导出的 PNG 把它编码成 <b>RGB=(255,255,255) + alpha=索引</b>，于是 uGUI 的
        /// <c>Image</c> 算出的屏上像素 = <c>(索引/255) × 本颜色</c> ——
        /// 与原版"加法混合叠在场景上"的**贡献值线性相等**。</para>
        ///
        /// <para><b>不许把 alpha 写成二值</b>（索引&gt;0 就不透明）：那会把图标圆盘内部的
        /// 低索引像素画成一块**实心暗块**，而原版那些像素几乎不加任何颜色 ⇒ 肉眼一眼就不一样。</para>
        ///
        /// <para><b>出处（5 张暗背景截图实测，逐张一致）</b>：
        /// ① 峰值像素在 5 张里都是 <c>(175,175,48)</c>（cs_paintball / de_okna / fy_quake_night /
        /// cs_prospeedball / gg_33_texture）⇒ 取它就是取**原版最亮的那个像素值**；
        /// ② 通道比 <b>R : G : B = 1.000 : 1.000 : 0.274</b>（线性拟合 <c>G = 1.003·R</c>，n≈308~312）；
        /// ③ 用它做离线合成后，我方 ink **均值 103 / 峰值 175**，原版 ink **均值 99.7 / 峰值 175**
        /// （均值差 3%、峰值相同）—— 复核方式见回报里的并排图。
        /// 它**不是** HUD 文字色 <c>#FFB000</c>（那支 R/G = 1.449，实测 R/G = 1.000），
        /// 也不是"我挑的一个好看的黄"。</para>
        ///
        /// <para><b>残差（已知，登记于此）</b>：① 原版是加法混合（叠在场景上做加法），
        /// 我方是常规 alpha 混合 ⇒ **亮背景**下原版会把图标"加亮"、我方会把它压回原色，亮处观感有差；
        /// ② 两张载体的图标墨迹尺寸本身不同（本体精灵 17×22 vs 截图 23×25，见上面的登记）⇒
        /// 落点按中心对齐后，四边仍有 ≤3 px 差。这两条想消掉都需要**同一份载体的精灵表**
        /// （现有仓库里没有：`cs16-maps` 不含 `sprites/`）。</para>
        /// </summary>
        public static readonly Color StopwatchTint = new Color32(0xAF, 0xAF, 0x30, 0xFF);

        // ═══════════════════════ 文本 ═══════════════════════

        /// <summary>带黑色投影的 HUD 文本（压在 3D 画面上，没有投影会在亮场景里糊掉）。</summary>
        public static Text CreateText(string name, Transform parent, string content, int fontSize,
            TextAnchor anchor, Color color)
        {
            var text = UIFactory.CreateText(name, parent, content, fontSize, anchor, color);
            // HUD 文本不换行、不截断：数字变长时布局不该跳
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            var shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.9f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
            shadow.useGraphicAlpha = false;
            return text;
        }

        // ═══════════════════════ 定位 ═══════════════════════

        /// <summary>
        /// 把矩形钉在父节点的某个锚点/轴心（<paramref name="pos"/> 为相对该锚点的偏移）。
        /// HUD 的元素全部靠这个定位：换分辨率时只需改锚点，不用重算绝对坐标。
        ///
        /// <para>实现只剩转发：通用形式在引擎 <see cref="UIFactory.Place"/>（<c>结构规则.md</c> §4.4
        /// —— 同一种能力只允许一个实现）。本族保留旧签名，调用方一个都不用改。</para>
        /// </summary>
        public static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            UIFactory.Place(rt, anchor, pivot, pos, size);
        }

        public static void PlaceBottomLeft(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, Vector2.zero, Vector2.zero, pos, size);

        public static void PlaceBottomRight(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, new Vector2(1f, 0f), new Vector2(1f, 0f), pos, size);

        public static void PlaceTopLeft(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), pos, size);

        public static void PlaceTopRight(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, new Vector2(1f, 1f), new Vector2(1f, 1f), pos, size);

        public static void PlaceTopCenter(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), pos, size);

        public static void PlaceBottomCenter(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), pos, size);

        public static void PlaceCenter(RectTransform rt, Vector2 pos, Vector2 size) =>
            UIFactory.Place(rt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);

        // ═══════════════════════ 控件 ═══════════════════════

        /// <summary>
        /// 进度条（底 + 已填）。已填部分用**锚点宽度**驱动（引擎 <see cref="UIFactory.SetBarWidth"/>），
        /// （skill 明确点出的坑；世界空间的同类陷阱见引擎 <c>WorldHpBar</c>）。
        ///
        /// <para>本方法只是"CS HUD 的组装"：底色 <see cref="BarBack"/> + <see cref="CsHudBar"/> 句柄，
        /// 每一步都调引擎既有件（<c>CreatePanel</c> / <c>Place</c> / <c>SetBarWidth</c>），
        /// 不含任何自建的进度条实现 —— 比例换算那份唯一实现在引擎。</para>
        /// </summary>
        public static CsHudBar CreateBar(string name, Transform parent, Vector2 anchor, Vector2 pivot,
            Vector2 pos, Vector2 size, Color fillColor)
        {
            var bg = UIFactory.CreatePanel(name, parent, BarBack, false);
            Place(bg.rectTransform, anchor, pivot, pos, size);

            var fill = UIFactory.CreatePanel("Fill", bg.rectTransform, fillColor, false);
            var bar = new CsHudBar { Root = bg.rectTransform, Fill = fill.rectTransform, FillImage = fill };
            bar.Set(1f);
            return bar;
        }

        /// <summary>纯色块（面板底 / 分隔条 / 图标块）。</summary>
        public static Image CreateBlock(string name, Transform parent, Vector2 anchor, Vector2 pivot,
            Vector2 pos, Vector2 size, Color color, bool raycast = false)
        {
            var img = UIFactory.CreatePanel(name, parent, color, raycast);
            Place(img.rectTransform, anchor, pivot, pos, size);
            return img;
        }

        /// <summary>
        /// 深色内容框（面板 / 输入框底）：半透明黑 + 一圈 1px 边线，接近 CS 1.6 的菜单观感。
        /// </summary>
        public static Image CreateFrame(string name, Transform parent, Vector2 anchor, Vector2 pivot,
            Vector2 pos, Vector2 size, Color? backdrop = null, bool raycast = true)
        {
            var img = UIFactory.CreatePanel(name, parent, backdrop ?? PanelBg, raycast);
            Place(img.rectTransform, anchor, pivot, pos, size);
            return img;
        }

        public static Button CreateButton(string name, Transform parent, string label, Vector2 anchor,
            Vector2 pivot, Vector2 pos, Vector2 size, System.Action onClick, bool accent = false)
        {
            var img = UIFactory.CreateButton(name, parent, label, size, Vector2.zero,
                accent ? CsUiStyle.Accent : CsUiStyle.Field, onClick);
            Place(img.rectTransform, anchor, pivot, pos, size);

            var button = img.GetComponent<Button>();
            if (button != null)
            {
                var colors = button.colors;
                colors.normalColor = accent ? CsUiStyle.Accent : CsUiStyle.Field;
                colors.highlightedColor = accent ? CsUiStyle.Accent : CsUiStyle.Accent;
                colors.pressedColor = CsUiStyle.AccentDim;
                colors.selectedColor = colors.normalColor;
                colors.disabledColor = new Color(0.22f, 0.22f, 0.22f, 1f);
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.08f;
                button.colors = colors;
            }

            var text = img.GetComponentInChildren<Text>();
            if (text != null)
            {
                text.fontSize = 22;
                text.color = accent ? CsUiStyle.TextDark : CsUiStyle.Text;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
            }

            return button;
        }

        /// <summary>
        /// HUD 输入框（控制台用）的外观 = 引擎 <see cref="UIFactory.CreateInputField"/> 的**最后一个参数**。
        ///
        /// <para><b>输入框的构造全在引擎</b>（`Runtime/Presentation/UIWidgetControls.cs`）——
        /// 含"用 <c>DefaultControls.CreateInputField</c>"与"<c>placeholder</c> 是 <c>Graphic</c>、
        /// 必须 <c>as Text</c> 才能设字体 / 字号 / 文案"这两条说明。这里只留 HUD 的取值：
        /// 底色用菜单同一支 <see cref="CsUiStyle.Field"/>（HUD 输入框压在暗底上，与菜单同色才不跳），
        /// 文本用 HUD 主体色 <see cref="TextHud"/>、20 号；占位 18 号、60% 灰。</para>
        ///
        /// <para><b><c>placeholder</c> 必须先 <c>as Text</c></b>：uGUI <c>InputField.placeholder</c> 的
        /// 声明类型是 <c>Graphic</c>（<c>com.unity.ugui .../Runtime/UGUI/UI/Core/InputField.cs</c> 的
        /// <c>public Graphic placeholder</c>），对它直接取 <c>.font / .fontSize / .text</c> 会 **CS1061**；
        /// <c>CsUiStyle.CreateInputField</c> 的 placeholder 段已有那一行。</para>
        /// </summary>
        public static WidgetInputFieldStyle InputFieldStyle => new WidgetInputFieldStyle
        {
            Background = CsUiStyle.Field,
            Colors = new ColorBlock
            {
                normalColor = CsUiStyle.Field,
                highlightedColor = CsUiStyle.Field,
                pressedColor = CsUiStyle.Field,
                selectedColor = CsUiStyle.Field,
                disabledColor = new Color(0.3f, 0.3f, 0.3f, 1f),
                colorMultiplier = 1f,
                fadeDuration = 0f,
            },
            TextColor = TextHud,
            TextFontSize = 20,
            PlaceholderColor = new Color(TextDim.r, TextDim.g, TextDim.b, 0.65f),
            PlaceholderFontSize = 18,
            CaretColor = CsUiStyle.Accent,
            SelectionColor = new Color(CsUiStyle.Accent.r, CsUiStyle.Accent.g, CsUiStyle.Accent.b, 0.4f),
        };

        // ═══════════════════════ 格式化 ═══════════════════════

        /// <summary>秒 → <c>M:SS</c>（规格 H4 的 <c>1:45</c>）。</summary>
        public static string FormatClock(float seconds)
        {
            var total = Mathf.Max(0, Mathf.RoundToInt(seconds));
            return $"{total / 60}:{total % 60:00}";
        }

        /// <summary>血量取色（规格 H1：&lt;25 红）。</summary>
        public static Color HealthColor(int health)
        {
            if (health < HealthLowThreshold) return HealthLow;
            if (health < HealthMidThreshold) return HealthMid;
            return HealthOk;
        }

        /// <summary>阵营取色。</summary>
        public static Color TeamColor(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.CT: return TeamCT;
                case CsTeam.T: return TeamT;
                default: return TeamSpec;
            }
        }

        /// <summary>阵营显示名（HUD / 记分板统一口径）。</summary>
        public static string TeamName(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.CT: return "CT";
                case CsTeam.T: return "T";
                default: return "SPEC";
            }
        }

        /// <summary>回合阶段文案（Freeze 期规格要求显示"买枪中"）。</summary>
        public static string PhaseText(CsRoundPhase phase)
        {
            switch (phase)
            {
                case CsRoundPhase.Freeze: return "买枪中";
                case CsRoundPhase.Live: return string.Empty;
                case CsRoundPhase.RoundEnd: return "回合结束";
                case CsRoundPhase.MatchEnd: return "比赛结束";
                default: return string.Empty;
            }
        }

        /// <summary>回合结束原因文案（必须与 <see cref="CsRoundEndReason"/> 一一对应）。</summary>
        public static string ReasonText(CsRoundEndReason reason)
        {
            switch (reason)
            {
                case CsRoundEndReason.BombExploded: return "炸弹爆炸";
                case CsRoundEndReason.BombDefused: return "炸弹被拆除";
                case CsRoundEndReason.AllTargetsEliminated: return "一方被全歼";
                case CsRoundEndReason.TimeExpired: return "时间到";
                default: return "回合结束";
            }
        }

        /// <summary>背包里的弹药文本（规格 H3：<c>30 / 90</c>）。</summary>
        public static string AmmoText(int mag, int reserve) => $"{mag} / {reserve}";
    }

    /// <summary>
    /// 进度条句柄（可序列化 → 能存进预制体）。<see cref="Set"/> 走锚点宽度，见
    /// <see cref="CsHudTheme.CreateBar"/> 的说明。
    /// </summary>
    [System.Serializable]
    public sealed class CsHudBar
    {
        public RectTransform Root;
        public RectTransform Fill;
        public Image FillImage;

        private float _last = float.NaN;

        public void Set(float progress01)
        {
            // 值没变就不碰 UI：HUD 每帧都在刷，无意义的重建/重排要避免
            if (!float.IsNaN(_last) && Mathf.Abs(_last - progress01) < 0.001f) return;
            _last = progress01;
            // 直接调引擎（比例换算的唯一实现在那里），不再经 CsUiStyle 中转一层。
            UIFactory.SetBarWidth(Fill, progress01);
        }

        public void SetColor(Color color)
        {
            if (FillImage != null) FillImage.color = color;
        }

        public void SetActive(bool active)
        {
            if (Root != null) Root.gameObject.SetActive(active);
        }
    }
}
