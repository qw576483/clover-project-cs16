using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 启动画面：Logo / 标题 / 版权字。
    /// <list type="bullet">
    /// <item>2 秒后自动推进（<c>AfterUnscaled</c>，暂停画面也能走时间）；</item>
    /// <item>任意键 / 鼠标键可跳过。</item>
    /// </list>
    ///
    /// <para>
    /// 推进方式是发 <see cref="Events.StartNewGame"/>：Flow 在 <c>Boot</c> 站点收到它表示
    /// "启动画面结束 → 主菜单"（同一事件在主菜单站点表示"开新游戏"，见 <c>AppFlow.OnStartNewGame</c>）。
    /// 事件名来自契约，不新增常量。
    /// </para>
    /// </summary>
    public class BootPanel : CsPanelBase
    {
        /// <summary>自动推进延时（秒）。</summary>
        private const float AutoAdvanceDelay = 2f;

        [SerializeField] private Text _title;
        [SerializeField] private Text _hint;

        private long _timerId = -1;
        private bool _advanced;

        public override void BuildLayout(RectTransform root)
        {
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);
            CsUiStyle.CreateBoxRect("AccentBar", root, new Vector2(166f, -300f), new Vector2(560f, 4f), CsUiStyle.Accent);

            _title = CsUiStyle.CreateLabel("Title", root, "COUNTER-STRIKE", 84,
                new Vector2(160f, -200f), new Vector2(1400f, 100f), TextAnchor.MiddleLeft, CsUiStyle.Text);
            CsUiStyle.CreateLabel("Subtitle", root, "1.6   ·   单机版 / Standalone", 36,
                new Vector2(166f, -320f), new Vector2(1400f, 56f), TextAnchor.MiddleLeft, CsUiStyle.Accent);
            _hint = CsUiStyle.CreateLabel("Hint", root, "按任意键开始 / Press any key", 22,
                new Vector2(166f, -860f), new Vector2(900f, 40f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);
            CsUiStyle.CreateLabel("Copyright", root,
                "学习用单机复刻工程；Counter-Strike 为 Valve Corporation 的商标，本项目与 Valve 无关。", 18,
                new Vector2(60f, -1035f), new Vector2(1700f, 30f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);
        }

        public override void OnOpen(object param)
        {
            _advanced = false;
            WarnIfNull(_title, "标题文本");
            WarnIfNull(_hint, "提示文本");

            _timerId = Game.Timer.AfterUnscaled(AutoAdvanceDelay, OnAutoAdvance);
        }

        public override void OnUpdate(float dt)
        {
            if (_advanced) return;

            var input = Game.Input;
            if (input == null) return;

            if (input.GetKeyDown(GameKey.Space) || input.GetKeyDown(GameKey.Enter) ||
                input.GetKeyDown(GameKey.Escape) || input.GetMouseButtonDown(0) ||
                input.GetMouseButtonDown(1))
            {
                OnAutoAdvance();
            }
        }

        public override void OnClose()
        {
            if (_timerId < 0) return;
            Game.Timer.Stop(_timerId);
            _timerId = -1;
        }

        private void OnAutoAdvance()
        {
            if (_advanced) return;

            // 面板可能已经被 Flow 关掉（场景切换等），此时绝不能再发"开始"事件，
            // 否则会在主菜单站点被解释成"点 New Game"。
            if (!Game.UI.IsOpen<BootPanel>())
            {
                _advanced = true;
                Game.Logger.Info(Tag, "启动画面已关闭，忽略自动推进");
                return;
            }

            _advanced = true;
            Game.Logger.Info(Tag, "启动画面结束 → 主菜单");
            Game.Event.Emit(Events.StartNewGame);
        }
    }
}
