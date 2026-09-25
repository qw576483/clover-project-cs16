using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 买枪菜单（原版 CS 1.6 的 <c>Resource/UI</c> 买枪面板）。
    ///
    /// <para><b>载体（几何与文字）</b>：原版客户端的 VGUI 面板定义
    /// <c>cstrike/resource/UI/{BuyMenu,MainBuyMenu,Buy*_{CT,TER}}.res</c> +
    /// <c>cstrike/resource/cstrike_english.txt</c>（分类标签令牌的英文串）。
    /// 本文件的每个布局常量都逐字段搬自它们，出处写在常量行的注释里
    /// （本机副本：<c>原版资源/cs16src/cstrike/resource/UI/</c>）。</para>
    ///
    /// <para><b>设计空间与换算比</b>：原版这些面板的坐标写在 <b>640×480</b> 设计空间里
    /// （<c>BuyMenu.res</c> 的 <c>BuySubMenu wide 640 tall 480</c>）；本工程画布基准是
    /// 1920×1080、<c>scaleFactor = 1</c> ⇒ 换算比 = 1080/480 = <b>2.25</b>（等比，见
    /// <see cref="Scale"/>）。字号不换算：原版 VGUI 字号按屏幕高分段给绝对值
    /// （<c>clientscheme.res</c> 的 <c>yres</c> 段），1080 落在 <c>1024 1199</c> 段。</para>
    ///
    /// <para><b>两页</b>：与原版一致 —— 分类页（<c>MainBuyMenu.res</c>）列出 1~8 分类 +
    /// 关闭，武器页（<c>Buy*.res</c>）列出该分类的武器 + 关闭，右侧信息块显示鼠标所指武器的
    /// 原版图标（<c>gfx/vgui/*.tga</c>）与名称 / 价格。</para>
    ///
    /// <para><b>武器数据一律来自 <see cref="CsWeapons"/></b>（<c>BuyableByClass(cls, team)</c> +
    /// <c>CsWeaponDef.Price/DisplayName</c>）—— 界面上没有任何硬编码的价格或武器名。</para>
    ///
    /// <para><b>分类口径</b>：手枪 / 霰弹 / 冲锋 / 步枪（含狙击）/ 机枪 / 装备（含手雷），
    /// 顺序与标签取自原版分类页。<b>买不起 / 买不了</b>：买不起的条目变灰且不可点（规格 G3）；
    /// 不在买枪区 / 不在买枪时间时，底部红字提示并且点击会被本地拦下（不发事件、给出原因）。</para>
    /// </summary>
    public class BuyMenuPanel : CsPanelBase
    {
        // ═══════════════════════ 设计空间与换算 ═══════════════════════

        /// <summary>原版设计分辨率宽（出处 <c>BuyMenu.res</c> 的 <c>BuySubMenu wide 640</c>）。</summary>
        private const float DesignWidth = 640f;

        /// <summary>原版设计分辨率高（出处 <c>BuyMenu.res</c> 的 <c>BuySubMenu tall 480</c>）。</summary>
        private const float DesignHeight = 480f;

        /// <summary>
        /// 设计空间 → 画布像素的换算比 = 画布高 / 设计高 = <b>2.25</b>（画布 = 1920×1080、<c>scaleFactor = 1</c>）。
        /// 等比换算（不是每轴各自拉满）：原版图标 <c>gfx/vgui/*.tga</c> 是 256×64 / 256×128 的实拍画布，
        /// 与 <c>.res</c> 声明的 <c>ItemInfo</c> 框共用同一个 640×480 像素栅格 ⇒ 只有等比缩放才不把它们压扁。
        /// </summary>
        private const float Scale = 1080f / DesignHeight;

        /// <summary>面板自身在设计空间的位置与尺寸（出处 <c>BuyPistols_CT.res:7-10</c> 的 <c>WizardSubPanel</c>）。</summary>
        private const float PanelX = 50f;
        private const float PanelY = 10f;
        private const float PanelWidth = 552f;
        private const float PanelHeight = 448f;

        /// <summary>标题框（出处 <c>MainBuyMenu.res:20-24</c> / <c>BuyPistols_CT.res:20-24</c>：<c>xpos 76 ypos 22 wide 500 tall 48</c>）。</summary>
        private const float TitleX = 76f;
        private const float TitleY = 22f;
        private const float TitleWidth = 500f;
        private const float TitleHeight = 48f;

        /// <summary>分类页的 `SHOP BY CATEGORY` 提示（出处 <c>MainBuyMenu.res:40-44</c>：<c>xpos 84 ypos 87 wide 160 tall 24</c>）。</summary>
        private const float HintX = 84f;
        private const float HintY = 87f;
        private const float HintWidth = 160f;
        private const float HintHeight = 24f;

        /// <summary>列表行（分类行与武器行同一个栅格）：<c>xpos 76 wide 148 tall 20</c>，出处 <c>MainBuyMenu.res:58-61</c>。</summary>
        private const float RowX = 76f;
        private const float RowWidth = 148f;
        private const float RowHeight = 20f;

        /// <summary>第一行的 <c>ypos</c>（出处 <c>MainBuyMenu.res:59</c> 的 <c>pistols ypos 116</c>）。</summary>
        private const float RowFirstY = 116f;

        /// <summary>行距 = 32（出处 <c>MainBuyMenu.res</c> 的 116 → 148 → 180 → 212 → 244）。</summary>
        private const float RowPitch = 32f;

        /// <summary>武器页的行槽位数（原版装备页 8 条：<c>ypos</c> 116…340，出处 <c>BuyEquipment_CT.res</c>）。</summary>
        private const int ItemSlots = 8;

        /// <summary>关闭行 <c>ypos</c>（出处 <c>MainBuyMenu.res:220</c> / <c>BuyPistols_CT.res:164</c>：<c>CancelButton ypos 380</c>）。</summary>
        private const float CancelY = 380f;

        /// <summary>右侧武器信息块左上角（出处 <c>BuyPistols_CT.res:43-46</c>：<c>ItemInfo xpos 244 ypos 116</c>）。</summary>
        private const float InfoX = 244f;
        private const float InfoY = 116f;

        /// <summary>信息块宽度 = 图标画布的原生宽 <b>256</b>（<c>gfx/vgui/*.tga</c> 的 TGA 头，逐张都是 256 宽）。
        /// 原版 <c>ItemInfo</c> 声明的是 <c>wide 400 tall 380</c> + <c>autoResize 3</c>（缩到内容）
        /// ⇒ 真正的框尺寸由代码往里面塞的东西决定，取图标原生宽是与载体唯一对得上的那一档。</summary>
        private const float InfoWidth = 256f;

        /// <summary>竖向分隔线（出处 <c>MainBuyMenu.res:279-282</c>：<c>Divider1 xpos 236 ypos 116 wide 2 tall 284</c>）。</summary>
        private const float DividerX = 236f;
        private const float DividerY = 116f;
        private const float DividerWidth = 2f;
        private const float DividerHeight = 284f;

        /// <summary>原版分类页里 Autobuy 按钮的框（出处 <c>MainBuyMenu.res:239-242</c>：<c>xpos 249 ypos 116 wide 160 tall 20</c>）。
        /// 本工程没有 autobuy / rebuy（无对应事件）⇒ 该槽位放金钱读数。</summary>
        private const float MoneyX = 249f;
        private const float MoneyY = 116f;
        private const float MoneyWidth = 160f;

        /// <summary>字号 = 原版 <c>clientscheme.res</c> 在 <c>yres 1024 1199</c> 段的绝对值（标题走 <c>Title</c> 族）。
        /// 出处：<c>cstrike__resource__ClientScheme.res:389-396</c>（<c>Title</c> Verdana Bold tall 18）、
        /// <c>:300-307</c>（<c>DefaultSmall</c> tall 20）。</summary>
        private const int TitleFontSize = 18;
        private const int BodyFontSize = 20;

        /// <summary>行内名称 / 价格两侧的留白（原版由 <c>BuyMouseOverPanelButton</c> 在 <c>client.dll</c> 里画，
        /// <c>.res</c> 只给了整行 148×20 ⇒ 行内左右分配是本项目取值）。</summary>
        private const float RowInnerPad = 4f;

        /// <summary>
        /// 一个买枪分类（数字键 → 一页）。<c>Key</c> = 原版标签里的 <c>&amp;N</c> 编号；
        /// <c>OrderCT</c> / <c>OrderTER</c> = 该页条目的**顺序与集合**，逐条搬自原版武器页 `.res` 的
        /// <c>labelText</c> 序列（CT 页 / TER 页各一份 —— 原版两阵营的页内容不同，见数组注释）。
        /// </summary>
        private struct Category
        {
            public string Key;
            public string Label;
            public float Y;
            public string[] OrderCT;
            public string[] OrderTER;

            public Category(string key, string label, float y, string[] orderCT, string[] orderTER)
            {
                Key = key;
                Label = label;
                Y = y;
                OrderCT = orderCT;
                OrderTER = orderTER;
            }
        }

        /// <summary>原版装备页列了、而本工程武器表没有对应件的条目（名称/价格逐条搬自
        /// <c>BuyEquipment_{CT,TER}.res</c> 的 <c>labelText</c> + <c>cost</c>；⛔ 不往 <c>Core/CsWeapons.cs</c> 里塞）。</summary>
        private sealed class ExtraItem
        {
            public string Id;
            public string Name;
            public int Price;
            public string IconFile;
        }

        /// <summary>夜视仪：原版 <c>BuyEquipment_CT.res</c> / <c>BuyEquipment_TER.res</c> 的 `#Cstrike_NightVision_Button_*` `cost 1250`。</summary>
        private const string ExtraNightVisionId = "nightvision";

        /// <summary>战术盾：原版 <c>BuyEquipment_CT.res</c> 的 `#Cstrike_Shield` `cost 2200`（TER 页没有它）。</summary>
        private const string ExtraShieldId = "shield";

        private static readonly ExtraItem[] Extras =
        {
            new ExtraItem { Id = ExtraNightVisionId, Name = "夜视仪", Price = 1250, IconFile = "nightvision" },
            new ExtraItem { Id = ExtraShieldId, Name = "战术盾", Price = 2200, IconFile = "shield" },
        };

        /// <summary>当前页的一行：武器行走 <see cref="CsWeapons"/>，原版装备页的夜视仪 / 战术盾走 <see cref="Extras"/>。</summary>
        private sealed class RowData
        {
            public string Id;
            public string Name;
            public int Price;
            public string IconFile;
            /// <summary>武器表里的那一条（非武器条目为 null）。</summary>
            public CsWeaponDef Def;
            /// <summary>原版装备页有、本工程没有对应件的条目。</summary>
            public bool IsExtra;
        }

        /// <summary>
        /// 分类行与顺序逐条搬自原版分类页（<c>MainBuyMenu.res</c> 的 <c>MouseOverPanelButton</c>，
        /// 编号与英文串出自 <c>cstrike_english.txt</c>）：
        /// <list type="bullet">
        /// <item><c>&amp;1 PISTOLS</c> ypos 116（<c>MainBuyMenu.res:58-61</c>）</item>
        /// <item><c>&amp;2 SHOTGUNS</c> ypos 148（<c>:78-81</c>）</item>
        /// <item><c>&amp;3 SMG</c> ypos 180（<c>:98-101</c>）</item>
        /// <item><c>&amp;4 RIFLES</c> ypos 212（<c>:118-121</c>）</item>
        /// <item><c>&amp;5 MACHINE GUNS</c> ypos 244（<c>:138-141</c>）</item>
        /// <item><c>&amp;8 EQUIPMENT</c> ypos 340（<c>:198-201</c>）</item>
        /// </list>
        /// 原版另有 <c>&amp;6 PRIMARY AMMO</c> / <c>&amp;7 SECONDARY AMMO</c> 两页
        /// （<c>MainBuyMenu.res:155-194</c>，<c>Command primammo / secammo</c>）——
        /// 本工程没有购买弹药的通路（<c>Core/Events.cs</c> 只有 <c>BuyWeapon</c>）⇒ 这两行未接，
        /// 缺的槽位（ypos 276 / 308）保持空着。
        /// </summary>
        private static readonly Category[] Categories =
        {
            // 原版手枪页：CT 页列 Glock18/USP45/P228/DesertEagle/FiveSeven、TER 页把最后一项换成 Elites
            // （BuyPistols_CT.res / BuyPistols_TER.res 的 labelText 序列）
            new Category("1", "手枪", 116f,
                new[] { CsWeapons.Glock18, CsWeapons.Usp, CsWeapons.P228, CsWeapons.Deagle, CsWeapons.FiveSeven },
                new[] { CsWeapons.Glock18, CsWeapons.Usp, CsWeapons.P228, CsWeapons.Deagle, CsWeapons.Elite }),
            new Category("2", "霰弹", 148f,
                new[] { CsWeapons.M3, CsWeapons.Xm1014 },
                new[] { CsWeapons.M3, CsWeapons.Xm1014 }),
            // 原版冲锋枪页：CT 页 Tmp/MP5/UMP45/P90、TER 页把 Tmp 换成 MAC10
            new Category("3", "冲锋枪", 180f,
                new[] { CsWeapons.Tmp, CsWeapons.Mp5, CsWeapons.Ump45, CsWeapons.P90 },
                new[] { CsWeapons.Mac10, CsWeapons.Mp5, CsWeapons.Ump45, CsWeapons.P90 }),
            // 原版 RIFLES 页本身就列着 Scout / AWP / SG550 / G3SG1（BuyRifles_CT.res:78-142）
            // ⇒ 狙击枪并进步枪页，与载体一致（两阵营的 6 条各自成序）
            new Category("4", "步枪", 212f,
                new[] { CsWeapons.Famas, CsWeapons.Scout, CsWeapons.M4A1, CsWeapons.Aug, CsWeapons.Sg550, CsWeapons.Awp },
                new[] { CsWeapons.Galil, CsWeapons.Ak47, CsWeapons.Scout, CsWeapons.Sg552, CsWeapons.Awp, CsWeapons.G3sg1 }),
            new Category("5", "机枪", 244f,
                new[] { CsWeapons.M249 },
                new[] { CsWeapons.M249 }),
            // 原版装备页：护甲 / 拆弹器 / 三种手雷 + 夜视仪 +（仅 CT）战术盾
            // （BuyEquipment_CT.res:75-186 / BuyEquipment_TER.res:75-152 的 labelText 序列）
            new Category("8", "装备", 340f,
                new[]
                {
                    CsWeapons.Vest, CsWeapons.VestHelm, CsWeapons.Flashbang, CsWeapons.HeGrenade,
                    CsWeapons.SmokeGrenade, CsWeapons.Defuser, ExtraNightVisionId, ExtraShieldId,
                },
                new[]
                {
                    CsWeapons.Vest, CsWeapons.VestHelm, CsWeapons.Flashbang, CsWeapons.HeGrenade,
                    CsWeapons.SmokeGrenade, ExtraNightVisionId,
                }),
        };

        /// <summary>数字键 → 分类行下标（编号逐条取原版标签里的 <c>&amp;N</c>）。</summary>
        private static GameKey CategoryKey(int index)
        {
            switch (index)
            {
                case 0: return GameKey.Num1;
                case 1: return GameKey.Num2;
                case 2: return GameKey.Num3;
                case 3: return GameKey.Num4;
                case 4: return GameKey.Num5;
                default: return GameKey.Num8;
            }
        }

        /// <summary>武器页里第 <paramref name="slot"/> 行的数字键（原版武器页的标签不带编号，行号键由 <c>CBuySubMenu</c> 给）。</summary>
        private static GameKey ItemKey(int slot)
        {
            switch (slot)
            {
                case 0: return GameKey.Num1;
                case 1: return GameKey.Num2;
                case 2: return GameKey.Num3;
                case 3: return GameKey.Num4;
                case 4: return GameKey.Num5;
                case 5: return GameKey.Num6;
                case 6: return GameKey.Num7;
                default: return GameKey.Num8;
            }
        }

        /// <summary>
        /// 武器 id → 原版图标文件名（<c>gfx/vgui/&lt;名&gt;.tga</c>）。
        /// 文件名与原版条目的 <c>labelText</c> 令牌一致（如 <c>#Cstrike_USP45</c> → <c>usp45.tga</c>、
        /// <c>#Cstrike_DesertEagle</c> → <c>deserteagle.tga</c>，见 <c>BuyPistols_CT.res</c> 与
        /// <c>cstrike_english.txt</c>）；像素由 <c>tools/probes/tga-extract.py</c> 逐字节转成工程内 PNG。
        /// </summary>
        private static readonly Dictionary<string, string> IconFiles = new Dictionary<string, string>
        {
            { CsWeapons.Glock18, "glock18" },
            { CsWeapons.Usp, "usp45" },
            { CsWeapons.P228, "p228" },
            { CsWeapons.Deagle, "deserteagle" },
            { CsWeapons.FiveSeven, "fiveseven" },
            { CsWeapons.Elite, "elites" },
            { CsWeapons.Mp5, "mp5" },
            { CsWeapons.Tmp, "tmp" },
            { CsWeapons.Mac10, "mac10" },
            { CsWeapons.Ump45, "ump45" },
            { CsWeapons.P90, "p90" },
            { CsWeapons.Galil, "galil" },
            { CsWeapons.Famas, "famas" },
            { CsWeapons.Ak47, "ak47" },
            { CsWeapons.M4A1, "m4a1" },
            { CsWeapons.Sg552, "sg552" },
            { CsWeapons.Aug, "aug" },
            { CsWeapons.Scout, "scout" },
            { CsWeapons.Awp, "awp" },
            { CsWeapons.G3sg1, "g3sg1" },
            { CsWeapons.Sg550, "sg550" },
            { CsWeapons.M3, "m3" },
            { CsWeapons.Xm1014, "xm1014" },
            { CsWeapons.M249, "m249" },
            { CsWeapons.HeGrenade, "hegrenade" },
            { CsWeapons.Flashbang, "flashbang" },
            { CsWeapons.SmokeGrenade, "smokegrenade" },
            { CsWeapons.Vest, "kevlar" },
            { CsWeapons.VestHelm, "kevlar_helmet" },
            { CsWeapons.Defuser, "defuser" },
        };

        /// <summary>工程内图标 key 前缀（真实文件 <c>Resources/UI/Art/buy_&lt;原版名&gt;.png</c>）。</summary>
        private const string BuyIconDir = "UI/Art/buy_";

        // ─────────── 引用 ───────────
        [SerializeField] private RectTransform _panel;
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _hintText;
        /// <summary>分类行按钮（顺序 = <see cref="Categories"/>）。</summary>
        [SerializeField] private Button[] _categoryButtons;
        /// <summary>关闭行（分类页与武器页都在 ypos 380，出处见 <see cref="CancelY"/>）。</summary>
        [SerializeField] private Button _cancelButton;
        /// <summary>武器行的容器（行由运行期按槽位建，见 <see cref="CreateRow"/>）。</summary>
        [SerializeField] private RectTransform _itemRoot;
        [SerializeField] private Image _divider;
        [SerializeField] private Text _moneyText;
        [SerializeField] private Text _warnText;
        [SerializeField] private Image _infoIcon;
        [SerializeField] private Text _infoName;
        [SerializeField] private Text _infoPrice;

        // ─────────── 运行期 ───────────
        private readonly List<RowData> _items = new List<RowData>(ItemSlots);
        private readonly List<Row> _rows = new List<Row>(ItemSlots);
        private readonly Dictionary<string, Sprite> _iconCache = new Dictionary<string, Sprite>();
        private int _category;
        private bool _itemPage;
        private bool _warnedRefs;
        private bool _warnedAmmoPage;

        private sealed class Row
        {
            public Button Button;
            public Text Name;
            public Text Price;
            public Image Bg;
        }

        public override UILayer Layer => UILayer.Popup;

        /// <summary>设计坐标 → 画布像素（以面板左上角为原点；<c>PlaceTopLeft</c> 的 y 向下为负）。</summary>
        private static Vector2 Px(float designX, float designY) =>
            new Vector2((designX - PanelX) * Scale, -(designY - PanelY) * Scale);

        private static Vector2 Size(float designW, float designH) =>
            new Vector2(designW * Scale, designH * Scale);

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.5f), true);
            UIFactory.Stretch(backdrop.rectTransform);

            var box = UIFactory.CreatePanel("Dialog", root, CsUiStyle.Box, true);
            CsHudTheme.PlaceCenter(box.rectTransform, Vector2.zero, Size(PanelWidth, PanelHeight));
            _panel = box.rectTransform;

            _titleText = CsHudTheme.CreateText("Title", _panel, "BUY MENU", TitleFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(_titleText.rectTransform, Px(TitleX, TitleY), Size(TitleWidth, TitleHeight));

            _hintText = CsHudTheme.CreateText("Hint", _panel, "按分类选购", BodyFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(_hintText.rectTransform, Px(HintX, HintY), Size(HintWidth, HintHeight));

            _categoryButtons = new Button[Categories.Length];
            for (var i = 0; i < Categories.Length; i++)
            {
                var index = i;
                var button = CsHudTheme.CreateButton($"Cat{i}", _panel,
                    CategoryLabel(Categories[i]), new Vector2(0f, 1f), new Vector2(0f, 1f),
                    Px(RowX, Categories[i].Y), Size(RowWidth, RowHeight),
                    () => OpenCategory(index));
                SetRowLabel(button, CategoryLabel(Categories[i]));
                _categoryButtons[i] = button;
            }

            _cancelButton = CsHudTheme.CreateButton("Cat_Cancel", _panel, "关闭", new Vector2(0f, 1f),
                new Vector2(0f, 1f), Px(RowX, CancelY), Size(RowWidth, RowHeight), Close);
            SetRowLabel(_cancelButton, "0 关闭");

            _divider = CsHudTheme.CreateBlock("Divider", _panel, new Vector2(0f, 1f), new Vector2(0f, 1f),
                Px(DividerX, DividerY), Size(DividerWidth, DividerHeight), CsHudTheme.TextDim);

            _moneyText = CsHudTheme.CreateText("Money", _panel, "$0", BodyFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.MoneyCanBuy);
            CsHudTheme.PlaceTopLeft(_moneyText.rectTransform, Px(MoneyX, MoneyY), Size(MoneyWidth, RowHeight));

            var items = UIFactory.CreateNode("Items", _panel);
            CsHudTheme.PlaceTopLeft(items, Px(RowX, RowFirstY),
                Size(RowWidth, RowHeight * ItemSlots + RowPitch * (ItemSlots - 1)));
            _itemRoot = items;

            var info = UIFactory.CreateNode("ItemInfo", _panel);
            CsHudTheme.PlaceTopLeft(info, Px(InfoX, InfoY), Size(InfoWidth, PanelHeight - (InfoY - PanelY)));

            _infoIcon = UIFactory.CreatePanel("Icon", info, Color.white, false);
            CsHudTheme.PlaceTopLeft(_infoIcon.rectTransform, Vector2.zero, Size(InfoWidth, 64f));

            // 名称 / 价格贴在图标下方（图标高度随武器不同，落地位置在 ApplyIcon 里按实际高度摆）
            _infoName = CsHudTheme.CreateText("InfoName", info, string.Empty, BodyFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(_infoName.rectTransform, new Vector2(0f, -64f * Scale - 8f),
                Size(InfoWidth, 24f * Scale));

            _infoPrice = CsHudTheme.CreateText("InfoPrice", info, string.Empty, BodyFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.MoneyNormal);
            CsHudTheme.PlaceTopLeft(_infoPrice.rectTransform, new Vector2(0f, -64f * Scale - 38f),
                Size(InfoWidth, 24f * Scale));

            _warnText = CsHudTheme.CreateText("Warn", _panel, string.Empty, BodyFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.Danger);
            CsHudTheme.PlaceBottomLeft(_warnText.rectTransform, new Vector2(26f * Scale, 24f * Scale),
                Size(PanelWidth - 52f, 24f));
        }

        /// <summary>分类行的文案 = 原版标签的编号 + 本工程的分类名（编号取自 <c>cstrike_english.txt</c> 的 <c>&amp;N</c>）。</summary>
        private static string CategoryLabel(Category category) => category.Key + " " + category.Label;

        public override void OnOpen(object param)
        {
            WarnIfNull(_itemRoot, "武器列表容器");
            WarnIfNull(_titleText, "标题文本");
            WarnIfNull(_panel, "面板");
            if (!_warnedRefs)
            {
                _warnedRefs = true;
                if (_categoryButtons == null || _categoryButtons.Length != Categories.Length)
                    Game.Logger?.Error(Tag, "BuyMenuPanel 分类按钮引用不完整（预制体未由 UiBuilder 生成？），数字键仍然可用");
            }

            _category = 0;
            _itemPage = false;
            _warnedAmmoPage = false;
            for (var i = 0; i < Categories.Length; i++)
            {
                var index = i;
                if (_categoryButtons != null && index < _categoryButtons.Length)
                    Bind(_categoryButtons[index], () => OpenCategory(index), $"分类 {Categories[index].Key}");
            }
            Bind(_cancelButton, Close, "关闭");

            // 买枪菜单要点击 → 光标必须解锁（操作模块锁定光标时 uGUI 只能命中屏幕正中）
            CsIngameCursor.UnlockForMenu(nameof(BuyMenuPanel));

            ShowCategories();
            Refresh();

            Game.Logger?.Info(Tag,
                $"买枪菜单已打开：阵营={CsHudSnapshot.Team} 金钱=${CsHudSnapshot.Money} " +
                $"在买枪区={CsHudSnapshot.InBuyZone} 可买={CsHudSnapshot.CanBuyNow}");
        }

        public override void OnClose()
        {
            CsIngameCursor.RelockIfIdle(nameof(BuyMenuPanel), CsIngameCursor.AnyMenuOpen());
        }

        public override void OnUpdate(float dt)
        {
            var input = Game.Input;
            if (input != null)
            {
                if (!_itemPage)
                {
                    // 分类页：数字编号逐条取自原版标签的 `&N`（1/2/3/4/5/8），取消用 `&0`
                    for (var i = 0; i < Categories.Length; i++)
                    {
                        if (input.GetKeyDown(CategoryKey(i))) { OpenCategory(i); return; }
                    }
                    if (input.GetKeyDown(GameKey.Num6) || input.GetKeyDown(GameKey.Num7))
                    {
                        if (!_warnedAmmoPage)
                        {
                            _warnedAmmoPage = true;
                            Game.Logger?.Warn(Tag, "买枪分类 6/7 = 原版的 PRIMARY/SECONDARY AMMO 页，本工程没有购买弹药的通路，已忽略");
                        }
                    }
                }
                else
                {
                    for (var slot = 0; slot < ItemSlots; slot++)
                    {
                        if (input.GetKeyDown(ItemKey(slot)) && slot < _items.Count)
                        {
                            TryBuy(_items[slot].Id);
                            return;
                        }
                    }
                }

                if (input.GetKeyDown(GameKey.Num0) || input.GetKeyDown(GameKey.B) ||
                    input.GetKeyDown(GameKey.Escape))
                {
                    Close();
                    return;
                }
            }

            Refresh();
        }

        private void Close()
        {
            Game.Logger?.Info(Tag, "买枪菜单关闭");
            Game.UI.Close<BuyMenuPanel>();
        }

        // ═══════════════════════ 分类 / 列表 ═══════════════════════

        private void OpenCategory(int index)
        {
            if (index < 0 || index >= Categories.Length)
            {
                Game.Logger?.Warn(Tag, $"买枪分类序号 {index} 越界（共 {Categories.Length} 类），已忽略");
                return;
            }

            _category = index;
            _itemPage = true;
            Game.Logger?.Info(Tag, $"买枪分类打开：{Categories[index].Label}");
            RebuildItems();
            Refresh();
        }

        private void ShowCategories()
        {
            _itemPage = false;
            RebuildItems();
        }

        private void RebuildItems()
        {
            _items.Clear();

            SetCategoryRowsActive(!_itemPage);
            _hintText.gameObject.SetActive(!_itemPage);
            _divider.gameObject.SetActive(!_itemPage);
            _moneyText.gameObject.SetActive(!_itemPage);
            SetInfoVisible(_itemPage);

            if (!_itemPage)
            {
                _titleText.text = "BUY MENU";
                _titleText.color = CsHudTheme.TextMain;
                HideRows();
                return;
            }

            var category = Categories[Mathf.Clamp(_category, 0, Categories.Length - 1)];
            _titleText.text = category.Label;
            _titleText.color = CsHudTheme.TextMain;

            // 页面条目 = 原版该页 `.res` 列出的顺序与集合（不是按武器大类临场筛）
            var team = CsHudSnapshot.Team;
            var order = team == CsTeam.T ? category.OrderTER : category.OrderCT;
            for (var i = 0; i < order.Length; i++)
            {
                var def = CsWeapons.Get(order[i]);
                if (def != null)
                {
                    _items.Add(new RowData { Id = def.Id, Name = def.DisplayName, Price = def.Price, Def = def });
                    continue;
                }

                var extra = FindExtra(order[i]);
                if (extra == null)
                {
                    Game.Logger?.Warn(Tag, $"原版页里的 {order[i]} 在武器表与兜底表里都没有 ⇒ 该条目跳过（原版有、工程无）");
                    continue;
                }
                _items.Add(new RowData
                {
                    Id = extra.Id, Name = extra.Name, Price = extra.Price, IconFile = extra.IconFile, IsExtra = true,
                });
            }

            EnsureRows(_items.Count);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;

                if (i >= _items.Count)
                {
                    if (row.Button != null && row.Button.gameObject.activeSelf)
                        row.Button.gameObject.SetActive(false);
                    continue;
                }

                var item = _items[i];
                if (row.Button != null && !row.Button.gameObject.activeSelf)
                    row.Button.gameObject.SetActive(true);
                if (row.Name != null) row.Name.text = item.Name;
                if (row.Price != null) row.Price.text = PriceText(item.Price);

                var id = item.Id;
                Bind(row.Button, () => TryBuy(id), $"购买 {item.Name}");
                BindHover(row.Button, id);
            }

            // 分类列表为空 = 数据/阵营组合有问题，不能静默留一片空白
            if (_items.Count == 0)
                Game.Logger?.Warn(Tag, $"买枪分类「{category.Label}」在当前阵营（{team}）下没有任何可买武器");

            // 原版一页最多 8 条（BuyEquipment_CT.res 的 ypos 116…340）——超出就静默丢条目，
            // 那不是"少显示一行"，是"买不到"，所以必须留痕
            if (_items.Count > ItemSlots)
                Game.Logger?.Warn(Tag,
                    $"买枪分类「{category.Label}」有 {_items.Count} 条，超过原版一页的 {ItemSlots} 个槽位；多出的条目没有行可点");

            if (_items.Count > 0) ShowRowInfo(_items[0].Id);
        }

        private static string PriceText(int price) => price > 0 ? $"${price}" : "-";

        private void EnsureRows(int count)
        {
            while (_rows.Count < count && _rows.Count < ItemSlots)
            {
                _rows.Add(CreateRow(_rows.Count));
            }
        }

        private void HideRows()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row?.Button != null && row.Button.gameObject.activeSelf)
                    row.Button.gameObject.SetActive(false);
            }
        }

        private void SetCategoryRowsActive(bool active)
        {
            if (_categoryButtons == null) return;
            for (var i = 0; i < _categoryButtons.Length; i++)
            {
                if (_categoryButtons[i] != null) _categoryButtons[i].gameObject.SetActive(active);
            }
        }

        private void SetInfoVisible(bool visible)
        {
            if (_infoIcon != null) _infoIcon.gameObject.SetActive(visible);
            if (_infoName != null) _infoName.gameObject.SetActive(visible);
            if (_infoPrice != null) _infoPrice.gameObject.SetActive(visible);
        }

        private Row CreateRow(int index)
        {
            var pos = new Vector2(0f, -index * RowPitch * Scale);
            var size = Size(RowWidth, RowHeight);

            var bg = UIFactory.CreatePanel($"Item{index}", _itemRoot, CsUiStyle.Field, true);
            CsHudTheme.PlaceTopLeft(bg.rectTransform, pos, size);
            var button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;

            var colors = button.colors;
            colors.normalColor = CsUiStyle.Field;
            colors.highlightedColor = CsUiStyle.AccentDim;
            colors.pressedColor = CsUiStyle.Accent;
            colors.selectedColor = CsUiStyle.Field;
            colors.disabledColor = new Color(0.18f, 0.18f, 0.18f, 1f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.05f;
            button.colors = colors;

            var name = CsHudTheme.CreateText("Name", bg.rectTransform, "-", BodyFontSize, TextAnchor.MiddleLeft,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceTopLeft(name.rectTransform, new Vector2(RowInnerPad * Scale, 0f),
                new Vector2((RowWidth - RowInnerPad * 2f) * Scale, size.y));
            name.raycastTarget = false;

            var price = CsHudTheme.CreateText("Price", bg.rectTransform, "-", BodyFontSize, TextAnchor.MiddleRight,
                CsHudTheme.MoneyNormal);
            CsHudTheme.PlaceTopRight(price.rectTransform, new Vector2(-RowInnerPad * Scale, 0f),
                new Vector2(60f * Scale, size.y));
            price.raycastTarget = false;

            return new Row { Button = button, Name = name, Price = price, Bg = bg };
        }

        /// <summary>列表行按钮的文案与排版：原版这些行都是 <c>textAlignment west</c>（左对齐）、
        /// 字号走 1080p 段的 20（<see cref="CsHudTheme.CreateButton"/> 默认给的是 22）。</summary>
        private static void SetRowLabel(Button button, string label)
        {
            if (button == null) return;
            var text = button.GetComponentInChildren<Text>();
            if (text == null) return;
            text.text = label;
            text.fontSize = BodyFontSize;
            text.alignment = TextAnchor.MiddleLeft;
        }

        /// <summary>鼠标进入武器行 ⇒ 右侧信息块换成该武器的原版图标与价格（原版的行为，见 ItemInfo 的出处）。</summary>
        private static void BindHover(Button button, string weaponId)
        {
            if (button == null) return;
            var panel = button.GetComponentInParent<BuyMenuPanel>();
            if (panel == null) return;

            var trigger = button.GetComponent<EventTrigger>();
            if (trigger == null) trigger = button.gameObject.AddComponent<EventTrigger>();

            // 每次重建都会重绑这一行 ⇒ 先清掉上一轮的条目，否则同一行会挂多条一样的监听
            trigger.triggers.Clear();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            entry.callback.AddListener(_ => panel.ShowRowInfo(weaponId));
            trigger.triggers.Add(entry);
        }

        // ═══════════════════════ 刷新 / 购买 ═══════════════════════

        private void Refresh()
        {
            var money = CsHudSnapshot.Money;
            var canBuyNow = CsHudSnapshot.CanBuyNow;
            var inZone = CsHudSnapshot.InBuyZone;

            if (_moneyText != null)
            {
                _moneyText.text = $"${money}";
                _moneyText.color = canBuyNow ? CsHudTheme.MoneyCanBuy : CsHudTheme.MoneyNormal;
            }

            if (_warnText != null)
            {
                if (!inZone) _warnText.text = "必须在买枪区内（Buy Zone）才能购买";
                else if (!canBuyNow) _warnText.text = "现在不是买枪时间（只能在每回合开局的买枪时间内购买）";
                else if (CsHudSnapshot.Team != CsTeam.T && CsHudSnapshot.Team != CsTeam.CT)
                    _warnText.text = "观察者不能购买武器";
                else _warnText.text = string.Empty;
            }

            // 买不起的条目变灰且不可点（规格 G3）
            for (var i = 0; i < _rows.Count && i < _items.Count; i++)
            {
                var row = _rows[i];
                if (row?.Button == null) continue;

                var affordable = money >= _items[i].Price;
                if (row.Button.interactable != affordable) row.Button.interactable = affordable;
                if (row.Price != null)
                    row.Price.color = affordable ? CsHudTheme.MoneyNormal : CsHudTheme.Disabled;
                if (row.Name != null)
                    row.Name.color = affordable ? CsHudTheme.TextMain : CsHudTheme.Disabled;
            }

            RefreshCategoryHighlight();
        }

        private void RefreshCategoryHighlight()
        {
            if (_categoryButtons == null) return;
            for (var i = 0; i < _categoryButtons.Length && i < Categories.Length; i++)
            {
                var button = _categoryButtons[i];
                if (button == null) continue;

                var selected = i == _category && _itemPage;
                var colors = button.colors;
                colors.normalColor = selected ? CsUiStyle.AccentDim : CsUiStyle.Field;
                colors.selectedColor = colors.normalColor;
                colors.highlightedColor = CsUiStyle.Accent;
                colors.pressedColor = CsUiStyle.AccentDim;
                colors.colorMultiplier = 1f;
                button.colors = colors;
            }
        }

        /// <summary>按 id 找当前页的那一行（点 / 悬停都走它，界面上没有第二个列表）。</summary>
        private RowData FindItem(string itemId)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].Id == itemId) return _items[i];
            }
            return null;
        }

        /// <summary>按 id 找原版装备页的兜底条目（夜视仪 / 战术盾）。</summary>
        private static ExtraItem FindExtra(string itemId)
        {
            for (var i = 0; i < Extras.Length; i++)
            {
                if (Extras[i].Id == itemId) return Extras[i];
            }
            return null;
        }

        /// <summary>把某一行的原版图标 / 名称 / 价格放进右侧信息块（找不到该行则只清空文本）。</summary>
        private void ShowRowInfo(string itemId)
        {
            var item = FindItem(itemId);
            if (_infoName != null) _infoName.text = item != null ? item.Name : string.Empty;
            if (_infoPrice != null) _infoPrice.text = item != null ? PriceText(item.Price) : string.Empty;
            if (_infoIcon == null) return;

            var key = item == null ? null : IconKey(item);
            if (key == null)
            {
                _infoIcon.enabled = false;
                return;
            }

            if (_iconCache.TryGetValue(key, out var cached) && cached != null)
            {
                ApplyIcon(cached);
                return;
            }

            _infoIcon.enabled = false;
            var res = Game.Res;
            if (res == null) return;
            res.LoadAsset<Sprite>(key, sprite =>
            {
                if (sprite == null)
                {
                    Game.Logger?.Warn(Tag, $"买枪菜单图标加载失败：Resources/{key}.png 没导入成 Sprite？该武器只显示名称与价格");
                    return;
                }
                _iconCache[key] = sprite;
                ApplyIcon(sprite);
            });
        }

        /// <summary>
        /// 图标按**原版像素等比放大**（256×64 / 256×128 的原生画布 × <see cref="Scale"/>）——
        /// 原版画布尺寸取自 <c>gfx/vgui/*.tga</c> 的 TGA 头，转换见 <c>tools/probes/tga-extract.py</c>。
        /// </summary>
        private void ApplyIcon(Sprite sprite)
        {
            if (_infoIcon == null || sprite == null) return;
            _infoIcon.sprite = sprite;
            _infoIcon.enabled = true;

            var width = sprite.rect.width * Scale;
            var height = sprite.rect.height * Scale;
            _infoIcon.rectTransform.sizeDelta = new Vector2(width, height);

            if (_infoName != null)
                _infoName.rectTransform.anchoredPosition = new Vector2(0f, -height - 8f);
            if (_infoPrice != null)
                _infoPrice.rectTransform.anchoredPosition = new Vector2(0f, -height - 38f);
        }

        private static string IconKey(RowData item)
        {
            if (item.IsExtra) return BuyIconDir + item.IconFile;
            return IconFiles.TryGetValue(item.Id, out var file) ? BuyIconDir + file : null;
        }

        private void TryBuy(string weaponId)
        {
            var item = FindItem(weaponId);
            if (item == null)
            {
                Game.Logger?.Error(Tag, $"买枪菜单点了一个不在当前页的 id：{weaponId}（界面与数据不同步）");
                HudPanel.Notify($"未知条目：{weaponId}");
                return;
            }

            // 原版装备页列了、本工程没有对应件的条目（夜视仪 / 战术盾）：照列不给买，并且要说清原因
            if (item.IsExtra)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {item.Name}：本工程没有这件装备（原版装备页列了它）");
                HudPanel.Notify($"本工程未实现「{item.Name}」（原版装备页有此条目）");
                return;
            }

            var def = item.Def;
            if (def == null)
            {
                Game.Logger?.Error(Tag, $"买枪菜单的「{item.Name}」既不是武器条目也没有兜底数据，已忽略");
                return;
            }

            // 阵营限制：原版 CT 手枪页含 Glock18、TER 页含 USP45，而武器表把这两把标成单阵营专用
            // ⇒ 行照原版列出，点下去在本地拦下并说清是**阵营限制**（不是界面缺项）。
            if (!TeamAllows(def.TeamLimit, CsHudSnapshot.Team))
            {
                Game.Logger?.Info(Tag,
                    $"拒绝购买 {def.DisplayName}：阵营限制 {def.TeamLimit}，本地阵营 {CsHudSnapshot.Team}");
                HudPanel.Notify($"不能购买：{def.DisplayName} 只属于{TeamLimitText(def.TeamLimit)}");
                return;
            }

            // 先在本地把"必然失败"的情况挡住：比赛模块对这些失败只打 Warn 日志、不推消息栏事件，
            // 不挡的话玩家点下去会毫无反馈（点了没反应是最糟的交互）。
            if (!CsHudSnapshot.InBuyZone)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {def.DisplayName}：不在买枪区");
                HudPanel.Notify("不能购买：必须在买枪区内");
                return;
            }
            if (!CsHudSnapshot.CanBuyNow)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {def.DisplayName}：不在买枪时间");
                HudPanel.Notify("不能购买：不在买枪时间内");
                return;
            }
            if (CsHudSnapshot.Money < def.Price)
            {
                Game.Logger?.Info(Tag, $"拒绝购买 {def.DisplayName}：金钱不足（${CsHudSnapshot.Money} < ${def.Price}）");
                HudPanel.Notify($"金钱不足：{def.DisplayName} 需要 ${def.Price}");
                return;
            }

            Game.Logger?.Info(Tag, $"买枪请求：{def.DisplayName}（{def.Id}）${def.Price}，由比赛模块判定是否成交");
            Game.Event.Emit(Events.BuyWeapon, def.Id);
            HudPanel.Notify($"购买 {def.DisplayName}（${def.Price}）");
        }

        /// <summary>武器表的阵营限制是否包含这个阵营（<see cref="CsTeamLimit.Any"/> 一律允许）。</summary>
        private static bool TeamAllows(CsTeamLimit limit, CsTeam team)
        {
            switch (limit)
            {
                case CsTeamLimit.TerroristOnly: return team == CsTeam.T;
                case CsTeamLimit.CounterTerroristOnly: return team == CsTeam.CT;
                default: return true;
            }
        }

        /// <summary>阵营限制的中文说法（提示语用）。</summary>
        private static string TeamLimitText(CsTeamLimit limit)
        {
            switch (limit)
            {
                case CsTeamLimit.TerroristOnly: return "恐怖分子阵营";
                case CsTeamLimit.CounterTerroristOnly: return "反恐精英阵营";
                default: return "任意阵营";
            }
        }
    }
}
