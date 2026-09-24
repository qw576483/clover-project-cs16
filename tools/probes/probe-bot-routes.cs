// 判据资产（tools/probes/）：差异 #67 后半 ——「**路线**都是相同的 / 没有分工」的**实机载体**取证。
//
//   为什么没有分工？？？」。上一轮交付的是"角色名不同 + 守点秒数不同"（probe-bot-roles.cs PASS），
//   而**路线与目标点**仍会撞车 ⇒ 本探针补的就是这一层。
//
// 判据（写死在这里，不靠人眼看数）：
//   PASS ⇔ 场上机器人 >= 2，且**每个"有 >=2 只 bot 的阵营"**内部满足：
//          ① 两两之间**路线标记**互不相同（0 对相同）
//          ② 两两之间**整条路线序列**（BotNavigator.RouteSignature，有序路点串）互不相同（0 对相同）
//          ③ 两两之间**目标点**互不相同（0 对相同）
//          ④ BotModule 自己的审计（AuditRouteDistinctness）报告 0 对同队撞车
//   上述 ①~④ 的**强度**取决于每队有几只 bot：4 只 ⇒ 每队 6 对，2 只 ⇒ 每对只 1 对。
//          ⑤ `RESULT-POPULATION`：**每个在场阵营的 bot 数 == 权威槽位数**
//             期望值**不写字面 4**，取产品自己的常量 <c>CsBotPlans.Slots</c>
//             （`CsBotPlans.cs:78` = `public const int Slots = 4`；同文件 `:304-308` 的
//              `AliveSlotTable` 就是 `new bool[Slots]` ⇒ 它就是门禁的槽位表长度），
//             并要求**两队都在场**（T/CT 的计划表是两套不同映射，缺一队该队的表根本没被测）。
//   判据原写法是"**首段**路点不同"，实测**在 T 队不可满足且与代码无关**：
//      `Resources/MapData/de_dust2_markers.bytes` 里 Route_T_To_A ∩ Route_T_Mid ∩ Route_T_To_B
//      共用同一个岔口点 (-7.5, 3.251, -47.5)（离 T 出生点最近）⇒ 最近邻排序后三条路的第 0 段必然相同。
//      ⇒ 本探针把"首段路点"降为**观测列**（照打），硬判据换成"整条序列不同"
//      （两条路一旦分岔序列就不同；两条路完全相同则序列相同 —— 判据依然能失败）。
//   只读：反射取 BotModule._brains，只读 public 的只读成员（PlanRoute / RouteSignature / PlanSlot /
//      TeamOrdinal / GoalPosition / FirstWaypoint / RoleText / Name / ActorId）。不改业务状态、不发包、不点按钮。
//
//   它是本判据的**负控**：反事实那一栏必须出现 >0 的撞车对数，否则说明这批样本根本区分不出新旧
//   （"负控打空"），本探针的 PASS 也就不足为凭。
//
// 用法（编辑器须在 Play 且有一局在跑）：
//   unity command eval_file --file tools/probes/probe-bot-routes.cs --format tsv
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

sb.Append("ROUTES bots=").Append(dict.Count)
  .Append(" round=").Append(match.RoundNumber)
  .Append(" phase=").Append(match.Phase)
  .Append(" running=").Append(match.IsRunning);

// ---------- 逐个 bot：路线 / 首段路点 / 目标点 / 角色（全部由运行时实例自己报） ----------
// 列：bot \t id \t team \t 槽位 \t 队内序号 \t 角色 \t 路线 \t 路点数 \t 首段路点 \t 目标点 \t 站点 \t 出生点 \t 路线序列签名
sb.Append("\n#\tbot\tid\tteam\tslot\tordinal\trole\troute\twaypoints\tfirstWaypoint\tgoal\tisSite\tpos\tsignature");

var rows = new System.Collections.Generic.List<string[]>();
var legacyRows = new System.Collections.Generic.List<string[]>();
var lineCount = 0;

