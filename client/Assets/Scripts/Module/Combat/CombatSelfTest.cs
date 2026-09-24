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
            CheckDroppedWeaponWorldEntity();

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

        /// <summary>
        /// **只跑差异 #75 的数值判据**并返回报告 —— 供 <c>unity command eval_file</c> 调用。
        /// <para>⛔ 刻意不走 <see cref="RunAll"/>：RunAll 有 6 组快进，整跑会撞 eval_file 的
        /// 「主线程 5 s」上限（实测 <c>Main thread operation timed out after 5000ms</c>），
        /// 而本入口只跑一组、秒级返回。</para>
        /// </summary>
        public static string RunDropOnly()
        {
            Failures.Clear();
            Notes.Clear();
            CheckDroppedWeaponWorldEntity();

            var sb = new StringBuilder();
            sb.AppendLine("========== 差异 #75 「掉落武器不在世界里」数值自检 ==========");
            for (var i = 0; i < Notes.Count; i++) sb.AppendLine("· " + Notes[i]);
            if (Failures.Count == 0)
            {
                sb.AppendLine(">>> 结论: PASS");
            }
            else
            {
                sb.AppendLine($">>> 结论: FAIL（{Failures.Count} 项未达标）");
                for (var i = 0; i < Failures.Count; i++) sb.AppendLine("   ✗ " + Failures[i]);
            }
            sb.AppendLine("=============================================================");

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
            // 同 BotSelfTest：自检临时受击体 ⇒ ① Gizmos 叠层不画它（HideInHierarchy 只改叠加层，不动物理）
            // ② 用完整例 DestroyImmediate（本方法调用方的 finally）。
            go.hideFlags = HideFlags.HideInHierarchy;
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

                // ---- 差异 #72：换弹边沿 = 单调序号 ReloadSeq，不是「ReloadEndTime 前推」 ----
                // 为什么在同一段里断言：旧口径（比截止时间戳）只能在**换弹进行中那个时间窗**里被看见，
                // 视图一旦漏采样（掉帧 / 切局 / 视图模块暂停）就**永久丢**这一次；序号是**黏的** ——
                // 迟到的采样照样看得到。所以"每次成功换弹 ⇒ 序号 +1"必须是硬不变式。
                var seqAfterRequest = local.ReloadSeq;
                Assert(seqAfterRequest >= 1,
                    $"请求换弹成功却没有推进 ReloadSeq（={seqAfterRequest}）—— 表现层收不到换弹边沿（差异 #72）");

                // 边界①「连点 R」：换弹进行中再按 R 必须被挡下（CsInventory.Reload 的 ReloadEndTime>0 分支）
                // ⇒ 序号**不许**再动，否则一次换弹会被表现层播成多次。
                match.RequestReload();
                match.RequestReload();
                Assert(local.ReloadSeq == seqAfterRequest,
                    $"换弹进行中连点 R 不该推进序号（{local.ReloadSeq} != {seqAfterRequest}）—— 差异 #72 边界①");

                var guard = 0;
                while (simTime < reloadEnd + FrameDt && guard++ < 600)
                {
                    match.SetLocalInput(default);
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }
                Assert(local.ReloadSeq == seqAfterRequest,
                    $"换弹结算（CompleteReload）不该推进序号（{local.ReloadSeq} != {seqAfterRequest}）—— " +
                    "序号只记「成功发起的换弹」，不记结算");

                var afterReload = local.GetAmmo(CsWeapons.Ak47);
                Notes.Add($"换弹后弹匣：{afterReload.inMag}/{afterReload.reserve}（换弹前 3）");
                Assert(afterReload.inMag > 3, $"换弹后弹匣没补满（{afterReload.inMag}）");

                // 边界②「换弹中切枪再切回再换弹」：切枪会把 ReloadEndTime 归零，而新一次换弹的截止
                // 时间**可能比旧的更小**（AK47 3.0s ⇒ Glock18 2.2s，`CsWeapons.cs` 武器表）
                // ⇒ 旧的"时间戳前推"口径在这条序列上判不出新换弹；序号只增不减 ⇒ 必须看得到第 2 次。
                local.SetAmmo(CsWeapons.Ak47, 5, 90);
                match.SwitchSlot(2);                       // 2 = 手枪（切枪取消换弹：ReloadEndTime 归零）
                var switchToPistol = local.SwitchEndTime;
                var guard2 = 0;
                while (simTime < switchToPistol + FrameDt && guard2++ < 600)
                {
                    match.SetLocalInput(default);
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }
                match.SwitchSlot(1);                       // 1 = 主武器
                var switchBack = local.SwitchEndTime;
                var guard3 = 0;
                while (simTime < switchBack + FrameDt && guard3++ < 600)
                {
                    match.SetLocalInput(default);
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }
                match.RequestReload();
                Assert(local.ReloadSeq == seqAfterRequest + 1,
                    $"换弹中切枪再切回后重新换弹，序号必须 +1（实测 {local.ReloadSeq}，期望 {seqAfterRequest + 1}）" +
                    " —— 差异 #72 边界②（这条序列上 ReloadEndTime 前推口径判不出来）");
                Notes.Add($"差异 #72：跨「连点 R / 切枪再换弹」两条边界后 ReloadSeq={local.ReloadSeq}（单调递增，未漏报）");

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

                // ================= 差异 #68：attack2（右键）—— 切换型输入 =================
                // 判据分三块：① 能力表（哪几把武器有出处的"可切状态"）；② **一次按下只切一次**（必须
                // 判沿：电平型/黏住的输入不许每帧翻转）；③ 切枪期间照样可切、没出处的武器一律不动状态。
                // ⛔ 数值影响（消音后的伤害/散布、连发的发数与节奏）**不在本段**：没有出处，见差异 #68 的
                //    「仍未证」段 —— 本段只判"状态可切换 + 可观测"。
                Assert(CsWeapons.Get(CsWeapons.Usp).CanSilence, "USP 的 attack2 能力表没打上 CanSilence（差异 #68）");
                Assert(CsWeapons.Get(CsWeapons.M4A1).CanSilence, "M4A1 的 attack2 能力表没打上 CanSilence（差异 #68）");
                Assert(CsWeapons.Get(CsWeapons.Glock18).CanBurst, "Glock18 的 attack2 能力表没打上 CanBurst（差异 #68）");
                Assert(CsWeapons.Get(CsWeapons.Famas).CanBurst, "FAMAS 的 attack2 能力表没打上 CanBurst（差异 #68）");
                Assert(!CsWeapons.Get(CsWeapons.Ak47).CanSilence && !CsWeapons.Get(CsWeapons.Ak47).CanBurst,
                    "AK47 没有 attack2 出处，能力表不该给它打标（⛔ 无出处不许接）");

                // 一个只推进假时钟、不发任何输入意图的帧步（换弹/切枪计时都要靠它走完）
                void TickFrames(int n)
                {
                    for (var k = 0; k < n; k++)
                    {
                        match.SetLocalInput(default);
                        match.Tick(FrameDt);
                        simTime += FrameDt;
                    }
                }

                // ---- ① USP：按下沿 ⇒ 翻转一次 ----
                local.PrimaryWeapon = CsWeapons.Usp;
                local.ActiveWeapon = CsWeapons.Usp;
                local.Silenced = false;
                local.BurstMode = false;
                var switchUsp = local.SwitchEndTime;
                while (simTime < switchUsp + FrameDt && guard++ < 600) TickFrames(1);

                var press = default(CsInputState);          // 一次"按下"
                press.Attack2 = true;
                match.SetLocalInput(press);
                match.Tick(FrameDt);
                simTime += FrameDt;
                Assert(local.Silenced, "USP 按了一次右键却没装上消音器（差异 #68）");
                Notes.Add($"差异 #68：USP attack2 第 1 次按下 ⇒ Silenced={local.Silenced}");

                // ---- ② 关键边界：**按住不重复翻转** ----
                // `_localInput` 是黏的（门面只在 SetLocalInput 时更新）⇒ 若模拟侧不判沿，下面这 5 帧会
                // 把消音器来回拆装 5 次。这条断言就是"判沿"这件事的**唯一可离线证伪点**。
                for (var k = 0; k < 5; k++)
                {
                    match.SetLocalInput(press);             // 仍按住
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }
                Assert(local.Silenced,
                    "按住右键 5 帧后消音器被翻回去了 —— 模拟侧没判'按下沿'（差异 #68 边界①：切换型输入）");

                // 松手再按一次 ⇒ 才应该翻回去
                TickFrames(1);
                match.SetLocalInput(press);
                match.Tick(FrameDt);
                simTime += FrameDt;
                Assert(!local.Silenced, "松手后再按一次右键，消音器没拆下来（差异 #68）");
                TickFrames(1);
                Notes.Add("差异 #68：USP attack2 按住 5 帧只切 1 次；松手再按才第 2 次（切换型语义成立）");

                // ---- ③ Glock18：切连发模式 ----
                local.SecondaryWeapon = CsWeapons.Glock18;
                match.SwitchSlot(2);
                var switchGlock = local.SwitchEndTime;
                while (simTime < switchGlock + FrameDt && guard++ < 600) TickFrames(1);
                local.BurstMode = false;
                match.SetLocalInput(press);
                match.Tick(FrameDt);
                simTime += FrameDt;
                Assert(local.BurstMode, "Glock18 按了一次右键却没切到连发模式（差异 #68）");
                Assert(!local.Silenced, "Glock18 只有连发模式（CanSilence=false），不该被动到 Silenced");
                TickFrames(1);
                Notes.Add($"差异 #68：Glock18 attack2 ⇒ BurstMode={local.BurstMode}（Silenced 保持 false）");

                // ---- ④ 切枪期间照样能切（⛔ 与 Reload 不同：原版的消音器拆装不受切枪影响）----
                match.SwitchSlot(1);
                Assert(local.SwitchEndTime > simTime,
                    "本段需要'正在切枪'的状态：SwitchSlot(1) 没有产生切枪计时，断言④前提不成立");
                local.Silenced = false;
                match.SetLocalInput(press);
                match.Tick(FrameDt);
                simTime += FrameDt;
                Assert(local.Silenced,
                    "切枪期间按右键被挡下了 —— 原版 attack2 不受切枪影响（差异 #68 边界②）");
                TickFrames(1);
                Notes.Add("差异 #68：切枪期间 attack2 仍生效（与 Reload 的拦截规则不同）");

                // ---- ⑤ 没出处的武器：右键无动作（状态一位都不许动）----
                local.PrimaryWeapon = CsWeapons.Ak47;
                local.ActiveWeapon = CsWeapons.Ak47;
                var switchAk = local.SwitchEndTime;
                while (simTime < switchAk + FrameDt && guard++ < 600) TickFrames(1);
                local.Silenced = false;
                local.BurstMode = false;
                match.SetLocalInput(press);
                match.Tick(FrameDt);
                simTime += FrameDt;
                Assert(!local.Silenced && !local.BurstMode,
                    "AK47 没有 attack2 出处，按右键后状态却变了（⛔ 无出处不许给效果）");
                TickFrames(1);
                Notes.Add("差异 #68：AK47 attack2 无动作（能力表无出处 ⇒ 不接）");

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

        // ==================================================================
        //  ⑥ 世界中的掉落武器（差异 #75）—— 数值类判据，**离线跑，不进 Play**
        // ==================================================================
        /// <summary>
        /// 差异 #75 的数值判据：**掉落 ⇒ 世界上多一件（并带着当时的余弹）；站在 5 m 外捡不到（负控）；
        /// 走到 1.2 m 内 ⇒ 自动拾取且余弹原样回来；主槽已满 ⇒ 捡不起来（负控）**。
        ///
        /// <para>判据两点说明：① 用 <see cref="SelfTestMap"/> 快进（与 ④ 同做法），
        /// <c>TrySampleGround</c> 会把角色贴到 y=0，但**水平的 x/z 由本自检摆** ⇒ 距离判定不受影响；
        /// ② 每次 Tick 都写 <c>default(CsInputState)</c>（不按任何键）⇒ 角色不会自己走开。</para>
        /// </summary>
        private static void CheckDroppedWeaponWorldEntity()
        {
            var prevClock = CsMatch.Clock;
            var simTime = 0f;
            CsMatch match = null;

            try
            {
                match = new CsMatch(new SelfTestMap());
                var cfg = new CsMatchConfig
                {
                    MapName = "drop-selftest",
                    BotsPerTeam = 1,
                    PlayerTeam = CsTeam.CT,
                    PlayerName = "DropSelfTest",
                    RoundsPerHalf = 1,
                    RoundTime = 60f,
                    FreezeTime = 0.2f,
                    HalfTimeSwap = false,
                };

                CsMatch.Clock = () => simTime;
                match.Start(cfg);

                var local = match.LocalPlayer;
                if (local == null)
                {
                    Fail("掉落自检：开局后拿不到 LocalPlayer");
                    match.Stop();
                    return;
                }

                // 先过冻结期：拾取只在 Live 判定。
                var idle = default(CsInputState);

                // ⛔ 必须把**非本地** actor 挪远：本用例的掉落点就在 CT 出生点旁 0.72 m，
                //    而 CT 侧那个 bot 正出生在那里 ⇒ 它会在第一帧就把枪捡走（实测过）。
                //    每帧 Tick 前重设一次（bot 每帧会自己走，所以不能只设一次）。
                void KeepBotsAway()
                {
                    var list = match.Actors;
                    for (var i = 0; i < list.Count; i++)
                    {
                        var a = list[i];
                        if (!ReferenceEquals(a, local)) a.Position = new Vector3(100f, 0f, 100f);
                    }
                }
                for (var i = 0; i < 60 && match.Phase != CsRoundPhase.Live; i++)
                {
                    match.SetLocalInput(idle);
                    KeepBotsAway();
                    match.Tick(FrameDt);
                    simTime += FrameDt;
                }
                if (match.Phase != CsRoundPhase.Live)
                {
                    Fail($"掉落自检：快进 60 帧后仍未进 Live（实际 {match.Phase}）");
                    match.Stop();
                    return;
                }

                // 备货：主武器 AK（弹匣 17 + 备弹 42，特意不是满弹 ⇒ 能判"余弹原样回来"）。
                local.PrimaryWeapon = CsWeapons.Ak47;
                local.SecondaryWeapon = CsWeapons.Usp;
                local.ActiveWeapon = CsWeapons.Ak47;
                local.SetAmmo(CsWeapons.Ak47, 17, 42);

                var before = match.DroppedWeapons.Count;

                // ---- ① 掉落：世界上应多一件，且带当时余弹 ----
                match.DropActiveWeapon();
                Assert(match.DroppedWeapons.Count == before + 1,
                    $"掉落自检①：DropActiveWeapon 后世界上应有 {before + 1} 件，实际 {match.DroppedWeapons.Count}");

                if (match.DroppedWeapons.Count == 0)
                {
                    Fail("掉落自检①：掉落列表为空，后面的用例无法进行");
                    match.Stop();
                    return;
                }

                var d0 = match.DroppedWeapons[match.DroppedWeapons.Count - 1];
                Assert(d0.WeaponId == CsWeapons.Ak47,
                    $"掉落自检①：掉落的应是 {CsWeapons.Ak47}，实际 {d0.WeaponId}");
                Assert(d0.MagAmmo == 17 && d0.ReserveAmmo == 42,
                    $"掉落自检①：掉落物应带当时余弹 17+42，实际 {d0.MagAmmo}+{d0.ReserveAmmo}");
                Notes.Add($"掉落① {d0.WeaponId} @ ({d0.Position.x:F2},{d0.Position.y:F2},{d0.Position.z:F2}) 余弹 {d0.MagAmmo}+{d0.ReserveAmmo}");

                var dropPos = d0.Position;

                // ---- ② 负控：站在 5 m 外（> PickupRadius 1.2）⇒ 不该被捡 ----
                local.Position = new Vector3(dropPos.x + 5f, dropPos.y, dropPos.z);
                for (var i = 0; i < 5; i++) { match.SetLocalInput(idle); KeepBotsAway(); match.Tick(FrameDt); simTime += FrameDt; }
                Assert(match.DroppedWeapons.Count == 1,
                    $"掉落自检②（负控）：站在 5 m 外不该被拾取，实际剩 {match.DroppedWeapons.Count} 件");

                // ---- ③ 走到 0.5 m 内（< 1.2）⇒ 自动拾取，余弹原样回来 ----
                local.Position = new Vector3(dropPos.x + 0.5f, dropPos.y, dropPos.z);
                for (var i = 0; i < 5; i++) { match.SetLocalInput(idle); KeepBotsAway(); match.Tick(FrameDt); simTime += FrameDt; }
                Assert(match.DroppedWeapons.Count == 0,
                    $"掉落自检③：走到 0.5 m 内应被自动拾取（世界清零），实际剩 {match.DroppedWeapons.Count} 件");
                Assert(local.PrimaryWeapon == CsWeapons.Ak47,
                    $"掉落自检③：拾取后应重新拿回 {CsWeapons.Ak47}，实际 {local.PrimaryWeapon}");
                var back = local.GetAmmo(CsWeapons.Ak47);
                Assert(back.inMag == 17 && back.reserve == 42,
                    $"掉落自检③：拾取后余弹应原样回来 17+42，实际 {back.inMag}+{back.reserve}");
                Notes.Add($"拾取③ {CsWeapons.Ak47} 回到手上 余弹 {back.inMag}+{back.reserve}");

                // ---- ④ 负控：主槽已被占 ⇒ 主武器捡不起来 ----
                local.PrimaryWeapon = null;
                local.ActiveWeapon = null;
                local.SecondaryWeapon = CsWeapons.Usp;
                local.SetAmmo(CsWeapons.Ak47, 0, 0);
                match.DropActiveWeapon();   // 手上没主武器 ⇒ 会走 "没这把枪" 分支，这里改用直接掉落验负控
                // 上面那次不一定丢成功；改为"先给一把再丢"以确保世界上有一件：
                if (match.DroppedWeapons.Count == 0)
                {
                    local.PrimaryWeapon = CsWeapons.Ak47;
                    local.ActiveWeapon = CsWeapons.Ak47;
                    local.SetAmmo(CsWeapons.Ak47, 5, 5);
                    match.DropActiveWeapon();
                }
                Assert(match.DroppedWeapons.Count == 1,
                    $"掉落自检④：动手前世界上应有 1 件，实际 {match.DroppedWeapons.Count}");
                if (match.DroppedWeapons.Count == 1)
                {
                    var d1 = match.DroppedWeapons[0];
                    local.PrimaryWeapon = CsWeapons.M4A1;   // 主槽**已满**
                    local.SetAmmo(CsWeapons.M4A1, 30, 90);
                    local.Position = new Vector3(d1.Position.x + 0.5f, d1.Position.y, d1.Position.z);
                    for (var i = 0; i < 5; i++) { match.SetLocalInput(idle); KeepBotsAway(); match.Tick(FrameDt); simTime += FrameDt; }
                    Assert(match.DroppedWeapons.Count == 1,
                        "掉落自检④（负控）：主槽已满时不该能捡起主武器（原版不会挤掉手里那把）");
                }

                // ---- ⑤ 重开一局 ⇒ 世界上不留上一局的掉落物（`Stop()`/`Start()` 都走 ClearDroppedWeapons）----
                if (match.DroppedWeapons.Count == 0)
                {
                    local.PrimaryWeapon = CsWeapons.Ak47;
                    local.ActiveWeapon = CsWeapons.Ak47;
                    local.SetAmmo(CsWeapons.Ak47, 3, 3);
                    match.DropActiveWeapon();
                }
                var beforeRestart = match.DroppedWeapons.Count;
                Assert(beforeRestart >= 1,
                    $"掉落自检⑤：重开前世界上应有 ≥1 件，实际 {beforeRestart}");
                match.Stop();
                match.Start(cfg);
                Assert(match.DroppedWeapons.Count == 0,
                    $"掉落自检⑤：重开一局后世界上不该留着上一局的掉落物，实际还剩 {match.DroppedWeapons.Count} 件");
                Notes.Add($"重开清空⑤ 重开前 {beforeRestart} 件 ⇒ 重开后 {match.DroppedWeapons.Count} 件");
            }
            catch (Exception ex)
            {
                Fail($"掉落自检抛异常：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                CsMatch.Clock = prevClock;
                if (match != null) match.Stop();
            }
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

            /// <summary>假地图的地面 = y 0 的水平面（法线朝上 ⇒ 永远是可站立的缓面）。</summary>
            public bool TrySampleGround(Vector3 pos, out Vector3 point, out Vector3 normal, float maxDrop = 8f)
            {
                point = new Vector3(pos.x, 0f, pos.z);
                normal = Vector3.up;
                return true;
            }
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
