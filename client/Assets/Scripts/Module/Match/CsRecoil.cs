using Cs16.Core;

namespace Cs16.Module.Match
{
    /// <summary>逐发后坐力状态（原版 <c>punchangle</c> + 水平方向位）。</summary>
    public struct CsRecoilState
    {
        /// <summary>抬升（度，正 = 准星向上）。</summary>
        public float Pitch;

        /// <summary>水平偏移（度，正 = 向右）。</summary>
        public float Yaw;

        /// <summary>原版 <c>m_iDirection</c>：水平偏移当前往哪一侧累加（+1 / -1）。</summary>
        public int Direction;
    }

    /// <summary>
    /// 逐发后坐力的**唯一**解算。首发（<c>CsInventory.TryDischarge</c>）与连发续发
    /// （<c>CsInventory.TickBurst</c>）都走这里 —— 两处各写一份必然算得不一样。
    ///
    /// <para><b>机制出处</b>（机制形状）= <c>原版资源/hlsdk/dlls/weapons.cpp:534-571</c> 的
    /// <c>CBasePlayerWeapon::KickBack</c>：</para>
    /// <list type="number">
    /// <item>首发 <c>flFront = up_base</c> / <c>flSide = lateral_base</c>；第 2 发起
    /// <c>flFront = shots · up_modifier + up_base</c>、<c>flSide = shots · lateral_modifier + lateral_base</c>
    /// （<c>shots</c> = 本梭已发数，从 1 起）；</item>
    /// <item>抬升**累加**并按 <c>up_max</c> 夹住（原版 <c>punchangle.x -= flFront</c> 后
    /// <c>if (punchangle.x &lt; -up_max) = -up_max</c>；本工程的 <c>Pitch</c> 以"向上为正"，
    /// 故等价于 <c>Pitch += flFront</c> 再夹到 <c>up_max</c>）；</item>
    /// <item>水平偏移**按当前方向位累加**并按 <c>lateral_max</c> 夹住（不是每发换向）；</item>
    /// <item>每发以 <c>1/(direction_change+1)</c> 的概率翻转方向位。</item>
    /// </list>
    ///
    /// <para><b>参数出处</b> = <c>策划/手感参数对照.md</c> §5.5，落在
    /// <see cref="CsWeaponDef.HasKickback"/> 为真的那些武器上。没有出处的武器走
    /// <see cref="CsWeaponDef.RecoilVert"/> / <see cref="CsWeaponDef.RecoilHoriz"/> 的既有模型。</para>
    ///
    /// <para><b>未取到的一项</b>：原版方向位的**初始值**（<c>m_iDirection</c> 初值）本片没有取证，
    /// 按 +1 起（只影响水平往左还是往右先偏，不影响抬升）。</para>
    /// </summary>
    public static class CsRecoil
    {
        /// <summary>
        /// 打一发。<paramref name="shotsFired"/> = 本梭已发数（首发传 1）；
        /// <paramref name="flipRoll"/> = [0,1) 的随机数（只用于方向翻转）；
        /// <paramref name="lateralRoll"/> = [-1,1] 的随机数（只用于没有 kickback 出处的旧模型）。
        /// </summary>
        public static CsRecoilState Apply(CsRecoilState s, CsWeaponDef def, int shotsFired,
                                          float flipRoll, float lateralRoll)
        {
            if (def == null) return s;

            if (!def.HasKickback)
            {
                // 没有出处 ⇒ 沿用既有模型（每发固定度数、水平每发独立抽样）。
                s.Pitch += def.RecoilVert;
                s.Yaw += lateralRoll * def.RecoilHoriz;
                return s;
            }

            float front, side;
            if (shotsFired <= 1)
            {
                front = def.KickUpBase;
                side = def.KickLateralBase;
            }
            else
            {
                front = shotsFired * def.KickUpModifier + def.KickUpBase;
                side = shotsFired * def.KickLateralModifier + def.KickLateralBase;
            }

            s.Pitch += front;
            if (s.Pitch > def.KickUpMax) s.Pitch = def.KickUpMax;

            if (s.Direction >= 0)
            {
                s.Yaw += side;
                if (s.Yaw > def.KickLateralMax) s.Yaw = def.KickLateralMax;
            }
            else
            {
                s.Yaw -= side;
                if (s.Yaw < -def.KickLateralMax) s.Yaw = -def.KickLateralMax;
            }

            // 原版 `if (!RANDOM_LONG(0, direction_change)) m_iDirection = !m_iDirection;`
            // ⇒ 翻转概率 = 1 / (direction_change + 1)。
            var flipChance = 1f / (def.KickDirectionChange + 1);
            if (flipRoll < flipChance) s.Direction = -s.Direction;

            return s;
        }
    }
}