foreach (System.Collections.DictionaryEntry kv in dict)
{
    var id = System.Convert.ToInt64(kv.Key);
    var brain = kv.Value as Cs16.Module.Bot.CsBotBrain;
    if (brain == null) continue;

    var self = match.Find(id);
    if (self == null) continue;

    var teamName = self.Team.ToString();
    var roleText = brain.RoleText() ?? "-";
    var head = roleText;
    var cut = roleText.IndexOf('（');
    if (cut > 0) head = roleText.Substring(0, cut);

    var first = brain.HasFirstWaypoint
        ? string.Format(inv, "({0:F1},{1:F1},{2:F1})", brain.FirstWaypoint.x, brain.FirstWaypoint.y, brain.FirstWaypoint.z)
        : "NONE";
    var goal = string.Format(inv, "({0:F1},{1:F1},{2:F1})",
        brain.GoalPosition.x, brain.GoalPosition.y, brain.GoalPosition.z);
    var pos = string.Format(inv, "({0:F1},{1:F1},{2:F1})", self.Position.x, self.Position.y, self.Position.z);

    lineCount++;
    sb.Append('\n').Append(lineCount)
      .Append('\t').Append(brain.Name)
      .Append('\t').Append(id)
      .Append('\t').Append(teamName)
      .Append('\t').Append(brain.PlanSlot)
      .Append('\t').Append(brain.TeamOrdinal)
      .Append('\t').Append(head)
      .Append('\t').Append(brain.PlanRoute ?? "NONE")
      .Append('\t').Append(brain.WaypointCount)
      .Append('\t').Append(first)
      .Append('\t').Append(goal)
      .Append('\t').Append(brain.GoalIsSite)
      .Append('\t').Append(pos)
      .Append('\t').Append(brain.RouteSignature);

    rows.Add(new[] { teamName, brain.PlanRoute ?? "NONE", brain.RouteSignature, first, goal });

    // ---- 旧规则反事实：`Id % 4` 落到**只有 3 条**的路池（T: idx2=中路、idx3=另一包点、其余=主攻路；
    //      CT: idx%3 ⇒ 0=守A、1=守B、2=中路）。只重算"确定性"的两列（路线 / 目标标记）。
    var siteIsA = (match.RoundNumber % 2) == 0;
    var idx = (int)(id % 4);
    string lgRoute, lgGoalMarker;
    if (self.Team == Cs16.Core.CsTeam.T)
    {
        if (idx == 2) { lgRoute = "Route_T_Mid"; lgGoalMarker = siteIsA ? "Bombsite_A" : "Bombsite_B"; }
        else if (idx == 3) { lgRoute = siteIsA ? "Route_T_To_B" : "Route_T_To_A"; lgGoalMarker = siteIsA ? "Bombsite_B" : "Bombsite_A"; }
        else { lgRoute = siteIsA ? "Route_T_To_A" : "Route_T_To_B"; lgGoalMarker = siteIsA ? "Bombsite_A" : "Bombsite_B"; }
    }
    else if (self.Team == Cs16.Core.CsTeam.CT)
    {
        var pick = idx % 3;
        if (pick == 0) { lgRoute = "Route_CT_To_A"; lgGoalMarker = "Bombsite_A"; }
        else if (pick == 1) { lgRoute = "Route_CT_To_B"; lgGoalMarker = "Bombsite_B"; }
        else { lgRoute = "Route_CT_Mid"; lgGoalMarker = "Route_CT_Mid"; }
    }
    else { lgRoute = "NONE"; lgGoalMarker = "NONE"; }

    legacyRows.Add(new[] { teamName, lgRoute, lgGoalMarker });
}

