using CloverEngine;
using Cs16.Core;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 回合系统：阶段机（Freeze → Live → RoundEnd → 下一回合）、比分、半场交换、比赛结束判定。
    ///
    /// <para>胜负判定四种原因全部实现（<see cref="CsRoundEndReason"/>）：
    /// 炸弹爆炸(T 胜) / 拆包成功(CT 胜) / 一方被全歼(剩下一方胜) / 时间到(CT 胜)。
    /// <b>炸弹已下包后时间到不结束比赛</b> —— 一直等到爆炸或拆包。</para>
    /// </summary>
    public sealed class CsRound
    {
        private const string Tag = CsMatch.Tag;

        private readonly CsMatch _m;

        private CsRoundPhase _phase = CsRoundPhase.None;
        private float _phaseTimer;
        private int _roundNumber;
        private int _scoreT;
        private int _scoreCT;
        private bool _halfSwapped;

        /// <summary>
        /// 买枪期剩余秒数。它与 <see cref="_phaseTimer"/> 是**两个独立的计时器**（原版行为）：
        /// 回合开始同时启动，冻结期（<c>mp_freezetime</c> 4 s）结束不结束买枪期 ——
        /// 买枪期是 <c>mp_buytime</c> <b>15 s</b>（出处 <c>server.cfg:42</c>）。判定见 <see cref="BuyTimeLeft"/>。
        /// </summary>
        private float _buyTimer;
        /// <summary>"买枪期结束"只打一条日志（回合内一帧一次会刷屏）。</summary>
        private bool _buyExpiredLogged;

        internal CsRound(CsMatch m)
        {
            _m = m;
        }

        // ==================================================================
        //  只读状态
        // ==================================================================
        public CsRoundPhase Phase => _phase;
        public float PhaseTimeLeft => _phaseTimer;
        /// <summary>
        /// 买枪期剩余秒数（&gt; 0 = 还能买枪）。**与 <see cref="PhaseTimeLeft"/> 无关** ——
        /// 冻结期结束后它仍可能有余量（原版规则：买枪期 15 s &gt; 冻结期 4 s）。
        /// </summary>
        public float BuyTimeLeft => _buyTimer;
        public int RoundNumber => _roundNumber;
        public int ScoreT => _scoreT;
        public int ScoreCT => _scoreCT;

        /// <summary>半场是否已经交换过（由 CsMatch 在换边时置位，防止重复交换）。</summary>
        public bool HalfSwapped
        {
            get => _halfSwapped;
            set => _halfSwapped = value;
        }

        /// <summary>距离"16 胜"还差几个回合的阈值（RoundsPerHalf + 1）。</summary>
        private int WinTarget
        {
            get
            {
                var cfg = _m.Cfg;
                return (cfg != null ? cfg.RoundsPerHalf : CsConst.RoundsPerHalf) + 1;
            }
        }

        // ==================================================================
        //  生命周期
        // ==================================================================
        public void Reset()
        {
            _phase = CsRoundPhase.None;
            _phaseTimer = 0f;
            _roundNumber = 0;
            _scoreT = 0;
            _scoreCT = 0;
            _halfSwapped = false;
            _buyTimer = 0f;
            _buyExpiredLogged = false;
        }

        /// <summary>开一场比赛：比分归零，进入第 1 回合的 Freeze。</summary>
        public void BeginMatch()
        {
            _scoreT = 0;
            _scoreCT = 0;
            _roundNumber = 0;
            _halfSwapped = false;
            AdvanceToNextRound();
        }

        /// <summary>重开当前回合（比分不变，重新出生 + 重新冻结）。</summary>
        public void PrepareRound(int roundNumber, bool again)
        {
            _roundNumber = roundNumber;
            _m.Bomb.Reset();
            SetPhase(CsRoundPhase.Freeze, FreezeSeconds());
            BeginBuyWindow();
            _m.BeginRoundInternal(_roundNumber);
        }

        private void AdvanceToNextRound()
        {
            _roundNumber++;
            SetPhase(CsRoundPhase.Freeze, FreezeSeconds());
            BeginBuyWindow();
            _m.BeginRoundInternal(_roundNumber);
        }

        private float FreezeSeconds()
        {
            var cfg = _m.Cfg;
            return cfg != null && cfg.FreezeTime > 0f ? cfg.FreezeTime : CsConst.FreezeTime;
        }

        /// <summary>
        /// 开一个新的买枪窗口（每个回合开始时调用一次）。
        ///
        /// <para><b>原版规则（本项目此前的实现缺失的那一半）</b>：买枪期与冻结期是**两个独立计时器** ——
        /// 回合开始后 <c>mp_buytime</c> = <b>15 s</b> 内（出处 <c>server.cfg:42</c>；出厂默认 90 s ·
        /// <c>mp.dll:0x11b9a8</c>）、且身处 <c>func_buyzone</c>（T/CT 各一块）、且活着，才能买；
        /// 15 s 用尽即不能买。冻结期 <c>mp_freezetime</c> = 4 s（<c>server.cfg:51</c>）只是这 15 s 的前 4 秒。
        /// 两者都不对：旧实现没有 15 s 上限）。</para>
        /// </summary>
        private void BeginBuyWindow()
        {
            _buyTimer = CsConst.BuyTime;
            _buyExpiredLogged = false;
            Game.Logger.Info(Tag,
                $"第 {_roundNumber} 回合买枪期开始：窗口 {_buyTimer:F0}s" +
                $"（mp_buytime=0.25min · server.cfg:42），冻结期 {FreezeSeconds():F0}s 与之独立计时" +
                " —— 冻结结束后只要还在窗口内且人在买枪区即可继续买枪");
        }

        /// <summary>买枪期推进（跨 Freeze→Live 两个阶段，见 <see cref="BeginBuyWindow"/>）。</summary>
        private void TickBuyWindow(float dt)
        {
            if (_buyTimer <= 0f) return;
            if (_phase != CsRoundPhase.Freeze && _phase != CsRoundPhase.Live) return;

            _buyTimer -= dt;
            if (_buyTimer > 0f) return;

            _buyTimer = 0f;
            if (_buyExpiredLogged) return;
            _buyExpiredLogged = true;
            Game.Logger.Info(Tag,
                $"第 {_roundNumber} 回合买枪期结束（已过 {CsConst.BuyTime:F0}s · mp_buytime / server.cfg:42）" +
                "—— 此后即便站在买枪区内也不能买枪（必须等下一回合的窗口）");
        }

        private float LiveSeconds()
        {
            var cfg = _m.Cfg;
            return cfg != null && cfg.RoundTime > 0f ? cfg.RoundTime : CsConst.RoundTime;
        }

        private void SetPhase(CsRoundPhase p, float seconds)
        {
            _phase = p;
            _phaseTimer = seconds;
        }

        /// <summary>日志用的阶段描述。</summary>
        public string PhaseTimerText()
        {
            return $"{_phase} {_phaseTimer:F1}s";
        }

        // ==================================================================
        //  每帧推进
        // ==================================================================
        public void TickPhase(float dt, float now)
        {
            // 买枪期与阶段机**并行**推进：它跨 Freeze→Live，不能挂在任一阶段的 case 里
            TickBuyWindow(dt);

            switch (_phase)
            {
                case CsRoundPhase.Freeze:
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        SetPhase(CsRoundPhase.Live, LiveSeconds());
                        Game.Logger.Info(Tag, $"第 {_roundNumber} 回合进入交战阶段（Live，{LiveSeconds():F0}s）");
                    }
                    break;

                case CsRoundPhase.Live:
                    _phaseTimer -= dt;
                    if (_phaseTimer < 0f) _phaseTimer = 0f;
                    // 时间到不在这里判负 —— 已下包时要继续（见 EvaluateWin）。
                    break;

                case CsRoundPhase.RoundEnd:
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        if (IsMatchOver())
                        {
                            Game.Logger.Info(Tag,
                                $"比赛胜负已定（CT {_scoreCT} : {_scoreT} T，第 {_roundNumber} 回合），进入 MatchEnd");
                            SetPhase(CsRoundPhase.MatchEnd, 0f);
                            _m.EndMatchInternal();
                        }
                        else
                        {
                            AdvanceToNextRound();
                        }
                    }
                    break;

                case CsRoundPhase.None:
                case CsRoundPhase.MatchEnd:
                    break;

                default:
                    _m.RateWarn("round.phase.unknown", $"未知回合阶段 {_phase}，阶段机不做推进");
                    break;
            }
        }

        /// <summary>全歼 / 超时判定。炸弹已下包时只认「CT 全灭」与「爆炸 / 拆包」。</summary>
        public void EvaluateWin(float now)
        {
            if (_phase != CsRoundPhase.Live) return;

            var bombPlanted = _m.Bomb.Planted;
            var aliveT = _m.AliveCount(CsTeam.T);
            var aliveCT = _m.AliveCount(CsTeam.CT);

            if (bombPlanted)
            {
                // 已下包：T 即使全灭也要等爆炸/拆包；CT 全灭则 T 立刻取胜。
                if (aliveCT <= 0)
                {
                    Game.Logger.Info(Tag, "炸弹已安放且 CT 全灭 → T 立刻取胜");
                    EndRound(CsTeam.T, CsRoundEndReason.AllTargetsEliminated);
                }
                return;
            }

            if (aliveT <= 0 && aliveCT <= 0)
            {
                Game.Logger.Info(Tag, "双方全灭且炸弹未安放 → 判定 CT 取胜");
                EndRound(CsTeam.CT, CsRoundEndReason.AllTargetsEliminated);
                return;
            }

            if (aliveT <= 0)
            {
                Game.Logger.Info(Tag, "T 全灭 → CT 取胜");
                EndRound(CsTeam.CT, CsRoundEndReason.AllTargetsEliminated);
                return;
            }

            if (aliveCT <= 0)
            {
                Game.Logger.Info(Tag, "CT 全灭 → T 取胜");
                EndRound(CsTeam.T, CsRoundEndReason.AllTargetsEliminated);
                return;
            }

            if (_phaseTimer <= 0f)
            {
                Game.Logger.Info(Tag, "回合时间到且炸弹未安放 → CT 取胜");
                EndRound(CsTeam.CT, CsRoundEndReason.TimeExpired);
            }
        }

        // ==================================================================
        //  回合结束
        // ==================================================================
        public void EndRound(CsTeam winner, CsRoundEndReason reason)
        {
            if (_phase != CsRoundPhase.Live && _phase != CsRoundPhase.Freeze)
            {
                _m.RateWarn("round.end.invalid", $"EndRound({winner}/{reason}) 在 {_phase} 阶段被调用，已忽略");
                return;
            }

            if (winner == CsTeam.T) _scoreT++;
            else if (winner == CsTeam.CT) _scoreCT++;
            else
            {
                Game.Logger.Warn(Tag, $"EndRound：胜方非法（{winner}），回合未结算");
                return;
            }

            _m.Economy.AwardRoundEnd(winner, reason);

            var info = new CsRoundEndInfo
            {
                RoundNumber = _roundNumber,
                Winner = winner,
                Reason = reason,
                ScoreT = _scoreT,
                ScoreCT = _scoreCT,
            };

            SetPhase(CsRoundPhase.RoundEnd, CsConst.RoundEndTime);
            _m.OnRoundEnded(in info);
        }

        private bool IsMatchOver()
        {
            var target = WinTarget;
            if (_scoreT >= target || _scoreCT >= target) return true;
            if (_roundNumber >= CsConst.MaxRounds) return true;
            return false;
        }

        /// <summary>半场交换：比分跟着"人"走 → 两边的分数互换。</summary>
        public void SwapScoresAndStreaks()
        {
            var t = _scoreT;
            _scoreT = _scoreCT;
            _scoreCT = t;
        }
    }
}
