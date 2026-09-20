#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEditor;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人 AI 自检探针（**Editor-only**；用 <c>#if UNITY_EDITOR</c> 包住，构建时不会进包）。
    ///
    /// <para><b>为什么放在 <c>Module/Bot/**</c> 而不是 <c>Assets/Editor/</c></b>：任务书 §2+§6 的硬约束是
    /// "本 agent 只能写 <c>Assets/Scripts/Module/Bot/**</c>"，所以探针落在本目录、靠 <c>#if UNITY_EDITOR</c> 隔离。
    /// （任务书 §5 写的是 <c>Assets/Editor/BotSelfTest.cs</c>，路径冲突已在交付回报里说明。）</para>
    ///
    /// <para><b>两个入口</b>：</para>
    /// <list type="number">
    /// <item><c>Clover/自检/机器人 3 档难度（4v4 快进 3×45s）</c> —— 自包含：假地图 + 假时钟 + 受击体 +
    /// **真实的 <see cref="CsBotBrain"/>**（不是我另写的简化大脑），三档各跑一轮，打印
    /// <c>shots / hits / acc / kills</c> 对比表。它是验收表 B1~B3 的可复现证据（不用进 Play、不用等真实 45 秒）。</item>
    /// <item><c>Clover/自检/机器人 当前统计（Play 模式）</c> 与 <c>Clover/自检/机器人/三档轮换跑（Play 模式）</c>
    /// —— 作用在**真实场景**里的 <see cref="BotModule"/> 上，读它的分档统计 / 轮换难度（对应验收表 B4）。</item>
    /// </list>
    /// </summary>
    public static class BotSelfTest
    {
        private const string Tag = BotModule.Tag;
        private const float FrameDt = 1f / 60f;
        private const float SecondsPerTier = 45f;
        private const float MoveSampleInterval = 0.5f;

        // ==================================================================
        //  ① 三档 4v4 快进对比
        // ==================================================================
        [MenuItem("Clover/自检/机器人 3 档难度（4v4 快进 3×45s）")]
        public static void RunThreeTiers()
        {
            var results = new List<TierResult>(3);

            foreach (var difficulty in new[] { CsBotDifficulty.Easy, CsBotDifficulty.Normal, CsBotDifficulty.Hard })
            {
                TierResult r;
                try
                {
                    r = RunTier(difficulty);
                }
                catch (Exception ex)
                {
                    Emit($"档位 {difficulty} 的自检崩了：{ex}", true);
                    return;
                }
                results.Add(r);
            }

            ReportThreeTiers(results);
        }

        private sealed class TierResult
        {
            public CsBotDifficulty Difficulty;
            public int Shots;
            public int Hits;
            public int Kills;
            public int Damage;
            public int Engages;
            public int RoundEnds;
            public int BombPlanted;
            public int BombDefused;
            public int ErrorLogs;
            public int StuckWarns;
            public float MovedDistance;
            public int Bots;
            public string Note;

            public float Accuracy => Shots > 0 ? (float)Hits / Shots : 0f;
        }

        private static TierResult RunTier(CsBotDifficulty difficulty)
        {
            var result = new TierResult { Difficulty = difficulty };

            var spawned = new List<GameObject>();
            var prevClock = CsMatch.Clock;
            var simTime = 0f;
            var map = new StubMap();
            var match = new CsMatch(map);
            var stats = new BotStats();
            var watcher = new BotWorldWatcher();
            var brains = new Dictionary<long, CsBotBrain>();
            var lastPos = new Dictionary<long, Vector3>();
            var errors = new List<string>();
            Application.LogCallback catcher = (cond, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    result.ErrorLogs++;
                    if (errors.Count < 5) errors.Add(cond);
                }
                else if (type == LogType.Warning && cond != null && cond.Contains("卡住"))
                {
                    result.StuckWarns++;
                }
            };

            var cfg = new CsMatchConfig
            {
                MapName = "bot_selftest",
                BotsPerTeam = 4,                       // 4v4（验收表要求）
                BotDifficulty = difficulty,
                PlayerTeam = CsTeam.CT,
                PlayerName = "SelfTestPlayer",
                RoundsPerHalf = 4,
                RoundTime = 40f,
                FreezeTime = 1f,                       // 冻结 1s：够走一轮"买枪 → 出发"
                StartMoney = 5000,                     // 让三档都能买到自己档位的枪（tier2 的 AK+头盔+雷 ≈ $3800）
                HalfTimeSwap = false,
                FriendlyFire = false,
            };

            // 命中归属：契约的 OnDamaged 不带攻击者，只能按"当前目标 + 刚开火"归属（与 BotModule 同一套逻辑）
            match.OnDamaged += (victim, dmg, headshot, lethal) =>
            {
                if (victim == null) return;
                var brain = BotHitAttribution.Pick(brains.Values, victim.Id, simTime, out _);
                if (brain != null) stats.AddHit(brain.Difficulty, dmg);
            };
            match.OnKill += e =>
            {
                if (e.KillerId == 0) return;
                if (brains.TryGetValue(e.KillerId, out var brain)) stats.AddKill(brain.Difficulty);
            };
            match.OnRoundEnd += _ => result.RoundEnds++;

            var bus = Game.Event;
            Action onPlanted = () => result.BombPlanted++;
            Action onDefused = () => result.BombDefused++;
            if (bus != null)
            {
                bus.On(Events.BombPlanted, onPlanted);
                bus.On(Events.BombDefused, onDefused);
            }

            try
            {
                Application.logMessageReceived += catcher;

                match.Start(cfg);
                CreateHitboxes(match.Actors, spawned);

                for (var i = 0; i < match.Actors.Count; i++)
                {
                    var a = match.Actors[i];
                    if (!a.IsBot) continue;

                    result.Bots++;
                    brains[a.Id] = new CsBotBrain(match, map, watcher.Sense, a.Id, a.Name, a.Difficulty);
                    lastPos[a.Id] = a.Position;
                }

                CsMatch.Clock = () => simTime;

                var frames = Mathf.CeilToInt(SecondsPerTier / FrameDt);
                var brainAccumulator = 0f;
                var moveSampleAccumulator = 0f;

                for (var frame = 0; frame < frames; frame++)
                {
                    // 观测（每帧）：射击次数估算 + 听觉线索
                    watcher.Tick(match.Actors, FrameDt, simTime, false, stats);

                    // 决策（10Hz，与 CsConst.BotTickInterval 一致）
                    brainAccumulator += FrameDt;
                    if (brainAccumulator >= CsConst.BotTickInterval)
                    {
                        var dt = brainAccumulator;
                        brainAccumulator = 0f;

                        foreach (var kv in brains)
                        {
                            var intent = kv.Value.Think(dt, simTime);
                            match.SubmitBotIntent(kv.Key, intent);

                            if (kv.Value.ConsumeEngage()) stats.AddEngage(kv.Value.Difficulty);
                        }
                    }

                    SyncHitboxes(match.Actors, spawned);
                    match.Tick(FrameDt);
                    simTime += FrameDt;

                    // 位移统计（每 0.5s 采一次）：验收表 B7"会走、不卡死"的量化证据
                    moveSampleAccumulator += FrameDt;
                    if (moveSampleAccumulator >= MoveSampleInterval)
                    {
                        moveSampleAccumulator = 0f;
                        for (var i = 0; i < match.Actors.Count; i++)
                        {
                            var a = match.Actors[i];
                            if (!a.IsBot || !a.IsAlive) continue;
                            if (!lastPos.TryGetValue(a.Id, out var prev)) { lastPos[a.Id] = a.Position; continue; }
                            result.MovedDistance += Vector3.Distance(prev, a.Position);
                            lastPos[a.Id] = a.Position;
                        }
                    }
                }
            }
            finally
            {
                Application.logMessageReceived -= catcher;
                CsMatch.Clock = prevClock;
                if (bus != null)
                {
                    bus.Off(Events.BombPlanted, onPlanted);
                    bus.Off(Events.BombDefused, onDefused);
                }
                for (var i = 0; i < spawned.Count; i++)
                {
                    if (spawned[i] != null) UnityEngine.Object.DestroyImmediate(spawned[i]);
                }
            }

            var s = stats.Get(difficulty);
            result.Shots = s.Shots;
            result.Hits = s.Hits;
            result.Kills = s.Kills;
            result.Damage = s.Damage;
            result.Engages = s.Engages;
            if (errors.Count > 0) result.Note = string.Join(" | ", errors);

            return result;
        }

        private static void ReportThreeTiers(List<TierResult> results)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 机器人 3 档难度 自检报告 ==========");
            sb.AppendLine($"每档模拟时长    : {SecondsPerTier:F0}s（{Mathf.CeilToInt(SecondsPerTier / FrameDt)} 帧，假时钟快进）");
            sb.AppendLine("档位      shots  hits    acc     kills  damage  engages  planted/defused  位移(m)  卡住告警");
            foreach (var r in results)
            {
                sb.AppendLine(
                    $"{r.Difficulty,-9} {r.Shots,5} {r.Hits,5}  {r.Accuracy,6:P1}  {r.Kills,5}  {r.Damage,6}  {r.Engages,7}  " +
                    $"{r.BombPlanted,6}/{r.BombDefused,-6}  {r.MovedDistance,7:F1}  {r.StuckWarns,6}");
            }

            foreach (var r in results)
            {
                if (!string.IsNullOrEmpty(r.Note)) sb.AppendLine($"  · {r.Difficulty} 期间有 Error/异常日志：{r.Note}");
            }

            // ---- 结论 ----
            var easy = results[0];
            var normal = results[1];
            var hard = results[2];

            if (easy.Shots == 0 || normal.Shots == 0 || hard.Shots == 0)
            {
                sb.AppendLine(">>> 结论: 无数据（有档位整轮没有射击 —— 检查受击体是否能被射线打到 / 地图标记是否齐）");
            }
            else if (hard.Accuracy > normal.Accuracy && normal.Accuracy > easy.Accuracy)
            {
                sb.AppendLine(">>> 结论: PASS —— 命中率随难度单调上升（Hard > Normal > Easy），与验收表 B1~B3 一致");
            }
            else
            {
                sb.AppendLine(">>> 结论: 需复核 —— 命中率未呈现单调上升。请看上面的原始数值：");
                sb.AppendLine($"      Easy={easy.Accuracy:P1} Normal={normal.Accuracy:P1} Hard={hard.Accuracy:P1}；" +
                              "若某档 shots 极小（<50），说明该档几乎没打成（多打几轮或延长时长再看）");
            }

            sb.AppendLine("备注: 本探针的命中/射击是**估算**（OnDamaged 归属 + NextFireTime 推进量），");
            sb.AppendLine("      用于横向比较三档；绝对值以 Play 里的实战日志为准。");
            sb.AppendLine("============================================");

            var text = sb.ToString();
            Emit(text);
            Debug.Log(text);
        }

        // ==================================================================
        //  ② 真实场景里的 BotModule（Play 模式）
        // ==================================================================
        [MenuItem("Clover/自检/机器人 当前统计（Play 模式）")]
        public static void PrintLiveStats()
        {
            var module = UnityEngine.Object.FindAnyObjectByType<BotModule>();
            if (module == null)
            {
                Emit("当前场景里找不到 BotModule —— 请先进 Stage 场景并进入 Play 模式", true);
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 机器人 当前统计（Play 模式） ==========");
            sb.AppendLine($"就绪={module.IsReady} brain 数={module.BrainCount} 地图={(module.Map != null ? module.Map.MapName : "null")}");

            foreach (var d in new[] { CsBotDifficulty.Easy, CsBotDifficulty.Normal, CsBotDifficulty.Hard })
            {
                var s = module.GetTierStat(d);
                sb.AppendLine($"difficulty={d} shots={s.Shots} hits={s.Hits} acc={s.Accuracy:P1} kills={s.Kills} " +
                              $"damage={s.Damage} engages={s.Engages}");
            }

            var lines = module.DescribeBrains();
            for (var i = 0; i < lines.Count; i++) sb.AppendLine("  · " + lines[i]);
            sb.AppendLine("================================================");

            var text = sb.ToString();
            Emit(text);
            Debug.Log(text);
        }

        [MenuItem("Clover/自检/机器人 三档轮换跑（Play 模式，每档 30s）")]
        public static void RunLiveThreeTierCycle()
        {
            if (!Application.isPlaying)
            {
                Emit("三档轮换跑需要 Play 模式（它要真实时间推进）—— 请先进 Play 再点本菜单", true);
                return;
            }

            _cycleModule = UnityEngine.Object.FindAnyObjectByType<BotModule>();
            if (_cycleModule == null)
            {
                Emit("当前场景里找不到 BotModule", true);
                return;
            }

            _cycleModule.ResetTierStats();
            _cycleIndex = 0;
            _cycleStart = EditorApplication.timeSinceStartup;
            ApplyCycleDifficulty();
            Emit($"三档轮换开始：每档 {CycleSeconds:F0}s（顺序 Easy → Normal → Hard）。" +
                 "结束时打印三档统计 —— 这就是验收表 B4 的证据（难度即时生效）");

            EditorApplication.update -= OnCycleUpdate;
            EditorApplication.update += OnCycleUpdate;
        }

        private const double CycleSeconds = 30.0;
        private static BotModule _cycleModule;
        private static int _cycleIndex;
        private static double _cycleStart;

        private static readonly CsBotDifficulty[] CycleOrder =
        {
            CsBotDifficulty.Easy, CsBotDifficulty.Normal, CsBotDifficulty.Hard,
        };

        private static void ApplyCycleDifficulty()
        {
            var d = CycleOrder[_cycleIndex];
            if (Game.Event != null) Game.Event.Emit<CsBotDifficulty>(Events.SetBotDifficulty, d);
            Emit($"→ 难度切换为 {d}（对已存在的 bot 即时生效；{CycleSeconds:F0}s 后换下一档）");
        }

        private static void OnCycleUpdate()
        {
            if (_cycleModule == null || !Application.isPlaying)
            {
                StopCycle("Play 模式已退出");
                return;
            }

            if (EditorApplication.timeSinceStartup - _cycleStart < CycleSeconds) return;

            _cycleIndex++;
            _cycleStart = EditorApplication.timeSinceStartup;

            if (_cycleIndex >= CycleOrder.Length)
            {
                PrintLiveStats();
                StopCycle("三档轮换完成");
                return;
            }

            ApplyCycleDifficulty();
        }

        private static void StopCycle(string reason)
        {
            EditorApplication.update -= OnCycleUpdate;
            Emit($"三档轮换结束：{reason}");
            _cycleModule = null;
        }

        // ==================================================================
        //  假地图（只为让模拟/导航跑起来；形状取自契约里的 CsMarkers）
        // ==================================================================
        private sealed class StubMap : ICsMap
        {
            private const float Half = 28f;      // 28m × 28m 的方形场地（边界外不可走 → 会触发避障）
            private static readonly Vector3 SpawnT = new Vector3(-20f, 0f, -20f);
            private static readonly Vector3 SpawnCt = new Vector3(20f, 0f, 20f);
            private static readonly Vector3 SiteA = new Vector3(20f, 0f, -20f);
            private static readonly Vector3 SiteB = new Vector3(-20f, 0f, 20f);
            private static readonly Vector3 Mid = new Vector3(0f, 0f, 0f);

            public bool IsLoaded => true;
            public string MapName => "bot_selftest";
            public string Status => "stub";
            public int SpawnPointCount => 8;

            public void LoadAsync(string mapResourcePath, Action onLoaded, Action<string> onFailed = null) => onLoaded?.Invoke();
            public void Unload() { }

            public bool WalkableAt(float x, float z) => Mathf.Abs(x) <= Half && Mathf.Abs(z) <= Half;

            public bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius)
            {
                if (!WalkableAt(pos.x, pos.z)) return false;
                for (var i = 0; i < 8; i++)
                {
                    var ang = i * 45f * Mathf.Deg2Rad;
                    if (!WalkableAt(pos.x + Mathf.Cos(ang) * radius, pos.z + Mathf.Sin(ang) * radius)) return false;
                }
                return true;
            }

            /// <summary>假地图不做精细碰撞：钳在场地内（边界能挡住人 → 避障逻辑会被走到）。</summary>
            public Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius)
            {
                return new Vector3(
                    Mathf.Clamp(to.x, -Half, Half),
                    to.y,
                    Mathf.Clamp(to.z, -Half, Half));
            }

            public float SampleGround(Vector3 pos, float maxDrop = 8f)
            {
                return WalkableAt(pos.x, pos.z) ? 0f : float.NegativeInfinity;
            }

            /// <summary>假地图的地面 = y 0 的水平面（法线朝上 ⇒ 永远是可站立的缓面；场外无地面）。</summary>
            public bool TrySampleGround(Vector3 pos, out Vector3 point, out Vector3 normal, float maxDrop = 8f)
            {
                normal = Vector3.up;
                if (!WalkableAt(pos.x, pos.z))
                {
                    point = default;
                    return false;
                }
                point = new Vector3(pos.x, 0f, pos.z);
                return true;
            }

            public Vector3 GetSpawnPoint(int index)
            {
                var basePos = index % 2 == 0 ? SpawnT : SpawnCt;
                return basePos + new Vector3(CsConst.PlayerRadius * 2f * (index / 2), 0f, 0f);
            }

            public Vector3[] Points(string marker)
            {
                switch (marker)
                {
                    case CsMarkers.SpawnT: return new[] { SpawnT };
                    case CsMarkers.SpawnCT: return new[] { SpawnCt };
                    case CsMarkers.BuyZoneT: return new[] { SpawnT };
                    case CsMarkers.BuyZoneCT: return new[] { SpawnCt };
                    case CsMarkers.BombsiteA: return new[] { SiteA };
                    case CsMarkers.BombsiteB: return new[] { SiteB };

                    case CsMarkers.TAttackA:
                        return new[]
                        {
                            new Vector3(-16f, 0f, -20f), new Vector3(-8f, 0f, -20f),
                            new Vector3(4f, 0f, -20f), new Vector3(14f, 0f, -20f),
                        };
                    case CsMarkers.TAttackB:
                        return new[]
                        {
                            new Vector3(-20f, 0f, -16f), new Vector3(-20f, 0f, -8f),
                            new Vector3(-20f, 0f, 4f), new Vector3(-20f, 0f, 14f),
                        };
                    case CsMarkers.TMid:
                        return new[]
                        {
                            new Vector3(-14f, 0f, -14f), new Vector3(-4f, 0f, -4f),
                            new Vector3(4f, 0f, 4f), new Vector3(14f, 0f, 14f),
                        };
                    case CsMarkers.CTDefendA:
                        return new[]
                        {
                            new Vector3(20f, 0f, 16f), new Vector3(20f, 0f, 8f),
                            new Vector3(20f, 0f, -4f), new Vector3(20f, 0f, -14f),
                        };
                    case CsMarkers.CTDefendB:
                        return new[]
                        {
                            new Vector3(16f, 0f, 20f), new Vector3(8f, 0f, 20f),
                            new Vector3(-4f, 0f, 20f), new Vector3(-14f, 0f, 20f),
                        };
                    case CsMarkers.CTMid:
                        return new[]
                        {
                            new Vector3(4f, 0f, 4f), new Vector3(-4f, 0f, -4f), Mid,
                        };
                    case CsMarkers.Patrol:
                        return new[]
                        {
                            new Vector3(20f, 0f, -14f), new Vector3(14f, 0f, -20f),
                            new Vector3(-20f, 0f, 14f), new Vector3(-14f, 0f, 20f),
                            new Vector3(12f, 0f, 0f), new Vector3(0f, 0f, 12f),
                            new Vector3(-12f, 0f, 0f), new Vector3(0f, 0f, -12f),
                        };
                    default:
                        Emit($"StubMap.Points：未知标记 '{marker}' → 返回空数组", true);
                        return new Vector3[0];
                }
            }

            public bool TryGetPoint(string marker, out Vector3 point)
            {
                var pts = Points(marker);
                if (pts.Length == 0) { point = default; return false; }
                point = pts[UnityEngine.Random.Range(0, pts.Length)];
                return true;
            }
        }

        // ==================================================================
        //  受击体（否则机器人射线打不到任何人 → hits=0）
        // ==================================================================
        private static void CreateHitboxes(IReadOnlyList<CsActor> actors, List<GameObject> spawned)
        {
            for (var i = 0; i < actors.Count; i++)
            {
                spawned.Add(NewHitbox(actors[i].Id, CsHitbox.Chest, "Chest"));
                spawned.Add(NewHitbox(actors[i].Id, CsHitbox.Head, "Head"));
            }
        }

        private static GameObject NewHitbox(long actorId, CsHitbox box, string suffix)
        {
            var go = new GameObject($"BotSelfTest_{actorId}_{suffix}");
            var col = go.AddComponent<SphereCollider>();
            col.radius = box == CsHitbox.Head ? CsConst.PlayerRadius : CsConst.PlayerRadius * 2f;
            col.isTrigger = false;

            var proxy = go.AddComponent<CsHitboxProxy>();
            proxy.ActorId = actorId;
            proxy.Hitbox = box;
            return go;
        }

        /// <summary>把受击体跟到 actor 身上，并 <c>Physics.SyncTransforms()</c>（否则物理查询看不到新位置）。</summary>
        private static void SyncHitboxes(IReadOnlyList<CsActor> actors, List<GameObject> spawned)
        {
            var k = 0;
            for (var i = 0; i < actors.Count && k + 1 < spawned.Count; i++)
            {
                var a = actors[i];
                var chest = spawned[k++];
                var head = spawned[k++];
                if (chest == null || head == null) continue;

                if (a.IsAlive)
                {
                    chest.transform.position = a.Position + new Vector3(0f, CsConst.EyeHeight * 0.7f, 0f);
                    head.transform.position = a.Position + new Vector3(0f, CsConst.EyeHeight, 0f);
                }
                else
                {
                    chest.transform.position = new Vector3(0f, -1000f, 0f);
                    head.transform.position = new Vector3(0f, -1000f, 0f);
                }
            }

            Physics.SyncTransforms();
        }

        // ==================================================================
        //  日志（Game.Logger 优先；探针在 Game 未 Launch 时退化到 Debug.Log）
        // ==================================================================
        private static void Emit(string message, bool error = false)
        {
            var logger = Game.Logger;
            if (logger != null)
            {
                if (error) logger.Error(Tag, message);
                else logger.Info(Tag, message);
                return;
            }

            if (error) Debug.LogError($"[{Tag}] {message}");
            else Debug.Log($"[{Tag}] {message}");
        }
    }
}
#endif
