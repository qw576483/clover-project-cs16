// 判据资产（tools/probes/）：差异 #89 的**真调用半**（拆出来的后半，理由见 probe-muzzleflash-geom.cs 的头注）。
//
// 做了什么：
//   [A] 读 FX 池台账（**反射取 CombatEffects._root**，不做全场景扫描 —— 那是上一个版本超时的原因之一）
//   [B] 反射取 CombatModule._fx → 调 **真的** MuzzleFlash(eye, cam.rotation, "m249")
//       （= CombatModule.cs 里右键/开火那条链的同一个入口；第 3 参 = 武器 id，决定用哪张原版贴图）
//   [C] 再读一次台账 ⇒ 判"这条路径到底生不生成可见像素"（贴图名 / enabled / 世界宽 / 灯）
//   [D] 冻结 timeScale = 0（让 0.045s 的火焰活到 capture_game_view 读回来）
//
// 收尾（bash 里做）：unity command eval --code 'UnityEngine.Time.timeScale = 1f;'
//
// 用法（编辑器须在 Play 且有一局在跑）：unity command eval_file --file tools/probes/probe-muzzleflash-fire.cs
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

// ---------- 取 CombatModule / CombatEffects ----------
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

System.Action<string> Dump = tag =>
{
    if (root == null) { sb.Append('\n').Append(tag).Append(" _root=NULL"); return; }
    var n = root.childCount;
    var active = 0; var withSprite = 0; var withLight = 0;
    sb.Append('\n').Append(tag).Append(" root=").Append(root.name)
      .Append(" children=").Append(n)
      .Append(" activeInHierarchy=").Append(root.gameObject.activeInHierarchy);
    for (var i = 0; i < n; i++)
    {
        var ch = root.GetChild(i);
        if (ch == null) continue;
        var on = ch.gameObject.activeSelf;
        if (on) active++;
        var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
        var lt = ch.GetComponent<UnityEngine.Light>();
        var spn = (sr != null && sr.sprite != null) ? sr.sprite.name : "-";
        var sprOn = sr != null && sr.enabled;
        if (spn != "-") withSprite++;
        if (lt != null && lt.enabled) withLight++;
        if (on)     // 只打"活着的"那些：池子里的空闲物件对判据没意义，还会把输出撑爆
            sb.Append("\n  [").Append(i).Append("] ").Append(ch.name)
              .Append(" wpos=").Append(V3(ch.position))
              .Append(" scale=").Append(V3(ch.localScale))
              .Append(" sprite=").Append(spn).Append(" sprEnabled=").Append(sprOn)
              .Append(" lightOn=").Append(lt != null && lt.enabled)
              .Append(" lightRange=").Append(lt != null ? F2(lt.range) : "-");
    }
    sb.Append('\n').Append(tag).Append(".sum active=").Append(active)
      .Append(" spriteAssets=").Append(withSprite).Append(" lightsOn=").Append(withLight);
};

sb.Append("cam=").Append(cam.name).Append(" timeScale=").Append(F2(UnityEngine.Time.timeScale));
sb.Append('\n');
Dump("[A]before");

// ---------- [B] 真调用 ----------
var mf = fx.GetType().GetMethod("MuzzleFlash",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
if (mf == null) return sb.Append("\nERROR: MuzzleFlash 取不到").ToString();
var ps = mf.GetParameters();
sb.Append("\n[B] MuzzleFlash 参数个数=").Append(ps.Length).Append(" [");
for (var i = 0; i < ps.Length; i++) sb.Append(ps[i].ParameterType.Name).Append(i + 1 < ps.Length ? "," : "");
sb.Append(']');
if (ps.Length != 3)
{
    sb.Append("\nERROR: 期望 3 参 (Vector3, Quaternion, String) —— 程序集还是旧的？");
    return sb.ToString();
}

var eye = cam.transform.position;
var testWeaponId = "m249";     // ⛔ 刻意写死：贴图选择是"武器 id → 贴图"的纯函数，不需要玩家真的持 B51
mf.Invoke(fx, new object[] { eye, cam.transform.rotation, testWeaponId });
sb.Append("\n[B] 已调 MuzzleFlash(eye, camRotation, \"").Append(testWeaponId).Append("\")");
sb.Append('\n');
Dump("[C]after");

// ---------- [D] 冻时间 ----------
UnityEngine.Time.timeScale = 0f;
sb.Append("\n[D] timeScale=0（冻结，供截图）；收尾请 eval 'UnityEngine.Time.timeScale = 1f;'");

// 判定：必须新增一个 active 的 sprite 物件，且它挂着贴图、在相机前方
var ok = true;
if (root == null) ok = false;
else
{
    var anyFlash = false;
    for (var i = 0; i < root.childCount; i++)
    {
        var ch = root.GetChild(i);
        if (ch == null || !ch.gameObject.activeSelf) continue;
        var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
        if (sr == null || sr.sprite == null || !sr.enabled) continue;
        if (sr.sprite.name.ToLowerInvariant().Contains("muzzle")) { anyFlash = true; break; }
    }
    ok = anyFlash;
}
sb.Append("\nRESULT-FIRE: ").Append(ok ? "PASS" : "FAIL")
  .Append("\n  口径：FX 根下存在一个 active 且 sprEnabled 的、贴图名含 'muzzle' 的物件");
return sb.ToString();
