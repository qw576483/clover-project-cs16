using CloverEngine;
using Cs16.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Cs16.UI
{
    /// <summary>
    /// 回合结算参数（HUD → 面板的 <c>param</c> 载体）。
    ///
    /// <para>
    /// 为什么另开一个类型：<c>Events.RoundEnded</c> 的参数是 <c>(CsRoundEndReason, CsTeam)</c> 两个值，
    /// 比分与回合号还得从 <see cref="CsHudSnapshot"/> 补 —— 面板的 <c>OnOpen(param)</c> 只能收一个对象，
    /// 所以把"这一回合结算的完整信息"打包传进来，避免面板在 <c>OnOpen</c> 里到处探状态。
    /// 这是 UI 层自己的类型（**不是 Core 契约**，Core 里也没有 <c>CsRoundEndInfo</c> 的 UI 版）。
    /// </para>
    /// </summary>
    public sealed class CsRoundEndArgs
    {
        public CsRoundEndReason Reason;
        public CsTeam Winner;
        public int ScoreT;
        public int ScoreCT;
        public int RoundNumber;
    }

    /// <summary>
    /// 回合结算（规格 H12）：中央大字 <c>Counter-Terrorists Win</c> / <c>Terrorists Win</c> + 原因 + 比分。
    ///
    /// <para><b>生命周期</b>：由常驻的 <see cref="HudPanel"/> 在收到 <c>Events.RoundEnded</c> 时打开，
    /// 在 <c>Events.RoundStarted</c>（下一回合开始）时关闭 —— 面板自己不做计时，
    /// 因为"结算显示多久"是回合阶段（<see cref="CsRoundPhase.RoundEnd"/>）的事，由比赛模块决定。</para>
    ///
    /// <para><b>层</b>：<see cref="UILayer.Normal"/>。它是"压在 HUD 上的信息"，不是要抢输入权的弹窗；
    /// 用 Popup 会让它把买枪菜单顶掉，也会让 ESC 误判成"有遮挡"。</para>
    /// </summary>
    public class RoundEndPanel : CsPanelBase
    {
        [SerializeField] private Text _titleText;
        [SerializeField] private Text _reasonText;
        [SerializeField] private Text _scoreText;
        [SerializeField] private Text _roundText;
        [SerializeField] private Image _backdrop;

        private bool _subscribed;

        public override UILayer Layer => UILayer.Normal;

        public override void BuildLayout(RectTransform root)
        {
            var backdrop = UIFactory.CreatePanel("Backdrop", root, new Color(0f, 0f, 0f, 0.45f), false);
            UIFactory.Stretch(backdrop.rectTransform);
            _backdrop = backdrop;

            _titleText = CsHudTheme.CreateText("Title", root, "Round Over", 60, TextAnchor.MiddleCenter,
                CsHudTheme.TextMain);
            CsHudTheme.PlaceCenter(_titleText.rectTransform, new Vector2(0f, 70f), new Vector2(1400f, 78f));

            _reasonText = CsHudTheme.CreateText("Reason", root, string.Empty, 30, TextAnchor.MiddleCenter,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceCenter(_reasonText.rectTransform, new Vector2(0f, 16f), new Vector2(1200f, 40f));

            _scoreText = CsHudTheme.CreateText("Score", root, string.Empty, 44, TextAnchor.MiddleCenter,
                CsHudTheme.TextHud);
            CsHudTheme.PlaceCenter(_scoreText.rectTransform, new Vector2(0f, -44f), new Vector2(1200f, 56f));

            _roundText = CsHudTheme.CreateText("Round", root, string.Empty, 22, TextAnchor.MiddleCenter,
                CsHudTheme.TextDim);
            CsHudTheme.PlaceCenter(_roundText.rectTransform, new Vector2(0f, -96f), new Vector2(1200f, 30f));
        }

        public override void OnOpen(object param)
        {
            WarnIfNull(_titleText, "结算标题文本");
            WarnIfNull(_scoreText, "结算比分文本");

            Apply(param as CsRoundEndArgs);

            // 订阅一遍：面板开着时若又来一次 RoundEnded（重开回合等），界面立刻跟上
            if (!_subscribed && Game.Event != null)
            {
                Game.Event.On<CsRoundEndReason, CsTeam>(Events.RoundEnded, OnRoundEnded);
                _subscribed = true;
            }
        }

        public override void OnClose()
        {
            // Off 必须传同一方法引用（匿名 lambda 是 Off 不掉的）
            if (_subscribed && Game.Event != null)
            {
                Game.Event.Off<CsRoundEndReason, CsTeam>(Events.RoundEnded, OnRoundEnded);
            }
            _subscribed = false;
        }

        private void OnRoundEnded(CsRoundEndReason reason, CsTeam winner)
        {
            Apply(new CsRoundEndArgs
            {
                Reason = reason,
                Winner = winner,
                ScoreT = CsHudSnapshot.ScoreT,
                ScoreCT = CsHudSnapshot.ScoreCT,
                RoundNumber = CsHudSnapshot.RoundNumber,
            });
        }

        private void Apply(CsRoundEndArgs args)
        {
            if (args == null)
            {
                // param 不是 CsRoundEndArgs（被别处手工 Open 了）→ 用快照兜底，并留痕
                Game.Logger?.Warn(Tag,
                    $"{nameof(RoundEndPanel)} 没有拿到 {nameof(CsRoundEndArgs)} 参数，用 HUD 快照兜底显示");
                args = new CsRoundEndArgs
                {
                    Reason = CsRoundEndReason.None,
                    Winner = CsHudSnapshot.ScoreCT >= CsHudSnapshot.ScoreT ? CsTeam.CT : CsTeam.T,
                    ScoreT = CsHudSnapshot.ScoreT,
                    ScoreCT = CsHudSnapshot.ScoreCT,
                    RoundNumber = CsHudSnapshot.RoundNumber,
                };
            }

            var ctWin = args.Winner == CsTeam.CT;
            if (_titleText != null)
            {
                _titleText.text = ctWin ? "Counter-Terrorists Win" : "Terrorists Win";
                _titleText.color = ctWin ? CsHudTheme.TeamCT : CsHudTheme.TeamT;
            }
            if (_reasonText != null) _reasonText.text = CsHudTheme.ReasonText(args.Reason);
            if (_scoreText != null) _scoreText.text = $"CT {args.ScoreCT}  :  {args.ScoreT} T";
            if (_roundText != null) _roundText.text = $"第 {args.RoundNumber} 回合结束";

            Game.Logger?.Info(Tag,
                $"回合结算显示：{_titleText?.text}（{args.Reason}）比分 CT {args.ScoreCT} : {args.ScoreT} T（第 {args.RoundNumber} 回合）");
        }
    }
}
