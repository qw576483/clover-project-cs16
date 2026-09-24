// 判据资产（tools/probes/）：差异 #88「能开局」第②段的**实机侧**取证 —— Unity 客户端消费远端快照、
// 把远端角色画出来。与 `tools/probes/lan-remote-view-check.py`（**独立进程**当主机）配对跑。
//
// 角色分工（别搞反）：
//   · python 脚本 = **主机**（第二个进程、真 socket）：发 CS16-LAN-SNAP/1，坐标是它脚本化的；
//   · 本探针跑在 **Play 里的 Unity 侧** = **客户端**：`CsLanClient.Start()` 连上去，
//     `CsLanRemoteView` 把 actors[] 画成场上的人；本探针只**读运行时对象**并把值落盘。
//   · "手上的值"（视图 transform / 存活 / Collider 数）出自本探针；"收到的值"出自 python 侧
//     ⇒ 两侧由**不同进程各自独立**产生，最后由 python 逐字段比对（不靠任何一方的叙述）。
//
// 模式由落盘文件给定（`eval_file` 每次只跑一段、受 5s 主线程上限 ⇒ 不许在本文件里 Sleep）：
//   .ai-tmp/test/lan-remote-mode.txt  内容 = whereami | start | observe | observe0 | cam [id] | camall | stop
//   whereami  → 把本地玩家坐标 + **最开阔的水平方向**写进 .ai-tmp/test/lan-remote-place.txt
//               （给 python 当"底座"：远端角色摆在开阔方向上、同一片可行走地面，而不是埋进墙里）
//   start     → CsLanClient.Start(127.0.0.1:8012)
//   observe   → 读远端视图 + 最新快照，落盘 `.ai-tmp/test/lan-remote-view-unity.tsv` + RESULT-LANREMOTE
//   observe0  → 只看"视图是否已收干净"（停客户端之后的负控）
//   cam [id]  → 在**单个**远端角色上架一台探针相机（深度 100 ⇒ 盖住第一人称视角，贴脸看模型/动画）
//   camall    → 架一台**宽景**相机把场上三个远端角色一起框住（联络图主图）
//
// 用法：unity command eval_file --file tools/probes/probe-lan-remote-view.cs
var inv = System.Globalization.CultureInfo.InvariantCulture;
var TMP = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\";
var OBS = TMP + "lan-remote-view-unity.tsv";
var PLACE = TMP + "lan-remote-place.txt";
var MODEFILE = TMP + "lan-remote-mode.txt";
const string Endpoint = "127.0.0.1:8012";

System.Func<float, string> F3 = v => v.ToString("F3", inv);
System.Func<float, string> F1 = v => v.ToString("F1", inv);
System.Func<UnityEngine.Vector3, string> P3 = v => F3(v.x) + "," + F3(v.y) + "," + F3(v.z);

var sb = new System.Text.StringBuilder();
var mode = "observe";
if (System.IO.File.Exists(MODEFILE))
{
    var raw = System.IO.File.ReadAllText(MODEFILE).Trim();
    if (raw.Length > 0) mode = raw;
}
var parts = mode.Split(' ');
var verb = parts[0];
var arg = parts.Length > 1 ? parts[1] : "";
sb.Append("[mode] ").Append(mode);

