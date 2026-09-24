using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Map
{
    /// <summary>
    /// 地图门面：**唯一**可以读地图数据的模块（其它模块只用它导出的只读 API）。
    ///
    /// <para>数据来源：<c>Assets/Resources/MapData/de_dust2.bytes</c>（由 <c>Clover/地图烘焙</c> 生成），
    /// 通过引擎 <c>Game.Map.LoadFromResource</c> 装入 <c>Game.Map</c>（CloverMap 二进制 v1）。</para>
    ///
    /// <para>本模块只做两件事：① 触发加载；② 提供服务端同源的"空间事实"查询 + 业务侧本地碰撞解算
    /// （半径采样 / 分轴滑墙 / 扫掠细分 —— 引擎只给"这一格能不能走"，手感归业务）。</para>
    /// </summary>
    public interface ICsMap
    {
        /// <summary>地图是否加载完成（<c>Game.Map.Loaded</c>）。</summary>
        bool IsLoaded { get; }
        string MapName { get; }
        string Status { get; }

        /// <summary>异步加载地图数据；失败走 onFailed（必须打日志）。</summary>
        void LoadAsync(string mapResourcePath, System.Action onLoaded, System.Action<string> onFailed = null);
        void Unload();

        /// <summary>该点是否可站立（引擎空间事实；未加载时返回 true —— 与引擎语义一致）。</summary>
        bool WalkableAt(float x, float z);

        /// <summary>以给定半径判断是否站得下（中心 + 8 向采样）。</summary>
        bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius);

        /// <summary>
        /// 本地碰撞解算：从 <paramref name="from"/> 走向 <paramref name="to"/>（一帧位移，已含跳/落 Y），
        /// 返回可用终点。内部做分轴滑墙 + 扫掠细分 + 台阶。
        /// </summary>
        Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius);

        /// <summary>地面高度（向下探测）；找不到地面返回 float.NegativeInfinity。</summary>
        float SampleGround(Vector3 pos, float maxDrop = 8f);

        /// <summary>
        /// 向下探测地面**（含法线）**：<c>true</c> = 命中世界几何。
        ///
        /// <para>与 <see cref="SampleGround"/> 的区别只有"多给一个法线" —— 但那是**能不能判陡坡**的唯一依据：
        /// 法线在**上轴**（本工程 = <c>y</c>，原版 GoldSrc = <c>z</c>）上的分量 &lt;
        /// <see cref="CsConst.MaxStandableSlopeNormalZ"/>（0.7 ⇒ 45.573°）的面**不是地面**
        /// （原版 <c>PM_CatagorizePosition</c> 的 <c>// too steep</c> 分支），
        /// 详口径见 <see cref="CsConst.MaxStandableSlopeNormalZ"/>。</para>
        ///
        /// <para>判断陡坡必须用**世界法线**，不要用 <c>Vector3.Angle</c> 反算角度再去比角度（多一次三角函数、
        /// 还引入一个角度阈值）—— 原版就是直接比这个分量。</para>
        /// </summary>
        /// <param name="pos">探测起点（通常 = 角色位置）。</param>
        /// <param name="point">命中点（世界坐标）。</param>
        /// <param name="normal">命中面的世界法线（已归一化）。</param>
        /// <param name="maxDrop">向下最大探测距离（米）。</param>
        bool TrySampleGround(Vector3 pos, out Vector3 point, out Vector3 normal, float maxDrop = 8f);

        /// <summary>出生点（索引循环取用；由烘焙时名为 Spawn* 的对象导出）。</summary>
        Vector3 GetSpawnPoint(int index);
        int SpawnPointCount { get; }

        /// <summary>
        /// 取场景中被标记为 <paramref name="marker"/> 的点位（来自场景中名为该标记的 GameObject）。
        /// 例：<c>Points(CsMarkers.TAttackA)</c> → T 进攻 A 点的集合点。
        /// 未标记时返回空数组（调用方必须判空并打日志，不许静默）。
        /// </summary>
        Vector3[] Points(string marker);

        /// <summary>按标记取一个点（随机取一个；没有则返回 false）。</summary>
        bool TryGetPoint(string marker, out Vector3 point);
    }

    /// <summary>地图标记点名（与 Editor 地图生成器写死的命名一致，改这里必须同步生成器）。</summary>
    public static class CsMarkers
    {
        public const string SpawnT = "Spawn_T";
        public const string SpawnCT = "Spawn_CT";

        /// <summary>炸弹点标记（下包/拆包判定区，半径由 <see cref="BombsiteRadius"/> 决定）。</summary>
        public const string BombsiteA = "Bombsite_A";
        public const string BombsiteB = "Bombsite_B";
        public const float BombsiteRadius = 7f;

        /// <summary>买枪区（完整覆盖出生点附近）。</summary>
        public const string BuyZoneT = "BuyZone_T";
        public const string BuyZoneCT = "BuyZone_CT";

        // ---- 机器人路线锚点（AI 巡逻/进攻/防守用）----
        public const string TAttackA = "Route_T_To_A";
        public const string TAttackB = "Route_T_To_B";
        public const string TMid = "Route_T_Mid";
        public const string CTDefendA = "Route_CT_To_A";
        public const string CTDefendB = "Route_CT_To_B";
        public const string CTMid = "Route_CT_Mid";
        /// <summary>通用巡逻点（散点，AI 随机巡逻用）。</summary>
        public const string Patrol = "Route_Patrol";
    }
}
