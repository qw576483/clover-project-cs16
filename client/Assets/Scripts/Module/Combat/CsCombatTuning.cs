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
    /// </summary>
    internal static class CsCombatTuning
    {
        // ==================================================================
        //  视角 / 鼠标
        // ==================================================================
        /// <summary>俯仰角上限（度）。与 <c>CsActor.Pitch</c> 的 -89~89 约定、agent-03 的钳制口径一致。</summary>
        public const float PitchLimit = 89f;

        /// <summary>
        /// 鼠标灵敏度换算系数（度 / 每个鼠标计数），即官方 CS 1.6 的 <c>m_yaw</c> 默认值。
        /// <para>引擎的 <c>Game.Input.MouseDelta</c> 给的是像素位移，与 CS 的"计数"在同一量级
        /// （400DPI 鼠标 1 英寸 ≈ 400 计数 ≈ 400 像素），所以
        /// <c>角度 = MouseDelta * MouseSensitivity * 本系数</c> 与 CS 的手感同量纲。
        /// 灵敏度本身来自玩家设置（<c>CsPlayerSettings.MouseSensitivity</c>，默认 3.0 = CS 默认值）。</para>
        /// </summary>
        public const float DegreesPerMouseCount = 0.022f;

        // ==================================================================
        //  后坐力（只做表现；权威累加在比赛模拟里）
        // ==================================================================
        /// <summary>视角后坐力放大系数（1 = 与模拟累加的度数 1:1，视觉上"抬多少就是多少"）。</summary>
        public const float RecoilViewScale = 1f;

        /// <summary>后坐力"起跳"跟随时间常数（秒）：越小越跟手。</summary>
        public const float RecoilRiseTau = 0.035f;

        /// <summary>后坐力"回正"时间常数（秒）：比上跳慢，形成"抬得快、落得慢"的观感。</summary>
        public const float RecoilFallTau = 0.13f;

        // ==================================================================
        //  散布（子弹角度）
        // ==================================================================
        /// <summary>开镜（Zoom）时的散布倍率。</summary>
        public const float ZoomSpreadScale = 0.25f;

        /// <summary>蹲下时的散布倍率。</summary>
        public const float CrouchSpreadScale = 0.7f;

        /// <summary>离地（跳跃/下落）时额外附加的散布倍率（乘 <c>def.MoveSpread</c>）。</summary>
        public const float AirSpreadScale = 0.5f;

        /// <summary>每发连射累积的散布惩罚（度/发），模拟"越扫越散"。</summary>
        public const float RecoilSpreadPerShot = 0.15f;

        /// <summary>连射散布惩罚的上限（度），防止长时间扫射把散布堆到荒谬值。</summary>
        public const float RecoilSpreadMax = 2.4f;

        // ---- 穿墙 ----
        /// <summary>允许继续穿透的武器门槛（<c>CsWeaponDef.ArmorPenetration</c> 高于它才可穿）。</summary>
        public const float PenetrationArmorThreshold = 0.6f;

        /// <summary>最多穿过几层"墙"（命中非角色的碰撞体算一层）。</summary>
        public const int MaxPenetrationLayers = 2;

        /// <summary>单发最多接受的射线命中数（缓冲上限；超出即丢弃，见 <c>Firearm</c>）。</summary>
        public const int MaxRayHits = 32;

        // ==================================================================
        //  准星扩散（0~1，供 HUD 读）
        // ==================================================================
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
        /// <summary>FOV 过渡时间常数（秒）——即"开镜延迟"的可见部分（AWP/Scout）。</summary>
        public const float ZoomTransitionTau = 0.06f;

        /// <summary>开镜"稳定"所需时间（秒）：未稳定前不享受开镜精度加成（cs 的 quickscope 惩罚）。</summary>
        public const float ScopeSettleTime = 0.18f;

        /// <summary>视高平滑时间常数（秒）：站/蹲切换与台阶上下不"跳眼睛"。</summary>
        public const float EyeHeightSmoothTau = 0.045f;

        /// <summary>超过该位移（米）认为发生了瞬移（出生/观战切换），视高直接吸附不平滑。</summary>
        public const float TeleportSnapDistance = 2.5f;

        /// <summary>受击晃动最大角度（度，满伤害时）。</summary>
        public const float DamageShakeMaxDeg = 2.4f;

        /// <summary>受击晃动持续时间（秒）。</summary>
        public const float DamageShakeDuration = 0.28f;

        /// <summary>受击晃动幅度对应的伤害参考值（伤害 >= 它即取最大晃动）。</summary>
        public const float DamageShakeFullDamage = 45f;

        // ==================================================================
        //  视点晃动（bob）
        // ==================================================================
        /// <summary>bob 幅度到达目标值的速度（每秒的归一化变化量）。</summary>
        public const float BobBlendSpeed = 3.2f;

        /// <summary>bob 的横滚角幅度（度）。</summary>
        public const float BobRollDegrees = 0.55f;

        /// <summary>落地"沉一下"的竖直幅度（米）。</summary>
        public const float LandDipAmount = 0.05f;

        /// <summary>落地沉降的恢复时间常数（秒）。</summary>
        public const float LandDipTau = 0.11f;

        // ==================================================================
        //  表现（枪口火焰 / 弹道 / 手雷视觉）
        // ==================================================================
        public const float MuzzleFlashDuration = 0.045f;
        public const float MuzzleFlashScale = 0.085f;
        public const float MuzzleLightDuration = 0.055f;
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