// ------------------------------------------------------------------ whereami
if (verb == "whereami")
{
    var mm = Cs16.Module.Match.MatchModule.Instance;
    if (mm == null) return sb.Append("\nERROR: MatchModule.Instance 为 null（没从 Boot 场景进 Play？）").ToString();
    var m = mm.Match;
    var lp = m != null ? m.LocalPlayer : null;
    if (lp == null) return sb.Append("\nERROR: 本地玩家为 null（这一局还没开局？先 New Game → Start）").ToString();

    // 为什么连朝向一起写：远端角色要摆在「本地玩家**正前方**的一片地上」——
    // ① 前方 = 一定在本机相机视野里（截图才拍得到它们）；② 前方 = 大概率和玩家踩的是同一片可行走地面
    //    （凭空给一个世界坐标很可能埋在墙里）。python 侧按 f（前）/ r（右）拼出绝对坐标。
    var yr = lp.Yaw * UnityEngine.Mathf.Deg2Rad;
    var fx = UnityEngine.Mathf.Sin(yr);
    var fz = UnityEngine.Mathf.Cos(yr);

    // ---- 再选一个"最开阔的水平方向"（ax,az）：**远端角色按它摆**，不按玩家朝向摆。
    // 实测教训（第 1 轮实拍）：CT 出生点朝向前方 ~2 m 就是一堵墙 ⇒ 按朝向摆会把三个人**放进墙里**，
    // 第一人称原视角拍出来只有墙（`lan_remote_actor.png` 第一版就是那样）。
    // 口径：从玩家身边 1.5 m 外沿 8 个方向各打 12 m 射线（**只打 CsWorld 层**，不打角色/bot，
    // 否则"最开阔的方向"会被自己人挡住），取"最远的那条"当开阔方向。不引入随机。
    var worldMask = 1 << Cs16.Core.PhysicsLayers.World;
    var bestDir = new UnityEngine.Vector3(0f, 0f, 1f);
    var bestClear = -1f;
    for (var k = 0; k < 8; k++)
    {
        var ang = k * 45f * UnityEngine.Mathf.Deg2Rad;
        var d = new UnityEngine.Vector3(UnityEngine.Mathf.Sin(ang), 0f, UnityEngine.Mathf.Cos(ang));
        var from = lp.Position + UnityEngine.Vector3.up * 0.9f + d * 1.5f;
        UnityEngine.RaycastHit h;
        var clear = UnityEngine.Physics.Raycast(from, d, out h, 12f, worldMask) ? 1.5f + h.distance : 13.5f;
        if (clear > bestClear) { bestClear = clear; bestDir = d; }
    }

    System.IO.File.WriteAllText(PLACE,
        "x=" + F3(lp.Position.x) + "\ny=" + F3(lp.Position.y) + "\nz=" + F3(lp.Position.z) + "\n" +
        "fx=" + F3(fx) + "\nfz=" + F3(fz) + "\n" +
        "ax=" + F3(bestDir.x) + "\naz=" + F3(bestDir.z) + "\nclear=" + F3(bestClear) + "\n");
    sb.Append("\n[whereami] 本地玩家 ").Append(lp.Name).Append(" pos=").Append(P3(lp.Position))
      .Append(" yaw=").Append(F1(lp.Yaw))
      .Append(" 朝向前=(").Append(F3(fx)).Append(",0,").Append(F3(fz)).Append(")")
      .Append(" ⇒ 开阔方向=(").Append(F3(bestDir.x)).Append(",0,").Append(F3(bestDir.z))
      .Append(") 净空=").Append(F3(bestClear)).Append(" m")
      .Append("\n[whereami] 已写 ").Append(PLACE).Append("（远端角色将摆在这个开阔方向上）");
    return sb.ToString();
}

// ------------------------------------------------------------------ start
if (verb == "start")
{
    string err;
    var started = Cs16.Module.Net.CsLanClient.Start(Endpoint, "unity-lan-client", out err);
    sb.Append("\n[start] CsLanClient.Start(").Append(Endpoint).Append(")=").Append(started)
      .Append(" err=").Append(err ?? "-");
    sb.Append("\n[start] ").Append(Cs16.Module.Net.CsLanClient.Describe());
    if (!started) sb.Append("\nERROR: 客户端没起来，后面的判据没有意义");
    return sb.ToString();
}

