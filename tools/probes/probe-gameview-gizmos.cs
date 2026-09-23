// 判据资产（tools/probes/）：Unity Game view 的 **Gizmos / 组件图标叠加层**开关原文。
//
// 判据用途：
//   * Game view 的 Gizmos 开关**是编辑器层偏好**，它决定 Unity 是否在 Game view 上
//     叠加绘制「组件图标」（AudioSource 的喇叭 / Light 的太阳 / Camera 的相机）
//     与 DrawGizmos 线框。这些都不是游戏自己渲染出来的东西 ⇒
//     capture_game_view（source=camera|screen）**采不到**，只有屏幕级截屏才采得到。
//   * 本探针把「开关现在是什么值 + 每个 Game view 实例的窗口矩形」写成原文，
//     作为 A/B 差分（Gizmos 开 vs 关）的锚点：差分数字必须能对回这里的 boolean。
//
// 只读：不改进程状态。用 eval_file 调（返回 string）。
var t = System.Type.GetType("UnityEditor.GameView,UnityEditor");
var sb = new System.Text.StringBuilder();
sb.Append("gameViewType=").Append(t == null ? "NULL" : t.FullName);
if (t == null) return sb.ToString();

var flags = System.Reflection.BindingFlags.Instance
          | System.Reflection.BindingFlags.Public
          | System.Reflection.BindingFlags.NonPublic
          | System.Reflection.BindingFlags.Static;

sb.Append("\n[members-with-gizmo]");
foreach (var f in t.GetFields(flags))
    if (f.Name.ToLowerInvariant().Contains("gizmo"))
        sb.Append("\n  field ").Append(f.Name).Append(" : ").Append(f.FieldType.Name);
foreach (var p in t.GetProperties(flags))
    if (p.Name.ToLowerInvariant().Contains("gizmo"))
        sb.Append("\n  prop ").Append(p.Name).Append(" : ").Append(p.PropertyType.Name).Append(" canWrite=").Append(p.CanWrite);

var wins = UnityEngine.Resources.FindObjectsOfTypeAll(t);
sb.Append("\n[instances] ").Append(wins == null ? 0 : wins.Length);
if (wins != null)
{
    for (var i = 0; i < wins.Length; i++)
    {
        var w = wins[i];
        if (w == null) continue;
        var ed = w as UnityEditor.EditorWindow;
        sb.Append("\n  window[").Append(i).Append("] title=").Append(ed != null ? ed.titleContent.text : "?")
          .Append(" pos=").Append(ed != null ? ed.position.ToString() : "?")
          .Append(" pixelsPerPoint=").Append(UnityEngine.Screen.width).Append('/').Append(UnityEngine.Screen.currentResolution.width);
        foreach (var f in t.GetFields(flags))
        {
            if (!f.Name.ToLowerInvariant().Contains("gizmo")) continue;
            object v = null;
            try { v = f.GetValue(w); } catch (System.Exception e) { v = "<" + e.GetType().Name + ">"; }
            sb.Append("\n    f.").Append(f.Name).Append(" = ").Append(v == null ? "null" : v.ToString());
        }
        foreach (var p in t.GetProperties(flags))
        {
            if (!p.Name.ToLowerInvariant().Contains("gizmo")) continue;
            if (!p.CanRead) continue;
            object v = null;
            try { v = p.GetValue(w, null); } catch (System.Exception e) { v = "<" + e.GetType().Name + ">"; }
            sb.Append("\n    p.").Append(p.Name).Append(" = ").Append(v == null ? "null" : v.ToString());
        }
    }
}
return sb.ToString();
