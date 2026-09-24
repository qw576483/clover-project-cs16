using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.UI
{
    /// <summary>
    /// <see cref="CsPlayerSettings"/> 的持久化读写（<c>Game.Setting</c> 的强类型包装）。
    /// 这里**没有「语言」设置键，是本工程有意为之**：A（CS 1.6）本体没有游戏内语言设置项
    /// （原版 Options = 7 个子页 .res、**没有语言页**，依据见 <c>OptionsPanel</c> 的 <c>TabNames</c> 注释），
    ///
    /// <para>
    /// 为什么要有它：<c>Game.Setting</c> 只支持基础类型（string / int / float / bool），
    /// 装不下整个 <see cref="CsPlayerSettings"/> 对象，所以必须逐字段搬运 ——
    /// 而键名只应该在一个地方定义（Bootstrap 加载、Options 保存、主菜单显示玩家名三处都要用）。
    /// </para>
    ///
    /// <para>
    /// 放在 UI 层的理由：Core 下的文件是本工程的契约（由主 agent 写死、其它 agent 不许改），
    /// </para>
    /// </summary>
    public static class CsPlayerSettingsStore
    {
        private const string Tag = "UI";


        /// <summary>默认玩家名（用户没填也没存过时用）。</summary>
        public const string DefaultPlayerName = "Player";

        /// <summary>玩家名最大长度（与 UI 输入框的 characterLimit 保持一致）。</summary>
        public const int MaxNameLength = 16;

        public const float MinSensitivity = 0.1f;
        public const float MaxSensitivity = 20f;
        public const int MinFov = 60;
        public const int MaxFov = 120;

        /// <summary>从 <c>Game.Setting</c> 读一份设置（缺失项用 <see cref="CsPlayerSettings"/> 的字段默认值）。</summary>
        public static CsPlayerSettings Load()
        {
            var s = new CsPlayerSettings();
            var setting = Game.Setting;
            if (setting == null)
            {
                Game.Logger?.Error(Tag, "Game.Setting 不可用（Game.Launch 未完成？），玩家设置回退默认值");
                return s;
            }

            // 键名唯一真源 = CsSettingsKeys（Core 契约层）：本处与 Module/Player 读的是同一批常量。
            s.PlayerName = setting.Get(CsSettingsKeys.PlayerName, s.PlayerName);
            s.MouseSensitivity = setting.Get(CsSettingsKeys.Sensitivity, s.MouseSensitivity);
            s.InvertMouseY = setting.Get(CsSettingsKeys.InvertY, s.InvertMouseY);
            s.MasterVolume = setting.Get(CsSettingsKeys.VolumeMaster, s.MasterVolume);
            s.SfxVolume = setting.Get(CsSettingsKeys.VolumeSfx, s.SfxVolume);
            s.BgmVolume = setting.Get(CsSettingsKeys.VolumeBgm, s.BgmVolume);
            s.Fov = setting.Get(CsSettingsKeys.Fov, s.Fov);
            s.ShowFps = setting.Get(CsSettingsKeys.ShowFps, s.ShowFps);
            s.AutoReload = setting.Get(CsSettingsKeys.AutoReload, s.AutoReload);

            Normalize(s);
            return s;
        }

        /// <summary>写盘（顺带把越界值夹回合法范围；<c>Game.Setting.Save</c> 只在有改动时真正落盘）。</summary>
        public static void Save(CsPlayerSettings s)
        {
            if (s == null)
            {
                Game.Logger?.Error(Tag, "Save 收到 null 的 CsPlayerSettings，忽略");
                return;
            }

            var setting = Game.Setting;
            if (setting == null)
            {
                Game.Logger?.Error(Tag, "Game.Setting 不可用，玩家设置没写进去");
                return;
            }

            Normalize(s);

            // 键名唯一真源 = CsSettingsKeys（Core 契约层），与 Load 逐条同键。
            setting.Set(CsSettingsKeys.PlayerName, s.PlayerName);
            setting.Set(CsSettingsKeys.Sensitivity, s.MouseSensitivity);
            setting.Set(CsSettingsKeys.InvertY, s.InvertMouseY);
            setting.Set(CsSettingsKeys.VolumeMaster, s.MasterVolume);
            setting.Set(CsSettingsKeys.VolumeSfx, s.SfxVolume);
            setting.Set(CsSettingsKeys.VolumeBgm, s.BgmVolume);
            setting.Set(CsSettingsKeys.Fov, s.Fov);
            setting.Set(CsSettingsKeys.ShowFps, s.ShowFps);
            setting.Set(CsSettingsKeys.AutoReload, s.AutoReload);
            setting.Save();

            Game.Logger?.Info(Tag,
                $"玩家设置已保存: name={s.PlayerName} sens={s.MouseSensitivity:0.##} invertY={s.InvertMouseY} " +
                $"vol(master/sfx/bgm)={s.MasterVolume:0.##}/{s.SfxVolume:0.##}/{s.BgmVolume:0.##} " +
                $"fov={s.Fov} showFps={s.ShowFps} autoReload={s.AutoReload}");
        }

        /// <summary>
        /// 把"能立刻生效"的设置推给引擎。<see cref="SoundGroup"/> 只有 BGM / SFX / Voice 三组、
        /// 没有独立 Master 组，因此主音量作为总乘数作用于三组。
        /// </summary>
        public static void ApplyToEngine(CsPlayerSettings s)
        {
            if (s == null) return;

            var sound = Game.Sound;
            if (sound == null)
            {
                Game.Logger?.Warn(Tag, "Game.Sound 不可用（表现域未挂载？），音量设置未应用到引擎");
                return;
            }

            sound.SetVolume(SoundGroup.BGM, s.BgmVolume * s.MasterVolume);
            sound.SetVolume(SoundGroup.SFX, s.SfxVolume * s.MasterVolume);
            sound.SetVolume(SoundGroup.Voice, s.SfxVolume * s.MasterVolume);
        }

        public static void Normalize(CsPlayerSettings s)
        {
            if (s == null) return;

            if (string.IsNullOrWhiteSpace(s.PlayerName))
                s.PlayerName = DefaultPlayerName;
            else
                s.PlayerName = s.PlayerName.Trim();
            if (s.PlayerName.Length > MaxNameLength)
            {
                Game.Logger?.Warn(Tag, $"玩家名过长（{s.PlayerName.Length} > {MaxNameLength}），已截断");
                s.PlayerName = s.PlayerName.Substring(0, MaxNameLength);
            }

            s.MouseSensitivity = ClampLog(s.MouseSensitivity, MinSensitivity, MaxSensitivity, "鼠标灵敏度");
            s.MasterVolume = ClampLog(s.MasterVolume, 0f, 1f, "主音量");
            s.SfxVolume = ClampLog(s.SfxVolume, 0f, 1f, "音效音量");
            s.BgmVolume = ClampLog(s.BgmVolume, 0f, 1f, "背景音乐音量");
            if (s.Fov < MinFov || s.Fov > MaxFov)
            {
                Game.Logger?.Warn(Tag, $"FOV 越界（{s.Fov} 不在 [{MinFov},{MaxFov}]），夹到 {CsConst.DefaultFov}");
                s.Fov = Mathf.Clamp(s.Fov, MinFov, MaxFov);
            }
        }

        private static float ClampLog(float value, float min, float max, string what)
        {
            if (value >= min && value <= max) return value;
            var clamped = Mathf.Clamp(value, min, max);
            Game.Logger?.Warn(Tag, $"{what} 越界（{value} 不在 [{min},{max}]），夹到 {clamped}");
            return clamped;
        }
    }
}
