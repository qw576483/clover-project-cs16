namespace Cs16.Module.Audio
{
    /// <summary>
    ///
    /// <para><b>命名口径（必须与 <c>Module/Combat</c> 对齐）</b>：引擎的 <c>Game.Sound.PlaySFX(name)</c> 会加载
    /// <c>Resources/Sound/SFX/{name}</c>；本工程约定 <c>name = "sfx/&lt;短名&gt;"</c>
    /// 由 <c>tools</c> 侧的 <c>cs16_build.py</c> 从 CS 1.6 原始 wav 生成。**改短名 = 同时改这里和生成脚本**。</para>
    ///
    /// <para><b>为什么本文件每一行都要有出处</b>（切片K 补）：本类的常量分两类 ——
    /// ① **短名**（<c>"sfx/xxx"</c>）：出处 = 它在盘上的那个资源文件，以及"原版哪一个 wav"的映射，
    /// ② **数值**（间隔 / 并发上限 / 距离）：出处 = 本项目音频层的实现参数（原版无对应量），
    /// 或本工程另有定义处的那个文件。查不到出处的**不许编**，一律登记 <c>策划/差异登记.tsv</c>。</para>
    ///
    /// <para><b>为什么不重复播枪声</b>：枪声（含机器人枪声）由 <c>CombatModule</c> 统一发放
    /// （它是射击队列的唯一消费者）。本模块只补它没做的：脚步 / 落地 / 换弹 / 命中反馈 / 死亡 /
    /// 回合开始结束 / 炸弹蜂鸣与爆炸。</para>
    /// </summary>
    internal static class CsAudioTuning
    {
        // ==================================================================
        //  引擎侧路径（SFX 根目录真源 = Core/ResPaths.cs 的 SoundSfxPrefix）
        // ==================================================================
        /// <summary>引擎 <c>Sound</c> 之下的短名前缀（根目录前缀真源 = <see cref="ResPaths.SoundSfxPrefix"/>）。
        /// 出处：<c>Core/ResPaths.cs</c> 的 <c>SoundSfxPrefix = "Sound/SFX/"</c>（"前缀 + 短名"的用法固定，
        /// 值逐字搬自该处，见 ResPaths 类注释）。</summary>
        public const string ClipRoot = "sfx/";

        private static string S(string shortName) => ClipRoot + shortName;

        // ==================================================================
        //  脚步 / 落地 / 跳跃
        // ==================================================================
        /// <summary>4 个脚步变体。出处：原版 CS 1.6 <c>sound/player/pl_step1..4.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:32</c> 第 6 项"当前实现"列），
        /// 盘上文件 = <c>Assets/Resources/Sound/SFX/sfx/step{,2,3,4}.wav</c>。交替播放避免"复读机"。</summary>
        public static readonly string[] Step =
        {
            S("step"), S("step2"), S("step3"), S("step4"),
        };

        /// <summary>落地音。出处：**本项目新增** —— CS 1.6 原版**没有**独立的落地音
        /// （GoldSrc 落地复用脚步采样，见 <c>client/资源欠缺清单.md:33</c> 第 7 项，状态"半占位"）；
        /// 本工程用 <c>player/pl_step4.wav</c> 作 <c>sfx/land</c>（在盘）。</summary>
        public const string Land = "sfx/land";

        /// <summary>跳跃音。出处：原版 CS 1.6 <c>sound/player/pl_jump1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:32</c> 第 6 项）。</summary>
        public const string Jump = "sfx/jump";

        // ------------------------------------------------------------------
        //
        //  原版是**时间制**：`PM_ReduceTimers` 每帧 `flTimeStepSound -= cmd.msec`，
        //  `PM_UpdateStepSound` 在冷却归零后重新装填冷却并放音 ⇒ 跑得越快步幅越大。
        //  同一速度下两者步频差约 1.8 倍 ⇒ 本工程按原版取时间制（下面三条即口径）。
        //  实测（S1，v=SpeedRifle 4.4 m/s）：3.40 Hz；
        //  步幅制口径下同速是 6.00 Hz（比 1.76 x —— 步幅被钉在 0.62 m、又被 0.16 s 地面限流）。
        //  出处：`原版资源/hlsdk/pm_shared/pm_shared.c:500-639`（PM_UpdateStepSound）、
        //  `:2400-2410`（PM_ReduceTimers，递减在 `:2404`）、调用点 `:2493`（PM_PlayerMove 内、PM_Duck 之后）。
        //  单位换算：1 unit = 0.0254 m，见 `client/Assets/Scripts/Core/CsConst.cs:11-12`。
        // ------------------------------------------------------------------

        /// <summary>低于该速度就不算"在跑"（米/秒）= 原版 `PM_UpdateStepSound` 的
        /// `if (speed &lt; 150)`（GoldSrc units/s）x 0.0254 = <b>3.81 m/s</b>。
        /// 出处：`原版资源/hlsdk/pm_shared/pm_shared.c:519`。
        /// <para>原版量的是 `Length(pmove-&gt;velocity)`（三维模长），本工程同此口径（见 AudioModule）。
        /// CS 里 Shift 慢走约 0.42 x 5.4 = 2.27 m/s、蹲行约 0.34 x 5.4 = 1.84 m/s 都低于 3.81
        /// ⇒ **"慢走/蹲行无声"在原版就是这一行给的**（不是另有一条"Shift 静音"规则）。</para></summary>
        public const float StepMinSpeed = 3.81f;

        /// <summary>一步之后的冷却（毫秒）= 原版 `flTimeStepSound = 300`。
        /// 出处：`原版资源/hlsdk/pm_shared/pm_shared.c:567`~`:626`（材质 switch 的 10 个分支，`:626` 是 default）；
        /// 另一处同值在 `:556`（脚部涉水 SLOSH 分支）。这一句在原文里是**多处同值**，不是单一出处。
        /// <para><b>这一条就是节拍本身</b>：冷却制 ⇒ 同一冷却下跑得越快步幅越大，
        /// 而不是"每 0.62 m 一步"。</para></summary>
        public const float StepCooldownConcreteMs = 300f;

        /// <summary>速度不达标时的冷却（毫秒）= 原版 `flTimeStepSound = 400`
        /// （`speed &lt; 150` 分支：只推冷却、不发声）。出处：`原版资源/hlsdk/pm_shared/pm_shared.c:521`。</summary>
        public const float StepCooldownSlowMs = 400f;

        /// <summary>蹲行时的冷却附加（毫秒）= 原版 `flTimeStepSound += 100`。原版那一行的条件是
        /// `if (flags &amp; FL_DUCKING || fLadder)`，本工程只有蹲、没有梯子。
        /// 出处：`原版资源/hlsdk/pm_shared/pm_shared.c:630-632`。</summary>
        public const float StepDuckingExtraMs = 100f;

        /// <summary>别人的脚步声的 3D 播放距离（米）——超过就不播（省音源、也符合听觉直觉）。
        /// 出处：**本项目新增**（音频层实现参数，原版无对应量）。</summary>
        public const float StepHearDistance = 26f;

        /// <summary>落地判定：落地前的下落速率超过它才出声（米/秒）= 原版 `PM_CrashLand` 的
        /// `else if (flFallVelocity &gt; PLAYER_MAX_SAFE_FALL_SPEED / 2)` = 580 / 2 = <b>290 units/s</b>
        /// x 0.0254 = <b>7.366 m/s</b>。
        /// 出处：阈值行 `原版资源/hlsdk/pm_shared/pm_shared.c:2243`、宏 `:125`
        /// （`PLAYER_MAX_SAFE_FALL_SPEED 580`）、`flFallVelocity = -velocity[2]` 见 `:2477`
        /// （= 下落速率的**大小**，与本工程取 `-Velocity.y` 同口径）。
        /// <para>紧邻的下一分支 `else if (flFallVelocity &lt; PLAYER_MIN_BOUNCE_SPEED)`（宏值 350，`:128`）
        /// 在原版里**永远走不到** —— 前一条 `&gt; 290` 已经把 290~350 全拦下 ⇒ 真正的阈是 <b>290</b>，不是 350。
        /// 音量两档（`&gt; 580` 记 1.0 / `&gt; 290` 记 0.85，`:2239-2246`）本工程**未复现**：
        /// `SfxService.Play` 没有音量形参 —— 本条只对齐"有没有这一档"。</para>
        /// <para>落地音这个**采样**本身仍是"本项目新增"：A（CS 1.6）没有独立落地音，GoldSrc 落地复用
        /// 脚步采样（见 `client/资源欠缺清单.md:33` 第 7 项）⇒ 本工程用 `player/pl_step4.wav` 代替。</para></summary>
        public const float LandMinFallSpeed = 7.366f;

        // ==================================================================
        //  命中 / 死亡
        // ==================================================================
        /// <summary>自己被击中（无甲）。出处：原版 CS 1.6 <c>sound/player/bhit_flesh-2.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:34</c> 第 8 项）。</summary>
        public const string HitFlesh = "sfx/hit_flesh";

        /// <summary>自己被击中（有甲 / 头盔）。出处：原版 CS 1.6 <c>sound/player/bhit_kevlar-1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:34</c> 第 8 项）。</summary>
        public const string HitKevlar = "sfx/hit_kevlar";

        /// <summary>别人的死亡音。出处：原版 CS 1.6 <c>sound/player/die1..3.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:35</c> 第 9 项；盘上 = <c>sfx/death{,2,3}.wav</c>）。</summary>
        public static readonly string[] Death = { S("death"), S("death2"), S("death3") };

        // ==================================================================
        /// <summary>空仓扣扳机。出处：资源文件 <c>Assets/Resources/Sound/SFX/sfx/dryfire.wav</c>（在盘，
        /// 原版**源文件名的映射未记录**（<c>client/资源欠缺清单.md:37</c> 第 11 项只记了
        /// <c>weapons/c4_beep1.wav</c> 等 8 个）⇒ 一并登记 <c>策划/差异登记.tsv</c>。
        /// 事件挂点：<c>Module/Combat/CombatModule.CanFire</c> 的"弹匣为空"分支。</summary>
        public const string Dryfire = "sfx/dryfire";

        /// <summary>两次空仓击发音之间的最短间隔（秒）。出处：**本项目新增**（按住左键时
        /// <c>CombatModule.CanFire</c> 的"弹匣为空"分支**每帧**都会走到，若不加时间闸就是"每帧一响"；
        /// 原版对应的是"每次扣扳机一响"，但其节拍写在 <c>mp.dll</c> 的武器 <c>PrimaryAttack</c> 里，
        /// 载体不在盘 ⇒ 值本项目自定）。</summary>
        public const float DryfireMinInterval = 0.25f;

        /// <summary>弹着墙（非角色碰撞体）。出处：资源文件
        /// <c>Assets/Resources/Sound/SFX/sfx/hit_wall.wav</c>（在盘）；
        /// 原版**按材质族分的多条弹着音**（<c>weapons/ric_metal-{1,2}.wav</c> / <c>ric_conc-{1,2}.wav</c> /
        /// <c>ric1..5.wav</c>，出处见 <see cref="ImpactClassOfLetter"/>）**采样本体不在盘**
        /// ⇒ 金属 / 混凝土两族现在落这一条 clip；族本身由 <see cref="ClassifyImpact"/> 按原版
        /// <c>sound/materials.txt</c> 的材质字母给出。已登记 <c>策划/差异登记.tsv</c> #56 / #172。
        /// 事件挂点：<c>Module/Combat/CombatModule.HandleLocalShot</c> 的"打墙落点"循环。</summary>
        public const string HitWall = "sfx/hit_wall";

        /// <summary>刀命中。出处：资源文件 <c>Assets/Resources/Sound/SFX/sfx/knife_hit.wav</c>（在盘，
        /// 原版刀命中音）；原版源文件名映射未记录 ⇒ 登记 <c>策划/差异登记.tsv</c>。
        /// 事件挂点：<c>Module/Match/CsDamage.ApplyHit</c>（<c>def.Class == Knife</c> 且出刀者是本地玩家）。
        /// 原版"刀命中"有 hit / hit_wall 两种落点（打人 vs 打墙），盘上只有 <c>knife_hit</c> 一条。</summary>
        public const string KnifeHit = "sfx/knife_hit";

        /// <summary>闪光弹爆炸。出处：原版 CS 1.6 <c>sound/weapons/flashbang-1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项；盘上 = <c>sfx/flash_explode.wav</c>）。
        /// 事件挂点：<c>Module/Match/CsDamage.ApplyFlash</c>（闪成功任何目标时按爆炸点播一次）。</summary>
        public const string FlashExplode = "sfx/flash_explode";

        /// <summary>C4 蜂鸣的另一支采样（5 档表的第 5 档用它）。出处：资源文件
        /// <c>Assets/Resources/Sound/SFX/sfx/bomb_beep_fast.wav</c>（在盘）；
        /// 原版源文件名映射未记录（<c>资源欠缺清单.md:37</c> 只记了 <c>c4_beep1.wav</c>）⇒
        /// 已登记 <c>策划/差异登记.tsv</c>。事件挂点：<c>Module/Audio/AudioModule.TickBombBeep</c>。</summary>
        public const string BombBeepFast = "sfx/bomb_beep_fast";

        // ==================================================================
        //  回合 / 比赛
        // ==================================================================
        /// <summary>回合开始（CS 1.6 的 radio 语音 "Let's go"）。出处：原版 CS 1.6
        /// <c>sound/radio/letsgo.wav</c>（映射见 <c>client/资源欠缺清单.md:36</c> 第 10 项）。</summary>
        public const string RoundStart = "sfx/round_start";

        /// <summary>回合开始的备用语音（第一段播放失败时用）。出处：资源文件
        /// <c>Assets/Resources/Sound/SFX/sfx/round_start2.wav</c>（在盘）；原版源文件名映射未记录
        /// （<c>资源欠缺清单.md:36</c> 只记了 <c>radio/letsgo.wav</c>）⇒ 登记 <c>策划/差异登记.tsv</c>。</summary>
        public const string RoundStartAlt = "sfx/round_start2";

        /// <summary>回合结束：T 方获胜播报。出处：原版 CS 1.6 <c>sound/radio/terwin.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:36</c> 第 10 项）。</summary>
        public const string WinT = "sfx/win_t";

        /// <summary>回合结束：CT 方获胜播报。出处：原版 CS 1.6 <c>sound/radio/ctwin.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:36</c> 第 10 项）。</summary>
        public const string WinCT = "sfx/win_ct";

        // ==================================================================
        //  炸弹
        // ==================================================================
        /// <summary>炸弹蜂鸣（第 1 档）。出处：原版 CS 1.6 <c>sound/weapons/c4_beep1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string BombBeep = "sfx/bomb_beep";

        /// <summary>5 档蜂鸣采样（下标 = 档位 0..4）。出处：原版每档一支采样
        /// <c>sound/weapons/c4_beep1..5.wav</c> —— <c>mp.dll</c> 按档把声音名写进 <c>m_sBeepName</c> 的 5 个串：
        /// <c>0x10115160</c> / <c>0x10115178</c> / <c>0x10115190</c> / <c>0x101151A8</c> / <c>0x101151C0</c>。
        ///
        /// <para><b>盘上只有两支采样</b>：<see cref="BombBeep"/>（= 原版 <c>c4_beep1.wav</c>）与
        /// <see cref="BombBeepFast"/>（原版源文件名未记录，且与前者字节相同）。
        /// <c>c4_beep2..5</c> 四条采样不在盘、工程内也没有生成入口 ⇒ 5 档现在实际只换到这两支：
        /// 档 0..3 用 <see cref="BombBeep"/>、档 4 用 <see cref="BombBeepFast"/>。
        /// 补齐采样后只改本表（已登记 <c>策划/差异登记.tsv</c>）。</para></summary>
        public static readonly string[] BombBeepTiers =
        {
            BombBeep, BombBeep, BombBeep, BombBeep, BombBeepFast,
        };

        /// <summary>每轮安放里逐响打印蜂鸣明细的响数上限（超过只打换档）。
        /// 出处：**本项目新增**（日志降频；前 8 响已覆盖全部 5 个档位与间隔衰减，
        /// 而引信末段每秒可响十余次，逐响打会刷屏）。</summary>
        public const int BombBeepLogCount = 8;

        /// <summary>下包动作音。出处：原版 CS 1.6 <c>sound/weapons/c4_plant.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string BombPlant = "sfx/bomb_plant";

        /// <summary>下包播报。出处：原版 CS 1.6 <c>sound/radio/bombpl.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string BombPlantRadio = "sfx/bomb_plant_radio";

        /// <summary>拆包动作音。出处：原版 CS 1.6 <c>sound/weapons/c4_disarm.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string BombDefuse = "sfx/bomb_defuse";

        /// <summary>拆包播报。出处：原版 CS 1.6 <c>sound/radio/bombdef.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string BombDefuseRadio = "sfx/bomb_defuse_radio";

        /// <summary>爆炸（C4 爆炸）。出处：原版 CS 1.6 <c>sound/weapons/c4_explode1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string BombExplode = "sfx/bomb_explode";

        /// <summary>手雷爆炸（HE）。出处：原版 CS 1.6 <c>sound/weapons/hegrenade-1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项；盘上 = <c>sfx/grenade_explode.wav</c>）。
        /// 事件挂点：<c>Module/Match/CsDamage.ApplyHeExplosion</c>（HE 爆炸时按爆心播一次）。</summary>
        public const string GrenadeExplode = "sfx/grenade_explode";

        // ==================================================================
        /// <summary>原版对「<c>materials.txt</c> 里没有该贴图」给的**默认材质字母** = <c>'C'</c>（concrete）。
        /// 出处：<c>原版资源/hlsdk/pm_shared/pm_shared.c:288</c>（<c>PM_FindTextureType</c> 二分查不到时
        /// <c>return CHAR_TEX_CONCRETE;</c>）与 <c>:100</c>（<c>#define CHAR_TEX_CONCRETE 'C'</c>）。
        /// ⛔ 表外**不给**任何关键词猜测：原版这里只有这一个默认值。</summary>
        public const char ImpactDefaultLetter = 'C';

        /// <summary>金属弹着族（原版材质字母 <c>M</c> / <c>V</c>）。出处见 <see cref="ImpactClassOfLetter"/>。</summary>
        public const string ImpactClassMetal = "metal";

        /// <summary>混凝土弹着族（原版材质字母表里非金属字母的族）。出处见 <see cref="ImpactClassOfLetter"/>。</summary>
        public const string ImpactClassConcrete = "concrete";

        /// <summary>
        /// 原版**材质字母 ⇒ 弹着音族**：<c>'M'</c>（metal）/ <c>'V'</c>（ventilation）⇒ 金属族，
        /// 其余字母 ⇒ 混凝土族。
        ///
        /// <para><b>出处</b>：<c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>（1,093,128 B，
        /// ImageBase <c>0x1900000</c>）里"打墙的冲击音 + 弹痕"那个函数，RVA <c>0x1E80-0x2091</c>：
        /// <list type="bullet">
        /// <item><c>0x1EAE</c> 取字母（<c>mov cl, byte ptr [esp+0x18]</c>，第三个实参）；</item>
        /// <item><c>0x1EB2</c> <c>cmp cl,'M'</c> / <c>0x1EBB</c> <c>cmp cl,'V'</c> ⇒ 都跳 <c>0x1FBE</c>；</item>
        /// <item><c>0x1FBE-0x1FEC</c> 那两个分支 push 的串 = <c>weapons/ric_metal-1.wav</c>
        /// （.data RVA <c>0xE1590</c>）与 <c>weapons/ric_metal-2.wav</c>（<c>0xE1578</c>）；</item>
        /// <item>其余字母走 <c>0x1EC4-0x1ED4</c> 的 6 路跳表（表在 RVA <c>0x2094</c>）⇒
        /// <c>weapons/ric1..5.wav</c>（<c>0xE1628/0xE1614/0xE1600/0xE15EC/0xE15D8</c>）与
        /// <c>weapons/ric_conc-1.wav</c>（<c>0xE15C0</c>）。</item>
        /// </list>
        /// 即：**只有 M / V 走金属族，其余字母一律混凝土族**（族内随机取一条采样）。</para>
        ///
        /// <para><b>字母从哪来</b>：<see cref="MaterialLetters.TryGetLetter"/> 查原版
        /// <c>sound/materials.txt</c>（解析口径 = <c>PM_InitTextureTypes</c>）；
        /// 查不到时用 <see cref="ImpactDefaultLetter"/>（<c>'C'</c>，<c>pm_shared.c:288</c>）。</para>
        ///
        /// <para><b>族 ⇒ 采样那一步在本工程仍缺</b>：原版那两条金属 / 混凝土采样本体不在盘，
        /// 盘上只有一条 <c>sfx/hit_wall.wav</c> ⇒ 各族现在落同一 clip，但"族"本身有出处。
        /// 补齐采样后只改本函数与调用点，不散落到别处。</para>
        /// </summary>
        public static string ImpactClassOfLetter(char letter)
            => letter == 'M' || letter == 'V' ? ImpactClassMetal : ImpactClassConcrete;

        /// <summary>
        /// 把命中物的材质名（<c>Material.name</c> = BSP 贴图组名，如 <c>csSandWallDJ1</c>；
        /// 阻挡盒没有材质 ⇒ 材质名为 <c>null</c>）归到一个音效族。
        ///
        /// <para><b>口径与</b>原版 <c>PM_FindTextureType</c> <b>逐条一致（两条分支）</b>：
        /// <list type="number">
        /// <item>材质名在 <c>materials.txt</c> 里 ⇒ 用它那一行的字母
        /// （<see cref="TryLookupImpactLetter"/>）再给族（<see cref="ImpactClassOfLetter"/>）；</item>
        /// <item>查不到（含 <c>null</c> / 空串）⇒ 用默认字母 <see cref="ImpactDefaultLetter"/>
        /// = <c>'C'</c> ⇒ <b>混凝土族</b>（<c>pm_shared.c:288</c>）。</item>
        /// </list>
        /// ⛔ **表外不做任何按名字的猜测**：原版这一步只有"默认 'C'"这一个出口
        /// （关键词分类是"按贴图名猜"，与 1:1 复刻冲突，已删）。</para>
        ///
        /// <para><b>本图的后果（如实记）</b>：de_dust2 的 38 个贴图名在原版表里**一个都没有**
        /// （交叉输出见 <c>tools/probes/cs16_materials_table.py</c>）⇒ 本图全图弹着音都落**混凝土族** ——
        /// 这正是原版对这张图的行为。</para>
        /// </summary>
        public static string ClassifyImpact(string materialName)
        {
            if (TryLookupImpactLetter(materialName, out var letter)) return ImpactClassOfLetter(letter);
            return ImpactClassOfLetter(ImpactDefaultLetter);
        }

        /// <summary>材质名 ⇒ 原版材质字母（表里没有该贴图 / <c>null</c> / 空串 ⇒ <c>false</c>）。
        /// 表见 <see cref="MaterialLetters"/>。</summary>
        public static bool TryLookupImpactLetter(string materialName, out char letter)
            => MaterialLetters.TryGetLetter(materialName, out letter);

        // >>> 生成块：MaterialLetters（生成器 = tools/probes/cs16_materials_table.py --csharp）
        /// <summary>
        /// 原版 CS 1.6 `sound/materials.txt` 的「贴图名 -> 材质字母」表。
        ///
        /// <para><b>表本身是数据、不是手写常量</b>：由 <c>tools/probes/cs16_materials_table.py --csharp</c>
        /// 逐行打印，⚠️ 手改这张表等于绕过生成器 —— 要改就改生成器再重跑。
        /// 人读版本 = <c>client/Assets/ThirdParty/Dust2/materials_letters.tsv</c>。</para>
        ///
        /// <para><b>载体</b>：<c>原版资源/cs16src/cstrike/sound/materials.txt</c>（11064 B / 787 行 / SHA256 <c>0D35D708CB3737FC99CAF50E41AE32680E197218D2A51488489ECB7561B3AB6C</c>）。</para>
        ///
        /// <para><b>解析口径</b> = 原版 <c>PM_InitTextureTypes</c> 的逐语句搬运，出处
        /// <c>原版资源/hlsdk/pm_shared/pm_shared.c</c>：行文法 <c>:217-249</c>（<c>'/'</c> 或非字母开头跳过；字母 <c>toupper</c>；名字扫到空白；行尾无空白则丢弃；
        /// 名字截断到 <c>CBTEXTURENAMEMAX-1</c> = 16 字符）；查表 <c>:258-289</c>（<c>strnicmp(name, stored, 16)</c> = 大小写不敏感、按前 16 字符整名相等；
        /// 查不到返回 <c>CHAR_TEX_CONCRETE</c> '<c>C</c>'，<c>:288</c>）。</para>
        ///
        /// <para><b>同名不同字母</b>：原版 <c>PM_SortTextures</c>（<c>:178-190</c>）只在严格大于时交换，同名条目保留文件序；
        /// 但 <c>PM_FindTextureType</c> 是二分查找，同键命中哪一条**原版自己不确定**。
        /// 本表保留全部条目，<see cref="TryGetLetter"/> 取**文件序第一条**并只 Warn 一次（见调用点）。</para>
        /// </summary>
        public static class MaterialLetters
        {
            /// <summary>一行的两个字段。名字是原版文件里的原样（<= 16 字符），比较时才截断运行时名。</summary>
            private struct Row
            {
                public readonly string Texture;
                public readonly char Letter;

                public Row(string texture, char letter)
                {
                    Texture = texture;
                    Letter = letter;
                }
            }

            /// <summary>表（文件序；701 条，行号见 materials_letters.tsv）。</summary>
            private static readonly Row[] Table =
            {
            new Row("snow", 'N'),
            new Row("A_Snow", 'N'),
            new Row("znow1", 'N'),
            new Row("TSBgVent", 'G'),
            new Row("TSCaution", 'M'),
            new Row("TSCprFlr", 'M'),
            new Row("TSCrteSd", 'M'),
            new Row("TSCrteTp", 'M'),
            new Row("TSFlrGrt", 'G'),
            new Row("TSHull2", 'M'),
            new Row("TSHull4", 'M'),
            new Row("TSHull7", 'M'),
            new Row("TSHull8", 'M'),
            new Row("TSHull9", 'M'),
            new Row("TSIBeam", 'M'),
            new Row("TSNoSkd", 'G'),
            new Row("TSOily", 'M'),
            new Row("TSPlainRed", 'M'),
            new Row("TSRfGrvl", 'D'),
            new Row("TSShutters", 'G'),
            new Row("TSVent", 'G'),
            new Row("747wall_x4", 'M'),
            new Row("-1a747plane_x4", 'M'),
            new Row("-0a747plane_x4", 'M'),
            new Row("747inv_block", 'M'),
            new Row("planegen1", 'M'),
            new Row("pl_out_shell4a", 'M'),
            new Row("pl_out_shell3", 'M'),
            new Row("pl_out_shell_dr", 'M'),
            new Row("overhead_x4", 'M'),
            new Row("curtain3", 'W'),
            new Row("blueback_x4", 'W'),
            new Row("chair1_x4", 'W'),
            new Row("planeseatsideba", 'W'),
            new Row("brickPath", 'D'),
            new Row("64crate0", 'W'),
            new Row("64crate1", 'W'),
            new Row("64crateTop", 'W'),
            new Row("bigCrate0", 'W'),
            new Row("bigCrate1", 'W'),
            new Row("bigCrate2", 'W'),
            new Row("bigCrateTop", 'W'),
            new Row("chestDrawers", 'W'),
            new Row("crateBox", 'W'),
            new Row("crateBoxLabel", 'W'),
            new Row("oldWood", 'W'),
            new Row("panelWood", 'W'),
            new Row("wBarrelSide", 'W'),
            new Row("wBarrelTop", 'W'),
            new Row("wdnDrawer", 'W'),
            new Row("wdbDrawers", 'W'),
            new Row("TrainTop01", 'M'),
            new Row("TrainTop02", 'M'),
            new Row("DumpsterFront", 'M'),
            new Row("DumpsterSide", 'M'),
            new Row("Tracks01", 'D'),
            new Row("Tracks02", 'D'),
            new Row("Gravel01", 'D'),
            new Row("Gravel02", 'D'),
            new Row("GravelGrass01", 'D'),
            new Row("GravelGrass02", 'D'),
            new Row("GravelGrass03", 'D'),
            new Row("GravelGrass04", 'D'),
            new Row("Grass01", 'D'),
            new Row("Wood01", 'W'),
            new Row("{cstrike_OE4Ven", 'V'),
            new Row("-0cstrike_FJ2Dk", 'W'),
            new Row("-0cstrike_ME1Pl", 'M'),
            new Row("-0cstrike_ME2Ri", 'M'),
            new Row("-0cstrike_MH5Tl", 'T'),
            new Row("-0cstrike_MJ2Sg", 'W'),
            new Row("-0cstrike_WE1Mt", 'M'),
            new Row("-0cstrike_WE4Al", 'M'),
            new Row("-1cstrike_FJ2Dk", 'W'),
            new Row("-1cstrike_ME1Pl", 'M'),
            new Row("-1cstrike_ME2Ri", 'M'),
            new Row("-1cstrike_MH5Tl", 'T'),
            new Row("-1cstrike_MJ2Sg", 'W'),
            new Row("-1cstrike_WE1Mt", 'M'),
            new Row("-1cstrike_WE4Al", 'M'),
            new Row("-2cstrike_ME1Pl", 'M'),
            new Row("-2cstrike_WE1Mt", 'M'),
            new Row("-3cstrike_ME1Pl", 'M'),
            new Row("-4cstrike_ME1Pl", 'M'),
            new Row("-5cstrike_ME1Pl", 'M'),
            new Row("-6cstrike_ME1Pl", 'M'),
            new Row("cstrike_CE2XCng", 'M'),
            new Row("cstrike_CH3Tile", 'T'),
            new Row("cstrike_CJ2Beam", 'W'),
            new Row("cstrike_DE6Horz", 'M'),
            new Row("cstrike_DE6Vert", 'M'),
            new Row("cstrike_DJ2Old", 'W'),
            new Row("cstrike_FE2Gtng", 'G'),
            new Row("cstrike_FE2Sqrs", 'M'),
            new Row("cstrike_FE4Crgd", 'M'),
            new Row("cstrike_FH4WtTl", 'T'),
            new Row("cstrike_FJ2Dock", 'W'),
            new Row("cstrike_FT2Dirt", 'D'),
            new Row("cstrike_FT2Mud", 'D'),
            new Row("cstrike_FT4Apht", 'D'),
            new Row("cstrike_ME1Clng", 'M'),
            new Row("cstrike_ME1Grat", 'G'),
            new Row("cstrike_ME2DRst", 'M'),
            new Row("cstrike_ME2RstW", 'M'),
            new Row("cstrike_ME4DCln", 'M'),
            new Row("cstrike_ME4Greb", 'M'),
            new Row("cstrike_ME4Metl", 'M'),
            new Row("cstrike_ME4Radr", 'G'),
            new Row("cstrike_ME7Caut", 'M'),
            new Row("cstrike_OE1BxSd", 'M'),
            new Row("cstrike_OE1BxTp", 'M'),
            new Row("cstrike_OE1LgBm", 'M'),
            new Row("cstrike_OE1SmBm", 'M'),
            new Row("cstrike_OE2StrR", 'M'),
            new Row("cstrike_OE2Vent", 'V'),
            new Row("cstrike_OE4FnVt", 'M'),
            new Row("cstrike_OE4Pnl", 'M'),
            new Row("cstrike_OE4PnlH", 'M'),
            new Row("cstrike_OE4Vent", 'V'),
            new Row("cstrike_OE6TnkF", 'G'),
            new Row("cstrike_OE6TnkS", 'G'),
            new Row("cstrike_WE2Ribd", 'M'),
            new Row("{cxchana", 'G'),
            new Row("{cxchanb", 'G'),
            new Row("{cxcnstrctpoles", 'M'),
            new Row("{cxcrtna", 'D'),
            new Row("{cxcrtnd", 'D'),
            new Row("{cxcrtnf", 'D'),
            new Row("{cxgate", 'M'),
            new Row("{fence2", 'M'),
            new Row("cxbarrela", 'W'),
            new Row("cxbooksa", 'D'),
            new Row("cxbooksb", 'D'),
            new Row("cxbooksc", 'D'),
            new Row("cxbooksd", 'D'),
            new Row("cxbookse", 'D'),
            new Row("cxbooksf", 'D'),
            new Row("cxbrusha", 'N'),
            new Row("cxbrushb", 'N'),
            new Row("cxcnstrcttarp", 'D'),
            new Row("cxcnstrcttarpb", 'D'),
            new Row("cxcnstrctwooda", 'W'),
            new Row("cxcnstrctwoodb", 'W'),
            new Row("cxcrptb", 'D'),
            new Row("cxcrptc", 'D'),
            new Row("cxcrptd", 'D'),
            new Row("cxdecwdc", 'W'),
            new Row("cxdecwdd", 'W'),
            new Row("cxdrc", 'W'),
            new Row("cxdrd", 'W'),
            new Row("cxgrounda", 'D'),
            new Row("cxgroundc", 'D'),
            new Row("cxgroundd", 'D'),
            new Row("cxgroundh", 'D'),
            new Row("cxgroundk", 'D'),
            new Row("cxmarblec", 'T'),
            new Row("cxmarblee", 'T'),
            new Row("cxmrbla", 'T'),
            new Row("cxroofa", 'W'),
            new Row("cxruga", 'D'),
            new Row("cxsolida", 'G'),
            new Row("cxsolidb", 'G'),
            new Row("cxsolidc", 'G'),
            new Row("cxstairsd", 'W'),
            new Row("cxstairse", 'W'),
            new Row("cxstairsf", 'W'),
            new Row("cxtapa", 'D'),
            new Row("cxtapb", 'D'),
            new Row("cxtapc", 'D'),
            new Row("cxtapd", 'D'),
            new Row("cxtape", 'D'),
            new Row("cxwllbooksa", 'D'),
            new Row("cxwllh", 'W'),
            new Row("cxwooda", 'W'),
            new Row("cxwoodb", 'W'),
            new Row("cxwoodd", 'W'),
            new Row("cxwoodf", 'W'),
            new Row("cxwoodh", 'W'),
            new Row("cxwoodi", 'W'),
            new Row("cxwoodj", 'W'),
            new Row("cxwoodk", 'W'),
            new Row("cxwoodl", 'W'),
            new Row("cxwoodm", 'W'),
            new Row("{pi_benchside", 'M'),
            new Row("{pi_chairback", 'W'),
            new Row("{pi_orrery01", 'M'),
            new Row("{pi_orrery02", 'M'),
            new Row("{pi_orrery03", 'M'),
            new Row("{pi_railing", 'M'),
            new Row("{pi_scaffold", 'M'),
            new Row("{pi_window", 'M'),
            new Row("pi_art1", 'W'),
            new Row("pi_art2", 'W'),
            new Row("pi_art3", 'W'),
            new Row("pi_art4", 'W'),
            new Row("pi_barrel", 'W'),
            new Row("pi_barrelt", 'W'),
            new Row("pi_bcase", 'W'),
            new Row("pi_bigdoor", 'W'),
            new Row("pi_brusha", 'D'),
            new Row("pi_brushb", 'D'),
            new Row("pi_buckets", 'M'),
            new Row("pi_buckett", 'S'),
            new Row("pi_chairleg", 'W'),
            new Row("pi_chairtop", 'W'),
            new Row("pi_floorst1", 'T'),
            new Row("pi_floorst1g", 'T'),
            new Row("pi_floorst4", 'T'),
            new Row("pi_floorst4g", 'T'),
            new Row("pi_floorst5", 'T'),
            new Row("pi_floorwd1", 'W'),
            new Row("pi_floorwd2", 'W'),
            new Row("pi_floorwd3", 'W'),
            new Row("pi_grnmetal", 'M'),
            new Row("pi_grnmetalp", 'M'),
            new Row("pi_grnmetalt", 'M'),
            new Row("pi_ground", 'X'),
            new Row("pi_lantern", 'M'),
            new Row("pi_motor", 'M'),
            new Row("pi_orrery04", 'M'),
            new Row("pi_orrery05", 'M'),
            new Row("pi_orrery06", 'M'),
            new Row("pi_orrery07", 'M'),
            new Row("pi_potb", 'D'),
            new Row("pi_roof", 'T'),
            new Row("pi_roofedge", 'T'),
            new Row("pi_rug1", 'D'),
            new Row("pi_rug2", 'D'),
            new Row("pi_rug3", 'D'),
            new Row("pi_rug4", 'D'),
            new Row("pi_rug5", 'D'),
            new Row("pi_rug6", 'D'),
            new Row("pi_rust", 'M'),
            new Row("pi_scaffold2", 'M'),
            new Row("pi_secretf", 'W'),
            new Row("pi_secrets", 'W'),
            new Row("pi_signexc", 'T'),
            new Row("pi_signexd", 'T'),
            new Row("pi_steptop1", 'T'),
            new Row("pi_steptop1d", 'T'),
            new Row("pi_sundial", 'M'),
            new Row("pi_ticketwnd", 'Y'),
            new Row("pi_treetr1", 'W'),
            new Row("pi_treetr2", 'W'),
            new Row("pi_wood1", 'W'),
            new Row("pi_wood2", 'W'),
            new Row("pi_wood3", 'W'),
            new Row("pi_wood4", 'W'),
            new Row("pi_wood5", 'W'),
            new Row("pi_woodceil", 'W'),
            new Row("{ikcanopt", 'W'),
            new Row("{ikcanopy", 'W'),
            new Row("{ikcnp1", 'W'),
            new Row("{ikfencea", 'G'),
            new Row("{ikfenceb", 'G'),
            new Row("{ikfencec", 'G'),
            new Row("{ikjungwt", 'W'),
            new Row("{ikjungwt2", 'W'),
            new Row("{ikjungwt3", 'W'),
            new Row("{ikjungwt4", 'W'),
            new Row("{ikvinea", 'W'),
            new Row("{ikvineb", 'W'),
            new Row("{ikvinec", 'D'),
            new Row("{ikvined", 'W'),
            new Row("-0ikjungw", 'W'),
            new Row("-1ikjungw", 'W'),
            new Row("-2ikjungw", 'W'),
            new Row("ikbarls1", 'M'),
            new Row("ikbarls2", 'M'),
            new Row("ikbarls3", 'M'),
            new Row("ikbarls4", 'M'),
            new Row("ikbarlt1", 'M'),
            new Row("ikbarlt2", 'M'),
            new Row("ikbarlt3", 'M'),
            new Row("ikbarlt4", 'M'),
            new Row("ikcratea", 'W'),
            new Row("ikcrateb", 'W'),
            new Row("ikcratec", 'W'),
            new Row("ikcrated", 'W'),
            new Row("ikcratee", 'W'),
            new Row("ikcratef", 'W'),
            new Row("ikcrateg", 'W'),
            new Row("ikcrateh", 'W'),
            new Row("ikftanks", 'M'),
            new Row("ikftankt", 'M'),
            new Row("ikgrndj", 'X'),
            new Row("ikgrndjt", 'D'),
            new Row("ikgrndrt", 'D'),
            new Row("ikgrndrw", 'D'),
            new Row("ikgrnds", 'D'),
            new Row("ikgrndt", 'D'),
            new Row("ikgrndt2", 'D'),
            new Row("ikgrndt3", 'D'),
            new Row("ikgrndte", 'D'),
            new Row("ikgrndte2", 'D'),
            new Row("ikgrndte3", 'D'),
            new Row("ikjungw", 'W'),
            new Row("ikjungwg", 'D'),
            new Row("ikmetla", 'M'),
            new Row("ikmetlb", 'M'),
            new Row("ikmetlc", 'M'),
            new Row("ikvines", 'W'),
            new Row("DUCT_FLR01", 'V'),
            new Row("DUCT_FLR01A", 'V'),
            new Row("DUCT_FLR02A", 'V'),
            new Row("DUCT_VNT", 'V'),
            new Row("DUCT_VNT2", 'V'),
            new Row("DUCT_WALL01", 'V'),
            new Row("DUCT_WALL02", 'V'),
            new Row("DUCT_WALL03", 'V'),
            new Row("DUCT_WALL04", 'V'),
            new Row("SILO2_COR", 'V'),
            new Row("OUT_GRVL1", 'D'),
            new Row("OUT_GRVL2", 'D'),
            new Row("OUT_GRVL2B", 'D'),
            new Row("OUT_GRVL3", 'D'),
            new Row("OUT_MUD1", 'D'),
            new Row("OUT_SND1", 'D'),
            new Row("OUT_SND2", 'D'),
            new Row("OUT_SND2B", 'D'),
            new Row("OUT_SND2C", 'D'),
            new Row("OUT_WLK", 'D'),
            new Row("OUT_GRSS1", 'D'),
            new Row("OUT_DIRT1", 'D'),
            new Row("OUT_DIRT2", 'D'),
            new Row("GENERIC48", 'D'),
            new Row("GENERIC49", 'D'),
            new Row("GENERIC51", 'D'),
            new Row("GENERIC52", 'D'),
            new Row("GENERIC93", 'D'),
            new Row("OUT_PAVE2", 'D'),
            new Row("OUT_SNBAG", 'D'),
            new Row("OUT_SNBAG2", 'D'),
            new Row("OUT_SNBAGB", 'D'),
            new Row("OUT_SNBAGC", 'D'),
            new Row("OUT_TNT2", 'D'),
            new Row("OUT_TNT1", 'D'),
            new Row("OUT_TNT1B", 'D'),
            new Row("OUT_TNT1C", 'D'),
            new Row("OUT_TNT3", 'D'),
            new Row("OUT_TNT3B", 'D'),
            new Row("OUT_NET1", 'D'),
            new Row("OUT_NET1B", 'D'),
            new Row("FROSTSEWFLR", 'D'),
            new Row("GENERIC109", 'G'),
            new Row("GENERIC015C", 'G'),
            new Row("GENERIC015D", 'G'),
            new Row("GENERIC015E", 'G'),
            new Row("GENERIC015F", 'G'),
            new Row("GENERIC015G", 'G'),
            new Row("GENERIC015H", 'G'),
            new Row("GENERIC015I", 'G'),
            new Row("GENERIC015", 'G'),
            new Row("GENERIC015A", 'G'),
            new Row("GENERIC015B", 'G'),
            new Row("BABTECH_FLR02", 'G'),
            new Row("CRETE2_FLR03", 'G'),
            new Row("CRETE2_FLR03A", 'G'),
            new Row("CRETE2_FLR03B", 'G'),
            new Row("CRETE2_FLR03C", 'G'),
            new Row("ELEV1_FLR", 'G'),
            new Row("ELEV2_FLR", 'G'),
            new Row("ELEV_FLR", 'G'),
            new Row("GRATE1", 'G'),
            new Row("GRATE2", 'G'),
            new Row("GRATE2A", 'G'),
            new Row("GRATE3A", 'G'),
            new Row("GRID1", 'G'),
            new Row("GRID2", 'G'),
            new Row("TNNL_FLR12", 'G'),
            new Row("TNNL_FLR12A", 'G'),
            new Row("TNNL_FLR1A", 'G'),
            new Row("LAB1_STAIR2A", 'G'),
            new Row("FROSTFLOOR", 'G'),
            new Row("GRATE4A", 'G'),
            new Row("GRATE3B", 'G'),
            new Row("GRATESTEP1", 'G'),
            new Row("GRATESTEP2", 'G'),
            new Row("GRATESTEP3", 'G'),
            new Row("GRID1", 'G'),
            new Row("GRID2", 'G'),
            new Row("GENERIC015V", 'G'),
            new Row("C1A1_FLR1", 'M'),
            new Row("BABFL", 'M'),
            new Row("C2A3TURBINE1", 'M'),
            new Row("C2A3TURBINE2", 'M'),
            new Row("C2A3TURBINE3", 'M'),
            new Row("C2A3TURBINE4", 'M'),
            new Row("SILO2_P2", 'M'),
            new Row("SILO2_P2B", 'M'),
            new Row("SILO2_P3", 'M'),
            new Row("SILO2_P4", 'M'),
            new Row("SILO2_PAN2", 'M'),
            new Row("SILO2_W1", 'M'),
            new Row("SILO2_W1A", 'M'),
            new Row("SILO2_WALL1", 'M'),
            new Row("SILO2_W2", 'M'),
            new Row("BABTECH_C5", 'M'),
            new Row("LAB3_FLR2A", 'M'),
            new Row("LAB3_FLR2B", 'M'),
            new Row("FIFTIES_CMP3", 'M'),
            new Row("LAB1_STAIR2B", 'M'),
            new Row("OUT_QUNST11", 'M'),
            new Row("FREEZER_FLR1", 'M'),
            new Row("GENERIC015N", 'M'),
            new Row("GENERIC015P", 'M'),
            new Row("GENERIC015R", 'M'),
            new Row("GENERIC0150", 'M'),
            new Row("GENERIC015S", 'M'),
            new Row("GENERIC015T", 'M'),
            new Row("GENERIC015U", 'M'),
            new Row("GENERIC015V", 'M'),
            new Row("GENERIC015V2", 'M'),
            new Row("C1A4_DOME3", 'M'),
            new Row("GENERIC_114", 'S'),
            new Row("WATERBLUE", 'S'),
            new Row("WATERGREEN", 'S'),
            new Row("TOXICGRN", 'S'),
            new Row("FLUID1A", 'S'),
            new Row("FLUID1B", 'S'),
            new Row("FLUID2", 'S'),
            new Row("FLUID3", 'S'),
            new Row("FLUID4", 'S'),
            new Row("WATERSILO", 'S'),
            new Row("WATERSILO2", 'S'),
            new Row("WATERF", 'S'),
            new Row("FLUID1A", 'S'),
            new Row("FIFT_BLOODFLR", 'T'),
            new Row("FIFT_BLOODFLRA", 'T'),
            new Row("FIFTIES_F01", 'T'),
            new Row("FIFTIES_F02", 'T'),
            new Row("FIFTIES_F03", 'T'),
            new Row("FIFTIES_F03B", 'T'),
            new Row("FIFTIES_FLR01", 'T'),
            new Row("FIFTIES_FLR02", 'T'),
            new Row("FIFTIES_FLR02B", 'T'),
            new Row("FIFTIES_FLR02C", 'T'),
            new Row("FIFTIES_FLR03", 'T'),
            new Row("FIFTIES_FLR03B", 'T'),
            new Row("FIFTIES_FLR5", 'T'),
            new Row("LAB1_BLUXFLR1", 'T'),
            new Row("LAB1_BLUXFLR1B", 'T'),
            new Row("LAB1_BLUX1", 'T'),
            new Row("LAB1_BLUX1B", 'T'),
            new Row("LAB1_C4003", 'T'),
            new Row("LAB1_C4A001", 'T'),
            new Row("LAB1_C4B002", 'T'),
            new Row("LAB1_C4D2", 'T'),
            new Row("LAB1_CAB2", 'T'),
            new Row("LAB1_FLOOR2A", 'T'),
            new Row("LAB1_FLOOR2B", 'T'),
            new Row("LAB1_FLOOR3", 'T'),
            new Row("LAB1_FLOOR4", 'T'),
            new Row("LAB1_FLOOR5", 'T'),
            new Row("LAB1_FLOOR6", 'T'),
            new Row("LAB1_FLOOR10", 'T'),
            new Row("LAB1_FLR3", 'T'),
            new Row("LAB1_FLR4", 'T'),
            new Row("LAB1_FLR4B", 'T'),
            new Row("LAB1_FLR4C", 'T'),
            new Row("LAB1_FLR4D", 'T'),
            new Row("LAB1_FLR5B", 'T'),
            new Row("LAB1_FLR5C", 'T'),
            new Row("LAB1_FLR5D", 'T'),
            new Row("LAB1_FLR5D", 'T'),
            new Row("LAB1_FLR6B", 'T'),
            new Row("LAB1_FLR6C", 'T'),
            new Row("LAB1_FLR6D", 'T'),
            new Row("LAB1_W8FLR1", 'T'),
            new Row("LAB1_W8FLR1B", 'T'),
            new Row("LAB1_W8FLR1C", 'T'),
            new Row("LAB1_W8FLR1D", 'T'),
            new Row("C2A4_FLR6", 'T'),
            new Row("C1A2_FLR1", 'T'),
            new Row("C1A2_FLR2", 'T'),
            new Row("C1A0_LABFLRB", 'T'),
            new Row("C1A0_LABFLRC", 'T'),
            new Row("C1A0_LABFLRD", 'T'),
            new Row("C1A0_LABFLRE", 'T'),
            new Row("C1A0_LABFLR", 'T'),
            new Row("CRATE01", 'W'),
            new Row("CRATE02", 'W'),
            new Row("CRATE02B", 'W'),
            new Row("CRATE03", 'W'),
            new Row("CRATE04", 'W'),
            new Row("CRATE05", 'W'),
            new Row("CRATE06", 'W'),
            new Row("CRATE07", 'W'),
            new Row("CRATE08", 'W'),
            new Row("CRATE08B", 'W'),
            new Row("CRATE09", 'W'),
            new Row("CRATE09B", 'W'),
            new Row("CRATE09C", 'W'),
            new Row("CRATE19", 'W'),
            new Row("CRATE20", 'W'),
            new Row("CRATE21", 'W'),
            new Row("CRATE22", 'W'),
            new Row("CRATE23", 'W'),
            new Row("CRATE25", 'W'),
            new Row("FIFTIES_DR6", 'W'),
            new Row("FIFTIES_DR6A", 'W'),
            new Row("FIFTIES_CCH1", 'W'),
            new Row("FIFTIES_CCH2", 'W'),
            new Row("FIFTIES_CCH3", 'W'),
            new Row("FIFTIES_CCH4", 'W'),
            new Row("FIFTIES_DR1K", 'W'),
            new Row("FIFTIES_DR2", 'W'),
            new Row("FIFTIES_DR7", 'W'),
            new Row("FIFTIES_DR8", 'W'),
            new Row("FIFTIES_DR9", 'W'),
            new Row("FIFTIES_DSK1", 'W'),
            new Row("OUT_SLAT01", 'W'),
            new Row("OUT_WD", 'W'),
            new Row("OUT_CAC1", 'W'),
            new Row("OUT_CAC2", 'W'),
            new Row("OUT_CAC2", 'W'),
            new Row("BCRATE02", 'W'),
            new Row("BCRATE03", 'W'),
            new Row("BCRATE04", 'W'),
            new Row("BCRATE05", 'W'),
            new Row("BCRATE06", 'W'),
            new Row("BCRATE07", 'W'),
            new Row("BCRATE08", 'W'),
            new Row("BCRATE12", 'W'),
            new Row("BCRATE14", 'W'),
            new Row("BCRATE15", 'W'),
            new Row("BCRATE16", 'W'),
            new Row("BCRATE17", 'W'),
            new Row("BCRATE18", 'W'),
            new Row("BCRATE25", 'W'),
            new Row("BCRATE26", 'W'),
            new Row("CRATE10", 'W'),
            new Row("CRATE11", 'W'),
            new Row("CRATE12", 'W'),
            new Row("CRATE13", 'W'),
            new Row("CRATE24", 'W'),
            new Row("CRATE27", 'W'),
            new Row("C3A1_NRC2", 'P'),
            new Row("C1A1_GAD1", 'P'),
            new Row("C1A1_GAD2", 'P'),
            new Row("C1A1_GAD3", 'P'),
            new Row("C1A1_GAD4", 'P'),
            new Row("C1A1_GAD4A", 'P'),
            new Row("C1A1_GGT8", 'P'),
            new Row("C1A4_PAN1A", 'P'),
            new Row("C1A4_PAN1B", 'P'),
            new Row("C1A4_SWTCH1", 'P'),
            new Row("C2A2_SATORB", 'P'),
            new Row("C2A4_CMP1", 'P'),
            new Row("C2A4_GAD2", 'P'),
            new Row("C3A2A_W1D", 'P'),
            new Row("C3A2A_W1E", 'P'),
            new Row("C3A2A_W2D", 'P'),
            new Row("C3A2A_W2E", 'P'),
            new Row("DRKMTL_SCRN2", 'P'),
            new Row("DRKMTL_SCRN3", 'P'),
            new Row("DRKMTL_SCRN4", 'P'),
            new Row("DRKMTLT_WALL", 'P'),
            new Row("FIFTIES_GGT8", 'P'),
            new Row("FIFTIES_MON1", 'P'),
            new Row("FIFTIES_MON1B", 'P'),
            new Row("FIFTIES_MON2", 'P'),
            new Row("FIFTIES_MON2B", 'P'),
            new Row("FIFTIES_MON3", 'P'),
            new Row("FIFTIES_MON3B", 'P'),
            new Row("FIFTIES_MON4", 'P'),
            new Row("FIFTIES_PAN2", 'P'),
            new Row("GENERIC105", 'P'),
            new Row("GENERIC106", 'P'),
            new Row("GENERIC107", 'P'),
            new Row("GENERIC114", 'P'),
            new Row("GENERIC87A", 'P'),
            new Row("GENERIC88A", 'P'),
            new Row("GENERIC89A", 'P'),
            new Row("GENERIC113", 'P'),
            new Row("LAB1_CMPM1", 'P'),
            new Row("LAB1_CMPM2", 'P'),
            new Row("LAB1_CMPM3", 'P'),
            new Row("LAB1_CMPM4", 'P'),
            new Row("LAB1_CMPM5", 'P'),
            new Row("LAB1_CMPM6", 'P'),
            new Row("LAB1_CMPM7", 'P'),
            new Row("LAB1_CMPM8", 'P'),
            new Row("LAB1_COMP1", 'P'),
            new Row("LAB1_COMP2", 'P'),
            new Row("LAB1_COMP3", 'P'),
            new Row("LAB1_COMP2A", 'P'),
            new Row("LAB1_COMP10A", 'P'),
            new Row("LAB1_COMP10B", 'P'),
            new Row("LAB1_COMP10C", 'P'),
            new Row("LAB1_COMP10D", 'P'),
            new Row("LAB1_COMP10E", 'P'),
            new Row("LAB1_COMP7", 'P'),
            new Row("LAB1_COMP8", 'P'),
            new Row("LAB1_COMP9A", 'P'),
            new Row("LAB1_COMP9A2", 'P'),
            new Row("LAB1_COMP9B", 'P'),
            new Row("LAB1_COMP9C", 'P'),
            new Row("LAB1_COMP9D", 'P'),
            new Row("LAB1_GAD2", 'P'),
            new Row("LAB1_GAD3", 'P'),
            new Row("LAB1_GAD4", 'P'),
            new Row("LAB1_RADSCRN2", 'P'),
            new Row("LAB4_GAD3", 'P'),
            new Row("LAB4_GAD4", 'P'),
            new Row("LAB4_SWTCH", 'P'),
            new Row("LAB_COMPM4", 'P'),
            new Row("RECHARGEA", 'P'),
            new Row("TNNL_GAD1", 'P'),
            new Row("TNNL_GAD2", 'P'),
            new Row("TNNL_GAD4", 'P'),
            new Row("C2A4C2C", 'P'),
            new Row("C2A4W1B", 'P'),
            new Row("LAB1_CMP", 'P'),
            new Row("LAB1_CMP1", 'P'),
            new Row("LAB1_CMP2", 'P'),
            new Row("LAB1_COMP3", 'P'),
            new Row("LAB1_COMP3A", 'P'),
            new Row("LAB1_COMP3B", 'P'),
            new Row("LAB1_COMP3C", 'P'),
            new Row("LAB1_COMP3D", 'P'),
            new Row("LAB1_COMP4", 'P'),
            new Row("LAB1_COMP5", 'P'),
            new Row("LAB1_COMP7", 'P'),
            new Row("LAB1_COMP8", 'P'),
            new Row("LAB1_SW1", 'P'),
            new Row("LAB_CRT1", 'P'),
            new Row("LAB_CRT10A", 'P'),
            new Row("LAB_CRT10B", 'P'),
            new Row("LAB_CRT10C", 'P'),
            new Row("LAB_CRT10D", 'P'),
            new Row("LAB_CRT2", 'P'),
            new Row("LAB_CRT3", 'P'),
            new Row("LAB_CRT4", 'P'),
            new Row("LAB_CRT5", 'P'),
            new Row("LAB_CRT6", 'P'),
            new Row("LAB_CRT7", 'P'),
            new Row("LAB_CRT8", 'P'),
            new Row("LAB_CRT9C", 'P'),
            new Row("CRETESTCH01A", 'P'),
            new Row("DRKMTLLGT1", 'P'),
            new Row("FIFTS_LGHT01", 'P'),
            new Row("FIFTS_LGHT3", 'P'),
            new Row("FIFTS_LGHT4", 'P'),
            new Row("FIFTIES_LGT2", 'P'),
            new Row("FIFTS_LGHT5", 'P'),
            new Row("GYMLIGHT", 'P'),
            new Row("LIGHT1", 'P'),
            new Row("LIGHT2A", 'P'),
            new Row("LIGHT3A", 'P'),
            new Row("LIGHT3B", 'P'),
            new Row("LIGHT3C", 'P'),
            new Row("LIGHT3D", 'P'),
            new Row("LIGHT3E", 'P'),
            new Row("LIGHT3F", 'P'),
            new Row("LIGHT4A", 'P'),
            new Row("LIGHT5A", 'P'),
            new Row("LIGHT5B", 'P'),
            new Row("LIGHT5C", 'P'),
            new Row("LIGHT5D", 'P'),
            new Row("LIGHT5E", 'P'),
            new Row("LIGHT5F", 'P'),
            new Row("LIGHT6A", 'P'),
            new Row("METALSTCH2", 'P'),
            new Row("TNNL_LGT1", 'P'),
            new Row("TNNL_LGT2", 'P'),
            new Row("TNNL_LGT3", 'P'),
            new Row("TNNL_LGT4", 'P'),
            new Row("LAB1_GAD2", 'P'),
            new Row("ELEV1_DWN", 'P'),
            new Row("SPOTBLUE", 'P'),
            new Row("SPOTGREEN", 'P'),
            new Row("SPOTRED", 'P'),
            new Row("SPOTYELLOW", 'P'),
            new Row("LAB1_GAD3", 'P'),
            new Row("LAB1_GAD3B", 'P'),
            new Row("ELEV2_PAN", 'P'),
            new Row("ELEV1_PAN", 'P'),
            new Row("C2A4X_C3", 'P'),
            new Row("C2A4X_C1", 'P'),
            new Row("MEDKIT", 'P'),
            new Row("GLASS_BRIGHT", 'Y'),
            new Row("GLASS_DARK", 'Y'),
            new Row("GLASS_MED", 'Y'),
            new Row("GLASSBLUE1", 'Y'),
            new Row("GLASSBLUE2", 'Y'),
            new Row("GLASSGREEEN", 'Y'),
            new Row("GLASSGREEEN", 'Y'),
            new Row("cubed", 'D'),
            new Row("cubrusha", 'N'),
            new Row("cudumpsterc", 'M'),
            new Row("cugroundb", 'D'),
            new Row("cuiflra", 'T'),
            new Row("cuiflrb", 'T'),
            new Row("cuiflrc", 'T'),
            new Row("cuiflrd", 'T'),
            new Row("cuiflre", 'T'),
            new Row("cuiflrf", 'T'),
            new Row("custovetop", 'M'),
            new Row("cutrashb", 'M'),
            new Row("cuwoodm", 'W'),
            };

            /// <summary>表里的行数（离线宿主用它核对「表没被截断」）。</summary>
            public static int TableCount => Table.Length;

            /// <summary>按序号读表的一行（供离线自检遍历；越界返回 false）。</summary>
            public static bool TryGetRow(int index, out string texture, out char letter)
            {
                texture = null;
                letter = '\0';
                if (index < 0 || index >= Table.Length) return false;
                texture = Table[index].Texture;
                letter = Table[index].Letter;
                return true;
            }

            /// <summary>
            /// 按运行时材质名查材质字母（纯函数，不依赖 Unity，可离线断言）。
            ///
            /// <para><b>比较口径</b> = 原版 <c>strnicmp(name, stored, 16)</c>（<c>pm_shared.c:272</c>）：
            /// 大小写不敏感；比较到库里名字的结尾为止 ⇒ 等价于「**运行时名的前 16 字符**与库里的名字**整名相等**」。
            /// （<c>strnicmp</c> 用进程当前 locale 折叠大小写，这里用 <c>OrdinalIgnoreCase</c> —— 表里全是 ASCII，两者对本表等价。）</para>
            ///
            /// <para><b>查不到</b>返回 <c>false</c>（本方法只回答「表里有没有这个名字」）：由调用点 <c>ClassifyImpact</c> 按原版口径落默认字母 '<c>C</c>'（<c>pm_shared.c:288</c>）。</para>
            /// </summary>
            public static bool TryGetLetter(string texture, out char letter)
            {
                letter = '\0';
                if (string.IsNullOrEmpty(texture)) return false;

                var key = texture.Length > 16 ? texture.Substring(0, 16) : texture;
                for (var i = 0; i < Table.Length; i++)
                {
                    if (string.Equals(Table[i].Texture, key, System.StringComparison.OrdinalIgnoreCase))
                    {
                        letter = Table[i].Letter;
                        return true;
                    }
                }
                return false;
            }
        }
        // <<< 生成块结束

        // ==================================================================
        //  高频防护 / 日志
        // ==================================================================
        /// <summary>同一个音效在 <see cref="ClipWindow"/> 内最多同时起播几次（超出丢弃）。
        /// 出处：**本项目新增**（高频防护阈值；由 <c>AudioModule.Start</c> 下发给引擎
        /// <c>ISoundManager.MaxConcurrentPerClip</c>，原版无对应量）。</summary>
        public const int MaxConcurrentPerClip = 8;

        /// <summary>并发计数的滑动窗口（秒）。出处：**本项目新增**（与
        /// <see cref="MaxConcurrentPerClip"/> 配套，同上）。</summary>
        public const float ClipWindow = 0.10f;

        /// <summary>单帧最多起播的音效数（防"一帧打进几十个人"把音源池打满）。
        /// 出处：**本项目新增**（下发给引擎 <c>ISoundManager.MaxPlaysPerFrame</c>，原版无对应量）。</summary>
        public const int MaxPlaysPerFrame = 8;

        /// <summary>音量设置轮询间隔（秒）——设置面板没有变更事件，用轮询 + 只在变化时应用。
        /// 出处：**本项目新增**（本工程设置层 <c>UI/Flow/CsPlayerSettingsStore</c> 无变更通知，
        /// 故轮询；轮询周期是音频层实现参数）。</summary>
        public const float VolumePollInterval = 0.5f;

        // ---- 玩家设置键：本文件不再持有字面量 ----
        // 音量键 = <c>CsSettingsKeys.VolumeMaster</c> / <c>CsSettingsKeys.VolumeSfx</c>（消费点见 AudioModule.ApplyVolume）。
        // 不要在本文件重新引入转发常量（那还是多一处副本）。

        /// <summary>同类日志的打点间隔（首次 + 每 N 次）。出处：**本项目新增**
        /// （日志降频口径；工程内同款见 <c>Module/Bot/CsBotConst.LogRateEvery</c>）。</summary>
        public const int LogRateEvery = 50;
    }
}