// ---------- 同队两两比较 ----------
System.Func<System.Collections.Generic.List<string[]>, int, System.Collections.Generic.Dictionary<string, int>> dup =
    delegate (System.Collections.Generic.List<string[]> list, int col)
    {
        var res = new System.Collections.Generic.Dictionary<string, int>();
        for (var i = 0; i < list.Count; i++)
        for (var j = i + 1; j < list.Count; j++)
        {
            if (list[i][0] != list[j][0]) continue;                 // 只看同队
            if (list[i][col] != list[j][col]) continue;
            var key = list[i][0] + "×" + list[i][1];
            res[key] = res.ContainsKey(key) ? res[key] + 1 : 1;
        }
        return res;
    };

var dupRoute = dup(rows, 1);
var dupSig = dup(rows, 2);
var dupFirst = dup(rows, 3);
var dupGoal = dup(rows, 4);
var lgRouteDup = dup(legacyRows, 1);
var lgGoalDup = dup(legacyRows, 2);

// ================= 负控B · 门禁失效（探针侧反事实 · 零产品改动 · 与上面的正样本同源） =================
// 门禁（CsBotBrain.TryRouteObjective:833-848）做的事：从自己的槽位起 `(from + k) % 4` 轮转，
//   **跳过 `taken[]` 里被「活着的」队友占着的槽位**；`taken` 由 CsBotPlans.AliveSlotTable 产出。
// 反事实 = 把那个世界换成"没有活人"：直接把 `actors = null` 喂给**产品自己的** AliveSlotTable ⇒ 它
//   在 :309 立刻 `return` 全 false 的表 ⇒ 门禁失效 ⇒ 每一只 bot 的第一步 k=1 必被接受 ⇒ 拿 (from+1)%4。
//   走哪条路**仍然问产品自己的表** `CsBotPlans.For(team, slot, round).RouteMarker`（不另造第二套映射）。
// 撞车口径：某只**活** bot 的"无门禁下一格"正好落在**另一只活队友此刻正走的**路上 ⇒ 那就是门禁此刻在挡的撞车。
//   （活人占用位一并打出来：喂 null 得到理应全 false，若不为 0 说明 pin 没生效。）
// main agent 的硬要求：反事实**取不到就 NEGCTL-UNAVAILABLE + 总判据 FAIL**，不许把"跑不出来"读成"通过"。
var gateOffDup = new System.Collections.Generic.Dictionary<string, int>();
var negCtlUnavailable = false;
var negCtlSlots = -1;        // 槽位数（取不到 = -1）
var negCtlLiveTaken = -1;    // 活人占用位数（真实世界）
var negCtlOffTaken = -1;     // 喂 null 之后的占用位数（必须 = 0，否则 pin 没生效）
var negCtlSample = -1;       // 参与反事实的活 bot 数
try
{
    var liveRoutes = new System.Collections.Generic.List<string[]>();
    foreach (System.Collections.DictionaryEntry kv2 in dict)
    {
        var id2 = System.Convert.ToInt64(kv2.Key);
        var br = kv2.Value as Cs16.Module.Bot.CsBotBrain;
        if (br == null) continue;
        var ac = match.Find(id2);
        if (ac == null) continue;
        if (!ac.IsAlive) continue;                 // 死人不动 ⇒ 与产品同口径（只判活人）
        if (match.Actors == null) { negCtlUnavailable = true; break; }
        if (br.PlanSlot < 0) continue;

        var tn = ac.Team.ToString();
        var carrier2 = Cs16.Module.Bot.CsBotPlans.RoundCarrierOrdinal(
            match.Actors, match, ac.Team, match.RoundNumber);

        // 真实世界（门禁生效）
        var takenLive = Cs16.Module.Bot.CsBotPlans.AliveSlotTable(
            match.Actors, ac.Team, carrier2, match.RoundNumber, null);
        // 反事实世界（门禁失效）：actors = null ⇒ AliveSlotTable:309 返回全 false
        var takenOff = Cs16.Module.Bot.CsBotPlans.AliveSlotTable(
            null, ac.Team, carrier2, match.RoundNumber, null);

        negCtlSlots = takenOff.Length;
        negCtlLiveTaken = 0;
        for (var q = 0; q < takenLive.Length; q++) if (takenLive[q]) negCtlLiveTaken++;
        negCtlOffTaken = 0;
        for (var q = 0; q < takenOff.Length; q++) if (takenOff[q]) negCtlOffTaken++;

        // 门禁失效的选择规则第一步：k=1 ⇒ slot=(from+1)%Slots，takenOff 全 false ⇒ 立刻被接受
        var ungatedSlot = ((br.PlanSlot + 1) % Cs16.Module.Bot.CsBotPlans.Slots
                           + Cs16.Module.Bot.CsBotPlans.Slots) % Cs16.Module.Bot.CsBotPlans.Slots;
        var ungatedRoute = Cs16.Module.Bot.CsBotPlans.For(ac.Team, ungatedSlot, match.RoundNumber).RouteMarker;
        if (string.IsNullOrEmpty(ungatedRoute)) continue;   // 产品表该槽位没路线 ⇒ 这一步不会被接受（照产品规则）

        liveRoutes.Add(new[] { tn, ungatedRoute, br.PlanRoute ?? "NONE", br.Name, ungatedSlot.ToString() });
    }
    negCtlSample = liveRoutes.Count;

    // 同队两两：我的"无门禁目标路" == 他的"此刻正走的路" ⇒ 门禁挡下的撞车
    for (var i = 0; i < liveRoutes.Count; i++)
    for (var j = 0; j < liveRoutes.Count; j++)
    {
        if (i == j) continue;
        if (liveRoutes[i][0] != liveRoutes[j][0]) continue;      // 只看同队
        if (liveRoutes[i][1] != liveRoutes[j][2]) continue;
        var key = liveRoutes[i][3] + "（" + liveRoutes[i][0] + "/槽位" + liveRoutes[i][4]
                  + "）无门禁会轮转到 '" + liveRoutes[i][1] + "'，而 " + liveRoutes[j][3] + " 此刻正走着它";
        gateOffDup[key] = gateOffDup.ContainsKey(key) ? gateOffDup[key] + 1 : 1;
    }
}
catch (System.Exception ex)
{
    negCtlUnavailable = true;
    sb.Append("\n[负控B · 门禁失效] NEGCTL-UNAVAILABLE: ")
      .Append(ex.GetType().Name).Append(": ").Append(ex.Message);
}

