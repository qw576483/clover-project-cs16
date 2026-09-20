using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 选阵营 —— **逐字段重建自原版 `.res`**：`原版资源/cs16src/cs16game/app/cstrike/resource/ui/teammenu.res`。
    ///
    /// <para>
    /// 出现时机：地图与场景读完（读条 100%）之后、<c>_match.Start</c> 之前 ——
    /// 所以玩家看到的背景是真实的 de_dust2，与原版一致（原版这个框也是盖在已加载的地图上）。
    /// </para>
    ///
    /// <para>
    /// <b>坐标一律写"原版值"再乘 <see cref="CsUiStyle.ResScale"/></b>（= 1080/480 = 2.25），
    /// 而不是把乘完的数直接写死 —— 这样代码里的每个数都能与 `.res` 逐字对上，
    /// 换算口径也只有一处（出处的说明见 <see cref="CsUiStyle.ResScale"/>）。
    /// </para>
    ///
    /// <para>
    /// <b>文案</b>逐字取自 `cstrike/resource/cstrike_english.txt`（行号见各常量注释）；
    /// `&amp;N` 是 VGUI 的热键前缀，**显示时去掉 `&amp;`、保留数字/字母**（VGUI 惯例；字母会被画上角标下划线）。
    /// </para>
    ///
    /// <para>
    /// <b>按钮形态</b>：原版是"黑底 + 橙字 + 左对齐"的纯文本按钮（`.res` 里 6 个按钮全是
    /// `textAlignment west`），**不是**橙色大按钮网格（本面板上一版那样）。
    /// </para>
    /// </summary>
    public class TeamSelectPanel : CsPanelBase
    {
        // ─────────── 原版 teammenu.res 的坐标（设计空间 640×480，单位 = 设计像素）───────────
        /// <summary>`TeamMenu`（Frame）：`.res:7-10`。</summary>
        private const float FrameX = 76f, FrameY = 0f, FrameW = 552f, FrameH = 448f;
        /// <summary>`joinTeam`（Label，`font "Title"`，`textAlignment west`）：`.res:49-63`。</summary>
        private const float TitleX = 0f, TitleY = 22f, TitleW = 500f, TitleH = 48f;
        /// <summary>`MapInfo`（ControlName `HTML`，`autoResize 3`）：`.res:35-38`。</summary>
        private const float MapX = 168f, MapY = 116f, MapW = 316f, MapH = 286f;
        /// <summary>6 个按钮共用的 `xpos / wide / tall`：`.res:86-89` 等（每个按钮都一样）。</summary>
        private const float BtnX = 0f, BtnW = 148f, BtnH = 20f;

        /// <summary>6 个按钮的 `ypos`，顺序 = `.res` 里的控件顺序。</summary>
        private static readonly float[] ButtonY = { 116f, 148f, 180f, 212f, 244f, 276f };

        /// <summary>`.res` 的 `fieldName`（节点名与它一致，便于与 `.res` 逐条对照）。</summary>
        private static readonly string[] ButtonNames =
        {
            "terbutton", "ctbutton", "vipbutton", "autobutton", "specbutton", "CancelButton",
        };

        /// <summary>
        /// 显示文案 = `cstrike_english.txt` 的 token 去 `&amp;`。
        /// token 行号：`Cstrike_Terrorist_Forces` :85 / `Cstrike_CT_Forces` :86 / `Cstrike_VIP_Team` :87 /
        /// `Cstrike_Team_AutoAssign` :88 / `Cstrike_Menu_Spectate` :89 / `Cstrike_Cancel` :79。
        /// </summary>
        private static readonly string[] ButtonTexts =
        {
            "1 TERRORIST FORCES", "2 CT FORCES", "3 VIP", "5 AUTO ASSIGN", "6 SPECTATE", "0 CANCEL",
        };

        /// <summary>`.res` 的 `command` / `Command`（原版就是大小写混用：最后一个是 `"Command"`）。</summary>
        private static readonly string[] ButtonCommands =
        {
            "jointeam 1", "jointeam 2", "jointeam 3", "jointeam 5", "jointeam 6", "vguicancel",
        };

        /// <summary>`#Cstrike_Join_Team` = `SELECT TEAM`（`cstrike_english.txt:84`）。</summary>
        private const string TitleText = "SELECT TEAM";

        /// <summary>
        /// `MapInfo` 面板的正文 —— 逐字取自原版 `原版资源/cs16src/cs16game/app/cstrike/maps/de_dust2.txt`
        /// （原版 `HTML` 控件读的就是这个文件；`autoResize 3` 让框随文本调整）。
        /// </summary>
        private const string MapInfoText =
            "Dust II - Bomb/Defuse\n" +
            "*** GameHelper.com exclusive ***\n" +
            "by DaveJ (http://www.johnsto.co.uk/)\n" +
            "textures by MacMan (MacManInfi@aol.com)\n" +
            "\n" +
            "Counter-Terrorists: Prevent Terrorists\n" +
            "from bombing chemical weapon crates.\n" +
            "Team members must defuse any bombs\n" +
            "that threaten targeted areas.\n" +
            "\n" +
            "Terrorists: The Terrorist carrying the\n" +
            "C4 must destroy one of the chemical\n" +
            "weapon stashes.\n" +
            "\n" +
            "Other Notes: There are 2 chemical\n" +
            "weapon stashes in the mission.";

        /// <summary>AUTO ASSIGN（原版 `jointeam 5`）在本工程里按对半随机挑边（见 <see cref="OnAutoAssign"/>）。</summary>
        private const int AutoAssignTeams = 2;

        [SerializeField] private Text _titleLabel;
        [SerializeField] private Text _mapInfoText;
        /// <summary>6 个按钮的引用（`[SerializeField]` 才能进预制体 —— 数组元素是场景对象引用，可序列化）。</summary>
        [SerializeField] private Button[] _buttons;

        public override void BuildLayout(RectTransform root)
        {
            // 原版 `.res` 里**没有全屏压暗控件**（只有 Frame / SysMenu / MapInfo）⇒ 本面板不铺全屏 Backdrop。
            //
            // `TeamMenu` 是 VGUI `Frame`，它的底色取 scheme 默认的 `BgColor` → `ControlBG "0 0 0 0"`
            // （`clientscheme.res:38` + `:101`）⇒ **Frame 本身完全透明，没有黑板**。
            // ⛔ 这里原先铺的是 `WindowBG "0 0 0 200"`（scheme:41）—— 那是"文本编辑框 / 聊天"的底色，
            //    不是 Frame 的；用户看到的"凭空多出一整块黑板"就是它。**不要**再补任何自创底板/边框。
            // 这个节点仍要存在：它是 6 个按钮 / 标题 / MapInfo 的**坐标空间**（`.res` 的子控件坐标都相对它）。
            var frame = CsUiStyle.CreateBoxRect("TeamMenu", root,
                ResPos(FrameX, FrameY), ResSize(FrameW, FrameH), CsUiStyle.ControlBg, true);

            // ── joinTeam（标题，原版 font "Title" = Verdana Bold）──
            _titleLabel = CsUiStyle.CreateLabel("joinTeam", frame.rectTransform, TitleText,
                CsUiStyle.OriginalTitleFontSize, ResPos(TitleX, TitleY), ResSize(TitleW, TitleH),
                TextAnchor.MiddleLeft, CsUiStyle.Text);

            // ── 6 个按钮（黑底橙字、左对齐）──
            _buttons = new Button[ButtonNames.Length];
            for (var i = 0; i < ButtonNames.Length; i++)
            {
                _buttons[i] = CsUiStyle.CreateOriginalButton(ButtonNames[i], frame.rectTransform,
                    ButtonTexts[i], ResPos(BtnX, ButtonY[i]), ResSize(BtnW, BtnH), null);
            }

            // ── MapInfo（原版是 HTML；本工程没有 HTML 控件 ⇒ 用列表底 + 原文文本复刻同一块矩形）──
            var mapBox = CsUiStyle.CreateBoxRect("MapInfo", frame.rectTransform,
                ResPos(MapX, MapY), ResSize(MapW, MapH), CsUiStyle.ListBg);
            _mapInfoText = CsUiStyle.CreateLabel("MapInfoText", mapBox.rectTransform, MapInfoText,
                CsUiStyle.OriginalFontSize, new Vector2(8f, -8f), ResSize(MapW, MapH) - new Vector2(16f, 16f),
                TextAnchor.UpperLeft, CsUiStyle.Text);

            // 生成期（FlowSetup 造预制体）也设一次字体：动态字体不是工程资产、存不进 .prefab，
            // 因此 OnOpen 还会再设一次；这里只是为了运行期兜底搭布局那条路径不至于少字体。
            CsUiStyle.ApplyOriginalFonts(root);
            CsUiStyle.ApplyTitleFont(_titleLabel);
        }

        public override void OnOpen(object param)
        {
            if (_buttons == null || _buttons.Length != ButtonNames.Length)
            {
                Game.Logger.Warn(Tag,
                    $"选阵营面板的 {ButtonNames.Length} 个按钮引用不完整（预制体未由 FlowSetup 生成或已被改动），事件未绑定");
            }
            else
            {
                for (var i = 0; i < _buttons.Length; i++)
                {
                    var index = i;
                    Bind(_buttons[index], () => OnCommand(index), $"{ButtonNames[index]}（{ButtonCommands[index]}）");
                }
            }

            // 预制体里存不下动态字体（见 ApplyOriginalFonts 的说明）⇒ 每次打开都重设一遍。
            CsUiStyle.ApplyOriginalFonts(transform);
            CsUiStyle.ApplyTitleFont(_titleLabel);

            Game.Logger.Info(Tag, "选阵营面板已打开（原版 teammenu.res：6 项，命令 jointeam 1/2/3/5/6 + vguicancel）");
        }

        /// <summary>把原版 `.res` 的 `command` 派到本工程的事件上；每条命令都留痕（含未实现的那条）。</summary>
        private void OnCommand(int index)
        {
            var command = ButtonCommands[index];
            switch (command)
            {
                case "jointeam 1":
                    Choose(CsTeam.T, "TERRORIST FORCES");
                    break;

                case "jointeam 2":
                    Choose(CsTeam.CT, "CT FORCES");
                    break;

                case "jointeam 3":
                    // 原版 jointeam 3 = VIP 阵营（`cstrike_english.txt:87` 的 `&3 VIP`）。
                    // 本工程的阵营契约 `CsTeam` 只有 Spectator/T/CT（`Core/CsEnums.cs`），没有 VIP
                    // ⇒ 按 VIP 属 CT 一侧落到 CT，并留一条 Warn（登记在验收表「允许的差异」）。
                    Game.Logger.Warn(Tag,
                        "原版 jointeam 3 = VIP 阵营；本工程 CsTeam 契约无 VIP ⇒ 落到 CT 侧（VIP 模式未实现）");
                    Choose(CsTeam.CT, "VIP");
                    break;

                case "jointeam 5":
                    OnAutoAssign();
                    break;

                case "jointeam 6":
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
            var pick = Random.Range(0, AutoAssignTeams);
            var team = pick == 0 ? CsTeam.T : CsTeam.CT;
            Game.Logger.Info(Tag, $"选阵营: AUTO ASSIGN（原版按人数少的一方分配）→ {team}");
            Game.Event.Emit(Events.TeamChosen, team);
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
        /// ⚠️ `.res` 的 `ypos` 向下为正，而 <see cref="CsUiStyle.AnchoredTopLeft"/> 的 y 向上为正 ⇒ **y 取负**。
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
