using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 暂停菜单（游戏内按 ESC）—— 原版 GameUI 的 `GameMenu` 形态：
    /// 整屏半透明黑 + 居中一列纯文字菜单项（无底色、无边框，悬停时出现 `SelectionBG` 橙条）。
    ///
    /// <list type="bullet">
    /// <item>项与文案：`cstrike/resource/GameMenu.res` 的 `ResumeGame` / `Options` / `Disconnect` / `Quit`
    /// （`GameMenu.res:3-48`）；文案 = `#GameUI_GameMenu_*` 的英文原文。</item>
    /// <item>几何：项高 = `MenuItemHeight "28"`、项宽 = 字标画布宽 207、左内缩 = `TextInset "6"`，
    /// 均按 <see cref="CsUiStyle.ResScale"/> 换算到 1080p 画布。</item>
    /// <item><see cref="Layer"/> = <see cref="UILayer.Popup"/>：盖在 HUD 上并挡住下层点击。
    /// 真正的暂停（<c>_match.SetPaused</c>）由 Flow 在状态机切进/切出暂停族时做，面板只发事件。</item>
    /// </list>
    /// </summary>
    public class PausePanel : CsPanelBase
    {
        /// <summary>项宽 = 原版字标画布宽 207 × <see cref="CsUiStyle.ResScale"/>（与主菜单同一列宽）。</summary>
        private const float ItemWidth = 207f * CsUiStyle.ResScale;

        /// <summary>项高 = 原版 `InGameDesktop/MenuItemHeight "28"` × <see cref="CsUiStyle.ResScale"/>。</summary>
        private const float ItemHeight = 28f * CsUiStyle.ResScale;

        /// <summary>标题行高（本项目新增：原版该面板没有这一行标题）。</summary>
        private const float TitleHeight = 72f;

        /// <summary>标题与第一个菜单项之间的间距（本项目新增，理由同 <see cref="TitleHeight"/>）。</summary>
        private const float TitleGap = 12f;

        /// <summary>四项：继续 / 选项 / 断开（单机 = 回主菜单）/ 退出。</summary>
        private const int ItemCount = 4;

        private const float ColumnHeight = TitleHeight + TitleGap + ItemCount * ItemHeight;

        [SerializeField] private Button _resumeButton;
        [SerializeField] private Button _optionsButton;
        [SerializeField] private Button _disconnectButton;
        [SerializeField] private Button _quitButton;

        public override UILayer Layer => UILayer.Popup;

        public override void BuildLayout(RectTransform root)
        {
            // 原版菜单整屏底 = `Menu/BgColor "0 0 0 227"`（ClientScheme.res:48）。
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.MenuBg);

            // 居中列：用居中对齐的容器装，画布高度随窗口变化时仍保持在屏幕正中。
            var column = UIFactory.CreateNode("Column", root);
            column.anchorMin = new Vector2(0.5f, 0.5f);
            column.anchorMax = new Vector2(0.5f, 0.5f);
            column.pivot = new Vector2(0.5f, 0.5f);
            column.anchoredPosition = Vector2.zero;
            column.sizeDelta = new Vector2(ItemWidth, ColumnHeight);

            var title = CsUiStyle.CreateLabel("Title", column, "GAME PAUSED", 34,
                new Vector2(0f, 0f), new Vector2(ItemWidth, TitleHeight),
                TextAnchor.MiddleCenter, CsUiStyle.Text);
            title.fontStyle = FontStyle.Bold;

            var y = -(TitleHeight + TitleGap);
            _resumeButton = CsUiStyle.CreateMenuItem("Btn_Resume", column, "Resume Game",
                new Vector2(0f, y), new Vector2(ItemWidth, ItemHeight), null);
            y -= ItemHeight;
            _optionsButton = CsUiStyle.CreateMenuItem("Btn_Options", column, "Options",
                new Vector2(0f, y), new Vector2(ItemWidth, ItemHeight), null);
            y -= ItemHeight;
            _disconnectButton = CsUiStyle.CreateMenuItem("Btn_Disconnect", column, "Disconnect",
                new Vector2(0f, y), new Vector2(ItemWidth, ItemHeight), null);
            y -= ItemHeight;
            _quitButton = CsUiStyle.CreateMenuItem("Btn_Quit", column, "Quit",
                new Vector2(0f, y), new Vector2(ItemWidth, ItemHeight), null);
        }

        public override void OnOpen(object param)
        {
            CsUiStyle.ApplyOriginalFonts(transform);

            Bind(_resumeButton, OnResume, "Resume Game");
            Bind(_optionsButton, OnOptions, "Options");
            Bind(_disconnectButton, OnDisconnect, "Disconnect");
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

        /// <summary>
        /// 原版 `Disconnect` 项。单机版没有网络连接，语义是"放弃当前比赛、回主菜单"
        /// —— 落地事件用 <see cref="Events.BackToMain"/>（Flow 侧就是关比赛回主菜单站点）。
        /// </summary>
        private void OnDisconnect()
        {
            Game.UI.Confirm("Disconnect", "Abandon the current match and return to the main menu?",
                OnConfirmedDisconnect, null, "Disconnect", "Cancel");
        }

        private void OnConfirmedDisconnect()
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
