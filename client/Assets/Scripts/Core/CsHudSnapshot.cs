using System.Collections.Generic;

namespace Cs16.Core
{
    /// <summary>HUD 击杀信息一行。</summary>
    public struct CsKillFeedItem
    {
        public string KillerName;
        public CsTeam KillerTeam;
        public string VictimName;
        public CsTeam VictimTeam;
        public string WeaponId;
        public bool Headshot;
        public float BornTime;   // 比赛时钟（Core/CsClock.Now），用于淡出
    }

    /// <summary>雷达上的一个点。</summary>
    public struct CsRadarDot
    {
        public float X;          // 世界 X
        public float Z;          // 世界 Z
        public CsTeam Team;
        public bool IsSelf;
        public bool IsBomb;
        public bool IsBombsite;
    }

    /// <summary>
    /// **UI 与业务之间的唯一数据契约**（只读快照）。
    ///
    /// <para>原因：`reference/architecture.md` 要求 UI 不许 `using` 任何 `Module.*`。
    /// HUD / 记分板 / 买枪菜单只读本类。</para>
    ///
    /// <para><b>写入方</b>：<see cref="Cs16.Module.Match"/>（血量/护甲/金钱/弹药/回合/比分/炸弹/买枪/观战/击杀信息/雷达）、
    /// <see cref="Cs16.Module.Combat"/>（准星扩散/命中标记/闪光）。
    /// <b>读取方</b>：全部 UI 面板。</para>
    /// </summary>
    public static class CsHudSnapshot
    {
        /// <summary>是否有比赛在运行（false 时 HUD 应隐藏）。</summary>
        public static bool Valid;

        // ---- 玩家自身 ----
        public static CsTeam Team;
        public static bool IsAlive;
        public static bool IsSpectating;
        public static string SpectateName;
        public static int Health;
        public static int Armor;
        public static bool HasHelmet;
        public static int Money;
        public static int Mag;
        public static int Reserve;
        public static string WeaponId;
        public static string WeaponName;
        public static bool IsZoomed;

        // ---- 回合 / 比分 ----
        public static CsRoundPhase Phase;
        public static int RoundNumber;
        public static float PhaseTimeLeft;
        public static int ScoreT;
        public static int ScoreCT;
        public static bool BombPlanted;
        public static float BombTimeLeft;
        public static float UseProgress;      // -1 = 未在操作

        // ---- 买枪 ----
        public static bool InBuyZone;
        public static bool CanBuyNow;
        /// <summary>主武器槽当前那把枪的 id（null = 该槽为空）—— 买弹药的分类 6（PRIMARY AMMO）按它列条目。</summary>
        public static string PrimaryWeaponId;
        /// <summary>主武器槽当前那把枪的备弹。</summary>
        public static int PrimaryReserve;
        /// <summary>副武器槽当前那把枪的 id（null = 该槽为空）—— 买弹药的分类 7（SECONDARY AMMO）按它列条目。</summary>
        public static string SecondaryWeaponId;
        /// <summary>副武器槽当前那把枪的备弹。</summary>
        public static int SecondaryReserve;

        // ---- 准星 / 命中（由 Module/Combat 写）----
        public static float CrosshairSpread;        // 0~1
        public static float HitMarkerTime;          // 剩余显示时间（>0 时显示）
        public static bool HitMarkerHeadshot;
        public static float FlashAlpha;             // 0~1 全屏白

        // ---- 受伤提示（自己被打）----
        public static float DamageIndicatorTime;    // 剩余显示时间
        public static float DamageFromYaw;          // 伤害来源相对朝向（度）

        // ---- 列表（每帧由模拟清空并重填）----
        public static readonly List<CsKillFeedItem> KillFeed = new List<CsKillFeedItem>(8);
        public static readonly List<CsRadarDot> Radar = new List<CsRadarDot>(32);
        /// <summary>最近一条系统/无线电消息 + 它的剩余显示时间。</summary>
        public static string LastMessage;
        public static float LastMessageTime;

        /// <summary>
        /// 清空（比赛结束 / 回主菜单时调用）。
        /// **必须调用** —— 否则关掉快速进入 Play 模式时静态值会跨局残留（见 skill P-3）。
        /// </summary>
        public static void Reset()
        {
            Valid = false;
            Team = CsTeam.Spectator;
            IsAlive = false;
            IsSpectating = false;
            SpectateName = null;
            Health = 0;
            Armor = 0;
            HasHelmet = false;
            Money = 0;
            Mag = 0;
            Reserve = 0;
            WeaponId = null;
            WeaponName = null;
            IsZoomed = false;
            Phase = CsRoundPhase.None;
            RoundNumber = 0;
            PhaseTimeLeft = 0f;
            ScoreT = 0;
            ScoreCT = 0;
            BombPlanted = false;
            BombTimeLeft = -1f;
            UseProgress = -1f;
            InBuyZone = false;
            CanBuyNow = false;
            PrimaryWeaponId = null;
            PrimaryReserve = 0;
            SecondaryWeaponId = null;
            SecondaryReserve = 0;
            CrosshairSpread = 0f;
            HitMarkerTime = 0f;
            HitMarkerHeadshot = false;
            FlashAlpha = 0f;
            DamageIndicatorTime = 0f;
            DamageFromYaw = 0f;
            KillFeed.Clear();
            Radar.Clear();
            LastMessage = null;
            LastMessageTime = 0f;
        }
    }
}
