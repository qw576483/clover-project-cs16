using CloverEngine;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 引擎署名行（逐字 `by clover-engine`）的**常驻件**：一个独立小画布 + <c>DontDestroyOnLoad</c>，
    /// 进游戏后一直贴在屏幕底部居中。
    ///
    /// <para><b>为什么是常驻件而不是各面板各加一行</b>：署名要求「所有页面常驻」（切页面 / 开关面板 /
    /// 读条 / 进游戏全都在）。在面板里各加一行必然漏 —— 新增面板时没人记得补，而且面板一关那一行就没了。
    /// 本件与任何面板无关：面板开关、切场景、引擎 <c>Game.UI</c> 的批量关闭都带不走它。</para>
    ///
    /// <para><b>层级</b>：<c>sortingOrder = 1</c>，高于引擎 <c>[UI]</c> 画布的 <c>0</c>
    /// （<c>Runtime/Presentation/UI.cs</c> 的 <c>canvas.sortingOrder = 0</c>）⇒ 压在所有面板之上；
    /// <b>不挂 <c>GraphicRaycaster</c></b>，且行本身的 <c>raycastTarget = false</c>
    /// （<c>UIFactory.CreateText</c> 的默认值）⇒ 永不挡点击。</para>
    ///
    /// <para><b>画布适配</b>取引擎同一份参数（<see cref="CloverPresentation.ReferenceResolution"/> /
    /// <see cref="CloverPresentation.MatchWidthOrHeight"/>）—— 写死 1920×1080 会在竖版 / 其它参考分辨率的
    /// 项目上与面板的缩放不一致，署名的字号与底距就对不上了。</para>
    ///
    /// <para>行本身由引擎 <c>UIFactory.CreateCreditLabel</c> 建（文案 = 引擎默认值逐字
    /// <c>by clover-engine</c>；字体经本工程的 <see cref="CsUiStyle.CreateCreditLabel"/> 传原版字体链）。</para>
    /// </summary>
    public static class CsCreditBadge
    {
        private const string Tag = "UI";
        private const string RootName = "CsCreditBadge";

        /// <summary>本件画的层的排序值：引擎 <c>[UI]</c> 是 0，取 1 即压在其上。</summary>
        private const int SortingOrder = 1;

        /// <summary>署名行文本框宽（一行短文本，只需包住它）。</summary>
        private const float LabelWidth = 600f;

        /// <summary>流程页（主菜单 / 选阵营 / 设置…）的署名位置：底部居中。</summary>
        private const float MenuBottomOffset = 56f;

        /// <summary>游戏内署名位置：左下角，贴边小字，避开视野中央与 HUD 血量行。</summary>
        private const float InGameLeftInset = 10f;
        private const float InGameBottomOffset = 70f;
        private const int InGameFontSize = 13;

        private static GameObject _root;
        private static Text _label;
        private static Color _menuColor;
        private static int _menuFontSize;

        /// <summary>建常驻署名（幂等：已建则直接复用，重复调用不会出第二行）。</summary>
        public static void Ensure()
        {
            if (_root != null) return;

            _root = new GameObject(RootName);
            Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.referenceResolution = CloverPresentation.ReferenceResolution;
            scaler.matchWidthOrHeight = CloverPresentation.MatchWidthOrHeight;

            var label = CsUiStyle.CreateCreditLabel(_root.transform, 22, MenuBottomOffset);
            if (label == null)
            {
                Game.Logger?.Error(Tag, "引擎署名常驻件建不出文本节点（CreateCreditLabel 返回 null）");
                return;
            }

            _label = label;
            _menuColor = label.color;
            _menuFontSize = label.fontSize;

            Game.Logger?.Info(Tag,
                $"引擎署名常驻件已建：sortingOrder={canvas.sortingOrder} " +
                $"参考分辨率={scaler.referenceResolution.x}×{scaler.referenceResolution.y} " +
                $"match={scaler.matchWidthOrHeight} 文案='{label.text}' " +
                $"底距={label.rectTransform.anchoredPosition.y:F1} 字号={label.fontSize} " +
                $"raycast={label.raycastTarget}");
        }

        /// <summary>
        /// 切换署名的落点：<paramref name="inGame"/> = true 时贴屏幕左下角（小字、低对比），
        /// false 时回到流程页的底部居中。幂等，可在每次面板打开 / 关闭时调用。
        /// </summary>
        public static void SetInGame(bool inGame)
        {
            if (_label == null)
            {
                // 常驻件还没建（Bootstrap 尚未跑）：记录意图由调用方在建好后再调一次。
                Game.Logger?.Warn(Tag, $"引擎署名常驻件尚未建立，忽略 SetInGame({inGame})");
                return;
            }

            if (inGame)
            {
                _label.fontSize = InGameFontSize;
                _label.alignment = TextAnchor.MiddleLeft;
                _label.color = new Color(_menuColor.r, _menuColor.g, _menuColor.b, 0.55f);
                CsUiStyle.AnchoredBottom(_label.rectTransform,
                    new Vector2(InGameLeftInset, InGameBottomOffset),
                    new Vector2(LabelWidth, InGameFontSize + 7f), TextAnchor.LowerLeft);
            }
            else
            {
                _label.fontSize = _menuFontSize;
                _label.alignment = TextAnchor.MiddleCenter;
                _label.color = _menuColor;
                CsUiStyle.AnchoredBottom(_label.rectTransform,
                    new Vector2(0f, MenuBottomOffset),
                    new Vector2(LabelWidth, _menuFontSize + 10f), TextAnchor.LowerCenter);
            }
        }
    }
}
