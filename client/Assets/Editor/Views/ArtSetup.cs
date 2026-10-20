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
    /// Assets/Resources/Art/world_{武器id}.prefab
    /// </code>
    ///
    /// <para><b>角色预制体的结构（运行期 <see cref="ActorView"/> 依赖它）</b>：</para>
    /// <code>
    /// player (root；挂 ActorView + Animator)
    ///   └─ Body（承载整体缩放/贴地）
    ///        ├─ Skeleton
    ///        │    └─ Bip01 → …（原版骨骼层级；命中区胶囊挂在对应骨骼下，跟着动画走）
    ///        │         └─ Hitbox_{Head|Chest|Stomach|LeftLeg|RightLeg}（CapsuleCollider + CsHitboxProxy）
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

            // 贴图兜底查找的缓存跨执行会陈旧（本轮可能新增了 PNG）：每轮生成前作废。
            _prefixedTexCache = null;

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
                EnsureFolder(ArtDir + "/T");
                EnsureFolder(ArtDir + "/CT");

                ok &= FixTextureImporters(msg);

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

                // ---- 世界模型（躺在地上的枪：原版 w_*.mdl，无骨骼的静态网格）----
                for (var i = 0; i < assets.Count; i++)
                {
                    var a = assets[i];
                    if (!a.Key.StartsWith("w_")) continue;
                    var weapon = a.Key.Substring(2);
                    // 路径不带阵营子目录：原版世界模型两边共用一份（CsViewTuning.WorldModelPrefix）
                    if (AnimSetup.BuildStaticModelPrefab(
                            a, CsViewTuning.WorldModelPrefix + weapon, materials, msg)) _prefabCount++;
                    else ok = false;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                // 生成后自检：本次生成出来的每块网格都必须仍是"双面展开"态（顶点/索引 = 2×源），
                // 否则 `Art/Mesh` 会出现单面网格（背面缺面）。见 VerifyDoubleSided 的类注释。
                DoubleSidedReport(false, out var dsOk);
                ok &= dsOk;

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
        /// <summary>
        /// **只重建网格资产**（<c>Art/Mesh/*.asset</c> **就地覆盖**，GUID 与预制体引用都不变），
        /// 不动材质 / 动画 / 预制体。
        ///
        /// <para>用途：改了"网格怎么建"的口径后（例如 <c>AnimSetup.ExpandDoubleSided</c> 的
        /// 双面副本外移量），不必跑整棵资产树（<c>Art</c> 下 4000+ 文件、会顺带重写预制体），
        /// 只把网格刷一遍就能验证。可被 <c>unity command run_script</c> 直接调用（public static）。</para>
        /// </summary>
        [MenuItem("Clover/CS16/只重建网格（就地覆盖）", false, 33)]
        public static void RebuildMeshesOnly()
        {
            var n = AnimSetup.RebuildMeshAssetsFromModelData(ModelDataDir, out var report);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"{LogTag} 只重建网格：{n} 块\n{report}");
        }

        /// <summary>
        /// **只重建 9 个角色预制体的命中区节点**（就地覆盖预制体），不重写骨骼 / 网格 / 材质 / 动画。
        /// 改了 <c>.cs16anim</c> 的胶囊段后跑它，比 <see cref="Generate"/> 小得多
        /// （后者会连带重写 <c>Art</c> 下的网格与全部预制体）。
        /// 命令行入口：<c>unity run … -- -executeMethod Cs16.EditorTools.ArtSetup.RebuildHitboxesOnly</c>。
        /// </summary>
        [MenuItem("Clover/CS16/只重建角色命中盒（就地覆盖）", false, 34)]
        public static void RebuildHitboxesOnly()
        {
            var ok = true;
            var made = 0;
            for (var i = 0; i < PlayerTargets.Length; i++)
            {
                var key = PlayerTargets[i][0];
                var prefab = $"Assets/Resources/Art/{PlayerTargets[i][1]}.prefab";
                var a = Cs16AnimReader.Read($"{ModelDataDir}/{key}.cs16anim", out var err);
                if (a == null)
                {
                    Debug.LogError($"{LogTag} 读不了动画数据：{err}");
                    ok = false;
                    continue;
                }

                var n = AnimSetup.RebuildHitboxNodes(a, prefab, out var report);
                if (n < 0)
                {
                    Debug.LogError($"{LogTag} {report}");
                    ok = false;
                    continue;
                }
                made += n;
                Debug.Log($"{LogTag} ✓ {report}");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"{LogTag} 命中区重建：共 {made} 个节点；" + (ok ? "全部通过。" : "**有失败项，见上面的 Error**。"));
        }

        /// <summary>
        /// **只读**：逐块检查 <c>Art/Mesh/*.asset</c> 是不是"双面展开"态 —— 顶点数 = <c>2×</c> 源顶点、
        /// 索引数 = <c>2×3×</c> 源三角形（<see cref="AnimSetup.ExpandDoubleSided"/> 的口径）。
        ///
        /// <para><b>为什么必须有这个闸门</b>：<c>ExpandDoubleSided</c> 是"几何双面"（顶点与三角形各翻倍），
        /// 是绕开 MDL tristrip 绕序不可靠 + Unity 内置 Standard 没有 <c>_Cull</c> 属性这两条的唯一办法。
        /// 一旦某次生成/重建把它丢了，网格会**静默**变成单面（画面上是从背面看不见）—— 差异登记 #202
        /// 记的就是"重跑生成链会把 <c>w_*</c> 砍掉一半"这件事（本入口把那条地雷变成红灯）。
        /// 它只读：不写任何资产、不碰算法、不重跑生成器。</para>
        ///
        /// <para>命令行入口：<c>unity command run_script … --entry Cs16.EditorTools.ArtSetup.VerifyDoubleSidedReport</c>
        /// （返回逐块判决文本，便于直接落判据）。</para>
        /// </summary>
        [MenuItem("Clover/CS16/校验网格双面展开（只读）", false, 35)]
        public static void VerifyDoubleSided()
        {
            var report = DoubleSidedReport(true, out var allOk);
            Debug.Log($"{LogTag} 双面展开自检（只读）\n{report}");
            Debug.Log($"{LogTag} 双面展开自检结果：{(allOk ? "全部通过。" : "**有异常，见上面的 Error**。")}");
        }

        /// <summary>只读自检的命令行/脚本入口：返回逐块判决文本（首行 <c>PASS</c>/<c>FAIL</c>）。</summary>
        public static string VerifyDoubleSidedReport()
        {
            var report = DoubleSidedReport(false, out var allOk);
            return (allOk ? "PASS\n" : "FAIL\n") + report;
        }

        /// <summary>
        /// 逐块比对 <c>ModelData/*.cs16anim</c> 的源顶点/三角形数与 <c>Art/Mesh/{key}_Skin{s}.asset</c>
        /// 的实际值（期望 <c>顶点 = 2×源顶点</c>、<c>索引 = 2×3×源三角形</c>）。
        /// 异常一律 <c>Error</c> 点名（块名 + 实测/期望）；<paramref name="verbose"/> 为真时连 OK 的块也逐条打日志。
        /// </summary>
        private static string DoubleSidedReport(bool verbose, out bool allOk)
        {
            var lines = new List<string>();
            var blocks = 0;
            var bad = 0;
            var missing = 0;
            var skipped = 0;
            var files = Directory.Exists(ModelDataDir)
                ? Directory.GetFiles(ModelDataDir, "*.cs16anim") : new string[0];
            for (var f = 0; f < files.Length; f++)
            {
                var file = files[f].Replace('\\', '/');
                var a = Cs16AnimReader.Read(file, out var err);
                if (a == null)
                {
                    Debug.LogError($"{LogTag} 双面展开自检读不了动画数据：{err}");
                    bad++;
                    lines.Add($"! {file} 读不了：{err}");
                    continue;
                }

                // 只查**真正接进工程的那批**（角色的 9 个 key 由 PlayerTargets 定义；vm_*/w_* 全部接）：
                // 留在 ModelData 里但没接的遗留件（如 player_T_orig）本来就没有对应网格资产，
                // 报它"缺网格"会把闸门刷成假红 —— 显式跳过并列出，不藏。
                if (!WiredKey(a.Key))
                {
                    skipped++;
                    lines.Add($"SKIP {a.Key}（未接入 PlayerTargets 的遗留模型数据，无对应网格资产）");
                    continue;
                }

                for (var s = 0; s < a.Subs.Count; s++)
                {
                    var sub = a.Subs[s];
                    // 与生成器的跳块条件逐字一致（VertCount/TriCount 为 0 的块本来就不建资产）
                    if (sub.VertCount == 0 || sub.TriCount == 0) continue;

                    var path = $"{MeshDir}/{a.Key}_{AnimSetup.SkinName}{s}.asset";
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (mesh == null)
                    {
                        missing++;
                        Debug.LogError($"{LogTag} ** 双面展开自检：缺网格 ** {path}（源自 {file}）");
                        lines.Add($"! 缺网格 {path}");
                        continue;
                    }

                    blocks++;
                    var wantV = sub.VertCount * 2;
                    var wantI = sub.TriCount * 3 * 2;
                    var gotV = mesh.vertexCount;
                    var gotI = mesh.subMeshCount > 0 ? mesh.GetIndexCount(0) : 0;
                    if (gotV == wantV && gotI == wantI)
                    {
                        lines.Add($"OK   {path}  顶点 {gotV} = 2×{sub.VertCount}，索引 {gotI} = 2×{sub.TriCount * 3}");
                        if (verbose)
                        {
                            Debug.Log($"{LogTag} ✓ {a.Key}_Skin{s} 顶点 {gotV} = 2×{sub.VertCount}、" +
                                      $"索引 {gotI} = 2×{sub.TriCount * 3}");
                        }
                    }
                    else
                    {
                        bad++;
                        Debug.LogError($"{LogTag} ** 双面展开缺失/异常 ** {path}：实测 顶点 {gotV}" +
                                       $"（期望 {wantV} = 2×{sub.VertCount}）、索引 {gotI}" +
                                       $"（期望 {wantI} = 2×{sub.TriCount * 3}）" +
                                       " ⇒ 该块是单面或被砍半，请查 AnimSetup.ExpandDoubleSided / FillSkinMesh");
                        lines.Add($"FAIL {path}  顶点 {gotV}/{wantV}、索引 {gotI}/{wantI}");
                    }
                }
            }

            allOk = bad == 0 && missing == 0;
            lines.Add($"合计：块 {blocks}、异常 {bad}、缺网格 {missing}、跳过 {skipped}（未接的遗留模型数据）" +
                      $" ⇒ {(allOk ? "全部通过" : "**有异常**")}");
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>
        /// 该 <c>ModelData/*.cs16anim</c> 的 key 是否**接进了工程**：
        /// 角色（<c>player*</c>）以 <see cref="PlayerTargets"/> 为准（那里没列的 = 遗留件，生成器不消费）；
        /// 其余前缀（<c>vm_*</c> / <c>w_*</c>）由生成器按文件前缀全量消费 ⇒ 一律视为已接。
        /// </summary>
        private static bool WiredKey(string key)
        {
            if (!key.StartsWith("player")) return true;
            for (var i = 0; i < PlayerTargets.Length; i++)
            {
                if (PlayerTargets[i][0] == key) return true;
            }
            return false;
        }

        [MenuItem("Clover/CS16/校验视图与音效资源（只读）", false, 31)]
        public static void Verify()
        {
            var ok = true;

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

            // 每份世界模型数据都要有对应预制体（不要求每把武器都有：原版只有部分武器带 w_*.mdl）
            var worldData = Directory.Exists(ModelDataDir)
                ? Directory.GetFiles(ModelDataDir, "w_*.cs16anim") : new string[0];
            for (var i = 0; i < worldData.Length; i++)
            {
                var weapon = Path.GetFileNameWithoutExtension(worldData[i]).Substring(2);
                var p = $"Assets/Resources/Art/{CsViewTuning.WorldModelPrefix}{weapon}.prefab";
                ok &= Report(File.Exists(p), p);
            }

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

                var dirty = false;
                if (importer.textureType != TextureImporterType.Default) { importer.textureType = TextureImporterType.Default; dirty = true; }
                // 出处：GoldSrc 硬件渲染默认**双线性 + mip**（原版侧无出处文件 ⇒ 见 对照表 BLOCKED-W1，
                //   按"最接近不做处理的硬件默认"取 Bilinear）；`textureCompression = Uncompressed` 与
                //   `maxTextureSize = 2048 ≥ 源尺寸最大边 256` ⇒ 不缩放（W-01）。
                if (importer.filterMode != FilterMode.Bilinear) { importer.filterMode = FilterMode.Bilinear; dirty = true; }
                if (!importer.mipmapEnabled) { importer.mipmapEnabled = true; dirty = true; }
                if (importer.wrapMode != TextureWrapMode.Repeat) { importer.wrapMode = TextureWrapMode.Repeat; dirty = true; }
                if (importer.maxTextureSize != 2048) { importer.maxTextureSize = 2048; dirty = true; }
                if (importer.textureCompression != TextureImporterCompression.Uncompressed) { importer.textureCompression = TextureImporterCompression.Uncompressed; dirty = true; }
                if (importer.anisoLevel != 1) { importer.anisoLevel = 1; dirty = true; }

                if (!dirty) continue;
                importer.SaveAndReimport();
                changed++;
            }

            msg.Add($"✓ 贴图导入设置已规范化 {changed} / {files.Length} 张（{TexDir}）");
            return true;
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
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            var created = mat == null;
            if (created) mat = new Material(shader) { name = texName };
            else if (mat.shader != shader) mat.shader = shader;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{texName}.png");
            // 裸名找不到时兜底扫目录：导出的 PNG 带模型前缀（player_T_arctic_ARTIC_Working1.png），
            // 且大小写不保证与材质名一致（player_CT_sas_sas_dmbase1 vs SAS_DMBASE1）⇒ 忽略大小写按
            // 「同名 / 前缀_材质名」找，同名多份（Chrome1）任取其一。不兜底就会生成一批 _MainTex
            // 为空的材质 —— 运行期表现为"部分人物模型是白色的"。
            if (tex == null) tex = FindPrefixedTexture(texName);
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
                    // 从 Cutout 改回不透明时也要**显式复位**，否则会留着上一次设置的关键字/队列
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

        /// <summary>
        /// 裸名贴图缺失时的兜底查找：扫 <c>TexDir</c> 找「同名 / 任意前缀_材质名」的 PNG
        /// （忽略大小写 —— 导出文件名与材质名大小写不保证一致）。同名多份任取其一。
        /// 结果（含"确实没有"）按材质名缓存，避免逐材质重复扫目录。
        /// </summary>
        private static Texture2D FindPrefixedTexture(string texName)
        {
            if (!Directory.Exists(TexDir)) return null;

            if (_prefixedTexCache == null)
                _prefixedTexCache = new Dictionary<string, Texture2D>(System.StringComparer.Ordinal);
            if (_prefixedTexCache.TryGetValue(texName, out var cached)) return cached;

            Texture2D found = null;
            var suffix = "_" + texName.ToLowerInvariant();
            var files = Directory.GetFiles(TexDir, "*.png");
            for (var i = 0; i < files.Length; i++)
            {
                var stem = Path.GetFileNameWithoutExtension(files[i]);
                var lower = stem.ToLowerInvariant();
                if (lower == texName.ToLowerInvariant() || lower.EndsWith(suffix))
                {
                    found = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{stem}.png");
                    if (found != null) break;
                }
            }

            _prefixedTexCache[texName] = found;
            return found;
        }

        /// <summary><see cref="FindPrefixedTexture"/> 的按材质名结果缓存。</summary>
        private static Dictionary<string, Texture2D> _prefixedTexCache;

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
