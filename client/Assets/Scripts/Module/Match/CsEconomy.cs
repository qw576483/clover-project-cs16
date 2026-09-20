using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 经济系统：起始金、上限、回合胜负奖励、连败递增奖励、击杀/下包/拆包奖励。
    ///
    /// <para><b>每一笔变动都打一条 <c>Game.Logger.Info</c>（金额 + 原因）</b> —— 这是验收证据。</para>
    /// </summary>
    public sealed class CsEconomy
    {
        private const string Tag = CsMatch.Tag;

        private readonly CsMatch _m;

        private int _lossStreakT;
        private int _lossStreakCT;

        internal CsEconomy(CsMatch m)
        {
            _m = m;
        }

        public void Reset()
        {
            _lossStreakT = 0;
            _lossStreakCT = 0;
        }

        /// <summary>半场交换 / 重开时清空连败计数（官方行为）。</summary>
        public void ResetLossStreaks()
        {
            _lossStreakT = 0;
            _lossStreakCT = 0;
        }

        public int LossStreak(CsTeam team)
        {
            return team == CsTeam.T ? _lossStreakT : team == CsTeam.CT ? _lossStreakCT : 0;
        }

        /// <summary>
        /// 金钱变动（唯一入口）。负数=支出，正数=收入。
        /// 会夹在 [0, <see cref="CsConst.MaxMoney"/>] 内，并把"请求值 vs 实际值"都记进日志。
        /// </summary>
        public void Add(CsActor a, int delta, string reason)
        {
            if (a == null) return;
            if (a.Team == CsTeam.Spectator)
            {
                _m.RateWarn("economy.spectator", $"观战者 {a.Name} 不应有金钱变动（{delta} / {reason}）");
                return;
            }
            if (delta == 0) return;

            var before = a.Money;
            var after = Mathf.Clamp(before + delta, 0, CsConst.MaxMoney);
            a.Money = after;
            var actual = after - before;

            Game.Logger.Info(Tag,
                $"[经济] {a.Name}({a.Team}) ${before} → ${after}（请求 {delta:+#;-#;0}，实际 {actual:+#;-#;0}；{reason}）" +
                (actual != delta ? " [已触及上下限]" : string.Empty));
        }

        /// <summary>回合奖励标准（按胜负原因区分）。</summary>
        public static int RoundWinReward(CsRoundEndReason reason)
        {
            switch (reason)
            {
                case CsRoundEndReason.BombExploded: return CsConst.RewardRoundWinBombExplode;
                case CsRoundEndReason.BombDefused: return CsConst.RewardRoundWinBombDefuse;
                case CsRoundEndReason.TimeExpired: return CsConst.RewardRoundWinTimeExpired;
                case CsRoundEndReason.AllTargetsEliminated: return CsConst.RewardRoundWinElimination;
                default: return CsConst.RewardRoundWinElimination;
            }
        }

        /// <summary>连败补偿：第 1 败 = 基数，之后每败 +step，封顶。</summary>
        public static int LossBonus(int streak)
        {
            if (streak <= 0) return 0;
            var bonus = CsConst.RewardLossBase + CsConst.RewardLossStep * (streak - 1);
            return Mathf.Min(bonus, CsConst.RewardLossMax);
        }

        /// <summary>回合结束结算：胜方拿胜利奖励、败方拿连败补偿。</summary>
        public void AwardRoundEnd(CsTeam winner, CsRoundEndReason reason)
        {
            if (winner != CsTeam.T && winner != CsTeam.CT)
            {
                Game.Logger.Warn(Tag, $"AwardRoundEnd：胜方非法（{winner}），跳过结算");
                return;
            }

            var loser = winner == CsTeam.T ? CsTeam.CT : CsTeam.T;

            var winReward = RoundWinReward(reason);
            AwardTeam(winner, winReward, $"回合胜利奖励（{CsMatch.ReasonText(reason)}）");

            if (winner == CsTeam.T) _lossStreakT = 0;
            else _lossStreakCT = 0;

            if (loser == CsTeam.T) _lossStreakT++;
            else _lossStreakCT++;

            var streak = LossStreak(loser);
            var lossBonus = LossBonus(streak);
            AwardTeam(loser, lossBonus, $"连败补偿（第 {streak} 连败，{CsMatch.ReasonText(reason)}）");

            Game.Logger.Info(Tag,
                $"[经济] 回合结算：胜方 {winner} 每人 +${winReward}；败方 {loser} 每人 +${lossBonus}（连败 {streak}）");
        }

        private void AwardTeam(CsTeam team, int amount, string reason)
        {
            if (amount <= 0) return;
            var list = _m.ActorList;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a.Team != team) continue;
                Add(a, amount, reason);
            }
        }
    }
}
