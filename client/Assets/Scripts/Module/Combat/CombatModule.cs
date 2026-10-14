using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Audio;
using Cs16.Module.CameraRig;
using Cs16.Module.Match;
using Cs16.Module.Player;
using Cs16.Module.View;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 本地玩家的**开火/换弹/切枪/开镜**输入采集，以及射击的表现与命中回传。
    ///
    /// <para><b>弹药/射速/后坐力都不在这里扣</b>：<c>cmd.Fire = true</c> 后由比赛模拟
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

        /// <summary>与 <see cref="_drainedShots"/> **同索引**的"这一发是否装了消音器"（远端选开火采样用）。</summary>
        private readonly List<bool> _drainedShotSilenced = new List<bool>(CsCombatTuning.MaxShotsConsumedPerFrame);

        private ICsMatch _match;
        private FirstPersonCamera _view;

        /// <summary>
        /// 当前活着的战斗模块（装配点唯一）。供 <c>ActorView</c> 把"别的角色开火了"这条信号送进来做
        /// 第三人称枪口表现 —— 射击队列只带武器 id、不带射手（<see cref="ICsMatch.ConsumeShotFired"/>），
        /// 射手只能由视图侧给。取不到就是 null，取用方自己判空。
        /// </summary>
        public static CombatModule Instance { get; private set; }

        // ---- 本帧输入快照（必须在模拟 Tick 之前取）----
        private bool _fireRequested;
        private float _preNextFireTime;
        private string _preWeapon;

        /// <summary>
        /// 差异 #68：模拟的"连发自动补发"计数在本帧开始前的值（判"本帧这发是我的续发"用）。
        /// 续发不由输入触发 ⇒ 不能只看 <c>_fireRequested</c>。
        /// </summary>
        private int _preBurstAutoShots;

        private bool _autoReload = true;
        private int _unmatchedShotCount;

        /// <summary>切片K（D8）：上一次空仓击发音的时刻（秒，<c>CsClock.Now</c>）——见 <see cref="CanFire"/>。
        /// 不许直接读 <c>Time.time</c>：它要跟模拟写下的绝对时间（<c>ReloadEndTime</c> 等）比，
        /// 必须与模拟同一个时钟源（见 <c>Core/CsClock.cs</c> 的类注释）。</summary>
        private float _lastDryfireTime = -999f;

        // ---- 「上一把武器」（Q = 原版 lastinv）跟踪；纯本模块内部状态，不动任何契约 ----
        private string _lastSeenWeapon;
        private string _previousWeapon;

        /// <summary>弹着音的「原版 materials.txt 未收录该贴图」日志闸门 —— 只 Warn 一次
        /// （每发子弹打墙都会走到那条回落分支，不闸就是刷屏）。</summary>
        private readonly CsCombatTuning.OnceFlag _impactLetterMiss = new CsCombatTuning.OnceFlag();

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
            Instance = this;
            _fx.Init();
            _nades.Init(_fx);

            // 受击血迹（差异 #74）：订阅在**比赛模拟**上而不是只订阅"本地玩家的射线"——
            // 机器人的子弹也要出血（原版里谁打中谁都会出血），而且只有模拟知道"确实命中了角色"。
            if (_match != null) _match.OnBulletHit += OnBulletHit;

            _log.Always("战斗模块就绪（射线由本模块负责，伤害结算归比赛模拟）");
        }

        /// <summary>
        /// <see cref="ICsMatch.OnBulletHit"/> 的处理：一枪打在角色身上 → 命中点血雾 + 后面的面上贴血迹。
        /// </summary>
        private void OnBulletHit(CsActor victim, Vector3 point, Vector3 direction, bool headshot)
        {
            var placed = _fx.BloodImpact(point, direction, headshot);
            var who = victim != null ? victim.Name : "?";
            _log.Info("hit.blood",
                $"命中 {who}（爆头={headshot}）于 {point} → 血迹贴花 {(placed ? "已贴" : "未贴（后面没有面）")}");
        }

        /// <summary>玩家设置：空仓自动换弹。</summary>
        internal void ApplySettings(bool autoReload)
        {
            _autoReload = autoReload;
        }

        // ==================================================================
        //  测试入口（类型化；**只给离线取证驱动用**，不在真实玩家输入链路上）
        // ==================================================================
        /// <summary>
        /// **测试入口，供离线取证驱动使用**：把「本帧左键按住」这一位直接置位 ——
        /// 语义与 <see cref="FillInput"/> 里按真实鼠标写 <c>_fireRequested = true</c> 的那一行**完全一致**。
        ///
        /// <para><b>为什么必须有它</b>：离线驱动只能通过 <see cref="ICsMatch.SetLocalInput"/> 下发
        /// <c>cmd.Fire</c>，而那**只让模拟开火**（扣弹 / 推 <c>NextFireTime</c> / 记一条枪声记录）；
        /// 本模块要不要射线（枪口火焰 / 弹痕 / 命中回传）取决于它自己那个由**真实鼠标**置位的
        /// <c>_fireRequested</c>（原因见 <c>IsLocalShot</c> 的注释：不拿别人的射击记录去射线 ⇒ 防双份伤害）。
        /// 改成这个 public 方法，驱动侧的反射已删。</para>
        ///
        /// <para><b>不改变真实玩家行为</b>：本方法**只在被显式调用时生效** —— 没有任何 Update /
        /// 事件会调它，真实玩家路径仍然只由 <see cref="FillInput"/> 按鼠标置位。
        /// 调用时序由驱动保证：order −150 落在 <c>PlayerModule.Update(−200)</c> 的
        /// <see cref="FillInput"/> 覆盖**之后**、模拟 Tick（order 0）消费**之前**。</para>
        /// </summary>
        public void SetFireHeldForTest(bool held)
        {
            if (held != _fireHeldForTest)
            {
                _fireHeldForTest = held;
                _log.Info("fire.testheld", held
                    ? "测试入口 SetFireHeldForTest(true)：本帧视为按住左键（离线驱动专用）"
                    : "测试入口 SetFireHeldForTest(false)：本帧左键松开");
            }
            _fireRequested = held;
        }

        /// <summary>测试入口的上一状态（只为「状态变化时打一条日志」，不参与任何判定）。</summary>
        private bool _fireHeldForTest;

        /// <summary>
        /// 测试入口：让**下一次** <see cref="FillInput"/> 视为"按下了右键"（attack2 的按下沿）。
        ///
        /// <para><b>为什么必须有它</b>：右键的按下沿读的是真实鼠标
        /// （<c>input.GetKeyDown(GameKey.MouseRight)</c>），而**开镜音 / 切换音那一支就在 <see cref="FillInput"/> 里**
        /// （不是模拟侧）⇒ 离线驱动没有鼠标层，光有 <c>input</c> 通道的 <c>zoom</c> 位取不到这两条音。
        /// 本入口置一个一次性旗标，<see cref="FillInput"/> 消费一次即清 —— 右键是**按下沿**语义，
        /// 用"按住"型旗标会让每一帧都翻转一次。</para>
        ///
        /// <para><b>不改变真实玩家行为</b>：旗标默认 false，真实路径仍只由 <c>input.GetKeyDown</c> 决定，
        /// 没有任何 Update / 事件会调它（与 <see cref="SetFireHeldForTest"/> 同一形状）。</para>
        /// </summary>
        public void SetAttack2PressedForTest()
        {
            _attack2PressedForTest = true;
            _log.Info("attack2.testpress",
                "测试入口 SetAttack2PressedForTest()：下一次 FillInput 视为按下右键（离线驱动专用）");
        }

        /// <summary>测试入口的一次性旗标（被 <see cref="FillInput"/> 消费即清）。</summary>
        private bool _attack2PressedForTest;

        /// <summary>
        /// 测试入口：让**下一次** <see cref="FillInput"/> 视为「滚轮朝该方向滚动了一次」
        /// （&gt; 0 = 滚轮下 / 原版 <c>invnext</c>，&lt; 0 = 滚轮上 / 原版 <c>invprev</c>）。
        ///
        /// <para><b>为什么必须有它</b>：滚轮与左右键同属"本帧"语义、读的是真实设备
        /// （<c>input.GetKeyDown(GameKey.MouseScrollUp/Down)</c>），而离线驱动没有鼠标层
        /// （与 <see cref="SetAttack2PressedForTest"/> 同一形状）。本入口置一个一次性旗标，
        /// <see cref="FillInput"/> 消费一次即清 —— 用"按住"型旗标会让每一帧都切一次枪。</para>
        ///
        /// <para><b>不改变真实玩家行为</b>：旗标默认 0，真实路径仍只由 <c>input.GetKeyDown</c> 决定，
        /// 没有任何 Update / 事件会调它。</para>
        /// </summary>
        public void SetWheelCycleForTest(int direction)
        {
            _wheelCycleForTest = direction == 0 ? 0 : (direction > 0 ? 1 : -1);
            _log.Info("wheel.testscroll",
                $"测试入口 SetWheelCycleForTest({direction})：下一次 FillInput 视为滚轮滚动一次（离线驱动专用）");
        }

        /// <summary>测试入口的一次性旗标（被 <see cref="FillInput"/> 消费即清）。</summary>
        private int _wheelCycleForTest;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_match != null) _match.OnBulletHit -= OnBulletHit;
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
            _preBurstAutoShots = 0;

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

            // 必须早于模拟 Tick：这是"这一发到底是不是我打的"的判据（见类注释）。
            _preWeapon = local.ActiveWeapon;
            _preNextFireTime = local.NextFireTime;
            _preBurstAutoShots = local.BurstAutoShots;   // 差异 #68：连发续发的帧间差分

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

                // ---- 右键**按下沿** = attack2（差异 #68）：USP·M4A1 拆装消音器、Glock18·FAMAS 切连发。
                // 必须 GetKeyDown（切换型输入）：用 GetKey(按住) 会在每一帧翻转一次。
                cmd.Attack2 = input.GetKeyDown(GameKey.MouseRight);
                // 测试入口补的"这一次按下"（一次性旗标，消费即清）——供离线驱动取下面两条音的证。
                if (_attack2PressedForTest) { _attack2PressedForTest = false; cmd.Attack2 = true; }

                // ---- attack2 的**切换音**（差异 #68）：原版切换音在客户端层（`cl_dlls/client.dll` 的
                // `weapons/usp_silencer_on|off.wav`、`m4a1_silencer_on|off.wav`、`famas-burst.wav`），
                // 所以由这里播，模拟侧只翻状态位。武器没有这条音（Glock18）⇒ Attack2ToggleSound 回空串 ⇒ 静音。
                if (cmd.Attack2 && def != null && (def.CanSilence || def.CanBurst))
                {
                    var turningOn = def.CanSilence ? !local.Silenced : !local.BurstMode;
                    _audio.Play(CsWeapons.Attack2ToggleSound(def, turningOn));
                }

                // ---- 右键**开镜音**（原版只有狙击四把的 SecondaryAttack 播 `weapons/zoom.wav`；
                //      AUG / SG552 的开镜分支里没有这条调用 ⇒ 它们的 SoundZoom 是空串 ⇒ 静音）。
                if (cmd.Attack2 && def != null && def.CanZoom && def.SoundZoom != "") _audio.Play(def.SoundZoom);

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
                if (ammo.inMag <= 0 && ammo.reserve > 0 && CsClock.Now >= local.ReloadEndTime)
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

            // ---- 鼠标滚轮切枪（原版滚轮上的两条绑定：滚轮上 = `invprev`、滚轮下 = `invnext`）----
            // 滚轮只有"本帧"语义（引擎 `Runtime/Core/Input.cs` 的 GameKey.MouseScrollUp / MouseScrollDown），
            // 必须 GetKeyDown：用 GetKey(按住) 会在每一帧切一次枪。
            if (inputAllowed)
            {
                var wheel = _wheelCycleForTest;
                _wheelCycleForTest = 0;
                if (wheel == 0)
                {
                    if (input.GetKeyDown(GameKey.MouseScrollUp)) wheel = -1;
                    else if (input.GetKeyDown(GameKey.MouseScrollDown)) wheel = 1;
                }
                if (wheel != 0)
                {
                    match.CycleWeapon(wheel);
                    _log.Info("input.invcycle", wheel > 0
                        ? "滚轮下 → 下一把武器（原版 invnext）"
                        : "滚轮上 → 上一把武器（原版 invprev）");
                }
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
                // 原版 chooseteam 是**打开阵营菜单**；本工程局内没有"再开一次 TeamMenu"的流程入口，
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

            var now = CsClock.Now;

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

            // 时间基准与模拟一致：两边都经 Core/CsClock（CsMatch.Clock 的默认值 = () => CsClock.Now）。
            var left = local.FlashEndTime - CsClock.Now;
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
            //   差异 #68：连发的续发不由输入触发（松手后模拟继续补发）⇒ 用模拟的补发计数差分认领，
            //   否则续发只会"扣弹 + 播枪声"，打出伤害的那条射线永远不会发。
            var localFired = local != null &&
                (IsLocalShot(_fireRequested, _preNextFireTime, local.NextFireTime) ||
                 local.BurstAutoShots > _preBurstAutoShots);

            // ② 把本帧的记录全部取走（模拟会警告"没人消费"），再挑出属于我的那一条。
            _drainedShots.Clear();
            _drainedShotSilenced.Clear();
            while (_drainedShots.Count < CsCombatTuning.MaxShotsConsumedPerFrame &&
                   match.ConsumeShotFired(out var weaponId, out var shotSilenced))
            {
                _drainedShots.Add(weaponId);
                _drainedShotSilenced.Add(shotSilenced);
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
            var otherDetail = "";
            for (var i = 0; i < consumed; i++)
            {
                var id = _drainedShots[i];
                if (!claimed && mineWeapon != null && id == mineWeapon)
                {
                    claimed = true;     // 我的那一条由 HandleLocalShot 发声，避免重复播两次
                    continue;
                }

                var otherDef = CsWeapons.Get(id);
                // 远端开火采样按**这一发实际的消音状态**选 —— 原版把这一位随射击事件发给所有客户端
                // （wpn_m4a1.cpp:200-201 的 PLAYBACK_EVENT_FULL 末组实参），见 ICsMatch.ConsumeShotFired。
                if (otherDef != null)
                {
                    var sample = CsWeapons.FireSound(otherDef, _drainedShotSilenced[i]);
                    _audio.Play(sample);
                    otherDetail += (otherDetail.Length > 0 ? ", " : "") +
                        id + (_drainedShotSilenced[i] ? "(消音)" : "(未消音)") + "→" + sample;
                }
            }

            if (otherCount > 0)
            {
                // 高频路径 → 降频（用于排查"机器人的射击有没有被误当成我的"）。
                _log.Info("shot.others",
                    $"本帧另有 {otherCount} 条他人射击记录（只放枪声，不做射线）：{otherDetail}");
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

        /// <summary>
        /// 别的角色开火时的**世界空间**枪口表现（枪口火焰）：落点 = 射手的枪口节点
        /// （<c>ActorView.TryGetMuzzle</c>；位置逐武器来自原版 <c>models/p_*.mdl</c> 的枪口骨）。
        ///
        /// <para><b>为什么由视图侧驱动</b>：射击记录只带武器 id、不带射手
        /// （<see cref="ICsMatch.ConsumeShotFired"/>），这里拿不到"这一发是谁打的"；
        /// 而 <c>ActorView</c> 侧本来就有逐 actor 的权威开火信号（<c>CsActor.NextFireTime</c> 前推，
        /// 它也是那里播 <c>ref_shoot_*</c> 的判据）。</para>
        ///
        /// <para><b>与本地玩家不重叠</b>：本地那一发走 <see cref="HandleLocalShot"/> 的第一人称路径；
        /// <c>ActorView</c> 对自己（<c>IsLocal</c>）不调本方法。</para>
        /// </summary>
        internal void FxRemoteShot(ActorView shooter, string weaponId)
        {
            if (shooter == null || string.IsNullOrEmpty(weaponId)) return;
            var def = CsWeapons.Get(weaponId);
            if (def == null) return;
            // 原版只有"开枪"才有枪口火焰：刀 / 手雷 / C4 都没有。
            if (def.Class == CsWeaponClass.Knife || def.Class == CsWeaponClass.Grenade ||
                def.Class == CsWeaponClass.Bomb) return;

            if (!shooter.TryGetMuzzle(out var pos, out _))
            {
                _log.Warn("shot.remote.nomuzzle",
                    $"角色视图「{shooter.name}」开火时还没有枪口节点（武器模型尚未挂上），本发没有火光");
                return;
            }
            _log.Info("shot.remote.flash",
                $"角色视图「{shooter.name}」开火：武器 {weaponId} 枪口世界坐标 {pos} → 第三人称枪口火焰");
            _fx.MuzzleFlashWorld(pos, weaponId);
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
            // 名称按**消音器状态**两态取：未装档是另一支更响的采样（出处见 CsWeapons.FireSound）。
            _audio.Play(CsWeapons.FireSound(def, local.Silenced));

            // ---- 枪口火焰 / 投掷视觉 ----
            if (def.Class == CsWeaponClass.Grenade)
            {
                _nades.ThrowVisual(eye, aim, def);
            }
            else if (def.Class != CsWeaponClass.Knife && def.Class != CsWeaponClass.Bomb)
            {
                // 落点按**相机局部系**给（CsCombatTuning 的三个偏移，从视模型实测包围盒反推）
                // ⇒ 这里传相机旋转而不是 aim：world-up 叉乘算 right 在俯仰 ±90° 会退化。
                // 旋转必须取**相机**：`_view` 自身的 transform 是 rig 根、恒为单位旋转，随视角转的
                // 是它的子相机 `CsFpsCamera`（`_view.Camera`）—— 取错就会让弹壳与枪口火焰在
                // 视角 yaw≠0 时偏到世界轴方向（= 玩家看不见）。
                // weaponId 决定用哪张原版贴图（B51/M249 = 十字形，见 CombatEffects.PickMuzzleFlash）。
                var viewRotation = _view != null && _view.Camera != null
                    ? _view.Camera.transform.rotation
                    : _view.transform.rotation;
                _fx.MuzzleFlash(eye, viewRotation, weaponId);

                // 抛壳：逐武器一种原版壳模型（霰弹枪那条载体取不到 ⇒ 不抛，见 CsShellModels.KindFor）。
                // 第 4 个实参 = 原版初速里那一项 pev->velocity（跑动中开枪时壳跟着人走）。
                _fx.ShellEject(eye, viewRotation, weaponId, local.Velocity);
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
                // 命中面的材质名（= 命中 Mesh 的 sharedMaterial.name）——**弹痕族与弹着音分类共用同一个来源**：
                // 弹痕族由 CsCombatTuning.SurfaceDecals 按它查表（原版 client.dll 的 [surf+0x8C]/[surf+0x68]），
                // 弹着音分类由 CsAudioTuning.ClassifyImpact 按它分流。
                var matName = i < impactMaterials.Count ? impactMaterials[i] : null;
                _fx.BulletImpact(impactPoints[i], _firearm.ImpactNormals[i], matName);

                // 打得碎的世界几何（原版 func_breakable 木箱）：按原版扣血，health 1 ⇒ 一发即碎。
                // 命中方向取"眼睛 → 落点"，就是这一发自己的弹道方向（原版 g_vecAttackDir 的来源）。
                var impacts = _firearm.ImpactColliders;
                var breakable = i < impacts.Count ? CsBreakable.Find(impacts[i]) : null;
                if (breakable != null)
                {
                    breakable.ApplyBulletDamage(CsWeapons.BaseDamage(def, local.Silenced, local.BurstMode),
                        (impactPoints[i] - eye).normalized, impactPoints[i]);
                }

                // 弹着音的分族 = 原版口径（CsAudioTuning.ClassifyImpact）：
                //   ① 材质名在 sound/materials.txt 里 ⇒ 用它那一行的字母给族（M/V = 金属，其余 = 混凝土）；
                //   ② 查不到 ⇒ 原版的默认字母 'C' = 混凝土族（pm_shared.c:288）。
                // 盘上**只有一条** hit_wall.wav（原版按族分的多条弹着采样不在盘）⇒ 两族落同一 clip，
                //   但"族"是真的、且逐族可在日志核对；缺口已登记 策划/差异登记.tsv #56。
                var cls = CsAudioTuning.ClassifyImpact(matName);
                // 闸门与 CombatEffects 的「命中面材质不在 flags 表里」同款（CsCombatTuning.OnceFlag，
                // 纯逻辑、可离线断言）：每发子弹都会走到这个分支，不闸就是刷屏。
                if (!CsAudioTuning.TryLookupImpactLetter(matName, out var impactLetter) && _impactLetterMiss.Take())
                {
                    _log.Warn("hitwall.material.miss",
                        $"原版 sound/materials.txt 未收录命中贴图「{matName ?? "null"}」—— " +
                        "按原版落默认材质字母 'C' / 混凝土族（pm_shared.c:288）");
                }
                _audio.PlayAt(CsAudioTuning.HitWall, impactPoints[i]);
                _log.Info("hitwall." + cls,
                    $"弹着音 hit_wall（落点 {impactPoints[i]}）：命中材质「{matName ?? "null"}」→ 分类 {cls}" +
                    (impactLetter != '\0'
                        ? $"（原版材质字母 {impactLetter}）"
                        : $"（materials.txt 未收录该贴图 → 按原版落默认 {CsAudioTuning.ImpactDefaultLetter} / 混凝土族）") +
                    "（原版按族分的多条采样不在盘，见 策划/差异登记.tsv）");
            }

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
