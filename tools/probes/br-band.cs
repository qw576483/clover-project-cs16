// ============================================================================
// slice BR -- 判据资产: the "cell centre" vs "whole cell" mismatch, measured on the LIVE geometry.
//
//   (Assets/Editor/MapGen/Dust2GeoData.cs:213-247 BuildBlockedBitmap(): "cell column vs blocker AABB"),
// while every runtime consumer samples the CELL CENTRE (Module/Bot/BotNavigator.cs CellCenter /
// GroundYAbove, ICsMap.TrySampleGround). This script measures, in ONE Play session:
//   ① cells that the bitmap calls walkable but whose CELL CENTRE has no legal landing face inside
//      [foot - maxDrop, foot + StepUpHeight]; of those, how many are RESCUED by the in-cell 5x5 grid
//      (= the fix in BotNavigator.CellLandingFace); and how big the "false connectivity edge" set is;
//   ② four path-existence questions (CT/T spawn -> Bombsite_A / Bombsite_B) before and after the fix,
//      plus whether the rescued cells are CUT VERTICES (must-pass) on those paths;
//   ③ the two-sided verdict table (same column layout as slice BO/BP's instrument) re-judged with the
//      NEW cell rule, so tools/probes/height-consistent-predict.py can print the criterion on it.
//
// Ray semantics: mirrors the product EXACTLY, never re-invents the query.
//   Module/Map/CsMap.cs:542-557 TrySampleGround = single Physics.Raycast(origin + (0,Gcd,0), down,
//   maxDrop + Gcd, GroundMask(), QueryTriggerInteraction.Ignore); a downward single ray yields the
//   TOPMOST face of the column (== bo-reach.cs's `newTopY`).
//   Landing face (CsMap.cs:520-522 TryStepUp, verbatim): dy <= CsConst.StepUpHeight (0.45),
//   dy >= -maxDrop, normal.y >= CsConst.MaxStandableSlopeNormalZ (0.7). CsConst is READ, never changed.
//
// Marker cells (goals) come from the product's own marker set, read by slice-BJ's instrument
// tools/probes/marker-connectivity.py out of Resources/MapData/de_dust2_markers.bytes
// (cells listed in .ai-tmp/test/bq-analysis.txt, "Bombsite_A/B  [i] cell=(..)").
//
// No args. Reads : .ai-tmp/test/bo-cells.tsv (start cells), .ai-tmp/test/bo-want-in.tsv (frozen rows)
// Writes         : .ai-tmp/test/br/bp-reach.tsv, .ai-tmp/test/br/bp-want-verdict.tsv,
//                  .ai-tmp/test/br/br-band-summary.txt
// ============================================================================
var CI = System.Globalization.CultureInfo.GetCultureInfo("en-US");
var proj = @"c:\Work\Server\f-v2\clover-project-cs16";
var tmp = proj + @"\.ai-tmp\test";
var outDir = tmp + @"\br";
System.IO.Directory.CreateDirectory(outDir);
var cellsP = tmp + @"\bo-cells.tsv";
var wantP = tmp + @"\bo-want-in.tsv";
var outReachP = outDir + @"\bp-reach.tsv";
var outWantP = outDir + @"\bp-want-verdict.tsv";
var outSumP = outDir + @"\br-band-summary.txt";
var mapP = proj + @"\client\Assets\MapData\de_dust2.bytes";
var t0 = System.Diagnostics.Stopwatch.StartNew();

const float Gcd = 0.12f;        // CsConst.GroundCheckDistance (Core/CsConst.cs:114)
const float StepUp = 0.45f;     // CsConst.StepUpHeight       (Core/CsConst.cs:113)
const float MaxDrop = 8f;       // CsMap.SampleGround default (Module/Map/CsMap.cs:534)
const float SlopeMinN = 0.7f;   // CsConst.MaxStandableSlopeNormalZ (Module/Map/CsMap.cs:521)
const int MaxCells = 20000;     // CsBotConst.HeightReachMaxCells (same guard as the product)

