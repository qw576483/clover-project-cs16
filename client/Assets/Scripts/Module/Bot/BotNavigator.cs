using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人导航：**全局 A\* 路径** + 路点推进 + 简单避障 + 卡住检测 + **卡住自恢复**。
    ///
    /// <para><b>刻意不用 Unity NavMesh</b>：本工程的 <c>StageDust2</c> 场景没有烘焙 NavMesh，用了之后
    /// 在 <see cref="ICsMap.WalkableAt"/> 这张可走位图上求出的路径（**怎么绕过去**）。</para>
    ///
    /// <para><b>不自建寻路</b>：格子 A* 是引擎能力（<c>clover-client-unity-engine/Runtime/Core/AStar.cs</c>，
    /// 8 邻接 / 对角要求两侧可走 / octile 启发 / 视线拉直 <c>FindSmoothed</c> / <c>DefaultMaxNodes</c>），
    /// 只走直线绕不开整片墙 ⇒ 用法是"先求路径、再沿路径走"，**局部避障 <see cref="Avoid"/> 保留、两者叠加**。</para>
    ///
    /// <para><b>职责边界</b>：本类只输出"这一帧往哪个世界方向走"（已归一化、y=0）。
    /// 真正的位置解算（分轴滑墙 / 台阶 / 重力）由比赛模拟内部的 <c>ICsMap.ResolveMove</c> 负责 ——
    /// 机器人 AI 不执行移动。</para>
    ///
    /// <para><b>卡住自恢复（本类自己解决"这一帧怎么走"，"接下来去哪"交给上层）</b>：</para>
    /// <list type="number">
    /// <item>判定卡住 → 日志**按原因降频**（同一原因首次 + 每 <see cref="CsBotConst.StuckWarnEvery"/> 次各一条，
    /// 不再每 0.5s × 每个 bot 刷屏）；</item>
    /// <item>换向**真的换**：从期望方向向两侧按 <see cref="CsBotConst.EscapeSweepStepDegrees"/> 扫描，
    /// 取"前方跑道最长"的方向，并保持 <see cref="CsBotConst.EscapeHoldSeconds"/> 秒沿它走
    /// （只把探针偏角设成 90°、或靠路线游标自增换向，都会退化成"原地反复换向"）；</item>
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
        /// <summary>
        /// 攒够 <see cref="CsBotConst.AvoidBadDirSeconds"/> 才允许把锚点换掉 —— 见 <see cref="Avoid"/> 第 ① 段。
        /// </summary>
        private float _probeBadSince;
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
        private bool _warnedBitmapDisagrees;
        private Vector3 _lastDir;          // 上一次真正提交出去的方向（判"哪个方向走不动"用）

        private Vector3 _diagLastDir;      // 上一次提交出去的方向（判"翻转 >120°"用）
        private int _diagFlips;            // 本 bot 累计翻转次数（换路线时归零）
        private Vector3 _blockedDir;       // 最近一次"想走却走不动"的方向
        private float _blockedUntil;

        // ---- 全局路径（引擎 A*；消除差异 #77「业务零使用 A*」）----
        /// <summary>当前路径（引擎 <see cref="AStar"/> 的**格坐标**序列；null = 没有可用路径，退化为"朝目标直线走"）。</summary>
        private List<Vector2Int> _path;
        /// <summary>沿 <see cref="_path"/> 推进到的下标。</summary>
        private int _pathIndex;
        /// <summary><see cref="_path"/> 的终点格：终点格一变就必须重求（这就是"目标变了"的判据）。</summary>
        private Vector2Int _pathGoalCell;
        /// <summary>下一次最早允许重求路径的时刻（性能闸：不许每帧求路径）。</summary>
        private float _nextRepathAt;
        /// <summary>"求路径失败"日志的时间闸（见 <see cref="WarnPathFailed"/>）。</summary>
        private float _nextPathFailLogAt;
        /// <summary>本 bot 求路径成功的次数（"这条移动由 A* 给出"的计数）。</summary>
        private int _replanCount;

        /// <summary>
        /// 8 邻接的格偏移（与引擎 <see cref="AStar"/> 的邻接一致：4 正 + 4 斜）。只用于
        /// <see cref="BuildHeightReach"/> 的高度可达扩张（不是寻路）。
        /// </summary>
        private static readonly int[] CellStepX = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly int[] CellStepZ = { 0, 1, 1, 1, 0, -1, -1, -1 };

        /// <summary>
        /// <see cref="_heightReachCache"/> 的条目上限（**内存护栏**，不是玩法阈值）：一个条目 = 一份可达集
        /// （实测主分量 ~4.4k 格 ⇒ <c>HashSet&lt;Vector2Int&gt;</c> ≈ 140 KB）⇒ 64 条 ≈ 9 MB。
        /// 满了**淘汰最旧的那一条**（不是整表清空）：键不再被用到就自然被挤掉。
        /// </summary>
        private const int HeightReachCacheCap = 64;

        /// <summary>
        /// 可达集的备忘键 = **起点格 + 该格的基准落脚高度**（后者 = <c>SampleGround(位置)</c> 再经
        /// <see cref="SnapStartFace"/> 吸附；两者都只是"位置 / 格 → 数"的纯函数）。
        /// 扩张（<see cref="BuildHeightReach"/>）从头到尾只依赖这两个量 + 世界几何（比赛内不变）
        /// ⇒ 同一个键命中的结果与"不缓存直接重算"**逐值相同**。
        /// </summary>
        private readonly struct HeightKey : System.IEquatable<HeightKey>
        {
            public readonly int X, Y, BaseBits;

            public HeightKey(Vector2Int cell, float baseY)
            {
                X = cell.x;
                Y = cell.y;
                BaseBits = System.BitConverter.SingleToInt32Bits(baseY);
            }

            public bool Equals(HeightKey o) => X == o.X && Y == o.Y && BaseBits == o.BaseBits;
            public override bool Equals(object o) => o is HeightKey k && Equals(k);
            public override int GetHashCode() => ((X * 397) ^ Y) * 397 ^ BaseBits;
        }

        /// <summary>备忘条目：那份可达集 + 最近一次被用到的序号（淘汰最旧用）。</summary>
        private struct ReachEntry
        {
            public HashSet<Vector2Int> Set;
            public long Used;
        }

        /// <summary>
        /// <see cref="CanReach"/> 的布尔备忘键 = 起点格 + 终点格 + **判据本身**（可达集对象引用；null = 本帧无高度层）。
        /// <c>AStar.Find</c> 的结果只依赖 (walkable 回调, 起点格, 终点格, maxNodes)，而 walkable 回调由
        /// 位图（随地图）+ <c>_hasHeightReach</c> + 那一份可达集唯一确定 ⇒ 同一个键的结果与重算逐值相同。
        /// </summary>
        private readonly struct ReachQuery : System.IEquatable<ReachQuery>
        {
            public readonly int FX, FY, TX, TY;
            public readonly HashSet<Vector2Int> Reach;

            public ReachQuery(Vector2Int from, Vector2Int to, HashSet<Vector2Int> reach)
            {
                FX = from.x; FY = from.y; TX = to.x; TY = to.y; Reach = reach;
            }

            public bool Equals(ReachQuery o)
                => FX == o.FX && FY == o.FY && TX == o.TX && TY == o.TY && ReferenceEquals(Reach, o.Reach);

            public override bool Equals(object o) => o is ReachQuery q && Equals(q);

            public override int GetHashCode()
                => (((FX * 397) ^ FY) * 397 ^ TX) * 397 ^ TY
                   ^ (Reach != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Reach) : 0);
        }

        /// <summary><see cref="_canReachMemo"/> 的条目上限（内存护栏）：一个条目只有两个格 + 一个 bool，满了整表清空。</summary>
        private const int CanReachMemoCap = 8192;

        /// <summary>
        /// "从当前位置按**抬升 ≤ <see cref="CsConst.StepUpHeight"/>** 逐格走得到"的格集合
        /// （= 高度一致性层；见 <see cref="BuildHeightReach"/>）。null / <see cref="_hasHeightReach"/> == false
        /// </summary>
        private HashSet<Vector2Int> _heightReach;
        private Vector2Int _heightReachFrom;
        /// <summary>当前这份 <see cref="_heightReach"/> 对应的基准落脚高度（与 <see cref="_heightReachFrom"/> 合成备忘键）。</summary>
        private int _heightReachBaseBits;
        private bool _hasHeightReach;
        /// <summary>
        /// 共享的"可达集"备忘（键 = 起点格 + 基准落脚高度，见 <see cref="HeightKey"/>）。
        /// 结论只依赖 (地图, 起点格, 基准高度, 世界几何) 四个量、与是哪个 bot 无关 ⇒ 同一张地图上
        /// 所有 <see cref="BotNavigator"/> **共用一份**：同一个键 ⇒ 与"不缓存重算"逐值相同的结果；
        /// 满了淘汰**最旧**一条（<see cref="ReachEntry.Used"/>）。
        /// </summary>
        private sealed class ReachTables
        {
            /// <summary>本表对应的地图（换图 ⇒ 换一份表）。</summary>
            public IMapData Map;
            public readonly Dictionary<HeightKey, ReachEntry> Cache =
                new Dictionary<HeightKey, ReachEntry>(HeightReachCacheCap);
            /// <summary>LRU 时刻（<see cref="ReachEntry.Used"/> 的比较基准）。</summary>
            public long Used;
        }

        /// <summary>当前地图的可达集备忘（<see cref="BuildHeightReach"/> 里按地图建立 / 替换）。</summary>
        private static ReachTables _sharedReach;
        private static ReachTables Reaches => _sharedReach;

        /// <summary><see cref="CanReach"/> 的布尔备忘（键见 <see cref="ReachQuery"/>）；满了整表清空（只是多算一次，结论不变）。</summary>
        private readonly Dictionary<ReachQuery, bool> _canReachMemo = new Dictionary<ReachQuery, bool>(1024);
        private IMapData _canReachMemoMap;
        /// <summary>"高度层这次为何没生效"的时间闸（同原因 <see cref="CsBotConst.StuckWarnCooldown"/> 内只报一条）。</summary>
        private float _nextHeightLogAt;

        /// <summary>
        /// <see cref="ResolveNextTarget"/> 的"这个路点走得到吗"备忘：同一路点下标 + 同一所在格只在
        /// <see cref="CsBotConst.PathReplanInterval"/> 秒内问一次 <see cref="CanReach"/>
        /// （<see cref="CanReach"/> 内含一次 A*，不许每帧调）。
        /// </summary>
        private int _reachCheckedIndex = -1;
        private Vector2Int _reachCheckedFrom = new Vector2Int(int.MinValue, int.MinValue);
        private Vector2Int _reachCheckedCell = new Vector2Int(int.MinValue, int.MinValue);
        private bool _reachCheckedOk;
        private float _nextReachCheckAt;

        /// <summary>
        /// <see cref="IsTrapCell"/> 的备忘（键 = 格；值 = 该格所在的位图连通分量是否 ≤
        /// <see cref="CsBotConst.TrapComponentCells"/>）。格心查询是热路径（每帧 ≤ 24 个方向候选），
        /// 而分量格数只需数一次。
        /// </summary>
        private readonly Dictionary<Vector2Int, bool> _trapCellCache = new Dictionary<Vector2Int, bool>(256);
        /// <summary><see cref="_trapCellCache"/> 的条目上限（**内存护栏**，不是玩法阈值）：满了整表清空。</summary>
        private const int TrapCellCacheCap = 4096;
        /// <summary><see cref="_trapCellCache"/> 对应的地图（换图 ⇒ 缓存作废）。</summary>
        private IMapData _trapCachedMap;

        /// <summary>
        /// **整格口径**的多点取样偏移（**格边长的比例**，升序；`0f` = 格心）。
        /// 取 ±0.4 而**不是** ±0.5：±0.5 正好落在格边上，格归属有歧义（同一个世界点会被相邻两格各算一次），
        /// 而 ±0.4 保证 25 个点都严格落在**本格内**。
        ///
        /// <para><b>与位图生成侧口径的差异（必须写明）</b>：位图生成侧**根本不取地面** ——
        /// <c>Assets/Editor/MapGen/Dust2GeoData.cs:213-247 BuildBlockedBitmap()</c> 判的是
        /// "格柱 y∈[GroundTopY+ProbeBottomY, GroundTopY+ProbeTopY] 与障碍 AABB 相交 ⇒ 阻挡"（**整格柱体**语义）；
        /// 而运行时的消费方（<see cref="CellCenter"/> / <see cref="GroundYAbove"/> /
        /// <c>ICsMap.TrySampleGround</c>）只在**格心**取样 ⇒ 两侧口径不一致，产生"位图上连通、运行时断"的伪连通边
        /// （实证：格 <c>(49,17)</c> 格心 8 m 内探不到地面，5×5 只在格角命中 <c>y=8.941</c>）。
        /// 生成侧没有可复用的"格内采样点"，所以这里用**格内 5×5** 近似"整格"：只要格内**任一点**存在带内合法
        /// 落脚面，这一格就按"能站"处理（= 生成侧的整格语义）。</para>
        /// </summary>
        private static readonly float[] InCellFrac = { 0f, -0.2f, 0.2f, -0.4f, 0.4f };
        /// <summary><see cref="InCellFrac"/> 里 `0f` 的下标（= 格心那一档；多点循环里跳过，因为格心已经试过）。</summary>
        private const int InCellCenter = 0;
        /// <summary>带内下界用的探测深度 = <c>CsMap.SampleGround</c> 的默认 <c>maxDrop</c>（Module/Map/CsMap.cs:534）。</summary>
        private const float GroundProbeDepth = 8f;

        /// <summary>起点格吸附落脚面时的上下搜索带（米）。只作用于**起点那一格**：边判据 / 可站立判据 / 探测深度都不受它影响。</summary>
        private const float StartFaceSnapBand = 2.5f;
        private int _mpCenterFail, _mpSaved, _mpDead;

        // ==================================================================
        //  「格内采样点 → 该列有哪些向上的面」备忘（把逐格射线换成查表）
        // ==================================================================
        //
        // 本层不改任何判据，只改"面从哪来"：原来每一格都现场打一根向下射线，现在同一格内采样点
        // 只枚举一次、把该列的面向下有序记下来，之后按 `origin.y` 直接取"起点之下最高的那个面" ——
        // 这正是 `Physics.Raycast(origin, Vector3.down, …, maxDrop + GroundCheckDistance)` 的定义
        // （向下射线只命中"从上方看得到"的面，竖面与向下射线平行、不参与；`queriesHitBackfaces`
        //  默认 false ⇒ 盒体底面的背面不被命中，所以"人站在盒体内部"那种情形与现场射线一致）。
        // 于是命中高度、命中与否、法线 y 三个字段与现场射线逐值相同，调用方的带内判据一个字没动。
        //
        // 表按 (格, 格内采样点下标) 建，键与 `CellCenter + InCellFrac[i/k] × CellSize` 一一对应，
        // 所以 `SnapStartFace` / `GroundYAbove` / `CellLandingFace` 三处共用同一份。
        // 世界几何（CsWorld 层，见 `Module/Map/CsMap.cs` 的 `GroundMask`）在比赛内不变。

        /// <summary>一列记忆的"向上的面"个数上限；到顶 ⇒ 记 <see cref="Column.Overflow"/>、该列走现场射线。</summary>
        private const int ColMaxFaces = 6;
        /// <summary>枚举一列时的第一根射线起点（世界 y，取在整张图所有面之上）。</summary>
        private const float ColProbeTopY = 128f;
        /// <summary>枚举时单根射线的射程（米）：从 <see cref="ColProbeTopY"/> 起要能一路探到图底。</summary>
        private const float ColProbeDrop = 400f;
        /// <summary>枚举下一根射线相对上一个命中点的下移量（米）：正好贴面起步会自交，下移一点跳过它。</summary>
        private const float ColFaceEps = 0.01f;
        /// <summary>
        /// <see cref="ColumnTables.Insets"/> 的条目上限（内存护栏，不是玩法阈值）：满了**不再新增**条目
        /// （已建好的照旧命中，新列当场枚举、不入表）—— 整表清空会让每个 bot 重新枚举同一批列。
        /// </summary>
        private const int ColCacheCap = 400000;
        /// <summary><see cref="Cols.State"/> 里"该列面数超上限"的标记（这一列回退现场射线）。</summary>
        private const byte ColStateOverflow = 0xFF;

        /// <summary>一个格内采样点所在竖直列上的"向上的面"，按 y **降序**。</summary>
        private struct Column
        {
            public int Count;
            public bool Overflow;
            public float Y0, Y1, Y2, Y3, Y4, Y5;
            public float Ny0, Ny1, Ny2, Ny3, Ny4, Ny5;
        }

        /// <summary>
        /// 共享的"列面表"：列面只依赖 (地图, 格, 格内采样点) 三个量，**与是哪个 bot 无关** ⇒
        /// 同一张地图上所有 <see cref="BotNavigator"/> 共用一份，取到的值逐字相同（少打的是重复射线）。
        /// </summary>
        private sealed class ColumnTables
        {
            /// <summary>本表对应的地图（换图 ⇒ 换一份表）。</summary>
            public IMapData Map;
            /// <summary>位图尺寸各加 1 格外框（= <see cref="GeoIdx"/> 的步长 / 行数）。</summary>
            public int W, D;
            /// <summary>格**心**列 × 格 → 面数 + 1；0 = 未建立，<see cref="ColStateOverflow"/> = 溢出。</summary>
            public byte[] State;
            public float[] Y;
            public float[] Ny;
            /// <summary>格**内**其余 24 个采样点的列（只有"格心失败"的格才会用到），键见 <see cref="ColumnKey"/>。</summary>
            public readonly Dictionary<long, Column> Insets = new Dictionary<long, Column>(8192);
        }

        /// <summary>当前地图的列面表（<see cref="EnsureGeoArrays"/> 按地图建立 / 替换）。</summary>
        private static ColumnTables _sharedCols;
        /// <summary>本实例上次见到的列面表（表一换 ⇒ 本实例的扩张代次作废）。</summary>
        private ColumnTables _colsSeen;

        private static ColumnTables Cols => _sharedCols;

        /// <summary>扩张时每格的最小落脚高度 / 代次（按格下标索引，避免逐格哈希表操作）。</summary>
        private float[] _gH;
        private int[] _gStamp;
        /// <summary>可达集（按格下标索引）：值 == <see cref="_gGen"/> ⇒ 本代可达（= <see cref="WalkableCellHeightAware"/> 的判据）。</summary>
        private int[] _gReach;
        private int _gGen;
        /// <summary>上面这些数组的步长/行数（= 位图尺寸各加 1 格外框）。</summary>
        private int _gW, _gD;
        private readonly Queue<Vector2Int> _gQueue = new Queue<Vector2Int>(8192);

        /// <summary><see cref="_walkBitmap"/> 对应的地图。</summary>
        private IMapData _walkMap;
        /// <summary>位图展开成数组（键 = <c>iz * Width + ix</c>）：与 <c>IMapData.WalkableAt(格心)</c> 同值。</summary>
        private bool[] _walkBitmap;
        private int _walkW, _walkD;

        /// <summary>
        /// 最近一次被判为"**与起点格不连通**"的终点格（`AStar.Find` 在两端都可走时仍返回 null）。
        ///
        /// <para>为什么需要这份备忘：位图可以有好几块互不连通的区域，而**标记点/路线路点只判过"可走"、
        /// 没判过"走得到"**（生成侧 <c>SnapMarkerToWalkable</c> 与运行侧 <c>ICsMap.CanStand</c> 都只管本格的
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

        /// <summary>
        /// 当前路线的**第一段路点**（= 从设置路线那一刻的起点做"最近邻排序"后的 `_route[0]`）。
        ///
        /// <para>为什么要暴露它：
        /// 验收判据写的是「**同一回合内任意两个同队 bot 的首段路点不同**」——
        /// 判据必须读**真的那条路的第一段**（含 `SetRoute` 的最近邻排序 + 两层过滤的结果），
        /// 不能拿"路线标记名不同"当近似（两只 bot 可以标记不同却在同一格起走，也可以标记相同却岔开）。</para>
        /// <para>只读：不改变游标、不触发重排。路线为空时返回 <see cref="Vector3.zero"/>，
        /// 用 <see cref="HasFirstWaypoint"/> 先判有没有。</para>
        /// </summary>
        public Vector3 FirstWaypoint => _route.Count > 0 ? _route[0] : Vector3.zero;

        /// <summary><see cref="FirstWaypoint"/> 是否有意义（路线为空 ⇒ false）。</summary>
        public bool HasFirstWaypoint => _route.Count > 0;

        /// <summary>
        /// 当前路线**整条有序序列**的签名（"x1,z1;x2,z2;…"，1 位小数；空路线 = "EMPTY"）。
        ///
        /// <para>为什么要它：判据写的是"同队两只 bot 的**首段**路点不同"，但本工程
        /// 的地图标记数据里 **T 队三条路共用同一个岔口点** <c>(-7.5, 3.251, -47.5)</c>
        /// （`Resources/MapData/de_dust2_markers.bytes`：Route_T_To_A ∩ Route_T_Mid ∩ Route_T_To_B 都是它，
        /// 离 T 出生点最近 ⇒ 最近邻排序后必然排在第一位）⇒ "首段不同"在 T 队**任何代码都做不到**，
        /// 序列必然不同；两条路线完全相同则签名相同）。首段路点仍照打（观测列）。</para>
        /// <para>只给判据用（探针只读），不参与任何运行时决策；每次调用都重新拼串（低频）。</para>
        /// </summary>
        public string RouteSignature
        {
            get
            {
                if (_route.Count == 0) return "EMPTY";
                var sb = new System.Text.StringBuilder(_route.Count * 12);
                for (var i = 0; i < _route.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(_route[i].x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(',')
                      .Append(_route[i].z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        /// <summary>当前路线的路点数（= <c>_route.Count</c>，含已走完的）。</summary>
        public int WaypointCount => _route.Count;
        /// <summary>路线有路点且已经全部走完（= "剩余路点 0"）。此时继续"跳过路点"是空操作，必须由上层换目标。</summary>
        public bool RouteExhausted => _route.Count > 0 && _index >= _route.Count;

        public void SetOwner(string name)
        {
            if (!string.IsNullOrEmpty(name)) _ownerName = name;
        }

        /// <summary>
        /// 绑定地图（**不设路线**也要绑）。必要性：<see cref="Avoid"/> / <see cref="Runway"/> / 路点可走性检查
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
            // 同一条理由也适用于"这个路点走不到"的备忘（见 SkipUnreachableWaypoint）：
            // 它是**当前这条路线 + 当前这个路点下标**的结论，路线一换就必须重问。
            _reachCheckedIndex = -1;
            _reachCheckedFrom = new Vector2Int(int.MinValue, int.MinValue);
            _reachCheckedCell = new Vector2Int(int.MinValue, int.MinValue);
            _nextReachCheckAt = 0f;
        }

        /// <summary>
        /// 设置路线：取地图标记点，并按"离自己最近优先"排序成一条可推进的顺序。
        ///
        /// <para>为什么要排序：<see cref="ICsMap.Points"/> 只保证"这些点属于这条路线"，不保证数组顺序就是推进顺序；
        /// 最近邻排序对"乱序"和"已排序"两种情况都成立（已排序时结果一致），且不会来回震荡。</para>
        ///
        /// <para>标记缺失（空数组）→ 打 Error 并退化：<see cref="ComputeMove"/> 会直接朝目标点直线走，
        /// **不许站着不动**。</para>
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

            //
            // 为什么要有这一层：标记点的坐标是**数据**（生成侧已在 Dust2Builder.DumpMarkers 里
            // 把"落在阻挡格上"的点吸附到最近可走格心，见 Dust2Builder.SnapMarkerToWalkable），而
            // `CanStand` 是**运行时**才有的判据（位图 8 向 + 地面一步台阶 + 身体高度带几何复核）——
            // 只有它知道"这一格上是否真的站得下一个 radius 半径的人"。两者不一致时（地图资产过期、
            // 兜底排掉 = 让路线只含"真正能站的格"，而不是到地方才发现过不去。
            //
            // 不许**静默**排点：每有一个点被排掉都必须留痕（降频，见下）。
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
        /// 换目标时设置路线：**路线标记没变就保留既有顺序与游标**；标记变了（或还没有路线）才走
        /// <see cref="SetRoute"/> —— 即"取标记点 + 最近邻排序 + 游标归零"。
        ///
        /// <para>路线的推进顺序是**路线数据的属性**，与自己此刻站在哪一格无关。若每次换目标都按"离当前位置
        /// 最近优先"重排并把 <c>_index</c> 归零，刚走过、在身后的那个路点会排到最前 ⇒ 机器人掉头；
        /// 巡逻 / 长路线于是每换一次目标就重走一遍。</para>
        ///
        /// <para>游标只在**路线已走完**（<see cref="RouteExhausted"/>）时归零：同一条路线再次被选中时从
        /// 它自己的第一个路点继续推进（顺序照旧），而不是"回头找离自己最近的路点"。</para>
        /// </summary>
        public void SetRouteKeepProgress(ICsMap map, string marker, Vector3 fromPosition)
        {
            if (!string.IsNullOrEmpty(marker) && marker == _marker && _route.Count > 0)
            {
                if (_index >= _route.Count) _index = 0;
                _stuckSeconds = 0f;
                _hasCheckPos = false;
                ClearEscape();
                ClearStuckState();
                InvalidatePath();
                return;
            }

            SetRoute(map, marker, fromPosition);
        }

        /// <summary>
        ///
        /// <para><b>为什么必须有这一层</b>：生成侧 <c>SnapMarkerToWalkable</c> 与运行侧 <see cref="ICsMap.CanStand"/>
        /// 都只判"这一点**站得住 / 可走**"，**不判"走得到"**；而引擎 <see cref="AStar"/> 是**格子连通性**查询。
        /// 该位图 **46 个连通分量**，主分量（含 CT/T 出生点、A/B 包点）4393 格，其余 45 个分量共 919 格；
        /// **11/117 个标记点、10/35 条相邻路点对**落在这些孤岛里。路点一旦落在孤岛里，
        /// <see cref="EnsurePath"/> 就**永远**返回 false ⇒ 退化成"朝它直线走" ⇒ 每 0.5s 判一次卡住 ⇒
        /// 无限循环（实测 373s 里 CT 进点 0、除真人外无 actor 位移 &gt; 5.8m）。</para>
        ///
        /// <para><b>判据与运行时同源</b>：直接调引擎 <see cref="AStar.Find"/> + 本类同一个 <see cref="WalkableCell"/>
        /// 推进顺序 = 上面刚排好的顺序（从机器人当前位置逐个走）。</para>
        ///
        /// <para>不许静默排点（降频 Warn，逐点给格号）；"整条路线全被排掉"时**退回未过滤的路线**并打 Error
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

        /// <summary>
        /// **原地重新求路径**：只作废 A* 路径与卡住/逃逸状态，**路线顺序与游标一字不动**。
        ///
        /// <para><b>为什么要它</b>：上层 <c>CsBotBrain.MaybeRepath</c> 只想"用当前位置重求一次路径"。
        /// 若改走 <see cref="SetRoute"/>，它会 ① 按"离**当前位置**最近优先"**重排整条路线**、
        /// ② `_index = 0` **把游标归零** ⇒ 机器人一旦因为绕箱子/上坡在 2 个 1s 窗口内没有靠近
        /// **最终目标**（`RepathProgressEpsilon`），就被"重排 + 归零"一次 ⇒ 最近的（可能是刚走过的）
        /// 路点排到最前 ⇒ 掉头（实测单次 Play 里一条路线可重排几十次、剩余路点列在 3→2→3 之间反复）。</para>
        ///
        /// <para>不重排顺序 = 保持既有推进方向；不丢连通性兜底：下一个 <see cref="EnsurePath"/>
        /// 仍会走同一套 <see cref="SnapToWalkable"/> + <see cref="WalkableCellHeightAware"/> + 引擎 A*。</para>
        /// </summary>
        public void RefreshPathOnly(Vector3 fromPosition)
        {
            if (_route.Count == 0) return;
            InvalidatePath();
            ClearEscape();
            ClearStuckState();
            _hasCheckPos = false;
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
            InvalidateUnreachable();   // 路线没了 ⇒ 上一条的"哪一格不可达"结论作废
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
                    DiagFlip(selfPosition, desired, _escapeDir, goal, target, "escape-hold-return", now);
                    return _escapeDir;
                }

                // 逃逸方向也被堵住了 → 立刻弃用，回到正常避障（否则会顶着墙把 1.2s 耗完）
                ClearEscape();
            }

            var branch = "avoid";
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

                branch = "avoid+stuck-escape";
                dir = BeginEscape(selfPosition, desired, now);
            }

            if (dir.sqrMagnitude > 0.5f) _lastDir = dir;
            DiagFlip(selfPosition, desired, dir, goal, target, branch, now);
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
        /// <summary>
        /// 这一帧追哪个点：先按既有口径推进"路线点"（<see cref="ICsMap.Points"/> 的标记点），
        /// 再在**可走位图**上用引擎 <see cref="AStar"/> 求一条路径，返回路径上的下一个拐点。
        ///
        /// <para><b>为什么要用 A*</b>：只按最近邻排出一条路线、再逐点走**直线**的话，直线不看几何 ——
        /// 机器人会贴着墙角磨、绕不开整片墙。"绕开墙"交给引擎 <c>Runtime/Core/AStar.cs</c>
        /// （A* + 视线拉直）。</para>
        ///
        /// <para><b>与 <see cref="Avoid"/> 的分工（两者叠加，缺一不可，不是二选一）</b>：
        /// ① **全局** = 本方法 + <see cref="EnsurePath"/>：用 <see cref="AStar.FindSmoothed"/> 在可走位图上
        /// 求"从我在哪到目标该走哪几格"，解决"绕开整片墙 / 走哪条通道"；
        /// ② **局部** = <see cref="Avoid"/>：A* 只给"格子级"的通行方向，1m 格内仍会贴墙角、被别的角色挤住、
        /// 被<see cref="ICsMap.WalkableAt"/> 说可走而物理走不动的落差卡住 —— 那一段由前向试探 / 偏角 /
        /// 跑道扫描 / 逃逸负责。A* 看不见"这一帧被谁挤住"，<see cref="Avoid"/> 不知道"该绕远路"。</para>
        ///
        /// <para><b>退化口径</b>：不许比既有实现更差：地图位图未加载 / 起点终点都 snap 不到可走格 /
        /// 不可达（<see cref="AStar.Find"/> 返回 null）⇒ 走**既有**的"朝标记点（或 goal）直线走"，
        /// 并留一条降频 Warn（见 <see cref="WarnPathFailed"/>，不许静默）。</para>
        ///
        /// <list type="number">
        /// <item>被追的路点若已被判为**不连通**（见 <see cref="NotePathFail"/> / <see cref="_unreachableCell"/>）
        /// ⇒ **跳过它**推进到下一个走得到的路点（与"跳过不可走路点"同一形状）。这是"CT 进点 0"的直接前提：
        /// 一直追着孤岛里的那个路点只会原地打转，路线根本没机会往下走。</item>
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

                //   "可走"（`WalkableAt` 说这一格能站）与"走得到"（A* 求得出路径）是两件事：
                //   实测 `Route_CT_Mid[1]`=(80,93) 世界 (17.5,21.5) 落在 **19 格的孤立分量**里（主分量 4393 格）
                //   —— 这一点可走，却永远走不到。朝它直线走 ⇒ 每 0.5s 判一次卡住（位移 0.00m）⇒
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

                //   第三道：位图说"可走"，但从**当前格按导航口径**（SnapToWalkable + 高度一致性层 +
                //   引擎 AStar）求不出路径的路点。它与上面那条备忘（靠连续失败攒够才记）覆盖的是同一件事，
                //   区别是**在退化之前就跳掉**：追一条走不到的路点会先退化成"朝它直线走"，被反复判卡住
                //   （<see cref="CsBotConst.StuckCheckInterval"/> 一次的位移判卡）⇒ 上报"必须换目标"⇒
                //   上层换目标时会把整条路线按最近点重排、游标归零 ⇒ 又走回刚路过的那个路点 ⇒
                //   同一个"追不到 → 换目标 → 走回去"的环，整回合净位移只有十几米。
                //   判据与 <see cref="EnsurePath"/> 逐字同一份（<see cref="CanReach"/>），阈值一个都没改。
                if (SkipUnreachableWaypoint(selfPosition, wp))
                {
                    if (Time.time >= _nextSkipLogAt)
                    {
                        _nextSkipLogAt = Time.time + CsBotConst.StuckWarnCooldown;
                        var cc = CellOf(Game.Map, wp);
                        var wc = CellCenter(cc);
                        Game.Logger.Warn(Tag,
                            $"{_ownerName} 跳过**导航口径走不到**的路点 {wp}（格 {cc}，世界 {wc.x:F1},{wc.z:F1}；" +
                            $"路线 '{_marker}'，第 {_index + 1}/{_route.Count} 个）—— 位图判可走却求不出路径" +
                            "（高度一致性层 / 连通性口径），追它只会先退化成直线走再被换目标（判据 = BotNavigator.CanReach）");
                    }
                    _index++;
                    continue;
                }

                break;
            }

            var target = _index < _route.Count ? _route[_index] : goal;

            // 全局路径可用 → 追路径上的下一个拐点；不可用 → 追目标本身（并留 Warn，见 WarnPathFailed）。
            if (EnsurePath(selfPosition, target)) return AdvancePath(selfPosition, target);

            return target;
        }

        /// <summary>
        /// "这个路点按导航口径走得到吗"的**降频**查询（<see cref="ResolveNextTarget"/> 的第三道跳点用）。
        ///
        /// <list type="bullet">
        /// <item>口径 = <see cref="CanReach"/>，与 <see cref="EnsurePath"/> 逐字同一份（同一份
        /// <see cref="SnapToWalkable"/>、同一份 <see cref="WalkableCellHeightAware"/>、同一个引擎
        /// <see cref="AStar"/>）——本方法只是"问一句"的外壳，判据一个字没改；</item>
        /// <item>只在该路点**下标或自己所在格变化**时才重问，且两次之间至少隔
        /// <see cref="CsBotConst.PathReplanInterval"/>（<see cref="CanReach"/> 内含一次 A*，
        /// 不许进每帧热路径）；</item>
        /// <item>退化口径（一律返回 false = 不跳，行为同既有）：地图不可用 / 自己所在格附近没有可走格 /
        /// 该路点格位图判**不可走**（那是 <see cref="ResolveNextTarget"/> 上一条分支的事）。</item>
        /// </list>
        /// </summary>
        private bool SkipUnreachableWaypoint(Vector3 selfPosition, Vector3 wp)
        {
            var map = Game.Map;
            if (map == null || !map.Loaded || map.CellSize <= 0f) return false;
            if (_map == null || !_map.IsLoaded) return false;

            var from = CellOf(map, selfPosition);
            var cell = CellOf(map, wp);
            if (!SnapToWalkable(ref from)) return false;        // 自己被卡在位图外 ⇒ 不是路点的问题
            if (!WalkableCell(cell)) return false;              // 位图判挡 ⇒ 交给"跳过不可走的路点"那条

            if (_reachCheckedIndex == _index && _reachCheckedFrom == from && _reachCheckedCell == cell
                && CsClock.Now < _nextReachCheckAt)
            {
                return !_reachCheckedOk;                        // 命中备忘
            }

            _reachCheckedIndex = _index;
            _reachCheckedFrom = from;
            _reachCheckedCell = cell;
            _nextReachCheckAt = CsClock.Now + CsBotConst.PathReplanInterval;
            _reachCheckedOk = CanReach(selfPosition, wp);
            return !_reachCheckedOk;
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

            if (_path != null && _pathGoalCell == to && _pathIndex < _path.Count && CsClock.Now < _nextRepathAt)   // replan gate is decision timing; not injected it is literally Time.time
            {
                return true;                       // 沿用既有路径（时间闸未到）
            }

            _nextRepathAt = CsClock.Now + CsBotConst.PathReplanInterval;

            //   位图本体与 `ICsMap.WalkableAt` / `CsMap.WalkableAt` 的语义**一字不动**（玩家行为零影响）。
            //   失败（位图不可用 / 自己脚下探不到地面 / 可达规模超上限）⇒ 不加这一层，行为同既有。
            var heightOk = BuildHeightReach(from, selfPosition) != null;

            // `FindSmoothed` = `Find`（8 邻接、对角要求两侧可走）+ 视线拉直（拐角变成长直线，正合走位）。
            // 节点上限用引擎既有的 `DefaultMaxNodes`（不自己加魔法数）。
            var path = AStar.FindSmoothed(WalkableCellHeightAware, from, to, AStar.DefaultMaxNodes);
            if (path == null || path.Count == 0)
            {
                //   起/终点会**先**返回 null 并打 `badstart`/`badgoal`），所以这一支的真实含义是
                //   **「两格都可走，但位图上不连通」**（`AStar.cs:130` 的 `astar.nopath`）——
                //   与"位图不可用""起终点不可走"是三件不同的事，不许混在一句日志里（旧日志写
                //   "位图不可用 / 目标点不可达"，实测会把人引到错误的方向）。
                //   连续 `PathFailStreakToUnreachable` 次同端点求不出 ⇒ 记入"不可达格"备忘 + 上报"必须换目标"：
                //   目标格本身走不到时，换向/跳点都救不了，只有换目标或跳掉该路点两条路（都不许无限循环）。
                NotePathFail(to);
                InvalidatePath();
                //   引擎 `AStar.Find` 会在 `walkable(to) == false` 时**先**返回 null（键 `astar.badgoal`）——
                //   而这里的 `walkable` = `WalkableCellHeightAware`，所以"终点不可走"并不等于"位图把这一格标成阻挡格"，
                //   它同样可能是"高度一致性层没扩展到这一格"。实测（br-hold-plant-log.tsv）13 次 badgoal 全被旧日志
                //   写成"位图孤立分量"，而真·孤立分量只有 18 次 ⇒ 修 bug 的人会去修位图、修不到真正的门。
                var rejectedByHeight = heightOk && !WalkableCellHeightAware(to);
                WarnPathFailed(target, rejectedByHeight
                    ? $"**终点格在高度一致性层里不存在**（终点 {to}：位图判可走 = {WalkableCell(to)}，" +
                      $"但从起点 {from} 起按抬腿 ≤ {CsConst.StepUpHeight:F2}m 扩张到不了它）—— 该点多半被摆在抬升面/墙里" +
                      "（判据见 tools/probes/bu-goal-reachability.py）。选目标时应先用 BotNavigator.CanReach 过滤"
                    : (path == null
                        ? $"**两格都可走但位图不连通**（起点 {from} / 终点 {to}）—— 位图孤立分量（单层 2D 位图 + 多层几何）"
                          + HeightVerdict(from, to, heightOk)
                        : $"A* 返回空路径（起点 {from} / 终点 {to}）"));
                return false;
            }

            //
            // 引擎 `AStar.Find` 返回的路径 `[0]` 就是**起点格自己**（`AStar.cs:95` 的 Reconstruct 从 `from`
            // 重建，`Smooth` 又把 `path[0]` 原样放进 `result[0]`，见 `AStar.cs:160`），而本类的 `from`
            // 就是"我现在这一格"（`CellOf(map, selfPosition)`）⇒ `_path[0]` 恒等于"我脚下的格心"。
            //
            // **永远不成立**：下一个拐点在 8~10m 外，永远比"我自己脚下的格心"远 ⇒ 游标整条路径生命周期内
            // 钉在第 0 个节点上 ⇒ 机器人一直在追"自己脚下的格心"：朝格心走 → 越过格心 2cm（`ComputeMove`
            // 的 0.0004 = 0.02m 死区）→ 方向翻 180° → 回头 → 再越过。
            //
            // 实测（8 bot 全中、与 bot 无关）：
            // Move 方向**每 ~30 帧精确翻转 180.0°**（318 个同向段里 295 个相邻段夹角 = 180.0°），
            // 往返幅度 ±0.5m、速度 3.7m/s ⇒ 总路径 90m / 净位移 6m、rev% 78%、
            // 且 15 帧采样的 A/B 两行都出现"位移恒为 0.00"（频闪锁定）。去掉起点节点，游标从**真正的第一个拐点**开始。
            // 出处：引擎 `AStar.Find`/`Smooth` 的节点语义（`clover-client-unity-engine/Runtime/Core/AStar.cs:73-76,95,160`）。
            if (path.Count > 1 && path[0] == from) path.RemoveAt(0);

            _path = path;
            _pathIndex = 0;
            _pathGoalCell = to;
            ClearPathFail();
            PathLog(from, to, path);
            return true;
        }

        /// <summary>累计本 bot 成功求出路径的次数（<see cref="_replanCount"/> 的唯一自增口）。</summary>
        private void PathLog(Vector2Int from, Vector2Int to, List<Vector2Int> path)
        {
            _replanCount++;
        }

        /// <summary>
        /// **这个目标点走得到吗**——给上层选目标用的纯查询（不推进、不改 <c>_path</c>）。
        ///
        /// <para><b>为什么要这一问</b>：上层选目标只能问"这一点可走吗"（<c>ICsMap.WalkableAt</c> /
        /// <c>CanStand</c>），而"可走"与"走得到"是两件事。两类失败都会被上层记成"位图不连通"：
        /// ① <c>[AStar] Find: 终点不可走 to=(45, 15)</c> —— 该格在**高度一致性层**里不存在；
        /// ② <c>[AStar] Find: 无可达路径 from=(107,85) to=(116,52)</c> —— 位图孤立分量。
        /// 只判了"可走"就选它 ⇒ <see cref="EnsurePath"/> 必然失败 ⇒ 退化成"朝目标直线走" ⇒ 顶着墙被反复判卡住。</para>
        ///
        /// <para><b>口径与 <see cref="EnsurePath"/> 逐字同源</b>（不另立一套判据）：同一份
        /// <see cref="SnapToWalkable"/>（半径 <see cref="CsBotConst.PathSnapRadiusCells"/>）、同一份
        /// <see cref="WalkableCellHeightAware"/>、同一个引擎 <see cref="AStar.Find"/> + <c>DefaultMaxNodes</c>。
        /// 差别只有一点：**不写 <c>_path</c> / 不动告警计数** —— 它是"问一句"，不是"走一步"。</para>
        ///
        /// <para><b>代价</b>：起点格相同时 <see cref="BuildHeightReach"/> 命中缓存（见
        /// <see cref="_heightReachCache"/>），所以"同一次换目标里连问七八个候选点"只算一次扩张。</para>
        /// </summary>
        /// <returns>true = 从 <paramref name="fromPosition"/> 求得出到 <paramref name="toPosition"/> 的格子路径。</returns>
        public bool CanReach(Vector3 fromPosition, Vector3 toPosition)
        {
            var map = Game.Map;
            if (map == null || !map.Loaded || map.CellSize <= 0f) return false;

            var from = CellOf(map, fromPosition);
            var to = CellOf(map, toPosition);
            if (!SnapToWalkable(ref from) || !SnapToWalkable(ref to)) return false;
            if (from == to) return true;

            BuildHeightReach(from, fromPosition);          // null ⇒ _hasHeightReach=false ⇒ 与 WalkableCell 等价

            // **确定性函数的备忘**：结果只依赖 (walkable 回调, 起点格, 终点格, maxNodes)，而回调由
            // 地图 + 上面这一份可达集唯一确定（键里带上可达集对象引用）⇒ 命中的答案与重算逐值相同。
            if (!ReferenceEquals(_canReachMemoMap, map))
            {
                _canReachMemoMap = map;
                _canReachMemo.Clear();
            }

            var query = new ReachQuery(from, to, _hasHeightReach ? _heightReach : null);
            if (_canReachMemo.TryGetValue(query, out var known)) return known;

            var ok = AStar.Find(WalkableCellHeightAware, from, to, AStar.DefaultMaxNodes) != null;
            if (_canReachMemo.Count >= CanReachMemoCap) _canReachMemo.Clear();
            _canReachMemo[query] = ok;
            return ok;
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
        /// <para>不能拿 <see cref="CsBotConst.WaypointArriveRadius"/>（2.5m）当格子级的到达半径：
        /// 那是给稀疏标记点的，用在 1m 的格子上会一口气跳过好几个拐点 —— 路径就白求了
        /// （等于又退化成直线走）。</para>
        /// </summary>
        private Vector3 AdvancePath(Vector3 selfPosition, Vector3 target)
        {
            if (_path == null) return target;

            //   旧的唯一规则是"离下一个更近才推进"：机器人站在某个拐点的格子里（离格心 ≤ 半格）时，
            //   下一个拐点通常还在几米外 ⇒ 规则不成立 ⇒ 它继续追**自己脚下这一格的格心**，
            //   越过 2cm 就翻 180°、来回蹭（`EnsurePath` 每次重求都把 `_pathIndex` 归零，所以这个
            //   死循环每 `PathReplanInterval`(1s) 重来一次）。半格 = 格边长 × 0.5，取的是**地图数据**
            //   （`IMapData.CellSize`），不是新加的魔法数。
            var mapData = Game.Map;
            var nodeArrive = mapData != null && mapData.CellSize > 0f ? mapData.CellSize * 0.5f : 0.5f;

            while (_pathIndex + 1 < _path.Count)
            {
                var dNow = FlatDistance(selfPosition, _path[_pathIndex]);
                if (dNow <= nodeArrive)
                {
                    _pathIndex++;                  // 已经在这一格里 ⇒ 该往下一个拐点走了
                    continue;
                }

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

            if (EnsureWalkBitmap(map))
            {
                var ix = cell.x;
                var iz = cell.y;
                if (ix < 0 || iz < 0 || ix >= _walkW || iz >= _walkD) return false;   // 图外 = 不可走（IMapData.WalkableAt 同口径）
                return _walkBitmap[iz * _walkW + ix];
            }

            var c = CellCenter(cell);
            return _map != null ? _map.WalkableAt(c.x, c.z) : map.WalkableAt(c.x, c.z);
        }

        /// <summary>
        /// 把可走位图一次性展开成数组（<c>bool[iz * Width + ix]</c>），值 = <c>IMapData.WalkableAt(格心)</c>。
        /// 位图在整场比赛内不变 ⇒ 只建一次；换图 / 未加载 ⇒ 返回 false（调用方退回逐次查询）。
        /// </summary>
        private bool EnsureWalkBitmap(IMapData map)
        {
            if (ReferenceEquals(_walkMap, map) && _walkBitmap != null) return true;
            if (!map.Loaded || map.Width <= 0 || map.Depth <= 0 || map.CellSize <= 0f) return false;

            _walkMap = map;
            _walkW = map.Width;
            _walkD = map.Depth;
            var bits = new bool[_walkW * _walkD];
            var size = map.CellSize;
            var ox = map.Origin.x;
            var oz = map.Origin.z;
            for (var iz = 0; iz < _walkD; iz++)
            {
                var z = oz + (iz + 0.5f) * size;
                var row = iz * _walkW;
                for (var ix = 0; ix < _walkW; ix++)
                {
                    bits[row + ix] = map.WalkableAt(ox + (ix + 0.5f) * size, z);
                }
            }

            _walkBitmap = bits;
            return true;
        }

        /// <summary>
        /// 按地图尺寸准备列备忘 / 扩张用的定长数组（换图 ⇒ 整表作废）。返回 false = 位图不可用。
        /// </summary>
        private bool EnsureGeoArrays(IMapData map)
        {
            if (!EnsureWalkBitmap(map)) return false;

            _gW = _walkW + 2;
            _gD = _walkD + 2;
            var n = _gW * _gD;

            var cols = _sharedCols;
            if (cols == null || cols.W != _gW || cols.D != _gD || !ReferenceEquals(cols.Map, map))
            {
                cols = new ColumnTables
                {
                    Map = map,
                    W = _gW,
                    D = _gD,
                    State = new byte[n],
                    Y = new float[n * ColMaxFaces],
                    Ny = new float[n * ColMaxFaces],
                };
                _sharedCols = cols;
            }

            if (_gH == null || _gH.Length != n)
            {
                _gReach = new int[n];
                _gStamp = new int[n];
                _gH = new float[n];
                _gGen = 0;
                _colsSeen = cols;
                return true;
            }

            if (!ReferenceEquals(_colsSeen, cols))
            {
                _colsSeen = cols;
                System.Array.Clear(_gReach, 0, n);
                System.Array.Clear(_gStamp, 0, n);
                _gGen = 0;
            }

            return true;
        }

        /// <summary>淘汰最旧的一条可达集备忘（<see cref="ReachEntry.Used"/> 最小者）——表满时用，不是整表清空。</summary>
        private void EvictOldestReach()
        {
            var oldest = long.MaxValue;
            var victim = default(HeightKey);
            foreach (var kv in Reaches.Cache)
            {
                if (kv.Value.Used >= oldest) continue;
                oldest = kv.Value.Used;
                victim = kv.Key;
            }
            Reaches.Cache.Remove(victim);
        }

        /// <summary>把一份可达集标进本代的 <see cref="_gReach"/>（缓存命中时用：判据从此只读数组）。</summary>
        private void MarkReach(HashSet<Vector2Int> set)
        {
            foreach (var cell in set)
            {
                var idx = GeoIdx(cell);
                if (idx >= 0) _gReach[idx] = _gGen;
            }
        }

        /// <summary>代次到顶 ⇒ 清一次数组重来（否则旧代的标记会被当成新代）。</summary>
        private void ResetGen()
        {
            System.Array.Clear(_gReach, 0, _gReach.Length);
            System.Array.Clear(_gStamp, 0, _gStamp.Length);
            _gGen = 1;
        }

        /// <summary>格内采样点（<see cref="InCellFrac"/> 的二维下标压成一维）× 格 → 列备忘的键。</summary>
        private static long ColumnKey(Vector2Int cell, int offIdx)
            => (((long)(cell.x + 4096) << 20) | (long)(cell.y + 4096)) * 32L + (uint)offIdx;

        /// <summary>
        /// 格内第 <paramref name="offIdx"/> 个采样点（<c>i = offIdx / 5</c>、<c>k = offIdx % 5</c>）的世界坐标，
        /// 与既有三处现场射线的起点**同式同序**（<c>CellCenter + InCellFrac[·] × CellSize</c>）。
        /// </summary>
        private Vector3 ProbeOrigin(Vector2Int cell, int offIdx, float y)
        {
            var c = CellCenter(cell);
            var size = Game.Map.CellSize;
            return new Vector3(c.x + InCellFrac[offIdx / InCellFrac.Length] * size, y,
                               c.z + InCellFrac[offIdx % InCellFrac.Length] * size);
        }

        private static float FaceY(in Column col, int k)
            => k == 0 ? col.Y0 : k == 1 ? col.Y1 : k == 2 ? col.Y2 : k == 3 ? col.Y3 : k == 4 ? col.Y4 : col.Y5;

        private static float FaceNy(in Column col, int k)
            => k == 0 ? col.Ny0 : k == 1 ? col.Ny1 : k == 2 ? col.Ny2 : k == 3 ? col.Ny3 : k == 4 ? col.Ny4 : col.Ny5;

        /// <summary>格坐标 → 列备忘 / 扩张数组的下标（带 1 格外框，好让图外一格的邻格也能查；超出返回 -1）。</summary>
        private int GeoIdx(Vector2Int cell)
        {
            var ix = cell.x + 1;
            var iz = cell.y + 1;
            if (ix < 0 || iz < 0 || ix >= _gW || iz >= _gD) return -1;
            return iz * _gW + ix;
        }

        /// <summary>
        /// 枚举一列的面。建立方式 = 从 <see cref="ColProbeTopY"/> 起反复"探到面 → 下移 <see cref="ColFaceEps"/> 再探"，
        /// 与一根连续向下的射线逐次命中**同一串面**；到 <see cref="ColMaxFaces"/> 还没探完 ⇒
        /// <see cref="Column.Overflow"/>（该列回退现场射线）。
        /// </summary>
        private void BuildColumnFaces(float px, float pz, ref Column col)
        {
            col.Count = 0;
            col.Overflow = false;
            var y = ColProbeTopY;
            for (var k = 0; k < ColMaxFaces; k++)
            {
                Vector3 point; Vector3 normal;
                if (!_map.TrySampleGround(new Vector3(px, y, pz), out point, out normal, ColProbeDrop)) break;
                switch (k)
                {
                    case 0: col.Y0 = point.y; col.Ny0 = normal.y; break;
                    case 1: col.Y1 = point.y; col.Ny1 = normal.y; break;
                    case 2: col.Y2 = point.y; col.Ny2 = normal.y; break;
                    case 3: col.Y3 = point.y; col.Ny3 = normal.y; break;
                    case 4: col.Y4 = point.y; col.Ny4 = normal.y; break;
                    default: col.Y5 = point.y; col.Ny5 = normal.y; break;
                }
                col.Count = k + 1;
                // <see cref="ICsMap.TrySampleGround"/> 把起点抬 <see cref="CsConst.GroundCheckDistance"/>，
                // 所以"下一根射线的起点"要把这个抬高量扣掉再减 <see cref="ColFaceEps"/>，否则会再次命中同一个面。
                y = point.y - CsConst.GroundCheckDistance - ColFaceEps;
            }

            // 面数到顶且"再探一根还有"才叫溢出；到顶后正好探空 ⇒ 这一列就是满 6 个面，照旧可用。
            if (col.Count == ColMaxFaces)
            {
                Vector3 point; Vector3 normal;
                col.Overflow = _map.TrySampleGround(
                    new Vector3(px, col.Y5 - CsConst.GroundCheckDistance - ColFaceEps, pz),
                    out point, out normal, ColProbeDrop);
            }
        }

        /// <summary>格心列（扩张的热路径）：第一次查到时建立，之后是两次数组读。</summary>
        private void EnsureCenterColumn(int idx)
        {
            var cell = new Vector2Int(idx % _gW - 1, idx / _gW - 1);
            var origin = ProbeOrigin(cell, 0, ColProbeTopY);
            var col = default(Column);
            BuildColumnFaces(origin.x, origin.z, ref col);

            var b = idx * ColMaxFaces;
            Cols.Y[b + 0] = col.Y0; Cols.Ny[b + 0] = col.Ny0;
            Cols.Y[b + 1] = col.Y1; Cols.Ny[b + 1] = col.Ny1;
            Cols.Y[b + 2] = col.Y2; Cols.Ny[b + 2] = col.Ny2;
            Cols.Y[b + 3] = col.Y3; Cols.Ny[b + 3] = col.Ny3;
            Cols.Y[b + 4] = col.Y4; Cols.Ny[b + 4] = col.Ny4;
            Cols.Y[b + 5] = col.Y5; Cols.Ny[b + 5] = col.Ny5;
            Cols.State[idx] = col.Overflow ? ColStateOverflow : (byte)(col.Count + 1);
        }

        /// <summary>格内其余 24 个采样点的列（字典）：只有"格心失败"的格才会走到。</summary>
        private Column GetInsetColumn(Vector2Int cell, int offIdx)
        {
            var key = ColumnKey(cell, offIdx);
            if (Cols.Insets.TryGetValue(key, out var col)) return col;

            var origin = ProbeOrigin(cell, offIdx, ColProbeTopY);
            col = default(Column);
            BuildColumnFaces(origin.x, origin.z, ref col);

            if (Cols.Insets.Count < ColCacheCap) Cols.Insets[key] = col;
            return col;
        }

        /// <summary>
        /// 与 <c>ICsMap.TrySampleGround(pos, out point, out normal, maxDrop)</c> **同口径**的落地探测
        /// （起点抬 <see cref="CsConst.GroundCheckDistance"/>、向下射 <c>maxDrop + GroundCheckDistance</c>、
        /// 只打 CsWorld 层、忽略 trigger），只是把射线换成查列备忘：
        /// 命中 = 起点之下**最高**的那个面；它低于射程 ⇒ 与现场射线一样判"探不到"。
        /// 表不可用 / 该列溢出 ⇒ 现场射线，参数逐字同原调用。
        /// </summary>
        private bool TryProbeGround(Vector2Int cell, int offIdx, float posY, float maxDrop,
                                   out float hitY, out float hitNy)
        {
            hitY = float.NaN;
            hitNy = 1f;

            var top = posY + CsConst.GroundCheckDistance;
            var bottom = posY - maxDrop;

            if (offIdx == 0 && Cols != null && Cols.State != null)
            {
                var idx = GeoIdx(cell);
                if (idx >= 0)
                {
                    var st = Cols.State[idx];
                    if (st == 0)
                    {
                        EnsureCenterColumn(idx);
                        st = Cols.State[idx];
                    }
                    if (st != ColStateOverflow)
                    {
                        var cnt = st - 1;
                        var b = idx * ColMaxFaces;
                        for (var k = 0; k < cnt; k++)
                        {
                            var fy = Cols.Y[b + k];
                            if (fy > top) continue;         // 面在起点之上：向下的射线够不到它
                            if (fy < bottom) return false;  // 起点之下最高的那个面已经低于射程 ⇒ 无命中
                            hitY = fy;
                            hitNy = Cols.Ny[b + k];
                            return true;
                        }
                        return false;
                    }
                }
            }
            else if (offIdx != 0 && Cols != null)
            {
                var col = GetInsetColumn(cell, offIdx);
                if (!col.Overflow)
                {
                    for (var k = 0; k < col.Count; k++)
                    {
                        var fy = FaceY(col, k);
                        if (fy > top) continue;
                        if (fy < bottom) return false;
                        hitY = fy;
                        hitNy = FaceNy(col, k);
                        return true;
                    }
                    return false;
                }
            }

            if (_map == null) return false;
            Vector3 point; Vector3 normal;
            if (!_map.TrySampleGround(ProbeOrigin(cell, offIdx, posY), out point, out normal, maxDrop)) return false;
            hitY = point.y;
            hitNy = normal.y;
            return true;
        }

        // ==================================================================
        /// <summary>
        /// A* 的 <c>walkable</c> 回调 + **高度一致性**（只在 <see cref="EnsurePath"/> 的求路径上用；
        /// <see cref="SnapToWalkable"/> / <see cref="DropUnreachableWaypoints"/> 仍用纯位图的
        /// <see cref="WalkableCell"/>，行为与既有逐字一致）。
        ///
        /// <para>没有可用的高度层（<see cref="_hasHeightReach"/> == false）⇒ 与 <see cref="WalkableCell"/> 等价。</para>
        /// </summary>
        private bool WalkableCellHeightAware(Vector2Int cell)
        {
            if (!WalkableCell(cell)) return false;
            if (!_hasHeightReach || _gReach == null) return true;
            var idx = GeoIdx(cell);
            return idx >= 0 && _gReach[idx] == _gGen;
        }

        /// <summary>
        /// 建立"抬起腿 ≤ <see cref="CsConst.StepUpHeight"/> 逐格走得到"的格集合（高度一致性层的**全部**内容）。
        ///
        /// <para><b>为什么需要它</b>（两个独立仪器同一结论）：可走位图是**单层 2D**，
        /// 它把一个 0.8~3.2 m 的**抬升面**（台阶/台沿）标成了"可走落脚格" ⇒ A* 求出的路径直接指过那个面
        /// ⇒ 机器人贴面磨：运行时探针 1592 行 / 离线射线 754 行，全部是"落脚面高于脚底"，
        /// 其中 `wantTopDy`（命中面 y − 脚底 y）min 0.823 / p50 2.297 / max 3.193，
        /// 而 `CsConst.StepUpHeight` = 0.4572 —— **100% 超过一个台阶**。复算脚本见
        /// </para>
        ///
        /// <para><b>判据出处（不另立定义、不新增阈值）</b>：与产品自己的台阶判据同源同式 ——
        /// <c>Module/Map/CsMap.cs:514-523</c> 的 <c>TryStepUp</c>：
        /// <c>if (hasGround &amp;&amp; point.y - from.y &gt; CsConst.StepUpHeight) return false;</c>
        /// 即"落点地面比当前脚底高出超过一个台阶 ⇒ 那是一堵台沿，不许神抬腿迈上去"。
        /// 本层把同一式用在**相邻两格**上：<c>h(下一格) − h(当前格) &gt; StepUpHeight ⇒ 这条边不通</c>。
        /// 下降方向**不设限**（与 <c>TryStepUp</c> 同为**单向**判据：重力/坠落由
        /// <c>CsMatch.StepActorPhysics</c> 负责，跳下去是合法移动）。</para>
        ///
        /// <para><b>高度怎么来（不许拍一个高度、不许把位图当高度）</b>：射线起点抬
        /// <see cref="CsConst.StepUpHeight"/>（= 站到下一格后的脚面高度上限），向下射
        /// <c>ICsMap.SampleGround</c> 的**默认**探测深度（<c>Module/Map/CsMap.cs:534</c> 的
        /// <c>maxDrop = 8f</c>）—— 与机器人自己的贴地判据**同一份**射线口径。起点抬到"一个台阶之上"而不是
        /// "头顶之上"是关键：从头顶起射会命中**头顶的屋顶/挑檐**（实测 CONTROL 组里
        /// <c>highDy</c> 最大 8.634 m）⇒ 会把隧道/桥下这些合法格误杀。两次探针的区分力已在盘上验证：
        /// 754 个误判格用"脚底高度起射"**全部探不到地面**（0/754 命中），404 个 CONTROL 行**全部**命中在脚面
        /// （Δy max 0.000 / min −0.159）⇒ 本层复现两者、零误杀。</para>
        ///
        /// <para><b>代价（不是每帧每格打射线）</b>：每次**重求路径**（<see cref="CsBotConst.PathReplanInterval"/>
        /// = 1 s 一次，且只在起点格变了时才重算）对可达格各打 1 根射线（实测主分量 ~4.4k 格）；
        /// 结果按**起点格**备忘（<see cref="_heightReachCache"/>）⇒ 卡住不动时（起点格不变）零重算。
        /// **缓存粒度不变**（仍然是"起点格 → 整个可达集"，多点结论不单独缓存，因为它只在这一层被消费）。</para>
        /// </summary>
        /// <returns>可达格集合；null = 本帧不加这一层（调用方按既有行为走）。</returns>
        private HashSet<Vector2Int> BuildHeightReach(Vector2Int from, Vector3 selfPosition)
        {
            var map = Game.Map;
            if (map == null || !map.Loaded || map.CellSize <= 0f) { _hasHeightReach = false; return null; }
            if (_map == null || !_map.IsLoaded) { _hasHeightReach = false; return null; }
            if (!EnsureGeoArrays(map)) { _hasHeightReach = false; return null; }

            // 换图 ⇒ 位图换了 ⇒ 高度层整片作废（不许拿旧图的结论判新图）。备忘键不含地图对象，所以换图即换表。
            if (_sharedReach == null || !ReferenceEquals(_sharedReach.Map, map))
                _sharedReach = new ReachTables { Map = map };

            // 先留住上一次的结论：连续同键的快路径要用它（下面的重算失败会把 _hasHeightReach 置回 false）。
            var prevHas = _hasHeightReach;
            var prevSet = _heightReach;
            var prevFrom = _heightReachFrom;
            var prevBase = _heightReachBaseBits;
            _hasHeightReach = false;

            // 基准高度 = 机器人**脚下的地面**（产品同源：ICsMap.SampleGround → Module/Map/CsMap.cs:534）。
            // 探不到（人在空中 / 图外）⇒ 本帧不加这一层（不许瞎猜一个基准高度）。
            var baseY = _map.SampleGround(selfPosition);
            if (float.IsNegativeInfinity(baseY))
            {
                HeightLayerOff("自己脚下探不到地面（SampleGround = -inf）", from, selfPosition);
                return null;
            }

            // **起点格吸附落脚面**（本层的第一个不变量）：`SampleGround` 给的是"脚下那一点"的地面，
            //   而扩张用的每一步都要拿"本格落脚面"当基准（射线起点 = 当前层 + StepUpHeight）。
            //   两者可以差一整层：格内 5×5 取样只看到台地面、而机器人正站在台沿下/同一格的另一层时，
            //   基准比邻格的面低 > StepUpHeight ⇒ **邻格一个都探不到** ⇒ 可达集塌成 1 格
            //   （离线实测：把起点 baseY 调到比本格落地面低 0.5 m ⇒ 4402 格塌到 **1** 格；
            //   实机形态 = 15:27:33 那条「终点格 (20,90) 在高度一致性层里不存在（从起点 (90,60) 起…）」，
            //   而 (90,60) 的取样面是 2.438、机器人当时的脚下地面远低于它）。
            //   所以起点的高度**吸到本格离它最近的合法落脚面**上；吸附不到（本格真的没有面）就保持原值。
            var startFace = SnapStartFace(from, baseY);
            if (!float.IsNaN(startFace)) baseY = startFace;

            // **精确备忘**：键 = (起点格, 基准落脚高度)。扩张只依赖这两个量 + 世界几何 ⇒ 命中即
            // 与"不缓存直接重算"逐值相同；预算里省掉的是重算本身，不是判据。
            var key = new HeightKey(from, baseY);
            if (prevHas && prevSet != null && prevFrom == from && prevBase == key.BaseBits) return prevSet;
            if (Reaches.Cache.TryGetValue(key, out var cached))
            {
                cached.Used = ++Reaches.Used;
                Reaches.Cache[key] = cached;
                if (++_gGen == int.MaxValue) ResetGen();
                MarkReach(cached.Set);
                _heightReach = cached.Set;
                _heightReachFrom = from;
                _heightReachBaseBits = key.BaseBits;
                _hasHeightReach = true;
                return cached.Set;
            }

            _mpCenterFail = 0; _mpSaved = 0; _mpDead = 0;   // 取样统计计数（只在**重算**时清，命中缓存不改）

            if (++_gGen == int.MaxValue) ResetGen();
            var gen = _gGen;

            var reach = new HashSet<Vector2Int> { from };
            var height = _gH;                     // 本代：格 → 最小落脚高度（按格下标索引，不用逐格哈希表）
            var stamps = _gStamp;                 // 本代："这一格有高度"的标记
            var queue = _gQueue;
            queue.Clear();
            var fromIdx = GeoIdx(from);
            if (fromIdx >= 0)
            {
                height[fromIdx] = baseY;
                stamps[fromIdx] = gen;
                _gReach[fromIdx] = gen;
            }
            queue.Enqueue(from);
            var blocked = 0;
            var relaxations = 0;

            while (queue.Count > 0)
            {
                if (reach.Count > CsBotConst.HeightReachMaxCells)
                {
                    // 退化口径：规模超上限 ⇒ **整层关掉**（宁可回到既有行为，也不许拿半个可达集把合法路径判死）。
                    HeightLayerOff($"可达格数超过上限 {CsBotConst.HeightReachMaxCells}", from, selfPosition);
                    return null;
                }
                if (++relaxations > 8 * CsBotConst.HeightReachMaxCells)
                {
                    // 松弛是有界的（只允许把高度压低、下方有界），这一条是"契约被改坏时不许死循环"的挡板。
                    HeightLayerOff($"扩张次数超过上限 {8 * CsBotConst.HeightReachMaxCells}", from, selfPosition);
                    return null;
                }

                var cur = queue.Dequeue();
                var curIdx = GeoIdx(cur);
                var hCur = curIdx >= 0 ? height[curIdx] : baseY;

                for (var k = 0; k < 8; k++)
                {
                    var n = new Vector2Int(cur.x + CellStepX[k], cur.y + CellStepZ[k]);
                    if (!WalkableCell(n)) continue;                 // 层①：位图（语义一字不动）

                    var hN = GroundYAbove(n, hCur);                  // 层②：落脚高度
                    // 判"探不到"只能用 **NaN**，不许用 `hN < 0f`：GroundYAbove 返回的是**命中点的 y**，
                    // 用 `hN < 0f` 会把 -3.251（真实命中、dy = 0.000 的平地）判成"探不到地面"
                    // ⇒ 可达集塌成 **1 格**，而 CT 半个地图的脚底都在 y < 0 ⇒ 整层形同把 CT 侧封死。
                    // 出处：Module/Map/CsMap.cs:534-535（SampleGround 返回 point.y、找不到返回 -inf）
                    // 与 CsMap.cs:542-557（TrySampleGround 用 bool 区分"有没有命中"）。
                    if (float.IsNaN(hN))
                    {
                        blocked++;
                        continue;                                    // 探不到地面（台沿内部 / 空洞）⇒ 边不通
                    }
                    if (hN - hCur > CsConst.StepUpHeight)
                    {
                        // 带内（dy ≤ StepUpHeight）的面，所以这里再判一次是为了"契约被改坏时仍然不通"，
                        // 阈值一个字没动、也不许被这里绕过。
                        blocked++;
                        continue;
                    }

                    // 「先到先得」改成**取更低的层**：每格只存一个高度时，"谁先到"决定了这一格的高度，
                    //   而"抬腿 ≤ StepUpHeight"这条边判据对**更低的当前层**更宽松 ⇒ 先到的层更高时会把本可通的路判死
                    //   （实测：同一张图、同一份几何，只换起点基准高度，可达集在 4400 / 4402 / 4404 之间变，
                    //   判死格集合两两差 0~4 格 —— 同一个问题给出不同答案）。
                    //   改成松弛：只有把这一格的高度**压低**才重新入队 ⇒ 结论不再依赖到达顺序，
                    //   且是这条（单向）关系在"每格一个高度"口径下的最大不动点。
                    var idx = GeoIdx(n);
                    if (stamps[idx] == gen && hN >= height[idx] - 1e-4f) continue;

                    reach.Add(n);
                    height[idx] = hN;
                    stamps[idx] = gen;
                    _gReach[idx] = gen;
                    queue.Enqueue(n);
                }
            }

            // 精确备忘入库：满了淘汰**最旧**的一条（不是整表清空）。
            if (Reaches.Cache.Count >= HeightReachCacheCap) EvictOldestReach();
            Reaches.Cache[key] = new ReachEntry { Set = reach, Used = ++Reaches.Used };

            _heightReach = reach;
            _heightReachFrom = from;
            _heightReachBaseBits = key.BaseBits;
            _hasHeightReach = true;

            // 退化留痕（非预期分支）：可达集塌成 1 格 ⇒ 扩张一步都没走通，是一条"走不动"的直接证据。
            if (reach.Count <= 1 && Time.time >= _nextHeightLogAt)
            {
                _nextHeightLogAt = Time.time + CsBotConst.StuckWarnCooldown;
                Game.Logger.Warn(Tag,
                    $"{_ownerName} 高度一致性层的可达集塌成 {reach.Count} 格：从格 {from}（脚下地面 y={baseY:F3}）" +
                    $"按抬升 ≤ {CsConst.StepUpHeight:F2} 一个邻格都扩张不出去（被高度判死 {blocked} 格，" +
                    $"格心探不到带内落脚面 {_mpCenterFail} 格、格内 5×5 多点救回 {_mpSaved} 格）");
            }
            return reach;
        }

        /// <summary>
        /// **起点格吸附落脚面**：在本格（格心 + 格内 5×5 多点）找一个**离 <paramref name="baseY"/> 最近**的合法落脚面，
        /// 返回它当高度层的基准高度；本格一个合法面都找不到 ⇒ <see cref="float.NaN"/>（调用方保持原值）。
        ///
        /// <para><b>为什么必须有</b>：扩张的每条边都拿"当前层 + <see cref="CsConst.StepUpHeight"/>"当射线起点
        /// （<see cref="GroundYAbove"/>），所以基准高度一旦比本格真正的落脚面低超过一个台阶，
        /// **邻格一个都探不到**、可达集塌成 1 格（离线实测：起点 baseY 比本格落地面低 0.5 m ⇒ 4402 → **1**）。
        /// 而 <see cref="ICsMap.SampleGround"/> 给的是"脚下那一点"的地面：格内 5×5 只看到台地面、
        /// 机器人却站在同一格的另一层 / 台沿下时，两者会差一整层（实机形态 =
        /// 「终点格 (20,90) 在高度一致性层里不存在（从起点 (90,60) 起…）」，而 (90,60) 的取样面是 2.438）。</para>
        ///
        /// <para><b>只放宽"起点这一格"的上下搜索带</b>（<see cref="StartFaceSnapBand"/>，也顺手排除头顶屋顶）：
        /// 边判据（抬腿 ≤ <see cref="CsConst.StepUpHeight"/>）、可站立判据
        /// （<see cref="CsConst.MaxStandableSlopeNormalZ"/>）、探测深度（<see cref="GroundProbeDepth"/>）一个都没改。
        /// 并列取先命中的（遍历顺序 = <see cref="InCellFrac"/> 升序）⇒ 确定性。</para>
        /// </summary>
        private float SnapStartFace(Vector2Int cell, float baseY)
        {
            var band = StartFaceSnapBand;
            var best = float.NaN;
            var bestAbs = float.MaxValue;

            for (var i = 0; i < InCellFrac.Length; i++)
            {
                for (var k = 0; k < InCellFrac.Length; k++)
                {
                    float hitY; float hitNy;
                    if (!TryProbeGround(cell, i * InCellFrac.Length + k, baseY + band,
                                        band + GroundProbeDepth, out hitY, out hitNy)) continue;
                    if (hitNy < CsConst.MaxStandableSlopeNormalZ) continue;      // 陡坡不算落脚面（CsMap.cs:521 同式）
                    if (hitY > baseY + band || hitY < baseY - GroundProbeDepth) continue;

                    var ad = Mathf.Abs(hitY - baseY);
                    if (ad < bestAbs - 1e-4f)                                    // 更近（并列保留先命中的 ⇒ 确定性）
                    {
                        bestAbs = ad;
                        best = hitY;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// 把 <paramref name="cell"/> 的格心放到"从 <paramref name="fromY"/> 抬一个台阶"的高度上向下打射线，
        /// 返回命中的地面高度；<b>探不到返回 <see cref="float.NaN"/></b>（= 从这一层没有落脚面 ⇒ 这条边不通）。
        /// <para>**哨兵必须是 NaN，不许用"负数"**：<see cref="ICsMap.SampleGround"/> 返回的是**命中点的 y**，
        /// <c>hN &lt; 0f</c> 把 -3.251（真实命中、dy = 0.000 的平地）判成"探不到地面" ⇒ 可达集塌成 1 格。
        /// 区分"有没有命中"只能靠 <see cref="ICsMap.TrySampleGround"/> 的 <c>bool</c>
        /// （出处 Module/Map/CsMap.cs:542-557），**探测起点 / 掩码 / 深度一个都不改**。</para>
        /// <para>起点抬 <see cref="CsConst.StepUpHeight"/>：高于它的面**不构成落脚面**（那正是"抬升面"的形状：
        /// 从脚底起射会落在实体内部 ⇒ 不收"内部起步"的命中 ⇒ 探不到 ⇒ 判不通，与实证 0/754 一致）。</para>
        ///
        /// <para><b>格心失败 ⇒ 退到「整格」口径</b>（见 <see cref="InCellFrac"/> 里那段"与生成侧口径的差异"）。
        /// 先按**格心**取样；格心探不到 / 命中的面落在带外（高于脚底一个台阶、或深过探测深度）⇒ 再按
        /// <see cref="CellLandingFace"/> 在**格内 5×5** 找带内合法落脚面。判据一个字没放宽：
        /// 仍然是"带内（<c>[脚底 − GroundProbeDepth, 脚底 + StepUpHeight]</c>）存在**可站立**的合法面"，
        /// <see cref="CsConst.StepUpHeight"/> / <see cref="CsConst.MaxStandableSlopeNormalZ"/> 都没改。</para>
        /// </summary>
        private float GroundYAbove(Vector2Int cell, float fromY)
        {
            // 层②a —— **格心口径，一字不动**：命中、且落在带内就直接返回。刻意**不加**任何新判据。
            float hitY; float hitNy;
            if (TryProbeGround(cell, 0, fromY + CsConst.StepUpHeight, GroundProbeDepth, out hitY, out hitNy))
            {
                var dy = hitY - fromY;
                if (dy <= CsConst.StepUpHeight && dy >= -GroundProbeDepth) return hitY;
            }

            // 这不是放宽：陡坡（normal.y < CsConst.MaxStandableSlopeNormalZ）在回退分支里依然不算落脚面。
            _mpCenterFail++;
            var mp = CellLandingFace(cell, fromY);
            if (float.IsNaN(mp))
            {
                _mpDead++;                                   // 整格里也没有带内合法落脚面 ⇒ 这格真的不能站
                return float.NaN;
            }

            _mpSaved++;
            return mp;
        }

        /// <summary>
        /// **回退分支**的单点取样：命中、且落在 <c>[fromY − <see cref="GroundProbeDepth"/>, fromY + <see cref="CsConst.StepUpHeight"/>]</c>
        /// 带内、且**可站立**（法线 y ≥ <see cref="CsConst.MaxStandableSlopeNormalZ"/>）⇒ true 并给出高度。
        /// 任一条不满足 ⇒ false（与既有判据同式：<c>Module/Map/CsMap.cs:520-522</c> 的
        /// <c>TryStepUp</c>；探到与否仍只由 <see cref="ICsMap.TrySampleGround"/> 的 bool 给出，
        /// 出处 <c>Module/Map/CsMap.cs:542-557</c>）。
        /// </summary>
        private bool SampleLandingFace(Vector2Int cell, int offIdx, float fromY, out float y)
        {
            y = float.NaN;
            float hitY; float hitNy;
            if (!TryProbeGround(cell, offIdx, fromY + CsConst.StepUpHeight, GroundProbeDepth, out hitY, out hitNy))
                return false;
            var dy = hitY - fromY;
            if (dy > CsConst.StepUpHeight) return false;                            // 抬升超一个台阶 ⇒ 不是落脚面
            if (dy < -GroundProbeDepth) return false;                               // 深过探测深度 ⇒ 不在带内
            if (hitNy < CsConst.MaxStandableSlopeNormalZ) return false;              // 陡坡不算地面（CsMap.cs:521 同式）
            y = hitY;
            return true;
        }

        /// <summary>
        /// **整格口径**的落脚面：格内 5×5 多点取样（<see cref="InCellFrac"/>，格心那一档已在上层试过 ⇒ 跳过），
        /// 取**带内、与脚底最近**的那一个合法落脚面（并列 → 保留先遍历到的，遍历顺序 = <see cref="InCellFrac"/> 升序，
        /// 所以确定性）；格内**任何一点**都没有带内合法面 ⇒ <see cref="float.NaN"/>（= 这格真的站不住）。
        /// <para><b>代价</b>：只在**格心失败**时才走这一支（每次扩张的失败格数 = 日志里的 <c>blocked</c>，实测 CT 74 / T 95 量级），
        /// 每个失败格 24 根射线；整个可达集的结论按**起点格**备忘（<see cref="_heightReachCache"/>）⇒
        /// 卡住不动（起点格不变）时零重算，不是每帧每格打多点射线。</para>
        /// </summary>
        private float CellLandingFace(Vector2Int cell, float fromY)
        {
            var best = float.NaN;
            var bestAbs = float.MaxValue;

            for (var i = 0; i < InCellFrac.Length; i++)
            {
                for (var k = 0; k < InCellFrac.Length; k++)
                {
                    if (i == InCellCenter && k == InCellCenter) continue;      // 格心刚才已经试过
                    float h;
                    if (!SampleLandingFace(cell, i * InCellFrac.Length + k, fromY, out h)) continue;

                    var ad = Mathf.Abs(h - fromY);
                    if (ad < bestAbs - 1e-4f)                                   // 更近（并列保留先命中的 ⇒ 确定性）
                    {
                        bestAbs = ad;
                        best = h;
                    }
                }
            }

            return best;
        }

        /// <summary>高度层"这次没生效"的原因（不许静默退化；同一原因按 <see cref="CsBotConst.StuckWarnCooldown"/> 降频）。</summary>
        private void HeightLayerOff(string why, Vector2Int from, Vector3 selfPosition)
        {
            if (Time.time < _nextHeightLogAt) return;
            _nextHeightLogAt = Time.time + CsBotConst.StuckWarnCooldown;
            Game.Logger.Warn(Tag,
                $"{_ownerName} 高度一致性层本帧**未生效**（原因：{why}）⇒ 本次求路径只用可走位图（既有行为）。" +
                $"起点格 {from}，位置 ({selfPosition.x:F1},{selfPosition.y:F1},{selfPosition.z:F1})");
        }

        /// <summary>求路径失败时补一句"高度层怎么说"（与"位图不连通"是三件不同的事，不许混在一句里）。</summary>
        private string HeightVerdict(Vector2Int from, Vector2Int to, bool heightOk)
        {
            if (!heightOk)
                return "；（高度一致性层本帧未生效，见上一条 Warn）";
            if (!_hasHeightReach)
                return "；（高度一致性层本帧未生效）";

            var sb = new System.Text.StringBuilder("；高度一致性层：");
            sb.Append(_heightReach.Contains(to) ? $"终点格 {to} 在可达集内" : $"**终点格 {to} 不可达**");
            sb.Append(_heightReach.Contains(from) ? $"，起点格 {from} 在可达集内" : $"，**起点格 {from} 不可达**");
            sb.Append($"（可达集 {_heightReach.Count} 格；判据 = 带内无合法落脚面 / 抬升 > {CsConst.StepUpHeight:F2} m 的边不通" +
                      $"（出处 Module/Map/CsMap.cs:514-523）；取样口径 = 格心 → 格内 5×5（切片BR，BotNavigator.cs:GroundYAbove/CellLandingFace））");
            return sb.ToString();
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
        /// <para>口径出处（逐字一致，不是自定的换算）：<c>clover-client-unity-engine/Runtime/Presentation/MapFormat.cs:243-252</c>
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
        /// "求不出路径"必须留痕（不许静默退化）。降频口径 = <see cref="CsBotConst.StuckWarnCooldown"/>
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

            // ① 上一次选定的偏角还有效 → 先沿用（带惯性，防止贴着障碍物左右抖）。
            //
            //      （`if (!WalkableAhead(cached)) _probeAngle = 0f;`）每帧都能把保持清掉 ⇒ 实测
            //      `ProbeHoldSeconds` 形同不存在：方向以 ~10 次/秒 翻（实测日志
            //      的 296 条 [BOTFLIP] 行里 `probeLeft=0.50` 恒成立 = 每条都是"刚重掷"），
            //      Darrell/Scuzzy 于是绕圈：总路径 384/376m、净位移 17/22m。
            //    只有两种证据能让它失效（顺序 = 证据强度）：
            //      (a) **硬证据**：该方向已被**实测位移**证明走不动（IsBlockedDir ← NoteMovement）⇒ 立刻放弃；
            //      (b) **弱证据攒够**：位图说不可走**连续**超过 AvoidBadDirSeconds ⇒ 才放弃。
            //    弱证据没攒够时：本帧**不返回**这条已知不可走的方向（那会顶着墙推），而是返回"离它最近的
            //    可走候选"，并把这个偏角继续留在 _probeAngle 当"最小偏差"的锚 —— 锚不动 ⇒ 挑出的候选也不动
            //    ⇒ 方向稳定（等价于"候选必须连续 N 帧都最优才换向"：这里 N 就是 AvoidBadDirSeconds 内的帧数）。
            if (_probeAngle != 0f && now < _probeUntil)
            {
                var cached = Rotate(dir, _probeAngle);
                if (WalkableAhead(position, cached) && !IsBlockedDir(cached, now))
                {
                    _probeBadSince = 0f;
                    return cached;
                }

                if (IsBlockedDir(cached, now))
                {
                    _probeAngle = 0f;
                    _probeUntil = 0f;
                    _probeBadSince = 0f;
                }
                else
                {
                    if (_probeBadSince <= 0f) _probeBadSince = now;
                    if (now - _probeBadSince < CsBotConst.AvoidBadDirSeconds)
                    {
                        _probeUntil = now + CsBotConst.ProbeHoldSeconds;   // 锚点身份顺延（弱证据还不够格推翻它）
                        var sticky = PickAvoidAngle(position, dir, _probeAngle, now);
                        if (sticky != 0f) return Rotate(dir, sticky);
                    }
                    else
                    {
                        _probeAngle = 0f;
                        _probeUntil = 0f;
                        _probeBadSince = 0f;
                    }
                }
            }

            // 上一次在这个方向上一动不动 → 不要再往这里推（WalkableAt 说能走、物理说走不动的情况真有）
            if (WalkableAhead(position, dir) && !IsBlockedDir(dir, now))
            {
                _probeAngle = 0f;
                _probeBadSince = 0f;
                return dir;
            }

            //    不再按固定表序 —— 表序在"±100 都能走"时会在两侧之间来回跳 180°。
            var angle = PickAvoidAngle(position, dir, _probeAngle, now);
            if (angle != 0f)
            {
                _probeAngle = angle;
                _probeUntil = now + CsBotConst.ProbeHoldSeconds;
                _probeBadSince = 0f;
                return Rotate(dir, angle);
            }

            // 一圈都不通（或都在"走不动"的方向上）→ 取"跑道最长"且**不是失败方向**的那条。
            // **只返回验证过的方向**（直接 `Rotate(dir, 180)` 的反向没验证过）：没验证的反向在贴墙时
            // 会连着几十帧位移 0，这正是"卡住日志里换向却原地不动"的另一半成因。
            _probeAngle = 0f;
            _probeUntil = 0f;
            _probeBadSince = 0f;
            var fallback = PickBestRunwayDir(position, dir, out var run, now);
            if (run <= 0f) WarnNoRunway(position);
            return fallback;
        }

        /// <summary>
        /// 在 <see cref="CsBotConst.AvoidAngles"/> 里挑这一帧的落地偏角（0 = 一个都不通）。
        ///
        /// <para><b>试序 = 与 <paramref name="anchor"/> 的偏差升序</b>（<paramref name="anchor"/> = 当前
        /// 正在沿用的偏角；为 0 时退化为**表原序**，因为此时"离期望方向最近"就是表序本身）。
        /// 候选**全部**不可走时才考虑换到另一侧的大偏角。原写法固定按表序取第一个通过者 ⇒
        /// 实测 ±100° 两侧同时可走时，机器人在两个相隔 180° 的方向之间以 ~10 次/秒 翻
        /// （实测：`probe=-100 … probe=100` 交替，51~55% 恰 180°）。</para>
        ///
        /// <para>代价不变：还是一圈最多 <see cref="CsBotConst.AvoidAngles"/>.Length 次
        /// <see cref="WalkableAhead"/>（每帧 ≤ 24 次位图查询），没有引入新的寻路 / 物理调用。</para>
        /// </summary>
        private float PickAvoidAngle(Vector3 position, Vector3 dir, float anchor, float now)
        {
            var usedMask = 0;
            for (var k = 0; k < CsBotConst.AvoidAngles.Length; k++)
            {
                var pick = -1;
                var pickDist = float.MaxValue;
                for (var i = 0; i < CsBotConst.AvoidAngles.Length; i++)
                {
                    if ((usedMask & (1 << i)) != 0) continue;
                    // anchor == 0 时用**下标**当"距离"⇒ 退化成表原序（先 ±25、再 ±50…）；
                    // 不是随手写的：表本身就是"离期望方向由近到远"，用下标即"最小偏差"的下界近似。
                    var dist = anchor == 0f ? i : Mathf.Abs(CsBotConst.AvoidAngles[i] - anchor);
                    if (dist < pickDist)
                    {
                        pickDist = dist;
                        pick = i;
                    }
                }

                if (pick < 0) break;
                usedMask |= 1 << pick;

                var angle = CsBotConst.AvoidAngles[pick];
                var candidate = Rotate(dir, angle);
                if (!WalkableAhead(position, candidate)) continue;
                if (IsBlockedDir(candidate, now)) continue;
                return angle;
            }

            return 0f;
        }

        /// <summary>这个方向最近被证明"走不动"吗（<see cref="CsBotConst.BlockedDirMemorySeconds"/> 内、夹角 ≤ 35°）。</summary>
        private bool IsBlockedDir(Vector3 dir, float now)
        {
            if (now >= _blockedUntil) return false;
            if (_blockedDir.sqrMagnitude < 0.5f) return false;
            return Vector3.Angle(_blockedDir, dir) <= CsBotConst.BlockedDirDegrees;
        }

        /// <summary>
        /// 沿 <paramref name="dir"/> 迈出**一格**是不是踏进"位图孤立格区"（判据 = <see cref="IsTrapCell"/>）。
        ///
        /// <para><b>只在"自己站在非孤立格区里"时判</b>：已经在孤立格区里的机器人若也被本层拦住，
        /// 就连走回主分量的机会都没有（所以那时放行，让逃逸逻辑与物理去决定怎么出去）。</para>
        ///
        /// <para><b>为什么这一层必须存在</b>：位图是单层 2D 而几何是多层，位图说"可走"的地方在物理上可能是
        /// 没有落脚面的缝（<see cref="CsBotConst.TrapComponentCells"/> 说明的小分量）。走进去以后
        /// <c>CanReach</c> 对每一个候选目标都返回 false ⇒ 目标选择整条链（换路线 / 巡逻 / 出生点）全失败 ⇒
        /// 机器人剩下整回合原地换向。"别走进去"是这一层唯一能做的事。</para>
        /// </summary>
        private bool StepsIntoTrap(Vector3 position, Vector3 dir)
        {
            var map = Game.Map;
            if (map == null || !map.Loaded || map.CellSize <= 0f) return false;

            var mine = CellOf(map, position);
            if (IsTrapCell(mine)) return false;
            return IsTrapCell(CellOf(map, position + dir * map.CellSize));
        }

        /// <summary>
        /// 这一格所在位图连通分量是不是"孤立格区"（格数 ≤ <see cref="CsBotConst.TrapComponentCells"/>）。
        /// 不可走的格返回 false —— 那种格由位图本身拦住，不属于本层。
        /// </summary>
        private bool IsTrapCell(Vector2Int cell)
        {
            var map = Game.Map;
            if (map == null || !map.Loaded) return false;

            if (!ReferenceEquals(_trapCachedMap, map))
            {
                _trapCachedMap = map;
                _trapCellCache.Clear();
            }

            if (_trapCellCache.TryGetValue(cell, out var known)) return known;

            //   数不出分量（不可走 / 数超上限）一律按"不是孤立格区"处理：本层只拦"确定知道的小分量"。
            var size = FloodComponentSize(cell, CsBotConst.TrapComponentCells);
            var trap = size > 0 && size <= CsBotConst.TrapComponentCells;

            if (_trapCellCache.Count >= TrapCellCacheCap) _trapCellCache.Clear();
            _trapCellCache[cell] = trap;
            return trap;
        }

        /// <summary>
        /// 从 <paramref name="cell"/> 数它所在连通分量的格数（4 正 + 4 斜、对角要求两侧可走 ——
        /// 与引擎 <see cref="AStar"/> 的移动规则逐条一致，所以"分量"就是 A* 意义上的连通类）。
        ///
        /// <para>超过 <paramref name="limit"/> 立即返回 -1（"这是大连通区，不必数完"）；起点不可走返回 0。</para>
        /// </summary>
        private int FloodComponentSize(Vector2Int cell, int limit)
        {
            if (!WalkableCell(cell)) return 0;

            var seen = new HashSet<Vector2Int> { cell };
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(cell);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                for (var k = 0; k < 8; k++)
                {
                    var sx = CellStepX[k];
                    var sz = CellStepZ[k];
                    var n = new Vector2Int(cur.x + sx, cur.y + sz);
                    if (seen.Contains(n)) continue;
                    if (!WalkableCell(n)) continue;
                    if (sx != 0 && sz != 0)
                    {
                        if (!WalkableCell(new Vector2Int(cur.x + sx, cur.y))) continue;
                        if (!WalkableCell(new Vector2Int(cur.x, cur.y + sz))) continue;
                    }

                    seen.Add(n);
                    if (seen.Count > limit) return -1;
                    queue.Enqueue(n);
                }
            }

            return seen.Count;
        }

        /// <summary>
        /// "前方这一段能不能走"：**近端 + 远端两处都要可走**。
        ///
        /// <para>为什么不能只看远端：<see cref="ICsMap.WalkableAt"/> 是"这一格可不可走"的格子查询。
        /// 面前一堵 1 格厚（1m）的墙时，<see cref="CsBotConst.ProbeDistance"/>（1.4m）处的格子落在
        /// **墙的另一侧**，那格是可走的 ⇒ 只看远端会判定"前方畅通"，机器人就会一直顶着墙推：
        /// 每帧位移 0 → 被判卡住 → 日志刷屏，而且避障的偏角候选**一次都不会被尝试**。
        /// 这是实测那条"卡住：0.00m，原地反复换向"的头号成因。</para>
        /// </summary>
        private bool WalkableAhead(Vector3 position, Vector3 dir)
        {
            //   孤立格区一票否决：那里位图说可走、物理往往没有落脚面，且 **A\* 到不了任何目标**
            //   （走进去 = 目标选择整条链全失败）⇒ 不许把这一帧的方向提交给它（见 StepsIntoTrap）。
            if (StepsIntoTrap(position, dir)) return false;

            var near = position + dir * CsBotConst.ProbeClearance;
            var far = position + dir * CsBotConst.ProbeDistance;
            if (_map.WalkableAt(near.x, near.z) && _map.WalkableAt(far.x, far.z)) return true;

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

            //   地形护栏与 <see cref="WalkableAhead"/> 同一份：逃逸 / 换向也不许把机器人送进孤立格区
            //   （否则"卡住自恢复"自己就能把它推进去）。已经在里面时 <see cref="StepsIntoTrap"/> 放行。
            if (StepsIntoTrap(position, dir)) return 0f;

            var run = 0f;
            for (var d = CsBotConst.EscapeRunwayStep; d <= CsBotConst.EscapeRunwayMax + 0.001f;
                 d += CsBotConst.EscapeRunwayStep)
            {
                var p = position + dir * d;
                if (!_map.WalkableAt(p.x, p.z)) break;
                run = d;
            }

            //   实测（14888/14888 行）：卡住的 bot 用
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
            _diagLastDir = Vector3.zero;
            _diagFlips = 0;
            _probeAngle = 0f;          // 迟滞状态随"卡住状态"一起归零（换路线/重开时不许带着旧锚点）
            _probeUntil = 0f;
            _probeBadSince = 0f;
        }

        // ==================================================================
        /// <summary>
        /// 统计"本帧的 Move 方向相对上一帧翻了 &gt;120°"的次数（<see cref="_diagFlips"/>），
        /// <c>_diagLastDir</c> 记上一帧方向。改向是 ~30 帧一次的高频事件，只计数、不逐条打印。
        /// </summary>
        private void DiagFlip(Vector3 pos, Vector3 desired, Vector3 dir, Vector3 goal, Vector3 target,
            string branch, float now)
        {
            if (dir.sqrMagnitude > 0.5f && _diagLastDir.sqrMagnitude > 0.5f &&
                Vector3.Angle(_diagLastDir, dir) > 120f)
            {
                _diagFlips++;
            }

            if (dir.sqrMagnitude > 0.5f) _diagLastDir = dir;
        }
    }
}
