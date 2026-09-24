// 判据资产（tools/probes/）：差异 #89「枪口火焰没有效果」的**存在性 + 可见性几何**只读实测。
//
// 为什么必须进 Play：这是"某一帧上到底有没有那个 FX 物件、它在相机的哪一侧、有没有被视模型盖住"
// 的问题，只有活相机 + 活实例能给；离线渲染不了（原版 mdl 不在盘、工程渲染走引擎 UI 层）。
//
// 三段（每一段各留一行判据，不许合并成"看起来还行"）：
//   [A] 台账：FX 根下的每一个池内物件（名字/激活/世界坐标/缩放/贴图名/贴图启用）
//   [B] 几何：**视模型动画后**的世界 AABB（SkinnedMeshRenderer.bounds，含蒙皮）换算到相机局部系，
//            与 CombatEffects 里**硬编码**的火焰落点 (dir*0.34 + right*0.13 + down*0.09) 对比
//            ⇒ 判"火焰在不在枪管里 / 在不在相机前方 / 投到屏幕哪一格"
//   [C] 真调用：反射取 CombatModule._fx 调 MuzzleFlash(eye, aim)（**就是** CombatModule.cs:508 那一个
//            入口），再扫描一次台账 ⇒ 判"这条路径到底生不生成可见像素"；成功即冻结 timeScale
//            （让这一帧能截图；Time.deltaTime 归零 ⇒ CombatEffects.Tick 不会把 Life 减完）。
//
// 只读：不点按钮、不改 state.txt、不改业务代码；唯一写是 timeScale（为了截图，收尾请调
//    `unity command eval --code 'UnityEngine.Time.timeScale = 1f;'` 复原）。
//
// 用法（编辑器必须已在 Play 且有一局在跑）：
//   unity command eval_file --file tools/probes/probe-muzzleflash.cs
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();
System.Func<float, string> F2 = v => v.ToString("F4", inv);
System.Func<UnityEngine.Vector3, string> V3 = v => "(" + F2(v.x) + "," + F2(v.y) + "," + F2(v.z) + ")";

// ---------- 相机 ----------
var cams = UnityEngine.Object.FindObjectsByType<UnityEngine.Camera>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
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

var w = UnityEngine.Screen.width;
var h = UnityEngine.Screen.height;
sb.Append("cam=").Append(cam.name)
  .Append(" screen=").Append(w).Append('x').Append(h)
  .Append(" fovY=").Append(F2(cam.fieldOfView))
  .Append(" near=").Append(F2(cam.nearClipPlane))
  .Append(" far=").Append(F2(cam.farClipPlane))
  .Append(" eye=").Append(V3(cam.transform.position))
  .Append(" timeScale=").Append(F2(UnityEngine.Time.timeScale));

// ---------- FX 根 + 池台账 ----------
UnityEngine.Transform fxRoot = null;
var allTr = UnityEngine.Object.FindObjectsByType<UnityEngine.Transform>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
for (var i = 0; i < allTr.Length; i++)
    if (allTr[i] != null && allTr[i].name == "CsCombatFX") { fxRoot = allTr[i]; break; }

System.Action<string> DumpFx = (tag) =>
{
    if (fxRoot == null) { sb.Append('\n').Append(tag).Append(" fxRoot=NULL"); return; }
    var n = fxRoot.childCount;
    var active = 0;
    var withSprite = 0;
    var withLight = 0;
    sb.Append('\n').Append(tag).Append(" children=").Append(n)
      .Append(" rootActive=").Append(fxRoot.gameObject.activeInHierarchy);
    for (var i = 0; i < n; i++)
    {
        var ch = fxRoot.GetChild(i);
        if (ch == null) continue;
        var on = ch.gameObject.activeSelf;
        if (on) active++;
        var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
        var lt = ch.GetComponent<UnityEngine.Light>();
        var spn = (sr != null && sr.sprite != null) ? sr.sprite.name : "-";
        var sprOn = sr != null && sr.enabled;
        if (spn != "-") withSprite++;
        if (lt != null && lt.enabled) withLight++;
        sb.Append('\n').Append("  [").Append(i).Append("] ").Append(ch.name)
          .Append(" active=").Append(on)
          .Append(" wpos=").Append(V3(ch.position))
          .Append(" scale=").Append(V3(ch.localScale))
          .Append(" sprite=").Append(spn)
          .Append(" sprEnabled=").Append(sprOn)
          .Append(" lightOn=").Append(lt != null && lt.enabled)
          .Append(" lightRange=").Append(lt != null ? F2(lt.range) : "-");
    }
    sb.Append('\n').Append(tag).Append(".sum active=").Append(active)
      .Append(" spriteAssets=").Append(withSprite)
      .Append(" lightsOn=").Append(withLight);
};

