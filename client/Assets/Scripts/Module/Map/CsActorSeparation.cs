using System;

namespace Cs16.Module.Map
{
    /// <summary>
    /// **角色间水平推开**（用户报「人物和人物能重合」的修复；原版行为出处见下）。
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
    /// 本工程已有的 <c>CsConst.PlayerRadius</c>（0.36 m，见那里的出处注释）就是同一件事的水平半径口径
    /// ⇒ 推开判据直接用它：两个角色的水平间距必须 ≥ <c>2 × PlayerRadius</c>。</para>
    ///
    /// <para><b>为什么是"纯数学、不依赖 UnityEngine"</b>：这段判定要能被**独立宿主/自检**
    /// 直接调用并断言（"两个 actor 同格 ⇒ 被推到不重合"），不该为了跑一次断言就起 Play；
    /// 与引擎 <c>MapFormat.cs</c>「可被直链进裸 .NET 程序做跨端校验」是同一个理由。
    /// 调用方（<c>CsMatch.StepActorPhysics</c>）负责把结果再喂回
    /// <see cref="CsMap.ResolveMove"/> 钳一次 —— 推开**不许**把人推进墙里。</para>
    /// </summary>
    public static class CsActorSeparation
    {
        /// <summary>参与推开的另一个角色的**水平**位置（竖直分量由调用方按
        /// <c>CsConst.StandHeight</c> 先筛过：不在同一层的不该互相推）。</summary>
        public struct ActorCircle
        {
            /// <summary>角色 id：完全重合（没有方向可言）时用它算一个**确定**的散开方向。</summary>
            public long Id;
            public float X;
            public float Z;
        }

        /// <summary>
        /// 迭代轮数上限：每个"需求位移"都是瞬时完成的（推到位），所以正常情况 1 轮就够；
        /// 给 8 轮是为了让被挤在墙角/人堆里的情况也能收敛（不收敛时调用方按"挤住"处理 = 原地不动）。
        /// </summary>
        public const int MaxIterations = 8;

        /// <summary>
        /// 推开后额外留的缝隙（米）：只推到"刚好相切"会让两人在浮点上反复判重叠
        /// ⇒ 每帧各推一点点 ⇒ 位置抖动。留 1 mm 的余量。
        /// </summary>
        public const float Skin = 0.001f;

        /// <summary>
        /// 把申请位置 <paramref name="x"/>/<paramref name="z"/> 推到与
        /// <paramref name="others"/> 里每个圆都不重叠（水平间距 ≥ 2×<paramref name="radius"/>）。
        ///
        /// <para>返回 <c>true</c> = 已推出（或本来就无重叠）；<c>false</c> = 迭代上限内仍有重叠
        /// （几人挤成一堆 / 被墙夹住），调用方应据此留痕并按"挤住"处理。</para>
        /// </summary>
        public static bool TryResolve(float x, float z, float radius,
                                     ActorCircle[] others, int count,
                                     out float outX, out float outZ)
        {
            outX = x;
            outZ = z;
            if (others == null || count <= 0) return true;

            var minSep = radius * 2f;          // 两圆心最小间距 = 两个半径之和
            var minSep2 = minSep * minSep;

            for (var iter = 0; iter < MaxIterations; iter++)
            {
                var overlapped = false;
                for (var i = 0; i < count; i++)
                {
                    var dx = outX - others[i].X;
                    var dz = outZ - others[i].Z;
                    var d2 = dx * dx + dz * dz;
                    if (d2 >= minSep2) continue;

                    overlapped = true;
                    var d = (float)Math.Sqrt(d2);
                    if (d < 1e-5f)
                    {
                        // 完全重合：没有"推开方向"可言。用 id 定一个**确定**的方向
                        // （id × 黄金角），而不是随机/取当前速度 —— 随机每帧都会变（抖动），
                        // 取速度在静止时又是零向量（推不开）。
                        var ang = others[i].Id * 2.399963229728653;   // 黄金角(rad)
                        dx = (float)Math.Cos(ang);
                        dz = (float)Math.Sin(ang);
                        d = 1f;                                        // 已是单位向量
                    }
                    else
                    {
                        dx /= d;
                        dz /= d;
                    }

                    var need = minSep - d + Skin;
                    outX += dx * need;
                    outZ += dz * need;
                }
                if (!overlapped) return true;
            }
            return false;
        }
    }
}
