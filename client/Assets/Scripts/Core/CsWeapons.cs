using System.Collections.Generic;

namespace Cs16.Core
{
    /// <summary>
    /// 武器定义（数值真源）。只读数据，运行时不得修改；
    /// 查询一律走 <see cref="CsWeapons.Get"/> / <see cref="CsWeapons.ByClass"/>。
    ///
    /// <para><b>数值出处（逐条核对过）</b>：<see cref="Price"/> / <see cref="Magazine"/> /
    /// <see cref="ReserveAmmo"/> 三项全部来自原版 <c>mp.dll</c> 的 <c>WeaponInfo[]</c> ——
    /// 数组基址 <c>mp.dll:0x10f708</c>、步长 <c>0x20</c>，字段偏移 <c>iCost=+0x04</c>、
    /// <c>iMaxClip=+0x10</c>、<c>iMaxAmmo=+0x14</c>、<c>pszName=+0x1c</c>
    /// （字段语义的铁证见 <c>原版资源/解包产物/原版数值表.md</c> §0.3/§3）。</para>
    ///
    /// <para><b>已取到原版出处的几组</b>（载体同 <c>mp.dll</c>，逐条 VA / 反汇编见
    /// <c>策划/手感参数对照.md</c>）：<see cref="CsWeaponDef.ReloadTime"/>（§5.9，21 把）、
    /// <see cref="CsWeaponDef.MaxSpeed"/>（§5.4，逐武器类）、<see cref="CsWeaponDef.CycleTime"/>（§2），
    /// 以及 AK47 / AUG / SG552 / M249 的 <see cref="CsWeaponDef.Damage"/>（§5.2）。</para>
    ///
    /// <para><b>仍无原版出处的几组</b>：<see cref="CsWeaponDef.ArmorPenetration"/> /
    /// <see cref="CsWeaponDef.Rpm"/> / <see cref="CsWeaponDef.Range"/> / <see cref="CsWeaponDef.Spread"/> /
    /// <see cref="CsWeaponDef.MoveSpread"/> / <see cref="CsWeaponDef.RecoilVert"/> /
    /// <see cref="CsWeaponDef.RecoilHoriz"/> / <see cref="CsWeaponDef.KillReward"/> ——
    /// 原版把它们写死在每把武器类的 <c>PrimaryAttack</c> 浮点立即数里、部分尚未反汇编
    /// ⇒ 按"未验证"处理，见 <c>策划/对照表.md</c> BLOCKED-2。</para>
    ///
    /// <para><b>逐武器穿墙层数与距离衰减</b>：<see cref="CsWeaponDef.Penetration"/>（原版 <c>iPenetration</c>）、
    /// <see cref="CsWeaponDef.RangeModifier"/> 与 <see cref="CsWeaponDef.FalloffDenominator"/>（弹种分母）
    /// 只在**确证了武器归属**的那 22 把上落值，落值处见 <c>CsWeapons.ApplyPenetrationAndFalloffValues</c>
    /// （出处 <c>.ai-tmp/test/mpdll-reverse/PEN-FILL.md</c>）。</para>
    ///
    /// <para><b>未取到出处的项一律回落工程旧行为</b>（⛔ 不是"未知即零"）：没有
    /// <see cref="CsWeaponDef.FalloffDenominator"/> 的武器走 <see cref="CsWeaponDef.FalloffPerMeter"/>
    /// 的线性衰减（见 <c>CsWeapons.DistanceFalloff</c>）；没有 <see cref="CsWeaponDef.Penetration"/>
    /// 的武器走旧的穿墙判据（见 <c>Firearm.CanPenetrate</c>）。两处都登记在 <c>策划/差异登记.tsv</c>
    /// 待取到出处后切换。</para>
    /// </summary>
    public sealed class CsWeaponDef
    {
        public string Id;
        public string DisplayName;
        public CsWeaponClass Class;
        public CsWeaponSlot Slot;          // (CsWeaponSlot)0 = 无槽位（装备）
        public int Price;
        public int Damage;                 // 基础伤害（近距离、无甲）

        /// <summary>0~1，越高越无视护甲（只作用于护甲吸收）；穿墙与否与之无关。</summary>
        public float ArmorPenetration;

        /// <summary>
        /// 原版 <c>FireBullets3</c> 的 <c>iPenetration</c>：本武器一发能穿过的墙层数。
        /// 0 = 该武器未取到值 ⇒ **回落工程旧穿墙判据**（护甲穿透系数 + 固定 2 层），
        /// 见 <c>Firearm.CanPenetrate</c>；登记见 <c>策划/差异登记.tsv</c>。
        ///
        /// <para>出处 = <c>原版资源/cs16src/cstrike/dlls/mp.dll</c> 各武器开火点对 <c>FireBullets3</c>
        /// （VA <c>0x10026420</c>）的第 3 实参；逐武器调用点 VA 见
        /// <c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §4.2。</para>
        ///
        /// <para>消费点 = <c>Firearm.ResolvePellet</c>（可穿与否 + 层数上限）。</para>
        /// </summary>
        public int Penetration;
        public int Rpm;                    // 每分钟射速（0 = 不可连发）
        public int Magazine;               // 弹匣容量
        public int ReserveAmmo;            // 备弹
        public float ReloadTime;           // 换弹秒数
        public float Spread;               // 静止基础散布（度）
        public float MoveSpread;           // 移动附加散布（度）

        /// <summary>
        /// 手持本武器时的最大移动速度（m/s）；0 = 原版该武器类**未覆盖** <c>GetMaxSpeed</c>，
        /// 由 <c>CsInventory.MovementSpeed</c> 退回 <c>CsConst.SpeedDefault</c>。
        ///
        /// <para>出处 = 原版 <c>mp.dll</c> 各武器类的 <c>GetMaxSpeed</c>（vftable 槽 78）返回值，
        /// 逐类的取值函数 VA / vtable 起点见 <c>策划/手感参数对照.md</c> §5.4（归属按 <c>WeaponModel</c> 槽 61 确证）。
        /// 原版值单位是 unit/s，× 0.0254 得 m/s。</para>
        /// </summary>
        public float MaxSpeed;

