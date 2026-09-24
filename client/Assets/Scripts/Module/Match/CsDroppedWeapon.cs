using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// **世界中的掉落武器**（差异 #75「掉落的枪不在世界里」）。
    ///
    /// <para><b>为什么要有这个类</b>：在这之前 <see cref="CsInventory.DropWeapon"/> 只从库存里摘字段
    /// （<c>PrimaryWeapon</c> / <c>SecondaryWeapon</c>），世界侧**什么都不生成** ⇒ 玩家看到的
    /// 「枪也不在地上」是**结构性未做**，不是掉落逻辑写错（这是差异 #75 的原始判定）。
    /// 本类就是那个一直缺席的「世界中的武器」对象。</para>
    ///
    /// <para><b>拾取口径</b>：与原版一致 = **走到 1.2 m 内自动拾取**（<see cref="CsMatchConst.PickupRadius"/>，
    /// 与 <c>CsBomb.TryPickupDropped</c> 的 C4 走**同一条口径、同一个常量** —— 见 <c>CsMatch</c> 里
    /// 「原版口径 = 走到 1.2m 内自动拾取」那行注释）。不额外要求按键 / 瞄准。</para>
    ///
    /// <para><b>刻意不做的两件</b>：① **不自转** —— 差异 #75 的「何时消除」列写了「绕 Y 轴微转」，
    /// ② **C4 不走这里** —— C4 的世界掉落由 <c>CsBomb</c> 链（<c>OnCarrierLost</c> +
    /// <c>TryPickupDropped</c>）负责，本类只承载武器。</para>
    /// </summary>
    public sealed class CsDroppedWeapon
    {
        /// <summary>武器 id（<see cref="CsWeapons"/> 口径，如 <c>weapon_ak47</c>）。</summary>
        public string WeaponId;

        /// <summary>掉落者阵营（只用来选表现层预制体目录 <c>Art/T</c> 或 <c>Art/CT</c>；原版世界模型不分阵营）。</summary>
        public CsTeam Team;

        /// <summary>世界坐标（掉落瞬间的角色位置）。</summary>
        public Vector3 Position;

        /// <summary>水平朝向（度，0=+Z），取掉落瞬间的 <see cref="CsActor.Yaw"/>。</summary>
        public float YawDeg;

        /// <summary>掉落时弹匣内余弹（拾取后原样回到拾取者手上，不是"满弹"）。</summary>
        public int MagAmmo;

        /// <summary>掉落时备弹。</summary>
        public int ReserveAmmo;

        /// <summary>掉落时刻（<c>CsMatch.Now</c>）。</summary>
        public float DroppedAt;

        /// <summary>已被拾取。被拾取的项会**立刻从** <c>CsMatch.DroppedWeapons</c> 里摘掉。</summary>
        public bool Consumed;
    }
}
