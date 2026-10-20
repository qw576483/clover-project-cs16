using System.Collections.Generic;
using Cs16.Core;
using Cs16.Module.Audio;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 手雷投掷的**视觉飞行体**。
    ///
    /// <para><b>分工</b>（这是与比赛模拟的关键约定）：手雷的飞行、引信、爆炸伤害**全部在比赛模拟里**
    /// （<c>CsInventory.ThrowGrenade / TickProjectiles / Detonate</c> 已实现），本类**不做任何伤害结算**，
    /// 只按**同一套常量**（<see cref="CsConst.GrenadeThrowSpeed"/> / <see cref="CsConst.Gravity"/> /
    /// <see cref="CsConst.GrenadeFuse"/>）积分出一个"看得见的手雷"，让玩家能看见它飞、落在哪。</para>
    ///
    /// <para><b>为什么不用 Rigidbody</b>：模拟里那颗手雷是无刚体的纯数据体。再挂一个 Rigidbody
    /// 等于同一颗手雷有两套物理，两者会在撞墙/落地时越差越远 —— 玩家看到的爆炸位置就与真实伤害位置不符
    /// （"明明在拐角外炸的，我却死了"）。逐帧用同样的公式积分，视觉与判定才一致。</para>
    /// </summary>
    internal sealed class GrenadeThrower
    {
        /// <summary>场上同时存在的视觉手雷上限（防呆：手雷数量本来就有限）。</summary>
        private const int MaxFlights = 8;

        /// <summary>
        /// 烟球**收尾**时长（秒）：烟球剩余时间进入这一段后，直径按时长线性缩到 0。
        /// 原版服务端只发 <c>TE_SMOKE</c> 的几档 scale（15.0 主档 + 3.5 / 2.5 收尾档），
        /// 逐档时长在客户端且载体没给 ⇒ 这里取一个**显示用**的收尾段（不参与遮挡判定）。
        /// </summary>
        private const float SmokeDissipateSeconds = 2f;

        /// <summary>闪光弹在爆点的白光（与 <c>CsHudSnapshot.FlashAlpha</c> 的全屏白同源）。</summary>
        private static readonly Color FlashBlastColor = new Color(0.92f, 0.96f, 1f, 1f);

        private sealed class Flight
        {
            public GameObject Go;
            public Vector3 Position;
            public Vector3 Velocity;
            public float FuseLeft;
            public string WeaponId;
            public bool Landed;
            public float SmokeLeft;      // 烟雾弹：爆炸后残留烟雾的剩余时间
            public float SmokeAge;       // 烟雾弹：爆开后已过的时长（决定当前半径）
            public GameObject Smoke;
        }

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly List<Flight> _flights = new List<Flight>(MaxFlights);

        /// <summary>手雷撞墙 / 烟雾弹释放的播放口（音高 / 音量经 <see cref="SfxService.PlayAt(string, Vector3, float, float)"/> 下发）。</summary>
        private readonly SfxService _sfx = new SfxService("Combat");

        private CombatEffects _fx;

        public void Init(CombatEffects fx)
        {
            _fx = fx;
        }

        /// <summary>投出一颗手雷（视觉）。参数与比赛模拟内部的出生条件保持一致。</summary>
        public void ThrowVisual(Vector3 eyePosition, Vector3 direction, CsWeaponDef def)
        {
            if (_fx == null || def == null) return;

            if (_flights.Count >= MaxFlights)
            {
                _log.Warn("nade.flight.full", $"视觉手雷已达上限 {MaxFlights}，本次不显示飞行体（伤害仍由模拟结算）");
                return;
            }

            var dir = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward;

            // 飞行体一律是手雷本体（烟雾弹的烟团在爆开时才生成，见 Tick）。
            GameObject visual = null;
            if (def.Id == CsWeapons.Flashbang) visual = _fx.RentSphere(new Color(0.6f, 0.65f, 0.9f, 1f), CsCombatTuning.GrenadeVisualRadius);
            else visual = _fx.RentSphereGrenade(CsCombatTuning.GrenadeVisualRadius);

            // 与模拟同一条初速口径（CsConst.GrenadeThrowSpeed）：俯仰由方向自己给出
            // （本工程 dir.y = sin(pitch)，故回算 pitch 再查表 —— 视觉与伤害用同一套常量）。
            var pitchDeg = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            var flight = new Flight
            {
                Go = visual,
                Position = eyePosition + dir * CsConst.PlayerRadius,
                Velocity = dir * CsConst.GrenadeThrowSpeed(pitchDeg),
                FuseLeft = CsConst.GrenadeFuse,
                WeaponId = def.Id,
            };

            if (flight.Go != null) flight.Go.transform.position = flight.Position;
            _flights.Add(flight);
        }

        /// <summary>每帧推进（与模拟的抛射物用同样的积分公式）。</summary>
        public void Tick(float dt)
        {
            for (var i = _flights.Count - 1; i >= 0; i--)
            {
                var f = _flights[i];

                if (f.SmokeLeft > 0f)
                {
                    f.SmokeLeft -= dt;
                    f.SmokeAge += dt;

                    // 与模拟的烟球同一半径、同一生长时长：直径 = 2 × SmokeRadius × 生长系数。
                    // 末段再乘一个收尾系数（原版 TE_SMOKE 有 scale 3.5 / 2.5 的收尾档）：
                    // 烟球从满径缩到 0，不再"到点整团凭空消失"。
                    if (f.Smoke != null)
                    {
                        var fade = Mathf.Clamp01(f.SmokeLeft / SmokeDissipateSeconds);
                        var diameter = 2f * Match.CsMatchConst.SmokeRadius * SmokeGrow(f.SmokeAge) * fade;
                        f.Smoke.transform.localScale = Vector3.one * Mathf.Max(0.001f, diameter);
                    }

                    if (f.SmokeLeft <= 0f)
                    {
                        DestroyFlight(f);
                        _flights.RemoveAt(i);
                    }
                    continue;
                }

                if (!f.Landed)
                {
                    f.Velocity.y -= CsConst.Gravity * dt;
                    var to = f.Position + f.Velocity * dt;

                    // 撞到非角色碰撞体即视为落地（角色的受击体不算障碍，手雷可以"穿过"人物继续飞）。
                    if (Physics.Linecast(f.Position, to, out var hit, ~0, QueryTriggerInteraction.Ignore))
                    {
                        var proxy = hit.collider != null ? hit.collider.GetComponentInParent<Match.CsHitboxProxy>() : null;
                        if (proxy == null)
                        {
                            // 撞击帧：工程内手雷落地即停（没有多次反弹）⇒ 只在撞上的这一帧按撞击速度
                            // 播一次弹跳音；速度低于门限不响（落地后贴地微动不连响）。
                            var impactSpeed = f.Velocity.magnitude;
                            to = hit.point;
                            f.Landed = true;
                            f.Velocity = Vector3.zero;
                            if (impactSpeed > CsAudioTuning.NadeBounceMinSpeed)
                            {
                                _sfx.PlayAt(CsAudioTuning.HitWall, hit.point,
                                    CsAudioTuning.NadeBouncePitch, CsAudioTuning.NadeBounceVolume);
                            }
                        }
                    }

                    f.Position = to;
                    if (f.Go != null) f.Go.transform.position = f.Position;
                }

                f.FuseLeft -= dt;
                if (f.FuseLeft > 0f) continue;

                // 引信到点：模拟那边同时（同一帧）在做真实爆炸结算，这里只补视觉。
                // 只有 HE 是橙色爆球；闪光弹是白光（与全屏白屏同源）；烟雾弹不放爆球 ——
                // 它的"爆开"由烟球自己从 0 长到满径表达，套一层橙色爆炸会看成 HE 手雷。
                if (f.WeaponId == CsWeapons.Flashbang) _fx?.Explosion(f.Position, FlashBlastColor);
                else if (f.WeaponId != CsWeapons.SmokeGrenade) _fx?.Explosion(f.Position);

                if (f.WeaponId == CsWeapons.SmokeGrenade)
                {
                    // 烟球开始生长的这一帧播释放音（降调闷响，与 HE 的爆球声区分）。
                    _sfx.PlayAt(CsAudioTuning.GrenadeExplode, f.Position,
                        CsAudioTuning.SmokeReleasePitch, CsAudioTuning.SmokeReleaseVolume);

                    // 烟雾弹：飞行体（手雷本体）归还池，另借一个半透明烟团；残留时长与模拟的烟雾体积一致。
                    // 烟团球心 = 爆点（与模拟 CsSmokeVolume.Position 同一点），半径由 Tick 逐帧按生长系数给。
                    f.Smoke = _fx?.RentSmoke(0.001f);
                    if (f.Smoke != null)
                    {
                        f.Smoke.transform.position = f.Position;
                        _log.Info("nade.smoke.spawn",
                            $"烟团起雾于 {f.Position}（半径 {Match.CsMatchConst.SmokeRadius:F1}m，" +
                            $"持续 {CsConst.GrenadeSmokeDuration:F1}s）");
                    }
                    if (f.Go != null)
                    {
                        _fx?.Return(f.Go);
                        f.Go = null;
                    }
                    f.SmokeAge = 0f;
                    f.SmokeLeft = CsConst.GrenadeSmokeDuration;
                    continue;
                }

                DestroyFlight(f);
                _flights.RemoveAt(i);
            }
        }

        /// <summary>烟雾球生长系数 0~1：<c>SmokeAge / CsMatchConst.SmokeGrowSeconds</c> 线性，与模拟同一条曲线。</summary>
        private static float SmokeGrow(float smokeAge)
        {
            var grow = Match.CsMatchConst.SmokeGrowSeconds;
            return grow <= 0f ? 1f : Mathf.Clamp01(smokeAge / grow);
        }

        public void Dispose()
        {
            for (var i = _flights.Count - 1; i >= 0; i--) DestroyFlight(_flights[i]);
            _flights.Clear();
        }

        private void DestroyFlight(Flight f)
        {
            if (f?.Smoke != null) _fx?.Return(f.Smoke);
            else if (f?.Go != null) _fx?.Return(f.Go);
            f.Go = null;
            f.Smoke = null;
        }
    }
}
