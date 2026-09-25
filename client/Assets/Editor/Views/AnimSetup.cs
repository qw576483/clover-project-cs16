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
    /// <item><b>命中区胶囊</b>：4 段，挂在对应骨骼下（跟随动画）。</item>
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

            // 命中区：4 个胶囊挂到对应骨骼下（跟随动画）
            var capBindWorld = a.BindWorld();
            for (var i = 0; i < a.Caps.Count; i++)
            {
                var cap = a.Caps[i];
                if (cap.Bone < 0 || cap.Bone >= bones.Length) continue;
                var node = new GameObject(CsViewTuning.HitboxNodePrefix + cap.Zone);
                var bt = bones[cap.Bone];
                node.transform.SetParent(bt, false);

                // 胶囊在绑定姿态下必须**竖直**（与 Body 轴对齐）：
                // localRotation = 绑定世界旋转的逆 ⇒ 世界旋转回到 identity。
                var bindWorld = ToMatrix(capBindWorld[cap.Bone]);
                node.transform.localRotation = Quaternion.Inverse(bindWorld.rotation);
                var worldCenter = new Vector3(0f, cap.CenterY, 0f);
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
            }

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
                uvs[i] = new Vector2(sub.Uvs[i * 2], sub.Uvs[i * 2 + 1]);
                norms[i] = new Vector3(sub.Norms[i * 3], sub.Norms[i * 3 + 1], sub.Norms[i * 3 + 2]);
                if (!skinned) continue;
                var bi = Mathf.Clamp(sub.BoneIdx[i], 0, maxBone);
                weights[i] = new BoneWeight { boneIndex0 = bi, weight0 = 1f };
            }

            var mesh = new Mesh { name = $"{key}_{SkinName}{index}" };
            if (n > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = norms;
            if (skinned)
            {
                mesh.boneWeights = weights;
                mesh.bindposes = bindposes;
            }
            mesh.triangles = ExpandDoubleSided(sub.Tris);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// 三角形正反各一份（几何层面的双面）。
        /// <para>MDL 的三角形是 tristrip 命令流（<c>hlsdk/utils/studiomdl/tristrip.c::BuildTris</c>），
        /// 条带里相邻三角形绕序交替；<c>cs16_build.py</c> 按"顺序取三点"解出后绕序被拉平
        /// （实测与 MDL 自带法线的同向率只有 44%~52%、且逐对严格交替）。
        /// 条带边界在 <c>.cs16anim</c> 里没有保留 ⇒ 无法在生成器里可靠还原绕序；
        /// 而 Unity 内置 Standard 着色器**没有 `_Cull` 材质属性**（实测），关不掉背面剔除。
        /// 双面展开对观感无损（背面那份与正面共面、被 Z 缓冲挡住），
        /// 受光仍用 MDL 自带的外向法线。</para>
        /// </summary>
        private static int[] ExpandDoubleSided(uint[] tris)
        {
            if (tris == null || tris.Length < 3) return new int[0];
            var outIdx = new int[tris.Length * 2];
            for (int i = 0, j = 0; i + 2 < tris.Length; i += 3, j += 6)
            {
                outIdx[j] = (int)tris[i];
                outIdx[j + 1] = (int)tris[i + 1];
                outIdx[j + 2] = (int)tris[i + 2];
                outIdx[j + 3] = (int)tris[i];
                outIdx[j + 4] = (int)tris[i + 2];
                outIdx[j + 5] = (int)tris[i + 1];
            }
            return outIdx;
        }

        private static CsHitbox ParseZone(string zone)
        {
            switch (zone)
            {
                case "Head": return CsHitbox.Head;
                case "Chest": return CsHitbox.Chest;
                case "Stomach": return CsHitbox.Stomach;
                case "Leg": return CsHitbox.Leg;
                default:
                    Debug.LogWarning($"[AnimSetup] 未知命中区名「{zone}」，按 Chest 处理");
                    return CsHitbox.Chest;
            }
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

            var clips = new List<AnimationClip>();
            var stateNames = new List<string>();          // 与 clips 一一对应（state 名 = 去前缀序列名）
            var usedStates = new HashSet<string>();
            for (var c = 0; c < a.Clips.Count; c++)
            {
                var clipData = a.Clips[c];
                if (!usedStates.Add(clipData.Name))
                {
                    Debug.LogWarning($"[AnimSetup] {a.Key} 的序列名「{clipData.Name}」重复 —— " +
                                     "只保留第一条的 state（state 名必须唯一，同名后一条运行期按名字查不到）");
                    continue;
                }
                var clipPath = $"{AnimDir}/{a.Key}_{clipData.Name}.anim";
                if (File.Exists(clipPath)) AssetDatabase.DeleteAsset(clipPath);

                var clip = new AnimationClip { name = clipData.Name, frameRate = clipData.Fps };
                var any = false;
                for (var b = 0; b < a.Bones.Count && b < clipData.Tracks.Length; b++)
                {
                    var tr = clipData.Tracks[b];
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
                    Debug.LogWarning($"[AnimSetup] {a.Key}/{clipData.Name} 一条曲线都没写进去 —— 跳过");
                    continue;
                }

                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = clipData.Loop;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                AssetDatabase.CreateAsset(clip, clipPath);
                clips.Add(clip);
                stateNames.Add(clipData.Name);      // 注意：CreateAsset 会把 clip.name 改成文件名（带 key 前缀），
                                                    // 所以 state 名从**序列名**另存一份，不读 clip.name
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            if (controller == null || clips.Count == 0)
            {
                Debug.LogError($"[AnimSetup] {a.Key} 的 controller 创建失败或没有可用 clip");
                return null;
            }

            AnimatorState first = null;
            for (var i = 0; i < clips.Count; i++)
            {
                var st = controller.AddMotion(clips[i]);
                st.name = stateNames[i];     // = 去前缀序列名（运行时候选名），**不是** clip 资产名
                if (first == null) first = st;
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

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
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
        /// <c>localRotation = inverse(全零矩阵的 rotation)</c> ⇒ 4 个命中盒全堆在骨骼原点上，
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
