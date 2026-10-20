using System.Collections.Generic;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// <c>de_dust2_geo.bin</c> 的**朝上面**查询：按格心把朝上三角面（面法线 y 分量占比 &gt; 0.1）光栅化成
    /// "每格的可站面清单"（每个面记录**格心高度**与**格内最低高度**），并提供"取离某个参考高度最近的朝上面"。
    ///
    /// <para><b>为什么需要</b>：可走位图是**单层 2D**（一格一位），它只回答"这一格在不在阻挡位上"，
    /// 不回答"这一格脚踩在哪一层"。本表一处生成、两处使用：
    /// ① <see cref="Dust2Solid"/> 按它算"可站面之下的实心区间"；
    /// ② <see cref="Dust2Builder"/> 与 <see cref="Dust2WalkSeal"/> 按它把标记点的 Y 落到该格真实可站面。</para>
    ///
    /// <para><b>为什么取"最近的"而不是"最高的"</b>：一格柱里可能有多个朝上面（下层地板 + 上层楼板／雨棚），
    /// 取最高会把标记点抬到屋顶上去。标记点自带的 y 来自原版 BSP 实体原点 —— 它可能与重建地表相差若干米，
    /// 但它**指的就是这一层**，所以"离它最近的那个面"才是这个点的落脚面。</para>
    ///
    /// <para><b>格坐标口径</b>：<c>ix = floor((x - OriginX) / CellSize)</c>，格心 = <see cref="Dust2GeoData.CellCenter"/>，
    /// 与位图 / 烘焙器 / 运行时 <c>WalkableAt</c> 同一口径。</para>
    /// </summary>
    internal static class Dust2Surfaces
    {
        /// <summary>朝上三角面的判定：面法线 y 分量下限。</summary>
        private const float UpFaceNormalY = 0.1f;

        /// <summary>同格内两片高度相差不到它的面算同一个面（米）。</summary>
        private const float FaceDedup = 0.02f;

        /// <summary>一个朝上面落在某一格里的两个高度。</summary>
        internal struct Face
        {
            /// <summary>格心处的面高度：标记点吸附 / BSP 落点判定用这个（"离参考高度最近的面"）。</summary>
            public float CenterY;
            /// <summary>面被裁进本格之后的最低高度：实心盒顶用这个（盒顶处处不高于可见面）。</summary>
            public float MinY;
        }

        /// <summary>
        /// 朝上面光栅化：格索引（<c>idx = iz * Width + ix</c>）→ 该格的朝上面清单（未排序，
        /// 但同格内按格心高度已按 <see cref="FaceDedup"/> 去重）。
        /// </summary>
        internal static Dictionary<int, List<Face>> BuildTable(Dust2GeoData geo)
        {
            var result = new Dictionary<int, List<Face>>();
            if (geo == null || geo.Groups == null || geo.CellSize <= 0f) return result;

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
                            float x0 = ox + ix * cell, z0 = oz + iz * cell;
                            float x1 = x0 + cell, z1 = z0 + cell;
                            if (!TryFaceHeightAtCellCenter(a, b, c, x0 + cell * 0.5f, z0 + cell * 0.5f,
                                                           out float y)) continue;
                            int key = iz * geo.Width + ix;
                            if (!result.TryGetValue(key, out var list))
                            {
                                list = new List<Face>();
                                result[key] = list;
                            }
                            // 同一张面通常被切成多个三角面：它们在格心的高度相同 ⇒ 认为是同一面，
                            // 但**格内最低高度要取各三角面里更低的那个**（否则漏掉压在另一侧三角面里的下缘）。
                            float faceMin = MinHeightInCell(a, b, c, x0, z0, x1, z1);
                            bool dup = false;
                            for (int k = 0; k < list.Count; k++)
                            {
                                if (Mathf.Abs(list[k].CenterY - y) >= FaceDedup) continue;
                                var merged = list[k];
                                if (faceMin < merged.MinY) { merged.MinY = faceMin; list[k] = merged; }
                                dup = true;
                                break;
                            }
                            if (!dup) list.Add(new Face { CenterY = y, MinY = faceMin });
                        }
                    }
                }
            }

            Debug.Log($"[Dust2Surfaces] 朝上面光栅化：{result.Count} 格拿到可站面");
            return result;
        }

        /// <summary>
        /// 取 <paramref name="x"/>/<paramref name="z"/> 所在格、离 <paramref name="refY"/> 最近的那个朝上面高度。
        /// 返回 false = 这一格没有朝上面（或几何不可用）⇒ 调用方必须留痕并保留原值（<paramref name="y"/> 原样返回）。
        /// </summary>
        internal static bool TryNearestSurface(Dust2GeoData geo, Dictionary<int, List<Face>> table,
                                               float x, float z, float refY, out float y)
        {
            y = refY;
            if (geo == null || table == null || geo.CellSize <= 0f) return false;

            int ix = Mathf.FloorToInt((x - geo.OriginX) / geo.CellSize);
            int iz = Mathf.FloorToInt((z - geo.OriginZ) / geo.CellSize);
            if (ix < 0 || iz < 0 || ix >= geo.Width || iz >= geo.Depth) return false;
            if (!table.TryGetValue(iz * geo.Width + ix, out var list) || list == null || list.Count == 0) return false;

            y = list[0].CenterY;
            for (int i = 1; i < list.Count; i++)
            {
                if (Mathf.Abs(list[i].CenterY - refY) < Mathf.Abs(y - refY)) y = list[i].CenterY;
            }
            return true;
        }

        /// <summary>格心 <c>(px, pz)</c> 是否落在三角面内（含边）；是则给出该处的高度。</summary>
        private static bool TryFaceHeightAtCellCenter(Vector3 a, Vector3 b, Vector3 c, float px, float pz, out float y)
        {
            y = 0f;
            if (!InTriangle(a, b, c, px, pz)) return false;
            float h = FaceHeight(a, b, c, px, pz);
            if (float.IsNaN(h)) return false;
            y = h;
            return true;
        }

        /// <summary>
        /// 三角面被裁进格 <c>[x0,x1]×[z0,z1]</c> 之后的最低高度。
        /// 面是平面 ⇒ 高度在格内是 <c>(x,z)</c> 的线性函数 ⇒ 最小值落在裁剪多边形（凸）的某个顶点上：
        /// 三角形顶点（在格内的）+ 三角边与格边界的交点 + 落在三角形内的格角。
        /// </summary>
        private static float MinHeightInCell(Vector3 a, Vector3 b, Vector3 c, float x0, float z0, float x1, float z1)
        {
            var pts = new List<Vector2>(12);

            if (InCell(a.x, a.z, x0, z0, x1, z1)) pts.Add(new Vector2(a.x, a.z));
            if (InCell(b.x, b.z, x0, z0, x1, z1)) pts.Add(new Vector2(b.x, b.z));
            if (InCell(c.x, c.z, x0, z0, x1, z1)) pts.Add(new Vector2(c.x, c.z));

            AddEdgeHits(a, b, x0, z0, x1, z1, pts);
            AddEdgeHits(b, c, x0, z0, x1, z1, pts);
            AddEdgeHits(c, a, x0, z0, x1, z1, pts);

            if (InTriangle(a, b, c, x0, z0)) pts.Add(new Vector2(x0, z0));
            if (InTriangle(a, b, c, x1, z0)) pts.Add(new Vector2(x1, z0));
            if (InTriangle(a, b, c, x0, z1)) pts.Add(new Vector2(x0, z1));
            if (InTriangle(a, b, c, x1, z1)) pts.Add(new Vector2(x1, z1));

            float min = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                float h = FaceHeight(a, b, c, pts[i].x, pts[i].y);
                if (!float.IsNaN(h) && h < min) min = h;
            }
            if (min < float.MaxValue) return min;

            float fallback = FaceHeight(a, b, c, (x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
            return float.IsNaN(fallback) ? a.y : fallback;
        }

        private static bool InCell(float x, float z, float x0, float z0, float x1, float z1)
            => x >= x0 - 1e-4f && x <= x1 + 1e-4f && z >= z0 - 1e-4f && z <= z1 + 1e-4f;

        /// <summary>三角边 <paramref name="p"/>→<paramref name="q"/> 与格四条边的交点（在线段内才收）。</summary>
        private static void AddEdgeHits(Vector3 p, Vector3 q, float x0, float z0, float x1, float z1, List<Vector2> pts)
        {
            AddEdgeHit(p, q, 0, x0, x0, x1, z0, z1, pts);
            AddEdgeHit(p, q, 0, x1, x0, x1, z0, z1, pts);
            AddEdgeHit(p, q, 2, z0, x0, x1, z0, z1, pts);
            AddEdgeHit(p, q, 2, z1, x0, x1, z0, z1, pts);
        }

        /// <param name="axis">0 = 与竖直线 <c>x = line</c> 求交；2 = 与水平线 <c>z = line</c> 求交。</param>
        private static void AddEdgeHit(Vector3 p, Vector3 q, int axis, float line,
                                       float ax0, float ax1, float bx0, float bx1, List<Vector2> pts)
        {
            float dp = axis == 0 ? p.x : p.z;
            float dq = axis == 0 ? q.x : q.z;
            float d = dq - dp;
            if (Mathf.Abs(d) < 1e-9f) return;
            float t = (line - dp) / d;
            if (t < -1e-6f || t > 1f + 1e-6f) return;
            float x = p.x + (q.x - p.x) * t;
            float z = p.z + (q.z - p.z) * t;
            if (x < ax0 - 1e-4f || x > ax1 + 1e-4f || z < bx0 - 1e-4f || z > bx1 + 1e-4f) return;
            pts.Add(new Vector2(x, z));
        }

        /// <summary>格内点是否落在三角面内（含边）。</summary>
        private static bool InTriangle(Vector3 a, Vector3 b, Vector3 c, float px, float pz)
        {
            float d0 = (b.x - a.x) * (pz - a.z) - (b.z - a.z) * (px - a.x);
            float d1 = (c.x - b.x) * (pz - b.z) - (c.z - b.z) * (px - b.x);
            float d2 = (a.x - c.x) * (pz - c.z) - (a.z - c.z) * (px - c.x);
            bool neg = d0 < 0f || d1 < 0f || d2 < 0f;
            bool pos = d0 > 0f || d1 > 0f || d2 > 0f;
            return !(neg && pos);
        }

        /// <summary>平面在 <c>(px, pz)</c> 处的高度（不做落点判定；XZ 不可解时返回 NaN）。</summary>
        private static float FaceHeight(Vector3 a, Vector3 b, Vector3 c, float px, float pz)
        {
            float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
            if (Mathf.Abs(den) < 1e-12f) return float.NaN;
            float w0 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / den;
            float w1 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / den;
            return w0 * a.y + w1 * b.y + (1f - w0 - w1) * c.y;
        }
    }
}