// ------------------------------------------------------------------ stop
if (verb == "stop")
{
    Cs16.Module.Net.CsLanClient.Stop();
    var cam0 = UnityEngine.GameObject.Find("LanRemoteProbeCam");
    // （探针文件不在业务扫描范围里，这里用 Find 只为收掉自己架的那台相机；业务代码不许这么写）
    if (cam0 != null) UnityEngine.Object.Destroy(cam0);
    sb.Append("\n[stop] 已停：").Append(Cs16.Module.Net.CsLanClient.Describe());
    sb.Append("\n[stop] 下一帧 MatchModule.Update 的 SyncAll 应把远端视图收干净（再跑 observe0 看）");
    return sb.ToString();
}

// ------------------------------------------------------------------ 收集视图
// 口径（照引擎源码定的，不是猜）：`ObjectPool.Despawn` = `SetActive(false)` + 挂回 [ObjectPool] 根，
//    **不销毁**（com.clover.unity-engine/Runtime/Presentation/ObjectPool.cs:164）。所以"远端角色收干净了没有"
//    绝不能数"场上对象总数 == 0" —— 回收后的实例仍在内存里。正确口径 = **本系统自己的账**：
//    `RemoteId >= 0` ⟺ 该视图还在 `CsLanRemoteView.Views` 字典里（`Dispose()` 会把它复位成 -1）。
//    三组各自落盘（在场 / 非活跃 / 回池），原始数字全在，不靠"看着收干净了"。
var found = UnityEngine.Object.FindObjectsByType<Cs16.Module.View.CsLanRemoteView>(
    UnityEngine.FindObjectsInactive.Include);
var live = new System.Collections.Generic.List<Cs16.Module.View.CsLanRemoteView>();    // 在场（RemoteId>=0）
var pooled = new System.Collections.Generic.List<Cs16.Module.View.CsLanRemoteView>();  // 已回池（RemoteId<0）
var activeOnField = 0;
for (var i = 0; i < found.Length; i++)
{
    var vv = found[i];
    if (vv == null) continue;
    if (vv.RemoteId >= 0) { live.Add(vv); if (vv.gameObject.activeInHierarchy) activeOnField++; }
    else pooled.Add(vv);
}
live.Sort((x, y) => x.RemoteId.CompareTo(y.RemoteId));

// ------------------------------------------------------------------ observe0（停客户端后的负控）
if (verb == "observe0")
{
    var running = Cs16.Module.Net.CsLanClient.IsRunning;
    var reg = Cs16.Module.View.CsLanRemoteView.ViewCount;
    sb.Append("\n[observe0] 客户端 IsRunning=").Append(running)
      .Append(" | 在场远端视图(RemoteId>=0)=").Append(live.Count)
      .Append("（其中 activeInHierarchy=").Append(activeOnField).Append("）")
      .Append(" | 注册表 CsLanRemoteView.ViewCount=").Append(reg)
      .Append(" | 已回池实例(RemoteId<0)=").Append(pooled.Count);
    for (var i = 0; i < live.Count; i++)
    {
        sb.Append("\n    id=").Append(live[i].RemoteId).Append(" pos=").Append(P3(live[i].transform.position));
    }
    var okStop = live.Count == 0 && reg == 0 && !running;
    sb.Append("\nRESULT-LANREMOTE-STOP: ").Append(okStop ? "PASS" : "FAIL");
    sb.Append("\n  口径（收尾侧）：客户端已停=").Append(!running).Append("（IsRunning=").Append(running).Append("）")
      .Append("；远端视图已收干净（场上=0 且 注册表=0）=").Append(live.Count == 0 && reg == 0)
      .Append("；回池实例=").Append(pooled.Count)
      .Append(" 具（引擎池口径：回收=置非活跃+挂回池根、⛔ 不销毁 ⇒ 这个数**不必**为 0，它是「没有泄漏 / 没有每帧新建」的证据）");
    return sb.ToString();
}

