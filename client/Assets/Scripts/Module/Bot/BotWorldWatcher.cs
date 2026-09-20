using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;

namespace Cs16.Module.Bot
{
    /// <summary>一条"声音线索"（枪声 / 跑动声）。**本项目新增**。</summary>
    public struct CsBotNoise
    {
        public long ActorId;
        public CsTeam Team;
        public UnityEngine.Vector3 Position;
        public float Time;
        public bool Gunshot;
    }

    /// <summary>
    /// 听觉线索池：机器人 AI 的"耳朵"（任务书 §4.2 听觉）。
    ///
    /// <para><b>为什么不做成"射击事件订阅"</b>：契约里没有"某人开了一枪"的事件
    /// （<see cref="Events"/> 里只有炸弹/回合/击杀类事件，<c>ICsMatch.ConsumeShotFired</c> 是给表现层消费的
    /// 队列 —— 机器人去消费会**抢走**表现层的射击记录）。所以听觉从**可观测的权威状态**推出来：
    /// <c>CsActor.NextFireTime</c> 只在真正扣扳机时被推进（见 <c>CsInventory.TryDischarge</c>），
    /// 跑动看 <c>CsActor.Velocity</c> —— 两者都是模拟写、我只读。</para>
    /// </summary>
    public sealed class BotSense
    {
        private readonly List<CsBotNoise> _items = new List<CsBotNoise>(CsBotConst.NoiseCapacity);

        public int Count => _items.Count;

        public void Push(in CsBotNoise noise)
        {
            if (_items.Count >= CsBotConst.NoiseCapacity) _items.RemoveAt(0);
            _items.Add(noise);
        }

        public void Clear()
        {
            _items.Clear();
        }

        /// <summary>丢弃过期线索（超过 <see cref="CsBotConst.NoiseMemorySeconds"/>）。</summary>
        public void Expire(float now)
        {
            for (var i = _items.Count - 1; i >= 0; i--)
            {
                if (now - _items[i].Time > CsBotConst.NoiseMemorySeconds) _items.RemoveAt(i);
            }
        }

        /// <summary>
        /// 听最近的**敌方**声响（<see cref="CsConst.BotHearRadius"/> 内）。枪声比跑动更"可信"，
        /// 同样距离下优先返回枪声（权重式，不用第二个魔法半径）。
        /// </summary>
        public bool TryHearEnemy(CsTeam selfTeam, UnityEngine.Vector3 listener, float radius,
            out UnityEngine.Vector3 position, out float time, out bool gunshot)
        {
            position = default;
            time = 0f;
            gunshot = false;

            var r2 = radius * radius;
            var bestScore = float.MaxValue;
            var found = false;

            for (var i = 0; i < _items.Count; i++)
            {
                var n = _items[i];
                if (n.Team == selfTeam) continue;
                if (n.Team != CsTeam.T && n.Team != CsTeam.CT) continue;

                var d = n.Position - listener;
                d.y = 0f;
                var sq = d.sqrMagnitude;
                if (sq > r2) continue;

                var score = n.Gunshot ? sq * 0.5f : sq;
                if (score >= bestScore) continue;

                bestScore = score;
                position = n.Position;
                time = n.Time;
                gunshot = n.Gunshot;
                found = true;
            }

            return found;
        }
    }

