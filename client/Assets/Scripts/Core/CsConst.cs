using System;
using System.Collections.Generic;
using UnityEngine;

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
        /// <summary>下包耗时（秒）。出处 = 原版 <c>CC4::PrimaryAttack</c> 的
        /// <c>m_fArmedTime = gpGlobals->time + C4_ARMING_ON_TIME</c>（进度条时长同用该常量），
        /// 见 <c>regamedll/dlls/wpn_shared/wpn_c4.cpp:143</c> 与 <c>:156</c>（<c>SetProgressBarTime</c> 同参）；
        /// <c>const float C4_ARMING_ON_TIME = 3.0f;</c> 见 <c>regamedll/dlls/weapons.h:860</c>。</summary>
        public const float PlantTime = 3f;

        /// <summary>拆包耗时（秒）· 无拆弹器。出处 = 原版 <c>mp.dll</c> VA <c>0x10043D1B</c>
        /// <c>push 10</c> + VA <c>0x10043D1F</c> <c>fadd [0x10142090]</c>（常量 <c>10.0f</c>），
        /// 字符串锚点 <c>Begin_Bomb_Defuse_Without_Kit</c>（<c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §3.1）。</summary>
        public const float DefuseTime = 10f;

        /// <summary>拆包耗时（秒）· 有拆弹器。出处 = 原版 <c>mp.dll</c> VA <c>0x10043CA8</c>
        /// <c>push 5</c> + VA <c>0x10043CAC</c> <c>fadd [0x10142054]</c>（常量 <c>5.0f</c>），
        /// 字符串锚点 <c>Begin_Bomb_Defuse_With_Kit</c>（<c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §3.1）。</summary>
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

        /// <summary>首响用的间隔（秒）= C4 倒计时 ÷ 4。出处 = 原版安放炸弹处
        /// <c>pGrenade->m_flNextFreqInterval = float(CSGameRules()->m_iC4Timer / 4);</c>
        /// （<c>regamedll/dlls/ggrenade.cpp</c> 的 <c>CGrenade::C4Think</c> 建立段），
        /// 与 <c>m_flNextFreq = gpGlobals->time</c> 一起决定首响时刻；之后每响一次
        /// 间隔 ×<see cref="BombBeepIntervalDecay"/>（= 原版的 ×0.9）。</summary>
        public const float BombBeepIntervalInitial = BombTimer / 4f;

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
        // 护甲两系数同源（regamedll/dlls/player.h:66-67）：
        //   const float ARMOR_RATIO = 0.5f;   // Armor Takes 50% of the damage
        //   const float ARMOR_BONUS = 0.5f;   // Each Point of Armor is work 1/x points of health
        // 原版 TakeDamage 里 flRatio = ARMOR_RATIO、flBonus = ARMOR_BONUS，护甲块为
        //   flNew = flRatio * flDamage; flArmor = (flDamage - flNew) * flBonus;
        // （regamedll/dlls/player.cpp:912-913 取系数、:992-993 落护甲块，CBasePlayer::TakeDamage；
        //  本机 mp.dll 同段为 VA 0x10064075-fm。
        //  门控 = armorvalue≠0 且 !(bitsDamageType & (DMG_FALL|DMG_DROWN))，逐字节见 VALUES.md §8）。
        /// <summary>原版 <c>ARMOR_RATIO</c>（0.5）：护甲吸收后**留下**的伤害比例（吸收掉的是 <c>1 − 它</c>）。
        /// 逐武器还会再乘一个比率（原版 <c>TakeDamage</c> 的 <c>switch (iGunType)</c>，见
        /// <c>CsWeaponDef.ArmorRatioScale</c>）。</summary>
        public const float ArmorAbsorbRatio = 0.5f;
        /// <summary>护甲损耗比例 = 原版 <c>ARMOR_BONUS</c>（0.5）：每 1 点护甲折 2 点伤害。</summary>
        public const float ArmorDamageRatio = 0.5f;
        // ---- 距离衰减的分母（公式 pow(rangeModifier, dist_in_units / 分母)）----
        //
        // 分母是**固定 500、与弹种无关**：出处 = 原版 FireBullets3 的
        //   iCurrentDamage *= Q_pow(flRangeModifier, flCurrentDistance / 500);
        // （公开源 regamedll/dlls/cbase.cpp 的 CBaseEntity::FireBullets3；本机载体 mp.dll 为同一构建，
        //  其子弹模拟函数 VA 0x10025640 同位置的分母即该常量）。
        // 注意区分：同一张按弹种的表里的那几个 float（9MM 800 / 45ACP 500 / 556MM 4000 / 762MM 5000 /
        // 338MAG 8000 / 50AE 1000 / 57MM 2000 / 357SIG 800）是**穿透距离** flPenetrationDistance
        // （与 iPenetrationPower 21/15/35/39/45/30/30/25 同表，命中距离超过它即停止继续穿透），
        // 不是衰减分母。
        //
        // 消费点 = CsWeapons.DistanceFalloff；CsWeaponDef.FalloffDenominator 携带本值。
        public const float DamageFalloffDenominator = 500f;

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

        /// <summary>
        /// 可迈上的台阶高度 = 引擎 cvar <c>sv_stepsize</c> 的默认值 <b>18</b> unit
        /// ⇒ <c>18 × 0.0254</c> = <b>0.4572 m</b>。
        ///
        /// <para>出处 = 原版 <c>原版资源/cs16src/hw.dll</c> 的 cvar 默认串表：file <c>0x18A058</c>
        /// 逐字是 <c>sv_stepsize\0 18\0</c>。原版消费点 = <c>pmove-&gt;movevars-&gt;stepsize</c>
        /// （<c>原版资源/hlsdk/pm_shared/pm_shared.c:1024</c> 上台阶下探 / <c>:1034</c> 复位 /
        /// <c>:1211</c> 起跳上探）。</para>
        ///
        /// <para>本工程消费点 = <c>CsMap.GroundWithinStep</c> / <c>CsMap.TryStepUp</c> /
        /// <c>CsMatch.StepActorPhysics</c> 的陡坡闸门与"抬一个台阶再探"分支。</para>
        /// </summary>
        public const float StepUpHeight = 18f * UnitToMeter;

        public const float GroundCheckDistance = 0.12f;

        // ---- 移动 cvar（地面摩擦 / 地面加速 / 空中加速 / 停速阈值）----
        //
        // 口径 = 随包 `server.cfg` 的覆盖值（= 玩家实际生效值）；引擎出厂值同时登记在
        // `策划/对照表.md` §5.2 的 N-12~N-14 行，⛔ 两套口径不混用。
        // 消费点 = `CsMovement.Solve`（本地输入 / 远端输入 / 机器人三条路共用）。

        /// <summary>地面摩擦 = <c>sv_friction 4</c> · 出处 <c>策划/对照表.md</c> N-12（随包 <c>server.cfg:33</c>）；
        /// 引擎出厂同为 4（<c>原版资源/cs16src/hw.dll</c> 的 cvar 默认串表，<c>sv_friction\0 4\0</c> 在 file 0x18a00c 附近）。</summary>
        public const float SvFriction = 4f;

        /// <summary>
        /// 边缘摩擦倍率 = 引擎 cvar <c>edgefriction</c> 的默认值 <b>2</b>。出处 = 原版
        /// <c>原版资源/cs16src/hw.dll</c> 的 cvar 默认串表：file <c>0x18B490</c> 逐字是
        /// <c>edgefriction\0 2\0</c>。
        ///
        /// <para>语义（机制形状出处 <c>原版资源/hlsdk/pm_shared/pm_shared.c:1079-1100</c>）：地面摩擦时，
        /// 从脚面沿**水平速度方向**前 <c>16</c> unit 处向下探 <c>34</c> unit，探不到任何面
        /// （<c>trace.fraction == 1.0</c>，= 前方是断崖/边缘）⇒ 本帧摩擦 <c>= sv_friction × 本值</c>；
        /// 探到面 ⇒ 只用 <c>sv_friction</c>。</para>
        ///
        /// <para>消费点 = <c>CsMovement.Friction</c>（探测本身在 <c>CsMatch.GroundAheadOf</c>）。</para>
        /// </summary>
        public const float SvEdgeFriction = 2f;

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

        // ---- 观战：自由飞行（原版 OBS_ROAMING = 档位 3）----
        //
        // 机位语义 = **观战者自己**的漫游原点 + 他自己的鼠标角，出处 `view.cpp:1538-1540`
        //   （`VectorCopy (v_cl_angles, v_angles); VectorCopy (v_sim_org, v_origin);`）；
        // 漫游原点的推进 = 共享代码 `PM_SpectatorMove`（`pm_shared.c:1490-1572`）——
        //   服务端与 HLTV 客户端走的是同一个函数，本工程没有服务端，故在客户端按同一套式解算。
        // ⛔ 该分支**没有重力 / 没有碰撞 / 没有边界钳制**：`pm_shared.c:2456-2461` 进了
        //   `PM_SpectatorMove` 就 `return`（不进 `PM_AddGravity` / `PM_FixupGravityVelocity`、
        //   也不做任何 trace），位置唯一的写点是 `:1571` 的
        //   `VectorMA(origin, frametime, velocity, origin)`。

        /// <summary>
        /// 自由飞行的速度上限 = 原版 cvar <c>sv_spectatormaxspeed</c> 的默认 <b>500</b>（unit/s）· 出处
        /// <c>原版资源/cs16src/hw.dll</c> file <c>0x18A098</c>（名串）/ file <c>0x18A0AF</c>（默认串 <c>500</c>）
        /// ⇒ 500 × <see cref="UnitToMeter"/> = <b>12.7 m/s</b>。
        /// 消费点 = <c>CsSpectatorRoam.Step</c>（对应原版 <c>pm_shared.c:1551-1555</c> 的那次钳制）。
        /// </summary>
        public const float SpecRoamMaxSpeed = 500f * UnitToMeter;

        /// <summary>
        /// 自由飞行的**水平指令幅值** = 原版 cvar <c>cl_forwardspeed</c> 的默认 <b>400</b>（unit/s）· 出处
        /// <c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c> file <c>0xE9E40</c>（名串）/
        /// file <c>0xE9E50</c>（默认串 <c>400</c>）⇒ 400 × <see cref="UnitToMeter"/> = <b>10.16 m/s</b>。
        ///
        /// <para><b>为什么前进 / 后退 / 侧移共用这一个幅值</b>：原版侧移与后退的幅值取自同族的
        /// <c>cl_sidespeed</c> / <c>cl_backspeed</c>，而这两条 cvar 在同一个 client.dll 里的默认串是
        /// <b>空串</b>（file <c>0xE9E20</c> / <c>0xE9E30</c>；整份二进制里 <c>"400"</c> 字面量只出现一次，
        /// 就在 <c>cl_forwardspeed</c> 的默认值处），随包 <c>config.cfg</c> 又不在盘 ⇒ 盘上给不出这两个
        /// 幅值的第二口径。本工程不另立数值，三条水平轴共用已取到的那一条 <c>cl_forwardspeed</c>
        /// （登记在 <c>策划/差异登记.tsv</c>）。</para>
        ///
        /// <para>消费点 = <c>CsSpectatorRoam.Step</c> 的 <c>forwardMove</c> / <c>sideMove</c>
        /// （对应原版 <c>pm_shared.c:1537-1544</c> 的 <c>cmd-&gt;forwardmove</c> / <c>cmd-&gt;sidemove</c>）。</para>
        /// </summary>
        public const float SpecRoamMoveSpeed = 400f * UnitToMeter;

        /// <summary>
        /// 自由飞行的摩擦倍率 = <b>1.5 ×</b> 地面摩擦 · 出处 <c>pm_shared.c:1523</c>
        /// （<c>friction = pmove-&gt;movevars-&gt;friction * 1.5;</c>，其中的 1.5 就是本常量）。
        /// 摩擦本体取 <see cref="SvFriction"/>（<c>sv_friction 4</c>）。
        /// </summary>
        public const float SpecRoamFrictionScale = 1.5f;

        /// <summary>
        /// 自由飞行的**速度死区** = 原版 <c>if (speed &lt; 1)</c> 里的那个 1（unit/s）· 出处
        /// <c>pm_shared.c:1515-1518</c>（低于它就 <c>VectorCopy(vec3_origin, pmove-&gt;velocity)</c>，
        /// 免得摩擦只剩无限逼近 0 的残速）⇒ 1 × <see cref="UnitToMeter"/> = <b>0.0254 m/s</b>。
        /// </summary>
        public const float SpecRoamDeadbandSpeed = 1f * UnitToMeter;

        // ---- 武器切换/动作时长 ----
        public const float SwitchTimePrimary = 0.6f;
        public const float SwitchTimePistol = 0.3f;
        public const float SwitchTimeKnife = 0.2f;
        public const float KnifeSwingTime = 0.4f;
        public const float KnifeDamage = 55f;
        public const float KnifeRange = 1.6f;

        // ---- 手雷 ----

        /// <summary>
        /// 投掷初速的**俯仰系数**（unit/s 每度）· 出处 <c>原版资源/cs16src/cstrike/dlls/mp.dll</c>
        /// 的 <c>.rdata</c> VA <c>0x10142030</c> = <c>4.0f</c> —— 与 VA <c>0x10142184</c> = <c>90.0f</c>
        /// 同属原版 <c>CBasePlayer::ThrowGrenade</c> 的常量族（该函数口径 = <c>flVel = (90 - angThrow.x) * 4</c>）。
        /// </summary>
        public const float GrenadeThrowPitchScale = 4f;

        /// <summary>
        /// 投掷初速的**上限**（unit/s）· 出处同上 <c>.rdata</c> VA <c>0x10142268</c> = <c>750.0f</c>
        /// （原版 <c>if (flVel &gt; 750) flVel = 750;</c>）。
        /// </summary>
        public const float GrenadeThrowSpeedMax = 750f;

        /// <summary>
        /// 手雷投掷初速（m/s）= <c>min((90 + pitchDeg) × 4, 750) × UnitToMeter</c>。
        ///
        /// <para><b>为什么是俯仰的函数</b>：原版初速 = <c>(90 - angThrow.x) × 4</c> unit/s，
        /// 其中 <c>angThrow.x</c> 就是**瞄向俯仰**（原版正方向朝下）⇒ 抬头扔得更远。本工程
        /// <c>CsActor.Pitch</c> 正方向**朝上** ⇒ 原式的 <c>(90 - angThrow.x)</c> 在这里写作
        /// <c>(90 + pitchDeg)</c>。平视 = 360 unit/s = 9.14 m/s；抬头 89° = 716 unit/s = 18.19 m/s。</para>
        ///
        /// <para><b>未取到的部分</b>（⛔ 本工程不实现、不猜）：原版在取角度前对 <c>angThrow.x</c> 还有一次
        /// 按朝向缩放的钳制（同族常量 <c>10.0f</c> VA <c>0x10142090</c> / <c>-10.0f</c> VA <c>0x101423D0</c> /
        /// <c>16.0f</c> VA <c>0x101420BC</c> 疑属它），该步在 mp.dll 里的消费点**未逐字节确认**。
        /// 因此 750 的上限在本工程 ±89° 的可用俯仰范围内不会真正触发，仍照原版保留。</para>
        /// </summary>
        public static float GrenadeThrowSpeed(float pitchDeg)
        {
            var unitPerSec = (90f + pitchDeg) * GrenadeThrowPitchScale;
            if (unitPerSec > GrenadeThrowSpeedMax) unitPerSec = GrenadeThrowSpeedMax;
            return unitPerSec * UnitToMeter;
        }
        /// <summary>手雷引信（秒）：从脱手到爆炸。出处 = 原版三种手雷的投掷调用
        /// <c>m_pPlayer->ThrowGrenade(this, vecSrc, vecThrow, 1.5, m_usCreateExplosion)</c> ——
        /// HE / 闪光 / 烟雾的第 4 实参同为 <b>1.5</b>（公开源 <c>regamedll/dlls/wpn_shared/wpn_hegrenade.cpp:201</c>、
        /// <c>wpn_flashbang.cpp:199</c>、<c>wpn_smokegrenade.cpp:202</c>；本机 <c>mp.dll</c> 为同一构建）。</summary>
        public const float GrenadeFuse = 1.5f;

        /// <summary>HE 爆炸的最大伤害（爆心、无遮挡）。出处 = 原版 HE 投掷工厂
        /// <c>CGrenade::ShootTimed2</c> 里的 <c>pGrenade->pev->dmg = 100.0f;</c>
        /// （<c>regamedll/dlls/ggrenade.cpp:965</c> 公开源）。</summary>
        public const float GrenadeHeMaxDamage = 100f;

        /// <summary>HE 爆炸的作用半径（米）= 原版 <b>350 GoldSrc 单位</b> × <see cref="UnitToMeter"/>。
        /// 半径由 <c>CBaseMonster::RadiusDamage</c> 在 <c>flDamage &gt; 80</c> 时取 <c>flRadius = flDamage * 3.5</c>
        /// （<c>regamedll/dlls/basemonster.cpp:555-556</c>；HE 的 <c>pev->dmg = 100.0f</c> ⇒ 100 × 3.5 = <b>350</b>；
        /// <c>CGrenade : public CBaseMonster</c> 见 <c>regamedll/dlls/weapons.h:171</c>）。衰减形状 = 原版
        /// <c>RadiusDamage</c> 的线性式（<c>flAdjustedDamage = flDamage - length * (flDamage / flRadius)</c>，
        /// <c>regamedll/dlls/combat.cpp:243-335</c>）。同一 350 落在本机载体 <c>mp.dll</c> 的常量：
        /// VA <c>0x10066976</c> 在 <c>sqrt(Δ²)</c> 之后做
        /// <c>350 - dist</c>（<c>350.0f</c> 的字节见 <c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §5）。</summary>
        public const float GrenadeHeRadius = 350f * UnitToMeter;

        /// <summary>闪光弹**最近距离、正对**时的致盲时长（秒）。出处 = 原版
        /// <c>regamedll/dlls/combat.cpp</c> 的 <c>RadiusFlash</c> / <c>PlayerBlind</c>：
        /// <c>flDamage</c> 恒为 <b>4</b>（<c>ggrenade.cpp:63</c> 的
        /// <c>RadiusFlash(pev->origin, pev, pevOwner, 4, CLASS_NONE, bitsDamageType)</c>），
        /// 正对分支取 <c>fadeTime = flAdjustedDamage * 3</c>（<c>combat.cpp:144</c>；近距 ⇒ <c>flAdjustedDamage ≈ 4</c>）、
        /// 再 <c>flDurationTime = fadeTime * 0.33</c>（<c>combat.cpp:23</c>）⇒ 4 × 3 × 0.33 ≈ 3.96 s。</summary>
        public const float GrenadeFlashDuration = 4f;

        /// <summary>烟球存活时长（秒）：原版服务端只按事件推进烟雾贴片（<c>TE_SMOKE</c> 逐档实参见
        /// <c>regamedll/dlls/ggrenade.cpp</c>：<c>CGrenade::Smoke2</c>（<c>:463</c> 字节 150 = scale 15.0、
        /// <c>:464</c> framerate 8）、<c>CGrenade::Smoke</c>（<c>:485</c> 字节 25 = 2.5、<c>:486</c> framerate 6）、
        /// <c>CGrenade::Smoke3_A</c>（<c>:443</c> 字节 15+rand(0,10)、<c>:444</c> framerate 12）、
        /// <c>CGrenade::Smoke3_B</c>（<c>:420</c> 同 scale、<c>:421</c> framerate 10）、
        /// <c>CGrenade::Smoke3_C</c>（<c>:398</c> 字节 35+rand(0,10)、<c>:399</c> framerate 5）），
        /// 烟球本体体积由**客户端事件脚本**决定 ⇒ 属客户端表现层差异，本工程按 12 s 落地。</summary>
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

    /// <summary>
    /// 观战**主模式**档位（= 原版 <c>g_iUser1</c> 的取值）。原版的符号名
    /// <c>OBS_CHASE_LOCKED … OBS_MAP_CHASE</c> 定义在 <c>hltv.h</c>，**该头文件本机不在盘**
    /// ⇒ 数值由三处载体交叉推得，逐条可查：
    /// <list type="number">
    /// <item><c>原版资源/hlsdk/cl_dll/hud_spectator.cpp:876-877</c> 把档位号直接拼进本地化词条
    /// （<c>sprintf(string, "#Spec_Mode%d", g_iUser1)</c>）⇒ **档位号 = 词条号**；</item>
    /// <item><c>原版资源/cs16src/cstrike/cstrike__resource__cstrike_english.txt:941-946</c> 给出六个名字
    /// （<c>Spec_Mode1</c> "Locked Chase Cam" / <c>2</c> "Free Chase Cam" / <c>3</c> "Free Look" /
    /// <c>4</c> "First Person" / <c>5</c> "Free Overview" / <c>6</c> "Chase Overview"）；</item>
    /// <item><c>hud_spectator.cpp:715-733</c>（JUMP 键的循环）与 <c>:799-803</c>（<c>spec_mode</c> 的范围校验
    /// <c>&lt; OBS_CHASE_LOCKED || &gt; OBS_MAP_CHASE</c>）给出符号之间的先后与上下界。</item>
    /// </list>
    /// 三者合起来唯一确定"名字 ↔ 符号 ↔ 数值"：例如 <c>Spec_Mode4</c> 是 "First Person"、而循环里
    /// <c>OBS_IN_EYE</c> 的下一个是 <c>OBS_ROAMING</c>（"Free Look"），故 <c>OBS_IN_EYE = 4</c>、
    /// <c>OBS_ROAMING = 3</c>。
    /// </summary>
    public enum CsSpectatorMode
    {
        /// <summary>1 = "Locked Chase Cam"（原版 <c>OBS_CHASE_LOCKED</c>，锁定追尾：相机角度锁到目标的视角）。</summary>
        ChaseLocked = 1,
        /// <summary>2 = "Free Chase Cam"（原版 <c>OBS_CHASE_FREE</c>，自由追尾：相机角度用观察者自己的视角）。</summary>
        ChaseFree = 2,
        /// <summary>3 = "Free Look"（原版 <c>OBS_ROAMING</c>，自由飞行）。</summary>
        FreeLook = 3,
        /// <summary>4 = "First Person"（原版 <c>OBS_IN_EYE</c>，目标第一人称）。</summary>
        FirstPerson = 4,
        /// <summary>5 = "Free Overview"（原版 <c>OBS_MAP_FREE</c>，自由俯瞰）。</summary>
        FreeOverview = 5,
        /// <summary>6 = "Chase Overview"（原版 <c>OBS_MAP_CHASE</c>，跟随俯瞰）。</summary>
        ChaseOverview = 6,
    }

    /// <summary>
    /// 观战**控制面**（档位循环 / 自动导演标记 / 目标定位）的纯逻辑与状态。
    ///
    /// <para><b>为什么在 <c>Core</c></b>：写入者有 <c>UI/InGame/ConsolePanel</c>（控制台命令）与
    /// <c>Module/View/ViewModule</c>（换目标、开局复位），读取者有 <c>UI/InGame/CsSpectatorWidget</c>
    /// （显示档位名）—— 三者共有的层只有 <c>Cs16.Core</c>（UI 不许引用 <c>Cs16.Module.*</c>）。</para>
    ///
    /// <para><b>不吃机位</b>：真正的机位（锁定追尾 / 自由追尾 / 目标第一人称 / 自由飞行）由
    /// <c>Module/CameraRig/FirstPersonCamera</c> 独占写，本类只承载档位状态与"哪几档有机位"的判据
    /// （<see cref="IsImplemented"/>，集合见 <see cref="ImplementedModes"/>），其余档位不可切
    /// （见 <see cref="TrySetMode"/>）。自由飞行的位置解算是**纯函数**，放在
    /// <see cref="CsSpectatorRoam"/> 里（它也不碰相机，只算位置与速度）。</para>
    /// </summary>
    public static class CsSpectator
    {
        /// <summary>当前观战档位（默认见 <see cref="DefaultMode"/>）。</summary>
        public static CsSpectatorMode Mode = DefaultMode;

        /// <summary>
        /// 自动导演标记 = 原版 cvar <c>spec_autodirector</c>（命令名与内部 cvar 名同在客户端
        /// <c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>：<c>spec_autodirector</c> @ file <c>0xe9664</c>、
        /// <c>spec_autodirector_internal</c> @ file <c>0xe95e4</c>）。
        ///
        /// <para><b>默认值 = 关</b>，出处 <c>hud_spectator.cpp:1610-1613</c>：HUD 复位时
        /// <c>if (IsSpectateOnly() || demo playback) m_autoDirector-&gt;value = 1; else m_autoDirector-&gt;value = 0;</c>
        /// —— 本工程的观战路径是"本地玩家阵亡后观战"，属 <c>else</c> 分支（非 HLTV 代理、非录像回放）⇒ <b>0</b>。
        /// （另一口径：同文件 <c>:160</c> 注册时给的默认串是 <c>"1"</c>；那一条只对 HLTV 生效，两套口径不混用。）</para>
        ///
        /// <para>本工程没有导演流（原版由代理的 <c>DRC_CMD_*</c> 消息驱动，见 <c>hud_spectator.cpp:464-600</c>）
        /// ⇒ 置开后只记录状态，不改变机位。</para>
        /// </summary>
        public static bool AutoDirector;

        /// <summary>
        /// 默认档位 = <see cref="CsSpectatorMode.ChaseFree"/>，出处 <c>hud_spectator.cpp:1617</c>：
        /// <c>SetModes( OBS_CHASE_FREE, INSET_OFF );</c>（HUD 复位时进入的档位）。
        /// </summary>
        public static CsSpectatorMode DefaultMode => CsSpectatorMode.ChaseFree;

        /// <summary>一局开始时复位（同原版 HUD 复位：档位回默认、自动导演回 0）。</summary>
        public static void Reset()
        {
            Mode = DefaultMode;
            AutoDirector = false;
        }

        /// <summary>
        /// **本工程有真机位**的档位集合（判据见 <see cref="IsImplemented"/>）；<c>spec_mode</c> 只认这四档。
        /// 顺序 = 原版 JUMP 键循环序（<c>hud_spectator.cpp:715-733</c>）扣掉未实现档后的样子
        /// —— 原版那条循环自档位 1 起 <c>1 → 2 → 4 → 3 → 5 → 6 → 2</c>，本工程实现了其中的 1 / 2 / 4 / 3。
        ///
        /// <para>⛔ 本数组**不是** <see cref="NextMode"/> 的循环：循环只有 <c>2 → 4 → 3 → 2</c>
        /// 三档，档位 1 不在循环里（原版循环自己也跳过它，见 <see cref="NextMode"/>）——
        /// 档位 1 只能由 <c>spec_mode 1</c> 进入。</para>
        /// </summary>
        public static readonly CsSpectatorMode[] ImplementedModes =
        {
            CsSpectatorMode.ChaseLocked,   // 1
            CsSpectatorMode.ChaseFree,     // 2
            CsSpectatorMode.FirstPerson,   // 4
            CsSpectatorMode.FreeLook,      // 3
        };

        /// <summary>
        /// 该档位在本工程是否有**真机位**（不是只有名字）。
        /// <list type="bullet">
        /// <item><see cref="CsSpectatorMode.ChaseLocked"/>（原版 <c>OBS_CHASE_LOCKED</c>）= 锁定追尾：
        /// 位置算法与档位 2 **完全共用**（同一个 <c>V_GetChaseOrigin</c>，<c>HLSDK/cl_dll/view.cpp:894-1050</c>），
        /// 差异只在相机角度**锁到目标的视角** —— 原版给 <c>V_GetChasePos</c> 传的鼠标角是 <c>NULL</c>
        /// （<c>view.cpp:1532</c>），那里判 <c>cl_angles == NULL</c> 就取 <c>ent-&gt;angles</c>
        /// （<c>view.cpp:1264-1268</c>）。角度来源的纯判据 = <see cref="TryChaseCameraAngles"/>；</item>
        /// <item><see cref="CsSpectatorMode.ChaseFree"/>（原版 <c>OBS_CHASE_FREE</c>）= 第三人称追尾：
        /// 机位常量 <c>CsConst.ChaseDistance</c> / <c>ChaseMinDistance</c> / <c>ChaseWallOffset</c> /
        /// <c>ChaseDistanceLerp</c> / <c>ChaseDistanceSnap</c>，出处 <c>HLSDK/cl_dll/view.cpp:894-1050</c>
        /// （<c>V_GetChaseOrigin</c>）/ <c>HLSDK/cl_dll/in_camera.cpp:386-389</c>；角度用观察者自己的视角
        /// （原版传 <c>v_cl_angles</c>，<c>view.cpp:1535</c> + <c>:1269-1270</c>）；</item>
        /// <item><see cref="CsSpectatorMode.FirstPerson"/>（原版 <c>OBS_IN_EYE</c>）= 目标眼位 + 目标朝向：
        /// 出处 <c>HLSDK/cl_dll/view.cpp:1289-1321</c>（<c>V_GetInEyePos</c>：<c>origin = ent-&gt;origin</c>、
        /// <c>angles = ent-&gt;angles</c>，站立 <c>origin[2] += 28</c>、蹲姿 <c>+= 12</c>）。眼位高**直接复用
        /// 模拟自己的 <c>CsActor.EyePosition</c>**（<c>Module/Match/CsTypes.cs:28/32</c>），⛔ 不新增任何眼高常量。</item>
        /// <item><see cref="CsSpectatorMode.FreeLook"/>（原版 <c>OBS_ROAMING</c>）= 观察者**自己**的漫游机位：
        /// 机位 = 他自己的漫游原点 + 他自己的鼠标角（<c>HLSDK/cl_dll/view.cpp:1538-1540</c>），原点推进 =
        /// 共享代码 <c>PM_SpectatorMove</c>（<c>HLSDK/pm_shared/pm_shared.c:1490-1572</c>；数值取自
        /// <see cref="CsConst"/> 的 <c>SpecRoam*</c> 常量）。</item>
        /// </list>
        ///
        /// <para><b>其余两档（5 / 6）在本工程没有机位实现</b>：
        /// <list type="number">
        /// <item>5 / 6 = 原版 <c>OBS_MAP_FREE</c> / <c>OBS_MAP_CHASE</c>：主画面**不是 3D 机位** ——
        /// <c>view.cpp:1545-1551</c> 把 <c>pparams-&gt;onlyClientDraw</c> 置真，而该字段的契约是
        /// "引擎除 clientDraw 外什么都不画"（<c>common/ref_params.h:70</c>），画面上看到的是客户端自己画的
        /// <b>2D 概览图</b>（<c>hud_spectator.cpp:395-416</c> 的 <c>m_mapZoom</c> / <c>m_mapOrigin</c>，
        /// 取值来自 <c>overviews/de_dust2.txt</c> 的 <c>ZOOM 1.50</c> / <c>ORIGIN -223 1097 -192</c>）；
        /// <c>V_GetMap*Position</c> 算出的 origin/angles 只用来把玩家投影到那张图上
        /// （<c>hud_spectator.cpp:1305-1328</c> 的 <c>WorldToScreen</c>）。本工程没有 2D 概览子系统
        /// ⇒ 这两档保持不可切（登记 <c>策划/差异登记.tsv</c>）。</item>
        /// </list>
        /// 这两档**不可切**：<see cref="TrySetMode"/> 拒绝、<see cref="NextMode"/> 不经过，因此画面上
        /// 显示的档位名永远等于真正生效的那个机位。</para>
        /// </summary>
        public static bool IsImplemented(CsSpectatorMode mode)
        {
            return mode == CsSpectatorMode.ChaseLocked
                || mode == CsSpectatorMode.ChaseFree
                || mode == CsSpectatorMode.FirstPerson
                || mode == CsSpectatorMode.FreeLook;
        }

        /// <summary>
        /// 档位循环：按原版 JUMP 键的循环序走 <c>2 → 4 → 3 → 2</c>（判据 <see cref="IsImplemented"/>）。
        ///
        /// <para>原版 JUMP 键的循环序是 <c>1 → 2 → 4 → 3 → 5 → 6 → 2 …</c>
        /// （<c>hud_spectator.cpp:715-733</c>，逐档一一对应；末档回到 2 的注释在 <c>:733</c>）。
        /// ⛔ 本方法**不经过档位 1**：原版循环自己也跳过它 —— <c>:733</c> 那一支的注释逐字是
        /// <c>don't use OBS_CHASE_LOCKED anymore</c>，档位 1 只能由 <c>spec_mode 1</c> 进入。
        /// 本工程实现了该循环里的 2 / 4 / 3 ⇒ 本方法就是那条循环**扣掉未实现档与档位 1**后的样子：
        /// 4 之后是 3；3 之后是 5 / 6（未实现）⇒ 按原版末档口径回到 2。
        /// ⛔ 不切到没有机位的档位（切过去只会让档位名变了、镜头没动）。既没机位、也不在循环里的档位被问到就归一为默认档。</para>
        /// </summary>
        public static CsSpectatorMode NextMode(CsSpectatorMode current)
        {
            switch (current)
            {
                case CsSpectatorMode.ChaseFree: return CsSpectatorMode.FirstPerson;   // 2 → 4
                case CsSpectatorMode.FirstPerson: return CsSpectatorMode.FreeLook;    // 4 → 3
                case CsSpectatorMode.FreeLook: return CsSpectatorMode.ChaseFree;      // 3 →（5/6 未实现）→ 2
                default: return DefaultMode;
            }
        }

        /// <summary>
        /// 追尾机位（档位 1 / 2）的**相机角度来源** —— 纯函数。位置算法两档完全共用
        /// （<c>V_GetChaseOrigin</c>，<c>HLSDK/cl_dll/view.cpp:894-1050</c>），差异只在这里：
        /// <list type="bullet">
        /// <item>档位 1 <see cref="CsSpectatorMode.ChaseLocked"/>（原版 <c>OBS_CHASE_LOCKED</c>）：
        /// 相机角度**锁到目标** —— 原版 <c>view.cpp:1532</c> 给 <c>V_GetChasePos</c> 传的鼠标角是 <c>NULL</c>，
        /// 那里判 <c>cl_angles == NULL</c> 就取 <c>ent-&gt;angles</c>（<c>view.cpp:1264-1268</c>，
        /// 注释逐字 <c>no mouse angles given, use entity angles ( locked mode )</c>）；</item>
        /// <item>档位 2 <see cref="CsSpectatorMode.ChaseFree"/>（原版 <c>OBS_CHASE_FREE</c>）：
        /// 相机角度取**观察者自己**的视角 —— <c>view.cpp:1535</c> 传的是 <c>v_cl_angles</c>，
        /// <c>V_GetChasePos</c> 走 <c>VectorCopy(cl_angles, angles)</c>（<c>view.cpp:1269-1270</c>）。</item>
        /// </list>
        ///
        /// <para><paramref name="targetYaw"/> / <paramref name="targetPitch"/> = 目标的视角角，
        /// 与档位 4（原版 <c>OBS_IN_EYE</c>）用的是同一套目标朝向（<c>V_GetInEyePos</c> 同样取
        /// <c>ent-&gt;angles</c>，<c>view.cpp:1306</c>）。返回 false = <paramref name="mode"/> 不是追尾档位，
        /// 此时按观察者自己的角给出，调用方据此留痕。</para>
        /// </summary>
        public static bool TryChaseCameraAngles(CsSpectatorMode mode, float spectatorYaw, float spectatorPitch,
                                                float targetYaw, float targetPitch,
                                                out float yaw, out float pitch)
        {
            switch (mode)
            {
                case CsSpectatorMode.ChaseLocked:
                    yaw = targetYaw;
                    pitch = targetPitch;
                    return true;

                case CsSpectatorMode.ChaseFree:
                    yaw = spectatorYaw;
                    pitch = spectatorPitch;
                    return true;

                default:
                    yaw = spectatorYaw;
                    pitch = spectatorPitch;
                    return false;
            }
        }

        /// <summary>档位名 = 原版本地化串原文（<c>cstrike_english.txt:941-946</c>，经 <c>#Spec_Mode%d</c> 取）。</summary>
        public static string ModeName(CsSpectatorMode mode)
        {
            switch (mode)
            {
                case CsSpectatorMode.ChaseLocked: return "Locked Chase Cam";
                case CsSpectatorMode.ChaseFree: return "Free Chase Cam";
                case CsSpectatorMode.FreeLook: return "Free Look";
                case CsSpectatorMode.FirstPerson: return "First Person";
                case CsSpectatorMode.FreeOverview: return "Free Overview";
                case CsSpectatorMode.ChaseOverview: return "Chase Overview";
                default: return string.Empty;
            }
        }

        /// <summary>非法档位号的原版提示原文（<c>hud_spectator.cpp:801</c>；亦见客户端 <c>client.dll</c> file <c>0xe9a00</c>）。</summary>
        public const string InvalidModeText = "Invalid spectator mode.";

        /// <summary><c>spec_mode</c> 的用法串 = 原版原文（客户端 <c>client.dll</c> file <c>0xe9530</c>）。</summary>
        public const string SpecModeUsageText = "usage:  spec_mode <Main Mode> [<Inset Mode>]";

        /// <summary>未实现档位的拒绝文案（沿用原版英文提示的句式；该档位的机位在本工程不存在，见 <see cref="IsImplemented"/>）。</summary>
        public static string NotImplementedText(CsSpectatorMode mode)
        {
            return $"Not implemented in this build: {ModeName(mode)}.";
        }

        /// <summary>
        /// <c>spec_mode &lt;n&gt;</c> 的档位解析与**范围**校验。原版口径：<c>SetModes(atoi(argv1), …)</c>
        /// 后判 <c>iNewMainMode &lt; OBS_CHASE_LOCKED || iNewMainMode &gt; OBS_MAP_CHASE</c> 就报
        /// <c>"Invalid spectator mode."</c>（<c>hud_spectator.cpp:799-803</c>）⇒ 原版的合法区间就是
        /// <b>1..6</b>；本方法只判这个区间（"本工程有没有该机位"由 <see cref="TrySetMode"/> 再判一层）。
        ///
        /// <para>解析用 <c>int.TryParse</c>（整数严格匹配）；原版 <c>atoi</c> 会把 <c>"3abc"</c> 截成 3，
        /// 本工程不模仿那截断行为（越界与非法输入都落到同一条 <c>false</c>）。</para>
        /// </summary>
        /// <param name="reason">失败原因（用法串 / 原版原文 / 未实现文案），成功时为 null。</param>
        public static bool TryParseMode(string text, out CsSpectatorMode mode, out string reason)
        {
            mode = DefaultMode;
            reason = null;

            if (string.IsNullOrEmpty(text))
            {
                reason = SpecModeUsageText;
                return false;
            }

            if (!int.TryParse(text.Trim(), out var value))
            {
                reason = InvalidModeText;
                return false;
            }

            if (value < (int)CsSpectatorMode.ChaseLocked || value > (int)CsSpectatorMode.ChaseOverview)
            {
                reason = InvalidModeText;
                return false;
            }

            mode = (CsSpectatorMode)value;
            return true;
        }

        /// <summary>
        /// <c>spec_mode &lt;n&gt;</c> 的唯一写入入口：解析 + **已实现机位闸门** + 写状态。
        ///
        /// <para>只有已实现机位（<see cref="IsImplemented"/>）才会改 <see cref="Mode"/>；被拒时
        /// **状态一个字节都不改**，返回 false 并把文案交给调用方（控制台显示 + 留痕）。
        /// 这是"画面上显示的档位名必须等于真正生效的机位"的实现面：不存在"名字切了、机位没动"的中间态。</para>
        /// </summary>
        public static bool TrySetMode(string text, out string reason)
        {
            if (!TryParseMode(text, out var mode, out reason)) return false;

            if (!IsImplemented(mode))
            {
                reason = NotImplementedText(mode);
                return false;
            }

            Mode = mode;
            reason = null;
            return true;
        }

        /// <summary>
        /// 非观战态按观战键的原因文案。<b>本项目新增措辞</b>：原版在 <c>if ( !g_iUser1 ) return;</c>
        /// （<c>hud_spectator.cpp:689-690</c>）静默返回，盘上没有对应提示串。
        /// </summary>
        public const string NotSpectatingText = "Not spectating (spectator keys are ignored while alive).";

        /// <summary>
        /// 观战键（原版 JUMP / 本工程 <c>[空格]</c>）的**唯一入口**：按原版循环序
        /// （<see cref="NextMode"/>）切到下一档，写入仍走 <see cref="TrySetMode"/> ——
        /// 档位状态只有 <see cref="Mode"/> 这一份，⛔ 不另立第二套。
        ///
        /// <para><b>闸门</b>：原版把 JUMP 循环写在 <c>hud_spectator.cpp:715-733</c>，入口闸门是同文件
        /// <c>:689-690</c> 的 <c>if ( !g_iUser1 ) return;</c> ⇒ 只有观战/阵亡态才消费这个键
        /// （<paramref name="spectating"/> = false 时返回 false，且状态一个字节都不改）。
        /// 活着时空格是跳跃，由 <c>Module/Player/PlayerMotor.cs</c> 那条读键负责。</para>
        ///
        /// <para>⛔ 循环按原版**跳过档位 1**（<c>:733</c> 注释 <c>don't use OBS_CHASE_LOCKED anymore</c>）——
        /// 档位 1 只能由 <c>spec_mode 1</c> 进入，从档位 1 按本键回到默认档而不是停在 1。</para>
        /// </summary>
        /// <param name="spectating">本地玩家当前是否处于观战/阵亡态。</param>
        /// <param name="reason">没切档的原因（非观战态 / 目标档位没有机位），成功时为 null；调用方据此留痕。</param>
        /// <returns>是否真的切了档位。</returns>
        public static bool TryCycleModeOnJump(bool spectating, out string reason)
        {
            reason = null;
            if (!spectating)
            {
                reason = NotSpectatingText;
                return false;
            }

            var want = NextMode(Mode);
            if (!IsImplemented(want))
            {
                // NextMode 只返回有机位的档位；这一步是防口径漂移的闸门（⛔ 不静默：调用方会 Warn）。
                reason = NotImplementedText(want);
                return false;
            }

            // 单一写入入口：被拒时 Mode 保持不变，reason 由它给出（"名字切了、机位没动"的中间态不存在）。
            return TrySetMode(((int)want).ToString(), out reason);
        }

        /// <summary>
        /// next / prev 的**首尾环绕**：返回在 <paramref name="count"/> 个元素上从
        /// <paramref name="index"/> 朝 <paramref name="dir"/> 走一格后的下标（<c>dir &gt;= 0</c> 为下一个，
        /// 否则上一个）。末位 +1 回到首位、首位 −1 回到末位。
        ///
        /// <para>公式与原版一致：<c>hud_spectator.cpp:635-643</c> 的
        /// <c>if (iCurrent &gt; MAX_PLAYERS) iCurrent = 1; if (iCurrent &lt; 1) iCurrent = MAX_PLAYERS;</c>
        /// 是同一件事（环绕），<c>CsMatch.SpectateNext</c> 用的也是同一条 <c>((i + step) % n + n) % n</c>。</para>
        ///
        /// <para><paramref name="index"/> 越界（含 −1 = "当前还没有目标"）时归一：<c>dir &gt;= 0</c> 取首位、
        /// 否则取末位 —— 对应原版 <c>FindNextPlayer</c> 在 <c>g_iUser2 == 0</c> 时也照走一格
        /// （<c>:620-629</c>：无目标时 <c>iStart = 1</c> 再 <c>iCurrent += iDir</c>）。</para>
        /// <paramref name="count"/> &lt;= 0（没有可观察目标）返回 −1。</summary>
        public static int StepIndex(int index, int count, int dir)
        {
            if (count <= 0) return -1;
            if (index < 0 || index >= count) return dir < 0 ? count - 1 : 0;
            var step = dir >= 0 ? 1 : -1;
            return ((index + step) % count + count) % count;
        }

        /// <summary>
        /// <c>spec_player &lt;名字|#序号&gt;</c> 的参数解析。<paramref name="names"/> = 观战对象表
        /// （只含活人）中每个目标的名字，下标与表一致。
        ///
        /// <para><c>#n</c> 里的 <c>n</c> 是**表内 1-based 序号**（与模拟自己的日志口径
        /// <c>CsMatch.SpectateNext</c> 的 <c>_spectateIndex + 1/n</c> 一致）；名字按
        /// **不区分大小写完全相等**匹配。</para>
        ///
        /// <para>原版的观战对象列表项是 <c>#Spec_PlayerItem</c> / <c>#Spec_PlayerItem_Team</c>
        /// （<c>client.dll</c> file <c>0xed870</c> / <c>0xed884</c>，即列表里的"玩家项"），
        /// 选中不可观战对象时给 <c>#Spec_Not_Valid_Choice</c>（"** You are not allowed to spectate this person."）；
        /// 盘上三份二进制里**没有** <c>spec_player</c> 这个命令串 ⇒ 本方法的参数形态是本工程按上面两条
        /// 列表口径取的（见 <c>策划/差异登记.tsv</c>）。</para>
        /// </summary>
        public static bool TryParseTarget(string text, IReadOnlyList<string> names, out int index, out string error)
        {
            index = -1;
            error = null;

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                error = "用法：spec_player <名字|#序号>";
                return false;
            }

            if (names == null || names.Count == 0)
            {
                error = "没有可观察目标（原版 #Spec_NoPlayers：" + NoPlayersText + "）";
                return false;
            }

            var token = text.Trim();

            if (token[0] == '#')
            {
                if (!int.TryParse(token.Substring(1), out var ordinal) || ordinal < 1 || ordinal > names.Count)
                {
                    error = $"序号超出范围（1..{names.Count}）";
                    return false;
                }
                index = ordinal - 1;
                return true;
            }

            for (var i = 0; i < names.Count; i++)
            {
                if (string.Equals(names[i], token, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            error = $"找不到观战对象：{token}";
            return false;
        }

        /// <summary>原版 <c>#Spec_NoPlayers</c> 的文案（<c>cstrike_english.txt:947</c>）。</summary>
        public const string NoPlayersText = "No Players to Spectate";
    }

    /// <summary>
    /// 观战**自由飞行**（原版 <c>OBS_ROAMING</c> = 档位 3）的位置解算 —— 纯函数，不碰相机 / 不碰
    /// Transform（相机写入仍然只在 <c>Module/CameraRig/FirstPersonCamera</c>）。
    ///
    /// <para><b>它为什么在 <c>Core</c></b>：数值全是 <see cref="CsConst"/> 的 <c>SpecRoam*</c> 常量，
    /// 逻辑是原版共享代码 <c>PM_SpectatorMove</c> 的逐语句搬运（<c>pm_shared.c:1490-1572</c>）——
    /// 放在这里才能被离线断言宿主直接跑（不启编辑器、不进 Play）。</para>
    ///
    /// <para><b>原版语义</b>：自由飞行时观察者的机位就是**他自己的**模拟原点 + 他自己的鼠标角
    /// （<c>view.cpp:1538-1540</c>）；这个原点由 <c>PM_SpectatorMove</c> 按他的 usercmd 推进。
    /// 该分支**无重力、无碰撞、无边界钳制**（<c>pm_shared.c:2456-2461</c> 进了函数就 return）。</para>
    /// </summary>
    public static class CsSpectatorRoam
    {
        /// <summary>自由飞行的一帧状态：位置（米）与速度（米/秒），都是本工程的世界坐标口径。</summary>
        public struct State
        {
            public Vector3 Origin;
            public Vector3 Velocity;
        }

        /// <summary>
        /// 推进一帧自由飞行，**逐语句对应原版 <c>PM_SpectatorMove</c> 的 <c>OBS_ROAMING</c> 分支
        /// （<c>pm_shared.c:1490-1572</c>）：
        /// <list type="number">
        /// <item>摩擦：<c>speed &lt; SpecRoamDeadbandSpeed</c> 直接清零（原版 <c>:1515-1518</c>），否则
        /// <c>control = max(speed, SvStopSpeed)</c>、<c>drop = control × SvFriction × 1.5 × dt</c>、
        /// 速度按 <c>(speed − drop)/speed</c> 缩放（原版 <c>:1521-1534</c>）；</item>
        /// <item>期望速度 <c>wish = forward × forwardMove + right × sideMove</c>，再 <c>wish.y += upMove</c>，
        /// 然后按 <see cref="CsConst.SpecRoamMaxSpeed"/> 钳制（原版 <c>:1537-1555</c>）；</item>
        /// <item><c>addSpeed = wishSpeed − dot(velocity, wishDir)</c>；<c>≤ 0</c> 时**提前返回** ——
        /// 速度已经被摩擦过、位置这一步不推进（原版 <c>:1560-1561</c> 就是这样，"松手即停"）；</item>
        /// <item>否则 <c>accel = min(SvAccelerate × dt × wishSpeed, addSpeed)</c>，速度加上去，位置按
        /// <c>origin += dt × velocity</c> 积分（原版 <c>:1563-1571</c>）。</item>
        /// </list>
        ///
        /// <para><b>单位</b>：入口出口都是米 / 米每秒（本工程口径），<paramref name="dt"/> 吃秒；
        /// <paramref name="forward"/> / <paramref name="right"/> 须是单位向量，且 <paramref name="forward"/>
        /// **含俯仰** —— 原版 <c>pm_shared.c:2454</c> 的
        /// <c>AngleVectors(pmove-&gt;angles, pmove-&gt;forward, pmove-&gt;right, pmove-&gt;up)</c> 就是这样，
        /// 所以"抬头 / 低头 + 前进"本来就是原版的垂直移动方式（<c>upMove</c> 另有 <c>+moveup</c> /
        /// <c>+movedown</c> 一条路，本工程不绑专用上下键 ⇒ 调用方传 0）。</para>
        /// </summary>
        public static State Step(State s, float dt, Vector3 forward, Vector3 right,
                                 float forwardMove, float sideMove, float upMove)
        {
            if (dt <= 0f) return s;

            // ① 摩擦（原版 :1513-1535）
            var velocity = s.Velocity;
            var speed = velocity.magnitude;
            if (speed < CsConst.SpecRoamDeadbandSpeed)
            {
                velocity = Vector3.zero;
            }
            else
            {
                var friction = CsConst.SvFriction * CsConst.SpecRoamFrictionScale;
                var control = speed < CsConst.SvStopSpeed ? CsConst.SvStopSpeed : speed;
                var drop = control * friction * dt;
                var newspeed = speed - drop;
                if (newspeed < 0f) newspeed = 0f;
                velocity = velocity * (newspeed / speed);
            }

            // ② 期望速度（原版 :1537-1555）
            var wish = forward * forwardMove + right * sideMove;
            wish.y += upMove;
            var wishSpeed = wish.magnitude;
            var wishDir = wishSpeed > 0f ? wish * (1f / wishSpeed) : Vector3.zero;
            if (wishSpeed > CsConst.SpecRoamMaxSpeed) wishSpeed = CsConst.SpecRoamMaxSpeed;

            // ③ 已达期望速度 ⇒ 原版在这里 return：速度已摩擦、位置不推进（原版 :1557-1561）
            var currentSpeed = wishDir.x * velocity.x + wishDir.y * velocity.y + wishDir.z * velocity.z;
            var addSpeed = wishSpeed - currentSpeed;
            if (addSpeed <= 0f) return new State { Origin = s.Origin, Velocity = velocity };

            // ④ 加速 + 位置积分（原版 :1563-1571）
            var accelSpeed = CsConst.SvAccelerate * dt * wishSpeed;
            if (accelSpeed > addSpeed) accelSpeed = addSpeed;
            velocity = velocity + wishDir * accelSpeed;

            return new State { Origin = s.Origin + velocity * dt, Velocity = velocity };
        }
    }
}
