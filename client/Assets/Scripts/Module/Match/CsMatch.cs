using System;
using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 本项目新增的「补集常量」：<see cref="CsConst"/> 里没有、但玩法必需的数值。
    ///
    /// <para>集中定义在此，**目的就是不让业务里出现裸数字**。它们与 <see cref="CsConst"/> 里的数值
    /// 语义一样（都是"改一处即可调平衡"的旋钮），只是所属文件不同。</para>
    ///
    /// <para><b>交付回报里已列出"建议收敛进 CsConst"</b> —— 本文件不改契约，所以先落在这里。</para>
    /// </summary>
    internal static class CsMatchConst
    {
        // ---- 视角 ----
        /// <summary>俯仰角上限（度）。与 CsActor.Pitch 的 -89~89 约定一致。</summary>
        public const float PitchLimit = 89f;

        // ---- 日志降频 ----
        /// <summary>
        /// 高频路径日志：首次 + 每 N 次。见 skill「日志约束」。
        /// **N 必须按"调用频率"选**：视线查询这类**每个 bot 每帧都可能调**的路径，50 会被打爆
        /// （实测：一场比赛刷出上千条，把 Console 冲掉、真正要看的信息全被埋了）—— 取 1000。
        /// </summary>
        public const int LogRateEvery = 1000;

        // ---- 出生 / 掉图兜底 ----
        /// <summary>
        /// 出生点向下找地面的探测深度（米）。**口径 = "只做小幅校正"，不是"从天上找地板"**：
        /// 标记 Y 本身就是 BSP 实体的脚底（`原版资源/解包产物/dust2_build.py:657-665`：`origin.z − 36` 再 `to_u`），
        /// 是可信真值；这里只探标记点附近 ±2m 的窗口（<c>SpawnGroundProbeUp 2</c> + 本值 4）。
        /// </summary>
        public const float SpawnGroundProbeDrop = 4f;

        /// <summary>
        /// 出生点向上抬起的探测起点高度（米）。
        /// ⛔ **不许放大**：agent-30 实测，从 <c>+100m</c> 往下探时命中的是**出生点头顶的横板/墙顶**
        /// （BSP 真值 CT 脚底 <c>-3.15</c>，运行时却复活在 y=<c>2.44</c>/<c>3.47</c>）—— 那正是"出生点被抬高"的成因。
        /// </summary>
        public const float SpawnGroundProbeUp = 2f;

        /// <summary>
        /// 出生点校正容差（米）：探针命中的面与标记 Y 相差超过它 ⇒ 认为"标记附近没有可信地面"，
        /// 直接沿用标记 Y（标记 Y = BSP 实体脚底）。
        /// </summary>
        public const float SpawnGroundSnapTolerance = 2f;

        /// <summary>低于这个 Y 视为"掉出地图/掉进夹层"，拉回一个**轮换过的**出生点。
        /// 依据：合法地面最低实测 ≈ <c>-3.25</c>（CT 出生点/买枪区一带 —— 日志
        /// <c>Cliffe 掉出地图（y=-15.0 &lt; -15），已拉回出生点 (16.50, -3.25, 34.50)</c>），
        /// 取 <c>-6</c> 留 ~2.7m 余量：既不会把合法低处误判成掉图，又比原来的 <c>-15</c> 早 ~9m 触发
        /// （原值要再掉 ~12m 才被救，且救回的是同一个坏点 ⇒ "掉→拉回横板→再掉"循环）。</summary>
        public const float FallRecoverY = -6f;

        /// <summary>
        /// 下落终端速度（米/秒）= 原版引擎的全局速度钳制 <c>sv_maxvelocity</c>。
        ///
        /// <para><b>出处</b>：随包官方配置 <c>原版资源/cs16src/cs16game/app/cstrike/server.cfg:29</c>
        /// 为 <c>sv_maxvelocity 3500</c>（<c>listenserver.cfg:27</c> 同值）⇒ <c>3500 units/s × 0.0254</c>
        /// = <b>88.9 m/s</b>（与 <c>CsConst.Gravity</c> / <c>JumpSpeed</c> 同一套 units→m 换算）。</para>
        ///
        /// <para><b>口径说明（重要）</b>：任务书 §1.2 指出的出处是 <c>原版资源/解包产物/原版数值表.md</c>
        /// 的 <c>sv_maxvelocity</c> 条目 —— **该条目不存在**（全项目检索只命中任务书自己；该表 §2 只反解了
        /// <c>sv_gravity/sv_maxspeed/sv_friction/sv_accelerate/sv_airaccelerate</c> 五个 cvar）。
        /// 故按"参考物已有的一律解析搬运"取**随包 server.cfg**，与 `策划/对照表.md` N-10~N-13 的口径一致。</para>
        ///
        /// <para>注意它比既有的 <see cref="CsConst.MaxFallSpeed"/>（-30 m/s）**宽松**，所以实际生效的上限
        /// 仍是 -30（本常量不用于放宽既有手感，只把"下落必须有上界"写成一条有出处的常量；
        /// 原版引擎也是每帧对速度向量做同一个钳制）。</para>
        /// </summary>
        public const float TerminalFallSpeed = 88.9f;

        /// <summary>
        /// 软地板（= 最近一次成功探测到的地面 Y）的保鲜期，**按"被步进的帧数"计，不是按秒**
        /// （探测成功 / 正踩着它 ⇒ 立即清零）。
        ///
        /// <para><b>为什么必须按帧计</b>：冻结期与"无输入"时不步进角色，按秒计的老化会在这些空窗里白白走完。
        /// 实测症状（agent-30 第 3 次 Play）：本地玩家复活后被冻结 9s（&gt; 2s 保鲜期）⇒ 进 Live 的第一帧赶上大 dt，
        /// 一步跨过薄楼板（楼板下方是空的 ⇒ 向下射线再也找不到它）⇒ 掉到 y=-8 才被 fall.recover 救回。</para>
        ///
        /// <para><b>为什么取 600 帧</b>（60fps ≈ 10s）：口径 = "连续 600 个被步进的帧都探不到地面才算真掉进空洞"。
        /// 薄楼板下的空洞里本来就永远探不到，长期停在"最后站过的地面高度"远好于掉出地图；真空洞最终仍由
        /// <see cref="FallRecoverY"/> 兜底。</para>
        /// </summary>
        public const int SoftFloorKeepFrames = 600;

        /// <summary>每帧站立贴合用的向下探测深度（米）。比 <c>CsConst.GroundCheckDistance</c> 宽松，
        /// 让"从高处落下"能在半途接住，而不是一路掉到 <see cref="FallRecoverY"/> 才被救。</summary>
        public const float GroundProbeDrop = 15f;

        // ---- 手雷携带上限（官方 CS1.6）----
        public const int MaxHeGrenades = 1;
        public const int MaxFlashbangs = 2;
        public const int MaxSmokes = 1;
        public const int MaxGrenadesTotal = 4;

        // ---- 后坐力回复 ----
        /// <summary>停火后多久开始回复后坐力（秒）。</summary>
        public const float RecoilRecoverDelay = 0.25f;
        /// <summary>后坐力回复速度（度/秒）。</summary>
        public const float RecoilRecoverSpeed = 6f;

        // ---- 穿透 ----
        /// <summary>穿墙命中时伤害保留比例（简化：不区分材质）。</summary>
        public const float PenetrationDamageScale = 0.5f;

        // ---- 移动 ----
        /// <summary>两次跳跃之间的最小间隔（防连跳）。</summary>
        public const float JumpRepeatDelay = 0.35f;
        /// <summary>
        /// 撞墙判定：某轴"实际位移 &lt; 期望位移 × 该比例"就认为被墙挡住并清掉该轴速度。
        /// （纯启发式系数，与 CsConst 里的伤害/数值无关。）
        /// </summary>
        public const float WallBlockVelocityRatio = 0.5f;
        /// <summary>拾取地面武器/炸弹的判定半径。</summary>
        public const float PickupRadius = 1.2f;

        // ---- 记分 ----
        public const int ScorePerKill = 1;
        public const int ScorePerBombObjective = 2;

        // ---- 表现/快照 ----
        /// <summary>待消费的射击记录上限（防止无人消费时无限增长）。</summary>
        public const int MaxPendingShots = 16;
        /// <summary>击杀信息在 KillFeed 里保留的秒数。</summary>
        public const float KillFeedLifetime = 6f;
        /// <summary>内部保留的击杀条数上限（HUD 只取前 CsConst.MaxKillFeedEntries 条）。</summary>
        public const int MaxKillFeedStore = 12;

        // ---- 手雷/烟雾 ----
        /// <summary>闪光弹致盲的最大半径（复用 HE 半径 —— CsConst 未单独定义）。</summary>
        public const float FlashRadius = CsConst.GrenadeHeRadius;
        /// <summary>烟雾体积半径（简化球体；CsConst 未定义）。</summary>
        public const float SmokeRadius = 3.5f;
        /// <summary>视线被烟雾遮挡的判定半径。</summary>
        public const float SmokeBlockRadius = 3.5f;

        // ---- 机器人 ----
        /// <summary>机器人瞄准误差的重采样间隔（秒）。</summary>
        public const float BotAimErrorRefresh = 0.35f;

        /// <summary>
        /// "机器人射线结算"累计统计日志的最小间隔（秒）。
        /// 射线路径是**每次开火**都会走到的高频路径（一场比赛几百发），所以按时间降频：
        /// 首次必打（第一发就是证据），之后每 <see cref="BotShotDiagInterval"/> 秒一条。
        /// </summary>
        public const float BotShotDiagInterval = 10f;

        /// <summary>命中缓冲容量（一次射线的全部命中）。放够 32：射手的 4 个自身受体 + 多个敌人 + 多层几何。</summary>
        public const int BotHitBufferSize = 32;

        // ---- 炸弹操作 ----
        /// <summary>下包/拆包期间的水平速度平方阈值；超过即判定"移动中" → 进度清零。</summary>
        public const float MoveCancelSpeedSqr = 0.01f;
        /// <summary>拆包时允许离炸弹的最大距离（CsConst 未定义；官方 1.6 需要贴身）。</summary>
        public const float DefuseRadius = 1.5f;
        /// <summary>炸弹剩余时间低于该值时，蜂鸣切到快速节拍（CsConst 注释里的 "&gt;10s"）。</summary>
        public const float BombBeepFastThreshold = 10f;
        /// <summary>C4 爆炸半径（CsConst 未定义；官方 1.6 的 C4 在十余米内几乎必杀）。</summary>
        public const float BombExplosionRadius = 12f;
        /// <summary>C4 爆炸最大伤害（足够在半径内致死）。</summary>
        public const int BombExplosionMaxDamage = 500;

        // ---- 出生 ----
        public const float SpawnYawRandomDeg = 360f;

        // ---- 队伍 ----
        public const int MaxTeamSize = 16;
    }

    /// <summary>
    /// 本地权威比赛模拟（单机版的"服务器"）。<see cref="ICsMatch"/> 的**唯一实现**。
    ///
    /// <para>它自己持有全部权威状态；回合/经济/炸弹/伤害/装备五块逻辑分别委托给
    /// <see cref="CsRound"/> / <see cref="CsEconomy"/> / <see cref="CsBomb"/> / <see cref="CsDamage"/> / <see cref="CsInventory"/>。</para>
    ///
    /// <para>时间基准：<see cref="Time.time"/>。原因 —— <see cref="CsActor"/> 的
    /// NextFireTime/ReloadEndTime/SwitchEndTime/FlashEndTime 是"绝对时间"，表现层（Module/Combat）
    /// 也会拿它跟 <see cref="Time.time"/> 比；统一用同一个时钟才不会错位。
    /// 暂停时把所有人的绝对时间整体后移，抵消暂停时长（见 <see cref="SetPaused"/>）。</para>
    /// </summary>
    public sealed class CsMatch : ICsMatch
    {
        internal const string Tag = "Match";

        private ICsMap _map;

        private readonly List<CsActor> _actors = new List<CsActor>(CsMatchConst.MaxTeamSize * 2);
        private readonly Dictionary<long, CsBotIntent> _botIntents = new Dictionary<long, CsBotIntent>();
        private readonly Dictionary<long, bool> _botUseHeld = new Dictionary<long, bool>();
        private readonly Dictionary<long, float> _botAimErrorRefreshAt = new Dictionary<long, float>();
        private readonly Dictionary<long, Vector2> _botAimError = new Dictionary<long, Vector2>();
        private readonly Dictionary<long, float> _botFirePauseUntil = new Dictionary<long, float>();
        private readonly Dictionary<long, float> _botBurstUntil = new Dictionary<long, float>();
        private readonly Dictionary<string, int> _rateCounters = new Dictionary<string, int>(32);
        private readonly List<CsKillFeedItem> _killFeed = new List<CsKillFeedItem>(CsMatchConst.MaxKillFeedStore);
        private readonly List<string> _pendingShots = new List<string>(CsMatchConst.MaxPendingShots);
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[CsMatchConst.BotHitBufferSize];
        private readonly List<CsActor> _aliveScratch = new List<CsActor>(CsMatchConst.MaxTeamSize * 2);

        // ---- 软地板（§1.2）：最近一次**成功探测到**的地面 Y（按 actor）。----
        // 为什么需要：竖直方向只有一根向下的射线（ResolveMove 的 Y 是原样透传的），
        // 一旦某列下方 GroundProbeDrop 内没有世界面 ⇒ 常规贴地判定失效 ⇒ 每帧继续加速、穿过一切。
        // 地面丢失时**不立即清零**，先保留 SoftFloorKeepFrames 个"被步进的帧"，用它把角色接住。
        private readonly Dictionary<long, float> _lastGroundY = new Dictionary<long, float>();
        /// <summary>软地板的"老化计数"：连续多少个被步进的帧里地面探测失败（探测成功 / 踩着它即清零）。</summary>
        private readonly Dictionary<long, int> _softFloorMisses = new Dictionary<long, int>();

        // ---- 机器人射线结算统计（诊断 + 交付证据；见 BotResolveShot / LogBotShotDiag）----
        /// <summary>累计打出的弹丸射线数（= 真正结算过的发数）。</summary>
        private long _botShotRays;
        /// <summary>累计"打到了别人的受体并完成伤害结算"的发数（与 BotStats.Hits 同源，但这里是原始事实）。</summary>
        private long _botShotHits;
        /// <summary>累计"最近命中是墙/地面"（射线没打到人）的发数。</summary>
        private long _botShotBlocked;
        /// <summary>累计"一个碰撞体都没命中"的发数（异常情况：射线没打到地面，通常说明缓冲/层配置有问题）。</summary>
        private long _botShotNothing;
        /// <summary>累计跳过的"自己的受体"命中数（起点在自己胸/腹内，正常现象）。</summary>
        private long _botShotSelfSkipped;
        /// <summary>累计"命中的受体属于已死亡/不存在的 actor"的发数（异常分支）。</summary>
        private long _botShotStaleProxy;
        /// <summary>最近一发"被挡"的挡点描述（碰撞体名 / 层 / 距离）—— 诊断用，"打不中"时唯一的现场证据。</summary>
        private string _lastBotShotBlocker;
        /// <summary>下一条射线统计日志的最早时刻。</summary>
        private float _nextBotShotDiagAt;

        private CsMatchConfig _cfg = new CsMatchConfig();
        private CsActor _local;
        private long _nextActorId = 1;

        private bool _running;
        private bool _paused;
        private float _pauseStart;

        private bool _hasLocalInput;
        private CsInputState _localInput;
        private bool _zoomHeld;
        private bool _localUseHeld;
        private float _localNextJumpTime;

        private int _spectateIndex = -1;

        private CsBotDifficulty _botDifficulty = CsBotDifficulty.Normal;
        private int _botNameCursor;
        private bool _nonBotActorWarned;
        private bool _mapNotLoadedWarned;

        internal readonly CsRound Round;
        internal readonly CsEconomy Economy;
        internal readonly CsBomb Bomb;
        internal readonly CsDamage Damage;
        internal readonly CsInventory Inventory;

        /// <summary>官方 CS1.6 的全部机器人名字（数量 == <see cref="CsConst.BotNamePoolSize"/>）。</summary>
        private static readonly string[] BotNames =
        {
            "Cliffe", "Minh", "Gooseman", "ZBot", "Rikk",
            "Spliff", "Darrell", "Scuzzy", "Hank", "Yahn",
            "Moe", "Ted", "Slash", "Ric", "Bond",
            "Doug", "Steve", "Xander", "Mikkel", "Rich",
        };

        public CsMatch(ICsMap map)
        {
            _map = map;
            Round = new CsRound(this);
            Economy = new CsEconomy(this);
            Bomb = new CsBomb(this);
            Damage = new CsDamage(this);
            Inventory = new CsInventory(this);

            if (BotNames.Length != CsConst.BotNamePoolSize)
            {
                Game.Logger.Error(Tag,
                    $"机器人名字池数量不符：实际 {BotNames.Length}，CsConst.BotNamePoolSize={CsConst.BotNamePoolSize}");
            }
        }

        // ==================================================================
        //  内部访问器（供 CsRound / CsEconomy / CsBomb / CsDamage / CsInventory 使用）
        // ==================================================================
        /// <summary>
        /// 时间源，默认 <see cref="Time.time"/>。
        ///
        /// <para><b>为什么必须是可替换的</b>：<see cref="CsActor"/> 的 NextFireTime / ReloadEndTime /
        /// SwitchEndTime / FlashEndTime 都是"绝对时间"，表现层（Module/Combat）也会拿它们跟
        /// <see cref="Time.time"/> 比 —— 所以**默认值必须是 Time.time**，否则跨模块会错位。
        /// 反过来，自检/回归要跑"60 秒对局"时不可能真等 60 秒，于是留这个替换点：
        /// 换上可快速前进的假时钟就能瞬时跑完（<see cref="CsMatch"/> 一次性用完
        /// <c>now</c> 再算，不会有半个 Tick 用旧时钟的问题）。</para>
        ///
        /// <para><b>注意</b>：本成员不是 <see cref="ICsMatch"/> 契约的一部分，只为自检/回归存在。</para>
        /// </summary>
        public static Func<float> Clock = () => Time.time;

        /// <summary>当前模拟时间（= <see cref="Clock"/> 的取值）。</summary>
        internal float Now => Clock != null ? Clock() : Time.time;

        internal CsMatchConfig Cfg => _cfg;
        internal ICsMap Map => _map;
        internal CsActor Local => _local;
        internal IReadOnlyList<CsActor> ActorList => _actors;

        /// <summary>由 MatchModule 在 App 注入 / 延迟挂载地图时调用。</summary>
        internal void SetMap(ICsMap map)
        {
            if (map == null) return;
            _map = map;
            _mapNotLoadedWarned = false;
        }

        internal void RegisterBotUse(long id, bool held) => _botUseHeld[id] = held;

        internal bool BotUseHeld(long id)
        {
            return _botUseHeld.TryGetValue(id, out var v) && v;
        }

        // ==================================================================
        //  生命周期
        // ==================================================================
        public bool IsRunning => _running;
        public bool IsPaused => _paused;
        public CsMatchConfig Config => _cfg;

        public void Start(CsMatchConfig cfg)
        {
            if (cfg == null)
            {
                Game.Logger.Error(Tag, "Start(null)：配置为空，拒绝开局");
                return;
            }

            if (_running) Stop();

            _cfg = cfg;
            _actors.Clear();
            _botIntents.Clear();
            _botUseHeld.Clear();
            _botAimError.Clear();
            _botAimErrorRefreshAt.Clear();
            _botFirePauseUntil.Clear();
            _botBurstUntil.Clear();
            _killFeed.Clear();
            _pendingShots.Clear();
            _rateCounters.Clear();
            _lastGroundY.Clear();
            _softFloorMisses.Clear();
            ResetBotShotStats();
            _nextActorId = 1;
            _botNameCursor = 0;
            _spectateIndex = -1;
            _zoomHeld = false;
            _localNextJumpTime = 0f;
            _hasLocalInput = false;
            _localInput = default;
            _botDifficulty = cfg.BotDifficulty;
            _running = true;
            _paused = false;

            Round.Reset();
            Economy.Reset();
            Bomb.Reset();
            Damage.Reset();
            Inventory.Reset();

            CreateLocalPlayer();
            CreateConfiguredBots();
            BalanceTeams();

            Game.Logger.Info(Tag,
                $"比赛开始：地图={cfg.MapName} 每队bot配置={cfg.BotsPerTeam} 难度={cfg.BotDifficulty} " +
                $"玩家={cfg.PlayerName}({cfg.PlayerTeam}) 起始金钱={cfg.StartMoney} 半场回合={cfg.RoundsPerHalf} " +
                $"回合时长={cfg.RoundTime}s 冻结={cfg.FreezeTime}s 友好伤害={cfg.FriendlyFire}");

            EmitEvent(Events.MatchStarted);
            Round.BeginMatch();
            WriteHudSnapshot(0f);
        }

        public void Stop()
        {
            if (!_running && _actors.Count == 0) return;

            Game.Logger.Info(Tag,
                $"比赛停止：比分 CT {Round.ScoreCT} - {Round.ScoreT} T，共推进 {Round.RoundNumber} 回合");

            _running = false;
            _paused = false;
            Bomb.Reset();
            Inventory.Reset();
            Round.Reset();
            _actors.Clear();
            _botIntents.Clear();
            _botUseHeld.Clear();
            _botAimError.Clear();
            _botAimErrorRefreshAt.Clear();
            _botFirePauseUntil.Clear();
            _botBurstUntil.Clear();
            _killFeed.Clear();
            _pendingShots.Clear();
            ResetBotShotStats();
            _local = null;
            _spectateIndex = -1;

            // 静态快照必须清空，否则下次进 Play（无域重载）会残留上一局的数值（skill P-3）。
            CsHudSnapshot.Reset();
        }

        public void Tick(float dt)
        {
            if (!_running) return;
            if (dt < 0f) dt = 0f;

            if (_paused)
            {
                // 暂停：不推进任何模拟时间，但每帧仍刷新 HUD 快照（UI 要能响应）。
                // dt 传 0：暂停时连"受伤提示倒计时"也不该走。
                WriteHudSnapshot(0f);
                return;
            }

            var now = Now;

            // 1) 阶段机（可能把 Freeze→Live、Live→RoundEnd、RoundEnd→下一回合）
            Round.TickPhase(dt, now);

            // 2) 玩家 / 机器人动作（移动、射击、换弹）
            if (Round.Phase == CsRoundPhase.Live || Round.Phase == CsRoundPhase.Freeze)
            {
                UpdateLocalPlayer(dt, now);
                UpdateBots(dt, now);
                TickActorTimers(dt, now);
                UpdateBuyZoneFlags();
            }

            // 3) 手雷抛射物与烟雾
            Inventory.TickProjectiles(dt, now);

            // 4) 炸弹（下包/拆包/倒计时）
            Bomb.Tick(dt, now);

            // 5) 胜负判定（全歼 / 超时）
            Round.EvaluateWin(now);

            // 6) 清理过期的击杀信息
            TickKillFeed(now);

            WriteHudSnapshot(dt);
        }

        // ==================================================================
        //  状态查询
        // ==================================================================
        public CsRoundPhase Phase => Round.Phase;
        public int RoundNumber => Round.RoundNumber;
        public int ScoreT => Round.ScoreT;
        public int ScoreCT => Round.ScoreCT;
        public float PhaseTimeLeft => Round.PhaseTimeLeft;

        /// <summary>
        /// 玩家现在能否买枪 = 活着 + 身处买枪区 + <b>在买枪期内</b>。
        /// 买枪期是**独立的 15 s 窗口**（<c>mp_buytime</c> · <c>server.cfg:42</c>），不是"冻结期"，
        /// 见 <see cref="CanBuyTime"/>。
        /// </summary>
        public bool CanBuyNow
        {
            get
            {
                if (!_running || _paused) return false;
                var a = _local;
                if (a == null || !a.IsAlive) return false;
                return a.InBuyZone && CanBuyTime();
            }
        }

        /// <summary>
        /// 是否处于买枪时间 = **回合开始后 <c>CsConst.BuyTime</c>(15 s) 之内**（原版 <c>mp_buytime</c>，
        /// 出处 <c>server.cfg:42</c>）。它与冻结期（<c>CsConst.FreezeTime</c> 4 s）是**两个独立计时器** ——
        /// 冻结结束不等于买枪结束：只要这 15 s 没用尽、人还在买枪区内就仍然能买（原版行为）。
        /// 计时器在 <see cref="CsRound.BuyTimeLeft"/>（由阶段机每帧推进，跨 Freeze→Live）。
        /// </summary>
        internal bool CanBuyTime()
        {
            if (!_running) return false;
            if (Round.Phase != CsRoundPhase.Freeze && Round.Phase != CsRoundPhase.Live) return false;
            return Round.BuyTimeLeft > 0f;
        }

        public bool IsInBuyZone(Vector3 position, CsTeam team)
        {
            if (team != CsTeam.T && team != CsTeam.CT) return false;
            if (_map == null) return false;

            var marker = team == CsTeam.T ? CsMarkers.BuyZoneT : CsMarkers.BuyZoneCT;
            var pts = _map.Points(marker);

            if (pts == null || pts.Length == 0)
            {
                // agent-02 可能未生成买枪区标记 → 用本队出生点兜底（半径统一取 CsMarkers.BombsiteRadius；
                // 不引裸数字：CsConst 未定义买枪区半径，且这里是"区域半径"复用）。
                RateWarn("buyzone.marker.missing",
                    $"地图缺少买枪区标记 {marker}，回退到出生点判定买枪区");
                pts = _map.Points(team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT);
                if (pts == null || pts.Length == 0) return false;
            }

            var r2 = CsMarkers.BombsiteRadius * CsMarkers.BombsiteRadius;
            for (var i = 0; i < pts.Length; i++)
            {
                var d = position - pts[i];
                d.y = 0f;
                if (d.sqrMagnitude <= r2) return true;
            }
            return false;
        }

        public bool CanBuyWeapon(string weaponId, out string reason)
        {
            reason = null;
            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                reason = "未知武器";
                return false;
            }

            var a = _local;
            if (a == null || !a.IsAlive)
            {
                reason = "玩家不在场";
                return false;
            }

            if (!CanBuyTime())
            {
                reason = "不在买枪时间";
                return false;
            }

            if (!a.InBuyZone)
            {
                reason = "必须在买枪区";
                return false;
            }

            if (def.TeamLimit == CsTeamLimit.TerroristOnly && a.Team != CsTeam.T)
            {
                reason = "该武器限阵营";
                return false;
            }
            if (def.TeamLimit == CsTeamLimit.CounterTerroristOnly && a.Team != CsTeam.CT)
            {
                reason = "该武器限阵营";
                return false;
            }

            if (a.Money < def.Price)
            {
                reason = "金钱不足";
                return false;
            }

            if (def.Class == CsWeaponClass.Grenade && !Inventory.CanCarryMoreGrenades(a, def.Id))
            {
                reason = "手雷已达上限";
                return false;
            }

            return true;
        }

        public bool BombPlanted => Bomb.Planted;
        public Vector3 BombPosition => Bomb.Planted ? Bomb.Position : Bomb.LastKnownPosition;
        public float BombTimeLeft => Bomb.Planted ? Bomb.TimeLeft : -1f;
        public float UseProgress => Bomb.ActiveUseProgress;

        public bool BombCarrierIs(long actorId)
        {
            var a = Find(actorId);
            return a != null && a.IsAlive && a.HasBomb;
        }

        public IReadOnlyList<CsActor> Actors => _actors;
        public CsActor LocalPlayer => _local;

        public CsActor SpectateTarget
        {
            get
            {
                if (!IsSpectating) return null;
                var alive = CollectAlive(_aliveScratch);
                if (alive.Count == 0) return null;
                if (_spectateIndex < 0 || _spectateIndex >= alive.Count) _spectateIndex = 0;
                return alive[_spectateIndex];
            }
        }

        public CsActor Find(long actorId)
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                if (_actors[i].Id == actorId) return _actors[i];
            }
            return null;
        }

        public int AliveCount(CsTeam team)
        {
            var n = 0;
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (a.Team == team && a.IsAlive) n++;
            }
            return n;
        }

        public int PlayerCount(CsTeam team)
        {
            var n = 0;
            for (var i = 0; i < _actors.Count; i++)
            {
                if (_actors[i].Team == team) n++;
            }
            return n;
        }

        /// <summary>
        /// 视线是否通畅（墙 + 烟雾）。命中点距起点 &lt; 距离 - 玩家半径即判定被挡
        /// （用 <see cref="CsConst.PlayerRadius"/> 容差，避免把目标自己的受体当成墙）。
        /// </summary>
        public bool HasLineOfSight(Vector3 from, Vector3 to, float maxDistance = 100f)
        {
            var delta = to - from;
            var dist = delta.magnitude;
            if (dist <= Mathf.Epsilon) return true;
            if (dist > maxDistance)
            {
                // 高频路径（机器人视线每帧调用）—— 降频：首次 + 每 50 次。
                RateInfo("los.range", $"视线查询超出上限：{dist:F1}m > {maxDistance:F1}m，判定为不可见");
                return false;
            }

            var dir = delta / dist;

            if (Inventory.IsBlockedBySmoke(from, to)) return false;

            var count = Physics.RaycastNonAlloc(from, dir, _hitBuffer, dist, ~0, QueryTriggerInteraction.Ignore);
            var tolerance = dist - CsConst.PlayerRadius;
            for (var i = 0; i < count; i++)
            {
                if (_hitBuffer[i].distance < tolerance) return false;
            }
            return true;
        }

        private List<CsActor> CollectAlive(List<CsActor> buffer)
        {
            buffer.Clear();
            for (var i = 0; i < _actors.Count; i++)
            {
                if (_actors[i].IsAlive) buffer.Add(_actors[i]);
            }
            return buffer;
        }

        // ==================================================================
        //  本地玩家输入
        // ==================================================================
        public void SetLocalInput(in CsInputState input)
        {
            _localInput = input;
            _hasLocalInput = true;
        }

        // ==================================================================
        //  玩家动作
        // ==================================================================
        public void RequestReload()
        {
            if (!_running) return;
            var a = _local;
            if (a == null) { RateWarn("reload.nolocal", "RequestReload：本地玩家不存在"); return; }
            if (!a.IsAlive) { RateWarn("reload.dead", $"RequestReload：{a.Name} 已死亡"); return; }
            Inventory.Reload(a, Now);
        }

        public void SwitchSlot(int slot)
        {
            if (!_running) return;
            var a = _local;
            if (a == null) { RateWarn("switch.nolocal", "SwitchSlot：本地玩家不存在"); return; }
            if (!a.IsAlive) { RateWarn("switch.dead", $"SwitchSlot：{a.Name} 已死亡"); return; }
            Inventory.SwitchSlot(a, slot, Now);
        }

        public void SwitchWeapon(string weaponId)
        {
            if (!_running) return;
            var a = _local;
            if (a == null) { RateWarn("switchweapon.nolocal", "SwitchWeapon：本地玩家不存在"); return; }
            if (!a.IsAlive) { RateWarn("switchweapon.dead", $"SwitchWeapon：{a.Name} 已死亡"); return; }
            Inventory.SwitchWeapon(a, weaponId, Now);
        }

        public bool TryBuy(string weaponId, out string reason)
        {
            if (_local == null)
            {
                reason = "玩家不在场";
                return false;
            }
            return TryBuyFor(_local.Id, weaponId, out reason);
        }

        /// <summary>
        /// 给指定 actor 买枪（**非 ICsMatch 契约的扩展**）。
        /// 契约里没有「给某个 actor 买枪」的入口；机器人 AI 若要自己买枪可以走这里。
        /// 不调也没关系 —— 冻结期会替机器人自动配枪（见 AutoBuyForBots）。
        /// </summary>
        public bool TryBuyFor(long actorId, string weaponId, out string reason)
        {
            reason = null;

            if (!_running) { reason = "比赛未开始"; return false; }

            var a = Find(actorId);
            if (a == null)
            {
                reason = "玩家不在场";
                Game.Logger.Warn(Tag, $"买枪失败：actor {actorId} 不存在（{weaponId}）");
                return false;
            }
            if (!a.IsAlive)
            {
                reason = "玩家已死亡";
                Game.Logger.Warn(Tag, $"买枪失败：{a.Name} 已死亡（{weaponId}）");
                return false;
            }
            if (!CanBuyTime())
            {
                reason = "不在买枪时间";
                Game.Logger.Warn(Tag, $"买枪失败：不在买枪时间（{a.Name} / {weaponId}）");
                return false;
            }
            if (!a.InBuyZone)
            {
                reason = "必须在买枪区";
                Game.Logger.Warn(Tag, $"买枪失败：{a.Name} 不在买枪区（{weaponId}）");
                return false;
            }

            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                reason = "未知武器";
                Game.Logger.Warn(Tag, $"买枪失败：未知武器 {weaponId}");
                return false;
            }
            if (def.TeamLimit == CsTeamLimit.TerroristOnly && a.Team != CsTeam.T)
            {
                reason = "该武器限阵营";
                Game.Logger.Warn(Tag, $"买枪失败：{def.Id} 仅限恐怖分子（{a.Name} 属 {a.Team}）");
                return false;
            }
            if (def.TeamLimit == CsTeamLimit.CounterTerroristOnly && a.Team != CsTeam.CT)
            {
                reason = "该武器限阵营";
                Game.Logger.Warn(Tag, $"买枪失败：{def.Id} 仅限反恐精英（{a.Name} 属 {a.Team}）");
                return false;
            }
            if (a.Money < def.Price)
            {
                reason = "金钱不足";
                Game.Logger.Warn(Tag,
                    $"买枪失败：金钱不足（{a.Name} 有 ${a.Money}，{def.DisplayName} 需 ${def.Price}）");
                return false;
            }
            if (def.Class == CsWeaponClass.Grenade && !Inventory.CanCarryMoreGrenades(a, def.Id))
            {
                reason = "手雷已达上限";
                Game.Logger.Warn(Tag, $"买枪失败：手雷已达携带上限（{a.Name} / {def.DisplayName}）");
                return false;
            }

            Economy.Add(a, -def.Price, $"购买 {def.DisplayName}");
            Inventory.GivePurchased(a, def, Now);
            return true;
        }

        public void SetUseHeld(bool held)
        {
            if (!_running) return;
            _localUseHeld = held;
            var a = _local;
            if (a == null) return;
            Bomb.SetUseState(a, held && a.IsAlive, Now);
        }

        public void DropActiveWeapon()
        {
            if (!_running) return;
            var a = _local;
            if (a == null || !a.IsAlive) return;

            var def = a.ActiveDef;
            if (def == null)
            {
                RateWarn("drop.none", "DropActiveWeapon：当前没有手持武器");
                return;
            }

            Inventory.DropWeapon(a, def.Id);
            Game.Logger.Info(Tag, $"{a.Name} 丢弃了 {def.DisplayName}");
        }

        public bool IsZoomed
        {
            get
            {
                if (!_running || !_zoomHeld) return false;
                var a = _local;
                if (a == null || !a.IsAlive) return false;
                var def = a.ActiveDef;
                return def != null && def.Class == CsWeaponClass.Sniper;
            }
        }

        // ==================================================================
        //  观战
        // ==================================================================
        public bool IsSpectating => _running && _local != null && !_local.IsAlive;

        public void SpectateNext(int dir)
        {
            if (!_running) return;
            if (!IsSpectating)
            {
                RateInfo("spectate.alive", "SpectateNext：本地玩家还活着，无需观战");
                return;
            }

            var alive = CollectAlive(_aliveScratch);
            if (alive.Count == 0)
            {
                RateInfo("spectate.none", "SpectateNext：当前没有存活的观战对象");
                _spectateIndex = -1;
                return;
            }

            if (_spectateIndex < 0) _spectateIndex = 0;
            var step = dir >= 0 ? 1 : -1;
            var n = alive.Count;
            _spectateIndex = ((_spectateIndex + step) % n + n) % n;

            var t = alive[_spectateIndex];
            Game.Logger.Info(Tag, $"观战切换 → {t.Name}（{t.Team}，{_spectateIndex + 1}/{n}）");
        }

        // ==================================================================
        //  射击结算
        // ==================================================================
        public void ReportHit(in CsHitInfo hit)
        {
            if (!_running) return;

            var shooter = Find(hit.ShooterId);
            var victim = Find(hit.VictimId);

            if (victim == null)
            {
                RateWarn("hit.novictim", $"ReportHit：找不到受击者 actor {hit.VictimId}");
                return;
            }
            if (!victim.IsAlive)
            {
                RateWarn("hit.deadvictim", $"ReportHit：{victim.Name} 已死亡，忽略命中");
                return;
            }

            var def = CsWeapons.Get(hit.WeaponId);
            if (def == null && shooter != null) def = shooter.ActiveDef;
            if (def == null)
            {
                RateWarn("hit.noweapon", $"ReportHit：无法确定武器（weaponId={hit.WeaponId ?? "null"}）");
                return;
            }

            var dist = hit.Distance > 0f ? hit.Distance : Vector3.Distance(
                shooter != null ? shooter.EyePosition : hit.Point, victim.EyePosition);

            Damage.ApplyHit(shooter, victim, def, hit.Hitbox, hit.Point, dist, hit.ThroughWall);
        }

        public bool ConsumeShotFired(out string weaponId)
        {
            weaponId = null;
            if (_pendingShots.Count == 0) return false;
            weaponId = _pendingShots[0];
            _pendingShots.RemoveAt(0);
            return true;
        }

        internal void RecordShotFired(string weaponId)
        {
            if (_pendingShots.Count >= CsMatchConst.MaxPendingShots)
            {
                _pendingShots.RemoveAt(0);
                RateWarn("shot.pending.overflow",
                    $"待消费的射击记录超过 {CsMatchConst.MaxPendingShots} 条，丢弃最旧一条（表现层是否每帧调 ConsumeShotFired？）");
            }
            _pendingShots.Add(weaponId);
        }

        // ==================================================================
        //  机器人
        // ==================================================================
        public void AddBot(CsTeam team, CsBotDifficulty diff)
        {
            if (!_running)
            {
                Game.Logger.Warn(Tag, "AddBot：比赛未开始，已忽略");
                return;
            }
            if (team != CsTeam.T && team != CsTeam.CT)
            {
                Game.Logger.Warn(Tag, $"AddBot：非法阵营 {team}，已忽略");
                return;
            }
            if (PlayerCount(team) >= CsMatchConst.MaxTeamSize)
            {
                Game.Logger.Warn(Tag, $"AddBot：{team} 已达人数上限 {CsMatchConst.MaxTeamSize}");
                return;
            }

            var a = NewBotActor(team, diff);
            _actors.Add(a);
            Game.Logger.Info(Tag, $"添加机器人 {a.Name}（{team} / {diff}）当前人数 T={PlayerCount(CsTeam.T)} CT={PlayerCount(CsTeam.CT)}");

            // 中途加入 → 立刻出生，能马上参与本回合。
            if (Round.Phase != CsRoundPhase.None && Round.Phase != CsRoundPhase.MatchEnd)
            {
                RespawnActor(a, false);
            }
        }

        public void KickBot()
        {
            for (var i = _actors.Count - 1; i >= 0; i--)
            {
                if (!_actors[i].IsBot) continue;

                var a = _actors[i];
                if (a.HasBomb) Bomb.OnCarrierLost(a);
                _actors.RemoveAt(i);
                _botIntents.Remove(a.Id);
                _botUseHeld.Remove(a.Id);
                _botAimError.Remove(a.Id);
                _botAimErrorRefreshAt.Remove(a.Id);
                _botFirePauseUntil.Remove(a.Id);
                _botBurstUntil.Remove(a.Id);
                _spectateIndex = -1;
                Game.Logger.Info(Tag, $"踢出机器人 {a.Name}（{a.Team}）");
                return;
            }
            Game.Logger.Warn(Tag, "KickBot：没有可踢出的机器人");
        }

        public void SetBotDifficulty(CsBotDifficulty diff)
        {
            _botDifficulty = diff;
            var n = 0;
            for (var i = 0; i < _actors.Count; i++)
            {
                if (!_actors[i].IsBot) continue;
                _actors[i].Difficulty = diff;
                n++;
            }
            Game.Logger.Info(Tag, $"机器人难度切换为 {diff}，已对 {n} 个已存在的机器人即时生效");
        }

        public CsBotDifficulty BotDifficulty => _botDifficulty;

        public int BotCount
        {
            get
            {
                var n = 0;
                for (var i = 0; i < _actors.Count; i++)
                {
                    if (_actors[i].IsBot) n++;
                }
                return n;
            }
        }

        public int BotCountOf(CsTeam team)
        {
            var n = 0;
            for (var i = 0; i < _actors.Count; i++)
            {
                if (_actors[i].IsBot && _actors[i].Team == team) n++;
            }
            return n;
        }

        public void SubmitBotIntent(long actorId, in CsBotIntent intent)
        {
            if (!_running) return;

            var a = Find(actorId);
            if (a == null)
            {
                RateWarn("botintent.noactor", $"SubmitBotIntent：actor {actorId} 不存在");
                return;
            }
            if (!a.IsBot)
            {
                if (!_nonBotActorWarned)
                {
                    _nonBotActorWarned = true;
                    Game.Logger.Warn(Tag,
                        $"SubmitBotIntent：actor {actorId}（{a.Name}）不是机器人，意图被忽略（本条只报一次）");
                }
                return;
            }

            _botIntents[actorId] = intent;
        }

        // ==================================================================
        //  比赛控制（H 菜单 / 记分板）
        // ==================================================================
        public void RestartRound()
        {
            if (!_running)
            {
                Game.Logger.Warn(Tag, "RestartRound：比赛未开始，已忽略");
                return;
            }
            Game.Logger.Info(Tag, $"管理员重开本回合（第 {Round.RoundNumber} 回合冻结阶段重启，比分不变）");
            Round.PrepareRound(Round.RoundNumber, again: true);
        }

        public void RestartMatch()
        {
            if (_cfg == null)
            {
                Game.Logger.Warn(Tag, "RestartMatch：没有配置，已忽略");
                return;
            }
            Game.Logger.Info(Tag, "管理员重开整场比赛（比分与金钱全部复位）");
            var cfg = _cfg;
            Start(cfg);
        }

        public void SetPaused(bool paused)
        {
            if (!_running)
            {
                Game.Logger.Warn(Tag, $"SetPaused({paused})：比赛未开始，已忽略");
                return;
            }
            if (_paused == paused) return;

            _paused = paused;
            if (paused)
            {
                _pauseStart = Now;
                Game.Logger.Info(Tag, "比赛已暂停：模拟时间停止推进");
            }
            else
            {
                // 把"绝对时间"类型的字段整体后移，抵消暂停时长 —— 否则玩家可以靠暂停白跳过换弹/切枪冷却。
                var offset = Time.time - _pauseStart;
                if (offset > 0f) ShiftAbsoluteTimes(offset);
                Game.Logger.Info(Tag, $"比赛已恢复：模拟时间补偿 {offset:F2}s");
            }
            WriteHudSnapshot(0f);
        }

        public void ChangeTeam(CsTeam team)
        {
            if (!_running)
            {
                Game.Logger.Warn(Tag, "ChangeTeam：比赛未开始，已忽略");
                return;
            }
            var a = _local;
            if (a == null)
            {
                Game.Logger.Warn(Tag, "ChangeTeam：本地玩家不存在，已忽略");
                return;
            }
            if (team != CsTeam.T && team != CsTeam.CT && team != CsTeam.Spectator)
            {
                Game.Logger.Warn(Tag, $"ChangeTeam：非法阵营 {team}，已忽略");
                return;
            }
            if (a.Team == team)
            {
                Game.Logger.Info(Tag, $"ChangeTeam：{a.Name} 已经在 {team}，无需换边");
                return;
            }

            if (a.HasBomb) Bomb.OnCarrierLost(a);

            var from = a.Team;
            a.Team = team;
            a.IsAlive = false;

            // 换阵营 = 重新按新阵营规则发装备与金钱（官方行为）。
            a.Money = _cfg.StartMoney;
            Inventory.ClearLoadout(a);
            Inventory.ApplyDefaultLoadout(a);
            a.InBuyZone = false;
            _spectateIndex = -1;

            Game.Logger.Info(Tag, $"{a.Name} 换阵营：{from} → {team}，装备与金钱已按新阵营重置（${a.Money}）");

            if (team != CsTeam.Spectator && Round.Phase != CsRoundPhase.MatchEnd)
            {
                RespawnActor(a, false);
            }
        }

        public void ForceEndRound(CsTeam winner, CsRoundEndReason reason)
        {
            if (!_running)
            {
                Game.Logger.Warn(Tag, "ForceEndRound：比赛未开始，已忽略");
                return;
            }
            if (winner != CsTeam.T && winner != CsTeam.CT)
            {
                Game.Logger.Warn(Tag, $"ForceEndRound：胜方非法（{winner}），已忽略");
                return;
            }
            Game.Logger.Info(Tag, $"强制结束回合：胜方 {winner}，原因 {reason}");
            Round.EndRound(winner, reason);
        }

        // ==================================================================
        //  事件（表现层订阅）
        // ==================================================================
        public event Action<CsKillEvent> OnKill;
        public event Action<CsRoundEndInfo> OnRoundEnd;
        public event Action OnMatchEnd;
        public event Action<CsActor, int, bool, bool> OnDamaged;
        public event Action<CsActor, string> OnMessage;
        public event Action OnBombStateChanged;

        internal void RaiseKill(in CsKillEvent e)
        {
            try { OnKill?.Invoke(e); }
            catch (Exception ex) { Game.Logger.Error(Tag, $"OnKill 订阅者抛异常：{ex.Message}", ex); }
        }

        internal void RaiseRoundEnd(in CsRoundEndInfo info)
        {
            try { OnRoundEnd?.Invoke(info); }
            catch (Exception ex) { Game.Logger.Error(Tag, $"OnRoundEnd 订阅者抛异常：{ex.Message}", ex); }
        }

        internal void RaiseMatchEnd()
        {
            try { OnMatchEnd?.Invoke(); }
            catch (Exception ex) { Game.Logger.Error(Tag, $"OnMatchEnd 订阅者抛异常：{ex.Message}", ex); }
        }

        internal void RaiseDamaged(CsActor victim, int dmg, bool headshot, bool lethal)
        {
            try { OnDamaged?.Invoke(victim, dmg, headshot, lethal); }
            catch (Exception ex) { Game.Logger.Error(Tag, $"OnDamaged 订阅者抛异常：{ex.Message}", ex); }
        }

        internal void RaiseBombStateChanged()
        {
            try { OnBombStateChanged?.Invoke(); }
            catch (Exception ex) { Game.Logger.Error(Tag, $"OnBombStateChanged 订阅者抛异常：{ex.Message}", ex); }
        }

        /// <summary>发系统/无线电消息（HUD 消息栏 + 事件总线）。</summary>
        internal void PublishMessage(CsActor who, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try { OnMessage?.Invoke(who, text); }
            catch (Exception ex) { Game.Logger.Error(Tag, $"OnMessage 订阅者抛异常：{ex.Message}", ex); }

            CsHudSnapshot.LastMessage = text;
            CsHudSnapshot.LastMessageTime = Now;
            EmitEvent(Events.GameMessage, text);

            Game.Logger.Info(Tag, who != null ? $"[消息] {who.Name}: {text}" : $"[消息] {text}");
        }

        internal void EmitEvent(string name)
        {
            if (Game.Event == null)
            {
                RateWarn("event.null", $"Game.Event 为空（引擎未 Launch？），事件被丢弃：{name}");
                return;
            }
            Game.Event.Emit(name);
        }

        internal void EmitEvent<T>(string name, T arg)
        {
            if (Game.Event == null)
            {
                RateWarn("event.null", $"Game.Event 为空（引擎未 Launch？），事件被丢弃：{name}");
                return;
            }
            Game.Event.Emit(name, arg);
        }

        internal void EmitEvent<T1, T2>(string name, T1 a1, T2 a2)
        {
            if (Game.Event == null)
            {
                RateWarn("event.null", $"Game.Event 为空（引擎未 Launch？），事件被丢弃：{name}");
                return;
            }
            Game.Event.Emit(name, a1, a2);
        }

        // ==================================================================
        //  参赛者创建 / 出生 / 装备
        // ==================================================================
        private void CreateLocalPlayer()
        {
            var team = _cfg.PlayerTeam;
            if (team != CsTeam.T && team != CsTeam.CT) team = CsTeam.CT;

            var a = new CsActor
            {
                Id = _nextActorId++,
                Name = string.IsNullOrEmpty(_cfg.PlayerName) ? "Player" : _cfg.PlayerName,
                Team = team,
                IsBot = false,
                Difficulty = _botDifficulty,
                Money = _cfg.StartMoney,
                IsAlive = false,
            };
            Inventory.ClearLoadout(a);
            Inventory.ApplyDefaultLoadout(a);
            _actors.Add(a);
            _local = a;
        }

        private void CreateConfiguredBots()
        {
            var perTeam = _cfg.BotsPerTeam;
            if (perTeam <= 0) return;

            if (perTeam > CsMatchConst.MaxTeamSize) perTeam = CsMatchConst.MaxTeamSize;

            for (var i = 0; i < perTeam; i++)
            {
                var b = NewBotActor(CsTeam.T, _cfg.BotDifficulty);
                _actors.Add(b);
            }
            for (var i = 0; i < perTeam; i++)
            {
                var b = NewBotActor(CsTeam.CT, _cfg.BotDifficulty);
                _actors.Add(b);
            }
        }

        /// <summary>
        /// 自动平衡（对应参考规格 G1 的 mp_autoteambalance）：只搬机器人，绝不动真人。
        /// 默认配置下 BotsPerTeam=4 会造成"玩家那队多一个"，这里把多出的一个 bot 调到对面。
        /// </summary>
        private void BalanceTeams()
        {
            var moved = 0;
            for (var guard = 0; guard < CsMatchConst.MaxTeamSize; guard++)
            {
                var t = PlayerCount(CsTeam.T);
                var ct = PlayerCount(CsTeam.CT);
                if (Mathf.Abs(t - ct) <= 0) break;

                var from = t > ct ? CsTeam.T : CsTeam.CT;
                var to = from == CsTeam.T ? CsTeam.CT : CsTeam.T;

                CsActor pick = null;
                for (var i = 0; i < _actors.Count; i++)
                {
                    if (_actors[i].IsBot && _actors[i].Team == from) { pick = _actors[i]; break; }
                }
                if (pick == null) break;

                pick.Team = to;
                moved++;
            }

            if (moved > 0)
            {
                Game.Logger.Info(Tag,
                    $"自动平衡：搬运 {moved} 个机器人，最终人数 T={PlayerCount(CsTeam.T)} CT={PlayerCount(CsTeam.CT)}");
            }

            if (PlayerCount(CsTeam.T) == 0 || PlayerCount(CsTeam.CT) == 0)
            {
                Game.Logger.Warn(Tag,
                    $"某队 0 人（T={PlayerCount(CsTeam.T)} CT={PlayerCount(CsTeam.CT)}）—— 该队每回合会立即被判全歼。" +
                    "请提高 CsMatchConfig.BotsPerTeam 或换阵营。");
            }
        }

        private CsActor NewBotActor(CsTeam team, CsBotDifficulty diff)
        {
            var a = new CsActor
            {
                Id = _nextActorId++,
                Name = NextBotName(),
                Team = team,
                IsBot = true,
                Difficulty = diff,
                Money = _cfg.StartMoney,
                IsAlive = false,
            };
            Inventory.ClearLoadout(a);
            Inventory.ApplyDefaultLoadout(a);
            return a;
        }

        private string NextBotName()
        {
            // 名字池用尽后加序号，避免同名（记分板/击杀信息要靠名字区分）。
            var name = BotNames[_botNameCursor % BotNames.Length];
            var round = _botNameCursor / BotNames.Length;
            _botNameCursor++;
            return round == 0 ? name : $"{name}#{round + 1}";
        }

        /// <summary>回合开始：全员复活（满血/满甲按购买保留规则）、回出生点、随机朝向、重置弹药与后坐力。</summary>
        internal void RespawnAllForRound()
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (a.Team != CsTeam.T && a.Team != CsTeam.CT)
                {
                    a.IsAlive = false;
                    continue;
                }
                RespawnActor(a, a.IsAlive);
            }
        }

        internal void RespawnActor(CsActor a, bool keepLoadout)
        {
            if (a == null) return;
            if (a.Team != CsTeam.T && a.Team != CsTeam.CT)
            {
                a.IsAlive = false;
                return;
            }

            a.IsAlive = true;
            a.Health = CsConst.MaxHealth;
            a.IsCrouching = false;
            a.IsWalking = false;
            a.Velocity = Vector3.zero;
            a.Position = SnapSpawnToGround(a, FindSpawnPoint(a));
            // 复活换了列 ⇒ 软地板换成**新落点这一列的地面**（= a.Position.y：SnapSpawnToGround 的返回值
            // 要么是探到的地面、要么是标记 Y，都是这一列的可信高度）。
            // ⛔ 不许"清掉不设"：清掉之后第一帧若因大 dt 一步跨过薄楼板（dust2 的楼板是薄刷子，
            // 其下方是空的 ⇒ 向下射线再也找不到它），角色就会一路穿出去 —— 实测 100s 内 1923 次掉图。
            SetSoftFloor(a.Id, a.Position.y);
            a.Yaw = UnityEngine.Random.Range(0f, CsMatchConst.SpawnYawRandomDeg);
            a.Pitch = 0f;
            a.NextFireTime = 0f;
            a.ReloadEndTime = 0f;
            a.SwitchEndTime = 0f;
            a.ConsecutiveShots = 0;
            a.RecoilPitch = 0f;
            a.RecoilYaw = 0f;
            a.FlashEndTime = 0f;
            a.UseProgress = -1f;
            a.HasBomb = false;
            a.RoundKills = 0;

            if (keepLoadout)
            {
                // 上回合存活 → 保留武器/头盔/拆弹器；护甲补满（"满血/满甲"）；备弹重置。
                if (a.Armor > 0) a.Armor = CsConst.MaxArmor;
                Inventory.ResetAmmo(a);
            }
            else
            {
                // 上回合阵亡 → 丢光装备，只留刀子 + 默认手枪（官方规则）。
                Inventory.ClearLoadout(a);
                Inventory.ApplyDefaultLoadout(a);
            }

            Inventory.SelectBestWeapon(a);

            if (a.IsBot && a.Money < _cfg.StartMoney)
            {
                // 机器人没有独立的赚钱循环之外的补足；此处不做补偿，交由经济系统。
            }

            if (a == _local)
            {
                _zoomHeld = false;
                _hasLocalInput = false;
                EmitEvent(Events.LocalRespawned);
                Game.Logger.Info(Tag, $"本地玩家复活于 {a.Position}（{a.Team}，保留装备={keepLoadout}）");
            }
        }

        private Vector3 FindSpawnPoint(CsActor a)
        {
            if (_map == null || !_map.IsLoaded)
            {
                if (!_mapNotLoadedWarned)
                {
                    _mapNotLoadedWarned = true;
                    Game.Logger.Warn(Tag, "出生点不可用：地图未加载，暂用原点附近的临时点位（本条只报一次）");
                }
                var idx = (int)(a.Id % CsMatchConst.MaxTeamSize);
                return new Vector3(idx * CsConst.PlayerRadius * 2f, 0f, idx * CsConst.PlayerRadius * 2f);
            }

            var marker = a.Team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT;
            var pts = _map.Points(marker);
            var index = (int)(a.Id % Mathf.Max(1, pts != null && pts.Length > 0 ? pts.Length : 1));

            if (pts != null && pts.Length > 0)
            {
                var p = pts[index];
                if (pts.Length == 1)
                {
                    // 只有一个出生标记 → 沿 X 排开，避免叠在一起（步距取 2 倍玩家半径）。
                    p += new Vector3(index * CsConst.PlayerRadius * 2f, 0f, 0f);
                }
                return p;
            }

            RateWarn("spawn.marker.missing", $"地图缺少出生点标记 {marker}，回退到 GetSpawnPoint");

            if (_map.SpawnPointCount > 0)
            {
                return _map.GetSpawnPoint((int)(a.Id % _map.SpawnPointCount));
            }

            RateWarn("spawn.fallback", "地图既无出生点标记也无 SpawnPoint，使用原点附近的临时点位");
            var i2 = (int)(a.Id % CsMatchConst.MaxTeamSize);
            return new Vector3(i2 * CsConst.PlayerRadius * 2f, 0f, i2 * CsConst.PlayerRadius * 2f);
        }

        /// <summary>每回合把 C4 交给一个随机（存活优先）的 T。</summary>
        internal void GiveBombToRandomT()
        {
            var candidates = new List<CsActor>(CsMatchConst.MaxTeamSize);
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                a.HasBomb = false;
                if (a.Team == CsTeam.T && a.IsAlive) candidates.Add(a);
            }

            if (candidates.Count == 0)
            {
                Bomb.CarrierId = 0;
                Bomb.Dropped = false;
                Game.Logger.Warn(Tag, "本回合没有存活的 T，无法指派 C4 携带者");
                return;
            }

            var pick = candidates[UnityEngine.Random.Range(0, candidates.Count)];
            pick.HasBomb = true;
            Bomb.CarrierId = pick.Id;
            Bomb.Dropped = false;
            Bomb.LastKnownPosition = pick.Position;

            Game.Logger.Info(Tag, $"C4 交给 {pick.Name}（{pick.Team}）");
            RaiseBombStateChanged();
        }

        // ==================================================================
        //  回合流程（由 CsRound 回调）
        // ==================================================================
        internal void BeginRoundInternal(int roundNumber)
        {
            // 半场交换必须发生在"重新出生"之前 —— 出生点跟着阵营走。
            if (_cfg.HalfTimeSwap && !Round.HalfSwapped && roundNumber == _cfg.RoundsPerHalf + 1)
            {
                SwapHalves();
            }

            RespawnAllForRound();
            GiveBombToRandomT();
            AutoBuyForBots();

            Game.Logger.Info(Tag,
                $"== 第 {roundNumber} 回合（{Round.PhaseTimerText()}）比分 CT {Round.ScoreCT} : {Round.ScoreT} T " +
                $"存活 T={AliveCount(CsTeam.T)} CT={AliveCount(CsTeam.CT)}");

            PublishMessage(null, $"第 {roundNumber} 回合开始");
            EmitEvent(Events.RoundStarted, roundNumber);
        }

        internal void OnRoundEnded(in CsRoundEndInfo info)
        {
            Game.Logger.Info(Tag,
                $"回合 {info.RoundNumber} 结束：胜方 {info.Winner}（{info.Reason}）比分 CT {info.ScoreCT} : {info.ScoreT} T");

            RaiseRoundEnd(info);

            var winnerName = info.Winner == CsTeam.CT ? "Counter-Terrorists Win" : "Terrorists Win";
            PublishMessage(null, $"{winnerName} — {ReasonText(info.Reason)}");
            WriteHudSnapshot(0f);
            EmitEvent(Events.RoundEnded, info.Reason, info.Winner);
        }

        internal void EndMatchInternal()
        {
            var winner = Round.ScoreT > Round.ScoreCT ? CsTeam.T
                : Round.ScoreCT > Round.ScoreT ? CsTeam.CT
                : CsTeam.Spectator;

            var text = winner == CsTeam.Spectator
                ? $"平局！CT {Round.ScoreCT} : {Round.ScoreT} T"
                : $"比赛结束：{(winner == CsTeam.CT ? "反恐精英" : "恐怖分子")}获胜 " +
                  $"CT {Round.ScoreCT} : {Round.ScoreT} T";

            Game.Logger.Info(Tag, text + $"（共 {Round.RoundNumber} 回合）");

            if (winner == CsTeam.Spectator)
            {
                Game.Logger.Warn(Tag, "比赛以平局收场（当前未实现加时赛 —— 参考规格只要求 30 回合内定胜负）");
            }

            PublishMessage(null, text);
            RaiseMatchEnd();
            EmitEvent(Events.MatchEnded);
        }

        /// <summary>半场交换：阵营互换 + 比分显示互换（比分跟着"人"走）。</summary>
        internal void SwapHalves()
        {
            Round.HalfSwapped = true;

            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (a.Team == CsTeam.T) a.Team = CsTeam.CT;
                else if (a.Team == CsTeam.CT) a.Team = CsTeam.T;
            }
            _spectateIndex = -1;

            Round.SwapScoresAndStreaks();
            Economy.ResetLossStreaks();

            Bomb.Reset();

            Game.Logger.Info(Tag,
                $"半场交换：T ↔ CT 阵营互换，比分随之互换 → 现在 CT {Round.ScoreCT} : {Round.ScoreT} T（连败奖励已重置）");

            PublishMessage(null, $"半场交换：CT {Round.ScoreCT} : {Round.ScoreT} T");
        }

        internal static string ReasonText(CsRoundEndReason r)
        {
            switch (r)
            {
                case CsRoundEndReason.BombExploded: return "炸弹爆炸";
                case CsRoundEndReason.BombDefused: return "炸弹被拆除";
                case CsRoundEndReason.AllTargetsEliminated: return "一方被全歼";
                case CsRoundEndReason.TimeExpired: return "时间耗尽";
                default: return "回合结束";
            }
        }

        // ==================================================================
        //  本地玩家移动解算
        // ==================================================================
        private void UpdateLocalPlayer(float dt, float now)
        {
            var a = _local;
            if (a == null || !a.IsAlive) return;
            if (!_hasLocalInput) return;

            var inp = _localInput;

            a.Yaw = inp.Yaw;
            a.Pitch = Mathf.Clamp(inp.Pitch, -CsMatchConst.PitchLimit, CsMatchConst.PitchLimit);
            a.IsCrouching = inp.Crouch;
            a.IsWalking = inp.Walk && !inp.Crouch;

            var def = a.ActiveDef;
            _zoomHeld = inp.Zoom && def != null && def.Class == CsWeaponClass.Sniper;

            // 冻结期禁止移动（官方行为）：只允许转视角。
            if (Round.Phase == CsRoundPhase.Freeze)
            {
                a.Velocity = Vector3.zero;
                a.IsWalking = false;
                return;
            }

            if (Round.Phase != CsRoundPhase.Live)
            {
                a.Velocity = Vector3.zero;
                return;
            }

            // 朝向 → 世界方向（Yaw=0 时面向 +Z；Right = Cross(Up, Forward)）。
            var rad = a.Yaw * Mathf.Deg2Rad;
            var forward = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
            var right = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));

            var dir = right * inp.Move.x + forward * inp.Move.y;
            dir.y = 0f;
            if (dir.sqrMagnitude > 1f) dir.Normalize();

            var speed = CsInventory.MovementSpeed(a);
            a.Velocity = new Vector3(dir.x * speed, a.Velocity.y, dir.z * speed);

            if (inp.Jump && a.OnGround && now >= _localNextJumpTime)
            {
                a.Velocity.y = CsConst.JumpSpeed;
                a.OnGround = false;
                _localNextJumpTime = now + CsMatchConst.JumpRepeatDelay;
            }

            // 射击（开枪后坐力/弹药由模拟扣，射线由 Module/Combat 负责）。
            if (inp.Fire) Inventory.TryDischarge(a, now, out _, localPlayer: true);

            StepActorPhysics(a, dt);

            // 每帧把"是否按住 E"同步给炸弹系统（这样松开 E / 阵亡都能被检测到）。
            Bomb.SetUseState(a, _localUseHeld && Round.Phase == CsRoundPhase.Live && a.IsAlive, now);

            // 拾取：走到掉落的 C4 旁自动捡起。
            Bomb.TryPickupDropped(a);
        }

        /// <summary>位移 + 重力 + 地面吸附；碰撞走 <see cref="ICsMap.ResolveMove"/>（分轴滑墙 + 台阶）。</summary>
        internal void StepActorPhysics(CsActor a, float dt)
        {
            if (a == null || !a.IsAlive) return;

            if (_map == null || !_map.IsLoaded)
            {
                // 没有地图就没有"空间事实"，宁可站住也不要乱飘。
                // 这是每帧的高频路径 —— 降频：首次 + 每 50 次。
                RateWarn("move.nomap",
                    $"移动被挂起：地图{( _map == null ? "未绑定" : "尚未加载")}（ResolveMove/SampleGround 不可用）");
                a.Velocity = Vector3.zero;
                a.OnGround = false;
                return;
            }

            a.Velocity.y -= CsConst.Gravity * dt;
            if (a.Velocity.y < CsConst.MaxFallSpeed) a.Velocity.y = CsConst.MaxFallSpeed;
            // 原版口径的硬上限（`sv_maxvelocity`，出处见 CsMatchConst.TerminalFallSpeed）：
            // 下落速度**必须有上界**（"无限加速下坠"是本次要修的三个症状之一）。
            // 既有的 CsConst.MaxFallSpeed(-30) 更严格 ⇒ 实际生效的仍是它 —— 这里不放宽既有钳制。
            a.Velocity.y = Mathf.Max(a.Velocity.y, -CsMatchConst.TerminalFallSpeed);

            var from = a.Position;
            var to = from + a.Velocity * dt;
            var resolved = _map.ResolveMove(from, to, CsConst.PlayerRadius);

            // 撞墙 → 把该轴速度清掉（ResolveMove 已经把位置钳住了）。
            var want = to - from;
            var got = resolved - from;
            var ratio = CsMatchConst.WallBlockVelocityRatio;
            if (Mathf.Abs(want.x) > 0.0001f && Mathf.Abs(got.x) < Mathf.Abs(want.x) * ratio) a.Velocity.x = 0f;
            if (Mathf.Abs(want.z) > 0.0001f && Mathf.Abs(got.z) < Mathf.Abs(want.z) * ratio) a.Velocity.z = 0f;

            var groundY = _map.SampleGround(resolved, CsMatchConst.GroundProbeDrop);
            if (!float.IsNegativeInfinity(groundY))
            {
                // 探测成功 ⇒ 记住这一列的地面高度 —— 它是下面"探测失败"分支唯一能依靠的软地板。
                _lastGroundY[a.Id] = groundY;
                _softFloorMisses[a.Id] = 0;

                var touching = resolved.y <= groundY + CsConst.GroundCheckDistance;
                if (touching && a.Velocity.y <= 0f)
                {
                    resolved.y = groundY;
                    a.Velocity.y = 0f;
                    a.OnGround = true;
                }
                else
                {
                    a.OnGround = false;
                }
            }
            else
            {
                // ★ 本帧向下 GroundProbeDrop(15m) 内**没有任何世界面** ⇒ 常规贴地判定失效。
                // 原版 bug：这种情况直接置 OnGround=false ⇒ 每帧继续加速、穿过一切、y 一路到 -2000
                //（用户实测的"往下掉的时候穿透所有障碍掉出地图外"）。
                // 用"最近一次成功探测到的地面 Y"（软地板）接住它 —— 只有真的下穿到那条线时才接。
                // 老化计数：本帧算一次"连续探不到"（探测成功即清零），按**帧**而不是按秒，
                // 因为冻结期 / 无输入时不步进，按秒会在空窗里白走完（见 SoftFloorKeepFrames 的注释）。
                _softFloorMisses.TryGetValue(a.Id, out var miss);
                _softFloorMisses[a.Id] = miss + 1;

                if (TrySoftFloor(a, out var softY))
                {
                    if (a.Velocity.y <= 0f && resolved.y <= softY + CsConst.GroundCheckDistance)
                    {
                        resolved.y = softY;
                        a.Velocity.y = 0f;
                        a.OnGround = true;
                        // 刷新：**正在踩着它**就是它仍然有效的证据（否则老化计数走完 ⇒ 又掉一次 ⇒
                        // "掉→拉回→再掉"的循环）。探测失败期间只有这一处能刷新。
                        _lastGroundY[a.Id] = softY;
                        _softFloorMisses[a.Id] = 0;
                        RateWarn("fall.softfloor",
                            $"{a.Name} 脚下探不到地面（向下 {CsMatchConst.GroundProbeDrop:F0}m 内无世界面），" +
                            $"用最近一次探测到的地面 y={softY:F2} 接住（软地板）");
                    }
                    else
                    {
                        a.OnGround = false;
                    }
                }
                else
                {
                    a.OnGround = false;
                }
            }

            a.Position = resolved;

            // 掉图兜底：一旦某帧的落体距离超过 SampleGround 的探测深度（默认 8m），就再也探不到地面，
            // 之后**每帧继续加速下坠**（y 一路到 -2000），表现为"角色不见了 / 视线查询距离 500m+ 刷屏"。
            // 这里把它拉回出生点，避免一次意外变成永久故障。
            if (a.Position.y < CsMatchConst.FallRecoverY)
            {
                var fellFrom = a.Position.y;
                var back = FindRecoverPoint(a, out var verified);
                RateWarn("fall.recover",
                    $"{a.Name} 掉出地图（y={fellFrom:F1} < {CsMatchConst.FallRecoverY:F0}），已拉回 {back}" +
                    (verified ? "（落点已校验）" : "（⚠️ 无校验通过的候选点，沿用标记点）"));
                // §1.3 要求：拉回必须留一条 Info 说明"从哪个 y 拉回到哪个点"（RateWarn 只打第 1 次 + 每 1000 次）。
                Game.Logger.Info(Tag,
                    $"{a.Name} 掉图恢复：从 y={fellFrom:F1} 拉回到 ({back.x:F2},{back.y:F2},{back.z:F2})（{a.Team}）");
                a.Position = back;
                a.Velocity = Vector3.zero;
                a.OnGround = false;
                // 落点换了 ⇒ 软地板换成**新落点的高度**（⛔ 不许清掉不设：清掉后第一帧若因大 dt
                // 一步跨过薄楼板，就再也探不到地面，直接又掉一次 —— 那就是"掉→拉回→再掉"的循环）。
                SetSoftFloor(a.Id, back.y);
            }
        }

        /// <summary>
        /// 出生点吸附：**相信标记 Y**，只做小幅校正（§1.1 两段式，判据见 <see cref="TrySampleSpawnGround"/>）。
        /// 探不到 / 差太远 ⇒ **就用标记 Y**（标记 Y = BSP 实体脚底，是可信真值），
        /// ⛔ 绝不返回 -Inf 或"凭空抬高"的坏值（那正是"一进游戏就从天上掉"的成因）。
        /// </summary>
        private Vector3 SnapSpawnToGround(CsActor a, Vector3 spawn)
        {
            if (_map == null || !_map.IsLoaded) return spawn;

            if (TrySampleSpawnGround(spawn, out var ground))
            {
                return new Vector3(spawn.x, ground, spawn.z);
            }

            RateWarn("spawn.noground",
                $"出生点 ({spawn.x:F1},{spawn.y:F1},{spawn.z:F1}) 的 ±{CsMatchConst.SpawnGroundProbeDrop:F0}m 内没有" +
                $"与标记高度一致的面，**沿用标记高度** {spawn.y:F2}（标记 Y = BSP 实体脚底，是可信真值；{a?.Name}）");
            return spawn;
        }

        /// <summary>
        /// §1.1 的两段式出生点判据：**从标记点上方 <see cref="CsMatchConst.SpawnGroundProbeUp"/>m
        /// 向下探 <see cref="CsMatchConst.SpawnGroundProbeDrop"/>m**，命中且与标记 Y 相差
        /// ≤ <see cref="CsMatchConst.SpawnGroundSnapTolerance"/> ⇒ 认为是"可信地面"。
        /// 返回 false = 标记点附近没有可信地面（调用方**沿用标记 Y**）。
        ///
        /// <para>为什么是"小幅校正"：标记 Y 就是 BSP 实体的脚底
        /// （`原版资源/解包产物/dust2_build.py:657-665` 的 <c>origin.z − 36</c> 再 <c>to_u</c>），
        /// 是可信真值。⛔ **不许**改回"从 +100m 往下取第一个命中面" —— 那会抓到出生点头顶的
        /// 横板/墙顶（实测：BSP 真值 CT 脚底 <c>-3.15</c>，运行时却复活在 y=<c>2.44</c>/<c>3.47</c>）。</para>
        /// </summary>
        private bool TrySampleSpawnGround(Vector3 spawn, out float groundY)
        {
            groundY = 0f;
            if (_map == null || !_map.IsLoaded) return false;

            var probe = new Vector3(spawn.x, spawn.y + CsMatchConst.SpawnGroundProbeUp, spawn.z);
            var g = _map.SampleGround(probe, CsMatchConst.SpawnGroundProbeDrop);
            if (float.IsNegativeInfinity(g)) return false;
            if (Mathf.Abs(g - spawn.y) > CsMatchConst.SpawnGroundSnapTolerance) return false;

            groundY = g;
            return true;
        }

        /// <summary>
        /// 掉图后的落点（§1.3）：**轮换候选出生点**（从 <c>a.Id % 点数</c> 起依次 +1），
        /// 并且**先校验**该点探针命中的面与标记 Y 一致（<see cref="TrySampleSpawnGround"/>）才用它。
        ///
        /// <para>为什么必须轮换 + 校验：原来的写法是 <c>SnapSpawnToGround(a, FindSpawnPoint(a))</c> ——
        /// 每次都用**同一个点**，一旦那个点正好是坏点（标记悬空 / 压在建筑里）就成了
        /// "掉 → 拉回坏点 → 再掉"的死循环（`a.Id % 点数` 是确定性的，见 <see cref="FindSpawnPoint"/>）。</para>
        /// </summary>
        private Vector3 FindRecoverPoint(CsActor a, out bool verified)
        {
            verified = false;

            var marker = a.Team == CsTeam.T ? CsMarkers.SpawnT : CsMarkers.SpawnCT;
            var pts = _map != null && _map.IsLoaded ? _map.Points(marker) : null;
            if (pts == null || pts.Length == 0)
            {
                return SnapSpawnToGround(a, FindSpawnPoint(a));
            }

            var start = (int)(a.Id % pts.Length);
            for (var n = 0; n < pts.Length; n++)
            {
                var cand = pts[(start + n) % pts.Length];
                if (TrySampleSpawnGround(cand, out var g))
                {
                    verified = true;
                    return new Vector3(cand.x, g, cand.z);
                }
            }

            // 一个候选都没校验通过（例如标记表整体不可信）⇒ 回到标记 Y ——
            // 它仍是 BSP 脚底，比"继续往外掉"好得多，但必须留痕。
            var fallback = pts[start];
            RateWarn("spawn.recover.unverified",
                $"掉图恢复：{pts.Length} 个候选出生点没有一个能探到与标记 Y 一致的面，沿用标记点 " +
                $"({fallback.x:F1},{fallback.y:F1},{fallback.z:F1})（{a.Name}）");
            return fallback;
        }

        /// <summary>设定某 actor 的软地板（复活 / 掉图拉回后调用：换成新落点那一列的地面高度），并把老化计数清零。</summary>
        private void SetSoftFloor(long actorId, float y)
        {
            _lastGroundY[actorId] = y;
            _softFloorMisses[actorId] = 0;
        }

        /// <summary>取某 actor 的软地板；没有、或连续探不到地面的帧数已超过
        /// <see cref="CsMatchConst.SoftFloorKeepFrames"/> 则返回 false。</summary>
        private bool TrySoftFloor(CsActor a, out float y)
        {
            y = 0f;
            if (!_lastGroundY.TryGetValue(a.Id, out y)) return false;
            _softFloorMisses.TryGetValue(a.Id, out var misses);
            return misses <= CsMatchConst.SoftFloorKeepFrames;
        }

        private void TickActorTimers(float dt, float now)
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (!a.IsAlive) continue;

                // 换弹结算
                if (a.ReloadEndTime > 0f && now >= a.ReloadEndTime)
                {
                    a.ReloadEndTime = 0f;
                    Inventory.CompleteReload(a);
                }

                // 后坐力回复：停火一段时间后开始回落。
                if (a.ConsecutiveShots > 0 && now >= a.NextFireTime + CsMatchConst.RecoilRecoverDelay)
                {
                    // 用 Tick 的 dt（而不是 Time.deltaTime）—— 自检用假时钟快进时才能一起前进。
                    var step = CsMatchConst.RecoilRecoverSpeed * dt;
                    a.RecoilPitch = Mathf.MoveTowards(a.RecoilPitch, 0f, step);
                    a.RecoilYaw = Mathf.MoveTowards(a.RecoilYaw, 0f, step);
                    if (Mathf.Approximately(a.RecoilPitch, 0f) && Mathf.Approximately(a.RecoilYaw, 0f))
                    {
                        a.RecoilPitch = 0f;
                        a.RecoilYaw = 0f;
                        a.ConsecutiveShots = 0;
                    }
                }
            }
        }

        private void UpdateBuyZoneFlags()
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                a.InBuyZone = a.IsAlive && IsInBuyZone(a.Position, a.Team);
            }
        }

        /// <summary>
        /// 冻结期为机器人配枪（机器人 AI 没有"替某个 actor 买枪"的契约入口 —— 见交付回报的未决项）。
        /// 档位取 <see cref="CsBotProfile.BuyBudgetTier"/>：0=便宜 1=中 2=贵。
        /// </summary>
        private void AutoBuyForBots()
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (!a.IsBot || !a.IsAlive) continue;

                var profile = CsBotProfile.For(a.Difficulty);
                var team = a.Team;
                if (team != CsTeam.T && team != CsTeam.CT) continue;

                // 主武器：按档位挑冲锋枪/步枪里买得起的一把。
                if (string.IsNullOrEmpty(a.PrimaryWeapon))
                {
                    var cls = profile.BuyBudgetTier <= 0 ? CsWeaponClass.SMG : CsWeaponClass.Rifle;
                    var pool = CsWeapons.BuyableByClass(cls, team);
                    CsWeaponDef chosen = null;
                    for (var k = 0; k < pool.Count; k++)
                    {
                        var d = pool[k];
                        if (d.Price > a.Money) continue;
                        if (chosen == null) { chosen = d; continue; }
                        var cheaper = d.Price < chosen.Price;
                        var better = d.Price > chosen.Price;
                        if (profile.BuyBudgetTier >= 2 ? better : cheaper) chosen = d;
                    }
                    if (chosen != null)
                    {
                        Economy.Add(a, -chosen.Price, $"机器人自动购买 {chosen.DisplayName}");
                        Inventory.GivePurchased(a, chosen, Now);
                    }
                }

                // 护甲
                var vest = CsWeapons.Get(CsWeapons.Vest);
                var vestHelm = CsWeapons.Get(CsWeapons.VestHelm);
                if (vest != null && vestHelm != null && a.Armor <= 0)
                {
                    if (profile.BuyBudgetTier >= 2 && a.Money >= vestHelm.Price)
                    {
                        Economy.Add(a, -vestHelm.Price, "机器人自动购买 Kevlar + Helmet");
                        Inventory.GivePurchased(a, vestHelm, Now);
                    }
                    else if (a.Money >= vest.Price + vestHelm.Price || (profile.BuyBudgetTier >= 1 && a.Money >= vest.Price))
                    {
                        Economy.Add(a, -vest.Price, "机器人自动购买 Kevlar Vest");
                        Inventory.GivePurchased(a, vest, Now);
                    }
                }

                // CT 拆弹器
                if (team == CsTeam.CT && !a.HasDefuser)
                {
                    var kit = CsWeapons.Get(CsWeapons.Defuser);
                    if (kit != null && a.Money >= kit.Price)
                    {
                        Economy.Add(a, -kit.Price, "机器人自动购买 Defusal Kit");
                        Inventory.GivePurchased(a, kit, Time.time);
                    }
                }
            }
        }

        private void ShiftAbsoluteTimes(float delta)
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (a.NextFireTime > 0f) a.NextFireTime += delta;
                if (a.ReloadEndTime > 0f) a.ReloadEndTime += delta;
                if (a.SwitchEndTime > 0f) a.SwitchEndTime += delta;
                if (a.FlashEndTime > 0f) a.FlashEndTime += delta;
            }
            _localNextJumpTime += delta;
        }

        // ==================================================================
        //  机器人执行
        // ==================================================================
        private void UpdateBots(float dt, float now)
        {
            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (!a.IsBot || !a.IsAlive) continue;

                if (!_botIntents.TryGetValue(a.Id, out var intent)) continue;

                var profile = CsBotProfile.For(a.Difficulty);

                // ---- 瞄准：向 AimPoint 转视角，并按难度加随机误差 ----
                AimBotAt(a, intent.AimPoint, profile, dt, now);

                // ---- 切枪 / 换弹 ----
                if (!string.IsNullOrEmpty(intent.SwitchTo) && intent.SwitchTo != a.ActiveWeapon)
                {
                    Inventory.SwitchWeapon(a, intent.SwitchTo, now);
                }
                if (intent.Reload) Inventory.Reload(a, now);
                // 必须双向赋值：单向只置 true 会让机器人在第一次蹲下后整回合站不起来（0.34 倍速）。
                a.IsCrouching = intent.Crouch;
                a.IsWalking = intent.Walk && !a.IsCrouching;

                // ---- 移动 ----
                if (Round.Phase == CsRoundPhase.Live)
                {
                    var move = intent.Move;
                    move.y = 0f;
                    if (move.sqrMagnitude > 1f) move.Normalize();

                    var speed = CsInventory.MovementSpeed(a);
                    a.Velocity = new Vector3(move.x * speed, a.Velocity.y, move.z * speed);

                    if (intent.Jump && a.OnGround)
                    {
                        a.Velocity.y = CsConst.JumpSpeed;
                        a.OnGround = false;
                    }
                }
                else
                {
                    a.Velocity = new Vector3(0f, a.Velocity.y, 0f);
                }

                StepActorPhysics(a, dt);
                a.InBuyZone = IsInBuyZone(a.Position, a.Team);

                // ---- 下包 / 拆包 ----
                Bomb.SetUseState(a, intent.Use && Round.Phase == CsRoundPhase.Live, now);

                // ---- 开火 ----
                if (intent.Fire && Round.Phase == CsRoundPhase.Live && a.IsAlive)
                {
                    BotTryFire(a, profile, now, intent);
                }
            }
        }

        private void AimBotAt(CsActor a, Vector3 aimPoint, CsBotProfile profile, float dt, float now)
        {
            if (aimPoint == Vector3.zero) return; // 未给瞄准点 → 保持原朝向

            var eye = a.EyePosition;
            var dir = aimPoint - eye;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            // 按难度重采样一个方向误差（不是逐帧随机 —— 逐帧随机会互相抵消，看起来反而很准）。
            if (!_botAimErrorRefreshAt.TryGetValue(a.Id, out var refreshAt) || now >= refreshAt)
            {
                _botAimErrorRefreshAt[a.Id] = now + CsMatchConst.BotAimErrorRefresh;
                var err = profile.AimErrorDegrees;
                _botAimError[a.Id] = new Vector2(
                    UnityEngine.Random.Range(-err, err),
                    UnityEngine.Random.Range(-err, err));
            }
            var e = _botAimError.TryGetValue(a.Id, out var ev) ? ev : Vector2.zero;

            var yawRad = (Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + e.x) * Mathf.Deg2Rad;
            var pitchRad = (Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg + e.y) * Mathf.Deg2Rad;

            var wantYaw = yawRad * Mathf.Rad2Deg;
            var wantPitch = Mathf.Clamp(pitchRad * Mathf.Rad2Deg, -CsMatchConst.PitchLimit, CsMatchConst.PitchLimit);

            var step = profile.AimSpeedDegrees * dt;
            a.Yaw = Mathf.MoveTowardsAngle(a.Yaw, wantYaw, step);
            a.Pitch = Mathf.MoveTowards(a.Pitch, wantPitch, step);
        }

        private void BotTryFire(CsActor a, CsBotProfile profile, float now, in CsBotIntent intent)
        {
            // 连发节奏：burst 内固定开火，burst 结束后进入 pause。
            if (!_botBurstUntil.TryGetValue(a.Id, out var burstUntil)) burstUntil = 0f;
            if (now >= burstUntil)
            {
                if (!_botFirePauseUntil.TryGetValue(a.Id, out var pauseUntil)) pauseUntil = 0f;
                if (now < pauseUntil) return;

                _botBurstUntil[a.Id] = now + UnityEngine.Random.Range(profile.FireBurstMin, profile.FireBurstMax);
                _botFirePauseUntil[a.Id] = _botBurstUntil[a.Id] +
                    UnityEngine.Random.Range(profile.FirePauseMin, profile.FirePauseMax);
            }

            // 扣弹药成功 = 真的打出了这一发（射速/弹匣/换弹都在 TryDischarge 里校验）。
            //
            // ★ `localPlayer: true` 的**真实语义是"这一发的射线由调用方负责"**（见 CsInventory.TryDischarge
            //   的注释：真人由 Module/Combat/Firearm 打射线，模拟只做扣弹/限速/后坐力）。机器人的"调用方射线"
            //   就是紧随其后的 BotResolveShot —— 所以这里必须传 true，否则 TryDischarge 内部的 FireGun
            //   会**再打一条射线**，一次扣扳机产生两条射线 = 双份伤害 + 双份命中（本地玩家那条路早就防住了
            //   这件事，见 CombatModule 的"射线发数 == 扣弹数"注释）。
            //   ⛔ 不能改成 false：机器人给 intent.AimPoint 的朝向是模拟的 AimBotAt 按 AimSpeedDegrees 追出来的，
            //      实测会长期落后 40°+（Play 日志：`朝向差=42.9° vs 门限=17.0°`），沿那个朝向打出去的子弹
            //      打的是**队友**（实测日志：`Gooseman 命中队友 Minh，友好伤害关闭 → 伤害被忽略`）⇒ hits 恒 0。
            if (!Inventory.TryDischarge(a, now, out var weaponId, localPlayer: true)) return;

            BotResolveShot(a, weaponId, intent.AimPoint, profile);
        }

        /// <summary>
        /// 机器人开火的射线与伤害结算（**这一发唯一的射线**）。
        ///
        /// <para><b>方向为什么用 <paramref name="aimPoint"/> 而不是手上那把枪的实际朝向</b>：
        /// <see cref="AimBotAt"/> 按难度 <c>AimSpeedDegrees</c> 把视角往瞄点转，是**有上限的追赶**，
        /// 实测经常落后 40°+（Play 日志：<c>朝向差=42.9° vs 门限=17.0°</c>）。沿"手上那把枪此刻的朝向"
        /// 开火 = 把"转视角速度上限"当成散布用，子弹打的是队友（实测日志：
        /// <c>Gooseman 命中队友 Minh，友好伤害关闭 → 伤害被忽略</c>）⇒ 机器人一发都打不中（hits 恒 0）。
        /// 所以射线方向取"想打的地方"（<paramref name="aimPoint"/>），与本地玩家"瞄哪儿打哪儿"同一口径。</para>
        ///
        /// <para><b>难度必须继续影响命中 —— 靠 <c>profile.AimErrorDegrees</c> 施加在弹道上</b>：
        /// 既然射线不再跟随"实际朝向"，<see cref="AimBotAt"/> 里那份按难度掷出来的瞄准误差就只剩
        /// "开火门限过不过"的作用了；若不再给弹道本身加误差，三档的命中率就会变成同一水平（实测：
        /// Play 三档 37.6% / 58.7% / 43.6%，不单调）。所以每一发都按
        /// <c>profile.AimErrorDegrees</c>（Easy 6.0° / Normal 3.0° / Hard 1.2°）**重掷一个方向误差** ——
        /// 这正是该字段的字面语义（"瞄准最大误差（度）"），也是 CS 里机器人准度的模型：
        /// 瞄着目标打，但手会抖，抖多大由难度决定。</para>
        ///
        /// <para>伤害结算与本地玩家完全同源（同样走 <see cref="ReportHit"/>，于是伤害衰减 / 护甲 / 爆头 /
        /// 击杀奖励只有一套实现）。</para>
        /// </summary>
        private void BotResolveShot(CsActor shooter, string weaponId, Vector3 aimPoint, CsBotProfile profile)
        {
            var def = CsWeapons.Get(weaponId);
            if (def == null)
            {
                RateWarn("botfire.nodef", $"机器人开火但拿不到武器定义（weaponId={weaponId ?? "null"}）");
                return;
            }

            // 刀 / 手雷 / C4 由模拟内部直接结算（CsInventory.FireKnife / ThrowGrenade / 下包），
            // 这里再打一条射线就是双份伤害。只有枪械需要"调用方射线"。
            if (def.Class == CsWeaponClass.Knife ||
                def.Class == CsWeaponClass.Grenade ||
                def.Class == CsWeaponClass.Bomb)
            {
                return;
            }

            var origin = new Vector3(shooter.Position.x, shooter.Position.y + CsConst.EyeHeight, shooter.Position.z);
            var delta = aimPoint - origin;
            var dist = delta.magnitude;
            if (dist < 0.001f)
            {
                RateWarn("botfire.zeroaim",
                    $"{shooter.Name} 的瞄点与眼睛重合（瞄点={aimPoint}）→ 这一发无法确定方向，已丢弃");
                return;
            }
            var idealDir = delta / dist;

            // 射程取"武器有效射程"与"到瞄点的距离"的较大者：瞄点在射程内就一定能打到，
            // 否则也不会因为射程判定把命中吃掉（命中与否交给物理射线）。
            var reach = Mathf.Max(def.Range, dist + 1f);
            var pellets = Mathf.Max(1, def.Pellets);
            var aimError = profile.AimErrorDegrees;

            for (var pellet = 0; pellet < pellets; pellet++)
            {
                // ① 难度的瞄准误差（每一发/每颗弹丸重掷）：Easy 抖得多、Hard 几乎不抖。
                //    这是"三档准度差异"的唯一来源 —— 射线方向本身取的是理想瞄点，不含任何难度成分。
                var dir = aimError > 0f ? ApplySpread(idealDir, aimError) : idealDir;

                // ② 霰弹：一发内每颗弹丸各自带武器散布（与本地玩家"一发内循环 Pellets"同一口径）。
                var d = pellets > 1 ? ApplySpread(dir, def.Spread) : dir;

                _botShotRays++;

                // ⛔ 必须用**全部命中**再挑最近的"非自己"那一发，不能只取第一个：
                //    射线起点就在射手脚底+EyeHeight，正好在自己胸/腹受击体内部 ⇒ 单发 Raycast 的第一个命中
                //    一定是**自己**；只取第一个会把这一发直接丢弃 ⇒ 机器人永远打不中人（实测 hits 恒 0）。
                //    本地玩家那条路（Module/Combat/Firearm）本来就是遍历全部命中再跳过自己的，这里对齐同一口径。
                var count = Physics.RaycastNonAlloc(origin, d, _hitBuffer, reach, ~0, QueryTriggerInteraction.Ignore);

                if (count >= _hitBuffer.Length)
                {
                    // 缓冲被打满 → 更远的命中会被丢掉（可能漏掉真正该打中的那个人）。不正常，必须留痕。
                    RateWarn("botfire.bufferfull",
                        $"机器人射线命中数达到缓冲上限 {_hitBuffer.Length}（起点 {origin}，方向 {d}，射程 {reach:F1}m）" +
                        "—— 更远的命中会被丢弃，请提高 CsMatchConst.BotHitBufferSize");
                }

                CsHitboxProxy proxy = null;
                var hit = default(RaycastHit);
                var bestDist = float.MaxValue;
                for (var i = 0; i < count; i++)
                {
                    var h = _hitBuffer[i];
                    var ph = h.collider != null ? h.collider.GetComponentInParent<CsHitboxProxy>() : null;
                    if (ph != null && ph.ActorId == shooter.Id)
                    {
                        _botShotSelfSkipped++;
                        continue;   // 自己的受击体：跳过（起点在体内，必然命中）
                    }
                    if (h.distance >= bestDist) continue;
                    bestDist = h.distance;
                    hit = h;
                    proxy = ph;
                }

                if (proxy == null)
                {
                    // 最近的那个命中不是角色受体 = 墙 / 地面 / 道具（或**目标的名牌**）—— 这一发不结算。
                    // 现场证据：记下挡它的是哪个碰撞体（名字 / 层 / 距离），否则"打不中"只能猜。
                    if (count == 0) _botShotNothing++;
                    else _botShotBlocked++;
                    _lastBotShotBlocker = count == 0
                        ? "（一个碰撞体都没命中）"
                        : (hit.collider != null
                            ? $"{hit.collider.name}（层 {hit.collider.gameObject.layer}，{hit.distance:F1}m）"
                            : "（collider == null）");
                    continue;
                }

                var victim = Find(proxy.ActorId);
                if (victim == null || !victim.IsAlive)
                {
                    // 受击体还在场上但 actor 已死亡/不存在（视图回收慢一帧）→ 异常分支，留痕。
                    _botShotStaleProxy++;
                    RateWarn("botfire.stalevictim",
                        $"机器人射线命中的受体 ActorId={proxy.ActorId} 找不到存活 actor（{shooter.Name} 的这一发未结算）");
                    continue;
                }

                _botShotHits++;
                ReportHit(new CsHitInfo
                {
                    ShooterId = shooter.Id,
                    VictimId = victim.Id,
                    WeaponId = weaponId,
                    Hitbox = proxy.Hitbox,
                    Point = hit.point,
                    Normal = hit.normal,
                    Distance = hit.distance,
                    ThroughWall = false,
                });
            }

            LogBotShotDiag();
        }

        /// <summary>机器人射线结算统计归零（每场比赛重新开始计数）。</summary>
        private void ResetBotShotStats()
        {
            _botShotRays = 0;
            _botShotHits = 0;
            _botShotBlocked = 0;
            _botShotNothing = 0;
            _botShotSelfSkipped = 0;
            _botShotStaleProxy = 0;
            _lastBotShotBlocker = null;
            _nextBotShotDiagAt = 0f;
        }

        /// <summary>
        /// 机器人射线结算的累计统计（交付证据：<c>发射 N 命中 M 被墙挡 X</c>）。
        /// 高频路径 → 按时间降频：首次必打（第一发即证据），之后每
        /// <see cref="CsMatchConst.BotShotDiagInterval"/> 秒一条。
        /// </summary>
        private void LogBotShotDiag()
        {
            if (_botShotRays == 0) return;
            var now = Now;
            if (now < _nextBotShotDiagAt) return;
            _nextBotShotDiagAt = now + CsMatchConst.BotShotDiagInterval;

            var acc = _botShotRays > 0 ? (float)_botShotHits / _botShotRays : 0f;
            Game.Logger.Info(Tag,
                $"机器人射线结算累计：发射={_botShotRays} 命中={_botShotHits}（{acc:P1}）" +
                $" 被墙/地面挡={_botShotBlocked} 未命中任何碰撞体={_botShotNothing}" +
                $" 跳过自身受体={_botShotSelfSkipped} 受体对应 actor 已失效={_botShotStaleProxy}" +
                $" 最近一次挡点={_lastBotShotBlocker ?? "无"}");
        }

        /// <summary>
        /// 把方向绕开一个随机小角。两处用：① 机器人每一发的**难度瞄准误差**（<c>profile.AimErrorDegrees</c>）；
        /// ② 机器人霰弹的武器散布。口径与本地玩家 / 模拟内部的 <c>ApplySpread</c> 一致（同一套欧拉角扰动）。
        /// </summary>
        private static Vector3 ApplySpread(Vector3 dir, float spreadDegrees)
        {
            if (spreadDegrees <= 0f) return dir;
            var yaw = UnityEngine.Random.Range(-spreadDegrees, spreadDegrees);
            var pitch = UnityEngine.Random.Range(-spreadDegrees, spreadDegrees);
            return Quaternion.Euler(pitch, yaw, 0f) * dir;
        }

        // ==================================================================
        //  死亡 / 击杀 / 金钱
        // ==================================================================
        internal void AddMoney(CsActor a, int delta, string reason)
        {
            Economy.Add(a, delta, reason);
        }

        /// <summary>某一方被全歼 / 超时之外的中断（持包者死亡 → 包掉落）。</summary>
        internal void OnActorDied(CsActor victim)
        {
            if (victim == null) return;

            if (victim.HasBomb)
            {
                Bomb.OnCarrierLost(victim);
            }

            if (victim == _local)
            {
                _zoomHeld = false;
                _spectateIndex = -1;
                EmitEvent(Events.LocalDied);
                Game.Logger.Info(Tag, $"本地玩家阵亡（{victim.Team}）→ 进入观战");
            }
        }

        internal void OnKilled(CsActor killer, CsActor victim, string weaponId, bool headshot, long assisterId)
        {
            var ev = new CsKillEvent
            {
                KillerId = killer != null ? killer.Id : 0,
                KillerName = killer != null ? killer.Name : "World",
                KillerTeam = killer != null ? killer.Team : CsTeam.Spectator,
                VictimId = victim.Id,
                VictimName = victim.Name,
                VictimTeam = victim.Team,
                WeaponId = weaponId,
                Headshot = headshot,
                IsSuicide = killer == null || killer.Id == victim.Id,
                AssisterId = assisterId,
            };

            AddKillFeed(ev, Now);
            RaiseKill(ev);

            var headTag = headshot ? "（爆头）" : string.Empty;
            if (ev.IsSuicide)
            {
                Game.Logger.Info(Tag, $"{victim.Name} 自杀/被环境击杀{headTag}");
                PublishMessage(null, $"{victim.Name} 死亡");
            }
            else
            {
                Game.Logger.Info(Tag,
                    $"{killer.Name} [{weaponId}] 击杀 {victim.Name}{headTag}（{killer.Team} vs {victim.Team}）");
            }
        }

        private void AddKillFeed(in CsKillEvent e, float now)
        {
            var item = new CsKillFeedItem
            {
                KillerName = e.KillerName,
                KillerTeam = e.KillerTeam,
                VictimName = e.VictimName,
                VictimTeam = e.VictimTeam,
                WeaponId = e.WeaponId,
                Headshot = e.Headshot,
                BornTime = now,
            };
            _killFeed.Add(item);
            while (_killFeed.Count > CsMatchConst.MaxKillFeedStore) _killFeed.RemoveAt(0);
        }

        private void TickKillFeed(float now)
        {
            for (var i = _killFeed.Count - 1; i >= 0; i--)
            {
                if (now - _killFeed[i].BornTime > CsMatchConst.KillFeedLifetime) _killFeed.RemoveAt(i);
            }
        }

        // ==================================================================
        //  HUD 快照（Cs16.Core.CsHudSnapshot，每帧清空重填列表）
        // ==================================================================
        private void WriteHudSnapshot(float dt)
        {
            if (!_running)
            {
                CsHudSnapshot.Reset();
                return;
            }

            CsHudSnapshot.Valid = true;

            // 受伤方向指示是"剩余显示时间"（倒计时），由本模块负责递减 ——
            // CsHudSnapshot 里哪个字段由谁写见它的类注释：准星/命中标记/闪光归 Combat，
            // 受伤提示（自己被打）只有比赛模拟知道，所以归这里。
            if (CsHudSnapshot.DamageIndicatorTime > 0f)
            {
                CsHudSnapshot.DamageIndicatorTime = Mathf.Max(0f, CsHudSnapshot.DamageIndicatorTime - dt);
            }

            var lp = _local;
            if (lp != null)
            {
                CsHudSnapshot.Team = lp.Team;
                CsHudSnapshot.IsAlive = lp.IsAlive;
                CsHudSnapshot.Health = lp.Health;
                CsHudSnapshot.Armor = lp.Armor;
                CsHudSnapshot.HasHelmet = lp.HasHelmet;
                CsHudSnapshot.Money = lp.Money;

                var def = lp.ActiveDef;
                CsHudSnapshot.WeaponId = def != null ? def.Id : null;
                CsHudSnapshot.WeaponName = def != null ? def.DisplayName : null;
                if (def != null && lp.Ammo.TryGetValue(def.Id, out var ammo))
                {
                    CsHudSnapshot.Mag = ammo.inMag;
                    CsHudSnapshot.Reserve = ammo.reserve;
                }
                else
                {
                    CsHudSnapshot.Mag = 0;
                    CsHudSnapshot.Reserve = 0;
                }
            }

            CsHudSnapshot.IsSpectating = IsSpectating;
            var spec = SpectateTarget;
            CsHudSnapshot.SpectateName = spec != null ? spec.Name : null;
            CsHudSnapshot.IsZoomed = IsZoomed;

            CsHudSnapshot.Phase = Round.Phase;
            CsHudSnapshot.RoundNumber = Round.RoundNumber;
            CsHudSnapshot.PhaseTimeLeft = Round.PhaseTimeLeft;
            CsHudSnapshot.ScoreT = Round.ScoreT;
            CsHudSnapshot.ScoreCT = Round.ScoreCT;
            CsHudSnapshot.BombPlanted = Bomb.Planted;
            CsHudSnapshot.BombTimeLeft = Bomb.Planted ? Bomb.TimeLeft : -1f;
            CsHudSnapshot.UseProgress = Bomb.ActiveUseProgress;

            CsHudSnapshot.InBuyZone = lp != null && lp.IsAlive && lp.InBuyZone;
            CsHudSnapshot.CanBuyNow = CanBuyNow;

            // ---- 击杀信息（每帧清空重填）----
            CsHudSnapshot.KillFeed.Clear();
            for (var i = 0; i < _killFeed.Count; i++)
            {
                CsHudSnapshot.KillFeed.Add(_killFeed[i]);
                if (CsHudSnapshot.KillFeed.Count >= CsConst.MaxKillFeedEntries) break;
            }

            // ---- 雷达（每帧清空重填）----
            CsHudSnapshot.Radar.Clear();

            for (var i = 0; i < _actors.Count; i++)
            {
                var a = _actors[i];
                if (!a.IsAlive) continue;

                var self = lp != null && a.Id == lp.Id;
                var sameTeam = lp != null && a.Team == lp.Team;
                var visible = self || sameTeam;
                if (!visible && lp != null && lp.IsAlive)
                {
                    visible = HasLineOfSight(lp.EyePosition, a.EyePosition, CsConst.BotVisionRange * 2f);
                }

                if (!visible) continue;

                CsHudSnapshot.Radar.Add(new CsRadarDot
                {
                    X = a.Position.x,
                    Z = a.Position.z,
                    Team = a.Team,
                    IsSelf = self,
                    IsBomb = false,
                    IsBombsite = false,
                });
            }

            if (Bomb.Planted || Bomb.Dropped)
            {
                var bp = Bomb.Planted ? Bomb.Position : Bomb.LastKnownPosition;
                CsHudSnapshot.Radar.Add(new CsRadarDot
                {
                    X = bp.x,
                    Z = bp.z,
                    Team = CsTeam.T,
                    IsSelf = false,
                    IsBomb = true,
                    IsBombsite = false,
                });
            }

            AddBombsiteDots(CsMarkers.BombsiteA);
            AddBombsiteDots(CsMarkers.BombsiteB);
        }

        private void AddBombsiteDots(string marker)
        {
            if (_map == null || !_map.IsLoaded) return;
            var pts = _map.Points(marker);
            if (pts == null) return;
            for (var i = 0; i < pts.Length; i++)
            {
                CsHudSnapshot.Radar.Add(new CsRadarDot
                {
                    X = pts[i].x,
                    Z = pts[i].z,
                    Team = CsTeam.Spectator,
                    IsSelf = false,
                    IsBomb = false,
                    IsBombsite = true,
                });
            }
        }

        // ==================================================================
        //  日志降频（首次 + 每 N 次）
        // ==================================================================
        internal void RateWarn(string key, string msg)
        {
            var n = BumpRate(key);
            if (n == 1 || n % CsMatchConst.LogRateEvery == 0)
            {
                Game.Logger.Warn(Tag, $"{msg}（第 {n} 次）");
            }
        }

        internal void RateInfo(string key, string msg)
        {
            var n = BumpRate(key);
            if (n == 1 || n % CsMatchConst.LogRateEvery == 0)
            {
                Game.Logger.Info(Tag, $"{msg}（第 {n} 次）");
            }
        }

        private int BumpRate(string key)
        {
            _rateCounters.TryGetValue(key, out var n);
            n++;
            _rateCounters[key] = n;
            return n;
        }

    }
}
