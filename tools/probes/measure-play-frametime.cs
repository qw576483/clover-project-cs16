// 一次性探针（.ai-tmp/test/）：Play 模式采帧时间 —— 每帧记 unscaledDelta + FrameTimingManager 的 CPU/GPU 拆分。
// 采 200 帧或 10 秒（先到为准），结果写 .ai-tmp/test/perf-result.txt。只读，不改工程。
var outPath = @"c:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\perf-result.txt";
int n = 0;
float t0 = -1f;
var lines = new System.Collections.Generic.List<string>();
UnityEditor.EditorApplication.CallbackFunction tick = null;
tick = () =>
{
    n++;
    float now = (float)UnityEditor.EditorApplication.timeSinceStartup;
    if (t0 < 0f) t0 = now;
    if (n > 3)
    {
        UnityEngine.FrameTimingManager.CaptureFrameTimings();
        var ft = new UnityEngine.FrameTiming[1];
        var got = UnityEngine.FrameTimingManager.GetLatestTimings(1, ft);
        string cpu = got > 0 ? ft[0].cpuFrameTime.ToString("F2") : "na";
        string gpu = got > 0 ? ft[0].gpuFrameTime.ToString("F2") : "na";
        string cm = got > 0 ? ft[0].cpuMainThreadFrameTime.ToString("F2") : "na";
        string cr = got > 0 ? ft[0].cpuRenderThreadFrameTime.ToString("F2") : "na";
        lines.Add((UnityEngine.Time.unscaledDeltaTime * 1000f).ToString("F2") + "," + cpu + "," + gpu + "," + cm + "," + cr);
    }
    if (n >= 200 || (now - t0) > 10f)
    {
        UnityEditor.EditorApplication.update -= tick;
        var sw = new System.IO.StreamWriter(outPath);
        sw.WriteLine("unscaledDeltaMs,cpuFrameTimeMs,gpuFrameTimeMs,cpuMainThreadMs,cpuRenderThreadMs");
        foreach (var s in lines) sw.WriteLine(s);
        sw.WriteLine("frames=" + lines.Count + " elapsedSec=" + (now - t0).ToString("F2"));
        sw.Close();
    }
};
UnityEditor.EditorApplication.update += tick;
return "probe-registered:" + outPath;
