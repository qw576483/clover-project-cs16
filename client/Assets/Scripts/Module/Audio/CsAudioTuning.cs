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
    /// <para><b>为什么不重复播枪声</b>：枪声（含机器人枪声）由 agent-04 的 <c>CombatModule</c> 统一发放
    /// （它是射击队列的唯一消费者）。本模块只补它没做的：脚步 / 落地 / 换弹 / 命中反馈 / 死亡 /
    /// 回合开始结束 / 炸弹蜂鸣与爆炸。</para>
    /// </summary>
    internal static class CsAudioTuning
    {
        // ==================================================================
        //  引擎侧路径（SFX 根目录真源 = Core/ResPaths.cs 的 SoundSfxPrefix）
        // ==================================================================
        /// <summary>引擎 <c>Sound</c> 之下的短名前缀（根目录前缀真源 = <see cref="ResPaths.SoundSfxPrefix"/>）。</summary>
        public const string ClipRoot = "sfx/";

        private static string S(string shortName) => ClipRoot + shortName;

        // ==================================================================
        //  脚步 / 落地 / 跳跃
        // ==================================================================
        /// <summary>4 个脚步变体（CS 1.6 的 player/pl_step1..4）——交替播放避免"复读机"。</summary>
        public static readonly string[] Step =
        {
            S("step"), S("step2"), S("step3"), S("step4"),
        };

        /// <summary>落地音（CS 1.6 没有独立落地音，用脚步的短促一记，见《资源欠缺清单》）。</summary>
        public const string Land = "sfx/land";

        /// <summary>跳跃音。</summary>
        public const string Jump = "sfx/jump";

        /// <summary>跑一步的距离（米）——脚步声按"走过的距离"触发，而不是按固定时间。</summary>
        public const float StepDistanceRun = 0.62f;

        /// <summary>低于该水平速度就不算"在跑"（米/秒）。</summary>
        public const float StepMinSpeed = 1.2f;

        /// <summary>两步之间的最短间隔（秒）——防止贴墙抖动/高频刷音。</summary>
        public const float StepMinInterval = 0.16f;

        /// <summary>别人的脚步声的 3D 播放距离（米）——超过就不播（省音源、也符合听觉直觉）。</summary>
        public const float StepHearDistance = 26f;

        /// <summary>落地判定：落地前竖直速度低于它才算"摔了一下"（米/秒）。</summary>
        public const float LandMinFallSpeed = 5.5f;

        // ==================================================================
        //  命中 / 死亡
        // ==================================================================
        /// <summary>自己被击中（无甲）。</summary>
        public const string HitFlesh = "sfx/hit_flesh";

        /// <summary>自己被击中（有甲 / 头盔）。</summary>
        public const string HitKevlar = "sfx/hit_kevlar";

        /// <summary>别人的死亡音（CS 1.6 的 player/die1..3）。</summary>
        public static readonly string[] Death = { S("death"), S("death2"), S("death3") };

        // ==================================================================
        //  回合 / 比赛
        // ==================================================================
        /// <summary>回合开始（CS 1.6 的 radio 语音 "Let's go"）。</summary>
        public const string RoundStart = "sfx/round_start";

        /// <summary>回合开始的备用语音（第一段播放失败时用）。</summary>
        public const string RoundStartAlt = "sfx/round_start2";

        /// <summary>回合结束：T 方获胜播报。</summary>
        public const string WinT = "sfx/win_t";

        /// <summary>回合结束：CT 方获胜播报。</summary>
        public const string WinCT = "sfx/win_ct";

        // ==================================================================
        //  炸弹
        // ==================================================================
        /// <summary>炸弹蜂鸣（普通 / 加速两档共用同一个音，靠间隔区分）。</summary>
        public const string BombBeep = "sfx/bomb_beep";

        /// <summary>下包动作音 / 下包播报。</summary>
        public const string BombPlant = "sfx/bomb_plant";
        public const string BombPlantRadio = "sfx/bomb_plant_radio";

        /// <summary>拆包动作音 / 拆包播报。</summary>
        public const string BombDefuse = "sfx/bomb_defuse";
        public const string BombDefuseRadio = "sfx/bomb_defuse_radio";

        /// <summary>爆炸（C4 爆炸 / 手雷爆炸）。</summary>
        public const string BombExplode = "sfx/bomb_explode";
        public const string GrenadeExplode = "sfx/grenade_explode";

        /// <summary>剩余时间低于它的蜂鸣间隔切换到"加速档"（秒）——与 <c>CsConst</c> 的口径一致。</summary>
        public const float BombBeepFastBelow = 10f;

        // ==================================================================
        //  高频防护 / 日志
        // ==================================================================
        /// <summary>同一个音效在 <see cref="ClipWindow"/> 内最多同时起播几次（超出丢弃）。</summary>
        public const int MaxConcurrentPerClip = 8;

        /// <summary>并发计数的滑动窗口（秒）。</summary>
        public const float ClipWindow = 0.10f;

        /// <summary>单帧最多起播的音效数（防"一帧打进几十个人"把音源池打满）。</summary>
        public const int MaxPlaysPerFrame = 8;

        /// <summary>音量设置轮询间隔（秒）——设置面板没有变更事件，用轮询 + 只在变化时应用。</summary>
        public const float VolumePollInterval = 0.5f;

        // ---- 玩家设置键（必须与 agent-01 的 CsPlayerSettingsStore 一致）----
        public const string SettingKeyVolumeMaster = "cs.player.volumeMaster";
        public const string SettingKeyVolumeSfx = "cs.player.volumeSfx";

        /// <summary>同类日志的打点间隔（首次 + 每 N 次）。</summary>
        public const int LogRateEvery = 50;
    }
}
