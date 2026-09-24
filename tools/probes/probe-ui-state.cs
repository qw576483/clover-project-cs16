//
// 判据用途：
//   * 「当前在哪一屏」= 哪个 Panel 的 gameObject 是 active（截图之外的可复核锚点）；
//   * 悬停态（原版 ButtonArmedBg → SelectionBG）只有 Selectable.currentSelectionState
//     + Image.color + CanvasRenderer.GetColor()（= 真正上屏的顶点色）三个数在一起才算证死；
//   * Options 的 7 个页签 = pages / tabs / handles 计数 + 逐页 tab=N page=Page_X active=True。
// 只读：不点按钮、不写 state.txt、不改任何进程状态。用 eval_file 调（全限定名，不依赖 using）。
var inv = System.Globalization.CultureInfo.InvariantCulture;
System.Func<UnityEngine.Color, string> col = c =>
    "(" + c.r.ToString("F3", inv) + "," + c.g.ToString("F3", inv) + "," + c.b.ToString("F3", inv) + "," + c.a.ToString("F3", inv) + ")";
System.Func<UnityEngine.Transform, int, string> dig = (tr, depth) =>
{
    var s = tr.name;
    var p = tr.parent;
    var k = 0;
    while (p != null && k < depth) { s = p.name + "/" + s; p = p.parent; k++; }
    return s;
};

var sb = new System.Text.StringBuilder();
sb.Append("screen=").Append(UnityEngine.Screen.width).Append('x').Append(UnityEngine.Screen.height);
var canvases = UnityEngine.Object.FindObjectsByType<UnityEngine.Canvas>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < canvases.Length; i++)
{
    var c = canvases[i];
    if (c == null) continue;
    var sc = c.GetComponent<UnityEngine.UI.CanvasScaler>();
    sb.Append("|canvas[").Append(c.gameObject.name).Append("] renderMode=").Append(c.renderMode)
      .Append(" scaleFactor=").Append(c.scaleFactor.ToString("F4", inv))
      .Append(" pixelRect=").Append(c.pixelRect.width.ToString("F0", inv)).Append('x').Append(c.pixelRect.height.ToString("F0", inv));
    if (sc != null)
    {
        sb.Append(" refRes=").Append(sc.referenceResolution.x.ToString("F0", inv)).Append('x')
          .Append(sc.referenceResolution.y.ToString("F0", inv));
    }
}

// ---- 哪些 Panel 在场（含未激活的，靠 active 字段区分）----
sb.Append("\n[panels]");
var mbs = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(UnityEngine.FindObjectsInactive.Include);
var panelCount = 0;
for (var i = 0; i < mbs.Length; i++)
{
    var m = mbs[i];
    if (m == null) continue;
    var tn = m.GetType().Name;
    if (tn.Length < 5 || !tn.EndsWith("Panel", System.StringComparison.Ordinal)) continue;
    if (tn == "CsPanelBase") continue;
    panelCount++;
    sb.Append("\n  ").Append(tn).Append(" active=").Append(m.gameObject.activeInHierarchy)
      .Append(" root=").Append(dig(m.transform, 3));
}
sb.Append("\n[panels.count] ").Append(panelCount);

// ---- 按钮：状态 + 上屏色 + 悬停态 ----
sb.Append("\n[buttons]");
var btns = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(UnityEngine.FindObjectsInactive.Include);
var activeBtns = 0;
for (var i = 0; i < btns.Length; i++)
{
    var b = btns[i];
    if (b == null || !b.gameObject.activeInHierarchy) continue;
    activeBtns++;
    var label = "";
    var txt = b.GetComponentInChildren<UnityEngine.UI.Text>();
    if (txt != null) label = txt.text.Replace("\n", "\\n");
    var img = b.GetComponent<UnityEngine.UI.Image>();
    var cr = b.GetComponent<UnityEngine.CanvasRenderer>();
    // currentSelectionState 是 protected -> 反射取（只有它能区分 Normal / Highlighted）
    var selProp = typeof(UnityEngine.UI.Selectable).GetProperty("currentSelectionState",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
    var sel = (selProp != null) ? selProp.GetValue(b, null).ToString() : "?";
    sb.Append("\n  ").Append(dig(b.transform, 2))
      .Append(" label='").Append(label).Append("'")
      .Append(" interactable=").Append(b.IsInteractable())
      .Append(" state=").Append(sel)
      .Append(" transition=").Append(b.transition);
    if (img != null) sb.Append(" Image.color=").Append(col(img.color));
    if (cr != null) sb.Append(" CR=").Append(col(cr.GetColor()));
}
sb.Append("\n[buttons.active] ").Append(activeBtns).Append(" of ").Append(btns.Length);

// ---- 在场文本（只打印激活对象上的）----
sb.Append("\n[texts]");
var texts = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(UnityEngine.FindObjectsInactive.Include);
var n = 0;
for (var i = 0; i < texts.Length; i++)
{
    var t = texts[i];
    if (t == null || !t.gameObject.activeInHierarchy) continue;
    n++;
    if (n > 90) continue;
    sb.Append("\n  ").Append(dig(t.transform, 3)).Append(" = '").Append(t.text.Replace("\n", "\\n")).Append("'");
}
sb.Append("\n[texts.active] ").Append(n);
return sb.ToString();
