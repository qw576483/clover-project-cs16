// 判据资产（tools/probes/）：首页（主菜单）**运行时节点树**的机械判据源。
//
// 为什么需要它（clover-engine skill §8「品牌」）：`by clover-engine` 的判据是
// **实机截图 / 运行时节点树**，⛔ 不是 grep 源码。改动前闸门第 10 条只做源码字面
// 检查，然后写 HUMAN-ONLY（"must be seen rendered and case-exact"）—— 那条
// 只能由人判。本探针把同一件事变成可计算的：在真 Play 里把**当前屏幕上每个 Text
// 节点**的「层级路径 / 文本 / 字号 / 屏幕矩形」dump 成固定文件
// `tools/probes/home-screen-nodetree.txt`，闸门第 25 条（home-credit-rendered）
// 再断言「最底部的那个文本节点逐字 == `by clover-engine`」。
//
// 坐标口径（⛔ 必须写死，否则判据会随实现漂移）：
//   * 屏幕原点 = 左上角，**y 向下增长**（与 .txt 头部 `origin=` 行一致）；
//   * 每行格式：`TEXT <路径> | text='<原文> | fontSize=<n> | bestFit=<bool> | rect=<x>,<y>,<w>,<h>`
//     rect 的 (x, y) = 包围盒左上角，w/h = 包围盒宽高；bottom = y + h
//     ⇒ "最底部" = **bottom 最大**的那一行。
//   * 只列 `activeInHierarchy == true` 且有非空文本的节点。
//
// 只读（不点按钮、不改任何进程状态），只写这一个 dump 文件。用 eval_file 调。
var inv = System.Globalization.CultureInfo.InvariantCulture;
var texts = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(UnityEngine.FindObjectsInactive.Include);

System.Func<UnityEngine.Transform, int, string> dig = (tr, depth) =>
{
    var s = tr.name;
    var p = tr.parent;
    var k = 0;
    while (p != null && k < depth) { s = p.name + "/" + s; p = p.parent; k++; }
    return s;
};

// 哪个 Panel 是 active 的（锚点：证明这份 dump 采自首页而不是别的屏）
var activePanels = new System.Text.StringBuilder();
var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < mbs.Length; i++)
{
    var m = mbs[i];
    if (m == null) continue;
    var tn = m.GetType().Name;
    if (tn.Length < 5 || !tn.EndsWith("Panel", System.StringComparison.Ordinal) || tn == "CsPanelBase") continue;
    if (m.gameObject.activeInHierarchy) activePanels.Append(tn).Append(',');
}

var sb = new System.Text.StringBuilder();
sb.Append("# home-screen Text node tree (clover-engine skill section 8 brand gate)\n");
sb.Append("# screen=").Append(UnityEngine.Screen.width).Append('x').Append(UnityEngine.Screen.height).Append('\n');
sb.Append("# origin=top-left, y grows downward; bottom = y + h\n");
sb.Append("# activePanels=").Append(activePanels.ToString()).Append('\n');
sb.Append("# columns: TEXT <hierarchy path> | text='<raw text>' | fontSize=<n> | bestFit=<bool> | rect=<x>,<y>,<w>,<h>\n");

var rows = new System.Collections.Generic.List<string>();
var nAct = 0;
for (var i = 0; i < texts.Length; i++)
{
    var t = texts[i];
    if (t == null || !t.gameObject.activeInHierarchy) continue;
    var raw = t.text;
    if (raw == null || raw.Trim().Length == 0) continue;
    nAct++;
    var rt = t.rectTransform;
    var corners = new UnityEngine.Vector3[4];
    rt.GetWorldCorners(corners);
    var canvas = t.canvas;
    var cam = (canvas != null && canvas.renderMode != UnityEngine.RenderMode.ScreenSpaceOverlay) ? canvas.worldCamera : null;
    var minX = float.MaxValue; var minY = float.MaxValue; var maxX = float.MinValue; var maxY = float.MinValue;
    for (var k = 0; k < 4; k++)
    {
        var sp = UnityEngine.RectTransformUtility.WorldToScreenPoint(cam, corners[k]);
        // 屏幕 y 翻转成"向下增长"
        var sy = UnityEngine.Screen.height - sp.y;
        if (sp.x < minX) minX = sp.x;
        if (sp.x > maxX) maxX = sp.x;
        if (sy < minY) minY = sy;
        if (sy > maxY) maxY = sy;
    }
    var line = "TEXT " + dig(t.transform, 4)
        + " | text='" + raw.Replace("\n", "\\n") + "'"
        + " | fontSize=" + t.fontSize.ToString(inv)
        + " | bestFit=" + (t.resizeTextForBestFit ? "True" : "False")
        + " | rect=" + minX.ToString("F1", inv) + "," + minY.ToString("F1", inv)
        + "," + (maxX - minX).ToString("F1", inv) + "," + (maxY - minY).ToString("F1", inv);
    rows.Add(line);
}
// 按"最底部"排序输出（bottom 最大在前），文件本身即可直读结论
var ordered = new System.Collections.Generic.List<string>(rows);
ordered.Sort((a, b) =>
    string.CompareOrdinal(
        a.Substring(a.LastIndexOf("rect=", System.StringComparison.Ordinal)),
        b.Substring(b.LastIndexOf("rect=", System.StringComparison.Ordinal))));
foreach (var r in ordered) { sb.Append(r).Append('\n'); }
sb.Append("# texts.active=").Append(nAct).Append('\n');

var outp = @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\home-screen-nodetree.txt";
System.IO.File.WriteAllText(outp, sb.ToString(), new System.Text.UTF8Encoding(false));
return "home-screen-nodetree.txt written: " + nAct + " text node(s); activePanels=" + activePanels.ToString() + "; screen=" + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + "\n" + sb.ToString();