// ---------- BotModule 自己的审计（运行期每 1s 一次，见 AuditRouteDistinctness） ----------
var auditPairs = bm.RouteCollisionPairs;
var auditOk = bm.RouteDistinctOk;

// ---------- 每队 bot 数 ----------
var perTeamCount = new System.Collections.Generic.Dictionary<string, int>();
for (var i = 0; i < rows.Count; i++)
{
    var t = rows[i][0];
    perTeamCount[t] = perTeamCount.ContainsKey(t) ? perTeamCount[t] + 1 : 1;
}
var multiTeams = 0;
foreach (var kv in perTeamCount) if (kv.Value >= 2) multiTeams++;

// 为什么必须**独立**：okA 只要求 ">=2 只 bot 且 >=1 个多 bot 阵营" ⇒ 退化成一队 4 只 + 一队 2 只、
//   甚至 2v2 也照样 PASS，而"同队两两路线互异"的强度 = 每队对数 C(n,2) 完全由人口决定。
// 期望值**不写死字面 4**，取产品自己的权威常量 CsBotPlans.Slots（CsBotPlans.cs:78 的
//   `public const int Slots = 4`；`CsBotPlans.cs:304-308` 的 AliveSlotTable 就是 `new bool[Slots]`
//   ⇒ 它就是门禁判"这个槽位有没有被活着的队友占"时用的表长度）。
// 双条件：① 两队都在场（T / CT 是两套**不同**的槽位→路线映射，缺一队 ⇒ 那张表根本没被测）；
//        ② 每个在场阵营的 bot 数 == Slots（少 ⇒ 判据强度不足；多 ⇒ 按鸽巢原理必有人无槽位）。
var expectedPerTeam = Cs16.Module.Bot.CsBotPlans.Slots;
var teamKeys = new System.Collections.Generic.List<string>(perTeamCount.Keys);
teamKeys.Sort(System.StringComparer.Ordinal);
var ptParts = new System.Collections.Generic.List<string>();
var popBad = new System.Collections.Generic.List<string>();
var okPopCount = true;
for (var i = 0; i < teamKeys.Count; i++)
{
    var tn3 = teamKeys[i];
    var n3 = perTeamCount[tn3];
    ptParts.Add(tn3 + ":" + n3);
    if (n3 != expectedPerTeam) { okPopCount = false; popBad.Add(tn3 + "=" + n3); }
}
var perTeamToken = ptParts.Count > 0 ? string.Join(",", ptParts.ToArray()) : "NA";
var okPopTeams = teamKeys.Count == 2;
var okPop = okPopCount && okPopTeams;

