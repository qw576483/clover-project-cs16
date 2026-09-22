// 一次性探针（.ai-tmp/test/）：读 .ai-tmp/test/probe-cam.txt 的 `x,y,z,tx,ty,tz,fov` 摆取证相机并回读读数。
var p = System.IO.Path.Combine(@"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test", "probe-cam.txt");
var parts = System.IO.File.ReadAllText(p).Trim().Split(',');
if (parts.Length < 6) return "ERROR: probe-cam.txt 需要 x,y,z,tx,ty,tz[,fov]";
float fx(int i) { return float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture); }
var cx = fx(0); var cy = fx(1); var cz = fx(2);
var lx = fx(3); var ly = fx(4); var lz = fx(5);
var fov = parts.Length > 6 ? fx(6) : 58.72f;
var go = UnityEngine.GameObject.Find("Cs16ProbeCam");
if (go == null) go = new UnityEngine.GameObject("Cs16ProbeCam");
if (!go.activeSelf) go.SetActive(true);
var cam = go.GetComponent<UnityEngine.Camera>();
if (cam == null) cam = go.AddComponent<UnityEngine.Camera>();
cam.depth = 100f;
cam.fieldOfView = fov;
cam.nearClipPlane = 0.05f;
cam.farClipPlane = 500f;
cam.clearFlags = UnityEngine.CameraClearFlags.Skybox;
go.transform.position = new UnityEngine.Vector3(cx, cy, cz);
go.transform.LookAt(new UnityEngine.Vector3(lx, ly, lz));
return "cam pos=(" + cx + "," + cy + "," + cz + ") look=(" + lx + "," + ly + "," + lz + ") fov=" + fov
       + " mode=" + (UnityEditor.EditorApplication.isPlaying ? "PLAY" : "EDIT")
       + " allCams=" + UnityEngine.Camera.allCamerasCount;
