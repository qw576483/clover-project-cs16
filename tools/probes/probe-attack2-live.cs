// 判据资产（tools/probes/）：差异 #68「很多枪械的右键还是无效」的**实机消费点**取证。
//
// 为什么要有它：本行此前的判据只有两类 —— ① 结构断言（tools/probes/attack2-probe.py，查代码里有没有那条链）
// ② 离线自检（CombatSelfTest.cs 的「差异 #68」段）。缺的正是用户能感知的那一层：
// **在跑着的这一局里，右键那条链真的把状态翻过来了没有**。本探针补的即是它。
//
// 三段：
//   [A] 能力表：逐武器读 CsWeapons.Get(id).CanSilence / CanBurst（⛔ 不在这里抄一份"应该是什么"）
//   [B] 消费点：反射取 CsMatch.Inventory（`internal` 字段，跨程序集只能反射）→
//               对本地玩家逐个武器调 **ToggleWeaponMode**（= CombatModule 右键那条链的**唯一**出口），
//               读 CsActor.Silenced / BurstMode 的前后值 ⇒ 判"翻没翻"。
//   [C] 幂等：同一把枪连调两次必须回到原值（否则"按一次右键状态自己在抖"）
//
// ⛔ 只读业务数据：改 ActiveWeapon 只是为了把"手里那把枪"换成待测武器（换完**恢复原值**），
//    不点任何按钮、不发任何网络包、不改 state.txt。
//
// 用法（编辑器须在 Play 且有一局在跑）：
//   unity command eval_file --file tools/probes/probe-attack2-live.cs
var sb = new System.Text.StringBuilder();

var match = Cs16.Module.Match.MatchModule.Instance != null
    ? Cs16.Module.Match.MatchModule.Instance.Match
    : null;
if (match == null) return "ERROR: MatchModule.Instance.Match == NULL（不在对局里）";

var local = match.LocalPlayer;
if (local == null) return "ERROR: LocalPlayer == NULL";
if (!local.IsAlive) return "ERROR: LocalPlayer 不是活人（IsAlive=false）⇒ ToggleWeaponMode 会直接 return";

sb.Append("local=").Append(local.Name).Append(" id=").Append(local.Id)
  .Append(" team=").Append(local.Team).Append(" 手持=").Append(local.ActiveWeapon ?? "-")
  .Append(" Silenced=").Append(local.Silenced).Append(" BurstMode=").Append(local.BurstMode)
  .Append(" phase=").Append(match.Phase).Append(" running=").Append(match.IsRunning);

// ---------- [A] 能力表 ----------
// 出处表在 CsWeapons.MarkAttack2Capabilities()（USP/M4A1 = 消音、Glock18/FAMAS = 连发；
// 其余一律 false = 无出处不接）。这里**逐条读出来**，⛔ 不在探针里写死期望值 ——
// 探针只负责"报数"，期望值由人 / 断言脚本对比。
var probeWeapons = new[] { "usp", "m4a1", "glock18", "famas", "ak47", "m249", "awp", "deagle", "knife" };
sb.Append("\n[A] 能力表（CsWeapons.Get）：");
for (var i = 0; i < probeWeapons.Length; i++)
{
    var d = Cs16.Core.CsWeapons.Get(probeWeapons[i]);
    sb.Append("\n  ").Append(probeWeapons[i]).Append("=")
      .Append(d == null ? "NOT FOUND" : ("silence=" + d.CanSilence + " burst=" + d.CanBurst));
}

// ---------- [B]/[C] 消费点 ----------
var invField = typeof(Cs16.Module.Match.CsMatch).GetField("Inventory",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
if (invField == null) return sb.Append("\nERROR: CsMatch.Inventory 字段取不到（改名了？）").ToString();
var inv = invField.GetValue(match);
if (inv == null) return sb.Append("\nERROR: CsMatch.Inventory == null").ToString();

var toggle = inv.GetType().GetMethod("ToggleWeaponMode",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
if (toggle == null) return sb.Append("\nERROR: CsInventory.ToggleWeaponMode 取不到").ToString();

sb.Append("\n[B] 消费点实测（ToggleWeaponMode = 右键那条链的唯一出口）：");
var origWeapon = local.ActiveWeapon;
var flips = 0;
var idempotent = 0;
for (var i = 0; i < probeWeapons.Length; i++)
{
    var id = probeWeapons[i];
    var d = Cs16.Core.CsWeapons.Get(id);
    if (d == null) { sb.Append("\n  ").Append(id).Append(": 表里没有这把枪，跳过"); continue; }

    local.ActiveWeapon = id;
    var s0 = local.Silenced;
    var b0 = local.BurstMode;

    var args = new object[] { local, UnityEngine.Time.time };
    try { toggle.Invoke(inv, args); }
    catch (System.Exception ex)
    {
        sb.Append("\n  ").Append(id).Append(": 调用抛异常 ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
        continue;
    }

    var s1 = local.Silenced;
    var b1 = local.BurstMode;
    var changed = (s0 != s1) || (b0 != b1);
    if (changed) flips++;

    sb.Append("\n  ").Append(id)
      .Append(" CanSilence=").Append(d.CanSilence).Append(" CanBurst=").Append(d.CanBurst)
      .Append(" → Silenced ").Append(s0).Append("→").Append(s1)
      .Append(" BurstMode ").Append(b0).Append("→").Append(b1)
      .Append(changed ? "  [翻]" : "  [不动]");

    // [C] 再点一次：有能力的必须回到原值（幂等），没能力的当然也不动
    try { toggle.Invoke(inv, new object[] { local, UnityEngine.Time.time }); }
    catch (System.Exception ex) { sb.Append("  [二次调用抛异常 ").Append(ex.GetType().Name).Append(']'); continue; }

    var backOk = local.Silenced == s0 && local.BurstMode == b0;
    if (backOk) idempotent++;
    sb.Append(backOk ? "  [二次回原值 OK]" : "  [二次**没回原值**]");

    // 复原这一把枪的状态，别把状态留给下一把
    local.Silenced = s0;
    local.BurstMode = b0;
    local.ActiveWeapon = origWeapon;
}

sb.Append("\n[B/C] 汇总：翻过的武器=").Append(flips).Append("/").Append(probeWeapons.Length)
  .Append("  二次调用回原值=").Append(idempotent).Append("/").Append(probeWeapons.Length);

// 判定（口径写在探针里，避免"只看一眼日志"）：能翻的必须**恰好**是能力表里 silence||burst 的那几把。
var expected = 0;
for (var i = 0; i < probeWeapons.Length; i++)
{
    var d = Cs16.Core.CsWeapons.Get(probeWeapons[i]);
    if (d != null && (d.CanSilence || d.CanBurst)) expected++;
}
sb.Append("\n[B] 能力表里应当能翻的武器数=").Append(expected)
  .Append("（口径 = Silence||Burst 为 true 的武器；== 实测翻过的数才是对的）");

// 复原玩家手里的枪（⛔ 探针不改业务状态）
local.ActiveWeapon = origWeapon;
sb.Append("\n复原：local.ActiveWeapon=").Append(local.ActiveWeapon ?? "-");
sb.Append("\nRESULT: ").Append(flips == expected ? "PASS" : "FAIL（实测翻过的武器数 != 能力表里的可翻武器数）");
return sb.ToString();
