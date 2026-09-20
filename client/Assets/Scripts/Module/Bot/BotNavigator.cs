using System.Collections.Generic;
using CloverEngine;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人导航：路点推进 + 简单避障 + 卡住检测 + **卡住自恢复**。
    ///
    /// <para><b>刻意不用 Unity NavMesh</b>：本工程的 <c>StageDust2</c> 场景没有烘焙 NavMesh，用了之后
    /// <c>NavMeshAgent</c> 会**静默不动**（不报错、不告警）—— 这正是任务书 §2.7 点名的坑。
    /// 所以走法只有两条：<see cref="ICsMap.Points"/> 的路点 + <see cref="ICsMap.WalkableAt"/> 的"这一格能不能走"。</para>
    ///
    /// <para><b>职责边界</b>：本类只输出"这一帧往哪个世界方向走"（已归一化、y=0）。
    /// 真正的位置解算（分轴滑墙 / 台阶 / 重力）由比赛模拟内部的 <c>ICsMap.ResolveMove</c> 负责 ——
    /// 机器人 AI 不执行移动（任务书 §2）。</para>
    ///
    /// <para><b>卡住自恢复（本类自己解决"这一帧怎么走"，"接下来去哪"交给上层）</b>：</para>
    /// <list type="number">
    /// <item>判定卡住 → 日志**按原因降频**（同一原因首次 + 每 <see cref="CsBotConst.StuckWarnEvery"/> 次各一条，
    /// 不再每 0.5s × 每个 bot 刷屏）；</item>
    /// <item>换向**真的换**：从期望方向向两侧按 <see cref="CsBotConst.EscapeSweepStepDegrees"/> 扫描，
    /// 取"前方跑道最长"的方向，并保持 <see cref="CsBotConst.EscapeHoldSeconds"/> 秒沿它走
    /// —— 旧写法只把探针偏角设成 90°、且路线走完后 <c>_index++</c> 是空操作，表现就是"原地反复换向"；</item>
    /// <item>连续卡住达到 <see cref="CsBotConst.StuckReplanStreak"/> 次 → 置 <see cref="ConsumeStuck"/> 的
    /// <c>escalate</c> 标志，由 <see cref="CsBotBrain"/> **重新选目标**（巡逻点 / 换一条路线 / 出生点）。
    /// 这是"路线走完后不许停在原地空转"的落地点：路线走完时本类自己也换不出目标来。</item>
    /// </list>
    /// </summary>
    public sealed class BotNavigator
    {
        private const string Tag = BotModule.Tag;

        private readonly List<Vector3> _route = new List<Vector3>(16);
        private readonly Dictionary<string, int> _stuckLogCounts = new Dictionary<string, int>(4);

        private ICsMap _map;
        private string _marker;
        private string _ownerName = "bot";
        private int _index;
        private bool _hasCheckPos;
        private Vector3 _lastCheckPos;
        private float _nextStuckCheckAt;
        private float _stuckSeconds;
        private float _probeAngle;
        private float _probeUntil;
        private float _nextSkipLogAt;
        /// <summary>"卡住"日志的时间闸（见 <see cref="ReportStuck"/>）：下一次最早可以记一条的时刻。</summary>
        private float _nextStuckLogAt;

        // ---- 卡住自恢复 ----
        private int _stuckStreak;
        private bool _stuckEvent;
        private bool _stuckEscalate;
        private float _lastStuckDistance;
        private Vector3 _escapeDir;
        private float _escapeUntil;
        private float _escapeSign = 1f;
        private bool _warnedNoRunway;
        private Vector3 _lastDir;          // 上一次真正提交出去的方向（判"哪个方向走不动"用）
        private Vector3 _blockedDir;       // 最近一次"想走却走不动"的方向
        private float _blockedUntil;

        /// <summary>当前路线是否至少有一个路点（false = 退化成"朝目标直线走"）。</summary>
        public bool HasRoute => _route.Count > 0;
        /// <summary>当前路线名（日志/自检用）。</summary>
        public string RouteMarker => _marker;
        /// <summary>路线最后一个路点（没有路线时为 <see cref="Vector3.zero"/>）。</summary>
        public Vector3 RouteEnd => _route.Count > 0 ? _route[_route.Count - 1] : Vector3.zero;
        /// <summary>还没有走完的路点数量（日志/自检用）。</summary>
        public int RemainingWaypoints => Mathf.Max(0, _route.Count - _index);
        /// <summary>路线有路点且已经全部走完（= "剩余路点 0"）。此时继续"跳过路点"是空操作，必须由上层换目标。</summary>
        public bool RouteExhausted => _route.Count > 0 && _index >= _route.Count;

        public void SetOwner(string name)
        {
            if (!string.IsNullOrEmpty(name)) _ownerName = name;
        }

        /// <summary>
        /// 绑定地图（**不设路线**也要绑）。必要性：<see cref="Avoid"/> / <see cref="Runway"/> / 路点可走性检查
        /// 全靠 <c>_map</c>，而它原先只在 <see cref="SetRoute"/> 里赋值 —— 只要有一轮"无路线"的巡航
        /// （例如重选目标后退化成"朝一个点直线走"），避障就会**静默失效**（不报错、机器人直着撞墙）。
        /// </summary>
        public void BindMap(ICsMap map)
        {
            _map = map;
        }

        public void Reset()
        {
            _route.Clear();
            _index = 0;
            _marker = null;
            _stuckSeconds = 0f;
            _hasCheckPos = false;
            ClearEscape();
            ClearStuckState();
            _stuckLogCounts.Clear();
            _nextStuckLogAt = 0f;
            _nextSkipLogAt = 0f;
            _warnedNoRunway = false;
        }

        /// <summary>
        /// 设置路线：取地图标记点，并按"离自己最近优先"排序成一条可推进的顺序。
        ///
        /// <para>为什么要排序：<see cref="ICsMap.Points"/> 只保证"这些点属于这条路线"，不保证数组顺序就是推进顺序；
        /// 最近邻排序对"乱序"和"已排序"两种情况都成立（已排序时结果一致），且不会来回震荡。</para>
        ///
        /// <para>标记缺失（空数组）→ 打 Error 并退化：<see cref="ComputeMove"/> 会直接朝目标点直线走，
        /// **不许站着不动**（任务书 §4.2）。</para>
        /// </summary>
        public void SetRoute(ICsMap map, string marker, Vector3 fromPosition)
        {
            _map = map;
            _marker = marker;
            _route.Clear();
            _index = 0;
            _stuckSeconds = 0f;
            _hasCheckPos = false;
            ClearEscape();
            ClearStuckState();

            if (map == null)
            {
                Game.Logger.Error(Tag, $"{_ownerName} 取路线 '{marker}' 失败：地图未绑定（ICsMap == null）→ 退化为朝目标点直线走");
                return;
            }

            var pts = map.Points(marker);
            if (pts == null || pts.Length == 0)
            {
                Game.Logger.Error(Tag,
                    $"{_ownerName} 取路线 '{marker}' 失败：地图上没有该标记（Points 返回空）→ 退化为朝目标点直线走。" +
                    "请检查 de_dust2 生成器是否摆了这个标记（CsMarkers）");
                return;
            }

            var remaining = new List<Vector3>(pts);
            var cursor = fromPosition;
            while (remaining.Count > 0)
            {
                var bestIndex = 0;
                var bestSq = float.MaxValue;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var d = remaining[i] - cursor;
                    d.y = 0f;
                    var sq = d.sqrMagnitude;
                    if (sq < bestSq) { bestSq = sq; bestIndex = i; }
                }
                var pick = remaining[bestIndex];
                remaining.RemoveAt(bestIndex);
                _route.Add(pick);
                cursor = pick;
            }
        }

        /// <summary>清空路线（"重新选目标"时用）：之后 <see cref="ComputeMove"/> 直接朝 goal 走（仍带避障）。</summary>
        public void ClearRoute()
        {
            _route.Clear();
            _index = 0;
            _marker = null;
            _hasCheckPos = false;
            ClearEscape();
            ClearStuckState();
        }

        /// <summary>
        /// 本帧应该往哪走（世界方向，已归一化，y=0）。返回 <see cref="Vector3.zero"/> = 已到目标，不必再动。
        /// </summary>
        /// <param name="selfPosition">机器人当前位置</param>
        /// <param name="goal">最终目标点（包点 / 炸弹 / 守卫点 / 巡逻点）</param>
        /// <param name="arriveRadius">到目标的停步半径（米）</param>
        /// <param name="viaRoute">
        /// true = 沿本轮路线推进到 goal（本轮计划目标用）；
        /// **false = 无视路线直奔 goal** —— 用于"目标变了"的场合：炸弹已下（CT 全冲 C4）、去捡掉落的 C4、
        /// 守点挪窝、听到动静去看一眼。否则会先把自己那条旧路线走完才去，白绕一大圈。
        /// </param>
        public Vector3 ComputeMove(Vector3 selfPosition, Vector3 goal, float arriveRadius, float now,
            bool viaRoute = true)
        {
            var target = viaRoute ? ResolveNextTarget(selfPosition, goal) : goal;
            var toGoal = goal - selfPosition;
            toGoal.y = 0f;

            var routeDone = !viaRoute || _index >= _route.Count;
            if (routeDone && toGoal.magnitude <= arriveRadius)
            {
                NoteMovement(selfPosition, false, now);
                return Vector3.zero;
            }

            var delta = target - selfPosition;
            delta.y = 0f;
            if (delta.sqrMagnitude < 0.0004f)
            {
                // 已经到了当前路点但还没到目标（例如最后一个路点离目标有距离）→ 直接朝目标走
                delta = toGoal;
            }
            if (delta.sqrMagnitude < 0.0004f)
            {
                NoteMovement(selfPosition, false, now);
                return Vector3.zero;
            }

            var desired = delta.normalized;

            // ① 逃逸中：沿上次卡住时选定的"有跑道"方向继续走（保持一段时间 → 真的走出去）。
            //    不做卡住判定（这一段的位移正是为了脱离卡住），逃逸结束后会立刻重新判定。
            if (IsEscaping(now))
            {
                if (Runway(selfPosition, _escapeDir) > 0f)
                {
                    _lastDir = _escapeDir;      // 逃逸方向也是"提交出去的方向"：它走不动时同样要记住
                    return _escapeDir;
                }

                // 逃逸方向也被堵住了 → 立刻弃用，回到正常避障（否则会顶着墙把 1.2s 耗完）
                ClearEscape();
            }

            var dir = Avoid(selfPosition, desired, now);

            if (NoteMovement(selfPosition, true, now, out var moved))
            {
                _stuckEvent = true;
                _lastStuckDistance = moved;
                if (++_stuckStreak >= CsBotConst.StuckReplanStreak) _stuckEscalate = true;
                ReportStuck(moved, now);

                // 记住"这个方向走不动"：贴墙/被挤住时 WalkableAt 可能说能走、物理却说走不动，
                // 只有把失败方向记下来，下一次"换向"才会真的换到别处（否则永远在同一个方向上反复）。
                if (_lastDir.sqrMagnitude > 0.5f)
                {
                    _blockedDir = _lastDir;
                    _blockedUntil = now + CsBotConst.BlockedDirMemorySeconds;
                }

                dir = BeginEscape(selfPosition, desired, now);
            }

            if (dir.sqrMagnitude > 0.5f) _lastDir = dir;
            return dir;
        }

        /// <summary>当前正在追的路点（供"朝行进方向看"用）；没有路线 / 不走路线时返回 goal。</summary>
        public Vector3 CurrentTarget(Vector3 selfPosition, Vector3 goal, bool viaRoute = true)
        {
            return viaRoute ? ResolveNextTarget(selfPosition, goal) : goal;
        }

        /// <summary>是否已经到达目标点（水平距离）。</summary>
        public static bool Arrived(Vector3 selfPosition, Vector3 goal, float arriveRadius)
        {
            var d = goal - selfPosition;
            d.y = 0f;
            return d.magnitude <= arriveRadius;
        }

        /// <summary>
        /// 取走"上一帧判定了卡住"的事件。返回 true 时 <paramref name="moved"/> 是那个检测周期内的位移，
        /// <paramref name="escalate"/> 为 true 表示**连续卡住已达到阈值**（上层必须重新选目标，
        /// 不许再靠"跳过路点/换向"糊过去）。
        /// </summary>
        public bool ConsumeStuck(out float moved, out bool escalate)
        {
            moved = _lastStuckDistance;
            escalate = _stuckEscalate;
            if (!_stuckEvent) return false;
            _stuckEvent = false;
            _stuckEscalate = false;
            return true;
        }

        /// <summary>卡住计数是否已经达到"必须换目标"的阈值（自检/日志用，不消费）。</summary>
        public bool StuckEscalated => _stuckEscalate;

        // ==================================================================
        //  路点推进
        // ==================================================================
        private Vector3 ResolveNextTarget(Vector3 selfPosition, Vector3 goal)
        {
            while (_index < _route.Count)
            {
                var wp = _route[_index];
                var d = wp - selfPosition;
                d.y = 0f;

                if (d.magnitude <= CsBotConst.WaypointArriveRadius)
                {
                    _index++;
                    continue;
                }

                if (_map != null && _map.IsLoaded && !_map.WalkableAt(wp.x, wp.z))
                {
                    // 路点落在墙里/图外 → 跳过。这类是地图标记问题，必须留痕（降频，避免每条路线每帧刷）。
                    if (Time.time >= _nextSkipLogAt)
                    {
                        _nextSkipLogAt = Time.time + CsBotConst.StuckWarnCooldown;
                        Game.Logger.Warn(Tag,
                            $"{_ownerName} 跳过不可走的路点 {wp}（路线 '{_marker}'，第 {_index + 1}/{_route.Count} 个）—— " +
                            "多半是地图标记摆进了几何体");
                    }
                    _index++;
                    continue;
                }

                break;
            }

            return _index < _route.Count ? _route[_index] : goal;
        }

        // ==================================================================
        //  避障（基于 ICsMap.WalkableAt 的前向试探）
        // ==================================================================
        private Vector3 Avoid(Vector3 position, Vector3 dir, float now)
        {
            if (_map == null || !_map.IsLoaded) return dir;   // 没有地图就没有"空间事实"，直线走（外层已打过 Error）

            // 上一帧的偏角还有效 → 先沿用（带惯性，防止贴着障碍物左右抖）
            if (_probeAngle != 0f && now < _probeUntil)
            {
                var cached = Rotate(dir, _probeAngle);
                if (WalkableAhead(position, cached) && !IsBlockedDir(cached, now)) return cached;
                _probeAngle = 0f;
            }

            // 上一次在这个方向上一动不动 → 不要再往这里推（WalkableAt 说能走、物理说走不动的情况真有）
            if (WalkableAhead(position, dir) && !IsBlockedDir(dir, now))
            {
                _probeAngle = 0f;
                return dir;
            }

            for (var i = 0; i < CsBotConst.AvoidAngles.Length; i++)
            {
                var angle = CsBotConst.AvoidAngles[i];
                var candidate = Rotate(dir, angle);
                if (!WalkableAhead(position, candidate)) continue;
                if (IsBlockedDir(candidate, now)) continue;

                _probeAngle = angle;
                _probeUntil = now + CsBotConst.ProbeHoldSeconds;
                return candidate;
            }

            // 一圈都不通（或都在"走不动"的方向上）→ 取"跑道最长"且**不是失败方向**的那条。
            // **不再返回一条没验证过的方向**（旧写法直接 `Rotate(dir, 180)`）：没验证的反向在贴墙时
            // 会连着几十帧位移 0，这正是"卡住日志里换向却原地不动"的另一半成因。
            _probeAngle = 0f;
            _probeUntil = 0f;
            var fallback = PickBestRunwayDir(position, dir, out var run, now);
            if (run <= 0f) WarnNoRunway(position);
            return fallback;
        }

        /// <summary>这个方向最近被证明"走不动"吗（<see cref="CsBotConst.BlockedDirMemorySeconds"/> 内、夹角 ≤ 35°）。</summary>
        private bool IsBlockedDir(Vector3 dir, float now)
        {
            if (now >= _blockedUntil) return false;
            if (_blockedDir.sqrMagnitude < 0.5f) return false;
            return Vector3.Angle(_blockedDir, dir) <= CsBotConst.BlockedDirDegrees;
        }

        /// <summary>
        /// "前方这一段能不能走"：**近端 + 远端两处都要可走**。
        ///
        /// <para>为什么不能只看远端：<see cref="ICsMap.WalkableAt"/> 是"这一格可不可走"的格子查询。
        /// 面前一堵 1 格厚（1m）的墙时，<see cref="CsBotConst.ProbeDistance"/>（1.4m）处的格子落在
        /// **墙的另一侧**，那格是可走的 ⇒ 只看远端会判定"前方畅通"，机器人就会一直顶着墙推：
        /// 每帧位移 0 → 被判卡住 → 日志刷屏，而且避障的偏角候选**一次都不会被尝试**。
        /// 这是主 agent 实测那条"卡住：0.00m，原地反复换向"的头号成因。</para>
        /// </summary>
        private bool WalkableAhead(Vector3 position, Vector3 dir)
        {
            var near = position + dir * CsBotConst.ProbeClearance;
            if (!_map.WalkableAt(near.x, near.z)) return false;

            var far = position + dir * CsBotConst.ProbeDistance;
            return _map.WalkableAt(far.x, far.z);
        }

        private static Vector3 Rotate(Vector3 dir, float degrees)
        {
            return Quaternion.Euler(0f, degrees, 0f) * dir;
        }

        // ==================================================================
        //  逃逸（卡住时"真的换一个方向并走出去"）
        // ==================================================================
        private bool IsEscaping(float now) => _escapeUntil > now && _escapeDir.sqrMagnitude > 0.5f;

        private void ClearEscape()
        {
            _escapeDir = Vector3.zero;
            _escapeUntil = 0f;
        }

        /// <summary>
        /// 沿 <paramref name="dir"/> 从 <paramref name="position"/> 起连续可走的距离（米），
        /// 上限 <see cref="CsBotConst.EscapeRunwayMax"/>。没加载地图时按"全通"处理。
        /// </summary>
        private float Runway(Vector3 position, Vector3 dir)
        {
            if (_map == null || !_map.IsLoaded) return CsBotConst.EscapeRunwayMax;

            var run = 0f;
            for (var d = CsBotConst.EscapeRunwayStep; d <= CsBotConst.EscapeRunwayMax + 0.001f;
                 d += CsBotConst.EscapeRunwayStep)
            {
                var p = position + dir * d;
                if (!_map.WalkableAt(p.x, p.z)) break;
                run = d;
            }
            return run;
        }

        /// <summary>
        /// 从 <paramref name="desired"/> 向两侧按 <see cref="CsBotConst.EscapeSweepStepDegrees"/> 扫描，
        /// 返回**前方跑道最长**的方向（<paramref name="bestRun"/> 是它的跑道长度，0 = 四面都被堵）。
        ///
        /// <para>两条硬要求：① 期望方向若刚被判过"走不动"，它没有资格当默认值（否则扫描完还是它，
        /// 就变成"每次都换向、每次都换到同一个方向"）；② 优先从 <c>_escapeSign</c> 那一侧开始，每次换边。</para>
        /// </summary>
        private Vector3 PickBestRunwayDir(Vector3 position, Vector3 desired, out float bestRun, float now)
        {
            var best = desired;
            bestRun = IsBlockedDir(desired, now) ? -1f : Runway(position, desired);

            var first = _escapeSign >= 0f ? 1f : -1f;
            for (var step = 1; step <= CsBotConst.EscapeSweepSteps; step++)
            {
                for (var s = 0; s < 2; s++)
                {
                    var sign = s == 0 ? first : -first;
                    var cand = Rotate(desired, step * CsBotConst.EscapeSweepStepDegrees * sign);
                    var run = Runway(position, cand);
                    if (run <= 0f) continue;
                    if (IsBlockedDir(cand, now)) continue;
                    if (run <= bestRun) continue;
                    bestRun = run;
                    best = cand;
                }
            }

            if (bestRun < 0f)
            {
                // 期望方向不能用、别的又都不可走 → 退回"正后方"（宁可退也不能原地不动）
                best = Rotate(desired, 180f);
                bestRun = Runway(position, best);
            }
            return best;
        }

        /// <summary>
        /// 卡住时选一条能走的方向并**保持 <see cref="CsBotConst.EscapeHoldSeconds"/> 秒**。
        /// 返回这一帧要提交的方向（一定非零 —— 全是墙的话也照样给"跑道最长"的那条，让物理去滑墙，
        /// 同时由上层换目标把机器人带离这里）。
        /// </summary>
        private Vector3 BeginEscape(Vector3 position, Vector3 desired, float now)
        {
            var dir = PickBestRunwayDir(position, desired, out var run, now);

            _escapeSign = -_escapeSign;                  // 下次先从另一侧扫
            _escapeDir = dir;
            _escapeUntil = now + CsBotConst.EscapeHoldSeconds;

            if (run <= 0f) WarnNoRunway(position);

            return dir;
        }

        /// <summary>四面都不可走：只报一次（这是地图标记/几何问题，不是每帧都要刷的事故）。</summary>
        private void WarnNoRunway(Vector3 position)
        {
            if (_warnedNoRunway) return;
            _warnedNoRunway = true;
            Game.Logger.Warn(Tag,
                $"{_ownerName} 四周（含 ±180°）前方 {CsBotConst.EscapeRunwayMax:F1}m 内都没有可走的地面（位置 {position}）—— " +
                "要么落在图外，要么被几何体围住；只能朝跑道最长的方向硬走，并等待上层换目标");
        }

        // ==================================================================
        //  卡住检测
        // ==================================================================
        /// <summary>
        /// 采样位移并判定卡住。<paramref name="wantMove"/> = 本帧表达了"想移动"。
        /// 返回 true = 本检测周期判定为卡住（调用方据此换向/换目标）；<paramref name="moved"/> 返回周期内位移。
        /// </summary>
        private bool NoteMovement(Vector3 position, bool wantMove, float now)
        {
            return NoteMovement(position, wantMove, now, out _);
        }

        private bool NoteMovement(Vector3 position, bool wantMove, float now, out float moved)
        {
            moved = 0f;

            if (!_hasCheckPos)
            {
                _hasCheckPos = true;
                _lastCheckPos = position;
                _nextStuckCheckAt = now + CsBotConst.StuckCheckInterval;
                return false;
            }

            if (now < _nextStuckCheckAt) return false;
            _nextStuckCheckAt = now + CsBotConst.StuckCheckInterval;

            var d = position - _lastCheckPos;
            d.y = 0f;
            moved = d.magnitude;
            _lastCheckPos = position;

            if (!wantMove || moved >= CsBotConst.StuckMoveEpsilon)
            {
                _stuckSeconds = 0f;
                _stuckStreak = 0;                  // 动了 = 没卡住，连续计数归零
                return false;
            }

            _stuckSeconds += CsBotConst.StuckCheckInterval;
            if (_stuckSeconds < CsBotConst.StuckCheckInterval) return false;

            _stuckSeconds = 0f;

            // 路线还没走完 → 跳过当前路点（这一步有意义）；路线已走完（剩余路点 0）时跳过是空操作，
            // 真正的出路是上层的"重新选目标"，由 escalate 标志驱动。
            if (_index < _route.Count) _index++;
            _hasCheckPos = false;                  // 换向后重新取基准
            return true;
        }

        /// <summary>
        /// "卡住"日志：**按原因降频** —— 同一原因首次 + 每 <see cref="CsBotConst.StuckWarnEvery"/> 次各一条，
        /// **并且**同一原因两次日志之间至少隔 <see cref="CsBotConst.StuckWarnMinInterval"/> 秒。
        ///
        /// <para>两道闸都要：现场是 8 个机器人 × 每 0.5s 一次判定，只按次数降频（每 10 次一条）
        /// 仍会每秒刷出 1~2 条（日志里"同一原因第 70 次"就是这么来的）。</para>
        /// </summary>
        private void ReportStuck(float moved, float now)
        {
            var reason = _marker == null
                ? "无路线（直奔目标）"
                : (RouteExhausted ? "路线已走完（剩余路点 0）" : "路上被挡");

            var key = "stuck." + reason;
            _stuckLogCounts.TryGetValue(key, out var n);
            n++;
            _stuckLogCounts[key] = n;

            if (n != 1 && n % CsBotConst.StuckWarnEvery != 0) return;

            // 时间闸：同一原因在 StuckWarnMinInterval 内已经记过就不再记（次数仍在累计，日志里能看到"第 N 次"）
            if (now < _nextStuckLogAt) return;
            _nextStuckLogAt = now + CsBotConst.StuckWarnMinInterval;

            Game.Logger.Warn(Tag,
                $"{_ownerName} 卡住：{CsBotConst.StuckCheckInterval:F1}s 内位移 {moved:F2}m < {CsBotConst.StuckMoveEpsilon:F2}m" +
                $"（路线 '{_marker ?? "无"}'，剩余路点 {RemainingWaypoints}，原因 {reason}）" +
                $"→ 沿跑道最长的方向走出 {CsBotConst.EscapeHoldSeconds:F1}s" +
                $"（同一原因第 {n} 次：本日志只在首次 + 每 {CsBotConst.StuckWarnEvery} 次、" +
                $"且间隔 ≥ {CsBotConst.StuckWarnMinInterval:F0}s 时才记一条）");
        }

        private void ClearStuckState()
        {
            _stuckStreak = 0;
            _stuckEvent = false;
            _stuckEscalate = false;
            _lastStuckDistance = 0f;
            _stuckSeconds = 0f;
            _blockedDir = Vector3.zero;
            _blockedUntil = 0f;
            _lastDir = Vector3.zero;
        }
    }
}
