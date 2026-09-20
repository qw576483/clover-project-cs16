using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 比赛结束（规格 H13）：<c>Counter-Terrorists Win!</c> / <c>Terrorists Win!</c> + 最终比分 +
    /// <c>回主菜单</c> / <c>再来一局</c>。
    ///
    /// <para><b>为什么是 Popup</b>：比赛已经结束，玩家必须在这里做一个选择（回主菜单 / 再来一局），
    /// 不该还能点开买枪菜单或对着战场点来点去。Popup 层的遮罩正好表达"现在归结算画面"。</para>
    ///
    /// <para><b>比分来源</b>：<c>Events.MatchEnded</c> 是**无参**事件（契约如此），
    /// 所以最终比分从 <see cref="CsHudSnapshot.ScoreCT"/> / <see cref="CsHudSnapshot.ScoreT"/> 读 ——
    /// 比赛结束时快照仍在刷新（<c>CsMatch</c> 只在 <c>Stop()</c> 里 Reset），因此是最终值。</para>
    /// </summary>
    public class MatchEndPanel : CsPanelBase
    {
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _scoreText;
        [SerializeField] private Text _detailText;
        [SerializeField] private Button _backToMainButton;
        [SerializeField] private Button _rematchButton;

        public override UILayer Layer => UILayer.Popup;

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.72f), true);
            UIFactory.Stretch(backdrop.rectTransform);

            var box = UIFactory.CreatePanel("Dialog", root, CsUiStyle.Box, true);
            CsHudTheme.PlaceCenter(box.rectTransform, Vector2.zero, new Vector2(880f, 460f));
            var boxRt = box.rectTransform;

            _titleText = CsHudTheme.CreateText("Title", boxRt, "Match Over", 58, TextAnchor.MiddleCenter,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceCenter(_titleText.rectTransform, new Vector2(0f, 140f), new Vector2(820f, 76f));

            _scoreText = CsHudTheme.CreateText("Score", boxRt, "CT 0 : 0 T", 52, TextAnchor.MiddleCenter,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceCenter(_scoreText.rectTransform, new Vector2(0f, 52f), new Vector2(820f, 66f));

            _detailText = CsHudTheme.CreateText("Detail", boxRt, string.Empty, 22, TextAnchor.MiddleCenter,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceCenter(_detailText.rectTransform, new Vector2(0f, -8f), new Vector2(820f, 30f));

            _rematchButton = CsHudTheme.CreateButton("Btn_Rematch", boxRt, "再来一局", new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(-190f, -110f), new Vector2(340f, 58f), null);
            _backToMainButton = CsHudTheme.CreateButton("Btn_BackToMain", boxRt, "回主菜单",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(190f, -110f),
                new Vector2(340f, 58f), null, accent: true);

            var hint = CsHudTheme.CreateText("Hint", boxRt,
                "回主菜单会清掉本场比分；再来一局用当前配置（地图 / bot 数 / 难度）重开",
                18, TextAnchor.MiddleCenter, CsHudTheme.TextDim);
            CsHudTheme.PlaceCenter(hint.rectTransform, new Vector2(0f, -170f), new Vector2(820f, 28f));
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_titleText, "比赛结束标题");
            WarnIfNull(_scoreText, "最终比分文本");

            Bind(_backToMainButton, OnBackToMain, "回主菜单");
            Bind(_rematchButton, OnRematch, "再来一局");

            // 比赛结束：光标必须能用（结算画面要点击选路）
            CsIngameCursor.UnlockForMenu(nameof(MatchEndPanel));

            var ct = CsHudSnapshot.ScoreCT;
            var t = CsHudSnapshot.ScoreT;

            if (_titleText != null)
            {
                if (ct == t)
                {
                    _titleText.text = "Draw!";
                    _titleText.color = CsHudTheme.TextMain;
                }
                else
                {
                    _titleText.text = ct > t ? "Counter-Terrorists Win!" : "Terrorists Win!";
                    _titleText.color = ct > t ? CsHudTheme.TeamCT : CsHudTheme.TeamT;
                }
            }

            if (_scoreText != null) _scoreText.text = $"CT {ct}  :  {t} T";
            if (_detailText != null)
            {
                _detailText.text = $"共 {CsHudSnapshot.RoundNumber} 回合　本场比分已按回合结算";
            }

            Game.Logger?.Info(Tag, $"比赛结束画面：{_titleText?.text} 最终比分 CT {ct} : {t} T");
        }

        public override void OnClose()
        {
            CsIngameCursor.RelockIfIdle(nameof(MatchEndPanel), CsIngameCursor.AnyMenuOpen());
        }

        private void OnBackToMain()
        {
            Game.Logger?.Info(Tag, "比赛结束 → 回主菜单（发 Flow.BackToMain，由 Flow 清场）");
            Game.UI.Close<MatchEndPanel>();
            Game.Event.Emit(Events.BackToMain);
        }

        private void OnRematch()
        {
            // RestartMatch 由比赛模块接（它对已开局的重开 = 先 Stop 再 Start），Flow 不用重走读条
            Game.Logger?.Info(Tag, "比赛结束 → 再来一局（发 Game.RestartMatch）");
            Game.UI.Close<MatchEndPanel>();
            Game.Event.Emit(Events.RestartMatch);
            Game.UI.Toast("已重开比赛", 2f);
        }
    }
}
