using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 左上角雷达（规格 H6 / G14，策划案里叫 <c>RadarWidget</c>）。
    ///
    /// <para>
    /// 数据源是 <see cref="CsHudSnapshot.Radar"/>（agent-03 每帧重填：自己 / 队友 / **视线可见**的敌人 /
    /// 炸弹 / 两个包点）。本件只做坐标映射与画点，不参与"谁该被看见"的判断 —— 那是比赛模拟的权威结论。
    /// </para>
    ///
    /// <para>
    /// 世界范围（min/max X、Z）由 <see cref="HudPanel"/> 从引擎的 <c>Game.Map</c>
    /// （<c>IMapData.Origin / Width / Depth / CellSize</c>，**引擎 API，不是业务 Module**）算好传进来；
    /// 地图没加载时退化为"以原点为中心的固定视野"，并且只报一次警告（不静默、不画错）。
    /// </para>
    ///
    /// <para>
    /// 点的颜色：自己 = 白，队友 = 阵营色（CT 蓝 / T 橙），可见敌人 = 同款阵营色但更小 ——
    /// <b>为什么不做"红=敌人"</b>：<see cref="CsRadarDot"/> 只有 Team 字段、没有 IsEnemy，
    /// 按"不是我这队就画红"来涂会把队友误判成敌人（观察者视角下全是"敌人"）。
    /// 宁可只表示阵营，也不编一个快照里不存在的事实。
    /// 炸弹 = 红色闪烁方块，包点 = 黄色方块。雷达不随视角旋转（CS 1.6 默认即正北朝上）。
    /// </para>
    /// </summary>
    [System.Serializable]
    public sealed class CsRadarWidget
    {
        public const float SelfDot = 10f;
        public const float MateDot = 7f;
        public const float SpecDot = 5f;
        public const float BombDot = 11f;
        public const float SiteDot = 12f;

        /// <summary>地图范围未知时的兜底半宽（米）—— de_dust2 规格约 64m×64m。</summary>
        public const float FallbackHalfExtent = 32f;

        /// <summary>炸弹点闪烁周期（秒）。</summary>
        public const float BombBlinkPeriod = 0.6f;

        public RectTransform Root;
        public RectTransform DotLayer;

        [System.NonSerialized] private readonly List<Image> _dots = new List<Image>(48);
        [System.NonSerialized] private bool _warned;
        [System.NonSerialized] private bool _fallbackWarned;
        [System.NonSerialized] private float _blinkTimer;

        public void Build(RectTransform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Error("UI", "CsRadarWidget.Build 收到 null 父节点，雷达不会显示");
                return;
            }

            var frame = UIFactory.CreatePanel("Radar", parent, CsHudTheme.PanelBgSoft, false);
            CsHudTheme.PlaceTopLeft(frame.rectTransform, new Vector2(24f, -14f),
                new Vector2(CsHudTheme.RadarSize, CsHudTheme.RadarSize));
            Root = frame.rectTransform;

            // 内部再收一层：留出边框，画点区域比外框小一圈
            var inner = UIFactory.CreatePanel("RadarField", Root, new Color(0.08f, 0.10f, 0.08f, 0.55f), false);
            UIFactory.Stretch(inner.rectTransform);
            inner.rectTransform.offsetMin = new Vector2(3f, 3f);
            inner.rectTransform.offsetMax = new Vector2(-3f, -3f);

            var layer = UIFactory.CreateNode("RadarDots", inner.rectTransform);
            UIFactory.Stretch(layer);
            DotLayer = layer;

            // **这里不放说明文字**：原来那行 "阵营色点·黄=包点 红=炸弹" 被摆在雷达 Root 里、
            // 坐标 (4,-2)、字号 14 —— 正好压在雷达外框的上边线上，而且 14 个汉字在 200px 宽的
            // 雷达里会把右端挤出面板，实测截图上就是"文字互相重叠 + 显示不全"。
            // CS 1.6 原版雷达本来就没有这行说明（只有左上角一个雷达框 + 点），
            // 所以按 1:1 复刻直接去掉，而不是把文案缩短/挪位置。
            // （点的颜色含义见本类文档注释：自己=白、队友=阵营色、包点=黄、炸弹=红闪。）
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="visible">比赛没跑 / 不在局内时整体隐藏。</param>
        /// <param name="minX">地图世界 X 最小值。</param>
        /// <param name="maxX">地图世界 X 最大值。</param>
        /// <param name="minZ">地图世界 Z 最小值。</param>
        /// <param name="maxZ">地图世界 Z 最大值。</param>
        /// <param name="dots"><see cref="CsHudSnapshot.Radar"/>。</param>
        public void Refresh(bool visible, float minX, float maxX, float minZ, float maxZ, List<CsRadarDot> dots)
        {
            if (Root == null || DotLayer == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Game.Logger?.Error("UI", "CsRadarWidget 引用缺失（预制体未由 UiBuilder 生成或已被改动），雷达不会显示");
                }
                return;
            }

            if (Root.gameObject.activeSelf != visible) Root.gameObject.SetActive(visible);
            if (!visible) return;

            if (dots == null)
            {
                Game.Logger?.Warn("UI", "CsRadarWidget 收到 null 的点列表（快照被清空？），本帧不画点");
                return;
            }

            var field = DotLayer.rect;
            var w = maxX - minX;
            var h = maxZ - minZ;

            if (w <= 1.01f || h <= 1.01f)
            {
                // 地图数据没加载时 IMapData 的宽深是 0 → 退化为固定视野，并且**只报一次**（否则每帧刷屏）
                if (!_fallbackWarned)
                {
                    _fallbackWarned = true;
                    Game.Logger?.Warn("UI",
                        $"雷达拿不到地图范围（minX={minX:0.##} maxX={maxX:0.##} minZ={minZ:0.##} maxZ={maxZ:0.##}），" +
                        $"退化为以原点为中心的 {FallbackHalfExtent * 2f:0}m 视野（Game.Map 未加载？）");
                }
                minX = -FallbackHalfExtent; maxX = FallbackHalfExtent;
                minZ = -FallbackHalfExtent; maxZ = FallbackHalfExtent;
                w = maxX - minX;
                h = maxZ - minZ;
            }

            // 等比缩放：非等比会把 dust2 拉变形，两点间的距离关系就不成立了
            var scale = Mathf.Min(field.width / w, field.height / h);
            var cx = (minX + maxX) * 0.5f;
            var cz = (minZ + maxZ) * 0.5f;

            _blinkTimer += Time.deltaTime;

            EnsureDots(dots.Count);

            for (var i = 0; i < _dots.Count; i++)
            {
                var img = _dots[i];
                if (img == null) continue;

                if (i >= dots.Count)
                {
                    if (img.gameObject.activeSelf) img.gameObject.SetActive(false);
                    continue;
                }

                var dot = dots[i];
                var size = SizeOf(dot, out var color);

                // 炸弹是闪的：静止不闪会被误认成包点
                if (dot.IsBomb)
                {
                    var on = Mathf.Repeat(_blinkTimer, BombBlinkPeriod) < BombBlinkPeriod * 0.5f;
                    color.a = on ? 1f : 0.3f;
                }

                if (!img.gameObject.activeSelf) img.gameObject.SetActive(true);
                img.color = color;
                img.rectTransform.sizeDelta = new Vector2(size, size);
                img.rectTransform.anchoredPosition = new Vector2(
                    (dot.X - cx) * scale,
                    (dot.Z - cz) * scale);
            }
        }

        private static float SizeOf(in CsRadarDot dot, out Color color)
        {
            if (dot.IsBombsite)
            {
                color = new Color32(0xFF, 0xD2, 0x40, 0xFF);
                return SiteDot;
            }
            if (dot.IsBomb)
            {
                color = new Color32(0xFF, 0x38, 0x28, 0xFF);
                return BombDot;
            }
            if (dot.IsSelf)
            {
                color = Color.white;
                return SelfDot;
            }
            if (dot.Team == CsTeam.Spectator)
            {
                color = CsHudTheme.TeamSpec;
                return SpecDot;
            }

            color = CsHudTheme.TeamColor(dot.Team);
            return MateDot;
        }

        private void EnsureDots(int count)
        {
            while (_dots.Count < count)
            {
                var img = UIFactory.CreatePanel($"Dot{_dots.Count}", DotLayer, Color.white, false);
                CsHudTheme.PlaceCenter(img.rectTransform, Vector2.zero, new Vector2(MateDot, MateDot));
                _dots.Add(img);
            }
        }
    }
}
