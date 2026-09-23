// 判据资产（tools/probes/，片BU-V 新建）：**按对象名**把图标宿主的 hideFlags 来回切，
// 用来做"逐对象隔离"的同帧 A/B（bv-r 的 probe-hideflags-ab.cs 只能按组件类别批量切，
// 所以它量不出"外来主相机 Main Camera 的图标"单独占多少像素）。
//
// 用 run_script 调（--file <本文件> --entry Entry.<名字>），参数走
// `<项目根>/.ai-tmp/test/bvr-spec.txt`：
//   hf_host=<对象名>[,<对象名>...]  目标对象（按名字精确匹配，含未激活）
//   hf_out=<TSV 绝对路径>           追加写
//
// 三个 entry：
//   State        只读点名：每个 hf_host 的 hideFlags / activeInHierarchy / 自身组件类型
//   HideByName   把 hf_host 的 hideFlags 设成 HideInHierarchy，并回读（⛔ 不信"设了"）
//   RestoreHosts 把引擎/业务会画图标的那些宿主的 hideFlags 写回 None（口径与 bv-r 的 Restore 一致：
//                AudioSource / Light / Camera / AudioListener / Canvas 所在的 GameObject）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public static class Entry
{
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private static string FindProjectRoot()
    {
        var d = new DirectoryInfo(Application.dataPath);
        for (var i = 0; i < 5 && d != null; i++, d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".ai-tmp")) &&
                Directory.Exists(Path.Combine(d.FullName, "client"))) return d.FullName;
        }
        return Directory.GetParent(Application.dataPath).FullName;
    }

    private static Dictionary<string, string> ReadSpec()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var p = Path.Combine(FindProjectRoot(), ".ai-tmp", "test", "bvr-spec.txt");
        if (!File.Exists(p)) return d;
        foreach (var raw in File.ReadAllLines(p))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == '#') continue;
            var i = line.IndexOf('=');
            if (i <= 0) continue;
            d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
        }
        return d;
    }

    private static string PathOf(Transform t, int depth)
    {
        var s = t.name;
        var p = t.parent;
        var k = 0;
        while (p != null && k < depth) { s = p.name + "/" + s; p = p.parent; k++; }
        return s;
    }

    private static List<GameObject> Named(string spec)
    {
        var want = new List<string>(spec.Split(','));
        for (var i = 0; i < want.Count; i++) want[i] = want[i].Trim();
        var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include);
        var outList = new List<GameObject>();
        for (var i = 0; i < all.Length; i++)
        {
            if (all[i] == null) continue;
            if (!want.Contains(all[i].name)) continue;
            outList.Add(all[i].gameObject);
        }
        // 稳定输出：按层级路径排序
        outList.Sort((a, b) => string.CompareOrdinal(PathOf(a.transform, 6), PathOf(b.transform, 6)));
        return outList;
    }

    private static string Append(string outPath, string phase, List<string> lines, string ret)
    {
        var text = "";
        if (File.Exists(outPath)) text = File.ReadAllText(outPath);
        if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
        text += string.Join("\n", lines.ToArray()) + "\n";
        File.WriteAllText(outPath, text, Utf8NoBom);
        return ret + " -> " + outPath;
    }

    private static string TypesOf(GameObject go)
    {
        var comps = go.GetComponents<Component>();
        var sb = new StringBuilder();
        for (var i = 0; i < comps.Length; i++)
        {
            if (comps[i] == null) continue;
            if (sb.Length > 0) sb.Append('+');
            sb.Append(comps[i].GetType().Name);
        }
        return sb.ToString();
    }

    /// <summary>只读点名 hf_host 的对象状态。</summary>
    public static string State()
    {
        var spec = ReadSpec();
        string hosts, outPath, phase;
        if (!spec.TryGetValue("hf_host", out hosts)) return "spec missing hf_host=";
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";

        var gos = Named(hosts);
        var lines = new List<string>();
        for (var i = 0; i < gos.Count; i++)
        {
            var go = gos[i];
            lines.Add("#state\t" + phase + "\t" + PathOf(go.transform, 6) + "\thideFlags=" + go.hideFlags +
                      "\tactiveInHierarchy=" + go.activeInHierarchy + "\tcomponents=" + TypesOf(go) +
                      "\ticonDrawn=" + (go.hideFlags == HideFlags.None && go.activeInHierarchy));
        }
        return Append(outPath, phase, lines, "state phase=" + phase + " matched=" + gos.Count +
                                             " hosts=" + hosts);
    }

    /// <summary>把 hf_host 的 hideFlags 设成 HideInHierarchy 并回读。</summary>
    public static string HideByName()
    {
        var spec = ReadSpec();
        string hosts, outPath, phase;
        if (!spec.TryGetValue("hf_host", out hosts)) return "spec missing hf_host=";
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";

        var gos = Named(hosts);
        var lines = new List<string>();
        for (var i = 0; i < gos.Count; i++)
        {
            var go = gos[i];
            go.hideFlags = HideFlags.HideInHierarchy;
            lines.Add("#hidebyname\t" + phase + "\t" + PathOf(go.transform, 6) + "\thideFlags=" + go.hideFlags +
                      "\tactiveInHierarchy=" + go.activeInHierarchy + "\ticonDrawn=" +
                      (go.hideFlags == HideFlags.None && go.activeInHierarchy));
        }
        return Append(outPath, phase, lines, "hidebyname phase=" + phase + " hidden=" + gos.Count);
    }

    /// <summary>把会画图标的宿主 hideFlags 全写回 None（口径同 bv-r 的 Restore）。</summary>
    public static string RestoreHosts()
    {
        var spec = ReadSpec();
        string outPath, phase;
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";

        var gos = new List<GameObject>();
        var audio = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include);
        for (var i = 0; i < audio.Length; i++) if (audio[i] != null) gos.Add(audio[i].gameObject);
        var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
        for (var i = 0; i < lights.Length; i++) if (lights[i] != null) gos.Add(lights[i].gameObject);
        var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
        for (var i = 0; i < cams.Length; i++) if (cams[i] != null) gos.Add(cams[i].gameObject);
        var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include);
        for (var i = 0; i < listeners.Length; i++) if (listeners[i] != null) gos.Add(listeners[i].gameObject);
        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include);
        for (var i = 0; i < canvases.Length; i++) if (canvases[i] != null) gos.Add(canvases[i].gameObject);

        // ⛔ 不用 GetInstanceID()（Unity 6 已过时 ⇒ run_script 的 standalone 编译会把它当错误；
        // 片BU-V 实测 CS0619）。HashSet<GameObject> 的对象相等性已够用。
        var seen = new HashSet<GameObject>();
        var n = 0;
        for (var i = 0; i < gos.Count; i++)
        {
            var go = gos[i];
            if (go == null || !seen.Add(go)) continue;
            if (go.hideFlags == HideFlags.None) continue;
            go.hideFlags = HideFlags.None;
            n++;
        }
        var lines = new List<string>();
        lines.Add("#restorehosts\t" + phase + "\trestored=" + n);
        return Append(outPath, phase, lines, "restorehosts phase=" + phase + " restored=" + n +
                                            " (hosts seen=" + gos.Count + ")");
    }
}