        public float RecoilVert;           // 每发垂直后坐力（度）
        public float RecoilHoriz;          // 每发水平后坐力（度）
        public int Pellets;                // 单发弹丸数（霰弹 > 1）
        public float Range;                // 有效射程（米）
        /// <summary>
        /// 每米伤害衰减系数 —— **弹种未取到**的武器仍走它（线性模型
        /// <c>1 - FalloffPerMeter * dist</c>）。有 <see cref="FalloffDenominator"/> 的武器不再消费本字段。
        /// 回落分支见 <see cref="CsWeapons.DistanceFalloff"/>。
        /// </summary>
        public float FalloffPerMeter;
        public int KillReward;             // 用本武器击杀的奖励
        public bool AutoFire;              // 全自动
        public CsTeamLimit TeamLimit;
        public string SoundFire = "fire";
        public string SoundReload = "reload";

        /// <summary>
        /// 差异 #68：<b>attack2（右键 / 次级开火）能切换"消音器 on/off"</b>。
        /// 出处 = 原版 <c>client.dll</c> 的 `weapons/usp_silencer_on|off.wav`、`weapons/m4a1_silencer_on|off.wav`
        /// （详见 <see cref="CsWeapons"/> 里 <c>MarkAttack2Capabilities</c> 的出处表）。默认 false = 无出处不接。
        /// </summary>
        public bool CanSilence;

        /// <summary>
        /// 差异 #68：<b>attack2（右键）能切换"连发模式"</b>。
        /// 出处 = 原版 <c>client.dll</c> 的 `weapons/famas-burst.wav` + `#Switch_To_BurstFire`。
        /// </summary>
        public bool CanBurst;

        // ==================================================================
        //  差异 #68：attack2 两态的**数值**（0 = 该武器没有这一档的出处 ⇒ 一律不接）
        //  出处 = 原版 `mp.dll`（基址 0x10000000）的字段常量与分支，逐条见
        //  `策划/武器右键数值出处.md`；消费点见 `CsWeapons.BaseDamage` /
        //  `CsWeapons.StaticSpreadFor` / `CsWeapons.CycleTimeFor` 的调用处。
        // ==================================================================

        /// <summary>装消音后的基础伤害（原版武器对象 +0x148/+0x14c 那份；0 = 两态同值或没出处）。</summary>
        public int DamageSilenced;

        /// <summary>连发档的基础伤害（原版 Famas 的 +0x148 那份；0 = 两态同值或没出处）。</summary>
        public int DamageBurst;

        /// <summary>原版 <c>flRangeModifier</c>：伤害随距离衰减的底数（0 = 没出处）。</summary>
        public float RangeModifier;

        /// <inheritdoc cref="RangeModifier"/>
        public float RangeModifierSilenced;

        /// <summary>原版开火时传的射程距离（世界单位，四把枪同为 8192）。</summary>
        public float RangeModifierMaxDistance;

        /// <summary>
        /// 距离衰减公式 <c>pow(rangeModifier, dist_in_units / 本值)</c> 的分母，**按弹种取**。
        /// 0 = 该武器的弹种未取到 ⇒ **回落 <see cref="FalloffPerMeter"/> 的线性衰减**
        /// （见 <see cref="CsWeapons.DistanceFalloff"/>；登记见 <c>策划/差异登记.tsv</c>）。
        ///
        /// <para>出处 = <c>原版资源/cs16src/cstrike/dlls/mp.dll</c> 子弹模拟函数 VA <c>0x10025640</c>
        /// 的按弹种参数表（逐弹种的 float 与本字段的对应关系见
        /// <c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §4.3）。</para>
        /// </summary>
        public float FalloffDenominator;

        /// <summary>原版静止站散布系数（未装消音 / 半自动档）。</summary>
        public float StaticSpreadFactor;

        /// <summary>原版静止站散布系数（装消音档；0 = 与未装同档）。</summary>
        public float StaticSpreadFactorSilenced;

        /// <summary>原版静止站散布系数（连发档；0 = 与普通同档）。</summary>
        public float StaticSpreadFactorBurst;

        /// <summary>连发一轮的发数（0 = 不可连发）。</summary>
        public int BurstShots;

        /// <summary>连发首发 → 第 2 发的间隔（秒）。</summary>
        public float BurstIntervalFirst;

        /// <summary>连发的后续续发间隔（秒）。</summary>
        public float BurstInterval;

        /// <summary>连发模式的循环时间（秒）：一轮打满后到下一轮扣扳机的最短间隔。</summary>
        public float BurstCycleTime;

        /// <summary>非连发档的循环时间（秒）；0 = 用 <see cref="Rpm"/> 推出来的 <see cref="SecondsPerShot"/>。</summary>
        public float CycleTime;

        // ==================================================================
        //  后坐力：原版 `KickBack` 的实参
        //  出处 = 原版 mp.dll 各武器类 `fire` 函数内对 `0x10097F10` 的调用点常量，
        //  逐把的调用点 VA / 参数见 `策划/手感参数对照.md` §5.5；
        //  累加公式见 `CsRecoil`（机制形状出处 = 原版资源/hlsdk/dlls/weapons.cpp:534-571）。
        // ==================================================================

        /// <summary>
        /// 本武器是否**参数齐全**的原版 kickback（7 个实参都取到）。
        /// <para>false ⇒ <c>CsRecoil.Apply</c> 退回本工程既有的"每发固定度数"模型
        /// （<see cref="RecoilVert"/> / <see cref="RecoilHoriz"/>）。
        /// 不齐 / 归属未确证的一律留 false —— 拿"看起来合理"的数补齐比空缺更糟。</para>
        /// </summary>
        public bool HasKickback;

        /// <summary>首发抬升基准（度）。</summary>
        public float KickUpBase;

        /// <summary>首发水平偏移基准（度）。</summary>
        public float KickLateralBase;

        /// <summary>第 2 发起每发递增的抬升量（度）。</summary>
        public float KickUpModifier;

        /// <summary>第 2 发起每发递增的水平量（度）。</summary>
        public float KickLateralModifier;

        /// <summary>抬升上限（度）。</summary>
        public float KickUpMax;

        /// <summary>水平偏移上限（度）。</summary>
        public float KickLateralMax;

        /// <summary>方向翻转除数 <c>d</c>：每发有 <c>1/(d+1)</c> 的概率翻转水平方向（0 = 每发都翻）。</summary>
        public int KickDirectionChange;

        public bool HasSlot => (int)Slot > 0;
        public float SecondsPerShot => Rpm > 0 ? 60f / Rpm : 0f;
    }

