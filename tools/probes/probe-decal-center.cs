// 判据资产（tools/probes/）：差异 #69 的**表现类那一格** —— 把弹痕打在**画面上的确定位置**，
// （枪身以上、图像上部那条带），这样截帧里「一个弹孔画在墙上」肉眼可判。
//
// 为什么另开一支（而不是复用 probe-decal.cs）：
//   probe-decal.cs 的 [B] 是"绕相机扫 26 个方向找一个面"，命中的点**不一定在视野内**
//   ⇒ 它证明"弹痕生成了、变体对了、尺寸对了"，但**不能**用来截"弹痕上屏"的图
//   （实测：那一版截帧里屏幕上什么都看不到，因为弹痕在视野外）。
//   本支只做一件事：瞄准**指定视口点**（默认 (0.5,0.76)，可降级到 9 个候选）打一条射线，
//   命中 ≤8 m 的真实面即在该点贴弹痕 ⇒ 弹痕**必然**落在那个视口点上。
//
// 做了什么：
//   [A] 取主相机 + 反射取 CombatModule._fx → CombatEffects
//   [B] 「指定视口点」射线 → 真实几何命中（全候选都落空就明确报 FAIL，⛔ 不凭空摆一个）
//   [C] 命中点贴 1 张 + 沿"相机右向在面内的投影"偏移 ±0.30 m 各贴 1 张（共 3 张，排成一排好认）
//   [D] 逐张回报屏幕坐标（WorldToScreenPoint）—— 供裁图定位
//   [E] 不冻时间（冻结会让驱动的 shot= 失效；弹痕寿命 25 s）
//
// 用法：unity command eval_file --file tools/probes/probe-decal-center.cs
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();
System.Func<float, string> F2 = v => v.ToString("F4", inv);
System.Func<UnityEngine.Vector3, string> V3 = v => "(" + F2(v.x) + "," + F2(v.y) + "," + F2(v.z) + ")";
System.Func<UnityEngine.Vector3, string> V0 = v => "(" + v.x.ToString("F0", inv) + "," + v.y.ToString("F0", inv) + ")";

var cams = UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
UnityEngine.Camera cam = null;
for (var i = 0; i < cams.Length; i++)
{
    if (cams[i] == null || !cams[i].enabled) continue;
    if (cams[i].CompareTag("MainCamera")) { cam = cams[i]; break; }
}
if (cam == null)
    for (var i = 0; i < cams.Length; i++)
        if (cams[i] != null && cams[i].enabled) { cam = cams[i]; break; }
if (cam == null) return "ERROR: no enabled camera";

var cm = UnityEngine.Object.FindAnyObjectByType<Cs16.Module.Combat.CombatModule>(
    UnityEngine.FindObjectsInactive.Include);
