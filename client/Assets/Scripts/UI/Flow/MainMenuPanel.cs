using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 主菜单 —— 按原版 **GameUI 的 `GameMenu`** 重做（原版的形态就是：左上角一张游戏字标，
    /// 正下方一串文字项）：
    ///
    /// <list type="bullet">
    /// <item>项与文案：原版 `cstrike/resource/gamemenu.res` 的**局外四项**
    /// （`"10" #GameUI_GameMenu_NewGame` / `"11" …FindServers` / `"12" …Options` / `"13" …Quit`）
    /// + `valve/resource/gameui_english.txt:110/115/118/119` 的英文原文；</item>
    /// <item>几何：原版 `platform/resource/trackerscheme.res` 的 `InGameDesktop` 块（:164-173）
    /// —— 项高 / 步进 / 左上 inset / 项宽，逐项出处见各常量注释，一律 ×<see cref="CsUiStyle.ResScale"/>；</item>
    /// <item>文字色：同块 `:166-168` 的常态 / 悬停 / 按下三色；</item>
    /// <item>顶部字标：原版贴图 `game_menu.tga`（常态，金）/ `game_menu_mouseover.tga`（悬停，白）。</item>
    /// </list>
    ///
    /// <para>UI 只发事件（<see cref="Events"/> 常量），由 <c>Module/Flow/AppFlow</c> 订阅后驱动，
    /// 因此本类**不 using 任何 Module**。</para>
    /// </summary>
    public class MainMenuPanel : CsPanelBase
    {
        // ══════════════════ 原版几何（每个常量都带载体出处）══════════════════
        //
        // 设计空间 → 画布的换算比 = CsUiStyle.ResScale（2.25；640×480 → 1920×1080，理由见那里）。

        /// <summary>菜单项高 = 原版 `InGameDesktop/MenuItemHeight "28"`（trackerscheme.res:171）× ResScale。</summary>
        private const float ItemHeight = 28f * CsUiStyle.ResScale;

        /// <summary>菜单项步进 = 项高：原版各项**紧邻**排列，无额外间隙（间隙值无载体，⛔ 不编）。
        /// <para>⛔ 符号：<see cref="CsUiStyle.AnchoredTopLeft"/> 的口径是「以左上角为原点、**y 为负 = 向下**」
        /// ⇒ 往下排必须**逐步减少** y，所以步进取负。</para></summary>
        private const float ItemStep = -ItemHeight;

        /// <summary>菜单项宽 = 原版贴图 `game_menu.tga` 的画布宽 207（唯一宽度载体）× ResScale。</summary>
        private const float ItemWidth = 207f * CsUiStyle.ResScale;

        /// <summary>菜单左上 inset = 原版 `InGameDesktop/GameMenuInset "32"`（trackerscheme.res:172）× ResScale。</summary>
        private const float Inset = 32f * CsUiStyle.ResScale;

        /// <summary>顶部字标宽 = 原版 `game_menu.tga` 画布宽 207 × ResScale。</summary>
        private const float LogoWidth = 207f * CsUiStyle.ResScale;

        /// <summary>顶部字标高 = 原版 `game_menu.tga` 画布高 32 × ResScale。</summary>
        private const float LogoHeight = 32f * CsUiStyle.ResScale;

        /// <summary>字标落点 y = 同一个 inset（原版两块共用这一个 inset 键）；**取负**＝从顶部往下 inset
        /// （<see cref="CsUiStyle.AnchoredTopLeft"/> 的 y 为负才是向下）。</summary>
        private const float LogoTop = -Inset;

        /// <summary>首个菜单项 y = 字标下沿（字标 y 再往下让出字标高）—— 项紧接字标之下。</summary>
        private const float FirstItemY = LogoTop - LogoHeight;

        // 原版字标贴图的**加载路径真源在 `Core/ResPaths.cs`**（不带扩展名）：
        //   ResPaths.MenuLogoNormal（常态，金）/ ResPaths.MenuLogoHover（悬停，白）。

        [SerializeField] private Image _logo;
        [SerializeField] private Sprite _logoNormal;
        [SerializeField] private Sprite _logoHover;
        /// <summary>原版背景拼图（12 块 TGA，见 <see cref="CsMenuBackground"/>）。</summary>
        [SerializeField] private CsMenuBackground _background;
        [SerializeField] private Button _newGameButton;
        [SerializeField] private Button _findServersButton;
        [SerializeField] private Button _optionsButton;
        [SerializeField] private Button _quitButton;

        private bool _logoRequested;
        private bool _logoWarned;

        /// <summary>
        /// 编辑器生成器（<c>Editor/Flow/FlowSetup.cs</c>）在建预制体时调一次：把两张**原版字标
        /// sprite** 绑进来 —— 运行期没有 AssetDatabase，引用必须在编辑器里绑进预制体。
        /// 传空 = 只报一次 Warn，绝不静默把字标吞掉。
        /// </summary>
        public void BindMenuSprites(Sprite normal, Sprite hover)
        {
            if (normal == null || hover == null)
            {
                WarnLogoOnce($"原版字标 sprite 缺失（常态={Describe(normal)} 悬停={Describe(hover)}）—— " +
                             "Resources/UI/Art/game_menu*.tga 没导入成 Sprite？字标不显示");
                return;
            }

            _logoNormal = normal;
            _logoHover = hover;
            ApplyLogoSprite();
            Game.Logger?.Info(Tag,
                $"主菜单字标已绑定：常态={normal.name}({normal.rect.width}×{normal.rect.height}) " +
                $"悬停={hover.name}({hover.rect.width}×{hover.rect.height})（原版 cstrike/resource/game_menu*.tga）");
        }

        public override void BuildLayout(RectTransform root)
        {
            // 第 0 层：纯色兜底（原版菜单底不是纯色 —— 它是下面那 12 块 TGA 拼图；
            // 这一层只在拼图缺失时才有可见效果，⛔ 不许删掉它去换"静默黑屏"）。
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);

            // 第 1 层：原版背景拼图（`backgroundlayout.txt` 的 12 块 800×600 设计空间贴图）。
            // 必须建在 Backdrop 之后、字标/菜单项之前 —— 拼图在最底层，其余元素层级不动。
            _background = CsMenuBackground.Attach(root);

            // 顶部：原版游戏字标（`game_menu.tga` 常态金 / `game_menu_mouseover.tga` 悬停白，两张同画布）
            _logo = CsUiStyle.CreateSpriteButton("Logo", root, new Vector2(Inset, LogoTop),
                new Vector2(LogoWidth, LogoHeight), _logoNormal, _logoHover, null);

            // 菜单项：原版文案 + 原版几何。点击回调在 OnOpen 里绑
            //（BuildLayout 里挂的运行时监听器不进预制体，见 CsPanelBase.Bind）
            var y = FirstItemY;
            _newGameButton = CsUiStyle.CreateMenuItem("Btn_NewGame", root, "New Game",
                new Vector2(Inset, y), new Vector2(ItemWidth, ItemHeight), null);
            y += ItemStep;
            _findServersButton = CsUiStyle.CreateMenuItem("Btn_FindServers", root, "Find Servers",
                new Vector2(Inset, y), new Vector2(ItemWidth, ItemHeight), null);
            y += ItemStep;
            _optionsButton = CsUiStyle.CreateMenuItem("Btn_Options", root, "Options",
                new Vector2(Inset, y), new Vector2(ItemWidth, ItemHeight), null);
            y += ItemStep;
            _quitButton = CsUiStyle.CreateMenuItem("Btn_Quit", root, "Quit",
                new Vector2(Inset, y), new Vector2(ItemWidth, ItemHeight), null);

            // 署名（skill §8 品牌硬规则：每个游戏的界面下方必须有这一行，**居底居中**、低调）。
            // 单一入口 = CsUiStyle.CreateCreditLabel（→ 引擎 UIFactory.CreateCreditLabel，文案取引擎默认值
            // 逐字 `by clover-engine`）：贴底锚点 / 字号 / 字体回退都在那一处，本面板不再自写文案与定位
            // （原先自写"左上锚点 + 大负 y"曾让整行掉出屏幕，只有实机截图才发现）。
            CsUiStyle.CreateCreditLabel(root);
        }

        public override void OnOpen(object param)
        {
            // ⛔ 必须在实例化之后再刷一次原版字体：Font.CreateDynamicFontFromOSFont 是**运行期对象**，
            // 写不进 .prefab ⇒ 从预制体实例化出来的 Text.font 是 null（文字根本不显示）。
            // 见 CsUiStyle.ApplyOriginalFonts 的注释（同款做法见 TeamSelectPanel / OptionsPanel）。
            CsUiStyle.ApplyOriginalFonts(transform);

            Bind(_newGameButton, () => Game.Event.Emit(Events.StartNewGame), "New Game");
            Bind(_findServersButton, () => Game.Event.Emit(Events.OpenServerList), "Find Servers");
            Bind(_optionsButton, () => Game.Event.Emit(Events.OpenOptions), "Options");
            Bind(_quitButton, () => Game.Event.Emit(Events.QuitGame), "Quit");

            // 预制体是生成器产的，字标 sprite 通常已经在预制体里；预制体被改坏时运行期兜底取一次。
            if (_logoNormal == null || _logoHover == null) EnsureLogoSprites();

            // 背景拼图的 sprite 同理：生成期已绑好就什么都不做，缺哪块才补哪块（逐块报出文件名）。
            if (_background != null) _background.EnsureSprites();
            else WarnLogoOnce("原版背景拼图组件缺失（预制体未由 FlowSetup 生成？），本界面只有纯色底");
        }

        /// <summary>运行期兜底取字标：预制体里已经绑好就什么都不做；只在缺的时候请求一次。</summary>
        private void EnsureLogoSprites()
        {
            if (_logoRequested) return;

            var res = Game.Res;
            if (res == null)
            {
                WarnLogoOnce("Game.Res 为 null（CloverRes.Init 未执行？），原版字标加载不了");
                return;
            }

            _logoRequested = true;
            res.LoadAsset<Sprite>(ResPaths.MenuLogoNormal, s =>
            {
                if (s == null)
                {
                    _logoRequested = false;
                    WarnLogoOnce($"字标常态贴图加载失败（sprite 为空）：Resources/{ResPaths.MenuLogoNormal}");
                    return;
                }
                _logoNormal = s;
                ApplyLogoSprite();
            });
            res.LoadAsset<Sprite>(ResPaths.MenuLogoHover, s =>
            {
                if (s == null)
                {
                    _logoRequested = false;
                    WarnLogoOnce($"字标悬停贴图加载失败（sprite 为空）：Resources/{ResPaths.MenuLogoHover}");
                    return;
                }
                _logoHover = s;
                ApplyLogoSprite();
            });
        }

        /// <summary>把当前手上的字标 sprite 写到 Image 上（缺常态图就先不显示，绝不画成实心白块）。</summary>
        private void ApplyLogoSprite()
        {
            if (_logo == null)
            {
                WarnIfNull(_logo, "字标 Image");
                return;
            }

            if (!CsUiStyle.ApplySpriteStates(_logo, _logoNormal, _logoHover))
                WarnLogoOnce($"字标常态 sprite 未就绪（Resources/{ResPaths.MenuLogoNormal}），字标暂不显示");
        }

        private void WarnLogoOnce(string message)
        {
            if (_logoWarned) return;
            _logoWarned = true;
            Game.Logger?.Warn(Tag, message);
        }

        private static string Describe(Object obj) => obj == null ? "<null>" : obj.name;
    }
}
