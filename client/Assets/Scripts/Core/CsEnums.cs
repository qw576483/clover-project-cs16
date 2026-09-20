namespace Cs16.Core
{
    /// <summary>阵营（官方 CS 三类）。</summary>
    public enum CsTeam
    {
        Spectator = 0,
        T = 1,
        CT = 2,
    }

    /// <summary>回合阶段。</summary>
    public enum CsRoundPhase
    {
        None = 0,
        Freeze = 1,     // 买枪/冻结时间：玩家不能移动，可买枪
        Live = 2,       // 回合进行
        RoundEnd = 3,   // 回合结算展示
        MatchEnd = 4,   // 比赛结束
    }

    /// <summary>回合胜负原因（决定 HUD 文案）。</summary>
    public enum CsRoundEndReason
    {
        None = 0,
        BombExploded = 1,          // 炸弹爆炸 → T 胜
        BombDefused = 2,           // 拆包成功 → CT 胜
        AllTargetsEliminated = 3,  // 全歼
        TimeExpired = 4,           // 时间到 → CT 胜
    }

    /// <summary>机器人难度（3 档，用户点名必须可切）。</summary>
    public enum CsBotDifficulty
    {
        Easy = 0,
        Normal = 1,
        Hard = 2,
    }

    /// <summary>武器大类（买枪菜单分类依据）。</summary>
    public enum CsWeaponClass
    {
        Knife = 0,
        Pistol = 1,
        SMG = 2,
        Rifle = 3,
        Shotgun = 4,
        MachineGun = 5,
        Sniper = 6,
        Grenade = 7,
        Equipment = 8,
        Bomb = 9,
    }

    /// <summary>武器槽位（对应键盘 1-5）。</summary>
    public enum CsWeaponSlot
    {
        Primary = 1,    // 主武器
        Secondary = 2,  // 手枪
        Knife = 3,
        Grenade = 4,
        Bomb = 5,       // C4
    }

    /// <summary>命中部位（伤害倍率不同）。</summary>
    public enum CsHitbox
    {
        Generic = 0,
        Head = 1,
        Chest = 2,
        Stomach = 3,
        Leg = 4,
    }

    /// <summary>仅某一方可购买的武器标记。</summary>
    public enum CsTeamLimit
    {
        Any = 0,
        TerroristOnly = 1,
        CounterTerroristOnly = 2,
    }
}