    /// <summary>全部武器表（手工维护；两端一致由本文件唯一持有）。</summary>
    public static class CsWeapons
    {
        public const string Knife = "knife";
        public const string Glock18 = "glock18";
        public const string Usp = "usp";
        public const string P228 = "p228";
        public const string Deagle = "deagle";
        public const string FiveSeven = "fiveseven";
        public const string Elite = "elite";
        public const string Mp5 = "mp5";
        public const string Tmp = "tmp";
        public const string Mac10 = "mac10";
        public const string Ump45 = "ump45";
        public const string P90 = "p90";
        public const string Galil = "galil";
        public const string Famas = "famas";
        public const string Ak47 = "ak47";
        public const string M4A1 = "m4a1";
        public const string Sg552 = "sg552";
        public const string Aug = "aug";
        public const string Scout = "scout";
        public const string Awp = "awp";
        public const string G3sg1 = "g3sg1";
        public const string Sg550 = "sg550";
        public const string M3 = "m3";
        public const string Xm1014 = "xm1014";
        public const string M249 = "m249";
        public const string HeGrenade = "hegrenade";
        public const string Flashbang = "flashbang";
        public const string SmokeGrenade = "smokegrenade";
        public const string Vest = "vest";
        public const string VestHelm = "vesthelm";
        public const string Defuser = "defuser";
        public const string C4 = "c4";

        // 原版 `GetMaxSpeed` 的档位常量（unit/s）⇒ × 0.0254 = m/s。出处 = mp.dll 各武器类 `GetMaxSpeed`，
        // 逐档的常量 VA / 字节见 `策划/手感参数对照.md` §1 与 §5.4。
        private const float Spd210 = 5.334f;    // AWP / SG550 / G3SG1
        private const float Spd220 = 5.588f;    // M249
        private const float Spd221 = 5.6134f;   // AK47
        private const float Spd230 = 5.842f;    // M4A1 / M3
        private const float Spd235 = 5.969f;    // SG552
        private const float Spd240 = 6.096f;    // AUG / FAMAS / Galil / XM1014
        private const float Spd245 = 6.223f;    // P90
        private const float Spd250 = 6.35f;     // UMP45 / TMP / MP5 / MAC10 / Elite / 手枪系 / 刀
        private const float Spd260 = 6.604f;    // Scout

