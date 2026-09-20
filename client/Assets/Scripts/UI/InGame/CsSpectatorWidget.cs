using CloverEngine;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 观战 HUD（规格 H11，策划案里叫 <c>SpectatorWidget</c>）：死亡后居中显示
    /// <c>观战中 - [名字]</c> 与切换目标的按键提示。
    ///
    /// <para>
    /// 数据源：<see cref="Cs16.Core.CsHudSnapshot.IsSpectating"/> /
    /// <see cref="Cs16.Core.CsHudSnapshot.SpectateName"/>（由比赛模拟写）。
    /// </para>
    ///
    /// <para>
    /// <b>注意</b>：本件只负责"显示切换提示"，不处理空格键 —— 切观战目标是
    /// <c>ICsMatch.SpectateNext(dir)</c>，而 <c>Core/Events.cs</c> 里没有对应的 UI→比赛 事件常量，
    /// 按分层铁律 UI 不许直接调 Module，因此这一步留给操作模块（Module/Player 读键）或主 agent 补事件常量。
    /// 见本文件的交付说明。
    /// </para>
    /// </summary>
    [System.Serializable]
    public sealed class CsSpectatorWidget
    {
        public RectTransform Root;
        public Text Banner;
        public Text Hint;

        [System.NonSerialized] private bool _warned;

        public void Build(RectTransform parent)
        {
            if (parent == null)
            {
                Game.Logger?.Error("UI", "CsSpectatorWidget.Build 收到 null 父节点，观战提示不会显示");
                return;
            }

            var root = UIFactory.CreatePanel("Spectator", parent, CsHudTheme.PanelBgSoft, false);
            CsHudTheme.PlaceTopCenter(root.rectTransform, new Vector2(0f, -150f), new Vector2(620f, 84f));
            Root = root.rectTransform;

            Banner = CsHudTheme.CreateText("Banner", Root, "观战中", 34, TextAnchor.MiddleCenter,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceTopCenter(Banner.rectTransform, new Vector2(0f, -6f), new Vector2(600f, 42f));

            Hint = CsHudTheme.CreateText("Hint", Root, "[空格] 切换目标", 20, TextAnchor.MiddleCenter,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceTopCenter(Hint.rectTransform, new Vector2(0f, -50f), new Vector2(600f, 28f));
        }

        public void Refresh(bool visible, string targetName)
        {
            if (Root == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Game.Logger?.Error("UI", "CsSpectatorWidget 引用缺失（预制体未由 UiBuilder 生成或已被改动），观战提示不会显示");
                }
                return;
            }

            if (Root.gameObject.activeSelf != visible) Root.gameObject.SetActive(visible);
            if (!visible) return;

            if (Banner != null)
                Banner.text = string.IsNullOrEmpty(targetName) ? "观战中" : $"观战中 - {targetName}";
        }
    }
}
