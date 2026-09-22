// ============================================================================
// 判据资产 · 切片BD：实机逐帧定位「B 旋转楼梯上不去」到底卡在哪一格、哪道闸门。
//
// 为什么是 C# 而不是 Python（与 tools/probes/bodyheight-walkline.cs 同一条理由）：
//   被判的是**真代码 + 真物理** —— CsMap.CanStand / ResolveMove / TrySampleGround /
//   TryStepUp 走 Unity Physics（MeshCollider）。Python 复刻公式只会得到"两份实现互相印证"
//   的假证据；"射线起点落在实心体内"这种形态本来就是 **PhysX 的行为**。
//
// 判据（回答差异 #66 的问题："卡在哪一格 / 走到第几帧 / 被哪道闸门拒 / 数值差多少"）：
//   取**运行时**标记表（CsMap.Points 的真源）里每条到 B 的路线的**最后一段**：
//       Route_T_To_B  pts[-2] -> pts[-1]     （T 侧楼梯 底 -> 顶）
//       Route_CT_To_B pts[-2] -> pts[-1]     （CT 侧楼梯 底 -> 顶）
//   （同口径见 tools/probes/bot-path-check.py 的 stair_cases()；片BA 已离线断言"位图层面全通"）
//   对每一侧出三张表：
//     (A) 俯视占用图 —— 0.5m 网格上 map.WalkableAt 的 ASCII 图（看走廊形状，判"哪一格"）
//     (B) 地面剖面   —— 沿 底->顶 直线每 0.25m：地面 y / 逐段高差 Δy / 地面法线 y /
//                       位图可走 / 四道闸门各自的判定 / 第一个被拒的闸门（判"是整片斜楔还是多级台阶"）
//     (C) 逐帧推进   —— 每帧 ≤0.25m 调 ResolveMove（= CsMatch.StepActorPhysics 的同一入口），
//                       报 cur/want/end/实际位移/四个闸门/是否被钳住；卡住时做**横向偏移扫描**
//                       （贴左/贴右/斜着上），把"某条特定路线/某个角上不去"一并试掉。
//
// 四道闸门的口径与出处（⛔ 探针只**读**产品常量与产品公开入口，不抄任何公式）：
//   ① 中心格可走            = CsMap.WalkableAt(x,z)                     出处 CsMap.cs:516
//   ② 落点地面高差 ≤ 0.45m  = CsConst.StepUpHeight                      出处 CsMap.cs:522 / CsConst.cs:113
//   ③ 落点法线 y ≥ 0.70     = CsConst.MaxStandableSlopeNormalZ          出处 CsMap.cs:521 / CsConst.cs:135
//   ④ 膝盖射线通畅          = Physics.Raycast(foot + (0,0.45,0), dir)   出处 CsMap.cs:529-530
//   两层判据入口（位图 ∨ 几何）            = CsMap.CanStand              出处 CsMap.cs:298-299
//
// 用法（在 client/ 里跑，或每条都带 --project-path）：
//   unity command run_script --file <本文件绝对路径> --entry BStairsWalkline.Run
//
// 输出：一行 SUMMARY + 三张表；同时写盘到
//   <项目根>/tools/probes/bstairs-walkline.txt（判据资产，供复核）。
//
// 本探针**只读**：不摆位、不下输入、不推进模拟、不改任何坐标 ⇒ 与回合相位无关。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

public static class BStairsWalkline
{
    private const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\bstairs-walkline.txt";

    private static readonly StringBuilder Sb = new StringBuilder();
    private static int _fail;

    private const float MicroStep = 0.25f;   // = CsMap.MaxSweepStep 的量级（≤0.25m/帧的推进口径）
    private const int MaxFrames = 400;

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
    private static string XZ(Vector3 v) => "(" + F(v.x) + "," + F(v.z) + ")";