        // W(...) 的行尾注释 = 该武器在 mp.dll `WeaponInfo[]` 里的条目偏移与三项原版值
        // （格式：`mp.dll:偏移 iCost / iMaxClip / iMaxAmmo`，均照 mp.dll 逐条读出，读法见类注释）。
        //
        // 逐武器 `reload`（第 11 个浮参）= 原版共享函数 `DefaultReload` 的 `fDelay` 实参，
        // 出处 `策划/手感参数对照.md` §5.9（已取到 21 把；未取到的 M3 / XM1014 保持原值不动）。
        // 逐武器 `maxSpeed`（末尾可选参）= 原版该类 `GetMaxSpeed` 的返回值 × 0.0254，出处同文件 §5.4。
        private static readonly List<CsWeaponDef> _all = new List<CsWeaponDef>
        {
            // 刀 / 手雷 / 装备 / C4 不在 `WeaponInfo[]` 里（该表只含 24 把枪 + 战术盾 idx25）：
            W(Knife, "Knife", CsWeaponClass.Knife, CsWeaponSlot.Knife, 0, 55, 0.50f, 200, 0, 0, 0f, 0f, 0f, 0f, 0f, 1, 1.6f, 0f, 1500, false, CsTeamLimit.Any, Spd250),

            W(Glock18, "Glock 18", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 400, 25, 0.47f, 400, 20, 120, 2.2f, 0.60f, 1.50f, 1.00f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.TerroristOnly, Spd250), // mp.dll:0x10f728 400 / 20 / 120（W-02）
            W(Usp, "USP .45", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 500, 34, 0.50f, 352, 12, 100, 2.7f, 0.50f, 1.40f, 1.10f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.CounterTerroristOnly, Spd250), // mp.dll:0x10f8a8 500 / 12 / 100（W-13）
            W(P228, "P228", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 600, 32, 0.50f, 400, 13, 52, 2.7f, 0.50f, 1.40f, 1.10f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any, Spd250), // mp.dll:0x10f708 600 / 13 / 52（W-01）
            W(Deagle, "Desert Eagle", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 650, 54, 0.62f, 267, 7, 35, 2.2f, 0.60f, 2.00f, 1.80f, 0.60f, 1, 40f, 0.02f, 300, false, CsTeamLimit.Any, Spd250), // mp.dll:0x10f9a8 650 / 7 / 35（W-21）
            W(FiveSeven, "Five-SeveN", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 750, 20, 0.60f, 400, 20, 100, 2.7f, 0.50f, 1.40f, 1.00f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.CounterTerroristOnly, Spd250), // mp.dll:0x10f808 750 / 20 / 100（W-08）
            W(Elite, "Dual Berettas", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 800, 38, 0.47f, 400, 30, 120, 4.5f, 0.60f, 1.60f, 1.20f, 0.50f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any, Spd250), // mp.dll:0x10f7e8 800 / 30 / 120（W-07）

            W(Mp5, "MP5 Navy", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1500, 26, 0.52f, 750, 30, 120, 2.63f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.Any, Spd250), // mp.dll:0x10f8e8 1500 / 30 / 120（W-15）
            W(Tmp, "TMP", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1250, 20, 0.50f, 857, 30, 120, 2.12f, 0.60f, 1.30f, 0.80f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.CounterTerroristOnly, Spd250), // mp.dll:0x10f968 1250 / 30 / 120（W-19）
            W(Mac10, "MAC-10", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1400, 29, 0.48f, 857, 30, 100, 3.15f, 0.60f, 1.40f, 1.00f, 0.50f, 1, 35f, 0.015f, 600, false, CsTeamLimit.TerroristOnly, Spd250), // mp.dll:0x10f7a8 1400 / 30 / 100（W-05）
            W(Ump45, "UMP-45", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1700, 30, 0.50f, 571, 25, 100, 3.5f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.Any, Spd250), // mp.dll:0x10f828 1700 / 25 / 100（W-09）
            W(P90, "P90", CsWeaponClass.SMG, CsWeaponSlot.Primary, 2350, 21, 0.53f, 857, 50, 100, 3.4f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, true, CsTeamLimit.Any, Spd245), // mp.dll:0x10fa08 2350 / 50 / 100（W-24）

            W(Galil, "Galil", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2000, 30, 0.70f, 666, 35, 90, 2.45f, 0.50f, 1.60f, 1.30f, 0.50f, 1, 45f, 0.01f, 300, true, CsTeamLimit.TerroristOnly, Spd240), // mp.dll:0x10f868 2000 / 35 / 90（W-11）
            W(Famas, "FAMAS", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2250, 30, 0.70f, 666, 25, 90, 3.0f, 0.50f, 1.60f, 1.30f, 0.50f, 1, 45f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly, Spd240), // mp.dll:0x10f888 2250 / 25 / 90（W-12）
            W(Ak47, "AK-47", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2500, 36, 0.775f, 600, 30, 90, 2.45f, 0.45f, 2.00f, 1.90f, 0.70f, 1, 50f, 0.01f, 300, true, CsTeamLimit.TerroristOnly, Spd221), // mp.dll:0x10f9e8 2500 / 30 / 90（W-23）
            // 伤害 32 = 原版"未装消音"档（另一档 33 见 ApplyAttack2Values 的 DamageSilenced）。
            W(M4A1, "M4A1", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3100, 32, 0.70f, 666, 30, 90, 3.05f, 0.40f, 1.80f, 1.50f, 0.60f, 1, 50f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly, Spd230), // mp.dll:0x10f948 3100 / 30 / 90（W-18）
            W(Sg552, "SG-552", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3500, 33, 0.70f, 666, 30, 90, 3.0f, 0.50f, 1.80f, 1.60f, 0.60f, 1, 45f, 0.01f, 300, true, CsTeamLimit.TerroristOnly, Spd235), // mp.dll:0x10f9c8 3500 / 30 / 90（W-22）
            W(Aug, "AUG", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3500, 32, 0.70f, 666, 30, 90, 3.3f, 0.50f, 1.80f, 1.60f, 0.60f, 1, 45f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly, Spd240), // mp.dll:0x10f7c8 3500 / 30 / 90（W-06）

            W(Scout, "Scout", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 2750, 75, 0.85f, 90, 10, 90, 2.0f, 0.20f, 3.00f, 3.00f, 1.00f, 1, 80f, 0.005f, 300, false, CsTeamLimit.Any, Spd260), // mp.dll:0x10f768 2750 / 10 / 90（W-03）
            W(Awp, "AWP", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 4750, 115, 0.975f, 41, 10, 30, 2.5f, 0.10f, 4.00f, 4.00f, 1.20f, 1, 100f, 0.005f, 100, false, CsTeamLimit.Any, Spd210), // mp.dll:0x10f8c8 4750 / 10 / 30（W-14）
            W(G3sg1, "G3SG1", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 5000, 80, 0.98f, 240, 20, 90, 3.5f, 0.30f, 3.00f, 2.50f, 1.00f, 1, 90f, 0.005f, 300, false, CsTeamLimit.TerroristOnly, Spd210), // mp.dll:0x10f988 5000 / 20 / 90（W-20）
            W(Sg550, "SG-550", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 4200, 70, 0.98f, 240, 30, 90, 3.35f, 0.30f, 3.00f, 2.50f, 1.00f, 1, 90f, 0.005f, 300, false, CsTeamLimit.CounterTerroristOnly, Spd210), // mp.dll:0x10f848 4200 / 30 / 90（W-10）

            W(M3, "M3 Super 90", CsWeaponClass.Shotgun, CsWeaponSlot.Primary, 1700, 22, 0.50f, 85, 8, 32, 3.0f, 3.50f, 1.00f, 3.50f, 1.00f, 9, 15f, 0.05f, 900, false, CsTeamLimit.Any, Spd230), // mp.dll:0x10f928 1700 / 8 / 32（W-17）
            W(Xm1014, "XM1014", CsWeaponClass.Shotgun, CsWeaponSlot.Primary, 3000, 20, 0.50f, 171, 7, 32, 4.0f, 3.50f, 1.00f, 3.00f, 1.00f, 6, 15f, 0.05f, 900, false, CsTeamLimit.Any, Spd240), // mp.dll:0x10f788 3000 / 7 / 32（W-04）

            W(M249, "M249", CsWeaponClass.MachineGun, CsWeaponSlot.Primary, 5750, 32, 0.80f, 750, 100, 200, 4.7f, 0.60f, 2.20f, 1.60f, 0.70f, 1, 45f, 0.01f, 300, true, CsTeamLimit.Any, Spd220), // mp.dll:0x10f908 5750 / 100 / 200（W-16）

            W(HeGrenade, "HE Grenade", CsWeaponClass.Grenade, CsWeaponSlot.Grenade, 300, 98, 0.50f, 0, 1, 0, 0f, 0f, 0f, 0f, 0f, 1, 5.5f, 0f, 300, false, CsTeamLimit.Any),
            W(Flashbang, "Flashbang", CsWeaponClass.Grenade, CsWeaponSlot.Grenade, 200, 0, 0f, 0, 2, 0, 0f, 0f, 0f, 0f, 0f, 1, 0f, 0f, 300, false, CsTeamLimit.Any),
            W(SmokeGrenade, "Smoke Grenade", CsWeaponClass.Grenade, CsWeaponSlot.Grenade, 300, 0, 0f, 0, 1, 0, 0f, 0f, 0f, 0f, 0f, 1, 0f, 0f, 300, false, CsTeamLimit.Any),

            W(Vest, "Kevlar Vest", CsWeaponClass.Equipment, (CsWeaponSlot)0, 650, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.Any),
            W(VestHelm, "Kevlar + Helmet", CsWeaponClass.Equipment, (CsWeaponSlot)0, 1000, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.Any),
            W(Defuser, "Defusal Kit", CsWeaponClass.Equipment, (CsWeaponSlot)0, 200, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.CounterTerroristOnly),

            W(C4, "C4 Explosive", CsWeaponClass.Bomb, CsWeaponSlot.Bomb, 0, 0, 0f, 0, 0, 0, 0f, 0f, 0f, 0f, 0f, 0, 0f, 0f, 0, false, CsTeamLimit.TerroristOnly),
        };

        private static readonly Dictionary<string, CsWeaponDef> _byId = new Dictionary<string, CsWeaponDef>();

