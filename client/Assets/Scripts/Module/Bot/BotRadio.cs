using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 单只机器人的无线电发言策略：按**原版 bot chatter 的触发条件**挑一条
    /// <see cref="CsRadio"/> 消息，交给 <c>CsMatch.PublishMessage</c> 打到 HUD 消息栏。
    ///
    /// <para><b>节流口径全部来自原版</b>（逐条出处见 <see cref="CsRadio"/> 的常量注释）：
    /// 同队任意两条 ≥ <see cref="CsRadio.TeamMinInterval"/>（原版 <c>m_radioSilenceInterval</c> 是**按队**的，
    /// <c>cs_bot_chatter.cpp:1606</c>）；同一条消息 ≥ <see cref="CsRadio.SameLineInterval"/>（10.0f）。</para>
    ///
    /// <para><b>为什么没有语音</b>：原版 bot 语音 wav 与 <c>bot_phrases.txt</c> 本机不在盘
    /// （见 <see cref="CsRadioLine.NoVoice"/>）⇒ 只发屏幕文本，不自造音频；
    /// 首次发言时打一条 Warn 说明这件事，避免"看起来发过就以为有声音"。</para>
    /// </summary>
    internal sealed class BotRadio
    {
        private const string Tag = BotModule.Tag;

        /// <summary>同队无线电静默（按队一份）—— 原版 <c>m_radioSilenceInterval[team-1]</c>
        /// （<c>cs_bot_chatter.cpp:1606</c>、<c>:1616</c>）。</summary>
        private static readonly float[] TeamLastAt = new float[3];

        /// <summary>按"消息 × 队"的最近发送时刻 —— 原版 <c>m_radioMsgTimestamp[24][2]</c>
        /// （<c>cs_bot_manager.h:243</c>、<c>cs_bot_manager.cpp:1893-1900</c>）。</summary>
        private static readonly float[,] LineLastAt = new float[CsRadio.Count + 1, 3];

        /// <summary>语音载体缺失这件事只提醒一次（否则 8 只 bot 每回合刷屏）。</summary>
        private static bool _warnedNoVoice;

        private readonly long _id;
        private readonly string _name;

        private string _pending;

        // ---- 本回合的闩（全部对应原版 chatter 的成员）----
        /// <summary><c>m_reportedEnemies</c>：附近敌人归零前只报一次（<c>cs_bot_chatter.cpp:1396-1419</c>）。</summary>
        private bool _reportedEnemies;

        /// <summary>本回合是否已经报过"我到位了 / 快撤"（原版：到达分工点后报一次）。</summary>
        private bool _reportedInPosition;

        /// <summary>上一 tick 的可见敌人 / 可见队友计数（用于"归零"与"被压制"判据）。</summary>
        private int _visibleEnemies;
        private int _visibleFriends;

        /// <summary>刚发生了击杀、还没被消费（由 <see cref="NotifyKill"/> 置位）。</summary>
        private bool _killPending;

        /// <summary>最近一次看见敌人的时刻（<c>0</c> = 本回合还没看见过）。</summary>
        private float _lastEnemySeenAt;

        private int _round = -1;

        public BotRadio(CsActor self)
        {
            _id = self != null ? self.Id : 0;
            _name = self != null ? self.Name : "bot";
        }

        /// <summary>本 bot 的 actor id（只读）。</summary>
        public long ActorId => _id;

        /// <summary>本 bot 刚打死了人（由 <c>BotModule.OnKill</c> 调用）。</summary>
        public void NotifyKill()
        {
            _killPending = true;
        }

        /// <summary>本回合尚未发出去的无线电行（已被消费则返回 null）。</summary>
        public string Consume()
        {
            var line = _pending;
            _pending = null;
            return line;
        }

        /// <summary>
        /// 跑一次无线电决策。返回**本 tick 要发出的一行**（未发则为 null）。
        /// </summary>
        /// <param name="engaging">本 bot 当前是否处于交战状态。</param>
        /// <param name="atGoal">是否已站到本轮目标点（守位 / 分工点）。</param>
        /// <param name="headingToPlant">T 持包且正在冲包点下包。</param>
        public string Tick(ICsMatch match, CsActor self, float now, bool engaging, bool atGoal, bool headingToPlant)
        {
            if (match == null || self == null || !self.IsAlive) return null;

            var team = TeamIndex(self.Team);
            if (team < 0) return null;

            // 只有回合进行中才说话（原版 chatter 在 RoundOver 时不发：
            // cs_bot_chatter.cpp:1851 / :1912 / :1930 / :1942 的 IsRoundOver() 早退）。
            if (match.Phase != CsRoundPhase.Live) return null;

            if (_round != match.RoundNumber)
            {
                _round = match.RoundNumber;
                OnRoundStart(team);
            }

            // 原版：附近敌人归零 ⇒ "已报过敌人"这个闩复位（cs_bot_chatter.cpp:1396-1400）
            if (_visibleEnemies > 0) _lastEnemySeenAt = now;
            if (_visibleEnemies == 0) _reportedEnemies = false;

            var enemiesRemaining = CountAliveEnemies(match, self.Team);
            var friendsRemaining = CountAliveFriends(match, self.Team) - 1;   // 不含自己

            // ---- 触发条件（按优先级；每条都对应原版 chatter 的一个分支）----

            // ① 刚击杀 → "Enemy down."（原版 ReportingIn 的 EnemyDown 分支，cs_bot_chatter.cpp:1775-1780）
            if (_killPending)
            {
                _killPending = false;
                if (Gate(now, team, 21)) return Emit(now, team, 21, "击杀");
            }

            // ② 被压制（附近队友 < 附近敌人-1）→ "Need backup."
            //    出处：cs_bot_chatter.cpp:1411（ReportEnemies 里改发）+ cs_bot.cpp:659-662（IsOutnumbered）
            if (_visibleEnemies > 0 && _visibleFriends < _visibleEnemies - 1 && Gate(now, team, 15))
            {
                _reportedEnemies = true;
                return Emit(now, team, 15, $"被压制（可见队友 {_visibleFriends} < 可见敌人 {_visibleEnemies}-1）");
            }

            // ③ 看见敌人且本回合还没报过 → "Enemy spotted."
            //    出处：cs_bot_chatter.cpp:1408-1419
            if (_visibleEnemies > 0 && !_reportedEnemies)
            {
                _reportedEnemies = true;
                if (Gate(now, team, 14)) return Emit(now, team, 14, $"发现 {_visibleEnemies} 个敌人");
                return null;
            }

            // ④ 包已下、且自己是离 C4 最近的那个 T → "Get out of there, it's gonna blow!"
            //    出处：cs_bot_radio.cpp:165-174（人类指挥这条时 bot 执行 EscapeFromBomb）。
            //    "由离 C4 最近的 T 喊"是本项目的选举口径（本项目新增；原版是每只 bot 自己判断撤不撤）。
            if (match.BombPlanted && self.Team == CsTeam.T && !_reportedInPosition && IsNearestToBomb(match, self) &&
                Gate(now, team, 19))
            {
                _reportedInPosition = true;
                return Emit(now, team, 19, $"包已下且本 bot 离 C4 最近（剩 {match.BombTimeLeft:F1}s）");
            }

            // ⑤ T 持包冲包点 → "Go go go!"（跟上去一起打 B）
            //    出处：cs_bot_chatter.cpp:1910-1926（GoingToPlantTheBomb：REPORT_CRITICAL_EVENT + BotFollowMeme，10.0f 一次）
            if (headingToPlant && Gate(now, team, 7))
            {
                return Emit(now, team, 7, "持包冲包点");
            }

            // ⑥ 站到目标点 → "I'm in position."（对应原版按 GetTask() 上报那一类，cs_bot_chatter.cpp:1709-1753）
            if (atGoal && !_reportedInPosition && Gate(now, team, 17))
            {
                _reportedInPosition = true;
                return Emit(now, team, 17, "已到达目标点");
            }

            // ⑦ 最近 10s 没见过敌人、但场上还有敌人 → "Sector clear."
            //    出处：cs_bot_chatter.cpp:1775-1790（recentTime = 10.0f 之后的 "Clear" 分支）
            if (!engaging && enemiesRemaining > 0 && _lastEnemySeenAt > 0f &&
                now - _lastEnemySeenAt >= CsRadio.EnemyRecentSeconds && Gate(now, team, 16))
            {
                return Emit(now, team, 16, $"已 {now - _lastEnemySeenAt:F1}s 没见过敌人");
            }

            // ⑧ 无线电静默超过 30s、场上还有敌人、且"说了有人听" → "Reporting in."
            //    出处：cs_bot_chatter.cpp:1457-1464（longTime = 30.0f）+ ShouldSpeak（:1586-1597）
            if (friendsRemaining > 0 && _visibleFriends < friendsRemaining && enemiesRemaining > 0 &&
                now - TeamLastAt[team] >= CsRadio.ReportInSilence)
            {
                return Emit(now, team, 18, "无线电静默超 30s");
            }

            return null;
        }

        /// <summary>本回合是否已经喊过"我到位"（<c>BotRadio</c> 内部状态，供探针只读）。</summary>
        public bool ReportedInPosition => _reportedInPosition;

        // ==================================================================
        //  闸门 / 计数
        // ==================================================================

        /// <summary>
        /// 这条消息此刻能不能发：同队静默（<see cref="CsRadio.TeamMinInterval"/>）+ 同条消息间隔
        /// （<see cref="CsRadio.SameLineInterval"/>）。返回 true 就立刻记时刻（先占位再发，避免同帧重复）。
        /// </summary>
        private static bool Gate(float now, int team, int id)
        {
            if (now - TeamLastAt[team] < CsRadio.TeamMinInterval) return false;
            if (now - LineLastAt[id, team] < CsRadio.SameLineInterval) return false;

            TeamLastAt[team] = now;
            LineLastAt[id, team] = now;
            return true;
        }

        /// <summary>新回合：清本 bot 的闩，并把按队/按消息的时刻表清零（原版 <c>ResetRadioMessageTimestamps</c>，
        /// <c>cs_bot_manager.cpp:1903-1906</c>）。</summary>
        private void OnRoundStart(int team)
        {
            _reportedEnemies = false;
            _reportedInPosition = false;
            _killPending = false;
            _lastEnemySeenAt = 0f;

            for (var t = 0; t < 3; t++)
            {
                TeamLastAt[t] = 0f;
                for (var id = 1; id <= CsRadio.Count; id++) LineLastAt[id, t] = 0f;
            }

            Game.Logger?.Info(Tag, $"{_name} 无线电：第 {_round} 回合开始，闩与节流表清零（队 {team}）");
        }

        /// <summary>记一次可见敌/友计数（由 <c>CsBotBrain.UpdatePerception</c> 在同一趟判定里累加）。</summary>
        public void SetPerception(int visibleEnemies, int visibleFriends)
        {
            _visibleEnemies = visibleEnemies;
            _visibleFriends = visibleFriends;
        }

        /// <summary>本 bot 是否"离 C4 最近的那个 T"。</summary>
        private static bool IsNearestToBomb(ICsMatch match, CsActor self)
        {
            var bomb = match.BombPosition;
            var myDist = (self.Position - bomb).sqrMagnitude;
            if (myDist > CsBotConst.SiteRadius * CsBotConst.SiteRadius) return false;

            var actors = match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Id == self.Id) continue;
                if (a.Team != CsTeam.T) continue;
                if ((a.Position - bomb).sqrMagnitude < myDist) return false;
            }
            return true;
        }

        private static int CountAliveEnemies(ICsMatch match, CsTeam selfTeam)
        {
            var actors = match.Actors;
            var n = 0;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive) continue;
                if (IsEnemyTeam(selfTeam, a.Team)) n++;
            }
            return n;
        }

        private static int CountAliveFriends(ICsMatch match, CsTeam selfTeam)
        {
            var actors = match.Actors;
            var n = 0;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive) continue;
                if (a.Team == selfTeam) n++;
            }
            return n;
        }

        private static bool IsEnemyTeam(CsTeam selfTeam, CsTeam other)
        {
            if (selfTeam == CsTeam.T) return other == CsTeam.CT;
            if (selfTeam == CsTeam.CT) return other == CsTeam.T;
            return false;
        }

        private static int TeamIndex(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.T: return 0;
                case CsTeam.CT: return 1;
                default: return -1;
            }
        }

        /// <summary>记下要发的行 + 打一条 Info（证据行：含消息号 / 组 / 槽 / 原版标识 / 原文）。</summary>
        private string Emit(float now, int team, int id, string why)
        {
            var line = CsRadio.ById(id);
            _pending = CsRadio.Format(_name, line);

            Game.Logger?.Info(Tag,
                $"[RADIO] id={line.Id} 组={line.SetLabel}(radio{(int)line.Set + 1}) 槽={line.Slot} " +
                $"{line.EventName} 原文=\"{line.Text}\" ⇒ HUD 行=\"{_pending}\"（触发：{why}）");

            if (!_warnedNoVoice)
            {
                _warnedNoVoice = true;
                Game.Logger?.Warn(Tag,
                    "无线电只发了屏幕文本、没有语音：原版 bot 语音 wav 与 bot_phrases.txt 本机不在盘" +
                    "（CsRadioLine.Voice 全部 = 原版无）⇒ 不自造音频；拿到原版载体后在此处接播放");
            }

            return _pending;
        }
    }
}
