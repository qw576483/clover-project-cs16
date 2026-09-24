// ============================================================================
//
// 为什么是 C# 而不是 Python：被判的是**真代码 + 真物理**
//   （CsMap.BodyHeightClearAt / BodyVolumeBlocked / ResolveMove 走 Unity Physics）。
//   Python 复刻一遍公式只会得到"两份实现互相印证"的假证据；而"射线起点落在实心体内"这种形态
//   本来就是 **PhysX 的行为**，只有真物理能给答案。
//
// 用法（在 client/ 里跑，或每条都带 --project-path）：
//   unity command run_script --file <本文件绝对路径> --entry BodyHeightWalkline.Run
//
// 输出：一行 SUMMARY + 逐行明细；同时写盘到
//   <项目根>/tools/probes/bodyheight-walkline.txt（判据资产，供复核）。
//
// 本探针**只读**：不摆位、不下输入、不推进模拟 ⇒ 与回合相位无关（Freeze/Live/RoundEnd 一个样）。
// ============================================================================
using System;
using System.Globalization;
using System.Text;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

public static class BodyHeightWalkline
{
    private const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\bodyheight-walkline.txt";

    private static readonly StringBuilder Sb = new StringBuilder();
    private static int _fail;

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";

    private static void Line(string s)
    {
        Sb.AppendLine(s);
        Debug.Log("[walkline] " + s);
    }

    private static void Check(bool ok, string name, string detail)
    {
        if (!ok) _fail++;
        Line((ok ? "PASS  " : "FAIL  ") + name + "  " + detail);
    }