        static CsWeapons()
        {
            foreach (var w in _all) _byId[w.Id] = w;
            ApplyAttack2Values();
            ApplyPenetrationAndFalloffValues();
            ApplyCycleTimes();
            ApplyKickbackValues();
        }

        /// <summary>
        /// 逐武器**循环时间**（射速两发之间的最短间隔，秒）的原版值。
        ///
        /// <para>出处 = <c>mp.dll</c> 各武器类的开火函数所引用的浮点常量（VA / 字节见
        /// <c>策划/手感参数对照.md</c> §2「射速（循环时间）」各行）：</para>
        /// <list type="bullet">
        /// <item>AUG / SG552 = <c>0.0825</c>（常量 <c>0x10141A24</c>）；</item>
        /// <item>AWP = <c>1.45</c>（常量 <c>0x10141D60</c>）；</item>
        /// <item>M4A1 = <c>0.0875</c>（常量 <c>0x10141A2C</c>）；</item>
        /// <item>USP = <c>0.15</c>（常量 <c>0x10141A4C</c>）。</item>
        /// </list>
        ///
        /// <para>写进 <see cref="CsWeaponDef.CycleTime"/> 而不是改 <see cref="CsWeaponDef.Rpm"/>：
        /// <c>CycleTimeFor</c> 取的是 <c>CycleTime &gt; 0 ? CycleTime : 60/Rpm</c>，两处都给会打架。</para>
        /// </summary>
        private static void ApplyCycleTimes()
        {
            void Set(string id, float cycleTime)
            {
                if (_byId.TryGetValue(id, out var def)) def.CycleTime = cycleTime;
            }

            Set(Aug, 0.0825f);
            Set(Sg552, 0.0825f);
            Set(Awp, 1.45f);
            Set(M4A1, 0.0875f);
            Set(Usp, 0.15f);
        }

        /// <summary>
        /// 逐武器**后坐力 kickback 实参**（原版 6 个 float + 1 个方向翻转除数）。
        ///
        /// <para><b>出处 = <c>策划/手感参数对照.md</c> §5.5</b>（mp.dll 各武器 <c>fire</c> 函数里对
        /// <c>0x10097F10</c> 的调用点；逐把的调用点 VA 与常量 VA 都列在该节）。这里的 11 把是
        /// **参数齐全**的那些。</para>
        ///
        /// <para><b>为什么不在这里的武器</b>：</para>
        /// <list type="bullet">
        /// <item>FAMAS（缺 <c>lateral_modifier</c>）、G3SG1 / SG550（缺 <c>lateral_base</c>）——
        /// §5.5/§5.8 标 <c>?</c>，缺槽不猜；</item>
        /// <item>Glock18 —— §5.8 标 BLOCKED（两次解析都得到全 0，与直觉矛盾）；</item>
        /// <item>手枪系 / 狙击 / 霰弹的其余武器 —— §5.8 的归属只标到"代码邻接标称"，§6 U-3 明确
        /// **尚未确证**（那 12 个 kickback 站点还没跑过归属判定）⇒ 按"不把未确证的归属当事实改代码"处理。</item>
        /// </list>
        /// <para>上面这些一律留 <see cref="CsWeaponDef.HasKickback"/> = false ⇒ 走既有模型。</para>
        /// </summary>
        private static void ApplyKickbackValues()
        {
            void Set(string id, float upBase, float latBase, float upMod, float latMod,
                     float upMax, float latMax, int dirChange)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.HasKickback = true;
                def.KickUpBase = upBase;
                def.KickLateralBase = latBase;
                def.KickUpModifier = upMod;
                def.KickLateralModifier = latMod;
                def.KickUpMax = upMax;
                def.KickLateralMax = latMax;
                def.KickDirectionChange = dirChange;
            }

            Set(Ak47, 1.5f, 0.45f, 0.225f, 0.05f, 6.5f, 2.5f, 7);
            Set(M4A1, 1f, 0.45f, 0.28f, 0.045f, 3.75f, 3f, 7);
            Set(Galil, 1f, 0.45f, 0.28f, 0.045f, 3.75f, 3f, 7);
            Set(Aug, 1f, 0.45f, 0.275f, 0.05f, 4f, 2.5f, 7);
            Set(Sg552, 1f, 0.45f, 0.28f, 0.04f, 4.25f, 2.5f, 7);
            Set(Mp5, 0.9f, 0.475f, 0.35f, 0.0425f, 5f, 3f, 6);
            Set(Tmp, 1.1f, 0.5f, 0.35f, 0.045f, 4.5f, 3.5f, 6);
            Set(Ump45, 0.125f, 0.65f, 0.55f, 0.0475f, 5.5f, 4f, 10);
            Set(Mac10, 1.3f, 0.55f, 0.4f, 0.05f, 4.75f, 3.75f, 5);
            Set(P90, 0.9f, 0.45f, 0.35f, 0.04f, 5.25f, 3.5f, 4);
            Set(M249, 1.8f, 0.65f, 0.45f, 0.125f, 5f, 3.5f, 8);
        }

