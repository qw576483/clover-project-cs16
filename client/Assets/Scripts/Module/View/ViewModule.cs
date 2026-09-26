using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Match;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.View
{
    /// <summary>
    /// **角色视图 + 第一人称武器视图**的宿主（由 <c>Bootstrap</c> 挂在常驻对象上）。
    ///
    /// <para><b>职责边界</b>：本模块只做"把 <see cref="ICsMatch"/> 的权威状态画出来" ——
    /// 位置/朝向只读 <see cref="CsActor"/>，一个字段都不写；伤害/回合/经济/买枪一概不碰；
    /// 本模块只负责"音效资源 + 脚步/换弹/命中/回合/炸弹蜂鸣"（见 <c>Module/Audio</c>）。</para>
    ///
    /// <para><b>为什么在 LateUpdate</b>：模拟在 <c>MatchModule.Update</c>（默认 order 0）推进，
    /// 本模块跟着在 LateUpdate 里读结果，保证"画面上的位置 == 本帧模拟的位置"，不会慢一帧。</para>
    ///
    /// <para><b>两个硬约束</b>：</para>
    /// <list type="number">
    /// <item>每个角色视图必须有**恰好 4 个** <see cref="CsHitboxProxy"/>（头/胸/腹/腿），
    /// 层用 <c>PhysicsLayers.Bot</c>/<c>Player</c> —— 少了视图侧的射线就打不中；</item>
    /// <item>本地玩家自己的第三人称模型要**隐藏渲染但保留碰撞体**（否则自己被自己的模型挡屏）。</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// 早于默认的 <c>0</c>。视图只读模拟结果，这个顺序保证"画面上的位置 == 本帧模拟的位置"。
    /// </remarks>
    [DefaultExecutionOrder(ExecutionOrder)]
    public sealed class ViewModule : MonoBehaviour
    {
        private const string Tag = "View";

        /// <summary>本模块的执行顺序（见类注释；必须晚于 <c>PlayerModule</c> 的 -200）。</summary>
        public const int ExecutionOrder = -100;

        // ---- 引擎对象池（Game.Pool）的分组名：仅作标签（ClearGroup 用），三样对象各一组便于排查 ----

        /// <summary>角色视图实例的池分组。</summary>
        private const string PoolGroupActorViews = "cs16.actorview";
        /// <summary>头顶名牌实例的池分组。</summary>
        private const string PoolGroupNameplates = "cs16.nameplate";
        /// <summary>世界掉落武器视图的池分组。</summary>
        private const string PoolGroupDrops = "cs16.dropview";

        private readonly CsModuleLog _log = new CsModuleLog(Tag);

        /// <summary>actorId → 视图。</summary>
        private readonly Dictionary<long, ActorView> _views = new Dictionary<long, ActorView>(32);
        private readonly List<long> _seen = new List<long>(32);
        private readonly List<long> _stale = new List<long>(8);

        /// <summary>已加载的预制体（含"加载中"的占位，避免同一路径重复发起加载）。</summary>
        private readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>(16);

        /// <summary>世界掉落武器视图（差异 #75）：数据对象 → 场景实例。键用**数据对象本身**（引用唯一）。</summary>
        private readonly Dictionary<CsDroppedWeapon, CsDroppedWeaponView> _dropViews =
            new Dictionary<CsDroppedWeapon, CsDroppedWeaponView>(16);

        /// <summary>回收掉落视图时的临时列表（避免遍历时改字典）。</summary>
        private readonly List<CsDroppedWeapon> _dropScratch = new List<CsDroppedWeapon>(16);
        private readonly HashSet<string> _pending = new HashSet<string>();
        private readonly HashSet<string> _missing = new HashSet<string>();

        /// <summary>皮肤随机分配（CS 1.6 每个玩家在队内随机一个皮肤）；按 actorId 稳定。</summary>
        private readonly Dictionary<long, string> _skinOf = new Dictionary<long, string>(32);

        /// <summary>本地玩家的 actorId（<see cref="EnsureView"/> 里记下）：只有它按「选兵种页选的兵种」取皮肤。</summary>
        private long _localId;

        /// <summary>已就「该兵种在本工程没有模型数据」告警过的兵种行号（只告一次，不刷屏）。</summary>
        private readonly HashSet<int> _missingClassSkin = new HashSet<int>();

        private MatchModule _matchModule;
        private ICsMatch _match;
        private Transform _root;
        private ViewModelRig _viewModel;
        private bool _ready;
        private bool _wasRunning;

        /// <summary>当前活着的视图模块（装配点唯一；取不到就是 null，取用方自己判空并留痕）。</summary>
        public static ViewModule Instance { get; private set; }

        /// <summary>读条预热的在途加载数。</summary>
        private int _warmupPending;
        /// <summary>读条预热的等待者：全部预制体落定（进缓存 / 确认缺失）后一次性回调。</summary>
        private readonly List<System.Action> _warmupWaiters = new List<System.Action>(4);
        private readonly List<string> _warmupPaths = new List<string>(16);
        /// <summary>逐帧实例化预热：待做队列 / 已热过的路径 / 宿主节点 / 游标。</summary>
        private readonly List<string> _warmQueue = new List<string>(16);
        private readonly HashSet<string> _warmInstances = new HashSet<string>();
        private GameObject _warmRoot;
        private int _warmIndex;
        private float _warmT0;

        /// <summary>预热总预算（秒）：从 <see cref="WarmUpMatchPrefabs"/> 发起那一刻起算，到点立即收尾放行。</summary>
        private const float WarmupDeadlineSeconds = 14f;

        /// <summary>预热阶段：<c>0</c> = 空闲；<c>1</c> = 逐帧实例化；<c>2</c> = 渲染预热（见 <see cref="CsRenderWarmup"/>）。</summary>
        private int _warmPhase;

        /// <summary>本次预热整体起点（实例化段 + 渲染段共用同一份预算）。</summary>
        private float _warmupT0;

        /// <summary>射线只被**世界几何**遮挡（排除角色层，否则目标自己就把视线挡了）。</summary>
        private int _worldOnlyMask;

        /// <summary>上一帧的观战目标 id（0 = 没在观战）——用于只在"目标真的换了"时留一条日志。</summary>
        private long _lastSpectateTargetId;

        /// <summary>本模块最近一次统计（自检用）。</summary>
        public int ViewCount => _views.Count;

        /// <summary>当前是否比赛进行中。</summary>
        public bool MatchRunning => _match != null && _match.IsRunning;

        // ==================================================================
        //  装配
        // ==================================================================
        private void Start()
        {
            _matchModule = GetComponent<MatchModule>();
            if (_matchModule == null)
            {
                Game.Logger.Error(Tag,
                    "ViewModule 拿不到 MatchModule（应与本组件挂在同一个 GameObject 上、由 Bootstrap 装配）。" +
                    "角色/武器视图与音效已被禁用。");
                enabled = false;
                return;
            }

            _match = _matchModule.Match;
            if (_match == null)
            {
                Game.Logger.Error(Tag, "MatchModule.Match 为 null（门面未就绪），角色/武器视图已被禁用。");
                enabled = false;
                return;
            }

            _worldOnlyMask = ~((1 << PhysicsLayers.Bot) | (1 << PhysicsLayers.Player));

            var rootGo = new GameObject("CsActorViews");
            Object.DontDestroyOnLoad(rootGo);
            _root = rootGo.transform;

            // 名牌预制体：先异步加载进缓存（加载好之前视图照常显示，只是没有头顶牌子）。
            // 实例化在 AttachNameplate 里走引擎对象池，按 CsViewTuning.NameplatePath 这个 key 取。
            LoadPrefab(CsViewTuning.NameplatePath, null);

            Instance = this;
            _ready = true;
            _log.Always($"视图模块就绪：角色模型 Art/{{T|CT}}/player，武器视图 viewmodel_*，名牌 {CsViewTuning.NameplatePath}");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ClearViews();
            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
        }

        // ==================================================================
        //  每帧
        // ==================================================================
        private void LateUpdate()
        {
            WarmTick();
            // 门面可能为空（见 PlayerModule.Update 的同款注释）——不判就是每帧一条 NRE。
            if (!_ready || _match == null) return;

            var running = _match.IsRunning;
            if (running != _wasRunning)
            {
                _wasRunning = running;
                if (running)
                {
                    _log.Always($"视图同步开始：本局就位（本地玩家={DescribeLocal()})");
                    // 开局先把已存在的 actor 一次性建齐（避免"进图头几帧没人"）。
                    SyncViews();
                }
                else
                {
                    _log.Always("比赛未运行：回收全部角色视图与武器视图（保持主菜单干净）");
                    ClearViews();
                    ClearDropViews();
                }
            }

            if (!running) return;

            SyncViews();
            SyncDroppedWeapons();
        }

        private string DescribeLocal()
        {
            var l = _match.LocalPlayer;
            return l == null ? "<null>" : $"{l.Name}(id={l.Id}, {l.Team})";
        }

        // ==================================================================
        //  掉落武器视图同步（差异 #75）
        // ==================================================================
        /// <summary>
        /// 让场景里的"地上的枪"与 <c>CsMatch.DroppedWeapons</c> 对齐：
        /// 列表里有而场景没有 ⇒ 造；场景有而列表里没有（被拾取时 CsMatch 会立刻摘掉它）⇒ 销毁。
        /// </summary>
        private void SyncDroppedWeapons()
        {
            var dropped = _match.DroppedWeapons;
            if (dropped == null)
            {
                ClearDropViews();
                return;
            }

            // 回收：数据已不在列表里。
            _dropScratch.Clear();
            foreach (var kv in _dropViews)
            {
                var still = false;
                for (var i = 0; i < dropped.Count; i++)
                {
                    if (ReferenceEquals(dropped[i], kv.Key)) { still = true; break; }
                }
                if (!still) _dropScratch.Add(kv.Key);
            }
            for (var i = 0; i < _dropScratch.Count; i++) DestroyDropView(_dropScratch[i]);

            // 新建：列表里有、场景还没有的。
            for (var i = 0; i < dropped.Count; i++)
            {
                var d = dropped[i];
                if (d.Consumed || _dropViews.ContainsKey(d)) continue;
                EnsureDropView(d);
            }
        }

        private void EnsureDropView(CsDroppedWeapon d)
        {
            // 地上的枪用**世界模型**（Art/world_<武器id>，原版 models/w_*.mdl）——第一人称模型
            // （viewmodel_*）的原点与朝向是给相机用的，直接摆到地上会侧躺、且大半截埋进地面。
            // 没有世界模型的武器（原版并非每把枪都有 w_*.mdl）退回首人称模型，至少"有把枪"。
            var world = true;
            var path = CsViewTuning.ArtRoot + CsViewTuning.WorldModelPrefix + d.WeaponId;
            if (!_prefabs.TryGetValue(path, out var prefab) || prefab == null)
            {
                // 首次走到这里时可能还在异步加载：发起一次，下一帧自动补上（与 EnsureView 同款）。
                LoadPrefab(path, null);
                if (!_missing.Contains(path)) return;

                world = false;
                var folder = d.Team == CsTeam.T ? CsViewTuning.TeamFolderT : CsViewTuning.TeamFolderCT;
                path = CsViewTuning.ArtRoot + folder + "/" + CsViewTuning.ViewModelPrefix + d.WeaponId;
                if (!_prefabs.TryGetValue(path, out prefab) || prefab == null)
                {
                    LoadPrefab(path, null);
                    return;
                }
            }

            // 正是池的用途；它的生命周期终点也是池（Game.Pool.Despawn，见 DestroyDropView / ClearDropViews）。
            // 存在性仍由 _prefabs 缓存把关（上面刚判过），池只负责"造 / 复用"。
            // 不许在这里直接 Destroy —— 池里会留下已销毁的引用，下次 Spawn 把它发给业务。
            var pool = Game.Pool;
            if (pool == null)
            {
                _log.Error("pool.null", "Game.Pool 为 null（Game.Launch 未执行？），掉落武器视图无法生成");
                return;
            }
            var go = pool.Spawn(path, _root, PoolGroupDrops);
            if (go == null)
            {
                // 池自己已记 Error（预制体找不到 / 工厂返回 null）：这里补一条业务语义的留痕
                _log.Warn("dropview.spawnfail", $"掉落武器视图生成失败（对象池返回 null）：{path}");
                return;
            }
            go.name = "DroppedWeapon_" + d.WeaponId;
            go.transform.localScale = Vector3.one * (world ? CsViewTuning.WorldModelScale : CsViewTuning.ViewModelScale);

            // 世界物件绝不能参与射线（与 ViewModelRig 同款：生成器没加，这里再兜一层）。
            var cols = go.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < cols.Length; i++) Object.Destroy(cols[i]);

            // 池复用的实例身上**还挂着上一次的视图组件**（归还只 SetActive(false)，不摘组件）
            // ⇒ 必须复用：直接 AddComponent 会在同一个 GameObject 上叠出第二个视图组件，
            //    旧的仍绑着已被拾取的那份数据（场景里就出现"同一坐标两个视图"）。
            var view = go.GetComponent<CsDroppedWeaponView>();
            if (view == null) view = go.AddComponent<CsDroppedWeaponView>();
            view.Bind(d);
            _dropViews[d] = view;

            _log.Info("dropview.create",
                $"{d.WeaponId} 的世界视图已生成（位置 ({d.Position.x:F2}, {d.Position.y:F2}, {d.Position.z:F2})，" +
                $"模型={path}，{(world ? "世界模型" : "降级用第一人称模型")}）");
        }

        private void DestroyDropView(CsDroppedWeapon d)
        {
            if (!_dropViews.TryGetValue(d, out var view)) return;
            _dropViews.Remove(d);
            // 生命周期终点 = 归还对象池（不是 Destroy：Destroy 会让池里留下已销毁引用）
            if (view != null) Game.Pool.Despawn(view.gameObject);
        }

        private void ClearDropViews()
        {
            if (_dropViews.Count == 0) return;
            foreach (var kv in _dropViews) if (kv.Value != null) Game.Pool.Despawn(kv.Value.gameObject);
            _dropViews.Clear();
            _log.Always("掉落武器视图已全部回收（差异 #75）");
        }

        // ==================================================================
        //  视图集合同步：存在则更新、消失则回收
        // ==================================================================
        private void SyncViews()
        {
            var actors = _match.Actors;
            if (actors == null) return;

            var local = _match.LocalPlayer;
            var localId = local != null ? local.Id : 0;
            var eye = local != null ? local.EyePosition : Vector3.zero;
            var haveEye = local != null && local.IsAlive;

            // 观战中：藏掉被观察者的**世界空间**名牌（Canvas + 血条 + 名字）。
            // 那台相机正对着他 —— 观战机位是第三人称（见 Module/CameraRig/FirstPersonCamera.cs 的
            // ResolveChaseDistance：机位在目标身后 CsConst.ChaseDistance 处），名牌会顶在镜头前糊住画面。
            var spectateTargetId = ResolveSpectateTargetId();

            _seen.Clear();
            for (var i = 0; i < actors.Count; i++)
            {
                var actor = actors[i];
                if (actor == null) continue;
                _seen.Add(actor.Id);

                var view = EnsureView(actor, localId);
                if (view == null) continue;      // 预制体还没加载好 —— 已在 EnsureView 里留过日志

                var isLocal = actor.Id == localId;
                var isSpectateTarget = spectateTargetId != 0L && actor.Id == spectateTargetId;
                var plateVisible = false;
                var showName = false;
                if (!isLocal && !isSpectateTarget && actor.IsAlive)
                {
                    EvaluateNameplate(actor, eye, haveEye, out plateVisible, out showName);
                }

                view.Apply(actor, plateVisible, showName, Time.time);
            }

            // ---- 回收：本帧没出现的 actor（被踢 / 换局 / 重建）----
            _stale.Clear();
            foreach (var kv in _views)
            {
                if (kv.Value == null) { _stale.Add(kv.Key); continue; }
                var found = false;
                for (var i = 0; i < _seen.Count; i++)
                {
                    if (_seen[i] != kv.Key) continue;
                    found = true;
                    break;
                }
                if (!found) _stale.Add(kv.Key);
            }
            for (var i = 0; i < _stale.Count; i++) RemoveView(_stale[i]);

            // ---- 第一人称武器视图 ----
            SyncViewModel();
        }

        /// <summary>
        /// 头顶名牌的可见性判定：**距离 + 视线 + 是否在画面里**（AoI 的简化实现）。
        ///
        /// <para>视线用自己发的射线（而不是 <c>ICsMatch.HasLineOfSight</c>）：因为角色自己的碰撞体
        /// 就挡在射线上，引擎那个通用接口会把目标本人算成遮挡。这里用"只让世界几何挡"的层遮罩，
        /// 语义正是我们要的"中间有没有墙"。</para>
        /// </summary>
        private void EvaluateNameplate(CsActor actor, Vector3 eye, bool haveEye,
            out bool visible, out bool showName)
        {
            visible = false;

            var myTeam = _localTeamOf();
            var sameTeam = actor.Team == myTeam;
            showName = sameTeam || CsViewTuning.ShowEnemyName;
            if (!sameTeam && !CsViewTuning.ShowEnemyHealth) return;

            var to = actor.Position + Vector3.up * CsViewTuning.LosTargetHeight;
            var delta = to - eye;
            var dist = delta.magnitude;
            if (dist < 0.01f || dist > CsViewTuning.NameplateMaxDistance) return;

            // 视野：只在"大致看向他"的时候显示（与 CS 里"看着队友才见名字"一致）
            var cam = Camera.main;
            if (cam != null)
            {
                var cos = Vector3.Dot(cam.transform.forward, delta / dist);
                if (cos < CsViewTuning.NameplateMinFacingDot) return;
            }

            if (haveEye)
            {
                var blocked = Physics.Linecast(eye, to, out _, _worldOnlyMask,
                    QueryTriggerInteraction.Ignore);
                if (blocked) return;
            }

            visible = true;
        }

        private CsTeam _localTeamOf()
        {
            var l = _match != null ? _match.LocalPlayer : null;
            return l != null ? l.Team : CsTeam.Spectator;
        }

        /// <summary>
        /// 本帧的观战目标 id（没在观战 / 目标为空 = 0）。
        ///
        /// <para><b>为什么每帧只解析一次</b>：<c>ICsMatch.SpectateTarget</c> 每次访问都会收集一次存活列表
        /// （还会顺手纠正越界的索引），放进 actor 循环里逐个比就是白跑 N 遍。</para>
        ///
        /// <para>目标变化时留一条日志：这是"名牌为什么不见了"的唯一现场证据，且只在换目标时打一次
        /// （不是每帧），不会刷屏。</para>
        /// </summary>
        private long ResolveSpectateTargetId()
        {
            CsActor target = null;
            if (_match != null && _match.IsSpectating) target = _match.SpectateTarget;

            var id = target != null ? target.Id : 0L;
            if (id != _lastSpectateTargetId)
            {
                if (id != 0L)
                {
                    // 用 Always（不降频）：本方法本就在"目标真的换了"时才走到这里（一局十几次），
                    // 是"换观战目标时名牌为什么不见了"的唯一证据；用降频版会把它吞掉（实测第 2 次起就没了）。
                    _log.Always(
                        $"观战目标是 {target.Name}(id={id})：已隐藏他的头顶名牌" +
                        $"（那台相机正对着他，世界空间血条+名字会顶在镜头前糊住画面）");
                }
                _lastSpectateTargetId = id;
            }
            return id;
        }

        // ==================================================================
        //  视图创建 / 销毁
        // ==================================================================
        private ActorView EnsureView(CsActor actor, long localId)
        {
            _localId = localId;
            if (_views.TryGetValue(actor.Id, out var existing) && existing != null) return existing;

            var path = PrefabPathFor(actor);
            if (!_prefabs.TryGetValue(path, out var prefab) || prefab == null)
            {
                // 首次走到这里时可能还在加载：只报一次（同一路径），别刷屏。
                LoadPrefab(path, null);
                if (!_missing.Contains(path))
                {
                    _log.Info("prefab.pending." + path,
                        $"角色预制体 {path} 还在异步加载 —— 该角色本帧没有视图（下一帧会自动补上）");
                }
                return null;
            }

            // 生命周期终点 = Game.Pool.Despawn（见 RemoveView / ClearViews）——不许直接 Destroy，
            // 否则池里留下已销毁引用，下一次 Spawn 会把它发给业务。
            // 存在性仍由 _prefabs 缓存把关（上面刚判过）：池只负责"造 / 复用"，不负责"要不要造"。
            var pool = Game.Pool;
            if (pool == null)
            {
                _log.Error("pool.null", "Game.Pool 为 null（Game.Launch 未执行？），角色视图无法生成");
                return null;
            }
            var go = pool.Spawn(path, _root, PoolGroupActorViews);
            if (go == null)
            {
                // 池自己已记 Error（预制体找不到 / 工厂返回 null）：这里补一条业务语义的留痕
                _log.Warn("view.spawnfail", $"角色视图生成失败（对象池返回 null）：{path}");
                return null;
            }
            go.name = $"{CsViewTuning.PlayerModelName}_{actor.Id}";
            var view = go.GetComponent<ActorView>();
            if (view == null)
            {
                _log.Error("prefab.nocomponent",
                    $"角色预制体 {path} 上没有 ActorView 组件 —— 视图无法工作，实例已还给对象池。" +
                    "请重跑 ArtSetup 生成 player.prefab");
                pool.Despawn(go);
                return null;
            }

            // 复用的实例带着上一世的状态（名牌子节点 / 被关掉的渲染与碰撞体 / 尸体冻结 / 只报一次的闸）：
            // 必须先清理再 Bind —— 否则"上一世是本地玩家或尸体"的实例会让新角色隐身 / 打不中。
            view.PrepareForReuse();

            // 先 Bind（量测身高时把名牌排除在外），再挂名牌。
            view.Bind(actor.Id, actor.Team, actor.IsBot, actor.Id == localId);
            view.SnapTo(actor);
            var plate = AttachNameplate(go.transform, actor.Id);
            view.SetPlate(plate);
            _views[actor.Id] = view;

            _log.Info("view.create",
                $"新建角色视图 {actor.Name}(id={actor.Id}, {actor.Team}, bot={actor.IsBot}) " +
                $"模型={path} 命中盒={view.Proxies.Length} 名牌={(plate != null ? "有" : "无")}");
            return view;
        }

        private Nameplate AttachNameplate(Transform parent, long actorId)
        {
            // 存在性把关：预制体还没加载好（异步在途）/ 不存在时**不建**，也不重复发起加载（见 LoadPrefab）。
            if (!_prefabs.TryGetValue(CsViewTuning.NameplatePath, out var prefab) || prefab == null) return null;

            // 回收在 ActorView.PrepareForReuse（复用时）与 Despawn 链上。
            var pool = Game.Pool;
            if (pool == null)
            {
                _log.Error("pool.null", "Game.Pool 为 null（Game.Launch 未执行？），头顶名牌无法生成");
                return null;
            }
            var go = pool.Spawn(CsViewTuning.NameplatePath, parent, PoolGroupNameplates);
            if (go == null)
            {
                _log.Warn("plate.spawnfail",
                    $"头顶名牌生成失败（对象池返回 null）：{CsViewTuning.NameplatePath}（池已记 Error）");
                return null;
            }
            go.name = CsViewTuning.PlateNodeName;
            go.transform.localPosition = new Vector3(0f, CsViewTuning.NameplateHeight, 0f);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;   // 父节点不缩放（Body 才缩放）→ 名牌尺寸恒定
            return go.GetComponent<Nameplate>();
        }

        private void RemoveView(long actorId)
        {
            _views.TryGetValue(actorId, out var view);
            _views.Remove(actorId);
            _skinOf.Remove(actorId);
            // 用 _views 里的引用而不再按名字 _root.Find：池回收后对象被移出 _root，按名字找不到会漏回收。
            if (view != null) Game.Pool.Despawn(view.gameObject);
        }

        private void ClearViews()
        {
            foreach (var kv in _views)
            {
                if (kv.Value != null) Game.Pool.Despawn(kv.Value.gameObject);
            }
            _views.Clear();
            _skinOf.Clear();
            _missingClassSkin.Clear();
            if (_root == null) return;
            if (_viewModel != null)
            {
                _viewModel.Teardown();
                Object.Destroy(_viewModel);
                _viewModel = null;
            }
        }

        // ==================================================================
        //  预制体路径
        // ==================================================================
        /// <summary>
        /// 阵营 + 皮肤 → 模型预制体路径（<c>Art/{T|CT}/{skin}</c>）。
        ///
        /// <para><b>本地玩家</b>取选兵种页选的那个兵种（<see cref="CsPlayerClass"/> → <see cref="CsClassSkins"/>，
        /// 原版 <c>joinclass N</c> 的语义：换皮肤）；其余角色（bot / LAN 远端）以及
        /// "没选 / 选了自动 / 选的兵种本工程没有模型数据"仍按 actorId 稳定地挑一个
        /// （CS 1.6 里每个玩家在队内随机一个皮肤，同一局内保持不变）。</para>
        /// </summary>
        private string PrefabPathFor(CsActor actor)
        {
            var team = actor.Team == CsTeam.CT ? CsViewTuning.TeamFolderCT : CsViewTuning.TeamFolderT;
            var skins = actor.Team == CsTeam.CT ? CsViewTuning.SkinsCT : CsViewTuning.SkinsT;
            if (skins == null || skins.Length == 0) return CsViewTuning.ArtRoot + team + "/" + CsViewTuning.PlayerModelName;

            var picked = false;
            var skin = (string)null;
            if (actor.Id == _localId && CsPlayerClass.TryGet(actor.Team, out var joinClass))
            {
                skin = CsClassSkins.SkinKey(actor.Team, joinClass);
                picked = !string.IsNullOrEmpty(skin);
                if (picked)
                {
                    _log.Info("class.skin",
                        $"本地玩家皮肤：兵种 {CsClassSkins.ClassName(actor.Team, joinClass)}（原版 joinclass {joinClass}）" +
                        $"→ 皮肤键 {skin} → 预制体 {CsViewTuning.ArtRoot + team + "/" + skin}");
                }
                else if (_missingClassSkin.Add(joinClass))
                {
                    _log.Warn("class.skin.missing",
                        $"兵种 {CsClassSkins.ClassName(actor.Team, joinClass)}（原版 joinclass {joinClass}）在本工程没有模型数据" +
                        "（Assets/Editor/Views/ModelData 下无对应 player 模型）⇒ 回落到队内默认皮肤");
                }
            }

            if (!picked && (!_skinOf.TryGetValue(actor.Id, out skin) || string.IsNullOrEmpty(skin)))
            {
                // 按 actorId 稳定地挑一个皮肤（CS 1.6 里每个玩家在队内随机一个皮肤；这里同一局内保持不变）
                var h = unchecked((ulong)actor.Id * 2654435761UL + 12345UL);
                var idx = (int)(h % (ulong)skins.Length);
                skin = skins[idx];
                _skinOf[actor.Id] = skin;
            }
            return CsViewTuning.ArtRoot + team + "/" + skin;
        }

        /// <summary>
        /// 先查缓存，再发起异步加载。**同一路径只发起一次**：加载中的记 <c>_pending</c>，
        /// 确认不存在的记 <c>_missing</c> 并**不再重试** —— 否则"缺失的预制体"会在每帧
        /// 重新发起一次异步加载（每帧一次 IO + 一次回调，是实打实的资源抖动）。
        /// </summary>
        private void LoadPrefab(string path, System.Action<GameObject> onLoaded)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_prefabs.TryGetValue(path, out var cached) && cached != null)
            {
                onLoaded?.Invoke(cached);
                return;
            }
            if (_missing.Contains(path)) return;      // 已确认缺失：不重试
            if (!_pending.Add(path)) return;          // 已在加载中

            var res = Game.Res;
            if (res == null)
            {
                _log.Error("res.null", "Game.Res 为 null（CloverRes.Init 未执行？），角色/武器视图无法加载任何预制体");
                _pending.Remove(path);
                _missing.Add(path);
                return;
            }

            res.LoadAsset<GameObject>(path, go =>
            {
                _pending.Remove(path);
                if (go == null)
                {
                    _missing.Add(path);
                    _log.Warn("prefab.missing." + path,
                        $"预制体缺失：Resources/{path}.prefab —— 该资源不会显示（不会重复重试）。" +
                        "先执行菜单 Clover/CS16/整理视图与音效资源 生成它");
                    return;
                }
                _prefabs[path] = go;
                // 从"缺失"转回"有"（例如资源是刚生成的）：允许后续重新探测
                _missing.Remove(path);
                onLoaded?.Invoke(go);
            });
        }

        // ==================================================================
        //  读条预热
        // ==================================================================
        /// <summary>
        /// 把本局可能用到的角色预制体（两个阵营的全部兵种皮肤 + 名牌）提前发起加载，
        /// 让"首次加载"发生在读条期而不是开局那一帧：全部落定（进缓存 / 确认缺失）后回调一次。
        ///
        /// <para>⛔ **绝不把人钉在读条屏**：模块未就绪、资源后端不可用、或本次一个都没发起加载时，
        /// 立即回调（把"已就绪"交给调用方，代价只是退回原来那种"开局那一帧加载"的行为）。</para>
        ///
        /// <para>路径拼法与 <see cref="PrefabPathFor"/> 同源（<c>Art/{T|CT}/{skin}</c>）。</para>
        /// </summary>
        public void WarmUpMatchPrefabs(System.Action onDone)
        {
            if (onDone != null) _warmupWaiters.Add(onDone);
            _warmupT0 = Time.realtimeSinceStartup;

            if (!_ready || Game.Res == null)
            {
                _log.Warn("warmup.skip",
                    $"读条预热跳过（视图模块未就绪或资源后端不可用：ready={_ready}）；" +
                    "角色预制体仍会在开局那一帧首次加载");
                if (_warmupPending == 0) FlushWarmupWaiters();
                return;
            }

            CollectMatchPrefabPaths(_warmupPaths);
            var started = 0;
            for (var i = 0; i < _warmupPaths.Count; i++)
            {
                var p = _warmupPaths[i];
                if (_prefabs.ContainsKey(p) || _missing.Contains(p) || _pending.Contains(p)) continue;
                _warmupPending++;
                started++;
                LoadPrefab(p, _ => OnWarmupOneDone());
            }

            _log.Always($"读条预热：本局角色预制体 {_warmupPaths.Count} 个，本次发起加载 {started} 个" +
                        $"（已在缓存 / 已在途 {_warmupPaths.Count - started} 个）");
            if (_warmupPending == 0) FlushWarmupWaiters();
        }

        private void OnWarmupOneDone()
        {
            if (_warmupPending > 0) _warmupPending--;
            if (_warmupPending > 0) return;
            _log.Always("读条预热完成：本局角色预制体已全部落定（进缓存或确认缺失）");

            // 预制体"进缓存"并不等于"能用得快"：每个**不同**预制体的**首次 Instantiate** 要 1.1~1.4 s
            // （同一预制体第 2 次起 1 ms）；本局 9 具角色涉及约 8 个不同预制体 ⇒ 约 9.6 s，
            // 正是"选完人进游戏"那一帧的长帧来源。实例化本身就是代价所在（不需要渲染、不需要相机），
            // 所以在读条期把它们各实例化一具并保留，开局时引擎再实例化就落在 1 ms 量级。
            BuildWarmQueue();
        }

        /// <summary>把"已进缓存、本次还没热过"的预制体排进逐帧预热队列。</summary>
        private void BuildWarmQueue()
        {
            _warmQueue.Clear();
            _warmIndex = 0;
            for (var i = 0; i < _warmupPaths.Count; i++)
            {
                var p = _warmupPaths[i];
                if (_warmInstances.Contains(p)) continue;
                if (!_prefabs.TryGetValue(p, out var prefab) || prefab == null) continue;
                _warmQueue.Add(p);
            }

            if (_warmQueue.Count == 0)
            {
                BeginRenderWarm();
                return;
            }

            _warmRoot = new GameObject("CsActorViewsWarmup");
            _warmRoot.transform.SetParent(_root, false);
            _warmRoot.transform.localPosition = new Vector3(0f, -5000f, 0f);
            _warmPhase = 1;
            _log.Always($"读条预热：开始逐帧实例化 {_warmQueue.Count} 个不同预制体（每帧 1 具，读条画面不被长帧卡住）");
        }

        /// <summary>
        /// 预热泵（每帧一次，由 <c>LateUpdate</c> 驱动）：按阶段分发，并在超出
        /// <see cref="WarmupDeadlineSeconds"/> 时**就地收尾放行** —— 未预热的部分退回"开局那一帧付代价"，
        /// 绝不让玩家钉在读条屏。
        /// </summary>
        private void WarmTick()
        {
            if (_warmPhase == 0) return;

            if (Time.realtimeSinceStartup - _warmupT0 > WarmupDeadlineSeconds)
            {
                _log.Warn("warmup.deadline",
                    $"读条预热超出 {WarmupDeadlineSeconds:F0}s 预算（第 {_warmPhase} 段未做完：实例化 " +
                    $"{_warmIndex}/{_warmQueue.Count}）⇒ 就此收尾放行，剩余代价落到开局那一帧");
                EndWarm();
                return;
            }

            if (_warmPhase == 1) { WarmInstancesTick(); return; }
            WarmRenderTick();
        }

        /// <summary>
        /// 逐帧实例化预热（每帧只做 1 具）。一口气做完会阻塞约 12 s，读条画面会像卡死；
        /// 全部做完后才继续下一段。宿主节点远在地图外，只付实例化代价、不进任何相机。
        /// </summary>
        private void WarmInstancesTick()
        {
            if (_warmIndex >= _warmQueue.Count)
            {
                _log.Always($"读条预热完成：{_warmInstances.Count} 个预制体已各实例化一具并保留" +
                            $"（耗时 {(Time.realtimeSinceStartup - _warmT0) * 1000f:F0} ms）");
                BeginRenderWarm();
                return;
            }

            if (_warmIndex == 0) _warmT0 = Time.realtimeSinceStartup;

            var path = _warmQueue[_warmIndex];
            var t0 = Time.realtimeSinceStartup;
            var go = Object.Instantiate(_prefabs[path], _warmRoot.transform);
            go.name = "Warm" + _warmIndex + "_" + path.Replace('/', '_');
            _warmInstances.Add(path);
            _warmIndex++;
            _log.Info("warmup.inst." + path,
                $"读条预热：{path} 首次实例化 {(Time.realtimeSinceStartup - t0) * 1000f:F0} ms" +
                $"（{_warmIndex}/{_warmQueue.Count}）");
        }

        /// <summary>实例化段做完 → 排到渲染段（下一帧才做，让读条画面先拿到一帧）。</summary>
        private void BeginRenderWarm()
        {
            if (CsRenderWarmup.Enabled)
            {
                _warmPhase = 2;
                _log.Always("读条预热：接着做渲染预热（着色器变体 → Canvas 网格 → 离屏绘制一帧）");
                return;
            }

            _log.Always("读条预热：渲染预热已关闭（CsRenderWarmup.Enabled=false），直接结束读条");
            EndWarm();
        }

        /// <summary>
        /// 渲染预热段：着色器变体 → Canvas 网格 → 离屏绘制一帧（见 <see cref="CsRenderWarmup"/>）。
        /// 三段各自到点即跳，做完就放行"读条可以结束"。
        /// </summary>
        private void WarmRenderTick()
        {
            _warmPhase = 0;

            CsRenderWarmup.WarmInUseVariants(_warmupT0);
            CsRenderWarmup.WarmCanvases(_warmupT0);

            if (!CsRenderWarmup.OverBudget(_warmupT0))
            {
                var anchor = _warmRoot != null ? _warmRoot.transform : null;
                CsRenderWarmup.WarmOffscreen("本局角色实例", anchor, _warmupT0);
            }

            _log.Always($"读条预热完成（含渲染预热，共 {(Time.realtimeSinceStartup - _warmupT0) * 1000f:F0} ms）");
            FlushWarmupWaiters();
        }

        /// <summary>收尾：关泵、放行等待者（不丢回调 —— 丢了就是永远进不去）。</summary>
        private void EndWarm()
        {
            _warmPhase = 0;
            FlushWarmupWaiters();
        }

        private void FlushWarmupWaiters()
        {
            for (var i = 0; i < _warmupWaiters.Count; i++) _warmupWaiters[i]?.Invoke();
            _warmupWaiters.Clear();
        }

        /// <summary>本局可能出现的角色预制体路径（去重）。</summary>
        private static void CollectMatchPrefabPaths(List<string> into)
        {
            into.Clear();
            AddTeamPaths(into, CsViewTuning.TeamFolderCT, CsViewTuning.SkinsCT);
            AddTeamPaths(into, CsViewTuning.TeamFolderT, CsViewTuning.SkinsT);
            AddPath(into, CsViewTuning.NameplatePath);
        }

        private static void AddTeamPaths(List<string> into, string teamFolder, string[] skins)
        {
            if (skins == null || skins.Length == 0)
            {
                AddPath(into, CsViewTuning.ArtRoot + teamFolder + "/" + CsViewTuning.PlayerModelName);
                return;
            }

            for (var i = 0; i < skins.Length; i++)
            {
                if (!string.IsNullOrEmpty(skins[i])) AddPath(into, CsViewTuning.ArtRoot + teamFolder + "/" + skins[i]);
            }
        }

        private static void AddPath(List<string> into, string path)
        {
            if (!string.IsNullOrEmpty(path) && !into.Contains(path)) into.Add(path);
        }

        // ==================================================================
        //  第一人称武器视图
        // ==================================================================
        /// <summary>
        ///
        /// <para>为什么运行期绑定而不是 Bootstrap 里挂：那台相机是视图层在自己 Start 里创建/启停的，
        /// 装配期还不存在；而且主菜单相机也带 <c>MainCamera</c> 标签，必须"比赛运行中"才绑，
        /// 否则武器会挂到菜单相机上。这里只在 <c>IsRunning</c> 时绑，停局即解绑。</para>
        /// </summary>
        private void SyncViewModel()
        {
            var cam = Camera.main;
            if (cam == null) return;

            if (_viewModel != null && _viewModel.Host != cam)
            {
                _viewModel.Teardown();
                Object.Destroy(_viewModel);
                _viewModel = null;
            }

            if (_viewModel == null)
            {
                _viewModel = cam.gameObject.AddComponent<ViewModelRig>();
                if (_viewModel == null)
                {
                    _log.Error("viewmodel.addfail", "在相机上挂 ViewModelRig 失败，第一人称武器不会显示");
                    return;
                }
                _viewModel.Init(_match, this);
                _log.Always($"第一人称武器视图已挂到相机「{cam.name}」");
            }

            _viewModel.Tick();
        }

        /// <summary>供 <see cref="ViewModelRig"/> 复用同一份预制体缓存与加载口径。</summary>
        internal void RequestViewModelPrefab(string path, System.Action<GameObject> onLoaded) => LoadPrefab(path, onLoaded);

        internal bool TryGetViewModelPrefab(string path, out GameObject go)
        {
            go = null;
            return _prefabs.TryGetValue(path, out go) && go != null;
        }

        internal CsModuleLog Log => _log;
    }
}
