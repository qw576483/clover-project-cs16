//
// 判据用途：勾不再是"同色小方块"，而是原版 **Marlett gid 12** 的预渲染贴图
//（`Resources/UI/Art/menu_check`，132x140）⇒ 逐格给四个数：
//   * 所在行名（= OptionsPanel 控件表里的 fieldName）+ 所在页；
//   * `active` = 自身 GameObject 是否激活（**勾选态 = 显示**，语义不变）；
//   * `sprite` = 当前 sprite 名（改造前是 NONE，改造后是 `menu_check_0`）；
//   * `rect` = 上屏矩形（屏幕像素，y **从下往上**，与 canvas 坐标同系）—— 截图裁切靠它对齐；
//   * `color` / `cr` = Image.color 与 CanvasRenderer 的顶点色（上屏色 = 二者相乘）。
//
// 只读：不点按钮、不写 state.txt、不改任何进程状态。用 `unity command eval_file` 调。
var inv = System.Globalization.CultureInfo.InvariantCulture;
System.Func<UnityEngine.Color, string> col = c =>
    "(" + c.r.ToString("F3", inv) + "," + c.g.ToString("F3", inv) + "," + c.b.ToString("F3", inv) + "," + c.a.ToString("F3", inv) + ")";

var lines = new System.Collections.Generic.List<string>();
var imgs = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Image>(UnityEngine.FindObjectsInactive.Include);
var total = 0; var on = 0; var spriteOk = 0;
var corners = new UnityEngine.Vector3[4];
for (var i = 0; i < imgs.Length; i++)
{
    var im = imgs[i];
    if (im == null || im.name != "Mark") continue;
    total++;
    var rt = im.rectTransform;
    var row = rt.parent;
    var page = (row != null && row.parent != null) ? row.parent.name : "?";
    var rowName = (row != null) ? row.name : "?";
    var active = im.gameObject.activeInHierarchy;
    if (active) on++;
    var spr = (im.sprite != null) ? im.sprite.name : "NONE";
    if (spr == "menu_check") spriteOk++;   // Sprite 导入模式 Single ⇒ sprite 名 = 文件名（不是表里的 menu_check_0）
    rt.GetWorldCorners(corners);   // 0=bl 1=tl 2=tr 3=br；Canvas 是 Screen Space-Overlay ⇒ world == 屏幕像素（y 向上）
    var cr = im.GetComponent<UnityEngine.CanvasRenderer>();
    lines.Add("MARK\t" + rowName + "\t" + page
              + "\tself=" + im.gameObject.activeSelf
              + "\tinh=" + active
              + "\tsprite=" + spr
              + "\tpreserveAspect=" + im.preserveAspect
              + "\ttype=" + im.type
              + "\trect=" + corners[0].x.ToString("F0", inv) + "," + corners[0].y.ToString("F0", inv)
              + "," + corners[2].x.ToString("F0", inv) + "," + corners[2].y.ToString("F0", inv)
              + "\tcolor=" + col(im.color)
              + "\tcr=" + ((cr != null) ? col(cr.GetColor()) : "-")
              + "\tenabled=" + im.enabled);
}
lines.Sort(System.StringComparer.Ordinal);

var sb = new System.Text.StringBuilder();
sb.Append("[check-marks] screen=").Append(UnityEngine.Screen.width).Append('x').Append(UnityEngine.Screen.height);
for (var i = 0; i < lines.Count; i++) { sb.Append('\n').Append(lines[i]); }
sb.Append("\n[check-marks.count] total=").Append(total)
  .Append(" activeInHierarchy=").Append(on)
  .Append(" withSprite=menu_check:").Append(spriteOk);
return sb.ToString();
