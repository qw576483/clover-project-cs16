using Cs16.Core;
using Cs16.Module.Map;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人模块私有常量（**本项目新增**，不是引擎 API）。
    ///
    /// <para><b>为什么单独一个文件</b>：<see cref="CsConst"/> 是**契约文件**（只有指定范围的文件可改）。
    /// 这里集中放"机器人手感 / 阈值"类数值，目的与 <see cref="CsConst"/> 一致 ——
    /// **业务代码里不出现裸数字，改一处即可调平衡**。</para>
    ///
    /// <para><b>出处口径</b>：本文件 66 条的出处分三类，逐条写在各自注释里 ——
    /// ① **A 里有对应量**（能查到出处的，含只能从行为特征反推的那些）；
    /// ② **本工程另有定义处的那个文件**（<c>CsBotProfile</c> 三档表在
    /// <c>Module/Match/CsTypes.cs:148</c>；<c>CsMarkers.BombsiteRadius</c> 在 <c>Module/Map/ICsMap.cs:83</c>；
    /// <c>CsMatch.DefuseRadius/PickupRadius</c> 在 <c>Module/Match/CsMatch.cs</c>）；
    /// ③ **本项目新增**（A = CS 1.6 本体**不含机器人 AI** —— 官方 bot 属 Condition Zero / PodBot，
    /// 不在本工程的载体范围内，所以"bot 手感阈值"在 A 里**没有对应量**，只能自定并如实标注）。
    /// 其中**确有一个原版对应量、但载体拿不到**的三组（瞄胸高度比例 / 脚步噪声阈值 / C4 无关）已单独登记
    /// <c>策划/差异登记.tsv</c>，不许当成"有出处"。</para>
    ///
    /// <para><b>与 <see cref="CsConst"/> / <see cref="CsBotProfile"/> 的分工</b>（避免双重生效）：
    /// 三档难度参数（反应时间 / 瞄准误差 / 转视角速度 / 连发节奏 / 视野 / 偏好距离 / 买枪档位 / 爆头率 /
    /// 重决策间隔）**一律只从 <see cref="CsBotProfile.For"/> 取**，本文件里不许再写第二套；
    /// 这里只放"模拟没有、AI 才需要"的阈值。</para>
    ///
    /// <para><b>可见性 = public</b>：<c>Assets/Scripts/</c> 有独立程序集
    /// <c>Cs16.asmdef</c>，而 Editor 生成器在 <c>Assembly-CSharp-Editor</c> ⇒ <c>internal</c> 跨不过去。
    /// 生成侧（<c>Editor/MapGen/Dust2Builder.cs</c> 的标记点吸附）**必须**用本类
    /// <c>PathSnapRadiusCells</c> 这**同一个半径**，否则"生成侧吸附半径"与"消费侧 snap 半径"会各用一套。</para>
    /// </summary>
    public static class CsBotConst
    {
        // ==================== 感知 ====================
        /// <summary>
        /// 目标记忆时长（秒）：丢视野后仍算"同一个目标"的上限。
        ///
        /// <para><b>它同时是"反应时间要不要重算"的唯一分界</b>：反应计时（
        /// <see cref="CsBotProfile.ReactionTime"/>）绑在**目标身份**上 —— 换了目标、或彻底忘掉这个目标
        /// （丢视野时间 &gt; 它）之后再看，才算"重新首次看见"。
        /// 阈值取更短的 0.5s 时：视野断续超过 0.5s 就把反应计时清零重来 ——
        /// "断续可见"的目标**永远过不了反应门槛**（也就永远不开枪）。</para>
        ///
        /// <para>出处：**本项目新增**（bot 感知记忆时长，A 无 bot AI ⇒ 无原版对应量；
        /// 规格 §2.4 只规定三档的"反应时间 / 瞄准误差 / 行为"，不含记忆时长）。</para>
        /// </summary>
        public const float TargetMemorySeconds = 2f;

        /// <summary>交战中的"贴脸"距离（米）：小于它时放弃下包/拆包先应战。
        /// 出处：**本项目新增**（bot 交战取舍阈值；A 无 bot AI，规格 §2.4 未给该阈值）。</summary>
        public const float EnemyTooCloseRange = 5f;

        /// <summary>判定"跑动出声"的水平速度阈值（米/秒）。低于它 = 慢走/站定，不出声。
        /// 出处：**本项目新增**（bot 听觉阈值）。原版对应的"脚步噪声判定"在服务端 <c>mp.dll</c>
        /// （按 <c>mp_footsteps</c> 开关 + 速度档判定），该载体**已在盘、但尚未反汇编**
        /// （<c>原版资源/cs16src/cstrike/dlls/mp.dll</c>，1,640,960 B / SHA256 <c>D7294D9B…1F2974</c>，
        /// 已登记 <c>策划/差异登记.tsv</c>（#58 的「脚步噪声阈值」条）。</summary>
        public const float RunNoiseSpeed = 2.5f;

        /// <summary>听觉记忆时长（秒）。出处：**本项目新增**（bot 听觉模型参数，A 无 bot AI）。</summary>
        public const float NoiseMemorySeconds = 2f;

        /// <summary>
        /// 听觉的**楼层容差**（米）：声源与听者高差超过它就不算"听见"。
        /// 出处：原版 <c>cs_bot_listen.cpp:53</c> 的 <c>oneStoreyHeight = 120.0f</c>（GoldSrc 单位，
        /// 换算到本工程 1 单位 = 0.025 m ⇒ 3.0 m）。没有这条时，楼上/楼下跑动的敌人
        /// 会把正在守点的 bot 引到楼下对着天花板看。
        /// </summary>
        public const float NoiseMaxFloorDelta = 3f;

        /// <summary>噪声环形缓冲容量（条）。出处：**本项目新增**（bot 听觉模型的实现容量，A 无 bot AI）。</summary>
        public const int NoiseCapacity = 24;

        /// <summary>同一个 actor 的跑动噪声最小间隔（秒）。出处：**本项目新增**（防刷同一噪声，A 无 bot AI）。</summary>
        public const float RunNoiseInterval = 0.4f;

        /// <summary>同一个 actor 的枪声噪声最小间隔（秒）。出处：**本项目新增**（同上）。</summary>
        public const float GunshotNoiseInterval = 0.1f;

        // ==================== 感知分档（视野 / 记忆 / 听觉）====================
        // 这三组量在 CsBotProfile 里没有对应字段（它只放反应 / 瞄准误差 / 转视角 / 连发 / 视野距离 /
        // 偏好距离 / 买枪档 / 爆头率 / 重决策），所以三档表落在这里，不存在第二套。
        // 出处：**本项目新增**（A = CS 1.6 本体不含机器人 AI，官方 bot 属 Condition Zero / PodBot）。

        /// <summary>视野角（度，全角）—— Easy 90 / Normal 100 / Hard 110。
        /// 全局上限 = <c>CsConst.BotFovDegrees</c>(110)：Hard 取满值，其余两档依次收窄。</summary>
        public const float FovDegreesEasy = 90f;

        /// <summary>视野角（度）—— Normal。见 <see cref="FovDegreesEasy"/>。</summary>
        public const float FovDegreesNormal = 100f;

        /// <summary>视野角（度）—— Hard。见 <see cref="FovDegreesEasy"/>。</summary>
        public const float FovDegreesHard = 110f;

        /// <summary>目标记忆时长（秒）—— Easy：丢视野超过它就删掉这个敌人的记忆。</summary>
        public const float MemorySecondsEasy = 0.8f;

        /// <summary>目标记忆时长（秒）—— Normal。</summary>
        public const float MemorySecondsNormal = 1.2f;

        /// <summary>目标记忆时长（秒）—— Hard。</summary>
        public const float MemorySecondsHard = 1.8f;

        /// <summary>枪声听觉半径（米）—— Easy：枪声只在这么近才听得见。</summary>
        public const float GunshotHearRadiusEasy = 10f;

        /// <summary>枪声听觉半径（米）—— Normal。</summary>
        public const float GunshotHearRadiusNormal = 14f;

        /// <summary>枪声听觉半径（米）—— Hard。取 <c>CsConst.BotHearRadius</c>(18) 的满值。</summary>
        public const float GunshotHearRadiusHard = 18f;

        /// <summary>脚步听觉半径（米）—— Easy：跑动声只在这么近才听得见。</summary>
        public const float StepHearRadiusEasy = 4f;

        /// <summary>脚步听觉半径（米）—— Normal。</summary>
        public const float StepHearRadiusNormal = 6f;

        /// <summary>脚步听觉半径（米）—— Hard。</summary>
        public const float StepHearRadiusHard = 8f;

        /// <summary>
        /// 听觉方向的不确定半角（度）：感知给出的是"声源来自这个方位 ± 它"的区间，
        /// 不是精确坐标。取值大于 0 的作用是让守点 / 搜索用区间而不是钉死一个点。
        /// 出处：**本项目新增**（bot 听觉模型参数，A 无 bot AI）。
        /// </summary>
        public const float HearBearingConeDegrees = 20f;

        /// <summary>听觉命中日志的每 bot 最小间隔（秒）。出处：**本项目新增**（日志降频参数）。</summary>
        public const float HearLogInterval = 2f;

        /// <summary>记忆生效日志的每 bot 最小间隔（秒）。出处：**本项目新增**（日志降频参数）。</summary>
        public const float MemoryLogInterval = 3f;

        /// <summary>某一档的视野角（度）。未知档位按 Normal 处理并告警一次。</summary>
        public static float FovDegrees(CsBotDifficulty d)
        {
            switch (d)
            {
                case CsBotDifficulty.Easy: return FovDegreesEasy;
                case CsBotDifficulty.Normal: return FovDegreesNormal;
                case CsBotDifficulty.Hard: return FovDegreesHard;
                default:
                    WarnIllegalTier(d, "FovDegrees");
                    return FovDegreesNormal;
            }
        }

        /// <summary>某一档的目标记忆时长（秒）。未知档位按 Normal 处理并告警一次。</summary>
        public static float MemorySeconds(CsBotDifficulty d)
        {
            switch (d)
            {
                case CsBotDifficulty.Easy: return MemorySecondsEasy;
                case CsBotDifficulty.Normal: return MemorySecondsNormal;
                case CsBotDifficulty.Hard: return MemorySecondsHard;
                default:
                    WarnIllegalTier(d, "MemorySeconds");
                    return MemorySecondsNormal;
            }
        }

        /// <summary>某一档的枪声听觉半径（米）。未知档位按 Normal 处理并告警一次。</summary>
        public static float GunshotHearRadius(CsBotDifficulty d)
        {
            switch (d)
            {
                case CsBotDifficulty.Easy: return GunshotHearRadiusEasy;
                case CsBotDifficulty.Normal: return GunshotHearRadiusNormal;
                case CsBotDifficulty.Hard: return GunshotHearRadiusHard;
                default:
                    WarnIllegalTier(d, "GunshotHearRadius");
                    return GunshotHearRadiusNormal;
            }
        }

        /// <summary>某一档的脚步听觉半径（米）。未知档位按 Normal 处理并告警一次。</summary>
        public static float StepHearRadius(CsBotDifficulty d)
        {
            switch (d)
            {
                case CsBotDifficulty.Easy: return StepHearRadiusEasy;
                case CsBotDifficulty.Normal: return StepHearRadiusNormal;
                case CsBotDifficulty.Hard: return StepHearRadiusHard;
                default:
                    WarnIllegalTier(d, "StepHearRadius");
                    return StepHearRadiusNormal;
            }
        }

        /// <summary>非法难度值只告警一次（难度只来自 <c>CsActor.Difficulty</c>，越界说明枚举/回包被写坏）。</summary>
        private static bool _warnedIllegalTier;

        private static void WarnIllegalTier(CsBotDifficulty d, string what)
        {
            if (_warnedIllegalTier) return;
            _warnedIllegalTier = true;

            var logger = CloverEngine.Game.Logger;
            if (logger != null) logger.Warn(BotModule.Tag, $"{what}：收到非法难度值 {(int)d}，已按 Normal 处理");
        }

        // ==================== 导航 ====================
        /// <summary>路点到达半径（米）：进入它就推进到下一个路点。
        /// 出处：**本项目新增**（导航实现参数；原版无 bot 导航，A 无 bot AI）。</summary>
        public const float WaypointArriveRadius = 2.5f;

        /// <summary>默认目标点到达半径（米）。出处：**本项目新增**（同 <see cref="WaypointArriveRadius"/>）。</summary>
        public const float DefaultObjectiveRadius = 2f;

        /// <summary>
        /// 重求全局路径（引擎 <c>AStar.FindSmoothed</c>）的兜底间隔（秒）：到点就重求一次，兼作"被挤开后自我纠偏"。
        ///
        /// <para>为什么需要一个闸：<c>AStar</c> 一次求解最多展开 <c>DefaultMaxNodes</c>(20000) 个节点，不能每帧对
        /// 每个 bot 求一次。<see cref="BotNavigator"/> 只在"没有路径 / 目标格变了 / 路径走完 / 判到卡住"之外再按这个间隔兜底。</para>
        ///
        /// <para>出处：**本项目新增**（导航实现参数，A 无 bot AI；与同文件里 <see cref="StuckCheckInterval"/> 同一量级，
        /// 但**不复用**它 —— 卡住检测的周期与"路径新鲜度"是两件事，混用会让调其中一个时另一个跟着变）。</para>
        /// </summary>
        public const float PathReplanInterval = 1f;

        /// <summary>
        /// 沿既有路径这一秒是否算"在前进"的距离阈值（米）：到当前拐点的距离缩短 ≥ 它才算有进展，
        /// 有进展就**不重求**路径。
        /// <para>为什么要它：重求会用当前格重跑一次 A*，等价代价的两条分支（绕左边 / 绕右边）会在
        /// 起点的微小变化下互换 ⇒ 机器人按新路径掉头，表现为"同一路点上来回拉锯、整回合进不去包点"。</para>
        /// <para>出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float PathProgressEpsilon = 0.3f;

        /// <summary>
        /// 捡包者放弃的时长（秒）：连续这么久到掉落点的距离没有实质缩短 ⇒ 换下一个离 C4 最近的 T。
        /// <para>为什么要有它：被选中的捡包者可能站在走不到的一侧（隔离/高台/被自己人挤住），
        /// 其余 T 又在交战 ⇒ 整回合没人捡包、回合直接作废。兜底后至少能换人再试一次。</para>
        /// <para>出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float PickupGiveUpSeconds = 9f;

        /// <summary>捡包"有进展"的距离阈值（米）：到落点缩短 ≥ 它才把计时锚点刷新。</summary>
        public const float PickupProgressMeters = 1.5f;

        /// <summary>捡包时"落点走得到吗"（<c>CanReach</c>，内含一次 A*）的检查间隔（秒）。</summary>
        public const float PickupReachCheckInterval = 1f;

        /// <summary>
        /// 捡包者放弃后**换人**所需的最小距离收益（米）：下一个候选必须比当前捡包者至少近这么多才换。
        /// <para>为什么要有它：没有收益门槛时，"40m 外走不动"会换给 48m 外的队友 —— 两个人都在路上耗，
        /// 本回合再没人捡包；换人只在"确实有人更近"时才有意义。门槛取 5m ≈ 1 秒的正常步速。</para>
        /// <para>出处：**本项目新增**（捡包实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float PickupHandoffMinGain = 5f;

        /// <summary>
        /// "没有更近的候选人 ⇒ 本人继续去捡"最多重复几个窗口（窗口长 <see cref="PickupGiveUpSeconds"/>）。
        /// <para>上限的目的：落点真的走不到时，不许让一只 T 整回合追一个捡不到的包（它会因此丢掉
        /// 下包与交战）。到上限后照旧弃权，回交战/推进。</para>
        /// <para>出处：**本项目新增**（捡包实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const int PickupKeepWindows = 2;

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
        /// 出处：**本项目新增**（护栏，不是玩法阈值；与 <c>CsConst.StepUpHeight</c> 无关）。</para>
        /// </summary>
        public const int HeightReachMaxCells = 20000;

        /// <summary>
        /// 位图里"**孤立格区**"的规模上限（格）：连通分量格数 ≤ 它 ⇒ 局部层当它不可走。
        ///
        /// <para>必要性：可走位图是**单层 2D**（多层几何上下重叠），de_dust2 有 53 个连通分量、非主分量 941 格
        /// （17.7%）。小分量是"位图说可走、物理没有落脚面"的区域 —— 例如沿 <c>x=49</c> 的 11 格一条缝
        /// （<c>z=27..37</c>，向下 15m 探不到任何世界面）。机器人走进去就出不来：目标选择里
        /// <c>CanReach</c> 对路线点 / 巡逻点 / 出生点**全部**返回 false，此后整回合只能原地换向。</para>
        ///
        /// <para>取值理由：主分量 4369 格、次大分量 323 格（多层几何的合法区域，如 B 点南侧）；再往下是
        /// 191 / 103 / 82 格，然后掉到 ≤ 24 格。取 32 ⇒ 只挡住 ≤ 32 格那一档，**不挡** 82 格以上的（挡住会让
        /// 机器人走不进整片合法区域）。分量格数见离线分量表 <c>.ai-tmp/test/astar-markers/bitmap-connect.py</c>；
        /// 消费方 = <see cref="BotNavigator"/> 的 <c>IsTrapCell</c>。</para>
        ///
        /// <para>出处：**本项目新增**（导航护栏，不是玩法阈值；A 里没有 bot 导航）。</para>
        /// </summary>
        public const int TrapComponentCells = 32;

        /// <summary>
        /// "同一对端点上连续求不出路径几次" ⇒ 判为**终点格与起点格不连通**（位图连通性问题，不是偶发）。
        ///
        /// <para>必要性：引擎 <c>AStar.Find</c> 在**起终点都可走**但位图上不连通时返回 null
        /// （<c>astar.nopath</c>）。此时若照旧"朝目标直线走"，机器人每 <c>StuckCheckInterval</c>(0.5s)
        /// 判一次卡住、位移恒为 0.00m，再触发换目标 → 换到的路线里又有同样落在孤岛里的路点
        /// ⇒ **无限循环**（实测 373s 里 CT 进点 0、除真人外无任何 actor 位移 &gt; 5.8m）。
        /// 判"不连通"要连续观测，单次失败可能只是"被挤进阻挡格 / 重求时机"。</para>
        ///
        /// <para>出处：**本项目新增**（导航自恢复实现参数，A 无 bot AI；与 <see cref="StuckReplanStreak"/> 同一形状，
        /// 不复用它的值 —— "卡住次数"与"求路径失败次数"是两件事，混用会让调其中一个时另一个跟着变）。</para>
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
        /// 避障"选定一侧"之后的保持时长（秒）：这一侧没有更进一步的可走候选、也没有实质进展之前，
        /// 不许换到另一侧。
        ///
        /// <para>只靠 <see cref="ProbeHoldSeconds"/> 不够：偏角缓存到期、或机器人沿偏角走开一小段之后
        /// 期望方向又能走，就会立刻退回直线 ⇒ 直线把人送回同一个转角 ⇒ 输出方向以"缓存时长"的周期
        /// 在同一段墙前来回。保持时长必须长到"沿这一侧绕过这个转角"的量级：4.7m/s × 1.2s ≈ 5.6m。</para>
        ///
        /// <para>出处：**本项目新增**（避障实现参数，A 无 bot AI；同族参数 = <see cref="ProbeHoldSeconds"/>）。</para>
        /// </summary>
        public const float AvoidSideHoldSeconds = 1.2f;

        /// <summary>
        /// 侧向保持期间的"实质进展"判据（米）：到最终目标的水平距离比选定这一侧时缩短这么多，
        /// 这段障碍就算绕过去了 —— 允许立刻重新选向（不必等满 <see cref="AvoidSideHoldSeconds"/>）。
        ///
        /// <para>取 0.6m：小于一格，够灵敏；又大于逐帧位置抖动（格宽 1m），不会被噪声触发。</para>
        ///
        /// <para>出处：**本项目新增**（避障实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float AvoidSideProgressMeters = 0.6f;

        /// <summary>
        /// 期望方向变化超过这个角度就作废侧向保持（度）：换目标 / 改用 A* 拐点之后是另一段路，
        /// 不是"同一个转角的两侧"，继续沿旧侧走会绕远。
        ///
        /// <para>取 35°：与 <see cref="BlockedDirDegrees"/> 同一个"算同一个方向"的容差口径。</para>
        ///
        /// <para>出处：**本项目新增**（避障实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float AvoidSideDesiredDegrees = 35f;

        /// <summary>
        /// 单帧（或少数几帧）的"不可走"**不算数** —— <see cref="WalkableAhead"/> 的判定落在
        /// <c>position + dir × ProbeClearance/ProbeDistance</c> 两个采样点上，机器人来回蹭 0.1m 就会让这两个
        /// 采样点跨过格子边界、结论翻转；按单帧判定换向 = 方向在 ~10 次/秒 的速率上翻。
        ///
        /// <para><b>取值理由（离线复算，不是手感）</b>：噪声周期量自产品自己的 <c>_diagFlips</c> 计数 ——
        /// <c>[BOTFLIP]</c> 的闸是 0.5s/条，而每条之间累计翻转数 +3~+5（实测 Darrell/Scuzzy）
        /// ⇒ 单次翻转间隔 ≈ 0.5/5 ≈ <b>0.1s</b>。取它 ⇒ 恰好覆盖**一个完整噪声周期**，
        /// 单帧抖动推不翻；而 0.1s × 实测行走速度 4.7m/s ≈ 0.47m &lt; 一格(1m)，真墙照样在半个格内被认出来。
        /// 参数扫描（第 4 节）显示 0~0.4s 全落在同一平台
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
        /// <para>为什么必须有：<c>StuckCheckInterval</c>=0.5s × <c>StuckReplanStreak</c>=2
        /// ⇒ 最早 1.0s 就能攒够一次升级；而每次升级都换一个**全新的远目标**，没有间隔闸时会出现
        /// <c>stuck-escalate=70</c>/回合、目标在 <c>Route_Patrol</c> ↔ <c>Route_T_To_{A,B}</c> ↔
        /// <c>Route_T_Mid</c> 之间每 ~2.5s 换一次（换目标间隔 = 守点时长 2.5~4.8s），
        /// 全回合净位移 net/total = 0.001~0.007 ⇒ 原地打转。</para>
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
        /// <para>实测：8 个机器人 × 每 0.5s 一次判定，即使按次数降频（每 10 次一条）
        /// 也仍然每秒刷出 1~2 条"卡住"（日志里能看到"第 70 次"）；现场日志量级要求必须压下来。
        /// 所以次数阈值之外再加一道时间闸：≤ 每 <see cref="StuckWarnMinInterval"/> 秒一条（每个 bot / 每种原因各自计时）。</para>
        ///
        /// <para>出处：**本项目新增**（日志降频参数，A 无 bot AI）。</para>
        /// </summary>
        public const float StuckWarnMinInterval = 15f;

        // ==================== 全局卡住诊断（有移动意图却连续不动）====================
        /// <summary>卡住诊断的判定窗口（秒）：有移动意图、且这整段时间水平位移不足就算一次卡住。
        /// 出处：**本项目新增**（诊断参数，A 无 bot AI；与 <see cref="StuckCheckInterval"/> 分开 ——
        /// 后者是导航内部的短周期自恢复，这里要的是"持续不动"的稳定结论）。</summary>
        public const float StuckDiagSeconds = 2f;

        /// <summary>判定窗口内水平位移低于它就算"没动"（米）。与 <see cref="StuckMoveEpsilon"/> 同值，
        /// 但两者各自独立：改一个不会连带改另一个。</summary>
        public const float StuckDiagMoveEpsilon = 0.15f;

        /// <summary>意图的水平移动分量低于它就不算"想走"（守点 / 下包 / 拆包本身就该站定，不能算卡住）。</summary>
        public const float StuckIntentMoveEpsilon = 0.1f;

        /// <summary>同一只 bot 两次卡住 Warn 之间的最小间隔（秒）—— 计数逐次累计，只有日志限频。</summary>
        public const float StuckDiagLogInterval = 4f;

        /// <summary>"贴身挡路"的判定半径（米）：这个距离内且在本 tick 的目标方向上，就认为是被角色堵住。</summary>
        public const float StuckNeighborRadius = 1f;

        // ==================== 行进朝向（无交战目标时朝移动方向看）====================
        /// <summary>
        /// 非交战时注视点的最小前视距离（米）：注视方向不变，只把注视点推到这么远。
        /// 近处目标点（路点常常只有 1~3m）会让 ±0.1m 的横向微动放大成 ±4° 的朝向摆动，
        /// 表现是观战视角逐帧抖；推到 10m 后同样的微动只占 ~0.6°。
        /// </summary>
        public const float TravelAimDistance = 10f;

        /// <summary>
        /// 非交战时"当前注视方向与移动方向的夹角余弦"低于它就改看移动方向（-0.2 ⇒ 夹角 &gt; 101°）：
        /// 只有真的背对行进方向（避障偏角带偏 / 让行后退）才改朝向；侧移不改 —— 侧移时跟随
        /// 移动方向会把避障方向的逐帧跳动直接放大成朝向摆动。
        /// </summary>
        public const float TravelFaceMoveDot = -0.2f;

        /// <summary>
        /// 交战中允许连续纯后退（dot(move, 朝向) &lt; -0.5）的最长时间（秒）：超过就改成横向侧移。
        /// </summary>
        public const float MaxBackpedalSeconds = 0.6f;

        /// <summary>
        /// 纯后退的判定余弦（dot(move, 朝向) 低于它 = 边打边退）：-0.5 ⇒ 与朝向夹角 &gt; 120°。
        /// </summary>
        public const float BackpedalDot = -0.5f;

        /// <summary>
        /// 瞄准平滑的、**小角度跳变**档角速度上限（度/秒）：180°/s ≈ 60fps 下 3°/帧，
        /// 与验收口径（逐帧 yaw 步长 p95 ≤ 3°/帧）同值。只压这一档 —— 大幅换向走快速档。
        /// </summary>
        public const float AimSmoothDegPerSec = 180f;

        /// <summary>瞄准平滑的**快速档**角速度上限（度/秒）：切目标 / 转身找敌人时不压速。</summary>
        public const float AimSmoothFastDegPerSec = 240f;

        /// <summary>超过这个夹角就算"换目标"而不是"抖"，走快速档（度）。</summary>
        public const float AimSmoothFastAngle = 45f;

        /// <summary>让行（侧移 + 后退）的保持时长（秒）。</summary>
        public const float YieldSeconds = 1.2f;

        /// <summary>同一目标连续判定卡住达到这次数，就把该目标记为"物理走不到"并换下一个目标/守位。</summary>
        public const int UnreachableGoalStreak = 2;

        /// <summary>被标记为"走不到"的目标点记忆时长（秒）：这段时间内选目标时跳过它。</summary>
        public const float UnreachableGoalMemorySeconds = 20f;

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
        /// <summary>近身换手枪的距离（米）：「距离 &lt; 3m → 倾向换手枪」。
        /// 规格 <c>策划/策划案/CS1.6单机参考规格.md</c> §2.4 未给该阈值 ⇒ 只能标"本项目新增"）。</summary>
        public const float MeleeSwitchRange = 3f;

        /// <summary>换回主武器的距离（米）：与上面组成滞回，避免来回切枪。
        /// 出处：**本项目新增**（滞回上界，与 <see cref="MeleeSwitchRange"/> 配套）。</summary>
        public const float MeleeBackRange = 6f;

        /// <summary>两次换枪之间的最小间隔（秒）。出处：**本项目新增**（交战实现参数，A 无 bot AI）。</summary>
        public const float WeaponSwitchCooldown = 1.5f;

        /// <summary>
        /// 换弹时后退的距离（米）：后退点 = 自身沿"远离敌人"方向再走这么远。
        /// 出处：**本项目新增**（A 无 bot AI；取值 = 一个身位多一点，够脱离对方准星又不会退出战场）。
        /// </summary>
        public const float RetreatDistance = 3.5f;

        /// <summary>距离 &gt; 偏好距离 × 它时推进；否则站定（远距离蹲下）。
        /// 出处：**本项目新增**（倍数；被乘的"偏好距离"真源 = <c>CsBotProfile.For</c> 三档表，
        /// <c>Module/Match/CsTypes.cs:148</c>）。</summary>
        public const float AdvanceRangeFactor = 1.5f;

        /// <summary>
        /// 允许扣扳机的最大水平速度（米/秒）：速度没落下来之前一律不开火。
        ///
        /// <para>依据 = 子弹散布直接读水平速度（<see cref="Cs16.Module.Combat.Firearm.ComputeSpread"/>
        /// 的 <c>moveFactor</c> 与档位判据）⇒ 跑动中扣扳机等于把子弹打飞。
        /// 0.05 ≈ 2 unit/s：模拟的 <c>PM_WalkMove</c> 在水平速度 &lt; 0.0254 m/s 时直接清零
        /// （<c>Module/Match/CsMovement.cs</c> 的 <c>MinMoveSpeed</c>）⇒ 本阈值等价于"已经站住"。</para>
        ///
        /// <para>出处：**本项目新增**（实现参数，A 无 bot AI）。</para>
        /// </summary>
        public const float FireStandSpeed = 0.05f;

        /// <summary>
        /// 持包压点途中遇敌时站定射击的保持时长（秒）：停下打完这一梭 / 这一串点射再继续压。
        /// 出处：**本项目新增**（实现参数，A 无 bot AI；取值落在"打完一个连发段"的量级）。</summary>
        public const float EngageShotHoldSeconds = 0.45f;

        /// <summary>
        /// 站定射击时蹲下的距离门槛（米）：超过它才蹲（压低受弹面），近距离不蹲（换位更快）。
        /// 出处：**本项目新增**（实现参数，A 无 bot AI）。</summary>
        public const float EngageCrouchDistance = 20f;

        /// <summary>
        /// 开火角度门限 = AimErrorDegrees × 它 + AimGateExtraDegrees。
        ///
        /// <para><b>量与什么</b>：夹角是"本 tick 提交给模拟的瞄向（<c>SmoothAim</c> 的方向，也就是子弹要飞的
        /// 方向）"与"到目标瞄点的方向"之差，**不是**角色朝向与瞄点之差 —— 角色朝向由模拟按难度转视角速度追，
        /// 落后十几度是常态，拿它当判据等于允许子弹沿偏十几度的方向打出去（实测 77%~86% 的弹着点是墙）。
        /// 提交的瞄向由本类自己逐帧平滑，几十毫秒内就收敛，所以门限可以收紧到瞄准误差的量级。</para>
        ///
        /// <para>三档门限：Easy ±6° → 4.8°，Normal ±3° → 3.0°，Hard ±1.2° → 2.2°，
        /// 与 <c>AimErrorDegrees</c> 同向（越准的档越早扣扳机、偏得也越少）。</para>
        ///
        /// <para>出处：**本项目新增**（门限系数，A 无 bot AI；被乘的 <c>AimErrorDegrees</c> 真源 =
        /// 规格 §2.4 三档瞄准误差 ±6° / ±3° / ±1.2°，落在 <c>CsBotProfile.For</c>）。</para>
        /// </summary>
        public const float AimGateErrorScale = 0.6f;

        /// <summary>开火角度门限的固定附加值（度）。
        /// 出处：**本项目新增**（门限项，与 <see cref="AimGateErrorScale"/> 同组）。</summary>
        public const float AimGateExtraDegrees = 1.2f;

        /// <summary>开火角度门限下限（度）。
        /// 出处：**本项目新增**（门限钳制下限，与 <see cref="AimGateErrorScale"/> 同组）。</summary>
        public const float AimGateMinDegrees = 2.2f;

        /// <summary>开火角度门限上限（度）。
        /// 出处：**本项目新增**（门限钳制上限，与 <see cref="AimGateErrorScale"/> 同组）。</summary>
        public const float AimGateMaxDegrees = 5f;

        /// <summary>
        /// "站定"判据相对开火门限的倍数：瞄向差 ≤ 门限 × 它就先停下来等速度归零，
        /// 落定后再按门限扣扳机。站定判据必须比开火门限宽，否则瞄向没收敛时一直侧移、
        /// 水平速度永不归零，扳机永远扣不下去。
        /// 出处：**本项目新增**（bot 交火实现参数，A 无 bot AI）。
        /// </summary>
        public const float StandGateScale = 3f;

        /// <summary>交战开火的俯仰角上限（度）：瞄准方向相对地平线的 |pitch| 超过它即视为未锁定、不开火。
        /// 出处：**本项目新增**（原版视角上限 89°，交战瞄准不应超过 45° —— 修"AI 朝天开枪"）。</summary>
        public const float AimPitchLimitDegrees = 45f;

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
        /// 出处：**本项目新增**。原版**有**对应量（玩家模型三组 hitbox 的高度偏移，写在 mdl 的 hitbox 表里，
        /// 由 <c>mp.dll</c> 消费），但承载它的原版 <c>models/player/*.mdl</c> 本机不在盘
        /// （<c>原版资源/</c> 实测只有 <c>cs16src/</c> 73 份 cstrike 资源 · <c>hlsdk/</c> · <c>备份/</c> ·
        /// <c>_moved-out-from-assets/</c>，无 <c>models/</c>；<c>mp.dll</c> 本身已在盘但尚未反汇编，
        /// 不许当"有出处"。</summary>
        public const float ChestHeightRatio = 0.78f;

        /// <summary>预瞄提前量的最大秒数（按难度插值：Normal 0 → Hard 满值）。
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

        /// <summary>
        /// 远距点射的门槛距离（米）：超过它每串只打 <see cref="BurstMaxRounds"/> 发以内的点射。
        /// 步枪连发时后坐力/散布随时间累积，远距离整梭扫射的命中率低于点射；
        /// 近于 <see cref="BurstAutoRange"/> 才允许连发。
        /// 出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。
        /// </summary>
        public const float BurstFarRange = 15f;

        /// <summary>允许连发的近身距离（米）：小于它不限制连发（贴脸时射速优先于精度）。
        /// 出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。</summary>
        public const float BurstAutoRange = 8f;

        /// <summary>点射的每串最少发数。出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。</summary>
        public const int BurstMinRounds = 2;

        /// <summary>点射的每串最多发数。出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。</summary>
        public const int BurstMaxRounds = 4;

        /// <summary>
        /// 每串发数从最多降到最少的距离上界（米）：到该距离取 <see cref="BurstMinRounds"/> 发。
        /// 距离越远、目标角尺寸越小，串内后几发的散布越可能直接脱靶 ⇒ 远距离只打短点射。
        /// 出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。
        /// </summary>
        public const float BurstRoundsDropRange = 30f;

        /// <summary>两串点射之间的停火时长（秒）：停火期间只保持指向、不扣扳机。
        /// 出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。</summary>
        public const float BurstPauseSeconds = 0.28f;

        /// <summary>
        /// 远距"只打慢目标"的距离门槛（米）：超过它时目标横向速度大于
        /// <see cref="FarFireMaxTargetSpeed"/> 就不扣扳机。判据来自外推的失效边界：
        /// 在该距离上，一个身位（约 0.7m）对应的角度已经小于瞄准误差，跑动中的目标靠外推打不中。
        /// 出处：**本项目新增**（bot 交火节奏阈值，A 无 bot AI）。
        /// </summary>
        public const float FarFireRange = 25f;

        /// <summary>远距仍允许开火的目标横向速度上限（米/秒）：走位 / 静步 / 站定都在此之内，跑动超过它。
        /// 出处：**本项目新增**（bot 交火节奏阈值；量级参照静步 0.52 倍速与跑速 5.4 m/s 之间）。</summary>
        public const float FarFireMaxTargetSpeed = 2f;

        /// <summary>
        /// 主动换弹的弹匣余量比例：低于它就换弹（前提见 <c>CsBotBrain.ShouldReloadNow</c> ——
        /// 看不见敌人、或敌人在 <see cref="ReloadSafeDistance"/> 之外）。
        /// 出处：**本项目新增**（bot 换弹时机阈值，A 无 bot AI）。
        /// </summary>
        public const float ReloadLowMagRatio = 0.25f;

        /// <summary>换弹时"敌人至少这么远才安全"（米）。出处：**本项目新增**（bot 换弹时机阈值）。</summary>
        public const float ReloadSafeDistance = 30f;

        /// <summary>
        /// 目标离开视野后按速度外推的**上限秒数**：瞄点 = 最后所见位置 + 目标速度 ×
        /// min(离开视野时长, 它)。外推只在外推量仍然可信的窗口内有效，再久就按"记忆位置"静瞄。
        /// 出处：**本项目新增**（bot 预瞄实现参数，A 无 bot AI）。
        /// </summary>
        public const float PredictLastSeenMaxSeconds = 0.35f;

        /// <summary>
        /// 可见目标的固定提前量（秒）= 半个决策 tick：决策到实际扣扳机之间平均隔半个
        /// <see cref="CsConst.BotTickInterval"/>，不补偿就等于每发都打在目标半个 tick 前的位置上。
        /// 出处：**本项目新增**（bot 预瞄实现参数，A 无 bot AI）；被乘的 tick 真源 =
        /// <c>CsConst.BotTickInterval = 0.1f</c>（<c>Core/CsConst.cs:688</c> 的 AI 决策频率 10Hz）。
        /// </summary>
        public const float PredictTickLeadSeconds = CsConst.BotTickInterval * 0.5f;

        /// <summary>远距离优先用狙击枪的距离门槛（米）：手上有狙且目标在这个距离外就先切回主武器。
        /// 出处：**本项目新增**（bot 选枪阈值，A 无 bot AI）。</summary>
        public const float SniperRange = 30f;

        /// <summary>
        /// 残血后撤的血量门槛：血量低于它、且护甲低于 <see cref="RetreatArmorBelow"/>、
        /// 且目标在 <see cref="RetreatTriggerRange"/> 之外 ⇒ 先脱离交火找掩体。
        /// 出处：**本项目新增**（bot 撤退阈值，A 无 bot AI）。
        /// </summary>
        public const int RetreatHealthBelow = 30;

        /// <summary>残血后撤的护甲门槛。出处：**本项目新增**（bot 撤退阈值，A 无 bot AI）。</summary>
        public const int RetreatArmorBelow = 20;

        /// <summary>残血后撤的目标距离下限（米）：只有远距离对枪才值得后撤，近距离撤离等于送背身。
        /// 出处：**本项目新增**（bot 撤退阈值，A 无 bot AI）。</summary>
        public const float RetreatTriggerRange = 25f;

        /// <summary>一次后撤的持续时长（秒）：窗口内不开火、只朝掩体移动，结束后重新交战。
        /// 出处：**本项目新增**（bot 撤退实现参数，A 无 bot AI）。</summary>
        public const float RetreatHoldSeconds = 1.6f;

        /// <summary>两次后撤之间的最小间隔（秒）：残血且敌人仍远时，后撤-交战会反复发生，
        /// 这个冷却让每次后撤之后至少打一段再走。出处：**本项目新增**（bot 撤退实现参数）。</summary>
        public const float RetreatCooldownSeconds = 5f;

        /// <summary>后撤落点的候选扇面（度）：以"远离目标"为中线向两侧各扫
        /// <see cref="RetreatCoverSteps"/> × 它，挑一个有掩体、离目标最远的可站立点。
        /// 出处：**本项目新增**（bot 撤退实现参数，A 无 bot AI）。</summary>
        public const float RetreatCoverStepDegrees = 20f;

        /// <summary>后撤候选扇面的单侧步数（每步 <see cref="RetreatCoverStepDegrees"/> 度）。
        /// 出处：**本项目新增**（bot 撤退实现参数）。</summary>
        public const int RetreatCoverSteps = 4;

        /// <summary>
        /// 后撤落点评分的"掩体权重"（米）：断视线（<c>ICsMatch.HasLineOfSight</c> 为假）的候选点
        /// 加这么多分，与"离目标多远"同量纲比较。取 10m ⇒ 有掩体的近点优先于无掩体的远点，
        /// 但掩体后仍会挑更远的那个。出处：**本项目新增**（bot 撤退实现参数）。
        /// </summary>
        public const float RetreatCoverWeightMeters = 10f;

        /// <summary>
        /// 投掷前"预测落点距自己"的安全余量（米）：实际投掷方向带难度瞄准误差
        /// （<c>CsActor.Yaw/Pitch</c> 由模拟按 <c>AimErrorDegrees</c> 扰动后再积分），
        /// 预测落点与真实落点会差一小段，余量保证误差吃掉之后仍在爆心半径之外。
        /// 出处：**本项目新增**（投掷安全实现参数，A 无 bot AI）。
        /// </summary>
        public const float GrenadeSelfSafetyMargin = 0.5f;

        /// <summary>
        /// 释放瞬间发现"这一投会落在自己爆心半径内"（视角尚未收敛到位）时的重试间隔（秒）：
        /// 换一颗雷的代价远小于自伤，稍等让视角收敛再投。
        /// 出处：**本项目新增**（投掷安全实现参数，A 无 bot AI）。
        /// </summary>
        public const float GrenadeSelfRetrySeconds = 2f;

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

        /// <summary>
        /// 非拆弹者的**掩护站位半径**（米）：CT 只派"离 C4 最近的那一个"按住 E，
        /// 其余人在这个距离外站住、盯住 T 的来路。
        /// 出处：**本项目新增**（A 无 bot AI；取值 = C4 爆炸致伤半径 <c>CsConst.GrenadeHeRadius</c>(5.5 m)
        /// 之上留 0.5 m 余量，保证掩护者不在拆包点被同一颗手雷一起带走）。
        /// </summary>
        public const float DefuseSupportRadius = 6f;

        /// <summary>拆包被挡时绕行落脚点到 C4 的水平半径（米）：取能绕开箱子/掩体、又不算"远离"的一圈。</summary>
        public const float DefuseSidestepRadius = 1.1f;

        /// <summary>
        /// 模拟层转身角速度的**硬上限**（度/秒）。Bot 提交的瞄点再平滑，最终 `a.Yaw` 仍由
        /// <c>CsMatch</c> 按难度 AimSpeedDegrees 追（Hard 520°/s = 8.7°/帧@60fps）；配合每 0.35s
        /// 重采样的 ±瞄准误差，就是观战画面抖动的来源。180°/s = 3°/帧，与表现层判据一致。
        /// </summary>
        public const float AimMaxDegPerSec = 180f;

        /// <summary>
        /// 拆包/下包人选重新选举的间隔（秒）：C4 位置固定，选举只在"最近的人死掉/换位"时才变；
        /// 按间隔重算即可，不必逐帧遍历全队。
        /// 出处：**本项目新增**（选举实现参数）。
        /// </summary>
        public const float DefuseElectInterval = 0.5f;

        /// <summary>
        /// 守包（C4 已下）时盯住来路的搜索半径（米）：取该半径内**离 C4 最近的** T 侧来路标记点。
        /// 出处：**本项目新增**（守包注视点实现参数；标记点本身来自 de_dust2 的 <c>CsMarkers.CTDefend*</c>）。
        /// </summary>
        public const float GuardWatchRange = 30f;

        /// <summary>
        /// 守点注视点选路的**最小距离**（米）：T 进攻路线里"落在包点判定球内"的那几个点离守位只有 4~7m，
        /// 站在旁边时方位随一步移动就摆几十度（守卫会来回甩头），所以不拿它们当注视点；
        /// 取"包点判定球以外"里离守位最近的那个路点 = 真正的来路。
        /// <para>出处：**本项目新增**（守点注视点实现参数）；数值 = 2 × <see cref="SiteRadius"/>，
        /// 后者逐值搬自 <c>Module/Map/ICsMap.cs</c> 的 <c>CsMarkers.BombsiteRadius = 7f</c>。</para>
        /// </summary>
        public const float HoldWatchMinDistance = 2f * SiteRadius;

        /// <summary>炸弹剩余时间 ≤ 拆包所需 + 它时，优先拆包（Hard 的"拆包果断"）。
        /// **余量秒数本项目新增**。</summary>
        public const float DefuseUrgencyMargin = 2f;

        /// <summary>
        /// 已下包后，"眼前有多近的敌人才值得先打、而不是先去拆包"（米）。
        /// 超过它就一律先冲包点 —— 官方 CT 的行为是"下包即刻回防"，站在远处对枪等于把回合送掉。
        ///
        /// </summary>
        public const float DefuseOverFightRange = 15f;

        /// <summary>守点时每次挪窝后的停留时长（秒）。
        /// 出处：**本项目新增**（守点实现参数，A 无 bot AI）。</summary>
        public const float CampRepositionSeconds = 2.5f;

        /// <summary>守点时可以在守点周围多大半径内挪窝（米）——"会换位"的范围。
        /// 出处：行为口径见规格 §2.4 的 Hard 行「会换位」；**半径值本项目新增**。</summary>
        public const float CampRepositionRadius = 6f;

        /// <summary>
        /// 包点守卫**换位间隔**（秒）：CT 在自己的守位表里每这么多秒挪到下一个守位。
        ///
        /// <para>为什么不是"到点就走"：守 <see cref="ObjectiveHoldSeconds"/> 就换目标会退化成"原地踱步"——
        /// 守位表把"守点"拆成**站住 + 定时换位** —— 人始终留在包点，只是在包点内的 2~3 个守位之间轮换。</para>
        ///
        /// <para>出处：**本项目新增**（A = CS 1.6 本体不含 bot AI，见本文件 :18-19，本量在 A 里没有对应载体）。
        /// 取值理由：① 明显大于 <see cref="CampRepositionSeconds"/>=2.5s 的"抖动"量级，移动是"换位"而不是来回蹭；
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
        /// 守位相对包点**标记重心**的最大水平距离（米）：超出的标记点不进守位表。
        ///
        /// <para>出处：**本项目新增**（守位表实现参数，A 无 bot AI）。取值理由 = 略小于包点判定半径
        /// <see cref="SiteRadius"/>（7m）：包点标记可能横跨十几米，只按"离队友最远"挑会把守卫挑到最外侧的
        /// 标记上、离实际下包位置（A#0）十几米，观感就是"没人在包点里守"。收进重心 6m 内既保证在判定圈内，
        /// 也保证在下包点附近。</para>
        /// </summary>
        public const float HoldSpotCenterRadius = 6f;

        /// <summary>
        /// 携带 C4 的 T 走到离**最近的包点标记**多远就停下开下（米）。
        ///
        /// <para>为什么必须**远小于**包点判定半径 <see cref="SiteRadius"/>（= 7m，
        /// 真源 <c>Module/Map/ICsMap.cs:83</c> 的 <c>CsMarkers.BombsiteRadius</c>）：
        /// 停步半径 == 判定半径（用 <see cref="SiteRadius"/> 同时当"走到哪算到"与"能不能下"）时，
        /// 机器人会**正好停在判定球面上**，`CsBomb.CanPlant` 的 `IsInBombsite` 随浮点误差反复真假 ⇒
        /// 表现为"到了包点又走了"（下包永远起不来）。这里取 1.5m，保证站定时
        /// "到最近包点标记的距离" 恒 &lt; 判定半径。</para>
        ///
        /// <para>出处：**本项目新增**（下包实现参数，A 无 bot AI；被保护的量 <c>BombsiteRadius=7</c> 有出处）。</para>
        /// </summary>
        public const float PlantStopRadius = 1.5f;

        /// <summary>
        /// 捡掉落 C4 的旧分工模数：只有 <c>id % 它 == 0</c> 的 T 才去捡（想让 4 人一队只出一个人）。
        /// 这个模数已被 **"离 C4 最近的那一个 T 去捡"** 取代（<c>CsBotBrain.IsElectedBombHunter</c>，
        /// 唯一且确定）：按模数挑的人**不是离 C4 最近的那个**，而且"最近的 T 编号不整除"时
        /// 整条捡包分支一次都不进 ⇒ C4 躺到回合结束（`拾起了` = 0 条）。
        /// 现行口径仍然只有一个人去，不改掉落/拾取规则本身。
        /// 出处：**本项目新增**（分工实现参数，A 无 bot AI）。
        /// 现行口径的落点：<c>CsBotBrain.IsElectedBombHunter</c>。
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

        /// <summary>Hard 档买 AWP 的概率（30%）。
        /// 规格 §2.4 的 Hard 行只写「会买最好枪」、未给概率 ⇒ 只能标"本项目新增"）。</summary>
        public const float AwpChanceTier2 = 0.30f;

        // ==================== 投掷物（角色分工）====================
        /// <summary>烟雾弹的最近投出距离（米）：比它更近就直接开枪，不扔。
        /// 取值 = 烟雾球半径量级（<c>CsMatchConst.SmokeBlockRadius</c> 3.5m）稍下 —— 扔在脚下的烟只挡自己。
        /// 出处：**本项目新增**（bot 投掷取舍阈值）。原版 mp.dll 的 bot 侧只留状态名 <c>GrenadeThrow</c>（0x1247BC）
        /// 与购买序列（0x124AB0 的 <c>sgren/flash/hegren</c>），**没有任何距离阈值数据**。</summary>
        public const float GrenadeMinRange = 3f;

        /// <summary>
        /// 会伤到自己的那两种投掷物（高爆 / 闪光）的最近投出距离（米）——
        /// **等于爆心半径本身**（<c>CsConst.GrenadeHeRadius</c> 5.5m；<c>CsMatchConst.FlashRadius</c> 也取它）：
        /// 站在半径之外才投 ⇒ 自己既不吃自己的 HE，也不会被自己的闪光照瞎
        /// （<c>CsDamage.ApplyHeExplosion</c> / <c>ApplyFlash</c> 都不豁免投掷者本人，两者都按"距离 × 视线"给强度）。
        /// </summary>
        public const float GrenadeSelfSafeRange = CsConst.GrenadeHeRadius;

        /// <summary>
        /// 投出的最远距离（米）——**由投掷物理定出的上界，不是手感值**：
        /// 初速随瞄向俯仰变化 <c>CsConst.GrenadeThrowSpeed(pitch)</c>（原版口径 <c>(90 - angThrow.x) × 4</c>），
        /// 之后只受重力 <c>CsConst.Gravity</c>(20.32 m/s²) ⇒ 平抛落点 <c>d(θ) = v(θ)² · sin(2θ) / g</c>。
        /// 抬头同时抬高仰角与初速 ⇒ 峰值**不再**在 45°：数值扫得峰值 ≈ <b>10.04 m（θ ≈ 55°）</b>。
        /// 取 9.6 m 留 0.44 m 余量 ⇒ 低弧解算（见 <c>CsBotBrain.GrenadeAimPoint</c>）在这个窗口内恒有解（9.6 m 处所需仰角 ≈ 52°）。
        /// </summary>
        public const float GrenadeMaxRange = 9.6f;

        /// <summary>两次投掷之间的最小间隔（秒）：防"一回合把手上几颗雷连着一次性扔光"。
        /// 出处：**本项目新增**。</summary>
        public const float GrenadeThrowSpacing = 8f;

        /// <summary>一回合最多投几颗。出处：**本项目新增**（原版口径只体现"一回合带得起 sgren/flash/hegren 各有限"，
        /// 见 <c>CsMatchConst.MaxHeGrenades/MaxFlashbangs/MaxSmokes</c>）。</summary>
        public const int GrenadeMaxPerRound = 2;

        /// <summary>"切到手上那颗雷"的等待上限（秒）：切枪动作由模拟执行，这里只做超时兜底 ——
        /// 超时就放弃这颗并进入下一个间隔，避免机器人抱着 SwitchTo 站着不动。
        /// 出处：**本项目新增**（实现兜底；量级 = <c>CsConst.SwitchTime*</c> 两倍）。</summary>
        public const float GrenadeSwitchTimeout = 1.6f;

        /// <summary>切雷后允许扣扳机的朝向门限（度）：视角还没转到瞄点就投 = 扔到身后。
        /// 出处：**本项目新增**（投掷实现参数）。</summary>
        public const float GrenadeAimGateDegrees = 12f;

        /// <summary>开局多少秒内禁止投掷（秒）。开局投掷只会砸在出生点的队友身上。
        /// 出处：**本项目新增**（投掷时机门槛）。</summary>
        public const float GrenadeNoThrowSeconds = 9f;

        /// <summary>没有敌人时允许投掷的"进攻准备位"距离（米）：到本轮包点标记 ≤ 它才投
        /// （更远时雷只能飞出 9.6m，扔不到点上）。出处：**本项目新增**（投掷时机门槛）。</summary>
        public const float GrenadeApproachRange = 22f;

        /// <summary>投掷物落点 / 投掷锥内的队友安全半径（米）：这个范围内有队友就不投
        /// （不许把闪光/高爆扔在队友身上）。出处：**本项目新增**（投掷安全门槛）。</summary>
        public const float GrenadeTeammateSafeRadius = 25f;

        /// <summary>投掷锥半角（度）：队友落在这个锥内且距离 ≤ <see cref="GrenadeTeammateSafeRadius"/> 也算会被打到。</summary>
        public const float GrenadeTeammateConeDegrees = 30f;

        /// <summary>下包前的敌人闸门（米）：敌人可见、或刚丢视野（记忆期内）且水平距离 ≤ 它 ⇒ 放弃下包先应战。
        /// 出处：**本项目新增**（bot 下包取舍阈值；A 无 bot AI）。</summary>
        public const float PlantEnemyAbortRange = 15f;

        /// <summary>下包前敌人闸门的"刚丢视野"记忆时长（秒）。</summary>
        public const float PlantEnemyMemorySeconds = 3f;

        /// <summary>同层判定容差（米）：下包点标记与自身脚面的高度差超过它就不算同一落脚层。
        /// 取两倍台阶高，允许标记点在台阶边缘的轻微高度差。</summary>
        public const float PlantSameFloorHeight = 2f * CsConst.StepUpHeight;

        // ==================== 统计 / 日志 ====================
        /// <summary>命中率统计日志间隔（秒）：按难度分档的命中率要的就是这条。
        /// 出处：**本项目新增**（分档统计口径 + 日志降频参数）。</summary>
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

        /// <summary>"同队路线互斥"审计间隔（秒）——见 <c>BotModule.AuditRouteDistinctness</c>。
        /// 取值 1.0s：路线只在"回合开始 / 换目标"时变（低频事件），审计比它密一个量级即可抓到
        /// 回合中途加入的 bot 造成的重合，而 28 对比较/tick 的开销可忽略。
        /// 出处：**本项目新增**（A 无 bot AI，同差异 #67）。</summary>
        public const float RouteAuditIntervalSeconds = 1f;

        /// <summary>
        /// 路线审计那两条 Warn（"同队路线撞车" / "同队非分工路线共用"）的**日志**降频间隔（秒）。
        ///
        /// <para><b>为什么需要</b>：<see cref="RouteAuditIntervalSeconds"/> = 1s，审计每次**逐对**比较
        /// 4 个槽位 ⇒ 一旦真的撞车，同一秒内会打出最多 6 条 Warn（原先没有闸）。而
        /// <c>CsBotBrain.TryRouteObjective</c> 的"最后一档兜底"会**有意**点开被队友占着的家族
        /// （无空家族 + 本槽位换不出目标点时的自恢复），撞车对随之为真 ⇒ 不打闸就是刷屏。</para>
        ///
        /// <para>计数（<c>BotModule.RouteCollisionPairs</c> / <c>RouteOffPlanPairs</c>）**照旧逐对累计**，
        /// 这里只限速日志 —— 判据与可见性都不动。</para>
        ///
        /// <para>取值 10s：比审计间隔密一个量级就够抓"何时开始撞"，而 10s 一条对排查足够；
        /// 出处：**本项目新增**（日志降频口径）。</para>
        /// </summary>
        public const float RouteCollisionWarnIntervalSeconds = 10f;

        /// <summary>
        /// 逃逸跑道的地面预检允许的**最大下降量**（米）：预检点在"该方向 <see cref="EscapeRunwayStep"/>×2"
        /// 处，命中地面比脚下低超过它 ⇒ 这个方向不算跑道。
        ///
        /// <para><b>为什么不能取 <see cref="CsConst.StepUpHeight"/>(0.457)</b>：那等于"1.4m 水平内下降
        /// 超过 0.457m 就否"，换算成坡度是 <b>&gt; 18.1° 一律否</b> —— 而本图有大量 20~30° 斜坡
        /// （T 出生坡道 / 中路 / A 大道），还有"从箱顶/矮墙朝地面走"这类**物理本来就走得下去**的形态。
        /// 更糟的是预检只在 `run &lt;= 0`（位图沿该向全判挡）时生效，而坡道/台沿正是"位图误挡"的原用例
        /// 多发地带 ⇒ 会把本方法要救的情形误杀，退回"位图说挡就原地不动"。</para>
        ///
        /// <para><b>为什么取 1.2m</b>：≈ 一个箱子高（<c>0.9~1.2m</c>），足以放行坡道与矮台下落，
        /// 又明显小于本图**图内层间落差 6.8m**，更拦得住"图外真空/下整层"这类真正的假跑道。</para>
        ///
        /// <para>出处：**本项目新增**（导航自恢复实现参数，原版 A 无 bot AI）。</para>
        /// </summary>
        public const float EscapeRunwayMaxStepDown = 1.2f;
    }
}
