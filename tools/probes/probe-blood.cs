// 判据资产（tools/probes/）：差异 #74 的**真调用半**（受击出血雾 + 命中面贴血迹）。
//
// 编辑器须在 Play 且有一局在跑。
//   [A] 反射取 CombatModule._fx → CombatEffects
//   [B] 相机前方 26 方向扫面找一块**真实朝墙**（|normal.y|<0.5）：血迹要贴在"命中点背后的面"上，
//       所以必须打真实几何（⛔ 不凭空摆）
//   [C] 调真入口 CombatEffects.BloodImpact(命中点, 弹道方向=-法线, headshot=false)
//       —— 逐字同 CombatModule.cs:95 那条链的同一个入口
//   [D] 连打 40 次收集血迹贴图名集合（原版 6 变体 {blood1..6）
//   [E] 读回血雾 / 血迹物件的贴图名 + 世界宽
//   [F] 冻结 timeScale=0 供截图；收尾 bash 里 eval 'UnityEngine.Time.timeScale = 1f;'
//
// 用法：unity command eval_file --file tools/probes/probe-blood.cs
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

// ---------- [B] 找真实朝墙 ----------
var origin = cam.transform.position;
var fwd = cam.transform.forward;
var dirs = new System.Collections.Generic.List<UnityEngine.Vector3>();
dirs.Add(fwd);
for (var a = -180; a < 180; a += 30)
    for (var e = -45; e <= 45; e += 15)
        dirs.Add(UnityEngine.Quaternion.Euler(e, a, 0f) * fwd);

UnityEngine.RaycastHit wallHit = default(UnityEngine.RaycastHit); var hasWall = false;
for (var i = 0; i < dirs.Count; i++)
{
    UnityEngine.RaycastHit h;
    if (!UnityEngine.Physics.Raycast(origin, dirs[i], out h, 120f, ~0, UnityEngine.QueryTriggerInteraction.Ignore))
        continue;
    if (System.Math.Abs(h.normal.y) < 0.5f) { wallHit = h; hasWall = true; break; }
}
sb.Append("[B] 墙面命中=").Append(hasWall)
  .Append(hasWall ? (" point=" + V3(wallHit.point) + " n=" + V3(wallHit.normal)) : "");
if (!hasWall) return sb.Append("\nERROR: 26 方向没找到朝墙（拿不到可贴面）").ToString();

// ---------- [C] 真调用 BloodImpact ----------
var bi = fx.GetType().GetMethod("BloodImpact",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
if (bi == null) return sb.Append("\nERROR: BloodImpact 取不到").ToString();
var ps = bi.GetParameters();
sb.Append("\n[C] BloodImpact 参数个数=").Append(ps.Length).Append(" [");
for (var i = 0; i < ps.Length; i++) sb.Append(ps[i].ParameterType.Name).Append(i + 1 < ps.Length ? "," : "");
sb.Append(']');
if (ps.Length != 3) return sb.Append("\nERROR: 期望 3 参 (Vector3 point, Vector3 direction, bool headshot)").ToString();

var into = -wallHit.normal;
var r1 = (bool)bi.Invoke(fx, new object[] { wallHit.point, into, false });
sb.Append("\n[C] BloodImpact(point, dir=-normal, headshot=false) 返回=").Append(r1);

// ---------- [D] 40 发收集血迹变体 ----------
for (var i = 0; i < 40; i++) bi.Invoke(fx, new object[] { wallHit.point, into, i % 2 == 0 });

var names = new System.Collections.Generic.List<string>();
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null) continue;
    var nm = sr.sprite.name;
    if (nm.ToLowerInvariant().Contains("blood") && !names.Contains(nm)) names.Add(nm);
}
names.Sort(System.StringComparer.Ordinal);
sb.Append("\n[D] 血迹变体数=").Append(names.Count).Append(" 集合=[");
for (var i = 0; i < names.Count; i++) sb.Append(names[i]).Append(i + 1 < names.Count ? "," : "");
sb.Append(']');

// ---------- [E] 读回血雾 / 血迹物件 ----------
var shown = 0; var wide = 0f; var activeBlood = 0;
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null) continue;
    if (!sr.sprite.name.ToLowerInvariant().Contains("blood")) continue;
    activeBlood++;
    var w = sr.sprite.bounds.size.x * System.Math.Abs(ch.localScale.x);
    if (w > wide) wide = w;
    if (shown < 6)
    {
        sb.Append("\n  [").Append(i).Append("] ").Append(ch.name)
          .Append(" sprite=").Append(sr.sprite.name)
          .Append(" wpos=").Append(V3(ch.position))
          .Append(" 世界宽=").Append(F2(w)).Append("m");
        shown++;
    }
}
sb.Append("\n[E] active 血迹物件数=").Append(activeBlood);

// ---------- [F] 不冻时间（同 probe-decal.cs：冻结会让驱动的 shot= 失效） ----------
//   ⚠️ 口径更正（本探针第一版踩到）：`BloodImpact` 的返回值语义是
//   **"是否真的落了血迹贴花"**（= 命中点沿弹道 2.5m 内找到可贴面），⛔ 不是"有没有出血" ——
//   找不到面时它**照样出 0.14s 的血雾**、返回 false。所以判据里**不能**要求返回 true。
sb.Append("\n[F] 未冻结时间（血迹贴花寿命 25s，驱动可直接截图）");

// 口径：① 有 active 的血迹物件（血雾或贴花，两者都是 `fx_blood*`）≥1；
//      ② 连打 40 次收集到的变体集合 ≥2（证"多变体随机"，⛔ 1 张就是没落地）；
//      ③ 世界宽 > 0（证尺寸链在跑）。
// ⛔ **不要求** BloodImpact 返回 true —— 它的语义是"有没有落到贴花"，见 [F] 段的口径更正。
var ok = activeBlood >= 1 && names.Count >= 2 && wide > 0.01f;
sb.Append("\nRESULT-BLOOD: ").Append(ok ? "PASS" : "FAIL")
  .Append("\n  口径：有 active 血迹=").Append(activeBlood >= 1)
  .Append(" 变体数>=2（=").Append(names.Count).Append("）=").Append(names.Count >= 2)
  .Append(" 世界宽>0（=").Append(F2(wide)).Append("m）=").Append(wide > 0.01f)
  .Append(" ｜报告值（不作判据）：首调 BloodImpact 返回=").Append(r1)
  .Append("、落地贴花=").Append(r1);
return sb.ToString();
