using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    public sealed partial class CsBotBrain
    {
        /// <summary>补位转点后到下一次可再判补位的最小间隔（秒）。转点后不能被反向交火立刻拉回去。</summary>
        private const float ReinforceCooldownSeconds = 8f;

        /// <summary>下一次允许判补位的时刻（0 = 本回合还没转过）。</summary>
        private float _nextReinforceAt;

        /// <summary>本回合补位转点次数（日志与探针只读）。</summary>
        private int _reinforceCount;

        private void MaybeResolveRound(CsActor self, float now)
        {
            if (_planRound == _match.RoundNumber) return;

            _planRound = _match.RoundNumber;

            //   序号在这里算一次，ChoosePlan 直接复用（两处同一口径，不各算一套）。
            _ordinal = CsBotPlans.TeamLocalOrdinal(_match.Actors, self.Team, self.Id);
            _planSlot = -1;
            _role = CsBotRoles.For(self.Team, _ordinal);
            _buy.OnRoundStart(_match.RoundNumber);
            _buy.SetOwner(self.Name);
            _nav.Reset();
            _nav.SetOwner(self.Name);

            _targetId = 0;
            _visibleNow = false;
            _closestVisibleDist = float.MaxValue;
            _firstSeenTime = 0f;
            _lastSeenTime = 0f;
            _lastSeenPos = Vector3.zero;
            _enemySeenAt.Clear();
            _defuserElectAt = 0f;
            _defuserElected = false;
            _pickupKeepCount = 0;
            _heardFresh = false;
            _heardTime = 0f;
            _campMoveUntil = 0f;
            _campPickAt = 0f;
            _holdSpots.Clear();
            _holdSlot = -1;
            _holdSwapAt = 0f;
            _holdSiteMarker = null;
            _holdSwapCount = 0;
            _nextReinforceAt = 0f;
            _reinforceCount = 0;
            _switchCooldownUntil = 0f;
            _grenadeThrows = 0;
            _nextGrenadeAt = 0f;
            _roundStartAt = now;
            _grenadeReturnToWeapon = false;
            _grenadeDiagKeys.Clear();
            ClearGrenadeAttempt();
            _strafeFlipAt = 0f;
            _engageEvent = false;
            _state = CsBotState.Idle;
            // 状态机同步复位（否则 _fsm.Current 还停在上回合的 Engage/Objective，与新回合的 _state 不一致）
            _fsm.Transition(CsBotFsmStates.Idle);
            _objectiveFsm.Transition(CsBotObjectiveStates.Approach);
            _replanCount = 0;
            _goalHoldUntil = 0f;
            _nextRepathAt = 0f;
            _lastGoalDist = float.MaxValue;
            _stallCount = 0;
            _nextStuckReplanAt = 0f;
            _nextJumpAt = 0f;
            _stuckProbeValid = false;
            _stuckDiagCount = 0;
            _nextStuckDiagLogAt = 0f;
            _stuckAtGoalCount = 0;
            _stuckGoalPos = Vector3.zero;
            _stuckLastEventPos = Vector3.zero;
            _stuckSamePosCount = 0;
            _yieldUntil = 0f;
            _yieldDir = Vector3.zero;
            _unreachableGoal = Vector3.zero;
            _unreachableGoalUntil = 0f;

            _lastSight = BotSightResult.Clear;
            _lastSightBlocker = null;
            _nextSightBlockLogAt = 0f;
            _nextFireDiagAt = 0f;
            _nextGraceLogAt = 0f;

            // 搜点 / 听声调查的回合内状态（已搜格是 per-bot 的，队级共享表在回合切换时清空）
            if (!ReferenceEquals(_searchShareMatch, _match) || _searchShareRound != _match.RoundNumber)
            {
                _searchShareMatch = _match;
                _searchShareRound = _match.RoundNumber;
                _searchSharedCells[0].Clear();
                _searchSharedCells[1].Clear();
            }

            _searchKind = BotSearchKind.None;
            _searchGoal = Vector3.zero;
            _searchStartedAt = 0f;
            _searchDeadline = 0f;
            _searchArrived = false;
            _sweepStartedAt = 0f;
            _sweepDirUntil = 0f;
            _sweepOrderPos = 0;
            _sweepOrderCount = 0;
            _sweepSearched = 0;
            _searchedCells.Clear();
            _lastKnownSearchedSeenTime = -1f;
            _nextNoiseInvestigateAt = 0f;
            _noisePos = Vector3.zero;

            ChoosePlan(self, now);
        }



        // ==================================================================
        //  本轮计划（路线 + 目标点）
        // ==================================================================
        private void ChoosePlan(CsActor self, float now)
        {
            _planRoute = null;
            _goalValid = false;
            _goalIsSite = false;
            _goalPos = self.Position;

            if (self.Team != CsTeam.T && self.Team != CsTeam.CT)
            {
                Game.Logger.Warn(Tag,
                    $"{self.Name} 的阵营是 {self.Team}（非 T/CT）—— 机器人不该出现在这个阵营，本轮不做导航");
                return;
            }

            //
            //   ① T 队 idx==0 与 idx==1 走**同一条路、同一个目标点**；
            //   ② CT 队 `idx % 3` ⇒ 槽位 0 与槽位 3 撞在 Route_CT_To_A + 同一个点；
            //   ③ 每回合固定出现 1 对 bot 的「路线+目标点」完全一样（Spliff/Darrell 恒同、Gooseman/ZBot 恒同），
            //      另有 44 对"目标点相同" ⇒ 用户看到的正是"路线都是相同的"。
            //
            // 新口径（构造性保证互不相同，不靠随机、不靠概率）：
            //   队内序号 = 同阵营 actor 里按 Id 升序的名次（0..n-1，与"真人在哪队"无关）；
            //   槽位 = (队内序号 - 持包者队内序号) mod 4（双射 ⇒ 4 只 bot 必得 4 个不同槽位）；
            //   持包者恒落槽位 0 = 主攻包点路线（出处：策划/策划案/CS1.6单机参考规格.md:130「T 持包到 B 点 → 下包」）。
            var ordinal = CsBotPlans.TeamLocalOrdinal(_match.Actors, self.Team, self.Id);
            _ordinal = ordinal;
            // 平移量必须取**本回合缓存的那一份**（RoundCarrierOrdinal），不能每只 bot 各问一次
            //   "现在谁持包"：持包者中途被打死会让不同 bot 各拿到 -1 / k ⇒ 槽位重合
            var carrierOrdinal = CsBotPlans.RoundCarrierOrdinal(
                _match.Actors, _match, self.Team, _match.RoundNumber);          // CT ⇒ -1（该函数内已判）
            // 槽位表按**本队实际 bot 数**紧凑映射（3 人时巡逻让位、另一包点恒有人），见 SlotLayout。
            var botCount = CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber);
            var plan = CsBotPlans.For(self.Team, CsBotPlans.SlotOf(ordinal, carrierOrdinal, botCount), _match.RoundNumber);
            _planSlot = plan.Slot;
            _role = plan.Role;   // 角色与计划槽位同源（持包者平移槽位后不许再按队内序号取角色）

            var routeMarker = plan.RouteMarker;
            var siteMarker = plan.GoalMarker;
            _planRoute = routeMarker;
            _nav.SetRoute(_map, routeMarker, self.Position);

            //   目标点**不能只判"地图上有这个标记"**：该点可能落在"位图说可走、但从出生点
            //   按抬腿 ≤ `CsConst.StepUpHeight` 扩张到不了"的格上 ⇒ 引擎 `AStar.Find` 直接 `badgoal` 返回 null
            //   ⇒ 退化成"朝 86m 外的点直线走" ⇒ 顶着墙原地卡到回合结束。
            //   口径与运行时同一份：BotNavigator.CanReach = SnapToWalkable + WalkableCellHeightAware + AStar。
            if (!string.IsNullOrEmpty(siteMarker) && TryPickGoalPointForPlan(self, plan, out var site))
            {
                _goalPos = site;
                _goalValid = true;
                _goalIsSite = plan.GoalIsSite;
            }
            else if (TryPickReachableRouteEnd(self, routeMarker, out var routeEnd))
            {
                // 包点标记缺失 / 该包点没有走得到的点 → 用**本路线上走得到的那个终点**（有路点就一定走得到）
                _goalPos = routeEnd;
                _goalValid = true;
                Game.Logger.Warn(Tag,
                    $"{self.Name} 取不到「走得到」的包点标记 '{siteMarker ?? "无"}' → 退化为「本路线上走得到的终点」" +
                    $"（目标={_goalPos}；判据 = BotNavigator.CanReach）");
            }
            else if (_nav.HasRoute)
            {
                // 兜底：连可达终点都取不到 ⇒ 退回"路线最后一点"（不许比既有实现更差），并把数字打进日志
                _goalPos = _nav.RouteEnd;
                _goalValid = true;
                Game.Logger.Warn(Tag,
                    $"{self.Name} 本路线的标记点**没有一个判为走得到**（路线 '{routeMarker}'）→ " +
                    $"退回未过滤的路线终点 {_goalPos}（⛔ 不复现「无目标」这种更差的行为）");
            }
            else
            {
                Game.Logger.Error(Tag,
                    $"{self.Name} 本轮既没有包点标记也没有任何路点（路线 '{routeMarker}'）→ 只能在原地朝当前朝向警戒。" +
                    "请检查 de_dust2 生成器是否摆了 CsMarkers 里的路线标记");
            }

            ArmGoal(self, now);

            //   「谁走哪条路、去哪个点、担任什么角色、路的头一段是哪」）。不许删字段：
            Game.Logger.Info(Tag,
                $"{self.Name}（{self.Team}/{_difficulty}/角色={RoleText()}）第 {_match.RoundNumber} 回合计划：" +
                $"本队bot={botCount}/槽位={_planSlot}/队内序号={_ordinal}{(self.HasBomb ? "(持C4)" : "")} 路线={_planRoute ?? "无"}" +
                $"（{CsBotPlans.RouteLabel(_planRoute)}） 首段路点={(HasFirstWaypoint ? FirstWaypoint.ToString() : "无")} 路点={_nav.RemainingWaypoints}" +
                $" 目标={( _goalValid ? _goalPos.ToString() : "无" )}（{CsBotPlans.GoalLabel(siteMarker)}#{plan.GoalOrdinal}）站点={_goalIsSite}" +
                $" 守点时长={ObjectiveHoldSeconds():F1}s 重寻路间隔={_profile.RepathInterval:F2}s");
        }



        /// <summary>
        /// 在标记 <paramref name="marker"/> 的点里按槽位序号挑一个**走得到**的目标点。
        ///
        /// <para><b>为什么要"按序号"而不是"取最近"</b>：一个包点标记里有 6~9 个点，两个槽位（T 槽位 0 与 1）
        /// 都去同一个包点 —— 若各自"取离自己最近的点"，两只 bot 从相邻出生点出发会**取到同一个点**。
        /// 改成"取确定性序列里第 <paramref name="ordinal"/> 个"后，**目标点由槽位决定、与出生位置无关**，
        /// 于是"同队两个槽位必得不同目标点"是构造性的。</para>
        ///
        /// <para><b>确定性序列</b> = 该标记的全部点先过滤 <c>CanStand</c>（与移动解算同口径），再按 (x, z) 排序
        /// —— 排序键与 <see cref="CsBotHoldSpots.Build"/> 完全一致（同一份口径，不另写一套）。
        /// 然后从第 <c>ordinal % count</c> 个起**向后找第一个走得到**的点（<see cref="BotNavigator.CanReach"/>）；
        /// 一个都走不到时退化为"最近的走得到的点"并打 Warn（不许比既有实现更差）。</para>
        /// </summary>
        /// <returns>true = 目标点已落进 <paramref name="point"/>。</returns>
        private bool TryPickGoalPoint(CsActor self, string marker, int ordinal, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null || !_map.IsLoaded) return false;

            var pts = _map.Points(marker);
            if (pts == null || pts.Length == 0) return false;

            var cand = new List<Vector3>(pts.Length);
            for (var i = 0; i < pts.Length; i++)
                if (_map.CanStand(pts[i]) && !IsBannedGoal(pts[i])) cand.Add(pts[i]);

            if (cand.Count == 0)
            {
                RateWarn("goal.nostand." + marker,
                    $"{_name} 的标记 '{marker}' 有 {pts.Length} 个点但**没有一个站得住**（CanStand 全 false）" +
                    "→ 退回未过滤点集里离自己最近的（与本方法引入前的行为一致）");
                return TryPickReachablePoint(self, marker, out point);
            }

            cand.Sort(CompareXZ);

            var start = ((ordinal % cand.Count) + cand.Count) % cand.Count;
            for (var k = 0; k < cand.Count; k++)
            {
                var p = cand[(start + k) % cand.Count];
                if (!_nav.CanReach(self.Position, p)) continue;

                point = p;
                return true;
            }

            RateWarn("goal.unreachable." + marker,
                $"{_name} 的标记 '{marker}' 第 {start + 1}/{cand.Count} 个点起**向后一个都走不到**" +
                "→ 退化为「离自己最近的走得到的点」（旧口径，⛔ 不许比旧行为更差）");
            return TryPickReachablePoint(self, marker, out point);
        }



        /// <summary>
        /// 本轮目标点。巡逻位（T 的侦察）取**对侧包点**附近的巡逻点：主攻在 A 时它压 B、主攻在 B 时它压 A，
        /// 同队才不会全部挤在同一个包点。其它标记仍按槽位序号取点。
        /// </summary>
        private bool TryPickGoalPointForPlan(CsActor self, CsBotPlan plan, out Vector3 point)
        {
            if (self.Team == CsTeam.T && plan.GoalMarker == CsMarkers.Patrol)
            {
                var siteIsA = (_match.RoundNumber % 2) == 0;
                var otherSite = siteIsA ? CsMarkers.BombsiteB : CsMarkers.BombsiteA;
                if (TryPickReachableNear(self, CsMarkers.Patrol, otherSite, out point)) return true;
            }

            return TryPickGoalPoint(self, plan.GoalMarker, plan.GoalOrdinal, out point);
        }



        /// <summary>
        /// 在 <paramref name="marker"/> 的点里取**离 <paramref name="anchorMarker"/> 最近**、且走得到的点
        /// （CanStand 与 CanReach 与 <see cref="TryPickGoalPoint"/> 同口径）。
        /// </summary>
        private bool TryPickReachableNear(CsActor self, string marker, string anchorMarker, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null || !_map.IsLoaded) return false;

            var pts = _map.Points(marker);
            var anchors = _map.Points(anchorMarker);
            if (pts == null || pts.Length == 0 || anchors == null || anchors.Length == 0) return false;

            var best = float.MaxValue;
            var found = false;
            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.CanStand(pts[i])) continue;
                if (IsBannedGoal(pts[i])) continue;

                var nearest = float.MaxValue;
                for (var a = 0; a < anchors.Length; a++)
                {
                    var v = pts[i] - anchors[a];
                    v.y = 0f;
                    nearest = Mathf.Min(nearest, v.magnitude);
                }

                if (nearest >= best) continue;
                if (!_nav.CanReach(self.Position, pts[i])) continue;

                best = nearest;
                point = pts[i];
                found = true;
            }

            return found;
        }



        private static int CompareXZ(Vector3 a, Vector3 b)
        {
            var c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.z.CompareTo(b.z);
        }



        private bool TryPickPoint(string marker, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null)
            {
                RateWarn("map.null", $"{_name} 需要地图标记 '{marker}' 但地图未绑定（ICsMap == null）");
                return false;
            }

            if (_map.TryGetPoint(marker, out point)) return true;

            RateWarn("point.missing." + marker, $"{_name} 取标记 '{marker}' 失败（地图上没有这个标记）");
            return false;
        }



        // ==================================================================
        //  目标生命周期：守点时长到 / 连续卡住 / 没有进展 → 重新选目标
        // ==================================================================
        /// <summary>
        /// 守点（到达目标后原地警戒）的时长（秒），同时也是"多久重新选一次目标"的间隔。
        ///
        /// <para>= <c>max(</c><see cref="CsBotConst.CampHoldSeconds"/>,
        /// <c>profile.RepathInterval × </c><see cref="CsBotConst.ObjectiveHoldScale"/><c>)</c>：
        /// Easy 1.6s → 4.8s / Normal 1.0s → 3.0s / Hard 0.5s → 2.5s。
        /// 这是三档难度里"重寻路间隔"最直观的落地 —— 低难度慢慢挪、高难度勤换位。</para>
        /// </summary>
        private float ObjectiveHoldSeconds()
        {
            //   突破手（0.6×）到点就走、守点位（2.5×）赖着不走，这才是用户要的"分工"。
            //   难度倍率与角色倍率是**两层**：前者定"这局 bot 多勤快"，后者定"队里每个人干什么"。
            var roleScale = CsBotRoles.HoldScale(_role);
            return Mathf.Max(CsBotConst.CampHoldSeconds,
                _profile.RepathInterval * CsBotConst.ObjectiveHoldScale) * roleScale;
        }



        /// <summary>
        /// 偏好交战距离（米）= <c>CsBotProfile.PreferredRange × <see cref="CsBotRoles.RangeScale"/></c>。
        /// 唯一取值处：交战段（<c>Engage</c>）不许再直接读 <c>_profile.PreferredRange</c>，
        /// 否则角色倍率只在一半的判定里生效（同一支枪在"推进"和"开火"上按两套距离走）。
        /// </summary>
        private float PreferredRange()
        {
            return _profile.PreferredRange * CsBotRoles.RangeScale(_role);
        }



        /// <summary>换了目标之后的统一收尾：重置守点计时、重寻路计时与"朝目标距离"基准。</summary>
        private void ArmGoal(CsActor self, float now)
        {
            _goalHoldUntil = 0f;        // 0 = 还没到达；Advance 里到达时才起算守点时长
            _nextRepathAt = now + Mathf.Max(0.25f, _profile.RepathInterval);
            var d = _goalValid ? _goalPos - self.Position : Vector3.zero;
            d.y = 0f;
            _lastGoalDist = d.magnitude;
            _stallCount = 0;
            _stuckAtGoalCount = 0;
            _stuckGoalPos = _goalPos;
            _campPickAt = 0f;
            _campMoveUntil = 0f;

            // 包点守卫 → 构造守位表（数据源 = 包点标记点，见 CsBotHoldSpots）。
            // 非包点目标（中路 / 巡逻 / 出生点）清空守位表 ⇒ 那几个目标仍走旧的"守够就换目标"。
            _holdSpots.Clear();
            _holdSlot = -1;
            _holdSwapAt = 0f;
            _holdSiteMarker = null;

            if (_goalValid && _goalIsSite && self.Team == CsTeam.CT)
            {
                NearestBombsitePoint(_goalPos, out var siteDist, out var siteName);
                if (siteDist <= CsBotConst.SiteRadius)
                {
                    _holdSiteMarker = siteName == "Bombsite A" ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
                    CsBotHoldSpots.Build(_map, _holdSiteMarker, _holdSpots);
                    // 记忆期内"走不到"的守位直接剔除（BotUnstuck 式的物理卡点在这里被跳过，而不是反复走进去）。
                    for (var i = _holdSpots.Count - 1; i >= 0; i--)
                        if (IsBannedGoal(_holdSpots[i])) _holdSpots.RemoveAt(i);

                    var n = _holdSpots.Count;
                    if (n >= 2)
                    {
                        _holdSlot = CsBotHoldSpots.PickSlot(_holdSpots, -1, self, _match.Actors);
                        Game.Logger.Info(Tag,
                            $"守位表就绪：{_name}（CT）包点 {_holdSiteMarker} 守位 {n} 个 " +
                            $"起始守位 {_holdSlot + 1}/{n} @ {_holdSpots[_holdSlot]} " +
                            $"（每 {CsBotConst.HoldSwapSeconds:F0}s 换位一次；守位间距 ≥ {CsBotConst.HoldSpotMinSeparation:F1}m）");
                    }
                    else
                    {
                        _holdSiteMarker = null;   // 取不到多点 ⇒ 明确退回单点守点（并已由 Build 打 Warn）
                    }
                }
                else
                {
                    Game.Logger?.Warn(Tag,
                        $"{_name} 的目标被判为包点（_goalIsSite）但离最近的包点标记 {siteDist:F1}m > {CsBotConst.SiteRadius:F0}m " +
                        "→ 不构造守位表，退回单点守点");
                }
            }
        }



        /// <summary>
        /// 离 <paramref name="pos"/> 最近的**包点标记点**（A/B 一起比），并给出这个距离与包点名。
        ///
        /// <para>这就是"在不在包点里"的判据 —— 与 <c>CsBomb.IsInBombsite</c>（<c>Module/Match/CsBomb.cs:478</c>）
        /// 同一口径：到**任一**包点标记的水平距离 ≤ <c>CsMarkers.BombsiteRadius</c>（= <see cref="CsBotConst.SiteRadius"/>）。</para>
        /// </summary>
        /// <returns>最近的包点标记点；地图/标记不可用时返回 <paramref name="pos"/> 且距离 = <c>float.MaxValue</c>。</returns>
        private Vector3 NearestBombsitePoint(Vector3 pos, out float dist, out string siteName)
        {
            dist = float.MaxValue;
            siteName = null;
            var point = pos;

            if (_map == null || !_map.IsLoaded) return point;

            for (var s = 0; s < 2; s++)
            {
                var isA = s == 0;
                var pts = _map.Points(isA ? CsMarkers.BombsiteA : CsMarkers.BombsiteB);
                if (pts == null) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - pos;
                    d.y = 0f;
                    var len = d.magnitude;
                    if (len >= dist) continue;

                    dist = len;
                    point = pts[i];
                    siteName = isA ? "Bombsite A" : "Bombsite B";
                }
            }

            return point;
        }



        /// <summary>是否正在执行"去捡掉落的 C4"（这个目标不能被当成普通目标拉黑：必须有人去捡）。</summary>
        private bool IsPickupRun(CsActor self)
        {
            if (self.Team != CsTeam.T || self.HasBomb || _match.BombPlanted) return false;
            if (_match.BombPosition.sqrMagnitude < 1f) return false;
            if (AnyTCarriesBomb()) return false;
            return IsElectedBombHunter(self);
        }



        /// <summary>
        /// 重新选目标 —— 本轮路线走完 / 守点时长到 / 连续卡住时的**唯一**出路。
        ///
        /// <para>候选顺序（任一命中即用；每个候选目标点都要"走得到"才采用）：</para>
        /// <list type="number">
        /// <item><b>换一条路线</b>：同阵营的另一条路（T: A 路 / B 路 / 中路；CT: A 守 / B 守 / 中），
        /// 目标是该路配套的包点（拿不到包点标记时退化为该路终点）；</item>
        /// <item><b>去巡逻</b>：<see cref="CsMarkers.Patrol"/> 整条巡点路线（最近邻排序后逐个走）——
        /// 对应"无敌人时在 Route_Patrol 里随机巡点"；</item>
        /// <item><b>回本方出生点</b>：最坏情况也能回到有队友的地方，绝不原地空转。</item>
        /// </list>
        ///
        /// <para>每 <see cref="CsBotConst.ReplanAlternatePeriod"/> 次换目标交替一次"换路线优先 / 去巡逻优先"，
        /// 否则机器人会永远在同两条路线之间来回、永远不去巡图。</para>
        /// </summary>
        /// <returns>true = 已选到新目标（<see cref="_goalPos"/> / <see cref="_planRoute"/> 已更新）。</returns>
        private bool ReplanObjective(CsActor self, string reason, float now)
        {
            if (_map == null || !_map.IsLoaded)
            {
                RateWarn("replan.nomap",
                    $"{_name} 需要重新选目标（{reason}）但地图不可用（{(_map == null ? "未绑定" : "尚未加载")}）→ 先原地警戒");
                return false;
            }

            _replanCount++;

            // 包已下 → T 的新目标就是 C4 本身（守包）；这时去"换路线/巡逻"等于把回合送掉。
            if (_match.BombPlanted && self.Team == CsTeam.T)
            {
                var bomb = _match.BombPosition;
                if (bomb.sqrMagnitude > 1f && _map.WalkableAt(bomb.x, bomb.z))
                {
                    _nav.ClearRoute();
                    ApplyObjective(self, CsBotConst.BombGuardRoute, bomb, false, $"包已下 → 回 C4 旁守包（{reason}）", now);
                    return true;
                }

                RateWarn("replan.bomb.nowalk",
                    $"{_name} 包已下但 C4 位置 {bomb} 不可走（WalkableAt=false）→ 退化为常规换目标");
            }

            var preferPatrol = (_replanCount % CsBotConst.ReplanAlternatePeriod) == 0;

            //   **本项目新增**（修"AI 无胜利策略 / 不守点"）：包未下时，换目标**不许离开本轮的分工目标**。
            //   规格（策划/策划案/CS1.6单机参考规格.md §2.4）：CT = 守包点、T = 进攻包点下包；
            //   而本函数原先的候选序是"换路线 → 巡逻 → 出生点"且**隔次以巡逻优先** ⇒ 包未下的绝大多数时间里
            //   bot 落进 Route_Patrol 到处巡点 ⇒ 用户看到的"没有胜利策略、乱跑、不蹲点不进攻"。
            //   规则（按 CsBotPlans.For 的槽位分工，不新增数据表）：当前槽位的计划目标是**包点**的
            //   （T 的 0/1/3 进攻位、CT 的 0/1 守点位）换目标时只许回到自己分工的包点 ——
            //   不去巡逻、不出生点；侦察位（槽位 2，计划目标就是巡逻线）保持原候选序游走。
            if (!_match.BombPlanted)
            {
                var carrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
                var botCount = CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber);
                var from = _planSlot >= 0 ? _planSlot : CsBotPlans.SlotOf(_ordinal, carrier, botCount);
                var anchor = CsBotPlans.For(self.Team, from, _match.RoundNumber);
                var anchorSite = anchor.GoalIsSite ? _map.Points(anchor.GoalMarker) : null;

                if (anchorSite != null && anchorSite.Length > 0)
                {
                    // 路线被活着的队友占着（接管/撞车后的残留）⇒ 不动路线、只换目标点，且挑点要避开队友
                    var routeFree = !CsBotPlans.FamilyHeldByAliveTeammate(_match.Actors, self.Team, carrier,
                        _match.RoundNumber, self.Id, anchor.RouteMarker);

                    //   与 TryRouteObjective 同一不变量：**先只读筛、选定后才切路线** ——
                    //   TryPickGoalPoint / TryPickReachable 只用 _nav.CanReach（与 nav 当前装哪条路线无关），
                    //   全都筛不出时 nav 一个字都不动（否则残留会污染下面 TryRouteObjective 的 prevMarker）。
                    if (TryPickGoalPoint(self, anchor.GoalMarker, anchor.GoalOrdinal, out var goalPt))
                    {
                        if (routeFree) _nav.SetRouteKeepProgress(_map, anchor.RouteMarker, self.Position);
                        ApplyObjective(self, anchor.RouteMarker, goalPt, true,
                            $"站回分工位（{reason}；槽位 {anchor.Slot}={CsBotRoles.Label(anchor.Role)}" +
                            $"→{CsBotPlans.RouteLabel(anchor.RouteMarker)}→{CsBotPlans.GoalLabel(anchor.GoalMarker)}，" +
                            $"包未下不巡逻）", now);
                        return true;
                    }

                    // 分工位取不到走得到的点（或路线被占）→ 该包点标记里走得到且不与队友同格的点
                    if (TryPickReachable(self, anchorSite, CsBotConst.MinPatrolDistance, false,
                                            out var sitePt, avoidTeammates: true) ||
                        TryPickReachable(self, anchorSite, 0f, false, out sitePt, avoidTeammates: true))
                    {
                        if (routeFree) _nav.SetRouteKeepProgress(_map, anchor.RouteMarker, self.Position);
                        ApplyObjective(self, anchor.RouteMarker, sitePt, true,
                            $"站回分工包点上走得到的点（{reason}；分工位 '{anchor.GoalMarker}'#{anchor.GoalOrdinal} 走不到" +
                            $"{(routeFree ? "" : " 或路线被队友占")}）", now);
                        return true;
                    }

                    RateWarn("replan.anchor.unreachable",
                        $"{_name} 分工位（槽位 {anchor.Slot} → {anchor.GoalMarker}#{anchor.GoalOrdinal}）" +
                        $"及其包点标记全判为走不到（{reason}）→ 退回常规候选序");
                }
            }

            // 常规候选序只留给"槽位 2 的侦察 / 分工位全部走不到"这两种情况（与本函数引入前一致）：
            if (preferPatrol && TryPatrolObjective(self, reason, now)) return true;
            if (TryRouteObjective(self, reason, now)) return true;
            if (!preferPatrol && TryPatrolObjective(self, reason, now)) return true;
            if (TrySpawnObjective(self, reason, now)) return true;
            if (TryLastResortObjective(self, reason, now)) return true;

            RateWarn("replan.exhausted",
                $"{_name} 重新选目标失败（{reason}）：路线 / 巡逻点 / 出生点都取不到可走的目标点 → 原地警戒。" +
                "请检查地图标记（CsMarkers）与几何体是否把标记点埋住了");
            return false;
        }



        /// <summary>
        /// 换目标：从**自己的槽位**起按 <c>(_planSlot + k) % 4</c> 轮转（k = 1,2,3）挑下一条路。
        ///
        /// <para><b>为什么按槽位轮转</b>：若按 <c>(Id + replanCount) % 3</c> 之类取起点、且路只有 3 条，
        /// 4 只 bot 必然有两只落在同一条路上（换目标之后"路线都一样"，正是用户看到的现象）。</para>
        ///
        /// <para>从槽位起轮转 ⇒ 每只 bot 的 <c>_planSlot</c> 互不相同，取 <c>k</c> 相同的两只 bot
        /// 得到的槽位也互不相同 ⇒ **每次换目标后，同队 4 只 bot 仍走 4 条不同的路、去 4 个不同的目标点**
        /// （撞车需要两只 bot 的槽位相同，而那已被构造性排除）。</para>
        ///
        /// <para>轮转时跳过**刚走完的那条路**（<c>_planRoute</c>）：槽位差 1 的平移天然换路，所以 k 从 1 起即可。</para>
        /// </summary>
        private bool TryRouteObjective(CsActor self, string reason, float now)
        {
            // **进入本函数时** nav 装的是哪条路线：本函数内每一档都会在**筛选期间**尝试换路线
            // （`SetRouteKeepProgress` 一旦发现标记不同就立即 SetRoute 重排 + 游标归零），所以必须在这里 ——
            // 而不是在中途某一档 —— 记住"前值"，否则抓到的已经是前面档位的尝试结果。最终若一个都没选中，
            // 末尾把它恢复回来（"没选中 ⇒ 不改变 nav 状态"）。
            var prevMarker = _nav.RouteMarker;

            // 「路线家族」是同队的**共享资源**：计划表保证开局 4 只各占一格，轮转也必须遵守同一条互斥，
            // 否则一只卡住的 bot 会轮转进队友正走着的路（日志形态：两只**都活着**的 bot 同时走同一条
            // 'Route_T_*'，即用户看到的"路线都一样"）。
            var carrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
            var taken = CsBotPlans.AliveSlotTable(_match.Actors, self.Team, carrier, _match.RoundNumber, null);
            var botCount = CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber);
            var from = _planSlot >= 0 ? _planSlot : CsBotPlans.SlotOf(_ordinal, carrier, botCount);

            for (var k = 1; k <= CsBotPlans.Slots; k++)
            {
                var slot = ((from + k) % CsBotPlans.Slots + CsBotPlans.Slots) % CsBotPlans.Slots;
                if (slot >= 0 && slot < CsBotPlans.Slots && taken[slot])
                {
                    continue;   // 该家族有**活着的**队友在走 ⇒ ⛔ 不抢（死人的家族算空出来的，可以被接手）
                }

                var plan = CsBotPlans.For(self.Team, slot, _match.RoundNumber);
                var routeMarker = plan.RouteMarker;
                if (string.IsNullOrEmpty(routeMarker)) continue;
                if (k < CsBotPlans.Slots && routeMarker == _planRoute) continue;   // 刚走完的那条最后再试

                var routePts = _map.Points(routeMarker);
                if (routePts == null || routePts.Length == 0)
                {
                    RateWarn("replan.route.empty",
                        $"{_name} 换路线 '{routeMarker}'（槽位 {plan.Slot}）失败（地图上没有该标记的路点）→ 试下一条");
                    continue;
                }

                // **先只读筛、选定后才切路线**：`TryPickGoalPoint` / `TryPickReachable` 都只用
                // `_nav.CanReach`（"问一句"这一点能不能走得到），与 nav 当前装的是**哪条**路线无关 ⇒
                // 可以在切路线之前就把目标点定下来；筛不出就 `continue`，nav 不会被留在
                // **没被选中**的家族上（否则要靠 `RestoreRouteMarker` 兜回来，那次恢复是
                // "按当前位置最近邻重排 + 游标归零"，属白付的代价）。
                //
                //   不直接用 `_nav.RouteEnd` / 裸包点标记点：这两者都可能是"可走但走不到"的格。
                //   （这里**不传** `avoidTeammates`：本档挑的是"没有活队友在走的空家族"，
                //    取点不存在与队友撞同一点的问题 —— 需要排除队友占位的是下面"抢家族"的两档。）
                var goal = Vector3.zero;
                var isSite = false;

                if (!string.IsNullOrEmpty(plan.GoalMarker) &&
                    TryPickGoalPoint(self, plan.GoalMarker, plan.GoalOrdinal, out var site))
                {
                    goal = site;
                    isSite = plan.GoalIsSite;
                }
                else if (TryPickReachable(self, routePts, 0f, false, out var end))
                {
                    goal = end;
                }
                else
                {
                    RateWarn("replan.route.unreachable." + routeMarker,
                        $"{_name} 换路线 '{routeMarker}' 的目标点全判为走不到" +
                        $"（目标标记 '{plan.GoalMarker ?? "无"}' 与该路线的标记点都没过 BotNavigator.CanReach）→ 试下一条");
                    continue;
                }

                _nav.SetRouteKeepProgress(_map, routeMarker, self.Position);
                if (!_nav.HasRoute)
                {
                    // 这里**不必**恢复 nav：本分支必然 `continue`，而后面无论"某一条选中"（会在 return 前
                    // 再切一次路线）还是"全都没选中"（末尾统一 `RestoreRouteMarker`）都会把状态收干净 ——
                    // 每多调一次恢复就多跑一次 `SetRoute`（内含逐点 A* 的 DropUnreachableWaypoints）。
                    // key 与上面的"前置判空"分开，避免两件事共用同一份降频计数。
                    RateWarn("replan.route.empty.nav",
                        $"{_name} 换路线 '{routeMarker}'（槽位 {plan.Slot}）后 nav 仍没有路线（标记异常）→ 试下一条");
                    continue;
                }

                _planSlot = plan.Slot;
                ApplyObjective(self, routeMarker, goal, isSite,
                    $"换一条路线（槽位 {plan.Slot}={plan.RouteLabel}→{plan.GoalLabel}#{plan.GoalOrdinal}；{reason}）", now);
                return true;
            }

            // ---- 退一档：4 条家族都被**活着的**队友占着（全队都活着 = 常态）⇒ 不抢队友的路，
            //      留在自己的槽位、只把**目标点**换成同一条路线上另一个点（序号也要与队友互斥）。----
            //
            var mySlot = from;
            var myPlan = CsBotPlans.For(self.Team, mySlot, _match.RoundNumber);
            if (!string.IsNullOrEmpty(myPlan.RouteMarker))
            {
                // 与第一档同形：**先只读筛（不碰 nav）、选定后才切路线**。本档筛的全是只读判据
                // （`GoalCandidateCount` / `NextFreeGoalOrdinal` / `TryPickGoalPoint` / `TryPickReachable`，
                // 后者只用 `_nav.CanReach`），与 nav 当前装的是哪条路线无关。
                var myRoutePts = _map.Points(myPlan.RouteMarker);
                if (myRoutePts != null && myRoutePts.Length > 0)
                {
                    var count = GoalCandidateCount(myPlan.GoalMarker);
                    var goal = Vector3.zero;
                    var picked = false;
                    var ordinal = myPlan.GoalOrdinal;

                    for (var d = 0; d < count; d++)
                    {
                        var o = CsBotPlans.NextFreeGoalOrdinal(_match.Actors, self.Team, carrier,
                            _match.RoundNumber, self.Id, myPlan.GoalMarker, ordinal, count);
                        if (o < 0) break;                       // 该标记上的序号被队友占满了
                        if (o == ordinal) break;                // 没有下一个（count=1 之类）
                        ordinal = o;
                        if (TryPickGoalPoint(self, myPlan.GoalMarker, ordinal, out var pt))
                        {
                            goal = pt;
                            picked = true;
                            break;
                        }
                    }

                    if (!picked && TryPickReachable(self, myRoutePts, 0f, false,
                                                    out var end, avoidTeammates: true))
                    {
                        goal = end;
                        picked = true;
                        RateWarn("replan.inslot.gofallback",
                            $"{_name} 留在槽位 {mySlot}（'{myPlan.RouteLabel}'）但目标标记 '{myPlan.GoalMarker}' " +
                            "上的序号都被占/走不到 → 退化为该路线上离自己最近的走得到的路点");
                    }

                    if (picked)
                    {
                        // 选定之后才真正切路线（此前一律不碰 nav）
                        _nav.SetRouteKeepProgress(_map, myPlan.RouteMarker, self.Position);
                        if (!_nav.HasRoute)
                        {
                            // 极罕见（`myRoutePts` 非空 + 地图已加载时 `SetRoute` 必留下路点 ⇒ `HasRoute` 必真）。
                            // 处理方式：撤销"已选中"并**继续落到下面的兜底档**（改前落到兜底档，现在保持一致），
                            // 而不是直接 `return false` 把这一帧的机会丢掉。
                            RateWarn("replan.inslot.nav",
                                $"{_name} 选定留在槽位 {mySlot}（'{myPlan.RouteLabel}'）后 nav 仍没有路线" +
                                "（标记异常）→ 放弃本档，继续试兜底档");
                            RestoreRouteMarker(prevMarker, self);
                            picked = false;
                        }
                    }

                    if (picked)
                    {
                        _planSlot = mySlot;
                        ApplyObjective(self, myPlan.RouteMarker, goal, myPlan.GoalIsSite,
                            $"无空路线（4 条都被**活着的**队友占着）⇒ 留在槽位 {mySlot}={myPlan.RouteLabel}，" +
                            $"只换目标点（{myPlan.GoalLabel}#{ordinal}；{reason}）", now);
                        return true;
                    }
                }
            }

            // ---- 最后一档兜底（**本项目新增**）：走到这里说明 ① 没有空家族（4 条都被活着的队友占着），
            //      ② 连自己槽位的"换目标点"也换不出来（目标标记上的序号被占满，且本路线没有任何
            //      走得到的标记点 —— TryPickReachable 全失败）。
            //
            //      直接 `return false` ⇒ 上层拿不到新目标 ⇒ 只能继续朝那个**走不到**的点直线走 ⇒
            //      被几何卡住就是永久 0.00m。
            //
            //      这一档允许**点开一条被活队友占着的家族**，且只取"离自己最近的、走得到的路点"
            //      （序号互斥仍在，不抢队友的目标点）—— 宁可跟队友同路，也不许原地卡死。
            //      （原版 bot 同样允许与队友走同一条路；出处：本项目新增，导航自恢复的最后一档。）
            //
            //      起点**必须与本 bot 的 `_planSlot` 无关**（这里用"队内序号算出的槽位"）：
            //      ① 若一律从 slot 0 扫起，全队候选顺序完全相同（只有"刚走过的那条"会跳过）⇒ 同时卡住的
            //         几只都会在第一轮选中同一个家族，必然撞车（并触发同队路线撞车审计）；
            //      ② 若从 `mySlot = _planSlot` 起推也不行 —— **兜底成功后 `_planSlot` 会被写成抢到的那个
            //         槽位**（= 真主人的槽位），于是抢家者与被抢者的起点序列**完全相同** ⇒ 下一次必然再次
            //         选中同一家族（自我固化、撞车周期性复发）。`SlotOf(_ordinal, carrier)` 只依赖自己
            //         的队内序号，抢家者与被抢者的 `_ordinal` 必不同 ⇒ 起点必不同。
            var startSlot = CsBotPlans.SlotOf(_ordinal, carrier,
                CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber));
            for (var i = 0; i < CsBotPlans.Slots; i++)
            {
                var slot = ((startSlot + 1 + i) % CsBotPlans.Slots + CsBotPlans.Slots) % CsBotPlans.Slots;
                var plan = CsBotPlans.For(self.Team, slot, _match.RoundNumber);
                var marker = plan.RouteMarker;
                if (string.IsNullOrEmpty(marker) || marker == _planRoute) continue;   // 刚走完的那条最后再试

                // **先只读筛**（不碰 nav）：`TryPickReachable` 只"问一句"这个家族有没有走得到的路点
                // （内含一次 A*），不改 nav 的路线。边扫边 `SetRouteKeepProgress` 不行：任何一次
                // continue 都会把 nav 的路线留在**没被选中**的那个家族上，而 `_planRoute`/`_goalPos`
                // 还是旧的 ⇒ 下一帧 Advance 会沿一条自己没选的路线走格子。
                // 本函数的三档（第一档换家族 / 退一档换目标点 / 本档抢家族）**统一是这个形状**，
                // 所以 `RestoreRouteMarker` 的恢复分支实际只剩"进来时本来没路线"那条路径会走到。
                var points = _map.Points(marker);
                if (points == null || points.Length == 0) continue;
                if (!TryPickReachable(self, points, 0f, false, out var end, avoidTeammates: true)) continue;

                // 选定之后才真正切路线；此后不再 continue，nav 与 `_planRoute` 一致地换到这一条。
                _nav.SetRouteKeepProgress(_map, marker, self.Position);
                if (!_nav.HasRoute)
                {
                    RateWarn("replan.route.grab.nav",
                        $"{_name} 兜底选中家族 '{plan.RouteLabel}' 后 nav 仍没有路线（标记异常）→ 放弃本档");
                    RestoreRouteMarker(prevMarker, self);   // 早退同样要对齐 nav（否则停在没选中的家族上）
                    return false;
                }

                _planSlot = plan.Slot;
                RateWarn("replan.route.grab",
                    $"{_name} 无空路线、且本槽位换不出目标点 ⇒ 兜底点开被队友占着的家族 " +
                    $"'{plan.RouteLabel}'（槽位 {plan.Slot}），只取离自己最近的可走路点（{reason}）");
                ApplyObjective(self, marker, end, false,
                    $"兜底：点开被占家族 {plan.RouteLabel}（槽位 {plan.Slot}）= 最近可走路点；{reason}", now);
                return true;
            }

            // 全部档都没选中 ⇒ nav 不许留下任何"筛选时的痕迹"（见函数开头 prevMarker 的说明）。
            RestoreRouteMarker(prevMarker, self);

            return false;
        }



        /// <summary>
        /// 把 nav 的路线标记对齐回 <paramref name="prevMarker"/> —— "没选中任何家族 ⇒ 不改变 nav 状态"。
        ///
        /// <para>两种形态都要处理：① 进来时本来没路线（<c>null</c>）⇒ 必须清掉中途被装上的那条
        /// （否则 nav 停在自己没选的家族上，下一帧 <c>Advance</c>/<c>ResolveNextTarget</c> 会沿它走格子）；
        /// ② 进来时有路线 ⇒ 恢复那条 —— 注意 <see cref="BotNavigator.SetRouteKeepProgress"/> 内部是
        /// "按当前位置最近邻重排"，恢复的是**标记**、游标会归零（比"沿一条自己没选的路线走"轻，可接受）。</para>
        ///
        /// <para><b>代价（已知，登记在此，别当 bug 查）</b>：<c>SetRoute</c> 内部会
        /// <c>ClearEscape()</c> + <c>ClearStuckState()</c> + <c>InvalidatePath()</c> ⇒
        /// "兜底全失败 ⇒ 恢复"这一帧会把**刚攒够的卡住判据清掉**，下一次换目标/逃逸要再等一个
        /// <c>StuckCheckInterval</c>(0.5s)；且重排取"离当前位置最近的标记点"，而巡逻家族是**环线** ⇒
        /// 那个点可能是**已经走过**的一个（表现为走一小段回头路，一个路点间距，不是 U 形掉头）。
        /// ⇒ ⛔ 不要把 <c>StuckWarn</c> 的计数当成"卡住"的性能指标（它会被这里清掉一部分）。</para>
        /// </summary>
        private void RestoreRouteMarker(string prevMarker, CsActor self)
        {
            if (_nav == null) return;

            if (prevMarker == null)
            {
                // **无条件清**（不能只在 `HasRoute` 时清）："兜底选中后 nav 仍没有路线"那条早退路径上
                // `_route` 本来就是空的（`HasRoute` 为假），但 `SetRoute` 已经把 `_marker` 设成了新标记
                // ⇒ 只判 `HasRoute` 会留下"标记是刚抢的家族、路点却是空"的**假状态**：
                // 行为上无害（后续 `SetRouteKeepProgress` 要求 `_route.Count > 0` ⇒ 必走重建），
                // 但读 `_nav.RouteMarker` 的探针/审计会把"没选中"读成"选中了那个家族"。
                _nav.ClearRoute();
                return;
            }

            if (_nav.RouteMarker != prevMarker) _nav.SetRouteKeepProgress(_map, prevMarker, self.Position);
        }



        /// <summary>
        /// 某个目标标记上**实际可用**的点数（<c>CanStand</c> 过滤后）—— 给
        /// <see cref="CsBotPlans.NextFreeGoalOrdinal"/> 当 <c>candidateCount</c>。
        /// <para>与 <see cref="TryPickGoalPoint"/> 用的是**同一条过滤**（<c>CanStand</c>）；两者若漂移，
        /// 序号与点就对应不上，所以这里刻意只留一处过滤条件、并在 <c>TryPickGoalPoint</c> 注释里互指。</para>
        /// </summary>
        private int GoalCandidateCount(string marker)
        {
            if (_map == null || !_map.IsLoaded || string.IsNullOrEmpty(marker)) return 0;

            var pts = _map.Points(marker);
            if (pts == null || pts.Length == 0) return 0;

            var n = 0;
            for (var i = 0; i < pts.Length; i++)
                if (_map.CanStand(pts[i])) n++;

            return n;
        }



        /// <summary>
        /// "去巡逻"候选：走 <see cref="CsMarkers.Patrol"/>，目标 = **走得到且离自己最近**的那个巡点
        /// （距离 ≥ <see cref="CsBotConst.MinPatrolDistance"/>）。
        ///
        /// </summary>
        private bool TryPatrolObjective(CsActor self, string reason, float now)
        {
            // 进入本函数时 nav 装的是哪条路线（与 `TryRouteObjective` 同口径）：本函数会切到
            // `Route_Patrol`，若之后没能选中目标就必须恢复 —— 否则 `ReplanObjective` 紧接着调
            // `TryRouteObjective` 时，它开头的 `prevMarker` 会抓到 `Route_Patrol` 这个残留，
            // 而那个残留又可能被它末尾"恢复/固化"成合法状态（同一形状的传播链，见那里注释）。
            var prevMarker = _nav.RouteMarker;

            var pts = _map.Points(CsMarkers.Patrol);
            if (pts == null || pts.Length == 0)
            {
                RateWarn("replan.patrol.empty",
                    $"{_name} 想巡逻但地图上没有 '{CsMarkers.Patrol}' 标记（Points 返回空）→ 试其它目标");
                return false;
            }

            //   "换目标"不许直接跳进巡逻线：那会让槽位 2 的主人（本来就该走巡逻）与这只 bot
            //   同时走 `Route_Patrol`（同队路线撞车，用户看到的就是这个）。
            //   活着的队友在走 ⇒ 不抢；死人的家族算空出来的（可以被接手）。
            if (_planRoute != CsMarkers.Patrol)
            {
                var patrolCarrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
                if (CsBotPlans.FamilyHeldByAliveTeammate(_match.Actors, self.Team, patrolCarrier,
                        _match.RoundNumber, self.Id, CsMarkers.Patrol))
                {
                    Game.Logger.Info(Tag,
                        $"{_name}（{self.Team}/{_difficulty}）本想去巡逻，但巡逻线已被一只**活着的**队友占着" +
                        $"→ 不抢（互斥优先于换目标；{reason}）");
                    return false;
                }
            }

            // 与 `TryRouteObjective` 同形：**先只读筛、选定后才切路线**。
            // 先 `SetRouteKeepProgress(Patrol)` 再筛不行：两次 `TryPickReachable` 都失败就 `return false`
            // ⇒ nav 停在 `Route_Patrol` 而 brain 的 `_planRoute` 还是旧家族，且会
            // **跨函数传播**：`ReplanObjective` 紧接着调 `TryRouteObjective`，后者开头抓到的 `prevMarker`
            // 就是这个残留，一旦它最终没选中，末尾会把残留**恢复/固化**成"合法状态"
            // （于是"没选中 ⇒ 不改 nav"这条不变量在这一整条链上都不成立）。
            // （`TryPickReachable` 只用 `_nav.CanReach`，与 nav 当前装的是哪条路线无关 ⇒ 可以先筛。）

            // 近的优先（不再"专挑最远"）：门口那个走得到的巡点，比 80m 外走不到的那个有用。
            if (!TryPickReachable(self, pts, CsBotConst.MinPatrolDistance, false, out var goal))
            {
                // 退化一层：去掉最小距离限制再问一次（"最近的走得到的点"口径），并把"为什么退化"写进日志
                if (!TryPickReachable(self, pts, 0f, false, out goal))
                {
                    RateWarn("replan.patrol.unreachable",
                        $"{_name} 的巡逻点**没有一个走得到**（'{CsMarkers.Patrol}' 标记点全没过 " +
                        $"BotNavigator.CanReach：位图可走但高度一致性层到不了 / 或真孤岛）→ 试其它目标");
                    return false;   // ⛔ 这条路径**不碰 nav**（残留问题见上）
                }

                RateWarn("replan.patrol.nofar",
                    $"{_name} 的巡逻点里没有「≥ {CsBotConst.MinPatrolDistance:F0}m 且走得到」的点 → " +
                    $"退化为最近的那个走得到的巡点 {goal}");
            }

            _nav.SetRouteKeepProgress(_map, CsMarkers.Patrol, self.Position);
            if (!_nav.HasRoute)
            {
                // 极罕见（`pts` 已在 :1125 判过非空 ⇒ `SetRoute` 必留下路点 ⇒ `HasRoute` 必真）。
                // 仍按"没选中 ⇒ 不改 nav"处理：恢复进来时的那条再返回，别把 `Route_Patrol` 残留
                // 传给紧接着调用的 `TryRouteObjective`。
                RateWarn("replan.patrol.nav",
                    $"{_name} 切到 '{CsMarkers.Patrol}' 后 nav 仍没有路线（标记异常）→ 放弃本档并恢复原路线");
                RestoreRouteMarker(prevMarker, self);
                return false;
            }

            ApplyObjective(self, CsMarkers.Patrol, goal, false, $"去巡逻（{reason}）", now);
            return true;
        }



        private bool TrySpawnObjective(CsActor self, string reason, float now)
        {
            var marker = self.Team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT;
            var pts = _map.Points(marker);
            if (pts == null || !TryPickReachable(self, pts, 0f, false, out var point))
            {
                RateWarn("replan.spawn.none",
                    $"{_name} 本方出生点 '{marker}' 取不到**走得到**的点（CanReach 全部 false）→ 无法重新选目标");
                return false;
            }

            _nav.ClearRoute();
            ApplyObjective(self, marker, point, false, $"回出生点（{reason}）", now);
            return true;
        }



        /// <summary>
        /// 兜底目标：路线 / 巡逻 / 出生点**全部**没过 <see cref="BotNavigator.CanReach"/> 时的最后一条出路 ——
        /// 在这三组标记点里取"站得住（<c>CanStand</c>）且离自己最近"的一个当目标，**不再问可达性**。
        ///
        /// <para><b>为什么必须兜这一层</b>：可达性判据建立在位图上，而位图是**单层 2D**（多层几何上下重叠），
        /// 它会把一些物理上走得通的地方判成走不到。全 false 时的既有行为是"原地警戒"
        /// （本帧 <c>intent.Move = zero</c>）—— 机器人剩下整回合一动不动、净位移 0。</para>
        ///
        /// <para>候选只用**已有的**地图标记点（本回合路线标记 / 巡逻线 / 本方出生点），不新增目标坐标。</para>
        /// </summary>
        private bool TryLastResortObjective(CsActor self, string reason, float now)
        {
            if (_map == null || !_map.IsLoaded) return false;

            var markers = new[]
            {
                _planRoute,
                CsMarkers.Patrol,
                self.Team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT,
            };

            var bestDist = -1f;
            var bestMarker = (string)null;
            var bestPoint = Vector3.zero;
            var heldSkipped = 0;

            // 兜底**不是**互斥门禁的例外：3 个候选里的 `_planRoute` / `CsMarkers.Patrol` 都是**分工家族**，
            //   活着的队友正走着就不许抢（与 TryRouteObjective 的槽位表、TryPatrolObjective 的家族门禁同一条判据）。
            //   不加这一道，兜底会把一只 bot 放进队友的家族里 —— 而且它自己的槽位还写着旧家族
            //   （实机形态：`同队路线撞车：Cliffe(CT/槽位2) 与 Scuzzy(CT/槽位3) 都走 'Route_Patrol'`）。
            var carrierOrdinal = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);

            for (var i = 0; i < markers.Length; i++)
            {
                var marker = markers[i];
                if (string.IsNullOrEmpty(marker)) continue;

                if (CsBotPlans.SlotOwningRoute(self.Team, marker, _match.RoundNumber) >= 0 &&
                    CsBotPlans.FamilyHeldByAliveTeammate(_match.Actors, self.Team, carrierOrdinal,
                        _match.RoundNumber, self.Id, marker))
                {
                    heldSkipped++;
                    continue;
                }

                var pts = _map.Points(marker);
                if (pts == null) continue;

                for (var k = 0; k < pts.Length; k++)
                {
                    if (!_map.CanStand(pts[k])) continue;

                    var d = pts[k] - self.Position;
                    d.y = 0f;
                    var dist = d.magnitude;
                    if (bestDist >= 0f && dist >= bestDist) continue;

                    bestDist = dist;
                    bestMarker = marker;
                    bestPoint = pts[k];
                }
            }

            if (bestDist < 0f)
            {
                if (heldSkipped > 0)
                {
                    RateWarn("replan.lastresort.held",
                        $"{_name} 兜底目标：3 个候选标记里 {heldSkipped} 个是**活着的**队友正走着的分工家族 ⇒ " +
                        $"不抢（互斥优先于兜底：本帧保留上一个目标，宁可不动也不与队友同路；{reason}）");
                }
                return false;
            }

            _nav.ClearRoute();
            ApplyObjective(self, bestMarker, bestPoint, false,
                $"可达性判据对路线 / 巡逻 / 出生点**全部**为 false ⇒ 兜底取标记 '{bestMarker}' 上站得住且离自己" +
                $"最近的点（{bestDist:F1}m，不问可达性；{reason}）", now);
            return true;
        }



        /// <summary>把新目标落进 brain 并打一条 Info（换目标是低频事件，不降频）。</summary>
        private void ApplyObjective(CsActor self, string routeMarker, Vector3 goal, bool isSite, string why, float now)
        {
            _planRoute = routeMarker;

            // 「槽位 ↔ 路线」必须一一对应：路线家族换了就跟着换槽位（判据见 CsBotPlans.SlotOwningRoute）。
            //   只在**分工家族**上做（出生点 / 守包这类集体目标返回 -1，槽位保持不动）。
            //   不做这一步的后果：兜底路径（TryLastResortObjective）把 bot 放进队友正走着的家族，
            //   而它的槽位还写着别的家族 ⇒ 槽位表 / 互斥门禁 / 撞车审计三处读到的都是错的。
            var owner = CsBotPlans.SlotOwningRoute(self.Team, routeMarker, _match.RoundNumber);
            if (owner >= 0 && owner != _planSlot)
            {
                Game.Logger.Info(Tag,
                    $"{_name}（{self.Team}）路线换成 '{routeMarker}'（家族拥有者 = 槽位 {owner}）" +
                    $"⇒ 槽位 {_planSlot} → {owner}（保持「槽位 ↔ 路线」一一对应）");
                _planSlot = owner;
            }

            _goalPos = goal;
            _goalValid = true;
            _goalIsSite = isSite;
            ArmGoal(self, now);

            Game.Logger.Info(Tag,
                $"{_name}（{self.Team}/{_difficulty}）重新选目标：{why} → 槽位={_planSlot} 路线={_planRoute}" +
                $" 首段路点={(HasFirstWaypoint ? FirstWaypoint.ToString() : "无")} " +
                $"目标={_goalPos} 站点={_goalIsSite} 路点={_nav.RemainingWaypoints} " +
                $"守点时长={ObjectiveHoldSeconds():F1}s（第 {_replanCount} 次换目标）");
        }

        //   路线/目标一律由 `CsBotPlans.For(team, slot, round)` 这张**4 槽位表**给（含巡逻线，4 条路互不相同），
        //   换目标走 `TryRouteObjective` 的槽位轮转。按 `slot` 0/1/2 映射到 3 条路（slot 2 = 中路、
        //   其余 = 两个包点）就是"每队必有一对 bot 同路同点"的直接来源（T: 槽位 0 与 1 都落主攻路；
        //   CT 由 `idx % 3` 使槽位 0 与 3 同路）。
        //   需要改路线选择时改 `CsBotPlans`，不要在这里复活三分支映射。



        /// <summary>
        /// 在候选点里挑第一个**走得到**的点（判据见下）。
        ///
        /// <para><b>为什么必须有这一层</b>：只判"可走"（<see cref="ICsMap.WalkableAt"/> /
        /// <see cref="ICsMap.CanStand"/>）不够 —— "可走"与"走得到"是两件事，只判前者就等于"挑了一个
        /// 求不出路径的点去追"，而 <c>EnsurePath</c> 失败后只会退化成直线走 ⇒ 顶着墙卡死。</para>
        ///
        /// <para><b>判据同源</b>：可达性只走 <see cref="BotNavigator.CanReach"/>（= 同一份
        /// <c>SnapToWalkable</c> + <c>WalkableCellHeightAware</c> + 引擎 <c>AStar.Find</c>），
        /// 不另写一套连通性算法（否则判据与被判对象会漂移）。</para>
        ///
        /// <para><b>调用顺序省算力</b>：先按距离过滤（&lt; minDistance / 不比自己好）**再**问可达 ——
        /// 于是越问越近，每轮最多问到的点数是"当前最优被刷新"的次数，且 <c>CanReach</c> 的
        /// 高度扩张按起点格缓存，同一次换目标里只算一次。</para>
        /// </summary>
        /// <param name="pts">候选点（地图标记点；null / 空 ⇒ false）</param>
        /// <param name="minDistance">水平距离下限（米）</param>
        /// <param name="farthest">false = 取"走得到的里面最近的"；true = 取最远的</param>
        /// <param name="avoidTeammates">true = 跳过"已被同队活体站着的点"（见 <see cref="TeammateOccupies"/>）。
        /// 默认 false ⇒ 既有调用点行为逐字不变；只有"最后一档兜底 / 退一档 fallback"这类
        /// **可能两只同时选点**的路径才传 true。</param>
        private bool TryPickReachable(CsActor self, Vector3[] pts, float minDistance, bool farthest,
                                      out Vector3 point, bool avoidTeammates = false)
        {
            point = Vector3.zero;
            if (pts == null || pts.Length == 0) return false;
            if (_map == null || !_map.IsLoaded) return false;

            var best = -1f;
            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.CanStand(pts[i])) continue;                  // 层①：本格站得住（与路线过滤同一口径）
                if (IsBannedGoal(pts[i])) continue;                    // 层①'：已判定走不到（记忆期内跳过）
                if (avoidTeammates && TeammateOccupies(self, pts[i])) continue;   // 层①''：队友正站在那儿

                var d = pts[i] - self.Position;
                d.y = 0f;
                var dist = d.magnitude;
                if (dist < minDistance) continue;
                if (best >= 0f && (farthest ? dist <= best : dist >= best)) continue;   // 已有个更好的候选，省掉这次可达查询
                if (!_nav.CanReach(self.Position, pts[i])) continue;   // 层②：真的走得到（口径与运行时同一份）

                best = dist;
                point = pts[i];
            }

            return best >= 0f;
        }



        /// <summary>
        /// 该点是否被**同队活体**占着 —— 只给"可能两只同时选点"的路径用（见 <see cref="TryPickReachable"/>）。
        ///
        /// <para><b>为什么需要</b>：路线标记点**没有序号互斥表**（<c>CsBotPlans.NextFreeGoalOrdinal</c>
        /// 只覆盖"目标标记"的计划序号），所以两只卡在同一角落时会选中**同一个最近路点**，然后互相挤开
        /// （<c>CsMatch.SeparateFromOtherActors</c>）并被 BotModule 记一对"路线撞车"。这里用"排除队友
        /// 已经站着的点"作为最小互斥 —— 不上序号表（那是给目标标记用的，路线标记点没有序号）。</para>
        ///
        /// <para><b>同层的口径用 <see cref="CsConst.StepUpHeight"/>(0.457) 而不是
        /// <see cref="CsConst.StandHeight"/>(1.80)</b>：后者在"队友站在正上一层、层高恰 1.8m"时
        /// 算出 <c>|Δy| = 1.8</c>、而判据是 <c>&gt; 1.8</c> ⇒ 会**判成同层**并误排该候选点；本图多层
        /// 上下重叠是常态、两个标记点垂直对齐（水平 &lt; 0.64m）并不罕见 ⇒ 一旦该家族只剩这一个候选，
        /// 就会被跳过（防抢点反而造出"原地不动"，比两只挤一下更糟）。`StepUpHeight` 的语义正是
        /// "同一落脚层"（抬腿带）：跨层最小落差 1.8m ≫ 0.457 ⇒ 不会误排，而同层的软地板/坡面误差
        /// （0.05~0.3m）仍会被正确排开。</para>
        /// </summary>
        private bool TeammateOccupies(CsActor self, Vector3 point)
        {
            var actors = _match.Actors;
            if (actors == null) return false;

            var minDist = CsConst.PlayerRadius * 2f;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == self.Id || a.Team != self.Team || !a.IsAlive) continue;

                var d = a.Position - point;
                d.y = 0f;
                if (d.magnitude >= minDist) continue;
                if (Mathf.Abs(a.Position.y - point.y) > CsConst.StepUpHeight) continue;
                return true;
            }
            return false;
        }



        /// <summary>取某个标记里"走得到且离自己最近"的一个点（包点/出生点用）。取不到时打一条带数字的 Warn。</summary>
        private bool TryPickReachablePoint(CsActor self, string marker, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null || !_map.IsLoaded) return false;

            var pts = _map.Points(marker);
            if (pts == null || pts.Length == 0) return false;
            if (TryPickReachable(self, pts, 0f, false, out point)) return true;

            RateWarn("reach.marker." + marker,
                $"{_name} 的标记 '{marker}' 有 {pts.Length} 个点，**没有一个走得到**" +
                $"（CanStand 过了但 BotNavigator.CanReach 全 false）→ 换别的目标。" +
                "判据：tools/probes/bu-goal-reachability.py");
            return false;
        }



        /// <summary>取某条路线的标记里"走得到且离自己最远"的那个点当路线终点（加门禁后的 RouteEnd）。</summary>
        private bool TryPickReachableRouteEnd(CsActor self, string routeMarker, out Vector3 point)
            => TryPickReachable(self, _map != null && _map.IsLoaded ? _map.Points(routeMarker) : null,
                0f, true, out point);



        /// <summary>取"离 <paramref name="from"/> 最远且可走"的一个标记点（<paramref name="minDistance"/> 以内的不要）。
        /// 为什么要可走：目标点本身在墙里 = 永远走不到 —— 顶着墙只会把卡住日志刷到天荒地老。
        /// </summary>
        private bool TryPickFarthestWalkable(Vector3[] pts, Vector3 from, float minDistance, out Vector3 point)
        {
            point = Vector3.zero;
            var best = -1f;

            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.WalkableAt(pts[i].x, pts[i].z)) continue;

                var d = pts[i] - from;
                d.y = 0f;
                var dist = d.magnitude;
                if (dist < minDistance) continue;
                if (dist <= best) continue;

                best = dist;
                point = pts[i];
            }

            return best >= 0f;
        }



        /// <summary>
        /// 按难度 <see cref="CsBotProfile.RepathInterval"/> 检查"朝目标有没有进展"：没进展就**用当前位置重排路线**
        /// （真正意义上的重新寻路）。只在"路线还有剩余路点"时重排 —— 路线已走完时重排等于掉头。
        /// 间隔 = Easy 1.6s / Normal 1.0s / Hard 0.5s：低难度反应慢，撞上障碍后愣得久。
        /// </summary>
        private void MaybeRepath(CsActor self, float now)
        {
            if (now < _nextRepathAt) return;
            _nextRepathAt = now + Mathf.Max(0.25f, _profile.RepathInterval);

            var d = _goalPos - self.Position;
            d.y = 0f;
            var dist = d.magnitude;
            var progress = _lastGoalDist - dist;
            _lastGoalDist = dist;

            if (progress >= CsBotConst.RepathProgressEpsilon)
            {
                _stallCount = 0;                                        // 有进展 → 什么都不做
                return;
            }

            // 连续这么多轮都"原地打转"才重排一次路线：偶尔一次慢（上坡/绕箱）不该触发重排，
            // 否则会把已经走顺的路线反复重排成"回头找最近路点"，看起来就是来回蹭。
            if (++_stallCount < CsBotConst.RepathStallStreak) return;
            _stallCount = 0;

            if (!_nav.HasRoute || _nav.RouteExhausted) return;          // 路线走完 → 交给"换目标"那条路
            if (string.IsNullOrEmpty(_planRoute)) return;

            // 到**最终目标**的直线距离没缩短，不代表没在走：绕建筑/上坡时它在沿路径前进。
            // 这时重求路径会用当前格重跑 A*，等价代价的两条分支会互换 ⇒ 按新路径掉头
            // （实测形态：同一路点上来回拉锯几米、整回合进不去包点）。
            if (_nav.PathHasProgress(self.Position)) return;

            var remainingBefore = _nav.RemainingWaypoints;              // 先取值：重求路径之后剩余路点会按新路径重算
            //   不能调 `SetRoute`：它会按"离**当前位置**最近优先"重排整条路线并把 `_index` 归零，
            //   于是"最近的路点"（很可能是刚走过、在身后的那个）排到最前 ⇒ 机器人掉头，
            //   与"原地重求路径、顺序与游标不动"这条契约相反。
            _nav.RefreshPathOnly(self.Position);
            RateWarn("repath." + _planRoute,
                $"{_name} 在 {_profile.RepathInterval:F2}s 内朝目标没有进展（距目标 {dist:F1}m，路线 '{_planRoute}'，" +
                $"重排前剩余路点 {remainingBefore}）→ 用当前位置**原地重求路径**（顺序与游标不动，⛔ 不重排路线）");
        }



        /// <summary>进行中的下包/拆包：保持按住（下包 = 左键，拆包 = E）、不移动（贴脸敌人时由调用方放行去交战）。</summary>
        private CsBotIntent ContinueUse(CsActor self, CsBotIntent intent, float now)
        {
            var planting = self.Team == CsTeam.T && self.HasBomb;
            var state = planting ? CsBotState.Plant : CsBotState.Defuse;

            SetState(state, now);
            intent.State = state;
            intent.Move = Vector3.zero;
            intent.Use = !planting;
            intent.Fire = planting;
            if (planting) intent.SwitchTo = CsWeapons.C4;

            if (now >= _lastUseLogAt)
            {
                _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                Game.Logger.Info(Tag,
                    $"{_name} 保持{(planting ? "下包（按住左键）" : "拆包（按住 E）")}中（进度 {self.UseProgress:P0}）：不移动");
            }

            intent.AimPoint = planting && self.HasBomb
                ? LookPoint(self, self.EyePosition + Forward(self.Yaw, -12f) * CsBotConst.AimFallbackDistance)
                : (_match.BombPlanted ? WithEyeHeight(_match.BombPosition) : LookAhead(self));
            return intent;
        }



        /// <summary>
        /// "这一 tick 不打架、先去拆包"（只对 CT 且已下包时成立）：
        /// ① 时间不够了（剩余 ≤ 拆包所需 + **走到 C4 的路上用时** + 余量）→ 无条件去拆；
        /// ② 时间还够 → 只有"眼前（<see cref="CsBotConst.DefuseOverFightRange"/> 内）就有敌人"才先打，
        ///    否则一律先冲包点（官方 CT 行为：下包即刻回防）。
        ///
        /// <para>①里必须算路上用时：只算拆包时间时，40m 外的 CT 会一直"先打"到引信只剩 12s 才动身，
        /// 到了包点引信已经烧完（实测：下包后 26s，最近的一名 CT 仍在 46m 外）。路上用时按
        /// <c>CsInventory.MovementSpeed</c>（模拟施加移动用的同一口径）与到 C4 的水平距离算。</para>
        /// </summary>
        private bool MustDefuseFirst(CsActor self, float now)
        {
            if (self.Team != CsTeam.CT || !_match.BombPlanted) return false;

            var need = self.HasDefuser ? CsConst.DefuseTimeWithKit : CsConst.DefuseTime;
            var left = _match.BombTimeLeft;

            var bomb = _match.BombPosition;
            var d = bomb - self.Position;
            d.y = 0f;
            var speed = Mathf.Max(1f, CsInventory.MovementSpeed(self));
            var travel = d.magnitude / speed;

            if (left >= 0f && left <= need + travel + CsBotConst.DefuseUrgencyMargin)
            {
                // 引信只够"跑过去 + 拆"⇒ 不再让远处交火抢走这几秒；这条日志就是"为什么此刻不打了"。
                if (CloverEngine.LogThrottle.ShouldLog("bot.defuse.rush:" + _name, 5f))
                {
                    Game.Logger.Info(Tag,
                        $"{_name} 剩余 {left:F1}s ≤ 拆包 {need:F1}s + 路上 {travel:F1}s（距 C4 {d.magnitude:F1}m，" +
                        $"移动速度 {speed:F2}m/s）+ 余量 {CsBotConst.DefuseUrgencyMargin:F0}s → 放弃交战，直冲 C4");
                }
                return true;
            }

            return !_visibleNow || _closestVisibleDist > CsBotConst.DefuseOverFightRange;
        }



        /// <summary>
        /// "这一 tick 不打架、先下包"（只对 **T 且持包** 成立；与 CT 的 <see cref="MustDefuseFirst"/> 对称）：
        /// 持包者**已经进了包点判定区**（离最近包点标记 ≤ <see cref="CsBotConst.SiteRadius"/>）⇒ 该下包了。
        ///
        /// <para><b>出处</b>：规格 <c>策划/策划案/CS1.6单机参考规格.md</c> §2.4 行为树第 130 行
        /// 「**T 持包到 B 点 → 下包**」—— 进了包点还在交战 = 把"下包"这条回合目标丢掉了。
        /// 判定半径与 <c>TryPlantOrPickup</c> / <c>CsBomb.IsInBombsite</c> **同口径**
        /// （<c>CsBotConst.SiteRadius</c> = <c>Module/Map/ICsMap.cs:83</c> 的 <c>CsMarkers.BombsiteRadius = 7m</c>），
        /// 不新增第二个半径。</para>
        ///
        /// <para>与 <see cref="ContinueUse"/> 的分工：那一条管"已经开始下包、别被抢走"，
        /// 这一条管"还没开始下包、站在包点里却还在打仗"。</para>
        /// </summary>
        private bool MustPlantFirst(CsActor self, float now)
        {
            if (self.Team != CsTeam.T || !self.HasBomb) return false;
            if (_match.BombPlanted) return false;                 // 已下过包 → 交给守包点
            if (EnemyNearPlant(self, now, out _, out _)) return false;   // 当人面不许下包

            // 站到下包点（同层包点标记的停步半径内）才算"该下包了"。
            // 只判包点判定球（≤ SiteRadius）会让 bot 在球面（= 包点边缘）就停住按 E。
            return AtPlantSpot(self);
        }



        /// <summary>
        /// 下包前的敌人闸门：敌人**可见**、或**刚丢视野（记忆期内）**且水平距离 ≤
        /// <see cref="CsBotConst.PlantEnemyAbortRange"/> ⇒ 不许下包。
        /// </summary>
        /// <param name="enemyDist">可见目标的水平距离（记忆期分支用最后所见位置）。</param>
        /// <param name="seenAge">距上次看见的秒数（可见时为 0）。</param>
        private bool EnemyNearPlant(CsActor self, float now, out float enemyDist, out float seenAge)
        {
            enemyDist = float.MaxValue;
            seenAge = float.MaxValue;

            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target == null || !target.IsAlive || target.Team == self.Team) return false;

            if (_visibleNow)
            {
                var d = target.Position - self.Position;
                d.y = 0f;
                enemyDist = d.magnitude;
                seenAge = 0f;
                return enemyDist <= CsBotConst.PlantEnemyAbortRange;
            }

            if (_lastSeenTime <= 0f) return false;
            seenAge = now - _lastSeenTime;
            if (seenAge > CsBotConst.PlantEnemyMemorySeconds) return false;

            var dp = _lastSeenPos - self.Position;
            dp.y = 0f;
            enemyDist = dp.magnitude;
            return enemyDist <= CsBotConst.PlantEnemyAbortRange;
        }



        /// <summary>是否已经站在下包点：同层包点标记的水平距离 ≤ 停步半径，且该点仍在包点判定球内。</summary>
        private bool AtPlantSpot(CsActor self)
        {
            if (!TryGetPlantAim(self, out var aim)) return false;

            NearestBombsitePoint(self.Position, out var zoneDist, out _);
            var d = aim - self.Position;
            d.y = 0f;
            return d.magnitude <= CsBotConst.PlantStopRadius && zoneDist <= CsBotConst.SiteRadius;
        }



        // ==================================================================
        //  炸弹任务
        // ==================================================================
        /// <summary>
        /// 炸弹任务（T 下包/捡包；CT 拆包）。<b><paramref name="intent"/> 必须 <c>ref</c></b> ——
        /// <see cref="CsBotIntent"/> 是 struct（值类型），按值传时本方法写入的 Move/State/Use 会在返回时丢掉
        /// </summary>
        private bool TryBombObjective(CsActor self, ref CsBotIntent intent, float now)
        {
            if (self.Team == CsTeam.CT) return TryDefuse(self, ref intent, now);
            if (self.Team == CsTeam.T) return TryPlantOrPickup(self, ref intent, now);
            return false;
        }



        /// <summary>CT 拆包分支（`intent` 必须 <c>ref</c>，理由同 <see cref="TryBombObjective"/>）。</summary>
        private bool TryDefuse(CsActor self, ref CsBotIntent intent, float now)
        {
            if (!_match.BombPlanted) return false;

            var bomb = _match.BombPosition;
            var d = bomb - self.Position;
            d.y = 0f;
            var dist = d.magnitude;
            var elected = IsElectedDefuser(self, now);

            // 拆包前先清点：当选者只在"没有看得见的敌人进到偏好交战距离内"时才按 E。
            // 视野里还有人在这个距离内 ⇒ 站着拆等于把几秒的引信送给对方（拆包期间不能移动、不能还击）。
            var enemyClose = _visibleNow && _closestVisibleDist <= PreferredRange();
            if (elected && enemyClose)
            {
                SetState(CsBotState.Engage, now);
                intent.State = CsBotState.Engage;
                intent.Move = dist > CsBotConst.DefuseStopRadius
                    ? _nav.ComputeMove(self.Position, bomb, CsBotConst.DefuseStopRadius, now,
                                       viaRoute: false, hullHeight: self.Height, forcePath: true)
                    : Vector3.zero;

                var seen = _lastSeenPos.sqrMagnitude > 0.0001f ? _lastSeenPos : bomb;
                ApplyAimAndFire(self, seen, now, ref intent);

                if (now >= _lastUseLogAt)
                {
                    _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"{_name} 是拆包人选但看得见敌人（{_closestVisibleDist:F1}m ≤ 偏好交战距离 {PreferredRange():F1}m）" +
                        $"→ 先清点再拆（距 C4 {dist:F1}m，剩余 {_match.BombTimeLeft:F1}s）");
                }
                return true;
            }

            if (elected && dist <= CsBotConst.DefuseStopRadius)
            {
                SetState(CsBotState.Defuse, now);
                intent.State = CsBotState.Defuse;
                intent.Move = Vector3.zero;
                intent.Use = true;
                intent.Fire = false;
                intent.AimPoint = WithEyeHeight(bomb);

                if (now >= _lastUseLogAt)
                {
                    _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"{_name} 到达 C4（距离 {dist:F2}m ≤ {CsBotConst.DefuseStopRadius:F1}m）→ 按住 E 拆包" +
                        $"（{(self.HasDefuser ? "有拆弹器" : "无拆弹器")}，剩余 {_match.BombTimeLeft:F1}s）");
                }
                return true;
            }

            // 掩护位：非当选者不贴到 C4 上（否则全队挤在爆点里，一颗雷/一串子弹一起收走，
            // 也没人看 T 的来路）。站到 SupportRadius 外，盯住来路。
            if (!elected)
            {
                SetState(CsBotState.Defuse, now);
                intent.State = CsBotState.Defuse;

                intent.Move = dist > CsBotConst.DefuseSupportRadius
                    ? _nav.ComputeMove(self.Position, bomb, CsBotConst.DefuseSupportRadius, now,
                                       viaRoute: false, hullHeight: self.Height, forcePath: true)
                    : Vector3.zero;

                var coverWatch = _visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f
                    ? _lastSeenPos
                    : WithEyeHeight(NearestWatchPoint(bomb, AttackRoutes));
                ApplyAimAndFire(self, coverWatch, now, ref intent);

                if (now >= _lastUseLogAt)
                {
                    _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"{_name} 不是拆包人选 → 在 C4 外 {CsBotConst.DefuseSupportRadius:F0}m 掩护（距 C4 {dist:F1}m），" +
                        $"盯 T 来路（剩余 {_match.BombTimeLeft:F1}s）");
                }
                return true;
            }

            // 路上若看得见敌人，就"边走边盯边打"：不停下（停下就到不了包点），但不放弃警戒。
            SetState(CsBotState.Defuse, now);
            intent.State = CsBotState.Defuse;
            // viaRoute=false：包已经下了，目标是"冲到 C4"而不是继续走自己那条旧防守路线。
            // 还在外围（> 掩护半径）时强制按 A* 路径走：直线被墙挡住时局部避障会在包点外来回，
            // 实测一名 CT 在离 C4 7m 处耗掉整个引信；贴到近处再交回局部避障（细调方向更灵）。
            intent.Move = _nav.ComputeMove(self.Position, bomb, CsBotConst.DefuseStopRadius, now,
                                           viaRoute: false, hullHeight: self.Height,
                                           forcePath: dist > CsBotConst.DefuseSupportRadius);
            // 正对 C4 这一帧被判"四面都走不了"（炸弹落在掩体/箱子上，A* 求不出该格、直线与全部
            // 偏角候选都被挡）时，绕到弹侧面再进 —— 原地不动等于把引信站到烧完。
            if (intent.Move.sqrMagnitude < 0.0004f && dist > CsBotConst.DefuseStopRadius + 0.3f)
            {
                intent.Move = SidestepTowardBomb(self, bomb, dist, now);
            }

            var watch = _visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f
                ? _lastSeenPos
                : WithEyeHeight(bomb);
            ApplyAimAndFire(self, watch, now, ref intent);
            return true;
        }



        /// <summary>
        /// 正对 C4 走不动时的兜底：围着炸弹取 8 个落脚点（半径 <see cref="CsBotConst.DefuseSidestepRadius"/>），
        /// 选"离自己最近、且导航层真能给出方向"的那一个当这一帧的目的地。
        ///
        /// <para>为什么需要：包点里的 C4 常落在箱子/掩体边上，导航层对**炸弹所在格**求不出路径 ⇒ 退回直线；
        /// 直线与 <c>Avoid</c> 的一圈偏角又全被 <c>WalkableAhead</c> 判死 ⇒ <c>ComputeMove</c> 返回零向量。
        /// 实测形态是 CT 停在离 C4 7.0m 不动直到引信烧完（<c>到达 C4</c> 日志一次都没出现）。</para>
        /// </summary>
        private Vector3 SidestepTowardBomb(CsActor self, Vector3 bomb, float dist, float now)
        {
            var best = Vector3.zero;
            var bestD = float.MaxValue;
            for (var i = 0; i < 8; i++)
            {
                var ang = i * Mathf.PI * 0.25f;
                var probe = bomb + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * CsBotConst.DefuseSidestepRadius;
                var d = probe - self.Position;
                d.y = 0f;
                var pd = d.magnitude;
                if (pd >= bestD) continue;

                var mv = _nav.ComputeMove(self.Position, probe, CsBotConst.DefuseStopRadius, now,
                                          viaRoute: false, hullHeight: self.Height, forcePath: true);
                if (mv.sqrMagnitude < 0.0004f) continue;
                bestD = pd;
                best = mv;
            }

            if (best.sqrMagnitude > 0.0004f && now >= _lastUseLogAt)
            {
                _lastUseLogAt = now + CsBotConst.StateLogMinInterval * 2f;
                Game.Logger.Info(Tag,
                    $"{_name} 直冲 C4 被挡（距 C4 {dist:F1}m）→ 绕侧面再进（取 {CsBotConst.DefuseSidestepRadius:F1}m 落脚点）");
            }
            return best;
        }



        /// <summary>
        /// 本人是否本轮唯一的拆弹人选 = **离 C4 最近的存活 CT**（并列取 Id 小者）。
        ///
        /// <para>为什么必须选举：全部存活 CT 都走"冲 C4 + 按 E"会让全队挤在爆点上 ——
        /// 既没人看 T 的来路，也会被同一颗手雷/同一串扫射一起收走。原版 CT 的形态是"一个人拆、其余人架枪"。</para>
        ///
        /// <para>缓存 <see cref="CsBotConst.DefuseElectInterval"/> 秒：C4 位置固定，选举只在"最近的人死掉"时变。</para>
        /// </summary>
        private bool IsElectedDefuser(CsActor self, float now)
        {
            if (self.Team != CsTeam.CT || !_match.BombPlanted) return false;
            if (_defuserElectAt > 0f && now < _defuserElectAt) return _defuserElected;

            var bomb = _match.BombPosition;
            var actors = _match.Actors;
            var bestId = 0L;
            var bestD2 = float.MaxValue;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != CsTeam.CT) continue;

                var dv = a.Position - bomb;
                dv.y = 0f;
                var d2 = dv.sqrMagnitude;
                if (d2 < bestD2 - 1e-4f || (Mathf.Abs(d2 - bestD2) <= 1e-4f && a.Id < bestId))
                {
                    bestD2 = d2;
                    bestId = a.Id;
                }
            }

            _defuserElected = bestId != 0L && bestId == self.Id;
            _defuserElectAt = now + CsBotConst.DefuseElectInterval;
            return _defuserElected;
        }



        /// <summary>
        /// 离 <paramref name="origin"/> 最近的、该组标记里的点（水平距离 ≤ <see cref="CsBotConst.GuardWatchRange"/>）。
        /// 取不到就返回 <paramref name="origin"/> 本身（保证调用方拿到的瞄点非零）。
        /// </summary>
        private Vector3 NearestWatchPoint(Vector3 origin, string[] markers)
        {
            if (_map == null || !_map.IsLoaded || markers == null) return origin;

            var best = float.MaxValue;
            var found = false;
            var result = origin;

            for (var m = 0; m < markers.Length; m++)
            {
                var pts = _map.Points(markers[m]);
                if (pts == null) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - origin;
                    d.y = 0f;
                    var dist = d.magnitude;
                    if (dist > CsBotConst.GuardWatchRange || dist >= best) continue;
                    best = dist;
                    result = pts[i];
                    found = true;
                }
            }

            return found ? result : origin;
        }



        /// <summary>
        /// 本轮计划不可用时退化为"全场离自己最近的包点标记"。
        ///
        /// <para>抽出来的唯一理由：<see cref="Engage"/> 的"持包压上"要用**同一份**取值
        /// （不是第二套口径）—— 它与 <see cref="TryPlantOrPickup"/> 下行处的 <c>aim</c> 是同一个算式，
        /// 那一边改成调用本方法，保证两边永远一致。</para>
        /// <returns>false = 地图未加载 / 没有可用的包点标记（调用方退回原行为）。</returns>
        /// </summary>
        private bool TryGetPlantAim(CsActor self, out Vector3 aim)
        {
            aim = Vector3.zero;
            if (self == null || _map == null || !_map.IsLoaded) return false;

            var near = NearestBombsitePoint(self.Position, out var zoneDist, out _);
            var dest = _goalValid ? _goalPos : near;

            // 本轮目标点本身就是包点里的标记点 ⇒ 下包点就用它（与计划一致）；
            // 目标点不在判定球里（路线终点 / 巡逻点）⇒ 退化为离它最近的包点标记。
            NearestBombsitePoint(dest, out var destSiteDist, out _);
            var anchor = destSiteDist <= CsBotConst.SiteRadius
                ? dest
                : NearestBombsitePoint(dest, out _, out _);

            aim = ResolvePlantPoint(self.Position, anchor, zoneDist <= CsBotConst.SiteRadius);
            return aim.sqrMagnitude > 0.0001f;
        }



        /// <summary>
        /// 下包点：**在 <paramref name="anchor"/> 所属的那个包点里**取离它最近、且（<paramref name="sameFloor"/>
        /// 为真时）与自身同层的标记点。
        /// <para>只在该包点内选 —— 跨包点最近会选中对面的点（站在 B 却把下包点定到 A）。
        /// 同层过滤只在"已经走到包点附近"时才生效：从出生点看，包点标记都在脚下，楼层差没有意义。</para>
        /// <para>没有同层点时退回该包点里离 <paramref name="anchor"/> 最近的标记点（不比改动前更差）。</para>
        /// </summary>
        private Vector3 ResolvePlantPoint(Vector3 selfPos, Vector3 anchor, bool sameFloor)
        {
            var siteMarker = (string)null;
            var bestAnchorDist = float.MaxValue;

            for (var s = 0; s < 2; s++)
            {
                var marker = s == 0 ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
                var pts = _map.Points(marker);
                if (pts == null || pts.Length == 0) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - anchor;
                    d.y = 0f;
                    var len = d.magnitude;
                    if (len < bestAnchorDist)
                    {
                        bestAnchorDist = len;
                        siteMarker = marker;
                    }
                }
            }

            if (siteMarker == null) return anchor;
            var sitePts = _map.Points(siteMarker);
            if (sitePts == null || sitePts.Length == 0) return anchor;

            var best = anchor;
            var bestDist = float.MaxValue;
            var found = false;

            for (var pass = 0; pass < 2; pass++)
            {
                var requireFloor = sameFloor && pass == 0;
                for (var i = 0; i < sitePts.Length; i++)
                {
                    if (requireFloor && Mathf.Abs(sitePts[i].y - selfPos.y) > CsBotConst.PlantSameFloorHeight) continue;
                    if (IsBannedGoal(sitePts[i])) continue;

                    var d = sitePts[i] - anchor;
                    d.y = 0f;
                    var len = d.magnitude;
                    if (len >= bestDist) continue;

                    bestDist = len;
                    best = sitePts[i];
                    found = true;
                }

                if (found) return best;
            }

            return anchor;
        }



        /// <summary>T 下包 / 捡包分支（`intent` 必须 <c>ref</c>，理由同 <see cref="TryBombObjective"/>）。</summary>
        private bool TryPlantOrPickup(CsActor self, ref CsBotIntent intent, float now)
        {
            if (_match.BombPlanted) return false;   // 包已下 → 交给 Advance 守包点

            if (self.HasBomb)
            {
                //   （与 CsBomb.IsInBombsite 同口径，见 NearestBombsitePoint 注释）。
                //   停步半径不能取 SiteRadius（= 判定半径）：那会让机器人正好停在判定球面上，
                //      CanPlant 的 IsInBombsite 随浮点误差真假 ⇒ 表现为"到了又走了"。
                var near = NearestBombsitePoint(self.Position, out var zoneDist, out var zoneName);

                if (!_goalValid)
                {
                    RateWarn("plant.nogoal",
                        $"{_name} 拿着 C4 但没有可用包点标记（本轮计划无目标）→ 只能朝最近的包点标记走");
                }

                if (!TryGetPlantAim(self, out var aim)) aim = near;

                var toAim = aim - self.Position;
                toAim.y = 0f;
                var atSpot = toAim.magnitude <= CsBotConst.PlantStopRadius && zoneDist <= CsBotConst.SiteRadius;

                if (atSpot)
                {
                    // 敌人看得见 / 刚丢视野且离得近 ⇒ 不许当着人下包（先交给交战分支）。
                    if (EnemyNearPlant(self, now, out var enemyDist, out var enemyAge))
                    {
                        if (now >= _lastPlantGateLogAt)
                        {
                            _lastPlantGateLogAt = now + CsBotConst.StateLogMinInterval;
                            Game.Logger.Info(Tag,
                                $"[C4] {_name} 放弃下包：{(enemyAge <= 0f ? "看得见敌人" : $"距上次看见敌人 {enemyAge:F1}s")} " +
                                $"水平距离 {enemyDist:F1}m ≤ {CsBotConst.PlantEnemyAbortRange:F0}m → 先应战");
                        }
                        return false;
                    }

                    SetState(CsBotState.Plant, now);
                    intent.State = CsBotState.Plant;
                    intent.Move = Vector3.zero;   // CanPlant 要求"停下"（CsBomb.IsMoving ⇒ 不许下包）
                    intent.SwitchTo = CsWeapons.C4;   // 下包前先切出 C4（CanPlant 要求手持 C4）
                    intent.Use = false;
                    intent.Fire = true;               // 按住左键 = 下包
                    intent.AimPoint = WithEyeHeight(aim);

                    if (now >= _lastUseLogAt)
                    {
                        _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                        Game.Logger.Info(Tag,
                            $"[C4] {_name} 已站到下包点 {zoneName}（距同层包点标记 {toAim.magnitude:F2}m ≤ " +
                            $"{CsBotConst.PlantStopRadius:F1}m，判定球内 {zoneDist:F2}m ≤ {CsBotConst.SiteRadius:F0}m）" +
                            $"→ 下包决策：切出 C4 + 停住 + 按住左键（需 {CsConst.PlantTime:F1}s）");
                    }
                    return true;
                }

                // 还没站到下包点（包括"进了判定球但只在球面附近"）：继续走进去；站到点前先切出 C4，
                // 免得到位那一帧手里还是步枪（CanPlant 会判"未切出 C4"）。

                SetState(CsBotState.Plant, now);
                intent.State = CsBotState.Plant;
                intent.SwitchTo = CsWeapons.C4;
                intent.Move = _nav.ComputeMove(self.Position, aim, CsBotConst.PlantStopRadius, now,
                                               hullHeight: self.Height);
                intent.AimPoint = LookPoint(self, WithEyeHeight(_nav.CurrentTarget(self.Position, aim)));

                // 求出的方向是零向量 = 本帧不动。两种情况：
                //   ① 已经到了停步半径（合法，交回调用方按"到了"处理）；
                //   ② **目标点与自身重合**（包点标记缺失时 NearestBombsitePoint 返回 pos 本身，
                //      TryGetPlantAim 也退化成 pos）⇒ 停在 Plant 态一动不动、下包永远起不来。
                // ② 必须留痕并交回推进分支（路线推进 + 卡住自恢复都在那里），不许静默冻结。
                if (intent.Move.sqrMagnitude < 0.0004f)
                {
                    var dAim = aim - self.Position;
                    dAim.y = 0f;
                    if (dAim.magnitude < CsBotConst.PlantStopRadius)
                    {
                        if (now >= _lastUseLogAt)
                        {
                            _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                            Game.Logger.Warn(Tag,
                                $"[C4] {_name} 持包但本帧求不出可走方向：下包目标 ({aim.x:F1},{aim.z:F1}) " +
                                $"与自身距离 {dAim.magnitude:F2}m ≤ 停步半径 {CsBotConst.PlantStopRadius:F1}m，" +
                                $"而离最近包点标记 {zoneDist:F2}m（判定半径 {CsBotConst.SiteRadius:F0}m）" +
                                "（标记缺失或目标坐标退化）→ 本帧交回推进分支，不停在原地");
                        }
                        return false;
                    }
                }

                // 持包推进是每 tick 都会走到的常态分支：按时间闸留一条现场，不逐 tick 打印。
                if (CloverEngine.LogThrottle.ShouldLog("bot.c4.charge:" + _name, 30f))
                {
                    Game.Logger.Info(Tag,
                        $"[C4] {_name} 携带 C4 走向下包点：目标={aim} 距目标 {toAim.magnitude:F2}m " +
                        $"（停步半径 {CsBotConst.PlantStopRadius:F1}m，最近包点标记 {zoneDist:F2}m）");
                }
                return true;
            }

            // 持包者阵亡 → 包掉在地上：**只派离 C4 最近的那一个 T** 去捡（其余人继续交战/推进，不许全队扑向同一个点）。
            //
            //   ① 被选中的人**往往不是离 C4 最近的**（捡包要多走很多路，容易在途中被打死）；
            //   ② 叠加"交战中直接走 Engage"的路径（见 CommitTacticalDecision 的新分支）⇒ 最近的 T 若编号不整除，
            //   现在准入量法 = 选举（全场**唯一**一人为真），与 `CommitTacticalDecision` 用的是同一个判据。
            if (!IsElectedBombHunter(self)) return false;
            if (AnyTCarriesBomb()) return false;

            var dropped = _match.BombPosition;
            if (dropped.sqrMagnitude < 1f) return false;   // 还没人拿过包 → 位置无意义（契约：未下包时返回 LastKnownPosition）

            var dd = dropped - self.Position;
            dd.y = 0f;
            if (dd.magnitude <= CsBotConst.PickupStopRadius)
            {
                intent.State = CsBotState.Patrol;
                intent.Move = Vector3.zero;
                intent.AimPoint = WithEyeHeight(dropped);
                return true;    // 模拟会在 1.2m 内自动拾取
            }

            // 捡包者卡死（一直走不到 / 距离不缩）⇒ 让位给下一个离 C4 最近的 T，自己回去交战/推进。
            if (PickupHunterStalled(self, now, dd.magnitude)) return false;

            intent.State = CsBotState.Patrol;
            //   走 A* 路径而不是"朝落点直线 + 局部避障"：落点常在墙后几十米，直线恒被挡，
            //   避障只能在原地绕；C4 是静止目标，没有"边打边走"的必要。
            intent.Move = _nav.ComputeMove(self.Position, dropped, CsBotConst.PickupStopRadius, now,
                                           viaRoute: false, hullHeight: self.Height, forcePath: true);
            intent.AimPoint = WithEyeHeight(dropped);

            // 每个 bot 5s 最多一条：捡包是每 tick 都走到的常态分支，逐次打印只会刷屏。
            if (CloverEngine.LogThrottle.ShouldLog("bot.c4.pickup:" + _name, 5f))
            {
                Game.Logger.Info(Tag,
                    $"[C4] {_name} 去捡掉落的 C4：到落点 {dd.magnitude:F2}m（本人是场上离 C4 最近的 T，其余 T 继续交战）");
            }
            return true;
        }



        private bool AnyTCarriesBomb()
        {
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive) continue;
                if (a.Team != CsTeam.T) continue;
                if (a.HasBomb) return true;
            }
            return false;
        }



        /// <summary>
        ///
        /// <para>三条同时成立才为真：</para>
        /// <list type="number">
        /// <item>本人在世、本方为 T；</item>
        /// <item>场上**确实没有任何 T 持 C4**，且 C4 **掉在地上**：判据 = <c>BombPlanted == false</c>
        /// 且 <c>BombPosition</c> 有意义。这里用的是**现有 API 的既有语义**、不新增状态：
        /// <c>ICsMatch.BombPosition</c> 在未安放时返回 <c>CsBomb.LastKnownPosition</c>，而
        /// <c>CsBomb.OnCarrierLost</c> 正是把**掉落点**写进 LastKnownPosition（且 <c>CsBomb.Reset</c>
        /// 每回合把它清零，<c>sqrMagnitude &lt; 1</c> 即"本回合还没人拿过包"）；
        /// "有没有人持包" = <see cref="AnyTCarriesBomb"/>（与 <c>CsBomb.CarrierId</c> 同一事实的两面）；</item>
        /// <item>自己是**所有在世的 T **bot** 里离 C4（水平距离）最近的那一个，并列时取
        /// <see cref="CsActor.Id"/> 小的 —— 唯一且确定。</item>
        /// </list>
        ///
        /// <para>为什么选举范围只算 bot（<c>a.IsBot</c>）：本方法是 bot 的决策器，只能分配 bot 的行为；
        /// 把本地人类选手算进选举，会出现"最近的是人类 ⇒ 全体 bot 都不去捡"——
        /// 那等于把这条分支再次变成"没人捡"。人类若自己捡起，<see cref="AnyTCarriesBomb"/> 立刻为真，bot 自动让位。</para>
        ///
        /// <para>本方法不改变"谁能不能捡包"：真正的拾取仍由模拟每帧的
        /// <c>CsBomb.TryPickupDropped</c>（1.2m 内自动拾取）完成，本方法只决定"**谁**该往掉落点走"。</para>
        /// </summary>
        private bool IsElectedBombHunter(CsActor self)
        {
            if (self == null || !self.IsAlive) return false;
            if (self.Team != CsTeam.T) return false;
            if (_match.BombPlanted) return false;

            var dropped = _match.BombPosition;
            if (dropped.sqrMagnitude < 1f) return false;   // 本回合还没人拿过包 → 位置无意义
            if (AnyTCarriesBomb()) return false;           // 有人拿着（含自己刚捡起）→ 掉落分支不成立

            SyncPickupBanRound();

            // 弃权者本回合**恒定落选**；唯一的例外 = 所有在世 T bot 都已弃权（否则没人捡包），
            // 此时最近者隔一个完整的捡包窗口再试 —— 不设这个间隔就会每秒把同一人重新选回来，
            // 表现是"放弃 → 1s 后去捡 → 再放弃"的抖动与刷屏。
            if (_pickupBanned.Contains(self.Id))
            {
                // 回落只在"所有在世的 T bot 都已弃权"时成立：此时最近者继续前往（不再判弃权、
                // 不再打第二条 Warn），否则谁都不去 = 整回合没人捡包。
                if (!AllAliveTBotsBanned()) return false;
                return NearestAliveTBotIs(self.Id, includeBanned: true);
            }

            return NearestAliveTBotIs(self.Id, includeBanned: false);
        }



        /// <summary>把弃权名单同步到当前回合（回合号变化 ⇒ 清空；所有 brain 共用同一份）。</summary>
        private void SyncPickupBanRound()
        {
            if (ReferenceEquals(_pickupBanMatch, _match) && _pickupBanRound == _match.RoundNumber) return;
            _pickupBanMatch = _match;
            _pickupBanRound = _match.RoundNumber;
            _pickupBanned.Clear();
        }



        /// <summary>在世的 T bot 是否**全部**已在弃权名单里（没有任何一只时返回 false）。</summary>
        private bool AllAliveTBotsBanned()
        {
            var actors = _match.Actors;
            var any = false;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot || a.Team != CsTeam.T) continue;
                any = true;
                if (!_pickupBanned.Contains(a.Id)) return false;
            }
            return any;
        }



        /// <summary>离 C4 最近的在世 T bot 是不是 <paramref name="selfId"/>（并列取 Id 小者，唯一且确定）。</summary>
        private bool NearestAliveTBotIs(long selfId, bool includeBanned)
        {
            var dropped = _match.BombPosition;
            var actors = _match.Actors;
            var bestId = 0L;
            var bestD2 = float.MaxValue;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot || a.Team != CsTeam.T) continue;
                if (!includeBanned && _pickupBanned.Contains(a.Id)) continue;

                var dv = a.Position - dropped;
                dv.y = 0f;
                var d2 = dv.sqrMagnitude;
                if (d2 < bestD2 - 1e-4f || (Mathf.Abs(d2 - bestD2) <= 1e-4f && a.Id < bestId))
                {
                    bestD2 = d2;
                    bestId = a.Id;
                }
            }

            return bestId != 0L && bestId == selfId;
        }



        /// <summary>除自己以外，是否还有在世的 T bot 离 C4 比 <paramref name="maxDist"/> 更近。</summary>
        private bool AnyOtherAliveTBotCloserThan(long selfId, float maxDist)
        {
            var dropped = _match.BombPosition;
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot || a.Team != CsTeam.T || a.Id == selfId) continue;

                var dv = a.Position - dropped;
                dv.y = 0f;
                if (dv.magnitude < maxDist) return true;
            }

            return false;
        }



        /// <summary>
        /// 捡包者是否应该放弃：连续 <see cref="CsBotConst.PickupGiveUpSeconds"/> 秒到落点距离没有
        /// 缩短 <see cref="CsBotConst.PickupProgressMeters"/> 米以上，或导航口径走不到落点。
        /// <para>放弃后把本 bot 记入**本回合**的弃权名单（全队所有 brain 共用同一份选择结果 ⇒
        /// 下一次选举必然换到下一个离 C4 最近的 T），自己回到交战/推进。</para>
        /// </summary>
        private bool PickupHunterStalled(CsActor self, float now, float dist)
        {
            // 本回合已经弃过权（只可能是"全员弃权 → 最近者再试"这一条回落）：不再判"走不到/不收敛"，
            // 也不再打第二条 Warn —— 弃权结论在本回合内不变。
            SyncPickupBanRound();
            if (_pickupBanned.Contains(self.Id)) return false;

            // 中间隔了别的分支（交战）⇒ 计时作废，只统计"这连续一段真的在往落点走"的时间。
            if (_pickupLastTick > 0f && now - _pickupLastTick > 2f) _pickupSince = 0f;
            _pickupLastTick = now;

            if (_pickupSince <= 0f)
            {
                _pickupSince = now;
                _pickupBestDist = dist;
                _pickupKeepCount = 0;
                _nextPickupReachCheckAt = now + CsBotConst.PickupReachCheckInterval;
                return false;
            }

            if (dist < _pickupBestDist - CsBotConst.PickupProgressMeters)
            {
                _pickupBestDist = dist;
                _pickupSince = now;
            }

            string reason = null;
            if (now >= _nextPickupReachCheckAt)
            {
                _nextPickupReachCheckAt = now + CsBotConst.PickupReachCheckInterval;
                if (!_nav.CanReach(self.Position, _match.BombPosition))
                    reason = ReachFailReason;
            }

            var stalledFor = now - _pickupSince;
            if (reason == null && stalledFor < CsBotConst.PickupGiveUpSeconds) return false;
            if (reason == null)
                reason = $"{stalledFor:F1}s 内到落点距离没有缩短 {CsBotConst.PickupProgressMeters:F1}m";

            // 换人只在"确实有人更近"时做：走不动的人 40m、下一个候选 48m 时换人等于把同一个回合
            // 交给更远的人重走一遍，本回合再没人能捡到包。
            // 兜底上限：连续两个窗口都没进展、且一直没人更近 ⇒ 第三个窗口起不再钉在原地
            // （否则落点真的走不到时，这只 T 会整回合丢掉交战与下包，只为追一个捡不到的包）。
            if (reason != ReachFailReason && _pickupKeepCount < CsBotConst.PickupKeepWindows &&
                !AnyOtherAliveTBotCloserThan(self.Id, dist - CsBotConst.PickupHandoffMinGain))
            {
                _pickupKeepCount++;
                _pickupSince = now;
                _pickupBestDist = dist;
                RateWarn("pickup.keep",
                    $"[C4] {_name} 捡包 {stalledFor:F1}s 无进展（{reason}），但场上没有更近的活 T" +
                    $"（本人距落点 {dist:F1}m，换人门槛 {CsBotConst.PickupHandoffMinGain:F0}m）→ 本人继续去捡" +
                    $"（第 {_pickupKeepCount}/{CsBotConst.PickupKeepWindows} 次）");
                return false;
            }

            _pickupSince = 0f;
            _pickupBestDist = float.MaxValue;
            _pickupBanned.Add(self.Id);

            Game.Logger.Warn(Tag,
                $"[C4] {_name} 放弃捡包：{reason}（当前距落点 {dist:F1}m）→ 换下一个离 C4 最近的 T，" +
                "自己回交战/推进（本回合不再参与捡包）");
            return true;
        }



        // ==================================================================
        //  搜点与听声调查
        // ==================================================================
        //
        // 出处：原版 cs_bot.h:65 HuntState → 去最后已知敌人位置搜索；
        //       cs_bot.h:118 InvestigateNoiseState → 听到枪声/脚步去查看；
        //       cs_bot.h:436 TaskType::MOVE_TO_LAST_KNOWN_ENEMY_POSITION。
        //
        // 两个动作都只在"没有可见敌人、且不在记忆追击里"时接管本 tick（树上挂在交战之后、
        // 炸弹任务之前），下包/拆包/持包推进期间一律不接管。

        /// <summary>最后已知位置在短时记忆过期后的保留时长（秒）。</summary>
        private const float LastKnownRetainSeconds = 15f;

        /// <summary>去最后已知位置的赶路上限（秒）：超过就放弃，交回任务层。</summary>
        private const float LastKnownApproachSeconds = 12f;

        /// <summary>到"最后已知位置"算到达的水平距离（米）。</summary>
        private const float SearchArriveRadius = 2f;

        /// <summary>声源在这么近时不再"走过去查"（人就在跟前，交由交战 / 守点朝向处理）。</summary>
        private const float NoiseSkipRadius = 5f;

        /// <summary>听声调查的到位半径（米）：走到声源 2m 内确认（仍在 5m 查看范围内）。</summary>
        private const float NoiseArriveRadius = 2f;

        /// <summary>听声调查的赶路上限（秒）。</summary>
        private const float NoiseApproachSeconds = 10f;

        /// <summary>8 方向扫视半径（米）。</summary>
        private const float SearchSweepRadius = 6f;

        /// <summary>扫视时每个方向的停留时长（秒）。</summary>
        private const float SearchStepSeconds = 0.6f;

        /// <summary>一轮 8 方向扫视的总时长上限（秒）：扫完全部方向或到时即结束。</summary>
        private const float SearchMaxSeconds = 6f;

        /// <summary>听声调查到达后原地扫视的总时长（秒）。</summary>
        private const float NoiseSweepSeconds = 1.5f;

        /// <summary>两次听声调查之间的最小间隔（秒）：不被枪声牵着跑。</summary>
        private const float NoiseInvestigateCooldown = 12f;

        /// <summary>听见声响后允许据以出发的最长延迟（秒）。</summary>
        private const float NoiseReactionSeconds = 2f;

        private enum BotSearchKind
        {
            None = 0,
            LastKnown = 1,
            Noise = 2,
        }

        private BotSearchKind _searchKind;

        /// <summary>本次搜索的目的地（最后已知位置 / 声源）。</summary>
        private Vector3 _searchGoal;

        private float _searchStartedAt;

        /// <summary>赶路阶段的截止时刻。</summary>
        private float _searchDeadline;

        /// <summary>已经到达目的地、进入扫视阶段。</summary>
        private bool _searchArrived;

        private float _sweepStartedAt;

        /// <summary>当前方向的时隙截止时刻。</summary>
        private float _sweepDirUntil;

        /// <summary>8 方向扫视顺序（优先没搜过的方向）。</summary>
        private readonly int[] _sweepOrder = new int[8];

        private int _sweepOrderPos;

        private int _sweepOrderCount;

        /// <summary>本轮扫视走过的方向数（日志用）。</summary>
        private int _sweepSearched;

        /// <summary>扫视中心（到达目的地时的站位）与基准朝向（8 方向由此旋转）。</summary>
        private Vector3 _sweepCenter;

        private Vector3 _sweepForward = Vector3.forward;

        /// <summary>本 bot 本回合搜过的格（<see cref="BotNavigator.CellKeyOf"/> 打包键）。</summary>
        private readonly HashSet<long> _searchedCells = new HashSet<long>();

        /// <summary>已经去搜过的那一次"最后所见"时刻（同一时刻不重复出发）。</summary>
        private float _lastKnownSearchedSeenTime = -1f;

        /// <summary>下一次允许听声调查的时刻。</summary>
        private float _nextNoiseInvestigateAt;

        /// <summary>最近一次听到的声源（探针 / 日志只读）。</summary>
        private Vector3 _noisePos;

        /// <summary>队级共享的"已搜格"（只读参考：自己搜过的格子，队友也读得到）。</summary>
        private static ICsMatch _searchShareMatch;

        private static int _searchShareRound = int.MinValue;

        private static readonly HashSet<long>[] _searchSharedCells =
        {
            new HashSet<long>(),      // T
            new HashSet<long>(),      // CT
        };

        /// <summary>当前搜索种类（探针只读：0 = 无 / 1 = 去最后已知位置 / 2 = 听声调查）。</summary>
        public int SearchKindForProbe => (int)_searchKind;

        /// <summary>当前搜索目的地（探针只读）。</summary>
        public Vector3 SearchGoalForProbe => _searchGoal;

        /// <summary>本回合本 bot 已搜过的格数（探针只读）。</summary>
        public int SearchedCellCountForProbe => _searchedCells.Count;

        /// <summary>
        /// 本 tick 尝试接管"去最后已知位置 / 听声调查"。
        /// 返回 true = 本 tick 的移动与瞄点已经写进 <c>_tickIntent</c>。
        /// </summary>
        private bool TrySearchStep(float now)
        {
            var self = _tickSelf;
            if (self == null || !self.IsAlive) return false;

            if (SearchBlockedByObjective(self))
            {
                AbortSearch(now, "任务层优先（下包 / 拆包 / 持包推进）");
                return false;
            }

            // 交战分支先于本节点执行；走到这里还带着"有目标"的搜索 = 上一次搜索被交战打断。
            if (_visibleNow || _targetId != 0)
            {
                AbortSearch(now, "出现交战目标");
                return false;
            }

            if (_searchKind == BotSearchKind.None && !TryBeginSearch(self, now)) return false;
            return ContinueSearch(self, ref _tickIntent, now);
        }


        /// <summary>下包 / 拆包 / 持包下包期间不搜点、不调查。</summary>
        private bool SearchBlockedByObjective(CsActor self)
        {
            if (self.UseProgress >= 0f) return true;                        // E 正在按（下包 / 拆包）
            if (self.Team == CsTeam.CT && _match.BombPlanted) return true;  // C4 已下：拆包优先
            if (self.Team == CsTeam.T && self.HasBomb && !_match.BombPlanted) return true;  // 持包推进下包
            return false;
        }


        /// <summary>按"听声调查优先于搜点"的顺序决定是否出发；返回 true = 已开始一次搜索。</summary>
        private bool TryBeginSearch(CsActor self, float now)
        {
            // ① 听声调查：枪声 / 脚步，听见后 2s 内出发，每 12s 最多一次。
            //    声源已在查看半径内时不出发 —— 此时人就在跟前，走过去的位移为零，
            //    看一眼由交战 / 守点的朝向逻辑负责；只有"需要走过去"的声响才值得打断当前任务。
            if (_heardFresh && now - _heardTime <= NoiseReactionSeconds && now >= _nextNoiseInvestigateAt &&
                _heardDistance > NoiseSkipRadius)
            {
                _noisePos = _heardPos;
                _nextNoiseInvestigateAt = now + NoiseInvestigateCooldown;
                BeginSearch(BotSearchKind.Noise, _noisePos, now, NoiseApproachSeconds);

                Game.Logger.Info(Tag,
                    $"{_name}（{_difficulty}）听声调查 heard={(_heardGunshot ? "gun" : "step")} " +
                    $"dist={_heardDistance:F1}m 声源=({_noisePos.x:F1},{_noisePos.z:F1}) " +
                    $"距响声 {now - _heardTime:F2}s ≤ {NoiseReactionSeconds:F0}s → 去 {NoiseArriveRadius:F0}m 内查看" +
                    $"（本 bot {NoiseInvestigateCooldown:F0}s 内最多一次）");
                return true;
            }

            // ② 去最后已知位置：短时记忆已过期（记忆期内由交战分支追击），最后所见仍在保留期内。
            if (_lastSeenTime <= 0f || _lastSeenPos.sqrMagnitude < 0.0001f) return false;

            var age = now - _lastSeenTime;
            if (age <= CsBotConst.TargetMemorySeconds) return false;
            if (age > LastKnownRetainSeconds) return false;
            if (Mathf.Abs(_lastKnownSearchedSeenTime - _lastSeenTime) < 0.001f) return false;

            var flat = _lastSeenPos - self.Position;
            flat.y = 0f;

            BeginSearch(BotSearchKind.LastKnown, _lastSeenPos, now, LastKnownApproachSeconds);

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）去最后已知位置 ({_lastSeenPos.x:F1},{_lastSeenPos.y:F1},{_lastSeenPos.z:F1}) " +
                $"age={age:F2}s（短时记忆 {CsBotConst.TargetMemorySeconds:F1}s 已过期，最后已知保留 {LastKnownRetainSeconds:F0}s）" +
                $"距 {flat.magnitude:F1}m → 到达 {SearchArriveRadius:F0}m 内做 {SearchSweepRadius:F0}m 8 方向扫视");
            return true;
        }


        private void BeginSearch(BotSearchKind kind, Vector3 goal, float now, float approachSeconds)
        {
            _searchKind = kind;
            _searchGoal = goal;
            _searchStartedAt = now;
            _searchDeadline = now + approachSeconds;
            _searchArrived = false;
            _sweepStartedAt = 0f;
            _sweepDirUntil = 0f;
            _sweepOrderPos = 0;
            _sweepOrderCount = 0;
            _sweepSearched = 0;
        }


        /// <summary>赶路 → 到达 → 扫视。返回 false = 本次搜索已结束（交回任务层）。</summary>
        private bool ContinueSearch(CsActor self, ref CsBotIntent intent, float now)
        {
            var arrive = _searchKind == BotSearchKind.Noise ? NoiseArriveRadius : SearchArriveRadius;

            if (!_searchArrived)
            {
                if (now >= _searchDeadline)
                {
                    EndSearch(self, now, "赶路超时");
                    return false;
                }

                if (BotNavigator.Arrived(self.Position, _searchGoal, arrive))
                {
                    BeginSweep(self, now);
                }
                else
                {
                    SetState(CsBotState.Patrol, now);
                    intent.State = CsBotState.Patrol;
                    intent.Move = _nav.ComputeMove(self.Position, _searchGoal, arrive, now,
                                                   viaRoute: false, hullHeight: self.Height);
                    intent.AimPoint = WithEyeHeight(_searchGoal);
                    return true;
                }
            }

            return SweepStep(self, ref intent, now);
        }


        private void BeginSweep(CsActor self, float now)
        {
            _searchArrived = true;
            _sweepStartedAt = now;
            _sweepCenter = self.Position;
            _sweepForward = Forward(self.Yaw, 0f);
            _sweepOrderPos = 0;
            _sweepSearched = 0;

            // 8 方向 = 基准朝向每 45° 一个；顺序上把"没搜过的格"排前面。
            _sweepOrderCount = 8;
            for (var i = 0; i < _sweepOrderCount; i++) _sweepOrder[i] = i;
            for (var i = 1; i < _sweepOrderCount; i++)
            {
                var cur = _sweepOrder[i];
                var curSearched = CellSearched(SweepPoint(cur), self.Team) ? 1 : 0;
                var j = i - 1;
                while (j >= 0 && (CellSearched(SweepPoint(_sweepOrder[j]), self.Team) ? 1 : 0) > curSearched)
                {
                    _sweepOrder[j + 1] = _sweepOrder[j];
                    j--;
                }
                _sweepOrder[j + 1] = cur;
            }

            var step = StepSeconds();
            _sweepDirUntil = now + step;

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）{KindLabel()}到达 ({self.Position.x:F1},{self.Position.z:F1}) " +
                $"→ 开始扫视（{SearchSweepRadius:F0}m × 8 方向，{(_searchKind == BotSearchKind.Noise ? NoiseSweepSeconds : SearchMaxSeconds):F1}s）");
        }


        private bool SweepStep(CsActor self, ref CsBotIntent intent, float now)
        {
            var step = StepSeconds();

            // 时隙推进：一个方向看满 step 秒就记格子换下一个；8 个方向走完即结束。
            while (now >= _sweepDirUntil)
            {
                MarkSweptCell(self);
                _sweepOrderPos++;
                if (_sweepOrderPos >= _sweepOrderCount)
                {
                    EndSearch(self, now, "8 方向已扫完");
                    return false;
                }
                _sweepDirUntil += step;
            }

            // 最后已知位置：扫视超过 6s 直接结束（不无限转圈）。
            if (_searchKind == BotSearchKind.LastKnown && now - _sweepStartedAt >= SearchMaxSeconds)
            {
                EndSearch(self, now, $"扫视 {SearchMaxSeconds:F0}s 超时");
                return false;
            }

            var point = SweepPoint(_sweepOrder[_sweepOrderPos]);
            var canWalk = _searchKind == BotSearchKind.LastKnown && PointReachable(self, point);

            SetState(canWalk ? CsBotState.Patrol : CsBotState.Camp, now);
            intent.State = _state;
            intent.Move = canWalk
                ? _nav.ComputeMove(self.Position, point, 0.8f, now, viaRoute: false, hullHeight: self.Height)
                : Vector3.zero;
            intent.AimPoint = WithEyeHeight(point);
            return true;
        }


        private float StepSeconds()
        {
            return _searchKind == BotSearchKind.Noise ? NoiseSweepSeconds / 8f : SearchStepSeconds;
        }


        /// <summary>第 <paramref name="dirIndex"/> 个扫视方向上的世界点（基准朝向旋转 45°×i，半径 6m，贴地）。</summary>
        private Vector3 SweepPoint(int dirIndex)
        {
            var dir = Quaternion.Euler(0f, dirIndex * 45f, 0f) * _sweepForward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            dir.Normalize();

            var p = _sweepCenter + dir * SearchSweepRadius;
            if (_map != null && _map.IsLoaded &&
                _map.TrySampleGround(p + Vector3.up * 2f, out var ground, out _, 4f))
            {
                p.y = ground.y;
            }
            return p;
        }


        private bool PointReachable(CsActor self, Vector3 point)
        {
            if (_map == null || !_map.IsLoaded) return false;
            if (!_map.CanStand(point)) return false;
            return _nav.CanReach(self.Position, point);
        }


        /// <summary>这个格是否已经搜过（本 bot 搜过的 + 队友搜过的）。</summary>
        private bool CellSearched(Vector3 point, CsTeam team)
        {
            var key = BotNavigator.CellKeyOf(point);
            if (key == long.MinValue) return false;
            if (_searchedCells.Contains(key)) return true;
            var idx = TeamCellIndex(team);
            return idx >= 0 && _searchSharedCells[idx].Contains(key);
        }


        private void MarkSweptCell(CsActor self)
        {
            var key = BotNavigator.CellKeyOf(SweepPoint(_sweepOrder[_sweepOrderPos]));
            if (key == long.MinValue) return;

            if (_searchedCells.Add(key)) _sweepSearched++;

            var idx = TeamCellIndex(self.Team);
            if (idx >= 0) _searchSharedCells[idx].Add(key);
        }


        private static int TeamCellIndex(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.T: return 0;
                case CsTeam.CT: return 1;
                default: return -1;
            }
        }


        private string KindLabel()
        {
            return _searchKind == BotSearchKind.Noise ? "听声调查" : "去最后已知位置";
        }


        private void EndSearch(CsActor self, float now, string why)
        {
            if (_searchKind == BotSearchKind.None) return;

            var kind = _searchKind;
            if (kind == BotSearchKind.LastKnown) _lastKnownSearchedSeenTime = _lastSeenTime;

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）{KindLabel()}结束：{why}（扫过 {_sweepSearched} 个方向，" +
                $"本回合已搜 {_searchedCells.Count} 格，耗时 {now - _searchStartedAt:F1}s）");

            _searchKind = BotSearchKind.None;
            _searchGoal = Vector3.zero;
            _searchStartedAt = 0f;
            _searchDeadline = 0f;
            _searchArrived = false;
            _sweepStartedAt = 0f;
            _sweepDirUntil = 0f;
            _sweepOrderPos = 0;
            _sweepOrderCount = 0;
            _sweepSearched = 0;
        }


        /// <summary>任务层接管时中止搜索（只留一条痕，不重复刷）。</summary>
        private void AbortSearch(float now, string why)
        {
            if (_searchKind == BotSearchKind.None) return;

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）{KindLabel()}中止：{why}（已走 {now - _searchStartedAt:F1}s、" +
                $"扫过 {_sweepSearched} 个方向）");

            _searchKind = BotSearchKind.None;
            _searchGoal = Vector3.zero;
            _searchStartedAt = 0f;
            _searchDeadline = 0f;
            _searchArrived = false;
            _sweepStartedAt = 0f;
            _sweepDirUntil = 0f;
            _sweepOrderPos = 0;
            _sweepOrderCount = 0;
            _sweepSearched = 0;
        }


        // ==================================================================
        //  推进 / 守点
        // ==================================================================
        private CsBotIntent Advance(CsActor self, CsBotIntent intent, float now)
        {
            if (!_goalValid)
            {
                // 没有目标点（地图标记全缺）→ 不乱走；有听觉线索就朝声源看一眼
                SetState(CsBotState.Idle, now);
                intent.State = CsBotState.Idle;
                intent.Move = Vector3.zero;
                intent.AimPoint = _heardFresh ? WithEyeHeight(_heardPos) : LookAhead(self);
                RateWarn("advance.nogoal", $"{_name} 没有目标点（地图标记缺失）→ 原地警戒");
                return intent;
            }

            var radius = _goalIsSite ? CsBotConst.SiteRadius : CsBotConst.DefaultObjectiveRadius;

            //   不要把"到没到包点"与"站没站上那个标记点"耦合在一起：目标点是包点里的**一个标记点**，
            //   站不到它 ≠ 没进包点（判据用 `CsBomb.IsInBombsite` 同口径）；
            //   "到了没有"只作用于**当前守位**（在 Camp 里按 CampSpotArriveRadius 判，且不阻塞换位计时）。
            if (_goalIsSite && self.Team == CsTeam.CT && _holdSpots.Count >= 2 &&
                InsideHoldSite(self, out var siteNow) && siteNow == _holdSiteMarker)
            {
                if (_goalHoldUntil <= 0f) _goalHoldUntil = now + ObjectiveHoldSeconds();
                return Camp(self, intent, _goalPos, now);
            }

            if (BotNavigator.Arrived(self.Position, _goalPos, radius))
            {
                // 刚到达 → 起算"守点时长"（守够就换目标，见 Camp 第 ④ 步）
                if (_goalHoldUntil <= 0f) _goalHoldUntil = now + ObjectiveHoldSeconds();
                return Camp(self, intent, _goalPos, now);
            }

            // 按难度间隔检查"朝目标有没有进展"，没进展就用当前位置重排路线（真实的重寻路）
            MaybeRepath(self, now);

            SetState(CsBotState.Patrol, now);
            intent.State = CsBotState.Patrol;
            intent.Move = _nav.ComputeMove(self.Position, _goalPos, radius, now,
                                           hullHeight: self.Height);
            intent.AimPoint = LookPoint(self, WithEyeHeight(_nav.CurrentTarget(self.Position, _goalPos)));
            return intent;
        }



        /// <summary>
        /// 按队友的交火现场补位：只在「自己这个包点没有交火、另一个包点有队友在开火、而那个包点只剩 1 名存活 CT」
        /// 时把目标换成那个包点并转点。两个包点同时有交火时两支守卫都不转（否则会互相换位）。
        ///
        /// <para>交火现场由可观测状态推出：队友 <c>CsActor.NextFireTime &gt; now</c>（开火时被推进一个循环时间
        /// ⇒ 仍在冷却窗内 = 正在打），且其位置在包点判定球内（<see cref="CsBotConst.SiteRadius"/>，
        /// 与 <c>CsBomb.IsInBombsite</c> 同口径）。不新增事件类型。</para>
        /// </summary>
        /// <returns>true = 已改目标（调用方本 tick 走 <see cref="Advance"/> 出发）。</returns>
        private bool TryReinforceHotSite(CsActor self, float now)
        {
            if (self.Team != CsTeam.CT || _match.BombPlanted) return false;
            if (_holdSiteMarker == null || now < _nextReinforceAt) return false;
            if (_map == null || !_map.IsLoaded) return false;

            var mineIsA = _holdSiteMarker == CsMarkers.BombsiteA;

            var hotA = 0;
            var hotB = 0;
            var aliveA = 0;
            var aliveB = 0;
            string hotName = null;

            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != CsTeam.CT || a.Id == self.Id) continue;

                NearestBombsitePoint(a.Position, out var siteDist, out var siteName);
                if (siteDist > CsBotConst.SiteRadius) continue;

                var atA = siteName == "Bombsite A";
                if (atA) aliveA++;
                else aliveB++;

                if (a.NextFireTime <= now) continue;    // 冷却窗已过 = 这一瞬没在打
                if (atA) hotA++;
                else hotB++;
                if (hotName == null) hotName = a.Name;
            }

            // 自己这个点也有人开着火 ⇒ 不转（两边都在打时互不换位）。
            if (mineIsA ? hotA > 0 : hotB > 0) return false;

            // 另一个包点：有队友在开火，且该点只剩他 1 名存活 CT（计数不含本人）。
            var otherIsHot = mineIsA ? hotB > 0 && aliveB == 1
                                     : hotA > 0 && aliveA == 1;
            if (!otherIsHot) return false;

            var targetIsA = !mineIsA;
            var site = targetIsA ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
            var route = targetIsA ? CsMarkers.CTDefendA : CsMarkers.CTDefendB;

            // 同 site 的次优守位：优先取 2 号点（0/1 号留给原守卫），再退 1 号点。
            var goal = Vector3.zero;
            if (!TryPickGoalPoint(self, site, 2, out goal) &&
                !TryPickGoalPoint(self, site, 1, out goal))
            {
                return false;
            }

            _nextReinforceAt = now + ReinforceCooldownSeconds;
            _reinforceCount++;

            // 换路线后再改目标：Advance 走 viaRoute=true，不换 nav 路线会先把自己旧包点的路线走完。
            _nav.SetRouteKeepProgress(_map, route, self.Position);

            ApplyObjective(self, route, goal, true,
                $"补位：{hotName} 在 {(targetIsA ? "A" : "B")} 点交火且该点只剩 1 人、" +
                $"本人在 {(mineIsA ? "A" : "B")} 点无交火 → 转 {(targetIsA ? "A" : "B")}（本回合第 {_reinforceCount} 次补位）",
                now);
            return true;
        }

        /// <summary>守点：站住 + 周期性换位（"会换位"），听到动静会去看一眼（不离开守点太远）。</summary>
        private CsBotIntent Camp(CsActor self, CsBotIntent intent, Vector3 goal, float now)
        {
            SetState(CsBotState.Camp, now);
            intent.State = CsBotState.Camp;

            // ① 听到动静 → 去看一眼（且声源不能离守点太远）。
            //    包点守卫（HoldSiteActive）不走这一支：守点的契约是"站在守位上盯着来路"，
            //    跟着声音挪步会让守卫在包点里打转、镜头离开入口方向（实测守点采样里 4 成样本朝向偏离入口 35°+）。
            //    视野内的敌人仍由感知/交战分支处理，这一支只影响"没看见人时要不要循声走"。
            if (!HoldSiteActive(self) && _heardFresh && now - _heardTime <= CsBotConst.NoiseMemorySeconds &&
                BotNavigator.Arrived(_heardPos, goal, CsBotConst.CampRepositionRadius * 2f))
            {
                var dir = _nav.ComputeMove(self.Position, _heardPos, CsBotConst.WaypointArriveRadius, now,
                                           viaRoute: false, hullHeight: self.Height);
                if (dir.sqrMagnitude > 0.0001f)
                {
                    intent.Move = dir;
                    intent.AimPoint = WithEyeHeight(_heardPos);
                    return intent;
                }
            }

            //   这里**不会**走下面的 ③"守够时间就换目标" ——
            //   守卫整回合留在自己这个包点，只是在包点内的 2~3 个守位之间轮换
            //   （守 ObjectiveHoldSeconds（2.5~4.8s）就换路线/去巡逻/回出生点会退化成"原地踱步、警不去守点"）。
            if (HoldSiteActive(self) && TryReinforceHotSite(self, now))
            {
                return Advance(self, intent, now);
            }

            if (HoldSiteActive(self))
            {
                if (_holdSwapAt <= 0f) _holdSwapAt = now + CsBotConst.HoldSwapSeconds;   // 到达才起算换位计时
                HoldRotate(self, now);

                var spot = _holdSpots[_holdSlot];
                var ds = spot - self.Position;
                ds.y = 0f;
                if (ds.magnitude > CampSpotArriveRadius)
                {
                    intent.Move = _nav.ComputeMove(self.Position, spot, CampSpotArriveRadius, now,
                                                   viaRoute: false, hullHeight: self.Height);
                    // 挪守位途中也看着入口：瞄脚下那个点会让整段换位期间都是"低头走"，
                    // 到位后再转回来，观感就是"守点的人一直没看该看的方向"。
                    intent.AimPoint = WithEyeHeight(HoldWatchPoint(self, goal));
                    return intent;
                }

                intent.Move = Vector3.zero;
                intent.AimPoint = WithEyeHeight(HoldWatchPoint(self, goal));
                return intent;
            }

            // ② 周期性挪窝
            if (now >= _campPickAt)
            {
                _campPickAt = now + Mathf.Max(1f, _profile.RepathInterval * 2f);
                _campSpot = PickCampSpot(self, goal, out var ok);
                _campMoveUntil = ok ? now + CsBotConst.CampRepositionSeconds : 0f;
            }

            if (now < _campMoveUntil)
            {
                var delta = _campSpot - self.Position;
                delta.y = 0f;
                if (delta.magnitude > 0.6f)
                {
                    intent.Move = _nav.ComputeMove(self.Position, _campSpot, 0.6f, now,
                                                   viaRoute: false, hullHeight: self.Height);
                    intent.AimPoint = WithEyeHeight(_campSpot);
                    return intent;
                }
            }

            // ③' 角色 = **守点**（Anchor，CT 的 A/B 守位）：守点时长到点后**不换目标**，只把计时重新起算。
            //     守够 2.5~4.8s 就 ReplanObjective（换路线 / 去巡逻 / 回出生点），CT 守卫也每几秒改一次
            //     目的地 ⇒ 观感就是"全队都在乱走、没人真的守点"。
            //     这不是"站着不动"：本函数上面的守位表轮换（_holdSpots + _holdSwapAt）照常跑，
            //        玩家看到的是"在包点里换位置盯人"；也不影响卡住/包已下等其它换目标路径。
            //     CT 只要当前目标是包点（_goalIsSite）就持续守：B 点守卫是 Support 角色，旧条件按
            //     StaysOnObjective 只放行 Anchor ⇒ B 守卫每 ~4.8s 换目标离开包点，观感就是"没人守 B"。
            //     换位仍由守位表轮换负责，不是站着不动。
            if (_goalHoldUntil > 0f && now >= _goalHoldUntil && _goalIsSite && self.Team == CsTeam.CT)
            {
                var ensure = ObjectiveHoldSeconds();
                _goalHoldUntil = now + ensure;
                RateWarn("role.anchor.hold",
                    $"{_name}（角色={CsBotRoles.Label(_role)}）在包点 {_holdSiteMarker} 守满 {ensure:F1}s → " +
                    $"{ensure:F1}s 内**继续守**（不 ReplanObjective；守位 {_holdSpots.Count} 个、累计换位 {_holdSwapCount} 次）");
            }
            else if (_goalHoldUntil > 0f && now >= _goalHoldUntil)
            {
                //
                //   换目标"换了但等于没换"的空转闸（修用户报的 AI 乱跑）：
                //   候选被限死时（如「包未下不许离分工位」⇒ 只剩自己这个分工位可选），
                //   ReplanObjective 成功返回但新目标 == 旧目标 ⇒ 下一行 Advance 立刻判
                //   Arrived → 守点计时重起 → 到点再换 → 又是同一个点……实测以守点时长为周期
                //   空转（突破手 1.8s/次，日志同一句「站回分工位」连打几十遍，_replanCount 涨到 16+）。
                //   换不到新目标时**原地续一个守点周期**：守位表轮换（HoldRotate）照常跑，
                //   观感是在包点里换位盯人，而不是每 1.8s 假装重新出发一次。
                var prevGoal = _goalPos;
                if (ReplanObjective(self, $"守点 {ObjectiveHoldSeconds():F1}s 已到（{_difficulty}）", now))
                {
                    var delta = _goalPos - prevGoal;
                    delta.y = 0f;
                    if (delta.magnitude >= 1f)
                    {
                        return Advance(self, intent, now);   // 真换了新目标 → 照常出发
                    }

                    _goalHoldUntil = now + ObjectiveHoldSeconds();
                    RateWarn("replan.same",
                        $"{_name} 守点到时但候选受限（「包未下不离分工位」等闸门）⇒ 换到的还是同一个目标点 " +
                        $"({_goalPos})，原地续守 {ObjectiveHoldSeconds():F1}s（不重排路线，防空转）");
                }
            }

            // ④ 站定守点，盯住守的方向
            intent.Move = Vector3.zero;

            // C4 已下 → T 的"守包"是盯 **CT 的回防来路**，不是低头看包；
            // 盯着包点标记点本身会让一队人围着包站着，被从后面打死。
            if (_match.BombPlanted && self.Team == CsTeam.T)
            {
                var bomb = _match.BombPosition;
                var anchor = bomb.sqrMagnitude > 1f ? bomb : goal;
                intent.AimPoint = WithEyeHeight(NearestWatchPoint(anchor, DefendRoutes));
                return intent;
            }

            // CT 守点（守位表不可用、只剩单点守点时也走这里）→ 仍然盯着该包点的进攻来路。
            intent.AimPoint = WithEyeHeight(self.Team == CsTeam.CT ? HoldWatchPoint(self, goal) : goal);
            return intent;
        }



        private Vector3 PickCampSpot(CsActor self, Vector3 goal, out bool ok)
        {
            ok = false;
            var best = Vector3.zero;

            if (_map != null && _map.IsLoaded)
            {
                var patrol = _map.Points(CsMarkers.Patrol);
                if (patrol != null)
                {
                    var bestScore = float.MaxValue;
                    for (var i = 0; i < patrol.Length; i++)
                    {
                        var d = patrol[i] - goal;
                        d.y = 0f;
                        var dist = d.magnitude;
                        if (dist > CsBotConst.CampRepositionRadius) continue;

                        // 站得下（8 向半径采样）= 与移动解算同一判据。只看 WalkableAt（单格）时，
                        // 会把"紧贴墙壁的格子"选成纳窝点 → 走不过去 → 顶着墙被反复判卡住。
                        if (!_map.CanStand(patrol[i])) continue;

                        // 带随机项的择优：都取最近的话全队会挤在同一格里。
                        // **玩法**（决定 bot 站哪一格 ⇒ 谁先看到谁）⇒ 走本 bot 的 BotDecision 流。
                        var score = dist + _rngDecision.NextFloat() * CsBotConst.CampRepositionRadius;
                        if (score >= bestScore) continue;

                        bestScore = score;
                        best = patrol[i];
                        ok = true;
                    }
                }

                if (!ok)
                {
                    // 没有巡点标记 → 在守点周围随机取一个可走的点。
                    // **玩法**（同上）⇒ 同一个 BotDecision 流（ang 与 r 是"一条子流上连抽两次"）。
                    for (var i = 0; i < 6; i++)
                    {
                        var ang = _rngDecision.NextFloat() * 360f;
                        var r = CsBotConst.CampRepositionRadius * _rngDecision.NextFloat();
                        var cand = goal + Quaternion.Euler(0f, ang, 0f) * new Vector3(0f, 0f, r);
                        if (!_map.CanStand(cand)) continue;
                        best = cand;
                        ok = true;
                        break;
                    }
                }
            }

            if (!ok)
            {
                RateWarn("camp.spot", $"{_name} 找不到守点附近的驻点（{CsMarkers.Patrol} 缺失或都不可走）→ 原地守着");
            }

            return best;
        }



        // ==================================================================
        /// <summary>
        /// 自己**在不在某个包点区域内**（到该包点任一标记点 ≤ <see cref="CsBotConst.SiteRadius"/>），
        /// 并把命中的包点标记名带出来。
        ///
        /// <para>口径与 <c>CsBomb.IsInBombsite</c>（<c>Module/Match/CsBomb.cs:478</c>）同一份：
        /// 到**任一**包点标记的水平距离 ≤ <c>CsMarkers.BombsiteRadius</c>（= 7m）。</para>
        ///
        /// </summary>
        private bool InsideHoldSite(CsActor self, out string marker)
        {
            marker = null;
            if (_map == null || !_map.IsLoaded) return false;

            for (var s = 0; s < 2; s++)
            {
                var m = s == 0 ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
                var pts = _map.Points(m);
                if (pts == null) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - self.Position;
                    d.y = 0f;
                    if (d.magnitude > CsBotConst.SiteRadius) continue;
                    marker = m;
                    return true;
                }
            }

            return false;
        }



        /// <summary>
        /// 是否处于"包点守卫"模式：CT + 包未下 + 本轮目标是包点 + 该包点构造出了 ≥2 个可用守位。
        /// 任一不满足 ⇒ 走旧的"守够就换目标"路径（缺守位表时不许假装能多点分布）。
        /// </summary>
        private bool HoldSiteActive(CsActor self)
        {
            return self.Team == CsTeam.CT
                && !_match.BombPlanted
                && _goalIsSite
                && _holdSiteMarker != null
                && _holdSpots.Count >= 2
                && _holdSlot >= 0
                && _holdSlot < _holdSpots.Count;
        }



        /// <summary>到点就换到下一个守位（<see cref="CsBotHoldSpots.PickSlot"/> 取"离队友最远"的那个），并打一条带数字的 Info。</summary>
        private void HoldRotate(CsActor self, float now)
        {
            if (now < _holdSwapAt) return;

            _holdSwapAt = now + CsBotConst.HoldSwapSeconds;

            var next = CsBotHoldSpots.PickSlot(_holdSpots, _holdSlot, self, _match.Actors);
            if (next < 0 || next == _holdSlot) return;    // 守位只有 1 个 → 不"换"

            var from = _holdSlot;
            _holdSlot = next;
            _holdSwapCount++;

            // 守点换位按 HoldSwapSeconds 周期发生（每个守卫每轮都会走到）：按时间闸留一条现场。
            if (CloverEngine.LogThrottle.ShouldLog("bot.holdswap:" + _name, 15f))
            {
                Game.Logger.Info(Tag,
                    $"守点换位：{_name}（CT）包点 {_holdSiteMarker} 第 {_holdSwapCount} 次换位 " +
                    $"守位 {from + 1}→{next + 1}/{_holdSpots.Count} 新位置={_holdSpots[next]} " +
                    $"（下次换位 {CsBotConst.HoldSwapSeconds:F0}s 后）");
            }
        }



        /// <summary>
        /// 守卫的注视点 = **该包点的 T 进攻路线**（A ↔ <see cref="CsMarkers.TAttackA"/>、
        /// B ↔ <see cref="CsMarkers.TAttackB"/>）里离**该包点的标准下包点**最近的标记点 ——
        /// 敌人就是从那条路出来的。
        ///
        /// <para>为什么锚点取包点而不是自己脚下 / 当前守位：按自己位置取点会让注视方向随一步移动摆动；
        /// 按守位取点则在换位时把"最近的入口"换成另一个（A 长 / A 短两条来路相差约 180°），
        /// 表现为守卫每 8s 甩头半圈。按包点取 → 同一个包点整回合只有一个入口方向。</para>
        ///
        /// <para>包点归属：优先用 <see cref="_holdSiteMarker"/>；守位表没建起来（&lt;2 个可用守位）时
        /// 该字段为空，此时按目标点 / 自身位置离哪个包点标记更近反推，仍能给出入口方向。
        /// 标记缺失（地图数据不全）才退化为 <paramref name="goal"/>。</para>
        /// </summary>
        private Vector3 HoldWatchPoint(CsActor self, Vector3 goal)
        {
            if (_map == null || !_map.IsLoaded) return goal;

            var site = _holdSiteMarker;
            if (site == null)
            {
                var anchor = goal.sqrMagnitude > 0.0001f ? goal : self.Position;
                NearestBombsitePoint(anchor, out var siteDist, out var siteName);
                if (siteDist > CsBotConst.SiteRadius || siteName == null) return goal;
                site = siteName == "Bombsite A" ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
            }

            var route = site == CsMarkers.BombsiteB ? CsMarkers.TAttackB : CsMarkers.TAttackA;
            var pts = _map.Points(route);
            if (pts == null || pts.Length == 0) return goal;

            // 锚点取**该包点的标准下包点**（标记表第 0 个，A#0 / B#0），不是当前守位：
            // 同一个包点的入口因此是恒定的一个方向。按守位取会在换位时把"最近的入口"换成另一个
            // （包点有两条来路时 A 长 / A 短正好相差约 180°），表现为守卫每 8s 甩头半圈。
            var sitePts = _map.Points(site);
            var from = sitePts != null && sitePts.Length > 0 ? sitePts[0] : goal;

            var best = -1f;
            var bestAny = -1f;
            var watch = Vector3.zero;
            var watchAny = Vector3.zero;
            for (var i = 0; i < pts.Length; i++)
            {
                var d = pts[i] - from;
                d.y = 0f;
                var dist = d.magnitude;

                if (bestAny < 0f || dist < bestAny) { bestAny = dist; watchAny = pts[i]; }

                // 包点判定球内的路点不算"来路"：离守位只有几米，方位随一步移动就摆几十度。
                if (dist < CsBotConst.HoldWatchMinDistance) continue;
                if (best >= 0f && dist >= best) continue;
                best = dist;
                watch = pts[i];
            }

            if (best >= 0f) return watch;
            return bestAny >= 0f ? watchAny : goal;
        }



    }
}
