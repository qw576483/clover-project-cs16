using System.Collections.Generic;
using System.IO;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.View;
using UnityEditor;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 把 CS 1.6 的模型/贴图规整成 Unity 资源，并生成**运行期真正加载的那批预制体**。
    ///
    /// <para><b>输入</b>：<c>Assets/Editor/Views/ModelData/*.cs16anim</c>（+ <c>Assets/Resources/Art/Tex/*.png</c>）
    /// —— 由 <c>原版资源/cs16src/cs16_anim.py</c> 从 CS 1.6 原始 <c>cstrike/models/**/*.mdl</c>
    /// 解析（GoldSrc MDL v10：骨骼表 + 单骨骼蒙皮网格 + 逐帧动画）而来。</para>
    ///
    /// <para><b>本文件只做"贴图/材质/名牌 + 调度"</b>；骨骼层级、SkinnedMeshRenderer、
    /// AnimationClip、AnimatorController 由 <see cref="AnimSetup"/> 生成（那里有格式细节）。
    /// 之前那套"静态分块 mesh（MeshFilter + MeshRenderer）"的生成器已被替换 ——
    /// 它的产物是"某个姿态冻住的一张网格"，无法播放原版动画（见资源欠缺清单第 13/14 项）。</para>
    ///
    /// <para><b>输出</b>（命名规范写死在 <see cref="CsViewTuning"/> 与生成脚本里，**替换素材 = 换文件**）：</para>
    /// <code>
    /// Assets/Resources/Art/Mat/{贴图名}.mat
    /// Assets/Resources/Art/Mesh/{模型key}_Skin{序号}.asset               蒙皮网格
    /// Assets/Resources/Art/Anim/{模型key}.controller                     动画状态机
    /// Assets/Resources/Art/Anim/{模型key}_{状态}.anim                    每条动画
    /// Assets/Resources/Art/T/{player,leet,arctic,guerilla}.prefab
    /// Assets/Resources/Art/CT/{player,gsg9,sas,gign,vip}.prefab
    /// Assets/Resources/Art/{T,CT}/viewmodel_{武器id}.prefab
    /// Assets/Resources/UI/WorldNameplate.prefab
    /// </code>
    ///
    /// <para><b>角色预制体的结构（运行期 <see cref="ActorView"/> 依赖它）</b>：</para>
    /// <code>
    /// player (root；挂 ActorView + Animator)
    ///   └─ Body（承载整体缩放/贴地）
    ///        ├─ Skeleton
    ///        │    └─ Bip01 → …（原版骨骼层级；命中区胶囊挂在对应骨骼下，跟着动画走）
    ///        │         └─ Hitbox_{Head|Chest|Stomach|Leg}（CapsuleCollider + CsHitboxProxy）
    ///        └─ Skin0..n（SkinnedMeshRenderer，按贴图分块）
    /// </code>
    ///
    /// <para>菜单：<b>Clover/CS16/整理视图与音效资源</b>；只读自检：<b>Clover/CS16/校验视图与音效资源（只读）</b>。
    /// 命令行入口：<see cref="GenerateFromCommandLine"/>。</para>
    /// </summary>
    public static class ArtSetup
    {
        private const string LogTag = "[ArtSetup]";

        private const string ModelDataDir = "Assets/Editor/Views/ModelData";
        private const string ArtDir = "Assets/Resources/Art";
        private const string TexDir = ArtDir + "/Tex";
        private const string MatDir = ArtDir + "/Mat";
        private const string MeshDir = ArtDir + "/Mesh";
        private const string UiDir = "Assets/Resources/UI";

        /// <summary>1×1 白 sprite（UI Image 必须有 sprite，见 <see cref="NameplatePrefabBuilder"/>）。</summary>
        private const string WhiteTextureName = "ui_white";

        /// <summary>角色模型 key → (阵营子目录, 预制体名)。</summary>
        private static readonly string[][] PlayerTargets =
        {
            new[] { "player_T", "T/player" },
            new[] { "player_T_leet", "T/leet" },
            new[] { "player_T_arctic", "T/arctic" },
            new[] { "player_T_guerilla", "T/guerilla" },
            new[] { "player_CT", "CT/player" },
            new[] { "player_CT_gsg9", "CT/gsg9" },
            new[] { "player_CT_sas", "CT/sas" },
            new[] { "player_CT_gign", "CT/gign" },
            new[] { "player_CT_vip", "CT/vip" },
        };

        // ==================================================================
        //  菜单入口
        // ==================================================================
        [MenuItem("Clover/CS16/整理视图与音效资源", false, 30)]
        public static void Generate()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError($"{LogTag} 正在 Play 模式：请先停止 Play 再执行（生成会写资源并刷新导入）");
                return;
            }

            var ok = true;
            var msg = new List<string>();

            try
            {
                AssetDatabase.Refresh();       // 先把 Python 新写出来的 png/bin 收进来

                // 物理层是"角色/武器/世界按层走射线"的前置（CsConst.PhysicsLayers 的注释说
                // "由 Editor 脚本保证"）—— 顺带在这里保证，免得层缺了却没人发现（见 PhysicsLayerSetup 的类注释）。
                ok &= PhysicsLayerSetup.EnsureAll();

                EnsureFolder("Assets/Resources");
                EnsureFolder(ArtDir);
                EnsureFolder(TexDir);
                EnsureFolder(MatDir);
                EnsureFolder(MeshDir);
                EnsureFolder(AnimSetup.AnimDir);
                EnsureFolder(UiDir);
                EnsureFolder(ArtDir + "/T");
                EnsureFolder(ArtDir + "/CT");

                ok &= FixTextureImporters(msg);
                var white = EnsureWhiteSprite(msg);
                ok &= white != null;

                var files = Directory.Exists(ModelDataDir)
                    ? Directory.GetFiles(ModelDataDir, "*.cs16anim")
                    : new string[0];

                if (files.Length == 0)
                {
                    Debug.LogError($"{LogTag} {ModelDataDir} 下没有任何 .cs16anim —— " +
                                   "请先跑 原版资源/cs16src/cs16_anim.py（从 CS 1.6 原始 mdl 导出骨骼动画）");
                    ok = false;
                }

                var assets = new List<Cs16AnimAsset>();
                for (var i = 0; i < files.Length; i++)
                {
                    var a = Cs16AnimReader.Read(files[i].Replace('\\', '/'), out var err);
                    if (a == null)
                    {
                        Debug.LogError($"{LogTag} 读不了动画数据：{err}");
                        ok = false;
                        continue;
                    }
                    assets.Add(a);
                }

                // 贴图用法 → 材质（cutout 与否由 mdl 的贴图 flags 决定）
                var texMap = AnimSetup.CollectTextures(assets);
                var materials = new Dictionary<string, Material>();
                foreach (var kv in texMap)
                {
                    var mat = EnsureMaterial(kv.Key, kv.Value, msg);
                    if (mat == null) ok = false;
                    else materials[kv.Key] = mat;
                }

                _prefabCount = 0;

                // ---- 角色 ----
                for (var i = 0; i < PlayerTargets.Length; i++)
                {
                    var key = PlayerTargets[i][0];
                    var rel = PlayerTargets[i][1];
                    var a = Find(assets, key);
                    if (a == null)
                    {
                        Debug.LogError($"{LogTag} 缺少动画数据 {key}.cs16anim（对应 Art/{rel}.prefab 不会生成）");
                        ok = false;
                        continue;
                    }
                    if (AnimSetup.BuildPlayerPrefab(a, rel, materials, msg)) _prefabCount++;
                    else ok = false;
                }

                // ---- 武器视模型（两份：T / CT 各一套，路径与 CsViewTuning 的约定一致）----
                for (var i = 0; i < assets.Count; i++)
                {
                    var a = assets[i];
                    if (!a.Key.StartsWith("vm_")) continue;
                    var weapon = a.Key.Substring(3);
                    var okT = AnimSetup.BuildViewModelPrefab(
                        a, CsViewTuning.TeamFolderT + "/" + CsViewTuning.ViewModelPrefix + weapon, materials, msg);
                    var okC = AnimSetup.BuildViewModelPrefab(
                        a, CsViewTuning.TeamFolderCT + "/" + CsViewTuning.ViewModelPrefix + weapon, materials, msg);
                    if (okT && okC) _prefabCount += 2;
                    else ok = false;
                }

                // ---- 头顶名牌 ----
                if (white != null)
                {
                    if (NameplatePrefabBuilder.Build(white, out var plateMsg))
                    {
                        _prefabCount++;
                        msg.Add($"✓ {plateMsg}");
                    }
                    else
                    {
                        Debug.LogError($"{LogTag} 名牌预制体生成失败：{plateMsg}");
                        ok = false;
                    }
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                for (var i = 0; i < msg.Count; i++) Debug.Log($"{LogTag} {msg[i]}");
                Debug.Log($"{LogTag} 完成：预制体 {_prefabCount} 个；材质 {materials.Count} 个；" +
                          $"动画数据 {assets.Count} 份（骨骼蒙皮网格 + AnimationClip + AnimatorController）。" +
                          (ok ? "全部通过。" : "**有失败项，见上面的 Error**。"));
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogTag} 生成中止（异常）：{ex}");
            }
        }

        /// <summary>命令行入口（<c>unity run … -- -executeMethod Cs16.EditorTools.ArtSetup.GenerateFromCommandLine</c>）。</summary>
        public static void GenerateFromCommandLine() => Generate();

        // ==================================================================
        //  只读自检
        // ==================================================================
        [MenuItem("Clover/CS16/校验视图与音效资源（只读）", false, 31)]
        public static void Verify()
        {
            var ok = true;
            ok &= Report(File.Exists(NameplatePrefabBuilder.PrefabAssetPath),
                NameplatePrefabBuilder.PrefabAssetPath);

            for (var i = 0; i < PlayerTargets.Length; i++)
            {
                var key = PlayerTargets[i][0];
                var p = $"Assets/Resources/Art/{PlayerTargets[i][1]}.prefab";
                ok &= Report(File.Exists(p), p);
                // 角色预制体必须同时有动画状态机，否则"模型在但不播动画"
                ok &= Report(File.Exists($"{AnimSetup.AnimDir}/{key}.controller"), $"{AnimSetup.AnimDir}/{key}.controller");
            }

            // 每把武器 **两个阵营** 都要有视模型 + 状态机
            var modelData = Directory.Exists(ModelDataDir)
                ? Directory.GetFiles(ModelDataDir, "vm_*.cs16anim") : new string[0];
            for (var i = 0; i < modelData.Length; i++)
            {
                var key = Path.GetFileNameWithoutExtension(modelData[i]);
                var weapon = key.Substring(3);
                for (var t = 0; t < 2; t++)
                {
                    var folder = t == 0 ? CsViewTuning.TeamFolderT : CsViewTuning.TeamFolderCT;
                    var p = $"Assets/Resources/Art/{folder}/{CsViewTuning.ViewModelPrefix}{weapon}.prefab";
                    ok &= Report(File.Exists(p), p);
                }
                ok &= Report(File.Exists($"{AnimSetup.AnimDir}/{key}.controller"),
                    $"{AnimSetup.AnimDir}/{key}.controller");
            }

            // 音效：agent-04 用到的枪声/换弹 + 本模块用到的那些
            var sfxDir = "Assets/Resources/Sound/SFX/sfx";
            var required = new List<string>
            {
                "step", "step2", "step3", "step4", "land", "hitmarker", "hitmarker_head",
                "hit_flesh", "hit_kevlar", "death", "round_start", "win_t", "win_ct",
                "bomb_beep", "bomb_plant", "bomb_defuse", "bomb_explode", "grenade_explode",
            };
            foreach (var w in CsWeapons.All)
            {
                // 装备（护甲/拆弹器）不开火也不换弹 —— 它们只是"买得到"的道具，没有对应音效。
                if (w.Class == CsWeaponClass.Equipment) continue;
                required.Add(w.Id + "_fire");
                required.Add(w.Id + "_reload");
            }
            for (var i = 0; i < required.Count; i++)
            {
                ok &= Report(File.Exists($"{sfxDir}/{required[i]}.wav"), $"{sfxDir}/{required[i]}.wav");
            }

            Debug.Log($"{LogTag} 自检结果：{(ok ? "全部通过" : "有缺失项，见上面的 Error")}");
        }

        // ==================================================================
        //  贴图 / 材质
        // ==================================================================
        private static bool FixTextureImporters(List<string> msg)
        {
            if (!Directory.Exists(TexDir)) return false;
            var files = Directory.GetFiles(TexDir, "*.png");
            var changed = 0;
            for (var i = 0; i < files.Length; i++)
            {
                var path = files[i].Replace('\\', '/');
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null)
                {
                    Debug.LogWarning($"{LogTag} {path} 没有 TextureImporter（导入中？）—— 本轮跳过");
                    continue;
                }

                var isWhite = Path.GetFileNameWithoutExtension(path) == WhiteTextureName;
                var dirty = false;
                var wantType = isWhite ? TextureImporterType.Sprite : TextureImporterType.Default;
                if (importer.textureType != wantType) { importer.textureType = wantType; dirty = true; }
                if (isWhite)
                {
                    if (importer.spriteImportMode != SpriteImportMode.Single) { importer.spriteImportMode = SpriteImportMode.Single; dirty = true; }
                    if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
                }
                else
                {
                    // 可平铺（GoldSrc 的 UV 常常超出 0..1）+ 不做有损压缩 + 不缩放（片W 定案，见 策划/对照表.md §W）
                    // 出处：GoldSrc 硬件渲染默认**双线性 + mip**（原版侧无出处文件 ⇒ 见 对照表 BLOCKED-W1，
                    //   按"最接近不做处理的硬件默认"取 Bilinear）；`textureCompression = Uncompressed` 与
                    //   片W 已落盘的 242 张一致（BC1 有损编码实测 maxΔ ≈19%，见 W-03/W-04）；
                    //   `maxTextureSize = 2048 ≥ 源尺寸最大边 256` ⇒ 不缩放（W-01）。
                    // ⚠️ 改本行 = 改生成器语义：⛔ 不许再写回 Point / 512（片W 之前就是被这里回退的）。
                    if (importer.filterMode != FilterMode.Bilinear) { importer.filterMode = FilterMode.Bilinear; dirty = true; }
                    if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; dirty = true; }
                    if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
                    if (importer.maxTextureSize != 2048) { importer.maxTextureSize = 2048; dirty = true; }
                    if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
                    if (importer.anisoLevel != 1) { importer.anisoLevel = 1; dirty = true; }
                }

                if (!dirty) continue;
                importer.SaveAndReimport();
                changed++;
            }

            msg.Add($"✓ 贴图导入设置已规范化 {changed} / {files.Length} 张（{TexDir}）");
            return true;
        }

        private static Sprite EnsureWhiteSprite(List<string> msg)
        {
            var texPath = $"{TexDir}/{WhiteTextureName}.png";
            if (!File.Exists(texPath))
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var px = new Color32[16];
                for (var i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(px);
                tex.Apply();
                File.WriteAllBytes(texPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(texPath);
                Debug.Log($"{LogTag} 生成 1×1 白 sprite 源文件 {texPath}");
            }

            var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
            if (importer != null && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.filterMode = FilterMode.Point;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(texPath);
            if (sprite == null)
            {
                Debug.LogError($"{LogTag} 取不到白 sprite：{texPath}（Image 没有 sprite 会不渲染）");
                msg.Add($"✗ 白 sprite 缺失：{texPath}");
                return null;
            }
            msg.Add($"✓ 白 sprite 就绪：{texPath}");
            return sprite;
        }

        private static Material EnsureMaterial(string texName, bool masked, List<string> msg)
        {
            var matPath = $"{MatDir}/{texName}.mat";

            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null)
            {
                Debug.LogError($"{LogTag} 找不到可用 shader（Standard / Legacy Shaders/Diffuse 都没有）—— 材质无法创建");
                return null;
            }

            // **已存在也要重写一遍**（不能早退复用）：材质参数由本生成器决定，早退会让
            // "改了生成器却没生效"变成静默失败（贴图 / 切图模式 / 高光都可能这么被漏掉）。
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            var created = mat == null;
            if (created) mat = new Material(shader) { name = texName };
            else if (mat.shader != shader) mat.shader = shader;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{texName}.png");
            if (tex != null) mat.mainTexture = tex;
            else
            {
                Debug.LogWarning($"{LogTag} 材质 {texName} 的贴图 {TexDir}/{texName}.png 不存在 —— 会显示纯色");
                msg.Add($"! 材质 {texName} 缺贴图");
            }
            mat.color = Color.white;

            if (mat.shader.name == "Standard")
            {
                mat.SetFloat("_Glossiness", 0.06f);
                mat.SetFloat("_Metallic", 0f);

                // 注意：**Standard 着色器没有 `_Cull` 材质属性**（实测生出来的材质 YAML 里只有
                // _Mode/_SrcBlend/_DstBlend/_ZWrite，没有 _Cull），`SetInt("_Cull", Off)` 是静默无效的。
                // "模型绕序不看方向都能看见"这件事改在**网格侧**做 —— 见 AnimSetup.ExpandDoubleSided。

                if (masked)
                {
                    mat.SetFloat("_Mode", 1f);   // Cutout
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    mat.SetInt("_ZWrite", 1);
                    mat.EnableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
                    mat.SetFloat("_Cutoff", 0.5f);
                }
                else
                {
                    // 从 Cutout 改回不透明时也要**显式复位**，否则会留着上一版的关键字/队列
                    mat.SetFloat("_Mode", 0f);   // Opaque
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                    mat.SetInt("_ZWrite", 1);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.DisableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = -1;        // 用 shader 自己的队列
                }
            }

            if (created) AssetDatabase.CreateAsset(mat, matPath);
            else EditorUtility.SetDirty(mat);
            return mat;
        }

        // ==================================================================
        //  小工具
        // ==================================================================
        private static int _prefabCount;

        private static Cs16AnimAsset Find(List<Cs16AnimAsset> all, string key)
        {
            for (var i = 0; i < all.Count; i++)
            {
                if (all[i].Key == key) return all[i];
            }
            return null;
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) parent = parent.Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
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
