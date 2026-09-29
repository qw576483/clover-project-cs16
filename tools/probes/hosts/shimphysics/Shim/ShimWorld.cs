// =====================================================================================
//  Shim/ShimWorld.cs —— 解析几何世界（AABB + 地面平面 + 单层 2D 位图）。
//
//  与 `tools/probes/phys-stuck-probe.py` 同一个世界模型、同一组常量，因此两条链的
//  输入逐值可比：探针给「机制级」结论（它自己复刻判据），本宿主给「代码级」结论
//  （判据由真 `CsMap.cs` 执行）。
//
//  位图口径与引擎烘焙一致：格子足迹被任一**实心**盒覆盖 ⇒ 该格不可走（单层 2D、
//  一格一位、没有高度）。trigger 盒只服务烘焙、不参与任何查询（对应
//  `QueryTriggerInteraction.Ignore`），与 `Level/Blockers/Blocker_*` 的语义相同。
// =====================================================================================

using System;
using System.Collections.Generic;

namespace ShimPhysics
{
    /// <summary>轴对齐盒（世界坐标，米）。</summary>
    public struct Box
    {
        public float X0, X1, Y0, Y1, Z0, Z1;
        public bool IsTrigger;
        public string Name;

        public Box(float x0, float x1, float y0, float y1, float z0, float z1, string name = "", bool isTrigger = false)
        {
            X0 = x0; X1 = x1; Y0 = y0; Y1 = y1; Z0 = z0; Z1 = z1; Name = name; IsTrigger = isTrigger;
        }
    }

    public sealed class ShimWorld
    {
        /// <summary>当前世界（`UnityEngine.Physics` 替身查的就是它）。</summary>
        public static ShimWorld Current;

        public float GroundY;
        public float Cell = 1f;
        public readonly List<Box> Boxes = new List<Box>();

        private readonly List<Box> _solid = new List<Box>();

        /// <summary>把盒列表装进世界（trigger 盒不进 `_solid`，不参与查询）。</summary>
        public void Load(IEnumerable<Box> boxes, float groundY, float cell)
        {
            GroundY = groundY;
            Cell = cell;
            Boxes.Clear();
            _solid.Clear();
            foreach (var b in boxes)
            {
                Boxes.Add(b);
                if (!b.IsTrigger) _solid.Add(b);
            }
            Current = this;
        }

        public int SolidCount => _solid.Count;

