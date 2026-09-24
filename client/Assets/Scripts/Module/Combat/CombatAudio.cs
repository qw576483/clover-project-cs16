using System.Collections.Generic;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Player;
using UnityEngine;

namespace Cs16.Module.Combat
{
    /// <summary>
    /// 战斗音效的**发放闸门**。
    ///
    /// <para><b>为什么不能直接 <c>Game.Sound.PlaySFX(name)</c></b>：引擎的资源层在"路径加载失败"时
    /// 会打一条 <c>Error: 加载失败：Sound/SFX/xxx</c>。开枪是每秒 10+ 次的高频路径 ——
    /// 只要 agent-07 的音频还没落地，日志就会被这种 Error 刷爆，真问题反而看不见。</para>
    ///
    /// <para><b>做法</b>：每种音效**首次用到时探测一次**（异步加载 + 缓存判定），
    /// 之后只对"确实存在"的音效调用 <c>Game.Sound</c>；缺失的音效**只告警一次**并静音。
    /// 因此：资源齐了就正常响，没齐也不刷屏。</para>
    /// </summary>
    internal sealed class CombatAudio
    {
        private enum ClipState
        {
            Unknown = 0,
            Loading = 1,
            Ready = 2,
            Missing = 3,
        }

        // SFX 根目录前缀真源 = ResPaths.SoundSfxPrefix（带尾斜杠的"前缀 + 短名"用法）——

        private readonly CsModuleLog _log = new CsModuleLog("Combat");
        private readonly Dictionary<string, ClipState> _states = new Dictionary<string, ClipState>(32);
        private bool _warnedNoRes;
        private bool _warnedNoSound;

        /// <summary>以 2D 方式播放（自己的枪声 / 命中标记）。</summary>
        public void Play(string clipName) => PlayInternal(clipName, Vector3.zero, false);

        /// <summary>以 3D 方式在指定位置播放（别人的枪声 / 爆炸）。</summary>
        public void PlayAt(string clipName, Vector3 position) => PlayInternal(clipName, position, true);

        private void PlayInternal(string clipName, Vector3 position, bool spatial)
        {
            if (string.IsNullOrEmpty(clipName)) return;

            if (!_states.TryGetValue(clipName, out var state))
            {
                var res = Game.Res;
                if (res == null)
                {
                    if (!_warnedNoRes)
                    {
                        _warnedNoRes = true;
                        _log.Error("audio.nores", "Game.Res 为 null（CloverRes.Init 未执行？），音效将全部静音");
                    }
                    _states[clipName] = ClipState.Missing;
                    return;
                }

                // 首次用到：探测一次（顺带把 clip 装进资源缓存），本次不播（避免"探测即播放"的错帧）。
                _states[clipName] = ClipState.Loading;
                var path = ResPaths.SoundSfxPrefix + clipName;
                res.LoadAsset<AudioClip>(path, clip => OnProbeDone(clipName, clip));
                return;
            }

            if (state != ClipState.Ready) return;

            var sound = Game.Sound;
            if (sound == null)
            {
                if (!_warnedNoSound)
                {
                    _warnedNoSound = true;
                    _log.Error("audio.nosound", "Game.Sound 为 null（表现域未挂载？），音效不会播放");
                }
                return;
            }

            if (spatial) sound.PlaySFXAt(clipName, position);
            else sound.PlaySFX(clipName);
        }

        private void OnProbeDone(string clipName, AudioClip clip)
        {
            if (clip != null)
            {
                _states[clipName] = ClipState.Ready;
                return;
            }

            _states[clipName] = ClipState.Missing;
            _log.Warn("audio.missing." + clipName,
                $"音效资源缺失：{ResPaths.SoundSfxPrefix}{clipName}" +
                $"（约定由 agent-07 装在 Resources/{ResPaths.SoundSfxPrefix}），该音效静音");
        }
    }
}