sb.Append('\n');
DumpFx("[A]before");

// ---------- [B] 视模型动画后包围盒（相机局部系） ----------
UnityEngine.Transform vmRoot = null;
var kids = cam.transform.GetComponentsInChildren<UnityEngine.Transform>(true);
for (var i = 0; i < kids.Length; i++)
    if (kids[i] != null && kids[i].name == "CsViewModel") { vmRoot = kids[i]; break; }

var vmInFrustumHint = "vm=<none>";
if (vmRoot != null)
{
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
    if (found > 0)
    {
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
        // 在里面 = 火焰被放进枪身 ⇒ 透明队列 + 深度测试下被近端枪身几何盖掉 = 用户报的"没有效果"。
        var fwd = cam.transform.forward.normalized;
        var rgt = UnityEngine.Vector3.Cross(UnityEngine.Vector3.up, fwd).normalized;
        var oldLocal = cam.transform.InverseTransformPoint(
            cam.transform.position + fwd * 0.34f + rgt * 0.13f + UnityEngine.Vector3.down * 0.09f);
        var oldInside = oldLocal.x >= lLo.x && oldLocal.x <= lHi.x
                     && oldLocal.y >= lLo.y && oldLocal.y <= lHi.y
                     && oldLocal.z >= lLo.z && oldLocal.z <= lHi.z;

        vmInFrustumHint = "vm smr=" + found + " worldAABB[min=" + V3(lo) + " max=" + V3(hi) + "]"
            + " camLocal[min=" + V3(lLo) + " max=" + V3(lHi) + "]"
            + " (x=右 y=上 z=前) | 旧落点 camLocal=" + V3(oldLocal) + " 在包围盒内=" + oldInside;
        sb.Append('\n').Append("[B]").Append(vmInFrustumHint);
    }
    else
    {
        sb.Append('\n').Append("[B] vm smr=0（视模型没挂蒙皮渲染器 / 不在 hierarchy）");
    }
}
else
{
    sb.Append('\n').Append("[B] CsViewModel <none under camera>");
}

// ---------- [C] 真调用 MuzzleFlash ----------
var match = Cs16.Module.Match.MatchModule.Instance != null
    ? Cs16.Module.Match.MatchModule.Instance.Match
    : null;
var local = match != null ? match.LocalPlayer : null;
sb.Append('\n').Append("[C] match=").Append(match != null ? "ok" : "NULL")
  .Append(" running=").Append(match != null && match.IsRunning)
  .Append(" local=").Append(local != null ? local.Id.ToString() : "NULL")
  .Append(" weapon=").Append(local != null ? (local.ActiveWeapon ?? "-") : "-");

var eye = cam.transform.position;
var aim = cam.transform.forward.normalized;
var right = UnityEngine.Vector3.Cross(UnityEngine.Vector3.up, aim).normalized;

// 判据是"它落在视模型包围盒里面"（= 被枪身几何盖掉 ⇒ 用户看到"没有效果"）。
var oldPos = eye + aim * 0.34f + right * 0.13f + UnityEngine.Vector3.down * 0.09f;
var oldSp = cam.WorldToScreenPoint(oldPos);
sb.Append('\n').Append("[C] 旧落点 pos=").Append(V3(oldPos))
  .Append(" camLocal=").Append(V3(cam.transform.InverseTransformPoint(oldPos)))
  .Append(" screenPoint=").Append(V3(oldSp))
  .Append(" dist=").Append(F2(UnityEngine.Vector3.Distance(eye, oldPos)));

