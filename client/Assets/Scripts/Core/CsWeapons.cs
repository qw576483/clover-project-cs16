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
    /// <para><b><see cref="CsWeaponDef.Damage"/> / <see cref="CsWeaponDef.ArmorPenetration"/> /
    /// <see cref="CsWeaponDef.Rpm"/> / <see cref="CsWeaponDef.ReloadTime"/> / <see cref="CsWeaponDef.Range"/>
    /// / <see cref="CsWeaponDef.Spread"/> / <see cref="CsWeaponDef.RecoilVert"/> /
    /// <see cref="CsWeaponDef.KillReward"/> 这几组仍无原版出处</b>（原版把它们写死在每把武器类的
    /// <c>PrimaryAttack</c> 浮点立即数里，不是数据表）⇒ 按"未验证"处理，见
    /// <c>策划/对照表.md</c> BLOCKED-2。</para>
    /// </summary>
    public sealed class CsWeaponDef
    {
        public string Id;
        public string DisplayName;
        public CsWeaponClass Class;
        public CsWeaponSlot Slot;          // (CsWeaponSlot)0 = 无槽位（装备）
        public int Price;
        public int Damage;                 // 基础伤害（近距离、无甲）
        public float ArmorPenetration;     // 0~1，越高越无视护甲
        public int Rpm;                    // 每分钟射速（0 = 不可连发）
        public int Magazine;               // 弹匣容量
        public int ReserveAmmo;            // 备弹
        public float ReloadTime;           // 换弹秒数
        public float Spread;               // 静止基础散布（度）
        public float MoveSpread;           // 移动附加散布（度）
        public float RecoilVert;           // 每发垂直后坐力（度）
        public float RecoilHoriz;          // 每发水平后坐力（度）
        public int Pellets;                // 单发弹丸数（霰弹 > 1）
        public float Range;                // 有效射程（米）
        public float FalloffPerMeter;      // 每米伤害衰减系数
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

        // W(...) 的行尾注释 = 该武器在 mp.dll `WeaponInfo[]` 里的条目偏移与三项原版值
        // （格式：`mp.dll:偏移 iCost / iMaxClip / iMaxAmmo`，均照 mp.dll 逐条读出，读法见类注释）。
        private static readonly List<CsWeaponDef> _all = new List<CsWeaponDef>
        {
            // 刀 / 手雷 / 装备 / C4 不在 `WeaponInfo[]` 里（该表只含 24 把枪 + 战术盾 idx25）：
            W(Knife, "Knife", CsWeaponClass.Knife, CsWeaponSlot.Knife, 0, 55, 0.50f, 200, 0, 0, 0f, 0f, 0f, 0f, 0f, 1, 1.6f, 0f, 1500, false, CsTeamLimit.Any),

            W(Glock18, "Glock 18", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 400, 25, 0.47f, 400, 20, 120, 2.2f, 0.60f, 1.50f, 1.00f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.TerroristOnly), // mp.dll:0x10f728 400 / 20 / 120（W-02）
            W(Usp, "USP .45", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 500, 34, 0.50f, 352, 12, 100, 2.2f, 0.50f, 1.40f, 1.10f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f8a8 500 / 12 / 100（W-13）
            W(P228, "P228", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 600, 32, 0.50f, 400, 13, 52, 2.2f, 0.50f, 1.40f, 1.10f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f708 600 / 13 / 52（W-01）
            W(Deagle, "Desert Eagle", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 650, 54, 0.62f, 267, 7, 35, 2.4f, 0.60f, 2.00f, 1.80f, 0.60f, 1, 40f, 0.02f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f9a8 650 / 7 / 35（W-21）
            W(FiveSeven, "Five-SeveN", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 750, 20, 0.60f, 400, 20, 100, 2.2f, 0.50f, 1.40f, 1.00f, 0.40f, 1, 30f, 0.02f, 300, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f808 750 / 20 / 100（W-08）
            W(Elite, "Dual Berettas", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 800, 38, 0.47f, 400, 30, 120, 2.4f, 0.60f, 1.60f, 1.20f, 0.50f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f7e8 800 / 30 / 120（W-07）

            W(Mp5, "MP5 Navy", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1500, 26, 0.52f, 750, 30, 120, 2.6f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.Any), // mp.dll:0x10f8e8 1500 / 30 / 120（W-15）
            W(Tmp, "TMP", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1250, 20, 0.50f, 857, 30, 120, 2.4f, 0.60f, 1.30f, 0.80f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f968 1250 / 30 / 120（W-19）
            W(Mac10, "MAC-10", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1400, 29, 0.48f, 857, 30, 100, 2.6f, 0.60f, 1.40f, 1.00f, 0.50f, 1, 35f, 0.015f, 600, false, CsTeamLimit.TerroristOnly), // mp.dll:0x10f7a8 1400 / 30 / 100（W-05）
            W(Ump45, "UMP-45", CsWeaponClass.SMG, CsWeaponSlot.Primary, 1700, 30, 0.50f, 571, 25, 100, 2.8f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, false, CsTeamLimit.Any), // mp.dll:0x10f828 1700 / 25 / 100（W-09）
            W(P90, "P90", CsWeaponClass.SMG, CsWeaponSlot.Primary, 2350, 21, 0.53f, 857, 50, 100, 3.3f, 0.50f, 1.20f, 0.90f, 0.40f, 1, 40f, 0.015f, 600, true, CsTeamLimit.Any), // mp.dll:0x10fa08 2350 / 50 / 100（W-24）

            W(Galil, "Galil", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2000, 30, 0.70f, 666, 35, 90, 3.0f, 0.50f, 1.60f, 1.30f, 0.50f, 1, 45f, 0.01f, 300, true, CsTeamLimit.TerroristOnly), // mp.dll:0x10f868 2000 / 35 / 90（W-11）
            W(Famas, "FAMAS", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2250, 30, 0.70f, 666, 25, 90, 3.0f, 0.50f, 1.60f, 1.30f, 0.50f, 1, 45f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f888 2250 / 25 / 90（W-12）
            W(Ak47, "AK-47", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 2500, 36, 0.775f, 600, 30, 90, 3.0f, 0.45f, 2.00f, 1.90f, 0.70f, 1, 50f, 0.01f, 300, true, CsTeamLimit.TerroristOnly), // mp.dll:0x10f9e8 2500 / 30 / 90（W-23）
            // 伤害 32 = 原版"未装消音"档（另一档 33 见 ApplyAttack2Values 的 DamageSilenced）。
            W(M4A1, "M4A1", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3100, 32, 0.70f, 666, 30, 90, 3.0f, 0.40f, 1.80f, 1.50f, 0.60f, 1, 50f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f948 3100 / 30 / 90（W-18）
            W(Sg552, "SG-552", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3500, 33, 0.70f, 666, 30, 90, 3.0f, 0.50f, 1.80f, 1.60f, 0.60f, 1, 45f, 0.01f, 300, true, CsTeamLimit.TerroristOnly), // mp.dll:0x10f9c8 3500 / 30 / 90（W-22）
            W(Aug, "AUG", CsWeaponClass.Rifle, CsWeaponSlot.Primary, 3500, 32, 0.70f, 666, 30, 90, 3.0f, 0.50f, 1.80f, 1.60f, 0.60f, 1, 45f, 0.01f, 300, true, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f7c8 3500 / 30 / 90（W-06）

            W(Scout, "Scout", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 2750, 75, 0.85f, 90, 10, 90, 3.0f, 0.20f, 3.00f, 3.00f, 1.00f, 1, 80f, 0.005f, 300, false, CsTeamLimit.Any), // mp.dll:0x10f768 2750 / 10 / 90（W-03）
            W(Awp, "AWP", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 4750, 115, 0.975f, 41, 10, 30, 3.6f, 0.10f, 4.00f, 4.00f, 1.20f, 1, 100f, 0.005f, 100, false, CsTeamLimit.Any), // mp.dll:0x10f8c8 4750 / 10 / 30（W-14）
            W(G3sg1, "G3SG1", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 5000, 80, 0.98f, 240, 20, 90, 4.0f, 0.30f, 3.00f, 2.50f, 1.00f, 1, 90f, 0.005f, 300, false, CsTeamLimit.TerroristOnly), // mp.dll:0x10f988 5000 / 20 / 90（W-20）
            W(Sg550, "SG-550", CsWeaponClass.Sniper, CsWeaponSlot.Primary, 4200, 70, 0.98f, 240, 30, 90, 4.0f, 0.30f, 3.00f, 2.50f, 1.00f, 1, 90f, 0.005f, 300, false, CsTeamLimit.CounterTerroristOnly), // mp.dll:0x10f848 4200 / 30 / 90（W-10）

            W(M3, "M3 Super 90", CsWeaponClass.Shotgun, CsWeaponSlot.Primary, 1700, 22, 0.50f, 85, 8, 32, 3.0f, 3.50f, 1.00f, 3.50f, 1.00f, 9, 15f, 0.05f, 900, false, CsTeamLimit.Any), // mp.dll:0x10f928 1700 / 8 / 32（W-17）
            W(Xm1014, "XM1014", CsWeaponClass.Shotgun, CsWeaponSlot.Primary, 3000, 20, 0.50f, 171, 7, 32, 4.0f, 3.50f, 1.00f, 3.00f, 1.00f, 6, 15f, 0.05f, 900, false, CsTeamLimit.Any), // mp.dll:0x10f788 3000 / 7 / 32（W-04）

            W(M249, "M249", CsWeaponClass.MachineGun, CsWeaponSlot.Primary, 5750, 32, 0.80f, 750, 100, 200, 5.7f, 0.60f, 2.20f, 1.60f, 0.70f, 1, 45f, 0.01f, 300, true, CsTeamLimit.Any), // mp.dll:0x10f908 5750 / 100 / 200（W-16）

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
            float range, float falloff, int killReward, bool auto, CsTeamLimit limit)
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
