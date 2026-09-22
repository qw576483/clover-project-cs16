using System.Collections.Generic;
using CloverEngine;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人导航：**全局 A\* 路径** + 路点推进 + 简单避障 + 卡住检测 + **卡住自恢复**。
    ///
    /// <para><b>刻意不用 Unity NavMesh</b>：本工程的 <c>StageDust2</c> 场景没有烘焙 NavMesh，用了之后
    /// <c>NavMeshAgent</c> 会**静默不动**（不报错、不告警）—— 这正是任务书 §2.7 点名的坑。
    /// 所以走法只有两条：<see cref="ICsMap.Points"/> 的路点（**去哪**）+ 引擎 <see cref="AStar"/>
    /// 在 <see cref="ICsMap.WalkableAt"/> 这张可走位图上求出的路径（**怎么绕过去**）。</para>
    ///
    /// <para><b>⛔ 不自建寻路</b>：格子 A* 是引擎能力（<c>clover-client-unity-engine/Runtime/Core/AStar.cs</c>，
    /// 8 邻接 / 对角要求两侧可走 / octile 启发 / 视线拉直 <c>FindSmoothed</c> / <c>DefaultMaxNodes</c>），
    /// 本类只提供 <c>walkable</c> 回调与"格 ↔ 世界"换算。旧实现（差异 #77）是"沿标记点最近邻走直线"，
    /// 绕不开整片墙；本片起改为"求路径再沿路径走"，**局部避障 <see cref="Avoid"/> 原样保留、两者叠加**。</para>
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
        /// <summary>片BL-R：位图判"不可走"、物理判"走得动"这条留证只报一次。</summary>
        private bool _warnedBitmapDisagrees;
        private Vector3 _lastDir;          // 上一次真正提交出去的方向（判"哪个方向走不动"用）
        private Vector3 _blockedDir;       // 最近一次"想走却走不动"的方向
        private float _blockedUntil;

        // ---- 全局路径（引擎 A*；消除差异 #77「业务零使用 A*」）----
        /// <summary>当前路径（引擎 <see cref="AStar"/> 的**格坐标**序列；null = 没有可用路径，退化为"朝目标直线走"）。</summary>
        private List<Vector2Int> _path;
        /// <summary>沿 <see cref="_path"/> 推进到的下标。</summary>
        private int _pathIndex;
        /// <summary><see cref="_path"/> 的终点格：终点格一变就必须重求（这就是"目标变了"的判据）。</summary>
        private Vector2Int _pathGoalCell;
        /// <summary>下一次最早允许重求路径的时刻（性能闸：⛔ 不许每帧求路径）。</summary>
        private float _nextRepathAt;
        /// <summary>"求路径失败"日志的时间闸（见 <see cref="WarnPathFailed"/>）。</summary>
        private float _nextPathFailLogAt;

        // ---- 连通性（切片BJ）：可走 ≠ 走得到 ----
        /// <summary>
        /// 最近一次被判为"**与起点格不连通**"的终点格（`AStar.Find` 在两端都可走时仍返回 null）。
        ///
        /// <para>为什么需要这份备忘：位图可以有好几块互不连通的区域，而**标记点/路线路点只判过"可走"、
        /// 没判过"走得到"**（生成侧 <c>SnapMarkerToWalkable</c> 与运行侧 <c>ICsMap.CanStand</c> 都只管本格的
        /// 站立性）。切片BJ 实测：127×145 位图有 **46 个连通分量**，主分量 4393 格，其余 45 个分量共 919 格；
        /// <c>Route_CT_Mid[1]</c>=(80,93) 世界 (17.5,21.5) 就落在 **19 格的孤岛**里 ⇒ 追它 = 永远求不出路径。
        /// 记住这一格，<see cref="ResolveNextTarget"/> 就能把对应路点跳掉，而不是一路朝它撞墙。</para>
        /// </summary>
        private Vector2Int _unreachableCell;
        private bool _hasUnreachableCell;
        /// <summary>同一终点格连续求路径失败的次数（达到 <see cref="CsBotConst.PathFailStreakToUnreachable"/> 即记入上面那条备忘）。</summary>
        private int _pathFailStreak;
        private Vector2Int _pathFailCell;
        /// <summary>"判到不连通"日志的时间闸。</summary>
        private float _nextUnreachableLogAt;

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
            InvalidatePath();
            InvalidateUnreachable();
            _stuckLogCounts.Clear();
            _nextStuckLogAt = 0f;
            _nextSkipLogAt = 0f;
            _nextPathFailLogAt = 0f;
            _warnedNoRunway = false;
        }

        /// <summary>
        /// 丢掉"哪一格不可达"的备忘（换路线 / 重选目标 / 复位时）。
        /// 必要性：备忘是**当前这条路线**的结论，路线一换起点与终点全变，留着它会把新路线里合法的路点也跳掉
        /// （那就是"修一个 bug 换出一个更差的行为"）。
        /// </summary>
        private void InvalidateUnreachable()
        {
            _hasUnreachableCell = false;
            _pathFailStreak = 0;
            _nextUnreachableLogAt = 0f;
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
            InvalidatePath();
            InvalidateUnreachable();

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

            // ★ 兜底（切片BC）：抽点后**排掉站不住的点**（`!CanStand`）。
            //
            // 为什么要有这一层：标记点的坐标是**数据**（生成侧已在 DumpMarkers / ExportMarkerResource 里
            // 把"落在阻挡格上"的点吸附到最近可走格心，见 Dust2Builder.SnapMarkerToWalkable），而
            // `CanStand` 是**运行时**才有的判据（位图 8 向 + 地面一步台阶 + 身体高度带几何复核）——
            // 只有它知道"这一格上是否真的站得下一个 radius 半径的人"。两者不一致时（地图资产过期、
            // 手改场景、几何与位图口径漂移），机器人会一路走到一个站不住的点上再卡住。
            // 兜底排掉 = 让路线只含"真正能站的格"，而不是到地方才发现过不去。
            //
            // ⛔ 不许**静默**排点：每有一个点被排掉都必须留痕（降频，见下），
            //    且"整条路线全被排掉"要打 Error 并**退回未过滤的路线**（⛔ 不许比旧实现更差）。
            var kept = new List<Vector3>(pts.Length);
            var dropped = 0;
            for (var i = 0; i < pts.Length; i++)
            {
                if (map.CanStand(pts[i]))
                {
                    kept.Add(pts[i]);
                    continue;
                }

                dropped++;
                // 降频口径 = CsBotConst.StuckWarnCooldown（与「跳过不可走的路点」同一个闸：这是
                // 每个 bot 每次换目标都会碰到的分支，只许按时间降频，不许每次打）。
                if (Time.time >= _nextSkipLogAt)
                {
                    _nextSkipLogAt = Time.time + CsBotConst.StuckWarnCooldown;
                    Game.Logger.Warn(Tag,
                        $"{_ownerName} 路线 '{marker}' 第 {i + 1}/{pts.Length} 个路点 " +
                        $"({pts[i].x:F1},{pts[i].y:F1},{pts[i].z:F1}) 站不住（ICsMap.CanStand=false）" +
                        $"→ 已从路线里排掉（累计已排 {dropped} 个；本日志按 {CsBotConst.StuckWarnCooldown:F0}s 降频）");
                }
            }

            if (kept.Count == 0)
            {
                Game.Logger.Error(Tag,
                    $"{_ownerName} 路线 '{marker}' 的 {pts.Length} 个标记点**全部** CanStand=false " +
                    "（多半是地图资产与几何口径不一致，或场景里的标记对象被改过）→ " +
                    "退回未过滤的路线（⛔ 不复现「整条路线为空」这种更差的行为）；请检查 de_dust2 生成器与标记表");
                kept = new List<Vector3>(pts);
            }

            var remaining = kept;
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

            DropUnreachableWaypoints(fromPosition, marker, pts.Length);
        }

        /// <summary>
        /// 排掉"**可走但走不到**"的路点（切片BJ 新增的兜底，与上面 <c>CanStand</c> 那层同一形状）。
        ///
        /// <para><b>为什么必须有这一层</b>：生成侧 <c>SnapMarkerToWalkable</c> 与运行侧 <see cref="ICsMap.CanStand"/>
        /// 都只判"这一点**站得住 / 可走**"，**不判"走得到"**；而引擎 <see cref="AStar"/> 是**格子连通性**查询。
        /// 一张单层 2D 位图里可以有多个互不连通的区域 —— 切片BJ 离线实测（<c>tools/probes/marker-connectivity.py</c>）：
        /// 该位图 **46 个连通分量**，主分量（含 CT/T 出生点、A/B 包点）4393 格，其余 45 个分量共 919 格；
        /// **11/117 个标记点、10/35 条相邻路点对**落在这些孤岛里。路点一旦落在孤岛里，
        /// <see cref="EnsurePath"/> 就**永远**返回 false ⇒ 旧行为是"朝它直线走" ⇒ 每 0.5s 判一次卡住 ⇒
        /// 无限循环（切片BI 实测 373s 里 CT 进点 0、除真人外无 actor 位移 &gt; 5.8m）。</para>
        ///
        /// <para><b>判据与运行时同源</b>：直接调引擎 <see cref="AStar.Find"/> + 本类同一个 <see cref="WalkableCell"/>
        /// + 同一个 <see cref="SnapToWalkable"/> 半径（⛔ 不另写一套连通性算法，否则判据与被判对象会漂移）。
        /// 推进顺序 = 上面刚排好的顺序（从机器人当前位置逐个走）。</para>
        ///
        /// <para>⛔ 不许静默排点（降频 Warn，逐点给格号）；⛔ "整条路线全被排掉"时**退回未过滤的路线**并打 Error
        /// —— 与上面的 <c>CanStand</c> 层同口径，绝不复现"整条路线为空"这种更差的行为。</para>
        /// </summary>
        private void DropUnreachableWaypoints(Vector3 fromPosition, string marker, int rawCount)
        {
            var mapData = Game.Map;
            if (_route.Count == 0) return;
            if (mapData == null || !mapData.Loaded || mapData.CellSize <= 0f) return;   // 位图不可用：无从判连通

            var cursorCell = CellOf(mapData, fromPosition);
            if (!SnapToWalkable(ref cursorCell)) return;      // 自己在图外/全是阻挡格：不排，交给运行时

            var kept = new List<Vector3>(_route.Count);
            var dropped = 0;
            for (var i = 0; i < _route.Count; i++)
            {
                var pt = _route[i];
                var cell = CellOf(mapData, pt);
                if (!SnapToWalkable(ref cell))
                {
                    kept.Add(pt);                             // 本格不可走这一支由运行时"跳过不可走路点"负责
                    continue;
                }

                if (AStar.Find(WalkableCell, cursorCell, cell, AStar.DefaultMaxNodes) == null)
                {
                    dropped++;
                    if (Time.time >= _nextSkipLogAt)
                    {
                        _nextSkipLogAt = Time.time + CsBotConst.StuckWarnCooldown;
                        var c = CellCenter(cell);
                        Game.Logger.Warn(Tag,
                            $"{_ownerName} 路线 '{marker}' 第 {i + 1}/{_route.Count} 个路点 " +
                            $"({pt.x:F1},{pt.y:F1},{pt.z:F1})（格 {cell}，世界 {c.x:F1},{c.z:F1}）" +
                            $"**可走但走不到**（引擎 AStar.Find 判不连通）→ 已从路线里排掉（累计已排 {dropped} 个；" +
                            $"本日志按 {CsBotConst.StuckWarnCooldown:F0}s 降频）");
                    }
                    continue;
                }

                kept.Add(pt);
                cursorCell = cell;
            }

            if (dropped == 0) return;

            if (kept.Count == 0)
            {
                Game.Logger.Error(Tag,
                    $"{_ownerName} 路线 '{marker}' 的 {rawCount} 个标记点**全部**与当前位置判为不连通（引擎 AStar.Find 全返回 null）" +
                    "→ 退回未过滤的路线（⛔ 不复现「整条路线为空」这种更差的行为）。" +
                    "多半是位图把该片区域封成了孤立分量（单层 2D 位图 + 多层几何），见 tools/probes/marker-connectivity.py");
                return;
            }

            _route.Clear();
            _route.AddRange(kept);
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
            InvalidatePath();
            InvalidateUnreachable();   // 切片BJ：路线没了 ⇒ 上一条的"哪一格不可达"结论作废
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

                // 判到卡住 ⇒ 当前位置已经偏离原路径（被挤开 / 物理走不动）⇒ 强制重求：
                // 全局路径是"计划"，这一帧的事实是"计划失效了"。下一帧 <see cref="EnsurePath"/> 会立刻重求，
                // 再由 <see cref="Avoid"/> 负责把这一帧走出去。
                InvalidatePath();

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
        //  路点推进 = ① 路线点推进（既有） + ② **全局**格子 A* 求路径（本片新增）
        // ==================================================================
        /// <summary>
        /// 这一帧追哪个点：先按既有口径推进"路线点"（<see cref="ICsMap.Points"/> 的标记点），
        /// 再在**可走位图**上用引擎 <see cref="AStar"/> 求一条路径，返回路径上的下一个拐点。
        ///
        /// <para><b>为什么改成求路径</b>：旧实现是"把标记点按最近邻排序成一条路线，然后逐点走**直线**"——
        /// 直线不看几何，机器人于是贴着墙角磨、绕不开整片墙（差异 #77：业务零使用引擎 A*，
        /// 而引擎 <c>Runtime/Core/AStar.cs</c> 早就有 A* + 视线拉直）。现在"绕开墙"由 A* 负责。</para>
        ///
        /// <para><b>与 <see cref="Avoid"/> 的分工（两者叠加，缺一不可，⛔ 不是二选一）</b>：
        /// ① **全局** = 本方法 + <see cref="EnsurePath"/>：用 <see cref="AStar.FindSmoothed"/> 在可走位图上
        /// 求"从我在哪到目标该走哪几格"，解决"绕开整片墙 / 走哪条通道"；
        /// ② **局部** = <see cref="Avoid"/>：A* 只给"格子级"的通行方向，1m 格内仍会贴墙角、被别的角色挤住、
        /// 被<see cref="ICsMap.WalkableAt"/> 说可走而物理走不动的落差卡住 —— 那一段由前向试探 / 偏角 /
        /// 跑道扫描 / 逃逸负责。A* 看不见"这一帧被谁挤住"，<see cref="Avoid"/> 不知道"该绕远路"。</para>
        ///
        /// <para><b>退化口径（⛔ 不许比旧实现更差）</b>：地图位图未加载 / 起点终点都 snap 不到可走格 /
        /// 不可达（<see cref="AStar.Find"/> 返回 null）⇒ 走**既有**的"朝标记点（或 goal）直线走"，
        /// 并留一条降频 Warn（见 <see cref="WarnPathFailed"/>，⛔ 不许静默）。</para>
        ///
        /// <para><b>切片BJ 补的两处（都只针对"可走但走不到"这一支，其余口径不变）</b>：</para>
        /// <list type="number">
        /// <item>被追的路点若已被判为**不连通**（见 <see cref="NotePathFail"/> / <see cref="_unreachableCell"/>）
        /// ⇒ **跳过它**推进到下一个走得到的路点（与既有的"跳过不可走路点"同一形状）。这是"CT 进点 0"的直接前提：
        /// 旧行为会永远追着孤岛里的那个路点原地打转，路线根本没机会往下走。</item>
        /// <item>**最终目标**(goal) 落在孤岛里（不是路点，跳不掉）⇒ 立刻上报"卡住 + 必须换目标"
        /// （<see cref="ConsumeStuck"/> 的 escalate），不再等 2×0.5s 的位移判卡 —— 终点走不到时，
        /// "换个方向/换条路点"都无解，只有换目标能走出去。</item>
        /// </list>
        /// </summary>
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

                // ★ 切片BJ：路点**可走但不连通**时也要跳掉它。
                //   "可走"（`WalkableAt` 说这一格能站）与"走得到"（A* 求得出路径）是两件事：
                //   实测 `Route_CT_Mid[1]`=(80,93) 世界 (17.5,21.5) 落在 **19 格的孤立分量**里（主分量 4393 格）
                //   —— 这一点可走，却永远走不到。旧行为是"朝它直线走"⇒ 每 0.5s 判一次卡住（位移 0.00m）⇒
                //   换目标 ⇒ 换到的路线里又有同样落在孤岛里的路点 ⇒ 无限循环。跳过它，机器人才能继续沿
                //   路线推进到下一个**走得到**的路点（这正是"CT 进点 0 → >0"的前提）。
                if (_hasUnreachableCell && Game.Map != null && CellOf(Game.Map, wp) == _unreachableCell)
                {
                    if (Time.time >= _nextSkipLogAt)
                    {
                        _nextSkipLogAt = Time.time + CsBotConst.StuckWarnCooldown;
                        var c = CellCenter(_unreachableCell);
                        Game.Logger.Warn(Tag,
                            $"{_ownerName} 跳过**求不出路径**的路点 {wp}（格 {_unreachableCell}，世界 {c.x:F1},{c.z:F1}；" +
                            $"路线 '{_marker}'，第 {_index + 1}/{_route.Count} 个）—— 该格与当前位置不连通" +
                            "（位图孤立分量），继续追它只会原地卡住");
                    }
                    _index++;
                    continue;
                }

                break;
            }

            var target = _index < _route.Count ? _route[_index] : goal;

            // 全局路径可用 → 追路径上的下一个拐点；不可用 → 追目标本身（旧行为，已留 Warn）。
            if (EnsurePath(selfPosition, target)) return AdvancePath(selfPosition, target);

            return target;
        }

        // ==================================================================
        //  全局路径（引擎 A*）——「这一帧往哪走」的**全局**那一半
        // ==================================================================
        /// <summary>
        /// 保证 <see cref="_path"/> 是"从我所在格到 <paramref name="target"/> 所在格"的一条可用路径。
        /// 返回 false = 位图不可用 / 求不出（调用方退化为直线走）。
        ///
        /// <para><b>重求时机（都不是"每帧"，性能闸就靠它）</b>：① 还没有路径；② 目标格变了（"目标变了"）；
        /// ③ 路径已经走完；④ 兜底时间闸 <see cref="CsBotConst.PathReplanInterval"/> 到点（兼作"被挤开后自我纠偏"）；
        /// ⑤ 判到卡住时由 <see cref="ComputeMove"/> 主动 <see cref="InvalidatePath"/> 强制重求。</para>
        /// </summary>
        private bool EnsurePath(Vector3 selfPosition, Vector3 target)
        {
            // 出处：引擎门面 `Runtime/Core/Game.cs:265` 的 `Game.Map`（IMapData）——它给出位图的**格边长 / 原点**
            // （`clover-client-unity-engine/Runtime/Core/PresentationContracts.cs:552-559`），
            // 而"世界坐标可走吗"仍走 <see cref="ICsMap.WalkableAt"/>（与 Avoid/Runway 同一份空间事实）。
            var map = Game.Map;
            if (map == null || !map.Loaded || map.CellSize <= 0f)
            {
                _path = null;
                WarnPathFailed(target, "地图位图未加载 / 格边长非法（Game.Map 不可用）");
                return false;
            }

            var from = CellOf(map, selfPosition);
            var to = CellOf(map, target);

            // `AStar.Find` 的入参契约 = 起点/终点格**必须可走**，否则直接返回 null。机器人被挤进位图判阻挡的
            // 格子（1m 格 + 0.4m 角色半径，贴墙时很常见）时若不 snap，就会**永远**求不出路径。
            if (!SnapToWalkable(ref from) || !SnapToWalkable(ref to))
            {
                InvalidatePath();
                WarnPathFailed(target, $"起点格或终点格在 {CsBotConst.PathSnapRadiusCells} 格内找不到可走格" +
                                       $"（起点 {from} / 终点 {to}）");
                return false;
            }

            if (_path != null && _pathGoalCell == to && _pathIndex < _path.Count && Time.time < _nextRepathAt)
            {
                return true;                       // 沿用既有路径（时间闸未到）
            }

            _nextRepathAt = Time.time + CsBotConst.PathReplanInterval;

            // `FindSmoothed` = `Find`（8 邻接、对角要求两侧可走）+ 视线拉直（拐角变成长直线，正合走位）。
            // 节点上限用引擎既有的 `DefaultMaxNodes`（⛔ 不自己加魔法数）。
            var path = AStar.FindSmoothed(WalkableCell, from, to, AStar.DefaultMaxNodes);
            if (path == null || path.Count == 0)
            {
                // ★ 切片BJ：走到这一支 ⇒ **起点与终点都已通过 `WalkableCell`**（引擎 `AStar.Find` 对不可走的
                //   起/终点会**先**返回 null 并打 `badstart`/`badgoal`），所以这一支的真实含义是
                //   **「两格都可走，但位图上不连通」**（`AStar.cs:130` 的 `astar.nopath`）——
                //   与"位图不可用""起终点不可走"是三件不同的事，⛔ 不许混在一句日志里（旧日志写
                //   "位图不可用 / 目标点不可达"，实测会把人引到错误的方向）。
                //   连续 `PathFailStreakToUnreachable` 次同端点求不出 ⇒ 记入"不可达格"备忘 + 上报"必须换目标"：
                //   目标格本身走不到时，换向/跳点都救不了，只有换目标或跳掉该路点两条路（都不许无限循环）。
                NotePathFail(to);
                InvalidatePath();
                WarnPathFailed(target, path == null
                    ? $"**两格都可走但位图不连通**（起点 {from} / 终点 {to}）—— 位图孤立分量（单层 2D 位图 + 多层几何）"
                    : $"A* 返回空路径（起点 {from} / 终点 {to}）");
                return false;
            }

            _path = path;
            _pathIndex = 0;
            _pathGoalCell = to;
            ClearPathFail();
            return true;
        }

        /// <summary>
        /// 记一次"这个终点格求不出路径"。同一格连续 <see cref="CsBotConst.PathFailStreakToUnreachable"/> 次之后：
        /// ① 记入 <see cref="_unreachableCell"/> 备忘（<see cref="ResolveNextTarget"/> 据此跳掉对应路点）；
        /// ② 立刻上报"卡住 + 必须换目标"（<c>_stuckEvent</c> 必须一起置位 —— 否则
        /// <see cref="ConsumeStuck"/> 会因 <c>_stuckEvent == false</c> 直接 return，escalate 永远送不到上层）。
        /// </summary>
        private void NotePathFail(Vector2Int to)
        {
            if (_pathFailCell != to)
            {
                _pathFailCell = to;
                _pathFailStreak = 0;
            }

            _pathFailStreak++;
            if (_pathFailStreak < CsBotConst.PathFailStreakToUnreachable) return;

            _hasUnreachableCell = true;
            _unreachableCell = to;

            _stuckEvent = true;          // 让 ConsumeStuck 真的把 escalate 送出去（见方法注释）
            _stuckEscalate = true;
            _lastStuckDistance = 0f;

            if (Time.time < _nextUnreachableLogAt) return;
            _nextUnreachableLogAt = Time.time + CsBotConst.StuckWarnCooldown;
            var c = CellCenter(to);
            Game.Logger.Warn(Tag,
                $"{_ownerName} 目标格 {to}（世界 {c.x:F1},{c.z:F1}）**与当前位置不连通**" +
                $"（连续 {_pathFailStreak} 次 AStar 求路径失败，两端都可走）→ 记入不可达备忘并上报换目标。" +
                "位图连通性问题：判据与数字见 tools/probes/astar-adj-diag.py / marker-connectivity.py");
        }

        /// <summary>求路径成功 ⇒ 清掉"连续失败"计数（备忘本身留到换路线时清，见 <see cref="InvalidateUnreachable"/>）。</summary>
        private void ClearPathFail()
        {
            _pathFailStreak = 0;
        }

        /// <summary>
        /// 沿 <see cref="_path"/> 取"这一帧该追的点"：**用"离下一个拐点是否比离当前拐点更近"推进游标**。
        ///
        /// <para>⛔ 不能拿 <see cref="CsBotConst.WaypointArriveRadius"/>（2.5m）当格子级的到达半径：
        /// 那是给稀疏标记点的，用在 1m 的格子上会一口气跳过好几个拐点 —— 路径就白求了
        /// （等于又退化成直线走）。</para>
        /// </summary>
        private Vector3 AdvancePath(Vector3 selfPosition, Vector3 target)
        {
            if (_path == null) return target;

            while (_pathIndex + 1 < _path.Count)
            {
                var dNow = FlatDistance(selfPosition, _path[_pathIndex]);
                var dNext = FlatDistance(selfPosition, _path[_pathIndex + 1]);
                if (dNext >= dNow) break;          // 还在往当前拐点走
                _pathIndex++;
            }

            return _pathIndex < _path.Count ? CellCenter(_path[_pathIndex]) : target;
        }

        /// <summary>
        /// A* 的 <c>walkable</c> 回调（契约见 <see cref="AStar"/> 的类注释：<c>Func&lt;Vector2Int,bool&gt;</c>，
        /// 8 邻接、对角要求两侧可走由 A* 内部保证）。查的是**格心**世界坐标是否可走。
        /// </summary>
        private bool WalkableCell(Vector2Int cell)
        {
            var map = Game.Map;
            if (map == null || map.CellSize <= 0f) return false;
            var c = CellCenter(cell);
            return _map != null ? _map.WalkableAt(c.x, c.z) : map.WalkableAt(c.x, c.z);
        }

        /// <summary>
        /// 把一个格坐标挪到最近的可走格（只看半径 ≤ <see cref="CsBotConst.PathSnapRadiusCells"/> 的那几环），
        /// 挪不动返回 false。
        /// <para>口径出处：<c>Assets/Editor/MapGen/MapConnectivityProbe.cs</c> 的 <c>SnapToWalkable</c>
        /// （逐环扩张搜最近可走格）——本类只把半径收小：角色半径 0.4m &lt; 1 格边长，`AStar.Find` 对"起点不可走"
        /// 直接判 null，snap 半径取 2 格足以覆盖"被挤进相邻格"的情形。</para>
        /// </summary>
        private bool SnapToWalkable(ref Vector2Int cell)
        {
            if (WalkableCell(cell)) return true;

            for (var r = 1; r <= CsBotConst.PathSnapRadiusCells; r++)
            {
                for (var dz = -r; dz <= r; dz++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) continue;   // 只看这一环（里环已经查过）
                        var c = new Vector2Int(cell.x + dx, cell.y + dz);
                        if (!WalkableCell(c)) continue;
                        cell = c;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 格坐标 → 格心世界坐标。
        /// <para>口径出处（逐字一致，⛔ 不是自定的换算）：<c>clover-client-unity-engine/Runtime/Presentation/MapFormat.cs:243-252</c>
        /// 的 floor 取整反解，与 <c>Assets/Editor/MapGen/Dust2GeoData.cs:201-203</c> 的 <c>CellCenter</c>：
        /// 格 (ix,iz) 的世界中心 = <c>(Origin.x + (ix+0.5)*CellSize, ·, Origin.z + (iz+0.5)*CellSize)</c>。</para>
        /// </summary>
        private static Vector3 CellCenter(Vector2Int cell)
        {
            var map = Game.Map;
            return new Vector3(map.Origin.x + (cell.x + 0.5f) * map.CellSize, 0f,
                               map.Origin.z + (cell.y + 0.5f) * map.CellSize);
        }

        /// <summary>世界坐标 → 格坐标（与 <c>MapFormat.cs:247-248</c> 逐字同口径：<c>FloorToInt((x-Origin)/CellSize)</c>）。</summary>
        private static Vector2Int CellOf(IMapData map, Vector3 p)
        {
            return new Vector2Int(Mathf.FloorToInt((p.x - map.Origin.x) / map.CellSize),
                                  Mathf.FloorToInt((p.z - map.Origin.z) / map.CellSize));
        }

        /// <summary>水平距离（**忽略 y**：格心 y 取 0，机器人可能站在高台上，比 3D 距离会把"同一格"判成很远）。</summary>
        private static float FlatDistance(Vector3 position, Vector2Int cell)
        {
            var c = CellCenter(cell);
            var dx = c.x - position.x;
            var dz = c.z - position.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>丢弃当前路径（下一次 <see cref="EnsurePath"/> 立即重求）。</summary>
        private void InvalidatePath()
        {
            _path = null;
            _pathIndex = 0;
            _nextRepathAt = 0f;
        }

        /// <summary>
        /// "求不出路径"必须留痕（⛔ 不许静默退化）。降频口径 = <see cref="CsBotConst.StuckWarnCooldown"/>
        /// （与"跳过不可走路点"同一个闸："这是一个 bot 每帧都会碰到的分支，只许按时间降频，不许每次打"）。
        /// <para>注：引擎 <see cref="AStar.Find"/> 自己也会按 <c>astar.badstart / badgoal / budget / nopath</c>
        /// 键降频打日志（见 <c>Runtime/Core/AStar.cs</c>），本条是**业务侧**的"我因此退化成了什么行为"。</para>
        /// </summary>
        private void WarnPathFailed(Vector3 target, string why)
        {
            if (Time.time < _nextPathFailLogAt) return;
            _nextPathFailLogAt = Time.time + CsBotConst.StuckWarnCooldown;

            Game.Logger.Warn(Tag,
                $"{_ownerName} 求路径失败（位图不可用 / 目标点不可达）：目标 ({target.x:F1},{target.z:F1})，" +
                $"路线 '{_marker ?? "无"}'，剩余路点 {RemainingWaypoints} → 退化为朝目标直线走（仍带局部避障）" +
                $"；具体原因：{why}");
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
            var far = position + dir * CsBotConst.ProbeDistance;
            if (_map.WalkableAt(near.x, near.z) && _map.WalkableAt(far.x, far.z)) return true;

            // ★ 片BL-R：位图说不行时用**物理**复核（位图是单层 2D，多层几何重叠处会误判 ⇒
            //   机器人被自己的位图判成"路上被挡"、原地不动；实测见 <see cref="PhysRunway"/> 的注释）。
            //   阈值 = 近端探距的一半（与 CsMatchConst.WallBlockVelocityRatio 同源的"走成了没有"口径）：
            //   近端都迈不过去 ⇒ 真的顶住了墙（1m 厚墙的用例照旧被拦住，物理会在墙前停下）。
            return PhysRunway(position, dir) >= CsBotConst.ProbeClearance * CsBotConst.PhysRunwayRatio;
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

            // ★ 片BL-R：位图与物理是**两套空间事实**，位图说"没跑道"时必须用物理复核。
            //   实测（.ai-tmp/test/bk-bot-phys.tsv，14888/14888 行）：卡住的 bot 用
            //   ICsMap.ResolveMove 朝目标迈 1m 的实际位移是 **1.000m**、8 个方向**全部迈得动**，
            //   而 ICsMap.WalkableAt 说这一带不可走（日志 101 条"跳过不可走的路点 (17.50, 0.68, 14.50)"）。
            //   位图是**单层 2D**（de_dust2.bytes 46 个连通分量 / 919 格非主分量），多层几何上下重叠处
            //   它给不出正确答案。只认位图 ⇒ 机器人被自己判成"路上被挡" ⇒ 原地不动（病灶）。
            var phys = PhysRunway(position, dir);
            if (run <= 0f && phys > 0f) WarnBitmapDisagrees();
            return Mathf.Max(run, phys);
        }

        /// <summary>
        /// 沿 <paramref name="dir"/> 的**物理**跑道：用与模拟同源的 <see cref="ICsMap.ResolveMove"/>
        /// 试迈 <see cref="CsBotConst.EscapeRunwayStep"/> 的整数倍，取"实际沿该方向走出来的距离"。
        ///
        /// <para>只算**投影到 dir 上的分量** —— ResolveMove 会分轴滑墙，若按位移模长算，
        /// "顶着墙侧滑"会被误判成"走得通"。</para>
        ///
        /// <para>验收阈值与模拟自身的撞墙判据同源：<c>CsMatchConst.WallBlockVelocityRatio</c>
        /// （<c>Module/Match/CsMatch.cs:128</c> = 0.5）：实际位移 &lt; 期望 × 0.5 即"没走成"。</para>
        /// </summary>
        private float PhysRunway(Vector3 position, Vector3 dir)
        {
            if (_map == null || !_map.IsLoaded) return CsBotConst.EscapeRunwayMax;

            var run = 0f;
            for (var d = CsBotConst.EscapeRunwayStep; d <= CsBotConst.EscapeRunwayMax + 0.001f;
                 d += CsBotConst.EscapeRunwayStep)
            {
                var resolved = _map.ResolveMove(position, position + dir * d);
                var along = (resolved.x - position.x) * dir.x + (resolved.z - position.z) * dir.z;
                if (along < d * CsBotConst.PhysRunwayRatio) break;
                run = along;
            }
            return run;
        }

        /// <summary>位图判"没跑道"、物理判"走得动"→ 留证一次（"站得下却不动"的直接病灶）。</summary>
        private void WarnBitmapDisagrees()
        {
            if (_warnedBitmapDisagrees) return;
            _warnedBitmapDisagrees = true;
            Game.Logger.Warn(Tag,
                $"{_ownerName} 可走位图说这一带没有跑道、物理（ResolveMove）却说走得动 —— " +
                "位图是单层 2D，多层几何重叠处会误判；本帧起以物理为准（判据见 BotNavigator.PhysRunway）");
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