// ---- map header + walkable bitmap (engine MapFormat.cs:54-75 / 243-252) ----
byte[] bits; float cellSize, ox, oz; int W, D;
{
    var ms = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(mapP));
    var br = new System.IO.BinaryReader(ms);
    var magic = br.ReadBytes(4);
    if (magic[0] != (byte)'C' || magic[1] != (byte)'L') return "bad magic";
    br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt64();
    cellSize = br.ReadSingle();
    ox = br.ReadSingle(); br.ReadSingle(); oz = br.ReadSingle();
    W = (int)br.ReadUInt32(); D = (int)br.ReadUInt32();
    br.ReadUInt32(); br.ReadUInt32();
    int nameLen = (int)br.ReadUInt32();
    br.ReadBytes(12);
    if (nameLen > 0) br.ReadBytes(nameLen);
    bits = br.ReadBytes((int)(((long)W * D + 7) / 8));
}

int worldLayer = UnityEngine.LayerMask.NameToLayer("CsWorld");
int mask = worldLayer >= 0 ? (1 << worldLayer) : ~0;
var hits = new UnityEngine.RaycastHit[24];
// the product's own 5x5 in-cell offsets (BotNavigator.cs:InCellFrac), same order => same tie-break
var frac = new float[] { 0f, -0.2f, 0.2f, -0.4f, 0.4f };
var stepX = new int[] { 1, 1, 0, -1, -1, -1, 0, 1 };
var stepZ = new int[] { 0, 1, 1, 1, 0, -1, -1, -1 };

string F(float v) {
    if (float.IsNaN(v) || float.IsInfinity(v)) return "na";
    return v.ToString("F3", CI);
}
bool WalkableCell(int cx, int cz) {
    if (cx < 0 || cz < 0 || cx >= W || cz >= D) return false;
    int idx = cz * W + cx;
    return (bits[idx >> 3] & (1 << (idx & 7))) != 0;
}
float CellX(int cx) { return ox + (cx + 0.5f) * cellSize; }   // BotNavigator.cs:CellCenter
float CellZ(int cz) { return oz + (cz + 0.5f) * cellSize; }
long Key(int cx, int cz) { return ((long)cx << 32) | (uint)cz; }

// single downward ray == CsMap.TrySampleGround (origin foot+StepUp, the caller of GroundYAbove)
bool Probe(float footY, float x, float z, out float y, out float ny) {
    UnityEngine.RaycastHit h;
    y = float.NaN; ny = 0f;
    if (UnityEngine.Physics.Raycast(new UnityEngine.Vector3(x, footY + StepUp + Gcd, z),
            UnityEngine.Vector3.down, out h, MaxDrop + StepUp + Gcd, mask,
            UnityEngine.QueryTriggerInteraction.Ignore)) {
        y = h.point.y; ny = h.normal.y; return true;
    }
    return false;
}
// CsMap.cs:520-522 verbatim: in-band AND standable
bool Landable(float footY, float y, float ny) {
    if (float.IsNaN(y)) return false;
    float dy = y - footY;
    return dy <= StepUp && dy >= -MaxDrop && ny >= SlopeMinN;
}
// the product's PRE-BR centre rule (slice BN/BP, BotNavigator.cs:891-898 before this slice):
// a single sample, in-band = accepted, NO normal test at all (the normal test lives in CsMap.TryStepUp,
// not in the height layer's GroundYAbove) -> NaN when there is no hit or the hit is outside the band.
bool InBand(float footY, float y) {
    if (float.IsNaN(y)) return false;
    float dy = y - footY;
    return dy <= StepUp && dy >= -MaxDrop;
}
float OldY(float footY, float x, float z) {
    float y, ny;
    if (!Probe(footY, x, z, out y, out ny)) return float.NaN;
    return InBand(footY, y) ? y : float.NaN;
}
// slice-BN's sentinel bug (BotNavigator.cs:838 `hN < 0f`): a real NEGATIVE floor counted as no-ground
float ProdBefore(float footY, float x, float z) {
    float y = OldY(footY, x, z);
    if (float.IsNaN(y) || y < 0f) return float.NaN;
    return y;
}
// the NEW cell rule (slice BR): centre first, then the 5x5 grid, nearest-in-band wins.
// returns NaN when the WHOLE cell has no legal landing face.
float CellRule(float footY, int cx, int cz, out bool centerFail, out int nLive) {
    float bx = CellX(cx), bz = CellZ(cz);
    centerFail = false; nLive = 0;
    float y, ny;
    if (Probe(footY, bx, bz, out y, out ny) && InBand(footY, y)) { nLive = 1; return y; }  // centre: unchanged rule
    centerFail = true;
    float best = float.NaN, bestAbs = float.MaxValue;
    for (int i = 0; i < frac.Length; i++)
        for (int k = 0; k < frac.Length; k++) {
            if (i == 0 && k == 0) continue;
            Probe(footY, bx + frac[i] * cellSize, bz + frac[k] * cellSize, out y, out ny);
            if (!Landable(footY, y, ny)) continue;
            nLive++;
            float ad = y - footY; if (ad < 0) ad = -ad;
            if (ad < bestAbs - 1e-4f) { bestAbs = ad; best = y; }
        }
    return best;
}

