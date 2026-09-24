// ============================================================================
//          / #6「碰撞和模型不一样」的**逐帧**证据。
//
// 为什么必须是 C# + 真物理：被判的是 CsMap.CanStand / ResolveMove / TrySampleGround / TryStepUp
//   （Unity Physics + MeshCollider）与 CsMatch.StepActorPhysics 的真实解算链。
//   Python 复刻公式只会得到"两份实现互相印证"的假证据。
//
// 为什么走 SetLocalInput + Tick（而不是直接调 ResolveMove）：
//   SetLocalInput 正是 PlayerMotor.BuildInput 每帧下发的那个入口（cs16-play-driver.cs 同口径），
//   Tick 正是 MatchModule.Update 每帧调的那个入口 ⇒ 本探针复刻的就是用户那条链。
//   本探针**不在游戏代码里加任何钩子**、不反射、不改任何对象结构；只读 + 只写本文件自己的产物。
//
// 用法（在 client/ 里跑，或每条都带 --project-path）：
//   unity command run_script --file <本文件绝对路径> --entry MoveStuck.Geom
//   unity command run_script --file <本文件绝对路径> --entry MoveStuck.Drive    （参数读 r5-args.txt）
//
// 产物（绝对路径，判据资产）：
//   <项目根>/tools/probes/move-geom.txt      （#6 三套值对照表 + 场景几何台账）
//   <项目根>/tools/probes/move-drive-<label>.txt （逐帧 dump）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

public static class MoveStuck
{
    private const string Root = @"C:\Work\Server\f-v2\clover-project-cs16";
    private const string Tmp = Root + @"\.ai-tmp\test\";
    private const string ArgsPath = Tmp + "r5-args.txt";

    private static readonly StringBuilder Sb = new StringBuilder();

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
    private static void L(string s) => Sb.AppendLine(s);

    private static void Save(string name)
    {
        Directory.CreateDirectory(Tmp);
        File.WriteAllText(Tmp + name, Sb.ToString(), new UTF8Encoding(false));
    }

