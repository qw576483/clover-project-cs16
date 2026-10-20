using CloverEngine;
using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.Audio
{
    /// <summary>
    /// 音效播放的**薄转发**（不做任何自己的计数 / 记账 / 缓存）。
    ///
    /// <para><b>为什么这么薄</b>：记账（探测状态表、单帧计数、同音效并发滑动窗口、缺失告警计数）
    /// 全部归引擎 <c>Game.Sound</c>；<c>Module/Combat/CombatAudio.cs</c> 另有一份
    /// （**已有能力不够用时优先扩展既有实现，不准平行再起一套**）：
    /// <list type="bullet">
    /// <item>缺失只报一次 ⇒ 引擎 <c>LogThrottle.WarnOnce("Sound", "missing:&lt;path&gt;", …)</c>；</item>
    /// <item>单帧起播上限 / 同 clip 并发上限 ⇒ 引擎 <c>ISoundManager.MaxPlaysPerFrame</c> /
    /// <c>MaxConcurrentPerClip</c>（阈值仍是本项目的 <c>CsAudioTuning</c>，由
    /// <c>AudioModule.Start</c> 显式下发给引擎）。</item>
    /// </list>
    /// 所以本类**只剩转发**：一个"记账"字段都不留。</para>
    ///
    /// <para><b>为什么转发前先问一句 <c>Game.Res.Exists</c></b>：引擎资源层在加载失败时是
    /// <b>每次调用</b>一条 <c>Error("Resource", "加载失败：{path}")</c>（在
    /// <c>ResourceManager.CompletePending</c>，不在 <c>Sound</c> 里）—— 而脚步 / 命中 / 蜂鸣都是
    /// 每秒多次的高频路径，只要某个音效没落地，日志就会被它刷爆。这里问的 <c>Exists</c> 正是
    /// **引擎自己的按路径缓存**（只回答"在不在"，不占缓存、不动引用计数、同一路径只探一次）
    /// ⇒ "缺失"这件事的唯一真相仍**在引擎**，本类不记账，只在"引擎说没有"时把这件事实经引擎
    /// <c>LogThrottle.WarnOnce</c> 说一次然后静音。</para>
    ///
    /// <para><b>2D / 3D 都走引擎</b>：3D 用 <c>Game.Sound.PlaySFXAt(clip, pos, SfxAtOptions)</c> 显式给
    /// 距离衰减 / 音量 / 音高（<see cref="CsAudioTuning.SpatialMinDistance"/> /
    /// <see cref="CsAudioTuning.SpatialMaxDistance"/>）；2D 用 <c>PlaySFX</c>。</para>
    ///
    /// <para><b>与 <c>CombatAudio</c> 的分工</b>：枪声归 <c>Module/Combat</c>（它自带一份探测缓存）；
    /// 同一个模式应当也收敛到本类这条路径上。</para>
    /// </summary>
    internal sealed class SfxService
    {
        /// <summary><c>Game.Sound</c> 里 SFX 的根目录前缀真源 = <see cref="ResPaths.SoundSfxPrefix"/>
        /// （带尾斜杠的"前缀 + 短名"用法，与 <c>CsAudioTuning.ClipRoot</c> 同口径）。</summary>

        /// <summary>本转发层的日志 tag（与 <c>AudioModule</c> 一致，LogThrottle 用它分清来源）。</summary>
        private readonly string _tag;

        public SfxService(string tag)
        {
            _tag = string.IsNullOrEmpty(tag) ? "Audio" : tag;
        }

        /// <summary>2D 播放（自己的脚步 / 命中 / 回合播报）。</summary>
        public void Play(string clip) => Fire(clip, Vector3.zero, false, 1f, 1f);

        /// <summary>3D 定点播放（别人的脚步 / 死亡 / 爆炸）。</summary>
        public void PlayAt(string clip, Vector3 position) => Fire(clip, position, true, 1f, 1f);

        /// <summary>3D 定点播放 + 音高 / 音量缩放（逐材质脚步、手雷弹跳、烟雾弹释放、C4 蜂鸣档位）。
        /// 位置必须给：引擎按它算距离衰减。</summary>
        public void PlayAt(string clip, Vector3 position, float pitch, float volumeScale)
            => Fire(clip, position, true, pitch, volumeScale);

        /// <summary>随机取一个变体播放（脚步/死亡这类多采样音效，避免"复读机"）—— 选变体是玩法表现，不是闸门。
        /// <paramref name="pitch"/> / <paramref name="volumeScale"/> 为变形参数（默认不变形）。</summary>
        public void PlayRandom(string[] clips, bool spatial, Vector3 position, float pitch = 1f, float volumeScale = 1f)
        {
            if (clips == null || clips.Length == 0) return;
            // 选变体是**表现**（避免"复读机"），与玩法无关；但**必须**走 CsRng 而不是
            // UnityEngine.Random —— 后者是全局静态流，脚步/死亡每秒多次，用它抽会把
            // 玩法侧的散布/瞄准序列推位。独立一路 SfxVariant ⇒ 表现抽多少次都不动玩法流。
            var idx = clips.Length == 1 ? 0 : CsRng.Stream(CsRngStream.SfxVariant).Next(0, clips.Length);
            if (spatial) PlayAt(clips[idx], position, pitch, volumeScale);
            else Fire(clips[idx], position, false, pitch, volumeScale);
        }

        /// <summary>
        /// 预热：把"一定会用到"的音效顶进引擎的资源缓存，避免第一条声音被吃在加载里。
        /// 直接走引擎的批量预加载（<c>Preload</c> 只顶缓存、不长期持有引用），本类不记任何状态。
        /// </summary>
        public void Prewarm(params string[] clips)
        {
            if (clips == null || clips.Length == 0) return;

            var res = Game.Res;
            if (res == null) return;

            var paths = new System.Collections.Generic.List<string>(clips.Length);
            for (var i = 0; i < clips.Length; i++)
            {
                if (string.IsNullOrEmpty(clips[i])) continue;
                paths.Add(ResPaths.SoundSfxPrefix + clips[i]);
            }
            if (paths.Count == 0) return;

            res.Preload(paths, null);
        }

        private void Fire(string clip, Vector3 position, bool spatial, float pitch, float volumeScale)
        {
            if (string.IsNullOrEmpty(clip))
            {
                // 空短名 = 调用点拼错 / 该武器没有这一档采样（如 attack2 没有切换音的武器）。
                // 原本这里是静默 return，表现成"这条音效就是不响"而无从定位。
                LogThrottle.WarnOnce(_tag, "sfx.empty", "音效短名为空，本次播放已跳过（检查调用点的武器 / 档位）");
                return;
            }

            var res = Game.Res;
            if (res == null)
            {
                LogThrottle.WarnOnce(_tag, "sfx.nores", "Game.Res 为 null（CloverRes.Init 未执行？），音效将全部静音");
                return;
            }

            var path = ResPaths.SoundSfxPrefix + clip;

            // "在不在"问引擎：不在 ⇒ 静音 + 只报一次。
            // 必须问的原因见类注释（资源层对加载失败是"每次一条 Error"）。key 与引擎 Sound 的缺失
            // 告警同口径（"missing:<path>"），因此同一条路径在本进程内只会有一条告警。
            if (!res.Exists(path))
            {
                LogThrottle.WarnOnce(_tag, "missing:" + path,
                    $"音效资源缺失：Resources/{path}.wav —— 该音效静音。" +
                    "请先执行菜单 Clover/CS16/整理视图与音效资源");
                return;
            }

            var sound = Game.Sound;
            if (sound == null)
            {
                LogThrottle.WarnOnce(_tag, "sfx.nosound", "Game.Sound 为 null（表现域未挂载？），音效不会播放");
                return;
            }

            // 单帧上限 / 同 clip 并发上限都在引擎里判（默认 0 = 不限；本项目在 AudioModule.Start 下发阈值）。
            if (spatial)
            {
                sound.PlaySFXAt(clip, position, new SfxAtOptions(
                    CsAudioTuning.SpatialMinDistance,
                    CsAudioTuning.SpatialMaxDistance,
                    AudioRolloffMode.Logarithmic,
                    volumeScale,
                    pitch));
                return;
            }

            if (pitch != 1f || volumeScale != 1f)
            {
                // 非预期分支：2D 播放没有音高 / 音量形参，变形会被丢掉（当前调用点都不走到这里）。
                LogThrottle.WarnOnce(_tag, "sfx.2d.deform",
                    $"2D 音效「{clip}」请求了音高/音量变形（pitch={pitch:F2} volume={volumeScale:F2}），" +
                    "引擎 2D 播放接口没有这两个形参 ⇒ 按原样播放");
            }

            sound.PlaySFX(clip);
        }
    }
}
