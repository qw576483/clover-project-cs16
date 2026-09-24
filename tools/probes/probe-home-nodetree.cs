// 判据资产（tools/probes/）：首页（主菜单）**运行时节点树**的机械判据源。
//
// **实机截图 / 运行时节点树**，不是 grep 源码。改动前闸门第 10 条只做源码字面
// 检查，然后写 HUMAN-ONLY（"must be seen rendered and case-exact"）—— 那条
// 只能由人判。本探针把同一件事变成可计算的：在真 Play 里把**当前屏幕上每个 Text
// 节点**的「层级路径 / 文本 / 字号 / 屏幕矩形」dump 成固定文件
// `tools/probes/home-screen-nodetree.txt`，闸门第 25 条（home-credit-rendered）
// 再断言「最底部的那个文本节点逐字 == `by clover-engine`」。
//
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
sb.Append("# columns: TEXT <hierarchy path> | text='<raw text>' | fontSize=<n> | bestFit=<bool> | rect=<x>,<y>,<w>,<h> | font=<font asset name> | fontDyn=<bool> | glyphs=<characterInfo.Length> | hasA=<bool> | hasa=<bool>\n");
sb.Append("# font columns (clover-engine skill section 6.9: the brand judgement is the ACTUAL text + the ACTUAL font,\n");
sb.Append("#  because a pixel font that only carries uppercase glyphs renders `by clover-engine` as `BY CLOVER-ENGINE`):\n");
sb.Append("#   font    = UnityEngine.UI.Text.font.name (the asset actually used to rasterize that node)\n");
sb.Append("#   fontDyn = Font.dynamic (a dynamic font rasterizes any system glyph, so lowercase is always available)\n");
sb.Append("#   glyphs  = Font.characterInfo.Length (glyphs BAKED into a non-dynamic font asset; 0 => dynamic)\n");
sb.Append("#   hasA/hasa = Font.HasCharacter('A') / Font.HasCharacter('a') -- false on 'a' means the font asset\n");
sb.Append("#               has NO lowercase glyph and the string WILL be drawn all-caps => not compliant\n");
sb.Append("# TMPTEXT lines (should the signature ever become a TextMeshPro node) carry fontAsset/hasA/hasa instead.\n");

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
    // font evidence (skill section 6.9): which font asset renders this node, and
    // whether it can actually draw lowercase. Wrapped defensively so one odd font
    // asset can never abort the whole dump.
    var fnt = t.font;
    var fname = (fnt != null) ? fnt.name : "(null)";
    var fdyn = false; var nglyph = 0; var hasA = false; var hasa = false;
    if (fnt != null)
    {
        try { fdyn = fnt.dynamic; } catch { }
        try { nglyph = fnt.characterInfo.Length; } catch { nglyph = -1; }
        try { hasA = fnt.HasCharacter('A'); } catch { }
        try { hasa = fnt.HasCharacter('a'); } catch { }
    }
    var line = "TEXT " + dig(t.transform, 4)
        + " | text='" + raw.Replace("\n", "\\n") + "'"
        + " | fontSize=" + t.fontSize.ToString(inv)
        + " | bestFit=" + (t.resizeTextForBestFit ? "True" : "False")
        + " | rect=" + minX.ToString("F1", inv) + "," + minY.ToString("F1", inv)
        + "," + (maxX - minX).ToString("F1", inv) + "," + (maxY - minY).ToString("F1", inv)
        + " | font=" + fname
        + " | fontDyn=" + (fdyn ? "True" : "False")
        + " | glyphs=" + nglyph.ToString(inv)
        + " | hasA=" + (hasA ? "True" : "False")
        + " | hasa=" + (hasa ? "True" : "False");
    rows.Add(line);
}
// 按"最底部"排序输出（bottom 最大在前），文件本身即可直读结论
var ordered = new System.Collections.Generic.List<string>(rows);
ordered.Sort((a, b) =>
    string.CompareOrdinal(
        a.Substring(a.LastIndexOf("rect=", System.StringComparison.Ordinal)),
        b.Substring(b.LastIndexOf("rect=", System.StringComparison.Ordinal))));