if (cm == null) return "ERROR: CombatModule <none>";
var fxField = typeof(Cs16.Module.Combat.CombatModule).GetField("_fx",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var fx = fxField != null ? fxField.GetValue(cm) : null;
if (fx == null) return "ERROR: CombatModule._fx 取不到";
var rootField = fx.GetType().GetField("_root",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var root = rootField != null ? rootField.GetValue(fx) as UnityEngine.Transform : null;
if (root == null) return "ERROR: CombatEffects._root=NULL（Init 没跑？）";

System.Func<int> CountShots = () =>
{
    var n = 0;
    for (var i = 0; i < root.childCount; i++)
    {
        var ch = root.GetChild(i);
        if (ch == null || !ch.gameObject.activeSelf) continue;
        var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
        if (sr == null || sr.sprite == null || !sr.enabled) continue;
        if (sr.sprite.name.ToLowerInvariant().Contains("shot")) n++;
    }
    return n;
};

var before = CountShots();

// ---------- [B] 扫面：在**前方锥内**选最近的可贴面 ----------
// ⚠️ 三个坑都实测踩过：
//   ① 只用"视口中心一条射线"⇒ 打到 **36 m** 外的墙，7.5 cm 弹痕在屏幕上约 **1 像素**，截图不可判；
//   ② 扫"全 360° yaw"取最近 ⇒ 最近面在 3.2 m（约 22 px，够判）**但在画面外**（屏坐标 x=−569）；
//   ③ 只按"最近"选面 ⇒ 最近面落在 **y=968（画面下缘）**，正被**第一人称视图模型（手/枪）挡住**，截图还是不可判。
//   ④ **⚠️ 上一版写反了判据**：上一版丢的是 `sp.y > 0.62*H`（=只保留**下部**）——
//      而 Unity 的屏坐标 y **自底部起算**，0.62*1080=670 ⇒ 选中的 y=656 其实落在**屏幕中部偏上**，
//      可第一人称枪身恰好压在中线 ⇒ 截出来的图**整片是枪**，弹痕被挡得干干净净（实测 `z-decal-closeup2.png`）。
//   ⇒ 正解 = **不再依赖"前方锥"**（玩家朝向每次 Play 都不一样：实测同一驱动两次 fwd 分别是
//      `(-0.73,0,-0.68)` 与 `(0.79,0,0.61)`，固定前锥必然有一次落空 —— 实测 `找到=False`），
//      改成**直接瞄准指定的视口点**：从 `cam.ViewportPointToRay(vp)` 打射线、要求命中 ≤8 m 的真实面。
//      vp 全部取在 **y≥0.70**（枪身以上、图像上 30% 那条带），依次降级取第一个命中的。
//      命中即贴弹痕 ⇒ **弹痕必然落在那个视口点**、必然在枪身之上、必然在画面内，与玩家朝向无关。
//   ⑤ 贴 3 张时沿 **相机右向在命中面内** 偏移 ±0.30 m，并在偏移点**沿法线反打一次**吸到面上
//      （否则偏移点可能悬在空中，`BulletImpact` 找不到面、贴不上）。
var origin = cam.transform.position;
// 候选视口点：全部 y≥0.70（自底部起算 ⇒ 落在图像上部 30% 那条带，枪身/HUD 都够不到）。
// 依次取第一个能在 ≤8 m 命中真实面的点 ⇒ 弹痕必然画在该视口点上。
var vps = new UnityEngine.Vector2[]
{
    new UnityEngine.Vector2(0.50f, 0.76f),
    new UnityEngine.Vector2(0.50f, 0.70f),
    new UnityEngine.Vector2(0.36f, 0.74f),
    new UnityEngine.Vector2(0.64f, 0.74f),
    new UnityEngine.Vector2(0.50f, 0.84f),
    new UnityEngine.Vector2(0.44f, 0.71f),
    new UnityEngine.Vector2(0.56f, 0.71f),
    new UnityEngine.Vector2(0.30f, 0.70f),
    new UnityEngine.Vector2(0.70f, 0.70f),
};
UnityEngine.RaycastHit best = default(UnityEngine.RaycastHit);
var hasBest = false; var bestVp = -1; var failLog = "";
for (var k = 0; k < vps.Length; k++)
{
    var ray = cam.ViewportPointToRay(new UnityEngine.Vector3(vps[k].x, vps[k].y, 0f));
    UnityEngine.RaycastHit h;
    if (!UnityEngine.Physics.Raycast(ray.origin, ray.direction, out h, 8f, ~0,
        UnityEngine.QueryTriggerInteraction.Ignore)) { failLog += " vp[" + k + "]=无命中"; continue; }
    if (h.distance < 0.8f) { failLog += " vp[" + k + "]=" + F2(h.distance) + "m(太近)"; continue; }
    best = h; hasBest = true; bestVp = k; break;
}
sb.Append("[A] 相机=").Append(cam.name).Append(" pos=").Append(V3(origin))
  .Append(" fwd=").Append(V3(cam.transform.forward))
  .Append(" 视口=").Append(cam.pixelWidth).Append("x").Append(cam.pixelHeight)
  .Append(" fov=").Append(F2(cam.fieldOfView))
  .Append(" 候选视口点=").Append(vps.Length);
var ok = hasBest;
sb.Append("\n[B] 按「指定视口点」打射线 找到=").Append(ok);
if (!ok)
    return sb.Append(" 逐点结果:").Append(failLog)
      .Append("\nRESULT-DECALCENTER: FAIL\n  口径：全部候选视口点都没有 ≤8m 的可贴面").ToString();
sb.Append(" 用的视口点=(").Append(F2(vps[bestVp].x)).Append(",").Append(F2(vps[bestVp].y)).Append(")")
  .Append(" 命中点屏坐标=").Append(V0(cam.WorldToScreenPoint(best.point)));
var colName = best.collider != null ? best.collider.name : "-";
sb.Append(" point=").Append(V3(best.point)).Append(" n=").Append(V3(best.normal))
  .Append(" dist=").Append(F2(best.distance)).Append("m col=").Append(colName)
  .Append(" 屏坐标=").Append(V0(cam.WorldToScreenPoint(best.point)));

// ---------- [C] 三张：中心 1 张 + 沿相机右向在面内 ±0.30 m ----------
// ⛔ CombatEffects 是 internal（`internal sealed class CombatEffects`）⇒ 不能写 typeof(CombatEffects)；
//    必须从**实例**上取类型再反射（与 probe-decal.cs 同一写法）。
var bi = fx.GetType().GetMethod("BulletImpact",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
if (bi == null) return sb.Append("\nERROR: CombatEffects.BulletImpact 不存在").ToString();
// 沿"相机右向在命中面内的投影"偏移，再**沿法线反打**把偏移点吸回面上（⛔ 不吸的话偏移点可能悬空）。
var right = UnityEngine.Vector3.ProjectOnPlane(cam.transform.right, best.normal);
if (right.sqrMagnitude < 0.0001f) right = cam.transform.right;
right = right.normalized;
var pts = new UnityEngine.Vector3[3];
var placed = new bool[3];
pts[0] = best.point; placed[0] = true;
for (var i = 1; i < 3; i++)
{
    var wanted = best.point + right * (i == 1 ? 0.30f : -0.30f);
    UnityEngine.RaycastHit sh;
    // 从偏移点沿 +n 抬高 0.5 m 再朝 −n 打 ⇒ 吸到面上（同一块墙/地）
    if (UnityEngine.Physics.Raycast(wanted + best.normal * 0.5f, -best.normal, out sh, 1.0f, ~0,
        UnityEngine.QueryTriggerInteraction.Ignore))
    { pts[i] = sh.point; placed[i] = true; }
    else { pts[i] = wanted; placed[i] = false; }   // 吸不住就仍试一下原偏移点（贴不上也不影响 [D] 的判据）
}
var names = new System.Collections.Generic.List<string>();
for (var i = 0; i < 3; i++)
{
    // 用真入口（表现层与开火链同一实例、同一条路径）
    bi.Invoke(fx, new object[] { pts[i], best.normal });
    sb.Append("\n[C] #").Append(i).Append(" BulletImpact(").Append(V3(pts[i])).Append(", n) 已调")
      .Append(" 吸附到面=").Append(placed[i])
      .Append(" 屏坐标=").Append(V0(cam.WorldToScreenPoint(pts[i])));
}

// ---------- [D] 回读：离命中面最近的弹痕 ----------
var after = CountShots();
var nearD = float.MaxValue; string nearName = "-"; UnityEngine.Vector3 nearPos = UnityEngine.Vector3.zero;
float nearW = 0f; UnityEngine.Vector3 nearUp = UnityEngine.Vector3.zero;
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null || !sr.enabled) continue;
    if (!sr.sprite.name.ToLowerInvariant().Contains("shot")) continue;
    names.Add(sr.sprite.name);
    var d = UnityEngine.Vector3.Distance(ch.position, best.point);
    if (d < nearD)
    {
        nearD = d; nearName = sr.sprite.name; nearPos = ch.position;
        nearW = sr.sprite.bounds.size.x * UnityEngine.Mathf.Abs(ch.localScale.x);
        nearUp = ch.up;
    }
}
sb.Append("\n[D] 弹痕 active：before=").Append(before).Append(" after=").Append(after)
  .Append(" 新增=").Append(after - before);
sb.Append("\n[D] 最近的弹痕：sprite=").Append(nearName)
  .Append(" wpos=").Append(V3(nearPos)).Append(" 屏坐标=").Append(V0(cam.WorldToScreenPoint(nearPos)))
  .Append(" 世界宽=").Append(F2(nearW)).Append("m up=").Append(V3(nearUp))
  .Append(" 离命中点=").Append(F2(nearD)).Append("m");
names.Sort(System.StringComparer.Ordinal);

// ---------- [E] 上屏判定 + 弹痕在屏幕上大约几像素 ----------
var sp = cam.WorldToScreenPoint(best.point);
var onScreen = sp.z > 0f && sp.x >= 0f && sp.x < cam.pixelWidth && sp.y >= 0f && sp.y < cam.pixelHeight;
// 屏幕占比（近似，竖直视场）：worldW / (2 * dist * tan(fov/2)) * 视口高
var px = nearW / (2f * UnityEngine.Mathf.Max(0.01f, best.distance) *
         UnityEngine.Mathf.Tan(cam.fieldOfView * 0.5f * UnityEngine.Mathf.Deg2Rad)) * cam.pixelHeight;
sb.Append("\n[E] 命中面屏坐标=").Append(V0(sp)).Append(" 在画面内=").Append(onScreen)
  .Append(" 命中距离=").Append(F2(best.distance)).Append("m")
  .Append(" 弹痕屏幕上约=").Append(F2(px)).Append("px（越近越大；≤2px 则截图不可判）");
sb.Append("\n[F] 未冻结时间（弹痕寿命 25s，驱动可直接截图）");

var pass = ok && (after - before) >= 1 && onScreen && nearW > 0.01f && nearD < 0.25f;
sb.Append("\nRESULT-DECALCENTER: ").Append(pass ? "PASS" : "FAIL")
  .Append("\n  口径：有 ≤8m 的可贴面=True 新增弹痕≥1（=").Append(after - before)
  .Append("）命中面在画面内=True 最近弹痕离命中点<0.25m（=").Append(F2(nearD))
  .Append("m）世界宽>0（=").Append(F2(nearW)).Append("m）");
return sb.ToString();
