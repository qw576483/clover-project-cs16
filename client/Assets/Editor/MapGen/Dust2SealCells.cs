using UnityEngine;

namespace Cs16.EditorTools
{
    /// <summary>
    /// 烘焙前把「位图说可走、格心向下探不到任何真几何」的格**封为阻挡**（写进场景的
    /// <c>Level/Blockers</c>，引擎烘焙器只按它逐格复算位图）。
    ///
    /// <para><b>为什么需要</b>：可走位图是**单层 2D**（一格一位，没有高度），它只回答
    /// "这一格的格柱与障碍 AABB 相不相交"，不回答"这一格脚下有没有地面"。于是存在这样一类格：
    /// 位图判可走，但格心向下打射线**打不到任何世界几何**（该格是真几何的虚空/缝隙）。
    /// 运行时 <c>CsMap.TrySampleGround</c> 在这里探不到地面 ⇒ 贴地判定落到
    /// <c>CsMatch</c> 的软地板分支（贴到最后一次已知地面高度）⇒ 观感是**钻进地面**。</para>
    ///
    /// <para><b>表里的格逐格登记，不按规则自动封</b>：全图同类格共 106 个（离线量法
    /// <c>tools/probes/g39-map-regression.py</c> 的 R5 段），其中一部分是**承重通路** ——
    /// 封掉会把可走连通分量切开（离线实测：不设限制地全封会让主分量从 4369 格掉到 4347 格）。
    /// 所以只收已定案的那几格；要加格必须先跑量法确认"封掉不切开任何可走分量"。</para>
    ///
    /// <para><b>格坐标口径</b>：与位图同一口径 ——
    /// <c>ix = floor((x - OriginX) / CellSize)</c>，格心 = <see cref="Dust2GeoData.CellCenter"/>。</para>
    /// </summary>
    public static class Dust2SealCells
    {
        /// <summary>要封的格（格坐标，闭区间单格）。</summary>
        public static readonly (int Ix, int Iz)[] Cells =
        {
            (27, 63),
            (9, 102),
            (10, 102),
            (10, 109),
        };

        /// <summary>把封格写进一张"哪一格被阻挡"的位图（与 <see cref="Dust2GeoData.BuildBlockedBitmap"/> 输出同形）。</summary>
        public static void Apply(bool[] blocked, int width, int depth)
        {
            if (blocked == null || blocked.Length != width * depth)
            {
                Debug.LogError($"[Dust2SealCells] 位图尺寸不符（{blocked?.Length ?? -1} ≠ {width}x{depth}）⇒ 封格没有生效");
                return;
            }
            for (var i = 0; i < Cells.Length; i++)
            {
                var (ix, iz) = Cells[i];
                if (ix < 0 || iz < 0 || ix >= width || iz >= depth)
                {
                    Debug.LogError($"[Dust2SealCells] 封格 ({ix},{iz}) 出图外（{width}x{depth}）⇒ 跳过");
                    continue;
                }
                blocked[iz * width + ix] = true;
            }
        }

        /// <summary>
        /// 把封格转成阻挡盒（单格、Y 覆盖整个探测带），追加在 <paramref name="geo"/> 自带的阻挡盒之后。
        /// 单格盒与格边界对齐 ⇒ 只会命中它自己那一格（相邻格心到盒面的距离 &gt; 半格）。
        /// </summary>
        public static Dust2GeoData.Blocker[] AppendedTo(Dust2GeoData geo)
        {
            var src = geo.Blockers ?? new Dust2GeoData.Blocker[0];
            var all = new Dust2GeoData.Blocker[src.Length + Cells.Length];
            for (var i = 0; i < src.Length; i++) all[i] = src[i];
            for (var i = 0; i < Cells.Length; i++)
            {
                var (ix, iz) = Cells[i];
                all[src.Length + i] = new Dust2GeoData.Blocker
                {
                    Ix0 = ix, Iz0 = iz, Ix1 = ix, Iz1 = iz,
                    YMin = geo.ProbeBottomY,
                    YMax = geo.ProbeTopY,
                };
            }
            return all;
        }
    }
}
