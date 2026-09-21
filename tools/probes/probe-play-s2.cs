// 判据资产（tools/probes/）：S2「性能」维度的**只读**运行时读数（切片P）。
//
// 判据用途（对应 策划/实体清单.tsv 的 S2 行 帧时间 / 分辨率 / 内存）：
//   * 帧时间 —— 必须与**渲染设备名**同时给：设备是 Microsoft Basic Render Driver / WARP 时，
//     机器上任何帧时间数字都无效（skill §4 性能类 / experience/perf-triage.md）。
//     所以本探针把 `SystemInfo.graphicsDeviceName` 与帧时间打在**同一行**上。
//   * 分辨率 —— Screen.width/height + 主 Canvas 的 scaleFactor / referenceResolution（1:1 的判据）。
//   * 内存 —— Profiler 的已分配 / 保留 / Mono 三档（同一时刻取，避免"三行三个时刻"）。
//   * 帧时间同时给 FrameTimingManager 的 CPU/GPU 拆分（与 tools/probes/measure-play-frametime.cs 同口径）。
//
// 只读：不点按钮、不写 state.txt、不改任何进程状态。用 eval_file 调（脚本式，无 using）。
var inv = System.Globalization.CultureInfo.InvariantCulture;
var sb = new System.Text.StringBuilder();

// ---- ① 渲染设备（帧时间数字有没有意义的那一条）----
sb.Append("device=").Append(UnityEngine.SystemInfo.graphicsDeviceName)
  .Append(" api=").Append(UnityEngine.SystemInfo.graphicsDeviceType)
  .Append(" vramMB=").Append(UnityEngine.SystemInfo.graphicsMemorySize.ToString(inv));

// ---- ② 分辨率 / 画布 1:1 ----
sb.Append("\nscreen=").Append(UnityEngine.Screen.width).Append('x').Append(UnityEngine.Screen.height)
  .Append(" fullScreen=").Append(UnityEngine.Screen.fullScreen)
  .Append(" dpi=").Append(UnityEngine.Screen.dpi.ToString(inv));
var canvases = UnityEngine.Object.FindObjectsByType<UnityEngine.Canvas>(UnityEngine.FindObjectsInactive.Include);
for (var i = 0; i < canvases.Length; i++)
{
    var c = canvases[i];
    if (c == null) continue;
    var sc = c.GetComponent<UnityEngine.UI.CanvasScaler>();
    sb.Append("|canvas[").Append(c.gameObject.name).Append("] scaleFactor=").Append(c.scaleFactor.ToString("F4", inv))
      .Append(" pixelRect=").Append(c.pixelRect.width.ToString("F0", inv)).Append('x').Append(c.pixelRect.height.ToString("F0", inv));
    if (sc != null)
    {
        sb.Append(" refRes=").Append(sc.referenceResolution.x.ToString("F0", inv)).Append('x')
          .Append(sc.referenceResolution.y.ToString("F0", inv));
    }
}

// ---- ③ 帧时间（本帧）+ FrameTimingManager 拆分 ----
var frameMs = UnityEngine.Time.unscaledDeltaTime * 1000f;
sb.Append("\nframe=").Append(UnityEngine.Time.frameCount)
  .Append(" unscaledDeltaMs=").Append(frameMs.ToString("F3", inv))
  .Append(" fps=").Append((1f / UnityEngine.Mathf.Max(0.0001f, UnityEngine.Time.unscaledDeltaTime)).ToString("F1", inv))
  .Append(" timeScale=").Append(UnityEngine.Time.timeScale.ToString("F2", inv));
try
{
    UnityEngine.FrameTimingManager.CaptureFrameTimings();
    var ft = new UnityEngine.FrameTiming[1];
    var got = UnityEngine.FrameTimingManager.GetLatestTimings(1, ft);
    sb.Append("\nframeTiming got=").Append(got);
    if (got > 0)
    {
        sb.Append(" cpuFrameTimeMs=").Append(ft[0].cpuFrameTime.ToString("F3", inv))
          .Append(" gpuFrameTimeMs=").Append(ft[0].gpuFrameTime.ToString("F3", inv))
          .Append(" cpuMainThreadMs=").Append(ft[0].cpuMainThreadFrameTime.ToString("F3", inv))
          .Append(" cpuRenderThreadMs=").Append(ft[0].cpuRenderThreadFrameTime.ToString("F3", inv));
    }
}
catch (System.Exception e) { sb.Append("\nframeTiming ERROR ").Append(e.Message); }

// ---- ④ 内存（三档同一时刻）----
try
{
    sb.Append("\nmem totalAllocatedMB=")
      .Append((UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576.0).ToString("F1", inv))
      .Append(" totalReservedMB=")
      .Append((UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / 1048576.0).ToString("F1", inv))
      .Append(" monoUsedMB=")
      .Append((UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / 1048576.0).ToString("F1", inv))
      .Append(" gfxDriverMB=")
      .Append((UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / 1048576.0).ToString("F1", inv));
}
catch (System.Exception e) { sb.Append("\nmem ERROR ").Append(e.Message); }

return sb.ToString();
