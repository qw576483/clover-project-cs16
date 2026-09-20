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
    /// **业务自写**的第一人称相机（引擎只提供锁 Z 的 <c>Game.Camera.Follow</c>，明确不许用于 FPS）。
    ///
    /// <para><b>职责</b>：每帧把相机放到本地玩家眼睛位置、朝向 = 玩家视角 + 后坐力表现，
    /// 叠加视点晃动 / 受击晃动 / 开镜 FOV；**死亡观战走第三人称机位**（见 <see cref="ResolveChaseDistance"/>）。</para>
    ///
    /// <para><b>FOV 口径</b>：<c>_fov</c> 是**水平** FOV（原版口径，见 <see cref="CsConst.DefaultFov"/>），
    /// 每帧按当前宽高比换算成 Unity 要的**垂直** <c>fieldOfView</c>
    /// （见引擎件 <see cref="CameraMath.FovYFromFovX"/>，E-core-18 下沉）。</para>
    ///
    /// <para><b>两条硬约定</b>：</para>
    /// <list type="number">
    /// <item><b>不自己累加后坐力</b>：权威值在比赛模拟里（<c>CsActor.RecoilPitch/RecoilYaw</c>，
    /// 模拟负责累加与回复），本组件只做"指数跟随 + 回正"的**表现**，绝不再加一份；</item>
    /// <item><see cref="AimDirection"/> 把这份后坐力一起算进去 —— 因为后坐力在 CS 里就是"准星被抬起来"，
    /// 子弹必须跟着抬起来的方向走（射线由 <c>CombatModule</c> 用同一个方向发出）。</item>
    /// </list>
    ///
    /// <para><b>为什么相机由本组件创建</b>：舞台场景由 agent-02 生成，不保证里面有相机；
    /// 而第一人称必须有相机。自建一台常驻相机（比赛未运行时关闭）可以彻底消除
    /// "进图后黑屏 / 和菜单相机抢 <c>Camera.main</c>"这两类问题。</para>
    /// </summary>
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        private const string Tag = "Camera";
        private const string CameraObjectName = "CsFpsCamera";

        /// <summary>近裁剪面（米）：第一人称离墙很近，必须足够小。</summary>
        private const float NearClipPlane = 0.05f;

        /// <summary>远裁剪面（米）：de_dust2 对角约 90m，留足余量。</summary>
        private const float FarClipPlane = 1000f;

        /// <summary>自检（相机 / 音频监听 / 隐藏自身模型）间隔（秒）。</summary>
        private const float SelfCheckInterval = 1f;

        private const float MinBaseFov = 30f;
        private const float MaxBaseFov = 120f;

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        /// <summary>已处置过的"别人的主相机"（按引用去重；Unity 6 里 <c>GetInstanceID</c> 已废弃）。</summary>
        private readonly HashSet<Camera> _handledForeignCameras = new HashSet<Camera>();

        /// <summary>
        /// 当前被本组件藏起来的 actor（<see cref="_hasHiddenBody"/> = false 时无意义）。
        /// 观战切换时靠它知道"上一个目标是谁"，才能把上一批渲染还回去。
        /// </summary>
        private long _hiddenBodyActorId;
        private bool _hasHiddenBody;

        /// <summary>
        /// 本组件**亲手**关掉的渲染 —— 只装**当前这一个 actor** 的（去重，避免每秒重复关）。
        /// 目标一变就整批还原并清空（旧实现是只增不减的 HashSet ⇒ 被看过的人的模型会一直隐形）。
        /// </summary>
        private readonly HashSet<Renderer> _hiddenBodyRenderers = new HashSet<Renderer>();

        private Camera _camera;
        private ICsMatch _match;
        private PlayerMotor _motor;

        /// <summary>
        /// 视点晃动 = **引擎件** <see cref="CloverEngine.ViewBob"/>（纯逻辑类，不再是 MonoBehaviour）。
        /// 由 <c>PlayerModule</c> 用项目自己的 <see cref="CloverEngine.ViewBobConfig"/> 构造后经
        /// <see cref="Init"/> 注入（7 个数值仍取项目原常量，一个都没变）。
        /// </summary>
        private ViewBob _bob;

        private bool _subscribedDamage;

        private float _baseFov = CsConst.DefaultFov;
        private float _fov;
        private float _eyeHeight;
        private Vector3 _lastActorPosition;
        private bool _hasLastPosition;

        private long _lastTargetId;
        private float _recoilPitch;
        private float _recoilYaw;
        private float _zoomHeldTime;

        private float _shakeTime;
        private float _shakeMagnitude;

        private float _selfCheckTimer;

        // ---- 第三人称（观战 / 跟随）机位状态 ----
        /// <summary>当前平滑后的机位距离（米）。进第三人称时按原版重置为 <see cref="CsConst.ChaseMinDistance"/>。</summary>
        private float _chaseDistance = CsConst.ChaseDistance;
        /// <summary>上一帧是否在第三人称（用于"进入第三人称时重置机位距离"）。</summary>
        private bool _lastSpectating;
        /// <summary>本帧射线命中点距眼位的距离（米）；&lt;0 = 本帧没打到墙。仅用于日志。</summary>
        private float _chaseHitDistance = -1f;

        // ---- FOV / 机位诊断日志的"变了才打"缓存 ----
        private float _loggedFovY = float.NaN;
        private int _loggedWidth;
        private int _loggedHeight;

        /// <summary>本帧射线起点（眼睛位置，**不含** bob：子弹不跟着脚步摆）。</summary>
        public Vector3 EyePosition { get; private set; }

        /// <summary>
        /// 本帧视线方向（含后坐力表现，与相机朝向一致）：
        /// <c>(cos(pitch)*sin(yaw), sin(pitch), cos(pitch)*cos(yaw))</c> —— 与比赛模拟的
        /// <c>CsInventory.AimDirection</c> 同口径（yaw 0 = +Z，pitch + = 抬头）。
        /// </summary>
        public Vector3 AimDirection { get; private set; } = Vector3.forward;

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
            _fov = _baseFov;

            EnsureCamera();
            EnsureAudioListener();
            HookDamage();

            // 让第一帧 PrepareView 立刻做一次自检（启用相机 / 找监听器 / 关掉别的主相机）。
            _selfCheckTimer = SelfCheckInterval;

            _log.Always($"第一人称相机就绪：camera={(_camera != null ? _camera.name : "null")} " +
                        $"水平FOV={_baseFov:F0}（开镜 {CsConst.ZoomFov:F0}；Unity.fieldOfView 按宽高比逐帧换算）" +
                        $"near={NearClipPlane} far={FarClipPlane}；第三人称机位 {CsConst.ChaseDistance:F4}~{CsConst.ChaseMinDistance:F4} m");
        }

        /// <summary>玩家设置里的 FOV 变了（Options 面板）时同步。</summary>
        internal void SetBaseFov(int fovDegrees)
        {
            var clamped = Mathf.Clamp(fovDegrees, (int)MinBaseFov, (int)MaxBaseFov);
            if (Mathf.Approximately(_baseFov, clamped)) return;
            _baseFov = clamped;
            _log.Always($"FOV 设置更新：{_baseFov:F0}");
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

        /// <summary>被打时晃一下（只处理"受击者就是自己"的情况，队友/敌人挨打不动我的镜头）。</summary>
        private void OnDamaged(CsActor victim, int damage, bool headshot, bool lethal)
        {
            var local = _match != null ? _match.LocalPlayer : null;
            if (local == null || victim == null || victim.Id != local.Id) return;

            _shakeMagnitude = Mathf.Clamp01(damage / CsCombatTuning.DamageShakeFullDamage);
            _shakeTime = CsCombatTuning.DamageShakeDuration;
            _log.Info("shake", $"自己受击：{damage} 点（{(headshot ? "爆头" : "普通")}{(lethal ? "·致死" : string.Empty)}），镜头晃动");
        }

        // ==================================================================
        //  每帧：算好 → 应用
        // ==================================================================
        /// <summary>算本帧的相机位姿 / 视线方向（不写 Transform）。必须在射线之前调用。</summary>
        internal void PrepareView(float dt)
        {
            if (_camera == null) EnsureCamera();
            if (_camera == null)
            {
                _log.Warn("camera.missing", "相机不可用（创建失败？），第一人称视角不会更新");
                return;
            }

            var running = _match != null && _match.IsRunning;
            TickSelfCheck(dt, running);

            if (!running)
            {
                // 比赛没开始（主菜单 / 读条 / 已回主菜单）：不动相机、不算射线。
                AimDirection = Vector3.forward;
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

            if (_lastTargetId != target.Id)
            {
                _lastTargetId = target.Id;
                _recoilPitch = 0f;
                _recoilYaw = 0f;
                _hasLastPosition = false;
                _bob?.Reset();
                // 观战切换：把自由视角先对齐到被观察者的朝向，避免"瞬间背对"。
                if (spectating && _motor != null) _motor.ForceLook(target.Yaw, target.Pitch);
                _log.Always($"相机跟随目标切换 → {target.Name}（id={target.Id}，{(spectating ? "观战·第三人称" : "自己·第一人称")}）");

                // 模型显隐：
                // · 第一人称（不观战）—— 藏掉自己的身体，否则镜头被自己的后脑勺糊满；
                // · 第三人称观战 —— **必须把被观察者的模型露出来**（相机在他身后，藏了就看不到人），
                //   同时把之前藏过的还回去。
                if (spectating)
                {
                    RestoreHiddenBody();
                }
                else
                {
                    // 与相机目标**同帧**切换：藏住自己、并把上一个目标的渲染还回去。
                    // （只靠 1 秒一次的自检会出现"相机已经贴到他眼睛上、他的模型还开着"的最多 1 秒糊屏）
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
            }

            // ---- 眼睛位置（站/蹲与台阶做平滑；瞬移直接吸附）----
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
            EyePosition = target.Position + new Vector3(0f, _eyeHeight, 0f);

            // ---- 后坐力表现（只跟随模拟的权威值）----
            var recoilPitch = 0f;
            var recoilYaw = 0f;
            if (!spectating)
            {
                var wantPitch = target.RecoilPitch * CsCombatTuning.RecoilViewScale;
                var wantYaw = target.RecoilYaw * CsCombatTuning.RecoilViewScale;
                // 后坐力的表现跟随：**上跳用快常数、回正用慢常数**（"抬得快、落得慢"）；
                // 只平滑跟随模拟的权威值，不累加。
                // 合并口径：引擎只提供一条 CameraMath.Follow（原 FollowRecoil 只是"按
                // |target| > |current| 在上升/回落两个 tau 里挑一个"，属业务数值选择）⇒
                // 那一步就近内联在这里，⛔ 不留第二份跟随实现。
                _recoilPitch = CameraMath.Follow(_recoilPitch, wantPitch, dt,
                    Mathf.Abs(wantPitch) > Mathf.Abs(_recoilPitch) ? CsCombatTuning.RecoilRiseTau : CsCombatTuning.RecoilFallTau);
                _recoilYaw = CameraMath.Follow(_recoilYaw, wantYaw, dt,
                    Mathf.Abs(wantYaw) > Mathf.Abs(_recoilYaw) ? CsCombatTuning.RecoilRiseTau : CsCombatTuning.RecoilFallTau);
                recoilPitch = _recoilPitch;
                recoilYaw = _recoilYaw;
            }
            else
            {
                _recoilPitch = 0f;
                _recoilYaw = 0f;
            }

            // ---- 视角 ----
            var yaw = (_motor != null ? _motor.Yaw : target.Yaw) + recoilYaw;
            var pitch = Mathf.Clamp((_motor != null ? _motor.Pitch : target.Pitch) + recoilPitch,
                -CsCombatTuning.PitchLimit, CsCombatTuning.PitchLimit);

            // 射线方向 = 不含 bob / 不含受击抖动的视线（子弹不该跟着画面抖）
            // 俯仰的 ±PitchLimit 夹紧已在上一行做过（引擎件 CameraMath.AimDirection 只做纯几何，见其注释）。
            AimDirection = CameraMath.AimDirection(yaw, pitch);

            // ---- 开镜 FOV ----
            var zoomed = _match.IsZoomed;
            _zoomHeldTime = zoomed ? _zoomHeldTime + dt : 0f;
            var wantFov = zoomed ? CsConst.ZoomFov : _baseFov;
            _fov = CameraMath.Follow(_fov, wantFov, dt, CsCombatTuning.ZoomTransitionTau);

            // ---- bob ----
            var speedXZ = new Vector2(target.Velocity.x, target.Velocity.z).magnitude;
            if (_bob != null) _bob.Tick(dt, spectating ? 0f : speedXZ, target.OnGround);
            var bobOffset = _bob != null ? _bob.Offset : Vector3.zero;
            var bobRoll = _bob != null ? _bob.Roll : 0f;

            // ---- 受击晃动 ----
            var shakePitch = 0f;
            var shakeYaw = 0f;
            var shakeRoll = 0f;
            if (_shakeTime > 0f)
            {
                _shakeTime = Mathf.Max(0f, _shakeTime - dt);
                var k = _shakeTime / CsCombatTuning.DamageShakeDuration *
                        CsCombatTuning.DamageShakeMaxDeg * _shakeMagnitude;
                shakePitch = Random.Range(-k, k);
                shakeYaw = Random.Range(-k, k);
                shakeRoll = Random.Range(-k, k);
            }

            var finalYaw = yaw + shakeYaw;
            var finalPitch = Mathf.Clamp(pitch + shakePitch, -CsCombatTuning.PitchLimit, CsCombatTuning.PitchLimit);

            if (spectating)
            {
                // ---- 第三人称观战 / 跟随（原版 view.cpp:617-635）----
                // 机位 = 眼位沿**视线反方向**退 ofs[2]；roll 归零；距离与防穿墙见 ResolveChaseDistance。
                // 方向用 AimDirection（不含 bob / 不含受击抖动）：原版的 camForward 也是由玩家视角算的，
                // 抖动的只是视角、不是机位本身。
                var dist = ResolveChaseDistance(EyePosition, AimDirection);
                _pendingRotation = Quaternion.Euler(-finalPitch, finalYaw, 0f);   // roll 归零（view.cpp:627）
                _pendingPosition = EyePosition - AimDirection * dist;
                _hasPendingView = true;
                return;
            }

            var rotation = Quaternion.Euler(-finalPitch, finalYaw, bobRoll + shakeRoll);

            // bob 是"画面局部偏移"：先转进视线空间再加到眼睛位置（这样左右晃不会穿墙方向错）。
            _pendingPosition = EyePosition + rotation * new Vector3(bobOffset.x, bobOffset.y, 0f);
            _pendingRotation = rotation;
            _hasPendingView = true;
        }

        /// <summary>把 <see cref="PrepareView"/> 算出的位姿写到相机上（在射线之后调用）。</summary>
        internal void ApplyToTransform()
        {
            if (_camera == null) return;
            if (!_hasPendingView)
            {
                _camera.fieldOfView = CurrentVerticalFov();
                return;
            }

            _camera.transform.SetPositionAndRotation(_pendingPosition, _pendingRotation);
            // Unity 的 fieldOfView 是**垂直** FOV，而 _fov 是原版口径的**水平** FOV
            // ⇒ 每帧按当前宽高比换算（见引擎件 CameraMath.FovYFromFovX）。⛔ 不许写死 58.72。
            _camera.fieldOfView = CurrentVerticalFov();
        }

        /// <summary>当前屏幕宽高比下的垂直 FOV（度）——<c>_fov</c> 是水平 FOV。</summary>
        private float CurrentVerticalFov()
        {
            if (Screen.width <= 0 || Screen.height <= 0) return _fov;   // 取不到视口时保持原值（不在 Play 里的退化分支）
            return CameraMath.FovYFromFovX(_fov, Screen.width / (float)Screen.height);
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

            var go = new GameObject(CameraObjectName);
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
        /// 保证场上**恰好有一个 AudioListener**：舞台场景由 agent-02 生成，可能完全没有相机/监听器，
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

            var fovY = CurrentVerticalFov();
            if (w == _loggedWidth && h == _loggedHeight && Mathf.Abs(fovY - _loggedFovY) < 0.001f) return;
            _loggedWidth = w;
            _loggedHeight = h;
            _loggedFovY = fovY;

            var aspect = w / (float)h;
            var backToHorizontal = 2f * Mathf.Atan(Mathf.Tan(fovY * 0.5f * Mathf.Deg2Rad) * aspect) * Mathf.Rad2Deg;
            _log.Always($"FOV 口径核对：Screen={w}x{h} aspect={aspect:F4} 水平FOV={_fov:F2}° " +
                        $"→ Unity.fieldOfView(垂直)={fovY:F2}°；反算等效水平={backToHorizontal:F2}° " +
                        $"（原版 default_fov=90 是水平 · HLSDK/cl_dll/hud.cpp:332 + view.cpp:1737-1752）");
        }

        /// <summary>
        /// 第三人称机位的运行时自证行（数值类证据）：相机 ↔ 目标眼位的水平距离 / 三维距离 / 理想值 / 下限 /
        /// 本帧射线命中点距离。判据 = 在 [0.762, 2.8448] m 内、且贴墙时收缩。
        /// </summary>
        private void LogSpectateDiagnostics()
        {
            if (_camera == null || !_camera.enabled) return;
            var target = IsThirdPersonSpectate() ? _match.SpectateTarget : null;
            if (target == null) return;

            var eye = target.Position + new Vector3(0f, target.EyeHeight, 0f);
            var camPos = _camera.transform.position;
            var dh = new Vector2(camPos.x - eye.x, camPos.z - eye.z).magnitude;
            var d3 = Vector3.Distance(camPos, eye);
            _log.Always($"第三人称机位：相机↔目标眼位 水平距离={dh:F4} m（三维 {d3:F4} m）" +
                        $"，理想={CsConst.ChaseDistance:F4} 下限={CsConst.ChaseMinDistance:F4} " +
                        $"当前距离={_chaseDistance:F4} m，本帧射线命中点={(_chaseHitDistance >= 0f ? _chaseHitDistance.ToString("F4") : "无（未贴墙）")}" +
                        "（cl_chasedist 112 unit + CAM_MIN_DIST 30 unit + V_GetChaseOrigin）");
        }

        /// <summary>相机是否正处于第三人称观战（观战中且拿到了被观察者）。</summary>
        private bool IsThirdPersonSpectate()
        {
            if (_match == null || !_match.IsSpectating) return false;
            return _match.SpectateTarget != null;
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
        /// 比赛运行时关掉场景里别的"主相机"（agent-02 生成舞台时可能带一台预览相机）。
        /// 引擎的 UI Canvas 是 <c>ScreenSpaceOverlay</c>，不依赖相机，因此关掉它们不会影响 HUD。
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
                if (_handledForeignCameras.Add(c))
                {
                    _log.Always($"舞台里另有一台主相机「{c.name}」已关闭（第一人称相机接管画面）");
                }
            }
        }

        /// <summary>
        /// 隐藏"当前不该看见的模型"：
        /// <list type="bullet">
        /// <item><b>第一人称</b>（不观战）= 本地玩家自己 —— 第一人称不能看见自己的身体
        /// （否则镜头被自己的后脑勺糊满）；</item>
        /// <item><b>第三人称观战中</b> = **谁都不藏**：相机在目标身后 2.8448 m 处（见
        /// <see cref="PrepareView"/> 的第三人称分支），被观察者的模型必须看得见 —— 藏了就没人可看。
        /// 这条分支在 <see cref="HideOwnBody"/> 开头就 return 掉了。</item>
        /// </list>
        ///
        /// <para>依据契约：角色的模型由 agent-07 生成，并在受击体上挂 <see cref="CsHitboxProxy"/> +
        /// <c>ActorId</c>。这里把 <c>ActorId == 目标</c> 的那些受击体节点（含子节点）的 <c>Renderer</c>
        /// 关掉 —— 只影响这一个 actor，队友/敌人的模型照常显示。</para>
        ///
        /// <para>本方法由 1 秒一次的自检调用（兜住"角色视图被重建"这类情况）；
        /// <b>切换观战目标的即时还原</b>在 <see cref="PrepareView"/> 里做（相机换目标的同一帧）。</para>
        ///
        /// <para>若你（agent-07）做的是"独立的第一人称手臂模型"，它不带 <c>CsHitboxProxy</c>，
        /// 因此不会被这里关掉。</para>
        /// </summary>
        private void HideOwnBody()
        {
            if (_match == null) return;
            if (_camera == null || !_camera.enabled) return;

            // 第三人称观战中：相机在目标**身后**，必须把被观察者的模型露出来（藏了就没东西可看）；
            // 只把之前（第一人称时期）藏过的还回去。
            if (IsThirdPersonSpectate())
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
        /// <c>if( abs( camAngles[2] - cam_idealdist-&gt;value ) &lt; 2.0 ) camAngles[2] = cam_idealdist-&gt;value;
        /// else camAngles[2] += ( cam_idealdist-&gt;value - camAngles[2] ) / 4.0;</c>
        /// ⇒ 差值 &lt; 2.0 unit（0.0508 m）直接吸附，否则每帧朝理想值移 1/4。
        /// ⛔ 这不是"手感参数"，是原版的收敛公式（本工程不许凭空定平滑常数）。</para>
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
        //  数学（已下沉为引擎件）
        // ==================================================================
        // 本组件原先自带的三个纯函数已下沉为**引擎件** `CloverEngine.CameraMath`（E-core-18）：
        //   · `FovYFromFovX`：水平 → 垂直 FOV（原版 `CalcFov` 的等价式，出处
        //     `HLSDK/cl_dll/view.cpp:1737-1752`；越界回退 90、`aspect<=0` 原样返回）；
        //   · `AimDirection`：yaw/pitch（度）→ 单位方向向量（yaw 0 = +Z、pitch + = 抬头）；
        //   · `Follow`：指数平滑跟随。原 `FollowRecoil` 只是"按 `|target| > |current|` 在上升/回落
        //     两个 tau 里挑一个"，属业务数值选择 ⇒ 按 `结构规则.md` §4.4「已有能力不准再起第二套」
        //     **合并成一条 `Follow`**，挑 tau 那一步内联在 `PrepareView` 的后坐力分支里。
        // ⛔ 本组件不许再留同名 `private static` 副本 —— 判据：全项目搜
        //    `FovYFromFovX|ComputeAimDirection|FollowRecoil`，命中只应出现在 `CameraMath.Xxx(...)` 调用处。
    }
}
