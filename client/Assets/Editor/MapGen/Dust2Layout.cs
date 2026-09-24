using Cs16.Core;
using Cs16.Module.Map;

namespace Cs16.EditorTools
{
    /// <summary>
    /// de_dust2 的**布局数据**（纯数据，扩展/调整只改这里）。
    ///
    /// <para>
    /// ★ 本项目的几何**不来自手写矩形表**，而来自 **CS 1.6 官方 de_dust2 的 BSP**（用户指示：
    /// "资源就用 CS 1.6 的现成资源和地图，你只需要实现逻辑"）。原来 §3 那种"布局数据 → 批量生成 Box"
    /// 只是兜底方案，本工程走的是保真路线：
    /// </para>
    /// <list type="bullet">
    /// <item><c>de_dust2.bsp</c>（2.0MB，GoldSource BSP v30）由 `tools/` 侧脚本转换成
    /// <see cref="GeoFileName"/>（几何 + 碰撞盒 + 标记 + 烘焙参数），生成器与烘焙器都读它；</item>
    /// <item>几何：世界刷 + 木箱刷（func_breakable）全部面，真实坐标（1 unit = 1 inch = 0.0254m）；
    /// 贴图：15 张从 BSP 内嵌数据解出（自带调色板），其余按角色回落到 de_dust2 素材包；</item>
    /// <item>标记点：出生点/包点/买枪区**直接来自 BSP 实体**（<c>info_player_deathmatch</c> ×20、
    /// <c>info_player_start</c> ×20、<c>func_bomb_target</c> A/B、<c>func_buyzone</c> 1=T / 2=CT）；
    /// 路线锚点由真实位图 BFS 最短路径采样得到。</item>
    /// </list>
    ///
    /// <para>
    /// 这里定义的是**生成器/烘焙器/探针共用的一份约定**：资产路径、层级名、标记清单与最小点数、
    /// 烘焙参数的文件位置。改这些**工程内资产路径**只需改本文件（运行期的**素材加载路径**真源是
    /// <see cref="ResPaths"/>；标记名真源是
    /// <see cref="CsMarkers"/>，两者必须一致 —— 生成器用的是 <see cref="CsMarkers"/> 常量本身）。
    /// </para>
    /// </summary>
    public static class Dust2Layout
    {
        // ---- 资源（CS 1.6 原始资源 + 转换产物，全部落在 Assets/ThirdParty/Dust2/）----
        public const string AssetRoot = "Assets/ThirdParty/Dust2";
        /// <summary>原始 BSP（溯源用，运行时不需要）。</summary>
        public const string BspFile = AssetRoot + "/de_dust2.bsp";
        /// <summary>转换后的几何/碰撞/标记数据（生成器与烘焙器的输入）。</summary>
        public const string GeoFileName = "de_dust2_geo.bin";
        public const string GeoFile = AssetRoot + "/" + GeoFileName;
        public const string TextureDir = AssetRoot + "/Textures";
        public const string SkyboxDir = AssetRoot + "/Skybox";
        /// <summary>由生成器派生的材质球（一处收敛：换贴图在这里换）。</summary>
        public const string MaterialDir = AssetRoot + "/Materials";

        // ---- 场景与产物 ----
        public const string ScenePath = "Assets/Scenes/" + SceneNames.StageDust2 + ".unity";
        /// <summary>服务端/参考产物目录（引擎烘焙器写两份，同源）。</summary>
        public const string ServerMapDir = "Assets/MapData";
        /// <summary>客户端运行时读取的产物目录（必须在 Resources 下）。</summary>
        public const string ClientMapDir = "Assets/Resources/MapData";

        // ---- 场景层级（烘焙器按名字决定"谁算障碍"）----
        public const string LevelRoot = "Level";
        /// <summary>真实几何：渲染 + MeshCollider（物理/子弹/地面）——**烘焙时临时关闭**。</summary>
        public const string VisualRoot = LevelRoot + "/Visual";
        /// <summary>阻挡体：每格一个轴对齐 BoxCollider ——**只有它参与烘焙**，位图由它逐格还原。</summary>
        public const string BlockerRoot = LevelRoot + "/Blockers";
        /// <summary>
        /// 标记点根对象（名字 = <see cref="CsMarkers"/> 里的字符串）。
        /// <para>⚠️ <b>必须是**场景根对象**</b>（⛔ 不是 <c>Level</c> 的子物体）：引擎烘焙器按
        /// <c>MapBakeOptions.MarkerRootName</c> 在**场景根对象列表**里按名字找它
        /// （<c>Editor/MapBake/MapBaker.cs:424-427</c> 的 <c>scene.GetRootGameObjects()</c>），
        /// 找到后把该根下每个子物体的"对象名 = 标记名、世界坐标 = 点位"写进 <c>.bytes</c> 的
        /// <c>FlagMarkers</c> 段（<c>MapBaker.cs:418-457</c>）。
        /// 引擎文档里那句"如 cs16 的 <c>Level/Markers</c> 那个 <c>Markers</c>"说的就是本对象 —— 名字是 <c>Markers</c>；
        /// 旧层级把它挂在 <c>Level</c> 下（烘焙器看不见），本轮已提到场景根。</para>
        /// </summary>
        public const string MarkerRoot = "Markers";
        public const string LightingRoot = LevelRoot + "/Lighting";

        /// <summary>
        /// 出生点标记的对象名前缀：引擎 <c>MapBaker</c> 按此前缀收集出生点写进 .bytes
        /// （与 <c>MapBakeOptions.SpawnMarkerPrefix</c> 的默认值一致，两边不许各写一个）。
        /// </summary>
        public const string SpawnMarkerPrefix = "Spawn";

        /// <summary>标记名 + 验收最小点数（任务书 §5 的硬要求，探针按此断言）。</summary>
        public static readonly (string Marker, int MinCount)[] RequiredMarkers =
        {
            (CsMarkers.SpawnT, 5),
            (CsMarkers.SpawnCT, 5),
            (CsMarkers.BombsiteA, 1),
            (CsMarkers.BombsiteB, 1),
            (CsMarkers.BuyZoneT, 1),
            (CsMarkers.BuyZoneCT, 1),
            (CsMarkers.TAttackA, 3),
            (CsMarkers.TAttackB, 3),
            (CsMarkers.TMid, 3),
            (CsMarkers.CTDefendA, 3),
            (CsMarkers.CTDefendB, 3),
            (CsMarkers.CTMid, 3),
            (CsMarkers.Patrol, 8),
        };

        /// <summary>连通性自证要跑通的点对（起点标记, 终点标记）。</summary>
        public static readonly (string From, string To)[] ConnectivityPairs =
        {
            (CsMarkers.SpawnT, CsMarkers.BombsiteA),
            (CsMarkers.SpawnT, CsMarkers.BombsiteB),
            (CsMarkers.SpawnT, CsMarkers.SpawnCT),
        };

        /// <summary>逻辑地图 id（与服务端 mmo 场景 id / 客户端 CloverScene.SceneID 对齐；单机版自用）。</summary>
        public const ulong SceneId = 1;
    }
}
