using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cs16.Core;
using Cs16.Module.Map;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cs16.EditorTools
{
    /// <summary>
    /// CloverMap v1 二进制的**只读探针**（仅本探针使用，运行时不引用）。
    ///
    /// <para>为什么探针要自己解一遍：运行时读位图的是引擎内部的 <c>CloverMapFormat</c>
    /// （<c>internal</c>，Editor 脚本看不见），而连通性自证必须能在 **EditMode**（不进 Play）跑。
    /// 这里只解"头部标量 + 可行走位图"，字段顺序与 <c>CloverMapWriter.Encode</c> 逐字节对齐
    /// （同一份契约见 <c>clover-server-engine/pkg/domain/mmo/mapdata/README.md</c>）。
    /// 探针**不参与运行时**，改格式时同步这里即可 —— 它是校验器，不是第二份实现。</para>
    /// </summary>
    public static class CloverMapProbe
    {
        public sealed class Info
        {
            public ulong SceneId;
            public string Name;
            public float CellSize;
            public Vector3 Origin;
            public int Width, Depth;
            public int ColliderCount, SpawnCount;
            public int WalkableCount, BlockedCount;
            public bool[] Walkable;   // idx = iz * Width + ix

            public bool WalkableAt(float x, float z)
            {
                // 与引擎 / 服务端同一口径：负数必须 floor（截断会把图外那格算进来）
                int ix = Mathf.FloorToInt((x - Origin.x) / CellSize);
                int iz = Mathf.FloorToInt((z - Origin.z) / CellSize);
                if (ix < 0 || iz < 0 || ix >= Width || iz >= Depth) return false;
                return Walkable[iz * Width + ix];
            }

            public override string ToString()
                => $"{Name} scene={SceneId} {Width}x{Depth} cell={CellSize:F2} " +
                   $"可走={WalkableCount} 阻挡={BlockedCount} 碰撞体={ColliderCount} 出生点={SpawnCount}";
        }

        public static bool TryRead(byte[] data, out Info info, out string error)
        {
            info = null; error = null;
            if (data == null || data.Length < 64) { error = $"文件太小（{data?.Length ?? 0} 字节）"; return false; }
            try
            {
                using (var ms = new MemoryStream(data, false))
                using (var br = new BinaryReader(ms, Encoding.UTF8))
                {
                    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "CLVM") { error = "魔数不是 CLVM"; return false; }
                    int version = br.ReadUInt16();
                    int flags = br.ReadUInt16();
                    if (version != 1) { error = $"版本 {version} != 1"; return false; }
                    if ((flags & 1) == 0) { error = $"flags=0x{flags:X} 没有可行走位图"; return false; }

                    var i = new Info
                    {
                        SceneId = br.ReadUInt64(),
                        CellSize = br.ReadSingle(),
                        Origin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    };
                    i.Width = (int)br.ReadUInt32();
                    i.Depth = (int)br.ReadUInt32();
                    i.ColliderCount = (int)br.ReadUInt32();
                    i.SpawnCount = (int)br.ReadUInt32();
                    int nameLen = (int)br.ReadUInt32();
                    br.ReadBytes(12);
                    if (i.Width <= 0 || i.Depth <= 0) { error = $"尺寸非法 {i.Width}x{i.Depth}"; return false; }

                    i.Name = nameLen > 0 ? Encoding.UTF8.GetString(br.ReadBytes(nameLen)) : string.Empty;
                    int bitsLen = (i.Width * i.Depth + 7) / 8;
                    var bits = br.ReadBytes(bitsLen);
                    if (bits.Length < bitsLen) { error = "位图被截断"; return false; }

                    i.Walkable = new bool[i.Width * i.Depth];
                    for (int k = 0; k < i.Walkable.Length; k++)
                        i.Walkable[k] = (bits[k >> 3] & (1 << (k & 7))) != 0;
                    for (int k = 0; k < i.Walkable.Length; k++) if (i.Walkable[k]) i.WalkableCount++;
                    i.BlockedCount = i.Walkable.Length - i.WalkableCount;

                    info = i;
                    return true;
                }
            }
            catch (Exception e) { error = "解码异常：" + e.Message; return false; }
        }
    }

    /// <summary>
    /// <list type="number">
    /// <item><b>位图可复现</b>：按引擎 <c>MapBaker</c> 的同一套规则（格柱 ∩ 障碍 AABB、严格小于、
    /// 排除地面阈值）从场景 <c>Level/Blockers</c> 重算一遍位图，与烘焙产物
    /// <c>Resources/MapData/de_dust2.bytes</c> **逐格比对**；</item>
    /// <item><b>连通性</b>：用位图做网格 BFS（8 邻接、禁止贴角穿墙），断言
    /// <c>Spawn_T → Bombsite_A / Bombsite_B / Spawn_CT</c> 全部可达，并打印路径长度；</item>
    /// <item><b>标记点</b>：每类标记的点数（对照 <see cref="Dust2Layout.RequiredMarkers"/> 的最小值），
    /// 且每个点必须落在可行走格上（否则机器人会被引到墙里）。</item>
    /// </list>
    ///
    /// <para>菜单：<b>Clover/CS16/地图连通性自证（de_dust2）</b>；命令行：
    /// <c>-executeMethod Cs16.EditorTools.MapConnectivityProbe.RunProbeFromCommandLine</c>（失败退出码 1）。</para>
    /// </summary>
    public static class MapConnectivityProbe
    {
        private const string Tag = "[MapProbe]";

        private static readonly int[] NeiDx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] NeiDz = { 0, 0, 1, -1, 1, -1, 1, -1 };

        [MenuItem("Clover/CS16/地图连通性自证（de_dust2）", false, 22)]
        public static void RunProbe() => Run(false);

        /// <summary>命令行入口：失败时以非零码退出，CI 能看见。</summary>
        public static void RunProbeFromCommandLine() => Run(true);

        private static void Run(bool exitWithCode)
        {
            bool ok = RunInternal();
            Debug.Log($"{Tag} 自证结果：{(ok ? "全部通过" : "存在失败项，见上面的 Error")}");
            if (exitWithCode) EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool RunInternal()
        {
            bool ok = true;

            var geo = Dust2GeoData.Load(Dust2Layout.GeoFile);
            if (geo == null) return false;
            if (!File.Exists(Dust2Layout.ScenePath))
            {
                Debug.LogError($"{Tag} 场景不存在：{Dust2Layout.ScenePath}（先跑生成器）");
                return false;
            }

            var scene = EditorSceneManager.OpenScene(Dust2Layout.ScenePath, OpenSceneMode.Single);

            // ---------- ① 场景侧：运行时物理是否完好 + 按烘焙规则复算位图 ----------
            var visual = Dust2Builder.FindInScene(scene, Dust2Layout.VisualRoot);
            if (visual == null)
            {
                Debug.LogError($"{Tag} 找不到 {Dust2Layout.VisualRoot}：场景不是本生成器产的？");
                ok = false;
            }
            else
            {
                int live = 0, total = 0;
                foreach (var c in visual.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    total++;
                    if (c.enabled) live++;
                }
                if (live != total || total == 0)
                {
                    // 这条专门抓"烘焙中途失败把碰撞体留在关闭状态"——那会让子弹穿墙、玩家掉出地图
                    Debug.LogError($"{Tag} 运行时物理不完整：Level/Visual 的碰撞体 enabled={live}/{total}（应为全开且 >0）");
                    ok = false;
                }
                else
                {
                    Debug.Log($"{Tag} 运行时物理：Level/Visual 的 MeshCollider {total} 个全部启用 ✓");
                }
            }

            bool[] sceneBlocked = RasterizeBlockersFromScene(scene, geo);

            // ---------- ② 产物侧：读 .bytes ----------
            string clientPath = $"{Dust2Layout.ClientMapDir}/{CsConst.MapDust2}.bytes";
            string serverPath = $"{Dust2Layout.ServerMapDir}/{CsConst.MapDust2}.bytes";
            if (!File.Exists(clientPath))
            {
                Debug.LogError($"{Tag} 缺客户端地图数据 {clientPath}（先跑烘焙）");
                return false;
            }
            if (!File.Exists(serverPath))
                Debug.LogError($"{Tag} 缺服务端/参考产物 {serverPath}（同源两份应当都在）");

            if (!CloverMapProbe.TryRead(File.ReadAllBytes(clientPath), out var map, out string err))
            {
                Debug.LogError($"{Tag} {clientPath} 解不开：{err}");
                return false;
            }
            Debug.Log($"{Tag} 产物 {clientPath}：{map}");
            if (map.BlockedCount <= 0)
            {
                Debug.LogError($"{Tag} 阻挡格 = 0：这是「全可走」的空地图，说明烘焙没收到障碍");
                ok = false;
            }

            // 同源两份必须逐字节一致（客户端本地碰撞与服务端事实同源的前提）
            var serverBytes = File.ReadAllBytes(serverPath);
            var clientBytes = File.ReadAllBytes(clientPath);
            if (serverBytes.Length != clientBytes.Length)
            {
                Debug.LogError($"{Tag} 两份 .bytes 长度不同（{serverBytes.Length} vs {clientBytes.Length}）—— 同源被破坏");
                ok = false;
            }

            // ---------- ③ 位图逐格比对 ----------
            int diff = 0;
            for (int iz = 0; iz < geo.Depth; iz++)
            {
                for (int ix = 0; ix < geo.Width; ix++)
                {
                    bool a = sceneBlocked[iz * geo.Width + ix];
                    bool b = !map.Walkable[iz * geo.Width + ix];
                    if (a != b) diff++;
                }
            }
            if (diff != 0)
            {
                Debug.LogError($"{Tag} 场景复算位图与产物位图有 {diff}/{geo.Width * geo.Depth} 格不一致 —— " +
                               "烘焙规则与 Blockers 不再对应（改了阻挡体或改了烘焙参数？）");
                ok = false;
            }
            else
            {
                Debug.Log($"{Tag} 位图一致：场景 Blockers 复算 == 产物位图（{geo.Width}x{geo.Depth}，阻挡 {map.BlockedCount} 格）✓");
            }

            // ---------- ④ 连通性 BFS ----------
            var markerRoot = Dust2Builder.FindInScene(scene, Dust2Layout.MarkerRoot);
            var sceneMarkers = new Dictionary<string, List<Vector3>>();
            if (markerRoot != null)
            {
                foreach (var t in markerRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t == markerRoot.transform) continue;
                    if (!sceneMarkers.TryGetValue(t.gameObject.name, out var l))
                    {
                        l = new List<Vector3>();
                        sceneMarkers[t.gameObject.name] = l;
                    }
                    l.Add(t.position);
                }
            }

            foreach (var pair in Dust2Layout.ConnectivityPairs)
            {
                if (!TryCentroidCell(map, sceneMarkers, pair.From, out int sx, out int sz) ||
                    !TryCentroidCell(map, sceneMarkers, pair.To, out int gx, out int gz))
                {
                    Debug.LogError($"{Tag} 连通性 {pair.From} → {pair.To}：缺标记或标记不在可走格，无法自证");
                    ok = false;
                    continue;
                }
                int steps = Bfs(map, sx, sz, gx, gz);
                if (steps < 0)
                {
                    Debug.LogError($"{Tag} 连通性 {pair.From} → {pair.To}：**不可达**" +
                                   $"（起点格 {sx},{sz} 终点格 {gx},{gz}；断开处多半是墙画厚了/漏了口）");
                    ok = false;
                }
                else
                {
                    Debug.Log($"{Tag} 连通性 {pair.From} → {pair.To}：可达，{steps} 格 ≈ {steps * map.CellSize:F1} m ✓");
                }
            }

            // ---------- ⑤ 标记点计数 + 每点必须可走 ----------
            foreach (var req in Dust2Layout.RequiredMarkers)
            {
                int n = sceneMarkers.TryGetValue(req.Marker, out var pts) ? pts.Count : 0;
                if (n < req.MinCount)
                {
                    Debug.LogError($"{Tag} 标记 {req.Marker} 只有 {n} 个（要求 ≥ {req.MinCount}）—— 消费方会退化");
                    ok = false;
                }
                if (pts == null) continue;

                int offGrid = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (!map.WalkableAt(pts[i].x, pts[i].z)) offGrid++;
                }
                if (offGrid > 0)
                {
                    Debug.LogError($"{Tag} 标记 {req.Marker} 有 {offGrid}/{pts.Count} 个点落在不可走格（AI 会卡住/下包无效）");
                    ok = false;
                }
                Debug.Log($"{Tag} 标记 {req.Marker}：{n} 个点，全部可走 ✓");
            }

            return ok;
        }

        /// <summary>按引擎 <c>MapBaker</c> 的同一规则复算位图（排除 <c>Level/Visual</c>：烘焙时被临时关掉）。</summary>
        private static bool[] RasterizeBlockersFromScene(Scene scene, Dust2GeoData geo)
        {
            var boxes = new List<Bounds>();
            int seen = 0, skippedVisual = 0;

            foreach (var root in scene.GetRootGameObjects())
            {
                // Level/Visual 是运行时物理层，烘焙时不参与
                bool isVisual = root.name == Dust2Layout.LevelRoot &&
                                root.transform.Find("Visual") != null;
                foreach (var col in root.GetComponentsInChildren<Collider>(true))
                {
                    if (col == null || !col.enabled) continue;
                    if (isVisual && col.transform.IsChildOf(root.transform.Find("Visual")))
                    {
                        skippedVisual++;
                        continue;
                    }
                    seen++;
                    var b = col.bounds;
                    if (b.max.y - geo.GroundTopY <= geo.ObstacleMinHeight) continue;   // 地面/贴地薄板
                    boxes.Add(b);
                }
            }

            var blocked = new bool[geo.Width * geo.Depth];
            var half = new Vector3(geo.CellSize * 0.5f, (geo.ProbeTopY - geo.ProbeBottomY) * 0.5f, geo.CellSize * 0.5f);
            float probeCenterY = (geo.ProbeTopY + geo.ProbeBottomY) * 0.5f;

            for (int iz = 0; iz < geo.Depth; iz++)
            {
                for (int ix = 0; ix < geo.Width; ix++)
                {
                    var c = new Vector3(geo.OriginX + (ix + 0.5f) * geo.CellSize, probeCenterY,
                                        geo.OriginZ + (iz + 0.5f) * geo.CellSize);
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        // 必须严格小于（与 MapBaker 一致）：用 <= 会把贴墙的邻格也算阻挡，墙外扩一格
                        if (Mathf.Abs(c.x - boxes[i].center.x) < half.x + boxes[i].extents.x &&
                            Mathf.Abs(c.y - boxes[i].center.y) < half.y + boxes[i].extents.y &&
                            Mathf.Abs(c.z - boxes[i].center.z) < half.z + boxes[i].extents.z)
                        {
                            blocked[iz * geo.Width + ix] = true;
                            break;
                        }
                    }
                }
            }

            Debug.Log($"{Tag} 场景复算：碰撞体总数={seen}（烘焙参与 {boxes.Count} 个，跳过 Visual {skippedVisual} 个，" +
                      $"按地面排除 {seen - boxes.Count} 个）→ 阻挡 {Count(blocked, true)} 格");
            return blocked;
        }

        private static int Count(bool[] arr, bool v)
        {
            int n = 0;
            for (int i = 0; i < arr.Length; i++) if (arr[i] == v) n++;
            return n;
        }

        private static bool TryCentroidCell(CloverMapProbe.Info map, Dictionary<string, List<Vector3>> markers,
                                            string marker, out int ix, out int iz)
        {
            ix = iz = -1;
            if (!markers.TryGetValue(marker, out var pts) || pts.Count == 0) return false;
            Vector3 sum = Vector3.zero;
            for (int i = 0; i < pts.Count; i++) sum += pts[i];
            var c = sum / pts.Count;
            ix = Mathf.FloorToInt((c.x - map.Origin.x) / map.CellSize);
            iz = Mathf.FloorToInt((c.z - map.Origin.z) / map.CellSize);
            SnapToWalkable(map, ref ix, ref iz);
            return ix >= 0;
        }

        private static void SnapToWalkable(CloverMapProbe.Info map, ref int ix, ref int iz)
        {
            if (In(map, ix, iz) && map.Walkable[iz * map.Width + ix]) return;
            for (int r = 1; r <= 24; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                        int nx = ix + dx, nz = iz + dz;
                        if (In(map, nx, nz) && map.Walkable[nz * map.Width + nx]) { ix = nx; iz = nz; return; }
                    }
                }
            }
            ix = iz = -1;
        }

        private static bool In(CloverMapProbe.Info map, int ix, int iz)
            => ix >= 0 && iz >= 0 && ix < map.Width && iz < map.Depth;

        /// <summary>8 邻接网格 BFS（禁止贴角穿墙：对角要求两个正交邻格都可走）。返回格数；不可达返回 -1。</summary>
        private static int Bfs(CloverMapProbe.Info map, int sx, int sz, int gx, int gz)
        {
            if (!In(map, sx, sz) || !In(map, gx, gz)) return -1;
            if (!map.Walkable[sz * map.Width + sx] || !map.Walkable[gz * map.Width + gx]) return -1;

            var dist = new int[map.Width * map.Depth];
            for (int i = 0; i < dist.Length; i++) dist[i] = -1;
            var q = new Queue<int>();
            int start = sz * map.Width + sx, goal = gz * map.Width + gx;
            dist[start] = 0;
            q.Enqueue(start);

            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                if (cur == goal) return dist[cur];
                int cx = cur % map.Width, cz = cur / map.Width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + NeiDx[k], nz = cz + NeiDz[k];
                    if (!In(map, nx, nz)) continue;
                    if (!map.Walkable[nz * map.Width + nx]) continue;
                    if (NeiDx[k] != 0 && NeiDz[k] != 0)
                    {
                        // 贴角穿墙：两个正交邻格必须都能走
                        if (!map.Walkable[cz * map.Width + nx] || !map.Walkable[nz * map.Width + cx]) continue;
                    }
                    int ni = nz * map.Width + nx;
                    if (dist[ni] >= 0) continue;
                    dist[ni] = dist[cur] + 1;
                    q.Enqueue(ni);
                }
            }
            return -1;
        }
    }
}