// ==========================================================================
// ① per-cell scan: the bitmap's whole-cell claim vs the cell-centre sample
// ==========================================================================
int nWalk = 0, nCentreFail = 0, nRescued = 0, nStillDead = 0, nNoFloor = 0, nCentreLive = 0;
float loMin = 9e9f, loMax = -9e9f;
var rescueSamples = new System.Text.StringBuilder();
var deadSamples = new System.Text.StringBuilder();
for (int cz = 0; cz < D; cz++) {
    for (int cx = 0; cx < W; cx++) {
        if (!WalkableCell(cx, cz)) continue;
        nWalk++;
        // the cell's own floor = the LOWEST top face of the centre column (a +50 m ray, all hits)
        float lo = float.NaN;
        int nh = UnityEngine.Physics.RaycastNonAlloc(new UnityEngine.Vector3(CellX(cx), 50f, CellZ(cz)),
                     UnityEngine.Vector3.down, hits, 200f, mask, UnityEngine.QueryTriggerInteraction.Ignore);
        for (int i = 0; i < nh; i++) { float y = hits[i].point.y; if (y > -40f && (float.IsNaN(lo) || y < lo)) lo = y; }
        if (float.IsNaN(lo)) { nNoFloor++; continue; }
        if (lo < loMin) loMin = lo; if (lo > loMax) loMax = lo;
        float y0, ny0;
        Probe(lo, CellX(cx), CellZ(cz), out y0, out ny0);
        if (InBand(lo, y0)) { nCentreLive++; continue; }   // the product's own centre rule (in-band, no normal test)
        nCentreFail++;
        bool cf; int nl;
        float mp = CellRule(lo, cx, cz, out cf, out nl);
        if (float.IsNaN(mp)) {
            nStillDead++;
            if (deadSamples.Length < 900) deadSamples.Append("(" + cx + "," + cz + ") floor=" + F(lo) + "\n");
        } else {
            nRescued++;
            if (rescueSamples.Length < 900)
                rescueSamples.Append("(" + cx + "," + cz + ") floor=" + F(lo) + " centreHit=" + F(y0) +
                                     " -> rescuedY=" + F(mp) + " dy=" + F(mp - lo) + " livePts=" + nl + "\n");
        }
    }
}