// ------------------------------------------------------------------ cam
if (verb == "cam")
{
    if (live.Count == 0) return sb.Append("\nERROR: 场上没有远端视图，截图会是一场空").ToString();
    var wantId = -1;
    if (arg.Length > 0) int.TryParse(arg, out wantId);
    Cs16.Module.View.CsLanRemoteView pick = null;
    for (var i = 0; i < live.Count; i++)
    {
        if (wantId < 0 || live[i].RemoteId == wantId) { pick = live[i]; break; }
    }
    if (pick == null) return sb.Append("\nERROR: 找不到 id=").Append(wantId).Append(" 的远端视图").ToString();

    // 为什么"贴近拍"这条路被放弃（两次实拍都失败）：出生点往外是**带坡/台阶的地形**，
    //    把镜头挪到目标近旁（回退 2.8 m、只抬 1.55 m）会**扎进地面以下** ⇒ 拍出来整幅是地面
    //    （`lan_remote_actor_closeup.png` 前两版都是那样）。⇒ 改成**长焦**：站到 `camall` **已证可用**
    //    的那处视点（质心沿开阔方向回退 5.0 m、抬高 3.0 m），把 FOV 收窄、直接瞄那一具 ⇒
    //    视点可靠 + 目标占满画面，两个目的同时满足。不用任何"试探位置"。
    var c2 = UnityEngine.Vector3.zero;
    for (var i = 0; i < live.Count; i++) c2 += live[i].transform.position;
    c2 /= live.Count;

    var mmc = Cs16.Module.Match.MatchModule.Instance;
    var lpc = mmc != null && mmc.Match != null ? mmc.Match.LocalPlayer : null;
    var backC = lpc != null ? c2 - lpc.Position : new UnityEngine.Vector3(0f, 0f, -1f);
    backC.y = 0f;
    if (backC.sqrMagnitude < 0.0001f) backC = new UnityEngine.Vector3(0f, 0f, -1f);
    backC = backC.normalized;

    var go = UnityEngine.GameObject.Find("LanRemoteProbeCam");
    if (go == null) go = new UnityEngine.GameObject("LanRemoteProbeCam");
    var cam = go.GetComponent<UnityEngine.Camera>();
    if (cam == null) cam = go.AddComponent<UnityEngine.Camera>();
    cam.depth = 100f;                                  // 盖住第一人称相机（截图看的是它）
    cam.fieldOfView = 16f;                             // 长焦：把目标拉满画面
    cam.nearClipPlane = 0.05f;
    cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
    cam.backgroundColor = new UnityEngine.Color(0.12f, 0.12f, 0.14f, 1f);

    var eye2 = c2 - backC * 5.0f + UnityEngine.Vector3.up * 3.0f;
    var aim = pick.transform.position + UnityEngine.Vector3.up * 0.80f;
    go.transform.position = eye2;
    go.transform.LookAt(aim);

    sb.Append("\n[cam] 探针相机（长焦 fov=16）对准远端角色 id=").Append(pick.RemoteId)
      .Append(" 站位=").Append(P3(pick.transform.position))
      .Append("（相机 ").Append(P3(eye2)).Append(" = 质心沿开阔方向回退 5.0 m、抬高 3.0 m，depth=100）");
    sb.Append("\n[cam] 现在可以 capture_game_view --source screen 截图");
    return sb.ToString();
}

