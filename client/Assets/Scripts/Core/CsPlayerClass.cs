namespace Cs16.Core
{
    /// <summary>
    /// 本地玩家在「选兵种」页选的兵种（原版 <c>joinclass N</c>：N = 1..5，6 = 自动/随机）。
    ///
    /// <para><b>为什么单独一个文件</b>：兵种在上层 UI（选兵种页）产生、在视图层（皮肤）消费，
    /// 两边都不该直接依赖对方 —— 这里只存"哪一侧 + 第几行"这一个纯数据；
    /// "兵种 → 本工程皮肤键"的资产映射在 <c>Module/View/CsClassSkins</c>。</para>
    /// </summary>
    public static class CsPlayerClass
    {
        /// <summary>没有显式选择：还没进过选兵种页，或点了「自动选择」（原版 <c>joinclass 6</c>，由服务端分配）。</summary>
        public const int None = 0;

        private static bool _has;
        private static CsTeam _team;
        private static int _joinClass;

        /// <summary>记下本地玩家选的兵种。<paramref name="joinClass"/> = 1..5；<see cref="None"/> = 清掉（回随机皮肤）。</summary>
        public static void Set(CsTeam team, int joinClass)
        {
            if (joinClass <= None || team == CsTeam.Spectator)
            {
                Clear();
                return;
            }
            _has = true;
            _team = team;
            _joinClass = joinClass;
        }

        /// <summary>取<paramref name="team"/>这一侧的选择；没选过 / 选的是另一侧 / 选的是自动 ⇒ false（调用方自己随机）。</summary>
        public static bool TryGet(CsTeam team, out int joinClass)
        {
            joinClass = _joinClass;
            return _has && team != CsTeam.Spectator && team == _team;
        }

        /// <summary>清空（换局 / 回主菜单时调用，避免上一局的兵种串到下一局）。</summary>
        public static void Clear()
        {
            _has = false;
            _team = CsTeam.Spectator;
            _joinClass = None;
        }
    }
}