    /// <summary>
    /// 逐 tick 扫描**全部 actor**，产出两样东西：
    /// ① 听觉线索（喂 <see cref="BotSense"/>）；② 按难度累计的**射击次数估算**（喂 <see cref="BotStats"/>）。
    ///
    /// <para><b>射击次数为什么是"估算"</b>：模拟内部的机器人射线是自己打的（我提交 <c>Fire=true</c> 后由
    /// <c>CsMatch.UpdateBots</c> → <c>CsInventory.TryDischarge</c> 落地），过程中没有回调给 AI。
    /// 可观测的痕迹是 <c>NextFireTime</c>：每开一枪它被推进一个 <c>SecondsPerShot</c>，
    /// 所以 <c>Δ(NextFireTime) / SecondsPerShot</c> 就是这段时间内开出的枪数（至少 1，且按 dt 封顶）。
    /// 命中数则来自 <c>ICsMatch.OnDamaged</c> 的归属（见 <see cref="BotHitAttribution"/>）。</para>
    /// </summary>
    internal sealed class BotWorldWatcher
    {
        private readonly Dictionary<long, float> _lastNextFire = new Dictionary<long, float>(32);
        private readonly Dictionary<long, float> _nextRunNoiseAt = new Dictionary<long, float>(32);
        private readonly Dictionary<long, float> _nextGunNoiseAt = new Dictionary<long, float>(32);
        private bool _pausedLastTick;
        private bool _warnedNoWeapon;

        public BotSense Sense { get; } = new BotSense();

        public void Reset()
        {
            _lastNextFire.Clear();
            _nextRunNoiseAt.Clear();
            _nextGunNoiseAt.Clear();
            Sense.Clear();
            _pausedLastTick = false;
            _warnedNoWeapon = false;
        }

        /// <summary>扫描一帧。<paramref name="stats"/> 为 null 时只收线索、不记统计（模拟未开局时的空转）。</summary>
        public void Tick(IReadOnlyList<CsActor> actors, float dt, float now, bool paused, BotStats stats)
        {
            if (actors == null) return;

            if (paused)
            {
                // 暂停恢复后 CsMatch 会把全部绝对时间整体后移（见它的 ShiftAbsoluteTimes），
                // 采样基准必须清掉 —— 否则会被误判成"一口气打了一串"，统计虚高。只在刚进暂停时清一次。
                if (!_pausedLastTick)
                {
                    _lastNextFire.Clear();
                    _pausedLastTick = true;
                }
                return;
            }
            _pausedLastTick = false;

            Sense.Expire(now);

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;

                if (!a.IsAlive)
                {
                    _lastNextFire.Remove(a.Id);
                    continue;
                }

                var nextFire = a.NextFireTime;
                if (_lastNextFire.TryGetValue(a.Id, out var prev) && nextFire > prev + 0.0001f)
                {
                    var shots = EstimateShots(a, dt, nextFire - prev);
                    if (stats != null && a.IsBot) stats.AddShots(a.Difficulty, shots);

                    _nextGunNoiseAt.TryGetValue(a.Id, out var gunAt);
                    if (now >= gunAt)
                    {
                        _nextGunNoiseAt[a.Id] = now + CsBotConst.GunshotNoiseInterval;
                        Sense.Push(new CsBotNoise
                        {
                            ActorId = a.Id,
                            Team = a.Team,
                            Position = a.Position,
                            Time = now,
                            Gunshot = true,
                        });
                    }
                }
                _lastNextFire[a.Id] = nextFire;

                var v = a.Velocity;
                var speed = UnityEngine.Mathf.Sqrt(v.x * v.x + v.z * v.z);
                if (speed >= CsBotConst.RunNoiseSpeed)
                {
                    _nextRunNoiseAt.TryGetValue(a.Id, out var runAt);
                    if (now >= runAt)
                    {
                        _nextRunNoiseAt[a.Id] = now + CsBotConst.RunNoiseInterval;
                        Sense.Push(new CsBotNoise
                        {
                            ActorId = a.Id,
                            Team = a.Team,
                            Position = a.Position,
                            Time = now,
                            Gunshot = false,
                        });
                    }
                }
            }
        }

