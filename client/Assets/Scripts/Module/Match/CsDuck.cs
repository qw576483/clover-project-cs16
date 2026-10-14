namespace Cs16.Module.Match
{
    /// <summary>
    /// 蹲姿的**过渡门**：碰撞盒什么时候从站立盒切到蹲姿盒。状态就是原版的两个量
    /// <c>flDuckTime</c>（毫秒倒计时）与 <c>FL_DUCKING</c>，落在
    /// <see cref="CsActor.DuckTimerMs"/> / <see cref="CsActor.HullCrouched"/> 上。
    ///
    /// <para><b>原版时序（逐条对应 <c>原版资源/hlsdk/pm_shared/pm_shared.c</c>）：</b></para>
    /// <list type="number">
    /// <item>按下蹲那一帧 <c>flDuckTime = 1000; bInDuck = true</c>（<c>:1708-1711</c>，
    /// 门条件 <c>nButtonPressed &amp; IN_DUCK &amp;&amp; !(flags &amp; FL_DUCKING)</c> ⇒ 已经是蹲姿盒时不重置）；</item>
    /// <item>每帧 <c>flDuckTime -= msec</c>、下限 0（<c>:2412-2419</c>）；</item>
    /// <item><c>flDuckTime / 1000 &lt;= 1 − TIME_TO_DUCK(0.4) = 0.6</c>（即**按下满 0.4 s**）或
    /// <c>onground == -1</c>（空中）时切盒：<c>usehull = 1</c> + <c>flags |= FL_DUCKING</c>（<c>:1718-1724</c>）；</item>
    /// <item>松开蹲键走 <c>PM_UnDuck</c>（<c>:1644-1672</c>）：头顶有空间时**立即** <c>usehull = 0</c> +
    /// <c>flags &amp;= ~FL_DUCKING</c> + <c>flDuckTime = 0</c>。</item>
    /// </list>
    ///
    /// <para><b>本类管的范围</b>：只有"碰撞盒是哪一个"这一个状态（消费点 = <see cref="CsActor.Height"/>）。
    /// 视高曲线不在本类（在 <c>Module/CameraRig</c>，同一 <c>TIME_TO_DUCK</c> 口径）；
    /// 蹲下时的移动倍率不在本类（原版 <c>:1702-1704</c> 的 ×0.333，本工程走
    /// <c>CsConst.SpeedCrouchMultiplier</c>）。</para>
    ///
    /// <para><b>头顶空间</b>：<c>PM_UnDuck</c> 的 <c>PM_PlayerTrace</c>（<c>:1644-1656</c>）由调用方以
    /// <c>headroom</c> 委托传入 —— 本工程复用移动解算同一个 <c>ICsMap.CanStand</c>（它查的就是**站立**身高带）。</para>
    /// </summary>
    internal static class CsDuck
    {
        /// <summary>蹲下过渡时长（秒）= 原版 <c>TIME_TO_DUCK</c>（pm_shared.c:82）。</summary>
        public const float TimeToDuck = 0.4f;

        /// <summary>按下蹲时倒计时初值（毫秒）= 原版 <c>flDuckTime = 1000</c>（pm_shared.c:1710）。</summary>
        public const float DuckTimeInitialMs = 1000f;

        /// <summary>切盒门限 = 原版 <c>1.0 − TIME_TO_DUCK</c>（= 0.6）：判据是
        /// <c>flDuckTime / 1000 &lt;= 本值</c>（pm_shared.c:1718）。</summary>
        public const float HullSwitchRatio = 1f - TimeToDuck;

        /// <summary>
        /// 推进一个参与者的蹲姿门（原版 <c>PM_Duck</c> + <c>PM_UnDuck</c>）。
        /// </summary>
        /// <param name="a">被推进的参与者（写 <c>DuckTimerMs</c> / <c>HullCrouched</c> / <c>DuckKeyHeld</c>）。</param>
        /// <param name="crouchHeld">本帧是否按住蹲（原版 <c>cmd.buttons &amp; IN_DUCK</c>）。</param>
        /// <param name="onGround">本帧是否踩地（原版 <c>pmove-&gt;onground != -1</c>）。</param>
        /// <param name="dt">本帧时长（秒）。</param>
        /// <param name="headroom">头顶空间判据（原版 <c>PM_UnDuck</c> 的 <c>PM_PlayerTrace</c>，<c>:1644-1656</c>）：
        /// 把**站立**身体盒摆回该参与者当前脚下位置是否放得下。返回 false ⇒ 保持蹲姿盒、下一帧继续试。
        /// 传 null = 不做该项检查（一律可起身）—— 自测 / 无地图路径用。</param>
        public static void Step(CsActor a, bool crouchHeld, bool onGround, float dt,
                                System.Func<CsActor, bool> headroom = null)
        {
            if (a == null) return;
            if (dt < 0f) dt = 0f;

            var wasHeld = a.DuckKeyHeld;
            a.DuckKeyHeld = crouchHeld;

            if (!crouchHeld)
            {
                // PM_UnDuck（:1644-1672）：先看头顶 —— trace.startsolid ⇒ usehull 保持 1（继续蹲着）。
                if (headroom != null && !headroom(a)) return;

                // 头顶放得下：立即回站立盒、倒计时清零。
                a.HullCrouched = false;
                a.DuckTimerMs = 0f;
                return;
            }

            if (!wasHeld && !a.HullCrouched)
            {
                // 刚按下、且当前还不是蹲姿盒 ⇒ 重置倒计时（本帧不递减：原版递减在 PM_Duck 之前）。
                a.DuckTimerMs = DuckTimeInitialMs;
            }
            else if (a.DuckTimerMs > 0f)
            {
                a.DuckTimerMs -= dt * 1000f;
                if (a.DuckTimerMs < 0f) a.DuckTimerMs = 0f;
            }

            if (!a.HullCrouched &&
                (a.DuckTimerMs / 1000f <= HullSwitchRatio || !onGround))
            {
                a.HullCrouched = true;
            }
        }
    }
}
