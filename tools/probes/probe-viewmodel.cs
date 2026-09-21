// 判据资产（tools/probes/）：第一人称视模型（viewmodel）**屏幕占比**与相机 FOV 口径的只读实测。
//
// 为什么必须进 Play 才能判：屏幕占比是"投影结果"，只有活相机的 projectionMatrix / WorldToViewportPoint
// 能给；原版 mdl 载体不在盘（原版资源/cs16src 为空）⇒ 也做不了离线渲染。
// 只读：不点按钮、不写 state.txt、不改任何进程状态。用 eval_file 调（全限定名，不依赖 using）。
//
// 输出（每一行一条判据）：
//   screen=WxH  aspect=A  fovY=..  fovX_back=..  near=.. far=..
//   rg=x=.. y=.. z=.. scale=.. child=CsViewModel
//   smr[node] verts=N tris=M bindBounds=[x..x,y..y,z..z] worldBoundsMin/Max=...
//   vp[node] bbox x[..] y[..]  w%=.. h%=..   (viewport 0..1，含 z<=0 的"在相机后方"计数)
//   vm.anim controller=.. state=.. normTime=.. len=.. HasState(idle)=.. HasState(idle1)=..
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();
System.Func<float, string> F2 = v => v.ToString("F4", inv);

var cams = UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(UnityEngine.FindObjectsInactive.Include);
UnityEngine.Camera cam = null;
for (var i = 0; i < cams.Length; i++)
{
    var c = cams[i];
    if (c == null || !c.enabled) continue;
    if (c.CompareTag("MainCamera")) { cam = c; break; }
}
if (cam == null)
{
    for (var i = 0; i < cams.Length; i++)
        if (cams[i] != null && cams[i].enabled) { cam = cams[i]; break; }
}
if (cam == null) return "ERROR: no enabled camera";

var w = UnityEngine.Screen.width; var h = UnityEngine.Screen.height;
var aspect = (h > 0) ? (w / (float)h) : 1f;
var fovY = cam.fieldOfView;
var fovX = 2f * UnityEngine.Mathf.Atan(UnityEngine.Mathf.Tan(fovY * 0.5f * UnityEngine.Mathf.Deg2Rad) * aspect) * UnityEngine.Mathf.Rad2Deg;
sb.Append("screen=").Append(w).Append('x').Append(h)
  .Append(" aspect=").Append(F2(aspect))
  .Append(" fovY=").Append(F2(fovY)).Append(" fovX_back=").Append(F2(fovX))
  .Append(" near=").Append(F2(cam.nearClipPlane)).Append(" far=").Append(F2(cam.farClipPlane))
  .Append(" cam=").Append(cam.name);

UnityEngine.Transform vmRoot = null;
var kids = cam.transform.GetComponentsInChildren<UnityEngine.Transform>(true);
for (var i = 0; i < kids.Length; i++)
    if (kids[i] != null && kids[i].name == "CsViewModel") { vmRoot = kids[i]; break; }
if (vmRoot == null)
{
    sb.Append("\nCsViewModel <none under camera ").Append(cam.name).Append(">");
    return sb.ToString();
}
sb.Append("\nrg=").Append(vmRoot.localPosition.ToString("F4", inv))
  .Append(" scale=").Append(vmRoot.localScale.ToString("F4", inv))
  .Append(" worldPos=").Append(vmRoot.position.ToString("F4", inv))
  .Append(" active=").Append(vmRoot.gameObject.activeInHierarchy)
  .Append(" children=").Append(vmRoot.childCount);

var smrs = vmRoot.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true);
var totalBehind = 0; var totalPts = 0;
var gx0 = 9f; var gy0 = 9f; var gx1 = -9f; var gy1 = -9f;
for (var s = 0; s < smrs.Length; s++)
{
    var r = smrs[s];
    if (r == null || r.sharedMesh == null) continue;
    var mesh = r.sharedMesh;
    var bb = mesh.bounds;
    var wb = r.bounds;
    sb.Append("\nsmr[").Append(r.gameObject.name).Append("] verts=").Append(mesh.vertexCount)
      .Append(" tris=").Append(mesh.triangles.Length / 3)
      .Append(" bindBounds=").Append(bb.min.ToString("F4", inv)).Append("..").Append(bb.max.ToString("F4", inv))
      .Append(" worldAB=").Append(wb.min.ToString("F3", inv)).Append("..").Append(wb.max.ToString("F3", inv))
      .Append(" enabled=").Append(r.enabled);

    // 精确：烘焙当前姿态 → 相机视口坐标
    var baked = new UnityEngine.Mesh();
    r.BakeMesh(baked);
    var mtx = r.transform.localToWorldMatrix;
    var vs = baked.vertices;
    var x0 = 9f; var y0 = 9f; var x1 = -9f; var y1 = -9f; var behind = 0;
    for (var i = 0; i < vs.Length; i++)
    {
        var wp = mtx.MultiplyPoint3x4(vs[i]);
        var vp = cam.WorldToViewportPoint(wp);
        if (vp.z <= 0f) { behind++; continue; }
        if (vp.x < x0) x0 = vp.x; if (vp.x > x1) x1 = vp.x;
        if (vp.y < y0) y0 = vp.y; if (vp.y > y1) y1 = vp.y;
    }
    totalBehind += behind; totalPts += vs.Length;
    if (x1 > gx1) gx1 = x1; if (x0 < gx0) gx0 = x0;
    if (y1 > gy1) gy1 = y1; if (y0 < gy0) gy0 = y0;
    sb.Append("\nvp[").Append(r.gameObject.name).Append("] bbox normalised x[").Append(F2(x0)).Append("..").Append(F2(x1))
      .Append("] y[").Append(F2(y0)).Append("..").Append(F2(y1))
      .Append("] w%=").Append(x1 >= x0 ? F2((x1 - x0) * 100f) : "n/a")
      .Append(" h%=").Append(y1 >= y0 ? F2((y1 - y0) * 100f) : "n/a")
      .Append(" verts=").Append(vs.Length).Append(" behindCamera=").Append(behind);
    UnityEngine.Object.Destroy(baked);
}
sb.Append("\n[vm.total] renderers=").Append(smrs.Length)
  .Append(" verts=").Append(totalPts).Append(" behindCamera=").Append(totalBehind);
if (gx1 >= gx0)
    sb.Append("\n[vm.union] x[").Append(F2(gx0)).Append("..").Append(F2(gx1)).Append("] y[").Append(F2(gy0)).Append("..").Append(F2(gy1))
      .Append("] w%=").Append(F2((gx1 - gx0) * 100f)).Append(" h%=").Append(F2((gy1 - gy0) * 100f))
      .Append(" area%=").Append(F2((gx1 - gx0) * (gy1 - gy0) * 100f));

var an = vmRoot.GetComponentInChildren<UnityEngine.Animator>(true);
if (an != null)
{
    var st = an.GetCurrentAnimatorStateInfo(0);
    sb.Append("\nvm.anim ctrl=").Append(an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "<null>")
      .Append(" curHash=").Append(st.shortNameHash)
      .Append(" normTime=").Append(F2(st.normalizedTime))
      .Append(" len=").Append(F2(st.length))
      .Append(" HasState(idle)=").Append(an.HasState(0, UnityEngine.Animator.StringToHash("idle")))
      .Append(" HasState(idle1)=").Append(an.HasState(0, UnityEngine.Animator.StringToHash("idle1")))
      .Append(" HasState(draw)=").Append(an.HasState(0, UnityEngine.Animator.StringToHash("draw")));
}
else sb.Append("\nvm.anim <none>");
return sb.ToString();
