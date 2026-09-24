using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人买枪（任务书 §4.2 买枪表 / 验收表 B5）。
    ///
    /// <para><b>唯一入口</b>：<c>ICsMatch.TryBuyFor(actorId, weaponId, out reason)</c> —— 机器人自己没有
    /// "替某个 actor 买枪"之外的捷径（<c>ICsMatch.TryBuy</c> / <c>SwitchWeapon</c> 只作用于**本地玩家**，别用）。
    /// 失败的 reason 由模拟给出，这里据此**降级买更便宜的**并打 Warn（不许静默）。</para>
    ///
    /// <para><b>档位</b>：<see cref="CsBotProfile.BuyBudgetTier"/>（0=便宜 / 1=中 / 2=贵），
    /// 数值只从 <see cref="CsBotProfile.For"/> 取，本文件不另写一套。</para>
    /// </summary>
    public sealed class BotBuyLogic
    {
        private const string Tag = BotModule.Tag;

        private int _round = -1;
        private bool _done;
        private int _attempts;
        private float _nextAttemptAt;
        private float _freezeSince;
        private bool _warnedBuyZone;
        private string _ownerName = "bot";

        public void SetOwner(string name)
        {
            if (!string.IsNullOrEmpty(name)) _ownerName = name;
        }

        /// <summary>本轮是否已完成买枪决策（含"钱不够，放弃"）。</summary>
        public bool DoneThisRound => _done;

        /// <summary>回合切换时复位（由 brain 在检测到 RoundNumber 变化时调用）。</summary>
        public void OnRoundStart(int roundNumber)
        {
            _round = roundNumber;
            _done = false;
            _attempts = 0;
            _nextAttemptAt = 0f;
            _freezeSince = 0f;
            _warnedBuyZone = false;
        }

        /// <summary>
        /// 冻结期每 tick 调一次。买完后 <paramref name="switchTo"/> 给出"应该拿在手里的武器"
        /// （交给 <c>CsBotIntent.SwitchTo</c> 由模拟执行，AI 不自己换枪）。
        /// </summary>
        public void Tick(ICsMatch match, CsActor self, in CsBotProfile profile, float now, out string switchTo)
        {
            switchTo = null;
            if (match == null || self == null) return;
            if (match.Phase != CsRoundPhase.Freeze) return;
            if (_round != match.RoundNumber) OnRoundStart(match.RoundNumber);
            if (_done) return;

            if (!self.InBuyZone)
            {
                // 冻结期全员在出生点，买枪区覆盖出生点（agent-02 的 BuyZone 标记或出生点兜底）。
                // 站不进去 = 地图标记问题，必须留痕（降频：每轮最多一次，且 1.5s 后才报）。
                if (_freezeSince <= 0f) _freezeSince = now;
                if (!_warnedBuyZone && now - _freezeSince >= CsBotConst.BuyZoneWarnDelay)
                {
                    _warnedBuyZone = true;
                    Game.Logger.Warn(Tag,
                        $"{_ownerName} 在冻结期 {CsBotConst.BuyZoneWarnDelay:F1}s 后仍不在买枪区（位置 {self.Position}，" +
                        $"阵营 {self.Team}）→ 本轮不买枪。多半是地图缺 {CsMarkersFor(self.Team)} 标记");
                }
                return;
            }

            if (now < _nextAttemptAt) return;

            _attempts++;
            if (_attempts > CsBotConst.MaxBuyAttempts)
            {
                _done = true;
                Game.Logger.Warn(Tag,
                    $"{_ownerName} 买枪尝试已达上限 {CsBotConst.MaxBuyAttempts} 次，本轮放弃（余额 ${self.Money}）");
                return;
            }
            _nextAttemptAt = now + CsBotConst.BuyRetryInterval;

            _done = RunPlan(match, self, profile.BuyBudgetTier, out switchTo);
            if (_done) _round = match.RoundNumber;
        }

        // ==================================================================
        //  三档买枪计划
        // ==================================================================
        private bool RunPlan(ICsMatch match, CsActor self, int tier, out string switchTo)
        {
            switchTo = null;
            _ownerName = self.Name;

            // ★ 验收表 B5 的证据：每回合一次，说明"这一档想买什么"（金额/现有装备都记下来，
            //   便于事后核对"为什么他没买"）。所以即使后面全部买失败，这条决策日志也在。
            Game.Logger.Info(Tag,
                $"{self.Name}({self.Team}) 冻结期买枪决策：tier={tier}（{TierName(tier)}）" +
                $" 现有主武器={self.PrimaryWeapon ?? "无"} 护甲={self.Armor} 拆弹器={self.HasDefuser} 余额=${self.Money}");

            switch (tier)
            {
                case 0: PlanCheap(match, self); break;
                case 1: PlanMid(match, self); break;
                case 2: PlanExpensive(match, self); break;
                default:
                    Game.Logger.Warn(Tag,
                        $"{self.Name} 的 BuyBudgetTier={tier} 不在 0~2 内（CsBotProfile 被改过？）→ 按最便宜档处理");
                    PlanCheap(match, self);
                    break;
            }

            // 买完把主武器拿在手里（模拟执行 SwitchTo；GivePurchased 已经切过一次，这里是兜底）
            if (!string.IsNullOrEmpty(self.PrimaryWeapon) && self.ActiveWeapon != self.PrimaryWeapon)
            {
                switchTo = self.PrimaryWeapon;
            }

            return true;
        }

        /// <summary>tier 0（Easy）：优先 deagle → 不够就保持默认手枪（glock18/usp）+ 背心。</summary>
        private void PlanCheap(ICsMatch match, CsActor self)
        {
            var deagle = CsWeapons.Get(CsWeapons.Deagle);
            if (string.IsNullOrEmpty(self.SecondaryWeapon) || self.SecondaryWeapon == CsWeapons.DefaultPistol(self.Team))
            {
                if (deagle != null && self.Money >= deagle.Price)
                {
                    Buy(match, self, CsWeapons.Deagle, "tier0 首选手枪");
                }
                else
                {
                    var fallback = self.SecondaryWeapon ?? CsWeapons.DefaultPistol(self.Team);
                    Game.Logger.Info(Tag,
                        $"{self.Name}({self.Team}/tier0) 余额 ${self.Money} 买不起 {deagle?.DisplayName ?? CsWeapons.Deagle}" +
                        $"（${deagle?.Price ?? 0}）→ 保持默认手枪 {fallback}");
                }
            }

            BuyVest(match, self, tier2: false);
        }

        /// <summary>tier 1（Normal）：mp5 → galil(T)/famas(CT) 里买得起的一把 + 背心（+ CT 拆弹器）。</summary>
        private void PlanMid(ICsMatch match, CsActor self)
        {
            if (string.IsNullOrEmpty(self.PrimaryWeapon))
            {
                var bought = Buy(match, self, CsWeapons.Mp5, "tier1 冲锋枪") ||
                             Buy(match, self, self.Team == CsTeam.T ? CsWeapons.Galil : CsWeapons.Famas, "tier1 步枪降级");
                if (!bought)
                {
                    Game.Logger.Warn(Tag,
                        $"{self.Name}({self.Team}/tier1) 余额 ${self.Money} 连 " +
                        $"{CsWeapons.Mp5}/{CsWeapons.Galil}/{CsWeapons.Famas} 都买不起 → 降级到 tier0 方案");
                    PlanCheap(match, self);
                    return;
                }
            }

            BuyVest(match, self, tier2: false);
            BuyDefuser(match, self);
        }

        /// <summary>tier 2（Hard）：awp（30%）或 ak47(T)/m4a1(CT) + vesthelm + HE 手雷（+ CT 拆弹器）。</summary>
        private void PlanExpensive(ICsMatch match, CsActor self)
        {
            if (string.IsNullOrEmpty(self.PrimaryWeapon))
            {
                var rifle = self.Team == CsTeam.T ? CsWeapons.Ak47 : CsWeapons.M4A1;
                var awp = CsWeapons.Get(CsWeapons.Awp);
                // AWP 掷：**玩法**（决定该 bot 的火力档 ⇒ 影响经济与回合结果）。
                // 走 CsRng 的 BotBuy 流：每回合每 bot 抽一次，序列必须能从本局 seed 重放。
                var wantAwp = awp != null && self.Money >= awp.Price &&
                              CsRng.Stream(CsRngStream.BotBuy).NextFloat() < CsBotConst.AwpChanceTier2;

                if (wantAwp)
                {
                    Game.Logger.Info(Tag, $"{self.Name}({self.Team}/tier2) 掷中 AWP（{CsBotConst.AwpChanceTier2:P0} 概率）→ 尝试买入");
                    if (!Buy(match, self, CsWeapons.Awp, "tier2 狙击枪"))
                    {
                        Buy(match, self, rifle, "tier2 狙击失败降级步枪");
                    }
                }
                else
                {
                    Buy(match, self, rifle, "tier2 步枪");
                }
            }

            BuyVest(match, self, tier2: true);
            BuyDefuser(match, self);

            var he = CsWeapons.Get(CsWeapons.HeGrenade);
            if (he != null && self.Money >= he.Price)
            {
                Buy(match, self, CsWeapons.HeGrenade, "tier2 手雷");
            }
            else
            {
                Game.Logger.Info(Tag,
                    $"{self.Name}({self.Team}/tier2) 余额 ${self.Money} < 手雷 ${he?.Price ?? 0} → 跳过高爆手雷");
            }
        }

        /// <summary>档位的话术（对应任务书 §4.2 的买枪表，只用于日志）。</summary>
        private static string TierName(int tier)
        {
            switch (tier)
            {
                case 0: return "便宜档：deagle / 默认手枪 + 背心";
                case 1: return "中档：mp5 → galil/famas + 背心 + 拆弹器";
                case 2: return "贵档：awp(30%) / ak47 / m4a1 + 头盔 + 手雷 + 拆弹器";
                default: return "未知档位（按便宜档处理）";
            }
        }

        // ==================================================================
        //  通用购买（失败必打 Warn + 由调用方降级）
        // ==================================================================
        private bool Buy(ICsMatch match, CsActor self, string weaponId, string why)
        {
            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                Game.Logger.Warn(Tag, $"{self.Name}(tier 买枪) 目标武器 '{weaponId}' 不在武器表里（{why}）→ 跳过");
                return false;
            }

            if (self.Money < def.Price)
            {
                Game.Logger.Warn(Tag,
                    $"{self.Name}({self.Team}) 买不起 {def.DisplayName}（${def.Price} > 余额 ${self.Money}，{why}）→ 降级");
                return false;
            }

            if (!match.TryBuyFor(self.Id, weaponId, out var reason))
            {
                // 模拟内部已经打过一条 Warn；这里补上"是谁 / 哪个档位 / 打算买什么"的上下文
                Game.Logger.Warn(Tag,
                    $"{self.Name}({self.Team}/tier) 买枪失败：{def.DisplayName}（{why}）原因={reason ?? "未知"} → 降级");
                return false;
            }

            Game.Logger.Info(Tag,
                $"{self.Name}({self.Team}) 买入 {def.DisplayName}（{why}）价格 ${def.Price} 余额 ${self.Money}");
            return true;
        }

        private void BuyVest(ICsMatch match, CsActor self, bool tier2)
        {
            if (self.Armor > 0) return;

            var vestHelm = CsWeapons.Get(CsWeapons.VestHelm);
            var vest = CsWeapons.Get(CsWeapons.Vest);

            if (tier2 && vestHelm != null && self.Money >= vestHelm.Price)
            {
                Buy(match, self, CsWeapons.VestHelm, "护甲（含头盔）");
                return;
            }

            if (vest != null && self.Money >= vest.Price)
            {
                Buy(match, self, CsWeapons.Vest, tier2 ? "护甲（头盔买不起，降级）" : "护甲");
            }
            else
            {
                Game.Logger.Info(Tag, $"{self.Name}({self.Team}) 余额 ${self.Money} 买不起护甲（${vest?.Price ?? 0}）→ 跳过");
            }
        }

        private void BuyDefuser(ICsMatch match, CsActor self)
        {
            if (self.Team != CsTeam.CT || self.HasDefuser) return;

            var kit = CsWeapons.Get(CsWeapons.Defuser);
            if (kit != null && self.Money >= kit.Price)
            {
                Buy(match, self, CsWeapons.Defuser, "拆弹器（拆包果断）");
            }
        }

        private static string CsMarkersFor(CsTeam team)
        {
            return team == CsTeam.T ? CsMarkers.BuyZoneT : CsMarkers.BuyZoneCT;
        }
    }
}
