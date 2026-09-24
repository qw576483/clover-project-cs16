// 只读，不改进程状态。
// 为什么必须有：bw-gate / bot-ai-r 都实测过“同机并发改 .cs ⇒ 域重载 ⇒ Play 被掐掉 ⇒ 采到
// no local / no match”。在那之后拍到的任何帧都必须作废；本探针把“这一帧是不是真的在 Play 里”
// 变成可机械判定的字符串，不再靠“脚本没报错”这种叙述。
var sb = new System.Text.StringBuilder();
sb.Append("isPlaying=").Append(UnityEngine.Application.isPlaying);
sb.Append((char)124).Append("editorPlaying=").Append(UnityEditor.EditorApplication.isPlaying);
sb.Append((char)124).Append("scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
sb.Append((char)124).Append("frame=").Append(UnityEngine.Time.frameCount);
sb.Append((char)124).Append("realtime=").Append(UnityEngine.Time.realtimeSinceStartup.ToString("F1"));
sb.Append((char)124).Append("gpu=").Append(UnityEngine.SystemInfo.graphicsDeviceName);
return sb.ToString();
