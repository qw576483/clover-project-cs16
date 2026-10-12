using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Combat;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    /// 一个 actor（真人/机器人）的**第三人称视图**外壳：位置/朝向跟随 + 蹲下压扁 + 死亡留尸
    /// + 自身渲染隐藏 + 头顶名牌。
    ///
    /// <para><b>层级约定（与 <c>ArtSetup</c> 生成的 player.prefab 一致）</b>：</para>
    /// <code>
    /// ActorView 根（不缩放；position = CsActor.Position，rotation = yaw）   ← 本组件
    ///   └─ Body（承载"整体缩放/贴地"；脚本只碰这一个节点的 localScale/localPosition）
    ///        ├─ Hitbox_Leg / Hitbox_Stomach / Hitbox_Chest / Hitbox_Head
    ///        │    （每个都带 MeshRenderer + CapsuleCollider + CsHitboxProxy）
    ///   └─ Plate（名牌预制体实例，不缩放，每帧 billboard）
    /// </code>
    /// <para>把缩放放在 <c>Body</c> 而不是根节点，是为了让名牌不受"蹲下压扁"影响
    /// （否则蹲下时名字/血条也会被压扁）。</para>
    ///
    /// <para><b>网格为什么按命中区切成 4 份</b>：这样"隐藏本地玩家自己的模型"只要关掉这 4 个
    /// （按 <see cref="CsHitboxProxy"/> 反查子 Renderer）也天然生效，两条路不会互相打架。</para>
    ///
    /// <para><b>只读契约</b>：本类只读 <see cref="CsActor"/>，一个字段都不写
    /// （写它 = 和比赛模拟抢权威）。</para>
    /// </summary>
    public sealed class ActorView : MonoBehaviour
    {
        private const string Tag = "View";

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        private CsHitboxProxy[] _proxies = new CsHitboxProxy[0];
        private Renderer[] _renderers = new Renderer[0];
        private Collider[] _colliders = new Collider[0];

        private Transform _body;
        private Nameplate _plate;
        private bool _isLocal;
        private bool _alive = true;
        private bool _bodyHiddenLogged;
        private bool _proxiesChecked;

        /// <summary>把模型缩到 <c>CsViewTuning.TargetHeight</c> 的竖直缩放（实测反算，不假定单位）。</summary>
        private float _fitScale = 1f;

        /// <summary>模型最低点相对 <c>Body</c> 原点的抬升量（米）——让脚底正好落在 <c>CsActor.Position</c>。
        /// Bind 时先取绑定姿态的值（<see cref="Measure"/>），此后由 <see cref="AlignFeetToGround"/>
        /// 按**当前姿态**逐帧跟随。</summary>
        private float _modelLift;

        /// <summary>模型自带的蒙皮网格（只取 <c>Body</c> 下的，头顶名牌不在其中）—— 取贴地基准用。</summary>
        private SkinnedMeshRenderer[] _modelMeshes = new SkinnedMeshRenderer[0];

        /// <summary>贴地对齐量被上限截断的告警是否已打过（每具视图一次，避免每帧刷屏）。</summary>
        private bool _alignClampWarned;

        private float _crouchScale = 1f;

        // ---- 骨骼动画（原版序列驱动；见 CsViewTuning 的「骨骼动画」一节）----
        private Animator _animator;
        private IAnimPlayer _anim;
        private bool _animFailedLogged;

        /// <summary>下次"恒更新复查"的时间（见 <see cref="EnsureAlwaysAnimateTick"/>）：异步加载的身体网格
        /// 与运行时挂上的武器都可能晚于 <see cref="Bind"/> 出现，所以不能只扫一次。</summary>
        private float _nextAlwaysAnimateScanAt;

        /// <summary>上面那次复查的间隔（秒）——1s 足够兜住"异步/换武器"，又不进每帧热路径。</summary>
        private const float AlwaysAnimateRescanSeconds = 1f;

        /// <summary>一次性动作（开枪 / 换弹）占用的状态名；播完由 OnComplete 清空。</summary>
        private string _overrideState;

        /// <summary>当前正在播的位移状态（避免每帧重复 CrossFade）。</summary>
        private string _lastState;

        /// <summary>死亡序列是否已经播过（每局一次）。</summary>
        private bool _deathPlayed;

        /// <summary>
        /// 尸体是否已经"落地留场"（倒地序列播完 → 冻住姿态、关掉碰撞体、但**不隐藏**）。
        ///
        /// <para>原版是尸体躺到本局结束、回合重开（复活）时才消失，所以这里**冻在倒地序列最后一帧**、
        /// 不隐藏；若走 <c>OnClipFinished → _hideAfterDeath = true → SetShown(false)</c>，
        /// 倒地序列播完整具身体就没了（连"倒地"这一下都看不完）。</para>
        ///
        /// <para><b>停留时长出处</b>：原版由客户端 cvar <c>cl_corpsestay</c> 控制，默认
        /// <b>600 s</b>（<c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>：注册处 VA <c>0x0194DAF9</c>，
        /// 默认值串 VA <c>0x019E8C40</c> = <c>"600"</c>，flags = 1 = FCVAR_ARCHIVE）。
        /// 本工程单局时长远短于它 ⇒ 表现等价于"留到本局结束"，故这里不设倒计时。</para>
        /// </summary>
        private bool _corpseHeld;

        /// <summary>没有 Animator（预制体太旧）时的兜底：死亡后立刻隐藏（相机与逐帧表现另说）。</summary>
        private bool _hideAfterDeath;

        /// <summary>这一局播的是哪条倒地序列（尸体留场时要把姿态精确钉在它的**最后一帧**，见 <see cref="EnsureCorpseShown"/>）。</summary>
        private string _deathState;

        /// <summary>尸体姿态是否已经冻住（每具尸体只做一次的收尾动作）。</summary>
        private bool _corpseFrozen;

        private bool _haveAnimSnapshot;
        private float _preNextFireTime;
        private int _preReloadSeq;
        private string _preWeapon;

        /// <summary>当前挂着的第三人称武器模型实例与武器 id（换枪 / 死亡 / 池复用时归还，见 <see cref="EnsureHeldWeapon"/>）。</summary>
        private GameObject _heldWeapon;
        private string _heldKey;
        /// <summary>武器挂点节点（优先挂在右手骨骼下；取不到骨骼时退回 <see cref="_body"/>）。
        /// 跟着蹲下压扁与逐帧贴地走。</summary>
        private Transform _weaponSocket;

        /// <summary>武器挂点是否挂在**右手骨骼**上（false = 退回到 <c>Body</c> 下的固定点）。
        /// 决定武器实例用哪一套局部变换（骨骼口径的逐武器表 / 固定点的旧口径）。</summary>
        private bool _socketOnHand;

        /// <summary>枪口节点（第三人称开火的火光落点）：<see cref="_weaponSocket"/> 的子节点，
        /// 位置逐武器来自 <see cref="CsViewTuning.HeldWeaponAlignOf"/> 的枪口列。</summary>
        private Transform _muzzleSocket;

        /// <summary>已经报过"没有世界模型"的武器 id（只报一次，避免每帧刷屏）。</summary>
        private readonly HashSet<string> _heldMissingLogged = new HashSet<string>();

        /// <summary>上一帧的权威血量。掉血 = 中了没死的一发（阵亡走 <see cref="PlayDeath"/>），
        /// 用来触发原版受击抖动 <see cref="PlayFlinch"/>。</summary>
        private int _preHealth;

        /// <summary>本具身体播过几次换弹（差异 #72 的断言用：每个成功换弹序号都该对上这里 +1）。</summary>
        private int _reloadAnimsPlayed;

        /// <summary>绑定的 actor id（与 4 个 <see cref="CsHitboxProxy.ActorId"/> 一致）。</summary>
        public long ActorId { get; private set; }

        /// <summary>阵营（决定模型目录与名牌颜色）。</summary>
        public CsTeam Team { get; private set; }

        public CsHitboxProxy[] Proxies => _proxies;

        /// <summary>
        /// 这具身体**播过几次换弹**（差异 #72 的断言口）：成功换弹一次 ⇒ 这里必须 +1。
        /// 判据 = 与 <c>CsActor.ReloadSeq</c> 对齐（"每次成功换弹 → 动画至少进入一次 reload"）。
        /// </summary>
        public int ReloadAnimsPlayed => _reloadAnimsPlayed;

        /// <summary>当前是否显示中（死亡 → false）。</summary>
        public bool IsShown { get; private set; }

        /// <summary>本地玩家自己的视图（渲染被隐藏、碰撞体保留）。</summary>
        public bool IsLocal => _isLocal;

        // ==================================================================
        //  对象池复用前置清理
        // ==================================================================
        /// <summary>
        ///
        /// <para><b>为什么必须有</b>（不清就会静默画出错的东西）：池交回的实例**带着上一世的状态** ——
        /// <list type="number">
        /// <item>上一世的名牌 <c>Plate</c> 子节点还在 ⇒ 会被 <see cref="Bind"/> 的
        /// <c>GetComponentsInChildren&lt;Renderer&gt;</c> 快照算进"实测身高"，整具模型的缩放 / 贴地全错；</item>
        /// <item>上一世若是本地玩家，4 个渲染被 <see cref="HideOwnRenderers"/> 关过 ⇒ 新角色**看不见**；</item>
        /// <item>上一世若是尸体，碰撞体被关过、Animator 速度被冻过（<see cref="ReleaseCorpsePose"/> 之外的回滚路径）
        /// ⇒ 射线打不中（"打不死人"）；</item>
        /// <item>各种"只报一次"的闸 / 动画快照 / 复活标记仍是上一世的 ⇒ 日志与表现错位。</item>
        /// </list></para>
        ///
        /// <para><b>调用时机</b>：<c>ViewModule.EnsureView</c> 里 <see cref="Bind"/> **之前** ——
        /// 必须早于 Bind 的快照，否则第 ① 条照样发生。</para>
        /// </summary>
        public void PrepareForReuse()
        {
            // ① 上一世的名牌：整棵子节点删掉（名牌本身也是池对象，交给池回收）
            var plateTf = transform.Find(CsViewTuning.PlateNodeName);
            if (plateTf != null)
            {
                var old = plateTf.gameObject;
                var pool = Game.Pool;
                if (pool != null) pool.Despawn(old);
                else Destroy(old);      // 池不可用（未 Launch）：退回销毁，别把它留在新角色头上
            }
            _plate = null;

            // ②③ 渲染与碰撞体还回来 + 动画速度复位（关过的都要开回来）
            for (var i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] != null) _renderers[i].enabled = true;
            }
            for (var i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null) _colliders[i].enabled = true;
            }
            if (_animator != null) _animator.speed = 1f;

            // ④ 身份与状态闸复位（Bind 会重新填身份；这里清的是"上一世的账"）
            _isLocal = false;
            _alive = true;
            _bodyHiddenLogged = false;
            _proxiesChecked = false;
            _animFailedLogged = false;
            _overrideState = null;
            _lastState = null;
            _deathPlayed = false;
            _deathState = null;
            _hideAfterDeath = false;
            _corpseHeld = false;
            _corpseFrozen = false;
            _haveAnimSnapshot = false;
            _preWeapon = null;
            _preNextFireTime = 0f;
            _preReloadSeq = 0;
            _preHealth = 0;
            _reloadAnimsPlayed = 0;
            ReleaseHeldWeapon();

            // 武器挂点**必须一起清掉**：它是 `EnsureHeldWeapon` 里按"右手骨骼"现找、找不到才退回
            // Body 下固定节点的一次性结果，而骨骼树可能随换模型/换槽位变。留着旧挂点会让"改了挂点逻辑"
            // 对**池复用**的视图完全无效（旧 socket 一直挂在 Body 上，手臂一动枪还是横在原地）。
            if (_weaponSocket != null)
            {
                Destroy(_weaponSocket.gameObject);
                _weaponSocket = null;
            }
            _anim = null;
            _fitScale = 1f;
            _modelLift = 0f;
            _modelMeshes = new SkinnedMeshRenderer[0];
            _alignClampWarned = false;
            _crouchScale = 1f;
            IsShown = false;
        }

        // ==================================================================
        //  绑定
        // ==================================================================
        /// <summary>绑定 actor 身份，并做**量测优先**的尺寸/贴地校正。</summary>
        /// <param name="actorId">actor id（写进 4 个 <see cref="CsHitboxProxy"/>）</param>
        /// <param name="team">阵营</param>
        /// <param name="isBot">机器人 → 物理层 <c>PhysicsLayers.Bot</c>；真人 → <c>PhysicsLayers.Player</c></param>
        /// <param name="isLocal">本地玩家自己（渲染隐藏、碰撞体保留）</param>
        /// <remarks>
        /// **名牌必须在 <see cref="Bind"/> 之后用 <see cref="SetPlate"/> 挂上**：本方法里的
        /// <c>_renderers</c> 快照会排除名牌，否则名牌那点小几何会把"实测身高"抬高、把缩放算错。
        /// </remarks>
        public void Bind(long actorId, CsTeam team, bool isBot, bool isLocal)
        {
            ActorId = actorId;
            Team = team;
            _isLocal = isLocal;

            _body = transform.Find(CsViewTuning.BodyNodeName);
            if (_body == null)
            {
                _log.Warn("body.missing",
                    $"角色视图「{name}」找不到子节点「{CsViewTuning.BodyNodeName}」，退化到直接缩放根节点" +
                    "（名牌会跟着蹲下一起被压扁）—— 请重新执行 ArtSetup 生成 player.prefab");
                _body = transform;
            }

            _proxies = GetComponentsInChildren<CsHitboxProxy>(true);
            _renderers = GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);
            // 贴地基准只取 Body 下的蒙皮网格：Body 之外的任何几何（头顶名牌）都不该参与。（此刻还没挂名牌，
            // 但池复用时上一世的名牌由 PrepareForReuse 清掉，两条路一起保证这里量到的只是身体。）
            _modelMeshes = _body.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            var layer = isBot ? PhysicsLayers.Bot : PhysicsLayers.Player;
            for (var i = 0; i < _proxies.Length; i++)
            {
                if (_proxies[i] == null) continue;
                _proxies[i].ActorId = actorId;
                _proxies[i].gameObject.layer = layer;
            }
            for (var i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] == null) continue;
                _colliders[i].gameObject.layer = layer;
            }

            EnsureAlwaysAnimate();

            if (!_proxiesChecked)
            {
                _proxiesChecked = true;
                if (_proxies.Length != 4)
                {
                    // 不是"预期分支"就必须留痕：命中盒数量错了 → 射线认不出人/缺部位（打不中）。
                    _log.Error("proxy.count",
                        $"角色视图「{name}」的 CsHitboxProxy 数量 = {_proxies.Length}，期望 4（头/胸/腹/腿）。" +
                        "射线命中判定会缺部位 —— 请检查 ArtSetup 生成的 player.prefab");
                }
            }

            Measure();
            SetShown(true);
            if (_isLocal) HideOwnRenderers();
            SetupAnimator();
        }

        // ==================================================================
        //  骨骼动画：装配 / 状态机
        // ==================================================================
        /// <summary>
        /// 把模型自带的 <c>AnimatorController</c> 交给引擎的动画管理器（<c>Game.Anim</c>）拿播放器句柄。
        /// 控制器由生成器（<c>AnimSetup</c>）直接写进预制体 ⇒ 同步可得，没有异步加载竞态。
        /// </summary>
        private void SetupAnimator()
        {
            _animator = GetComponent<Animator>();
            if (_animator == null)
            {
                _log.Error("anim.noanimator",
                    $"角色视图「{name}」上没有 Animator —— 该角色不会播放任何动作（静止在某一个姿态）。" +
                    "请重跑菜单 Clover/CS16/整理视图与音效资源（会从 .cs16anim 生成带动画的预制体）");
                return;
            }

            var ctrl = _animator.runtimeAnimatorController;
            if (ctrl == null)
            {
                _log.Error("anim.nocontroller",
                    $"角色视图「{name}」的 Animator 没有 controller —— 动作不会播放。" +
                    "请重跑菜单 Clover/CS16/整理视图与音效资源");
                return;
            }

            var anim = Game.Anim;
            if (anim == null)
            {
                _log.Warn("anim.nomanager",
                    "Game.Anim 为 null（CloverPresentation.Init 未执行？）—— 角色动作不会播放");
                return;
            }

            _anim = anim.CreateAnimator(gameObject, ctrl);
            if (_anim == null)
            {
                _log.Warn("anim.noplayer", $"CreateAnimator 返回 null（{name}）—— 角色动作不会播放");
                return;
            }
            // 一次性动作（开枪/换弹/倒地）播完 → 回到位移状态。循环状态每次过 1.0 也会触发，
            // 但那时 _overrideState 已经是空，处理里什么都不做。
            _anim.OnComplete(OnClipFinished);
            _log.Info("anim.ready", $"角色视图「{name}」骨骼动画就绪（controller={ctrl.name}）");
        }

        private void OnClipFinished()
        {
            if (_overrideState != null)
            {
                _overrideState = null;
                _lastState = null;         // 强制下一次 Apply 重新选状态
            }
            // 死亡：倒地序列播完 ⇒ **冻住留场**（不是隐藏）。见 _corpseHeld 的说明（差异 #73）。
            if (!_alive) _corpseHeld = true;
        }

        /// <summary>按候选顺序取 Animator 里**真实存在**的状态名（名字来自 mdl 实测标签，不统一）。</summary>
        private string ResolveState(string[] candidates)
        {
            if (candidates == null || candidates.Length == 0) return null;
            if (_animator == null) return null;
            for (var i = 0; i < candidates.Length; i++)
            {
                if (string.IsNullOrEmpty(candidates[i])) continue;
                if (_animator.HasState(0, Animator.StringToHash(candidates[i]))) return candidates[i];
            }
            if (!_animFailedLogged)
            {
                _animFailedLogged = true;
                _log.Warn("anim.state.missing",
                    $"角色视图「{name}」的 Animator 里找不到状态 {string.Join("/", candidates)} —— " +
                    "该动作不会播放（预制体与 .cs16anim 不同步？请重跑生成器）");
            }
            return null;
        }

        /// <summary>
        /// 这具身体此刻该播哪条位移序列（速度/蹲/空中 → 原版标签）。
        ///
        /// <para>走 / 跑的分档按**实际水平速度**（<see cref="CsViewTuning.AnimRunSpeedThreshold"/>），
        /// 与原版一致；<c>IsWalking</c>（Shift 标志）只决定期望速度，不参与选序列。</para>
        /// </summary>
        private static string[] LocomotionStates(CsActor actor)
        {
            var v = actor.Velocity;
            var speed = new Vector2(v.x, v.z).magnitude;
            var moving = speed > CsViewTuning.AnimMoveSpeedEpsilon;
            if (!actor.OnGround) return CsViewTuning.PStateJump;
            if (actor.IsCrouching)
                return moving ? CsViewTuning.PStateCrouchRun : CsViewTuning.PStateCrouchIdle;
            if (!moving) return CsViewTuning.PStateIdle;
            return speed > CsViewTuning.AnimRunSpeedThreshold ? CsViewTuning.PStateRun : CsViewTuning.PStateWalk;
        }

        /// <summary>当前水平速率（米/秒）——位移状态的判据与日志口径。</summary>
        private static float HorizontalSpeed(CsActor actor)
        {
            var v = actor.Velocity;
            return new Vector2(v.x, v.z).magnitude;
        }

        /// <summary>
        /// 只读感知「刚刚发生了什么」：权威状态里 <c>NextFireTime</c> 前推 = 打出一发、
        /// <c>ReloadSeq</c> 变了 = 开始换弹（与 <see cref="ViewModelRig"/> 同口径 ——
        /// <para>差异 #72：换弹**不能**再用「<c>ReloadEndTime</c> 比上一帧大」当边沿 —— 那是截止时间，
        /// 结算/切枪会把它归零、同帧内"开始→完成"更是连一次采样都没有 ⇒ 动画整段丢。改用单调序号。</para>
        /// </summary>
        private void UpdateAnim(CsActor actor)
        {
            if (_anim == null) return;

            if (!_haveAnimSnapshot)
            {
                _haveAnimSnapshot = true;
                _preWeapon = actor.ActiveWeapon;
                _preNextFireTime = actor.NextFireTime;
                _preReloadSeq = actor.ReloadSeq;
                _preHealth = actor.Health;
                return;
            }

            if (actor.ActiveWeapon != _preWeapon) _preWeapon = actor.ActiveWeapon;

            // 受击（掉了血但这一帧还活着 ⇒ 不是阵亡）：播原版 flinch。判阵亡走 Apply 的死亡分支。
            if (actor.Health < _preHealth) PlayFlinch(actor);
            _preHealth = actor.Health;

            // 换弹优先（原版里换弹会打断射击姿势）
            if (actor.ReloadSeq != _preReloadSeq)
            {
                _preReloadSeq = actor.ReloadSeq;
                var cand = CsViewTuning.PlayerReloadStates(actor.ActiveWeapon, actor.IsCrouching);
                var st = ResolveState(cand);
                if (st != null)
                {
                    _anim.Play(st, 0f);
                    _overrideState = st;
                    _reloadAnimsPlayed++;
                    _log.Info("anim.reload.play",
                        $"角色视图「{name}」播换弹 seq={actor.ReloadSeq} 状态={st}");
                }
            }

            if (actor.NextFireTime > _preNextFireTime + 0.0001f)
            {
                var cand = CsViewTuning.PlayerShootStates(actor.ActiveWeapon, actor.IsCrouching);
                var st = ResolveState(cand);
                if (st != null) { _anim.Play(st, 0f); _overrideState = st; }
                // 别人的开火要有第三人称枪口表现。本地玩家走第一人称那一套
                // （CombatModule.HandleLocalShot）—— 这里必须排除，否则同一次开火会出现两处火光。
                if (!_isLocal) CombatModule.Instance?.FxRemoteShot(this, actor.ActiveWeapon);
            }
            _preNextFireTime = actor.NextFireTime;

            if (_overrideState != null) return;      // 一次性动作播完前不改状态

            var want = ResolveState(LocomotionStates(actor));
            if (want != null && want != _lastState)
            {
                _lastState = want;
                _anim.CrossFade(want, CsViewTuning.AnimCrossFade);
                // 位移态是逐帧重算的连续量：按时间抽样留一条现场，不每次切换都打。
                if (CloverEngine.LogThrottle.ShouldLog("view.loco:" + name, 5f))
                {
                    _log.Info("anim.loco",
                        $"角色视图「{name}」位移状态 → {want}（水平速度 {HorizontalSpeed(actor):F3} m/s，" +
                        $"蹲={actor.IsCrouching} 离地={!actor.OnGround}）");
                }
            }
        }

        // ==================================================================
        //  第三人称手持武器（差异 #75 同族：别的角色手里要看得见那把枪）
        // ==================================================================
        /// <summary>
        /// 让**别的角色**手里看得见那把枪：模型 = <c>Art/world_&lt;武器id&gt;</c>
        /// （原版世界模型 <c>models/w_*.mdl</c>；原版本来就只有一部分武器带它 —— 没有的武器
        /// 原版第三人称也不显示，所以这里**不做第一人称视模型的兜底**）。
        ///
        /// <para>挂点优先 = 右手骨骼 <c>Bip01 R Hand</c>；武器实例的局部变换按武器取
        /// <see cref="CsViewTuning.HeldWeaponAlignOf"/>（那张表由原版 <c>models/p_*.mdl</c> 与
        /// <c>models/w_*.mdl</c> 的同名几何解出，出处见该表注释）。找不到右手骨骼时退回
        /// <c>Body</c> 下的固定点，用固定点口径（零偏移 + <c>WorldModelScale</c>）。</para>
        ///
        /// <para>挂上去的实例照 <c>EnsureDropView</c> / <c>ViewModelRig</c> 的口径**剥掉全部 Collider**
        /// —— 世界物件绝不能参与射线（否则子弹会打中"一把悬空的枪"）。</para>
        ///
        /// <para>池加载是异步友好的：取不到就**保持未挂**、下一帧自动重试
        /// （与 <c>EnsureDropView</c> 的"首次发起加载、下一帧补上"同款，不阻塞）。</para>
        /// </summary>
        private void EnsureHeldWeapon(CsActor actor)
        {
            var weaponId = actor.ActiveWeapon;
            if (string.IsNullOrEmpty(weaponId)) { ReleaseHeldWeapon(); return; }
            if (_heldKey == weaponId && _heldWeapon != null) return;   // 同一把枪，不用换

            ReleaseHeldWeapon();

            if (_weaponSocket == null)
            {
                // **优先挂到右手骨骼**：枪必须跟着手臂动画走。原先固定挂在 `Body` 下的一个空节点上
                // （位置 = CsViewTuning.HeldWeaponLocalPosition 常量），手臂一摆动枪就停在原地不动 ——
                // 用户看到的就是"枪横在那儿"。CS 系骨骼命名是 `Bip01 R Hand`（dump 实测 rootBone=Bip01），
                // 找不到右手骨骼时**退回原来的固定节点**（宁可不跟手，也不许枪消失）。
                var hand = FindHandBone(_body != null ? _body : transform);
                _socketOnHand = hand != null;
                _weaponSocket = new GameObject("HeldWeapon").transform;
                _weaponSocket.SetParent(_socketOnHand ? hand : (_body != null ? _body : transform), false);
                _weaponSocket.localPosition = _socketOnHand ? Vector3.zero : CsViewTuning.HeldWeaponLocalPosition;
                _weaponSocket.localRotation = Quaternion.identity;
                // 枪口节点与挂点同生共死；它的局部口径与挂点一致，位置逐武器在下面刷。
                _muzzleSocket = new GameObject("Muzzle").transform;
                _muzzleSocket.SetParent(_weaponSocket, false);
                _muzzleSocket.localPosition = CsViewTuning.HeldWeaponMuzzleFallbackLocal;
                _muzzleSocket.localRotation = Quaternion.identity;
                if (hand == null)
                {
                    _log.Warn("held.nohand",
                        $"角色视图「{name}」找不到右手骨骼（预期名字含 Hand/R，如 Bip01 R Hand）" +
                        "⇒ 武器退回挂在 Body 下的固定节点，不会跟着手臂动画走");
                }
            }

            var pool = Game.Pool;
            if (pool == null)
            {
                _log.Warn("held.pool.null", "Game.Pool 为 null（Game.Launch 未执行？），第三人称武器模型无法生成");
                return;
            }

            var path = CsViewTuning.ArtRoot + CsViewTuning.WorldModelPrefix + weaponId;
            var go = pool.Spawn(path, _weaponSocket, "cs16.heldweapon");
            if (go == null)
            {
                // 原版世界模型只有一部分武器有（手雷就没有）。没有 ⇒ 原版第三人称也不显示，
                // 这里同样不显示；⛔ 不退第一人称视模型（那等于把"一双第一人称的手"塞进手里）。
                // 池加载是异步的 ⇒ 保持"下一帧重试"，只按武器 id 报一次这条信息；
                // 若该武器原版确有 w_*.mdl，则是预制体缺失 —— 池自己会报 Prefab not found。
                if (_heldMissingLogged.Add(weaponId))
                {
                    _log.Info("held.nomodel",
                        $"角色视图「{name}」的武器「{weaponId}」没有世界模型 {path}（原版也没有 ⇒ 不显示）");
                }
                return;
            }

            go.name = "HeldWeapon_" + weaponId;
            if (_socketOnHand)
            {
                // 手骨口径：逐武器对齐表（出处见 CsViewTuning.HeldWeaponAlign 的注释）。
                var align = CsViewTuning.HeldWeaponAlignOf(weaponId);
                go.transform.localPosition = align.LocalPosition;
                go.transform.localRotation = Quaternion.Euler(align.LocalEuler);
                go.transform.localScale = Vector3.one * CsViewTuning.HeldWeaponScale;
                _muzzleSocket.localPosition = align.MuzzleLocalPosition;
            }
            else
            {
                // 固定点口径：逐武器表是"手骨局部"的量，这里没有手骨可跟，只能用旧口径。
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one * CsViewTuning.WorldModelScale;
                _muzzleSocket.localPosition = CsViewTuning.HeldWeaponMuzzleFallbackLocal;
            }

            var cols = go.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++)
            {
                if (cols[i] != null) Destroy(cols[i]);
            }

            _heldWeapon = go;
            _heldKey = weaponId;
            // 运行期挂上来的几何不在 Bind 的快照里 ⇒ 挂上这一帧就把"恒更新"补掉，
            // 不等降频复查（这段时间里那把枪的蒙皮网格照旧会按绑定盒被裁）。
            EnsureAlwaysAnimate();
        }

        /// <summary>
        /// 找**右手骨骼**（武器挂点用）。CS 系模型的骨骼命名是 <c>Bip01 R Hand</c> 一族
        /// （dump 实测本工程预制体 <c>rootBone=Bip01</c>），这里按"名字含 hand、且不是左手"匹配，
        /// 容忍 <c>Bip01 R Hand</c> / <c>Bip01_R_Hand</c> / <c>rhand</c> 等写法。
        /// </summary>
        /// <returns>找不到返回 <c>null</c>（调用方退回固定挂点）。</returns>
        private static Transform FindHandBone(Transform root)
        {
            if (root == null) return null;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                var n = all[i].name;
                if (string.IsNullOrEmpty(n)) continue;
                if (n.IndexOf("hand", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                // 排除左手（"l hand" / "lhand" / "left"）
                if (n.IndexOf("l hand", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (n.IndexOf("lhand", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (n.IndexOf("left", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                return all[i];
            }
            return null;
        }

        /// <summary>
        /// 取枪口的世界位姿（第三人称开火表现用）。
        /// </summary>
        /// <returns>挂点/枪口节点都还没建起来（这具视图还没挂过武器）时返回 <c>false</c>，
        /// 两个出参为默认值。</returns>
        public bool TryGetMuzzle(out Vector3 position, out Quaternion rotation)
        {
            if (_muzzleSocket == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            position = _muzzleSocket.position;
            rotation = _muzzleSocket.rotation;
            return true;
        }

        /// <summary>当前挂着的手持武器 id；没挂任何武器时为 <c>null</c>（供取证/排障读取）。</summary>
        public string HeldWeaponId => _heldKey;

        /// <summary>
        /// **测试入口，供离线取证驱动使用**：让这具视图走一次"别人开火"的第三人称枪口表现
        /// —— 与真实链路上（<c>CsActor.NextFireTime</c> 前推那一帧）调的是**同一个方法**。
        ///
        /// <para><b>不改变真实玩家行为</b>：没有任何 Update / 事件会调它，只在被显式调用时生效
        /// （与 <c>CombatModule.SetFireHeldForTest</c> 同口径）。</para>
        /// </summary>
        public void FireRemoteMuzzleForTest(string weaponId)
        {
            var id = string.IsNullOrEmpty(weaponId) ? _heldKey : weaponId;
            CombatModule.Instance?.FxRemoteShot(this, id);
        }

        /// <summary>归还当前挂着的武器模型（换枪 / 死亡 / 池复用前调）。</summary>
        private void ReleaseHeldWeapon()
        {
            _heldKey = null;
            if (_heldWeapon == null) return;
            var pool = Game.Pool;
            if (pool != null) pool.Despawn(_heldWeapon);
            else Destroy(_heldWeapon);
            _heldWeapon = null;
        }

        /// <summary>
        /// 中弹**未死**时的抖动序列。原版（<c>mp.dll 0x10067439</c>）在
        /// <c>head_flinch</c> / <c>gut_flinch</c> 之间用 <c>RANDOM(0,1)</c> 二选一，两条都只有 2 帧。
        ///
        /// <para><b>只在"中性姿态"时替换</b>：原版那次替换挂在"当前序列"的表上
        /// （<c>0x1006742B</c> 的字节表 <c>0x1006790C</c>：序列 1 = <c>idle1</c>、2 = <c>crouch_idle</c>
        /// 走 flinch 分支，位移/跳跃序列落到默认分支）⇒ 这里同样只在站定或蹲定时播，
        /// 不给跑动、换弹、开枪中途插入抖动（那会把一次完整动作截断成"抽搐"）。</para>
        /// </summary>
        private void PlayFlinch(CsActor actor)
        {
            if (_anim == null || _overrideState != null) return;
            if (HorizontalSpeed(actor) > CsViewTuning.AnimMoveSpeedEpsilon) return;
            var cand = UnityEngine.Random.Range(0, 2) == 1
                ? CsViewTuning.PStateFlinchHead
                : CsViewTuning.PStateFlinchGut;
            var st = ResolveState(cand);
            if (st == null) return;
            _anim.Play(st, 0f);
            _overrideState = st;
            _log.Info("anim.flinch.play",
                $"角色视图「{name}」受击未死：血量 {_preHealth}→{actor.Health}，播状态 {st}");
        }

        /// <summary>
        /// 死亡：蹲着死走原版 <c>crouch_die</c>（<c>FL_DUCKING</c> 分支），否则走 <c>death1/2/3</c>
        /// （按 actorId 稳定取一条）。播完冻住留场（见 <see cref="_corpseHeld"/>）。
        /// </summary>
        private void PlayDeath(CsActor actor)
        {
            if (_anim == null)
            {
                SetShown(false);
                return;
            }

            // 蹲姿：原版先测 pev->flags 的 FL_DUCKING（mp.dll 0x100676F7），命中就只播 crouch_die。
            if (actor != null && actor.IsCrouching)
            {
                var cst = ResolveState(CsViewTuning.PStateDeathCrouch);
                if (cst != null)
                {
                    _deathState = cst;
                    _anim.Play(cst, 0f);
                    _overrideState = cst;
                    _log.Info("anim.death.play", $"角色视图「{name}」蹲姿死亡 → 状态 {cst}");
                    return;
                }
            }

            var arr = CsViewTuning.PStateDeath;
            var start = (int)(ActorId % arr.Length);
            var cand = new string[arr.Length];
            for (var i = 0; i < arr.Length; i++) cand[i] = arr[(start + i) % arr.Length];
            var st = ResolveState(cand);
            if (st == null)
            {
                SetShown(false);
                return;
            }
            _deathState = st;
            _anim.Play(st, 0f);
            _overrideState = st;
            _log.Info("anim.death.play", $"角色视图「{name}」死亡 → 状态 {st}（蹲={actor != null && actor.IsCrouching}）");
        }

        /// <summary>
        ///
        /// <para>实测：<c>player_T.cs16anim</c> 的网格**绑定姿态** bbox = x∈[-0.1947,+0.2722]、
        /// y∈[0.0000,1.8000]、z∈[-0.9111,+0.8997]；而它自己 <c>idle1</c> 轨道第 0 帧蒙皮出来的姿态 =
        /// x∈[-0.4540,+0.2410]、y∈[0.0000,+1.6150]、z∈[-0.3610,+0.6620] —— **绑定盒不覆盖渲染姿态**
        /// （x 方向差了 0.26 m）。用绑定盒当"看不看得见 / 要不要更新"的判据，就会把姿态卡住或被裁掉。</para>
        /// </summary>
        private void EnsureAlwaysAnimate()
        {
            // **实时扫描整棵子树**，不用 `Bind` 时刻的快照 `_renderers`：身体网格可能是**异步加载**进来的
            // （实例化时它还不存在），手里的枪是 `EnsureHeldWeapon` 在**运行时**才挂上去的 —— 快照里
            // 一个都没有它们，于是那两块几何照旧拿绑定盒当可见性判据 ⇒ 表现就是"身体姿态卡在绑定姿势
            // （T-pose，双臂张开）/ 手里的枪横着且位置错 / 网格被裁成一片一片（用户原话：像个沥水框全是洞）"。
            var fixedSmr = 0;
            var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);   // 含 Body 与运行时挂上的武器
            for (var i = 0; i < smrs.Length; i++)
            {
                var smr = smrs[i];
                if (smr == null || smr.updateWhenOffscreen) continue;
                smr.updateWhenOffscreen = true;
                fixedSmr++;
            }

            var anims = GetComponentsInChildren<Animator>(true);
            var fixedAnim = 0;
            for (var i = 0; i < anims.Length; i++)
            {
                if (anims[i] == null || anims[i].cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
                anims[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
                fixedAnim++;
            }
            if (fixedSmr > 0 || fixedAnim > 0)
            {
                _log.Info("actor.cullfix",
                    $"角色视图「{name}」：已修正 {fixedSmr} 个 SkinnedMeshRenderer.updateWhenOffscreen " +
                    $"与 {fixedAnim} 个 Animator.cullingMode（绑定包围盒不得当可见性判据）");
            }
        }

        /// <summary>
        /// **降频复查**（1 秒一次）——"恒更新"这件事不能只在 <see cref="Bind"/> 时做一次。
        ///
        /// <para>两条漏网路径（都会让表现退回"绑定姿态 + 网格被裁"）：① 身体网格是**异步加载**的，
        /// Bind 那一刻还没实例化；② 手里的枪由 <see cref="EnsureHeldWeapon"/> 在**运行时**挂上。
        /// 本方法是**幂等且便宜**的：只有真的需要改（`updateWhenOffscreen == false` /
        /// `cullingMode != AlwaysAnimate`）才会写值，正常帧什么都不做（`GetComponentsInChildren`
        /// 每 1 秒一次，不在每帧热路径上）。</para>
        /// </summary>
        private void EnsureAlwaysAnimateTick()
        {
            var now = Time.time;
            if (now < _nextAlwaysAnimateScanAt) return;
            _nextAlwaysAnimateScanAt = now + AlwaysAnimateRescanSeconds;
            EnsureAlwaysAnimate();
        }

        /// <summary>挂上头顶名牌（**必须在 <see cref="Bind"/> 之后**：名牌的渲染不计入身高实测）。</summary>
        public void SetPlate(Nameplate plate)
        {
            _plate = plate;
            if (_plate != null) _plate.SetVisible(IsShown);
        }

        /// <summary>
        /// **量测优先**：实测包围盒 → 反算缩放到目标身高，并把模型最低点对齐到 <c>Body</c> 原点（脚底）。
        ///
        /// <para>为什么不假定单位：预制体虽是生成器按 1.8m 归一化出来的，但素材随时可能被换成
        /// 另一个单位的模型（inch / cm / m）。实测一次并在偏差超容差时纠偏 + 告警，
        /// 保证"碰撞盒 / 射线命中位置 / 贴地"三者永远一致。</para>
        ///
        /// <para>本方法给的是**绑定姿态**那一个值（这一姿态脚底就在 0）；运行期每个姿态各自的最低点由
        /// <see cref="AlignFeetToGround"/> 逐帧对齐 —— 姿态一变最低点就变，单一常数盖不住。</para>
        /// </summary>
        private void Measure()
        {
            var maxY = float.NegativeInfinity;
            var minY = float.PositiveInfinity;
            var any = false;

            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;

                // 量的是**网格自带的绑定姿态包围盒**（`Mesh.bounds`，生成器按绑定姿态归一化到
                //   目标身高），**不是** `Renderer.bounds`。
                //   实例化当帧的 LateUpdate，此刻 Animator 已经把 clip 的第 0 帧写进骨骼 ——
                //   实测量到的最低点是 `1.0195`（正好 = 根骨骼 `Bip01` 的绑定 y），于是
                //   而渲染时 Animator 用的是 clip 的根骨骼 y（≈0），这个 lift 就成了纯粹的偏移。
                //   `Mesh.bounds` 与姿态无关（Unity 不会按蒙皮结果重算 mesh 包围盒）⇒ 量出来
                //   就是导出时归一化的那一个姿态：身高 = 目标值、脚底 = 0 ⇒ fitScale=1、lift=0。
                var smr = r as SkinnedMeshRenderer;
                var b = (smr != null && smr.sharedMesh != null) ? smr.sharedMesh.bounds : r.localBounds;
                if (b.size.sqrMagnitude <= 0f) continue;
                maxY = Mathf.Max(maxY, b.max.y);
                minY = Mathf.Min(minY, b.min.y);
                any = true;
            }

            if (!any || maxY - minY <= 0.001f)
            {
                _log.Warn("measure.empty",
                    $"角色视图「{name}」实测不到可渲染包围盒（Renderer={_renderers.Length} 个）——" +
                    "尺寸校正被跳过，模型可能偏大/偏小");
                return;
            }

            var height = maxY - minY;
            _fitScale = CsViewTuning.TargetHeight / height;
            _modelLift = -minY * _fitScale;

            if (Mathf.Abs(1f - _fitScale) > CsViewTuning.HeightTolerance)
            {
                _log.Warn("measure.scale." + name,
                    $"角色视图「{name}」实测身高 {height:F3}m ≠ 目标 {CsViewTuning.TargetHeight:F2}m，" +
                    $"已纠偏缩放 ×{_fitScale:F4}（超出容差 {CsViewTuning.HeightTolerance:F2}）");
            }
            else if (Mathf.Abs(_modelLift) > 0.02f)
            {
                _log.Info("measure.lift." + name,
                    $"角色视图「{name}」脚底校正 {_modelLift:F3}m（模型原点不在脚底，已自动对齐）");
            }

            ApplyScale();
            if (_body != null) _body.localPosition = new Vector3(0f, _modelLift, 0f);
        }

        private void ApplyScale()
        {
            if (_body == null) return;
            _body.localScale = new Vector3(_fitScale, _fitScale * _crouchScale, _fitScale);
        }

        /// <summary>
        /// 把模型最低点对齐到根原点（= 脚底落在 <c>CsActor.Position</c>）。
        ///
        /// <para><b>为什么必须随姿态</b>：模型最低点由**当前动画姿态**决定，不是绑定姿态的常数 ——
        /// 同一具模型 <c>idle1</c> 贴地、<c>walk</c> 途中双脚会同时离地数厘米、<c>crouch_idle</c> 又悬空一点
        /// （见 <c>CsViewTuning.FootAlignLimit</c> 注释里的实测量级）⇒ 任何单一常数都会"按下这个姿态、
        /// 翘起那个姿态"。</para>
        ///
        /// <para><b>不参与的姿态</b>：离地（jump）时最低点不是脚，硬对齐会把整具模型往下拽 ⇒
        /// 调用方只在 <c>OnGround</c> 时调；渲染被关掉（本地玩家第一人称下自己的模型）时量不到有效
        /// 包围盒 ⇒ 保持既有值，不动。</para>
        /// </summary>
        private void AlignFeetToGround()
        {
            if (_body == null || _modelMeshes.Length == 0) return;

            var minY = float.PositiveInfinity;
            for (var i = 0; i < _modelMeshes.Length; i++)
            {
                var r = _modelMeshes[i];
                if (r == null || !r.enabled) continue;
                if (r.bounds.min.y < minY) minY = r.bounds.min.y;
            }
            if (float.IsInfinity(minY)) return;

            var rootY = transform.position.y;
            var want = _body.localPosition.y + (rootY - minY);
            var clamped = Mathf.Clamp(want, -CsViewTuning.FootAlignLimit, CsViewTuning.FootAlignLimit);
            if (!_alignClampWarned && !Mathf.Approximately(clamped, want))
            {
                _alignClampWarned = true;
                _log.Warn("feet.align.clamp",
                    $"角色视图「{name}」姿态最低点 {minY:F3}m 与根 {rootY:F3}m 相差 {rootY - minY:F3}m，" +
                    $"超出贴地对齐上限 {CsViewTuning.FootAlignLimit:F3}m ⇒ 按上限对齐（包围盒异常？姿态不该走这条？）");
            }

            if (Mathf.Abs(clamped - _modelLift) < 0.0001f) return;      // 没变就不写 transform（省一次 dirty）
            _modelLift = clamped;
            _body.localPosition = new Vector3(0f, _modelLift, 0f);
        }

        // ==================================================================
        //  每帧同步（由 ViewModule 在 LateUpdate 调用）
        // ==================================================================
        /// <summary>把 actor 的权威状态刷到视图上。只在**活着**时改位置/朝向。</summary>
        /// <param name="actor">权威 actor（只读）</param>
        /// <param name="plateVisible">名牌是否可见（由 ViewModule 按距离/视线/阵营算好）</param>
        /// <param name="showName">是否显示名字（队友才显示，敌人只显示血条）</param>
        /// <param name="now">当前时间（供名牌做平滑）</param>
        public void Apply(CsActor actor, bool plateVisible, bool showName, float now)
        {
            if (actor == null) return;

            if (actor.IsAlive != _alive)
            {
                _alive = actor.IsAlive;
                if (_alive)
                {
                    // 复活：碰撞体与冻结速度复位（死亡当帧就关过碰撞体，见 EnterCorpse）。
                    ReleaseCorpsePose();
                    SetShown(true);
                    // 只在"上一世真的留下了停住的东西"时才重启整具身体：死亡与复活这两个边沿若成对出现
                    // （同一具身体在相邻帧被判死又判活），无条件清 _lastState 会让下一帧再次 CrossFade，
                    // 状态机永远走不完一次过渡 ⇒ 画面定格在过渡中的混合姿态。
                    if (_deathPlayed || _deathState != null || _hideAfterDeath || _overrideState != null)
                    {
                        _deathPlayed = false;
                        _deathState = null;
                        _hideAfterDeath = false;
                        _overrideState = null;
                        _lastState = null;
                        _haveAnimSnapshot = false;
                    }
                }
                // 死亡**不立刻隐藏**：先播原版倒地序列，播完（OnComplete）**冻住留场**。
                // 没有动画时（预制体里是静态网格）走兜底：立即隐藏。
                else if (_anim == null) SetShown(false);
            }

            if (!_alive)
            {
                // 原版死亡即掉枪（DroppedWeapons 系统会生成掉落物）⇒ 手上那把同时消失。
                ReleaseHeldWeapon();
                if (!_deathPlayed)
                {
                    _deathPlayed = true;
                    PlayDeath(actor);
                    EnterCorpse();
                }
                // 尸体：确保可见（尸体不被"上一帧的隐藏"带走）+ 名牌/血条关掉（死人头顶不该有血条）。
                if (_corpseHeld) EnsureCorpseShown();
                if (_hideAfterDeath) SetShown(false);
                return;
            }

            // ---- 活着就不许停在"尸体冻结速度"上 ----
            // speed = 0 时状态机不推进：任何一次 CrossFade 都会永远停在中途（画面上就是一具定格在
            // 混合姿态的身体）。尸体冻结（见 EnsureCorpseShown）必须只作用在尸体上。
            if (_animator != null && _animator.speed <= 0f)
            {
                _animator.speed = 1f;
                _log.Info("anim.unfreeze",
                    $"角色视图「{name}」活着但 Animator.speed = 0（尸体冻结未清除）⇒ 已复位为 1");
            }

            // ---- 位置 / 朝向（只读 actor，绝不写回）----
            transform.SetPositionAndRotation(actor.Position, Quaternion.Euler(0f, actor.Yaw, 0f));

            // ---- 蹲下（整体压扁：脚底不动，命中盒跟着一起缩）----
            var want = actor.IsCrouching ? CsViewTuning.CrouchScale : 1f;
            if (Mathf.Abs(want - _crouchScale) > 0.001f)
            {
                var dt = Time.deltaTime;
                var t = dt <= 0f ? 1f : 1f - Mathf.Exp(-dt / Mathf.Max(0.0001f, CsViewTuning.CrouchBlendTau));
                _crouchScale = Mathf.Lerp(_crouchScale, want, t);
                ApplyScale();
            }

            // ---- 骨骼动画：按权威状态切原版序列 ----
            UpdateAnim(actor);

            // ---- "恒更新"复查（降频 1s）：异步加载的身体网格 / 运行时挂上的武器都必须被覆盖到，
            //      否则它们拿绑定姿态包围盒当可见性判据 ⇒ 姿态卡在绑定姿势 + 网格被裁（见 EnsureAlwaysAnimateTick）----
            EnsureAlwaysAnimateTick();

            // ---- 手里的枪：换枪/拾枪时把模型换掉（别人手里要看得见那把枪，差异 #75 同族）----
            EnsureHeldWeapon(actor);

            // ---- 脚底贴地：按**当前姿态**（idle / walk / crouch 各有各的最低点）对齐，见 AlignFeetToGround ----
            if (actor.OnGround) AlignFeetToGround();

            // ---- 本地玩家：自己的第三人称模型隐藏渲染、保留碰撞体 ----
            if (_isLocal) HideOwnRenderers();

            // ---- 头顶名牌 ----
            if (_plate != null)
            {
                _plate.SetVisible(plateVisible);
                if (plateVisible)
                {
                    _plate.Refresh(actor.Name, actor.Health, CsConst.MaxHealth,
                        TeamColorOf(actor.Team), showName, true);
                }
            }
        }

        /// <summary>把视图直接摆到指定位置（生成 / 复活时先摆好，避免第一帧从池原点飞过来）。</summary>
        public void SnapTo(CsActor actor)
        {
            if (actor == null) return;
            transform.SetPositionAndRotation(actor.Position, Quaternion.Euler(0f, actor.Yaw, 0f));
        }

        /// <summary>阵营 → 颜色（名牌文字与血条用）。</summary>
        public static Color TeamColorOf(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.T: return CsViewTuning.TeamColorT;
                case CsTeam.CT: return CsViewTuning.TeamColorCT;
                default: return CsViewTuning.TeamColorSpec;
            }
        }

        // ==================================================================
        //  显隐
        // ==================================================================
        /// <summary>
        /// 显示/隐藏整个视图（含碰撞体与名牌）。死亡 → false，复活 → true。
        ///
        /// <para>自身渲染只在**活着的**本地玩家身上才藏（第一人称相机在他眼位）；尸体必须看得见
        /// —— 尸体留场走的就是本方法，不判 <see cref="_alive"/> 的话每具本地尸体都会被这里藏掉。</para>
        /// </summary>
        public void SetShown(bool shown)
        {
            IsShown = shown;
            if (gameObject.activeSelf != shown) gameObject.SetActive(shown);
            if (_plate != null) _plate.SetVisible(shown);
            if (shown && _isLocal && _alive) HideOwnRenderers();
        }

        // ==================================================================
        //  尸体留场
        // ==================================================================
        /// <summary>
        /// 死亡当帧的收尾：**立刻剥掉碰撞体** + 本地玩家把自己的渲染还回来。
        ///
        /// <para><b>为什么不等倒地序列播完</b>：倒地序列要 1.3~1.9 s，这段时间里死人还带着碰撞体 ⇒
        /// 子弹还能打中他（<c>CsHitboxProxy</c>）、活人还会被他挡住。尸体从"死"那一刻起就不该参与这两件事。</para>
        ///
        /// <para><b>为什么本地玩家要还渲染</b>：活着时藏自身模型是因为第一人称相机就在他头里；
        /// 死后相机转去观战（<c>CsMatch.IsSpectating</c>，观战对象只取活人）⇒ 他的尸体该被看见。</para>
        /// </summary>
        private void EnterCorpse()
        {
            var off = DisableColliders();
            if (_isLocal) ShowOwnRenderers();
            _log.Info("corpse.enter",
                $"角色视图「{name}」阵亡（本地={_isLocal}）：已关掉 {off}/{_colliders.Length} 个碰撞体" +
                "（尸体不挡活人、也不再被子弹打中），随后播原版倒地序列");
        }

        /// <summary>关掉本视图全部碰撞体（尸体不挡人 / 不被子弹命中）。返回本次关掉的个数。</summary>
        private int DisableColliders()
        {
            var off = 0;
            for (var i = 0; i < _colliders.Length; i++)
            {
                var c = _colliders[i];
                if (c == null || !c.enabled) continue;
                c.enabled = false;
                off++;
            }
            return off;
        }

        /// <summary>
        /// 尸体留场：**确保可见 + 名牌关掉 + 姿态冻住 + 碰撞体全关**（每具尸体只做一次收尾）。
        ///
        /// <para><b>为什么要关碰撞体</b>：尸体留场后 GameObject 仍活着，不显式关碰撞体就会被子弹打中
        /// （射线会命中 <c>CsHitboxProxy</c> ⇒ 死人反复"中弹"），又会**挡住活人走路**。</para>
        ///
        /// <para><b>为什么冻 <c>Animator.speed</c> 而不是 <c>SetShown(false)</c></b>：倒地序列是一次性 clip，
        /// 播完若状态机继续跑会被拉回 idle（尸体站起来）。冻在最后一帧 = 尸体就是倒地姿势。</para>
        /// </summary>
        private void EnsureCorpseShown()
        {
            if (!IsShown) SetShown(true);
            if (_plate != null) _plate.SetVisible(false);

            if (_corpseFrozen) return;
            _corpseFrozen = true;

            // 先把姿态**钉在倒地序列的最后一帧**，再冻速度：只冻速度会停在"OnComplete 被回调时那一帧"，
            // 而引擎的完成回调与状态机回绕之间没有顺序保证 ⇒ 有可能冻在回绕后的第 0 帧（尸体半站着）。
            if (_anim != null && _deathState != null) _anim.Play(_deathState, 1f);
            if (_animator != null) _animator.speed = CsViewTuning.CorpseAnimSpeed;
            DisableColliders();
            _log.Info("corpse.hold",
                $"角色视图「{name}」倒地序列播完 ⇒ 尸体留场（姿态冻在最后一帧，关掉 {_colliders.Length} 个碰撞体）；" +
                "回合重开复活时才清掉");
        }

        /// <summary>
        /// 复活：解冻姿态、把碰撞体还回来（<c>SetShown(true)</c> 之前调用即可）。**幂等**。
        ///
        /// <para>不拿 <c>_corpseFrozen</c> 当闸门：碰撞体是**死亡当帧**就被关掉的（见 <see cref="EnterCorpse"/>），
        /// 而 <c>_corpseFrozen</c> 要等倒地序列播完（<c>OnComplete</c>）才置位 —— 序列没播完就复活的那些，
        /// 用闸门就会把碰撞体永久关着（死人挡不住活人、活人也打不中他）。</para>
        /// </summary>
        private void ReleaseCorpsePose()
        {
            _corpseFrozen = false;
            _corpseHeld = false;
            if (_animator != null) _animator.speed = 1f;
            for (var i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null) _colliders[i].enabled = true;
            }
        }

        /// <summary>把本地玩家自己的渲染开回来（阵亡后尸体要看得见；复活时由存活分支再藏回去）。</summary>
        private void ShowOwnRenderers()
        {
            var back = 0;
            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null || r.enabled) continue;
                r.enabled = true;
                back++;
            }
            if (back > 0)
            {
                _bodyHiddenLogged = false;      // 复活时"第一人称已隐藏"那条日志要能再报一次
                _log.Info("corpse.local.shown",
                    $"本地玩家阵亡：把自己模型的 {back} 个渲染开回来 —— 观战相机只跟活人，尸体不该被藏");
            }
        }

        private void HideOwnRenderers()
        {
            var hidden = 0;
            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                hidden++;
            }
            if (hidden > 0 && !_bodyHiddenLogged)
            {
                _bodyHiddenLogged = true;
                _log.Always($"第一人称：已隐藏本地玩家自身模型的 {hidden} 个渲染（碰撞体保留，敌人照样打得到我）");
            }
        }
    }
}
