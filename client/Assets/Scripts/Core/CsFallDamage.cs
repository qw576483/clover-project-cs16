namespace Cs16.Core
{
    /// <summary>
    /// 坠落伤害的取值口径（原版 GoldSrc <c>pm_shared.c</c> 的落地速度判定）。
    ///
    /// <para><b>原版公式</b>（出处 <c>原版资源/hlsdk/pm_shared/pm_shared.c:124-126</c>）：
    /// <code>
    /// #define PLAYER_FATAL_FALL_SPEED     1024
    /// #define PLAYER_MAX_SAFE_FALL_SPEED   580
    /// #define DAMAGE_FOR_FALL_SPEED (float)100 / (PLAYER_FATAL_FALL_SPEED - PLAYER_MAX_SAFE_FALL_SPEED)
    /// </code>
    /// 即 <c>伤害 = (落地瞬间的下坠速率 − 580) × 100/444</c>，速率的单位是 unit/s。</para>
    ///
    /// <para><b>本工程口径</b>：世界与速度用米 / 米每秒（<c>1 unit = 0.0254 m</c>，见
    /// <see cref="CsConst.UnitToMeter"/>）⇒ 两个阈值按同一折算落在
    /// <see cref="CsConst.FallSafeSpeed"/>（580 unit/s ⇒ 14.732 m/s）与
    /// <see cref="CsConst.FallFatalSpeed"/>（1024 unit/s ⇒ 26.0096 m/s），速率系数
    /// <see cref="CsConst.DamageForFallSpeed"/> 折算后 = 8.8671 伤害每 (m/s)。</para>
    ///
    /// <para><b>语义</b>：≤ 安全速度无伤；安全速度与致死速度之间按速率线性给；达到致死速度时公式
    /// 恰好给出 100 点 ⇒ 满血即死。落地**只结算一次**（在 <c>OnGround</c> 由假变真的那一帧，
    /// 原版消费完 <c>m_flFallVelocity</c> 后立即清零）。</para>
    /// </summary>
    public static class CsFallDamage
    {
        /// <summary>
        /// 落地伤害（未取整；&lt;= 0 = 无伤）。<paramref name="impactFallSpeed"/> = 落地瞬间的下坠速率
        /// （m/s，取正数）。
        /// </summary>
        public static float DamageFor(float impactFallSpeed)
        {
            if (impactFallSpeed <= CsConst.FallSafeSpeed) return 0f;
            return (impactFallSpeed - CsConst.FallSafeSpeed) * CsConst.DamageForFallSpeed;
        }
    }
}
