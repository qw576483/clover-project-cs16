namespace Cs16.Core
{
    /// <summary>
    ///
    /// <para><b>为什么单开这个类</b>：这些路径字符串原先散落在七个文件里 ——
    /// <c>Core/CsConst</c>（地图数据）、<c>Module/Map/CsMap</c>（标记表）、
    /// <c>Module/Audio/SfxService</c> 与 <c>Module/Combat/CombatAudio</c>（音效根，**同值同名各定义一份**）、
    /// <c>UI/InGame/CsHudTheme</c>（HUD 图标）、<c>UI/Flow/MainMenuPanel</c>（菜单字标）、
    /// <c>Editor/MapGen/Dust2Layout</c>（生成器侧的标记表）。后果是：换一个素材目录要全工程 grep，
    /// 且两份同值常量迟早改歪一个（"两套并行实现"的温床）。
    /// 现在**路径字面量只写在这里**——改路径只改这一处，所有调用点一律走 <c>ResPaths.X</c>。</para>
    ///
    /// <para><b>口径（两条）</b>：① 这里的值都是 <c>Assets/Resources/</c> **之后**的相对路径，
    /// 直接喂给引擎的 <c>Game.Res.LoadAsset&lt;T&gt;(path)</c> / <c>Game.Map.LoadAsync(path)</c>；
    /// ② **不带扩展名**——真实文件是 <c>.bytes</c> / <c>.wav</c> / <c>.png</c> / <c>.tga</c>，
    /// 由资源系统按请求类型解析。值是**逐字**从各自的原定义处搬过来的
    /// （含 <see cref="SoundSfxPrefix"/> 的尾斜杠），一个字符都没改。</para>
    /// </summary>
    public static class ResPaths
    {
        // ==================================================================
        //  地图数据 —— 落在 Resources/MapData/
        // ==================================================================

        /// <summary>
        /// 可行走位图（真实文件 <c>Resources/MapData/de_dust2.bytes</c>）：引擎
        /// <c>Game.Map.LoadAsync</c> 的入参，由菜单 <c>Clover/CS16/烘焙 de_dust2</c> 导出（与服务端同源）。
        /// </summary>
        public const string MapDust2 = "MapData/de_dust2";

        //    命名标记点改随**同一份** `MapData/de_dust2.bytes` 的 `CloverMapFormat.FlagMarkers` 段同行，
        //    运行时经引擎 `Game.Map.Points/GetPoints/TryGetPoint` 取（见 `Module/Map/CsMap.cs`）。
        //    这里不许再冒出一个"标记表旁路"的路径常量：同一份空间事实只允许一份载体。

        // ==================================================================
        //  音效 —— 落在 Resources/Sound/SFX/（短名形如 sfx/xxx）
        // ==================================================================

        /// <summary>
        /// SFX **根目录前缀**（注意带尾斜杠）。用法固定是"前缀 + 短名"，不要为了看起来整齐
        /// 把它拆成"目录 + 名字"两段（那会改变调用点语义）：
        /// <c>ResPaths.SoundSfxPrefix + CsAudioTuning.Step[i]</c> ⇒ <c>Sound/SFX/sfx/step</c>，
        /// 真实文件 <c>Resources/Sound/SFX/sfx/*.wav</c>（原版 wav，由 <c>tools</c> 侧脚本生成）。
        /// 短名真源见 <c>Module/Audio/CsAudioTuning</c> 与 <c>Core/CsWeapons.SoundFire</c>。
        /// </summary>
        public const string SoundSfxPrefix = "Sound/SFX/";

        /// <summary>
        /// 背景音乐短名（喂引擎 <c>Game.Sound.PlayBGM(clipName)</c>；引擎拼的是
        /// <c>Sound/BGM/{clipName}</c>）——真实文件 <c>Resources/Sound/BGM/gamestartup.mp3</c>。
        ///
        /// <para>文件名 <c>gamestartup</c> 就是**原版 GoldSrc 约定的启动曲文件名**
        /// （引擎在主菜单出现时读 <c>&lt;gamedir&gt;/media/gamestartup.mp3</c> 播放；本工程沿用同名文件，
        /// 见 <c>Module/Flow/AppFlow</c> 的启动曲注释）。</para>
        /// </summary>
        public const string BgmGameStartup = "gamestartup";

        /// <summary>
        /// UI 点击音短名（喂引擎 <c>Game.Sound.PlaySFX(clipName)</c>）——真实文件
        /// <c>Resources/Sound/SFX/sfx/menu_click.wav</c>（与其它音效同批由 <c>cs16_build.py</c> 从 A 的
        /// <c>sound/**</c> 产出；引擎侧根前缀见 <see cref="SoundSfxPrefix"/>，喂给引擎的短名要带 <c>sfx/</c>）。
        ///
        /// <para>UI 层**不许 using 任何 Module**，拿不到 <c>CsAudioTuning</c> ⇒ 这个短名只能放 Core，
        /// 由 <c>UI/Flow/CsPanelBase</c> 的点击音用。不要在 UI 里另抄一份字面量。</para>
        /// </summary>
        public const string SfxMenuClick = "sfx/menu_click";

        // ==================================================================
        //  HUD 图标 —— 落在 Resources/UI/Art/（原版位图精灵解出的 PNG）
        // ==================================================================

        /// <summary>
        /// 血量图标（真实文件 <c>Resources/UI/Art/hud_cross.png</c>）——
        /// 原版 <c>sprites/hud.txt:121</c> 的 <c>cross</c>，由 <c>640hud7.spr</c> 解出（24×24）。
        /// </summary>
        public const string HudHealthIcon = "UI/Art/hud_cross";

        /// <summary>
        /// 护甲图标（无头盔，真实文件 <c>Resources/UI/Art/hud_suit_full.png</c>）——
        /// 原版 <c>sprites/hud.txt:135</c> 的 <c>suit_full</c>。
        /// </summary>
        public const string HudArmorIcon = "UI/Art/hud_suit_full";

        /// <summary>
        /// 护甲图标（有头盔，真实文件 <c>Resources/UI/Art/hud_suithelmet_full.png</c>）——
        /// 原版 <c>sprites/hud.txt:137</c> 的 <c>suithelmet_full</c>。
        /// </summary>
        public const string HudArmorHelmetIcon = "UI/Art/hud_suithelmet_full";

        /// <summary>
        /// 回合计时秒表图标（真实文件 <c>Resources/UI/Art/stopwatch.png</c>）——
        /// 原版 <c>sprites/hud.txt:127</c> 的 <c>stopwatch</c>，由 <c>640hud7.spr</c> 的
        /// <c>144,72,24,24</c> 源矩形解出（24×24）。
        /// </summary>
        public const string HudStopwatchIcon = "UI/Art/stopwatch";

        /// <summary>
        /// 雷达**俯视底图**（真实文件 <c>Resources/UI/Art/overview_de_dust2.png</c>，128×128）。
        ///
        /// <para><b>为什么不是"原版那张 bmp"</b>：原版雷达底图 = <c>cstrike/overviews/de_dust2.bmp</c>
        /// （787510 B = 1024×768×8bpp）+ <c>overviews/de_dust2.txt</c>（ZOOM/ORIGIN/ROTATED），
        /// 这两份载体**在本机不在盘**（<c>原版资源/{cs16src,cs109,cs16-maps,解包产物}</c> 为空，
        /// 见 <c>原版资源/清单.md</c>）。按载体降级链退到**级①原始数据**：由工程内的
        /// <c>Assets/ThirdParty/Dust2/de_dust2_geo.bin</c>（= 原版 de_dust2 BSP 的几何）离线俯视
        /// 栅格化而成，生成器 = <c>tools/probes/render-overview.py</c>
        /// （判据资产，随仓库提交；口径 = 逐三角面按自身法线分"可站立面/竖直面/陡面"，
        /// 俯视投影后按高度着色，A/B 点字母打在原版 <c>func_bomb_target</c> 的标记点质心上）。</para>
        ///
        /// <para>不是手绘近似图、也不是网格截图；像素 100% 来自原版几何。拿到原版
        /// <c>overviews/de_dust2.bmp</c> 后可**只换这个 PNG**（同名覆盖），代码一行不用改。</para>
        /// </summary>
        public const string RadarOverviewDust2 = "UI/Art/overview_de_dust2";

        // ==================================================================
        //  菜单贴图 —— 落在 Resources/UI/Art/（原版 GameUI 字标）
        // ==================================================================

        /// <summary>
        /// 主菜单游戏字标·常态（金）——原版 <c>cstrike/resource/game_menu.tga</c>（207×32）；
        /// 真实文件 <c>Resources/UI/Art/game_menu.tga</c>。
        /// </summary>
        public const string MenuLogoNormal = "UI/Art/game_menu";

        /// <summary>
        /// 主菜单游戏字标·悬停（白）——原版 <c>cstrike/resource/game_menu_mouseover.tga</c>
        /// （与常态同一画布）；真实文件 <c>Resources/UI/Art/game_menu_mouseover.tga</c>。
        /// </summary>
        public const string MenuLogoHover = "UI/Art/game_menu_mouseover";

        /// <summary>
        /// 勾选框里那个"勾"的贴图（真实文件 <c>Resources/UI/Art/menu_check.png</c>，**132×140 RGBA**）——
        /// 原版 **Marlett** 字体 <c>gid 12</c> 的预渲染件（可达码位 <c>U+F061</c>）。
        ///
        /// <para><b>载体</b>：<c>原版资源\cs16src\marlett.ttf</c>（27,724 B，本机
        /// <c>C:\Windows\Fonts\marlett.ttf</c> 的字节副本）；**渲染器** = <c>tools/probes/make-check-glyph.py</c>
        /// （判据资产，与 <c>tools/probes/marlett-glyphs.py</c> 同口径：补一张 (3,1) cmap → FreeType 光栅化 →
        /// 裁到紧致 ink 框）。**未拉伸、未自绘**：尺寸 = 判据 <c>bbox</c>，逐像素来自原版字形。</para>
        ///
        /// <para><b>判据数字</b>（<c>--size 300</c>，与 <c>marlett-glyphs.py</c> 同一量法）：
        /// <c>ink=7523  bbox=132x140  ratio=0.943  comps=1  vx=0.352  arm=0.264  tick=Y</c>。
        /// PNG 的 <c>alpha&gt;=32</c> 像素数 = <c>7523</c>（与判据逐数相等）。</para>
        ///
        /// <para><b>颜色</b>：RGB 逐像素 = 原版 <c>CheckButtonCheck</c> → <c>BrightControlText
        /// "255 176 0 255"</c>（<c>clientscheme.res:30/179</c>）= 本工程常量 <see cref="Cs16.UI.CsUiStyle"/>-侧
        /// 的 <c>CheckMark</c>；面板上 <c>Image.color</c> 给**白**即得原版橙色勾。</para>
        ///
        /// <para>为什么是贴图而不是字符：见 <see cref="CheckGlyphFont"/> 的实测结论（该字体在 Unity
        /// 侧取不到自己的字形）。</para>
        /// </summary>
        public const string MenuCheckGlyph = "UI/Art/menu_check";

        // ==================================================================
        //  菜单背景拼图 —— 落在 Resources/Background/（原版 12 张 TGA）
        // ==================================================================
        //
        // 载体：`原版资源/cs16src/cs16game/app/cstrike/resource/background/800_{1,2,3}_{a,b,c,d}_loading.tga`
        // 共 12 张（24bpp TGA），按 `原版资源/.../valve/resource/backgroundlayout.txt` 的
        // `scaled` 坐标拼成 800×600。**已逐字节复制**进工程（SHA256 与源文件一致）。

        // ==================================================================
        //  程序化特效贴图 —— 落在 Resources/UI/Art/
        // ==================================================================
        //
        // `fx_spark` 一张是**本项目程序化生成**的（`tools/probes/make-fx-sprites.py`，登记在
        // `client/资源欠缺清单.md`）：原版击中火星没有独立载体。另两张（枪口火焰 / 弹痕）已由
        // **原版载体**解出同名覆盖（枪口火焰 ← `sprites/muzzleflash2.spr`，工具 `tools/probes/spr-extract.py`；
        // 弹痕/血迹 ← `decals.wad` 的 `{shot*` / `{blood*`，工具 `tools/probes/wad3-extract.py`）。
        // 换素材仍是**只换文件**（同名覆盖），代码一行都不用改。
        //
        // `decals.wad` 的 `{` 贴花是 **decal** 口径、**不是** `{` 透明纹理口径：整张图是灰阶
        // **不透明度**（白=透明、黑=实心），`palette[255]` 是整张贴花的**基色**（弹孔=黑、血=暗红）
        // 口径出处与逐条自检见 `tools/probes/wad3-extract.py` 的模块头。

        /// <summary>枪口火焰精灵（真实文件 <c>Resources/UI/Art/fx_muzzleflash.png</c>，64×64）。
        ///
        /// <para>载体 = 原版 <c>sprites/muzzleflash2.spr</c> 的**帧 0**
        /// （<c>原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash2.spr</c>，64×64×3 帧，
        /// 由 <c>tools/probes/spr-extract.py</c> 解出）。这是**默认**那张（多数武器用它）。</para>
        ///
        /// <para>原版**逐武器**选 <c>muzzleflash1..4</c>，而选择表在引擎 <c>hw.dll</c>（不在盘）——
        /// 见 <see cref="FxMuzzleFlashCross"/> 与 <c>策划/差异登记.tsv</c> #89。</para></summary>
        public const string FxMuzzleFlash = "UI/Art/fx_muzzleflash";

        /// <summary>
        /// 枪口火焰**十字形**变体（真实文件 <c>Resources/UI/Art/fx_muzzleflash3.png</c>，72×72）
        /// —— 原版 <c>sprites/muzzleflash3.spr</c> 的帧 0。
        ///
        /// <para><b>为什么单独立一条 key</b>：原版按武器选 1..4，本工程只落**有一条证据**的那条映射 ——
        /// ② 四张载体解帧后**只有** <c>muzzleflash3.spr</c> 是十字/X 形（其余三张是星芒/圆团），
        /// 唯一匹配。其余武器**不编**映射（缺口在 <c>策划/差异登记.tsv</c> #89）。</para>
        ///
        /// <para>帧数：载体是 72×72×**3 帧**，本工程只落帧 0（逐帧播放的时长无出处 —— 引擎不在盘）。</para>
        /// </summary>
        public const string FxMuzzleFlashCross = "UI/Art/fx_muzzleflash3";

        /// <summary>弹痕**兜底**精灵（真实文件 <c>Resources/UI/Art/fx_bullethole.png</c>，16×16）——
        /// 与 <c>fx_shot1</c> 同源：原版 <c>decals.wad</c> 的 `{shot1`（口径 = 灰阶不透明度 +
        /// <c>palette[255]</c> 基色），只在 5 张变体**全都**加载不到时才被用
        /// （<c>CombatEffects.PickBulletHole</c>），见 <see cref="FxBulletHoleKeys"/>。
        /// 用户看到的就是「弹痕是一个白点」（差异 #69，2026-09-24 复查）。</summary>
        public const string FxBulletHole = "UI/Art/fx_bullethole";

        /// <summary>
        /// 弹痕**五变体 key 表**：真实文件 <c>Resources/UI/Art/fx_shot1.png</c> … <c>fx_shot5.png</c>。
        ///
        /// <para><b>为什么逐条写成字面量</b>（而不是"前缀 + 序号"拼串）：拼出来的 key 在**静态**扫描里
        /// 看不见 —— 闸门 <c>coverage-diff</c> 的 D1 维度会把那几张贴图判成"**文件在盘上但无人读**"
        /// 字面量 key 既能被引用扫描命中，也让"名字写错 ⇒ 静默加载不到"变成可 grep 的事。</para>
        ///
        /// <para><b>载体出处</b>：原版 <c>decals.wad</c> 的 `{shot1` … `{shot5`（各 16×16、载体原生尺寸），
        /// 由 <c>tools/probes/wad3-extract.py</c> 解出（每个 lump 过"mip 连续 + `pal_ofs+2+768==size` +
        /// 调色板计数 256" 三重自洽断言）。</para>
        ///
        /// <para><b>为什么是 5 张</b>：原版 <c>mp.dll</c> 的贴花注册名表实测为
        /// `{shot1 {shot2 {shot3 {shot4 {shot5` **连续 5 项**（表基址 `0x10165ED8`、步长 8、
        /// 索引 0..4，见 `策划/差异登记.tsv` #69 的出处段）。不是"随便挑几张"。</para>
        /// </summary>
        public static readonly string[] FxBulletHoleKeys =
        {
            "UI/Art/fx_shot1",
            "UI/Art/fx_shot2",
            "UI/Art/fx_shot3",
            "UI/Art/fx_shot4",
            "UI/Art/fx_shot5",
        };

        /// <summary>弹痕变体张数（= <see cref="FxBulletHoleKeys"/> 的长度；不许与那张表分开维护）。</summary>
        public static int FxBulletHoleVariants => FxBulletHoleKeys.Length;

        /// <summary>
        /// 血迹贴花**六变体 key 表**：真实文件 <c>Resources/UI/Art/fx_blood1.png</c> … <c>fx_blood6.png</c>
        /// （原版 `decals.wad` 的 `{blood1` … `{blood6`，mp.dll 名表索引 13..18）。
        ///
        /// <para><b>为什么是 6 张 / 为什么不是 `{yblood*`</b>：原版名表里血迹是**两组** ——
        /// `{blood1..6`（红）与 `{yblood1..6`（黄，异形血）。红/黄由 cvar `violence_hblood` /
        /// `violence_ablood` 选择（两个串都在 <c>mp.dll</c>），CS 里人类角色一律走**红血**组
        /// ⇒ 本工程只接红的 6 张。</para>
        ///
        /// <para>同样逐条字面量（理由见 <see cref="FxBulletHoleKeys"/>）。</para>
        /// </summary>
        public static readonly string[] FxBloodKeys =
        {
            "UI/Art/fx_blood1",
            "UI/Art/fx_blood2",
            "UI/Art/fx_blood3",
            "UI/Art/fx_blood4",
            "UI/Art/fx_blood5",
            "UI/Art/fx_blood6",
        };

        /// <summary>血迹贴花变体张数（= <see cref="FxBloodKeys"/> 的长度）。</summary>
        public static int FxBloodVariants => FxBloodKeys.Length;

        // 大口径弹痕（原版 `decals.wad` 的 `{bigshot1` … `{bigshot5`，mp.dll 名表索引 28..32）
        //    本工程**刻意不落盘、不接**：原版"哪种武器用大口径弹痕"的选择逻辑在**引擎**里
        //    （`hw.dll` 不在盘）⇒ 映射无出处。落盘 = 立刻变成"文件在盘上但无人读"的 T0 不一致
        //    ⇒ 不落，等拿到出处再落。

        /// <summary>击中火星精灵（真实文件 <c>Resources/UI/Art/fx_spark.png</c>，16×16）。</summary>
        public const string FxSpark = "UI/Art/fx_spark";

        /// <summary>
        /// 菜单背景拼图**目录前缀**（注意带尾斜杠；真实文件 <c>Resources/Background/*.tga</c>）。
        /// 用法固定是"前缀 + 短名"，与 <see cref="SoundSfxPrefix"/> 同款（不改调用点语义）。
        /// </summary>
        public const string BackgroundMenuDir = "Background/";

        // ==================================================================
        //  原版图标字体 —— 落在 Resources/UI/Fonts/
        // ==================================================================

        /// <summary>
        /// 原版 Windows 图标字体 **Marlett**（真实文件 <c>Resources/UI/Fonts/marlett.ttf</c>，
        /// 27,724 B，SHA1 <c>2d9e6a4751ece0ede6790acbea5e604b3f3c2241</c>）——
        /// 用来画菜单里的"勾 / 箭头 / 圆点"这类原版 VGUI 图标字形（uGUI 的字符集中没有）。
        ///
        /// <para><b>载体出处</b>：原版 CS 1.6 安装目录的 <c>marlett.ttf</c>
        /// （<c>原版资源\cs16src\marlett.ttf</c>）；本机 <c>C:\Windows\Fonts\marlett.ttf</c> 与它
        /// 只把**被引用的那一个文件**复制进工程。</para>
        ///
        /// <para><b>码位出处</b>：判据资产 <c>tools/probes/marlett-glyphs.py</c>（<c>--size 300</c>）
        /// 与决定性读图（勾 = <c>U+F061</c>，gid12）。</para>
        ///
        /// <para>本常量保留为登记（字体文件仍在 <c>Resources/UI/Fonts/marlett.ttf</c>，是那份 PNG 的载体出处）—— 不要删它。</para>
        ///
        /// <para>不要在调用点另写字面量或 <c>Resources.Load</c> 路径 —— 一律走本常量。</para>
        /// </summary>
        public const string CheckGlyphFont = "UI/Fonts/marlett";

        /// <summary>
        /// 取某一块背景拼图的加载路径（不带扩展名）：<c>BackgroundMenuTile("800_1_a_loading")</c>
        /// ⇒ <c>Background/800_1_a_loading</c>（真实文件 <c>Resources/Background/800_1_a_loading.tga</c>）。
        ///
        /// <para>短名就是 <c>backgroundlayout.txt</c> 里写的原版文件名去扩展名 —— 拼图的**位置/尺寸表**
        /// 是布局数据（在 <c>UI/Flow/CsMenuBackground</c> 里逐行对应那份 .txt），只有**路径**归到这里。</para>
        /// </summary>
        public static string BackgroundMenuTile(string tileName)
        {
            return BackgroundMenuDir + tileName;
        }
    }
}
