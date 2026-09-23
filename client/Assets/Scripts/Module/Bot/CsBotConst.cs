using Cs16.Core;
using Cs16.Module.Map;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人模块私有常量（**本项目新增**，不是引擎 API）。
    ///
    /// <para><b>为什么单独一个文件</b>：<see cref="CsConst"/> 是我无权修改的契约文件（任务书 §2：
    /// 只写 <c>Module/Bot/**</c>）。这里集中放"机器人手感/阈值"类数值，目的与 <see cref="CsConst"/> 一致 ——
    /// **业务代码里不出现裸数字，改一处即可调平衡**。交付回报里已列出"建议收敛进 CsConst"的清单。</para>
    ///
    /// <para><b>出处口径（切片K 逐条补）</b>：本文件 66 条的出处分三类，逐条写在各自注释里 ——
    /// ① **规格条款**：<c>策划/策划案/CS1.6单机参考规格.md</c> §2.4 的机器人三档表（反应时间 / 瞄准误差 /
    /// 行为特征）；② **本工程另有定义处的那个文件**（<c>CsBotProfile</c> 三档表在
    /// <c>Module/Match/CsTypes.cs:148</c>；<c>CsMarkers.BombsiteRadius</c> 在 <c>Module/Map/ICsMap.cs:83</c>；
    /// <c>CsMatch.DefuseRadius/PickupRadius</c> 在 <c>Module/Match/CsMatch.cs</c>）；
    /// ③ **本项目新增**（A = CS 1.6 本体**不含机器人 AI** —— 官方 bot 属 Condition Zero / PodBot，
    /// 不在本工程的载体范围内，所以"bot 手感阈值"在 A 里**没有对应量**，只能自定并如实标注）。
    /// ⛔ 其中**确有一个原版对应量、但载体拿不到**的三组（瞄胸高度比例 / 脚步噪声阈值 / C4 无关）已单独登记
    /// <c>策划/差异登记.tsv</c>，不许当成"有出处"。</para>
    ///
    /// <para><b>与 <see cref="CsConst"/> / <see cref="CsBotProfile"/> 的分工</b>（避免双重生效）：
    /// 三档难度参数（反应时间 / 瞄准误差 / 转视角速度 / 连发节奏 / 视野 / 偏好距离 / 买枪档位 / 爆头率 /
    /// 重决策间隔）**一律只从 <see cref="CsBotProfile.For"/> 取**，本文件里不许再写第二套；
    /// 连发节奏与瞄准误差的落地由比赛模拟（agent-03 的 <c>CsMatch.UpdateBots</c>）执行，
    /// 这里只放"模拟没有、AI 才需要"的阈值。</para>
    ///
    /// <para><b>可见性 = public（切片BC 起）</b>：<c>Assets/Scripts/</c> 有独立程序集
    /// <c>Cs16.asmdef</c>，而 Editor 生成器在 <c>Assembly-CSharp-Editor</c> ⇒ <c>internal</c> 跨不过去。
    /// 生成侧（<c>Editor/MapGen/Dust2Builder.cs</c> 的标记点吸附）**必须**用本类
    /// <c>PathSnapRadiusCells</c> 这**同一个半径**，否则"生成侧吸附半径 / 消费侧 snap 半径"
    /// 会各写一份、必然漂移。所以本类由 <c>internal</c> 放宽为 <c>public</c>（⛔ 未改任何常量值、
    /// 未改任何签名，只是可见性）。</para>
    /// </summary>
    public static class CsBotConst
    {
        // ==================== 感知 ====================
        /// <summary>
        /// 目标记忆时长（秒）：看见后丢了视野也不立刻忘（任务书 §4.2）。
        ///
        /// <para><b>它同时是"反应时间要不要重算"的唯一分界</b>：反应计时（
        /// <see cref="CsBotProfile.ReactionTime"/>）绑在**目标身份**上 —— 换了目标、或彻底忘掉这个目标
        /// （丢视野时间 &gt; 它）之后再看，才算"重新首次看见"。
        /// 旧写法用的是另一个更短的阈值（0.5s）：只要视野断续超过 0.5s，
        /// 反应计时就被清零重来 —— 于是"断续可见"的目标**永远过不了反应门槛**（也就永远不开枪）。</para>
        ///
        /// <para>出处：**本项目新增**（bot 感知记忆时长，A 无 bot AI ⇒ 无原版对应量；
        /// 规格 §2.4 只规定三档的"反应时间 / 瞄准误差 / 行为"，不含记忆时长）。</para>
        /// </summary>
        public const float TargetMemorySeconds = 2f;

        /// <summary>交战中的"贴脸"距离（米）：小于它时放弃下包/拆包先应战。
        /// 出处：**本项目新增**（bot 交战取舍阈值；A 无 bot AI，规格 §2.4 未给该阈值）。</summary>
        public const float EnemyTooCloseRange = 5f;

        /// <summary>判定"跑动出声"的水平速度阈值（米/秒）。低于它 = 慢走/站定，不出声。
        /// 出处：**本项目新增**（bot 听觉阈值）。⚠️ 原版对应的"脚步噪声判定"在服务端 <c>mp.dll</c>
        /// （按 <c>mp_footsteps</c> 开关 + 速度档判定），该载体**已在盘、但尚未反汇编**
        /// （<c>原版资源/cs16src/cstrike/dlls/mp.dll</c>，1,640,960 B / SHA256 <c>D7294D9B…1F2974</c>，
        /// 片AW 取回，见 <c>原版资源/清单.md</c>「切片AW」§1）⇒ 取不到 <c>文件:偏移</c> 级出处，
        /// 已登记 <c>策划/差异登记.tsv</c>（#58 的「脚步噪声阈值」条）。</summary>
        public const float RunNoiseSpeed = 2.5f;

        /// <summary>听觉记忆时长（秒）。出处：**本项目新增**（bot 听觉模型参数，A 无 bot AI）。</summary>
        public const float NoiseMemorySeconds = 2f;

        /// <summary>噪声环形缓冲容量（条）。出处：**本项目新增**（bot 听觉模型的实现容量，A 无 bot AI）。</summary>
        public const int NoiseCapacity = 24;

        /// <summary>同一个 actor 的跑动噪声最小间隔（秒）。出处：**本项目新增**（防刷同一噪声，A 无 bot AI）。</summary>
        public const float RunNoiseInterval = 0.4f;

        /// <summary>同一个 actor 的枪声噪声最小间隔（秒）。出处：**本项目新增**（同上）。</summary>
        public const float GunshotNoiseInterval = 0.1f;

        // ==================== 导航 ====================
        /// <summary>路点到达半径（米）：进入它就推进到下一个路点。
        /// 出处：**本项目新增**（导航实现参数；原版无 bot 导航，A 无 bot AI）。</summary>
        public const float WaypointArriveRadius = 2.5f;

        /// <summary>默认目标点到达半径（米）。出处：**本项目新增**（同 <see cref="WaypointArriveRadius"/>）。</summary>
        public const float DefaultObjectiveRadius = 2f;

        /// <summary>
        /// 重求全局路径（引擎 <c>AStar.FindSmoothed</c>）的兜底间隔（秒）：到点就重求一次，兼作"被挤开后自我纠偏"。
        ///
        /// <para>为什么需要一个闸：<c>AStar</c> 一次求解最多展开 <c>DefaultMaxNodes</c>(20000) 个节点，⛔ 不能每帧对
        /// 每个 bot 求一次。<see cref="BotNavigator"/> 只在"没有路径 / 目标格变了 / 路径走完 / 判到卡住"之外再按这个间隔兜底。</para>
        ///
        /// <para>出处：**本项目新增**（导航实现参数，A 无 bot AI；与同文件里 <see cref="StuckCheckInterval"/> 同一量级，
        /// 但**不复用**它 —— 卡住检测的周期与"路径新鲜度"是两件事，混用会让调其中一个时另一个跟着变）。</para>
        /// </summary>
        public const float PathReplanInterval = 1f;

        /// <summary>
        /// "起点/终点格不可走时"向外找最近可走格的半径（格）。
        ///
        /// <para>必要性：引擎 <c>AStar.Find</c> 对"起点不可走"直接返回 null ⇒ 机器人被挤进位图判阻挡的格子
        /// （1 米格 + <c>CsConst.PlayerRadius</c> 角色半径，贴墙时常见）时若不 snap，就**永远**求不出路径、
        /// 整体退化成直线走。角色半径 &lt; 1 格边长 ⇒ 2 格足以覆盖"被挤进相邻格"。</para>
        ///
        /// <para>出处：**本项目新增**（导航实现参数）；snap 的**做法**出处 =
        /// <c>Assets/Editor/MapGen/MapConnectivityProbe.cs</c> 的 <c>SnapToWalkable</c>（逐环扩张搜最近可走格，
        /// 本类只把它的半径 24 收到 2）。</para>
        /// </summary>
        public const int PathSnapRadiusCells = 2;

        /// <summary>
        /// 高度一致性层（<see cref="BotNavigator"/> 的 <c>BuildHeightReach</c>）单次扩张的**规模上限**（格）。
        ///
        /// <para>用途：不是玩法判据，而是"这次扩张是不是已经把整张图刷完了"的护栏 ——
        /// 一旦超过就**整层关掉**并打 Warn（宁可回到既有行为，也绝不许拿"半个可达集"把合法路径判死）。</para>
        ///
        /// <para>取值出处：引擎自己的节点预算 <c>CloverEngine.AStar.DefaultMaxNodes</c> = <b>20000</b>
        /// （<c>Runtime/Core/AStar.cs:52</c> 一带，<see cref="BotNavigator"/> 求路径用的就是它）——
        /// 同量级即可：切片BJ 离线实测那张 127×145 位图**全部可走格约 5.3k**（46 个连通分量，
        /// 主分量 4393 + 其余 919，见 <c>tools/probes/marker-connectivity.py</c>），离 20000 有 4 倍余量。
        /// 出处：**本项目新增**（护栏，不是玩法阈值；⛔ 与 <c>CsConst.StepUpHeight</c> 无关）。</para>
        /// </summary>
        public const int HeightReachMaxCells = 20000;

        /// <summary>
        /// "同一对端点上连续求不出路径几次" ⇒ 判为**终点格与起点格不连通**（位图连通性问题，不是偶发）。
        ///
        /// <para>必要性（切片BJ 实测根因）：引擎 <c>AStar.Find</c> 在**起终点都可走**但位图上不连通时返回 null
        /// （<c>astar.nopath</c>）。此时若照旧"朝目标直线走"，机器人每 <c>StuckCheckInterval</c>(0.5s) 判一次卡住、
        /// 位移恒为 0.00m，再触发换目标 → 换到的路线里又有同样落在孤岛里的路点 ⇒ **无限循环**
        /// （切片BI 实测：<c>[AStar] 无可达路径 from=(79,96) to=(80,93)</c> ×21、换目标 ×28、373s 内除真人外
        /// 无任何 actor 位移 &gt; 5.8m）。判"不连通"要连续观测，单次失败可能只是"被挤进阻挡格 / 重求时机"。</para>
        ///
        /// <para>出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI；与 <see cref="StuckReplanStreak"/> 同一形状，
        /// ⛔ 不复用它的值 —— "卡住次数"与"求路径失败次数"是两件事，混用会让调其中一个时另一个跟着变）。</para>
        /// </summary>
        public const int PathFailStreakToUnreachable = 2;

        /// <summary>包点/买枪区半径 —— 复用地图契约里的值，保证与模拟的判定口径一致。
        /// 出处：<c>Module/Map/ICsMap.cs:83</c> 的 <c>CsMarkers.BombsiteRadius = 7f</c>
        /// （**逐值搬自该处**，不是另定一个数）。</summary>
        public const float SiteRadius = CsMarkers.BombsiteRadius;

        /// <summary>避障前探距离（米）。出处：**本项目新增**（避障实现参数，A 无 bot AI）。</summary>
        public const float ProbeDistance = 1.4f;

        /// <summary>
        /// 避障前探的**近端**采样距离（米）。必须同时确认这一段也可走，不能只看 <see cref="ProbeDistance"/> 处的那个格子：
        /// 面前一堵 1m 厚的墙时，"1.4m 处的格子"落在**墙的另一侧**（那格是可走的），只看它就会认定"前方畅通"，
        /// 机器人于是顶着墙推 —— 每帧位移 0，被判卡住。这是"卡住日志里换向却原地不动"的头号成因。
        ///
        /// <para>出处：**本项目新增**（避障实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float ProbeClearance = 0.7f;

        /// <summary>避障偏角的保持时长（秒）：带惯性，避免左右抖动。
        /// 出处：**本项目新增**（避障实现参数，A 无 bot AI）。</summary>
        public const float ProbeHoldSeconds = 0.5f;

        /// <summary>
        /// **换向迟滞（片BU-R3）**：保持中的偏角被判"不可走"时，必须**连续**持续这么久才允许丢开它（秒）。
        /// 单帧（或少数几帧）的"不可走"**不算数** —— <see cref="WalkableAhead"/> 的判定落在
        /// <c>position + dir × ProbeClearance/ProbeDistance</c> 两个采样点上，机器人来回蹭 0.1m 就会让这两个
        /// 采样点跨过格子边界、结论翻转；按单帧判定换向 = 方向在 ~10 次/秒 的速率上翻。
        ///
        /// <para><b>取值理由（离线复算，不是手感）</b>：噪声周期量自产品自己的 <c>_diagFlips</c> 计数 ——
        /// <c>[BOTFLIP]</c> 的闸是 0.5s/条，而每条之间累计翻转数 +3~+5（实测 Darrell/Scuzzy）
        /// ⇒ 单次翻转间隔 ≈ 0.5/5 ≈ <b>0.1s</b>。取它 ⇒ 恰好覆盖**一个完整噪声周期**，
        /// 单帧抖动推不翻；而 0.1s × 实测行走速度 4.7m/s ≈ 0.47m &lt; 一格(1m)，真墙照样在半个格内被认出来。
        /// 参数扫描（<c>tools/probes/bu-r3-avoid-replay.py</c> 第 4 节）显示 0~0.4s 全落在同一平台
        /// （换向率 0.1 次/秒 不变）⇒ 0.1s 在平台**内部**，不是拐点上的刀锋值。</para>
        ///
        /// <para>出处：**本项目新增**（避障实现参数，A 无 bot AI；同族参数 = <see cref="ProbeHoldSeconds"/>）。</para>
        /// </summary>
        public const float AvoidBadDirSeconds = 0.1f;

        /// <summary>卡住检测间隔（秒）。出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float StuckCheckInterval = 0.5f;

        /// <summary>一个检测周期内位移小于它就判定"卡住"（米）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float StuckMoveEpsilon = 0.15f;

        /// <summary>同一条"路点不可走"日志的最小间隔（秒）。
        /// 出处：**本项目新增**（日志降频参数，A 无 bot AI）。</summary>
        public const float StuckWarnCooldown = 3f;

        /// <summary>避障试探的偏角序列（度，先小后大）。
        /// 出处：**本项目新增**（避障实现参数，A 无 bot AI）。</summary>
        public static readonly float[] AvoidAngles =
        {
            25f, -25f, 50f, -50f, 75f, -75f, 100f, -100f, 125f, -125f, 160f, -160f,
        };

        // ==================== 导航自恢复（卡住 → 逃逸 / 换目标）====================
        /// <summary>逃逸方向的保持时长（秒）。必须明显长于 <see cref="ProbeHoldSeconds"/>：
        /// 偏角只保持一小会儿、每帧重掷 = 原地反复换向；保持足够久，机器人才会真的沿新方向位移出去。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float EscapeHoldSeconds = 1.2f;

        /// <summary>逃逸候选方向的"跑道"探测步长（米）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float EscapeRunwayStep = 0.7f;

        /// <summary>跑道探测最远距离（米）：沿该方向连续可走的长度越长，越优先选它。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float EscapeRunwayMax = 4.2f;

        /// <summary>
        /// 物理跑道判定比例：**与模拟自身的"撞墙"判据同源** ——
        /// <c>CsMatchConst.WallBlockVelocityRatio</c>（<c>Module/Match/CsMatch.cs:128</c> = 0.5）：
        /// "实际位移 &lt; 期望位移 × 0.5"就认为这一档没走成。
        /// 用于 <c>BotNavigator.PhysRunway</c>（可走位图说"不可走"时用物理复核）。
        /// 出处：<c>Module/Match/CsMatch.cs:123-128</c>。
        /// </summary>
        public const float PhysRunwayRatio = 0.5f;

        /// <summary>逃逸候选方向的扫描步长（度）：从期望方向向两侧各扫 180°。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float EscapeSweepStepDegrees = 30f;

        /// <summary>逃逸扫描的步数（度/步 × 步数 = 覆盖半圈）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const int EscapeSweepSteps = 6;

        /// <summary>连续判定"卡住"达到这次数就**重新选目标**（而不是继续跳过路点/原地换向）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const int StuckReplanStreak = 2;

        /// <summary>
        /// 两次"因卡住而重新选目标"之间的最小间隔（秒）—— 防**目标反复横跳**。
        ///
        /// <para>为什么必须有（片BU 实测根因）：<c>StuckCheckInterval</c>=0.5s × <c>StuckReplanStreak</c>=2
        /// ⇒ 最早 1.0s 就能攒够一次升级；而每次升级都换一个**全新的远目标**，于是
        /// <c>.ai-tmp/test/br-hold-plant-log.tsv</c> 里出现 <c>stuck-escalate=70</c>/回合、目标在
        /// <c>Route_Patrol</c> ↔ <c>Route_T_To_{A,B}</c> ↔ <c>Route_T_Mid</c> 之间每 ~2.5s 换一次
        /// （换目标间隔 = 守点时长 2.5~4.8s），全回合净位移 net/total = 0.001~0.007 ⇒ 原地打转。</para>
        ///
        /// <para>取值理由：必须**大于**"走到一个候选目标所需的时间量级"，否则新目标还没走出结果就被换掉。
        /// 取 6s ≈ 2× 三档里最长的守点时长（Easy 4.8s，见 <c>ObjectiveHoldSeconds</c>）。</para>
        ///
        /// <para>出处：**本项目新增**（防抖参数，A 无 bot AI；同族参数 = <see cref="StuckWarnMinInterval"/>）。</para>
        /// </summary>
        public const float StuckReplanCooldownSeconds = 6f;

        /// <summary>
        /// "这个方向走不动"的记忆时长（秒）：卡住时把当时提交的方向记下来，这段时间内不再往它推。
        /// 必要性：<see cref="ICsMap.WalkableAt"/> 说"前方可走"、物理却一步不动的情况真实存在
        /// （贴墙挤压 / 被几何卡住），没有这份记忆，"换向"就会永远换回同一个方向。
        ///
        /// <para>出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float BlockedDirMemorySeconds = 2.5f;

        /// <summary>判定"同一方向"的夹角容差（度）：与失败方向夹角 ≤ 它就算同一个方向。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float BlockedDirDegrees = 35f;

        /// <summary>同一条"卡住"原因：首个日志 + 之后每这么多次各记一条（防刷屏）。
        /// 出处：**本项目新增**（日志降频参数；工程内同款 = <see cref="LogRateEvery"/>）。</summary>
        public const int StuckWarnEvery = 50;

        /// <summary>
        /// 同一条"卡住"原因的**两次日志之间的最小间隔**（秒）。
        ///
        /// <para>主 agent 实测：8 个机器人 × 每 0.5s 一次判定，即使按次数降频（每 10 次一条）
        /// 也仍然每秒刷出 1~2 条"卡住"（日志里能看到"第 70 次"）；现场日志量级要求必须压下来。
        /// 所以次数阈值之外再加一道时间闸：≤ 每 <see cref="StuckWarnMinInterval"/> 秒一条（每个 bot / 每种原因各自计时）。</para>
        ///
        /// <para>出处：**本项目新增**（日志降频参数，A 无 bot AI）。</para>
        /// </summary>
        public const float StuckWarnMinInterval = 15f;

        /// <summary>巡逻点/换路目标必须离当前位置至少这么远（米），否则等于没换目标。
        /// 出处：**本项目新增**（导航实现参数，A 无 bot AI）。</summary>
        public const float MinPatrolDistance = 6f;

        /// <summary>守点（到达目标后原地警戒）的最短时长（秒）。
        /// 出处：**本项目新增**（导航实现参数，A 无 bot AI）。</summary>
        public const float CampHoldSeconds = 2.5f;

        /// <summary>守点时长 = <c>profile.RepathInterval</c> × 它（再与 <see cref="CampHoldSeconds"/> 取下限）。
        /// ⇒ Easy 4.8s / Normal 3.0s / Hard 2.5s：三档"多久换一次目标"肉眼可辨。
        /// 出处：倍数**本项目新增**；被乘的 <c>RepathInterval</c> 真源 =
        /// <c>Module/Match/CsTypes.cs:148</c> 的 <c>CsBotProfile.For</c>（Easy 1.6 / Normal 1.0 / Hard 0.5）。</summary>
        public const float ObjectiveHoldScale = 3f;

        /// <summary>按 <c>profile.RepathInterval</c> 检查"朝目标是否有进展"时，距离缩短小于它就算没进展（米）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const float RepathProgressEpsilon = 0.5f;

        /// <summary>连续几轮"没进展"才真的重排路线（防止把走顺的路线反复重排成来回蹭）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const int RepathStallStreak = 2;

        /// <summary>"重新选目标"里"换一条路线"与"去巡逻"的交替粒度（每这么多次换目标交替一次优先项）。
        /// 出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</summary>
        public const int ReplanAlternatePeriod = 2;

        /// <summary>
        /// "目标 = C4 本身（守包）"的伪路线名：**不是地图标记**，只用于日志。
        /// 用它时必须先 <see cref="BotNavigator.ClearRoute"/>（无路线）—— 否则重寻路会拿它去查标记、打 Error。
        /// 出处：**本项目新增**（本工程日志用伪路线名，不是 <c>de_dust2</c> 的标记定义）。</summary>
        public const string BombGuardRoute = "(守包-C4)";

        // ==================== 交战 ====================
        /// <summary>近身换手枪的距离（米）：任务书 §4.2「距离 &lt; 3m → 倾向换手枪」。
        /// 出处：**本项目新增**（阈值来自本片/前片任务书 §4.2；⚠️ 该任务书**不在工程内**、
        /// 规格 <c>策划/策划案/CS1.6单机参考规格.md</c> §2.4 未给该阈值 ⇒ 只能标"本项目新增"）。</summary>
        public const float MeleeSwitchRange = 3f;

        /// <summary>换回主武器的距离（米）：与上面组成滞回，避免来回切枪。
        /// 出处：**本项目新增**（滞回上界，与 <see cref="MeleeSwitchRange"/> 配套）。</summary>
        public const float MeleeBackRange = 6f;

        /// <summary>两次换枪之间的最小间隔（秒）。出处：**本项目新增**（交战实现参数，A 无 bot AI）。</summary>
        public const float WeaponSwitchCooldown = 1.5f;

        /// <summary>距离 &gt; 偏好距离 × 它时推进；否则站定（远距离蹲下）。
        /// 出处：**本项目新增**（倍数；被乘的"偏好距离"真源 = <c>CsBotProfile.For</c> 三档表，
        /// <c>Module/Match/CsTypes.cs:148</c>）。</summary>
        public const float AdvanceRangeFactor = 1.5f;

        /// <summary>
        /// 开火角度门限 = AimErrorDegrees × 它 + AimGateExtraDegrees。
        ///
        /// <para><b>为什么取这么宽</b>：门限是"必须先把视角转到位才扣扳机"的闸门。原值（scale 2 / extra 2 /
        /// max 16 ⇒ Normal 只有 8°）要求"抬枪完成 → 开火"两段式，实测在真实对局里机器人**几乎永远开不出枪**
        /// （22 次交战 0 发：目标一直在动 ⇒ 瞄点每帧变 ⇒ 朝向差总是差几度 ⇒ 门限永远过不去，扳机永远不扣）。
        /// CS 的真实玩家是**边转边打**的；放宽后 Hard 仍最严（≈12°）、Easy 最松（≈26°），
        /// 三档的"准/不准"差异由 <c>AimErrorDegrees</c> 继续承担。</para>
        ///
        /// <para>出处：**本项目新增**（门限系数，A 无 bot AI；被乘的 <c>AimErrorDegrees</c> 真源 =
        /// 规格 §2.4 三档瞄准误差 ±6° / ±3° / ±1.2°，落在 <c>CsBotProfile.For</c>）。</para>
        /// </summary>
        public const float AimGateErrorScale = 3f;

        /// <summary>开火角度门限的固定附加值（度）。
        /// 出处：**本项目新增**（门限项，与 <see cref="AimGateErrorScale"/> 同组）。</summary>
        public const float AimGateExtraDegrees = 8f;

        /// <summary>开火角度门限下限（度）。
        /// 出处：**本项目新增**（门限钳制下限，与 <see cref="AimGateErrorScale"/> 同组）。</summary>
        public const float AimGateMinDegrees = 12f;

        /// <summary>开火角度门限上限（度）。
        /// 出处：**本项目新增**（门限钳制上限，与 <see cref="AimGateErrorScale"/> 同组）。</summary>
        public const float AimGateMaxDegrees = 30f;

        /// <summary>瞄头时的额外误差比例（倍 AimErrorDegrees）。出处：行为口径见规格 §2.4 的 Hard 行
        /// 「精准、会点射/爆头倾向」（<c>策划/策划案/CS1.6单机参考规格.md:118</c>）；
        /// **比例值本身本项目新增**（A 无 bot AI，没有"瞄头误差倍数"这个量）。</summary>
        public const float HeadAimExtraErrorScale = 0.4f;

        /// <summary>瞄头额外误差的重采样间隔（秒），与模拟内部误差同节奏。
        /// 出处：**本项目新增**（重采样节奏；与模拟内部瞄准误差刷新同周期，见 <c>CsMatch</c> 的 bot 瞄准更新）。</summary>
        public const float AimWobbleRefreshSeconds = 0.35f;

        /// <summary>"这一轮瞄不瞄头"的重掷间隔（秒）：不逐帧掷，避免抖动。
        /// 出处：**本项目新增**（重掷节奏，A 无 bot AI）。</summary>
        public const float HeadshotRollSeconds = 1.2f;

        /// <summary>瞄胸时的高度比例（占角色身高的比例）。
        /// 出处：**本项目新增**。⚠️ 原版**有**对应量（玩家模型三组 hitbox 的高度偏移，写在 mdl 的 hitbox 表里，
        /// 由 <c>mp.dll</c> 消费），但承载它的原版 <c>models/player/*.mdl</c> 本机不在盘
        /// （<c>原版资源/</c> 实测只有 <c>cs16src/</c> 73 份 cstrike 资源 · <c>hlsdk/</c> · <c>备份/</c> ·
        /// <c>_moved-out-from-assets/</c>，无 <c>models/</c>；<c>mp.dll</c> 本身已在盘但尚未反汇编，
        /// 见 <c>原版资源/清单.md</c>「切片AW」§1）⇒ 已登记 <c>策划/差异登记.tsv</c>（#58 的「瞄胸高度比例」条），
        /// 不许当"有出处"。</summary>
        public const float ChestHeightRatio = 0.78f;

        /// <summary>预瞄提前量的最大秒数（按难度插值：Normal 0 → Hard 满值）。
        /// 出处：行为口径见规格 §2.4 的 Hard 行「会预瞄」（<c>策划/策划案/CS1.6单机参考规格.md:118</c>）；
        /// **上限秒数本项目新增**（A 无 bot AI）。</summary>
        public const float PredictSecondsMax = 0.12f;

        /// <summary>预瞄力度归一化的速度下界（度/秒）—— 取 <see cref="CsBotProfile"/> 三档里 Normal 的 AimSpeedDegrees。
        /// 出处：<c>Module/Match/CsTypes.cs:185</c>（<c>CsBotProfile.For(Normal)</c> 的 <c>AimSpeedDegrees = 300f</c>）。</summary>
        public const float PredictSkillSpeedLo = 300f;

        /// <summary>预瞄力度归一化的速度上界（度/秒）—— 取三档里 Hard 的 AimSpeedDegrees。
        /// 出处：<c>Module/Match/CsTypes.cs:171</c>（<c>CsBotProfile.For(Hard)</c> 的 <c>AimSpeedDegrees = 520f</c>）。</summary>
        public const float PredictSkillSpeedHi = 520f;

        /// <summary>未给瞄准点时的兜底注视距离（米）。
        /// 出处：**本项目新增**（瞄准实现参数，A 无 bot AI）。</summary>
        public const float AimFallbackDistance = 10f;

        /// <summary>"开火被抑制"诊断日志的每-bot 最小间隔（秒）—— 首次必打（这是"哪一条恒假"的证据）。
        /// 出处：**本项目新增**（诊断日志降频参数）。</summary>
        public const float FireDiagInterval = 2f;

        /// <summary>"视线只被角色受体挡住"（按 CS 语义放行）日志的每-bot 最小间隔（秒）。
        /// 出处：**本项目新增**（诊断日志降频参数）。</summary>
        public const float SightActorBlockLogInterval = 10f;

        // ==================== 炸弹 ====================
        /// <summary>CT 停下拆包的距离（米）。必须 ≤ 模拟内部允许的拆包半径，否则按 E 会被判"离炸弹太远"。
        /// 出处：上界 = <c>Module/Match/CsMatch.cs</c> 的 <c>CsMatch.DefuseRadius = 1.5f</c>
        /// （对照表里它对应原版 <c>func_bomb_target</c> 的触发范围）；**1.2 这个具体值本项目新增**
        /// （严格小于上界，留出判定余量）。</summary>
        public const float DefuseStopRadius = 1.2f;

        /// <summary>停下捡 C4 的距离（米）。
        /// 出处：上界 = <c>Module/Match/CsMatch.cs</c> 的 <c>CsMatch.PickupRadius = 1.2f</c>；
        /// **1.0 这个具体值本项目新增**。</summary>
        public const float PickupStopRadius = 1.0f;

        /// <summary>炸弹剩余时间 ≤ 拆包所需 + 它时，优先拆包（Hard 的"拆包果断"）。
        /// 出处：行为口径见规格 §2.4 的 Hard 行「拆包果断」（<c>策划/策划案/CS1.6单机参考规格.md:118</c>）；
        /// **余量秒数本项目新增**。</summary>
        public const float DefuseUrgencyMargin = 2f;

        /// <summary>
        /// 已下包后，"眼前有多近的敌人才值得先打、而不是先去拆包"（米）。
        /// 超过它就一律先冲包点 —— 官方 CT 的行为是"下包即刻回防"，站在远处对枪等于把回合送掉。
        ///
        /// <para>出处：行为口径见规格 §2.4 的 Hard 行「拆包果断」；**距离值本项目新增**（A 无 bot AI）。</para>
        /// </summary>
        public const float DefuseOverFightRange = 15f;

        /// <summary>守点时每次挪窝后的停留时长（秒）。
        /// 出处：**本项目新增**（守点实现参数，A 无 bot AI）。</summary>
        public const float CampRepositionSeconds = 2.5f;

        /// <summary>守点时可以在守点周围多大半径内挪窝（米）——"会换位"的范围。
        /// 出处：行为口径见规格 §2.4 的 Hard 行「会换位」；**半径值本项目新增**。</summary>
        public const float CampRepositionRadius = 6f;

        // ==================== 包点守位表（切片BG） ====================
        /// <summary>
        /// 包点守卫**换位间隔**（秒）：CT 在自己的守位表里每这么多秒挪到下一个守位。
        ///
        /// <para>为什么不是"到点就走"（旧行为 = 守 <see cref="ObjectiveHoldSeconds"/> 就换目标 ⇒ 观感"原地踱步"）：
        /// 守位表把"守点"拆成**站住 + 定时换位** —— 人始终留在包点，只是在包点内的 2~3 个守位之间轮换。</para>
        ///
        /// <para>出处：**本项目新增**（A = CS 1.6 本体不含 bot AI，见本文件 :18-19，本量在 A 里没有对应载体）。
        /// 取值理由：① 明显大于旧 <see cref="CampRepositionSeconds"/>=2.5s 的"抖动"量级，移动是"换位"而不是来回蹭；
        /// ② 小于单个 C4 周期 <c>CsConst.BombTimer</c>=35s（出处 <c>server.cfg:43</c> 的 <c>mp_c4timer 35</c>）的 1/4，
        /// 使一次守包窗口内每个守位都至少被转移到一次。</para>
        /// </summary>
        public const float HoldSwapSeconds = 8f;

        /// <summary>
        /// 同一包点内两个守位之间的**最小间距**（米）：间距小于它的标记点会被折叠成同一个守位。
        ///
        /// <para>出处：**本项目新增**（守位表实现参数，A 无 bot AI）。取值理由 = 远大于两人贴身距离
        /// （<c>CsConst.PlayerRadius</c> = 0.36m 直径 0.72m）：否则"多点分布"退化成一格上叠两个人。</para>
        /// </summary>
        public const float HoldSpotMinSeparation = 3f;

        /// <summary>
        /// 携带 C4 的 T 走到离**最近的包点标记**多远就停下开下（米）。
        ///
        /// <para>⛔ 为什么必须**远小于**包点判定半径 <see cref="SiteRadius"/>（= 7m，
        /// 真源 <c>Module/Map/ICsMap.cs:83</c> 的 <c>CsMarkers.BombsiteRadius</c>）：
        /// 停步半径 == 判定半径（旧行为用 <see cref="SiteRadius"/> 同时当"走到哪算到"与"能不能下"）时，
        /// 机器人会**正好停在判定球面上**，`CsBomb.CanPlant` 的 `IsInBombsite` 随浮点误差反复真假 ⇒
        /// 表现为"到了包点又走了"（下包永远起不来）。这里取 1.5m，保证站定时
        /// "到最近包点标记的距离" 恒 &lt; 判定半径。</para>
        ///
        /// <para>出处：**本项目新增**（下包实现参数，A 无 bot AI；被保护的量 <c>BombsiteRadius=7</c> 有出处）。</para>
        /// </summary>
        public const float PlantStopRadius = 1.5f;

        /// <summary>
        /// 【片BU-R5 起**已废弃、不再被引用**，仅保留常量与出处以便回溯】
        /// 旧口径：只有 <c>id % 它 == 0</c> 的 T 会去捡掉落的 C4（想让 4 人一队只出一个人，避免全队扑向同一个点）。
        ///
        /// <para>为什么废弃：实测（片BU-R5 L3）它挑的人**不是离 C4 最近的那个**，而且"最近的 T 编号不整除"
        /// 时整条捡包分支一次都不进 ⇒ C4 躺到回合结束（`拾起了` = 0 条、A5 恒 0）。
        /// 现行口径 = **离 C4 最近的那一个 T 去捡**（<c>CsBotBrain.IsElectedBombHunter</c>，唯一且确定），
        /// 仍然只有一个人去，⛔ 不改掉落/拾取规则本身。</para>
        /// 出处：**本项目新增**（分工实现参数，A 无 bot AI）。
        /// 替代口径的落点：<c>CsBotBrain.IsElectedBombHunter</c>。
        /// </summary>
        public const long BombHunterModulo = 4L;

        // ==================== 买枪 ====================
        /// <summary>买枪失败后的重试间隔（秒）。出处：**本项目新增**（买枪实现参数，A 无 bot AI）。</summary>
        public const float BuyRetryInterval = 0.5f;

        /// <summary>一个冻结期最多尝试几轮买枪（防刷屏、防呆循环）。
        /// 出处：**本项目新增**（买枪实现参数，A 无 bot AI）。</summary>
        public const int MaxBuyAttempts = 3;

        /// <summary>进入冻结期多久还没站进买枪区就告警（秒）。
        /// 出处：**本项目新增**（诊断告警参数）。</summary>
        public const float BuyZoneWarnDelay = 1.5f;

        /// <summary>Hard 档买 AWP 的概率（任务书 §4.2：30%）。
        /// 出处：**本项目新增**（概率值来自本片/前片任务书 §4.2；⚠️ 该任务书**不在工程内**，
        /// 规格 §2.4 的 Hard 行只写「会买最好枪」、未给概率 ⇒ 只能标"本项目新增"）。</summary>
        public const float AwpChanceTier2 = 0.30f;

        // ==================== 统计 / 日志 ====================
        /// <summary>命中率统计日志间隔（秒）：验收表 B1~B3 要的就是这条。
        /// 出处：**本项目新增**（验收表 B1~B3 要求的统计口径 + 日志降频参数）。</summary>
        public const float StatsLogInterval = 30f;

        /// <summary>伤害事件归属给"某个 bot 打中"的时间窗（秒）。
        /// 出处：**本项目新增**（伤害归属实现参数，A 无 bot AI；归属逻辑见 <c>CsBotBrain</c> 的命中归属）。</summary>
        public const float HitAttributionWindow = 0.4f;

        /// <summary>状态切换日志的每 bot 最小间隔（秒）。
        /// 出处：**本项目新增**（日志降频参数）。</summary>
        public const float StateLogMinInterval = 2f;

        /// <summary>每 tick 单 actor 的射击次数估算上限（防假时钟/暂停把估值放大）。
        /// 出处：**本项目新增**（实现防护参数，A 无 bot AI）。</summary>
        public const int MaxShotEstimatePerTick = 8;

        /// <summary>通用日志降频：首次 + 每 N 次。
        /// 出处：**本项目新增**（日志降频口径；工程内同款 = <c>Module/Audio/CsAudioTuning.LogRateEvery</c>）。</summary>
        public const int LogRateEvery = 50;
    }
}