foreach (var r in ordered) { sb.Append(r).Append('\n'); }

// --- TextMeshPro nodes (skill section 6.9 names TMP_FontAsset explicitly) -----
// Resolved by TYPE NAME through reflection on purpose: a hard `using TMPro;`
// would fail to compile wherever the package is absent, and this probe must
// always produce a dump. Small sample (only TextMeshPro/TMP_Text components).
var nTmp = 0;
try
{
    var tmpComps = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(UnityEngine.FindObjectsInactive.Include);
    for (var i = 0; i < tmpComps.Length; i++)
    {
        var mm = tmpComps[i];
        if (mm == null || !mm.gameObject.activeInHierarchy) continue;
        var full = mm.GetType().FullName;
        if (full == null || !full.StartsWith("TMPro.", System.StringComparison.Ordinal)) continue;
        if (full != "TMPro.TextMeshProUGUI" && full != "TMPro.TextMeshPro") continue;
        var ptxt = mm.GetType().GetProperty("text");
        var tv = (ptxt != null) ? ptxt.GetValue(mm, null) : null;
        var traw = (tv == null) ? "" : tv.ToString();
        if (traw == null || traw.Trim().Length == 0) continue;
        var pfont = mm.GetType().GetProperty("font");
        var fav = (pfont != null) ? pfont.GetValue(mm, null) : null;
        var faName = "(null)";
        var faA = false; var faa = false;
        if (fav != null)
        {
            var fo = fav as UnityEngine.Object;
            if (fo != null) faName = fo.name;
            var mh = fav.GetType().GetMethod("HasCharacter", new System.Type[] { typeof(char) });
            if (mh != null)
            {
                try { faA = (bool)mh.Invoke(fav, new object[] { 'A' }); } catch { }
                try { faa = (bool)mh.Invoke(fav, new object[] { 'a' }); } catch { }
            }
        }
        var comp = mm as UnityEngine.Component;
        if (comp == null) continue;
        var rtr = comp.GetComponent<UnityEngine.RectTransform>();
        var rectTxt = "(no rect)";
        if (rtr != null)
        {
            var cs = new UnityEngine.Vector3[4];
            rtr.GetWorldCorners(cs);
            var cv = comp.GetComponent<UnityEngine.Canvas>();
            var cw = (cv != null) ? cv.worldCamera : null;
            var mnX = float.MaxValue; var mnY = float.MaxValue; var mxX = float.MinValue; var mxY = float.MinValue;
            for (var k = 0; k < 4; k++)
            {
                var sp = UnityEngine.RectTransformUtility.WorldToScreenPoint(cw, cs[k]);
                var sy = UnityEngine.Screen.height - sp.y;
                if (sp.x < mnX) mnX = sp.x;
                if (sp.x > mxX) mxX = sp.x;
                if (sy < mnY) mnY = sy;
                if (sy > mxY) mxY = sy;
            }
            rectTxt = mnX.ToString("F1", inv) + "," + mnY.ToString("F1", inv) + "," + (mxX - mnX).ToString("F1", inv) + "," + (mxY - mnY).ToString("F1", inv);
        }
        nTmp++;
        sb.Append("TMPTEXT ").Append(dig(comp.transform, 4))
          .Append(" | text='").Append(traw.Replace("\n", "\\n")).Append('\'')
          .Append(" | fontAsset=").Append(faName)
          .Append(" | hasA=").Append(faA ? "True" : "False")
          .Append(" | hasa=").Append(faa ? "True" : "False")
          .Append(" | rect=").Append(rectTxt).Append('\n');
    }
}
catch { }
sb.Append("# tmpText.active=").Append(nTmp).Append('\n');
sb.Append("# texts.active=").Append(nAct).Append('\n');

var outp = @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\home-screen-nodetree.txt";
System.IO.File.WriteAllText(outp, sb.ToString(), new System.Text.UTF8Encoding(false));
return "home-screen-nodetree.txt written: " + nAct + " text node(s); activePanels=" + activePanels.ToString() + "; screen=" + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height + "\n" + sb.ToString();