// ------------------------------------------------------------------ camall（宽景：三个人一起进画面）
if (verb == "camall")
{
    if (live.Count == 0) return sb.Append("\nERROR: 场上没有远端视图，截图会是一场空").ToString();

    var c = UnityEngine.Vector3.zero;
    for (var i = 0; i < live.Count; i++) c += live[i].transform.position;
    c /= live.Count;

    // 回退方向 = 从玩家指向"三人质心"的那条线：远端角色就摆在玩家选出的开阔方向上
    // ⇒ 沿这条线往回退，镜头必然在开阔一侧（不是从墙里往外看）。
    var mm2 = Cs16.Module.Match.MatchModule.Instance;
    var lp2 = mm2 != null && mm2.Match != null ? mm2.Match.LocalPlayer : null;
    var back = lp2 != null ? c - lp2.Position : new UnityEngine.Vector3(0f, 0f, -1f);
    back.y = 0f;
    if (back.sqrMagnitude < 0.0001f) back = new UnityEngine.Vector3(0f, 0f, -1f);
    back = back.normalized;

    var go = UnityEngine.GameObject.Find("LanRemoteProbeCam");
    if (go == null) go = new UnityEngine.GameObject("LanRemoteProbeCam");
    var cam = go.GetComponent<UnityEngine.Camera>();
    if (cam == null) cam = go.AddComponent<UnityEngine.Camera>();
    cam.depth = 100f;                                  // 盖住第一人称相机（截图看的是它）
    cam.fieldOfView = 50f;
    cam.nearClipPlane = 0.05f;
    cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
    cam.backgroundColor = new UnityEngine.Color(0.10f, 0.11f, 0.13f, 1f);

    go.transform.position = c - back * 5.0f + UnityEngine.Vector3.up * 3.0f;
    go.transform.LookAt(c + UnityEngine.Vector3.up * 0.7f);

    sb.Append("\n[camall] 三个人质心=").Append(P3(c))
      .Append(" ⇒ 相机=").Append(P3(go.transform.position)).Append("（沿开阔方向回退 5.0 m、抬高 3.0 m，depth=100）");
    for (var i = 0; i < live.Count; i++)
    {
        sb.Append("\n    id=").Append(live[i].RemoteId).Append(" pos=").Append(P3(live[i].transform.position));
    }
    sb.Append("\n[camall] 现在可以 capture_game_view --source screen 截图（三个远端角色应与场上一同入画）");
    return sb.ToString();
}

// ------------------------------------------------------------------ observe（主判据）
var snap = new System.Collections.Generic.List<Cs16.Module.Net.CsLanRemoteActor>();
var haveSnap = Cs16.Module.Net.CsLanClient.TryGetActors(snap);
snap.Sort((x, y) => x.Id.CompareTo(y.Id));

var tsv = new System.Text.StringBuilder();
tsv.Append("section\tkey\tvalue\n");
tsv.Append("client\tisrunning\t").Append(Cs16.Module.Net.CsLanClient.IsRunning ? "true" : "false").Append('\n');
tsv.Append("client\twelcomes\t").Append(Cs16.Module.Net.CsLanClient.Welcomes).Append('\n');
tsv.Append("client\tsnapshots\t").Append(Cs16.Module.Net.CsLanClient.Snapshots).Append('\n');
tsv.Append("client\tmalformed\t").Append(Cs16.Module.Net.CsLanClient.Malformed).Append('\n');
tsv.Append("client\tviews\t").Append(live.Count).Append('\n');
tsv.Append("client\tviewsActive\t").Append(activeOnField).Append('\n');
tsv.Append("client\tregistry\t").Append(Cs16.Module.View.CsLanRemoteView.ViewCount).Append('\n');
tsv.Append("client\tpooled\t").Append(pooled.Count).Append('\n');
tsv.Append("client\tcreatedTotal\t").Append(Cs16.Module.View.CsLanRemoteView.CreatedTotal).Append('\n');
tsv.Append("client\trecycledTotal\t").Append(Cs16.Module.View.CsLanRemoteView.RecycledTotal).Append('\n');
tsv.Append("client\tstrippedColliders\t").Append(Cs16.Module.View.CsLanRemoteView.StrippedCollidersTotal).Append('\n');
tsv.Append("client\tsnapshotActors\t").Append(snap.Count).Append('\n');

for (var i = 0; i < snap.Count; i++)
{
    var a = snap[i];
    tsv.Append("snap\t").Append(a.Id).Append(".pos\t").Append(F3(a.X)).Append(',').Append(F3(a.Y)).Append(',').Append(F3(a.Z)).Append('\n');
    tsv.Append("snap\t").Append(a.Id).Append(".yaw\t").Append(F1(a.Yaw)).Append('\n');
    tsv.Append("snap\t").Append(a.Id).Append(".alive\t").Append(a.Alive ? "true" : "false").Append('\n');
    tsv.Append("snap\t").Append(a.Id).Append(".hp\t").Append(a.Hp).Append('\n');
    tsv.Append("snap\t").Append(a.Id).Append(".team\t").Append(a.Team).Append('\n');
    tsv.Append("snap\t").Append(a.Id).Append(".name\t").Append(a.Name).Append('\n');
}

