using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    /// **世界中的掉落武器视图**（差异 #75）：把地上的那把枪画出来。
    ///
    /// <para><b>模型来源 = 降级链，必须留在台账里</b>：原版掉落物用 <c>models/w_*.mdl</c>，
    /// 而**本机全盘 <c>.mdl</c> 计数 = 0**（<c>原版资源/</c> 下没有 <c>models/</c>，差异 #75/#1415 已登记）
    /// （同一批 CS 1.6 原始资产转出的 prefab，**不是**占位色块 / 内置几何体）。
    /// ⇒ 这条降级**要写进差异 #75 的正文**，别当它没发生。</para>
    ///
    /// <para><b>为什么没有 <c>Update</c></b>：掉落物是**静止**的（不自转，理由见
    /// <see cref="CsDroppedWeapon"/> 的类注释）；位置只在 <see cref="Bind"/> 时对一次。</para>
    /// </summary>
    public sealed class CsDroppedWeaponView : MonoBehaviour
    {
        /// <summary>绑定的权威数据（只读；拾取后 <c>Consumed=true</c>，由 ViewModule 回收本视图）。</summary>
        public CsDroppedWeapon Data { get; private set; }

        /// <summary>把视图摆到数据的位置与朝向（调用方已把 instance 放到世界根下）。</summary>
        public void Bind(CsDroppedWeapon data)
        {
            Data = data;
            transform.position = data.Position;
            transform.rotation = Quaternion.Euler(0f, data.YawDeg, 0f);
        }
    }
}
