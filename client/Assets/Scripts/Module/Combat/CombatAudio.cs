using CloverEngine;
using Cs16.Core;
using Cs16.Module.Audio;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 战斗音效的**薄转发**：记账（缺失只报一次、单帧起播上限、同 clip 并发上限）全部归引擎
    /// <see cref="ISoundManager"/>，缺失探测与"资源在不在"的判定只在 <see cref="SfxService"/> 一处
    /// （它问 <c>Game.Res.Exists</c>，结果由引擎按路径缓存）。
    ///
    /// <para><b>为什么不能直接 <c>Game.Sound.PlaySFX(name)</c></b>：引擎资源层在"路径加载失败"时
    /// 是<b>每次调用</b>一条 <c>Error("Resource", "加载失败：{path}")</c>。开枪是每秒 10+ 次的高频路径 ——
    /// 只要音频资源还没落地，日志就会被这种 Error 刷爆，真问题反而看不见。所以转发前先由
    /// <see cref="SfxService"/> 问一次 <c>Game.Res.Exists</c>：缺失时只告警一次并静音。</para>
    /// </summary>
    internal sealed class CombatAudio
    {
        private readonly SfxService _sfx = new SfxService("Combat");

        /// <summary>以 2D 方式播放（自己的枪声 / 命中标记）。</summary>
        public void Play(string clipName) => _sfx.Play(clipName);

        /// <summary>以 3D 方式在指定位置播放（别人的枪声 / 弹着）：衰减口径与 <see cref="SfxService"/> 一致
        /// （<see cref="CsAudioTuning.SpatialMinDistance"/> / <see cref="CsAudioTuning.SpatialMaxDistance"/>）。</summary>
        public void PlayAt(string clipName, Vector3 position) => _sfx.PlayAt(clipName, position);
    }
}