    // ==================================================================
    //  入口 A：几何三套值对照（#6）
    // ==================================================================
    public static string Geom()
    {
        Sb.Clear();
        ICsMatch match;
        var map = Facade(out match);
        if (map == null || !map.IsLoaded)
        {
            L("ERROR 地图门面/数据拿不到（需要在 Play 里、比赛已开始）");
            Save("move-geom.txt");
            return "ERROR no map";
        }

        L("# 片FIX-4 线M · 几何三套值对照（#6「碰撞和模型不一样」）");
        L("# map=" + map.Status + " match=" + (match != null));

        // ---- ① 场景几何台账：碰撞体集合 vs 渲染网格集合 ----
        var colliders = UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var renderers = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        var byType = new Dictionary<string, int>();
        var colNoRenderer = 0;
        var colNoMeshFilter = 0;      // MeshCollider 但没有 MeshFilter（几何来源不同）
        var meshColliderShared = 0;   // MeshCollider.sharedMesh == 同 GO 上 MeshFilter.sharedMesh
        var meshColliderDiff = 0;     // 两者不同（= 碰撞网格 ≠ 渲染网格，这条对上了就是 #6 的直接形态）
        // 判据自己出过事故（留档）：第一版用 `ReferenceEquals(mc.sharedMesh, mf.sharedMesh)` 比，
        //    得到「相同=0 不同=33」—— 那是**假的**。`ReferenceEquals` 比的是**托管包装对象**，
        //    同一个原生 Mesh 在两处 `sharedMesh` 上会给出**两个不同的托管包装** ⇒ 恒 false。
        //    正确口径 = Unity 重载的 `==`（内部比 instanceID）或用 `GetInstanceID()` 比。
        var worldLayer = LayerMask.NameToLayer(PhysicsLayers.WorldName);
        var colNotWorld = 0;
        var triggers = 0;
        var mcSameCount = 0;         // 同名 GO 上两者是**同一份 Mesh 资产**
        var mcDiffSameGeom = 0;      // 不同实例，但 bounds + 顶点/三角数逐字相同
        var mcDiffGeom = 0;          // 不同实例，且几何计数不同（= 碰撞网格 ≠ 渲染网格）
        var mcMeshReadable = 0;
        var mfMeshReadable = 0;
        var meshGeomDetail = new List<string>();
        for (var i = 0; i < colliders.Length; i++)
        {
            var c = colliders[i];
            if (c == null) continue;
            var tn = c.GetType().Name;
            byType.TryGetValue(tn, out var n);
            byType[tn] = n + 1;
            if (c.isTrigger) triggers++;
            if (c.gameObject.layer != worldLayer) colNotWorld++;
            var r = c.GetComponent<Renderer>();
            if (r == null) colNoRenderer++;
            var mc = c as MeshCollider;
            if (mc != null)
            {
                var mf = c.GetComponent<MeshFilter>();
                if (mf == null) colNoMeshFilter++;
                else if (mc.sharedMesh == mf.sharedMesh) { meshColliderShared++; mcSameCount++; }
                else
                {
                    meshColliderDiff++;
                    // 不同实例 ⇒ 继续比几何：bounds（不可读也能取）+ 顶点数 + 三角面数
                    var a = mc.sharedMesh; var b = mf.sharedMesh;
                    if (a != null && a.isReadable) mcMeshReadable++;
                    if (b != null && b.isReadable) mfMeshReadable++;
                    var ba = a != null ? a.bounds : new Bounds();
                    var bb = b != null ? b.bounds : new Bounds();
                    var sameB = (ba.center - bb.center).magnitude < 1e-4f && (ba.size - bb.size).magnitude < 1e-4f;
                    // `triangles` 只在 isReadable 时才给得到（不可读恒返回空数组 ⇒ 会把两份网格都算成 tri=0
                    //    而"看起来一样"）⇒ 这里显式分成"可读才比计数 / 不可读只比 bounds"两档，不许混。
                    var va = a != null && a.isReadable ? a.vertexCount : -1;
                    var vb = b != null && b.isReadable ? b.vertexCount : -1;
                    var ta = a != null && a.isReadable ? a.triangles.Length / 3 : -1;
                    var tb = b != null && b.isReadable ? b.triangles.Length / 3 : -1;
                    var cmpCounts = va >= 0 && vb >= 0;
                    if (sameB && (!cmpCounts || (va == vb && ta == tb))) mcDiffSameGeom++;
                    else
                    {
                        mcDiffGeom++;
                        if (meshGeomDetail.Count < 8)
                            meshGeomDetail.Add("GO=" + c.gameObject.name + " 碰撞(vc=" + va + ",tri=" + ta +
                                               ",读=" + (a != null && a.isReadable) + ",bounds=" + V(ba.center) + "/" + V(ba.size) +
                                               ") 渲染(vc=" + vb + ",tri=" + tb + ",读=" + (b != null && b.isReadable) +
                                               ",bounds=" + V(bb.center) + "/" + V(bb.size) + ")");
                    }
                }
            }
        }
        var rendNoCollider = 0;
        var rendNoMesh = 0;
        var rendNonReadable = 0;
        for (var i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            if (r.GetComponent<Collider>() == null) rendNoCollider++;
            var mf = r.GetComponent<MeshFilter>();
            var m = mf != null ? mf.sharedMesh : null;
            if (m == null) rendNoMesh++;
            else if (!m.isReadable) rendNonReadable++;
        }
        L("## ① 场景几何台账");
        L("  colliders=" + colliders.Length + "  meshRenderers=" + renderers.Length +
          "  worldLayer=" + worldLayer + "  不在世界层的碰撞体=" + colNotWorld + "  trigger=" + triggers);
        foreach (var kv in byType) L("  colliderByType[" + kv.Key + "]=" + kv.Value);
        L("  碰撞体所在的 GO 上没有 Renderer=" + colNoRenderer + "（= 只有碰撞、看不见）");
        L("  渲染网格所在的 GO 上没有 Collider=" + rendNoCollider + "（= 看得见、不挡人）");
        L("  MeshCollider 与同 GO MeshFilter.sharedMesh 相同=" + meshColliderShared +
          " 不同=" + meshColliderDiff + " 无 MeshFilter=" + colNoMeshFilter);
        L("    ↳ 不同实例中：bounds+计数逐字相同=" + mcDiffSameGeom + "  几何计数不同=" + mcDiffGeom +
          "  （碰撞网格可读=" + mcMeshReadable + " 渲染网格可读=" + mfMeshReadable + "）");
        for (var i = 0; i < meshGeomDetail.Count; i++) L("      几何不同 " + meshGeomDetail[i]);
        L("  MeshRenderer 无 MeshFilter/mesh=" + rendNoMesh + "（其中不可读=" + rendNonReadable + "）");
        // 「不同实例」这条必须钉死到具体数字，否则一句话两种结论（场景 YAML 说同一份资产、
        //    运行期 `==` 说不同）—— 逐条打 GO 名 / 两个 mesh 的 instanceID / 名字 / 可读性 / 顶点数。
        L("    活动场景=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        var shown = 0;
        for (var i = 0; i < colliders.Length && shown < 4; i++)
        {
            var mc2 = colliders[i] as MeshCollider;
            if (mc2 == null || mc2.sharedMesh == null) continue;
            var mf2 = mc2.GetComponent<MeshFilter>();
            if (mf2 == null) continue;
            var am = mc2.sharedMesh; var bm = mf2.sharedMesh;
            if (am == bm) continue;   // 只打"不同"的样本
            shown++;
            var sA = "碰撞(name=" + am.name + ",读=" + am.isReadable +
                     ",vc=" + (am.isReadable ? am.vertexCount.ToString() : "?") +
                     ",bounds=" + V(am.bounds.center) + "/" + V(am.bounds.size) + ")";
            var sB = "渲染(name=" + (bm != null ? bm.name : "null") +
                     ",读=" + (bm != null && bm.isReadable) +
                     ",vc=" + (bm != null && bm.isReadable ? bm.vertexCount.ToString() : "?") +
                     ",bounds=" + (bm != null ? V(bm.bounds.center) + "/" + V(bm.bounds.size) : "null") +
                     ",ReferenceEquals(托管)=" + ReferenceEquals(am, bm) + ")";
            L("      样本 GO=" + mc2.gameObject.name + " " + sA + " " + sB);
        }

        // ---- ② 指定区域逐列：位图 vs 碰撞面 vs 渲染面 ----
        L("");
        L("## ② 逐列三套值（bitmap / 碰撞面上沿 / 渲染面上沿）");
        L("  列=(x,z) 0.5m 网格；bmp=CsMap.WalkableAt；colUp=CsWorld 层朝上面(y>0.1)最高者；" +
          "renUp=渲染网格 mesh.Raycast 朝上面最高者；Δ=colUp-renUp");
        Area(map, "匪家扶手/斜坡车道 x=-41.5..-36.5 z=-54..-36", -41.5f, -36.5f, -54f, -36.5f);
        Area(map, "B 通台阶（中门）x=-30..-10 z=31..40", -30f, 31f, -10.5f, 40f);

        // ---- ③ 停留点定向查墙：把「停滞」拆成「真有一堵墙」还是「解算不了」 ----
        L("");
        L("## ③ 停留点定向射线（地面层，朝上/法线低的都算障碍）");
        L("  口径：起点 = 停滞点 + 各高度；方向 = 该点当时的输入朝向 / 沿轴滑行方向 / 八个方位");
        L("  高度 0.45 = 膝盖（TryStepUp 的射线高度）、0.90 = 腰、1.50 = 视线");
        Wall(map, "B通台阶 停滞点(20fps)", new Vector3(-18.887f, 0.653f, 36.948f));
        Wall(map, "B通台阶 停滞点(135fps)", new Vector3(-18.976f, 0.688f, 36.995f));
        Wall(map, "匪家车道 修前停滞点(20fps REP1/REP2)", new Vector3(-41.500f, 3.930f, -53.920f));

        L("");
        L("SUMMARY geom-done");
        Save("move-geom.txt");
        return "OK geom -> " + Tmp + "move-geom.txt";
    }

    /// <summary>Möller–Trumbore：射线(o,d) 与三角形(v0,v1,v2) 求交（都在同一空间内）。</summary>
    private static bool RayTri(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2,
                               out float t, out Vector3 n)
    {
        t = 0f; n = Vector3.up;
        var e1 = v1 - v0; var e2 = v2 - v0;
        var pv = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, pv);
        if (det > -1e-9f && det < 1e-9f) return false;
        var inv = 1f / det;
        var tv = o - v0;
        var u = Vector3.Dot(tv, pv) * inv;
        if (u < 0f || u > 1f) return false;
        var qv = Vector3.Cross(tv, e1);
        var v = Vector3.Dot(d, qv) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, qv) * inv;
        if (t <= 1e-5f) return false;
        n = Vector3.Cross(e1, e2).normalized;
        return true;
    }

    private sealed class Rend { public MeshRenderer r; public Mesh mesh; public int[] tris; }

    /// <summary>矩形区域逐列对照（axis-aligned box: [x0..x1] × [z0..z1]，0.5 m 网格）。</summary>
    private static void Area(CsMap map, string title, float x0, float z0, float x1, float z1)
    {
        var wx = LayerMask.NameToLayer(PhysicsLayers.WorldName);
        var mask = wx >= 0 ? (1 << wx) : ~0;

        // 渲染网格候选（**一次收集**，别每列都全场景扫一遍）
        var allRends = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include);
        var cands = new List<Rend>();
        var skippedBig = 0;
        var skippedUnreadable = 0;
        for (var i = 0; i < allRends.Length; i++)
        {
            var r = allRends[i];
            if (r == null || !r.enabled) continue;
            var mf = r.GetComponent<MeshFilter>();
            var m = mf != null ? mf.sharedMesh : null;
            if (m == null) continue;
            if (!m.isReadable) { skippedUnreadable++; continue; }
            if (m.triangles.Length > 100000) { skippedBig++; continue; }
            cands.Add(new Rend { r = r, mesh = m, tris = m.triangles });
        }
        L("  渲染候选网格=" + cands.Count + "（不可读跳过=" + skippedUnreadable + " 三角面>10万跳过=" + skippedBig + "）");

        var cols = 0;
        var bothCols = 0;
        var maxAbs = 0f;
        var maxAbsAt = default(Vector3);
        var overTol = 0;
        var bmpBlockedWithFloor = 0;    // 位图判挡、但碰撞面在脚面附近（= 差异 #64 的形态）
        var bmpBlockedWithFloorAt = new List<string>();
        var bmpFreeNoFloor = 0;         // 位图判通、但该列没有任何朝上碰撞面（= 悬空/虚空列）
        var bmpFreeNoFloorAt = new List<string>();

        L("");
        L("-- " + title + " --");
        L("  x        z        bmp colUp   colN  renUp   renN  Δ(col-ren)  备注");
        for (var x = Mathf.Min(x0, x1); x <= Mathf.Max(x0, x1) + 1e-4f; x += 0.5f)
        {
            for (var z = Mathf.Min(z0, z1); z <= Mathf.Max(z0, z1) + 1e-4f; z += 0.5f)
            {
                cols++;
                var bmp = map.WalkableAt(x, z);
                // 碰撞面：整列 RaycastAll（含背面），只取朝上面
                var hits = Physics.RaycastAll(new Vector3(x, 60f, z), Vector3.down, 120f, mask,
                                              QueryTriggerInteraction.Ignore);
                var colUp = float.NegativeInfinity;
                var colN = 0;
                for (var i = 0; i < hits.Length; i++)
                {
                    if (hits[i].normal.y <= 0.1f) continue;
                    colN++;
                    if (hits[i].point.y > colUp) colUp = hits[i].point.y;
                }
                // 渲染面：对候选渲染网格逐个做 local 空间射线-三角求交（Mesh.Raycast 在 Unity 6 已移除）
                var renUp = float.NegativeInfinity;
                var renN = 0;
                for (var i = 0; i < cands.Count; i++)
                {
                    var c = cands[i];
                    var b = c.r.bounds;
                    if (x < b.min.x || x > b.max.x || z < b.min.z || z > b.max.z) continue;
                    var verts = c.mesh.vertices;
                    var o = c.r.transform.InverseTransformPoint(new Vector3(x, 60f, z));
                    var d = c.r.transform.InverseTransformDirection(Vector3.down);
                    for (var k = 0; k + 2 < c.tris.Length; k += 3)
                    {
                        var v0 = verts[c.tris[k]];
                        var v1 = verts[c.tris[k + 1]];
                        var v2 = verts[c.tris[k + 2]];
                        // 三角面 AABB 快筛（含 o.x/o.z 才做求交）
                        if (Mathf.Min(v0.x, v1.x, v2.x) > o.x || Mathf.Max(v0.x, v1.x, v2.x) < o.x) continue;
                        if (Mathf.Min(v0.z, v1.z, v2.z) > o.z || Mathf.Max(v0.z, v1.z, v2.z) < o.z) continue;
                        if (!RayTri(o, d, v0, v1, v2, out var t, out var nl)) continue;
                        var wp = c.r.transform.TransformPoint(o + d * t);
                        var wn = c.r.transform.TransformDirection(nl);
                        if (wn.y > 0.1f) { renN++; if (wp.y > renUp) renUp = wp.y; }
                    }
                }

                string note = "";
                var hasBoth = colN > 0 && renN > 0;
                if (hasBoth)
                {
                    bothCols++;
                    var dev = colUp - renUp;
                    if (Mathf.Abs(dev) > maxAbs) { maxAbs = Mathf.Abs(dev); maxAbsAt = new Vector3(x, 0f, z); }
                    if (Mathf.Abs(dev) > 0.01f) { overTol++; note = "**Δ>1cm**"; }
                }
                if (!bmp && colN > 0 && colUp > -100f)
                {
                    bmpBlockedWithFloor++;
                    if (bmpBlockedWithFloorAt.Count < 12)
                        bmpBlockedWithFloorAt.Add("cell(" + Mathf.RoundToInt(x + 63f) + "," + Mathf.RoundToInt(z + 72f) +
                                                  ")@(" + F(x) + "," + F(z) + ") colUp=" + F(colUp) +
                                                  " renUp=" + F(renUp));
                }
                // 反方向偏差（用户报"能穿"的那一半）：位图说能走，但该列**一个朝上的碰撞面都没有**
                // （脚下是虚空）⇒ 位图与几何在这一列说的也不是一回事。
                if (bmp && colN == 0)
                {
                    bmpFreeNoFloor++;
                    if (bmpFreeNoFloorAt.Count < 12)
                        bmpFreeNoFloorAt.Add("cell(" + Mathf.RoundToInt(x + 63f) + "," + Mathf.RoundToInt(z + 72f) +
                                             ")@(" + F(x) + "," + F(z) + ") 该列朝上碰撞面=0");
                }
                if ((!bmp && colN > 0) || (hasBoth && Mathf.Abs(colUp - renUp) > 0.01f))
                {
                    L("  " + F(x).PadLeft(8) + " " + F(z).PadLeft(8) + " " + (bmp ? "1  " : "0  ") +
                      (colN > 0 ? F(colUp) : "  -inf").PadLeft(8) + " " + colN.ToString().PadLeft(4) + "  " +
                      (renN > 0 ? F(renUp) : "  -inf").PadLeft(8) + " " + renN.ToString().PadLeft(4) + "  " +
                      (hasBoth ? F(colUp - renUp).PadLeft(9) : "        -") + "  " + note);
                }
            }
        }
        L("  小结[" + title + "] cols=" + cols + " 碰撞+渲染都有面=" + bothCols +
          " |Δ|>1cm 的列=" + overTol + " 最大|Δ|=" + F(maxAbs) + " @ " + V(maxAbsAt) +
          " 位图判挡但有碰撞面的列=" + bmpBlockedWithFloor +
          " 位图判通但无碰撞面的列=" + bmpFreeNoFloor);
        for (var i = 0; i < bmpBlockedWithFloorAt.Count; i++) L("    位图判挡但有面 " + bmpBlockedWithFloorAt[i]);
        for (var i = 0; i < bmpFreeNoFloorAt.Count; i++) L("    位图判通但无面 " + bmpFreeNoFloorAt[i]);
    }

    /// <summary>
    /// 停留点定向射线：把「停滞」拆成「正前方真有一堵墙」还是「解算不了」。
    ///
    /// <para>口径：从停留点 + 各高度向 8 个方位各打一根水平射线（世界层，忽略 trigger），
    /// 报**最近命中**的距离 / 法线 y / 碰撞体名。判据：膝盖高度（0.45，= <c>TryStepUp</c> 的打射线高度）
    /// 那一圈**全被挡** ⇒ 人被墙围住（能走的只有身后的来路）⇒ 停滞是几何事实；
    /// 若膝高某方向**通畅**而角色仍不动 ⇒ 停滞来自解算（才是缺陷）。</para>
    /// </summary>
    private static void Wall(ICsMap map, string title, Vector3 p)
    {
        var mask = LayerMask.GetMask(PhysicsLayers.WorldName);
        var dirs = new[]
        {
            new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, -1f), new Vector3(-1f, 0f, 0f),
            new Vector3(0.7071f, 0f, 0.7071f), new Vector3(0.7071f, 0f, -0.7071f),
            new Vector3(-0.7071f, 0f, 0.7071f), new Vector3(-0.7071f, 0f, -0.7071f),
        };
        var names = new[] { "+Z", "+X", "-Z", "-X", "+X+Z", "+X-Z", "-X+Z", "-X-Z" };
        var heights = new[] { 0.45f, 0.90f, 1.50f };
        L("  [" + title + "] 点=" + V(p));
        for (var h = 0; h < heights.Length; h++)
        {
            var o = new Vector3(p.x, p.y + heights[h], p.z);
            var open = 0;
            var parts = new List<string>();
            for (var i = 0; i < dirs.Length; i++)
            {
                var hit = Physics.Raycast(o, dirs[i], out var hh, 3f, mask, QueryTriggerInteraction.Ignore);
                if (!hit) { open++; parts.Add(names[i] + "=通"); continue; }
                parts.Add(names[i] + "=" + F(hh.distance) + "m nY=" + F(hh.normal.y) +
                          " [" + hh.collider.GetType().Name + " " + hh.collider.name + " L" + hh.collider.gameObject.layer + "]");
            }
            L("    高度 " + F(heights[h]) + "m  通畅方向=" + open + "/8");
            for (var i = 0; i < parts.Count; i++) L("      " + parts[i]);
        }
        L("    该点 CanStand=" + map.CanStand(p) + " 位图可走=" + map.WalkableAt(p.x, p.z) +
          " 地面=" + F(map.SampleGround(new Vector3(p.x, p.y + 0.5f, p.z), 8f)));
    }

    // ==================================================================
    /// <summary>
    /// <para>用途（片FIX-4 线M · #5「B 通台阶跳一下就卡死」）：把**停滞点**
    /// `(-18.887, 0.653, 36.948)`（<c>stairs05A</c> / <c>i5-str</c> 实测停住的那一格）到底是
    /// 并由膝高（0.45 m）通畅方向定**真折点**，供下一轮补一条**真折线**用例
    /// —— 本片 #5 尚不消号的直接原因就是"折点是我离线按地理常识拍的"，实测 reached=0/1。</para>
    ///
    /// <para>本入口**只打印读数、不下结论**：判词由 team-lead 定（判据不许自证）。
    /// 参数读 <c>.ai-tmp/test/r5-args.txt</c>：<c>label</c> / <c>px</c> / <c>py</c> / <c>pz</c>。
    /// 产物：<c>.ai-tmp/test/move-wall-&lt;label&gt;.txt</c>。</para>
    /// </summary>
    public static string WallAt()
    {
        Sb.Clear();
        ICsMatch match;
        var map = Facade(out match);
        if (map == null || !map.IsLoaded)
        {
            L("ERROR 地图门面/数据拿不到（需要在 Play 里、地图已加载）");
            Save("move-wall-args.txt");
            return "ERROR no map";
        }
        var a = LoadArgs();
        var label = Get(a, "label", "wall");
        var ptsSpec = Get(a, "pts", "");
        var pts = new List<Vector3>();
        if (ptsSpec.Length > 0)
        {
            var segs = ptsSpec.Split(';');
            for (var i = 0; i < segs.Length; i++)
            {
                var t = segs[i].Trim();
                if (t.Length == 0) continue;
                var c = t.Split(',');
                if (c.Length < 3)
                {
                    L("!! pts 第 " + i + " 段格式错（需 `x,y,z`）: " + t);
                    continue;
                }
                pts.Add(new Vector3(float.Parse(c[0], CultureInfo.InvariantCulture),
                                    float.Parse(c[1], CultureInfo.InvariantCulture),
                                    float.Parse(c[2], CultureInfo.InvariantCulture)));
            }
        }
        if (pts.Count == 0) pts.Add(new Vector3(G(a, "px"), G(a, "py"), G(a, "pz")));
        L("# 片FIX-4 线M · 多点定向射线扫描（#5 定位：墙 vs 解算）label=" + label + " 点数=" + pts.Count);
        L("# map=" + map.Status + " match=" + (match != null));
        for (var i = 0; i < pts.Count; i++)
        {
            Wall(map, label + "#" + i, pts[i]);
            L(KneeSummary(map, label + "#" + i, pts[i]));
        }
        Save("move-wall-" + label + ".txt");
        return "OK wall[" + label + "] 点数=" + pts.Count;

        // ---- 机械可读汇总行（膝高那一圈）：膝高 = TryStepUp 的打射线高度 0.45 m ----
        // 只报读数（通畅方向数 + 位图/CanStand/地面），**不含任何结论文字**。
        // 单独重打一遍这 8 根射线（不复用 Wall 内部变量）：**不动 Wall() 一个字符** ⇒
        //    既有 `move-geom.txt`（#6 证据）的产出路径逐字未变，避免"顺手改旧判据资产"。
    }

    private static string KneeSummary(ICsMap map, string title, Vector3 p)
    {
        var dirs = new[]
        {
            new Vector3(0f, 0f, 1f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, -1f), new Vector3(-1f, 0f, 0f),
            new Vector3(0.7071f, 0f, 0.7071f), new Vector3(0.7071f, 0f, -0.7071f),
            new Vector3(-0.7071f, 0f, 0.7071f), new Vector3(-0.7071f, 0f, -0.7071f),
        };
        var names = new[] { "+Z", "+X", "-Z", "-X", "+X+Z", "+X-Z", "-X+Z", "-X-Z" };
        var mask = LayerMask.GetMask(PhysicsLayers.WorldName);
        var knee = new Vector3(p.x, p.y + 0.45f, p.z);
        var open = 0;
        var openDirs = new List<string>();
        for (var i = 0; i < dirs.Length; i++)
        {
            var dummy = default(RaycastHit);
            if (!Physics.Raycast(knee, dirs[i], out dummy, 3f, mask, QueryTriggerInteraction.Ignore))
            { open++; openDirs.Add(names[i]); }
        }
        return "RESULT-WALL[" + title + "]: 点=" + V(p) + " 膝高0.45通畅=" + open + "/8 方向=[" +
               string.Join(",", openDirs) + "] CanStand=" + map.CanStand(p) +
               " 位图可走=" + map.WalkableAt(p.x, p.z) +
               " 地面=" + F(map.SampleGround(new Vector3(p.x, p.y + 0.5f, p.z), 8f));
    }

    // ==================================================================
    //  入口 B：逐帧驱动（用户那条链）
    // ==================================================================
    public static string Drive()
    {
        Sb.Clear();
        var a0 = LoadArgs();
        var label = Get(a0, "label", "case");
        ICsMatch match;
        var map = Facade(out match);
        if (map == null || !map.IsLoaded) { L("ERROR 地图门面/数据拿不到"); Save("move-drive-" + label + ".txt"); return "ERROR no map"; }
        if (match == null || !match.IsRunning) { L("ERROR 比赛没在跑（需要先进 Live）"); Save("move-drive-" + label + ".txt"); return "ERROR no match"; }

        var local = match.LocalPlayer;
        if (local == null) { L("ERROR 没有本地玩家"); Save("move-drive-" + label + ".txt"); return "ERROR no local"; }

        // 必须先进 Live：冻结期 `UpdateLocalPlayer` 在 Phase==Freeze 时**直接清零速度并 return**
        var idle = default(CsInputState);
        var warm = 0;
        for (; warm < 600 && match.Phase != CsRoundPhase.Live; warm++)
        {
            match.SetLocalInput(idle);
            match.Tick(0.05f);
        }

        // 清场：把别的角色挪到远处（与 CombatSelfTest.KeepBotsAway 同口径），
        var actors = match.Actors;
        for (var i = 0; i < actors.Count; i++)
            if (!ReferenceEquals(actors[i], local)) actors[i].Position = new Vector3(100f, 0f, 100f);

        float x0 = G(a0, "x0"), z0 = G(a0, "z0"), x1 = G(a0, "x1"), z1 = G(a0, "z1");
        var y0 = Get(a0, "y0", "");
        var dt = G(a0, "dt", "0.05");
        var frames = (int)G(a0, "frames", "600");
        var reps = (int)G(a0, "reps", "1");
        var jumpFrame = (int)G(a0, "jump", "-1");
        var expectClimb = G(a0, "expectClimb", "0");
        var yaw = Mathf.Atan2(x1 - x0, z1 - z0) * Mathf.Rad2Deg;

        L("# 片FIX-4 线M · 逐帧驱动 label=" + label);
        L("# 口径：每帧 match.SetLocalInput(cmd) + match.Tick(dt)（= PlayerModule/MatchModule 的同一条链）");
        L("# 参数 x0=" + F(x0) + " z0=" + F(z0) + " x1=" + F(x1) + " z1=" + F(z1) +
          " y0=" + (y0.Length == 0 ? "(探地)" : y0) + " dt=" + dt + " frames=" + frames +
          " reps=" + reps + " jumpFrame=" + jumpFrame + " yaw=" + F(yaw));
        L("# phase=" + match.Phase + " paused=" + match.IsPaused + " map=" + map.Status +
          " 快进到Live用了" + warm + " tick");

        var stuckTotal = 0;
        var reachedTotal = 0;
        var climbTotal = 0;
        L("");
        for (var rep = 0; rep < reps; rep++)
        {
            var sy = y0.Length > 0 ? float.Parse(y0, CultureInfo.InvariantCulture)
                                   : map.SampleGround(new Vector3(x0, 8f, z0), 20f);
            if (float.IsNegativeInfinity(sy)) sy = 2f;
            local.Position = new Vector3(x0, sy, z0);
            local.Velocity = Vector3.zero;
            local.OnGround = true;
            local.Health = 10000;
            local.Yaw = yaw;
            var yawUse = yaw;

            L("== REP " + rep + " phase=" + match.Phase + " alive=" + local.IsAlive +
              " start=" + V(local.Position) + " 目标=(" + F(x1) + "," + F(z1) + ") ==");
            L("  F   t      pos                         vel                      onG disp  |from canStand bmp ray vol gws |to bmp |sgy  sgnY  stepUpΔ early");
            var startY = local.Position.y;
            var maxY = startY;
            var stuckRun = 0;
            var stuckMax = 0;
            var stuckFirst = -1;
            var traveled = 0f;
            var reached = false;
            var stepUpHits0 = CsMatch.StepUpProbeHits;
            // `prevVel` = **上一帧 tick 结束时**的速度。本帧 `StepActorPhysics`（CsMatch.cs:1950-1958）用
            //   `to = from + (v.x, v.y - Gravity*dt, v.z) * dt`（y 先钳到 [MaxFallSpeed, -TerminalFallSpeed]）
            // ⇒ 由此**精确重建**本帧的请求目标 `to`，才能把 `ResolveMove` 用同一条输入再跑一次。
            var prevVel = Vector3.zero;
            var deathF = -1;
            var hitTally = new Dictionary<string, int>();

            for (var f = 0; f < frames; f++)
            {
                var cmd = default(CsInputState);
                cmd.Move = new Vector2(0f, 1f);
                cmd.Yaw = yawUse;
                cmd.Pitch = 0f;
                cmd.Jump = f == jumpFrame;
                match.SetLocalInput(cmd);

                var before = local.Position;
                var h0 = CsMatch.StepUpProbeHits;
                match.Tick(dt);
                var after = local.Position;

                var dxz = new Vector2(after.x - before.x, after.z - before.z).magnitude;
                traveled += dxz;
                if (after.y > maxY) maxY = after.y;

                var from = before;
                var to = after;
                bool csFrom = map.CanStand(from);
                bool bmpFrom = map.WalkableAt(from.x, from.z);
                bool rayFrom = map.RayClearForTest(from.x, from.y, from.z);
                bool volFrom = map.VolumeBlockedForTest(from.x, from.y, from.z);
                bool gwsFrom = map.GroundWithinStepForTest(from);
                bool bmpTo = map.WalkableAt(to.x, to.z);
                map.TrySampleGround(after, out var gp, out var gn, 15f);
                var dHits = CsMatch.StepUpProbeHits - h0;

                // ── 出口判别（影子复核）────────────────────────────────────────────────────
                // 为什么需要它：判据「`csFrom=0` ∧ 水平disp=0 ⇒ 只有这些行可能进那颗三元」**对 `? to` 是
                // 结构性盲的** —— `? to` 一旦被走，返回的就是请求目标 ⇒ 位移 = 请求位移 ⇒ 那些行
                // 做法：用**精确重建**的 `to` 把 `ResolveMove` 再跑一次（纯函数、只读地图几何），
                // 由 trace 回调看它返回的是哪个出口；并要求**影子返回值与真实结果逐位相同**才采信，
                // 否则本列记 `?`（不可信）。
                // 出口标签：`to` = 返回请求目标（= `:498` 的 `? to` 逃生口；也可能是扫掠干净地走到请求点，
                //   `from` = 整点原样返回；`step` = 扫掠/分轴滑墙后的中间点；`none` = 没进 `!CanStand` 分支。
                var vy0 = prevVel.y - CsConst.Gravity * dt;
                if (vy0 < CsConst.MaxFallSpeed) vy0 = CsConst.MaxFallSpeed;
                vy0 = Mathf.Max(vy0, -CsMatchConst.TerminalFallSpeed);
                var toRecon = before + new Vector3(prevVel.x, vy0, prevVel.z) * dt;
                var hit = "-";
                var leak0 = CsMatch.StepUpProbeHits;
                var shadow = map.ResolveMoveTraceForTest(before, toRecon,
                    (ii, want, curBefore, curAfter) =>
                    {
                        if (ii != 0) return;   // 扫掠分支从 i=1 起回调 ⇒ i==0 只可能来自 :481/:498 两个直通出口
                        if ((curAfter - want).sqrMagnitude < 1e-10f) hit = "to";
                        else if ((curAfter - curBefore).sqrMagnitude < 1e-10f) hit = "from";
                        else if ((curAfter.x - curBefore.x) * (curAfter.x - curBefore.x) +
                                 (curAfter.z - curBefore.z) * (curAfter.z - curBefore.z) < 1e-10f) hit = "falseY";
                        else hit = "step";
                    }, CsConst.PlayerRadius);
                var stepUpLeak = CsMatch.StepUpProbeHits - leak0;
                if ((shadow - after).sqrMagnitude > 1e-10f) hit = "?";   // 影子 ≠ 真实（后处理改过结果）⇒ 不采信
                if (hit == "-") hit = "none";                             // 没被回调 = 没进 `!CanStand` 分支
                hitTally.TryGetValue(hit, out var hitN);
                hitTally[hit] = hitN + 1;

                // `CsMatch.cs:1868/1937` 两处 `!a.IsAlive` 早退都是**直接 return**（不清速度）⇒ 表现是
                // "位置与速度双双冻结且 airborne"。这里把 alive/hp 逐帧落盘，并把首次死亡打一行标记，
                if (!local.IsAlive && deathF < 0)
                {
                    deathF = f;
                    L("  !! [" + label + "] 本地玩家阵亡 f=" + f + "（hp=" + F(local.Health) +
                      "）⇒ 此后若位置/速度冻结，那是 `!IsAlive` 早退，**不是**解算停滞");
                }

                var row = "  " + f.ToString().PadLeft(4) + " " + F(f * dt).PadLeft(6) + " " +
                          V(after).PadLeft(28) + " " + V(local.Velocity).PadLeft(24) + " " +
                          (local.OnGround ? "1  " : "0  ") + F(dxz).PadLeft(5) + "  |      " +
                          (csFrom ? "1" : "0") + "        " + (bmpFrom ? "1" : "0") + "   " +
                          (rayFrom ? "1" : "0") + "   " + (volFrom ? "1" : "0") + "   " + (gwsFrom ? "1" : "0") +
                          "   |   " + (bmpTo ? "1" : "0") + "   |" + F(gp.y).PadLeft(7) + " " + F(gn.y).PadLeft(6) +
                          " " + dHits.ToString().PadLeft(5) + " " + (!csFrom ? "EARLY" : "-") +
                          "  hp=" + local.Health.ToString("F0").PadLeft(6) +
                          " alive=" + (local.IsAlive ? "1" : "0") + " hit=" + hit +
                          // `hitTo` = **本帧 `!CanStand(from)` 三元走了 `? to` 支路的直接读数**
                          //   旧判据「`csFrom=0` ∧ 水平 disp=0 ⇒ 可达行集合」**结构性看不见 `? to`**
                          //   —— `? to` 返回整个 `to` ⇒ `disp≠0` ⇒ 那些行不在集合里（判据盲区，见
                          //   .ai-tmp/test/issue5-branch-coverage.md）。⇒ 覆盖判定**必须**靠这一列。
                          // 影子≠真实（`hit=="?"`）时记 0，不冒充"走过"也不冒充"没走"。
                          " hitTo=" + (hit == "to" ? "1" : "0") +
                          (stepUpLeak != 0 ? " LEAK=" + stepUpLeak : "");
                L(row);

                //    旧写法 `dxz < 0.001f` = "每帧 1 mm"：它随 dt 改变物理含义（dt=0.05 ⇒ 0.02 m/s，
                //    dt=0.0074 ⇒ 0.135 m/s）⇒ 固定 frames 变 dt 时两侧**不可比**（实测：高帧率侧的
                //    "0 卡死 / 32 帧"只是帧预算截断的假象）。逐帧 raw 行不变 ⇒ 旧产物仍可离线复算。
                if (dxz / dt < 0.02f)
                {
                    stuckRun++;
                    if (stuckRun == 1) stuckFirst = f;
                    if (stuckRun > stuckMax) stuckMax = stuckRun;
                }
                else stuckRun = 0;

                prevVel = local.Velocity;   // 下一帧重建 `to` 用的入口速度

                var prog = Project(new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z1), new Vector3(after.x, 0f, after.z));
                if (prog >= 1f) { reached = true; L("  -> 到达终点（f=" + f + "）"); break; }
            }

            var climb = maxY - startY;
            var isStuck = stuckMax >= 60;
            if (isStuck) stuckTotal++;
            if (reached) reachedTotal++;
            if (climb > 0.5f) climbTotal++;
            L("  REP 小结: frames=" + frames + " 终点=" + V(local.Position) + " 走了=" + F(traveled) +
              " 最高y=" + F(maxY) + " 爬升=" + F(climb) + " 到终点=" + reached +
              " 最长停滞帧=" + stuckMax + (stuckFirst >= 0 ? "(从 f=" + stuckFirst + ")" : "") +
              " 抬台阶接住=" + (CsMatch.StepUpProbeHits - stepUpHits0) +
              " 卡死=" + isStuck + " 出口=" + Tally(hitTally) +
              (deathF >= 0 ? " 阵亡f=" + deathF + "(本 rep 的冻结含物理停摆，⛔ 不是解算停滞)" : "") +
              // 把「阵亡后 `!IsAlive` 早退的物理停摆」和「真正顶墙的停滞」搅在一起（stairsjfA rep0
              // 实测 2335 = 阵亡冻结 676 + 真停滞 1659，拆分产物见
              // .ai-tmp/test/_lineM-stairsjfA-deathsplit.txt）⇒ 该 rep **不得当读数用**。
              // ⇒ 判据侧（.ai-tmp/test/_lineM-drive-judge.py 的 deathsplit）按 raw 行离线复核。
              (deathF >= 0 ? " void=1(本 rep 有阵亡 ⇒ 其 最长停滞帧/卡死 读数作废，须离线拆分或先回血)" : " void=0"));
        }

        L("");
        L("RESULT-DRIVE[" + label + "]: reps=" + reps + " 到终点=" + reachedTotal + " 有爬升(>0.5m)=" + climbTotal +
          " 卡死(停滞≥60帧)=" + stuckTotal +
          " | 期望: expectClimb=" + expectClimb);
        Save("move-drive-" + label + ".txt");
        return "OK drive[" + label + "] reached=" + reachedTotal + "/" + reps + " climb=" + climbTotal + " stuck=" + stuckTotal;
    }

    /// <summary>把「出口计数」打成 `to:12,falseY:3,...`（键排序 ⇒ 逐位可复现）。</summary>
    private static string Tally(Dictionary<string, int> d)
    {
        var keys = new List<string>(d.Keys);
        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append(keys[i]).Append(":").Append(d[keys[i]]);
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    /// <summary>点在 (a→b) 上的投影参数 0..1（只按水平分量）。</summary>
    private static float Project(Vector3 a, Vector3 b, Vector3 p)
    {
        var abx = b.x - a.x; var abz = b.z - a.z;
        var len2 = abx * abx + abz * abz;
        if (len2 < 1e-6f) return 1f;
        return ((p.x - a.x) * abx + (p.z - a.z) * abz) / len2;
    }

    // ==================================================================
    //  公共
    // ==================================================================
    private static CsMap Facade(out ICsMatch match)
    {
        match = null;
        var mm = MatchModule.Instance;
        if (mm == null) return null;
        match = mm.Match;
        return mm.Map as CsMap;
    }

    private static Dictionary<string, string> LoadArgs()
    {
        var d = new Dictionary<string, string>();
        if (!File.Exists(ArgsPath))
        {
            Debug.Log("[move-stuck] 没有参数文件 " + ArgsPath + " ⇒ 用默认值");
            return d;
        }
        var lines = File.ReadAllLines(ArgsPath);
        for (var i = 0; i < lines.Length; i++)
        {
            var s = lines[i].Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            var k = s.IndexOf('=');
            if (k <= 0) continue;
            d[s.Substring(0, k).Trim()] = s.Substring(k + 1).Trim();
        }
        Debug.Log("[move-stuck] 参数 " + lines.Length + " 行，键=" + d.Count);
        return d;
    }

    private static string Get(Dictionary<string, string> d, string k, string def)
        => d.TryGetValue(k, out var v) ? v : def;

    private static float G(Dictionary<string, string> d, string k, string def = "0")
        => float.Parse(Get(d, k, def), CultureInfo.InvariantCulture);
}
