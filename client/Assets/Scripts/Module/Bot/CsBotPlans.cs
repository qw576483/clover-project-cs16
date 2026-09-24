using System.Collections.Generic;
using System.Text;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 一个"本轮计划槽位"落成的完整计划：**走哪条路 + 去哪个点 + 担任什么角色**。
    /// 由 <see cref="CsBotPlans.For"/> 纯函数生成（同一输入永远同一结果，可离线段言）。
    /// </summary>
    public struct CsBotPlan
    {
        /// <summary>槽位号 0..<see cref="CsBotPlans.Slots"/>-1（决定路线与目标点）。</summary>
        public int Slot;

        /// <summary>该槽位的战术角色（<see cref="CsBotRoles"/>）。</summary>
        public CsBotRole Role;

        /// <summary>路线标记名（<see cref="CsMarkers"/> 常量；写进 <c>BotNavigator.SetRoute</c>）。</summary>
        public string RouteMarker;

        /// <summary>目标点所在的地图标记名（包点 / 该路线 / 巡逻）。</summary>
        public string GoalMarker;

        /// <summary>在 <see cref="GoalMarker"/> 的点集里取第几个（**确定性序**：按 (x,z) 排序后取第
        /// <c>ordinal % count</c> 个，再向后找第一个"走得到"的）。同一个标记被两个槽位共用时，
        /// 靠这个序号保证两个槽位的**目标点不同**。</summary>
        public int GoalOrdinal;

        /// <summary>目标是否"包点"（决定停步半径 / 是否构造守位表，与既有 <c>_goalIsSite</c> 同义）。</summary>
        public bool GoalIsSite;

        /// <summary>中文路线名（日志与探针用，便于人眼核对"这两只 bot 走的是不是同一条路"）。</summary>
        public string RouteLabel;

        /// <summary>中文目标名（同上）。</summary>
        public string GoalLabel;
    }

    /// <summary>
    /// 机器人**本轮路线计划表** —— 本项目新增（原版 CS 1.6 本体无 bot AI，同 <see cref="CsBotRoles"/> 的说明；
    /// 差异登记 <c>策划/差异登记.tsv</c> #67）。
    ///
    /// <para><b>为什么必须有它（用户 2026-09-24 第三次投诉原话）</b>：「为什么每个机器人的操作，路线都是相同的。
    /// 你这什么行为树，什么 ai 啊？？为什么没有分工？？？」。上一位交付的"分工"只做到
    /// "角色名不同 + 守点秒数不同"（实机探针 PASS），而**路线与目标点还是同一套**：</para>
    /// <list type="bullet">
    /// <item>T 队 <c>ChoosePlan</c> 里 <c>idx == 0</c> 与 <c>idx == 1</c> 落到**同一条路、同一个点**；</item>
    /// <item>CT 队用 <c>idx % 3</c> ⇒ 槽位 0 与槽位 3 落到**同一个 <c>Route_CT_To_A</c> + 同一个目标点**；</item>
    /// <item>换目标（<c>ReplanObjective</c>）只在本阵营 3 条路里轮 <c>(Id + replanCount) % 3</c> ⇒ 4 只 bot
    /// 必然有两只在同一条路上。</item>
    /// </list>
    /// <para>⇒ 实测（用户本人 2026-09-24 的 <c>client/Logs/2026-09-24.log</c>，22 个"队-回合"）
    /// **每队每回合都恰好有 1 对 bot 的「路线+目标点」完全一样**（Spliff/Darrell 恒同、Gooseman/ZBot 恒同），
    /// 另有 44 对"目标点相同"。用户看到的就是"路线都是相同的"。</para>
    ///
    /// <para><b>本表怎么保证"同队 4 只 bot 互不相同"（可证、不靠概率）</b>：</para>
    /// <list type="number">
    /// <item>队伍内部先算**队内序号** <c>ordinal</c>（同阵营存活的 actor 里按 Id 升序的名次，见
    /// <see cref="TeamLocalOrdinal"/>）—— 用队内序号而不是全局 <c>Id % 4</c>，是因为全局 Id 取决于
    /// "真人玩家在哪一队"（真人 Id 最小），会让某队的两只 bot 折到同一个槽位；</item>
    /// <item>槽位 = <c>(ordinal - carrierOrdinal) mod 4</c>（<see cref="SlotOf"/>）：**模 4 的循环平移**，
    /// 对固定 <c>carrierOrdinal</c> 是双射 ⇒ 4 只 bot 必得 4 个**互不相同**的槽位。
    /// 平移量取"持 C4 那只的队内序号"，是为了让**持包者恒落在槽位 0 = 主攻包点路线**
    /// （出处：<c>策划/策划案/CS1.6单机参考规格.md:130</c>「T 持包到 B 点 → 下包」——下包是持包者本回合唯一目标），
    /// 同时全队一起平移 ⇒ 互不相同这条性质不被破坏；</item>
    /// <item>四个槽位的**路线标记两两不同**（T：主攻路 / 中路 / 另一包点路 / 巡逻；CT：守 A / 守 B / 中路 / 巡逻），
    /// 且共用一个包点标记的两个槽位（T 槽位 0/1 都去本轮主攻包点）用**不同的 <see cref="CsBotPlan.GoalOrdinal"/>**
    /// ⇒ 目标点也不同。</item>
    /// </list>
    /// <para>⇒ "互不相同"是**构造性成立**的，不是"大概率不撞"—— 所以判据能写死（见 <see cref="SelfCheck"/>）。</para>
    /// </summary>
    public static class CsBotPlans
    {
        /// <summary>每队的计划槽位数（= T/CT 各自的巡逻外路线数 3 + 巡逻 1）。</summary>
        public const int Slots = 4;

        /// <summary>
        /// 队内序号 → 槽位：**模 <see cref="Slots"/> 的循环平移**，平移量 = 持 C4 那只 bot 的队内序号。
        /// <c>carrierOrdinal &lt; 0</c>（该队没人持 C4 / CT 队）⇒ 不平移。
        /// </summary>
        public static int SlotOf(int teamLocalOrdinal, int carrierOrdinal)
        {
            var ord = ((teamLocalOrdinal % Slots) + Slots) % Slots;
            if (carrierOrdinal < 0) return ord;

            var shift = ((carrierOrdinal % Slots) + Slots) % Slots;
            return ((ord - shift) % Slots + Slots) % Slots;
        }

        /// <summary>
        /// 槽位 → 计划（**纯函数**：只依赖阵营 / 槽位 / 回合号，与地图、自己站在哪、谁在场都无关）。
        ///
        /// <para>"主攻包点"按回合奇偶统一（与旧实现的 <c>siteIsA</c> 同一口径）：<b>全队同一回合打同一个点</b>，
        /// 像一支队伍而不是散兵；四个槽位的区别在于**从哪条路过去、到点位上的哪个位置**。</para>
        /// </summary>
        /// <param name="team">阵营（观战 / 未定 ⇒ 退化为 T 主攻路的"支援"计划，不抛异常）。</param>
        /// <param name="slot">槽位号（会被规整到 0..3）。</param>
        /// <param name="roundNumber">回合号（决定主攻 A 还是 B，与旧 <c>siteIsA</c> 同式）。</param>
        public static CsBotPlan For(CsTeam team, int slot, int roundNumber)
        {
            var s = ((slot % Slots) + Slots) % Slots;
            var siteIsA = (roundNumber % 2) == 0;
            var mainSite = siteIsA ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
            var otherSite = siteIsA ? CsMarkers.BombsiteB : CsMarkers.BombsiteA;
            var mainRoute = siteIsA ? CsMarkers.TAttackA : CsMarkers.TAttackB;
            var otherRoute = siteIsA ? CsMarkers.TAttackB : CsMarkers.TAttackA;

            var plan = new CsBotPlan { Slot = s };
            switch (team)
            {
                case CsTeam.T:
                    switch (s)
                    {
                        case 1:
                            // 支援：走中路再拐进本轮主攻包点（与槽位 0 分流），目标是包点上**另一个**点。
                            plan.Role = CsBotRole.Support;
                            plan.RouteMarker = CsMarkers.TMid;
                            plan.GoalMarker = mainSite;
                            plan.GoalOrdinal = 1;
                            plan.GoalIsSite = true;
                            break;
                        case 2:
                            plan.Role = CsBotRole.Scout;
                            plan.RouteMarker = CsMarkers.Patrol;
                            plan.GoalMarker = CsMarkers.Patrol;
                            plan.GoalOrdinal = 0;
                            plan.GoalIsSite = false;
                            break;
                        case 3:
                            // 断后 / 佯攻：走**另一条**包点路（与本轮主攻点不在同一边）。
                            plan.Role = CsBotRole.Breaker;
                            plan.RouteMarker = otherRoute;
                            plan.GoalMarker = otherSite;
                            plan.GoalOrdinal = 0;
                            plan.GoalIsSite = true;
                            break;
                        default:
                            // 突破手：主攻路 → 主攻包点（包点上的 0 号位置）。
                            plan.Role = CsBotRole.Breaker;
                            plan.RouteMarker = mainRoute;
                            plan.GoalMarker = mainSite;
                            plan.GoalOrdinal = 0;
                            plan.GoalIsSite = true;
                            break;
                    }
                    break;

                case CsTeam.CT:
                    switch (s)
                    {
                        case 1:
                            // 守 B：另一个包点的守卫位（会按守点时长轮转）。
                            plan.Role = CsBotRole.Support;
                            plan.RouteMarker = CsMarkers.CTDefendB;
                            plan.GoalMarker = CsMarkers.BombsiteB;
                            plan.GoalOrdinal = 1;
                            plan.GoalIsSite = true;
                            break;
                        case 2:
                            plan.Role = CsBotRole.Scout;
                            plan.RouteMarker = CsMarkers.CTMid;
                            plan.GoalMarker = CsMarkers.CTMid;
                            plan.GoalOrdinal = 0;
                            plan.GoalIsSite = false;
                            break;
                        case 3:
                            // 轮转支援：巡逻线（哪个点打起来就去哪边）。
                            plan.Role = CsBotRole.Support;
                            plan.RouteMarker = CsMarkers.Patrol;
                            plan.GoalMarker = CsMarkers.Patrol;
                            plan.GoalOrdinal = 0;
                            plan.GoalIsSite = false;
                            break;
                        default:
                            // 守 A：固守位（守到底，见 CsBotRoles.StaysOnObjective）。
                            plan.Role = CsBotRole.Anchor;
                            plan.RouteMarker = CsMarkers.CTDefendA;
                            plan.GoalMarker = CsMarkers.BombsiteA;
                            plan.GoalOrdinal = 0;
                            plan.GoalIsSite = true;
                            break;
                    }
                    break;

                default:
                    // 观战 / 未定：给最保守的"跟主攻路支援"（不抛异常、不返回空计划）。
                    plan.Role = CsBotRole.Support;
                    plan.RouteMarker = mainRoute;
                    plan.GoalMarker = mainSite;
                    plan.GoalOrdinal = 1;
                    plan.GoalIsSite = true;
                    break;
            }

            plan.RouteLabel = RouteLabel(plan.RouteMarker);
            plan.GoalLabel = GoalLabel(plan.GoalMarker);
            return plan;
        }

        /// <summary>
        /// 队内序号：同阵营 actor 里按 <c>Id</c> 升序的名次（0 起）。
        /// <para>为什么不用全局 <c>Id % 4</c>：真人玩家的 Id 恒为最小（<c>CsMatch._nextActorId</c> 从 0 起递增），
        /// "T 队槽位 0 与槽位 1 撞车"。取队内序号后，**每队的 bot 序号恒为连续 0..n-1** ⇒ 取模必得互不相同的槽位。</para>
        /// <para>口径与 <c>CsMatch.FindSpawnPoint</c> 一样只用 <c>Id</c> 排序（不依赖遍历顺序），所以同一份 actor 集合
        /// 在每台机器、每一帧都得到同一个序号。</para>
        /// </summary>
        /// <returns>找不到 <paramref name="actorId"/> 时返回 0（调用方继续跑，不抛异常）。</returns>
        public static int TeamLocalOrdinal(IReadOnlyList<CsActor> actors, CsTeam team, long actorId)
        {
            if (actors == null) return 0;

            var rank = 0;
            var found = false;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Team != team) continue;
                if (a.Id == actorId) found = true;
                else if (a.Id < actorId) rank++;
            }

            return found ? rank : 0;
        }

        /// <summary>持 C4 那只 bot 的**队内序号**（<paramref name="team"/> 队没人持包 ⇒ -1 = 不平移）。</summary>
        public static int CarrierOrdinal(IReadOnlyList<CsActor> actors, ICsMatch match, CsTeam team)
        {
            if (actors == null || match == null) return -1;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Team != team) continue;
                if (!match.BombCarrierIs(a.Id)) continue;
                return TeamLocalOrdinal(actors, team, a.Id);
            }

            return -1;
        }

        // ==================================================================
        /// <summary>已缓存平移量对应的回合号（<see cref="int.MinValue"/> = 还没有）。</summary>
        private static int _cachedRound = int.MinValue;

        /// <summary>该回合全队共用的平移量（= 持包者队内序号；-1 = 不平移）。</summary>
        private static int _cachedCarrierOrdinal = -1;

        /// <summary>
        /// **本回合的平移量**（全队共用一份，回合内不再变）。
        ///
        /// <para><b>为什么必须缓存</b>（实测缺陷，2026-09-24 11:31:09 线M 的实机对局）：
        /// 判据原本每只 bot 在自己 `ChoosePlan` 时**各自**去问一次"现在谁持 C4"。若两次问之间 C4 状态变了
        /// （持包者被打死 → `CsBomb.CarrierId = 0`；或本回合中途加入的 bot 第一次计划），
        /// 后来那只就拿到 <c>-1</c>（不平移）、先来的拿到 <c>k</c>（平移 k）⇒ **两只 bot 的槽位重合**，
        /// 审计当场打出 `同队路线撞车：Minh(T/槽位3) 与 Gooseman(T/槽位3) 都走 'Route_T_To_A'`。
        /// 缓存后全队读到同一个平移量 ⇒ "模 4 平移是双射 ⇒ 槽位两两不同"这条构造性保证才真正成立。</para>
        ///
        /// <para>回合号变小（新一局 / 比赛重开）⇒ 缓存作废重算（否则会沿用上一局的平移量）。</para>
        /// </summary>
        public static int RoundCarrierOrdinal(IReadOnlyList<CsActor> actors, ICsMatch match, CsTeam team, int roundNumber)
        {
            if (team != CsTeam.T) return -1;                       // C4 只在 T 手上
            if (_cachedRound == roundNumber) return _cachedCarrierOrdinal;

            var ord = CarrierOrdinal(actors, match, team);
            _cachedRound = roundNumber;
            _cachedCarrierOrdinal = ord;
            return ord;
        }

        /// <summary>仅供自检/探针：清掉回合平移量缓存（运行时不调，避免把"回合内唯一"这条性质破坏掉）。</summary>
        public static void ResetRoundCacheForTest()
        {
            _cachedRound = int.MinValue;
            _cachedCarrierOrdinal = -1;
        }

        // ==================================================================
        /// <summary>
        /// **存活**同队 bot 占用的名义槽位表（<c>true</c> = 该槽位的路线家族已被一只活着的队友占着）。
        ///
        /// <para><b>为什么需要它</b>（实机证据，2026-09-24 11:39 线M 的实机对局，本工程自己的审计打出）：
        /// <c>同队路线撞车：Cliffe(T/槽位3) 与 ZBot(T/槽位3) 都走 'Route_T_To_A'</c> —— 两只**都活着**的
        /// 而在**换目标**：<c>TryRouteObjective</c> 按 <c>(本槽位 + k) % 4</c> 轮转，**不问那只槽位的主人是否还活着**
        /// ⇒ 一只卡住的 bot 会轮转进队友正走着的路。计划表管"开局分工"，轮转管"中途改分工"，
        /// 后者必须同样遵守互斥 —— 这个函数就是判据。</para>
        ///
        /// <para>口径：只数 <c>IsAlive</c> 的 bot。死人不再移动，与死人同名一条路**不产生用户可见的
        /// "行为相同"**（互补面：死人占的家族应当被释放出来给卡住的活人用，见 <see cref="AliveSlotTable"/>
        /// 的"少一只 ⇒ 恰好空出一格"断言）。</para>
        /// </summary>
        /// <param name="carrierOrdinal">本回合平移量（= <see cref="RoundCarrierOrdinal"/>；-1 = 不平移）。</param>
        public static bool[] AliveSlotTable(IReadOnlyList<CsActor> actors, CsTeam team, int carrierOrdinal,
                                            int roundNumber, bool[] reuse)
        {
            var taken = reuse != null && reuse.Length == Slots ? reuse : new bool[Slots];
            for (var i = 0; i < taken.Length; i++) taken[i] = false;
            if (actors == null) return taken;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Team != team || !a.IsAlive) continue;
                var slot = SlotOf(TeamLocalOrdinal(actors, team, a.Id), carrierOrdinal);
                if (slot >= 0 && slot < Slots) taken[slot] = true;
            }

            return taken;
        }

        /// <summary>
        /// 某条路线家族是否已被**另一只活着的同队 bot** 占着（换目标选家族前的门禁）。
        /// <para>比 <see cref="AliveSlotTable"/> 更好用的地方：<c>TryPatrolObjective</c> 这类"点状目标"
        /// 只知道自己要的**标记名**，不知道它是几号槽位。</para>
        /// </summary>
        public static bool FamilyHeldByAliveTeammate(IReadOnlyList<CsActor> actors, CsTeam team, int carrierOrdinal,
                                                     int roundNumber, long selfId, string routeMarker)
        {
            if (actors == null || string.IsNullOrEmpty(routeMarker)) return false;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Team != team || !a.IsAlive || a.Id == selfId) continue;
                var p = For(team, SlotOf(TeamLocalOrdinal(actors, team, a.Id), carrierOrdinal), roundNumber);
                if (p.RouteMarker == routeMarker) return true;
            }

            return false;
        }

        /// <summary>
        /// 在**同一条目标标记**上，为一只 bot 选一个"存活队友没占"的目标序号。
        ///
        /// <para><b>为什么需要它</b>：4 条路线家族在全队活着时必然被占满（那是双射的正常状态），
        /// 此时换目标**不许**抢队友的路，只能"留在自己的槽位、改目标点"。而目标点也要互斥 ——
        /// 槽位 0 与槽位 1 都在同一个包点上（<c>#0</c> / <c>#1</c>），改了序号不排除队友就会在同一个点上重叠
        /// （实机日志里 `目标 (34.50, 2.44, 29.50) vs (34.50, 2.44, 29.50)` 两个值一模一样）。</para>
        ///
        /// <para>只数 <c>IsAlive</c> 的队友：死人让出的目标序号可以被接手（与 <see cref="AliveSlotTable"/> 同口径）。</para>
        /// </summary>
        /// <param name="fromOrdinal">自己当前的序号（从它的**下一个**开始找）。</param>
        /// <param name="candidateCount">该标记上实际可用的点数（调用方给：CanStand 过滤后的个数）。</param>
        public static int NextFreeGoalOrdinal(IReadOnlyList<CsActor> actors, CsTeam team, int carrierOrdinal,
                                              int roundNumber, long selfId, string goalMarker,
                                              int fromOrdinal, int candidateCount)
        {
            if (candidateCount <= 0 || string.IsNullOrEmpty(goalMarker)) return -1;

            var used = new bool[candidateCount];
            if (actors != null)
            {
                for (var i = 0; i < actors.Count; i++)
                {
                    var a = actors[i];
                    if (a == null || a.Team != team || !a.IsAlive || a.Id == selfId) continue;
                    var p = For(team, SlotOf(TeamLocalOrdinal(actors, team, a.Id), carrierOrdinal), roundNumber);
                    if (p.GoalMarker != goalMarker) continue;
                    used[((p.GoalOrdinal % candidateCount) + candidateCount) % candidateCount] = true;
                }
            }

            for (var d = 1; d <= candidateCount; d++)
            {
                var o = ((fromOrdinal + d) % candidateCount + candidateCount) % candidateCount;
                if (!used[o]) return o;
            }

            return -1;
        }

        /// <summary>路线标记 → 中文名（日志与探针共用同一份，不在两处各写一张表）。</summary>
        public static string RouteLabel(string marker)
        {
            if (marker == CsMarkers.TAttackA) return "T_A路";
            if (marker == CsMarkers.TAttackB) return "T_B路";
            if (marker == CsMarkers.TMid) return "T_中路";
            if (marker == CsMarkers.CTDefendA) return "CT_守A";
            if (marker == CsMarkers.CTDefendB) return "CT_守B";
            if (marker == CsMarkers.CTMid) return "CT_中路";
            if (marker == CsMarkers.Patrol) return "巡逻";
            if (marker == CsMarkers.SpawnT) return "T_出生点";
            if (marker == CsMarkers.SpawnCT) return "CT_出生点";
            if (marker == CsBotConst.BombGuardRoute) return "守C4";
            return marker ?? "无";
        }

        /// <summary>目标标记 → 中文名。</summary>
        public static string GoalLabel(string marker)
        {
            if (marker == CsMarkers.BombsiteA) return "包点A";
            if (marker == CsMarkers.BombsiteB) return "包点B";
            if (marker == CsMarkers.Patrol) return "巡逻点";
            return RouteLabel(marker);
        }

        /// <summary>
        /// **离线段言**（判据资产 = <c>tools/probes/probe-bot-routes.cs</c> 的离线半 + <c>BotSelfTest</c> 菜单）：
        /// 对每一队、每一个可能的持包序号，断言 4 个槽位的 (路线, 目标标记, 目标序号) **两两不同**；
        /// 并断言"路线标记只有 4 个、且恰好来自 4 条不同的路"。
        /// <para>这里判的是**构造性质**（"任意两只同队 bot 不可能同路同点"），不是某一局的抽样结果 ——
        /// 抽样只能证明"这一局没撞"，构造性质才是用户说的"路线都是相同的"被根治的凭据。</para>
        /// <para>负控（判据必须能失败）：<paramref name="corrupt"/> = true 时把槽位 3 的路由与目标点改成与槽位 0
        /// 编辑器进程已经在跑，外部改环境变量影响不到它（会得到一个"永远正控"的假判据）。</para>
        /// </summary>
        /// <summary>
        /// 离线段言 + **负控**（判据必须能失败）一条命令跑完：
        /// 必须 <c>RESULT-PLAN: FAIL</c>。两条都对才输出 <c>RESULT-PLAN-NEGCTL: PASS</c>。
        /// <para>为什么必须成对：只跑正控时，"判据永远不会红"和"判据真的在判"无法区分
        /// （见 skill 的「判据自己也会出事故」）。</para>
        /// </summary>
        public static string SelfCheckWithNegativeControl()
        {
            var pos = SelfCheck(false);
            var neg = SelfCheck(true);

            var ok = pos.Contains("RESULT-PLAN: PASS") && neg.Contains("RESULT-PLAN: FAIL");
            return pos + "\n\n---- 负控：注入「槽位 3 与槽位 0 同路同点」（= 旧实现的实际行为）----\n" + neg +
                   "\n\nRESULT-PLAN-NEGCTL: " + (ok ? "PASS" : "FAIL") +
                   "（正控必须 PASS 且负控必须 FAIL；实际 正控=" +
                   (pos.Contains("RESULT-PLAN: PASS") ? "PASS" : "FAIL") + " 负控=" +
                   (neg.Contains("RESULT-PLAN: FAIL") ? "FAIL" : "PASS") + "）";
        }

        /// <summary>负控开关的说明见 <see cref="SelfCheck(bool)"/>。</summary>
        public static string SelfCheck()
        {
            return SelfCheck(false);
        }

        /// <summary>带负控开关的 <see cref="SelfCheck()"/>。</summary>
        public static string SelfCheck(bool corrupt)
        {
            var sb = new StringBuilder();
            var failures = 0;

            sb.Append("== CsBotPlans 离线段言 ==");
            sb.Append("\n负控形参 corrupt=").Append(corrupt ? "true" : "false").Append("：")
              .Append(corrupt ? "槽位0 与 槽位3 被改成同一条路（应 FAIL）" : "未注入（正控，应 PASS）");

            foreach (var team in new[] { CsTeam.T, CsTeam.CT })
            {
                for (var carrier = -1; carrier < Slots; carrier++)
                {
                    var routes = new HashSet<string>();
                    var goals = new HashSet<string>();
                    var line = new List<string>(Slots);

                    for (var ord = 0; ord < Slots; ord++)
                    {
                        var slot = SlotOf(ord, carrier);
                        var p = For(team, slot, 2);            // 回合 2 = 偶数 ⇒ 本轮主攻 A
                        if (corrupt && p.Slot == 3)
                        {
                            var p0 = For(team, 0, 2);
                            p.RouteMarker = p0.RouteMarker;
                            p.GoalMarker = p0.GoalMarker;
                            p.GoalOrdinal = p0.GoalOrdinal;
                            p.RouteLabel = p0.RouteLabel;
                            p.GoalLabel = p0.GoalLabel;
                        }

                        var key = p.RouteMarker + "|" + p.GoalMarker + "|" + p.GoalOrdinal;
                        if (!routes.Add(p.RouteMarker))
                        {
                            failures++;
                            sb.Append("\n  ✗ 失败：").Append(team).Append(" 持包序号=").Append(carrier)
                              .Append(" ⇒ 队内序号 ").Append(ord).Append(" 与更小的序号落在**同一条路线** ")
                              .Append(p.RouteMarker);
                        }

                        if (!goals.Add(key))
                        {
                            failures++;
                            sb.Append("\n  ✗ 失败：").Append(team).Append(" 持包序号=").Append(carrier)
                              .Append(" ⇒ 队内序号 ").Append(ord).Append(" 与更小的序号落在**同一个目标点** ")
                              .Append(p.GoalMarker).Append("#").Append(p.GoalOrdinal);
                        }

                        line.Add($"序号{ord}→槽{p.Slot}:{p.RouteLabel}→{p.GoalLabel}#{p.GoalOrdinal}（{CsBotRoles.Label(p.Role)}）");

                        // 机器可读行（给 tools/probes/bot-route-sequence-check.py 用：它拿这些**标记名**
                        // 去 resources/MapData/de_dust2_markers.bytes 上算"整条路线序列"，做素材层的离线判据）。
                        sb.Append("\nPLANROW team=").Append(team)
                          .Append(" slot=").Append(p.Slot)
                          .Append(" route=").Append(p.RouteMarker)
                          .Append(" goal=").Append(p.GoalMarker)
                          .Append(" goalord=").Append(p.GoalOrdinal)
                          .Append(" role=").Append(p.Role);
                    }

                    if (carrier == -1)
                        sb.Append("\n  ").Append(team).Append("（无持包）路线种数=").Append(routes.Count)
                          .Append("/").Append(Slots).Append(" 目标点种数=").Append(goals.Count).Append("/").Append(Slots);
                    if (carrier == 0 || carrier == -1)
                        sb.Append("\n    ").Append(carrier == -1 ? "无持包: " : "持包者=序号0: ").Append(string.Join("  ", line.ToArray()));
                }
            }

            // 持包者必须落在槽位 0（主攻包点路线）—— 这是"持包者一定去打点"的构造保证
            for (var carrier = 0; carrier < Slots; carrier++)
            {
                if (SlotOf(carrier, carrier) != 0)
                {
                    failures++;
                    sb.Append("\n  ✗ 失败：持包者（序号 ").Append(carrier).Append("）未落在槽位 0（持包者必须走主攻包点路线）");
                }
            }

            // ------------------------------------------------------------------
            //  ① 全队活着 ⇒ 4 个家族恰好被占满 ⇒ **换目标没有空家族可去**（必须走"留在本槽位只换目标点"）
            //  ② 死一只 ⇒ 恰好空出 1 格（否则卡住的 bot 永远换不到路）
            //  ③ 目标序号互斥：同一条目标标记上，换目标不许落到队友占着的序号上
            //     （实机日志里两只 bot 的目标坐标一模一样 `(34.50, 2.44, 29.50) vs (34.50, 2.44, 29.50)`）
            // ------------------------------------------------------------------
            {
                var actors = new List<CsActor>(Slots);
                for (var i = 0; i < Slots; i++)
                    actors.Add(new CsActor { Id = 100 + i, Team = CsTeam.T, IsBot = true, IsAlive = true });

                var occ = AliveSlotTable(actors, CsTeam.T, 0, 2, null);
                var occCount = 0;
                for (var i = 0; i < Slots; i++) if (occ[i]) occCount++;
                if (occCount != Slots)
                {
                    failures++;
                    sb.Append("\n  ✗ 失败：全队 4 只活着时应占满 ").Append(Slots)
                      .Append(" 个槽位，实际只占 ").Append(occCount);
                }

                actors[1].IsAlive = false;                       // 死一只
                var occ2 = AliveSlotTable(actors, CsTeam.T, 0, 2, null);
                var occCount2 = 0;
                for (var i = 0; i < Slots; i++) if (occ2[i]) occCount2++;
                if (occCount2 != Slots - 1)
                {
                    failures++;
                    sb.Append("\n  ✗ 失败：死掉一只队友后应剩 ").Append(Slots - 1)
                      .Append(" 个被占槽位，实际 ").Append(occCount2).Append("（卡住的 bot 将永远没有空家族可换）");
                }

                actors[1].IsAlive = true;
                var freeFamily = 0;
                var occ3 = AliveSlotTable(actors, CsTeam.T, 0, 2, null);
                for (var i = 0; i < Slots; i++) if (!occ3[i]) freeFamily++;
                if (freeFamily != 0)
                {
                    failures++;
                    sb.Append("\n  ✗ 失败：全队活着时不该有「空的路线家族」，实际有 ").Append(freeFamily);
                }

                // 目标序号互斥：T 槽位 0 与槽位 1 都在主攻包点（#0 / #1）；序号 #1 被队友占着，
                // 所以序号 #0 那只换目标必须**跳开 #1**（候选数按 Bombsite_A 实际点数 6 算）。
                const int siteCandidates = 6;
                var teammateGoalOrdinal = For(CsTeam.T, 1, 2).GoalOrdinal % siteCandidates;
                var next = corrupt
                    ? (0 + 1) % siteCandidates                       // 负控 = 旧规则（不问队友、直接 +1）
                    : NextFreeGoalOrdinal(actors, CsTeam.T, 0, 2, 100, CsMarkers.BombsiteA, 0, siteCandidates);

                if (next == teammateGoalOrdinal)
                {
                    failures++;
                    sb.Append("\n  ✗ 失败：槽位 0 换目标取到序号 #").Append(next)
                      .Append("，与槽位 1 的目标点**重合**（两只 bot 会去同一个点）");
                }
                else
                {
                    sb.Append("\n  ✓ 目标序号互斥：槽位 0 换目标取 #").Append(next)
                      .Append("，跳开了槽位 1 占着的 #").Append(teammateGoalOrdinal);
                }

                sb.Append("\n  ").Append(corrupt ? "负控" : "正控")
                  .Append("：全队占用槽位数=").Append(occCount).Append(" 死一只后=").Append(occCount2)
                  .Append(" 空家族数=").Append(freeFamily);
            }

            sb.Append("\nRESULT-PLAN: ").Append(failures == 0 ? "PASS" : "FAIL").Append("（失败 ").Append(failures).Append(" 条）");
            return sb.ToString();
        }
    }
}
