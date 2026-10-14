using System;

namespace Cs16.Module.View
{
    /// <summary>
    /// 原版把「视线方向」变成两个混合权重的那段算术（`player.cpp` 的
    /// <c>StudioPlayerBlend</c> / <c>CalculatePitchBlend</c> / <c>CalculateYawBlend</c>），
    /// 以及本项目把它接到 Animator 2D BlendTree 上用的两个归一化轴。
    ///
    /// <para><b>原版口径（逐字转写，非近似）</b>：</para>
    /// <list type="bullet">
    /// <item>pitch（`pev->blending[1]`）：`range = int(pitch*3)`；
    ///       `range &lt;= -45 → 255`、`range &gt;= 45 → 0`、否则 `int((45 - range) * 255/90)`。
    ///       `pitch` = `pev->angles.x`（GoldSrc 角度，**正 = 向下看**）⇒ 0 = 完全俯视、255 = 完全仰视，
    ///       且 ±15° 之外饱和。</item>
    /// <item>yaw（`pev->blending[0]`）：`fl = fmod(viewYaw - gaityaw, 360)` 规整到 (−180,180]；
    ///       `gaitMovement != 0` 时 `fl &gt; 120 → fl -= 180`、`fl &lt; −120 → fl += 180`；
    ///       然后 `fl = (fl/90)*128 + 127` 夹到 [0,255]；`blending[0] = 255 - fl`。
    ///       ⇒ 0 = 视线在移动方向**左侧 90°**、255 = **右侧 90°**，中间 128 = 正前
    ///       （`flYaw` 正 = 视线偏左 ⇒ 越大越偏右；见 <see cref="YawBlend255"/> 末行的取反）。</item>
    /// </list>
    ///
    /// <para><b>网格轴（本项目口径）</b>：9 路 blend 的索引是 `b = pitchIdx*3 + yawIdx`
    /// （pitchIdx 0/1/2 = 上/中/下；yawIdx 0/1/2 = 一侧/中/另一侧）。两个轴都取
    /// "原版 0..255 线性缩放到 [0,1]"：<c>AimPitch = (255 - blending[1]) / 255</c>（0 = 上、1 = 下）、
    /// <c>AimYaw = blending[0] / 255</c>（0 = 一侧、1 = 另一侧）。三格心因此落在 0 / 0.5 / 1。</para>
    ///
    /// <para><b>⚠️ 模型假设（不是取证）</b>：把 0..255 量化到 3 格的确切公式在原版客户端渲染器
    /// `hw.dll` 里，本机只有 dll、没有反汇编源码 ⇒ 判不了。本类采用的是**线性同域**模型：
    /// 格心 = 0/0.5/1，格内交给 Animator 的 2D 自由笛卡尔 BlendTree 插值（在 3×3 规则格上等价双线性）。
    /// 原版 9 路姿势本身的重心（实测 3 行 pitch ≈ +15° / 0 / −15°，列间隔 ≈ 90°）与这个模型自洽，
    /// 但"原版是否也做格内插值、权重是否线性"**没有在盘依据**。</para>
    /// </summary>
    public static class CsAimBlend
    {
        /// <summary>Animator 上的两个浮点参数名（生成器建 BlendTree、运行期喂值都用它）。</summary>
        public const string ParamYaw = "AimYaw";

        /// <summary>见 <see cref="ParamYaw"/>。</summary>
        public const string ParamPitch = "AimPitch";

        /// <summary>每轴的格数（9 路 = 3×3）。</summary>
        public const int GridN = 3;

        /// <summary>原版 <c>StudioPlayerBlend</c>：视线 pitch（度，正 = 向下）→ 0..255。</summary>
        public static int PitchBlend255(float viewPitchDeg)
        {
            var range = (int)Math.Truncate(viewPitchDeg * 3f);
            if (range <= -45) return 255;
            if (range >= 45) return 0;
            return (int)Math.Truncate((45f - range) * (255f / 90f));
        }

        /// <summary>原版 <c>CalculateYawBlend</c>：视线相对移动方向的偏角 → 0..255。</summary>
        public static int YawBlend255(float viewYawDeg, float gaitYawDeg, bool gaitMoving)
        {
            var flYaw = viewYawDeg - gaitYawDeg;
            // Q_fmod = C 的 fmod（结果与**被除数同号**，向零截断）。依据：同一段源码在紧接着的行里
            // 把等价式写了出来 —— `player.cpp:9030` `flYawDiff = flYawDiff - int64(flYawDiff/360)*360;`，
            // 只有截断能让两行相等（floor 会把每个负差都 +360，使 −180 落到 +180、下面的分支永死）。
            flYaw -= (float)Math.Truncate(flYaw / 360f) * 360f;
            // 两个分支都是**严格**不等号（源码 `flYaw < -180` / `flYaw > 180`）：
            // 恰好 −180 时要留在 −180（映射到偏航轴的一端），不能折成 +180。
            if (flYaw < -180f) flYaw += 360f;
            else if (flYaw > 180f) flYaw -= 360f;

            if (gaitMoving)
            {
                if (flYaw > 120f) flYaw -= 180f;
                else if (flYaw < -120f) flYaw += 180f;
            }

            flYaw = (flYaw / 90f) * 128f + 127f;
            if (flYaw > 255f) flYaw = 255f;
            else if (flYaw < 0f) flYaw = 0f;

            return (int)Math.Truncate(255f - flYaw);
        }

        /// <summary>BlendTree 的横轴值 ∈ [0,1]（0 = yawIdx 0，1 = yawIdx 2）。</summary>
        public static float YawAxis(float viewYawDeg, float gaitYawDeg, bool gaitMoving)
        {
            return YawBlend255(viewYawDeg, gaitYawDeg, gaitMoving) / 255f;
        }

        /// <summary>BlendTree 的纵轴值 ∈ [0,1]（0 = 上/pitchIdx 0，1 = 下/pitchIdx 2）。</summary>
        public static float PitchAxis(float viewPitchDeg)
        {
            return (255f - PitchBlend255(viewPitchDeg)) / 255f;
        }

        /// <summary>该轴值落在第几格（0..GridN-1）—— 用于日志与判据，不参与混合。</summary>
        public static int AxisCell(float axis01)
        {
            var a = axis01 < 0f ? 0f : (axis01 > 1f ? 1f : axis01);
            var i = (int)Math.Floor(a * (GridN - 1) + 0.5f);      // 四舍五入（远离零），与 Mathf.RoundToInt 同口径
            return i < 0 ? 0 : (i > GridN - 1 ? GridN - 1 : i);
        }

        /// <summary>该轴值在 BlendTree 里的格心横/纵坐标（格心 = 0 / 0.5 / 1）。</summary>
        public static float CellCentre(int cell)
        {
            var c = cell < 0 ? 0 : (cell > GridN - 1 ? GridN - 1 : cell);
            return c / (float)(GridN - 1);
        }
    }
}
