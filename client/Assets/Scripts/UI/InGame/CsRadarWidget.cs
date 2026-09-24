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
    /// <list type="number">
    /// <item><b>加上地图底图</b>：原版雷达不是"一个空框 + 几个点"，而是把地图**俯视图**半透明压在
    /// <c>overviews/de_dust2.bmp</c> 的逐像素 PNG**（1024×768；由
    /// <c>tools/probes/import-original-overview.py</c> 从载体
    /// <c>原版资源/cs16src/cstrike/cstrike__overviews__de_dust2.bmp</c> 无损转换，绿键色 → alpha 0；
    /// 重解码自检 rgb/alpha mismatch = 0），不再是 <c>tools/probes/render-overview.py</c> 的几何栅格化
    /// 近似图。A/B 点字母是原版图自带的。</item>
    /// <item><b>尺寸落 1:1 真值</b>：<see cref="CsHudTheme.RadarSize"/> 由 200 → <b>128</b>
    /// （原版 <c>hud.txt:183</c> <c>radar 640 radar640 0 0 128 128</c> 的原生像素；
    /// 640 档精灵在 1080p 上 1:1 不缩放，判据见该常量的注释）。</item>
    /// <item><b>点色按原版判据</b>：自己 = 绿（<see cref="CsHudTheme.RadarSelf"/>）、同队 = 黄
    /// （<see cref="CsHudTheme.RadarMate"/>）、异队（雷达可见的敌人）= 蓝
    /// （<see cref="CsHudTheme.RadarOther"/>）、已安放炸弹 = 红闪
    /// （<see cref="CsHudTheme.RadarBomb"/>）。上一版把"自己"画成白的、队友画成阵营色，
    /// 与判据不符，本片改掉。</item>
    /// <item><b>朝向</b>：**不随视角旋转**（CS 1.6 默认即如此），且与原版底图同向 ——
    /// 屏幕右 ← 世界 <c>-Z</c>、屏幕上 ← 世界 <c>+X</c>（cs16-AO 由包点地标判据定下，见
    /// <see cref="Refresh"/> 里 scale 那段的注释）；世界 → 雷达的映射与底图**同一套窗口**（各向同性
    /// scale、中心 = 原版 ORIGIN 的地标配准值）⇒ 点必然落在底图对应像素上（判据见
    /// <c>tools/probes/radar-window-check.py</c>：A/B 两处包点的偏差 1.17 radar px）。</item>
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
    /// <para><b>世界范围（口径，片AS 改）</b>：雷达覆盖哪块世界，由**原版 overview 的窗口**决定
    /// （X = 中心 ± 2048、Z = 中心 ± 2730.6667 GoldSrc 单位；公式真源
    /// <c>原版资源/hlsdk/cl_dll/hud_spectator.cpp:1069-1193</c>，ZOOM 见
    /// <c>原版资源/cs16src/cstrike/overviews/de_dust2.txt:5</c>），中心 = 原版 <c>ORIGIN</c> 经地标配准
    /// 到本工程坐标系（推导见 <see cref="CsHudTheme.RadarWindowCenter"/>）。不再取引擎位图包围盒
    /// （<c>Game.Map</c>）等比 —— 那是底图还是"几何栅格化"时代的旧口径。矩形由 <see cref="HudPanel"/>
    /// 按同一组常量换算成米后传进来；传来退化矩形时本件用同一组常量兜底，并且只报一次警告。</para>
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

            // 地图底图（sprite 由 EnsureMapSprite 在运行期取，取不到就保持禁用 + Warn 一次）。
            //
            // `原版资源/cs16src/cstrike/cstrike__overviews__de_dust2.bmp` 逐像素转来），4:3 的图
            // ⇒ 必须按原比例画：**宽 = 内区宽 122、高 = 122 × 768/1024 = 91.5**，在内区里上下居中。
            // 不许 Stretch 成 122×122 —— 那会让底图水平/竖直的"米/像素"不同，而雷达点的映射是
            // 单一 scale（各向同性，出自原版公式 8/ZOOM）⇒ 点与底图立刻不再同尺度，"同尺度同原点"
            // 这条判据当场失效。
            var map = UIFactory.CreatePanel("RadarMap", inner.rectTransform, Color.white, false);
            var mapRt = map.rectTransform;
            mapRt.anchorMin = mapRt.anchorMax = new Vector2(0.5f, 0.5f);
            mapRt.pivot = new Vector2(0.5f, 0.5f);
            mapRt.anchoredPosition = Vector2.zero;
            mapRt.sizeDelta = new Vector2(
                CsHudTheme.RadarInnerSize,
                CsHudTheme.RadarInnerSize * 768f / 1024f);
            MapImage = mapRt.GetComponent<Image>();
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
                    $"雷达底图就绪：Resources/{ResPaths.RadarOverviewDust2}（{s.texture.width}×{s.texture.height}" +
                    $"= 原版 overview 像素；按原比例画成 {CsHudTheme.RadarInnerSize:0}×" +
                    $"{CsHudTheme.RadarInnerSize * 768f / 1024f:0.#}px，scale 与原版 8/ZOOM 口径一致）");
            });
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="visible">比赛没跑 / HUD 整体隐藏时为 false。</param>
        /// <param name="minX">雷达窗口世界 X 最小值（米）——**原版 overview 窗口**，由 HudPanel 按
        /// CsHudTheme 的窗口常量下发；不是引擎位图包围盒。</param>
        /// <param name="maxX">雷达窗口世界 X 最大值（米）。</param>
        /// <param name="minZ">雷达窗口世界 Z 最小值（米）。</param>
        /// <param name="maxZ">雷达窗口世界 Z 最大值（米）。</param>
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
                // 这样即使 HudPanel 传了空矩形，点与底图仍然同尺度同原点。
                var fc = CsHudTheme.RadarWindowCenter;
                var fx = CsHudTheme.RadarWindowHalfXUnits * CsHudTheme.GoldSrcUnitToMetre;
                var fz = CsHudTheme.RadarWindowHalfZUnits * CsHudTheme.GoldSrcUnitToMetre;
                minX = fc.x - fx; maxX = fc.x + fx;
                minZ = fc.y - fz; maxZ = fc.y + fz;
                w = maxX - minX;
                h = maxZ - minZ;
            }

            // 底图已换成原版那张 `overviews/de_dust2.bmp`（1024×768，逐像素转成
            // Resources/UI/Art/overview_de_dust2.png），所以"雷达显示哪块世界"必须照**原版
            // overview 的窗口**：
            //     X ∈ 中心 ± 2048 单位、Z ∈ 中心 ± 2730.6667 单位
            //   · 跨度出处 = 原版公式 世界 X 跨度 6144/ZOOM(=4096)、世界 Y 跨度 8192/ZOOM(=5461.3333)；
            //     ZOOM = 1.50 逐字取自 `原版资源/cs16src/cstrike/overviews/de_dust2.txt:5`；
            //     公式真源 `原版资源/hlsdk/cl_dll/hud_spectator.cpp:1069-1193`（Half-Life SDK，
            //   · 四个数值与窗口中心全部落在 `CsHudTheme.RadarWindowHalfXUnits /
            //     RadarWindowHalfZUnits / RadarWindowCenter`，由 `HudPanel.UpdateRadarBounds`
            //     用**同一组常量**算成米后传进来（本件不再自己读 Game.Map）。
            //
            // 缩放仍是**各向同性**（= 原版"每像素 8/ZOOM 单位"）：
            //   内区 122×122 ⇒ scale = min(122/h, 122/w) = 122/138.718 = 0.8795 px/m；
            //   底图按 4:3 画成 122 × 91.5 ⇒ 水平 122px ↔ Z 跨度、竖直 91.5px ↔ X 跨度，同一个 scale
            //   ⇒ 点必然落在底图对应像素上（这就是"同尺度同原点"）。
            //
            // 依据 = 原版底图自带的两处包点标记：图上向量 vs 世界向量的角度残差 **1.55°**
            // （旧轴对 88.45°），`tools/probes/locate-overview-letters.py` ⇒ 水平用 Z 跨度 h、
            // 竖直用 X 跨度 w。
            var scale = Mathf.Min(field.width / h, field.height / w);
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
                // 不随视角旋转（CS 1.6 默认即如此），但**不是正北朝上**：与原版底图同向 ⇒
                // 屏幕右 ← 世界 -Z、屏幕上 ← 世界 +X（轴对依据见上面 scale 那段的注释与
                img.rectTransform.anchoredPosition = new Vector2(
                    -(dot.Z - cz) * scale,
                    (dot.X - cx) * scale);
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
