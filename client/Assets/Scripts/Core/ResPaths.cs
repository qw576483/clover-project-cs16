namespace Cs16.Core
{
    /// <summary>
    /// 工程内**素材路径真源**（skill §8「原版资源」硬约定：原版素材必须复制进
    /// <c>Assets/Resources/**</c>，加载路径一律收敛到本文件）。
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
    /// 由资源系统按请求类型解析。⛔ 值是**逐字**从各自的原定义处搬过来的
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

        /// <summary>
        /// 运行时标记点表（真实文件 <c>Resources/MapData/de_dust2_markers.bytes</c>）：
        /// 位图里没有名字，而 AI / 包点 / 买枪区都要按名字取点，所以单独带一份。
        /// 生成器（<c>Editor/MapGen</c>）导出与运行时（<c>Module/Map/CsMap</c>）读取**同源**。
        /// </summary>
        public const string MapDust2Markers = "MapData/de_dust2_markers";

        // ==================================================================
        //  音效 —— 落在 Resources/Sound/SFX/（短名形如 sfx/xxx）
        // ==================================================================

        /// <summary>
        /// SFX **根目录前缀**（注意带尾斜杠）。用法固定是"前缀 + 短名"，⛔ 不要为了看起来整齐
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
        /// <para>⚠️ UI 层**不许 using 任何 Module**，拿不到 <c>CsAudioTuning</c> ⇒ 这个短名只能放 Core，
        /// 由 <c>UI/Flow/CsPanelBase</c> 的点击音用。⛔ 不要在 UI 里另抄一份字面量。</para>
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
        /// <para>⛔ 不是手绘近似图、也不是网格截图；像素 100% 来自原版几何。拿到原版
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
        // ⚠️ 这三张是**本项目程序化生成**的（`tools/probes/make-fx-sprites.py`，登记在
        // `client/资源欠缺清单.md`）：原版枪口火焰是 `sprites/muzzleflash*.spr`、弹痕是
        // `decals.wad` 的 `{shot*`，两者载体都不在本仓库、archive.org 又连不上（实测超时）。
        // 拿到原版素材后**只换文件**（同名覆盖），代码一行都不用改。

        /// <summary>枪口火焰精灵（真实文件 <c>Resources/UI/Art/fx_muzzleflash.png</c>，64×64）。</summary>
        public const string FxMuzzleFlash = "UI/Art/fx_muzzleflash";

        /// <summary>弹痕精灵（真实文件 <c>Resources/UI/Art/fx_bullethole.png</c>，32×32）。</summary>
        public const string FxBulletHole = "UI/Art/fx_bullethole";

        /// <summary>击中火星精灵（真实文件 <c>Resources/UI/Art/fx_spark.png</c>，16×16）。</summary>
        public const string FxSpark = "UI/Art/fx_spark";

        /// <summary>
        /// 菜单背景拼图**目录前缀**（注意带尾斜杠；真实文件 <c>Resources/Background/*.tga</c>）。
        /// 用法固定是"前缀 + 短名"，与 <see cref="SoundSfxPrefix"/> 同款（⛔ 不改调用点语义）。
        /// </summary>
        public const string BackgroundMenuDir = "Background/";

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
