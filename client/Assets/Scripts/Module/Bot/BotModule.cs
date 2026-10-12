using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using Unity.Profiling;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>某个难度档的累计统计（验收表 B1~B3 的证据载体）。</summary>
    public struct BotTierStat
    {
        /// <summary>射击次数（由 <c>NextFireTime</c> 推进量估算 —— 见 <see cref="BotWorldWatcher"/>）。</summary>
        public int Shots;
        /// <summary>命中次数（由 <c>ICsMatch.OnDamaged</c> 归属到本档机器人 —— 见 <see cref="BotHitAttribution"/>）。</summary>
        public int Hits;
        public int Kills;
        public int Damage;
        /// <summary>进入交战的次数。</summary>
        public int Engages;

        public float Accuracy => Shots > 0 ? (float)Hits / Shots : 0f;
    }

    /// <summary>
    /// 机器人 AI 宿主（<c>Cs16.Module.Bot</c>）：把 <see cref="CsBotBrain"/> 挂进 Unity 帧循环。
    ///
    /// <para><b>装配</b>：与 <see cref="MatchModule"/> / <c>CsMapModule</c> **挂在同一个 GameObject 上**
    /// （Stage 场景的常驻物体）。它自己不创建 actor —— bot actor 由 <c>ICsMatch.AddBot</c>（New Game / H 菜单）
    /// 创建，本模块只负责"发现新 bot actor → 建 brain；actor 消失 → 销毁 brain"。</para>
    ///
    /// <para><b>频率</b>：决策按 <see cref="CsConst.BotTickInterval"/>（10 Hz）跑，但**每只 bot 有自己的相位**
    /// （见 <see cref="ThinkDueBrains"/>），不把全部 bot 挤进同一帧；感知快照每帧刷新；
    /// brain 增删 / 统计 / 路线审计按模块 tick（10 Hz）跑。注意"意图会持续生效"这件事 ——
    /// 模拟每帧都拿最后一次提交的意图执行，所以每个活着的 bot 每个周期都必须重新提交一次，
    /// 否则会残留上一周期的 <c>Fire=true</c>（表现为"死了还在开枪"）。</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BotModule : MonoBehaviour
    {
        public const string Tag = "Bot";

        // Game.Logger.Info 完全重复的 Debug.Log（允许的差异 #8 ①）。冗余行与只服务于它的

        private readonly Dictionary<long, CsBotBrain> _brains = new Dictionary<long, CsBotBrain>(16);
        private readonly List<long> _removeScratch = new List<long>(16);
        private readonly HashSet<long> _seenScratch = new HashSet<long>();

        /// <summary>
        /// 每只 bot 的**决策相位**（<see cref="ThinkDueBrains"/> 用）：<c>NextAt</c> = 下一次该决策的
        /// 游戏时间，<c>LastAt</c> = 上一次决策时间（作为 <see cref="CsBotBrain.Think"/> 的 <c>dt</c>）。
        /// </summary>
        private struct ThinkClock
        {
            public float NextAt;
            public float LastAt;
        }

        private readonly Dictionary<long, ThinkClock> _thinkClock = new Dictionary<long, ThinkClock>(16);

        /// <summary>
        /// 错峰槽位数（**纯调度量，不是玩法数值**）：8 只 bot 各占一个槽，相位差 =
        /// <see cref="CsConst.BotTickInterval"/> / 8 ⇒ 最大相位差 87.5 ms &lt; 一个决策周期 100 ms。
        /// 每只 bot 的决策周期仍是 100 ms（10 Hz），只是不落在同一帧。
        /// </summary>
        private const int ThinkStaggerSlots = 8;

        /// <summary>
        /// **测试入口，供离线取证驱动使用**：<c>true</c> = 关掉决策错峰（全部 bot 落在模块 tick 那一帧，
        /// 即错峰之前的口径），用来在同一会话里做开/关 A/B。不改变任何玩法数值。
        /// </summary>
        public static bool StaggerDisabledForTest { get; set; }

        /// <summary>
        /// 分段耗时打点（Unity 标准 `ProfilerMarker`；profiler 不录时开销≈0，不参与任何逻辑）。
        /// 配合 `ProfilerRecorder` 按名字取样即可拿到"每个 slot 每次调用"的耗时，量法见
        /// <c>.ai-tmp/test/report-perf.md</c> §20。
        /// </summary>
        private static readonly ProfilerMarker MarkTickBots = new ProfilerMarker("BotModule.TickBots");

        /// <summary>单只 bot 的一次 `Think`（含意图提交前的决策体）。</summary>
        private static readonly ProfilerMarker MarkBrainThink = new ProfilerMarker("BotModule.BrainThink");

        private readonly BotStats _stats = new BotStats();
        private readonly BotWorldWatcher _watcher = new BotWorldWatcher();

        private MatchModule _matchModule;
        private ICsMatch _match;
        private ICsMatch _subscribed;
        private ICsMap _map;

        private float _accumulator;
        private float _statsTimer;
        private float _nextResolveWarnAt;
        private float _nextMapWarnAt;
        private int _ambiguousAttribution;
        private int _prevRoundNumber;

        private float _nextRouteAuditAt;
        private bool _routeAuditClear = true;
        private int _routeAuditCount;

        private int _routeAuditOffPlan;

        /// <summary>
        /// 上面两条 Warn 的**日志**降频闸（计数照旧逐对累计，只有日志限速）。
        /// 审计每 <see cref="CsBotConst.RouteAuditIntervalSeconds"/>(1s) 跑一次且逐对比较 ⇒
        /// 真撞车时同一秒最多 6 条；而 <c>CsBotBrain.TryRouteObjective</c> 的"最后一档兜底"
        /// 会**有意**点开被队友占着的家族，撞车对随之为真 ⇒ 不打闸就是刷屏。
        /// </summary>
        private float _nextRouteAuditWarnAt;

        /// <summary>本趟审计里第一对"同队路线撞车"的样本描述（供循环后唯一那条 Warn 引用"是谁"）；无则 null。</summary>
        private string _routeCollisionSample;

        /// <summary>本趟审计里第一对"同队非分工路线共用"的样本描述；无则 null。</summary>
        private string _routeOffPlanSample;

        /// <summary>最近一次审计里"同队两人走同一条路"的对数（0 = 互斥成立）。给探针只读用。</summary>
        public int RouteCollisionPairs => _routeAuditCount;

        /// <summary>
        /// 最近一次审计里**被排除**（非分工家族：包已下的守 C4 / 兜底回出生点）的同队同路对数。
        /// <para>给探针只读用：判据排除了一整类状态，就必须能把它的数量单独报出来（不然"排除"就成了藏分）。</para>
        /// </summary>
        public int RouteOffPlanPairs => _routeAuditOffPlan;

        /// <summary>最近一次审计是否**没有**发现同队撞车。</summary>
        public bool RouteDistinctOk => _routeAuditClear;

        /// <summary>是否已拿到比赛门面（拿不到时本组件会自己禁用并打 Error）。</summary>
        public bool IsReady => _match != null;

        /// <summary>当前门面（只读；给自检探针用）。</summary>
        public ICsMatch Match => _match;

        /// <summary>当前 brain 数量。</summary>
        public int BrainCount => _brains.Count;

        /// <summary>当前使用的地图门面（可能为 null —— 地图模块还没就绪）。</summary>
        public ICsMap Map => _map;

        // ==================================================================
        //  生命周期
        // ==================================================================
        private void Awake()
        {
            _matchModule = GetComponent<MatchModule>();
            if (_matchModule == null)
            {
                Game.Logger.Error(Tag,
                    "BotModule 拿不到同级的 MatchModule（GetComponent<MatchModule>() == null）→ 机器人 AI 已禁用。" +
                    "请把 BotModule 与 MatchModule 挂在同一个 GameObject 上（Stage 场景常驻物体）");
                enabled = false;
                return;
            }

            _match = _matchModule.Match;
            _map = ResolveMap();
        }

        private void Start()
        {
            if (!enabled) return;

            if (_match == null) _match = _matchModule.Match;
            if (_match == null)
            {
                Game.Logger.Error(Tag,
                    "BotModule 在 Start 时仍拿不到 ICsMatch（MatchModule.Match == null）→ 机器人 AI 已禁用。" +
                    "检查 MatchModule 是否因为拿不到 ICsMap 而把自己禁用了");
                enabled = false;
                return;
            }

            Subscribe();
            Game.Logger.Info(Tag,
                $"BotModule 就绪：地图={(_map != null ? _map.MapName : "null")} 决策频率={1f / CsConst.BotTickInterval:F0}Hz " +
                $"FOV={CsConst.BotFovDegrees:F0}° 听半径={CsConst.BotHearRadius:F0}m");
        }

        private void OnDestroy()
        {
            Unsubscribe();
            _brains.Clear();
        }

        private void Update()
        {
            if (_match == null && !TryResolveMatch()) return;
            if (_map == null) _map = ResolveMap();

            var now = CsClock.Now;   // gameplay clock: bot decisions + hit attribution share the simulation time source (Core/CsClock.cs), never the wall clock

            if (!_match.IsRunning)
            {
                if (_brains.Count > 0) ClearBrains("比赛未在运行（未开局 / 已停止）");
                _watcher.Reset();
                _accumulator = 0f;
                _prevRoundNumber = 0;
                return;
            }

            if (_match.IsPaused)
            {
                // 暂停：不决策，但要告诉 watcher "时间被整体后移了"，避免恢复后统计虚高
                _watcher.Tick(_match.Actors, 0f, now, true, null);
                return;
            }

            // 重开比赛（比分/回合复位）→ 上一局的 brain 里还留着旧路线，必须重建；
            // 旧感知基准（_lastNextFire 等）也要一起清掉，否则会被误判成"一口气打了一串"。
            if (_match.RoundNumber < _prevRoundNumber)
            {
                ClearBrains($"比赛重开（回合号 {_prevRoundNumber} → {_match.RoundNumber}）");
                _watcher.Reset();
            }
            _prevRoundNumber = _match.RoundNumber;

            // 感知快照**每帧**刷新：决策已按各自相位发生（见 ThinkDueBrains），快照若仍只在 10 Hz
            // 模块 tick 刷新，决策用到的那份会比"同帧刷新"旧最多一个槽宽（87.5 ms）⇒「感知→决策」
            // 的上界从 ≤100 ms 变成 ≤187.5 ms，而反应时间正是三档难度可辨的支点。
            // 代价：watcher 单项实测 ≈0.1 ms/tick ⇒ 逐帧后约 +0.1 ms/帧。
            _watcher.Tick(_match.Actors, CsClock.Delta, now, false, _stats);

            _accumulator += CsClock.Delta;   // cadence uses the same dt; not injected this is literally Time.deltaTime (host MatchModule.Update calls CsClock.Drive())
            if (_accumulator >= CsConst.BotTickInterval)
            {
                var dt = _accumulator;
                _accumulator = 0f;
                using (MarkTickBots.Auto()) TickBots(dt, now);   // brain 增删 / 统计 / 审计（10 Hz）
            }

            // 决策按每只 bot 自己的相位跑（见 ThinkDueBrains）：不再把全部 bot 挤进上面那一帧。
            if (!StaggerDisabledForTest) ThinkDueBrains(now);
        }

        // ==================================================================
        //  装配
        // ==================================================================
        private bool TryResolveMatch()
        {
            if (_matchModule == null) _matchModule = GetComponent<MatchModule>();
            if (_matchModule == null)
            {
                if (Time.time >= _nextResolveWarnAt)
                {
                    _nextResolveWarnAt = Time.time + 2f;
                    Game.Logger.Error(Tag, "BotModule 找不到 MatchModule（同级 GameObject）→ 本帧不做任何决策，已禁用自己");
                    enabled = false;
                }
                return false;
            }

            _match = _matchModule.Match;
            if (_match == null)
            {
                if (Time.time >= _nextResolveWarnAt)
                {
                    _nextResolveWarnAt = Time.time + 2f;
                    Game.Logger.Warn(Tag, "BotModule 还在等 MatchModule 创建比赛门面（Match == null）");
                }
                return false;
            }

            Subscribe();
            return true;
        }

        /// <summary>
        /// 取地图门面。三条路都试（与 <see cref="MatchModule"/> 同样的三级策略）：
        /// ① 比赛模拟已绑定的地图（最权威：App 注入的那份也在里面）→ ② 同级/子级任何 ICsMap 组件
        /// → ③ 契约约定的具体类型 <c>CsMapModule.Map</c>。
        /// </summary>
        private ICsMap ResolveMap()
        {
            if (_matchModule != null && _matchModule.Map != null) return _matchModule.Map;

            var behaviours = GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is ICsMap onSelf) return onSelf;
            }

            var children = GetComponentsInChildren<MonoBehaviour>(true);
            for (var i = 0; i < children.Length; i++)
            {
                if (children[i] is ICsMap onChild) return onChild;
            }

            var module = GetComponent<CsMapModule>();
            if (module != null && module.Map != null) return module.Map;

            return null;
        }

        private void Subscribe()
        {
            if (_match == null || _subscribed == _match) return;

            Unsubscribe();
            _match.OnDamaged += OnDamaged;
            _match.OnKill += OnKill;
            _subscribed = _match;
        }

        private void Unsubscribe()
        {
            if (_subscribed == null) return;
            _subscribed.OnDamaged -= OnDamaged;
            _subscribed.OnKill -= OnKill;
            _subscribed = null;
        }

        // ==================================================================
        //  主循环
        // ==================================================================
        private void TickBots(float dt, float now)
        {
            SyncBrains();

            // 关掉错峰时走错峰之前的口径：这一帧把全部活着的 bot 都决策一次（dt = 模块刻度）。
            if (StaggerDisabledForTest)
            {
                foreach (var kv in _brains) ThinkOne(kv.Value, dt, now);
            }

            _statsTimer += dt;
            if (_statsTimer >= CsBotConst.StatsLogInterval)
            {
                _statsTimer = 0f;
                LogStats();
            }

            AuditRouteDistinctness(now);
        }

        /// <summary>
        /// 让**到点**的 bot 决策一次（每帧调一次）。每只 bot 的决策周期仍 = <see cref="CsConst.BotTickInterval"/>；
        /// 相位在 <see cref="SyncBrains"/> 建 brain 时按顺序分配，最大相位差 &lt; 一个周期。
        ///
        /// <para><b>为什么错开</b>：全部 bot 落在同一帧时，那一帧要一次做完所有 bot 的决策（实测每 tick 约 42 ms
        /// ⇒ 表现为"每 0.1 s 固定一顿"）；错开后**同样的总工作量**摊到决策周期内的不同帧，单帧峰值随之下降。</para>
        ///
        /// <para><b>不变量</b>：<c>dt</c> 传的是"这只 bot 距上次决策的间隔"（≈100 ms，与原口径同量级）；
        /// 决策周期与相位差上限都不改变单只 bot 的反应时间上界 —— 错峰之前它本来就被量化到 100 ms 的
        /// tick 边界上。</para>
        /// </summary>
        private void ThinkDueBrains(float now)
        {
            if (_brains.Count == 0) return;

            foreach (var kv in _brains)
            {
                if (!_thinkClock.TryGetValue(kv.Key, out var clock))
                {
                    // 刚建 brain 但还没登记相位（例如刚加入）：本帧就决策一次
                    clock = new ThinkClock { NextAt = now, LastAt = now };
                }

                if (now < clock.NextAt)
                {
                    _thinkClock[kv.Key] = clock;
                    continue;
                }

                var dt = now - clock.LastAt;
                if (dt <= 0f) dt = CsConst.BotTickInterval;
                clock.LastAt = now;
                // 相位推进必须加在**这只 bot 上一次的相位**上，不能用整帧共享的 `now`：
                // `now` 对同一帧里所有 bot 是同一个值 ⇒ 只要出现一次长帧（开局 / 加载 / 任何掉帧），
                // 所有 bot 会在同一帧一起到期，而 `now + 周期` 会把它们**永久**锁在同一相位
                // （实测相位差塌成 0.0000，8 只此后一直同帧决策）。按 `NextAt += 周期` 推进则相位永远保留。
                do { clock.NextAt += CsConst.BotTickInterval; } while (clock.NextAt <= now);
                _thinkClock[kv.Key] = clock;

                ThinkOne(kv.Value, dt, now);
            }
        }

        /// <summary>
        /// 单只 bot 的一次决策 + 意图提交。**唯一输出口**：真正执行（移动 / 射线 / 换枪 / 下包）由模拟负责。
        /// </summary>
        private void ThinkOne(CsBotBrain brain, float dt, float now)
        {
            if (brain == null) return;

            var intent = default(CsBotIntent);
            using (MarkBrainThink.Auto()) intent = brain.Think(dt, now);
            _match.SubmitBotIntent(brain.ActorId, intent);

            if (brain.ConsumeEngage())
            {
                _stats.AddEngage(brain.Difficulty);
                var s = _stats.Get(brain.Difficulty);
                Game.Logger.Info(Tag,
                    $"{brain.Name}（{brain.Difficulty}）进入交战：目标 actor {brain.TargetId}；" +
                    $"该难度累计交战 {s.Engages} 次");
            }
        }

        /// <summary>
        ///
        /// <para>为什么要有它：<see cref="CsBotPlans"/> 用"模 4 循环平移"**构造性**保证同队 4 只 bot 的
        /// 槽位互不相同，但**运行期**仍有两种途径让两只 bot 撞到同一条路：① 回合中途加入的 bot
        /// （换目标前的 <c>_planRoute</c>）。这两种都是**非预期分支** —— 按项目铁律必须留痕，
        /// 所以这里逐 tick 审计一次（8 只 bot ⇒ 28 对比较，可忽略），撞了就降频 Warn 并给出**是哪两只、哪条路**。</para>
        ///
        /// <para>只审计、不"纠正"：纠正会让"谁先 tick 谁赢"进入计划（不可复现）；互斥必须靠计划表的构造性质。</para>
        /// </summary>
        private void AuditRouteDistinctness(float now)
        {
            if (_brains.Count < 2) return;
            if (now < _nextRouteAuditAt) return;
            _nextRouteAuditAt = now + CsBotConst.RouteAuditIntervalSeconds;

            _routeAuditClear = true;
            _routeAuditCount = 0;
            _routeAuditOffPlan = 0;
            _routeCollisionSample = null;
            _routeOffPlanSample = null;

            foreach (var kv in _brains)
            {
                var a = kv.Value;
                if (a.PlanRoute == null) continue;

                foreach (var kv2 in _brains)
                {
                    var b = kv2.Value;
                    if (b.ActorId <= a.ActorId) continue;                  // 每对只比一次
                    if (b.PlanRoute != a.PlanRoute) continue;

                    var sa = _match.Find(a.ActorId);
                    var sb = _match.Find(b.ActorId);
                    if (sa == null || sb == null || sa.Team != sb.Team) continue;   // 跨队同路是正常的

                    //   "行为相同"；反过来，死人让出的家族/目标序号应当可以被卡住的活人接手
                    //   （口径与 CsBotPlans.AliveSlotTable 一致，两处必须同步改）。
                    if (!sa.IsAlive || !sb.IsAlive) continue;

                    if (!IsPlanFamily(a.PlanRoute))
                    {
                        // 「包已下 → 全队回 C4 守包」（_match.BombPlanted 后所有 T 都走守C4）与
                        // 「回出生点」（换目标全部失败时的兜底）是**集体**目标 —— 全队去同一个点是有意的，
                        // 不属于"分工路线"判据。但也不许静默放过：单独计数 + 打日志，让它看得见。
                        _routeAuditOffPlan++;
                        if (_routeOffPlanSample == null)
                        {
                            _routeOffPlanSample =
                                $"（本趟第一对：{sa.Name}({sa.Team}) 与 {sb.Name}({sb.Team}) 都在 " +
                                $"'{a.PlanRoute}'（{CsBotPlans.RouteLabel(a.PlanRoute)}））";
                        }
                        continue;
                    }

                    _routeAuditClear = false;
                    _routeAuditCount++;
                    if (_routeCollisionSample == null)
                    {
                        _routeCollisionSample =
                            $"（本趟第一对：{sa.Name}({sa.Team}/槽位{a.PlanSlot}) 与 " +
                            $"{sb.Name}({sb.Team}/槽位{b.PlanSlot}) 都走 '{a.PlanRoute}'" +
                            $"（{CsBotPlans.RouteLabel(a.PlanRoute)}）→ 目标 {a.GoalPosition} vs {b.GoalPosition}）";
                    }
                }
            }

            // 两条 Warn 都**移出双重循环、在这里只发一条**（优先"撞车"）：
            // ① 原先在双重循环里逐对打 ⇒ 一秒最多 6 条；
            // ② 更隐蔽的问题：两条分支共用一个降频闸时，若"非分工路线共用"那一对在字典迭代顺序上排在
            //    前面，它每 10s 会**稳定抢走闸** ⇒ 真正的"同队路线撞车"长期一条都打不出来（被掩盖）。
            // 计数（_routeAuditCount / _routeAuditOffPlan / _routeAuditClear）照旧逐对累计，探针口径不变。
            if (now >= _nextRouteAuditWarnAt && (_routeAuditCount > 0 || _routeAuditOffPlan > 0))
            {
                _nextRouteAuditWarnAt = now + CsBotConst.RouteCollisionWarnIntervalSeconds;
                // **两类合成一条**：两条各发一条时，只要存在撞车，"非分工路线共用"就永远打不出来
                // （撞车优先 + 共用同一个 10s 闸）⇒ 那是"静默放过"，与本节"不许静默"的初衷冲突。
                // 合成一条后：既不刷屏（全局 10s 一条），两类计数又都必然可见。
                var collision = _routeAuditCount > 0
                    ? $"【撞车 {_routeAuditCount} 对】{_routeCollisionSample} "
                    : string.Empty;
                var offPlan = _routeAuditOffPlan > 0
                    ? $"【非分工共用 {_routeAuditOffPlan} 对】{_routeOffPlanSample}"
                    : string.Empty;
                Game.Logger.Warn(Tag,
                    "同队路线审计：" + collision + offPlan + "。" +
                    "分工家族的槽位是模 4 平移、构造性互异（CsBotPlans），**撞车预期不会出现** —— 出现即说明存在" +
                    "「回合中途加入」「上一回合遗留目标」，或「走的是最后一档兜底（无空家族且本槽位换不出目标点" +
                    "时有意点开被队友占着的家族）」；" +
                    "**非分工共用**（包已下的集合守包 / 换目标兜底回出生点）属于有意行为，" +
                    "但若出现在回合开始的进攻/防守阶段，才是异常。" +
                    "（见 BotModule.AuditRouteDistinctness 与 CsBotBrain.TryRouteObjective 的注释）");
            }
        }

        /// <summary>
        /// 这个路线标记是不是**计划表里的分工家族**（4 槽位表给出的路线 ∪ 巡逻线）。
        /// <para>用途：互斥判据只对"分工路线"成立；守 C4 / 回出生点这类集体兜底目标要排除，
        /// 但会用 <see cref="RouteOffPlanPairs"/> 单独计数并打日志（不静默放过）。</para>
        /// <para>表不在这里重写一份：直接问 <see cref="CsBotPlans.For"/>（回合取 0/1 两种奇偶，
        /// 因为 T 的主攻包点按回合奇偶轮换）。</para>
        /// </summary>
        private static bool IsPlanFamily(string routeMarker)
        {
            if (string.IsNullOrEmpty(routeMarker)) return false;

            for (var round = 0; round <= 1; round++)
            {
                for (var slot = 0; slot < CsBotPlans.Slots; slot++)
                {
                    if (CsBotPlans.For(CsTeam.T, slot, round).RouteMarker == routeMarker) return true;
                    if (CsBotPlans.For(CsTeam.CT, slot, round).RouteMarker == routeMarker) return true;
                }
            }

            return false;
        }

        private void SyncBrains()
        {
            var actors = _match.Actors;

            if (_map == null && actors.Count > 0)
            {
                // 没有地图：模拟内部会拒绝一切移动（StepActorPhysics 直接挂起），brain 建了也只会空转。
                // 不当成致命错误 —— 地图是异步加载的，等它到；但要留痕（降频）。
                if (Time.time >= _nextMapWarnAt)
                {
                    _nextMapWarnAt = Time.time + 3f;
                    Game.Logger.Warn(Tag, "地图（ICsMap）尚未就绪 → 暂不为机器人建 brain（等地图加载完再建）");
                }
                return;
            }

            _seenScratch.Clear();

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsBot) continue;

                _seenScratch.Add(a.Id);
                if (_brains.ContainsKey(a.Id)) continue;

                var brain = new CsBotBrain(_match, _map, _watcher.Sense, a.Id, a.Name, a.Difficulty);
                _brains[a.Id] = brain;

                // 决策相位：按建 brain 的顺序取模槽位数（确定性 —— 跟随 actors 的遍历顺序，不用随机）。
                // 先延迟 slot 个槽再开始，之后每 BotTickInterval 决策一次。
                var createdAt = CsClock.Now;
                var slot = _thinkClock.Count % ThinkStaggerSlots;
                _thinkClock[a.Id] = new ThinkClock
                {
                    LastAt = createdAt,
                    NextAt = createdAt + slot * (CsConst.BotTickInterval / ThinkStaggerSlots),
                };

                // 验收证据：每个 bot 建 brain 时打印一次名字 / 阵营 / 难度 / 反应时间 / 瞄准误差
                Game.Logger.Info(Tag, $"新建 brain：{a.Name}（{a.Team}）难度={a.Difficulty} {brain.ProfileText()}");
            }

            _removeScratch.Clear();
            foreach (var kv in _brains)
            {
                if (!_seenScratch.Contains(kv.Key)) _removeScratch.Add(kv.Key);
            }

            for (var i = 0; i < _removeScratch.Count; i++)
            {
                var id = _removeScratch[i];
                var name = _brains[id].Name;
                _brains.Remove(id);
                _thinkClock.Remove(id);
                Game.Logger.Info(Tag, $"销毁 brain：{name}（actor {id} 已不在场 —— 被踢出 / 换局 / 比赛停止）");
            }
        }

        private void ClearBrains(string reason)
        {
            if (_brains.Count == 0) return;

            var n = _brains.Count;
            _brains.Clear();
            _thinkClock.Clear();
            Game.Logger.Info(Tag, $"清空全部 brain（{n} 个）：{reason}");
        }

        // ==================================================================
        //  统计（验收表 B1~B3）
        // ==================================================================
        /// <summary>把"某人受到伤害"归属给刚开火的机器人 —— 契约的回调里没有攻击者，见 <see cref="BotHitAttribution"/>。</summary>
        private void OnDamaged(CsActor victim, int damage, bool headshot, bool lethal)
        {
            if (victim == null) return;

            var brain = BotHitAttribution.Pick(_brains.Values, victim.Id, CsClock.Now, out var candidates);   // reproducibility gap: LastFireIntentTime is written from the simulation now, so the tie-break must read the same Now
            if (brain == null) return;

            if (candidates > 1)
            {
                _ambiguousAttribution++;
                if (_ambiguousAttribution == 1 || _ambiguousAttribution % CsBotConst.LogRateEvery == 0)
                {
                    Game.Logger.Info(Tag,
                        $"命中归属有 {candidates} 个候选（{victim.Name} 同时被多个机器人打）→ 归给最近开火的一个" +
                        $"（第 {_ambiguousAttribution} 次）");
                }
            }

            _stats.AddHit(brain.Difficulty, damage);
            var s = _stats.Get(brain.Difficulty);

            // 降频：首次 + 每 10 次（否则高射速武器会把日志刷满）
            if (s.Hits == 1 || s.Hits % 10 == 0)
            {
                Game.Logger.Info(Tag,
                    $"{brain.Name}（{brain.Difficulty}）命中 {victim.Name}：伤害 {damage}{(headshot ? "（爆头）" : string.Empty)}" +
                    $"{(lethal ? "（致死）" : string.Empty)}；该难度累计命中 {s.Hits} 次");
            }
        }

        private void OnKill(CsKillEvent e)
        {
            if (e.KillerId == 0) return;
            if (!_brains.TryGetValue(e.KillerId, out var brain)) return;

            _stats.AddKill(brain.Difficulty);
            var s = _stats.Get(brain.Difficulty);

            Game.Logger.Info(Tag,
                $"机器人击杀：{e.KillerName}（{brain.Difficulty}）[{e.WeaponId}] → {e.VictimName}" +
                $"{(e.Headshot ? "（爆头）" : string.Empty)}；该难度累计击杀 {s.Kills}");
        }

        /// <summary>每 <see cref="CsBotConst.StatsLogInterval"/> 秒一条：验收表 B1~B3 要的就是这个格式。</summary>
        public void LogStats()
        {
            for (var i = 0; i < 3; i++)
            {
                var d = (CsBotDifficulty)i;
                var line = _stats.Line(d);
                Game.Logger.Info(Tag, line);
            }
        }

        /// <summary>取某难度的累计统计（自检探针用）。</summary>
        public BotTierStat GetTierStat(CsBotDifficulty difficulty)
        {
            return _stats.Get(difficulty);
        }

        /// <summary>统计归零（自检探针"三档各跑一轮"用）。</summary>
        public void ResetTierStats()
        {
            _stats.ResetAll();
            _statsTimer = 0f;
            Game.Logger.Info(Tag, "难度统计已归零（自检探针）");
        }

        /// <summary>当前所有 bot 的一行描述（自检报告用）。</summary>
        public List<string> DescribeBrains()
        {
            var list = new List<string>(_brains.Count);
            foreach (var kv in _brains)
            {
                var b = kv.Value;
                list.Add($"{b.Name} 难度={b.Difficulty} 状态={b.State} 目标={b.TargetId} " +
                         $"路线={b.PlanRoute ?? "无"} 剩余路点={b.RemainingWaypoints}");
            }
            return list;
        }
    }
}
