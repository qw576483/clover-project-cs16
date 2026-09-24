// ─────────────────────────────────────────────────────────────────────────────
// Cs16 · Core/CsClock.cs
// 本工程**唯一的时间入口**：绝对时间 <see cref="CsClock.Now"/> + 帧步长 <see cref="CsClock.Delta"/>。
//
//     · `CsMatch.SetPaused` 用 `Time.time` 算暂停补偿，而 `CsActor.NextFireTime` 用的是模拟时钟
//       ⇒ 换过时钟的自检/重放里"暂停一次白得一段冷却"（见 CsMatch.SetPaused 的注释）；
//     · `CombatModule.CanFire` 拿 `Time.time` 跟模拟写下的 `ReloadEndTime` 比 ⇒ 时钟一换就是
//       "准星能按但打不出"或"刚换完弹又能打"；
//       时间源可以换，但**换没换、换成谁，必须看得见**。
//
// 谁可以读 `UnityEngine.Time`：
//   只有本文件（+ 宿主 `Module/Match/MatchModule.Update` 的那一次 <see cref="Drive()"/> 调用）。
//   其余玩法代码一律 `CsClock.Now` / `CsClock.Delta`。判据见
//   自检项 `gameplay-no-wallclock`（可红可绿）。
//
// 默认值 = 引擎 Tick 的 dt（出处）：
//   引擎驱动器每帧 `Game.Tick(Time.deltaTime)`
//   （`clover-client-unity-engine/Runtime/Core/EngineRunner.cs:207`），
//   引擎 `Timer`/`Sync`/`Fsm` 收到的 dt 都是这一个值（同文件 750-766 行）。
//   ⇒ 未注入时 <see cref="Delta"/> 与引擎 Tick 的 dt 逐字相同（**不是另一个墙钟**：
//     `Time.time` 是 Unity 的会话游戏时间，被 `Time.timeScale` 缩放；这里只是把"取哪一个"
//     收敛成一个可替换的委托，不改默认取值）。
//
// 注入（离线驱动 / 自检）：
//   `CsClock.Inject(() => simTime, frameDt)` ⇒ `Now`/`Delta` 全部来自注入，**与墙钟彻底断开**；
//   `CsClock.Restore()` 撤销。两次动作都打 Info（含注入来源与次数），便于"这局到底用哪个时钟"。
//   注意：`CsMatch.Clock` 的默认值就是 `() => CsClock.Now`（代理到本入口），
//   所以只写 `CsClock.Inject(...)` 一处，模拟 / 战斗 / 玩家三侧同时被换掉。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using CloverEngine;
using UnityEngine;

namespace Cs16.Core
{
    /// <summary>
    /// 时间抽象：玩法只许从这里取时间，不许直接读 <see cref="Time"/>。
    ///
    /// <para><b>两个量</b>：<see cref="Now"/>（绝对时间，秒；与 <c>CsActor.NextFireTime</c> /
    /// <c>ReloadEndTime</c> / <c>FlashEndTime</c> 这些"绝对时间"字段同一口径）与
    /// <see cref="Delta"/>（本帧步长，秒）。</para>
    ///
    /// <para><b>三种来源，优先级从高到低</b>：
    /// ① <see cref="Inject"/> 注入（离线驱动 / 自检 —— 与墙钟无关）；
    /// ② 宿主每帧 <see cref="Drive()"/> 推进（实机主链路，值 = 引擎 Tick 的 dt）；
    /// ③ 兜底 = <see cref="DeltaSource"/>（默认 <c>Time.deltaTime</c>，只在宿主没驱动时用到，
    /// 例如编辑器菜单里直接跑的自检）。</para>
    ///
    /// <para><b>非线程安全</b>：主线程使用（与引擎 <c>Rng</c> / 本工程 <c>CsRng</c> 同约定）。</para>
    /// </summary>
    public static class CsClock
    {
        /// <summary>日志标签（与 <c>CsRng</c> 同口径：注入 / 撤销都留痕，不静默换时钟）。</summary>
        public const string Tag = "CsClock";

