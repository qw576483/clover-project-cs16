using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 加入游戏的两级菜单：**选阵营** + **选兵种**（原版客户端就是这两个 VGUI 面板，顺序也是先阵营后兵种）。
    ///
    /// <para>
    /// <b>载体（几何 + 文案令牌）</b>：原版 <c>Resource/UI/ClassMenu_CT.res</c> / <c>ClassMenu_TER.res</c>
    /// （本机副本 <c>原版资源/cs16src/cstrike/cstrike__resource__UI__Classmenu_CT.res</c> = 4,063 B /
    /// <c>..._Classmenu_TER.res</c> = 4,087 B）与 <c>cstrike/resource/cstrike_english.txt</c>（文案令牌的英文原文）。
    /// 「原版确实读这两份 .res」的客户端侧证据在 <c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>：
    /// 字符串 <c>Resource/UI/ClassMenu_CT.res</c> @0x0e5240、<c>Resource/UI/ClassMenu_TER.res</c> @0x0e5220、
    /// <c>joinclass 6</c> @0x0ed4a8、面板名 <c>ClassMenu</c> @0x0ed458 / <c>ClassInfo</c> @0x0ed44c。
    /// 选阵营页的节点名 / 命令来自 <c>Resource/UI/TeamMenu.res</c>（同名文件在本机没有副本，见 <c>原版资源/清单.md:357</c>）。
    /// </para>
    ///
    /// <para>
    /// <b>两页的几何逐字相同</b>（框 76/0/552/448、标题 0/22/500/48、行 0/宽 148/高 20、起始 ypos 116、行距 32）——
    /// 选阵营页 6 行（116…276），选兵种页 7 行（多一行 308 = 取消）。
    /// 所以两页各建一个框节点，名字取各自载体里的 <c>fieldName</c>（<c>TeamMenu</c> / <c>ClassMenu</c>）。
    /// </para>
    ///
    /// <para>
    /// <b>坐标一律写"原版值"再乘 <see cref="CsUiStyle.ResScale"/></b>（= 1080/480 = 2.25），
    /// 而不是把乘完的数直接写死 —— 这样代码里的每个数都能与 <c>.res</c> 逐字对上（换算口径见 <see cref="CsUiStyle.ResScale"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>两级顺序</b>：选阵营页选了 T / CT（含 VIP 与 AUTO ASSIGN 落到的那一侧）⇒ 进选兵种页；
    /// 选兵种页选完（含 <c>joinclass 6</c> 自动）才发 <see cref="Events.TeamChosen"/>。
    /// 观察者（<c>jointeam 6</c>）两份 classmenu 都不覆盖 ⇒ 直接进局。
    /// </para>
    ///
    /// <para>
    /// <b>文案</b>：原版逐字取自 <c>cstrike_english.txt</c>（行号见各常量注释）；<c>&amp;N</c> 是 VGUI 的热键前缀，
    /// 显示时去 <c>&amp;</c>、保留那个字符（原版这些行都是 `textAlignment west` 的纯文本）。
    /// 「用中文显示」与中文译法是**本项目新增**的可见差异（登记在 <c>策划/差异登记.tsv</c>）；
    /// 字体链（Verdana + CJK 回退族）见 <see cref="CsUiStyle.OriginalFont"/>。
    /// </para>
    ///
    /// <para>
    /// <b>载体里 <c>visible 0</c> 的控件不建</b>：<c>SysMenu</c>（`.res:28`）、<c>classInfoLabel</c>（`:64`）、
    /// <c>ClassInfo</c>（`:82`）在原版就是隐藏的 —— 建出来反而与原来的画面不一致。
    /// 其中 <c>classInfoLabel</c> 的令牌 <c>#Cstrike_Class_Info</c> 在 <c>cstrike_english.txt</c> 里**没有词条**
    /// （相邻的 <c>Cstrike_Join_Class</c> / <c>Cstrike_Auto_Select</c> 都有）⇒ 它显示出来也只是一个裸令牌。
    /// </para>
    /// </summary>
    public class TeamSelectPanel : CsPanelBase
    {
        // ─────────── 两页共用的几何（设计空间 640×480，单位 = 设计像素）───────────
        /// <summary>框：`Classmenu_CT.res:7-10` / `Classmenu_TER.res:7-10` 的 `ClassMenu`（xpos 76 ypos 0 wide 552 tall 448）。</summary>
        private const float FrameX = 76f, FrameY = 0f, FrameW = 552f, FrameH = 448f;

        /// <summary>标题框：`Classmenu_CT.res:37-40` 的 `joinClass`（xpos 0 ypos 22 wide 500 tall 48；`font "Title"` 在 `:50`）。</summary>
        private const float TitleX = 0f, TitleY = 22f, TitleW = 500f, TitleH = 48f;

        /// <summary>一行的 `xpos / wide / tall`：`Classmenu_CT.res:91-94` 的 `urban`（xpos 0 wide 148 tall 20；其余 6 行逐字相同）。</summary>
        private const float BtnX = 0f, BtnW = 148f, BtnH = 20f;

        /// <summary>
        /// 行格的 `ypos`：起始 116、行距 32 —— 逐个取 `Classmenu_CT.res` 的
        /// `urban:92` / `gsg9:112` / `sas:133` / `gign:154` / `spetsnaz:175` /
        /// `autoselect_ct:196` / `CancelButton:217`。
        /// 选阵营页取前 6 行（`.res` 侧同名栅格），选兵种页取全部 7 行。
        /// </summary>
        private static readonly float[] RowY = { 116f, 148f, 180f, 212f, 244f, 276f, 308f };

        /// <summary>
        /// 全屏压暗层：实机帧 `class_select_team_panel.png` 的控件压在**明显压暗过**的地图上（约 5 成黑），
        /// 否则橙字/橙框在亮地图上几乎看不见。
        /// </summary>
        private static readonly Color DimBackdrop = new Color(0f, 0f, 0f, 0.5f);

        /// <summary>
        /// 16:9 下原版把 640 宽的设计区**横向居中**（实机帧证据：`.res` 的 `xpos 76` 落在 640×360 帧的
        /// 137 px 处 = 80 px 左留白 + 76 × 0.75）⇒ 画布上要补 (1920 − 640 × 2.25) / 2 = 240 px 的左边距。
        /// </summary>
        private const float CenterX = (1920f - 640f * CsUiStyle.ResScale) * 0.5f;

        // ─────────── 左上角：阵营图标 + 大标题（实机帧量得，单位 = 设计像素）───────────
        /// <summary>图标框：帧上 106..121 px × 18..44 px（= 设计 20×35），相对 `Frame` 左移 41。</summary>
        private const float IconX = -41f, IconY = 24f, IconW = 20f, IconH = 35f;

        /// <summary>图标字号：图标框高 35 设计单位，字标取框高的 ~0.55。</summary>
        private const int IconFontSize = 44;

        // ─────────── 右侧描述框（实机帧量得：640×360 下 x 264..502、y 88..302）───────────
        private const float DescX = 245f, DescY = 117f, DescW = 319f, DescH = 285f;
        /// <summary>文本框内缩（原版文字离边框约 3 px @360p）。</summary>
        private const float DescInset = 5f;
        /// <summary>滚动箭头方块（帧上 18×19 px ≈ 设计 24×25），贴框内右上 / 右下。</summary>
        private const float ArrowW = 24f, ArrowH = 25f, ArrowInset = 3f;

        // ─────────── 右下角金钱（实机帧量得：右边缘距屏右 8 px、文字高 13 px @360p；按 ×3 换成画布单位）───────────
        private const float MoneyMarginRight = 24f, MoneyMarginBottom = 150f;
        private const float MoneyW = 300f, MoneyH = 45f;
        private const int MoneyFontSize = 40;

        // ─────────── 选阵营页（`TeamMenu.res` 的节点名与命令）───────────
        /// <summary>`.res` 的 `fieldName`（节点名与它一致，便于与 `.res` 逐条对照）。</summary>
        private static readonly string[] ButtonNames =
        {
            "terbutton", "ctbutton", "vipbutton", "autobutton", "specbutton", "CancelButton",
        };

        /// <summary>
        /// 文案逐字取自 `cstrike_english.txt` 的 token：`Cstrike_Terrorist_Forces` :85 /
        /// `Cstrike_CT_Forces` :86 / `Cstrike_VIP_Team` :87 / `Cstrike_Team_AutoAssign` :88 /
        /// `Cstrike_Menu_Spectate` :89 / `Cstrike_Cancel` :79。
        /// VGUI 把 `&amp;` 当热键前缀（显示时去掉 `&amp;`、保留后面的字符），所以这里就是去 `&amp;` 后的原文。
        ///
        /// <para>`ButtonCommands` 侧的 `jointeam 1/2/3/5/6` + `vguicancel` 是 `.res` 的命令，逐字不变。</para>
        /// </summary>
        private static readonly string[] ButtonTexts =
        {
            "1 TERRORIST FORCES",
            "2 CT FORCES",
            "3 VIP",
            "5 AUTO ASSIGN",
            "6 SPECTATE",
            "0 CANCEL",
        };

        /// <summary>`.res` 的 `command` / `Command`（原版就是大小写混用：最后一个是 `"Command"`）。</summary>
        private static readonly string[] ButtonCommands =
        {
            "jointeam 1", "jointeam 2", "jointeam 3", "jointeam 5", "jointeam 6", "vguicancel",
        };

        /// <summary>标题 = `#Cstrike_Join_Team`「SELECT TEAM」（`cstrike_english.txt:84`）。</summary>
        private const string TitleText = "SELECT TEAM";

        /// <summary>
        /// 右侧描述框的内容 = 本图（de_dust2）的任务简报，结构与实机帧一致：
        /// 首行「地图名 - 模式」，随后 CT / T 各一段目标，末行补两条机制说明。
        /// 原版的这段文字来自地图 worldspawn 的 message（不在 `cstrike_english.txt` 里），本工程按同一结构写。
        /// </summary>
        private static readonly string TeamBriefing =
            "DE_DUST II  -  Bomb Defusal\n" +
            "\n" +
            "Counter-Terrorists: Prevent the Terrorists\n" +
            "from planting the bomb at either bombsite.\n" +
            "Eliminate the entire Terrorist team to win\n" +
            "the round.\n" +
            "\n" +
            "Terrorists: Plant the bomb at bombsite A or B\n" +
            "and defend it until it detonates. Eliminate\n" +
            "the entire Counter-Terrorist team to win the\n" +
            "round.\n" +
            "\n" +
            "Other Notes: Two bombsites (A / B). The bomb\n" +
            $"explodes {CsConst.BombTimer:0} seconds after it is planted.";

        /// <summary>
        /// AUTO ASSIGN（原版 `jointeam 5`）对半随机挑边的**实际范围**：本按钮只在**赛前**（选阵营站点）可达，
        /// 那一刻还没有任何 roster，且两侧 bot 数逐字相等（<see cref="CsMatchConfig.BotsPerTeam"/> 是单值）
        /// ⇒ 原版 `CCSGameRules::SelectDefaultTeam()` 的「人数少的一队」分支走不到，只剩平局分支
        /// （见 <see cref="OnAutoAssign"/>）。
        /// </summary>
        private const int AutoAssignTeams = 2;

        // ─────────── 选兵种页（`Classmenu_{CT,TER}.res`）───────────
        /// <summary>
        /// CT 侧 5 个兵种：节点名 = `.res` 的 `fieldName`
        /// （`Classmenu_CT.res:90/110/131/152/173`），
        /// 文案令牌逐条取自 `cstrike_english.txt`（去 `&amp;` 后显示）：
        /// `Cstrike_Urban :100` = "&amp;1 SEAL TEAM 6"、`Cstrike_GSG9 :101` = "&amp;2 GSG-9"、
        /// `Cstrike_SAS :102` = "&amp;3 SAS"、`Cstrike_GIGN :103` = "&amp;4 GIGN"、
        /// `Cstrike_Spetsnaz :104` = "&amp;5 SPETSNAZ"。
        /// </summary>
        private static readonly string[] ClassNamesCT = { "urban", "gsg9", "sas", "gign", "spetsnaz" };

        private static readonly string[] ClassTextsCT =
        {
            "1 SEAL TEAM 6",
            "2 GSG-9",
            "3 SAS",
            "4 GIGN",
            "5 SPETSNAZ",
        };

        /// <summary>
        /// T 侧 5 个兵种：节点名 = `.res` 的 `fieldName`（`Classmenu_TER.res:90/110/131/152/173`），
        /// 文案令牌：`Cstrike_Terror :95` = "&amp;1 PHOENIX CONNEXION"、`Cstrike_L337_Krew :96` = "&amp;2 ELITE CREW"、
        /// `Cstrike_Arctic :97` = "&amp;3 ARCTIC AVENGERS"、`Cstrike_Guerilla :98` = "&amp;4 GUERILLA WARFARE"、
        /// `Cstrike_Militia :99` = "&amp;5 MIDWEST MILITIA"。
        /// </summary>
        private static readonly string[] ClassNamesTER = { "terror", "leet", "arctic", "guerilla", "militia" };

        private static readonly string[] ClassTextsTER =
        {
            "1 PHOENIX CONNEXION",
            "2 ELITE CREW",
            "3 ARCTIC AVENGERS",
            "4 GUERILLA WARFARE",
            "5 MIDWEST MILITIA",
        };

        /// <summary>
        /// 选兵种页的「自动选择」行：`.res` 的 `autoselect_ct:194` / `autoselect_t:194`，`command "joinclass 6"`
        /// （`:208`），`labelText "#Cstrike_Auto_Select"`（`:209`）= `cstrike_english.txt:93` 的 "&amp;5 AUTO-SELECT"
        /// —— 它的 `&amp;5` 与第 5 个兵种的 `&amp;5` 同号（原版数据如此，不是这里写错）。
        /// </summary>
        private const string ClassAutoCommand = "joinclass 6";
        private const string ClassAutoText = "5 AUTO-SELECT";
        private const string ClassAutoNameCT = "autoselect_ct";
        private const string ClassAutoNameTER = "autoselect_t";

        /// <summary>
        /// 选兵种页的取消行：`command "vguicancel"`（`Classmenu_CT.res:229` / `_TER.res:229`）。
        /// 节点名取 TER 载体的 `fieldName`（`_TER.res:215` = `cancelbutton`；CT 那份是 `:215` = `CancelButton`）
        /// —— 两页的取消行几何相同（xpos 0 ypos 308 wide 148 tall 20），本面板只建这一个节点给两页共用。
        /// </summary>
        private const string ClassCancelCommand = "vguicancel";
        private const string ClassCancelName = "cancelbutton";
        private const string ClassCancelText = "0 CANCEL";

        /// <summary>选兵种页的标题 = `#Cstrike_Join_Class`（`cstrike_english.txt:92` = "CHOOSE A CLASS"）。</summary>
        private const string ClassTitleText = "CHOOSE A CLASS";

        [SerializeField] private RectTransform _teamFrame;
        [SerializeField] private Text _titleLabel;
        /// <summary>6 个按钮的引用（`[SerializeField]` 才能进预制体 —— 数组元素是场景对象引用，可序列化）。</summary>
        [SerializeField] private Button[] _buttons;

        [SerializeField] private RectTransform _classFrame;
        [SerializeField] private Text _classTitleLabel;
        /// <summary>CT / T 两组兵种行（同一页只激活其中一组）。</summary>
        [SerializeField] private RectTransform _classRowsCT;
        [SerializeField] private RectTransform _classRowsTER;
        [SerializeField] private Button[] _classButtonsCT;
        [SerializeField] private Button[] _classButtonsTER;
        [SerializeField] private Button _classAutoButtonCT;
        [SerializeField] private Button _classAutoButtonTER;
        [SerializeField] private Button _classCancelButton;

        /// <summary>右侧描述框的正文（两页共用）。</summary>
        [SerializeField] private Text _descText;
        /// <summary>右下角金钱。</summary>
        [SerializeField] private Text _moneyText;

        public override void BuildLayout(RectTransform root)
        {
            // 全屏压暗层（最先建 ⇒ 在所有控件之下），raycast=true 把背景的点击挡在这一层。
            CsUiStyle.CreateFullScreen("Backdrop", root, DimBackdrop);

            // 两个 Frame 的底色都取 scheme 默认的 `BgColor` → `ControlBG "0 0 0 0"`
            // （`clientscheme.res:38` + `:101`）⇒ **Frame 本身完全透明，没有黑板**。
            // 它仍然要存在：两页的子控件坐标都相对各自的 Frame。
            _teamFrame = BuildFrame("TeamMenu", root);
            _classFrame = BuildFrame("ClassMenu", root);

            // ── 选阵营页 ──
            _titleLabel = BuildHeader(_teamFrame, "joinTeam", TitleText);

            _buttons = new Button[ButtonNames.Length];
            for (var i = 0; i < ButtonNames.Length; i++)
            {
                _buttons[i] = CsUiStyle.CreateBorderedButton(ButtonNames[i], _teamFrame,
                    ButtonTexts[i], ResLocal(BtnX, RowY[i]), ResSize(BtnW, BtnH), null);
            }

            // ── 选兵种页 ──
            _classTitleLabel = BuildHeader(_classFrame, "joinClass", ClassTitleText);

            BuildClassRows("ClassRows_CT", ClassNamesCT, ClassTextsCT, ClassAutoNameCT,
                out _classRowsCT, out _classButtonsCT, out _classAutoButtonCT);
            BuildClassRows("ClassRows_TER", ClassNamesTER, ClassTextsTER, ClassAutoNameTER,
                out _classRowsTER, out _classButtonsTER, out _classAutoButtonTER);

            _classCancelButton = CsUiStyle.CreateBorderedButton(ClassCancelName, _classFrame,
                ClassCancelText, ResLocal(BtnX, RowY[6]), ResSize(BtnW, BtnH), null);

            // ── 两页共用的右侧描述框 + 右下角金钱 ──
            BuildDescriptionBox(root);
            BuildMoney(root);

            ShowTeamPage(false);

            // 引擎署名（逐字 `by clover-engine`）不建在这里：它由常驻件 CsCreditBadge 画在面板之上，
            // 各面板各建一行会漏页（新增面板没人记得补）、且面板一关那一行就没了。

            // 生成期（FlowSetup 造预制体）也设一次字体：动态字体不是工程资产、存不进 .prefab，
            // 因此 OnOpen 还会再设一次；这里只是为了运行期兜底搭布局那条路径不至于少字体。
            CsUiStyle.ApplyOriginalFonts(root);
            CsUiStyle.ApplyTitleFont(_titleLabel);
            CsUiStyle.ApplyTitleFont(_classTitleLabel);
        }

        public override void OnOpen(object param)
        {
            if (_buttons == null || _buttons.Length != ButtonNames.Length)
            {
                Game.Logger.Warn(Tag,
                    $"选阵营面板的 {ButtonNames.Length} 个按钮引用不完整（预制体未由布局代码生成或已被改动），事件未绑定");
            }
            else
            {
                for (var i = 0; i < _buttons.Length; i++)
                {
                    var index = i;
                    Bind(_buttons[index], () => OnTeamCommand(index), $"{ButtonNames[index]}（{ButtonCommands[index]}）");
                    BindRowHover(_buttons[index]);
                }
            }

            BindClassRows(CsTeam.CT, ClassNamesCT, _classButtonsCT, _classAutoButtonCT);
            BindClassRows(CsTeam.T, ClassNamesTER, _classButtonsTER, _classAutoButtonTER);
            Bind(_classCancelButton, OnClassCancel, $"{ClassCancelName}（{ClassCancelCommand}）");

            // 每次打开都回到第一页（选阵营），避免上一次的兵种页残留
            ShowTeamPage(false);
            RefreshMoney();

            // 预制体里存不下动态字体（见 ApplyOriginalFonts 的说明）⇒ 每次打开都重设一遍。
            CsUiStyle.ApplyOriginalFonts(transform);
            CsUiStyle.ApplyTitleFont(_titleLabel);
            CsUiStyle.ApplyTitleFont(_classTitleLabel);

            // 预制体里存的是生成时的文字位置（旧版带 8 px 左内缩）⇒ 每次打开把按钮文字重贴到边框左缘。
            ApplyButtonTextInsets();

            Game.Logger.Info(Tag,
                "选阵营面板已打开（原版 teammenu.res：6 项，命令 jointeam 1/2/3/5/6 + vguicancel；" +
                "选完阵营进选兵种页 = 原版 Classmenu_{CT,TER}.res：5 个兵种 + joinclass 6 + vguicancel）");
        }

        /// <summary>把两页所有边框按钮的文字重贴到边框左缘（口径见 <see cref="CsUiStyle.CreateBorderedButton"/>）。</summary>
        private void ApplyButtonTextInsets()
        {
            ApplyTextInset(_buttons);
            ApplyTextInset(_classButtonsCT);
            ApplyTextInset(_classButtonsTER);
            CsUiStyle.ApplyBorderedButtonTextInset(_classAutoButtonCT);
            CsUiStyle.ApplyBorderedButtonTextInset(_classAutoButtonTER);
            CsUiStyle.ApplyBorderedButtonTextInset(_classCancelButton);
        }

        private static void ApplyTextInset(Button[] buttons)
        {
            if (buttons == null) return;
            for (var i = 0; i < buttons.Length; i++) CsUiStyle.ApplyBorderedButtonTextInset(buttons[i]);
        }

        /// <summary>建一个 `Frame`（透明底），返回它的矩形当作子控件的坐标空间。</summary>
        private static RectTransform BuildFrame(string name, RectTransform root)
        {
            var frame = CsUiStyle.CreateBoxRect(name, root,
                ResPos(FrameX, FrameY), ResSize(FrameW, FrameH), CsUiStyle.ControlBg, true);
            return frame.rectTransform;
        }

        /// <summary>
        /// 左上角：阵营图标 + 大标题（实机帧里图标在标题左侧、标题橙色且明显大于菜单项）。
        /// 原版图标是一张 VGUI 贴图，本机载体 `gfx/vgui` 里只有买枪图标、没有队伍图标
        /// ⇒ 这里用同色的 "CT" 字标占位。
        /// </summary>
        private static Text BuildHeader(RectTransform frame, string titleName, string titleText)
        {
            var badge = CsUiStyle.CreateLabel("TeamIcon", frame, "CT", IconFontSize,
                ResLocal(IconX, IconY), ResSize(IconW, IconH), TextAnchor.MiddleCenter, CsUiStyle.Text);
            if (badge != null)
            {
                badge.fontStyle = FontStyle.Bold;
                badge.horizontalOverflow = HorizontalWrapMode.Overflow;
                badge.verticalOverflow = VerticalWrapMode.Overflow;
            }

            return CsUiStyle.CreateLabel(titleName, frame, titleText, CsUiStyle.MenuTitleFontSize,
                ResLocal(TitleX, TitleY), ResSize(TitleW, TitleH), TextAnchor.MiddleLeft, CsUiStyle.Text);
        }

        /// <summary>右侧描述框：深色底 + 1 px 橙边 + 多行英文 + 右上 / 右下滚动箭头。</summary>
        private void BuildDescriptionBox(RectTransform root)
        {
            var size = ResSize(DescW, DescH);
            var box = CsUiStyle.CreateBoxRect("ClassInfo", root, ResPos(DescX, DescY), size,
                CsUiStyle.WindowBg, false);
            CsUiStyle.CreateBorderEdges(box.rectTransform, size, CsUiStyle.BorderWidth, CsUiStyle.BorderLine);

            _descText = CsUiStyle.CreateLabel("textLabel", box.transform, TeamBriefing,
                CsUiStyle.OriginalFontSize, ResLocal(DescInset, DescInset),
                ResSize(DescW - DescInset * 2f, DescH - DescInset * 2f), TextAnchor.UpperLeft, CsUiStyle.Text);
            if (_descText != null)
            {
                _descText.horizontalOverflow = HorizontalWrapMode.Wrap;
                _descText.verticalOverflow = VerticalWrapMode.Truncate;
                _descText.lineSpacing = 1f;
            }
            else
            {
                Game.Logger.Warn(Tag, "描述框正文建不出来（CreateLabel 返回 null）");
            }

            BuildArrow(box.rectTransform, "ScrollUp", ArrowInset);
            BuildArrow(box.rectTransform, "ScrollDown", DescH - ArrowInset - ArrowH);
        }

        /// <summary>
        /// 框内右侧的滚动箭头方块（上 / 下）。正文放得下时它们保持原版的"不可滚"状态（暗色），
        /// 因此这里只画不绑点击 —— 画一个能点但没反应的按钮比画一个暗色箭头更糟。
        /// </summary>
        private static void BuildArrow(RectTransform box, string name, float y)
        {
            var size = ResSize(ArrowW, ArrowH);
            var arrow = CsUiStyle.CreateBoxRect(name, box,
                ResLocal(DescW - ArrowInset - ArrowW, y), size, CsUiStyle.ControlBg, false);
            CsUiStyle.CreateBorderEdges(arrow.rectTransform, size, CsUiStyle.BorderWidth, CsUiStyle.BorderLine);

            var glyph = CsUiStyle.CreateLabel("Glyph", arrow.transform,
                name == "ScrollUp" ? "\u25B2" : "\u25BC", 14,
                Vector2.zero, size, TextAnchor.MiddleCenter, CsUiStyle.TextDim);
            if (glyph != null)
            {
                glyph.raycastTarget = false;
                glyph.horizontalOverflow = HorizontalWrapMode.Overflow;
                glyph.verticalOverflow = VerticalWrapMode.Overflow;
            }
        }

        /// <summary>右下角金钱：原版在选人界面右下角一直显示 `$ 16000`（位置按实机帧，屏幕锚定、不随设计区居中）。</summary>
        private void BuildMoney(RectTransform root)
        {
            var size = new Vector2(MoneyW, MoneyH);
            var pos = new Vector2(1920f - MoneyW - MoneyMarginRight, -(1080f - MoneyH - MoneyMarginBottom));
            _moneyText = CsUiStyle.CreateLabel("Money", root, "$ " + CsConst.MaxMoney,
                MoneyFontSize, pos, size, TextAnchor.MiddleRight, CsUiStyle.Text);
            if (_moneyText == null)
            {
                Game.Logger.Warn(Tag, "右下角金钱文本建不出来（CreateLabel 返回 null）");
                return;
            }

            _moneyText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _moneyText.verticalOverflow = VerticalWrapMode.Overflow;
        }

        /// <summary>建一组兵种行（5 个兵种 + 自动选择）到 `ClassMenu` 下，返回这组的宿主节点以便整组开关。</summary>
        private void BuildClassRows(string groupName, string[] names, string[] texts, string autoName,
            out RectTransform group, out Button[] rows, out Button auto)
        {
            group = UIFactory.CreateNode(groupName, _classFrame);
            CsUiStyle.AnchoredTopLeft(group, Vector2.zero, ResSize(FrameW, FrameH));

            rows = new Button[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                rows[i] = CsUiStyle.CreateBorderedButton(names[i], group, texts[i],
                    ResLocal(BtnX, RowY[i]), ResSize(BtnW, BtnH), null);
            }

            auto = CsUiStyle.CreateBorderedButton(autoName, group, ClassAutoText,
                ResLocal(BtnX, RowY[names.Length]), ResSize(BtnW, BtnH), null);
        }

        /// <summary>把原版 `.res` 的 `command` 派到本工程的事件上；每条命令都留痕（含未实现的那条）。</summary>
        private void OnTeamCommand(int index)
        {
            var command = ButtonCommands[index];
            switch (command)
            {
                case "jointeam 1":
                    ShowClassPage(CsTeam.T, "TERRORIST FORCES");
                    break;

                case "jointeam 2":
                    ShowClassPage(CsTeam.CT, "CT FORCES");
                    break;

                case "jointeam 3":
                    // 原版 jointeam 3 = VIP 阵营（`cstrike_english.txt:87` 的 `&3 VIP`）。
                    // 本工程的阵营契约 `CsTeam` 只有 Spectator/T/CT（`Core/CsEnums.cs`），没有 VIP
                    // ⇒ 按 VIP 属 CT 一侧落到 CT，并留一条 Warn（登记在 `策划/差异登记.tsv`）。
                    Game.Logger.Warn(Tag,
                        "原版 jointeam 3 = VIP 阵营；本工程 CsTeam 契约无 VIP ⇒ 落到 CT 侧（VIP 模式未实现）");
                    ShowClassPage(CsTeam.CT, "VIP");
                    break;

                case "jointeam 5":
                    OnAutoAssign();
                    break;

                case "jointeam 6":
                    // 观察者：原版两份 classmenu 只覆盖 T / CT ⇒ 没有兵种页可进，直接进局。
                    Choose(CsTeam.Spectator, "SPECTATE");
                    break;

                case "vguicancel":
                    OnCancel();
                    break;

                default:
                    Game.Logger.Warn(Tag, $"未知的原版命令「{command}」，已忽略");
                    break;
            }
        }

        /// <summary>绑一组兵种行（CT / T 各一次，两组只有一组是激活的）。</summary>
        private void BindClassRows(CsTeam team, string[] names, Button[] rows, Button auto)
        {
            if (rows == null || rows.Length != names.Length)
            {
                Game.Logger.Warn(Tag,
                    $"{team} 侧选兵种页的 {names.Length} 个兵种行引用不完整（预制体未由布局代码生成或已被改动），事件未绑定");
            }
            else
            {
                for (var i = 0; i < rows.Length; i++)
                {
                    var index = i;
                    Bind(rows[index], () => OnClassChosen(team, names[index], index + 1),
                        $"{names[index]}（joinclass {index + 1}）");
                    BindRowHover(rows[index]);
                }
            }

            Bind(auto, () => OnClassAuto(team), $"{team} 侧自动选择（{ClassAutoCommand}）");
            BindRowHover(auto);
        }

        /// <summary>右下角金钱 = 本地玩家当前金钱（HUD 快照）；快照还没建立时用起始金钱。</summary>
        private void RefreshMoney()
        {
            if (_moneyText == null) return;
            _moneyText.text = "$ " + (CsHudSnapshot.Valid ? CsHudSnapshot.Money : CsConst.MaxMoney);
        }

        /// <summary>悬停 / 选中文字色 = 黑（`SelectionBG` 橙条上的原版做法，见选兵种页实机帧里高亮的 SAS 行）。</summary>
        private static readonly Color HoverTextColor = new Color32(0x00, 0x00, 0x00, 0xFF);

        /// <summary>
        /// 悬停时把行文字换成黑色：底色由 `Button` 的 ColorTint 换成 `SelectionBG`，
        /// 文字不是 `targetGraphic` ⇒ 只能靠指针事件改色。
        /// </summary>
        private static void BindRowHover(Button button)
        {
            if (button == null) return;
            var label = button.GetComponentInChildren<Text>();
            if (label == null) return;

            var trigger = button.GetComponent<EventTrigger>();
            if (trigger == null) trigger = button.gameObject.AddComponent<EventTrigger>();
            trigger.triggers.Clear();
            var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
            enter.callback.AddListener(_ => label.color = HoverTextColor);
            trigger.triggers.Add(enter);
            var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            exit.callback.AddListener(_ => label.color = CsUiStyle.Text);
            trigger.triggers.Add(exit);
        }

        // ═══════════════════════════════ 两页的切换 ═══════════════════════════════

        /// <summary>显示选阵营页（第二页隐藏）。</summary>
        private void ShowTeamPage(bool log)
        {
            SetActive(_teamFrame, true);
            SetActive(_classFrame, false);
            if (log) Game.Logger.Info(Tag, "回选阵营页（原版 teammenu.res：6 项）");
        }

        /// <summary>显示选兵种页（第一页隐藏；只激活该阵营的那 5 个兵种行）。</summary>
        private void ShowClassPage(CsTeam team, string teamLabel)
        {
            SetActive(_teamFrame, false);
            SetActive(_classFrame, true);
            SetActive(_classRowsCT, team == CsTeam.CT);
            SetActive(_classRowsTER, team == CsTeam.T);
            Game.Logger.Info(Tag,
                $"选阵营: {team}（原版项 {teamLabel}）→ 进选兵种页（原版 Classmenu_{(team == CsTeam.CT ? "CT" : "TER")}.res：" +
                "5 个兵种 + joinclass 6 + vguicancel）");
        }

        private static void SetActive(RectTransform rt, bool active)
        {
            if (rt == null || rt.gameObject.activeSelf == active) return;
            rt.gameObject.SetActive(active);
        }

        // ═══════════════════════════════ 选择落地 ═══════════════════════════════

        /// <summary>
        /// 选兵种页上点了一个兵种（`joinclass 1..5`）。
        /// 原版这条命令由服务端换玩家皮肤；本工程把选择交给 <see cref="CsPlayerClass"/>，
        /// 皮肤在视图层按兵种取（`Module/View/CsClassSkins` → `Art/{T|CT}/{皮肤键}`）。
        /// </summary>
        private void OnClassChosen(CsTeam team, string classId, int joinClass)
        {
            Game.Logger.Info(Tag, $"选兵种: {classId}（原版 joinclass {joinClass}，{team} 侧第 {joinClass} 项）");
            CsPlayerClass.Set(team, joinClass);
            Choose(team, classId);
        }

        /// <summary>选兵种页上点了「自动选择」（原版 `joinclass 6`）。</summary>
        private void OnClassAuto(CsTeam team)
        {
            // 原版这条命令不带兵种号 ⇒ 由服务端分配一个；本工程清掉显式选择 = 回"队内随机皮肤"。
            Game.Logger.Info(Tag, $"选兵种: 自动选择（原版 joinclass 6 = &5 AUTO-SELECT，由服务端分配）");
            CsPlayerClass.Set(team, CsPlayerClass.None);
            Choose(team, "AUTO-SELECT");
        }

        /// <summary>选兵种页的取消（原版 `vguicancel`）：退回选阵营页，不把玩家直接踢回主菜单。</summary>
        private void OnClassCancel()
        {
            Game.Logger.Info(Tag, "选兵种被取消（原版 vguicancel），回选阵营页");
            ShowTeamPage(true);
        }

        private void Choose(CsTeam team, string label)
        {
            Game.Logger.Info(Tag, $"选阵营: {team}（原版项 {label}）");
            Game.Event.Emit(Events.TeamChosen, team);
        }

        /// <summary>
        /// 原版 `jointeam 5` = AUTO ASSIGN：由服务器按两队人数分配（`CCSGameRules::SelectDefaultTeam()`）。
        /// 本按钮只在**赛前**可达（站点序 `Loading → TeamSelect → Stage`，比赛在 `Stage` 站点才开始）⇒
        /// 那一刻没有 roster、两侧人数相等 ⇒ 只有**平局分支**可达 ⇒ 按 <see cref="AutoAssignTeams"/> 对半随机挑边。
        /// </summary>
        private void OnAutoAssign()
        {
            // **玩法**：这一掷决定玩家进 T 还是 CT ⇒ 影响整局。
            // 走 CsRng 的 UiAutoAssign 流（UI 不许引用 Module，只能经 Core 取随机源）。
            // 注意时序：本面板出现在 `_match.Start` **之前** ⇒ CsRng 此时尚未定种子，
            // 首次取流会按「首次使用（时间）」显式定一次并打 Info（含 seed）；
            // 随后 CsMatch.Start → CsRng.BeginMatch() 会重定本局种子并清空子流。
            var pick = CsRng.Stream(CsRngStream.UiAutoAssign).Next(0, AutoAssignTeams);
            var team = pick == 0 ? CsTeam.T : CsTeam.CT;
            ShowClassPage(team, "AUTO ASSIGN");
        }

        private void OnCancel()
        {
            // 关掉自己即可：AppFlow 的 TeamSelect 站点 onTick 检测到面板已关，会退回主菜单站点
            // （并顺手卸载已加载的舞台场景）。
            Game.Logger.Info(Tag, "选阵营被取消（原版 vguicancel），回主菜单");
            Game.UI.Close<TeamSelectPanel>();
        }

        /// <summary>
        /// 原版设计空间坐标 → 本工程画布坐标（唯一换算处，见 <see cref="CsUiStyle.ResScale"/>）。
        /// `.res` 的 `ypos` 向下为正，而 <see cref="CsUiStyle.AnchoredTopLeft"/> 的 y 向上为正 ⇒ **y 取负**。
        /// 只用于挂在面板根节点上的控件（`Frame` / 描述框），会补上 16:9 的横向居中位移 <see cref="CenterX"/>。
        /// </summary>
        private static Vector2 ResPos(float x, float y)
        {
            return new Vector2(x * CsUiStyle.ResScale + CenterX, -y * CsUiStyle.ResScale);
        }

        /// <summary>同上，但**不加**居中位移 —— 挂在 `Frame` 里、坐标相对 Frame 左上角的子控件用这个。</summary>
        private static Vector2 ResLocal(float x, float y)
        {
            return new Vector2(x * CsUiStyle.ResScale, -y * CsUiStyle.ResScale);
        }

        /// <summary>原版设计空间尺寸 → 本工程画布尺寸（同一比值；见 <see cref="CsUiStyle.ResScale"/>）。</summary>
        private static Vector2 ResSize(float w, float h)
        {
            return new Vector2(w * CsUiStyle.ResScale, h * CsUiStyle.ResScale);
        }
    }
}