// ==========================================================================
// ② reach (both rules) from the two spawn cells + recorded live-edge graph
// ==========================================================================
// mode 0 = OLD (centre only), 1 = NEW cell rule (centre -> 5x5), 2 = PROD-BEFORE (BN sentinel bug)
string Reach(int cx0, int cz0, float footY, int mode, out int blocked, out int saved,
             out System.Collections.Generic.Dictionary<long, float> h,
             out System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>> adj) {
    blocked = 0; saved = 0;
    h = new System.Collections.Generic.Dictionary<long, float>();
    adj = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>>();
    var reach = new System.Collections.Generic.HashSet<long>();
    var q = new System.Collections.Generic.Queue<int[]>();
    long k0 = Key(cx0, cz0);
    reach.Add(k0); h[k0] = footY; q.Enqueue(new int[] { cx0, cz0 });
    while (q.Count > 0) {
        var cur = q.Dequeue();
        long kc = Key(cur[0], cur[1]);
        float hCur = h[kc];
        for (int k = 0; k < 8; k++) {
            int nx = cur[0] + stepX[k], nz = cur[1] + stepZ[k];
            long kn = Key(nx, nz);
            if (!WalkableCell(nx, nz)) continue;
            if (reach.Count > MaxCells) { blocked = -1; return "over-cap"; }
            float hN; bool cf; int nl;
            if (mode == 2) hN = ProdBefore(hCur, CellX(nx), CellZ(nz));
            else if (mode == 1) hN = CellRule(hCur, nx, nz, out cf, out nl);
            else hN = OldY(hCur, CellX(nx), CellZ(nz));
            if (float.IsNaN(hN)) { if (!reach.Contains(kn)) blocked++; continue; }
            if (hN - hCur > StepUp) { if (!reach.Contains(kn)) blocked++; continue; }
            if (reach.Contains(kn)) {
                // already reachable: still a live edge (needed for the cut-vertex graph)
                if (!adj.ContainsKey(kc)) adj[kc] = new System.Collections.Generic.List<long>();
                adj[kc].Add(kn);
                continue;
            }
            reach.Add(kn); h[kn] = hN; q.Enqueue(new int[] { nx, nz });
            if (!adj.ContainsKey(kc)) adj[kc] = new System.Collections.Generic.List<long>();
            adj[kc].Add(kn);
        }
    }
    return reach.Count.ToString(CI);
}
// shortest edge count on the RECORDED live-edge graph (no rays) + cut-vertex test
int GraphDist(System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>> adj, long start, long goal,
              System.Collections.Generic.HashSet<long> banned) {
    if (banned != null && banned.Contains(start)) return -1;
    var dist = new System.Collections.Generic.Dictionary<long, int>();
    var q = new System.Collections.Generic.Queue<long>();
    dist[start] = 0; q.Enqueue(start);
    while (q.Count > 0) {
        long c = q.Dequeue();
        if (c == goal) return dist[c];
        System.Collections.Generic.List<long> ns;
        if (!adj.TryGetValue(c, out ns)) continue;
        for (int i = 0; i < ns.Count; i++) {
            long n = ns[i];
            if (banned != null && banned.Contains(n)) continue;
            if (dist.ContainsKey(n)) continue;
            dist[n] = dist[c] + 1; q.Enqueue(n);
        }
    }
    return -1;
}

var startRows = new System.Collections.Generic.List<string[]>();
foreach (var raw in System.IO.File.ReadAllLines(cellsP)) {
    if (raw.Length == 0 || raw[0] == '#') continue;
    var p = raw.Split('\t');
    if (p.Length < 4) continue;
    if (p[0] == "CT_spawn" || p[0] == "T_spawn") startRows.Add(p);
}
// Bombsite marker cells: the product's own markers (source: bq-analysis.txt, read from
// Resources/MapData/de_dust2_markers.bytes by tools/probes/marker-connectivity.py).
var goalA = new int[][] { new int[]{97,101}, new int[]{104,101}, new int[]{98,106},
                          new int[]{106,106}, new int[]{98,110}, new int[]{106,109} };
var goalB = new int[][] { new int[]{29,105}, new int[]{33,106}, new int[]{37,105},
                          new int[]{27,110}, new int[]{33,111}, new int[]{36,110} };

var rsb = new System.Text.StringBuilder();
rsb.Append("label\tcellX\tcellZ\tfootY\treachOld\treachOldBlocked\treachNew\treachNewBlocked\tneighFaces\treachProd\treachProdBlocked\n");
var summary = new System.Text.StringBuilder();
summary.Append("=== slice BR: cell-centre vs whole-cell (live geometry) ===\n");
summary.Append("mask=0x" + mask.ToString("X8") + " (CsWorld layer=" + worldLayer + ")  bitmap " + W + "x" + D +
               "  walkable=" + nWalk + "\n\n");
