using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 任务层 + 指挥（队长）+ 士气：把"这一段时间干什么"从行为树里抽出来，每
    /// <see cref="CsBotTaskConst.EvalInterval"/> 秒推导一次（<see cref="CsBotTask"/> / <see cref="CsBotDisposition"/> /
    /// <see cref="CsBotMorale"/> 的原版枚举见 <c>原版资源/cs16src/regamedll/dlls/bot/cs_bot.h:417-476</c>）。
    ///
    /// <para><b>职责边界</b>：本层只产出**任务与态度**，并驱动三件现有决策没有覆盖的动作
    /// （<see cref="CsBotTask.EscapeFromBomb"/> 往外跑、<see cref="CsBotTask.HoldPosition"/> 守爆点、
    /// <see cref="CsBotTask.Follow"/> 跟队长）。下包 / 拆包 / 守包点 / 交战仍走各自原有的叶子
    /// （<c>TryBombObjective</c> / <c>Engage</c> / <c>Advance</c>），本层只负责"该不该进交战"的门。</para>
    ///
    /// <para><b>士气观测</b>：全部由本 bot 自己能从 <see cref="ICsMatch"/> 读到的量推出
    /// （自己的 <c>Kills</c>、队友存活集合、回合比分），不订阅事件、不改 <c>BotModule</c>。</para>
    /// </summary>
    public sealed partial class CsBotBrain
    {
        // ---- 任务 ----
        private CsBotTask _task = CsBotTask.None;
        private CsBotDisposition _disposition = CsBotDisposition.EngageAndInvestigate;
        private string _taskWhy = "回合未开始";
        private float _taskSince;
        private float _nextTaskEvalAt;
        private float _tickNow;

        // ---- 队长 ----
        private long _taskLeaderId;
        private string _taskLeaderName;

        // ---- 撤离 / 守爆点 ----
        private float _bombPlantedSeenAt = -1f;
        private Vector3 _bombPlantedPos;
        private float _escapeStartedAt;
        private Vector3 _escapeGoal;
        private bool _escapeGoalValid;
        private float _nextEscapeLogAt;

        // ---- 跟随时临时换到队长的路线（离开 FOLLOW 要还原自己的计划路线）----
        private bool _followRouteApplied;
        private string _followRouteMarker;

        // ---- 士气 ----
        private CsBotMorale _morale = CsBotMorale.Neutral;
        private int _observedKills;
        private bool _observedKillsSeeded;
        private readonly HashSet<long> _matesAlive = new HashSet<long>();
        private readonly HashSet<long> _matesScratch = new HashSet<long>();
        private int _moraleRound;
        private int _moraleScoreT;
        private int _moraleScoreCT;
        private bool _moraleScoreSeeded;
        private bool _roundResultApplied;
        private int _observedHealth;
        private float _hurtUntil;

        // ---- 开局窗口（Live 起算，不含冻结期）----
        private int _liveRound = int.MinValue;
        private float _liveStartAt;

        // ---- 周期日志 ----
        private float _nextDefuseWitnessLogAt;

        /// <summary>当前任务（探针 / 日志只读）。</summary>
        public CsBotTask Task => _task;

        /// <summary>当前任务的推导理由（探针只读）。</summary>
        public string TaskWhy => _taskWhy;

        /// <summary>当前任务的开始时刻（游戏时钟；探针只读）。</summary>
        public float TaskSince => _taskSince;

        /// <summary>当前对敌人的态度（探针 / 日志只读）。</summary>
        public CsBotDisposition Disposition => _disposition;

        /// <summary>当前士气（探针 / 日志只读）。</summary>
        public CsBotMorale Morale => _morale;

        /// <summary>当前队长（0 = 本队没有存活成员；探针 / 日志只读）。</summary>
        public long LeaderId => _taskLeaderId;


        // ==================================================================
        //  每 tick 入口
        // ==================================================================
        /// <summary>
        /// 任务层 tick：回合复位 → 路线登记 → 炸弹/士气观测 → 每 0.5s 重推任务。
        /// 由行为树的 <c>LeafTaskTick</c> 在感知 / 卡住恢复之后、所有战术分支之前调用一次。
        /// </summary>
        private void TaskTick(CsActor self, float now)
        {
            _tickNow = now;

            if (self == null) return;
            if (_match.RoundNumber != _moraleRound) BeginRound(self, now);

            TrackLiveStart(now);
            CsBotRouteBoard.Publish(_match, _match.RoundNumber, self.Id, _planRoute);

            ObserveBombPlanted(now);
            ObserveMorale(self, now);

            if (now < _nextTaskEvalAt) return;
            _nextTaskEvalAt = now + CsBotTaskConst.EvalInterval;
            EvaluateTask(self, now);
        }


        /// <summary>新回合复位：任务 / 队长 / 撤离窗口 / 士气观测基准（士气值本身跨回合保留 —— 原版也是累积的）。</summary>
        private void BeginRound(CsActor self, float now)
        {
            _moraleRound = _match.RoundNumber;
            _task = CsBotTask.None;
            _taskWhy = "回合开始";
            _taskSince = now;
            _nextTaskEvalAt = 0f;
            _taskLeaderId = 0L;
            _taskLeaderName = null;

            _bombPlantedSeenAt = -1f;
            _bombPlantedPos = Vector3.zero;
            _escapeStartedAt = 0f;
            _escapeGoal = Vector3.zero;
            _escapeGoalValid = false;
            _nextEscapeLogAt = 0f;

            _followRouteApplied = false;
            _followRouteMarker = null;

            _matesAlive.Clear();
            _observedKills = self != null ? self.Kills : 0;
            _observedKillsSeeded = true;
            _observedHealth = self != null ? self.Health : 0;
            _hurtUntil = 0f;
            _moraleScoreT = _match.ScoreT;
            _moraleScoreCT = _match.ScoreCT;
            _moraleScoreSeeded = true;
            _roundResultApplied = false;
            _nextDefuseWitnessLogAt = 0f;
        }


        /// <summary>记录本回合第一次看见"包已安放"的时刻（撤离窗口与守爆点都按它计龄）。</summary>
        private void ObserveBombPlanted(float now)
        {
            if (!_match.BombPlanted || _bombPlantedSeenAt >= 0f) return;

            _bombPlantedSeenAt = now;
            _bombPlantedPos = _match.BombPosition;

            Game.Logger.Info(Tag,
                $"[BOT-TASK] {_name} 观察到 C4 已安放：位置 ({_bombPlantedPos.x:F1},{_bombPlantedPos.z:F1}) " +
                $"距本人 {HorizontalDistance(_bombPlantedPos, _tickSelf != null ? _tickSelf.Position : _bombPlantedPos):F1}m " +
                $"→ T 进入撤离窗口 {CsBotTaskConst.EscapeSeconds:F0}s / {CsBotTaskConst.EscapeDistance:F0}m");
        }


        /// <summary>Live 起点（跟随窗口按它计龄；冻结期不算"开局 8s"）。</summary>
        private void TrackLiveStart(float now)
        {
            if (_match.Phase != CsRoundPhase.Live) return;
            if (_liveRound == _match.RoundNumber) return;

            _liveRound = _match.RoundNumber;
            _liveStartAt = now;
        }


        // ==================================================================
        //  士气
        // ==================================================================
        /// <summary>
        /// 士气观测（<see cref="CsBotMorale"/> 七级、夹在 -3..3）：
        /// <list type="bullet">
        /// <item>击杀 +1（原版士气只有"回合胜负"两处升降，见 <c>cs_bot_event.cpp:36-58</c>；
        /// 击杀 / 队友阵亡两级由本工程补齐）；</item>
        /// <item>队友阵亡 -1；</item>
        /// <item>本回合赢 +1 / 输 -1（口径同原版 <c>EVENT_TERRORISTS_WIN</c> / <c>EVENT_CTS_WIN</c>）。</item>
        /// </list>
        /// 血量下降记一个"刚被打过"的窗口，供 <see cref="CsBotDisposition.SelfDefense"/> 放行还手。
        /// </summary>
        private void ObserveMorale(CsActor self, float now)
        {
            if (self.Health < _observedHealth) _hurtUntil = now + CsBotTaskConst.HurtMemorySeconds;
            _observedHealth = self.Health;

            if (!_observedKillsSeeded)
            {
                _observedKills = self.Kills;
                _observedKillsSeeded = true;
            }
            else if (self.Kills > _observedKills)
            {
                var gained = self.Kills - _observedKills;
                _observedKills = self.Kills;
                ChangeMorale(gained, $"击杀 +{gained}（本局累计 {self.Kills}）", now);
            }
            else if (self.Kills < _observedKills)
            {
                // 重开局 / 换边会把累计击杀清零：只重设基准，不产生士气变化。
                _observedKills = self.Kills;
            }

            _matesScratch.Clear();
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != self.Team || a.Id == self.Id) continue;
                _matesScratch.Add(a.Id);
            }

            foreach (var mateId in _matesAlive)
            {
                if (_matesScratch.Contains(mateId)) continue;
                var mate = _match.Find(mateId);
                ChangeMorale(-1, $"队友 {(mate != null ? mate.Name : mateId.ToString())} 阵亡", now);
            }

            _matesAlive.Clear();
            foreach (var mateId in _matesScratch) _matesAlive.Add(mateId);

            ObserveRoundResult(self, now);
        }


        /// <summary>
        /// 回合结果：<see cref="CsRoundPhase.RoundEnd"/> 里用比分增量判胜负（比分在本模块 tick 时已结算），
        /// 只结算一次。比分没变化 ⇒ 本 tick 不下结论，等下一次（重开局 / 换边等非预期时序仍会到这里，
        /// 但不会产生错误的士气变化）。
        /// </summary>
        private void ObserveRoundResult(CsActor self, float now)
        {
            if (_roundResultApplied || _match.Phase != CsRoundPhase.RoundEnd) return;
            if (!_moraleScoreSeeded) return;

            var tUp = _match.ScoreT > _moraleScoreT;
            var ctUp = _match.ScoreCT > _moraleScoreCT;
            if (!tUp && !ctUp) return;

            _roundResultApplied = true;
            var ourTeamScored = self.Team == CsTeam.T ? tUp : ctUp;
            _moraleScoreT = _match.ScoreT;
            _moraleScoreCT = _match.ScoreCT;

            ChangeMorale(ourTeamScored ? 1 : -1,
                ourTeamScored
                    ? $"本回合获胜（比分 {_match.ScoreT}:{_match.ScoreCT}）"
                    : $"本回合失利（比分 {_match.ScoreT}:{_match.ScoreCT}）",
                now);
        }


        /// <summary>士气加减一步（夹在 <c>TERRIBLE..EXCELLENT</c>），变化才打日志。</summary>
        private void ChangeMorale(int delta, string why, float now)
        {
            var from = _morale;
            var value = Mathf.Clamp((int)_morale + delta,
                (int)CsBotMorale.Terrible, (int)CsBotMorale.Excellent);
            _morale = (CsBotMorale)value;
            if (_morale == from) return;

            Game.Logger.Info(Tag,
                $"[BOT-TASK] {_name} 士气 {(int)from}({CsBotTaskText.Morale(from)})→{(int)_morale}" +
                $"({CsBotTaskText.Morale(_morale)})：{why}｜任务={CsBotTaskText.Task(_task)}");
        }


        // ==================================================================
        //  任务推导
        // ==================================================================
        /// <summary>
        /// 推导本 tick 的任务。顺序 = 优先级（先命中先得）：
        /// 1) T：已下包 → 撤离 / 守爆点；持包且已在包点 → 下包；
        /// 2) CT：已下包 → 拆包 / 掩护拆包者 / 守爆点；
        /// 3) 敌全灭或人数优势 ≥3 → 搜索歼灭；
        /// 4) 15s 内有敌人消失 → 去最后已知位置；
        /// 5) Live 开局 8s 内、非队长、同队 ≥3 人 → 跟随队长；
        /// 6) 其余 → 走本轮计划槽（<see cref="CsBotTask.None"/>）。
        /// </summary>
        private void EvaluateTask(CsActor self, float now)
        {
            if (!self.IsAlive)
            {
                SetTask(self, CsBotTask.None, "已阵亡", now, 0L, null);
                return;
            }

            if (self.Team != CsTeam.T && self.Team != CsTeam.CT)
            {
                SetTask(self, CsBotTask.None, $"阵营 {self.Team} 不做任务", now, 0L, null);
                return;
            }

            var enemyTeam = self.Team == CsTeam.T ? CsTeam.CT : CsTeam.T;
            var mates = _match.AliveCount(self.Team);
            var enemies = _match.AliveCount(enemyTeam);

            var leaderId = CsBotLeader.Select(_match, self.Team, out var leaderName, out var leaderWhy);

            CsBotTask next;
            string why;

            if (self.Team == CsTeam.T)
            {
                EvaluateT(self, now, mates, enemies, leaderId, out next, out why);
            }
            else
            {
                EvaluateCT(self, now, mates, enemies, leaderId, out next, out why);
            }

            SetTask(self, next, why, now, leaderId, leaderName);

            if (_task == CsBotTask.DefuseBomb) LogDefuseWitness(self, now);
        }


        private void EvaluateT(CsActor self, float now, int mates, int enemies, long leaderId,
            out CsBotTask next, out string why)
        {
            if (_match.BombPlanted)
            {
                var bomb = _match.BombPosition;
                var distance = HorizontalDistance(bomb, self.Position);
                var age = _bombPlantedSeenAt >= 0f ? now - _bombPlantedSeenAt : 0f;

                if (distance < CsBotTaskConst.EscapeDistance &&
                    age <= CsBotTaskConst.EscapeSeconds + CsBotTaskConst.EscapeGraceSeconds)
                {
                    next = CsBotTask.EscapeFromBomb;
                    why = $"已下包 {age:F1}s，距爆点 {distance:F1}m < {CsBotTaskConst.EscapeDistance:F0}m → 往外撤";
                    return;
                }

                next = CsBotTask.HoldPosition;
                why = $"已下包 {age:F1}s，距爆点 {distance:F1}m → 守住爆点周边";
                return;
            }

            if (self.HasBomb)
            {
                NearestBombsitePoint(self.Position, out var zoneDist, out var zoneName);

                //   判断用**本轮计划的下包点**（TryGetPlantAim，与 TryPlantOrPickup 同一个算式），
                //   不是"离自己最近的包点标记"：T 的路线指向 A 点时，人在中途离 B 的标记更近，
                //   用最近标记会在 6.9m ↔ 10.4m 之间来回切任务（B 变远 = 以为离开了 A）。
                var hasAim = TryGetPlantAim(self, out var aim);
                var aimDist = hasAim ? HorizontalDistance(self.Position, aim) : zoneDist;

                // 滞回：进入 8m、离开 12m，避免在站点边缘来回切。
                var inSite = zoneDist <= CsBotConst.SiteRadius && aimDist <= CsBotTaskConst.PlantTaskEnterRange;
                var stay = _task == CsBotTask.PlantBomb && aimDist <= CsBotTaskConst.PlantTaskStayRange;
                if (inSite || stay)
                {
                    next = CsBotTask.PlantBomb;
                    why = $"持 C4 已在包点 {zoneName}：距本轮下包点 {aimDist:F1}m" +
                          $"（进入 ≤ {CsBotTaskConst.PlantTaskEnterRange:F0}m / 保持 ≤ {CsBotTaskConst.PlantTaskStayRange:F0}m）";
                    return;
                }

                next = CsBotTask.None;
                why = $"持 C4 去包点（距本轮下包点 {aimDist:F1}m，最近包点标记 {zoneDist:F1}m）";
                return;
            }

            if (TryPickFallbackTask(self, now, mates, enemies, leaderId, out next, out why)) return;
        }


        private void EvaluateCT(CsActor self, float now, int mates, int enemies, long leaderId,
            out CsBotTask next, out string why)
        {
            if (_match.BombPlanted)
            {
                var bomb = _match.BombPosition;
                var distance = HorizontalDistance(bomb, self.Position);

                if (IsElectedDefuser(self, now))
                {
                    next = CsBotTask.DefuseBomb;
                    why = $"离 C4 最近的 CT（距 C4 {distance:F1}m，引信 {_match.BombTimeLeft:F1}s）";
                    return;
                }

                if (GuardDefuserNeeded(self, out var defuserId, out var defuserName,
                        out var enemyDistance, out var myDistance))
                {
                    next = CsBotTask.GuardBombDefuser;
                    why = $"队友 {defuserName} 正在拆包且 {enemyDistance:F1}m 内有敌 → " +
                          $"本人是离他最近的另一名 CT（{myDistance:F1}m）";
                    return;
                }

                next = CsBotTask.GuardTickingBomb;
                why = $"炸弹已安放，守爆点（距 C4 {distance:F1}m，引信 {_match.BombTimeLeft:F1}s）";
                return;
            }

            if (TryPickFallbackTask(self, now, mates, enemies, leaderId, out next, out why)) return;
        }


        /// <summary>
        /// 两个阵营共用的低优先级任务：搜索歼灭 → 去最后已知位置 → 跟随队长 → 计划槽。
        /// </summary>
        private bool TryPickFallbackTask(CsActor self, float now, int mates, int enemies, long leaderId,
            out CsBotTask next, out string why)
        {
            if (enemies == 0)
            {
                next = CsBotTask.SeekAndDestroy;
                why = "敌方已全部阵亡 → 搜索歼灭";
                return true;
            }

            if (mates > enemies + 2)
            {
                next = CsBotTask.SeekAndDestroy;
                why = $"残局人数优势 己方 {mates} vs 敌方 {enemies}（差 > 2）→ 搜索歼灭";
                return true;
            }

            if (LastKnownEnemyFresh(self, now, out var age, out var distance))
            {
                next = CsBotTask.MoveToLastKnownEnemy;
                why = $"敌人在 {age:F1}s 前消失于 {distance:F1}m 外 → 去最后已知位置";
                return true;
            }

            if (now - _liveStartAt <= CsBotTaskConst.FollowWindowSeconds &&
                mates >= CsBotTaskConst.FollowMinTeammates &&
                leaderId != 0L && leaderId != self.Id)
            {
                next = CsBotTask.Follow;
                why = $"开局 {(now - _liveStartAt):F1}s ≤ {CsBotTaskConst.FollowWindowSeconds:F0}s、" +
                      $"同队 {mates} 人、本人不是队长 → 跟随队长";
                return true;
            }

            next = CsBotTask.None;
            why = "按本轮计划槽推进";
            return false;
        }


        /// <summary>15s 内有没有敌人从视野里消失（位置 / 计龄都取自感知层的最后所见）。</summary>
        private bool LastKnownEnemyFresh(CsActor self, float now, out float age, out float distance)
        {
            age = float.MaxValue;
            distance = 0f;

            // 眼前还有敌人时由交战分支负责，这一条只表示"人已经离开视野、去最后见过的地方找"。
            if (_visibleNow) return false;
            if (_lastSeenTime <= 0f || _lastSeenPos.sqrMagnitude < 0.0001f) return false;
            age = now - _lastSeenTime;
            if (age > CsBotTaskConst.LastKnownEnemySeconds) return false;

            distance = HorizontalDistance(_lastSeenPos, self.Position);
            return true;
        }


        /// <summary>
        /// 是否有队友正在拆包、且该队友 <see cref="CsBotTaskConst.GuardDefuserEnemyRadius"/> 米内有敌人；
        /// 并且本人就是"离他最近的那另一名 CT"。
        /// </summary>
        private bool GuardDefuserNeeded(CsActor self, out long defuserId, out string defuserName,
            out float enemyDistance, out float myDistance)
        {
            defuserId = 0L;
            defuserName = null;
            enemyDistance = float.MaxValue;
            myDistance = float.MaxValue;

            var actors = _match.Actors;
            CsActor defuser = null;
            var bestBombDist = float.MaxValue;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != self.Team || a.Id == self.Id) continue;
                if (a.UseProgress < 0f) continue;

                var d = HorizontalDistance(a.Position, _match.BombPosition);
                if (d < bestBombDist || (Mathf.Abs(d - bestBombDist) <= 0.05f && defuser != null && a.Id < defuser.Id))
                {
                    bestBombDist = d;
                    defuser = a;
                }
            }

            if (defuser == null) return false;

            for (var i = 0; i < actors.Count; i++)
            {
                var e = actors[i];
                if (e == null || !e.IsAlive || e.Team == self.Team) continue;
                var d = HorizontalDistance(e.Position, defuser.Position);
                if (d < enemyDistance) enemyDistance = d;
            }

            if (enemyDistance > CsBotTaskConst.GuardDefuserEnemyRadius) return false;

            long nearestId = 0L;
            var nearestDist = float.MaxValue;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != self.Team) continue;
                if (a.Id == defuser.Id) continue;

                var d = HorizontalDistance(a.Position, defuser.Position);
                if (d < nearestDist - 0.05f || (Mathf.Abs(d - nearestDist) <= 0.05f && (nearestId == 0L || a.Id < nearestId)))
                {
                    nearestDist = d;
                    nearestId = a.Id;
                }
            }

            myDistance = HorizontalDistance(self.Position, defuser.Position);
            defuserId = defuser.Id;
            defuserName = defuser.Name;
            return nearestId == self.Id;
        }


        /// <summary>拆包中每秒一条"最近队友距拆包者多远"——守拆包者这件事的可核对载体。</summary>
        private void LogDefuseWitness(CsActor self, float now)
        {
            if (now < _nextDefuseWitnessLogAt) return;
            _nextDefuseWitnessLogAt = now + CsBotTaskConst.PeriodicLogSeconds;

            var actors = _match.Actors;
            var bestName = (string)null;
            var bestDist = float.MaxValue;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != self.Team || a.Id == self.Id) continue;

                var d = HorizontalDistance(a.Position, self.Position);
                if (d >= bestDist) continue;
                bestDist = d;
                bestName = a.Name;
            }

            if (bestName == null)
            {
                Game.Logger.Warn(Tag, $"[BOT-TASK] {_name} 正在拆包，但本队没有其他存活成员可掩护（距 C4 " +
                                      $"{HorizontalDistance(self.Position, _match.BombPosition):F1}m）");
                return;
            }

            var guardRange = CsBotTaskConst.GuardDefuserEnemyRadius * 2f;
            Game.Logger.Info(Tag,
                $"[BOT-TASK] {_name} 拆包中：最近队友 {bestName} 距拆包者 {bestDist:F1}m" +
                $"（{(bestDist <= guardRange ? "在掩护位" : "不在掩护位")}，掩护判据 ≤ {guardRange:F0}m，" +
                $"引信 {_match.BombTimeLeft:F1}s）");
        }


        /// <summary>
        /// 写任务并（变了才）打一条切换日志；同时按新任务换 disposition、处理跟随路线的进出。
        /// </summary>
        private void SetTask(CsActor self, CsBotTask next, string why, float now, long leaderId, string leaderName)
        {
            _taskLeaderId = leaderId;
            _taskLeaderName = leaderName;

            var nextDisposition = DispositionFor(next);
            if (next == _task)
            {
                // 任务没变：态度可能因为士气变化而变，但不该打一条"无→无"的切换日志。
                _taskWhy = why;
                _disposition = nextDisposition;
                return;
            }

            var from = _task;
            _task = next;
            _taskWhy = why;
            _taskSince = now;
            _disposition = nextDisposition;

            if (next == CsBotTask.EscapeFromBomb) BeginEscape(self, now);
            if (next == CsBotTask.Follow) ApplyFollowRoute(self, leaderId);
            else if (from == CsBotTask.Follow) RestoreOwnRoute(self);

            Game.Logger.Info(Tag,
                $"[BOT-TASK] {_name}（{self.Team}）任务 {CsBotTaskText.Task(from)}→{CsBotTaskText.Task(next)}：{why}" +
                $"｜倾向={CsBotTaskText.Disposition(_disposition)} 士气={(int)_morale}({CsBotTaskText.Morale(_morale)})" +
                $" 队长={_taskLeaderName ?? "无"}");
        }


        /// <summary>任务 → 对敌人的态度。</summary>
        private CsBotDisposition DispositionFor(CsBotTask task)
        {
            switch (task)
            {
                case CsBotTask.PlantBomb:
                case CsBotTask.DefuseBomb:
                case CsBotTask.EscapeFromBomb:
                    return CsBotDisposition.IgnoreEnemies;

                case CsBotTask.GuardTickingBomb:
                case CsBotTask.GuardBombDefuser:
                case CsBotTask.HoldPosition:
                    return CsBotDisposition.OpportunityFire;

                case CsBotTask.SeekAndDestroy:
                case CsBotTask.MoveToLastKnownEnemy:
                    return CsBotDisposition.EngageAndInvestigate;

                case CsBotTask.Follow:
                    return (int)_morale <= (int)CsBotMorale.Negative
                        ? CsBotDisposition.SelfDefense
                        : CsBotDisposition.EngageAndInvestigate;

                default:
                    return (int)_morale <= (int)CsBotMorale.Bad
                        ? CsBotDisposition.SelfDefense
                        : CsBotDisposition.EngageAndInvestigate;
            }
        }


        // ==================================================================
        //  行为树门（交战前的 disposition 检查）
        // ==================================================================
        /// <summary>
        /// 本 tick 是否允许进入交战分支：
        /// <list type="bullet">
        /// <item><see cref="CsBotDisposition.IgnoreEnemies"/>：一律不进（下包 / 拆包 / 逃爆）；</item>
        /// <item><see cref="CsBotDisposition.SelfDefense"/>：只在敌人贴脸（≤
        /// <see cref="CsBotTaskConst.SelfDefenseEngageRange"/>）或刚被打过（<c>_hurtUntil</c>）时才还手；</item>
        /// <item>士气 ≤ <see cref="CsBotMorale.Bad"/>：不主动追
        /// <see cref="CsBotTaskConst.LowMoraleChaseRange"/> 外的远敌。</item>
        /// </list>
        /// </summary>
        private bool EngageAllowed()
        {
            if (_disposition == CsBotDisposition.IgnoreEnemies) return false;

            var visible = _visibleNow ? _closestVisibleDist : float.MaxValue;

            if (_disposition == CsBotDisposition.SelfDefense)
            {
                return visible <= CsBotTaskConst.SelfDefenseEngageRange || _tickNow < _hurtUntil;
            }

            if ((int)_morale <= (int)CsBotMorale.Bad && visible > CsBotTaskConst.LowMoraleChaseRange)
            {
                return false;
            }

            return true;
        }


        // ==================================================================
        //  撤离 / 守爆点 / 跟随
        // ==================================================================
        /// <summary>进入撤离：选一个"背对 C4、12m 外、站得住"的落点（当前的朝向用来兜底退化情形）。</summary>
        private void BeginEscape(CsActor self, float now)
        {
            _escapeStartedAt = now;
            _nextEscapeLogAt = now;

            var fallback = -Forward(self.Yaw, 0f);
            _escapeGoalValid = PickFleePoint(self.Position, _match.BombPosition, fallback, out _escapeGoal);

            Game.Logger.Info(Tag,
                $"[BOT-TASK] {_name} 撤离起点：距包点 {HorizontalDistance(self.Position, _match.BombPosition):F1}m，" +
                $"落点 ({_escapeGoal.x:F1},{_escapeGoal.z:F1}){( _escapeGoalValid ? "" : "（无站得住的落点 → 按方向直走）")}");
        }


        /// <summary>背对 <paramref name="bomb"/> 的方向上取 <see cref="CsBotTaskConst.EscapeRunDistance"/> 米的落点。</summary>
        private bool PickFleePoint(Vector3 from, Vector3 bomb, Vector3 fallbackDir, out Vector3 point)
        {
            var away = from - bomb;
            away.y = 0f;
            if (away.sqrMagnitude < 0.25f) away = fallbackDir;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;
            away.Normalize();

            var offsets = new[] { 0f, 30f, -30f, 60f, -60f, 90f, -90f };
            var fallback = from + away * CsBotTaskConst.EscapeRunDistance;
            var hasFallback = false;

            for (var i = 0; i < offsets.Length; i++)
            {
                var dir = Quaternion.AngleAxis(offsets[i], Vector3.up) * away;
                var candidate = from + dir * CsBotTaskConst.EscapeRunDistance;
                if (_map != null && _map.IsLoaded && !_map.CanStand(candidate)) continue;

                // 站得住 ≠ 走得到：先找"位图连通"的那一个，免得朝墙后的点绕远路。
                if (_nav.CanReach(from, candidate))
                {
                    point = candidate;
                    return true;
                }

                if (hasFallback) continue;
                fallback = candidate;
                hasFallback = true;
            }

            point = fallback;
            return false;
        }


        /// <summary>撤离：朝落点走（A* 被挡时退回"背对 C4"的直线方向），每秒打一条当前距离。</summary>
        private void EscapeStep(CsActor self, ref CsBotIntent intent, float now)
        {
            // 落点只选一次：反复重选会让每帧的落点与日志一起跳，且"朝哪跑"应是这一段撤离的常量。
            if (_escapeStartedAt <= 0f) BeginEscape(self, now);

            var move = _nav.ComputeMove(self.Position, _escapeGoal, 0.5f, now,
                viaRoute: false, hullHeight: self.Height, forcePath: true);
            if (move.sqrMagnitude < 0.0004f) move = HorizontalDir(self.Position, _escapeGoal);

            SetState(CsBotState.Patrol, now);
            intent.State = CsBotState.Patrol;
            intent.Move = move;
            intent.AimPoint = move.sqrMagnitude > 0.0004f
                ? self.EyePosition + move * CsBotConst.TravelAimDistance
                : LookAhead(self);

            if (now >= _nextEscapeLogAt)
            {
                _nextEscapeLogAt = now + CsBotTaskConst.PeriodicLogSeconds;
                var distance = HorizontalDistance(self.Position, _match.BombPosition);
                Game.Logger.Info(Tag,
                    $"[BOT-TASK] {_name} 撤离中：距包点 {distance:F1}m" +
                    $"（下包后 {(now - _bombPlantedSeenAt):F1}s，目标 ≥ {CsBotTaskConst.EscapeDistance:F0}m，" +
                    $"撤离起点距今 {(now - _escapeStartedAt):F1}s）");
            }
        }


        /// <summary>
        /// 守爆点：离 C4 超过 <see cref="CsBotTaskConst.HoldRingRadius"/> 就靠回
        /// <see cref="CsBotTaskConst.HoldArriveRadius"/> 的圈上，进圈后站住盯住 C4。
        /// </summary>
        private void HoldBombStep(CsActor self, ref CsBotIntent intent, float now)
        {
            var bomb = _match.BombPosition;
            var distance = HorizontalDistance(bomb, self.Position);

            if (_match.BombPlanted && distance > CsBotTaskConst.HoldRingRadius)
            {
                SetState(CsBotState.Patrol, now);
                intent.State = CsBotState.Patrol;
                intent.Move = _nav.ComputeMove(self.Position, bomb, CsBotTaskConst.HoldArriveRadius, now,
                    viaRoute: false, hullHeight: self.Height, forcePath: true);
                intent.AimPoint = LookPoint(self, WithEyeHeight(bomb));
                return;
            }

            SetState(CsBotState.Camp, now);
            intent.State = CsBotState.Camp;
            intent.Move = Vector3.zero;
            intent.AimPoint = _match.BombPlanted ? WithEyeHeight(bomb) : LookAhead(self);
        }


        /// <summary>跟随队长：走到 <see cref="CsBotTaskConst.FollowDistance"/> 米内停住，朝队长看。</summary>
        private bool FollowStep(CsActor self, ref CsBotIntent intent, float now)
        {
            var leader = _match.Find(_taskLeaderId);
            if (leader == null || !leader.IsAlive) return false;

            var distance = HorizontalDistance(self.Position, leader.Position);

            SetState(CsBotState.Patrol, now);
            intent.State = CsBotState.Patrol;
            intent.Move = distance > CsBotTaskConst.FollowDistance
                ? _nav.ComputeMove(self.Position, leader.Position, CsBotTaskConst.FollowDistance, now,
                    viaRoute: false, hullHeight: self.Height)
                : Vector3.zero;
            intent.AimPoint = leader.EyePosition.sqrMagnitude > 0.0001f ? leader.EyePosition : LookAhead(self);
            return true;
        }


        /// <summary>跟随开始时把导航路线换成队长登记的那条（队长没登记 / 与本轮计划同路就不换）。</summary>
        private void ApplyFollowRoute(CsActor self, long leaderId)
        {
            if (_map == null || !_map.IsLoaded || leaderId == 0L) return;
            if (!CsBotRouteBoard.TryGet(_match, _match.RoundNumber, leaderId, out var route)) return;
            if (string.IsNullOrEmpty(route) || route == _planRoute) return;

            _nav.SetRouteKeepProgress(_map, route, self.Position);
            _followRouteApplied = true;
            _followRouteMarker = route;

            Game.Logger.Info(Tag,
                $"[BOT-TASK] {_name} 跟随队长换路线：'{_planRoute ?? "无"}' → '{_followRouteMarker}'" +
                $"（队长登记路线，{_nav.RemainingWaypoints} 个路点）");
        }


        /// <summary>离开跟随时还原自己的计划路线（没换过就什么都不做）。</summary>
        private void RestoreOwnRoute(CsActor self)
        {
            if (!_followRouteApplied) return;

            _followRouteApplied = false;
            var marker = _planRoute;
            _followRouteMarker = null;

            if (_map == null || !_map.IsLoaded || string.IsNullOrEmpty(marker)) return;
            _nav.SetRouteKeepProgress(_map, marker, self.Position);
        }


    }
}
