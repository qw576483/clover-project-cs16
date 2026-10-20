using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 准星扩散状态（0~1），由**本发的真实散布角**驱动，**扩张快、回缩慢**。
    ///
    /// <para>数值由 <see cref="CombatModule"/> 每帧写进 <c>CsHudSnapshot.CrosshairSpread</c>
    /// （契约规定该字段归 Module/Combat 写），HUD 只读。</para>
    ///
    /// <para><b>与真实散布同源</b>：每帧取 <see cref="Firearm.ComputeSpread(CsActor, CsWeaponDef, bool)"/>
    /// 返回的度数（含移动 / 离地 / 连射累积 / 蹲姿 / 开镜各档），除以
    /// <see cref="CsCombatTuning.CrosshairMaxSpreadDegrees"/> 得到 0~1 ⇒ 准星张开多少就是子弹偏多少。
    /// 量纲只在这里换算一次，射击侧仍按度走 <see cref="Firearm.ApplySpread"/>。</para>
    ///
    /// <para><b>蹲姿另收一档</b>：真实模型里步枪族的蹲姿档与站姿同值（见
    /// <see cref="CsCombatTuning.CrosshairCrouchScale"/>），光换算度数看不出蹲下，所以蹲姿整体再乘一次
    /// <see cref="CsCombatTuning.CrosshairCrouchScale"/>；开镜同理乘
    /// <see cref="CsCombatTuning.CrosshairZoomScale"/>。</para>
    /// </summary>
    internal sealed class CrosshairState
    {
        /// <summary>当前扩散量（0 = 收拢，1 = 全开）。</summary>
        public float Value { get; private set; }

        /// <summary>本帧参与换算的真实散布角（度，供自检与取证读）。</summary>
        public float SpreadDegrees { get; private set; }

        public void Reset()
        {
            Value = 0f;
            SpreadDegrees = 0f;
        }

        /// <param name="dt">帧间隔。</param>
        /// <param name="local">本地玩家（null / 死亡时收拢）。</param>
        /// <param name="def">当前手持武器（null 时收拢）。</param>
        /// <param name="zoomed">是否开镜（开镜时几乎不散）。</param>
        public void Tick(float dt, CsActor local, CsWeaponDef def, bool zoomed)
        {
            if (dt <= 0f) return;

            var target = 0f;
            SpreadDegrees = 0f;
            if (local != null && local.IsAlive && def != null)
            {
                SpreadDegrees = SpreadDegreesFor(local, def, zoomed);
                target = Mathf.Clamp01(SpreadDegrees / CsCombatTuning.CrosshairMaxSpreadDegrees);
                if (local.IsCrouching) target *= CsCombatTuning.CrosshairCrouchScale;
                if (zoomed) target *= CsCombatTuning.CrosshairZoomScale;
            }

            var speed = target > Value ? CsCombatTuning.CrosshairExpandSpeed : CsCombatTuning.CrosshairShrinkSpeed;
            Value = Mathf.MoveTowards(Value, target, dt * speed);
        }

        /// <summary>
        /// 本帧的散布角（度）—— 与 <see cref="Firearm.ComputeSpread(CsActor, CsWeaponDef, bool)"/> 同一套参数，
        /// 只多一条 acc 兜底：**本梭还没开过火**（<c>AccuracyShots == 0</c>）时，把 acc 抬到该武器的 Deploy 复位值
        /// （<see cref="CsWeapons.ResetAccuracy"/>）。出生 / 回合开始的路径不回写 acc（只有切枪、换弹、卸装三处复位），
        /// 直接用 actor 上的 0 会把首发的散布放大十几倍、准星整局顶满。
        /// </summary>
        private static float SpreadDegreesFor(CsActor local, CsWeaponDef def, bool zoomed)
        {
            var acc = local.Accuracy;
            if (local.AccuracyShots == 0)
            {
                var reset = CsWeapons.ResetAccuracy(def);
                if (reset > acc) acc = reset;
            }

            return Firearm.ComputeSpread(def, Firearm.MoveFactor(local), local.OnGround, local.IsCrouching,
                local.ConsecutiveShots, zoomed, local.Silenced, local.BurstMode, acc, Firearm.SpeedUnits(local));
        }
    }
}
