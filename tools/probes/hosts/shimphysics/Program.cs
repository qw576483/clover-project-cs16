// =====================================================================================
//  Program.cs —— shim-physics 宿主的驱动：把 `tools/probes/phys-stuck-probe.py` 的那个
//  世界（一条 0.50 m 宽的缝）喂给**真** `CsMap.cs`，逐帧给满额 wish，看它一步走得动走不动。
//
//  判据（与机制级探针同一套，便于两条链对照）：
//    STUCK  ⇔ N 帧累计位移 < 1e-4 m 且 每帧 wish 位移 ≥ 0.24 m（MaxSweepStep = 0.25）
//    MOVING ⇔ N 帧累计位移 ≥ N × 0.24 m（对照组：同方向、开阔地）
//  末行 `RESULT-SHIMSTUCK: PASS|FAIL`；exit 0 = PASS。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using ShimPhysics;
using UnityEngine;

internal static class Program
{
    private const int Frames = 8;
    private const float MaxSweepStep = 0.25f;      // CsMap.cs:39
    private const float Cell = 1f;

    private static int Main()
    {
        // ---- 世界：两堵墙之间的 0.50 m 缝 + 无限地面 y=0（与 phys-stuck-probe.py 同参数）----
        var world = new ShimWorld();
        world.Load(new[]
        {
            new Box(-3.0f, -0.25f, 0.0f, 3.0f, -2.0f, 6.0f, "wall_left"),
            new Box(0.25f, 3.0f, 0.0f, 3.0f, -2.0f, 6.0f, "wall_right"),
        }, groundY: 0f, cell: Cell);

        var stub = new MapDataStub { World = world };
        AddRequiredMarkers(stub);
        Game.Map = stub;
        ShimLog.Verbose = true;

        var map = new CsMap();
        var failed = "";
        map.LoadAsync("MapData/de_dust2", () => { }, reason => failed = reason);
        if (!map.IsLoaded) { Console.WriteLine("地图未加载，取证无效： " + failed); return 2; }

        Console.WriteLine("world      : 两堵墙 x∈[-3,-0.25] / [0.25,3]，y∈[0,3]，z∈[-2,6]；缝宽 0.50 m；地面 y=0；格 1.00 m");
        Console.WriteLine("body       : StandHeight={0:F2} PlayerRadius={1:F2} StepUpHeight={2:F4} GroundCheckDistance={3:F2}",
            CsConst.StandHeight, CsConst.PlayerRadius, CsConst.StepUpHeight, CsConst.GroundCheckDistance);
        Console.WriteLine("code       : 真 CsMap.cs（逐字） + shim UnityEngine.Physics（解析几何）");
        Console.WriteLine();

        // ---- case A：身体在缝内（x=0，两侧墙各在 ±0.25）----
        var aStart = new Vector3(0f, 0f, 3f);
        var aDir = new Vector3(0f, 0f, 1f);
        var aTotal = RunCase(map, aStart, aDir, Frames, out var aEnd);

        Console.WriteLine("case A start=({0:F2},{1:F2},{2:F2})  bitmap={3}  canStand={4}  sampleGround={5:F3}",
            aStart.x, aStart.y, aStart.z, map.WalkableAt(aStart.x, aStart.z),
            map.CanStand(aStart), map.SampleGround(aStart));
        TraceFirstFrame(map, aStart, aDir);
        Console.WriteLine("case A total={0:F4} m  end=({1:F2},{2:F2})", aTotal, aEnd.x, aEnd.z);
        Console.WriteLine();

        // ---- case B：对照组（同方向、开阔地 x=5）----
        var bStart = new Vector3(5f, 0f, 3f);
        var bTotal = RunCase(map, bStart, aDir, Frames, out var bEnd);
        Console.WriteLine("case B start=({0:F2},{1:F2},{2:F2})  bitmap={3}  canStand={4}",
            bStart.x, bStart.y, bStart.z, map.WalkableAt(bStart.x, bStart.z), map.CanStand(bStart));
        Console.WriteLine("case B total={0:F4} m  end=({1:F2},{2:F2})  (期望 ≈ {3} × {4:F2} = {5:F2})",
            bTotal, bEnd.x, bEnd.z, Frames, MaxSweepStep, Frames * MaxSweepStep);
        Console.WriteLine();

        var stuckA = aTotal < 1e-4f;
        var movingB = bTotal >= Frames * 0.24f;
        Console.WriteLine("verdict    : A_STUCK={0} B_MOVING={1}", stuckA, movingB);
        var ok = stuckA && movingB;
        Console.WriteLine("RESULT-SHIMSTUCK: {0}", ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>连续 <paramref name="frames"/> 帧、每帧给满额 wish（MaxSweepStep 米）后的累计位移。</summary>
    private static float RunCase(CsMap map, Vector3 start, Vector3 dir, int frames, out Vector3 end)
    {
        var cur = start;
        var total = 0f;
        for (var i = 0; i < frames; i++)
        {
            var want = new Vector3(cur.x + dir.x * MaxSweepStep, cur.y, cur.z + dir.z * MaxSweepStep);
            var next = map.ResolveMove(cur, want);
            total += new Vector3(next.x - cur.x, 0f, next.z - cur.z).magnitude;
            cur = new Vector3(next.x, start.y, next.z);
        }
        end = cur;
        return total;
    }

    /// <summary>第 1 帧的逐步 trace（真代码的 `ResolveMoveTraceForTest`，与产品走同一条实现）。</summary>
    private static void TraceFirstFrame(CsMap map, Vector3 start, Vector3 dir)
    {
        var want = new Vector3(start.x + dir.x * MaxSweepStep, start.y, start.z + dir.z * MaxSweepStep);
        var rows = new List<string>();
        map.ResolveMoveTraceForTest(start, want, (i, w, prev, cur) =>
        {
            rows.Add(string.Format(CultureInfo.InvariantCulture,
                "  frame1 step{0} want=({1:F3},{2:F3}) prev=({3:F3},{4:F3}) cur=({5:F3},{6:F3})",
                i, w.x, w.z, prev.x, prev.z, cur.x, cur.z));
        });
        foreach (var r in rows) Console.WriteLine(r);
        if (rows.Count == 0) Console.WriteLine("  frame1 无 trace 行（一段就走完）");
    }

    /// <summary>把 `CsMap.RequiredMarkerNames` 点名的 13 个标记各放一个点（只为消掉契约告警）。</summary>
    private static void AddRequiredMarkers(MapDataStub stub)
    {
        foreach (var n in new[]
        {
            CsMarkers.SpawnT, CsMarkers.SpawnCT, CsMarkers.BombsiteA, CsMarkers.BombsiteB,
            CsMarkers.BuyZoneT, CsMarkers.BuyZoneCT, CsMarkers.TAttackA, CsMarkers.TAttackB,
            CsMarkers.TMid, CsMarkers.CTDefendA, CsMarkers.CTDefendB, CsMarkers.CTMid, CsMarkers.Patrol,
        })
        {
            stub.AddPoint(n, new Vector3(0f, 0f, 3f));
        }
    }
}
