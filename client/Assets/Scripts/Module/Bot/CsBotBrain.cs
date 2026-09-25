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

        // ---- 交战细节 ----
        private float _wobbleRefreshAt;
        private Vector2 _wobble;
        private float _headRollAt;
        private bool _headRoll;
        private float _switchCooldownUntil;
        private float _strafeSign = 1f;
        private float _strafeFlipAt;

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
        /// <summary>进行中投掷的选择理由（写进日志）。</summary>
        private string _grenadeWhy;
        /// <summary>切到那颗雷的等待上限时刻（超时放弃，见 <see cref="CsBotConst.GrenadeSwitchTimeout"/>）。</summary>
        private float _grenadeSwitchDeadline;
        /// <summary>投完一颗雷后是否还要主动换回主武器（同类雷还有剩余时模拟不会自动换，见 <see cref="TryThrowGrenade"/>）。</summary>
        private bool _grenadeReturnToWeapon;
        /// <summary>"为什么没投掷"诊断已打过的 (回合, 原因) 组合（每回合每类原因最多一条，防刷屏）。</summary>
        private readonly HashSet<string> _grenadeDiagKeys = new HashSet<string>();

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
                !MustDefuseFirst(self) && !MustPlantFirst(self))
            {
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
            return intent;
        }

        private void RefreshProfile(CsActor self)
        {
            if (self.Difficulty == _difficulty) return;

            var from = _difficulty;
            _difficulty = self.Difficulty;
            _profile = CsBotProfile.For(_difficulty);

            // 验收表 B4：H 菜单改难度后，已存在的 bot 参数要**即时生效** —— 这条日志就是证据
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
            //   "现在谁持包"：实测持包者中途被打死会让后来的 bot 拿到 -1、先来的拿到 k ⇒ 槽位重合
            var carrierOrdinal = CsBotPlans.RoundCarrierOrdinal(
                _match.Actors, _match, self.Team, _match.RoundNumber);          // CT ⇒ -1（该函数内已判）
            var plan = CsBotPlans.For(self.Team, CsBotPlans.SlotOf(ordinal, carrierOrdinal), _match.RoundNumber);
            _planSlot = plan.Slot;

            var routeMarker = plan.RouteMarker;
            var siteMarker = plan.GoalMarker;
            _planRoute = routeMarker;
            _nav.SetRoute(_map, routeMarker, self.Position);

            //   目标点**不能只判"地图上有这个标记"**：该点可能落在"位图说可走、但从出生点
            //   按抬腿 ≤ 0.45m 扩张到不了"的格上 ⇒ 引擎 `AStar.Find` 直接 `badgoal` 返回 null
            //   ⇒ 退化成"朝 86m 外的点直线走" ⇒ 顶着墙原地卡到回合结束（实测全程位移 0.00m）。
            //   口径与运行时同一份：BotNavigator.CanReach = SnapToWalkable + WalkableCellHeightAware + AStar。
            if (!string.IsNullOrEmpty(siteMarker) && TryPickGoalPoint(self, siteMarker, plan.GoalOrdinal, out var site))
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
                $"槽位={_planSlot}/队内序号={_ordinal}{(self.HasBomb ? "(持C4)" : "")} 路线={_planRoute ?? "无"}" +
                $"（{CsBotPlans.RouteLabel(_planRoute)}） 首段路点={(HasFirstWaypoint ? FirstWaypoint.ToString() : "无")} 路点={_nav.RemainingWaypoints}" +
                $" 目标={( _goalValid ? _goalPos.ToString() : "无" )}（{CsBotPlans.GoalLabel(siteMarker)}#{plan.GoalOrdinal}）站点={_goalIsSite}" +
                $" 守点时长={ObjectiveHoldSeconds():F1}s 重寻路间隔={_profile.RepathInterval:F2}s");
        }

        /// <summary>
        /// 在标记 <paramref name="marker"/> 的点里按槽位序号挑一个**走得到**的目标点。
        ///
        /// <para><b>为什么要"按序号"而不是"取最近"</b>：一个包点标记里有 6~9 个点，两个槽位（T 槽位 0 与 1）
        /// 都去同一个包点 —— 若各自"取离自己最近的点"，两只 bot 从相邻出生点出发会**取到同一个点**
        /// （用户实测日志里 3 只 T bot 的目标点同为 <c>(-25.50, 0.00, 33.50)</c>、3 只同 <c>(34.50, 2.44, 29.50)</c>）。
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
                if (_map.CanStand(pts[i])) cand.Add(pts[i]);

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
                    var n = CsBotHoldSpots.Build(_map, _holdSiteMarker, _holdSpots);
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
        /// 于是机器人停在原地被反复判卡住（实测日志：剩余路点 0，每 0.5s 一条）。
        /// </summary>
        private void HandleStuck(CsActor self, float now)
        {
            if (!_nav.ConsumeStuck(out var moved, out var escalate)) return;
            if (!escalate) return;

            // 正在下包/拆包时不换目标（一移动进度就清零）；拆包任务的目标是 C4，换目标没有意义。
            if (self.UseProgress >= 0f) return;
            if (_match.BombPlanted && self.Team == CsTeam.CT) return;

            //   没有这道闸时：0.5s 一次判定 / 2 次即升级 ⇒ 最快 1.0s 就换一次目标，而每个新目标都在
            //   86m 外（实测 C4 push 行 d=86.68 恒定）⇒ 一次都没走完就换掉 ⇒ 目标反复横跳、净位移≈0。
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
            // 「路线家族」是同队的**共享资源**：计划表保证开局 4 只各占一格，轮转也必须遵守同一条互斥，
            // 否则一只卡住的 bot 会轮转进队友正走着的路（日志形态：两只**都活着**的 bot 同时走同一条
            // 'Route_T_*'，即用户看到的"路线都一样"）。
            var carrier = CsBotPlans.RoundCarrierOrdinal(_match.Actors, _match, self.Team, _match.RoundNumber);
            var taken = CsBotPlans.AliveSlotTable(_match.Actors, self.Team, carrier, _match.RoundNumber, null);
            var from = _planSlot >= 0 ? _planSlot : CsBotPlans.SlotOf(_ordinal, carrier);

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

                _nav.SetRoute(_map, routeMarker, self.Position);
                if (!_nav.HasRoute)
                {
                    RateWarn("replan.route.empty",
                        $"{_name} 换路线 '{routeMarker}'（槽位 {plan.Slot}）失败（地图上没有该标记的路点）→ 试下一条");
                    continue;
                }

                //   不直接用 `_nav.RouteEnd` / 裸包点标记点：这两者都可能是"可走但走不到"的格。
                var goal = Vector3.zero;
                var isSite = false;

                if (!string.IsNullOrEmpty(plan.GoalMarker) &&
                    TryPickGoalPoint(self, plan.GoalMarker, plan.GoalOrdinal, out var site))
                {
                    goal = site;
                    isSite = plan.GoalIsSite;
                }
                else if (TryPickReachable(self, _map.Points(routeMarker), 0f, false, out var end))
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

                _planSlot = plan.Slot;
                ApplyObjective(self, routeMarker, goal, isSite,
                    $"换一条路线（槽位 {plan.Slot}={plan.RouteLabel}→{plan.GoalLabel}#{plan.GoalOrdinal}；{reason}）", now);
                return true;
            }

            // ---- 退一档：4 条家族都被**活着的**队友占着（全队都活着 = 常态）⇒ 不抢队友的路，
            //      留在自己的槽位、只把**目标点**换成同一条路线上另一个点（序号也要与队友互斥）。----
            var mySlot = from;
            var myPlan = CsBotPlans.For(self.Team, mySlot, _match.RoundNumber);
            if (!string.IsNullOrEmpty(myPlan.RouteMarker))
            {
                _nav.SetRoute(_map, myPlan.RouteMarker, self.Position);
                if (_nav.HasRoute)
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

                    if (!picked && TryPickReachable(self, _map.Points(myPlan.RouteMarker), 0f, false, out var end))
                    {
                        goal = end;
                        picked = true;
                        RateWarn("replan.inslot.gofallback",
                            $"{_name} 留在槽位 {mySlot}（'{myPlan.RouteLabel}'）但目标标记 '{myPlan.GoalMarker}' " +
                            "上的序号都被占/走不到 → 退化为该路线上离自己最近的走得到的路点");
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

            return false;
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

            _nav.SetRoute(_map, CsMarkers.Patrol, self.Position);
            if (!_nav.HasRoute) return false;

            // 近的优先（不再"专挑最远"）：门口那个走得到的巡点，比 80m 外走不到的那个有用。
            if (!TryPickReachable(self, pts, CsBotConst.MinPatrolDistance, false, out var goal))
            {
                // 退化一层：去掉最小距离限制再问一次（"最近的走得到的点"口径），并把"为什么退化"写进日志
                if (!TryPickReachable(self, pts, 0f, false, out goal))
                {
                    RateWarn("replan.patrol.unreachable",
                        $"{_name} 的巡逻点**没有一个走得到**（'{CsMarkers.Patrol}' 标记点全没过 " +
                        $"BotNavigator.CanReach：位图可走但高度一致性层到不了 / 或真孤岛）→ 试其它目标");
                    return false;
                }

                RateWarn("replan.patrol.nofar",
                    $"{_name} 的巡逻点里没有「≥ {CsBotConst.MinPatrolDistance:F0}m 且走得到」的点 → " +
                    $"退化为最近的那个走得到的巡点 {goal}");
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

            for (var i = 0; i < markers.Length; i++)
            {
                var marker = markers[i];
                if (string.IsNullOrEmpty(marker)) continue;

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

            if (bestDist < 0f) return false;

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
        /// 求不出路径的点去追"，而 <c>EnsurePath</c> 失败后只会退化成直线走 ⇒ 顶着墙卡死
        /// （实测：全程 net 位移 0.00m、换目标 70 次全由 stuck-escalate 触发）。</para>
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
        private bool TryPickReachable(CsActor self, Vector3[] pts, float minDistance, bool farthest, out Vector3 point)
        {
            point = Vector3.zero;
            if (pts == null || pts.Length == 0) return false;
            if (_map == null || !_map.IsLoaded) return false;

            var best = -1f;
            for (var i = 0; i < pts.Length; i++)
            {
                if (!_map.CanStand(pts[i])) continue;                  // 层①：本格站得住（与路线过滤同一口径）

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

            var remainingBefore = _nav.RemainingWaypoints;              // 先取值：SetRoute 会把 _index 归零
            //   本方法的注释已经写明要避免"把已经走顺的路线反复重排成'回头找最近路点'"，
            //   但旧代码调的是 `SetRoute` —— 它会按"离**当前位置**最近优先"重排整条路线并把 `_index` 归零，
            //   于是"最近的路点"（很可能是刚走过、在身后的那个）排到最前 ⇒ 机器人掉头。
            //   实测（bu-r-console.json）：单次 Play 里 Cliffe 一条 Route_CT_Mid 被重排 32 次，
            //   8 个 bot 的"剩余路点"在 3→2→3 之间反复（bu-r-bot-phys.tsv 第 29 列）。
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
            //   `Move` / `State` / `Use` **全部落在副本上、返回即丢**。
            //   一动不动（Gooseman/Rikk 均 0.000m 整回合），却在副本上照常打印
            //   `[C4] 携带 C4 冲向包点` 与 `[BOTFLIP]`，并被导航判成"卡住 0.00m"→ 每 0.5s 换目标 11 次
            //   ⇒ **T 永远到不了包点 ⇒ 永远不下包（A5 恒 0）**。
            if (TryBombObjective(self, ref intent, now)) return intent;

            // ⑤ 推进 / 守点
            return Advance(self, intent, now);
        }

        /// <summary>进行中的下包/拆包：保持按住 E、不移动、不开火（贴脸敌人时由调用方放行去交战）。</summary>
        private CsBotIntent ContinueUse(CsActor self, CsBotIntent intent, float now)
        {
            var planting = self.Team == CsTeam.T && self.HasBomb;
            var state = planting ? CsBotState.Plant : CsBotState.Defuse;

            SetState(state, now);
            intent.State = state;
            intent.Move = Vector3.zero;
            intent.Use = true;
            intent.Fire = false;

            if (now >= _lastUseLogAt)
            {
                _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                Game.Logger.Info(Tag,
                    $"{_name} 保持{(planting ? "下包" : "拆包")}中（进度 {self.UseProgress:P0}）：不移动、不开火");
            }

            intent.AimPoint = planting && self.HasBomb
                ? LookPoint(self, self.EyePosition + Forward(self.Yaw, -12f) * CsBotConst.AimFallbackDistance)
                : (_match.BombPlanted ? WithEyeHeight(_match.BombPosition) : LookAhead(self));
            return intent;
        }

        /// <summary>
        /// "这一 tick 不打架、先去拆包"（只对 CT 且已下包时成立）：
        /// ① 时间不够了（剩余 ≤ 拆包所需 + 余量）→ 无条件去拆；
        /// ② 时间还够 → 只有"眼前（<see cref="CsBotConst.DefuseOverFightRange"/> 内）就有敌人"才先打，
        ///    否则一律先冲包点（官方 CT 行为：下包即刻回防）。
        /// </summary>
        private bool MustDefuseFirst(CsActor self)
        {
            if (self.Team != CsTeam.CT || !_match.BombPlanted) return false;

            var need = self.HasDefuser ? CsConst.DefuseTimeWithKit : CsConst.DefuseTime;
            var left = _match.BombTimeLeft;
            if (left >= 0f && left <= need + CsBotConst.DefuseUrgencyMargin) return true;

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
        private bool MustPlantFirst(CsActor self)
        {
            if (self.Team != CsTeam.T || !self.HasBomb) return false;
            if (_match.BombPlanted) return false;                 // 已下过包 → 交给守包点

            NearestBombsitePoint(self.Position, out var zoneDist, out _);
            return zoneDist <= CsBotConst.SiteRadius;
        }

        // ==================================================================
        //  感知
        // ==================================================================
        private void UpdatePerception(CsActor self, float now)
        {
            _visibleNow = false;
            _closestVisibleDist = float.MaxValue;

            var eye = self.EyePosition;
            var forward = Forward(self.Yaw, 0f);
            var halfFov = CsConst.BotFovDegrees * 0.5f;
            var actors = _match.Actors;

            CsActor best = null;
            var bestDist = float.MaxValue;

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

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = other;
                    _lastSight = sight;
                    _lastSightBlocker = sightBlocker;
                }
            }

            if (best != null)
            {
                // "重新首次看见"的判据 = **已经忘了这个目标**（距上次看见超过记忆时长）。
                //
                // 不要用"换了目标就重置"，也不要掺进"本 tick 是否可见"：
                //    ① 多人交火时 `best`（最佳可见目标）会在几个敌人之间来回切；
                //    ② 机器人在视野边缘走动时可见性会**逐帧闪烁**（实测诊断行：`可见=True（距上次看见 0.00s）
                //       反应=未过（0.00/0.32s）`）。
                //    这两种情况都会让 `_firstSeenTime` 每 tick 归零 ⇒ `reacted` 永远 false ⇒
                //    **扳机永远扣不下去**（实测：8 个机器人 120 秒只打出 10 发、命中 0）。
                //    只有"真的忘掉了目标"才算重新发现，那时重算反应时间才是对的。
                var remembered = now - _lastSeenTime <= CsBotConst.TargetMemorySeconds;
                if (!remembered)
                {
                    _firstSeenTime = now;
                    _wobbleRefreshAt = 0f;
                }

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
            _heardFresh = false;
            if (_sense != null &&
                _sense.TryHearEnemy(self.Team, self.Position, CsConst.BotHearRadius, out var heardPos, out var heardTime, out _))
            {
                if (heardTime >= _heardTime)
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

            //
            //   第 130 行「**T 持包到 B 点 → 下包**」—— 下包是 T 持包者这一回合的**唯一**回合目标；
            //   规格三档表（第 119-121 行）里没有任何一档写了"持包者与敌人保持偏好距离对峙"。
            //   （Normal = 16×1.5 = 24m）判"够近" ⇒ 不进压上分支 ⇒ 站在离敌人 23.6m 处
            //   **连续静止 21.3s / 21.4s**，整回合只把"到最近包点标记"从 87.1m 挪到 29.6m
            //   （同段日志 `状态 Plant → Engage 目标=actor 9 距离=39.7m`、`换弹失败：MP5 Navy 备弹为 0`、
            //   `交战中开火被抑制第 124/143/154/155 次`）。
            // 只改"压上方向"：瞄准 / 开火 / 难度参数一字不动（`ApplyAimAndFire` 已在上方跑完）；
            //    也不新增第二个半径 —— 停步半径复用既有 `CsBotConst.PlantStopRadius`（与
            //    `TryPlantOrPickup` 同值），"进包点判定区就下包"由 `CommitTacticalDecision.MustPlantFirst` 负责。
            var plantAim = Vector3.zero;
            var pressing = false;
            if (self.Team == CsTeam.T && self.HasBomb && !_match.BombPlanted)
            {
                pressing = TryGetPlantAim(self, out plantAim);
            }

            if (pressing)
            {
                // 持包：边走边打，朝本轮包点压上（不因"距敌人够近"而置零）
                intent.Move = _nav.ComputeMove(self.Position, plantAim, CsBotConst.PlantStopRadius, now);
            }
            else if (dist > PreferredRange() * CsBotConst.AdvanceRangeFactor)
            {
                // 太远：压上去
                intent.Move = HorizontalDir(self.Position, target.Position);
            }
            else
            {
                if (dist > PreferredRange() && _nav.RemainingWaypoints <= 0)
                {
                    //
                    // `CsMatch.UpdateBots` 只写了 `if (intent.Crouch) a.IsCrouching = true;` —— **没有反向赋值**，
                    // 所以蹲下之后 AI 无法让它站起来（除非下一回合复活时被 RespawnActor 清掉）。
                    // 让"推进中的 bot"蹲下 = 全队以 0.34 倍速度爬行（B7"会走"会明显退化）。
                    // → 只在"站定射击"时启用蹲；同时我想表达"不想蹲"时也会显式提交 Crouch=false，
                    intent.Crouch = true;
                }

                // 开火间隙的横向走位（Hard 才有；正在开火时不走，避免移动散布把命中率打下去）
                intent.Move = canFire ? Vector3.zero : Strafe(self, now);
            }

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
        /// 朝向已经收敛到瞄点（<see cref="AimSettled"/>）、没在换弹、没被闪光致盲。
        /// 连发节奏不在这里 —— 那是模拟内部按 <c>FireBurst*/FirePause*</c> 执行的。</para>
        ///
        /// <para><b>宽限为什么要存在</b>：实测（Play 日志）机器人 22 次交战 0 发 —— 每 tick 都恰好
        /// "这一眼看不见"（射线被队友/墙角一瞬打断），于是扳机永远不扣。宽限只放行
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

            if (grace) aimPoint = _lastSeenPos;   // 瞄"最后所见位置"，不外推、不穿墙预测

            intent.AimPoint = aimPoint;

            var reacted = now - _firstSeenTime >= _profile.ReactionTime;
            var angle = AimAngle(self, aimPoint);
            var gate = AimGate();
            var canFire = (_visibleNow || grace) && reacted && angle <= gate;

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

            if (!canFire) LogFireDenied(self, now, reacted, angle, gate, sinceSeen, grace);

            intent.Fire = canFire;
            if (canFire) LastFireIntentTime = now;
            return canFire;
        }

        /// <summary>
        /// "这一 tick 决定不开火"的诊断：把三条门限**连同数值**打出来 ——
        /// 这就是"到底哪一条恒假"的原始证据（降频：每 bot 每 <see cref="CsBotConst.FireDiagInterval"/> 秒一条，首次必打）。
        /// </summary>
        private void LogFireDenied(CsActor self, float now, bool reacted, float angle, float gate,
            float sinceSeen, bool grace)
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
            return (right * _wobble.x + Vector3.up * _wobble.y) * scale;
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
        /// 投掷在本工程里是"沿瞄向给初速 <c>CsConst.GrenadeThrowForce</c>(12m/s) + 重力"，45° 最远 ≈ 7.09m，
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
            Game.Logger.Info(Tag,
                $"{_name}({self.Team}/{CsBotRoles.Label(_role)}) 投掷 {def?.DisplayName ?? _grenadePendingId}" +
                $"（{_grenadeWhy}，距目标 {_grenadeRange:F1}m，本回合第 {_grenadeThrows} 颗，" +
                $"剩余 {Mathf.Max(0, CountGrenade(self, _grenadePendingId) - 1)} 颗）");

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

            _grenadeAim = GrenadeAimPoint(self, point, out var range);
            _grenadePendingId = id;
            _grenadeWhy = why;
            _grenadeRange = range;
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
        /// <c>CsConst.GrenadeThrowForce</c>、之后只受 <c>CsConst.Gravity</c>）：
        /// 平抛落点 d = v²·sin(2θ)/g ⇒ sin(2θ) = d·g/v²，取低弧那一支（近弹道、飞行时间短、不易被墙挡）。
        /// </summary>
        private static Vector3 GrenadeAimPoint(CsActor self, Vector3 point, out float range)
        {
            var flat = point - self.EyePosition;
            flat.y = 0f;
            var d = flat.magnitude;
            range = d;
            if (d < 0.01f) return point;

            var v2 = CsConst.GrenadeThrowForce * CsConst.GrenadeThrowForce;
            var s = Mathf.Clamp(d * CsConst.Gravity / v2, 0f, 0.98f);
            var pitch = 0.5f * Mathf.Asin(s);

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

            if (d.magnitude <= CsBotConst.DefuseStopRadius)
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
                        $"{_name} 到达 C4（距离 {d.magnitude:F2}m ≤ {CsBotConst.DefuseStopRadius:F1}m）→ 按住 E 拆包" +
                        $"（{(self.HasDefuser ? "有拆弹器" : "无拆弹器")}，剩余 {_match.BombTimeLeft:F1}s）");
                }
                return true;
            }

            // 路上若看得见敌人，就"边走边盯边打"：不停下（停下就到不了包点），但不放弃警戒。
            SetState(CsBotState.Defuse, now);
            intent.State = CsBotState.Defuse;
            // viaRoute=false：包已经下了，目标是"冲到 C4"而不是继续走自己那条旧防守路线
            intent.Move = _nav.ComputeMove(self.Position, bomb, CsBotConst.DefuseStopRadius, now, viaRoute: false);

            var watch = _visibleNow && _lastSeenPos.sqrMagnitude > 0.0001f
                ? _lastSeenPos
                : WithEyeHeight(bomb);
            ApplyAimAndFire(self, watch, now, ref intent);
            return true;
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

            var near = NearestBombsitePoint(self.Position, out _, out _);
            var dest = _goalValid ? _goalPos : near;
            var destNear = NearestBombsitePoint(dest, out _, out _);
            aim = destNear.sqrMagnitude > 0.0001f ? destNear : dest;
            return aim.sqrMagnitude > 0.0001f;
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

                if (zoneDist <= CsBotConst.SiteRadius)
                {
                    SetState(CsBotState.Plant, now);
                    intent.State = CsBotState.Plant;
                    intent.Move = Vector3.zero;   // CanPlant 要求"停下"（CsBomb.IsMoving ⇒ 不许下包）
                    intent.Use = true;
                    intent.Fire = false;
                    intent.AimPoint = WithEyeHeight(near);

                    if (now >= _lastUseLogAt)
                    {
                        _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                        Game.Logger.Info(Tag,
                            $"[C4] {_name} 在包点 {zoneName} 内（距最近包点标记 {zoneDist:F2}m ≤ {CsBotConst.SiteRadius:F0}m）" +
                            $"→ 下包决策：停住 + 按住 E（需 {CsConst.PlantTime:F1}s）");
                    }
                    return true;
                }

                // 还没进包点：朝"本轮计划的包点"推进；计划不可用时退化为"离自己最近的包点"。
                // 目标点 = 该包点里**离自己最近的那个标记点**；停步半径 = PlantStopRadius（不是 SiteRadius）。
                if (!_goalValid)
                {
                    RateWarn("plant.nogoal",
                        $"{_name} 拿着 C4 但没有可用包点标记（本轮计划无目标）→ 只能朝最近的包点标记走");
                }

                if (!TryGetPlantAim(self, out var aim)) aim = near;

                SetState(CsBotState.Plant, now);
                intent.State = CsBotState.Plant;
                intent.Move = _nav.ComputeMove(self.Position, aim, CsBotConst.PlantStopRadius, now);
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
                                $"但离最近包点标记 {zoneDist:F2}m > {CsBotConst.SiteRadius:F0}m" +
                                "（标记缺失或目标坐标退化）→ 本帧交回推进分支，不停在原地");
                        }
                        return false;
                    }
                }

                if (now >= _lastUseLogAt)
                {
                    _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
                    Game.Logger.Info(Tag,
                        $"[C4] {_name} 携带 C4 冲向包点：目标={aim} 距最近包点标记 {zoneDist:F2}m " +
                        $"(> {CsBotConst.SiteRadius:F0}m) 停步半径={CsBotConst.PlantStopRadius:F1}m");
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

            intent.State = CsBotState.Patrol;
            intent.Move = _nav.ComputeMove(self.Position, dropped, CsBotConst.PickupStopRadius, now, viaRoute: false);
            intent.AimPoint = WithEyeHeight(dropped);

            if (now >= _lastUseLogAt)
            {
                _lastUseLogAt = now + CsBotConst.StateLogMinInterval;
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

            var actors = _match.Actors;
            var bestId = 0L;
            var bestD2 = float.MaxValue;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || !a.IsAlive || !a.IsBot) continue;
                if (a.Team != CsTeam.T) continue;

                var dv = a.Position - dropped;
                dv.y = 0f;
                var d2 = dv.sqrMagnitude;
                if (d2 < bestD2 - 1e-4f || (Mathf.Abs(d2 - bestD2) <= 1e-4f && a.Id < bestId))
                {
                    bestD2 = d2;
                    bestId = a.Id;
                }
            }

            return bestId != 0L && bestId == self.Id;
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
            intent.Move = _nav.ComputeMove(self.Position, _goalPos, radius, now);
            intent.AimPoint = LookPoint(self, WithEyeHeight(_nav.CurrentTarget(self.Position, _goalPos)));
            return intent;
        }

        /// <summary>守点：站住 + 周期性换位（"会换位"），听到动静会去看一眼（不离开守点太远）。</summary>
        private CsBotIntent Camp(CsActor self, CsBotIntent intent, Vector3 goal, float now)
        {
            SetState(CsBotState.Camp, now);
            intent.State = CsBotState.Camp;

            // ① 听到动静 → 去看一眼（且声源不能离守点太远）
            if (_heardFresh && now - _heardTime <= CsBotConst.NoiseMemorySeconds &&
                BotNavigator.Arrived(_heardPos, goal, CsBotConst.CampRepositionRadius * 2f))
            {
                var dir = _nav.ComputeMove(self.Position, _heardPos, CsBotConst.WaypointArriveRadius, now, viaRoute: false);
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
                    intent.Move = _nav.ComputeMove(self.Position, spot, CampSpotArriveRadius, now, viaRoute: false);
                    intent.AimPoint = WithEyeHeight(spot);
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
                    intent.Move = _nav.ComputeMove(self.Position, _campSpot, 0.6f, now, viaRoute: false);
                    intent.AimPoint = WithEyeHeight(_campSpot);
                    return intent;
                }
            }

            // ③' 角色 = **守点**（Anchor，CT 的 A/B 守位）：守点时长到点后**不换目标**，只把计时重新起算。
            //     守够 2.5~4.8s 就 ReplanObjective（换路线 / 去巡逻 / 回出生点），CT 守卫也每几秒改一次
            //     目的地 ⇒ 观感就是"全队都在乱走、没人真的守点"。
            //     这不是"站着不动"：本函数上面的守位表轮换（_holdSpots + _holdSwapAt）照常跑，
            //        玩家看到的是"在包点里换位置盯人"；也不影响卡住/包已下等其它换目标路径。
            if (_goalHoldUntil > 0f && now >= _goalHoldUntil && _goalIsSite && self.Team == CsTeam.CT &&
                CsBotRoles.StaysOnObjective(_role))
            {
                var ensure = ObjectiveHoldSeconds();
                _goalHoldUntil = now + ensure;
                RateWarn("role.anchor.hold",
                    $"{_name}（角色={CsBotRoles.Label(_role)}）在包点 {_holdSiteMarker} 守满 {ensure:F1}s → " +
                    $"{ensure:F1}s 内**继续守**（不 ReplanObjective；守位 {_holdSpots.Count} 个、累计换位 {_holdSwapCount} 次）");
            }
            else if (_goalHoldUntil > 0f && now >= _goalHoldUntil &&
                ReplanObjective(self, $"守点 {ObjectiveHoldSeconds():F1}s 已到（{_difficulty}）", now))
            {
                return Advance(self, intent, now);   // 新目标一定在 MinPatrolDistance 之外 → 不会立刻又 Arrived
            }

            // ④ 站定守点，盯住守的方向
            intent.Move = Vector3.zero;
            intent.AimPoint = WithEyeHeight(goal);
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

            Game.Logger.Info(Tag,
                $"守点换位：{_name}（CT）包点 {_holdSiteMarker} 第 {_holdSwapCount} 次换位 " +
                $"守位 {from + 1}→{next + 1}/{_holdSpots.Count} 新位置={_holdSpots[next]} " +
                $"（下次换位 {CsBotConst.HoldSwapSeconds:F0}s 后）");
        }

        /// <summary>
        /// 守卫的注视点：看向**本方包点被进攻的那条路**（A ↔ <see cref="CsMarkers.TAttackA"/>、
        /// B ↔ <see cref="CsMarkers.TAttackB"/>）里离自己最近的标记点 —— 即"盯住入口方向"。
        /// 取不到（标记缺失）就用包点目标点兜底，保证 <c>AimPoint</c> 非零。
        /// </summary>
        private Vector3 HoldWatchPoint(CsActor self, Vector3 goal)
        {
            if (_map != null && _map.IsLoaded && _holdSiteMarker != null)
            {
                var route = _holdSiteMarker == CsMarkers.BombsiteB ? CsMarkers.TAttackB : CsMarkers.TAttackA;
                var pts = _map.Points(route);
                if (pts != null && pts.Length > 0)
                {
                    var best = -1f;
                    var watch = Vector3.zero;
                    for (var i = 0; i < pts.Length; i++)
                    {
                        var d = pts[i] - self.Position;
                        d.y = 0f;
                        var dist = d.magnitude;

                        // 只看自己这一侧的入口（守位半径的 8 倍内），避免盯着地图另一头的路点
                        if (dist > CsBotConst.CampRepositionRadius * 8f) continue;
                        if (best >= 0f && dist >= best) continue;

                        best = dist;
                        watch = pts[i];
                    }

                    if (best >= 0f) return watch;
                }
            }

            return goal;
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

            // 进入交战必打（这是验收表 B8 的证据）；**离开交战也必须必打**（这是"为什么打不起来"的关键证据：
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
