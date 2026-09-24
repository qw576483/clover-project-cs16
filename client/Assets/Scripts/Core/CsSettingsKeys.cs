namespace Cs16.Core
{
    /// <summary>
    /// 玩家设置键名的**唯一真源**（<c>Game.Setting</c> 的键；改键名只改这里）。
    ///
    /// <para><b>为什么要有这个文件（改前状态）</b>：同一条键面原先在**两处各写一套** ——
    /// UI 层的 <c>CsPlayerSettingsStore</c>（读盘 / 写盘 / 面板绑定）与
    /// <c>Module/Player/PlayerModule</c>（每帧把设置生效到操作上）。
    /// 两处一旦漂移，现象是"设置改了不生效 / 重启后丢失"，而且**不报任何错**
    /// （读到的永远是字段默认值，看起来像"面板没写进去"）。</para>
    ///
    /// <para><b>为什么放 <c>Core/</c></b>：分层铁律是 <c>UI/**</c> 与 <c>Module/**</c> 互不引用
    /// （<c>Module/Player</c> 不许 <c>using Cs16.UI</c>），所以单一真源既不能放 UI 也不能放 Module，
    /// 只能落在两边都允许引用的 <c>Core/</c>（契约层）。</para>
    ///
    /// <para><b>键面沿用原样</b>：<c>cs.player.&lt;字段&gt;</c> —— 与改前 <c>CsPlayerSettingsStore.KeyPrefix</c>
    /// 及 <c>PlayerModule</c> 的常量逐字一致；⛔ 不许改（改了 = 老存档全部读不到，玩家设置静默回默认值）。</para>
    /// </summary>
    public static class CsSettingsKeys
    {
        /// <summary>公共前缀：键面 = <c>cs.player.&lt;字段&gt;</c>。</summary>
        public const string Prefix = "cs.player.";

        /// <summary>玩家名（<c>CsPlayerSettings.PlayerName</c>）。</summary>
        public const string PlayerName = Prefix + "name";

        /// <summary>鼠标灵敏度（<c>CsPlayerSettings.MouseSensitivity</c>）。</summary>
        public const string Sensitivity = Prefix + "sensitivity";

        /// <summary>是否反转鼠标 Y 轴（<c>CsPlayerSettings.InvertMouseY</c>）。</summary>
        public const string InvertY = Prefix + "invertY";

        /// <summary>主音量（<c>CsPlayerSettings.MasterVolume</c>）。</summary>
        public const string VolumeMaster = Prefix + "volumeMaster";

        /// <summary>音效音量（<c>CsPlayerSettings.SfxVolume</c>）。</summary>
        public const string VolumeSfx = Prefix + "volumeSfx";

        /// <summary>背景音乐音量（<c>CsPlayerSettings.BgmVolume</c>）。</summary>
        public const string VolumeBgm = Prefix + "volumeBgm";

        /// <summary>视野（<c>CsPlayerSettings.Fov</c>）。</summary>
        public const string Fov = Prefix + "fov";

        /// <summary>是否显示 FPS（<c>CsPlayerSettings.ShowFps</c>）。</summary>
        public const string ShowFps = Prefix + "showFps";

        /// <summary>是否自动换弹（<c>CsPlayerSettings.AutoReload</c>）。</summary>
        public const string AutoReload = Prefix + "autoReload";
    }
}
