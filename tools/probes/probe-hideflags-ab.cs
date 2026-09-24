//
// 要回答的问题（不许用推断回答）：
//   "把运行时创建的图标宿主设成 HideFlags.HideInHierarchy，用户自己把 Game view 的 Gizmos 打开
//    时，那些图标还会不会画出来？"
//      并据此决定"业务侧是否落地/引擎侧是否提建议"。
//
// 分组（按组件所在对象分类，机械分组，不手写路径）：
//   cam   = Camera（不许藏着功能：只测，不落地）
//   ui    = RenderMode == ScreenSpaceOverlay 的 Canvas（同上）
//
// 用 run_script 调（--file <本文件> --entry Entry.Census|Entry.Hide|Entry.Restore），
// 参数走 `<项目根>/.ai-tmp/test/bvr-spec.txt`：
//   hf_phase=<A|B|...>          本次相位名（写进 TSV，供差分对齐）
//   hf_group=biz,sound          要设 HideInHierarchy 的分组（逗号分隔）
//   hf_out=<TSV 绝对路径>       追加写
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
        // 只认"项目根"：**同时**含 `client/` 与 `.ai-tmp/` 的那一层（理由见 probe-ui-visibility.cs）
        var d = new DirectoryInfo(Application.dataPath);
        for (var i = 0; i < 5 && d != null; i++, d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".ai-tmp")) &&
                Directory.Exists(Path.Combine(d.FullName, "client"))) return d.FullName;
        }
        d = new DirectoryInfo(Application.dataPath);
        for (var i = 0; i < 5 && d != null; i++, d = d.Parent)
        {
            if (Directory.Exists(Path.Combine(d.FullName, ".ai-tmp"))) return d.FullName;
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
            var line = raw.Trim().TrimStart('\uFEFF');   // PS 5.1 的 Set-Content -Encoding UTF8 会带 BOM
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

    private static bool UnderSound(Transform t)
    {
        var p = t;
        while (p != null)
        {
            if (p.name == "[Sound]") return true;
            p = p.parent;
        }
        return false;
    }

    private static string GroupOf(GameObject go, string kind)
    {
        // （Module/CameraRig/FirstPersonCamera.cs:435 `AddComponent<AudioListener>()`）⇒ 归 cam 组。
        // 这条是主 agent 更正后的真因口径：用户看到的那只"喇叭"是**相机上的 AudioListener**，
        // 与引擎 [Sound] 池的 AudioSource 喇叭同类但不是同一个。
        if (kind == "Camera" || kind == "AudioListener") return "cam";
        if (kind == "Canvas")
        {
            var c = go.GetComponent<Canvas>();
            if (c != null && c.renderMode == RenderMode.ScreenSpaceOverlay) return "ui";
        }
        if (UnderSound(go.transform)) return "sound";
        return "biz";
    }

    // 图标宿主 = Unity 在 Game view 叠加层会画图标的组件：AudioSource(喇叭) / Light(太阳) / Camera / Canvas
    private static List<Component> Hosts()
    {
        var list = new List<Component>();
        var audio = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include);
        for (var i = 0; i < audio.Length; i++) if (audio[i] != null) list.Add(audio[i]);
        var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
        for (var i = 0; i < lights.Length; i++) if (lights[i] != null) list.Add(lights[i]);
        var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
        for (var i = 0; i < cams.Length; i++) if (cams[i] != null) list.Add(cams[i]);
        // AudioListener（相机上的小喇叭图标；见 GroupOf 的注释）
        var listeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsInactive.Include);
        for (var i = 0; i < listeners.Length; i++) if (listeners[i] != null) list.Add(listeners[i]);
        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include);
        for (var i = 0; i < canvases.Length; i++) if (canvases[i] != null) list.Add(canvases[i]);
        return list;
    }

    private static string Write(string outPath, string phase, List<string> lines, string ret)
    {
        var text = "";
        if (File.Exists(outPath)) text = File.ReadAllText(outPath);
        if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
        text += string.Join("\n", lines.ToArray()) + "\n";
        File.WriteAllText(outPath, text, Utf8NoBom);
        return ret + " -> " + outPath;
    }

    /// <summary>只读点名：每个图标宿主的 kind / 分组 / hideFlags / active。</summary>
    public static string Census()
    {
        var spec = ReadSpec();
        string phase, outPath;
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";

        var hosts = Hosts();
        var lines = new List<string>();
        var counts = new Dictionary<string, int>();
        var shown = new Dictionary<string, int>();
        for (var i = 0; i < hosts.Count; i++)
        {
            var c = hosts[i];
            var kind = c.GetType().Name;
            var go = c.gameObject;
            var g = GroupOf(go, kind);
            int n;
            counts.TryGetValue(g, out n); counts[g] = n + 1;
            var iconDrawn = go.hideFlags == HideFlags.None && go.activeInHierarchy;
            if (iconDrawn) { shown.TryGetValue(g, out n); shown[g] = n + 1; }
            lines.Add("#census\t" + phase + "\t" + g + "\t" + kind + "\t" + PathOf(go.transform, 6) +
                      "\thideFlags=" + go.hideFlags + "\tactiveInHierarchy=" + go.activeInHierarchy +
                      "\ticonDrawn=" + iconDrawn + "\thost=" + go.name);
        }
        var sb = new StringBuilder();
        sb.Append("census phase=").Append(phase).Append(" hosts=").Append(hosts.Count);
        foreach (var kv in counts)
        {
            int s;
            shown.TryGetValue(kv.Key, out s);
            sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value).Append("(iconDrawn=").Append(s).Append(')');
        }
        return Write(outPath, phase, lines, sb.ToString());
    }

    /// <summary>把指定分组的所有图标宿主设成 HideFlags.HideInHierarchy，并回读（不信"设了"）。</summary>
    public static string Hide()
    {
        var spec = ReadSpec();
        string phase, outPath, groups;
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";
        if (!spec.TryGetValue("hf_group", out groups)) return "spec missing hf_group=";
        var want = new List<string>(groups.Split(','));
        for (var i = 0; i < want.Count; i++) want[i] = want[i].Trim();

        var hosts = Hosts();
        var lines = new List<string>();
        var hit = new Dictionary<string, int>();
        for (var i = 0; i < hosts.Count; i++)
        {
            var c = hosts[i];
            var kind = c.GetType().Name;
            var go = c.gameObject;
            var g = GroupOf(go, kind);
            if (!want.Contains(g)) continue;
            go.hideFlags = HideFlags.HideInHierarchy;
            int n; hit.TryGetValue(g, out n); hit[g] = n + 1;
            lines.Add("#hide\t" + phase + "\t" + g + "\t" + kind + "\t" + PathOf(go.transform, 6) +
                      "\thideFlags=" + go.hideFlags + "\tactiveInHierarchy=" + go.activeInHierarchy + "\thost=" + go.name);
        }
        var sb = new StringBuilder();
        sb.Append("hide phase=").Append(phase).Append(" groups=").Append(groups);
        foreach (var kv in hit) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value);
        if (hit.Count == 0) sb.Append(" (no host matched)");
        return Write(outPath, phase, lines, sb.ToString());
    }

    /// <summary>
    /// 按**组件类型**隐藏（spec: hf_kind=Light|Canvas|AudioSource|Camera，逗号分隔）。
    /// 为什么要有它：分组口径（biz）把"枪口点光的 Light"和"名牌的 WorldSpace Canvas"混在一起，
    /// 落地时却必须知道**到底是哪一类**在画图标 —— 只按分组测出来的数字无法回答这个问题。
    /// </summary>
    public static string HideKind()
    {
        var spec = ReadSpec();
        string phase, outPath, kinds;
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";
        if (!spec.TryGetValue("hf_kind", out kinds)) return "spec missing hf_kind=";
        var want = new List<string>(kinds.Split(','));
        for (var i = 0; i < want.Count; i++) want[i] = want[i].Trim();

        var hosts = Hosts();
        var lines = new List<string>();
        var hit = new Dictionary<string, int>();
        for (var i = 0; i < hosts.Count; i++)
        {
            var c = hosts[i];
            var kind = c.GetType().Name;
            if (!want.Contains(kind)) continue;
            var go = c.gameObject;
            go.hideFlags = HideFlags.HideInHierarchy;
            int n; hit.TryGetValue(kind, out n); hit[kind] = n + 1;
            lines.Add("#hidekind\t" + phase + "\t" + kind + "\t" + GroupOf(go, kind) + "\t" + PathOf(go.transform, 6) +
                      "\thideFlags=" + go.hideFlags + "\tactiveInHierarchy=" + go.activeInHierarchy + "\thost=" + go.name);
        }
        var sb = new StringBuilder();
        sb.Append("hidekind phase=").Append(phase).Append(" kinds=").Append(kinds);
        foreach (var kv in hit) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value);
        if (hit.Count == 0) sb.Append(" (no host matched)");
        return Write(outPath, phase, lines, sb.ToString());
    }

    /// <summary>复原（把本次测过的宿主 hideFlags 全部写回 None）。</summary>
    public static string Restore()
    {
        var spec = ReadSpec();
        string phase, outPath;
        if (!spec.TryGetValue("hf_phase", out phase)) phase = "?";
        if (!spec.TryGetValue("hf_out", out outPath) || outPath.Length == 0) return "spec missing hf_out=";

        var hosts = Hosts();
        var n = 0;
        for (var i = 0; i < hosts.Count; i++)
        {
            var go = hosts[i].gameObject;
            if (go.hideFlags == HideFlags.None) continue;
            go.hideFlags = HideFlags.None;
            n++;
        }
        var lines = new List<string>();
        lines.Add("#restore\t" + phase + "\trestored=" + n);
        return Write(outPath, phase, lines, "restore phase=" + phase + " restored=" + n);
    }
}
