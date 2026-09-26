using Cs16.Core;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// **本项目新增**的第一人称操作 / 射击表现层可调数值。
    ///
    /// <para><b>为什么单独一个文件</b>：与 <c>Cs16.Core.CsConst</c> 同语义（"改一处即可调手感"的旋钮），
    /// 但它们是 <c>Module/Player</c>、<c>Module/CameraRig</c>、<c>Module/Combat</c> 三处共用的，
    /// 目的同样是：**业务脚本里不出现裸数字**。</para>
    ///
    /// <para><b>改这里必须同步回报</b>：这些值直接决定"像不像 CS 1.6"（后坐力表现、准星扩散、开镜、bob）。</para>
    ///
    /// <para><b>出处口径（切片L 逐条补）</b>：本文件的量**绝大多数是「本项目新增」的操作 / 表现层旋钮** ——
    /// 它们对应的是 CS 1.6 **客户端**手感（后坐力 / 散布 / 准星扩散 / bob / 开镜 / 受击晃动 / 枪口火焰），
    /// 而原版把这些量写死在 <c>cstrike/cl_dlls/client.dll</c> 与 <c>mp.dll</c> 的逐武器代码里
    /// 两条例外：<c>PitchLimit</c>（口径同 <c>CsActor.Pitch</c>）、<c>DegreesPerMouseCount</c>（原版 <c>m_yaw</c> 默认值）。</para>
    /// </summary>
    internal static class CsCombatTuning
    {
        // ==================================================================
        //  视角 / 鼠标
        // ==================================================================
        /// <summary>俯仰角上限（度）。与 <c>CsActor.Pitch</c> 的 -89~89 约定、模拟侧的钳制口径一致。
        /// 出处：**本项目新增**（钳制值 ±89° 与 <c>CsActor.Pitch</c> / <c>CsMatchConst.PitchLimit</c> 同约定；
        /// 原版同一钳制在 GoldSrc 客户端视角侧，载体不在盘 ⇒ 不给 file:line）。</summary>
        public const float PitchLimit = 89f;

        /// <summary>
        /// 鼠标灵敏度换算系数（度 / 每个鼠标计数），即官方 CS 1.6 的 <c>m_yaw</c> 默认值。
        /// <para>引擎的 <c>Game.Input.MouseDelta</c> 给的是像素位移，与 CS 的"计数"在同一量级
        /// （400DPI 鼠标 1 英寸 ≈ 400 计数 ≈ 400 像素），所以
        /// <c>角度 = MouseDelta * MouseSensitivity * 本系数</c> 与 CS 的手感同量纲。
        /// 灵敏度本身来自玩家设置（<c>CsPlayerSettings.MouseSensitivity</c>，默认 3.0 = CS 默认值）。</para>
        /// <para>出处：原版 cvar <c>m_yaw</c> 的出厂默认 = <b>0.022</b>（度 / 鼠标计数），本值即照搬该默认值
        /// （<c>m_yaw</c> 属**客户端** cvar，其载体 <c>client.dll</c> 现不在盘 ⇒ 只能记「原版 cvar 默认值」，不给 file:line）。</para>
        /// </summary>
        public const float DegreesPerMouseCount = 0.022f;

        // ==================================================================
        //  后坐力（只做表现；权威累加在比赛模拟里）
        // ==================================================================
        /// <summary>视角后坐力放大系数（1 = 与模拟累加的度数 1:1，视觉上"抬多少就是多少"）。
        /// 出处：**本项目新增**（后坐力**表现**层的归一化系数）。</summary>
        public const float RecoilViewScale = 1f;

        /// <summary>后坐力"起跳"跟随时间常数（秒）：越小越跟手。
        /// 出处：**本项目新增**（原版后坐力是每发角度累加，没有"跟随时间常数"这个量，见类注释「出处口径」）。</summary>
        public const float RecoilRiseTau = 0.035f;

        /// <summary>后坐力"回正"时间常数（秒）：比上跳慢，形成"抬得快、落得慢"的观感。
        /// 出处：**本项目新增**（同 <c>RecoilRiseTau</c>）。</summary>
        public const float RecoilFallTau = 0.13f;

        // ==================================================================
        //  散布（子弹角度）
        // ==================================================================
        /// <summary>开镜（Zoom）时的散布倍率。
        /// 出处：**本项目新增**（原版散布模型见类注释「出处口径」）。</summary>
        public const float ZoomSpreadScale = 0.25f;

        /// <summary>蹲下时的散布倍率。
        /// 出处：**本项目新增**（同 <c>ZoomSpreadScale</c>）。</summary>
        public const float CrouchSpreadScale = 0.7f;

        /// <summary>离地（跳跃/下落）时额外附加的散布倍率（乘 <c>def.MoveSpread</c>）。
        /// 出处：**本项目新增**（同 <c>ZoomSpreadScale</c>）。</summary>
        public const float AirSpreadScale = 0.5f;

        /// <summary>每发连射累积的散布惩罚（度/发），模拟"越扫越散"。
        /// 出处：**本项目新增**（同 <c>ZoomSpreadScale</c>）。</summary>
        public const float RecoilSpreadPerShot = 0.15f;

        /// <summary>连射散布惩罚的上限（度），防止长时间扫射把散布堆到荒谬值。
        /// 出处：**本项目新增**（同 <c>ZoomSpreadScale</c>）。</summary>
        public const float RecoilSpreadMax = 2.4f;

        // ---- 穿墙 ----
        /// <summary>允许继续穿透的武器门槛（<c>CsWeaponDef.ArmorPenetration</c> 高于它才可穿）。
        /// 出处：**本项目新增**（原版护甲穿透是 <c>mp.dll</c> 里逐武器写死的浮点立即数，见 <c>策划/对照表.md</c> §6 BLOCKED-2）。</summary>
        public const float PenetrationArmorThreshold = 0.6f;

        /// <summary>最多穿过几层"墙"（命中非角色的碰撞体算一层）。
        /// 出处：**本项目新增**（实现容量上限：原版穿透是逐武器 <c>PrimaryAttack</c> 里的射线循环，层数上限未解出）。</summary>
        public const int MaxPenetrationLayers = 2;

        /// <summary>单发最多接受的射线命中数（缓冲上限；超出即丢弃，见 <c>Firearm</c>）。
        /// 出处：**本项目新增**（射线命中缓冲容量，纯实现上限）。</summary>
        public const int MaxRayHits = 32;

        // ==================================================================
        //  准星扩散（0~1，供 HUD 读）
        // ==================================================================
        // 出处（本组 7 条）：**本项目新增** —— 准星扩散是本工程自定的 0~1 模型；原版准星是"臂长随屏幕宽比例缩放"的
        // 绘制（<c>client.dll:0x043170-0x043198</c>，见 <c>策划/对照表.md</c> U-07）⇒ 与 A **不是同一机制**，
        // 因此没有可搬运的原版值（逐条不再重复这句话）。
        public const float CrosshairExpandSpeed = 6f;    // 扩张（每秒变化量）
        public const float CrosshairShrinkSpeed = 2.6f;  // 回缩（比扩张慢）
        public const float CrosshairMoveWeight = 0.8f;   // 移动贡献
        public const float CrosshairShotWeight = 0.7f;   // 连射贡献
        public const float CrosshairAirWeight = 0.5f;    // 离地贡献
        public const int CrosshairFullShots = 12;        // 连射达到该发数即满扩散
        public const float CrosshairZoomScale = 0.15f;   // 开镜时准星几乎不散

        // ==================================================================
        //  开镜 / 视高 / 受击
        // ==================================================================
        /// <summary>FOV 过渡时间常数（秒）——即"开镜延迟"的可见部分（AWP/Scout）。
        /// 出处：**本项目新增**（原版开镜 FOV 由 CS 自己的 <c>client.dll</c> 下发，HLSDK 里没有该实现
        /// ⇒ <c>策划/对照表.md</c> A-05 [BLOCKED]）。</summary>
        public const float ZoomTransitionTau = 0.06f;

        /// <summary>开镜"稳定"所需时间（秒）：未稳定前不享受开镜精度加成（cs 的 quickscope 惩罚）。
        /// 出处：**本项目新增**（quickscope 惩罚时长；原版同一机制在 <c>client.dll</c> / <c>mp.dll</c>，未解出）。</summary>
        public const float ScopeSettleTime = 0.18f;

        /// <summary>视高平滑时间常数（秒）：站/蹲切换与台阶上下不"跳眼睛"。
        /// 出处：**本项目新增**（原版客户端的插值是 <c>HLSDK/cl_dll/view.cpp</c> 的 <c>ViewInterp</c> 位置回放，
        /// 不是指数平滑 tau ⇒ 换算不出，口径同 <c>策划/对照表.md</c> E-03）。</summary>
        public const float EyeHeightSmoothTau = 0.045f;

        /// <summary>超过该位移（米）认为发生了瞬移（出生/观战切换），视高直接吸附不平滑。</summary>
        public const float TeleportSnapDistance = 2.5f;

        /// <summary>受击晃动最大角度（度，满伤害时）。
        /// 出处：**本项目新增**（受击晃动是客户端视角表现；原版载体 <c>client.dll</c> 不在盘）。</summary>
        public const float DamageShakeMaxDeg = 2.4f;

        /// <summary>受击晃动持续时间（秒）。
        /// 出处：**本项目新增**（同 <c>DamageShakeMaxDeg</c>）。</summary>
        public const float DamageShakeDuration = 0.28f;

        /// <summary>受击晃动幅度对应的伤害参考值（伤害 >= 它即取最大晃动）。
        /// 出处：**本项目新增**（"满伤害"参考值取"一枪致死"的量级 45）。</summary>
        public const float DamageShakeFullDamage = 45f;

        // ==================================================================
        //  视点晃动（bob）
        // ==================================================================
        /// <summary>bob 幅度到达目标值的速度（每秒的归一化变化量）。
        /// 出处：**本项目新增** —— 原版 bob 是**速度驱动**（<c>bob = |simvel.xy| * cl_bob</c>，
        /// 见 <c>策划/对照表.md</c> A-08 = <c>HLSDK/cl_dll/view.cpp:180-203</c>）⇒ 与 A 不是同一套公式。</summary>
        public const float BobBlendSpeed = 3.2f;

        /// <summary>bob 的横滚角幅度（度）。
        /// 出处：**本项目新增**（原版 roll 分量是 <c>roll -= bob * 1</c>，同 <c>策划/对照表.md</c> A-08）。</summary>
        public const float BobRollDegrees = 0.55f;

        /// <summary>落地"沉一下"的竖直幅度（米）。
        /// 出处：**本项目新增**（原版对应量是"起飞屏抖"阈值 <c>PLAYER_FALL_PUNCH_THRESHHOLD 350</c>
        /// unit/s（<c>策划/对照表.md</c> N-22），不是"下沉米数" ⇒ 换算不出）。</summary>
        public const float LandDipAmount = 0.05f;

        /// <summary>落地沉降的恢复时间常数（秒）。
        /// 出处：**本项目新增**（同 <c>LandDipAmount</c>）。</summary>
        public const float LandDipTau = 0.11f;

        // ==================================================================
        //  表现（枪口火焰 / 弹道 / 手雷视觉）
        // ==================================================================
        // ------------------------------------------------------------------
        //  枪口火焰的落点 / 尺寸 / 时长 —— 三条都是**逐武器**的
        // ------------------------------------------------------------------
        /// <summary>
        /// **表外武器的兜底**横向落点（米，相机局部 <c>x</c> = 相机右方）= 本工程既有值。
        /// 已知武器一律走 <see cref="MuzzleLateral"/>（逐武器实测），本常量只给"武器表里没有的 id"。
        ///
        /// <para><b>为什么落点按相机局部系给</b>：视模型（<c>v_*</c>）本身是在**相机空间**里建模的
        /// （原点就在相机原点，只 y 下移 1 unit，见 <c>CsViewTuning.ViewModelLocalPosition</c>）⇒
        /// <c>eye + 相机旋转 × 局部点</c> 定位与模型同一坐标系；用 world-up 叉乘算 right/up 反而在
        /// 俯仰 ±90° 时退化。</para>
        /// </summary>
        public const float MuzzleOffsetRight = 0.13f;

        /// <summary>见 <see cref="MuzzleOffsetRight"/>（相机局部系 y；负数 = 视平线以下）。</summary>
        public const float MuzzleOffsetUp = -0.12f;

        /// <summary>
        /// 逐武器**枪口横向位置**（米，相机局部 <c>x</c>）。
        ///
        /// <para><b>出处 = 逐武器实测（逐顶点）</b>：视模型 idle 姿态下取"沿相机前向最靠前的 2 cm 内顶点"
        /// 的质心，取其相机构型的 <c>x</c> 分量（载体 = <c>client/Assets/Resources/Art/{T,CT}/viewmodel_&lt;武器&gt;.prefab</c>）。
        /// 实测 0.1113（p90）~ 0.1807（sg550）—— 用一个常数会让长枪的火焰偏离枪口（实测 <c>ak47</c> 差 4.4 cm、
        /// <c>awp</c> 差 2.3 cm）。</para>
        ///
        /// <para><b>两条例外（不是原始质心，均按同类中位数）</b>：
        /// ① <c>elite</c>（双持）有两个枪口，前 2 cm 顶点含左右两把 ⇒ 质心落在两枪口中点（实测 0.0003），
        /// 单张火焰贴不到任一枪口 ⇒ 取手枪类中位数；
        /// ② <c>xm1014</c> 的前沿顶点落在护木/泵上（实测质心 <c>y=-0.284</c>，明显不是枪口）⇒ 取霰弹类中位数。
        /// 表外武器同样走类别中位数（= **类别推断**），见 <see cref="MuzzleLateralFallback"/>。</para>
        /// </summary>
        public static float MuzzleLateral(string weaponId)
        {
            switch (weaponId)
            {
                // ---- 手枪（实测 0.1168~0.1250）----
                case CsWeapons.Glock18: return 0.1168f;
                case CsWeapons.Usp: return 0.1192f;
                case CsWeapons.P228: return 0.1195f;
                case CsWeapons.Deagle: return 0.1204f;
                case CsWeapons.FiveSeven: return 0.1250f;
                case CsWeapons.Elite: return 0.1195f;      // 双枪：质心不可用，取手枪类中位数
                // ---- 冲锋枪（实测 0.1113~0.1643）----
                case CsWeapons.Mp5: return 0.1424f;
                case CsWeapons.Tmp: return 0.1643f;
                case CsWeapons.Mac10: return 0.1223f;
                case CsWeapons.Ump45: return 0.1568f;
                case CsWeapons.P90: return 0.1113f;
                // ---- 步枪（实测 0.1220~0.1743）----
                case CsWeapons.Galil: return 0.1220f;
                case CsWeapons.Famas: return 0.1231f;
                case CsWeapons.Ak47: return 0.1743f;
                case CsWeapons.M4A1: return 0.1550f;
                case CsWeapons.Sg552: return 0.1334f;
                case CsWeapons.Aug: return 0.1443f;
                // ---- 狙击枪（实测 0.1301~0.1807）----
                case CsWeapons.Scout: return 0.1301f;
                case CsWeapons.Awp: return 0.1530f;
                case CsWeapons.G3sg1: return 0.1578f;
                case CsWeapons.Sg550: return 0.1807f;
                // ---- 霰弹枪 ----
                case CsWeapons.M3: return 0.1338f;
                case CsWeapons.Xm1014: return 0.1338f;     // 前沿顶点不是枪口，取霰弹类中位数
                // ---- 机枪 ----
                case CsWeapons.M249: return 0.1317f;
                default: return MuzzleLateralFallback(weaponId);
            }
        }

        /// <summary>
        /// 逐武器**枪口竖直位置**（米，相机局部 <c>y</c>；负数 = 视平线以下）。出处同
        /// <see cref="MuzzleLateral"/>（逐顶点实测，实测 -0.0762~-0.1348）。
        /// <c>xm1014</c> 例外（前沿顶点在护木上）⇒ 取霰弹类中位数。
        /// </summary>
        public static float MuzzleVertical(string weaponId)
        {
            switch (weaponId)
            {
                case CsWeapons.Glock18: return -0.1009f;
                case CsWeapons.Usp: return -0.0962f;
                case CsWeapons.P228: return -0.0856f;
                case CsWeapons.Deagle: return -0.0852f;
                case CsWeapons.FiveSeven: return -0.1061f;
                case CsWeapons.Elite: return -0.0762f;
                case CsWeapons.Mp5: return -0.1101f;
                case CsWeapons.Tmp: return -0.1228f;
                case CsWeapons.Mac10: return -0.1156f;
                case CsWeapons.Ump45: return -0.1134f;
                case CsWeapons.P90: return -0.1102f;
                case CsWeapons.Galil: return -0.1021f;
                case CsWeapons.Famas: return -0.1078f;
                case CsWeapons.Ak47: return -0.1159f;
                case CsWeapons.M4A1: return -0.1200f;
                case CsWeapons.Sg552: return -0.1046f;
                case CsWeapons.Aug: return -0.0934f;
                case CsWeapons.Scout: return -0.0954f;
                case CsWeapons.Awp: return -0.0812f;
                case CsWeapons.G3sg1: return -0.1074f;
                case CsWeapons.Sg550: return -0.1348f;
                case CsWeapons.M3: return -0.1058f;
                case CsWeapons.Xm1014: return -0.1058f;    // 前沿顶点不是枪口，取霰弹类中位数
                case CsWeapons.M249: return -0.0885f;
                default: return MuzzleVerticalFallback(weaponId);
            }
        }

        /// <summary>表外武器的横向位置（米）。**类别推断**：同类已测武器的中位数
        /// （手枪 0.1195 / 冲锋枪 0.1424 / 步枪 0.1389 / 狙击 0.1554 / 霰弹 0.1338 / 机枪 0.1317），
        /// 类别也没有时用全部已测武器的中位数 0.1326。</summary>
        private static float MuzzleLateralFallback(string weaponId)
        {
            switch (MuzzleClass(weaponId))
            {
                case CsWeaponClass.Pistol: return 0.1195f;
                case CsWeaponClass.SMG: return 0.1424f;
                case CsWeaponClass.Rifle: return 0.1389f;
                case CsWeaponClass.Sniper: return 0.1554f;
                case CsWeaponClass.Shotgun: return 0.1338f;
                case CsWeaponClass.MachineGun: return 0.1317f;
                default: return 0.1326f;
            }
        }

        /// <summary>表外武器的竖直位置（米）。**类别推断**：同类已测武器的中位数
        /// （手枪 -0.0909 / 冲锋枪 -0.1134 / 步枪 -0.1062 / 狙击 -0.1014 / 霰弹 -0.1058 / 机枪 -0.0885），
        /// 类别也没有时用全部已测武器的中位数 -0.1060。</summary>
        private static float MuzzleVerticalFallback(string weaponId)
        {
            switch (MuzzleClass(weaponId))
            {
                case CsWeaponClass.Pistol: return -0.0909f;
                case CsWeaponClass.SMG: return -0.1134f;
                case CsWeaponClass.Rifle: return -0.1062f;
                case CsWeaponClass.Sniper: return -0.1014f;
                case CsWeaponClass.Shotgun: return -0.1058f;
                case CsWeaponClass.MachineGun: return -0.0885f;
                default: return -0.1060f;
            }
        }

        /// <summary>
        /// 枪口火焰比**枪口**再往前让的距离（米）。SpriteRenderer 走透明队列但**开**深度测试 ⇒
        /// 贴片与枪口几何贴在一起会 z-fighting、落进枪身几何之内则整片被盖掉（表现 = 没有火焰）。
        /// </summary>
        public const float MuzzleForwardClearance = 0.015f;

        /// <summary>
        /// 逐武器**枪口轴向距离**（米，相机局部 z）= 该武器视模型 <c>viewmodel_*</c> 预制体在 idle
        /// 姿态下、动画后包围盒沿相机前向的**最大投影**（= 枪管尖在相机前方多远）。
        ///
        /// <para><b>出处 = 逐武器实测（逐顶点）</b>：载体 = <c>client/Assets/Resources/Art/{T,CT}/viewmodel_&lt;武器&gt;.prefab</c>，
        /// 量法 = idle 姿态下"沿相机前向最靠前的 2 cm 内顶点"的质心（与 <see cref="MuzzleLateral"/> 同一量法），
        /// 实测跨度 <c>p228</c> 0.335 m ~ <c>sg550</c> 0.925 m（2.8 倍）⇒ 用一个常数必然让短枪的火焰飘在
        /// 枪口前方、长枪的火焰埋进枪管里。</para>
        ///
        /// <para><b>表里没有的武器</b>走 <see cref="MuzzleAxialFallback"/>：取**同类别**已测武器的中位数
        /// （类别取自 <c>CsWeaponDef.Class</c>）—— 那一档是**类别推断**，不是实测值。</para>
        /// </summary>
        public static float MuzzleAxial(string weaponId)
        {
            switch (weaponId)
            {
                // ---- 手枪（实测 0.335~0.549）----
                case CsWeapons.Glock18: return 0.4339f;
                case CsWeapons.Usp: return 0.5485f;
                case CsWeapons.P228: return 0.3348f;
                case CsWeapons.Deagle: return 0.3789f;
                case CsWeapons.FiveSeven: return 0.3859f;
                case CsWeapons.Elite: return 0.3989f;
                // ---- 冲锋枪（实测 0.381~0.584）----
                case CsWeapons.Mp5: return 0.5069f;
                case CsWeapons.Tmp: return 0.5837f;
                case CsWeapons.Mac10: return 0.3809f;
                case CsWeapons.Ump45: return 0.5134f;
                case CsWeapons.P90: return 0.3819f;
                // ---- 步枪（实测 0.553~0.722）----
                case CsWeapons.Galil: return 0.7168f;
                case CsWeapons.Famas: return 0.5615f;
                case CsWeapons.Ak47: return 0.6965f;
                case CsWeapons.M4A1: return 0.7223f;
                case CsWeapons.Sg552: return 0.5533f;
                case CsWeapons.Aug: return 0.5640f;
                // ---- 狙击枪（实测 0.670~0.925）----
                case CsWeapons.Scout: return 0.6701f;
                case CsWeapons.Awp: return 0.7039f;
                case CsWeapons.G3sg1: return 0.7200f;
                case CsWeapons.Sg550: return 0.9249f;
                // ---- 霰弹枪（实测 0.531 / 0.717）----
                case CsWeapons.M3: return 0.5313f;
                // 逐顶点质心（0.668）落在护木/泵上、不是枪口 ⇒ 取该武器包围盒前沿的实测值
                case CsWeapons.Xm1014: return 0.7169f;
                // ---- 机枪（实测）----
                case CsWeapons.M249: return 0.5442f;
                default: return MuzzleAxialFallback(weaponId);
            }
        }

        /// <summary>
        /// <see cref="MuzzleAxial"/> 表外的武器的轴向距离（米）。**类别推断**：取同类别**已测**武器的中位数
        /// （手枪 0.3924 / 冲锋枪 0.5069 / 步枪 0.6303 / 狙击 0.7120 / 霰弹 0.6241 / 机枪 0.5442）；
        /// 连武器定义都没有时取全部已测武器的中位数 0.5509。
        /// </summary>
        private static float MuzzleAxialFallback(string weaponId)
        {
            var def = CsWeapons.Get(weaponId);
            if (def == null) return 0.5509f;

            switch (def.Class)
            {
                case CsWeaponClass.Pistol: return 0.3924f;
                case CsWeaponClass.SMG: return 0.5069f;
                case CsWeaponClass.Rifle: return 0.6303f;
                case CsWeaponClass.Sniper: return 0.7120f;
                case CsWeaponClass.Shotgun: return 0.6241f;
                case CsWeaponClass.MachineGun: return 0.5442f;
                default: return 0.5509f;
            }
        }

        /// <summary>枪口火焰落点的相机局部 z（米）= <see cref="MuzzleAxial"/> + <see cref="MuzzleForwardClearance"/>。</summary>
        public static float MuzzleForward(string weaponId)
        {
            return MuzzleAxial(weaponId) + MuzzleForwardClearance;
        }

        /// <summary>
        /// 逐武器枪口火焰**贴片世界尺寸**（米）。**类别推断**（原版逐武器的尺寸是引擎选择例程
        /// 的一个立即数、由调用方传入，本机拿不到 ⇒ 无出处）：
        /// 手枪 0.24 / 冲锋枪 0.27 / 步枪 0.30 / 狙击 0.33 / 霰弹 0.36 / 机枪 0.42 m。
        /// 类别序的理由 = 口径与装药越大，枪口焰越亮越大；基准档取**步枪 0.30 m**（= 本工程既有值）。
        ///
        /// <para><b>单位是"米"，调用点必须经 <c>CombatEffects.SpriteScaleForMeters</c> 换成倍率</b>：
        /// 贴片导入 PPU=100，64 px 天生只有 0.64 世界单位宽，直接写 `Vector3.one * 0.30f`
        /// 画出来只有 0.192 m（= 0.64 × 0.30，小 3.1 倍）—— 同族口径见 <see cref="DecalSize"/>。</para>
        /// </summary>
        public static float MuzzleFlashSize(string weaponId)
        {
            switch (MuzzleClass(weaponId))
            {
                case CsWeaponClass.Pistol: return 0.24f;
                case CsWeaponClass.SMG: return 0.27f;
                case CsWeaponClass.Rifle: return 0.30f;
                case CsWeaponClass.Sniper: return 0.33f;
                case CsWeaponClass.Shotgun: return 0.36f;
                case CsWeaponClass.MachineGun: return 0.42f;
                default: return 0.30f;
            }
        }

        /// <summary>
        /// 逐武器枪口火焰**存活时长**（秒）。**类别推断**（同 <see cref="MuzzleFlashSize"/>：原版时长由引擎按
        /// 贴片帧数推进，逐武器值在 <c>hw.dll</c>、不在盘）：手枪 0.040 / 冲锋枪 0.045 / 步枪 0.050 /
        /// 狙击 0.055 / 霰弹 0.050 / 机枪 0.045 s。
        ///
        /// <para>取值带的两条硬边界：**下界 ≥ 一帧**（<c>1/60 s</c>，否则可能整发看不见）、
        /// **上界 &lt; 该武器一次射击的最小间隔**（否则相邻两发的火焰在画面上重叠成一团）。</para>
        /// </summary>
        public static float MuzzleFlashDuration(string weaponId)
        {
            switch (MuzzleClass(weaponId))
            {
                case CsWeaponClass.Pistol: return 0.040f;
                case CsWeaponClass.SMG: return 0.045f;
                case CsWeaponClass.Rifle: return 0.050f;
                case CsWeaponClass.Sniper: return 0.055f;
                case CsWeaponClass.Shotgun: return 0.050f;
                case CsWeaponClass.MachineGun: return 0.045f;
                default: return 0.045f;
            }
        }

        /// <summary>武器类别（拿不到武器定义时返回 <see cref="CsWeaponClass.Equipment"/> = 走默认档）。</summary>
        private static CsWeaponClass MuzzleClass(string weaponId)
        {
            var def = CsWeapons.Get(weaponId);
            return def != null ? def.Class : CsWeaponClass.Equipment;
        }

        public const float MuzzleLightDuration = 0.055f;

        /// <summary>
        /// 弹痕贴片**整块画布**的世界尺寸（米）= 16 GoldSrc 单位 = **0.4064 m**。
        ///
        /// <para><b>出处 = 原版引擎的贴花尺寸公式</b>（<c>原版资源/cs16src/hw.dll</c>）：
        /// ① <c>pfnDecalShoot</c> VA <c>0x1D56AA0</c>（函数指针表 file <c>0x18264C</c>，= <c>cl_enginefunc_t</c>
        /// 偏移 <c>0xD4</c>）把 scale **写死**为 <c>push 1.0f</c>（VA <c>0x56AB3</c>，调用方传的第 6 实参被丢弃）；
        /// ② 内部 <c>R_DecalShoot</c>（VA <c>0x1D56770</c>）把 <c>1.0f</c> 改写成 <c>-1.0f</c> 哨兵（= 用默认）；
        /// ③ 尺寸例程（RVA <c>0x55E20</c> 起）<c>size = (scale == -1) ? |texinfo-&gt;vecs[0]| : scale</c>，
        /// 贴花世界尺度 = <c>decalTexture.width</c>（texel）× <c>size</c>
        /// ⇒ 16 texel × 1.0 世界单位/texel = **16 单位**；1 单位 = 1 inch ⇒ ×
        /// <see cref="CsConst.UnitToMeter"/> = 0.4064 m。</para>
        ///
        /// <para><b>载体</b>：<c>decals.wad</c> 的 <c>{shot1..5</c> 各 16×16、RGB 纯黑、alpha 为不透明度掩码
        /// （<c>alpha≥32</c> 的 13~16 texel / 256 才是会改变屏幕像素的墨迹）。世界尺度按**贴花自己的 texel 宽**
        /// 给 ⇒ 换任何一张贴花都自动同比例。</para>
        ///
        /// <para><b>不是 0.4096</b>：那是把 1 单位当 0.0256 m 的算法；本工程唯一换算口径是
        /// <see cref="CsConst.UnitToMeter"/> = 0.0254（出处 <c>原版资源/cs16src/cs16_build.py:43</c>
        /// <c>HL_UNIT = 0.0254</c>）。</para>
        ///
        /// <para>投影（1920 px / 水平 90°）：可见墨迹 <see cref="DecalVisibleCoreMeters"/> = 0.127 m
        /// ⇒ 5 m 处 ≈ 24 px。全工程**只有这一个旋钮**决定贴花大小（血迹按
        /// <see cref="DecalMetersPerPixel"/> 同比例联动）。</para>
        /// </summary>
        public const float DecalSize = 16f * CsConst.UnitToMeter;

        /// <summary>
        /// 墙上的贴花（弹痕 / 血迹）的 **alpha 裁切阈值** = 0.25 —— alpha 不高于它的像素整片丢弃
        /// （<c>clip(a - 0.25)</c>）。
        ///
        /// <para><b>出处 = 原版 cvar <c>gl_alphamin</c> 的默认值 <c>"0.25"</c></b>
        /// （<c>hw.dll</c> cvar 结构 VA <c>0x01E76D48</c>、值 VA <c>0x01E76D54</c>）。贴花绘制函数
        /// （RVA <c>0x57800</c>）在 <c>0x0057822</c> 打开 <c>GL_ALPHA_TEST</c> 但**不设** <c>glAlphaFunc</c>
        /// ⇒ 阈值继承引擎上一次设置，全引擎唯一的直接设置点就是 <c>0x00549F8</c> 读 <c>[0x1E76D54]</c>、
        /// <c>0x00549FF push 0x0204</c>（<c>GL_GREATER</c>）、<c>0x0054A04 call glAlphaFunc</c>。</para>
        ///
        /// <para><b>为什么必须裁</b>：<c>{shot*</c> 的载体是 16×16 里只有 13~16 个 <c>alpha≥32</c> 的 texel
        /// 的软晕；不裁时整块软晕都参与混合，黑贴花压暗量 = <c>alpha × dst</c>，软晕那部分几乎不改墙面 ⇒
        /// 墙上只剩一小块极淡的暗斑。裁掉 <c>alpha ≤ 0.25</c> 之后只画硬核，读感与"一个洞"一致。</para>
        /// </summary>
        public const float DecalAlphaCutoff = 0.25f;

        /// <summary>
        /// 弹痕**可见墨迹**占整块画布宽度的比例 = 5/16 = 0.3125。
        ///
        /// <para><b>出处 = 载体逐像素实测</b>（<c>decals.wad</c> 的 <c>{shot1..5</c>，读的就是进工程的
        /// 同一批 PNG）：16×16、RGB 纯黑、alpha = 不透明度掩码；<c>alpha≥32</c>（= 会真的改变屏幕像素的
        /// 墨迹）的水平跨度 = 4~5 px（总像素 13~16 / 256），<c>alpha≥160</c> 的实心黑核心只有 1~3 px 宽。
        /// 前者是"看得见"的直接成因。</para>
        ///
        /// <para>运行期日志按它打出"可见墨迹的世界宽度 / 屏幕投影"（见
        /// <see cref="DecalVisibleCoreMeters"/>）。</para>
        /// </summary>
        public const float DecalOpaqueCoreRatio = 5f / 16f;

        /// <summary>参考屏幕上该距离处 1 米世界宽度投成的像素数（1920 px 宽 / 水平 90° FOV ⇒ <c>1920/(2d)</c>）。</summary>
        public static float ScreenPixelsPerMeter(float distanceMeters)
        {
            if (distanceMeters <= 0.0001f) return 0f;
            const float screenW = 1920f;      // HUD 画布参考宽（CsHudTheme 的 referenceResolution）
            const float halfFovTan = 1f;      // 水平 90° ⇒ tan(45°) = 1
            return screenW / (2f * distanceMeters * halfFovTan);
        }

        /// <summary>弹痕**可见核心**的世界宽度（米）= <see cref="DecalSize"/> × <see cref="DecalOpaqueCoreRatio"/>。</summary>
        public static float DecalVisibleCoreMeters => DecalSize * DecalOpaqueCoreRatio;

        /// <summary>弹痕存活时长（秒）。原版 decal 会留很久（受 decal 数量上限控制），这里给足 25s。</summary>
        public const float DecalDuration = 25f;

        /// <summary>同时存在的弹痕上限（超出复用最旧的一条）—— 防止长扫射把特效池撑满。</summary>
        public const int MaxDecals = 64;

        // ------------------------------------------------------------------
        // ------------------------------------------------------------------
        /// <summary>
        /// 贴花的"米/texel" = <see cref="DecalSize"/> / 16 = **0.0254 m/texel**（= 1 GoldSrc 单位/texel）。
        ///
        /// <para>出处同 <see cref="DecalSize"/>：原版尺寸公式里 <c>size</c> 的默认值就是
        /// <c>|texinfo-&gt;vecs[0]|</c> —— 1:1 地图纹理上等于 1 世界单位/texel，
        /// 而贴花世界尺度 = <c>decalTexture.width</c> × <c>size</c> ⇒ 逐 texel = 1 单位。</para>
        ///
        /// <para>用途：<see cref="BloodDecalSize"/> 按各张血迹贴花自己的像素宽给世界尺寸
        /// （48 px → 1.2192 m、64 px → 1.6256 m）。</para>
        /// </summary>
        public const float DecalMetersPerPixel = DecalSize / 16f;

        /// <summary>
        /// 血迹贴花的世界尺寸（米）—— 按**该张贴花自己的像素宽**反算（见 <see cref="DecalMetersPerPixel"/>）。
        /// 例：48 px → 1.2192 m、64 px → 1.6256 m。
        /// </summary>
        public static float BloodDecalSize(int pixels)
        {
            // 本文件刻意不 `using UnityEngine`（只放常量，与 CsConst 同处置）⇒ 不用 Mathf。
            return pixels <= 0 ? 0.001f : pixels * DecalMetersPerPixel;
        }

        /// <summary>血迹贴花存活时长（秒）。与弹痕同量级（原版贴花同为"留很久、按数量上限回收"）。</summary>
        public const float BloodDecalDuration = 25f;

        /// <summary>同时存在的血迹贴花上限（与弹痕分开计数，否则扫射会把血迹挤掉）。</summary>
        public const int MaxBloodDecals = 24;

        /// <summary>
        /// 血雾贴片存活时长（秒）。出处：**本项目新增**（载体 `valve/sprites/bloodspray.spr` 给的是
        /// 10 帧像素，**帧时长在引擎**里、载体没给 ⇒ 同枪口火焰时长同一处置）。
        /// </summary>
        public const float BloodPuffDuration = 0.14f;

        /// <summary>血雾贴片的世界尺寸（米）= 0.18（本项目新增；取"命中点上一小团、不遮住对手"）。
        /// 载体是 64×64 贴图（PPU=100 ⇒ 天生 0.64 世界单位），调用点必须走
        /// <c>CombatEffects.SpriteScaleForMeters</c> 换成倍率，⛔ 不能直接写米。</summary>
        public const float BloodPuffSize = 0.18f;

        /// <summary>同一发命中里各张血雾贴片的散开半径（米）= 0.06（本项目新增：让一团血雾有厚度，
        /// 否则多张贴片完全重合、画面与单张无差别）。</summary>
        public const float BloodPuffSpread = 0.06f;

        /// <summary>
        /// 一发命中**至少**出几张血雾贴片 = 3。
        ///
        /// <para><b>出处 = 原版 CS 1.6 的 `mp.dll`</b>：血迹临时实体的数量字节是
        /// <c>clamp(amount / 10, 3, 16)</c> —— 下界立即数 <c>3</c> 在 VA <c>0x1008f687</c>、
        /// 上界立即数 <c>0x10</c>（=16）在 VA <c>0x1008f67b</c>，`/10` 由 <c>0x66666667</c> 魔数除法实现
        /// （VA <c>0x1008f676</c>）。</para>
        ///
        /// <para><b>为什么本工程取的是**下界**</b>：公式的自变量 <c>amount</c> 来自受击者实体上的一个浮点
        /// 场（`(int)pev->field_0x1e0`，读取点 VA <c>0x100395cf</c>），命中层的伤害值在
        /// <c>Module/Match</c> 里、不在表现层可达 ⇒ 本层拿不到 `amount`，只能取有出处的下界 3。
        /// 要按 <c>clamp(damage/10, 3, 16)</c> 分流 ⇒ 需要把伤害透传到 <c>ICsMatch.OnBulletHit</c>
        /// （跨 Match 层，不在本片写入范围）。缺口登记在 `策划/差异登记.tsv` #74。</para>
        /// </summary>
        public const int BloodSprayMinCount = 3;

        /// <summary>一发命中的血雾贴片**上限** = 16（出处见 <see cref="BloodSprayMinCount"/>：
        /// 立即数 <c>0x10</c> 在 VA <c>0x1008f67b</c>）。本层因拿不到 `amount` 暂不使用，留作口径登记。</summary>
        public const int BloodSprayMaxCount = 16;

        /// <summary>每张血雾贴片对应的"量" = 10（出处见 <see cref="BloodSprayMinCount"/>：`clamp(amount/10,3,16)`）。
        /// 同样因 `amount` 不可达而暂未参与计算，留作口径登记。</summary>
        public const int BloodSprayAmountPerSprite = 10;

        /// <summary>
        /// 从**命中点**沿弹道方向继续找"能贴血迹的面"的最大距离（米）。
        ///
        /// <para>口径来源：原版是"子弹命中角色后，在**后面的墙面**上贴血迹贴花"
        /// （`mp.dll` 的 `DecalGunshot` 链路 + `{blood1..6` 名表），而不是把血迹贴在角色身上。
        /// 所以从命中点往前追一小段（2.5 m 覆盖"贴脸打墙"到"隔着一两步"）。</para>
        /// </summary>
        public const float BloodDecalTraceRange = 2.5f;

        /// <summary>击中火星的存活时长（秒）。</summary>
        public const float SparkDuration = 0.05f;

        /// <summary>击中火星的世界尺寸（米）。</summary>
        public const float SparkSize = 0.05f;
        public const float TracerDuration = 0.05f;
        public const float TracerThickness = 0.022f;
        public const float ExplosionVisualDuration = 0.28f;
        public const float ExplosionVisualMaxRadius = 1.6f;
        public const float GrenadeVisualRadius = 0.09f;

        /// <summary>单帧最多消费的"待表现射击"条数（与模拟的 MaxPendingShots 同量级）。</summary>
        public const int MaxShotsConsumedPerFrame = 16;

        // ==================================================================
        // ==================================================================
        //  弹壳（模型与贴图来自原版 mdl，见 CsShellModels）
        // ==================================================================
        // 出处口径：几何 / 贴图 = 原版 models/{rshell,pshell,rshell_big}.mdl 逐点搬运；
        // 下面这批运动参数 = 原版抛壳代码的常量与 RANDOM 区间，逐条列在各自注释的地址里。
        // 服务端出处 = 原版 cstrike/dlls/mp.dll 的 CBasePlayerWeapon::EjectBrassLate（RVA 0x946D0，
        // 抛壳口 0x9480F / 初速 0x9470A）；引擎侧出处 = hw.dll（1 单位 = CsConst.UnitToMeter）。

        /// <summary>抛壳口·前（相机局部系 z，米）= 原版 forward*16 单位。
        /// 出处：mp.dll RVA 0x9480F 的 <c>[gpGlobals+0x28] * [0x101420BC]=16.0f</c>。</summary>
        public const float ShellPortForward = 16f * CsConst.UnitToMeter;

        /// <summary>抛壳口·横（相机局部系 x，米）= 原版 right*(-9) 单位（**负值 = 观察者左侧**）。
        /// 出处：mp.dll RVA 0x9480F 的 <c>[gpGlobals+0x34] * [0x101423CC]=-9.0f</c>（gpGlobals->v_right）。</summary>
        public const float ShellPortLateral = -9f * CsConst.UnitToMeter;

        /// <summary>抛壳口·下（相机局部系 y，米）= 原版 up*(-9) 单位（负值 = 眼睛下方）。
        /// 出处：mp.dll RVA 0x9480F 的 <c>[gpGlobals+0x40] * [0x101423CC]=-9.0f</c>（gpGlobals->v_up）。</summary>
        public const float ShellPortVertical = -9f * CsConst.UnitToMeter;

        /// <summary>初速·上（米/秒）= 原版 <c>v_up * RANDOM_FLOAT(100,150)</c> 单位/秒 —— 三个分量里最大的一项。
        /// 出处：mp.dll RVA 0x9470A（常量 100.0f @VA 0x10142194 / 150.0f @VA 0x101421C0）。</summary>
        public const float ShellSpeedUpMin = 100f * CsConst.UnitToMeter;
        public const float ShellSpeedUpMax = 150f * CsConst.UnitToMeter;

        /// <summary>初速·右（米/秒）= 原版 <c>v_right * RANDOM_FLOAT(50,70)</c> 单位/秒。
        /// 出处：mp.dll RVA 0x94745（常量 50.0f @VA 0x10142138 / 70.0f @VA 0x10142168）。</summary>
        public const float ShellSpeedRightMin = 50f * CsConst.UnitToMeter;
        public const float ShellSpeedRightMax = 70f * CsConst.UnitToMeter;

        /// <summary>初速·前（米/秒）= 原版 <c>v_forward * 25.0</c> 单位/秒。
        /// 出处：mp.dll RVA 0x9470A 区段（常量 25.0f @VA 0x101420E8）。</summary>
        public const float ShellSpeedForward = 25f * CsConst.UnitToMeter;

        /// <summary>自旋角速度逐轴区间（度/秒）= 引擎侧 <c>RANDOM_FLOAT(-512,511) / (-256,255) / (-256,255)</c>。
        /// 出处：hw.dll RVA 0x2F16C（cl_enginefuncs 表 +0xC0 = 生成 tempent 的引擎例程）。
        /// 原版把这个向量再整体乘一个正整数，该整数来自 client.dll 生成器的实参表、
        /// **未回溯到具体值** ⇒ 这里取 RANDOM 原式（等价于乘 1），即原版量级的下界。</summary>
        public const float ShellSpinXMin = -512f;
        public const float ShellSpinXMax = 511f;
        public const float ShellSpinYMin = -256f;
        public const float ShellSpinYMax = 255f;
        public const float ShellSpinZMin = -256f;
        public const float ShellSpinZMax = 255f;

        /// <summary>弹壳存活时长（秒）= 原版 2.0 s。
        /// 出处：client.dll RVA 0x4600F（push 2.0f）+ hw.dll RVA 0x2F203（写 tempent 的 die 槽）。</summary>
        public const float ShellLife = 2.0f;

        /// <summary>弹壳的碰撞半径（米）：射线终点外扩、命中后沿法线抬起都用它。
        /// 出处：**本项目新增**（取载体半径量级 0.4 GoldSrc 单位 ≈ 0.01 m 的下侧）。</summary>
        public const float ShellRadius = 0.006f;

        /// <summary>撞面后速度保留比例。出处：**未取到**（原版 tempent 每帧的碰撞反射在 hw.dll 里
        /// 没有可定位锚点）⇒ 保持工程原值，缺口见 策划/差异登记.tsv。</summary>
        public const float ShellBounceDamping = 0.35f;

        /// <summary>低于该速率（米/秒）即"躺下"（贴面静止）。出处：**本项目新增**。</summary>
        public const float ShellRestSpeed = 0.25f;

        /// <summary>命中标记音（普通）。</summary>
        public const string HitMarkerSfx = "sfx/hitmarker";

        /// <summary>命中标记音（爆头）。</summary>
        public const string HitMarkerHeadshotSfx = "sfx/hitmarker_head";

        /// <summary>缺资源时日志降频口径（首次 + 每 N 次）。</summary>
        public const int LogRateEvery = 50;
    }
}
