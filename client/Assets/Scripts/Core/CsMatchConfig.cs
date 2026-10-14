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
        /// <summary>
        /// **每回合开局**发放的起始金钱。
        ///
        /// <para>本工程口径 = 满额：原版 <c>mp_startmoney</c> 默认 800，且只在**开局与换边**发放一次，局内靠胜负 /
        /// 连败 / 击杀奖累积；这里改成**每回合开局都发放**，默认值取 <see cref="CsConst.MaxMoney"/>
        /// （= <c>mp_maxmoney</c> 上限 16000）⇒ 每回合开局金钱直接给满。经济链其余部分
        /// （上限夹取 / 购买扣钱 / 各项奖励）一律不动。登记 = 策划/差异登记.tsv #193。</para>
        /// </summary>
        public int StartMoney = CsConst.MaxMoney;
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
