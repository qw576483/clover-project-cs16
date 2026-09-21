using System.Collections.Generic;
using System.Globalization;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 左上角雷达（规格 H6 / G14，策划案里叫 <c>RadarWidget</c>）。
    ///
    /// <para><b>本片（cs16-收尾C）按用户"小地图雷达都不对"重做，四件事</b>：</para>
    /// <list type="number">
    /// <item><b>加上地图底图</b>：原版雷达不是"一个空框 + 几个点"，而是把地图**俯视图**半透明压在
    /// 画面上。底图 = <see cref="ResPaths.RadarOverviewDust2"/>（原版 <c>overviews/de_dust2.bmp</c>
    /// 本机不在盘 ⇒ 按载体降级链退到级①原始数据，由 <c>tools/probes/render-overview.py</c>
    /// 把工程内的原版地图几何 <c>de_dust2_geo.bin</c> 俯视栅格化得到，含 A/B 点字母）。</item>
    /// <item><b>尺寸落 1:1 真值</b>：<see cref="CsHudTheme.RadarSize"/> 由 200 → <b>128</b>
    /// （原版 <c>hud.txt:183</c> <c>radar 640 radar640 0 0 128 128</c> 的原生像素；
    /// 640 档精灵在 1080p 上 1:1 不缩放，判据见该常量的注释）。</item>
    /// <item><b>点色按原版判据</b>：自己 = 绿（<see cref="CsHudTheme.RadarSelf"/>）、同队 = 黄
    /// （<see cref="CsHudTheme.RadarMate"/>）、异队（雷达可见的敌人）= 蓝
    /// （<see cref="CsHudTheme.RadarOther"/>）、已安放炸弹 = 红闪
    /// （<see cref="CsHudTheme.RadarBomb"/>）。上一版把"自己"画成白的、队友画成阵营色，
    /// 与判据不符，本片改掉。</item>
    /// <item><b>朝向</b>：正北朝上、**不随视角旋转**（CS 1.6 默认即如此）；世界 → 雷达的映射
    /// 与底图生成器**同一套**（等比、以地图包围盒中心为中心）⇒ 点必然落在底图对应位置上。</item>
    /// </list>
    ///
    /// <para><b>数据源</b>是 <see cref="CsHudSnapshot.Radar"/>（比赛模拟每帧重填：自己 / 队友 /
    /// **视线可见**的敌人 / 炸弹）。本件只做坐标映射与画点 —— "谁该被看见"是比赛模拟的权威结论，
    /// UI 不重复判断。</para>
    ///
    /// <para><b>包点**不再画点**</b>：A/B 两处已作为字母烧进底图（原版那张 <c>.bmp</c> 也是把
    /// A/B 画在图上的），再叠两个方块会互相压住。快照里的 <c>IsBombsite</c> 条目仍然存在
    /// （G14 的数值验收看的是快照），只是本件不画。</para>
    ///
    /// <para><b>世界范围</b>由 <see cref="HudPanel"/> 从引擎的 <c>Game.Map</c>
    /// （<c>IMapData.Origin / Width / Depth / CellSize</c>，引擎 API）算好传进来；地图没加载时退化为
    /// "以原点为中心的固定视野"，并且只报一次警告（不静默、不画错）。</para>
    /// </summary>
    [System.Serializable]
    public sealed class CsRadarWidget
    {
        /// <summary>地图范围未知时的兜底半宽（米）—— de_dust2 规格约 64m×64m。</summary>
        public const float FallbackHalfExtent = 32f;

        /// <summary>炸弹点闪烁周期（秒）—— 原版雷达上的炸弹/闪烁点会明暗交替，静止不闪会被当成包点。</summary>
        public const float BombBlinkPeriod = 0.6f;

        public RectTransform Root;
        public RectTransform DotLayer;
        /// <summary>地图底图节点（sprite 到手前 <c>enabled=false</c>：无 sprite 的 Image 会被画成实心白块）。</summary>
        public Image MapImage;

        [System.NonSerialized] private readonly List<Image> _dots = new List<Image>(48);
        [System.NonSerialized] private bool _warned;
        [System.NonSerialized] private bool _fallbackWarned;
        [System.NonSerialized] private bool _mapSpriteRequested;
        [System.NonSerialized] private bool _warnedMapSprite;
        [System.NonSerialized] private bool _bakedSites;
        [System.NonSerialized] private float _blinkTimer;
        /// <summary>上一次记「雷达映射」判据日志时的世界矩形（变了才记，见 <see cref="Refresh"/>）。</summary>
        [System.NonSerialized] private string _lastRectStamp;

        public void Build(RectTransform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Error("UI", "CsRadarWidget.Build 收到 null 父节点，雷达不会显示");
                return;
            }

            var frame = UIFactory.CreatePanel("Radar", parent, CsHudTheme.PanelBgSoft, false);
            CsHudTheme.PlaceTopLeft(frame.rectTransform, CsHudTheme.RadarTopLeftOffset,
                new Vector2(CsHudTheme.RadarSize, CsHudTheme.RadarSize));
            Root = frame.rectTransform;

            // 内部再收一层：留出边框，画点区域比外框小一圈（外框 128 → 内区 122）
            var inner = UIFactory.CreatePanel("RadarField", Root, new Color(0.06f, 0.08f, 0.06f, 0.55f), false);
            UIFactory.Stretch(inner.rectTransform);
            inner.rectTransform.offsetMin = new Vector2(3f, 3f);
            inner.rectTransform.offsetMax = new Vector2(-3f, -3f);

            // 地图底图（铺满内区；sprite 由 EnsureMapSprite 在运行期取，取不到就保持禁用 + Warn 一次）
            var map = UIFactory.CreatePanel("RadarMap", inner.rectTransform, Color.white, false);
            UIFactory.Stretch(map.rectTransform);
            MapImage = map.rectTransform.GetComponent<Image>();
            if (MapImage == null)
            {
                Game.Logger?.Error("UI", "雷达底图节点上没有 Image（UIFactory.CreatePanel 变了？），底图不会显示");
            }
            else
            {
                MapImage.color = CsHudTheme.RadarMapTint;
                MapImage.enabled = false;
            }

            var layer = UIFactory.CreateNode("RadarDots", inner.rectTransform);
            UIFactory.Stretch(layer);
            DotLayer = layer;

            EnsureMapSprite();

            // **这里不放说明文字**：原来那行 "阵营色点·黄=包点 红=炸弹" 被摆在雷达 Root 里、
            // 坐标 (4,-2)、字号 14 —— 正好压在雷达外框的上边线上，而且 14 个汉字在 200px 宽的
            // 雷达里会把右端挤出面板，实测截图上就是"文字互相重叠 + 显示不全"。
            // CS 1.6 原版雷达本来就没有这行说明（只有左上角一个雷达框 + 底图 + 点），
            // 所以按 1:1 复刻直接去掉，而不是把文案缩短/挪位置。
        }

        /// <summary>
        /// 取底图 sprite（幂等、失败可重试）。
        ///
        /// <para><b>为什么运行期还要取一次</b>：雷达节点是 <see cref="Build"/> 在运行期造的
        /// （不像 HUD 的秒表图标由生成器绑进预制体），所以 sprite 只能走
        /// <c>Game.Res.LoadAsset&lt;Sprite&gt;</c>。失败**不静默**：报一次 Warn 并保持
        /// <c>enabled=false</c>（宁可只有点和框，也不要一块实心白板）。</para>
        /// </summary>
        private void EnsureMapSprite()
        {
            if (MapImage == null || MapImage.sprite != null || _mapSpriteRequested) return;

            var res = Game.Res;
            if (res == null)
            {
                if (!_warnedMapSprite)
                {
                    _warnedMapSprite = true;
                    Game.Logger?.Warn("UI",
                        "Game.Res 为 null（CloverRes.Init 未执行？），雷达底图加载不了：Resources/" +
                        ResPaths.RadarOverviewDust2);
                }
                return;
            }

            _mapSpriteRequested = true;
            res.LoadAsset<Sprite>(ResPaths.RadarOverviewDust2, s =>
            {
                if (s == null)
                {
                    _mapSpriteRequested = false;      // 允许下次 OnOpen 再试（资源刚导入时常见）
                    if (!_warnedMapSprite)
                    {
                        _warnedMapSprite = true;
                        Game.Logger?.Warn("UI",
                            $"雷达底图加载失败（sprite 为空）：Resources/{ResPaths.RadarOverviewDust2}" +
                            "（.png 没导入成 Sprite？见 Editor/UiGenInGame/UiBuilder 的导入设置规整）");
                    }
                    return;
                }

                MapImage.sprite = s;
                MapImage.enabled = true;
                Game.Logger?.Info("UI",
                    $"雷达底图就绪：Resources/{ResPaths.RadarOverviewDust2}（{s.texture.width}×{s.texture.height}，" +
                    $"雷达边长 {CsHudTheme.RadarSize:0}px，1:1 绘制）");
            });
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="visible">比赛没跑 / HUD 整体隐藏时为 false。</param>
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

            EnsureMapSprite();

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

            // 等比缩放：非等比会把 dust2 拉变形，两点间的距离关系就不成立了。
            // 底图生成器（tools/probes/render-overview.py）用的是**同一口径**
            // （正方形窗口、边长 = max(地图X跨度, 地图Z跨度)、以包围盒中心为中心）
            // ⇒ 这里的 scale / cx / cz 与底图像素一一对应，点不会偏。
            var scale = Mathf.Min(field.width / w, field.height / h);
            var cx = (minX + maxX) * 0.5f;
            var cz = (minZ + maxZ) * 0.5f;

            // ── 判据日志（矩形变化时 1 条）：把「世界矩形 → 雷达像素」的映射原文记下来 ──
            //    为什么必须留这条：「雷达上的点有没有偏」不能靠眼睛说 —— 得能拿 actors 的
            //    世界坐标把点的像素位置算回去，再与截图上量到的位置对上。只记一条（一局里矩形不变），
            //    不是每帧刷屏。
            var inv = CultureInfo.InvariantCulture;
            var stamp = minX.ToString("F2", inv) + "," + maxX.ToString("F2", inv) + "," +
                        minZ.ToString("F2", inv) + "," + maxZ.ToString("F2", inv);
            if (stamp != _lastRectStamp)
            {
                _lastRectStamp = stamp;
                Game.Logger?.Info("UI",
                    $"雷达映射：世界 x[{minX.ToString("F2", inv)}..{maxX.ToString("F2", inv)}] " +
                    $"z[{minZ.ToString("F2", inv)}..{maxZ.ToString("F2", inv)}] " +
                    $"内区 {field.width.ToString("F1", inv)}x{field.height.ToString("F1", inv)}px " +
                    $"scale={scale.ToString("F4", inv)}px/m 中心=({cx.ToString("F2", inv)}," +
                    $"{cz.ToString("F2", inv)}) 点={dots.Count}");
            }

            _blinkTimer += Time.deltaTime;

            var drawn = 0;
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

                // 包点：A/B 字母已烧进底图，这里不再叠方块（见类注释）
                if (dot.IsBombsite)
                {
                    if (!_bakedSites)
                    {
                        _bakedSites = true;
                        Game.Logger?.Info("UI", "雷达：快照里的包点标记不画点（A/B 已烧入底图 overview_de_dust2）");
                    }
                    if (img.gameObject.activeSelf) img.gameObject.SetActive(false);
                    continue;
                }

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
                // 正北朝上、不随视角旋转：世界 +X → 屏幕右，世界 +Z（北）→ 屏幕上
                img.rectTransform.anchoredPosition = new Vector2(
                    (dot.X - cx) * scale,
                    (dot.Z - cz) * scale);
                drawn++;
            }

            _lastDrawn = drawn;
        }

        /// <summary>上一帧画出的点数（取证探针读它判断"雷达这一帧到底画了几个点"）。</summary>
        [System.NonSerialized] public int _lastDrawn;

        /// <summary>点色/点尺寸：**自己 = 绿、同队 = 黄、异队 = 蓝、炸弹 = 红**（判据见 <see cref="CsHudTheme"/> 雷达点色一节）。</summary>
        private static float SizeOf(in CsRadarDot dot, out Color color)
        {
            if (dot.IsBomb)
            {
                color = CsHudTheme.RadarBomb;
                return CsHudTheme.RadarBombDotPx;
            }
            if (dot.IsSelf)
            {
                color = CsHudTheme.RadarSelf;
                return CsHudTheme.RadarSelfDotPx;
            }
            if (dot.Team == CsTeam.Spectator)
            {
                color = CsHudTheme.TeamSpec;
                return CsHudTheme.RadarSpecDotPx;
            }

            // 快照只上报"自己这一队"的队友 + **视线可见**的异队敌人 ⇒ 这里能区分同队/异队。
            // 观察者（本地阵亡后观战 / 尚未选边）没有"自己这一队" ⇒ 除自己那一颗外一律按队友色，
            // 免得队友被涂成"敌人蓝"。
            var localTeam = CsHudSnapshot.Team;
            color = (localTeam == CsTeam.Spectator || dot.Team == localTeam)
                ? CsHudTheme.RadarMate : CsHudTheme.RadarOther;
            return CsHudTheme.RadarMateDotPx;
        }

        private void EnsureDots(int count)
        {
            while (_dots.Count < count)
            {
                var img = UIFactory.CreatePanel($"Dot{_dots.Count}", DotLayer, Color.white, false);
                CsHudTheme.PlaceCenter(img.rectTransform, Vector2.zero,
                    new Vector2(CsHudTheme.RadarMateDotPx, CsHudTheme.RadarMateDotPx));
                _dots.Add(img);
            }
        }
    }
}
