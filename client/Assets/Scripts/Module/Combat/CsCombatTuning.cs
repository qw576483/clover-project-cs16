namespace Cs16.Module.Combat
{
    /// <summary>
    /// **本项目新增**的第一人称操作 / 射击表现层可调数值。
    ///
    /// <para><b>为什么单独一个文件</b>：与 <c>Cs16.Core.CsConst</c> 同语义（"改一处即可调手感"的旋钮），
    /// 但它们是 <c>Module/Player</c>、<c>Module/CameraRig</c>、<c>Module/Combat</c> 三处共用的，
    /// 且 <c>CsConst</c> 是**契约文件（不许改）** —— 与 agent-03 的 <c>CsMatchConst</c> 同一处置方式。
    /// 目的同样是：**业务脚本里不出现裸数字**。</para>
    ///
    /// <para><b>改这里必须同步回报</b>：这些值直接决定"像不像 CS 1.6"（后坐力表现、准星扩散、开镜、bob）。</para>
    ///
    /// <para><b>出处口径（切片L 逐条补）</b>：本文件的量**绝大多数是「本项目新增」的操作 / 表现层旋钮** ——
    /// 它们对应的是 CS 1.6 **客户端**手感（后坐力 / 散布 / 准星扩散 / bob / 开镜 / 受击晃动 / 枪口火焰），
    /// 而原版把这些量写死在 <c>cstrike/cl_dlls/client.dll</c> 与 <c>mp.dll</c> 的逐武器代码里
    /// （不是 cvar、也不是数据表 —— 见 <c>策划/对照表.md</c> §6 BLOCKED-1 / BLOCKED-2）
    /// ⇒ 本机拿不到它们的 <c>文件:偏移</c> 级出处（载体 <c>原版资源/cs16src</c> 已空，见 <c>原版资源/清单.md</c>）。
    /// 因此逐条只标「本项目新增」+ 该条与 A 的关系；⛔ 这些标注**不是**"已 1:1 对齐原版"的证据。
    /// 两条例外：<c>PitchLimit</c>（口径同 <c>CsActor.Pitch</c>）、<c>DegreesPerMouseCount</c>（原版 <c>m_yaw</c> 默认值）。</para>
    /// </summary>
    internal static class CsCombatTuning
    {
        // ==================================================================
        //  视角 / 鼠标
        // ==================================================================
        /// <summary>俯仰角上限（度）。与 <c>CsActor.Pitch</c> 的 -89~89 约定、agent-03 的钳制口径一致。
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
        /// <summary>枪口火焰持续时间（秒）。出处：**本项目新增**（原版 <c>sprites/muzzleflash*.spr</c>
        /// 载体不在盘，见 <c>策划/对照表.md</c> §9 附录「枪口火焰/弹痕」）。</summary>
        public const float MuzzleFlashDuration = 0.045f;

        /// <summary>
        /// 枪口火焰**贴片**的世界尺寸（米）= 0.30m。
        /// 口径来自原版：`sprites/muzzleflash1.spr` 的原画布约 96×96，在 4:3、fov 90（水平）下
        /// 贴片落在枪口前方 ~0.3m 处时约占屏幕 6~7%（≈0.3m）—— 本工程取同一量级；
        /// ⛔ 原版 `.spr` 载体不在仓库 ⇒ 这个数是**本项目新增**（登记在验收表「允许的差异」）。
        /// </summary>
        public const float MuzzleFlashSize = 0.30f;

        public const float MuzzleLightDuration = 0.055f;

        /// <summary>弹痕贴片的世界尺寸（米）= 0.075m（原版 decal 的观感量级：~7cm 的弹孔）。</summary>
        public const float DecalSize = 0.075f;

        /// <summary>弹痕存活时长（秒）。原版 decal 会留很久（受 decal 数量上限控制），这里给足 25s。</summary>
        public const float DecalDuration = 25f;

        /// <summary>同时存在的弹痕上限（超出复用最旧的一条）—— 防止长扫射把特效池撑满。</summary>
        public const int MaxDecals = 64;

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
        //  音效名（**必须与 agent-07 的 Resources/Sound/SFX 一致**）
        // ==================================================================
        /// <summary>命中标记音（普通）。</summary>
        public const string HitMarkerSfx = "sfx/hitmarker";

        /// <summary>命中标记音（爆头）。</summary>
        public const string HitMarkerHeadshotSfx = "sfx/hitmarker_head";

        /// <summary>缺资源时日志降频口径（首次 + 每 N 次）。</summary>
        public const int LogRateEvery = 50;
    }
}
