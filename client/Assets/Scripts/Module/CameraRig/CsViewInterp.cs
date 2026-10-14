using Cs16.Core;
using UnityEngine;

namespace Cs16.Module.CameraRig
{
    /// <summary>
    /// 原版客户端的**视位回放**（<c>HLSDK/cl_dll/view.cpp</c> 的 <c>ViewInterp</c> 位置历史环形缓冲）
    /// 与**上台阶视高平滑**（同文件 "smooth out stair step ups"），外加站↔蹲的视高过渡时长
    /// （<c>pm_shared.c</c> 的 <c>TIME_TO_DUCK</c>）。
    ///
    /// <para><b>本类只做纯计算</b>（不依赖 Unity 生命周期、不引用 MonoBehaviour）：模拟侧把
    /// <c>CsActor.Position</c> 逐帧交给 <see cref="Record"/>，相机侧用 <see cref="Playback"/> 取回放位移，
    /// 两者都是可直接离线断言的（宿主 <c>.ai-tmp/hosts/movefix-check</c>）。</para>
    ///
    /// <para><b>三段机制与出处</b>：</para>
    /// <list type="number">
    /// <item><b>位置历史 + 按窗口插值回放</b>：<c>view.cpp:723-789</c> —— <c>ORIGIN_BACKUP 64</c> 的
    /// 环形缓冲（<c>:470-483</c>），只在 <c>Length(delta) != 0</c> 时入缓冲（<c>:727-736</c>），
    /// 回放把视位插到 <c>cl_vsmoothing</c> 秒前的历史位置上（<c>:752-789</c>），
    /// 位移 <c>≥ 64</c> unit 时不插值（<c>:777-778</c> "Dont interpolate large changes"）。</item>
    /// <item><b>上台阶平滑</b>：<c>view.cpp:698-720</c> —— 踩地且模拟原点高于当前视位时，
    /// 视位竖直以 <c>steptime * 150</c> unit/s 追赶、落后不超过 <c>18</c> unit；其余情况直接吸附。</item>
    /// <item><b>蹲姿过渡</b>：<c>pm_shared.c:82</c> 的 <c>TIME_TO_DUCK 0.4</c>（用在
    /// <c>PM_Duck</c> <c>:1676-1746</c>），形状取同文件的 <c>PM_SplineFraction</c>（<c>:1596-1604</c>）；
    /// 起身（<c>PM_UnDuck</c> <c>:1658-1661</c>）是把视高立即拨回 <c>VEC_VIEW</c>，不走这条曲线。</item>
    /// </list>
    ///
    /// <para><b>回放的门与原版逐字一致</b>：原版那一段的条件是
    /// <c>cl_vsmoothing-&gt;value &amp;&amp; pparams-&gt;smoothing &amp;&amp; maxclients &gt; 1</c>
    /// （<c>view.cpp:740-741</c>，注释逐字 "Smooth out whole view in multiplayer when on trains, lifts"）
    /// —— 单人 / 没有移动平台时恒不生效，视位就是模拟原点。调用方按同一口径传 <c>smoothing</c>。</para>
    /// </summary>
    internal sealed class CsViewInterp
    {
        /// <summary>位置历史环形缓冲的长度 = 原版 <c>ORIGIN_BACKUP</c>（view.cpp:470）。</summary>
        public const int OriginBackup = 64;

        /// <summary>环形缓冲下标掩码 = 原版 <c>ORIGIN_MASK = ORIGIN_BACKUP - 1</c>（view.cpp:471）。</summary>
        public const int OriginMask = OriginBackup - 1;

        /// <summary>回放窗口（秒）= 原版 cvar <c>cl_vsmoothing</c> 的出厂默认值 <c>0.05</c>：
        /// 载体 <c>原版资源/cs16src/cstrike/cl_dlls/client.dll</c>，名字串 file <c>0xE9DEC</c>
        /// 后接默认串 <c>"0.05"</c>、flags <c>0x89</c>。</summary>
        public const float DefaultWindow = 0.05f;

        /// <summary>可插值的位移上限（米）= 原版 <c>Length(delta) &lt; 64</c> unit（view.cpp:778）
        /// ⇒ <c>64 × 0.0254</c> = 1.6256 m。</summary>
        public const float MaxInterpDelta = 64f * CsConst.UnitToMeter;

        /// <summary>上台阶时视位竖直的追赶速率（米/秒）= 原版 <c>steptime * 150</c>（view.cpp:709，
        /// 单位 unit/s）⇒ <c>150 × 0.0254</c> = 3.81 m/s。</summary>
        public const float StepFollowSpeed = 150f * CsConst.UnitToMeter;

        /// <summary>上台阶时视位落后模拟位置的竖直上限（米）= 原版 <c>18</c> unit
        /// （view.cpp:712-713）⇒ 0.4572 m。</summary>
        public const float StepLagMax = 18f * CsConst.UnitToMeter;

        /// <summary>站↔蹲过渡时长（秒）= 原版 <c>TIME_TO_DUCK</c>（pm_shared.c:82）。</summary>
        public const float DuckTransitionSeconds = 0.4f;

        private readonly Vector3[] _origins = new Vector3[OriginBackup];
        private readonly float[] _times = new float[OriginBackup];
        private int _current;
        private Vector3 _lastRecorded;
        private bool _hasRecorded;

        private float _stepZ;
        private bool _hasStep;