        /// <summary>
        /// 差异 #68：attack2（右键 / 次级开火）的**逐武器语义**由这里唯一持有。
        ///
        /// <para><b>为什么用"打标记"而不是给 <c>W(...)</c> 加形参</b>：那要给 30 个调用点各补两个恒为 false
        /// 的实参（噪声 + 一次改错就整表错位）；而**有出处的只有 4 把**，一条方法反而看得清、也好核。</para>
        ///
        /// <para><b>出处</b>（降级链第 2 级「可执行里的常量/串」；载体 = 原版 <c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>，
        /// 1,093,128 B，**已在盘**）。下列为字符串级证据，括号内是**文件偏移**（可复算：
        /// 判据会在该偏移处逐字重取一遍）：</para>
        /// <list type="bullet">
        /// <item><c>weapons/usp_silencer_off.wav</c> (0x0e3804) · <c>weapons/usp_silencer_on.wav</c> (0x0e3824)</item>
        /// <item><c>weapons/m4a1_silencer_off.wav</c> (0x0e308c) · <c>weapons/m4a1_silencer_on.wav</c> (0x0e30ac)</item>
        /// <item><c>weapons/famas-burst.wav</c> (0x0e26f4) · <c>#Switch_To_BurstFire</c> (0x0e27d4)</item>
        /// <item><c>+attack2</c> / <c>-attack2</c>（attack2 是**原版真输入通道**，不是本工程发明的）</item>
        /// <item><c>#Cstrike_TitlesTXT_M4A1_Short</c>（客户端对"消音版 M4A1"有独立标题 ⇒ 状态确实存在）</item>
        /// </list>
        /// <para>⇒ 可证的语义只有两句：「**USP / M4A1 有一个消音器 on/off 状态**」「**Glock18 / FAMAS 有可切换的
        /// 连发模式**」，而 attack2 就是切换它们的那条输入。</para>
        ///
        /// <para><b>两态数值的出处</b>（载体 = 原版 <c>原版资源/cs16src/cstrike/dlls/mp.dll</c>，
        /// 逐字节 + 公开源码逐字交叉验证；逐条的 VA/文件偏移/反汇编见
        /// <c>策划/武器右键数值出处.md</c>）：伤害写在武器对象的 <c>+0x148</c>/<c>+0x14c</c> 双字段里、
        /// 开火时按 <c>+0x128</c> 的位（0x04 消音 · 0x02/0x10 连发）二选一；射程修正与静止站散布系数是
        /// <c>PrimaryAttack</c> 内联常量；连发 = 共享的 <c>FireRemaining</c> 上限 3 发 + 每类续发时间戳。
        /// 数值全部落在 <see cref="CsWeaponDef"/> 的新字段上（0 = 该武器没有这一档的出处）。</para>
        /// </summary>
        private static void ApplyAttack2Values()
        {
            void Set(string id, bool silence, bool burst)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.CanSilence = silence;
                def.CanBurst = burst;
            }

            Set(Usp, silence: true, burst: false);
            Set(M4A1, silence: true, burst: false);
            Set(Glock18, silence: false, burst: true);
            Set(Famas, silence: false, burst: true);

            CsWeaponDef Def(string id) => _byId.TryGetValue(id, out var d) ? d : null;

            // USP：装消音只改伤害（34 → 30）；射程修正两态共用 0.79。
            var usp = Def(Usp);
            if (usp != null)
            {
                usp.DamageSilenced = 30;
                usp.RangeModifier = 0.79f;
                usp.RangeModifierSilenced = 0.79f;
                usp.RangeModifierMaxDistance = RangeMaxDistance;
            }

            // M4A1：装消音伤害 32 → 33、射程修正 0.97 → 0.95、静止站散布系数 0.02 → 0.025。
            var m4 = Def(M4A1);
            if (m4 != null)
            {
                m4.DamageSilenced = 33;
                m4.RangeModifier = 0.97f;
                m4.RangeModifierSilenced = 0.95f;
                m4.RangeModifierMaxDistance = RangeMaxDistance;
                m4.StaticSpreadFactor = 0.02f;
                m4.StaticSpreadFactorSilenced = 0.025f;
            }

            // FAMAS：连发伤害 30 → 34、射程修正两态共用 0.96、静止站散布两档同系数 0.02；
            // 连发 = 3 发，首发间隔 0.05 s / 续发 0.1 s，循环 0.55 s（普通档 0.0825 s）。
            var famas = Def(Famas);
            if (famas != null)
            {
                famas.DamageBurst = 34;
                famas.RangeModifier = 0.96f;
                famas.RangeModifierSilenced = 0.96f;
                famas.RangeModifierMaxDistance = RangeMaxDistance;
                famas.StaticSpreadFactor = 0.02f;
                famas.StaticSpreadFactorBurst = 0.02f;
                famas.BurstShots = 3;
                famas.BurstIntervalFirst = 0.05f;
                famas.BurstInterval = 0.1f;
                famas.BurstCycleTime = 0.55f;
                famas.CycleTime = 0.0825f;
            }

