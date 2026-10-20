using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 一个机器人的大脑：<c>Idle → Patrol → Engage → (Plant | Defuse | Camp)</c>。
    ///
    /// <para><b>职责边界</b>：本类**只决策**，产出 <see cref="CsBotIntent"/>；
    /// 所以这里不调 <c>ICsMatch.SwitchWeapon</c>（那只作用于本地玩家），换枪走 <c>intent.SwitchTo</c>。</para>
    ///
    /// <para><b>意图契约（以 <c>CsTypes.cs</c> 注释为准）</b>：</para>
    /// <list type="bullet">
    /// <item><c>Move</c> = **世界空间**方向，已归一化、y=0（<b>不是</b>相对自身朝向）。</item>
    /// <item><c>AimPoint</c> = 世界坐标点；模拟内部把它换算成朝向，**并按难度施加瞄准误差与转视角速度上限**。
    /// 因此这里给的是"理想瞄点"，<b>不再自己叠加一份难度误差</b>（否则与模拟内部的误差相乘、命中率被双重惩罚）。</item>
    /// <item>连发节奏（<c>FireBurstMin/Max</c>、<c>FirePauseMin/Max</c>）由模拟内部执行，这里<b>只表达"此刻想开火"</b>。</item>
    /// </list>
    ///
    /// <para><b>AimPoint 不能是零向量</b>：模拟的 <c>CsMatch.AimBotAt</c> 把 <c>AimPoint == Vector3.zero</c>
    /// 当作"未给瞄准点 → 保持原朝向"，所以每条返回路径都保证非零（见 <see cref="LookAhead"/>）。</para>
    ///
    /// <para><b>本轮目标的生命周期（"路线走完"之后绝不停在原地）</b>：
    /// <see cref="ChoosePlan"/> 定本回合的目标 → 沿路线推进 → 到达后 <see cref="Camp"/> 守点
    /// （时长 = <see cref="ObjectiveHoldSeconds"/>，按难度 <c>RepathInterval</c> 取 4.8s / 3.0s / 2.5s）
    /// → **<see cref="ReplanObjective"/> 换目标**（交替"换一条路线"/"去巡逻"/"回出生点"）
    /// → 再推进……如此循环。另外两条入口：① 导航报"连续卡住"（<see cref="HandleStuck"/>）→ 立刻换目标；
    /// ② <see cref="MaybeRepath"/> 按难度间隔发现"朝目标没有进展"→ 用当前位置重排路线。</para>
    /// </summary>
    public sealed class CsBotBrain
    {
        private const string Tag = BotModule.Tag;

        /// <summary>走到守位上"算到了"的水平距离（米）。出处：**本项目新增**（守位实现参数，A 无 bot AI）；
        /// 取值 = 略小于 <c>CsConst.PlayerRadius</c>×2（0.72m），保证人确实落在守位格上而不是擦着过。</summary>
        private const float CampSpotArriveRadius = 0.6f;

        private readonly ICsMatch _match;
        private readonly ICsMap _map;
        private readonly BotSense _sense;
        private readonly BotNavigator _nav = new BotNavigator();
        private readonly BotBuyLogic _buy = new BotBuyLogic();
        private readonly BotRadio _radio;
        private readonly long _id;
        private readonly Dictionary<string, int> _rateCounters = new Dictionary<string, int>(8);

        // 为什么按 actor id 再派生，而不是共用一路：同一难度下 N 个 bot 都在抽随机，
        // 共用一路时"第 3 个 bot 的瞄头掷"取决于前 2 个 bot 抽了几次 ⇒ 谁先出生 / 谁掉线
        // 都会改掉别人的行为；per-bot 子流后，每个 bot 的序列只由 (本局 seed, 用途, 自己的 id) 决定。
        /// <summary>瞄准手感流：瞄头掷（<see cref="ChooseHeadshot"/>）+ 头部摆动（<see cref="HeadWobbleOffset"/>）。</summary>
        private readonly Rng _rngAim;
        /// <summary>决策流：守点择优 / 换位（<see cref="PickCampSpot"/>）。</summary>
        private readonly Rng _rngDecision;

        // ---- 战术层状态机（引擎 IFsm 语义，每个 bot 各一份；见 CsBotFsm 的类注释）----
        /// <summary>顶层：<see cref="CsBotFsmStates"/>（Idle / Patrol / Engage / Objective）。</summary>
        private readonly CsBotFsm _fsm;
        /// <summary>目标层（Objective 子层，规格 <c>…参考规格.md:128-129</c> 的"找目标"分支）：
        /// <see cref="CsBotObjectiveStates"/>（Approach / Plant / Hold / Defuse）。</summary>
        private readonly CsBotFsm _objectiveFsm;

        private CsBotProfile _profile;
        private CsBotDifficulty _difficulty = (CsBotDifficulty)(-1);
        private string _name = "bot";

        // ---- 本轮计划 ----
        private int _planRound = -1;

        /// <summary>
        /// 每回合在 <see cref="MaybeResolveRound"/> 里重算一次（换边 / 换 id 都能跟上），
        /// 影响三处：守点时长倍率、偏好交战距离倍率、以及"守够了要不要换目标"。
        /// </summary>
        private CsBotRole _role = CsBotRole.Support;

        /// <summary>本机器人在**本方阵营内的序号**（0 起；<see cref="CsBotPlans.TeamLocalOrdinal"/>）。
        /// 不是全局 actor Id —— 全局 Id 会被"真人玩家在哪一队"整体位移。</summary>
        private int _ordinal;

        /// <summary>本回合的计划槽位（<see cref="CsBotPlans.SlotOf"/>；决定路线与目标点，见 <see cref="CsBotPlans"/>）。
        /// 换目标时按 <c>(_planSlot + k) % Slots</c> 轮转 ⇒ 同队的 bot 仍然岔开。</summary>
        private int _planSlot = -1;

        private string _planRoute;
        private Vector3 _goalPos;
        private bool _goalValid;
        private bool _goalIsSite;

        // ---- 状态机 / 感知 ----
        private CsBotState _state = CsBotState.Idle;
        private long _targetId;
        private bool _visibleNow;
        private float _closestVisibleDist = float.MaxValue;
        private float _firstSeenTime;
        private float _lastSeenTime;
        private Vector3 _lastSeenPos;
        private bool _heardFresh;
        private Vector3 _heardPos;
        private float _heardTime;

        /// <summary>每个敌人"上次被看见"的时刻（回合内）。反应计时按**敌人身份**重算，
        /// 靠它区分"真的新敌人"与"两个敌人在视野里交替成为最近目标"（后者不许重新计时）。</summary>
        private readonly Dictionary<long, float> _enemySeenAt = new Dictionary<long, float>(8);

        /// <summary>拆包人选缓存的到期时刻 / 本人是否当选（见 <see cref="IsElectedDefuser"/>）。</summary>
        private float _defuserElectAt;
        private bool _defuserElected;

        // ---- 无线电（可见敌/友计数来自同一趟视线判定，见 UpdatePerception）----
        /// <summary>本 tick 判定为"看得见"的敌人数（原版 <c>GetNearbyEnemyCount</c>）。</summary>
        private int _visibleEnemies;
        /// <summary>本 tick 判定为"看得见"的存活队友数（原版 <c>GetNearbyFriendCount</c>）。</summary>
        private int _visibleFriends;
        /// <summary>本 tick 待发出的无线电行（<c>null</c> = 本 tick 不发言）。</summary>
        private string _pendingRadio;

        // ---- 交战细节 ----
        private float _wobbleRefreshAt;
        private Vector2 _wobble;
        private float _headRollAt;
        private bool _headRoll;
        private float _switchCooldownUntil;
        private float _strafeSign = 1f;
        private float _strafeFlipAt;

        /// <summary>本段"纯后退"的起始时刻（0 = 当前不在纯后退）。见 <see cref="ApplyBackpedalCap"/>。</summary>
        private float _backpedalSince;
        private float _nextBackpedalLogAt;

        /// <summary>瞄准平滑状态：上一帧提交的注视方向（世界空间单位向量）与时刻。见 <see cref="SmoothAim"/>。</summary>
        private Vector3 _aimSmoothDir;
        private float _aimSmoothAt;
        private long _aimSmoothActor = -1;

        /// <summary>持包压点途中遇敌、站定打完这一梭的保持到期时刻（见 <see cref="CsBotConst.EngageShotHoldSeconds"/>）。</summary>
        private float _engageHoldUntil;

        // ---- 投掷物（角色分工；偏好表见 CsBotRoles.PreferredGrenades）----
        /// <summary>本回合已经投出的颗数（上限 <see cref="CsBotConst.GrenadeMaxPerRound"/>）。</summary>
        private int _grenadeThrows;
        /// <summary>下一次允许开始投掷的时刻（两颗之间至少 <see cref="CsBotConst.GrenadeThrowSpacing"/> 秒）。</summary>
        private float _nextGrenadeAt;
        /// <summary>正在切 / 待投的那颗雷（null = 没有进行中的投掷）。</summary>
        private string _grenadePendingId;
        /// <summary>进行中投掷的瞄点（世界坐标；低弧解算的结果，见 <see cref="GrenadeAimPoint"/>）。</summary>
        private Vector3 _grenadeAim;
        /// <summary>进行中投掷的目标水平距离（米，写进日志）。</summary>
        private float _grenadeRange;
        /// <summary>门槛放行理由（"为什么投"，写进日志）。</summary>
        private string _grenadeGateWhy;
        /// <summary>本次投掷的落点（与门槛检查同一坐标，写进日志）。</summary>
        private Vector3 _grenadeLanding;
        /// <summary>落点 / 投掷锥内最近的队友距离（米；没人在半径内时 = float.MaxValue）。</summary>
        private float _grenadeMateDist = float.MaxValue;
        /// <summary>进行中投掷的选择理由（写进日志）。</summary>
        private string _grenadeWhy;
        /// <summary>切到那颗雷的等待上限时刻（超时放弃，见 <see cref="CsBotConst.GrenadeSwitchTimeout"/>）。</summary>
        private float _grenadeSwitchDeadline;
        /// <summary>投完一颗雷后是否还要主动换回主武器（同类雷还有剩余时模拟不会自动换，见 <see cref="TryThrowGrenade"/>）。</summary>
        private bool _grenadeReturnToWeapon;
        /// <summary>"为什么没投掷"诊断已打过的 (回合, 原因) 组合（每回合每类原因最多一条，防刷屏）。</summary>
        private readonly HashSet<string> _grenadeDiagKeys = new HashSet<string>();

        /// <summary>本回合开始时刻（游戏时钟；投掷门槛按"回合已进行多少秒"判）。</summary>
        private float _roundStartAt;

        /// <summary>下包敌人闸门日志的降频时刻。</summary>
        private float _lastPlantGateLogAt;

        // ---- 捡包（C4 掉在地上）----
        /// <summary>弃权原因里"导航口径走不到落点"那一条：此时换人没有意义（谁去都走不到），
        /// 换人策略只对"走不动"这一类生效。</summary>
        private const string ReachFailReason = "导航口径走不到落点（CanReach=false）";

        /// <summary>本 bot 连续担任捡包者的起点时刻（0 = 还没起算；中间去交战后作废重算）。</summary>
        private float _pickupSince;
        /// <summary>这一段捡包过程中到落点的最短距离（米）；缩短一个阈值才刷新 <see cref="_pickupSince"/>。</summary>
        private float _pickupBestDist = float.MaxValue;
        /// <summary>连续"没有更近的候选人，本人继续去捡"的窗口数（见 <see cref="CsBotConst.PickupKeepWindows"/>）。</summary>
        private int _pickupKeepCount;
        /// <summary>上一次执行捡包分支的时刻（用来识别"中间被打断过"）。</summary>
        private float _pickupLastTick;
        /// <summary>下一次问"落点走得到吗"（<see cref="BotNavigator.CanReach"/>）的时刻。</summary>
        private float _nextPickupReachCheckAt;
        /// <summary>弃权名单所属的比赛实例（换局 / 换比赛时清空名单）。</summary>
        private static ICsMatch _pickupBanMatch;
        /// <summary>弃权名单所属的回合（回合号变化时清空）。</summary>
        private static int _pickupBanRound = int.MinValue;
        /// <summary>本回合已弃权的 T（全队共用：所有 brain 的选举读到同一份，才能真的换人）。</summary>
        private static readonly HashSet<long> _pickupBanned = new HashSet<long>();

        // ---- 守点 ----
        private float _campMoveUntil;
        private float _campPickAt;
        private Vector3 _campSpot;

        /// <summary>本包点的守位表（数据源 = 该包点的地图标记点，全部经 <c>ICsMap.CanStand</c> 过滤）。</summary>
        private readonly List<Vector3> _holdSpots = new List<Vector3>(16);
        /// <summary>自己当前占的守位下标（<c>-1</c> = 还没选过）。</summary>
        private int _holdSlot = -1;
        /// <summary>下一次换位的时刻（<c>0</c> = 还没起算，到达守位时才起算）。</summary>
        private float _holdSwapAt;
        /// <summary>本包点的标记名（<see cref="CsMarkers.BombsiteA"/> / <see cref="CsMarkers.BombsiteB"/>；null = 本轮不在包点守卫）。</summary>
        private string _holdSiteMarker;
        /// <summary>本回合累计换位次数（写进日志，是"真的换位了"的证据载体）。</summary>
        private long _holdSwapCount;

        // ---- 路线走完之后的后续行为（换目标 / 重寻路）----
        /// <summary>第几次"重新选目标"（用来交替"换一条路线"与"去巡逻"）。</summary>
        private int _replanCount;
        /// <summary>到达目标后原地警戒到这一刻；<c>0</c> = 刚换了目标、还没到达（到达时才起算）。</summary>
        private float _goalHoldUntil;
        /// <summary>下一次按难度 <see cref="CsBotProfile.RepathInterval"/> 检查"朝目标有没有进展"的时刻。</summary>
        private float _nextRepathAt;
        /// <summary>上一次检查时到目标的水平距离（判断进展用）。</summary>
        private float _lastGoalDist = float.MaxValue;
        /// <summary>连续几轮"朝目标没有进展"（达到 <see cref="CsBotConst.RepathStallStreak"/> 才重排路线）。</summary>
        private int _stallCount;
        /// <summary>"因卡住而换目标"的下一次允许时刻（防目标反复横跳；见 <see cref="HandleStuck"/>）。</summary>
        private float _nextStuckReplanAt;

        // ---- 全局卡住诊断（有移动意图但连续不动；见 TrackStuck）----
        /// <summary>位移采样基准点（窗口起点）。</summary>
        private Vector3 _stuckProbePos;
        /// <summary>窗口起点的时刻；与 <see cref="CsBotConst.StuckDiagSeconds"/> 比较。</summary>
        private float _stuckProbeSince;
        /// <summary>窗口是否有效（false = 下一 tick 重新取基准）。</summary>
        private bool _stuckProbeValid;
        /// <summary>本回合累计判定的卡住次数（探针只读）。</summary>
        private int _stuckDiagCount;
        /// <summary>本次卡住 Warn 的时间闸（<see cref="_stuckDiagCount"/> 不受它影响）。</summary>
        private float _nextStuckDiagLogAt;
        /// <summary>同一目标连续被判卡住的窗口数（换目标时归零）。</summary>
        private int _stuckAtGoalCount;
        /// <summary>上一次判卡时的目标点（用来识别"换目标了"）。</summary>
        private Vector3 _stuckGoalPos;
        /// <summary>上一次判卡的位置（用来识别"同一位置反复卡"）。</summary>
        private Vector3 _stuckLastEventPos;
        /// <summary>同一位置连续判卡的次数（探针只读）。</summary>
        private int _stuckSamePosCount;

        // ---- 卡住恢复 ----
        /// <summary>让行方向（侧移 + 后退的世界空间方向，已归一化）。</summary>
        private Vector3 _yieldDir;
        /// <summary>让行到这一刻为止（<c>CsClock.Now</c>）。</summary>
        private float _yieldUntil;
        /// <summary>被判定"走不到"的目标点 + 记忆截止时刻；选目标时跳过它。</summary>
        private Vector3 _unreachableGoal;
        private float _unreachableGoalUntil;

        /// <summary>
        /// 关掉卡住恢复（只留诊断）—— 测试入口，用于在同一环境里对比"恢复开/关"；
        /// 不改变任何玩法数值。
        /// </summary>
        public static bool RecoveryDisabledForTest { get; set; }

        // ---- 跳跃（问题"AI 好像不会跳"：CsBotIntent.Jump 过去从没有任何分支赋值过）----
        /// <summary>下一次允许发起跳跃的时刻（防贴着矮墙原地连跳；见 <see cref="MaybeJump"/>）。</summary>
        private float _nextJumpAt;

        // ---- 日志节流 ----
        private float _lastStateLogAt;
        private float _lastUseLogAt;

        private bool _engageEvent;

        // ---- 开火门限诊断（"三条里哪一条恒假"的证据载体；全部按时间降频）----
        /// <summary>最近一次对"当前交战目标"的视线判定（诊断日志用，不参与决策）。</summary>
        private BotSightResult _lastSight = BotSightResult.Clear;
        /// <summary>上一条视线判定的"挡它的东西"（角色名 / 几何体名）。</summary>
        private string _lastSightBlocker;
        /// <summary>累计"视线只被角色受体挡住、按 CS 语义放行"的次数。</summary>
        private long _sightActorBlockCount;
        private float _nextSightBlockLogAt;
        /// <summary>累计"这一 tick 决定不开火"的次数（诊断日志首行必打）。</summary>
        private long _fireDeniedCount;
        private float _nextFireDiagAt;
        /// <summary>累计"靠刚丢视野宽限打出的开火意图"次数。</summary>
        private long _graceFireCount;
        private float _nextGraceLogAt;

        public long ActorId => _id;
        public CsBotState State => _state;
        public long TargetId => _targetId;
        public bool IsEngaging => _state == CsBotState.Engage;
        public CsBotDifficulty Difficulty => _difficulty;
        public CsBotProfile Profile => _profile;
        public string Name => _name;
        public string PlanRoute => _planRoute;

        /// <summary>本回合的计划槽位（0..3；探针与日志用。只读）。</summary>
        public int PlanSlot => _planSlot;

        /// <summary>本机器人在本方阵营内的序号（探针用来核对"角色 = <c>(阵营, 序号)</c>"这条纯函数口径）。</summary>
        public int TeamOrdinal => _ordinal;

        /// <summary>当前路线的**第一段路点**（<see cref="BotNavigator.FirstWaypoint"/>）。
        /// 判据"同队任意两只 bot 的首段路点不同"直接读它（探针只读）。</summary>
        public Vector3 FirstWaypoint => _nav.FirstWaypoint;

        /// <summary>当前路线是否至少有一段路点（false ⇒ <see cref="FirstWaypoint"/> 无意义）。</summary>
        public bool HasFirstWaypoint => _nav.HasFirstWaypoint;

        /// <summary>当前路线**整条有序序列**的签名（判据"同队两只 bot 走的是不是同一条路"用；见 BotNavigator.RouteSignature）。</summary>
        public string RouteSignature => _nav.RouteSignature;

        /// <summary>当前路线的路点数。</summary>
        public int WaypointCount => _nav.WaypointCount;

        /// <summary>本轮目标点（探针/日志只读；配合 <see cref="GoalIsSite"/> 一起看）。</summary>
        public Vector3 GoalPosition => _goalPos;

        /// <summary>本轮目标是否包点。</summary>
        public bool GoalIsSite => _goalIsSite;
        public int RemainingWaypoints => _nav.RemainingWaypoints;
        public float LastFireIntentTime { get; private set; }

        /// <summary>本回合累计判定的卡住次数（探针只读；回合开始时归零）。</summary>
        public int StuckEventCount => _stuckDiagCount;

        /// <summary>同一位置连续判卡的次数（探针只读）。</summary>
        public int StuckSamePositionCount => _stuckSamePosCount;

        public CsBotBrain(ICsMatch match, ICsMap map, BotSense sense, long actorId, string name,
            CsBotDifficulty difficulty)
        {
            _match = match;
            _map = map;
            _sense = sense;
            _id = actorId;
            // 这里只按 (用途, 本 bot id) 派生 —— 纯函数，同 seed 同 id ⇒ 同序列，可重放。
            _rngAim = CsRng.Derive(CsRngStream.BotAimFlavor, _id);
            _rngDecision = CsRng.Derive(CsRngStream.BotDecision, _id);
            _difficulty = difficulty;
            _profile = CsBotProfile.For(difficulty);
            if (!string.IsNullOrEmpty(name)) _name = name;
            _nav.SetOwner(_name);
            _nav.BindMap(map);       // 不设路线时也要有地图，否则避障会静默失效（详见 BotNavigator.BindMap）
            _buy.SetOwner(_name);

            _fsm = new CsBotFsm($"{_name}.顶层");
            _objectiveFsm = new CsBotFsm($"{_name}.目标层");
            _radio = new BotRadio(_match.Find(actorId));
            InitFsm();
        }

        /// <summary>
        /// 注册两层状态机（每个 bot 各一份实例）。
        ///
        /// <para><b>状态与转换的出处</b>（缺口一律标注，不许编）：</para>
        /// <list type="bullet">
        /// <item>骨架 = 规格 <c>策划/策划案/CS1.6单机参考规格.md:117-131</c>：「行为树（3 档共用、参数不同）：
        /// 巡逻 → 发现敌人 → 交战 → 目标死亡/丢失 → 回到巡逻 / 找目标（T 去炸弹点 / CT 去守卫点）」。</item>
        /// <item>因此顶层 = Patrol（巡逻）/ Engage（发现敌人→交战）/ Objective（找目标）；
        /// 目标层 = T: Approach→Plant、CT: Approach→Hold|Defuse。</item>
        /// <item>三档难度**共用同一套状态**、只换 <see cref="CsBotProfile"/> 的参数 —— 规格同段
        /// （Easy 0.5~0.8s/±6°、Normal 0.25~0.4s/±3°、Hard 0.1~0.2s/±1.2° 在 <c>CsBotProfile.For</c> 里逐值落地）。</item>
        /// <item>触发器的"行为口径"出处：交战中停/蹲/瞄准/射击/走位 = 规格 §2.4 各行；
        /// 「发现敌人 → 交战」的可见性/反应时间口径 = <c>CsBotConst.TargetMemorySeconds</c> 与本类
        /// <see cref="UpdatePerception"/> 的注释。</item>
        /// </list>
        /// </summary>
        private void InitFsm()
        {
            _fsm.RegisterState(CsBotFsmStates.Idle);
            _fsm.RegisterState(CsBotFsmStates.Patrol);
            _fsm.RegisterState(CsBotFsmStates.Engage);
            _fsm.RegisterState(CsBotFsmStates.Objective);

            _fsm.AddTransition("enemy-visible", CsBotFsmStates.Engage);
            _fsm.AddTransition("enemy-lost", CsBotFsmStates.Patrol);
            _fsm.AddTransition("objective", CsBotFsmStates.Objective);
            _fsm.AddTransition("round-freeze", CsBotFsmStates.Idle);

            _objectiveFsm.RegisterState(CsBotObjectiveStates.Approach);
            _objectiveFsm.RegisterState(CsBotObjectiveStates.Plant);
            _objectiveFsm.RegisterState(CsBotObjectiveStates.Hold);
            _objectiveFsm.RegisterState(CsBotObjectiveStates.Defuse);
        }

        /// <summary>
        /// 顶层"这一 tick 该走哪条分支"的**唯一**决策入口（规格那条决策链）：
        /// 有交战目标且在记忆期内（除非"再不打就没机会拆包了"）→ <c>Engage</c>；
        /// 否则是否该做目标层的事（下包/拆包）→ <c>Objective</c>；其余 → <c>Patrol</c>。
        /// 决策结果**提交给状态机**，<see cref="ThinkLive"/> 再按 <c>_fsm.Current</c> 分发 ——
        /// 分支选择权在状态机，不在 if 链的书写顺序。
        /// </summary>
        private string CommitTacticalDecision(CsActor self, float now)
        {
            if (!self.IsAlive) return ForceTopState(CsBotFsmStates.Idle);

            //
            // 依据（原版行为，不是本项目自创）：CS 1.6 里 T 会去捡掉在地上的 C4（bot 也会）——
            //   `[Match] ZBot 携带的 C4 掉落在 (-36.34, 0.00, -29.20)（T 走过去可拾取）`，
            //   随后**整回合无人捡**（`拾起了` = 0 条）⇒ 回合时间到、CT 取胜、A5 恒 0。
            //   机制：捡包写在 `TryPlantOrPickup` 里，而它只在 `branch != Engage` 时才被走到（见 ThinkLive ④）；
            //   只要有敌人在 `TargetMemorySeconds` 内，T 全去交战 ⇒ 捡包分支从未执行。
            //
            // 边界（三条同时成立才生效；准入量法见 IsElectedBombHunter）：
            //   ① 只在"场上**确实没有任何 T 持 C4** 且 C4 已掉落在地（`BombPosition` 有意义、且未被安放）"时成立；
            //   ② 执行者 = **离 C4 最近的那一个 T**（并列取 Id 小者）—— 不是全队都去捡，其余 T 照常交战；
            //   ③ 一旦有人拾起（`AnyTCarriesBomb()` 为真）或包被安放，本分支立刻不成立 ⇒ 回到正常分支。
            if (IsElectedBombHunter(self)) return ForceTopState(CsBotFsmStates.Objective);

            if (_targetId != 0 && now - _lastSeenTime <= CsBotConst.TargetMemorySeconds &&
                !MustDefuseFirst(self, now) && !MustPlantFirst(self, now))
            {
                // 持包者因为"敌人在近处"被从目标层拉回交战时留痕（不然只有"没下包"这个结果，看不出原因）。
                if (self.Team == CsTeam.T && self.HasBomb && !_match.BombPlanted &&
                    EnemyNearPlant(self, now, out var plantEnemyDist, out var plantEnemyAge) &&
                    now >= _lastPlantGateLogAt)
                {
                    _lastPlantGateLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"[C4] {_name} 放弃下包：{(plantEnemyAge <= 0f ? "看得见敌人" : $"距上次看见敌人 {plantEnemyAge:F1}s")} " +
                        $"水平距离 {plantEnemyDist:F1}m ≤ {CsBotConst.PlantEnemyAbortRange:F0}m → 转交战");
                }

                return ForceTopState(CsBotFsmStates.Engage);
            }

            if (self.UseProgress >= 0f && _closestVisibleDist > CsBotConst.EnemyTooCloseRange)
            {
                // 正在下包/拆包：保持目标层（不许被巡逻分支抢走，否则 E 一松进度就清零）
                return ForceTopState(CsBotFsmStates.Objective);
            }

            if (self.Team == CsTeam.CT && _match.BombPlanted) return ForceTopState(CsBotFsmStates.Objective);
            if (self.Team == CsTeam.T && self.HasBomb) return ForceTopState(CsBotFsmStates.Objective);

            return ForceTopState(CsBotFsmStates.Patrol);
        }

        /// <summary>提交顶层状态并返回落定后的名字（引擎语义：自环忽略、回调内转换排队）。</summary>
        private string ForceTopState(string state)
        {
            _fsm.Transition(state);
            return _fsm.Current;
        }

        /// <summary>难度参数的一行文本（初始化日志与自检报告共用）。</summary>
        public string ProfileText()
        {
            return $"难度={_difficulty} 反应={_profile.ReactionTime:F2}s 瞄准误差=±{_profile.AimErrorDegrees:F1}° " +
                   $"转视角={_profile.AimSpeedDegrees:F0}°/s 视野={_profile.VisionRange:F0}m " +
                   $"偏好距离={_profile.PreferredRange:F0}m（角色后={PreferredRange():F0}m）爆头率={_profile.HeadshotChance:P0} " +
                   $"买枪档={_profile.BuyBudgetTier} 重决策={_profile.RepathInterval:F2}s " +
                   $"连发={_profile.FireBurstMin:F2}~{_profile.FireBurstMax:F2}s/停顿={_profile.FirePauseMin:F2}~{_profile.FirePauseMax:F2}s（由模拟执行）";
        }

        /// <summary>
        /// 本机器人当前**战术分工**的中文名（写进开局日志与回合计划日志 —— 就是"真的有分工"的可核对载体：
        /// 同一队 4 个人应当打印出至少 2 种不同的角色名，且 4 个角色的 <c>守点时长</c> 互不相同）。
        /// </summary>
        public string RoleText()
        {
            return $"{CsBotRoles.Label(_role)}（守点×{CsBotRoles.HoldScale(_role):F1} " +
                   $"交火×{CsBotRoles.RangeScale(_role):F2}{(CsBotRoles.StaysOnObjective(_role) ? " 守到底" : "")}）";
        }

        /// <summary>取"进入交战"事件（已被消费则返回 false）。统计与日志用。</summary>
        public bool ConsumeEngage()
        {
            if (!_engageEvent) return false;
            _engageEvent = false;
            return true;
        }

        // ==================================================================
        //  主入口
        // ==================================================================
        public CsBotIntent Think(float dt, float now)
        {
            var intent = new CsBotIntent { State = CsBotState.Idle, Move = Vector3.zero };
            var self = _match.Find(_id);

            if (self == null)
            {
                // actor 已被踢出（KickBot / 比赛停止）→ 返回中性意图 + 非零瞄点，等 BotModule 销毁 brain
                intent.AimPoint = FallbackAim();
                return intent;
            }

            _name = self.Name;
            RefreshProfile(self);
            MaybeResolveRound(self, now);

            if (!self.IsAlive)
            {
                SetState(CsBotState.Idle, now);
                intent.State = CsBotState.Idle;
                intent.Crouch = false;
                intent.AimPoint = LookAhead(self);
                return intent;
            }

            switch (_match.Phase)
            {
                case CsRoundPhase.Freeze:
                    intent = ThinkFreeze(self, intent, now);
                    break;

                case CsRoundPhase.Live:
                    intent = ThinkLive(self, dt, now);
                    break;

                default:
                    // RoundEnd / MatchEnd / None：站着不动（模拟此时也不接受移动/开火）
                    SetState(CsBotState.Idle, now);
                    intent.State = CsBotState.Idle;
                    intent.AimPoint = _goalValid ? WithEyeHeight(_goalPos) : LookAhead(self);
                    break;
            }

            if (intent.AimPoint.sqrMagnitude < 0.0001f) intent.AimPoint = LookAhead(self);

            // 本 tick 要扣扳机 ⇒ 一定站定：子弹散布按水平速度放大（`Firearm.ComputeSpread`），
            // 边跑边打等于把子弹打飞。换弹 / 后撤分支不受影响（那两条 Fire 恒 false）。
            // 放在各分支之后，管住所有分支（交战压点 / 下包 / 拆包掩护 / 投掷 / 推进）。
            if (intent.Fire) intent.Move = Vector3.zero;

            // 无交战目标时朝向跟随行进方向（含避障偏角 / 让行后退），并把注视点推远
            // ⇒ 不背对行进方向、也不因近处路点的横向微动逐帧摆头。
            ApplyTravelFacing(self, ref intent);

            // 瞄准平滑：注视点每帧的跳变按角度限速分摊到多帧（必须是最后一个改 AimPoint 的地方）
            SmoothAim(self, ref intent, now);

            // 全局卡住诊断 + 恢复：有移动意图却连续不动 ⇒ 留痕，并按原因让行 / 换目标（见 TrackStuck）。
            TrackStuck(self, ref intent, now);

            TickRadio(self, now);

            return intent;
        }

        /// <summary>跑一次无线电决策（<see cref="BotRadio"/>），结果由 <see cref="ConsumeRadio"/> 取走。
        /// 放在 <see cref="Think"/> 的收尾 —— 决策所需的状态（可见敌/友、是否交战、目标点、持包）此刻都已落定。</summary>
        private void TickRadio(CsActor self, float now)
        {
            var engaging = _state == CsBotState.Engage;
            var atGoal = _goalValid &&
                (self.Position - _goalPos).sqrMagnitude <=
                CsBotConst.DefaultObjectiveRadius * CsBotConst.DefaultObjectiveRadius;
            var headingToPlant = self.Team == CsTeam.T && self.HasBomb && !_match.BombPlanted && _goalIsSite;

            _radio.SetPerception(_visibleEnemies, _visibleFriends);
            _pendingRadio = _radio.Tick(_match, self, now, engaging, atGoal, headingToPlant);
        }

        /// <summary>取本 tick 待发出的无线电行（已被消费则返回 null）。<c>BotModule</c> 消费。</summary>
        public string ConsumeRadio()
        {
            var line = _pendingRadio;
            _pendingRadio = null;
            return line;
        }

        /// <summary>本 bot 打死了人（由 <c>BotModule.OnKill</c> 调用；对应原版 chatter 的 "Enemy down."）。</summary>
        public void NotifyKill()
        {
            _radio.NotifyKill();
        }

        private void RefreshProfile(CsActor self)
        {
            if (self.Difficulty == _difficulty) return;

            var from = _difficulty;
            _difficulty = self.Difficulty;
            _profile = CsBotProfile.For(_difficulty);

            // H 菜单改难度后，已存在的 bot 参数要**即时生效** —— 这条日志就是证据
            Game.Logger.Info(Tag, $"{_name} 难度切换 {from} → {_difficulty}（下一个决策 tick 起生效）：{ProfileText()}");
        }

        private void MaybeResolveRound(CsActor self, float now)
        {
            if (_planRound == _match.RoundNumber) return;

            _planRound = _match.RoundNumber;

            //   序号在这里算一次，ChoosePlan 直接复用（两处同一口径，不各算一套）。
            _ordinal = CsBotPlans.TeamLocalOrdinal(_match.Actors, self.Team, self.Id);
            _planSlot = -1;
            _role = CsBotRoles.For(self.Team, _ordinal);
            _buy.OnRoundStart(_match.RoundNumber);
            _buy.SetOwner(self.Name);
            _nav.Reset();
            _nav.SetOwner(self.Name);

            _targetId = 0;
            _visibleNow = false;
            _closestVisibleDist = float.MaxValue;
            _firstSeenTime = 0f;
            _lastSeenTime = 0f;
            _lastSeenPos = Vector3.zero;
            _enemySeenAt.Clear();
            _defuserElectAt = 0f;
            _defuserElected = false;
            _pickupKeepCount = 0;
            _heardFresh = false;
            _heardTime = 0f;
            _campMoveUntil = 0f;
            _campPickAt = 0f;
            _holdSpots.Clear();
            _holdSlot = -1;
            _holdSwapAt = 0f;
            _holdSiteMarker = null;
            _holdSwapCount = 0;
            _switchCooldownUntil = 0f;
            _grenadeThrows = 0;
            _nextGrenadeAt = 0f;
            _roundStartAt = now;
            _grenadeReturnToWeapon = false;
            _grenadeDiagKeys.Clear();
            ClearGrenadeAttempt();
            _strafeFlipAt = 0f;
            _engageEvent = false;
            _state = CsBotState.Idle;
            // 状态机同步复位（否则 _fsm.Current 还停在上回合的 Engage/Objective，与新回合的 _state 不一致）
            _fsm.Transition(CsBotFsmStates.Idle);
            _objectiveFsm.Transition(CsBotObjectiveStates.Approach);
            _replanCount = 0;
            _goalHoldUntil = 0f;
            _nextRepathAt = 0f;
            _lastGoalDist = float.MaxValue;
            _stallCount = 0;
            _nextStuckReplanAt = 0f;
            _nextJumpAt = 0f;
            _stuckProbeValid = false;
            _stuckDiagCount = 0;
            _nextStuckDiagLogAt = 0f;
            _stuckAtGoalCount = 0;
            _stuckGoalPos = Vector3.zero;
            _stuckLastEventPos = Vector3.zero;
            _stuckSamePosCount = 0;
            _yieldUntil = 0f;
            _yieldDir = Vector3.zero;
            _unreachableGoal = Vector3.zero;
            _unreachableGoalUntil = 0f;

            _lastSight = BotSightResult.Clear;
            _lastSightBlocker = null;
            _nextSightBlockLogAt = 0f;
            _nextFireDiagAt = 0f;
            _nextGraceLogAt = 0f;

            ChoosePlan(self, now);
        }

        // ==================================================================
        //  本轮计划（路线 + 目标点）
        // ==================================================================
        private void ChoosePlan(CsActor self, float now)
        {
            _planRoute = null;
            _goalValid = false;
            _goalIsSite = false;
            _goalPos = self.Position;

            if (self.Team != CsTeam.T && self.Team != CsTeam.CT)
            {
                Game.Logger.Warn(Tag,
                    $"{self.Name} 的阵营是 {self.Team}（非 T/CT）—— 机器人不该出现在这个阵营，本轮不做导航");
                return;
            }

            //
            //   ① T 队 idx==0 与 idx==1 走**同一条路、同一个目标点**；
            //   ② CT 队 `idx % 3` ⇒ 槽位 0 与槽位 3 撞在 Route_CT_To_A + 同一个点；
            //   ③ 每回合固定出现 1 对 bot 的「路线+目标点」完全一样（Spliff/Darrell 恒同、Gooseman/ZBot 恒同），
            //      另有 44 对"目标点相同" ⇒ 用户看到的正是"路线都是相同的"。
            //
            // 新口径（构造性保证互不相同，不靠随机、不靠概率）：
            //   队内序号 = 同阵营 actor 里按 Id 升序的名次（0..n-1，与"真人在哪队"无关）；
            //   槽位 = (队内序号 - 持包者队内序号) mod 4（双射 ⇒ 4 只 bot 必得 4 个不同槽位）；
            //   持包者恒落槽位 0 = 主攻包点路线（出处：策划/策划案/CS1.6单机参考规格.md:130「T 持包到 B 点 → 下包」）。
            var ordinal = CsBotPlans.TeamLocalOrdinal(_match.Actors, self.Team, self.Id);
            _ordinal = ordinal;
            // 平移量必须取**本回合缓存的那一份**（RoundCarrierOrdinal），不能每只 bot 各问一次
            //   "现在谁持包"：持包者中途被打死会让不同 bot 各拿到 -1 / k ⇒ 槽位重合
            var carrierOrdinal = CsBotPlans.RoundCarrierOrdinal(
                _match.Actors, _match, self.Team, _match.RoundNumber);          // CT ⇒ -1（该函数内已判）
            // 槽位表按**本队实际 bot 数**紧凑映射（3 人时巡逻让位、另一包点恒有人），见 SlotLayout。
            var botCount = CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber);
            var plan = CsBotPlans.For(self.Team, CsBotPlans.SlotOf(ordinal, carrierOrdinal, botCount), _match.RoundNumber);
            _planSlot = plan.Slot;
            _role = plan.Role;   // 角色与计划槽位同源（持包者平移槽位后不许再按队内序号取角色）

            var routeMarker = plan.RouteMarker;
            var siteMarker = plan.GoalMarker;
            _planRoute = routeMarker;
            _nav.SetRoute(_map, routeMarker, self.Position);

            //   目标点**不能只判"地图上有这个标记"**：该点可能落在"位图说可走、但从出生点
            //   按抬腿 ≤ `CsConst.StepUpHeight` 扩张到不了"的格上 ⇒ 引擎 `AStar.Find` 直接 `badgoal` 返回 null
            //   ⇒ 退化成"朝 86m 外的点直线走" ⇒ 顶着墙原地卡到回合结束。
            //   口径与运行时同一份：BotNavigator.CanReach = SnapToWalkable + WalkableCellHeightAware + AStar。
            if (!string.IsNullOrEmpty(siteMarker) && TryPickGoalPointForPlan(self, plan, out var site))
            {
                _goalPos = site;
                _goalValid = true;
                _goalIsSite = plan.GoalIsSite;
            }
            else if (TryPickReachableRouteEnd(self, routeMarker, out var routeEnd))
            {
                // 包点标记缺失 / 该包点没有走得到的点 → 用**本路线上走得到的那个终点**（有路点就一定走得到）
                _goalPos = routeEnd;
                _goalValid = true;
                Game.Logger.Warn(Tag,
                    $"{self.Name} 取不到「走得到」的包点标记 '{siteMarker ?? "无"}' → 退化为「本路线上走得到的终点」" +
                    $"（目标={_goalPos}；判据 = BotNavigator.CanReach）");
            }
            else if (_nav.HasRoute)
            {
                // 兜底：连可达终点都取不到 ⇒ 退回"路线最后一点"（不许比既有实现更差），并把数字打进日志
                _goalPos = _nav.RouteEnd;
                _goalValid = true;
                Game.Logger.Warn(Tag,
                    $"{self.Name} 本路线的标记点**没有一个判为走得到**（路线 '{routeMarker}'）→ " +
                    $"退回未过滤的路线终点 {_goalPos}（⛔ 不复现「无目标」这种更差的行为）");
            }
            else
            {
                Game.Logger.Error(Tag,
                    $"{self.Name} 本轮既没有包点标记也没有任何路点（路线 '{routeMarker}'）→ 只能在原地朝当前朝向警戒。" +
                    "请检查 de_dust2 生成器是否摆了 CsMarkers 里的路线标记");
            }

            ArmGoal(self, now);

            //   「谁走哪条路、去哪个点、担任什么角色、路的头一段是哪」）。不许删字段：
            Game.Logger.Info(Tag,
                $"{self.Name}（{self.Team}/{_difficulty}/角色={RoleText()}）第 {_match.RoundNumber} 回合计划：" +
                $"本队bot={botCount}/槽位={_planSlot}/队内序号={_ordinal}{(self.HasBomb ? "(持C4)" : "")} 路线={_planRoute ?? "无"}" +
                $"（{CsBotPlans.RouteLabel(_planRoute)}） 首段路点={(HasFirstWaypoint ? FirstWaypoint.ToString() : "无")} 路点={_nav.RemainingWaypoints}" +
                $" 目标={( _goalValid ? _goalPos.ToString() : "无" )}（{CsBotPlans.GoalLabel(siteMarker)}#{plan.GoalOrdinal}）站点={_goalIsSite}" +
                $" 守点时长={ObjectiveHoldSeconds():F1}s 重寻路间隔={_profile.RepathInterval:F2}s");
        }

        /// <summary>
        /// 在标记 <paramref name="marker"/> 的点里按槽位序号挑一个**走得到**的目标点。
        ///
        /// <para><b>为什么要"按序号"而不是"取最近"</b>：一个包点标记里有 6~9 个点，两个槽位（T 槽位 0 与 1）
        /// 都去同一个包点 —— 若各自"取离自己最近的点"，两只 bot 从相邻出生点出发会**取到同一个点**。
        /// 改成"取确定性序列里第 <paramref name="ordinal"/> 个"后，**目标点由槽位决定、与出生位置无关**，
        /// 于是"同队两个槽位必得不同目标点"是构造性的。</para>
        ///
        /// <para><b>确定性序列</b> = 该标记的全部点先过滤 <c>CanStand</c>（与移动解算同口径），再按 (x, z) 排序
        /// —— 排序键与 <see cref="CsBotHoldSpots.Build"/> 完全一致（同一份口径，不另写一套）。
        /// 然后从第 <c>ordinal % count</c> 个起**向后找第一个走得到**的点（<see cref="BotNavigator.CanReach"/>）；
        /// 一个都走不到时退化为"最近的走得到的点"并打 Warn（不许比既有实现更差）。</para>
        /// </summary>
        /// <returns>true = 目标点已落进 <paramref name="point"/>。</returns>
        private bool TryPickGoalPoint(CsActor self, string marker, int ordinal, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null || !_map.IsLoaded) return false;

            var pts = _map.Points(marker);
            if (pts == null || pts.Length == 0) return false;

            var cand = new List<Vector3>(pts.Length);
            for (var i = 0; i < pts.Length; i++)
                if (_map.CanStand(pts[i]) && !IsBannedGoal(pts[i])) cand.Add(pts[i]);

            if (cand.Count == 0)
            {
                RateWarn("goal.nostand." + marker,
                    $"{_name} 的标记 '{marker}' 有 {pts.Length} 个点但**没有一个站得住**（CanStand 全 false）" +
                    "→ 退回未过滤点集里离自己最近的（与本方法引入前的行为一致）");
                return TryPickReachablePoint(self, marker, out point);
            }

            cand.Sort(CompareXZ);

            var start = ((ordinal % cand.Count) + cand.Count) % cand.Count;
            for (var k = 0; k < cand.Count; k++)
            {
                var p = cand[(start + k) % cand.Count];
                if (!_nav.CanReach(self.Position, p)) continue;

                point = p;
                return true;
            }

            RateWarn("goal.unreachable." + marker,
                $"{_name} 的标记 '{marker}' 第 {start + 1}/{cand.Count} 个点起**向后一个都走不到**" +
                "→ 退化为「离自己最近的走得到的点」（旧口径，⛔ 不许比旧行为更差）");
            return TryPickReachablePoint(self, marker, out point);
        }

        /// <summary>
        /// 本轮目标点。巡逻位（T 的侦察）取**对侧包点**附近的巡逻点：主攻在 A 时它压 B、主攻在 B 时它压 A，
        /// 同队才不会全部挤在同一个包点。其它标记仍按槽位序号取点。
        /// </summary>
        private bool TryPickGoalPointForPlan(CsActor self, CsBotPlan plan, out Vector3 point)
        {
            if (self.Team == CsTeam.T && plan.GoalMarker == CsMarkers.Patrol)
            {
                var siteIsA = (_match.RoundNumber % 2) == 0;
                var otherSite = siteIsA ? CsMarkers.BombsiteB : CsMarkers.BombsiteA;
                if (TryPickReachableNear(self, CsMarkers.Patrol, otherSite, out point)) return true;
            }

            return TryPickGoalPoint(self, plan.GoalMarker, plan.GoalOrdinal, out point);
        }

        /// <summary>
        /// 在 <paramref name="marker"/> 的点里取**离 <paramref name="anchorMarker"/> 最近**、且走得到的点
        /// （CanStand 与 CanReach 与 <see cref="TryPickGoalPoint"/> 同口径）。
        /// </summary>
        private bool TryPickReachableNear(CsActor self, string marker, string anchorMarker, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null || !_map.IsLoaded) return false;

            var pts = _map.Points(marker);
            var anchors = _map.Points(anchorMarker);
            if (pts == null || pts.Length == 0 || anchors == null || anchors.Length == 0) return false;

            var best = float.MaxValue;
            var found = false;
            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.CanStand(pts[i])) continue;
                if (IsBannedGoal(pts[i])) continue;

                var nearest = float.MaxValue;
                for (var a = 0; a < anchors.Length; a++)
                {
                    var v = pts[i] - anchors[a];
                    v.y = 0f;
                    nearest = Mathf.Min(nearest, v.magnitude);
                }

                if (nearest >= best) continue;
                if (!_nav.CanReach(self.Position, pts[i])) continue;

                best = nearest;
                point = pts[i];
                found = true;
            }

            return found;
        }

        private static int CompareXZ(Vector3 a, Vector3 b)
        {
            var c = a.x.CompareTo(b.x);
            return c != 0 ? c : a.z.CompareTo(b.z);
        }

        private bool TryPickPoint(string marker, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null)
            {
                RateWarn("map.null", $"{_name} 需要地图标记 '{marker}' 但地图未绑定（ICsMap == null）");
                return false;
            }

            if (_map.TryGetPoint(marker, out point)) return true;

            RateWarn("point.missing." + marker, $"{_name} 取标记 '{marker}' 失败（地图上没有这个标记）");
            return false;
        }

        // ==================================================================
        //  目标生命周期：守点时长到 / 连续卡住 / 没有进展 → 重新选目标
        // ==================================================================
        /// <summary>
        /// 守点（到达目标后原地警戒）的时长（秒），同时也是"多久重新选一次目标"的间隔。
        ///
        /// <para>= <c>max(</c><see cref="CsBotConst.CampHoldSeconds"/>,
        /// <c>profile.RepathInterval × </c><see cref="CsBotConst.ObjectiveHoldScale"/><c>)</c>：
        /// Easy 1.6s → 4.8s / Normal 1.0s → 3.0s / Hard 0.5s → 2.5s。
        /// 这是三档难度里"重寻路间隔"最直观的落地 —— 低难度慢慢挪、高难度勤换位。</para>
        /// </summary>
        private float ObjectiveHoldSeconds()
        {
            //   突破手（0.6×）到点就走、守点位（2.5×）赖着不走，这才是用户要的"分工"。
            //   难度倍率与角色倍率是**两层**：前者定"这局 bot 多勤快"，后者定"队里每个人干什么"。
            var roleScale = CsBotRoles.HoldScale(_role);
            return Mathf.Max(CsBotConst.CampHoldSeconds,
                _profile.RepathInterval * CsBotConst.ObjectiveHoldScale) * roleScale;
        }

        /// <summary>
        /// 偏好交战距离（米）= <c>CsBotProfile.PreferredRange × <see cref="CsBotRoles.RangeScale"/></c>。
        /// 唯一取值处：交战段（<c>Engage</c>）不许再直接读 <c>_profile.PreferredRange</c>，
        /// 否则角色倍率只在一半的判定里生效（同一支枪在"推进"和"开火"上按两套距离走）。
        /// </summary>
        private float PreferredRange()
        {
            return _profile.PreferredRange * CsBotRoles.RangeScale(_role);
        }

        /// <summary>换了目标之后的统一收尾：重置守点计时、重寻路计时与"朝目标距离"基准。</summary>
        private void ArmGoal(CsActor self, float now)
        {
            _goalHoldUntil = 0f;        // 0 = 还没到达；Advance 里到达时才起算守点时长
            _nextRepathAt = now + Mathf.Max(0.25f, _profile.RepathInterval);
            var d = _goalValid ? _goalPos - self.Position : Vector3.zero;
            d.y = 0f;
            _lastGoalDist = d.magnitude;
            _stallCount = 0;
            _stuckAtGoalCount = 0;
            _stuckGoalPos = _goalPos;
            _campPickAt = 0f;
            _campMoveUntil = 0f;

            // 包点守卫 → 构造守位表（数据源 = 包点标记点，见 CsBotHoldSpots）。
            // 非包点目标（中路 / 巡逻 / 出生点）清空守位表 ⇒ 那几个目标仍走旧的"守够就换目标"。
            _holdSpots.Clear();
            _holdSlot = -1;
            _holdSwapAt = 0f;
            _holdSiteMarker = null;

            if (_goalValid && _goalIsSite && self.Team == CsTeam.CT)
            {
                NearestBombsitePoint(_goalPos, out var siteDist, out var siteName);
                if (siteDist <= CsBotConst.SiteRadius)
                {
                    _holdSiteMarker = siteName == "Bombsite A" ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
                    CsBotHoldSpots.Build(_map, _holdSiteMarker, _holdSpots);
                    // 记忆期内"走不到"的守位直接剔除（BotUnstuck 式的物理卡点在这里被跳过，而不是反复走进去）。
                    for (var i = _holdSpots.Count - 1; i >= 0; i--)
                        if (IsBannedGoal(_holdSpots[i])) _holdSpots.RemoveAt(i);

                    var n = _holdSpots.Count;
                    if (n >= 2)
                    {
                        _holdSlot = CsBotHoldSpots.PickSlot(_holdSpots, -1, self, _match.Actors);
                        Game.Logger.Info(Tag,
                            $"守位表就绪：{_name}（CT）包点 {_holdSiteMarker} 守位 {n} 个 " +
                            $"起始守位 {_holdSlot + 1}/{n} @ {_holdSpots[_holdSlot]} " +
                            $"（每 {CsBotConst.HoldSwapSeconds:F0}s 换位一次；守位间距 ≥ {CsBotConst.HoldSpotMinSeparation:F1}m）");
                    }
                    else
                    {
                        _holdSiteMarker = null;   // 取不到多点 ⇒ 明确退回单点守点（并已由 Build 打 Warn）
                    }
                }
                else
                {
                    Game.Logger?.Warn(Tag,
                        $"{_name} 的目标被判为包点（_goalIsSite）但离最近的包点标记 {siteDist:F1}m > {CsBotConst.SiteRadius:F0}m " +
                        "→ 不构造守位表，退回单点守点");
                }
            }
        }

        /// <summary>
        /// 离 <paramref name="pos"/> 最近的**包点标记点**（A/B 一起比），并给出这个距离与包点名。
        ///
        /// <para>这就是"在不在包点里"的判据 —— 与 <c>CsBomb.IsInBombsite</c>（<c>Module/Match/CsBomb.cs:478</c>）
        /// 同一口径：到**任一**包点标记的水平距离 ≤ <c>CsMarkers.BombsiteRadius</c>（= <see cref="CsBotConst.SiteRadius"/>）。</para>
        /// </summary>
        /// <returns>最近的包点标记点；地图/标记不可用时返回 <paramref name="pos"/> 且距离 = <c>float.MaxValue</c>。</returns>
        private Vector3 NearestBombsitePoint(Vector3 pos, out float dist, out string siteName)
        {
            dist = float.MaxValue;
            siteName = null;
            var point = pos;

            if (_map == null || !_map.IsLoaded) return point;

            for (var s = 0; s < 2; s++)
            {
                var isA = s == 0;
                var pts = _map.Points(isA ? CsMarkers.BombsiteA : CsMarkers.BombsiteB);
                if (pts == null) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - pos;
                    d.y = 0f;
                    var len = d.magnitude;
                    if (len >= dist) continue;

                    dist = len;
                    point = pts[i];
                    siteName = isA ? "Bombsite A" : "Bombsite B";
                }
            }

            return point;
        }

        /// <summary>
        /// 消费导航报上来的"卡住"事件。连续卡住达到 <see cref="CsBotConst.StuckReplanStreak"/> 次就
        /// **重新选目标** —— 只"跳过当前路点 + 换向"在路线走完后这两步都是空操作，
        /// 于是机器人停在原地被反复判卡住。
        /// </summary>
        private void HandleStuck(CsActor self, float now)
        {
            if (!_nav.ConsumeStuck(out var moved, out var escalate)) return;
            if (!escalate) return;

            // 正在下包/拆包时不换目标（一移动进度就清零）；拆包任务的目标是 C4，换目标没有意义。
            if (self.UseProgress >= 0f) return;
            if (_match.BombPlanted && self.Team == CsTeam.CT) return;

            //   没有这道闸时：0.5s 一次判定 / 2 次即升级 ⇒ 最快 1.0s 就换一次目标，而每个新目标都在
            //   86m 外 ⇒ 一次都没走完就换掉 ⇒ 目标反复横跳、净位移≈0。
            if (now < _nextStuckReplanAt)
            {
                RateWarn("stuckreplan.cooldown",
                    $"{_name} 连续卡住但仍处于换目标冷却期（{_nextStuckReplanAt - now:F1}s 后到期，" +
                    $"窗口 {CsBotConst.StuckReplanCooldownSeconds:F0}s）→ 本 tick 只换向不换目标");
                return;
            }

            _nextStuckReplanAt = now + CsBotConst.StuckReplanCooldownSeconds;

            ReplanObjective(self,
                $"连续卡住 {CsBotConst.StuckReplanStreak} 次（最近 {CsBotConst.StuckCheckInterval:F1}s 内位移 {moved:F2}m，" +
                $"原路线 '{_planRoute ?? "无"}'，剩余路点 {_nav.RemainingWaypoints}）", now);
        }

        // ==================================================================
        //  全局卡住诊断（有移动意图却连续不动）
        // ==================================================================
        /// <summary>
        /// 有移动意图（<c>intent.Move</c> 水平分量 ≥ <see cref="CsBotConst.StuckIntentMoveEpsilon"/>）
        /// 却在 <see cref="CsBotConst.StuckDiagSeconds"/> 内水平位移不足
        /// <see cref="CsBotConst.StuckDiagMoveEpsilon"/> ⇒ 判一次卡住：打一条 Warn（名字 / 位置 / 状态 / 目标与距离 /
        /// CanReach / 当前路点与剩余点数 / 1m 内角色 / OnGround / 导航阻挡原因），并按原因恢复：
        /// ① 1m 内有角色挡在移动方向上 ⇒ 侧移 + 后退让行 <see cref="CsBotConst.YieldSeconds"/> 秒；
        /// ② 目标走不到（<c>CanReach=false</c>，或同一目标连续 <see cref="CsBotConst.UnreachableGoalStreak"/>
        /// 个窗口不动）⇒ 记入不可达记忆，摘掉该守位或换下一个目标。
        ///
        /// <para>站定是合法状态（守点 / 下包 / 拆包 / 开火时 <c>Move</c> 为零向量）⇒ 只统计"想走却走不动"。</para>
        /// </summary>
        private void TrackStuck(CsActor self, ref CsBotIntent intent, float now)
        {
            // 让行窗口内只替换移动方向；瞄准 / 开火 / 下包意图保持不变。
            if (now < _yieldUntil && intent.Move.sqrMagnitude > 0.0001f) intent.Move = _yieldDir;

            var move = intent.Move;
            move.y = 0f;
            if (move.magnitude < CsBotConst.StuckIntentMoveEpsilon)
            {
                _stuckProbeValid = false;
                return;
            }

            if (!_stuckProbeValid)
            {
                _stuckProbeValid = true;
                _stuckProbePos = self.Position;
                _stuckProbeSince = now;
                return;
            }

            var d = self.Position - _stuckProbePos;
            d.y = 0f;
            var moved = d.magnitude;
            if (moved >= CsBotConst.StuckDiagMoveEpsilon)
            {
                _stuckProbePos = self.Position;
                _stuckProbeSince = now;
                return;
            }

            var window = now - _stuckProbeSince;
            if (window < CsBotConst.StuckDiagSeconds) return;

            // 窗口结算：基准前移 ⇒ 连续不动时每个窗口各记一次，但日志由时间闸限频。
            _stuckProbePos = self.Position;
            _stuckProbeSince = now;
            _stuckDiagCount++;

            if ((_stuckGoalPos - _goalPos).sqrMagnitude > 1f)
            {
                _stuckGoalPos = _goalPos;
                _stuckAtGoalCount = 0;
            }
            _stuckAtGoalCount++;

            var p = self.Position;
            if ((_stuckLastEventPos - p).sqrMagnitude <= 0.25f) _stuckSamePosCount++;
            else
            {
                _stuckLastEventPos = p;
                _stuckSamePosCount = 1;
            }

            var mate = NearestOtherActor(self, out var mateDist, out var mateTeam, out var mateName);
            var toMateDot = 0f;
            if (mate != null)
            {
                var toMate = mate.Position - self.Position;
                toMate.y = 0f;
                if (toMate.sqrMagnitude > 0.0001f) toMateDot = Vector3.Dot(toMate.normalized, move.normalized);
            }
            var mateBlocking = mate != null && mateDist <= CsBotConst.StuckNeighborRadius &&
                               (toMateDot >= 0.2f || mateDist <= 0.7f);
            var reachable = !_goalValid || _nav.CanReach(self.Position, _goalPos);

            if (now >= _nextStuckDiagLogAt)
            {
                _nextStuckDiagLogAt = now + CsBotConst.StuckDiagLogInterval;
                Game.Logger.Warn(Tag,
                    BuildStuckLine(self, move, window, moved, mate, mateBlocking, reachable));
            }

            if (RecoveryDisabledForTest) return;

            // ① 角色贴身挡路：位图看不到角色 ⇒ 先让行，不走"目标不可达"那条。
            if (mateBlocking)
            {
                StartYield(self, move, mate, now);
                return;
            }

            // ② 目标本身走不到：位图不连通，或位图可走但物理连续两个窗口推不动。
            var goalDriven = _state == CsBotState.Patrol || _state == CsBotState.Camp || _state == CsBotState.Plant;
            if (goalDriven && _goalValid && (!reachable || _stuckAtGoalCount >= CsBotConst.UnreachableGoalStreak))
            {
                var blocked = _goalPos;
                if (_state == CsBotState.Plant && self.HasBomb && TryGetPlantAim(self, out var aim)) blocked = aim;
                BanGoalAndReplan(self, blocked, now, reachable ? "位图可走但物理连续走不动" : "CanReach=false");
                return;
            }

            // ③ 其它局部几何卡阻：短暂侧移 + 后退，自己换个入角。
            StartYield(self, move, null, now);
        }

        /// <summary>1m 内最近的角色（含队友与敌人）；没有则返回 null。</summary>
        private CsActor NearestOtherActor(CsActor self, out float dist, out CsTeam team, out string name)
        {
            dist = float.MaxValue;
            team = self.Team;
            name = null;

            var actors = _match.Actors;
            CsActor best = null;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == self.Id || !a.IsAlive) continue;

                var d = a.Position - self.Position;
                d.y = 0f;
                var len = d.magnitude;
                if (len >= dist) continue;

                dist = len;
                best = a;
            }

            if (best != null)
            {
                team = best.Team;
                name = best.Name;
            }
            else dist = float.MaxValue;

            return best;
        }

        /// <summary>组装卡住 Warn 的一整行（字段顺序 = 排查时的阅读顺序）。</summary>
        private string BuildStuckLine(CsActor self, Vector3 move, float window, float moved,
            CsActor mate, bool mateBlocking, bool reachable)
        {
            var sb = new System.Text.StringBuilder(320);
            sb.Append("[卡住] ").Append(_name)
              .Append(" 状态=").Append(_state)
              .Append(" 连续 ").Append(window.ToString("F1")).Append("s 水平位移 ")
              .Append(moved.ToString("F2")).Append("m<").Append(CsBotConst.StuckDiagMoveEpsilon.ToString("F2"))
              .Append("m（本回合第 ").Append(_stuckDiagCount).Append(" 次，同一位置第 ")
              .Append(_stuckSamePosCount).Append(" 次）")
              .Append(" 位置=(").Append(self.Position.x.ToString("F2")).Append(',')
              .Append(self.Position.y.ToString("F2")).Append(',')
              .Append(self.Position.z.ToString("F2")).Append(')')
              .Append(" OnGround=").Append(self.OnGround);

            // 目标：交战追的是人、捡包追的是 C4，其余追本轮目标点。
            var kind = "路线目标";
            var target = _goalPos;
            if (_state == CsBotState.Engage)
            {
                var t = _match.Find(_targetId);
                target = t != null && t.IsAlive ? t.Position : _lastSeenPos;
                kind = "交战追击";
            }
            else if (IsPickupRun(self))
            {
                target = _match.BombPosition;
                kind = "捡 C4";
            }
            else if (_state == CsBotState.Plant) kind = "下包点";
            else if (_goalIsSite) kind = "包点/守位";

            var td = target - self.Position;
            td.y = 0f;
            sb.Append(" 目标=").Append(kind).Append('(')
              .Append(target.x.ToString("F1")).Append(',').Append(target.z.ToString("F1")).Append(')')
              .Append(" 距 ").Append(td.magnitude.ToString("F1")).Append("m")
              .Append(" CanReach=").Append(reachable)
              .Append(" 移动意图=(").Append(move.x.ToString("F2")).Append(',').Append(move.z.ToString("F2")).Append(')');

            var wp = _state == CsBotState.Camp && _holdSlot >= 0 && _holdSlot < _holdSpots.Count
                ? _holdSpots[_holdSlot]
                : _nav.CurrentTarget(self.Position, _goalPos);
            sb.Append(" 当前路点=(").Append(wp.x.ToString("F1")).Append(',').Append(wp.z.ToString("F1")).Append(')')
              .Append(" 剩余路点=").Append(_nav.RemainingWaypoints)
              .Append(" 路线=").Append(_planRoute ?? "无");

            var navReason = _nav.BlockedReason(self.Position, now: CsClock.Now);
            sb.Append(" 导航阻挡=").Append(navReason ?? "无记录");
            if (_map != null && _map.IsLoaded)
            {
                sb.Append(" 位图可走=").Append(_map.WalkableAt(self.Position.x, self.Position.z))
                  .Append(" 可站=").Append(_map.CanStand(self.Position));
            }

            // 1m 内的全部角色（不只最近一个）：队友推挤与敌人贴身是两种不同的卡住成因。
            sb.Append(" 1m内角色=");
            var listed = 0;
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == self.Id || !a.IsAlive) continue;

                var ad = a.Position - self.Position;
                ad.y = 0f;
                var adist = ad.magnitude;
                if (adist > CsBotConst.StuckNeighborRadius) continue;

                if (listed == 0) sb.Append('[');
                else sb.Append("; ");

                var isMate = a.Team == self.Team;
                sb.Append(isMate ? "队友 " : "敌方 ").Append(a.Name)
                  .Append('(').Append(adist.ToString("F2")).Append("m")
                  .Append(a.Id == (mate != null ? mate.Id : 0L)
                      ? (mateBlocking ? " 挡在移动方向上" : " 不在移动方向上")
                      : string.Empty)
                  .Append(')');
                listed++;
                if (listed >= 4) break;
            }

            if (listed == 0) sb.Append("无");
            else sb.Append(']');

            return sb.ToString();
        }

        /// <summary>是否正在执行"去捡掉落的 C4"（这个目标不能被当成普通目标拉黑：必须有人去捡）。</summary>
        private bool IsPickupRun(CsActor self)
        {
            if (self.Team != CsTeam.T || self.HasBomb || _match.BombPlanted) return false;
            if (_match.BombPosition.sqrMagnitude < 1f) return false;
            if (AnyTCarriesBomb()) return false;
            return IsElectedBombHunter(self);
        }

        /// <summary>
        /// 让行：沿移动方向的侧向 + 后退一步（对角），<see cref="CsBotConst.YieldSeconds"/> 秒内覆盖移动方向。
        /// 侧向符号优先"避开挡路的那个角色"；没有角色时按 id 奇偶取一侧（确定性，不掷随机数）。
        /// </summary>
        private void StartYield(CsActor self, Vector3 move, CsActor blocker, float now)
        {
            var fwd = move;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) return;
            fwd.Normalize();

            var side = Vector3.Cross(Vector3.up, fwd);
            side.y = 0f;
            if (side.sqrMagnitude < 0.0001f) return;
            side.Normalize();

            var sign = (self.Id & 1L) == 0L ? 1f : -1f;
            if (blocker != null)
            {
                var toBlocker = blocker.Position - self.Position;
                toBlocker.y = 0f;
                if (Vector3.Dot(side, toBlocker) > 0f) sign = -1f;
                else sign = 1f;
            }

            var dir = side * sign - fwd * 0.6f;
            _yieldDir = dir.normalized;
            _yieldUntil = now + CsBotConst.YieldSeconds;
            _stuckProbeValid = false;      // 让行期间位移会变，重新取基准
        }

        /// <summary>
        /// 把走不到的点记入记忆（<see cref="CsBotConst.UnreachableGoalMemorySeconds"/> 秒），
        /// 守位表里有它就摘掉换下一个守位；否则换下一个目标。
        /// </summary>
        private void BanGoalAndReplan(CsActor self, Vector3 blocked, float now, string why)
        {
            var streak = _stuckAtGoalCount;
            _unreachableGoal = blocked;
            _unreachableGoalUntil = now + CsBotConst.UnreachableGoalMemorySeconds;
            _stuckAtGoalCount = 0;
            _nextStuckReplanAt = now + CsBotConst.StuckReplanCooldownSeconds;   // 与 HandleStuck 共用冷却，避免同 tick 连换两次

            if (_holdSpots.Count >= 2 && _holdSlot >= 0 && _holdSlot < _holdSpots.Count)
            {
                var d = _holdSpots[_holdSlot] - blocked;
                d.y = 0f;
                if (d.magnitude <= CsBotConst.StuckNeighborRadius)
                {
                    var dropped = _holdSpots[_holdSlot];
                    _holdSpots.RemoveAt(_holdSlot);
                    _holdSlot = CsBotHoldSpots.PickSlot(_holdSpots, -1, self, _match.Actors);
                    _holdSwapAt = now + CsBotConst.HoldSwapSeconds;
                    Game.Logger.Warn(Tag,
                        $"[卡住] {_name} 守位 ({dropped.x:F1},{dropped.z:F1}) 判为走不到（{why}，连续 {streak} 个窗口）" +
                        $"→ 摘掉该守位，换到 {(_holdSlot >= 0 ? _holdSpots[_holdSlot].ToString() : "无")}" +
                        $"（本包点剩 {_holdSpots.Count} 个守位）");
                    return;
                }
            }

            var ok = ReplanObjective(self,
                $"目标 ({blocked.x:F1},{blocked.z:F1}) 判为走不到（{why}，连续 {streak} 个窗口位移不足）", now);
            Game.Logger.Warn(Tag,
                $"[卡住] {_name} 目标 ({blocked.x:F1},{blocked.z:F1}) 记入不可达记忆 {CsBotConst.UnreachableGoalMemorySeconds:F0}s" +
                $"（{why}）→ {(ok ? "已换下一个目标" : "换目标失败（保留原目标）")}");
        }

        /// <summary>这个点是否在"走不到"记忆里（选目标 / 选守位 / 选下包点时跳过）。</summary>
        private bool IsBannedGoal(Vector3 p)
        {
            if (_unreachableGoalUntil <= CsClock.Now) return false;
            var d = p - _unreachableGoal;
            d.y = 0f;
            return d.sqrMagnitude <= 1f;
        }

        /// <summary>跳跃发起冷却（秒）：贴着矮墙/箱沿反复跳只会在原地蹦。</summary>
        private const float JumpCooldown = 1.2f;

        /// <summary>判断用的跳跃预备距离（米）—— 卡住判定（StuckCheckInterval 0.5s × 5.4 m/s）之后再稍近一点。</summary>
        private const float JumpLookAhead = 1.6f;

        /// <summary>
        /// 跳跃意图：<b>只在"想走的那个方向上有可跳上去的台阶/箱沿"时才置 <c>intent.Jump</c></b>。
        ///
        /// <para><b>为什么写在这里（而不是 BotNavigator）</b>：导航只产出 Move 方向，
        /// Jump 的执行在 <c>CsMatch.UpdateBots</c>（<c>intent.Jump && OnGround</c> 时给
        /// <c>Velocity.y = JumpSpeed</c>），需要 <c>CsBotIntent</c> —— 只有大脑能写它。
        /// 过去全工程<b>没有任何分支给 intent.Jump 赋过值</b>（grep 无匹配），这就是"AI 不会跳"的根因。</para>
        ///
        /// <para><b>判据（两条都过才跳）</b>：</para>
        /// <list type="number">
        /// <item>沿 Move 方向前方 <see cref="JumpLookAhead"/> m 处探到的地面比脚下高
        /// <b>≤ StepUpHeight 且 > 5cm</b>（≤ StepUpHeight 否则根本跳不上去；> 5cm 排除平地微差）；</item>
        /// <item>头顶与目标地面之间**竖直通畅**（跳起 1.2m 内没有压着的天花板/横梁）。</item>
        /// </list>
        ///
        /// <para>冻结期（ThinkFreeze）与站桩状态（Idle）不跳 —— 那些 Move 是零向量，本来也不进这里。
        /// 冷却 <see cref="JumpCooldown"/> 防贴墙连跳；日志按 <see cref="RateWarn"/> 降频。</para>
        /// </summary>
        private void MaybeJump(CsActor self, ref CsBotIntent intent, float now)
        {
            if (intent.Jump) return;                       // 本 tick 已有别的分支要求跳
            if (self.OnGround == false) return;            // 空中不补跳
            if (now < _nextJumpAt) return;
            if (intent.Move.sqrMagnitude < 0.0001f) return; // 没在移动（守点 / 下包 / 拆包 / 交战站桩）

            var move = intent.Move;
            move.y = 0f;
            move.Normalize();

            // ① 前方探地面：脚下 y 与 (前方 JumpLookAhead) 处的地面 y 比较
            var ahead = self.Position + move * JumpLookAhead;
            if (_map == null || !_map.IsLoaded) return;
            if (!_map.TrySampleGround(ahead, out var ground, out var normal, CsConst.GroundCheckDistance + 3f))
                return;

            var rise = ground.y - self.Position.y;
            if (rise <= 0.05f) return;                      // 平地或下坡：不用跳
            if (rise > CsConst.StepUpHeight) return;        // 跳也上不去的高台：不浪费跳
            if (normal.y < CsConst.MaxStandableSlopeNormalZ) return;   // 陡面跳上去也站不住

            // ② 头顶通暢：跳起高度约 v²/2g = 6.82²/(2×20.32) ≈ 1.15m，取 1.2m
            var head = new Vector3(ahead.x, ground.y + 1.2f, ahead.z);
            var body = self.Position + Vector3.up * CsConst.StepUpHeight;
            if (Physics.Linecast(body, head, 1 << PhysicsLayers.World, QueryTriggerInteraction.Ignore))
                return;

            intent.Jump = true;
            _nextJumpAt = now + JumpCooldown;
            RateWarn("bot.jump",
                $"{_name} 判定可跳：方向 ({move.x:F2}, {move.z:F2})、前方 {JumpLookAhead:F1}m 处高差 " +
                $"{rise:F2}m ≤ StepUpHeight {CsConst.StepUpHeight:F2}m、头顶通畅 → 置 intent.Jump（冷却 {JumpCooldown:F1}s）");
        }

        /// <summary>
        /// 重新选目标 —— 本轮路线走完 / 守点时长到 / 连续卡住时的**唯一**出路。
        ///
        /// <para>候选顺序（任一命中即用；每个候选目标点都要"走得到"才采用）：</para>
        /// <list type="number">
        /// <item><b>换一条路线</b>：同阵营的另一条路（T: A 路 / B 路 / 中路；CT: A 守 / B 守 / 中），
        /// 目标是该路配套的包点（拿不到包点标记时退化为该路终点）；</item>
        /// <item><b>去巡逻</b>：<see cref="CsMarkers.Patrol"/> 整条巡点路线（最近邻排序后逐个走）——
        /// 对应"无敌人时在 Route_Patrol 里随机巡点"；</item>
        /// <item><b>回本方出生点</b>：最坏情况也能回到有队友的地方，绝不原地空转。</item>
        /// </list>
        ///
        /// <para>每 <see cref="CsBotConst.ReplanAlternatePeriod"/> 次换目标交替一次"换路线优先 / 去巡逻优先"，
        /// 否则机器人会永远在同两条路线之间来回、永远不去巡图。</para>
        /// </summary>
        /// <returns>true = 已选到新目标（<see cref="_goalPos"/> / <see cref="_planRoute"/> 已更新）。</returns>
        private bool ReplanObjective(CsActor self, string reason, float now)
        {
            if (_map == null || !_map.IsLoaded)
            {
                RateWarn("replan.nomap",
                    $"{_name} 需要重新选目标（{reason}）但地图不可用（{(_map == null ? "未绑定" : "尚未加载")}）→ 先原地警戒");
                return false;
            }

            _replanCount++;

            // 包已下 → T 的新目标就是 C4 本身（守包）；这时去"换路线/巡逻"等于把回合送掉。
            if (_match.BombPlanted && self.Team == CsTeam.T)
            {
                var bomb = _match.BombPosition;
                if (bomb.sqrMagnitude > 1f && _map.WalkableAt(bomb.x, bomb.z))
                {
                    _nav.ClearRoute();
                    ApplyObjective(self, CsBotConst.BombGuardRoute, bomb, false, $"包已下 → 回 C4 旁守包（{reason}）", now);
                    return true;
                }

                RateWarn("replan.bomb.nowalk",
                    $"{_name} 包已下但 C4 位置 {bomb} 不可走（WalkableAt=false）→ 退化为常规换目标");
            }

            var preferPatrol = (_replanCount % CsBotConst.ReplanAlternatePeriod) == 0;

            //   **本项目新增**（修"AI 无胜利策略 / 不守点"）：包未下时，换目标**不许离开本轮的分工目标**。
            //   规格（策划/策划案/CS1.6单机参考规格.md §2.4）：CT = 守包点、T = 进攻包点下包；
            //   而本函数原先的候选序是"换路线 → 巡逻 → 出生点"且**隔次以巡逻优先** ⇒ 包未下的绝大多数时间里
            //   bot 落进 Route_Patrol 到处巡点 ⇒ 用户看到的"没有胜利策略、乱跑、不蹲点不进攻"。
            //   规则（按 CsBotPlans.For 的槽位分工，不新增数据表）：当前槽位的计划目标是**包点**的
            //   （T 的 0/1/3 进攻位、CT 的 0/1 守点位）换目标时只许回到自己分工的包点 ——
            //   不去巡逻、不出生点；侦察位（槽位 2，计划目标就是巡逻线）保持原候选序游走。
            if (!_match.BombPlanted)
            {
                var carrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
                var botCount = CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber);
                var from = _planSlot >= 0 ? _planSlot : CsBotPlans.SlotOf(_ordinal, carrier, botCount);
                var anchor = CsBotPlans.For(self.Team, from, _match.RoundNumber);
                var anchorSite = anchor.GoalIsSite ? _map.Points(anchor.GoalMarker) : null;

                if (anchorSite != null && anchorSite.Length > 0)
                {
                    // 路线被活着的队友占着（接管/撞车后的残留）⇒ 不动路线、只换目标点，且挑点要避开队友
                    var routeFree = !CsBotPlans.FamilyHeldByAliveTeammate(_match.Actors, self.Team, carrier,
                        _match.RoundNumber, self.Id, anchor.RouteMarker);

                    //   与 TryRouteObjective 同一不变量：**先只读筛、选定后才切路线** ——
                    //   TryPickGoalPoint / TryPickReachable 只用 _nav.CanReach（与 nav 当前装哪条路线无关），
                    //   全都筛不出时 nav 一个字都不动（否则残留会污染下面 TryRouteObjective 的 prevMarker）。
                    if (TryPickGoalPoint(self, anchor.GoalMarker, anchor.GoalOrdinal, out var goalPt))
                    {
                        if (routeFree) _nav.SetRouteKeepProgress(_map, anchor.RouteMarker, self.Position);
                        ApplyObjective(self, anchor.RouteMarker, goalPt, true,
                            $"站回分工位（{reason}；槽位 {anchor.Slot}={CsBotRoles.Label(anchor.Role)}" +
                            $"→{CsBotPlans.RouteLabel(anchor.RouteMarker)}→{CsBotPlans.GoalLabel(anchor.GoalMarker)}，" +
                            $"包未下不巡逻）", now);
                        return true;
                    }

                    // 分工位取不到走得到的点（或路线被占）→ 该包点标记里走得到且不与队友同格的点
                    if (TryPickReachable(self, anchorSite, CsBotConst.MinPatrolDistance, false,
                                            out var sitePt, avoidTeammates: true) ||
                        TryPickReachable(self, anchorSite, 0f, false, out sitePt, avoidTeammates: true))
                    {
                        if (routeFree) _nav.SetRouteKeepProgress(_map, anchor.RouteMarker, self.Position);
                        ApplyObjective(self, anchor.RouteMarker, sitePt, true,
                            $"站回分工包点上走得到的点（{reason}；分工位 '{anchor.GoalMarker}'#{anchor.GoalOrdinal} 走不到" +
                            $"{(routeFree ? "" : " 或路线被队友占")}）", now);
                        return true;
                    }

                    RateWarn("replan.anchor.unreachable",
                        $"{_name} 分工位（槽位 {anchor.Slot} → {anchor.GoalMarker}#{anchor.GoalOrdinal}）" +
                        $"及其包点标记全判为走不到（{reason}）→ 退回常规候选序");
                }
            }

            // 常规候选序只留给"槽位 2 的侦察 / 分工位全部走不到"这两种情况（与本函数引入前一致）：
            if (preferPatrol && TryPatrolObjective(self, reason, now)) return true;
            if (TryRouteObjective(self, reason, now)) return true;
            if (!preferPatrol && TryPatrolObjective(self, reason, now)) return true;
            if (TrySpawnObjective(self, reason, now)) return true;
            if (TryLastResortObjective(self, reason, now)) return true;

            RateWarn("replan.exhausted",
                $"{_name} 重新选目标失败（{reason}）：路线 / 巡逻点 / 出生点都取不到可走的目标点 → 原地警戒。" +
                "请检查地图标记（CsMarkers）与几何体是否把标记点埋住了");
            return false;
        }

        /// <summary>
        /// 换目标：从**自己的槽位**起按 <c>(_planSlot + k) % 4</c> 轮转（k = 1,2,3）挑下一条路。
        ///
        /// <para><b>为什么按槽位轮转</b>：若按 <c>(Id + replanCount) % 3</c> 之类取起点、且路只有 3 条，
        /// 4 只 bot 必然有两只落在同一条路上（换目标之后"路线都一样"，正是用户看到的现象）。</para>
        ///
        /// <para>从槽位起轮转 ⇒ 每只 bot 的 <c>_planSlot</c> 互不相同，取 <c>k</c> 相同的两只 bot
        /// 得到的槽位也互不相同 ⇒ **每次换目标后，同队 4 只 bot 仍走 4 条不同的路、去 4 个不同的目标点**
        /// （撞车需要两只 bot 的槽位相同，而那已被构造性排除）。</para>
        ///
        /// <para>轮转时跳过**刚走完的那条路**（<c>_planRoute</c>）：槽位差 1 的平移天然换路，所以 k 从 1 起即可。</para>
        /// </summary>
        private bool TryRouteObjective(CsActor self, string reason, float now)
        {
            // **进入本函数时** nav 装的是哪条路线：本函数内每一档都会在**筛选期间**尝试换路线
            // （`SetRouteKeepProgress` 一旦发现标记不同就立即 SetRoute 重排 + 游标归零），所以必须在这里 ——
            // 而不是在中途某一档 —— 记住"前值"，否则抓到的已经是前面档位的尝试结果。最终若一个都没选中，
            // 末尾把它恢复回来（"没选中 ⇒ 不改变 nav 状态"）。
            var prevMarker = _nav.RouteMarker;

            // 「路线家族」是同队的**共享资源**：计划表保证开局 4 只各占一格，轮转也必须遵守同一条互斥，
            // 否则一只卡住的 bot 会轮转进队友正走着的路（日志形态：两只**都活着**的 bot 同时走同一条
            // 'Route_T_*'，即用户看到的"路线都一样"）。
            var carrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
            var taken = CsBotPlans.AliveSlotTable(_match.Actors, self.Team, carrier, _match.RoundNumber, null);
            var botCount = CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber);
            var from = _planSlot >= 0 ? _planSlot : CsBotPlans.SlotOf(_ordinal, carrier, botCount);

            for (var k = 1; k <= CsBotPlans.Slots; k++)
            {
                var slot = ((from + k) % CsBotPlans.Slots + CsBotPlans.Slots) % CsBotPlans.Slots;
                if (slot >= 0 && slot < CsBotPlans.Slots && taken[slot])
                {
                    continue;   // 该家族有**活着的**队友在走 ⇒ ⛔ 不抢（死人的家族算空出来的，可以被接手）
                }

                var plan = CsBotPlans.For(self.Team, slot, _match.RoundNumber);
                var routeMarker = plan.RouteMarker;
                if (string.IsNullOrEmpty(routeMarker)) continue;
                if (k < CsBotPlans.Slots && routeMarker == _planRoute) continue;   // 刚走完的那条最后再试

                var routePts = _map.Points(routeMarker);
                if (routePts == null || routePts.Length == 0)
                {
                    RateWarn("replan.route.empty",
                        $"{_name} 换路线 '{routeMarker}'（槽位 {plan.Slot}）失败（地图上没有该标记的路点）→ 试下一条");
                    continue;
                }

                // **先只读筛、选定后才切路线**：`TryPickGoalPoint` / `TryPickReachable` 都只用
                // `_nav.CanReach`（"问一句"这一点能不能走得到），与 nav 当前装的是**哪条**路线无关 ⇒
                // 可以在切路线之前就把目标点定下来；筛不出就 `continue`，nav 不会被留在
                // **没被选中**的家族上（否则要靠 `RestoreRouteMarker` 兜回来，那次恢复是
                // "按当前位置最近邻重排 + 游标归零"，属白付的代价）。
                //
                //   不直接用 `_nav.RouteEnd` / 裸包点标记点：这两者都可能是"可走但走不到"的格。
                //   （这里**不传** `avoidTeammates`：本档挑的是"没有活队友在走的空家族"，
                //    取点不存在与队友撞同一点的问题 —— 需要排除队友占位的是下面"抢家族"的两档。）
                var goal = Vector3.zero;
                var isSite = false;

                if (!string.IsNullOrEmpty(plan.GoalMarker) &&
                    TryPickGoalPoint(self, plan.GoalMarker, plan.GoalOrdinal, out var site))
                {
                    goal = site;
                    isSite = plan.GoalIsSite;
                }
                else if (TryPickReachable(self, routePts, 0f, false, out var end))
                {
                    goal = end;
                }
                else
                {
                    RateWarn("replan.route.unreachable." + routeMarker,
                        $"{_name} 换路线 '{routeMarker}' 的目标点全判为走不到" +
                        $"（目标标记 '{plan.GoalMarker ?? "无"}' 与该路线的标记点都没过 BotNavigator.CanReach）→ 试下一条");
                    continue;
                }

                _nav.SetRouteKeepProgress(_map, routeMarker, self.Position);
                if (!_nav.HasRoute)
                {
                    // 这里**不必**恢复 nav：本分支必然 `continue`，而后面无论"某一条选中"（会在 return 前
                    // 再切一次路线）还是"全都没选中"（末尾统一 `RestoreRouteMarker`）都会把状态收干净 ——
                    // 每多调一次恢复就多跑一次 `SetRoute`（内含逐点 A* 的 DropUnreachableWaypoints）。
                    // key 与上面的"前置判空"分开，避免两件事共用同一份降频计数。
                    RateWarn("replan.route.empty.nav",
                        $"{_name} 换路线 '{routeMarker}'（槽位 {plan.Slot}）后 nav 仍没有路线（标记异常）→ 试下一条");
                    continue;
                }

                _planSlot = plan.Slot;
                ApplyObjective(self, routeMarker, goal, isSite,
                    $"换一条路线（槽位 {plan.Slot}={plan.RouteLabel}→{plan.GoalLabel}#{plan.GoalOrdinal}；{reason}）", now);
                return true;
            }

            // ---- 退一档：4 条家族都被**活着的**队友占着（全队都活着 = 常态）⇒ 不抢队友的路，
            //      留在自己的槽位、只把**目标点**换成同一条路线上另一个点（序号也要与队友互斥）。----
            //
            var mySlot = from;
            var myPlan = CsBotPlans.For(self.Team, mySlot, _match.RoundNumber);
            if (!string.IsNullOrEmpty(myPlan.RouteMarker))
            {
                // 与第一档同形：**先只读筛（不碰 nav）、选定后才切路线**。本档筛的全是只读判据
                // （`GoalCandidateCount` / `NextFreeGoalOrdinal` / `TryPickGoalPoint` / `TryPickReachable`，
                // 后者只用 `_nav.CanReach`），与 nav 当前装的是哪条路线无关。
                var myRoutePts = _map.Points(myPlan.RouteMarker);
                if (myRoutePts != null && myRoutePts.Length > 0)
                {
                    var count = GoalCandidateCount(myPlan.GoalMarker);
                    var goal = Vector3.zero;
                    var picked = false;
                    var ordinal = myPlan.GoalOrdinal;

                    for (var d = 0; d < count; d++)
                    {
                        var o = CsBotPlans.NextFreeGoalOrdinal(_match.Actors, self.Team, carrier,
                            _match.RoundNumber, self.Id, myPlan.GoalMarker, ordinal, count);
                        if (o < 0) break;                       // 该标记上的序号被队友占满了
                        if (o == ordinal) break;                // 没有下一个（count=1 之类）
                        ordinal = o;
                        if (TryPickGoalPoint(self, myPlan.GoalMarker, ordinal, out var pt))
                        {
                            goal = pt;
                            picked = true;
                            break;
                        }
                    }

                    if (!picked && TryPickReachable(self, myRoutePts, 0f, false,
                                                    out var end, avoidTeammates: true))
                    {
                        goal = end;
                        picked = true;
                        RateWarn("replan.inslot.gofallback",
                            $"{_name} 留在槽位 {mySlot}（'{myPlan.RouteLabel}'）但目标标记 '{myPlan.GoalMarker}' " +
                            "上的序号都被占/走不到 → 退化为该路线上离自己最近的走得到的路点");
                    }

                    if (picked)
                    {
                        // 选定之后才真正切路线（此前一律不碰 nav）
                        _nav.SetRouteKeepProgress(_map, myPlan.RouteMarker, self.Position);
                        if (!_nav.HasRoute)
                        {
                            // 极罕见（`myRoutePts` 非空 + 地图已加载时 `SetRoute` 必留下路点 ⇒ `HasRoute` 必真）。
                            // 处理方式：撤销"已选中"并**继续落到下面的兜底档**（改前落到兜底档，现在保持一致），
                            // 而不是直接 `return false` 把这一帧的机会丢掉。
                            RateWarn("replan.inslot.nav",
                                $"{_name} 选定留在槽位 {mySlot}（'{myPlan.RouteLabel}'）后 nav 仍没有路线" +
                                "（标记异常）→ 放弃本档，继续试兜底档");
                            RestoreRouteMarker(prevMarker, self);
                            picked = false;
                        }
                    }

                    if (picked)
                    {
                        _planSlot = mySlot;
                        ApplyObjective(self, myPlan.RouteMarker, goal, myPlan.GoalIsSite,
                            $"无空路线（4 条都被**活着的**队友占着）⇒ 留在槽位 {mySlot}={myPlan.RouteLabel}，" +
                            $"只换目标点（{myPlan.GoalLabel}#{ordinal}；{reason}）", now);
                        return true;
                    }
                }
            }

            // ---- 最后一档兜底（**本项目新增**）：走到这里说明 ① 没有空家族（4 条都被活着的队友占着），
            //      ② 连自己槽位的"换目标点"也换不出来（目标标记上的序号被占满，且本路线没有任何
            //      走得到的标记点 —— TryPickReachable 全失败）。
            //
            //      直接 `return false` ⇒ 上层拿不到新目标 ⇒ 只能继续朝那个**走不到**的点直线走 ⇒
            //      被几何卡住就是永久 0.00m。
            //
            //      这一档允许**点开一条被活队友占着的家族**，且只取"离自己最近的、走得到的路点"
            //      （序号互斥仍在，不抢队友的目标点）—— 宁可跟队友同路，也不许原地卡死。
            //      （原版 bot 同样允许与队友走同一条路；出处：本项目新增，导航自恢复的最后一档。）
            //
            //      起点**必须与本 bot 的 `_planSlot` 无关**（这里用"队内序号算出的槽位"）：
            //      ① 若一律从 slot 0 扫起，全队候选顺序完全相同（只有"刚走过的那条"会跳过）⇒ 同时卡住的
            //         几只都会在第一轮选中同一个家族，必然撞车（并触发同队路线撞车审计）；
            //      ② 若从 `mySlot = _planSlot` 起推也不行 —— **兜底成功后 `_planSlot` 会被写成抢到的那个
            //         槽位**（= 真主人的槽位），于是抢家者与被抢者的起点序列**完全相同** ⇒ 下一次必然再次
            //         选中同一家族（自我固化、撞车周期性复发）。`SlotOf(_ordinal, carrier)` 只依赖自己
            //         的队内序号，抢家者与被抢者的 `_ordinal` 必不同 ⇒ 起点必不同。
            var startSlot = CsBotPlans.SlotOf(_ordinal, carrier,
                CsBotPlans.RoundBotCount(_match.Actors, self.Team, _match.RoundNumber));
            for (var i = 0; i < CsBotPlans.Slots; i++)
            {
                var slot = ((startSlot + 1 + i) % CsBotPlans.Slots + CsBotPlans.Slots) % CsBotPlans.Slots;
                var plan = CsBotPlans.For(self.Team, slot, _match.RoundNumber);
                var marker = plan.RouteMarker;
                if (string.IsNullOrEmpty(marker) || marker == _planRoute) continue;   // 刚走完的那条最后再试

                // **先只读筛**（不碰 nav）：`TryPickReachable` 只"问一句"这个家族有没有走得到的路点
                // （内含一次 A*），不改 nav 的路线。边扫边 `SetRouteKeepProgress` 不行：任何一次
                // continue 都会把 nav 的路线留在**没被选中**的那个家族上，而 `_planRoute`/`_goalPos`
                // 还是旧的 ⇒ 下一帧 Advance 会沿一条自己没选的路线走格子。
                // 本函数的三档（第一档换家族 / 退一档换目标点 / 本档抢家族）**统一是这个形状**，
                // 所以 `RestoreRouteMarker` 的恢复分支实际只剩"进来时本来没路线"那条路径会走到。
                var points = _map.Points(marker);
                if (points == null || points.Length == 0) continue;
                if (!TryPickReachable(self, points, 0f, false, out var end, avoidTeammates: true)) continue;

                // 选定之后才真正切路线；此后不再 continue，nav 与 `_planRoute` 一致地换到这一条。
                _nav.SetRouteKeepProgress(_map, marker, self.Position);
                if (!_nav.HasRoute)
                {
                    RateWarn("replan.route.grab.nav",
                        $"{_name} 兜底选中家族 '{plan.RouteLabel}' 后 nav 仍没有路线（标记异常）→ 放弃本档");
                    RestoreRouteMarker(prevMarker, self);   // 早退同样要对齐 nav（否则停在没选中的家族上）
                    return false;
                }

                _planSlot = plan.Slot;
                RateWarn("replan.route.grab",
                    $"{_name} 无空路线、且本槽位换不出目标点 ⇒ 兜底点开被队友占着的家族 " +
                    $"'{plan.RouteLabel}'（槽位 {plan.Slot}），只取离自己最近的可走路点（{reason}）");
                ApplyObjective(self, marker, end, false,
                    $"兜底：点开被占家族 {plan.RouteLabel}（槽位 {plan.Slot}）= 最近可走路点；{reason}", now);
                return true;
            }

            // 全部档都没选中 ⇒ nav 不许留下任何"筛选时的痕迹"（见函数开头 prevMarker 的说明）。
            RestoreRouteMarker(prevMarker, self);

            return false;
        }

        /// <summary>
        /// 把 nav 的路线标记对齐回 <paramref name="prevMarker"/> —— "没选中任何家族 ⇒ 不改变 nav 状态"。
        ///
        /// <para>两种形态都要处理：① 进来时本来没路线（<c>null</c>）⇒ 必须清掉中途被装上的那条
        /// （否则 nav 停在自己没选的家族上，下一帧 <c>Advance</c>/<c>ResolveNextTarget</c> 会沿它走格子）；
        /// ② 进来时有路线 ⇒ 恢复那条 —— 注意 <see cref="BotNavigator.SetRouteKeepProgress"/> 内部是
        /// "按当前位置最近邻重排"，恢复的是**标记**、游标会归零（比"沿一条自己没选的路线走"轻，可接受）。</para>
        ///
        /// <para><b>代价（已知，登记在此，别当 bug 查）</b>：<c>SetRoute</c> 内部会
        /// <c>ClearEscape()</c> + <c>ClearStuckState()</c> + <c>InvalidatePath()</c> ⇒
        /// "兜底全失败 ⇒ 恢复"这一帧会把**刚攒够的卡住判据清掉**，下一次换目标/逃逸要再等一个
        /// <c>StuckCheckInterval</c>(0.5s)；且重排取"离当前位置最近的标记点"，而巡逻家族是**环线** ⇒
        /// 那个点可能是**已经走过**的一个（表现为走一小段回头路，一个路点间距，不是 U 形掉头）。
        /// ⇒ ⛔ 不要把 <c>StuckWarn</c> 的计数当成"卡住"的性能指标（它会被这里清掉一部分）。</para>
        /// </summary>
        private void RestoreRouteMarker(string prevMarker, CsActor self)
        {
            if (_nav == null) return;

            if (prevMarker == null)
            {
                // **无条件清**（不能只在 `HasRoute` 时清）："兜底选中后 nav 仍没有路线"那条早退路径上
                // `_route` 本来就是空的（`HasRoute` 为假），但 `SetRoute` 已经把 `_marker` 设成了新标记
                // ⇒ 只判 `HasRoute` 会留下"标记是刚抢的家族、路点却是空"的**假状态**：
                // 行为上无害（后续 `SetRouteKeepProgress` 要求 `_route.Count > 0` ⇒ 必走重建），
                // 但读 `_nav.RouteMarker` 的探针/审计会把"没选中"读成"选中了那个家族"。
                _nav.ClearRoute();
                return;
            }

            if (_nav.RouteMarker != prevMarker) _nav.SetRouteKeepProgress(_map, prevMarker, self.Position);
        }

        /// <summary>
        /// 某个目标标记上**实际可用**的点数（<c>CanStand</c> 过滤后）—— 给
        /// <see cref="CsBotPlans.NextFreeGoalOrdinal"/> 当 <c>candidateCount</c>。
        /// <para>与 <see cref="TryPickGoalPoint"/> 用的是**同一条过滤**（<c>CanStand</c>）；两者若漂移，
        /// 序号与点就对应不上，所以这里刻意只留一处过滤条件、并在 <c>TryPickGoalPoint</c> 注释里互指。</para>
        /// </summary>
        private int GoalCandidateCount(string marker)
        {
            if (_map == null || !_map.IsLoaded || string.IsNullOrEmpty(marker)) return 0;

            var pts = _map.Points(marker);
            if (pts == null || pts.Length == 0) return 0;

            var n = 0;
            for (var i = 0; i < pts.Length; i++)
                if (_map.CanStand(pts[i])) n++;

            return n;
        }

        /// <summary>
        /// "去巡逻"候选：走 <see cref="CsMarkers.Patrol"/>，目标 = **走得到且离自己最近**的那个巡点
        /// （距离 ≥ <see cref="CsBotConst.MinPatrolDistance"/>）。
        ///
        /// </summary>
        private bool TryPatrolObjective(CsActor self, string reason, float now)
        {
            // 进入本函数时 nav 装的是哪条路线（与 `TryRouteObjective` 同口径）：本函数会切到
            // `Route_Patrol`，若之后没能选中目标就必须恢复 —— 否则 `ReplanObjective` 紧接着调
            // `TryRouteObjective` 时，它开头的 `prevMarker` 会抓到 `Route_Patrol` 这个残留，
            // 而那个残留又可能被它末尾"恢复/固化"成合法状态（同一形状的传播链，见那里注释）。
            var prevMarker = _nav.RouteMarker;

            var pts = _map.Points(CsMarkers.Patrol);
            if (pts == null || pts.Length == 0)
            {
                RateWarn("replan.patrol.empty",
                    $"{_name} 想巡逻但地图上没有 '{CsMarkers.Patrol}' 标记（Points 返回空）→ 试其它目标");
                return false;
            }

            //   "换目标"不许直接跳进巡逻线：那会让槽位 2 的主人（本来就该走巡逻）与这只 bot
            //   同时走 `Route_Patrol`（同队路线撞车，用户看到的就是这个）。
            //   活着的队友在走 ⇒ 不抢；死人的家族算空出来的（可以被接手）。
            if (_planRoute != CsMarkers.Patrol)
            {
                var patrolCarrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
                if (CsBotPlans.FamilyHeldByAliveTeammate(_match.Actors, self.Team, patrolCarrier,
                        _match.RoundNumber, self.Id, CsMarkers.Patrol))
                {
                    Game.Logger.Info(Tag,
                        $"{_name}（{self.Team}/{_difficulty}）本想去巡逻，但巡逻线已被一只**活着的**队友占着" +
                        $"→ 不抢（互斥优先于换目标；{reason}）");
                    return false;
                }
            }

            // 与 `TryRouteObjective` 同形：**先只读筛、选定后才切路线**。
            // 先 `SetRouteKeepProgress(Patrol)` 再筛不行：两次 `TryPickReachable` 都失败就 `return false`
            // ⇒ nav 停在 `Route_Patrol` 而 brain 的 `_planRoute` 还是旧家族，且会
            // **跨函数传播**：`ReplanObjective` 紧接着调 `TryRouteObjective`，后者开头抓到的 `prevMarker`
            // 就是这个残留，一旦它最终没选中，末尾会把残留**恢复/固化**成"合法状态"
            // （于是"没选中 ⇒ 不改 nav"这条不变量在这一整条链上都不成立）。
            // （`TryPickReachable` 只用 `_nav.CanReach`，与 nav 当前装的是哪条路线无关 ⇒ 可以先筛。）

            // 近的优先（不再"专挑最远"）：门口那个走得到的巡点，比 80m 外走不到的那个有用。
            if (!TryPickReachable(self, pts, CsBotConst.MinPatrolDistance, false, out var goal))
            {
                // 退化一层：去掉最小距离限制再问一次（"最近的走得到的点"口径），并把"为什么退化"写进日志
                if (!TryPickReachable(self, pts, 0f, false, out goal))
                {
                    RateWarn("replan.patrol.unreachable",
                        $"{_name} 的巡逻点**没有一个走得到**（'{CsMarkers.Patrol}' 标记点全没过 " +
                        $"BotNavigator.CanReach：位图可走但高度一致性层到不了 / 或真孤岛）→ 试其它目标");
                    return false;   // ⛔ 这条路径**不碰 nav**（残留问题见上）
                }

                RateWarn("replan.patrol.nofar",
                    $"{_name} 的巡逻点里没有「≥ {CsBotConst.MinPatrolDistance:F0}m 且走得到」的点 → " +
                    $"退化为最近的那个走得到的巡点 {goal}");
            }

            _nav.SetRouteKeepProgress(_map, CsMarkers.Patrol, self.Position);
            if (!_nav.HasRoute)
            {
                // 极罕见（`pts` 已在 :1125 判过非空 ⇒ `SetRoute` 必留下路点 ⇒ `HasRoute` 必真）。
                // 仍按"没选中 ⇒ 不改 nav"处理：恢复进来时的那条再返回，别把 `Route_Patrol` 残留
                // 传给紧接着调用的 `TryRouteObjective`。
                RateWarn("replan.patrol.nav",
                    $"{_name} 切到 '{CsMarkers.Patrol}' 后 nav 仍没有路线（标记异常）→ 放弃本档并恢复原路线");
                RestoreRouteMarker(prevMarker, self);
                return false;
            }

            ApplyObjective(self, CsMarkers.Patrol, goal, false, $"去巡逻（{reason}）", now);
            return true;
        }

        private bool TrySpawnObjective(CsActor self, string reason, float now)
        {
            var marker = self.Team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT;
            var pts = _map.Points(marker);
            if (pts == null || !TryPickReachable(self, pts, 0f, false, out var point))
            {
                RateWarn("replan.spawn.none",
                    $"{_name} 本方出生点 '{marker}' 取不到**走得到**的点（CanReach 全部 false）→ 无法重新选目标");
                return false;
            }

            _nav.ClearRoute();
            ApplyObjective(self, marker, point, false, $"回出生点（{reason}）", now);
            return true;
        }

        /// <summary>
        /// 兜底目标：路线 / 巡逻 / 出生点**全部**没过 <see cref="BotNavigator.CanReach"/> 时的最后一条出路 ——
        /// 在这三组标记点里取"站得住（<c>CanStand</c>）且离自己最近"的一个当目标，**不再问可达性**。
        ///
        /// <para><b>为什么必须兜这一层</b>：可达性判据建立在位图上，而位图是**单层 2D**（多层几何上下重叠），
        /// 它会把一些物理上走得通的地方判成走不到。全 false 时的既有行为是"原地警戒"
        /// （本帧 <c>intent.Move = zero</c>）—— 机器人剩下整回合一动不动、净位移 0。</para>
        ///
        /// <para>候选只用**已有的**地图标记点（本回合路线标记 / 巡逻线 / 本方出生点），不新增目标坐标。</para>
        /// </summary>
        private bool TryLastResortObjective(CsActor self, string reason, float now)
        {
            if (_map == null || !_map.IsLoaded) return false;

            var markers = new[]
            {
                _planRoute,
                CsMarkers.Patrol,
                self.Team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT,
            };

            var bestDist = -1f;
            var bestMarker = (string)null;
            var bestPoint = Vector3.zero;
            var heldSkipped = 0;

            // 兜底**不是**互斥门禁的例外：3 个候选里的 `_planRoute` / `CsMarkers.Patrol` 都是**分工家族**，
            //   活着的队友正走着就不许抢（与 TryRouteObjective 的槽位表、TryPatrolObjective 的家族门禁同一条判据）。
            //   不加这一道，兜底会把一只 bot 放进队友的家族里 —— 而且它自己的槽位还写着旧家族
            //   （实机形态：`同队路线撞车：Cliffe(CT/槽位2) 与 Scuzzy(CT/槽位3) 都走 'Route_Patrol'`）。
            var carrierOrdinal = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);

            for (var i = 0; i < markers.Length; i++)
            {
                var marker = markers[i];
                if (string.IsNullOrEmpty(marker)) continue;

                if (CsBotPlans.SlotOwningRoute(self.Team, marker, _match.RoundNumber) >= 0 &&
                    CsBotPlans.FamilyHeldByAliveTeammate(_match.Actors, self.Team, carrierOrdinal,
                        _match.RoundNumber, self.Id, marker))
                {
                    heldSkipped++;
                    continue;
                }

                var pts = _map.Points(marker);
                if (pts == null) continue;

                for (var k = 0; k < pts.Length; k++)
                {
                    if (!_map.CanStand(pts[k])) continue;

                    var d = pts[k] - self.Position;
                    d.y = 0f;
                    var dist = d.magnitude;
                    if (bestDist >= 0f && dist >= bestDist) continue;

                    bestDist = dist;
                    bestMarker = marker;
                    bestPoint = pts[k];
                }
            }

            if (bestDist < 0f)
            {
                if (heldSkipped > 0)
                {
                    RateWarn("replan.lastresort.held",
                        $"{_name} 兜底目标：3 个候选标记里 {heldSkipped} 个是**活着的**队友正走着的分工家族 ⇒ " +
                        $"不抢（互斥优先于兜底：本帧保留上一个目标，宁可不动也不与队友同路；{reason}）");
                }
                return false;
            }

            _nav.ClearRoute();
            ApplyObjective(self, bestMarker, bestPoint, false,
                $"可达性判据对路线 / 巡逻 / 出生点**全部**为 false ⇒ 兜底取标记 '{bestMarker}' 上站得住且离自己" +
                $"最近的点（{bestDist:F1}m，不问可达性；{reason}）", now);
            return true;
        }

        /// <summary>把新目标落进 brain 并打一条 Info（换目标是低频事件，不降频）。</summary>
        private void ApplyObjective(CsActor self, string routeMarker, Vector3 goal, bool isSite, string why, float now)
        {
            _planRoute = routeMarker;

            // 「槽位 ↔ 路线」必须一一对应：路线家族换了就跟着换槽位（判据见 CsBotPlans.SlotOwningRoute）。
            //   只在**分工家族**上做（出生点 / 守包这类集体目标返回 -1，槽位保持不动）。
            //   不做这一步的后果：兜底路径（TryLastResortObjective）把 bot 放进队友正走着的家族，
            //   而它的槽位还写着别的家族 ⇒ 槽位表 / 互斥门禁 / 撞车审计三处读到的都是错的。
            var owner = CsBotPlans.SlotOwningRoute(self.Team, routeMarker, _match.RoundNumber);
            if (owner >= 0 && owner != _planSlot)
            {
                Game.Logger.Info(Tag,
                    $"{_name}（{self.Team}）路线换成 '{routeMarker}'（家族拥有者 = 槽位 {owner}）" +
                    $"⇒ 槽位 {_planSlot} → {owner}（保持「槽位 ↔ 路线」一一对应）");
                _planSlot = owner;
            }

            _goalPos = goal;
            _goalValid = true;
            _goalIsSite = isSite;
            ArmGoal(self, now);

            Game.Logger.Info(Tag,
                $"{_name}（{self.Team}/{_difficulty}）重新选目标：{why} → 槽位={_planSlot} 路线={_planRoute}" +
                $" 首段路点={(HasFirstWaypoint ? FirstWaypoint.ToString() : "无")} " +
                $"目标={_goalPos} 站点={_goalIsSite} 路点={_nav.RemainingWaypoints} " +
                $"守点时长={ObjectiveHoldSeconds():F1}s（第 {_replanCount} 次换目标）");
        }

        //   路线/目标一律由 `CsBotPlans.For(team, slot, round)` 这张**4 槽位表**给（含巡逻线，4 条路互不相同），
        //   换目标走 `TryRouteObjective` 的槽位轮转。按 `slot` 0/1/2 映射到 3 条路（slot 2 = 中路、
        //   其余 = 两个包点）就是"每队必有一对 bot 同路同点"的直接来源（T: 槽位 0 与 1 都落主攻路；
        //   CT 由 `idx % 3` 使槽位 0 与 3 同路）。
        //   需要改路线选择时改 `CsBotPlans`，不要在这里复活三分支映射。

        /// <summary>
        /// 在候选点里挑第一个**走得到**的点（判据见下）。
        ///
        /// <para><b>为什么必须有这一层</b>：只判"可走"（<see cref="ICsMap.WalkableAt"/> /
        /// <see cref="ICsMap.CanStand"/>）不够 —— "可走"与"走得到"是两件事，只判前者就等于"挑了一个
        /// 求不出路径的点去追"，而 <c>EnsurePath</c> 失败后只会退化成直线走 ⇒ 顶着墙卡死。</para>
        ///
        /// <para><b>判据同源</b>：可达性只走 <see cref="BotNavigator.CanReach"/>（= 同一份
        /// <c>SnapToWalkable</c> + <c>WalkableCellHeightAware</c> + 引擎 <c>AStar.Find</c>），
        /// 不另写一套连通性算法（否则判据与被判对象会漂移）。</para>
        ///
        /// <para><b>调用顺序省算力</b>：先按距离过滤（&lt; minDistance / 不比自己好）**再**问可达 ——
        /// 于是越问越近，每轮最多问到的点数是"当前最优被刷新"的次数，且 <c>CanReach</c> 的
        /// 高度扩张按起点格缓存，同一次换目标里只算一次。</para>
        /// </summary>
        /// <param name="pts">候选点（地图标记点；null / 空 ⇒ false）</param>
        /// <param name="minDistance">水平距离下限（米）</param>
        /// <param name="farthest">false = 取"走得到的里面最近的"；true = 取最远的</param>
        /// <param name="avoidTeammates">true = 跳过"已被同队活体站着的点"（见 <see cref="TeammateOccupies"/>）。
        /// 默认 false ⇒ 既有调用点行为逐字不变；只有"最后一档兜底 / 退一档 fallback"这类
        /// **可能两只同时选点**的路径才传 true。</param>
        private bool TryPickReachable(CsActor self, Vector3[] pts, float minDistance, bool farthest,
                                      out Vector3 point, bool avoidTeammates = false)
        {
            point = Vector3.zero;
            if (pts == null || pts.Length == 0) return false;
            if (_map == null || !_map.IsLoaded) return false;

            var best = -1f;
            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.CanStand(pts[i])) continue;                  // 层①：本格站得住（与路线过滤同一口径）
                if (IsBannedGoal(pts[i])) continue;                    // 层①'：已判定走不到（记忆期内跳过）
                if (avoidTeammates && TeammateOccupies(self, pts[i])) continue;   // 层①''：队友正站在那儿

                var d = pts[i] - self.Position;
                d.y = 0f;
                var dist = d.magnitude;
                if (dist < minDistance) continue;
                if (best >= 0f && (farthest ? dist <= best : dist >= best)) continue;   // 已有个更好的候选，省掉这次可达查询
                if (!_nav.CanReach(self.Position, pts[i])) continue;   // 层②：真的走得到（口径与运行时同一份）

                best = dist;
                point = pts[i];
            }

            return best >= 0f;
        }

        /// <summary>
        /// 该点是否被**同队活体**占着 —— 只给"可能两只同时选点"的路径用（见 <see cref="TryPickReachable"/>）。
        ///
        /// <para><b>为什么需要</b>：路线标记点**没有序号互斥表**（<c>CsBotPlans.NextFreeGoalOrdinal</c>
        /// 只覆盖"目标标记"的计划序号），所以两只卡在同一角落时会选中**同一个最近路点**，然后互相挤开
        /// （<c>CsMatch.SeparateFromOtherActors</c>）并被 BotModule 记一对"路线撞车"。这里用"排除队友
        /// 已经站着的点"作为最小互斥 —— 不上序号表（那是给目标标记用的，路线标记点没有序号）。</para>
        ///
        /// <para><b>同层的口径用 <see cref="CsConst.StepUpHeight"/>(0.457) 而不是
        /// <see cref="CsConst.StandHeight"/>(1.80)</b>：后者在"队友站在正上一层、层高恰 1.8m"时
        /// 算出 <c>|Δy| = 1.8</c>、而判据是 <c>&gt; 1.8</c> ⇒ 会**判成同层**并误排该候选点；本图多层
        /// 上下重叠是常态、两个标记点垂直对齐（水平 &lt; 0.64m）并不罕见 ⇒ 一旦该家族只剩这一个候选，
        /// 就会被跳过（防抢点反而造出"原地不动"，比两只挤一下更糟）。`StepUpHeight` 的语义正是
        /// "同一落脚层"（抬腿带）：跨层最小落差 1.8m ≫ 0.457 ⇒ 不会误排，而同层的软地板/坡面误差
        /// （0.05~0.3m）仍会被正确排开。</para>
        /// </summary>
        private bool TeammateOccupies(CsActor self, Vector3 point)
        {
            var actors = _match.Actors;
            if (actors == null) return false;

            var minDist = CsConst.PlayerRadius * 2f;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == self.Id || a.Team != self.Team || !a.IsAlive) continue;

                var d = a.Position - point;
                d.y = 0f;
                if (d.magnitude >= minDist) continue;
                if (Mathf.Abs(a.Position.y - point.y) > CsConst.StepUpHeight) continue;
                return true;
            }
            return false;
        }

        /// <summary>取某个标记里"走得到且离自己最近"的一个点（包点/出生点用）。取不到时打一条带数字的 Warn。</summary>
        private bool TryPickReachablePoint(CsActor self, string marker, out Vector3 point)
        {
            point = Vector3.zero;
            if (_map == null || !_map.IsLoaded) return false;

            var pts = _map.Points(marker);
            if (pts == null || pts.Length == 0) return false;
            if (TryPickReachable(self, pts, 0f, false, out point)) return true;

            RateWarn("reach.marker." + marker,
                $"{_name} 的标记 '{marker}' 有 {pts.Length} 个点，**没有一个走得到**" +
                $"（CanStand 过了但 BotNavigator.CanReach 全 false）→ 换别的目标。" +
                "判据：tools/probes/bu-goal-reachability.py");
            return false;
        }

        /// <summary>取某条路线的标记里"走得到且离自己最远"的那个点当路线终点（加门禁后的 RouteEnd）。</summary>
        private bool TryPickReachableRouteEnd(CsActor self, string routeMarker, out Vector3 point)
            => TryPickReachable(self, _map != null && _map.IsLoaded ? _map.Points(routeMarker) : null,
                0f, true, out point);

        /// <summary>取"离 <paramref name="from"/> 最远且可走"的一个标记点（<paramref name="minDistance"/> 以内的不要）。
        /// 为什么要可走：目标点本身在墙里 = 永远走不到 —— 顶着墙只会把卡住日志刷到天荒地老。
        /// </summary>
        private bool TryPickFarthestWalkable(Vector3[] pts, Vector3 from, float minDistance, out Vector3 point)
        {
            point = Vector3.zero;
            var best = -1f;

            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.WalkableAt(pts[i].x, pts[i].z)) continue;

                var d = pts[i] - from;
                d.y = 0f;
                var dist = d.magnitude;
                if (dist < minDistance) continue;
                if (dist <= best) continue;

                best = dist;
                point = pts[i];
            }

            return best >= 0f;
        }

        /// <summary>
        /// 按难度 <see cref="CsBotProfile.RepathInterval"/> 检查"朝目标有没有进展"：没进展就**用当前位置重排路线**
        /// （真正意义上的重新寻路）。只在"路线还有剩余路点"时重排 —— 路线已走完时重排等于掉头。
        /// 间隔 = Easy 1.6s / Normal 1.0s / Hard 0.5s：低难度反应慢，撞上障碍后愣得久。
        /// </summary>
        private void MaybeRepath(CsActor self, float now)
        {
            if (now < _nextRepathAt) return;
            _nextRepathAt = now + Mathf.Max(0.25f, _profile.RepathInterval);

            var d = _goalPos - self.Position;
            d.y = 0f;
            var dist = d.magnitude;
            var progress = _lastGoalDist - dist;
            _lastGoalDist = dist;

            if (progress >= CsBotConst.RepathProgressEpsilon)
            {
                _stallCount = 0;                                        // 有进展 → 什么都不做
                return;
            }

            // 连续这么多轮都"原地打转"才重排一次路线：偶尔一次慢（上坡/绕箱）不该触发重排，
            // 否则会把已经走顺的路线反复重排成"回头找最近路点"，看起来就是来回蹭。
            if (++_stallCount < CsBotConst.RepathStallStreak) return;
            _stallCount = 0;

            if (!_nav.HasRoute || _nav.RouteExhausted) return;          // 路线走完 → 交给"换目标"那条路
            if (string.IsNullOrEmpty(_planRoute)) return;

            // 到**最终目标**的直线距离没缩短，不代表没在走：绕建筑/上坡时它在沿路径前进。
            // 这时重求路径会用当前格重跑 A*，等价代价的两条分支会互换 ⇒ 按新路径掉头
            // （实测形态：同一路点上来回拉锯几米、整回合进不去包点）。
            if (_nav.PathHasProgress(self.Position)) return;

            var remainingBefore = _nav.RemainingWaypoints;              // 先取值：重求路径之后剩余路点会按新路径重算
            //   不能调 `SetRoute`：它会按"离**当前位置**最近优先"重排整条路线并把 `_index` 归零，
            //   于是"最近的路点"（很可能是刚走过、在身后的那个）排到最前 ⇒ 机器人掉头，
            //   与"原地重求路径、顺序与游标不动"这条契约相反。
            _nav.RefreshPathOnly(self.Position);
            RateWarn("repath." + _planRoute,
                $"{_name} 在 {_profile.RepathInterval:F2}s 内朝目标没有进展（距目标 {dist:F1}m，路线 '{_planRoute}'，" +
                $"重排前剩余路点 {remainingBefore}）→ 用当前位置**原地重求路径**（顺序与游标不动，⛔ 不重排路线）");
        }

        // ==================================================================
        //  冻结期（买枪 + 转头）
        // ==================================================================
        private CsBotIntent ThinkFreeze(CsActor self, CsBotIntent intent, float now)
        {
            _buy.Tick(_match, self, _profile, _role, now, out var switchTo);

            SetState(CsBotState.Idle, now);
            intent.State = CsBotState.Idle;
            intent.SwitchTo = switchTo;
            intent.Move = Vector3.zero;      // 冻结期模拟本来也不让动，这里显式表达"我不动"
            intent.Fire = false;
            intent.Use = false;
            intent.Crouch = false;
            intent.AimPoint = _goalValid ? WithEyeHeight(_goalPos) : LookAhead(self);
            return intent;
        }

        // ==================================================================
        //  回合进行中
        // ==================================================================
        private CsBotIntent ThinkLive(CsActor self, float dt, float now)
        {
            var intent = new CsBotIntent { State = CsBotState.Idle, Move = Vector3.zero };

            UpdatePerception(self, now);

            // ⓪ 上一 tick 导航报了"卡住" → 连续卡住就换目标（放在最前面，好让本 tick 的决策直接用上新目标）
            HandleStuck(self, now);

            // ⓪.5 投掷物（角色分工）：该用的时候先扔一颗 —— 扔雷要"切雷 → 转视角 → 扣扳机"，
            //     飞行 / 引信 / 伤害全部由模拟结算（CsInventory.ThrowGrenade），这里只提交意图。
            if (TryThrowGrenade(self, ref intent, now)) return intent;

            // ① 提交顶层战术决策（守卫见 CommitTacticalDecision），本 tick 的分支**由状态机给**：
            //    Engage → 交战；Objective → 炸弹任务；Patrol → 推进。
            var branch = CommitTacticalDecision(self, now);

            // ② 正在下包/拆包：没人贴脸就不打断（一移动进度就清零，与模拟的判定一致）。
            //    _closestVisibleDist 在"看不见敌人"时是 float.MaxValue，所以这一条同时覆盖两种情况。
            if (branch != CsBotFsmStates.Engage && self.UseProgress >= 0f &&
                _closestVisibleDist > CsBotConst.EnemyTooCloseRange)
            {
                return ContinueUse(self, intent, now);
            }

            // ③ 交战分支：目标还在记忆期内，且目标仍活着 → 进 Engage；已死/已离场则立刻忘掉它，
            //    本 tick 直接落到炸弹任务（不空等一个 tick）
            if (branch == CsBotFsmStates.Engage)
            {
                var target = _match.Find(_targetId);
                if (target != null && target.IsAlive) return Engage(self, intent, now);

                _targetId = 0;
            }

            // ④ 炸弹任务（下包 / 拆包 / 捡包）。**两条分支都要走这一步**：T 手里没包时"去捡掉落的 C4"
            //    属于炸弹任务而不是推进，只在 Objective 分支里调用会让捡包整条失效。
            //
            //   `ref intent` 是契约：本方法写的 `Move` / `State` / `Use` 必须落到调用方那具 intent 上
            //   （只写副本 ⇒ 调用方拿到的是"不动"）。
            if (TryBombObjective(self, ref intent, now)) return intent;

            // ⑤ 推进 / 守点
            return Advance(self, intent, now);
        }

        /// <summary>进行中的下包/拆包：保持按住（下包 = 左键，拆包 = E）、不移动（贴脸敌人时由调用方放行去交战）。</summary>
        private CsBotIntent ContinueUse(CsActor self, CsBotIntent intent, float now)
        {
            var planting = self.Team == CsTeam.T && self.HasBomb;
            var state = planting ? CsBotState.Plant : CsBotState.Defuse;

            SetState(state, now);
            intent.State = state;
            intent.Move = Vector3.zero;
            intent.Use = !planting;
            intent.Fire = planting;
            if (planting) intent.SwitchTo = CsWeapons.C4;

            if (now >= _lastUseLogAt)
            {
                _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                Game.Logger.Info(Tag,
                    $"{_name} 保持{(planting ? "下包（按住左键）" : "拆包（按住 E）")}中（进度 {self.UseProgress:P0}）：不移动");
            }

            intent.AimPoint = planting && self.HasBomb
                ? LookPoint(self, self.EyePosition + Forward(self.Yaw, -12f) * CsBotConst.AimFallbackDistance)
                : (_match.BombPlanted ? WithEyeHeight(_match.BombPosition) : LookAhead(self));
            return intent;
        }

        /// <summary>
        /// "这一 tick 不打架、先去拆包"（只对 CT 且已下包时成立）：
        /// ① 时间不够了（剩余 ≤ 拆包所需 + **走到 C4 的路上用时** + 余量）→ 无条件去拆；
        /// ② 时间还够 → 只有"眼前（<see cref="CsBotConst.DefuseOverFightRange"/> 内）就有敌人"才先打，
        ///    否则一律先冲包点（官方 CT 行为：下包即刻回防）。
        ///
        /// <para>①里必须算路上用时：只算拆包时间时，40m 外的 CT 会一直"先打"到引信只剩 12s 才动身，
        /// 到了包点引信已经烧完（实测：下包后 26s，最近的一名 CT 仍在 46m 外）。路上用时按
        /// <c>CsInventory.MovementSpeed</c>（模拟施加移动用的同一口径）与到 C4 的水平距离算。</para>
        /// </summary>
        private bool MustDefuseFirst(CsActor self, float now)
        {
            if (self.Team != CsTeam.CT || !_match.BombPlanted) return false;

            var need = self.HasDefuser ? CsConst.DefuseTimeWithKit : CsConst.DefuseTime;
            var left = _match.BombTimeLeft;

            var bomb = _match.BombPosition;
            var d = bomb - self.Position;
            d.y = 0f;
            var speed = Mathf.Max(1f, CsInventory.MovementSpeed(self));
            var travel = d.magnitude / speed;

            if (left >= 0f && left <= need + travel + CsBotConst.DefuseUrgencyMargin)
            {
                // 引信只够"跑过去 + 拆"⇒ 不再让远处交火抢走这几秒；这条日志就是"为什么此刻不打了"。
                if (CloverEngine.LogThrottle.ShouldLog("bot.defuse.rush:" + _name, 5f))
                {
                    Game.Logger.Info(Tag,
                        $"{_name} 剩余 {left:F1}s ≤ 拆包 {need:F1}s + 路上 {travel:F1}s（距 C4 {d.magnitude:F1}m，" +
                        $"移动速度 {speed:F2}m/s）+ 余量 {CsBotConst.DefuseUrgencyMargin:F0}s → 放弃交战，直冲 C4");
                }
                return true;
            }

            return !_visibleNow || _closestVisibleDist > CsBotConst.DefuseOverFightRange;
        }

        /// <summary>
        /// "这一 tick 不打架、先下包"（只对 **T 且持包** 成立；与 CT 的 <see cref="MustDefuseFirst"/> 对称）：
        /// 持包者**已经进了包点判定区**（离最近包点标记 ≤ <see cref="CsBotConst.SiteRadius"/>）⇒ 该下包了。
        ///
        /// <para><b>出处</b>：规格 <c>策划/策划案/CS1.6单机参考规格.md</c> §2.4 行为树第 130 行
        /// 「**T 持包到 B 点 → 下包**」—— 进了包点还在交战 = 把"下包"这条回合目标丢掉了。
        /// 判定半径与 <c>TryPlantOrPickup</c> / <c>CsBomb.IsInBombsite</c> **同口径**
        /// （<c>CsBotConst.SiteRadius</c> = <c>Module/Map/ICsMap.cs:83</c> 的 <c>CsMarkers.BombsiteRadius = 7m</c>），
        /// 不新增第二个半径。</para>
        ///
        /// <para>与 <see cref="ContinueUse"/> 的分工：那一条管"已经开始下包、别被抢走"，
        /// 这一条管"还没开始下包、站在包点里却还在打仗"。</para>
        /// </summary>
        private bool MustPlantFirst(CsActor self, float now)
        {
            if (self.Team != CsTeam.T || !self.HasBomb) return false;
            if (_match.BombPlanted) return false;                 // 已下过包 → 交给守包点
            if (EnemyNearPlant(self, now, out _, out _)) return false;   // 当人面不许下包

            // 站到下包点（同层包点标记的停步半径内）才算"该下包了"。
            // 只判包点判定球（≤ SiteRadius）会让 bot 在球面（= 包点边缘）就停住按 E。
            return AtPlantSpot(self);
        }

        /// <summary>
        /// 下包前的敌人闸门：敌人**可见**、或**刚丢视野（记忆期内）**且水平距离 ≤
        /// <see cref="CsBotConst.PlantEnemyAbortRange"/> ⇒ 不许下包。
        /// </summary>
        /// <param name="enemyDist">可见目标的水平距离（记忆期分支用最后所见位置）。</param>
        /// <param name="seenAge">距上次看见的秒数（可见时为 0）。</param>
        private bool EnemyNearPlant(CsActor self, float now, out float enemyDist, out float seenAge)
        {
            enemyDist = float.MaxValue;
            seenAge = float.MaxValue;

            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target == null || !target.IsAlive || target.Team == self.Team) return false;

            if (_visibleNow)
            {
                var d = target.Position - self.Position;
                d.y = 0f;
                enemyDist = d.magnitude;
                seenAge = 0f;
                return enemyDist <= CsBotConst.PlantEnemyAbortRange;
            }

            if (_lastSeenTime <= 0f) return false;
            seenAge = now - _lastSeenTime;
            if (seenAge > CsBotConst.PlantEnemyMemorySeconds) return false;

            var dp = _lastSeenPos - self.Position;
            dp.y = 0f;
            enemyDist = dp.magnitude;
            return enemyDist <= CsBotConst.PlantEnemyAbortRange;
        }

        /// <summary>是否已经站在下包点：同层包点标记的水平距离 ≤ 停步半径，且该点仍在包点判定球内。</summary>
        private bool AtPlantSpot(CsActor self)
        {
            if (!TryGetPlantAim(self, out var aim)) return false;

            NearestBombsitePoint(self.Position, out var zoneDist, out _);
            var d = aim - self.Position;
            d.y = 0f;
            return d.magnitude <= CsBotConst.PlantStopRadius && zoneDist <= CsBotConst.SiteRadius;
        }

        // ==================================================================
        //  感知
        // ==================================================================
        private void UpdatePerception(CsActor self, float now)
        {
            _visibleNow = false;
            _closestVisibleDist = float.MaxValue;
            _visibleEnemies = 0;
            _visibleFriends = 0;

            var eye = self.EyePosition;
            var forward = Forward(self.Yaw, 0f);
            var halfFov = CsConst.BotFovDegrees * 0.5f;
            var actors = _match.Actors;

            CsActor best = null;
            var bestDist = float.MaxValue;
            var bestScore = float.MaxValue;

            for (var i = 0; i < actors.Count; i++)
            {
                var other = actors[i];
                if (other == null || !other.IsAlive || other.Id == self.Id) continue;
                if (other.Team == self.Team) continue;                       // 只打敌对阵营
                if (other.Team != CsTeam.T && other.Team != CsTeam.CT) continue;   // Spectator 不打

                var targetEye = other.EyePosition;
                var d = targetEye - eye;
                var dist = d.magnitude;
                if (dist > _profile.VisionRange) continue;

                d.y = 0f;
                if (d.sqrMagnitude > 0.0004f && Vector3.Angle(forward, d) > halfFov) continue;

                // 视线用 BotSight（CS 1.6 语义：其它角色不挡视线）而不是直接问契约 —— 理由见该类的注释。
                var sight = BotSight.Check(_match, eye, targetEye, _profile.VisionRange,
                    out var sightBlocker, out _);
                if (sight == BotSightResult.BlockedByActorOnly)
                {
                    LogSightActorBlock(sightBlocker, now);
                }
                else if (sight != BotSightResult.Clear)
                {
                    continue;
                }

                _visibleEnemies++;

                // 目标选择：**威胁优先**而不是纯粹的"最近优先"。原版用
                // FindMostDangerousThreat()（cs_bot_vision.cpp:1048）综合距离/是否在打我等因素；
                // 这里只用一个可观测的近似 —— 对方的朝向是否指着我（正对着我 = 他下一秒会开枪）。
                // 权重扣减 6m：足够让"正对着我的敌人"压过"近 6m 内但背对我的敌人"，又不会
                // 让远处的人抢走贴脸目标的优先级。
                var aimsAtMe = 0f;
                var toMe = eye - other.EyePosition;
                toMe.y = 0f;
                if (toMe.sqrMagnitude > 0.0004f &&
                    Vector3.Angle(Forward(other.Yaw, 0f), toMe) <= 20f)
                {
                    aimsAtMe = 6f;
                }

                if (dist - aimsAtMe < bestScore)
                {
                    bestScore = dist - aimsAtMe;
                    bestDist = dist;
                    best = other;
                    _lastSight = sight;
                    _lastSightBlocker = sightBlocker;
                }
            }

            // ---- 可见**队友**计数（无线电"被压制 / 说了有人听"要用；口径与上面完全同源）----
            for (var i = 0; i < actors.Count; i++)
            {
                var mate = actors[i];
                if (mate == null || !mate.IsAlive || mate.Id == self.Id) continue;
                if (mate.Team != self.Team) continue;
                if ((mate.Team != CsTeam.T && mate.Team != CsTeam.CT)) continue;

                var mateEye = mate.EyePosition;
                var md = mateEye - eye;
                var mdist = md.magnitude;
                if (mdist > _profile.VisionRange) continue;

                md.y = 0f;
                if (md.sqrMagnitude > 0.0004f && Vector3.Angle(forward, md) > halfFov) continue;

                var mateSight = BotSight.Check(_match, eye, mateEye, _profile.VisionRange, out _, out _);
                if (mateSight != BotSightResult.Clear) continue;

                _visibleFriends++;
            }

            if (best != null)
            {
                // 反应计时按**敌人身份**算：换成另一个敌人时，只有"这个敌人上一次被看见已超出记忆时长"
                // 才算首次发现。两个人交替成为最近可见目标时各自都有近期记录 ⇒ 不重新计时，
                // 否则 `reacted` 永远过不去、扳机永远扣不下去。
                if (best.Id != _targetId &&
                    (!_enemySeenAt.TryGetValue(best.Id, out var seenAt) ||
                     now - seenAt > CsBotConst.TargetMemorySeconds))
                {
                    _firstSeenTime = now;
                    _wobbleRefreshAt = 0f;
                    _headRollAt = 0f;
                }

                _enemySeenAt[best.Id] = now;
                _targetId = best.Id;
                _lastSeenTime = now;
                _lastSeenPos = best.EyePosition;
                _visibleNow = true;
                _closestVisibleDist = bestDist;
            }
            else if (_targetId != 0 && now - _lastSeenTime > CsBotConst.TargetMemorySeconds)
            {
                _targetId = 0;   // 记忆到期 → 忘掉
            }

            // ---- 听觉（枪声 / 跑动）----
            // 原版对噪声同样有反应延迟（cs_bot.h:1141-1145 IsNoiseHeard：距噪声时间 ≥ ReactionTime 才算听见），
            // 这里用同一口径：刚响的那一声不会立刻让人转身。
            _heardFresh = false;
            if (_sense != null &&
                _sense.TryHearEnemy(self.Team, self.Position, CsConst.BotHearRadius, out var heardPos, out var heardTime, out _))
            {
                if (heardTime >= _heardTime && now - heardTime >= _profile.ReactionTime)
                {
                    _heardPos = heardPos;
                    _heardTime = heardTime;
                    _heardFresh = true;
                }
            }
        }

        // ==================================================================
        //  交战
        // ==================================================================
        private CsBotIntent Engage(CsActor self, CsBotIntent intent, float now)
        {
            var target = _match.Find(_targetId);
            if (target == null || !target.IsAlive)
            {
                _targetId = 0;
                intent.State = CsBotState.Idle;
                intent.AimPoint = LookAhead(self);
                return intent;
            }

            var dist = Vector3.Distance(self.EyePosition, target.EyePosition);
            var aimHead = ChooseHeadshot(now);
            var point = aimHead
                ? target.EyePosition
                : target.Position + Vector3.up * (target.Height * CsBotConst.ChestHeightRatio);

            // 预瞄提前量（按难度：Normal 0 → Hard 满值，用 profile.AimSpeedDegrees 归一化，不引第二套难度表）
            var predict = PredictSeconds();
            if (predict > 0.0001f)
            {
                point += new Vector3(target.Velocity.x, 0f, target.Velocity.z) * predict;
            }

            // 瞄头更难：额外加一份"低误差"，量 = AimErrorDegrees × 0.4（重采样节奏与模拟内部一致）
            if (aimHead) point += HeadWobbleOffset(self, point, now);

            var canFire = ApplyAimAndFire(self, point, now, ref intent);

            // ---- 姿态与走位 ----
            intent.Crouch = false;
            intent.Walk = false;

            // 本 tick 该不该站定：要么真的扣扳机，要么目标可见且瞄点已收敛（先停稳，速度落下来才打）。
            // 子弹散布直接读水平速度（`Firearm.ComputeSpread` 的 MoveFactor 与档位判据）⇒ 边跑边打就是把子弹打飞。
            var standToFire = canFire || (_visibleNow && AimSettled(self, intent.AimPoint));

            //
            //   持包者（T 且未下包）的走位目标是**本轮包点**，不回落到"与敌人保持偏好距离"：
            //   规格「T 持包到 B 点 → 下包」（`策划/策划案/CS1.6单机参考规格.md:131`）里，
            //   下包是持包者这一回合唯一的回合目标；三档表（同文件 :119-121）没有任何一档写"对峙"。
            // 只改"压上方向"：瞄准 / 开火 / 难度参数一字不动（`ApplyAimAndFire` 已在上方跑完）；
            //    不新增第二个半径 —— 停步半径复用既有 `CsBotConst.PlantStopRadius`（与
            //    `TryPlantOrPickup` 同值），"进包点判定区就下包"由 `CommitTacticalDecision.MustPlantFirst` 负责。
            var plantAim = Vector3.zero;
            var pressing = false;
            if (self.Team == CsTeam.T && self.HasBomb && !_match.BombPlanted)
            {
                pressing = TryGetPlantAim(self, out plantAim);
            }

            // 看不见目标时，"追"的位置是**最后所见位置**而不是敌人的实时坐标：
            // 隔墙跟着实时位置走 = 透视。
            var knownPos = _visibleNow || _lastSeenPos.sqrMagnitude < 0.0001f
                ? target.Position
                : _lastSeenPos;

            // 弹匣空了正在换弹、且已经在有效射程内 → 边退边换（背朝掩体方向退，不站在开阔地当靶子）。
            var retreating = intent.Reload && dist <= PreferredRange() * CsBotConst.AdvanceRangeFactor;

            if (pressing)
            {
                // 持包压点：遇敌要开枪就先停 `EngageShotHoldSeconds` 打完这一梭 / 这一串点射，
                // 再继续朝包点压 —— 不停下就没有 0 速开火帧（见 `CsBotConst.FireStandSpeed`）。
                if (standToFire) _engageHoldUntil = now + CsBotConst.EngageShotHoldSeconds;

                if (now < _engageHoldUntil)
                {
                    intent.Move = Vector3.zero;
                    LogEngageStance(self, true, "持包压点遇敌：站定打完这一梭再压");
                }
                else
                {
                    intent.Move = _nav.ComputeMove(self.Position, plantAim, CsBotConst.PlantStopRadius, now,
                                                   hullHeight: self.Height);
                    LogEngageStance(self, false, "持包压点（本 tick 不扣扳机）");
                }
            }
            else if (retreating)
            {
                var away = HorizontalDir(target.Position, self.Position);
                away.y = 0f;
                if (away.sqrMagnitude < 0.5f) away = -HorizontalDir(self.Position, target.Position);
                var back = self.Position + away.normalized * CsBotConst.RetreatDistance;
                intent.Move = _nav.ComputeMove(self.Position, back, 0.5f, now,
                                               viaRoute: false, hullHeight: self.Height);
                RateWarn("engage.retreat",
                    $"{_name} 弹匣已空 → 后退换弹（距目标 {dist:F1}m，后退 {CsBotConst.RetreatDistance:F1}m，" +
                    $"武器 {self.ActiveWeapon ?? "无"}，换弹中不站定当靶子）");
            }
            else if (standToFire)
            {
                // 站定射击：要开枪就 `Move = zero`（速度靠模拟摩擦落到 0，开火门限等它落完）。
                // 远距离蹲下压低受弹面；近身站着，换位更快。
                intent.Move = Vector3.zero;
                intent.Crouch = dist > CsBotConst.EngageCrouchDistance;
                LogEngageStance(self, true, canFire
                    ? $"开火（距目标 {dist:F1}m）"
                    : "瞄点已收敛，等水平速度归零再开火");
            }
            else if (dist > PreferredRange() * CsBotConst.AdvanceRangeFactor)
            {
                // 太远且还没瞄上：压上去（看得见就追人，看不见就追最后所见位置）
                intent.Move = HorizontalDir(self.Position, knownPos);
                LogEngageStance(self, false, $"距离 {dist:F1}m 超出推进门槛，压上去");
            }
            else if (!_visibleNow)
            {
                // 目标仍在记忆期内但此刻看不见 → 压到"最后所见位置"去搜，而不是原地横移。
                var dLast = knownPos - self.Position;
                dLast.y = 0f;
                intent.Move = dLast.magnitude > CsBotConst.WaypointArriveRadius
                    ? dLast.normalized
                    : Vector3.zero;
                LogEngageStance(self, false, "追最后所见位置");
            }
            else
            {
                // 看得见、但瞄点还没收敛（视角还在转）⇒ 横向走位等转向；一旦瞄上就进上面的站定分支。
                intent.Move = Strafe(self, now);
                LogEngageStance(self, false, "瞄点未收敛 → 侧移等转向");
            }

            // 纯后退超过 0.6s ⇒ 改成横向侧移（不许整段背对目标倒着走）
            ApplyBackpedalCap(self, ref intent, now);

            // 近身倾向换手枪（< 3m），拉开后换回主武器（> 6m）：滞回 + 冷却，防来回切
            intent.SwitchTo = NearWeaponChoice(self, dist, now);

            SetState(CsBotState.Engage, now);
            intent.State = CsBotState.Engage;
            // 注意：**不要再写 `intent.AimPoint = point`** —— ApplyAimAndFire 已经落过瞄点，
            // 且在"刚丢视野的宽限"里它会把瞄点改成"最后所见位置"（这一行会把它覆盖回去，
            // 于是模拟把视角转向一个此刻根本打不到的点、朝向差永远收敛不到门限里）。
            return intent;
        }

        /// <summary>开火门限：当前朝向与理想瞄点的夹角足够小才开火（"抬枪→开火"，而不是边转边扫）。</summary>
        private bool AimSettled(CsActor self, Vector3 aimPoint)
        {
            return AimAngle(self, aimPoint) <= AimGate();
        }

        /// <summary>当前朝向与 <paramref name="aimPoint"/> 的夹角（度）。与 <see cref="AimSettled"/> 同一算法，
        /// 单独拆出来是为了让"开火被抑制"的诊断日志能打出**实际角度**而不是只有一个布尔。</summary>
        private float AimAngle(CsActor self, Vector3 aimPoint)
        {
            var to = aimPoint - self.EyePosition;
            if (to.sqrMagnitude < 0.0001f) return 0f;
            return Vector3.Angle(Forward(self.Yaw, self.Pitch), to.normalized);
        }

        /// <summary>本难度的开火角度门限（度）：<c>clamp(AimErrorDegrees×scale + extra, min, max)</c>。</summary>
        private float AimGate()
        {
            return Mathf.Clamp(
                _profile.AimErrorDegrees * CsBotConst.AimGateErrorScale + CsBotConst.AimGateExtraDegrees,
                CsBotConst.AimGateMinDegrees, CsBotConst.AimGateMaxDegrees);
        }

        /// <summary>
        /// "瞄这个点 + 现在该不该扣扳机"的统一计算（交战时、以及冲包点途中警戒时共用）。
        ///
        /// <para>开火要同时满足：目标可见（本 tick 的 <c>_visibleNow</c>，或"刚丢视野"的宽限
        /// <c>profile.ReactionTime + CsConst.BotTickInterval</c>）、已经过了难度反应时间、
        /// 朝向已经收敛到瞄点（<see cref="AimSettled"/>）、**已经站定**（在地面且水平速度
        /// ≤ <see cref="CsBotConst.FireStandSpeed"/>）、没在换弹、没被闪光致盲。
        /// 连发节奏不在这里 —— 那是模拟内部按 <c>FireBurst*/FirePause*</c> 执行的。</para>
        ///
        /// <para><b>宽限为什么要存在</b>：逐 tick 判"这一眼看不见"（射线被队友/墙角一瞬打断）
        /// 会让扳机永远不扣。宽限只放行
        /// "把这次已经看见的反应走完"（反应时间 + 一个 tick），瞄点用**最后所见位置**（不预测、不外推，
        /// 子弹照样被物理射线挡在墙上），超过就照旧"看不见 = 不开枪"。</para>
        ///
        /// <para><b>每一条不满足都留证</b>：<see cref="LogFireDenied"/> 每 <see cref="CsBotConst.FireDiagInterval"/> 秒
        /// 打一行"可见 / 反应 / 朝向差 / 视线判定 / 弹匣 / 致盲"，所以"是哪一条恒假"从日志就能读出来，不用猜。</para>
        /// </summary>
        /// <returns>本次是否表达"想开火"。</returns>
        private bool ApplyAimAndFire(CsActor self, Vector3 aimPoint, float now, ref CsBotIntent intent)
        {
            // ---- 刚丢视野的宽限：目标仍在记忆期内、且"距上次看见 ≤ 刚够走完这次反应" → 仍按"可打"处理 ----
            //
            // 宽限 = 反应时间 + 一个决策 tick（**不是写死的秒数**）。理由：Easy 的反应时间是 0.65s ——
            // 若宽限短于反应时间，"只看了一眼就丢"的目标永远等不到 reacted 变真，宽限等于不存在。
            // 上限含义：把"这次已经看见了的反应"走完、把这一两发打出去，然后照旧"看不见 = 不开枪"。
            var graceSeconds = _profile.ReactionTime + CsConst.BotTickInterval;
            var sinceSeen = now - _lastSeenTime;
            var grace = !_visibleNow
                        && _targetId != 0
                        && _lastSeenTime > 0f
                        && sinceSeen <= graceSeconds
                        && _lastSeenPos.sqrMagnitude > 0.0001f;

            // 看不见目标时一律瞄"最后所见位置"（不只宽限期内）：否则会隔着墙把准星钉在敌人的真实位置上，
            // 人一露头就已经被预瞄好了 —— 那是玩家感觉得到的"透视"。
            if (!_visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f) aimPoint = _lastSeenPos;

            intent.AimPoint = aimPoint;

            var reacted = now - _firstSeenTime >= _profile.ReactionTime;
            var angle = AimAngle(self, aimPoint);
            var gate = AimGate();

            // 交战瞄准的俯仰角门限：相对地平线 |pitch| ≤ 45°（本项目新增；原版视角上限 89°，交战瞄准不应超过 45°）。
            // 超过即视为未锁定、不开火 —— 配合 HeadWobbleOffset 的竖直夹取，堵死"朝天开枪"。
            var to = aimPoint - self.EyePosition;
            var aimPitchDeg = to.sqrMagnitude < 0.0001f
                ? 0f
                : Mathf.Asin(Mathf.Clamp(to.y / to.magnitude, -1f, 1f)) * Mathf.Rad2Deg;
            var levelEnough = Mathf.Abs(aimPitchDeg) <= CsBotConst.AimPitchLimitDegrees;

            // 站定才扣扳机：子弹散布按水平速度放大（`Firearm.ComputeSpread` 的 MoveFactor 与档位判据），
            // 速度没落下来之前开枪就是把子弹打飞。与 Engage 的"要开枪就 Move = zero"是同一条约束的两端：
            // 那一端让 bot 停下来，这一端保证"停下来之前不扣扳机"。
            var hv = self.Velocity;
            var speed = new Vector2(hv.x, hv.z).magnitude;
            var planted = self.OnGround && speed <= CsBotConst.FireStandSpeed;

            var canFire = (_visibleNow || grace) && reacted && angle <= gate && levelEnough && planted;

            if (grace && canFire)
            {
                _graceFireCount++;
                if (now >= _nextGraceLogAt)
                {
                    _nextGraceLogAt = now + CsBotConst.FireDiagInterval;
                    Game.Logger.Info(Tag,
                        $"{_name}（{_difficulty}）靠「刚丢视野宽限」开火（第 {_graceFireCount} 次）：" +
                        $"距上次看见 {sinceSeen:F2}s ≤ {graceSeconds:F2}s" +
                        $"（= 反应 {_profile.ReactionTime:F2}s + tick {CsConst.BotTickInterval:F2}s），瞄最后所见位置");
                }
            }

            // 弹药空了 → 请求换弹（模拟内部有完整校验）
            var def = self.ActiveDef;
            if (def != null && def.Magazine > 0 && self.GetAmmo(def.Id).inMag <= 0)
            {
                intent.Reload = true;
                canFire = false;
            }

            // 被闪光弹致盲时不打（模拟只记致盲时间，不下降命中率 —— 这里补上行为）
            if (now < self.FlashEndTime) canFire = false;

            if (!canFire) LogFireDenied(self, now, reacted, angle, gate, aimPitchDeg, sinceSeen, grace, speed);

            intent.Fire = canFire;
            if (canFire) LastFireIntentTime = now;
            return canFire;
        }

        /// <summary>
        /// "这一 tick 决定不开火"的诊断：把三条门限**连同数值**打出来 ——
        /// 这就是"到底哪一条恒假"的原始证据（降频：每 bot 每 <see cref="CsBotConst.FireDiagInterval"/> 秒一条，首次必打）。
        /// </summary>
        private void LogFireDenied(CsActor self, float now, bool reacted, float angle, float gate,
            float aimPitchDeg, float sinceSeen, bool grace, float speed)
        {
            // 没有交战目标（例如冲包点路上的警戒开火）时的"不开火"是**预期行为**，不是异常分支 → 不记
            if (_targetId == 0) return;

            _fireDeniedCount++;
            if (now < _nextFireDiagAt) return;
            _nextFireDiagAt = now + CsBotConst.FireDiagInterval;

            var sight = _lastSight;
            var blocker = _lastSightBlocker;
            var target = _match.Find(_targetId);
            if (target != null && !target.IsAlive) target = null;

            if (!_visibleNow && target != null)
            {
                sight = BotSight.Check(_match, self.EyePosition, target.EyePosition, _profile.VisionRange,
                    out blocker, out _);
            }

            // 手里到底有没有枪 / 弹匣与备弹（"扣了扳机却没扣出子弹"的另一条死路：没枪 / 弹匣空且备弹也用光）
            var def = self.ActiveDef;
            var mag = -1;
            var reserve = -1;
            if (def != null)
            {
                var ammo = self.GetAmmo(def.Id);
                mag = ammo.inMag;
                reserve = ammo.reserve;
            }

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）交战中开火被抑制第 {_fireDeniedCount} 次：目标=actor {_targetId}" +
                $" 可见={_visibleNow}（距上次看见 {sinceSeen:F2}s）" +
                $" 反应={(reacted ? "过" : "未过")}（{now - _firstSeenTime:F2}/{_profile.ReactionTime:F2}s）" +
                $" 朝向差={angle:F1}° vs 门限={gate:F1}°" +
                $" 俯仰={aimPitchDeg:F1}° vs 上限={CsBotConst.AimPitchLimitDegrees:F0}°" +
                $" 水平速度={speed:F2}m/s vs 站定上限={CsBotConst.FireStandSpeed:F2}m/s" +
                $"（{(self.OnGround ? "在地面" : "离地")}）" +
                $" 视线={BotSight.Text(sight)}{(string.IsNullOrEmpty(blocker) ? string.Empty : $"（挡={blocker}）")}" +
                $"{(grace ? "（宽限内）" : string.Empty)}" +
                $" 武器={self.ActiveWeapon ?? "无"} 弹匣={mag} 备弹={reserve}" +
                $" 致盲剩余={Mathf.Max(0f, self.FlashEndTime - now):F2}s");
        }

        /// <summary>视线只被"角色受体"挡住、按 CS 语义放行的留证（降频：每 bot 每 10s 一条）。</summary>
        private void LogSightActorBlock(string blocker, float now)
        {
            _sightActorBlockCount++;
            if (now < _nextSightBlockLogAt) return;
            _nextSightBlockLogAt = now + CsBotConst.SightActorBlockLogInterval;

            Game.Logger.Info(Tag,
                $"{_name}（{_difficulty}）视线被角色受体挡住（挡={blocker ?? "未知"}）→ 按 CS 1.6 语义" +
                $"（其它玩家不挡视线）判定为**可见**（第 {_sightActorBlockCount} 次；本日志每 " +
                $"{CsBotConst.SightActorBlockLogInterval:F0}s 最多一条）");
        }

        /// <summary>本轮这一枪瞄不瞄头（按 <c>HeadshotChance</c> 掷，整段交战保持同一选择）。</summary>
        private bool ChooseHeadshot(float now)
        {
            if (now >= _headRollAt)
            {
                _headRollAt = now + CsBotConst.HeadshotRollSeconds;
                // 瞄不瞄头：**玩法**（直接改伤害与击杀数）。走本 bot 的 BotAimFlavor 流。
                _headRoll = _rngAim.NextFloat() < _profile.HeadshotChance;
            }
            return _headRoll;
        }

        private Vector3 HeadWobbleOffset(CsActor self, Vector3 point, float now)
        {
            if (now >= _wobbleRefreshAt)
            {
                _wobbleRefreshAt = now + CsBotConst.AimWobbleRefreshSeconds;
                // 头部摆动：**玩法**（它是瞄准偏移，直接进 AimPoint）。同一个 BotAimFlavor 流。
                _wobble = new Vector2(_rngAim.Range(-1f, 1f), _rngAim.Range(-1f, 1f));
            }

            if (Mathf.Approximately(_wobble.x, 0f) && Mathf.Approximately(_wobble.y, 0f)) return Vector3.zero;

            var to = point - self.EyePosition;
            var dist = to.magnitude;
            if (dist < 0.01f) return Vector3.zero;

            var dir = to / dist;
            var right = Vector3.Cross(Vector3.up, dir);
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            right.Normalize();

            var magDeg = _profile.AimErrorDegrees * CsBotConst.HeadAimExtraErrorScale;
            var scale = dist * Mathf.Tan(magDeg * Mathf.Deg2Rad);
            var offset = (right * _wobble.x + Vector3.up * _wobble.y) * scale;

            // 竖直分量夹到 ±0.3 米（本项目新增；CS 1.6 头部半径约 0.1m 量级，夹住防止瞄点飞出头部区域——
            // 原实现无界：远处 scale = dist×tan(AimError×HeadAimExtraErrorScale) 可达数十度仰角 ⇒ 朝天开枪）。水平分量保留。
            return new Vector3(offset.x, Mathf.Clamp(offset.y, -0.3f, 0.3f), offset.z);
        }

        /// <summary>预瞄力度 0（Easy/Normal）~ 1（Hard）。</summary>
        private float PredictSeconds()
        {
            var t = Mathf.InverseLerp(CsBotConst.PredictSkillSpeedLo, CsBotConst.PredictSkillSpeedHi, _profile.AimSpeedDegrees);
            return CsBotConst.PredictSecondsMax * t;
        }

        /// <summary>
        /// 交战中的横向走位（"走位"）。
        ///
        ///
        /// <para><b>出处（不是本项目自创）</b>：规格 <c>策划/策划案/CS1.6单机参考规格.md</c>
        /// —— "走位"是**三档共有的行为**，档位差别只在参数上。本方法里随档位变化的参数本来就有
        /// （<c>period = max(0.6, RepathInterval)</c>：Easy 1.6s / Normal 1.0s / Hard 0.5s ⇒ 换向节奏不同），
        /// 所以修法 = **去掉"低难度不产生走位"这个非参数性的门**，把三档交回同一个行为。</para>
        /// </summary>
        private Vector3 Strafe(CsActor self, float now)
        {
            var period = Mathf.Max(0.6f, _profile.RepathInterval);
            if (now >= _strafeFlipAt)
            {
                _strafeFlipAt = now + period;
                _strafeSign = -_strafeSign;
            }

            var yawRad = self.Yaw * Mathf.Deg2Rad;
            var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
            return right * _strafeSign;
        }

        /// <summary>
        /// 交战走位的现场日志（按 <see cref="CsBotConst.StateLogMinInterval"/> 限频）：
        /// 这一 tick 为什么站着不动（<c>engage.stand</c>）或为什么还在走（<c>engage.move</c>）。
        /// 站定的三个原因 = 开火 / 瞄点收敛等速度归零 / 持包遇敌；移动的原因 = 压点 / 换弹后撤 / 压上 / 追最后所见 / 侧移等转向。
        /// </summary>
        private void LogEngageStance(CsActor self, bool standing, string why)
        {
            var key = "bot.engage." + (standing ? "stand" : "move") + ":" + _name;
            if (!CloverEngine.LogThrottle.ShouldLog(key, CsBotConst.StateLogMinInterval)) return;

            var v = self != null ? self.Velocity : Vector3.zero;
            var speed = new Vector2(v.x, v.z).magnitude;
            Game.Logger.Info(Tag,
                $"engage.{(standing ? "stand" : "move")} {_name}（{_difficulty}）" +
                $"水平速度 {speed:F2}m/s 蹲={(self != null && self.IsCrouching)} 原因={why}");
        }

        private string NearWeaponChoice(CsActor self, float dist, float now)
        {
            if (now < _switchCooldownUntil) return null;

            var primary = self.PrimaryWeapon;
            var secondary = self.SecondaryWeapon;
            if (string.IsNullOrEmpty(primary)) return null;

            if (dist < CsBotConst.MeleeSwitchRange)
            {
                if (!string.IsNullOrEmpty(secondary) && self.ActiveWeapon != secondary)
                {
                    _switchCooldownUntil = now + CsBotConst.WeaponSwitchCooldown;
                    return secondary;
                }
                return null;
            }

            if (dist > CsBotConst.MeleeBackRange && self.ActiveWeapon != primary)
            {
                _switchCooldownUntil = now + CsBotConst.WeaponSwitchCooldown;
                return primary;
            }

            return null;
        }

        // ==================================================================
        //  投掷物（角色分工）
        // ==================================================================
        /// <summary>
        /// 本 tick 是否提交"投掷"意图（true = 已被投掷占用，调用方直接返回该意图）。
        ///
        /// <para><b>为什么放在顶层分支之前</b>：投掷要么用来"开打前先炸/闪"，要么在推进途中朝目标点投
        /// （到点封烟、冲点前清角）—— 只挂在交战分支里会漏掉后一种，只挂在推进里会漏掉前一种。</para>
        ///
        /// <para><b>投什么</b>：按角色偏好表（<see cref="CsBotRoles.PreferredGrenades"/>）取**手上有的**第一种：
        /// 突破 = 高爆 → 闪光，支援 = 高爆 → 烟，侦察 = 闪光 → 烟，守点 = 烟 → 高爆。
        /// 出处分段见 <see cref="CsBotRoles.PreferredGrenades"/>（三种雷都在原版 mp.dll 的 bot 购买序列里，
        /// "哪个角色先拿哪一种"本项目新增）。</para>
        ///
        /// <para><b>该不该投</b>：目标（敌人 / 包点 / 本轮目标点）的水平距离必须 ≥ 该类型的下界，
        /// 落点取 <c>min(距离, GrenadeMaxRange)</c>（见 <see cref="TryAimWithinReach"/>）——
        /// 投掷在本工程里是"沿瞄向给初速 <c>CsConst.GrenadeThrowSpeed(仰角)</c> + 重力"，最远 ≈ 10.04m（仰角 ≈ 55°），
        /// 更远的目标物理上扔不到 ⇒ 落在朝它方向的最远处；
        /// 下界按类型取（烟雾 = <see cref="CsBotConst.GrenadeMinRange"/>，
        /// 高爆/闪光 = <see cref="CsBotConst.GrenadeSelfSafeRange"/>，即"自己不在爆心半径里"）。</para>
        ///
        /// <para><b>切雷与转视角都要等</b>：切枪、引信、爆炸都在模拟里，本类只能"等换手（
        /// <see cref="GrenadeSwitchTimeout"/> 兜底）→ 等视角转到瞄点（<see cref="CsBotConst.GrenadeAimGateDegrees"/>）
        /// → 扣扳机"。</para>
        /// </summary>
        private bool TryThrowGrenade(CsActor self, ref CsBotIntent intent, float now)
        {
            // 手上还剩同一种雷时，投完那一颗模拟不会自动换回枪（CsInventory.ThrowGrenade 只在"这一类归零"时
            //   才 SelectBestWeapon）⇒ 这里补一次"换回主武器"，否则机器人抱着手雷站着不开枪。
            if (_grenadeReturnToWeapon)
            {
                var back = !string.IsNullOrEmpty(self.PrimaryWeapon) ? self.PrimaryWeapon : self.KnifeWeapon;
                if (string.IsNullOrEmpty(back) || self.ActiveWeapon == back)
                {
                    _grenadeReturnToWeapon = false;
                }
                else
                {
                    intent.SwitchTo = back;
                    intent.Fire = false;
                    intent.Move = Vector3.zero;
                    intent.AimPoint = _goalValid ? WithEyeHeight(_goalPos) : LookAhead(self);
                    return true;
                }
            }

            // 正在下包/拆包时不许打断（一移动进度就清零，与模拟判定一致）；投满上限也不再投。
            if (self.UseProgress >= 0f || _grenadeThrows >= CsBotConst.GrenadeMaxPerRound)
            {
                if (self.UseProgress >= 0f)
                {
                    NadeDiag(self, "在下包拆包", $"正在下包/拆包（进度 {self.UseProgress:P0}），本轮投掷让位");
                }
                else
                {
                    NadeDiag(self, "已投满", $"本回合已投 {_grenadeThrows} 颗（上限 {CsBotConst.GrenadeMaxPerRound}）");
                }
                ClearGrenadeAttempt();
                return false;
            }

            if (_grenadePendingId == null && !TryBeginGrenade(self, now)) return false;

            if (!string.IsNullOrEmpty(_grenadePendingId) && self.ActiveWeapon != _grenadePendingId)
            {
                if (now > _grenadeSwitchDeadline)
                {
                    RateWarn("nade.switch.timeout",
                        $"{_name} 切到 {_grenadePendingId} 超过 {CsBotConst.GrenadeSwitchTimeout:F1}s 仍未换手 → 放弃这次投掷");
                    NadeDiag(self, "切雷超时", $"切到 {_grenadePendingId} 超过 {CsBotConst.GrenadeSwitchTimeout:F1}s 仍未换手");
                    _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
                    ClearGrenadeAttempt();
                    return false;
                }

                intent.SwitchTo = _grenadePendingId;
                intent.Fire = false;
                intent.Move = Vector3.zero;
                intent.AimPoint = _grenadeAim;
                return true;
            }

            intent.SwitchTo = null;
            intent.Move = Vector3.zero;
            intent.Crouch = false;
            intent.AimPoint = _grenadeAim;

            // 切枪动作由模拟执行：ActiveWeapon 立即变成那颗雷，但 SwitchEndTime 之前扣扳机会被拒
            // （CsInventory.TryDischarge 的 `now < a.SwitchEndTime`），所以这里要等换手完成。
            if (now < self.SwitchEndTime || AimAngle(self, _grenadeAim) > CsBotConst.GrenadeAimGateDegrees)
            {
                // 等不出来的情况必须留痕并放弃：否则机器人会抱着手雷一直"等视角"，既不开枪也不移动。
                if (now > _grenadeSwitchDeadline)
                {
                    NadeDiag(self, "视角未收敛",
                        $"切到 {_grenadePendingId} 后视角 {AimAngle(self, _grenadeAim):F1}° 未收敛" +
                        $"（门限 {CsBotConst.GrenadeAimGateDegrees:F0}°）→ 放弃本次投掷");
                    _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
                    ClearGrenadeAttempt();
                    return false;
                }

                intent.Fire = false;
                return true;
            }

            intent.Fire = true;
            _grenadeThrows++;
            _nextGrenadeAt = now + CsBotConst.GrenadeThrowSpacing;
            // 这一颗投出去后手上还剩同类雷（如闪光弹上限 2 颗）⇒ 下一 tick 要主动换回主武器。
            _grenadeReturnToWeapon = CountGrenade(self, _grenadePendingId) - 1 > 0;

            var def = CsWeapons.Get(_grenadePendingId);
            var mateText = _grenadeMateDist < float.MaxValue ? $"{_grenadeMateDist:F1}m" : "无";
            Game.Logger.Info(Tag,
                $"{_name}({self.Team}/{CsBotRoles.Label(_role)}) 投掷 {def?.DisplayName ?? _grenadePendingId}" +
                $"（{_grenadeWhy}；放行={_grenadeGateWhy}，落点=({_grenadeLanding.x:F1},{_grenadeLanding.y:F1}," +
                $"{_grenadeLanding.z:F1})，距目标 {_grenadeRange:F1}m，最近队友 {mateText}，" +
                $"本回合第 {_grenadeThrows} 颗，剩余 {Mathf.Max(0, CountGrenade(self, _grenadePendingId) - 1)} 颗）");

            ClearGrenadeAttempt();
            return true;
        }

        /// <summary>选出这一颗要投的雷与瞄点（成功即进入"切雷"阶段；理由写进日志）。</summary>
        private bool TryBeginGrenade(CsActor self, float now)
        {
            if (now < _nextGrenadeAt)
            {
                NadeDiag(self, "投掷间隔中", $"距下次允许投掷还有 {_nextGrenadeAt - now:F1}s");
                return false;
            }

            var prefs = CsBotRoles.PreferredGrenades(_role);
            var id = (string)null;
            for (var i = 0; i < prefs.Length; i++)
            {
                if (CountGrenade(self, prefs[i]) > 0) { id = prefs[i]; break; }
            }
            if (id == null)
            {
                NadeDiag(self, "没有雷", $"手上没有偏好表里的投掷物（偏好 {CsBotRoles.GrenadeText(_role)}）");
                return false;
            }

            if (!TryPickGrenadeTarget(self, id, out var point, out var why)) return false;

            if (!GrenadeAllowed(self, id, point, now, out var gateWhy, out var landing, out var mateDist))
            {
                NadeDiag(self, "投掷门槛", gateWhy);
                return false;
            }

            _grenadeAim = GrenadeAimPoint(self, point, out var range);
            _grenadePendingId = id;
            _grenadeWhy = why;
            _grenadeRange = range;
            _grenadeGateWhy = gateWhy;
            _grenadeLanding = landing;
            _grenadeMateDist = mateDist;
            _grenadeSwitchDeadline = now + CsBotConst.GrenadeSwitchTimeout;
            return true;
        }

        /// <summary>
        /// 投掷目标点，按优先级：① 记忆期内的敌人（此时才叫"该炸"）；② 最近的包点标记
        /// （进点封烟 / 冲点前清角，与下包/拆包同用 <see cref="NearestBombsitePoint"/> 的口径）；
        /// ③ 本轮目标点（巡逻点 / 守位）。
        ///
        /// <para>下界按类型：烟雾 <see cref="CsBotConst.GrenadeMinRange"/>（3m，别扔在脚下）、
        /// 高爆/闪光 <see cref="CsBotConst.GrenadeSelfSafeRange"/>（= 爆心半径 5.5m，自己不吃自己的雷）。</para>
        /// </summary>
        private bool TryPickGrenadeTarget(CsActor self, string grenadeId, out Vector3 point, out string why)
        {
            point = Vector3.zero;
            why = null;

            var min = grenadeId == CsWeapons.SmokeGrenade
                ? CsBotConst.GrenadeMinRange
                : CsBotConst.GrenadeSelfSafeRange;

            var target = _targetId != 0 ? _match.Find(_targetId) : null;
            if (target != null && target.IsAlive && target.Team != self.Team)
            {
                var raw = target.Position + Vector3.up * (target.Height * CsBotConst.ChestHeightRatio);
                if (TryAimWithinReach(self, raw, min, _visibleNow ? "交战投掷" : "朝刚丢视野的目标位置投", out point, out why))
                    return true;
            }

            var site = NearestBombsitePoint(self.Position, out var siteDist, out var siteName);
            if (siteDist < float.MaxValue && siteName != null)
            {
                var raw = site + Vector3.up * CsConst.EyeHeight;
                if (TryAimWithinReach(self, raw, min, $"{siteName} 进点投掷", out point, out why)) return true;
            }

            if (_goalValid)
            {
                var raw = _goalPos + Vector3.up * CsConst.EyeHeight;
                var baseWhy = _goalIsSite ? "目标点投掷" : "朝本轮目标点投";
                if (TryAimWithinReach(self, raw, min, baseWhy, out point, out why)) return true;
            }

            NadeDiag(self, "没有目标",
                $"没有投得出手的目标：敌人目标={(_targetId != 0 ? _targetId.ToString() : "无")}，" +
                $"离最近包点标记 {siteDist:F1}m，本轮目标点=" +
                (_goalValid ? Vector3.Distance(self.Position, _goalPos).ToString("F1") + "m" : "无"));
            return false;
        }

        /// <summary>
        /// 投掷时机 / 队友安全闸门（true = 可以投）。
        /// <list type="number">
        /// <item>开局 <see cref="CsBotConst.GrenadeNoThrowSeconds"/> 秒内一律不投（出生点附近全是队友）；</item>
        /// <item>没有可见 / 记忆期敌人时，T 只在"进攻准备位"（到本轮包点 ≤
        /// <see cref="CsBotConst.GrenadeApproachRange"/>）投，CT 只在已下包回防时投；</item>
        /// <item>烟只在进点 / 回防时投；</item>
        /// <item>落点 <see cref="CsBotConst.GrenadeTeammateSafeRadius"/> 内、或投掷锥内该半径内有队友 ⇒ 不投。</item>
        /// </list>
        /// </summary>
        /// <param name="gateWhy">放行理由（进日志）。</param>
        /// <param name="landing">本次落点（= 门槛检查用的坐标）。</param>
        /// <param name="mateDist">落点 / 锥内最近的队友距离（没有队友 = <c>float.MaxValue</c>）。</param>
        private bool GrenadeAllowed(CsActor self, string id, Vector3 point, float now,
            out string gateWhy, out Vector3 landing, out float mateDist)
        {
            gateWhy = null;
            landing = point;
            mateDist = float.MaxValue;

            var elapsed = now - _roundStartAt;
            if (elapsed < CsBotConst.GrenadeNoThrowSeconds)
            {
                gateWhy = $"开局 {elapsed:F1}s < {CsBotConst.GrenadeNoThrowSeconds:F0}s";
                return false;
            }

            var hasEnemy = _targetId != 0 && now - _lastSeenTime <= CsBotConst.PlantEnemyMemorySeconds;
            var siteDist = float.MaxValue;
            var approach = self.Team == CsTeam.T && AtAttackApproach(self, out siteDist);
            var siteDistText = siteDist < float.MaxValue ? siteDist.ToString("F1") : "∞";

            if (id == CsWeapons.SmokeGrenade && !approach && !(self.Team == CsTeam.CT && _match.BombPlanted))
            {
                gateWhy = $"烟只在进点/回防时投（没有可见敌人，到本轮包点 {siteDistText}m）";
                return false;
            }

            if (!hasEnemy && !approach && !(self.Team == CsTeam.CT && _match.BombPlanted))
            {
                gateWhy = $"没有可见/记忆期敌人，且不在进攻准备位（到本轮包点 {siteDistText}m > " +
                          $"{CsBotConst.GrenadeApproachRange:F0}m）";
                return false;
            }

            mateDist = NearestTeammateToThrow(self, point, out var mateName, out var inCone);
            if (mateDist < CsBotConst.GrenadeTeammateSafeRadius)
            {
                gateWhy = inCone
                    ? $"投掷锥内 {mateDist:F1}m 有队友 {mateName}"
                    : $"落点 {mateDist:F1}m 内有队友 {mateName}";
                return false;
            }

            gateWhy = hasEnemy
                ? "有可见/记忆期敌人"
                : approach ? $"进攻准备位（到本轮包点 {siteDistText}m）" : "CT 回防（包已下）";
            return true;
        }

        /// <summary>是否在"进攻准备位"：本轮目标是包点，且到该包点标记 ≤ <see cref="CsBotConst.GrenadeApproachRange"/>。</summary>
        private bool AtAttackApproach(CsActor self, out float siteDist)
        {
            siteDist = float.MaxValue;
            if (!_goalValid || !_goalIsSite) return false;

            var site = NearestBombsitePoint(_goalPos, out var d, out _);
            if (d > CsBotConst.SiteRadius) return false;

            var v = site - self.Position;
            v.y = 0f;
            siteDist = v.magnitude;
            return siteDist <= CsBotConst.GrenadeApproachRange;
        }

        /// <summary>
        /// 落点 / 投掷锥内最近的队友距离。锥 = 从自己指向落点的方向 ±
        /// <see cref="CsBotConst.GrenadeTeammateConeDegrees"/>，半径 <see cref="CsBotConst.GrenadeTeammateSafeRadius"/>。
        /// </summary>
        private float NearestTeammateToThrow(CsActor self, Vector3 point, out string mateName, out bool inCone)
        {
            mateName = null;
            inCone = false;

            var toPoint = point - self.EyePosition;
            toPoint.y = 0f;
            var aimDir = toPoint.sqrMagnitude > 0.0001f ? toPoint.normalized : Vector3.zero;

            var best = float.MaxValue;
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Id == self.Id || a.Team != self.Team) continue;

                var dp = a.Position - point;
                dp.y = 0f;
                var landingDist = dp.magnitude;

                var toMate = a.Position - self.Position;
                toMate.y = 0f;
                var mateDist = toMate.magnitude;
                var cone = aimDir != Vector3.zero && mateDist <= CsBotConst.GrenadeTeammateSafeRadius &&
                           Vector3.Angle(aimDir, toMate) <= CsBotConst.GrenadeTeammateConeDegrees;

                if (landingDist < best)
                {
                    best = landingDist;
                    mateName = a.Name;
                    inCone = false;
                }

                if (cone && mateDist < best)
                {
                    best = mateDist;
                    mateName = a.Name;
                    inCone = true;
                }
            }

            return best;
        }

        /// <summary>
        /// 把"想投的地方"折算成**投得出去**的落点：水平距离 &lt; <paramref name="min"/>（贴脸 / 脚下）⇒ 不投；
        /// 否则落在 <c>min(距离, GrenadeMaxRange)</c> 处 —— 目标更远时，雷落在**朝它方向的最远处**
        /// （"朝那个方向扔一颗"，预炸 / 预封，落点仍在投掷物理能达到的范围内）。
        /// </summary>
        private static bool TryAimWithinReach(CsActor self, Vector3 raw, float min, string baseWhy,
            out Vector3 point, out string why)
        {
            point = Vector3.zero;
            why = null;

            var flat = raw - self.EyePosition;
            flat.y = 0f;
            var d = flat.magnitude;
            if (d < min || d < 0.01f) return false;

            var reach = d <= CsBotConst.GrenadeMaxRange ? d : CsBotConst.GrenadeMaxRange;
            point = self.EyePosition + flat.normalized * reach;
            why = d <= CsBotConst.GrenadeMaxRange
                ? baseWhy
                : $"{baseWhy}（超出投掷距离 {d:F1}m → 落在朝该方向的 {CsBotConst.GrenadeMaxRange:F1}m 处）";
            return true;
        }

        /// <summary>
        /// 低弧解算：把瞄点抬到"按这个仰角投出，正好落在目标点水平距离上"的位置。
        /// 解的就是本工程投掷用的那套运动（<c>CsInventory.ThrowGrenade</c> 给初速
        /// <c>CsConst.GrenadeThrowSpeed(仰角)</c>、之后只受 <c>CsConst.Gravity</c>）：
        /// 平抛落点 <c>d(θ) = v(θ)²·sin(2θ)/g</c>，其中 <c>v</c> **自己也是 θ 的函数**
        /// （原版口径 <c>(90 - angThrow.x) × 4</c>）⇒ 定值 v 的闭式解 <c>sin(2θ) = d·g/v²</c> 不再成立，
        /// 改为在**单调上升段** <c>[0°, 55°]</c> 上二分求 θ —— 该段的右端点由
        /// <c>CsBotConst.GrenadeMaxRange</c> 的注释给出（峰值 ≈ 10.04 m @ 55° &gt; 9.6 m 上限
        /// ⇒ 解必落在该段内）。取上升段那一支 = 低弧（近弹道、飞行时间短、不易被墙挡）。
        /// </summary>
        private static Vector3 GrenadeAimPoint(CsActor self, Vector3 point, out float range)
        {
            var flat = point - self.EyePosition;
            flat.y = 0f;
            var d = flat.magnitude;
            range = d;
            if (d < 0.01f) return point;

            var lo = 0f;
            var hi = 55f * Mathf.Deg2Rad;
            for (var i = 0; i < 24; i++)
            {
                var mid = (lo + hi) * 0.5f;
                var v = CsConst.GrenadeThrowSpeed(mid * Mathf.Rad2Deg);
                var reach = v * v * Mathf.Sin(2f * mid) / CsConst.Gravity;
                if (reach < d) lo = mid;
                else hi = mid;
            }
            var pitch = (lo + hi) * 0.5f;

            return self.EyePosition + flat.normalized * d + Vector3.up * (d * Mathf.Tan(pitch));
        }

        /// <summary>手上这类投掷物有几颗（与 <c>CsInventory.GrenadeCount</c> 同读法：弹药表的弹匣位）。</summary>
        private static int CountGrenade(CsActor self, string grenadeId)
        {
            return self.Ammo.TryGetValue(grenadeId, out var v) ? v.inMag : 0;
        }

        /// <summary>
        /// 投掷诊断：说明"为什么没投出去"。**按 (回合, 原因) 去重**（每回合每类原因最多一条）——
        /// 只按回合去重会让"先因为没雷报一次"把后面真正的原因（切雷超时 / 视角没收敛 / 没有目标）永久吞掉。
        /// ⛔ 不许静默：没这条就无法区分"没实现"与"条件没满足"。
        /// </summary>
        private void NadeDiag(CsActor self, string key, string message)
        {
            if (!_grenadeDiagKeys.Add(_match.RoundNumber + ":" + key)) return;
            Game.Logger.Info(Tag, $"{_name}({self.Team}/{CsBotRoles.Label(_role)}) 未投掷[{key}]：{message}");
        }

        /// <summary>清掉"进行中的投掷"（不影响本回合已投颗数与下一次允许时刻）。</summary>
        private void ClearGrenadeAttempt()
        {
            _grenadePendingId = null;
            _grenadeWhy = null;
            _grenadeRange = 0f;
            _grenadeGateWhy = null;
            _grenadeLanding = Vector3.zero;
            _grenadeMateDist = float.MaxValue;
        }

        // ==================================================================
        //  炸弹任务
        // ==================================================================
        /// <summary>
        /// 炸弹任务（T 下包/捡包；CT 拆包）。<b><paramref name="intent"/> 必须 <c>ref</c></b> ——
        /// <see cref="CsBotIntent"/> 是 struct（值类型），按值传时本方法写入的 Move/State/Use 会在返回时丢掉
        /// </summary>
        private bool TryBombObjective(CsActor self, ref CsBotIntent intent, float now)
        {
            if (self.Team == CsTeam.CT) return TryDefuse(self, ref intent, now);
            if (self.Team == CsTeam.T) return TryPlantOrPickup(self, ref intent, now);
            return false;
        }

        /// <summary>CT 拆包分支（`intent` 必须 <c>ref</c>，理由同 <see cref="TryBombObjective"/>）。</summary>
        private bool TryDefuse(CsActor self, ref CsBotIntent intent, float now)
        {
            if (!_match.BombPlanted) return false;

            var bomb = _match.BombPosition;
            var d = bomb - self.Position;
            d.y = 0f;
            var dist = d.magnitude;
            var elected = IsElectedDefuser(self, now);

            if (elected && dist <= CsBotConst.DefuseStopRadius)
            {
                SetState(CsBotState.Defuse, now);
                intent.State = CsBotState.Defuse;
                intent.Move = Vector3.zero;
                intent.Use = true;
                intent.Fire = false;
                intent.AimPoint = WithEyeHeight(bomb);

                if (now >= _lastUseLogAt)
                {
                    _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"{_name} 到达 C4（距离 {dist:F2}m ≤ {CsBotConst.DefuseStopRadius:F1}m）→ 按住 E 拆包" +
                        $"（{(self.HasDefuser ? "有拆弹器" : "无拆弹器")}，剩余 {_match.BombTimeLeft:F1}s）");
                }
                return true;
            }

            // 掩护位：非当选者不贴到 C4 上（否则全队挤在爆点里，一颗雷/一串子弹一起收走，
            // 也没人看 T 的来路）。站到 SupportRadius 外，盯住来路。
            if (!elected)
            {
                SetState(CsBotState.Defuse, now);
                intent.State = CsBotState.Defuse;

                intent.Move = dist > CsBotConst.DefuseSupportRadius
                    ? _nav.ComputeMove(self.Position, bomb, CsBotConst.DefuseSupportRadius, now,
                                       viaRoute: false, hullHeight: self.Height, forcePath: true)
                    : Vector3.zero;

                var coverWatch = _visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f
                    ? _lastSeenPos
                    : WithEyeHeight(NearestWatchPoint(bomb, AttackRoutes));
                ApplyAimAndFire(self, coverWatch, now, ref intent);

                if (now >= _lastUseLogAt)
                {
                    _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"{_name} 不是拆包人选 → 在 C4 外 {CsBotConst.DefuseSupportRadius:F0}m 掩护（距 C4 {dist:F1}m），" +
                        $"盯 T 来路（剩余 {_match.BombTimeLeft:F1}s）");
                }
                return true;
            }

            // 路上若看得见敌人，就"边走边盯边打"：不停下（停下就到不了包点），但不放弃警戒。
            SetState(CsBotState.Defuse, now);
            intent.State = CsBotState.Defuse;
            // viaRoute=false：包已经下了，目标是"冲到 C4"而不是继续走自己那条旧防守路线。
            // 还在外围（> 掩护半径）时强制按 A* 路径走：直线被墙挡住时局部避障会在包点外来回，
            // 实测一名 CT 在离 C4 7m 处耗掉整个引信；贴到近处再交回局部避障（细调方向更灵）。
            intent.Move = _nav.ComputeMove(self.Position, bomb, CsBotConst.DefuseStopRadius, now,
                                           viaRoute: false, hullHeight: self.Height,
                                           forcePath: dist > CsBotConst.DefuseSupportRadius);
            // 正对 C4 这一帧被判"四面都走不了"（炸弹落在掩体/箱子上，A* 求不出该格、直线与全部
            // 偏角候选都被挡）时，绕到弹侧面再进 —— 原地不动等于把引信站到烧完。
            if (intent.Move.sqrMagnitude < 0.0004f && dist > CsBotConst.DefuseStopRadius + 0.3f)
            {
                intent.Move = SidestepTowardBomb(self, bomb, dist, now);
            }

            var watch = _visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f
                ? _lastSeenPos
                : WithEyeHeight(bomb);
            ApplyAimAndFire(self, watch, now, ref intent);
            return true;
        }

        /// <summary>
        /// 正对 C4 走不动时的兜底：围着炸弹取 8 个落脚点（半径 <see cref="CsBotConst.DefuseSidestepRadius"/>），
        /// 选"离自己最近、且导航层真能给出方向"的那一个当这一帧的目的地。
        ///
        /// <para>为什么需要：包点里的 C4 常落在箱子/掩体边上，导航层对**炸弹所在格**求不出路径 ⇒ 退回直线；
        /// 直线与 <c>Avoid</c> 的一圈偏角又全被 <c>WalkableAhead</c> 判死 ⇒ <c>ComputeMove</c> 返回零向量。
        /// 实测形态是 CT 停在离 C4 7.0m 不动直到引信烧完（<c>到达 C4</c> 日志一次都没出现）。</para>
        /// </summary>
        private Vector3 SidestepTowardBomb(CsActor self, Vector3 bomb, float dist, float now)
        {
            var best = Vector3.zero;
            var bestD = float.MaxValue;
            for (var i = 0; i < 8; i++)
            {
                var ang = i * Mathf.PI * 0.25f;
                var probe = bomb + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * CsBotConst.DefuseSidestepRadius;
                var d = probe - self.Position;
                d.y = 0f;
                var pd = d.magnitude;
                if (pd >= bestD) continue;

                var mv = _nav.ComputeMove(self.Position, probe, CsBotConst.DefuseStopRadius, now,
                                          viaRoute: false, hullHeight: self.Height, forcePath: true);
                if (mv.sqrMagnitude < 0.0004f) continue;
                bestD = pd;
                best = mv;
            }

            if (best.sqrMagnitude > 0.0004f && now >= _lastUseLogAt)
            {
                _lastUseLogAt = now + CsBotConst.StateLogMinInterval * 2f;
                Game.Logger.Info(Tag,
                    $"{_name} 直冲 C4 被挡（距 C4 {dist:F1}m）→ 绕侧面再进（取 {CsBotConst.DefuseSidestepRadius:F1}m 落脚点）");
            }
            return best;
        }

        /// <summary>T 的进攻来路（CT 掩护拆包时盯这些方向）。</summary>
        private static readonly string[] AttackRoutes = { CsMarkers.TAttackA, CsMarkers.TAttackB };

        /// <summary>CT 的回防来路（T 守包时盯这些方向）。</summary>
        private static readonly string[] DefendRoutes = { CsMarkers.CTDefendA, CsMarkers.CTDefendB };

        /// <summary>
        /// 本人是否本轮唯一的拆弹人选 = **离 C4 最近的存活 CT**（并列取 Id 小者）。
        ///
        /// <para>为什么必须选举：全部存活 CT 都走"冲 C4 + 按 E"会让全队挤在爆点上 ——
        /// 既没人看 T 的来路，也会被同一颗手雷/同一串扫射一起收走。原版 CT 的形态是"一个人拆、其余人架枪"。</para>
        ///
        /// <para>缓存 <see cref="CsBotConst.DefuseElectInterval"/> 秒：C4 位置固定，选举只在"最近的人死掉"时变。</para>
        /// </summary>
        private bool IsElectedDefuser(CsActor self, float now)
        {
            if (self.Team != CsTeam.CT || !_match.BombPlanted) return false;
            if (_defuserElectAt > 0f && now < _defuserElectAt) return _defuserElected;

            var bomb = _match.BombPosition;
            var actors = _match.Actors;
            var bestId = 0L;
            var bestD2 = float.MaxValue;

            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || a.Team != CsTeam.CT) continue;

                var dv = a.Position - bomb;
                dv.y = 0f;
                var d2 = dv.sqrMagnitude;
                if (d2 < bestD2 - 1e-4f || (Mathf.Abs(d2 - bestD2) <= 1e-4f && a.Id < bestId))
                {
                    bestD2 = d2;
                    bestId = a.Id;
                }
            }

            _defuserElected = bestId != 0L && bestId == self.Id;
            _defuserElectAt = now + CsBotConst.DefuseElectInterval;
            return _defuserElected;
        }

        /// <summary>
        /// 离 <paramref name="origin"/> 最近的、该组标记里的点（水平距离 ≤ <see cref="CsBotConst.GuardWatchRange"/>）。
        /// 取不到就返回 <paramref name="origin"/> 本身（保证调用方拿到的瞄点非零）。
        /// </summary>
        private Vector3 NearestWatchPoint(Vector3 origin, string[] markers)
        {
            if (_map == null || !_map.IsLoaded || markers == null) return origin;

            var best = float.MaxValue;
            var found = false;
            var result = origin;

            for (var m = 0; m < markers.Length; m++)
            {
                var pts = _map.Points(markers[m]);
                if (pts == null) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - origin;
                    d.y = 0f;
                    var dist = d.magnitude;
                    if (dist > CsBotConst.GuardWatchRange || dist >= best) continue;
                    best = dist;
                    result = pts[i];
                    found = true;
                }
            }

            return found ? result : origin;
        }

        /// <summary>
        /// 本轮计划不可用时退化为"全场离自己最近的包点标记"。
        ///
        /// <para>抽出来的唯一理由：<see cref="Engage"/> 的"持包压上"要用**同一份**取值
        /// （不是第二套口径）—— 它与 <see cref="TryPlantOrPickup"/> 下行处的 <c>aim</c> 是同一个算式，
        /// 那一边改成调用本方法，保证两边永远一致。</para>
        /// <returns>false = 地图未加载 / 没有可用的包点标记（调用方退回原行为）。</returns>
        /// </summary>
        private bool TryGetPlantAim(CsActor self, out Vector3 aim)
        {
            aim = Vector3.zero;
            if (self == null || _map == null || !_map.IsLoaded) return false;

            var near = NearestBombsitePoint(self.Position, out var zoneDist, out _);
            var dest = _goalValid ? _goalPos : near;

            // 本轮目标点本身就是包点里的标记点 ⇒ 下包点就用它（与计划一致）；
            // 目标点不在判定球里（路线终点 / 巡逻点）⇒ 退化为离它最近的包点标记。
            NearestBombsitePoint(dest, out var destSiteDist, out _);
            var anchor = destSiteDist <= CsBotConst.SiteRadius
                ? dest
                : NearestBombsitePoint(dest, out _, out _);

            aim = ResolvePlantPoint(self.Position, anchor, zoneDist <= CsBotConst.SiteRadius);
            return aim.sqrMagnitude > 0.0001f;
        }

        /// <summary>
        /// 下包点：**在 <paramref name="anchor"/> 所属的那个包点里**取离它最近、且（<paramref name="sameFloor"/>
        /// 为真时）与自身同层的标记点。
        /// <para>只在该包点内选 —— 跨包点最近会选中对面的点（站在 B 却把下包点定到 A）。
        /// 同层过滤只在"已经走到包点附近"时才生效：从出生点看，包点标记都在脚下，楼层差没有意义。</para>
        /// <para>没有同层点时退回该包点里离 <paramref name="anchor"/> 最近的标记点（不比改动前更差）。</para>
        /// </summary>
        private Vector3 ResolvePlantPoint(Vector3 selfPos, Vector3 anchor, bool sameFloor)
        {
            var siteMarker = (string)null;
            var bestAnchorDist = float.MaxValue;

            for (var s = 0; s < 2; s++)
            {
                var marker = s == 0 ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
                var pts = _map.Points(marker);
                if (pts == null || pts.Length == 0) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - anchor;
                    d.y = 0f;
                    var len = d.magnitude;
                    if (len < bestAnchorDist)
                    {
                        bestAnchorDist = len;
                        siteMarker = marker;
                    }
                }
            }

            if (siteMarker == null) return anchor;
            var sitePts = _map.Points(siteMarker);
            if (sitePts == null || sitePts.Length == 0) return anchor;

            var best = anchor;
            var bestDist = float.MaxValue;
            var found = false;

            for (var pass = 0; pass < 2; pass++)
            {
                var requireFloor = sameFloor && pass == 0;
                for (var i = 0; i < sitePts.Length; i++)
                {
                    if (requireFloor && Mathf.Abs(sitePts[i].y - selfPos.y) > CsBotConst.PlantSameFloorHeight) continue;
                    if (IsBannedGoal(sitePts[i])) continue;

                    var d = sitePts[i] - anchor;
                    d.y = 0f;
                    var len = d.magnitude;
                    if (len >= bestDist) continue;

                    bestDist = len;
                    best = sitePts[i];
                    found = true;
                }

                if (found) return best;
            }

            return anchor;
        }

        /// <summary>T 下包 / 捡包分支（`intent` 必须 <c>ref</c>，理由同 <see cref="TryBombObjective"/>）。</summary>
        private bool TryPlantOrPickup(CsActor self, ref CsBotIntent intent, float now)
        {
            if (_match.BombPlanted) return false;   // 包已下 → 交给 Advance 守包点

            if (self.HasBomb)
            {
                //   （与 CsBomb.IsInBombsite 同口径，见 NearestBombsitePoint 注释）。
                //   停步半径不能取 SiteRadius（= 判定半径）：那会让机器人正好停在判定球面上，
                //      CanPlant 的 IsInBombsite 随浮点误差真假 ⇒ 表现为"到了又走了"。
                var near = NearestBombsitePoint(self.Position, out var zoneDist, out var zoneName);

                if (!_goalValid)
                {
                    RateWarn("plant.nogoal",
                        $"{_name} 拿着 C4 但没有可用包点标记（本轮计划无目标）→ 只能朝最近的包点标记走");
                }

                if (!TryGetPlantAim(self, out var aim)) aim = near;

                var toAim = aim - self.Position;
                toAim.y = 0f;
                var atSpot = toAim.magnitude <= CsBotConst.PlantStopRadius && zoneDist <= CsBotConst.SiteRadius;

                if (atSpot)
                {
                    // 敌人看得见 / 刚丢视野且离得近 ⇒ 不许当着人下包（先交给交战分支）。
                    if (EnemyNearPlant(self, now, out var enemyDist, out var enemyAge))
                    {
                        if (now >= _lastPlantGateLogAt)
                        {
                            _lastPlantGateLogAt = now + CsBotConst.StateLogMinInterval;
                            Game.Logger.Info(Tag,
                                $"[C4] {_name} 放弃下包：{(enemyAge <= 0f ? "看得见敌人" : $"距上次看见敌人 {enemyAge:F1}s")} " +
                                $"水平距离 {enemyDist:F1}m ≤ {CsBotConst.PlantEnemyAbortRange:F0}m → 先应战");
                        }
                        return false;
                    }

                    SetState(CsBotState.Plant, now);
                    intent.State = CsBotState.Plant;
                    intent.Move = Vector3.zero;   // CanPlant 要求"停下"（CsBomb.IsMoving ⇒ 不许下包）
                    intent.SwitchTo = CsWeapons.C4;   // 下包前先切出 C4（CanPlant 要求手持 C4）
                    intent.Use = false;
                    intent.Fire = true;               // 按住左键 = 下包
                    intent.AimPoint = WithEyeHeight(aim);

                    if (now >= _lastUseLogAt)
                    {
                        _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                        Game.Logger.Info(Tag,
                            $"[C4] {_name} 已站到下包点 {zoneName}（距同层包点标记 {toAim.magnitude:F2}m ≤ " +
                            $"{CsBotConst.PlantStopRadius:F1}m，判定球内 {zoneDist:F2}m ≤ {CsBotConst.SiteRadius:F0}m）" +
                            $"→ 下包决策：切出 C4 + 停住 + 按住左键（需 {CsConst.PlantTime:F1}s）");
                    }
                    return true;
                }

                // 还没站到下包点（包括"进了判定球但只在球面附近"）：继续走进去；站到点前先切出 C4，
                // 免得到位那一帧手里还是步枪（CanPlant 会判"未切出 C4"）。

                SetState(CsBotState.Plant, now);
                intent.State = CsBotState.Plant;
                intent.SwitchTo = CsWeapons.C4;
                intent.Move = _nav.ComputeMove(self.Position, aim, CsBotConst.PlantStopRadius, now,
                                               hullHeight: self.Height);
                intent.AimPoint = LookPoint(self, WithEyeHeight(_nav.CurrentTarget(self.Position, aim)));

                // 求出的方向是零向量 = 本帧不动。两种情况：
                //   ① 已经到了停步半径（合法，交回调用方按"到了"处理）；
                //   ② **目标点与自身重合**（包点标记缺失时 NearestBombsitePoint 返回 pos 本身，
                //      TryGetPlantAim 也退化成 pos）⇒ 停在 Plant 态一动不动、下包永远起不来。
                // ② 必须留痕并交回推进分支（路线推进 + 卡住自恢复都在那里），不许静默冻结。
                if (intent.Move.sqrMagnitude < 0.0004f)
                {
                    var dAim = aim - self.Position;
                    dAim.y = 0f;
                    if (dAim.magnitude < CsBotConst.PlantStopRadius)
                    {
                        if (now >= _lastUseLogAt)
                        {
                            _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                            Game.Logger.Warn(Tag,
                                $"[C4] {_name} 持包但本帧求不出可走方向：下包目标 ({aim.x:F1},{aim.z:F1}) " +
                                $"与自身距离 {dAim.magnitude:F2}m ≤ 停步半径 {CsBotConst.PlantStopRadius:F1}m，" +
                                $"而离最近包点标记 {zoneDist:F2}m（判定半径 {CsBotConst.SiteRadius:F0}m）" +
                                "（标记缺失或目标坐标退化）→ 本帧交回推进分支，不停在原地");
                        }
                        return false;
                    }
                }

                // 持包推进是每 tick 都会走到的常态分支：按时间闸留一条现场，不逐 tick 打印。
                if (CloverEngine.LogThrottle.ShouldLog("bot.c4.charge:" + _name, 30f))
                {
                    Game.Logger.Info(Tag,
                        $"[C4] {_name} 携带 C4 走向下包点：目标={aim} 距目标 {toAim.magnitude:F2}m " +
                        $"（停步半径 {CsBotConst.PlantStopRadius:F1}m，最近包点标记 {zoneDist:F2}m）");
                }
                return true;
            }

            // 持包者阵亡 → 包掉在地上：**只派离 C4 最近的那一个 T** 去捡（其余人继续交战/推进，不许全队扑向同一个点）。
            //
            //   ① 被选中的人**往往不是离 C4 最近的**（捡包要多走很多路，容易在途中被打死）；
            //   ② 叠加"交战中直接走 Engage"的路径（见 CommitTacticalDecision 的新分支）⇒ 最近的 T 若编号不整除，
            //   现在准入量法 = 选举（全场**唯一**一人为真），与 `CommitTacticalDecision` 用的是同一个判据。
            if (!IsElectedBombHunter(self)) return false;
            if (AnyTCarriesBomb()) return false;

            var dropped = _match.BombPosition;
            if (dropped.sqrMagnitude < 1f) return false;   // 还没人拿过包 → 位置无意义（契约：未下包时返回 LastKnownPosition）

            var dd = dropped - self.Position;
            dd.y = 0f;
            if (dd.magnitude <= CsBotConst.PickupStopRadius)
            {
                intent.State = CsBotState.Patrol;
                intent.Move = Vector3.zero;
                intent.AimPoint = WithEyeHeight(dropped);
                return true;    // 模拟会在 1.2m 内自动拾取
            }

            // 捡包者卡死（一直走不到 / 距离不缩）⇒ 让位给下一个离 C4 最近的 T，自己回去交战/推进。
            if (PickupHunterStalled(self, now, dd.magnitude)) return false;

            intent.State = CsBotState.Patrol;
            //   走 A* 路径而不是"朝落点直线 + 局部避障"：落点常在墙后几十米，直线恒被挡，
            //   避障只能在原地绕；C4 是静止目标，没有"边打边走"的必要。
            intent.Move = _nav.ComputeMove(self.Position, dropped, CsBotConst.PickupStopRadius, now,
                                           viaRoute: false, hullHeight: self.Height, forcePath: true);
            intent.AimPoint = WithEyeHeight(dropped);

            // 每个 bot 5s 最多一条：捡包是每 tick 都走到的常态分支，逐次打印只会刷屏。
            if (CloverEngine.LogThrottle.ShouldLog("bot.c4.pickup:" + _name, 5f))
            {
                Game.Logger.Info(Tag,
                    $"[C4] {_name} 去捡掉落的 C4：到落点 {dd.magnitude:F2}m（本人是场上离 C4 最近的 T，其余 T 继续交战）");
            }
            return true;
        }

        private bool AnyTCarriesBomb()
        {
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive) continue;
                if (a.Team != CsTeam.T) continue;
                if (a.HasBomb) return true;
            }
            return false;
        }

        /// <summary>
        ///
        /// <para>三条同时成立才为真：</para>
        /// <list type="number">
        /// <item>本人在世、本方为 T；</item>
        /// <item>场上**确实没有任何 T 持 C4**，且 C4 **掉在地上**：判据 = <c>BombPlanted == false</c>
        /// 且 <c>BombPosition</c> 有意义。这里用的是**现有 API 的既有语义**、不新增状态：
        /// <c>ICsMatch.BombPosition</c> 在未安放时返回 <c>CsBomb.LastKnownPosition</c>，而
        /// <c>CsBomb.OnCarrierLost</c> 正是把**掉落点**写进 LastKnownPosition（且 <c>CsBomb.Reset</c>
        /// 每回合把它清零，<c>sqrMagnitude &lt; 1</c> 即"本回合还没人拿过包"）；
        /// "有没有人持包" = <see cref="AnyTCarriesBomb"/>（与 <c>CsBomb.CarrierId</c> 同一事实的两面）；</item>
        /// <item>自己是**所有在世的 T **bot** 里离 C4（水平距离）最近的那一个，并列时取
        /// <see cref="CsActor.Id"/> 小的 —— 唯一且确定。</item>
        /// </list>
        ///
        /// <para>为什么选举范围只算 bot（<c>a.IsBot</c>）：本方法是 bot 的决策器，只能分配 bot 的行为；
        /// 把本地人类选手算进选举，会出现"最近的是人类 ⇒ 全体 bot 都不去捡"——
        /// 那等于把这条分支再次变成"没人捡"。人类若自己捡起，<see cref="AnyTCarriesBomb"/> 立刻为真，bot 自动让位。</para>
        ///
        /// <para>本方法不改变"谁能不能捡包"：真正的拾取仍由模拟每帧的
        /// <c>CsBomb.TryPickupDropped</c>（1.2m 内自动拾取）完成，本方法只决定"**谁**该往掉落点走"。</para>
        /// </summary>
        private bool IsElectedBombHunter(CsActor self)
        {
            if (self == null || !self.IsAlive) return false;
            if (self.Team != CsTeam.T) return false;
            if (_match.BombPlanted) return false;

            var dropped = _match.BombPosition;
            if (dropped.sqrMagnitude < 1f) return false;   // 本回合还没人拿过包 → 位置无意义
            if (AnyTCarriesBomb()) return false;           // 有人拿着（含自己刚捡起）→ 掉落分支不成立

            SyncPickupBanRound();

            // 弃权者本回合**恒定落选**；唯一的例外 = 所有在世 T bot 都已弃权（否则没人捡包），
            // 此时最近者隔一个完整的捡包窗口再试 —— 不设这个间隔就会每秒把同一人重新选回来，
            // 表现是"放弃 → 1s 后去捡 → 再放弃"的抖动与刷屏。
            if (_pickupBanned.Contains(self.Id))
            {
                // 回落只在"所有在世的 T bot 都已弃权"时成立：此时最近者继续前往（不再判弃权、
                // 不再打第二条 Warn），否则谁都不去 = 整回合没人捡包。
                if (!AllAliveTBotsBanned()) return false;
                return NearestAliveTBotIs(self.Id, includeBanned: true);
            }

            return NearestAliveTBotIs(self.Id, includeBanned: false);
        }

        /// <summary>把弃权名单同步到当前回合（回合号变化 ⇒ 清空；所有 brain 共用同一份）。</summary>
        private void SyncPickupBanRound()
        {
            if (ReferenceEquals(_pickupBanMatch, _match) && _pickupBanRound == _match.RoundNumber) return;
            _pickupBanMatch = _match;
            _pickupBanRound = _match.RoundNumber;
            _pickupBanned.Clear();
        }

        /// <summary>在世的 T bot 是否**全部**已在弃权名单里（没有任何一只时返回 false）。</summary>
        private bool AllAliveTBotsBanned()
        {
            var actors = _match.Actors;
            var any = false;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot || a.Team != CsTeam.T) continue;
                any = true;
                if (!_pickupBanned.Contains(a.Id)) return false;
            }
            return any;
        }

        /// <summary>离 C4 最近的在世 T bot 是不是 <paramref name="selfId"/>（并列取 Id 小者，唯一且确定）。</summary>
        private bool NearestAliveTBotIs(long selfId, bool includeBanned)
        {
            var dropped = _match.BombPosition;
            var actors = _match.Actors;
            var bestId = 0L;
            var bestD2 = float.MaxValue;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot || a.Team != CsTeam.T) continue;
                if (!includeBanned && _pickupBanned.Contains(a.Id)) continue;

                var dv = a.Position - dropped;
                dv.y = 0f;
                var d2 = dv.sqrMagnitude;
                if (d2 < bestD2 - 1e-4f || (Mathf.Abs(d2 - bestD2) <= 1e-4f && a.Id < bestId))
                {
                    bestD2 = d2;
                    bestId = a.Id;
                }
            }

            return bestId != 0L && bestId == selfId;
        }

        /// <summary>除自己以外，是否还有在世的 T bot 离 C4 比 <paramref name="maxDist"/> 更近。</summary>
        private bool AnyOtherAliveTBotCloserThan(long selfId, float maxDist)
        {
            var dropped = _match.BombPosition;
            var actors = _match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot || a.Team != CsTeam.T || a.Id == selfId) continue;

                var dv = a.Position - dropped;
                dv.y = 0f;
                if (dv.magnitude < maxDist) return true;
            }

            return false;
        }

        /// <summary>
        /// 捡包者是否应该放弃：连续 <see cref="CsBotConst.PickupGiveUpSeconds"/> 秒到落点距离没有
        /// 缩短 <see cref="CsBotConst.PickupProgressMeters"/> 米以上，或导航口径走不到落点。
        /// <para>放弃后把本 bot 记入**本回合**的弃权名单（全队所有 brain 共用同一份选择结果 ⇒
        /// 下一次选举必然换到下一个离 C4 最近的 T），自己回到交战/推进。</para>
        /// </summary>
        private bool PickupHunterStalled(CsActor self, float now, float dist)
        {
            // 本回合已经弃过权（只可能是"全员弃权 → 最近者再试"这一条回落）：不再判"走不到/不收敛"，
            // 也不再打第二条 Warn —— 弃权结论在本回合内不变。
            SyncPickupBanRound();
            if (_pickupBanned.Contains(self.Id)) return false;

            // 中间隔了别的分支（交战）⇒ 计时作废，只统计"这连续一段真的在往落点走"的时间。
            if (_pickupLastTick > 0f && now - _pickupLastTick > 2f) _pickupSince = 0f;
            _pickupLastTick = now;

            if (_pickupSince <= 0f)
            {
                _pickupSince = now;
                _pickupBestDist = dist;
                _pickupKeepCount = 0;
                _nextPickupReachCheckAt = now + CsBotConst.PickupReachCheckInterval;
                return false;
            }

            if (dist < _pickupBestDist - CsBotConst.PickupProgressMeters)
            {
                _pickupBestDist = dist;
                _pickupSince = now;
            }

            string reason = null;
            if (now >= _nextPickupReachCheckAt)
            {
                _nextPickupReachCheckAt = now + CsBotConst.PickupReachCheckInterval;
                if (!_nav.CanReach(self.Position, _match.BombPosition))
                    reason = ReachFailReason;
            }

            var stalledFor = now - _pickupSince;
            if (reason == null && stalledFor < CsBotConst.PickupGiveUpSeconds) return false;
            if (reason == null)
                reason = $"{stalledFor:F1}s 内到落点距离没有缩短 {CsBotConst.PickupProgressMeters:F1}m";

            // 换人只在"确实有人更近"时做：走不动的人 40m、下一个候选 48m 时换人等于把同一个回合
            // 交给更远的人重走一遍，本回合再没人能捡到包。
            // 兜底上限：连续两个窗口都没进展、且一直没人更近 ⇒ 第三个窗口起不再钉在原地
            // （否则落点真的走不到时，这只 T 会整回合丢掉交战与下包，只为追一个捡不到的包）。
            if (reason != ReachFailReason && _pickupKeepCount < CsBotConst.PickupKeepWindows &&
                !AnyOtherAliveTBotCloserThan(self.Id, dist - CsBotConst.PickupHandoffMinGain))
            {
                _pickupKeepCount++;
                _pickupSince = now;
                _pickupBestDist = dist;
                RateWarn("pickup.keep",
                    $"[C4] {_name} 捡包 {stalledFor:F1}s 无进展（{reason}），但场上没有更近的活 T" +
                    $"（本人距落点 {dist:F1}m，换人门槛 {CsBotConst.PickupHandoffMinGain:F0}m）→ 本人继续去捡" +
                    $"（第 {_pickupKeepCount}/{CsBotConst.PickupKeepWindows} 次）");
                return false;
            }

            _pickupSince = 0f;
            _pickupBestDist = float.MaxValue;
            _pickupBanned.Add(self.Id);

            Game.Logger.Warn(Tag,
                $"[C4] {_name} 放弃捡包：{reason}（当前距落点 {dist:F1}m）→ 换下一个离 C4 最近的 T，" +
                "自己回交战/推进（本回合不再参与捡包）");
            return true;
        }

        // ==================================================================
        //  推进 / 守点
        // ==================================================================
        private CsBotIntent Advance(CsActor self, CsBotIntent intent, float now)
        {
            if (!_goalValid)
            {
                // 没有目标点（地图标记全缺）→ 不乱走；有听觉线索就朝声源看一眼
                SetState(CsBotState.Idle, now);
                intent.State = CsBotState.Idle;
                intent.Move = Vector3.zero;
                intent.AimPoint = _heardFresh ? WithEyeHeight(_heardPos) : LookAhead(self);
                RateWarn("advance.nogoal", $"{_name} 没有目标点（地图标记缺失）→ 原地警戒");
                return intent;
            }

            var radius = _goalIsSite ? CsBotConst.SiteRadius : CsBotConst.DefaultObjectiveRadius;

            //   不要把"到没到包点"与"站没站上那个标记点"耦合在一起：目标点是包点里的**一个标记点**，
            //   站不到它 ≠ 没进包点（判据用 `CsBomb.IsInBombsite` 同口径）；
            //   "到了没有"只作用于**当前守位**（在 Camp 里按 CampSpotArriveRadius 判，且不阻塞换位计时）。
            if (_goalIsSite && self.Team == CsTeam.CT && _holdSpots.Count >= 2 &&
                InsideHoldSite(self, out var siteNow) && siteNow == _holdSiteMarker)
            {
                if (_goalHoldUntil <= 0f) _goalHoldUntil = now + ObjectiveHoldSeconds();
                return Camp(self, intent, _goalPos, now);
            }

            if (BotNavigator.Arrived(self.Position, _goalPos, radius))
            {
                // 刚到达 → 起算"守点时长"（守够就换目标，见 Camp 第 ④ 步）
                if (_goalHoldUntil <= 0f) _goalHoldUntil = now + ObjectiveHoldSeconds();
                return Camp(self, intent, _goalPos, now);
            }

            // 按难度间隔检查"朝目标有没有进展"，没进展就用当前位置重排路线（真实的重寻路）
            MaybeRepath(self, now);

            SetState(CsBotState.Patrol, now);
            intent.State = CsBotState.Patrol;
            intent.Move = _nav.ComputeMove(self.Position, _goalPos, radius, now,
                                           hullHeight: self.Height);
            intent.AimPoint = LookPoint(self, WithEyeHeight(_nav.CurrentTarget(self.Position, _goalPos)));
            return intent;
        }

        /// <summary>守点：站住 + 周期性换位（"会换位"），听到动静会去看一眼（不离开守点太远）。</summary>
        private CsBotIntent Camp(CsActor self, CsBotIntent intent, Vector3 goal, float now)
        {
            SetState(CsBotState.Camp, now);
            intent.State = CsBotState.Camp;

            // ① 听到动静 → 去看一眼（且声源不能离守点太远）。
            //    包点守卫（HoldSiteActive）不走这一支：守点的契约是"站在守位上盯着来路"，
            //    跟着声音挪步会让守卫在包点里打转、镜头离开入口方向（实测守点采样里 4 成样本朝向偏离入口 35°+）。
            //    视野内的敌人仍由感知/交战分支处理，这一支只影响"没看见人时要不要循声走"。
            if (!HoldSiteActive(self) && _heardFresh && now - _heardTime <= CsBotConst.NoiseMemorySeconds &&
                BotNavigator.Arrived(_heardPos, goal, CsBotConst.CampRepositionRadius * 2f))
            {
                var dir = _nav.ComputeMove(self.Position, _heardPos, CsBotConst.WaypointArriveRadius, now,
                                           viaRoute: false, hullHeight: self.Height);
                if (dir.sqrMagnitude > 0.0001f)
                {
                    intent.Move = dir;
                    intent.AimPoint = WithEyeHeight(_heardPos);
                    return intent;
                }
            }

            //   这里**不会**走下面的 ③"守够时间就换目标" ——
            //   守卫整回合留在自己这个包点，只是在包点内的 2~3 个守位之间轮换
            //   （守 ObjectiveHoldSeconds（2.5~4.8s）就换路线/去巡逻/回出生点会退化成"原地踱步、警不去守点"）。
            if (HoldSiteActive(self))
            {
                if (_holdSwapAt <= 0f) _holdSwapAt = now + CsBotConst.HoldSwapSeconds;   // 到达才起算换位计时
                HoldRotate(self, now);

                var spot = _holdSpots[_holdSlot];
                var ds = spot - self.Position;
                ds.y = 0f;
                if (ds.magnitude > CampSpotArriveRadius)
                {
                    intent.Move = _nav.ComputeMove(self.Position, spot, CampSpotArriveRadius, now,
                                                   viaRoute: false, hullHeight: self.Height);
                    // 挪守位途中也看着入口：瞄脚下那个点会让整段换位期间都是"低头走"，
                    // 到位后再转回来，观感就是"守点的人一直没看该看的方向"。
                    intent.AimPoint = WithEyeHeight(HoldWatchPoint(self, goal));
                    return intent;
                }

                intent.Move = Vector3.zero;
                intent.AimPoint = WithEyeHeight(HoldWatchPoint(self, goal));
                return intent;
            }

            // ② 周期性挪窝
            if (now >= _campPickAt)
            {
                _campPickAt = now + Mathf.Max(1f, _profile.RepathInterval * 2f);
                _campSpot = PickCampSpot(self, goal, out var ok);
                _campMoveUntil = ok ? now + CsBotConst.CampRepositionSeconds : 0f;
            }

            if (now < _campMoveUntil)
            {
                var delta = _campSpot - self.Position;
                delta.y = 0f;
                if (delta.magnitude > 0.6f)
                {
                    intent.Move = _nav.ComputeMove(self.Position, _campSpot, 0.6f, now,
                                                   viaRoute: false, hullHeight: self.Height);
                    intent.AimPoint = WithEyeHeight(_campSpot);
                    return intent;
                }
            }

            // ③' 角色 = **守点**（Anchor，CT 的 A/B 守位）：守点时长到点后**不换目标**，只把计时重新起算。
            //     守够 2.5~4.8s 就 ReplanObjective（换路线 / 去巡逻 / 回出生点），CT 守卫也每几秒改一次
            //     目的地 ⇒ 观感就是"全队都在乱走、没人真的守点"。
            //     这不是"站着不动"：本函数上面的守位表轮换（_holdSpots + _holdSwapAt）照常跑，
            //        玩家看到的是"在包点里换位置盯人"；也不影响卡住/包已下等其它换目标路径。
            //     CT 只要当前目标是包点（_goalIsSite）就持续守：B 点守卫是 Support 角色，旧条件按
            //     StaysOnObjective 只放行 Anchor ⇒ B 守卫每 ~4.8s 换目标离开包点，观感就是"没人守 B"。
            //     换位仍由守位表轮换负责，不是站着不动。
            if (_goalHoldUntil > 0f && now >= _goalHoldUntil && _goalIsSite && self.Team == CsTeam.CT)
            {
                var ensure = ObjectiveHoldSeconds();
                _goalHoldUntil = now + ensure;
                RateWarn("role.anchor.hold",
                    $"{_name}（角色={CsBotRoles.Label(_role)}）在包点 {_holdSiteMarker} 守满 {ensure:F1}s → " +
                    $"{ensure:F1}s 内**继续守**（不 ReplanObjective；守位 {_holdSpots.Count} 个、累计换位 {_holdSwapCount} 次）");
            }
            else if (_goalHoldUntil > 0f && now >= _goalHoldUntil)
            {
                //
                //   换目标"换了但等于没换"的空转闸（修用户报的 AI 乱跑）：
                //   候选被限死时（如「包未下不许离分工位」⇒ 只剩自己这个分工位可选），
                //   ReplanObjective 成功返回但新目标 == 旧目标 ⇒ 下一行 Advance 立刻判
                //   Arrived → 守点计时重起 → 到点再换 → 又是同一个点……实测以守点时长为周期
                //   空转（突破手 1.8s/次，日志同一句「站回分工位」连打几十遍，_replanCount 涨到 16+）。
                //   换不到新目标时**原地续一个守点周期**：守位表轮换（HoldRotate）照常跑，
                //   观感是在包点里换位盯人，而不是每 1.8s 假装重新出发一次。
                var prevGoal = _goalPos;
                if (ReplanObjective(self, $"守点 {ObjectiveHoldSeconds():F1}s 已到（{_difficulty}）", now))
                {
                    var delta = _goalPos - prevGoal;
                    delta.y = 0f;
                    if (delta.magnitude >= 1f)
                    {
                        return Advance(self, intent, now);   // 真换了新目标 → 照常出发
                    }

                    _goalHoldUntil = now + ObjectiveHoldSeconds();
                    RateWarn("replan.same",
                        $"{_name} 守点到时但候选受限（「包未下不离分工位」等闸门）⇒ 换到的还是同一个目标点 " +
                        $"({_goalPos})，原地续守 {ObjectiveHoldSeconds():F1}s（不重排路线，防空转）");
                }
            }

            // ④ 站定守点，盯住守的方向
            intent.Move = Vector3.zero;

            // C4 已下 → T 的"守包"是盯 **CT 的回防来路**，不是低头看包；
            // 盯着包点标记点本身会让一队人围着包站着，被从后面打死。
            if (_match.BombPlanted && self.Team == CsTeam.T)
            {
                var bomb = _match.BombPosition;
                var anchor = bomb.sqrMagnitude > 1f ? bomb : goal;
                intent.AimPoint = WithEyeHeight(NearestWatchPoint(anchor, DefendRoutes));
                return intent;
            }

            // CT 守点（守位表不可用、只剩单点守点时也走这里）→ 仍然盯着该包点的进攻来路。
            intent.AimPoint = WithEyeHeight(self.Team == CsTeam.CT ? HoldWatchPoint(self, goal) : goal);
            return intent;
        }

        private Vector3 PickCampSpot(CsActor self, Vector3 goal, out bool ok)
        {
            ok = false;
            var best = Vector3.zero;

            if (_map != null && _map.IsLoaded)
            {
                var patrol = _map.Points(CsMarkers.Patrol);
                if (patrol != null)
                {
                    var bestScore = float.MaxValue;
                    for (var i = 0; i < patrol.Length; i++)
                    {
                        var d = patrol[i] - goal;
                        d.y = 0f;
                        var dist = d.magnitude;
                        if (dist > CsBotConst.CampRepositionRadius) continue;

                        // 站得下（8 向半径采样）= 与移动解算同一判据。只看 WalkableAt（单格）时，
                        // 会把"紧贴墙壁的格子"选成纳窝点 → 走不过去 → 顶着墙被反复判卡住。
                        if (!_map.CanStand(patrol[i])) continue;

                        // 带随机项的择优：都取最近的话全队会挤在同一格里。
                        // **玩法**（决定 bot 站哪一格 ⇒ 谁先看到谁）⇒ 走本 bot 的 BotDecision 流。
                        var score = dist + _rngDecision.NextFloat() * CsBotConst.CampRepositionRadius;
                        if (score >= bestScore) continue;

                        bestScore = score;
                        best = patrol[i];
                        ok = true;
                    }
                }

                if (!ok)
                {
                    // 没有巡点标记 → 在守点周围随机取一个可走的点。
                    // **玩法**（同上）⇒ 同一个 BotDecision 流（ang 与 r 是"一条子流上连抽两次"）。
                    for (var i = 0; i < 6; i++)
                    {
                        var ang = _rngDecision.NextFloat() * 360f;
                        var r = CsBotConst.CampRepositionRadius * _rngDecision.NextFloat();
                        var cand = goal + Quaternion.Euler(0f, ang, 0f) * new Vector3(0f, 0f, r);
                        if (!_map.CanStand(cand)) continue;
                        best = cand;
                        ok = true;
                        break;
                    }
                }
            }

            if (!ok)
            {
                RateWarn("camp.spot", $"{_name} 找不到守点附近的驻点（{CsMarkers.Patrol} 缺失或都不可走）→ 原地守着");
            }

            return best;
        }

        // ==================================================================
        /// <summary>
        /// 自己**在不在某个包点区域内**（到该包点任一标记点 ≤ <see cref="CsBotConst.SiteRadius"/>），
        /// 并把命中的包点标记名带出来。
        ///
        /// <para>口径与 <c>CsBomb.IsInBombsite</c>（<c>Module/Match/CsBomb.cs:478</c>）同一份：
        /// 到**任一**包点标记的水平距离 ≤ <c>CsMarkers.BombsiteRadius</c>（= 7m）。</para>
        ///
        /// </summary>
        private bool InsideHoldSite(CsActor self, out string marker)
        {
            marker = null;
            if (_map == null || !_map.IsLoaded) return false;

            for (var s = 0; s < 2; s++)
            {
                var m = s == 0 ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
                var pts = _map.Points(m);
                if (pts == null) continue;

                for (var i = 0; i < pts.Length; i++)
                {
                    var d = pts[i] - self.Position;
                    d.y = 0f;
                    if (d.magnitude > CsBotConst.SiteRadius) continue;
                    marker = m;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 是否处于"包点守卫"模式：CT + 包未下 + 本轮目标是包点 + 该包点构造出了 ≥2 个可用守位。
        /// 任一不满足 ⇒ 走旧的"守够就换目标"路径（缺守位表时不许假装能多点分布）。
        /// </summary>
        private bool HoldSiteActive(CsActor self)
        {
            return self.Team == CsTeam.CT
                && !_match.BombPlanted
                && _goalIsSite
                && _holdSiteMarker != null
                && _holdSpots.Count >= 2
                && _holdSlot >= 0
                && _holdSlot < _holdSpots.Count;
        }

        /// <summary>到点就换到下一个守位（<see cref="CsBotHoldSpots.PickSlot"/> 取"离队友最远"的那个），并打一条带数字的 Info。</summary>
        private void HoldRotate(CsActor self, float now)
        {
            if (now < _holdSwapAt) return;

            _holdSwapAt = now + CsBotConst.HoldSwapSeconds;

            var next = CsBotHoldSpots.PickSlot(_holdSpots, _holdSlot, self, _match.Actors);
            if (next < 0 || next == _holdSlot) return;    // 守位只有 1 个 → 不"换"

            var from = _holdSlot;
            _holdSlot = next;
            _holdSwapCount++;

            // 守点换位按 HoldSwapSeconds 周期发生（每个守卫每轮都会走到）：按时间闸留一条现场。
            if (CloverEngine.LogThrottle.ShouldLog("bot.holdswap:" + _name, 15f))
            {
                Game.Logger.Info(Tag,
                    $"守点换位：{_name}（CT）包点 {_holdSiteMarker} 第 {_holdSwapCount} 次换位 " +
                    $"守位 {from + 1}→{next + 1}/{_holdSpots.Count} 新位置={_holdSpots[next]} " +
                    $"（下次换位 {CsBotConst.HoldSwapSeconds:F0}s 后）");
            }
        }

        /// <summary>
        /// 守卫的注视点 = **该包点的 T 进攻路线**（A ↔ <see cref="CsMarkers.TAttackA"/>、
        /// B ↔ <see cref="CsMarkers.TAttackB"/>）里离**该包点的标准下包点**最近的标记点 ——
        /// 敌人就是从那条路出来的。
        ///
        /// <para>为什么锚点取包点而不是自己脚下 / 当前守位：按自己位置取点会让注视方向随一步移动摆动；
        /// 按守位取点则在换位时把"最近的入口"换成另一个（A 长 / A 短两条来路相差约 180°），
        /// 表现为守卫每 8s 甩头半圈。按包点取 → 同一个包点整回合只有一个入口方向。</para>
        ///
        /// <para>包点归属：优先用 <see cref="_holdSiteMarker"/>；守位表没建起来（&lt;2 个可用守位）时
        /// 该字段为空，此时按目标点 / 自身位置离哪个包点标记更近反推，仍能给出入口方向。
        /// 标记缺失（地图数据不全）才退化为 <paramref name="goal"/>。</para>
        /// </summary>
        private Vector3 HoldWatchPoint(CsActor self, Vector3 goal)
        {
            if (_map == null || !_map.IsLoaded) return goal;

            var site = _holdSiteMarker;
            if (site == null)
            {
                var anchor = goal.sqrMagnitude > 0.0001f ? goal : self.Position;
                NearestBombsitePoint(anchor, out var siteDist, out var siteName);
                if (siteDist > CsBotConst.SiteRadius || siteName == null) return goal;
                site = siteName == "Bombsite A" ? CsMarkers.BombsiteA : CsMarkers.BombsiteB;
            }

            var route = site == CsMarkers.BombsiteB ? CsMarkers.TAttackB : CsMarkers.TAttackA;
            var pts = _map.Points(route);
            if (pts == null || pts.Length == 0) return goal;

            // 锚点取**该包点的标准下包点**（标记表第 0 个，A#0 / B#0），不是当前守位：
            // 同一个包点的入口因此是恒定的一个方向。按守位取会在换位时把"最近的入口"换成另一个
            // （包点有两条来路时 A 长 / A 短正好相差约 180°），表现为守卫每 8s 甩头半圈。
            var sitePts = _map.Points(site);
            var from = sitePts != null && sitePts.Length > 0 ? sitePts[0] : goal;

            var best = -1f;
            var bestAny = -1f;
            var watch = Vector3.zero;
            var watchAny = Vector3.zero;
            for (var i = 0; i < pts.Length; i++)
            {
                var d = pts[i] - from;
                d.y = 0f;
                var dist = d.magnitude;

                if (bestAny < 0f || dist < bestAny) { bestAny = dist; watchAny = pts[i]; }

                // 包点判定球内的路点不算"来路"：离守位只有几米，方位随一步移动就摆几十度。
                if (dist < CsBotConst.HoldWatchMinDistance) continue;
                if (best >= 0f && dist >= best) continue;
                best = dist;
                watch = pts[i];
            }

            if (best >= 0f) return watch;
            return bestAny >= 0f ? watchAny : goal;
        }

        // ==================================================================
        //  工具
        // ==================================================================
        private static Vector3 Forward(float yawDeg, float pitchDeg)
        {
            var yaw = yawDeg * Mathf.Deg2Rad;
            var pitch = pitchDeg * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Cos(pitch) * Mathf.Sin(yaw),
                Mathf.Sin(pitch),
                Mathf.Cos(pitch) * Mathf.Cos(yaw));
        }

        /// <summary>
        /// 行进朝向（无交战目标时）：注视点 = 行进方向上的 <see cref="CsBotConst.TravelAimDistance"/> 米处。
        ///
        /// <para><b>为什么不是"看路点"</b>：路点常常只有 1~3m 远，机器人横向 ±0.1m 的微动
        /// 就会让朝向摆 ±4°（观战视角逐帧抖）；推到 10m 后同样的微动只占 ~0.6°。
        /// 另外当注视方向与移动方向夹角 &gt; 56°（避障偏角、让行后退）时直接改看行进方向 ——
        /// 否则机器人就是"目光朝前、身体倒着走"。</para>
        ///
        /// <para>交战中不介入（朝向由 AimPoint = 敌人决定）；站定（Move 为零）也不介入
        /// （守点盯的方向是 HoldWatchPoint / 包点，不是"行进"）。</para>
        /// </summary>
        private void ApplyTravelFacing(CsActor self, ref CsBotIntent intent)
        {
            if (_state == CsBotState.Engage) return;
            if (_targetId != 0) return;

            var move = intent.Move;
            move.y = 0f;
            if (move.sqrMagnitude < 0.04f) return;
            move.Normalize();

            var aim = intent.AimPoint - self.EyePosition;
            aim.y = 0f;
            var dir = aim.sqrMagnitude > 0.01f ? aim.normalized : move;

            // 只在"确实背对行进方向"时才改看移动方向：侧移时跟随移动方向会把避障方向的逐帧跳动
            // 直接放大成朝向摆动（实测 p95 由 4.7°/帧 升到 17.3°/帧）。
            if (Vector3.Dot(dir, move) < CsBotConst.TravelFaceMoveDot) dir = move;

            intent.AimPoint = self.EyePosition + dir * CsBotConst.TravelAimDistance;
        }

        /// <summary>
        /// 瞄准平滑：把注视点方向的**逐帧跳变**按角速度上限分摊到多帧。
        ///
        /// <para>为什么在 Bot 侧做：<c>CsMatch.AimBotAt</c> 用 <c>MoveTowardsAngle(a.Yaw, wantYaw,
        /// profile.AimSpeedDegrees * dt)</c> 落角，而 <c>AimSpeedDegrees</c> 是 300~520°/s
        /// （<c>CsTypes.cs:171</c> Hard = 520）⇒ 一帧最多 8.7°；再叠加它每
        /// <c>CsMatchConst.BotAimErrorRefresh</c>（0.35s）重采样的 ±AimErrorDegrees，
        /// wantYaw 会瞬移一次，Yaw 就以 520°/s 追过去 —— 观战看到的就是"一顿一顿地摆头"。
        /// 这里让**我们提交的注视点**本身只按 ≤<see cref="CsBotConst.AimSmoothDegPerSec"/> 转，
        /// 于是 wantYaw 的移动速度被压到该上限；误差重采样那一档仍由 Match 层决定，Bot 侧管不到。</para>
        ///
        /// <para>大幅换向（&gt; <see cref="CsBotConst.AimSmoothFastAngle"/>，切目标/转身找敌人）
        /// 走快速档，不牺牲交战反应。</para>
        /// </summary>
        private void SmoothAim(CsActor self, ref CsBotIntent intent, float now)
        {
            var aim = intent.AimPoint - self.EyePosition;
            var dist = aim.magnitude;
            if (dist < 0.05f) return;
            var want = aim / dist;

            if (_aimSmoothActor != self.Id || _aimSmoothDir.sqrMagnitude < 0.5f)
            {
                _aimSmoothActor = self.Id;
                _aimSmoothDir = want;
                _aimSmoothAt = now;
                return;
            }

            var dt = Mathf.Clamp(now - _aimSmoothAt, 0.001f, 0.1f);
            _aimSmoothAt = now;

            var ang = Vector3.Angle(_aimSmoothDir, want);
            if (ang < 0.01f)
            {
                _aimSmoothDir = want;
                return;
            }

            var rate = ang > CsBotConst.AimSmoothFastAngle
                ? CsBotConst.AimSmoothFastDegPerSec
                : CsBotConst.AimSmoothDegPerSec;
            var maxStep = rate * dt;

            _aimSmoothDir = ang <= maxStep
                ? want
                : Vector3.RotateTowards(_aimSmoothDir, want, maxStep * Mathf.Deg2Rad, 0f);

            intent.AimPoint = self.EyePosition + _aimSmoothDir * dist;
        }

        /// <summary>
        /// 交战中的纯后退上限：连续后退（与朝向夹角 &gt; 120°）超过
        /// <see cref="CsBotConst.MaxBackpedalSeconds"/> 就改成横向侧移 —— 允许边打边侧移，
        /// 但不许整段背对目标倒着走。
        /// </summary>
        private void ApplyBackpedalCap(CsActor self, ref CsBotIntent intent, float now)
        {
            var move = intent.Move;
            move.y = 0f;
            if (move.sqrMagnitude < 0.04f)
            {
                _backpedalSince = 0f;
                return;
            }

            move.Normalize();
            if (Vector3.Dot(move, Forward(self.Yaw, 0f)) >= CsBotConst.BackpedalDot)
            {
                _backpedalSince = 0f;
                return;
            }

            if (_backpedalSince <= 0f)
            {
                _backpedalSince = now;
                return;
            }

            if (now - _backpedalSince < CsBotConst.MaxBackpedalSeconds) return;

            var yawRad = self.Yaw * Mathf.Deg2Rad;
            var right = new Vector3(Mathf.Cos(yawRad), 0f, -Mathf.Sin(yawRad));
            intent.Move = right * (Vector3.Dot(move, right) >= 0f ? 1f : -1f);
            _backpedalSince = 0f;

            if (now >= _nextBackpedalLogAt)
            {
                _nextBackpedalLogAt = now + CsBotConst.StateLogMinInterval;
                var target = _targetId != 0 ? _match.Find(_targetId) : null;
                var dist = -1f;
                if (target != null)
                {
                    var d = target.Position - self.Position;
                    d.y = 0f;
                    dist = d.magnitude;
                }

                Game.Logger.Info(Tag,
                    $"{_name} 交战纯后退超过 {CsBotConst.MaxBackpedalSeconds:F1}s → 改为横向侧移（距目标 {dist:F1}m）");
            }
        }

        /// <summary>保证非零的兜底注视点：沿当前朝向看 <see cref="CsBotConst.AimFallbackDistance"/> 米（眼高 1.62m &gt; 0，永不为零）。</summary>
        private static Vector3 LookAhead(CsActor self)
        {
            return self.EyePosition + Forward(self.Yaw, 0f) * CsBotConst.AimFallbackDistance;
        }

        private static Vector3 FallbackAim()
        {
            return new Vector3(0f, CsConst.EyeHeight, CsBotConst.AimFallbackDistance);
        }

        private static Vector3 LookPoint(CsActor self, Vector3 worldPoint)
        {
            if (worldPoint.sqrMagnitude > 0.0001f) return worldPoint;
            return LookAhead(self);
        }

        private static Vector3 WithEyeHeight(Vector3 groundPoint)
        {
            return groundPoint + Vector3.up * CsConst.EyeHeight;
        }

        private static Vector3 HorizontalDir(Vector3 from, Vector3 to)
        {
            var d = to - from;
            d.y = 0f;
            var m = d.magnitude;
            return m > 0.0001f ? d / m : Vector3.zero;
        }

        private void SetState(CsBotState leaf, float now)
        {
            //   移到状态表 + 转换守卫上 —— 顶层定 Idle/Patrol/Engage/Objective，目标层定 Approach/Plant/Hold/Defuse。
            var next = RouteLeafState(leaf);
            if (_state == next) return;

            var from = _state;
            _state = next;
            if (next == CsBotState.Engage) _engageEvent = true;

            // 进入交战必打（这是交战必打的证据）；**离开交战也必须必打**（这是"为什么打不起来"的关键证据：
            // 之前它走的是同一套 2s 降频，于是"什么时候退出的交战"被日志掩盖了）；
            // 其余状态切换降频。
            var leavingEngage = from == CsBotState.Engage && next != CsBotState.Engage;
            if (next == CsBotState.Engage || leavingEngage || now >= _lastStateLogAt)
            {
                _lastStateLogAt = now + CsBotConst.StateLogMinInterval;

                string extra;
                if (next == CsBotState.Engage && _targetId != 0)
                {
                    extra = $" 目标=actor {_targetId} 距离={(_visibleNow ? _closestVisibleDist.ToString("F1") + "m" : "已丢失")}";
                }
                else if (leavingEngage)
                {
                    extra = $"（退出原因：距上次看见目标 {now - _lastSeenTime:F2}s" +
                            $"，记忆时长 {CsBotConst.TargetMemorySeconds:F1}s，本 tick 可见={_visibleNow}，" +
                            $"目标={(_targetId == 0 ? "无" : _targetId.ToString())}）";
                }
                else
                {
                    extra = string.Empty;
                }

                Game.Logger.Info(Tag, $"{_name}（{_difficulty}）状态 {from} → {next}{extra}");
            }
        }

        /// <summary>
        /// 把叶子状态提交给两层状态机，并返回落定后的叶子状态。
        /// 映射（出处 = 规格 <c>…参考规格.md:128-129</c>「找目标（T 去炸弹点 / CT 去守卫点）」）：
        /// <c>Plant/Defuse/Camp</c> ⇒ 目标层对应状态 + 顶层 <c>Objective</c>；其余三个叶子即顶层状态本身。
        /// </summary>
        private CsBotState RouteLeafState(CsBotState leaf)
        {
            switch (leaf)
            {
                case CsBotState.Engage:
                    _fsm.Transition(CsBotFsmStates.Engage);
                    break;
                case CsBotState.Patrol:
                    _fsm.Transition(CsBotFsmStates.Patrol);
                    break;
                case CsBotState.Idle:
                    _fsm.Transition(CsBotFsmStates.Idle);
                    break;
                case CsBotState.Plant:
                case CsBotState.Defuse:
                case CsBotState.Camp:
                    _objectiveFsm.Transition(CsBotObjectiveStates.For(leaf));
                    _fsm.Transition(CsBotFsmStates.Objective);
                    break;
                default:
                    RateWarn("fsm.unmapped." + leaf,
                        $"{_name} 的状态 {leaf} 没有映射到状态机（CsBotBrain.RouteLeafState 漏了一支）→ 保持现状");
                    return _state;
            }

            return LeafState();
        }

        /// <summary>由两层状态机的 <c>Current</c> 推出对外叶子状态（意图/日志/自检用）。</summary>
        private CsBotState LeafState()
        {
            switch (_fsm.Current)
            {
                case CsBotFsmStates.Engage:
                    return CsBotState.Engage;
                case CsBotFsmStates.Idle:
                    return CsBotState.Idle;
                case CsBotFsmStates.Objective:
                    switch (_objectiveFsm.Current)
                    {
                        case CsBotObjectiveStates.Plant: return CsBotState.Plant;
                        case CsBotObjectiveStates.Defuse: return CsBotState.Defuse;
                        case CsBotObjectiveStates.Hold: return CsBotState.Camp;
                        default: return CsBotState.Patrol;
                    }
                default:
                    return CsBotState.Patrol;
            }
        }

        private void RateWarn(string key, string message)
        {
            _rateCounters.TryGetValue(key, out var n);
            n++;
            _rateCounters[key] = n;

            if (n == 1 || n % CsBotConst.LogRateEvery == 0)
            {
                Game.Logger.Warn(Tag, $"{message}（第 {n} 次）");
            }
        }
    }
}

