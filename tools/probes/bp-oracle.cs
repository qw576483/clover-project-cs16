// ============================================================================
// 判据资产（切片BQ 2026-09-22 从 `.ai-tmp/test/` 迁入 `tools/probes/`，与 bo-reach.cs /
// bot-hold-plant.cs 等同一约定：**判据资产必须留在盘上可复跑**，删了就没法重判同一件事）。
//
// 用法（必须在 client/ 目录下跑，且 Play 会话已起、地图已加载）：
//   unity command run_script --file <abs>/tools/probes/bp-oracle.cs --entry BpOracle.Run
//         --args ["<abs out path>"]        # 默认输出 .ai-tmp/test/bp-oracle.txt
//   驱动侧：`.ai-tmp/drivers/bp-play.ps1` 的 phase 4（`$oracle` 指向本文件）自动调用它。
//
// 为什么**必须在 Play 内跑**（离线脚本代替不了它）：
//   它问的是**产品自己**的三件"只有 Play 里才存在"的事实 ——
//     ① 运行时 `Game.Map` 的 Origin / CellSize 与由它派生的格心（离线脚本只能自己读 .bytes 头）；
//     ② `CsMap._groundRayMask`（层名 → 层号只在运行时解析，退化会让贴地射线命中角色自己）；
//     ③ 列几何在**活的 PhysX** 里长什么样（Collider 只在 Play 存在；edit-mode 下 collidersTotal = 0）。
//   它只**调用**产品的 `ICsMap.SampleGround` / `BotNavigator.GroundYAbove` / `BuildHeightReach`，
//   ⛔ 不重新实现任何射线。
//   只读：不改比赛状态、不写 state.txt、不注入输入。
// ============================================================================
// slice BP -- THE ORACLE. Runs INSIDE a live Play session and asks the PRODUCT ITSELF
// (no re-implementation of the ray) why the height-consistency layer collapsed the
// reach set at cells like (80,104) / (83,104) while the frozen offline instrument
// (bo-geom.cs / bo-cell-faces.tsv) said those very cells PASS with dy=0.000.
//
// It settles the three variables slice BO could not:
//   (1) the PRODUCT's own Game.Map.Origin / CellSize in Play (vs the offline self-read
//       of the .bytes header) + the cell centres derived from them;
//   (2) the PRODUCT's own CsMap._groundRayMask (0 / missing CsWorld / real);
//   (3) the column geometry as the live PhysX sees it (colliders only exist in Play).
//
// Product entry points used (never re-implemented, always called):
//   ICsMap.SampleGround / TrySampleGround       Module/Map/CsMap.cs:534 / 542-557
//   CsMap.GroundMask (private)                  Module/Map/CsMap.cs:571-603
//   BotNavigator.CellCentre (private static)    Module/Bot/BotNavigator.cs:946-951
//   BotNavigator.GroundYAbove (private)         Module/Bot/BotNavigator.cs:879-884
//   BotNavigator.BuildHeightReach (private)     Module/Bot/BotNavigator.cs:781-871
//   Game.Map (engine)                           Packages/com.clover.unity-engine/Runtime/Presentation/Map.cs:37-45
//
// GroundYAbove / BuildHeightReach are invoked on a THROWAWAY `new BotNavigator()`
// (BindMap'ed, never ticking) => live bots are NOT touched, no state is mutated.
// Read-only w.r.t. the match: never writes state.txt, never injects input.
//
// Usage (inside client/, Play must be live with the map loaded + bots running):
//   unity command run_script --file <abs path> --entry BpOracle.Run --args ["<out path>"]
// Product: <abs out path> (default .ai-tmp/test/bp-oracle.txt)
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Bot;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

public static class BpOracle
{
    private static readonly CultureInfo CI = CultureInfo.GetCultureInfo("en-US");
    private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.NonPublic;

    // CsConst.GroundCheckDistance (Core/CsConst.cs:114) / CsConst.StepUpHeight (Core/CsConst.cs:113)
    private const float Gcd = 0.12f;
    private const float StepUp = 0.45f;

