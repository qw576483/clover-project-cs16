using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 单只机器人的无线电发言策略：按**原版 bot chatter 的触发条件**挑一条
    /// <see cref="CsRadio"/> 消息，交给 <c>CsMatch.PublishMessage</c> 打到 HUD 消息栏。
    ///
    /// <para><b>节流口径全部来自原版</b>（逐条出处见 <see cref="CsRadio"/> 的常量注释）：
    /// 同队任意两条 ≥ <see cref="CsRadio.TeamMinInterval"/>（原版 <c>m_radioSilenceInterval</c> 是**按队**的，
    /// <c>cs_bot_chatter.cpp:1606</c>）；同一条消息 ≥ <see cref="SameLineSeconds"/>（原版 10.0f）。</para>
    ///
    /// <para><b>语音</b>：原版无线电语音 wav 已接入（<c>Assets/Resources/Sound/radio/</c>，8 条，
    /// 资源路径见 <see cref="CsRadio.VoiceOf"/>，出处 = 原版 <c>sound/sentences.txt</c> 的 <c>MRAD_*</c> 段）。
    /// 资源缺失时回退 <see cref="PlayBeep"/>（代码合成的 1kHz 短提示音，只作"有人发了无线电"的
    /// 听觉提示，不冒充原版台词）。两者都由本机 <c>AudioSource</c> 播放、不进网络同步。</para>
    /// </summary>
    internal sealed class BotRadio
    {
        private const string Tag = BotModule.Tag;

        /// <summary>
        /// 同一条消息的最小重复间隔（秒）。出处：原版 <c>cs_bot_chatter.cpp:1798</c>（<c>NeedBackup</c>）、
        /// <c>:1830</c>（<c>PinnedDown</c>）、<c>:1915</c>（<c>GoingToPlantTheBomb</c>）——
        /// 三处都是 <c>const float duration = 10.0f</c>。
        ///
        /// <para>本类按原版 10s 执行；<see cref="CsRadio.SameLineInterval"/> 里的 30s 是 Core 层的取值，
        /// 与这里不同源，机器人发言不使用它。</para>
        /// </summary>
        private const float SameLineSeconds = 10f;

        /// <summary>
        /// 无线电（原版 wav 与回退 beep 共用）的播放音量。
        ///
        /// <para>这个源不在引擎 <c>SoundGroup.SFX</c> 池里（<c>Sound.ApplyGroupVolume</c> 只改池内音源），
        /// 按 master×sfx 算是 0.24，1.2~1.9s 的人声会被枪声/脚步压住；固定 0.6 才听得清。</para>
        /// </summary>
        private const float RadioVolume = 0.6f;

        /// <summary>同队无线电静默（按队一份）—— 原版 <c>m_radioSilenceInterval[team-1]</c>
        /// （<c>cs_bot_chatter.cpp:1606</c>、<c>:1616</c>）。</summary>
        private static readonly float[] TeamLastAt = new float[3];

        /// <summary>按"消息 × 队"的最近发送时刻 —— 原版 <c>m_radioMsgTimestamp[24][2]</c>
        /// （<c>cs_bot_manager.h:243</c>、<c>cs_bot_manager.cpp:1893-1900</c>）。</summary>
        private static readonly float[,] LineLastAt = new float[CsRadio.Count + 1, 3];

        // ---- 发言音频：优先播原版 wav，缺文件回退代码合成的短提示音 ----
        /// <summary>合成好的提示音 clip（静态缓存，全进程只合成一次）。</summary>
        private static AudioClip _beepClip;

        /// <summary>常驻的本地 <c>AudioSource</c>（挂在隐藏的常驻对象上，不进网络同步）。</summary>
        private static AudioSource _beepSource;

        /// <summary>原版 wav 的加载结果缓存（静态，全进程只取一次；null 也缓存，表示该条没有可播载体）。
        /// 资源路径的真源 = <see cref="CsRadio.VoiceOf"/>（消息号 → <c>Resources/</c> 相对路径），
        /// 本类不再另存一份映射。</summary>
        private static readonly Dictionary<int, AudioClip> RadioClipCache = new Dictionary<int, AudioClip>();

        /// <summary>已向资源门面发起、尚未回包的语音请求（同一条消息只请求一次）。</summary>
        private static readonly HashSet<int> RadioClipPending = new HashSet<int>();

        private readonly long _id;
        private readonly string _name;

        private string _pending;

        // ---- 本回合的闩（全部对应原版 chatter 的成员）----
        /// <summary><c>m_reportedEnemies</c>：附近敌人归零前只报一次（<c>cs_bot_chatter.cpp:1396-1419</c>）。</summary>
        private bool _reportedEnemies;

        /// <summary>本回合是否已经报过"我到位了 / 快撤"（原版：到达分工点后报一次）。</summary>
        private bool _reportedInPosition;

        /// <summary>本回合是否已经报过"Sector clear"（10s 没见敌就报；每回合每 bot 至多一次，
        /// 复用 <see cref="_reportedEnemies"/> 同款闩模式，回合开始在 <see cref="OnRoundStart"/> 复位）。</summary>
        private bool _reportedClear;

        /// <summary>上一 tick 的可见敌人 / 可见队友计数（用于"归零"与"被压制"判据）。</summary>
        private int _visibleEnemies;
        private int _visibleFriends;

        /// <summary>刚发生了击杀、还没被消费（由 <see cref="NotifyKill"/> 置位）。</summary>
        private bool _killPending;

        /// <summary>最近一次看见敌人的时刻（<c>0</c> = 本回合还没看见过）。</summary>
        private float _lastEnemySeenAt;

        private int _round = -1;

        /// <summary>
        /// 共享节流表（<see cref="TeamLastAt"/> / <see cref="LineLastAt"/>）上一次清零时的回合号与时钟。
        ///
        /// <para><b>为什么要有这道闸</b>：那两张表是 <c>static</c> 的全队共享状态，而
        /// <see cref="OnRoundStart"/> 是**每只 bot 各自**在新回合第一次 Tick 时调的。
        /// 不加闸时同一帧内 bot 依次思考：bot1 清零 → 过队闸占位发消息 → bot2 又清零 → 又过闸……
        /// 队静默（<see cref="CsRadio.TeamMinInterval"/> 12s）被反复清空 ⇒ 开局一帧里全队
        /// 每只各发一条（用户报"开局时机器人的无线电发的很多、很频繁"的根因）。
        /// 清零动作仍按原版 <c>ResetRadioMessageTimestamps</c> 每回合一次，只是**只准第一只
        /// 观察到新回合的 bot 执行</c>。</para>
        ///
        /// <para>时钟回拨分支：新一局开赛时模拟时钟归零，回合号可能与静态记录撞上
        /// （同为 1）——此时 <c>now &lt; _tableResetClock</c> 也判定需要清零。</para>
        /// </summary>
        private static int _tableResetRound = int.MinValue;
        private static float _tableResetClock;

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
                OnRoundStart(team, now);   // now 与 Gate 同源（Think 传入的模拟时钟）
            }

            // 原版：附近敌人归零 ⇒ "已报过敌人"这个闩复位（cs_bot_chatter.cpp:1396-1400）
            if (_visibleEnemies > 0) _lastEnemySeenAt = now;
            if (_visibleEnemies == 0) _reportedEnemies = false;

            var enemiesRemaining = CountAliveEnemies(match, self.Team);
            var friendsRemaining = CountAliveFriends(match, self.Team) - 1;   // 不含自己

            // ---- 触发条件（按优先级；每条都对应原版 chatter 的一个分支）----

            // ① 刚击杀 → "Enemy down."（原版 ReportingIn 的 EnemyDown 分支，cs_bot_chatter.cpp:1775-1780）
            //    _killPending 只在本条真的发出去时清掉：节流挡住时留着，下一个窗口再报。
            if (_killPending && Gate(now, team, 21))
            {
                _killPending = false;
                return Emit(now, team, 21, "击杀");
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
            //    节流挡住时**不置闩**（置了会整段"看得见敌人"期间都报不了），等下一个 12s 窗口。
            if (_visibleEnemies > 0 && !_reportedEnemies && Gate(now, team, 14))
            {
                _reportedEnemies = true;
                return Emit(now, team, 14, $"发现 {_visibleEnemies} 个敌人");
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
            //    每回合每 bot 至多报一次
            //    （_reportedClear，回合开始在 OnRoundStart 复位）—— 否则同一只 bot 一回合里
            //    只要再隔 10s 没见敌就会复读这条。
            if (!engaging && enemiesRemaining > 0 && _lastEnemySeenAt > 0f && !_reportedClear &&
                now - _lastEnemySeenAt >= CsRadio.EnemyRecentSeconds)
            {
                _reportedClear = true;
                if (Gate(now, team, 16))
                    return Emit(now, team, 16, $"已 {now - _lastEnemySeenAt:F1}s 没见过敌人（每回合最多报一次）");
                return null;
            }

            // ⑧ 无线电静默超过 30s、场上还有敌人、且"说了有人听" → "Reporting in."
            //    出处：cs_bot_chatter.cpp:1457-1464（longTime = 30.0f）+ ShouldSpeak（:1586-1597）
            if (friendsRemaining > 0 && _visibleFriends < friendsRemaining && enemiesRemaining > 0 &&
                now - TeamLastAt[team] >= CsRadio.ReportInSilence &&
                Gate(now, team, 18))
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
        /// （<see cref="SameLineSeconds"/>）。返回 true 就立刻记时刻（先占位再发，避免同帧重复）。
        /// </summary>
        private static bool Gate(float now, int team, int id)
        {
            if (now - TeamLastAt[team] < CsRadio.TeamMinInterval) return false;
            if (now - LineLastAt[id, team] < SameLineSeconds) return false;

            TeamLastAt[team] = now;
            LineLastAt[id, team] = now;
            return true;
        }

        /// <summary>新回合：清本 bot 的闩（每只 bot 各自一份，永远都清）；共享的按队/按消息时刻表
        /// **每回合只清一次**（由静态闸 <see cref="_tableResetRound"/> 挑第一只观察到新回合的 bot 执行，
        /// 否则同帧依次思考的每只 bot 都清一遍、队静默被清空 ⇒ 开局全队各发一条）。
        /// 清零动作对应原版 <c>ResetRadioMessageTimestamps</c>（<c>cs_bot_manager.cpp:1903-1906</c>）。</summary>
        private void OnRoundStart(int team, float now)
        {
            _reportedEnemies = false;
            _reportedInPosition = false;
            _reportedClear = false;
            _killPending = false;
            _lastEnemySeenAt = 0f;

            if (_tableResetRound != _round || now < _tableResetClock)
            {
                _tableResetRound = _round;
                _tableResetClock = now;

                for (var t = 0; t < 3; t++)
                {
                    TeamLastAt[t] = 0f;
                    for (var id = 1; id <= CsRadio.Count; id++) LineLastAt[id, t] = 0f;
                }

                Game.Logger?.Info(Tag,
                    $"{_name} 无线电：第 {_round} 回合开始，本 bot 闩复位 + **全队**节流表清零（本轮首清，队 {team}）");
                return;
            }

            Game.Logger?.Info(Tag, $"{_name} 无线电：第 {_round} 回合开始，本 bot 闩复位（队 {team}；节流表已由本轮首只 bot 清过）");
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

            PlayRadio(id);

            return _pending;
        }

        /// <summary>
        /// 发一条无线电时播一次原版语音。
        ///
        /// <para>按 <see cref="CsRadio.VoiceOf"/> 给出的资源路径向引擎资源门面要一次（结果进
        /// <see cref="RadioClipCache"/> 缓存，加载失败的 null 也缓存，避免每次发消息都重复请求）。
        /// 回包前 / 该条没有载体 / 文件不在盘 → 回退 <see cref="PlayBeep"/> 合成提示音；
        /// 回包后同一条消息再发就走常驻 2D <c>AudioSource</c>
        /// （spatialBlend=0，耳机里的声音、不按世界位置衰减）<c>PlayOneShot</c> 播放。</para>
        ///
        /// <para><b>只在本机播，不进网络同步</b>：直接操作本机 <c>AudioSource</c>。</para>
        /// </summary>
        private static void PlayRadio(int id)
        {
            var path = CsRadio.VoiceOf(id);
            if (path == null)
            {
                PlayBeep();   // 该条消息没有语音载体
                return;
            }

            if (!RadioClipCache.TryGetValue(id, out var clip))
            {
                RequestRadioClip(id, path);
                PlayBeep();   // 回包前先用合成提示音
                return;
            }

            if (clip == null)
            {
                // 缺失已在 RequestRadioClip 的回包里报过一次（LogThrottle 去重）。
                PlayBeep();
                return;
            }

            EnsureSource();
            if (_beepSource == null)
            {
                Game.Logger?.Error(Tag, $"无线电语音无法播放：消息 {id} 的常驻 AudioSource 建不出来（clip={clip.name}）");
                return;
            }

            ApplyVolume(_beepSource);
            _beepSource.PlayOneShot(clip);

            Game.Logger?.Info(Tag,
                $"[RADIO-AUDIO] 消息 {id} 播放原版语音 clip={clip.name} path=Resources/{path}.wav " +
                $"长度={clip.length:F2}s 音量={_beepSource.volume:F2} isPlaying={_beepSource.isPlaying} " +
                $"源={_beepSource.gameObject.name}(2D spatialBlend={_beepSource.spatialBlend:F1}) mute={_beepSource.mute}");
        }

        /// <summary>向引擎资源门面要一次该条消息的语音（同一条消息只请求一次，结果落 <see cref="RadioClipCache"/>）。</summary>
        private static void RequestRadioClip(int id, string path)
        {
            if (!RadioClipPending.Add(id)) return;   // 已在途

            var res = Game.Res;
            if (res == null)
            {
                RadioClipPending.Remove(id);
                LogThrottle.WarnOnce(Tag, "radio.nores",
                    "Game.Res 为 null（CloverRes.Init 未执行？），无线电语音不可用（回退合成提示音）");
                return;
            }

            res.LoadAsset<AudioClip>(path, clip => OnRadioClipLoaded(id, path, clip));
        }

        private static void OnRadioClipLoaded(int id, string path, AudioClip clip)
        {
            RadioClipPending.Remove(id);
            RadioClipCache[id] = clip;   // null 也缓存：该条没有可播的语音载体

            if (clip != null) return;

            LogThrottle.WarnOnce(Tag, "radio.missing:" + path,
                $"无线电语音缺失：Resources/{path}.wav（消息 {id}），回退合成提示音");
        }

        /// <summary>
        /// 播放代码合成的短提示音（原版 wav 缺失时的回退）。
        ///
        /// <para><b>合成参数</b>：1kHz 正弦 <c>AudioClip.Create</c> 0.08s / 单声道 / 44100Hz，
        /// 只当"有人发了无线电"的听觉提示。</para>
        ///
        /// <para><b>只在本机播，不进网络同步</b>：直接操作本机 <c>AudioSource</c>，与
        /// <c>Module/Audio</c> 的 <c>SfxService</c> 无关（那边走的是 <c>Game.Sound.PlaySFX</c>
        /// 的资源路径，而本 clip 是代码合成、没有资源路径可喂）。</para>
        /// </summary>
        private static void PlayBeep()
        {
            if (_beepClip == null)
            {
                const int sampleRate = 44100;
                const float seconds = 0.08f;
                var count = Mathf.CeilToInt(sampleRate * seconds);

                var samples = new float[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = Mathf.Sin(2f * Mathf.PI * 1000f * i / sampleRate) * 0.3f;
                }

                _beepClip = AudioClip.Create("bot_radio", count, 1, sampleRate, false);
                _beepClip.SetData(samples, 0);

                Game.Logger?.Info(Tag, "bot 无线电提示音已合成（1kHz 正弦 0.08s，本机 AudioSource 播放，不进网络同步）");
            }

            EnsureSource();

            if (_beepSource != null && _beepClip != null)
            {
                ApplyVolume(_beepSource);
                _beepSource.PlayOneShot(_beepClip);

                Game.Logger?.Info(Tag,
                    $"[RADIO-AUDIO] 合成提示音（该条没有原版 wav） 音量={_beepSource.volume:F2} " +
                    $"isPlaying={_beepSource.isPlaying} 源={_beepSource.gameObject.name}" +
                    $"(2D spatialBlend={_beepSource.spatialBlend:F1}) mute={_beepSource.mute}");
            }
        }

        /// <summary>
        /// 把玩家设置里的主音量 × 音效音量写到这条本机 <c>AudioSource</c> 上。
        /// 这条源不经过引擎 <c>Game.Sound</c>（合成 beep 没有资源路径），分组音量收不到它 ⇒ 必须自己同步；
        /// 否则玩家把音量调到 0 仍能听见无线电。
        /// </summary>
        private static void ApplyVolume(AudioSource src)
        {
            if (src == null) return;
            src.volume = RadioVolume;
        }

        /// <summary>懒建常驻的本机 <c>AudioSource</c>（原版 wav 与合成 beep 共用；只建一次）。</summary>
        private static void EnsureSource()
        {
            if (_beepSource != null) return;

            var go = new GameObject("BotRadioBeep") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            _beepSource = go.AddComponent<AudioSource>();
            _beepSource.playOnAwake = false;
            _beepSource.spatialBlend = 0f;   // 2D：无线电是耳机里的声音，不按世界位置衰减
        }
    }
}
