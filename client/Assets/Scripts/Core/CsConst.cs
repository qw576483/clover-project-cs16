namespace Cs16.Core
{
    /// <summary>
    /// 全局玩法常量（数值真源）。改数值一律改这里，禁止在业务脚本里写裸数字。
    ///
    /// <para><b>口径（主 agent 已定，见 <c>策划/对照表.md</c> §5.2）</b>：一律取
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
        /// （另一口径：<c>mp.dll</c> 出厂默认 <b>6</b> · <c>mp.dll:0x11b9c0</c>，登记在对照表 §5.2 N-26）。
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
        public const float BombBeepIntervalSlow = 1.0f;   // 剩余 >10s
        public const float BombBeepIntervalFast = 0.25f;  // 剩余 <=10s
        public const float BombBeepFuseSlow = 1.2f;       // 剩余 >10s 的节拍
        public const float BombBeepFuseFast = 0.2f;

        // ---- 伤害 ----
        public const float HitHead = 4.0f;
        public const float HitChest = 1.0f;
        public const float HitStomach = 1.25f;
        public const float HitLeg = 0.75f;
        public const float ArmorAbsorbRatio = 0.5f;   // 有甲吸收
        public const float ArmorDamageRatio = 0.5f;   // 护甲损耗比例
        public const float DamageFalloffPerMeter = 0.01f; // 每米衰减

        // ---- 玩家 ----
        public const int MaxHealth = 100;
        public const int MaxArmor = 100;
        public const float EyeHeight = 1.62f;
        public const float StandHeight = 1.80f;
        public const float CrouchHeight = 1.25f;
        public const float PlayerRadius = 0.36f;
        public const float SpeedKnife = 5.4f;
        public const float SpeedPistol = 5.2f;
        public const float SpeedRifle = 4.4f;
        public const float SpeedAWP = 3.6f;
        public const float SpeedWalkMultiplier = 0.42f;   // Shift 慢走
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
        /// <para>⛔ <b>不许把这个 90 直接赋给 Unity 的 <c>Camera.fieldOfView</c></b>：Unity 那个字段是**垂直** FOV。
        /// 旧实现就是那么干的 ⇒ 16:9 下等效水平 = <c>2·atan(tan45°·16/9)</c> = <b>121.26°</b>（原版 90°），
        /// 这就是用户报的"相机鱼泡眼 / 胳膊太长"。正确做法 = 逐帧按当前宽高比换算，
        /// 见 <see cref="CloverEngine.CameraMath.FovYFromFovX"/>（E-core-18 已下沉为引擎件）。</para>
        /// </summary>
        public const float DefaultFov = 90f;

        /// <summary>
        /// 开镜（狙击镜）的 FOV，**同 <see cref="DefaultFov"/> 是水平口径**。
        ///
        /// <para>⛔ <b>这个 40 仍是无出处值</b>：CS 的开镜 FOV 由 CS 自己的
        /// <c>cstrike/cl_dlls/client.dll</c> 在开镜时下发，HLSDK 里**没有** CS 的 HUD/开镜实现
        /// （<c>cl_dll/</c> 只有 HL 的 <c>hud_*</c>）⇒ 见 <c>策划/对照表.md</c> 的 A-05 [BLOCKED]。
        /// 本片只把它的**口径**与 <see cref="DefaultFov"/> 统一（都当水平），数值本身不动、
        /// 仍按"无出处"登记在验收表的「允许的差异」里。</para>
        /// </summary>
        public const float ZoomFov = 40f;

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
        /// ⛔ 这不是"手感参数"，是原版的收敛公式。
        /// </summary>
        public const float ChaseDistanceLerp = 0.25f;

        /// <summary>
        /// 距离与理想值之差小于它就**直接吸附**（不再逐帧收敛）= 原版那两行的 <c>2.0</c>（unit）· 出处
        /// <c>HLSDK/cl_dll/in_camera.cpp:386</c>（<c>if( abs( camAngles[2] - cam_idealdist-&gt;value ) &lt; 2.0 )</c>）
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
        public const float HitMarkerTime = 0.25f;
        public const float DamageNumberTime = 0.8f;

        // ---- 地图 / 场景 ----
        public const string MapDust2 = "de_dust2";
        // 资源路径（Resources 相对）真源 = Core/ResPaths.cs：MapDust2（位图）/ MapDust2Markers（标记表）。
    }

    /// <summary>Unity 场景名（禁止散落字符串字面量）。</summary>
    public static class SceneNames
    {
        public const string Boot = "Boot";
        public const string Menu = "Menu";
        public const string StageDust2 = "StageDust2";
    }

    /// <summary>物理层（需与 ProjectSettings/TagManager 对齐，由 Editor 脚本保证）。</summary>
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
