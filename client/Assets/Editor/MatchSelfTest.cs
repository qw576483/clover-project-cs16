using System;
using System.Collections.Generic;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.Map;
using UnityEditor;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 比赛核心模拟的自检探针（任务书 §5 要求）。
    ///
    /// <para>它做的事：造一个假地图 → <c>Start(cfg)</c> → 给 4v4 机器人每帧下发意图 → 用假时钟
    /// **快进 60 秒**模拟 → 断言：回合推进过 ≥1 次、发生过击杀、比分变化、无异常日志、数值自洽。</para>
    ///
    /// <para><b>用假时钟快进的理由</b>：<see cref="CsMatch.Clock"/> 是可替换的时间源（默认
    /// <see cref="Time.time"/>）。不换的话，一次自检要真等 60 秒；换了之后 3600 帧瞬间跑完。</para>
    ///
    /// <para>用法：编辑器菜单 <c>Clover/自检/比赛核心模拟（快进 90s）</c>。它临时创建受击体 GameObject，
    /// 跑完会全部销毁（含异常路径）。</para>
    ///
    /// <para><b>若"击杀=0"而其它项都过</b>：多半是 EditMode 下物理查询没生效（射线打不到受击体）。
    /// 进 Play 模式后再点一次菜单即可确证。</para>
    /// </summary>
    public static class MatchSelfTest
    {
        private const int SimFrames = 5400;                  // 90 秒 × 60 帧（够跑到半场交换甚至 MatchEnd）
        private const float FrameDt = 1f / 60f;
        private const string HostName = "[MatchSelfTest]";

        [MenuItem("Clover/自检/比赛核心模拟（快进 90s）")]
        public static void Run()
        {
            var spawned = new List<GameObject>();
            var prevClock = CsMatch.Clock;
            var errors = new List<string>();
            Application.LogCallback catcher = (cond, stack, type) =>
            {
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    errors.Add(cond);
                }
            };

            var kills = 0;
            var roundEnds = 0;
            var roundStarts = 0;
            var bombPlanted = 0;
            var bombDefused = 0;
            var bombExploded = 0;
            var firstRound = 0;
            var lastRound = 0;
            var startScore = 0;
            var endScore = 0;
            var tickExceptions = 0;
            string firstException = null;

            try
            {
                var map = new StubMap();
                var match = new CsMatch(map);

                var cfg = new CsMatchConfig
                {
                    MapName = "selftest",
                    BotsPerTeam = 4,
                    BotDifficulty = CsBotDifficulty.Normal,
                    PlayerTeam = CsTeam.CT,
                    PlayerName = "SelfTestPlayer",
                    RoundsPerHalf = 3,      // 缩短半场，让"半场交换"也能被覆盖
                    RoundTime = 10f,        // 缩短回合，让回合推进/比分变化必然发生
                    FreezeTime = 0.5f,
                    StartMoney = CsConst.StartMoney,
                    HalfTimeSwap = true,
                    FriendlyFire = false,
                };

                match.OnKill += _ => kills++;
                match.OnRoundEnd += _ => roundEnds++;
                match.OnBombStateChanged += () => { };
                // 事件总线上的炸弹事件（CsMatch 会 Emit）
                Action<int> onRoundStarted = _ => roundStarts++;

                var bus = CloverEngine.Game.Event;
                if (bus != null)
                {
                    bus.On<int>(Events.RoundStarted, onRoundStarted);
                    bus.On(Events.BombPlanted, () => bombPlanted++);
                    bus.On(Events.BombDefused, () => bombDefused++);
                    bus.On(Events.BombExploded, () => bombExploded++);
                }

                Application.logMessageReceived += catcher;

                match.Start(cfg);
                firstRound = match.RoundNumber;
                startScore = match.ScoreT + match.ScoreCT;

                var actors = match.Actors;
                CreateHitboxes(actors, spawned);

                // 预检：受击体之间能不能被射线打到？（打不到的话"击杀"必然为 0，得先排除环境问题）
                var physicsPath = ProbePhysicsPath(actors, spawned);

                // ---- 快进模拟 ----
                var simTime = 0f;
                CsMatch.Clock = () => simTime;
                try
                {
                    for (var i = 0; i < SimFrames; i++)
                    {
                        SubmitBrains(match, simTime);
                        SyncHitboxes(actors, spawned);
                        try
                        {
                            match.Tick(FrameDt);
                        }
                        catch (Exception ex)
                        {
                            tickExceptions++;
                            if (firstException == null) firstException = ex.GetType().Name + ": " + ex.Message;
                        }
                        simTime += FrameDt;
                    }
                }
                finally
                {
                    CsMatch.Clock = prevClock;
                }

                lastRound = match.RoundNumber;
                endScore = match.ScoreT + match.ScoreCT;

                // ---- 数值自洽 ----
                var sanity = new List<string>();
                for (var i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (float.IsNaN(a.Position.x) || float.IsNaN(a.Position.y) || float.IsNaN(a.Position.z))
                        sanity.Add($"{a.Name} 位置为 NaN");
                    if (a.Health < 0 || a.Health > CsConst.MaxHealth)
                        sanity.Add($"{a.Name} 血量越界 {a.Health}");
                    if (a.Armor < 0 || a.Armor > CsConst.MaxArmor)
                        sanity.Add($"{a.Name} 护甲越界 {a.Armor}");
                    if (a.Money < 0 || a.Money > CsConst.MaxMoney)
                        sanity.Add($"{a.Name} 金钱越界 {a.Money}");
                }
                if (!CsHudSnapshot.Valid) sanity.Add("CsHudSnapshot.Valid == false");

                // ---- 汇总 ----
                var failed = new List<string>();
                if (roundEnds < 1) failed.Add($"回合未推进过（roundEnds={roundEnds}）");
                if (lastRound <= firstRound) failed.Add($"RoundNumber 未增长（{firstRound} → {lastRound}）");
                if (kills < 1)
                {
                    failed.Add(physicsPath
                        ? $"没有发生任何击杀（kills=0）—— 射线通路正常，说明是打法/参数问题"
                        : "没有发生任何击杀，且**射线打不到受击体**：EditMode 下 Physics 查询可能不可用，请进 Play 模式重跑");
                }
                if (endScore <= startScore) failed.Add($"比分未变化（{startScore} → {endScore}）");
                if (tickExceptions > 0) failed.Add($"Tick 抛异常 {tickExceptions} 次：{firstException}");
                if (errors.Count > 0) failed.Add($"模拟期间出现 {errors.Count} 条 Error/Exception 日志");
                if (sanity.Count > 0) failed.Add("数值越界 " + sanity.Count + " 处");

                Report(match, kills, roundEnds, roundStarts, bombPlanted, bombDefused, bombExploded,
                    firstRound, lastRound, startScore, endScore, tickExceptions, errors, sanity, failed,
                    physicsPath);

                if (bus != null)
                {
                    bus.Off<int>(Events.RoundStarted, onRoundStarted);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{HostName} 自检本身崩了：{ex}");
            }
            finally
            {
                Application.logMessageReceived -= catcher;
                CsMatch.Clock = prevClock;
                for (var i = 0; i < spawned.Count; i++)
                {
                    if (spawned[i] != null) UnityEngine.Object.DestroyImmediate(spawned[i]);
                }
            }
        }

        // ==================================================================
        //  假地图（只为让模拟能跑；不含任何真实几何）
        // ==================================================================
        private sealed class StubMap : ICsMap
        {
            private static readonly Vector3 SpawnTPos = new Vector3(-8f, 0f, -8f);
            private static readonly Vector3 SpawnCTPos = new Vector3(8f, 0f, 8f);
            private static readonly Vector3 SiteAPos = new Vector3(10f, 0f, -10f);
            private static readonly Vector3 SiteBPos = new Vector3(-10f, 0f, 10f);

            public bool IsLoaded => true;
            public string MapName => "selftest";
            public string Status => "stub";
            public int SpawnPointCount => 2;

            public void LoadAsync(string mapResourcePath, Action onLoaded, Action<string> onFailed = null) => onLoaded?.Invoke();
            public void Unload() { }
            public bool WalkableAt(float x, float z) => true;
            public bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius) => true;

            /// <summary>假地图不做碰撞：原样返回目标点（模拟照样能移动/结算）。</summary>
            public Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius) => to;

            public float SampleGround(Vector3 pos, float maxDrop = 8f) => 0f;

            /// <summary>假地图的地面 = y 0 的水平面（法线朝上 ⇒ 永远是可站立的缓面）。</summary>
            public bool TrySampleGround(Vector3 pos, out Vector3 point, out Vector3 normal, float maxDrop = 8f)
            {
                point = new Vector3(pos.x, 0f, pos.z);
                normal = Vector3.up;
                return true;
            }

            public Vector3 GetSpawnPoint(int index) => index % 2 == 0 ? SpawnTPos : SpawnCTPos;

            public Vector3[] Points(string marker)
            {
                switch (marker)
                {
                    case CsMarkers.SpawnT: return new[] { SpawnTPos };
                    case CsMarkers.SpawnCT: return new[] { SpawnCTPos };
                    case CsMarkers.BuyZoneT: return new[] { SpawnTPos };
                    case CsMarkers.BuyZoneCT: return new[] { SpawnCTPos };
                    case CsMarkers.BombsiteA: return new[] { SiteAPos };
                    case CsMarkers.BombsiteB: return new[] { SiteBPos };
                    case CsMarkers.Patrol: return new[] { SpawnTPos, SpawnCTPos, SiteAPos, SiteBPos };
                    default: return new Vector3[0];
                }
            }

            public bool TryGetPoint(string marker, out Vector3 point)
            {
                var pts = Points(marker);
                if (pts.Length == 0) { point = default; return false; }
                point = pts[0];
                return true;
            }
        }

        // ==================================================================
        //  受击体（否则射击射线打不到任何人，就不可能有击杀）
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
            var go = new GameObject($"SelfTest_{actorId}_{suffix}");
            var col = go.AddComponent<SphereCollider>();
            col.radius = box == CsHitbox.Head ? CsConst.PlayerRadius : CsConst.PlayerRadius * 2f;
            col.isTrigger = false;
            var proxy = go.AddComponent<CsHitboxProxy>();
            proxy.ActorId = actorId;
            proxy.Hitbox = box;
            return go;
        }

        /// <summary>把受击体跟随 actor（并 SyncTransforms，否则编辑器的物理查询看不到新位置）。</summary>
        private static void SyncHitboxes(IReadOnlyList<CsActor> actors, List<GameObject> spawned)
        {
            var k = 0;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (k + 1 >= spawned.Count) break;
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
                    // 死亡 → 挪到地下，避免被继续打
                    chest.transform.position = new Vector3(0f, -1000f, 0f);
                    head.transform.position = new Vector3(0f, -1000f, 0f);
                }
            }
            Physics.SyncTransforms();
        }

        // ==================================================================
        //  极简 bot 大脑（探针专用；真 AI 由 agent-05 提供）
        // ==================================================================
        private static void SubmitBrains(CsMatch match, float now)
        {
            var actors = match.Actors;
            var needDefuse = match.BombPlanted;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (!a.IsBot) continue;

                var intent = new CsBotIntent { AimPoint = a.EyePosition, State = CsBotState.Patrol };

                if (!a.IsAlive)
                {
                    match.SubmitBotIntent(a.Id, intent);
                    continue;
                }

                var enemy = NearestEnemy(actors, a);

                // 交火优先：有敌人就压上去打（这样才能产生击杀）
                if (enemy != null)
                {
                    intent.State = CsBotState.Engage;
                    intent.AimPoint = enemy.Position + new Vector3(0f, CsConst.EyeHeight, 0f);
                    var to = enemy.Position - a.Position;
                    to.y = 0f;
                    intent.Move = to.sqrMagnitude > 1f ? to.normalized : Vector3.zero;
                    intent.Fire = true;
                }
                else if (needDefuse && a.Team == CsTeam.CT)
                {
                    intent.State = CsBotState.Defuse;
                    var bomb = match.BombPosition;
                    intent.AimPoint = bomb;
                    var to = bomb - a.Position;
                    to.y = 0f;
                    intent.Move = to.sqrMagnitude > 0.6f ? to.normalized : Vector3.zero;
                    intent.Use = to.sqrMagnitude <= 0.6f;
                }
                else if (a.HasBomb)
                {
                    // 持包 T 去 A 点下包 —— 覆盖"下包 → 爆炸/拆包"这条链
                    intent.State = CsBotState.Plant;
                    var site = SiteA();
                    intent.AimPoint = site;
                    var to = site - a.Position;
                    to.y = 0f;
                    intent.Move = to.sqrMagnitude > 0.6f ? to.normalized : Vector3.zero;
                    intent.Use = to.sqrMagnitude <= 0.6f;
                }
                else
                {
                    intent.AimPoint = a.Position + new Vector3(0f, CsConst.EyeHeight, 0f) + a.Velocity;
                    intent.Move = Vector3.zero;
                }

                match.SubmitBotIntent(a.Id, intent);
            }
        }

        private static Vector3 SiteA() => new Vector3(10f, 0f, -10f);

        private static CsActor NearestEnemy(IReadOnlyList<CsActor> actors, CsActor self)
        {
            CsActor best = null;
            var bestSq = float.MaxValue;
            for (var i = 0; i < actors.Count; i++)
            {
                var o = actors[i];
                if (!o.IsAlive) continue;
                if (o.Team == self.Team) continue;
                if (o.Team != CsTeam.T && o.Team != CsTeam.CT) continue;

                var d = (o.Position - self.Position).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = o; }
            }
            return best;
        }

        // ==================================================================
        //  报告
        // ==================================================================
        /// <summary>受击体之间做一次试探射线，判断物理查询在这台机器/这个模式下是否可用。</summary>
        private static bool ProbePhysicsPath(IReadOnlyList<CsActor> actors, List<GameObject> spawned)
        {
            try
            {
                if (spawned.Count < 4) return false;
                var a = spawned[0];   // actor0 的胸
                var b = spawned[2];   // actor1 的胸（T 与 CT 各一个）
                if (a == null || b == null) return false;

                // 把受击体摆到已知位置（不依赖游戏的出生点）
                a.transform.position = new Vector3(0f, 0f, 0f);
                b.transform.position = new Vector3(0f, 0f, 10f);
                Physics.SyncTransforms();

                var origin = new Vector3(0f, 0f, -1f);
                var n = Physics.RaycastNonAlloc(origin, Vector3.forward, new RaycastHit[8], 20f, ~0, QueryTriggerInteraction.Ignore);
                return n > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void Report(CsMatch match, int kills, int roundEnds, int roundStarts,
            int planted, int defused, int exploded, int firstRound, int lastRound,
            int startScore, int endScore, int tickExceptions,
            List<string> errors, List<string> sanity, List<string> failed, bool physicsPath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("========== 比赛核心模拟 自检报告 ==========");
            sb.AppendLine($"模拟时长        : {SimFrames * FrameDt:F0}s（{SimFrames} 帧，假时钟快进）");
            sb.AppendLine($"回合            : {firstRound} → {lastRound}（RoundStarted {roundStarts} 次，RoundEnd {roundEnds} 次）");
            sb.AppendLine($"比分            : CT {match.ScoreCT} : {match.ScoreT} T（总回合胜场 {startScore} → {endScore}）");
            sb.AppendLine($"击杀            : {kills}");
            sb.AppendLine($"炸弹            : 下包 {planted} / 拆包 {defused} / 爆炸 {exploded}");
            sb.AppendLine($"终局阶段        : {match.Phase}");
            sb.AppendLine($"现场人数        : T 存活 {match.AliveCount(CsTeam.T)}，CT 存活 {match.AliveCount(CsTeam.CT)}");
            sb.AppendLine($"Tick 异常       : {tickExceptions}");
            sb.AppendLine($"射线通路        : {(physicsPath ? "正常（受击体可被命中）" : "不可用 —— 多半是 EditMode 下物理查询未生效")}");

            if (errors.Count > 0)
            {
                sb.AppendLine($"Error/异常日志   : {errors.Count} 条（前 5 条）");
                for (var i = 0; i < Math.Min(5, errors.Count); i++) sb.AppendLine("   · " + errors[i]);
            }
            else
            {
                sb.AppendLine("Error/异常日志   : 0 条");
            }

            if (sanity.Count > 0)
            {
                sb.AppendLine($"数值越界        : {sanity.Count} 处（前 5 条）");
                for (var i = 0; i < Math.Min(5, sanity.Count); i++) sb.AppendLine("   · " + sanity[i]);
            }
            else
            {
                sb.AppendLine("数值越界        : 无（血量/护甲/金钱/坐标全部自洽）");
            }

            sb.AppendLine(failed.Count == 0 ? ">>> 结论: PASS" : $">>> 结论: FAIL（{failed.Count} 项未达标）");
            for (var i = 0; i < failed.Count; i++) sb.AppendLine("   ✗ " + failed[i]);
            sb.AppendLine("==========================================");

            var text = sb.ToString();
            if (failed.Count == 0) Debug.Log(text);
            else Debug.LogError(text);
        }
    }
}
