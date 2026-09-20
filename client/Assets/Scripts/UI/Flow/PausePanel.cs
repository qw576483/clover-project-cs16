using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 暂停菜单（游戏内按 ESC）：Resume / Options / Back to Main Menu（二次确认）/ Quit。
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
        private const float DialogHeight = 480f;
        private const float ButtonWidth = 400f;
        private const float ButtonHeight = 56f;

        [SerializeField] private Button _resumeButton;
        [SerializeField] private Button _optionsButton;
        [SerializeField] private Button _backToMainButton;
        [SerializeField] private Button _quitButton;

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

            var y = -128f;
            _resumeButton = CsUiStyle.CreateButton("Btn_Resume", box, "Resume",
                new Vector2(80f, y), new Vector2(ButtonWidth, ButtonHeight), null, accent: true);
            y -= ButtonHeight + 14f;
            _optionsButton = CsUiStyle.CreateButton("Btn_Options", box, "Options",
                new Vector2(80f, y), new Vector2(ButtonWidth, ButtonHeight), null);
            y -= ButtonHeight + 14f;
            _backToMainButton = CsUiStyle.CreateButton("Btn_BackToMain", box, "Back to Main Menu",
                new Vector2(80f, y), new Vector2(ButtonWidth, ButtonHeight), null);
            y -= ButtonHeight + 14f;
            _quitButton = CsUiStyle.CreateButton("Btn_Quit", box, "Quit",
                new Vector2(80f, y), new Vector2(ButtonWidth, ButtonHeight), null);

            CsUiStyle.CreateLabel("Hint", box, "再按一次 ESC 也能继续游戏", 18,
                new Vector2(40f, -432f), new Vector2(DialogWidth - 80f, 30f),
                TextAnchor.MiddleCenter, CsUiStyle.TextDim);
        }

        public override void OnOpen(object param)
        {
            Bind(_resumeButton, OnResume, "Resume");
            Bind(_optionsButton, OnOptions, "Options");
            Bind(_backToMainButton, OnBackToMainMenu, "Back to Main Menu");
            Bind(_quitButton, OnQuit, "Quit");
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
