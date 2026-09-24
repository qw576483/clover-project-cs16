using System.IO;
using CloverEngine;
using Cs16.Core;
using Cs16.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 一键生成**游戏内 8 个面板**的预制体到 <c>Assets/Resources/UI/</c>。
    ///
    /// <para>
    /// 为什么由代码产出：本工程禁止手写 <c>.prefab</c> 的 YAML（fileID / GUID 手写必错，
    /// 且运行中的编辑器看不到改动）。布局定义放在各面板的 <see cref="CsPanelBase.BuildLayout"/> 里，
    /// 生成器与运行期兜底共用同一份 —— 编辑器里看到的和跑起来看到的是同一个东西。
    /// </para>
    ///
    /// <para><b>预制体名 = 面板类名</b>（<c>Game.UI.Open&lt;T&gt;</c> 按 <c>Resources/UI/{类名}</c> 加载），
    /// 名字写错的表现是"面板打不开且只有一行 Error 日志"，所以这里的名字全部用
    /// <c>nameof</c> 取，不写字符串字面量。</para>
    ///
    /// <para><b>与 agent-01 的 FlowSetup 的关系</b>：两者互不重叠（他做 Boot/MainMenu/...8 个流程面板，
    /// 本脚本只做游戏内 8 个），都往同一个 <c>Assets/Resources/UI/</c> 目录写，但各自只碰自己的文件。</para>
    ///
    /// <para>菜单：<b>Clover/CS16/生成游戏内面板</b>；只读自检：<b>Clover/CS16/校验游戏内面板（只读）</b>。</para>
    /// </summary>
    public static class UiBuilder
    {
        private const string LogTag = "[UiBuilder]";
        private const string UiDir = "Assets/Resources/UI";

        /// <summary>
        /// HUD 的 **sprite 素材**（PNG）落盘路径。**只有一个来源、一处定义**：
        /// 由 <see cref="ResPaths.HudStopwatchIcon"/>（= <c>UI/Art/stopwatch</c>，运行期加载用的路径）
        /// 拼出工程内的 <c>Assets/Resources/…png</c>，避免"生成器写一个路径、运行期读另一个路径"。
        /// </summary>
        private static readonly string HudIconDir = "Assets/Resources/UI/Art";
        private static string HudIconPngPath(string resourcePath) => $"Assets/Resources/{resourcePath}.png";

        private static readonly string[] PanelNames =
        {
            nameof(HudPanel),
            nameof(BuyMenuPanel),
            nameof(HMenuPanel),
            nameof(ScoreboardPanel),
            nameof(RoundEndPanel),
            nameof(MatchEndPanel),
            nameof(RadioMenuPanel),
            nameof(ConsolePanel),
        };

        [MenuItem("Clover/CS16/生成游戏内面板", false, 20)]
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
                EnsureFolder(UiDir);
                EnsureFolder(HudIconDir);

                // HUD 的 sprite 素材（原版精灵解出的 PNG）：导入设置必须先规整成 Sprite，
                // 否则运行期 Game.Res.LoadAsset<Sprite> 拿不到东西（PNG 默认导入是 Texture，不是 Sprite）。
                var stopwatchSprite = EnsureHudIconSprite(ResPaths.HudStopwatchIcon);
                if (stopwatchSprite == null)
                {
                    Debug.LogError($"{LogTag} 缺 HUD 秒表图标 sprite：生成出来的 HudPanel 会没有该图标" +
                                   "（原因见上面那条 Error；**不许用占位图顶替**）");
                }

                // 血量 / 护甲图标（原版 640hud7.spr 的 cross / suit_full / suithelmet_full，
                // hud.txt:121/135/137；见 CsHudTheme 的"HUD 图标"一节）—— 同样是"解出原版像素再复制进工程"。
                // 它**不是**从原版 spr/bmp 解出的（那两份载体本机不在盘），而是由原版地图几何
                // 离线俯视栅格化生成 —— 生成命令写在下面的 hint 里；导入设置仍必须是 Sprite，
                // 否则运行期 `Game.Res.LoadAsset<Sprite>` 取不到、雷达只有点没有地图。
                var radarSprite = EnsureHudIconSprite(ResPaths.RadarOverviewDust2,
                    "python tools/probes/render-overview.py --size 128 " +
                    "--out client/Assets/Resources/UI/Art/overview_de_dust2.png");
                if (radarSprite == null)
                {
                    Debug.LogError($"{LogTag} 缺雷达底图 sprite：雷达会只有框和点、没有地图" +
                                   "（原因见上面那条 Error；**不许用占位图顶替**）");
                }

                var healthIconSprite = EnsureHudIconSprite(ResPaths.HudHealthIcon);
                var armorIconSprite = EnsureHudIconSprite(ResPaths.HudArmorIcon);
                var armorHelmetIconSprite = EnsureHudIconSprite(ResPaths.HudArmorHelmetIcon);
                if (healthIconSprite == null || armorIconSprite == null || armorHelmetIconSprite == null)
                {
                    Debug.LogError($"{LogTag} 缺 HUD 血量/护甲图标 sprite：HudPanel 会少那两张位图图标" +
                                   "（原因见上面那些 Error；**不许用占位图顶替**）");
                }

                // 在临时空场景里造节点再存成预制体：不把临时节点留在用户的场景里
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

                CreatePanelPrefab<HudPanel>(nameof(HudPanel), stopwatchSprite,
                    healthIconSprite, armorIconSprite, armorHelmetIconSprite);
                CreatePanelPrefab<BuyMenuPanel>(nameof(BuyMenuPanel));
                CreatePanelPrefab<HMenuPanel>(nameof(HMenuPanel));
                CreatePanelPrefab<ScoreboardPanel>(nameof(ScoreboardPanel));
                CreatePanelPrefab<RoundEndPanel>(nameof(RoundEndPanel));
                CreatePanelPrefab<MatchEndPanel>(nameof(MatchEndPanel));
                CreatePanelPrefab<RadioMenuPanel>(nameof(RadioMenuPanel));
                CreatePanelPrefab<ConsolePanel>(nameof(ConsolePanel));

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"{LogTag} 完成：{UiDir}/ 下 8 个游戏内面板预制体已生成/刷新" +
                          "（HudPanel / BuyMenuPanel / HMenuPanel / ScoreboardPanel / " +
                          "RoundEndPanel / MatchEndPanel / RadioMenuPanel / ConsolePanel）" +
                          (stopwatchSprite != null ? "；HUD 秒表图标已绑入 HudPanel" : "；**HUD 秒表图标缺失**"));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogTag} 生成中止（异常）：{ex}");
            }
        }

        /// <summary>命令行入口（供 unity run -- -executeMethod …GenerateFromCommandLine 用）。</summary>
        public static void GenerateFromCommandLine() => Generate();

        /// <summary>只读自检：确认 8 个预制体都在磁盘上（不改任何东西）。</summary>
        [MenuItem("Clover/CS16/校验游戏内面板（只读）", false, 21)]
        public static void Verify()
        {
            var ok = true;
            for (var i = 0; i < PanelNames.Length; i++)
            {
                var path = $"{UiDir}/{PanelNames[i]}.prefab";
                ok &= Report(File.Exists(path), $"预制体 {path}");
            }

            // HUD 的 sprite 素材：PNG 必须在，且必须已经导入成 Sprite（否则运行期拿不到图标）
            var hudIcons = new[]
            {
                ResPaths.HudStopwatchIcon,
                ResPaths.HudHealthIcon,
                ResPaths.HudArmorIcon,
                ResPaths.HudArmorHelmetIcon,
                ResPaths.RadarOverviewDust2,
            };
            for (var i = 0; i < hudIcons.Length; i++)
            {
                var png = HudIconPngPath(hudIcons[i]);
                ok &= Report(File.Exists(png), $"HUD 图标 PNG {png}");
                var icon = AssetDatabase.LoadAssetAtPath<Sprite>(png);
                ok &= Report(icon != null,
                    $"HUD 图标已导入成 Sprite {png}（{icon?.rect.width}×{icon?.rect.height}）");
            }

            Debug.Log($"{LogTag} 自检结果：{(ok ? "全部通过" : "有缺失项，见上面的 Error")}");
        }

        /// <summary>
        /// 把 HUD 用的 PNG 导入设置规整成 **Sprite/Single**（点采样、无 mipmap、alpha 透明），并取回 sprite。
        /// 原版那种 24×24 的图标必须是 Sprite 才能被 <c>Image</c> 用 / 被 <c>Game.Res.LoadAsset&lt;Sprite&gt;</c> 取到。
        /// <para>PNG 本身**不在这里生成**：它是 `原版资源` 里解出的原版素材副本
        /// （<c>python 原版资源/解包产物/cs16_asset_extract.py</c> 产出并复制进工程），
        /// 本方法只负责"导入设置 + 取引用" —— 素材缺失时报 Error 而不是造一个占位图。</para>
        /// </summary>
        private static Sprite EnsureHudIconSprite(string resourcePath, string hint = null)
        {
            var path = HudIconPngPath(resourcePath);
            if (!File.Exists(path))
            {
                Debug.LogError($"{LogTag} 缺 HUD 图标 PNG：{path} —— " + (hint ??
                               "请先在项目根跑 `python 原版资源/解包产物/cs16_asset_extract.py`") +
                               "（不许用占位图顶替）");
                return null;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"{LogTag} {path} 没有 TextureImporter（资源还没被 Unity 导入？）—— 图标不会显示");
                return null;
            }

            var dirty = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; dirty = true; }
            if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; dirty = true; }
            if (importer.filterMode != FilterMode.Point) { importer.filterMode = FilterMode.Point; dirty = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; dirty = true; }
            if (importer.spritePixelsPerUnit != 100f) { importer.spritePixelsPerUnit = 100f; dirty = true; }

            if (dirty)
            {
                importer.SaveAndReimport();
                Debug.Log($"{LogTag} 已规整导入设置 {path}（Sprite/Single/Point/无 mipmap/alphaIsTransparency）");
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                Debug.LogError($"{LogTag} {path} 导入了但取不到 Sprite —— 图标不会显示");
                return null;
            }

            Debug.Log($"{LogTag} OK  HUD 图标 sprite {path}（{sprite.rect.width}×{sprite.rect.height}）");
            return sprite;
        }

        private static void CreatePanelPrefab<T>(string panelName, Sprite hudSprite = null,
            Sprite healthIcon = null, Sprite armorIcon = null, Sprite armorHelmetIcon = null)
            where T : CsPanelBase
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

            // HUD 的 sprite 图标：BuildLayout 只建"没有 sprite 的 Image 节点"（运行期也能建），
            // sprite 引用本身必须在编辑器里绑（运行期没有 AssetDatabase）—— 绑完再存预制体，
            // 于是预制体自带 sprite 引用，运行期连同 Resources/UI/HudPanel.prefab 一起被带走。
            if (panel is HudPanel hud)
            {
                if (hudSprite != null)
                {
                    hud.BindStopwatchSprite(hudSprite);
                    Debug.Log($"{LogTag} {panelName} 已绑定秒表图标 sprite（{ResPaths.HudStopwatchIcon}）");
                }
                hud.BindHudIconSprites(healthIcon, armorIcon, armorHelmetIcon);
            }

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
