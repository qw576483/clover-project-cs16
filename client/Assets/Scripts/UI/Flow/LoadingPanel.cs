using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 读条面板：地图名 + 真实进度条 + 轮播提示。
    /// <list type="bullet">
    /// <item><see cref="Layer"/> = <see cref="UILayer.System"/>：读条要盖住一切（含暂停菜单）。</item>
    /// <item>进度由 <c>AppFlow</c> 用 <c>Game.Scene.Load</c> 的 <c>onProgress</c> 直接喂进来
    /// （<see cref="SetProgress"/>），不是假进度条。</item>
    /// <item>进度条用**锚点宽度**而不是 <c>Image.fillAmount</c>：本工程零美术资源、
    /// sprite 为空，而空 sprite 的 <c>Image</c> 会走 <c>Graphic.OnPopulateMesh</c> 的实心四边形分支、
    /// 完全忽略 <c>fillAmount</c>（看着就是"进度条永不动"）。</item>
    /// </list>
    /// </summary>
    public class LoadingPanel : CsPanelBase
    {
        /// <summary>提示轮播间隔（秒，不受 timeScale 影响）。</summary>
        private const float TipInterval = 2f;

        private static readonly string[] Tips =
        {
            "提示：按 B 打开买枪菜单（仅买枪时间 + 买枪区内有效）",
            $"提示：炸弹 {CsConst.BombTimer:0} 秒爆炸，拆包要 {CsConst.DefuseTime:0} 秒（带拆弹器 {CsConst.DefuseTimeWithKit:0} 秒）",
            "提示：Shift 慢走不会发出脚步声",
            "提示：连续点射比按住扫射准得多",
            $"提示：回合时间 {CsConst.RoundTime:0} 秒，半场 {CsConst.RoundsPerHalf} 局",
        };

        /// <summary>原版背景拼图（12 块 TGA，见 <see cref="CsMenuBackground"/>；原版读条界面用的是同一套图）。</summary>
        [SerializeField] private CsMenuBackground _background;
        [SerializeField] private Text _mapName;
        [SerializeField] private Text _tip;
        [SerializeField] private Text _percent;
        [SerializeField] private RectTransform _barFill;

        private long _tipTimerId = -1;
        private int _tipIndex;
        private float _progress;

        /// <summary>读条要盖住一切（含暂停菜单）。</summary>
        public override UILayer Layer => UILayer.System;

        /// <summary>当前进度（0~1），供自检/日志用。</summary>
        public float Progress => _progress;

        public override void BuildLayout(RectTransform root)
        {
            // 第 0 层：纯色兜底（与主菜单同款；原版读条底也是 `backgroundlayout.txt` 那 12 块 TGA，
            // 原版 `backgroundloadinglayout.txt` 与之逐字相同 ⇒ 两个界面共用同一套拼图）。只在贴图缺失时可见。
            CsUiStyle.CreateFullScreen("Backdrop", root, CsUiStyle.Backdrop);

            // 第 1 层：原版背景拼图。必须建在 Backdrop 之后、标题/进度条之前（拼图在最底层）。
            _background = CsMenuBackground.Attach(root);

            CsUiStyle.CreateLabel("Title", root, "LOADING", 44, new Vector2(90f, -80f),
                new Vector2(800f, 60f), TextAnchor.MiddleLeft, CsUiStyle.Text);
            CsUiStyle.CreateBoxRect("TitleBar", root, new Vector2(92f, -152f), new Vector2(520f, 4f), CsUiStyle.Accent);
            _mapName = CsUiStyle.CreateLabel("MapName", root, CsConst.MapDust2, 30, new Vector2(92f, -168f),
                new Vector2(900f, 44f), TextAnchor.MiddleLeft, CsUiStyle.Accent);

            CsUiStyle.CreateLabel("ProgressLabel", root, "正在加载地图数据…", 22, new Vector2(92f, -890f),
                new Vector2(900f, 32f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);
            _tip = CsUiStyle.CreateLabel("Tip", root, string.Empty, 20, new Vector2(92f, -860f),
                new Vector2(1500f, 30f), TextAnchor.MiddleLeft, CsUiStyle.TextDim);

            var track = CsUiStyle.CreateBoxRect("BarTrack", root, new Vector2(90f, -940f),
                new Vector2(1740f, 26f), CsUiStyle.Field);
            var fill = CsUiStyle.CreateFullScreen("BarFill", track.transform, CsUiStyle.Accent, false);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = Vector2.zero;
            fill.rectTransform.offsetMin = Vector2.zero;
            fill.rectTransform.offsetMax = Vector2.zero;
            _barFill = fill.rectTransform;

            _percent = CsUiStyle.CreateLabel("Percent", root, "0%", 22, new Vector2(1660f, -978f),
                new Vector2(170f, 30f), TextAnchor.MiddleRight, CsUiStyle.Text);
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_barFill, "进度条填充");
            WarnIfNull(_mapName, "地图名文本");

            // 背景拼图：生成期已绑好就什么都不做；缺哪块才补哪块（逐块报出文件名，失败保留纯色兜底）。
            if (_background != null) _background.EnsureSprites();
            else Game.Logger?.Warn(Tag, "原版背景拼图组件缺失（预制体未由 FlowSetup 生成？），读条界面只有纯色底");

            var mapName = param as string;
            if (string.IsNullOrEmpty(mapName))
            {
                mapName = CsConst.MapDust2;
                Game.Logger.Warn(Tag, $"{GetType().Name} 未收到地图名参数（param 应为 string），显示默认地图名");
            }
            if (_mapName != null) _mapName.text = mapName;

            _tipIndex = 0;
            ShowTip();
            _tipTimerId = Game.Timer.EveryUnscaled(TipInterval, ShowTip);

            SetProgress(0f);
        }

        public override void OnClose()
        {
            if (_tipTimerId < 0) return;
            Game.Timer.Stop(_tipTimerId);
            _tipTimerId = -1;
        }

        /// <summary>写进度（0~1）。由 AppFlow 用场景加载的真实进度驱动。</summary>
        public void SetProgress(float progress01)
        {
            _progress = Mathf.Clamp01(progress01);
            CsUiStyle.SetBarWidth(_barFill, _progress);
            if (_percent != null) _percent.text = $"{Mathf.RoundToInt(_progress * 100f)}%";
        }

        private void ShowTip()
        {
            if (_tip == null || Tips.Length == 0) return;
            _tip.text = Tips[_tipIndex % Tips.Length];
            _tipIndex++;
        }
    }
}
