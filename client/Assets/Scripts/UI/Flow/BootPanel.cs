using System.Diagnostics;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 启动画面：Logo / 标题 / 版权字。
    /// <list type="bullet">
    /// <item>2 秒后自动推进（按**绝对** <c>Time.unscaledTime</c> 差值计时，暂停画面也能走时间）；</item>
    /// <item>任意键 / 鼠标键可跳过。</item>
    /// </list>
    ///
    /// <para>
    /// 推进方式是发 <see cref="Events.StartNewGame"/>：Flow 在 <c>Boot</c> 站点收到它表示
    /// "启动画面结束 → 主菜单"（同一事件在主菜单站点表示"开新游戏"，见 <c>AppFlow.OnStartNewGame</c>）。
    /// 事件名来自契约，不新增常量。
    /// </para>
    ///
    /// <para>
    /// <b>⛔ 这里刻意 <c>不用</c> 任何 Unity/引擎的"游戏时间"，只用 BCL 的
    /// <see cref="System.Diagnostics.Stopwatch"/>（真实墙钟）</b>。两个都试过，都被同一个现象毁掉：
    /// </para>
    /// <para>
    /// <b>现象（2026-09-21 实机实测，两条日志原文）</b>：进 Play 时 Unity 的帧时钟会在
    /// **第一帧之后**跳一大段 ——
    /// <c>[UI] 启动画面已开：本帧起停留 2s（基准 unscaledTime=0.000）</c>（12:16:46.467）
    /// → <c>[UI] 启动画面结束 → 主菜单（实际停留 8.225s）</c>（12:16:46.517）：
    /// **墙钟只过了 50 ms，而 <c>Time.unscaledTime</c> 跳了 8.225 s**。
    /// </para>
    /// <list type="number">
    /// <item><b>旧实现</b>（<c>Game.Timer.AfterUnscaled(2f, …)</c>）挂在这里：引擎 Timer 的 unscaled 条目按
    /// <c>Time.unscaledDeltaTime</c> **累加**（引擎件 <c>Runtime/Core/Timer.cs</c> 的
    /// <c>Tick</c>：<c>e.Elapsed += e.Unscaled ? UnscaledDeltaTime() : dt</c>）⇒ 那一跳把刚排下的
    /// 2 s 定时器**在第一次 Tick 就吃满**，启动画面只在屏上 33~37 ms（= 修前的实际表现）。</item>
    /// <item><b>改成"<c>OnOpen</c> 记 <c>Time.unscaledTime</c> 基准 + 每帧比差值"也不成立</b>（本片第一版）：
    /// 那一跳落在**第一帧与第二帧之间** ⇒ 基准取在第一帧（0.000）、第二帧就跳到 8.225 ⇒ 同样在 ~50 ms 内推进。</item>
    /// </list>
    /// <para>
    /// 结论：**Unity 的帧时钟在进 Play 的头两帧之间不连续**，拿它当"停留时长"的尺子怎么取基准都会被那一跳污染。
    /// 判据要的是"启动画面真的在屏上停留 2 秒"（出处：<c>策划/策划案/CS1.6单机参考规格.md:62</c>
    /// 「黑底 + Logo + 版权字，**几秒后**进主菜单」+ 本类 <see cref="AutoAdvanceDelay"/>），
    /// 那就该量**墙钟**。⇒ 用 <c>Stopwatch</c>：<c>OnOpen</c> 重启、每帧读 <c>Elapsed</c>。
    /// 它不受 timeScale、不受首帧跳变、不受帧率影响。
    /// </para>
    /// </summary>
    /// </summary>
    public class BootPanel : CsPanelBase
    {
        /// <summary>自动推进延时（秒）。</summary>
        private const float AutoAdvanceDelay = 2f;

        [SerializeField] private Text _title;
        [SerializeField] private Text _hint;

        private bool _advanced;
        /// <summary>停留时长的计时基准 = 真实墙钟（理由见类注释：Unity 帧时钟在进 Play 的头两帧之间不连续）。</summary>
        private readonly Stopwatch _stay = new Stopwatch();
        /// <summary>已经检查过"首帧间隔异常大"这件事（只报一次，避免刷屏）。</summary>
        private bool _frameGapChecked;

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
            _frameGapChecked = false;
            _stay.Restart();                       // 基准 = 打开这一刻的墙钟（见类注释）
            WarnIfNull(_title, "标题文本");
            WarnIfNull(_hint, "提示文本");

            Game.Logger.Info(Tag,
                $"启动画面已开：本帧起停留 {AutoAdvanceDelay:F0}s（墙钟计时，unscaledTime={Time.unscaledTime:F3}）");
        }

        public override void OnUpdate(float dt)
        {
            if (_advanced) return;

            // 非预期分支留痕（只报一次）：进 Play 的首帧把整段加载耗时算进了 unscaledDeltaTime，
            // 这正是旧实现"2s 定时器被一帧吃满"的那一帧。用绝对时间差值计时后它不再有害，但要看得见。
            if (!_frameGapChecked)
            {
                _frameGapChecked = true;
                var gap = Time.unscaledDeltaTime;
                if (gap > AutoAdvanceDelay)
                {
                    Game.Logger.Warn(Tag,
                        $"启动帧间隔异常：unscaledDeltaTime={gap:F3}s（> {AutoAdvanceDelay:F0}s）—— " +
                        "进 Play 的首帧把整段加载耗时算了进来；本面板按 unscaledTime 差值计时，不受它影响");
                }
            }

            var input = Game.Input;
            if (input != null &&
                (input.GetKeyDown(GameKey.Space) || input.GetKeyDown(GameKey.Enter) ||
                 input.GetKeyDown(GameKey.Escape) || input.GetMouseButtonDown(0) ||
                 input.GetMouseButtonDown(1)))
            {
                OnAutoAdvance();
                return;
            }

            if (_stay.Elapsed.TotalSeconds >= AutoAdvanceDelay) OnAutoAdvance();
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
            Game.Logger.Info(Tag,
                $"启动画面结束 → 主菜单（实际停留 {_stay.Elapsed.TotalSeconds:F3}s，墙钟）");
            Game.Event.Emit(Events.StartNewGame);
        }
    }
}