        // ------------------------------------------------------------------ 位图
        /// <summary>格子足迹（1 m）被任一实心盒覆盖 ⇒ 不可走（单层 2D 口径）。</summary>
        public bool WalkableAt(float x, float z)
        {
            var ci = (int)Math.Floor(x / Cell);
            var cj = (int)Math.Floor(z / Cell);
            float x0 = ci * Cell, x1 = x0 + Cell, z0 = cj * Cell, z1 = z0 + Cell;
            for (var i = 0; i < _solid.Count; i++)
            {
                var b = _solid[i];
                if (b.X0 < x1 && b.X1 > x0 && b.Z0 < z1 && b.Z1 > z0) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ 射线
        /// <summary>
        /// 向下 / 任意方向的射线求交（无限地面平面 + 实心 AABB）。
        /// `queriesHitBackfaces = false` 的语义：起点落在某盒**内部**时该盒不给交点
        /// （CsMap 的 `BodyHeightClearAt` 注释描述的正是这个形态）。
        /// </summary>
        public bool Raycast(UnityEngine.Vector3 origin, UnityEngine.Vector3 dir,
                            out UnityEngine.RaycastHit hit, float maxDistance)
        {
            hit = default;
            var d = dir.normalized;
            if (d.sqrMagnitude < 0.5f) return false;

            var bestT = float.PositiveInfinity;
            var bestNormal = UnityEngine.Vector3.up;

            // 无限地面平面 y = GroundY（朝上法线）
            if (Math.Abs(d.y) > 1e-9f)
            {
                var t = (GroundY - origin.y) / d.y;
                if (t > 1e-6f && t <= maxDistance && t < bestT)
                {
                    bestT = t;
                    bestNormal = d.y < 0f ? UnityEngine.Vector3.up : UnityEngine.Vector3.down;
                }
            }

            for (var i = 0; i < _solid.Count; i++)
            {
                var b = _solid[i];
                if (PointInside(origin, b)) continue;       // queriesHitBackfaces = false
                if (!Slab(origin, d, b, maxDistance, out var t, out var n)) continue;
                if (t < bestT) { bestT = t; bestNormal = n; }
            }

            if (float.IsPositiveInfinity(bestT)) return false;
            hit.point = new UnityEngine.Vector3(origin.x + d.x * bestT, origin.y + d.y * bestT, origin.z + d.z * bestT);
            hit.normal = bestNormal;
            hit.distance = bestT;
            return true;
        }

        private static bool PointInside(UnityEngine.Vector3 p, Box b)
            => p.x > b.X0 && p.x < b.X1 && p.y > b.Y0 && p.y < b.Y1 && p.z > b.Z0 && p.z < b.Z1;

        private static bool Slab(UnityEngine.Vector3 o, UnityEngine.Vector3 d, Box b,
                                 float maxDistance, out float tHit, out UnityEngine.Vector3 normal)
        {
            tHit = 0f;
            normal = UnityEngine.Vector3.up;
            var tMin = 0f;
            var tMax = maxDistance;
            var axis = -1;
            var sign = 0f;

            if (!Clip(o.x, d.x, b.X0, b.X1, ref tMin, ref tMax, ref axis, ref sign, 0)) return false;
            if (!Clip(o.y, d.y, b.Y0, b.Y1, ref tMin, ref tMax, ref axis, ref sign, 1)) return false;
            if (!Clip(o.z, d.z, b.Z0, b.Z1, ref tMin, ref tMax, ref axis, ref sign, 2)) return false;
            if (tMax < 0f || tMin > maxDistance) return false;

            tHit = tMin > 0f ? tMin : 0f;
            normal = axis == 0 ? new UnityEngine.Vector3(-sign, 0f, 0f)
                   : axis == 1 ? new UnityEngine.Vector3(0f, -sign, 0f)
                               : new UnityEngine.Vector3(0f, 0f, -sign);
            return true;
        }

        private static bool Clip(float o, float d, float lo, float hi,
                                 ref float tMin, ref float tMax, ref int axis, ref float sign, int a)
        {
            if (Math.Abs(d) < 1e-9f)
                return o >= lo && o <= hi;
            var t1 = (lo - o) / d;
            var t2 = (hi - o) / d;
            var s = -1f;
            if (t1 > t2) { var tmp = t1; t1 = t2; t2 = tmp; s = 1f; }
            else { s = -1f; }
            if (t1 > tMin) { tMin = t1; axis = a; sign = s; }
            if (t2 < tMax) tMax = t2;
            return tMax >= tMin;
        }

        // ------------------------------------------------------------------ 胶囊
        /// <summary>
        /// 竖直胶囊（端点 a/b）与实心 AABB 的重叠：CsMap 的两处 `Physics.CheckCapsule`
        /// 都是**竖直**胶囊（`(x, lo, z)` → `(x, hi, z)`），按"线段到盒的距离 ≤ 半径"判。
        /// </summary>
        public bool CheckCapsule(UnityEngine.Vector3 a, UnityEngine.Vector3 b, float radius)
        {
            var loY = Math.Min(a.y, b.y);
            var hiY = Math.Max(a.y, b.y);
            for (var i = 0; i < _solid.Count; i++)
            {
                var box = _solid[i];
                var px = Math.Min(Math.Max(a.x, box.X0), box.X1);
                var pz = Math.Min(Math.Max(a.z, box.Z0), box.Z1);
                var dx = a.x - px;
                var dz = a.z - pz;
                var dxz2 = dx * dx + dz * dz;
                if (dxz2 > radius * radius) continue;                    // 水平距离已超半径
                var dy = 0f;
                if (box.Y1 < loY) dy = loY - box.Y1;
                else if (box.Y0 > hiY) dy = box.Y0 - hiY;
                if (dxz2 + dy * dy <= radius * radius + 1e-9f) return true;
            }
            return false;
        }
    }
}
