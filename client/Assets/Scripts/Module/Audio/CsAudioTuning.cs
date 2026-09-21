namespace Cs16.Module.Audio
{
    /// <summary>
    /// **本项目新增**的音效层数值与**音效名真源**（对应 agent-07 的产出）。
    ///
    /// <para><b>命名口径（必须与 agent-04 对齐）</b>：引擎的 <c>Game.Sound.PlaySFX(name)</c> 会加载
    /// <c>Resources/Sound/SFX/{name}</c>；本工程约定 <c>name = "sfx/&lt;短名&gt;"</c>
    /// （<c>CsWeapons.SoundFire</c> 就是 <c>"sfx/&lt;id&gt;_fire"</c>，agent-04 的 <c>CsCombatTuning</c>
    /// 里 <c>HitMarkerSfx = "sfx/hitmarker"</c> 同理）。
    /// 因此实际文件落在 <c>Assets/Resources/Sound/SFX/sfx/&lt;短名&gt;.wav</c> ——
    /// 由 <c>tools</c> 侧的 <c>cs16_build.py</c> 从 CS 1.6 原始 wav 生成。**改短名 = 同时改这里和生成脚本**。</para>
    ///
    /// <para><b>为什么本文件每一行都要有出处</b>（切片K 补）：本类的常量分两类 ——
    /// ① **短名**（<c>"sfx/xxx"</c>）：出处 = 它在盘上的那个资源文件，以及"原版哪一个 wav"的映射，
    /// 映射真源 = <c>client/资源欠缺清单.md</c> 第 1 节资源表（agent-07 逐条记的"当前实现"列）；
    /// ② **数值**（间隔 / 并发上限 / 距离）：出处 = 本项目音频层的实现参数（原版无对应量），
    /// 或本工程另有定义处的那个文件。⛔ 查不到出处的**不许编**，一律登记 <c>策划/差异登记.tsv</c>。</para>
    ///
    /// <para><b>为什么不重复播枪声</b>：枪声（含机器人枪声）由 agent-04 的 <c>CombatModule</c> 统一发放
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

        /// <summary>跑一步的距离（米）——脚步声按"走过的距离"触发，而不是按固定时间。
        /// 出处：**本项目新增**（原版的脚步触发口径在 GoldSrc <c>pm_shared.c</c> 的
        /// <c>PM_PlayStepSound</c>，该载体不在盘 —— <c>原版资源/cs16src</c> 已空，见
        /// <c>client/资源欠缺清单.md:76</c> 第 17 项；已登记 <c>策划/差异登记.tsv</c>）；
        /// 现值 = 与 <c>CsConst.SpeedKnife 5.4 m/s</c> 下的步频配平得出。</summary>
        public const float StepDistanceRun = 0.62f;

        /// <summary>低于该水平速度就不算"在跑"（米/秒）。出处：**本项目新增**（音频层实现参数；
        /// 原版对应量在 <c>pm_shared.c</c>，载体不在盘 ⇒ 同上，已登记 <c>策划/差异登记.tsv</c>）。</summary>
        public const float StepMinSpeed = 1.2f;

        /// <summary>两步之间的最短间隔（秒）——防止贴墙抖动/高频刷音。出处：**本项目新增**
        /// （高频防护参数，原版无对应量；与 <see cref="MaxConcurrentPerClip"/> 同属引擎
        /// <c>ISoundManager</c> 闸门的使用侧参数）。</summary>
        public const float StepMinInterval = 0.16f;

        /// <summary>别人的脚步声的 3D 播放距离（米）——超过就不播（省音源、也符合听觉直觉）。
        /// 出处：**本项目新增**（音频层实现参数，原版无对应量）。</summary>
        public const float StepHearDistance = 26f;

        /// <summary>落地判定：落地前竖直速度低于它才算"摔了一下"（米/秒）。出处：**本项目新增**
        /// —— 原版 GoldSrc 有 <c>PLAYER_FALL_PUNCH_THRESHHOLD 350</c> units/s（= 8.89 m/s，用于"落地屏抖"），
        /// 但那是**屏抖**阈值、不是"落地音"阈值，且该载体（<c>pm_shared.c</c>）不在盘 ⇒
        /// 本值按"落地音该在哪一档响"自定，并登记 <c>策划/差异登记.tsv</c>。</summary>
        public const float LandMinFallSpeed = 5.5f;

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
        //  切片K（D8 音效事件接线）新增的 5 条短名 —— 盘上早有文件，此前无人挂事件
        // ==================================================================
        /// <summary>空仓扣扳机。出处：资源文件 <c>Assets/Resources/Sound/SFX/sfx/dryfire.wav</c>（在盘，
        /// 原版 CS 1.6 的空仓击发音，见 <c>策划/验收表.md</c>「允许的差异」中切片H 登记的那 5 条之一）；
        /// ⚠️ 原版**源文件名的映射未记录**（<c>client/资源欠缺清单.md:37</c> 第 11 项只记了
        /// <c>weapons/c4_beep1.wav</c> 等 8 个）⇒ 一并登记 <c>策划/差异登记.tsv</c>。
        /// 事件挂点：<c>Module/Combat/CombatModule.CanFire</c> 的"弹匣为空"分支。</summary>
        public const string Dryfire = "sfx/dryfire";

        /// <summary>两次空仓击发音之间的最短间隔（秒）。出处：**本项目新增**（按住左键时
        /// <c>CombatModule.CanFire</c> 的"弹匣为空"分支**每帧**都会走到，若不加时间闸就是"每帧一响"；
        /// 原版对应的是"每次扣扳机一响"，但其节拍写在 <c>mp.dll</c> 的武器 <c>PrimaryAttack</c> 里，
        /// 载体不在盘 ⇒ 值本项目自定）。</summary>
        public const float DryfireMinInterval = 0.25f;

        /// <summary>弹着墙（非角色碰撞体）。出处：资源文件
        /// <c>Assets/Resources/Sound/SFX/sfx/hit_wall.wav</c>（在盘，原版弹着音）；
        /// ⚠️ 原版**按材质分的多条弹着音**（<c>sound/weapons/</c> 的弹着族）与文件名映射都未记录、
        /// 载体（<c>原版资源/cs16src</c>）已空 ⇒ 已登记 <c>策划/差异登记.tsv</c>：
        /// 现在全部材质落这一条 clip，材质分类见 <see cref="ClassifyImpact"/>（分类已做、表只有一行）。
        /// 事件挂点：<c>Module/Combat/CombatModule.HandleLocalShot</c> 的"打墙落点"循环。</summary>
        public const string HitWall = "sfx/hit_wall";

        /// <summary>刀命中。出处：资源文件 <c>Assets/Resources/Sound/SFX/sfx/knife_hit.wav</c>（在盘，
        /// 原版刀命中音）；⚠️ 原版源文件名映射未记录 ⇒ 登记 <c>策划/差异登记.tsv</c>。
        /// 事件挂点：<c>Module/Match/CsDamage.ApplyHit</c>（<c>def.Class == Knife</c> 且出刀者是本地玩家）。
        /// ⛔ 原版"刀命中"有 hit / hit_wall 两种落点（打人 vs 打墙），盘上只有 <c>knife_hit</c> 一条。</summary>
        public const string KnifeHit = "sfx/knife_hit";

        /// <summary>闪光弹爆炸。出处：原版 CS 1.6 <c>sound/weapons/flashbang-1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项；盘上 = <c>sfx/flash_explode.wav</c>）。
        /// 事件挂点：<c>Module/Match/CsDamage.ApplyFlash</c>（闪成功任何目标时按爆炸点播一次）。</summary>
        public const string FlashExplode = "sfx/flash_explode";

        /// <summary>C4 加速档蜂鸣（剩余时间 ≤ <see cref="BombBeepFastBelow"/> 时用它替换
        /// <see cref="BombBeep"/>）。出处：资源文件
        /// <c>Assets/Resources/Sound/SFX/sfx/bomb_beep_fast.wav</c>（在盘，原版 C4 快速蜂鸣音）；
        /// ⚠️ 原版源文件名映射未记录（<c>资源欠缺清单.md:37</c> 只记了 <c>c4_beep1.wav</c>）⇒
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
        /// <summary>炸弹蜂鸣（普通档）。出处：原版 CS 1.6 <c>sound/weapons/c4_beep1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。加速档见 <see cref="BombBeepFast"/>。</summary>
        public const string BombBeep = "sfx/bomb_beep";

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

        /// <summary>手雷爆炸。出处：原版 CS 1.6 <c>sound/weapons/hegrenade-1.wav</c>
        /// （映射见 <c>client/资源欠缺清单.md:37</c> 第 11 项）。</summary>
        public const string GrenadeExplode = "sfx/grenade_explode";

        /// <summary>剩余时间低于它的蜂鸣间隔切换到"加速档"（秒）——与 <c>CsConst</c> 的口径一致。
        /// 出处：本工程自己的分档点，与 <c>Core/CsConst.cs</c> 的
        /// <c>BombBeepIntervalSlow</c>（剩余 &gt;10s）/ <c>BombBeepIntervalFast</c>（剩余 ≤10s）同值同界
        /// （那两行的注释写明"剩余 &gt;10s / 剩余 &lt;=10s"）；⚠️ 该 10s 分界本身的**原版出处拿不到**
        /// （C4 蜂鸣节奏在 <c>mp.dll</c>，载体不在盘）⇒ 一并登记 <c>策划/差异登记.tsv</c>。</summary>
        public const float BombBeepFastBelow = 10f;

        // ==================================================================
        //  命中材质分类（切片K D8：hit_wall 的"按材质分流"入口）
        // ==================================================================
        /// <summary>命中材质无法识别时的分类名。出处：**本项目新增**（分类枚举的兜底值；
        /// 原版没有"材质分类"这个量，它是本工程为 <c>hit_wall</c> 的分流落点自建的）。</summary>
        public const string ImpactClassUnknown = "unknown";

        /// <summary>
        /// 把命中物的材质名（<c>Material.name</c> = BSP 贴图组名，如 <c>csSandWallDJ1</c>；
        /// 阻挡盒没有材质 ⇒ 退到节点名 <c>Blocker_*</c>）归到几个大类。
        ///
        /// <para><b>为什么要有这一步</b>：原版 CS 的着弹音**按材质分流**（打沙地 / 打木箱 / 打金属各有采样），
        /// 而本工程盘上**只有一条** <c>hit_wall.wav</c>。所以"分流"落在这一层：
        /// 分类先做出来、日志里记下来（可被离线断言与日志核对），拿不到的那几条按材质采样
        /// 已登记 <c>策划/差异登记.tsv</c> —— 补齐后只改本函数与调用点，不散落到别处。
        /// 关键词取自 <c>ThirdParty/Dust2/Textures/*.png</c> 的**真实组名**
        /// （<c>box</c>/<c>box_x</c> = 原版 <c>func_breakable</c> 木箱，见 <c>策划/对照表.md</c> G-16）。</para>
        /// </summary>
        public static string ClassifyImpact(string materialName)
        {
            if (string.IsNullOrEmpty(materialName)) return ImpactClassUnknown;
            var n = materialName.ToLowerInvariant();

            if (n.Contains("box")) return "wood";          // box / box_x = func_breakable 木箱
            if (n.Contains("door")) return "wood-door";    // SandWllDoor* = 门板面
            if (n.Contains("crt") || n.Contains("crate")) return "wood-crate";
            if (n.Contains("ccrete") || n.Contains("crete") || n.Contains("concrete")) return "concrete";
            if (n.Contains("metal") || n.Contains("trim")) return "metal";
            if (n.Contains("sand") || n.Contains("ground") || n.Contains("path") || n.Contains("site")) return "sand";
            return ImpactClassUnknown;
        }

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

        // ---- 玩家设置键（必须与 agent-01 的 CsPlayerSettingsStore 一致）----
        /// <summary>主音量设置键。出处：<c>UI/Flow/CsPlayerSettingsStore.cs</c>（本工程设置键真源；
        /// 本常量与它逐字同名同值 —— 两处必须一起改）。</summary>
        public const string SettingKeyVolumeMaster = "cs.player.volumeMaster";

        /// <summary>音效音量设置键。出处：<c>UI/Flow/CsPlayerSettingsStore.cs</c>（同
        /// <see cref="SettingKeyVolumeMaster"/>）。</summary>
        public const string SettingKeyVolumeSfx = "cs.player.volumeSfx";

        /// <summary>同类日志的打点间隔（首次 + 每 N 次）。出处：**本项目新增**
        /// （日志降频口径；工程内同款见 <c>Module/Bot/CsBotConst.LogRateEvery</c>）。</summary>
        public const int LogRateEvery = 50;
    }
}
