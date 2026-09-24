using Cs16.Core;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人的**战术分工（角色）** —— 本项目新增。
    ///
    /// <para><b>为什么要它</b>：用户 2026-09-24 复查原话 ——「机器人 ai 没有分工吗？感觉行为方式都是一样的。
    /// 机器人有点太笨了」。旧实现里每个 bot 只按 <see cref="Cs16.Module.Match.CsBotProfile"/>（难度三档）
    /// 取参数，**同一队里的每个人共用同一套参数**，差别只剩"路线槽位 idx"（<c>CsBotBrain</c> 的
    /// <c>self.Id % 4</c>）—— 观感就是"同一个人被复制了 4 份"。本类补的是**同一队内部的分工**：
    /// 同一个难度档下，不同角色在"在一个目标点待多久""多远开火""守点还是游走"上**互不相同**。</para>
    ///
    /// <para><b>出处口径（⛔ 必须如实标注）</b>：<b>原版 CS 1.6 本体不含机器人 AI</b>（官方 bot 属
    /// Condition Zero / PodBot，不在本工程载体范围内，同 <see cref="CsBotConst"/> 的说明）⇒ 角色划分与
    /// 四个倍率在 A 里**没有对应量**，属**本项目新增**数值，按 skill 的降级链第 4 级（参考坐标可自定）
    /// 登记在 <c>策划/差异登记.tsv</c> #67。⛔ 不许把它写成"原版就这样"。</para>
    ///
    /// <para><b>为什么用"槽位"而不是随机</b>：角色由 <c>(阵营, 队内序号 % 4)</c> 唯一决定 —— 同一局、同一台机器、
    /// 重开一局都得到同一张分工表（可复现、可断言）；随机分配会让"这局谁突破"变成不可复现的噪声，
    /// 判据也就写不出来。</para>
    ///
    /// <para><b>⛔ 2026-09-24 修正（用户第三次投诉「路线都是相同的」）</b>：本表原先按"全局 <c>self.Id % 4</c>"分工，
    /// 而全局 Id 取决于"真人玩家在哪一队"，且旧 <c>ChoosePlan</c> 把同一个模数映到**只有 3 条的路线池**上 ⇒
    /// 每队必然有一对 bot 同路同点。现在：</para>
    /// <list type="bullet">
    /// <item>入参 = **队内序号**（<see cref="CsBotPlans.TeamLocalOrdinal"/>，每队恒为连续 0..n-1），不是全局 Id；</item>
    /// <item>槽位 = <see cref="CsBotPlans.SlotOf"/>（模 4 循环平移，持包者恒在槽位 0），
    /// **路线与目标点由 <see cref="CsBotPlans.For"/> 决定**，与本表逐槽位对齐（见下面两个 switch 的注释）。</item>
    /// </list>
    /// </summary>
    public enum CsBotRole
    {
        /// <summary>突破手：往包里冲，到点就换位，交战距离最近。</summary>
        Breaker = 0,

        /// <summary>支援：跟突破手（T）/ 守另一个包点（CT），正常距离，正常守点时长。</summary>
        Support = 1,

        /// <summary>侦察 / 游走：拉远交火、待得久，走中路 / 巡逻线（<see cref="CsBotPlans.For"/> 槽位 2）。</summary>
        Scout = 2,

        /// <summary>守点：**到包点后不再换目标**（原地守到回合结束 / 包被拆）。CT 专属（槽位 0 = 守 A）。</summary>
        Anchor = 3,
    }

    /// <summary>
    /// 角色表（纯函数，无状态 —— 单测与运行时用**同一份**）。
    /// </summary>
    public static class CsBotRoles
    {
        /// <summary>每队的分工槽位数（= <see cref="CsBotPlans.Slots"/>，与路线计划表的模数**同源**）。</summary>
        public const int Slots = CsBotPlans.Slots;

        /// <summary>本方阵营的**第 <paramref name="ordinal"/> 号**机器人担任什么角色（确定性）。
        /// <para>⛔ 形参语义 = **队内序号 / 计划槽位**（0..3，周期 4），<b>不是</b>全局 actor Id ——
        /// 全局 Id 会被"真人玩家在哪一队"整体位移（旧实现对 T 队恰好安全、对 CT 队会撞车）。</para>
        /// <para>形参用 <c>long</c>：调用方可能直接传 Id（如探针按序号枚举），⛔ 这里不许先 <c>(int)</c> 截断。</para>
        /// </summary>
        public static CsBotRole For(CsTeam team, long ordinal)
        {
            var slot = (int)(((ordinal % Slots) + Slots) % Slots);   // ⛔ 不留负索引

            switch (team)
            {
                case CsTeam.T:
                    // 与 CsBotPlans.For(T, slot, round) 逐槽位对齐：
                    //   槽位 0 = 主攻路→本轮主攻包点（突破）  槽位 1 = 中路→同一包点另一点（支援）
                    //   槽位 2 = 巡逻线（侦察）                槽位 3 = 另一条包点路（突破/佯攻）
                    switch (slot)
                    {
                        case 2: return CsBotRole.Scout;
                        case 1: return CsBotRole.Support;
                        default: return CsBotRole.Breaker;
                    }

                case CsTeam.CT:
                    // 与 CsBotPlans.For(CT, slot, round) 逐槽位对齐：
                    //   槽位 0 = 守 A（固守位）  槽位 1 = 守 B（常规守卫）
                    //   槽位 2 = 中路（侦察）    槽位 3 = 巡逻线（轮转支援）
                    switch (slot)
                    {
                        case 1:
                        case 3: return CsBotRole.Support;
                        case 2: return CsBotRole.Scout;
                        default: return CsBotRole.Anchor;
                    }

                default:
                    // 观战 / 未定（不该有 bot）—— 收敛到最保守的"支援"，⛔ 不抛异常、不影响回合推进。
                    return CsBotRole.Support;
            }
        }

        /// <summary>
        /// "在同一个目标点待多久"的**倍率**（乘在 <c>ObjectiveHoldSeconds()</c> 的结果上）。
        /// 这是用户最容易看出差别的量：突破手 1.8s 就走，守点位的待 7.5s（Normal 档 3.0s 基准）。
        /// </summary>
        public static float HoldScale(CsBotRole role)
        {
            switch (role)
            {
                case CsBotRole.Breaker: return 0.6f;
                case CsBotRole.Scout: return 1.6f;
                case CsBotRole.Anchor: return 2.5f;
                default: return 1.0f;                 // Support = 基准
            }
        }

        /// <summary>
        /// 偏好交战距离的**倍率**（乘在 <c>CsBotProfile.PreferredRange</c> 上）。
        /// 突破手贴上去打（0.75×）、侦察位拉开打（1.3×）、守点的略拉远（1.15×，守点位上不主动贴脸）。
        /// </summary>
        public static float RangeScale(CsBotRole role)
        {
            switch (role)
            {
                case CsBotRole.Breaker: return 0.75f;
                case CsBotRole.Scout: return 1.30f;
                case CsBotRole.Anchor: return 1.15f;
                default: return 1.0f;                 // Support = 基准
            }
        }

        /// <summary>
        /// 是否"守到了就**不换目标**"（到包点后一直守）。
        /// 只有守点位如此 —— 这正是用户报的「警不去守点 / 一直在原地踱步」的反面：
        /// 旧实现里所有 bot 守够 <c>ObjectiveHoldSeconds</c> 就 `ReplanObjective`（换路线 / 去巡逻），
        /// 于是 CT 的守卫也每 3 秒改一次目的地，观感就是"全队都在乱走、没人真的守"。
        /// </summary>
        public static bool StaysOnObjective(CsBotRole role)
        {
            return role == CsBotRole.Anchor;
        }

        /// <summary>中文名（写进日志与自检输出，便于人眼核对）。</summary>
        public static string Label(CsBotRole role)
        {
            switch (role)
            {
                case CsBotRole.Breaker: return "突破";
                case CsBotRole.Scout: return "侦察";
                case CsBotRole.Anchor: return "守点";
                default: return "支援";
            }
        }
    }
}
