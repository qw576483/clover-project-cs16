// 判据资产（tools/probes/）：差异 #74 的**真集成半** —— 走一次**真实的命中链**
// `CsDamage.ApplyHit`（= CombatModule 订阅的 OnBulletHit 的唯一发出点），
// ⛔ 不是反射调 CombatEffects.BloodImpact（那是 probe-blood.cs 做的"真调用半"，两条互补）。
//
// 为什么需要 CsMatch 上的一个新测试入口：
//   `Damage` 是 **internal** 字段，离线驱动编进独立程序集 ⇒ 拿不到；
//   而"打中角色到底出不出血"这条判据必须有真调用。按 clover-engine skill §0.6 第 3 条，
//   在 CsMatch 上加 **public 类型化** 入口 `ApplyBulletHitForTest`（形状同 ApplyBombExplosionForTest）。
//
// 判据（可证伪）：
//   ① 入口返回 true（武器定义在、victim 活着）
//   ② victim 的 Health **下降**（证伤害落地那半也走了）
//   ③ FX 根下新增 ≥1 个 active 的血迹/血雾 sprite（证 OnBulletHit ⇒ BloodImpact 真跑了）
//   ①②③ 全真 ⇒ RESULT-BLOODHIT: PASS
//
// 用法（编辑器在 Play、且已有一局）：unity command eval_file --file tools/probes/probe-blood-hit.cs
var sb = new System.Text.StringBuilder();
var inv = System.Globalization.CultureInfo.InvariantCulture;
System.Func<float, string> F3 = v => v.ToString("F3", inv);
System.Func<UnityEngine.Vector3, string> V3 = v => "(" + F3(v.x) + "," + F3(v.y) + "," + F3(v.z) + ")";

var mod = UnityEngine.Object.FindAnyObjectByType<Cs16.Module.Match.MatchModule>(
    UnityEngine.FindObjectsInactive.Include);
if (mod == null) return "ERROR: MatchModule <none>";
var match = mod.Match as Cs16.Module.Match.CsMatch;
if (match == null) return "ERROR: Match 不是 CsMatch（拿到不了测试入口）";
if (match.Phase != Cs16.Core.CsRoundPhase.Live)
    return "ERROR: 不在 Live 相位（phase=" + match.Phase + "），命中链的伤害闸门会不同，本判据要求 Live";

var local = match.LocalPlayer;
if (local == null || !local.IsAlive) return "ERROR: 本地玩家为空或已阵亡";

// ---- 选一个"敌人"当受击者 ----
Cs16.Module.Match.CsActor victim = null;
var actors = match.Actors;
for (var i = 0; i < actors.Count; i++)
{
    var a = actors[i];
    if (a == null || !a.IsAlive) continue;
    if (a == local) continue;
    if (a.Team == local.Team) continue;
    victim = a;
    break;
}
if (victim == null) return "ERROR: 找不到敌方存活角色（本局只有自己？）";
sb.Append("[A] 射手=").Append(local.Name).Append(" team=").Append(local.Team)
  .Append("  受击者=").Append(victim.Name).Append(" team=").Append(victim.Team)
  .Append(" hp=").Append(victim.Health)
  .Append(" pos=").Append(V3(victim.Position)).Append(" 身高=").Append(F3(victim.Height));

