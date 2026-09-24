using CloverEngine;
using Cs16.Core;
using Cs16.Module.CameraRig;
using Cs16.Module.Combat;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Player
{
    /// <summary>
    /// 第一人称操作的**装配点与每帧驱动**：把 <see cref="PlayerMotor"/>（输入/视角）、
    /// <see cref="FirstPersonCamera"/>（相机）、<see cref="ViewBob"/>、<see cref="CombatModule"/>（射击）
    /// 串成一条确定的执行链。
    ///
    /// <para><b>执行顺序是这个模块的正确性前提，不能改</b>：</para>
    /// <code>
    /// PlayerModule.Update   (order -200)  采集输入 → match.SetLocalInput(cmd)      ← 必须早于模拟 Tick
    /// MatchModule.Update    (order    0)  模拟推进：扣弹/限速/累后坐力/移动解算    ← agent-03
    /// PlayerModule.LateUpdate             ① 相机算位姿与视线 ② 射击队列消费+射线 ③ 写相机
    /// </code>
    /// <para>放反了会得到两种典型故障：输入慢一帧（开火"点了没反应"）、以及"射线打的是上一帧的眼睛位置"。</para>
    ///
    /// <para><b>不碰契约</b>：本模块只通过 <see cref="ICsMatch"/> 读写状态；
    /// 不写 <c>CsActor.Position</c>（移动归模拟）、不做伤害结算（归 agent-03）、不画 HUD（归 agent-06）。</para>
    ///
    /// <para><b>鼠标光标</b>：引擎没有光标 API，且 <c>Game.Input.Lock()</c> 的语义是"**让所有按键读取失效**"
    /// （<c>InputManager.GetKey</c> 在 <c>IsLocked</c> 时恒返回 false），拿它当"锁定光标"会让游戏完全失去操作。
    /// 所以这里用 <c>UnityEngine.Cursor</c>（平台显示设置，不是输入读取）来捕获/释放光标，
    /// 由"是否处于可操作状态"驱动。</para>
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public sealed class PlayerModule : MonoBehaviour
    {
        private const string Tag = "Player";

        /// <summary>本模块的 Update 执行顺序：必须早于 MatchModule（默认 0）。</summary>
        public const int ExecutionOrder = -200;

        /// <summary>
        /// 常驻 HUD 面板名（`Resources/UI/HudPanel`，agent-06）——它整个比赛都开着，
        /// **不能**被当成"挡住了操作"。（与 AppFlow 的判据一致：除 HUD 外还有面板 = 有遮挡。）
        /// </summary>
        private const string HudPanelName = "HudPanel";

        /// <summary>
        /// 开着也不需要鼠标、也不该停掉操作的界面（官方 CS 里这些界面上照样能移动/转视角）。
        /// 其余面板（买枪菜单 / H 菜单 / 控制台 / 无线电 / 比赛结束）一律按"模态"处理：释放光标 + 停掉操作。
        ///
        /// <para>用名字而不是类型，是为了不引用 `Cs16.UI`（分层铁律：模块不引用 UI 层；
        /// 面板名与预制体名同源 —— 见 <c>Game.UI.Open&lt;T&gt;()</c> 的解析规则）。</para>
        /// </summary>
        private static readonly string[] NonBlockingPanels =
        {
            HudPanelName,      // 常驻 HUD
            "ScoreboardPanel", // TAB 记分板（按住看，不打断操作）
            "RoundEndPanel",   // 回合结算（官方在同一时刻仍可移动）
        };

        // ---- 玩家设置键：唯一真源 = `Core/CsSettingsKeys`（契约层）----
        // 改前这里自持一份字面量、UI 层的 `CsPlayerSettingsStore` 另持一份 ⇒ 两处漂移时不报错，
        // 只表现为"设置改了不生效"（本模块读到的是默认值）。分层禁止 Module 引用 UI，
        // 所以真源落在两边都能引用的 Core（见 CsSettingsKeys 的类注释）。

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        private MatchModule _matchModule;
        private ICsMatch _match;

        private PlayerMotor _motor;
        private FirstPersonCamera _view;
        private ViewBob _bob;
        private CombatModule _combat;

        private bool _ready;
        private bool _wasRunning;
        private bool _cursorFree = true;

        private readonly System.Collections.Generic.HashSet<string> _openPanels =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>观战/输入相关的"只报一次"告警闸（见 <see cref="PollSpectateInput"/>）。</summary>
        private bool _warnedNoInput;

        // ---- 已应用设置的镜像（用于"只有真的改了才打日志"）----
        private float _appliedSensitivity = CsConst.DefaultSensitivity;
        private bool _appliedInvertY;
        private bool _appliedAutoReload = true;
        private int _appliedFov = (int)CsConst.DefaultFov;

        // ==================================================================
        //  装配
        // ==================================================================
        private void Start()
        {
            _matchModule = GetComponent<MatchModule>();
            if (_matchModule == null)
            {
                // 不静默：拿不到比赛门面 = 这一整块操作与射击都不可能工作。
                Game.Logger.Error(Tag,
                    "PlayerModule 拿不到 MatchModule（应与本组件挂在同一个 GameObject 上、由 Bootstrap 装配）。" +
                    "第一人称操作与射击已被禁用。");
                enabled = false;
                return;
            }

            _match = _matchModule.Match;
            if (_match == null)
            {
                Game.Logger.Error(Tag, "MatchModule.Match 为 null（门面未就绪），第一人称操作与射击已被禁用。");
                enabled = false;
                return;
            }

            // 全部挂在本对象上（Bootstrap 的常驻对象）：这几个组件 themselves 都不需要自己的 Transform，
            // 真正需要位置的只有 FirstPersonCamera 自建的那台相机。
            // 视点晃动是**引擎件**（CloverEngine.ViewBob：纯逻辑类，不是 MonoBehaviour）⇒ 直接 new，
            // 数值口径在本文件里传满（见 BuildViewBobConfig —— 7 个数值逐字沿用项目原常量）。
            _bob = new ViewBob { Config = BuildViewBobConfig() };
            _motor = gameObject.AddComponent<PlayerMotor>();
            _view = gameObject.AddComponent<FirstPersonCamera>();
            _combat = gameObject.AddComponent<CombatModule>();

            _motor.Init(_match);
            _bob.Reset();
            _view.Init(_match, _motor, _bob, ReadFov());
            _combat.Init(_match, _view);

            ApplyRuntimeSettings(force: true);
            SubscribePanels();

            _ready = true;
            _log.Always($"PlayerModule 就绪：motor/camera/bob/combat 已装配（执行顺序 {ExecutionOrder}，" +
                        $"输入后端 {SafeBackendName()}）");
        }

        /// <summary>
        /// 视点晃动的**数值口径**：7 个数值**逐字沿用**原 <c>Module/CameraRig/ViewBob.cs</c> 里写死的常量
        /// （下沉到引擎后改为"业务传配置" —— ⛔ 数值一个都没动）：
        /// 满幅基准速度 = <see cref="CsConst.SpeedRifle"/>（4.4 m/s，拿"步枪速度"当满幅基准）、
        /// 幅度 = <see cref="CsConst.ViewBobAmount"/>、频率 = <see cref="CsConst.ViewBobSpeed"/>、
        /// 振幅平滑 / 横滚角 / 落地沉降两组四个来自 <see cref="CsCombatTuning"/>。
        /// </summary>
        private static ViewBobConfig BuildViewBobConfig()
        {
            return new ViewBobConfig
            {
                FullAmplitudeSpeed = CsConst.SpeedRifle,
                Amount = CsConst.ViewBobAmount,
                Speed = CsConst.ViewBobSpeed,
                BlendSpeed = CsCombatTuning.BobBlendSpeed,
                LandDipAmount = CsCombatTuning.LandDipAmount,
                LandDipTau = CsCombatTuning.LandDipTau,
                RollDegrees = CsCombatTuning.BobRollDegrees,
            };
        }

        private void OnDestroy()
        {
            // 退出 Play / 换场景时把光标还回去（编辑器里被"锁着"的光标很难受）。
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private string SafeBackendName()
        {
            var input = Game.Input;
            if (input == null) return "<no input>";
            return input.BackendName + (input.Available ? string.Empty : "（不可用！）");
        }

        private void SubscribePanels()
        {
            var ui = Game.UI;
            if (ui == null)
            {
                _log.Error("ui.missing",
                    "Game.UI 为 null（表现域未挂载？）：无法感知「菜单是否打开」，鼠标光标将一直保持捕获");
                return;
            }

            // 具名方法（引擎的事件/面板回调都要求同一引用才能注销）。
            ui.OnPanelOpened(OnPanelOpened);
            ui.OnPanelClosed(OnPanelClosed);
        }

        // ==================================================================
        //  每帧
        // ==================================================================
        private void Update()
        {
            // `_ready` 只代表"装配完成"，**不代表门面还在**：门面可能因为脚本执行顺序 / 重建而变空，
            // 一旦为空而这里不判，就是每帧一条 NullReferenceException 把 Console 刷爆（实测踩过）。
            if (!_ready || _match == null) return;

            var match = _match;
            if (match.IsRunning != _wasRunning)
            {
                _wasRunning = match.IsRunning;
                _log.Always(_wasRunning
                    ? $"比赛开始，第一人称操作接管：玩家={DescribeLocal(match)}"
                    : "比赛结束/未开局，第一人称操作进入待机");
            }

            ResolveInputGates(out var blockLook, out var blockMove);
            PollSpectateInput();

            var cmd = _motor.BuildInput(blockLook, blockMove);
            _combat.FillInput(ref cmd, inputAllowed: !blockLook);

            // 下发本帧意图（模拟在它自己的 Update 里消费；本地模拟 = 零延迟）。
            match.SetLocalInput(cmd);

            UpdateCursor(blockLook);
        }

        private void LateUpdate()
        {
            if (!_ready) return;

            // 时钟取 `Core/CsClock`（本帧由 MatchModule.Update 驱动过），**不再直接读 Time.deltaTime**：
            // 表现侧（相机 / 射击表现）与模拟侧必须同一步长，否则注入假时钟时会出现
            // "模拟快进了、表现没跟上"（换弹/切枪/开镜稳定的观感全乱）。出处见 CsClock 类注释。
            var dt = CsClock.Delta;

            // ① 相机先算好"眼睛在哪、看哪"；② 射击用同一个方向与起点射线；③ 再把位姿写进相机。
            // 顺序不能换：射线方向必须与画面上准星所指完全一致（含后坐力抬升）。
            _view.PrepareView(dt);
            _combat.LateTick(dt, _view.EyePosition, _view.AimDirection);
            _view.ApplyToTransform();
        }

        /// <summary>
        /// 观战切目标：<c>[空格]</c> → <see cref="Events.SpectateNext"/>。
        ///
        /// <para><b>为什么在这里读键</b>：规格 H11 的提示「[空格] 切换目标」由 <c>UI/InGame/CsSpectatorWidget</c>
        /// 显示，但按分层铁律 UI 不许直接调 <c>ICsMatch.SpectateNext</c> —— 键位必须由操作模块采集后
        /// 经事件总线转给 <c>MatchModule</c>（它订阅 <see cref="Events.SpectateNext"/> 再调
        /// <c>ICsMatch.SpectateNext(+1)</c>）。原先无人发这个事件 ⇒ 提示是空话。</para>
        ///
        /// <para><b>只在观战中发</b>：还活着时空格是跳跃（<c>PlayerMotor.BuildInput</c> 也在读它），
        /// 这里以 <c>ICsMatch.IsSpectating</c> 为闸，平时一次事件都不发。</para>
        /// </summary>
        private void PollSpectateInput()
        {
            var match = _match;
            if (match == null || !match.IsSpectating) return;

            var input = Game.Input;
            if (input == null)
            {
                // 非预期分支：没有输入后端就永远切不了观战目标（照 §7 留痕，降频只报一次）
                if (!_warnedNoInput) { _warnedNoInput = true; _log.Warn("spectate.noinput", "Game.Input 为 null，观战切换目标（空格）无法采集"); }
                return;
            }

            if (!input.GetKeyDown(GameKey.Space)) return;

            var bus = Game.Event;
            if (bus == null)
            {
                if (!_warnedNoInput) { _warnedNoInput = true; _log.Warn("spectate.noevent", "Game.Event 为 null，观战切换目标事件发不出去"); }
                return;
            }

            var cur = match.SpectateTarget;
            _log.Info("spectate.next", $"观战中按下 [空格] → 请求切换观战目标（当前 {cur?.Name ?? "<无>"}）");
            bus.Emit(Events.SpectateNext);
        }

        /// <summary>
        /// 计算两类输入闸门：
        /// <list type="bullet">
        /// <item><paramref name="blockLook"/>：面板/暂停/比赛未开始时为 true —— 连鼠标转视角都禁掉，
        /// 并把光标交还给鼠标（这些界面要能点）。</item>
        /// <item><paramref name="blockMove"/>：除上面之外，冻结期（买枪）/回合结算/比赛结束/死亡也不产生移动，
        /// 但**保留转视角**（官方 CS 在冻结期就能转视角）。</item>
        /// </list>
        /// </summary>
        private void ResolveInputGates(out bool blockLook, out bool blockMove)
        {
            blockLook = true;
            blockMove = true;

            var match = _match;
            if (match == null || !match.IsRunning) return;      // 未开局（主菜单 / 读条 / 已回主菜单）
            if (match.IsPaused) return;                         // 暂停族（含从暂停里打开的 Options）
            if (HasBlockingOverlay()) return;                   // 买枪菜单 / H 菜单 / 控制台…（模态界面）

            blockLook = false;

            var phase = match.Phase;
            if (phase != CsRoundPhase.Live && phase != CsRoundPhase.Freeze) return;  // None / RoundEnd / MatchEnd

            var local = match.LocalPlayer;
            if (local != null && local.IsAlive && phase == CsRoundPhase.Live) blockMove = false;
        }

        private static string DescribeLocal(ICsMatch match)
        {
            var local = match.LocalPlayer;
            return local == null ? "<null>" : $"{local.Name}(id={local.Id}, {local.Team})";
        }

        // ==================================================================
        //  光标
        // ==================================================================
        /// <summary>
        /// 只有"状态真的变了"才动光标（技能要求：不要每帧 Lock/Unlock 抖动）。
        /// 捕获 = 第一人称操作中；释放 = 有任何需要鼠标的界面 / 不在比赛中。
        /// </summary>
        private void UpdateCursor(bool free)
        {
            if (_cursorFree == free) return;
            _cursorFree = free;

            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;

            _log.Info("cursor", free ? "释放鼠标光标（菜单/暂停/非比赛状态）" : "捕获鼠标光标（第一人称操作）");
        }

        // ==================================================================
        //  面板感知
        // ==================================================================
        private void OnPanelOpened(string panelName)
        {
            if (this == null || string.IsNullOrEmpty(panelName)) return;
            if (_openPanels.Add(panelName)) _log.Info("panel", $"面板打开：{panelName}（当前 {_openPanels.Count} 个）");
        }

        private void OnPanelClosed(string panelName)
        {
            if (this == null || string.IsNullOrEmpty(panelName)) return;
            if (_openPanels.Remove(panelName)) _log.Info("panel", $"面板关闭：{panelName}（当前 {_openPanels.Count} 个）");

            // Options 面板改完设置后（或者任何面板关闭时）重新读一次设置：灵敏度 / FOV / 自动换弹即时生效。
            ApplyRuntimeSettings(force: false);
        }

        /// <summary>是否有"挡住操作"的面板（常驻 HUD / 记分板 / 回合结算不算）。</summary>
        private bool HasBlockingOverlay()
        {
            if (_openPanels.Count == 0) return false;

            foreach (var name in _openPanels)
            {
                var nonBlocking = false;
                for (var i = 0; i < NonBlockingPanels.Length; i++)
                {
                    if (name == NonBlockingPanels[i]) { nonBlocking = true; break; }
                }

                if (!nonBlocking) return true;
            }

            return false;
        }

        // ==================================================================
        //  玩家设置（Game.Setting 是唯一真源，键与 agent-01 的 UI 层实现一致）
        // ==================================================================
        private int ReadFov()
        {
            var setting = Game.Setting;
            if (setting == null) return (int)CsConst.DefaultFov;
            return setting.Get(CsSettingsKeys.Fov, (int)CsConst.DefaultFov);
        }

        private void ApplyRuntimeSettings(bool force)
        {
            if (_motor == null) return;

            var setting = Game.Setting;
            if (setting == null)
            {
                if (force) _log.Warn("settings.missing", "Game.Setting 为 null（未 Launch？），操作设置沿用默认值");
                return;
            }

            var sensitivity = setting.Get(CsSettingsKeys.Sensitivity, CsConst.DefaultSensitivity);
            var invertY = setting.Get(CsSettingsKeys.InvertY, false);
            var autoReload = setting.Get(CsSettingsKeys.AutoReload, true);
            var fov = setting.Get(CsSettingsKeys.Fov, (int)CsConst.DefaultFov);

            var changed = force
                          || !Mathf.Approximately(sensitivity, _appliedSensitivity)
                          || invertY != _appliedInvertY
                          || autoReload != _appliedAutoReload
                          || fov != _appliedFov;

            _appliedSensitivity = sensitivity;
            _appliedInvertY = invertY;
            _appliedAutoReload = autoReload;
            _appliedFov = fov;

            _motor.ApplySettings(sensitivity, invertY);
            _combat.ApplySettings(autoReload);
            _view.SetBaseFov(fov);

            if (changed)
            {
                _log.Always($"操作设置已应用：灵敏度={sensitivity:0.##} 反转Y={invertY} " +
                            $"自动换弹={autoReload} FOV={fov}");
            }
        }
    }
}
