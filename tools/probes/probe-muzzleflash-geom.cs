// 判据资产（tools/probes/）：差异 #89 的**几何半**（拆出来单独跑，理由见下）。
//
// 为什么拆：本沙箱的 Pipeline 对 eval_file 有 **5s 主线程上限**（实测报
// "Main thread operation timed out after 5000ms"），而棋盘上 8 个 bot + 一整张 de_dust2 在跑时
//   A（本文件）：相机 + 视模型动画后 AABB + 新老落点换算 —— 只读，最便宜；
//   B（probe-muzzleflash-fire.cs）：反射真调 MuzzleFlash + 台账 + 冻 timeScale。
// 两次都轻，才跑得动。
//
// 判据（写死在这里，不靠人眼看数）：
//   PASS ⇔ ① 视模型 AABB 取得到（smr>0 且 z 跨度 > 0）
//          ② 旧落点 (eye + dir*0.34 + right*0.13 + down*0.09) 落在 AABB **内部**
//          ③ 新落点 (eye + rot*(0.13, -0.12, 0.72)) 落在 AABB **之外且更靠前**
//          ④ 新落点在屏幕上（onScreen=true）—— 否则"位置对了但看不见"
//
// 用法（编辑器须在 Play 且有一局在跑）：unity command eval_file --file tools/probes/probe-muzzleflash-geom.cs
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();
System.Func<float, string> F2 = v => v.ToString("F4", inv);
System.Func<UnityEngine.Vector3, string> V3 = v => "(" + F2(v.x) + "," + F2(v.y) + "," + F2(v.z) + ")";

var cams = UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
UnityEngine.Camera cam = null;
for (var i = 0; i < cams.Length; i++)
{
    if (cams[i] == null || !cams[i].enabled) continue;
    if (cams[i].CompareTag("MainCamera")) { cam = cams[i]; break; }
}
if (cam == null)
{
    for (var i = 0; i < cams.Length; i++)
        if (cams[i] != null && cams[i].enabled) { cam = cams[i]; break; }
}
if (cam == null) return "ERROR: no enabled camera";

var w = UnityEngine.Screen.width;
var h = UnityEngine.Screen.height;
sb.Append("cam=").Append(cam.name)
  .Append(" screen=").Append(w).Append('x').Append(h)
  .Append(" fovY=").Append(F2(cam.fieldOfView))
  .Append(" near=").Append(F2(cam.nearClipPlane))
  .Append(" eye=").Append(V3(cam.transform.position))
  .Append(" timeScale=").Append(F2(UnityEngine.Time.timeScale));

// ---------- 视模型（只在相机子树里找，不做全场景扫描） ----------
UnityEngine.Transform vmRoot = null;
var kids = cam.transform.GetComponentsInChildren<UnityEngine.Transform>(true);
for (var i = 0; i < kids.Length; i++)
    if (kids[i] != null && kids[i].name == "CsViewModel") { vmRoot = kids[i]; break; }
if (vmRoot == null)
{
    sb.Append("\n[B] CsViewModel <none under camera> ⇒ 无法判几何（视模型没挂 / 名字改了）");
    return sb.ToString();
}

var smrs = vmRoot.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true);
var lo = new UnityEngine.Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
var hi = new UnityEngine.Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
var found = 0;
for (var s = 0; s < smrs.Length; s++)
{
    var smr = smrs[s];
    if (smr == null) continue;
    var b = smr.bounds;                       // 世界 AABB（SkinnedMeshRenderer 含蒙皮后的包盒）
    if (b.size.sqrMagnitude <= 0f) continue;
    found++;
    lo = UnityEngine.Vector3.Min(lo, b.min);
    hi = UnityEngine.Vector3.Max(hi, b.max);
}
if (found == 0)
{
    sb.Append("\n[B] CsViewModel 下 smr=0 ⇒ 视模型没有蒙皮渲染器，几何判不了");
    return sb.ToString();
}

// 世界 AABB 的 8 角 → 相机局部系（统一到相机空间才可和"硬编码偏移"直接比）
var lLo = new UnityEngine.Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
var lHi = new UnityEngine.Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
for (var c = 0; c < 8; c++)
{
    var p = new UnityEngine.Vector3((c & 1) == 0 ? lo.x : hi.x,
                                     (c & 2) == 0 ? lo.y : hi.y,
                                     (c & 4) == 0 ? lo.z : hi.z);
    var lp = cam.transform.InverseTransformPoint(p);
    lLo = UnityEngine.Vector3.Min(lLo, lp);
    lHi = UnityEngine.Vector3.Max(lHi, lp);
}
sb.Append("\n[B] 视模型动画后世界 AABB[min=").Append(V3(lo)).Append(" max=").Append(V3(hi)).Append("]")
  .Append(" smr=").Append(found)
  .Append("\n[B] 同一包盒 → 相机局部系(x=右 y=上 z=前)[min=").Append(V3(lLo)).Append(" max=").Append(V3(lHi)).Append("]");

