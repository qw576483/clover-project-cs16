using Cs16.Core;

namespace Cs16.Module.Match
{
    /// <summary>
    /// 出生朝向表：阵营 × 出生点索引 → GoldSrc 出生点实体的 yaw（度）。
    ///
    /// <para><b>出处</b>：CS 1.6 官方 <c>de_dust2.bsp</c>（<c>Assets/ThirdParty/Dust2/de_dust2.bsp</c>）
    /// 的 ENTITY lump —— T 侧 20 个 <c>info_player_deathmatch</c>、CT 侧 20 个 <c>info_player_start</c>，
    /// 每个实体自带 <c>"angles" "&lt;pitch&gt; &lt;yaw&gt; 0"</c>。原版 <c>GetPlayerSpawnSpot</c> 把出生点实体的
    /// angles 原样抄给玩家并把 pitch 清零 ⇒ 出生朝向 = 实体 angles 的 yaw 分量。</para>
    ///
    /// <para><b>换算</b>：GoldSrc 的 yaw 0 = +X、90 = +Y；本工程 Unity 口径 0 = +Z、90 = +X
    /// （<c>CameraMath.AimDirection</c> / <c>FirstPersonCamera</c>），地图坐标 x→x / y→z / z→y 无镜像
    /// ⇒ <c>unityYaw = 90 − goldSrcYaw</c>。</para>
    ///
    /// <para><b>索引口径</b>：索引 = 该出生点实体在 ENTITY lump 里的出现顺序；它与 <c>de_dust2_geo.bin</c>
    /// 的 <c>Spawn_T</c>/<c>Spawn_CT</c> 点序、以及烘焙产物 <c>de_dust2.bytes</c> 的 FlagMarkers 段点序
    /// 逐点相同（三处各 20/20，坐标逐值相等）。</para>
    /// </summary>
    public static class CsSpawnYaw
    {
        /// <summary>每个阵营的出生点个数（= BSP 实体个数 = 点位数组长度）。</summary>
        public const int Count = 20;

        /// <summary><c>info_player_deathmatch</c> 的 yaw 分量，按 BSP 实体顺序。</summary>
        private static readonly float[] GoldSrcYawT =
        {
            159f, 10f, 159f, 170f, 356f,
            0f, 43f, 22f, 170f, 43f,
            165f, 43f, 43f, 43f, 43f,
            140f, 140f, 21f, 140f, 140f,
        };

        /// <summary><c>info_player_start</c> 的 yaw 分量，按 BSP 实体顺序。</summary>
        private static readonly float[] GoldSrcYawCT =
        {
            255f, 255f, 255f, 72f, 255f,
            72f, 72f, 105f, 105f, 305f,
            41f, 333f, 178f, 186f, 200f,
            200f, 255f, 255f, 255f, 255f,
        };

        /// <summary>
        /// 该阵营第 <paramref name="index"/> 个出生点的朝向（Unity yaw，度，0 = +Z、90 = +X）。
        /// 索引越界按表长取模；非 T/CT（观战等）返回 0。
        /// </summary>
        public static float For(CsTeam team, int index)
        {
            float[] table;
            if (team == CsTeam.T) table = GoldSrcYawT;
            else if (team == CsTeam.CT) table = GoldSrcYawCT;
            else return 0f;

            var i = index % table.Length;
            if (i < 0) i += table.Length;

            var yaw = (90f - table[i]) % 360f;
            if (yaw < 0f) yaw += 360f;
            return yaw;
        }
    }
}
