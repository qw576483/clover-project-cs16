// ============================================================================
// 判据资产 · 切片V：给定立足点的**身体带几何证据**（回答"体积判据为什么判挡"）。
//
// 用途（本文件就是切片V 的结论出处）：
//   走线表（bodyheight-walkline.txt）里出现"判据翻转 = 可站→不可站"的点，若
//   `bodydiag` 说 volumeBlocked=True 而粗采样的净空看起来够，就必须回答：
//   **那一段身高里到底有没有实体**。本探针用**独立于产品代码**的办法取证据：
//     ① `Physics.OverlapBox`（不是 CheckCapsule）覆盖整个身高带的 AABB，列出
//        命中 collider 的名字 / 层 / trigger / 是否与身体重叠（trigger=Collide 再来一遍，
//        以证明"不是靠忽略 trigger 才判挡"）；
//     ② **三种高度 × 16 向**（22.5°）2 m 水平射线 —— 45° 采样会漏掉斜向 22.5° 的墙，
//        把 0.287 m 的墙报成 0.376 m（见 bodyheight-walkline.cs 的 MinWallDistance 注释）；
//     ③ 向上的天花板 / 向下的首交点（头顶净空与脚面）。
//
// 用法（在 client/ 里跑）：
//   unity command run_script --file <本文件绝对路径> --entry BodyVolumePoint.Run
// 输出：一行 SUMMARY + 逐点明细；同时写盘 tools/probes/bodyvolume-point.txt（判据资产）。
//
// 本探针**只读**：不摆位、不下输入、不推进模拟 ⇒ 与回合相位无关。
// ============================================================================
using System;
using System.Globalization;
using System.Text;
using UnityEngine;

public static class BodyVolumePoint
{
    private const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\bodyvolume-point.txt";

    private static readonly StringBuilder Sb = new StringBuilder();

    // 与 Cs16.Core.CsConst 同步（判据值仍以产品代码为准）
    private const float R = 0.36f, Stand = 1.80f, Gcd = 0.12f;

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";

    private static void Line(string s)
    {
        Sb.AppendLine(s);
        Debug.Log("[bodyvolume] " + s);
    }

    public static string Run()
    {
        Sb.Clear();

        // 切片V 的 4 个点：前 3 个是走线表里的判据翻转点，第 4 个是正常可站的对照片
        Dump(new Vector3(-42f, 0.813f, 4f), "翻转①：走线表唯一被标'可疑'的点（结论=本来就站不下）");
        Dump(new Vector3(-12f, -0.406f, 6f), "翻转②：头顶净空 < 身高（本来就站不下）");
        Dump(new Vector3(-2f, 3.251f, -32f), "翻转③：头顶净空 < 身高（本来就站不下）");
        Dump(new Vector3(19.5f, 2.438f, 20.5f), "对照：正常可站点（不应有命中）");

        var head = "SUMMARY points=4 | " + OutPath;
        Sb.AppendLine(head);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(OutPath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(OutPath, Sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e) { Line("WARN 写盘失败 " + e.Message); }
        return head;
    }

    private static void Dump(Vector3 p, string tag)
    {
        Line("");
        Line("================ " + tag + " | point " + V(p) + " ================");
        Line(" 身体带 = [" + F(p.y + Gcd) + ", " + F(p.y + Stand) + "]  胶囊轴 = [" +
             F(p.y + Gcd + R) + ", " + F(p.y + Stand - R) + "] 半径=" + F(R));

        var center = new Vector3(p.x, p.y + (Gcd + Stand) * 0.5f, p.z);
        var he = new Vector3(R, (Stand - Gcd) * 0.5f, R);

        foreach (var qi in new[] { QueryTriggerInteraction.Ignore, QueryTriggerInteraction.Collide })
        {
            var cols = Physics.OverlapBox(center, he, Quaternion.identity, ~0, qi);
            Line(" --- OverlapBox(trigger=" + qi + ") half=" + V(he) + " center=" + V(center) + " count=" + cols.Length);
            Array.Sort(cols, (a, b) => string.CompareOrdinal(a.gameObject.name, b.gameObject.name));
            foreach (var c in cols) Report(c, p);
        }

        var hs = new[] { 0.30f, 0.90f, 1.50f };
        for (var k = 0; k < hs.Length; k++)
        {
            var o = new Vector3(p.x, p.y + hs[k], p.z);
            var shown = 0;
            var nearest = 99f;
            var nearestTag = "-";
            for (var i = 0; i < 16; i++)
            {
                var a = i * Mathf.PI / 8f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                if (!Physics.Raycast(o, d, out var hit, 2f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (hit.distance < nearest) { nearest = hit.distance; nearestTag = hit.collider.gameObject.name + "@" + F(i * 22.5f) + "deg"; }
                Line("   rayP h=" + F(hs[k]) + " deg=" + F(i * 22.5f) + " dist=" + F(hit.distance) +
                     " hitY=" + F(hit.point.y) + " go=" + hit.collider.gameObject.name +
                     " layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer) +
                     " trigger=" + hit.collider.isTrigger);
                shown++;
            }
            Line("   rayP h=" + F(hs[k]) + " ⇒ 最近=" + F(nearest) + " m（" + nearestTag +
                 "）命中数=" + shown + "  ⇒ 半径内=" + (nearest < R));
        }

        if (Physics.Raycast(new Vector3(p.x, p.y + 0.1f, p.z), Vector3.up, out var up, 5f, ~0, QueryTriggerInteraction.Ignore))
            Line("   ceil: y=" + F(up.point.y) + " go=" + up.collider.gameObject.name +
                 " 头顶净空=" + F(up.distance) + " (>=" + F(Stand) + "? " + (up.distance >= Stand) + ")");
        else Line("   ceil: 头顶 5m 内无命中（净空 99）");

        if (Physics.Raycast(new Vector3(p.x, p.y + 5f, p.z), Vector3.down, out var dn, 10f, ~0, QueryTriggerInteraction.Ignore))
            Line("   down: 首交 y=" + F(dn.point.y) + " go=" + dn.collider.gameObject.name);
        else Line("   down: 上方 5m 起的 10m 内无命中");
    }

    private static void Report(Collider c, Vector3 p)
    {
        var b = c.bounds;
        var ax = new Vector3(p.x, p.y + 0.9f, p.z);
        var closest = b.ClosestPoint(ax);
        var d3 = Vector3.Distance(closest, ax);
        Line("   col go=" + c.gameObject.name + " type=" + c.GetType().Name +
             " layer=" + LayerMask.LayerToName(c.gameObject.layer) +
             " trigger=" + c.isTrigger + " enabled=" + c.enabled +
             " boundsMin=" + V(b.min) + " boundsMax=" + V(b.max) +
             " | d3(轴@y+0.9→bounds)=" + F(d3) + " ⇒ 与身体重叠=" + (d3 < R));
        var mc = c as MeshCollider;
        if (mc != null && mc.sharedMesh != null)
            Line("     mesh=" + mc.sharedMesh.name + " verts=" + mc.sharedMesh.vertexCount);
        var bc = c as BoxCollider;
        if (bc != null)
            Line("     box center=" + V(bc.center) + " size=" + V(bc.size) +
                 " lossyScale=" + V(c.transform.lossyScale));
    }
}
