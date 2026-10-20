using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 一个敌人的**短时记忆**：可见期间每个决策 tick 刷新，丢视野后保留
    /// <see cref="WindowSeconds"/> 秒（按难度分档）。**本项目新增**。
    ///
    /// <para>战斗侧用它做"目标不见了但还没忘记"的外推（<see cref="Extrapolate"/>），
    /// 而不是去读敌人的实时坐标 —— 后者等于隔墙透视。</para>
    /// </summary>
    public readonly struct CsBotEnemyMemory
    {
        public readonly long ActorId;

        /// <summary>最后一次被看见时的眼位（世界坐标）。</summary>
        public readonly Vector3 LastSeenPos;

        /// <summary>最后一次被看见时的水平速度（外推用）。</summary>
        public readonly Vector3 Velocity;

        public readonly float LastSeenTime;

        /// <summary>该档的记忆时长（秒）：<c>now - LastSeenTime</c> 超过它即失效。</summary>
        public readonly float WindowSeconds;

        public CsBotEnemyMemory(long actorId, Vector3 lastSeenPos, Vector3 velocity,
            float lastSeenTime, float windowSeconds)
        {
            ActorId = actorId;
            LastSeenPos = lastSeenPos;
            Velocity = velocity;
            LastSeenTime = lastSeenTime;
            WindowSeconds = windowSeconds;
        }

        /// <summary>距最后一次看见过去了多久（秒）。</summary>
        public float Age(float now) => now - LastSeenTime;

        /// <summary>这条记忆是否还在有效期内。</summary>
        public bool IsLive(float now) => now - LastSeenTime <= WindowSeconds;

        /// <summary>按记忆里的水平速度外推 <paramref name="seconds"/> 秒后的位置（不超过记忆窗口）。</summary>
        public Vector3 Extrapolate(float seconds)
        {
            var t = Mathf.Clamp(seconds, 0f, WindowSeconds);
            return LastSeenPos + new Vector3(Velocity.x, 0f, Velocity.z) * t;
        }
    }


    /// <summary>
    /// 一只 bot 本 tick 的**只读感知快照**：视野、目标与短时记忆、听觉（枪声 / 脚步）。
    /// **本项目新增**（A = CS 1.6 本体不含机器人 AI）。
    ///
    /// <para>战斗侧与目标侧只读它，不再各自算一遍"看不看得见 / 听没听见" ——
    /// 一份可见性同时决定"能不能看见"与"能不能打中"，不会出现两个模块各判一次而结论相反。</para>
    /// </summary>
    public readonly struct CsBotPercept
    {
        /// <summary>快照时刻（游戏时钟）。</summary>
        public readonly float Time;

        // ---- 视野（按难度分档）----
        public readonly float FovDegrees;
        public readonly float VisionRange;
        public readonly float ReactionTime;
        public readonly float MemorySeconds;

        // ---- 目标与短时记忆 ----
        /// <summary>当前交战目标（0 = 没有）。记忆期内即使此刻不可见也保持它。</summary>
        public readonly long TargetId;

        /// <summary>本 tick 是否真的看得见目标（视线 + 视野角 + 距离都通过）。</summary>
        public readonly bool TargetVisible;

        /// <summary>目标此刻不可见、但仍在记忆期内（"疑似敌"）。</summary>
        public readonly bool TargetInMemory;

        /// <summary>可见时 = 本 tick 判定通畅、真能打中的那一点（眼位与胸部都通 ⇒ 胸部，否则眼位）；
        /// 仅记忆期内 = 最后所见位置。</summary>
        public readonly Vector3 TargetAimPoint;

        /// <summary>本 tick 对目标眼位的视线判定结果（<see cref="BotSightResult.BlockedByActorOnly"/> 按可见处理）。</summary>
        public readonly BotSightResult TargetSight;

        public readonly Vector3 LastSeenPos;
        public readonly Vector3 LastSeenVelocity;
        public readonly float LastSeenTime;

        /// <summary>距最后一次看见目标的秒数（从没看见过 = <c>float.MaxValue</c>）。</summary>
        public readonly float SinceSeen;

        public readonly float ClosestVisibleDist;
        public readonly int VisibleEnemies;
        public readonly int VisibleFriends;

        // ---- 听觉 ----
        /// <summary>本 tick 是否有一条已过反应时间、可据以行动的敌方声响。</summary>
        public readonly bool Heard;

        /// <summary>听见的是枪声（true）还是跑动声（false）。</summary>
        public readonly bool HeardGunshot;

        public readonly Vector3 HeardPos;
        public readonly float HeardTime;

        /// <summary>声源到自身眼的水平距离（米）。</summary>
        public readonly float HeardDistance;

        /// <summary>声源方位相对自身朝向的角度（度，右正左负）。</summary>
        public readonly float HeardBearingDeg;

        /// <summary>方位的不确定半角（度）：真实方位在 <c>HeardBearingDeg ± 它</c> 之内。</summary>
        public readonly float HeardBearingConeDeg;

        /// <summary>距响声时刻过去多久（秒）。</summary>
        public readonly float HeardAge;

        public CsBotPercept(
            float time,
            float fovDegrees, float visionRange, float reactionTime, float memorySeconds,
            long targetId, bool targetVisible, bool targetInMemory, Vector3 targetAimPoint,
            BotSightResult targetSight,
            Vector3 lastSeenPos, Vector3 lastSeenVelocity, float lastSeenTime, float sinceSeen,
            float closestVisibleDist, int visibleEnemies, int visibleFriends,
            bool heard, bool heardGunshot, Vector3 heardPos, float heardTime,
            float heardDistance, float heardBearingDeg, float heardBearingConeDeg, float heardAge)
        {
            Time = time;
            FovDegrees = fovDegrees;
            VisionRange = visionRange;
            ReactionTime = reactionTime;
            MemorySeconds = memorySeconds;
            TargetId = targetId;
            TargetVisible = targetVisible;
            TargetInMemory = targetInMemory;
            TargetAimPoint = targetAimPoint;
            TargetSight = targetSight;
            LastSeenPos = lastSeenPos;
            LastSeenVelocity = lastSeenVelocity;
            LastSeenTime = lastSeenTime;
            SinceSeen = sinceSeen;
            ClosestVisibleDist = closestVisibleDist;
            VisibleEnemies = visibleEnemies;
            VisibleFriends = visibleFriends;
            Heard = heard;
            HeardGunshot = heardGunshot;
            HeardPos = heardPos;
            HeardTime = heardTime;
            HeardDistance = heardDistance;
            HeardBearingDeg = heardBearingDeg;
            HeardBearingConeDeg = heardBearingConeDeg;
            HeardAge = heardAge;
        }
    }


    public sealed partial class CsBotBrain
    {
        // ==================================================================
        //  感知
        // ==================================================================

        /// <summary>本 tick 的感知快照（每 tick 由 <see cref="UpdatePerception"/> 重建；对外只读）。</summary>
        private CsBotPercept _percept;

        /// <summary>敌人短时记忆（id → 最后所见位置 / 时刻 / 速度）。</summary>
        private readonly Dictionary<long, CsBotEnemyMemory> _enemyMemory = new Dictionary<long, CsBotEnemyMemory>(8);

        /// <summary>过期条目的暂存表（遍历字典时不能删，先收集再删）。</summary>
        private readonly List<long> _memoryExpire = new List<long>(8);

        /// <summary>目标此刻不可见但仍在记忆期内。</summary>
        private bool _targetInMemory;

        /// <summary>最后一次看见目标时的水平速度（外推用）。</summary>
        private Vector3 _lastSeenVel;

        /// <summary>本 tick 判定通畅、真能打中的那一点（眼位 / 胸部）。</summary>
        private Vector3 _targetAimPoint;

        /// <summary>本 tick 对目标眼位的视线判定。</summary>
        private BotSightResult _targetSight = BotSightResult.OutOfRange;

        // ---- 听觉（本 tick 被采纳的那一条）----
        private bool _heardGunshot;
        private float _heardDistance = float.MaxValue;
        private float _heardBearingDeg;
        private float _nextHearLogAt;
        private float _nextMemoryLogAt;

        /// <summary>本 tick 的只读感知快照（战斗片 / 目标片读它）。</summary>
        public CsBotPercept Percept => _percept;

        /// <summary>本 tick 是否听见了可据以行动的敌方声响。</summary>
        public bool Heard => _heardFresh;

        /// <summary>
        /// 取某个敌人的**有效**短时记忆（已过期的不返回）。战斗侧外推"他刚才往哪跑"用它。
        /// </summary>
        public bool TryGetEnemyMemory(long actorId, float now, out CsBotEnemyMemory memory)
        {
            if (_enemyMemory.TryGetValue(actorId, out memory) && memory.IsLive(now)) return true;
            memory = default;
            return false;
        }

        /// <summary>仍在记忆期内的敌人条数（探针 / 日志用）。</summary>
        public int MemoryContactCount(float now)
        {
            var n = 0;
            foreach (var kv in _enemyMemory)
                if (kv.Value.IsLive(now)) n++;
            return n;
        }


        private void UpdatePerception(CsActor self, float now)
        {
            _visibleNow = false;
            _closestVisibleDist = float.MaxValue;
            _visibleEnemies = 0;
            _visibleFriends = 0;
            _targetInMemory = false;
            _targetAimPoint = Vector3.zero;
            _targetSight = BotSightResult.OutOfRange;

            var memorySeconds = CsBotConst.MemorySeconds(_difficulty);
            var fov = CsBotConst.FovDegrees(_difficulty);
            var eye = self.EyePosition;
            var forward = Forward(self.Yaw, 0f);
            var halfFov = fov * 0.5f;
            var actors = _match.Actors;

            CsActor best = null;
            var bestDist = float.MaxValue;
            var bestScore = float.MaxValue;
            var bestAim = Vector3.zero;

            for (var i = 0; i < actors.Count; i++)
            {
                var other = actors[i];
                if (other == null || !other.IsAlive || other.Id == self.Id) continue;
                if (other.Team == self.Team) continue;                       // 只打敌对阵营
                if (other.Team != CsTeam.T && other.Team != CsTeam.CT) continue;   // Spectator 不打

                var targetEye = other.EyePosition;
                var d = targetEye - eye;
                var dist = d.magnitude;
                if (dist > _profile.VisionRange) continue;

                d.y = 0f;
                if (d.sqrMagnitude > 0.0004f && Vector3.Angle(forward, d) > halfFov) continue;

                // 视线用 BotSight（CS 1.6 语义：其它角色不挡视线）而不是直接问契约 —— 理由见该类的注释。
                // 看到哪一点（眼位 / 胸部）由同一次判定给出：快照里的瞄点永远是本 tick 已验证通畅的点，
                // 于是"看得见"与"打得中"用的是同一份可见性。
                var chest = other.Position + Vector3.up * (other.Height * CsBotConst.ChestHeightRatio);
                var sight = BotSight.CheckBody(_match, eye, targetEye, chest, _profile.VisionRange,
                    out var aimPoint, out var sightBlocker, out _);
                if (sight == BotSightResult.BlockedByActorOnly)
                {
                    LogSightActorBlock(sightBlocker, now);
                }
                else if (sight != BotSightResult.Clear)
                {
                    continue;
                }

                _visibleEnemies++;
                RememberEnemy(other, now, memorySeconds);

                // 目标选择：**威胁优先**而不是纯粹的"最近优先"。原版用
                // FindMostDangerousThreat()（cs_bot_vision.cpp:1048）综合距离/是否在打我等因素；
                // 这里只用一个可观测的近似 —— 对方的朝向是否指着我（正对着我 = 他下一秒会开枪）。
                // 权重扣减 6m：足够让"正对着我的敌人"压过"近 6m 内但背对我的敌人"，又不会
                // 让远处的人抢走贴脸目标的优先级。
                var aimsAtMe = 0f;
                var toMe = eye - other.EyePosition;
                toMe.y = 0f;
                if (toMe.sqrMagnitude > 0.0004f &&
                    Vector3.Angle(Forward(other.Yaw, 0f), toMe) <= 20f)
                {
                    aimsAtMe = 6f;
                }

                if (dist - aimsAtMe < bestScore)
                {
                    bestScore = dist - aimsAtMe;
                    bestDist = dist;
                    best = other;
                    bestAim = aimPoint;
                    _lastSight = sight;
                    _lastSightBlocker = sightBlocker;
                    _targetSight = sight;
                }
            }


            // ---- 可见**队友**计数（无线电"被压制 / 说了有人听"要用；口径与上面完全同源）----
            for (var i = 0; i < actors.Count; i++)
            {
                var mate = actors[i];
                if (mate == null || !mate.IsAlive || mate.Id == self.Id) continue;
                if (mate.Team != self.Team) continue;
                if ((mate.Team != CsTeam.T && mate.Team != CsTeam.CT)) continue;

                var mateEye = mate.EyePosition;
                var md = mateEye - eye;
                var mdist = md.magnitude;
                if (mdist > _profile.VisionRange) continue;

                md.y = 0f;
                if (md.sqrMagnitude > 0.0004f && Vector3.Angle(forward, md) > halfFov) continue;

                var mateSight = BotSight.Check(_match, eye, mateEye, _profile.VisionRange, out _, out _);
                if (mateSight != BotSightResult.Clear) continue;

                _visibleFriends++;
            }

            if (best != null)
            {
                // 反应计时按**敌人身份**算：换成另一个敌人时，只有"这个敌人上一次被看见已超出记忆时长"
                // 才算首次发现。两个人交替成为最近可见目标时各自都有近期记录 ⇒ 不重新计时，
                // 否则 `reacted` 永远过不去、扳机永远扣不下去。
                if (best.Id != _targetId &&
                    (!_enemySeenAt.TryGetValue(best.Id, out var seenAt) ||
                     now - seenAt > memorySeconds))
                {
                    _firstSeenTime = now;
                    _wobbleRefreshAt = 0f;
                    _headRollAt = 0f;
                }

                _enemySeenAt[best.Id] = now;
                _targetId = best.Id;
                _lastSeenTime = now;
                _lastSeenPos = best.EyePosition;
                _lastSeenVel = best.Velocity;
                _visibleNow = true;
                _closestVisibleDist = bestDist;
                _targetAimPoint = bestAim;
            }
            else if (_targetId != 0 && now - _lastSeenTime > memorySeconds)
            {
                _targetId = 0;   // 记忆到期 → 忘掉
                _targetInMemory = false;
            }
            else if (_targetId != 0)
            {
                // 目标此刻不可见但仍在记忆期内：战斗侧按最后所见位置瞄准 / 移动，这里留痕（降频）。
                _targetInMemory = true;
                _targetAimPoint = _lastSeenPos;
                LogMemoryAim(self, now, memorySeconds);
            }

            PruneMemory(now);

            UpdateHearing(self, now, forward);

            _percept = new CsBotPercept(
                time: now,
                fovDegrees: fov, visionRange: _profile.VisionRange,
                reactionTime: _profile.ReactionTime, memorySeconds: memorySeconds,
                targetId: _targetId, targetVisible: _visibleNow, targetInMemory: _targetInMemory,
                targetAimPoint: _targetAimPoint, targetSight: _targetSight,
                lastSeenPos: _lastSeenPos, lastSeenVelocity: _lastSeenVel,
                lastSeenTime: _lastSeenTime,
                sinceSeen: _lastSeenTime > 0f ? now - _lastSeenTime : float.MaxValue,
                closestVisibleDist: _closestVisibleDist,
                visibleEnemies: _visibleEnemies, visibleFriends: _visibleFriends,
                heard: _heardFresh, heardGunshot: _heardGunshot,
                heardPos: _heardPos, heardTime: _heardTime,
                heardDistance: _heardDistance,
                heardBearingDeg: _heardBearingDeg,
                heardBearingConeDeg: CsBotConst.HearBearingConeDegrees,
                heardAge: _heardFresh ? now - _heardTime : float.MaxValue);
        }


        /// <summary>记一次"看见了这个敌人"（位置 / 时刻 / 速度），并刷新该档记忆窗口。</summary>
        private void RememberEnemy(CsActor other, float now, float memorySeconds)
        {
            _enemyMemory[other.Id] = new CsBotEnemyMemory(
                other.Id, other.EyePosition, other.Velocity, now, memorySeconds);
        }


        /// <summary>删掉超过记忆时长的条目（每 tick 一次）。</summary>
        private void PruneMemory(float now)
        {
            if (_enemyMemory.Count == 0) return;

            _memoryExpire.Clear();
            foreach (var kv in _enemyMemory)
                if (!kv.Value.IsLive(now)) _memoryExpire.Add(kv.Key);

            for (var i = 0; i < _memoryExpire.Count; i++) _enemyMemory.Remove(_memoryExpire[i]);
            _memoryExpire.Clear();
        }


        /// <summary>
        /// 听觉：枪声 / 脚步各用按难度分档的半径，且距响声时刻 ≥ 反应时间才算听见
        /// （原版 <c>cs_bot.h:1141-1145</c> 的 <c>IsNoiseHeard</c> 同口径）。
        /// 采纳后把**来源方向**写进感知（角度 + 不确定半角，不当作精确坐标）。
        /// </summary>
        private void UpdateHearing(CsActor self, float now, Vector3 forward)
        {
            _heardFresh = false;
            _heardDistance = float.MaxValue;
            _heardBearingDeg = 0f;

            if (_sense == null) return;

            var gunRadius = CsBotConst.GunshotHearRadius(_difficulty);
            var stepRadius = CsBotConst.StepHearRadius(_difficulty);
            if (!_sense.TryHearEnemy(self.Team, self.Position, gunRadius, stepRadius,
                    out var heardPos, out var heardTime, out var gunshot, out var distance))
                return;

            // 刚响的那一声不会立刻让人转身：等过反应时间再采纳。
            if (heardTime < _heardTime || now - heardTime < _profile.ReactionTime) return;

            _heardPos = heardPos;
            _heardTime = heardTime;
            _heardFresh = true;
            _heardGunshot = gunshot;
            _heardDistance = distance;
            _heardBearingDeg = Bearing(self, forward, heardPos);

            if (now < _nextHearLogAt) return;
            _nextHearLogAt = now + CsBotConst.HearLogInterval;

            var radius = gunshot ? gunRadius : stepRadius;
            var cone = CsBotConst.HearBearingConeDegrees;
            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）heard={(gunshot ? "gun" : "step")} " +
                $"半径={radius:F0}m 距离={distance:F1}m 方位={_heardBearingDeg:+0;-0;0}°（±{cone:F0}°，" +
                $"右正左负）声源=({heardPos.x:F1},{heardPos.y:F1},{heardPos.z:F1}) " +
                $"距响声 {now - heardTime:F2}s ≥ 反应 {_profile.ReactionTime:F2}s");
        }


        /// <summary>目标方位相对自身朝向的角度（度，右正左负）。</summary>
        private static float Bearing(CsActor self, Vector3 forward, Vector3 target)
        {
            var to = target - self.Position;
            to.y = 0f;
            if (to.sqrMagnitude < 0.0004f) return 0f;
            return Vector3.SignedAngle(forward, to, Vector3.up);
        }


        /// <summary>记忆生效的留证：目标已不可见，但仍在记忆期内 → 按记忆位置瞄准 / 移动（每 bot 降频）。</summary>
        private void LogMemoryAim(CsActor self, float now, float memorySeconds)
        {
            if (now < _nextMemoryLogAt) return;
            _nextMemoryLogAt = now + CsBotConst.MemoryLogInterval;

            var age = now - _lastSeenTime;
            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）目标 actor {_targetId} 不可见 {age:F2}s / 记忆 {memorySeconds:F2}s " +
                $"→ 按记忆位置 ({_lastSeenPos.x:F1},{_lastSeenPos.y:F1},{_lastSeenPos.z:F1}) 瞄准、" +
                $"记忆速度 ({_lastSeenVel.x:F1},{_lastSeenVel.z:F1})m/s " +
                $"memory=lost {age:F2}/{memorySeconds:F2}s");
        }


        /// <summary>视线只被"角色受体"挡住、按 CS 语义放行的留证（降频：每 bot 每 10s 一条）。</summary>
        private void LogSightActorBlock(string blocker, float now)
        {
            _sightActorBlockCount++;
            if (now < _nextSightBlockLogAt) return;
            _nextSightBlockLogAt = now + CsBotConst.SightActorBlockLogInterval;

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）视线被角色受体挡住（挡={blocker ?? "未知"}）→ 按 CS 1.6 语义" +
                $"（其它玩家不挡视线）判定为**可见**（第 {_sightActorBlockCount} 次；本日志每 " +
                $"{CsBotConst.SightActorBlockLogInterval:F0}s 最多一条）");
        }



    }
}
