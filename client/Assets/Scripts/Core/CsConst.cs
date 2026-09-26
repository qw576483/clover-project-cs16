namespace Cs16.Core
{
    /// <summary>
    /// 全局玩法常量（数值真源）。改数值一律改这里，禁止在业务脚本里写裸数字。
    ///
    /// <para><b>口径（见 <c>策划/对照表.md</c> §5.2）</b>：一律取
    /// **随包 <c>server.cfg</c> 的覆盖值**（= 玩家实际生效值，出处
    /// <c>原版资源/cs16src/cs16game/app/cstrike/server.cfg</c>）；<c>mp.dll</c> 里的**出厂默认**作为第二口径
    /// 同时登记在对照表里、**不混用**。</para>
    ///
    /// <para><b>单位</b>：GoldSrc 1 unit = 1 inch = <b>0.0254 m</b>（出处
    /// <c>原版资源/cs16src/cs16_build.py:43</c> <c>HL_UNIT = 0.0254</c>）。凡"折算"= 原版 unit 值 × 0.0254。</para>
    ///
    /// <para><b>出处简写</b>（本文件用到的两处载体，全路径见 <c>策划/对照表.md</c> §0）：
    /// <c>server.cfg:N</c> = <c>原版资源/cs16src/cs16game/app/cstrike/server.cfg</c> 第 N 行；
    /// <c>mp.dll:0x…</c> = <c>原版资源/cs16src/cs16game/app/cstrike/dlls/mp.dll</c> 的文件偏移；
    /// <c>hw.dll.orig:0x…</c> = <c>原版资源/cs16src/cs16game/app/hw.dll.orig</c> 的文件偏移。</para>
    /// </summary>
    public static class CsConst
    {
        // ---- 经济 ----
        public const int StartMoney = 800;
        public const int MaxMoney = 16000;
        public const int RewardRoundWinElimination = 3250;   // 全歼取胜
        public const int RewardRoundWinBombExplode = 3500;   // 炸弹爆炸取胜（T）
        public const int RewardRoundWinBombDefuse = 3500;    // 拆包取胜（CT）
        public const int RewardRoundWinTimeExpired = 3250;   // 时间到（CT）
        public const int RewardLossBase = 1400;              // 连败奖励基数
        public const int RewardLossStep = 500;               // 每连败一局递增
        public const int RewardLossMax = 3400;               // 连败奖励上限
        public const int RewardBombPlant = 800;              // 下包者个人奖励
        public const int RewardBombDefuse = 300;             // 拆包者个人奖励

        // ---- 回合 ----
        /// <summary>
        /// 冻结时间 = <c>mp_freezetime 4</c>（秒）· 出处 <c>server.cfg:51</c>
        /// </summary>
        public const float FreezeTime = 4f;

        /// <summary>
        /// 买枪期 = <c>mp_buytime 0.25</c>（分钟）⇒ <b>15 秒</b> · 出处 <c>server.cfg:42</c>
        /// （另一口径：<c>mp.dll</c> 出厂默认 <c>1.5</c> 分钟 = <b>90 s</b> · <c>mp.dll:0x11b9a8</c>；<c>settings.scr:100</c> 也是 <c>0.250000</c>）。
        ///
        /// <para><b>它与冻结期是两个独立计时器</b>（原版行为）：冰结结束后只要 <see cref="BuyTime"/> 没用尽、且人还在
        /// <c>func_buyzone</c> 内，就仍然可以买枪；15 s 用尽后即便站在买枪区也不能买。
        /// 计时的实现与判定在 <c>Cs16.Module.Match.CsRound.BuyTimeLeft</c> / <c>CsMatch.CanBuyTime()</c>。</para>
        /// </summary>
        public const float BuyTime = 15f;

        /// <summary>
        /// 回合时长 = <c>mp_roundtime 1.75</c>（分钟）⇒ <b>105 秒</b> · 出处 <c>server.cfg:37</c>
        /// （另一口径：<c>mp.dll</c> 出厂默认 <b>5</b> 分钟 = 300 s · <c>mp.dll:0x11b990</c>）。
        /// </summary>
        public const float RoundTime = 105f;

        public const float RoundEndTime = 5f;       // 回合结算展示时长（原版未解出该时长 · 本项目自行设计，登记在对照表"允许的差异"）
        public const int RoundsPerHalf = 15;        // 半场回合数
        public const int MaxRounds = 30;            // 全场回合数（15+15）

        // ---- 炸弹 ----
        /// <summary>
        /// C4 倒计时 = <c>mp_c4timer 35</c>（秒）· 出处 <c>server.cfg:43</c>
        /// （另一口径：<c>mp.dll</c> 出厂默认 <b>45</b> · <c>mp.dll:0x11b9d8</c>；<c>settings.scr</c> 未收录该 cvar）。
        /// </summary>
        public const float BombTimer = 35f;
        public const float PlantTime = 3f;
        public const float DefuseTime = 10f;
        public const float DefuseTimeWithKit = 5f;
        /// <summary>蜂鸣间隔的衰减系数：每响一次，后续间隔 ×0.9（原版节奏不是"按剩余秒数分档"）。
        /// 出处：原版 <c>mp.dll:0x100449D8</c> 的 <c>fmul [0x10141BC8]</c>，
        /// 常量字节 <c>66 66 66 3F</c> = <b>0.9f</b>。</summary>
        public const float BombBeepIntervalDecay = 0.9f;

        /// <summary>蜂鸣档位数：档位计数 0..4 共 5 档，逐档换音 <c>c4_beep1..5</c>，
        /// 第 5 档之后沿用第 5 档（原版 <c>cmp eax,4 / ja</c> 直接跳过换音）。
        /// 出处：原版 <c>mp.dll</c> 蜂鸣函数读 <c>[edi+0x1B8]</c>（<c>0x100449C6</c>）、<c>cmp eax,4</c>（<c>0x100449E4</c>）、
        /// 跳表 <c>0x10044DD0</c>、递增点 <c>0x10044AD1</c>（<c>inc [edi+0x1B8]</c>）。</summary>
        public const int BombBeepTierCount = 5;

        /// <summary>首响前的等待（= 首响用的间隔，秒）。<b>本项目自定 · 原版未取证</b>：
        /// 原版 <c>m_flNextFreqInterval</c> 的初值（写点）在 <c>mp.dll</c> 里未定位到
        /// （<c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §6）。取 <see cref="BombTimer"/> / 11 的依据：
        /// 间隔按"每响 ×<see cref="BombBeepIntervalDecay"/>"衰减时，各次蜂鸣时刻之和收敛于 11 × 初值
        /// （首响 1 份 + 之后 1/(1−0.9) = 10 份），35/11 ≈ 3.18 s 让最后一响落在引信耗尽处，
        /// 而不是间隔衰减到 0 后每帧一响。登记在 <c>策划/差异登记.tsv</c>。</summary>
        public const float BombBeepIntervalInitial = BombTimer / 11f;

        // ---- 单位换算 ----
        /// <summary>GoldSrc 世界单位 → 米：1 unit = 1 inch = <b>0.0254 m</b>
        /// （出处 <c>原版资源/cs16src/cs16_build.py:43</c> <c>HL_UNIT = 0.0254</c>）。
        /// 凡"折算"= 原版 unit 值 × 本常量。</summary>
        public const float UnitToMeter = 0.0254f;

        // ---- 伤害 ----
        public const float HitHead = 4.0f;
        public const float HitChest = 1.0f;
        public const float HitStomach = 1.25f;
        public const float HitLeg = 0.75f;
        public const float ArmorAbsorbRatio = 0.5f;   // 有甲吸收
        public const float ArmorDamageRatio = 0.5f;   // 护甲损耗比例
        // ---- 距离衰减：按弹种取分母（公式 pow(rangeModifier, dist_in_units / 分母)）----
        //
        // 出处 = 原版资源/cs16src/cstrike/dlls/mp.dll 的子弹模拟函数 VA 0x10025640 里的按弹种参数表，
        // 逐弹种的 float 读数见 .ai-tmp/test/mpdll-reverse/VALUES.md §4.3。
        // 弹种 → 分母由 CsWeaponDef.FalloffDenominator 携带；消费点 = CsWeapons.DistanceFalloff。
        public const float FalloffDenom9MM = 800f;      // 弹种 1
        public const float FalloffDenom45ACP = 500f;    // 弹种 9
        public const float FalloffDenom556MM = 4000f;   // 弹种 12（0xC）
        public const float FalloffDenom762MM = 5000f;   // 弹种 11（0xB）
        public const float FalloffDenom338MAG = 8000f;  // 弹种 10（0xA）
        public const float FalloffDenom50AE = 1000f;    // 弹种 13（0xD）
        // 弹种 14（0xE，5.7mm）与 15（0xF，.357SIG）：取自**同一张**按弹种参数表的第 14/15 格
        // （PEN-FILL.md §3 旁证 / VALUES.md §4.3）；同表 6 个已知弹种逐条吻合 ⇒ 同表内插，
        // 非外部估算。置信度低于逐字节直读（这两格没有独立的第二出处）。
        public const float FalloffDenom57MM = 2000f;    // 弹种 14（0xE）
        public const float FalloffDenom357SIG = 800f;   // 弹种 15（0xF）

        // ---- 坠落伤害（原版落地判定）----
        // 出处：原版资源/hlsdk/pm_shared/pm_shared.c:124-126
        //   #define PLAYER_FATAL_FALL_SPEED     1024
        //   #define PLAYER_MAX_SAFE_FALL_SPEED   580
        //   #define DAMAGE_FOR_FALL_SPEED (float)100 / (PLAYER_FATAL_FALL_SPEED - PLAYER_MAX_SAFE_FALL_SPEED)
        // 原版量纲是 unit/s；本工程速度用 m/s ⇒ 阈值与系数按 UnitToMeter 折算，换算写在各自注释里。

        /// <summary>安全坠落速度 = 原版 <c>PLAYER_MAX_SAFE_FALL_SPEED 580</c> unit/s
        /// ⇒ <c>580 × 0.0254</c> = <b>14.732 m/s</b>。落地瞬间的下坠速率不高于它 ⇒ 无伤害。</summary>
        public const float FallSafeSpeed = 580f * UnitToMeter;

        /// <summary>致死坠落速度 = 原版 <c>PLAYER_FATAL_FALL_SPEED 1024</c> unit/s
        /// ⇒ <c>1024 × 0.0254</c> = <b>26.0096 m/s</b>。公式在这一点恰好给出 100 点伤害 ⇒ 满血即死。</summary>
        public const float FallFatalSpeed = 1024f * UnitToMeter;

        /// <summary>坠落伤害的速率系数 = 原版 <c>100 / (1024 − 580)</c>（每 unit/s 0.2252 伤害）
        /// ⇒ 米制 <c>100 / (FallFatalSpeed − FallSafeSpeed)</c> = <b>8.8671</b> 伤害每 (m/s)。
        /// 消费点 = <c>CsFallDamage.DamageFor</c>。</summary>
        public const float DamageForFallSpeed = 100f / (FallFatalSpeed - FallSafeSpeed);

        // ---- 玩家 ----
        public const int MaxHealth = 100;
        public const int MaxArmor = 100;
        public const float EyeHeight = 1.62f;
        public const float StandHeight = 1.80f;

        /// <summary>
        /// 蹲位身高。原版包围盒：站立 <c>VEC_HULL_MIN -36 / VEC_HULL_MAX 36</c> = <b>72</b> unit，
        /// 蹲姿 <c>VEC_DUCK_HULL_MIN -18 / VEC_DUCK_HULL_MAX 32</c> = <b>50</b> unit
        /// （<c>原版资源/hlsdk/pm_shared/pm_shared.c:83-91</c>）。
        /// ⇒ 蹲位身高 = <c>StandHeight 1.80 × 50/72</c> = <b>1.25 m</b>。
        /// <para>⛔ 不要拿 <c>VEC_HULL_MAX 36</c> 当蹲高（那是站立盒的上限，不是蹲姿总高）——
        /// 那样会算出 0.9 m 并把蹲姿压得比原版矮 28%。</para>
        /// </summary>
        public const float CrouchHeight = 1.25f;

        public const float PlayerRadius = 0.36f;

        /// <summary>
        /// 手持武器**未覆盖** <c>GetMaxSpeed</c> 时（刀 / 手雷 / C4 / 装备）的默认移动速度 =
        /// 原版基类默认 <b>250</b> unit/s ⇒ ×0.0254 = <b>6.35 m/s</b>。
        /// 出处 = <c>策划/手感参数对照.md</c> §5.4「手枪系 / 刀 —— 250（未覆盖，继承基类默认）」。
        /// 逐武器的速度一律在 <c>CsWeaponDef.MaxSpeed</c> 上给，本常量只作兜底。
        /// </summary>
        public const float SpeedDefault = 6.35f;

        /// <summary>
        /// **视点晃动的满幅基准速度**（m/s）= <c>PlayerModule</c> 的 <c>FullAmplitudeSpeed</c>。
        ///
        /// <para>⛔ 这个 4.4 <b>不是任何一把枪的移动速度</b>（逐武器速度见 <c>CsWeaponDef.MaxSpeed</c>）；
        /// 移动散布（<c>Firearm.MoveFactor</c>）已改用**当前武器的实测基准**
        /// （<c>CsInventory.MoveReferenceSpeed</c>），不再读本常量。</para>
        ///
        /// <para>原版视点晃动是速度驱动的线性量（<c>bob = |simvel.xy| * cl_bob</c>），没有"基准速度"这种概念
        /// （<c>策划/手感参数对照.md</c> §4「视点晃动（bob）」列为"机制不同"）——
        /// 这里保留固定 4.4 属于既有实现（引擎件只吃配置），登记在差异册里待接原版的线性口径。</para>
        /// </summary>
        public const float SpeedRifle = 4.4f;
        // Shift 慢走 · 出处：**本项目新增**（原版慢走倍率的实现载体 —— GoldSrc 客户端与 `pm_shared` —— 现不在盘
        // ⇒ 给不出 file:line；对照表 N-17 是"**下蹲**速度倍率"、与本条无关，不许互相充数）。
        public const float SpeedWalkMultiplier = 0.42f;
        public const float SpeedCrouchMultiplier = 0.34f;
        /// <summary>
        /// 跳跃初速度：<c>sqrt(2 * 800 * 45.0)</c> = <b>268.328 units/s</b> ⇒ ×0.0254 = <b>6.82 m/s</b>。
        /// 出处 <c>原版资源/cs16src/hlsdk/halflife-master/pm_shared/pm_shared.c:2596</c>（同一算式亦见 <c>:2601</c>；
        /// 其中 800 = <c>sv_gravity</c>、45.0 = 跳跃高度上限 units）。
        /// </summary>
        public const float JumpSpeed = 6.82f;

        /// <summary>
        /// 重力：<c>sv_gravity 800</c>（units/s²）· 出处 <c>hw.dll.orig:0x189e90</c>
        /// （<c>sw.dll</c>/<c>swds.dll</c> 交叉验证同值；随包 <c>server.cfg:16</c> 也是 800）
        /// ⇒ ×0.0254 = <b>20.32 m/s²</b>。应用方式见 <c>CsMatch.StepActorPhysics</c>。
        /// </summary>
        public const float Gravity = 20.32f;
        public const float MaxFallSpeed = -30f;
        public const float StepUpHeight = 0.45f;          // 可迈上的台阶高度
        public const float GroundCheckDistance = 0.12f;

        // ---- 移动 cvar（地面摩擦 / 地面加速 / 空中加速 / 停速阈值）----
        //
        // 口径 = 随包 `server.cfg` 的覆盖值（= 玩家实际生效值）；引擎出厂值同时登记在
        // `策划/对照表.md` §5.2 的 N-12~N-14 行，⛔ 两套口径不混用。
        // 消费点 = `CsMovement.Solve`（本地输入 / 远端输入 / 机器人三条路共用）。

        /// <summary>地面摩擦 = <c>sv_friction 4</c> · 出处 <c>策划/对照表.md</c> N-12（随包 <c>server.cfg:33</c>）；
        /// 引擎出厂同为 4（<c>原版资源/cs16src/hw.dll</c> 的 cvar 默认串表，<c>sv_friction\0 4\0</c> 在 file 0x18a00c 附近）。</summary>
        public const float SvFriction = 4f;

        /// <summary>地面加速度 = <c>sv_accelerate 5</c> · 出处 <c>策划/对照表.md</c> N-13（随包 <c>server.cfg:30</c>）。
        /// ⚠️ 引擎出厂是 <b>10</b>（<c>hw.dll</c> cvar 默认串 <c>sv_accelerate\0 10\0</c>）——两套口径本就不同，本工程按随包值 5。</summary>
        public const float SvAccelerate = 5f;

        /// <summary>空中加速度 = <c>sv_airaccelerate 10</c> · 出处 <c>策划/对照表.md</c> N-14（随包 <c>server.cfg:31</c>）；
        /// 引擎出厂同为 10（<c>hw.dll</c> cvar 默认串）。</summary>
        public const float SvAirAccelerate = 10f;

        /// <summary>
        /// 停速阈值 = <c>sv_stopspeed</c> 引擎出厂默认 <b>100</b> unit/s ⇒ ×0.0254 = <b>2.54 m/s</b>。
        /// 出处 = <c>原版资源/cs16src/hw.dll</c> 的 cvar 默认串表（<c>sv_stopspeed\0</c> 后接默认串 <c>100\0</c>，file 0x18a01c 附近）。
        /// 语义见 <c>PM_Friction</c>：低于它时摩擦按它算（速度越小减速越"狠"，保证能停下来）。
        /// </summary>
        public const float SvStopSpeed = 2.54f;

        /// <summary>
        /// 全局速度上限 = <c>sv_maxspeed 320</c>（unit/s）⇒ ×0.0254 = <b>8.128 m/s</b> · 出处
        /// <c>策划/对照表.md</c> N-11 / 随包 <c>server.cfg:15</c>；引擎出厂同为 320
        /// （<c>hw.dll</c> cvar 默认串 <c>sv_maxspeed\0 320\0</c>，file 0x18b4a4 附近）。
        /// <para>用途 = <c>PM_WalkMove</c> 里"期望速度不得超过本档上限"的那次钳制。
        /// 当前所有武器档（≤260 unit/s）都低于它 ⇒ 实际不生效，但把机制补齐（原版有这一层）。</para>
        /// </summary>
        public const float SvMaxSpeed = 8.128f;

        /// <summary>
        /// **可站立地面的法线阈值 = 0.7**（对应坡度 <b>acos(0.7) ≈ 45.573°</b>）：地面法线在"上轴"上的分量
        /// 小于它 ⇒ 这个面**太陡**，不算地面（站不住、也不能沿它走上去）。
        ///
        /// <para><b>出处（原版 GoldSrc `pm_shared.c`，两处同阈值）</b>：</para>
        /// <list type="number">
        /// <item><c>PM_CatagorizePosition</c>：<c>if (tr.plane.normal[2] &lt; 0.7) pmove-&gt;onground = -1; // too steep</c>
        /// —— 陡坡上不算"在地面"（于是没有地面摩擦、不能再跳，走 AirMove）；</item>
        /// <item><c>PM_WalkMove</c>：<c>if (trace.plane.normal[2] &lt; 0.7) goto usedown;</c>
        /// —— 下探后发现是陡坡就**放弃"上台阶"那条路径**，退回贴地滑动结果（即不会沿陡坡"迈上去"）。</item>
        /// </list>
        ///
        /// <para><b>轴的口径</b>：原版那个 <c>normal[2]</c> 是"上轴"分量；本工程是 Unity 左手系、<b>y 向上</b>
        /// ⇒ 对应 <c>normal.y</c>（<c>Module/Map/CsMap.TrySampleGround</c> 返回的就是世界法线，取 <c>.y</c> 比较）。</para>
        ///
        /// <para>为什么必须有它：本工程的本地碰撞是"2D 位图（水平）+ 竖直射线（高度）"，位图**不带坡度信息**
        /// ⇒ 删掉这条阈值，玩家能直接沿任意陡坡（岩石面 / 楔形坡的侧面）"走上去"，脚贴坡面而身体与
        /// camera 陷进地形里 —— 用户报的"坡道会穿模"就是这个。</para>
        /// </summary>
        public const float MaxStandableSlopeNormalZ = 0.7f;

        // ---- 相机 ----

        /// <summary>
        /// 未开镜的 FOV = 原版 <c>default_fov</c> 默认 <c>"90"</c>，**口径是水平 FOV**。
        ///
        /// <para><b>出处</b>：① <c>原版资源/cs16src/hlsdk/halflife-master/cl_dll/hud.cpp:332</c>
        /// （<c>default_fov = CVAR_CREATE("default_fov","90",FCVAR_ARCHIVE)</c>，声明 <c>hud.h:584</c>）；
        /// ② 原版唯一的 fov 换算实现 <c>cl_dll/view.cpp:1737-1752</c> 的
        /// <c>CalcFov(fov_x,width,height)</c>：<c>x = width/tan(fov_x/360*π)</c>、<c>a = atan(height/x)</c>、
        /// <c>return a*360/π</c> ⇒ <c>fov_y = 2·atan((H/W)·tan(fov_x/2))</c> —— 即 <c>fov_x</c> 是**自变量（水平）**、
        /// <c>fov_y</c> 由视口宽高算出。</para>
        ///
        /// <para><b>不许把这个 90 直接赋给 Unity 的 <c>Camera.fieldOfView</c></b>：Unity 那个字段是**垂直** FOV。
        /// 这就是用户报的"相机鱼泡眼 / 胳膊太长"。正确做法 = 逐帧按当前宽高比换算，
        /// 见 <see cref="CloverEngine.CameraMath.FovYFromFovX"/>（引擎件）。</para>
        /// </summary>
        public const float DefaultFov = 90f;

        /// <summary>
        /// 开镜**第一档**的 FOV（水平口径，同 <see cref="DefaultFov"/>）。出处 = 原版 <c>mp.dll</c>
        /// 常量 <c>0x10142128</c>（file <c>0x140D28</c>）= <b>40</b>，四种狙击共用；读点在各自
        /// <c>fire</c> 函数区间内 —— 见 <c>策划/手感参数对照.md</c> §5.2「开镜 FOV（第一档 / 第二档）」行。
        /// </summary>
        public const float ZoomFov = 40f;

        /// <summary>开镜**第二档** FOV：<b>AWP = 10</b>。出处 = 原版 <c>mp.dll</c> 常量
        /// <c>0x10142090</c>（file <c>0x140C90</c>）；见 §5.2 同行。</summary>
        public const float ZoomFovSecondAwp = 10f;

        /// <summary>开镜**第二档** FOV：<b>Scout / SG550 / G3SG1 = 15</b>。出处 = 原版 <c>mp.dll</c> 常量
        /// <c>0x101420B8</c>（file <c>0x140CB8</c>）；见 §5.2 同行。</summary>
        public const float ZoomFovSecondOther = 15f;

        /// <summary>
        /// 开镜某一档的水平 FOV。<paramref name="level"/>：0 = 不开镜（返回 0，调用方自己用基础 FOV）、
        /// 1 = 第一档 40、2 = 第二档（AWP 10 / 其余 15）。
        ///
        /// <para>档位序列出处 = §5.2「<b>AWP 90 → 40 → 10</b>；Scout / SG550 / G3SG1 <b>90 → 40 → 15</b>」。
        /// ⚠️ 原版的**按键语义**（同一右键逐档推进 / 按住进阶 / 点击切换）本片未取证
        /// ⇒ 模拟侧只落"档位 + 可切换"，触发方式写在 <c>CsMatch.UpdateLocalPlayer</c> 的注释里。</para>
        /// </summary>
        public static float ScopeFovFor(CsWeaponDef def, int level)
        {
            if (level <= 0) return 0f;
            if (level == 1) return ZoomFov;
            return def != null && def.Id == CsWeapons.Awp ? ZoomFovSecondAwp : ZoomFovSecondOther;
        }

        /// <summary>
        /// 开镜时的鼠标灵敏度补偿比例 —— 原版 cvar <c>zoom_sensitivity_ratio</c> 的出厂默认值。
        /// 出处 = <c>原版资源/hlsdk/cl_dll/hud.cpp:103</c>（<c>CVAR_CREATE( "zoom_sensitivity_ratio", "1.2", 0 )</c>）。
        ///
        /// <para>它是**乘数**（原版用法见 <see cref="ScopeSensitivityScale"/>，那里还有 FOV 一项）：
        /// 值越大开镜后转得越快；默认 1.2 的含义是"比按视野等比缩放再快 20%"，不是"开镜后按 1/1.2 变慢"。</para>
        /// </summary>
        public const float ZoomSensitivityRatio = 1.2f;

        /// <summary>
        /// 开镜某一档的**鼠标灵敏度缩放系数**，乘到"每 count 多少度"上（<paramref name="level"/> = 0 ⇒ 返回 1，不改）。
        ///
        /// <para><b>出处</b> = <c>原版资源/hlsdk/cl_dll/hud.cpp:476</c>：
        /// <c>m_flMouseSensitivity = sensitivity-&gt;value * ((float)newfov / (float)def_fov) * CVAR_GET_FLOAT("zoom_sensitivity_ratio")</c>
        /// —— 开镜（<c>m_iFOV != def_fov</c>）时原版的有效灵敏度 = <b>玩家设置值 × (开镜视野 / 基础视野) × zoom_sensitivity_ratio</b>；
        /// 不开镜时该值归零（灵敏度回到玩家设置值，见同文件 :468-472）。</para>
        ///
        /// <para><b>方向</b>：开镜后**更慢**（系数 &lt; 1）。第一档 40°（基础 90°）⇒ 40/90 × 1.2 ≈ 0.533；
        /// AWP 第二档 10° ⇒ 10/90 × 1.2 ≈ 0.133；Scout / SG550 / G3SG1 第二档 15° ⇒ 15/90 × 1.2 = 0.2。
        /// <paramref name="baseFov"/> = 玩家当前的基础（未开镜）水平 FOV —— 原版即 cvar <c>default_fov</c>，默认 90 =
        /// <see cref="DefaultFov"/>（出处 <c>原版资源/hlsdk/cl_dll/hud.cpp:112</c>）；须 &gt; 0，调用方从玩家设置读入。</para>
        /// </summary>
        public static float ScopeSensitivityScale(CsWeaponDef def, int level, float baseFov)
        {
            if (level <= 0) return 1f;                                    // 不开镜：原版不覆盖灵敏度
            return ScopeFovFor(def, level) / baseFov * ZoomSensitivityRatio;
        }

        public const float DefaultSensitivity = 3.0f;
        public const float ViewBobAmount = 0.035f;
        public const float ViewBobSpeed = 9.5f;

        // ---- 第三人称（观战 / 跟随）机位 ----
        //
        // 口径：原版客户端确有第三人称机位，本工程按 HLSDK 里**能读出**的那套常量实现
        // （CS 1.6 自己的观察者走 cstrike/cl_dlls/client.dll，其机位常量未解出 ⇒ 对照表 D-04 [BLOCKED]）。
        // 单位换算：GoldSrc 1 unit = 0.0254 m（原版资源/cs16src/cs16_build.py:43 HL_UNIT）。

        /// <summary>
        /// 机位到目标眼位的距离 = <c>cl_chasedist "112"</c>（unit）· 出处
        /// <c>HLSDK/cl_dll/view.cpp:1724</c>（注册；使用点 <c>:1282</c>
        /// <c>V_GetChaseOrigin(angles, origin, cl_chasedist-&gt;value, origin)</c>）
        /// ⇒ 112 × 0.0254 = <b>2.8448 m</b>。
        /// </summary>
        public const float ChaseDistance = 2.8448f;

        /// <summary>
        /// 机位距离下限 = <c>#define CAM_MIN_DIST 30.0</c>（unit）· 出处
        /// <c>HLSDK/cl_dll/in_camera.cpp:31</c> ⇒ 30 × 0.0254 = <b>0.762 m</b>。
        /// </summary>
        public const float ChaseMinDistance = 0.762f;

        /// <summary>
        /// 射线收缩后，机位在"命中点沿墙法线外推"的偏移 = <c>VectorMA(trace-&gt;endpos, 4, trace-&gt;plane.normal, returnvec)</c>
        /// 里的 4（unit）· 出处 <c>HLSDK/cl_dll/view.cpp:957</c> ⇒ 4 × 0.0254 = <b>0.1016 m</b>。
        /// </summary>
        public const float ChaseWallOffset = 0.1016f;

        /// <summary>
        /// 距离收敛速率：每帧朝理想距离移 <b>1/4</b>（<c>cam_snapto 0</c> 时的平滑分支）· 出处
        /// <c>HLSDK/cl_dll/in_camera.cpp:386-389</c>：
        /// <c>camAngles[2] += (cam_idealdist - camAngles[2]) / 4.0</c>。
        /// 这不是"手感参数"，是原版的收敛公式。
        /// </summary>
        public const float ChaseDistanceLerp = 0.25f;

        /// <summary>
        /// 距离与理想值之差小于它就**直接吸附**（不再逐帧收敛）= 原版那两行的 <c>2.0</c>（unit）· 出处
        /// <c>HLSDK/cl_dll/in_camera.cpp:386</c>（<c>if(abs(camAngles[2] - cam_idealdist-&gt;value ) &lt; 2.0 )</c>）
        /// ⇒ 2.0 × 0.0254 = <b>0.0508 m</b>。
        /// </summary>
        public const float ChaseDistanceSnap = 0.0508f;

        // ---- 武器切换/动作时长 ----
        public const float SwitchTimePrimary = 0.6f;
        public const float SwitchTimePistol = 0.3f;
        public const float SwitchTimeKnife = 0.2f;
        public const float KnifeSwingTime = 0.4f;
        public const float KnifeDamage = 55f;
        public const float KnifeRange = 1.6f;

        // ---- 手雷 ----
        public const float GrenadeThrowForce = 12f;
        public const float GrenadeFuse = 3.0f;
        public const float GrenadeHeRadius = 5.5f;
        public const float GrenadeHeMaxDamage = 98f;
        public const float GrenadeFlashDuration = 3.5f;
        public const float GrenadeSmokeDuration = 12f;

        // ---- 机器人 ----
        public const int BotNamePoolSize = 20;
        public const float BotTickInterval = 0.1f;    // AI 决策频率 10Hz
        public const float BotVisionRange = 45f;
        public const float BotFovDegrees = 110f;
        public const float BotHearRadius = 18f;

        // ---- 表现 ----
        public const int MaxKillFeedEntries = 5;
        // 出处：原版命中标记的**形态** = `hud.txt:179` 的 `d_headshot` 36×16（见 `策划/对照表.md` U-09）；
        // **0.25s 这个显示时长本项目新增**（原版命中标记的时长在 `client.dll` 里、未解出 ⇒ 不给 file:line）。
        public const float HitMarkerTime = 0.25f;
        // 语义 = **受击方向指示器**（屏幕边缘那道红框，哪边挨枪亮哪边）的显示时长；
        // `CsDamageIndicatorWidget` 拿它做 alpha 归一（剩余时间/总时长 ⇒ 渐隐）。
        // 出处：**本项目新增**（原版这一排 HUD 的时长写在 `client.dll` 里、未解出 —— 见
        // `策划/对照表.md` 的 HUD BLOCKED 条目与 `策划/差异登记.tsv`）⇒ 取本工程自定值 0.8s，
        // 不给一个并不存在的 file:line。
        // 「A 没有 ⇒ 不加」整链下架（`UI/InGame/HudPanel.cs` 的 ObserveLocalDamage/ShowDamageNumber），
        // 于是本常量只剩"受击方向指示器时长"这一个语义 ⇒ 随之改名（含全部引用点）。
        public const float DamageIndicatorTime = 0.8f;

        // ---- 地图 / 场景 ----
        public const string MapDust2 = "de_dust2";
        // 资源路径（Resources 相对）真源 = Core/ResPaths.cs：MapDust2（位图 + 命名标记点段，同一份 .bytes）。
    }

    /// <summary>Unity 场景名（禁止散落字符串字面量）。</summary>
    public static class SceneNames
    {
        public const string Boot = "Boot";
        public const string Menu = "Menu";
        public const string StageDust2 = "StageDust2";
    }

    /// <summary>物理层（需与 ProjectSettings/TagManager 对齐，由 Editor 脚本保证）。
    /// 出处：`client/ProjectSettings/TagManager.asset` 的 `layers:` 列表 —— 下标 0 = Unity **内置** `Default` 层，
    /// 8 / 9 / 10 = 本工程新增的 `CsPlayer` / `CsBot` / `CsWorld`；**层名与这三个层索引都是本项目新增**
    /// （Unity 内置层 0~7 不可改，用户自定义层从 8 起）。</summary>
    public static class PhysicsLayers
    {
        public const int Default = 0;
        public const int Player = 8;
        public const int Bot = 9;
        public const int World = 10;
        public const string PlayerName = "CsPlayer";
        public const string BotName = "CsBot";
        public const string WorldName = "CsWorld";
    }
}