    public static string Run()
    {
        Sb.Clear();
        _fail = 0;

        ICsMap iface = null;
        var mm = MatchModule.Instance;
        if (mm != null) iface = mm.Map;
        if (iface == null)
        {
            var cm = UnityEngine.Object.FindAnyObjectByType<CsMapModule>(FindObjectsInactive.Include);
            if (cm != null) iface = cm.Map;
        }
        var map = iface as CsMap;
        if (map == null) { Line("ERROR 地图门面拿不到（MatchModule.Instance=" + (mm != null) + "）"); return "ERROR: no map facade"; }
        Line("# map.IsLoaded=" + map.IsLoaded + " status=" + map.Status + " matchModule=" + (mm != null));
        if (!map.IsLoaded) { Line("ERROR 地图数据未加载 ⇒ ResolveMove 原样放行（无判据可言）"); return "ERROR: map not loaded"; }

        RunCase(map, "A-door 警家A门(yaw180)", 39.5f, 33.0f, new Vector3(0f, 0f, -1f));
        RunCase(map, "box 箱子面(yaw0)", 19.5f, 20.5f, new Vector3(0f, 0f, 1f));

        LedgeSweep(map, "A-door", 39.5f, 33.0f, 6f);
        LedgeSweep(map, "box", 19.5f, 20.5f, 6f);

        VerdictDiff(map);

        var head = "SUMMARY FAIL=" + _fail + " | " + OutPath;
        Sb.AppendLine(head);
        try
        {
            var dir = System.IO.Path.GetDirectoryName(OutPath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(OutPath, Sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e) { Sb.AppendLine("WARN 写盘失败 " + OutPath + " : " + e.Message); }
        return head;
    }

    private static void RunCase(CsMap map, string name, float x, float z, Vector3 dir)
    {
        var gy = map.SampleGround(new Vector3(x, 5f, z));
        if (float.IsNegativeInfinity(gy)) { Line("ERROR " + name + " 在 (" + F(x) + "," + F(z) + ") 探不到地面"); return; }
        var from = new Vector3(x, gy, z);
        Line("");
        Line("== " + name + " start=" + V(from) + " dir=" + V(dir) + " groundY实测=" + F(gy) + " ==");
        VerticalProfile(name, x, z);
        Line("   start diagnose: " + map.BodyHeightDiagnoseForTest(from));

        // ① 走线表（**交付判据**）：推进 d 之后解算终点，期望 = 停在面外
        Line("   ---- 走线表 d / want / end / 是否被钳住 / end 是否在实体内 ----");
        var blockedRows = 0;
        for (float d = 0.5f; d <= 4.0f + 1e-4f; d += 0.5f)
        {
            var to = from + dir * d;
            var end = map.ResolveMoveForTest(from, to);
            var inside = map.VolumeBlockedForTest(end.x, end.y, end.z);
            if (inside) blockedRows++;
            Line("   ROW d=" + F(d) + " want=" + V(to) + " end=" + V(end) +
                 " |Δ|=" + F(Vector3.Distance(from, end)) +
                 " clamped=" + (Vector3.Distance(from, end) < d - 1e-3f) +
                 " endInsideSolid=" + inside +
                 " endBitmap=" + map.WalkableAt(end.x, end.z) +
                 " endCanStand=" + map.CanStand(end));
        }
        Check(blockedRows == 0, "走线[" + name + "] 终点一律在实心之外（不许穿进去）",
              "rows=8 endInsideSolid=" + blockedRows);

        map.WalkLineForTest(from, dir, 4.0f, 0.5f);      // 游戏侧类型化批量入口（同一张表）

        Line("   ---- 沿走线逐点：bitmap / rayClear / volBlocked / canStandBefore(改前) / canStand(改后) ----");
        for (float d = 0.25f; d <= 4.0f + 1e-4f; d += 0.25f)
        {
            var p = from + dir * d;
            Line("   p d=" + F(d) + " pos=" + V(p) +
                 " bitmap=" + map.WalkableAt(p.x, p.z) +
                 " rayClear=" + map.RayClearForTest(p.x, p.y, p.z) +
                 " volBlocked=" + map.VolumeBlockedForTest(p.x, p.y, p.z) +
                 " canStandBefore=" + CanStandBefore(map, p) +
                 " canStand=" + map.CanStand(p));
        }

        // ③ 逐步 trace（同一个 to 若结果不一致，必须看每一步）
        TraceMove(map, from, from + dir * 3.0f, name + " d=3.0");
        TraceMove(map, from, from + dir * 3.5f, name + " d=3.5");
        TraceMove(map, from, from + dir * 4.0f, name + " d=4.0");
    }

    /// <summary>
    /// （<see cref="CsMap.CanStand"/> 的 OR 结构逐字照搬，几何分支取 `out legacy` 那一份）。
    /// 两条判据本身都由产品代码给，探针只做 OR —— 不复制任何公式。
    /// </summary>
    private static bool CanStandBefore(CsMap map, Vector3 p)
    {
        if (map.BitmapClearForTest(p) && map.GroundWithinStepForTest(p)) return true;
        map.BodyHeightClearForTest(p, Cs16.Core.CsConst.PlayerRadius, out var legacy);
        return legacy;
    }

    private static void TraceMove(CsMap map, Vector3 from, Vector3 to, string tag)
    {
        Line("   trace[" + tag + "] from=" + V(from) + " to=" + V(to));
        var end = map.ResolveMoveTraceForTest(from, to, (i, want, before, after) =>
            Line("     step " + i + " want=" + V(want) + " before=" + V(before) + " after=" + V(after) +
                 " canStand(want)=" + map.CanStand(want)));
        Line("   trace[" + tag + "] end=" + V(end));
    }

    /// <summary>
    /// 竖直剖面（判"起点 y 取对了没"）：从 y=40 往下 <see cref="Physics.RaycastAll"/>，
    /// 把这一列上**所有**世界面按 y 降序报出来（含 collider 名与层）。
    /// </summary>
    private static void VerticalProfile(string name, float x, float z)
    {
        var hits = Physics.RaycastAll(new Vector3(x, 40f, z), Vector3.down, 60f, ~0, QueryTriggerInteraction.Ignore);
        Line("   vert-profile[" + name + "] hits=" + hits.Length + " （y 降序）");
        Array.Sort(hits, (a, b) => b.point.y.CompareTo(a.point.y));
        for (var i = 0; i < hits.Length; i++)
            Line("     hit y=" + F(hits[i].point.y) + " normalY=" + F(hits[i].normal.y) +
                 " go=" + hits[i].collider.gameObject.name +
                 " layer=" + LayerMask.LayerToName(hits[i].collider.gameObject.layer) +
                 " trigger=" + hits[i].collider.isTrigger);
    }

    /// <summary>
    /// 「箱顶 / 台阶面」扫描：0.5 m 网格上向下取地面，取**最高且有平台**的面
    /// （单点最高往往是薄墙的墙头），断言它仍可站，并量"身侧净空"（8 向 2m 内最近的墙）
    /// —— 净空 &lt; PlayerRadius 的点本来就站不下，被体积判据挡住是**对的**。
    /// </summary>
    private static void LedgeSweep(CsMap map, string tag, float cx, float cz, float half)
    {
        const float grid = 0.5f, plateauTol = 0.3f;
        var n = 0;
        var plates = 0;        // 台面候选（≥4 邻点同高）
        var plateStand = 0;    // 其中 canStand（改后）
        var best = default(Vector3);
        var bestY = float.NegativeInfinity;
        var bestPlateau = -1;
        for (float x = cx - half; x <= cx + half + 1e-4f; x += grid)
        {
            for (float z = cz - half; z <= cz + half + 1e-4f; z += grid)
            {
                var y = map.SampleGround(new Vector3(x, 8f, z), 12f);
                if (float.IsNegativeInfinity(y)) continue;
                n++;
                var pla = 0;
                for (var dx = -2; dx <= 2; dx++)
                {
                    for (var dz = -2; dz <= 2; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var ny = map.SampleGround(new Vector3(x + dx * grid, 8f, z + dz * grid), 12f);
                        if (!float.IsNegativeInfinity(ny) && Math.Abs(ny - y) <= plateauTol) pla++;
                    }
                }
                if (pla < 4) continue;
                plates++;
                if (map.CanStand(new Vector3(x, y, z))) plateStand++;
                if (y > bestY) { bestY = y; best = new Vector3(x, y, z); bestPlateau = pla; }
            }
        }
        if (n == 0) { Line("ledge[" + tag + "] 网格内一个地面都没探到"); return; }
        if (bestPlateau < 0) { Line("ledge[" + tag + "] 采样 " + n + " 点，没有 ≥4 邻点同高的台面"); return; }

        var clear = MinWallDistance(best);
        var head = Headroom(best);
        var before = CanStandBefore(map, best);
        var stand = map.CanStand(best);
        Line("ledge[" + tag + "] 采样 " + n + " 点（台面 " + plates + " 个 / 其中可站 " + plateStand +
             "）；最高台面 y=" + F(bestY) + " @ " + V(best) +
             "（同高邻点 " + bestPlateau + "/24，身侧净空 " + F(clear) + " m，头顶净空 " + F(head) + " m，" +
             "canStandBefore=" + before + " canStand=" + stand + "）");
        Check(!before || stand, "ledge[" + tag + "] 改前可站的台面改后仍可站",
              "top=" + V(best) + " before=" + before + " now=" + stand + " clearance=" + F(clear) +
              " headroom=" + F(head) + " | " + map.BodyHeightDiagnoseForTest(best));
    }

    /// <summary>从 (x, y+0.1) 向上打 <c>StandHeight+0.1</c> 的射线，返回头顶净空（无上限 = 99）。</summary>
    private static float Headroom(Vector3 p)
    {
        if (Physics.Raycast(new Vector3(p.x, p.y + 0.1f, p.z), Vector3.up, out var h,
                            Cs16.Core.CsConst.StandHeight + 0.1f, ~0, QueryTriggerInteraction.Ignore))
            return h.distance;
        return 99f;
    }

    /// <summary>
    /// 身体带内的最小水平净空：在 <c>y+0.3 / y+0.9 / y+1.5</c>（下段 / 中段 / 上段）
    /// **三个高度**各打 **16 向**（22.5° 间隔）2 m 射线取最小。
    /// <list type="bullet">
    /// <item>只量一个高度会漏掉"贴着台沿/箱沿"那种只占身高带**某一段**的实体
    /// （那正是体积判据要抓的一类）；</item>
    /// <item>⛔ 45° 间隔（8 向）会**假报大净空** —— 切片V 实测：点 (-42.000,0.813,4.000)
    /// 的箱侧面 <c>SandCrtLrgSd.png</c> 在 <b>deg 157.5°</b> 处只有 <b>0.287 m</b>（&lt; PlayerRadius 0.36），
    /// 而 8 向采样最近只落在 deg 135° = <b>0.376 m</b>（&gt; 0.36）⇒ 该点被误报成
    /// "净空够却翻转 = 可疑"。<b>22.5° 采样后该点恢复为「本来就站不下」</b>
    /// （几何证据：<c>tools/probes/bodyvolume-point.txt</c> 的同名点条；产品代码未改）。</item>
    /// </list>
    /// </summary>
    private static float MinWallDistance(Vector3 p)
    {
        var min = 2.0f;
        var hs = new[] { 0.3f, 0.9f, 1.5f };
        for (var k = 0; k < hs.Length; k++)
        {
            var o = new Vector3(p.x, p.y + hs[k], p.z);
            for (var i = 0; i < 16; i++)
            {
                var a = i * Mathf.PI / 8f;
                var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                if (Physics.Raycast(o, d, out var h, 2f, ~0, QueryTriggerInteraction.Ignore))
                    min = Mathf.Min(min, h.distance);
            }
        }
        return min;
    }

    /// <summary>
    /// </summary>
    private static void VerdictDiff(CsMap map)
    {
        int surfaces = 0, before = 0, now = 0, turned = 0, turnedBack = 0, turnedSuspicious = 0;
        var samples = 0;
        for (float x = -60f; x <= 60f; x += 2f)
        {
            for (float z = -60f; z <= 60f; z += 2f)
            {
                var y = map.SampleGround(new Vector3(x, 8f, z), 14f);
                if (float.IsNegativeInfinity(y)) continue;
                surfaces++;
                var p = new Vector3(x, y, z);
                var b = CanStandBefore(map, p);
                var a = map.CanStand(p);
                if (b) before++;
                if (a) now++;
                if (b && !a)
                {
                    turned++;
                    // 判定"这次翻转是不是误杀"：站不下 ⇔ 身侧净空 < 半径 或 头顶净空 < 身高。
                    var c = MinWallDistance(p);
                    var hr = Headroom(p);
                    var legit = c < Cs16.Core.CsConst.PlayerRadius || hr < Cs16.Core.CsConst.StandHeight;
                    if (!legit) turnedSuspicious++;
                    if (samples++ < 10)
                        Line("   turned(可站→不可站) " + V(p) + " clearance=" + F(c) + " headroom=" + F(hr) +
                             " volBlocked=" + map.VolumeBlockedForTest(p.x, p.y, p.z) +
                             " 本来就站不下=" + legit);
                }
                if (!b && a) { turnedBack++; }
            }
        }
        Line("verdictDiff | surfaces=" + surfaces + " standableBefore=" + before + " standableNow=" + now +
             " turned=" + turned + " 其中本来就站不下的=" + (turned - turnedSuspicious) +
             " 可疑(净空够却翻转)=" + turnedSuspicious + " turnedBack=" + turnedBack);
        Check(turnedBack == 0, "verdictDiff 没有出现「改前站不下 → 改后能站」（方向只应变严不会变松）",
              "turnedBack=" + turnedBack);
        Check(turnedSuspicious == 0, "verdictDiff 每一处翻转都是「本来就站不下」（净空<半径 或 头顶<身高）",
              "turned=" + turned + " suspicious=" + turnedSuspicious);
        Check(now > surfaces / 4, "verdictDiff 大量立足点存活（体积判据没把整张图判死）",
              "surfaces=" + surfaces + " standableNow=" + now);
    }
}
