using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Combat;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cs16.Module.View
{
    /// <summary>
    /// 一个 actor（真人/机器人）的**第三人称视图**外壳：位置/朝向跟随 + 蹲下压扁 + 死亡留尸
    /// + 自身渲染隐藏。
    ///
    /// <para><b>层级约定（与 <c>ArtSetup</c> 生成的 player.prefab 一致）</b>：</para>
    /// <code>
    /// ActorView 根（不缩放；position = CsActor.Position，rotation = yaw）   ← 本组件
    ///   └─ Body（承载"整体缩放/贴地"；脚本只碰这一个节点的 localScale/localPosition）
    ///        ├─ Hitbox_LeftLeg / Hitbox_RightLeg / Hitbox_Stomach / Hitbox_Chest / Hitbox_Head
    ///        │    （每个都带 MeshRenderer + CapsuleCollider + CsHitboxProxy）
    /// </code>
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

        /// <summary>"背上的 C4 包"那一块网格的渲染（材质名含 <see cref="CsViewTuning.BackpackMaterialToken"/>；
        /// 原版靠玩家模型的 <c>backpack</c> bodygroup 开关，见该常量的出处）。已就绪但当前不持 C4 时为关闭态。</summary>
        private Renderer[] _backpackRenderers = new Renderer[0];

        private Transform _body;
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

        /// <summary>模型自带的蒙皮网格（只取 <c>Body</c> 下的）—— 取贴地基准用。</summary>
        private SkinnedMeshRenderer[] _modelMeshes = new SkinnedMeshRenderer[0];

        /// <summary>贴地对齐量被上限截断的告警是否已打过（每具视图一次，避免每帧刷屏）。</summary>
        private bool _alignClampWarned;

        /// <summary>下一条贴地对齐日志的时间（1 Hz 限频；见 <see cref="AlignFeetToGround"/>）。</summary>
        private float _nextFeetLogAt;

        private float _crouchScale = 1f;

        // ---- 脚底贴地圆影（见 CsBlobShadow）----
        /// <summary>本具视图的圆影。Bind 时建一次，池复用时跟着实例走（不销毁重建）。</summary>
        private CsBlobShadow _blobShadow;

        /// <summary>圆影贴的那层地面的世界 y：着地时逐帧跟 <c>CsActor.Position.y</c>，
        /// 离地时保持"起跳落点"的高度（影子留在地面、不跟着人飞）。</summary>
        private float _blobShadowGroundY;

        /// <summary><see cref="_blobShadowGroundY"/> 是否已有有效值（首次 Apply 之前没有）。</summary>
        private bool _blobShadowGroundReady;

        /// <summary>渲染体"收实时阴影"的纠偏是否已留过痕（每具视图一次）。</summary>
        private bool _shadowReceiveLogged;

        // ---- 骨骼动画（原版序列驱动；见 CsViewTuning 的「骨骼动画」一节）----
        private Animator _animator;
        private IAnimPlayer _anim;
        private bool _animFailedLogged;

        /// <summary>绑定姿态兜底是否已打过一次告警（每具视图只报一次，不刷屏）。</summary>
        private bool _bindPoseWarned;

        /// <summary>本视图的 controller 上是否真有 <see cref="CsAimBlend.ParamYaw"/> /
        /// <see cref="CsAimBlend.ParamPitch"/> 这两个 float 参数。
        /// <para>必须在装配时问一次并缓存：给**没有这个参数**的控制器 <c>SetFloat</c>，
        /// Unity 每帧刷一条 "Parameter 'X' does not exist" 警告（旧资产没有这两个参数）。</para></summary>
        private bool _aimParamsReady;

        /// <summary>上一次打进日志的两个瞄准轴值（−1 = 还没打过；见 <see cref="FeedAimBlend"/>）。</summary>
        private float _aimYawLogged = -1f;
        private float _aimPitchLogged = -1f;

        /// <summary>下次"恒更新复查"的时间（见 <see cref="EnsureAlwaysAnimateTick"/>）：异步加载的身体网格
        /// 与运行时挂上的武器都可能晚于 <see cref="Bind"/> 出现，所以不能只扫一次。</summary>
        private float _nextAlwaysAnimateScanAt;

        /// <summary>上面那次复查的间隔（秒）——1s 足够兜住"异步/换武器"，又不进每帧热路径。</summary>
        private const float AlwaysAnimateRescanSeconds = 1f;

        /// <summary>一次性动作（开枪 / 换弹）占用的状态名；播完由 OnComplete 清空。</summary>
        private string _overrideState;

        /// <summary>当前正在播的位移状态（避免每帧重复 CrossFade）。</summary>
        private string _lastState;

        /// <summary>下一条位移状态**不做过渡**、直接切（刚绑定 / 刚复活时状态机里还留着别的片段）。</summary>
        private bool _instantLoco;

        /// <summary>本帧做过"直接切"：Animator 的积分在 LateUpdate 之前，直接切要显式求值一次，
        /// 否则这一帧渲染的仍是刚激活时的绑定姿态（整具模型闪一帧 T-pose）。</summary>
        private bool _poseDirty;

        /// <summary>双手手枪的左右手射击组轮换位（0/1；见 <see cref="CsViewTuning.PlayerShootStates"/>）。
        /// 其它武器也照样翻，取值无害（它们只有一条射击组）。</summary>
        private int _shootVariant;

        // ---- 第 2 层：上身 aim 通道（见 DriveUpperChannel，生成器见 AnimSetup.BuildUpperStates）----
        /// <summary>
        /// 上身通道的层号。生成器把「upper」层钉在第 1 层（Base Layer 留在 0 层，运行期按 0 层切状态）
        /// ⇒ 这里与生成器同口径地按**层号**访问；控制器里没有第 2 层时整段不启用。
        /// </summary>
        private const int UpperLayer = 1;

        /// <summary>本视图的 controller 上是否真有第 <see cref="UpperLayer"/> 层（装配时问一次，见 <see cref="SetupAnimator"/>）。</summary>
        private bool _upperLayerReady;

        /// <summary>上身通道当前喂着的 aim 状态名（<c>null</c> = 通道关闭，层权重 0）。</summary>
        private string _upperState;

        /// <summary>查 layer0 本帧 clip 用的缓冲（见 <see cref="Layer0HasClip"/>；每帧复用，不产生 GC）。</summary>
        private readonly List<AnimatorClipInfo> _clipBuf = new List<AnimatorClipInfo>(4);

        /// <summary>上身通道的告警去重键（多个视图 / 多个角色共用同一 controller 与同一批状态名，不许逐具刷屏）。</summary>
        private static readonly HashSet<string> _upperWarned = new HashSet<string>(System.StringComparer.Ordinal);

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

        /// <summary>已经报过"没有手部对齐行"的武器 id（只报一次）。</summary>
        private readonly HashSet<string> _aimTableWarned = new HashSet<string>();

        /// <summary>上一帧的权威血量。掉血 = 中了没死的一发（阵亡走 <see cref="PlayDeath"/>），
        /// 用来触发原版受击抖动 <see cref="PlayFlinch"/>。</summary>
        private int _preHealth;

        /// <summary>本具身体播过几次换弹（差异 #72 的断言用：每个成功换弹序号都该对上这里 +1）。</summary>
        private int _reloadAnimsPlayed;

        /// <summary>绑定的 actor id（与 5 个 <see cref="CsHitboxProxy.ActorId"/> 一致）。</summary>
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

        /// <summary>
        /// 相机本帧是否正处在**本角色的眼位**（由 <see cref="ViewModule"/> 每帧写入）：
        /// 观战档位 4（目标第一人称）时，被观察者就是"当前这台相机的眼睛"。
        /// 这种情形下本体不该被画（藏渲染、**保留碰撞体**）—— 原版 <c>OBS_IN_EYE</c> 那一支
        /// （<c>view.cpp:1545-1551</c>）相机取的就是目标眼位，画面上只有视模型 + 准星 + HUD。
        /// </summary>
        private bool _spectateEye;

        /// <summary>
        /// 由 <see cref="ViewModule"/> 每帧写入；只有观战档位 4 的被观察者会被置 true。
        /// 按**状态边沿**动作（同一值重复写 = 什么都不做）：进档位 4 藏本体，**离开档位 4 必须还渲染**
        /// —— 只藏不还的话本体一直隐形（实机实测：档位 2 的渲染可见数与档位 4 相同）。
        /// 本地玩家那条路不在这里管（<see cref="Bind"/> / <see cref="EnterCorpse"/> 自己藏与还）。
        /// </summary>
        public void SetSpectateEye(bool on)
        {
            if (_spectateEye == on) return;
            _spectateEye = on;
            if (_isLocal || !_alive) return;
            if (on) HideOwnRenderers();
            else ShowOwnRenderers();
        }

        /// <summary>
        /// 由 <see cref="ViewModule"/> 每帧写入"这具视图现在是不是本机玩家"（状态边沿才动作）。
        ///
        /// <para>为什么不能只在 <see cref="Bind"/> 那一刻判：视图是**按 actor id 建一次**的，而
        /// "谁是本地玩家"可能晚于视图创建才成立（本地玩家那一帧还没就绪 / 新局与换图时 id 变了）。
        /// 那一刻判错就会把本地自己的身体一直画在第一人称镜头里。</para>
        ///
        /// <para>尸体不在这里管：死亡瞬间的显隐由 <see cref="EnterCorpse"/> 定（本地尸体必须看得见）。</para>
        /// </summary>
        public void SetLocal(bool isLocal)
        {
            if (_isLocal == isLocal) return;
            _isLocal = isLocal;
            if (!_alive) return;

            if (isLocal)
            {
                HideOwnRenderers();
                return;
            }

            // 仍是观战档位 4 的被观察者（相机在他眼位）⇒ 保持隐藏，别被这次身份变化还回来。
            if (_spectateEye) return;

            var back = EnableOwnRenderers();
            if (back > 0)
            {
                _bodyHiddenLogged = false;
                _log.Info("view.local.off",
                    $"角色视图「{name}」不再是本机玩家：把 {back} 个渲染还回来（换局 / 身份变化）");
            }
        }

        /// <summary>相机是否在本角色眼位（本地第一人称玩家 / 观战档位 4 的被观察者）。</summary>
        private bool EyeCam => _isLocal || _spectateEye;

        // ==================================================================
        //  对象池复用前置清理
        // ==================================================================
        /// <summary>
        ///
        /// <para><b>为什么必须有</b>（不清就会静默画出错的东西）：池交回的实例**带着上一世的状态** ——
        /// <list type="number">
        /// <item>上一世若是本地玩家，4 个渲染被 <see cref="HideOwnRenderers"/> 关过 ⇒ 新角色**看不见**；</item>
        /// <item>上一世若是尸体，碰撞体被关过、Animator 速度被冻过（<see cref="ReleaseCorpsePose"/> 之外的回滚路径）
        /// ⇒ 射线打不中（"打不死人"）；</item>
        /// <item>各种"只报一次"的闸 / 动画快照 / 复活标记仍是上一世的 ⇒ 日志与表现错位。</item>
        /// </list></para>
        ///
        /// <para><b>调用时机</b>：<c>ViewModule.EnsureView</c> 里 <see cref="Bind"/> **之前** ——
        /// 必须早于 Bind 的快照（快照会把残留几何算进"实测身高"）。</para>
        /// </summary>
        public void PrepareForReuse()
        {
            // ① 渲染与碰撞体还回来 + 动画速度复位（关过的都要开回来）
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
            _spectateEye = false;      // 上一世若是观战眼位目标，残留 true 会让复用出的新角色一开始就是隐形的
            _alive = true;
            _bodyHiddenLogged = false;
            _proxiesChecked = false;
            _animFailedLogged = false;
            _overrideState = null;
            _lastState = null;
            _instantLoco = true;
            _poseDirty = false;
            _shootVariant = 0;
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
            _aimParamsReady = false;
            _aimYawLogged = -1f;
            _aimPitchLogged = -1f;
            _upperLayerReady = false;
            _upperState = null;
            _fitScale = 1f;
            _modelLift = 0f;
            _modelMeshes = new SkinnedMeshRenderer[0];
            _alignClampWarned = false;
            _crouchScale = 1f;
            _backpackRenderers = new Renderer[0];
            _blobShadowGroundReady = false;
            _shadowReceiveLogged = false;
            if (_blobShadow != null) _blobShadow.ResetForReuse();
            IsShown = false;
        }

        // ==================================================================
        //  绑定
        // ==================================================================
        /// <summary>绑定 actor 身份，并做**量测优先**的尺寸/贴地校正。</summary>
        /// <param name="actorId">actor id（写进 5 个 <see cref="CsHitboxProxy"/>）</param>
        /// <param name="team">阵营</param>
        /// <param name="isBot">机器人 → 物理层 <c>PhysicsLayers.Bot</c>；真人 → <c>PhysicsLayers.Player</c></param>
        /// <param name="isLocal">本地玩家自己（渲染隐藏、碰撞体保留）</param>
        public void Bind(long actorId, CsTeam team, bool isBot, bool isLocal)
        {
            ActorId = actorId;
            Team = team;
            _isLocal = isLocal;
            _instantLoco = true;

            _body = transform.Find(CsViewTuning.BodyNodeName);
            if (_body == null)
            {
                _log.Warn("body.missing",
                    $"角色视图「{name}」找不到子节点「{CsViewTuning.BodyNodeName}」，退化到直接缩放根节点 " +
                    "—— 请重新执行 ArtSetup 生成 player.prefab");
                _body = transform;
            }

            _proxies = GetComponentsInChildren<CsHitboxProxy>(true);
            _renderers = CollectModelRenderers();
            _colliders = GetComponentsInChildren<Collider>(true);
            _backpackRenderers = CollectBackpackRenderers(_renderers);
            // 贴地基准只取 Body 下的蒙皮网格：Body 之外的任何几何都不该参与量测。
            _modelMeshes = _body.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            // 材质缺贴图兜底：部分皮肤材质的 _MainTex 是空的（生成器只找「裸贴图名」PNG，
            // 而盘上只有带模型前缀的贴图文件）⇒ 这里按材质名回绑一次，修好后后续 Bind 不再触发。
            EnsureTexturesBound();

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
                if (_proxies.Length != 5)
                {
                    // 不是"预期分支"就必须留痕：命中盒数量错了 → 射线认不出人/缺部位（打不中）。
                    _log.Error("proxy.count",
                        $"角色视图「{name}」的 CsHitboxProxy 数量 = {_proxies.Length}，" +
                        "期望 5（头/胸/腹/左腿/右腿）。" +
                        "射线命中判定会缺部位 —— 请检查 ArtSetup 生成的 player.prefab");
                }
            }

            if (_backpackRenderers.Length > 0)
            {
                _log.Info("backpack.found",
                    $"角色视图「{name}」背上的 C4 包 = {_backpackRenderers.Length} 块网格" +
                    $"（材质名含 {CsViewTuning.BackpackMaterialToken}）—— 只在该角色持 C4 时显示");
            }

            Measure();

            // 圆影必须建在 Measure **之后**：它自己也是一块 Renderer，早建会被量进"角色身高"。
            if (_blobShadow == null) _blobShadow = new CsBlobShadow();
            _blobShadow.Ensure(transform);
            _blobShadow.SetVisible(true);
            if (!_blobShadow.IsCreated)
            {
                _log.Warn("shadow.nocreate",
                    $"角色视图「{name}」的脚底圆影建不出来（程序化贴图生成失败）—— 该角色没有接地阴影");
            }

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

            // 瞄准 blend 的两个轴参数只有"出了 9 路 BlendTree 的控制器"才有（见 AnimSetup.BuildController）
            // ⇒ 在这里问一次并缓存，运行期按它决定要不要喂值。
            _aimParamsReady = AimParamsAvailable();

            // 第 2 层 = 上身 aim 通道（见 DriveUpperChannel 的说明）。老 controller 只有 1 层 ⇒ 整段不启用。
            _upperLayerReady = _animator.layerCount > UpperLayer;
            if (!_upperLayerReady && _upperWarned.Add("layer:" + ctrl.name))
            {
                _log.Warn("anim.upper.nolayer",
                    $"角色视图「{name}」的 controller「{ctrl.name}」只有 {_animator.layerCount} 层，没有第 {UpperLayer} 层" +
                    "（上身 aim 通道）—— 位移态会露出绑定姿态的手臂（双臂平伸）。" +
                    "请重跑菜单 Clover/CS16/整理视图与音效资源（会重建带动画层的预制体）");
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
        ///
        /// <para><b>站定那一档是瞄准姿势</b>：原版 <c>CBasePlayer::SetAnimation</c> 的 <c>ACT_WALK</c> 分支
        /// 在 <c>speed &lt;= 135 u/s</c> 时取 <c>ref_aim_&lt;武器动画类&gt;</c>
        /// （出处 <c>原版资源/cs16src/regamedll/dlls/player.cpp:2875-2929</c>），
        /// 该 state 是 9 路 2D BlendTree ⇒ 上身随瞄准方向（见 <see cref="FeedAimBlend"/>）。
        /// 其余档（空中 / 蹲 / 走 / 跑）不走这条，与原版的双通道口径（腿走 gait 序列、上身播 aim）
        /// 无关 —— 本工程没有 gait 通道，所以只有**站定**这一档切到瞄准姿势。</para>
        /// </summary>
        private string[] LocomotionStates(CsActor actor)
        {
            var v = actor.Velocity;
            var speed = new Vector2(v.x, v.z).magnitude;
            var moving = speed > CsViewTuning.AnimMoveSpeedEpsilon;
            if (!actor.OnGround) return CsViewTuning.PStateJump;
            if (actor.IsCrouching)
                return moving ? CsViewTuning.PStateCrouchRun : CsViewTuning.PStateCrouchIdle;
            if (!moving) return CsViewTuning.PlayerAimStates(actor.ActiveWeapon);
            return IsRunBand(speed) ? CsViewTuning.PStateRun : CsViewTuning.PStateWalk;
        }

        /// <summary>
        /// 走 / 跑分档，带迟滞：进跑档要超过「阈值 + 迟滞带」，退回走档要低于「阈值 − 迟滞带」。
        ///
        /// <para>速度停在阈值附近来回抖时（被墙 / 箱体钳住的角色：加速与撞墙清零逐帧交替），
        /// 不带迟滞会每帧重启一次 0.1 s 的 <c>CrossFade</c>；过渡期间 <c>Animator</c> 报不出当前
        /// clip，渲染退回绑定姿态（双臂平伸）。</para>
        /// </summary>
        private bool IsRunBand(float speed)
        {
            var threshold = CsViewTuning.AnimRunSpeedThreshold;
            var band = CsViewTuning.AnimRunSpeedHysteresis;
            var running = string.Equals(_lastState, CsViewTuning.PStateRun[0], System.StringComparison.Ordinal);
            return running ? speed > threshold - band : speed > threshold + band;
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
                var cand = CsViewTuning.PlayerShootStates(actor.ActiveWeapon, actor.IsCrouching, _shootVariant);
                _shootVariant ^= 1;
                var st = ResolveState(cand);
                if (st != null) { _anim.Play(st, 0f); _overrideState = st; }
                // 别人的开火要有 3D 枪声 + 第三人称枪口表现。本地玩家走第一人称那一套
                // （CombatModule.HandleLocalShot）—— 这里必须排除，否则同一次开火会出现两处火光。
                if (!_isLocal)
                    CombatModule.Instance?.FxRemoteShot(this, actor.ActiveWeapon, actor.Silenced, actor.EyePosition);
            }
            _preNextFireTime = actor.NextFireTime;

            if (_overrideState != null) return;      // 一次性动作播完前不改状态

            var want = ResolveState(LocomotionStates(actor));
            if (want != null && (want != _lastState || _instantLoco))
            {
                _lastState = want;
                // 刚绑定 / 刚复活：状态机里还留着上一世（或尸体）的片段，过渡会从那个姿势渐变过来，
                // 头一帧就是绑定姿态 ⇒ 直接切。
                if (_instantLoco)
                {
                    _instantLoco = false;
                    _anim.Play(want, 0f);
                    _poseDirty = true;
                }
                else
                {
                    _anim.CrossFade(want, CsViewTuning.AnimCrossFade);
                }
                // 位移态是逐帧重算的连续量：按时间抽样留一条现场，不每次切换都打。
                if (CloverEngine.LogThrottle.ShouldLog("view.loco:" + name, 5f))
                {
                    _log.Info("anim.loco",
                        $"角色视图「{name}」位移状态 → {want}（水平速度 {HorizontalSpeed(actor):F3} m/s，" +
                        $"蹲={actor.IsCrouching} 离地={!actor.OnGround}）");
                }
            }
        }

        /// <summary>
        /// 位移序列的**播放速率** = 实际水平速度 ÷ 该序列的设计速度。
        ///
        /// <para><b>为什么不能固定 1 倍速</b>：原版按**走过的距离**推进步态
        /// （<c>原版资源/cs16src/regamedll/dlls/player.cpp:9192-9195</c>：
        /// <c>m_flGaitframe += (m_flGaitMovement / pseqdesc-&gt;linearmovement.x) * pseqdesc-&gt;numframes</c>）
        /// ⇒ 播放速率 = 实际速度 ÷ 设计速度。固定 1 倍速时，walk 档顶（135 u/s = 3.43 m/s）脚每循环只推
        /// 1.52 m 而身体走了 3.43 m ⇒ 滑步 1.9 m/s；<c>run</c> 满速 250 u/s 的设计速度正好是 6.35 m/s
        /// ⇒ 跑满速时速率 = 1，与现状一致。</para>
        ///
        /// <para>只改位移态（<see cref="CsViewTuning.LocoDesignSpeed"/> 非 0 的那三类）；一次性动作
        /// （开火 / 换弹 / 受击 / 倒地）与空中一律 1 倍速，尸体冻结由 <see cref="EnsureCorpseShown"/> 管，
        /// 这里在 <see cref="_alive"/> 为假时整段不碰。</para>
        /// </summary>
        private void ApplyLocoPlaybackRate(CsActor actor)
        {
            if (_animator == null || !_alive) return;

            var rate = 1f;
            if (_overrideState == null)
            {
                var design = CsViewTuning.LocoDesignSpeed(_lastState);
                if (design > 0f) rate = HorizontalSpeed(actor) / design;
            }

            // 过渡期间强制 1 倍速：Animator.speed 同时缩放状态过渡，速度趋 0 时 0.1s 的 CrossFade
            // 会冻在 weight 0（walk 第 0 帧双臂就是绑定姿态）⇒ T-pose。
            if (_animator.IsInTransition(0) || (_upperLayerReady && _animator.IsInTransition(UpperLayer)))
                rate = 1f;

            // 速度下限：卡地形/被挤住时水平速度为 0，若把 Animator.speed 也设成 0，动画会停在该段第 0 帧，
            // 首次进入状态时甚至连过渡都不求值 ⇒ 角色回到绑定姿态（双臂展开的 T-pose）。给一个极小下限，
            // 只影响表现，不影响模拟。
            if (rate < 0.2f) rate = 0.2f;
            if (Mathf.Abs(_animator.speed - rate) > 0.0001f) _animator.speed = rate;
        }

        /// <summary>
        /// 上身通道：只要有活人身体，就把 layer <see cref="UpperLayer"/>（上身 AvatarMask，生成器
        /// <c>AnimSetup.BuildUpperStates</c>）的权重钉在 1，并常驻当前武器那条瞄准姿势
        /// —— 位移态、站定、走跑过渡期都一样，layer0 的选状态逻辑一行不动。
        ///
        /// <para><b>为什么必须常驻</b>：原版是双通道 —— 腿走 <c>gaitsequence</c>、上身走 aim 序列
        /// （<c>原版资源/cs16src/regamedll/dlls/player.cpp:2925-2928</c> 的
        /// <c>pev-&gt;gaitsequence = LookupActivity(ACT_WALK)</c> 与 <c>:2875-2929</c> 的
        /// <c>GetAnimDesired("aim", ANIM_CROUCH)</c>）。本工程的位移序列
        /// （<c>walk</c> / <c>run</c> / <c>crouchrun</c> / <c>jump</c>）是整身一条，其中 <c>walk</c> 的臂是
        /// 绑定姿态（<c>terror.mdl</c> 帧 0 的 <c>Bip01 L UpperArm</c> 欧拉角 ≈ 0）⇒ 这一层一旦不在，
        /// 画面就是双臂平伸。layer0 在过渡期（<c>IsInTransition</c>）报不出稳定的当前 clip，
        /// 走跑分档抖动时更是长期停在过渡里 ⇒ 这层不能按"当前位移态是哪一条"来开关。</para>
        ///
        /// <para><b>三条关层的情形</b>（都在 layer0 已经整身播好对应序列之后）：
        /// ① 死亡 / 尸体（倒地序列整身播）；② 一次性动作（开火 / 换弹 / 受击，见
        /// <see cref="_overrideState"/>）且 layer0 的过渡已走完 —— 过渡期间仍保持本层，
        /// 否则过渡头几帧露出的正是上一段（<c>walk</c>）的绑定手臂；③ 第 2 层里没有对应状态
        /// （老 controller）—— 此时告警并降级。</para>
        ///
        /// <para><b>蹲姿走 <c>crouch_aim_*</c></b>：原版蹲姿的上身是 <c>crouch_aim_*</c>
        /// （<c>GetAnimDesired("aim", ANIM_CROUCH)</c> 在 <c>FL_DUCKING</c> 时取 <c>crouch_</c> 前缀，
        /// <c>player.cpp:2647</c>），生成器把这两组同样放进了第 2 层
        /// （<c>AnimSetup.IsUpperBlendSequence</c>）⇒ 蹲下时切到蹲姿瞄准，不关层。</para>
        /// </summary>
        private void DriveUpperChannel(CsActor actor)
        {
            if (_animator == null || !_upperLayerReady) return;

            // ① 死亡 / 尸体：倒地序列要整身播。
            if (!_alive)
            {
                CloseUpperChannel();
                return;
            }

            // ② 一次性动作由 layer0 整身播；但 layer0 还在过渡、或这一帧还报不出任何 clip 时先保持本层 ——
            //    那时它渲染的仍是上一段（walk 的帧 0 双臂就是绑定姿态），关层会把平伸的手臂露出来。
            if (_overrideState != null && !_animator.IsInTransition(0) && Layer0HasClip())
            {
                CloseUpperChannel();
                return;
            }

            var aim = ResolveState(actor.IsCrouching
                ? CsViewTuning.PlayerCrouchAimStates(actor.ActiveWeapon)
                : CsViewTuning.PlayerAimStates(actor.ActiveWeapon));
            if (aim == null)
            {
                CloseUpperChannel();
                return;
            }

            if (aim != _upperState)
            {
                if (!_animator.HasState(UpperLayer, Animator.StringToHash(aim)))
                {
                    // 非预期分支：第 2 层里没有这条 aim 状态（老 controller / 状态没建齐）⇒ 别静默地一直没上身。
                    if (_upperWarned.Add("state:" + name + ":" + aim))
                    {
                        _log.Warn("anim.upper.nostate",
                            $"角色视图「{name}」的第 {UpperLayer} 层里没有状态「{aim}」—— 上身 aim 通道不生效" +
                            "（该姿势下会露出绑定姿态的手臂）。请重跑菜单 Clover/CS16/整理视图与音效资源");
                    }
                    CloseUpperChannel();
                    return;
                }
                var reopen = _upperState == null;
                _upperState = aim;
                if (reopen)
                {
                    // 层刚打开（复活 / 一次性动作结束）：层里还留着上一次的片段，过渡会从那个姿势渐变过来。
                    _animator.Play(Animator.StringToHash(aim), UpperLayer, 0f);
                    _poseDirty = true;
                }
                else
                {
                    // 固定时长（秒）版本：过渡快慢不随该层片段长度变化。
                    _animator.CrossFadeInFixedTime(aim, CsViewTuning.AnimCrossFade, UpperLayer);
                }
            }
            if (_animator.GetLayerWeight(UpperLayer) != 1f) _animator.SetLayerWeight(UpperLayer, 1f);
        }

        /// <summary>layer0 这一帧是否已经报得出 clip（过渡刚起 / 刚 <c>Play</c> 的那一帧报不出来）。</summary>
        /// <summary>
        /// 绑定姿态兜底：layer0 一个 clip 都报不出来时，画面上就是绑定姿态（双臂平伸的 T 字）。
        /// 强制回 idle（直接切 + 本帧求值），并把上身 aim 层打开，保证渲染前一定有姿态。
        /// </summary>
        private void EnsureNoBindPose(CsActor actor)
        {
            if (_animator == null || _anim == null || !_alive) return;

            var hasClip = Layer0HasClip();
            // walk 序列的第 0 帧双臂就是绑定姿态：只要 layer0 还在报 walk、而上身 aim 没盖满，
            // 画面上就会露出平伸的手臂 —— 这一帧也要把上身层顶上去。
            var walkExposed = hasClip && Layer0ClipIsWalk() &&
                              _animator.GetLayerWeight(UpperLayer) < 0.999f;
            if (hasClip && !walkExposed) return;

            if (!hasClip)
            {
                var idle = ResolveState(CsViewTuning.PStateIdle);
                if (idle != null)
                {
                    _anim.Play(idle, 0f);
                    _lastState = idle;
                    _overrideState = null;
                    _poseDirty = true;
                }
            }

            if (_upperLayerReady)
            {
                var aim = ResolveState(actor.IsCrouching
                    ? CsViewTuning.PlayerCrouchAimStates(actor.ActiveWeapon)
                    : CsViewTuning.PlayerAimStates(actor.ActiveWeapon));
                if (aim != null)
                {
                    _animator.Play(Animator.StringToHash(aim), UpperLayer, 0f);
                    _animator.SetLayerWeight(UpperLayer, 1f);
                    _upperState = aim;
                    _poseDirty = true;
                }
            }

            if (!_bindPoseWarned)
            {
                _bindPoseWarned = true;
                _log.Warn("anim.bindpose.guard",
                    $"角色视图「{name}」layer0 本帧无 clip（绑定姿态）→ 已强制回 idle 并打开上身瞄准层");
            }
        }

        private bool Layer0HasClip()
        {
            _clipBuf.Clear();
            _animator.GetCurrentAnimatorClipInfo(0, _clipBuf);
            for (var i = 0; i < _clipBuf.Count; i++)
            {
                if (_clipBuf[i].clip != null) return true;
            }
            return false;
        }

        /// <summary>layer0 本帧是否在报 walk 序列（它的第 0 帧双臂是绑定姿态，必须由上身层盖住）。</summary>
        private bool Layer0ClipIsWalk()
        {
            _clipBuf.Clear();
            _animator.GetCurrentAnimatorClipInfo(0, _clipBuf);
            for (var i = 0; i < _clipBuf.Count; i++)
            {
                var c = _clipBuf[i].clip;
                if (c != null && c.name.IndexOf("walk", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>关掉上身通道（层权重 0 + 忘掉当前喂过的状态名）。幂等，每帧调也只做一次写值。</summary>
        private void CloseUpperChannel()
        {
            _upperState = null;
            if (_animator == null || !_upperLayerReady) return;
            if (_animator.GetLayerWeight(UpperLayer) != 0f) _animator.SetLayerWeight(UpperLayer, 0f);
        }

        /// <summary>
        /// 把"视线相对移动方向"喂给 Animator 的两个 blend 轴（<see cref="CsAimBlend"/> 的两个参数）。
        ///
        /// <para><b>契约</b>：pitch 轴 = <c>-actor.Pitch</c>（原版 <c>pev-&gt;angles.x</c> 正 = 向下看，
        /// 本工程的 <see cref="CsActor.Pitch"/> 正 = 抬头）；yaw 轴 = 视线 yaw − 移动方向 yaw，移动方向由
        /// **水平速度方向**给出（原版 <c>StudioEstimateGait</c> 同款 <c>atan2(vy,vx)</c>；两个角度同域，
        /// 差值即原版 <c>pev-&gt;angles.y - m_flGaityaw</c>）。静止（速度低于
        /// <see cref="CsViewTuning.AnimMoveSpeedEpsilon"/>）时把移动方向当作视线方向 —— 原版在没有速度时
        /// 让 <c>m_flGaityaw</c> 追平视线方向，结果同为"相对角 0"。</para>
        ///
        /// <para>只有"controller 里真有这两个参数"的模型才喂（见 <see cref="_aimParamsReady"/>）。
        /// 参数只在多路 BlendTree 的 state 上有意义，其余 state 忽略它。</para>
        /// </summary>
        private void FeedAimBlend(CsActor actor)
        {
            if (!_aimParamsReady || _animator == null) return;

            var v = actor.Velocity;
            var speed = new Vector2(v.x, v.z).magnitude;
            var moving = speed > CsViewTuning.AnimMoveSpeedEpsilon;
            var gaitYaw = moving ? Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg : actor.Yaw;

            var yaw = CsAimBlend.YawAxis(actor.Yaw, gaitYaw, moving);
            var pitch = CsAimBlend.PitchAxis(-actor.Pitch);
            _animator.SetFloat(CsAimBlend.ParamYaw, yaw);
            _animator.SetFloat(CsAimBlend.ParamPitch, pitch);

            // 喂值留痕（只在轴值明显变化时打）：BlendTree 停在默认格时画面上是"双手举枪朝天"，
            // 只能靠这两个值判断是"喂错"还是"没喂"。格心 = 0 / 0.5 / 1。
            if (Mathf.Abs(yaw - _aimYawLogged) > 0.02f || Mathf.Abs(pitch - _aimPitchLogged) > 0.02f)
            {
                _aimYawLogged = yaw;
                _aimPitchLogged = pitch;
                _log.Info("anim.aim", $"角色视图「{name}」瞄准轴 AimYaw={yaw:F3}（第 {CsAimBlend.AxisCell(yaw)} 格）" +
                    $" AimPitch={pitch:F3}（第 {CsAimBlend.AxisCell(pitch)} 格）｜视线 yaw={actor.Yaw:F1}° " +
                    $"pitch={actor.Pitch:F1}° 移动={moving}");
            }
        }

        /// <summary>
        /// controller 上有没有那两个瞄准轴参数。
        ///
        /// <para><b>枚举不到参数表时按"有"处理</b>：<c>Animator.parameters</c> 依赖 Animator 已完成初始化，
        /// 而本工程的角色 Animator **没有 Avatar**（预制体里 <c>m_Avatar: 0</c>）—— 那种情况下参数表可能是空的。
        /// 判成"没有"的后果不是少一个特性：BlendTree 会一直停在参数默认值上（默认值已由生成器钉在中格
        /// 0.5 / 0.5），而站定时 layer0 与走动的第 2 层都在播这条树 ⇒ 姿势停在某一格不动。
        /// 9 份 player controller 都由生成器写入这两个参数，所以这里宁可按"有"处理并留一条 Info。</para>
        /// </summary>
        private bool AimParamsAvailable()
        {
            var ps = _animator.parameters;
            if (ps.Length == 0)
            {
                _log.Info("anim.aim.assume",
                    $"角色视图「{name}」读不到 Animator 参数表（无 Avatar 的 Animator）⇒ 按「有 AimYaw/AimPitch」处理");
                return true;
            }
            var has = HasFloatParameter(_animator, CsAimBlend.ParamYaw)
                      && HasFloatParameter(_animator, CsAimBlend.ParamPitch);
            if (!has)
            {
                _log.Warn("anim.aim.noparams",
                    $"角色视图「{name}」的 controller 没有 AimYaw/AimPitch —— 瞄准姿势不会随方向变" +
                    "（请重跑生成器重建 controller）");
            }
            return has;
        }

        /// <summary>
        /// controller 里有没有这个 float 参数（大小写敏感，与生成器写的名字逐字一致）。
        /// <para><c>Animator.parameters</c> 每次调用都会新建数组 ⇒ 只在装配时问一次并缓存。</para>
        /// </summary>
        private static bool HasFloatParameter(Animator animator, string name)
        {
            var ps = animator.parameters;
            for (var i = 0; i < ps.Length; i++)
            {
                if (ps[i].type == AnimatorControllerParameterType.Float && ps[i].name == name) return true;
            }
            return false;
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
            // 相机在眼位（本地第一人称玩家 / 观战档位 4 的被观察者）时整段本体被 HideOwnRenderers 隐藏
            // （Bind 与每帧两处），而运行时挂上来的武器不在那一快照里 ⇒ 画面里会多出一把第三人称世界枪
            // 悬在眼前（用户报"观战时出现第三人称枪的视角，多余"）。原版这两条路的第一人称都只渲染
            // 视模型（v_*.mdl 挂在 fps 相机下，由 ViewModelRig 负责），第三人称武器模型只属于旁观追尾视角
            // 与尸体那一路（EnterCorpse 的 ShowOwnRenderers 把 _alive 翻 false 后才会挂上）。
            if (EyeCam && _alive) { ReleaseHeldWeapon(); return; }

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

            // 缓存门：预制体还没落进缓存 / 已确认缺失 ⇒ 不 Spawn（直接 Spawn 会绕开缓存门，
            // 缺 prefab 的路径每帧打一条 [ObjectPool] Prefab not found Error）。
            // 未就绪时发起一次异步探测，已确认缺失的只报一次信息（原版手雷本就无世界模型）。
            var vm = ViewModule.Instance;
            if (vm == null || !vm.EnsureWorldModelReady(path))
            {
                if (_heldMissingLogged.Add(weaponId))
                {
                    _log.Info("held.nomodel",
                        $"角色视图「{name}」的武器「{weaponId}」世界模型 {path} 不可用" +
                        "（原版手雷本无 w_*.mdl ⇒ 不显示；若该武器原版有则是预制体缺失）");
                }
                return;
            }

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
        /// 第三人称持枪的**瞄准朝向**：把枪管（载体给的原点→枪口方向）转到该 actor 的 aim 方向。
        ///
        /// <para><b>为什么需要这一步</b>：本件给模型根只下 yaw（<c>Apply</c> 里
        /// <c>Quaternion.Euler(0f, actor.Yaw, 0f)</c>），而原版引擎会把玩家 pitch 灌进玩家模型的
        /// **骨控制器** —— <c>terror.mdl</c> 实测 <c>numbonecontrollers = 1</c>：控制器在
        /// <c>Bone01</c>（父 <c>Bip01 Head</c>）上、类型 <c>ZR</c>、量程 <c>[0, 30]°</c>
        /// （逐字段读自 <c>原版资源/cs16src/cstrike/cstrike__models__player__terror__terror.mdl</c>；
        /// 读法 = <c>.ai-tmp/test/specfix2-bonecontroller.py</c>）⇒ 原版第三人称的枪随俯仰抬落。
        /// 我们没移植那一步 ⇒ 敌人抬枪时枪口不指向射击目标。</para>
        ///
        /// <para><b>做法</b>：枪管方向取**载体自己的枪口列**（<c>MuzzleLocalPosition</c> −
        /// <c>LocalPosition</c>，两者同在手骨局部系），再用**最小旋转**
        /// （<c>Quaternion.FromToRotation</c>）把它拉到 <see cref="CameraMath.AimDirection"/> 给出的
        /// 瞄准方向 —— 枪的横滚仍由手骨动画决定，只有"管口指向"被拉正。</para>
        ///
        /// <para>⚠️ 手骨位姿在本帧的 Animator 求值**之前**读取 ⇒ 修正量有一帧延迟（60 fps 下不可见）。</para>
        /// </summary>
        private void AimHeldWeapon(CsActor actor)
        {
            if (_heldWeapon == null || _weaponSocket == null) return;

            var align = CsViewTuning.HeldWeaponAlignOf(_heldKey);
            var barrelLocal = align.MuzzleLocalPosition - align.LocalPosition;
            if (barrelLocal.sqrMagnitude < 1e-8f)
            {
                // 表里没有该武器 ⇒ 不给朝向（⛔ 不瞎补）。留痕：这时枪完全跟着手骨的朝向走，
                // 任何手臂姿势异常都会把枪指向带歪。
                if (_aimTableWarned.Add(_heldKey ?? "(null)"))
                {
                    _log.Warn("held.noalign",
                        $"角色视图「{name}」的武器「{_heldKey}」没有手部对齐行 ⇒ 枪口不会朝视线拉正，只跟手骨走");
                }
                return;
            }

            // 枪管方向必须取**武器自己当前的朝向**（不是挂点朝向）：本方法每帧都会改
            // `_heldWeapon.transform.rotation`，若拿"挂点 + 静态对齐"那个**未修正**的方向当起点，
            // 每帧都会再叠一次同样的修正 ⇒ 枪会自己转走（Play 实测 angleToAim 70~115°）。
            // 载体给的「枪口 − 枪身原点」是**手骨局部系**的量，换算到武器局部系要左乘 alignRot⁻¹
            // （alignRot = 武器在手骨系里的朝向，就是下面 `Quaternion.Euler(align.LocalEuler)`）。
            // 这样写是**幂等**的：修正后下一帧 barrelWorld == aim ⇒ 修正量退化为 identity。
            var barrelInGun =
                Quaternion.Inverse(Quaternion.Euler(align.LocalEuler)) * barrelLocal.normalized;
            var barrelWorld = (_heldWeapon.transform.rotation * barrelInGun).normalized;
            var aim = CameraMath.AimDirection(actor.Yaw, actor.Pitch);
            _heldWeapon.transform.rotation =
                Quaternion.FromToRotation(barrelWorld, aim) * _heldWeapon.transform.rotation;
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
            // 眼位 = 站姿眼高（测试入口不区分蹲姿）。
            CombatModule.Instance?.FxRemoteShot(this, id, false, transform.position + Vector3.up * CsConst.EyeHeight);
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
        /// （**三选一随机**取一条，同原版"每次死亡的倒地姿势不同"）。播完冻住留场（见 <see cref="_corpseHeld"/>）。
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

            // 起始条 = 随机抽一条（原版 death1..3 每次随机取，不是按 actorId 固定）。
            // 随机源走 CsRng 的表现层子流 FxVariant（独立于玩法各流）⇒ 抽多少都不推位玩法序列，
            // 且随本局 seed 可重放；抽到的起始条进日志，取证直接读日志。
            var arr = CsViewTuning.PStateDeath;
            var start = CsRng.Stream(CsRngStream.FxVariant).Next(0, arr.Length);
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
            _log.Info("anim.death.play", $"角色视图「{name}」死亡 → 状态 {st}（随机起始条 {start + 1}/{arr.Length}，蹲={actor != null && actor.IsCrouching}）");
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

            // 收实时阴影：场景里那盏 Directional 带软阴影，角色材质是 Standard（受光）⇒
            // 身体网格必须 receiveShadows = true、lightProbeUsage 不能是 Off（Off 时连探针/环境光都不参与）。
            // 预制体里本就是这两项的正确值，这里做**运行期兜底**：异步加载进来的网格、池复用实例、
            // 以及被别的系统改过的实例都回到"能收影子"的状态。
            var fixedShadow = 0;
            for (var i = 0; i < smrs.Length; i++)
            {
                var smr = smrs[i];
                if (smr == null) continue;
                if (!smr.receiveShadows)
                {
                    smr.receiveShadows = true;
                    fixedShadow++;
                }
                if (smr.lightProbeUsage == LightProbeUsage.Off)
                {
                    smr.lightProbeUsage = LightProbeUsage.BlendProbes;
                    fixedShadow++;
                }
            }
            if (fixedShadow > 0 && !_shadowReceiveLogged)
            {
                _shadowReceiveLogged = true;
                _log.Warn("shadow.receive.fix",
                    $"角色视图「{name}」有 {fixedShadow} 处渲染设置不能收实时阴影（receiveShadows / lightProbeUsage），" +
                    "已按 Standard 受光口径改正 —— 否则角色在光下像一块平面贴纸");
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

            // 判据：对齐后"模型最低点相对根"的残差应 ≈ 0（≤ 1 mm）。留一条 1 Hz 的日志，
            // 观战里看到脚离地时先看这个数：残差接近 0 = 姿态本身抬了脚（走路迈步），不是浮空。
            if (Time.unscaledTime >= _nextFeetLogAt && !float.IsInfinity(minY))
            {
                _nextFeetLogAt = Time.unscaledTime + 1f;
                var residual = rootY - (minY + (clamped - _modelLift));
                if (Mathf.Abs(residual) > 0.001f || Mathf.Abs(clamped) > 0.005f)
                {
                    _log.Info("feet.align." + name,
                        $"角色视图「{name}」贴地对齐：姿态={_lastState ?? "(无)"} 最低点={minY:F3}m 根={rootY:F3}m " +
                        $"对齐量={clamped:F3}m（未截断={want:F3}）⇒ 对齐后残差={residual:F4}m");
                }
            }

            if (Mathf.Abs(clamped - _modelLift) < 0.0001f) return;      // 没变就不写 transform（省一次 dirty）
            _modelLift = clamped;
            _body.localPosition = new Vector3(0f, _modelLift, 0f);
        }

        /// <summary>
        /// 脚底圆影：着地时贴脚面、离地时停在**起跳落点**那一层高度上淡出缩小。
        ///
        /// <para>不逐帧向下射线：<c>OnGround</c> 为真时 <c>CsActor.Position.y</c> 就是脚面高度
        /// （脚底对齐见 <see cref="AlignFeetToGround"/>），拿它当影子的 y 与地面严格同高；
        /// 离地时继续用最后一次着地的 y ⇒ 影子留在地面，不跟着人飞起来。</para>
        ///
        /// <para>相机在本角色眼位（第一人称 / 观战档位 4）时整具本体都不画，圆影一起藏。</para>
        /// </summary>
        private void ApplyBlobShadow(CsActor actor)
        {
            if (_blobShadow == null) return;

            var pos = actor.Position;
            if (actor.OnGround || !_blobShadowGroundReady)
            {
                _blobShadowGroundY = pos.y;
                _blobShadowGroundReady = true;
            }

            _blobShadow.SetVisible(!EyeCam);
            _blobShadow.Tick(pos, _blobShadowGroundY, actor.OnGround, Time.deltaTime);
        }

        // ==================================================================
        //  每帧同步（由 ViewModule 在 LateUpdate 调用）
        // ==================================================================
        /// <summary>把 actor 的权威状态刷到视图上。只在**活着**时改位置/朝向。</summary>
        /// <param name="actor">权威 actor（只读）</param>
        public void Apply(CsActor actor)
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
                        _instantLoco = true;
                        _haveAnimSnapshot = false;
                    }
                }
                // 死亡**不立刻隐藏**：先播原版倒地序列，播完（OnComplete）**冻住留场**。
                // 没有动画时（预制体里是静态网格）走兜底：立即隐藏。
                else if (_anim == null) SetShown(false);
            }

            if (!_alive)
            {
                // 尸体留场期间逐帧钉住"上身层关着"：倒地序列是整身的，层开着会让上半身僵在
                // 上一次的瞄准姿势、腿在倒地。只靠 EnterCorpse 关一次不够 —— 尸体被动画冻结
                // （Animator.speed = 0）后，任何一次残留的层权重都会被原样冻在画面上。
                CloseUpperChannel();
                // 原版死亡即掉枪（DroppedWeapons 系统会生成掉落物）⇒ 手上那把同时消失。
                ReleaseHeldWeapon();
                // 尸体不画脚底圆影：倒地姿态的脚不在原点，圆影会留在一块没有脚的黑斑上。
                if (_blobShadow != null) _blobShadow.SetVisible(false);
                if (!_deathPlayed)
                {
                    _deathPlayed = true;
                    PlayDeath(actor);
                    EnterCorpse();
                }
                // 尸体：确保可见（尸体不被"上一帧的隐藏"带走）+ 名牌/血条关掉（死人头顶不该有血条）。
                if (_corpseHeld) EnsureCorpseShown();
                if (_hideAfterDeath) SetShown(false);
                ApplyBackpack(actor);
                return;
            }

            // ---- 活着就不许停在"尸体冻结速度"上 ----
            // speed = 0 时状态机不推进：任何一次 CrossFade 都会永远停在中途（画面上就是一具定格在
            // 混合姿态的身体）。尸体冻结（见 EnsureCorpseShown）必须只作用在尸体上。
            // 只有 _corpseFrozen 才代表"尸体冻结没清掉"：位移序列的播放速率是逐帧重算的
            // （见 ApplyLocoPlaybackRate），它自己会在 0 与设计速度比值之间变化，不能在这里改写。
            if (_animator != null && _animator.speed <= 0f && _corpseFrozen)
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

            // ---- 位移序列的播放速率：按"走过的距离"推进（见 ApplyLocoPlaybackRate）----
            ApplyLocoPlaybackRate(actor);

            // ---- 上身通道：常驻瞄准姿势（layer0 的腿照旧，见 DriveUpperChannel）----
            DriveUpperChannel(actor);

            // ---- 绑定姿态兜底：layer0 本帧一个 clip 都报不出来时（状态被抢/过渡空拍），
            //      渲染的就是绑定姿态（双臂平伸）。强制回 idle 并打开上身 aim，保证画面上永远不是 T 字。
            EnsureNoBindPose(actor);

            // ---- 本帧做过"直接切"（刚绑定 / 刚复活）⇒ 立刻求值一次姿态 ----
            // Animator 的积分在 LateUpdate 之前，不求值这一帧渲染的仍是刚激活时的绑定姿态。
            if (_poseDirty && _animator != null)
            {
                _poseDirty = false;
                _animator.Update(0f);
            }

            // ---- 瞄准 blend：视线相对移动方向的偏角 → Animator 两个轴（多路 BlendTree 的 state 用）----
            FeedAimBlend(actor);

            // ---- "恒更新"复查（降频 1s）：异步加载的身体网格 / 运行时挂上的武器都必须被覆盖到，
            //      否则它们拿绑定姿态包围盒当可见性判据 ⇒ 姿态卡在绑定姿势 + 网格被裁（见 EnsureAlwaysAnimateTick）----
            EnsureAlwaysAnimateTick();

            // ---- 手里的枪：换枪/拾枪时把模型换掉（别人手里要看得见那把枪，差异 #75 同族）----
            EnsureHeldWeapon(actor);
            // 持枪朝向：按 actor 的 aim（yaw+pitch）拉正枪管（见 AimHeldWeapon 的注释）
            AimHeldWeapon(actor);

            // ---- 脚底贴地：按**当前姿态**（idle / walk / crouch 各有各的最低点）对齐，见 AlignFeetToGround ----
            if (actor.OnGround) AlignFeetToGround();

            // ---- 脚底圆影：贴地 / 离地淡出（必须排在贴地对齐之后，用对齐后的脚底高度）----
            ApplyBlobShadow(actor);

            // ---- 模型级显隐：本组件是唯一权威，逐帧把渲染钉到本帧应有的状态（见 ApplyModelVisibility）----
            ApplyModelVisibility();

            // ---- 背上的 C4 包：与权威持包状态对齐（必须排在模型显隐之后）----
            ApplyBackpack(actor);
        }

        /// <summary>
        /// 模型级显隐的**唯一权威**：每帧把整具模型的 <c>Renderer.enabled</c> 对齐到本帧应有的状态
        /// （相机在本角色眼位 ⇒ 藏；其余 ⇒ 露），而不是只在某个边沿动作。
        ///
        /// <para><b>为什么必须逐帧钉住</b>：相机侧也会为了机位开关同一批渲染（眼位藏本体 / 换目标还原），
        /// 它的自检与这里的边沿各自为政时，两个写者会在相邻帧互相覆盖 —— 画面上就是模型间歇性闪没。
        /// 本方法是 <c>LateUpdate</c> 里最后执行的写者，外部开关只影响本帧中间态，渲染前一定回到这里算出的值。</para>
        ///
        /// <para><b>不在这里管的两条</b>：整具视图关闭归 <see cref="SetShown"/>（<c>gameObject.activeSelf</c>）；
        /// 尸体显隐归 <see cref="EnsureCorpseShown"/>（本地/目标的尸体必须看得见）。</para>
        ///
        /// <para>幂等且便宜：只写与本帧期望不一致的那几个渲染（已经在目标态的跳过）。</para>
        /// </summary>
        private void ApplyModelVisibility()
        {
            if (!IsShown || !_alive) return;

            if (EyeCam)
            {
                HideOwnRenderers();
                return;
            }

            EnableOwnRenderers();
        }

        /// <summary>
        /// 取本具**模型**的全部渲染（排除脚底圆影那一块）。
        ///
        /// <para>为什么必须排除：圆影是一块 SpriteRenderer，它挂在角色根节点下；池复用后的第二世
        /// <c>GetComponentsInChildren</c> 会把它一起收进来 —— 而 <see cref="Measure"/> 会拿它的
        /// 1×1 局部包围盒当角色身高，直接把贴地对齐量算成半米（角色浮空）。
        /// 圆影自己由 <see cref="ApplyBlobShadow"/> 单独管显隐。</para>
        /// </summary>
        private Renderer[] CollectModelRenderers()
        {
            var all = GetComponentsInChildren<Renderer>(true);
            var shadowRoot = _blobShadow != null ? _blobShadow.Root : null;
            if (shadowRoot == null) return all;

            var kept = new List<Renderer>(all.Length);
            for (var i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;
                var t = r.transform;
                if (t == shadowRoot || t.IsChildOf(shadowRoot)) continue;
                kept.Add(r);
            }
            return kept.ToArray();
        }

        /// <summary>
        /// 从本具视图的全部渲染里挑出"背上的 C4 包"那一块。
        ///
        /// <para><b>认法</b>：材质名含 <see cref="CsViewTuning.BackpackMaterialToken"/>（生成器按原版模型
        /// 内嵌贴图名 <c>Backpack1</c>/<c>Backpack2</c> 命名材质），见该常量的出处。生成器把这一块做成了
        /// 独立节点，所以能整块开关 —— 等价于原版对玩家模型 <c>backpack</c> bodygroup 的开关。</para>
        /// </summary>
        private static Renderer[] CollectBackpackRenderers(Renderer[] all)
        {
            var picked = new List<Renderer>(2);
            for (var i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null) continue;

                var mats = r.sharedMaterials;
                for (var j = 0; j < mats.Length; j++)
                {
                    var m = mats[j];
                    if (m == null || string.IsNullOrEmpty(m.name)) continue;
                    if (m.name.IndexOf(CsViewTuning.BackpackMaterialToken,
                            System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    picked.Add(r);
                    break;
                }
            }
            return picked.ToArray();
        }

        // ==================================================================
        //  材质缺贴图兜底（"部分人物模型是白色的"）
        // ==================================================================
        /// <summary>
        /// 贴图查找表（贴图文件名小写 → 贴图）：首次用到时从 <c>Resources/Art/Tex</c> 整目录载入，
        /// 之后全进程复用。null = 还没载过；空表 = 载过但一个贴图都没有。
        /// </summary>
        private static Dictionary<string, Texture2D> _texTable;

        /// <summary>已告过"缺贴图"的材质名（静态去重：多具视图共用同一材质，不许逐具刷屏）。</summary>
        private static readonly HashSet<string> _texWarned = new HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>
        /// 给"缺 <c>_MainTex</c>"的角色材质按材质名回绑贴图（模型发白的兜底修复）。
        ///
        /// <para><b>为什么需要</b>：生成器 <c>ArtSetup.EnsureMaterial</c> 只找裸名
        /// <c>Art/Tex/{材质名}.png</c>，而盘上导出的 PNG 带模型前缀
        /// （<c>player_T_arctic_ARTIC_Working1.png</c>）⇒ arctic / guerilla / leet / gsg9 / sas /
        /// gign / vip 一批皮肤的材质 <c>_MainTex</c> 为空 ⇒ 网格按材质底色渲染成纯白。</para>
        ///
        /// <para><b>认法</b>：贴图名**等于**材质名，或以 <c>_{材质名}</c> 结尾（一律忽略大小写 ——
        /// 导出文件名与材质名大小写不保证一致，如 <c>player_CT_sas_sas_dmbase1</c> vs 材质 <c>SAS_DMBASE1</c>）。
        /// 同名多份（gsg9 / sas 各有一张 Chrome1）任取其一：都是同一块铬合金贴图。</para>
        ///
        /// <para><b>改的是 sharedMaterial</b>（不实例化副本）：同皮肤的所有实例、以及此后从对象池
        /// 复用出来的实例都直接拿到修好的材质；运行期对材质的修改不落盘，退出播放即还原。</para>
        /// </summary>
        private void EnsureTexturesBound()
        {
            var bound = 0;
            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;

                var mats = r.sharedMaterials;
                for (var j = 0; j < mats.Length; j++)
                {
                    var m = mats[j];
                    if (m == null || m.mainTexture != null) continue;

                    var tex = FindTextureFor(m.name);
                    if (tex != null)
                    {
                        m.mainTexture = tex;
                        bound++;
                    }
                    else if (_texWarned.Add(m.name))
                    {
                        _log.Warn("tex.missing",
                            $"材质「{m.name}」缺贴图，且 Resources/{CsViewTuning.TexArtRoot} 里" +
                            "没有同名 / 带前缀的贴图 ⇒ 这块网格会显示纯白");
                    }
                }
            }

            if (bound > 0)
            {
                _log.Info("tex.bound",
                    $"已给 {bound} 块材质回绑缺失的贴图（历史生成器漏绑 _MainTex 的运行期兜底）");
            }
        }

        /// <summary>按材质名在贴图表里找对应贴图：先精确（忽略大小写），再「任意前缀_材质名」后缀匹配。</summary>
        private static Texture2D FindTextureFor(string matName)
        {
            if (string.IsNullOrEmpty(matName)) return null;

            if (_texTable == null)
            {
                _texTable = new Dictionary<string, Texture2D>(128, System.StringComparer.Ordinal);
                var all = Resources.LoadAll<Texture2D>(CsViewTuning.TexArtRoot);
                for (var i = 0; i < all.Length; i++)
                {
                    if (all[i] == null || string.IsNullOrEmpty(all[i].name)) continue;
                    _texTable[all[i].name.ToLowerInvariant()] = all[i];
                }
            }

            var key = matName.ToLowerInvariant();
            if (_texTable.TryGetValue(key, out var exact)) return exact;

            var suffix = "_" + key;
            foreach (var kv in _texTable)
            {
                if (kv.Key.EndsWith(suffix, System.StringComparison.Ordinal)) return kv.Value;
            }
            return null;
        }

        /// <summary>
        /// 背上的 C4 包与权威持包状态对齐：**只有持 C4 的角色显示它** ——
        /// 下包（<c>PlantComplete</c> 置 <c>HasBomb=false</c>）、掉包、换阵营后立即消失。
        ///
        /// <para>本地玩家活着时整具第三人称模型被第一人称隐藏（<see cref="HideOwnRenderers"/>）⇒
        /// 背上的包必须跟着一起藏，否则会在自己眼前露出一块。</para>
        /// </summary>
        private void ApplyBackpack(CsActor actor)
        {
            if (_backpackRenderers.Length == 0) return;

            // 相机在本角色眼位（本地第一人称 / 观战档位 4 的被观察者）且人还活着 ⇒ 整具本体是藏起来的，
            // 背上这块也必须一起藏（否则它会被这一行单独打开，在第一人称画面里露出一块浮空的包）。
            var show = actor != null && actor.HasBomb && !(EyeCam && _alive);
            for (var i = 0; i < _backpackRenderers.Length; i++)
            {
                var r = _backpackRenderers[i];
                if (r != null && r.enabled != show) r.enabled = show;
            }
        }

        /// <summary>把视图直接摆到指定位置（生成 / 复活时先摆好，避免第一帧从池原点飞过来）。</summary>
        public void SnapTo(CsActor actor)
        {
            if (actor == null) return;
            transform.SetPositionAndRotation(actor.Position, Quaternion.Euler(0f, actor.Yaw, 0f));
        }

        // ==================================================================
        //  显隐
        // ==================================================================
        /// <summary>
        /// 显示/隐藏整个视图（含碰撞体）。死亡 → false，复活 → true。
        ///
        /// <para>自身渲染只在**活着的**本地玩家身上才藏（第一人称相机在他眼位）；尸体必须看得见
        /// —— 尸体留场走的就是本方法，不判 <see cref="_alive"/> 的话每具本地尸体都会被这里藏掉。</para>
        /// </summary>
        public void SetShown(bool shown)
        {
            IsShown = shown;
            if (gameObject.activeSelf != shown) gameObject.SetActive(shown);
            if (shown && EyeCam && _alive) HideOwnRenderers();
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
            // 上身 aim 层必须关掉：死亡分支在 DriveUpperChannel 之前就 return，层权重会停在最后一帧的 1
            // ⇒ 倒地序列的上半身被瞄准姿势盖住（尸体上半身僵着、腿在倒地）。
            CloseUpperChannel();
            var off = DisableColliders();
            // 两种"相机已离开他的眼位"都在这儿还渲染：本地玩家死后转观战；观战档位 4 的目标死后
            // 相机要换人（观战对象只取活人）⇒ 顺手把眼位标记清掉，免得尸体被当成眼位继续藏着。
            if (_isLocal || _spectateEye)
            {
                _spectateEye = false;
                ShowOwnRenderers();
            }
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
        /// 尸体留场：**确保可见 + 姿态冻住 + 碰撞体全关**（每具尸体只做一次收尾）。
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
            var back = EnableOwnRenderers();
            if (back > 0)
            {
                _bodyHiddenLogged = false;      // 复活时"第一人称已隐藏"那条日志要能再报一次
                _log.Info("corpse.local.shown",
                    $"本地玩家阵亡：把自己模型的 {back} 个渲染开回来 —— 观战相机只跟活人，尸体不该被藏");
            }
        }

        /// <summary>把本视图模型上关着的渲染全部开回来，返回本次真的开回来的个数（幂等）。</summary>
        private int EnableOwnRenderers()
        {
            var back = 0;
            for (var i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null || r.enabled) continue;
                r.enabled = true;
                back++;
            }
            return back;
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