// ---- FX 台账 before ----
var cms = UnityEngine.Object.FindObjectsByType<Cs16.Module.Combat.CombatModule>(
    UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
if (cms.Length == 0) return "ERROR: CombatModule <none>";
var fxField = typeof(Cs16.Module.Combat.CombatModule).GetField("_fx",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var fx = fxField != null ? fxField.GetValue(cms[0]) : null;
if (fx == null) return "ERROR: CombatModule._fx 取不到";
var rootField = fx.GetType().GetField("_root",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
var root = rootField != null ? rootField.GetValue(fx) as UnityEngine.Transform : null;
if (root == null) return "ERROR: CombatEffects._root=NULL";

System.Func<int> CountBlood = () =>
{
    var n = 0;
    for (var i = 0; i < root.childCount; i++)
    {
        var ch = root.GetChild(i);
        if (ch == null || !ch.gameObject.activeSelf) continue;
        var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
        if (sr == null || sr.sprite == null || !sr.enabled) continue;
        if (sr.sprite.name.ToLowerInvariant().Contains("blood")) n++;
    }
    return n;
};

var bloodBefore = CountBlood();
var hpBefore = victim.Health;
var chest = victim.Position + new UnityEngine.Vector3(0f, victim.Height * 0.62f, 0f);
var dx = chest.x - local.Position.x; var dz = chest.z - local.Position.z;
var dist = UnityEngine.Mathf.Sqrt(dx * dx + dz * dz);
sb.Append("\n[A] 命中点（胸口）=").Append(V3(chest)).Append("  水平距=").Append(F3(dist)).Append("m")
  .Append("\n[A] before: hp=").Append(hpBefore).Append(" 血迹物件=").Append(bloodBefore);

// ---- [B] 真调用：真实命中链 ----
var mi = typeof(Cs16.Module.Match.CsMatch).GetMethod("ApplyBulletHitForTest",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
if (mi == null) return sb.Append("\nERROR: CsMatch.ApplyBulletHitForTest 不存在（程序集是旧的？先 recompile）").ToString();
var ok = (bool)mi.Invoke(match, new object[] {
    local, victim, "ak47", Cs16.Core.CsHitbox.Chest, chest, dist });
sb.Append("\n[B] ApplyBulletHitForTest(shooter=local, victim=").Append(victim.Name)
  .Append(", weapon=ak47, box=Chest) 返回=").Append(ok);

var bloodAfter = CountBlood();
var hpAfter = victim.Health;
sb.Append("\n[C] after : hp=").Append(hpAfter).Append(" 血迹物件=").Append(bloodAfter)
  .Append("  新增血迹=").Append(bloodAfter - bloodBefore)
  .Append("  hp 变化=").Append(hpAfter - hpBefore);

// ---- 逐条列出新增血迹 ----
var names = new System.Collections.Generic.List<string>();
var shown = 0;
for (var i = 0; i < root.childCount; i++)
{
    var ch = root.GetChild(i);
    if (ch == null || !ch.gameObject.activeSelf) continue;
    var sr = ch.GetComponent<UnityEngine.SpriteRenderer>();
    if (sr == null || sr.sprite == null || !sr.enabled) continue;
    var nm = sr.sprite.name;
    if (!nm.ToLowerInvariant().Contains("blood")) continue;
    if (!names.Contains(nm)) names.Add(nm);
    if (shown < 6)
    {
        sb.Append("\n  [").Append(i).Append("] ").Append(ch.name).Append(" sprite=").Append(nm)
          .Append(" wpos=").Append(V3(ch.position))
          .Append(" 世界宽=").Append(F3(sr.sprite.bounds.size.x * UnityEngine.Mathf.Abs(ch.localScale.x))).Append("m");
        shown++;
    }
}
names.Sort(System.StringComparer.Ordinal);
sb.Append("\n[C] 血迹贴图集合=["); 
for (var i = 0; i < names.Count; i++) sb.Append(names[i]).Append(i + 1 < names.Count ? "," : "");
sb.Append(']');

// ---- 不冻时间（冻结会让驱动的 shot= 失效；血迹贴花寿命 25s） ----
sb.Append("\n[D] 未冻结时间（血迹贴花寿命 25s，驱动可直接截图）");

var hpDown = hpAfter < hpBefore;
var spawn = (bloodAfter - bloodBefore) > 0;
var pass = ok && hpDown && spawn;
sb.Append("\nRESULT-BLOODHIT: ").Append(pass ? "PASS" : "FAIL")
  .Append("\n  口径：入口返回 true=").Append(ok)
  .Append(" victim 掉血=").Append(hpDown).Append("（").Append(hpBefore).Append("→").Append(hpAfter).Append("）")
  .Append(" 新增血迹物件=").Append(spawn).Append("（+" ).Append(bloodAfter - bloodBefore).Append("）");
return sb.ToString();
