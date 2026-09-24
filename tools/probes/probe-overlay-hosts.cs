//
// 判据用途（用户原话：「开一枪竟然能看到 unity 组件的喇叭和太阳的图标…还有 ui 控件」）：
//   Unity 在 Game view 上叠加绘制的**组件图标**只跟着组件走 —— 喇叭=AudioSource、太阳=Light、
//   相机=Camera、UI 边框=Canvas。要证明"画面上那些图标是编辑器的叠加层、不是游戏渲染出来的"，
//   必须把**这些组件到底挂在哪些对象上、在哪一层、hideFlags 是什么、此刻是否 active**记成原文。
//   * hideFlags：None（=0）= 编辑器会为它画图标；HideInHierarchy/HideAndDontSave 等 = 不画。
//   * activeInHierarchy=false ⇒ Game view 的图标也不画（这条决定了"修前/修后"哪些行该变）。
//   * 同时回读 Game view 的 showGizmos（叠加层的总开关）：它为 True 时上面每一条 active 的
//
// 只读，不改进程状态。用 eval_file 调；同时把 TSV 落 <项目根>/.ai-tmp/test/bv-overlay-hosts.tsv。
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();

System.Func<UnityEngine.Transform, int, string> dig = (tr, depth) =>
{
    var s = tr.name;
    var p = tr.parent;
    var k = 0;
    while (p != null && k < depth) { s = p.name + "/" + s; p = p.parent; k++; }
    return s;
};

var gvt = System.Type.GetType("UnityEditor.GameView,UnityEditor");
var gvProp = gvt != null ? gvt.GetProperty("showGizmos",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) : null;
var gvWins = gvt != null ? UnityEngine.Resources.FindObjectsOfTypeAll(gvt) : null;
sb.Append("gameView.showGizmos=");
if (gvProp != null && gvWins != null && gvWins.Length > 0)
{
    for (var i = 0; i < gvWins.Length; i++)
    {
        if (i > 0) sb.Append(',');
        sb.Append(gvProp.GetValue(gvWins[i], null));
    }
}
else sb.Append("<unavailable>");
sb.Append("\tplayMode=").Append(UnityEditor.EditorApplication.isPlaying);

var lines = new System.Collections.Generic.List<string>();
lines.Add("kind\tpath\thost\thideFlags\tactiveInHierarchy\tenabled\tcomponent");

System.Action<string, UnityEngine.Component, string> add = (kind, c, extra) =>
{
    var go = c.gameObject;
    lines.Add(kind + "\t" + dig(c.transform, 6) + "\t" + go.name + "\t" + go.hideFlags +
              "\t" + go.activeInHierarchy + "\t" + extra + "\t" + c.GetType().Name);
};

var audio = UnityEngine.Object.FindObjectsByType<AudioSource>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < audio.Length; i++)
{
    var a = audio[i];
    if (a == null) continue;
    add("AudioSource", a, a.enabled + "/playing=" + a.isPlaying + "/clip=" + (a.clip != null ? a.clip.name : "<null>"));
}

var lights = UnityEngine.Object.FindObjectsByType<Light>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < lights.Length; i++)
{
    var l = lights[i];
    if (l == null) continue;
    add("Light", l, l.enabled + "/type=" + l.type);
}

var cams = UnityEngine.Object.FindObjectsByType<Camera>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < cams.Length; i++)
{
    var c = cams[i];
    if (c == null) continue;
    add("Camera", c, c.enabled + "/depth=" + c.depth.ToString("F1", inv));
}

var canvases = UnityEngine.Object.FindObjectsByType<UnityEngine.Canvas>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < canvases.Length; i++)
{
    var c = canvases[i];
    if (c == null) continue;
    add("Canvas", c, c.enabled + "/renderMode=" + c.renderMode);
}

lines.Add("#counts\tAudioSource=" + audio.Length + "\tLight=" + lights.Length +
          "\tCamera=" + cams.Length + "\tCanvas=" + canvases.Length);

var outPath = System.IO.Path.Combine(
    System.IO.Directory.GetParent(System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName).FullName,
    ".ai-tmp", "test", "bv-overlay-hosts.tsv");
System.IO.File.WriteAllText(outPath, string.Join("\n", lines.ToArray()), new System.Text.UTF8Encoding(false));

sb.Append("\nAudioSource=").Append(audio.Length).Append(" Light=").Append(lights.Length)
  .Append(" Camera=").Append(cams.Length).Append(" Canvas=").Append(canvases.Length);
sb.Append("\ntsv=").Append(outPath);
var hidden = 0; var shown = 0;
for (var i = 0; i < lines.Count; i++)
{
    if (!lines[i].StartsWith("AudioSource\t") && !lines[i].StartsWith("Light\t") &&
        !lines[i].StartsWith("Camera\t") && !lines[i].StartsWith("Canvas\t")) continue;
    var f = lines[i].Split('\t');
    if (f[3] == "None" && f[4] == "True") shown++; else hidden++;
}
sb.Append("\niconHosts: hideFlagsNone_and_active=").Append(shown).Append(" other=").Append(hidden);
return sb.ToString();
