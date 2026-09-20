using Cs16.Core;
using Cs16.Module.Match;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary><see cref="BotSight.Check"/> 的判定结果（"机器人这一眼到底看没看见"）。</summary>
    internal enum BotSightResult
    {
        /// <summary>看得见（契约与物理两层都通过，或被"只该忽略的角色受体"挡住后放行）。</summary>
        Clear = 0,
        /// <summary>被<b>世界几何</b>挡住（墙 / 箱子 / 地面 / 斜坡）—— 真的看不见。</summary>
        BlockedByWorld = 1,
        /// <summary>只被<b>角色受体</b>挡住（中间站着别人）—— 按 CS 语义<b>不算</b>遮挡，放行。</summary>
        BlockedByActorOnly = 2,
        /// <summary>契约说被挡，但物理层找不到任何挡点 —— 烟雾（无碰撞体）或未知 → 保守按"看不见"。</summary>
        BlockedUnknown = 3,
        /// <summary>超出视野距离（压根不在判定范围内）。</summary>
        OutOfRange = 4,
    }

    /// <summary>
    /// 机器人"能不能看见某人"的判定。**本项目新增**（不是引擎 API）。
    ///
    /// <para><b>为什么要单独一层，而不是直接问 <see cref="ICsMatch.HasLineOfSight"/></b>：
    /// 契约实现（<c>CsMatch.HasLineOfSight</c>）用的是 <c>Physics.RaycastNonAlloc(..., ~0, ...)</c>
    /// —— <b>全层</b>，于是"射线打到中间站着的队友/敌人的受击体"也被算成遮挡。实测后果是
    /// **机器人进入交战却一发不开**（Play 里 22 次交战 shots=0）：交战的每一 tick 都要重新判定可见，
    /// 而"看不见"的唯一原因就是这条把角色当成墙的视线查询。</para>
    ///
    /// <para><b>CS 1.6 的语义</b>：GoldSrc 的 <c>CBaseEntity::FVisible</c> 用的是
    /// <c>UTIL_TraceLine(..., ignore_monsters, ...)</c>（<c>ignore_monsters == 1</c>），
    /// 也就是**其它玩家不挡视线**，只有世界几何挡。本类就是这个语义。</para>
    ///
    /// <para><b>怎么保住"烟雾"</b>：烟雾在模拟里是纯数据（<c>CsSmokeVolume</c>，没有碰撞体），
    /// 只有契约的 <c>HasLineOfSight</c> 看得见它。所以这里的做法是**先问契约**：
    /// 契约说通 → 通；契约说挡 → 自己再打一趟全层射线看看"是什么挡的"：
    /// <list type="bullet">
    /// <item>找到<b>非角色层</b>的挡点 → 真被世界几何挡（<see cref="BotSightResult.BlockedByWorld"/>）；</item>
    /// <item>只有<b>角色受体</b>挡 → 按 CS 语义放行（<see cref="BotSightResult.BlockedByActorOnly"/>）；</item>
    /// <item><b>一个挡点都找不到</b> → 挡它的只能是"没有碰撞体的东西"= 烟雾 → 保守按看不见
    /// （<see cref="BotSightResult.BlockedUnknown"/>，即**烟雾的遮挡语义完好保留**）。</item>
    /// </list></para>
    ///
    /// <para><b>世界层口径与视图层一致</b>：<c>PhysicsLayers</c> 的 Bot/Player 两层即角色层，
    /// 与 <c>Module/View/ViewModule</c> 的名牌判定（"只让世界几何挡"）是同一套掩码定义。</para>
    /// </summary>
    internal static class BotSight
    {
        /// <summary>角色层（受体所在层）：与 <c>Module/View</c> 的层约定一致。</summary>
        private const int ActorMask = (1 << PhysicsLayers.Bot) | (1 << PhysicsLayers.Player);

        /// <summary>只让世界几何挡的掩码（名牌判定用的同一口径）。</summary>
        private const int WorldOnlyMask = ~ActorMask;

        private const int HitBufferSize = 32;

        /// <summary>本类私有的射线缓冲 —— 绝不借用模拟内部的缓冲（那是模拟的单向写域）。</summary>
        private static readonly RaycastHit[] Hits = new RaycastHit[HitBufferSize];

        /// <summary>
        /// 世界几何是否**没有**挡住 <paramref name="from"/> → <paramref name="to"/>（"只让世界几何挡"口径）。
        /// 诊断日志用它区分"墙挡"与"人挡"。
        /// </summary>
        public static bool WorldClear(Vector3 from, Vector3 to, float range)
        {
            var delta = to - from;
            var dist = delta.magnitude;
            if (dist <= Mathf.Epsilon) return true;
            if (dist > range) return false;

            return !Physics.Raycast(from, delta / dist, dist, WorldOnlyMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// 机器人能否看见 <paramref name="targetEye"/>（CS 1.6 语义：其它角色不挡视线）。
        /// </summary>
        /// <param name="blocker">被挡时给出"挡它的东西"的可读描述（角色名 / 几何体名）；通畅时为 null。</param>
        /// <param name="blockerPoint">被挡时的挡点（诊断用）。</param>
        public static BotSightResult Check(ICsMatch match, Vector3 eye, Vector3 targetEye, float range,
            out string blocker, out Vector3 blockerPoint)
        {
            blocker = null;
            blockerPoint = Vector3.zero;

            if (match == null) return BotSightResult.OutOfRange;

            var delta = targetEye - eye;
            var dist = delta.magnitude;
            if (dist <= Mathf.Epsilon) return BotSightResult.Clear;
            if (dist > range) return BotSightResult.OutOfRange;

            // ① 契约口径：含烟雾（烟雾没有碰撞体，只有契约看得见它）
            if (match.HasLineOfSight(eye, targetEye, range)) return BotSightResult.Clear;

            // ② 契约说"被挡" → 自己再打一趟全层，看清挡住的是什么
            var dir = delta / dist;
            var tolerance = dist - CsConst.PlayerRadius;
            var count = Physics.RaycastNonAlloc(eye, dir, Hits, dist, ~0, QueryTriggerInteraction.Ignore);

            var actorHit = false;
            var worldHit = false;
            var nearest = float.MaxValue;
            var nearestActor = 0L;
            var nearestWorld = (Collider)null;

            for (var i = 0; i < count; i++)
            {
                var h = Hits[i];
                if (h.distance >= tolerance) continue;      // 与契约同一容差口径：末端那点受体不算遮挡

                var proxy = h.collider != null ? h.collider.GetComponentInParent<CsHitboxProxy>() : null;
                if (proxy != null)
                {
                    actorHit = true;
                    if (h.distance < nearest) { nearest = h.distance; nearestActor = proxy.ActorId; blockerPoint = h.point; }
                }
                else
                {
                    worldHit = true;
                    if (h.distance < nearest) { nearest = h.distance; nearestWorld = h.collider; blockerPoint = h.point; }
                }
            }

            if (worldHit)
            {
                blocker = nearestWorld != null ? nearestWorld.name : "世界几何";
                return BotSightResult.BlockedByWorld;
            }

            if (actorHit)
            {
                var a = match.Find(nearestActor);
                blocker = a != null ? $"{a.Name}(actor {nearestActor})" : $"actor {nearestActor}";
                return BotSightResult.BlockedByActorOnly;
            }

            blocker = "无碰撞体的遮挡物（烟雾？）";
            return BotSightResult.BlockedUnknown;
        }

        /// <summary>判定结果 → 一行中文（日志用）。</summary>
        public static string Text(BotSightResult r)
        {
            switch (r)
            {
                case BotSightResult.Clear: return "通畅";
                case BotSightResult.BlockedByWorld: return "被世界几何挡";
                case BotSightResult.BlockedByActorOnly: return "被角色受体挡（CS 语义忽略）";
                case BotSightResult.BlockedUnknown: return "被无碰撞体物挡（烟雾？）";
                default: return "超出视野距离";
            }
        }
    }
}
