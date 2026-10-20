using System.Collections.Generic;
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

        /// <summary>
        /// 地形高度场是否有**可站面**数据（<c>.bytes</c> 的 <c>FlagHeightField</c> 段）。
        /// <para>没有该段（旧产物 / 未开烘焙选项）时，下面两个查询恒返回"无面"，
        /// 贴地取样退回物理射线。</para>
        /// </summary>
        bool HasSurfaceHeights { get; }

        /// <summary>
        /// 格 <c>(x,z)</c> 的可站面数量（**格索引**，不是世界坐标；世界 → 格 =
        /// <c>floor((world - Game.Map.Origin) / CellSize)</c>）。
        /// <para>同一格台上/台下两层各算一条 —— 这是"该站哪一层"的唯一数据来源。
        /// 未加载 / 无高度场 / 越界 / 无面 = 0。</para>
        /// </summary>
        int SurfaceCountAt(int x, int z);

        /// <summary>
        /// 格 <c>(x,z)</c> 的**全部**可站面高度（米，升序）追加进 <paramref name="heightsOut"/>。
        /// <para>参数口径与 <see cref="SurfaceCountAt"/> 相同（格索引）。无面时返回 false 且
        /// **清空** <paramref name="heightsOut"/>（不留上一次的残留）。</para>
        /// </summary>
        bool TryGetSurfaceHeights(int x, int z, List<float> heightsOut);

        /// <summary>以给定半径判断是否站得下（中心 + 8 向采样）。
        /// <paramref name="height"/> = 碰撞盒高度（站立 <see cref="CsConst.StandHeight"/> / 蹲姿
        /// <see cref="CsConst.CrouchHeight"/>），身高带自脚面起算 —— 盒越矮越能站进矮空间。
        /// 默认 = 站立高 ⇒ 既有调用点语义不变。</summary>
        bool CanStand(Vector3 pos, float radius = CsConst.PlayerRadius, float height = CsConst.StandHeight);

        /// <summary>
        /// 本地碰撞解算：从 <paramref name="from"/> 走向 <paramref name="to"/>（一帧位移，已含跳/落 Y），
        /// 返回可用终点。内部做分轴滑墙 + 扫掠细分 + 台阶。
        /// <para>台阶按原版 <c>PM_WalkMove</c>：贴着原脚高走一遍，被钳住时抬起 <c>StepUpHeight</c> 再走一遍，
        /// 取水平更远的那个 —— 采纳的那一遍返回**落点地面高度**作为 Y。</para>
        /// </summary>
        Vector3 ResolveMove(Vector3 from, Vector3 to, float radius = CsConst.PlayerRadius,
                            float height = CsConst.StandHeight);

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

        /// <summary>
        /// 身体足迹覆盖范围内的地面（中心 + 8 向 9 点，取**最高**的那一面），与
        /// <see cref="ResolveMove"/> 抬腿第二遍的探地同一口径。原版判贴地用 <c>PM_PlayerTrace</c>
        /// （玩家 hull 宽），不是中心零宽射线 —— 站在两级踏面之间时，中心射线只看得到脚下那一级，
        /// 足迹看得到身体压住的那一级，两者口径必须一致，否则落点会逐帧在两级之间来回跳。
        /// </summary>
        /// <param name="maxLift">取最高面时允许高出 <paramref name="pos"/> 的上限（米）：超出的面不参与比较
        /// （连续楼梯上足迹必然同时压到"再上一级"，不过滤就会采到高过一步的那级）。</param>
        bool TrySampleGroundUnderBody(Vector3 pos, float radius, float maxDrop, float maxLift,
                                      out Vector3 point, out Vector3 normal);

        /// <summary>
        /// 身高带是否放得下（自 <paramref name="pos"/> 脚面起算的站立身高带 + <paramref name="radius"/> 半径，
        /// 同一套防穿墙几何校验）。测试入口：台阶/坡沿贴地爬升豁免、BotUnstuck 脱困判定用。
        /// </summary>
        bool BodyHeightClearForTest(Vector3 pos, float radius, out bool legacy);

        /// <summary>
        /// 身体能否站在该点（**含台阶面豁免**）：体积不被占，或被占但它是"迈一步就让开"的
        /// 台阶面/坡面台面（与 <c>ResolveMove</c> 抬腿第二遍的采纳条件 ③ 同款）。
        /// 用途：坡面/台阶上的贴地爬升判定 —— 全宽胶囊在 63° 坡面天然与坡体重叠，
        /// 纯体积判据会把"站在坡面"误杀成"撞墙"。
        /// </summary>
        bool StepFaceStandable(Vector3 pos, float height);

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
