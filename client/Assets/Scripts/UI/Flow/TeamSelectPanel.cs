using CloverEngine;
using Cs16.Core;
using UnityEngine;
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

        // ─────────── 选阵营页（`TeamMenu.res` 的节点名与命令）───────────
        /// <summary>`.res` 的 `fieldName`（节点名与它一致，便于与 `.res` 逐条对照）。</summary>
        private static readonly string[] ButtonNames =
        {
            "terbutton", "ctbutton", "vipbutton", "autobutton", "specbutton", "CancelButton",
        };

        /// <summary>
        ///
        /// <para><b>原版出处仍在，只是不再直接显示</b>：原版文案取自 `cstrike_english.txt` 的 token
        /// （去 `&amp;` 后显示）：`Cstrike_Terrorist_Forces` :85 / `Cstrike_CT_Forces` :86 /
        /// `Cstrike_VIP_Team` :87 / `Cstrike_Team_AutoAssign` :88 / `Cstrike_Menu_Spectate` :89 /
        /// `Cstrike_Cancel` :79（英文原文逐字见下方注释）。中文译法取 CS 中文版的通行译名。</para>
        ///
        /// <para>这是**本项目新增**的可见差异（登记在 `策划/差异登记.tsv`）：中文字面在原版载体里
        /// **不存在**。之所以做：① 用户明确要求；② 本面板本来就已经带一条**非原版**的底部署名
        /// `by clover-engine`；③ 工程字体链已内置 CJK 回退族
        /// （`CsUiStyle.OriginalFont` = Verdana → Microsoft YaHei → PingFang SC → Noto Sans CJK SC），
        /// 该回退族本身也已登记为「允许的差异」⇒ 中文能正常出字形，不是方框。</para>
        ///
        /// <para>`ButtonCommands` 侧的 `jointeam 1/2/3/5/6` + `vguicancel` **逐字不变**（那是 `.res` 的
        /// 命令，翻译它等于改行为）。</para>
        /// </summary>
        private static readonly string[] ButtonTexts =
        {
            "1 恐怖分子",     // 原版 Cstrike_Terrorist_Forces :85 → "&1 TERRORIST FORCES"
            "2 反恐精英",     // 原版 Cstrike_CT_Forces :86        → "&2 CT FORCES"
            "3 VIP",          // 原版 Cstrike_VIP_Team :87         → "&3 VIP"（专有名词，保留原文）
            "5 随机分配",     // 原版 Cstrike_Team_AutoAssign :88  → "&5 AUTO ASSIGN"
            "6 观战",         // 原版 Cstrike_Menu_Spectate :89    → "&6 SPECTATE"
            "0 取消",         // 原版 Cstrike_Cancel :79           → "&0 CANCEL"
        };

        /// <summary>`.res` 的 `command` / `Command`（原版就是大小写混用：最后一个是 `"Command"`）。</summary>
        private static readonly string[] ButtonCommands =
        {
            "jointeam 1", "jointeam 2", "jointeam 3", "jointeam 5", "jointeam 6", "vguicancel",
        };

        /// <summary>`#Cstrike_Join_Team` = `SELECT TEAM`（`cstrike_english.txt:84`）。显示中文「选择阵营」。</summary>
        private const string TitleText = "选择阵营";

        /// <summary>AUTO ASSIGN（原版 `jointeam 5`）在本工程里按对半随机挑边（见 <see cref="OnAutoAssign"/>）。</summary>
        private const int AutoAssignTeams = 2;

        // ─────────── 选兵种页（`Classmenu_{CT,TER}.res`）───────────
        /// <summary>
        /// CT 侧 5 个兵种：节点名 = `.res` 的 `fieldName`
        /// （`Classmenu_CT.res:90/110/131/152/173`），
        /// 文案令牌逐条取自 `cstrike_english.txt`（去 `&amp;` 后显示）：
        /// `Cstrike_Urban :100` = "&amp;1 SEAL TEAM 6"、`Cstrike_GSG9 :101` = "&amp;2 GSG-9"、
        /// `Cstrike_SAS :102` = "&amp;3 SAS"、`Cstrike_GIGN :103` = "&amp;4 GIGN"、
        /// `Cstrike_Spetsnaz :104` = "&amp;5 SPETSNAZ"。
        /// 中译取 CS 中文版的通行译名；GSG-9 / SAS / GIGN 在中文版里也是原文简写，故保留。
        /// </summary>
        private static readonly string[] ClassNamesCT = { "urban", "gsg9", "sas", "gign", "spetsnaz" };

        private static readonly string[] ClassTextsCT =
        {
            "1 海豹六队",          // &1 SEAL TEAM 6
            "2 GSG-9",            // &2 GSG-9
            "3 SAS",              // &3 SAS
            "4 GIGN",             // &4 GIGN
            "5 俄罗斯特种部队",    // &5 SPETSNAZ
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
            "1 凤凰战士",      // &1 PHOENIX CONNEXION
            "2 精英部队",      // &2 ELITE CREW
            "3 北极复仇者",    // &3 ARCTIC AVENGERS
            "4 游击战士",      // &4 GUERILLA WARFARE
            "5 中西部民兵",    // &5 MIDWEST MILITIA
        };

        /// <summary>
        /// 选兵种页的「自动选择」行：`.res` 的 `autoselect_ct:194` / `autoselect_t:194`，`command "joinclass 6"`
        /// （`:208`），`labelText "#Cstrike_Auto_Select"`（`:209`）= `cstrike_english.txt:93` 的 "&amp;5 AUTO-SELECT"
        /// —— 它的 `&amp;5` 与第 5 个兵种的 `&amp;5` 同号（原版数据如此，不是这里写错）。
        /// </summary>
        private const string ClassAutoCommand = "joinclass 6";
        private const string ClassAutoText = "5 随机选择";
        private const string ClassAutoNameCT = "autoselect_ct";
        private const string ClassAutoNameTER = "autoselect_t";

        /// <summary>
        /// 选兵种页的取消行：`command "vguicancel"`（`Classmenu_CT.res:229` / `_TER.res:229`）。
        /// 节点名取 TER 载体的 `fieldName`（`_TER.res:215` = `cancelbutton`；CT 那份是 `:215` = `CancelButton`）
        /// —— 两页的取消行几何相同（xpos 0 ypos 308 wide 148 tall 20），本面板只建这一个节点给两页共用。
        /// </summary>
        private const string ClassCancelCommand = "vguicancel";
        private const string ClassCancelName = "cancelbutton";
        private const string ClassCancelText = "0 取消";

        /// <summary>选兵种页的标题 = `#Cstrike_Join_Class`（`cstrike_english.txt:92` = "CHOOSE A CLASS"），显示中文。</summary>
        private const string ClassTitleText = "选择兵种";

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

        public override void BuildLayout(RectTransform root)
        {
            // 原版 `.res` 里**没有全屏压暗控件**（只有 Frame / SysMenu / 可见的那些行）⇒ 本面板不铺全屏 Backdrop。
            //
            // 两个 Frame 的底色都取 scheme 默认的 `BgColor` → `ControlBG "0 0 0 0"`
            // （`clientscheme.res:38` + `:101`）⇒ **Frame 本身完全透明，没有黑板**。
            // 它仍然要存在：两页的子控件坐标都相对各自的 Frame。
            _teamFrame = BuildFrame("TeamMenu", root);
            _classFrame = BuildFrame("ClassMenu", root);

            // ── 选阵营页 ──
            _titleLabel = CsUiStyle.CreateLabel("joinTeam", _teamFrame, TitleText,
                CsUiStyle.OriginalTitleFontSize, ResPos(TitleX, TitleY), ResSize(TitleW, TitleH),
                TextAnchor.MiddleLeft, CsUiStyle.Text);

            _buttons = new Button[ButtonNames.Length];
            for (var i = 0; i < ButtonNames.Length; i++)
            {
                _buttons[i] = CsUiStyle.CreateOriginalButton(ButtonNames[i], _teamFrame,
                    ButtonTexts[i], ResPos(BtnX, RowY[i]), ResSize(BtnW, BtnH), null);
            }

            // ── 选兵种页 ──
            _classTitleLabel = CsUiStyle.CreateLabel("joinClass", _classFrame, ClassTitleText,
                CsUiStyle.OriginalTitleFontSize, ResPos(TitleX, TitleY), ResSize(TitleW, TitleH),
                TextAnchor.MiddleLeft, CsUiStyle.Text);

            BuildClassRows("ClassRows_CT", ClassNamesCT, ClassTextsCT, ClassAutoNameCT,
                out _classRowsCT, out _classButtonsCT, out _classAutoButtonCT);
            BuildClassRows("ClassRows_TER", ClassNamesTER, ClassTextsTER, ClassAutoNameTER,
                out _classRowsTER, out _classButtonsTER, out _classAutoButtonTER);

            _classCancelButton = CsUiStyle.CreateOriginalButton(ClassCancelName, _classFrame,
                ClassCancelText, ResPos(BtnX, RowY[6]), ResSize(BtnW, BtnH), null);

            ShowTeamPage(false);

            // 必须用底部锚点：左上锚点 + 大负 y 会随画布高度变化掉出屏幕（MainMenuPanel 实测踩过）。
            // 单一入口 = CsUiStyle.CreateCreditLabel（→ 引擎 UIFactory.CreateCreditLabel，文案取引擎默认值
            // 逐字 `by clover-engine`）：本面板不再自持文案常量、不再自写贴底定位与尺寸。
            CsUiStyle.CreateCreditLabel(root);

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
                }
            }

            BindClassRows(CsTeam.CT, ClassNamesCT, _classButtonsCT, _classAutoButtonCT);
            BindClassRows(CsTeam.T, ClassNamesTER, _classButtonsTER, _classAutoButtonTER);
            Bind(_classCancelButton, OnClassCancel, $"{ClassCancelName}（{ClassCancelCommand}）");

            // 每次打开都回到第一页（选阵营），避免上一次的兵种页残留
            ShowTeamPage(false);

            // 预制体里存不下动态字体（见 ApplyOriginalFonts 的说明）⇒ 每次打开都重设一遍。
            CsUiStyle.ApplyOriginalFonts(transform);
            CsUiStyle.ApplyTitleFont(_titleLabel);
            CsUiStyle.ApplyTitleFont(_classTitleLabel);

            Game.Logger.Info(Tag,
                "选阵营面板已打开（原版 teammenu.res：6 项，命令 jointeam 1/2/3/5/6 + vguicancel；" +
                "选完阵营进选兵种页 = 原版 Classmenu_{CT,TER}.res：5 个兵种 + joinclass 6 + vguicancel）");
        }

        /// <summary>建一个 `Frame`（透明底），返回它的矩形当作子控件的坐标空间。</summary>
        private static RectTransform BuildFrame(string name, RectTransform root)
        {
            var frame = CsUiStyle.CreateBoxRect(name, root,
                ResPos(FrameX, FrameY), ResSize(FrameW, FrameH), CsUiStyle.ControlBg, true);
            return frame.rectTransform;
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
                rows[i] = CsUiStyle.CreateOriginalButton(names[i], group, texts[i],
                    ResPos(BtnX, RowY[i]), ResSize(BtnW, BtnH), null);
            }

            auto = CsUiStyle.CreateOriginalButton(autoName, group, ClassAutoText,
                ResPos(BtnX, RowY[names.Length]), ResSize(BtnW, BtnH), null);
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
                    // ⇒ 按 VIP 属 CT 一侧落到 CT，并留一条 Warn（登记在验收表「允许的差异」）。
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
                }
            }

            Bind(auto, () => OnClassAuto(team), $"{team} 侧自动选择（{ClassAutoCommand}）");
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
        /// 原版 `jointeam 5` = AUTO ASSIGN：由服务器分配到人数少的一方。
        /// 本工程是单机版（无真人计数），按 <see cref="AutoAssignTeams"/> 对半随机挑边（登记在「允许的差异」）。
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
        /// </summary>
        private static Vector2 ResPos(float x, float y)
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
