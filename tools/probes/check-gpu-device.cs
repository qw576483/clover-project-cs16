// 一次性探针（.ai-tmp/test/）：环境基线 —— 渲染设备 + 分辨率 + 内存。
// 只读，不改进程状态。判据用途：证明"卡"是不是软件渲染造成。
var r = UnityEngine.Screen.currentResolution;
var sb = new System.Text.StringBuilder();
sb.Append(UnityEngine.SystemInfo.graphicsDeviceName);
sb.Append((char)124);
sb.Append(UnityEngine.SystemInfo.graphicsDeviceType);
sb.Append((char)124);
sb.Append(UnityEngine.SystemInfo.graphicsDeviceVendor);
sb.Append((char)124);
sb.Append(UnityEngine.SystemInfo.graphicsMemorySize);
sb.Append((char)124);
sb.Append(UnityEngine.SystemInfo.processorCount);
sb.Append((char)124);
sb.Append(UnityEngine.SystemInfo.systemMemorySize);
sb.Append((char)124);
sb.Append(r.width);
sb.Append((char)120);
sb.Append(r.height);
return sb.ToString();