for (var i = 0; i < live.Count; i++)
{
    var v = live[i];
    var id = v.RemoteId;
    var cols = v.GetComponentsInChildren<UnityEngine.Collider>(true);
    tsv.Append("view\t").Append(id).Append(".pos\t").Append(P3(v.transform.position)).Append('\n');
    tsv.Append("view\t").Append(id).Append(".yaw\t").Append(F1(v.transform.eulerAngles.y)).Append('\n');
    tsv.Append("view\t").Append(id).Append(".alive\t").Append(v.Shadow != null && v.Shadow.IsAlive ? "true" : "false").Append('\n');
    tsv.Append("view\t").Append(id).Append(".active\t").Append(v.gameObject.activeSelf ? "true" : "false").Append('\n');
    tsv.Append("view\t").Append(id).Append(".colliders\t").Append(cols.Length).Append('\n');
    tsv.Append("view\t").Append(id).Append(".team\t").Append(v.Team.ToString()).Append('\n');
    tsv.Append("view\t").Append(id).Append(".name\t").Append(v.gameObject.name).Append('\n');
}
System.IO.File.WriteAllText(OBS, tsv.ToString());

// ---- Unity 侧判据（"手上的值" vs "本进程收到的值"；跨进程那一半由 python 判）----
var okRunning = Cs16.Module.Net.CsLanClient.IsRunning;
var okWelcome = Cs16.Module.Net.CsLanClient.Welcomes >= 1;
var okSnaps = Cs16.Module.Net.CsLanClient.Snapshots >= 15;
var okMalformed = Cs16.Module.Net.CsLanClient.Malformed == 0;
var okHaveSnap = haveSnap && snap.Count > 0;
var okViewCount = snap.Count > 0 && live.Count == snap.Count &&
                  Cs16.Module.View.CsLanRemoteView.ViewCount == snap.Count;

var okFields = true;
var okColliders = true;
var okAlive = true;
var detail = new System.Text.StringBuilder();
for (var i = 0; i < snap.Count; i++)
{
    var a = snap[i];
    Cs16.Module.View.CsLanRemoteView v = null;
    for (var j = 0; j < live.Count; j++)
    {
        if (live[j].RemoteId == a.Id) { v = live[j]; break; }
    }
    if (v == null)
    {
        detail.Append("\n    id=").Append(a.Id).Append(" 快照里有、场上没有视图 ✗");
        okFields = false;
        continue;
    }

    var p = v.transform.position;
    var dp = System.Math.Max(System.Math.Abs(p.x - a.X),
               System.Math.Max(System.Math.Abs(p.y - a.Y), System.Math.Abs(p.z - a.Z)));
    var dy = System.Math.Abs(UnityEngine.Mathf.DeltaAngle(v.transform.eulerAngles.y, a.Yaw));
    var cols = v.GetComponentsInChildren<UnityEngine.Collider>(true).Length;
    var aliveOk = (v.Shadow != null) && (v.Shadow.IsAlive == a.Alive);

    detail.Append("\n    id=").Append(a.Id)
          .Append(" 手上=").Append(P3(p)).Append(" yaw=").Append(F1(v.transform.eulerAngles.y))
          .Append(" | 收到=").Append(F3(a.X)).Append(',').Append(F3(a.Y)).Append(',').Append(F3(a.Z))
          .Append(" yaw=").Append(F1(a.Yaw))
          .Append(" | Δpos=").Append(F3(dp)).Append(" Δyaw=").Append(F1(dy))
          .Append(" alive=").Append(a.Alive).Append('/').Append(aliveOk)
          .Append(" active=").Append(v.gameObject.activeSelf)
          .Append(" colliders=").Append(cols);

    if (!(dp <= 0.002f) || !(dy <= 0.06f)) okFields = false;
    if (cols != 0) okColliders = false;
    if (!aliveOk) okAlive = false;
    // 活人必须在场上看得见；死人由下面的"尸体"一段单独判（ActorView 的倒地序列需要几帧才停住）
    if (a.Alive && !v.gameObject.activeSelf) okAlive = false;
}

