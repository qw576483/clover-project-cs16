namespace Cs16.Core
{
    /// <summary>
    /// 全部事件名常量（唯一来源）。禁止在业务里写裸字符串。
    /// 命名规则：`域.动作`。域 = 归属方（Flow=流程编排，Game=比赛模拟，Ui=界面）。
    /// </summary>
    public static class Events
    {
        // ================= 流程（UI → Flow）=================
        public const string StartNewGame = "Flow.StartNewGame";
        public const string OpenServerList = "Flow.OpenServerList";
        public const string OpenOptions = "Flow.OpenOptions";
        public const string QuitGame = "Flow.QuitGame";
        public const string BackToMain = "Flow.BackToMain";
        public const string Disconnect = "Flow.Disconnect";
        public const string Resume = "Flow.Resume";
        public const string RequestPause = "Flow.RequestPause";
        public const string RequestScoreboard = "Ui.RequestScoreboard";

        /// <summary>参数：<see cref="Cs16.Core.CsMatchConfig"/></summary>
        public const string LaunchMatch = "Flow.LaunchMatch";
        /// <summary>参数：<see cref="Cs16.Core.CsTeam"/></summary>
        public const string TeamChosen = "Flow.TeamChosen";

        // ================= 游戏内操作（UI → Match）=================
        /// <summary>参数：string weaponId</summary>
        public const string BuyWeapon = "Game.BuyWeapon";
        /// <summary>参数：int 槽位（<see cref="Cs16.Core.CsAmmoBuy.PrimarySlot"/> / <see cref="Cs16.Core.CsAmmoBuy.SecondarySlot"/>）</summary>
        public const string BuyAmmo = "Game.BuyAmmo";
        /// <summary>参数：<see cref="Cs16.Core.CsBotDifficulty"/>（加上该难度的一队 bot）</summary>
        public const string AddBot = "Game.AddBot";
        /// <summary>踢出最后一个机器人</summary>
        public const string KickBot = "Game.KickBot";
        /// <summary>参数：<see cref="Cs16.Core.CsBotDifficulty"/></summary>
        public const string SetBotDifficulty = "Game.SetBotDifficulty";
        public const string RestartRound = "Game.RestartRound";
        public const string RestartMatch = "Game.RestartMatch";
        public const string TogglePause = "Game.TogglePause";
        public const string SpectateNext = "Game.SpectateNext";

        /// <summary>参数：string 地图名（H 菜单换图）</summary>
        public const string SwitchMap = "Game.SwitchMap";
        /// <summary>参数：CsTeam（换阵营）</summary>
        public const string ChangeTeam = "Game.ChangeTeam";
        /// <summary>参数：string 无线电文本</summary>
        public const string RadioCommand = "Game.RadioCommand";

        // ================= 比赛 → 表现层（Match → UI/View）=================
        public const string MatchStarted = "Game.MatchStarted";
        /// <summary>参数：int roundNumber</summary>
        public const string RoundStarted = "Game.RoundStarted";
        /// <summary>
        /// （RoundEndPanel 必须按这个签名订阅；`CsRoundEndInfo` 在 Module.Match 里，UI 按分层铁律不许引用。）
        /// </summary>
        public const string RoundEnded = "Game.RoundEnded";
        public const string MatchEnded = "Game.MatchEnded";
        public const string LocalDied = "Game.LocalDied";
        public const string LocalRespawned = "Game.LocalRespawned";
        public const string BombPlanted = "Game.BombPlanted";
        public const string BombDefused = "Game.BombDefused";
        public const string BombExploded = "Game.BombExploded";
        /// <summary>参数：string 文本（击杀/无线电/系统提示）</summary>
        public const string GameMessage = "Game.Message";

        // ================= 界面互发 =================
        public const string CloseBuyMenu = "Ui.CloseBuyMenu";
        public const string CloseHMenu = "Ui.CloseHMenu";
    }
}