// `CsCombatTuning` 是 **internal**，而 eval_file 编译出来的是**另一个程序集** ⇒ 直接写
//    `CsCombatTuning.MuzzleOffsetForward` 编译不过；用反射读**同一个常量**（不在探针里抄数字）。
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
sb.Append('\n').Append("[C] CsCombatTuning(反射) 偏移 right=").Append(F2(offR))
  .Append(" up=").Append(F2(offU)).Append(" forward=").Append(F2(offF))
  .Append(" 期望米宽=").Append(F2(sizeM)).Append(" type=").Append(tune != null ? tune.FullName : "NOT FOUND");

var flashPos = eye + cam.transform.rotation * new UnityEngine.Vector3(offR, offU, offF);
var sp = cam.WorldToScreenPoint(flashPos);
sb.Append('\n').Append("[C] 新落点 pos=").Append(V3(flashPos))
  .Append(" camLocal=").Append(V3(cam.transform.InverseTransformPoint(flashPos)))
  .Append(" screenPoint=").Append(V3(sp))
  .Append(" onScreen=").Append(sp.z > 0f && sp.x >= 0f && sp.x <= w && sp.y >= 0f && sp.y <= h)
  .Append(" dist=").Append(F2(UnityEngine.Vector3.Distance(eye, flashPos)));

var cm = UnityEngine.Object.FindAnyObjectByType<Cs16.Module.Combat.CombatModule>(
    UnityEngine.FindObjectsInactive.Include);
if (cm == null)
{
    sb.Append('\n').Append("[C] CombatModule <none> ⇒ 不能真调用");
    return sb.ToString();
}

var fxField = typeof(Cs16.Module.Combat.CombatModule).GetField("_fx",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var fx = fxField != null ? fxField.GetValue(cm) : null;
if (fx == null)
{
    sb.Append('\n').Append("[C] CombatModule._fx 取不到");
    return sb.ToString();
}
var mf = fx.GetType().GetMethod("MuzzleFlash", System.Reflection.BindingFlags.Public |
    System.Reflection.BindingFlags.Instance);
if (mf == null)
{
    sb.Append('\n').Append("[C] MuzzleFlash 方法取不到");
    return sb.ToString();
}
// 第 3 参**刻意写死 `m249`**：贴图选择是"武器 id → 贴图"的**纯函数**
//    （`CombatEffects.PickMuzzleFlash`），不需要玩家真的持 B51 —— 驱动侧走业务 `TryBuyFor`
//    会撞 `mp_buytime`（15s）买枪窗，而 `unity` CLI 每次往返的耗时不可控（实测被窗口睡过去）。
//    这里直接点验用户点名的那格（B51 = 十字形 fx_muzzleflash3）。
var testWeaponId = "m249";
mf.Invoke(fx, new object[] { eye, cam.transform.rotation, testWeaponId });
sb.Append('\n').Append("[C] 已调 MuzzleFlash(eye, camRotation, \"").Append(testWeaponId)
  .Append("\")（= CombatModule.cs 的同一入口；第 3 参 = 武器 id，决定用哪张原版贴图。" +
          "玩家实际手持=").Append(local != null ? (local.ActiveWeapon ?? "-") : "-").Append("）");
sb.Append('\n');
DumpFx("[C]after");

// 冻结时间：Time.deltaTime -> 0 ⇒ CombatEffects.Tick 不减 Life ⇒ 这一帧可被 capture_game_view 采到
UnityEngine.Time.timeScale = 0f;
sb.Append('\n').Append("[C] timeScale=0（冻结，供截图）；收尾请 eval 'UnityEngine.Time.timeScale = 1f;'");
return sb.ToString();
