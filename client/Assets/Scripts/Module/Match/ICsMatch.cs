using System;
using System.Collections.Generic;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Match
{
    /// <summary>本地玩家每帧下发的输入意图（由 Module/Player 采集 Game.Input 后填入）。</summary>
    public struct CsInputState
    {
        /// <summary>归一化移动方向：x = 右(+1)/左(-1)，y = 前(+1)/后(-1)。</summary>
        public Vector2 Move;
        public bool Jump;
        public bool Crouch;
        public bool Walk;      // Shift
        public bool Fire;      // 左键按住
        public bool Zoom;      // 右键（AWP/Scout 开镜）
        /// <summary>
        /// 右键的**按下沿**（差异 #68）：USP·M4A1 切换消音器、Glock18·FAMAS 切换连发模式。
        /// <para>⛔ 必须是"本帧刚按下"（采集方填 <c>GetKeyDown</c>）而**不是**按住 —— 原版的 attack2 是
        /// **切换型**（按一下切一次），按住会在每一帧翻转一次。出处见 <c>CsWeapons.MarkAttack2Capabilities</c>。</para>
        /// </summary>
        public bool Attack2;
        public float Yaw;      // 视角水平角（度，由相机模块算好）
        public float Pitch;    // 视角俯仰角（度）
    }

    /// <summary>
    /// 本地比赛模拟的**唯一对外门面**（单机版的"服务器"）。
    ///
    /// <para>职责：回合流转、经济、买枪、射击结算、炸弹、死亡/复活、机器人 AI 驱动、比分。</para>
    /// <para>写入者只有一个（<see cref="CsMatch"/>）；其余模块（Player / View / UI / Audio）只读本接口。</para>
    /// </summary>
    public interface ICsMatch
    {
        // ==================== 生命周期 ====================
        /// <summary>开一场比赛（进入 Stage 场景后调用）。重复调用 = 先 Stop 再 Start。</summary>
        void Start(CsMatchConfig cfg);
        void Stop();
        /// <summary>由 MatchModule 的 Update 驱动（App 不写业务每帧逻辑）。</summary>
        void Tick(float dt);

        bool IsRunning { get; }
        bool IsPaused { get; }
        CsMatchConfig Config { get; }

        // ==================== 状态查询 ====================
        CsRoundPhase Phase { get; }
        int RoundNumber { get; }
        int ScoreT { get; }
        int ScoreCT { get; }
        /// <summary>当前阶段剩余秒数（Freeze / Live / RoundEnd）。</summary>
        float PhaseTimeLeft { get; }
        /// <summary>玩家是否处于买枪时间且站在买枪区。</summary>
        bool CanBuyNow { get; }
        /// <summary>本队是否可买（有些武器限阵营）。</summary>
        bool CanBuyWeapon(string weaponId, out string reason);
        /// <summary>买枪区（用于底部 Buy Zone 提示）。</summary>
        bool IsInBuyZone(Vector3 position, CsTeam team);

        bool BombPlanted { get; }
        Vector3 BombPosition { get; }
        /// <summary>炸弹剩余秒数（未下包为 -1）。</summary>
        float BombTimeLeft { get; }
        /// <summary>下包/拆包进度 0~1（-1 = 未在进行）。</summary>
        float UseProgress { get; }
        bool BombCarrierIs(long actorId);

        IReadOnlyList<CsActor> Actors { get; }

        /// <summary>
        /// **世界中的掉落武器**（差异 #75）。表现层按它同步"地上那把枪"的世界视图；
        /// 已被拾取的项会立刻从列表里摘掉（⇒ 表现层按"不在列表里"回收视图）。
        /// </summary>
        IReadOnlyList<CsDroppedWeapon> DroppedWeapons { get; }
        CsActor LocalPlayer { get; }
        CsActor SpectateTarget { get; }
        CsActor Find(long actorId);
        int AliveCount(CsTeam team);
        int PlayerCount(CsTeam team);

        /// <summary>角色朝向某点的视线是否被墙挡住（机器人/闪光弹用；内部走 Physics.Raycast）。</summary>
        bool HasLineOfSight(Vector3 from, Vector3 to, float maxDistance = 100f);

        // ==================== 本地玩家输入 ====================
        /// <summary>每帧由 Module/Player 下发；模拟内部做移动解算（本地碰撞 + 台阶）。</summary>
        void SetLocalInput(in CsInputState input);

        // ==================== 玩家动作 ====================
        /// <summary>换弹（R）。</summary>
        void RequestReload();
        /// <summary>切换武器槽（1-5）。</summary>
        void SwitchSlot(int slot);
        /// <summary>切换到指定武器（拾取/购买后）。</summary>
        void SwitchWeapon(string weaponId);
        /// <summary>买枪（B 菜单，作用于本地玩家）。失败时 <paramref name="reason"/> 给出原因并打日志。</summary>
        bool TryBuy(string weaponId, out string reason);
        /// <summary>
        /// 为**指定 actor**（含机器人）买枪 —— 机器人 AI（Module/Bot）用它实现"会买枪"。
        /// 对不存在/已死亡的 actor 返回 false 并打 Warn。
        /// </summary>
        bool TryBuyFor(long actorId, string weaponId, out string reason);
        /// <summary>E 键：下包（T 持包且在包点）/ 拆包（CT 在包旁）。</summary>
        void SetUseHeld(bool held);
        /// <summary>丢下当前武器。</summary>
        void DropActiveWeapon();
        /// <summary>右键开镜状态（AWP/Scout）。</summary>
        bool IsZoomed { get; }

        // ==================== 观战 ====================
        bool IsSpectating { get; }
        /// <summary>切换观战目标（dir = +1 下一个 / -1 上一个）。</summary>
        void SpectateNext(int dir);

        // ==================== 射击结算 ====================
        /// <summary>射击模块完成射线检测后回传命中；由模拟计算伤害/击杀。</summary>
        void ReportHit(in CsHitInfo hit);
        /// <summary>本帧是否有射击动作发生（供表现层播枪声/后坐力，模拟内部已扣弹药）。</summary>
        bool ConsumeShotFired(out string weaponId);

        // ==================== 机器人 ====================
        void AddBot(CsTeam team, CsBotDifficulty diff);
        /// <summary>踢出最后一个加入的机器人。</summary>
        void KickBot();
        /// <summary>切换全部机器人难度（对已存在的 bot 即时生效）。</summary>
        void SetBotDifficulty(CsBotDifficulty diff);
        CsBotDifficulty BotDifficulty { get; }
        int BotCount { get; }
        /// <summary>机器人在某队的数量。</summary>
        int BotCountOf(CsTeam team);

        /// <summary>
        /// 机器人 AI 提交本帧意图（由 Module/Bot 调用）。模拟内部负责执行：移动解算、射击射线、
        /// 换弹/换枪、下包拆包。**只对 IsBot 的 actor 生效**，对真人 actor 调用会被忽略并打日志。
        /// </summary>
        void SubmitBotIntent(long actorId, in CsBotIntent intent);

        // ==================== 比赛控制（H 菜单 / 记分板） ====================
        void RestartRound();
        void RestartMatch();
        void SetPaused(bool paused);
        void ChangeTeam(CsTeam team);
        /// <summary>投降/换边等：直接结束当前回合给指定方。</summary>
        void ForceEndRound(CsTeam winner, CsRoundEndReason reason);

        // ==================== 事件（表现层订阅）====================
        event Action<CsKillEvent> OnKill;
        event Action<CsRoundEndInfo> OnRoundEnd;
        event Action OnMatchEnd;
        /// <summary>（受击者, 伤害, 是否爆头, 是否致死）</summary>
        event Action<CsActor, int, bool, bool> OnDamaged;
        /// <summary>
        /// （受击者, 命中点, 弹道方向, 是否爆头）—— **子弹确实打中了角色**时触发，供表现层出受击血迹。
        ///
        /// <para><b>为什么不能拿 <see cref="OnDamaged"/> 代替</b>：① 它带的是**伤害值**不是命中点，
        /// 而原版血迹贴在"命中点后面的那个面"上（没有命中点就贴不准）；② 友好伤害关闭 / 护甲全吸收时
        /// <c>OnDamaged</c> 根本不触发，但"打中了"这件事在原版照样出血（血与扣血是两件事）；
        /// ③ 它按"结算后"发，本事件按"命中时"发。</para>
        /// </summary>
        event Action<CsActor, Vector3, Vector3, bool> OnBulletHit;
        /// <summary>（行为者, 文本）—— 无线电/系统提示，用于 HUD 消息栏。</summary>
        event Action<CsActor, string> OnMessage;
        /// <summary>炸弹状态变化（下包/拆包/爆炸）时触发，供音效与 HUD。</summary>
        event Action OnBombStateChanged;
    }
}