        private int EstimateShots(CsActor a, float dt, float advanced)
        {
            var def = a.ActiveDef;
            if (def == null)
            {
                if (!_warnedNoWeapon)
                {
                    _warnedNoWeapon = true;
                    Game.Logger.Warn(BotModule.Tag,
                        $"射击次数估算：{a.Name} 推进了 NextFireTime 却没有手持武器（武器表里查不到 {a.ActiveWeapon ?? "null"}），" +
                        "本次按 1 发计（本条只报一次）");
                }
                return 1;
            }

            var sps = def.SecondsPerShot;
            if (sps <= 0f) return 1;

            var shots = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(advanced / sps));
            var cap = UnityEngine.Mathf.CeilToInt(dt / sps) + 1;
            if (cap < 1) cap = 1;
            if (shots > cap) shots = cap;
            if (shots > CsBotConst.MaxShotEstimatePerTick) shots = CsBotConst.MaxShotEstimatePerTick;
            return shots;
        }
    }

    /// <summary>
    /// 把"某 actor 受到伤害"归属到**某个机器人**身上（验收表 B1~B3 的命中率分母/分子）。
    ///
    /// <para><b>为什么需要归属</b>：<c>ICsMatch.OnDamaged</c> 的回调签名是
    /// <c>(victim, damage, headshot, lethal)</c> —— **没有攻击者**（契约如此，我不改）。所以用两条
    /// 可观测条件做归属：该 bot 当前交战目标就是这名受击者，且它在
    /// <see cref="CsBotConst.HitAttributionWindow"/> 内刚提交过 <c>Fire=true</c>。
    /// 多个候选时取时间最近的一个并降频告警（枪声重叠时无法完全区分，如实说明而非假装准确）。</para>
    /// </summary>
    internal static class BotHitAttribution
    {
        public static CsBotBrain Pick(IEnumerable<CsBotBrain> brains, long victimId, float now, out int candidates)
        {
            candidates = 0;
            CsBotBrain best = null;
            var bestGap = float.MaxValue;

            foreach (var brain in brains)
            {
                if (brain == null) continue;
                if (!brain.IsEngaging || brain.TargetId != victimId) continue;

                var gap = now - brain.LastFireIntentTime;
                if (gap > CsBotConst.HitAttributionWindow) continue;

                candidates++;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = brain;
                }
            }

            return best;
        }
    }

    /// <summary>
    /// 分难度累计的机器人统计（**验收证据**：<c>difficulty=Hard shots=.. hits=.. acc=..</c>）。
    /// </summary>
    internal sealed class BotStats
    {
        private readonly BotTierStat[] _tiers = new BotTierStat[3];

        public BotTierStat Get(CsBotDifficulty d)
        {
            var i = Index(d);
            return _tiers[i];
        }

        public void AddShots(CsBotDifficulty d, int shots)
        {
            if (shots <= 0) return;
            var i = Index(d);
            _tiers[i].Shots += shots;
        }

        public void AddHit(CsBotDifficulty d, int damage)
        {
            var i = Index(d);
            _tiers[i].Hits++;
            if (damage > 0) _tiers[i].Damage += damage;
        }

        public void AddKill(CsBotDifficulty d)
        {
            var i = Index(d);
            _tiers[i].Kills++;
        }

        public void AddEngage(CsBotDifficulty d)
        {
            var i = Index(d);
            _tiers[i].Engages++;
        }

        public void ResetAll()
        {
            for (var i = 0; i < _tiers.Length; i++) _tiers[i] = default;
        }

        public string Line(CsBotDifficulty d)
        {
            var s = _tiers[Index(d)];
            var acc = s.Shots > 0 ? (float)s.Hits / s.Shots : 0f;
            return $"difficulty={d} shots={s.Shots} hits={s.Hits} acc={acc:P1} kills={s.Kills} " +
                   $"damage={s.Damage} engages={s.Engages}";
        }

        private static int Index(CsBotDifficulty d)
        {
            var i = (int)d;
            if (i < 0 || i >= 3)
            {
                // 越界 = 难度值非法（枚举被写坏/回包被篡改）→ 留痕并归到 Normal，不许静默。
                Game.Logger.Warn(BotModule.Tag, $"统计收到非法难度值 {(int)d}，已归入 Normal");
                return 1;
            }
            return i;
        }
    }
}