summary.Append("--- 1. per-cell scan (foot reference = the cell's OWN floor = lowest top face of the centre column) ---\n");
summary.Append("  walkable cells                 : " + nWalk + "\n");
summary.Append("  cell floor found / none        : " + (nWalk - nNoFloor) + " / " + nNoFloor + "   (floor range " + F(loMin) + " .. " + F(loMax) + ")\n");
summary.Append("  centre sample LEGAL (bitmap ok) : " + nCentreLive + "  = " + (100.0 * nCentreLive / nWalk).ToString("F1", CI) + "%\n");
summary.Append("  centre sample NO legal face     : " + nCentreFail + "  = " + (100.0 * nCentreFail / nWalk).ToString("F1", CI) + "% of walkable\n");
summary.Append("    of those, rescued by 5x5 grid : " + nRescued + "  = " + (100.0 * nRescued / nWalk).ToString("F1", CI) + "% of walkable\n");
summary.Append("    of those, whole cell is dead  : " + nStillDead + "\n");
summary.Append("  (rescue examples)\n" + rescueSamples);
summary.Append("  (whole-cell-dead examples)\n" + deadSamples);

foreach (var row in startRows) {
    int cx = int.Parse(row[1], CI), cz = int.Parse(row[2], CI);
    float fy = float.Parse(row[3], CI);
    int bo = 0, bn = 0, bp = 0, so = 0, sn = 0, sp = 0;
    var ho = new System.Collections.Generic.Dictionary<long, float>();
    var hn = new System.Collections.Generic.Dictionary<long, float>();
    var ao = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>>();
    var an = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>>();
    var ap = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<long>>();
    var hp = new System.Collections.Generic.Dictionary<long, float>();
    string ro = "start-not-walkable", rn = "start-not-walkable", rp = "start-not-walkable";
    if (WalkableCell(cx, cz)) {
        ro = Reach(cx, cz, fy, 0, out bo, out so, out ho, out ao);
        rn = Reach(cx, cz, fy, 1, out bn, out sn, out hn, out an);
        rp = Reach(cx, cz, fy, 2, out bp, out sp, out hp, out ap);
    }
    if (bo < 0) bo = 0; if (bn < 0) bn = 0; if (bp < 0) bp = 0;
    var nf = new System.Text.StringBuilder();
    for (int k = 0; k < 8; k++) {
        int nx = cx + stepX[k], nz = cz + stepZ[k];
        float y, ny; Probe(fy, CellX(nx), CellZ(nz), out y, out ny);
        if (nf.Length > 0) nf.Append('|');
        nf.Append(nx).Append(',').Append(nz).Append(":w=").Append(WalkableCell(nx, nz) ? 1 : 0)
          .Append(",hit=").Append(F(y)).Append(",inBand=").Append(Landable(fy, y, ny) ? F(y) : "no");
    }
    rsb.Append(row[0]).Append('\t').Append(cx).Append('\t').Append(cz).Append('\t').Append(F(fy)).Append('\t');
    rsb.Append(ro).Append('\t').Append(bo).Append('\t').Append(rn).Append('\t').Append(bn).Append('\t').Append(nf)
       .Append('\t').Append(rp).Append('\t').Append(bp).Append('\n');

    summary.Append("\n--- 2. reach / edges from " + row[0] + " (" + cx + "," + cz + ") foot=" + F(fy) + " ---\n");
    summary.Append("  OLD (cell centre)      : reach " + ro + "  dead bitmap-edges " + bo + "\n");
    summary.Append("  NEW (centre -> 5x5)    : reach " + rn + "  dead bitmap-edges " + bn + "  (rescued edges " + (bo - bn) + ", centre-fail cells " + so + ")\n");
    summary.Append("  PROD-BEFORE (BN bug)   : reach " + rp + "  dead bitmap-edges " + bp + "\n");
    long kStart = Key(cx, cz);
    var siteNames = new string[] { "Bombsite_A", "Bombsite_B" };
    var siteGoals = new int[][][] { goalA, goalB };
    for (int s = 0; s < siteNames.Length; s++) {
        string name = siteNames[s];
        var goals = siteGoals[s];
        int rOld = 0, rNew = 0, dOld = -1, dNew = -1;
        var onPath = new System.Collections.Generic.List<int[]>();
        for (int g = 0; g < goals.Length; g++) {
            long kg = Key(goals[g][0], goals[g][1]);
            int dN = GraphDist(an, kStart, kg, null);
            int dO = GraphDist(ao, kStart, kg, null);
            if (dN >= 0) { rNew++; if (dNew < 0 || dN < dNew) dNew = dN; }
            if (dO >= 0) { rOld++; if (dOld < 0 || dO < dOld) dOld = dO; }
            if (dN >= 0 && !hn.ContainsKey(kg)) {
                onPath.Add(new int[] { goals[g][0], goals[g][1], dN });
            }
        }
        summary.Append("  path " + name + " : OLD reachable markers " + rOld + "/" + goals.Length +
                       " (pathCells " + (dOld < 0 ? "none" : (dOld + 1).ToString(CI)) + ")" +
                       " -> NEW " + rNew + "/" + goals.Length +
                       " (pathCells " + (dNew < 0 ? "none" : (dNew + 1).ToString(CI)) + ")\n");
        // cut-vertex test: rescue-only cells = in NEW reach but NOT in OLD reach
        var extras = new System.Collections.Generic.List<int[]>();
        foreach (var kv in hn) {
            if (ho.ContainsKey(kv.Key)) continue;
            int ex = (int)(kv.Key >> 32), ez = (int)(uint)kv.Key;
            extras.Add(new int[] { ex, ez });
        }
        if (extras.Count > 0 && rNew > rOld) {
            int bannedBreaks = 0, tested = 0;
            var banned = new System.Collections.Generic.HashSet<long>();
            for (int i = 0; i < extras.Count && tested < 3; i++) {
                tested++;
                banned.Clear(); banned.Add(Key(extras[i][0], extras[i][1]));
                bool still = false;
                for (int g = 0; g < goals.Length && !still; g++)
                    if (GraphDist(an, kStart, Key(goals[g][0], goals[g][1]), banned) >= 0) still = true;
                if (!still) bannedBreaks++;
            }
            summary.Append("     rescue-only cells (in NEW reach, not in OLD) = " + extras.Count +
                           " ; banned-one-cell test (up to 3): path lost " + bannedBreaks + "/" + tested + "\n");
        } else {
            summary.Append("     rescue-only cells (in NEW reach, not in OLD) = " + extras.Count + "\n");
        }
    }
}
System.IO.File.WriteAllText(outReachP, rsb.ToString(), new System.Text.UTF8Encoding(false));

