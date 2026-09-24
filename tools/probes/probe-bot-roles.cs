// 判据资产（tools/probes/）：差异 #67「机器人 AI 没有分工 / 太笨」的**实机载体**取证。
//
// 为什么要有它：本行此前的证据只有 ① 结构断言（源码里有没有角色表）② 离线自检
// （BotSelfTest.RunRoleTable，在编辑器里手点菜单跑纯函数）。缺的正是用户能感知的那一层：
// **在跑着的这一局里，场上这 8 个 bot 各自真的分到了不同角色没有**。本探针补的即是它。
//
// 判据（写死在这里，⛔ 不靠人眼看数）：
//   PASS ⇔ ① 场上 bot 数 >= 2（否则谈不上分工）
//          ② 每个"有 bot 的阵营"内部，RoleText() 的**不同取值数 >= 2**
//             （⇒ 同一队里不是"同一个人被复制 4 份"）
//          ③ 四个角色的 HoldScale 两两互异（守点时长真的不一样，不是一个数）
//          ④ StaysOnObjective 恰好 1 个为 true（守点角色唯一）
//          ⑤ 每个 bot 的 RoleText 首段 == CsBotRoles.Label(CsBotRoles.For(team, id))
//             （⇒ 运行时给的角色 与 纯函数 是同一份口径，没有"两处各写一张表"）
//
// ⛔ 只读：反射取 BotModule._brains（Dictionary<long, CsBotBrain>），只调 public 的**只读**成员
//    （Name / ActorId / RoleText / PlanRoute）。不改任何业务状态、不发包、不点按钮。
//
// 用法（编辑器须在 Play 且有一局在跑）：
//   unity command eval_file --file tools/probes/probe-bot-roles.cs
var sb = new System.Text.StringBuilder();
var inv = System.Globalization.CultureInfo.InvariantCulture;

var match = Cs16.Module.Match.MatchModule.Instance != null
    ? Cs16.Module.Match.MatchModule.Instance.Match
    : null;
if (match == null) return "ERROR: MatchModule.Instance.Match == NULL（不在对局里）";

var bm = UnityEngine.Object.FindAnyObjectByType<Cs16.Module.Bot.BotModule>(
    UnityEngine.FindObjectsInactive.Include);
if (bm == null) return "ERROR: BotModule <场景里没有>";