// ---------- 判定 ----------
var okA = rows.Count >= 2 && multiTeams >= 1;
var okB = dupRoute.Count == 0;
var okC = dupSig.Count == 0;
var okD = dupGoal.Count == 0;
var okE = auditOk && auditPairs == 0;
var pass = okA && okB && okC && okD && okE;

sb.Append("\n[AUDIT] BotModule.RouteDistinctOk=").Append(auditOk)
  .Append(" RouteCollisionPairs=").Append(auditPairs);
sb.Append("\n[各队 bot 数] ");
foreach (var kv in perTeamCount) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("  ");

sb.Append("\n[同队撞车（新实现 · 硬判据）] 路线=").Append(dupRoute.Count)
  .Append(" 对 路线序列=").Append(dupSig.Count)
  .Append(" 对 目标点=").Append(dupGoal.Count).Append(" 对");
sb.Append("\n[观测列（非硬判据）] 首段路点相同=").Append(dupFirst.Count)
  .Append(" 对 —— T 队三条路共用岔口点 (-7.5, 3.251, -47.5)（素材层，见文件头 ⚠️；可满足的等价判据 = 路线序列）");
foreach (var kv in dupRoute) sb.Append("\n   路线撞车: ").Append(kv.Key).Append(" ×").Append(kv.Value).Append(' ').Append(kv.Key);
foreach (var kv in dupSig) sb.Append("\n   路线序列撞车: ").Append(kv.Key).Append(" ×").Append(kv.Value);
foreach (var kv in dupGoal) sb.Append("\n   目标点撞车: ").Append(kv.Key).Append(" ×").Append(kv.Value);

sb.Append("\n[负控A · 旧规则反事实（Id%4 + 3 条路池）· 旁证] 路线=").Append(lgRouteDup.Count)
  .Append(" 对 目标标记=").Append(lgGoalDup.Count).Append(" 对");
foreach (var kv in lgRouteDup) sb.Append("\n   旧规则路线撞车: ").Append(kv.Key).Append(" ×").Append(kv.Value);

// ================= 负控B 的打印与判定（取不到 / 打空 ⇒ 一律红） =================
sb.Append("\n[负控B · 门禁失效（探针反事实 · 与上面正样本**同源**：同一次 Play / 同一批 bot / 同一时段）]")
  .Append(" 活 bot 样本=").Append(negCtlSample)
  .Append(" 沿用的槽位数=").Append(negCtlSlots)
  .Append(" 真实世界活人占用=").Append(negCtlLiveTaken).Append('/').Append(negCtlSlots)
  .Append(" 喂 null(门禁失效)后占用=").Append(negCtlOffTaken).Append('/').Append(negCtlSlots)
  .Append(" ⇒ 门禁失效撞车=").Append(gateOffDup.Count).Append(" 对");