    private static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "na";
        return v.ToString("F3", CI);
    }

    private static object Field(object o, string name, BindingFlags fl)
    {
        if (o == null) return null;
        var f = o.GetType().GetField(name, fl);
        return f == null ? null : f.GetValue(o);
    }

    private static object Call(object o, string name, BindingFlags fl, params object[] args)
    {
        if (o == null) return null;
        var m = o.GetType().GetMethod(name, fl);
        return m == null ? null : m.Invoke(o, args);
    }

    private static int CountOf(object collection)
    {
        if (collection == null) return -1;
        var p = collection.GetType().GetProperty("Count");
        return p == null ? -1 : (int)p.GetValue(collection);
    }

    private static bool Contains(object collection, int x, int z)
    {
        if (collection == null) return false;
        var m = collection.GetType().GetMethod("Contains");
        if (m == null) return false;
        return (bool)m.Invoke(collection, new object[] { new Vector2Int(x, z) });
    }

    public static string Run(string outPath)
    {
        if (string.IsNullOrEmpty(outPath)) outPath = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\bp-oracle.txt";
        var sb = new StringBuilder();
        var note = new StringBuilder();

        // ---- (1) the PRODUCT's own map truth ----------------------------------
        var map = Game.Map;
        sb.Append("== MAP: engine Game.Map (the product's own IMapData) ==\n");
        if (map == null)
        {
            sb.Append("Game.Map = null  => NOT in a map session\n");
        }
        else
        {
            sb.Append("loaded=").Append(map.Loaded).Append(" name=").Append(map.Name)
              .Append(" version=").Append(map.Version).Append(" sceneId=").Append(map.SceneId)
              .Append(" cellSize=").Append(F(map.CellSize))
              .Append(" origin=(").Append(F(map.Origin.x)).Append(",").Append(F(map.Origin.y)).Append(",").Append(F(map.Origin.z)).Append(")")
              .Append(" width=").Append(map.Width).Append(" depth=").Append(map.Depth).Append('\n');
        }

        // ---- the live business map object (ICsMap = CsMap) + its private mask --
        BotModule bots = null;
        var mod = MatchModule.Instance;
        if (mod != null) bots = mod.GetComponent<BotModule>();
        if (bots == null) bots = UnityEngine.Object.FindAnyObjectByType<BotModule>(FindObjectsInactive.Include);
        ICsMap csmap = bots != null ? bots.Map : null;
        if (csmap == null)
        {
            var mm = UnityEngine.Object.FindAnyObjectByType<CsMapModule>(FindObjectsInactive.Include);
            if (mm != null) csmap = mm.Map;
        }

        sb.Append("== MASK: the product's own ground-ray mask ==\n");
        sb.Append("csmap=").Append(csmap == null ? "null" : csmap.GetType().FullName)
          .Append(" isLoaded=").Append(csmap != null && csmap.IsLoaded).Append('\n');
        int cachedMask = -9999, maskNow = -9999;
        if (csmap != null)
        {
            var cached = Field(csmap, "_groundRayMask", Inst);
            if (cached != null) cachedMask = (int)cached;
            var forced = Call(csmap, "GroundMask", Inst);
            if (forced != null) maskNow = (int)forced;
        }
        int worldNameLayer = LayerMask.NameToLayer("CsWorld");               // CsConst.PhysicsLayers.WorldName
        int worldLayer = LayerMask.NameToLayer("CsWorld");
        sb.Append("NameToLayer(\"CsWorld\")=").Append(worldNameLayer)
          .Append(" expectedMask=0x").Append(worldLayer >= 0 ? (1 << worldLayer).ToString("X8") : "FFFFFFFF")
          .Append(" _groundRayMask(cached field)=").Append(cachedMask == -9999 ? "na" : "0x" + cachedMask.ToString("X8"))
          .Append(" GroundMask()(forced now)=").Append(maskNow == -9999 ? "na" : "0x" + maskNow.ToString("X8"))
          .Append('\n');
        int useMask = maskNow != -9999 ? maskNow : (worldLayer >= 0 ? 1 << worldLayer : ~0);

        // ---- throwaway navigator: the product's own private sampling methods ---
        BotNavigator nav = null;
        MethodInfo mGround = null, mCell = null, mReach = null;
        if (csmap != null)
        {
            nav = new BotNavigator();
            nav.BindMap(csmap);                                    // BotNavigator.cs:155-158 (public)
            var t = typeof(BotNavigator);
            mGround = t.GetMethod("GroundYAbove", Inst);            // (Vector2Int, float) -> float
            mCell = t.GetMethod("CellCenter", Stat);                // (Vector2Int) -> Vector3
            mReach = t.GetMethod("BuildHeightReach", Inst);         // (Vector2Int, Vector3) -> HashSet<Vector2Int>
        }
        sb.Append("== PRODUCT API: reflection targets ==\n");
        sb.Append("GroundYAbove=").Append(mGround != null ? "ok" : "MISSING")
          .Append(" CellCenter=").Append(mCell != null ? "ok" : "MISSING")
          .Append(" BuildHeightReach=").Append(mReach != null ? "ok" : "MISSING").Append('\n');

        // ---- target cells: the L3 log's collapsed cells + the crate cluster -----
        // foot y = the L3 log's own value ("从格 (80,104) … 脚下地面 y=-3.251", slice BN/BO
        // height-layer log line) and the `footY` column of bo-cells.tsv (all -3.251).
        int[] cells = new[] { 80, 104, 81, 103, 83, 103, 83, 104, 81, 104, 80, 103,
                              72, 101, 72, 102, 72, 103, 73, 101, 73, 102, 74, 102, 74, 103 };
        const float footY = -3.251f;
        var colBuf = new Collider[24];

        sb.Append("\n== CELLS (foot y = ").Append(F(footY)).Append(" from the L3 log line) ==\n");
        sb.Append("cell\tcellCentre(prod)\twalkableAt(ICsMap)\twalkableAt(engine)\t");
        sb.Append("G_fromFoot\tG_fromFootPlusStepUp\tGROUNDYABOVE(prod private)\tprodDy\tprodNormalY\t");
        sb.Append("facesProductMask\tfacesAllLayers\toriginInsideColliders\n");

        for (int i = 0; i + 1 < cells.Length; i += 2)
        {
            int cx = cells[i], cz = cells[i + 1];
            var cell = new Vector2Int(cx, cz);

            // product's own cell centre (private static CellCenter -> Game.Map.Origin/CellSize)
            Vector3 c = Vector3.zero;
            if (mCell != null)
            {
                var r = mCell.Invoke(null, new object[] { cell });
                if (r != null) c = (Vector3)r;
            }
            float cxw = map != null
                ? map.Origin.x + (cx + 0.5f) * map.CellSize
                : c.x;
            float czw = map != null
                ? map.Origin.z + (cz + 0.5f) * map.CellSize
                : c.z;

            bool wIcs = csmap != null && csmap.WalkableAt(cxw, czw);
            bool wEng = map != null && map.WalkableAt(cxw, czw);

            // the product's own ground probes
            float gFoot = csmap != null ? csmap.SampleGround(new Vector3(cxw, footY, czw)) : float.NegativeInfinity;
            float gUp = csmap != null ? csmap.SampleGround(new Vector3(cxw, footY + StepUp, czw)) : float.NegativeInfinity;
            Vector3 pt = Vector3.zero; Vector3 nrm = Vector3.up;
            bool okTry = csmap != null && csmap.TrySampleGround(new Vector3(cxw, footY + StepUp, czw), out pt, out nrm);
            float gAbove = float.NegativeInfinity;
            if (mGround != null && nav != null)
            {
                var r = mGround.Invoke(nav, new object[] { cell, footY });
                if (r != null) gAbove = (float)r;
            }
            string prodDy = gAbove < 0f ? "no-ground" : F(gAbove - footY);

            // all faces in the column with the product's own mask + with all layers
            var fProd = new StringBuilder();
            var hs = Physics.RaycastAll(new Vector3(cxw, footY + 30f, czw), Vector3.down, 60f, useMask, QueryTriggerInteraction.Ignore);
            var ordered = new List<RaycastHit>(hs);
            ordered.Sort((a, b) => b.point.y.CompareTo(a.point.y));
            foreach (var h in ordered)
            {
                if (fProd.Length > 0) fProd.Append('|');
                fProd.Append("y=").Append(F(h.point.y)).Append("/dy=").Append(F(h.point.y - footY))
                     .Append("/n=").Append(F(h.normal.y))
                     .Append("/L").Append(h.collider.gameObject.layer).Append("/").Append(h.collider.name);
            }
            int nAll = Physics.RaycastAll(new Vector3(cxw, footY + 30f, czw), Vector3.down, 60f, ~0, QueryTriggerInteraction.Ignore).Length;

            // is the sample origin (foot + StepUp + GroundCheckDistance, exactly what
            // CsMap.TrySampleGround builds at CsMap.cs:545) INSIDE a solid?  (the alpha shape)
            var inside = new StringBuilder();
            var probe = new Vector3(cxw, footY + StepUp + Gcd, czw);
            int nOv = Physics.OverlapBoxNonAlloc(probe, new Vector3(0.05f, 0.05f, 0.05f), colBuf, Quaternion.identity, useMask, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < nOv && k < colBuf.Length; k++)
            {
                if (inside.Length > 0) inside.Append('|');
                var b = colBuf[k].bounds;
                inside.Append(colBuf[k].name).Append("/L").Append(colBuf[k].gameObject.layer)
                      .Append("/tag=").Append(colBuf[k].tag)
                      .Append("[y ").Append(F(b.min.y)).Append("..").Append(F(b.max.y)).Append("]");
            }
            // second probe: the product's plain foot probe (foot + GroundCheckDistance)
            var inside2 = new StringBuilder();
            var probe2 = new Vector3(cxw, footY + Gcd, czw);
            int nOv2 = Physics.OverlapBoxNonAlloc(probe2, new Vector3(0.05f, 0.05f, 0.05f), colBuf, Quaternion.identity, useMask, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < nOv2 && k < colBuf.Length; k++)
            {
                if (inside2.Length > 0) inside2.Append('|');
                inside2.Append(colBuf[k].name).Append("/L").Append(colBuf[k].gameObject.layer);
            }

            sb.Append("(").Append(cx).Append(",").Append(cz).Append(")\t");
            sb.Append("(").Append(F(c.x)).Append(",").Append(F(c.z)).Append(")");
            sb.Append(" [api] vs (").Append(F(cxw)).Append(",").Append(F(czw)).Append(") [Game.Map]]\t");
            sb.Append(wIcs).Append('\t').Append(wEng).Append('\t');
            sb.Append(F(gFoot)).Append('\t').Append(F(gUp)).Append('\t').Append(F(gAbove)).Append('\t');
            sb.Append(prodDy).Append('\t').Append(okTry ? F(nrm.y) : "na").Append('\t');
            sb.Append(fProd.Length == 0 ? "NONE" : fProd.ToString()).Append('\t');
            sb.Append(nAll).Append('\t');
            sb.Append(inside.Length == 0 ? "none@stepup" : inside.ToString());
            sb.Append(" ; foot=").Append(inside2.Length == 0 ? "none" : inside2.ToString()).Append('\n');
        }

        // ---- the product's own reach expansion (private BuildHeightReach) -------
        sb.Append("\n== REACH (product's own BotNavigator.BuildHeightReach on a throwaway instance) ==\n");
        sb.Append("from\tfoot/spawnY\treach\tstartInReach\n");
        var starts = new List<object[]> { new object[] { "LOG(80,104)", 80, 104, footY },
                                          new object[] { "LOG(73,102)", 73, 102, footY } };
        // a real spawn cell (marker driven, the same source as the match spawns)
        if (csmap != null && csmap.SpawnPointCount > 0)
        {
            var sp = csmap.GetSpawnPoint(0);
            float fx = map != null ? (sp.x - map.Origin.x) / map.CellSize : 0f;
            float fz = map != null ? (sp.z - map.Origin.z) / map.CellSize : 0f;
            starts.Add(new object[] { "SPAWN0(" + Mathf.FloorToInt(fx) + "," + Mathf.FloorToInt(fz) + ")=" + sp.ToString("F2"),
                                      Mathf.FloorToInt(fx), Mathf.FloorToInt(fz), sp.y });
        }
        foreach (var st in starts)
        {
            if (mReach == null || nav == null) { sb.Append(st[0]).Append("\tno-method\n"); continue; }
            int scx = (int)st[1], scz = (int)st[2];
            var res = mReach.Invoke(nav, new object[] { new Vector2Int(scx, scz), new Vector3(
                map.Origin.x + (scx + 0.5f) * map.CellSize, (float)st[3], map.Origin.z + (scz + 0.5f) * map.CellSize) });
            sb.Append(st[0]).Append('\t').Append(F((float)st[3])).Append('\t').Append(CountOf(res))
              .Append('\t').Append(Contains(res, scx, scz)).Append('\n');
        }

        // ---- what the LIVE bots' own height layer (private fields, read-only) --
        sb.Append("\n== LIVE BOTS: private height-layer state (read-only reflection) ==\n");
        sb.Append("bot\thasHeightReach\tfromCell\treachCount\tneighbour(81,104)InReach\t(80,104)InReach\n");
        var brains = Field(bots, "_brains", Inst) as IDictionary;
        int nBots = 0;
        if (brains != null)
        {
            foreach (var v in brains.Values)
            {
                var nv = Field(v, "_nav", Inst);
                if (nv == null) continue;
                var has = Field(nv, "_hasHeightReach", Inst);
                var from = Field(nv, "_heightReachFrom", Inst);
                var set = Field(nv, "_heightReach", Inst);
                string nm = Field(v, "_name", Inst) as string;
                sb.Append(nm ?? "?").Append('\t').Append(has).Append('\t').Append(from ?? "null").Append('\t')
                  .Append(CountOf(set)).Append('\t').Append(Contains(set, 81, 104)).Append('\t').Append(Contains(set, 80, 104)).Append('\n');
                nBots++;
                if (nBots >= 10) break;
            }
        }
        sb.Append("botsSeen=").Append(nBots).Append('\n');

        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        note.Append("MAP ").Append(map != null && map.Loaded ? "loaded" : "NOT-LOADED")
            .Append(" cellSize=").Append(map != null ? F(map.CellSize) : "na")
            .Append(" origin=").Append(map != null ? "(" + F(map.Origin.x) + "," + F(map.Origin.y) + "," + F(map.Origin.z) + ")" : "na")
            .Append(" mask=0x").Append(useMask.ToString("X8"))
            .Append(" bots=").Append(nBots)
            .Append(" -> ").Append(outPath);
        return note.ToString();
    }
}
