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
    /// <para><b>数据来源（两份，各司其职）</b></para>
    /// <list type="bullet">
    /// <item><b>可行走位图</b>：引擎 <c>Game.Map</c>（<c>Resources/MapData/de_dust2.bytes</c>，
    /// 由 <c>Clover/CS16/烘焙 de_dust2</c> 导出，与服务端同源）。它只回答"这一格能不能走"。</item>
    /// <item><b>标记点表</b>：<c>Resources/MapData/de_dust2_markers.bytes</c>（由
    /// <c>Dust2Builder.ExportMarkerResource</c> 从场景里的标记对象导出）。位图里没有名字，
    /// 而 AI/包点/买枪区要按名字取点，所以必须单独带一份 —— 生成时名字写错这里就会报 Error。</item>
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

        /// <summary>运行时标记表资源路径真源 = <see cref="ResPaths.MapDust2Markers"/>
        /// （生成器侧 <c>Editor/MapGen/Dust2Layout</c> 也走同一个常量 ——
        /// 路径字面量只留在 <see cref="ResPaths"/>，两边不再各写一遍）。</summary>

        /// <summary>扫掠细分步长上限（米）：一帧位移切成 ≤ 这个长度的段，避免高速穿过薄墙。</summary>
        private const float MaxSweepStep = 0.25f;

        /// <summary>8 向采样的对角系数（√2/2）。</summary>
        private const float DiagonalScale = 0.70710678f;

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
                // 少了它 Points() 全是空数组，AI/包点会静默失效。
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
        /// 载入标记点表。失败**不阻断**地图加载（地图仍可玩），但一定打 Error：
        /// 标记缺失的表现是"机器人不动、下包无效"，不打日志就永远查不出来。
        /// </summary>
        private void LoadMarkers(Action done)
        {
            if (_markersRequested)
            {
                done?.Invoke();
                return;
            }
            _markersRequested = true;

            if (Game.Res == null)
            {
                _markersFailed = true;
                Game.Logger?.Error(Tag, $"标记表加载失败：Game.Res 为空（Resources/{ResPaths.MapDust2Markers}.bytes 读不了）");
                done?.Invoke();
                return;
            }

            Game.Res.LoadAsset<TextAsset>(ResPaths.MapDust2Markers, ta =>
            {
                if (ta == null)
                {
                    _markersFailed = true;
                    Game.Logger?.Error(Tag,
                        $"标记表缺失：Resources/{ResPaths.MapDust2Markers}.bytes 不存在 —— " +
                        "AI/包点/买枪区都会失效（跑 Clover/CS16/生成 de_dust2 场景 重新导出）");
                    done?.Invoke();
                    return;
                }

                ParseMarkers(ta.text);
                done?.Invoke();
            });
        }

        private void ParseMarkers(string text)
        {
            var table = new Dictionary<string, List<Vector3>>();
            int bad = 0;
            string firstBad = null;

            if (!string.IsNullOrEmpty(text))
            {
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    var parts = line.Split(' ');
                    if (parts.Length < 4)
                    {
                        if (bad++ == 0) firstBad = line;
                        continue;
                    }
                    if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                        !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                        !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        if (bad++ == 0) firstBad = line;
                        continue;
                    }
                    if (!table.TryGetValue(parts[0], out var list))
                    {
                        list = new List<Vector3>();
                        table[parts[0]] = list;
                    }
                    list.Add(new Vector3(x, y, z));
                }
            }

            if (bad > 0)
                Game.Logger?.Warn(Tag, $"标记表有 {bad} 行无法解析（首例：\"{firstBad}\"），已跳过");

            _markers = new Dictionary<string, Vector3[]>(table.Count);
            foreach (var kv in table) _markers[kv.Key] = kv.Value.ToArray();

            // 出生点：Spawn_T + Spawn_CT 汇总（GetSpawnPoint 按索引循环取用）
            var spawns = new List<Vector3>();
            if (_markers.TryGetValue(CsMarkers.SpawnT, out var t)) spawns.AddRange(t);
            if (_markers.TryGetValue(CsMarkers.SpawnCT, out var ct)) spawns.AddRange(ct);
            _spawns = spawns.ToArray();

            int total = 0;
            foreach (var kv in _markers) total += kv.Value.Length;

            // 契约要求的标记全点名核对：缺哪个报哪个（每个名字只报一次）
            foreach (var name in RequiredMarkerNames)
            {
                if (!_markers.ContainsKey(name))
                    Game.Logger?.Error(Tag, $"标记缺失：场景/标记表里没有 \"{name}\" —— " +
                                            "对应玩法（出生点/包点/买枪区/机器人路线）会静默退化");
            }

            Game.Logger?.Info(Tag, $"标记表已加载：{_markers.Count} 类 / {total} 点（出生点 {_spawns.Length} 个）");
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
                // 但**必须留痕**：否则"进图后走哪都不撞墙"这个症状会没人认领。
                WarnOnce("walk.notloaded", "地图未加载，WalkableAt 一律返回 true（不阻挡）—— 检查地图数据是否烘出来了");
                return true;
            }
            return m.WalkableAt(x, z);
        }

        /// <summary>中心 + 8 向（半径 <paramref name="radius"/>）全可走才算站得下。</summary>
        public bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius)
        {
            if (!IsLoaded)
            {
                WarnOnce("stand.notloaded", "地图未加载，CanStand 一律返回 true（不阻挡）");
                return true;
            }

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
        /// 本地碰撞解算：**扫掠细分（≤0.25m）+ 分轴滑墙（先 X 后 Z）+ 台阶（≤ StepUpHeight）**。
        /// Y 分量原样跟随目标（重力/落地由调用方用 <see cref="SampleGround"/> 收尾）。
        /// </summary>
        public Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius)
        {
            if (!IsLoaded)
            {
                WarnOnce("move.notloaded", "地图未加载，ResolveMove 原样放行（不做本地碰撞）");
                return to;
            }

            // 解除卡死：起点本身就站不下（出生点贴墙 / 被挤进墙里）→ 能直接到目标就去，否则别乱动
            if (!CanStand(from, radius)) return WalkableAt(to.x, to.z) ? to : from;

            float dx = to.x - from.x;
            float dz = to.z - from.z;
            float dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / MaxSweepStep));

            var cur = from;
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                var want = new Vector3(from.x + dx * t, to.y, from.z + dz * t);
                cur = StepOnce(cur, want, radius);
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
        /// <item><b>格子中心</b>必须可走（中心在墙里 ⇒ 这就是墙，没得商量）；</item>
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
            if (!WalkableAt(target.x, target.z)) return false;

            // 陡坡不算地面（原版 PM_WalkMove：下探后 `if (trace.plane.normal[2] < 0.7) goto usedown;`
            // ⇒ 放弃"迈上去"这条路径）。缺了它就会沿陡面走上去（= 用户报的"坡道穿模"）。
            var hasGround = TrySampleGround(target, out var point, out var normal);
            if (hasGround && normal.y < CsConst.MaxStandableSlopeNormalZ) return false;
            if (hasGround && point.y - from.y > CsConst.StepUpHeight) return false;

            var dir = new Vector3(target.x - from.x, 0f, target.z - from.z);
            float dist = dir.magnitude;
            if (dist < 0.001f) return false;
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
        /// <para><b>层名解析失败 ⇒ 一定报 Error，绝不静默</b>：这条路径曾经的实测后果是
        /// <c>NameToLayer("CsWorld") = -1</c> ⇒ 掩码退化成全层 ⇒ 贴地射线命中**角色自己的命中盒** ⇒
        /// 命中点跟着角色一起上移 ⇒ actor 的 y 每 0.05s 被抬一次（实测 -3 → 500+）。
        /// 现在即便退化也把层名与 <c>NameToLayer</c> 的返回值打进 Error，
        /// 并把"命中盒会被当地面"这句后果写进日志。</para>
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
            point = pts[UnityEngine.Random.Range(0, pts.Length)];
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
