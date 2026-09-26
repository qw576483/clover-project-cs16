using System;
using System.Text;
using Cs16.Core;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 坠落伤害的数值自检：把角色放到若干高度、用 <see cref="FallTestMap"/>（y=0 平面）自由落体，
    /// 读落地后的 HP / 护甲，逐条断言原版落地判定（出处
    /// <c>原版资源/hlsdk/pm_shared/pm_shared.c:124-126</c>）在三个区间上的行为：
    /// 不高于安全速无伤、安全速与致死速之间线性给、达到致死速即死。
    ///
    /// <para>还覆盖两条机制约束：① <b>护甲不吸收</b>（落地前后护甲值不变）；② <b>只在落地那一帧结算一次</b>
    /// （落地后继续步进 30 帧不再扣血）。</para>
    ///
    /// <para><see cref="RunAll"/> 只跑本机制、秒级返回，不启 Play、不动真实场景。</para>
    /// </summary>
    public static class CsFallDamageSelfTest
    {
        private const float FrameDt = 1f / 60f;
        private static readonly StringBuilder Report = new StringBuilder();
        private static int _fail;

        public static string RunAll()
        {
            Report.Clear();
            _fail = 0;

            var prevClock = CsMatch.Clock;
            var simTime = 0f;
            CsMatch match = null;
            try
            {
                match = new CsMatch(new FallTestMap());
                CsMatch.Clock = () => simTime;
                match.Start(new CsMatchConfig
                {
                    MapName = "fall-selftest",
                    BotsPerTeam = 1,
                    PlayerTeam = CsTeam.CT,
                    PlayerName = "FallSelfTest",
                    RoundsPerHalf = 1,
                    RoundTime = 60f,
                    FreezeTime = 0f,
                    HalfTimeSwap = false,
                });

                var a = match.LocalPlayer;
                if (a == null)
                {
                    Line("FAIL 开局后拿不到 LocalPlayer");
                    return Finish();
                }

                Line("== 坠落伤害自检（CsMatch.StepActorPhysics 落地边沿 → CsDamage.ApplyFallDamage）==");
                Line($"  安全速 {CsConst.FallSafeSpeed:F3} m/s（原版 580 unit/s）、" +
                     $"致死速 {CsConst.FallFatalSpeed:F3} m/s（原版 1024 unit/s）、" +
                     $"系数 {CsConst.DamageForFallSpeed:F4} 伤害每 m/s（原版 100/(1024-580)）");
                Line("  落差(m)  落地速率(m/s)  公式伤害  实测伤害(100-HP)  护甲(落地后)  落地后30帧又扣  阵亡  结果");

                float[] heights = { 0.5f, 2f, 5f, 8f, 10f, 12f, 16f, 17f, 25f };
                var prevDamage = -1;
                foreach (var h in heights)
                {
                    Drop(match, a, h, out var impact, out var damage, out var armorAfter,
                         out var died, out var extra);
                    var formula = Mathf.RoundToInt(CsFallDamage.DamageFor(impact));
                    // 可观测伤害上限 = 满血：血量扣到 0 为止（阵亡时 Health 被置 0）。
                    var expected = Mathf.Min(formula, CsConst.MaxHealth);

                    var ok = damage == expected                      // 运行时伤害 == 真实公式
                             && armorAfter == CsConst.MaxArmor       // 护甲不被吸收、也不掉
                             && extra == 0                           // 只在落地那一帧结算
                             && damage >= prevDamage                 // 伤害随落差单调不减
                             && (h > 5f || damage == 0)              // 低落差无伤
                             && (h < 17f || died);                   // 达到致死速即死
                    if (!ok) _fail++;

                    Line("  " + h.ToString("F2").PadRight(8) + impact.ToString("F3").PadRight(14) +
                         expected.ToString().PadRight(10) + damage.ToString().PadRight(16) +
                         armorAfter.ToString().PadRight(14) + extra.ToString().PadRight(16) +
                         (died ? "是" : "否").PadRight(6) + (ok ? "OK" : "FAIL"));
                    prevDamage = damage;
                }

                // 负控：本来就站在地上，连续 60 帧一分血都不该扣（若实现写成"每帧结算"这里会红）。
                a.IsAlive = true;
                a.Health = CsConst.MaxHealth;
                a.Armor = CsConst.MaxArmor;
                a.Position = new Vector3(0f, 0f, 0f);
                a.Velocity = Vector3.zero;
                a.OnGround = true;
                for (var i = 0; i < 60; i++) match.StepActorPhysics(a, FrameDt);
                Check("负控：站在地上连续 60 帧不扣血", a.Health == CsConst.MaxHealth,
                    $"HP={a.Health}");
            }
            catch (Exception e)
            {
                _fail++;
                Line("FAIL 抛出异常：" + e);
            }
            finally
            {
                CsMatch.Clock = prevClock;
                if (match != null) { try { match.Stop(); } catch (Exception e) { Line("（Stop 抛异常：" + e.Message + "）"); } }
            }

            return Finish();
        }

        /// <summary>
        /// 把角色放到 <paramref name="height"/> 米高空自由落体，直到落地（或阵亡）。
        /// <paramref name="impact"/> = 落地那一帧的下坠速率（按 StepActorPhysics 同一套
        /// 重力/钳制在外部复算 —— 用来把运行时的实测伤害钉在真实公式上）。
        /// </summary>
        private static void Drop(CsMatch match, CsActor a, float height, out float impact, out int damage,
            out int armorAfter, out bool died, out int extraDamageAfterLanding)
        {
            a.IsAlive = true;
            a.Health = CsConst.MaxHealth;
            a.Armor = CsConst.MaxArmor;
            a.HasHelmet = true;
            a.Position = new Vector3(0f, height, 0f);
            a.Velocity = Vector3.zero;
            a.OnGround = false;

            var vy = 0f;
            impact = 0f;
            for (var frame = 0; frame < 1200 && !a.OnGround; frame++)
            {
                var vAtFrame = Mathf.Max(vy - CsConst.Gravity * FrameDt, CsConst.MaxFallSpeed);
                vAtFrame = Mathf.Max(vAtFrame, -CsMatchConst.TerminalFallSpeed);
                match.StepActorPhysics(a, FrameDt);
                if (a.OnGround) { impact = -vAtFrame; break; }
                vy = a.Velocity.y;
            }

            died = !a.IsAlive;
            damage = CsConst.MaxHealth - Mathf.Max(0, a.Health);
            armorAfter = a.Armor;

            var hpAtLanding = a.Health;
            for (var i = 0; i < 30; i++) match.StepActorPhysics(a, FrameDt);
            extraDamageAfterLanding = hpAtLanding - a.Health;
        }

        private static void Check(string what, bool ok, string detail)
        {
            if (!ok) _fail++;
            Line("  " + (ok ? "OK  " : "FAIL") + " " + what + "  " + detail);
        }

        private static void Line(string s) => Report.AppendLine(s);

        private static string Finish()
        {
            Line(_fail == 0 ? "RESULT: PASS" : "RESULT: FAIL(=" + _fail + ")");
            return Report.ToString();
        }

        /// <summary>y=0 平面地图替身：只提供 <c>StepActorPhysics</c> 需要的"空间事实"。</summary>
        private sealed class FallTestMap : ICsMap
        {
            private static readonly Vector3 Spawn = new Vector3(100f, 0f, 100f);

            public bool IsLoaded => true;
            public string MapName => "fall-selftest";
            public string Status => "fall-selftest";

            public void LoadAsync(string mapResourcePath, Action onLoaded, Action<string> onFailed = null)
                => onLoaded?.Invoke();
            public void Unload() { }

            public bool WalkableAt(float x, float z) => true;
            public bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius) => true;
            public Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius) => to;
            public float SampleGround(Vector3 pos, float maxDrop = 8f) => 0f;

            public bool TrySampleGround(Vector3 pos, out Vector3 point, out Vector3 normal, float maxDrop = 8f)
            {
                point = new Vector3(pos.x, 0f, pos.z);
                normal = Vector3.up;
                return true;
            }

            public Vector3 GetSpawnPoint(int index) => Spawn;
            public int SpawnPointCount => 1;

            public Vector3[] Points(string marker) =>
                marker == CsMarkers.SpawnT || marker == CsMarkers.SpawnCT
                    ? new[] { Spawn }
                    : Array.Empty<Vector3>();

            public bool TryGetPoint(string marker, out Vector3 point)
            {
                point = Spawn;
                return marker == CsMarkers.SpawnT || marker == CsMarkers.SpawnCT;
            }
        }
    }
}
