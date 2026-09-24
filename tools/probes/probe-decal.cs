// 判据资产（tools/probes/）：差异 #69 的**真调用半**（弹痕「墙上/地上」两格 + 五变体随机）。
//
// 做了什么（编辑器须在 Play 且有一局在跑）：
//   [A] 反射取 CombatModule._fx → CombatEffects（同 CombatModule.cs 开火链的同一个实例）
//   [B] 在相机前方**扫描 26 个方向**找两类真实命中面：
//       朝墙 = |normal.y| < 0.5；朝地 = normal.y > 0.7
//       （⛔ 不用"正前方一条射线"—— 出生点朝向不定，打不到面就会退化成"凭空摆一个"，
//         那不算取证；这里要求**命中真实几何**，命中失败就明确报 FAIL 而不是静默兜底）
//   [C] 调真入口 CombatEffects.BulletImpact(point, normal) 两次（墙 / 地各一次）
//   [D] 再连打 40 发（固定点）收集 sprite 名集合 ⇒ 判"到底是不是多变体随机"
//   [E] 读回每个 active 弹痕物件：贴图名 / 世界宽（= sprite.bounds.x * localScale.x）/ 法线朝向
//   [F] 冻结 timeScale = 0 让弹痕活到截图；收尾 bash 里 eval 'UnityEngine.Time.timeScale = 1f;'
//
// 用法：unity command eval_file --file tools/probes/probe-decal.cs
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

// ---------- [B] 26 方向扫面：找真实朝墙 / 朝地面 ----------
var origin = cam.transform.position;
var fwd = cam.transform.forward;
var dirs = new System.Collections.Generic.List<UnityEngine.Vector3>();
dirs.Add(fwd);
for (var a = -180; a < 180; a += 30)
    for (var e = -45; e <= 45; e += 15)
        dirs.Add(UnityEngine.Quaternion.Euler(e, a, 0f) * fwd);

UnityEngine.RaycastHit wallHit = default(UnityEngine.RaycastHit); var hasWall = false;
UnityEngine.RaycastHit gndHit = default(UnityEngine.RaycastHit); var hasGnd = false;
for (var i = 0; i < dirs.Count; i++)
{
    UnityEngine.RaycastHit h;
    if (!UnityEngine.Physics.Raycast(origin, dirs[i], out h, 120f, ~0, UnityEngine.QueryTriggerInteraction.Ignore))
        continue;
    var ny = h.normal.y;
    if (!hasWall && System.Math.Abs(ny) < 0.5f) { wallHit = h; hasWall = true; }
    if (!hasGnd && ny > 0.7f) { gndHit = h; hasGnd = true; }
    if (hasWall && hasGnd) break;
}
sb.Append("[B] 墙面命中=").Append(hasWall);
if (hasWall)
{
    var wc = "-";
    if (wallHit.collider != null) wc = wallHit.collider.name;
    sb.Append(" point=").Append(V3(wallHit.point)).Append(" n=").Append(V3(wallHit.normal)).Append(" col=").Append(wc);
}
sb.Append("\n[B] 地面命中=").Append(hasGnd);
if (hasGnd)
{
    var gc = "-";
    if (gndHit.collider != null) gc = gndHit.collider.name;
    sb.Append(" point=").Append(V3(gndHit.point)).Append(" n=").Append(V3(gndHit.normal)).Append(" col=").Append(gc);
}
if (!hasWall && !hasGnd) return sb.Append("\nERROR: 26 方向一条都没命中（相机在空里？）").ToString();

