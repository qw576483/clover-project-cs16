using System.Collections.Generic;
using System.IO;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.View;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 从 <c>.cs16anim</c>（CS 1.6 原始 mdl 的骨骼/蒙皮/逐帧动画数据）生成运行期资产：
    /// <list type="number">
    /// <item><b>骨骼层级</b>：每根 mdl 骨骼一个 Transform（局部平移/旋转 = 原版绑定姿态值）；</item>
    /// <item><b>SkinnedMeshRenderer</b>：网格顶点 = 绑定姿态世界坐标、单骨骼权重 1.0
    ///       （GoldSrc <c>vertinfoindex</c> 就是单骨骼刚性蒙皮）、<c>bindposes</c> = 绑定世界矩阵的逆；</item>
    /// <item><b>AnimationClip</b>：每个状态一条，逐骨骼 keyframe（位置 + 四元数）；
    ///       帧率/帧数/是否循环**一律取 mdl 实测**（见 cs16_anim.py 的导出日志）；</item>
    /// <item><b>AnimatorController</b>：一条 clip = 一个 state；**state 名 = 去前缀的 mdl 序列名**
    ///       （与运行期 <c>CsViewTuning</c> 的候选逐字一致，见 <see cref="BuildController"/> 的注释）；</item>
    /// <item><b>命中区胶囊</b>：5 段（头/胸/腹/左腿/右腿），挂在对应骨骼下（跟随动画）。</item>
    /// </list>
    ///
    /// <para><b>为什么用引擎的 <c>Game.Anim</c></b>：运行期 <c>ActorView</c> / <c>ViewModelRig</c>
    /// 通过 <c>Game.Anim.CreateAnimator(go, controller)</c> 拿 <c>IAnimPlayer</c> 来切状态；
    /// 生成器只负责把 <c>RuntimeAnimatorController</c> 直接写进预制体（无异步加载竞态）。</para>
    ///
    /// <para><b>幂等</b>：clip / controller / mesh 都是"本轮全量重建"（先删后建），
    /// 重复执行结果一致，不会残留旧资产。</para>
    /// </summary>
    internal static class AnimSetup
    {
        internal const string AnimDir = "Assets/Resources/Art/Anim";
        internal const string MeshDir = "Assets/Resources/Art/Mesh";

        /// <summary>骨骼层级的挂载节点名（在 <c>Body</c> 下；视模型直接挂在根下）。</summary>
        internal const string SkeletonName = "Skeleton";

        /// <summary>每根骨骼的网格节点的名字前缀。</summary>
        internal const string SkinName = "Skin";

        /// <summary>
        /// **按 9 路 blend 出动画**的序列前缀（9 路 = 3×3 瞄准方向格；索引 <c>b = pitchIdx*3 + yawIdx</c>）：
        /// <c>ref_aim_*</c>（站姿瞄准）、<c>crouch_aim_*</c>（蹲姿瞄准）。
        /// </summary>
        private static readonly string[] BlendSeqPrefixes = { "ref_aim_", "crouch_aim_" };

        /// <summary>
        /// **按 9 路出 BlendTree 的射击序列类后缀**：站姿 <c>ref_shoot_&lt;类&gt;</c> 与
        /// 蹲姿 <c>crouch_shoot_&lt;类&gt;</c> 各一条。类名同 <c>CsViewTuning.PlayerAnimClass</c>。
        ///
        /// <para>只覆盖常用的四类：全部 15 类都 9 路化会再写 1 GB 以上的剪辑，而这四类之外
        /// （霰弹 / 机枪 / 刀 / 手雷 / C4 / 盾）的射击姿势由整身一次性动作表达，差异落在手上而不是上身躯干。</para>
        /// </summary>
        private static readonly string[] BlendShootClasses = { "rifle", "onehanded", "mp5", "carbine" };

        /// <summary>出 BlendTree 的路数 = 3×3 网格（<see cref="CsAimBlend.GridN"/>）。</summary>
        private const int BlendGridCount = CsAimBlend.GridN * CsAimBlend.GridN;

        /// <summary>失败日志里的阶段名（生成被中断时按它定位是哪一步留下的半套资产）。</summary>
        private const string StageController = "[阶段:controller]";
        private const string StageUpper = "[阶段:upper]";
        private const string StageClip = "[阶段:clip]";

        /// <summary>
        /// 上身通道所在的层号 —— 运行期 <c>ActorView.DriveUpperChannel</c> 按**层号 1**访问它
        /// （Base Layer 必须留在 0 层：运行期按 0 层切状态，见 <c>ActorView.ResolveState</c>）。
        /// </summary>
        private const int UpperLayerIndex = 1;

        /// <summary>上身层的层名（与运行期 <c>ActorView</c> 的层号口径配套）。</summary>
        internal const string UpperLayerName = "upper";

        /// <summary>
        /// 上身层的 AvatarMask 资产路径。9 个皮肤（<c>player_T</c> / <c>player_T_leet</c> / … /
        /// <c>player_CT_vip</c>）的骨骼层级同名（<c>Body/Skeleton/Bip01/…</c>）⇒ 共用同一份。
        /// </summary>
        internal const string UpperMaskPath = AnimDir + "/player_upper.mask";

        /// <summary>
        /// 上身层覆盖的骨骼**末段名**（14 项）：脊柱链 + 颈/头 + 双臂。⛔ 不含**根**（<c>Bip01</c>）与
        /// **骨盆**（<c>Bip01 Pelvis</c>）、也不含双腿 —— 它们的位移姿势由 layer0 的
        /// <c>walk</c> / <c>run</c> / <c>crouchrun</c> / <c>jump</c> 提供。
        ///
        /// <para><b>为什么必须分上下半身</b>：原版是双通道 —— 腿走 <c>gaitsequence</c>、上身走 aim 序列
        /// （<c>原版资源/cs16src/regamedll/dlls/player.cpp:2875-2929</c>）。而 mdl 里只有 <c>walk</c> 序列的臂
        /// 是绑定姿态（<c>terror.mdl</c> 帧 0 的 <c>Bip01 L UpperArm</c> 欧拉角 ≈ 0，见
        /// <c>.ai-tmp/test/walk-arm-check.py</c> 的直读结果）⇒ 单通道整身播 <c>walk</c> 就会把绑定臂
        /// 露出来（双臂平伸）。上路用 aim 序列补上，layer0 一行不动。</para>
        /// </summary>
        private static readonly string[] UpperBoneNames =
        {
            "Bip01 Spine", "Bip01 Spine1", "Bip01 Spine2", "Bip01 Spine3",
            "Bip01 Neck", "Bip01 Head",
            "Bip01 L Clavicle", "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 L Hand",
            "Bip01 R Clavicle", "Bip01 R UpperArm", "Bip01 R Forearm", "Bip01 R Hand",
        };

        // ==================================================================
        //  收集贴图用法（决定材质要不要走 cutout）
        // ==================================================================
        /// <summary>扫过所有 anim 资产，返回 贴图名 → 是否带 mask（flags &amp; 0x40）。</summary>
        internal static Dictionary<string, bool> CollectTextures(List<Cs16AnimAsset> assets)
        {
            var map = new Dictionary<string, bool>();
            for (var i = 0; i < assets.Count; i++)
            {
                var subs = assets[i].Subs;
                for (var s = 0; s < subs.Count; s++)
                {
                    var n = Path.GetFileNameWithoutExtension(subs[s].Tex);
                    var masked = (subs[s].Flags & 0x0040) != 0;
                    if (map.TryGetValue(n, out var old)) map[n] = old || masked;
                    else map[n] = masked;
                }
            }
            return map;
        }

        // ==================================================================
        //  角色预制体
        // ==================================================================
        /// <summary>
        /// 生成一个角色预制体：<c>root(ActorView+Animator) → Body → Skeleton → 骨骼…</c>，
        /// 命中区胶囊挂在 <c>Bip01 Head/Spine…</c> 下，蒙皮网格在 <c>Body</c> 下。
        /// </summary>
        internal static bool BuildPlayerPrefab(Cs16AnimAsset a, string rel,
            Dictionary<string, Material> mats, List<string> msg)
        {
            var root = new GameObject(Path.GetFileName(rel));
            var body = new GameObject(CsViewTuning.BodyNodeName);
            body.transform.SetParent(root.transform, false);

            var bones = BuildSkeleton(body.transform, a);
            var meshCount = AttachSkin(body.transform, a, bones, mats, msg);

            // 命中区：胶囊挂到对应骨骼下（跟随动画）
            AttachHitboxes(a, bones);

            root.AddComponent<ActorView>();
            AttachAnimator(root, a);

            var path = $"Assets/Resources/Art/{rel}.prefab";
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            if (saved == null)
            {
                Debug.LogError($"[AnimSetup] 角色预制体保存失败：{path}");
                return false;
            }
            msg.Add($"✓ {path}（骨骼 {a.Bones.Count}、网格 {meshCount}、命中区 {a.Caps.Count}、" +
                    $"动画 {a.Clips.Count} 条）");
            return true;
        }

        // ==================================================================
        //  视模型预制体
        // ==================================================================
        /// <summary>生成一个第一人称武器预制体（骨骼 + 蒙皮 + Animator，**不加任何碰撞体**）。</summary>
        internal static bool BuildViewModelPrefab(Cs16AnimAsset a, string rel,
            Dictionary<string, Material> mats, List<string> msg)
        {
            var root = new GameObject(Path.GetFileName(rel));
            var bones = BuildSkeleton(root.transform, a);
            var meshCount = AttachSkin(root.transform, a, bones, mats, msg);
            if (meshCount == 0)
            {
                Debug.LogError($"[AnimSetup] {rel} 没有任何网格，预制体不生成");
                Object.DestroyImmediate(root);
                return false;
            }
            AttachAnimator(root, a);

            var path = $"Assets/Resources/Art/{rel}.prefab";
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            if (saved == null)
            {
                Debug.LogError($"[AnimSetup] 视模型预制体保存失败：{path}");
                return false;
            }
            msg.Add($"✓ {path}（网格 {meshCount}、骨骼 {a.Bones.Count}、动画 {a.Clips.Count} 条）");
            return true;
        }

        // ==================================================================
        //  静态模型预制体
        // ==================================================================
        /// <summary>
        /// 生成一个**无骨骼**的静态预制体（<c>MeshFilter + MeshRenderer</c>，不加任何碰撞体）。
        ///
        /// <para>世界掉落物用的是原版 <c>models/w_*.mdl</c>，它们在 GoldSrc 里就是单骨骼静态网格
        /// （本工程的 <c>.cs16anim</c> 里骨骼表为空、顶点已是绑定姿态世界坐标）⇒ 不需要
        /// <c>SkinnedMeshRenderer</c>，直接当静态网格画。</para>
        /// </summary>
        internal static bool BuildStaticModelPrefab(Cs16AnimAsset a, string rel,
            Dictionary<string, Material> mats, List<string> msg)
        {
            var root = new GameObject(Path.GetFileName(rel));
            var count = 0;
            for (var s = 0; s < a.Subs.Count; s++)
            {
                var sub = a.Subs[s];
                if (sub.VertCount == 0 || sub.TriCount == 0) continue;

                var go = new GameObject($"{SkinName}{s}");
                go.transform.SetParent(root.transform, false);

                var mesh = BuildSkinMesh(a.Key, s, sub, null);
                EnsureFolder(MeshDir);
                var meshPath = $"{MeshDir}/{a.Key}_{SkinName}{s}.asset";
                // 幂等：网格内容完全由生成器决定，复用旧 .asset 会让"改了生成器却没生效"
                if (File.Exists(meshPath)) AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(mesh, meshPath);

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
                ApplyMaterial(mr, a, sub, mats, msg);
                count++;
            }

            if (count == 0)
            {
                Debug.LogError($"[AnimSetup] {rel} 没有任何网格，预制体不生成");
                Object.DestroyImmediate(root);
                return false;
            }

            var path = $"Assets/Resources/Art/{rel}.prefab";
            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var saved = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            if (saved == null)
            {
                Debug.LogError($"[AnimSetup] 世界模型预制体保存失败：{path}");
                return false;
            }
            msg.Add($"✓ {path}（静态网格 {count} 块）");
            return true;
        }

        // ==================================================================
        //  骨骼 / 蒙皮
        // ==================================================================
        /// <summary>按 mdl 的骨骼表建 Transform 层级（父骨骼的下标一定小于子骨骼 ⇒ 一趟到底）。</summary>
        private static Transform[] BuildSkeleton(Transform parent, Cs16AnimAsset a)
        {
            var holder = new GameObject(SkeletonName);
            holder.transform.SetParent(parent, false);
            var arr = new Transform[a.Bones.Count];
            for (var i = 0; i < a.Bones.Count; i++)
            {
                var b = a.Bones[i];
                var go = new GameObject(b.Name);
                var t = go.transform;
                t.SetParent(b.Parent >= 0 && b.Parent < i ? arr[b.Parent] : holder.transform, false);
                t.localPosition = new Vector3(b.Px, b.Py, b.Pz);
                t.localRotation = new Quaternion(b.Qx, b.Qy, b.Qz, b.Qw);
                t.localScale = Vector3.one;
                arr[i] = t;
            }
            return arr;
        }

        /// <summary>
        /// 每个贴图分块一个 <c>SkinnedMeshRenderer</c>：顶点已在绑定姿态世界坐标，
        /// 权重 = 单骨骼 1.0（GoldSrc <c>vertinfoindex</c>），<c>bindposes</c> = 绑定世界矩阵的逆。
        /// </summary>
        private static int AttachSkin(Transform parent, Cs16AnimAsset a, Transform[] bones,
            Dictionary<string, Material> mats, List<string> msg)
        {
            var bindWorld = a.BindWorld();
            var bindposes = new Matrix4x4[a.Bones.Count];
            for (var i = 0; i < a.Bones.Count; i++)
            {
                bindposes[i] = ToMatrix(bindWorld[i]).inverse;
            }

            var count = 0;
            for (var s = 0; s < a.Subs.Count; s++)
            {
                var sub = a.Subs[s];
                if (sub.VertCount == 0 || sub.TriCount == 0) continue;

                var go = new GameObject($"{SkinName}{s}");
                go.transform.SetParent(parent, false);
                go.layer = PhysicsLayers.Bot;

                var mesh = BuildSkinMesh(a.Key, s, sub, bindposes);
                if (mesh == null) continue;
                // 说明：骨骼表为空的模型（原版 w_*.mdl 这类静态网格）不走这里，见 BuildStaticModelPrefab。
                EnsureFolder(MeshDir);
                var meshPath = $"{MeshDir}/{a.Key}_{SkinName}{s}.asset";
                // 幂等：网格内容完全由生成器决定，复用旧 .asset 会让"改了生成器却没生效"
                if (File.Exists(meshPath)) AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(mesh, meshPath);

                var smr = go.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = mesh;
                smr.bones = bones;
                smr.rootBone = bones.Length > 0 ? bones[0] : null;
                //   由动画决定 —— 两者可以完全不重合。以 v_* 视模型为例（实测 `.cs16anim`）：
                //     绑定姿态 bbox  x∈[-0.9087,-0.1160]  y∈[-0.0469,+0.1763]  z∈[-0.1272,+0.1374]
                //     idle 姿态 bbox x∈[-0.0260,+0.2390]  y∈[-0.2860,-0.0620]  z∈[-0.0870,+0.7050]
                //   ⇒ 绑定姿态的包围盒把**相机原点夹在里面**（z 跨 0），而真正要画的几何全在它**右前方**。
                //   `updateWhenOffscreen=false` 时 Unity 用这个错位的包围盒同时做两件事：
                //   ① 视锥剔除 ② 蒙皮更新开关（配合 Animator 的 `CullUpdateTransforms`）——
                //   一旦判成"看不见"，骨骼就**停在绑定姿态**，而绑定姿态的顶点落在相机平面上
                //   （实测有一个顶点投影到视口 x=155，即 155 倍屏宽）⇒ 画出来就是**巨大到失真的手臂/枪**。
                //   `true` 让 Unity 按真实蒙皮结果算包围盒，两个开关都不再依赖那个错位的值。
                smr.updateWhenOffscreen = true;
                smr.localBounds = mesh.bounds;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                smr.receiveShadows = true;

                ApplyMaterial(smr, a, sub, mats, msg);
                count++;
            }
            return count;
        }

        /// <summary>按贴图名取材质挂到渲染器上；取不到只告警（该网格显示默认材质）。</summary>
        private static void ApplyMaterial(Renderer r, Cs16AnimAsset a, Cs16AnimSkin sub,
            Dictionary<string, Material> mats, List<string> msg)
        {
            var matName = Path.GetFileNameWithoutExtension(sub.Tex);
            if (mats.TryGetValue(matName, out var mat) && mat != null) r.sharedMaterial = mat;
            else
            {
                Debug.LogWarning($"[AnimSetup] {a.Key}/{matName} 找不到材质 —— 该网格会显示默认材质");
                msg.Add($"! 缺材质 {matName}");
            }
        }

        /// <summary>
        /// 建一块网格。<paramref name="bindposes"/> 为 <c>null</c> / 空 ⇒ 静态网格
        /// （不写 <c>boneWeights</c> 与 <c>bindposes</c>；见 <see cref="BuildStaticModelPrefab"/>）。
        /// </summary>
        private static Mesh BuildSkinMesh(string key, int index, Cs16AnimSkin sub, Matrix4x4[] bindposes)
        {
            var mesh = new Mesh { name = $"{key}_{SkinName}{index}" };
            FillSkinMesh(mesh, key, index, sub, bindposes);
            return mesh;
        }

        /// <summary>把一块贴图分块的网格内容写进 <paramref name="mesh"/>（新建或既有资产都走这里）。</summary>
        private static void FillSkinMesh(Mesh mesh, string key, int index, Cs16AnimSkin sub, Matrix4x4[] bindposes)
        {
            var n = sub.VertCount;
            var verts = new Vector3[n];
            var uvs = new Vector2[n];
            var norms = new Vector3[n];
            var skinned = bindposes != null && bindposes.Length > 0;
            var weights = skinned ? new BoneWeight[n] : null;
            var maxBone = skinned ? bindposes.Length - 1 : 0;
            for (var i = 0; i < n; i++)
            {
                verts[i] = new Vector3(sub.Verts[i * 3], sub.Verts[i * 3 + 1], sub.Verts[i * 3 + 2]);
                //   V 方向必须取反：`.cs16anim` 的 v 是 GoldSrc 口径（贴图**首行**为 t=0），
                //   贴图 PNG 按 mdl 原始字节序落盘（首行 = 图像上边），而 Unity 采样的 uv.y=0
                //   是图像**下边**（导入时纵向翻过一次）。不取反就会采到上下镜像的纹素。
                uvs[i] = new Vector2(sub.Uvs[i * 2], 1f - sub.Uvs[i * 2 + 1]);
                norms[i] = new Vector3(sub.Norms[i * 3], sub.Norms[i * 3 + 1], sub.Norms[i * 3 + 2]);
                if (!skinned) continue;
                var bi = Mathf.Clamp(sub.BoneIdx[i], 0, maxBone);
                weights[i] = new BoneWeight { boneIndex0 = bi, weight0 = 1f };
            }

            // 双面展开 + **副本顶点沿法线外移极小量**（见 ExpandDoubleSided）：这一步会**追加 n 个副本顶点**，
            // 所以必须在"赋给 mesh"之前做完（否则 mesh.vertices 与索引对不上）。
            var indices = ExpandDoubleSided(sub.Tris, ref verts, ref uvs, ref norms, ref weights, skinned);

            mesh.Clear();
            var total = verts.Length;
            if (total > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = norms;
            if (skinned)
            {
                mesh.boneWeights = weights;
                mesh.bindposes = bindposes;
            }
            mesh.triangles = indices;
            mesh.RecalculateBounds();
        }

        /// <summary>绑定姿态世界矩阵的逆（与 <see cref="AttachSkin"/> 同口径）。</summary>
        private static Matrix4x4[] Bindposes(Cs16AnimAsset a)
        {
            var bindWorld = a.BindWorld();
            var bindposes = new Matrix4x4[a.Bones.Count];
            for (var i = 0; i < a.Bones.Count; i++) bindposes[i] = ToMatrix(bindWorld[i]).inverse;
            return bindposes;
        }

        /// <summary>
        /// 只按当前口径重写 <c>Art/Mesh</c> 下已有的网格资产（**就地覆盖**，GUID 与预制体的引用都不变），
        /// 不碰材质 / 动画 / 预制体。网格内容与预制体生成走同一份 <see cref="FillSkinMesh"/>。
        /// 骨骼表为空、或由 <see cref="BuildStaticModelPrefab"/> 渲染的 <c>w_*</c> 世界模型
        /// ⇒ 不写 boneWeights / bindposes（静态网格不读蒙皮数据）。
        /// </summary>
        internal static int RebuildMeshAssetsFromModelData(string modelDataDir, out string report)
        {
            var sb = new List<string>();
            var files = Directory.GetFiles(modelDataDir, "*.cs16anim");
            var written = 0;
            var missing = 0;
            var skipped = 0;
            for (var f = 0; f < files.Length; f++)
            {
                var file = files[f].Replace('\\', '/');
                var a = Cs16AnimReader.Read(file, out var err);
                if (a == null)
                {
                    Debug.LogError($"[AnimSetup] 读不了动画数据：{err}");
                    continue;
                }
                //  世界模型（w_*）由 BuildStaticModelPrefab 以 MeshFilter + MeshRenderer 渲染 ⇒ 网格不带蒙皮数据。
                //  只按"骨骼表是否为空"判断不行：w_knife 的骨骼表非空，会拿到一份渲染时无人读取的 bindposes。
                var bindposes = a.Bones.Count > 0 && !a.Key.StartsWith("w_") ? Bindposes(a) : null;
                for (var s = 0; s < a.Subs.Count; s++)
                {
                    var sub = a.Subs[s];
                    if (sub.VertCount == 0 || sub.TriCount == 0) continue;
                    var path = $"{MeshDir}/{a.Key}_{SkinName}{s}.asset";
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (mesh == null)
                    {
                        missing++;
                        sb.Add($"! 缺网格 {path}（跳过）");
                        continue;
                    }
                    //  就地覆盖写不掉"要不要蒙皮"这个差异：Mesh.Clear() 清不掉资产里已有的
                    //  bindposes / boneWeights，照参数重填只会得到半新半旧的内容。所以状态对不上就
                    //  拒绝改写并点名整棵生成器（它按 DeleteAsset + CreateAsset 重出，是唯一能换
                    //  顶点通道布局的入口）。
                    var diskSkinned = mesh.bindposes != null && mesh.bindposes.Length > 0;
                    if (diskSkinned != (bindposes != null))
                    {
                        skipped++;
                        Debug.LogError($"[AnimSetup] {path} 的蒙皮状态与生成口径不符（盘上 " +
                                       $"{(diskSkinned ? "有" : "无")} bindposes、本入口要求 " +
                                       $"{(bindposes != null ? "有" : "无")}）—— 就地覆盖改不了，请跑 " +
                                       "Clover/CS16/整理视图与音效资源 重出：该入口会重建网格资产");
                        sb.Add($"! 蒙皮状态不符 {path}（跳过；请跑整棵生成器）");
                        continue;
                    }
                    FillSkinMesh(mesh, a.Key, s, sub, bindposes);
                    EditorUtility.SetDirty(mesh);
                    written++;
                }
            }
            AssetDatabase.SaveAssets();
            report = $"重写网格 {written} 块、缺 {missing} 块、跳过 {skipped} 块（键 {files.Length} 份）\n" +
                     string.Join("\n", sb.ToArray());
            return written;
        }

        /// <summary>
        /// 双面展开的副本相对正片的**外移量**（米）。
        ///
        /// <para><b>为什么必须有它（0 = 共面 = 前面那个 bug）</b>：副本与正片**同顶点、同深度**时，
        /// 深度缓冲分不出谁在前 ⇒ 哪一片赢取决于浮点精度 ⇒ **随机碎片化的破洞 + 能透过身体看到背景**
        /// （用户报的"像个沥水框，全是洞"就是它）。0.002 m 肉眼不可见，但足以让深度缓冲把两片
        /// 稳定分开（正面恒在前、副本恒在后）。</para>
        /// </summary>
        private const float DoubleSidedOffsetMeters = 0.002f;

        /// <summary>
        /// 三角形正反各一份（几何层面的双面），**副本顶点沿法线外移 <see cref="DoubleSidedOffsetMeters"/>**。
        ///
        /// <para><b>为什么是"几何展开"而不是"关背面剔除"</b>：MDL 的三角形是 tristrip 命令流
        /// （<c>hlsdk/utils/studiomdl/tristrip.c::BuildTris</c>），条带里相邻三角形绕序交替；
        /// <c>cs16_build.py</c> 按"顺序取三点"解出后绕序被拉平（实测与 MDL 自带法线的同向率只有
        /// 44%~52%、且逐对严格交替），条带边界在 <c>.cs16anim</c> 里没有保留 ⇒ 无法可靠还原绕序；
        /// 而 Unity 内置 Standard 着色器**没有 `_Cull` 材质属性**（实测）⇒ 关不掉背面剔除。</para>
        ///
        /// <para><b>代价</b>：顶点数翻倍（三角形本来就翻倍）。受光仍用 MDL 自带的外向法线。</para>
        /// </summary>
        /// <param name="tris">源三角形索引（每 3 个一个面）。</param>
        /// <param name="verts">顶点表（**会被替换成"正片 + 副本"两份**）。</param>
        /// <param name="uvs">UV 表（同上）。</param>
        /// <param name="norms">法线表（跟上，副本**沿用**原法线 —— 受光口径不变）。</param>
        /// <param name="weights">蒙皮权重表（同上；静态网格传 <c>null</c>）。</param>
        /// <param name="skinned">是否蒙皮网格（决定要不要复制权重表）。</param>
        private static int[] ExpandDoubleSided(uint[] tris, ref Vector3[] verts, ref Vector2[] uvs,
                                               ref Vector3[] norms, ref BoneWeight[] weights, bool skinned)
        {
            if (tris == null || tris.Length < 3) return new int[0];

            var n = verts.Length;
            var v2 = new Vector3[n * 2];
            var u2 = new Vector2[n * 2];
            var nr2 = new Vector3[n * 2];
            var w2 = skinned ? new BoneWeight[n * 2] : null;

            for (var i = 0; i < n; i++)
            {
                var nrm = norms[i].sqrMagnitude > 1e-8f ? norms[i].normalized : Vector3.up;
                v2[i] = verts[i];
                v2[i + n] = verts[i] + nrm * DoubleSidedOffsetMeters;   // 副本：沿法线外移
                u2[i] = uvs[i];
                u2[i + n] = uvs[i];
                nr2[i] = norms[i];
                nr2[i + n] = norms[i];                                   // 法线不偏移（受光不变）
                if (skinned)
                {
                    w2[i] = weights[i];
                    w2[i + n] = weights[i];
                }
            }

            var outIdx = new int[tris.Length * 2];
            for (int i = 0, j = 0; i + 2 < tris.Length; i += 3, j += 6)
            {
                outIdx[j] = (int)tris[i];
                outIdx[j + 1] = (int)tris[i + 1];
                outIdx[j + 2] = (int)tris[i + 2];
                outIdx[j + 3] = (int)tris[i] + n;          // 副本：用外移过的顶点，且绕序反向
                outIdx[j + 4] = (int)tris[i + 2] + n;
                outIdx[j + 5] = (int)tris[i + 1] + n;
            }

            verts = v2; uvs = u2; norms = nr2; weights = w2;
            return outIdx;
        }

        private static CsHitbox ParseZone(string zone)
        {
            switch (zone)
            {
                case "Head": return CsHitbox.Head;
                case "Chest": return CsHitbox.Chest;
                case "Stomach": return CsHitbox.Stomach;
                case "LeftLeg": return CsHitbox.LeftLeg;
                case "RightLeg": return CsHitbox.RightLeg;
                default:
                    // 区名来自 .cs16anim 的胶囊段（与 CsHitbox 的枚举名同源），对不上就是数据与代码脱节。
                    Debug.LogWarning($"[AnimSetup] 未知命中区名「{zone}」，按 Chest 处理");
                    return CsHitbox.Chest;
            }
        }

        // ==================================================================
        //  命中区节点（角色预制体；BuildPlayerPrefab 与"只重建命中区"共用这一处）
        // ==================================================================
        /// <summary>
        /// 按 <c>.cs16anim</c> 的命中区表建胶囊节点，挂在 <paramref name="bones"/> 里对应的骨骼下（跟随动画）。
        /// 节点名 = <see cref="CsViewTuning.HitboxNodePrefix"/> + 区名；每个节点带
        /// <c>CapsuleCollider</c> + <see cref="CsHitboxProxy"/>。
        ///
        /// <para><b>左右腿为什么能分列</b>：胶囊中心 = <c>(0, CenterY, CenterZ)</c> —— 侧向中心由
        /// <c>.cs16anim</c> 的 <c>centerZ</c> 给出（左右腿各挂在自己那条腿骨下），竖直中心与尺寸照旧。
        /// 其余命中区的 <c>centerZ</c> 为 0 = 仍在身体轴上。</para>
        /// </summary>
        /// <returns>建出来的命中区节点数。</returns>
        internal static int AttachHitboxes(Cs16AnimAsset a, Transform[] bones)
        {
            var capBindWorld = a.BindWorld();
            var made = 0;
            for (var i = 0; i < a.Caps.Count; i++)
            {
                var cap = a.Caps[i];
                if (cap.Bone < 0 || cap.Bone >= bones.Length) continue;
                var bt = bones[cap.Bone];
                if (bt == null) continue;

                var node = new GameObject(CsViewTuning.HitboxNodePrefix + cap.Zone);
                node.transform.SetParent(bt, false);

                // 胶囊在绑定姿态下必须**竖直**（与 Body 轴对齐）：
                // localRotation = 绑定世界旋转的逆 ⇒ 世界旋转回到 identity。
                var bindWorld = ToMatrix(capBindWorld[cap.Bone]);
                node.transform.localRotation = Quaternion.Inverse(bindWorld.rotation);
                var worldCenter = new Vector3(0f, cap.CenterY, cap.CenterZ);
                node.transform.localPosition = bindWorld.inverse.MultiplyPoint3x4(worldCenter);

                node.layer = PhysicsLayers.Bot;   // 运行期由 ActorView 按 bot/真人改成 Player
                var col = node.AddComponent<CapsuleCollider>();
                var height = Mathf.Max(cap.CapH, cap.CapR * 2.002f);   // Unity 会静默夹半径
                col.direction = 1;
                col.center = Vector3.zero;
                col.radius = Mathf.Min(cap.CapR, height * 0.5f);
                col.height = height;

                var proxy = node.AddComponent<CsHitboxProxy>();
                proxy.Hitbox = ParseZone(cap.Zone);
                proxy.ActorId = 0;
                made++;
            }
            return made;
        }

        /// <summary>
        /// **只重建一个角色预制体里的命中区节点**（就地覆盖预制体，不碰骨骼 / 蒙皮网格 / 材质 / 动画）。
        ///
        /// <para>用途：改了 <c>.cs16anim</c> 的胶囊段后，不必跑整棵资产树
        /// （<c>ArtSetup.Generate</c> 会连带重写 <c>Art</c> 下的网格与全部预制体）就能让预制体跟上。
        /// 幂等：先删掉预制体里全部 <see cref="CsHitboxProxy"/> 所在节点，再按数据重建。</para>
        /// </summary>
        /// <returns>建出来的命中区节点数；打不开预制体返回 -1。</returns>
        internal static int RebuildHitboxNodes(Cs16AnimAsset a, string prefabPath, out string report)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            if (root == null)
            {
                report = "打不开预制体 " + prefabPath;
                return -1;
            }
            try
            {
                var bones = new Transform[a.Bones.Count];
                var found = 0;
                for (var i = 0; i < a.Bones.Count; i++)
                {
                    bones[i] = FindDeep(root.transform, a.Bones[i].Name);
                    if (bones[i] != null) found++;
                }

                var removed = 0;
                var existing = root.GetComponentsInChildren<CsHitboxProxy>(true);
                for (var i = 0; i < existing.Length; i++)
                {
                    if (existing[i] == null) continue;
                    Object.DestroyImmediate(existing[i].gameObject);
                    removed++;
                }

                var made = AttachHitboxes(a, bones);
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                report = $"{prefabPath}：删 {removed} 个旧命中区、建 {made} 个（骨骼命中 {found}/{a.Bones.Count}）";
                return made;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            for (var i = 0; i < t.childCount; i++)
            {
                var r = FindDeep(t.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        // ==================================================================
        //  动画资产（AnimationClip + AnimatorController）
        // ==================================================================
        private static void AttachAnimator(GameObject root, Cs16AnimAsset a)
        {
            var controller = BuildController(a, root.transform);
            var animator = root.AddComponent<Animator>();
            animator.applyRootMotion = false;
            //   `CullUpdateTransforms` —— "看不见就不更新骨骼变换"。判"看得见"用的是渲染器**包围盒**，
            //   而 .cs16anim 的绑定姿态包围盒把相机原点夹在中间（见 AttachSkin 的注释，视模型尤其严重）：
            //   一旦被误判成不可见，骨骼就停在绑定姿态，而绑定姿态投影出来是巨大失真的手臂/枪。
            //   角色模型同理（绑定 bbox x∈[-0.1947,+0.2722] 不含 idle1 的 x∈[-0.4540,+0.2410]）。
            //   ⇒ 这两类模型都必须恒更新变换，不能让"包围盒猜可见性"决定姿态。
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (controller != null) animator.runtimeAnimatorController = controller;
            else Debug.LogError($"[AnimSetup] {a.Key} 的 AnimatorController 生成失败 —— 该模型不会播放任何动画");
        }

        /// <summary>
        /// 一条 clip = 一个 state；**state 名 = 去前缀的序列名**（= <c>clipData.Name</c>，取自 mdl 实测标签），
        /// 必须与运行期 <c>CsViewTuning.PState*</c> / <c>VmState*</c> 的候选**逐字一致**（运行期按名字切）。
        ///
        /// <para><b>为什么不能用 clip 资产名当 state 名（方案 A vs 方案 B）</b>：
        /// clip 资产路径是 <c>{模型key}_{序列名}.anim</c>，而 <c>AssetDatabase.CreateAsset</c> 会把资产对象名
        /// 改成文件名 ⇒ <c>clips[i].name</c> 拿到的是 <c>player_CT_gsg9_idle1</c> / <c>vm_glock18_idle</c> 这种
        /// **带皮肤/武器前缀**的名字。用它当 state 名时，运行期 <c>ActorView.ResolveState</c>
        /// （<c>ActorView.cs</c> 的「按候选顺序取真实存在的状态名」）与 <c>ViewModelRig.ResolveState</c>
        /// 查的是 <c>CsViewTuning</c> 里的**无前缀**候选（<c>idle1</c> / <c>run</c> / <c>walk</c> /
        /// <c>crouch_idle</c> / <c>jump</c> / <c>death1..3</c> / <c>ref_shoot_*</c> / <c>idle</c> / <c>fire1</c> /
        /// <c>draw</c> …），于是 <c>Animator.HasState</c> 恒 <c>False</c>、<c>CrossFade</c>/<c>Play</c> 从不执行，
        /// 只剩控制器的 <c>defaultState</c> 在自动循环（实测：<c>HasState("idle1")=False</c> 且
        /// <c>HasState("player_CT_gsg9_idle1")=True</c>）。</para>
        ///
        /// <para><b>本处采用方案 A（改生成器），不选方案 B（运行期按当前皮肤/武器补前缀）</b>：状态名本来就是
        /// 生成器定的，两边对齐只改一处即可；方案 B 要让运行期知道"当前模型的 key"，等于把资源命名规范
        /// （<c>{key}_{序列名}</c>）硬编码进业务代码，还得把 <c>CsViewTuning</c> 的候选表扩成"前缀 + 候选"
        /// 两段式 —— 契约与运行时代码都要动，而它要解决的问题在生成器侧本来就不存在。
        /// <c>.anim</c> **资产文件名保持不变**（仍是 <c>{key}_{序列名}</c>），只改 state 名 ⇒ 不牵动其它引用。</para>
        ///
        /// <para><b>同名冲突的确定性规则</b>：控制器内的 state 名必须唯一；而 <c>.anim</c> 文件名也由
        /// <c>{key}_{序列名}</c> 决定，真要出现重名序列，后一条连资产都写不到第二个文件上。
        /// 因此这里**保留第一条、丢弃后一条并告警**（运行期按名字查状态，同名后一条本来就查不到）。</para>
        /// </summary>
        private static AnimatorController BuildController(Cs16AnimAsset a, Transform root)
        {
            EnsureFolder(AnimDir);
            var ctrlPath = $"{AnimDir}/{a.Key}.controller";

            // 幂等：先删旧的 controller（clip 也要重建，避免"改了生成器却没生效"）
            if (File.Exists(ctrlPath)) AssetDatabase.DeleteAsset(ctrlPath);

            var paths = new string[a.Bones.Count];
            var bones = CollectBones(root, a);
            for (var i = 0; i < a.Bones.Count; i++)
                paths[i] = bones[i] == null ? null : RelativePath(root, bones[i]);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            if (controller == null)
            {
                Debug.LogError($"[AnimSetup] {a.Key} 的 controller 创建失败");
                return null;
            }

            // 两个 blend 轴参数只在真有 BlendTree state 时加 —— 没有它时运行期也不喂值
            //（ActorView 按"controller 里有没有这两个参数"决定要不要 SetFloat）。
            for (var i0 = 0; i0 < a.Clips.Count; i0++)
            {
                if (!UseBlendTree(a.Clips[i0])) continue;
                // 默认值 = 中格（0.5 / 0.5）：BlendTree 在"运行期还没喂值"的那些帧（装配当帧、
                // 参数表读不到的 Animator）必须落在**正前瞄准**那一格。默认 0 = 索引 0 的角格
                // （pitch 上 + yaw 一侧）⇒ 站定时会看到"双手举枪朝天"。
                // 默认值必须随参数一起 Add：AnimatorController.parameters 取回的是副本，
                // 改数组元素写不回 controller（盘上仍是 0）。
                AddAimParameter(controller, CsAimBlend.ParamYaw);
                AddAimParameter(controller, CsAimBlend.ParamPitch);
                break;
            }

            var stateNames = new List<string>();          // 已建 state 的名字（= 去前缀序列名）
            var usedStates = new HashSet<string>();
            var blended = 0;
            var blendTrees = 0;
            AnimatorState first = null;
            for (var c = 0; c < a.Clips.Count; c++)
            {
                var clipData = a.Clips[c];
                if (clipData.NumBlends > 1) blended++;
                if (!usedStates.Add(clipData.Name))
                {
                    Debug.LogWarning($"[AnimSetup] {a.Key} 的序列名「{clipData.Name}」重复 —— " +
                                     "只保留第一条的 state（state 名必须唯一，同名后一条运行期按名字查不到）");
                    continue;
                }

                if (UseBlendTree(clipData))
                {
                    var treeState = BuildBlendTreeState(controller, a, clipData, paths);
                    if (treeState == null) continue;
                    stateNames.Add(clipData.Name);
                    blendTrees++;
                    if (first == null) first = treeState;
                    continue;
                }

                // 注意：CreateAsset 会把 clip.name 改成文件名（带 key 前缀），
                // 所以 state 名从**序列名**另取一份，不读 clip.name。
                var clip = BuildClipAsset(a, clipData, clipData.Tracks,
                    $"{AnimDir}/{a.Key}_{clipData.Name}.anim", clipData.Name, paths);
                if (clip == null) continue;
                var single = controller.AddMotion(clip);
                single.name = clipData.Name;
                stateNames.Add(clipData.Name);
                if (first == null) first = single;
            }

            // 多路序列（瞄准 / 射击，9 路 = 3×3 瞄准方向格）在数据里带全部路数：
            // <see cref="UseBlendTree"/> 判定的那一组出 9 条 clip + 2D BlendTree（state 名不变），
            // 其余只把第一路写成一条 clip。没被消费的路显式报一条，免得"数据已导出、
            // 画面上却不随瞄准方向变"再被当成数据缺失排查。
            if (blended > 0)
                Debug.Log($"[AnimSetup] {a.Key}：{blended} 条序列带多路 blend（瞄准 / 射击）；其中 " +
                          $"{blendTrees} 条（{string.Join(" / ", BlendSeqPrefixes)}* 与 " +
                          $"ref_shoot_ / crouch_shoot_ 的 {string.Join(" / ", BlendShootClasses)} 类）" +
                          "出了 9 条 clip + 2D BlendTree，其余只写第一路（blend 0）的轨道");

            if (stateNames.Count == 0)
            {
                Debug.LogError($"[AnimSetup]{StageController} {a.Key} 没有任何可用 clip —— controller 不生成");
                return null;
            }

            // 默认状态：优先 idle（角色 / 视模型的静止状态），否则第一条
            var idleName = FindStateName(stateNames, "idle1");
            if (idleName == null) idleName = FindStateName(stateNames, "idle");
            if (idleName != null)
            {
                var states = controller.layers[0].stateMachine.states;
                for (var i = 0; i < states.Length; i++)
                {
                    if (states[i].state.name == idleName) { first = states[i].state; break; }
                }
            }
            var layers = controller.layers;
            layers[0].stateMachine.defaultState = first;
            controller.layers = layers;

            // 第 2 层「upper」：上身 aim 通道（只角色模型有 —— 视模型是第一人称的两只手，没有上下半身之分）。
            // 必须排在 layer0 的循环之后：这一层的状态**复用** layer0 刚写好的 .anim 资产，不从零写剪辑。
            var upperTrees = 0;
            if (a.Key.StartsWith("player", System.StringComparison.Ordinal))
                upperTrees = BuildUpperStates(controller, a, paths);

            // 落盘前核一遍树的条数：中途抛异常 / 被打断时磁盘上会留下半套 controller，
            // 而运行期查状态只按名字，缺树的表现是"姿势不动"而不是报错。
            var expectedTrees = blendTrees + upperTrees;
            var actualTrees = CountBlendTreeStates(controller);
            if (actualTrees != expectedTrees)
            {
                Debug.LogError($"[AnimSetup]{StageController} {a.Key} 的 BlendTree 数 {actualTrees} 与预期 " +
                               $"{expectedTrees} 不符 —— controller 可能只写成了半套（生成是否被中断？）");
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        /// <summary>
        /// 加一个瞄准轴 float 参数，默认值 = 中格 <see cref="CsAimBlend.CellCentre"/>(1)。
        /// 默认值只能随参数一起加：<c>AnimatorController.parameters</c> 返回副本，改元素写不回 controller。
        /// </summary>
        private static void AddAimParameter(AnimatorController controller, string paramName)
        {
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = paramName,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = CsAimBlend.CellCentre(1),
            });
        }

        // ==================================================================
        //  第 2 层「upper」：上身 aim 通道
        // ==================================================================
        /// <summary>
        /// 建第 2 层并把 <see cref="IsUpperBlendSequence"/> 那两组（<c>ref_aim_*</c> 站姿瞄准 /
        /// <c>crouch_aim_*</c> 蹲姿瞄准）的**同名状态**补进去：运行期在位移态把层权重开到 1、
        /// 并 CrossFade 到当前武器那条 aim 状态（见 <c>ActorView.DriveUpperChannel</c>）。
        /// 站定 / 蹲定 / 一次性动作 / 死亡时该层权重恒 0 ⇒ 那些路径与只有 layer0 时逐字相同。
        /// </summary>
        /// <returns>该层建出来的 BlendTree 状态数（用于落盘前的条数核对）。</returns>
        private static int BuildUpperStates(AnimatorController controller, Cs16AnimAsset a, string[] paths)
        {
            var sm = BuildUpperLayer(controller, a, paths);
            if (sm == null) return 0;

            var trees = 0;
            var used = new HashSet<string>();
            for (var c = 0; c < a.Clips.Count; c++)
            {
                var clipData = a.Clips[c];
                if (clipData == null || string.IsNullOrEmpty(clipData.Name)) continue;
                if (!IsUpperBlendSequence(clipData.Name)) continue;
                // 同名冲突：与 layer0 同一条规则（保留第一条、丢弃后一条）—— 两层的状态名必须逐条对上。
                if (!used.Add(clipData.Name)) continue;
                if (UseBlendTree(clipData))
                {
                    if (AddUpperBlendState(controller, a, clipData)) trees++;
                }
                else
                {
                    AddUpperSingleState(sm, a, clipData.Name);
                }
            }

            // 层必须有默认状态（否则该层不评估）。本值只决定"权重被打开、但还没 CrossFade 过"时的姿势；
            // 运行期每次进位移态都会 CrossFade 到当前武器那条。
            // 优先取站姿瞄准组：clip 顺序里 crouch_aim_* 在前，而 CreateBlendTreeInController 会把
            // 第一个状态自动设为默认 ⇒ 必须显式改回站姿那条，否则那种帧会停在蹲姿姿势上。
            var states = sm.states;
            AnimatorState def = null;
            for (var i = 0; i < states.Length; i++)
            {
                if (!states[i].state.name.StartsWith("ref_aim_", System.StringComparison.Ordinal)) continue;
                def = states[i].state;
                break;
            }
            if (def == null && states.Length > 0) def = states[0].state;
            if (def != null) sm.defaultState = def;
            return trees;
        }

        /// <summary>controller 各层里直接作为 state 的 BlendTree 条数（本生成器只建平铺树）。</summary>
        private static int CountBlendTreeStates(AnimatorController controller)
        {
            var n = 0;
            var layers = controller.layers;
            for (var l = 0; l < layers.Length; l++)
            {
                var sm = layers[l].stateMachine;
                if (sm == null) continue;
                var states = sm.states;
                for (var i = 0; i < states.Length; i++)
                {
                    if (states[i].state.motion is BlendTree) n++;
                }
            }
            return n;
        }

        /// <summary>
        /// 加第 2 层（<see cref="UpperLayerName"/>）：<c>Override</c> 混合 + <see cref="UpperMaskPath"/> 的
        /// 上身 mask + 权重 1。层与 mask 都按"已存在就复用"处理（重复执行结果一致）。
        /// </summary>
        /// <returns>该层的状态机；建不出来返回 <c>null</c>（该模型退回单通道 = 走起来露绑定臂）。</returns>
        private static AnimatorStateMachine BuildUpperLayer(AnimatorController controller, Cs16AnimAsset a, string[] paths)
        {
            var mask = EnsureUpperMask(a.Key, paths);
            if (mask == null) return null;

            // AddLayer 没有返回值（加没加上只能回读层表）⇒ 下面按名字在层表里找它。
            controller.AddLayer(UpperLayerName);

            // AddLayer 的插入位置在 Unity 各版本里没有文档保证 ⇒ 这里显式把「upper」钉在第 1 层
            //（运行期按层号 1 访问，Base Layer 必须仍是 0 层）。同层表里调换位置不影响层内内容。
            var layers = controller.layers;
            var up = -1;
            for (var i = 0; i < layers.Length; i++)
            {
                if (layers[i].name == UpperLayerName) { up = i; break; }
            }
            if (up < 0)
            {
                Debug.LogError($"[AnimSetup]{StageUpper} {a.Key} 加了层「{UpperLayerName}」却在层表里找不到 —— " +
                               "上身 aim 通道不生效");
                return null;
            }
            if (up != UpperLayerIndex)
            {
                if (layers.Length != UpperLayerIndex + 1)
                {
                    Debug.LogError($"[AnimSetup]{StageUpper} {a.Key} 的层表异常（共 {layers.Length} 层、" +
                                   $"「{UpperLayerName}」在第 {up} 层）—— 上身 aim 通道不生效（Base Layer 必须留在 0 层）");
                    return null;
                }
                var tmp = layers[UpperLayerIndex];
                layers[UpperLayerIndex] = layers[up];
                layers[up] = tmp;
                controller.layers = layers;
                layers = controller.layers;
                Debug.LogWarning($"[AnimSetup] {a.Key}：层「{UpperLayerName}」被加在第 {up} 层，" +
                                 $"已调回第 {UpperLayerIndex} 层");
            }

            var sm = layers[UpperLayerIndex].stateMachine;
            if (sm == null)
            {
                Debug.LogError($"[AnimSetup]{StageUpper} {a.Key} 的第 {UpperLayerIndex} 层没有状态机 —— " +
                               "上身 aim 通道不生效");
                return null;
            }

            layers[UpperLayerIndex].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[UpperLayerIndex].avatarMask = mask;
            layers[UpperLayerIndex].defaultWeight = 1f;
            controller.layers = layers;
            return sm;
        }

        /// <summary>
        /// 取 / 建上身层的 AvatarMask：清单 = layer0 同一份骨骼路径里**末段名**命中
        /// <see cref="UpperBoneNames"/> 的那 14 条（如 <c>Body/Skeleton/Bip01/Bip01 Pelvis/Bip01 Spine</c>）。
        /// 幂等：已有资产**就地改写**（不重建 ⇒ GUID 不变，controller 的引用不失效）。
        /// </summary>
        /// <returns>填好的 mask；一条骨骼都对不上时返回 <c>null</c>。</returns>
        private static AvatarMask EnsureUpperMask(string key, string[] paths)
        {
            var found = new List<string>(UpperBoneNames.Length);
            for (var i = 0; i < UpperBoneNames.Length; i++)
            {
                for (var b = 0; b < paths.Length; b++)
                {
                    if (paths[b] == null) continue;
                    if (Path.GetFileName(paths[b]) != UpperBoneNames[i]) continue;
                    found.Add(paths[b]);
                    break;
                }
            }

            if (found.Count != UpperBoneNames.Length)
            {
                // 非预期分支：骨骼名与清单对不上 ⇒ 该通道的覆盖面与设计不一致（缺口那些部位会露绑定姿态）。
                Debug.LogError($"[AnimSetup] {key} 的骨骼表里只找到 {found.Count}/{UpperBoneNames.Length} 根上身骨骼" +
                               $"（清单 {string.Join("/", UpperBoneNames)}）—— 上身通道覆盖不全");
            }
            if (found.Count == 0) return null;

            EnsureFolder(AnimDir);
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperMaskPath);
            var created = mask == null;
            if (created) mask = new AvatarMask();
            mask.transformCount = found.Count;
            for (var i = 0; i < found.Count; i++)
            {
                mask.SetTransformPath(i, found[i]);
                mask.SetTransformActive(i, true);
            }
            if (created) AssetDatabase.CreateAsset(mask, UpperMaskPath);
            else EditorUtility.SetDirty(mask);
            AssetDatabase.SaveAssets();
            return mask;
        }

        /// <summary>
        /// 第 2 层里补一条**单剪辑** aim 状态：motion 复用 layer0 已经写好的
        /// <c>{模型key}_{序列名}.anim</c>（⛔ 不重建剪辑资产）。
        /// </summary>
        /// <returns>状态建出来返回 <c>true</c>。</returns>
        private static bool AddUpperSingleState(AnimatorStateMachine sm, Cs16AnimAsset a, string seqName)
        {
            var clipPath = $"{AnimDir}/{a.Key}_{seqName}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                Debug.LogError($"[AnimSetup]{StageUpper} {a.Key}/{seqName} 的剪辑 {clipPath} 读不到 —— " +
                               "上身层缺这条状态（该瞄准姿势下位移会露出绑定姿态的手臂）");
                return false;
            }
            var st = sm.AddState(seqName);
            if (st == null)
            {
                Debug.LogError($"[AnimSetup]{StageUpper} {a.Key}/{seqName} 的上身层状态建不出来");
                return false;
            }
            st.motion = clip;
            return true;
        }

        /// <summary>
        /// 第 2 层里补一条 **9 路 BlendTree** aim 状态：与 layer0 同名、同两个轴参数
        /// （<see cref="CsAimBlend.ParamYaw"/> / <see cref="CsAimBlend.ParamPitch"/>），
        /// 9 路剪辑从盘上复用 layer0 写好的 <c>{模型key}_{序列名}_b{路号}.anim</c>。
        /// </summary>
        /// <returns>BlendTree 状态建出来返回 <c>true</c>。</returns>
        private static bool AddUpperBlendState(AnimatorController controller, Cs16AnimAsset a, Cs16AnimClip clipData)
        {
            // 剪辑先读齐、再建树：建树后 any AssetDatabase 操作都可能让 controller 重载、树句柄失效。
            var clips = new AnimationClip[clipData.NumBlends];
            var wrote = 0;
            for (var b = 0; b < clipData.NumBlends; b++)
            {
                var clipPath = $"{AnimDir}/{a.Key}_{clipData.Name}_b{b}.anim";
                clips[b] = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clips[b] == null)
                {
                    Debug.LogError($"[AnimSetup]{StageUpper} {a.Key}/{clipData.Name}_b{b} 的剪辑 {clipPath} 读不到 —— " +
                                   "上身层该方向格缺路（会退化到邻近格）");
                    continue;
                }
                wrote++;
            }

            var state = controller.CreateBlendTreeInController(clipData.Name, out var tree, UpperLayerIndex);
            if (state == null || tree == null)
            {
                Debug.LogError($"[AnimSetup]{StageUpper} {a.Key}/{clipData.Name} 的上身层 BlendTree 创建失败 —— " +
                               "该瞄准姿势下位移会露出绑定姿态的手臂");
                return false;
            }
            tree.blendType = BlendTreeType.FreeformCartesian2D;
            tree.blendParameter = CsAimBlend.ParamYaw;
            tree.blendParameterY = CsAimBlend.ParamPitch;

            for (var b = 0; b < clips.Length; b++)
            {
                if (clips[b] == null) continue;
                tree.AddChild(clips[b], new Vector2(
                    CsAimBlend.CellCentre(b % CsAimBlend.GridN),
                    CsAimBlend.CellCentre(b / CsAimBlend.GridN)));
            }

            if (wrote != BlendGridCount)
            {
                Debug.LogError($"[AnimSetup]{StageUpper} {a.Key}/{clipData.Name} 的上身层只写出 {wrote}/{BlendGridCount} 路" +
                               " —— 缺少的方向格会退化到邻近格");
            }
            state.name = clipData.Name;
            return true;
        }

        /// <summary>
        /// 这条序列是否按 9 路出 clip + BlendTree：名字在 <see cref="BlendSeqPrefixes"/> 的组里
        /// （或 <c>ref_shoot_</c> / <c>crouch_shoot_</c> + <see cref="BlendShootClasses"/> 之一），
        /// 且路数正好等于 3×3 网格（缺一路就不是完整格子，宁可按 1 路出，也不出一个缺角的树）。
        /// </summary>
        private static bool UseBlendTree(Cs16AnimClip clipData)
        {
            return clipData != null
                && clipData.NumBlends == BlendGridCount
                && IsBlendSequence(clipData.Name);
        }

        /// <summary>该序列名是否属于 9 路组（见 <see cref="UseBlendTree"/>）。</summary>
        private static bool IsBlendSequence(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (var i = 0; i < BlendSeqPrefixes.Length; i++)
            {
                if (name.StartsWith(BlendSeqPrefixes[i], System.StringComparison.Ordinal)) return true;
            }
            for (var i = 0; i < BlendShootClasses.Length; i++)
            {
                if (name == "ref_shoot_" + BlendShootClasses[i]) return true;
                if (name == "crouch_shoot_" + BlendShootClasses[i]) return true;
            }
            return false;
        }

        /// <summary>该序列是否进第 2 层（上身通道）：站姿瞄准组与蹲姿瞄准组。</summary>
        private static bool IsUpperBlendSequence(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("ref_aim_", System.StringComparison.Ordinal)
                || name.StartsWith("crouch_aim_", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 一条多路序列 → 一个 **2D 自由笛卡尔 BlendTree** state（state 名 = 序列名，运行期查状态的
        /// 候选名不变）：9 路各一条 clip，落在 <c>(yawIdx, pitchIdx)</c> 的格心上。
        ///
        /// <para>轴与格心由 <see cref="CsAimBlend"/> 给出（<c>AimYaw</c> / <c>AimPitch</c> ∈ [0,1]，
        /// 格心 0 / 0.5 / 1）；格内插值交给 Animator（3×3 规则格上等价双线性）。
        /// 格序走**数据自己的索引**（<c>b % 3</c> = 偏航格、<c>b / 3</c> = 俯仰格），不按姿势代理量反推。</para>
        /// </summary>
        /// <returns>建好的 state；一条曲线都没写进去时返回 <c>null</c>。</returns>
        private static AnimatorState BuildBlendTreeState(AnimatorController controller, Cs16AnimAsset a,
            Cs16AnimClip clipData, string[] paths)
        {
            // 剪辑先全部写好、再建树：CreateBlendTreeInController 返回的是 controller 的子资产，
            // 建树之后任何 AssetDatabase 删除/导入都可能让宿主 controller 重载 ⇒ 手里的树句柄失效
            // （MissingReferenceException: BlendTree has been destroyed）。
            var clips = new AnimationClip[clipData.NumBlends];
            var wrote = 0;
            for (var b = 0; b < clipData.NumBlends; b++)
            {
                var tracks = clipData.BlendTracks != null && b < clipData.BlendTracks.Length
                    ? clipData.BlendTracks[b] : null;
                if (tracks == null) continue;
                var name = $"{clipData.Name}_b{b}";
                clips[b] = BuildClipAsset(a, clipData, tracks, $"{AnimDir}/{a.Key}_{name}.anim", name, paths);
                if (clips[b] != null) wrote++;
            }

            // 同一序列的单路资产不再被任何 state / 上层树引用（每格各一条 _b*）⇒ 删掉，
            // 免得磁盘上留一份永远不会播的旧剪辑。
            var singlePath = $"{AnimDir}/{a.Key}_{clipData.Name}.anim";
            if (File.Exists(singlePath)) AssetDatabase.DeleteAsset(singlePath);

            var state = controller.CreateBlendTreeInController(clipData.Name, out var tree);
            if (state == null || tree == null)
            {
                Debug.LogError($"[AnimSetup]{StageController} {a.Key}/{clipData.Name} 的 BlendTree 创建失败" +
                               " —— 该序列不会出动画");
                return null;
            }
            tree.blendType = BlendTreeType.FreeformCartesian2D;
            tree.blendParameter = CsAimBlend.ParamYaw;
            tree.blendParameterY = CsAimBlend.ParamPitch;

            for (var b = 0; b < clips.Length; b++)
            {
                if (clips[b] == null) continue;
                tree.AddChild(clips[b], new Vector2(
                    CsAimBlend.CellCentre(b % CsAimBlend.GridN),
                    CsAimBlend.CellCentre(b / CsAimBlend.GridN)));
            }

            if (wrote != BlendGridCount)
            {
                // 非预期分支：格子里缺路 ⇒ 那一片方向的姿势会退化成邻居格（表现是"某几个瞄准角不动"）。
                Debug.LogError($"[AnimSetup]{StageClip} {a.Key}/{clipData.Name} 只写出 {wrote}/{BlendGridCount} 路" +
                               " —— 缺少的方向格会退化到邻近格");
            }
            state.name = clipData.Name;
            return state;
        }

        /// <summary>
        /// 把一条轨道集写成 <c>.anim</c> 资产（同一条序列的多路各调一次）。
        /// </summary>
        /// <param name="clipPath">资产路径（同一路径的旧资产先删，保证"改了生成器就生效"）。</param>
        /// <param name="clipName">资产对象名（多路序列带 <c>_b{路号}</c> 后缀）。</param>
        /// <returns>写好的 clip；一条曲线都没写进去时返回 <c>null</c>。</returns>
        private static AnimationClip BuildClipAsset(Cs16AnimAsset a, Cs16AnimClip clipData,
            Cs16AnimTrack[] tracks, string clipPath, string clipName, string[] paths)
        {
            if (File.Exists(clipPath)) AssetDatabase.DeleteAsset(clipPath);

            var clip = new AnimationClip { name = clipName, frameRate = clipData.Fps };
            var any = false;
            for (var b = 0; b < a.Bones.Count && tracks != null && b < tracks.Length; b++)
            {
                var tr = tracks[b];
                if (tr == null || tr.Count == 0) continue;
                if (paths[b] == null)
                {
                    Debug.LogWarning($"[AnimSetup] {a.Key} 找不到骨骼节点 {a.Bones[b].Name} —— " +
                                     "该骨骼的动画曲线被跳过（骨骼名与预制体不一致？）");
                    continue;
                }
                var px = new Keyframe[tr.Count];
                var py = new Keyframe[tr.Count];
                var pz = new Keyframe[tr.Count];
                var qx = new Keyframe[tr.Count];
                var qy = new Keyframe[tr.Count];
                var qz = new Keyframe[tr.Count];
                var qw = new Keyframe[tr.Count];
                for (var k = 0; k < tr.Count; k++)
                {
                    var t = tr.Times[k];
                    px[k] = new Keyframe(t, tr.Pos[k * 3]);
                    py[k] = new Keyframe(t, tr.Pos[k * 3 + 1]);
                    pz[k] = new Keyframe(t, tr.Pos[k * 3 + 2]);
                    qx[k] = new Keyframe(t, tr.Quat[k * 4]);
                    qy[k] = new Keyframe(t, tr.Quat[k * 4 + 1]);
                    qz[k] = new Keyframe(t, tr.Quat[k * 4 + 2]);
                    qw[k] = new Keyframe(t, tr.Quat[k * 4 + 3]);
                }
                Set(clip, paths[b], "localPosition.x", px);
                Set(clip, paths[b], "localPosition.y", py);
                Set(clip, paths[b], "localPosition.z", pz);
                Set(clip, paths[b], "localRotation.x", qx);
                Set(clip, paths[b], "localRotation.y", qy);
                Set(clip, paths[b], "localRotation.z", qz);
                Set(clip, paths[b], "localRotation.w", qw);
                any = true;
            }

            if (!any)
            {
                Debug.LogWarning($"[AnimSetup] {a.Key}/{clipName} 一条曲线都没写进去 —— 跳过");
                return null;
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = clipData.Loop;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            AssetDatabase.CreateAsset(clip, clipPath);
            return clip;
        }

        /// <summary>在已建 state 的名字表里按**去前缀序列名**找（找不到返回 null）。</summary>
        private static string FindStateName(List<string> stateNames, string name)
        {
            for (var i = 0; i < stateNames.Count; i++)
                if (stateNames[i] == name) return stateNames[i];
            return null;
        }

        private static void Set(AnimationClip clip, string path, string prop, Keyframe[] keys)
        {
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(path, typeof(Transform), prop),
                new AnimationCurve(keys));
        }

        /// <summary>从预制体根往下按名字找骨骼节点（骨骼层级是生成器自己搭的，名字与 mdl 一致）。</summary>
        private static Transform[] CollectBones(Transform root, Cs16AnimAsset a)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            var map = new Dictionary<string, Transform>();
            for (var i = 0; i < all.Length; i++) map[all[i].name] = all[i];
            var outArr = new Transform[a.Bones.Count];
            for (var i = 0; i < a.Bones.Count; i++)
                map.TryGetValue(a.Bones[i].Name, out outArr[i]);
            return outArr;
        }

        private static string RelativePath(Transform root, Transform t)
        {
            var parts = new List<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                parts.Add(cur.name);
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        // ==================================================================
        //  小工具
        // ==================================================================
        /// <summary>
        /// <see cref="Cs16Mat"/> 的 3x4（行主序、平移在第 4 列）→ Unity <see cref="Matrix4x4"/>。
        ///
        /// <para><b>为什么是"返回"而不是"填参数"（这是一条实测缺陷的修复）</b>：旧版是
        /// <c>void Fill(Matrix4x4 m, float[] a)</c> —— <see cref="Matrix4x4"/> 是 **struct**，
        /// 参数按值传递 ⇒ 被填的是**副本**，调用方的 <c>m</c> 仍是全零矩阵。后果是活的、而且很隐蔽：</para>
        /// <list type="bullet">
        /// <item><c>bindposes[i] = m.inverse</c> 变成「全零矩阵的逆 = 全零矩阵」⇒ 蒙皮公式
        /// <c>boneWorld × bindpose</c> 恒为 0 ⇒ **所有顶点塌到一个点**，三角形面积 0 ⇒
        /// 角色与第一人称武器在画面上**完全看不见**（渲染器仍是 <c>enabled</c>、
        /// <c>isVisible=True</c>、有三角有材质 —— 所以只看渲染器状态的探针会误判成"可见"）；</item>
        /// <item>命中区胶囊拿到的是全零矩阵 ⇒ <c>localPosition = 0</c>、
        /// <c>localRotation = inverse(全零矩阵的 rotation)</c> ⇒ 命中盒**全部**堆在骨骼原点上，
        /// 射线命中部位全错。</item>
        /// </list>
        /// <para>实测取证：磁盘上的 <c>Mesh.asset</c> 有 22 个 <c>bindposes</c>，逐个读出**全是全零矩阵**；
        /// 而按 <c>.cs16anim</c> 复算出来的 <c>inverse(BindWorld[0])</c> 平移是
        /// <c>(0.003, -1.019, -0.033)</c> —— 可见数据源没问题，是这一行把矩阵丢掉了。</para>
        /// </summary>
        private static Matrix4x4 ToMatrix(float[] a)
        {
            var m = new Matrix4x4();
            m.m00 = a[0]; m.m01 = a[1]; m.m02 = a[2]; m.m03 = a[3];
            m.m10 = a[4]; m.m11 = a[5]; m.m12 = a[6]; m.m13 = a[7];
            m.m20 = a[8]; m.m21 = a[9]; m.m22 = a[10]; m.m23 = a[11];
            m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
            return m;
        }

        internal static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) parent = parent.Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return;
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
