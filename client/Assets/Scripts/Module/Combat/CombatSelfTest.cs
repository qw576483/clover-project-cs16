using System;
using System.Collections.Generic;
using System.Text;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.CameraRig;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 第一人称操作 / 射击的自检探针（任务书 §5 要求的那份自证）。
    ///
    /// <para><b>为什么不放在 <c>Assets/Editor/</c></b>：agent-04 的产出路径只有
    /// <c>Assets/Scripts/Module/{Player,CameraRig,Combat}</c>（任务书 §2），
    /// 所以自检逻辑放在本文件的**运行时程序集**里（不依赖 <c>UnityEditor</c>），
    /// 由主 agent 用一个两行的 Editor 包装调起：</para>
    /// <code>
    /// using UnityEditor;
    /// namespace Cs16.EditorTools
    /// {
    ///     public static class CombatSelfTestMenu
    ///     {
    ///         [MenuItem("Clover/自检/第一人称操作与射击")]
    ///         public static void Run() => Debug.Log(Cs16.Module.Combat.CombatSelfTest.RunAll());
    ///     }
    /// }
    /// </code>
    ///
    /// <para><b>覆盖的断言</b>（对应验收表）：
    /// ① 散布：移动 &gt; 静止、跳跃 &gt; 静止、蹲下 &lt; 站立、开镜 &lt; 未开镜、连射递增；
    /// ② 准星扩散：移动/连射更大；
    /// ③ <b>相机朝向与射线方向一致</b>（欧拉角与 <c>AimDirection</c> 同解）；
    /// ④ 真实 <see cref="CsMatch"/> 快进：**每一发都恰好扣 1 颗弹 + 一条待表现记录**（"射线发数 == 扣弹数"）、
    /// 后坐力**递增**、射速被限制、R 换弹、1/2 切槽；
    /// ⑤ <c>Physics.Raycast</c> 能命中 <see cref="CsHitboxProxy"/>，且**跳过自己的受击体**；
    /// ⑥ "这一发是不是我打的"的判据（<see cref="CombatModule.IsLocalShot"/>）不会把别人的射击算成自己的。</para>
    ///
    /// <para>返回一份可读报告；失败项会同时打 <c>Game.Logger.Error</c>。</para>
    /// </summary>
    public static class CombatSelfTest
    {
        private const string Tag = "CombatSelfTest";
        private const float FrameDt = 1f / 60f;

        private static readonly List<string> Failures = new List<string>();
        private static readonly List<string> Notes = new List<string>();

        /// <summary>跑全部断言并返回报告文本。</summary>
        public static string RunAll()
        {
            Failures.Clear();
            Notes.Clear();

            CheckSpreadModel();
            CheckCrosshairModel();
            CheckCameraAimAgreement();
            CheckShotQueueDiscrimination();
            CheckRaycastHitsHitboxProxy();
            CheckMatchFastForward();

            var sb = new StringBuilder();
            sb.AppendLine("========== 第一人称操作与射击 自检报告 ==========");
            for (var i = 0; i < Notes.Count; i++) sb.AppendLine("· " + Notes[i]);
            if (Failures.Count == 0)
            {
                sb.AppendLine(">>> 结论: PASS");
            }
            else
            {
                sb.AppendLine($">>> 结论: FAIL（{Failures.Count} 项未达标）");
                for (var i = 0; i < Failures.Count; i++)
                {
                    sb.AppendLine("   ✗ " + Failures[i]);
                    Game.Logger.Error(Tag, Failures[i]);
                }
            }
            sb.AppendLine("===============================================");

            var text = sb.ToString();
            Game.Logger.Info(Tag, text);
            return text;
        }

        // ==================================================================
        //  ① 散布模型
        // ==================================================================
        private static void CheckSpreadModel()
        {
            var ak = CsWeapons.Get(CsWeapons.Ak47);
            var awp = CsWeapons.Get(CsWeapons.Awp);
            if (ak == null || awp == null)
            {
                Fail("武器表里找不到 ak47 / awp，散布自检无法进行");
                return;
            }

            var stand = Firearm.ComputeSpread(ak, 0f, true, false, 0, false);
            var move = Firearm.ComputeSpread(ak, 1f, true, false, 0, false);
            var air = Firearm.ComputeSpread(ak, 0f, false, false, 0, false);
            var crouch = Firearm.ComputeSpread(ak, 0f, true, true, 0, false);
            var burst3 = Firearm.ComputeSpread(ak, 0f, true, false, 3, false);
            var burst10 = Firearm.ComputeSpread(ak, 0f, true, false, 10, false);
            var awpHip = Firearm.ComputeSpread(awp, 0f, true, false, 0, false);
            var awpScope = Firearm.ComputeSpread(awp, 0f, true, false, 0, true);

            Notes.Add($"散布(度)：AK 静止={stand:F2} 移动={move:F2} 跳={air:F2} 蹲={crouch:F2} " +
                      $"连射3发={burst3:F2} 连射10发={burst10:F2}；AWP 腰射={awpHip:F2} 开镜={awpScope:F2}");

            Assert(move > stand, $"散布：移动({move:F2}) 必须大于静止({stand:F2})");
            Assert(air > stand, $"散布：跳跃({air:F2}) 必须大于静止({stand:F2})");
            Assert(crouch < stand, $"散布：蹲下({crouch:F2}) 必须小于站立({stand:F2})");
            Assert(burst3 > stand && burst10 > burst3, $"散布：连射必须递增（3发={burst3:F2}，10发={burst10:F2}）");
            Assert(awpScope < awpHip, $"散布：AWP 开镜({awpScope:F2}) 必须小于腰射({awpHip:F2})");
        }

        // ==================================================================
        //  ② 准星扩散模型
        // ==================================================================
        private static void CheckCrosshairModel()
        {
            var ak = CsWeapons.Get(CsWeapons.Ak47);
            if (ak == null) { Fail("武器表里找不到 ak47，准星自检无法进行"); return; }

            var still = new CrosshairState();
            var moving = new CrosshairState();

            var actor = new CsActor { Id = 1, IsAlive = true, OnGround = true, ActiveWeapon = CsWeapons.Ak47 };
            actor.Velocity = Vector3.zero;

            for (var i = 0; i < 60; i++) still.Tick(FrameDt, actor, ak, false);

            actor.Velocity = new Vector3(CsConst.SpeedRifle, 0f, 0f);
            actor.ConsecutiveShots = CsCombatTuning.CrosshairFullShots;
            for (var i = 0; i < 60; i++) moving.Tick(FrameDt, actor, ak, false);

            Notes.Add($"准星扩散(0~1)：静止={still.Value:F2} 满速+连射={moving.Value:F2}");

            Assert(still.Value < moving.Value,
                $"准星扩散：移动+连射({moving.Value:F2}) 必须大于静止({still.Value:F2})");
            Assert(moving.Value > 0.5f, $"准星扩散：满速连射时应明显张开（实测 {moving.Value:F2}）");
        }

        // ==================================================================
        //  ③ 相机朝向 == 射线方向（两处算的是同一个方向）
        // ==================================================================
        private static void CheckCameraAimAgreement()
        {
            var cases = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(90f, 0f),
                new Vector2(180f, 0f),
                new Vector2(-90f, 0f),
                new Vector2(0f, 45f),
                new Vector2(0f, -45f),
                new Vector2(37f, 12f),
            };

            var worst = 0f;
            for (var i = 0; i < cases.Length; i++)
            {
                var yaw = cases[i].x;
                var pitch = cases[i].y;

                var aim = CameraMath.AimDirection(yaw, pitch);   // 引擎件（E-core-18 下沉；口径与原实现逐字一致）
                // 相机实际用的是 Quaternion.Euler(-pitch, yaw, 0)（Unity 的 X 轴正方向与"抬头"相反）
                var camForward = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
                var diff = Vector3.Angle(aim, camForward);
                if (diff > worst) worst = diff;
            }

            Notes.Add($"相机朝向 vs 射线方向最大夹角：{worst:F3}°");

            Assert(worst < 0.01f, $"相机朝向与射线方向不一致（最大差 {worst:F3}°）：准星所指与子弹方向会分叉");

            // 顺带钉住方向约定本身（yaw 0 = +Z、pitch>0 = 抬头）
            var up = CameraMath.AimDirection(0f, 89f);
            Assert(up.y > 0.99f, "pitch=+89 必须朝上（与 CsActor.Pitch 的符号约定一致）");
            var east = CameraMath.AimDirection(90f, 0f);
            Assert(east.x > 0.99f, "yaw=90 必须朝 +X（与 CsActor.Yaw 的约定一致）");
        }

        // ==================================================================
        //  ⑥ "这一发是不是我打的"
        // ==================================================================
        private static void CheckShotQueueDiscrimination()
        {
            Assert(CombatModule.IsLocalShot(true, 0f, 0.1f),
                "判据：请求过开火 + 模拟推进了 NextFireTime ⇒ 应判为「我的这一发」");
            Assert(!CombatModule.IsLocalShot(false, 0f, 0.1f),
                "判据：没请求开火（机器人开火）⇒ 绝不能判成「我的这一发」（否则会双份伤害）");
            Assert(!CombatModule.IsLocalShot(true, 0.1f, 0.1f),
                "判据：NextFireTime 没变 ⇒ 模拟没真打（被射速/换弹拦下），不算我的这一发");

            // ---- 队列挑选：机器人抢在我前面开火时，必须仍然只认我那条 ----
            var hostileFirst = new List<string> { CsWeapons.Awp, CsWeapons.Ak47 };
            var mine = CombatModule.PickMyShot(hostileFirst, true, CsWeapons.Ak47, out var others);
            Assert(mine == CsWeapons.Ak47, $"队列挑选：应认领我那条 ak47，实测 {(mine == null ? "-" : mine)}");
            Assert(others == 1, $"队列挑选：机器人那条应被算作「他人记录」（实测 {others}）");

            mine = CombatModule.PickMyShot(hostileFirst, false, CsWeapons.Ak47, out others);
            Assert(mine == null, "队列挑选：我没开火时，一条都不能认领（否则双份伤害）");
            Assert(others == 2, $"队列挑选：我没开火时全部计入他人（实测 {others}）");

            // 同帧两条同武器（一个 bot 一个我）也只认一条
            var bothAk = new List<string> { CsWeapons.Ak47, CsWeapons.Ak47 };
            mine = CombatModule.PickMyShot(bothAk, true, CsWeapons.Ak47, out others);
            Assert(mine == CsWeapons.Ak47 && others == 1,
                $"队列挑选：同帧两条同武器只能认领一条（实测 mine={mine ?? "null"} others={others}）");

            // 手雷投出后手持会变，所以必须拿"开火前的武器"去认
            var nade = new List<string> { CsWeapons.HeGrenade };
            Assert(CombatModule.PickMyShot(nade, true, CsWeapons.HeGrenade, out _) == CsWeapons.HeGrenade,
                "队列挑选：手雷必须用「开火前手持」认领（投出后手持已变）");
            Assert(CombatModule.PickMyShot(nade, true, CsWeapons.Ak47, out _) == null,
                "队列挑选：武器对不上就不认领（避免把别人的记录当成自己的）");

            Notes.Add("射击队列：本地/机器人开火可区分，且我的那一发只会被认领一次（不会多打/少打）");
        }

        // ==================================================================
        //  ⑤ 射线能命中 CsHitboxProxy，且跳过自己
        // ==================================================================
        private static void CheckRaycastHitsHitboxProxy()
        {
            var spawned = new List<GameObject>();
            try
            {
                const long selfId = 9001L;
                const long victimId = 9002L;

                // 自己的受击体（挡在更近处）→ 必须被跳过
                var selfBox = NewHitbox(selfId, CsHitbox.Chest, "Self", new Vector3(0f, 0f, 1f));
                var victimBox = NewHitbox(victimId, CsHitbox.Head, "Victim", new Vector3(0f, 0f, 5f));
                spawned.Add(selfBox);
                spawned.Add(victimBox);
                Physics.SyncTransforms();

                var ak = CsWeapons.Get(CsWeapons.Ak47);
                var firearm = new Firearm();
                var request = new ShotRequest
                {
                    ShooterId = selfId,
                    Def = ak,
                    Origin = new Vector3(0f, 0f, 0f),
                    Direction = Vector3.forward,
                    Spread = 0f,
                    Range = ak != null ? ak.Range : 50f,
                    Pellets = 1,
                };

                firearm.Fire(request);

                var hits = firearm.Hits;
                var hitCount = hits.Count;
                var firstVictim = hitCount > 0 ? hits[0].VictimId : -1L;
                var firstHitbox = hitCount > 0 ? hits[0].Hitbox : CsHitbox.Generic;

                Notes.Add($"射线：命中 {hitCount} 个，首个 VictimId={firstVictim} 部位={firstHitbox}");

                if (hitCount == 0)
                {
                    // EditMode 下 Physics 查询可能没生效（与 agent-03 的自检同因），要点明而不是误判逻辑错。
                    Notes.Add("射线未命中任何受击体：若当前是 EditMode，请进 Play 模式重跑（编辑器的物理查询可能未生效）");
                    Fail("射线没有命中 CsHitboxProxy（见上一条说明：先排除 EditMode 物理环境问题）");
                    return;
                }

                Assert(firstVictim == victimId,
                    $"射线应跳过自己的受击体、命中 {victimId}，实测命中 {firstVictim}（近距离自体会挡住射线）");
                Assert(firstHitbox == CsHitbox.Head, $"命中部位应为 Head，实测 {firstHitbox}");
            }
            catch (Exception ex)
            {
                Fail("射线自检抛异常：" + ex.Message);
            }
            finally
            {
                for (var i = 0; i < spawned.Count; i++)
                {
                    if (spawned[i] != null) UnityEngine.Object.DestroyImmediate(spawned[i]);
                }
            }
        }

        private static GameObject NewHitbox(long actorId, CsHitbox box, string suffix, Vector3 position)
        {
            var go = new GameObject($"CombatSelfTest_{actorId}_{suffix}");
            go.transform.position = position;
            var col = go.AddComponent<BoxCollider>();
            col.size = Vector3.one * (CsConst.PlayerRadius * 2f);
            var proxy = go.AddComponent<CsHitboxProxy>();
            proxy.ActorId = actorId;
            proxy.Hitbox = box;
            return go;
        }

        // ==================================================================
        //  ④ 真实模拟快进：扣弹发数 == 待表现记录数、后坐力递增、射速受限、换弹、切槽
        // ==================================================================
        private static void CheckMatchFastForward()
        {
            var prevClock = CsMatch.Clock;
            var simTime = 0f;
            CsMatch match = null;

            try
            {
                match = new CsMatch(new SelfTestMap());
                var cfg = new CsMatchConfig
                {
                    MapName = "combat-selftest",
                    BotsPerTeam = 0,          // 只要本地玩家，队列里就只会有"我"的记录
                    PlayerTeam = CsTeam.CT,
                    PlayerName = "CombatSelfTest",
                    RoundsPerHalf = 1,
                    RoundTime = 30f,
                    FreezeTime = 0.2f,        // 快速过冻结期
                    HalfTimeSwap = false,
                };

                CsMatch.Clock = () => simTime;
                match.Start(cfg);

                var local = match.LocalPlayer;
                if (local == null)
                {
                    Fail("模拟开局后拿不到 LocalPlayer，快进自检无法进行");
                    match.Stop();
                    return;
                }

                // 装备一把 AK（走正规买枪入口需要买枪区/时间，这里直接给——自检只关心射击链路）
                local.PrimaryWeapon = CsWeapons.Ak47;
                local.ActiveWeapon = CsWeapons.Ak47;
                local.SetAmmo(CsWeapons.Ak47, 30, 90);
                local.RecoilPitch = 0f;
                local.ConsecutiveShots = 0;

                var ammoBefore = local.GetAmmo(CsWeapons.Ak47);
                var shotsRecorded = 0;
                var shotsWithRecoilGrowth = 0;
                var prevRecoil = 0f;
                var tickedFrames = 0;

                // ---- 快进 1.5 秒：全程按住左键 ----
                for (var i = 0; i < 90; i++)
                {
                    var cmd = default(CsInputState);
                    cmd.Fire = true;
                    cmd.Yaw = 0f;
                    cmd.Pitch = 0f;
                    match.SetLocalInput(cmd);
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                    tickedFrames++;

                    while (match.ConsumeShotFired(out _)) shotsRecorded++;

                    if (local.RecoilPitch > prevRecoil) shotsWithRecoilGrowth++;
                    prevRecoil = local.RecoilPitch;
                }

                var ammoAfter = local.GetAmmo(CsWeapons.Ak47);
                var ammoUsed = ammoBefore.inMag - ammoAfter.inMag;

                var ak = CsWeapons.Get(CsWeapons.Ak47);
                var expectedMax = ak != null && ak.SecondsPerShot > 0f
                    ? Mathf.CeilToInt(simTime / ak.SecondsPerShot) + 2
                    : 0;

                Notes.Add($"模拟快进 {tickedFrames} 帧（{simTime:F2}s，假时钟）：扣弹 {ammoUsed} 发，" +
                          $"待表现记录 {shotsRecorded} 条，后坐力抬升递增 {shotsWithRecoilGrowth} 次，" +
                          $"RecoilPitch={local.RecoilPitch:F2}°");

                Assert(ammoUsed > 0, $"按住左键 1.5 秒一发都没打出去（扣弹={ammoUsed}）");
                Assert(shotsRecorded == ammoUsed,
                    $"必须「射线发数 == 扣弹数」：扣弹 {ammoUsed} 发 vs 待表现记录 {shotsRecorded} 条");
                Assert(expectedMax <= 0 || shotsRecorded <= expectedMax,
                    $"射速限制：{simTime:F2}s 内不应超过 {expectedMax} 发（实测 {shotsRecorded} 发）");
                Assert(local.ConsecutiveShots > 1, $"连发计数没有累积（ConsecutiveShots={local.ConsecutiveShots}）");
                Assert(shotsWithRecoilGrowth >= 2, $"后坐力没有随连发递增（递增次数={shotsWithRecoilGrowth}）");

                // ---- R 换弹 ----
                local.SetAmmo(CsWeapons.Ak47, 3, 90);
                match.RequestReload();
                var reloadEnd = local.ReloadEndTime;
                Assert(reloadEnd > simTime, $"RequestReload 没有进入换弹状态（ReloadEndTime={reloadEnd:F2}）");

                var guard = 0;
                while (simTime < reloadEnd + FrameDt && guard++ < 600)
                {
                    match.SetLocalInput(default);
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }

                var afterReload = local.GetAmmo(CsWeapons.Ak47);
                Notes.Add($"换弹后弹匣：{afterReload.inMag}/{afterReload.reserve}（换弹前 3）");
                Assert(afterReload.inMag > 3, $"换弹后弹匣没补满（{afterReload.inMag}）");

                // ---- 切槽（2 = 手枪）----
                match.SwitchSlot(2);
                var switchEnd = local.SwitchEndTime;
                guard = 0;
                while (simTime < switchEnd + FrameDt && guard++ < 300)
                {
                    match.SetLocalInput(default);
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }

                Notes.Add($"切槽 2 后手持：{local.ActiveWeapon ?? "null"}（期望手枪）");
                Assert(local.ActiveWeapon != CsWeapons.Ak47,
                    $"切槽 2 之后手里不该还是 AK（实测 {local.ActiveWeapon ?? "null"}）");

                match.Stop();
            }
            catch (Exception ex)
            {
                Fail("模拟快进自检抛异常：" + ex);
            }
            finally
            {
                CsMatch.Clock = prevClock;
            }
        }

        // ==================================================================
        //  工具
        // ==================================================================
        private static void Assert(bool condition, string message)
        {
            if (condition) return;
            Failures.Add(message);
        }

        private static void Fail(string message)
        {
            Failures.Add(message);
        }

        /// <summary>自检用假地图：不做碰撞、出生点在原点附近，只让模拟能跑起来（与 agent-03 的自检同做法）。</summary>
        private sealed class SelfTestMap : Cs16.Module.Map.ICsMap
        {
            private static readonly Vector3 SpawnT = new Vector3(0f, 0f, -8f);
            private static readonly Vector3 SpawnCT = new Vector3(0f, 0f, 8f);
            private static readonly Vector3 SiteA = new Vector3(10f, 0f, -10f);
            private static readonly Vector3 SiteB = new Vector3(-10f, 0f, 10f);

            public bool IsLoaded => true;
            public string MapName => "combat-selftest";
            public string Status => "stub";
            public int SpawnPointCount => 2;

            public void LoadAsync(string mapResourcePath, Action onLoaded, Action<string> onFailed = null)
                => onLoaded?.Invoke();

            public void Unload() { }
            public bool WalkableAt(float x, float z) => true;
            public bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius) => true;
            public Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius) => to;
            public float SampleGround(Vector3 pos, float maxDrop = 8f) => 0f;
            public Vector3 GetSpawnPoint(int index) => index % 2 == 0 ? SpawnT : SpawnCT;

            public Vector3[] Points(string marker)
            {
                switch (marker)
                {
                    case CsMarkers.SpawnT:
                    case CsMarkers.BuyZoneT:
                        return new[] { SpawnT };
                    case CsMarkers.SpawnCT:
                    case CsMarkers.BuyZoneCT:
                        return new[] { SpawnCT };
                    case CsMarkers.BombsiteA: return new[] { SiteA };
                    case CsMarkers.BombsiteB: return new[] { SiteB };
                    case CsMarkers.Patrol: return new[] { SpawnT, SpawnCT, SiteA, SiteB };
                    default: return new Vector3[0];
                }
            }

            public bool TryGetPoint(string marker, out Vector3 point)
            {
                var pts = Points(marker);
                if (pts.Length == 0)
                {
                    point = default;
                    return false;
                }

                point = pts[0];
                return true;
            }
        }
    }
}