var fwd = cam.transform.forward.normalized;
var rgt = UnityEngine.Vector3.Cross(UnityEngine.Vector3.up, fwd).normalized;
var eye = cam.transform.position;
var oldPos = eye + fwd * 0.34f + rgt * 0.13f + UnityEngine.Vector3.down * 0.09f;
var oldLocal = cam.transform.InverseTransformPoint(oldPos);
var oldInside = oldLocal.x >= lLo.x && oldLocal.x <= lHi.x
             && oldLocal.y >= lLo.y && oldLocal.y <= lHi.y
             && oldLocal.z >= lLo.z && oldLocal.z <= lHi.z;

// ---------- 新落点：读 CsCombatTuning 的三个常量（探针里不抄数字） ----------
System.Type tune = null;
var asms = System.AppDomain.CurrentDomain.GetAssemblies();
for (var i = 0; i < asms.Length; i++)
{
    var t0 = asms[i].GetType("Cs16.Module.Combat.CsCombatTuning", false);
    if (t0 != null) { tune = t0; break; }
}
System.Func<string, float> TuneF = k =>
{
    if (tune == null) return float.NaN;
    var f = tune.GetField(k, System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Static);
    if (f == null) return float.NaN;
    var v = f.GetValue(null);
    return v == null ? float.NaN : System.Convert.ToSingle(v);
};
var offR = TuneF("MuzzleOffsetRight");
var offU = TuneF("MuzzleOffsetUp");
var offF = TuneF("MuzzleOffsetForward");
var sizeM = TuneF("MuzzleFlashSize");
sb.Append("\n[B] CsCombatTuning(反射) right=").Append(F2(offR)).Append(" up=").Append(F2(offU))
  .Append(" forward=").Append(F2(offF)).Append(" 期望米宽=").Append(F2(sizeM))
  .Append(" type=").Append(tune != null ? tune.FullName : "NOT FOUND");

var newPos = eye + cam.transform.rotation * new UnityEngine.Vector3(offR, offU, offF);
var newLocal = cam.transform.InverseTransformPoint(newPos);
var newInside = newLocal.x >= lLo.x && newLocal.x <= lHi.x
             && newLocal.y >= lLo.y && newLocal.y <= lHi.y
             && newLocal.z >= lLo.z && newLocal.z <= lHi.z;
var newer = newLocal.z > lHi.z;                       // 比盒子最前端还靠前

var oldSp = cam.WorldToScreenPoint(oldPos);
var newSp = cam.WorldToScreenPoint(newPos);
var newOnScreen = newSp.z > 0f && newSp.x >= 0f && newSp.x <= w && newSp.y >= 0f && newSp.y <= h;

sb.Append("\n[B] 旧落点 camLocal=").Append(V3(oldLocal)).Append(" 在包盒内=").Append(oldInside)
  .Append(" screen=").Append(V3(oldSp));
sb.Append("\n[B] 新落点 camLocal=").Append(V3(newLocal)).Append(" 在包盒内=").Append(newInside)
  .Append(" 比盒前端更前(z>").Append(F2(lHi.z)).Append(")=").Append(newer)
  .Append(" screen=").Append(V3(newSp)).Append(" onScreen=").Append(newOnScreen);

// ---------- 从 B51 载体侧再对一次：贴图应为十字形那张 ----------
var cross = typeof(Cs16.Core.ResPaths).GetField("FxMuzzleFlashCross",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
var crossKey = cross != null ? System.Convert.ToString(cross.GetValue(null)) : null;
sb.Append("\n[B] ResPaths.FxMuzzleFlashCross=").Append(crossKey ?? "MISSING")
  .Append(" 贴图在盘=").Append(!string.IsNullOrEmpty(crossKey)
        ? (UnityEngine.Resources.Load<UnityEngine.Sprite>(crossKey) != null) : false);

var pass = found > 0 && (lHi.z - lLo.z) > 0.01f && oldInside && !newInside && newer && newOnScreen;
sb.Append("\nRESULT-GEOM: ").Append(pass ? "PASS" : "FAIL");
sb.Append("\n  口径：smr>0 且盒有 z 跨度=").Append(found > 0 && (lHi.z - lLo.z) > 0.01f)
  .Append(" 旧落点在盒内=").Append(oldInside)
  .Append(" 新落点不在盒内=").Append(!newInside)
  .Append(" 新落点更前=").Append(newer)
  .Append(" 新落点可见=").Append(newOnScreen);
return sb.ToString();
