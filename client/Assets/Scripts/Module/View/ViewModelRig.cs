using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    /// **第一人称武器视图**：把 CS 1.6 的 <c>v_*</c> 模型挂到相机下，并播放**原版自己的**
    /// 逐帧动画序列（idle / shoot1..3 / shoot_empty(shootlast) / reload / draw）。
    ///
    /// <para><b>为什么不再有"程序化后坐/换弹下沉/切枪抬起"</b>：那些是"没有帧动画"时期的替代品
    /// 序列搬进来了 ⇒ 后坐、换弹、抬起全部由原版 <c>shoot*/reload/draw</c> 序列表达，
    /// 叠加程序化位移就是"动两次"。序列的 fps / 帧数一律取 mdl 实测（见
    /// <c>原版资源/cs16src/cs16_anim.py</c> 的导出日志）。</para>
    ///
    /// <para><b>为什么不需要"摆到右下角"的魔数</b>：CS 1.6 的 <c>v_*</c> 模型是在**相机空间**里建模的 ——
    /// 模型自带的坐标就已经把枪放到了右下（实测 <c>v_ak47</c> idle 姿态包围盒
    /// x∈[0.03,0.24] 偏右、y∈[-0.29,-0.06] 在准星下方、z∈[0.09,0.71] 向前），
    /// GoldSrc 也把模型原点直接放在相机原点 ⇒ 左右/前后都不需要加偏移。</para>
    ///
    /// <para><b>但上下有一个原版的固定偏移</b>：原版把视图原点放到眼位之后又下推 1 unit
    /// （<c>HLSDK/cl_dll/view.cpp:665</c> <c>view-&gt;origin[2] -= 1;</c>）⇒
    /// <see cref="CsViewTuning.ViewModelLocalPosition"/> 的 y = −0.0254 m。原因见那里的注释。</para>
    ///
    /// <para><b>开火/换弹怎么感知（只读）</b>：不去抢 <c>CombatModule</c> 的射击队列（那是它独占消费的），
    /// 而是观察权威状态的变化：<c>CsActor.NextFireTime</c> 前推 = 打出了一发；
    /// <c>ReloadEndTime</c> 前推 = 开始换弹；<c>ActiveWeapon</c> 变化 = 切枪（播 <c>draw</c>）。
    /// 这样"表现"与"模拟"永远一致，也不会多打一发子弹。</para>
    /// </summary>
    public sealed class ViewModelRig : MonoBehaviour
    {
        private const string Tag = "View";

        private ICsMatch _match;
        private ViewModule _owner;
        private CsModuleLog _log;

        private GameObject _model;
        private string _modelKey;
        private Transform _modelRoot;

        /// <summary>第一人称武器模型实例的池分组（仅作标签，ClearGroup 用）。</summary>
        private const string PoolGroupViewModel = "cs16.viewmodel";

        // ---- 只读快照（用于检测"发生了什么"）----
        private float _preNextFireTime;
        private int _preReloadSeq;
        private string _preWeapon;
        private bool _haveSnapshot;

        /// <summary>
        ///
        /// <para><b>为什么必须跟踪身份，而不只是序号</b>：换弹边沿原来的唯一信号是
        /// <c>CsActor.ReloadSeq</c>「变了没」。但 <c>ReloadSeq</c> 是**挂在 actor 对象上**的字段 ——
        /// 一旦对象被换掉（本工程的 <c>CsMatch</c> 在局/回合切换处 <c>new CsActor</c>），新对象的
        /// <c>ReloadSeq</c> 从 0 重新开始 ⇒ 第一次换弹是 <c>0→1</c>，而 <c>_preReloadSeq</c> 还停在旧对象的
        /// <c>1</c> ⇒ <b><c>1 == 1</c>，边沿被判为"没变"，整段换弹动画静默丢掉</b>。</para>
        ///
        /// <para>用户 2026-09-24 亲身实测的日志正好是这个形状：全天 <c>Player 开始换弹</c> 25 次，
        /// <c>第一人称播换弹</c> 只有 2 次，**且这 2 次都是 <c>seq=1</c>**（= 只有"基线还是 0"
        /// 的那个对象的第一发换弹被看见）。</para>
        /// </summary>
        private CsActor _preLocal;

        /// <summary>
        /// 上一帧的**消音器状态位**与它当时所属的武器 —— 两者合起来才判得出"这一帧按右键换了态"
        /// （只看状态位会在切枪时误判，见 <see cref="UpdateAnim"/> 的消音器段）。
        /// </summary>
        private bool _preSilenced;
        private string _preSilenceWeapon;

        /// <summary>当前是否处于"reload 覆盖态"（见 <see cref="UpdateAnim"/> 的换弹段）。</summary>
        private bool _reloadAnimActive;

        /// <summary>第一人称播过几次换弹（差异 #72 的断言用）。</summary>
        private int _reloadAnimsPlayed;

        /// <summary>第一人称播过几次换弹（差异 #72 的断言口：成功换弹一次 ⇒ +1）。</summary>
        public int ReloadAnimsPlayed => _reloadAnimsPlayed;

        // ---- 动画状态 ----
        private Animator _animator;
        private IAnimPlayer _anim;
        private string _overrideState;      // 一次性动作（开火/换弹/抬起）占用的状态
        private string _lastState;          // 当前状态（避免每帧重复 CrossFade）
        private int _shotIndex;             // 连发时在 shoot1→2→3 之间轮换（与原版一致）
        private bool _stateWarned;

        private float _missingLogged;

        /// <summary>当前宿主相机（<see cref="ViewModule"/> 用它判断是否需要重绑）。</summary>
        public Camera Host { get; private set; }

        /// <summary>当前武器模型资源路径（诊断用）。</summary>
        public string CurrentModelKey => _modelKey;

        /// <summary>当前武器 id。</summary>
        public string CurrentWeaponId { get; private set; }

        // ==================================================================
        //  装配 / 拆除
        // ==================================================================
        internal void Init(ICsMatch match, ViewModule owner)
        {
            _match = match;
            _owner = owner;
            _log = owner != null ? owner.Log : new CsModuleLog(Tag);
            Host = GetComponent<Camera>();

            var rootGo = new GameObject("CsViewModel");
            rootGo.transform.SetParent(transform, false);
            _modelRoot = rootGo.transform;
            ApplyRigTransform();

            _log.Always($"第一人称武器视图就绪：宿主相机={name}，模型原点放在相机原点下方 " +
                        $"{CsViewTuning.ViewModelLocalPosition.y:F4} m（原版 view.cpp:665 的 view->origin[2] -= 1 unit）；" +
                        "后坐/换弹/抬起走原版序列");
        }

        /// <summary>解绑/换相机时清掉模型（避免把枪留在菜单相机上）。</summary>
        internal void Teardown()
        {
            DespawnModel();
            _modelKey = null;
            CurrentWeaponId = null;
            _anim = null;
            _animator = null;
            ClearOverride("teardown");
            _haveSnapshot = false;
            _preLocal = null;              // 换相机后本地玩家基线一并作废
            _preReloadSeq = 0;
            _reloadAnimActive = false;
            _reloadProbeAt = 0f;
            _reloadProbeState = null;
            if (_modelRoot != null) Destroy(_modelRoot.gameObject);
            _modelRoot = null;
        }

        private void OnDestroy() => Teardown();

        // ==================================================================
        //  每帧
        // ==================================================================
        internal void Tick()
        {
            var match = _match;
            if (match == null || !match.IsRunning)
            {
                DiagReload(match, null, "闸门A !IsRunning");
                SetModelVisible(false);
                return;
            }

            var local = match.LocalPlayer;
            if (local == null)
            {
                DiagReload(match, null, "闸门B LocalPlayer==null");
                SetModelVisible(false);
                return;
            }

            // 视模型的**主角**：本地玩家；**观战档位 4（OBS_IN_EYE）时 = 被观察者** ——
            // 原版把被观察者当前武器的视模型塞进 GetViewModel() 并播 idle
            //（view.cpp:1483-1514：V_FindViewModelByWeaponModel(ent->curstate.weaponmodel) + pfnWeaponAnim(0,0)）。
            var actor = local;
            if (match.IsSpectating && Cs16.Core.CsSpectator.Mode == Cs16.Core.CsSpectatorMode.FirstPerson)
            {
                var t = match.SpectateTarget;
                if (t != null) actor = t;
            }

            if (!actor.IsAlive || match.IsZoomed)
            {
                // 观战 / 死亡：把枪收起来（CS 里观战没有第一人称武器）。
                // 开镜（狙击枪）：原版开镜后**根本不画视模型** —— 画面里只有镜筒遮罩与十字线
                //（出处：原版开镜实机原图 原版资源/参照物/viewmodel/viewmodel_awp_scoped.png，
                // 画面内没有任何枪身）⇒ 这里同样收起，遮罩另由 UI/InGame/CsScopeOverlayWidget 画。
                DiagReload(match, actor,
                    $"闸门C IsSpectating={match.IsSpectating} 主角={actor.Name} IsAlive={actor.IsAlive} IsZoomed={match.IsZoomed}");
                SetModelVisible(false);
                ResetSnapshot(actor);
                return;
            }

            SetModelVisible(true);
            SyncModel(actor);
            DiagReload(match, actor, "运行");
            UpdateAnim(actor);
            ApplyBob(actor);
            ProbeReloadAnim();
            SettleReload();
            CheckOverrideFinished();
            TraceOverride();
        }

        // ==================================================================
        //  覆盖态（一次性动作）的结束判定
        // ==================================================================
        /// <summary>
        /// 覆盖态 = 「一个一次性动作（抬起 / 开火 / 换弹）正在播，播完之前不回 idle」。
        /// 清它有两个口子：引擎的 OnComplete 回调（<see cref="OnClipFinished"/>）与这里的逐帧比对，
        /// 互为冗余。
        ///
        /// <para><b>为什么逐帧比对不能省</b>：<c>_anim.Play()</c> 之后的**第一帧**
        /// <c>GetCurrentAnimatorStateInfo(0)</c> 仍描述**上一段**剪辑（引擎 <c>Animation.cs</c> 的
        /// <c>Play()</c> 只是把 <c>_completeFired</c> 归 false，状态机的切换要等下一次求值）。
        /// 若上一段恰好已经播完（归一化时间 ≥ 1），引擎会在那一帧就派发一次 OnComplete ——
        /// 那次派发属于上一段剪辑，被 <see cref="OnClipFinished"/> 的陈旧判定挡掉，
        /// 而引擎的"已派发"标志已经被消费掉；这一段剪辑真正播完时归一化时间仍 ≥ 1，
        /// 引擎的"小于 1 就重新武装"那一行也不会触发 ⇒ <b>完成回调可能再也不来</b>，
        /// <c>_overrideState</c> 就停在原地（枪停在那一段的末帧）。
        /// 逐帧读 Animator 的真状态不经过那个标志，因此不受它影响。</para>
        ///
        /// <para><b>判据</b>（全部要先满足"这一段确实在播"= 观察到过归一化时间 &lt; 1，见
        /// <c>_overrideSeen</c> —— 它同时挡住"Play 之后那一两帧读到的仍是上一段"与
        /// "同一段被连着播两次时读到的是上一次的收尾"这两种假结束）：
        /// ① 报的就是覆盖态这一段且已播到头（归一化时间 ≥ 1）⇒ 结束；
        /// ② 它现在报的是别的状态 ⇒ 这一段离开了状态机 ⇒ 结束；
        /// ③ 以上都不成立、且占用时间超过「片段时长（没读到则用 <see cref="OverrideMaxSeconds"/>）+
        /// <see cref="OverrideStuckGrace"/>」⇒ 强制结束并留 Warn（非预期分支）。</para>
        /// </summary>
        private void CheckOverrideFinished()
        {
            if (_overrideState == null || _animator == null) return;

            var si = _animator.GetCurrentAnimatorStateInfo(0);
            if (si.IsName(_overrideState))
            {
                if (si.normalizedTime < 1f)
                {
                    _overrideSeen = true;
                    if (si.length > 0f) _overrideLen = si.length;
                }
                else if (_overrideSeen)
                {
                    ClearOverride("stateDone");
                    return;
                }
            }
            else if (_overrideSeen)
            {
                ClearOverride("stateLeft");
                return;
            }

            if (IsOverrideOverdue())
            {
                _log.Warn("viewmodel.override.timeout",
                    $"一次性动作 {_overrideState} 已占用 {Time.time - _overrideSetAt:F2}s" +
                    $"（上限 {OverrideLimit:F2}s）仍未拿到完成回调，Animator 报的是 {CurrentStateName(si)}" +
                    $"（归一化时间 {si.normalizedTime:F2}）⇒ 强制归位");
                _overrideTimeoutAt = Time.time;
                ClearOverride("timeout");
            }
        }

        /// <summary>覆盖态允许占用的上限（秒）：片段时长（没读到则 <see cref="OverrideMaxSeconds"/>）+ 宽限。</summary>
        private float OverrideLimit => (_overrideLen > 0f ? _overrideLen : OverrideMaxSeconds) + OverrideStuckGrace;

        /// <summary>
        /// 覆盖态是不是**超期未清**（占用时间超过 <see cref="OverrideLimit"/>）。
        /// 这是"真卡住"的硬判据：判据 J-R6 与逐帧清态两条路都用它，⛔ 不许放宽。
        /// </summary>
        private bool IsOverrideOverdue()
        {
            if (_overrideState == null) return false;
            return Time.time - _overrideSetAt > OverrideLimit;
        }

        /// <summary>状态名是不是换弹那一段（<see cref="CsViewTuning.VmStateReload"/> 的两态候选表）。</summary>
        private static bool IsReloadState(string state)
        {
            if (state == null) return false;
            if (InTable(CsViewTuning.VmStateReload, state)) return true;
            return InTable(CsViewTuning.VmStateReloadUnsil, state);
        }

        private static bool InTable(string[] table, string state)
        {
            for (var i = 0; i < table.Length; i++)
            {
                if (string.Equals(table[i], state, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 按**当前消音状态**在"装消音 / 未装消音"两套候选里挑一套（只有 <c>CanSilence</c> 的武器分两套）。
        ///
        /// <para>两套的出处 = <c>v_usp.mdl</c> 的序列 0..6（装）/ 8..14（未装），逐条见
        /// <see cref="CsViewTuning.VmStateIdleUnsil"/> 的注释。两张候选表都是"先自己那一条、再回落到
        /// 装消音那一套" ⇒ 未装档的 clip 还没导出时，挑不到就回落，行为与原来完全一致。</para>
        /// </summary>
        private static string[] SilenceVariant(string[] silenced, string[] unsil, CsActor local)
        {
            var def = local != null ? local.ActiveDef : null;
            if (def == null || !def.CanSilence) return silenced;
            return local.Silenced ? silenced : unsil;
        }

        /// <summary>下发一个覆盖态（一次性动作开始）：记录起点与"片段时长未知"。</summary>
        private void SetOverride(string state)
        {
            _overrideState = state;
            _overrideSeen = false;
            _overrideLen = 0f;
            _overrideSetAt = Time.time;
            _overrideTimeoutAt = 0f;
        }

        /// <summary>清掉覆盖态并让下一帧回到 idle；<paramref name="why"/> 只用于诊断读数。</summary>
        private void ClearOverride(string why)
        {
            _overrideClearedBy = why;
            _overrideState = null;
            _lastState = null;      // 强制下一帧回到 idle
        }

        /// <summary>Animator 是否已经真的切到本次覆盖态（跨过 Play 之后那一两帧的陈旧读数）。</summary>
        private bool _overrideSeen;

        /// <summary>本次覆盖态下发的时刻（诊断 / 兜底判据用）。</summary>
        private float _overrideSetAt;

        /// <summary>本次覆盖态对应片段的时长（秒；<c>AnimatorStateInfo.length</c>，读到之前为 0）。</summary>
        private float _overrideLen;

        /// <summary>上一次覆盖态是被哪个口子清的（诊断读数）。</summary>
        private string _overrideClearedBy;

        /// <summary>一次性动作还没读到片段时长时允许占用的上限（秒）——最长的一次性动作是换弹
        /// （<c>vm_glock18_reload.anim</c> 2.171s）。</summary>
        private const float OverrideMaxSeconds = 3f;

        /// <summary>判"该清未清"前在片段时长上再加的宽限（秒）。</summary>
        private const float OverrideStuckGrace = 0.5f;

        // ==================================================================
        //  覆盖态读数（判据 J-R5 / J-R6 的原始证据）
        // ==================================================================
        /// <summary>把状态机的已知状态名反读出来（诊断用；各视模型控制器的状态名见 <c>CsViewTuning.VmState*</c>）。</summary>
        private static readonly string[] TraceStates =
        {
            "idle", "fire1", "fire2", "fire3", "firelast", "reload", "draw",
            "idle_unsil", "fire1_unsil", "fire2_unsil", "fire3_unsil", "firelast_unsil",
            "reload_unsil", "draw_unsil", "add_silencer", "detach_silencer",
        };

        /// <summary>当前 <see cref="AnimatorStateInfo"/> 对应的状态名；都不匹配 ⇒ <c>&lt;其他&gt;</c>。</summary>
        private string CurrentStateName(AnimatorStateInfo si)
        {
            for (var i = 0; i < TraceStates.Length; i++)
            {
                if (si.IsName(TraceStates[i])) return TraceStates[i];
            }
            return "<其他>";
        }

        /// <summary>上一次打进日志的读数（只在读数变化时打一行，避免逐帧刷屏）。</summary>
        private string _traceKey;

        /// <summary>
        /// 逐帧读「覆盖态 / Animator 真状态名 / 归一化时间到没到头」，**只在读数变化时**留一行。
        /// 为什么要它：<c>_overrideState</c> 卡住时，光看"末帧卡住"这个现象分不出卡的是哪一段、
        /// 状态机当时在报什么；逐帧读数把"清没清、谁清的"变成读得出来的字段。
        /// </summary>
        private void TraceOverride()
        {
            if (_animator == null) return;
            var si = _animator.GetCurrentAnimatorStateInfo(0);
            var cur = CurrentStateName(si);
            var key = (_overrideState ?? "-") + "|" + cur + "|" + (si.normalizedTime >= 1f ? "1" : "0");
            if (string.Equals(key, _traceKey, System.StringComparison.Ordinal)) return;
            _traceKey = key;
            _log.Always(
                $"viewmodel.override.trace t={Time.time:F2} over={_overrideState ?? "-"} si={cur} " +
                $"nT={si.normalizedTime:F2} len={si.length:F2} active={_reloadAnimActive} " +
                $"clearedBy={_overrideClearedBy ?? "-"} own={Time.time - _overrideSetAt:F2}");
        }

        /// <summary>
        ///
        /// <para><b>为什么非要有它（这一条比修 bug 更重要）</b>：
        /// <c>LogThrottle.InfoCounted</c> 的口径是「每 key **首次必打**，之后每
        /// <see cref="CsModuleLog.LogRateEvery"/> 次打一条」（<c>CsCombatTuning.LogRateEvery = 50</c>）。
        /// 换弹是 5.5 s 一次的低频事件 ⇒ <b>一次比赛里 17 次换弹只会打出 1 行</b>。
        /// 实测：<c>Player 开始换弹</c>（走不降频的 <c>Game.Logger.Info</c>）17 行，
        /// <c>第一人称播换弹</c>（走降频的 <c>CsModuleLog.Info</c>）**恰好 1 行** ——
        /// 两者的"1 : 17"**完全是量具造成的**，不是表现层漏播。
        /// 结论：**任何"计数一致"型判据都必须用不降频的口子**，否则判据本身是坏的。</para>
        ///
        /// <para>本方法在"播过换弹之后 <see cref="ReloadProbeDelay"/> 秒"取一次
        /// <c>Animator</c> 的真实状态：状态是不是 reload 剪辑、归一化时间有没有在走。
        /// 这是"帧动画真的在跑"的 L3 读数 —— 光有 <c>_anim.Play(...)</c> 那句调用**不算证据**。</para>
        /// </summary>
        private void ProbeReloadAnim()
        {
            if (_reloadProbeAt <= 0f || Time.time < _reloadProbeAt) return;
            _reloadProbeAt = 0f;

            var detail = "无 Animator（拿不到状态）";
            if (_animator != null)
            {
                var si = _animator.GetCurrentAnimatorStateInfo(0);
                var isReload = _reloadProbeState != null && si.IsName(_reloadProbeState);
                detail = $"是reload剪辑={isReload} 归一化时间={si.normalizedTime:F2} 速度={si.speed:F2} " +
                         $"（期望状态={_reloadProbeState ?? "-"}）";
            }
            // 豁免条款：**换弹中途切枪 = 换弹被取消**（CsInventory.SwitchWeapon 把 ReloadEndTime 归零），
            //    判定用 _reloadAnimActive（reloading 变假时它会被清）—— 它假 = 这次换弹没在跑 = 被打断。
            var cancelled = !_reloadAnimActive;
            _log.Always($"换弹动画心跳（播后 {ReloadProbeDelay:F1}s）：seq={_reloadProbeSeq} " +
                        $"被打断={cancelled}" + (cancelled ? "（换弹中切枪，原版即如此，本判据豁免）" : "") +
                        $" {detail}");
        }

        /// <summary>
        /// 换弹诊断心跳（无条件逐帧）：把每一道闸门的实测值打进日志。
        ///
        /// <para><b>为什么要它</b>：实测 9 次真实换弹只播了 1 次（`seq=1`），而且**三条已有的闸门留痕一条都没打** ——
        /// 说明"边沿根本没走到判它的那一行"。⇒ 缺的是一个**无条件**的逐帧心跳：
        /// 只要"当前正在换弹"（<c>ReloadEndTime</c> 未到），就把**每一个闸门的实测值**打出来，
        /// 这样"卡在哪一道门"从推断变成一个**读得出来的字段**。</para>
        ///
        /// <para><b>不打屏</b>：只在"正在换弹"时打，且**每次换弹最多 N 行 / 每 M 帧一行**
        /// （<see cref="DiagEveryFrames"/> / <see cref="DiagMaxLinesPerReload"/>）；
        /// 非换弹期一条都不打。</para>
        /// </summary>
        private void DiagReload(ICsMatch match, CsActor local, string where)
        {
            // ------------------------------------------------------------------
            //  ① 闸门切换留痕 —— 这是"换弹动画被静默丢掉"的**第二种可能**的唯一读数：
            //     如果 Tick 在 A/B/C 任一闸门早退，UpdateAnim 根本不会被调用，那时
            //     「边沿去哪了」的答案不是"边沿没来"，而是"**判边沿的那一帧压根没跑到**"。
            //     只在闸门**变了**时打一行（否则每帧一行 = 刷屏），且总量封顶。
            // ------------------------------------------------------------------
            if (!string.Equals(where, _lastGate, System.StringComparison.Ordinal))
            {
                var prev = _lastGate;
                _lastGate = where;
                if (prev != null && _gateLogs < GateLogCap)
                {
                    _gateLogs++;
                    _log.Always(
                        $"每帧闸门 {prev} → {where}｜seq={(local != null ? local.ReloadSeq : -1)} " +
                        $"preSeq={_preReloadSeq} haveSnap={_haveSnapshot} " +
                        $"anim={(_anim == null ? "null" : "ok")} active={_reloadAnimActive} " +
                        $"phase={(match != null ? match.Phase.ToString() : "?")}");
                }
            }

            if (local == null)
            {
                // 连 local 都没有：只有真的在换弹节奏里才值得记（否则菜单期刷屏）。
                if (!_diagArmed) return;
                _diagArmed = false;
                _log.Always($"viewmodel.reload.diag 换弹心跳中断：{where}");
                return;
            }

            var reloading = local.ReloadEndTime > 0f && Time.time < local.ReloadEndTime;
            if (!reloading)
            {
                if (_diagArmed)
                {
                    _diagArmed = false;
                    _log.Always(
                        $"viewmodel.reload.diag 换弹结束：seq={local.ReloadSeq} 已播={_reloadAnimsPlayed}" +
                        $"（本次心跳共 {_diagLines} 行）");
                    // "换弹跑完之后"的取样 —— 心跳本身只在 reloading 期间打，看不到清没清。
                    // 若这 0.6 s 内又起了一次换弹，UpdateAnim 的播放分支会把 _reloadSettleAt 归零，
                    // 不会把"新一次换弹的覆盖态"误判成"上一次没清"。
                    _reloadSettleAt = Time.time + ReloadSettleDelay;
                    _reloadSettleArmedAt = Time.time;
                    _reloadSettleSeq = local.ReloadSeq;
                }
                return;
            }

            if (!_diagArmed)
            {
                _diagArmed = true;
                _diagLines = 0;
                _diagFrame = 0;
            }

            _diagFrame++;
            if (_diagLines >= DiagMaxLinesPerReload || _diagFrame % DiagEveryFrames != 0) return;
            _diagLines++;

            _log.Always(
                $"viewmodel.reload.diag [{where}] seq={local.ReloadSeq} preSeq={_preReloadSeq} " +
                $"haveSnap={_haveSnapshot} " +
                $"anim={(thisRefNull() ? "null" : "ok")} over={_overrideState ?? "-"} active={_reloadAnimActive} " +
                $"ReloadEndTime={local.ReloadEndTime:F2} now={Time.time:F2}");
        }

        /// <summary>`_anim` 是否为空（心跳里要打出来；单独包一个方法只是为了少写三目）。</summary>
        private bool thisRefNull() => _anim == null;

        private bool _diagArmed;
        private int _diagFrame;
        private int _diagLines;

        /// <summary>换弹跑完之后再过 <see cref="ReloadSettleDelay"/> 秒取一次覆盖态样本（判据 J-R6）。</summary>
        private float _reloadSettleAt;

        /// <summary>本次取样窗口的起点（换弹到点那一刻）；用来判"这个窗口内有没有发生过超期强制清"。</summary>
        private float _reloadSettleArmedAt;

        private int _reloadSettleSeq;
        private const float ReloadSettleDelay = 0.6f;

        /// <summary>上一次「超期未清 ⇒ 强制归位」发生的时刻（<see cref="SetOverride"/> 归零）。</summary>
        private float _overrideTimeoutAt;

        /// <summary>
        /// 判据 J-R6 取样：换弹结束后 <see cref="ReloadSettleDelay"/> 秒。语义是**换弹末帧卡住**，
        /// 不是"存在任何覆盖态"：
        /// 红 = 覆盖态仍是换弹那一段（没清），或**任何**覆盖态**超期未清**（<see cref="IsOverrideOverdue"/>，
        /// 或本窗口内刚发生过超期强制清 ⇒ <c>clearedBy=timeout</c>）——这两条是硬判据，⛔ 不放宽；
        /// 绿 = 覆盖态已归位（<c>over=-</c>），或已经切到之后才开始、且还在自己片段时长内的合法一次性动作
        /// （原版开火序列 <c>fire1</c>/<c>fire2</c> 实测 0.775 s &gt; 本取样延迟 0.6 s，撞上属正常行为）。
        /// 用 Always：一次换弹只该有一行。
        /// </summary>
        private void SettleReload()
        {
            if (_reloadSettleAt <= 0f || Time.time < _reloadSettleAt) return;
            _reloadSettleAt = 0f;

            var over = _overrideState;
            var stillReload = IsReloadState(over);
            var timedOutHere = _reloadSettleArmedAt > 0f && _overrideTimeoutAt >= _reloadSettleArmedAt;
            var overdue = IsOverrideOverdue() || timedOutHere;
            var red = stillReload || overdue;

            var why = stillReload
                ? "⛔（覆盖态仍是换弹段 = 换弹末帧卡住，判据 J-R6 红）"
                : overdue
                    ? "⛔（覆盖态超期未清 = 卡住，判据 J-R6 红）"
                    : over == null
                        ? "（覆盖态已归位 = 判据 J-R6 绿）"
                        : "（已切到之后的一次性动作、仍在片段时长内 = 判据 J-R6 绿）";

            _log.Always(
                $"viewmodel.reload.settle 换弹结束后 {ReloadSettleDelay:F1}s：seq={_reloadSettleSeq} " +
                $"over={over ?? "-"} active={_reloadAnimActive} " +
                $"clearedBy={_overrideClearedBy ?? "-"} own={(over != null ? Time.time - _overrideSetAt : 0f):F2} " +
                $"len={(over != null ? _overrideLen : 0f):F2} overdue={overdue} " + why);
        }

        /// <summary>上一帧走的闸门（闸门切换才留一条日志）。</summary>
        private string _lastGate;

        /// <summary>闸门切换日志已打几条（封顶，防"每帧翻转"刷屏）。</summary>
        private int _gateLogs;

        /// <summary>闸门切换日志上限。</summary>
        private const int GateLogCap = 60;

        /// <summary>播换弹之后多久取一次 Animator 真实状态（秒）。</summary>
        private const float ReloadProbeDelay = 0.45f;

        /// <summary>下一次"换弹动画真的在跑"取样的时刻（0 = 不取）。</summary>
        private float _reloadProbeAt;

        /// <summary>取样对应的换弹序号。</summary>
        private int _reloadProbeSeq;

        /// <summary>取样时期望的 reload 状态名。</summary>
        private string _reloadProbeState;

        /// <summary>心跳打点间隔（帧）—— 换弹 2.2 s ≈ 130 帧 ⇒ 每 20 帧一行 = 最多 7 行/次。</summary>
        private const int DiagEveryFrames = 20;

        /// <summary>单次换弹最多打几行心跳。</summary>
        private const int DiagMaxLinesPerReload = 8;

        private void SetModelVisible(bool visible)
        {
            if (_modelRoot == null) return;
            if (_modelRoot.gameObject.activeSelf != visible) _modelRoot.gameObject.SetActive(visible);
        }

        private void ResetSnapshot(CsActor local)
        {
            _haveSnapshot = false;
            _preWeapon = local.ActiveWeapon;
            _preNextFireTime = local.NextFireTime;
            _preReloadSeq = local.ReloadSeq;
        }

        // ==================================================================
        //  模型切换
        // ==================================================================
        private void SyncModel(CsActor local)
        {
            var weaponId = local.ActiveWeapon;
            if (string.IsNullOrEmpty(weaponId))
            {
                weaponId = CsViewTuning.ViewModelFallbackWeapon;
            }

            if (weaponId == CurrentWeaponId && _model != null) return;

            var wasNull = CurrentWeaponId == null;
            CurrentWeaponId = weaponId;

            // 先隐藏旧模型：新模型加载是异步的，中间不能出现"两把枪"。
            if (_model != null) _model.SetActive(false);

            var path = ViewModelPath(local.Team, weaponId);
            if (_owner == null)
            {
                _log.Error("viewmodel.noowner", "ViewModelRig 未拿到 ViewModule（装配异常），武器模型无法加载");
                return;
            }

            // 缓存命中 ⇒ 让池去实例化 / 复用（传的是**资源键**，不是预制体对象）。
            if (_owner.TryGetViewModelPrefab(path, out _))
            {
                Attach(path, path);
                return;
            }

            var fallbackPath = ViewModelPath(local.Team, CsViewTuning.ViewModelFallbackWeapon);
            if (_owner.TryGetViewModelPrefab(fallbackPath, out _))
            {
                Attach(fallbackPath, path + "(回退 刀)");
            }

            _owner.RequestViewModelPrefab(path, go =>
            {
                if (this == null) return;
                if (go == null)
                {
                    OnModelMissing(path, local.Team);
                    return;
                }
                Attach(path, path);
            });

            // 刚开局（还没有任何模型）时先摆一个刀，避免"手上空着"
            if (wasNull && _model == null)
            {
                _owner.RequestViewModelPrefab(fallbackPath, go =>
                {
                    if (this == null || _model != null) return;
                    if (go != null) Attach(fallbackPath, fallbackPath + "(回退)");
                });
            }
        }

        private void OnModelMissing(string path, CsTeam team)
        {
            _missingLogged++;
            if (_missingLogged != 1 && _missingLogged % CsViewTuning.LogRateEvery != 0) return;
            _log.Warn("viewmodel.missing." + path,
                $"第一人称武器模型缺失：Resources/{path}.prefab（第 {_missingLogged} 次）—— " +
                $"该武器手上不显示。请先执行菜单 Clover/CS16/整理视图与音效资源（会从 CS 1.6 的 v_*.mdl 生成）");
        }

        private static string ViewModelPath(CsTeam team, string weaponId)
        {
            var folder = team == CsTeam.CT ? CsViewTuning.TeamFolderCT : CsViewTuning.TeamFolderT;
            return CsViewTuning.ArtRoot + folder + "/" + CsViewTuning.ViewModelPrefix + weaponId;
        }

        /// <summary>把当前模型还给对象池（换枪 / <see cref="Teardown"/> 的**唯一出口**）。</summary>
        private void DespawnModel()
        {
            if (_model == null) return;
            var pool = Game.Pool;
            if (pool != null) pool.Despawn(_model);
            else Destroy(_model);      // 池不可用（未 Launch / 已 Shutdown）：退回销毁，别把模型留在场上
            _model = null;
        }

        /// <summary>
        ///
        /// <para><paramref name="path"/> = 资源键（<c>Art/{阵营}/viewmodel_&lt;武器&gt;</c>，与
        /// <c>CsViewTuning</c> 同源）；<paramref name="key"/> = 命名 / 日志用的展示键（回退分支带 "(回退 …)" 后缀）。</para>
        ///
        /// <para>存在性由调用方用 <c>ViewModule.TryGetViewModelPrefab</c> 把关（与 ViewModule 同款口径）：
        /// 池只负责"造 / 复用"，"要不要造"仍由预制体缓存决定 —— 缺资源时不建、也不每帧刷屏。</para>
        ///
        /// <para>生命周期终点是 <see cref="DespawnModel"/>（换枪 / 解绑都走它）：
        /// 不许 <c>Destroy</c>，否则池里留下已销毁引用，下次 Spawn 会把它发回来。</para>
        /// </summary>
        private void Attach(string path, string key)
        {
            if (_modelRoot == null) return;
            DespawnModel();          // 先还旧的（池内复用），再取新的

            var pool = Game.Pool;
            if (pool == null)
            {
                _log.Error("pool.null", "Game.Pool 为 null（Game.Launch 未执行？），第一人称武器模型无法生成");
                return;
            }
            _model = pool.Spawn(path, _modelRoot, PoolGroupViewModel);
            if (_model == null)
            {
                _log.Warn("viewmodel.spawnfail", $"第一人称武器模型生成失败（对象池返回 null）：{path}");
                return;
            }
            _model.name = "ViewModel_" + key;
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;
            _model.transform.localScale = Vector3.one * CsViewTuning.ViewModelScale;
            _modelKey = key;

            // 武器模型绝不能参与射线：去掉它身上任何碰撞体（生成器没加，这里再兜一层）。
            var cols = _model.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) Destroy(cols[i]);
            }

            EnsureAlwaysAnimate(_model, key);
            SetupAnimator();
            _log.Info("viewmodel.attach",
                $"第一人称武器模型切换 → {key}（共 {cols.Length} 个多余碰撞体已移除）");
        }

        /// <summary>
        /// 为什么必须把 <c>updateWhenOffscreen</c> / <c>cullingMode</c> 设成"恒更新"。
        ///
        /// <para>机制：<c>.cs16anim</c> 网格的顶点是**绑定姿态**坐标，而渲染姿态由动画决定 ——
        /// 以 v_* 视模型为例（实测）：绑定姿态 bbox = x∈[-0.9087,-0.1160]、y∈[-0.0469,+0.1763]、
        /// z∈[-0.1272,+0.1374]（**把相机原点夹在里面**）；而 idle 姿态 = x∈[-0.0260,+0.2390]、
        /// y∈[-0.2860,-0.0620]、z∈[-0.0870,+0.7050]。两者**完全不重合**。</para>
        ///
        /// <para>Unity 用 <c>SkinnedMeshRenderer.localBounds</c>（= 绑定姿态包围盒）同时做
        /// ① 视锥剔除 ② 蒙皮更新开关；Animator 默认的 <c>CullUpdateTransforms</c> 又拿"看不见"来决定
        /// 更不更新骨骼。一旦被判成不可见 ⇒ 骨骼**停在绑定姿态** ⇒ 画出来是**巨大到失真的手臂/枪**
        /// （实测绑定姿态有顶点投影到视口 x=155 —— 155 倍屏宽）。</para>
        ///
        /// <para>在**实例化时**把两个开关都设成"恒更新"，于是盘上**已烘好的旧预制体**也一并修好，
        /// 不必重跑生成器（生成器侧同样已改，见 <c>Editor/Views/AnimSetup.cs</c>）。</para>
        /// </summary>
        private void EnsureAlwaysAnimate(GameObject model, string key)
        {
            var fixedSmr = 0;
            var smrs = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < smrs.Length; i++)
            {
                if (smrs[i] == null || smrs[i].updateWhenOffscreen) continue;
                smrs[i].updateWhenOffscreen = true;
                fixedSmr++;
            }
            var fixedAnim = 0;
            var anims = model.GetComponentsInChildren<Animator>(true);
            for (var i = 0; i < anims.Length; i++)
            {
                if (anims[i] == null || anims[i].cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
                anims[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                fixedAnim++;
            }
            if (fixedSmr > 0 || fixedAnim > 0)
            {
                _log.Info("viewmodel.cullfix",
                    $"第一人称武器 {key}：已修正 {fixedSmr} 个 SkinnedMeshRenderer.updateWhenOffscreen " +
                    $"与 {fixedAnim} 个 Animator.cullingMode（绑定姿态不得当可见性判据 ⇒ 否则巨大失真）");
            }
        }

        // ==================================================================
        //  动画（原版序列）
        // ==================================================================
        private void SetupAnimator()
        {
            _animator = _model != null ? _model.GetComponent<Animator>() : null;
            _anim = null;
            ClearOverride("newModel");
            _shotIndex = 0;
            _stateWarned = false;

            if (_animator == null)
            {
                _log.Warn("viewmodel.noanimator",
                    $"武器预制体 {_modelKey} 上没有 Animator —— 手上这把枪不会播放任何动作。" +
                    "请重跑菜单 Clover/CS16/整理视图与音效资源");
                return;
            }
            var ctrl = _animator.runtimeAnimatorController;
            if (ctrl == null)
            {
                _log.Warn("viewmodel.nocontroller",
                    $"武器预制体 {_modelKey} 的 Animator 没有 controller —— 手上这把枪不会播放任何动作");
                return;
            }
            var anim = Game.Anim;
            if (anim == null)
            {
                _log.Warn("viewmodel.nomanager", "Game.Anim 为 null —— 第一人称武器不会播放任何动作");
                return;
            }
            _anim = anim.CreateAnimator(_model, ctrl);
            if (_anim == null) return;
            _anim.OnComplete(OnClipFinished);

            // 切枪：原版切枪会先播 draw（抬起）—— 与"新模型刚挂上"一一对应。
            // 消音两态按 `_preLocal`（同一玩家的上一帧快照；本方法没有 actor 形参）挑一套，
            // 它读的是 actor 的**当前** ActiveDef，所以拿到的是刚切上的那把枪。
            var st = ResolveState(SilenceVariant(CsViewTuning.VmStateDraw,
                                                 CsViewTuning.VmStateDrawUnsil, _preLocal));
            if (st != null)
            {
                _anim.Play(st, 0f);
                SetOverride(st);
            }
            else
            {
                var idle = ResolveState(SilenceVariant(CsViewTuning.VmStateIdle,
                                                       CsViewTuning.VmStateIdleUnsil, _preLocal));
                if (idle != null) { _anim.Play(idle, 0f); _lastState = idle; }
            }
        }

        private void OnClipFinished()
        {
            if (_overrideState == null) return;
            // ==================================================================
            // ------------------------------------------------------------------
            //   引擎 `clover-client-unity-engine/Runtime/Presentation/Animation.cs`
            //     `Play()`      第 116 行 `_completeFired = false;`
            //     `Update()`    第 146 行 `stateInfo.normalizedTime >= 1f && !_completeFired` ⇒ 派发 OnComplete
            //   而 Unity 的 `Animator.Play()` 之后**下一帧取到的 `GetCurrentAnimatorStateInfo(0)`
            //   仍描述上一段剪辑**（实测 isreload=False、归一化时间=0.66，即上一段已播完的状态）。
            //   ⇒ 换弹动作刚 `Play` 下去一两帧，就被上一段剪辑的收尾回调踢掉 `_overrideState`，
            //     那一帧起走回 idle 定格 = 用户看到的「这次换弹没有动画」。
            // 【实测频率】run E 9 次换弹里 1 次被截断；run D 4 次里 1 次 ⇒ 与用户「有时候」吻合。
            // 【为什么不会丢真完成】引擎 `Update()` 第 160-161 行
            //   `if (stateInfo.normalizedTime < 1f) _completeFired = false;`
            //   ⇒ 被我们拦掉的那一帧之后，剪辑进入播放中（<1）会**重新武装** `_completeFired`，
            //     真完成时仍会派发回调，不会被这次拦截吃掉。
            // 【残留风险（必须复验）】若 reload 剪辑**永远不会**让 `normalizedTime >= 1f`
            //   （speed=0 / 循环剪辑 / 状态带 exit 转移立刻跳出），则 `_overrideState` **永不清空**
            //   ⇒ 枪定格在换弹末帧（`UpdateAnim` 末尾 `if (_overrideState != null) return;` 也不回 idle）。
            //   表现上会自愈（下一次开火会重置 `_overrideState` 并在其播完时清），但"换弹末尾卡住"本身
            //   是用户可见的。故复验必须**同时**查这一条：
            //     J-R5 = `Animator 真状态取样` 的 `是reload剪辑=False` 计数为 0，
            //            且换弹中途不出现 `over=-`（陈旧回调被拦住的证据）；
            //     J-R6 = 换弹结束后 `over` **必须归 `-`**（末帧卡住的反面证据）。
            // ==================================================================
            if (_animator != null)
            {
                var si = _animator.GetCurrentAnimatorStateInfo(0);
                // same=False 就是"这次回调属于上一段剪辑"（陈旧完成）的现场证据。
                _log.Always(
                    $"viewmodel.oncomplete over={_overrideState} si={CurrentStateName(si)} " +
                    $"nT={si.normalizedTime:F2} same={si.IsName(_overrideState)}");
                if (!si.IsName(_overrideState)) return;   // 这条回调属于上一段剪辑（陈旧完成）
                if (si.normalizedTime < 1f) return;       // 当前这段还在播
            }
            ClearOverride("oncomplete");
        }

        /// <summary>按候选顺序取 Animator 里真实存在的状态（各武器序列命名不统一，实测见回报）。</summary>
        private string ResolveState(string[] candidates)
        {
            if (_animator == null || candidates == null) return null;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (string.IsNullOrEmpty(candidates[i])) continue;
                if (_animator.HasState(0, Animator.StringToHash(candidates[i]))) return candidates[i];
            }
            if (!_stateWarned)
            {
                _stateWarned = true;
                _log.Warn("viewmodel.state.missing",
                    $"武器 {_modelKey} 的状态机里找不到 {string.Join("/", candidates)} —— " +
                    "该动作不播放（预制体与 .cs16anim 不同步？请重跑生成器）");
            }
            return null;
        }

        /// <summary>只读权威状态 → 切原版序列（开火 / 换弹 / 静止）。</summary>
        private void UpdateAnim(CsActor local)
        {
            // ------------------------------------------------------------------
            // 闸门①：没有动画播放器 ⇒ 任何动作都播不出来。这里是"静默丢动作"的第一个岔口，
            // 必须在**边沿被吞掉的那一刻**留痕（否则只剩"用户说没动画"这一个现象）。
            // ------------------------------------------------------------------
            if (_anim == null)
            {
                if (local.ReloadSeq != _preReloadSeq)
                {
                    _preReloadSeq = local.ReloadSeq;
                    // Always：这一条是"边沿被吞"的**唯一**现场证据，一次换弹只可能有一次，
                    //    绝不能让它被 LogThrottle 的"每 50 次一条"口径吃掉。
                    _log.Always(
                        $"viewmodel.reload.gate.anim 换弹边沿被闸门①吞掉（_anim==null）：seq={local.ReloadSeq}，" +
                        $"武器={_modelKey ?? "null"}，本段换弹不会播（差异 #72）");
                }
                return;
            }

            // ------------------------------------------------------------------
            // 只重置基线、**不**在对象刚换完的那一帧就抢播（新对象 ReloadSeq 通常还是 0，
            //    与基线 0 相等 ⇒ 不会误播）；真正在换弹中的情况由下面的"拉取式"信号兜住。
            // ------------------------------------------------------------------
            if (!ReferenceEquals(_preLocal, local))
            {
                var prev = _preLocal;
                _preLocal = local;
                _log.Always(
                    $"viewmodel.local.changed 本地玩家对象变了（{Describe(prev)} → {Describe(local)}）⇒ 换弹边沿基线重置" +
                    $"（旧 seq={_preReloadSeq} ⇒ 新对象 seq={local.ReloadSeq}；差异 #72 的漏播根因之一）");
                _haveSnapshot = false;
                _preReloadSeq = 0;
                _reloadAnimActive = false;
            }

            if (!_haveSnapshot)
            {
                // 只对齐基线，**不再 return**：若此刻正处在换弹中，下面的"拉取式"信号要能补播。
                _haveSnapshot = true;
                _preWeapon = local.ActiveWeapon;
                _preNextFireTime = local.NextFireTime;
                _preReloadSeq = local.ReloadSeq;
            }

            if (local.ActiveWeapon != _preWeapon)
            {
                _preWeapon = local.ActiveWeapon;
                _shotIndex = 0;
            }

            // 消音器（差异 #68）：状态位翻一次 ⇒ 播原版那一条拆装序列。
            // 出处（序列名与帧数见 CsViewTuning.VmStateAddSilencer 的注释；态位翻的动作出处 =
            // 同族的 `regamedll/dlls/wpn_shared/wpn_m4a1.cpp` 的 `CM4A1::SecondaryAttack`，
            // 即 `m_iWeaponState` 翻一下 + `SendWeaponAnim(<ATTACH|DETACH>_SILENCER)`）。
            // 切枪那一帧**不**播拆装动作：原版 `Deploy()` 只换一套序列（`USP_DRAW` / `USP_UNSIL_DRAW`），
            // 不播拆装 —— 所以这里以"武器 id 没变"为判边沿的前提。
            var silenceSwitch = !string.Equals(local.ActiveWeapon, _preSilenceWeapon, System.StringComparison.Ordinal);
            var silDef = local.ActiveDef;
            if (silenceSwitch)
            {
                _preSilenceWeapon = local.ActiveWeapon;
                _preSilenced = local.Silenced;
            }
            else if (silDef != null && silDef.CanSilence && local.Silenced != _preSilenced)
            {
                _preSilenced = local.Silenced;
                var silState = ResolveState(local.Silenced
                    ? CsViewTuning.VmStateAddSilencer
                    : CsViewTuning.VmStateDetachSilencer);
                if (silState != null) { _anim.Play(silState, 0f); SetOverride(silState); }
                _log.Always($"第一人称{(local.Silenced ? "装上" : "拆下")}消音器 " +
                            $"状态={silState ?? "<状态机没有这一条>"}（{_modelKey}）");
            }

            //   信号甲（推）= ReloadSeq 变了：能盖住"同帧跨完 / 掉了中间帧"。
            //   信号乙（拉）= "现在正在换弹"（ReloadEndTime 尚未到）：能盖住"对象被换掉、
            //                 序号从 0 重来、边沿被判成没变"以及"边沿落在没有快照的那一帧"。
            //   单独任何一个都会漏：甲漏"对象换掉"，乙漏"同帧跨完"（那一刻 ReloadEndTime 已归零）。
            var reloading = local.ReloadEndTime > 0f && Time.time < local.ReloadEndTime;
            if (_reloadAnimActive && !reloading) _reloadAnimActive = false;

            var seqEdge = local.ReloadSeq != _preReloadSeq;
            if (seqEdge || (reloading && !_reloadAnimActive))
            {
                _preReloadSeq = local.ReloadSeq;
                var st = ResolveState(SilenceVariant(CsViewTuning.VmStateReload,
                                                     CsViewTuning.VmStateReloadUnsil, local));
                if (st != null)
                {
                    _anim.Play(st, 0f);
                    SetOverride(st);
                    _reloadAnimActive = true;
                    _reloadAnimsPlayed++;
                    // 新一次换弹 ⇒ 上一次的"归位取样"作废（否则会把新覆盖态误判成旧的一次没清）。
                    _reloadSettleAt = 0f;
                    // 必须用 Always（不降频）：换弹是 5.5 s 一次的低频事件，用降频口径
                    //    会让 17 次换弹只留 1 行 —— 那就是"换弹有时候没有动画"这条报障
                    //    被误判成"表现层漏播"的全部原因（见 ProbeReloadAnim 的长注释）。
                    _reloadProbeAt = Time.time + ReloadProbeDelay;
                    _reloadProbeSeq = local.ReloadSeq;
                    _reloadProbeState = st;
                    _log.Always($"第一人称播换弹 seq={local.ReloadSeq} 状态={st}（{_modelKey}）" +
                        $"[信号={(seqEdge ? "推:序号边沿" : "拉:正在换弹")}]");
                }
                else
                {
                    // 闸门②：状态机里没有 reload ⇒ 边沿在这里被吞。ResolveState 自己的告警只在
                    // 首次出现，单靠它无法把"这一次换弹"与"某一次换弹"对上，故在此显式留痕。
                    // Always：同 gate.anim —— 一次换弹只该有一行，不能被降频口径吃掉。
                    _log.Always(
                        $"viewmodel.reload.gate.state 换弹边沿被闸门②吞掉（状态机找不到 reload）：" +
                        $"seq={local.ReloadSeq} 武器={_modelKey ?? "null"}（差异 #72）");
                }
            }

            // 开火：NextFireTime 前推 = 确实打出了一发
            if (local.NextFireTime > _preNextFireTime + 0.0001f)
            {
                var mag = 1;
                if (!string.IsNullOrEmpty(local.ActiveWeapon)
                    && local.Ammo.TryGetValue(local.ActiveWeapon, out var ammo))
                {
                    mag = ammo.inMag;      // **只读**：直接查字典，不用会在缺项时写回的 CsActor.GetAmmo
                }

                // 两态的候选（只有 CanSilence 的武器分两套）：未装消音时走 *_unsil 那一条。
                string[] cand;
                if (mag <= 0) cand = SilenceVariant(CsViewTuning.VmStateFireLast, CsViewTuning.VmStateFireLastUnsil, local);   // 弹匣打空的那一发
                else if (_shotIndex == 0) cand = SilenceVariant(CsViewTuning.VmStateFire1, CsViewTuning.VmStateFire1Unsil, local);
                else if (_shotIndex == 1) cand = SilenceVariant(CsViewTuning.VmStateFire2, CsViewTuning.VmStateFire2Unsil, local);
                else cand = SilenceVariant(CsViewTuning.VmStateFire3, CsViewTuning.VmStateFire3Unsil, local);
                _shotIndex = (_shotIndex + 1) % 3;

                var st = ResolveState(cand);
                if (st != null) { _anim.Play(st, 0f); SetOverride(st); }
            }
            _preNextFireTime = local.NextFireTime;

            if (_overrideState != null) return;    // 一次性动作播完前不回 idle

            var idle = ResolveState(SilenceVariant(CsViewTuning.VmStateIdle,
                                                   CsViewTuning.VmStateIdleUnsil, local));
            if (idle != null && idle != _lastState)
            {
                _lastState = idle;
                _anim.CrossFade(idle, CsViewTuning.AnimCrossFade);
            }
        }

        /// <summary>诊断用：把 actor 描述成「名字(id=..)」；null 也要可读（差异 #72 的现场证据）。</summary>
        private static string Describe(CsActor a)
        {
            return a == null ? "<null>" : $"{a.Name}(id={a.Id})";
        }

        // ==================================================================
        //  模型根变换
        // ==================================================================
        private void ApplyRigTransform()
        {
            if (_modelRoot == null) return;
            _modelRoot.localPosition = CsViewTuning.ViewModelLocalPosition;
            _modelRoot.localRotation = Quaternion.Euler(CsViewTuning.ViewModelLocalEuler);
            _modelRoot.localScale = Vector3.one;
        }

        // ==================================================================
        //  走动摆动（原版 V_CalcBob + 视模型自己的那几项）
        // ==================================================================
        /// <summary>一个 bob 周期的时长（秒）。出处：原版 cvar <c>cl_bobcycle</c> 默认 <c>"0.8"</c>
        /// （<c>原版资源/hlsdk/cl_dll/view.cpp:1686</c>）。</summary>
        private const float BobCycle = 0.8f;

        /// <summary>速度驱动系数（unit/s → unit）。出处：原版 cvar <c>cl_bob</c> 默认 <c>"0.01"</c>
        /// （<c>view.cpp:1687</c>）。</summary>
        private const float BobAmount = 0.01f;

        /// <summary>周期前段占比（前半段映射到 0→π）。出处：原版 cvar <c>cl_bobup</c> 默认 <c>"0.5"</c>
        /// （<c>view.cpp:1688</c>）。</summary>
        private const float BobUp = 0.5f;

        /// <summary>bob 的下限（unit）。出处：<c>V_CalcBob</c> 的 <c>bob = max(bob, -7)</c>（<c>view.cpp:217</c>）。</summary>
        private const float BobMin = -7f;

        /// <summary>bob 的上限（unit）。出处：<c>V_CalcBob</c> 的 <c>bob = min(bob, 4)</c>（<c>view.cpp:216</c>）。</summary>
        private const float BobMax = 4f;

        /// <summary>已累计的 bob 时钟（秒）——原版是 <c>static bobtime</c> + 逐帧 <c>+= frametime</c>（<c>view.cpp:196</c>）。</summary>
        private float _bobTime;

        /// <summary>
        /// 把<b>视模型自己</b>的走动摆动写进模型根变换。原版这一段与相机的 bob 是**同一个 bob 值**：
        /// <c>view->origin[i] += bob*0.4*forward[i]</c>、<c>view->angles[YAW] -= bob*0.5</c>、
        /// <c>[ROLL] -= bob*1</c>、<c>[PITCH] -= bob*0.3</c>（<c>原版资源/hlsdk/cl_dll/view.cpp:648-663</c>），
        /// bob 本体 = 速度驱动的 <c>V_CalcBob</c>（<c>view.cpp:178-220</c>：<c>bob = |simvel.xy|·cl_bob</c>，
        /// 再 <c>bob·0.3 + bob·0.7·sin(cycle)</c>，clamp 到 [−7,+4] unit）。
        ///
        /// <para><b>为什么竖直那一项不写</b>：原版同时给<b>相机</b>和视模型各加一次 <c>+bob</c>
        /// （<c>view.cpp:526</c> 与 <c>:659</c>）⇒ 相对相机为 0；本工程的相机 bob 是另一套自定曲线
        /// （已按 <c>策划/差异登记.tsv</c> 登记），这里只落"相对相机"的那部分，不叠加第二份竖直位移。</para>
        ///
        /// <para><b>符号口径</b>：<c>view.cpp:371-372</c> 只把**俯仰**写成实体角
        /// （<c>viewent-&gt;angles[PITCH] = -viewangles[PITCH]</c>）而偏航是同号
        /// （<c>= +viewangles[YAW]</c>）⇒ 相对相机的俯仰增量要取反、偏航同号；
        /// 横滚在 GoldSrc 实体角里正方向 = 顶端向左倾，与 Unity 的 <c>euler.z</c> 正方向一致 ⇒ 直接取负。</para>
        /// </summary>
        private void ApplyBob(CsActor local)
        {
            if (_modelRoot == null || local == null) return;

            _bobTime += Time.deltaTime;
            var cycle = _bobTime - Mathf.Floor(_bobTime / BobCycle) * BobCycle;
            cycle /= BobCycle;
            cycle = cycle < BobUp
                ? Mathf.PI * cycle / BobUp
                : Mathf.PI + Mathf.PI * (cycle - BobUp) / (1f - BobUp);

            var v = local.Velocity;
            var speedUnits = new Vector2(v.x, v.z).magnitude / CsConst.UnitToMeter;
            var bob = speedUnits * BobAmount;
            bob = bob * 0.3f + bob * 0.7f * Mathf.Sin(cycle);
            bob = Mathf.Clamp(bob, BobMin, BobMax);

            _modelRoot.localPosition =
                CsViewTuning.ViewModelLocalPosition + Vector3.forward * (bob * 0.4f * CsConst.UnitToMeter);
            _modelRoot.localRotation =
                Quaternion.Euler(bob * 0.3f, bob * 0.5f, -bob);
        }
    }
}
