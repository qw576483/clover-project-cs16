using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 准星 + 命中标记（规格 H7 / H9，策划案里叫 <c>CrosshairWidget</c>）。
    ///
    /// <para><b>形态</b>：原版 <c>CHudCrosshair::Draw</c> 只画 **4 个矩形臂**（没有中心点），
    /// 臂粗恒 **1 px**（<c>client.dll:0x41970</c> 的 <c>push 1</c>）；中心到臂根的间隙与臂长
    /// **都随屏宽等比缩放** —— <c>间隙 = (int)(屏宽 × 动态量 ÷ 尺寸档除数)</c>、
    /// <c>臂长 = 屏宽 × (5 + 0.5 × (动态量 − 下界)) ÷ 尺寸档除数</c>。
    /// 尺寸档除数由 <c>cl_crosshair_size</c> 决定（默认 <c>"0"</c> = auto，按屏宽分档）。
    /// 全部取值见本类各常量注释里的 <c>client.dll</c> 偏移。</para>
    ///
    /// <para><b>命中标记</b>：4 条 45° 斜线拼成 X，显示
    /// <see cref="CsHudSnapshot.HitMarkerTime"/> 秒；爆头用红色（规格 H9 要求普通/爆头不同色）。</para>
    ///
    /// <para>
    /// 数值来源全是 <see cref="CsHudSnapshot"/>（由 <c>Module/Combat</c> 写），
    /// 本件只做表现，不读任何业务对象 —— 这是 UI 层的分层铁律。
    /// </para>
    /// </summary>
    [System.Serializable]
    public sealed class CsCrosshairWidget
    {
        // ─────────── 尺寸档除数（`cl_crosshair_size`）───────────
        // 取值：auto 档按屏宽分档 —— 屏宽 ≤ 640 → 1024；640 < 屏宽 < 1024 → 800；屏宽 ≥ 1024 → 640。
        //   client.dll:0x40af9-0x40b0a（`mov eax,[0x1a24a08]` = 屏宽；`cmp eax,0x280` / `cmp eax,0x400`）
        //   client.dll:0x40b1d（1024）/ 0x40b2e（800）/ 0x40b3f（640）
        /// <summary>auto 档：屏宽 ≤ 640 时的除数（原版 small 档）。</summary>
        public const int DivisorNarrow = 1024;
        /// <summary>auto 档：640 &lt; 屏宽 &lt; 1024 时的除数（原版 medium 档）。</summary>
        public const int DivisorMedium = 800;
        /// <summary>auto 档：屏宽 ≥ 1024 时的除数（原版 large 档）。</summary>
        public const int DivisorWide = 640;
        /// <summary>auto 分档的屏宽门槛 640（client.dll:0x40b0c 的 `0x280`）。</summary>
        private const int TierThresholdLow = 640;
        /// <summary>auto 分档的屏宽门槛 1024（client.dll:0x40b05 的 `cmp eax,0x400`）。</summary>
        private const int TierThresholdHigh = 1024;

        /// <summary>准星臂粗（像素）。原版 4 个臂的矩形另一维恒为 1（client.dll:0x41970 等的 `push 1`）。</summary>
        public const float LineThickness = 1f;
        /// <summary>
        /// 动态准星量的上限（原版 <c>m_flCrosshairSize</c> 的 float 上限 15.0）。
        /// 出处：client.dll:0x41823（`mov dword [ebx+0x44],0x41700000` = 15.0f）与 0x19cd4d0 = 15.0f。
        /// </summary>
        public const float DynamicMaxUnits = 15f;
        /// <summary>
        /// 动态准星量的下界 —— 原版把该量夹到不低于精灵宽（<c>client.dll:0x4186c-0x4187f</c>：
        /// <c>fild</c> 精灵宽 → <c>fcom</c> 动态量 → 仅当精灵宽 &gt; 动态量时才回写，即"抬到精灵宽"）。
        /// 取值 **8** 由原版 640×480 空闲帧实测反解：该帧间隙 5 px ⇒ <c>640 × d ÷ 1024 = 5</c>
        /// ⇒ <c>d = 8</c>；同帧臂长 3 px ⇒ <c>640 × 5 ÷ 1024 = 3.1</c>（载体
        /// <c>原版资源/参照物/viewmodel/viewmodel_deagle_640x480.png</c>，量法
        /// <c>.ai-tmp/test/hudui-cross2.py</c>）。
        /// </summary>
        public const float DynamicMinUnits = 8f;
        /// <summary>
        /// 臂长的基量与半斜率：原版 <c>esi = 5 − (int)((动态量 − 精灵宽) × k)</c>，
        /// <c>k = −0.5f</c>（<c>client.dll:0x19cd4c4</c>）、基量 <c>5</c>（<c>client.dll:0x41658</c>）。
        /// 代入下界后 <c>臂长基量 = 5 + 0.5 × (动态量 − 8)</c>。
        /// </summary>
        public const float ArmBaseUnits = 5f;
        public const float ArmSlope = 0.5f;
        /// <summary>
        /// 半透明开启时的 alpha（0~255）。原版 <c>cl_crosshair_translucent</c> 默认 <c>"1"</c>
        /// （client.dll:0xe45f8），alpha 取 <c>[this+0x48]</c> 的初值 200（client.dll:0x3f926）。
        /// </summary>
        public const byte TranslucentAlpha = 200;
        /// <summary>命中标记斜线长度与到中心的距离。</summary>
        public const float HitTickLength = 12f;
        public const float HitTickThickness = 2f;
        public const float HitTickOffset = 9f;

        /// <summary>本件的根节点（<c>active=false</c> 时整组准星消失）。</summary>
        public RectTransform Root;
        /// <summary>4 条准星线：0 左 / 1 右 / 2 上 / 3 下。</summary>
        public RectTransform[] Bars;
        /// <summary>
        /// 原版准星不画中心点（绘制只有 4 个矩形臂）；旧预制体里序列化过这个节点，
        /// 刷新时一律收掉。
        /// </summary>
        public RectTransform Dot;
        /// <summary>命中标记根节点（4 条斜线一起淡出）。</summary>
        public RectTransform HitMarker;
        /// <summary>命中标记的 4 条斜线（索引与 <see cref="Bars"/> 无关，仅内部使用）。</summary>
        public RectTransform[] HitTicks;
        /// <summary>命中标记 4 条斜线的 Image（改颜色用）。</summary>
        public Image[] HitTickImages;

        [System.NonSerialized] private bool _warned;

        /// <summary>搭出准星与命中标记（生成器与运行期兜底共用这一份）。</summary>
        public void Build(RectTransform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Error("UI", "CsCrosshairWidget.Build 收到 null 父节点，准星不会显示");
                return;
            }

            Root = UIFactory.CreateCentered("Crosshair", parent, Vector2.zero, Vector2.zero);

            Bars = new RectTransform[4];
            Bars[0] = CreateBar("Left", Root, new Vector2(LineThickness, LineThickness));
            Bars[1] = CreateBar("Right", Root, new Vector2(LineThickness, LineThickness));
            Bars[2] = CreateBar("Top", Root, new Vector2(LineThickness, LineThickness));
            Bars[3] = CreateBar("Bottom", Root, new Vector2(LineThickness, LineThickness));

            HitMarker = UIFactory.CreateCentered("HitMarker", parent, Vector2.zero, Vector2.zero);
            HitTicks = new RectTransform[4];
            HitTickImages = new Image[4];

            // X 形：一条对角线由 2 段构成（各自在中心的两侧），旋转 ±45°
            //   "/"  = 左下(-d,+d) 与 右上(+d,-d)，旋转 +45°
            //   "\"  = 左上(-d,-d) 与 右下(+d,+d)，旋转 -45°
            CreateHitTick(0, new Vector2(-HitTickOffset, HitTickOffset), 45f);
            CreateHitTick(1, new Vector2(HitTickOffset, -HitTickOffset), 45f);
            CreateHitTick(2, new Vector2(-HitTickOffset, -HitTickOffset), -45f);
            CreateHitTick(3, new Vector2(HitTickOffset, HitTickOffset), -45f);
        }

        /// <summary>
        /// 用当前主题色重刷准星本体（4 条线 + 中心点）。命中标记的取色由 <see cref="Refresh"/> 每帧决定，不改。
        ///
        /// <para><b>为什么需要这个方法</b>：准星颜色只在 <see cref="Build"/> 里设过一次，而运行期用的是
        /// **预制体里序列化的那批 Image**（<c>CsPanelBase.Awake</c> 见预制体有子节点就不再跑 Build）⇒
        /// 只改 <see cref="CsHudTheme.Crosshair"/> 而不重跑生成器时，实机看到的还是旧色。
        /// 由 <c>HudPanel.OnOpen</c> 调一次，与 Build 同一取值来源（<see cref="CsHudTheme.Crosshair"/>）。</para>
        /// </summary>
        public void ApplyColor(Color color)
        {
            color.a = TranslucentAlpha / 255f;
            if (Bars != null)
            {
                for (var i = 0; i < Bars.Length; i++)
                {
                    var img = Bars[i] != null ? Bars[i].GetComponent<Image>() : null;
                    if (img != null) img.color = color;
                }
            }
        }

        /// <summary>
        /// 按原版 <c>cl_crosshair_size</c> 的 auto 分档取除数（按屏宽分档，见本类三个除数常量的注释）。
        /// </summary>
        /// <param name="refWidth">画布参考宽（UI 布局单位；1080p 下与原版的屏宽同为 1920）。</param>
        public static int DivisorForWidth(float refWidth)
        {
            var w = Mathf.RoundToInt(refWidth);
            if (w <= TierThresholdLow) return DivisorNarrow;
            return w < TierThresholdHigh ? DivisorMedium : DivisorWide;
        }

        /// <summary>准星所在画布的参考宽（换算成本件的布局单位，与原版"屏宽"同一口径）。</summary>
        private float ReferenceWidth()
        {
            var node = Root;
            while (node != null)
            {
                var canvas = node.GetComponentInParent<Canvas>();
                if (canvas != null) return ((RectTransform)canvas.transform).rect.width;
                node = node.parent as RectTransform;
            }

            return Screen.width;
        }

        private RectTransform CreateBar(string name, RectTransform parent, Vector2 size)
        {
            var img = UIFactory.CreatePanel(name, parent, CsHudTheme.Crosshair, false);
            CsHudTheme.PlaceCenter(img.rectTransform, Vector2.zero, size);
            return img.rectTransform;
        }

        private void CreateHitTick(int index, Vector2 pos, float angle)
        {
            var img = UIFactory.CreatePanel($"Tick{index}", HitMarker, Color.white, false);
            CsHudTheme.PlaceCenter(img.rectTransform, pos, new Vector2(HitTickLength, HitTickThickness));
            img.rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
            HitTicks[index] = img.rectTransform;
            HitTickImages[index] = img;
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="visible">
        /// 是否显示准星（死亡观战 / 开镜时不显示 —— CS 1.6 死亡后没有准星，AWP 开镜后准星被镜片取代）。
        /// </param>
        /// <param name="spread01"><see cref="CsHudSnapshot.CrosshairSpread"/>，0~1。</param>
        /// <param name="hitTimeLeft"><see cref="CsHudSnapshot.HitMarkerTime"/>，&gt;0 时显示命中标记。</param>
        /// <param name="headshot"><see cref="CsHudSnapshot.HitMarkerHeadshot"/>，爆头用红色。</param>
        public void Refresh(bool visible, float spread01, float hitTimeLeft, bool headshot)
        {
            if (Root == null || HitMarker == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Game.Logger?.Error("UI",
                        "CsCrosshairWidget 引用缺失（预制体未由 UiBuilder 生成或已被改动），准星与命中标记都不会显示");
                }
                return;
            }

            if (Root.gameObject.activeSelf != visible) Root.gameObject.SetActive(visible);

            // 原版只画 4 个臂、没有中心点；预制体里序列化过的中心点节点要收掉。
            if (Dot != null && Dot.gameObject.activeSelf) Dot.gameObject.SetActive(false);

            if (visible && Bars != null && Bars.Length == 4)
            {
                // 动态量 d ∈ [下界, 上限]（原版 m_flCrosshairSize）；间隙 = sw×d÷除数（截断）、
                // 臂长 = sw×(5+0.5×(d−下界))÷除数（截断）、臂粗恒 1 px（client.dll:0x41970）。
                var refWidth = ReferenceWidth();
                var divisor = DivisorForWidth(refWidth);
                var d = Mathf.Lerp(DynamicMinUnits, DynamicMaxUnits, Mathf.Clamp01(spread01));
                var armUnits = ArmBaseUnits + ArmSlope * (d - DynamicMinUnits);
                var len = Mathf.Max(1, Mathf.FloorToInt(refWidth * armUnits / divisor));
                var gap = Mathf.FloorToInt(refWidth * d / divisor);

                Bars[0].sizeDelta = new Vector2(len, LineThickness);
                Bars[0].anchoredPosition = new Vector2(-gap - len * 0.5f, 0f);
                Bars[1].sizeDelta = new Vector2(len, LineThickness);
                Bars[1].anchoredPosition = new Vector2(gap + len * 0.5f, 0f);
                Bars[2].sizeDelta = new Vector2(LineThickness, len);
                Bars[2].anchoredPosition = new Vector2(0f, gap + len * 0.5f);
                Bars[3].sizeDelta = new Vector2(LineThickness, len);
                Bars[3].anchoredPosition = new Vector2(0f, -gap - len * 0.5f);
            }

            var showHit = hitTimeLeft > 0f;
            if (HitMarker.gameObject.activeSelf != showHit) HitMarker.gameObject.SetActive(showHit);
            if (!showHit) return;

            var color = headshot ? CsHudTheme.Danger : CsHudTheme.TextMain;
            var alpha = Mathf.Clamp01(hitTimeLeft / Mathf.Max(0.0001f, CsConst.HitMarkerTime));
            color.a = alpha;

            if (HitTickImages == null) return;
            for (var i = 0; i < HitTickImages.Length; i++)
            {
                if (HitTickImages[i] != null) HitTickImages[i].color = color;
            }
        }
    }
}
