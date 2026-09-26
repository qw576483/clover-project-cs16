using System.Collections.Generic;
using CloverEngine;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Cs16.Module.View
{
    /// <summary>
    /// 渲染预热：把"第一次真正把它画出来"的代价（着色器变体编译 / 贴图上传 / 骨骼蒙皮缓冲）
    /// 从比赛首帧挪到读条屏或启动画面上。
    ///
    /// <para><b>三段，先免相机后要相机</b>：</para>
    /// <list type="number">
    /// <item><see cref="WarmInUseVariants"/>：编译当前**在用材质**的着色器变体，**不需要相机**；</item>
    /// <item><see cref="WarmCanvases"/>：强制所有 Canvas 现在重建一次网格，**不需要相机**；</item>
    /// <item><see cref="WarmOffscreen"/>：用一台只活在预热期的临时相机（不进任何场景文件、不带
    /// <c>MainCamera</c> 标签、渲进离屏 RenderTexture 后立刻销毁）把给定位置画一帧，
    /// 强制贴图上传与蒙皮缓冲创建。</item>
    /// </list>
    ///
    /// <para><b>⛔ 不锁死</b>：这三段里昂贵的是 Unity 自己的同步调用，**不可中断** ⇒ 本类只保证
    /// "每段至多发起一次 + <see cref="OverBudget"/> 到点后调用方不再发起后续段"；
    /// 超时兜底是**调用方门上**的事（见 <c>ViewModule.WarmupDeadlineSeconds</c> 与
    /// <c>AppFlow.StageLoadTimeout</c>），本类绝不用来当进门条件。</para>
    /// </summary>
    public static class CsRenderWarmup
    {
        private const string Tag = "RenderWarm";

        /// <summary>总开关（关闭 = 每次调用直接返回，用于 A/B）。</summary>
        public static bool Enabled = true;

        /// <summary>是否做离屏绘制段（免相机的两段之后）。</summary>
        public static bool UseOffscreenRender = true;

        /// <summary>离屏绘制的方框边长（像素）。</summary>
        private const int OffscreenSize = 256;

        /// <summary>
        /// 预热起点标记（调用方在预热开始时取 <c>Time.realtimeSinceStartup</c> 传进来）。
        /// </summary>
        public static bool OverBudget(float phaseStart)
        {
            if (phaseStart <= 0f) return false;
            return Time.realtimeSinceStartup - phaseStart > BudgetSeconds;
        }

        /// <summary>预热总预算（秒）：超了就不再做后续段（已做的保留）。</summary>
        public static float BudgetSeconds = 8f;

        /// <summary>
        /// 着色器变体的收集口径：<c>false</c> = 只收**此刻在用材质**那批（默认）；
        /// <c>true</c> = 退回 <c>Shader.WarmupAllShaders()</c> 的全量口径（用来 A/B 比较）。
        /// </summary>
        public static bool UseFullShaderWarm = false;

        /// <summary>
        /// 每个在用材质要收的 pass 类型。材质首帧真正走的就是这几个；
        /// 某个 shader 没有该 pass 时 <see cref="ShaderVariantCollection.Add"/> 不产生变体。
        /// </summary>
        private static readonly PassType[] WarmPassTypes =
        {
            PassType.Normal,
            PassType.ForwardBase,
            PassType.ForwardAdd,
            PassType.ShadowCaster
        };

        /// <summary>在用材质的变体集合，跨调用复用（每次先清空再重建）。</summary>
        private static ShaderVariantCollection _inUseVariants;

        /// <summary>日志里最多列几个 shader 名（超过就截断，避免一条日志几百字）。</summary>
        private const int ShaderNameLogLimit = 12;

        /// <summary>
        /// 编译"此刻真的挂在物体上"的那批着色器变体。返回耗时毫秒（跳过 / 抛异常时返回 0 或已耗时）。
        ///
        /// <para><b>为什么不是 <c>Shader.WarmupAllShaders()</c></b>：后者编译**所有已加载 shader 的全部
        /// 已加载变体**（含此刻没有任何材质用到的组合），启动屏要用的那批只是其中一个子集；
        /// 本方法只编译这个子集 ⇒ 覆盖不缩水，代价按实际用量付。
        /// <see cref="UseFullShaderWarm"/> = <c>true</c> 时走全量口径。</para>
        /// </summary>
        public static float WarmInUseVariants(float phaseStart)
        {
            if (!Enabled) return 0f;
            if (OverBudget(phaseStart))
            {
                Game.Logger?.Warn(Tag, $"着色器变体预热跳过：已超出 {BudgetSeconds:0}s 预算");
                return 0f;
            }

            var t0 = Time.realtimeSinceStartup;
            var shaders = 0;
            var variants = 0;
            var shaderNames = string.Empty;
            try
            {
                if (UseFullShaderWarm) Shader.WarmupAllShaders();
                else CollectInUseVariants(out shaders, out variants, out shaderNames);
            }
            catch (System.Exception e)
            {
                var failed = (Time.realtimeSinceStartup - t0) * 1000f;
                Game.Logger?.Warn(Tag,
                    $"着色器变体预热抛异常（{e.GetType().Name}: {e.Message}），" +
                    $"耗时 {failed:F0} ms；照常继续，未编译的变体由首帧付代价");
                return failed;
            }

            var ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Game.Logger?.Info(Tag, UseFullShaderWarm
                ? $"着色器变体预热完成（全量口径 Shader.WarmupAllShaders）耗时 {ms:F0} ms"
                : $"着色器变体预热完成（在用材质口径：{shaders} 个 shader / {variants} 个变体 [{shaderNames}]）" +
                  $"耗时 {ms:F0} ms");
            return ms;
        }

        /// <summary>
        /// 把场上材质收进变体集合后一次 <c>WarmUp</c>，并回填（shader 数, 变体数, shader 名）。
        /// 遍历口径 = 全部 <see cref="Renderer"/>（含未激活；与 <c>FirstPersonCamera.cs</c> 里
        /// <c>Object.FindObjectsByType</c> 同一取法）的 <c>sharedMaterials</c>，
        /// 加上全部 uGUI <see cref="Graphic"/> 的 <c>material</c>（为空时取 <c>defaultMaterial</c>）。
        /// </summary>
        private static void CollectInUseVariants(out int shaders, out int variants, out string shaderNames)
        {
            if (_inUseVariants == null) _inUseVariants = new ShaderVariantCollection();
            else _inUseVariants.Clear();

            var usedShaders = new HashSet<Shader>();
            var seen = new HashSet<string>();

            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            for (var i = 0; i < renderers.Length; i++)
            {
                var mats = renderers[i].sharedMaterials;
                for (var m = 0; m < mats.Length; m++) AddVariants(mats[m], usedShaders, seen);
            }

            var graphics = Object.FindObjectsByType<Graphic>(FindObjectsInactive.Include);
            for (var i = 0; i < graphics.Length; i++)
            {
                // 没显式挂材质的（本工程的 uGUI 都是这种）取 uGUI 的 defaultMaterial ——
                // Graphic 给的是 Canvas 默认材质，Text 覆盖成**字体材质**，与渲染时取的是同一份。
                AddVariants(graphics[i].material ?? graphics[i].defaultMaterial, usedShaders, seen);
            }

            _inUseVariants.WarmUp();
            shaders = usedShaders.Count;
            variants = seen.Count;

            // shader 名进日志（超过 12 个截断）—— 预热的覆盖范围要能从日志读出来，而不是只能信代码。
            var names = new List<string>(usedShaders.Count);
            foreach (var s in usedShaders) names.Add(s.name);
            shaderNames = names.Count > ShaderNameLogLimit
                ? string.Join(", ", names.GetRange(0, ShaderNameLogLimit)) + $", …(+{names.Count - ShaderNameLogLimit})"
                : string.Join(", ", names);
        }

        private static void AddVariants(Material mat, HashSet<Shader> usedShaders, HashSet<string> seen)
        {
            if (mat == null || mat.shader == null) return;

            var shader = mat.shader;
            var keywords = mat.shaderKeywords;
            var keywordKey = keywords == null ? string.Empty : string.Join(",", keywords);
            for (var p = 0; p < WarmPassTypes.Length; p++)
            {
                var key = shader.name + "|" + (int)WarmPassTypes[p] + "|" + keywordKey;
                if (!seen.Add(key)) continue;
                _inUseVariants.Add(new ShaderVariantCollection.ShaderVariant(shader, WarmPassTypes[p], keywords));
            }

            usedShaders.Add(shader);
        }

        /// <summary>强制所有 Canvas 现在重建一次网格（免相机）。返回覆盖到的 Canvas 数。</summary>
        public static int WarmCanvases(float phaseStart)
        {
            if (!Enabled) return 0;
            if (OverBudget(phaseStart))
            {
                Game.Logger?.Warn(Tag, "Canvas 预热跳过：已超出预算");
                return 0;
            }

            var t0 = Time.realtimeSinceStartup;
            Canvas.ForceUpdateCanvases();
            var ms = (Time.realtimeSinceStartup - t0) * 1000f;
            Game.Logger?.Info(Tag, $"Canvas 网格预热完成（Canvas.ForceUpdateCanvases）耗时 {ms:F0} ms");
            return 1;
        }

        /// <summary>
        /// 用一台临时相机把 <paramref name="anchor"/> 处画一帧到离屏 RenderTexture，
        /// 然后立刻拆掉相机。返回耗时毫秒；<paramref name="anchor"/> 为空时跳过。
        /// </summary>
        public static float WarmOffscreen(string what, Transform anchor, float phaseStart)
        {
            if (!Enabled || !UseOffscreenRender) return 0f;
            if (OverBudget(phaseStart))
            {
                Game.Logger?.Warn(Tag, $"离屏绘制预热跳过（{what}）：已超出预算");
                return 0f;
            }

            var camGo = new GameObject("CsRenderWarmCam");
            RenderTexture rt = null;
            var t0 = Time.realtimeSinceStartup;
            try
            {
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Color.black;
                cam.cullingMask = ~0;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 40f;
                cam.enabled = false;

                if (anchor != null)
                {
                    // 正交 + 侧视：把 anchor 处的整批实例一次框住（它们都在 anchor 原点附近）。
                    cam.orthographic = true;
                    cam.orthographicSize = 4f;
                    camGo.transform.position = anchor.position + new Vector3(0f, 0f, -6f);
                    camGo.transform.rotation = Quaternion.identity;
                }
                else
                {
                    cam.orthographic = false;
                    cam.fieldOfView = 60f;
                    camGo.transform.position = new Vector3(0f, 1.6f, 0f);
                    camGo.transform.rotation = Quaternion.identity;
                }

                rt = RenderTexture.GetTemporary(OffscreenSize, OffscreenSize, 16);
                cam.targetTexture = rt;
                cam.enabled = true;
                cam.Render();
                cam.targetTexture = null;
                cam.enabled = false;

                var ms = (Time.realtimeSinceStartup - t0) * 1000f;
                Game.Logger?.Info(Tag, $"离屏绘制预热完成（{what}）耗时 {ms:F0} ms");
                return ms;
            }
            catch (System.Exception e)
            {
                var ms = (Time.realtimeSinceStartup - t0) * 1000f;
                Game.Logger?.Warn(Tag,
                    $"离屏绘制预热失败（{what}：{e.GetType().Name}: {e.Message}），耗时 {ms:F0} ms；照常继续");
                return ms;
            }
            finally
            {
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(camGo);
            }
        }
    }
}
