using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Combat;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.CameraRig
{
    /// <summary>
    /// 第一人称相机 —— **业务外壳（MonoBehaviour）**：装配自建相机、喂输入 / 眼位给引擎的
    /// 第一人称 rig，并处理**引擎 rig 不管的那几件事**（第三人称观战机位、模型显隐、外来相机 / 音频监听器）。
    ///
    /// <para><b>视角数学一律委托给引擎 rig</b> <see cref="CloverEngine.CloverFirstPersonCamera"/>
    /// （<c>Runtime/Presentation/CloverFirstPersonCamera.cs</c>；它的数学层是从本项目逐字搬过去的，
    /// 见其类注释的出处标注）：</para>
    /// <list type="bullet">
    /// <item><b>yaw/pitch 累加与夹取</b>：引擎 <c>LookAccumulator</c> + <c>PitchLimit</c>，本组件不再自己夹；</item>
    /// <item><b>后坐力表现跟随</b>：<c>SetRecoil(pitch, yaw)</c> —— 只喂**模拟的权威值**
    /// （<c>CsActor.RecoilPitch/RecoilYaw</c>），上跳/回正两个时间常数在引擎里挑（不再留第二份跟随）；</item>
    /// <item><b>受击摇晃</b>：<c>AddShake(amp, dur)</c> + 注入的 <see cref="CloverEngine.Rng"/>
    /// （<c>CsRng.Stream(CsRngStream.CameraShake)</c>）—— 本组件不再用裸 <c>UnityEngine.Random</c>；
    /// <b>视点晃动</b>：引擎件 <c>ViewBob</c> 的输出经 <c>ViewOffset</c>/<c>ViewRoll</c> 塞进去；</item>
    /// <item><b>水平 FOV + 宽高比换算</b>：<c>FovX</c>/<c>FovSmoothTau</c> 交给引擎
    /// （<see cref="CameraMath.FovYFromFovX"/> 在引擎里逐帧算垂直 <c>fieldOfView</c>）；</item>
    /// <item><b>位姿下发</b>：<c>Tick(dt)</c> 写相机 Transform 与 <c>fieldOfView</c>
    /// （<c>rotation = Euler(-Pitch, Yaw, ViewRoll+ShakeRoll)</c> 这一行也在引擎里）。</item>
    /// </list>
    ///
    /// <para><b>本组件自己保留的（引擎 rig 不提供的）</b>：</para>
    /// <list type="number">
    /// <item><b>相机本体</b>：舞台场景不保证有相机，自建一台常驻相机（比赛未运行时关闭）
    /// 可以彻底消除"进图后黑屏 / 和菜单相机抢 <c>Camera.main</c>"这两类问题。rig 用
    /// <c>Bind(camera)</c> 显式绑它（不走 <c>Game.Camera.Main</c>：那台可能不是我们这台）。</item>
    /// <item><b>眼位</b>：眼位来自**逻辑**（<c>CsActor.Position + 眼高</c>，没有 Transform），
    /// 故本组件维护一个隐藏的眼位锚点 Transform（<c>CsFpsEyeAnchor</c>）并只平滑**眼高**那一个标量
    /// （站↔蹲、上下台阶不跳变，瞬移直接吸附）；引擎 <c>EyeSmoothTau</c> 是**整点**平滑（会让机位
    /// 在水平方向落后于玩家）故置 0。</item>
    /// <item><b>输入映射</b>：视角输入由玩家模块采集（<c>PlayerMotor.Yaw/Pitch</c>），
    /// 本组件每帧 <c>SetView(motor.Yaw, motor.Pitch)</c> 喂给 rig；rig 自己的鼠标读取**关掉**
    /// （<c>SetControlEnabled(false)</c>）—— 两处读鼠标会把灵敏度算两遍。这是项目专有的输入映射。</item>
    /// <item><b>四种观战机位</b>：第三人称追尾（<see cref="ResolveChaseDistance"/>，原版
    /// <c>V_GetChaseOrigin</c>，view.cpp:894-1050；档位 1 锁定追尾 / 档位 2 自由追尾共用位置算法，
    /// 角度来源见 <c>CsSpectator.TryChaseCameraAngles</c>）、目标第一人称（原版 <c>V_GetInEyePos</c>，
    /// view.cpp:1289-1321；相机落在被观察者眼位、朝向随他）、自由飞行（原版 <c>OBS_ROAMING</c>，
    /// view.cpp:1538-1540：相机在**观察者自己的漫游原点**、朝向 = 他自己的鼠标角；原点推进 =
    /// <c>CsSpectatorRoam.Step</c>）—— 由观战档位 <c>CsSpectator.Mode</c> 选（有机位的档位见
    /// <c>CsSpectator.ImplementedModes</c>）；
    /// 另有模型显隐、外来主相机 / 音频监听器收编、FOV 与机位的自证日志。</item>
    /// </list>
    ///
    /// <para><b>两条硬约定（不变）</b>：</para>
    /// <list type="number">
    /// <item><b>不自己累加后坐力</b>：权威值在比赛模拟里（<c>CsActor.RecoilPitch/RecoilYaw</c>，
    /// 模拟负责累加与回复），本组件只把权威值**喂**给引擎 rig 做表现；</item>
    /// <item><see cref="AimDirection"/> 来自引擎 rig（含后坐力）—— 因为后坐力在 CS 里就是"准星被抬起来"，
    /// 子弹必须跟着抬起来的方向走（射线由 <c>CombatModule</c> 用同一个方向发出）。</item>
    /// </list>
    /// </summary>
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        private const string Tag = "Camera";
        private const string CameraObjectName = "CsFpsCamera";

        /// <summary>眼位锚点对象名（隐藏空物体；见 <see cref="_eyeAnchor"/>）。</summary>
        private const string EyeAnchorObjectName = "CsFpsEyeAnchor";

        /// <summary>近裁剪面（米）：第一人称离墙很近，必须足够小。</summary>
        private const float NearClipPlane = 0.05f;

        /// <summary>远裁剪面（米）：de_dust2 对角约 90m，留足余量。</summary>
        private const float FarClipPlane = 1000f;

        /// <summary>自检（相机 / 音频监听 / 隐藏自身模型）间隔（秒）。</summary>
        private const float SelfCheckInterval = 1f;

        private const float MinBaseFov = 30f;
        private const float MaxBaseFov = 120f;

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        private readonly HashSet<Camera> _handledForeignCameras = new HashSet<Camera>();

        /// <summary>
        /// 当前被本组件藏起来的 actor（<see cref="_hasHiddenBody"/> = false 时无意义）。
        /// 观战切换时靠它知道"上一个目标是谁"，才能把上一批渲染还回去。
        /// </summary>
        private long _hiddenBodyActorId;
        private bool _hasHiddenBody;

        /// <summary>
        /// 本组件**亲手**关掉的渲染 —— 只装**当前这一个 actor** 的（去重，避免每秒重复关）。
        /// </summary>
        private readonly HashSet<Renderer> _hiddenBodyRenderers = new HashSet<Renderer>();

        private Camera _camera;
        private ICsMatch _match;
        private PlayerMotor _motor;

        /// <summary>
        /// **视角数学的唯一实现**（引擎件）：yaw/pitch 累加与夹取、后坐力表现跟随、受击摇晃（注入 Rng）、
        /// 水平 FOV 平滑 + 宽高比换算、位姿下发。本组件不再自己算这些
        /// （见类注释与 <c>Runtime/Presentation/CloverFirstPersonCamera.cs</c>）。
        /// </summary>
        private CloverFirstPersonCamera _rig;

        /// <summary>
        /// 眼位锚点（隐藏的空物体）：眼位来自**逻辑**（<c>CsActor.Position</c>，角色模拟没有 Transform），
        /// 所以每帧把本锚点搬到目标脚下位置，<c>EyeOffset</c> 再给"脚 → 眼"的高度
        /// <c>target.Position + (0, _eyeHeight, 0)</c> 逐字等价。
        /// </summary>
        private Transform _eyeAnchor;

        /// <summary>
        /// 视点晃动 = **引擎件** <see cref="CloverEngine.ViewBob"/>（纯逻辑类，不再是 MonoBehaviour）。
        /// 由 <c>PlayerModule</c> 用项目自己的 <see cref="CloverEngine.ViewBobConfig"/> 构造后经
        /// <see cref="Init"/> 注入（7 个数值仍取项目原常量，一个都没变）。
        /// </summary>
        private ViewBob _bob;

        private bool _subscribedDamage;

        /// <summary>玩家设置里的**水平** FOV（原版口径，见 <see cref="CsConst.DefaultFov"/>）——
        /// 每帧转交给引擎 rig 的 <c>FovX</c>，垂直换算 / 平滑都在引擎里。</summary>
        private float _baseFov = CsConst.DefaultFov;

        /// <summary>本帧眼高（米，脚面之上）：只平滑这一个标量（见类注释"本组件自己保留的"第 2 条）。</summary>
        private float _eyeHeight;
        private Vector3 _lastActorPosition;
        private bool _hasLastPosition;

        private long _lastTargetId;
        private float _zoomHeldTime;

        /// <summary>最近一次受击的归一化幅度（0~1）：<c>OnDamaged</c> 用它折算摇晃幅度交给引擎 rig。</summary>
        private float _shakeMagnitude;

        private float _selfCheckTimer;

        // ---- 观战机位状态（第三人称追尾 / 目标第一人称）----
        /// <summary>当前平滑后的机位距离（米）。进第三人称时按原版重置为 <see cref="CsConst.ChaseMinDistance"/>。</summary>
        private float _chaseDistance = CsConst.ChaseDistance;
        /// <summary>上一帧是否在观战（用于"进入观战时重置机位距离"）。</summary>
        private bool _lastSpectating;
        /// <summary>本帧射线命中点距眼位的距离（米）；&lt;0 = 本帧没打到墙。仅用于日志。</summary>
        private float _chaseHitDistance = -1f;
        /// <summary>上一帧的观战档位（用于"档位变了就当场换机位 + 换模型显隐"）；非观战时记默认档。</summary>
        private CsSpectatorMode _lastSpectateMode = CsSpectatorMode.ChaseFree;

        // ---- 观战·自由飞行（原版 OBS_ROAMING = 档位 3）----
        /// <summary>
        /// 自由飞行的位置与速度。解算在 <see cref="CsSpectatorRoam.Step"/>（Core 里的纯函数，逐语句
        /// 对应原版 <c>PM_SpectatorMove</c>）；本组件只负责"喂输入、喂给解算、把结果写进相机"。
        /// </summary>
        private CsSpectatorRoam.State _roam;

        /// <summary>
        /// 进入自由飞行时要**对齐到当前机位**（否则换档那一帧会瞬移到 (0,0,0)）。原版对应
        /// <c>pm_shared.c:1503-1512</c> 的 <c>iJumpSpectator</c> 分支（把原点与角设成 vJumpOrigin /
        /// vJumpAngles、速度清零）与 <c>hud_spectator.cpp:834-842</c> 的"跳进当前机位"。
        /// </summary>
        private bool _roamNeedsStart;

        /// <summary>
        /// 控制台面板的名字（自由飞行据此停手，见 <see cref="ReadRoamMoveInput"/>）。
        /// <para><b>为什么用名字而不是类型</b>：本组件在 <c>Cs16.Module</c> 层，分层铁律不让模块引用
        /// <c>Cs16.UI</c>（控制台是 UI 层的面板）；面板名与预制体名同源，
        /// 与 <c>PlayerModule</c> 的 <c>NonBlockingPanels</c> 同一套写法。</para>
        /// </summary>
        private const string ConsolePanelName = "ConsolePanel";

        /// <summary>
        /// 当前打开中的面板名（只有**控制台**会挡住自由飞行，见 <see cref="ReadRoamMoveInput"/>）。
        /// </summary>
        private readonly HashSet<string> _openPanels = new HashSet<string>();

        private bool _subscribedPanels;

        /// <summary><c>Game.Input</c> 拿不到时只报一次（同 <c>PlayerMotor</c> 的口径）。</summary>
        private bool _warnedRoamInput;

        // ---- FOV / 机位诊断日志的"变了才打"缓存 ----
        private float _loggedFovY = float.NaN;
        private int _loggedWidth;
        private int _loggedHeight;

        /// <summary>本帧射线起点（眼睛位置，**不含** bob：子弹不跟着脚步摆）。
        /// 值来自引擎 rig 的 <c>EyePosition</c>（未在比赛中时为上一次的值 / 初始零）。</summary>
        public Vector3 EyePosition => _eyePosition;

        /// <summary>
        /// 本帧视线方向（含后坐力表现，与相机朝向一致）：
        /// <c>(cos(pitch)*sin(yaw), sin(pitch), cos(pitch)*cos(yaw))</c> —— 与比赛模拟的
        /// <c>CsInventory.AimDirection</c> 同口径（yaw 0 = +Z，pitch + = 抬头）。
        /// 值来自引擎 rig 的 <c>AimDirection</c>（未在比赛中时保持 <c>Vector3.forward</c>）。
        /// </summary>
        public Vector3 AimDirection => _aimDirection;

        private Vector3 _eyePosition;
        private Vector3 _aimDirection = Vector3.forward;

        /// <summary>开镜是否已"稳定"（未稳定 = 不享受开镜精度加成，即 quickscope 惩罚）。</summary>
        public bool ScopeSettled =>
            _match == null || !_match.IsZoomed || _zoomHeldTime >= CsCombatTuning.ScopeSettleTime;

        /// <summary>相机是否就绪（没就绪时 <c>CombatModule</c> 不会做射线）。</summary>
        public bool Ready => _camera != null;

        /// <summary>当前驱动中的相机（排障用）。</summary>
        public Camera Camera => _camera;

        // ==================================================================
        //  装配
        // ==================================================================
        internal void Init(ICsMatch match, PlayerMotor motor, ViewBob bob, int baseFovDegrees)
        {
            _match = match;
            _motor = motor;
            _bob = bob;

            _baseFov = Mathf.Clamp(baseFovDegrees > 0 ? baseFovDegrees : CsConst.DefaultFov, MinBaseFov, MaxBaseFov);

            EnsureCamera();
            EnsureAudioListener();
            HookDamage();
            HookPanels();
            EnsureRig();

            // 自由飞行：进入档位 3 时按"跳进当前机位"处理（见 _roamNeedsStart）。
            _roamNeedsStart = true;

            // 让第一帧 PrepareView 立刻做一次自检（启用相机 / 找监听器 / 关掉别的主相机）。
            _selfCheckTimer = SelfCheckInterval;

            _log.Always($"第一人称相机就绪：camera={(_camera != null ? _camera.name : "null")} " +
                        $"水平FOV={_baseFov:F0}（开镜 {CsConst.ZoomFov:F0}；垂直换算与平滑由引擎 rig 逐帧做）" +
                        $"near={NearClipPlane} far={FarClipPlane}；第三人称机位 {CsConst.ChaseDistance:F4}~{CsConst.ChaseMinDistance:F4} m" +
                        $"；视角 rig={(_rig != null ? "CloverFirstPersonCamera" : "缺失")}");
        }

        /// <summary>玩家设置里的 FOV 变了（Options 面板）时同步（只改引擎 rig 的 <c>FovX</c>；平滑在引擎里）。</summary>
        internal void SetBaseFov(int fovDegrees)
        {
            var clamped = Mathf.Clamp(fovDegrees, (int)MinBaseFov, (int)MaxBaseFov);
            if (Mathf.Approximately(_baseFov, clamped)) return;
            _baseFov = clamped;
            if (_rig != null) _rig.FovX = _baseFov;
            _log.Always($"FOV 设置更新：{_baseFov:F0}");
        }

        /// <summary>
        /// 建/绑**引擎第一人称 rig**（<see cref="CloverFirstPersonCamera"/>）—— 视角数学的唯一实现。
        /// 数值全部来自项目的数值表（引擎不内置手感值）：俯仰限位 / 后坐力两个时间常数 / 摇晃横滚比例 /
        /// 开镜 FOV 过渡常数见 <see cref="CsCombatTuning"/>，FOV 见 <see cref="CsConst"/>。
        /// </summary>
        private void EnsureRig()
        {
            if (_rig == null)
            {
                var anchorGo = new GameObject(EyeAnchorObjectName);
                anchorGo.hideFlags = HideFlags.HideInHierarchy;
                Object.DontDestroyOnLoad(anchorGo);
                _eyeAnchor = anchorGo.transform;

                _rig = new CloverFirstPersonCamera
                {
                    EyeAnchor = _eyeAnchor,
                    // 整点眼位平滑会让机位在**水平方向**落后于玩家（锚点随玩家移动）⇒ 置 0：
                    // 本组件只平滑"眼高"这一个标量（见 _eyeHeight 的那段注释），与既有实现行为一致。
                    EyeSmoothTau = 0f,
                    PitchLimit = CsCombatTuning.PitchLimit,
                    RecoilRiseTau = CsCombatTuning.RecoilRiseTau,
                    RecoilFallTau = CsCombatTuning.RecoilFallTau,
                    // 摇晃三轴同幅（pitch/yaw/roll 各取 Range(-k,k)）⇒ 横滚比例 1。
                    ShakeRollScale = 1f,
                    // 随机器**注入**（引擎禁用全局静态随机器）：走项目唯一的随机入口，按用途分一条独立子流。
                    ShakeRng = CsRng.Stream(CsRngStream.CameraShake),
                    FovSmoothTau = CsCombatTuning.ZoomTransitionTau,
                    FovX = _baseFov,
                };

                // rig 不许自己读鼠标：视角输入由 PlayerMotor 采集（项目专有的输入映射，见类注释），
                //    两处读鼠标会把灵敏度算两遍。关掉输入后 rig 仍照常由 Tick 驱动机位。
                _rig.SetControlEnabled(false);
            }

            if (_camera != null && !_rig.IsBound) _rig.Bind(_camera);
        }

        private void OnDestroy()
        {
            UnhookDamage();
        }

        private void HookDamage()
        {
            if (_subscribedDamage || _match == null) return;
            _match.OnDamaged += OnDamaged;
            _subscribedDamage = true;
        }

        private void UnhookDamage()
        {
            if (!_subscribedDamage || _match == null) return;
            _match.OnDamaged -= OnDamaged;
            _subscribedDamage = false;
        }

        /// <summary>
        /// 订阅面板开关（引擎 <c>Game.UI</c>）：**控制台打开时不推进自由飞行** —— 控制台里打的正是
        /// <c>spec_mode</c> / <c>spec_player</c> 这类含 WASD 的命令，那些键是在打字，不是要飞
        /// （控制台的输入框是 uGUI <c>InputField</c>，键盘照样被它收走，所以必须由这里闸一道）。
        ///
        /// <para>只闸控制台：其余面板不抢键盘，暂停族由 <c>ICsMatch.IsPaused</c> 挡（见
        /// <see cref="ReadRoamMoveInput"/>）。引擎的面板事件**只有订阅、没有退订**（<c>Game.UI</c>），
        /// 因此本组件只订阅一次，处理函数对"组件已销毁"做守卫（与 <c>PlayerModule</c> 同形）。</para>
        /// </summary>
        private void HookPanels()
        {
            if (_subscribedPanels) return;
            var ui = Game.UI;
            if (ui == null)
            {
                _log.Warn("ui.missing",
                    "Game.UI 为 null（未 Launch？）：自由飞行没有控制台闸门，在控制台里按 WASD 会一起飞");
                return;
            }

            ui.OnPanelOpened(OnPanelOpened);
            ui.OnPanelClosed(OnPanelClosed);
            _subscribedPanels = true;
        }

        /// <summary>面板打开 ⇒ 记名（自由飞行据此停手；见 <see cref="HookPanels"/>）。</summary>
        private void OnPanelOpened(string panelName)
        {
            if (this == null || string.IsNullOrEmpty(panelName)) return;
            _openPanels.Add(panelName);
        }

        private void OnPanelClosed(string panelName)
        {
            if (this == null || string.IsNullOrEmpty(panelName)) return;
            _openPanels.Remove(panelName);
        }

        /// <summary>被打时晃一下（只处理"受击者就是自己"的情况，队友/敌人挨打不动我的镜头）。</summary>
        private void OnDamaged(CsActor victim, int damage, bool headshot, bool lethal)
        {
            var local = _match != null ? _match.LocalPlayer : null;
            if (local == null || victim == null || victim.Id != local.Id) return;

            _shakeMagnitude = Mathf.Clamp01(damage / CsCombatTuning.DamageShakeFullDamage);
            // 摇晃**委托给引擎 rig**：幅度 = 满幅 × 归一化伤害、时长取项目常数；随机方向由注入的
            // Rng 抽一次（`AddShake` 的内部机制：一次方向 + 幅度线性衰减到 0）。
            // 出处对照：引擎 CloverFirstPersonCamera.AddShake 的注释逐条对应原版 :341-349 的三轴随机。
            var accepted = _rig != null &&
                           _rig.AddShake(CsCombatTuning.DamageShakeMaxDeg * _shakeMagnitude,
                                         CsCombatTuning.DamageShakeDuration);
            _log.Info("shake", $"自己受击：{damage} 点（{(headshot ? "爆头" : "普通")}{(lethal ? "·致死" : string.Empty)}），" +
                               $"镜头晃动幅度={CsCombatTuning.DamageShakeMaxDeg * _shakeMagnitude:F3}°（引擎 rig AddShake={accepted}）");
        }

        // ==================================================================
        //  每帧：喂输入 → 引擎 rig 推进并下发位姿 →（仅观战）覆盖为第三人称机位
        // ==================================================================
        /// <summary>
        /// 本帧：算好眼位 / 输入 / 后坐力 / 视点晃动 / FOV，喂给引擎 rig，由 rig 推进一帧并**下发第一人称位姿**。
        ///
        /// <para>与 <see cref="ApplyToTransform"/> 的分工（调用方 <c>PlayerModule</c> 在两者之间做射击射线）：
        /// 第一人称位姿在**本方法内**已经下发（引擎 <c>Tick</c> 的职责）；只有第三人称观战机位要等射线之后再覆盖一次。
        /// "射线方向必须与画面上准星所指一致"这条约定不受影响 —— 朝向由 <see cref="AimDirection"/> 给出（引擎算的）。</para>
        /// </summary>
        internal void PrepareView(float dt)
        {
            _hasPendingView = false;

            if (_camera == null) EnsureCamera();
            if (_camera == null)
            {
                _log.Warn("camera.missing", "相机不可用（创建失败？），第一人称视角不会更新");
                return;
            }

            EnsureRig();
            if (_rig == null)
            {
                // 非预期分支：rig 建不出来就没人算视角 —— 必须留痕，不许静默黑屏。
                _log.Warn("rig.missing", "第一人称 rig 不可用（引擎 CloverFirstPersonCamera 未建成），本帧视角不更新");
                return;
            }

            var running = _match != null && _match.IsRunning;
            TickSelfCheck(dt, running);

            if (!running)
            {
                // 比赛没开始（主菜单 / 读条 / 已回主菜单）：不推进 rig、不动相机。
                // 位姿表现状态清干净，免得下一局开局还挂着上一局的后坐力 / 摇晃。
                _rig.ClearRecoil();
                _rig.ClearShake();
                _aimDirection = Vector3.forward;
                return;
            }

            var local = _match.LocalPlayer;
            var target = local;
            var spectating = false;

            if (_match.IsSpectating)
            {
                var spec = _match.SpectateTarget;
                if (spec != null)
                {
                    target = spec;
                    spectating = true;
                }
                else if (local != null)
                {
                    _log.Info("spectate.none", "处于观战但拿不到观战目标（可能全员阵亡/回合结算中），相机保持在本地玩家位置");
                }
            }

            if (target == null)
            {
                _log.Info("target.missing", "拿不到本地玩家 actor（比赛刚开局？），本帧相机不更新");
                return;
            }

            // 观战档位（控制面）决定机位：4 = 原版 OBS_IN_EYE（目标第一人称）、2 = 原版 OBS_CHASE_FREE（追尾）、
            // 3 = 原版 OBS_ROAMING（观察者**自己**的自由飞行）。非观战永远是自己第一人称。
            var inEye = spectating && CsSpectator.Mode == CsSpectatorMode.FirstPerson;

            // 自由飞行**不要求有观战目标**（原版 SetModes 里 OBS_ROAMING 是唯一"没有 g_iUser2 也放行"的档位，
            // 见 hud_spectator.cpp:814；它的原点就是观察者自己、不看别人）⇒ 判据只取 IsSpectating。
            // 其余两档都要目标：`spectating` = "相机在看**别人**"，自由飞行时它是 false 也不影响本档。
            var freeLook = _match.IsSpectating && CsSpectator.Mode == CsSpectatorMode.FreeLook;
            var observeCamera = spectating || freeLook;

            var targetChanged = _lastTargetId != target.Id;
            var spectateModeChanged = observeCamera && _lastSpectateMode != CsSpectator.Mode;
            _lastSpectateMode = observeCamera ? CsSpectator.Mode : CsSpectatorMode.ChaseFree;

            if (targetChanged)
            {
                _lastTargetId = target.Id;
                _rig.ClearRecoil();      // 换目标：后坐力表现立即清零（引擎 rig 持有该状态）
                _hasLastPosition = false;
                _bob?.Reset();
                // 观战切换：把自由视角先对齐到被观察者的朝向，避免"瞬间背对"。
                if (spectating && _motor != null) _motor.ForceLook(target.Yaw, target.Pitch);
                var where = inEye ? "观战·目标第一人称"
                    : freeLook ? "观战·自由飞行"
                    : spectating ? "观战·第三人称"
                    : "自己·第一人称";
                _log.Always($"相机跟随目标切换 → {target.Name}（id={target.Id}，{where}）");
            }

            if (spectateModeChanged)
            {
                if (freeLook)
                {
                    // 进入自由飞行：**对齐到当前机位**（原版 iJumpSpectator 语义，pm_shared.c:1503-1512）。
                    StartRoamFrom(_camera.transform.position);
                    _log.Always($"观战机位 → 自由飞行（原版 Spec_Mode3 / OBS_ROAMING）：相机在**观察者自己**的漫游原点、" +
                                $"朝向 = 自己的鼠标角（view.cpp:1538-1540）；起点=({_roam.Origin.x:F3}, {_roam.Origin.y:F3}, " +
                                $"{_roam.Origin.z:F3}) m、速度清零；推进 = PM_SpectatorMove（pm_shared.c:1490-1572，" +
                                $"无重力 / 无碰撞 / 无边界），速度上限 sv_spectatormaxspeed {CsConst.SpecRoamMaxSpeed:F4} m/s");
                }
                else if (inEye)
                {
                    _log.Always($"观战机位 → 目标第一人称（原版 Spec_Mode4 / OBS_IN_EYE）：相机落在 {target.Name} 的眼位、" +
                                "朝向随他（view.cpp:1289-1321 V_GetInEyePos）");
                }
                else
                {
                    if (!CsSpectator.IsImplemented(CsSpectator.Mode))
                    {
                        // 非预期分支：档位与机位不匹配（唯一写入入口 CsSpectator.TrySetMode 会拒掉未实现档位，
                        // 走到这里说明状态是被别处直接改的）—— 留痕，本帧按追尾处理，不回写档位。
                        _log.Warn("spec.mode.mismatch",
                            $"观战档位 {CsSpectator.ModeName(CsSpectator.Mode)}（{(int)CsSpectator.Mode}）没有机位实现" +
                            "（CsSpectator.IsImplemented=false），本帧按第三人称追尾处理");
                    }
                    var locked = CsSpectator.Mode == CsSpectatorMode.ChaseLocked;
                    _log.Always($"观战机位 → 第三人称追尾（原版 Spec_Mode{(int)CsSpectator.Mode} / " +
                                (locked ? "OBS_CHASE_LOCKED" : "OBS_CHASE_FREE") + $"）：相机退到 {target.Name} 身后、" +
                                $"距离 cl_chasedist {CsConst.ChaseDistance:F4} m（view.cpp:894-1050 V_GetChaseOrigin）、" +
                                (locked
                                    ? "角度锁到目标的视角（原版给 V_GetChasePos 传 NULL 鼠标角 ⇒ 取 ent->angles，view.cpp:1532 + :1264-1268）"
                                    : "角度用观察者自己的视角（原版传 v_cl_angles，view.cpp:1535 + :1269-1270）"));
                }
            }

            // 模型显隐（换目标或换观战档位都要**同帧**改，不然最多 1 秒的自检间隔里会看到不该看的模型）：
            // · 自己第一人称 —— 藏掉自己的身体，否则镜头被自己的后脑勺糊满；
            // · 第三人称追尾 / 自由飞行 —— **必须把被观察者的模型露出来**（相机在他身外，藏了就看不到人），
            //   还把之前藏过的还回去；
            // · 目标第一人称 —— 相机就在被观察者的眼睛上，藏掉他自己的模型（同原版 OBS_IN_EYE 看不到自己的身子）。
            if (targetChanged || spectateModeChanged)
            {
                if (observeCamera)
                {
                    if (inEye) ApplyHiddenBody(target, true);
                    else RestoreHiddenBody();
                }
                else
                {
                    ApplyHiddenBody(target, false);
                }
            }

            // 进入 / 离开第三人称：按原版 CAM_ToThirdPerson（in_camera.cpp:446-456）把机位距离重置为
            // CAM_MIN_DIST（30 unit = 0.762 m），再让收敛公式自己长到 cl_chasedist。
            if (spectating != _lastSpectating)
            {
                _lastSpectating = spectating;
                _chaseDistance = CsConst.ChaseMinDistance;
                _chaseHitDistance = -1f;
                // 观战开始时若已在自由飞行档位：同样要对齐到当前机位（不然会从 (0,0,0) 起飞）。
                if (freeLook) _roamNeedsStart = true;
            }

            if (freeLook)
            {
                // ---- 自由飞行：推进**观察者自己**的漫游原点，眼位锚点落在相机自己身上 ----
                if (_roamNeedsStart) StartRoamFrom(_camera.transform.position);
                _roam = AdvanceRoam(dt, _motor != null ? _motor.Yaw : target.Yaw,
                                        _motor != null ? _motor.Pitch : target.Pitch);

                // 回到别的档位时眼高直接吸附（不拿自由飞行的位置去平滑）。
                _hasLastPosition = false;
                _eyeAnchor.position = _roam.Origin;
                _rig.EyeOffset = Vector3.zero;   // ⇒ rig.EyePosition = 漫游原点 = 本帧的相机位置
            }
            else
            {
                // ---- 眼高（站/蹲与台阶做平滑；瞬移直接吸附）----
                // 只平滑"眼高"这一个标量：引擎 rig 的 EyeSmoothTau 是**整点**平滑，会让机位在水平方向
                // 落后于玩家（锚点跟着玩家跑），所以那边置 0、由这里承担平滑（与既有实现行为一致）。
                var eyeTarget = target.EyeHeight;
                if (!_hasLastPosition || Vector3.Distance(target.Position, _lastActorPosition) > CsCombatTuning.TeleportSnapDistance)
                {
                    _eyeHeight = eyeTarget;
                }
                else
                {
                    _eyeHeight = CameraMath.Follow(_eyeHeight, eyeTarget, dt, CsCombatTuning.EyeHeightSmoothTau);
                }

                _lastActorPosition = target.Position;
                _hasLastPosition = true;

                // 眼位锚点搬到目标脚下，"脚 → 眼"的高度交给 rig 的 EyeOffset ⇒
                // rig.EyePosition = 锚点位置 + EyeOffset = target.Position + (0, _eyeHeight, 0)（与既有实现等价）。
                _eyeAnchor.position = target.Position;
                _rig.EyeOffset = new Vector3(0f, _eyeHeight, 0f);
            }

            // ---- 喂 rig ①：视角（**项目专有的输入映射** —— 权威 yaw/pitch 由 PlayerMotor 采集）----
            // 目标第一人称（OBS_IN_EYE）是唯一例外：视角取**被观察者的**朝向
            //（原版 V_GetInEyePos 里 VectorCopy(ent->angles, angles)，view.cpp:1306；只有 chase 档位才用
            // 观察者自己的鼠标角 v_cl_angles，view.cpp:1535）。
            _rig.SetView(inEye ? target.Yaw : (_motor != null ? _motor.Yaw : target.Yaw),
                         inEye ? target.Pitch : (_motor != null ? _motor.Pitch : target.Pitch));

            // ---- 喂 rig ②：后坐力（只喂**模拟的权威值**，跟随 / 上跳回落时间常数在引擎里）----
            if (observeCamera)
            {
                _rig.ClearRecoil();   // 观战时没有自己的后坐力（权威值属于被观察者）
            }
            else
            {
                _rig.SetRecoil(target.RecoilPitch * CsCombatTuning.RecoilViewScale,
                               target.RecoilYaw * CsCombatTuning.RecoilViewScale);
            }

            // ---- 喂 rig ③：开镜 FOV（水平口径；垂直换算与平滑在引擎里）----
            var zoomed = _match.IsZoomed;
            var scopeLevel = zoomed ? _match.ScopeLevel : 0;
            _zoomHeldTime = zoomed ? _zoomHeldTime + dt : 0f;
            // 逐档 FOV：第 1 档 40°，第 2 档 AWP 10° / 其余（Scout·SG550·G3SG1）15°
            // （出处 策划/手感参数对照.md §5.2，取值走 CsConst.ScopeFovFor）。
            _rig.FovX = zoomed ? CsConst.ScopeFovFor(target.ActiveDef, scopeLevel) : _baseFov;

            // ---- 喂 rig ④：视点晃动（引擎件 ViewBob 的输出缝）----
            var speedXZ = new Vector2(target.Velocity.x, target.Velocity.z).magnitude;
            if (_bob != null) _bob.Tick(dt, observeCamera ? 0f : speedXZ, target.OnGround);
            _rig.ViewOffset = _bob != null ? _bob.Offset : Vector3.zero;
            _rig.ViewRoll = _bob != null ? _bob.Roll : 0f;

            // ---- 引擎 rig 推进一帧：后坐力跟随 / 摇晃衰减 / FOV 平滑 / 位姿 + fieldOfView 下发 ----
            _rig.Tick(dt);
            _eyePosition = _rig.EyePosition;
            _aimDirection = _rig.AimDirection;

            if (observeCamera)
            {
                if (freeLook)
                {
                    // ---- 自由飞行（原版 OBS_ROAMING，view.cpp:1538-1540）----
                    // 机位 = 观察者**自己**的漫游原点（AdvanceRoam 推进的 _roam.Origin）；朝向 = 他自己的
                    // 鼠标角（rig 这一帧的 yaw/pitch 就是 motor 喂进去的值）。原版那两行就是
                    // `VectorCopy(v_cl_angles, v_angles); VectorCopy(v_sim_org, v_origin);`。
                    _pendingRotation = Quaternion.Euler(-_rig.Pitch, _rig.Yaw, 0f);
                    _pendingPosition = _roam.Origin;
                    _hasPendingView = true;
                }
                else if (inEye)
                {
                    // ---- 目标第一人称（原版 OBS_IN_EYE / V_GetInEyePos，view.cpp:1289-1321）----
                    // 机位 = 被观察者的眼位（那里 origin = ent->origin，再按站/蹲加视高），朝向 = 他本人的 yaw/pitch。
                    // 眼位不在这里另拼：rig 的 EyePosition 就是"眼位锚点（被观察者脚下）+ 眼高"，
                    // 而眼高取自模拟的 CsActor.EyePosition（站立 CsConst.EyeHeight，蹲姿 ×0.72）。
                    _pendingRotation = Quaternion.Euler(-_rig.Pitch, _rig.Yaw, 0f);
                    _pendingPosition = _eyePosition;
                    _hasPendingView = true;
                }
                else
                {
                    // ---- 第三人称观战 / 跟随（原版 view.cpp:617-635）----
                    // 机位 = 被观察者眼位沿**视线反方向**退 ofs[2]；roll 归零（view.cpp:627）；距离与防穿墙见
                    // ResolveChaseDistance。位置算法两档共用，角度来源按档位取（CsSpectator.TryChaseCameraAngles
                    // 是这条规则的唯一实现）：档位 1 锁到目标的视角、档位 2 用观察者自己的视角。
                    // 该函数对 5 / 6 返回 false 并按观察者自己的视角给出；那种"档位与机位不匹配"已在档位
                    // 变化时由 spec.mode.mismatch 留过痕（本分支不逐帧重复报）。
                    CsSpectator.TryChaseCameraAngles(CsSpectator.Mode, _rig.Yaw, _rig.Pitch,
                                                     target.Yaw, target.Pitch, out var camYaw, out var camPitch);

                    var chaseLook = CameraMath.AimDirection(camYaw, camPitch);
                    var dist = ResolveChaseDistance(_eyePosition, chaseLook);
                    _pendingRotation = Quaternion.Euler(-camPitch, camYaw, 0f);
                    _pendingPosition = _eyePosition - chaseLook * dist;
                    _hasPendingView = true;
                }
            }
        }

        /// <summary>
        /// 把**第三人称观战**机位写到相机上（在射线之后调用）。
        ///
        /// <para>第一人称位姿**已由引擎 rig 的 <c>Tick(dt)</c> 下发**（含垂直 <c>fieldOfView</c>：
        /// Unity 的 <c>fieldOfView</c> 是垂直口径，引擎在 <c>ApplyToCamera</c> 里按 <c>cam.aspect</c> 用
        /// <see cref="CameraMath.FovYFromFovX"/> 换算）⇒ 那种情况下本方法是空操作。
        /// 这正是调用方 <c>PlayerModule</c> 的「① 相机 ② 射线 ③ 落位」三段顺序仍然成立的原因：
        /// 第 ③ 步只对观战机位有意义，本组件不再自己算垂直 FOV（那是第二份宽高比换算）。</para>
        /// </summary>
        internal void ApplyToTransform()
        {
            if (_camera == null || !_hasPendingView) return;
            _camera.transform.SetPositionAndRotation(_pendingPosition, _pendingRotation);
        }

        private Vector3 _pendingPosition;
        private Quaternion _pendingRotation;
        private bool _hasPendingView;

        // ==================================================================
        //  相机 / 音频 / 自身模型
        // ==================================================================
        private void EnsureCamera()
        {
            if (_camera != null) return;

            // 编辑器 Game 视图的「Gizmos」叠层会给 **Camera / AudioListener** 各画一个图标
            //（用户报的"喇叭"就在这台上：本对象同时挂 Camera 与 AudioListener）。
            // HideInHierarchy 只改"编辑器叠加层画不画"：不改渲染、不动物理、不影响 Camera.main
            //（按 tag 查，与层级可见性无关）；本对象本就常驻，隐藏层级显示无副作用。
            // 不用 HideAndDontSave —— 那会改生命周期语义（它已经是 DontDestroyOnLoad 的）。
            var go = new GameObject(CameraObjectName);
            go.hideFlags = HideFlags.HideInHierarchy;
            Object.DontDestroyOnLoad(go);
            _camera = go.AddComponent<Camera>();
            _camera.tag = "MainCamera";          // 让 Game.UI.FloatText / 其它模块的 Camera.main 拿到它
            _camera.nearClipPlane = NearClipPlane;
            _camera.farClipPlane = FarClipPlane;
            _camera.cullingMask = ~0;
            _camera.fieldOfView = _baseFov;

            // 先关着：比赛没开始就不该有第二台相机抢画面（自检里会按"是否在比赛中"开关）。
            _camera.enabled = false;

            _log.Always($"舞台没有可用相机，已自建常驻第一人称相机「{CameraObjectName}」" +
                        $"（near={NearClipPlane} far={FarClipPlane} cullingMask=All，比赛开始时启用）");
        }

        /// <summary>
        /// 没有监听器 = 全场静音（而且不报错，非常难查）。
        /// </summary>
        private void EnsureAudioListener()
        {
            var listeners = Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include);
            if (listeners != null && listeners.Length > 0) return;
            if (_camera == null) return;

            _camera.gameObject.AddComponent<AudioListener>();
            _log.Warn("audio.listener", "场景里没有任何 AudioListener（会全场静音），已在第一人称相机上补一个");
        }

        private void TickSelfCheck(float dt, bool running)
        {
            _selfCheckTimer += dt;
            if (_selfCheckTimer < SelfCheckInterval) return;
            _selfCheckTimer = 0f;

            SyncCameraEnabled(running);
            if (!running)
            {
                // 离开比赛（回主菜单 / 换图）：把藏过的模型还回去，免得"下一局的 actor id 与上一局重号"时
                // 因为"目标没变"而漏掉一次隐藏（角色视图会被重建，留着旧账只会误导）。
                RestoreHiddenBody();
                return;
            }

            EnsureAudioListener();
            HideOwnBody();
            DisableForeignMainCameras();
            LogFovDiagnostics();
            LogSpectateDiagnostics();
        }

        /// <summary>
        /// FOV 口径的运行时自证行（数值类证据）：打印视口尺寸 / 宽高比 / 水平 FOV / 送进 Unity 的垂直
        /// <c>fieldOfView</c> / 反算回来的等效水平 FOV —— 等效水平必须恒等于 <see cref="CsConst.DefaultFov"/>（±0.05°），
        /// 换分辨率也一样（那才说明"水平固定"实现了）。
        /// 只在（尺寸或 fovY）变化时打，不刷屏。
        /// </summary>
        private void LogFovDiagnostics()
        {
            var w = Screen.width;
            var h = Screen.height;
            if (w <= 0 || h <= 0) return;

            if (_rig == null) return;
            // 两个数都取自引擎 rig（它是宽高比换算 / 平滑的唯一实现）：垂直 = 真正下发给 Unity 的那个。
            var fovY = _rig.VerticalFieldOfView;
            var fovX = _rig.FovXCurrent;
            if (w == _loggedWidth && h == _loggedHeight && Mathf.Abs(fovY - _loggedFovY) < 0.001f) return;
            _loggedWidth = w;
            _loggedHeight = h;
            _loggedFovY = fovY;

            var aspect = w / (float)h;
            var backToHorizontal = 2f * Mathf.Atan(Mathf.Tan(fovY * 0.5f * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg;
            _log.Always($"FOV 口径核对：Screen={w}x{h} aspect={aspect:F4} 水平FOV={fovX:F2}° " +
                        $"→ Unity.fieldOfView(垂直)={fovY:F2}°；反算等效水平={backToHorizontal:F2}° " +
                        $"（原版 default_fov=90 是水平 · HLSDK/cl_dll/hud.cpp:332 + view.cpp:1737-1752）");
        }

        /// <summary>
        /// 观战机位的运行时自证行（数值类证据）。每个有机位的档位各一条（行里都带档位号 + 相机位置 + 朝向），
        /// 判据分别是：
        /// <list type="bullet">
        /// <item>目标第一人称（OBS_IN_EYE）—— 相机 ↔ 目标眼位距离应为 0（相机就在他眼睛上）、
        /// 相机 yaw 应等于目标 yaw、眼位高应等于模拟给的值；</item>
        /// <item>第三人称追尾（OBS_CHASE_LOCKED / OBS_CHASE_FREE）—— 相机 ↔ 目标眼位水平/三维距离，在
        /// [<c>CsConst.ChaseMinDistance</c>, <c>CsConst.ChaseDistance</c>] 内、且贴墙时收缩；
        /// 相机朝向：档位 1 = 目标的视角、档位 2 = 观察者自己的视角；</item>
        /// <item>自由飞行（OBS_ROAMING）—— 相机位置与速度（速度上限
        /// <c>CsConst.SpecRoamMaxSpeed</c>）、朝向 = 观察者自己的鼠标角。</item>
        /// </list>
        /// </summary>
        private void LogSpectateDiagnostics()
        {
            if (_camera == null || !_camera.enabled) return;

            var camPos = _camera.transform.position;

            if (IsFreeLookSpectate())
            {
                _log.Always($"自由飞行机位：相机=({camPos.x:F3}, {camPos.y:F3}, {camPos.z:F3}) m，" +
                            $"速度={_roam.Velocity.magnitude:F4} m/s（上限 sv_spectatormaxspeed {CsConst.SpecRoamMaxSpeed:F4} m/s），" +
                            $"朝向 yaw={_camera.transform.eulerAngles.y:F2}° pitch={-_camera.transform.eulerAngles.x:F2}°" +
                            "；无重力 / 无碰撞 / 无边界（view.cpp:1538-1540 + pm_shared.c:1490-1572）");
                return;
            }

            if (!IsSpectateWithTarget()) return;

            var target = _match.SpectateTarget;
            if (target == null) return;

            var eye = target.EyePosition;

            if (IsFirstPersonSpectate())
            {
                var d = Vector3.Distance(camPos, eye);
                _log.Always($"目标第一人称机位：相机↔目标眼位 距离={d:F4} m（应为 0 —— 相机就在他眼睛上）" +
                            $"，相机 yaw={_camera.transform.eulerAngles.y:F2}° / 目标 yaw={target.Yaw:F2}°" +
                            $"，眼位高={target.EyeHeight:F4} m（站 {CsConst.EyeHeight:F2} / 蹲 ×0.72，取自模拟的 CsActor）" +
                            "（view.cpp:1289-1321 V_GetInEyePos）");
                return;
            }

            var dh = new Vector2(camPos.x - eye.x, camPos.z - eye.z).magnitude;
            var d3 = Vector3.Distance(camPos, eye);
            var chaseMode = CsSpectator.Mode;
            var chaseFace = chaseMode == CsSpectatorMode.ChaseLocked
                ? "角度锁到目标视角（view.cpp:1532 传 NULL ⇒ :1264-1268 取 ent->angles）"
                : "角度用观察者自己的视角（view.cpp:1535 传 v_cl_angles）";
            _log.Always($"第三人称机位（档位 {(int)chaseMode} {CsSpectator.ModeName(chaseMode)}，{chaseFace}）：" +
                        $"相机=({camPos.x:F3}, {camPos.y:F3}, {camPos.z:F3}) m，" +
                        $"朝向 yaw={_camera.transform.eulerAngles.y:F2}° pitch={-_camera.transform.eulerAngles.x:F2}°" +
                        $"，相机↔目标眼位 水平距离={dh:F4} m（三维 {d3:F4} m）" +
                        $"，理想={CsConst.ChaseDistance:F4} 下限={CsConst.ChaseMinDistance:F4} " +
                        $"当前距离={_chaseDistance:F4} m，本帧射线命中点={(_chaseHitDistance >= 0f ? _chaseHitDistance.ToString("F4") : "无（未贴墙）")}" +
                        "（cl_chasedist 112 unit + CAM_MIN_DIST 30 unit + V_GetChaseOrigin）");
        }

        /// <summary>相机是否处于观战且拿到了被观察者（需要目标的观战档位 1 / 2 / 4 共同的前提）。</summary>
        private bool IsSpectateWithTarget()
        {
            if (_match == null || !_match.IsSpectating) return false;
            return _match.SpectateTarget != null;
        }

        /// <summary>
        /// 当前是否**外部机位**的观战档位（原版 <c>OBS_CHASE_LOCKED</c> / <c>OBS_CHASE_FREE</c> = 两种追尾 /
        /// <c>OBS_ROAMING</c> = 自由飞行）—— 这几种机位相机都不在目标身上，被观察者的模型必须看得见。
        /// </summary>
        private bool IsExternalSpectate()
        {
            if (!IsSpectateWithTarget()) return false;
            var mode = CsSpectator.Mode;
            return mode == CsSpectatorMode.ChaseLocked
                || mode == CsSpectatorMode.ChaseFree
                || mode == CsSpectatorMode.FreeLook;
        }

        /// <summary>当前是否目标第一人称档位（原版 <c>OBS_IN_EYE</c>）。</summary>
        private bool IsFirstPersonSpectate()
        {
            return IsSpectateWithTarget() && CsSpectator.Mode == CsSpectatorMode.FirstPerson;
        }

        /// <summary>当前是否自由飞行档位（原版 <c>OBS_ROAMING</c>；不要求有观战目标 —— 原版该档位同样不需要）。</summary>
        private bool IsFreeLookSpectate()
        {
            return _match != null && _match.IsSpectating && CsSpectator.Mode == CsSpectatorMode.FreeLook;
        }

        /// <summary>自建相机只在比赛运行时启用 —— 否则它会盖住主菜单画面、并抢走 <c>Camera.main</c>。</summary>
        private void SyncCameraEnabled(bool running)
        {
            if (_camera == null) return;
            if (_camera.enabled == running) return;

            _camera.enabled = running;
            _log.Always(running ? "第一人称相机已启用（比赛进行中）" : "第一人称相机已关闭（不在比赛中，交还画面给菜单相机）");
        }

        /// <summary>
        /// 引擎的 UI Canvas 是 <c>ScreenSpaceOverlay</c>，不依赖相机，因此关掉它们不会影响 HUD。
        ///
        /// <para><b>顺带把它的层级图标也压掉</b>：编辑器 Game view 的「Gizmos」叠层会给每台 Camera
        /// 与它身上的 <c>AudioListener</c> 各画一个图标（用户报的"喇叭"那一族的一支就挂在这台外来相机上：
        /// 全场唯一的 <c>AudioListener</c> 在舞台的 <c>Main Camera</c>，而 <c>EnsureAudioListener</c>
        /// 因"场上已有监听器"提前返回、我们自己的相机上不会挂它 —— 见 :429-437）。
        /// 一台已经不参与画面的相机没有理由再画图标，因此这里连 <c>hideFlags</c> 一起设。
        /// 只设 <c>HideInHierarchy</c>：不改渲染、不动物理、不影响 <c>Camera.main</c>（按 tag 查）。</para>
        ///
        /// <para><b>与 VisualLeakGuard 的分工</b>（口径）：主防线是编辑器侧的
        /// <c>client/Assets/Editor/VisualLeakGuard.cs</c>（进 Play 自动把 Game view 的叠加层关掉）；
        /// 这里只是**第二防线**，专防"用户手动把 Gizmos 又点开"时业务自己创建/接管的相机还画图标。</para>
        /// </summary>
        private void DisableForeignMainCameras()
        {
            var cams = Camera.allCameras;
            for (var i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null || c == _camera) continue;
                if (!c.CompareTag("MainCamera")) continue;

                c.enabled = false;
                c.gameObject.hideFlags |= HideFlags.HideInHierarchy;
                if (_handledForeignCameras.Add(c))
                {
                    _log.Always($"舞台里另有一台主相机「{c.name}」已关闭（第一人称相机接管画面），" +
                                $"并已隐藏其层级图标（Camera/AudioListener 两个 gizmo）");
                }
            }
        }

        /// <summary>
        /// 隐藏"当前不该看见的模型"：
        /// <list type="bullet">
        /// <item><b>第一人称</b>（不观战）= 本地玩家自己 —— 第一人称不能看见自己的身体
        /// （否则镜头被自己的后脑勺糊满）；</item>
        /// <item><b>第三人称追尾观战中</b> = **谁都不藏**：相机在目标身后 2.8448 m 处（见
        /// <see cref="PrepareView"/> 的追尾分支），被观察者的模型必须看得见 —— 藏了就没人可看。
        /// 这条分支在 <see cref="HideOwnBody"/> 开头就 return 掉了。</item>
        /// <item><b>目标第一人称观战中</b>（原版 <c>OBS_IN_EYE</c>）= 藏掉**被观察者**自己 ——
        /// 相机就在他的眼睛上，不藏就是被他的后脑勺/躯干糊满（等同"第一人称看不见自己的身体"）。</item>
        /// </list>
        ///
        /// <para>依据契约：角色的模型由视图生成器生成，并在受击体上挂 <see cref="CsHitboxProxy"/> +
        /// <c>ActorId</c>。这里把 <c>ActorId == 目标</c> 的那些受击体节点（含子节点）的 <c>Renderer</c>
        /// 关掉 —— 只影响这一个 actor，队友/敌人的模型照常显示。</para>
        ///
        /// <para>本方法由 1 秒一次的自检调用（兜住"角色视图被重建"这类情况）；
        /// <b>切换观战目标的即时还原</b>在 <see cref="PrepareView"/> 里做（相机换目标的同一帧）。</para>
        ///
        /// <para>若做的是"独立的第一人称手臂模型"，它不带 <c>CsHitboxProxy</c>，
        /// 因此不会被这里关掉。</para>
        /// </summary>
        private void HideOwnBody()
        {
            if (_match == null) return;
            if (_camera == null || !_camera.enabled) return;

            // 外部机位观战中（追尾 2 / 自由飞行 3）：相机在目标**身外**，必须把被观察者的模型露出来
            // （藏了就没东西可看）；只把之前（第一人称时期）藏过的还回去。
            if (IsExternalSpectate())
            {
                RestoreHiddenBody();
                return;
            }

            var target = DesiredBodyActor(out var spectating);
            if (target == null)
            {
                // 非预期分支：比赛在跑，却既没有观战目标也拿不到本地玩家 ⇒ 不知道藏谁，本帧不动（留痕）。
                _log.Warn("body.notarget",
                    $"既拿不到观战目标也拿不到本地玩家 actor（IsSpectating={_match.IsSpectating}），本帧不隐藏任何模型");
                return;
            }

            ApplyHiddenBody(target, spectating);   // 目标变了 → 在这里"还原上一个 + 关掉这一个"
            HideRenderersOf(target.Id);            // 幂等再关一遍：兜住"视图被重建 / 别人又把它打开"
        }

        /// <summary>
        /// 本帧相机该藏谁的模型：观战中 = 被观察者，否则 = 本地玩家。
        /// 与 <see cref="PrepareView"/> 里选相机跟随目标的判据**完全一致**
        /// （<c>SpectateTarget</c> 为 null 时相机留在本地玩家位置，模型也就藏本地玩家）。
        /// </summary>
        private CsActor DesiredBodyActor(out bool spectating)
        {
            spectating = false;
            if (_match == null) return null;

            if (_match.IsSpectating)
            {
                var spec = _match.SpectateTarget;
                if (spec != null)
                {
                    spectating = true;
                    return spec;
                }
            }
            return _match.LocalPlayer;
        }

        /// <summary>
        /// 把"被藏起来的模型"从上一个 actor 切到 <paramref name="actor"/>：
        /// <b>先还原上一个</b>（否则被看过的人的模型会一直隐形），再关掉这一个。
        /// </summary>
        /// <returns>目标确实变了（做了"还原 + 关一遍"）→ true；目标没变 → false。</returns>
        private bool ApplyHiddenBody(CsActor actor, bool spectating)
        {
            if (actor == null) return false;
            if (_hasHiddenBody && _hiddenBodyActorId == actor.Id) return false;

            var previous = _hasHiddenBody ? _hiddenBodyActorId : 0L;
            var restored = RestoreHiddenBody();

            _hiddenBodyActorId = actor.Id;
            _hasHiddenBody = true;

            // 只在"目标真的变了"时打（本方法开头已挡住"目标没变"的重复调用）——
            // 自检是 1 秒一次，这里不会刷屏。尾巴只在**真的还回去了**时才说"已还原"。
            var tail = restored > 0 ? $"；上一个目标（id={previous}）的 {restored} 个渲染已还原" : string.Empty;
            _log.Always(spectating
                ? $"观战：已隐藏被观察者 {actor.Name}（id={actor.Id}）的模型渲染" +
                  $"（相机正在他的眼睛上，藏掉才不会被他的模型糊满屏幕）{tail}"
                : $"第一人称：已隐藏本地玩家 {actor.Name}（id={actor.Id}）的模型渲染" +
                  $"（碰撞体保留，敌人照样打得到我）{tail}");

            HideRenderersOf(actor.Id);
            return true;
        }

        /// <summary>还原 <see cref="_hiddenBodyRenderers"/> 里那批（= 上一个 actor 的）渲染。</summary>
        /// <returns>真的被重新打开的渲染个数（0 = 本来没关过 / 对象已销毁）。</returns>
        private int RestoreHiddenBody()
        {
            if (!_hasHiddenBody) return 0;

            var actorId = _hiddenBodyActorId;
            var restored = 0;
            var gone = 0;
            foreach (var rend in _hiddenBodyRenderers)
            {
                if (rend == null) { gone++; continue; }   // 视图已被销毁（换局 / 换图）
                if (rend.enabled) continue;
                rend.enabled = true;
                restored++;
            }
            _hiddenBodyRenderers.Clear();
            _hiddenBodyActorId = 0L;
            _hasHiddenBody = false;

            if (gone > 0)
            {
                _log.Info("body.restore.gone",
                    $"还原 actor {actorId} 的模型渲染时发现 {gone} 个渲染已被销毁（角色视图被重建？），已跳过");
            }
            if (restored > 0)
            {
                _log.Always($"模型显隐切换：已还原 actor {actorId} 的 {restored} 个模型渲染（相机不再看他）");
            }
            return restored;
        }

        /// <summary>
        /// 把某个 actor 的所有受击体渲染关掉（幂等；只把**还在开着**的记进 <see cref="_hiddenBodyRenderers"/> ——
        /// 已经关着的不抢账，免得把别人的隐藏逻辑（<c>ActorView.HideOwnRenderers</c>）也算成自己关的）。
        /// </summary>
        private void HideRenderersOf(long actorId)
        {
            var proxies = Object.FindObjectsByType<CsHitboxProxy>(FindObjectsInactive.Include);
            for (var i = 0; i < proxies.Length; i++)
            {
                var p = proxies[i];
                if (p == null || p.ActorId != actorId) continue;

                var renderers = p.GetComponentsInChildren<Renderer>(true);
                for (var r = 0; r < renderers.Length; r++)
                {
                    var rend = renderers[r];
                    if (rend == null || !rend.enabled) continue;
                    _hiddenBodyRenderers.Add(rend);
                    rend.enabled = false;
                }
            }
        }

        // ==================================================================
        //  第三人称机位（观战 / 跟随）
        // ==================================================================
        /// <summary>
        /// 本帧的机位距离（米）：先让理想距离收敛（原版 <c>cam_snapto 0</c> 的平滑分支），
        /// 再做一次**即时**的射线收缩防穿墙。
        ///
        /// <para><b>① 收敛</b>（出处 <c>HLSDK/cl_dll/in_camera.cpp:386-389</c>）：
        /// <c>if(abs(camAngles[2] - cam_idealdist-&gt;value ) &lt; 2.0 ) camAngles[2] = cam_idealdist-&gt;value;
        /// else camAngles[2] += (cam_idealdist-&gt;value - camAngles[2] ) / 4.0;</c>
        /// ⇒ 差值 &lt; 2.0 unit（0.0508 m）直接吸附，否则每帧朝理想值移 1/4。
        /// 这不是"手感参数"，是原版的收敛公式（本工程不许凭空定平滑常数）。</para>
        ///
        /// <para><b>② 防穿墙</b>（出处 <c>HLSDK/cl_dll/view.cpp:899-960</c> 的 <c>V_GetChaseOrigin</c>，
        /// 使用点 <c>:1282</c> 传的就是 <c>cl_chasedist-&gt;value</c>）：
        /// 从目标沿<b>反视线</b>打一条 <c>PM_TraceLine</c>，机位落在
        /// <c>VectorMA(trace-&gt;endpos, 4, trace-&gt;plane.normal, returnvec)</c>（<c>:957</c>）——
        /// 即"命中点沿墙法线再往外 4 unit（0.1016 m）"，保证相机在墙的这一侧；
        /// 下限取 <c>CAM_MIN_DIST</c>（<c>in_camera.cpp:31</c>）30 unit = 0.762 m。
        /// 收缩**即时生效**（原版那条 trace 也是每帧重打的），不参与 ① 的平滑，
        /// 否则贴着墙走的那几帧会看到墙里。</para>
        /// </summary>
        private float ResolveChaseDistance(Vector3 pivot, Vector3 lookDirection)
        {
            // ① 理想距离的平滑收敛
            var ideal = CsConst.ChaseDistance;
            if (Mathf.Abs(_chaseDistance - ideal) < CsConst.ChaseDistanceSnap)
                _chaseDistance = ideal;
            else
                _chaseDistance += (ideal - _chaseDistance) * CsConst.ChaseDistanceLerp;

            // ② 射线收缩：只打世界层（CsWorld = map 的 Level 子树），不会打到自己/别人身上
            var worldMask = 1 << PhysicsLayers.World;
            if (Physics.Raycast(pivot, -lookDirection, out var hit, _chaseDistance, worldMask,
                    QueryTriggerInteraction.Ignore))
            {
                _chaseHitDistance = hit.distance;
                var kept = hit.distance - CsConst.ChaseWallOffset;
                _chaseDistance = Mathf.Clamp(kept, CsConst.ChaseMinDistance, _chaseDistance);
            }
            else
            {
                _chaseHitDistance = -1f;
            }
            return _chaseDistance;
        }

        // ==================================================================
        //  观战·自由飞行（原版 OBS_ROAMING = 档位 3）
        // ==================================================================
        /// <summary>
        /// 进入自由飞行的起点（原版 <c>iJumpSpectator</c> 语义，<c>pm_shared.c:1503-1512</c>：
        /// <c>VectorCopy(vJumpOrigin, pmove-&gt;origin)</c> + 速度清零；对应 <c>hud_spectator.cpp:834-842</c>
        /// 切到 <c>OBS_ROAMING</c> 时先把观察者"跳"到当前机位）。这里取**当前相机位置**当起点
        /// （换档前那一帧机位就停在那儿），避免瞬移。
        /// </summary>
        private void StartRoamFrom(Vector3 position)
        {
            _roam = new CsSpectatorRoam.State { Origin = position, Velocity = Vector3.zero };
            _roamNeedsStart = false;
            _log.Info("spectate.roam.start",
                $"自由飞行起点：({position.x:F3}, {position.y:F3}, {position.z:F3}) m，速度清零（原版 iJumpSpectator，pm_shared.c:1503-1512）");
        }

        /// <summary>
        /// 本帧推进**观察者自己**的漫游原点（解算 = <see cref="CsSpectatorRoam.Step"/>，逐语句对应原版
        /// <c>PM_SpectatorMove</c>）。
        ///
        /// <para><b>方向口径</b>取本工程既有约定（<c>Module/Match/CsMatch.cs:1080-1082</c>：
        /// <c>forward = (sin yaw, 0, cos yaw)</c>、<c>right = (cos yaw, 0, -sin yaw)</c>），
        /// 这里 forward 再按俯仰抬起来 —— 原版 <c>pm_shared.c:2454</c> 的
        /// <c>AngleVectors(pmove-&gt;angles, …)</c> 给的 forward 本来就含俯仰，所以
        /// "抬头 / 低头 + 前进"能往下 / 往上飞（这也是本工程唯一的垂直移动方式，见
        /// <see cref="ReadRoamMoveInput"/> 的 upmove 说明）。</para>
        /// </summary>
        private CsSpectatorRoam.State AdvanceRoam(float dt, float yaw, float pitch)
        {
            var yawRad = yaw * Mathf.Deg2Rad;
            var pitchRad = pitch * Mathf.Deg2Rad;
            var forward = new Vector3(Mathf.Cos(pitchRad) * Mathf.Sin(yawRad), Mathf.Sin(pitchRad),
                                      Mathf.Cos(pitchRad) * Mathf.Cos(yawRad));
            var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));

            ReadRoamMoveInput(out var forwardMove, out var sideMove);
            // upmove 恒 0：原版它来自 `+moveup` / `+movedown` 的**按键绑定**，而绑定表（config.cfg）不在盘
            // ⇒ 盘上给不出"本工程该把上下飞绑到哪两个键"的口径，就不绑（垂直移动靠抬头/低头 + 前进）。
            return CsSpectatorRoam.Step(_roam, dt, forward, right, forwardMove, sideMove, 0f);
        }

        /// <summary>
        /// 读自由飞行的 WASD 指令（前进 / 后退 / 侧移幅值都取 <see cref="CsConst.SpecRoamMoveSpeed"/>）。
        ///
        /// <para><b>鼠标不在这里读</b>：视角仍由 <c>PlayerMotor</c> 采集（本组件读第二遍会把灵敏度算两遍，
        /// 这是本组件类注释里的既有约定）。</para>
        ///
        /// <para><b>什么时候不读</b>：有面板打开（控制台里打的 <c>spec_*</c> 命令含 WASD，按下去是在打字）、
        /// 暂停、不在比赛中 —— 这三种情况下推进原点就是在乱飞。</para>
        /// </summary>
        private void ReadRoamMoveInput(out float forwardMove, out float sideMove)
        {
            forwardMove = 0f;
            sideMove = 0f;

            if (_openPanels.Contains(ConsolePanelName)) return;   // 控制台里按 WASD 是在打字
            if (_match == null || !_match.IsRunning || _match.IsPaused) return;

            var input = Game.Input;
            if (input == null)
            {
                if (!_warnedRoamInput)
                {
                    _warnedRoamInput = true;
                    _log.Warn("input.missing", "Game.Input 为 null（CloverInput.Init 未执行？）：自由飞行不会响应 WASD");
                }
                return;
            }

            var f = (input.GetKey(GameKey.W) ? 1f : 0f) - (input.GetKey(GameKey.S) ? 1f : 0f);
            var s = (input.GetKey(GameKey.D) ? 1f : 0f) - (input.GetKey(GameKey.A) ? 1f : 0f);
            forwardMove = f * CsConst.SpecRoamMoveSpeed;
            sideMove = s * CsConst.SpecRoamMoveSpeed;
        }

        // ==================================================================
        //  数学（引擎件）
        // ==================================================================
        //   · `FovYFromFovX`：水平 → 垂直 FOV（原版 `CalcFov` 的等价式，出处
        //     `HLSDK/cl_dll/view.cpp:1737-1752`；越界回退 90、`aspect<=0` 原样返回）；
        //   · `AimDirection`：yaw/pitch（度）→ 单位方向向量（yaw 0 = +Z、pitch + = 抬头）；
        //   · `Follow`：指数平滑跟随 —— 上升/回落按 `|target| > |current|` 选 tau，两者合并成一条，
        //     挑 tau 那一步内联在 `PrepareView` 的后坐力分支里。
        // 本组件不许再留同名 `private static` 副本 —— 判据：全项目搜
        //    `FovYFromFovX|ComputeAimDirection|FollowRecoil`，命中只应出现在 `CameraMath.Xxx(...)` 调用处。
    }
}
