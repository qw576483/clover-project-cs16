using Cs16.Core;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人的**战术分工（角色）** —— 本项目新增。
    ///
    /// <para><b>为什么要它</b>：用户 2026-09-24 复查原话 ——「机器人 ai 没有分工吗？感觉行为方式都是一样的。
    /// 取参数，**同一队里的每个人共用同一套参数**，差别只剩"路线槽位 idx"（<c>CsBotBrain</c> 的
    /// <c>self.Id % 4</c>）—— 观感就是"同一个人被复制了 4 份"。本类补的是**同一队内部的分工**：
    /// 同一个难度档下，不同角色在"在一个目标点待多久""多远开火""守点还是游走"上**互不相同**。</para>
    ///
    /// <para><b>出处口径（必须如实标注）</b>：<b>原版 CS 1.6 本体不含机器人 AI</b>（官方 bot 属
    /// Condition Zero / PodBot，不在本工程载体范围内，同 <see cref="CsBotConst"/> 的说明）⇒ 角色划分与
    /// 四个倍率在 A 里**没有对应量**，属**本项目新增**数值，按 skill 的降级链第 4 级（参考坐标可自定）
    /// 登记在 <c>策划/差异登记.tsv</c> #67。不许把它写成"原版就这样"。</para>
    ///
    /// <para><b>为什么用"槽位"而不是随机</b>：角色由 <c>(阵营, 队内序号 % 4)</c> 唯一决定 —— 同一局、同一台机器、
    /// 重开一局都得到同一张分工表（可复现、可断言）；随机分配会让"这局谁突破"变成不可复现的噪声，
    /// 判据也就写不出来。</para>
    ///
    /// <para><b>修正</b>：本表原先按"全局 <c>self.Id % 4</c>"分工，
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
        /// <para>形参语义 = **队内序号 / 计划槽位**（0..3，周期 4），<b>不是</b>全局 actor Id ——
        /// 全局 Id 会被"真人玩家在哪一队"整体位移（对 T 队恰好安全、对 CT 队会撞车）。</para>
        /// <para>形参用 <c>long</c>：调用方可能直接传 Id（如探针按序号枚举），这里不许先 <c>(int)</c> 截断。</para>
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
                    // 观战 / 未定（不该有 bot）—— 收敛到最保守的"支援"，不抛异常、不影响回合推进。
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

        // ==================================================================
        //  武器偏好 / 投掷物偏好（每队 4 个角色各不相同）
        // ==================================================================
        //
        //  出处口径（逐段标清）：
        //
        //  ① **原版确实有"武器偏好"这件事**：原版 mp.dll 的 bot 侧自带属性表
        //     `Aggression / Skill / Skin / Teamwork / Cost / VoicePitch / VoiceBank / WeaponPreference /
        //      ReactionTime / AttackDelay / Difficulty / Team`（`原版资源/cs16src/cstrike/dlls/mp.dll`，
        //      字符串区 0x121E94–0x121F20），难度档取值 `EASY / NORMAL / HARD / EXPERT`（0x121BC0），
        //      并有一条 `Tried to buy preferred weapon %s.`（0x124A58）—— 即"机器人按偏好买枪"是原版行为。
        //  ② **原版的候选武器池**（同一 mp.dll）：CT 0x124938 起 `xm1014 ump45 famas scout m4a1 sg550 m249
        //     p228 deagle`；T 0x1249A0 起 `xm1014 mac10 ump45 galil ak47 scout sg552 g3sg1 m249 p228
        //     deagle elites`；投掷物/装备购买序列 0x124AB0 `primammo vesthelm vest secammo
        //     sgren flash hegren … defuser`（`sgren` = 烟雾、`flash` = 闪光、`hegren` = 高爆）。
        //  ③ **本表（角色 → 顺序）与"档位价格上限"属本项目新增**：原版偏好的数值语义写在数据文件
        //     `botprofile.db` 里，该文件不在盘（`原版资源/` 实测无 bot 相关文件），无法读出
        //     "哪个数值 = 哪把枪"⇒ 只借原版的池与"按偏好买枪"这一行为，不谎称顺序也是原版。
        //     登记见 `策划/差异登记.tsv` #68。

        /// <summary>突破手的偏好主武器（近距：冲锋枪 / 霰弹优先，再退到步枪）。</summary>
        private static readonly string[] PreferBreakerT = { CsWeapons.Mac10, CsWeapons.Ump45, CsWeapons.Xm1014, CsWeapons.Galil, CsWeapons.Ak47 };
        /// <summary>突破手（CT 侧）。</summary>
        private static readonly string[] PreferBreakerCT = { CsWeapons.Ump45, CsWeapons.Xm1014, CsWeapons.Famas, CsWeapons.M4A1 };

        /// <summary>支援的偏好主武器（中距：步枪优先，再退到冲锋枪）。</summary>
        private static readonly string[] PreferSupportT = { CsWeapons.Galil, CsWeapons.Ak47, CsWeapons.Sg552, CsWeapons.Ump45 };
        /// <summary>支援（CT 侧）。</summary>
        private static readonly string[] PreferSupportCT = { CsWeapons.Famas, CsWeapons.M4A1, CsWeapons.Aug, CsWeapons.Ump45 };

        /// <summary>侦察（游走 / 拉远）的偏好主武器：狙击优先。角色已有的 <see cref="RangeScale"/> 就是"最远交火"。</summary>
        private static readonly string[] PreferScoutT = { CsWeapons.Scout, CsWeapons.G3sg1, CsWeapons.Sg552, CsWeapons.Awp };
        /// <summary>侦察（CT 侧）。</summary>
        private static readonly string[] PreferScoutCT = { CsWeapons.Scout, CsWeapons.Sg550, CsWeapons.Aug, CsWeapons.Awp };

        /// <summary>守点的偏好主武器：守点位上"一次交火定生死"，步枪 / 连狙优先。</summary>
        private static readonly string[] PreferAnchorT = { CsWeapons.Ak47, CsWeapons.Galil, CsWeapons.G3sg1, CsWeapons.Awp };
        /// <summary>守点（CT 侧）。</summary>
        private static readonly string[] PreferAnchorCT = { CsWeapons.M4A1, CsWeapons.Famas, CsWeapons.Sg550, CsWeapons.Awp };

        /// <summary>
        /// 本角色偏好的主武器（**按优先级**；调用方取"档位上限内、且买得起"的第一把）。
        /// <para>返回的是只读共享数组（本类内静态持有），调用方**不许改内容**。</para>
        /// </summary>
        public static string[] PreferredPrimary(CsBotRole role, CsTeam team)
        {
            var t = team == CsTeam.T;
            switch (role)
            {
                case CsBotRole.Breaker: return t ? PreferBreakerT : PreferBreakerCT;
                case CsBotRole.Scout: return t ? PreferScoutT : PreferScoutCT;
                case CsBotRole.Anchor: return t ? PreferAnchorT : PreferAnchorCT;
                default: return t ? PreferSupportT : PreferSupportCT;
            }
        }

        /// <summary>
        /// 主武器偏好的**档位价格上限**（元）：便宜档只允许冲锋枪/霰弹这一档，
        /// 中档放开到 Scout（原版三档里"Normal 会买中档枪"），贵档不设限（Hard 会买最好枪）。
        /// <para>出处：**本项目新增**（上限的**分档**是我方口径；被限的池 = 原版 mp.dll 的候选武器表，
        /// 见本段开头 ②）。价格取 <c>Core/CsWeapons.cs</c> 的原版价。</para>
        /// </summary>
        public static float PrimaryPriceCap(int tier)
        {
            switch (tier)
            {
                case 0: return 1500f;                    // 上限 = UMP-45（1700）之下 ⇒ 只剩冲锋枪档
                case 1: return 2750f;                    // 放开到 Scout（2750）
                default: return float.MaxValue;          // 贵档不设限
            }
        }

        /// <summary>突破手的投掷物偏好：先高爆（冲点前炸开），再闪光。</summary>
        private static readonly string[] NadesBreaker = { CsWeapons.HeGrenade, CsWeapons.Flashbang };
        /// <summary>支援的投掷物偏好：高爆 + 烟（封住对面视线给突破手让路）。</summary>
        private static readonly string[] NadesSupport = { CsWeapons.HeGrenade, CsWeapons.SmokeGrenade };
        /// <summary>侦察的投掷物偏好：闪光先行（拉远交火前的致盲），再烟。</summary>
        private static readonly string[] NadesScout = { CsWeapons.Flashbang, CsWeapons.SmokeGrenade };
        /// <summary>守点的投掷物偏好：烟（遮住守位视野口），再高爆。</summary>
        private static readonly string[] NadesAnchor = { CsWeapons.SmokeGrenade, CsWeapons.HeGrenade };

        /// <summary>
        /// 本角色偏好的投掷物（按优先级）。三种各有限额，见 <c>CsMatchConst.MaxHeGrenades/MaxFlashbangs/MaxSmokes</c>。
        /// <para>出处：**这三种都在原版的 bot 购买序列里**（mp.dll 0x124AB0 的 `sgren/flash/hegren`）；
        /// **"哪个角色先拿哪一种"本项目新增**（原版把偏好写在不在盘的 `botprofile.db`）。</para>
        /// </summary>
        public static string[] PreferredGrenades(CsBotRole role)
        {
            switch (role)
            {
                case CsBotRole.Breaker: return NadesBreaker;
                case CsBotRole.Scout: return NadesScout;
                case CsBotRole.Anchor: return NadesAnchor;
                default: return NadesSupport;
            }
        }

        /// <summary>投掷物偏好的中文摘要（写进买枪决策日志，便于人眼核对分工）。</summary>
        public static string GrenadeText(CsBotRole role)
        {
            var ids = PreferredGrenades(role);
            var sb = new System.Text.StringBuilder(32);
            for (var i = 0; i < ids.Length; i++)
            {
                if (i > 0) sb.Append(" → ");
                var def = CsWeapons.Get(ids[i]);
                sb.Append(def != null ? def.DisplayName : ids[i]);
            }
            return sb.ToString();
        }
    }
}
