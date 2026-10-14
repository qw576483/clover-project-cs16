using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 俯视概览面板（观战档位 5 = Free Overview / 6 = Chase Overview）。
    ///
    /// <para><b>它表达什么</b>：这两档在原版里**不是 3D 机位** —— <c>view.cpp:1545-1551</c> 把
    /// <c>pparams-&gt;onlyClientDraw</c> 置真（契约 = 引擎除 clientDraw 外什么都不画，
    /// <c>common/ref_params.h:70</c>），画面上只剩客户端自己画的俯视概览图 + 投到图上的玩家
    /// （<c>hud_spectator.cpp:1069-1193</c> 画底图、<c>overview.cpp:48-149</c> 画玩家）。
    /// 本控件就是那块全屏概览：不透明底图铺满屏、盖住 3D 画面（相机侧不动机位）。</para>
    ///
    /// <para><b>底图与窗口</b>：底图用雷达那张原版像素图
    /// （<c>Resources/UI/Art/overview_de_dust2.png</c>，1024×768，由原版
    /// <c>cstrike__overviews__de_dust2.bmp</c> 无损转换）。它覆盖的世界矩形 = 原版 overview 窗口
    /// （<c>CsHudTheme.RadarWindowHalfXUnits / RadarWindowHalfZUnits</c>，出处
    /// <c>hud_spectator.cpp:1150-1154</c> 的步长数学 + <c>overviews/de_dust2.txt:5</c> 的 <c>ZOOM 1.50</c>）；
    /// 窗口中心：档位 5 = 底图自带的 <c>ORIGIN</c>（<c>CsHudTheme.RadarWindowCenter</c>）+ 平移
    /// （原版 <c>m_mapOrigin</c>），档位 6 = 被观察者的世界坐标（原版 <c>V_GetMapChasePosition</c>
    /// 取 <c>ent-&gt;origin</c>，view.cpp:1365）。方向与原版底图一致：屏右 ← 世界 −Z、屏上 ← 世界 +X。</para>
    ///
    /// <para><b>玩家标记</b>：原版 <c>CHudOverview::Draw</c> 对**每个活着**的 player 实体画一个按
    /// <c>pl-&gt;angles[1]</c> 旋转的方块（队伍色）、下方写 <c>"%s (%i%%)"</c>（名字 + 血量）；
    /// 方块半边长 = 世界 64 单位在屏上的长度（<c>overview.cpp:57-62、100-144</c>）。本件照此。</para>
    ///
    /// <para><b>等比，不是各向异性铺满</b>：原版底图是 4:3 的一张图，两轴的"每世界单位像素数"相同
    /// （<c>hud_spectator.cpp:1150-1151</c> 的两个步长代入 <c>xTiles=8 / yTiles=6 / screenaspect=4/3</c>
    /// 后同为 <c>1024/zoom</c>）⇒ 本件用**单一标量</b> <c>s = 屏宽×缩放倍数 / 世界窗口宽</c> 同时投影底图与点，
    /// 底图按 0.75（= 768/1024）的固定比例定高：水平铺满屏宽、超出的竖直部分由视窗裁掉。</para>
    ///
    /// <para><b>它与 3D 相机的关系</b>：整块不透明 ⇒ 压在 3D 画面之上；呼叫方把它塞到
    /// HUD 面板根节点的**第一个子节点**（同 <c>CsScopeOverlayWidget</c>），使血量/弹药等在它之上。</para>
    /// </summary>
    public sealed class CsOverviewWidget
    {
        private const string Tag = "UI";

        /// <summary>玩家方块半边长的世界长度（GoldSrc 单位）——原版量的是 64 单位（<c>overview.cpp:59</c>）。</summary>
        private const float MarkHalfSizeUnits = 64f;

        /// <summary>视角锥的长度（GoldSrc 单位）——原版 <c>VectorScale(forward, 512.0f, forward)</c>（<c>hud_spectator.cpp:1369</c>）。</summary>
        private const float ConeLengthUnits = 512f;

        /// <summary>视角锥半张角（度）——原版用 ±45°（<c>hud_spectator.cpp:1372/1378</c> 的 offset[1] = ±45）。</summary>
        private const float ConeHalfAngleDeg = 45f;

        private RectTransform _root;
        private RectTransform _mapRoot;
        private Image _map;
        private RectTransform _markRoot;

        private readonly List<Mark> _marks = new List<Mark>(32);
        private Image _coneA;
        private Image _coneB;

        private bool _built;
        private bool _spriteRequested;
        private bool _spriteWarned;
        private bool _wasVisible;
        private int _lastMarkCount = -1;

        private float _lastScale = float.NaN;
        private Vector2 _lastOffset = new Vector2(float.NaN, float.NaN);

        /// <summary>一个玩家标记的节点组（方块 + 下方文字）。</summary>
        private sealed class Mark
        {
            public RectTransform Node;
            public Image Quad;
            public Text Label;
        }

        /// <summary>概览根节点（呼叫方用它把整块塞到 HUD 之下：<c>SetAsFirstSibling</c>）。</summary>
        public RectTransform Root => _root;

        /// <summary>建节点（幂等）。<paramref name="parent"/> = HUD 面板根节点。</summary>
        public void Build(RectTransform parent)
        {
            if (_built || parent == null)
            {
                if (parent == null)
                    Game.Logger?.Error(Tag, "CsOverviewWidget.Build 收到 null 父节点，俯视概览不会显示");
                return;
            }

            _built = true;

            // 整块：全屏、纯黑底（原版 onlyClientDraw 下画面本来是空的 ⇒ 底图之外就是黑）。
            _root = UIFactory.CreatePanel("Overview", parent, Color.black, false).rectTransform;
            UIFactory.Stretch(_root);
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.gameObject.SetActive(false);

            // 底图容器：**居中定尺**（尺寸每帧按等比 scale 算，见 Refresh）—— 原版底图是一张**等比**的图
            //（两张轴的每世界单位像素数相同），⛔ 不拉满屏（那会让水平/竖直的"米/像素"不同）。
            _mapRoot = UIFactory.CreateNode("OverviewMap", _root);
            _mapRoot.anchorMin = _mapRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _mapRoot.pivot = new Vector2(0.5f, 0.5f);
            _mapRoot.anchoredPosition = Vector2.zero;

            _map = UIFactory.CreatePanel("OverviewImage", _mapRoot, CsHudTheme.OverviewMapTint, false);
            UIFactory.Stretch(_map.rectTransform);
            // 先关着：**没有 sprite 的 Image 会被 uGUI 画成实心白块**，取到素材才开。
            _map.enabled = false;

            // 标记层：**不随底图缩放**（标记尺寸按世界单位在屏上的长度单算，见 MarkHalfSizeUnits）。
            _markRoot = UIFactory.CreateNode("OverviewMarks", _root);
            UIFactory.Stretch(_markRoot);

            // 视角锥：两段细条构成 ±45° 的 V（原版那张 `sprites/camera.spr` 本机不在盘 ⇒ 用细条表达）。
            _coneA = UIFactory.CreatePanel("OverviewConeA", _markRoot, new Color(1f, 1f, 1f, 0.35f), false);
            _coneB = UIFactory.CreatePanel("OverviewConeB", _markRoot, new Color(1f, 1f, 1f, 0.35f), false);

            EnsureMapSprite();
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="visible">是否处于俯视概览档位（5 / 6）且本地在观战。</param>
        /// <param name="mode">当前观战档位（只有 5 / 6 会让本件显示）。</param>
        /// <param name="targetX">被观察者世界 X（米）；档位 6 的窗口中心、视角锥的锚点。</param>
        /// <param name="targetZ">被观察者世界 Z（米）。</param>
        public void Refresh(bool visible, CsSpectatorMode mode, float targetX, float targetZ)
        {
            if (_root == null)
            {
                if (visible)
                    Game.Logger?.Error(Tag, "CsOverviewWidget 引用缺失（未 Build），俯视概览不会显示");
                return;
            }

            var isChase = mode == CsSpectatorMode.ChaseOverview;

            if (_root.gameObject.activeSelf != visible)
                _root.gameObject.SetActive(visible);
            if (!visible) { _wasVisible = false; return; }

            EnsureMapSprite();

            // ---- 窗口中心（世界米）：档位 5 = 底图 ORIGIN + 平移；档位 6 = 被观察者 ----
            // 原版：V_GetMapFreePosition 用 m_mapOrigin（view.cpp:1333-1335）、
            //       V_GetMapChasePosition 用 ent->origin（view.cpp:1365）。
            var panMetres = CsSpectator.MapPan * CsHudTheme.GoldSrcUnitToMetre;
            var centerX = isChase ? targetX : CsHudTheme.RadarWindowCenter.x;
            var centerZ = (isChase ? targetZ : CsHudTheme.RadarWindowCenter.y) + (isChase ? 0f : panMetres);

            // ---- 缩放：原版 m_mapZoom（钳 0.5~3.0，hud_spectator.cpp:399-403）----
            var zoom = Mathf.Clamp(CsSpectator.MapZoom, CsHudTheme.OverviewZoomMin, CsHudTheme.OverviewZoomMax);
            var scale = zoom / CsHudTheme.OverviewZoomDefault;   // 1 = 底图正好铺满屏

            // ---- 世界 → 屏：**单一标量**（等比），与原版同一口径 ----
            // 原版 hud_spectator.cpp:1150-1151 的两轴步长代入 xTiles=8 / yTiles=6 / screenaspect=4/3
            //（:1081-1091）后**同为 1024/zoom** ⇒ 每块 tile 是正方形、单一标量；底图世界幅
            // 6144/zoom × 8192/zoom 的长宽比 = 0.75 = 768/1024，与底图像素同比例。
            var hz = CsHudTheme.RadarWindowHalfZUnits * CsHudTheme.GoldSrcUnitToMetre;
            var screenW = _root.rect.width;
            if (screenW <= 1f) return;                       // 布局还没跑，下一帧再来

            // 可见世界窗口半宽（水平轴 = 世界 Z）= 底图半宽 / 缩放倍数 ⇒ 水平正好铺满屏宽；
            // 竖直方向上底图高 = 屏宽 × 倍数 × 0.75 ⇒ 16:9 屏上**超出屏**、由视窗裁掉（对位原版：
            // 水平 FOV 固定、竖直随宽高比被裁，见 view.cpp:1342 的相机推拉）。
            var s = screenW * scale / (2f * hz);             // px / 世界米（等比：两轴同一个值）
            var mapSize = new Vector2(screenW * scale, screenW * scale * CsHudTheme.OverviewMapAspect);

            // 底图中心（= 世界 RadarWindowCenter）在屏上的偏移。
            var offset = new Vector2(
                (centerZ - CsHudTheme.RadarWindowCenter.y) * s,
                (CsHudTheme.RadarWindowCenter.x - centerX) * s);

            if (!Mathf.Approximately(scale, _lastScale) || offset != _lastOffset
                || _mapRoot.sizeDelta != mapSize)
            {
                _lastScale = scale;
                _lastOffset = offset;
                _mapRoot.sizeDelta = mapSize;
                _mapRoot.anchoredPosition = offset;
            }

            DrawMarks(centerX, centerZ, s, targetX, targetZ);

            if (!_wasVisible)
            {
                _wasVisible = true;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var spriteInfo = _map != null && _map.sprite != null
                    ? $"{_map.sprite.texture.width}×{_map.sprite.texture.height}"
                    : "未就绪";
                var modeName = isChase ? "Chase Overview" : "Free Overview";
                Game.Logger?.Info(Tag,
                    $"俯视概览显示：{modeName}（档位 {(int)mode}/6）" +
                    $"窗口中心=({centerX.ToString("F2", inv)},{centerZ.ToString("F2", inv)})m " +
                    $"缩放={zoom.ToString("F2", inv)}（原版 m_mapZoom，钳 {CsHudTheme.OverviewZoomMin}~{CsHudTheme.OverviewZoomMax}）" +
                    $" 平移={CsSpectator.MapPan.ToString("F1", inv)}u" +
                    $" 底图={spriteInfo} 缩放倍数={scale.ToString("F2", inv)}" +
                    $" 等比比例={s.ToString("F3", inv)}px/世界米（底图 {mapSize.x.ToString("F0", inv)}×{mapSize.y.ToString("F0", inv)}px）" +
                    $" 屏偏移=({offset.x.ToString("F1", inv)},{offset.y.ToString("F1", inv)})px 标记={CsHudSnapshot.Overview.Count}");
            }
        }

        /// <summary>
        /// 画玩家标记 + 名字血量 + 视角锥（原版 <c>overview.cpp:48-149</c> / <c>hud_spectator.cpp:1331-1391</c>）。
        /// </summary>
        private void DrawMarks(float centerX, float centerZ, float s, float targetX, float targetZ)
        {
            var marks = CsHudSnapshot.Overview;
            EnsureMarks(marks.Count);

            // 方块半边长 = 世界 64 单位投到屏上的长度（原版 overview.cpp:57-62 量的是 64 单位；
            // 等比 ⇒ 两轴同一个 s）
            var halfPx = MarkHalfSizeUnits * CsHudTheme.GoldSrcUnitToMetre * s;

            var drawn = 0;
            var targetIndex = -1;
            for (var i = 0; i < _marks.Count; i++)
            {
                var slot = _marks[i];
                if (slot.Node == null) continue;

                if (i >= marks.Count)
                {
                    if (slot.Node.gameObject.activeSelf) slot.Node.gameObject.SetActive(false);
                    continue;
                }

                var m = marks[i];
                if (!slot.Node.gameObject.activeSelf) slot.Node.gameObject.SetActive(true);

                // 世界 → 屏偏移（右 ← −Z、上 ← +X，与底图同向；等比 ⇒ 两轴同一个 s）
                var dx = -(m.Z - centerZ) * s;
                var dy = (m.X - centerX) * s;
                CsHudTheme.PlaceCenter(slot.Node, new Vector2(dx, dy),
                    new Vector2(2f * halfPx, 2f * halfPx));

                // 朝向 = 人脸朝的方向（yaw 0 = +Z、yaw 90 = +X，见引擎 CameraMath.AimDirection）
                var fx = Mathf.Sin(m.Yaw * Mathf.Deg2Rad);
                var fz = Mathf.Cos(m.Yaw * Mathf.Deg2Rad);
                var rot = Mathf.Atan2(fx, -fz) * Mathf.Rad2Deg - 90f;   // 方块"上"指向人脸朝向
                slot.Quad.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rot);

                if (m.IsSpectateTarget)
                {
                    targetIndex = i;
                    slot.Quad.color = CsHudTheme.OverviewTargetRing;
                }
                else
                {
                    slot.Quad.color = TeamColor(m.Team);
                }

                slot.Label.text = $"{m.Name} ({m.Health}%)";
                drawn++;
            }

            // ---- 视角锥：锚在被观察者身上、沿他的朝向 ±45°，长 512 单位 ----
            // 原版 hud_spectator.cpp:1349-1350 在 map 档位走 `V_GetChasePos(g_iUser2, NULL, …)` ⇒ 角度 = 目标自己的
            // （locked），再 :1368-1391 画 ±45°、长 512 的锥。
            var coneOn = drawn > 0 && targetIndex >= 0;
            _coneA.gameObject.SetActive(coneOn);
            _coneB.gameObject.SetActive(coneOn);
            if (coneOn)
            {
                var dx = -(targetZ - centerZ) * s;
                var dy = (targetX - centerX) * s;
                var lenPx = ConeLengthUnits * CsHudTheme.GoldSrcUnitToMetre * s;
                var yaw = 0f;
                for (var i = 0; i < marks.Count; i++)
                    if (marks[i].IsSpectateTarget) { yaw = marks[i].Yaw; break; }
                LayCone(_coneA, dx, dy, yaw + ConeHalfAngleDeg, lenPx);
                LayCone(_coneB, dx, dy, yaw - ConeHalfAngleDeg, lenPx);
            }

            _lastMarkCount = drawn;
        }

        /// <summary>把一段细条摆成"从标记点出发、朝向 <paramref name="yawDeg"/>、长 <paramref name="lenPx"/> 的线"。</summary>
        private static void LayCone(Image bar, float dx, float dy, float yawDeg, float lenPx)
        {
            // 单位方向（等比 ⇒ 两轴同一个 s，方向与长度都等比）
            var fx = Mathf.Sin(yawDeg * Mathf.Deg2Rad);
            var fz = Mathf.Cos(yawDeg * Mathf.Deg2Rad);
            var sx = -fz;
            var sy = fx;
            var rot = Mathf.Atan2(sy, sx) * Mathf.Rad2Deg - 90f;      // 细条"上"指向该方向

            var ex = dx + sx * lenPx;
            var ey = dy + sy * lenPx;
            CsHudTheme.PlaceCenter(bar.rectTransform, new Vector2((dx + ex) * 0.5f, (dy + ey) * 0.5f),
                new Vector2(2f, lenPx));
            bar.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rot);
        }

        /// <summary>队伍色（原版 <c>overview.cpp:77-89</c>）。</summary>
        private static Color TeamColor(CsTeam team)
        {
            switch (team)
            {
                case CsTeam.T: return CsHudTheme.OverviewTeamT;
                case CsTeam.CT: return CsHudTheme.OverviewTeamCT;
                default: return CsHudTheme.OverviewNobody;
            }
        }

        /// <summary>上一帧画出的标记数（供取证 / 日志用）。</summary>
        public int LastMarkCount => _lastMarkCount;

        /// <summary>按需补建标记节点（方块 + 下方文字）。</summary>
        private void EnsureMarks(int count)
        {
            while (_marks.Count < count)
            {
                var node = UIFactory.CreateNode($"Mark{_marks.Count}", _markRoot);
                CsHudTheme.PlaceCenter(node, Vector2.zero, new Vector2(10f, 10f));

                var quad = UIFactory.CreatePanel("Quad", node, Color.white, false);
                UIFactory.Stretch(quad.rectTransform);

                var label = CsHudTheme.CreateText("Text", node, string.Empty, CsHudTheme.OverviewTextSize,
                    TextAnchor.UpperCenter, CsHudTheme.OverviewText);
                UIFactory.Place(label.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f),
                    new Vector2(0f, -2f), new Vector2(160f, 18f));

                _marks.Add(new Mark { Node = node, Quad = quad, Label = label });
            }
        }

        /// <summary>
        /// 取底图 sprite（幂等、失败可重试）。走与雷达同一份原版像素图
        /// （<c>ResPaths.RadarOverviewDust2</c>）。失败**不静默**：报一次 Warn 并保持
        /// <c>enabled=false</c>（宁可只剩黑屏，也不要一块实心白板）。
        /// </summary>
        private void EnsureMapSprite()
        {
            if (_map == null || _map.sprite != null || _spriteRequested) return;

            var res = Game.Res;
            if (res == null)
            {
                if (!_spriteWarned)
                {
                    _spriteWarned = true;
                    Game.Logger?.Warn(Tag,
                        "Game.Res 为 null（CloverRes.Init 未执行？），俯视概览底图加载不了：Resources/" +
                        ResPaths.RadarOverviewDust2);
                }
                return;
            }

            _spriteRequested = true;
            res.LoadAsset<Sprite>(ResPaths.RadarOverviewDust2, s =>
            {
                if (s == null)
                {
                    _spriteRequested = false;      // 允许下次再试（资源刚导入时常见）
                    if (!_spriteWarned)
                    {
                        _spriteWarned = true;
                        Game.Logger?.Warn(Tag,
                            $"俯视概览底图加载失败（sprite 为空）：Resources/{ResPaths.RadarOverviewDust2}");
                    }
                    return;
                }

                _map.sprite = s;
                _map.enabled = true;
            });
        }
    }
}
