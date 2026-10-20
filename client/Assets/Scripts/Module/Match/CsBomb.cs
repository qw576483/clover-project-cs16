using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Audio;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// C4 炸弹：下包（T）/ 倒计时 / 蜂鸣 / 爆炸 / 拆包（CT）/ 掉落与拾取。
    ///
    /// <para>契约里没有"包掉落"的事件常量（<see cref="Events"/> 里只有 BombPlanted/BombDefused/BombExploded），
    /// 因此掉落只走 <c>Game.Logger</c> + <see cref="CsMatch.PublishMessage"/> —— 见交付回报的未决项。</para>
    /// </summary>
    public sealed class CsBomb
    {
        private const string Tag = CsMatch.Tag;

        private readonly CsMatch _m;
        private readonly Dictionary<long, bool> _useHeld = new Dictionary<long, bool>();
        /// <summary>每个 actor 是否按住左键（下包触发；拆包仍走 <see cref="_useHeld"/> = E）。</summary>
        private readonly Dictionary<long, bool> _plantHeld = new Dictionary<long, bool>();

        /// <summary>拆包动作音（<c>sfx/bomb_defuse</c> = 原版 <c>weapons/c4_disarm.wav</c>）的转发层。</summary>
        private readonly SfxService _sfx = new SfxService("Bomb");

        private long _plantingId;
        private long _defusingId;
        private float _plantProgress;
        private float _defuseProgress;

        // ---- 蜂鸣（**两条独立排期**：声音 = 固定 1.4s（首响 0.5s）；采样档 = 间隔 8s 起每档 ×0.9）----
        private float _beepTimer;      // 声音排期：距离下一声还有多久
        private float _beepInterval;   // 档位排期：当前换档间隔（每换一档 ×CsConst.BombBeepIntervalDecay）
        private float _tierTimer;      // 档位排期：距离下一次换档还有多久
        private float _beepGap;        // 最近一声排下的等待 = CsConst.BombBeepSoundInterval
        private int _beepSerial;       // 本次安放已经响过几声
        private int _beepTier;         // 当前采样档 0..CsConst.BombBeepTierCount-1
        private int _beepTierLogged = -1;

        internal CsBomb(CsMatch m)
        {
            _m = m;
        }

        // ==================================================================
        //  对外状态
        // ==================================================================
        /// <summary>炸弹是否已经安放。</summary>
        public bool Planted { get; private set; }

        /// <summary>已安放炸弹的位置（未安放时为上一次已知位置）。</summary>
        public Vector3 Position { get; private set; }

        /// <summary>最后一次已知的 C4 位置（含掉落点；给雷达用）。</summary>
        public Vector3 LastKnownPosition { get; internal set; }

        /// <summary>剩余秒数（未下包为 -1）。</summary>
        public float TimeLeft { get; private set; } = -1f;

        /// <summary>C4 是否掉在地上等人捡。</summary>
        public bool Dropped { get; internal set; }

        /// <summary>当前携带者 actorId（0 = 无人）。</summary>
        public long CarrierId { get; set; }

        /// <summary>把包丢到地上的那个 actor（0 = 无）。</summary>
        public long DropperActorId { get; internal set; }

        /// <summary>掉落者是否已经走出过 <see cref="CsMatchConst.PickupRadius"/>。
        /// <para>掉落点就在掉落者脚下，若一落地就允许他拾取，同一帧就会被他自己捡回去 ⇒
        /// 包永远上不了地。语义与武器链的 <c>CsDroppedWeapon.DropperReleased</c> 完全一致。</para></summary>
        public bool DropperReleased { get; internal set; }

        /// <summary>下包/拆包进度 0~1（-1 = 未在进行）。</summary>
        public float ActiveUseProgress { get; private set; } = -1f;

        /// <summary>
        /// 是否**正在下包**（进度条进行中、尚未 <see cref="Planted"/>）。
        /// 表现层据此在下包期间就把 C4 画在地面上（问题6："下包时候，地面上看不到包" ——
        /// 过去视图只在 Planted/Dropped 时生成，下包的几秒钟里包在画面上不存在）。
        /// </summary>
        public bool Planting => _plantingId != 0 && !Planted;

        /// <summary>本次安放已经响过的蜂鸣次数（下包瞬间清零；未安放 = 0）。节拍 = 首响 0.5s、之后固定 1.4s。
        /// 表现层按它判断"哪一帧该响一声"，因此不必再自带一份蜂鸣计时器。</summary>
        public int BeepSerial => _beepSerial;

        /// <summary>当前采样档 0..<see cref="CsConst.BombBeepTierCount"/>-1（决定放哪一支采样）；
        /// 安放瞬间即第 1 档，每次换档间隔 8s 起 ×0.9；最后一档之后恒为最后一档。</summary>
        public int BeepTier => _beepTier;

        /// <summary>最近一声排下的等待（秒）= <see cref="CsConst.BombBeepSoundInterval"/>（首声为
        /// <see cref="CsConst.BombBeepFirstSoundDelay"/>）。</summary>
        public float BeepGap => _beepGap;

        public void Reset()
        {
            Planted = false;
            Dropped = false;
            TimeLeft = -1f;
            Position = Vector3.zero;
            LastKnownPosition = Vector3.zero;
            CarrierId = 0;
            DropperActorId = 0;
            DropperReleased = false;
            ActiveUseProgress = -1f;
            _plantingId = 0;
            _defusingId = 0;
            _plantProgress = 0f;
            _defuseProgress = 0f;
            ResetBeepState();
            _useHeld.Clear();
            _plantHeld.Clear();
        }

        /// <summary>蜂鸣状态归零（未安放 / 收尾都走这里）。</summary>
        private void ResetBeepState()
        {
            _beepTimer = 0f;
            _beepInterval = CsConst.BombBeepIntervalInitial;
            _tierTimer = CsConst.BombBeepIntervalInitial;
            _beepGap = 0f;
            _beepSerial = 0;
            _beepTier = 0;
            _beepTierLogged = -1;
        }

        /// <summary>开始蜂鸣：首响等 <see cref="CsConst.BombBeepFirstSoundDelay"/>（原版 +0.5s）；
        /// 换档排期从 <see cref="CsConst.BombBeepIntervalInitial"/>（8s）起算；安放瞬间用第 1 档采样。</summary>
        private void StartBeeping()
        {
            ResetBeepState();
            _beepGap = CsConst.BombBeepFirstSoundDelay;
            _beepTimer = _beepGap;
        }

        /// <summary>某个 actor 是否按住 E（每帧由 CsMatch 写入，机器人写 intent.Use）。</summary>
        public void SetUseState(CsActor a, bool held, float now)
        {
            if (a == null) return;
            _useHeld[a.Id] = held;
        }

        public void ClearUseState(long actorId)
        {
            _useHeld.Remove(actorId);
        }

        /// <summary>某个 actor 是否按住左键（下包触发；每帧由 CsMatch 写入，机器人写 intent.Fire）。</summary>
        public void SetPlantState(CsActor a, bool held)
        {
            if (a == null) return;
            _plantHeld[a.Id] = held;
        }

        // ==================================================================
        //  每帧推进
        // ==================================================================
        public void Tick(float dt, float now)
        {
            ActiveUseProgress = -1f;

            var list = _m.ActorList;
            for (var i = 0; i < list.Count; i++) list[i].UseProgress = -1f;

            // 只在冻结期 / 交战期推进：回合结算与比赛结束期间 C4 停止倒计时、蜂鸣与操作进度，
            // 否则"回合已经判完"之后还会在结算画面里响、甚至把爆炸伤害打到结算中的角色身上。
            var phase = _m.Phase;
            if (phase != CsRoundPhase.Freeze && phase != CsRoundPhase.Live)
            {
                if (_plantingId != 0 || _defusingId != 0)
                {
                    Game.Logger.Info(Tag, $"回合已结束（{phase}）：C4 操作进度清零");
                    _plantingId = 0;
                    _plantProgress = 0f;
                    _defusingId = 0;
                    _defuseProgress = 0f;
                }
                return;
            }

            // ---- 已安放：倒计时 + 蜂鸣 ----
            if (Planted)
            {
                TimeLeft -= dt;
                TickBeep(dt);

                if (TimeLeft <= 0f)
                {
                    TimeLeft = 0f;
                    Explode();
                    return;
                }
            }

            // ---- 下包（T）----
            if (!Planted)
            {
                if (phase == CsRoundPhase.Live) LogPlantKeyHints();
                TickPlant(dt);
            }
            else
            {
                if (_plantingId != 0)
                {
                    Interrupt(_plantingId, "炸弹已经安放，下包流程结束");
                    _plantingId = 0;
                    _plantProgress = 0f;
                }
            }

            // ---- 拆包（CT）----
            if (Planted)
            {
                TickDefuse(dt);
            }
            else if (_defusingId != 0)
            {
                Interrupt(_defusingId, "炸弹已不在场，拆包流程结束");
                _defusingId = 0;
                _defuseProgress = 0f;
            }
        }

        private void TickBeep(float dt)
        {
            // ---- 档位排期（原版 mp.dll: 排期 0x100449BE → 衰减 0x100449D8 → 换音 0x100449F6.. → 计数 +1 @0x10044AD1）----
            // 只决定"这时该放哪一支采样"，与"何时响"无关：先用**未衰减**的间隔排下一次换档，再让间隔 ×0.9，最后档位 +1。
            _tierTimer -= dt;
            if (_tierTimer <= 0f)
            {
                var tierGap = _beepInterval;
                _tierTimer = tierGap;
                _beepInterval *= CsConst.BombBeepIntervalDecay;
                _beepTier = Mathf.Min(_beepTier + 1, CsConst.BombBeepTierCount - 1);

                if (_beepTier != _beepTierLogged)
                {
                    _beepTierLogged = _beepTier;
                    Game.Logger.Info(Tag,
                        $"C4 蜂鸣换档 → 第 {_beepTier + 1}/{CsConst.BombBeepTierCount} 档" +
                        $"（换档间隔 {tierGap:F3}s → {_beepInterval:F3}s，剩余 {TimeLeft:F1}s）");
                }
            }

            // ---- 声音排期（原版 mp.dll 0x10044AD7：m_flNextBeep = time + 1.4f，固定节拍，不受档位影响）----
            _beepTimer -= dt;
            if (_beepTimer <= 0f)
            {
                _beepGap = CsConst.BombBeepSoundInterval;
                _beepTimer = _beepGap;
                _beepSerial++;
                _m.RaiseBombStateChanged();
            }
        }

        // ==================================================================
        //  下包
        // ==================================================================
        /// <summary>
        /// 持包进包点却按 E：E 是拆包键，下包要**先切出 C4（5 号槽）再按住左键**。
        /// 只在对局进行中、未安放、人活着且确实在包点内时留痕，按 actor 限速。
        /// </summary>
        private void LogPlantKeyHints()
        {
            var list = _m.ActorList;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null || !a.IsAlive || a.Team != CsTeam.T) continue;
                if (!a.HasBomb || !IsHoldingUse(a)) continue;
                if (!IsInBombsite(a.Position, out var site)) continue;

                if (a.ActiveWeapon != CsWeapons.C4)
                {
                    _m.RateInfo("plant.use.notc4." + a.Id,
                        $"{a.Name} 在 {site} 按住 E 不能下包：未切出 C4（按 5 切出 C4，按住左键下包）");
                }
                else
                {
                    _m.RateInfo("plant.use.c4." + a.Id,
                        $"{a.Name} 在 {site} 按住 E 不能下包：已切出 C4，下包要按住左键（E 只用于拆包）");
                }
            }
        }

        private void TickPlant(float dt)
        {
            // 1) 沿用当前的下包者（只要条件仍满足）
            if (_plantingId != 0)
            {
                var cur = _m.Find(_plantingId);
                if (cur != null && IsHoldingPlant(cur) && CanPlant(cur, out _))
                {
                    AccumulatePlant(cur, dt);
                    return;
                }

                if (cur == null || !IsHoldingPlant(cur))
                {
                    Interrupt(_plantingId, cur == null ? "下包者已离线" : "松开左键");
                }
                else
                {
                    CanPlant(cur, out var why);
                    Interrupt(_plantingId, why ?? "条件不再满足");
                }
                _plantingId = 0;
                _plantProgress = 0f;
            }

            // 2) 找一个新的下包候选
            var list = _m.ActorList;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (!IsHoldingPlant(a)) continue;

                if (CanPlant(a, out var reason))
                {
                    _plantingId = a.Id;
                    _plantProgress = 0f;
                    Game.Logger.Info(Tag, $"{a.Name} 开始安放 C4（需 {CsConst.PlantTime:F1}s）");
                    AccumulatePlant(a, dt);
                    return;
                }

                _m.RateInfo("plant.holding.invalid", $"{a.Name} 按住左键但无法下包：{reason}");
            }
        }

        private void AccumulatePlant(CsActor a, float dt)
        {
            _plantProgress += dt / CsConst.PlantTime;
            var p = Mathf.Clamp01(_plantProgress);
            a.UseProgress = p;
            ActiveUseProgress = p;

            // 下包进行中把包钉在下包者脚下：表现层画世界视图用的坐标（BombPosition）在
            // Planted 之前只能取 LastKnownPosition —— 不更新它就会画在上一次的已知位置 / 原点。
            Position = a.Position;
            LastKnownPosition = Position;

            if (_plantProgress >= 1f)
            {
                PlantComplete(a);
            }
        }

        private bool CanPlant(CsActor a, out string reason)
        {
            reason = null;
            if (a == null || !a.IsAlive) { reason = "已死亡"; return false; }
            if (a.Team != CsTeam.T) { reason = "只有 T 能下包"; return false; }
            if (Planted) { reason = "炸弹已经安放"; return false; }
            if (!a.HasBomb) { reason = "身上没有 C4"; return false; }
            if (a.ActiveWeapon != CsWeapons.C4) { reason = "未切出 C4"; return false; }
            if (!IsInBombsite(a.Position, out var site)) { reason = "不在 A/B 包点内"; return false; }
            if (IsMoving(a)) { reason = "移动中（下包需要停下）"; return false; }
            return true;
        }

        private void PlantComplete(CsActor a)
        {
            Planted = true;
            Position = a.Position;
            LastKnownPosition = Position;
            TimeLeft = CsConst.BombTimer;
            Dropped = false;
            CarrierId = 0;
            a.HasBomb = false;
            // 安放后 C4 已离开手上：换回主武器（原版 C4::PrimaryAttack 安放完会 SelectItem 换枪）。
            _m.Inventory.SelectBestWeapon(a);
            a.UseProgress = -1f;
            a.Score += CsMatchConst.ScorePerBombObjective;
            _plantingId = 0;
            _plantProgress = 0f;
            StartBeeping();
            ActiveUseProgress = -1f;

            _m.Economy.Add(a, CsConst.RewardBombPlant, "成功安放 C4");

            Game.Logger.Info(Tag,
                $"★ {a.Name} 安放 C4 于 {Position}（{CsConst.BombTimer:F0}s 倒计时开始）");

            _m.PublishMessage(a, "The bomb has been planted.");
            _m.EmitEvent(Events.BombPlanted);
            _m.RaiseBombStateChanged();
        }

        // ==================================================================
        //  拆包
        // ==================================================================
        private void TickDefuse(float dt)
        {
            if (_defusingId != 0)
            {
                var cur = _m.Find(_defusingId);
                if (cur != null && IsHoldingUse(cur) && CanDefuse(cur, out _))
                {
                    AccumulateDefuse(cur, dt);
                    return;
                }

                if (cur == null || !IsHoldingUse(cur))
                {
                    Interrupt(_defusingId, cur == null ? "拆包者已离线" : "松开 E");
                }
                else
                {
                    CanDefuse(cur, out var why);
                    Interrupt(_defusingId, why ?? "条件不再满足");
                }
                _defusingId = 0;
                _defuseProgress = 0f;
            }

            var list = _m.ActorList;
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (!IsHoldingUse(a)) continue;

                if (CanDefuse(a, out var reason))
                {
                    _defusingId = a.Id;
                    _defuseProgress = 0f;
                    var dur = a.HasDefuser ? CsConst.DefuseTimeWithKit : CsConst.DefuseTime;
                    _sfx.PlayAt(CsAudioTuning.BombDefuse, Position);
                    Game.Logger.Info(Tag,
                        $"{a.Name} 开始拆除 C4（{(a.HasDefuser ? "有拆弹器" : "无拆弹器")}，需 {dur:F1}s），已播拆包动作音");
                    AccumulateDefuse(a, dt);
                    return;
                }

                _m.RateInfo("defuse.holding.invalid", $"{a.Name} 按住 E 但无法拆包：{reason}");
            }
        }

        private void AccumulateDefuse(CsActor a, float dt)
        {
            var dur = a.HasDefuser ? CsConst.DefuseTimeWithKit : CsConst.DefuseTime;
            _defuseProgress += dt / dur;
            var p = Mathf.Clamp01(_defuseProgress);
            a.UseProgress = p;
            ActiveUseProgress = p;

            if (_defuseProgress >= 1f)
            {
                DefuseComplete(a);
            }
        }

        private bool CanDefuse(CsActor a, out string reason)
        {
            reason = null;
            if (a == null || !a.IsAlive) { reason = "已死亡"; return false; }
            if (a.Team != CsTeam.CT) { reason = "只有 CT 能拆包"; return false; }
            if (!Planted) { reason = "炸弹尚未安放"; return false; }
            if (IsMoving(a)) { reason = "移动中（拆包需要停下）"; return false; }

            var d = a.Position - Position;
            d.y = 0f;
            if (d.sqrMagnitude > CsMatchConst.DefuseRadius * CsMatchConst.DefuseRadius)
            {
                reason = $"离炸弹太远（需 ≤ {CsMatchConst.DefuseRadius:F1}m）";
                return false;
            }
            return true;
        }

        private void DefuseComplete(CsActor a)
        {
            Planted = false;
            TimeLeft = -1f;
            Dropped = false;
            a.UseProgress = -1f;
            a.Score += CsMatchConst.ScorePerBombObjective;
            _defusingId = 0;
            _defuseProgress = 0f;
            ResetBeepState();
            ActiveUseProgress = -1f;

            // 原版不发"拆包个人奖励"：22 格奖金表（.data 0x10163AB0）里没有这一档，
            // 整支 mp.dll 的 .text 也没有对应的金额立即数 ⇒ 拆包者只得分队奖励。
            Game.Logger.Info(Tag,
                $"★ {a.Name} 拆除 C4 成功 → CT 赢下本回合（拆包者无个人奖励：原版奖金表无此档）");

            _m.PublishMessage(a, "The bomb has been defused.");
            _m.EmitEvent(Events.BombDefused);
            _m.RaiseBombStateChanged();
            _m.Round.EndRound(CsTeam.CT, CsRoundEndReason.BombDefused);
        }

        // ==================================================================
        //  爆炸
        // ==================================================================
        private void Explode()
        {
            var pos = Position;
            Planted = false;
            TimeLeft = -1f;
            Dropped = false;
            ResetBeepState();
            ActiveUseProgress = -1f;

            Game.Logger.Info(Tag, $"★★ C4 于 {pos} 爆炸 → T 赢下本回合");

            _m.EmitEvent(Events.BombExploded);
            _m.RaiseBombExploded(pos);
            _m.RaiseBombStateChanged();
            _m.Damage.ApplyBombExplosion(pos);
            _m.PublishMessage(null, "The bomb has exploded.");
            _m.Round.EndRound(CsTeam.T, CsRoundEndReason.BombExploded);
        }

        // ==================================================================
        //  掉落 / 拾取
        // ==================================================================
        /// <summary>携带者丢失 C4（阵亡 / 丢弃 / 换阵营）→ 掉在地上。</summary>
        public void OnCarrierLost(CsActor a)
        {
            if (a == null || !a.HasBomb) return;

            a.HasBomb = false;
            CarrierId = 0;
            DropperActorId = a.Id;
            DropperReleased = false;
            ClearUseState(a.Id);

            if (Planted)
            {
                // 包已经下好，谁拿着 C4 已经不重要了。
                return;
            }

            Dropped = true;
            Position = a.Position;
            LastKnownPosition = Position;

            Game.Logger.Info(Tag, $"{a.Name} 携带的 C4 掉落在 {Position}（T 走过去可拾取）");
            _m.PublishMessage(null, $"{a.Name} dropped the bomb.");
            _m.RaiseBombStateChanged();
        }

        /// <summary>走到掉落的 C4 旁自动拾取。
        /// <para>与武器链同一条口径：掉落者本人必须先走出 <see cref="CsMatchConst.PickupRadius"/>
        /// 才允许把自己丢下的包捡回 —— 否则丢包那一帧就被自己捡回，包永远上不了地。</para></summary>
        public void TryPickupDropped(CsActor a)
        {
            if (!Dropped || Planted) return;
            if (a == null || !a.IsAlive) return;
            if (a.Team != CsTeam.T) return;

            var d = a.Position - Position;
            d.y = 0f;
            var near = d.sqrMagnitude <= CsMatchConst.PickupRadius * CsMatchConst.PickupRadius;

            if (a.Id == DropperActorId && !DropperReleased)
            {
                if (near) return;
                DropperReleased = true;
                return;
            }

            if (!near) return;

            Dropped = false;
            a.HasBomb = true;
            CarrierId = a.Id;

            Game.Logger.Info(Tag, $"{a.Name} 拾起了掉落的 C4");
            _m.PublishMessage(a, $"{a.Name} picked up the bomb.");
            _m.RaiseBombStateChanged();
        }

        // ==================================================================
        //  工具
        // ==================================================================
        private bool IsHoldingUse(CsActor a)
        {
            return a != null && _useHeld.TryGetValue(a.Id, out var v) && v;
        }

        private bool IsHoldingPlant(CsActor a)
        {
            return a != null && _plantHeld.TryGetValue(a.Id, out var v) && v;
        }

        private static bool IsMoving(CsActor a)
        {
            var v = a.Velocity;
            return v.x * v.x + v.z * v.z > CsMatchConst.MoveCancelSpeedSqr;
        }

        private void Interrupt(long actorId, string reason)
        {
            var a = _m.Find(actorId);
            var name = a != null ? a.Name : $"actor {actorId}";
            if (a != null) a.UseProgress = -1f;

            Game.Logger.Info(Tag, $"{name} 的炸弹操作被打断：{reason}（进度清零）");
        }

        /// <summary>某点是否落在 A/B 包点半径内。</summary>
        public bool IsInBombsite(Vector3 pos, out string site)
        {
            site = null;
            var map = _m.Map;
            if (map == null || !map.IsLoaded)
            {
                _m.RateWarn("bombsite.nomap", "地图未加载，无法判定包点范围");
                return false;
            }

            if (InRadius(map, pos, CsMarkers.BombsiteA)) { site = "Bombsite A"; return true; }
            if (InRadius(map, pos, CsMarkers.BombsiteB)) { site = "Bombsite B"; return true; }
            return false;
        }

        private bool InRadius(ICsMap map, Vector3 pos, string marker)
        {
            var pts = map.Points(marker);
            if (pts == null || pts.Length == 0)
            {
                _m.RateWarn("bombsite.marker.missing." + marker, $"地图缺少包点标记 {marker}");
                return false;
            }

            var r2 = CsMarkers.BombsiteRadius * CsMarkers.BombsiteRadius;
            for (var i = 0; i < pts.Length; i++)
            {
                var d = pos - pts[i];
                d.y = 0f;
                if (d.sqrMagnitude <= r2) return true;
            }
            return false;
        }
    }
}
