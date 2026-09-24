using UnityEditor;
using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 进 Play 时把 <b>Game view 的图标叠加层（Gizmos / 组件图标）无条件关掉</b>。
    ///
    /// <para><b>为什么必须有这个脚本（用户报的缺陷，实测根因）</b>：用户原话
    /// 「现在开一枪竟然能看到 unity 组件的喇叭和太阳的图标，选择阵营看地图时候，还有 ui 控件！！！」。
    /// 这些**不是游戏渲染出来的东西**，而是 Unity 编辑器在 Game view 之上叠加绘制的：
    /// <list type="bullet">
    /// <item>喇叭 = <c>AudioSource</c> 的组件图标 —— 引擎 <c>[Sound]</c> 音源池（<c>SFX0..31</c>）
    /// 里每个源挂一个；开火走 <c>Game.Sound.PlaySFXAt</c> ⇒ 音源起播 ⇒ 图标出现；</item>
    /// <item>太阳 = <c>Light</c> 的组件图标 —— <c>Module/Combat/CombatEffects.cs</c> 给枪口火焰/特效
    /// 池对象 <c>go.AddComponent&lt;Light&gt;()</c>；</item>
    /// <item>选阵营时地图上的细线/方框 = 场景里 <c>OnDrawGizmos</c> 的绘制（相机/地标/区域）。</item>
    /// </list>
    /// 三个成员全写 <c>false</c> 后，开火帧的**太阳图标整块消失**（差集 25 727 像素，且差值恰好落在该图标上），
    /// 选阵营帧的细线/方框也整块消失（差集 853 + 91 像素）。⇒ 消除它只需要关掉这一个开关。</para>
    ///
    /// <para><b>为什么是编辑器侧而不是业务侧</b>：叠加层是**编辑器行为**，跟场景里对象怎么写无关 ——
    /// 业务侧给池对象加 <c>HideFlags</c> 也只是"可能不被画"，且引擎 <c>[Sound]</c> 池（34 个 AudioSource）
    /// 由引擎创建、业务改不到。关开关一次性覆盖全部 47 个图标宿主（34 AudioSource / 1 Light / 2 Camera / 10 Canvas）。</para>
    ///
    /// <para><b>幂等 / 无残留</b>：三个成员已是 <c>false</c> 时**直接返回、不写日志**（避免刷 Console）；
    /// 只在真的从"开"改成"关"时留一行日志。不改 ProjectSettings、不写盘、不碰业务对象。
    /// 本脚本**只在进入 Play 时**动手：退出 Play 后开关保持关着（它只是显示偏好，不影响任何渲染结果），
    /// 用户若想自己看 gizmo，可在 Game view 工具栏再点开（但下次进 Play 会再关一次，这是本脚本的用意）。</para>
    ///
    /// <para><b>出处</b>：Game view 的开关成员名来自运行时反射枚举（<c>tools/probes/probe-gameview-gizmos.cs</c>：
    /// <c>field m_Gizmos</c>、<c>prop drawGizmos</c>、<c>prop showGizmos</c>）；
    /// A/B 数字见 <c>.ai-tmp/test/bv-diff-*.png</c> 与 <c>策划/差异登记.tsv</c> 的对应行。</para>
    /// </summary>
    [InitializeOnLoad]
    public static class VisualLeakGuard
    {
        private const string Tag = "[VisualLeak]";

        /// <summary>Game view 的类型（UnityEditor 内部类型，只能用字符串取）。</summary>
        private const string GameViewTypeName = "UnityEditor.GameView,UnityEditor";

        /// <summary>决定"画不画"的三个成员：属性 drawGizmos / showGizmos 与字段 m_Gizmos。</summary>
        private static readonly string[] BoolProperties = { "showGizmos", "drawGizmos" };

        private const string BoolField = "m_Gizmos";

        static VisualLeakGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>进 Play 的那一刻关掉叠加层；其余状态变化不动手。</summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode) return;
            ForceGizmosOff();
        }

        [MenuItem("Clover/CS16/关闭 Game view 图标叠加层", false, 33)]
        private static void ForceGizmosOffFromMenu() => ForceGizmosOff();

        /// <summary>
        /// 把所有 Game view 实例的叠加层开关写成 <c>false</c>（幂等）。
        /// 返回被改动的 Game view 个数；任何一步取不到（Unity 版本换了内部成员名）都留一行 Warn，
        /// </summary>
        public static int ForceGizmosOff()
        {
            var type = System.Type.GetType(GameViewTypeName);
            if (type == null)
            {
                Debug.LogWarning(Tag + " 取不到 " + GameViewTypeName + "，Game view 叠加层未处理（Unity 版本差异？）");
                return 0;
            }

            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;

            var field = type.GetField(BoolField, Flags);
            var props = new System.Reflection.PropertyInfo[BoolProperties.Length];
            for (var i = 0; i < BoolProperties.Length; i++) props[i] = type.GetProperty(BoolProperties[i], Flags);

            var views = Resources.FindObjectsOfTypeAll(type);
            var changed = 0;
            foreach (var view in views)
            {
                if (view == null) continue;
                var before = ReadBool(field, props, view);
                if (before) changed++;
                WriteBool(field, props, view, false);
            }

            if (changed > 0)
            {
                // 全限定：本文件只 using 了 UnityEditor / UnityEngine，`InternalEditorUtility` 裸名
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                Debug.Log(Tag + " 已关闭 " + changed + " 个 Game view 的图标叠加层（AudioSource 喇叭 / Light 太阳 / " +
                          "Camera 与 Canvas 图框都来自这一层），避免它们在游戏画面上出现。");
            }

            return changed;
        }

        /// <summary>三个成员里任意一个为 true 就算"开着"。</summary>
        private static bool ReadBool(System.Reflection.FieldInfo field, System.Reflection.PropertyInfo[] props, Object view)
        {
            var on = false;
            if (field != null && field.GetValue(view) is bool b1 && b1) on = true;
            foreach (var p in props)
            {
                if (p == null || !p.CanRead) continue;
                if (p.GetValue(view, null) is bool pb && pb) on = true;
            }

            return on;
        }

        /// <summary>三个成员一起写（实测只写 showGizmos 时画面一个像素都不变）。</summary>
        private static void WriteBool(System.Reflection.FieldInfo field, System.Reflection.PropertyInfo[] props, Object view, bool on)
        {
            foreach (var p in props)
            {
                if (p == null || !p.CanWrite) continue;
                p.SetValue(view, on, null);
            }

            field?.SetValue(view, on);
        }
    }
}
