using System;
using System.Collections.Generic;
using System.IO;
using Cs16.Core;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 从原版 <c>de_dust2.bsp</c> 的**渲染节点树落 leaf**，把"可站面之下的 solid 区间"算成实心盒表。
    ///
    /// <para><b>为什么需要它</b>：GoldSrc 的 BSP 只把**空腔**用面围起来，没被围出来的部分属于
    /// <b>solid leaf（leaf 0）</b>。本工程的转换链只搬了面（<c>de_dust2_geo.bin</c> 的三种段里没有 solid、
    /// 没有 leaf），所以原版"地板以下整块实心"的地方在 Unity 里是**空的**
    /// ⇒ 角色/玩家能滑进原版根本进不去的岩体。这里把那段实心空间补成 Unity 的实心碰撞体。</para>
    ///
    /// <para><b>判据（逐格、确定性）</b>：</para>
    /// <list type="number">
    /// <item>把 <c>de_dust2_geo.bin</c> 的**朝上三角面**（法线 y 分量 &gt; 0.1）按格心落进位图格，
    /// 得到每格的"可站面"高度集合（与 <see cref="Dust2GeoData"/> 的格坐标口径一致）；</item>
    /// <item>对每个可站面，先用 leaf 判一次"它**下面**是不是实心"（不是 ⇒ 这个面不是 solid 的顶面，
    /// 例如 <c>aaatrigger</c> 那类方向相反的刷面，直接跳过）；</item>
    /// <item>从面向下扫，找到**第一个空腔** ⇒ 实心区间 = <c>[空腔顶, 可站面]</c>；一路实心到下限则用下限
    /// （全图最低点再往下留一段，保证掉进实心里的角色一路都被体积挡住）；</item>
    /// <item>盒顶 = 可站面本身（**绝不高出**它，否则会把合法地板顶掉），然后按
    /// <c>(YMax, YMin)</c> 量化后贪心合并成矩形（平地板会合并成大块，盒数从几千降到几百）。</item>
    /// </list>
    ///
    /// <para><b>为什么不参与烘焙</b>：本表生成的实心盒由 <see cref="Dust2Builder"/> 挂在
    /// <c>Level/Visual</c> 下，而 <see cref="MapBakeRunner"/> 在烘焙前会临时关掉
    /// <c>Level/Visual</c> 的全部 Collider ⇒ 它们不进位图 ⇒ <c>de_dust2.bytes</c> 的位图段不变。
    /// 落点选 Visual 而不是新开一层，正是为了复用那道"烘焙时整棵子树关掉"的既有机制。</para>
    /// </summary>
    internal static class Dust2Solid
    {
        /// <summary>一个实心盒（格坐标闭区间 + 世界 Y 区间，与 <see cref="Dust2GeoData.Blocker"/> 同形）。</summary>
        internal struct Box
        {
            public int Ix0, Iz0, Ix1, Iz1;
            public float YMin, YMax;
        }

        // ==================================================================
        //  原版 BSP ↔ Unity 世界 的坐标对应
        // ==================================================================

        /// <summary>GoldSrc 1 unit = 1 inch = 0.0254 m。</summary>
        private const float Unit = 0.0254f;

        /// <summary><c>Unity.x = Unit * HL.x + HlOriginX</c>（转换脚本把地图按包围盒居中后的偏移）。</summary>
        private const float HlOriginX = 9.7536f;
        /// <summary><c>Unity.z = Unit * HL.y + HlOriginZ</c>（GoldSrc 是 Z-up ⇒ 世界 Z 对应 HL 的 Y）。</summary>
        private const float HlOriginZ = -28.4480f;

        /// <summary>Unity 世界坐标 → GoldSrc 世界坐标（单位：原版 unit），与 BSP 的平面/节点同空间。</summary>
        private static void ToHalfLife(float ux, float uy, float uz, out float hx, out float hy, out float hz)
        {
            hx = (ux - HlOriginX) / Unit;
            hy = (uz - HlOriginZ) / Unit;
            hz = uy / Unit;
        }

        // ==================================================================
        //  扫描参数
        // ==================================================================

        /// <summary>朝上三角面的判定：法线 y 分量下限（与位图同格口径下的"能当可站面"）。</summary>
        private const float UpFaceNormalY = 0.1f;

        /// <summary>可站面高度的去重粒度（米）：同格内两片相差不到它的面算同一个面。</summary>
        private const float FaceDedup = 0.02f;

        /// <summary>从面向下起扫的偏移（米）：绕开"面正好落在 leaf 边界"的浮点歧义。</summary>
        private const float SurfaceEps = 0.05f;

        /// <summary>向下粗扫步长（米）：先按它找到第一个空腔，再二分。</summary>
        private const float CoarseStep = 0.25f;

        /// <summary>二分次数（<see cref="CoarseStep"/> 上 14 次 ⇒ 分辨率 ≈ 15 µm）。</summary>
        private const int BisectSteps = 14;

        /// <summary>盒底再往全图最低点以下留的深度（米）。</summary>
        private const float BottomMargin = 10f;

        /// <summary>合并时 <c>(YMax, YMin)</c> 的量化粒度（米）：向下取整 ⇒ 盒顶永不高于面、盒底只会更深。</summary>
        private const float MergeQuantum = 0.02f;

        // ==================================================================
        //  入口
        // ==================================================================

        /// <summary>
        /// 算这张实心盒表。<paramref name="walkable"/> = 位图可走格（<c>idx = iz * Width + ix</c>）。
        /// </summary>
        /// <param name="probeCount">未合并的候选盒数（诊断用）。</param>
        /// <param name="dangerCount">盒顶插进"更低一层的站立带"的盒数（必须为 0，见 <see cref="Dust2Builder"/> 的报错）。</param>
        /// <param name="aboveSolidCount">最大可站面**之上仍实心**的格数（坐标对应 / 数据的自检量）。</param>
        /// <param name="faceCells">有可站面的可走格数。</param>
        internal static Box[] Build(Dust2GeoData geo, bool[] walkable,
                                    out int probeCount, out int dangerCount,
                                    out int aboveSolidCount, out int faceCells)
        {
            probeCount = dangerCount = aboveSolidCount = faceCells = 0;
            if (geo == null || walkable == null || walkable.Length != geo.Width * geo.Depth)
            {
                Debug.LogError("[Dust2Solid] 位图与几何不匹配 ⇒ 不补实体");
                return Array.Empty<Box>();
            }

            var bsp = BspTree.Load(Dust2Layout.BspFile);
            if (bsp == null) return Array.Empty<Box>();

            var faces = RasterizeUpFaces(geo);
            float limit = Mathf.Floor(geo.WorldMin.y) - BottomMargin;

            var boxes = new List<Box>();
            var lowerFaces = new List<float>();
            for (int iz = 0; iz < geo.Depth; iz++)
            {
                for (int ix = 0; ix < geo.Width; ix++)
                {
                    if (!walkable[iz * geo.Width + ix]) continue;
                    if (!faces.TryGetValue(iz * geo.Width + ix, out var ys) || ys.Count == 0) continue;
                    faceCells++;

                    float ux = geo.OriginX + (ix + 0.5f) * geo.CellSize;
                    float uz = geo.OriginZ + (iz + 0.5f) * geo.CellSize;
                    ys.Sort();

                    // 自检：最高可站面**之上**应当是空的（坐标对应错了这里会立刻塌掉）
                    if (bsp.IsSolid(ux, ys[ys.Count - 1] + 0.3f, uz)) aboveSolidCount++;

                    lowerFaces.Clear();
                    for (int f = 0; f < ys.Count; f++)
                    {
                        float yTop = ys[f];
                        if (!TrySolidBottom(bsp, ux, uz, yTop, limit, out float yBottom)) continue;
                        probeCount++;

                        // 盒顶插进"更低可站面"的站立带 ⇒ 会把那一层顶掉（数出来给调用方报错）
                        for (int g = 0; g < lowerFaces.Count; g++)
                        {
                            float lo = lowerFaces[g];
                            float bandLo = lo + CsConst.GroundCheckDistance + CsConst.PlayerRadius;
                            float bandHi = lo + CsConst.StandHeight - CsConst.PlayerRadius;
                            if (yTop > bandLo && yBottom < bandHi) dangerCount++;
                        }

                        lowerFaces.Add(yTop);
                        boxes.Add(new Box { Ix0 = ix, Iz0 = iz, Ix1 = ix, Iz1 = iz, YMin = yBottom, YMax = yTop });
                    }
                }
            }

            return Merge(boxes);
        }

        /// <summary>
        /// 从 <paramref name="yTop"/> 往下的实心区间底。返回 false = 面下方**不是**实心（这个面不是 solid 的顶面）。
        /// </summary>
        private static bool TrySolidBottom(BspTree bsp, float ux, float uz, float yTop, float limit, out float yBottom)
        {
            yBottom = 0f;
            float y = yTop - SurfaceEps;
            if (!bsp.IsSolid(ux, y, uz)) return false;

            float empty = float.NaN;
            while (y - CoarseStep > limit)
            {
                float next = y - CoarseStep;
                if (!bsp.IsSolid(ux, next, uz)) { empty = next; break; }
                y = next;
            }
            if (float.IsNaN(empty))
            {
                yBottom = limit;                                  // 一路实心到下限
                return true;
            }

            // [empty, y] 上二分出实心/空腔的边界（empty 空、y 实心）
            for (int i = 0; i < BisectSteps; i++)
            {
                float mid = (y + empty) * 0.5f;
                if (bsp.IsSolid(ux, mid, uz)) y = mid; else empty = mid;
            }
            yBottom = y;
            return true;
        }

        // ==================================================================
        //  朝上面 → 每格的"可站面"高度集合
        // ==================================================================

        private static Dictionary<int, List<float>> RasterizeUpFaces(Dust2GeoData geo)
        {
            var result = new Dictionary<int, List<float>>();
            float cell = geo.CellSize;
            float ox = geo.OriginX, oz = geo.OriginZ;

            for (int g = 0; g < geo.Groups.Length; g++)
            {
                var grp = geo.Groups[g];
                if (grp?.Indices == null || grp.Indices.Length < 3 || grp.Vertices == null) continue;
                var idx = grp.Indices;
                var v = grp.Vertices;

                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    var a = v[idx[t]];
                    var b = v[idx[t + 1]];
                    var c = v[idx[t + 2]];

                    var n = Vector3.Cross(b - a, c - a);
                    float ln = n.magnitude;
                    if (ln < 1e-9f || n.y / ln <= UpFaceNormalY) continue;   // 只收朝上面

                    int ix0 = Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) - ox) / cell);
                    int ix1 = Mathf.FloorToInt((Mathf.Max(a.x, b.x, c.x) - ox) / cell);
                    int iz0 = Mathf.FloorToInt((Mathf.Min(a.z, b.z, c.z) - oz) / cell);
                    int iz1 = Mathf.FloorToInt((Mathf.Max(a.z, b.z, c.z) - oz) / cell);

                    for (int iz = iz0; iz <= iz1; iz++)
                    {
                        for (int ix = ix0; ix <= ix1; ix++)
                        {
                            if (!TryFaceHeightAtCellCenter(a, b, c, ox + (ix + 0.5f) * cell, oz + (iz + 0.5f) * cell,
                                                           out float y)) continue;
                            int key = iz * geo.Width + ix;
                            if (!result.TryGetValue(key, out var list))
                            {
                                list = new List<float>();
                                result[key] = list;
                            }
                            bool dup = false;
                            for (int k = 0; k < list.Count; k++)
                            {
                                if (Mathf.Abs(list[k] - y) < FaceDedup) { dup = true; break; }
                            }
                            if (!dup) list.Add(y);
                        }
                    }
                }
            }

            Debug.Log($"[Dust2Solid] 朝上面光栅化：{result.Count} 格拿到可站面");
            return result;
        }

        /// <summary>格心 <c>(px, pz)</c> 是否落在三角面内（含边）；是则给出该处的高度。</summary>
        private static bool TryFaceHeightAtCellCenter(Vector3 a, Vector3 b, Vector3 c, float px, float pz, out float y)
        {
            y = 0f;
            float d0 = (b.x - a.x) * (pz - a.z) - (b.z - a.z) * (px - a.x);
            float d1 = (c.x - b.x) * (pz - b.z) - (c.z - b.z) * (px - b.x);
            float d2 = (a.x - c.x) * (pz - c.z) - (a.z - c.z) * (px - c.x);
            bool neg = d0 < 0f || d1 < 0f || d2 < 0f;
            bool pos = d0 > 0f || d1 > 0f || d2 > 0f;
            if (neg && pos) return false;

            float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(den) < 1e-12f) return false;
            float w0 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / den;
            float w1 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / den;
            y = w0 * a.y + w1 * b.y + (1f - w0 - w1) * c.y;
            return true;
        }

        // ==================================================================
        //  合并：同 (YMax, YMin) 的格贪心并成矩形
        // ==================================================================

        private static Box[] Merge(List<Box> boxes)
        {
            if (boxes.Count == 0) return Array.Empty<Box>();

            var groups = new Dictionary<long, HashSet<long>>();
            foreach (var b in boxes)
            {
                int qMax = Mathf.FloorToInt(b.YMax / MergeQuantum);
                int qMin = Mathf.FloorToInt(b.YMin / MergeQuantum);
                long key = ((long)qMax << 32) ^ (uint)qMin;
                if (!groups.TryGetValue(key, out var set))
                {
                    set = new HashSet<long>();
                    groups[key] = set;
                }
                set.Add(((long)b.Iz0 << 32) | (uint)b.Ix0);
            }

            var merged = new List<Box>();
            foreach (var kv in groups)
            {
                float yMax = (kv.Key >> 32) * MergeQuantum;          // 还原时用整数量化值（见 MergeQuantum 注释）
                float yMin = (int)(uint)kv.Key * MergeQuantum;
                var set = kv.Value;
                var visited = new HashSet<long>();
                var ordered = new List<long>(set);
                ordered.Sort();
                foreach (var cell in ordered)
                {
                    if (visited.Contains(cell)) continue;
                    int x0 = (int)(cell & 0xFFFFFFFF);
                    int z0 = (int)(cell >> 32);
                    int x1 = x0;
                    while (set.Contains(Row(z0, x1 + 1)) && !visited.Contains(Row(z0, x1 + 1))) x1++;
                    int z1 = z0;
                    while (true)
                    {
                        bool ok = true;
                        for (int x = x0; x <= x1; x++)
                        {
                            if (!set.Contains(Row(z1 + 1, x)) || visited.Contains(Row(z1 + 1, x))) { ok = false; break; }
                        }
                        if (!ok) break;
                        z1++;
                    }
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++) visited.Add(Row(z, x));
                    merged.Add(new Box { Ix0 = x0, Iz0 = z0, Ix1 = x1, Iz1 = z1, YMin = yMin, YMax = yMax });
                }
            }
            return merged.ToArray();
        }

        private static long Row(int iz, int ix) => ((long)iz << 32) | (uint)ix;

        // ==================================================================
        //  BSP（v30）只读：平面表 + 渲染节点树 + leaf 落点
        // ==================================================================

        /// <summary>
        /// GoldSrc BSP v30 的**渲染节点树**查询：<c>leaf 0 = solid</c>。
        /// <para>节点结构（lump 5，24 字节/条）：<c>int planenum; short child0; short child1; …</c>；
        /// 平面（lump 1，20 字节/条）：<c>float nx, ny, nz, dist; int type</c>。
        /// child &lt; 0 ⇒ 叶子，下标 = <c>-(child + 1)</c>；带符号 16 位，负号即"这是叶子"。</para>
        /// </summary>
        private sealed class BspTree
        {
            private const int LumpPlanes = 1;
            private const int LumpNodes = 5;
            private const int PlaneStride = 20;
            private const int NodeStride = 24;

            private float[] _px, _py, _pz, _pd;
            private int[] _plane, _child0, _child1;
            private int _depthErrors;

            internal static BspTree Load(string path)
            {
                if (!File.Exists(path))
                {
                    Debug.LogError($"[Dust2Solid] 找不到原版 BSP：{path} ⇒ 不补实体（生成器其余部分照常）");
                    return null;
                }

                var raw = File.ReadAllBytes(path);
                if (raw.Length < 4 + 15 * 8) { Debug.LogError($"[Dust2Solid] {path} 太小，不是 BSP"); return null; }
                int ver = BitConverter.ToInt32(raw, 0);
                if (ver != 30)
                {
                    Debug.LogError($"[Dust2Solid] {path} 版本 {ver} ≠ 30（GoldSrc 渲染 BSP）⇒ 不补实体");
                    return null;
                }

                var tree = new BspTree();
                if (!tree.ReadPlanes(raw, path)) return null;
                if (!tree.ReadNodes(raw, path)) return null;
                Debug.Log($"[Dust2Solid] 原版 BSP：v{ver} 平面 {tree._px.Length} 条 / 节点 {tree._plane.Length} 个（{Path.GetFileName(path)}）");
                return tree;
            }

            private bool ReadPlanes(byte[] raw, string path)
            {
                if (!TryLump(raw, LumpPlanes, out int off, out int len)) return false;
                int n = len / PlaneStride;
                if (n <= 0) { Debug.LogError($"[Dust2Solid] {path} 平面段为空"); return false; }
                _px = new float[n]; _py = new float[n]; _pz = new float[n]; _pd = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int o = off + i * PlaneStride;
                    _px[i] = BitConverter.ToSingle(raw, o);
                    _py[i] = BitConverter.ToSingle(raw, o + 4);
                    _pz[i] = BitConverter.ToSingle(raw, o + 8);
                    _pd[i] = BitConverter.ToSingle(raw, o + 12);
                }
                return true;
            }

            private bool ReadNodes(byte[] raw, string path)
            {
                if (!TryLump(raw, LumpNodes, out int off, out int len)) return false;
                int n = len / NodeStride;
                if (n <= 0) { Debug.LogError($"[Dust2Solid] {path} 节点段为空"); return false; }
                _plane = new int[n]; _child0 = new int[n]; _child1 = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int o = off + i * NodeStride;
                    _plane[i] = BitConverter.ToInt32(raw, o);
                    _child0[i] = BitConverter.ToInt16(raw, o + 4);
                    _child1[i] = BitConverter.ToInt16(raw, o + 6);
                    if (_plane[i] < 0 || _plane[i] >= _px.Length)
                    {
                        Debug.LogError($"[Dust2Solid] 节点 {i} 的平面下标 {_plane[i]} 越界（平面 {_px.Length} 条）⇒ 不补实体");
                        return false;
                    }
                }
                return true;
            }

            private static bool TryLump(byte[] raw, int index, out int offset, out int length)
            {
                offset = BitConverter.ToInt32(raw, 4 + 8 * index);
                length = BitConverter.ToInt32(raw, 4 + 8 * index + 4);
                if (offset < 0 || length < 0 || (long)offset + length > raw.Length)
                {
                    Debug.LogError($"[Dust2Solid] BSP lump {index} 越界（offset={offset} len={length} 文件={raw.Length}）");
                    return false;
                }
                return true;
            }

            /// <summary>这一点在 solid leaf 里吗（世界坐标 = Unity 米）。</summary>
            internal bool IsSolid(float ux, float uy, float uz)
            {
                ToHalfLife(ux, uy, uz, out float hx, out float hy, out float hz);
                int node = 0;
                for (int depth = 0; depth < 64; depth++)
                {
                    int pn = _plane[node];
                    float d = _px[pn] * hx + _py[pn] * hy + _pz[pn] * hz - _pd[pn];
                    int child = d >= 0f ? _child0[node] : _child1[node];
                    if (child < 0) return -(child + 1) == 0;
                    if (child >= _plane.Length)
                    {
                        if (_depthErrors++ == 0)
                            Debug.LogError($"[Dust2Solid] 节点子下标 {child} 越界（节点 {_plane.Length} 个）⇒ 该点按非实心处理");
                        return false;
                    }
                    node = child;
                }
                if (_depthErrors++ == 0)
                    Debug.LogError("[Dust2Solid] 节点树深度 > 64（疑似树损坏）⇒ 该点按非实心处理");
                return false;
            }
        }
    }
}