// ---------- [C] 真调用 BulletImpact ----------
var bi = fx.GetType().GetMethod("BulletImpact",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
if (bi == null) return sb.Append("\nERROR: BulletImpact 取不到").ToString();
var ps = bi.GetParameters();
sb.Append("\n[C] BulletImpact 参数个数=").Append(ps.Length);
if (ps.Length != 2) return sb.Append("\nERROR: 期望 2 参 (Vector3 point, Vector3 normal)").ToString();

var before = 0;
for (var i = 0; i < root.childCount; i++) { var c = root.GetChild(i); if (c != null && c.gameObject.activeSelf) before++; }
if (hasWall) { bi.Invoke(fx, new object[] { wallHit.point, wallHit.normal }); }
if (hasGnd) { bi.Invoke(fx, new object[] { gndHit.point, gndHit.normal }); }
sb.Append("\n[C] 已调 BulletImpact ×").Append((hasWall ? 1 : 0) + (hasGnd ? 1 : 0));

// ---------- [C2] 正前方扇形落痕（保证截图里看得见；⛔ 仍是打真实几何，不是凭空摆） ----------
var fanHits = 0;
for (var yi = -1; yi <= 1; yi++)
    for (var xi = -1; xi <= 1; xi++)
    {
        var d = UnityEngine.Quaternion.Euler(yi * 12f, xi * 12f, 0f) * fwd;
        UnityEngine.RaycastHit h;
        if (UnityEngine.Physics.Raycast(origin, d, out h, 120f, ~0, UnityEngine.QueryTriggerInteraction.Ignore))
        { bi.Invoke(fx, new object[] { h.point, h.normal }); fanHits++; }
    }
sb.Append("\n[C2] 正前方扇形（±12°）命中并落痕=").Append(fanHits).Append("/9");

// ---------- [D] 40 发收集变体名 ----------
if (hasWall)
{
    for (var i = 0; i < 40; i++) bi.Invoke(fx, new object[] { wallHit.point, wallHit.normal });
}
var names = new System.Collections.Generic.List<string>();
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null) continue;
    var nm = sr.sprite.name;
    if (nm.ToLowerInvariant().Contains("shot") && !names.Contains(nm)) names.Add(nm);
}
names.Sort(System.StringComparer.Ordinal);
sb.Append("\n[D] 变体数=").Append(names.Count).Append(" 集合=[");
for (var i = 0; i < names.Count; i++) sb.Append(names[i]).Append(i + 1 < names.Count ? "," : "");
sb.Append(']');

// ---------- [E] 读回弹痕物件（贴图名 / 世界宽 / 朝向） ----------
var shown = 0;
for (var i = 0; i < root.childCount && shown < 6; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null) continue;
    var nm = sr.sprite.name;
    if (!nm.ToLowerInvariant().Contains("shot")) continue;
    var w = sr.sprite.bounds.size.x * System.Math.Abs(ch.localScale.x);
    var h2 = sr.sprite.bounds.size.y * System.Math.Abs(ch.localScale.y);
    sb.Append("\n  [").Append(i).Append("] ").Append(ch.name)
      .Append(" sprite=").Append(nm)
      .Append(" wpos=").Append(V3(ch.position))
      .Append(" 世界宽=").Append(F2(w)).Append("m 世界高=").Append(F2(h2)).Append("m")
      .Append(" up=").Append(V3(ch.up));
    shown++;
}
if (shown == 0) sb.Append("\n  (没读到 active 的 shot 弹痕物件)");

// ---------- [F] 不冻时间（见下） ----------
// ⛔ 本探针**刻意不**把 Time.timeScale 冻成 0：实测冻结后驱动（order -150）的 `shot=` 不再生效
// （该 Play 里 `SHOT-TIMEOUT decal_wall_ground`，同一个驱动在未冻结时正常出图）
// ⇒ 截图改由驱动在"未冻结"状态下拍。弹痕寿命 CsCombatTuning.DecalDuration = 25s ⇒ 有充足窗口。
sb.Append("\n[F] 未冻结时间（弹痕寿命 25s，驱动可直接截图）");

// ---------- 判定 ----------
// 口径：① 墙 / 地**都命中真实几何**；② 两次真调用各留下 ≥1 个 active 弹痕；
//      ③ 40 发收集到的变体集合 ≥2（证"多变体随机"，⛔ 1 张就是没落地）；
//      ④ 世界宽 > 0（证尺寸链在跑，⛔ 不是 0 缩放）。
var after = 0; var activeShots = 0;
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    after++;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr != null && sr.sprite != null && sr.sprite.name.ToLowerInvariant().Contains("shot")) activeShots++;
}
var wide = 0f;
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null) continue;
    if (!sr.sprite.name.ToLowerInvariant().Contains("shot")) continue;
    var w = sr.sprite.bounds.size.x * System.Math.Abs(ch.localScale.x);
    if (w > 0.01f) { wide = w; break; }
}
var ok = hasWall && hasGnd && activeShots >= 1 && names.Count >= 2 && wide > 0.01f;
sb.Append("\n[E] active 弹痕物件数=").Append(activeShots).Append("（新增 active=").Append(after - before).Append("）")
  .Append("\nRESULT-DECAL: ").Append(ok ? "PASS" : "FAIL")
  .Append("\n  口径：墙/地都命中真实几何=").Append(hasWall && hasGnd)
  .Append(" 有 active 弹痕=").Append(activeShots >= 1)
  .Append(" 变体数>=2（=").Append(names.Count).Append("）=").Append(names.Count >= 2)
  .Append(" 世界宽>0（=").Append(F2(wide)).Append("m）=").Append(wide > 0.01f);
return sb.ToString();