// 死亡那一具：系统必须**仍然为它保留一具视图**（不是被丢掉），且 alive 标记跟着快照走；
var okCorpseHeld = true;
var corpses = 0;
var corpseActive = 0;
for (var i = 0; i < snap.Count; i++)
{
    if (snap[i].Alive) continue;
    corpses++;
    Cs16.Module.View.CsLanRemoteView v = null;
    for (var j = 0; j < live.Count; j++) if (live[j].RemoteId == snap[i].Id) { v = live[j]; break; }
    if (v == null) okCorpseHeld = false;
    else if (v.gameObject.activeSelf) corpseActive++;
}

var ok = okRunning && okWelcome && okSnaps && okMalformed && okHaveSnap && okViewCount &&
         okFields && okColliders && okAlive && okCorpseHeld;

sb.Append("\n[observe] 客户端：IsRunning=").Append(okRunning).Append(" WELCOME=").Append(Cs16.Module.Net.CsLanClient.Welcomes)
  .Append(" 快照=").Append(Cs16.Module.Net.CsLanClient.Snapshots).Append(" 非法行=").Append(Cs16.Module.Net.CsLanClient.Malformed);
sb.Append("\n[observe] 快照里角色=").Append(snap.Count).Append(" 场上远端视图=").Append(live.Count)
  .Append("（其中 activeInHierarchy=").Append(activeOnField).Append("，注册表=")
  .Append(Cs16.Module.View.CsLanRemoteView.ViewCount).Append("，已回池=").Append(pooled.Count).Append("）")
  .Append(" | ").Append(Cs16.Module.View.CsLanRemoteView.Describe());
sb.Append(detail);
sb.Append("\n[observe] 观测值已落盘：").Append(OBS);
sb.Append("\n[observe] 首个视图逐字段：").Append(live.Count > 0 ? "见上表 id=" + live[0].RemoteId : "（场上没有视图）");
sb.Append("\nRESULT-LANREMOTE: ").Append(ok ? "PASS" : "FAIL");
sb.Append("\n  口径（实机侧）：客户端在跑=").Append(okRunning)
  .Append(" 握过手=").Append(okWelcome)
  .Append(" 快照>=15×（=").Append(Cs16.Module.Net.CsLanClient.Snapshots).Append("）=").Append(okSnaps)
  .Append(" 非法行=0=").Append(okMalformed)
  .Append(" 视图数=快照角色数（").Append(live.Count).Append("=").Append(snap.Count).Append("）=").Append(okViewCount)
  .Append(" 位置/朝向逐字段一致=").Append(okFields)
  .Append(" 存活标记一致且活人在场上=").Append(okAlive)
  .Append(" 死亡那具仍留视图（=").Append(corpses).Append(" 具）=").Append(okCorpseHeld)
  .Append(" 远端视图 Collider 全 0=").Append(okColliders)
  .Append("\n  池化（⛔ 不每帧新建）：累计新建=").Append(Cs16.Module.View.CsLanRemoteView.CreatedTotal)
  .Append(" 累计回收=").Append(Cs16.Module.View.CsLanRemoteView.RecycledTotal)
  .Append(" 销毁Collider=").Append(Cs16.Module.View.CsLanRemoteView.StrippedCollidersTotal)
  .Append(" 建失败=").Append(Cs16.Module.View.CsLanRemoteView.CreateFailures)
  .Append("；死亡那具此刻渲染着=").Append(corpseActive).Append("/").Append(corpses);
return sb.ToString();