            // Glock18：伤害两态同为 25（连发不改伤害）、射程修正两态共用 0.75；
            // 静止站散布系数半自动 0.1 / 连发 0.3（原版是 × (1 − acc) 口径）；
            // 连发 = 3 发，首发/续发均 0.1 s，循环 0.5 s（半自动档 0.15 s）。
            var glock = Def(Glock18);
            if (glock != null)
            {
                glock.RangeModifier = 0.75f;
                glock.RangeModifierSilenced = 0.75f;
                glock.RangeModifierMaxDistance = RangeMaxDistance;
                glock.StaticSpreadFactor = 0.1f;
                glock.StaticSpreadFactorBurst = 0.3f;
                glock.BurstShots = 3;
                glock.BurstIntervalFirst = 0.1f;
                glock.BurstInterval = 0.1f;
                glock.BurstCycleTime = 0.5f;
                glock.CycleTime = 0.15f;
            }
        }

        /// <summary>
        /// 逐武器的**穿墙层数**与**距离衰减**：原版 <c>iPenetration</c> 与
        /// （<c>flRangeModifier</c> + 弹种分母）。
        ///
        /// <para><b>出处 = <c>.ai-tmp/test/mpdll-reverse/PEN-FILL.md</c></b>（载体
        /// <c>原版资源/cs16src/cstrike/dlls/mp.dll</c>）。该文件 §1 表逐把给出开火点 VA、调用者函数
        /// 与类 vtable 槽、push 的 <c>bulletType</c> / <c>iPenetration</c> 原始字节、以及
        /// <c>flRangeModifier</c>；武器归属由两条独立硬链（类代码区间锚点 + MSVC RTTI 恢复的类 vtable
        /// 槽）交叉确证，22 把一致。这 22 把都在这里落值。</para>
        ///
        /// <para><b>弹种分母</b>：9MM=800 / 45ACP=500 / 556MM=4000 / 762MM=5000 / 338MAG=8000 /
        /// 50AE=1000（<c>CsConst.FalloffDenom*</c>）；<c>0xE</c>（5.7mm）= 2000、<c>0xF</c>（.357SIG）= 800
        /// —— 这两格取自 <c>FireBullets3</c> 内部按弹种参数表第 14/15 格（<c>PEN-FILL.md</c> §3 旁证 /
        /// <c>VALUES.md</c> §4.3）：同表 6 个已知弹种（9MM/45ACP/338MAG/762MM/556MM/50AE）逐条吻合
        /// ⇒ **同表内插，非外部估算**，置信度低于逐字节直读（这两格没有独立的第二出处）。
        /// <c>elite</c> 的分母有（9MM）但 <c>flRangeModifier</c> 未取到（PEN-FILL §1 记 "(寄存器)"）
        /// ⇒ 距离衰减走 <see cref="CsWeaponDef.FalloffPerMeter"/> 的线性回落（见 <see cref="DistanceFalloff"/>）。</para>
        ///
        /// <para><b>归属未确证的武器不落值</b>：<c>m3</c> / <c>xm1014</c> 的 <c>PrimaryAttack</c> 内部
        /// 没有 <c>FireBullets3</c> 直接调用（子弹走 ReGameDLL 钩子链派发器 <c>0x10026710</c>，弹种在
        /// 运行期填）⇒ 两把两条轴都留 0、走回落。登记见 <c>策划/差异登记.tsv</c>。</para>
        /// </summary>
        private static void ApplyPenetrationAndFalloffValues()
        {
            void Set(string id, int penetration, float rangeModifier, float falloffDenominator)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.Penetration = penetration;
                def.RangeModifier = rangeModifier;
                def.FalloffDenominator = falloffDenominator;
            }

            // 弹种 9MM（0x1，分母 800）：mp.dll 开火点 0x100DB443 / 0x100E0394 / 0x100E446B / 0x100D7787
            Set(Glock18, 1, 0.75f, CsConst.FalloffDenom9MM);
            Set(Mp5, 1, 0.84f, CsConst.FalloffDenom9MM);
            Set(Tmp, 1, 0.85f, CsConst.FalloffDenom9MM);
            // Elite 的 rangeModifier 未取到（PEN-FILL §1 记 "(寄存器)"）⇒ 距离衰减走回落
            Set(Elite, 1, 0f, CsConst.FalloffDenom9MM);
            // 弹种 45ACP（0x9，分母 500）：0x100E58A3 / 0x100DFB1B / 0x100E4CE0
            Set(Usp, 1, 0.79f, CsConst.FalloffDenom45ACP);
            Set(Mac10, 1, 0.82f, CsConst.FalloffDenom45ACP);
            Set(Ump45, 1, 0.82f, CsConst.FalloffDenom45ACP);
            // 弹种 50AE（0xD，分母 1000）
            Set(Deagle, 2, 0.81f, CsConst.FalloffDenom50AE);
            // 弹种 556MM（0xC，分母 4000）：0x100D81C4 / 0x100DF1D8 / 0x100D4C07 / 0x100E31C7 / 0x100E2943 / 0x100DA7DC / 0x100DDEAB
            Set(Famas, 2, 0.96f, CsConst.FalloffDenom556MM);
            Set(M4A1, 2, 0.97f, CsConst.FalloffDenom556MM);
            Set(Aug, 2, 0.96f, CsConst.FalloffDenom556MM);
            Set(Sg552, 2, 0.955f, CsConst.FalloffDenom556MM);
            Set(Sg550, 2, 0.98f, CsConst.FalloffDenom556MM);
            Set(Galil, 2, 0.98f, CsConst.FalloffDenom556MM);
            Set(M249, 2, 0.97f, CsConst.FalloffDenom556MM);
            // 弹种 762MM（0xB，分母 5000）：0x100D41CB / 0x100E1F89 / 0x100D9F8D
            Set(Ak47, 2, 0.98f, CsConst.FalloffDenom762MM);
            Set(Scout, 3, 0.98f, CsConst.FalloffDenom762MM);
            Set(G3sg1, 3, 0.98f, CsConst.FalloffDenom762MM);
            // 弹种 338MAG（0xA，分母 8000）
            Set(Awp, 3, 0.99f, CsConst.FalloffDenom338MAG);
            // 弹种 0xE（5.7mm）分母 = 2000、弹种 0xF（.357SIG）分母 = 800：取自 FireBullets3 内部
            // 按弹种参数表第 14/15 格（PEN-FILL.md §3 旁证 / VALUES.md §4.3）；同表 6 个已知弹种
            // （9MM/45ACP/338MAG/762MM/556MM/50AE）逐条吻合 ⇒ 同表内插，非外部估算（置信度低于直读）
            Set(P90, 1, 0.885f, CsConst.FalloffDenom57MM);
            Set(FiveSeven, 1, 0.885f, CsConst.FalloffDenom57MM);
            Set(P228, 1, 0.80f, CsConst.FalloffDenom357SIG);
        }

        /// <summary>原版开火时传入的射程距离（世界单位，逐字节：<c>mp.dll</c> <c>0x101422c0</c> = 8192.0）。</summary>
        public const float RangeMaxDistance = 8192f;

        // ==================================================================
        //  差异 #68：两态取值的唯一入口（模拟 / 表现 / 自检共用，避免各写一份分支）
        // ==================================================================
        /// <summary>
        /// 本发的基础伤害：连发档优先（Famas 34），其次消音档（USP 30 / M4A1 33），否则 <c>Damage</c>。
        /// <para>两态参数只在**该武器确有这一档出处**时生效（<c>CanBurst</c>/<c>CanSilence</c> + 字段非 0）——
        /// 角色身上的状态位不随切枪清零，不判武器就可能把上一把枪的模式带到这一把上。</para>
        /// </summary>
        public static int BaseDamage(CsWeaponDef def, bool silenced, bool burst)
        {
            if (def == null) return 0;
            if (burst && def.CanBurst && def.DamageBurst > 0) return def.DamageBurst;
            if (silenced && def.CanSilence && def.DamageSilenced > 0) return def.DamageSilenced;
            return def.Damage;
        }

        /// <summary>本发用的射程修正（<c>flRangeModifier</c>）；0 = 该武器没有出处。</summary>
        public static float RangeModifierFor(CsWeaponDef def, bool silenced)
        {
            if (def == null) return 0f;
            if (silenced && def.CanSilence && def.RangeModifierSilenced > 0f) return def.RangeModifierSilenced;
            return def.RangeModifier;
        }

        /// <summary>
        /// 距离衰减系数（0~1）：<c>pow(rangeModifier, dist_in_units / 弹种分母)</c>。
        ///
        /// <para><b>量纲</b>：<paramref name="distMeters"/> 是米，原版公式里的距离是 GoldSrc unit
        /// ⇒ 先 <c>/ CsConst.UnitToMeter</c> 折成 unit 再进公式（<c>rangeModifier</c> 与分母都是原版量纲，
        /// 不能直接吃米）。</para>
        ///
        /// <para><b>取不到出处时回落旧行为</b>：<c>FalloffDenominator</c> 或 <c>RangeModifier</c>
        /// 为 0（弹种 / 射程修正未取到）的武器走 <see cref="CsWeaponDef.FalloffPerMeter"/> 的线性模型
        /// —— ⛔ 不能返回 1（那是把"未知"当成"已知为零"，会让这些枪的远距伤害反而变高；
        /// 哪些武器缺出处见 <c>策划/差异登记.tsv</c>）。</para>
        ///
        /// <para>消费点 = <c>CsDamage.ApplyHit</c>。</para>
        /// </summary>
        public static float DistanceFalloff(CsWeaponDef def, bool silenced, float distMeters)
        {
            if (def == null) return 1f;
            if (distMeters <= 0f) return 1f;

            if (def.FalloffDenominator <= 0f) return LinearFalloff(def, distMeters);

            var rangeModifier = RangeModifierFor(def, silenced);
            if (rangeModifier <= 0f) return LinearFalloff(def, distMeters);

            var units = distMeters / CsConst.UnitToMeter;
            return (float)System.Math.Pow(rangeModifier, units / def.FalloffDenominator);
        }

        /// <summary>旧的线性衰减模型（每米），用于弹种 / 射程修正未取到的武器。</summary>
        private static float LinearFalloff(CsWeaponDef def, float distMeters)
        {
            var falloff = 1f - def.FalloffPerMeter * distMeters;
            return falloff < 0f ? 0f : falloff;
        }

        /// <summary>
        /// 静止站基础散布：以 <see cref="CsWeaponDef.Spread"/> 为基准，乘上**原版两态系数之比**
        /// （<c>StaticSpreadFactorSilenced|Burst / StaticSpreadFactor</c>）。
        /// <para>为什么只能用比值：原版该系数是 <c>系数 × m_flAccuracy</c> 口径的**无量纲系数**，
        /// 与本工程的"散布（度）"不同量纲 ⇒ 绝对值代进来会把 0.025 当成 0.025 度。比值与量纲无关，
        /// 且"有出处的那一档变差/不变"这件事照样是可判的（M4A1 静止站 ×1.25、Glock18 连发 ×3）。</para>
        /// </summary>
        public static float StaticSpreadFor(CsWeaponDef def, bool silenced, bool burst)
        {
            if (def == null) return 0f;

            var factor = 0f;
            if (burst && def.CanBurst) factor = def.StaticSpreadFactorBurst;
            else if (silenced && def.CanSilence) factor = def.StaticSpreadFactorSilenced;

            if (factor <= 0f || def.StaticSpreadFactor <= 0f) return def.Spread;
            return def.Spread * (factor / def.StaticSpreadFactor);
        }

        /// <summary>连发一轮的发数（非连发模式 / 没出处 ⇒ 0）。</summary>
        public static int BurstShotsFor(CsWeaponDef def, bool burst)
        {
            if (def == null || !burst || !def.CanBurst) return 0;
            return def.BurstShots > 0 ? def.BurstShots : 0;
        }

        /// <summary>连发续发间隔（秒）：<paramref name="followUpIndex"/> 从 1 起（1 = 首发→第 2 发）。</summary>
        public static float BurstIntervalFor(CsWeaponDef def, int followUpIndex)
        {
            if (def == null) return 0f;
            if (followUpIndex <= 1) return def.BurstIntervalFirst;
            return def.BurstInterval > 0f ? def.BurstInterval : def.BurstIntervalFirst;
        }

        /// <summary>本档的循环时间（秒）：连发模式取 <c>BurstCycleTime</c>，否则 <c>CycleTime</c>，都没有则按 <c>Rpm</c> 推。</summary>
        public static float CycleTimeFor(CsWeaponDef def, bool burst)
        {
            if (def == null) return 0f;
            if (burst && def.CanBurst && def.BurstCycleTime > 0f) return def.BurstCycleTime;
            return def.CycleTime > 0f ? def.CycleTime : def.SecondsPerShot;
        }

        public static IReadOnlyList<CsWeaponDef> All => _all;

        /// <summary>按 id 查询；不存在返回 null（调用方必须判空并打日志）。</summary>
        public static CsWeaponDef Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return _byId.TryGetValue(id, out var def) ? def : null;
        }

        /// <summary>按大类列出该阵营可购买的武器（买枪菜单用）。</summary>
        public static List<CsWeaponDef> BuyableByClass(CsWeaponClass cls, CsTeam team)
        {
            var list = new List<CsWeaponDef>();
            foreach (var w in _all)
            {
                if (w.Class != cls) continue;
                if (w.Price <= 0) continue;
                if (w.TeamLimit == CsTeamLimit.TerroristOnly && team != CsTeam.T) continue;
                if (w.TeamLimit == CsTeamLimit.CounterTerroristOnly && team != CsTeam.CT) continue;
                list.Add(w);
            }
            return list;
        }

        /// <summary>取阵营的默认手枪 id（出生装备）。</summary>
        public static string DefaultPistol(CsTeam team) => team == CsTeam.T ? Glock18 : Usp;

        private static CsWeaponDef W(string id, string name, CsWeaponClass cls, CsWeaponSlot slot,
            int price, int dmg, float armorPen, int rpm, int mag, int reserve, float reload,
            float spread, float moveSpread, float recoilV, float recoilH, int pellets,
            float range, float falloff, int killReward, bool auto, CsTeamLimit limit,
            float maxSpeed = 0f)
        {
            return new CsWeaponDef
            {
                Id = id,
                DisplayName = name,
                Class = cls,
                Slot = slot,
                Price = price,
                Damage = dmg,
                ArmorPenetration = armorPen,
                Rpm = rpm,
                Magazine = mag,
                ReserveAmmo = reserve,
                ReloadTime = reload,
                Spread = spread,
                MoveSpread = moveSpread,
                MaxSpeed = maxSpeed,
                RecoilVert = recoilV,
                RecoilHoriz = recoilH,
                Pellets = pellets,
                Range = range,
                FalloffPerMeter = falloff,
                KillReward = killReward,
                AutoFire = auto,
                TeamLimit = limit,
                SoundFire = "sfx/" + id + "_fire",
                SoundReload = "sfx/" + id + "_reload",
            };
        }
    }
}
