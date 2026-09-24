using CloverEngine;
using UnityEngine;

namespace Cs16.Module.Map
{
    /// <summary>
    /// 角色之间的**水平推开**（防"两个角色站进同一格"）：本类型是**调用侧封装**，
    /// 解算由引擎类 <see cref="CloverEngine.Separation2D"/> 承担
    /// （出处：<c>clover-client-unity-engine/Runtime/Core/Separation2D.cs:72</c>）。
    /// 本类型只做两件事：① 把本工程的调用形态（扁平 <c>X</c>/<c>Z</c> 的 <see cref="ActorCircle"/> 数组）
    /// 转成引擎形态（<see cref="CloverEngine.Separation2D.Circle"/> 数组 + <c>Vector2</c>）；
    /// ② 把 <see cref="MaxIterations"/> / <see cref="Skin"/> 两个契约量转发给调用方。
    ///
    /// <para><b>为什么需要它</b>：本工程的本地碰撞分两层 —— 水平走 2D 位图
    /// （<see cref="CsMap.ResolveMove"/>）、竖直走真实几何射线（<c>SampleGround</c>）。
    /// 两层都只回答"**世界几何**挡不挡"，**没有**任何"另一个角色挡不挡"的判定：
    /// 角色预制体上只有服务命中检测用的胶囊（被标到 <c>CsPlayer</c>/<c>CsBot</c> 层、
    /// 所有世界射线都 <c>Ignore</c> 掉），没有 <c>Rigidbody</c>/<c>CharacterController</c>
    /// ⇒ 物理引擎根本不参与角色位移解算。结果两个角色可以站在同一处。</para>
    ///
    /// <para><b>原版口径</b>：GoldSrc 里每个玩家都是一个实体，服务器给每个实体一个
    /// 碰撞包围盒（<c>origin ± 16×16×36</c> units，即水平半宽 16 units = 0.4064 m，
    /// 见 HLSDK <c>pm_shared.c</c> 的 <c>player_mins</c>/<c>player_maxs</c> 与
    /// <c>SV_Move</c> 的实体对实体裁剪）⇒ 两个玩家**不能**占同一块水平空间；
    /// 走到一起时是"互相挤住/被对方挡住"，而不是穿过。
    /// 本工程的 <c>CsConst.PlayerRadius</c>（0.36 m，见那里的出处注释）就是同一件事的水平半径口径
    /// ⇒ 推开判据直接用它：两个角色的水平间距必须 ≥ <c>2 × PlayerRadius</c>。</para>
    ///
    /// <para><b>竖直分层由调用方做</b>：本类型不处理分层 —— 不在同一层的角色不该互相推
    /// （站在箱子顶上的人会把箱子**下面**的人推开），调用方（<c>CsMatch.SeparateFromOtherActors</c>）
    /// 先按 <c>CsConst.StandHeight</c> 筛完再传进来。</para>
    ///
    /// <para><b>结果必须再喂回世界碰撞</b>：推开的几何方向只看"另一个角色在哪"，
    /// 完全可能把人往墙里/箱子里推 ⇒ 调用方把结果再喂回 <see cref="CsMap.ResolveMove"/> 钳一次
    /// （<c>CsMatch.cs:2178</c>）："不能重合"与"不能穿墙"两条同时成立，冲突时以世界优先（人被挤住）。</para>
    ///
    /// <para><b>线程</b>：非线程安全 —— 内部有一份进程级共享的转换缓冲（帧路径上零分配）
    /// ⇒ 同一瞬间只允许一个调用者。本工程只有主线程的帧步进调用它。</para>
    /// </summary>
    public static class CsActorSeparation
    {
        /// <summary>参与推开的另一个角色的**水平**位置（竖直分量由调用方先筛过：不在同一层的不该互相推）。</summary>
        public struct ActorCircle
        {
            /// <summary>角色 id（调用点按 <c>CsActor.Id</c> 填）。
            /// 解算**不使用**该字段：与障碍完全重合时的散开方向由引擎按下标取确定值，
            /// 见 <see cref="CloverEngine.Separation2D.TryResolveOne"/> 的确定性契约。</summary>
            public long Id;
            public float X;
            public float Z;
        }

