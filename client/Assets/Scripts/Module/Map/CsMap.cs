using System;
using System.Collections.Generic;
using System.Globalization;
using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Map
{
    /// <summary>
    /// <see cref="ICsMap"/> 的实现（业务侧的"空间事实 + 本地碰撞解算"）。
    ///
    /// <para><b>数据来源（**一份**：同一份 <c>.bytes</c> 里两段）</b></para>
    /// <list type="bullet">
    /// <item><b>可行走位图</b>：引擎 <c>Game.Map</c>（<c>Resources/MapData/de_dust2.bytes</c>，
    /// 由 <c>Clover/CS16/烘焙 de_dust2</c> 导出，与服务端同源）。它回答"这一格能不能走"。</item>
    /// <item><b>命名标记点</b>：**同一份 <c>.bytes</c> 的 <c>FlagMarkers</c> 段**
    /// （名字 + 世界坐标；引擎 <c>CloverMapFormat</c> / <c>IMapData.Points/GetPoints/TryGetPoint</c> 读）。
    /// 生成侧出处：<c>MapBakeRunner</c> 的 <c>MapBakeOptions.MarkerRootName</c> ⇐
    /// <c>Dust2Builder</c> 摆的场景根对象 <c>Markers</c>；名字写错 / 少摆标记，契约校验仍会报 Error。</item>
    /// </list>
    ///
    /// <para><b>高度从哪来</b>：真实 dust2 是多层地图（T 出生点比 CT 出生点高约 6.8m，有斜坡/楼梯/高台），
    /// 而引擎位图是**单层 2D**。所以本模块把两件事分开：</para>
    /// <list type="bullet">
    /// <item>水平阻挡 → 位图（<see cref="WalkableAt"/> / <see cref="ResolveMove"/>）；</item>
    /// <item>高度/落地 → 真实几何的 <c>MeshCollider</c>（<see cref="SampleGround"/> 用射线取地面）。
    /// 这样爬坡、下楼、跳箱子全部是真实高度，而不是被压平到一层。</item>
    /// </list>
    ///
    /// <para>本类**不引用任何其它模块**（只经 <c>Game.Map</c> / <c>Physics</c>），
    /// 符合"模块之间只经接口协作"的分层要求。</para>
    /// </summary>
    public sealed class CsMap : ICsMap
    {
        private const string Tag = "Map";

        /// <summary>扫掠细分步长上限（米）：一帧位移切成 ≤ 这个长度的段，避免高速穿过薄墙。</summary>
        private const float MaxSweepStep = 0.25f;

        /// <summary>8 向采样的对角系数（√2/2）。</summary>
        private const float DiagonalScale = 0.70710678f;

        /// <summary>
        /// 身体高度带射线的**起点抬升**（米）= <see cref="CsConst.GroundCheckDistance"/>：
        /// 与 <see cref="TrySampleGround"/> 抬射线起点用的是同一个口径 ——
        /// "脚正好贴面时避免自交 / 漏检"，所以直接复用那个常量，不再自造一个数值。
        /// </summary>
        private const float BodyProbeLift = CsConst.GroundCheckDistance;

        /// <summary>
        /// 身体高度带射线的**向下探测深度**（米）。必须够深到能穿过脚面：
        /// 跳在半空时脚面以下是空的，第一个交点会比脚面**低**（射线长度不够就会打不到东西，
        /// 从而把"正跨过矮墙"误判成"被挡"）。取 <see cref="SampleGround"/> 的默认探测深度（8 m），
        /// 本图相邻层的落差（T 家比 CT 家高约 6.8 m）在这个范围内。
        /// </summary>
        private const float BodyProbeDrop = 8f;

        // ---- 标记点表 ----
        private Dictionary<string, Vector3[]> _markers;
        private Vector3[] _spawns;
        private bool _markersRequested;
        private bool _markersFailed;

        // ---- 日志降频（每帧路径上不许刷屏）----
        private readonly HashSet<string> _loggedKeys = new HashSet<string>();
        private int _groundRayMask = -1;
        private bool _groundMaskWarned;

        // ==================================================================
        //  状态
        // ==================================================================

        public bool IsLoaded => Game.Map != null && Game.Map.Loaded;

        public string MapName => Game.Map != null ? Game.Map.Name : CsConst.MapDust2;

        public string Status
        {
            get
            {
                if (Game.Map == null) return "地图模块未挂载（Game.Map == null：引擎没 Launch 或表现域没挂）";
                var s = Game.Map.Status;
                if (!string.IsNullOrEmpty(s)) return s;
                return _markers == null ? "已加载（标记点未就绪）" : "已加载";
            }
        }

        /// <summary>标记点是否就绪（AI 能不能找人靠它）。</summary>
        public bool MarkersReady => _markers != null && _markers.Count > 0;

        public int SpawnPointCount => _spawns != null ? _spawns.Length : 0;

        // ==================================================================
        //  加载 / 卸载
        // ==================================================================

        public void LoadAsync(string mapResourcePath, Action onLoaded, Action<string> onFailed = null)
        {
            if (IsLoaded)
            {
                // 位图已经在了（别的调用方先加载过）——但标记表可能还没读，必须补上：
                LoadMarkers(onLoaded);
                return;
            }

            if (Game.Map == null)
            {
                Game.Logger?.Error(Tag, "地图加载失败：Game.Map 为空（引擎尚未 Launch / 表现域模块没挂载）");
                onFailed?.Invoke("Game.Map == null");
                return;
            }

            Game.Logger?.Info(Tag, $"加载地图数据：{mapResourcePath}（Resources/{mapResourcePath}.bytes）");
            Game.Map.LoadFromResource(mapResourcePath, ok =>
            {
                if (!ok)
                {
                    // 非预期分支：数据缺失/损坏 —— 必须显式报，不许静默当"全可走"
                    Game.Logger?.Error(Tag, $"地图数据加载失败：{Status}（用 Clover/CS16/烘焙 de_dust2 重新导出）");
                    onFailed?.Invoke(Status);
                    return;
                }

                Game.Logger?.Info(Tag, $"逻辑地图已加载：{Status}");
                LoadMarkers(onLoaded);
            });
        }

        /// <summary>
        /// 建**运行时标记点索引**：数据来自引擎 <see cref="IMapData.Points"/>
        /// （<c>de_dust2.bytes</c> 的 <c>CloverMapFormat.FlagMarkers</c> 段；引擎侧 <c>Map.cs</c> 已按名字建好索引）。
        /// 失败**不阻断**地图加载（地图仍可玩），但一定打 Error：
        /// 标记缺失的表现是"机器人不动、下包无效"，不打日志就永远查不出来。
        ///
        /// <para>本类不自己解析标记表：引擎侧 <c>Map.cs</c> 已把标记解析好，标记点与位图装在同一份
        /// <c>.bytes</c> 里（同一份空间事实只有一份载体）。</para>
        ///
        /// <para>本方法只做两件事：① 把引擎的 <c>IReadOnlyList&lt;MapPoint&gt;</c> 按名字拷进本地数组索引
        /// （<see cref="Points"/> 在 bot 决策热路径上，避免每次 <c>ToArray</c> 分配）；
        /// ② 契约点名校验（<see cref="RequiredMarkerNames"/> 缺哪个报哪个）。</para>
        /// </summary>
        private void LoadMarkers(Action done)
        {
            if (_markersRequested)
            {
                done?.Invoke();
                return;
            }
            _markersRequested = true;

            var map = Game.Map;
            if (map == null || !map.Loaded)
            {
                _markersFailed = true;
                Game.Logger?.Error(Tag,
                    "标记点不可用：引擎逻辑地图未加载（Game.Map 为空 / 未 Loaded）—— AI/包点/买枪区都会退化");
                done?.Invoke();
                return;
            }

            var table = new Dictionary<string, List<Vector3>>(RequiredMarkerNames.Length);
            var all = map.Points;
            for (var i = 0; i < all.Count; i++)
            {
                var p = all[i];
                if (string.IsNullOrEmpty(p.Name)) continue;
                if (!table.TryGetValue(p.Name, out var list))
                {
                    list = new List<Vector3>(4);
                    table[p.Name] = list;
                }
                list.Add(p.Position);
            }

            _markers = new Dictionary<string, Vector3[]>(table.Count);
            foreach (var kv in table) _markers[kv.Key] = kv.Value.ToArray();

            // 出生点：Spawn_T + Spawn_CT 汇总（GetSpawnPoint 按索引循环取用）
            var spawns = new List<Vector3>();
            if (_markers.TryGetValue(CsMarkers.SpawnT, out var t)) spawns.AddRange(t);
            if (_markers.TryGetValue(CsMarkers.SpawnCT, out var ct)) spawns.AddRange(ct);
            _spawns = spawns.ToArray();

            int total = 0;
            foreach (var kv in _markers) total += kv.Value.Length;

            if (total == 0)
            {
                // 非预期分支（**必须点名下一步**）：地图文件里没有标记点段（旧产物，或烘焙时没配
                // `MapBakeOptions.MarkerRootName`）。现象 = "进图后机器人集体不动"，不报就没法查。
                Game.Logger?.Error(Tag,
                    "地图文件里没有命名标记点段（flags 未含 FlagMarkers）：AI/包点/买枪区都会退化 —— " +
                    "跑 Clover/CS16/生成 de_dust2 场景 + Clover/CS16/烘焙 de_dust2 重新导出" +
                    "（⛔ 标记点已随 .bytes 同行，不再有独立标记表文件）");
            }
            else
            {
                // 契约要求的标记全点名核对：缺哪个报哪个（每个名字只报一次）
                foreach (var name in RequiredMarkerNames)
                {
                    if (!_markers.ContainsKey(name))
                        Game.Logger?.Error(Tag, $"标记缺失：地图标记点段里没有 \"{name}\" —— " +
                                                "对应玩法（出生点/包点/买枪区/机器人路线）会静默退化");
                }
            }

            Game.Logger?.Info(Tag,
                $"标记点已加载（引擎 FlagMarkers 段）：{_markers.Count} 类 / {total} 点（出生点 {_spawns.Length} 个）");
            done?.Invoke();
        }

        /// <summary><see cref="CsMarkers"/> 里契约要求的全部标记名（消费方按这些名字取点）。</summary>
        private static readonly string[] RequiredMarkerNames =
        {
            CsMarkers.SpawnT, CsMarkers.SpawnCT,
            CsMarkers.BombsiteA, CsMarkers.BombsiteB,
            CsMarkers.BuyZoneT, CsMarkers.BuyZoneCT,
            CsMarkers.TAttackA, CsMarkers.TAttackB, CsMarkers.TMid,
            CsMarkers.CTDefendA, CsMarkers.CTDefendB, CsMarkers.CTMid,
            CsMarkers.Patrol,
        };

        public void Unload()
        {
            Game.Map?.Clear();
            _markers = null;
            _spawns = null;
            _markersRequested = false;
            _markersFailed = false;
            _loggedKeys.Clear();
            Game.Logger?.Info(Tag, "地图数据已卸载");
        }

        // ==================================================================
        //  空间查询
        // ==================================================================

        public bool WalkableAt(float x, float z)
        {
            var m = Game.Map;
            if (m == null || !m.Loaded)
            {
                // 未加载 = 不阻挡（引擎语义：地图缺失是配置问题，不该表现成"玩家被锁死"）。
                WarnOnce("walk.notloaded", "地图未加载，WalkableAt 一律返回 true（不阻挡）—— 检查地图数据是否烘出来了");
                return true;
            }
            return m.WalkableAt(x, z);
        }

        /// <summary>
        /// <list type="number">
        /// <item><b>位图路径</b>（原判据，走绝大多数帧）：中心 + 8 向位图全可走
        /// **且**落点地面在当前脚高的一步台阶内（<see cref="CsConst.StepUpHeight"/>）。</item>
        /// <item><b>几何路径</b>：位图说挡、或落点地面高出一整步时，按真实世界几何复核**身体高度带**
        /// （<see cref="BodyHeightClear"/>）。</item>
        /// </list>
        ///
        /// <para>**为什么必须有第二层**：位图是**单层 2D**（一格一位，没有高度），
        /// 而"这一格能不能立足"本质上是**高度问题** —— 同一个格子在 y=0（地面）被箱子侧壁挡住、
        /// 在 y=1.2（跳起来）却是通的；矮墙/扶手/箱子顶面在位图里一律只有"挡"或"通"一个答案。
        /// 只按位图判会同时产生两个相反的错误：
        /// ① 箱子顶面是朝上的面 ⇒ 箱子所在格判成"可走" ⇒ 站在地面的角色**直接走进箱子**（用户报"箱子能穿"）；
        /// ② 矮障碍所在格判成"挡" ⇒ 跳起来也过不去（用户报"匪家楼梯扶手跳不过去"）。
        /// </para>
        /// </summary>
        public bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius)
        {
            if (!IsLoaded)
            {
                WarnOnce("stand.notloaded", "地图未加载，CanStand 一律返回 true（不阻挡）");
                return true;
            }

            if (BitmapClear(pos, radius) && GroundWithinStep(pos)) return true;
            return BodyHeightClear(pos, radius);
        }

        /// <summary>中心 + 8 向（半径 <paramref name="radius"/>）位图全可走（原判据，单独抽出来复用）。</summary>
        private bool BitmapClear(Vector3 pos, float radius)
        {
            if (!WalkableAt(pos.x, pos.z)) return false;

            float d = radius * DiagonalScale;
            return WalkableAt(pos.x + radius, pos.z) &&
                   WalkableAt(pos.x - radius, pos.z) &&
                   WalkableAt(pos.x, pos.z + radius) &&
                   WalkableAt(pos.x, pos.z - radius) &&
                   WalkableAt(pos.x + d, pos.z + d) &&
                   WalkableAt(pos.x + d, pos.z - d) &&
                   WalkableAt(pos.x - d, pos.z + d) &&
                   WalkableAt(pos.x - d, pos.z - d);
        }

        /// <summary>
        /// 落点地面是否在"当前脚高"的一步台阶内（<see cref="CsConst.StepUpHeight"/>）。
        ///
        /// <para>探不到地面（悬空 / 脚下是深坑）⇒ **不否决**：跳起、下落中间的帧就长这样
        /// （此时该格该不该通由 <see cref="BodyHeightClear"/> 说话）。</para>
        /// </summary>
        private bool GroundWithinStep(Vector3 pos)
        {
            if (!TrySampleGround(pos, out var point, out _)) return true;
            return point.y - pos.y <= CsConst.StepUpHeight;
        }

        /// <summary>
        /// 身体高度带复核 = **整身体积（一次，中心）** + **9 点零宽射线**（中心 + 8 向）：
        /// <list type="number">
        /// <item><see cref="BodyVolumeBlocked"/>：以玩家半径的胶囊覆盖身高带 —— 抓"身体被实体占据"
        /// （人在实心块柱内 / 身体插进墙）；</item>
        /// <item><see cref="BodyHeightClearAt"/> ×9：抓"身高带上横着一堵墙/一个实体"。</item>
        /// </list>
        /// 两条缺一不可：① 只管"占据"，对"贴着墙站"不敏感（那条由 ② 与位图管）；
        /// ② 只管"向下射线的首交点"，对"射线起点在实体内部"这种形态**天生看不见**（见
        /// <see cref="BodyVolumeBlocked"/> 的注释）。
        /// </summary>
        private bool BodyHeightClear(Vector3 pos, float radius)
        {
            // 体积复核只能在**中心**判一次：9 点探针是零宽射线（与 BitmapClear 同形），
            //    若在 9 个点上各加一颗半径 PlayerRadius 的球，等效半径会放大到 2×PlayerRadius
            //    ⇒ 把"贴着墙能站"误杀成不能站。
            if (BodyVolumeBlocked(pos)) return false;
            return LegacyBodyHeightClear(pos, radius);
        }

        /// <summary>
        /// </summary>
        private bool LegacyBodyHeightClear(Vector3 pos, float radius)
        {
            if (!BodyHeightClearAt(pos.x, pos.y, pos.z)) return false;

            float d = radius * DiagonalScale;
            return BodyHeightClearAt(pos.x + radius, pos.y, pos.z) &&
                   BodyHeightClearAt(pos.x - radius, pos.y, pos.z) &&
                   BodyHeightClearAt(pos.x, pos.y, pos.z + radius) &&
                   BodyHeightClearAt(pos.x, pos.y, pos.z - radius) &&
                   BodyHeightClearAt(pos.x + d, pos.y, pos.z + d) &&
                   BodyHeightClearAt(pos.x + d, pos.y, pos.z - d) &&
                   BodyHeightClearAt(pos.x - d, pos.y, pos.z + d) &&
                   BodyHeightClearAt(pos.x - d, pos.y, pos.z - d);
        }

        /// <summary>
        /// 单点身体高度带复核：从**头顶上方**向下打一根射线，只看**第一个交点**。
        /// <list type="bullet">
        /// <item>第一交点在**脚面以下**（含 <see cref="CsConst.GroundCheckDistance"/> 容差）
        /// ⇒ 身高带是空的：障碍的顶面比脚面低（人已经站在它上面 / 正跨过它）；</item>
        /// <item>第一交点在**身高带之内** ⇒ 有一堵墙 / 一坨障碍挡在身体高度上。</item>
        /// </list>
        ///
        /// <para>这条对"地面"和"箱子顶面"**同样成立**（都朝上、都在脚面高度）⇒
        /// "站在箱子顶上"与"站在地上"走同一条判据，**不需要给箱子开特例**。</para>
        ///
        /// <para>打不到任何世界面（该列一个世界几何都没有）⇒ 保守判**挡**：这一格没有可站立的世界几何。</para>
        /// </summary>
        private bool BodyHeightClearAt(float x, float feetY, float z)
        {
            var origin = new Vector3(x, feetY + CsConst.StandHeight + BodyProbeLift, z);
            float len = CsConst.StandHeight + BodyProbeLift + BodyProbeDrop;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, len, GroundMask(), QueryTriggerInteraction.Ignore))
                return false;
            return hit.point.y <= feetY + CsConst.GroundCheckDistance;
        }

        /// <summary>
        /// 以 <see cref="CsConst.PlayerRadius"/> 为半径的胶囊，覆盖身高带
        /// <c>[pos.y + GroundCheckDistance, pos.y + StandHeight]</c>；与任何世界几何重叠 ⇒ 判**挡**。
        ///
        /// <para><b>为什么单根向下射线挡不住这种形态</b>：
        /// <see cref="BodyHeightClearAt"/> 的射线起点固定在 <c>feetY + StandHeight + BodyProbeLift</c>，
        /// 人**站在实心块柱的足迹内**（脚面低于块顶）时，起点落在块**内部**；而 PhysX 默认
        /// <c>Physics.queriesHitBackfaces = false</c> ⇒ "从实体内部朝外"的射线不给交点 ⇒
        /// 射线一路穿到块底下的地面，首交点在脚面以下 ⇒ 被误判成"身高带是空的"
        /// ⇒ **位图判挡之后，几何分支又把这一格放行了**（人钻进实心块柱）。
        /// 体积检测求的是"**占据**"，与"起点在不在实体内部"无关，所以它能看见这种形态。</para>
        ///
        /// <para><b>为什么"站在箱顶 / 台阶上"仍能站</b>：胶囊的两个端点各**内缩一个半径**
        /// ⇒ 胶囊的**最低点**正好落在 <c>pos.y + GroundCheckDistance</c>、**最高点**正好落在
        /// <c>pos.y + StandHeight</c> —— 与向下射线的"脚面容差 / 身高"逐字对齐。
        /// 顶面正好在脚面高度（甚至高出一个 <see cref="CsConst.StepUpHeight"/>）的箱顶/台阶
        /// 因此**不与该胶囊重叠**（最低点还差一个 GroundCheckDistance 才碰到脚面），
        /// 可站性由 <see cref="BodyHeightClearAt"/> 的向下射线继续给（首交点 = 脚面 ⇒ 通）。</para>
        ///
        /// <para>层掩码 / trigger 口径与 <see cref="BodyHeightClearAt"/> 完全一致
        /// （<see cref="GroundMask"/> + <c>QueryTriggerInteraction.Ignore</c>）——
        /// 尤其重要：<c>Level/Blockers/Blocker_*</c> 那些盒子是 **trigger**（只服务烘焙），
        /// 必须继续被忽略，否则整张图会被 811 个隐形盒判成不可站。</para>
        /// </summary>
        private bool BodyVolumeBlocked(Vector3 pos)
        {
            const float r = CsConst.PlayerRadius;
            float lo = pos.y + CsConst.GroundCheckDistance + r;
            float hi = pos.y + CsConst.StandHeight - r;
            if (hi <= lo)
            {
                // 身高带比两个半径还短：没有可判的体积（退化配置）。
                // 只加留痕：`hi <= lo` 判据与 `return false` 走向**逐字不变**（不改数值语义）。
                // 走 WarnOnce（按 key 去重）—— 本方法在 CanStand 的半径采样路径上，不许每帧刷屏。
                WarnOnce("volume.degenerate",
                    "身高带比两个半径还短（退化配置）：本点跳过体积复核 —— 检查 CsConst.StandHeight / PlayerRadius");
                return false;
            }
            return Physics.CheckCapsule(new Vector3(pos.x, lo, pos.z), new Vector3(pos.x, hi, pos.z),
                                        r, GroundMask(), QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// 本地碰撞解算：**扫掠细分（≤0.25m）+ 分轴滑墙（先 X 后 Z）+ 台阶（≤ StepUpHeight）**。
        /// Y 分量原样跟随目标（重力/落地由调用方用 <see cref="SampleGround"/> 收尾）。
        /// </summary>
        public Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius)
            => ResolveMoveCore(from, to, radius, null);

        /// <summary>
        /// **测试入口**：与 <see cref="ResolveMove"/> 走**同一条实现**（不是复制体），
        /// 额外把每一步的 <c>(i, want, curBefore, curAfter)</c> 交给 <paramref name="trace"/>。
        /// </summary>
        public Vector3 ResolveMoveTraceForTest(Vector3 from, Vector3 to,
            Action<int, Vector3, Vector3, Vector3> trace, float radius = CsConst.PlayerRadius)
            => ResolveMoveCore(from, to, radius, trace);

        private Vector3 ResolveMoveCore(Vector3 from, Vector3 to, float radius,
                                        Action<int, Vector3, Vector3, Vector3> trace)
        {
            if (!IsLoaded)
            {
                WarnOnce("move.notloaded", "地图未加载，ResolveMove 原样放行（不做本地碰撞）");
                return to;
            }

            // 一帧位移**一律分段推进**（每段 ≤ <see cref="MaxSweepStep"/>）：段内走
            // <see cref="StepOnce"/> 的分轴滑墙 + <see cref="TryStepUp"/> 的台阶闸门。
            // 分段是**必须**的 —— 一帧位移的大小由 dt 决定，不分段时同一个位置在不同帧率下
            // 就是"一小步"与"一大步"两种形态：大 dt 那侧一步能跨过整格阻挡（穿墙），
            // 小 dt 那侧跨不过去（原地不动）⇒ 走位结果变成帧率的函数。
            //
            // 每一段上再补一次**几何放行**（<see cref="BodyHeightClear"/> + <see cref="GroundWithinStep"/>）：
            // 位图说挡、但身高带（含落点高差）是空的 ⇒ 放行。位图是**单层 2D**（一格一位、没有高度），
            // "薄墙/扶手所在的整格"与"顶面能站的箱子"在位图里是同一个答案，只有真几何能分开这两者；
            // 站位贴着这类几何时半径采样（<see cref="CanStand"/>）必然越界 ⇒ 位图单独说不出"能不能过"。
            // 放行仍在分段内 ⇒ 单段最多 MaxSweepStep，不会一步穿墙。
            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / MaxSweepStep));

            var cur = from;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                var want = new Vector3(from.x + dx * t, to.y, from.z + dz * t);
                var prev = cur;
                cur = StepOnce(cur, want, radius);
                var probe = new Vector3(want.x, cur.y, want.z);
                if (cur.x == prev.x && cur.z == prev.z &&
                    BodyHeightClear(probe, radius) && GroundWithinStep(probe))
                    cur = probe;
                trace?.Invoke(i, want, prev, cur);
            }
            return new Vector3(cur.x, to.y, cur.z);
        }

        /// <summary>走一小步（≤0.25m）：分轴滑墙（先 X 后 Z）+ 台阶。</summary>
        private Vector3 StepOnce(Vector3 cur, Vector3 want, float radius)
        {
            // 整体可站 → 直接过去（绝大多数帧走这条）
            if (CanStand(new Vector3(want.x, cur.y, want.z), radius)) return new Vector3(want.x, cur.y, want.z);

            var p = cur;
            if (AxisPassable(cur, new Vector3(want.x, cur.y, p.z), radius)) p.x = want.x;   // 先 X
            if (AxisPassable(p, new Vector3(p.x, cur.y, want.z), radius)) p.z = want.z;     // 再 Z

            return p;   // 某轴被钳住 ⇒ 贴墙滑行（调用方据此清掉该轴速度）
        }

        /// <summary>
        /// 单轴能否推进：先按"站得下（半径采样）"判；站不下时再给**台阶/贴边**一次机会
        /// （见 <see cref="TryStepUp"/>）。两条都不行 = 撞墙，保留该轴 = 贴墙滑行。
        /// </summary>
        private bool AxisPassable(Vector3 from, Vector3 target, float radius)
            => CanStand(target, radius) || TryStepUp(from, target, radius);

        /// <summary>
        /// 台阶 / 贴边挤压判定（<see cref="CsConst.StepUpHeight"/> 在这里真正起作用）：
        /// <list type="number">
        /// <item><b>格子中心</b>可走 ⇒ 走原路径；<b>位图判挡</b>时改用真几何四道闸门放行
        /// （该列有地面 ∨ 落点不比脚下低 ∨ 高差 ≤ 一步台阶 ∨ 法线不陡 ∨ 体积不被占 ∨ 膝盖射线通畅）——
        /// 见下面 段：单层 2D 位图对"侧面挡人、顶面能站"的台阶/扶手只有"挡"一个答案，
        /// 硬否决它就是用户报的「台阶上跳一下就卡住」；</item>
        /// <item>落点地面比当前脚下**高出不超过 <see cref="CsConst.StepUpHeight"/>**（0.45m）——
        /// 高过它就是一堵台沿，不许"神抬腿"迈上去；</item>
        /// <item>落点地面**不是陡坡**（法线 y ≥ <see cref="CsConst.MaxStandableSlopeNormalZ"/>）——
        /// 原版 `PM_WalkMove` 的下探之后就是拿这个分量判的（`if (trace.plane.normal[2] &lt; 0.7) goto usedown;`），
        /// 陡坡上不去（否则玩家会顺着陡面走上去、身体陷进地形）；</item>
        /// <item><b>膝盖高度</b>朝落点的射线必须通畅：挡住半径采样的只是相邻墙角的边，
        /// 不是一条拦在身前的墙。</item>
        /// </list>
        /// <para>为什么需要它：<see cref="CanStand"/> 用 8 向半径采样，紧贴墙角/楼梯边时会把"差一点点"的格
        /// 判成站不下，表现成"上楼梯被墙角挂住"。CS 里玩家可以贴着边挤过去 —— 这个分支就是那个手感，
        /// 且被"高差 ≤ StepUpHeight"卡住，不会变成穿墙。</para>
        /// </summary>
        private bool TryStepUp(Vector3 from, Vector3 target, float radius)
        {
            // 陡坡不算地面（原版 PM_WalkMove：下探后 `if (trace.plane.normal[2] < 0.7) goto usedown;`
            // ⇒ 放弃"迈上去"这条路径）。缺了它就会沿陡面走上去（= 用户报的"坡道穿模"）。
            var hasGround = TrySampleGround(target, out var point, out var normal);
            if (hasGround && normal.y < CsConst.MaxStandableSlopeNormalZ) return false;
            if (hasGround && point.y - from.y > CsConst.StepUpHeight) return false;

            // 位图是**单层 2D**（一格一位、没有高度）：楼梯踏步 / 扶手 / 台沿
            // 这些"侧面挡人、顶面能站"的几何在位图里**只有"挡"一个答案**。
            // 所以 `WalkableAt(target)` 为 false 时**不能直接判 false** —— 那会把
            //   "迈上台阶 / 走上扶手顶面"整条路掐掉，而调用方 `AxisPassable` 只有
            //   `CanStand || TryStepUp` 两条路 ⇒ 两条都假 ⇒ 该轴被钳住、速度被清 0 ⇒ 卡死。
            // 下面这些全部满足才放行：
            //   ① 该列必须探得到地面（`hasGround`）—— 没地面（虚空/墙外）一律不许进；
            //   ② 落点地面必须**不比脚下低**（低了就交给位图管：位图说挡就是挡，不许从边沿掉下去）；
            //   ③ 高差 ≤ `CsConst.StepUpHeight`、法线 ≥ `MaxStandableSlopeNormalZ`（上面两条已判）；
            //   ⑤ 膝盖高度朝落点的射线通畅（下面一行）。
            //   ⇒ 0.9 m 台沿 / 高墙照旧过不去（②③⑤ 挡），0.3 m 台阶、0.3 m 扶手能迈上去。
            if (!WalkableAt(target.x, target.z))
            {
                if (!hasGround) return false;
                if (point.y - from.y < 0f) return false;
                if (BodyVolumeBlocked(new Vector3(target.x, point.y, target.z))) return false;
            }

            var dir = new Vector3(target.x - from.x, 0f, target.z - from.z);
            float dist = dir.magnitude;
            if (dist < 0.001f)
            {
                // 落点与本点在水平面上重合（这一轴已经到位）：没有可推进的方向。
                // 只加留痕：阈值 `0.001f` 与 `return false` 走向**逐字不变**（不改数值语义）。
                // StepOnce 的按轴推进每帧都会走到这里 ⇒ 必须限频（WarnOnce 按 key 只报一次）。
                WarnOnce("stepup.zerodist",
                    "TryStepUp 的落点与本点水平重合（dist < 0.001f）：本轴无可推进，按未通过处理");
                return false;
            }
            dir /= dist;

            var knee = new Vector3(from.x, from.y + CsConst.StepUpHeight, from.z);
            return !Physics.Raycast(knee, dir, dist, GroundMask(), QueryTriggerInteraction.Ignore);
        }

        /// <summary>向下取地面高度；找不到返回 <c>float.NegativeInfinity</c>（调用方判它）。</summary>
        public float SampleGround(Vector3 pos, float maxDrop = 8f)
            => TrySampleGround(pos, out var point, out _, maxDrop) ? point.y : float.NegativeInfinity;

        /// <summary>
        /// 向下取地面**（含世界法线）**。法线是**陡坡判据的唯一来源**：上轴（本工程 = <c>y</c>）分量
        /// &lt; <see cref="CsConst.MaxStandableSlopeNormalZ"/>（0.7 ⇒ 45.573°）⇒ 原版所谓的 "too steep"，
        /// 那片地面**不算地面**（出处与后果见 <see cref="CsConst.MaxStandableSlopeNormalZ"/>）。
        /// </summary>
        public bool TrySampleGround(Vector3 pos, out Vector3 point, out Vector3 normal, float maxDrop = 8f)
        {
            // 射线起点抬高一丁点（复用引擎的贴地判定距离），免得脚正好贴面时自交/漏检
            var origin = new Vector3(pos.x, pos.y + CsConst.GroundCheckDistance, pos.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, maxDrop + CsConst.GroundCheckDistance,
                                GroundMask(), QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                normal = hit.normal;
                return true;
            }

            point = default;
            normal = Vector3.up;
            return false;
        }

        /// <summary>
        /// 地面/台阶射线用的层掩码。**正常路径只打"世界"层**（<c>CsWorld</c>）——世界的层名与层号
        /// 是契约：见 <c>Core/CsConst.PhysicsLayers</c>，由 <c>Assets/Editor/Views/PhysicsLayerSetup.cs</c>
        /// 落进 <c>TagManager</c>、由 <c>Dust2Builder</c> 标到 <c>Level/**</c>、由 <c>ActorView</c> 把角色标到
        /// <c>CsPlayer</c>/<c>CsBot</c>。
        ///
        /// <para><b>层名解析失败 ⇒ 一定报 Error，绝不静默</b>：<c>NameToLayer("CsWorld") = -1</c> 会让
        /// 掩码退化成全层 ⇒ 贴地射线命中**角色自己的命中盒** ⇒ 命中点跟着角色一起上移 ⇒
        /// actor 的 y 每 0.05s 被抬一次（实测 -3 → 500+）。所以退化时把层名与 <c>NameToLayer</c>
        /// 的返回值打进 Error，并把"命中盒会被当地面"这句后果写进日志。</para>
        /// </summary>
        private int GroundMask()
        {
            if (_groundRayMask >= 0) return _groundRayMask;

            int world = LayerMask.NameToLayer(PhysicsLayers.WorldName);
            int mask = ~0;
            if (world >= 0)
            {
                mask = 1 << world;
            }
            else
            {
                // 退化路径：只按**层号**排除角色层（层号是常量，不依赖层名能否解析），
                // 这样最坏情况是"世界层名没生效"，而不是"射线打到自己身上"。
                int player = LayerMask.NameToLayer(PhysicsLayers.PlayerName);
                int bot = LayerMask.NameToLayer(PhysicsLayers.BotName);
                if (player >= 0) mask &= ~(1 << player);
                if (bot >= 0) mask &= ~(1 << bot);
                if (!_groundMaskWarned)
                {
                    _groundMaskWarned = true;
                    Game.Logger?.Error(Tag,
                        $"物理层缺失：LayerMask.NameToLayer(\"{PhysicsLayers.WorldName}\") = {world}（期望 {PhysicsLayers.World}），" +
                        $"\"{PhysicsLayers.PlayerName}\" = {player}（期望 {PhysicsLayers.Player}），" +
                        $"\"{PhysicsLayers.BotName}\" = {bot}（期望 {PhysicsLayers.Bot}）—— " +
                        $"地面射线退化为掩码 0x{mask:X8}（全层减去角色层）：贴地判定变粗、" +
                        "命中盒/掉落物都可能被当成地面。按 CsConst.PhysicsLayers 建好层即可消除" +
                        "（菜单 Clover/CS16/确保物理层）");
                }
            }
            _groundRayMask = mask;
            return mask;
        }

        // ==================================================================
        //  测试入口（类型化；不参与任何真实玩家路径）
        // ==================================================================
        //
        // （把高风险动作做成**专用、类型化**的入口，别藏在反射里），形状与
        // `CombatModule.SetFireHeldForTest` / `PlayerMotor.ForceLookForTest` 一致。
        // 没有任何 Update / 事件会调它们；它们只把**已经存在的纯函数**暴露成可复现的入口。

        /// <summary>
        /// **测试入口**：解算一次并把「期望 vs 实际」写成一行日志（返回实际终点）。
        /// 语义与 <see cref="ResolveMove"/> **逐字一致** —— 本方法只是它的类型化壳，
        /// 不改动 <see cref="ResolveMove"/> 的任何行为。
        ///
        /// <para><b>为什么必须有它</b>：走位验证过去靠"每回合 tp3 重定位 + 持续按住输入"驱动真实角色，
        /// 而 <c>ResolveMove</c> 在 <c>Freeze</c>/<c>RoundEnd</c> 阶段根本不生效
        /// 本入口直接对**纯函数**求解，与回合相位无关（数值类证据：秒级、可复跑）。</para>
        /// </summary>
        public Vector3 ResolveMoveForTest(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius)
        {
            var end = ResolveMove(from, to, radius);
            Game.Logger?.Info(Tag,
                "walkline | from=" + V(from) + " to=" + V(to) + " end=" + V(end) +
                " |Δ|=" + F(Vector3.Distance(from, end)) + " want=" + F(Vector3.Distance(from, to)) +
                " bitmap(from)=" + WalkableAt(from.x, from.z) +
                " bitmap(to)=" + WalkableAt(to.x, to.z) +
                " bitmap(end)=" + WalkableAt(end.x, end.z) +
                " canStand(from)=" + CanStand(from, radius) +
                " canStand(end)=" + CanStand(end, radius) +
                " rayClear(end)=" + BodyHeightClearAt(end.x, end.y, end.z) +
                " volBlocked(end)=" + BodyVolumeBlocked(end));
            return end;
        }

        /// <summary>
        /// **测试入口**：沿一条水平走线按步长逐点解算，**一次调用**把整张表写进日志（返回行数）。
        /// <list type="bullet">
        /// <item>逐点行：对每个 d（step, 2·step, … ≤ maxDist）单独解算
        /// <c>ResolveMove(from, from + dir·d)</c> —— 判据 = "对着实心面推进，终点必须停在面外"；</item>
        /// <item>串行行：每 0.1 m 连续推进（= <see cref="ResolveMove"/> 的真实逐帧用法），
        /// 报最终落点与累计位移 —— 判据 = 累计位移 ≈ 起点到实心面的距离（不被放行穿进去）。</item>
        /// </list>
        /// </summary>
        public int WalkLineForTest(Vector3 from, Vector3 dir, float maxDist, float step,
                                   float radius = CsConst.PlayerRadius)
        {
            var flat = new Vector3(dir.x, 0f, dir.z);
            if (flat.sqrMagnitude < 1e-6f)
            {
                // 非预期分支：方向近乎竖直 ⇒ 没有水平走线可算，必须留痕（否则表现为"一行都没出"）
                Game.Logger?.Warn(Tag, "walkline | dir 的水平分量近似为 0，无走线可算（dir=" + V(dir) + "）");
                return 0;
            }
            flat.Normalize();
            if (step <= 0.01f)
            {
                // 非预期参数（测试入口）：步长过小时按 0.1 走 —— 静默改值会让"量法变了"没人认领。
                // 只加留痕：判据 `<= 0.01f` 与赋值 `0.1f` **逐字不变**（不改数值语义）。
                WarnOnce("walkline.step",
                    "WalkLineForTest 传入步长过小（<= 0.01）：本次按 0.1 处理（测试入口，不参与真实玩家路径）");
                step = 0.1f;
            }

            Game.Logger?.Info(Tag, "walkline.header | from=" + V(from) + " dir=" + V(flat) +
                                   " maxDist=" + F(maxDist) + " step=" + F(step) + " radius=" + F(radius) +
                                   " loaded=" + IsLoaded);
            int n = 0;
            for (float d = step; d <= maxDist + 1e-4f; d += step)
            {
                ResolveMoveForTest(from, from + flat * d, radius);
                n++;
            }

            // 串行推进（= 逐帧真实用法）：每 0.1 m 一步
            const float micro = 0.1f;
            int microSteps = Mathf.Max(1, Mathf.CeilToInt(maxDist / micro));
            var cur = from;
            float travelled = 0f;
            for (int i = 0; i < microSteps; i++)
            {
                var prev = cur;
                cur = ResolveMove(cur, cur + flat * micro, radius);
                travelled += Vector3.Distance(prev, cur);
            }
            Game.Logger?.Info(Tag, "walkline.chained | start=" + V(from) + " end=" + V(cur) +
                                   " travelled=" + F(travelled) + " micro=" + F(micro) +
                                   " steps=" + microSteps + " volBlocked(end)=" + BodyVolumeBlocked(cur));
            return n;
        }

        /// <summary>
        /// 位图 9 点 → 落点地面高差 → 向下射线（<see cref="BodyHeightClearAt"/>，旧判据）
        /// </summary>
        public string BodyHeightDiagnoseForTest(Vector3 pos, float radius = CsConst.PlayerRadius)
        {
            bool bitmap = BitmapClear(pos, radius);
            bool withinStep = GroundWithinStep(pos);
            bool rayClear = BodyHeightClearAt(pos.x, pos.y, pos.z);
            bool volBlocked = BodyVolumeBlocked(pos);
            bool geomNow = BodyHeightClearForTest(pos, radius, out var geomLegacy);
            bool standNow = bitmap && withinStep || geomNow;
            bool standBefore = bitmap && withinStep || geomLegacy;
            string line = "bodydiag | pos=" + V(pos) + " bitmapClear=" + bitmap +
                          " groundWithinStep=" + withinStep +
                          " rayClear(单点BodyHeightClearAt)=" + rayClear +
                          " volumeBlocked(新BodyVolumeBlocked)=" + volBlocked +
                          " geomLegacy(改前9点射线)=" + geomLegacy +
                          " geomNow(改后=体积+9点)=" + geomNow +
                          " canStandBefore=" + standBefore + " canStand=" + standNow +
                          " canStand(实调)=" + CanStand(pos, radius) +
                          " walkable=" + WalkableAt(pos.x, pos.z);
            Game.Logger?.Info(Tag, line);
            return line;
        }

        /// <summary>
        /// **测试入口**：单独读"位图 9 点"这一条判据（供走线取证逐点采样，不刷日志）。
        /// </summary>
        public bool BitmapClearForTest(Vector3 pos, float radius = CsConst.PlayerRadius)
            => BitmapClear(pos, radius);

        /// <summary>
        /// **测试入口**：单独读 <see cref="BodyHeightClearAt"/>（**旧**判据：向下射线的首交点）。
        /// </summary>
        public bool RayClearForTest(float x, float feetY, float z) => BodyHeightClearAt(x, feetY, z);

        public bool VolumeBlockedForTest(float x, float feetY, float z)
            => BodyVolumeBlocked(new Vector3(x, feetY, z));

        /// <summary>
        /// **测试入口**：落点地面是否在当前脚高的一步台阶内（<see cref="GroundWithinStep"/>）。
        /// 探针拿它与 <see cref="BitmapClearForTest"/> 组合，就能在同一份构建上复算
        /// </summary>
        public bool GroundWithinStepForTest(Vector3 pos) => GroundWithinStep(pos);

        /// <summary>
        /// </summary>
        public bool BodyHeightClearForTest(Vector3 pos, float radius, out bool legacy)
        {
            legacy = LegacyBodyHeightClear(pos, radius);
            return BodyHeightClear(pos, radius);
        }

        /// <summary>格式化为 Vector3（InvariantCulture —— 与全工程同口径，避免小数点变逗号）。</summary>
        private static string V(Vector3 v) =>
            "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";

        /// <summary>定点格式化（InvariantCulture）。</summary>
        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

        // ==================================================================
        //  标记点
        // ==================================================================

        public Vector3[] Points(string marker)
        {
            if (_markers == null)
            {
                WarnOnce("markers.notready",
                    _markersFailed
                        ? "标记表加载失败，Points() 返回空 —— AI/包点/买枪区都会退化"
                        : "标记表尚未加载完成，Points() 暂时返回空");
                return Array.Empty<Vector3>();
            }
            if (_markers.TryGetValue(marker, out var pts) && pts.Length > 0) return pts;

            ErrorOnce("marker.missing." + marker,
                $"地图上没有标记 \"{marker}\"（应为 CsMarkers 常量）—— 该玩法会静默失效");
            return Array.Empty<Vector3>();
        }

        public bool TryGetPoint(string marker, out Vector3 point)
        {
            var pts = Points(marker);
            if (pts.Length == 0)
            {
                point = default;
                return false;
            }
            // 标记点随机取用：**玩法**（AI 拿到的巡逻点/包点会决定它往哪走、在哪架枪）。
            // 走 CsRng 的 MapMarkerPick 流 —— 同一局同一调用序 ⇒ 同一个点，可重放。
            point = pts[CsRng.Stream(CsRngStream.MapMarkerPick).Next(0, pts.Length)];
            return true;
        }

        /// <summary>出生点：Spawn_T + Spawn_CT 汇总后按索引循环取用。</summary>
        public Vector3 GetSpawnPoint(int index)
        {
            if (_spawns == null || _spawns.Length == 0)
            {
                ErrorOnce("spawn.empty", "出生点为空（标记表里既没有 Spawn_T 也没有 Spawn_CT）→ 用原点兜底");
                return Vector3.zero;
            }
            int i = index % _spawns.Length;
            if (i < 0) i += _spawns.Length;
            return _spawns[i];
        }

        // ==================================================================
        //  日志降频
        // ==================================================================

        private void WarnOnce(string key, string msg)
        {
            if (_loggedKeys.Add(key)) Game.Logger?.Warn(Tag, msg);
        }

        private void ErrorOnce(string key, string msg)
        {
            if (_loggedKeys.Add(key)) Game.Logger?.Error(Tag, msg);
        }
    }
}