    private static void Line(string s)
    {
        Sb.AppendLine(s);
        Debug.Log("[bstairs] " + s);
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
        if (map == null)
        {
            Line("ERROR 地图门面拿不到（MatchModule.Instance=" + (mm != null) + "）");
            return "ERROR: no map facade";
        }
        Line("# map.IsLoaded=" + map.IsLoaded + " status=" + map.Status + " matchModule=" + (mm != null));
        if (!map.IsLoaded)
        {
            Line("ERROR 地图数据未加载 ⇒ ResolveMove 原样放行（无判据可言）");
            return "ERROR: map not loaded";
        }

        Case(map, "T side", "Route_T_To_B");
        Case(map, "CT side", "Route_CT_To_B");

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

    private static void Case(CsMap map, string side, string route)
    {
        var pts = map.Points(route);
        if (pts == null || pts.Length < 2)
        {
            _fail++;
            Line("SKIP " + side + " 标记点不足（" + route + " 点数=" + (pts == null ? -1 : pts.Length) + "）");
            return;
        }
        var lo = pts[pts.Length - 2];
        var hi = pts[pts.Length - 1];

        Line("");
        Line("=== " + side + "  " + route + " : bottom=" + V(lo) + " cell=" + Cell(lo) +
             "  ->  top=" + V(hi) + " cell=" + Cell(hi) +
             "  |Δxz|=" + F(Horiz(lo, hi)) + " rise=" + F(hi.y - lo.y) + " ===");

        Occupancy(map, side, lo, hi);
        Profile(map, side, lo, hi);
        Walk(map, side, lo, hi);
        Path(map, side, lo, hi);
    }

    // ── 走廊路径（A*，契约与引擎 CloverEngine.AStar 逐条对齐）─────────────────────
    //  判据资产口径同 tools/probes/bot-path-check.py：8 邻接；直走 10 / 斜走 14；
    //  对角要求两侧正交格都可走；展开上限 20000；不可达 => None。
    //  ⛔ 这里**独立复算**一遍（不是导航实现）——运行时那一半在 C# 引擎 AStar + BotNavigator。
    //  格子口径：cell = 1.0 m、格心 = 标记点自身（标记点即格心，见 de_dust2.bytes 头部）。
    private const float PathCell = 1.0f;
    private const int PathMaxNodes = 20000;
    private static readonly int[][] N8 =
    {
        new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 },
        new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 }
    };

    private static bool WalkableCell(CsMap map, Vector3 lo, int i, int j)
        => map.WalkableAt(lo.x + i * PathCell, lo.z + j * PathCell);

    private static Vector3 CellCenter(Vector3 lo, int i, int j)
        => new Vector3(lo.x + i * PathCell, lo.y, lo.z + j * PathCell);

    private static List<int[]> AStar(CsMap map, Vector3 lo, int[] start, int[] goal, out string why)
    {
        why = "";
        if (!WalkableCell(map, lo, start[0], start[1])) { why = "起点格不可走"; return null; }
        if (!WalkableCell(map, lo, goal[0], goal[1])) { why = "终点格不可走"; return null; }

        var g = new Dictionary<long, int>();
        var came = new Dictionary<long, long>();
        var closed = new HashSet<long>();
        var open = new List<long>();
        var f = new Dictionary<long, int>();
        long Key(int i, int j) => ((long)(i + 4096) << 16) | (long)(j + 4096);

        var sk = Key(start[0], start[1]);
        var gk = Key(goal[0], goal[1]);
        g[sk] = 0; f[sk] = H(start, goal); open.Add(sk);

        var expanded = 0;
        while (open.Count > 0)
        {
            var bi = 0;
            for (var k = 1; k < open.Count; k++) if (f[open[k]] < f[open[bi]]) bi = k;
            var cur = open[bi];
            open.RemoveAt(bi);
            if (!closed.Add(cur)) continue;
            if (cur == gk)
            {
                var path = new List<int[]>();
                var walk = cur;
                while (true)
                {
                    path.Add(new[] { (int)((walk >> 16) - 4096), (int)((walk & 0xFFFF) - 4096) });
                    if (walk == sk) break;
                    walk = came[walk];
                }
                path.Reverse();
                return path;
            }
            expanded++;
            if (expanded > PathMaxNodes) { why = "展开超上限 " + PathMaxNodes; return null; }

            var ci = (int)((cur >> 16) - 4096);
            var cj = (int)((cur & 0xFFFF) - 4096);
            var gc = g[cur];
            foreach (var d in N8)
            {
                var ni = ci + d[0]; var nj = cj + d[1];
                var nk = Key(ni, nj);
                if (closed.Contains(nk) || !WalkableCell(map, lo, ni, nj)) continue;
                var diag = d[0] != 0 && d[1] != 0;
                if (diag)
                {
                    if (!WalkableCell(map, lo, ci + d[0], cj)) continue;
                    if (!WalkableCell(map, lo, ci, cj + d[1])) continue;
                }
                var t = gc + (diag ? 14 : 10);
                if (g.TryGetValue(nk, out var old) && t >= old) continue;
                g[nk] = t; came[nk] = cur; f[nk] = t + H(new[] { ni, nj }, goal);
                open.Add(nk);
            }
        }
        why = "不连通";
        return null;
    }

    private static int H(int[] a, int[] b)
    {
        var dx = Mathf.Abs(a[0] - b[0]); var dy = Mathf.Abs(a[1] - b[1]);
        var lo = Mathf.Min(dx, dy); var hi2 = Mathf.Max(dx, dy);
        return 10 * hi2 + 4 * lo;
    }

    /// <summary>(D)+(E)：走廊路径（A*）上的逐格地面剖面 + 逐帧推进 —— 这才是"真的沿楼梯走"。</summary>
    private static void Path(CsMap map, string side, Vector3 lo, Vector3 hi)
    {
        var start = new[] { 0, 0 };
        var goal = new[]
        {
            Mathf.RoundToInt((hi.x - lo.x) / PathCell),
            Mathf.RoundToInt((hi.z - lo.z) / PathCell)
        };
        var path = AStar(map, lo, start, goal, out var why);
        Line("");
        Line("-- (D) 走廊路径 " + side + " cell=" + F(PathCell) + " start=" + start[0] + "," + start[1] +
             " goal=" + goal[0] + "," + goal[1] + " --");
        if (path == null)
        {
            _fail++;
            Line("  FAIL 求不出路径: " + why);
            return;
        }
        Line("  路径格数=" + path.Count + " 拐点数=" + Smooth(path).Count +
             " 全格可走=" + AllWalkable(map, lo, path));

        // (D) 逐格：地面高度 / 逐格高差 / 法线 / 四道闸门（= 沿楼梯逐级推进）
        Line("  " + Pad("k", 4) + Pad("cell", 10) + Pad("x", 10) + Pad("z", 9) + Pad("groundY", 9) +
             Pad("dY", 7) + Pad("n.y", 7) + Pad("g1", 4) + Pad("g2", 4) + Pad("g3", 4) + Pad("g4", 4) +
             Pad("canStand", 9) + "reject");
        var prevFoot = lo.y;
        var rejectCells = 0;
        var maxDY = 0f;
        var minNY = 2f;
        for (var k = 0; k < path.Count; k++)
        {
            var c = CellCenter(lo, path[k][0], path[k][1]);
            var hasGround = map.TrySampleGround(new Vector3(c.x, Mathf.Max(lo.y, hi.y) + 2f, c.z),
                                                out var gp, out var gn, 20f);
            var gy = hasGround ? gp.y : float.NegativeInfinity;
            var dY = k == 0 ? 0f : gy - prevFoot;
            if (hasGround && k > 0 && dY > maxDY) maxDY = dY;
            if (hasGround && gn.y < minNY) minNY = gn.y;

            var want = new Vector3(c.x, prevFoot, c.z);
            var g1 = map.WalkableAt(c.x, c.z);
            var g2 = !hasGround || gy - prevFoot <= CsConst.StepUpHeight;
            var g3 = !hasGround || gn.y >= CsConst.MaxStandableSlopeNormalZ;
            var g4 = KneeClearPrev(map, prevFoot, lo, path, k, c);
            var canStand = map.CanStand(want);
            var reject = ((!g1 ? "g1 " : "") + (!g2 ? "g2 " : "") + (!g3 ? "g3 " : "") + (!g4 ? "g4" : "")).Trim();
            if (reject.Length == 0) reject = "-"; else rejectCells++;

            Line("  " + Pad(k.ToString(), 4) + Pad(path[k][0] + "," + path[k][1], 10) + Pad(F(c.x), 10) +
                 Pad(F(c.z), 9) + Pad(hasGround ? F(gy) : "-inf", 9) +
                 Pad(k == 0 ? "-" : (dY >= 0 ? "+" : "") + F(dY), 7) +
                 Pad(hasGround ? F(gn.y) : "-", 7) + Pad(g1 ? "ok" : "NO", 4) + Pad(g2 ? "ok" : "NO", 4) +
                 Pad(g3 ? "ok" : "NO", 4) + Pad(g4 ? "ok" : "NO", 4) + Pad(canStand ? "yes" : "no", 9) + reject);

            if (hasGround) prevFoot = gy;
        }
        Line("  逐格小结[" + side + "] 格数=" + path.Count + " 被拒格=" + rejectCells +
             " 单格最大Δy=" + F(maxDY) + " 最小法线y=" + F(minNY));

        // (E) 逐帧推进：沿路径每 0.25m 一步
        Line("-- (E) 沿走廊路径逐帧推进 " + side + " step=" + F(MicroStep) + " --");
        Line("  " + Pad("frame", 6) + Pad("cur", 26) + Pad("want", 26) + Pad("end", 26) + Pad("moved", 7) +
             Pad("clamped", 8) + Pad("gates@want", 12) + "verdict");
        var pos = lo;
        var gy0 = map.SampleGround(new Vector3(lo.x, lo.y + 2f, lo.z), 20f);
        if (!float.IsNegativeInfinity(gy0)) pos = new Vector3(lo.x, gy0, lo.z);
        var wp = 1;
        var f2 = 0;
        var clampedFrames = 0;
        var stuckAt = -1;
        while (wp < path.Count && f2 < MaxFrames)
        {
            f2++;
            var target = CellCenter(lo, path[wp][0], path[wp][1]);
            var to = new Vector3(target.x - pos.x, 0f, target.z - pos.z);
            var d = to.magnitude;
            if (d <= MicroStep + 1e-3f) { wp++; continue; }
            var dir = to / d;
            var want = new Vector3(pos.x + dir.x * MicroStep, pos.y, pos.z + dir.z * MicroStep);
            var end = map.ResolveMove(pos, want);
            var moved = Horiz(pos, end);
            var clamped = moved < MicroStep - 1e-3f;
            if (clamped) clampedFrames++;
            var nextY = GroundFollow(map, pos.y, end);
            Line("  " + Pad(f2.ToString(), 6) + Pad(XZ(pos) + " y=" + F(pos.y), 26) + Pad(XZ(want), 26) +
                 Pad(XZ(end) + " y=" + F(nextY), 26) + Pad(F(moved), 7) + Pad(clamped ? "yes" : "no", 8) +
                 Pad(GatesAt(map, pos, want), 12) + "wp" + wp);
            if (moved < 0.02f)
            {
                stuckAt = f2;
                Line("  >>> **卡死** frame=" + f2 + " cell=" + Cell(pos) + " 位置=" + V(pos) +
                     " -> 目标格 " + path[wp][0] + "," + path[wp][1] + " @ " + V(target));
                Line("      " + GatesVerbose(map, pos, want));
                Line("      " + map.BodyHeightDiagnoseForTest(want));
                break;
            }
            pos = new Vector3(end.x, nextY, end.z);
        }
        Line("  逐帧小结[" + side + "] frames=" + f2 + " 被钳住帧=" + clampedFrames +
             " 到顶=" + (wp >= path.Count) + " 终点=" + V(pos) + " 目标=" + V(hi) +
             " 平面残差=" + F(Horiz(pos, hi)) + " 高度差=" + F(pos.y - hi.y) +
             (stuckAt >= 0 ? " **卡在第 " + stuckAt + " 帧**" : ""));
    }

    private static bool AllWalkable(CsMap map, Vector3 lo, List<int[]> path)
    {
        foreach (var c in path) if (!WalkableCell(map, lo, c[0], c[1])) return false;
        return true;
    }

    /// <summary>路径平滑（Bresenham 视线拉直，口径同引擎 AStar.Smooth）：只用于报"拐点数"。</summary>
    private static List<int[]> Smooth(List<int[]> path)
    {
        if (path.Count <= 2) return new List<int[]>(path);
        var outl = new List<int[]> { path[0] };
        var anchor = 0;
        for (var i = 2; i < path.Count; i++)
        {
            // ⛔ 这里只数拐点，不重新判 LOS（LOS 由 python 侧 bot-path-check 负责）——
            //    避免探针里再抄一遍 Bresenham。
            var straight = (path[i][0] - path[anchor][0]) == 0 || (path[i][1] - path[anchor][1]) == 0;
            if (straight) continue;
            outl.Add(path[i - 1]);
            anchor = i - 1;
        }
        if (outl[outl.Count - 1] != path[path.Count - 1]) outl.Add(path[path.Count - 1]);
        return outl;
    }

    /// <summary>沿"路径上一格 -> 本格"的水平方向打膝盖射线（口径同 CsMap.cs:529-530）。</summary>
    private static bool KneeClearPrev(CsMap map, float footY, Vector3 lo, List<int[]> path, int k, Vector3 c)
    {
        if (k == 0) return true;
        var p = CellCenter(lo, path[k - 1][0], path[k - 1][1]);
        var dx = c.x - p.x; var dz = c.z - p.z;
        var dist = Mathf.Sqrt(dx * dx + dz * dz);
        if (dist < 1e-4f) return true;
        var dir = new Vector3(dx / dist, 0f, dz / dist);
        var knee = new Vector3(p.x, footY + CsConst.StepUpHeight, p.z);
        return !Physics.Raycast(knee, dir, dist, ~0, QueryTriggerInteraction.Ignore);
    }

    /// <summary>世界坐标 -> 格号（位图 origin=(-63,-72) cell=1.0，与 bot-path-check.py 同口径）。</summary>
    private static string Cell(Vector3 p)
    {
        // 口径来源：client/Assets/Resources/MapData/de_dust2.bytes 头部（origin/cell）。
        // 这里只做**显示用**的取整（判据本身一律走 map.WalkableAt 世界坐标，不依赖本函数）。
        return "(" + Mathf.FloorToInt(p.x + 63f) + "," + Mathf.FloorToInt(p.z + 72f) + ")";
    }

    private static float Horiz(Vector3 a, Vector3 b)
    {
        var dx = b.x - a.x; var dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>(A) 俯视占用图：0.5m 网格上 map.WalkableAt 的 ASCII 图。</summary>
    private static void Occupancy(CsMap map, string side, Vector3 lo, Vector3 hi)
    {
        const float grid = 0.5f, half = 12f;
        var cx = (lo.x + hi.x) * 0.5f;
        var cz = (lo.z + hi.z) * 0.5f;
        var cols = Mathf.RoundToInt(2 * half / grid) + 1;
        var rows = cols;

        Line("-- (A) 俯视占用图 " + side + " grid=0.5m box=" + F(2 * half) + "m @(" + F(cx) + "," + F(cz) +
             ")  '.'可走 '#'挡 'S'楼梯底 'E'楼梯顶 '*'底->顶直线 --");

        var sb = new StringBuilder();
        sb.Append("      x->  ");
        for (var k = 0; k < cols; k += 4)
            sb.Append(F(cx - half + k * grid).PadLeft(6));
        Line(sb.ToString());

        var sx = Mathf.RoundToInt((lo.x - (cx - half)) / grid);
        var sz = Mathf.RoundToInt((lo.z - (cz - half)) / grid);
        var ex = Mathf.RoundToInt((hi.x - (cx - half)) / grid);
        var ez = Mathf.RoundToInt((hi.z - (cz - half)) / grid);

        for (var iz = rows - 1; iz >= 0; iz--)
        {
            var row = new StringBuilder();
            row.Append((cz - half + iz * grid).ToString("F1", CultureInfo.InvariantCulture).PadLeft(7) + " ");
            for (var ix = 0; ix < cols; ix++)
            {
                var x = cx - half + ix * grid;
                var z = cz - half + iz * grid;
                char c;
                if (ix == sx && iz == sz) c = 'S';
                else if (ix == ex && iz == ez) c = 'E';
                else if (OnSegment(lo, hi, x, z)) c = '*';
                else c = map.WalkableAt(x, z) ? '.' : '#';
                row.Append(c);
            }
            Line(row.ToString());
        }
    }

    private static bool OnSegment(Vector3 lo, Vector3 hi, float x, float z)
    {
        var vx = hi.x - lo.x; var vz = hi.z - lo.z;
        var wx = x - lo.x; var wz = z - lo.z;
        var len2 = vx * vx + vz * vz;
        if (len2 < 1e-6f) return false;
        var t = (wx * vx + wz * vz) / len2;
        if (t < 0f || t > 1f) return false;
        var px = lo.x + vx * t; var pz = lo.z + vz * t;
        return Mathf.Abs(px - x) <= 0.26f && Mathf.Abs(pz - z) <= 0.26f;
    }

    /// <summary>(B) 地面剖面：沿 底->顶 直线每 0.25m 采样，逐点报四道闸门。</summary>
    private static void Profile(CsMap map, string side, Vector3 lo, Vector3 hi)
    {
        var dist = Horiz(lo, hi);
        var n = Mathf.Max(1, Mathf.CeilToInt(dist / 0.25f));
        Line("-- (B) 地面剖面 " + side + " step=0.25m n=" + n + " （g1 中心格 / g2 Δy<=0.45 / g3 法线>=0.70 / g4 膝盖射线）--");
        Line("  " + Pad("i", 4) + Pad("t", 6) + Pad("x", 10) + Pad("z", 9) + Pad("groundY", 9) + Pad("dY", 7) +
             Pad("n.y", 7) + Pad("bmp", 5) + Pad("g1", 4) + Pad("g2", 4) + Pad("g3", 4) + Pad("g4", 4) +
             Pad("canStand", 9) + Pad("dgst", 6) + "reject");

        var prevY = lo.y;
        var hasPrev = false;
        var firstRejectT = -1f;
        var stepUpMax = 0f;
        var minNormY = 2f;
        var rejectCount = 0;

        for (var i = 0; i <= n; i++)
        {
            var t = (float)i / n;
            var x = lo.x + (hi.x - lo.x) * t;
            var z = lo.z + (hi.z - lo.z) * t;

            var hasGround = map.TrySampleGround(new Vector3(x, Mathf.Max(lo.y, hi.y) + 2f, z),
                                                out var gp, out var gn, 15f);
            var groundY = hasGround ? gp.y : float.NegativeInfinity;
            var dY = hasPrev && hasGround ? groundY - prevY : 0f;
            if (hasPrev && hasGround && dY > stepUpMax) stepUpMax = dY;
            if (hasGround && gn.y < minNormY) minNormY = gn.y;

            var bmp = map.WalkableAt(x, z);
            var footY = hasPrev ? prevY : lo.y;
            var g1 = bmp;
            var g2 = !hasGround || dY <= CsConst.StepUpHeight;
            var g3 = !hasGround || gn.y >= CsConst.MaxStandableSlopeNormalZ;
            var g4 = KneeClear(footY, lo, hi, t, x, z);
            var foot = new Vector3(x, footY, z);
            var canStand = map.CanStand(foot);
            var dgst = map.GroundWithinStepForTest(foot);

            var reject = (!g1 ? "g1 " : "") + (!g2 ? "g2 " : "") + (!g3 ? "g3 " : "") + (!g4 ? "g4" : "");
            reject = reject.Trim();
            if (reject.Length == 0) reject = "-";
            else { rejectCount++; if (firstRejectT < 0f) firstRejectT = t; }

            Line("  " + Pad(i.ToString(), 4) + Pad(F(t), 6) + Pad(F(x), 10) + Pad(F(z), 9) +
                 Pad(hasGround ? F(groundY) : "-inf", 9) +
                 Pad(hasPrev && hasGround ? (dY >= 0 ? "+" : "") + F(dY) : "-", 7) +
                 Pad(hasGround ? F(gn.y) : "-", 7) + Pad(bmp ? "1" : "0", 5) +
                 Pad(g1 ? "ok" : "NO", 4) + Pad(g2 ? "ok" : "NO", 4) + Pad(g3 ? "ok" : "NO", 4) +
                 Pad(g4 ? "ok" : "NO", 4) + Pad(canStand ? "yes" : "no", 9) +
                 Pad(dgst ? "ok" : "NO", 6) + reject);

            if (hasGround) { prevY = groundY; hasPrev = true; }
        }

        Line("  剖面小结[" + side + "] 采样=" + (n + 1) + " 被拒点=" + rejectCount +
             (firstRejectT >= 0f ? " 首次被拒 t=" + F(firstRejectT) : "") +
             " 最大单段Δy=" + F(stepUpMax) + " 最小法线y=" + F(minNormY));
    }

    /// <summary>④ 膝盖射线：从 footY+StepUpHeight 沿"底->顶"水平方向打 dist（口径同 CsMap.cs:529-530）。</summary>
    private static bool KneeClear(float footY, Vector3 lo, Vector3 hi, float t, float x, float z)
    {
        var dx = (hi.x - lo.x) / Mathf.Max(1e-6f, Horiz(lo, hi));
        var dz = (hi.z - lo.z) / Mathf.Max(1e-6f, Horiz(lo, hi));
        var origin = new Vector3(x, footY + CsConst.StepUpHeight, z);
        var dir = new Vector3(dx, 0f, dz);
        if (t <= 0f) return true;
        return !Physics.Raycast(origin, dir, 0.25f, ~0, QueryTriggerInteraction.Ignore);
    }

    /// <summary>(C) 逐帧推进：每帧 ≤0.25m 调 ResolveMove；被钳住时做横向偏移扫描。</summary>
    private static void Walk(CsMap map, string side, Vector3 lo, Vector3 hi)
    {
        Line("-- (C) 逐帧推进 " + side + " step=" + F(MicroStep) + " （end = map.ResolveMove(cur,want)，与 CsMatch.StepActorPhysics 同入口）--");
        Line("  " + Pad("frame", 6) + Pad("cur", 26) + Pad("want", 26) + Pad("end", 26) +
             Pad("moved", 7) + Pad("need", 6) + Pad("clamped", 8) + Pad("reject@want", 12) + "verdict");

        var cur = lo;
        // 起点 y 先贴地（口径同 CsMatch：脚下地面 = 站高）
        var gy0 = map.SampleGround(new Vector3(lo.x, lo.y + 2f, lo.z), 15f);
        if (!float.IsNegativeInfinity(gy0)) cur = new Vector3(lo.x, gy0, lo.z);

        var stuck = -1;
        var detourFrame = -1;

        for (var f = 1; f <= MaxFrames; f++)
        {
            var toGoal = new Vector3(hi.x - cur.x, 0f, hi.z - cur.z);
            var dGoal = toGoal.magnitude;
            if (dGoal <= MicroStep + 0.01f)
            {
                Line("  " + Pad(f.ToString(), 6) + Pad(XZ(cur) + " y=" + F(cur.y), 26) +
                     Pad("-", 26) + Pad("-", 26) + Pad("-", 7) + Pad(F(dGoal), 6) + Pad("-", 8) +
                     Pad("-", 12) + "到达目标（剩余 " + F(dGoal) + " m）");
                Line("  推进小结[" + side + "] **到顶** frames=" + f + " 终点=" + V(cur) +
                     " 目标=" + V(hi) + " 平面残差=" + F(dGoal) + " 高度差=" + F(cur.y - hi.y));
                return;
            }
            var dir = toGoal / dGoal;

            var want = new Vector3(cur.x + dir.x * MicroStep, cur.y, cur.z + dir.z * MicroStep);
            var end = map.ResolveMove(cur, want);
            var moved = Horiz(cur, end);
            var clamped = moved < MicroStep - 1e-3f;

            if (moved < 0.05f)
            {
                // 被完全钳住 ⇒ 横向偏移扫描（贴左/贴右/斜着上：±30°/±60°/±90°）
                var hit = TryDetour(map, cur, dir, out var dEnd, out var dLabel);
                Line("  " + Pad(f.ToString(), 6) + Pad(XZ(cur) + " y=" + F(cur.y), 26) +
                     Pad(XZ(want), 26) + Pad(XZ(end), 26) + Pad(F(moved), 7) + Pad(F(MicroStep), 6) +
                     Pad("YES", 8) + Pad(GatesAt(map, cur, want), 12) +
                     (hit ? "横向绕行 -> " + dLabel : "卡死"));
                if (hit)
                {
                    if (detourFrame < 0) detourFrame = f;
                    cur = new Vector3(dEnd.x, GroundFollow(map, cur.y, dEnd), dEnd.z);
                    continue;
                }

                stuck = f;
                var g = GatesAt(map, cur, want);
                Line("");
                Line("  >>> 卡死点[" + side + "] frame=" + f + " cell=" + Cell(cur) + " pos=" + V(cur) +
                     " want=" + V(want) + " cell=" + Cell(want));
                Line("      need(到目标)=" + F(dGoal) + " m   剩余高度差(目标y-当前y)=" + F(hi.y - cur.y));
                Line("      四道闸门@want: " + GatesVerbose(map, cur, want));
                Line("      canStand(want)=" + map.CanStand(want) +
                     "  bitmapClear=" + map.BitmapClearForTest(want) +
                     "  groundWithinStep=" + map.GroundWithinStepForTest(want) +
                     "  rayClear=" + map.RayClearForTest(want.x, want.y, want.z) +
                     "  volumeBlocked=" + map.VolumeBlockedForTest(want.x, want.y, want.z));
                Line("      " + map.BodyHeightDiagnoseForTest(want));
                OffsetSweep(map, cur, dir, hi);
                Line("  推进小结[" + side + "] **卡在第 " + f + " 帧** cell=" + Cell(cur) + " 位置=" + V(cur) +
                     "；已走=" + F(Horiz(lo, cur)) + " m / 全程 " + F(Horiz(lo, hi)) + " m" +
                     "；剩余平面 " + F(dGoal) + " m，剩余爬升 " + F(hi.y - cur.y) + " m");
                return;
            }

            var nextY = GroundFollow(map, cur.y, end);
            var verdict = clamped ? "被钳住(贴墙滑行)" : "正常推进";
            Line("  " + Pad(f.ToString(), 6) + Pad(XZ(cur) + " y=" + F(cur.y), 26) +
                 Pad(XZ(want), 26) + Pad(XZ(end) + " y=" + F(nextY), 26) + Pad(F(moved), 7) +
                 Pad(F(MicroStep), 6) + Pad(clamped ? "yes" : "no", 8) +
                 Pad(GatesAt(map, cur, want), 12) + verdict);
            cur = new Vector3(end.x, nextY, end.z);
        }

        Line("  推进小结[" + side + "] **超过 " + MaxFrames + " 帧仍未到顶**（stuck=" + stuck +
             (detourFrame > 0 ? " 首次绕行帧=" + detourFrame : "") + "） 末位置=" + V(cur) +
             " 剩余=" + F(Horiz(cur, hi)));
    }

    /// <summary>脚下贴地（口径同 CsMatch.StepActorPhysics：落在一步台阶内就抬到地面，否则保持）。</summary>
    private static float GroundFollow(CsMap map, float feetY, Vector3 p)
    {
        var g = map.SampleGround(new Vector3(p.x, feetY + 2f, p.z), 15f);
        if (float.IsNegativeInfinity(g)) return feetY;
        return g;
    }

    /// <summary>被钳住时的横向绕行：把方向旋转 ±30/±60/±90 度，取第一个能推进的。</summary>
    private static bool TryDetour(CsMap map, Vector3 cur, Vector3 dir, out Vector3 end, out string label)
    {
        var angles = new[] { 30f, -30f, 60f, -60f, 90f, -90f };
        foreach (var a in angles)
        {
            var r = a * Mathf.Deg2Rad;
            var cs = Mathf.Cos(r); var sn = Mathf.Sin(r);
            var d = new Vector3(dir.x * cs - dir.z * sn, 0f, dir.x * sn + dir.z * cs);
            var want = new Vector3(cur.x + d.x * MicroStep, cur.y, cur.z + d.z * MicroStep);
            var e = map.ResolveMove(cur, want);
            if (Horiz(cur, e) >= 0.05f)
            {
                end = e;
                label = "rot" + F(a) + "deg -> " + XZ(e);
                return true;
            }
        }
        end = cur;
        label = "none";
        return false;
    }

    /// <summary>卡死点周围的横向偏移扫描：把 wish 方向整体平移 ±0.5/±1.0/±1.5/±2.0 m（贴左/贴右）。</summary>
    private static void OffsetSweep(CsMap map, Vector3 cur, Vector3 dir, Vector3 goal)
    {
        var perp = new Vector3(-dir.z, 0f, dir.x);
        Line("      ---- 横向偏移扫描（贴左/贴右：沿 wish 方向整体平移后再解一步）----");
        foreach (var off in new[] { 0f, 0.5f, -0.5f, 1.0f, -1.0f, 1.5f, -1.5f, 2.0f, -2.0f })
        {
            var from = new Vector3(cur.x + perp.x * off, cur.y, cur.z + perp.z * off);
            var want = new Vector3(from.x + dir.x * MicroStep, from.y, from.z + dir.z * MicroStep);
            var ok = map.CanStand(from);
            var e = map.ResolveMove(from, want);
            Line("        off=" + F(off) + " m  from=" + XZ(from) + " standableFrom=" + ok +
                 " want=" + XZ(want) + " end=" + XZ(e) + " moved=" + F(Horiz(from, e)) +
                 " gates@want=" + GatesAt(map, from, want));
        }
    }

    /// <summary>四道闸门在 want 点的判定（简短）。</summary>
    private static string GatesAt(CsMap map, Vector3 cur, Vector3 want)
    {
        var g1 = map.WalkableAt(want.x, want.z);
        var hasGround = map.TrySampleGround(new Vector3(want.x, Mathf.Max(cur.y, want.y) + 2f, want.z),
                                            out var gp, out var gn, 15f);
        var g2 = !hasGround || gp.y - cur.y <= CsConst.StepUpHeight;
        var g3 = !hasGround || gn.y >= CsConst.MaxStandableSlopeNormalZ;
        var dx = want.x - cur.x; var dz = want.z - cur.z;
        var dist = Mathf.Sqrt(dx * dx + dz * dz);
        var g4 = true;
        if (dist > 1e-4f)
        {
            var d = new Vector3(dx / dist, 0f, dz / dist);
            var knee = new Vector3(cur.x, cur.y + CsConst.StepUpHeight, cur.z);
            g4 = !Physics.Raycast(knee, d, dist, ~0, QueryTriggerInteraction.Ignore);
        }
        return (g1 ? "1" : "x") + (g2 ? "2" : "x") + (g3 ? "3" : "x") + (g4 ? "4" : "x");
    }

    private static string GatesVerbose(CsMap map, Vector3 cur, Vector3 want)
    {
        var g1 = map.WalkableAt(want.x, want.z);
        var hasGround = map.TrySampleGround(new Vector3(want.x, Mathf.Max(cur.y, want.y) + 2f, want.z),
                                            out var gp, out var gn, 15f);
        var dy = hasGround ? gp.y - cur.y : 0f;
        var g2 = !hasGround || dy <= CsConst.StepUpHeight;
        var g3 = !hasGround || gn.y >= CsConst.MaxStandableSlopeNormalZ;
        var dx = want.x - cur.x; var dz = want.z - cur.z;
        var dist = Mathf.Sqrt(dx * dx + dz * dz);
        var g4 = true;
        if (dist > 1e-4f)
        {
            var d = new Vector3(dx / dist, 0f, dz / dist);
            var knee = new Vector3(cur.x, cur.y + CsConst.StepUpHeight, cur.z);
            g4 = !Physics.Raycast(knee, d, dist, ~0, QueryTriggerInteraction.Ignore);
        }
        return "① 中心格可走=" + (g1 ? "是" : "**否**") +
               " ② 高差=" + (hasGround ? F(dy) + "m " + (g2 ? "≤0.45 通过" : ">0.45 **拒**") : "(探不到地面,不否决)") +
               " ③ 法线y=" + (hasGround ? F(gn.y) + " " + (g3 ? "≥0.70 通过" : "<0.70 **拒**(陡坡)") : "(无)") +
               " ④ 膝盖射线=" + (g4 ? "通畅" : "**被挡**");
    }

    private static string Pad(string s, int w)
    {
        if (s == null) s = "";
        return s.Length >= w ? s + " " : s.PadRight(w);
    }
}