        /// <summary>
        /// 迭代轮数上限，转发自 <see cref="CloverEngine.Separation2D.MaxIterations"/>：
        /// 每个"需求位移"都是瞬时完成的（推到位），正常情况 1~2 轮就够；
        /// 到上限仍有重叠 ⇒ <see cref="TryResolve"/> 返回 <c>false</c>，调用方按"挤住"处理（原地不动）。
        /// </summary>
        public const int MaxIterations = Separation2D.MaxIterations;

        /// <summary>
        /// 推开后额外留的缝隙（米），转发自 <see cref="CloverEngine.Separation2D.Skin"/>：
        /// 只推到"刚好相切"会让两人在浮点上反复判重叠 ⇒ 每帧各推一点点 ⇒ 位置抖动。
        /// </summary>
        public const float Skin = Separation2D.Skin;

        /// <summary>日志 tag（本工程惯例：各能力用类名自报家门）。</summary>
        private const string Tag = "CsActorSeparation";

        /// <summary><see cref="ActorCircle"/> → <see cref="CloverEngine.Separation2D.Circle"/> 的转换缓冲，
        /// 按需扩容、之后复用 ⇒ 帧路径上零分配。见类型注释的线程说明。</summary>
        private static Separation2D.Circle[] _circles;

        /// <summary>
        /// 把申请位置 <paramref name="x"/>/<paramref name="z"/> 推到与 <paramref name="others"/> 里
        /// 每个圆都不重叠（水平间距 ≥ 2×<paramref name="radius"/>）。
        ///
        /// <para>解算走 <see cref="CloverEngine.Separation2D.TryResolveOne"/>
        /// —— 申请方退让全部、障碍不动，与"一次只挪当前这个角色"的调用形态一致。
        /// 每个障碍圆的半径都取 <paramref name="radius"/>
        /// ⇒ 最小圆心距 = <paramref name="radius"/> × 2（引擎侧按"两者半径之和"算）。</para>
        ///
        /// <para><b>返回值</b>：<c>true</c> = 已推出（或本来就无重叠 / <paramref name="others"/> 为空 /
        /// <paramref name="radius"/> ≤ 0）；<c>false</c> = 迭代上限内仍有重叠
        /// （几人挤成一堆 / 被墙夹住），调用方应据此留痕并按"挤住"处理。
        /// 两种情况下 <paramref name="outX"/>/<paramref name="outZ"/> 都是解算到的位置
        /// （<c>false</c> 时是**部分**解开的结果），且**不抛异常**。</para>
        /// </summary>
        /// <param name="x">申请位置的第一个水平轴（本工程 = 世界 x）。</param>
        /// <param name="z">申请位置的第二个水平轴（本工程 = 世界 z）。</param>
        /// <param name="radius">申请方的水平半径（米）；≤ 0 ⇒ 原样返回 <c>true</c>。</param>
        /// <param name="others">障碍圆数组（只读；其竖直分层已由调用方筛过）。</param>
        /// <param name="count">参与推开的元素个数。</param>
        /// <param name="outX">推开后的 x（未推开时等于 <paramref name="x"/>）。</param>
        /// <param name="outZ">推开后的 z（未推开时等于 <paramref name="z"/>）。</param>
        public static bool TryResolve(float x, float z, float radius,
                                     ActorCircle[] others, int count,
                                     out float outX, out float outZ)
        {
            if (others == null || count <= 0)
            {
                outX = x;
                outZ = z;
                return true;
            }

            if (count > others.Length)
            {
                // 非预期分支（调用方 bug）：多出来的项没有坐标可读 —— 留痕，只处理数组里有的部分。
                Game.Logger.Warn(Tag,
                    $"TryResolve: count={count} 超过 others 长度 {others.Length} ⇒ 只处理前 {others.Length} 个；" +
                    "调用方应传入数组长度以内的 count");
                count = others.Length;
            }

            var buf = Scratch(count);
            for (var i = 0; i < count; i++)
                buf[i] = new Separation2D.Circle(new Vector2(others[i].X, others[i].Z), radius);

            var resolved = Separation2D.TryResolveOne(new Vector2(x, z), radius, buf, count, out var p);
            outX = p.x;
            outZ = p.y;
            return resolved;
        }

        /// <summary>取长度 ≥ <paramref name="count"/> 的转换缓冲（不足则扩容后复用）。</summary>
        private static Separation2D.Circle[] Scratch(int count)
        {
            var buf = _circles;
            if (buf == null || buf.Length < count) buf = _circles = new Separation2D.Circle[count];
            return buf;
        }
    }
}
