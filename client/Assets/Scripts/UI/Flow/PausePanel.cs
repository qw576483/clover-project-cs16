using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 暂停菜单（游戏内按 ESC）：Resume / Options / 换阵营 / Back to Main Menu（二次确认）/ Quit。
    ///
    /// <list type="bullet">
    /// <item><see cref="Layer"/> = <see cref="UILayer.Popup"/>：盖在 HUD 上并挡住下层的点击
    /// （引擎会为此层自动加遮罩）。</item>
    /// <item>真正的"暂停"（<c>_match.SetPaused</c>）由 Flow 在状态机切进/切出暂停族时做，
    /// 面板只负责发事件 —— UI 不许碰任何 Module。</item>
    /// <item>Options 用 <see cref="UILayer.Top"/> 的 <see cref="OptionsPanel"/>，
    /// 所以从暂停里进 Options 时它显示在本面板之上。</item>
    /// </list>
    /// </summary>
    public class PausePanel : CsPanelBase
    {
        private const float DialogWidth = 560f;
        private const float DialogHeight = 552f;
        private const float ButtonWidth = 400f;
        private const float ButtonHeight = 56f;

        /// <summary>按钮行之间的间距（同行内相邻行 y 差 = <see cref="ButtonHeight"/> + 它）。</summary>
        private const float RowGap = 14f;

        /// <summary>换阵营三个按钮的宽度 / 间隙（3×128 + 2×8 = 400 = 上面各行的按钮宽）。</summary>
        private const float TeamButtonWidth = 128f;
        private const float TeamButtonGap = 8f;

        // 对话框内各行以"框顶"为原点的 y（y 为负 = 向下）：换阵营那一行插在 Options 与 Back to Main Menu 之间。
        private const float RowResumeY = -128f;
        private const float RowOptionsY = -198f;
        private const float TeamLabelY = -268f;
        private const float TeamRowY = -300f;
        private const float RowBackToMainY = -370f;
        private const float RowQuitY = -440f;
        private const float HintY = -504f;

        [SerializeField] private Button _resumeButton;
        [SerializeField] private Button _optionsButton;
        [SerializeField] private Button _backToMainButton;
        [SerializeField] private Button _quitButton;
        [SerializeField] private Button _changeTeamCtButton;
        [SerializeField] private Button _changeTeamTButton;
        [SerializeField] private Button _changeTeamSpecButton;

        public override UILayer Layer => UILayer.Popup;

        public override void BuildLayout(RectTransform root)
        {
            // 引擎会给 Popup 层自动加半透明遮罩；这里再压一层让战场更暗一点，接近 CS 1.6 的暂停观感。
            CsUiStyle.CreateFullScreen("Backdrop", root, new Color(0f, 0f, 0f, 0.35f));

            var dialog = CsUiStyle.CreateBoxRect("Dialog", root, Vector2.zero,
                new Vector2(DialogWidth, DialogHeight), CsUiStyle.Box, true);
            var dialogRect = dialog.rectTransform;
            dialogRect.anchorMin = new Vector2(0.5f, 0.5f);
            dialogRect.anchorMax = new Vector2(0.5f, 0.5f);
            dialogRect.pivot = new Vector2(0.5f, 0.5f);
            dialogRect.anchoredPosition = Vector2.zero;
            dialogRect.sizeDelta = new Vector2(DialogWidth, DialogHeight);
            var box = dialogRect;

            CsUiStyle.CreateLabel("Title", box, "GAME PAUSED", 36, new Vector2(40f, -30f),
                new Vector2(DialogWidth - 80f, 48f), TextAnchor.MiddleCenter, CsUiStyle.Text);
            CsUiStyle.CreateLabel("Subtitle", box, "游戏已暂停", 20, new Vector2(40f, -78f),
                new Vector2(DialogWidth - 80f, 28f), TextAnchor.MiddleCenter, CsUiStyle.TextDim);

            _resumeButton = CsUiStyle.CreateButton("Btn_Resume", box, "Resume",
                new Vector2(80f, RowResumeY), new Vector2(ButtonWidth, ButtonHeight), null, accent: true);
            _optionsButton = CsUiStyle.CreateButton("Btn_Options", box, "Options",
                new Vector2(80f, RowOptionsY), new Vector2(ButtonWidth, ButtonHeight), null);

            CreateTeamRow(box);

            _backToMainButton = CsUiStyle.CreateButton("Btn_BackToMain", box, "Back to Main Menu",
                new Vector2(80f, RowBackToMainY), new Vector2(ButtonWidth, ButtonHeight), null);
            _quitButton = CsUiStyle.CreateButton("Btn_Quit", box, "Quit",
                new Vector2(80f, RowQuitY), new Vector2(ButtonWidth, ButtonHeight), null);

            CsUiStyle.CreateLabel("Hint", box, "再按一次 ESC 也能继续游戏", 18,
                new Vector2(40f, HintY), new Vector2(DialogWidth - 80f, 30f),
                TextAnchor.MiddleCenter, CsUiStyle.TextDim);
        }

        /// <summary>
        /// 「换阵营」行：一个标签 + CT / T / 观察者 三个按钮（取值与 H 菜单同一组）。
        ///
        /// <para>口径：<b>规格要求；原版 `GameMenu.res` 无此项</b> ——
        /// `策划/策划案/CS1.6单机参考规格.md` 的 M8 / §1:51 要求 ESC 菜单能换阵营，
        /// 而盘上 `原版资源/cs16src/cstrike/cstrike__resource__GameMenu.res` 的项只有
        /// `1 ResumeGame / 9 NewGame / 10 FindServers / 11 Options / 12 Quit`。</para>
        /// </summary>
        private void CreateTeamRow(RectTransform box)
        {
            CsUiStyle.CreateLabel("TeamLabel", box, "换阵营", 22, new Vector2(80f, TeamLabelY),
                new Vector2(ButtonWidth, 26f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            _changeTeamCtButton = CsUiStyle.CreateButton("Btn_TeamCT", box, "CT",
                new Vector2(80f, TeamRowY), new Vector2(TeamButtonWidth, ButtonHeight), null);
            _changeTeamTButton = CsUiStyle.CreateButton("Btn_TeamT", box, "T",
                new Vector2(80f + TeamButtonWidth + TeamButtonGap, TeamRowY),
                new Vector2(TeamButtonWidth, ButtonHeight), null);
            _changeTeamSpecButton = CsUiStyle.CreateButton("Btn_TeamSpec", box, "观察者",
                new Vector2(80f + (TeamButtonWidth + TeamButtonGap) * 2f, TeamRowY),
                new Vector2(TeamButtonWidth, ButtonHeight), null);
        }

        public override void OnOpen(object param)
        {
            Bind(_resumeButton, OnResume, "Resume");
            Bind(_optionsButton, OnOptions, "Options");
            Bind(_backToMainButton, OnBackToMainMenu, "Back to Main Menu");
            Bind(_quitButton, OnQuit, "Quit");

            // 预制体（`Assets/Resources/UI/PausePanel.prefab`）由 FlowSetup 生成，生成期就已带子节点 ⇒
            // 运行期不再走 BuildLayout；预制体里没有换阵营行时在这里按 BuildLayout 同款补建
            // 并把其下的行一起下移（同一套行 y 常量）⇒ 重跑生成器后两条路径收敛。
            if (_changeTeamCtButton == null || _changeTeamTButton == null || _changeTeamSpecButton == null)
                EnsureTeamRow();

            Bind(_changeTeamCtButton, () => OnChangeTeam(CsTeam.CT), "换阵营 CT");
            Bind(_changeTeamTButton, () => OnChangeTeam(CsTeam.T), "换阵营 T");
            Bind(_changeTeamSpecButton, () => OnChangeTeam(CsTeam.Spectator), "换阵营 观察者");
        }

        /// <summary>预制体缺换阵营行时按 <see cref="BuildLayout"/> 的取值补建，并把它下面的行移到新位置。</summary>
        private void EnsureTeamRow()
        {
            var box = _optionsButton != null ? _optionsButton.transform.parent as RectTransform : null;
            if (box == null)
            {
                Game.Logger.Warn(Tag, "暂停对话框节点取不到（Options 按钮引用缺失），换阵营按钮补建不了");
                return;
            }

            MoveRow(_backToMainButton, RowBackToMainY);
            MoveRow(_quitButton, RowQuitY);
            MoveRow(box.Find("Hint") as RectTransform, HintY);
            box.sizeDelta = new Vector2(DialogWidth, DialogHeight);

            CreateTeamRow(box);
            Game.Logger.Info(Tag, "预制体里没有换阵营行 → 已按 BuildLayout 同款在运行期补建");
        }

        /// <summary>把一行的 y 移到 <paramref name="y"/>（x 不动）。</summary>
        private static void MoveRow(Component row, float y)
        {
            var rt = row != null ? row.transform as RectTransform : null;
            if (rt != null) rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y);
        }

        private void OnResume()
        {
            Game.Logger.Info(Tag, "暂停菜单：继续游戏");
            Game.Event.Emit(Events.Resume);
        }

        private void OnOptions()
        {
            // Flow 记住"Options 是从暂停进来的"，返回时回到暂停站点而不是主菜单
            Game.Event.Emit(Events.OpenOptions);
        }

        /// <summary>
        /// 换阵营（规格 M8 / 策划案 §1:51 要求 ESC 菜单能换阵营；原版 `GameMenu.res` 无此项）。
        /// 与 H 菜单同一条通路：发 <see cref="Events.ChangeTeam"/>，由 MatchModule 落到比赛上。
        /// </summary>
        private void OnChangeTeam(CsTeam team)
        {
            Game.Logger.Info(Tag, $"暂停菜单：换阵营 → {team}");
            Game.Event.Emit(Events.ChangeTeam, team);
        }

        private void OnBackToMainMenu()
        {
            // 回主菜单 = 放弃当前比赛，必须二次确认（误点代价太大）
            Game.UI.Confirm("返回主菜单", "确定要退出当前比赛并返回主菜单吗？当前的比分与装备都会丢失。",
                OnConfirmedBackToMain, null, "返回主菜单", "取消");
        }

        private void OnConfirmedBackToMain()
        {
            Game.Logger.Info(Tag, "暂停菜单：确认退出比赛，回主菜单");
            Game.Event.Emit(Events.BackToMain);
        }

        private void OnQuit()
        {
            Game.Logger.Info(Tag, "暂停菜单：退出游戏");
            Game.Event.Emit(Events.QuitGame);
        }
    }
}
