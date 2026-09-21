using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 游戏内常驻 HUD（规格 H1~H11 + H14 的入口；<c>Layer = Normal</c>，从进图开着到回主菜单）。
    ///
    /// <para><b>它是谁的"手"</b>：本工程没有"游戏内输入路由模块"，而买枪菜单 / H 菜单 / 记分板 /
    /// 无线电 / 控制台都必须在图内被键唤出。这些键（B / H / TAB / Z X C / ~）统一由**常驻的 HUD** 轮询，
    /// 它只负责 <c>Game.UI.Open&lt;T&gt;()</c>，具体面板自己处理自己的按键与关闭 ——
    /// 常驻面板做路由、临时面板管自己，避免"每个面板都去猜别人开没开"。</para>
    ///
    /// <para><b>数据来源</b>：全部来自 <see cref="CsHudSnapshot"/>（agent-03/04 每帧写）+
    /// <c>Game.Event</c> 事件。**本目录下没有任何对 Module 层的 using** —— 这是分层铁律
    /// （验收自检命令就是拿 Module 的命名空间去 grep 本目录，注释里也不留该字面量，免得自检出现假命中）。</para>
    ///
    /// <para><b>键盘归属</b>（谁在什么时候处理哪个键）：</para>
    /// <list type="bullet">
    /// <item>HUD：B / H / TAB / Z X C / <c>/</c> 或 <c>0</c>（控制台）—— 只在"没有弹窗面板开着"时生效；</item>
    /// <item>弹窗面板自己：ESC 关闭、数字键选项、再按一次开启键关闭；</item>
    /// <item>ESC 弹暂停菜单：归 <c>Module/Flow/AppFlow</c>（它已有"有遮挡时不弹暂停"的判断）。</item>
    /// </list>
    /// </summary>
    public class HudPanel : CsPanelBase
    {
        // ─────────── 布局常量（参考分辨率 1920x1080；只放布局，不放玩法数值）───────────
        private const float Margin = 24f;
        private const float MoneyY = 128f;
        private const float HealthY = 74f;
        private const float ArmorY = 26f;
        private const int MessageLines = 3;
        private const float MessageLineHeight = 26f;

        // ─────────── 文本 ───────────
        [SerializeField] private Text _moneyText;
        [SerializeField] private Text _healthText;
        [SerializeField] private Text _armorText;
        [SerializeField] private Text _ammoText;
        [SerializeField] private Text _weaponText;
        [SerializeField] private Text _scoreCTText;
        [SerializeField] private Text _scoreTText;
        [SerializeField] private Text _roundTimeText;
        /// <summary><c>Map: &lt;地图名&gt;</c>（原版在竖分隔线右侧、与比分行 1 同高，见 CsHudTheme 的 F-01 一节）。</summary>
        [SerializeField] private Text _mapText;
        /// <summary>比分/计时之间的竖分隔线（原版 x[1776..1777] y[27..93] = 2×67 px，见 CsHudTheme.ScoreDivider*）。</summary>
        [SerializeField] private Image _scoreDivider;
        /// <summary>比分右上角的秒表图标（原版精灵 hud.txt:127 = 640hud7 的 144,72,24,24）。</summary>
        [SerializeField] private Image _stopwatchIcon;
        /// <summary>血量图标（原版位图精灵 hud.txt:121 = 640hud7 的 48,25,24,24；**不是字形 ♥**）。</summary>
        [SerializeField] private Image _healthIcon;
        /// <summary>护甲图标（原版位图精灵 hud.txt:135/137；按有无头盔换 sprite）。</summary>
        [SerializeField] private Image _armorIcon;
        [SerializeField] private Text _roundNumberText;
        [SerializeField] private Text _phaseText;
        [SerializeField] private Text _bombText;
        [SerializeField] private Text _buyZoneText;
        [SerializeField] private Text _useLabel;

        // ─────────── 条 / 图 ───────────
        [SerializeField] private CsHudBar _healthBar;
        [SerializeField] private CsHudBar _armorBar;
        [SerializeField] private CsHudBar _useBar;
        [SerializeField] private Image _flashOverlay;
        [SerializeField] private RectTransform _hudRoot;

        // ─────────── 子件 ───────────
        [SerializeField] private CsCrosshairWidget _crosshair;
        [SerializeField] private CsRadarWidget _radar;
        [SerializeField] private CsKillFeedWidget _killFeed;
        [SerializeField] private CsSpectatorWidget _spectator;
        [SerializeField] private CsDamageIndicatorWidget _damage;

        // ─────────── 消息栏 ───────────
        [SerializeField] private Text[] _messageTexts;

        // ─────────── 运行期状态 ───────────
        private readonly CsIngameStats _stats = new CsIngameStats();
        private readonly float[] _messageUntil = new float[MessageLines];
        private readonly string[] _messageCache = new string[MessageLines];

        private bool _subscribed;
        private bool _visible;
        private bool _warnedRefs;
        /// <summary>秒表图标已经向资源模块发过一次请求（避免每次 OnUpdate/OnOpen 重复请求）。</summary>
        private bool _stopwatchRequested;
        /// <summary>秒表图标缺失/加载失败已经报过一次（防刷屏）。</summary>
        private bool _warnedStopwatch;

        // ─────────── 血量 / 护甲 位图图标（原版 640hud7 精灵；⛔ 不再用字形 ♥ / 🛡 / +）───────────
        /// <summary>血量图标 sprite（编辑器绑定 / 运行期兜底加载）。</summary>
        private Sprite _iconHealth;
        /// <summary>护甲图标 sprite（无头盔）。</summary>
        private Sprite _iconArmor;
        /// <summary>护甲图标 sprite（有头盔）。</summary>
        private Sprite _iconArmorHelmet;
        /// <summary>已经向资源模块请求过一次图标（避免每次 OnUpdate 重复请求）。</summary>
        private bool _hudIconsRequested;
        /// <summary>图标缺失/加载失败已经报过一次（防刷屏）。</summary>
        private bool _warnedHudIcons;
        /// <summary>图标就绪的运行时自证行已经打过（只打一次）。</summary>
        private bool _loggedHudIcons;
        /// <summary>本地玩家名（记分板"自己那行"用；来自 <see cref="CsPlayerSettingsStore"/>）。</summary>
        private string _selfName;
        /// <summary>`Map:` 行上一次打过日志的地图名（变了才再打）。</summary>
        private string _loggedMapName;
        private float _minX, _maxX, _minZ, _maxZ;
        private bool _haveBounds;

        /// <summary>
        /// <see cref="UILayer.Normal"/>（= <see cref="UIPanel"/> 的默认值，这里写出来是为了表达约定）：
        /// 常驻 HUD 不参与 <c>Popup</c> 的互斥与遮罩 —— 否则一开买枪菜单就会把 HUD 顶掉。
        /// </summary>
        public override UILayer Layer => UILayer.Normal;

        // ═══════════════════════ 布局 ═══════════════════════

        public override void BuildLayout(RectTransform root)
        {
            // HUD 不铺底色：它是压在 3D 画面上的信息层。
            // _hudRoot 用来一键隐藏整组 HUD（没有比赛时不该留半屏残留信息）
            var hud = UIFactory.CreateNode("HudRoot", root);
            UIFactory.Stretch(hud);
            _hudRoot = hud;

            // ---- 左下：血量 / 护甲 / 金钱（H1 / H2）----
            // 图标 = **原版位图精灵**（hud.txt:121/135/137，各 24×24），数字排在图标右边；
            // 旧实现是字体字形（♥ / 🛡 / +），已按任务书换掉。出处与编码见 CsHudTheme 的"HUD 图标"一节。
            // ⚠️ 先 enabled=false：**没有 sprite 的 Image 会被 uGUI 画成实心白块**，必须等 sprite 到手再开。
            _healthIcon = CsHudTheme.CreateBlock("HealthIcon", hud,
                Vector2.zero, Vector2.zero,
                new Vector2(Margin, HealthY + 20f - CsHudTheme.HudIconSizePx * 0.5f),
                new Vector2(CsHudTheme.HudIconSizePx, CsHudTheme.HudIconSizePx), CsHudTheme.HudIconTint);
            _healthIcon.enabled = false;

            _healthText = CsHudTheme.CreateText("Health", hud, "100", 36,
                TextAnchor.MiddleLeft, CsHudTheme.HealthOk);
            CsHudTheme.PlaceBottomLeft(_healthText.rectTransform,
                new Vector2(Margin + CsHudTheme.HudIconSizePx + CsHudTheme.HudIconNumberGapPx, HealthY),
                new Vector2(300f, 40f));

            _healthBar = CsHudTheme.CreateBar("HealthBar", hud, Vector2.zero, Vector2.zero,
                new Vector2(Margin, HealthY - 16f), new Vector2(150f, 8f), CsHudTheme.HealthOk);

            _armorIcon = CsHudTheme.CreateBlock("ArmorIcon", hud,
                Vector2.zero, Vector2.zero,
                new Vector2(Margin, ArmorY + 15f - CsHudTheme.HudIconSizePx * 0.5f),
                new Vector2(CsHudTheme.HudIconSizePx, CsHudTheme.HudIconSizePx), CsHudTheme.HudIconTint);
            _armorIcon.enabled = false;

            _armorText = CsHudTheme.CreateText("Armor", hud, "100", 26,
                TextAnchor.MiddleLeft, CsHudTheme.ArmorColor);
            CsHudTheme.PlaceBottomLeft(_armorText.rectTransform,
                new Vector2(Margin + CsHudTheme.HudIconSizePx + CsHudTheme.HudIconNumberGapPx, ArmorY),
                new Vector2(260f, 30f));

            _armorBar = CsHudTheme.CreateBar("ArmorBar", hud, Vector2.zero, Vector2.zero,
                new Vector2(Margin, ArmorY - 14f), new Vector2(110f, 6f), CsHudTheme.ArmorColor);

            _moneyText = CsHudTheme.CreateText("Money", hud, "$800", 32, TextAnchor.MiddleLeft,
                CsHudTheme.MoneyNormal);
            CsHudTheme.PlaceBottomLeft(_moneyText.rectTransform, new Vector2(Margin, MoneyY),
                new Vector2(320f, 38f));

            // ---- 右下：弹药 / 武器名（H3）----
            _ammoText = CsHudTheme.CreateText("Ammo", hud, "0 / 0", 38, TextAnchor.MiddleRight,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceBottomRight(_ammoText.rectTransform, new Vector2(-Margin, ArmorY),
                new Vector2(300f, 44f));

            _weaponText = CsHudTheme.CreateText("Weapon", hud, string.Empty, 22, TextAnchor.MiddleRight,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceBottomRight(_weaponText.rectTransform, new Vector2(-Margin, HealthY - 6f),
                new Vector2(360f, 28f));

            // ---- 右上角：比分两行 + 回合计时（H4 / H5）----
            // 原版是**右上角两行 + 右对齐**（不是顶部中央三件套），文字色 = HUD 金橙 #FFB000；
            // 行 1 = "Counter-Terrorists : N"、行 2 = "Terrorists : N"、回合计时在行 2 右侧。
            // 坐标/字号/颜色全部有原版出处 —— 见 CsHudTheme 的"右上角比分 / 回合计时块"一节的逐项注释。
            _scoreCTText = CsHudTheme.CreateText("ScoreCT", hud, "Counter-Terrorists : 0",
                CsHudTheme.ScoreFontSize, TextAnchor.MiddleRight, CsHudTheme.TextHud);
            _scoreTText = CsHudTheme.CreateText("ScoreT", hud, "Terrorists : 0",
                CsHudTheme.ScoreFontSize, TextAnchor.MiddleRight, CsHudTheme.TextHud);
            _roundTimeText = CsHudTheme.CreateText("RoundTime", hud, "0:00",
                CsHudTheme.ScoreFontSize, TextAnchor.MiddleRight, CsHudTheme.TextHud);

            // `Map: <地图名>`（原版有：竖分隔线右侧、与比分行 1 同高。出处与 x 的推导见 CsHudTheme 的 F-01 一节）
            _mapText = CsHudTheme.CreateText("MapName", hud,
                CsHudTheme.MapLabelPrefix + CsConst.MapDust2,
                CsHudTheme.ScoreFontSize, TextAnchor.MiddleRight, CsHudTheme.TextHud);

            // 秒表图标（原版在计时左侧，见 CsHudTheme 的"秒表图标"一节）。
            // ⚠️ 先 enabled=false：**没有 sprite 的 Image 会被 uGUI 画成实心白块**
            // （Graphic.OnPopulateMesh 的实心分支），必须等 sprite 到手（生成器绑定 / Game.Res 加载）再开。
            _stopwatchIcon = CsHudTheme.CreateBlock("StopwatchIcon", hud,
                new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero,
                new Vector2(CsHudTheme.StopwatchSizePx, CsHudTheme.StopwatchSizePx), Color.white);
            _stopwatchIcon.enabled = false;

            ApplyScoreBlock();

            _roundNumberText = CsHudTheme.CreateText("RoundNumber", hud, "第 1 回合", 20,
                TextAnchor.MiddleCenter, CsHudTheme.TextDim);
            CsHudTheme.PlaceTopCenter(_roundNumberText.rectTransform, new Vector2(0f, -60f),
                new Vector2(320f, 26f));

            _phaseText = CsHudTheme.CreateText("Phase", hud, string.Empty, 20, TextAnchor.MiddleCenter,
                CsHudTheme.Warn);
            CsHudTheme.PlaceTopCenter(_phaseText.rectTransform, new Vector2(0f, -84f),
                new Vector2(320f, 26f));

            _bombText = CsHudTheme.CreateText("Bomb", hud, string.Empty, 20, TextAnchor.MiddleCenter,
                CsHudTheme.Danger);
            CsHudTheme.PlaceTopCenter(_bombText.rectTransform, new Vector2(0f, -108f),
                new Vector2(420f, 26f));

            // ---- 左上：雷达（H6）+ 消息栏（无线电/系统提示，G13）----
            _radar = new CsRadarWidget();
            _radar.Build(hud);

            _messageTexts = new Text[MessageLines];
            for (var i = 0; i < _messageTexts.Length; i++)
            {
                var t = CsHudTheme.CreateText($"Message{i}", hud, string.Empty, 19, TextAnchor.MiddleLeft,
                    CsHudTheme.Message);
                CsHudTheme.PlaceTopLeft(t.rectTransform,
                    new Vector2(Margin, -CsHudTheme.RadarSize - 24f - i * MessageLineHeight),
                    new Vector2(760f, MessageLineHeight));
                _messageTexts[i] = t;
            }

            // ---- 中央：准星 / 命中标记（H7 / H9）----
            _crosshair = new CsCrosshairWidget();
            _crosshair.Build(root);

            // ---- 右上：击杀信息（H8）----
            _killFeed = new CsKillFeedWidget();
            _killFeed.Build(hud);

            // ---- 观战（H11）----
            _spectator = new CsSpectatorWidget();
            _spectator.Build(hud);

            // ---- 底部中央：Buy Zone（H10）+ 下包/拆包进度 ----
            _buyZoneText = CsHudTheme.CreateText("BuyZone", hud, "Buy Zone", 24, TextAnchor.MiddleCenter,
                CsHudTheme.HealthOk);
            CsHudTheme.PlaceBottomCenter(_buyZoneText.rectTransform, new Vector2(0f, 200f),
                new Vector2(300f, 30f));

            _useBar = CsHudTheme.CreateBar("UseBar", hud, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, 240f), new Vector2(320f, 16f), CsUiStyle.Accent);
            _useLabel = CsHudTheme.CreateText("UseLabel", hud, string.Empty, 20, TextAnchor.MiddleCenter,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceBottomCenter(_useLabel.rectTransform, new Vector2(0f, 264f),
                new Vector2(420f, 26f));

            // ---- 受伤提示（屏幕四边红 + 方向标记）----
            _damage = new CsDamageIndicatorWidget();
            _damage.Build(hud);

            // ---- 闪光弹全屏白（HUD 之上、弹窗之下）----
            var flash = UIFactory.CreatePanel("FlashOverlay", root, new Color(1f, 1f, 1f, 0f), false);
            UIFactory.Stretch(flash.rectTransform);
            _flashOverlay = flash;
        }

        /// <summary>
        /// 把右上角比分 / 回合计时块（3 个文本 + 1 条竖分隔线）按**原版 1920×1080 截图的像素坐标**定位，
        /// 并在缺失时补建竖分隔线。
        ///
        /// <para>出处与逐项实测值见 <see cref="CsHudTheme"/> 的"右上角比分 / 回合计时块"一节（对照表 V-01~V-03，
        /// 量测口径见 <c>原版资源/解包产物/原版HUD布局.md</c> §0.4/§0.5，本片重跑复核的原始输出在 agent-11 回报里）；
        /// 本单位参考分辨率也是 1920×1080 ⇒ **直接写原版像素、不换算**。</para>
        ///
        /// <para><b>为什么单独抽出来、并在 <see cref="OnOpen"/> 里再调一次</b>：HudPanel 的运行期布局来自
        /// **序列化的预制体**（<c>Resources/UI/HudPanel.prefab</c>，由 <c>Assets/Editor/UiGenInGame/UiBuilder</c>
        /// 调 <see cref="BuildLayout"/> 生成；<c>CsPanelBase.Awake</c> 见预制体已有子节点就跳过 BuildLayout）。
        /// 只改 BuildLayout 里的坐标/颜色，在**没重跑生成器**的工程里实机看到的仍是旧值（"代码改了但画面没变"
        /// 就是这条机制）。这两处调用同一份定位：重跑生成器后两者给出同样的值（幂等、不冲突），
        /// 没重跑时实机也已经是本片对齐后的结果。</para>
        /// </summary>
        private void ApplyScoreBlock()
        {
            // 行 1（Counter-Terrorists）：数字右边缘 x=1750 ⇒ 距屏右 170；ink 上边缘 y=38
            if (_scoreCTText != null)
            {
                CsHudTheme.PlaceTopRight(_scoreCTText.rectTransform,
                    new Vector2(-CsHudTheme.ScoreRightInsetPx, -CsHudTheme.ScoreCTTopPx),
                    new Vector2(CsHudTheme.ScoreBoxWidthPx, CsHudTheme.ScoreLineHeightPx));
                _scoreCTText.fontSize = CsHudTheme.ScoreFontSize;
                _scoreCTText.color = CsHudTheme.TextHud;
            }

            // 行 2（Terrorists）：与行 1 同一右边缘（原版两行数字右边缘都对齐在 1750）；ink 上边缘 y=65
            if (_scoreTText != null)
            {
                CsHudTheme.PlaceTopRight(_scoreTText.rectTransform,
                    new Vector2(-CsHudTheme.ScoreRightInsetPx, -CsHudTheme.ScoreTTopPx),
                    new Vector2(CsHudTheme.ScoreBoxWidthPx, CsHudTheme.ScoreLineHeightPx));
                _scoreTText.fontSize = CsHudTheme.ScoreFontSize;
                _scoreTText.color = CsHudTheme.TextHud;
            }

            // 回合计时：右边缘 x=1869 ⇒ 距屏右 51；与行 2 同一条 y（原版计时就在第 2 行右侧）
            if (_roundTimeText != null)
            {
                CsHudTheme.PlaceTopRight(_roundTimeText.rectTransform,
                    new Vector2(-CsHudTheme.ClockRightInsetPx, -CsHudTheme.ScoreTTopPx),
                    new Vector2(CsHudTheme.ClockBoxWidthPx, CsHudTheme.ScoreLineHeightPx));
                _roundTimeText.fontSize = CsHudTheme.ScoreFontSize;
                _roundTimeText.color = CsHudTheme.TextHud;
            }

            // `Map: <地图名>`：竖分隔线右侧、与比分行 1 同高（F-01）
            EnsureMapText();
            if (_mapText != null)
            {
                CsHudTheme.PlaceTopRight(_mapText.rectTransform,
                    new Vector2(-CsHudTheme.MapRightInsetPx, -CsHudTheme.MapTopPx),
                    new Vector2(CsHudTheme.ScoreBoxWidthPx, CsHudTheme.ScoreLineHeightPx));
                _mapText.fontSize = CsHudTheme.ScoreFontSize;
                _mapText.color = CsHudTheme.TextHud;
            }

            EnsureScoreDivider();
            if (_scoreDivider != null)
            {
                CsHudTheme.PlaceTopRight(_scoreDivider.rectTransform,
                    new Vector2(-CsHudTheme.ScoreDividerInsetPx, -CsHudTheme.ScoreDividerTopPx),
                    new Vector2(CsHudTheme.ScoreDividerWidthPx, CsHudTheme.ScoreDividerHeightPx));
                _scoreDivider.color = CsHudTheme.ScoreDivider;
            }

            // 秒表图标：框的上边缘 y=58、右边缘 x=1817（原版截图实测 ink bbox 的对应边）
            EnsureStopwatchIcon();
            if (_stopwatchIcon != null)
            {
                CsHudTheme.PlaceTopRight(_stopwatchIcon.rectTransform,
                    new Vector2(-CsHudTheme.StopwatchRightInsetPx, -CsHudTheme.StopwatchTopPx),
                    new Vector2(CsHudTheme.StopwatchSizePx, CsHudTheme.StopwatchSizePx));
            }
        }

        /// <summary>
        /// 秒表图标节点不存在时补建（**未重跑生成器的预制体里没有这个节点** —— 它是本片新增的元素）。
        /// 父节点与 <see cref="BuildLayout"/> 里一致（<c>_hudRoot</c>）：它属于"比赛停了就整组隐藏"的那批。
        /// </summary>
        private void EnsureStopwatchIcon()
        {
            if (_stopwatchIcon != null) return;

            var parent = _hudRoot != null ? _hudRoot : transform as RectTransform;
            if (parent == null)
            {
                Game.Logger?.Warn(Tag, "HUD 根节点不存在，秒表图标无法补建（不致命：仅少一个 24×24 的图标）");
                return;
            }

            _stopwatchIcon = CsHudTheme.CreateBlock("StopwatchIcon", parent,
                new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero,
                new Vector2(CsHudTheme.StopwatchSizePx, CsHudTheme.StopwatchSizePx), Color.white);
            _stopwatchIcon.enabled = false;   // sprite 未到手前不画（见 BuildLayout 的说明）
            Game.Logger?.Info(Tag,
                $"补建秒表图标 StopwatchIcon（{CsHudTheme.StopwatchSizePx:F0}×{CsHudTheme.StopwatchSizePx:F0} px，" +
                "原版精灵 hud.txt:127）—— HudPanel.prefab 里还没有该节点（本片改了布局、尚未重跑生成器）");
        }

        /// <summary>
        /// 把秒表图标的 sprite 绑上：编辑器的生成器在生成预制体时调一次（<c>UiBuilder</c>），
        /// 运行期由 <see cref="EnsureStopwatchSprite"/> 兜底。
        /// 传 null 不算成功：只报一次 Warn，绝不静默把图标吞掉。
        /// </summary>
        public void BindStopwatchSprite(Sprite sprite)
        {
            if (sprite == null)
            {
                if (!_warnedStopwatch)
                {
                    _warnedStopwatch = true;
                    Game.Logger?.Warn(Tag,
                        $"秒表图标 sprite 为空（Resources/{ResPaths.HudStopwatchIcon}.png 没导入成 Sprite？），图标不显示");
                }
                return;
            }

            EnsureStopwatchIcon();
            if (_stopwatchIcon == null) return;

            _stopwatchIcon.sprite = sprite;
            _stopwatchIcon.color = CsHudTheme.StopwatchTint;
            _stopwatchIcon.enabled = true;
        }

        /// <summary>
        /// 运行期兜底取 sprite：预制体里已经绑好就什么都不做（重跑生成器后的正常路径）。
        /// 只在需要时请求一次 —— 每帧去问资源模块是白烧。
        /// </summary>
        private void EnsureStopwatchSprite()
        {
            EnsureStopwatchIcon();
            if (_stopwatchIcon == null)
            {
                if (!_warnedStopwatch)
                {
                    _warnedStopwatch = true;
                    Game.Logger?.Warn(Tag, "秒表图标节点补建失败（HUD 根节点缺失），图标不显示");
                }
                return;
            }
            if (_stopwatchIcon.sprite != null) return;   // 生成器已绑好
            if (_stopwatchRequested) return;

            var res = Game.Res;
            if (res == null)
            {
                if (!_warnedStopwatch)
                {
                    _warnedStopwatch = true;
                    Game.Logger?.Error(Tag,
                        $"Game.Res 为 null（CloverRes.Init 未执行？），秒表图标加载不了：" +
                        $"Resources/{ResPaths.HudStopwatchIcon}");
                }
                return;
            }

            _stopwatchRequested = true;
            var path = ResPaths.HudStopwatchIcon;
            res.LoadAsset<Sprite>(path, s =>
            {
                if (s == null)
                {
                    // 允许下一次 OnOpen 再试（资源还没导入 / 首次加载失败不该把图标永久废掉）
                    _stopwatchRequested = false;
                    if (!_warnedStopwatch)
                    {
                        _warnedStopwatch = true;
                        Game.Logger?.Warn(Tag, $"秒表图标加载失败（sprite 为空）：Resources/{path}");
                    }
                    return;
                }
                BindStopwatchSprite(s);
            });
        }

        // ═══════════════════════ 血量 / 护甲 位图图标 ═══════════════════════

        /// <summary>
        /// 编辑器生成器在建预制体时调一次：把三个**原版图标 sprite** 绑进来
        /// （<c>Resources/UI/Art/hud_{cross,suit_full,suithelmet_full}.png</c>，
        /// 由 <c>原版资源/解包产物/cs16_asset_extract.py</c> 从本体 <c>640hud7.spr</c> 解出）。
        /// 运行期没有 AssetDatabase，所以引用必须在编辑器里绑进预制体。
        /// 传 null 不算成功：只报一次 Warn，绝不静默把图标吞掉。
        /// </summary>
        public void BindHudIconSprites(Sprite health, Sprite armor, Sprite armorHelmet)
        {
            if (health == null || armor == null || armorHelmet == null)
            {
                WarnHudIconsOnce("有一项 sprite 为空（Resources/UI/Art/hud_*.png 没导入成 Sprite？），HUD 图标不显示");
                return;
            }
            _iconHealth = health;
            _iconArmor = armor;
            _iconArmorHelmet = armorHelmet;
            ApplyHudIcons(CsHudSnapshot.HasHelmet, CsHudSnapshot.Armor > 0);
            Game.Logger?.Info(Tag,
                $"已绑定 HUD 位图图标 sprite：血量={health.name}({health.rect.width}×{health.rect.height}) " +
                $"护甲={armor.name} 护甲+头盔={armorHelmet.name}（原版 hud.txt:121/135/137，640hud7.spr）");
        }

        /// <summary>运行期兜底取 sprite：预制体里已经绑好就什么都不做；只在需要时请求一次。</summary>
        private void EnsureHudIconSprites()
        {
            if (_iconHealth != null && _iconArmor != null && _iconArmorHelmet != null) return;
            if (_hudIconsRequested) return;

            var res = Game.Res;
            if (res == null)
            {
                WarnHudIconsOnce("Game.Res 为 null（CloverRes.Init 未执行？），HUD 位图图标加载不了");
                return;
            }

            _hudIconsRequested = true;
            res.LoadAsset<Sprite>(ResPaths.HudHealthIcon, s =>
            {
                if (s == null) { _hudIconsRequested = false; WarnHudIconsOnce($"HUD 血量图标加载失败：Resources/{ResPaths.HudHealthIcon}"); }
                else _iconHealth = s;
            });
            res.LoadAsset<Sprite>(ResPaths.HudArmorIcon, s => { if (s != null) _iconArmor = s; });
            res.LoadAsset<Sprite>(ResPaths.HudArmorHelmetIcon, s => { if (s != null) _iconArmorHelmet = s; });
        }

        /// <summary>
        /// 把两个图标按当前状态刷上去：sprite / 着色 / 显隐。
        /// <b>sprite 没到手时保持 <c>enabled = false</c></b> —— 没有 sprite 的 uGUI <c>Image</c>
        /// 会被画成一块实心白块（<c>Graphic.OnPopulateMesh</c> 的实心分支），比"暂时没有图标"难看得多。
        /// </summary>
        private void ApplyHudIcons(bool hasHelmet, bool armorHasValue)
        {
            if (_healthIcon != null && _iconHealth != null)
            {
                if (_healthIcon.sprite != _iconHealth) _healthIcon.sprite = _iconHealth;
                _healthIcon.color = CsHudTheme.HudIconTint;
                _healthIcon.enabled = true;
            }

            if (_armorIcon != null)
            {
                var want = hasHelmet ? _iconArmorHelmet : _iconArmor;   // 有头盔 / 无头盔 = 换 sprite（规格 H1）
                if (want == null)
                {
                    if (_armorIcon.enabled) _armorIcon.enabled = false;
                }
                else
                {
                    if (_armorIcon.sprite != want) _armorIcon.sprite = want;
                    _armorIcon.color = armorHasValue ? CsHudTheme.HudIconTint : CsHudTheme.Disabled;
                    _armorIcon.enabled = true;
                }
            }

            LogHudIconsOnce(hasHelmet);
        }

        /// <summary>
        /// 图标就绪的运行时自证行（数值类证据）：sprite 名 / 目标尺寸 / 显隐 / 是否换了头盔版。
        /// 只在"血量 sprite 确实到手"之后打一次（否则会打一堆 null）。
        /// </summary>
        private void LogHudIconsOnce(bool hasHelmet)
        {
            if (_loggedHudIcons || _healthIcon == null || _iconHealth == null) return;
            if (_armorIcon == null || _armorIcon.sprite == null) return;
            _loggedHudIcons = true;

            Game.Logger?.Info(Tag,
                $"HUD 位图图标就绪：血量 sprite={_iconHealth.name}({_iconHealth.rect.width}×{_iconHealth.rect.height}) " +
                $"enabled={_healthIcon.enabled} pos={_healthIcon.rectTransform.anchoredPosition} " +
                $"size={_healthIcon.rectTransform.sizeDelta} | 护甲 sprite={_armorIcon.sprite.name} " +
                $"enabled={_armorIcon.enabled} hasHelmet={hasHelmet} —— 原版 640hud7 的 cross/suit_full/suithelmet_full，" +
                "⛔ 已不再是字体字形 ♥/🛡/+");
        }

        private void WarnHudIconsOnce(string msg)
        {
            if (_warnedHudIcons) return;
            _warnedHudIcons = true;
            Game.Logger?.Warn(Tag, msg);
        }

        /// <summary>
        /// <c>Map: &lt;地图名&gt;</c> 文本不存在时补建（**未重跑生成器的预制体里没有这个节点** —— 本片新增的元素）。
        /// 父节点与 <see cref="BuildLayout"/> 里一致（<c>_hudRoot</c>）：它属于"比赛停了就整组隐藏"的那批。
        /// </summary>
        private void EnsureMapText()
        {
            if (_mapText != null) return;

            var parent = _hudRoot != null ? _hudRoot : transform as RectTransform;
            if (parent == null)
            {
                Game.Logger?.Warn(Tag, "HUD 根节点不存在，`Map:` 文本无法补建（不致命：仅少一行地图名）");
                return;
            }

            _mapText = CsHudTheme.CreateText("MapName", parent,
                CsHudTheme.MapLabelPrefix + CsConst.MapDust2,
                CsHudTheme.ScoreFontSize, TextAnchor.MiddleRight, CsHudTheme.TextHud);
            Game.Logger?.Info(Tag,
                "补建 `Map: <地图名>` 文本（MapName）—— HudPanel.prefab 里还没有该节点（本片改了布局、尚未重跑生成器）");
        }

        /// <summary>
        /// 竖分隔线不存在时补建（**未重跑生成器的预制体里没有这个节点** —— 它是本片新增的元素）。
        /// 父节点与 <see cref="BuildLayout"/> 里一致（<c>_hudRoot</c>）：它属于"比赛停了就整组隐藏"的那批。
        /// </summary>
        private void EnsureScoreDivider()
        {
            if (_scoreDivider != null) return;

            var parent = _hudRoot != null ? _hudRoot : transform as RectTransform;
            if (parent == null)
            {
                Game.Logger?.Warn(Tag, "HUD 根节点不存在，比分竖分隔线无法补建（不致命：仅少一条 2×67 px 的线）");
                return;
            }

            _scoreDivider = CsHudTheme.CreateBlock("ScoreDivider", parent,
                new Vector2(1f, 1f), new Vector2(1f, 1f), Vector2.zero,
                new Vector2(CsHudTheme.ScoreDividerWidthPx, CsHudTheme.ScoreDividerHeightPx),
                CsHudTheme.ScoreDivider);
            Game.Logger?.Info(Tag,
                $"补建比分竖分隔线 ScoreDivider（{CsHudTheme.ScoreDividerWidthPx:F0}×{CsHudTheme.ScoreDividerHeightPx:F0} px）" +
                "—— HudPanel.prefab 里还没有该节点（本片改了布局、尚未重跑生成器）");
        }

        // ═══════════════════════ 生命周期 ═══════════════════════

        public override void OnOpen(object param)
        {
            WarnIfNull(_healthText, "血量文本");
            WarnIfNull(_ammoText, "弹药文本");
            WarnIfNull(_flashOverlay, "闪光弹遮罩");

            // 本片（agent-11）按原版 1920×1080 截图对齐的几件，在每次打开时重放一次：
            // 预制体里序列化的是"生成时的旧坐标/旧颜色"，只改 BuildLayout 的话实机看不到（见 ApplyScoreBlock 注释）。
            // 重跑生成器后 BuildLayout 与这里给出同一份值，幂等。
            ApplyScoreBlock();
            // 秒表图标的 sprite：生成器生成预制体时会直接绑进去；老预制体（没重跑生成器）走这里兜底。
            EnsureStopwatchSprite();
            _crosshair?.ApplyColor(CsHudTheme.Crosshair);

            _selfName = CsPlayerSettingsStore.Load().PlayerName;

            // AppFlow 在"从暂停恢复"时会对已开着的 HUD 再调一次 Open → 这里必须幂等，
            // 否则重复订阅 = 一次回合结束弹两次面板（同一条事件被处理两次）。
            if (_subscribed)
            {
                RefreshImmediate();
                return;
            }

            var bus = Game.Event;
            if (bus == null)
            {
                // 不置 _subscribed：下次再 Open 还会尝试订阅（一次失败不该把 HUD 永久废掉）
                Game.Logger?.Error(Tag, "Game.Event 为空（引擎未 Launch？），HUD 无法订阅回合/比赛事件，结算面板不会弹");
                return;
            }

            // 先订阅成功再置位：失败时保留重试机会（event bus 订阅失败不会抛异常，只会静默不回调）
            _subscribed = true;
            bus.On(Events.MatchStarted, OnMatchStarted);
            bus.On<int>(Events.RoundStarted, OnRoundStarted);
            bus.On<CsRoundEndReason, CsTeam>(Events.RoundEnded, OnRoundEnded);
            bus.On(Events.MatchEnded, OnMatchEnded);
            bus.On<string>(Events.GameMessage, OnGameMessage);

            Game.Logger?.Info(Tag, "HUD 已打开（游戏内常驻），已订阅 MatchStarted/RoundStarted/RoundEnded/MatchEnded/GameMessage");
        }

        public override void OnClose()
        {
            var bus = Game.Event;
            if (bus != null && _subscribed)
            {
                bus.Off(Events.MatchStarted, OnMatchStarted);
                bus.Off<int>(Events.RoundStarted, OnRoundStarted);
                bus.Off<CsRoundEndReason, CsTeam>(Events.RoundEnded, OnRoundEnded);
                bus.Off(Events.MatchEnded, OnMatchEnded);
                bus.Off<string>(Events.GameMessage, OnGameMessage);
            }
            _subscribed = false;

            Game.Logger?.Info(Tag, "HUD 已关闭（回合内界面上的临时面板由 AppFlow 的 CloseAll 一并收掉）");
        }

        // ═══════════════════════ 每帧刷新 ═══════════════════════

        public override void OnUpdate(float dt)
        {
            if (!CsHudSnapshot.Valid)
            {
                if (_visible)
                {
                    _visible = false;
                    SetHudVisible(false);
                    _stats.Reset("比赛停止或回主菜单（快照 Valid=false）");
                }
                // 比赛已停：把还在屏上的临时面板也收掉（否则会停在"回合结算"画面上没退路）
                CloseIngameOverlays();
                return;
            }

            if (!_visible)
            {
                _visible = true;
                SetHudVisible(true);
                RefreshImmediate();
            }

            RefreshValues();
            RefreshWidgets();
            HandleInput();
        }

        /// <summary>打开时先把整屏刷新一次，避免第一帧显示上一局的残留字。</summary>
        private void RefreshImmediate()
        {
            if (!CsHudSnapshot.Valid)
            {
                SetHudVisible(false);
                return;
            }
            SetHudVisible(true);
            RefreshValues();
            RefreshWidgets();
        }

        private void SetHudVisible(bool visible)
        {
            if (_hudRoot != null && _hudRoot.gameObject.activeSelf != visible)
                _hudRoot.gameObject.SetActive(visible);
            if (_flashOverlay != null && _flashOverlay.gameObject.activeSelf != visible)
                _flashOverlay.gameObject.SetActive(visible);

            // 准星挂在面板根上（不在 _hudRoot 里，这样它能压在 HUD 文字之上），隐藏时必须单独收掉
            if (!visible) _crosshair?.Refresh(false, 0f, 0f, false);
        }

        private void RefreshValues()
        {
            WarnRefsOnce();

            var hp = CsHudSnapshot.Health;
            var armor = CsHudSnapshot.Armor;
            var alive = CsHudSnapshot.IsAlive;

            // ---- 血量 / 护甲（H1）----
            // 口径：数字文本只放数字；图标是**独立的原版位图精灵**（hud.txt:121/135/137），
            // "有头盔 / 无头盔"的差别体现在**换 sprite**（suit_full ↔ suithelmet_full），不再是字形后缀 "+"。
            if (_healthText != null)
            {
                _healthText.text = hp.ToString();
                _healthText.color = CsHudTheme.HealthColor(hp);
            }
            if (_healthBar != null)
            {
                _healthBar.Set(Mathf.Clamp01(hp / (float)CsConst.MaxHealth));
                _healthBar.SetColor(CsHudTheme.HealthColor(hp));
            }
            if (_armorText != null)
            {
                _armorText.text = armor.ToString();
                _armorText.color = armor > 0 ? CsHudTheme.ArmorColor : CsHudTheme.Disabled;
            }
            if (_armorBar != null)
            {
                _armorBar.Set(Mathf.Clamp01(armor / (float)CsConst.MaxArmor));
                _armorBar.SetColor(armor > 0 ? CsHudTheme.ArmorColor : CsHudTheme.Disabled);
            }
            EnsureHudIconSprites();
            ApplyHudIcons(CsHudSnapshot.HasHelmet, armor > 0);

            // ---- 金钱（H2）：可以买东西时高亮 ----
            if (_moneyText != null)
            {
                _moneyText.text = $"${CsHudSnapshot.Money}";
                _moneyText.color = CsHudSnapshot.CanBuyNow ? CsHudTheme.MoneyCanBuy : CsHudTheme.MoneyNormal;
            }

            // ---- 弹药 / 武器名（H3）：死亡时不显示（CS 1.6 观战时不显示自己的枪械信息）----
            if (_ammoText != null)
            {
                _ammoText.text = alive ? CsHudTheme.AmmoText(CsHudSnapshot.Mag, CsHudSnapshot.Reserve) : string.Empty;
                _ammoText.color = CsHudTheme.TextHud;
            }
            if (_weaponText != null)
            {
                _weaponText.text = alive ? (CsHudSnapshot.WeaponName ?? string.Empty) : string.Empty;
            }

            // ---- 比分 / 回合 / 时间（H4 / H5）----
            // 文案按原版截图逐字对齐（"Counter-Terrorists : 0" / "Terrorists : 0"，见 CsHudTheme 比分块一节）
            if (_scoreCTText != null) _scoreCTText.text = $"Counter-Terrorists : {CsHudSnapshot.ScoreCT}";
            if (_scoreTText != null) _scoreTText.text = $"Terrorists : {CsHudSnapshot.ScoreT}";
            if (_roundTimeText != null) _roundTimeText.text = CsHudTheme.FormatClock(CsHudSnapshot.PhaseTimeLeft);

            // ---- `Map: <地图名>`（F-01；原版在右上角竖分隔线右侧）----
            if (_mapText != null)
            {
                var mapName = (Game.Map != null && !string.IsNullOrEmpty(Game.Map.Name))
                    ? Game.Map.Name
                    : CsConst.MapDust2;
                var content = CsHudTheme.MapLabelPrefix + mapName;
                if (_mapText.text != content) _mapText.text = content;
                LogMapTextOnce(mapName);
            }
            if (_roundNumberText != null) _roundNumberText.text = $"第 {CsHudSnapshot.RoundNumber} 回合";
            if (_phaseText != null) _phaseText.text = CsHudTheme.PhaseText(CsHudSnapshot.Phase);

            // ---- 炸弹（G7：下包后剩余时间）----
            if (_bombText != null)
            {
                if (CsHudSnapshot.BombPlanted && CsHudSnapshot.BombTimeLeft >= 0f)
                    _bombText.text = $"\u2605 C4 已安放 {CsHudSnapshot.BombTimeLeft:0.0}s";
                else
                    _bombText.text = string.Empty;
            }

            // ---- Buy Zone（H10）----
            if (_buyZoneText != null)
            {
                var inZone = CsHudSnapshot.InBuyZone;
                if (_buyZoneText.gameObject.activeSelf != inZone) _buyZoneText.gameObject.SetActive(inZone);
                if (inZone)
                    _buyZoneText.text = CsHudSnapshot.CanBuyNow ? "Buy Zone（按 B 买枪）" : "Buy Zone";
            }

            // ---- 下包 / 拆包进度 ----
            var using01 = CsHudSnapshot.UseProgress;
            var useVisible = using01 >= 0f;
            if (_useBar != null)
            {
                _useBar.SetActive(useVisible);
                _useBar.Set(Mathf.Clamp01(using01));
            }
            if (_useLabel != null)
            {
                if (_useLabel.gameObject.activeSelf != useVisible) _useLabel.gameObject.SetActive(useVisible);
                if (useVisible)
                    _useLabel.text = CsHudSnapshot.Team == CsTeam.T
                        ? $"下包中… {using01 * 100f:0}%"
                        : $"拆包中… {using01 * 100f:0}%";
            }

            // ---- 流光消息栏的过期（按本地时钟算，不依赖快照的 BornTime 时钟）----
            RefreshMessages();

            // ---- 记分板本地统计对齐 ----
            // 玩家名只在打开/开新局时读一次设置（每帧去读 9 个 Setting 键是白烧 CPU）
            _stats.Observe();
            _stats.SetSelf(_selfName, CsHudSnapshot.Team, alive, CsHudSnapshot.Money);
        }

        /// <summary>
        /// `Map:` 行的运行时自证行（F-01 的数值类证据）：元素内容 / 是否上屏 / 字号 / 颜色 / 落点。
        /// 只在首次、或地图名变化时打（不刷屏）。位置的对错是表现类判据（并排看基线图），数值只证明"元素在、内容对"。
        /// </summary>
        private void LogMapTextOnce(string mapName)
        {
            if (_loggedMapName == mapName) return;
            _loggedMapName = mapName;

            var rt = _mapText.rectTransform;
            Game.Logger?.Info(Tag,
                $"[MapText] text='{_mapText.text}' activeSelf={_mapText.gameObject.activeSelf} " +
                $"enabled={_mapText.enabled} fontSize={_mapText.fontSize} " +
                $"color=#{ColorUtility.ToHtmlStringRGB(_mapText.color)} " +
                $"anchor={rt.anchorMin}/{rt.anchorMax} pivot={rt.pivot} anchoredPosition={rt.anchoredPosition} " +
                $"sizeDelta={rt.sizeDelta}（原版：竖分隔线右侧、与比分行 1 同高）");
        }

        private void RefreshWidgets()
        {
            var alive = CsHudSnapshot.IsAlive;
            var overlayOpen = IsAnyOverlayOpen();

            _crosshair?.Refresh(alive && !CsHudSnapshot.IsZoomed && !overlayOpen,
                CsHudSnapshot.CrosshairSpread, CsHudSnapshot.HitMarkerTime, CsHudSnapshot.HitMarkerHeadshot);

            UpdateRadarBounds();
            _radar?.Refresh(true, _minX, _maxX, _minZ, _maxZ, CsHudSnapshot.Radar);
            _killFeed?.Refresh(true, CsHudSnapshot.KillFeed);
            _spectator?.Refresh(CsHudSnapshot.IsSpectating, CsHudSnapshot.SpectateName);
            _damage?.Refresh(CsHudSnapshot.DamageIndicatorTime, CsHudSnapshot.DamageFromYaw);

            if (_flashOverlay != null)
            {
                var a = Mathf.Clamp01(CsHudSnapshot.FlashAlpha);
                var c = _flashOverlay.color;
                if (Mathf.Abs(c.a - a) > 0.001f)
                {
                    c.a = a;
                    _flashOverlay.color = c;
                }
                _flashOverlay.raycastTarget = false;
            }
        }

        // ⛔ 切片N 下架：这里原有 ObserveLocalDamage / ShowDamageNumber 一对方法，把"本地掉血"
        //    渲染成屏幕上的 `-<数字>` 飘字（Game.UI.FloatText），时长取 CsConst.DamageNumberTime。
        //    它们**并非 A（CS 1.6）的行为** —— 原版 HUD 里没有"伤害数字"这一项（依据：`策划/对照表.md`
        //    §4 把原版 HUD 元素逐条出处化（U-01~U-37，引到 `hud.txt:110~183`），其中只有 hitmarker
        //    （`hud.txt:179` 的 `d_headshot`）与击杀条，**没有伤害数字**；`策划/验收表.md` B 段我方 HUD
        //    项清单与 `client/资源欠缺清单.md` 的"A 有/我方缺"对账里同样没有它）。
        //    ⚠️ 原版硬载体（`cstrike/sprites/hud.txt` / `原版资源/解包产物/`）本机不在盘
        //    （`原版资源/清单.md` 实测为空）⇒ 拿不到 hud.txt 原文级直证；故本条按「本项目新增、
        //    与原版无关」登记在 `策划/差异登记.tsv`，并在此按 skill §0 铁律 1「A 没有 ⇒ 不加」整链删除。
        //    受击的**方向**反馈仍在（屏幕边缘红框 = CsDamageIndicatorWidget，见下面 RefreshWidgets
        //    里 `_damage?.Refresh(...)` 那一行），其时长常量已改名 CsConst.DamageIndicatorTime。
        //    若要恢复，请先给出原版出处的 file:line（当前载体里没有）。

        private void UpdateRadarBounds()
        {
            // 地图边界是引擎 API（Game.Map = IMapData），不是业务 Module —— UI 可以用。
            // 加载完成后就不再重复算（每帧算一遍没有意义）。
            if (_haveBounds) return;

            var map = Game.Map;
            if (map == null || !map.Loaded || map.Width <= 0 || map.Depth <= 0 || map.CellSize <= 0f) return;

            _minX = map.Origin.x;
            _maxX = map.Origin.x + map.Width * map.CellSize;
            _minZ = map.Origin.z;
            _maxZ = map.Origin.z + map.Depth * map.CellSize;
            _haveBounds = true;
        }

        // ═══════════════════════ 消息栏 ═══════════════════════

        /// <summary>
        /// 把一条消息交给 HUD 消息栏（游戏内各面板的统一回执入口）。
        /// HUD 不在（异常情况）时退化为顶部 Toast —— 反馈不能因为"面板没开"就静默消失。
        /// </summary>
        public static void Notify(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var hud = Game.UI?.Get<HudPanel>();
            if (hud != null)
            {
                hud.PushMessage(text);
                return;
            }

            Game.UI?.Toast(text, 2.5f);
        }

        /// <summary>
        /// 供记分板取"逐人统计"。
        ///
        /// <para>统计累加器挂在常驻的 HUD 上而不是记分板自己身上：记分板只在按住 TAB 时才存在，
        /// 而击杀是随时发生的 —— 让临时面板累加统计，等于"没按住 TAB 的那几秒击杀全丢"。</para>
        /// </summary>
        public void BuildScoreRows(System.Collections.Generic.List<CsScoreRow> into) => _stats.Build(into);

        /// <summary>本地玩家名（记分板自己那行的高亮依据）。</summary>
        public string SelfName => _selfName;

        /// <summary>
        /// 往 HUD 消息栏推一条（无线电 / 系统提示 / 本工程各面板的操作回执）。
        /// </summary>
        public void PushMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            // 整体上移一行，最新一条放最上面（CS 1.6 的消息栏就是这个方向）
            for (var i = _messageUntil.Length - 1; i > 0; i--)
            {
                _messageCache[i] = _messageCache[i - 1];
                _messageUntil[i] = _messageUntil[i - 1];
            }
            _messageCache[0] = text;
            _messageUntil[0] = Time.time + CsHudTheme.MessageLifetime;
            RefreshMessages();
        }

        private void RefreshMessages()
        {
            if (_messageTexts == null) return;

            var now = Time.time;
            for (var i = 0; i < _messageTexts.Length; i++)
            {
                var text = _messageTexts[i];
                if (text == null) continue;

                var alive = i < _messageUntil.Length && _messageUntil[i] > now;
                var content = alive ? _messageCache[i] : string.Empty;
                if (text.text != content) text.text = content;
            }
        }

        // ═══════════════════════ 按键路由 ═══════════════════════

        private void HandleInput()
        {
            var input = Game.Input;
            if (input == null)
            {
                Game.Logger?.Error(Tag, "Game.Input 为空（CloverInput.Init 未调用？），B/H/TAB/无线电/控制台都唤不出来");
                return;
            }

            // 比赛结束面板开着时一切都归它（它要玩家做选择）
            if (Game.UI.IsOpen<MatchEndPanel>()) return;

            if (input.GetKeyDown(GameKey.Slash) || input.GetKeyDown(GameKey.Num0))
            {
                // GameKey 里没有 BackQuote（`）—— 契约缺口，主 agent 已知。
                // 这里同时接受 `/` 与 `0`：前者是 CS 里的控制台键位习惯，后者在没有 `/` 的键盘上是兜底。
                if (!Game.UI.IsOpen<ConsolePanel>())
                {
                    Game.Logger?.Info(Tag, "按键唤出控制台（GameKey.Slash / GameKey.Num0；GameKey 无 BackQuote）");
                    Game.UI.Open<ConsolePanel>();
                }
                return;
            }

            // TAB：按住显示记分板（G11）
            var tabHeld = input.GetKey(GameKey.Tab);
            if (tabHeld && !Game.UI.IsOpen<ScoreboardPanel>())
            {
                Game.UI.Open<ScoreboardPanel>();
            }
            else if (!tabHeld && Game.UI.IsOpen<ScoreboardPanel>())
            {
                Game.UI.Close<ScoreboardPanel>();
            }

            // 有弹窗类面板开着时，剩下的开面板键归它们（买枪菜单要收 1~8、H 菜单要收按钮）
            if (IsBlockingOverlayOpen()) return;

            if (input.GetKeyDown(GameKey.B))
            {
                Game.Logger?.Info(Tag, "按键唤出买枪菜单（B）");
                Game.UI.Open<BuyMenuPanel>();
            }
            else if (input.GetKeyDown(GameKey.H))
            {
                Game.Logger?.Info(Tag, "按键唤出 H 菜单（H）");
                Game.UI.Open<HMenuPanel>();
            }
            else if (input.GetKeyDown(GameKey.Z))
            {
                Game.UI.Open<RadioMenuPanel>(CsRadioGroup.A);
            }
            else if (input.GetKeyDown(GameKey.X))
            {
                Game.UI.Open<RadioMenuPanel>(CsRadioGroup.B);
            }
            else if (input.GetKeyDown(GameKey.C))
            {
                Game.UI.Open<RadioMenuPanel>(CsRadioGroup.C);
            }
        }

        private static bool IsBlockingOverlayOpen()
        {
            var ui = Game.UI;
            if (ui == null) return false;
            return ui.IsOpen<BuyMenuPanel>() || ui.IsOpen<HMenuPanel>() || ui.IsOpen<RadioMenuPanel>()
                   || ui.IsOpen<ConsolePanel>();
        }

        private static bool IsAnyOverlayOpen()
        {
            var ui = Game.UI;
            if (ui == null) return false;
            return ui.IsOpen<BuyMenuPanel>() || ui.IsOpen<HMenuPanel>() || ui.IsOpen<RadioMenuPanel>()
                   || ui.IsOpen<ConsolePanel>() || ui.IsOpen<MatchEndPanel>() || ui.IsOpen<ScoreboardPanel>();
        }

        private static void CloseIngameOverlays()
        {
            var ui = Game.UI;
            if (ui == null) return;
            ui.Close<BuyMenuPanel>();
            ui.Close<HMenuPanel>();
            ui.Close<RadioMenuPanel>();
            ui.Close<ConsolePanel>();
            ui.Close<ScoreboardPanel>();
            ui.Close<RoundEndPanel>();
            ui.Close<MatchEndPanel>();
        }

        // ═══════════════════════ 事件 ═══════════════════════

        private void OnMatchStarted()
        {
            _stats.Reset("新比赛开始");
            ClearMessages();
        }

        private void OnRoundStarted(int roundNumber)
        {
            // 回合结算面板只在回合结算期显示（下一回合开始就收）
            Game.UI?.Close<RoundEndPanel>();
            _stats.BeginRound();
        }

        private void OnRoundEnded(CsRoundEndReason reason, CsTeam winner)
        {
            // 比赛已结束就不弹回合结算（MatchEnd 面板的信息量更全，两个一起弹会互相盖）
            if (CsHudSnapshot.Phase == CsRoundPhase.MatchEnd) return;

            var args = new CsRoundEndArgs
            {
                Reason = reason,
                Winner = winner,
                ScoreT = CsHudSnapshot.ScoreT,
                ScoreCT = CsHudSnapshot.ScoreCT,
                RoundNumber = CsHudSnapshot.RoundNumber,
            };
            Game.UI.Open<RoundEndPanel>(args);
        }

        private void OnMatchEnded()
        {
            // 比赛结束：先把图内的一次性面板收掉，再弹最终结算（它是 Popup，会吸走输入）
            Game.UI?.Close<RoundEndPanel>();
            Game.UI?.Close<BuyMenuPanel>();
            Game.UI?.Close<HMenuPanel>();
            Game.UI?.Close<RadioMenuPanel>();
            Game.UI.Open<MatchEndPanel>();
        }

        private void OnGameMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            PushMessage(text);
        }

        private void ClearMessages()
        {
            for (var i = 0; i < _messageUntil.Length; i++)
            {
                _messageUntil[i] = 0f;
                _messageCache[i] = null;
            }
            RefreshMessages();
        }

        // ═══════════════════════ 自检 ═══════════════════════

        private void WarnRefsOnce()
        {
            if (_warnedRefs) return;
            _warnedRefs = true;

            if (_healthText == null || _moneyText == null || _ammoText == null || _roundTimeText == null ||
                _scoreCTText == null || _scoreTText == null)
            {
                Game.Logger?.Error(Tag,
                    "HudPanel 关键文本引用缺失（预制体未由 Clover/CS16/生成游戏内面板 生成，或已被改动）——" +
                    "运行期会兜底重搭布局，但请重新执行生成器");
            }
        }
    }
}