        /// <summary>
        /// 未注入、且宿主未驱动时的帧步长来源。默认 = **引擎 Tick 的 dt**
        /// （`Runtime/Core/EngineRunner.cs:207` 的 <c>Game.Tick(Time.deltaTime)</c>）。
        /// 换了默认值 = 换了语义，而不是换了名字。
        /// </summary>
        private static Func<float> _deltaSource = DefaultDelta;

        /// <summary>已注入的绝对时间源；<c>null</c> = 没注入（走宿主驱动 / 墙钟）。</summary>
        private static Func<float> _injectedNow;

        /// <summary>注入时一并给定的步长（与 <see cref="_injectedNow"/> 同生共死）。</summary>
        private static float _injectedDelta;

        /// <summary>步长是否来自注入（true ⇒ <see cref="Delta"/> 恒为 <see cref="_injectedDelta"/>）。</summary>
        private static bool _deltaInjected;

        /// <summary>宿主本帧交进来的 dt（<see cref="Drive()"/> 写入）。</summary>
        private static float _hostDelta;

        /// <summary>宿主有没有驱动过（没有 ⇒ <see cref="Delta"/> 退到 <see cref="_deltaSource"/>）。</summary>
        private static bool _hostDriven;

        /// <summary>"时钟已接入宿主"这条日志只打一次（每帧一条会刷屏）。</summary>
        private static bool _hostDriveLogged;

        private static int _injectCount;

        private static float DefaultNow() => Time.time;

        private static float DefaultDelta() => Time.deltaTime;

        /// <summary>绝对时间是否已注入（离线驱动 / 自检；false = 走宿主/墙钟）。</summary>
        public static bool IsInjected => _injectedNow != null;

        /// <summary>本进程注入过几次（证据用：>0 说明这一局的时钟不来自墙钟）。</summary>
        public static int InjectCount => _injectCount;

        /// <summary>
        /// 绝对时间源（读 = 当前生效的那个；写 = 注入/撤销，与 <see cref="Inject"/> 同义）。
        ///
        /// <para><c>CsMatch.Clock</c> 的默认值就是 <c>CsClock.Now</c>，所以这里换一次，
        /// 模拟侧跟着一起换（不要再单开第二个时钟钩子）。</para>
        /// </summary>
        public static Func<float> NowSource
        {
            get => _injectedNow;
            set
            {
                if (value == null)
                {
                    if (_injectedNow == null) return;
                    _injectedNow = null;
                    Game.Logger?.Info(Tag,
                        "[restore] 绝对时间源已撤销注入：回到宿主驱动（未驱动时 = 墙钟 Time.time）");
                    return;
                }

                _injectedNow = value;
                _injectCount++;
                Game.Logger?.Info(Tag,
                    $"[inject] 绝对时间源已注入（第 {_injectCount} 次）：Now 不再读墙钟 —— " +
                    "离线驱动 / 自检要的就是这条；重放请保持同一注入源与同一 Delta");
            }
        }

        /// <summary>
        /// 未注入时的帧步长来源（默认 <c>Time.deltaTime</c> = 引擎 Tick 的 dt）。
        /// 写 <c>null</c> 表示恢复默认。
        /// </summary>
        public static Func<float> DeltaSource
        {
            get => _deltaSource;
            set => _deltaSource = value ?? DefaultDelta;
        }

        /// <summary>
        /// 当前绝对时间（秒）。注入优先 → 否则墙钟 <c>Time.time</c>。
        /// </summary>
        public static float Now => _injectedNow != null ? _injectedNow() : Time.time;

