using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Audio;
using Cs16.Module.CameraRig;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 本地玩家的**开火/换弹/切枪/开镜**输入采集，以及射击的表现与命中回传。
    ///
    /// <para><b>弹药/射速/后坐力都不在这里扣</b>：按主 agent 的裁决，<c>cmd.Fire = true</c> 后由比赛模拟
    /// 内部扣弹、限射速、累后坐力；模拟每打出一发就往"待表现队列"里记一条，
    /// 本模块用 <see cref="ICsMatch.ConsumeShotFired"/> **消费到属于自己那一条**之后才做射线检测 ——
    /// 这样"射线发数 == 扣弹数"，不会多打也不会少打。</para>
    ///
    /// <para><b>为什么不能"消费到一条就射线"</b>：队列里同时也有**机器人的射击记录**
    /// （模拟给每个开火者都 <c>RecordShotFired</c>，机器人用它播枪声）。若不加区分，
    /// 一次抢在本地射击前面的机器人记录会被当成"我打的"，于是从我眼睛里多打一条射线 ——
    /// 那是**双份伤害**。这里的判定是：先用"本帧模拟是否真的扣过我的弹/推过我的射速时间"
    /// （<c>NextFireTime</c> 前进）确认"我这一发成立"，再去队列里找**与本帧手持武器一致**的那一条。</para>
    ///
    /// <para><b>哪些武器不射线</b>：刀与手雷由模拟内部直接结算（<c>FireKnife</c> / <c>ThrowGrenade</c>），
    /// 本模块对它们**只做表现**（枪声、投掷视觉），绝不再射线，否则就是双份伤害。</para>
    /// </summary>
    public sealed class CombatModule : MonoBehaviour
    {
        private const string Tag = "Combat";

        private readonly CsModuleLog _log = new CsModuleLog(Tag);
        private readonly Firearm _firearm = new Firearm();
        private readonly CrosshairState _crosshair = new CrosshairState();
        private readonly CombatAudio _audio = new CombatAudio();
        private readonly CombatEffects _fx = new CombatEffects();
        private readonly GrenadeThrower _nades = new GrenadeThrower();

        /// <summary>本帧从模拟队列取走的射击记录（复用，避免每帧产生垃圾）。</summary>
        private readonly List<string> _drainedShots = new List<string>(CsCombatTuning.MaxShotsConsumedPerFrame);

        private ICsMatch _match;
        private FirstPersonCamera _view;

        // ---- 本帧输入快照（必须在模拟 Tick 之前取）----
        private bool _fireRequested;
        private float _preNextFireTime;
        private string _preWeapon;

        private bool _autoReload = true;
        private int _unmatchedShotCount;

        /// <summary>切片K（D8）：上一次空仓击发音的时刻（秒，<c>Time.time</c>）——见 <see cref="CanFire"/>。</summary>
        private float _lastDryfireTime = -999f;

        // ---- 「上一把武器」（Q = 原版 lastinv）跟踪；纯本模块内部状态，不动任何契约 ----
        private string _lastSeenWeapon;
        private string _previousWeapon;

        /// <summary>准星扩散 0~1（同时写进 <c>CsHudSnapshot.CrosshairSpread</c> 供 HUD 读）。</summary>
        public float CrosshairSpread => _crosshair.Value;

        /// <summary>命中标记剩余显示时间（同时写进 <c>CsHudSnapshot.HitMarkerTime</c>）。</summary>
        public float HitMarkerTime => CsHudSnapshot.HitMarkerTime;

        /// <summary>最近一次命中是否爆头（同时写进 <c>CsHudSnapshot.HitMarkerHeadshot</c>）。</summary>
        public bool HitMarkerHeadshot => CsHudSnapshot.HitMarkerHeadshot;

        /// <summary>闪光弹致盲的全屏白强度 0~1（写进 <c>CsHudSnapshot.FlashAlpha</c>，由 HUD 绘制）。</summary>
        public float FlashAlpha => CsHudSnapshot.FlashAlpha;

        // ==================================================================
        //  装配
        // ==================================================================
        internal void Init(ICsMatch match, FirstPersonCamera view)
        {
            _match = match;
            _view = view;
            _fx.Init();
            _nades.Init(_fx);
            _log.Always("战斗模块就绪（射线由本模块负责，伤害结算归比赛模拟）");
        }

        /// <summary>玩家设置：空仓自动换弹。</summary>
        internal void ApplySettings(bool autoReload)
        {
            _autoReload = autoReload;
        }

        private void OnDestroy()
        {
            _nades.Dispose();
            _fx.Dispose();
        }

        // ==================================================================
        //  每帧（Update，早于模拟 Tick）
        // ==================================================================
        /// <param name="inputAllowed">面板/暂停/比赛未开始时为 false —— 连开火意图都不产生。</param>
        internal void FillInput(ref CsInputState cmd, bool inputAllowed)
        {
            _fireRequested = false;
            _preWeapon = null;
            _preNextFireTime = 0f;

            var match = _match;
            var local = match != null ? match.LocalPlayer : null;
            var input = Game.Input;

            // ---- E：下包 / 拆包（按住式）----
            // **必须在"死亡/面板打开"的早退之前下发**：模拟侧的 SetUseHeld 是"按住状态"，
            // 只在活着时才下发会让状态粘住 —— 表现就是"死前按住 E → 复活瞬间站在包点自动下包"。
            // 另外只在 Live 阶段下发"按住"：冻结期按住 E 不该推进进度。
            var useHeld = inputAllowed && local != null && local.IsAlive && input != null &&
                          match.Phase == CsRoundPhase.Live && input.GetKey(GameKey.E);
            match.SetUseHeld(useHeld);

            if (local == null || !local.IsAlive) return;
            if (input == null) return;

            // ★ 必须早于模拟 Tick：这是"这一发到底是不是我打的"的判据（见类注释）。
            _preWeapon = local.ActiveWeapon;
            _preNextFireTime = local.NextFireTime;

            // ---- 维护「上一把武器」：手持武器一变，就把变之前那一把记下来（供 Q = lastinv）----
            if (_lastSeenWeapon != null && _lastSeenWeapon != local.ActiveWeapon)
            {
                _previousWeapon = _lastSeenWeapon;
            }
            _lastSeenWeapon = local.ActiveWeapon;

            var def = local.ActiveDef;

            if (inputAllowed)
            {
                // ---- 右键开镜（模拟内部再按武器大类过滤：只有狙击枪才算开镜）----
                cmd.Zoom = input.GetKey(GameKey.MouseRight);

                // ---- 左键开火（门槛在模拟里也有一份，这里只是为了不产生必然失败的开火意图）----
                if (input.GetMouseButton(0) && CanFire(local, def, match))
                {
                    cmd.Fire = true;
                    _fireRequested = true;
                }
            }

            // ---- 换弹 / 空仓自动换弹 ----
            var reloadRequested = inputAllowed && input.GetKeyDown(GameKey.R);
            if (reloadRequested) match.RequestReload();
            else if (_autoReload && inputAllowed && def != null && def.Magazine > 0)
            {
                var ammo = local.GetAmmo(def.Id);
                if (ammo.inMag <= 0 && ammo.reserve > 0 && Time.time >= local.ReloadEndTime)
                {
                    match.RequestReload();
                }
            }

            // ---- 1-5 切槽 ----
            if (inputAllowed)
            {
                if (input.GetKeyDown(GameKey.Num1)) match.SwitchSlot(1);
                else if (input.GetKeyDown(GameKey.Num2)) match.SwitchSlot(2);
                else if (input.GetKeyDown(GameKey.Num3)) match.SwitchSlot(3);
                else if (input.GetKeyDown(GameKey.Num4)) match.SwitchSlot(4);
                else                 if (input.GetKeyDown(GameKey.Num5)) match.SwitchSlot(5);
            }

            // ---- Q / G / M：原版 CS 1.6 config.cfg 的**默认绑定**
            //      （`bind "q" "lastinv"` / `bind "g" "drop"` / `bind "m" "chooseteam"`）----
            // 出处：原版默认绑定表（策划/策划案/CS1.6单机参考规格.md 的游戏内按键段）
            //       + 本工程落点 = 本模块的本地输入采集（与 R / 1-5 / E 同一处）。
            if (inputAllowed)
            {
                // Q：切回上一把武器（原版 lastinv）。上一把由上面的"手持武器变化"跟踪给出。
                if (input.GetKeyDown(GameKey.Q))
                {
                    if (!string.IsNullOrEmpty(_previousWeapon) && _previousWeapon != local.ActiveWeapon)
                    {
                        match.SwitchWeapon(_previousWeapon);
                        _log.Info("input.lastinv", $"Q 切回上一把武器：{_previousWeapon}（原版 lastinv）");
                    }
                    else
                    {
                        _log.Info("input.lastinv", "Q 切回上一把武器：还没有可切的上一把（原版 lastinv）");
                    }
                }

                // G：丢下当前武器（原版 drop）。
                if (input.GetKeyDown(GameKey.G))
                {
                    var dropId = local.ActiveWeapon;
                    match.DropActiveWeapon();
                    _log.Always($"G 丢下当前武器：{(string.IsNullOrEmpty(dropId) ? "（无）" : dropId)}（原版 drop）");
                }

                // M：换阵营（原版 chooseteam）。
                // ⚠️ 原版 chooseteam 是**打开阵营菜单**；本工程局内没有"再开一次 TeamMenu"的流程入口，
                //    因此等价为"直接换到另一边"并如实写日志 —— 差异登记见 策划/差异登记.tsv / 验收表「允许的差异」。
                if (input.GetKeyDown(GameKey.M))
                {
                    var other = local.Team == CsTeam.T ? CsTeam.CT : CsTeam.T;
                    Game.Event.Emit(Events.ChangeTeam, other);
                    _log.Always($"M 换阵营 → {other}（原版 chooseteam；本工程无局内阵营菜单，等价为直接换边）");
                }
            }
        }

        /// <summary>
        /// 开火门槛（全部要过，任一不过就不产生 <c>Fire</c> 意图并打**降频**日志）。
        /// 与模拟内部的 <c>TryDischarge</c> 门槛保持一致 —— 这样"我判过了"和"模拟判过了"不会互相矛盾。
        /// </summary>
        private bool CanFire(CsActor local, CsWeaponDef def, ICsMatch match)
        {
            if (def == null)
            {
                _log.Info("fire.noweapon", "没有手持武器，开火被忽略");
                return false;
            }

            if (match.Phase != CsRoundPhase.Live)
            {
                _log.Info("fire.phase", $"当前阶段 {match.Phase} 不允许开火（冻结/结算期）");
                return false;
            }

            var now = Time.time;

            // 射速限制：高频路径（自动武器每帧都会走到），静默 —— 模拟侧同样静默。
            if (now < local.NextFireTime) return false;

            if (now < local.ReloadEndTime)
            {
                _log.Info("fire.reloading", "换弹中，开火被忽略");
                return false;
            }

            if (now < local.SwitchEndTime)
            {
                _log.Info("fire.switching", "切枪中，开火被忽略");
                return false;
            }

            if (def.Class == CsWeaponClass.Bomb)
            {
                _log.Info("fire.bomb", "手持 C4 时不能开火（按 E 下包）");
                return false;
            }

            if (def.Magazine > 0)
            {
                var ammo = local.GetAmmo(def.Id);
                if (ammo.inMag <= 0)
                {
                    // 切片K（D8）：空仓扣扳机 = 原版 dryfire（盘上 sfx/dryfire.wav 此前无人挂事件）。
                    // 这里就是"弹匣为空"的唯一分支（模拟侧同样拦在这里 ⇒ 没有第二处）。
                    // 按时间闸限速：按住左键时本分支**每帧**都会走到，不加闸就是每帧一响。
                    if (now - _lastDryfireTime >= CsAudioTuning.DryfireMinInterval)
                    {
                        _lastDryfireTime = now;
                        _audio.Play(CsAudioTuning.Dryfire);
                    }
                    _log.Info("fire.empty." + def.Id, $"{def.DisplayName} 弹匣为空（按 R 换弹）");
                    return false;
                }
            }

            return true;
        }

        // ==================================================================
        //  每帧（LateUpdate，晚于模拟 Tick）
        // ==================================================================
        /// <param name="eyePosition">射线起点（相机给的眼睛位置，不含 bob）。</param>
        /// <param name="aimDirection">视线方向（含后坐力表现，与准星一致）。</param>
        internal void LateTick(float dt, Vector3 eyePosition, Vector3 aimDirection)
        {
            var match = _match;
            var local = match != null ? match.LocalPlayer : null;
            var def = local != null ? local.ActiveDef : null;

            // ---- 准星扩散（契约指定由本模块写 CsHudSnapshot.CrosshairSpread）----
            _crosshair.Tick(dt, local, def, match != null && match.IsZoomed);
            CsHudSnapshot.CrosshairSpread = _crosshair.Value;

            // ---- 命中标记倒计时 ----
            // 暂停时用 dt=0：模拟侧（WriteHudSnapshot）在暂停时同样传 0，两边口径保持一致。
            var hudDt = match != null && match.IsPaused ? 0f : dt;
            if (CsHudSnapshot.HitMarkerTime > 0f)
            {
                CsHudSnapshot.HitMarkerTime = Mathf.Max(0f, CsHudSnapshot.HitMarkerTime - hudDt);
            }

            // ---- 闪光弹致盲（契约指定由本模块写 CsHudSnapshot.FlashAlpha）----
            CsHudSnapshot.FlashAlpha = ComputeFlashAlpha(local);

            // ---- 视觉推进 ----
            _fx.Tick(dt);
            _nades.Tick(dt);

            // ---- 消费本帧的射击记录 ----
            ConsumeShots(local, def, eyePosition, aimDirection, match);
        }

        private static float ComputeFlashAlpha(CsActor local)
        {
            if (local == null || !local.IsAlive) return 0f;

            // 时间基准与模拟一致（CsMatch.Clock 默认 Time.time）。
            var left = local.FlashEndTime - Time.time;
            if (left <= 0f) return 0f;

            return Mathf.Clamp01(left / CsConst.GrenadeFlashDuration);
        }

        // ==================================================================
        //  射击队列的消费
        // ==================================================================
        /// <summary>
        /// **本帧模拟是否真的打出了"我的那一发"** —— 消费射击队列时的核心判据。
        ///
        /// <para>为什么不能只看"请求过开火"：请求了也可能被射速/换弹/切枪/空仓拦下（模拟没扣弹、没记录）。
        /// 为什么不能只看"队列里有记录"：队列里同时有**机器人**的射击（模拟给每个开火者都记一条），
        /// 拿别人的记录去射线 = 从我眼睛里多打一发 = **双份伤害**。</para>
        ///
        /// <para>判据：我请求过开火 && 模拟把 <c>NextFireTime</c> 往后推了（扣弹与限速在模拟的同一条分支里完成）。</para>
        /// </summary>
        public static bool IsLocalShot(bool fireRequested, float preNextFireTime, float postNextFireTime)
        {
            return fireRequested && postNextFireTime > preNextFireTime + 0.0001f;
        }

        /// <summary>
        /// 从"本帧取走的射击记录"里挑出**属于我的那一发**（纯逻辑，便于离线自证）。
        ///
        /// <para>规则：只有 <paramref name="localFired"/>（= <see cref="IsLocalShot"/> 成立）时才认领，
        /// **最多认领一条**，且必须与"开火前手持的武器"一致；其余全部只当别人的枪声处理。</para>
        ///
        /// <para>为什么认领**只能有一条**：一帧内本地不可能打出两发（射速上限 &gt; 16ms）。
        /// 若把同一帧的多条都当自己的，就是"一次射击打出多发命中"。</para>
        /// </summary>
        /// <param name="drained">本帧按顺序取走的记录（已包含我与机器人的）。</param>
        /// <param name="localFired">本帧是否确认打出了"我的那一发"。</param>
        /// <param name="preWeapon">开火**之前**手持的武器 id（手雷投出后手持会变，所以必须用开火前的）。</param>
        /// <param name="otherCount">输出：属于别人的记录条数（放枪声用）。</param>
        /// <returns>属于我的那一发的武器 id；没有则 null。</returns>
        public static string PickMyShot(IReadOnlyList<string> drained,
            bool localFired, string preWeapon, out int otherCount)
        {
            otherCount = 0;
            if (drained == null) return null;

            string mine = null;
            for (var i = 0; i < drained.Count; i++)
            {
                var weaponId = drained[i];
                if (mine == null && localFired && !string.IsNullOrEmpty(preWeapon) && weaponId == preWeapon)
                {
                    mine = weaponId;
                    continue;
                }

                otherCount++;
            }

            return mine;
        }

        private void ConsumeShots(CsActor local, CsWeaponDef def, Vector3 eye, Vector3 aim, ICsMatch match)
        {
            if (match == null) return;

            // ① 本帧模拟是否真的打出了"我的那一发"？（判据见 IsLocalShot）
            var localFired = local != null && IsLocalShot(_fireRequested, _preNextFireTime, local.NextFireTime);

            // ② 把本帧的记录全部取走（模拟会警告"没人消费"），再挑出属于我的那一条。
            _drainedShots.Clear();
            while (_drainedShots.Count < CsCombatTuning.MaxShotsConsumedPerFrame &&
                   match.ConsumeShotFired(out var weaponId))
            {
                _drainedShots.Add(weaponId);
            }

            var consumed = _drainedShots.Count;
            if (consumed >= CsCombatTuning.MaxShotsConsumedPerFrame)
            {
                _log.Warn("shot.queue.drain", $"单帧消费射击记录达到上限 {CsCombatTuning.MaxShotsConsumedPerFrame}（队列可能积压）");
            }

            var mineWeapon = PickMyShot(_drainedShots, localFired, _preWeapon, out var otherCount);

            // 别人的射击（机器人）：只放枪声（2D），**绝不**从我的眼睛里射线。
            // 本模块是这条队列的唯一消费者，因此"所有人（含 bot）的枪声"都由这里放。
            var claimed = false;
            for (var i = 0; i < consumed; i++)
            {
                var id = _drainedShots[i];
                if (!claimed && mineWeapon != null && id == mineWeapon)
                {
                    claimed = true;     // 我的那一条由 HandleLocalShot 发声，避免重复播两次
                    continue;
                }

                var otherDef = CsWeapons.Get(id);
                if (otherDef != null) _audio.Play(otherDef.SoundFire);
            }

            if (otherCount > 0)
            {
                // 高频路径 → 降频（用于排查"机器人的射击有没有被误当成我的"）。
                _log.Info("shot.others", $"本帧另有 {otherCount} 条他人射击记录（只放枪声，不做射线）");
            }

            if (mineWeapon != null)
            {
                _unmatchedShotCount = 0;
                HandleLocalShot(mineWeapon, local, eye, aim, match);
                return;
            }

            if (!localFired) return;

            // 请求过开火、模拟也确实打了，但队列里没找到"我的那一条"（极端竞态）。
            // 宁可**漏一发表现**，也不能拿别人的记录去射线（那是双份伤害）。
            _unmatchedShotCount++;
            _log.Warn("shot.unmatched",
                $"本地射击未被队列确认（第 {_unmatchedShotCount} 次）：手持={_preWeapon ?? "null"} " +
                $"本帧消费={consumed} 条，本发不做射线（避免打到别人的记录上）");
        }

        private void HandleLocalShot(string weaponId, CsActor local, Vector3 eye, Vector3 aim, ICsMatch match)
        {
            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                _log.Warn("shot.nodef", $"射击队列给出了未知武器 id「{weaponId}」，本发只跳过表现");
                return;
            }

            // ---- 枪声（自己的枪声一定响）----
            _audio.Play(def.SoundFire);

            // ---- 枪口火焰 / 投掷视觉 ----
            if (def.Class == CsWeaponClass.Grenade)
            {
                _nades.ThrowVisual(eye, aim, def);
            }
            else if (def.Class != CsWeaponClass.Knife && def.Class != CsWeaponClass.Bomb)
            {
                _fx.MuzzleFlash(eye, aim);
            }

            // ---- 刀 / 手雷 / C4：模拟内部已直接结算，这里绝不再射线 ----
            if (def.Class == CsWeaponClass.Knife || def.Class == CsWeaponClass.Grenade ||
                def.Class == CsWeaponClass.Bomb)
            {
                return;
            }

            if (_view == null || !_view.Ready)
            {
                _log.Warn("shot.nocamera", "第一人称相机未就绪，本发只有声音没有射线（命中判定缺失）");
                return;
            }

            // ---- 散布（开镜且已稳定才吃"开镜精度"）----
            var scoped = match.IsZoomed && _view.ScopeSettled;
            var spread = Firearm.ComputeSpread(local, def, scoped);

            var request = new ShotRequest
            {
                ShooterId = local.Id,
                Def = def,
                Origin = eye,
                Direction = aim,
                Spread = spread,
                Range = def.Range,
                Pellets = def.Pellets,
            };

            _firearm.Fire(request);

            // ---- 弹道表现 ----
            var ends = _firearm.TracerEnds;
            for (var i = 0; i < ends.Count; i++) _fx.Tracer(eye, ends[i]);

            // ---- 弹痕 + 火星（打在墙上的那些弹丸；打在人身上的不留痕）----
            var impactPoints = _firearm.ImpactPoints;
            var impactMaterials = _firearm.ImpactMaterials;
            for (var i = 0; i < impactPoints.Count; i++)
            {
                _fx.BulletImpact(impactPoints[i], _firearm.ImpactNormals[i]);

                // 切片K（D8）：打中**非角色**碰撞体 ⇒ 弹着音（原版 hit_wall，盘上 sfx/hit_wall.wav 此前无人挂事件）。
                // "按材质分流"落在 CsAudioTuning.ClassifyImpact：先按命中物的材质名/节点名分类
                // （沙 / 木箱 / 门板 / 混凝土 / 金属 / 未知），再播 hit_wall。
                // ⚠️ 盘上**只有一条** hit_wall.wav（原版按材质分的多条弹着采样不在盘）⇒ 各类现在落同一 clip，
                //    但"分类"是真的、且逐类可在日志核对；缺口已登记 策划/差异登记.tsv。
                var matName = i < impactMaterials.Count ? impactMaterials[i] : null;
                var cls = CsAudioTuning.ClassifyImpact(matName);
                _audio.PlayAt(CsAudioTuning.HitWall, impactPoints[i]);
                _log.Info("hitwall." + cls,
                    $"弹着音 hit_wall（落点 {impactPoints[i]}）：命中材质「{matName ?? "null"}」→ 分类 {cls}" +
                    "（原版按材质分流的多条采样不在盘，见 策划/差异登记.tsv）");
            }

            // ---- 命中回传（伤害结算归 agent-03）----
            var hits = _firearm.Hits;
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                match.ReportHit(hit);
                OnLocalHit(hit, def, match);
            }

            if (hits.Count == 0)
            {
                // 未命中是高频路径（扫射时几乎每发都不中）→ 降频。
                _log.Info("shot.miss",
                    $"{def.DisplayName} 未命中（spread={spread:F2}° 弹丸={def.Pellets} 射程={def.Range:F0}m）");
            }
        }

        /// <summary>命中反馈三件套里的两件：命中标记（写快照给 HUD）+ 命中音效；血条下降由 agent-07 的目标视图负责。</summary>
        private void OnLocalHit(in CsHitInfo hit, CsWeaponDef def, ICsMatch match)
        {
            var headshot = hit.Hitbox == CsHitbox.Head;

            CsHudSnapshot.HitMarkerTime = CsConst.HitMarkerTime;
            CsHudSnapshot.HitMarkerHeadshot = headshot;

            _audio.Play(headshot ? CsCombatTuning.HitMarkerHeadshotSfx : CsCombatTuning.HitMarkerSfx);

            var victim = match.Find(hit.VictimId);
            var victimName = victim != null ? victim.Name : $"actor {hit.VictimId}";

            _log.Always($"Hit：{def.DisplayName} 命中 {victimName} 的 {hit.Hitbox}" +
                        $"（{hit.Distance:F1}m{(hit.ThroughWall ? "，穿墙" : string.Empty)}{(headshot ? "，爆头" : string.Empty)}）");
        }
    }
}
