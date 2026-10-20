using CloverEngine;
using UnityEngine;

namespace Cs16.Module.Audio
{
    /// <summary>
    /// 闪光弹致盲期间的耳鸣：只给本地听者、2D 播放、随致盲剩余时间线性衰减。
    ///
    /// <para><b>采样是程序化合成的</b>：4 kHz 正弦 + 5% 噪声的 2 秒循环，首次需要时由
    /// <c>AudioClip.Create</c> 生成（44100 Hz、单声道）。原版闪光耳鸣采样不在盘 ⇒
    /// **这是本项目合成的近似，不是原版采样**。</para>
    ///
    /// <para><b>环回无缝</b>：4 kHz 在 2 秒里正好 8000 个周期，正弦相位在环回点连续；
    /// 噪声在首尾各 <see cref="CsAudioTuning.TinnitusEdgeFadeSamples"/> 个采样内线性收敛到 0，
    /// 环回处不会咔哒。</para>
    ///
    /// <para><b>音量</b>：<see cref="CsAudioTuning.TinnitusVolume"/> × 致盲剩余比例 × 引擎分组音量；
    /// 分组静音时停止。<see cref="Stop"/> 停音源并把音量归零，不留残响。</para>
    /// </summary>
    internal sealed class CsTinnitus
    {
        private const string Tag = "Audio";

        private GameObject _root;
        private AudioSource _source;
        private AudioClip _clip;

        /// <summary>按致盲剩余时间推进。参数全 ≤ 0 / 已恢复 ⇒ 停止并保持静音。</summary>
        /// <param name="remaining">致盲剩余秒数（&lt;= 0 = 没被闪）。</param>
        /// <param name="span">本次致盲总时长（秒；&lt;= 0 时按"刚开始"处理）。</param>
        public void Tick(float remaining, float span)
        {
            if (remaining <= 0f)
            {
                Stop();
                return;
            }

            var src = EnsureSource();
            if (src == null) return;

            var group = 1f;
            var sound = Game.Sound;
            if (sound != null)
            {
                if (sound.IsMuted(SoundGroup.SFX))
                {
                    Stop();
                    return;
                }
                group = Mathf.Clamp01(sound.GetVolume(SoundGroup.SFX));
            }

            var left = span > 0.0001f ? Mathf.Clamp01(remaining / span) : 1f;
            src.volume = CsAudioTuning.TinnitusVolume * left * group;
            if (!src.isPlaying) src.Play();
        }

        /// <summary>停播并把音量归零（音源与采样保留，下次被闪时直接复用）。</summary>
        public void Stop()
        {
            if (_source == null) return;
            if (_source.isPlaying) _source.Stop();
            _source.volume = 0f;
        }

        /// <summary>释放音源对象与合成采样（模块销毁 / 回主菜单时调用）。</summary>
        public void Dispose()
        {
            Stop();
            if (_root != null) Object.Destroy(_root);
            if (_clip != null) Object.Destroy(_clip);
            _root = null;
            _source = null;
            _clip = null;
        }

        private AudioSource EnsureSource()
        {
            if (_source != null) return _source;

            if (Game.Sound == null)
            {
                LogThrottle.WarnOnce(Tag, "tinnitus.nosound",
                    "Game.Sound 为 null（表现域未挂载？），闪光耳鸣不会播放");
                return null;
            }

            _clip = BuildClip();
            if (_clip == null) return null;

            _root = new GameObject("[CsTinnitus]") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(_root);

            _source = _root.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;      // 2D：耳鸣在耳边，不随位置衰减
            _source.dopplerLevel = 0f;
            _source.clip = _clip;
            _source.volume = 0f;
            return _source;
        }

        private static AudioClip BuildClip()
        {
            var rate = CsAudioTuning.TinnitusSampleRate;
            var count = Mathf.RoundToInt(rate * CsAudioTuning.TinnitusLoopSeconds);
            if (count <= 0)
            {
                LogThrottle.WarnOnce(Tag, "tinnitus.count",
                    $"耳鸣循环长度无效（{CsAudioTuning.TinnitusLoopSeconds}s × {rate}Hz），闪光耳鸣不会播放");
                return null;
            }

            var fade = Mathf.Max(1, CsAudioTuning.TinnitusEdgeFadeSamples);
            var toneGain = 1f - CsAudioTuning.TinnitusNoiseAmount;
            var rng = new System.Random(0x43533136);
            var data = new float[count];

            for (var i = 0; i < count; i++)
            {
                var phase = 2.0 * System.Math.PI * CsAudioTuning.TinnitusToneHz * i / rate;
                var sine = (float)System.Math.Sin(phase) * toneGain;

                var edge = 1f;
                if (i < fade) edge = (float)i / fade;
                else if (count - 1 - i < fade) edge = (float)(count - 1 - i) / fade;

                var noise = ((float)rng.NextDouble() * 2f - 1f) * CsAudioTuning.TinnitusNoiseAmount * edge;
                data[i] = Mathf.Clamp(sine + noise, -1f, 1f);
            }

            var clip = AudioClip.Create("cs16_tinnitus", count, 1, rate, false);
            if (clip == null)
            {
                LogThrottle.WarnOnce(Tag, "tinnitus.clip",
                    "耳鸣采样生成失败（AudioClip.Create 返回 null），闪光耳鸣不会播放");
                return null;
            }
            clip.SetData(data, 0);
            return clip;
        }
    }
}
