using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 木箱碎块（原版 <c>TE_BREAKMODEL</c> 的 shard）的纯数据运动 + 寿命。
    ///
    /// <para><b>为什么不用 Rigidbody</b>：与 <c>GrenadeThrower</c> / <c>CombatEffects.TickShell</c>
    /// 同一条口径 —— 这些"表现体"不是模拟的一部分，刚体会把 PhysX 拉进表现层，
    /// 且它们的落地判定只是"别穿到地板下面去"。这里照 <c>TickShell</c> 的做法积分：
    /// 逐帧 <c>v -= g·dt</c>、位移做一次 <c>Physics.Linecast</c>，命中即贴到命中点并停止。</para>
    ///
    /// <para><b>重力</b>取 <see cref="CsConst.Gravity"/>（= 原版 <c>sv_gravity 800</c> × 0.0254），
    /// 与人物/手雷/弹壳同一条重力。寿命与初速见 <see cref="CsCombatTuning.BreakGibLife"/> /
    /// <see cref="CsCombatTuning.BreakGibSpeed"/>。</para>
    /// </summary>
    public sealed class CsBreakDebris : MonoBehaviour
    {
        private Vector3 _velocity;
        private float _life;
        private float _age;
        private bool _settled;
        private Mesh _ownedMesh;

        /// <summary>起飞：给初速、寿命与（运行时建出来、需要跟着销毁的）mesh。</summary>
        public void Launch(Vector3 velocity, float life, Mesh ownedMesh)
        {
            _velocity = velocity;
            _life = life > 0f ? life : 1f;
            _ownedMesh = ownedMesh;
        }

        private void Update()
        {
            var dt = Time.deltaTime;
            _age += dt;

            if (!_settled && _velocity.sqrMagnitude > 0f)
            {
                _velocity.y -= CsConst.Gravity * dt;
                var delta = _velocity * dt;
                var len = delta.magnitude;
                if (len > 1e-5f && Physics.Linecast(transform.position, transform.position + delta,
                        out var hit, ~0, QueryTriggerInteraction.Ignore))
                {
                    // 同 CombatEffects.TickShell：命中即贴到命中点并抬起一个壳半径，避免陷进面里
                    transform.position = hit.point + hit.normal * CsCombatTuning.ShellRadius;
                    _velocity = Vector3.zero;
                    _settled = true;
                }
                else
                {
                    transform.position += delta;
                }
            }

            if (_age >= _life) Expire();
        }

        private void Expire()
        {
            if (_ownedMesh != null) Destroy(_ownedMesh);
            Destroy(gameObject);
        }
    }
}
