// 判据资产（tools/probes/）：UI 的「画布缩放 → 屏幕像素」原文。
//
// 判据用途：「雷达在 1080p 下是 1:1 还是被放大」只能由 canvas.scaleFactor 决定 ——
// 界面尺寸 = 布局值 × scaleFactor。Screen 尺寸 + scaleFactor + 雷达节点的世界角点
// 三个数一起，才能把"雷达在屏幕上占多少像素"算死。
// 只读，不改进程状态。用 eval_file 调。
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();
sb.Append("screen=").Append(UnityEngine.Screen.width).Append('x').Append(UnityEngine.Screen.height);
sb.Append("|currentRes=").Append(UnityEngine.Screen.currentResolution.width).Append('x').Append(UnityEngine.Screen.currentResolution.height);
var canvases = UnityEngine.Object.FindObjectsByType<UnityEngine.Canvas>(UnityEngine.FindObjectsInactive.Include);
sb.Append("|canvases=").Append(canvases.Length);
for (var i = 0; i < canvases.Length; i++)
{
    var c = canvases[i];
    if (c == null) continue;
    var sc = c.GetComponent<UnityEngine.UI.CanvasScaler>();
    sb.Append("|canvas[").Append(c.gameObject.name).Append("] renderMode=").Append(c.renderMode)
      .Append(" scaleFactor=").Append(c.scaleFactor.ToString("F4", inv))
      .Append(" pixelRect=").Append(c.pixelRect.width.ToString("F1", inv)).Append('x')
      .Append(c.pixelRect.height.ToString("F1", inv))
      .Append(" overlaySortingOrder=").Append(c.sortingOrder);
    if (sc != null)
    {
        sb.Append(" refRes=").Append(sc.referenceResolution.x.ToString("F0", inv)).Append('x')
          .Append(sc.referenceResolution.y.ToString("F0", inv))
          .Append(" uiScaleMode=").Append(sc.uiScaleMode)
          .Append(" match=").Append(sc.matchWidthOrHeight.ToString("F2", inv))
          .Append(" screenMatchMode=").Append(sc.screenMatchMode);
    }
}
var rts = UnityEngine.Object.FindObjectsByType<UnityEngine.RectTransform>(UnityEngine.FindObjectsInactive.Include);
var corners = new UnityEngine.Vector3[4];
for (var i = 0; i < rts.Length; i++)
{
    var rt = rts[i];
    if (rt == null) continue;
    var n = rt.name;
    if (n.IndexOf("Radar", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
    rt.GetWorldCorners(corners);
    sb.Append("|").Append(n)
      .Append(" sizeDelta=").Append(rt.sizeDelta.x.ToString("F1", inv)).Append('x').Append(rt.sizeDelta.y.ToString("F1", inv))
      .Append(" screenRect x[").Append(corners[0].x.ToString("F1", inv)).Append("..").Append(corners[2].x.ToString("F1", inv))
      .Append("] y[").Append(corners[0].y.ToString("F1", inv)).Append("..").Append(corners[2].y.ToString("F1", inv))
      .Append("] active=").Append(rt.gameObject.activeInHierarchy);
}
return sb.ToString();
