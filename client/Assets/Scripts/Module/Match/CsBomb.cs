using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
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

        private long _plantingId;
        private long _defusingId;
        private float _plantProgress;
        private float _defuseProgress;
        private float _beepTimer;
        private bool _beepFast;
        private bool _beepFastLogged;

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

        /// <summary>下包/拆包进度 0~1（-1 = 未在进行）。</summary>
        public float ActiveUseProgress { get; private set; } = -1f;

        public void Reset()
        {
            Planted = false;
            Dropped = false;
            TimeLeft = -1f;
            Position = Vector3.zero;
            LastKnownPosition = Vector3.zero;
            CarrierId = 0;
            ActiveUseProgress = -1f;
            _plantingId = 0;
            _defusingId = 0;
            _plantProgress = 0f;
            _defuseProgress = 0f;
            _beepTimer = 0f;
            _beepFast = false;
            _beepFastLogged = false;
            _useHeld.Clear();
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

        // ==================================================================
        //  每帧推进
        // ==================================================================
        public void Tick(float dt, float now)
        {
            ActiveUseProgress = -1f;

            var list = _m.ActorList;
            for (var i = 0; i < list.Count; i++) list[i].UseProgress = -1f;

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
            if (_beepTimer > 0f)
            {
                _beepTimer -= dt;
                if (_beepTimer > 0f) return;
            }

            _beepFast = TimeLeft <= CsMatchConst.BombBeepFastThreshold;
            _beepTimer = _beepFast ? CsConst.BombBeepIntervalFast : CsConst.BombBeepIntervalSlow;

            if (_beepFast && !_beepFastLogged)
            {
                _beepFastLogged = true;
                Game.Logger.Info(Tag,
                    $"C4 剩余 {TimeLeft:F1}s ≤ {CsMatchConst.BombBeepFastThreshold:F0}s，蜂鸣切换为快速节拍" +
                    $"（{CsConst.BombBeepIntervalFast:F2}s）");
            }
            else if (!_beepFast && _beepFastLogged)
            {
                _beepFastLogged = false;
            }

            // 音频模块靠这个事件播蜂鸣（见任务书 §4.6「音效触发点」）。
            _m.RaiseBombStateChanged();
        }

        // ==================================================================
        //  下包
        // ==================================================================
        private void TickPlant(float dt)
        {
            // 1) 沿用当前的下包者（只要条件仍满足）
            if (_plantingId != 0)
            {
                var cur = _m.Find(_plantingId);
                if (cur != null && IsHoldingUse(cur) && CanPlant(cur, out _))
                {
                    AccumulatePlant(cur, dt);
                    return;
                }

                if (cur == null || !IsHoldingUse(cur))
                {
                    Interrupt(_plantingId, cur == null ? "下包者已离线" : "松开 E");
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
                if (!IsHoldingUse(a)) continue;

                if (CanPlant(a, out var reason))
                {
                    _plantingId = a.Id;
                    _plantProgress = 0f;
                    Game.Logger.Info(Tag, $"{a.Name} 开始安放 C4（需 {CsConst.PlantTime:F1}s）");
                    AccumulatePlant(a, dt);
                    return;
                }

                _m.RateInfo("plant.holding.invalid", $"{a.Name} 按住 E 但无法下包：{reason}");
            }
        }

        private void AccumulatePlant(CsActor a, float dt)
        {
            _plantProgress += dt / CsConst.PlantTime;
            var p = Mathf.Clamp01(_plantProgress);
            a.UseProgress = p;
            ActiveUseProgress = p;

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
            a.UseProgress = -1f;
            a.Score += CsMatchConst.ScorePerBombObjective;
            _plantingId = 0;
            _plantProgress = 0f;
            _beepTimer = 0f;
            _beepFast = false;
            _beepFastLogged = false;
            ActiveUseProgress = -1f;

            _m.Economy.Add(a, CsConst.RewardBombPlant, "成功安放 C4");

            Game.Logger.Info(Tag,
                $"★ {a.Name} 安放 C4 于 {Position}（{CsConst.BombTimer:F0}s 倒计时开始）");

            _m.PublishMessage(a, "炸弹已安放！");
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
                    Game.Logger.Info(Tag,
                        $"{a.Name} 开始拆除 C4（{(a.HasDefuser ? "有拆弹器" : "无拆弹器")}，需 {dur:F1}s）");
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
            _beepFastLogged = false;
            ActiveUseProgress = -1f;

            _m.Economy.Add(a, CsConst.RewardBombDefuse, "成功拆除 C4");

            Game.Logger.Info(Tag, $"★ {a.Name} 拆除 C4 成功 → CT 赢下本回合");

            _m.PublishMessage(a, "炸弹已被拆除！");
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
            ActiveUseProgress = -1f;

            Game.Logger.Info(Tag, $"★★ C4 于 {pos} 爆炸 → T 赢下本回合");

            _m.EmitEvent(Events.BombExploded);
            _m.RaiseBombStateChanged();
            _m.Damage.ApplyBombExplosion(pos);
            _m.PublishMessage(null, "炸弹已爆炸！");
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
            _m.PublishMessage(null, $"{a.Name} 携带的 C4 掉落了");
            _m.RaiseBombStateChanged();
        }

        /// <summary>走到掉落的 C4 旁自动拾取。</summary>
        public void TryPickupDropped(CsActor a)
        {
            if (!Dropped || Planted) return;
            if (a == null || !a.IsAlive) return;
            if (a.Team != CsTeam.T) return;

            var d = a.Position - Position;
            d.y = 0f;
            if (d.sqrMagnitude > CsMatchConst.PickupRadius * CsMatchConst.PickupRadius) return;

            Dropped = false;
            a.HasBomb = true;
            CarrierId = a.Id;

            Game.Logger.Info(Tag, $"{a.Name} 拾起了掉落的 C4");
            _m.PublishMessage(a, $"{a.Name} 捡起了 C4");
            _m.RaiseBombStateChanged();
        }

        // ==================================================================
        //  工具
        // ==================================================================
        private bool IsHoldingUse(CsActor a)
        {
            return a != null && _useHeld.TryGetValue(a.Id, out var v) && v;
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