        /// <summary>已写进环形缓冲的历史条数（自上次 <see cref="Reset"/> 起）。</summary>
        public int RecordCount => _current;

        /// <summary>清空历史与台阶状态（出生 / 复活 / 传送 / 换观察目标 / 进自由飞行）。</summary>
        public void Reset()
        {
            for (var i = 0; i < OriginBackup; i++)
            {
                _origins[i] = Vector3.zero;
                _times[i] = 0f;
            }

            _current = 0;
            _lastRecorded = Vector3.zero;
            _hasRecorded = false;
            _stepZ = 0f;
            _hasStep = false;
        }

        /// <summary>
        /// 记一次**模拟原点**（模拟侧入口）：与上一条记录相同的位置不重复入缓冲
        /// （原版判据是 <c>Length(delta) != 0</c>，view.cpp:727-736）。
        /// </summary>
        public void Record(Vector3 simOrigin, float time)
        {
            if (_hasRecorded && (simOrigin - _lastRecorded).sqrMagnitude == 0f) return;

            _origins[_current & OriginMask] = simOrigin;
            _times[_current & OriginMask] = time;
            _current++;
            _lastRecorded = simOrigin;
            _hasRecorded = true;
        }

        /// <summary>
        /// 按窗口回放历史位置（相机侧入口）：返回要加到视位上的位移，语义 = 原版
        /// <c>view.cpp:739-789</c> 的 <c>delta</c>（原版把它同时加到 <c>simorg</c> 与 <c>vieworg</c>）。
        ///
        /// <para>门不满足（<paramref name="smoothing"/> 为 false / 窗口 ≤ 0）、历史不足、
        /// 或插值段的位移 <c>≥ <see cref="MaxInterpDelta"/></c> 时返回零位移（视位取模拟原点）。</para>
        /// </summary>
        public Vector3 Playback(Vector3 simOrigin, float time, float window, bool smoothing)
        {
            if (!smoothing || !(window > 0f)) return Vector3.zero;

            var t = time - window;
            var foundidx = _current - 1;
            var i = 1;
            for (; i < OriginMask; i++)
            {
                foundidx = _current - 1 - i;
                if (_times[foundidx & OriginMask] <= t) break;
            }

            if (i >= OriginMask) return Vector3.zero;
            if (_times[foundidx & OriginMask] == 0f) return Vector3.zero;

            var dt = _times[(foundidx + 1) & OriginMask] - _times[foundidx & OriginMask];
            if (!(dt > 0f)) return Vector3.zero;

            var frac = (t - _times[foundidx & OriginMask]) / dt;
            if (frac > 1f) frac = 1f;

            var delta = _origins[(foundidx + 1) & OriginMask] - _origins[foundidx & OriginMask];
            var neworg = _origins[foundidx & OriginMask] + delta * frac;

            // Dont interpolate large changes（原版 view.cpp:777-778：位移 >= 64 unit 就整段丢弃）。
            if (delta.magnitude >= MaxInterpDelta) return Vector3.zero;

            return neworg - simOrigin;
        }

        /// <summary>
        /// 上台阶的视高平滑（原版 "smooth out stair step ups"，view.cpp:698-720）：踩在地面且模拟原点
        /// 高于当前视位（正在上台阶）时，视位竖直以 <see cref="StepFollowSpeed"/> 追赶、落后不超过
        /// <see cref="StepLagMax"/>；其余情况（下落 / 平移 / <paramref name="smoothing"/>）直接吸附。
        /// 返回要加到视位上的竖直位移（≤ 0）。
        /// </summary>
        public float StepOffset(float simZ, float dt, bool onGround, bool smoothing)
        {
            if (!_hasStep)
            {
                // 首帧对齐（原版 static oldz 初值 0 + lasttime 初值 0 ⇒ 第一帧 steptime 足够大、视位吸附）。
                _stepZ = simZ;
                _hasStep = true;
                return 0f;
            }

            if (!smoothing && onGround && simZ - _stepZ > 0f)
            {
                var steptime = dt > 0f ? dt : 0f;
                _stepZ += steptime * StepFollowSpeed;
                if (_stepZ > simZ) _stepZ = simZ;
                if (simZ - _stepZ > StepLagMax) _stepZ = simZ - StepLagMax;
            }
            else
            {
                _stepZ = simZ;
            }

            return _stepZ - simZ;
        }

        /// <summary>
        /// 推进蹲下进度（0 = 站立，1 = 蹲满），速率 = <see cref="DuckTransitionSeconds"/> 走完全程
        /// （原版 <c>flDuckTime</c> 从 1000 每帧按 msec 递减、<c>time = 1 − flDuckTime/1000</c>，
        /// pm_shared.c:1710-1714）。只向 1 推进：原版起身（<c>PM_UnDuck</c>）是立即的，不走这条曲线。
        /// </summary>
        public static float AdvanceDuckFraction(float current, float dt)
        {
            if (dt <= 0f) return current;

            var next = current + dt / DuckTransitionSeconds;
            return next > 1f ? 1f : next;
        }

        /// <summary>原版 <c>PM_SplineFraction</c>（pm_shared.c:1596-1604）：先把入参缩放，再取
        /// <c>3·v² − 2·v³</c>。用它把线性的蹲姿进度整成原版 <c>PM_Duck</c> 的过渡形状。</summary>
        public static float SplineFraction(float value, float scale)
        {
            var v = scale * value;
            var v2 = v * v;
            return 3f * v2 - 2f * v2 * v;
        }
    }
}