// ==========================================================================
// ③ the two-sided verdict table, re-judged with the NEW cell rule
// ==========================================================================
var wsb = new System.Text.StringBuilder();
wsb.Append("group\tname\tfootY\twantX\twantZ\trecWantGY\tprodY\tprodMatch\trecHighGY\thighY\thighMatch\t" +
           "oldY\toldBlocked\tnewTopY\tnewNearY\tnewBlocked\tcellX\tcellZ\tcellCentreY\tcellCentreBlocked\tcellMpY\tcellMpBlocked\tcellLivePts\n");
int nW = 0, prodMatch = 0, prodChecked = 0, highMatch = 0, highChecked = 0;
int rowsCentreDead = 0, rowsRescued = 0, rowsMpDead = 0;
var mpCells = new System.Collections.Generic.HashSet<long>();
var mpRescuedCells = new System.Collections.Generic.HashSet<long>();
var mpDeadCells = new System.Collections.Generic.HashSet<long>();
foreach (var raw in System.IO.File.ReadAllLines(wantP)) {
    if (raw.Length == 0 || raw[0] == '#') continue;
    var p = raw.Split('\t');
    if (p.Length < 11) continue;
    float fy = float.Parse(p[2], CI), wx = float.Parse(p[3], CI), wz = float.Parse(p[4], CI);
    UnityEngine.RaycastHit h1;
    bool ok1 = UnityEngine.Physics.Raycast(new UnityEngine.Vector3(wx, fy + Gcd, wz), UnityEngine.Vector3.down,
        out h1, MaxDrop + Gcd, mask, UnityEngine.QueryTriggerInteraction.Ignore);
    UnityEngine.RaycastHit h2;
    bool ok2 = UnityEngine.Physics.Raycast(new UnityEngine.Vector3(wx, fy + 24f + Gcd, wz), UnityEngine.Vector3.down,
        out h2, 50f + Gcd, mask, UnityEngine.QueryTriggerInteraction.Ignore);
    float oy = OldY(fy, wx, wz);
    int cix = (int)System.Math.Floor((wx - ox) / cellSize), ciz = (int)System.Math.Floor((wz - oz) / cellSize);
    float ccY, ccNy; Probe(fy, CellX(cix), CellZ(ciz), out ccY, out ccNy);
    bool ccLive = InBand(fy, ccY);   // the product's own centre rule (no normal test) == "centre block" of the fix
    bool cf; int nl;
    float mp = CellRule(fy, cix, ciz, out cf, out nl);
    mpCells.Add(Key(cix, ciz));
    if (cf) { rowsCentreDead++; if (float.IsNaN(mp)) { rowsMpDead++; mpDeadCells.Add(Key(cix, ciz)); }
              else { rowsRescued++; mpRescuedCells.Add(Key(cix, ciz)); } }
    string pm = "na", hm = "na";
    if (p[5] != "na") { prodChecked++; if (ok1 && F(h1.point.y) == p[5]) { prodMatch++; pm = "ok"; } else pm = "MISMATCH"; }
    if (p[8] != "na") { highChecked++; if (ok2 && F(h2.point.y) == p[8]) { highMatch++; hm = "ok"; } else hm = "MISMATCH"; }
    wsb.Append(p[0]).Append('\t').Append(p[1]).Append('\t').Append(F(fy)).Append('\t').Append(F(wx)).Append('\t').Append(F(wz)).Append('\t');
    wsb.Append(p[5]).Append('\t').Append(ok1 ? F(h1.point.y) : "na").Append('\t').Append(pm).Append('\t');
    wsb.Append(p[8]).Append('\t').Append(ok2 ? F(h2.point.y) : "na").Append('\t').Append(hm).Append('\t');
    wsb.Append(float.IsNaN(oy) ? "na" : F(oy)).Append('\t').Append(float.IsNaN(oy) ? 1 : 0).Append('\t');
    wsb.Append(float.IsNaN(mp) ? "na" : F(mp)).Append('\t').Append(float.IsNaN(mp) ? "na" : F(mp)).Append('\t')
       .Append(float.IsNaN(mp) ? 1 : 0).Append('\t');
    wsb.Append(cix).Append('\t').Append(ciz).Append('\t').Append(F(ccY)).Append('\t').Append(ccLive ? 0 : 1).Append('\t')
       .Append(float.IsNaN(mp) ? "na" : F(mp)).Append('\t').Append(float.IsNaN(mp) ? 1 : 0).Append('\t').Append(nl).Append('\n');
    nW++;
}
System.IO.File.WriteAllText(outWantP, wsb.ToString(), new System.Text.UTF8Encoding(false));
summary.Append("\n--- 3. frozen rows re-judged with the NEW cell rule ---\n");
summary.Append("  rows " + nW + " over " + mpCells.Count + " distinct cells\n");
summary.Append("  product own ground probe reproduced " + prodMatch + "/" + prodChecked +
               " ; +24 m ray " + highMatch + "/" + highChecked + "  (instrument self-check)\n");
summary.Append("  rows whose cell centre is dead : " + rowsCentreDead + " over " + mpRescuedCells.Count + "+" + mpDeadCells.Count + " cells\n");
summary.Append("    rescued by the 5x5 grid     : " + rowsRescued + " rows / " + mpRescuedCells.Count + " cells\n");
summary.Append("    whole cell still dead       : " + rowsMpDead + " rows / " + mpDeadCells.Count + " cells\n");
summary.Append("\nms=" + t0.ElapsedMilliseconds + "\n");
System.IO.File.WriteAllText(outSumP, summary.ToString(), new System.Text.UTF8Encoding(false));

return "BR-BAND mask=0x" + mask.ToString("X8") + " walkable=" + nWalk + " centreDead=" + nCentreFail +
       " rescued=" + nRescued + " wholeCellDead=" + nStillDead +
       " ; rows=" + nW + " cellCentreDeadRows=" + rowsCentreDead + " rescuedRows=" + rowsRescued +
       " ; ms=" + t0.ElapsedMilliseconds + " -> " + outSumP;
