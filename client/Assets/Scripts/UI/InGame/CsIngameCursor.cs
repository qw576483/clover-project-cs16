using CloverEngine;
using UnityEngine;

namespace Cs16.UI
{
    /// <summary>
    /// 游戏内"需要点鼠标"的面板打开时的光标管理（买枪菜单 / H 菜单 / 控制台 / 比赛结算）。
    ///
    /// <para><b>为什么这件事非得 UI 做</b>：第一人称操作模块（<c>Module/Player</c>）活着的时候会把光标
    /// 锁在屏幕中心，此时 uGUI 的 <c>GraphicRaycaster</c> 只能命中屏幕正中那个点 ——
    /// 表现就是"菜单能开、按钮点不动"。而 UI 又不许引用 Module（分层铁律），
    /// 无法反向告诉操作模块"我现在要鼠标"。</para>
    ///
    /// <para><b>做法</b>：面板打开时解锁 + 显示光标；**最后一个**需要鼠标的面板关掉时才重新锁回去。
    /// 用 <see cref="_unlockedByUi"/> 记住"是我们解的锁"，避免和别人的光标管理互相打架
    /// （不是我们解的就绝不替别人锁）。</para>
    ///
    /// <para><b>交给下一棒</b>：若 Module/Player 之后自己接管光标（例如只在 Live 阶段锁定），
    /// 请让它与这里的语义对齐：<b>任何 UI 面板开着时不要抢锁</b>。
    /// </para>
    /// </summary>
    public static class CsIngameCursor
    {
        private const string Tag = "UI";

        private static bool _unlockedByUi;

        /// <summary>打开需要鼠标的面板：解锁并显示光标（幂等）。</summary>
        public static void UnlockForMenu(string who)
        {
            if (!_unlockedByUi)
            {
                Game.Logger?.Info(Tag, $"解锁鼠标光标（{who}）：菜单需要点击，锁定状态下按钮点不动");
                _unlockedByUi = true;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>
        /// 面板关闭时调用：仅当没有其它需要鼠标的面板还开着，才把光标锁回屏幕中心（进入可操作状态）。
        /// </summary>
        /// <param name="anyMenuStillOpen">是否还有需要鼠标的面板开着（调用方用 <c>Game.UI.IsOpen&lt;T&gt;</c> 判断）。</param>
        public static void RelockIfIdle(string who, bool anyMenuStillOpen)
        {
            if (anyMenuStillOpen) return;
            if (!_unlockedByUi) return;

            _unlockedByUi = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            Game.Logger?.Info(Tag, $"重新锁定鼠标光标（{who} 关闭，回到游戏内锁定状态）");
        }

        /// <summary>游戏内是否还有"需要鼠标"的面板开着（买枪 / H 菜单 / 控制台 / 比赛结算）。</summary>
        public static bool AnyMenuOpen()
        {
            var ui = Game.UI;
            if (ui == null) return false;
            return ui.IsOpen<BuyMenuPanel>() || ui.IsOpen<HMenuPanel>() || ui.IsOpen<ConsolePanel>()
                   || ui.IsOpen<MatchEndPanel>();
        }
    }
}