var bf = typeof(Cs16.Module.Bot.BotModule).GetField("_brains",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
if (bf == null) return "ERROR: BotModule._brains 取不到（改名了？）";
var dict = bf.GetValue(bm) as System.Collections.IDictionary;
if (dict == null) return "ERROR: _brains 不是 IDictionary";

sb.Append("bots=").Append(dict.Count)
  .Append(" round=").Append(match.RoundNumber)
  .Append(" phase=").Append(match.Phase)
  .Append(" running=").Append(match.IsRunning);

// ---------- 逐个 bot：名称 / 阵营 / 角色（运行时实例自己报的） ----------
var perTeam = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>();
var labelMismatch = 0;
var withHoldToEnd = 0;
var lineCount = 0;

foreach (System.Collections.DictionaryEntry kv in dict)
{
    var id = System.Convert.ToInt64(kv.Key);
    var brain = kv.Value as Cs16.Module.Bot.CsBotBrain;
    if (brain == null) continue;

    var self = match.Find(id);
    var teamName = self != null ? self.Team.ToString() : "?（actor 找不到）";
    var teamEnum = self != null ? self.Team : default(Cs16.Core.CsTeam);
    // 2026-09-24（片FIX-4 线B）：角色口径从「全局 Id % 4」改成「**队内序号** % 4」——
    //   全局 Id 会被"真人玩家在哪一队"整体位移（玩家 Id 恒最小），旧口径下 T 队恰好安全、
    //   CT 队会撞槽位。探针必须问**运行时实例自己**算出来的那个序号（brain.TeamOrdinal），
    //   ⛔ 不在这里另算一遍（否则就是"判据与被判对象两套口径"）。
    var expect = Cs16.Module.Bot.CsBotRoles.Label(Cs16.Module.Bot.CsBotRoles.For(teamEnum, brain.TeamOrdinal));

    var roleText = brain.RoleText() ?? "-";
    var head = roleText;
    var cut = roleText.IndexOf('（');
    if (cut > 0) head = roleText.Substring(0, cut);
    if (head != expect) labelMismatch++;
    if (roleText.Contains("守到底")) withHoldToEnd++;

    if (!perTeam.ContainsKey(teamName))
        perTeam[teamName] = new System.Collections.Generic.HashSet<string>();
    perTeam[teamName].Add(roleText);

    lineCount++;
    sb.Append('\n').Append("  ").Append(brain.Name)
      .Append(" id=").Append(id)
      .Append(" team=").Append(teamName)
      .Append(" 角色=").Append(roleText)
      .Append(" 期望=").Append(expect)
      .Append(head == expect ? "" : "  [不一致]")
      .Append(" 槽位=").Append(brain.PlanSlot)
      .Append('/').Append(brain.TeamOrdinal)
      .Append(" 路线=").Append(brain.PlanRoute ?? "无");
    if (lineCount >= 24) { sb.Append('\n').Append("  ...（截断）"); break; }
}

// ---------- 分工表（纯函数半：同一份口径） ----------
var roleValues = new[]
{
    Cs16.Module.Bot.CsBotRole.Breaker,
    Cs16.Module.Bot.CsBotRole.Support,
    Cs16.Module.Bot.CsBotRole.Scout,
    Cs16.Module.Bot.CsBotRole.Anchor,
};
sb.Append("\n[分工表] 角色 | 守点倍率 | 交火距离倍率 | 守到底");
var scales = new System.Collections.Generic.List<float>(4);
var stayCount = 0;
for (var i = 0; i < roleValues.Length; i++)
{
    var r = roleValues[i];
    var h = Cs16.Module.Bot.CsBotRoles.HoldScale(r);
    var g = Cs16.Module.Bot.CsBotRoles.RangeScale(r);
    var s = Cs16.Module.Bot.CsBotRoles.StaysOnObjective(r);
    scales.Add(h);
    if (s) stayCount++;
    sb.Append('\n').Append("  ").Append(Cs16.Module.Bot.CsBotRoles.Label(r))
      .Append(" | ").Append(h.ToString("F2", inv))
      .Append(" | ").Append(g.ToString("F2", inv))
      .Append(" | ").Append(s);
}
var distinctScales = new System.Collections.Generic.HashSet<string>();
for (var i = 0; i < scales.Count; i++) distinctScales.Add(scales[i].ToString("F4", inv));

// ---------- 判定 ----------
var perTeamMin = int.MaxValue;
sb.Append("\n[各队分工] ");
foreach (var kv in perTeam)
{
    if (kv.Value.Count < perTeamMin) perTeamMin = kv.Value.Count;
    sb.Append(kv.Key).Append('=').Append(kv.Value.Count).Append("种").Append("  ");
}

var okA = dict.Count >= 2;
var okB = perTeamMin >= 2;
var okC = distinctScales.Count == 4;
var okD = stayCount == 1;
var okE = labelMismatch == 0 && lineCount > 0;
var pass = okA && okB && okC && okD && okE;

sb.Append("\nRESULT-ROLES: ").Append(pass ? "PASS" : "FAIL");
sb.Append("\n  口径： bot>=2=").Append(okA)
  .Append(" 每队>=2种角色=").Append(okB).Append("(最小=").Append(perTeamMin).Append(')')
  .Append(" 四个守点倍率互异=").Append(okC).Append('(').Append(distinctScales.Count).Append("个)")
  .Append(" 守点角色唯一=").Append(okD).Append('(').Append(stayCount).Append(')')
  .Append(" 运行时==纯函数=").Append(okE).Append("(不一致=").Append(labelMismatch).Append(')');
sb.Append("\n  带「守到底」的 bot=").Append(withHoldToEnd).Append(" 只（Anchor 落到 CT 队里的证据）");
return sb.ToString();
