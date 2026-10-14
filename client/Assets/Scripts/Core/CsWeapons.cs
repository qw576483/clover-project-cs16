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
    /// <c>iMaxClip=+0x10</c>、<c>iMaxAmmo=+0x14</c>、<c>pszName=+0x1c</c>。</para>
    ///
    /// <para><b>已取到原版出处的几组</b>（载体同 <c>mp.dll</c>，逐条 VA / 反汇编见
    /// <c>策划/手感参数对照.md</c>）：<see cref="CsWeaponDef.ReloadTime"/>（§5.9，21 把）、
    /// <see cref="CsWeaponDef.MaxSpeed"/>（§5.4，逐武器类）、<see cref="CsWeaponDef.CycleTime"/>（§2），
    /// 以及 AK47 / AUG / SG552 / M249 的 <see cref="CsWeaponDef.Damage"/>（§5.2）。
    /// 另有逐武器 <see cref="CsWeaponDef.ArmorRatioScale"/>（护甲比率乘数，出处 = 公开源
    /// <c>regamedll/dlls/player.cpp</c>，见 <see cref="CsWeapons.ApplyArmorRatioValues"/>）。</para>
    ///
    /// <para><b>仍无原版出处的几组</b>：<see cref="CsWeaponDef.ArmorPenetration"/> /
    /// <see cref="CsWeaponDef.Rpm"/> / <see cref="CsWeaponDef.Range"/> / <see cref="CsWeaponDef.Spread"/> /
    /// <see cref="CsWeaponDef.MoveSpread"/> / <see cref="CsWeaponDef.RecoilVert"/> /
    /// <see cref="CsWeaponDef.RecoilHoriz"/> / <see cref="CsWeaponDef.KillReward"/> ——
    /// 原版把它们写死在每把武器类的 <c>PrimaryAttack</c> 浮点立即数里、部分尚未反汇编
    /// ⇒ 按"未验证"处理，见 <c>策划/对照表.md</c> BLOCKED-2。</para>
    ///
    /// <para><b>逐武器穿墙层数与距离衰减</b>：<see cref="CsWeaponDef.Penetration"/>（原版 <c>iPenetration</c>）、
    /// <see cref="CsWeaponDef.RangeModifier"/> 与 <see cref="CsWeaponDef.FalloffDenominator"/>（原版固定 500）
    /// 在**确证了武器归属的全部 24 把**上落值（含霰弹枪的确证"不可穿墙"），落值处见
    /// <c>CsWeapons.ApplyPenetrationAndFalloffValues</c>（出处 <c>.ai-tmp/test/mpdll-reverse/PEN-FILL.md</c>
    /// ＋ 公开源 <c>regamedll/dlls/weapons.h</c> / <c>wpn_shared/*.cpp</c>）。</para>
    ///
    /// <para><b>没有证据的武器按可判定口径回落</b>（⛔ 不是"未知即零"）：<see cref="CsWeaponDef.PenetrationResolved"/>
    /// 为 false 的武器（只有刀 / 装备等没有开火点的存在）走护甲穿透系数判据（见 <c>Firearm.CanPenetrate</c>）；
    /// <see cref="CsWeaponDef.FalloffDenominator"/> 为 0 的武器走 <see cref="CsWeaponDef.FalloffPerMeter"/>
    /// 的线性衰减（见 <c>CsWeapons.DistanceFalloff</c>）。</para>
    /// </summary>
    public sealed class CsWeaponDef
    {
        public string Id;
        public string DisplayName;
        public CsWeaponClass Class;
        public CsWeaponSlot Slot;          // (CsWeaponSlot)0 = 无槽位（装备）
        public int Price;
        public int Damage;                 // 基础伤害（近距离、无甲）

        /// <summary>
        /// 无开火点证据的武器能否**穿墙**的回落判据（**本项目新增**，降级链第 4 级；
        /// 阈值见 <c>CsCombatTuning.PenetrationArmorThreshold</c>），只被 <c>Firearm.CanPenetrate</c> 消费。
        /// 护甲吸收不用本值 —— 那一条走 <see cref="ArmorRatioScale"/>。
        /// </summary>
        public float ArmorPenetration;

        /// <summary>
        /// 原版 <c>CBasePlayer::TakeDamage</c> 里逐武器护甲比率的乘数：<c>flRatio = ARMOR_RATIO × 本值</c>，
        /// 其后 <c>flNew = flRatio × flDamage</c>（<c>flDamage − flNew</c> 即护甲抵掉的伤害）。
        /// <b>1</b> = 该武器不在原版那张表里（<c>switch</c> 落空 ⇒ 伤害被护甲吃掉一半）。
        ///
        /// <para>逐武器取值与出处见 <see cref="CsWeapons.ApplyArmorRatioValues"/>；
        /// 消费点 = <see cref="CsWeapons.ArmorAbsorb"/>。</para>
        /// </summary>
        public float ArmorRatioScale = 1f;

        /// <summary>
        /// 原版 <c>FireBullets3</c> 的 <c>iPenetration</c>：本武器一发能穿过的墙层数。
        /// 只有 <see cref="PenetrationResolved"/> 为 true 时本值才是结论（0 = 确证不可穿墙）。
        ///
        /// <para>出处 = <c>原版资源/cs16src/cstrike/dlls/mp.dll</c> 各武器开火点对 <c>FireBullets3</c>
        /// （VA <c>0x10026420</c>）的第 3 实参；逐武器调用点 VA 见
        /// <c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §4.2 与 <c>PEN-FILL.md</c> §1。</para>
        ///
        /// <para>消费点 = <c>Firearm.ResolvePellet</c>（可穿与否 + 层数上限）。</para>
        /// </summary>
        public int Penetration;

        /// <summary>
        /// <see cref="Penetration"/> 是否已确证（true = 已按原版取证，含"确证为 0 = 不可穿墙"）。
        /// false = 本武器没有开火点证据 ⇒ <c>Firearm.CanPenetrate</c> 按护甲穿透系数回落。
        /// </summary>
        public bool PenetrationResolved;
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
        /// **未装消音器**那一档的开火音（空串 = 该武器开火音不分两态，永远用 <see cref="SoundFire"/>）。
        ///
        /// <para>出处：原版开火函数里"按消音状态在两支采样之间二选一"那一段 —— `client.dll` 文件偏移
        /// <c>0x4a3d</c>（VA <c>0x1904a3d</c>）：<c>test ebx,ebx</c> / <c>je</c>，两支分别
        /// <c>push 0x19e1888</c>（= 串 <c>weapons/usp1.wav</c>）与 <c>push 0x19e1870</c>
        /// （= 串 <c>weapons/usp_unsil-1.wav</c>），两支的浮点实参也不同（2.0 / 0.8）。
        /// 服务端那一侧的名单与状态位见 <c>regamedll/dlls/wpn_shared/wpn_m4a1.cpp:34-36</c>
        /// （<c>m4a1-1.wav</c> 与 <c>m4a1_unsil-1/2.wav</c> 并列 precache）与 <c>:200-201</c>
        /// （把 <c>WPNSTATE_M4A1_SILENCED</c> 当事件参数传出）。取用点 = <see cref="CsWeapons.FireSound"/>。</para>
        /// </summary>
        public string SoundFireUnsilenced = "";

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

        /// <summary>
        /// 差异 #68：attack2 把状态**切到**"装上消音器 / 进入连发"一侧时播的 SFX 短名
        /// （<c>ResPaths.SoundSfxPrefix</c> 后面那一段；空串 = 原版该武器没有这条音，⛔ 不许补）。
        /// 出处 = 原版 <c>cl_dlls/client.dll</c> 的串表（文件偏移见 <see cref="CsWeapons"/> 的
        /// <c>ApplyAttack2Values</c> 出处表）。
        /// </summary>
        public string SoundAttack2On = "";

        /// <summary>差异 #68：attack2 切**回**另一侧（拆下消音器 / 退出连发）时播的 SFX 短名。</summary>
        public string SoundAttack2Off = "";

        // ==================================================================
        //  散布（差异 #94）：原版 `PrimaryAttack` 里内联的三档系数
        //  空中 = SpreadAirAdd + SpreadAirMul × acc ／ 移动>140 = SpreadAirAdd + SpreadMoveMul × acc
        //  ／ 静止 = SpreadStillMul × acc（acc = `m_flAccuracy`，地址 `[this+0xF8]`）。
        //  0 = 该武器这一档没取到（⛔ 不许拿别把枪的值顶替）。
        // ==================================================================

        /// <summary>差异 #94：空中档（`!(pev->flags & FL_ONGROUND)`）的 acc 乘数。</summary>
        public float SpreadAirMul;

        /// <summary>差异 #94：空中档与移动档共用的加数（原版两条分支共用同一个常量）。</summary>
        public float SpreadAirAdd;

        /// <summary>
        /// 差异 #94：**蹲**档（`pev->flags &amp; FL_DUCKING`）的乘数（原版手枪 / 狙击族有这一档，
        /// 步枪族的三档里没有 ⇒ 步枪这一字段为 0）。
        /// </summary>
        public float SpreadDuckMul;

        /// <summary>
        /// 差异 #94：公式形态 —— <c>false</c> = 步枪族「<c>Add + Mul × acc</c>」；
        /// <c>true</c> = 手枪 / 狙击族「<c>Mul × (1 − acc)</c>」（这一族 <see cref="SpreadAirAdd"/> 恒为 0）。
        /// </summary>
        public bool SpreadInverted;

        /// <summary>差异 #94：移动档（`velocity.Length2D() &gt; 140`）的 acc 乘数。</summary>
        public float SpreadMoveMul;

        /// <summary>差异 #94：静止档的 acc 乘数。</summary>
        public float SpreadStillMul;

        /// <summary>差异 #94：静止档在**装消音**状态下的 acc 乘数（只有 M4A1 两者不同；0 = 与未装同）。</summary>
        public float SpreadStillMulSilenced;

        /// <summary>
        /// 差异 #94：公式形态 **3** —— **绝对常量**（原版 `flSpread` 直接就是常量本身，既不乘 acc
        /// 也不乘 `1 − acc`）。AWP / Scout / G3SG1 走这一形态（逐站点原文见
        /// <c>策划/手感参数对照.md</c> §A3.6；`CAWP` 的 `fld [0x10141B90]`=0.85 等）。
        /// <para>⚠️ 原版这个常量是**单位向量横向偏移**（`vecAiming + x·flSpread·v_right + …`），
        /// 本工程"度"口径 ⇒ 消费端按 <c>atan(值)</c> 折角（见 <see cref="SpreadFor"/>）；字段里
        /// 存的是**原版实参原值**，便于与原版逐条对照。</para>
        /// </summary>
        public bool SpreadAbsolute;

        /// <summary>差异 #94：**慢移档**的常量（原版"第二个速度阈值"那一档；只有 AWP 有，= 0.1）。0 = 本武器没有这一档。</summary>
        public float SpreadSlowMul;

        /// <summary>
        /// 差异 #94：**本武器**的"移动档"速度阈值（unit/s）。
        /// <para>原版逐武器不同：AWP = 140、Scout = 170、G3SG1 / SG550 = 0（"只要在动"）；
        /// 既有 16 把一律显式回填 <c>140</c>（= 旧常量 <see cref="SpreadMoveSpeedUnits"/> 的现值，
        /// 行为一字不变）。</para>
        /// </summary>
        public float SpreadMoveSpeedUnits;

        /// <summary>差异 #94：**本武器**的"慢移档"速度阈值（unit/s）；0 = 没有这一档（只有 AWP = 10）。</summary>
        public float SpreadSlowSpeedUnits;

        /// <summary>
        /// 差异 #94：本武器是否有**第二套档位**（原版"四档整体换一套"，不是只换静止档）：
        /// USP 装消音（<see cref="CanSilence"/> + <c>silenced</c>）/ Glock18 连发（<see cref="CanBurst"/> + <c>burst</c>）。
        /// </summary>
        public bool SpreadAltSet;

        /// <summary>差异 #94：第二套档位的四档乘数（含义同 <see cref="SpreadAirMul"/> 一族）。</summary>
        public float SpreadAirMulAlt, SpreadMoveMulAlt, SpreadDuckMulAlt, SpreadStillMulAlt;

        // ==================================================================
        //  差异 #94：acc（原版 `m_flAccuracy`，武器对象 +0xF8）模型
        //  两族：
        //    步枪 / 机枪 / 冲锋枪 —— `acc = min(AccBase + shots^AccPower / AccDiv, AccClampMax)`
        //    手枪 —— `acc = clamp(acc − AccDecayScale × (AccDecayRef − dt), AccClampLo, AccClampHi)`
        //  `shots` = 原版 `m_iShotsFired`（武器对象 +0x100）；`dt` = 距上一次开火的秒数
        //  （原版读上一次开火时刻，未开过火时不动精度）。0 = 该武器没取到 ⇒ 回落旧模型。
        //  出处 = `mp.dll` 各武器类开火函数的浮点常量与钳制分支，逐条 VA 见
        //  `策划/手感参数对照.md` §5.14；抽取工具 `tools/probes/feel-acc-model.py`。
        // ==================================================================

        /// <summary>差异 #94：acc 更新式的加数（原版 `fadd [const]`）。</summary>
        public float AccBase;

        /// <summary>差异 #94：acc 更新式的除数（= <c>1 / fmul 常量</c>）。</summary>
        public float AccDiv;

        /// <summary>差异 #94：<c>m_iShotsFired</c> 的次数 —— 3 = 立方（步枪 / 机枪），2 = 平方（MP5 / P90 / UMP45）。</summary>
        public int AccPower;

        /// <summary>差异 #94：acc 的**上限**钳制（原版 <c>fcom</c> 后写回的那个常量）。</summary>
        public float AccClampMax;

        /// <summary>差异 #94：acc 的复位值 —— 原版在 Spawn / Reset / Deploy 三处槽写进 <c>+0xF8</c> 的常量。</summary>
        public float AccReset;

        /// <summary>差异 #94（手枪族）：衰减系数 K1（原版 <c>fmul</c> 常量）。</summary>
        public float AccDecayScale;

        /// <summary>差异 #94（手枪族）：参考时长 K2（原版 <c>fsubr</c> 常量），衰减式 <c>−K1 × (K2 − dt)</c>。</summary>
        public float AccDecayRef;

        /// <summary>差异 #94（手枪族）：acc 的下限钳制。</summary>
        public float AccClampLo;

        /// <summary>差异 #94（手枪族）：acc 的上限钳制（与 <see cref="AccReset"/> 同值）。</summary>
        public float AccClampHi;

        /// <summary>差异 #94：该武器有 acc 模型（更新式 + 复位值都取到了）。</summary>
        public bool AccModel;

        /// <summary>差异 #94：true = 手枪族（按 <c>dt</c> 衰减），false = 步枪族（按发数累加）。</summary>
        public bool AccTimeDecay;

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

        /// <summary>
        /// 本武器开火时传给 <c>FireBullets3</c> 的**射程距离**（世界单位，不是衰减公式的分母）。
        /// 逐武器取值：手枪那一档是 4096，其余是 8192（<see cref="CsWeapons.RangeMaxDistance"/> /
        /// <see cref="CsWeapons.RangeMaxDistancePistol"/>）；出处 = <c>mp.dll</c> 各开火点的射程实参，
        /// 逐点见 <c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §4.2 的「射程」列。
        /// </summary>
        public float RangeModifierMaxDistance;

        /// <summary>
        /// 距离衰减公式 <c>pow(rangeModifier, dist_in_units / 本值)</c> 的分母 = 原版固定 <c>500</c>
        /// （与弹种无关，出处见 <see cref="CsConst.DamageFalloffDenominator"/>）。
        /// 0 = 该武器不按底数衰减（如霰弹枪的原版链路）⇒ 走 <see cref="FalloffPerMeter"/> 的线性衰减
        /// （见 <see cref="CsWeapons.DistanceFalloff"/>）。
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
        //  attack2 = 开镜：右键 toggle 的瞄具（逐武器）
        //  出处 = 原版 `mp.dll` 的开镜 FOV / 循环时间常量与武器类 `GetMaxSpeed`，
        //  逐把见 `策划/武器右键数值出处.md` §9。
        // ==================================================================

        /// <summary>true = 这把枪的右键是**开镜**（原版该武器类的 <c>SecondaryAttack</c>）。</summary>
        public bool CanZoom;

        /// <summary>开镜**第一档**的水平 FOV（0 = 没有开镜这一档）。</summary>
        public float ZoomFov;

        /// <summary>
        /// 开镜**第二档**的水平 FOV（0 = 这把枪只有一档，按一下开、按一下关）。
        /// 逐把：狙击四把 = AWP <c>10</c> / Scout·SG550·G3SG1 <c>15</c>；AUG·SG552 = 0（一档）。
        /// </summary>
        public float ZoomFovSecond;

        /// <summary>开镜时的循环时间（秒）；0 = 原版该枪的射速不随开镜变化。</summary>
        public float CycleTimeZoom;

        /// <summary>
        /// 开镜时的最大移动速度（m/s）；0 = 原版该枪开镜**不降速**
        /// （<c>GetMaxSpeed</c> 是内联常量、没有 zoom 档）。
        /// </summary>
        public float MaxSpeedZoom;

        /// <summary>
        /// true = 换弹开始时若已开镜就退出开镜（原版 <c>Reload()</c> 里那句
        /// <c>if (m_iFOV != DEFAULT_FOV) SecondaryAttack();</c>，只在 AUG / SG552 上）。
        /// </summary>
        public bool UnzoomOnReload;

        /// <summary>开镜动作音的 SFX 短名（"" = 原版这把枪开镜没有音）。</summary>
        public string SoundZoom = "";

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

        /// <summary>
        /// 击杀条要画的**原版死亡图标名**（= <c>sprites/hud.txt</c> 640 档的 <c>d_*</c> 条目名；
        /// 图标本体见 <see cref="ResPaths.KillIcon"/>）。没有对应图标时返回 null（调用方回退文字）。
        ///
        /// <para><b>映射依据</b>：<c>原版资源/cs16src/cstrike/cstrike__sprites__hud.txt</c> 640 档的
        /// <c>d_*</c> 全表（30 条）。其中 <c>mp5</c> 在原版叫 <c>d_mp5navy</c>；
        /// 手雷家族只有 <c>d_grenade</c>（通用）与 <c>d_flashbang</c> 两张 ⇒ HE / 烟雾都用 <c>d_grenade</c>
        /// （hud.txt 无 <c>d_hegrenade</c> / <c>d_smokegrenade</c>）；
        /// <b>hud.txt 无 <c>d_c4</c></b> ⇒ C4 击杀返回 null（回退文字，不造图标）。</para>
        /// </summary>
        public static string DeathIconId(string weaponId)
        {
            switch (weaponId)
            {
                case Knife: return "d_knife";
                case Glock18: return "d_glock18";
                case Usp: return "d_usp";
                case P228: return "d_p228";
                case Deagle: return "d_deagle";
                case FiveSeven: return "d_fiveseven";
                case Elite: return "d_elite";
                case Mp5: return "d_mp5navy";
                case Tmp: return "d_tmp";
                case Mac10: return "d_mac10";
                case Ump45: return "d_ump45";
                case P90: return "d_p90";
                case Galil: return "d_galil";
                case Famas: return "d_famas";
                case Ak47: return "d_ak47";
                case M4A1: return "d_m4a1";
                case Sg552: return "d_sg552";
                case Aug: return "d_aug";
                case Scout: return "d_scout";
                case Awp: return "d_awp";
                case G3sg1: return "d_g3sg1";
                case Sg550: return "d_sg550";
                case M3: return "d_m3";
                case Xm1014: return "d_xm1014";
                case M249: return "d_m249";
                case HeGrenade: return "d_grenade";
                case SmokeGrenade: return "d_grenade";
                case Flashbang: return "d_flashbang";
                default: return null;
            }
        }

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
        private const float Spd150 = 3.81f;     // AWP / SG550 / G3SG1 的**开镜**档（150 unit/s）
        private const float Spd200 = 5.08f;     // SG552 的开镜档（200 unit/s）

        // 开镜（attack2 = 瞄具）共用常量。出处逐条见 `ApplyZoomValues` 的注释。
        private const float ZoomFovAug = 55f;    // AUG / SG552 开镜后的水平 FOV
        private const float CycleTimeZoom = 0.135f;    // AUG / SG552 开镜时的循环时间
        private const string ZoomSound = "sfx/zoom";    // 狙击四把的开镜音

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
            // 伤害 36 = 该类 `Spawn()` 里 `fstp dword [eax+0x4838]` 的源常量：mp.dll VA 0x100D71EA
            // `d9 05 20 21 14 10`（fld dword [0x10142120] = 36.0f，.rdata file 0x140D20）→ VA 0x100D71FA
            // `d9 98 38 48 00 00`；开火点 0x100D7787 / 0x100D787B 按 [player+0x4838] 取伤害
            // （两把枪各一发、各 36）。全 mp.dll 只有两个引用点：0x100D3D4A（AK47 同值 36）。
            W(Elite, "Dual Berettas", CsWeaponClass.Pistol, CsWeaponSlot.Secondary, 800, 36, 0.47f, 400, 30, 120, 4.5f, 0.60f, 1.60f, 1.20f, 0.50f, 1, 30f, 0.02f, 300, false, CsTeamLimit.Any, Spd250), // mp.dll:0x10f7e8 800 / 30 / 120（W-07）

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
            ApplyZoomValues();
            ApplyPenetrationAndFalloffValues();
            ApplyArmorRatioValues();
            ApplyCycleTimes();
            ApplyKickbackValues();
            ApplySpreadValues();
            ApplyAccuracyValues();
        }

        /// <summary>
        /// 逐武器**精度（acc）模型**（差异 #94）。出处 = 各武器类的开火函数（vtable `slot[87]` 内被
        /// <c>call</c> ≥2 次的正文目标）里对 <c>[this+0xF8]</c> 的写入，逐条 VA 见
        /// <c>策划/手感参数对照.md</c> §5.14（抽取工具 <c>tools/probes/feel-acc-model.py</c>）。
        ///
        /// <para><b>步枪 / 机枪 / 冲锋枪</b>（立方 / 平方 + 上限钳制）：AK47 0.35+shots³/200≤1.25 ·
        /// Galil 0.35+shots³/200≤1.25 · AUG 0.3+shots³/215≤1 · FAMAS 0.3+shots³/215≤1 ·
        /// M4A1 0.3+shots³/220≤1 · SG552 0.3+shots³/220≤1 · M249 0.4+shots³/175≤0.9 ·
        /// MAC10 0.6+shots³/200≤1.65 · TMP 0.55+shots³/200≤1.4 ·
        /// MP5 0.45+shots²×0.00454339≤0.75 · P90 0.45+shots²/175≤1 · UMP45 0.5+shots²/210≤1。</para>
        ///
        /// <para><b>手枪</b>（按 <c>dt</c> 衰减 + 上下钳制）：Deagle · Elite · FiveSeven · Glock18 ·
        /// P228 · USP。</para>
        ///
        /// <para><b>逐武器的复位值</b>（原版 Spawn / Reset / Deploy 三处槽写进 <c>+0xF8</c> 的常量）：
        /// 步枪 / 机枪 / 冲锋枪 0.2（MAC10 0.15）；手枪 0.88~0.92；SG550 0.9。
        /// **AWP / Scout / M3 / XM1014 取不到**（开火函数里没有 <c>+0xF8</c>/<c>+0x100</c> 访问）⇒
        /// 复位值留 0 ⇒ 该武器回落旧散布模型。</para>
        /// </summary>
        private static void ApplyAccuracyValues()
        {
            // 步枪 / 机枪 / 冲锋枪：acc = base + shots^power / div，再钳到 ≤ clampMax。
            void SetRifle(string id, float basev, float div, int power, float clampMax, float reset)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.AccBase = basev;
                def.AccDiv = div;
                def.AccPower = power;
                def.AccClampMax = clampMax;
                def.AccReset = reset;
                def.AccModel = true;
                def.AccTimeDecay = false;
            }

            SetRifle(Ak47, 0.35f, 200f, 3, 1.25f, 0.2f);      // 0x100D4064 写 / fmul 0.005@0x101419A8 + fadd 0.35@0x10141AB0 / fld 1.25@0x10141CD8
            SetRifle(Galil, 0.35f, 200f, 3, 1.25f, 0.2f);     // 0x100DA675
            SetRifle(Aug, 0.3f, 215f, 3, 1f, 0.2f);           // 0x100D4A84 / 0.00465116@0x101419A0 / fld1
            SetRifle(Famas, 0.3f, 215f, 3, 1f, 0.2f);         // 0x100D8035
            SetRifle(M4A1, 0.3f, 220f, 3, 1f, 0.2f);          // 0x100DF004 / 0.00454545@0x1014199C
            SetRifle(Sg552, 0.3f, 220f, 3, 1f, 0.2f);         // 0x100E3044
            SetRifle(M249, 0.4f, 175f, 3, 0.9f, 0.2f);        // 0x100DDD24 / 0.00571429@0x101419AC
            SetRifle(Mac10, 0.6f, 200f, 3, 1.65f, 0.15f);     // 0x100DF994 / fld 1.65@0x10141DE8
            SetRifle(Tmp, 0.55f, 200f, 3, 1.4f, 0.2f);        // 0x100E4304 / fld 1.4@0x10141D20
            SetRifle(Mp5, 0.45f, 1f / 0.00454339f, 2, 0.75f, 0f);  // 0x100E022F / 单条 imul（平方）/ 0.00454339@0x10141998 · Spawn 0x100DFF5E fldz 写 0
            SetRifle(P90, 0.45f, 175f, 2, 1f, 0.2f);          // 0x100E13DF
            SetRifle(Ump45, 0.5f, 210f, 2, 1f, 0f);           // 0x100E4B7F / 0.0047619@0x101419A4 · Spawn 0x100E48BC fldz 写 0

            // 手枪族：acc = clamp(acc − K1 × (K2 − dt), lo, hi)；复位值 = 上限（原版三处槽写的就是上限常量）。
            void SetPistol(string id, float k1, float k2, float lo, float hi)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.AccDecayScale = k1;
                def.AccDecayRef = k2;
                def.AccClampLo = lo;
                def.AccClampHi = hi;
                def.AccReset = hi;
                def.AccModel = true;
                def.AccTimeDecay = true;
            }

            SetPistol(Deagle, 0.35f, 0.4f, 0.55f, 0.9f);      // 0x100D6C18 / fmul 0.35@0x10141AB0 fsubr 0.4@0x10141AC0 / fld 0.9@0x10141BC8 / fld 0.55@0x10141AD8
            SetPistol(Elite, 0.275f, 0.325f, 0.55f, 0.88f);   // 0x100D755D / fld 0.88@0x10141BA4
            SetPistol(FiveSeven, 0.25f, 0.275f, 0.725f, 0.92f); // 0x100D8948 / fld 0.725@0x10141B18
            SetPistol(Glock18, 0.275f, 0.325f, 0.6f, 0.9f);   // 0x100DB299
            SetPistol(P228, 0.3f, 0.325f, 0.6f, 0.9f);        // 0x100E0B98
            SetPistol(Usp, 0.275f, 0.3f, 0.6f, 0.92f);        // 0x100E56B8

            // SG550 有 acc 写入（0x100E27C2）但函数体里没有 shots 访问 ⇒ 只有复位值可落，逐发更新式不落。
            if (_byId.TryGetValue(Sg550, out var sg550))
            {
                sg550.AccReset = 0.9f;                        // 0x10141BC8，slot0 / slot64 两处写入
                sg550.AccClampMax = 0f;
            }
        }

        /// <summary>
        /// 逐武器**循环时间**（射速两发之间的最短间隔，秒）的原版值。
        ///
        /// <para>出处 = <c>mp.dll</c>（基址 <c>0x10000000</c>）各武器类开火函数里置
        /// <c>m_flNextPrimaryAttack</c> 的那组指令：签名 = <c>fstp dword ptr [reg+0x13c]</c>
        /// 紧跟 <c>fld dword ptr [&lt;常量VA&gt;]</c>，随后 <c>fst [reg+0x138]</c> /
        /// <c>fst [reg+0xbc]</c> / <c>fstp [reg+0xb8]</c>。逐把的常量 VA / 读点 VA / 原始字节见各行注释。</para>
        ///
        /// <para>Glock18 是唯一例外：读点给出的是 <c>flCycleTime</c> 实参 <c>0.2</c>，
        /// 共享函数 <c>GLOCK18Fire</c> 内再 <c>fsub [0x10141A08]=0.05</c> ⇒ 有效值 <c>0.15</c>。</para>
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

            // 每行出处 = 该武器类开火函数里置 `m_flNextPrimaryAttack` 那一组指令中的
            // `fld dword ptr [<常量VA>]` 读点（签名 = 紧跟在 `fstp [reg+0x13c]` 之后、
            // 随后 `fst [reg+0x138]` / `fst [reg+0xbc]` / `fstp [reg+0xb8]`）。
            // 刀：`CKnife` 的挥击函数 `0x100DC820`（slot[87] `0x100DC810`）里
            // `fld dword ptr [0x10141AC0]` @0x100DC939（字节 `d9 05 c0 1a 14 10`）= `0.4`。
            Set(Knife, 0.4f);
            Set(Ak47, 0.0955f);       // fld [0x10141A34] @0x100D4276（d9 05 34 1a 14 10）
            Set(Aug, 0.0825f);        // fld [0x10141A24] @0x100D496E（同族：开镜档 0.135 @0x100D4976）
            Set(Awp, 1.45f);          // fld [0x10141D60] @0x100D57B6（d9 05 60 1d 14 10）
            Set(Deagle, 0.225f);      // fld [0x10141A74] @0x100D6E58（d9 05 74 1a 14 10）
            Set(Elite, 0.122f);       // fld [0x10141A3C] @0x100D7608（d9 05 3c 1a 14 10）
            Set(Famas, 0.0825f);      // fld [0x10141A24] @0x100D7FC6
            Set(FiveSeven, 0.15f);    // fld [0x10141A50] @0x100D8B88
            Set(G3sg1, 0.25f);        // fld [0x10141A7C] @0x100DA00D
            Set(Galil, 0.0875f);      // fld [0x10141A2C] @0x100DA8AF
            // Glock18 = 实参 0.2 − 0.05：`fld [0x10141A60]` @0x100DB155（d9 05 60 1a 14 10）
            //            + `fsub [0x10141A08]` @0x100DB261（d8 25 08 1a 14 10）
            Set(Glock18, 0.15f);
            Set(M249, 0.1f);          // fld [0x10141A38] @0x100DDF36
            Set(M4A1, 0.0875f);       // fld [0x10141A2C] @0x100DF27E（d9 05 2c 1a 14 10）
            Set(Mac10, 0.07f);        // fld [0x10141A18] @0x100DFBA6
            Set(Mp5, 0.075f);         // fld [0x10141A1C] @0x100E043F
            Set(P228, 0.15f);         // fld [0x10141A50] @0x100E0DD8
            Set(P90, 0.066f);         // fld [0x10141A14] @0x100E15EB
            Set(Scout, 1.25f);        // fld [0x10141CD8] @0x100E200B
            Set(Sg550, 0.25f);        // fld [0x10141A7C] @0x100E29C3
            Set(Sg552, 0.0825f);      // fld [0x10141A24] @0x100E2F2E
            Set(Tmp, 0.07f);          // fld [0x10141A18] @0x100E44F6
            Set(Ump45, 0.1f);         // fld [0x10141A38] @0x100E4D8B
            Set(Usp, 0.15f);          // fld [0x10141A4C] @0x100E5763（d9 05 4c 1a 14 10）
            Set(M3, 0.875f);          // fld [0x10141BA0] @0x100DE732（d9 05 a0 1b 14 10）
            Set(Xm1014, 0.25f);       // fld [0x10141A7C] @0x100E60E7
        }

        /// <summary>
        /// 逐武器**后坐力 kickback 实参**（原版 6 个 float + 1 个方向翻转除数）。
        ///
        /// <para><b>出处 = <c>策划/手感参数对照.md</c> §5.5 / §5.8 + §5.11</b>（mp.dll 各武器
        /// <c>fire</c> 函数里对 <c>0x10097F10</c> 的调用点；逐把的调用点 VA 与常量 VA 都列在那两节，
        /// 归属见 §5.11）。这里的 21 把是**归属确证且参数齐全**的那些。</para>
        ///
        /// <para><b>为什么不在这里的武器</b>：</para>
        /// <list type="bullet">
        /// <item>G3SG1 / SG550 —— <c>lateral_base</c> 那一槽的写入指令 pop 自空的 x87 栈
        /// （<c>0x100DA0A8</c> / <c>0x100E2A5E</c>：<c>fldz</c> + <c>fst</c>×3 + <c>fstp</c> 之后
        /// 还有一条 <c>fstp [esp+4]</c>），寄存器里没有可用值 ⇒ 缺槽不猜；</item>
        /// <item>其余武器（刀 / C4 / 手雷 / 弹壳等）—— 原版 attack2 与 <c>fire</c> 里根本没有那条调用。</item>
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
            Set(Famas, 1f, 0.45f, 0.275f, 0.05f, 4f, 2.5f, 7);

            // 手枪 / 狙击 / 霰弹：原版只给 up_base，其余 5 个槽是 0、方向翻转 0
            // （`fldz` 连续 `fst` 把 0 写进 5 个槽，最后一个 `fstp` 弹出的是残留的 up_base；
            //  逐站点原文见 §5.8）。归属由 §5.11 的"与 FireBullets3 开火点同一函数"链确证。
            Set(Deagle, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(Awp, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(Scout, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(Usp, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(P228, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(FiveSeven, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(Elite, 2f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(M3, 2.5f, 0f, 0f, 0f, 0f, 0f, 0);
            Set(Xm1014, 0.75f, 0f, 0f, 0f, 0f, 0f, 0);

            // Glock18：7 个实参全是 0。站点 `0x100DB52E` `fldz` → `0x100DB533..0x100DB547`
            // 连续 5 条 `fst` + 1 条 `fstp`（6 个槽各写 0，x87 栈只弹 1 次、恰好平衡，
            // 不存在"残留 st(1)"）→ `push 0`（方向位）→ `call 0x10097F10`。
            Set(Glock18, 0f, 0f, 0f, 0f, 0f, 0f, 0);
        }

        /// <summary>
        /// 逐武器**散布三档系数**（差异 #94）。出处 = `mp.dll` 各武器类 vtable `slot[87]`（=
        /// `PrimaryAttack`）里"`fld [this+0xF8]` → `fmul &lt;常量&gt;` →（空中/移动档）`fadd &lt;常量&gt;`
        /// → `fstp [esp]` → `call &lt;开火函数&gt;`"那几段；逐站点 VA 与常量见
        /// <c>策划/手感参数对照.md</c> §5.12，抽取工具 <c>tools/probes/feel-spread-args.py</c>。
        ///
        /// <para><b>只落已确证的那一档族</b>：模板（空中 / 移动&gt;140 / 静止 三档、空中与移动共用加数）
        /// 由 `CAK47` 的全量反汇编手写确证（`0x100D3F60`：`test [eax+0x1A4],0x200` → 空中
        /// `0.4*acc+0.04`；`fcomp 140` 后 → 移动 `0.07*acc+0.04`；否则静止 `0.0275*acc`），
        /// 且与 §4.2 的 M4A1 / FAMAS 公开源逐条一致。手枪族另有一套模板（`Mul × (1 − acc)`、
        /// **四档**含蹲），由 `CDEAGLE` 的全量反汇编手写确证。其余武器（冲锋枪 2 站点、AWP/Scout/
        /// G3SG1/SG550 的 5 站点、USP/Glock18 的 8 站点 = 两棵带状态位的树）**档位语义未确证** ⇒ 不落值。</para>
        /// </summary>
        private static void ApplySpreadValues()
        {
            void Set(string id, float airMul, float add, float moveMul, float stillMul, float stillSilenced = 0f)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.SpreadAirMul = airMul;
                def.SpreadAirAdd = add;
                def.SpreadMoveMul = moveMul;
                def.SpreadStillMul = stillMul;
                def.SpreadStillMulSilenced = stillSilenced;
            }

            Set(Ak47, 0.4f, 0.04f, 0.07f, 0.0275f);
            Set(Galil, 0.3f, 0.04f, 0.07f, 0.0375f);
            Set(Aug, 0.4f, 0.035f, 0.07f, 0.02f);
            Set(Sg552, 0.45f, 0.035f, 0.075f, 0.02f);
            Set(Famas, 0.3f, 0.03f, 0.07f, 0.02f);
            Set(M249, 0.5f, 0.045f, 0.095f, 0.03f);
            // M4A1：静止档两态不同（未装 0.02 / 装消音 0.025），空中与移动档两态相同。
            Set(M4A1, 0.4f, 0.035f, 0.07f, 0.02f, 0.025f);

            // 手枪族：`Mul × (1 − acc)`，**四档**（空中 / 移动 / 蹲 / 站）。锚点 = CDEAGLE 的全量反汇编
            // （`0x100D6AF7` 空中 ×1.5 → `0x100D6B40` 移动 ×0.25 → `test edx,0x4000` 后
            // `0x100D6B5A` 蹲 ×0.115 / 落空路径 `0x100D6B6C` 站 ×0.13）⇒ 常量出现顺序即档位顺序。
            void SetInv(string id, float airMul, float moveMul, float duckMul, float stillMul,
                        float moveUnits = 140f)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.SpreadInverted = true;
                def.SpreadAirMul = airMul;
                def.SpreadAirAdd = 0f;
                def.SpreadMoveMul = moveMul;
                def.SpreadDuckMul = duckMul;
                def.SpreadStillMul = stillMul;
                // 差异 #94：`moveUnits` 默认 140 = 旧常量的现值 ⇒ 既有 4 把手枪一字不动。
                // USP / Glock18 / SG550 的原版判据是 `velocity.Length2D() > 0`（阈值 0）⇒ 显式传 0。
                def.SpreadMoveSpeedUnits = moveUnits;
            }

            // 差异 #94 形态 3：**绝对常量**（AWP / Scout / G3SG1）—— 原版 `flSpread` 直接就是常量、
            // 不乘 acc。常量 = 原版实参原值（消费端折 `atan`，见 CsWeapons.SpreadFor）。逐站点 VA 见 §A3.6。
            void SetAbs(string id, float air, float move, float slowMul, float duck, float still,
                        float moveUnits, float slowUnits)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.SpreadAbsolute = true;
                def.SpreadAirMul = air;
                def.SpreadAirAdd = 0f;
                def.SpreadMoveMul = move;
                def.SpreadSlowMul = slowMul;
                def.SpreadDuckMul = duck;
                def.SpreadStillMul = still;
                def.SpreadMoveSpeedUnits = moveUnits;
                def.SpreadSlowSpeedUnits = slowUnits;
            }

            // 差异 #94：**第二套档位**（USP 装消音 / Glock18 连发）—— 原版"四档整体换一套"。
            void SetInvAlt(string id, float airMul, float moveMul, float duckMul, float stillMul)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.SpreadAltSet = true;
                def.SpreadAirMulAlt = airMul;
                def.SpreadMoveMulAlt = moveMul;
                def.SpreadDuckMulAlt = duckMul;
                def.SpreadStillMulAlt = stillMul;
            }

            SetInv(Deagle, 1.5f, 0.25f, 0.115f, 0.13f);
            SetInv(P228, 1.5f, 0.255f, 0.075f, 0.15f);
            SetInv(FiveSeven, 1.5f, 0.255f, 0.075f, 0.15f);

            // 差异 #94（本轮新落）：USP 两态四档。原版 `CUSP::PrimaryAttack`（§A3.6）：
            // `100E54F0 test byte [ecx+0x128], 1`（= `WPNSTATE_USP_SILENCED`）→ `je` ⇒ **bit0 置位 = 装消音**。
            // 阈值 `fcomp [0x1014196c]` = 0 ⇒ 只要在动就算移动档。
            SetInv(Usp, 1.2f, 0.225f, 0.08f, 0.1f, moveUnits: 0f);      // 未装消音（bit0 清零）
            SetInvAlt(Usp, 1.3f, 0.25f, 0.125f, 0.15f);                 // 装消音（bit0 置位）
            // 差异 #94（本轮新落）：Glock18 两态四档（`WPNSTATE_GLOCK18_BURST_MODE` = `[+0x128]` bit1）。
            // 源码原件见 `原版资源/cs16src/regamedll/dlls/wpn_shared/wpn_glock18.cpp:114-154`。
            SetInv(Glock18, 1f, 0.165f, 0.075f, 0.1f, moveUnits: 0f);   // 半自动态（bit1 清零）
            SetInvAlt(Glock18, 1.2f, 0.185f, 0.095f, 0.3f);             // 连发态（bit1 置位）

            // 差异 #94（本轮新落）：4 把狙击 —— 形态 3「绝对常量」3 把（AWP / Scout / G3SG1），
            // 逐站点原文（VA + `fld`/`fldz` + `fcomp` 阈值）见 `策划/手感参数对照.md` §A3.6：
            //   AWP   0.85 / 0.25 / 0.1 / 0.0(fldz) / 0.001，阈值 140 然后 10（**5 档**）
            //   Scout 0.2  / 0.075 / —   / 0.0(fldz) / 0.007，阈值 170
            //   G3SG1 0.45 / 0.15  / —   / 0.035     / 0.055，阈值 0（= `Length2D() > 0`）
            SetAbs(Awp, 0.85f, 0.25f, 0.1f, 0f, 0.001f, 140f, 10f);
            SetAbs(Scout, 0.2f, 0.075f, 0f, 0f, 0.007f, 170f, 0f);
            SetAbs(G3sg1, 0.45f, 0.15f, 0f, 0.035f, 0.055f, 0f, 0f);
            // 差异 #94（本轮新落）：**霰弹两把 —— 原版没有档位树**，`flSpread` 是一个**固定锥体向量**
            // `M3_CONE_VECTOR = Vector(0.0675, 0.0675, 0.0)`（`wpn_m3.cpp:3`，另有 `FireBuckshots(9, …, 3000)`）；
            // XM1014 = `(0.0725, 0.0725, 0)` / 6 弹丸 / 3048（`100E5FD9 mov eax,[0x10166C8C]` 逐条见 §A3.6-4）。
            // ⇒ 落法同绝对档族：**四个档位都填同一个"单位向量偏移"常量**（消费端折 `atan` 后 = 恒定的
            //   3.86° / 4.15° 半角），移动阈值 0（原版 `Length2D() > 0`）。
            //   四档同值 ⇒ 原版"不随移动 / 空中 / acc 变"的行为自动成立；且进入原版档位路径后
            //   `Firearm.ComputeSpread` **不会再叠 `MoveSpread`**（原版没有那层，正是 lead 批的口径）。
            SetAbs(M3, 0.0675f, 0.0675f, 0.0675f, 0.0675f, 0.0675f, 0f, 0f);
            SetAbs(Xm1014, 0.0725f, 0.0725f, 0.0725f, 0.0725f, 0.0725f, 0f, 0f);
            // ⛔ SG550（0.45 / 0.15 / 0.04 / 0.05，阈值 0，形态 2 `(1−acc)×`）**本轮不落**：
            //    它是 `(1−acc)` 形态 ⇒ 必须有 acc 模型才成立，而 `ApplyAccValues` 只给它落了
            //    `AccReset`、没落逐发更新式（同文件注释「只有复位值可落」）⇒ 现在落值会被
            //    `ResetAccuracy` = 0 带成 10× 偏差。先补 SG550 的 acc 模型，再落它的四档。
            SetInv(Elite, 1.3f, 0.175f, 0.08f, 0.1f);

            // 冲锋枪族：`Mul × acc`，**只有两档**（空中 / 地面，无移动档、无加数）。锚点 = CMAC10 的全量反汇编
            // （`0x100DF8DC test [eax+0x1A4],0x200` → 空中 `fld acc; fmul 0.375`；落空路径 `fld acc; fmul 0.03`
            //  —— 函数里**没有** 与 140 的比较 ⇒ 两档）。
            void SetGround(string id, float airMul, float groundMul)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.SpreadAirMul = airMul;
                def.SpreadAirAdd = 0f;
                def.SpreadMoveMul = 0f;
                def.SpreadStillMul = groundMul;
            }

            SetGround(Mac10, 0.375f, 0.03f);
            SetGround(Mp5, 0.2f, 0.04f);
            SetGround(Tmp, 0.25f, 0.03f);
            SetGround(Ump45, 0.24f, 0.04f);
            // P90 三档（同样不含加数）：空中 0.3 / 移动>140 0.115 / 静止 0.045（含与 140 的比较）。
            Set(P90, 0.3f, 0f, 0.115f, 0.045f);
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
        /// <para><b>切换音</b>：上面那 4 条 `*_silencer_on|off.wav` 与 `famas-burst.wav` 就是 attack2 的
        /// **切换动作音**（它们与 HUD 提示串 <c>#Switch_To_BurstFire</c> 同段 ⇒ 属客户端 / 视模型层，
        /// 不是开火音 —— 开火音走 `<c>weapons/&lt;枪&gt;-1.wav</c>` 那一族命名）。
        /// 落进工程的 5 个文件在 <c>Resources/Sound/SFX/sfx/</c>，短名见
        /// <see cref="CsWeaponDef.SoundAttack2On"/> / <see cref="CsWeaponDef.SoundAttack2Off"/>；
        /// 消费点 = <c>CombatModule.FillInput</c>（状态位由模拟侧同一帧翻）。</para>
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

            // USP：装消音只改伤害（34 → 30）；射程修正两态共用 0.79；
            // 开火点 0x100E58A3 的射程距离是 4096（手枪那一档，差异 #129）。
            var usp = Def(Usp);
            if (usp != null)
            {
                usp.DamageSilenced = 30;
                usp.RangeModifier = 0.79f;
                usp.RangeModifierSilenced = 0.79f;
                usp.RangeModifierMaxDistance = RangeMaxDistancePistol;
                usp.SoundAttack2On = "sfx/usp_silencer_on";
                usp.SoundAttack2Off = "sfx/usp_silencer_off";
                // 未装消音档的开火音（原版 client.dll 0x4a3d 的二选一里那一支 `weapons/usp_unsil-1.wav`）。
                usp.SoundFireUnsilenced = "sfx/usp_fire_unsil";
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
                m4.SoundAttack2On = "sfx/m4a1_silencer_on";
                m4.SoundAttack2Off = "sfx/m4a1_silencer_off";
                // 未装消音档的开火音（出处同 USP；原版同一段 precache 里还有 `m4a1_unsil-2.wav`，
                // 它的取用点未反汇编 ⇒ 本工程只接第 1 支）。
                m4.SoundFireUnsilenced = "sfx/m4a1_fire_unsil";
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
                // 原版只有一条连发切换音（不是 on/off 两条）⇒ 两个方向同一个短名。
                famas.SoundAttack2On = "sfx/famas-burst";
                famas.SoundAttack2Off = "sfx/famas-burst";
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
                // ← 连发切换音留空：原版 `client.dll` 的串表里只有 FAMAS 那一条（`weapons/famas-burst.wav`），
                //   Glock18 没有对应的音效串 ⇒ 切模式静音（⛔ 不许拿 Famas 的顶替）。
            }
        }

        /// <summary>
        /// 逐武器的**开镜**档（右键 = 瞄具）——原版该武器类 <c>SecondaryAttack</c> 的全部可观测后果。
        ///
        /// <para><b>开镜 FOV</b>：原版 <c>mp.dll</c> 那个共用的开镜分支 —
        /// <c>cmp dword ptr [eax+0x5ac], 0x5A</c>（90 = <c>default_fov</c>），
        /// 相等则 <c>fld 55.0</c> 并写 <c>0x37</c>（55）回 <c>[player+0x5ac]</c>，
        /// 否则 <c>fld 90.0</c> 写回 <c>0x5A</c>；读点 VA <c>0x100d48e2</c>，
        /// 常量 <c>0x10142140</c> = 55.0f / <c>0x10142184</c> = 90.0f（<c>.rdata</c> 文件偏移
        /// <c>0x140D40</c> / <c>0x140D84</c>）。全 <c>mp.dll</c> 只有这一处读 55.0f ⇒ 它就是
        /// AUG 与 SG552 共用的开镜分支。狙击四把的两档 FOV（40 / AWP 10 / 其余 15）见
        /// <c>CsConst.ZoomFov</c> 的出处。</para>
        ///
        /// <para><b>开镜射速</b>：AUG / SG552 的 <c>PrimaryAttack</c> 里
        /// <c>flCycleTime = (pev-&gt;fov == DEFAULT_FOV) ? 0.0825f : 0.135f</c>（公开源
        /// <c>wpn_aug.cpp</c> / <c>wpn_sg552.cpp</c> 的 <c>PrimaryAttack</c> 逐字）；<c>mp.dll</c> 侧
        /// <c>0.135f</c> = <c>0x10141a44</c>，读点 VA <c>0x100d4976</c>（与上面那个开镜分支同一把枪的代码区）。
        /// 狙击四把的循环时间是常量（不随开镜变）⇒ 这两项留 0。</para>
        ///
        /// <para><b>开镜降速</b>：原版各武器类的 <c>GetMaxSpeed</c>（公开源 <c>regamedll/dlls/weapons.h</c>）：
        /// SG552 <c>SG552_MAX_SPEED_ZOOM = 200</c>（:648）、AWP <c>150</c>（:808）、G3SG1 <c>150</c>（:1014）、
        /// SG550 <c>150</c>（:1983）、Scout <c>220</c>（:1616）；**AUG 没有 zoom 档** ——
        /// <c>GetMaxSpeed</c> 是内联的 <c>return AUG_MAX_SPEED;</c>（:781）⇒ 开镜不降速，留 0。
        /// 原版值单位是 unit/s，× 0.0254 得 m/s（同 <see cref="MaxSpeed"/> 的口径）。</para>
        ///
        /// <para><b>开镜音</b>：<c>weapons/zoom.wav</c> 在 <c>mp.dll</c> 只有 4 把枪的开镜
        /// <c>SecondaryAttack</c> 会播（读点 VA <c>0x100d5419</c> AWP / <c>0x100d9c99</c> G3SG1 /
        /// <c>0x100e1cb9</c> Scout / <c>0x100e2659</c> SG550）；AUG 与 SG552 的开镜分支里
        /// <b>没有</b>这条调用 ⇒ 它们的 <see cref="SoundZoom"/> 留空（静音，⛔ 不许拿狙击的音顶替）。</para>
        ///
        /// <para><b>换弹退出开镜</b>：只有 AUG / SG552 的 <c>Reload()</c> 里有
        /// <c>if (m_iFOV != DEFAULT_FOV) SecondaryAttack();</c>（公开源逐字）⇒ 只有它们
        /// <see cref="UnzoomOnReload"/> = true。</para>
        /// </summary>
        private static void ApplyZoomValues()
        {
            CsWeaponDef Def(string id) => _byId.TryGetValue(id, out var d) ? d : null;

            // AUG：fov 55、开镜射速 0.135、不降速（内联 GetMaxSpeed）、无开镜音、换弹退出开镜。
            var aug = Def(Aug);
            if (aug != null)
            {
                aug.CanZoom = true;
                aug.ZoomFov = ZoomFovAug;      // 55
                aug.CycleTimeZoom = CycleTimeZoom;  // 0.135
                aug.MaxSpeedZoom = 0f;
                aug.SoundZoom = "";
                aug.UnzoomOnReload = true;
            }

            // SG552：与 AUG 同档，唯一差别是开镜时降速到 200 unit/s = 5.08 m/s。
            var sg552 = Def(Sg552);
            if (sg552 != null)
            {
                sg552.CanZoom = true;
                sg552.ZoomFov = ZoomFovAug;    // 55
                sg552.CycleTimeZoom = CycleTimeZoom;
                sg552.MaxSpeedZoom = Spd200;
                sg552.SoundZoom = "";
                sg552.UnzoomOnReload = true;
            }

            // 狙击四把：两档 FOV（第一档 40 走 CsConst.ZoomFov）+ 开镜音 + 各自的降速档。
            var awp = Def(Awp);
            if (awp != null)
            {
                awp.CanZoom = true;
                awp.ZoomFov = CsConst.ZoomFov;                 // 40
                awp.ZoomFovSecond = CsConst.ZoomFovSecondAwp;   // 10
                awp.MaxSpeedZoom = Spd150;
                awp.SoundZoom = ZoomSound;
            }

            var scout = Def(Scout);
            if (scout != null)
            {
                scout.CanZoom = true;
                scout.ZoomFov = CsConst.ZoomFov;                 // 40
                scout.ZoomFovSecond = CsConst.ZoomFovSecondOther; // 15
                scout.MaxSpeedZoom = Spd220;
                scout.SoundZoom = ZoomSound;
            }

            var sg550 = Def(Sg550);
            if (sg550 != null)
            {
                sg550.CanZoom = true;
                sg550.ZoomFov = CsConst.ZoomFov;
                sg550.ZoomFovSecond = CsConst.ZoomFovSecondOther;
                sg550.MaxSpeedZoom = Spd150;
                sg550.SoundZoom = ZoomSound;
            }

            var g3 = Def(G3sg1);
            if (g3 != null)
            {
                g3.CanZoom = true;
                g3.ZoomFov = CsConst.ZoomFov;
                g3.ZoomFovSecond = CsConst.ZoomFovSecondOther;
                g3.MaxSpeedZoom = Spd150;
                g3.SoundZoom = ZoomSound;
            }
        }

        /// <summary>
        /// 该武器的开镜**档位上限**（0 = 不可开镜；1 = 只有一档，按一下开按一下关；
        /// 2 = 两档，按到底回到不开镜）。消费点 = <c>CsMatch.UpdateLocalPlayer</c> 的档位推进。
        /// </summary>
        public static int ScopeMaxLevel(CsWeaponDef def)
        {
            if (def == null || !def.CanZoom) return 0;
            return def.ZoomFovSecond > 0f ? 2 : 1;
        }

        /// <summary>
        /// 逐武器的**穿墙层数**（原版 <c>iPenetration</c>）与**距离衰减**（原版 <c>flRangeModifier</c>）。
        ///
        /// <para><b>出处</b> = 载体 <c>原版资源/cs16src/cstrike/dlls/mp.dll</c> 的逐武器开火点的
        /// <c>FireBullets3</c> 实参（逐点 VA 与原始字节见 <c>.ai-tmp/test/mpdll-reverse/PEN-FILL.md</c> §1；
        /// 武器归属由「类代码区间锚点」与「MSVC RTTI 恢复的类 vtable 槽」两条独立硬链交叉确证，
        /// 24 个开火点一致）＋ 公开源 <c>regamedll/dlls/weapons.h</c> 的逐武器 <c>*_RANGE_MODIFER</c>
        /// 常量、<c>regamedll/dlls/wpn_shared/wpn_*.cpp</c> 的 <c>FireBullets3</c> 实参（两者逐条吻合）。</para>
        ///
        /// <para><b>距离衰减分母</b>一律 <c>CsConst.DamageFalloffDenominator</c>（原版固定 500，与弹种无关，
        /// 出处见该常量）。</para>
        ///
        /// <para><b>霰弹枪（m3 / xm1014）</b>：原版这两把**不走** <c>FireBullets3</c>，走
        /// <c>CBaseEntity::FireBuckshots</c> —— 8 参、**没有 <c>iPenetration</c> 形参**
        /// （<c>regamedll/dlls/cbase.cpp:1170</c>；<c>wpn_m3.cpp:132</c> = 9 弹丸 / 射程 3000、
        /// <c>wpn_xm1014.cpp:130</c> = 6 弹丸 / 射程 3048）。弹种为 <c>BULLET_PLAYER_BUCKSHOT</c>
        /// （<c>wpn_m3.cpp:134</c> / <c>wpn_xm1014.cpp:132</c> 的 <c>FireBullets</c> 分支），该弹种在
        /// <c>FireBullets3</c> 的按弹种表里落 <c>default</c> ⇒ 穿透力 / 穿透距离 = 0（<c>cbase.cpp:1321-1323</c>）
        /// ⇒ 语义上不透墙，落 <c>Penetration = 0</c> + <c>PenetrationResolved = true</c>。</para>
        /// </summary>
        private static void ApplyPenetrationAndFalloffValues()
        {
            void Set(string id, int penetration, float rangeModifier)
            {
                if (!_byId.TryGetValue(id, out var def)) return;
                def.Penetration = penetration;
                def.PenetrationResolved = true;
                def.RangeModifier = rangeModifier;
                def.FalloffDenominator = CsConst.DamageFalloffDenominator;
            }

            // 弹种 9MM（0x1）：mp.dll 开火点 0x100DB443 / 0x100E0394 / 0x100E446B / 0x100D7787
            Set(Glock18, 1, 0.75f);
            Set(Mp5, 1, 0.84f);
            Set(Tmp, 1, 0.85f);
            // Elite 的两处开火点（0x100D7787 左 / 0x100D787B 右）rangeModifier 在寄存器里给出，
            // 值见公开源 regamedll/dlls/weapons.h:1811 的 ELITE_RANGE_MODIFER = 0.75f（与 GLOCK18 同档）；
            // 对应公开源开火调用 regamedll/dlls/wpn_shared/wpn_elite.cpp:178-179 / 190-191
            //（9MM、iPenetration 1、射程 8192）
            Set(Elite, 1, 0.75f);
            // 弹种 45ACP（0x9）：0x100E58A3 / 0x100DFB1B / 0x100E4CE0
            Set(Usp, 1, 0.79f);
            Set(Mac10, 1, 0.82f);
            Set(Ump45, 1, 0.82f);
            // 弹种 50AE（0xD）
            Set(Deagle, 2, 0.81f);
            // 弹种 556MM（0xC）：0x100D81C4 / 0x100DF1D8 / 0x100D4C07 / 0x100E31C7 / 0x100E2943 / 0x100DA7DC / 0x100DDEAB
            Set(Famas, 2, 0.96f);
            Set(M4A1, 2, 0.97f);
            Set(Aug, 2, 0.96f);
            Set(Sg552, 2, 0.955f);
            Set(Sg550, 2, 0.98f);
            Set(Galil, 2, 0.98f);
            Set(M249, 2, 0.97f);
            // 弹种 762MM（0xB）：0x100D41CB / 0x100E1F89 / 0x100D9F8D
            Set(Ak47, 2, 0.98f);
            Set(Scout, 3, 0.98f);
            Set(G3sg1, 3, 0.98f);
            // 弹种 338MAG（0xA）
            Set(Awp, 3, 0.99f);
            // 弹种 57MM（0xE）与 357SIG（0xF）各一把（P90 / FiveSeven 与 P228）
            Set(P90, 1, 0.885f);
            Set(FiveSeven, 1, 0.885f);
            Set(P228, 1, 0.80f);
            // 霰弹枪：原版走 FireBuckshots、无穿墙（弹种 BULLET_PLAYER_BUCKSHOT）；
            // RangeModifier 留 0 —— 原版该链路不按距离衰减底数算伤害（见 DistanceFalloff 的线性回落）
            Set(M3, 0, 0f);
            Set(Xm1014, 0, 0f);

            // 差异 #129：同表的「射程」列（出处 = VALUES.md §4.2，归属 = PEN-FILL.md §1 的 RTTI 确证）——
            // Deagle 0x100D6DC9、FiveSeven 0x100D8AF9、P228 0x100E0D49 是 4096；P90 0x100E1560 是 8192。
            void SetDistance(string id, float units)
            {
                if (_byId.TryGetValue(id, out var d2)) d2.RangeModifierMaxDistance = units;
            }

            SetDistance(Deagle, RangeMaxDistancePistol);
            SetDistance(FiveSeven, RangeMaxDistancePistol);
            SetDistance(P228, RangeMaxDistancePistol);
            SetDistance(P90, RangeMaxDistance);
        }

        /// <summary>
        /// 逐武器的**护甲比率乘数**（原版 <c>CBasePlayer::TakeDamage</c> 里那台
        /// <c>switch (iGunType)</c>，<c>iGunType = pAttack->m_pActiveItem->m_iId</c>）。
        ///
        /// <para><b>出处</b> = 公开源 <c>rehlds/ReGameDLL_CS</c> 的
        /// <c>regamedll/dlls/player.cpp</c>（<c>CBasePlayer::TakeDamage</c> 的护甲块）：
        /// <c>https://cdn.jsdelivr.net/gh/rehlds/ReGameDLL_CS@master/regamedll/dlls/player.cpp</c>。
        /// 基础值 <c>flRatio = ARMOR_RATIO</c>（0.5，见 <see cref="CsConst.ArmorAbsorbRatio"/>），
        /// <c>switch</c> 再按武器乘一个数；表里没有的武器（MP5 / UMP45 / TMP / USP / M3 / XM1014 /
        /// 手雷 / C4 …）落 <c>default</c> ⇒ 乘数保持 1。</para>
        ///
        /// <para><b>含义</b>：<c>flNew = flRatio × flDamage</c>、护甲抵掉的伤害 = <c>flDamage − flNew</c>
        /// ⇒ 乘数越大越穿甲（AWP 1.95 ⇒ 护甲只挡 2.5%）；MAC10 的 0.95 是全场唯一小于 1 的
        /// （护甲吸收得比基准更多）。</para>
        /// </summary>
        private static void ApplyArmorRatioValues()
        {
            void Set(string id, float scale)
            {
                if (_byId.TryGetValue(id, out var def)) def.ArmorRatioScale = scale;
            }

            Set(Aug, 1.4f);          // case WEAPON_AUG / WEAPON_M4A1
            Set(M4A1, 1.4f);
            Set(Awp, 1.95f);         // case WEAPON_AWP
            Set(G3sg1, 1.65f);       // case WEAPON_G3SG1
            Set(Sg550, 1.45f);       // case WEAPON_SG550
            Set(M249, 1.5f);         // case WEAPON_M249
            Set(Elite, 1.05f);       // case WEAPON_ELITE
            Set(Deagle, 1.5f);       // case WEAPON_DEAGLE
            Set(Glock18, 1.05f);     // case WEAPON_GLOCK18
            Set(FiveSeven, 1.5f);    // case WEAPON_FIVESEVEN / WEAPON_P90
            Set(P90, 1.5f);
            Set(Mac10, 0.95f);       // case WEAPON_MAC10
            Set(P228, 1.25f);        // case WEAPON_P228
            Set(Scout, 1.7f);        // case WEAPON_SCOUT / WEAPON_KNIFE
            Set(Knife, 1.7f);
            Set(Famas, 1.4f);        // case WEAPON_FAMAS / WEAPON_SG552
            Set(Sg552, 1.4f);
            Set(Galil, 1.55f);       // case WEAPON_GALIL / WEAPON_AK47
            Set(Ak47, 1.55f);
        }

        /// <summary>原版开火时传入的射程距离（世界单位，逐字节：<c>mp.dll</c> <c>0x101422c0</c> = 8192.0）。</summary>
        public const float RangeMaxDistance = 8192f;

        /// <summary>
        /// 手枪那一档的射程距离（世界单位）：开火点里直接写 4096.0 的那几个点
        /// —— USP `0x100E58A3`、Deagle `0x100D6DC9`（逐点见 <c>.ai-tmp/test/mpdll-reverse/VALUES.md</c> §4.2）。
        /// </summary>
        public const float RangeMaxDistancePistol = 4096f;

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
        /// 本武器的护甲比率乘数；未列入原版那张表的武器（含 <paramref name="def"/> 为 null 的
        /// 手雷 / 坠落 / C4 这几种没有开火武器的伤害）一律 1。
        /// </summary>
        public static float ArmorRatioScaleFor(CsWeaponDef def)
            => def != null && def.ArmorRatioScale > 0f ? def.ArmorRatioScale : 1f;

        /// <summary>
        /// 原版 <c>CBasePlayer::TakeDamage</c> 的护甲块（纯函数；「有没有甲 / 头盔」那道门由调用方判）：
        /// <c>flRatio = ARMOR_RATIO × ArmorRatioScaleFor(def)</c> → <c>flNew = flRatio × damage</c>；
        /// 护甲应抵 <c>flArmor = (damage − flNew) × ARMOR_BONUS</c>；护甲不够时按
        /// <c>flNew = damage − armor × (1 / ARMOR_BONUS)</c> 折算、护甲清零（原版「护甲不足」分支）。
        ///
        /// <para>出处同 <see cref="ApplyArmorRatioValues"/>；<c>ARMOR_BONUS</c> 见
        /// <see cref="CsConst.ArmorDamageRatio"/>。消费点 = <c>CsDamage.ApplyDamage</c>。</para>
        /// </summary>
        /// <param name="armor">受击者当前护甲点数；0 ⇒ 伤害原样返回（原版同一分支：护甲 0 时
        /// <c>flArmor &gt; armorvalue</c> 成立，护甲不抵伤害）。</param>
        /// <param name="armorLoss">实际扣掉的护甲点数（护甲不足时 = 剩下的全部护甲）。</param>
        /// <returns>护甲吸收后的伤害（原版 <c>flNew</c>），不小于 0。</returns>
        public static float ArmorAbsorb(CsWeaponDef def, float damage, int armor, out int armorLoss)
        {
            var ratio = CsConst.ArmorAbsorbRatio * ArmorRatioScaleFor(def);
            var flNew = ratio * damage;
            var flArmor = (damage - flNew) * CsConst.ArmorDamageRatio;

            if (flArmor > armor)
            {
                armorLoss = armor > 0 ? armor : 0;
                flNew = damage - armorLoss * (1f / CsConst.ArmorDamageRatio);
            }
            else
            {
                armorLoss = (int)System.Math.Round(flArmor);
            }
            return flNew < 0f ? 0f : flNew;
        }

        /// <summary>
        /// 距离衰减系数（0~1）：<c>pow(rangeModifier, dist_in_units / 500)</c>
        /// （分母 = 原版固定 <c>500</c>，出处见 <see cref="CsConst.DamageFalloffDenominator"/>）。
        ///
        /// <para><b>量纲</b>：<paramref name="distMeters"/> 是米，原版公式里的距离是 GoldSrc unit
        /// ⇒ 先 <c>/ CsConst.UnitToMeter</c> 折成 unit 再进公式（<c>rangeModifier</c> 与分母都是原版量纲，
        /// 不能直接吃米）。</para>
        ///
        /// <para><b>线性回落的两种情形</b>：<c>FalloffDenominator</c> 为 0（如霰弹枪的原版链路不按底数衰减）
        /// 或 <c>RangeModifier</c> 为 0 的武器走 <see cref="CsWeaponDef.FalloffPerMeter"/> 的线性模型
        /// —— ⛔ 这两个情形都不能返回 1（那会让这些枪的远距伤害反而变高）。</para>
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

        /// <summary>线性衰减模型（每米），用于不走底数模型的那几把（见 <see cref="DistanceFalloff"/>）。</summary>
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

        /// <summary>差异 #94：原版"移动档"判据的速度阈值 = <b>140 unit/s</b>
        /// （`CAK47::PrimaryAttack` 0x100D3FAA `fcomp [0x101421B8]`，常量 = 140）。
        /// 本工程速度是 m/s ⇒ 比较时先按 <see cref="CsConst.UnitToMeter"/> 折回 unit/s。</summary>
        public const float SpreadMoveSpeedUnits = 140f;

        /// <summary>差异 #94：本武器"移动档"判据（水平速度 &gt; <see cref="CsWeaponDef.SpreadMoveSpeedUnits"/> unit/s）。</summary>
        public static bool IsMovingTier(CsWeaponDef def, float speedUnits)
            => def != null && speedUnits > def.SpreadMoveSpeedUnits;

        /// <summary>
        /// 差异 #94：本武器"慢移档"判据（只有 AWP 有这一档：&gt; <see cref="CsWeaponDef.SpreadSlowSpeedUnits"/> = 10 unit/s）。
        /// </summary>
        public static bool IsSlowTier(CsWeaponDef def, float speedUnits)
            => def != null && def.SpreadSlowSpeedUnits > 0f && speedUnits > def.SpreadSlowSpeedUnits;

        /// <summary>
        /// 差异 #94：本武器的散布是否走原版档位公式 —— 要求三档系数与 acc 模型**都**取到。
        /// <para>例外：<see cref="CsWeaponDef.SpreadAbsolute"/> 族（AWP / Scout / G3SG1）原版**不乘 acc**
        /// （`CAWP::PrimaryAttack` 里没有对 `+0xF8` 的访问，见 §A3.6）⇒ 这三把不需要 acc 模型。</para>
        /// </summary>
        public static bool HasSpreadCoefficients(CsWeaponDef def)
            => def != null && def.SpreadStillMul > 0f && (def.AccModel || def.SpreadAbsolute);

        /// <summary>
        /// 差异 #94：本发开火后的 acc（原版 `m_flAccuracy`）。
        ///
        /// <para>步枪 / 机枪 / 冲锋枪：<c>min(AccBase + shots^AccPower / AccDiv, AccClampMax)</c>；
        /// 手枪：<c>clamp(acc − AccDecayScale × (AccDecayRef − dt), AccClampLo, AccClampHi)</c>，
        /// 其中衰减只在"本发是这一梭的第一发"（原版 <c>m_iShotsFired == 1</c> 分支）且
        /// <paramref name="dtSinceLastAttack"/> ≥ 0（原版要求开过火）时执行。</para>
        /// </summary>
        /// <param name="shotsAfterIncrement">本发之后的 `m_iShotsFired`（从 1 起）。</param>
        /// <param name="dtSinceLastAttack">距上一次开火的秒数；&lt; 0 = 还没开过火。</param>
        public static float AccuracyAfterShot(CsWeaponDef def, float prevAcc, int shotsAfterIncrement,
            float dtSinceLastAttack)
        {
            if (def == null || !def.AccModel) return prevAcc;

            if (def.AccTimeDecay)
            {
                var acc = prevAcc;
                if (shotsAfterIncrement <= 1 && dtSinceLastAttack >= 0f)
                    acc -= def.AccDecayScale * (def.AccDecayRef - dtSinceLastAttack);
                if (acc > def.AccClampHi) acc = def.AccClampHi;
                if (acc < def.AccClampLo) acc = def.AccClampLo;
                return acc;
            }

            var v = def.AccBase +
                    (float)System.Math.Pow(shotsAfterIncrement, def.AccPower) / def.AccDiv;
            return v > def.AccClampMax ? def.AccClampMax : v;
        }

        /// <summary>
        /// 差异 #94：原版三档（手枪族四档）的**无量纲散布系数**（就是原版传给 `FireBullets3` 的
        /// `flSpread` 表达式，逐条代数式见 `策划/手感参数对照.md` §5.12 / §5.13）。
        /// </summary>
        /// <param name="moving">原版"移动档"判据：水平速度 &gt; <see cref="SpreadMoveSpeedUnits"/> unit/s。</param>
        /// <param name="slow">差异 #94：**慢移档**（只有 AWP 有，阈值 = `SpreadSlowSpeedUnits` = 10 unit/s）。</param>
        /// <param name="burst">差异 #94：连发态（Glock18 的第二套档位）。</param>
        public static float SpreadCoefficient(CsWeaponDef def, float acc, bool onGround, bool moving,
            bool crouching, bool silenced, bool slow = false, bool burst = false)
        {
            if (def == null || def.SpreadStillMul <= 0f) return 0f;

            // 差异 #94：**第二套档位** —— 原版是"四档整体换一套"（USP 装消音 / Glock18 连发），
            // 不是只换静止档（旧的 SpreadStillMulSilenced 只够表达 M4A1 那种单档差异）。
            if (def.SpreadAltSet
                && ((def.CanSilence && silenced) || (def.CanBurst && burst)))
                return Tier(def.SpreadAirMulAlt, def.SpreadMoveMulAlt, 0f,
                            def.SpreadDuckMulAlt, def.SpreadStillMulAlt);

            return Tier(def.SpreadAirMul, def.SpreadMoveMul, def.SpreadSlowMul,
                        def.SpreadDuckMul, def.SpreadStillMul);

            // 档位顺序 = 原版逐分支顺序：空中 → 移动 → 慢移 → 蹲 → 站（AWP 全量原文见 §A3.6）。
            float Tier(float air, float move, float slowMul, float duck, float still)
            {
                var mul = !onGround ? air
                    : moving ? move
                    : slow ? slowMul
                    : crouching ? duck
                    : still;

                // 形态 3（差异 #94 新增）：绝对常量（AWP / Scout / G3SG1）—— 不乘 acc。
                if (def.SpreadAbsolute) return mul;
                // 形态 2：`Mul × (1 − acc)`（手枪族 + SG550）。
                if (def.SpreadInverted) return mul * (1f - acc);
                // 形态 1（既有 16 把，一字不动）：`Add + Mul × acc`；冲锋枪族无移动档 ⇒ 落到静止档。
                if (!onGround) return def.SpreadAirAdd + air * acc;
                if (moving && move > 0f) return def.SpreadAirAdd + move * acc;
                var st = silenced && def.SpreadStillMulSilenced > 0f ? def.SpreadStillMulSilenced : still;
                return st * acc;
            }
        }

        /// <summary>
        /// 差异 #94：参考态（**静止站 · 未装消音 · acc = <see cref="CsWeaponDef.AccReset"/>**）的系数。
        /// `CsWeaponDef.Spread`（度）就是按这个态标定的 ⇒ 其余状态 / 其余发数按系数之**比**缩放。
        ///
        /// <para>为什么只能用比值：原版系数是 `系数 × acc` 口径的无量纲量（乘的是方向向量扰动），
        /// 与本工程的"度"不同量纲；比值与量纲无关，且原版公式的全部相对行为（移动/空中更散、
        /// 连发累计、手枪按 dt 衰减）照样可判。</para>
        /// </summary>
        public static float SpreadCoefficientReference(CsWeaponDef def)
        {
            if (def == null || def.SpreadStillMul <= 0f) return 0f;
            return SpreadCoefficient(def, ReferenceAccuracy(def), onGround: true, moving: false,
                crouching: false, silenced: false);
        }

        /// <summary>
        /// 差异 #94：**参考态**的 acc = <see cref="CsWeaponDef.Spread"/>（本工程"度"标定值）对应的那一档。
        ///
        /// <para>口径 = 原版复位值 <see cref="CsWeaponDef.AccReset"/>；但 MP5 / UMP45 的原版复位值是 **0**
        /// （`Spawn` 用 `fldz` 写 `+0xF8`）⇒ 该态下"静止站"系数恒 0、比值模型分母为 0（退化）。
        /// 这两把改用**首发后**的 acc = `AccBase + 1^AccPower / AccDiv`（幂次对 `shots = 1` 无影响），
        /// 除数 / 钳制口径与 <see cref="AccuracyAfterShot"/> 一致 ⇒ 可离线复算：
        /// MP5 = `0.45 + 1/220.1…`（`1/0.00454339`）· UMP45 = `0.5 + 1/210`。</para>
        /// </summary>
        public static float ReferenceAccuracy(CsWeaponDef def)
        {
            if (def == null) return 0f;
            if (def.AccReset > 0f) return def.AccReset;
            var div = def.AccDiv > 0f ? def.AccDiv : 1f;
            var v = def.AccBase + 1f / div;
            return def.AccClampMax > 0f && v > def.AccClampMax ? def.AccClampMax : v;
        }

        /// <summary>
        /// 差异 #94：本发**散布（度）**。有原版档位系数与 acc 模型的武器按原版公式相对参考态折算；
        /// 其余武器回落 <see cref="StaticSpreadFor"/>（调用方再按旧的"移动/离地附加"加
        /// <see cref="CsWeaponDef.MoveSpread"/>）。
        /// </summary>
        public static float SpreadFor(CsWeaponDef def, float acc, bool onGround, bool moving,
            bool crouching, bool silenced, bool burst, bool slow = false)
        {
            if (def == null) return 0f;

            // 形态 3（差异 #94 新增，绝对常量 = AWP / Scout / G3SG1）：原版 `flSpread` 是**单位向量横向偏移**
            // （`vecAiming + x·flSpread·v_right + y·flSpread·v_up`），本工程口径是**度** ⇒ 折 `atan` 后
            // **直接**就是本发散布角。⛔ 不能走下面的比值模型：AWP 静止档 0.001 / 空中档 0.85 差 850×，
            // 比值会把 0.10° 放大成 ~85°（超 45° 会朝后打）—— 那是比值锚点不对，不是原版行为。
            // ⛔ 本文件是纯 Core（没有 `using UnityEngine`）⇒ 用 `System.Math`，与同文件
            //    `AccuracyAfterShot` 的写法一致（那里也是 `(float)System.Math.Pow(...)`）。
            if (def.SpreadAbsolute)
                return (float)(System.Math.Atan(
                           SpreadCoefficient(def, acc, onGround, moving, crouching, silenced, slow, burst))
                       * 180.0 / System.Math.PI);

            var reference = SpreadCoefficientReference(def);
            if (reference <= 0f) return StaticSpreadFor(def, silenced, burst);

            var spread = def.Spread *
                         (SpreadCoefficient(def, acc, onGround, moving, crouching, silenced, slow, burst) / reference);

            // 连发档的系数树未逐条确证 ⇒ 只在原版比值（`StaticSpreadFactorBurst / StaticSpreadFactor`）上叠一层。
            // ⛔ 已有**第二套档位**的武器（Glock18 连发 / USP 装消音）在 SpreadCoefficient 里已按原版四档
            //    整体换过 ⇒ 再叠这层旧比值就是重复计。
            if (burst && def.CanBurst && !def.SpreadAltSet
                && def.StaticSpreadFactor > 0f && def.StaticSpreadFactorBurst > 0f)
                spread *= def.StaticSpreadFactorBurst / def.StaticSpreadFactor;

            return spread;
        }

        /// <summary>差异 #94：该武器的 acc 复位值（原版 Spawn / Reset / Deploy 写进 `+0xF8` 的常量）。</summary>
        public static float ResetAccuracy(CsWeaponDef def) => def != null && def.AccModel ? def.AccReset : 0f;

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

        /// <summary>
        /// 本发开火音的 SFX 短名：**未装消音**且该武器有未装档采样时用
        /// <see cref="CsWeaponDef.SoundFireUnsilenced"/>，否则一律用 <see cref="CsWeaponDef.SoundFire"/>。
        ///
        /// <para>只有 <c>CanSilence</c> 的武器（USP / M4A1）有未装档采样 —— 名字与出处见
        /// <see cref="CsWeaponDef.SoundFireUnsilenced"/>；其余武器那一栏是空串 ⇒ 两条路径同结果。</para>
        /// </summary>
        public static string FireSound(CsWeaponDef def, bool silenced)
        {
            if (def == null) return null;
            if (!silenced && def.SoundFireUnsilenced != "") return def.SoundFireUnsilenced;
            return def.SoundFire;
        }

        /// <summary>
        /// attack2 切换动作的 SFX 短名（<paramref name="turningOn"/> = 切向"装上消音器 / 进入连发"一侧）。
        /// 空串 = 该武器 / 该方向原版没有切换音（如 Glock18）⇒ 调用方静音，⛔ 不许顶替。
        /// </summary>
        public static string Attack2ToggleSound(CsWeaponDef def, bool turningOn)
        {
            if (def == null) return "";
            var clip = turningOn ? def.SoundAttack2On : def.SoundAttack2Off;
            return clip ?? "";
        }

        /// <summary>
        /// 本档的循环时间（秒）：连发模式取 <c>BurstCycleTime</c>，否则 <c>CycleTime</c>，都没有则按 <c>Rpm</c> 推。
        /// </summary>
        /// <param name="scoped">
        /// 已开镜。原版只有 AUG / SG552 的 <c>PrimaryAttack</c> 按 <c>pev-&gt;fov == DEFAULT_FOV</c> 选两档
        /// （<c>0.0825f : 0.135f</c>），狙击四把的循环时间是常量 ⇒ 其余武器这一位不起作用
        /// （<see cref="CsWeaponDef.CycleTimeZoom"/> = 0）。
        /// </param>
        public static float CycleTimeFor(CsWeaponDef def, bool burst, bool scoped = false)
        {
            if (def == null) return 0f;
            if (scoped && def.CycleTimeZoom > 0f) return def.CycleTimeZoom;
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

        /// <summary>
        /// 原版 <c>invnext</c> / <c>invprev</c> 的取枪规则：在**持有武器表**（已按槽位 1→5、槽内按武器表顺序排好）
        /// 上朝 <paramref name="dir"/> 方向取下一把 —— 扫到尾回绕一次；空槽位不入表，故天然跳过。
        /// <para><b>出处</b> = <c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>：<c>invnext</c> 实现 VA <c>0x1940B90</c>
        /// （<c>0x1940BE4</c> 起逐项 <c>test esi,esi</c> / <c>jne</c> 跳空项，槽步长 22、共 5 槽，<c>0x1940C0E</c> 的第二趟
        /// = 回绕一次）与 <c>invprev</c> VA <c>0x1940C70</c>（<c>0x1940CC3</c> <c>dec eax</c> 反向扫）。全表**没有**弹药判据
        /// ⇒ 「只切有弹的枪」不在原版行为里。</para>
        /// </summary>
        /// <param name="carried">持有武器 id 表（顺序即切换顺序）。</param>
        /// <param name="current">当前手持武器 id。</param>
        /// <param name="dir">方向：&gt; 0 = 下一把（invnext），&lt; 0 = 上一把（invprev），0 = 不动。</param>
        /// <returns>要切到的武器 id；<paramref name="carried"/> 空 / <paramref name="dir"/> 为 0 ⇒ null。</returns>
        public static string CycleNext(IReadOnlyList<string> carried, string current, int dir)
        {
            if (carried == null || carried.Count == 0 || dir == 0) return null;

            var n = carried.Count;
            var at = -1;
            for (var i = 0; i < n; i++)
            {
                if (carried[i] != current) continue;
                at = i;
                break;
            }
            // 当前武器不在表里（例如清单刚变过）：正向从表首起算，负向从表尾起算。
            if (at < 0) at = dir > 0 ? -1 : 0;
            return carried[((at + dir) % n + n) % n];
        }

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
                // 差异 #94：既有武器的"移动档"速度阈值一律显式回填 140（= 旧常量
                // `CsWeapons.SpreadMoveSpeedUnits` 的现值）⇒ 字段化后行为一字不变；
                // 狙击四把在 ApplySpreadValues 里覆盖（AWP 140 / Scout 170 / G3SG1·SG550 0）。
                SpreadMoveSpeedUnits = 140f,
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
