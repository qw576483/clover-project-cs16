using System.Collections.Generic;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 一个参与者（真人玩家或机器人）的**权威状态**。比赛模拟是唯一写入者，其余模块只读。
    /// </summary>
    public sealed class CsActor
    {
        // ---- 身份 ----
        public long Id;
        public string Name;
        public CsTeam Team;
        public bool IsBot;
        public CsBotDifficulty Difficulty;

        // ---- 空间 ----
        public Vector3 Position;
        public Vector3 Velocity;
        public float Yaw;               // 水平朝向（度，0=+Z）
        public float Pitch;             // 垂直朝向（度，-89~89）
        public bool OnGround;
        public bool IsCrouching;
        public bool IsWalking;          // Shift 慢走（无脚步声）
        public float Height => IsCrouching ? CsConst.CrouchHeight : CsConst.StandHeight;
        public Vector3 EyePosition => Position + new Vector3(0f, EyeHeight, 0f);
        public float EyeHeight => IsCrouching ? CsConst.EyeHeight * 0.72f : CsConst.EyeHeight;

        // ---- 生存 ----
        public bool IsAlive;
        public int Health;
        public int Armor;
        public bool HasHelmet;
        public bool HasDefuser;

        // ---- 经济 ----
        public int Money;

        // ---- 装备 ----
        public string PrimaryWeapon;    // null = 无
        public string SecondaryWeapon;
        public string KnifeWeapon = CsWeapons.Knife;
        public string ActiveWeapon;
        public bool HasBomb;

        /// <summary>每种武器的弹药：weaponId → (弹匣内, 备弹)。</summary>
        public readonly Dictionary<string, (int inMag, int reserve)> Ammo =
            new Dictionary<string, (int, int)>();

        // ---- 战斗统计 ----
        public int Kills;
        public int Deaths;
        public int Assists;
        public int RoundKills;
        public int Score;

        // ---- 运行时（表现/输入）----
        public float NextFireTime;
        public float ReloadEndTime;

        /// <summary>
        /// 换弹**请求序号**：单调递增，只在 <see cref="CsInventory.Reload"/> 的**成功分支**自增一次。
        /// <para><b>为什么要有它</b>（差异 #72「换弹动画会丢」）：<c>ReloadEndTime</c> 是**截止时间**，
        /// 不是"开始事件"——它会在 ① 换弹完成（<c>CsMatch.TickActorTimers</c> 结算时归零）、
        /// ② 切枪（<c>CsInventory.SwitchWeapon</c>）时被**归零**，③ 同一帧内"开始 → 完成"（掉帧、
        /// 时间跳变时的 <c>+= delta</c>）而**根本没有中间采样**。表现层若拿「<c>ReloadEndTime</c> 比上一帧大」
        /// 当边沿，就会在这三种序列上整段丢掉换弹动画（用户报的"有时候会丢"，"有时候"正是这些序列）。</para>
        /// <para>序号只增不减 ⇒ 表现层判「变了没」必然感知得到；且它**与成功换弹次数一一对应**，
        /// 所以「每次成功换弹都必须播到一次 reload」这件事可以被断言（见 <c>CombatSelfTest</c>）。</para>
        /// </summary>
        public int ReloadSeq;

        /// <summary>
        /// 差异 #68：当前武器的**消音器**是否装上（只对 <c>CsWeaponDef.CanSilence</c> 的武器有意义）。
        /// <para>刻意**不**在切枪时清零：原版里消音器是装在**那把枪**上的（换走再换回来仍是装着的），
        /// 而本工程只有一把主武器槽 + 一把手枪槽 ⇒ 挂在角色上、按当前手持武器是否 CanSilence 决定是否读它。</para>
        /// </summary>
        public bool Silenced;

        /// <summary>差异 #68：当前武器是否处于**连发模式**（只对 <c>CsWeaponDef.CanBurst</c> 的武器有意义）。</summary>
        public bool BurstMode;

        /// <summary>
        /// 差异 #68：连发模式下**本梭还差几发**没打（0 = 没有待发的续发）。
        /// 一次扣扳机只打首发，其余由 <c>CsInventory.TickBurst</c> 按原版时间戳补发。
        /// </summary>
        public int BurstShotsLeft;

        /// <summary>差异 #68：连发下一发的时刻（假时钟口径，与 <see cref="NextFireTime"/> 同一时间源）。</summary>
        public float NextBurstShotTime;

        /// <summary>
        /// 差异 #68：模拟**自动补发**的累计发数（每补发一发 +1）。
        /// 表现层据此认出"这一发也是我打的"—— 续发不由输入触发，不能靠"本帧按了左键"判断。
        /// </summary>
        public int BurstAutoShots;

        public float SwitchEndTime;
        public int ConsecutiveShots;    // 连发计数（后坐力累积）
        public float RecoilPitch;       // 当前后坐力抬升（度）
        public float RecoilYaw;
        public float FlashEndTime;      // 被闪光弹致盲的结束时间
        public float UseProgress;       // 下包/拆包进度 0~1

        public bool IsAliveAndPlayable => IsAlive;

        /// <summary>当前手持武器的定义（可能为 null）。</summary>
        public CsWeaponDef ActiveDef => CsWeapons.Get(ActiveWeapon);

        /// <summary>取某武器弹药；没有则按武器表初始化。</summary>
        public (int inMag, int reserve) GetAmmo(string weaponId)
        {
            if (Ammo.TryGetValue(weaponId, out var a)) return a;
            var def = CsWeapons.Get(weaponId);
            if (def == null) return (0, 0);
            var init = (def.Magazine, def.ReserveAmmo);
            Ammo[weaponId] = init;
            return init;
        }

        public void SetAmmo(string weaponId, int inMag, int reserve)
        {
            Ammo[weaponId] = (inMag, reserve);
        }

        /// <summary>是否在买枪时间/买枪区域内（由比赛模拟维护）。</summary>
        public bool InBuyZone;
    }

    /// <summary>击杀事件（HUD 击杀信息 + 记分板用）。</summary>
    public struct CsKillEvent
    {
        public long KillerId;
        public string KillerName;
        public CsTeam KillerTeam;
        public long VictimId;
        public string VictimName;
        public CsTeam VictimTeam;
        public string WeaponId;
        public bool Headshot;
        public bool IsSuicide;
        public long AssisterId;
    }

    /// <summary>回合结算信息。</summary>
    public struct CsRoundEndInfo
    {
        public int RoundNumber;
        public CsTeam Winner;
        public CsRoundEndReason Reason;
        public int ScoreT;
        public int ScoreCT;
    }

    /// <summary>子弹命中结果（射击模块 → 比赛模拟）。</summary>
    public struct CsHitInfo
    {
        public long ShooterId;
        public long VictimId;
        public string WeaponId;
        public CsHitbox Hitbox;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public bool ThroughWall;
    }

    /// <summary>机器人难度参数（3 档；数值差异是"3 档难度"的核心证据）。</summary>
    public struct CsBotProfile
    {
        public float ReactionTime;      // 发现敌人到开火的延迟（秒）
        public float AimErrorDegrees;   // 瞄准最大误差（度）
        public float AimSpeedDegrees;   // 每秒转视角速度（度/秒）
        public float FireBurstMin;      // 连发最短时长
        public float FireBurstMax;
        public float FirePauseMin;      // 连发间隔
        public float FirePauseMax;
        public float VisionRange;       // 视野距离
        public float PreferredRange;    // 偏好交战距离
        public int BuyBudgetTier;       // 买枪档位 0=便宜 1=中 2=贵
        public float HeadshotChance;    // 瞄准头部概率
        public float RepathInterval;    // 重新决策间隔

        public static CsBotProfile For(CsBotDifficulty d)
        {
            switch (d)
            {
                case CsBotDifficulty.Easy:
                    return new CsBotProfile
                    {
                        ReactionTime = 0.65f,
                        AimErrorDegrees = 6.0f,
                        AimSpeedDegrees = 160f,
                        FireBurstMin = 0.12f, FireBurstMax = 0.30f,
                        FirePauseMin = 0.55f, FirePauseMax = 1.10f,
                        VisionRange = 32f,
                        PreferredRange = 12f,
                        BuyBudgetTier = 0,
                        HeadshotChance = 0.08f,
                        RepathInterval = 1.6f,
                    };
                case CsBotDifficulty.Hard:
                    return new CsBotProfile
                    {
                        ReactionTime = 0.15f,
                        AimErrorDegrees = 1.2f,
                        AimSpeedDegrees = 520f,
                        FireBurstMin = 0.28f, FireBurstMax = 0.60f,
                        FirePauseMin = 0.12f, FirePauseMax = 0.28f,
                        VisionRange = 48f,
                        PreferredRange = 22f,
                        BuyBudgetTier = 2,
                        HeadshotChance = 0.42f,
                        RepathInterval = 0.5f,
                    };
                default: // Normal
                    return new CsBotProfile
                    {
                        ReactionTime = 0.32f,
                        AimErrorDegrees = 3.0f,
                        AimSpeedDegrees = 300f,
                        FireBurstMin = 0.18f, FireBurstMax = 0.42f,
                        FirePauseMin = 0.30f, FirePauseMax = 0.65f,
                        VisionRange = 40f,
                        PreferredRange = 16f,
                        BuyBudgetTier = 1,
                        HeadshotChance = 0.22f,
                        RepathInterval = 1.0f,
                    };
            }
        }
    }

    /// <summary>
    /// 机器人每帧产出的**意图**。机器人 AI（Module/Bot）只负责决策并提交本结构，
    /// 真正执行（移动/射击/下包）统一由 <see cref="ICsMatch"/> 内部完成 —— 这样 AI 与模拟完全解耦。
    /// </summary>
    public struct CsBotIntent
    {
        /// <summary>世界空间移动方向（已归一化；y=0）。</summary>
        public Vector3 Move;
        public bool Jump;
        public bool Crouch;
        public bool Walk;
        public bool Fire;
        /// <summary>瞄准的世界坐标点（模拟内部换算成朝向并施加难度误差）。</summary>
        public Vector3 AimPoint;
        public bool Reload;
        /// <summary>想切换到的武器 id（null = 不换）。</summary>
        public string SwitchTo;
        /// <summary>E：下包 / 拆包。按住式。</summary>
        public bool Use;
        /// <summary>本次决策对应的 AI 状态（仅用于日志/调试显示）。</summary>
        public CsBotState State;
    }

    /// <summary>机器人的 AI 状态。</summary>
    public enum CsBotState
    {
        Idle = 0,
        Patrol = 1,      // 沿路径点巡逻/推进
        Engage = 2,      // 交战中
        Plant = 3,       // 下包
        Defuse = 4,      // 拆包
        Camp = 5,        // 守点
    }
}
