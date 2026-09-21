using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 受伤提示：屏幕四边红色渐隐 + 朝向伤害来源的红色标记。
    ///
    /// <para>
    /// 数据源：<see cref="CsHudSnapshot.DamageIndicatorTime"/>（剩余显示时间）与
    /// <see cref="CsHudSnapshot.DamageFromYaw"/>（**相对玩家朝向**的方位角，正 = 右侧；
    /// 由 <c>CsDamage.WriteLocalDamageIndicator</c> 用 <c>victim.Yaw</c> 归一化后写入）。
    /// </para>
    ///
    /// <para>
    /// 四边红色用<b>纯色 + alpha</b> 表达"渐隐"：没有 sprite 做不了径向渐变，而
    /// 半透明红框逐帧降 alpha 在观感上就是"红一下然后退掉"，与 CS 1.6 一致（原版也是一圈红闪）。
    /// </para>
    /// </summary>
    [System.Serializable]
    public sealed class CsDamageIndicatorWidget
    {
        /// <summary>边缘红框厚度（像素）。</summary>
        public const float EdgeThickness = 110f;
        /// <summary>方向标记到屏幕中心的距离（像素）。</summary>
        public const float MarkerRadius = 190f;
        /// <summary>最大不透明度（压太实会挡住画面）。</summary>
        public const float MaxAlpha = 0.45f;

        public RectTransform Root;
        public Image Top;
        public Image Bottom;
        public Image Left;
        public Image Right;
        public RectTransform DirectionMarker;
        public Image DirectionMarkerImage;

        [System.NonSerialized] private bool _warned;

        public void Build(RectTransform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Error("UI", "CsDamageIndicatorWidget.Build 收到 null 父节点，受伤提示不会显示");
                return;
            }

            var root = UIFactory.CreateNode("DamageIndicator", parent);
            UIFactory.Stretch(root);
            Root = root;

            var red = new Color(CsHudTheme.Danger.r, CsHudTheme.Danger.g, CsHudTheme.Danger.b, 0f);
            Top = CreateEdge("EdgeTop", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, EdgeThickness), red);
            Bottom = CreateEdge("EdgeBottom", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, EdgeThickness), red);
            Left = CreateEdge("EdgeLeft", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(EdgeThickness, 0f), red);
            Right = CreateEdge("EdgeRight", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f),
                new Vector2(EdgeThickness, 0f), red);

            var marker = UIFactory.CreatePanel("DamageDir", root, red, false);
            CsHudTheme.PlaceCenter(marker.rectTransform, new Vector2(0f, MarkerRadius), new Vector2(46f, 12f));
            DirectionMarker = marker.rectTransform;
            DirectionMarkerImage = marker;
        }

        private Image CreateEdge(string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 sizeDelta, Color color)
        {
            var img = UIFactory.CreatePanel(name, Root, color, false);
            var rt = img.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = sizeDelta;
            return img;
        }

        /// <summary>
        /// 每帧刷新。
        /// </summary>
        /// <param name="timeLeft"><see cref="CsHudSnapshot.DamageIndicatorTime"/>。</param>
        /// <param name="yawDeg"><see cref="CsHudSnapshot.DamageFromYaw"/>（度，正 = 右侧）。</param>
        public void Refresh(float timeLeft, float yawDeg)
        {
            if (Root == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Game.Logger?.Error("UI", "CsDamageIndicatorWidget 引用缺失（预制体未由 UiBuilder 生成或已被改动），受伤提示不会显示");
                }
                return;
            }

            var visible = timeLeft > 0f;
            if (Root.gameObject.activeSelf != visible) Root.gameObject.SetActive(visible);
            if (!visible) return;

            var t = Mathf.Clamp01(timeLeft / Mathf.Max(0.0001f, CsConst.DamageIndicatorTime));
            var alpha = MaxAlpha * t;
            SetAlpha(Top, alpha);
            SetAlpha(Bottom, alpha);
            SetAlpha(Left, alpha);
            SetAlpha(Right, alpha);

            if (DirectionMarker != null)
            {
                // 相对朝向的方位：0° = 正前（屏幕上方），+90° = 右侧
                var rad = yawDeg * Mathf.Deg2Rad;
                var dir = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
                DirectionMarker.anchoredPosition = dir * MarkerRadius;
                // 长边与半径垂直（像罗盘上的刻度），指向中心
                DirectionMarker.localRotation = Quaternion.Euler(0f, 0f, -yawDeg);
            }
            if (DirectionMarkerImage != null)
            {
                var c = DirectionMarkerImage.color;
                c.a = alpha + 0.25f * t;
                DirectionMarkerImage.color = c;
            }
        }

        private static void SetAlpha(Image img, float alpha)
        {
            if (img == null) return;
            var c = img.color;
            c.a = alpha;
            img.color = c;
        }
    }
}
