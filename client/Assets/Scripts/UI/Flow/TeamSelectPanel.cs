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
    ///
    /// <para>
    /// ⛔ <b>`MapInfo` 那一块整块删掉（2026-09-20，用户要求）</b>：它原先复刻的是原版
    /// <c>maps/de_dust2.txt</c> 的正文，里面带三行**外部站点署名**
    /// （`*** GameHelper.com exclusive ***` / `by DaveJ (http://www.johnsto.co.uk/)` /
    /// `textures by MacMan (MacManInfi@aol.com)`）—— 用户看到后明确要求"内容全部去掉，
    /// 只保留一个 by clover-engine"。⇒ 本面板现在只留：标题 + 6 个原版按钮 + 底部居中
    /// <c>by clover-engine</c>（skill §8 的品牌行）。⛔ 不要再把那段正文加回来。
    /// </para>
    /// </summary>
    public class TeamSelectPanel : CsPanelBase
    {
        // ─────────── 原版 teammenu.res 的坐标（设计空间 640×480，单位 = 设计像素）───────────
        /// <summary>`TeamMenu`（Frame）：`.res:7-10`。</summary>
        private const float FrameX = 76f, FrameY = 0f, FrameW = 552f, FrameH = 448f;
        /// <summary>`joinTeam`（Label，`font "Title"`，`textAlignment west`）：`.res:49-63`。</summary>
        private const float TitleX = 0f, TitleY = 22f, TitleW = 500f, TitleH = 48f;
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

        /// <summary>AUTO ASSIGN（原版 `jointeam 5`）在本工程里按对半随机挑边（见 <see cref="OnAutoAssign"/>）。</summary>
        private const int AutoAssignTeams = 2;

        /// <summary>底部署名（skill §8 品牌硬约定：**逐字** `by clover-engine`）。</summary>
        private const string SignatureText = "by clover-engine";

        [SerializeField] private Text _titleLabel;
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
            // 这个节点仍要存在：它是 6 个按钮 / 标题的**坐标空间**（`.res` 的子控件坐标都相对它）。
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

            // ── 署名（skill §8 品牌硬约定）──
            // ⛔ 这一块**原先**是原版 `MapInfo`（HTML 控件）的复刻：一块列表底 + 原版 `maps/de_dust2.txt`
            //    的正文。用户 2026-09-20 明确要求把那段正文（含 GameHelper.com / johnsto.co.uk / aol.com
            //    三行外部站点署名）**整块去掉**，只留 `by clover-engine` ⇒ 现在这里就是那一行。
            // 必须用底部锚点：左上锚点 + 大负 y 会随画布高度变化掉出屏幕（MainMenuPanel 实测踩过）。
            CsUiStyle.CreateBottomLabel("Signature", root, SignatureText, 22,
                new Vector2(0f, 56f), new Vector2(600f, 32f));

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
