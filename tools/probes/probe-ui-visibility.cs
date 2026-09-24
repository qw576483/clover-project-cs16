// 判据资产（tools/probes/）：把「16 个流程态下到底哪些 UI 面板/UI 根是可见的」做成**机械可复核**的原文。
//
//   ② 真实 UI 节点残留（该隐藏的面板仍然 active）。判"②"的判据**只能**是运行时节点树的
//   `activeInHierarchy` —— 截图看不出一张"半透明/无内容的空面板"，源码里 grep 到 `Close<T>()`
//   也不等于运行时真的关掉了（引擎门面 `UIPanel.SetActive` 才是真源）。
//   所以本探针逐态把**每个派生于 MonoBehaviour 的 Cs16.*Panel 类型**（含从未实例化的 ⇒ absent）
//   与**每个画布的每个直接子节点**（= 玩家眼里的"UI 控件"根）记成 TSV 原文。
//
// 机械性（不是手写清单）：
//   * 面板类型 = 扫**所有已加载程序集**里 namespace 以 Cs16. 开头、非抽象、实现 MonoBehaviour、
//     类型名以 "Panel" 结尾的类型 ⇒ 新增面板自动进入矩阵，不需要改本文件。
//   * 实例 = `Resources.FindObjectsOfTypeAll(type)`（**含未激活**）；active 判据 = activeInHierarchy。
//
// 用 run_script 调（`unity command run_script --file <本文件> --entry Entry.Dump`），
// 参数走 spec 文件 `<项目根>/.ai-tmp/test/bvr-spec.txt`：
//   ui_state=<本次驱动的态名>   ui_out=<TSV 绝对路径>（追加写；驱动在链开始前删除）
// 输出行（TSV，均以 # 开头便于人读）：
//   #dump   state=<label> fsm=<Game.Fsm.Current> scene=<当前场景> playMode=<bool>
//   #canvas state / path / active / renderMode / enabled
//   #panel  state / typeName / path / active / activeInstances / instances
//   #uiroot state / path / activeInHierarchy / images / texts   （画布直接子节点 = 可见 UI 控件的根）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public static class Entry
{
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    // Application.dataPath = <client>/Assets ⇒ 往上找到含 .ai-tmp 的项目根（不写死"上一级"）
    private static string FindProjectRoot()
    {
        // 只认"项目根"：**同时**含 `client/` 与 `.ai-tmp/` 的那一层。
        // 为什么不能只找第一个含 .ai-tmp 的祖先：本机实测 `client/.ai-tmp/` 里有一个游离的
        // 相对路径产物（别的切片写歪了）⇒ 只判 .ai-tmp 会把 spec 路径解析到 `client/.ai-tmp/`
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

    private static string SpecPath()
    {
        return Path.Combine(FindProjectRoot(), ".ai-tmp", "test", "bvr-spec.txt");
    }

    private static Dictionary<string, string> ReadSpec()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var p = SpecPath();
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

    public static string Dump()
    {
        var spec = ReadSpec();
        string label, outPath;
        if (!spec.TryGetValue("ui_state", out label) || label.Length == 0)
            return "spec missing ui_state= (path=" + SpecPath() + " keys=" + spec.Count + " dataPath=" + Application.dataPath + ")";
        if (!spec.TryGetValue("ui_out", out outPath) || outPath.Length == 0)
            return "spec missing ui_out= (path=" + SpecPath() + " keys=" + spec.Count + ")";

        var fsm = "<n/a>";
        try { fsm = CloverEngine.Game.Fsm != null ? (CloverEngine.Game.Fsm.Current ?? "<null>") : "<Game.Fsm null>"; }
        catch (Exception e) { fsm = "<err:" + e.GetType().Name + ">"; }
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

        var lines = new List<string>();
        lines.Add("#dump\t" + label + "\tfsm=" + fsm + "\tscene=" + scene +
                  "\tplayMode=" + UnityEditor.EditorApplication.isPlaying);

        var canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include);
        for (var i = 0; i < canvases.Length; i++)
        {
            var c = canvases[i];
            if (c == null) continue;
            lines.Add("#canvas\t" + label + "\t" + PathOf(c.transform, 4) + "\tactive=" + c.gameObject.activeInHierarchy +
                      "\trenderMode=" + c.renderMode + "\tenabled=" + c.enabled);
        }

        // 面板类型：跨程序集扫（新面板自动进矩阵）
        var panelTypes = new List<Type>();
        var asms = AppDomain.CurrentDomain.GetAssemblies();
        for (var a = 0; a < asms.Length; a++)
        {
            Type[] ts;
            try { ts = asms[a].GetTypes(); }
            catch (Exception) { continue; }
            for (var t = 0; t < ts.Length; t++)
            {
                var ty = ts[t];
                if (ty == null || ty.IsAbstract || !ty.Name.EndsWith("Panel", StringComparison.Ordinal)) continue;
                if (ty.Namespace == null || !ty.Namespace.StartsWith("Cs16", StringComparison.Ordinal)) continue;
                if (!typeof(MonoBehaviour).IsAssignableFrom(ty)) continue;
                panelTypes.Add(ty);
            }
        }
        panelTypes.Sort(delegate (Type x, Type y) { return string.CompareOrdinal(x.Name, y.Name); });

        var activePanels = 0;
        for (var i = 0; i < panelTypes.Count; i++)
        {
            var ty = panelTypes[i];
            object[] objs;
            try { objs = UnityEngine.Resources.FindObjectsOfTypeAll(ty); }
            catch (Exception) { objs = new object[0]; }
            var inst = 0;
            var active = 0;
            var path = "";
            for (var k = 0; k < objs.Length; k++)
            {
                var mb = objs[k] as MonoBehaviour;
                if (mb == null) continue;
                inst++;
                if (mb.gameObject.activeInHierarchy)
                {
                    active++;
                    if (path.Length == 0) path = PathOf(mb.transform, 4);
                }
            }
            if (path.Length == 0 && inst > 0)
            {
                var mb0 = objs[0] as MonoBehaviour;
                if (mb0 != null) path = PathOf(mb0.transform, 4);
            }
            if (active > 0) activePanels++;
            lines.Add("#panel\t" + label + "\t" + ty.Name + "\t" + (path.Length > 0 ? path : "<absent>") +
                      "\tactive=" + (active > 0) + "\tactiveInstances=" + active + "\tinstances=" + inst);
        }

        // UI 控件的根：每个画布的直接子节点（含未激活的），玩家看到的"ui 控件"就长在这些下面
        for (var i = 0; i < canvases.Length; i++)
        {
            var c = canvases[i];
            if (c == null) continue;
            for (var k = 0; k < c.transform.childCount; k++)
            {
                var ch = c.transform.GetChild(k);
                var go = ch.gameObject;
                var imgs = go.GetComponentsInChildren<UnityEngine.UI.Image>(true).Length;
                var txts = go.GetComponentsInChildren<UnityEngine.UI.Text>(true).Length;
                lines.Add("#uiroot\t" + label + "\t" + PathOf(ch, 3) + "\tactiveInHierarchy=" + go.activeInHierarchy +
                          "\timages=" + imgs + "\ttexts=" + txts);
            }
        }

        var text = "";
        if (File.Exists(outPath)) text = File.ReadAllText(outPath);
        if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
        text += string.Join("\n", lines.ToArray()) + "\n";
        File.WriteAllText(outPath, text, Utf8NoBom);

        return "state=" + label + " fsm=" + fsm + " scene=" + scene + " panels=" + panelTypes.Count +
               " activePanels=" + activePanels + " lines=" + lines.Count + " -> " + outPath;
    }
}