foreach (var kv in gateOffDup) sb.Append("\n   门禁失效撞车: ").Append(kv.Key).Append(" ×").Append(kv.Value);

// 负控A（旧规则）不能"打空"：这批样本必须能区分新旧 —— 否则正样本的 PASS 不足为凭
var negCtlEffective = (lgRouteDup.Count + lgGoalDup.Count) > 0;
// 负控B（门禁层）：必须有数（不是 NEGCTL-UNAVAILABLE）、喂 null 后必须真的全空、且必须 >0 撞车
var negCtlPinOk = (negCtlSlots > 0) && (negCtlOffTaken == 0) && (negCtlLiveTaken > 0);
var negCtlB = (!negCtlUnavailable) && negCtlPinOk && (gateOffDup.Count > 0);
var passTotal = pass && negCtlB && okPop;

sb.Append("\nRESULT-ROUTES: ").Append(pass ? "PASS" : "FAIL");
sb.Append("\n  口径： 同队>=2只=").Append(okA).Append("(队数=").Append(multiTeams).Append('/').Append(rows.Count).Append("只)")
  .Append(" 路线互异=").Append(okB).Append('(').Append(dupRoute.Count).Append(" 对)")
  .Append(" 路线序列互异=").Append(okC).Append('(').Append(dupSig.Count).Append(" 对)")
  .Append(" 目标点互异=").Append(okD).Append('(').Append(dupGoal.Count).Append(" 对)")
  .Append(" 运行时审计通过=").Append(okE)
  .Append("  ← 本行只判「路线互异」，不含负控");
sb.Append("\nRESULT-NEGCTL-LEGACY: ").Append(negCtlEffective ? "PASS" : "FAIL")
  .Append("（旁证 · 旧规则反事实必须出现 >0 撞车，否则负控打空；实际 路线+目标 = ")
  .Append(lgRouteDup.Count + lgGoalDup.Count).Append("）");
// 本判据的负控 = 门禁这一层。取不到(NEGCTL-UNAVAILABLE) / pin 没生效 / 打空(0 对) ⇒ 一律 FAIL
sb.Append("\nRESULT-NEGCTL: ")
  .Append(negCtlUnavailable ? "FAIL（NEGCTL-UNAVAILABLE）" : (negCtlB ? "PASS" : "FAIL"))
  .Append("（门禁失效反事实必须 >0 撞车；实际 ").Append(gateOffDup.Count).Append(" 对")
  .Append("；pin 生效=").Append(negCtlPinOk)
  .Append("；取到=").Append(!negCtlUnavailable).Append("）");
// 独立判据 ⑤：人口。望值取自产品常量 CsBotPlans.Slots（CsBotPlans.cs:78），不是字面 4。
//   perTeam 这个 token 是给驱动 phase-6 的 GATE 行读的（`perTeam=CT:4,T:4`）—— 驱动不许自己编人口。
sb.Append("\nRESULT-POPULATION: ").Append(okPop ? "PASS" : "FAIL")
  .Append("（perTeam=").Append(perTeamToken)
  .Append(" expected=").Append(expectedPerTeam)
  .Append(" 在场队数=").Append(teamKeys.Count)
  .Append(" / 口径：期望每队人数 = CsBotPlans.Slots（CsBotPlans.cs:78，即门禁槽位表 AliveSlotTable 的长度）；"
    + "两队必须都在场；任一队人数 != 期望 ⇒ 本行 FAIL");
if (!okPopCount) sb.Append(" 人数不符队=").Append(string.Join(" ", popBad.ToArray()));
sb.Append("）");
sb.Append("\nRESULT-FIX4B: ").Append(passTotal ? "PASS" : "FAIL")
  .Append("\n  口径： 总判据 = RESULT-ROUTES && RESULT-NEGCTL && RESULT-POPULATION ⇒ 负控取不到 / pin 没生效 / 打空 / 每队人数不符 时本行一律 FAIL");
return sb.ToString();
