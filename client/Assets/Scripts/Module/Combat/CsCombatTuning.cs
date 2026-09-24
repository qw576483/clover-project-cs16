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
        /// <summary>枪口火焰持续时间（秒）。出处：**本项目新增**。
        /// 四张 `.spr` 现在都在盘（`原版资源/cs16src/cstrike/cstrike__sprites__muzzleflash1..4.spr`），
        /// 但它们只给出"每张几帧"（`muzzleflash2/3` 各 3 帧），给不出**总时长**（引擎 `hw.dll` 不在盘）
        /// ⇒ 时长仍是本项目新增，缺口记在 `策划/差异登记.tsv` #89。</summary>
        public const float MuzzleFlashDuration = 0.045f;

        /// <summary>
        /// 枪口火焰**贴片**的世界尺寸（米）= 0.30m（本项目新增，登记在 `策划/差异登记.tsv` #89）。
        ///
        /// <para><b>2026-09-24 更正</b>：旧注释说"载体不在盘 ⇒ 这个数无出处"，前半句已不成立
        /// （四张 `.spr` 在盘：48×48 / 64×64 / 72×72 / 48×48），但**没有任何载体给出"火焰的世界尺寸"**
        /// —— 那是引擎的投影选择 ⇒ 仍按本项目新增记。</para>
        ///
        /// <para><b>单位是"米"，但必须经 <c>SpriteScaleForMeters</c> 换成倍率</b>：贴片导入 PPU=100，
        /// 64 px 天生只有 0.64 世界单位宽，直接 `Vector3.one * 0.30f` 画出来只有 0.192 m
        /// （= 0.64 × 0.30，小 3.1 倍）—— 同族口径见 <see cref="DecalSize"/> 的注释与
        /// <c>CombatEffects.SpriteScaleForMeters</c>。</para>
        /// </summary>
        public const float MuzzleFlashSize = 0.30f;

        // ------------------------------------------------------------------
        // ------------------------------------------------------------------
        /// <summary>
        /// 枪口火焰在**相机局部系**里的落点（米）：<c>x</c>=右、<c>y</c>=上、<c>z</c>=前。
        ///
        /// <para><b>为什么不是旧值"相机前方 0.34 m + 右 0.13 + 下 0.09"</b>：
        /// <c>x∈[-0.026,+0.239] y∈[-0.286,-0.062] z∈[-0.087,+0.705]</c>
        /// （逐轴数字抄在 <c>ViewModelRig.EnsureAlwaysAnimate</c> 的注释里）——
        /// 枪管尖在 <c>z ≈ 0.70</c> ⇒ 火焰被放进**枪身内部**；SpriteRenderer 走透明队列且
        /// **开深度测试**，于是被枪身/手臂里 <c>z &lt; 0.34</c> 的那一截几何盖掉，
        /// 表现就是用户 2026-09-24 报的「枪口火焰没有效果」。</para>
        ///
        /// <para><b>本值怎么来的</b>：取上面那条实测包围盒的**最前端**（<c>z=0.705</c>）再往外让
        /// 0.015 m（防 z-fighting）⇒ <c>Forward = 0.72</c>；<c>x / y</c> 取枪管轴在包围盒里的位置
        /// （右偏 + 略低于视平线）。**这不是原版出处**：原版落点在引擎 <c>hw.dll</c>（不在盘）；
        /// 它是**从本工程实测几何反推**的，缺口与判据记在 <c>策划/差异登记.tsv</c> #89。</para>
        /// </summary>
        public const float MuzzleOffsetRight = 0.13f;

        /// <summary>见 <see cref="MuzzleOffsetRight"/>（相机局部系 y；负数 = 视平线以下）。</summary>
        public const float MuzzleOffsetUp = -0.12f;

        /// <summary>见 <see cref="MuzzleOffsetRight"/>（相机局部系 z；正数 = 相机前方）。</summary>
        public const float MuzzleOffsetForward = 0.72f;

        public const float MuzzleLightDuration = 0.055f;

        /// <summary>
        /// 弹痕贴片**整块画布**的世界尺寸（米）= 0.128 m（= 16 px × <see cref="DecalMetersPerPixel"/>）。
        ///
        /// <para><b>复核（用户报「弹痕还是没有」）</b>——
        /// 实测（判据资产 <c>tools/probes/probe-decal-visibility.py</c>，读的就是进工程的同一批 PNG）：
        /// <c>fx_shot1..5</c> 是 16×16、RGB **纯黑 (0,0,0)**、alpha = 不透明度掩码，其中
        /// <b><c>alpha≥32</c> 只有 13~16 px / 256、<c>alpha≥160</c> 只有 2~4 px</b>
        /// ⇒ 真正"黑得看得见"的**核心只有约 4 px 宽 = 整块的 4/16</b>。</para>
        ///
        /// <para>⇒ 旧值下<u>可见墨迹</u> = 0.075 × 5/16 = <b>2.34 cm</b>。按 1920 px / 水平 90° 的投影
        /// （<c>px = 1920 · w / (2d)</c>）：2 m 处只有 <b>约 11 px</b>、4.89 m 处约 4.6 px
        /// 说明"贴了、但只有 19 px 的淡影、其中仅约 5 px 是有墨的"）。这就是用户看不到它的原因。</para>
        ///
        /// <para><b>本值怎么定的</b>（可复算，不是"随手放大"）：把判据写成"**可见墨迹**在
        /// 2 m 处 ≥ 15 px"（2 m = 贴脸打墙的典型距离），反解
        /// <c>墨迹 ≥ 15·2·2/1920 = 3.13 cm</c> ⇒ 整块 ≥ 3.13 × 16/5 = 10.0 cm ⇒ 取 <b>12.8 cm</b>。
        /// 于是墨迹 = 4.0 cm（2 m 处 <b>19.2 px</b>、4.89 m 处 7.8 px），实心核心 = 1.92 cm（2 m 处 9.2 px）。</para>
        ///
        /// <para><b>出处缺口如实登记</b>：原版"贴花世界尺寸"的映射在**引擎**（<c>hw.dll</c>，
        /// 不在盘 ⇒ <c>策划/对照表.md</c> 的 BLOCKED 口径），本值**没有** <c>文件:偏移</c> 级出处，
        /// 属**本项目新增**；缺口记在 <c>策划/差异登记.tsv</c> #69，判据 = 可见性阈值（见上）。
        /// 全工程**只有这一个旋钮**决定弹痕大小（血迹按 <see cref="DecalMetersPerPixel"/> 同比例联动），
        /// 拿到引擎侧出处后**只改这一处**。</para>
        /// </summary>
        public const float DecalSize = 0.128f;

        /// <summary>
        /// 弹痕**可见墨迹**占整块画布宽度的比例 = 5/16 = 0.3125（实测 4~5 px 的上界取值）。
        ///
        /// <para><b>出处 = 载体逐像素实测</b>（判据资产 <c>tools/probes/probe-decal-visibility.py</c>，
        /// 读的就是进工程的同一批 PNG）：<c>decals.wad</c> 的 <c>{shot1..5</c> 是 16×16，RGB 纯黑、
        /// alpha = 不透明度掩码；其中
        /// <b><c>alpha≥32</c>（= 会真的改变屏幕像素的墨迹）的水平跨度 = 4~5 px</b>
        /// （总像素 13~16 / 256），而 <c>alpha≥160</c> 的**实心黑核心**只有 1~3 px 宽。
        /// 判据取前者（它是"看得见"的直接成因），后者只作诊断数报出来。</para>
        ///
        /// <para>用途：运行期日志把"**可见墨迹**的世界宽度 / 屏幕投影"直接打出来，
        /// 让"看不看得见"成为**可核对的数**，而不是"我看了觉得行"。</para>
        /// </summary>
        public const float DecalOpaqueCoreRatio = 5f / 16f;

        /// <summary>可见性判据的参考距离（米）—— 贴脸打墙的典型距离，判据 <see cref="DecalSize"/> 按它反解。</summary>
        public const float DecalVisibleReferenceDistance = 2f;

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
        /// 弹痕的"米/像素"。口径：弹痕载体 `{shot1..5` 是 **16×16**（`decals.wad` 实测），
        /// <see cref="DecalSize"/> 取的是 16 px 那张的世界尺寸 ⇒ 每像素 = DecalSize / 16。
        ///
        /// <para><b>这是什么、不是什么</b>：原版"贴花世界尺寸"的映射在**引擎**里（`hw.dll` 不在盘）
        /// ⇒ 拿不到。这里用的是**同族比例推演**：同一套 decal 载体、同一个换算，
        /// 让 48×48 的血迹 = 弹痕的 3 倍宽、64×64 的 `{blood5` = 4 倍宽。
        /// 这不是出处，出处缺口仍在 <c>策划/差异登记.tsv</c> #69 里；换掉 DecalSize 一处即可全改。</para>
        /// </summary>
        public const float DecalMetersPerPixel = DecalSize / 16f;

        /// <summary>
        /// 血迹贴花的世界尺寸（米）—— 按**该张贴花自己的像素宽**反算（见 <see cref="DecalMetersPerPixel"/>）。
        /// 例：48 px → 0.225 m、64 px → 0.30 m。
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
        /// 命中瞬间的**血雾贴片**存活时长（秒）—— 这是 `sprites/bloodspray.spr` 的降级替身
        /// （该 `.spr` 不在盘；载体缺口登记在 `client/资源欠缺清单.md`）。
        /// </summary>
        public const float BloodPuffDuration = 0.14f;

        /// <summary>血雾贴片的世界尺寸（米）。按"命中点上一小团、不遮住对手"取 0.18m。</summary>
        public const float BloodPuffSize = 0.18f;

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
        /// <summary>命中标记音（普通）。</summary>
        public const string HitMarkerSfx = "sfx/hitmarker";

        /// <summary>命中标记音（爆头）。</summary>
        public const string HitMarkerHeadshotSfx = "sfx/hitmarker_head";

        /// <summary>缺资源时日志降频口径（首次 + 每 N 次）。</summary>
        public const int LogRateEvery = 50;
    }
}
