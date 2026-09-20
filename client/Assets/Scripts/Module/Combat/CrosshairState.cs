using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 准星扩散状态（0~1），由"移动 / 离地 / 连射"驱动，**扩张快、回缩慢**。
    ///
    /// <para>数值由 <see cref="CombatModule"/> 每帧写进 <c>CsHudSnapshot.CrosshairSpread</c>
    /// （契约规定该字段归 Module/Combat 写），HUD（agent-06）只读。</para>
    ///
    /// <para><b>为什么不用真实射线夹角</b>：HUD 上的准星是 2D 的，"两线间距"只要单调、跟手、可读即可；
    /// 真正的散布角由 <see cref="Firearm.ComputeSpread"/> 按度算（两者量纲不同、都在各自的地方用）。</para>
    /// </summary>
    internal sealed class CrosshairState
    {
        /// <summary>当前扩散量（0 = 收拢，1 = 全开）。</summary>
        public float Value { get; private set; }

        public void Reset() => Value = 0f;

        /// <param name="dt">帧间隔。</param>
        /// <param name="local">本地玩家（null / 死亡时收拢）。</param>
        /// <param name="def">当前手持武器（null 时收拢）。</param>
        /// <param name="zoomed">是否开镜（开镜时几乎不散）。</param>
        public void Tick(float dt, CsActor local, CsWeaponDef def, bool zoomed)
        {
            if (dt <= 0f) return;

            var target = 0f;
            if (local != null && local.IsAlive && def != null)
            {
                var move = Firearm.MoveFactor(local) * CsCombatTuning.CrosshairMoveWeight;
                var shots = Mathf.Clamp01(local.ConsecutiveShots / (float)CsCombatTuning.CrosshairFullShots) *
                            CsCombatTuning.CrosshairShotWeight;
                var air = local.OnGround ? 0f : CsCombatTuning.CrosshairAirWeight;

                target = Mathf.Clamp01(move + shots + air);
                if (zoomed) target *= CsCombatTuning.CrosshairZoomScale;
            }

            var speed = target > Value ? CsCombatTuning.CrosshairExpandSpeed : CsCombatTuning.CrosshairShrinkSpeed;
            Value = Mathf.MoveTowards(Value, target, dt * speed);
        }
    }
}