        /// <summary>
        /// 本帧步长（秒）。优先级：注入的 Δ &gt; 宿主 <see cref="Drive()"/> 交进来的 dt &gt;
        /// <see cref="DeltaSource"/>。
        /// </summary>
        public static float Delta
        {
            get
            {
                if (_deltaInjected) return _injectedDelta;
                if (_hostDriven) return _hostDelta;
                return _deltaSource != null ? _deltaSource() : Time.deltaTime;
            }
        }

        /// <summary>
        /// 宿主每帧调一次：把**引擎 Tick 的 dt** 交给本抽象（值取自 <see cref="DeltaSource"/>，
        /// 默认 <c>Time.deltaTime</c>）。宿主 = <c>Module/Match/MatchModule.Update</c>（唯一调用点）。
        /// 首帧打一条 Info（证明时钟真的接上了，而不是"看起来有"）。
        /// </summary>
        public static void Drive() => Drive(_deltaSource != null ? _deltaSource() : Time.deltaTime);

        /// <summary>
        /// 宿主显式给定本帧 dt 的版本（离线宿主 / 驱动用；语义与 <see cref="Drive()"/> 完全一致）。
        /// </summary>
        public static void Drive(float engineTickDelta)
        {
            _hostDelta = engineTickDelta;
            _hostDriven = true;

            if (!_hostDriveLogged)
            {
                _hostDriveLogged = true;
                Game.Logger?.Info(Tag,
                    $"[drive] 时钟已接入宿主驱动：首帧 dt={engineTickDelta:F4}s（来源=引擎 Tick 的 dt；" +
                    "绝对时间默认 Time.time，见 EngineRunner.cs:207）");
            }
        }

        /// <summary>
        /// 注入时间源（离线驱动 / 自检的**唯一**入口）：此后 <see cref="Now"/> / <see cref="Delta"/>
        /// 全部来自注入，与墙钟彻底断开；直到 <see cref="Restore"/>。
        ///
        /// <para>与 <c>CsRng.InjectSeed</c> 同一条纪律：注入是**黏的**（不自动过期），
        /// 且必须留痕 —— "这局用的是假时钟"要能从日志看出来。</para>
        /// </summary>
        /// <param name="nowSource">绝对时间源（典型：<c>() =&gt; simTime</c>）。为 <c>null</c> 时等价于 <see cref="Restore"/>。</param>
        /// <param name="delta">同时冻结的帧步长（秒）。</param>
        public static void Inject(Func<float> nowSource, float delta)
        {
            if (nowSource == null)
            {
                Restore();
                return;
            }

            _injectedNow = nowSource;
            _injectedDelta = delta;
            _deltaInjected = true;
            _injectCount++;
            Game.Logger?.Info(Tag,
                $"[inject] 时间源已注入（第 {_injectCount} 次）：Now/Delta 全部来自注入（delta={delta:F4}s），" +
                "不再读墙钟 —— 同一注入 + 同一帧序 ⇒ 同一局");
        }

        /// <summary>
        /// 撤销注入，回到"宿主驱动 / 墙钟"（幂等；没注入过时不打日志也不报错）。
        ///
        /// <para>同时把"宿主已驱动"的痕迹清掉 ⇒ <see cref="Delta"/> 退到 <see cref="DeltaSource"/>。
        /// 实机里下一帧 <c>MatchModule.Update</c> 会把 dt 重新 <see cref="Drive()"/> 进来（同一个值），
        /// 所以这个复位对实机**零影响**；但它避免离线宿主 Restore 之后拿着一个上一帧的陈旧 dt。</para>
        /// </summary>
        public static void Restore()
        {
            if (_injectedNow == null && !_deltaInjected) return;

            _injectedNow = null;
            _injectedDelta = 0f;
            _deltaInjected = false;
            _hostDelta = 0f;
            _hostDriven = false;
            Game.Logger?.Info(Tag,
                "[restore] 时间源已恢复为宿主驱动（未驱动时 = 墙钟）：注入次数保留，" +
                "便于核对这一局用过哪个时钟");
        }
    }
}
