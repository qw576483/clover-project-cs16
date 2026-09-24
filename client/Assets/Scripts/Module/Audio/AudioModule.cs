using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Audio
{
    /// <summary>
    /// **音效模块**（由 <c>Bootstrap</c> 挂在常驻对象上）：脚下/落地、换弹、命中反馈、死亡、
    /// 回合开始与结束、炸弹蜂鸣与爆炸。
    ///
    /// <para><b>与 <c>Module/Combat</c> 的分工，一句话</b>：
    /// **枪声不归我**。射击队列（含机器人枪声）由 <c>CombatModule</c> 独占消费并播放
    /// <c>sfx/&lt;武器&gt;_fire</c>；本模块只补它没做的那几类，且命中标记（<c>sfx/hitmarker</c>）
    /// 也是它播 —— 所以这里绝不重复放。</para>
    ///
    /// <para><b>只用权威状态，不抢模拟</b>：脚步按**原版冷却计时器**触发（<c>CsActor.Velocity</c> /
    /// <c>IsWalking</c> / <c>OnGround</c> 都是只读），换弹按 <c>ReloadEndTime</c> 前推，
    /// 死亡按 <c>OnKill</c>，回合按事件总线与 <c>OnRoundEnd</c>，炸弹按 <c>BombPlanted</c> /
    /// <c>BombTimeLeft</c>。一个字段都不写回。</para>
    ///
    /// <para><b>执行顺序</b>：<c>Update</c> 即可（脚步/蜂鸣是"事后表现"，
    /// 与视图的相机/射线链无耦合；放在默认 order 上让它在模拟 Tick 之后读到本帧状态）。</para>
    /// </summary>
    public sealed class AudioModule : MonoBehaviour
    {
        private const string Tag = "Audio";

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        /// <summary>每个 actor 的脚步状态。</summary>
        private struct FootState
        {
            public bool Have;
            /// <summary>脚步冷却（毫秒）= 原版 <c>pm_shared.c</c> 的 <c>flTimeStepSound</c>：正值表示
            /// "还剩多少毫秒才允许下一步"，每帧按 dt 递减。递减点出处
            /// <c>原版资源/hlsdk/pm_shared/pm_shared.c:2404</c>。</summary>
            public float StepCooldownMs;
            public bool WasOnGround;
            public float LastFallSpeed; // 上一帧的竖直速度（用于落地判定）
            public bool Alive;
        }

        private readonly Dictionary<long, FootState> _feet = new Dictionary<long, FootState>(32);
        private readonly List<long> _footStale = new List<long>(8);

        private MatchModule _matchModule;
        private ICsMatch _match;
        private SfxService _sfx;

        private bool _subscribed;
        private bool _ready;
        private bool _wasRunning;

        // ---- 炸弹蜂鸣 ----
        private float _beepTimer;

        private bool _beepFast;
        private bool _wasPlanted;

        // ---- 换弹（只看本地玩家与机器人各自的那一次）----
        private readonly Dictionary<long, float> _reloadWatch = new Dictionary<long, float>(32);

        // ---- 音量轮询 ----
        private float _volumeTimer;
        private float _appliedSfx = -1f;

        // ==================================================================
        //  装配
        // ==================================================================
        private void Start()
        {
            _matchModule = GetComponent<MatchModule>();
            if (_matchModule == null)
            {
                Game.Logger.Error(Tag,
                    "AudioModule 拿不到 MatchModule（应与本组件挂在同一个 GameObject 上、由 Bootstrap 装配）。" +
                    "音效已被禁用。");
                enabled = false;
                return;
            }

            _match = _matchModule.Match;
            if (_match == null)
            {
                Game.Logger.Error(Tag, "MatchModule.Match 为 null（门面未就绪），音效模块已被禁用。");
                enabled = false;
                return;
            }

            _sfx = new SfxService(Tag);
            ApplySfxGates();
            Subscribe();
            ApplyVolume(force: true);

            // 开局前把"一定会用到"的音效先探一遍，避免第一条声音被吃在加载里。
            _sfx.Prewarm(CsAudioTuning.RoundStart, CsAudioTuning.Step[0], CsAudioTuning.Land,
                CsAudioTuning.HitFlesh, CsAudioTuning.Death[0], CsAudioTuning.BombBeep,
                CsAudioTuning.BombPlant, CsAudioTuning.BombExplode, CsAudioTuning.WinT, CsAudioTuning.WinCT,
                CsAudioTuning.BombBeepFast, CsAudioTuning.FlashExplode, CsAudioTuning.HitWall,
                CsAudioTuning.Dryfire, CsAudioTuning.KnifeHit);

            _ready = true;
            _log.Always("音效模块就绪：脚步/落地、换弹、命中反馈、死亡、回合开始结束、炸弹蜂鸣（枪声归 agent-04）");
        }

        private void OnDestroy()
        {
            Unsubscribe();
            _feet.Clear();
            _reloadWatch.Clear();
        }

        // ==================================================================
        //  事件接线
        // ==================================================================
        private void Subscribe()
        {
            if (_subscribed || _match == null) return;

            // 比赛门面的表现事件（契约）
            _match.OnKill += OnKill;
            _match.OnDamaged += OnDamaged;
            _match.OnRoundEnd += OnRoundEnd;
            _match.OnMatchEnd += OnMatchEnd;
            _match.OnBombStateChanged += OnBombStateChanged;

            var bus = Game.Event;
            if (bus != null)
            {
                bus.On<int>(Events.RoundStarted, OnRoundStarted);
                bus.On(Events.MatchStarted, OnMatchStarted);
            }
            else
            {
                _log.Warn("events.null", "Game.Event 为 null（引擎未 Launch？），回合开始音不会响");
            }

            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_match == null) return;

            _match.OnKill -= OnKill;
            _match.OnDamaged -= OnDamaged;
            _match.OnRoundEnd -= OnRoundEnd;
            _match.OnMatchEnd -= OnMatchEnd;
            _match.OnBombStateChanged -= OnBombStateChanged;

            var bus = Game.Event;
            if (bus == null) return;
            bus.Off<int>(Events.RoundStarted, OnRoundStarted);
            bus.Off(Events.MatchStarted, OnMatchStarted);
        }

        // ==================================================================
        //  每帧
        // ==================================================================
        private void Update()
        {
            // 门面可能为空（见 PlayerModule.Update 的同款注释）——不判就是每帧一条 NRE。
            if (!_ready || _match == null) return;

            var running = _match.IsRunning;
            if (running != _wasRunning)
            {
                _wasRunning = running;
                if (!running)
                {
                    _feet.Clear();
                    _reloadWatch.Clear();
                    _beepTimer = 0f;
                    _wasPlanted = false;
                }
                else
                {
                    _log.Always("比赛开始，音效进入工作状态");
                }
            }

            if (!running) return;

            var dt = _match.IsPaused ? 0f : Time.deltaTime;

            TickFootsteps(dt);
            TickReloads();
            TickBombBeep(dt);
            TickRoundStartWatch();
            TickVolume(dt);
        }

        // ==================================================================
        //  脚步 / 落地 / 跳跃
        // ==================================================================
        private void TickFootsteps(float dt)
        {
            var actors = _match.Actors;
            if (actors == null) return;

            var local = _match.LocalPlayer;
            var localId = local != null ? local.Id : 0L;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;

                if (!_feet.TryGetValue(a.Id, out var st))
                {
                    st = new FootState { Have = true, WasOnGround = a.OnGround };
                    _feet[a.Id] = st;
                    continue;
                }

                var pos = a.Position;
                // ---- 落地：上一帧在空中且竖直速度够快，本帧着地 ----
                if (a.IsAlive && !st.WasOnGround && a.OnGround && st.LastFallSpeed <= -CsAudioTuning.LandMinFallSpeed)
                {
                    PlayFor(a, localId, CsAudioTuning.Land, spatialFrom: pos);
                }
                // ---- 起跳 ----
                if (a.IsAlive && st.WasOnGround && !a.OnGround)
                {
                    PlayFor(a, localId, CsAudioTuning.Jump, spatialFrom: pos);
                }

                //   原版是**时间制冷却**：PM_ReduceTimers 每帧先 `flTimeStepSound -= cmd.msec`，冷却归零后
                //   PM_UpdateStepSound 才决定"再装多少毫秒"，装完即静默 ⇒ 同一冷却下跑得越快步幅越大。
                //   照抄原版的五道顺序：
                //     ① 每帧先递减冷却（:2404）；② 冷却未归零 ⇒ 什么都不做（原版函数第一行 return，:511）；
                //     ③ 冻结期（FL_FROZEN，:514）⇒ 直接返回 ⇒ 冷却归零后一直停在 0，解冻第一帧就补一步；
                //     ④ 速度 < StepMinSpeed（150 u/s，:519）⇒ 只把冷却装成 400 ms、不发声（:521）；
                //     ⑤ 否则放音并装 300 ms（混凝土，:626），蹲行再 +100 ms（:632）。
                //   速度取 `Length(pmove->velocity)`（三维模长，:517），**不是**水平分量。
                //   "慢走无声"在原版就是第 ④ 行给的：慢走 5.4 x 0.42 = 2.27 m/s、蹲行 1.84 m/s
                //   都低于 3.81 m/s。原版并没有另写一条"Shift 静音"规则 —— 这里保留 IsWalking 只是
                st.StepCooldownMs -= dt * 1000f;
                if (st.StepCooldownMs < 0f) st.StepCooldownMs = 0f;   // 原版 :2406-2408 同此夹零

                if (st.StepCooldownMs <= 0f && _match.Phase != CsRoundPhase.Freeze)
                {
                    var speed = a.Velocity.magnitude;
                    if (speed < CsAudioTuning.StepMinSpeed)
                    {
                        st.StepCooldownMs = CsAudioTuning.StepCooldownSlowMs;
                    }
                    else if (a.IsAlive && a.OnGround && !a.IsWalking)
                    {
                        st.StepCooldownMs = CsAudioTuning.StepCooldownConcreteMs
                            + (a.IsCrouching ? CsAudioTuning.StepDuckingExtraMs : 0f);
                        PlayFor(a, localId, null, spatialFrom: pos);
                    }
                }

                st.WasOnGround = a.OnGround;
                st.LastFallSpeed = a.Velocity.y;
                st.Alive = a.IsAlive;
                _feet[a.Id] = st;
            }

            // 回收已消失的 actor
            _footStale.Clear();
            foreach (var kv in _feet)
            {
                var found = false;
                for (var i = 0; i < actors.Count; i++)
                {
                    if (actors[i] != null && actors[i].Id == kv.Key) { found = true; break; }
                }
                if (!found) _footStale.Add(kv.Key);
            }
            for (var i = 0; i < _footStale.Count; i++) _feet.Remove(_footStale[i]);
        }

        /// <summary>自己的声音用 2D（在耳边），别人的用 3D 且限距。脚步声随机取变体。</summary>
        private void PlayFor(CsActor a, long localId, string clip, Vector3 spatialFrom)
        {
            var isLocal = a.Id == localId;
            if (clip == null)
            {
                // 脚步：4 个变体随机
                if (isLocal) _sfx.PlayRandom(CsAudioTuning.Step, false, Vector3.zero);
                else PlaySpatialIfNear(CsAudioTuning.Step, spatialFrom);
                return;
            }

            if (isLocal) _sfx.Play(clip);
            else PlaySpatialIfNear(new[] { clip }, spatialFrom);
        }

        private void PlaySpatialIfNear(string[] clips, Vector3 pos)
        {
            var local = _match != null ? _match.LocalPlayer : null;
            if (local != null)
            {
                var d = Vector3.Distance(local.Position, pos);
                if (d > CsAudioTuning.StepHearDistance) return;
            }
            _sfx.PlayRandom(clips, true, pos);
        }

        // ==================================================================
        //  换弹
        // ==================================================================
        private void TickReloads()
        {
            var actors = _match.Actors;
            if (actors == null) return;

            var local = _match.LocalPlayer;
            var localId = local != null ? local.Id : 0L;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;

                _reloadWatch.TryGetValue(a.Id, out var prev);
                var now = a.ReloadEndTime;
                _reloadWatch[a.Id] = now;

                if (now <= prev + 0.0001f) continue;      // 没开始换弹
                if (string.IsNullOrEmpty(a.ActiveWeapon)) continue;

                // 短名前缀真源 = CsAudioTuning.ClipRoot；不在这里另写一份字面量（同款"前缀两处
                var clip = CsAudioTuning.ClipRoot + a.ActiveWeapon + "_reload";
                if (a.Id == localId) _sfx.Play(clip);
                else PlaySpatialIfNear(new[] { clip }, a.Position);
            }
        }

        // ==================================================================
        //  死亡 / 命中
        // ==================================================================
        private void OnKill(CsKillEvent k)
        {
            var victim = _match.Find(k.VictimId);
            var pos = victim != null ? victim.Position : Vector3.zero;
            var local = _match.LocalPlayer;
            var isLocalVictim = local != null && k.VictimId == local.Id;

            if (isLocalVictim) _sfx.PlayRandom(CsAudioTuning.Death, false, Vector3.zero);
            else PlaySpatialIfNear(CsAudioTuning.Death, pos);

            _log.Info("kill",
                $"{k.KillerName} → {k.VictimName}（{k.WeaponId}{(k.Headshot ? "，爆头" : string.Empty)}），死亡音已播放");
        }

        private void OnDamaged(CsActor victim, int damage, bool headshot, bool lethal)
        {
            if (victim == null) return;

            var local = _match.LocalPlayer;
            if (local != null && victim.Id == local.Id)
            {
                // 自己被击中：有甲/头盔用 kevlar 音，否则 flesh（CS 1.6 的 bhit_* 采样）
                var hasArmor = victim.Armor > 0 || victim.HasHelmet;
                _sfx.Play(hasArmor ? CsAudioTuning.HitKevlar : CsAudioTuning.HitFlesh);
            }
        }

        // ==================================================================
        //  回合 / 比赛
        // ==================================================================
        private void OnMatchStarted()
        {
            _log.Always("比赛开始事件到达：音效模块跟随重置");
        }

        private void OnRoundStarted(int roundNumber)
        {
            PlayRoundStart($"事件总线 {Events.RoundStarted}({roundNumber})");
        }

        /// <summary>
        /// **双保险**：回合开始音的触发既订阅了事件总线，也在 <c>Update</c> 里盯
        /// <c>ICsMatch.RoundNumber</c> 的变化。原因：总线的参数签名（<c>On&lt;int&gt;</c>）
        /// —— 那种"声音没了但也不报错"最难查。这里用去重窗口保证两条路都通也只响一次。
        /// </summary>
        private void PlayRoundStart(string source)
        {
            if (Time.time - _lastRoundStartSfx < RoundStartDedupeWindow) return;
            _lastRoundStartSfx = Time.time;
            _sfx.Play(CsAudioTuning.RoundStart);
            _log.Info("round.start", $"回合开始音已播放（触发源：{source}）");
        }

        private float _lastRoundStartSfx = -999f;
        private int _lastRoundNumber = -1;
        private const float RoundStartDedupeWindow = 1.0f;

        /// <summary>盯权威状态：回合号变化（且已是正式回合）就播开始音。</summary>
        private void TickRoundStartWatch()
        {
            var n = _match.RoundNumber;
            if (n == _lastRoundNumber) return;
            var first = _lastRoundNumber < 0;
            _lastRoundNumber = n;
            if (first || n <= 0) return;      // 第一帧只是建立基线，不播
            PlayRoundStart($"RoundNumber → {n}");
        }

        private void OnRoundEnd(CsRoundEndInfo info)
        {
            // CS 1.6 的回合结束是 radio 播报"哪一方获胜"（与玩家阵营无关）
            var win = info.Winner == CsTeam.T ? CsAudioTuning.WinT : CsAudioTuning.WinCT;
            _sfx.Play(win);

            if (info.Reason == CsRoundEndReason.BombExploded)
            {
                _sfx.PlayAt(CsAudioTuning.BombExplode, _match.BombPosition);
            }
            else if (info.Reason == CsRoundEndReason.BombDefused)
            {
                _sfx.Play(CsAudioTuning.BombDefuseRadio);
            }

            _log.Info("round.end",
                $"第 {info.RoundNumber} 回合结束：{info.Winner} 胜（{info.Reason}），播报 {win}");
        }

        private void OnMatchEnd()
        {
            var winner = _match.ScoreT >= _match.ScoreCT ? CsTeam.T : CsTeam.CT;
            _sfx.Play(winner == CsTeam.T ? CsAudioTuning.WinT : CsAudioTuning.WinCT);
            _log.Always($"比赛结束：{winner} 获胜（T {_match.ScoreT} : {_match.ScoreCT} CT），已播获胜播报");
        }

        // ==================================================================
        //  炸弹
        // ==================================================================
        private void OnBombStateChanged()
        {
            var planted = _match != null && _match.BombPlanted;
            if (planted && !_wasPlanted)
            {
                _sfx.PlayAt(CsAudioTuning.BombPlant, _match.BombPosition);
                _sfx.Play(CsAudioTuning.BombPlantRadio);
                _beepTimer = 0f;
                _log.Always($"炸弹已安放（位置 {_match.BombPosition}）：下包音 + 播报已播放，开始蜂鸣");
            }
            _wasPlanted = planted;
        }

        private void TickBombBeep(float dt)
        {
            if (!_match.BombPlanted)
            {
                if (_wasPlanted) _wasPlanted = false;
                return;
            }

            var left = _match.BombTimeLeft;

            // 不再是"同一个音只靠间隔区分"。分界值与 CsConst 的 BombBeepIntervalSlow/Fast 同一处口径
            // （见 CsAudioTuning.BombBeepFastBelow 的注释）。
            var fast = left <= CsAudioTuning.BombBeepFastBelow;
            var interval = fast ? CsConst.BombBeepIntervalFast : CsConst.BombBeepIntervalSlow;
            if (interval <= 0.01f) interval = 1f;

            if (fast != _beepFast)
            {
                // 只在**档位变化**时打一条（蜂鸣本身可到 4 次/秒，逐次打会刷屏）。
                _beepFast = fast;
                _log.Info("bomb.beep.档位",
                    $"C4 蜂鸣档位切换 → {(fast ? "加速档" : "普通档")}（剩余 {left:F1}s，" +
                    $"分界 {CsAudioTuning.BombBeepFastBelow:F0}s），clip = " +
                    (fast ? CsAudioTuning.BombBeepFast : CsAudioTuning.BombBeep));
            }

            _beepTimer += dt;
            if (_beepTimer < interval) return;
            _beepTimer = 0f;

            _sfx.PlayAt(fast ? CsAudioTuning.BombBeepFast : CsAudioTuning.BombBeep, _match.BombPosition);
        }

        // ==================================================================
        //  音量（设置面板没有变更事件 → 轮询 + 只在变化时应用）
        // ==================================================================
        private void TickVolume(float dt)
        {
            _volumeTimer += dt;
            if (_volumeTimer < CsAudioTuning.VolumePollInterval) return;
            _volumeTimer = 0f;
            ApplyVolume(force: false);
        }

        private void ApplyVolume(bool force)
        {
            var setting = Game.Setting;
            if (setting == null)
            {
                if (force) _log.Warn("volume.noset", "Game.Setting 为 null（未 Launch？），音效音量沿用默认值");
                return;
            }

            var master = Mathf.Clamp01(setting.Get(CsSettingsKeys.VolumeMaster, 1f));
            var sfx = Mathf.Clamp01(setting.Get(CsSettingsKeys.VolumeSfx, 1f));
            var want = master * sfx;

            if (!force && Mathf.Abs(want - _appliedSfx) < 0.001f) return;
            _appliedSfx = want;

            var sound = Game.Sound;
            if (sound == null)
            {
                if (force) _log.Warn("volume.nosound", "Game.Sound 为 null，音效音量设置未应用");
                return;
            }

            sound.SetVolume(SoundGroup.SFX, want);
            _log.Always($"音效音量已应用：{want:0.##}（master {master:0.##} × sfx {sfx:0.##}）");
        }

        // ==================================================================
        //  播放闸门（阈值留在本项目，执行口径在引擎 Game.Sound）
        // ==================================================================
        /// <summary>
        /// 把本项目的两个高频防护阈值下发给引擎的播放闸门。
        ///
        /// <para><b>为什么要显式下发</b>：引擎闸门默认 <c>0</c> = 不限（那是"不改动任何既有项目表现"的默认值），
        /// 而本项目一直靠 <c>CsAudioTuning.MaxPlaysPerFrame / MaxConcurrentPerClip</c> 防"一帧打进几十个音效
        /// 把 32 个音源占满"。不下发就等于把这个项目原有的防护丢掉 —— 所以这里是**等价迁移**，
        /// 不是新增玩法：数值一字未改，只是执行者从项目侧的 <c>SfxService</c> 换成了引擎。</para>
        /// </summary>
        private void ApplySfxGates()
        {
            var sound = Game.Sound;
            if (sound == null)
            {
                // 非预期分支（表现域未挂载）：留痕一次即可，音效本来也不会响（SfxService 那条会再报一次）。
                _log.Warn("sfx.nogate", "Game.Sound 为 null（表现域未挂载？），音效闸门未下发（引擎默认 0 = 不限）");
                return;
            }

            sound.MaxPlaysPerFrame = CsAudioTuning.MaxPlaysPerFrame;
            sound.MaxConcurrentPerClip = CsAudioTuning.MaxConcurrentPerClip;
            _log.Always($"音效闸门已下发引擎：单帧上限 {sound.MaxPlaysPerFrame}、" +
                $"同 clip 并发上限 {sound.MaxConcurrentPerClip}（阈值口径见 CsAudioTuning）");
        }
    }
}
