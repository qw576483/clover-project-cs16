using System;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using UnityEngine;

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人**解卡兜底**（本项目新增；原版 CS 1.6 无 bot，故无对应行为 —— 登记在 策划/差异登记.tsv）。
    ///
    /// <para><b>它治什么</b>：单层 2D 位图把**箱体足迹**标成可走（差异 G-07 的下游形态）⇒ 机器人走进箱内 y=0；
    /// 箱内是空腔、箱底没有面 ⇒ 身高带判据（<see cref="CsMap.BodyHeightClearForTest"/> 背后那条
    /// <c>BodyHeightClearAt</c>："向下射线打不到东西 ⇒ 判挡"）持续为 false ⇒ <c>CsMap.ResolveMoveCore</c>
    /// 的两条放行路（<c>CanStand/TryStepUp</c> 那条 + 要求身高带干净的"几何放行"那条）都不通
    /// ⇒ 每帧位移 0 ⇒ 速度被清 0，而脑侧持续要求移动 ⇒ **永久零速度**。</para>
    ///
    /// <para><b>边界</b>：只处理 **bot**；只在"身高带持续判挡 ≥ <see cref="TriggerSeconds"/>"时动手；
    /// 只挪到**最近的可站且身高带干净**的点；每次留痕 <c>bot.unstuck.teleport</c>。
    /// 不做多层位图（那是差异 G-07 的根因，跨引擎），也不改"打不到 ⇒ 判挡"这条全局放行语义。</para>
    /// </summary>
    public static class BotUnstuck
    {
        /// <summary>身高带持续判挡达到这么久才触发（秒；调用方传 <c>CsClock.Now</c>）。</summary>
        public const float TriggerSeconds = 3f;

        /// <summary>最近可站点的搜索半径（米）与环间步长（米）。</summary>
        public const float SearchRadius = 3f;
        private const float RingStep = 0.5f;

        /// <summary>每环的方位采样数。</summary>
        private const int RingSamples = 16;

        /// <summary>身高带是否被判挡（= <c>CsMap.BodyHeightClear</c> 为 false）。地图不是 <see cref="CsMap"/> ⇒ false（本兜底不生效）。</summary>
        public static bool IsBodyBlocked(ICsMap map, Vector3 pos)
        {
            var cm = map as CsMap;
            if (cm == null) return false;
            try { return !cm.BodyHeightClearForTest(pos, CsConst.PlayerRadius, out _); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 最近的可站点：从 <paramref name="from"/> 起按环（半径 <see cref="RingStep"/> 递增到
        /// <see cref="SearchRadius"/>、每环 <see cref="RingSamples"/> 个方位）找**站得住且身高带干净**的点，
        /// 取最先命中的那一环里离得最近的一个；高度吸附到该点脚下地面，且落差超过一个
        /// <see cref="CsConst.StepUpHeight"/> 的候选直接丢弃（不许挪到别的楼层）。找不到 ⇒ false。
        /// </summary>
        public static bool TryFindStandable(ICsMap map, Vector3 from, out Vector3 target)
        {
            target = from;
            if (map == null || !map.IsLoaded) return false;

            var rings = Mathf.Max(1, Mathf.CeilToInt(SearchRadius / RingStep));
            for (var r = 1; r <= rings; r++)
            {
                var radius = r * RingStep;
                var best = float.MaxValue;
                var found = false;

                for (var i = 0; i < RingSamples; i++)
                {
                    var ang = i * (Mathf.PI * 2f / RingSamples);
                    var probe = new Vector3(from.x + Mathf.Cos(ang) * radius, from.y, from.z + Mathf.Sin(ang) * radius);
                    if (!map.TrySampleGround(probe, out var ground, out _, CsConst.GroundCheckDistance + 8f)) continue;

                    var cand = new Vector3(probe.x, ground.y, probe.z);
                    if (Mathf.Abs(cand.y - from.y) > CsConst.StepUpHeight) continue;
                    if (!map.CanStand(cand)) continue;
                    if (IsBodyBlocked(map, cand)) continue;

                    var d = (cand - from).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    target = cand;
                    found = true;
                }

                if (found) return true;
            }

            return false;
        }
    }
}
