using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Cs16.Core;
using Cs16.Module.Bot;
using Cs16.Module.Map;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 按 <c>de_dust2_geo.bin</c>（**由 CS 1.6 官方 de_dust2.bsp 转换而来**）批量生成
    /// <c>Assets/Scenes/StageDust2.unity</c>。
    ///
    /// <para><b>场景结构</b>（烘焙器按这些名字决定"谁算障碍"，改名必须同步 <see cref="Dust2Layout"/>）</para>
    /// <code>
    /// Level/
    ///   Visual/&lt;贴图名&gt;    真实 dust2 网格（每材质一个合并 Mesh）+ MeshCollider + 真实贴图材质
    ///   Blockers/Blocker_i    每格一个轴对齐 BoxCollider（**唯一参与烘焙的障碍**）
    ///   Markers/&lt;标记名&gt;     空物体，名字严格取 CsMarkers 常量（出生点/包点/买枪区/路线）
    ///   Lighting/            沙漠方向光（BSP light_environment 的真实参数）+ 天空盒
    /// </code>
    ///
    /// <para><b>为什么碰撞要拆成两层</b>：引擎 <c>MapBaker</c> 是**单层 2D 位图**烘焙
    /// （格柱 ∩ 障碍 AABB ⇒ 阻挡）。真实 dust2 是多层地图（T 出生点比 CT 出生点高约 6.8m，
    /// 有斜坡/楼梯/高台），直接把全部网格 MeshCollider 交给它烘，会把"高处的楼板"整片算成阻挡 ⇒
    /// 连通性当场断掉。所以：</para>
    /// <list type="bullet">
    /// <item><b>水平阻挡</b>交给 Blockers（由转换脚本从"近垂直且高 &gt;0.6m 的墙面"逐格化成矩形）——
    /// 烘焙只看得见它，导出的位图逐格可复现；</item>
    /// <item><b>垂直高度</b>（斜坡/楼梯/高台/地面）交给 Visual 的真实 MeshCollider，运行时用
    /// <c>Physics.Raycast</c> 取（见 <c>CsMap.SampleGround</c>），所以爬坡、下楼、跳箱子全部真实；</item>
    /// <item>烘焙时 <see cref="MapBakeRunner"/> 临时关掉 Visual 的碰撞体，烘完恢复 —— 两边都不将就。</item>
    /// </list>
    ///
    /// <para>菜单：<b>Clover/CS16/生成 de_dust2 场景</b>；命令行：
    /// <c>-executeMethod Cs16.EditorTools.Dust2Builder.GenerateFromCommandLine</c>。</para>
    /// </summary>
    public static class Dust2Builder
    {
        private const string Tag = "[Dust2Builder]";

        // ==================================================================
        //  入口
        // ==================================================================

        [MenuItem("Clover/CS16/生成 de_dust2 场景", false, 20)]
        public static void Generate()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError($"{Tag} 正在 Play 模式：生成会切换场景，请先停止 Play 再执行");
                return;
            }
            // 无人值守安全：**不许**弹「是否保存当前场景？」对话框 —— 它是原生模态框，
            // Pipeline 看不到、点不掉，会把编辑器主线程连同 AI 驱动一起卡死（skill P-5）。
            // 本生成器只往 Assets/ 写产物、不读当前场景内容，所以直接以 EmptyScene 新建覆盖即可。
            if (!Application.isBatchMode)
            {
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (activeScene.isDirty)
                {
                    Debug.LogWarning($"{Tag} 当前场景有未保存修改，生成会丢弃它们（不做保存询问，以免卡死自动化驱动）");
                }
            }

            try
            {
                GenerateInternal();
            }
            catch (Exception e)
            {
                Debug.LogError($"{Tag} 生成中止（异常）：{e}");
            }
        }

        /// <summary>命令行/CI 入口（<c>-executeMethod</c> 用）。</summary>
        public static void GenerateFromCommandLine() => Generate();

        private static void GenerateInternal()
        {
            var geo = Dust2GeoData.Load(Dust2Layout.GeoFile);
            if (geo == null)
            {
                Debug.LogError($"{Tag} 没有几何数据，生成中止（先跑 tools 侧的 BSP 转换脚本）");
                return;
            }

            EnsureFolder(Dust2Layout.TextureDir);
            EnsureFolder(Dust2Layout.MaterialDir);
            EnsureFolder(Dust2Layout.SkyboxDir);
            EnsureFolder("Assets/Resources/MapData");

            // ★ 必须先于 BuildVisual：贴图导入尺寸不对时几何/材质的 UV 口径会跟着错（见 NormalizeTextureImporters）
            NormalizeTextureImporters();
            AssertTextureSizesMatchSource();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var level = new GameObject(Dust2Layout.LevelRoot);
            var visualRoot = NewChild(level, "Visual");
            var blockerRoot = NewChild(level, "Blockers");
            var markerRoot = NewChild(level, "Markers");
            var lightingRoot = NewChild(level, "Lighting");

            // ---- ① 真实几何（渲染 + MeshCollider）----
            BuildVisual(geo, visualRoot);

            // ---- ② 阻挡体（唯一参与烘焙的障碍）----
            BuildBlockers(geo, blockerRoot);

            // ---- ③ 标记点（名字 = CsMarkers 常量）----
            DumpMarkers(geo, markerRoot);

            // ---- ④ 光照 / 天空（BSP light_environment 的真实参数）----
            BuildLighting(lightingRoot);
            BuildCamera(geo);

            // ---- ④' 物理层：世界几何标到 CsWorld（必须在存盘前做，否则场景里的层是错的）----
            // 为什么：CsMap.GroundMask() / 子弹 / 视线都按层走。层契约在 Core/CsConst.PhysicsLayers，
            // 层名由 PhysicsLayerSetup 写进 TagManager。**世界几何必须真的挂在那一层上**，
            // 否则 GroundMask 取到的是"只打 CsWorld"的掩码而世界在 Default ⇒ 贴地射线一个都打不到 ⇒
            // 角色一路下坠（另一种静默失效）。角色命中盒的层由 ActorView 在运行期按 bot/真人赋（同一套契约）。
            AssignWorldLayer(level);

            if (!EditorSceneManager.SaveScene(scene, Dust2Layout.ScenePath))
            {
                Debug.LogError($"{Tag} 场景保存失败：{Dust2Layout.ScenePath}");
                return;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // ---- ⑤ 运行时标记表（从**场景里实际存在的标记对象名**导出，命名错了这里就会报）----
            ExportMarkerResource(scene);

            RegisterSceneInBuildSettings();

            Debug.Log($"{Tag} 完成：{Dust2Layout.ScenePath}｜几何 {geo.TotalTriangles} 三角面 / " +
                      $"材质组 {geo.Groups.Length}｜阻挡盒 {geo.Blockers.Length}｜标记 {CountMarkers(geo)} 点");
        }

        // ==================================================================
        //  几何
        // ==================================================================

        /// <summary>
        /// 把 <c>Level</c> 整棵子树标到 <see cref="PhysicsLayers.World"/>（层名由 <c>PhysicsLayerSetup</c> 建）。
        /// <para>为什么整棵树都标：贴地/子弹/视线要打的是"世界几何"（Visual 的真实 MeshCollider +
        /// Blockers 的格子盒）；Markers/Lighting 没有碰撞体，跟着一起标只是为了层级语义一致。</para>
        /// </summary>
        private static void AssignWorldLayer(GameObject levelRoot)
        {
            if (levelRoot == null)
            {
                Debug.LogError($"{Tag} Level 根为空 —— 物理层没标上（贴地射线会打不到世界几何）");
                return;
            }
            var layer = PhysicsLayers.World;
            var all = levelRoot.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < all.Length; i++) all[i].gameObject.layer = layer;
            Debug.Log($"{Tag} 物理层：{Dust2Layout.LevelRoot} 下 {all.Length} 个对象 → " +
                      $"「{PhysicsLayers.WorldName}」(层 {layer})");
        }

        private static void BuildVisual(Dust2GeoData geo, GameObject visualRoot)
        {
            int ok = 0, missingTex = 0;
            for (int i = 0; i < geo.Groups.Length; i++)
            {
                var g = geo.Groups[i];
                if (g.Indices == null || g.Indices.Length < 3) continue;

                var mesh = new Mesh { name = "Dust2_" + Path.GetFileNameWithoutExtension(g.PngName) };
                if (g.Vertices.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = g.Vertices;
                // ★ UV 的单位是**原版 miptex 的原始 w×h**：转换脚本按 `u = …/miptex.w`、`v = 1 − …/miptex.h`
                //   算（`原版资源/解包产物/dust2_build.py:945-951`），不做任何二次幂取整。
                //   ⇒ 贴图**导入后尺寸必须等于原图尺寸**（`npotScale = None`，见 NormalizeTextureImporters），
                //   否则 Unity 把非 POT 贴图拉到最近的 2 的幂再上传，UV 却仍按原尺寸走 ⇒ 比例失真。
                mesh.uv = g.Uvs;
                mesh.normals = g.Normals;
                mesh.SetTriangles(g.Indices, 0, true);
                mesh.RecalculateBounds();

                var meshAsset = $"{Dust2Layout.MaterialDir}/mesh_{Path.GetFileNameWithoutExtension(g.PngName)}.asset";
                AssetDatabase.DeleteAsset(meshAsset);
                AssetDatabase.CreateAsset(mesh, meshAsset);

                var go = new GameObject(g.PngName);
                go.transform.SetParent(visualRoot.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = LoadOrMakeMaterial(g.PngName, ref missingTex);
                // ★ 非凸 MeshCollider：子弹打墙、脚下地面、斜坡台阶全部靠它（运行时那套）
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                // 静态批处理：地图不动，标记成静态后 Unity 会合批绘制
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
                ok++;
            }
            Debug.Log($"{Tag} 几何：{ok} 个材质组已上场景（缺贴图回落到沙色材质 {missingTex} 个）");
        }

        /// <summary>贴图 → 材质：先找 <c>ThirdParty/Dust2/Textures/&lt;png&gt;</c>，找不到退回沙色（并报 Error）。</summary>
        private static Material LoadOrMakeMaterial(string pngName, ref int missingTex)
        {
            string slug = Path.GetFileNameWithoutExtension(pngName);
            string matPath = $"{Dust2Layout.MaterialDir}/{slug}.mat";
            string texPath = $"{Dust2Layout.TextureDir}/{pngName}";

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            if (tex == null)
            {
                // 资源可能刚写进磁盘还没导入 —— 刷一次再试；仍没有就明确报错（不静默用白材质）
                AssetDatabase.Refresh();
                tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
            }
            if (tex == null)
            {
                missingTex++;
                Debug.LogError($"{Tag} 缺贴图 {texPath} → 该材质组用沙色兜底（画面会偏平，检查是不是资源被清了）");
            }

            var mat = new Material(Shader.Find("Standard"));
            mat.name = slug;
            // 出处：原版 GoldSrc 渲染管线**没有「材质色乘算」这一级** —— miptex 贴图本身即 albedo，
            // 亮度只由 lightmap / 顶点光（BSP light_environment）决定（见 策划/对照表.md 的 T-08 / B-03）。
            // ⇒ 原版值 = **恒等白 (1,1,1)**。
            // 这里原先那句 `new Color(0.79f,0.65f,0.42f)`（#C9A66B「沙色」）**没有任何原版出处**，
            // 它是一级额外的暖色乘算：中性石头 (150,149,159) 乘它 ⇒ (118,97,67) ⇒ 整图偏土黄。
            // （切片Y 给的量化预测；切片Z 已落地并实测，数字见 策划/对照表.md B-03 行。）
            mat.color = Color.white;
            mat.SetFloat("_Glossiness", 0.05f);                   // 沙土/砖墙：几乎无高光
            mat.SetFloat("_Metallic", 0f);
            if (tex != null) mat.mainTexture = tex;

            AssetDatabase.DeleteAsset(matPath);
            AssetDatabase.CreateAsset(mat, matPath);
            return mat;
        }

        // ==================================================================
        //  阻挡体
        // ==================================================================

        private static void BuildBlockers(Dust2GeoData geo, GameObject blockerRoot)
        {
            var cell = geo.CellSize;
            for (int i = 0; i < geo.Blockers.Length; i++)
            {
                var b = geo.Blockers[i];
                float w = (b.Ix1 - b.Ix0 + 1) * cell;
                float d = (b.Iz1 - b.Iz0 + 1) * cell;
                float h = b.YMax - b.YMin;

                var go = new GameObject($"Blocker_{i:0000}");
                go.transform.SetParent(blockerRoot.transform, false);
                go.transform.position = new Vector3(
                    geo.OriginX + b.Ix0 * cell + w * 0.5f,
                    b.YMin + h * 0.5f,
                    geo.OriginZ + b.Iz0 * cell + d * 0.5f);

                var col = go.AddComponent<BoxCollider>();
                col.size = new Vector3(w, h, d);
                // ★ 必须是 trigger：这些盒子**只服务烘焙**（供 MapBaker 复算阻挡格），不是真实几何 ——
                //   真实几何已经有 MeshCollider（Visual 那 21 个材质组）。若它们是实心碰撞体，就会**挡住子弹与视线**：
                //   实测拿到"子弹被 Blocker_0145 在 3.12m 处挡下"这类现场，机器人隔着这些隐形墙根本打不到人。
                //   运行时所有射线都带 QueryTriggerInteraction.Ignore，所以设成 trigger 后它们自动退出弹道计算，
                //   而烘焙走 bounds 复算，不受 trigger 影响。
                col.isTrigger = true;
                // 不挂 Renderer：不参与绘制、不投影阴影（否则整张图会被一排隐形高墙的黑影盖住）
                GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            }
            Debug.Log($"{Tag} 阻挡体：{geo.Blockers.Length} 个轴对齐 BoxCollider（每格一个，合并过）");
        }

        // ==================================================================
        //  标记点
        // ==================================================================

        private static void DumpMarkers(Dust2GeoData geo, GameObject markerRoot)
        {
            var missing = new List<string>();
            foreach (var req in Dust2Layout.RequiredMarkers)
            {
                var pts = geo.Points(req.Marker);
                if (pts.Length < req.MinCount) missing.Add($"{req.Marker}({pts.Length}<{req.MinCount})");
            }

            // ★ 标记点先做「落阻挡格 ⇒ 吸附到最近可走格心」（见 SnapMarkerToWalkable 的长注释）：
            //   采样点落在箱子/台阶上时，运行时的 A*/CanStand 拿到的起点就是"站不住"的格。
            //   这里改一次，**场景对象与运行时表**（ExportMarkerResource 走同一段逻辑）同时生效。
            var blocked = geo.BuildBlockedBitmap();

            int total = 0, snapped = 0, notFound = 0;
            foreach (var kv in geo.Markers)
            {
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var src = kv.Value[i];
                    var pos = SnapMarkerToWalkable(geo, blocked, src, out var moved);
                    if (moved > 0)
                    {
                        snapped++;
                        Debug.LogWarning($"{Tag} 标记点吸附：{kv.Key}[{i}] ({src.x:F3},{src.y:F3},{src.z:F3}) → " +
                                         $"({pos.x:F3},{pos.y:F3},{pos.z:F3})｜原格在阻挡盒内，已挪 {moved} 环到最近可走格心（只动 XZ）");
                    }
                    else if (moved < 0)
                    {
                        notFound++;
                        Debug.LogWarning($"{Tag} 标记点吸附失败：{kv.Key}[{i}] ({src.x:F3},{src.y:F3},{src.z:F3}) " +
                                         $"在 {CsBotConst.PathSnapRadiusCells} 环内没有可走格（原因 {(moved == -2 ? "几何/位图不可用，无法判定" : "整片阻挡")}）" +
                                         " ⇒ **保留原值**（不丢点；运行时会由 BotNavigator 跳过它）");
                    }

                    var go = new GameObject(kv.Key);   // ★ 名字就是标记名本身（消费方按名取点）
                    go.transform.SetParent(markerRoot.transform, false);
                    go.transform.position = pos;
                    total++;
                }
            }

            if (missing.Count > 0)
                Debug.LogError($"{Tag} 标记点不足（AI/包点/买枪区会失效）：{string.Join("、", missing)}");
            Debug.Log($"{Tag} 标记点：{geo.Markers.Count} 类 / {total} 个空物体已摆放" +
                      $"｜落阻挡格吸附 {snapped} 点 / 吸不到（保留原值）{notFound} 点（吸附半径 ≤ {CsBotConst.PathSnapRadiusCells} 格）");
        }

        // ==================================================================
        //  标记点吸附（落阻挡格 ⇒ 最近可走格心）
        // ==================================================================

        /// <summary>
        /// 把一个标记点从"落在阻挡格上"挪到**最近的可走格心**（只改 XZ，⛔ y 原样不动）。
        ///
        /// <para><b>为什么必须有</b>：标记点来自 BSP 实体原点（<c>info_player_*</c> / <c>func_bomb_target</c> /
        /// <c>func_buyzone</c>）与位图 BFS 采样，采样点会落在箱子、台阶、墙沿上 —— 那一格在
        /// <c>de_dust2.bytes</c> 里是**阻挡**。运行时消费方（<see cref="BotNavigator"/> 的
        /// <c>AStar.Find</c> / <c>ICsMap.CanStand</c>）对"起点不可走"直接判失败，于是机器人在这些路点上
        /// 集体退化（片BA 实测 19 个点落阻挡格：Bombsite_A 4 / Bombsite_B 5 / BuyZone_CT 7 / BuyZone_T 2 /
        /// Route_CT_Mid 1）。**治本只能在生成侧**：把点挪到最近的可走格心。</para>
        ///
        /// <para><b>判据口径与引擎逐字一致</b>：格坐标 = <see cref="Dust2GeoData.CellOf"/>（
        /// <c>FloorToInt((x-OriginX)/CellSize)</c>，同 <c>MapFormat.cs:243-252</c>）；
        /// 格心 = <see cref="Dust2GeoData.CellCenter"/>；"这一格可走吗" = 不在
        /// <see cref="Dust2GeoData.BuildBlockedBitmap"/> 的阻挡位上（该位图与引擎 <c>MapBaker</c> 的逐格判定
        /// 同语义，见其注释）。⇒ 这里判"可走"的那一格，与运行时 <c>WalkableAt</c> 读到的是同一格。</para>
        ///
        /// <para><b>逐环扩张</b>：先看原格；不可走则按切比雪夫半径 1、2 …（上限
        /// <see cref="CsBotConst.PathSnapRadiusCells"/> = 2，与运行时 <c>BotNavigator.SnapToWalkable</c> 同一半径）
        /// 逐环找第一个可走格 —— 环内按固定的 <c>dz</c> 外 <c>dx</c> 内顺序扫，结果**确定性可复现**
        /// （同一输入两次生成必须得到同一个点，否则生成器不幂等）。</para>
        ///
        /// <para><b>返回值 / <paramref name="moved"/> 口径</b>：<c>0</c> = 原本就在可走格（点未动）；
        /// <c>&gt;0</c> = 挪了几环（切比雪夫半径）；<c>-1</c> = 半径内没有可走格（**保留原值**，⛔ 不许丢点）；
        /// <c>-2</c> = 几何/位图不可用，无法判定（**保留原值**）。调用方对一切负值都必须留痕。</para>
        /// </summary>
        private static Vector3 SnapMarkerToWalkable(Dust2GeoData geo, bool[] blocked, Vector3 p, out int moved)
        {
            if (geo == null || blocked == null || blocked.Length != geo.Width * geo.Depth)
            {
                moved = -2;
                return p;
            }

            geo.CellOf(p.x, p.z, out var ix, out var iz);
            if (IsWalkableCell(geo, blocked, ix, iz))
            {
                moved = 0;
                return p;
            }

            for (var r = 1; r <= CsBotConst.PathSnapRadiusCells; r++)
            {
                for (var dz = -r; dz <= r; dz++)
                {
                    for (var dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Abs(dx) != r && Mathf.Abs(dz) != r) continue;   // 只看这一环（里环已经查过）
                        if (!IsWalkableCell(geo, blocked, ix + dx, iz + dz)) continue;
                        var c = geo.CellCenter(ix + dx, iz + dz);
                        moved = r;
                        return new Vector3(c.x, p.y, c.z);      // ★ 只改 XZ：y 是 BSP 实体的高度，不许动
                    }
                }
            }

            moved = -1;
            return p;                                           // 吸不到 ⇒ 保留原值（调用方 Warn，⛔ 不丢点）
        }

        /// <summary>这一格可走吗（图外 = 不可走；与引擎烘焙位图的取整口径一致）。</summary>
        private static bool IsWalkableCell(Dust2GeoData geo, bool[] blocked, int ix, int iz)
        {
            if (ix < 0 || iz < 0 || ix >= geo.Width || iz >= geo.Depth) return false;
            return !blocked[iz * geo.Width + ix];
        }

        private static int CountMarkers(Dust2GeoData geo)
        {
            int n = 0;
            foreach (var kv in geo.Markers) n += kv.Value.Count;
            return n;
        }

        // ==================================================================
        //  光照 / 天空 / 相机
        // ==================================================================

        /// <summary>
        /// BSP 里 <c>light_environment</c> 的真实参数：<c>_light "255 255 128 70"</c>、
        /// <c>angle 43</c>、<c>pitch -60</c> ⇒ 暖黄色太阳、仰角 60°、偏航 43°。
        /// 天空盒是 dust2 的真实天空（<c>worldspawn skyname = des</c> → gfx/env/des*.tga）。
        /// </summary>
        private static void BuildLighting(GameObject lightingRoot)
        {
            var sunGo = new GameObject("Sun (dust2 light_environment)");
            sunGo.transform.SetParent(lightingRoot.transform, false);
            sunGo.transform.rotation = Quaternion.Euler(60f, 43f, 0f);
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color32(255, 255, 128, 255);
            // intensity 1.75（片X）→ **0.70**（片AA 2026-09-21 重新平衡）：片X 定的 1.75 是配
            // **旧材质色 (0.79,0.65,0.42) 乘算**（albedo 亮度 0.663）调出来的；片Z 把材质色改成
            // `Color.white`（原版 GoldSrc 无材质色乘算这一级）后 albedo 亮度 → 1.0（×1.51），
            // 同一套光照下画面整体**过曝**（同机位 1920×1080 帧 meanLum 153.4、p95=245、
            // ≥250 像素占 **21.6%**；地面石板框 260,880,660,1050 由 (159,106,39) 跳到 (242,217,97) Lum 214.1）。
            // 材质色与光照是一个整体、不能各调各的，故本片**只按实测像素数字**把光重新压回基线量级。
            // 量化出处 = 原版基线图 `策划/基线图/original/de_dust2_freecam_A_00.jpg` 的**内容区**
            // （裁掉上下信箱黑边后 1280×810）meanLum 123.1 / p50 127 / p95 175 / 平均饱和度 0.523
            // / ≥250 像素 0.055%（G-17 已登记"原版 `_light "255 255 128 70"` 的亮度口径在 Unity
            // intensity 上无逐值对应" ⇒ 以基线图像素为准）。
            // 逐档实测（同机位、单变量，见 策划/对照表.md §AA）：
            //   sun 1.75→153.4 / 1.20→130.8 / 1.10→126.3 / 1.05→124.0 / 0.90→117.2 / 0.85→115.0 / 0.70→?
            // 只降直射时 meanLum 落不回 123 且 p95 几乎不动（峰值来自由环境梯度照亮的整片沙地，
            // 不只是直射高光）⇒ 改成"**降直射 + 抬 Trilight 环境梯度**"：环境占比高、直射才产生尖峰，
            // 这样能在同一 meanLum 下把 ≥250 像素压到基线量级。最终档 sun 0.70 + 环境梯度 ×1.40：
            //   meanLum **123.5**（Δ +0.4 / +0.3%）· p50 118 · p95 208 · 平均饱和度 0.384 · ≥250 像素 **0.145%**。
            // ⛔ 只动光照参数，⛔ 不加任何滤镜/后处理；⛔ 未把材质色乘算加回来。
            sun.intensity = 0.70f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            // Trilight 的三条梯度色 = 本场景**真正的**环境光（见下）；片AA 按 ×1.40 抬：
            //   150,150,170 → 210,210,238 ｜ 130,115,90 → 182,161,126 ｜ 80,68,50 → 112,95,70
            // 目的：把整体亮度从"直射"转一部分到"环境"，从而在 meanLum 不变的前提下削掉直射尖峰
            // （≥250 像素 21.6% → 0.145%，基线 0.055%）。
            RenderSettings.ambientSkyColor = new Color32(210, 210, 238, 255);
            RenderSettings.ambientEquatorColor = new Color32(182, 161, 126, 255);
            RenderSettings.ambientGroundColor = new Color32(112, 95, 70, 255);
            // ambientIntensity 归 1（默认）：**片AA 实测它是空操作** —— `ambientMode = Trilight` 下
            // Unity **不把 ambientIntensity 计入 ambient 计算**（只在 Flat / Skybox 模式生效），
            // 所以片X 那句"1→1.55"改了个没有任何效果的值（aa-L0/L1/L2 三档 ambIntensity=1.55/1.00/0.70
            // 采出的 1920×1080 帧**逐像素完全相同**，见 策划/对照表.md §AA）。真正的环境光在这里是
            // 上面三条 Gradient 颜色（ambientSky/Equator/Ground），它们**不被 ambientIntensity 调制**。
            // ⇒ 归 1，避免再有人以为"调 1.55 能补亮度"。
            RenderSettings.ambientIntensity = 1.0f;

            // 沙漠薄雾：远处沙色发白，贴近原版 dust2 的通透感（不影响近处辨识）
            // density 0.0025 → 0.010（片X 定案）：目标不是"抬远景带 RMS"（实测雾对本场景的远景带 RMS **无影响**，
            //   见 策划/对照表.md §X：那一段是天空盒主导，而 Unity 天空盒着色器**不吃雾**），
            //   而是按下述**实测**效果收敛到基线：meanLum +4.7（99.4 → 104.1）、平均饱和度 −0.038（0.546 → 0.508）。
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.010f;
            RenderSettings.fogColor = new Color32(214, 197, 155, 255);

            var sky = MakeSkyboxMaterial();
            if (sky != null) RenderSettings.skybox = sky;
            else RenderSettings.ambientMode = AmbientMode.Trilight;
        }

        private static Material MakeSkyboxMaterial()
        {
            string[] slots = { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex" };

            // ✅ 绑定 = **同名直塞**（`_LeftTex ← sky_lf` / `_RightTex ← sky_rt`），
            //    且 `up`/`dn` 用**预先旋转好的**贴图（`sky_up_r90cw` / `sky_dn_r270cw`）。
            //
            // 依据（agent-24 用 Unity 自己的渲染实测 + 原版像素直算，见 `策划/外观差异清单.md` C1/C1-b / 对照表 §9-C）：
            //   · 6 面**像素**与原版 TGA 逐像素相同（6/6，maxdiff = 0）⇒ 不是缺面 / 装错文件 / 分辨率不符；
            //   · 4 条竖边的真实邻接（两两竖边逐像素差，越小越接得上）= `ft` 右 = `lf` 左（3828）·
            //     `lf` 右 = `bk` 左（4777）· `bk` 右 = `rt` 左（4134）· `rt` 右 = `ft` 左（3158）
            //     ⇒ 真环序 = `ft → lf → bk → rt`（4 条缝全接得上）；镜像装配 4 条缝全部对不上（7× 差）
            //     —— 用户报的"天空盒左右两侧有接缝/破洞"就在这里。
            //   · ⛔ **Unity 的槽位方向是 `Left=+X` / `Right=-X`**（内置着色器属性名逐字：`_LeftTex "Left [+X]"`、
            //     `_RightTex "Right [-X]"`，见 `unity_builtin_extra`），edit-mode 渲染也实测 `sky_rt` 落在 +X、
            //     `sky_lf` 落在 -X。⇒ 要让**世界**环序 = `ft→lf→bk→rt`（即 +Z → +X → -Z → -X），
            //     **同名直塞就是唯一对的**；写成 `_LeftTex ← sky_rt` 会得到**镜像环**
            //     （agent-22 那次"交换一对相对面"就是踩了这个：它按"Left = 你向左看看到的那面"假设，与 Unity 相反）。
            //   · `up`/`dn`：edit-mode 把 6 个轴向各渲一遍再与源贴图做 8 朝向匹配，**6/6 都是"不旋转不翻转"**
            //     （即 Unity 把贴图原样贴上去），而 `Skybox/6 Sided` 没有逐面旋转/平铺（`[NoScaleOffset]`）
            //     ⇒ 只能**预先把贴图转好**：`up` 需 90° 顺时针、`dn` 需 270° 顺时针。
            //     判据 = 立方体邻接（up/dn 四条边必须与四侧面的顶/底边像素相接）：转好后 4 条边逐像素差
            //     1068 / 1247 / 1338 / 2798（up）、3780 / 4236 / 5389 / 6945（dn），次优 111965 / 83200
            //     ⇒ 区分度 17.4× / 4.1×（远高于 1.5× 门槛）。
            string[] files = { "sky_ft.png", "sky_bk.png", "sky_lf.png", "sky_rt.png", "sky_up_r90cw.png", "sky_dn_r270cw.png" };

            var shader = Shader.Find("Skybox/6 Sided");
            if (shader == null)
            {
                Debug.LogError($"{Tag} 找不到 Skybox/6 Sided 着色器 → 场景没有天空盒（检查渲染管线设置）");
                return null;
            }

            var mat = new Material(shader) { name = "Dust2Sky" };
            int found = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dust2Layout.SkyboxDir}/{files[i]}");
                if (tex == null)
                {
                    AssetDatabase.Refresh();
                    tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dust2Layout.SkyboxDir}/{files[i]}");
                }
                if (tex == null)
                {
                    Debug.LogError($"{Tag} 缺天空盒面 {files[i]}（第六面缺失会让天空盒整体不显示）");
                    continue;
                }
                mat.SetTexture(slots[i], tex);
                found++;
            }
            if (found != 6)
            {
                Debug.LogError($"{Tag} 天空盒只凑齐 {found}/6 面，改用沙色背景");
                return null;
            }

            string path = $"{Dust2Layout.SkyboxDir}/Dust2Sky.mat";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mat, path);
            Debug.Log($"{Tag} 天空盒：dust2 真实天空（des）{found}/6 面 → {path}");
            return mat;
        }

        private static void BuildCamera(Dust2GeoData geo)
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";
            var cam = go.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            // 口径说明：CsConst.DefaultFov 是**水平** FOV（见 CsConst 的注释 + HLSDK view.cpp:1737-1752），
            // 而这台只是"保证场景不是黑屏"的编辑器预览相机 —— 比赛运行时它会被
            // FirstPersonCamera.DisableForeignMainCameras 关掉，真实视口由 FirstPersonCamera
            // 每帧按当前宽高比换算（FovYFromFovX）。故此处不做换算、保持原样。
            cam.fieldOfView = CsConst.DefaultFov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 1000f;

            // 先站在"地图中部上空俯瞰"：真正的 FPS 相机由 agent-04 接管，这里只保证场景不是黑屏
            go.transform.position = new Vector3(geo.WorldMin.x * 0.25f, 28f, geo.WorldMin.z * 0.35f);
            go.transform.rotation = Quaternion.Euler(28f, 25f, 0f);
        }

        // ==================================================================
        //  运行时标记表（场景对象名 = 命名真源）
        // ==================================================================

        /// <summary>
        /// 把场景 <c>Level/Markers</c> 下的标记对象导成运行时资源
        /// （<c>Resources/MapData/de_dust2_markers.bytes</c>，文本：每行 <c>标记名 x y z</c>）。
        ///
        /// <para>为什么要有这份表：<c>CsMap.Points(marker)</c> 在运行时必须拿到点位，而 CloverMap
        /// 位图里**没有名字**。表由场景对象名生成 ⇒ 名字写错 / 少摆标记，这里就会报 Error，
        /// 不会出现"进图后机器人集体不动却没人知道为什么"。</para>
        /// </summary>
        public static void ExportMarkerResource(Scene scene)
        {
            var root = FindInScene(scene, Dust2Layout.MarkerRoot);
            if (root == null)
            {
                Debug.LogError($"{Tag} 场景里没有 {Dust2Layout.MarkerRoot} → 无法导出运行时标记表");
                return;
            }

            // ★ 与 DumpMarkers **同一段吸附逻辑**（本方法可能被 RefreshMarkerTable 在"未重建场景"时单独调用，
            //   那时场景里的标记对象还是旧坐标 ⇒ 吸附必须在这里也做一遍，才能保证"运行时表 == 场景对象应有位置"）。
            //   幂等：已经在可走格上的点 moved == 0，不会二次位移。
            var geo = Dust2GeoData.Load(Dust2Layout.GeoFile);
            var blocked = geo != null ? geo.BuildBlockedBitmap() : null;
            if (geo == null)
                Debug.LogWarning($"{Tag} 导出运行时标记表时读不到 {Dust2Layout.GeoFile} ⇒ " +
                                 "**不做**「落阻挡格吸附」（表按场景对象原值导出，可能有标记点仍落在阻挡格上）");

            var known = new HashSet<string>();
            foreach (var req in Dust2Layout.RequiredMarkers) known.Add(req.Marker);

            var order = new List<string>();
            var table = new Dictionary<string, List<Vector3>>();
            var unknown = new List<string>();
            int snapped = 0, notFound = 0;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root.transform) continue;
                string n = t.gameObject.name;
                if (!known.Contains(n))
                {
                    if (!unknown.Contains(n)) unknown.Add(n);
                    continue;
                }
                if (!table.TryGetValue(n, out var list))
                {
                    list = new List<Vector3>();
                    table[n] = list;
                    order.Add(n);
                }

                var src = t.position;
                var pos = SnapMarkerToWalkable(geo, blocked, src, out var moved);
                if (moved > 0)
                {
                    snapped++;
                    Debug.LogWarning($"{Tag} 运行时表吸附：{n}[{list.Count}] ({src.x:F3},{src.y:F3},{src.z:F3}) → " +
                                     $"({pos.x:F3},{pos.y:F3},{pos.z:F3})｜原格在阻挡盒内，已挪 {moved} 环到最近可走格心（只动 XZ）");
                }
                else if (moved < 0)
                {
                    notFound++;
                    Debug.LogWarning($"{Tag} 运行时表吸附失败：{n}[{list.Count}] ({src.x:F3},{src.y:F3},{src.z:F3}) " +
                                     $"在 {CsBotConst.PathSnapRadiusCells} 环内没有可走格（原因 {(moved == -2 ? "几何/位图不可用，无法判定" : "整片阻挡")}）" +
                                     " ⇒ **保留原值**（不丢点；运行时会由 BotNavigator 跳过它）");
                }
                list.Add(pos);
            }

            if (unknown.Count > 0)
                Debug.LogError($"{Tag} 标记对象名不在 CsMarkers 里（写错名字 = 消费方永远取不到）：" +
                               string.Join("、", unknown));

            var sb = new StringBuilder();
            sb.Append("# clover cs16 markers v1 (marker x y z) —— 由 Dust2Builder.ExportMarkerResource 生成\n");
            int total = 0;
            foreach (var name in order)
            {
                foreach (var p in table[name])
                {
                    sb.Append(name).Append(' ')
                      .Append(p.x.ToString("F3", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(p.y.ToString("F3", CultureInfo.InvariantCulture)).Append(' ')
                      .Append(p.z.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
                    total++;
                }
            }

            var missing = new List<string>();
            foreach (var req in Dust2Layout.RequiredMarkers)
            {
                if (!table.TryGetValue(req.Marker, out var list) || list.Count < req.MinCount)
                    missing.Add($"{req.Marker}({((table.TryGetValue(req.Marker, out var l2) ? l2.Count : 0))}<{req.MinCount})");
            }

            string dir = Path.GetDirectoryName(Dust2Layout.MarkerResourceFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Dust2Layout.MarkerResourceFile, sb.ToString(), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(Dust2Layout.MarkerResourceFile);

            if (missing.Count > 0)
                Debug.LogError($"{Tag} 标记点数不足（机器人/包点/买枪区会失效）：{string.Join("、", missing)}");
            Debug.Log($"{Tag} 运行时标记表：{Dust2Layout.MarkerResourceFile}（{order.Count} 类 / {total} 点）" +
                      $"｜落阻挡格吸附 {snapped} 点 / 吸不到（保留原值）{notFound} 点（吸附半径 ≤ {CsBotConst.PathSnapRadiusCells} 格）");
        }

        /// <summary>
        /// **重生成运行时标记表（不重建场景）**：读几何 → 打开已生成场景 → 重新走一遍
        /// <see cref="ExportMarkerResource"/>。
        ///
        /// <para><b>为什么要有这条单独入口</b>：标记点的吸附口径改动只影响
        /// <c>Resources/MapData/de_dust2_markers.bytes</c> 这一个产物；重跑整条生成链
        /// （<see cref="GenerateFromCommandLine"/>）会连带重写全部网格 / 材质 / 贴图导入设置 —— 那是
        /// 几百 MB 资产的无谓改写。这条入口只碰标记表，且**幂等**（吸附过的点在可走格上 ⇒ moved == 0，不再位移）。</para>
        ///
        /// <para>命令行：<c>unity command eval 'Cs16.EditorTools.Dust2Builder.RefreshMarkerTable();'</c></para>
        /// </summary>
        public static void RefreshMarkerTable()
        {
            var geo = Dust2GeoData.Load(Dust2Layout.GeoFile);
            if (geo == null)
            {
                Debug.LogError($"{Tag} 重生成标记表中止：读不到 {Dust2Layout.GeoFile}");
                return;
            }

            if (EditorApplication.isPlaying)
            {
                Debug.LogError($"{Tag} 正在 Play 模式：重生成标记表会切换场景，请先停止 Play 再执行");
                return;
            }

            // 与 Generate() 同一个口径：**不许**弹「是否保存当前场景？」原生模态框（Pipeline 看不到、点不掉，
            // 会把编辑器主线程连 AI 驱动一起卡死，见 skill P-5）。本入口只读场景里的标记对象，不改场景内容。
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!Application.isBatchMode && activeScene.isDirty)
            {
                Debug.LogWarning($"{Tag} 当前场景有未保存修改，重生成标记表会切换到 {Dust2Layout.ScenePath}" +
                                 "（不做保存询问，以免卡死自动化驱动）");
            }

            if (!File.Exists(Dust2Layout.ScenePath))
            {
                Debug.LogError($"{Tag} 重生成标记表中止：场景不存在 {Dust2Layout.ScenePath}（先跑一次生成器）");
                return;
            }

            var scene = EditorSceneManager.OpenScene(Dust2Layout.ScenePath, OpenSceneMode.Single);
            ExportMarkerResource(scene);
        }

        // ==================================================================
        //  贴图导入尺寸（非二次幂贴图**不许被缩放**）
        // ==================================================================

        /// <summary>
        /// 把 <see cref="Dust2Layout.TextureDir"/> 下**所有** PNG 的 <c>TextureImporter.npotScale</c>
        /// 统一成 <see cref="TextureImporterNPOTScale.None"/>（= 导入后尺寸 == 原图尺寸）。
        ///
        /// <para><b>为什么必须由生成器做</b>：GoldSrc 的 miptex 直接按**原始尺寸**贴，没有二次幂要求
        /// （原版 de_dust2 用到的 47 张里 22 张不是 POT：`256×192` / `192×160` / `96×32` / `128×240` …）。
        /// 而 Unity 的默认导入设置是 <c>npotScale = ToNearest</c> —— 非 POT 贴图会被**拉伸到最近的
        /// 2 的幂**再上传（实测 <c>SandCCrete.png</c> 原图 `256×192` → 导入成 `256×256`，
        /// <c>SandWllDoor.png</c> `192×160` → `256×128`，两个轴同时被改）。
        /// 网格的 UV 却是按**原图 w×h** 算的（<c>dust2_build.py:945-951</c>：
        /// <c>u = …/miptex.w</c>、<c>v = 1 − …/miptex.h</c>）⇒ 两边口径不一致 ⇒ **贴图比例失真**。
        /// </para>
        ///
        /// <para><b>为什么不能手工点 Inspector / 只改 <c>.meta</c></b>：前者不可复现且下次没人记得；
        /// 后者只要重跑一次生成链（重导/重建资产）就回退。写在这里 = 生成器每跑一次就把口径钉死。</para>
        ///
        /// <para><b>幂等</b>：只对"当前不是 <c>None</c>"的资产赋新值 + <c>SaveAndReimport()</c>；
        /// 已经是 <c>None</c> 的只读不写 ⇒ 重跑不炸、不重复导入、结果一致。
        /// 末尾**读回复核**一遍（不靠"我设过了"，靠"再查一遍"），仍有非 None 就报 Error。</para>
        ///
        /// <para>⛔ 只改**导入设置**，PNG 像素一个字节都不动（改的是 <c>.meta</c>）。</para>
        /// </summary>
        private static void NormalizeTextureImporters()
        {
            if (!AssetDatabase.IsValidFolder(Dust2Layout.TextureDir))
            {
                Debug.LogError($"{Tag} 贴图目录不存在：{Dust2Layout.TextureDir} ⇒ 跳过导入设置（地图会整片缺贴图）");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { Dust2Layout.TextureDir });
            if (guids.Length == 0)
            {
                Debug.LogError($"{Tag} {Dust2Layout.TextureDir} 下没有任何 Texture2D ⇒ 导入设置无事可做（先跑 dust2_build.py）");
                return;
            }

            int changed = 0, already = 0, bad = 0;
            var changedNames = new List<string>();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null)
                {
                    bad++;
                    Debug.LogError($"{Tag} {path} 拿不到 TextureImporter ⇒ 导入设置没改到");
                    continue;
                }
                if (imp.npotScale == TextureImporterNPOTScale.None) { already++; continue; }
                var was = imp.npotScale;
                imp.npotScale = TextureImporterNPOTScale.None;
                imp.SaveAndReimport();
                changed++;
                changedNames.Add($"{Path.GetFileName(path)}({was}→None)");
            }

            int stillNonNone = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Dust2Layout.TextureDir }))
            {
                var imp = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid)) as TextureImporter;
                if (imp != null && imp.npotScale != TextureImporterNPOTScale.None) stillNonNone++;
            }
            if (stillNonNone > 0)
                Debug.LogError($"{Tag} 贴图导入设置未落盘：读回仍有 {stillNonNone} 张 npotScale != None");

            Debug.Log($"{Tag} 贴图导入：{guids.Length} 张 → npotScale=None" +
                      $"（本次改写 {changed} 张 / 原本已是 {already} 张 / 读回仍非 None {stillNonNone} 张 / 非 TextureImporter {bad} 张）" +
                      (changed > 0 ? $"｜改写清单：{string.Join("、", changedNames)}" : "｜（无改写 = 幂等复跑）"));
        }

        /// <summary>
        /// **闸门**：逐张断言「原图 w×h == 导入后 <c>Texture2D.width×height</c>」。
        ///
        /// <para>原图尺寸直接读 PNG 文件头的 IHDR（磁盘字节，不经过任何导入器）；
        /// 导入尺寸取 <c>AssetDatabase.LoadAssetAtPath&lt;Texture2D&gt;</c> 的实际宽高。
        /// 两者不等就是 <see cref="NormalizeTextureImporters"/> 没生效（或有别的缩放设置，
        /// 例如 <c>maxTextureSize</c> 比原图小）⇒ 报 Error 并列出是哪几张。</para>
        /// </summary>
        private static void AssertTextureSizesMatchSource()
        {
            string absDir = Path.Combine(Application.dataPath,
                Dust2Layout.TextureDir.Substring("Assets/".Length)).Replace('\\', '/');
            var pngs = Directory.Exists(absDir)
                ? Directory.GetFiles(absDir, "*.png", SearchOption.TopDirectoryOnly)
                : new string[0];
            Array.Sort(pngs, StringComparer.Ordinal);
            if (pngs.Length == 0)
            {
                Debug.LogError($"{Tag} 尺寸断言：{absDir} 下没有 PNG ⇒ 断言没跑起来（先跑 dust2_build.py）");
                return;
            }

            int ok = 0, diff = 0;
            var bad = new List<string>();
            for (int i = 0; i < pngs.Length; i++)
            {
                string file = pngs[i];
                string assetPath = Dust2Layout.TextureDir + "/" + Path.GetFileName(file);
                int sw, sh;
                if (!TryReadPngSize(file, out sw, out sh))
                {
                    diff++;
                    bad.Add($"{Path.GetFileName(file)}(读不到 IHDR)");
                    continue;
                }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (tex == null)
                {
                    diff++;
                    bad.Add($"{Path.GetFileName(file)}(导入后取不到 Texture2D)");
                    continue;
                }
                if (tex.width == sw && tex.height == sh)
                {
                    ok++;
                    Debug.Log($"{Tag} 尺寸 {Path.GetFileName(file)}：原图 {sw}x{sh} = 导入 {tex.width}x{tex.height} ✓");
                }
                else
                {
                    diff++;
                    bad.Add($"{Path.GetFileName(file)}(原图 {sw}x{sh} ≠ 导入 {tex.width}x{tex.height})");
                }
            }
            if (diff > 0)
                Debug.LogError($"{Tag} 尺寸断言失败：{ok}/{pngs.Length} 相等，{diff} 张不等 ⇒ {string.Join("、", bad)}");
            else
                Debug.Log($"{Tag} 尺寸断言通过：{pngs.Length}/{pngs.Length} 张「原图 w×h == 导入 w×h」（非 POT 也没被缩放）");
        }

        /// <summary>读 PNG 文件头 IHDR 的宽高（PNG 签名 8B + 长度 4B + "IHDR" 4B + w/h 各 4B 大端）。</summary>
        private static bool TryReadPngSize(string absPath, out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                using (var fs = new FileStream(absPath, FileMode.Open, FileAccess.Read))
                {
                    var head = new byte[24];
                    if (fs.Read(head, 0, head.Length) != head.Length) return false;
                    if (head[0] != 0x89 || head[1] != 0x50 || head[2] != 0x4E || head[3] != 0x47) return false;
                    if (head[12] != (byte)'I' || head[13] != (byte)'H' ||
                        head[14] != (byte)'D' || head[15] != (byte)'R') return false;
                    w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                    h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                    return w > 0 && h > 0;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"{Tag} 读 PNG 头失败 {absPath}：{e.Message}");
                return false;
            }
        }

        // ==================================================================
        //  小工具
        // ==================================================================

        /// <summary>场景里按层级路径找对象（层级是生成器定的，这里按名字找，不做全场景扫描）。</summary>
        public static GameObject FindInScene(Scene scene, string hierarchyPath)
        {
            var parts = hierarchyPath.Split('/');
            GameObject cur = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == parts[0]) { cur = root; break; }
            }
            for (int i = 1; i < parts.Length && cur != null; i++)
            {
                Transform next = null;
                foreach (Transform c in cur.transform)
                {
                    if (c.name == parts[i]) { next = c; break; }
                }
                cur = next != null ? next.gameObject : null;
            }
            return cur;
        }

        private static GameObject NewChild(GameObject parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            return go;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) parent = parent.Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
            {
                Debug.LogError($"{Tag} 目录路径非法：{path}");
                return;
            }
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void RegisterSceneInBuildSettings()
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == Dust2Layout.ScenePath);
            list.Add(new EditorBuildSettingsScene(Dust2Layout.ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
            int idx = list.FindIndex(s => s.path == Dust2Layout.ScenePath);
            Debug.Log($"{Tag} Build Settings：{Dust2Layout.ScenePath} 已加入（索引 {idx}，共 {list.Count} 个）");
        }
    }
}
