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
    public sealed partial class CsBotBrain
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

        /// <summary>卡住原因的分类条数（贴墙 / 让行 / 换目标）。</summary>
        private const int StuckWarnReasonCount = 3;
        private const int StuckWarnReasonWall = 0;
        private const int StuckWarnReasonYield = 1;
        private const int StuckWarnReasonReplan = 2;

        /// <summary>同一 bot 同一原因两条 Warn 的最小间隔（秒）。</summary>
        private const float StuckWarnDedupeSeconds = 30f;

        /// <summary>每个原因各自的下一次允许时刻（见 <see cref="TrackStuck"/>）。</summary>
        private readonly float[] _stuckReasonNextLogAt = new float[StuckWarnReasonCount];

        /// <summary>换目标那条 Warn 的下一次允许时刻（与 <see cref="TrackStuck"/> 同一 30s 闸）。</summary>
        private float _banWarnNextAt;

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
            _btRoot = BuildTree();
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
        /// 决策结果**提交给状态机**，行为树（<see cref="BuildTree"/>）再按它选分支 ——
        /// 分支选择权在状态机，不在 if 链的书写顺序。
        /// </summary>
        private string CommitTacticalDecision(CsActor self, float now)
        {
            if (!self.IsAlive) return ForceTopState(CsBotFsmStates.Idle);

            //
            // 依据（原版行为，不是本项目自创）：CS 1.6 里 T 会去捡掉在地上的 C4（bot 也会）——
            //   `[Match] ZBot 携带的 C4 掉落在 (-36.34, 0.00, -29.20)（T 走过去可拾取）`，
            //   随后**整回合无人捡**（`拾起了` = 0 条）⇒ 回合时间到、CT 取胜、A5 恒 0。
            //   机制：捡包写在 `TryPlantOrPickup` 里，而它只在 `branch != Engage` 时才被走到（见 CsBotBrain.Tree.cs 的 objective-commit）；
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
            var self = _match.Find(_id);

            if (self == null)
            {
                // actor 已被踢出（KickBot / 比赛停止）→ 返回中性意图 + 非零瞄点，等 BotModule 销毁 brain
                return new CsBotIntent
                {
                    State = CsBotState.Idle,
                    Move = Vector3.zero,
                    AimPoint = FallbackAim()
                };
            }

            _name = self.Name;
            RefreshProfile(self);
            MaybeResolveRound(self, now);

            // 本 tick 的上下文：树节点按它读写（见 CsBotBrain.Tree.cs）。
            _tickSelf = self;
            _tickIntent = new CsBotIntent { State = CsBotState.Idle, Move = Vector3.zero };
            _tickBranch = null;
            _tickSkipTail = false;

            _btRoot.Tick(this, dt, now);

            var intent = _tickIntent;
            if (_tickSkipTail) return intent;

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
            var goalDriven = _state == CsBotState.Patrol || _state == CsBotState.Camp || _state == CsBotState.Plant;

            //   告警按原因各自限频：一次卡住夹在两个窗口里会连着判好几轮，
            //   同一 bot 同一原因 30s 内只报一条（贴墙 / 让行 / 换目标）。
            var reason = mateBlocking ? StuckWarnReasonYield
                : goalDriven && _goalValid && (!reachable || _stuckAtGoalCount >= CsBotConst.UnreachableGoalStreak)
                    ? StuckWarnReasonReplan
                    : StuckWarnReasonWall;

            if (StuckWarnAllowed(reason, now))
            {
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


        /// <summary>跳跃发起冷却（秒）：贴着矮墙/箱沿反复跳只会在原地蹦。</summary>
        private const float JumpCooldown = 1.2f;


        /// <summary>判断用的跳跃预备距离（米）—— 卡住判定（StuckCheckInterval 0.5s × 5.4 m/s）之后再稍近一点。</summary>
        private const float JumpLookAhead = 1.6f;


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


        /// <summary>T 的进攻来路（CT 掩护拆包时盯这些方向）。</summary>
        private static readonly string[] AttackRoutes = { CsMarkers.TAttackA, CsMarkers.TAttackB };


        /// <summary>CT 的回防来路（T 守包时盯这些方向）。</summary>
        private static readonly string[] DefendRoutes = { CsMarkers.CTDefendA, CsMarkers.CTDefendB };


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


        /// <summary>
        /// 卡住告警闸：同一 bot 同一原因 30s 一条，且任意两条之间不少于
        /// <see cref="CsBotConst.StuckDiagLogInterval"/> 秒。返回 true 时调用方负责打完这一条。
        /// </summary>
        private bool StuckWarnAllowed(int reason, float now)
        {
            if (now < _nextStuckDiagLogAt || now < _stuckReasonNextLogAt[reason]) return false;
            _nextStuckDiagLogAt = now + CsBotConst.StuckDiagLogInterval;
            _stuckReasonNextLogAt[reason] = now + StuckWarnDedupeSeconds;
            return true;
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
