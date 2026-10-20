using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using Cs16.Module.Bot.Bt;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 行为树骨架：树结构在 <see cref="BuildTree"/> 里组装一次，之后每个 tick
    /// <see cref="Think"/> 只做"取 self → 填 tick 上下文 → 跑树 → 收尾"。
    ///
    /// <para>每个节点都是"把已有的一段决策原样包起来"：节点自身不持有玩法数值，
    /// 只按顺序把控制流表达出来。叶子要读写的每 tick 状态放在本类的
    /// <c>_tick*</c> 字段上（nested class 能访问外层私有成员）。</para>
    /// </summary>
    public sealed partial class CsBotBrain
    {
        /// <summary>根节点（每个 brain 一棵；构造时组装）。</summary>
        private BtNode _btRoot;

        /// <summary>本 tick 的 self（树节点共享；避免每个叶子各查一次）。</summary>
        private CsActor _tickSelf;

        /// <summary>本 tick 正在填的意图（struct，叶子按 <c>ref</c> 交给既有方法写）。</summary>
        private CsBotIntent _tickIntent;

        /// <summary>本 tick 的顶层分支（<see cref="CommitTacticalDecision"/> 的结果，每 tick 只算一次）。</summary>
        private string _tickBranch;

        /// <summary>本 tick 是否跳过收尾（死亡分支原地站桩：不跟随行进朝向、不跑瞄准平滑 / 卡住诊断 / 无线电）。</summary>
        private bool _tickSkipTail;

        // ==================================================================
        //  树结构
        // ==================================================================
        /// <summary>
        /// 组装行为树。分支顺序 = 现在的 tick 决策顺序：
        /// 死亡 → 冻结期 → 非进行中 → 感知 / 卡住恢复 / 投掷 → 战术分支（续用 → 交战 → 炸弹任务 → 推进）。
        /// </summary>
        private BtNode BuildTree()
        {
            return new BtSelector("root",
                new BtSequence("alive-gate",
                    new BtCondition("!alive", b => b._tickSelf != null && !b._tickSelf.IsAlive),
                    new LeafStandNotAlive()),

                new BtSequence("freeze-gate",
                    new BtCondition("phase==Freeze", b => b._match.Phase == CsRoundPhase.Freeze),
                    new LeafFreeze()),

                new BtSequence("phase-gate",
                    new BtCondition("phase!=Live", b => b._match.Phase != CsRoundPhase.Live),
                    new LeafStandRoundEnd()),

                new BtSequence("live",
                    new LeafPerception(),
                    new LeafHandleStuck(),
                    // 任务层在感知之后、所有战术分支之前推导一次（每 0.5s 真算，其余 tick 只做观测）。
                    new LeafTaskTick(),
                    new BtSelector("live-body",
                        new BtSequence("grenade", new LeafGrenade()),
                        new BtSequence("tactical",
                            new LeafTacticalBranch(),
                            new BtSelector("tactical-branches",
                                new BtSequence("use-in-progress",
                                    new BtCondition("use-progress", b => b.UseInProgress()),
                                    new LeafContinueUse()),
                                new BtSequence("engage",
                                    new BtCondition("branch==Engage", b => b._tickBranch == CsBotFsmStates.Engage),
                                    // 任务层的 disposition 在这里把关：无视敌人（下包/拆包/逃爆）时不进交战。
                                    new BtCondition("disposition-allows", b => b.EngageAllowed()),
                                    new BtSelector("engage-body",
                                        // 狙击蹲守 → 掩体 → peek → 后撤 → 换弹 → 交战。
                                        // 前三条在 CsBotBrain.Combat.cs 里（持狙远距蹲守 / 挡视线的掩体点 / 2s 探身节奏）。
                                        new BtSequence("engage-sniper-hold",
                                            new BtCondition("sniper-hold-due",
                                                b => !BranchDisabledForTest && b.SniperHoldDue(b._tickSelf)),
                                            new LeafSniperHold()),
                                        new BtSequence("engage-hide",
                                            new BtCondition("hide-due", b => !BranchDisabledForTest && b.HideDue()),
                                            new BtCooldown("hide-cooldown", HideCooldown, new LeafHide())),
                                        new BtSequence("engage-peek",
                                            new BtCondition("peek-due",
                                                b => !BranchDisabledForTest && b.PeekDue(b._tickSelf)),
                                            new LeafPeek()),
                                        // 撤退 → 换弹 → 交战：残血且敌人还远才退；退的过程中本分支独占，
                                        // 窗口结束后 Success，由 BtCooldown 冷却一段时间再允许下一次后撤。
                                        new BtSequence("engage-retreat",
                                            new BtCondition("retreat-due", b => b.RetreatDue()),
                                            new BtCooldown("retreat-cooldown", CsBotConst.RetreatCooldownSeconds,
                                                new LeafRetreat())),
                                        // 弹匣见底且当前安全：先换弹再打，换完条件自然不成立。
                                        new BtSequence("engage-reload",
                                            new BtCondition("reload-due", b => b.ShouldReloadNow()),
                                            new LeafReload()),
                                        new LeafEngage())),
                                // 任务层独占的三支（撤离 / 守爆点 / 跟随队长）：在交战之后、
                                // 搜索与炸弹任务之前，命中即本 tick 结束。
                                new BtSequence("task-escape",
                                    new BtCondition("task==escape", b => b._task == CsBotTask.EscapeFromBomb),
                                    new LeafTaskEscape()),
                                new BtSequence("task-hold-bomb",
                                    new BtCondition("task==hold-bomb",
                                        b => b._task == CsBotTask.HoldPosition && b._match.BombPlanted),
                                    new LeafTaskHoldBomb()),
                                new BtSequence("task-follow",
                                    new BtCondition("task==follow", b => b._task == CsBotTask.Follow),
                                    new LeafTaskFollow()),
                                new BtSequence("search",
                                    new BtAction("search", (b, dt, n) => b.TrySearchStep(n) ? BtStatus.Success : BtStatus.Failure)),
                                new BtSequence("objective-commit",
                                    new BtCondition("objective-commit", b => b.ObjectiveCommit()),
                                    new LeafBombObjective()),
                                new LeafAdvance())))));
        }

        /// <summary>本 tick 是否该走炸弹任务入口。两条来源：
        /// ① 顶层分支是 <c>Objective</c>；
        /// ② 顶层分支是 <c>Engage</c> 但交战目标已死 / 已离场（<see cref="LeafEngage"/> 返回 Failure 并清掉目标）
        /// —— 此时本 tick 直接落到炸弹任务，不空等一个 tick。</summary>
        private bool ObjectiveCommit()
        {
            return _tickBranch == CsBotFsmStates.Objective || _tickBranch == CsBotFsmStates.Engage;
        }

        /// <summary>本 tick 是否走"续用"，条件 = 不在交战分支、有进度、且没有贴脸敌人。</summary>
        private bool UseInProgress()
        {
            return _tickBranch != CsBotFsmStates.Engage &&
                   _tickSelf != null &&
                   _tickSelf.UseProgress >= 0f &&
                   _closestVisibleDist > CsBotConst.EnemyTooCloseRange;
        }

        // ==================================================================
        //  叶子节点
        // ==================================================================

        /// <summary>死亡：状态置 Idle、注视推回朝前，并让本 tick 跳过收尾。</summary>
        private sealed class LeafStandNotAlive : BtNode
        {
            public LeafStandNotAlive() { Name = "stand-not-alive"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                var self = b._tickSelf;
                b.SetState(CsBotState.Idle, now);
                b._tickIntent.State = CsBotState.Idle;
                b._tickIntent.Crouch = false;
                b._tickIntent.AimPoint = LookAhead(self);
                b._tickSkipTail = true;
                return BtStatus.Success;
            }
        }

        /// <summary>冻结期：买枪 + 转头（<see cref="ThinkFreeze"/> 原样）。</summary>
        private sealed class LeafFreeze : BtNode
        {
            public LeafFreeze() { Name = "think-freeze"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b._tickIntent = b.ThinkFreeze(b._tickSelf, b._tickIntent, now);
                return BtStatus.Success;
            }
        }

        /// <summary>回合结束 / 比赛结束 / 无阶段：站着不动（模拟此时也不接受移动/开火）。</summary>
        private sealed class LeafStandRoundEnd : BtNode
        {
            public LeafStandRoundEnd() { Name = "stand-round-end"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                var self = b._tickSelf;
                b.SetState(CsBotState.Idle, now);
                b._tickIntent.State = CsBotState.Idle;
                b._tickIntent.AimPoint = b._goalValid ? WithEyeHeight(b._goalPos) : LookAhead(self);
                return BtStatus.Success;
            }
        }

        /// <summary>感知：本 tick 的可见敌/友、交战目标与听觉线索。</summary>
        private sealed class LeafPerception : BtNode
        {
            public LeafPerception() { Name = "perception"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b.UpdatePerception(b._tickSelf, now);
                return BtStatus.Success;
            }
        }

        /// <summary>卡住恢复：上一 tick 导航报了"卡住"，连续卡住就换目标（先于本 tick 的决策）。</summary>
        private sealed class LeafHandleStuck : BtNode
        {
            public LeafHandleStuck() { Name = "handle-stuck"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b.HandleStuck(b._tickSelf, now);
                return BtStatus.Success;
            }
        }

        /// <summary>投掷物（角色分工）：该用的时候先扔一颗 —— 扔雷要"切雷 → 转视角 → 扣扳机"，
        /// 飞行 / 引信 / 伤害全部由模拟结算（<c>CsInventory.ThrowGrenade</c>），这里只提交意图。
        /// Success ⇒ 本 tick 的决策到此为止。</summary>
        private sealed class LeafGrenade : BtNode
        {
            public LeafGrenade() { Name = "grenade"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                return b.TryThrowGrenade(b._tickSelf, ref b._tickIntent, now)
                    ? BtStatus.Success
                    : BtStatus.Failure;
            }
        }

        /// <summary>提交顶层战术决策（守卫见 <see cref="CommitTacticalDecision"/>）：本 tick 的分支由状态机给。</summary>
        private sealed class LeafTacticalBranch : BtNode
        {
            public LeafTacticalBranch() { Name = "tactical-branch"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b._tickBranch = b.CommitTacticalDecision(b._tickSelf, now);
                return BtStatus.Success;
            }
        }

        /// <summary>正在下包/拆包：没人贴脸就不打断（一移动进度就清零，与模拟的判定一致）。</summary>
        private sealed class LeafContinueUse : BtNode
        {
            public LeafContinueUse() { Name = "continue-use"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b._tickIntent = b.ContinueUse(b._tickSelf, b._tickIntent, now);
                return BtStatus.Success;
            }
        }

        /// <summary>
        /// 残血后撤：窗口内返回 Running（本分支独占、不扣扳机），窗口结束返回 Success
        /// （外层 <see cref="BtCooldown"/> 从这一刻起算冷却）。
        /// </summary>
        private sealed class LeafRetreat : BtNode
        {
            public LeafRetreat() { Name = "retreat-low-hp"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                return b.RetreatStep(b._tickSelf, ref b._tickIntent, now);
            }
        }

        /// <summary>主动换弹：提交换弹意图 + 后退到掩体方向，本 tick 不开火。</summary>
        private sealed class LeafReload : BtNode
        {
            public LeafReload() { Name = "reload-low-mag"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b.ReloadStep(b._tickSelf, ref b._tickIntent, now);
                return BtStatus.Success;
            }
        }

        /// <summary>交战分支：目标还在记忆期内且仍活着 → 进 Engage；已死/已离场则立刻忘掉它，
        /// 本 tick 直接落到炸弹任务（不空等一个 tick）。</summary>
        private sealed class LeafEngage : BtNode
        {
            public LeafEngage() { Name = "engage"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                var target = b._match.Find(b._targetId);
                if (target == null || !target.IsAlive)
                {
                    b._targetId = 0;
                    return BtStatus.Failure;
                }

                b._tickIntent = b.Engage(b._tickSelf, b._tickIntent, now);
                return BtStatus.Success;
            }
        }

        /// <summary>炸弹任务（下包 / 拆包 / 捡包）。T 的捡包、CT 的拆包、持包者的下包都在这一个入口里
        /// 按队伍分发（<see cref="TryBombObjective"/> → <see cref="TryDefuse"/> / <see cref="TryPlantOrPickup"/>）；
        /// <c>ref intent</c> 是契约：写进去的 Move/State/Use 必须落到本 tick 的意图上。</summary>
        private sealed class LeafBombObjective : BtNode
        {
            public LeafBombObjective() { Name = "objective-commit"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                return b.TryBombObjective(b._tickSelf, ref b._tickIntent, now)
                    ? BtStatus.Success
                    : BtStatus.Failure;
            }
        }

        /// <summary>任务层 tick：每 tick 观测士气 / 炸弹，每 0.5s 重推一次任务（见 <see cref="TaskTick"/>）。</summary>
        private sealed class LeafTaskTick : BtNode
        {
            public LeafTaskTick() { Name = "task-tick"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b.TaskTick(b._tickSelf, now);
                return BtStatus.Success;
            }
        }

        /// <summary>撤离爆点（<see cref="CsBotTask.EscapeFromBomb"/>）：朝包点外的落点跑。</summary>
        private sealed class LeafTaskEscape : BtNode
        {
            public LeafTaskEscape() { Name = "task-escape"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b.EscapeStep(b._tickSelf, ref b._tickIntent, now);
                return BtStatus.Success;
            }
        }

        /// <summary>守爆点（<see cref="CsBotTask.HoldPosition"/>）：在爆点外一圈站住盯住 C4。</summary>
        private sealed class LeafTaskHoldBomb : BtNode
        {
            public LeafTaskHoldBomb() { Name = "task-hold-bomb"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b.HoldBombStep(b._tickSelf, ref b._tickIntent, now);
                return BtStatus.Success;
            }
        }

        /// <summary>跟随队长（<see cref="CsBotTask.Follow"/>）：队长已不在场时交回后续分支。</summary>
        private sealed class LeafTaskFollow : BtNode
        {
            public LeafTaskFollow() { Name = "task-follow"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                return b.FollowStep(b._tickSelf, ref b._tickIntent, now)
                    ? BtStatus.Success
                    : BtStatus.Failure;
            }
        }

        /// <summary>推进 / 守点（路线走完之后的换目标也在 <see cref="Advance"/> 里）。</summary>
        private sealed class LeafAdvance : BtNode
        {
            public LeafAdvance() { Name = "advance"; }

            public override BtStatus Tick(CsBotBrain b, float dt, float now)
            {
                b._tickIntent = b.Advance(b._tickSelf, b._tickIntent, now);
                return BtStatus.Success;
            }
        }
    }
}
