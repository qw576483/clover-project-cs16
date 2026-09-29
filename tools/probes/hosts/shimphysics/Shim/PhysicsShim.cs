// =====================================================================================
//  Shim/PhysicsShim.cs —— `UnityEngine.Physics` 的替身：把真 CsMap.cs 的几何查询
//  接到 `ShimWorld`（解析几何）上。
//
//  形态与 Unity 的签名逐字对齐（重载、参数顺序、`out RaycastHit`），所以真源码**一行不改**
//  就能编过并跑起来。`queriesHitBackfaces` 作为公开字段存在：CsMap 的注释引用了它的语义
//  （默认 false ⇒ 从实体内部朝外的射线不给交点），ShimWorld 按同一口径实现。
//
//  只实现真源码用到的三个入口（清单来自 CsMap.cs 的调用点，⛔ 不多塞）：
//    · Raycast(origin, dir, out hit, maxDistance, layerMask, queryTriggerInteraction)
//    · Raycast(origin, dir, maxDistance, layerMask, queryTriggerInteraction)
//    · CheckCapsule(point0, point1, radius, layerMask, queryTriggerInteraction)
// =====================================================================================

namespace UnityEngine
{
    public static class Physics
    {
        /// <summary>与 Unity 默认一致（false）。ShimWorld.Raycast 按同一口径跳过"起点在盒内"的盒。</summary>
        public static bool queriesHitBackfaces = false;

        public static ShimPhysics.ShimWorld World => ShimPhysics.ShimWorld.Current;

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit,
                                   float maxDistance, int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            var w = World;
            if (w == null) { hit = default; return false; }
            return w.Raycast(origin, direction, out hit, maxDistance);
        }

        public static bool Raycast(Vector3 origin, Vector3 direction, out RaycastHit hit, float maxDistance)
            => Raycast(origin, direction, out hit, maxDistance, ~0, QueryTriggerInteraction.UseGlobal);

        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
                                   int layerMask, QueryTriggerInteraction queryTriggerInteraction)
            => Raycast(origin, direction, out _, maxDistance, layerMask, queryTriggerInteraction);

        public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance)
            => Raycast(origin, direction, out _, maxDistance, ~0, QueryTriggerInteraction.UseGlobal);

        public static bool CheckCapsule(Vector3 start, Vector3 end, float radius,
                                        int layerMask, QueryTriggerInteraction queryTriggerInteraction)
        {
            var w = World;
            return w != null && w.CheckCapsule(start, end, radius);
        }

        public static bool CheckCapsule(Vector3 start, Vector3 end, float radius)
            => CheckCapsule(start, end, radius, ~0, QueryTriggerInteraction.UseGlobal);
    }
}
