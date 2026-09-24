using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;

namespace Cs16.UI
{
    /// <summary>
    /// 记分板的一行（**UI 本地推导类型**，不是 Core 契约类型 —— 契约里没有花名册结构）。
    /// </summary>
    public struct CsScoreRow
    {
        public string Name;
        public CsTeam Team;
        public int Kills;
        public int Deaths;
        public bool Alive;
        /// <summary>金钱；<see cref="UnknownMoney"/> 表示快照没提供（非本地玩家）。</summary>
        public int Money;
        public bool IsSelf;

        /// <summary>快照未提供该玩家金钱时的占位值（显示成 <c>—</c>，不假装是 0）。</summary>
        public const int UnknownMoney = -1;
    }

    /// <summary>
    ///
    /// <para><b>为什么需要它</b>：<see cref="CsHudSnapshot"/> 里**没有花名册**
    /// （没有"每个玩家一行：名字/阵营/击杀/死亡/存活/金钱"的结构），UI 又按分层铁律不许
    /// 引用 <c>Cs16.Module.Match</c> 去拿 <c>ICsMatch.Actors</c>。
    /// 因此这里用快照里**确实有的**唯一逐人数据 —— <see cref="CsHudSnapshot.KillFeed"/>
    /// 得到与实际比赛一致的击杀/死亡数（与 <c>CsActor.Kills/Deaths</c> 同源同口径）。</para>
    ///
    /// <para><b>能力的边界（不掩饰）</b>：</para>
    /// <list type="bullet">
    /// <item>从未参与过任何击杀的玩家不会出现在表里（快照给不出花名册）；</item>
    /// <item>金钱只有本地玩家知道（<see cref="CsHudSnapshot.Money"/>），其余显示 <c>—</c>；</item>
    /// <item>存活状态：本回合内被击杀过 = 死亡（<see cref="BeginRound"/> 复位），本地玩家以快照为准。</item>
    /// </list>
    /// <para>
    /// 若 <see cref="CsHudSnapshot"/> 将来提供一份花名册（字段未定），
    /// 只需把 <see cref="Build"/> 换成读它即可，面板本身不用改。
    /// </para>
    /// </summary>
    public sealed class CsIngameStats
    {
        private const string Tag = "UI";

        private sealed class Entry
        {
            public string Name;
            public CsTeam Team;
            public int Kills;
            public int Deaths;
            public bool Alive = true;
            public int Money = CsScoreRow.UnknownMoney;
            public bool IsSelf;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(24);

        /// <summary>
        /// 已处理过的最大击杀时间戳（用于"只处理新出现的击杀"）。
        /// 初值用 <see cref="float.NegativeInfinity"/> 而不是 0：<c>BornTime</c> 是 <c>Time.time</c>，
        /// 理论上可以从 0 开始，用 0 当"还没见过任何击杀"会把开局第一秒的击杀漏掉。
        /// </summary>
        private float _lastBorn = float.NegativeInfinity;

        /// <summary>清空（新一局 / 回主菜单 / 比赛停止）。</summary>
        public void Reset(string reason)
        {
            _entries.Clear();
            _lastBorn = float.NegativeInfinity;
            Game.Logger?.Info(Tag, $"记分板本地统计已重置（{reason}）");
        }

        /// <summary>
        /// 每帧调用：把 <see cref="CsHudSnapshot.KillFeed"/> 里**新出现**的击杀并入统计。
        /// </summary>
        /// <returns>本次新增处理的击杀条数。</returns>
        public int Observe()
        {
            var feed = CsHudSnapshot.KillFeed;
            if (feed == null || feed.Count == 0) return 0;

            // 只从**当前 feed** 里算最大值：不能拿 _lastBorn 当起点（那样 maxBorn 永远 ≥ _lastBorn，
            // 下面"时间戳回退"的判断就永远不成立 —— 这个坑是离线自检 statsprobe 抓出来的）
            var maxBorn = float.NegativeInfinity;
            for (var i = 0; i < feed.Count; i++)
            {
                if (feed[i].BornTime > maxBorn) maxBorn = feed[i].BornTime;
            }

            // 时间戳回退 = 换了新一局（或比赛模拟的时钟被替换）→ 旧统计作废，不能把它当"新击杀"
            if (maxBorn < _lastBorn)
            {
                Game.Logger?.Warn(Tag,
                    $"击杀信息时间戳回退（max={maxBorn:0.000} < last={_lastBorn:0.000}），记分板统计已重置");
                _entries.Clear();
                _lastBorn = float.NegativeInfinity;
            }

            var added = 0;
            for (var i = 0; i < feed.Count; i++)
            {
                var item = feed[i];
                if (item.BornTime <= _lastBorn) continue;
                AddKill(item);
                added++;
            }

            _lastBorn = maxBorn;
            return added;
        }

