using System.Collections.Generic;
using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人**高层任务**（原版 <c>CCSBot::TaskType</c>，逐值对齐
    /// <c>原版资源/cs16src/regamedll/dlls/bot/cs_bot.h:417-441</c>）。
    ///
    /// <para>它是"本回合这一段时间在干什么"的标签，与 <see cref="CsBotState"/>（怎么动）正交：
    /// 状态由行为树的叶子提交，任务由任务层每 <see cref="CsBotTaskConst.EvalInterval"/> 秒推导一次。
    /// 枚举值顺序与顺序号**不得改动**（与日志、探针共用）。</para>
    /// </summary>
    public enum CsBotTask
    {
        /// <summary>没有高层任务：走本轮计划槽（路线 → 目标点 → 守点）。</summary>
        None = 0,

        /// <summary>搜索并消灭敌人。</summary>
        SeekAndDestroy,

        /// <summary>去包点下包。</summary>
        PlantBomb,

        /// <summary>去捡 / 找正在响的炸弹（枚举值保留，本工程暂不下发）。</summary>
        FindTickingBomb,

        /// <summary>拆包。</summary>
        DefuseBomb,

        /// <summary>守已安放的炸弹。</summary>
        GuardTickingBomb,

        /// <summary>掩护正在拆包的队友。</summary>
        GuardBombDefuser,

        /// <summary>守掉在地上的 C4（枚举值保留，本工程暂不下发）。</summary>
        GuardLooseBomb,

        /// <summary>守包点区域（枚举值保留，本工程暂不下发）。</summary>
        GuardBombZone,

        /// <summary>下包后撤离爆点。</summary>
        EscapeFromBomb,

        /// <summary>在当前位置守着（本工程 = 在爆点外一圈盯住 C4）。</summary>
        HoldPosition,

        /// <summary>跟随队长。</summary>
        Follow,

        /// <summary>VIP 撤离（枚举值保留，本工程暂不下发）。</summary>
        VipEscape,

        /// <summary>守 VIP 撤离区（枚举值保留）。</summary>
        GuardVipEscapeZone,

        /// <summary>集合人质（枚举值保留）。</summary>
        CollectHostages,

        /// <summary>营救人质（枚举值保留）。</summary>
        RescueHostages,

        /// <summary>守人质（枚举值保留）。</summary>
        GuardHostages,

        /// <summary>守人质营救区（枚举值保留）。</summary>
        GuardHostageRescueZone,

        /// <summary>去最后已知的敌人位置。</summary>
        MoveToLastKnownEnemy,

        /// <summary>去狙击点（枚举值保留）。</summary>
        MoveToSniperSpot,

        /// <summary>狙击（枚举值保留）。</summary>
        Sniping,
    }


    /// <summary>
    /// 对敌人的态度（原版 <c>CCSBot::DispositionType</c>，
    /// <c>原版资源/cs16src/regamedll/dlls/bot/cs_bot.h:448-456</c>）。
    /// </summary>
    public enum CsBotDisposition
    {
        /// <summary>看见就打，听见动静就去查。</summary>
        EngageAndInvestigate = 0,

        /// <summary>看见就打，但只朝声源转头、不离开当前目标去查。</summary>
        OpportunityFire,

        /// <summary>只在被打、或敌人贴脸时才还手。</summary>
        SelfDefense,

        /// <summary>完全不理会敌人（下包 / 拆包 / 逃爆期间）。</summary>
        IgnoreEnemies,
    }


    /// <summary>
    /// 士气（原版 <c>CCSBot::MoraleType</c>，<c>原版资源/cs16src/regamedll/dlls/bot/cs_bot.h:463-472</c>：
    /// 七级、整数边界 <c>-3..3</c>；升降时被 <c>cs_bot.cpp:751-766</c> 的 <c>if (m_morale &lt; EXCELLENT)</c>
    /// / <c>if (m_morale &gt; TERRIBLE)</c> 夹住）。
    /// </summary>
    public enum CsBotMorale
    {
        Terrible = -3,
        Bad = -2,
        Negative = -1,
        Neutral = 0,
        Positive = 1,
        Good = 2,
        Excellent = 3,
    }


    /// <summary>任务层的中文名（日志 / 探针共用；只影响可读性，不参与决策）。</summary>
    public static class CsBotTaskText
    {
        public static string Task(CsBotTask task)
        {
            switch (task)
            {
                case CsBotTask.SeekAndDestroy: return "搜索歼灭(SEEK_AND_DESTROY)";
                case CsBotTask.PlantBomb: return "下包(PLANT_BOMB)";
                case CsBotTask.FindTickingBomb: return "找炸弹(FIND_TICKING_BOMB)";
                case CsBotTask.DefuseBomb: return "拆包(DEFUSE_BOMB)";
                case CsBotTask.GuardTickingBomb: return "守包(GUARD_TICKING_BOMB)";
                case CsBotTask.GuardBombDefuser: return "掩护拆包(GUARD_BOMB_DEFUSER)";
                case CsBotTask.GuardLooseBomb: return "守落包(GUARD_LOOSE_BOMB)";
                case CsBotTask.GuardBombZone: return "守包点(GUARD_BOMB_ZONE)";
                case CsBotTask.EscapeFromBomb: return "撤离爆点(ESCAPE_FROM_BOMB)";
                case CsBotTask.HoldPosition: return "原地守爆点(HOLD_POSITION)";
                case CsBotTask.Follow: return "跟随队长(FOLLOW)";
                case CsBotTask.VipEscape: return "VIP撤离(VIP_ESCAPE)";
                case CsBotTask.GuardVipEscapeZone: return "守VIP撤离区(GUARD_VIP_ESCAPE_ZONE)";
                case CsBotTask.CollectHostages: return "集合人质(COLLECT_HOSTAGES)";
                case CsBotTask.RescueHostages: return "营救人质(RESCUE_HOSTAGES)";
                case CsBotTask.GuardHostages: return "守人质(GUARD_HOSTAGES)";
                case CsBotTask.GuardHostageRescueZone: return "守人质区(GUARD_HOSTAGE_RESCUE_ZONE)";
                case CsBotTask.MoveToLastKnownEnemy: return "去最后已知位置(MOVE_TO_LAST_KNOWN_ENEMY)";
                case CsBotTask.MoveToSniperSpot: return "去狙击点(MOVE_TO_SNIPER_SPOT)";
                case CsBotTask.Sniping: return "狙击(SNIPING)";
                default: return "无(None)";
            }
        }

        public static string Disposition(CsBotDisposition d)
        {
            switch (d)
            {
                case CsBotDisposition.OpportunityFire: return "机会开火(OPPORTUNITY_FIRE)";
                case CsBotDisposition.SelfDefense: return "自卫(SELF_DEFENSE)";
                case CsBotDisposition.IgnoreEnemies: return "无视敌人(IGNORE_ENEMIES)";
                default: return "主动交战(ENGAGE_AND_INVESTIGATE)";
            }
        }

        public static string Morale(CsBotMorale m)
        {
            switch (m)
            {
                case CsBotMorale.Terrible: return "极差";
                case CsBotMorale.Bad: return "差";
                case CsBotMorale.Negative: return "负面";
                case CsBotMorale.Positive: return "正面";
                case CsBotMorale.Good: return "好";
                case CsBotMorale.Excellent: return "极好";
                default: return "中立";
            }
        }
    }


    /// <summary>
    /// 任务层阈值。原版的 <c>TaskType</c> / <c>DispositionType</c> / <c>MoraleType</c> 只有**枚举**
    /// （<c>cs_bot.h:417-476</c>），对应状态机的实现文件（<c>cs_bot_attack.cpp</c> 等）不在本机源码快照里
    /// ⇒ 下面这些"多少秒 / 多少米"在本工程是**本项目新增**，逐条在注释里说明取值依据。
    /// </summary>
    public static class CsBotTaskConst
    {
        /// <summary>任务评估周期（秒）：每 0.5s 重推一次任务，避免逐帧抖动、又能在局面变化后很快跟上。</summary>
        public const float EvalInterval = 0.5f;

        /// <summary>下包后撤离的窗口（秒）：窗口内若还在 <see cref="EscapeDistance"/> 内就继续往外跑。</summary>
        public const float EscapeSeconds = 3f;

        /// <summary>撤离窗口的超时兜底（秒）：被墙挡住时最多再延长这么久，之后转入守爆点。</summary>
        public const float EscapeGraceSeconds = 1.5f;

        /// <summary>下包任务的进入半径（米）：距**本轮下包点**这么近就算"已在包点、该下包"。</summary>
        public const float PlantTaskEnterRange = 8f;

        /// <summary>下包任务的保持半径（米）：已经在执行下包时，退到这么远才放弃（滞回）。</summary>
        public const float PlantTaskStayRange = 12f;

        /// <summary>撤离目标距离（米）：下包点周围这么远才算出了爆心。取值 = 报警半径
        /// <c>CsConst.GrenadeHeRadius</c>(5.5m) 之上留出余量，且与撤离判定 8m 同向。</summary>
        public const float EscapeDistance = 8f;

        /// <summary>撤离时朝外跑的落点距离（米）= 8m 判定 + 4m 余量，保证跑过判定线时不正好停在线上。</summary>
        public const float EscapeRunDistance = 12f;

        /// <summary>守爆点的站位环外半径（米）：离 C4 超过它就往回靠，进入环内就站住。</summary>
        public const float HoldRingRadius = 14f;

        /// <summary>守爆点往回靠的停步半径（米）：与 <see cref="EscapeDistance"/> 同值，站在撤离判定的同一圈上。</summary>
        public const float HoldArriveRadius = 9f;

        /// <summary>拆包者的"有敌"判定半径（米）：正在拆的队友这么近有敌，就派人掩护他。</summary>
        public const float GuardDefuserEnemyRadius = 6f;

        /// <summary>"最后已知敌人位置"的有效窗口（秒）。</summary>
        public const float LastKnownEnemySeconds = 15f;

        /// <summary>开局跟随窗口（秒）：Live 开始后这段时间内非队长会跟队长走。</summary>
        public const float FollowWindowSeconds = 8f;

        /// <summary>跟随所需的最小同队人数：人太少时各自走计划槽。</summary>
        public const int FollowMinTeammates = 3;

        /// <summary>跟随时与队长保持的距离（米）：两人身位，不互相卡住。</summary>
        public const float FollowDistance = 3.5f;

        /// <summary>低士气时不主动交战的远敌距离（米）：超过它就不追。</summary>
        public const float LowMoraleChaseRange = 25f;

        /// <summary>
        /// 自卫态下放行交战的贴脸距离（米）："very close to enemy" 的落地值。
        /// <para>取 25m = <see cref="LowMoraleChaseRange"/>：低士气的两条规则（自卫态、不追远敌）
        /// 因此是同一个距离门槛，不会出现"标着自卫、却在 10~25m 之间既不打也不退"的空档。</para>
        /// </summary>
        public const float SelfDefenseEngageRange = LowMoraleChaseRange;

        /// <summary>"刚被打过"的记忆窗口（秒）：血量下降后的这段时间里，自卫态也允许还手。</summary>
        public const float HurtMemorySeconds = 3f;

        /// <summary>任务层日志（撤离距离 / 拆包掩护）的最小间隔（秒）。</summary>
        public const float PeriodicLogSeconds = 1f;
    }


    /// <summary>
    /// 每队选一个**队长**（原版 <c>FollowState::SetLeader</c>，<c>cs_bot.h:249</c>；选择规则本身
    /// 在原版是 <c>FollowState::OnUpdate</c> 里的动态挑选，该实现文件不在本机快照 ⇒ 规则是本项目新增）。
    ///
    /// <para>规则（按优先级比较，全部可观测、无随机）：存活 → 击杀数高 → 离队伍中心近 → Id 小。
    /// 纯函数：同一 tick 同一队伍的所有 bot 得到同一个 Id，不依赖调用顺序。</para>
    /// </summary>
    public static class CsBotLeader
    {
        /// <summary>
        /// 本回合已选定的队长（每队一个）。<b>必须缓存</b>：决胜项里有"离队伍中心近"，
        /// 而队伍中心随每个人移动而变 ⇒ 逐次重算会让队长在两个击杀数相同的 bot 之间来回跳，
        /// 于是每个人都一会儿是队长、一会儿不是（<see cref="CsBotTask.Follow"/> 每 1~2s 进出一次）。
        /// 只在队长阵亡 / 离场 / 换回合时重选。
        /// </summary>
        private static ICsMatch _cachedMatch;
        private static int _cachedRound = int.MinValue;
        private static readonly Dictionary<CsTeam, long> Cached = new Dictionary<CsTeam, long>(4);

        public static long Select(ICsMatch match, CsTeam team, out string name, out string why)
        {
            name = null;
            why = "没有存活队友";
            if (match == null) return 0L;

            if (!ReferenceEquals(match, _cachedMatch) || match.RoundNumber != _cachedRound)
            {
                _cachedMatch = match;
                _cachedRound = match.RoundNumber;
                Cached.Clear();
            }

            if (Cached.TryGetValue(team, out var keptId) && keptId != 0L)
            {
                var kept = match.Find(keptId);
                if (kept != null && kept.IsAlive && kept.Team == team)
                {
                    name = kept.Name;
                    why = $"本回合队长（击杀 {kept.Kills}）";
                    return keptId;
                }
            }

            var actors = match.Actors;
            var center = Vector3.zero;
            var alive = 0;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != team) continue;
                center += a.Position;
                alive++;
            }

            if (alive == 0) return 0L;
            center /= alive;

            var bestId = 0L;
            var bestKills = int.MinValue;
            var bestDist = float.MaxValue;
            var bestName = (string)null;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != team) continue;

                var d = a.Position - center;
                d.y = 0f;
                var dist = d.magnitude;

                var better = a.Kills > bestKills
                    || (a.Kills == bestKills && dist < bestDist - 0.05f)
                    || (a.Kills == bestKills && Mathf.Abs(dist - bestDist) <= 0.05f && (bestId == 0L || a.Id < bestId));
                if (!better) continue;

                bestId = a.Id;
                bestKills = a.Kills;
                bestDist = dist;
                bestName = a.Name;
            }

            name = bestName;
            why = $"存活 {alive} 人，{bestName} 击杀 {bestKills}、距队伍中心 {bestDist:F1}m";
            Cached[team] = bestId;
            return bestId;
        }
    }


    /// <summary>
    /// 队内**路线广播板**：每只 bot 每 tick 把自己的本轮路线标记登记进来，<see cref="CsBotTask.Follow"/>
    /// 的跟班据此走队长的路线（原版 <c>FollowState::OnUpdate</c> 按队长的移动状态跟随，
    /// 我们的载体是"路线标记 + 路点序列"）。
    ///
    /// <para>键 = (比赛实例, 回合号)：换局 / 换回合自动清空，不会跨局串路线。</para>
    /// </summary>
    public static class CsBotRouteBoard
    {
        private static ICsMatch _match;
        private static int _round = int.MinValue;
        private static readonly Dictionary<long, string> Routes = new Dictionary<long, string>(16);

        public static void Publish(ICsMatch match, int round, long actorId, string route)
        {
            if (match == null || round <= 0 || actorId == 0L) return;

            if (!ReferenceEquals(match, _match) || round != _round)
            {
                _match = match;
                _round = round;
                Routes.Clear();
            }

            if (string.IsNullOrEmpty(route)) Routes.Remove(actorId);
            else Routes[actorId] = route;
        }

        public static bool TryGet(ICsMatch match, int round, long actorId, out string route)
        {
            route = null;
            if (match == null || !ReferenceEquals(match, _match) || round != _round) return false;
            if (actorId == 0L) return false;
            return Routes.TryGetValue(actorId, out route) && !string.IsNullOrEmpty(route);
        }
    }
}
