using CloverEngine;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 观战 HUD（规格 H11，策划案里叫 <c>SpectatorWidget</c>）：死亡后居中显示
    /// <c>观战中 - [名字]</c>、当前**观战档位**与换目标的按键提示。
    ///
    /// <para>
    /// 数据源：目标名走 <see cref="Cs16.Core.CsHudSnapshot.IsSpectating"/> /
    /// <see cref="Cs16.Core.CsHudSnapshot.SpectateName"/>（由比赛模拟写）；
    /// 档位名走 <see cref="Cs16.Core.CsSpectator.Mode"/> —— 写入者有**两处**：观战/阵亡态按
    /// <c>[空格]</c> 循环切档（<see cref="Cs16.Core.CsSpectator.TryCycleModeOnJump"/>），
    /// 以及控制台 <c>spec_mode</c>。
    /// </para>
    ///
    /// <para>
    /// <b>层</b>：本件只显示，不读键、不切目标 —— 换目标是
    /// <c>Events.SpectateNext</c> / <c>Events.SpectatePrev</c> / <c>Events.SpectateTarget</c>
    /// 三条 UI→视图模块 的事件（UI 不许引用 <c>Cs16.Module.*</c>）。
    /// </para>
    /// </summary>
    [System.Serializable]
    public sealed class CsSpectatorWidget
    {
        /// <summary>
        /// 观战按键提示。键位出处 = 原版观战帮助文本
        /// （<c>原版资源/cs16src/cstrike/cstrike__resource__cstrike_english.txt:922-926</c>）：
        /// <c>FIRE1 - Chase next player</c> / <c>FIRE2 - Chase previous player</c> /
        /// <c>JUMP - Change view modes</c>。
        /// </summary>
        private const string KeysHint = "[左键] 下一个目标　[右键] 上一个目标　[空格] 切换观战档位";

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

            Hint = CsHudTheme.CreateText("Hint", Root, ModeLine(), 20, TextAnchor.MiddleCenter,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceTopCenter(Hint.rectTransform, new Vector2(0f, -50f), new Vector2(600f, 28f));
        }

        /// <summary>
        /// 档位 + 按键那一行。档位名用原版本地化串原文（<c>#Spec_Mode%d</c> →
        /// <c>cstrike_english.txt:941-946</c>，如 "Free Chase Cam"）—— 盘上没有中文档位名，
        /// 本工程不自造译名。
        /// </summary>
        private static string ModeLine()
        {
            var name = Cs16.Core.CsSpectator.ModeName(Cs16.Core.CsSpectator.Mode);
            return string.IsNullOrEmpty(name) ? KeysHint : name + "　" + KeysHint;
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

            // 档位由控制台 spec_mode 改，不在本控件里缓存：每帧读一次状态，避免"控件与状态各存一份"。
            if (Hint != null) Hint.text = ModeLine();
        }
    }
}
