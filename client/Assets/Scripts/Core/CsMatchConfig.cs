namespace Cs16.Core
{
    /// <summary>
    /// 一场比赛的配置。来源：New Game 面板 → Flow 模块 → 传给 ICsMatch.Start。
    /// 字段全部为平铺基础类型，便于 Game.Setting 持久化。
    /// </summary>
    [System.Serializable]
    public class CsMatchConfig
    {
        public string MapName = CsConst.MapDust2;

        /// <summary>每队机器人数量（会按阵营补足）。0 = 只打了真人。</summary>
        public int BotsPerTeam = 4;

        public CsBotDifficulty BotDifficulty = CsBotDifficulty.Normal;

        public int RoundsPerHalf = CsConst.RoundsPerHalf;
        public float RoundTime = CsConst.RoundTime;
        public float FreezeTime = CsConst.FreezeTime;
        public int StartMoney = CsConst.StartMoney;
        public bool FriendlyFire = false;

        /// <summary>玩家名字（显示在记分板/HUD）。</summary>
        public string PlayerName = "Player";

        /// <summary>玩家选择/被分配的阵营。</summary>
        public CsTeam PlayerTeam = CsTeam.CT;

        /// <summary>半场交换：到 RoundsPerHalf 局时自动换边。</summary>
        public bool HalfTimeSwap = true;

        public CsMatchConfig Clone()
        {
            return (CsMatchConfig)MemberwiseClone();
        }
    }

    /// <summary>玩家设置（Options 面板写入 Game.Setting）。</summary>
    [System.Serializable]
    public class CsPlayerSettings
    {
        public string PlayerName = "Player";
        public float MouseSensitivity = CsConst.DefaultSensitivity;
        public bool InvertMouseY = false;
        public float MasterVolume = 1f;
        public float SfxVolume = 1f;
        public float BgmVolume = 0.6f;
        public int Fov = (int)CsConst.DefaultFov;
        public bool ShowFps = true;
        public bool AutoReload = true;
    }
}