        private void AddKill(in CsKillFeedItem item)
        {
            var victimName = string.IsNullOrEmpty(item.VictimName) ? null : item.VictimName;
            if (victimName == null)
            {
                Game.Logger?.Warn(Tag, "击杀信息里受害者名字为空（数据异常），该条只计入击杀方");
            }
            else
            {
                var victim = GetOrAdd(victimName, item.VictimTeam);
                victim.Deaths++;
                victim.Alive = false;
            }

            // 自杀 / 环境致死（名字相同或击杀者为空）不算任何人的人头
            if (string.IsNullOrEmpty(item.KillerName)) return;
            if (victimName != null && item.KillerName == victimName) return;

            var killer = GetOrAdd(item.KillerName, item.KillerTeam);
            killer.Kills++;
        }

        /// <summary>新回合开始：所有人复活（CS 1.6 每回合满血重生）。</summary>
        public void BeginRound()
        {
            foreach (var kv in _entries) kv.Value.Alive = true;
        }

        /// <summary>本地玩家的权威信息（名字来自设置，状态/金钱/阵营来自快照）。</summary>
        public void SetSelf(string name, CsTeam team, bool alive, int money)
        {
            if (string.IsNullOrEmpty(name)) name = CsPlayerSettingsStore.DefaultPlayerName;
            var self = GetOrAdd(name, team);
            self.IsSelf = true;
            self.Team = team;
            self.Alive = alive;
            self.Money = money;
        }

        private Entry GetOrAdd(string name, CsTeam team)
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                // 半场换边后阵营会变，按最新一次见到的事实更新
                if (entry.Team != team) entry.Team = team;
                return entry;
            }

            entry = new Entry { Name = name, Team = team };
            _entries[name] = entry;
            return entry;
        }

        /// <summary>
        /// 生成按阵营分组、按表现排序的行（自己永远排在本队第一行）。
        /// </summary>
        public void Build(List<CsScoreRow> into)
        {
            if (into == null)
            {
                Game.Logger?.Error(Tag, "CsIngameStats.Build 收到 null 列表，记分板不会刷新");
                return;
            }

            into.Clear();

            // 快照里"我"必定在场（每局都有本地玩家），所以即使一条击杀都没发生，表里也至少有自己一行
            var rows = new List<CsScoreRow>(_entries.Count);
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                rows.Add(new CsScoreRow
                {
                    Name = e.Name,
                    Team = e.Team,
                    Kills = e.Kills,
                    Deaths = e.Deaths,
                    Alive = e.Alive,
                    Money = e.Money,
                    IsSelf = e.IsSelf,
                });
            }

            rows.Sort(Compare);
            into.AddRange(rows);
        }

        private static int Compare(CsScoreRow a, CsScoreRow b)
        {
            // **先按阵营分组**（记分板要求"两组各一行表头"，自己那行插在队里，不能把队拆开）
            // 组序：CT → T → 观察者（与 HUD 顶部比分的 CT 在左一致）
            var rankA = TeamRank(a.Team);
            var rankB = TeamRank(b.Team);
            if (rankA != rankB) return rankA.CompareTo(rankB);

            // 组内：自己在前 → 击杀多在前 → 死亡少在前 → 名字
            if (a.IsSelf != b.IsSelf) return a.IsSelf ? -1 : 1;
            if (a.Kills != b.Kills) return b.Kills.CompareTo(a.Kills);
            if (a.Deaths != b.Deaths) return a.Deaths.CompareTo(b.Deaths);
            return string.CompareOrdinal(a.Name, b.Name);
        }

        private static int TeamRank(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.CT: return 0;
                case CsTeam.T: return 1;
                default: return 2;
            }
        }

        /// <summary>阵营组的显示顺序（记分板要按这个顺序插组标题）。</summary>
        public static int OrderOf(CsTeam team) => TeamRank(team);

        /// <summary>当前记录到的玩家数（供 HUD/日志自检）。</summary>
        public int Count => _entries.Count;
    }
}
