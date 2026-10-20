using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    public sealed partial class CsBotBrain
    {

        /// <summary>
        /// 消费导航报上来的"卡住"事件。连续卡住达到 <see cref="CsBotConst.StuckReplanStreak"/> 次就
        /// **重新选目标** —— 只"跳过当前路点 + 换向"在路线走完后这两步都是空操作，
        /// 于是机器人停在原地被反复判卡住。
        /// </summary>
        private void HandleStuck(CsActor self, float now)
        {
            if (!_nav.ConsumeStuck(out var moved, out var escalate)) return;
            if (!escalate) return;

            // 正在下包/拆包时不换目标（一移动进度就清零）；拆包任务的目标是 C4，换目标没有意义。
            if (self.UseProgress >= 0f) return;

            // 搜点 / 听声调查自带赶路超时，期间换目标只会让搜索半途而废（交战中不拦，交战的卡住恢复优先）。
            if (_searchKind != BotSearchKind.None && _targetId == 0 && !_visibleNow) return;
            if (_match.BombPlanted && self.Team == CsTeam.CT) return;

            //   没有这道闸时：0.5s 一次判定 / 2 次即升级 ⇒ 最快 1.0s 就换一次目标，而每个新目标都在
            //   86m 外 ⇒ 一次都没走完就换掉 ⇒ 目标反复横跳、净位移≈0。
            if (now < _nextStuckReplanAt)
            {
                RateWarn("stuckreplan.cooldown",
                    $"{_name} 连续卡住但仍处于换目标冷却期（{_nextStuckReplanAt - now:F1}s 后到期，" +
                    $"窗口 {CsBotConst.StuckReplanCooldownSeconds:F0}s）→ 本 tick 只换向不换目标");
                return;
            }

            _nextStuckReplanAt = now + CsBotConst.StuckReplanCooldownSeconds;

            ReplanObjective(self,
                $"连续卡住 {CsBotConst.StuckReplanStreak} 次（最近 {CsBotConst.StuckCheckInterval:F1}s 内位移 {moved:F2}m，" +
                $"原路线 '{_planRoute ?? "无"}'，剩余路点 {_nav.RemainingWaypoints}）", now);
        }



        /// <summary>1m 内最近的角色（含队友与敌人）；没有则返回 null。</summary>
        private CsActor NearestOtherActor(CsActor self, out float dist, out CsTeam team, out string name)
        {
            dist = float.MaxValue;
            team = self.Team;
            name = null;

            var actors = _match.Actors;
            CsActor best = null;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == self.Id || !a.IsAlive) continue;

                var d = a.Position - self.Position;
                d.y = 0f;
                var len = d.magnitude;
                if (len >= dist) continue;

                dist = len;
                best = a;
            }

            if (best != null)
            {
                team = best.Team;
                name = best.Name;
            }
            else dist = float.MaxValue;

            return best;
        }



        /// <summary>组装卡住 Warn 的一整行（字段顺序 = 排查时的阅读顺序）。</summary>
        private string BuildStuckLine(CsActor self, Vector3 move, float window, float moved,
            CsActor mate, bool mateBlocking, bool reachable)
        {
            var sb = new System.Text.StringBuilder(320);
            sb.Append("[卡住] ").Append(_name)
              .Append(" 状态=").Append(_state)
              .Append(" 连续 ").Append(window.ToString("F1")).Append("s 水平位移 ")
              .Append(moved.ToString("F2")).Append("m<").Append(CsBotConst.StuckDiagMoveEpsilon.ToString("F2"))
              .Append("m（本回合第 ").Append(_stuckDiagCount).Append(" 次，同一位置第 ")
              .Append(_stuckSamePosCount).Append(" 次）")
              .Append(" 位置=(").Append(self.Position.x.ToString("F2")).Append(',')
              .Append(self.Position.y.ToString("F2")).Append(',')
              .Append(self.Position.z.ToString("F2")).Append(')')
              .Append(" OnGround=").Append(self.OnGround);

            // 目标：交战追的是人、捡包追的是 C4，其余追本轮目标点。
            var kind = "路线目标";
            var target = _goalPos;
            if (_state == CsBotState.Engage)
            {
                var t = _match.Find(_targetId);
                target = t != null && t.IsAlive ? t.Position : _lastSeenPos;
                kind = "交战追击";
            }
            else if (IsPickupRun(self))
            {
                target = _match.BombPosition;
                kind = "捡 C4";
            }
            else if (_state == CsBotState.Plant) kind = "下包点";
            else if (_goalIsSite) kind = "包点/守位";

            var td = target - self.Position;
            td.y = 0f;
            sb.Append(" 目标=").Append(kind).Append('(')
              .Append(target.x.ToString("F1")).Append(',').Append(target.z.ToString("F1")).Append(')')
              .Append(" 距 ").Append(td.magnitude.ToString("F1")).Append("m")
              .Append(" CanReach=").Append(reachable)
              .Append(" 移动意图=(").Append(move.x.ToString("F2")).Append(',').Append(move.z.ToString("F2")).Append(')');

            var wp = _state == CsBotState.Camp && _holdSlot >= 0 && _holdSlot < _holdSpots.Count
                ? _holdSpots[_holdSlot]
                : _nav.CurrentTarget(self.Position, _goalPos);
            sb.Append(" 当前路点=(").Append(wp.x.ToString("F1")).Append(',').Append(wp.z.ToString("F1")).Append(')')
              .Append(" 剩余路点=").Append(_nav.RemainingWaypoints)
              .Append(" 路线=").Append(_planRoute ?? "无");

            var navReason = _nav.BlockedReason(self.Position, now: CsClock.Now);
            sb.Append(" 导航阻挡=").Append(navReason ?? "无记录");
            if (_map != null && _map.IsLoaded)
            {
                sb.Append(" 位图可走=").Append(_map.WalkableAt(self.Position.x, self.Position.z))
                  .Append(" 可站=").Append(_map.CanStand(self.Position));
            }

            // 1m 内的全部角色（不只最近一个）：队友推挤与敌人贴身是两种不同的卡住成因。
            sb.Append(" 1m内角色=");
            var listed = 0;
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == self.Id || !a.IsAlive) continue;

                var ad = a.Position - self.Position;
                ad.y = 0f;
                var adist = ad.magnitude;
                if (adist > CsBotConst.StuckNeighborRadius) continue;

                if (listed == 0) sb.Append('[');
                else sb.Append("; ");

                var isMate = a.Team == self.Team;
                sb.Append(isMate ? "队友 " : "敌方 ").Append(a.Name)
                  .Append('(').Append(adist.ToString("F2")).Append("m")
                  .Append(a.Id == (mate != null ? mate.Id : 0L)
                      ? (mateBlocking ? " 挡在移动方向上" : " 不在移动方向上")
                      : string.Empty)
                  .Append(')');
                listed++;
                if (listed >= 4) break;
            }

            if (listed == 0) sb.Append("无");
            else sb.Append(']');

            return sb.ToString();
        }



        /// <summary>
        /// 让行：沿移动方向的侧向 + 后退一步（对角），<see cref="CsBotConst.YieldSeconds"/> 秒内覆盖移动方向。
        /// 侧向符号优先"避开挡路的那个角色"；没有角色时按 id 奇偶取一侧（确定性，不掷随机数）。
        /// </summary>
        private void StartYield(CsActor self, Vector3 move, CsActor blocker, float now)
        {
            var fwd = move;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return;
            fwd.Normalize();

            var side = Vector3.Cross(Vector3.up, fwd);
            side.y = 0f;
            if (side.sqrMagnitude < 0.0001f) return;
            side.Normalize();

            var sign = (self.Id & 1L) == 0L ? 1f : -1f;
            if (blocker != null)
            {
                var toBlocker = blocker.Position - self.Position;
                toBlocker.y = 0f;
                if (Vector3.Dot(side, toBlocker) > 0f) sign = -1f;
                else sign = 1f;
            }

            var dir = side * sign - fwd * 0.6f;
            _yieldDir = dir.normalized;
            _yieldUntil = now + CsBotConst.YieldSeconds;
            _stuckProbeValid = false;      // 让行期间位移会变，重新取基准
        }



        /// <summary>
        /// 把走不到的点记入记忆（<see cref="CsBotConst.UnreachableGoalMemorySeconds"/> 秒），
        /// 守位表里有它就摘掉换下一个守位；否则换下一个目标。
        /// </summary>
        private void BanGoalAndReplan(CsActor self, Vector3 blocked, float now, string why)
        {
            var streak = _stuckAtGoalCount;
            var warn = now >= _banWarnNextAt;
            if (warn) _banWarnNextAt = now + StuckWarnDedupeSeconds;
            _unreachableGoal = blocked;
            _unreachableGoalUntil = now + CsBotConst.UnreachableGoalMemorySeconds;
            _stuckAtGoalCount = 0;
            _nextStuckReplanAt = now + CsBotConst.StuckReplanCooldownSeconds;   // 与 HandleStuck 共用冷却，避免同 tick 连换两次

            if (_holdSpots.Count >= 2 && _holdSlot >= 0 && _holdSlot < _holdSpots.Count)
            {
                var d = _holdSpots[_holdSlot] - blocked;
                d.y = 0f;
                if (d.magnitude <= CsBotConst.StuckNeighborRadius)
                {
                    var dropped = _holdSpots[_holdSlot];
                    _holdSpots.RemoveAt(_holdSlot);
                    _holdSlot = CsBotHoldSpots.PickSlot(_holdSpots, -1, self, _match.Actors);
                    _holdSwapAt = now + CsBotConst.HoldSwapSeconds;
                    if (StuckWarnAllowed(StuckWarnReasonReplan, now))
                    {
                        if (warn)
                        {
                            Game.Logger.Warn(Tag,
                                $"[卡住] {_name} 守位 ({dropped.x:F1},{dropped.z:F1}) 判为走不到（{why}，连续 {streak} 个窗口）" +
                                $"→ 摘掉该守位，换到 {(_holdSlot >= 0 ? _holdSpots[_holdSlot].ToString() : "无")}" +
                                $"（本包点剩 {_holdSpots.Count} 个守位）");
                        }
                    }
                    return;
                }
            }

            var ok = ReplanObjective(self,
                $"目标 ({blocked.x:F1},{blocked.z:F1}) 判为走不到（{why}，连续 {streak} 个窗口位移不足）", now);
            if (warn)
            {
                if (StuckWarnAllowed(StuckWarnReasonReplan, now))
                {
                    Game.Logger.Warn(Tag,
                        $"[卡住] {_name} 目标 ({blocked.x:F1},{blocked.z:F1}) 记入不可达记忆 {CsBotConst.UnreachableGoalMemorySeconds:F0}s" +
                        $"（{why}）→ {(ok ? "已换下一个目标" : "换目标失败（保留原目标）")}");
                }
            }
        }



        /// <summary>这个点是否在"走不到"记忆里（选目标 / 选守位 / 选下包点时跳过）。</summary>
        private bool IsBannedGoal(Vector3 p)
        {
            if (_unreachableGoalUntil <= CsClock.Now) return false;
            var d = p - _unreachableGoal;
            d.y = 0f;
            return d.sqrMagnitude <= 1f;
        }



        /// <summary>
        /// 跳跃意图：<b>只在"想走的那个方向上有可跳上去的台阶/箱沿"时才置 <c>intent.Jump</c></b>。
        ///
        /// <para><b>为什么写在这里（而不是 BotNavigator）</b>：导航只产出 Move 方向，
        /// Jump 的执行在 <c>CsMatch.UpdateBots</c>（<c>intent.Jump && OnGround</c> 时给
        /// <c>Velocity.y = JumpSpeed</c>），需要 <c>CsBotIntent</c> —— 只有大脑能写它。
        /// 过去全工程<b>没有任何分支给 intent.Jump 赋过值</b>（grep 无匹配），这就是"AI 不会跳"的根因。</para>
        ///
        /// <para><b>判据（两条都过才跳）</b>：</para>
        /// <list type="number">
        /// <item>沿 Move 方向前方 <see cref="JumpLookAhead"/> m 处探到的地面比脚下高
        /// <b>≤ StepUpHeight 且 > 5cm</b>（≤ StepUpHeight 否则根本跳不上去；> 5cm 排除平地微差）；</item>
        /// <item>头顶与目标地面之间**竖直通畅**（跳起 1.2m 内没有压着的天花板/横梁）。</item>
        /// </list>
        ///
        /// <para>冻结期（ThinkFreeze）与站桩状态（Idle）不跳 —— 那些 Move 是零向量，本来也不进这里。
        /// 冷却 <see cref="JumpCooldown"/> 防贴墙连跳；日志按 <see cref="RateWarn"/> 降频。</para>
        /// </summary>
        private void MaybeJump(CsActor self, ref CsBotIntent intent, float now)
        {
            if (intent.Jump) return;                       // 本 tick 已有别的分支要求跳
            if (self.OnGround == false) return;            // 空中不补跳
            if (now < _nextJumpAt) return;
            if (intent.Move.sqrMagnitude < 0.0001f) return; // 没在移动（守点 / 下包 / 拆包 / 交战站桩）

            var move = intent.Move;
            move.y = 0f;
            move.Normalize();

            // ① 前方探地面：脚下 y 与 (前方 JumpLookAhead) 处的地面 y 比较
            var ahead = self.Position + move * JumpLookAhead;
            if (_map == null || !_map.IsLoaded) return;
            if (!_map.TrySampleGround(ahead, out var ground, out var normal, CsConst.GroundCheckDistance + 3f))
                return;

            var rise = ground.y - self.Position.y;
            if (rise <= 0.05f) return;                      // 平地或下坡：不用跳
            if (rise > CsConst.StepUpHeight) return;        // 跳也上不去的高台：不浪费跳
            if (normal.y < CsConst.MaxStandableSlopeNormalZ) return;   // 陡面跳上去也站不住

            // ② 头顶通暢：跳起高度约 v²/2g = 6.82²/(2×20.32) ≈ 1.15m，取 1.2m
            var head = new Vector3(ahead.x, ground.y + 1.2f, ahead.z);
            var body = self.Position + Vector3.up * CsConst.StepUpHeight;
            if (Physics.Linecast(body, head, 1 << PhysicsLayers.World, QueryTriggerInteraction.Ignore))
                return;

            intent.Jump = true;
            _nextJumpAt = now + JumpCooldown;
            RateWarn("bot.jump",
                $"{_name} 判定可跳：方向 ({move.x:F2}, {move.z:F2})、前方 {JumpLookAhead:F1}m 处高差 " +
                $"{rise:F2}m ≤ StepUpHeight {CsConst.StepUpHeight:F2}m、头顶通畅 → 置 intent.Jump（冷却 {JumpCooldown:F1}s）");
        }



    }
}
