using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 准星 + 命中标记（规格 H7 / H9，策划案里叫 <c>CrosshairWidget</c>）。
    ///
    /// <para><b>形态</b>：4 条短线 + 中心点（CS 1.6 的默认准星），间隙随
    /// <see cref="CsHudSnapshot.CrosshairSpread"/> 变化（0 = 静止最小，1 = 最大扩散）。</para>
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
        /// <summary>单条准星线的长度。</summary>
        public const float LineLength = 10f;
        /// <summary>准星线粗细。</summary>
        public const float LineThickness = 2f;
        /// <summary>完全静止时的中心间隙。</summary>
        public const float MinGap = 3f;
        /// <summary>最大扩散时的中心间隙。</summary>
        public const float MaxGap = 34f;
        /// <summary>中心点边长。</summary>
        public const float DotSize = 2f;
        /// <summary>命中标记斜线长度与到中心的距离。</summary>
        public const float HitTickLength = 12f;
        public const float HitTickThickness = 2f;
        public const float HitTickOffset = 9f;

        /// <summary>本件的根节点（<c>active=false</c> 时整组准星消失）。</summary>
        public RectTransform Root;
        /// <summary>4 条准星线：0 左 / 1 右 / 2 上 / 3 下。</summary>
        public RectTransform[] Bars;
        /// <summary>中心点。</summary>
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
            Bars[0] = CreateBar("Left", Root, new Vector2(LineLength, LineThickness));
            Bars[1] = CreateBar("Right", Root, new Vector2(LineLength, LineThickness));
            Bars[2] = CreateBar("Top", Root, new Vector2(LineThickness, LineLength));
            Bars[3] = CreateBar("Bottom", Root, new Vector2(LineThickness, LineLength));

            var dot = UIFactory.CreatePanel("Dot", Root, CsHudTheme.Crosshair, false);
            Dot = dot.rectTransform;
            CsHudTheme.PlaceCenter(Dot, Vector2.zero, new Vector2(DotSize, DotSize));

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
            if (Bars != null)
            {
                for (var i = 0; i < Bars.Length; i++)
                {
                    var img = Bars[i] != null ? Bars[i].GetComponent<Image>() : null;
                    if (img != null) img.color = color;
                }
            }

            var dot = Dot != null ? Dot.GetComponent<Image>() : null;
            if (dot != null) dot.color = color;
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

            if (visible && Bars != null && Bars.Length == 4)
            {
                var gap = Mathf.Lerp(MinGap, MaxGap, Mathf.Clamp01(spread01));
                var half = gap + LineLength * 0.5f;
                Bars[0].anchoredPosition = new Vector2(-half, 0f);
                Bars[1].anchoredPosition = new Vector2(half, 0f);
                Bars[2].anchoredPosition = new Vector2(0f, half);
                Bars[3].anchoredPosition = new Vector2(0f, -half);
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
