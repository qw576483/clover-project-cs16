using System.Collections.Generic;
using System.IO;
using CloverEngine;
using Cs16.App;
using Cs16.Core;
using Cs16.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 一键生成流程骨架：
    /// <list type="number">
    /// <item><c>Assets/Scenes/Boot.unity</c> —— Main Camera + <c>Bootstrap</c>（唯一组装点）；</item>
    /// <item><c>Assets/Scenes/Menu.unity</c> —— Main Camera + Canvas + EventSystem；</item>
    /// <item><c>Assets/Resources/UI/*.prefab</c> —— 8 个流程面板（含真实按钮 / 文本 / 滑块 / 进度条）；</item>
    /// <item>把两个场景写进 Build Settings（Boot 必须在索引 0）。</item>
    /// </list>
    ///
    /// <para>菜单：<b>Clover/CS16/生成流程场景与面板</b>。</para>
    ///
    /// <para>
    /// 为什么预制体由代码产出：本工程禁止手写 <c>.prefab</c>/<c>.unity</c> 的 YAML
    /// （fileID / GUID 靠手写必错，且运行中的编辑器看不到）。布局定义放在面板类的
    /// <see cref="CsPanelBase.BuildLayout"/> 里，生成器与运行期兜底共用同一份。
    /// </para>
    ///
    /// <para>注意：生成过程会切换当前打开的场景（结束时停在 <c>Boot.unity</c>，可直接 Play）。</para>
    /// </summary>
    public static class FlowSetup
    {
        private const string LogTag = "[FlowSetup]";
        private const string ScenesDir = "Assets/Scenes";
        private const string UiDir = "Assets/Resources/UI";
        private const string BootScenePath = ScenesDir + "/" + SceneNames.Boot + ".unity";
        private const string MenuScenePath = ScenesDir + "/" + SceneNames.Menu + ".unity";

        private const float UiReferenceWidth = 1920f;
        private const float UiReferenceHeight = 1080f;

        /// <summary>UI 素材目录（原版 TGA 的工程内副本都在这里）。</summary>
        private const string MenuArtDir = "Assets/Resources/UI/Art";

        /// <summary>主菜单字标用的两张原版 TGA：常态（金）/ 悬停（白）。</summary>
        private static readonly string[] MenuLogoNames = { "game_menu.tga", "game_menu_mouseover.tga" };

        /// <summary>原版菜单背景拼图的工程内目录（12 张原版 TGA 被逐字节复制到这里）。</summary>
        private const string BackgroundArtDir = "Assets/Resources/Background";

        /// <summary>
        /// 原版背景拼图的 12 个文件名，**逐行对应**
        /// `原版资源/cs16src/cs16game/app/valve/resource/backgroundlayout.txt`（:3-16）。
        /// 摆放坐标 / 尺寸表在运行期组件 <c>CsMenuBackground</c>（那边管布局），这里只负责把 sprite 绑进预制体。
        /// </summary>
        private static readonly string[] BackgroundTileNames =
        {
            "800_1_a_loading.tga", "800_1_b_loading.tga", "800_1_c_loading.tga", "800_1_d_loading.tga",
            "800_2_a_loading.tga", "800_2_b_loading.tga", "800_2_c_loading.tga", "800_2_d_loading.tga",
            "800_3_a_loading.tga", "800_3_b_loading.tga", "800_3_c_loading.tga", "800_3_d_loading.tga",
        };

        /// <summary>要生成的 8 个面板（名字必须 = <c>Resources/UI/</c> 下的预制体名 = 面板类名）。</summary>
        private static readonly string[] PanelNames =
        {
            nameof(BootPanel),
            nameof(MainMenuPanel),
            nameof(ServerListPanel),
            nameof(NewGamePanel),
            nameof(OptionsPanel),
            nameof(LoadingPanel),
            nameof(TeamSelectPanel),
            nameof(PausePanel),
        };

        [MenuItem("Clover/CS16/生成流程场景与面板", false, 10)]
        public static void Generate()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError($"{LogTag} 正在 Play 模式：生成会切换场景，请先停止 Play 再执行");
                return;
            }

            // 无人值守安全：**不许**弹「是否保存当前场景？」对话框 —— 原生模态框会阻塞编辑器主线程，
            // 把 Pipeline 连同后续所有命令一起卡死（skill P-5）。这里静默处理 + 只打日志。
            if (!Application.isBatchMode)
            {
                var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (activeScene.isDirty)
                {
                    Debug.LogWarning($"{LogTag} 当前场景有未保存修改，生成会丢弃它们（不做保存询问，以免卡死自动化驱动）");
                }
            }

            try
            {
                EnsureFolder("Assets/Resources");
                EnsureFolder(ScenesDir);
                EnsureFolder(UiDir);

                GeneratePrefabs();
                BuildBootScene();
                BuildMenuScene();
                RegisterScenesInBuildSettings();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // 停在 Boot 场景：用户回来直接点 Play 就能走完整启动链路
                EditorSceneManager.OpenScene(BootScenePath, OpenSceneMode.Single);

                Debug.Log($"{LogTag} 全部完成：{BootScenePath} + {MenuScenePath} + {UiDir}/*.prefab（8 个）。" +
                          $"当前已打开 Boot 场景，可直接 Play。");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogTag} 生成中止（异常）：{ex}");
            }
        }

        /// <summary>
        /// 命令行入口：
        /// <c>unity run &lt;project&gt; -- -executeMethod Cs16.EditorTools.FlowSetup.GenerateFromCommandLine</c>。
        /// </summary>
        public static void GenerateFromCommandLine() => Generate();

        /// <summary>只读自检：确认两个场景与 8 个预制体都在磁盘上（不改任何东西）。</summary>
        [MenuItem("Clover/CS16/校验流程产出（只读）", false, 11)]
        public static void Verify()
        {
            var ok = true;

            ok &= Report(File.Exists(BootScenePath), $"场景 {BootScenePath}");
            ok &= Report(File.Exists(MenuScenePath), $"场景 {MenuScenePath}");

            for (var i = 0; i < PanelNames.Length; i++)
            {
                var path = $"{UiDir}/{PanelNames[i]}.prefab";
                ok &= Report(File.Exists(path), $"预制体 {path}");
            }

            // 原版背景拼图的 12 张 TGA（主菜单 / 读条的背景由它们拼成；缺一张就是"背景缺一块"）
            for (var i = 0; i < BackgroundTileNames.Length; i++)
            {
                var path = $"{BackgroundArtDir}/{BackgroundTileNames[i]}";
                ok &= Report(File.Exists(path), $"原版背景拼图 {path}");
            }

            var scenes = EditorBuildSettings.scenes;
            var bootIndex = IndexOfScene(scenes, BootScenePath);
            ok &= Report(bootIndex >= 0, $"Build Settings 含 {BootScenePath}");
            ok &= Report(bootIndex == 0, $"Build Settings 里 {BootScenePath} 位于索引 0（实际 {bootIndex}）");
            ok &= Report(IndexOfScene(scenes, MenuScenePath) >= 0, $"Build Settings 含 {MenuScenePath}");
            ok &= Report(IndexOfScene(scenes, ScenesDir + "/" + SceneNames.StageDust2 + ".unity") >= 0,
                $"Build Settings 含 {SceneNames.StageDust2}.unity（agent-02 的产出）");

            Debug.Log($"{LogTag} 自检结果：{(ok ? "全部通过" : "有缺失项，见上面的 Error/Warn")}");
        }

        // ═══════════════════════════════ 预制体 ═══════════════════════════════

        private static void GeneratePrefabs()
        {
            // 先在临时空场景里造节点再存成预制体：不把临时节点留在用户的场景里
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreatePanelPrefab<BootPanel>(nameof(BootPanel));
            CreatePanelPrefab<MainMenuPanel>(nameof(MainMenuPanel));
            CreatePanelPrefab<ServerListPanel>(nameof(ServerListPanel));
            CreatePanelPrefab<NewGamePanel>(nameof(NewGamePanel));
            CreatePanelPrefab<OptionsPanel>(nameof(OptionsPanel));
            CreatePanelPrefab<LoadingPanel>(nameof(LoadingPanel));
            CreatePanelPrefab<TeamSelectPanel>(nameof(TeamSelectPanel));
            CreatePanelPrefab<PausePanel>(nameof(PausePanel));
        }

        private static void CreatePanelPrefab<T>(string panelName) where T : CsPanelBase
        {
            var path = $"{UiDir}/{panelName}.prefab";

            var go = new GameObject(panelName, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            UIFactory.Stretch(rt);

            var panel = go.AddComponent<T>();
            if (panel == null)
            {
                Debug.LogError($"{LogTag} {panelName} 组件添加失败（不是 CsPanelBase 的子类？），跳过");
                Object.DestroyImmediate(go);
                return;
            }

            try
            {
                panel.BuildLayout(rt);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogTag} {panelName}.BuildLayout 抛异常（预制体可能不完整）：{ex}");
            }

            // 主菜单的原版字标：BuildLayout 只建"没有 sprite 的 Image 节点"（运行期也能建），
            // sprite 引用本身必须在编辑器里绑（运行期没有 AssetDatabase）—— 绑完再存预制体，
            // 于是预制体自带 sprite 引用。
            if (panel is MainMenuPanel menu)
            {
                menu.BindMenuSprites(EnsureMenuSprite(MenuLogoNames[0]), EnsureMenuSprite(MenuLogoNames[1]));
            }

            // 原版菜单背景拼图（主菜单 / 读条两个面板有；其余 6 个面板没有该组件 ⇒ 空转）：
            // 12 张 sprite 引用必须在**生成期**绑进预制体 —— 运行期没有 AssetDatabase。
            BindMenuBackground(go);

            var childCount = rt.childCount;

            var saved = PrefabUtility.SaveAsPrefabAsset(go, path);
            if (saved == null)
                Debug.LogError($"{LogTag} 预制体保存失败：{path}");
            else if (childCount == 0)
                Debug.LogError($"{LogTag} {path} 生成成功但没有任何子节点 —— 布局代码没跑（运行期会兜底重搭）");
            else
                Debug.Log($"{LogTag} 已生成预制体 {path}（子节点 {childCount} 个）");

            Object.DestroyImmediate(go);
        }

        /// <summary>
        /// 把主菜单字标用的原版 TGA 导入设置规整成 **Sprite/Single**（双线性、无 mipmap、alpha 透明），
        /// 并取回 sprite。
        ///
        /// <para>为什么必须做：这两张是 32 位带 alpha 的 TGA，Unity 默认按 `Default` 纹理类型导入
        /// （`textureType: 0`）⇒ <c>Image.sprite</c> / <c>Game.Res.LoadAsset&lt;Sprite&gt;</c> 都取不到，
        /// 字标就画不出来。做法与 HUD 图标那条（<c>Editor/UiGenInGame/UiBuilder.EnsureHudIconSprite</c>）一致。</para>
        ///
        /// <para>TGA 本身**不在这里生成**：它是 `原版资源` 里复制进工程的原版素材；缺失时报 Error，
        /// 不造占位图顶替。</para>
        /// </summary>
        private static Sprite EnsureMenuSprite(string fileName)
        {
            var path = MenuArtDir + "/" + fileName;
            if (!File.Exists(path))
            {
                Debug.LogError($"{LogTag} 缺主菜单字标贴图：{path} —— " +
                               "应从 原版资源/cs16src/cs16game/app/cstrike/resource/ 复制进工程（不许用占位图顶替）");
                return null;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"{LogTag} {path} 没有 TextureImporter（资源还没被 Unity 导入？）—— 字标不会显示");
                return null;
            }

            var dirty = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; dirty = true; }
            if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; dirty = true; }
            if (importer.filterMode != FilterMode.Bilinear) { importer.filterMode = FilterMode.Bilinear; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; dirty = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; dirty = true; }
            if (importer.spritePixelsPerUnit != 100f) { importer.spritePixelsPerUnit = 100f; dirty = true; }

            if (dirty)
            {
                importer.SaveAndReimport();
                Debug.Log($"{LogTag} 已规整导入设置 {path}（Sprite/Single/Bilinear/无 mipmap/alphaIsTransparency）");
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogError($"{LogTag} {path} 导入了但取不到 Sprite —— 字标不会显示");
                return null;
            }

            Debug.Log($"{LogTag} OK  主菜单字标 sprite {path}（{sprite.rect.width}×{sprite.rect.height}）");
            return sprite;
        }

        /// <summary>
        /// 把 12 块原版背景拼图的 sprite 绑进当前面板的预制体（<c>CsMenuBackground</c>）。
        /// 面板里没有该组件时什么都不做（只有主菜单与读条两个面板有）。
        /// </summary>
        private static void BindMenuBackground(GameObject go)
        {
            var background = go.GetComponentInChildren<CsMenuBackground>(true);
            if (background == null) return;

            var sprites = new Sprite[BackgroundTileNames.Length];
            var missing = 0;
            for (var i = 0; i < BackgroundTileNames.Length; i++)
            {
                sprites[i] = EnsureBackgroundSprite(BackgroundTileNames[i]);
                if (sprites[i] == null) missing++;
            }

            background.BindSprites(sprites);
            if (missing > 0)
                Debug.LogError($"{LogTag} {go.name} 的原版背景拼图缺 {missing}/{BackgroundTileNames.Length} 张" +
                               "（见上面的 Error）—— 缺的块不显示，面板会退回纯色 Backdrop");
            else
                Debug.Log($"{LogTag} OK  {go.name} 原版背景拼图 {sprites.Length} 张 sprite 已绑进预制体");
        }

        /// <summary>
        /// 把一张原版背景 TGA 规整成 **Sprite/Single**（双线性、无 mipmap、Clamp）并取回 sprite。
        ///
        /// <para>与 <see cref="EnsureMenuSprite"/> 的唯一区别：这 12 张是 **24bpp、没有 alpha 通道**的 TGA
        /// （imageType=2 / desc=0，实测每块字节数 = 18 + 宽×高×3 + 26 footer 完全对得上），
        /// 所以**不设** <c>alphaIsTransparency</c>（那是给带 alpha 的字标用的）。</para>
        ///
        /// <para>TGA 本身**不在这里生成**：它是 `原版资源` 里逐字节复制进工程的素材；缺失时报 Error，
        /// 不造占位图顶替。</para>
        /// </summary>
        private static Sprite EnsureBackgroundSprite(string fileName)
        {
            var path = BackgroundArtDir + "/" + fileName;
            if (!File.Exists(path))
            {
                Debug.LogError($"{LogTag} 缺原版背景拼图：{path} —— 应从 " +
                               "原版资源/cs16src/cs16game/app/cstrike/resource/background/ 复制进工程（不许用占位图顶替）");
                return null;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"{LogTag} {path} 没有 TextureImporter（资源还没被 Unity 导入？）—— 该块不会显示");
                return null;
            }

            var dirty = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; dirty = true; }
            if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; dirty = true; }
            if (importer.filterMode != FilterMode.Bilinear) { importer.filterMode = FilterMode.Bilinear; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; dirty = true; }
            if (importer.spritePixelsPerUnit != 100f) { importer.spritePixelsPerUnit = 100f; dirty = true; }

            if (dirty)
            {
                importer.SaveAndReimport();
                Debug.Log($"{LogTag} 已规整导入设置 {path}（Sprite/Single/Bilinear/无 mipmap）");
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogError($"{LogTag} {path} 导入了但取不到 Sprite —— 该块不会显示");
                return null;
            }

            Debug.Log($"{LogTag} OK  背景拼图 sprite {path}（{sprite.rect.width}×{sprite.rect.height}）");
            return sprite;
        }

        // ═══════════════════════════════ 场景 ═══════════════════════════════

        private static void BuildBootScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateCamera();

            var bootGo = new GameObject(nameof(Bootstrap));
            bootGo.AddComponent<Bootstrap>();

            if (EditorSceneManager.SaveScene(scene, BootScenePath))
                Debug.Log($"{LogTag} 已生成场景 {BootScenePath}（Main Camera + {nameof(Bootstrap)}）");
            else
                Debug.LogError($"{LogTag} 场景保存失败：{BootScenePath}");
        }

        private static void BuildMenuScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateCamera();

            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(UiReferenceWidth, UiReferenceHeight);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            // 这里的 EventSystem **不带 InputModule**：引擎的 EventSystem 是 CloverInput.Init 建的常驻实例
            // （见 Bootstrap.OnSceneLoaded，场景里这份加载后会被收掉）。
            // 不挂 InputModule 就不会与常驻实例抢输入（两份带模块的 EventSystem 会让点击双发）。
            new GameObject("EventSystem", typeof(EventSystem));

            if (EditorSceneManager.SaveScene(scene, MenuScenePath))
                Debug.Log($"{LogTag} 已生成场景 {MenuScenePath}（Main Camera + Canvas + EventSystem）");
            else
                Debug.LogError($"{LogTag} 场景保存失败：{MenuScenePath}");
        }

        private static Camera CreateCamera()
        {
            var go = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            go.tag = "MainCamera";

            var cam = go.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = CsUiStyle.Backdrop;
            cam.fieldOfView = CsConst.DefaultFov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 1000f;
            go.transform.position = new Vector3(0f, 0f, -10f);

            return cam;
        }

        // ═══════════════════════════════ Build Settings ═══════════════════════════════

        private static void RegisterScenesInBuildSettings()
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            list.RemoveAll(s => s.path == BootScenePath || s.path == MenuScenePath);

            // Boot 必须在索引 0（首场景）；Menu 紧随其后；其它 agent 的场景原样保留在后面
            list.Insert(0, new EditorBuildSettingsScene(MenuScenePath, true));
            list.Insert(0, new EditorBuildSettingsScene(BootScenePath, true));

            EditorBuildSettings.scenes = list.ToArray();

            Debug.Log($"{LogTag} Build Settings 已更新：0={BootScenePath}，1={MenuScenePath}，其它保留 {list.Count - 2} 个");

            if (IndexOfScene(EditorBuildSettings.scenes, ScenesDir + "/" + SceneNames.StageDust2 + ".unity") < 0)
                Debug.Log($"{LogTag} 提示：{SceneNames.StageDust2}.unity 还没进 Build Settings（agent-02 生成后再加）。" +
                          $"未加入时进图会失败并打 Warn 日志。");
        }

        // ═══════════════════════════════ 小工具 ═══════════════════════════════

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) parent = parent.Replace('\\', '/');
            var leaf = Path.GetFileName(path);

            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf))
            {
                Debug.LogError($"{LogTag} 目录路径非法：{path}");
                return;
            }

            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
            Debug.Log($"{LogTag} 创建目录 {path}");
        }

        private static int IndexOfScene(EditorBuildSettingsScene[] scenes, string path)
        {
            if (scenes == null) return -1;
            for (var i = 0; i < scenes.Length; i++)
            {
                if (scenes[i] != null && scenes[i].path == path) return i;
            }
            return -1;
        }

        private static bool Report(bool ok, string what)
        {
            if (ok)
            {
                Debug.Log($"{LogTag} OK  {what}");
                return true;
            }
            Debug.LogError($"{LogTag} 缺失 {what}");
            return false;
        }
    }
}
