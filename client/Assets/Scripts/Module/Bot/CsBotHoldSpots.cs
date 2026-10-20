using System.Collections.Generic;
using CloverEngine;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 让守卫能在包点内**多点分布 + 定时换位**，而不是全队挤在同一个标记点上站桩。
    ///
    /// <para><b>数据源（不硬编码任何世界坐标）</b>：包点自己的地图标记点
    /// （<c>CsMarkers.BombsiteA / BombsiteB</c>，运行时读 <c>Resources/MapData/de_dust2_markers.bytes</c>）。
    /// 这批点同时就是 <c>CsBomb.IsInBombsite</c>（<c>Module/Match/CsBomb.cs:478</c>）判定"在不在包点里"用的那一批 ——
    /// 所以**站在守位上 ⇒ 一定在包点判定区内**，两处口径同源、不会漂移。</para>
    ///
    /// <para><b>为什么不用 <c>Route_Patrol</c> 当守位</b>：巡逻点是全图散点，落在包点半径内的只有零星几个，
    /// 且不保证在包点判定区内 ⇒ 从那里随机取点会取到包点外 ⇒
    /// 观感上"守卫在包点附近来回走"（用户报的"原地踱步"）。</para>
    /// </summary>
    public static class CsBotHoldSpots
    {
        private const string Tag = BotModule.Tag;

        /// <summary>
        /// 同一个守位点上最多站几个人。超过这个数就把后来者挤去同包点的次优守位
        /// （规格要求：同一守位点最多 2 人，第 3 人挑同 site 的次优守位）。
        /// </summary>
        public const int MaxPerSpot = 2;

        /// <summary>判定"某队友站在该守位上"的水平半径（米）—— 与守位最小间距同一口径。</summary>
        private const float OccupiedRadius = CsBotConst.HoldSpotMinSeparation;

        /// <summary>
        /// 构造某包点的守位表：取该包点的标记点 → 过滤"站得下"（<c>ICsMap.CanStand</c>，与移动解算同一判据）
        /// → 确定性排序（x 后 z，保证同一张地图每台机器得到同一张表）→ 折叠间距过近的点。
        /// </summary>
        /// <returns>可用守位个数（&lt;2 时调用方应退回旧的单点行为）。</returns>
        public static int Build(ICsMap map, string siteMarker, List<Vector3> outSpots)
        {
            outSpots.Clear();
            if (map == null || !map.IsLoaded)
            {
                Game.Logger?.Warn(Tag,
                    $"守位表构造失败：包点标记 '{siteMarker}' 需要地图，但地图未就绪 → 退回单点守点");
                return 0;
            }

            var pts = map.Points(siteMarker);
            if (pts == null || pts.Length == 0)
            {
                Game.Logger?.Warn(Tag,
                    $"守位表构造失败：地图上没有包点标记 '{siteMarker}'（应为 CsMarkers 常量）→ 退回单点守点");
                return 0;
            }

            var cand = new List<Vector3>(pts.Length);
            for (var i = 0; i < pts.Length; i++)
            {
                // 站得下（8 向半径采样 + 地面台阶 + 身体高度带）= 与移动解算同一判据；
                // 只看 WalkableAt（单格）会把"紧贴墙的格子"当成守位 ⇒ 走不过去 ⇒ 顶着墙被反复判卡住。
                if (map.CanStand(pts[i])) cand.Add(pts[i]);
            }

            cand.Sort(CompareXZ);

            // 只保留**首个包点标记**附近的点：标记表第一个点就是该包点的标准下包点（A#0/B#0），
            // 其余标记可能横跨十几米。按"离队友最远"挑会把守卫挑到最外侧标记、离实际下包位置十几米。
            // 收进标准下包点 6m 内 = 在下包点附近守。
            if (cand.Count > 1)
            {
                var center = pts[0];
                center.y = 0f;

                var near = new List<Vector3>(cand.Count);
                for (var i = 0; i < cand.Count; i++)
                {
                    var d = cand[i] - center;
                    d.y = 0f;
                    if (d.magnitude <= CsBotConst.HoldSpotCenterRadius) near.Add(cand[i]);
                }

                // 重心附近一个都没有（标记点围成一圈、重心落在几何外）⇒ 退回全部候选，不让守位表空掉。
                if (near.Count > 0) cand = near;

                cand.Sort((a, b) =>
                {
                    var da = a - center; da.y = 0f;
                    var db = b - center; db.y = 0f;
                    var c = da.sqrMagnitude.CompareTo(db.sqrMagnitude);
                    return c != 0 ? c : CompareXZ(a, b);
                });
            }

            for (var i = 0; i < cand.Count; i++)
            {
                var tooClose = false;
                for (var k = 0; k < outSpots.Count; k++)
                {
                    var d = cand[i] - outSpots[k];
                    d.y = 0f;
                    if (d.magnitude < CsBotConst.HoldSpotMinSeparation) { tooClose = true; break; }
                }

                if (!tooClose) outSpots.Add(cand[i]);
            }

            // 兜底：一个可站守位都没有（标记点被几何体埋住）→ 用原始标记点自身，至少别让守卫失去包点位置
            if (outSpots.Count == 0) outSpots.Add(cand.Count > 0 ? cand[0] : pts[0]);

            if (outSpots.Count < 2)
            {
                Game.Logger?.Warn(Tag,
                    $"包点 '{siteMarker}' 只构造出 {outSpots.Count} 个可用守位（标记点 {pts.Length} 个，" +
                    $"可站 {cand.Count} 个）→ 该包点无法多点分布，退回单点守点。" +
                    $"请检查包点标记是否落在几何体里（Dust2Builder.SnapMarkerToWalkable）");
            }

            return outSpots.Count;
        }

        /// <summary>
        /// 挑下一个守位：**离队友最远**的那个（并列时取离自己最近的）。
        ///
        /// <para>为什么用"离队友最远"而不是随机 / 按 id 取模：分散式决策 —— 每台机器人只看自己与队友的位置，
        /// 不需要任何共享的"占位登记"，也不会因为 id 排布变化而两台机器人选中同一格。
        /// 排除当前守位 ⇒ 每次选择都**真的换位**（不原地"换"）。</para>
        /// </summary>
        /// <returns>守位下标（守位表为空时 <c>-1</c>）。</returns>
        public static int PickSlot(List<Vector3> spots, int current, CsActor self, IReadOnlyList<CsActor> actors)
        {
            if (spots == null || spots.Count == 0) return -1;
            if (self == null) return 0;
            if (spots.Count == 1) return 0;

            var best = -1;
            var bestScore = float.MinValue;
            var cappedBest = -1;
            var cappedScore = float.MinValue;

            for (var i = 0; i < spots.Count; i++)
            {
                if (i == current) continue;

                var nearestMate = float.MaxValue;
                var occupants = 0;
                for (var a = 0; a < actors.Count; a++)
                {
                    var o = actors[a];
                    if (o == null || !o.IsAlive || o.Id == self.Id || o.Team != self.Team) continue;

                    var d = spots[i] - o.Position;
                    d.y = 0f;
                    var dist = d.magnitude;
                    if (dist < nearestMate) nearestMate = dist;
                    if (dist < OccupiedRadius) occupants++;
                }

                if (nearestMate == float.MaxValue) nearestMate = 1e6f;   // 附近没队友 → 都行

                var dz = spots[i] - self.Position;
                dz.y = 0f;

                // 主判据 = 离队友远（×1000 让它压倒次判据）；次判据 = 离自己近（换位别绕半张图）
                var score = nearestMate * 1000f - dz.magnitude;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }

                // 人数上限是硬闸：满载（≥ MaxPerSpot）的守位只在"全部满载"时才作为兜底候选。
                if (occupants >= MaxPerSpot) continue;
                if (score <= cappedScore) continue;

                cappedScore = score;
                cappedBest = i;
            }

            if (cappedBest >= 0) return cappedBest;

            if (LogThrottle.ShouldLog("bot.holdspot.full." + self.Id, CsBotConst.StuckWarnCooldown))
            {
                Game.Logger?.Warn(Tag,
                    $"{self.Name} 的 {spots.Count} 个守位**全部**已站满 {MaxPerSpot} 人" +
                    $"（判定半径 {OccupiedRadius:F1}m）→ 按「离队友最远」兜底选位，该守位点会超过 {MaxPerSpot} 人");
            }

            return best >= 0 ? best : 0;
        }

        private static int CompareXZ(Vector3 a, Vector3 b)
        {
            var c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.z.CompareTo(b.z);
        }
    }
}
