using CloverEngine;
using Cs16.Module.Combat;

namespace Cs16.Module.Player
{
    /// <summary>
    /// **本类已降级为引擎 `LogThrottle` 的薄适配**（per-tag 转发）—— 计数口径的唯一真相包在引擎
    /// <c>Runtime/Core/LogThrottle.cs</c>（`ShouldLogEvery` / `InfoCounted` / `WarnCounted` / `ErrorCounted`）。
    ///
    /// <para><b>出处 / 为什么</b>：原先本类自己持一张 `Dictionary&lt;string,int&gt;` 计数表 + `switch` 分发，
    /// 而那套"每 key 计数、首次必打、之后每 N 次打一条"的口径是**任何项目都要的底座**
    /// 本类只保留"每个调用方一个 tag"这一层 —— tag 是业务约定，不该塞进引擎。
    /// 调用面（类名 / 命名空间 / 公开签名 / tag 实例）**一个都没动**，12 个调用方无需改一行。</para>
    ///
    /// <para><b>口径</b>：每个 key **首次必打**，之后**每 <see cref="LogRateEvery"/> 次打一条**
    /// （并标注"同类第 N 次"）—— 该判定由引擎 <c>LogThrottle.ShouldLogEvery</c> 做，本类只传
    /// <see cref="LogRateEvery"/>。</para>
    ///
    /// <para><b>线程</b>：只在主线程使用（Unity 主循环），引擎那侧同样如此、不加锁。</para>
    /// </summary>
    internal sealed class CsModuleLog
    {
        /// <summary>同类日志的打点间隔（首次之后每 N 次打一条）。业务侧数值，属 <c>CsCombatTuning</c>。</summary>
        public const int LogRateEvery = CsCombatTuning.LogRateEvery;

        private readonly string _tag;

        public CsModuleLog(string tag)
        {
            _tag = tag;
        }

        /// <summary>打一条（降频的）Info。</summary>
        public void Info(string key, string msg) => LogThrottle.InfoCounted(_tag, key, msg, LogRateEvery);

        /// <summary>打一条（降频的）Warn。</summary>
        public void Warn(string key, string msg) => LogThrottle.WarnCounted(_tag, key, msg, LogRateEvery);

        /// <summary>打一条（降频的）Error。</summary>
        public void Error(string key, string msg) => LogThrottle.ErrorCounted(_tag, key, msg, LogRateEvery);

        /// <summary>
        /// **不降频**的 Info —— 只在"每次发生都值得看见"的低频事件上用
        /// （命中、开局、设置变更这类）。等价于引擎 `Game.Logger.Info(tag, msg)`，不重复造。
        /// </summary>
        public void Always(string msg) => Game.Logger.Info(_tag, msg);

        /// <summary>
        /// 把限频记录清掉（重新开局时调用，让"首次"重新生效）—— 转引擎 `LogThrottle.Reset()`，
        /// 它清空**所有**限频记录（时间口径 + 计数口径）。
        /// </summary>
        public void Reset() => LogThrottle.Reset();
    }
}
