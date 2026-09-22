// =====================================================================
// cs16 Play 驱动（.ai-tmp/drivers/，本轮复用）
//
// 用法（每次都要带绝对路径）：
//   unity command run_script --file <本文件绝对路径> --entry Cs16Drv.Entry.Setup
//   unity command run_script --file <本文件绝对路径> --entry Cs16Drv.Entry.StartMatch
//   unity command run_script --file <本文件绝对路径> --entry Cs16Drv.Entry.Dump --args '["tag"]'
//
// 设计要点（为什么不是一个纯 eval 回调）：
//   本地玩家输入必须**晚于** PlayerModule.Update（order -200）写进 ICsMatch，
//   否则会被 PlayerModule 每帧覆盖回零。因此驱动是一个 [DefaultExecutionOrder(-150)]
//   的 MonoBehaviour：-200 < -150 < 0（MatchModule 消费）。
//
// 状态来自一个文本文件（由外部改写），字段格式：
//   run=1
//   pause=0
//   input=moveX,moveY,yaw,pitch,jump,crouch,walk,fire,zoom      (0/1 for bools)
//   freezeLocal=0
//   localPos=x,z           (freezeLocal=1 时每帧写回本地玩家位置)
//   poseCount=2
//   pose0=x,y,z,yaw,state     state in idle|walk|run|crouch_idle|crouchrun|jump|death|fire
//   pose1=...
// =====================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Combat;
using Cs16.Module.Match;
using Cs16.Module.View;
using UnityEngine;

namespace Cs16Drv
{
    public static class Paths
    {
        public const string State = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\drivers\state.txt";
        // Runtime log written by THIS driver during a Play session.  Renamed from
        // `driver-log.tsv` (slice AI): the judgement input of
        // tools/probes/compare-cs16anim-bones.py is also called driver-log.tsv
        // (tools/probes/driver-log.tsv, a frozen 1.98 MB capture) and the two must
        // never be confused again -- read that one, write this one.
        public const string Log = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\play-driver-log.tsv";
        public const string Shots = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\screenshots";
    }

    [DefaultExecutionOrder(-150)]
    public sealed class Cs16Driver : MonoBehaviour
    {
        private float _lastRead;
        private readonly Dictionary<string, string> _kv = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> _poseLines = new List<string>();
        private readonly HashSet<int> _firePunched = new HashSet<int>();
        private string _lastStateText = "";
        private UnityEngine.UI.Button _hovered;
        private string _lastClick = "";
        private string _lastHover = null;
        private string _lastShot = null;
        private string _lastBurst = null;
        private int _burstLeft;
        private int _burstIndex;
        private float _burstNext;
        private float _burstInterval = 0.15f;
        private string _burstPrefix = "burst";
        private string _lastNote = "";
        private string _lastTeleport = "";
        private string _lastTp3 = "";
        private int _lastLiveRound = -1;
        private float _lastTrack;
        private int _readCount;
        private float _lastHeartbeat;
        private MatchModule _matchModule;
        private List<CsActor> _botScratch = new List<CsActor>();

        private void Awake()
        {
            Dump("driver.awake", "Cs16Driver 已挂载（order -150）");
        }

        private void Update()
        {
            var now = Time.realtimeSinceStartup;
            if (now - _lastHeartbeat > 5f)
            {
                _lastHeartbeat = now;
                // 心跳里带上"比赛模块拿没拿到" + 当前场景 —— 通道死掉时这条就是第一现场（见 Module() 注释）
                Dump("driver.heartbeat", "alive reads=" + _readCount + " t=" + now.ToString("F1", CultureInfo.InvariantCulture) +
                     " match=" + (MatchModule.Instance != null ? "yes" : "NO") +
                     " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                     " fps=" + (1f / Mathf.Max(0.0001f, Time.deltaTime)).ToString("F0", CultureInfo.InvariantCulture));
            }

            // 文件读取节流到 50ms；但 Apply() **必须每帧调**（本地输入会被 PlayerModule 每帧覆盖，
            // 隔帧写会让玩家"一步一停" —— 实测把 5.2 m/s 的移动压成 0.9 m/s 的假象，是驱动自身缺陷）。
            if (now - _lastRead >= 0.05f) { _lastRead = now; ReadState(); }
            Apply();
            TrackTick(now);
        }

        private float _tabNextLog;

        /// <summary>
        /// 记分板（G11 / `22_scoreboard.png`：TAB **按住**显示）——驱动没有键盘层，
        /// 而 <c>HudPanel.HandleInput</c> 每帧都在判"TAB 没按下 ⇒ 关掉记分板"，
        /// 所以只能在 <c>LateUpdate</c>（所有 Update 之后、渲染之前）把 <c>tab=1</c> 的记分板补开回来。
        /// ⛔ 不能放在 <c>Apply()</c>：那里是 order -150（HudPanel 之前），同帧就会被关掉、渲染不出来。
        /// </summary>
        private void LateUpdate()
        {
            if (!B(Get("tab"))) return;
            var ui = Game.UI;
            if (ui == null) return;
            if (ui.IsOpen<Cs16.UI.ScoreboardPanel>()) return;
            ui.Open<Cs16.UI.ScoreboardPanel>();
            var now = Time.realtimeSinceStartup;
            if (now - _tabNextLog > 1f)
            {
                _tabNextLog = now;
                AppendLine("tab", "记分板被 HudPanel 关掉后由驱动在 LateUpdate 补开（模拟 TAB 按住态）");
            }
        }

        /// <summary>track=1 时按 ~5Hz 记录本地玩家的走位轨迹（走位测试的主证据）。</summary>
        private void TrackTick(float now)
        {
            if (!B(Get("track"))) return;
            if (now - _lastTrack < 0.2f) return;
            _lastTrack = now;

            var mod = Module();
            if (mod == null) return;
            var match = mod.Match;
            if (match == null) return;
            var a = match.LocalPlayer;
            if (a == null) { AppendLine("track", "local=null"); return; }
            AppendLine("track",
                "phase=" + match.Phase + " round=" + match.RoundNumber + " alive=" + a.IsAlive +
                " pos=" + a.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                          a.Position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                          a.Position.z.ToString("F2", CultureInfo.InvariantCulture) +
                " vel=" + a.Velocity.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                          a.Velocity.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                          a.Velocity.z.ToString("F2", CultureInfo.InvariantCulture) +
                " ground=" + a.OnGround + " crouch=" + a.IsCrouching +
                " speed=" + Cs16.Module.Match.CsInventory.MovementSpeed(a).ToString("F2", CultureInfo.InvariantCulture) +
                " dt=" + Time.deltaTime.ToString("F4", CultureInfo.InvariantCulture) +
                " fps=" + (1f / Mathf.Max(0.0001f, Time.deltaTime)).ToString("F0", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        private void ReadState()
        {
            string text;
            try { text = File.ReadAllText(Paths.State); }
            catch (Exception e) { if (_readCount < 3) Dump("driver.state.missing", e.Message); return; }
            text = text.TrimStart('\uFEFF');
            _readCount++;
            if (text == _lastStateText) return;
            _lastStateText = text;

            _kv.Clear();
            _poseLines.Clear();
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim();
                if (k.StartsWith("pose", StringComparison.Ordinal) && k != "poseCount") _poseLines.Add(v);
                else _kv[k] = v;
            }
            Dump("driver.state", "reload kv=" + _kv.Count + " poses=" + _poseLines.Count);
        }

        private string Get(string key) => _kv.TryGetValue(key, out var v) ? v : null;

        private static float F(string s, float def)
        {
            float r;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private static int I(string s, int def)
        {
            int r;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private static bool B(string s) => s == "1" || s == "true";

        private float _lastNoMatchLog = -100f;

        /// <summary>
        /// 取比赛模块。**不用 <c>FindObjectOfType</c>**（Unity 6 已废弃，且它在本环境里静默返回 null，
        /// 导致整条驱动通道无声失效 —— 2026-09-21 实测：`snap  matchModule=null`，
        /// 根因是 Play 从 <c>StageDust2</c> 进而不是从 <c>Boot.unity</c> 进，
        /// 而 <c>Bootstrap</c>（比赛模块的挂载点）只在 <c>Boot.unity</c> 里）。
        /// 改走业务给出的模块入口 <see cref="MatchModule.Instance"/>。
        /// </summary>
        private MatchModule Module()
        {
            if (_matchModule != null) return _matchModule;
            _matchModule = MatchModule.Instance;
            if (_matchModule == null) NoMatchWarn();
            return _matchModule;
        }

        /// <summary>拿不到比赛模块时**必须留痕**（降频）—— "静默早退"是这条通道历史上最贵的一个缺陷。</summary>
        private void NoMatchWarn()
        {
            var now = Time.realtimeSinceStartup;
            if (now - _lastNoMatchLog < 5f) return;
            _lastNoMatchLog = now;
            Dump("driver.nomatch",
                "MatchModule.Instance == null ⇒ pause/godmode/cam/input/teleport 本轮全部不生效。" +
                "最常见原因：Play 不是从 Scenes/Boot.unity 进的（Bootstrap 只在 Boot 场景里）⇒ 引擎没 Launch。" +
                " 当前场景=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name +
                " engineRunning=" + CloverEngine.Game.IsRunning);
        }

        private void Apply()
        {
            var mod = Module();
            if (mod == null) return;
            var match = mod.Match;
            if (match == null)
            {
                NoMatchWarn();
                return;
            }

            var pauseSpec = Get("pause");
            if (pauseSpec != null) match.SetPaused(B(pauseSpec));

            // ---- click=<ButtonGameObjectName>：点一次按钮（走 EventSystem 指针事件）----
            var clickSpec = Get("click");
            if (clickSpec != null && clickSpec != _lastClick) { _lastClick = clickSpec; DoClick(clickSpec); }

            // ---- hover=<ButtonGameObjectName>：悬停（指针进 / 空值 = 指针出）----
            // 为什么要它：原版悬停态（ButtonArmedBg → SelectionBG）只有真指针进才变色，
            // 离线读 Selectable 的 inspector 值证明不了"上屏色变了"（见 策划/验收表.md M6 行）。
            var hoverSpec = Get("hover");
            if (hoverSpec != null && hoverSpec != _lastHover) { _lastHover = hoverSpec; DoHover(hoverSpec); }

            // ---- shot=<name>：把当前画面存到 .ai-tmp/screenshots/<name>.png（同帧，供短窗口用）----
            var shotSpec = Get("shot");
            if (shotSpec != null && shotSpec != _lastShot) { _lastShot = shotSpec; Shot(shotSpec); }

            // ---- burst=<prefix>,<count>,<intervalMs>：连拍（抢"启动画面 / 读条"这种秒级窗口）----
            var burstSpec = Get("burst");
            if (burstSpec != null && burstSpec != _lastBurst)
            {
                _lastBurst = burstSpec;
                var bp = burstSpec.Split(',');
                _burstPrefix = bp.Length > 0 ? bp[0] : "burst";
                _burstLeft = bp.Length > 1 ? (int)F(bp[1], 8f) : 8;
                _burstInterval = (bp.Length > 2 ? F(bp[2], 150f) : 150f) / 1000f;
                _burstIndex = 0;
                _burstNext = Time.realtimeSinceStartup;
                AppendLine("burst", "start prefix=" + _burstPrefix + " count=" + _burstLeft +
                                    " intervalMs=" + (_burstInterval * 1000f).ToString("F0", CultureInfo.InvariantCulture));
            }
            if (_burstLeft > 0 && Time.realtimeSinceStartup >= _burstNext)
            {
                _burstNext = Time.realtimeSinceStartup + _burstInterval;
                Shot(_burstPrefix + "_" + _burstIndex.ToString("00", CultureInfo.InvariantCulture));
                _burstIndex++;
                _burstLeft--;
                if (_burstLeft == 0) AppendLine("burst", "done " + _burstPrefix);
            }

            // ---- note=<text>：往日志里写一行（值变了才写）----
            var noteSpec = Get("note");
            if (noteSpec != null && noteSpec != _lastNote) { _lastNote = noteSpec; AppendLine("note", noteSpec); }

            // ---- teleport=x,z：一次性把本地玩家挪过去（值变了才生效）----
            var tp = Get("teleport");
            if (tp != null && tp != _lastTeleport)
            {
                _lastTeleport = tp;
                var lp = match.LocalPlayer;
                var p = tp.Split(',');
                if (lp != null && p.Length >= 2)
                {
                    lp.Position = new Vector3(F(p[0], lp.Position.x), lp.Position.y + 0.05f, F(p[1], lp.Position.z));
                    lp.Velocity = Vector3.zero;
                    lp.OnGround = true;
                    AppendLine("teleport", "local -> " + lp.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                           lp.Position.z.ToString("F2", CultureInfo.InvariantCulture));
                }
            }

            // ---- tp3=x,y,z：显式三轴摆放（teleport 只给 x,z、沿用旧 y，遇到"脚下是另一层"
            //      的场景会把人放到地板下面 —— 实测：脚下 y 落后于真实地面时 CanStand(from)=false，
            //      ResolveMove 直接原地不动，表现为"输进去了但一步没走"，是驱动自身的坑）----
            var tp3 = Get("tp3");
            if (tp3 != null && tp3 != _lastTp3)
            {
                _lastTp3 = tp3;
                var lp = match.LocalPlayer;
                var p = tp3.Split(',');
                if (lp != null && p.Length >= 3)
                {
                    lp.Position = new Vector3(F(p[0], lp.Position.x), F(p[1], lp.Position.y), F(p[2], lp.Position.z));
                    lp.Velocity = Vector3.zero;
                    lp.OnGround = false;
                    AppendLine("tp3", "local -> " + lp.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                              lp.Position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                              lp.Position.z.ToString("F2", CultureInfo.InvariantCulture));
                }
            }

            // ---- 本地玩家输入（晚于 PlayerModule.Update 覆盖）----
            var inputSpec = Get("input");
            if (inputSpec != null && match.IsRunning)
            {
                var p = inputSpec.Split(',');
                if (p.Length >= 9)
                {
                    var cmd = default(CsInputState);
                    cmd.Move = new Vector2(F(p[0], 0f), F(p[1], 0f));
                    cmd.Yaw = F(p[2], 0f);
                    cmd.Pitch = F(p[3], 0f);
                    cmd.Jump = B(p[4]);
                    cmd.Crouch = B(p[5]);
                    cmd.Walk = B(p[6]);
                    cmd.Fire = B(p[7]);
                    cmd.Zoom = B(p[8]);
                    match.SetLocalInput(cmd);
                }
            }

            // ---- godmode=1：本地玩家每帧满血（走位测试用；bot 打不死我，但位移解算完全真实）----
            if (B(Get("godmode")))
            {
                var g = match.LocalPlayer;
                if (g != null) { g.Health = 10000; g.Armor = 100; }
            }

            // ---- hm=1：命中标记（H9）由**快照值**驱动 ----
            // 判据原文：真实开火那 0.25s 瞬态自动化抓不到，取证帧用的是快照驱动渲染。
            // CombatModule 每帧把 CsHudSnapshot.HitMarkerTime 减到 0，这里每帧补回
            // CsConst.HitMarkerTime ⇒ 整段取证期间标记稳定留在画面上（hmHead=1 时用爆头异色）。
            if (B(Get("hm")))
            {
                CsHudSnapshot.HitMarkerTime = CsConst.HitMarkerTime;
                CsHudSnapshot.HitMarkerHeadshot = B(Get("hmHead"));
            }

            // ---- use=1：按住 E（下包 / 拆包）----
            // 为什么这一行能生效（切片P 只读查证，⛔ 未改任何游戏代码）：
            //   本帧「按住 E」的**真值**由 CombatModule.FillInput 在 PlayerModule.Update 里
            //   按真实键盘写进 CsMatch._localUseHeld（PlayerModule 是 [DefaultExecutionOrder(-200)]，
            //   而 CombatModule 本身没有 Update，只被 PlayerModule 调用）⇒ 那个覆盖发生在 order -200；
            //   本驱动是 -150（晚于 -200、早于 MatchModule 的 0 与它内部的 Bomb.SetUseState）⇒
            //   在 Apply() 里每帧补写一次，就落在「覆盖之后、模拟消费之前」，无需任何游戏代码改动。
            //   （旧记录写"驱动 order(-150) 早于它"是**把 CombatModule 当成有自己 Update 的组件**了，
            //     实际它的唯一调用点就在 PlayerModule.Update 里。）
            if (B(Get("use")) && match.IsRunning) match.SetUseHeld(true);

            // ---- di=1：受伤提示（屏幕四边红 + 方向标记）由**快照值**驱动 ----
            // 与 hm=1 同一口径：真实受击那一刻自动化抓不到，取证帧用快照驱动渲染；
            // CsMatch 每帧把 DamageIndicatorTime 递减，这里每帧补回 ⇒ 整段取证期间红框稳定留在画面上。
            if (B(Get("di")))
            {
                CsHudSnapshot.DamageIndicatorTime = CsConst.DamageIndicatorTime;
                CsHudSnapshot.DamageFromYaw = F(Get("diYaw"), 45f);
            }

            ApplyFireHeld();

            ApplyLook();
            ApplyTpBombsite(match);

            // ---- teleportOnLive=x,z：每个 Live 回合开始时把本地玩家摆到起点（走位测试的可重复入口）----
            var tol = Get("teleportOnLive");
            if (tol != null && match.Phase == CsRoundPhase.Live)
            {
                if (_lastLiveRound != match.RoundNumber)
                {
                    _lastLiveRound = match.RoundNumber;
                    var lp2 = match.LocalPlayer;
                    var pp = tol.Split(',');
                    if (lp2 != null && pp.Length >= 2)
                    {
                        lp2.Position = new Vector3(F(pp[0], lp2.Position.x), lp2.Position.y + 0.05f, F(pp[1], lp2.Position.z));
                        lp2.Velocity = Vector3.zero;
                        lp2.OnGround = true;
                        AppendLine("teleport.live", "round=" + match.RoundNumber + " local -> " +
                                    lp2.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                    lp2.Position.z.ToString("F2", CultureInfo.InvariantCulture));
                    }
                }
            }

            // ---- cam=dist,height,idx：持续把取证相机 Cs16ProbeCam 摆在第 idx 个非本地角色正前方 ----
            var camSpec = Get("cam");
            if (camSpec != null)
            {
                var cp = camSpec.Split(',');
                if (cp.Length >= 7)
                    Entry.UpdateProbeCamFixed(F(cp[0], 0f), F(cp[1], 0f), F(cp[2], 0f),
                                              F(cp[3], 0f), F(cp[4], 0f), F(cp[5], 0f), F(cp[6], 75f));
                else if (cp.Length >= 3)
                    Entry.UpdateProbeCam(F(cp[0], 3f), F(cp[1], 1.4f), I(cp[2], 0));
            }
            else
            {
                var g = Cs16Driver.Go("Cs16ProbeCam");
                if (g != null && g.activeSelf) g.SetActive(false);
            }

            var local = match.LocalPlayer;
            if (local != null && B(Get("freezeLocal")))
            {
                var pos = Get("localPos");
                if (pos != null)
                {
                    var p = pos.Split(',');
                    if (p.Length >= 2)
                    {
                        local.Position = new Vector3(F(p[0], local.Position.x), local.Position.y, F(p[1], local.Position.z));
                        local.Velocity = Vector3.zero;
                        local.OnGround = true;
                    }
                }
            }

            // ---- 姿态舞台：把 bot 摆到指定位并强制状态（用于人物模型逐序列截图）----
            if (_poseLines.Count > 0)
            {
                _botScratch.Clear();
                var actors = match.Actors;
                for (var i = 0; i < actors.Count; i++)
                {
                    if (actors[i] != null && actors[i].IsBot) _botScratch.Add(actors[i]);
                }
                // ring=<dist>：位置不改用 pose 行的坐标，而是按**相机实际朝向**把 bot 摆成一排
                // （相机 yaw 归 PlayerMotor 的 LookAccumulator，驱动无法改；只有按相机前向摆才一定在画面里）
                var ringSpec = Get("ring");
                var ring = ringSpec != null ? F(ringSpec, 3.0f) : 0f;
                Vector3 camPos = Vector3.zero, camFwd = Vector3.forward, camRight = Vector3.right;
                if (ring > 0f)
                {
                    var cam = Camera.main;
                    if (cam != null)
                    {
                        camPos = cam.transform.position;
                        camFwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized;
                        if (camFwd.sqrMagnitude < 0.5f) camFwd = Vector3.forward;
                        camRight = Vector3.Cross(Vector3.up, camFwd).normalized;
                    }
                }

                for (var i = 0; i < _poseLines.Count && i < _botScratch.Count; i++)
                {
                    var p = _poseLines[i].Split(',');
                    if (p.Length < 5) continue;
                    var a = _botScratch[i];
                    if (ring > 0f)
                    {
                        var span = 2.2f;                                  // 一排 ±2.2m
                        var off = _poseLines.Count > 1
                            ? (-span + 2f * span * i / (_poseLines.Count - 1)) : 0f;
                        var fp = camPos + camFwd * ring + camRight * off;
                        var feetY = match.LocalPlayer != null ? match.LocalPlayer.Position.y : camPos.y - 1.7f;
                        a.Position = new Vector3(fp.x, feetY, fp.z);
                        a.Yaw = Mathf.Atan2(camFwd.x, camFwd.z) * Mathf.Rad2Deg + 180f;   // 面朝相机
                    }
                    else
                    {
                        a.Position = new Vector3(F(p[0], 0f), F(p[1], 0f), F(p[2], 0f));
                        a.Yaw = F(p[3], 0f);
                    }
                    a.Pitch = 0f;
                    a.IsAlive = true;
                    a.Health = 100;
                    a.IsWalking = false;
                    a.IsCrouching = false;
                    a.OnGround = true;
                    a.Velocity = Vector3.zero;
                    // 动画速度控制：deathSpeed<1 时把倒地序列放慢，否则 1s 的 clip 播完 ActorView 就隐藏了，
                    // 取证截图（几秒延迟）根本抓不到。
                    var avv = FindView(a.Id);
                    var anv = avv != null ? avv.GetComponent<Animator>() : null;
                    if (anv != null)
                    {
                        // animSpeed=<f>：一次性把**所有**非本地视图的 Animator 钉在一个速度上。
                        // 为什么需要：姿态取证要"探针读数"与"截图"落在**同一帧**上，
                        // 否则两次调用之间序列已经往前走（speed=0 = 冻结，读数即画面对应帧）。
                        var aspec = Get("animSpeed");
                        if (aspec != null) anv.speed = F(aspec, 1f);
                        else
                        {
                            var ds = Get("deathSpeed");
                            anv.speed = (p[4] == "death" && ds != null) ? F(ds, 0.12f) : 1f;
                        }
                    }
                    var st = p[4];
                    switch (st)
                    {
                        case "idle":
                            break;
                        case "walk":
                            a.Velocity = new Vector3(0f, 0f, 1.05f);
                            a.IsWalking = true;
                            break;
                        case "run":
                            a.Velocity = new Vector3(0f, 0f, 4.4f);
                            break;
                        case "crouch_idle":
                            a.IsCrouching = true;
                            break;
                        case "crouchrun":
                            a.IsCrouching = true;
                            a.Velocity = new Vector3(0f, 0f, 1.05f);
                            break;
                        case "jump":
                            a.OnGround = false;
                            a.Velocity = new Vector3(0f, 1.5f, 0f);
                            break;
                        case "death":
                            a.IsAlive = false;
                            a.Health = 0;
                            break;
                        case "fire":
                            // 每帧前推 NextFireTime：ActorView 只认"NextFireTime 被前推" ⇒ 射击序列永远停在第 0 帧，
                            // 这样截图与状态采样都稳定命中该序列（只前推一次的话，0.3s 后就回到 idle 了）。
                            a.NextFireTime += 3.0f;
                            break;
                    }
                }
            }

            ApplySeparation();
            ApplyForce();
        }

        // ------------------------------------------------------------------
        //  视线对齐（look=yaw,pitch）—— 让"朝哪儿开枪"变成可复现的输入
        // ------------------------------------------------------------------
        // 为什么需要：射线方向取自 FirstPersonCamera.AimDirection，而它的 yaw/pitch 来自
        // PlayerMotor（LookAccumulator，鼠标累加）——驱动没有鼠标层 ⇒ 默认只能朝出生朝向开枪。
        //
        // 【切片R 起：改为**类型化的测试入口**，本处已无反射】
        // 旧做法是反射调 PlayerMotor 的 `internal ForceLook`。按 skill §0.6 第 3 条
        // （把高风险动作做成**专用、类型化**的入口，别藏在反射里），游戏侧新增了
        // public `PlayerMotor.ForceLookForTest(yaw, pitch)`（语义与 ForceLook 逐字一致），本驱动改用它。
        private Cs16.Module.Player.PlayerMotor _motor;
        private string _lastLook;

        private void ApplyLook()
        {
            var spec = Get("look");
            if (spec == null || spec == _lastLook) return;
            _lastLook = spec;
            if (_motor == null)
            {
                _motor = UnityEngine.Object.FindAnyObjectByType<Cs16.Module.Player.PlayerMotor>(
                    FindObjectsInactive.Include);
            }
            if (_motor == null) { AppendLine("look.fail", "PlayerMotor 取不到"); return; }
            var p = spec.Split(',');
            var yaw = F(p[0], 0f);
            var pitch = p.Length > 1 ? F(p[1], 0f) : 0f;
            _motor.ForceLookForTest(yaw, pitch);
            AppendLine("look", "ForceLookForTest yaw=" + yaw.ToString("F1", CultureInfo.InvariantCulture) +
                               " pitch=" + pitch.ToString("F1", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        //  摆到包点（tpbs=A|B）—— 下包取证的可复现落点
        // ------------------------------------------------------------------
        // 为什么要它：下包要求"站在 A/B 包点半径内"，而包点坐标是地图标记（`CsMarkers.Bombsite*`
        // 的 13 类标记点之一）⇒ 从地图门面把标记点读出来，而不是在驱动里写死一个坐标
        // （写死 = 换个地图/标记调整就静默失效）。地图门面与 CsBomb.IsInBombsite 同一取法。
        private string _lastTpbs;

        private void ApplyTpBombsite(ICsMatch match)
        {
            var spec = Get("tpbs");
            if (spec == null || spec == _lastTpbs) return;
            _lastTpbs = spec;

            var mm = MatchModule.Instance;
            // 【切片R 起：本处已无反射】MatchModule.Map 本来就是 public 属性 ⇒ 直接读。
            var map = mm != null ? mm.Map : null;
            if (map == null || !map.IsLoaded) { AppendLine("tpbs.fail", "地图门面拿不到 / 未加载"); return; }

            var marker = spec == "B" ? Cs16.Module.Map.CsMarkers.BombsiteB : Cs16.Module.Map.CsMarkers.BombsiteA;
            var pts = map.Points(marker);
            if (pts == null || pts.Length == 0) { AppendLine("tpbs.fail", "标记点为空：" + marker); return; }

            var lp = match.LocalPlayer;
            if (lp == null) { AppendLine("tpbs.fail", "本地玩家拿不到"); return; }

            lp.Position = new Vector3(pts[0].x, pts[0].y + 0.6f, pts[0].z);
            lp.Velocity = Vector3.zero;
            lp.OnGround = false;
            AppendLine("tpbs", marker + " 标记点[0] -> local=(" +
                lp.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                lp.Position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                lp.Position.z.ToString("F2", CultureInfo.InvariantCulture) + ")");
        }

        // ------------------------------------------------------------------
        //  fireheld=1 —— 把「本帧按下了左键」也补进 CombatModule
        // ------------------------------------------------------------------
        // **为什么光有 input=(...,fire=1) 不够**（切片P 实测，一次真实的坑）：
        // 下发的 <c>cmd.Fire</c> 只让**模拟**开火（扣弹、推 NextFireTime、给 bot 一条枪声记录），
        // 而 <c>CombatModule</c> 要不要**射线/枪口火焰/弹痕**，取决于它自己那个
        // `_fireRequested`（`FillInput` 里由真实鼠标置位）—— 见 `IsLocalShot` 的类注释：
        // 这是为了"不拿别人的射击记录去射线（双份伤害）"。驱动没有鼠标层 ⇒ `_fireRequested`
        // 恒 false ⇒ 弹匣照扣、**却一条弹痕都不画**（实测：glock18 20→16 发、控制台 0 条
        // `hitwall.*` / `shot.miss`）。所以弹痕取证必须把这一位也补上。
        //
        // 【切片Q 起：改为**类型化的测试入口**，本处已无反射】
        // 切片P 当时是"反射写组件实例的私有 bool"。按 skill §0.6 第 3 条
        // （把高风险动作做成**专用、类型化的入口**，别藏在自由命令/反射里 —— 否则 harness
        //  拿不到可拦截的钩子），游戏侧新增了 CombatModule.SetFireHeldForTest(bool)
        // （public，注释写明"测试入口，供离线驱动使用"），本驱动改用它。
        // 调用时序不变：FillInput(-200) 先清零 → 本驱动(-150) 每帧置位 → PlayerModule.LateUpdate 消费。
        // ⚠️ 必须**每帧**调（不能只在值变化时调一次）：FillInput 每帧开头都会把这一位清零。
        private CombatModule _combatMod;
        private bool _lastFireHeld;

        private void ApplyFireHeld()
        {
            var held = B(Get("fireheld"));
            // 关着且从没开过 ⇒ 无事可做（不必每帧摸组件）。
            if (!held && !_lastFireHeld) return;
            _lastFireHeld = held;

            if (_combatMod == null)
            {
                var all = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include);
                for (var i = 0; i < all.Length; i++)
                {
                    if (all[i] is CombatModule cm) { _combatMod = cm; break; }
                }
            }
            if (_combatMod == null)
            {
                AppendLine("fireheld.fail", "CombatModule 取不到 ⇒ 本帧不会有枪口火焰与弹痕");
                return;
            }
            _combatMod.SetFireHeldForTest(held);
        }

        private bool _sepPlaced;
        private float _lastSepLog;

        /// <summary>
        /// 角色间推开实测（状态字段 <c>sep=1</c>）：帧 ① 把本地玩家**摆到一个敌对 actor 的原位**，
        /// 其后每 0.2 s 记录两者的水平间距。判据 = 间距被 <c>CsMatch.SeparateFromOtherActors</c>
        /// 推到 ≥ 2×PlayerRadius（= 0.72 m）。
        /// </summary>
        private void ApplySeparation()
        {
            var mod = Module(); if (mod == null) return;
            var match = mod.Match; if (match == null) return;
            if (!B(Get("sep"))) { _sepPlaced = false; return; }

            var local = match.LocalPlayer;
            if (local == null) return;

            CsActor other = null;
            var bestD = float.MaxValue;
            var actors = match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null || a.Id == local.Id || !a.IsAlive || a.Team == local.Team) continue;
                var d = (a.Position - local.Position).sqrMagnitude;
                if (d < bestD) { bestD = d; other = a; }
            }
            if (other == null)
            {
                if (!_sepPlaced) { _sepPlaced = true; AppendLine("sep.fail", "没有其它活着的敌对 actor ⇒ 推开无从判定"); }
                return;
            }

            if (!_sepPlaced)
            {
                _sepPlaced = true;
                local.Position = new Vector3(other.Position.x, other.Position.y, other.Position.z);
                local.Velocity = Vector3.zero;
                AppendLine("sep.place", "local 已摆到 actor " + other.Id + " 的原位 (" +
                    other.Position.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                    other.Position.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                    other.Position.z.ToString("F3", CultureInfo.InvariantCulture) + ")");
            }

            var now = Time.realtimeSinceStartup;
            if (now - _lastSepLog < 0.05f) return;
            _lastSepLog = now;
            var dx = local.Position.x - other.Position.x;
            var dz = local.Position.z - other.Position.z;
            AppendLine("sep",
                "dist=" + Mathf.Sqrt(dx * dx + dz * dz).ToString("F4", CultureInfo.InvariantCulture) +
                " local=(" + local.Position.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                              local.Position.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                              local.Position.z.ToString("F3", CultureInfo.InvariantCulture) + ")" +
                " other=" + other.Id + ",(" + other.Position.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                             other.Position.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                             other.Position.z.ToString("F3", CultureInfo.InvariantCulture) + ")" +
                " minGap=" + (2f * CsConst.PlayerRadius).ToString("F3", CultureInfo.InvariantCulture));
        }

        // ------------------------------------------------------------------
        //  同帧对拍（forceState / forceTime）
        // ------------------------------------------------------------------
        /// <summary>
        /// 把目标角色的 Animator **钉在指定序列的指定时间点**，并把整具骨架的
        /// 局部/模型空间变换写进日志 —— 这是与 <c>tools/probes/render-cs16anim-frame.py</c>
        /// 的 <c>.cs16anim</c> 离线复算值**逐骨骼对拍**的量。
        ///
        /// <para>为什么是"时间"而不是"帧号"：两边都按 <c>t = frame / fps</c> 采样，
        /// 帧率/总帧数一律取 mdl 实测；<c>norm = t / clip.length</c> 里的 <c>clip.length</c>
        /// 取**运行期** clip 的真实长度（不靠离线复算，避免两边对长度口径不一致）。</para>
        /// </summary>
        private void ApplyForce()
        {
            var spec = Get("forceState");
            if (string.IsNullOrEmpty(spec)) return;
            var key = spec + "@" + (Get("forceTime") ?? "0");
            // 每帧都强制（ActorView 会在 LateUpdate 里按权威状态切回去），但只在参数变化时记录一次。
            var dump = key != _lastForceKey;
            _lastForceKey = key;
            ForceOnTarget(dump);
        }

        private string _lastForceKey;

        /// <summary>从 state.txt 读一次字段（供一次性 entry 用；不走 50ms 节流、只管 force*）。</summary>
        internal void LoadForceFromState()
        {
            _kv.Clear();
            string text;
            try { text = File.ReadAllText(Paths.State); }
            catch (Exception) { return; }
            text = text.TrimStart('\uFEFF');
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var k = line.Substring(0, eq).Trim();
                var v = line.Substring(eq + 1).Trim();
                if (k.StartsWith("pose", StringComparison.Ordinal) && k != "poseCount") continue;
                _kv[k] = v;
            }
        }

        internal void ForceOnTarget(bool dump)
        {
            var want = Get("forceState");
            if (string.IsNullOrEmpty(want)) return;
            var t = F(Get("forceTime"), 0f);

            var views = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            var idSpec = Get("forceActor");
            long wantId = idSpec != null ? I(idSpec, -1) : -1;
            var ctrlWant = Get("forceCtrl");
            ActorView v = null;
            var nth = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var c = views[i];
                if (c == null || c.IsLocal || !c.IsShown) continue;
                if (wantId >= 0)
                {
                    if (c.ActorId != wantId) continue;
                }
                else if (ctrlWant != null)
                {
                    var can = c.GetComponent<Animator>();
                    var cc = can != null ? can.runtimeAnimatorController : null;
                    if (cc == null || cc.name != ctrlWant) continue;
                }
                else if (nth++ > 0) continue;
                v = c;
                break;
            }
            if (v == null) { if (dump) AppendLine("force.fail", "no matching non-local ActorView (wantId=" + wantId + " ctrl=" + ctrlWant + ")"); return; }

            var an = v.GetComponent<Animator>();
            if (an == null) { if (dump) AppendLine("force.fail", "no Animator on " + v.name); return; }
            var ctrl = an.runtimeAnimatorController;
            if (ctrl == null) { if (dump) AppendLine("force.fail", "no controller"); return; }

            // 状态名 -> clip（clip 资产名是 {模型key}_{序列名}，state 名才是去前缀序列名）
            AnimationClip clip = null;
            var names = "";
            var clips = ctrl.animationClips;
            for (var i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null) continue;
                if (names.Length < 400) names += clips[i].name + ",";
                var n = clips[i].name;
                if (n == want || n.EndsWith("_" + want, StringComparison.Ordinal)) clip = clips[i];
            }
            if (clip == null)
            {
                if (dump) AppendLine("force.fail", "clip not found for state " + want + " (clips=" + names + ")");
                return;
            }

            var has = an.HasState(0, Animator.StringToHash(want));
            var len = clip.length;
            var norm = len > 0.0001f ? t / len : 0f;

            an.speed = 0f;                                   // 冻结：不让它在两次读数之间往前走
            an.Play(Animator.StringToHash(want), 0, norm);
            an.Update(0f);                                   // 立刻求值（不等下一帧）

            if (!dump) return;

            var si = an.GetCurrentAnimatorStateInfo(0);
            var ci = an.GetCurrentAnimatorClipInfo(0);
            var liveClip = (ci != null && ci.Length > 0 && ci[0].clip != null) ? ci[0].clip.name : "?";
            var body = v.transform.Find(CsViewTuning.BodyNodeName);
            AppendLine("force",
                "actorId=" + v.ActorId + " team=" + v.Team + " ctrl=" + ctrl.name +
                " state=" + want + " hasState=" + has + " clipAsset=" + clip.name +
                " clipLen=" + len.ToString("F5", CultureInfo.InvariantCulture) +
                " clipFps=" + clip.frameRate.ToString("F3", CultureInfo.InvariantCulture) +
                " wantTime=" + t.ToString("F6", CultureInfo.InvariantCulture) +
                " norm=" + norm.ToString("F6", CultureInfo.InvariantCulture) +
                " liveClip=" + liveClip +
                " liveNorm=" + si.normalizedTime.ToString("F5", CultureInfo.InvariantCulture) +
                " bodyScale=" + (body != null ? body.localScale.y.ToString("F6", CultureInfo.InvariantCulture) : "-") +
                " bodyPosY=" + (body != null ? body.localPosition.y.ToString("F6", CultureInfo.InvariantCulture) : "-") +
                " rootScale=" + v.transform.localScale.x.ToString("F6", CultureInfo.InvariantCulture) +
                " clipCount=" + clips.Length);

            if (body == null) return;
            var skel = body.Find(CsViewTuning.SkeletonNodeName);
            if (skel == null) { AppendLine("force.fail", "no Skeleton under Body"); return; }
            var all = skel.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                var tr = all[i];
                if (tr == skel) continue;
                var lp = tr.localPosition;
                var lr = tr.localRotation;
                var wp = body.InverseTransformPoint(tr.position);     // 模型空间（= Body 的局部空间）
                AppendLine("force.bone",
                    "state=" + want + " name=" + tr.name +
                    " parent=" + (tr.parent != null ? tr.parent.name : "-") +
                    " lp=" + lp.x.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             lp.y.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             lp.z.ToString("F6", CultureInfo.InvariantCulture) +
                    " lr=" + lr.x.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             lr.y.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             lr.z.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             lr.w.ToString("F6", CultureInfo.InvariantCulture) +
                    " mp=" + wp.x.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             wp.y.ToString("F6", CultureInfo.InvariantCulture) + "," +
                             wp.z.ToString("F6", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>跑完整张对拍表（同步；一次调用出全部数据）。返回采样点数。</summary>
        internal int RunSweep()
        {
            var n = 0;
            for (var i = 0; i < Sweep.GetLength(0); i++)
            {
                _kv["forceState"] = Sweep[i, 0];
                _kv["forceTime"] = Sweep[i, 1];
                _lastForceKey = null;
                AppendLine("force.sweep", "#" + i + " state=" + Sweep[i, 0] + " t=" + Sweep[i, 1]);
                ForceOnTarget(true);
                n++;
            }
            _kv.Remove("forceState");
            _kv.Remove("forceTime");
            // 恢复动画速度：否则整场角色冻在最后一格（下一帧 ActorView 会把状态切回去）
            var views = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            for (var i = 0; i < views.Length; i++)
            {
                var an = views[i] != null ? views[i].GetComponent<Animator>() : null;
                if (an != null) an.speed = 1f;
            }
            AppendLine("force.sweep.done", "poses=" + n);
            return n;
        }

        /// <summary>同帧对拍列表：与离线 <c>.cs16anim</c> 采样点一一对应。</summary>
        internal static readonly string[,] Sweep = new string[,]
        {
            // state            时间 t(s)           备注
            { "idle1",          "0.0000" },       // 15fps 第 0 帧
            { "idle1",          "2.0000" },       // 15fps 第 30 帧
            { "walk",           "0.0000" },       // 30fps 第 0 帧
            { "walk",           "0.5333" },       // 30fps 第 16 帧
            { "run",            "0.0000" },       // 60fps 第 0 帧
            { "run",            "0.2000" },       // 60fps 第 12 帧
            { "crouch_idle",    "0.0000" },       // 10fps 第 0 帧
            { "crouch_idle",    "1.5000" },       // 10fps 第 15 帧
            { "crouchrun",      "0.0000" },       // 30fps 第 0 帧
            { "crouchrun",      "0.5000" },       // 30fps 第 15 帧
            { "jump",           "0.0000" },       // 36fps 第 0 帧
            { "jump",           "0.8333" },       // 36fps 第 30 帧
            { "death1",         "0.0000" },
            { "death1",         "0.6667" },       // 30fps 第 20 帧
            { "death2",         "0.3333" },       // 30fps 第 10 帧
            { "death3",         "0.3333" },
            { "ref_shoot_onehanded", "0.2000" },  // 15fps 第 3 帧
            { "ref_shoot_ak47", "0.0667" },       // 30fps 第 2 帧
            { "ref_shoot_rifle","0.1500" },       // 20fps 第 3 帧
            { "ref_reload_rifle","1.0000" },      // 30fps 第 30 帧
            { "ref_shoot_mp5",  "0.1333" },       // 15fps 第 2 帧
        };

        // ------------------------------------------------------------------
        /// <summary>
        /// 按名字找驱动自己建的常驻 GameObject（<c>Cs16Driver</c> / <c>Cs16DriverTmp</c> / <c>Cs16ProbeCam</c>）。
        ///
        /// <para>⛔ 不用 <c>GameObject.Find</c>（SKILL §8 硬规则）：<c>FindObjectsByType&lt;Transform&gt;</c> 能同时看到
        /// <c>DontDestroyOnLoad</c> 场景里的对象（<c>GameObject.Find</c> 的可见范围是它的超集），
        /// 且不依赖名字索引。只按**根对象**匹配，避免命中同名的子节点。</para>
        /// </summary>
        internal static GameObject Go(string name)
        {
            var all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include);
            for (var i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t != null && t.parent == null && t.name == name) return t.gameObject;
            }
            return null;
        }

        internal static void AppendLine(string tag, string msg)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(Time.frameCount).Append('\t').Append(tag).Append('\t').Append(msg);
                if (!File.Exists(Paths.Log)) File.AppendAllText(Paths.Log, "time\tframe\ttag\tmsg\n");
                File.AppendAllText(Paths.Log, sb.Append('\n').ToString());
            }
            catch (Exception) { }
        }

        internal void Dump(string tag, string msg) => AppendLine(tag, msg);

        /// <summary>把所有 AudioSource 里的"正在播 + 有 clip"的挑出来写日志（点击音一帧内查）。</summary>
        internal static void DumpAudioNow(string tag)
        {
            var all = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            var live = 0;
            for (var i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null || a.clip == null) continue;
                live++;
                AppendLine(tag, "go=" + a.gameObject.name + " clip=" + a.clip.name +
                                " playing=" + a.isPlaying + " loop=" + a.loop +
                                " vol=" + a.volume.ToString("F2", CultureInfo.InvariantCulture) +
                                " time=" + a.time.ToString("F3", CultureInfo.InvariantCulture));
            }
            AppendLine(tag + ".summary", "sourcesWithClip=" + live + " of " + all.Length);
        }

        /// <summary>点一个 UI 按钮（Go 名精确匹配；走 EventSystem 指针事件 = 与真鼠标点击同一条链）。</summary>
        private static void DoClick(string goName)
        {
            var all = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null || b.name != goName || !b.gameObject.activeInHierarchy) continue;
                var es = UnityEngine.EventSystems.EventSystem.current;
                var data = new UnityEngine.EventSystems.PointerEventData(es)
                {
                    button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                    position = b.transform.position,
                };
                UnityEngine.EventSystems.ExecuteEvents.Execute(b.gameObject, data,
                    UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                AppendLine("click", "clicked button " + goName + " interactable=" + b.IsInteractable() +
                                    " active=" + b.gameObject.activeInHierarchy);
                DumpAudioNow("click.audio");     // ★ 同一帧立刻查音频源（点击音很短，过后就查不到了）
                return;
            }
            AppendLine("click.fail", "button NOT FOUND or inactive: " + goName + " (buttons=" + all.Length + ")");
        }

        /// <summary>按 GameObject 名找一个**当前激活**的 Button（与 DoClick 同一套匹配口径）。</summary>
        private static UnityEngine.UI.Button FindButton(string goName)
        {
            var all = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b != null && b.name == goName && b.gameObject.activeInHierarchy) return b;
            }
            return null;
        }

        /// <summary>
        /// 悬停 / 取消悬停一个 UI 按钮（<c>hover=</c> 字段）。
        /// 空值或 <c>-</c> = 对上一次悬停的按钮发 pointerExit。
        /// 走的是 EventSystem 的指针事件（与真鼠标同一条链）⇒ Selectable 真的进 Highlighted。
        /// </summary>
        private void DoHover(string goName)
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (_hovered != null)
            {
                var prev = _hovered;
                var outData = new UnityEngine.EventSystems.PointerEventData(es)
                {
                    button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                    position = prev.transform.position,
                };
                UnityEngine.EventSystems.ExecuteEvents.Execute(prev.gameObject, outData,
                    UnityEngine.EventSystems.ExecuteEvents.pointerExitHandler);
                AppendLine("hover", "exit " + prev.name);
                _hovered = null;
            }
            if (goName == null || goName.Length == 0 || goName == "-") return;

            var b = FindButton(goName);
            if (b == null) { AppendLine("hover.fail", "button NOT FOUND or inactive: " + goName); return; }
            var data = new UnityEngine.EventSystems.PointerEventData(es)
            {
                button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                position = b.transform.position,
            };
            UnityEngine.EventSystems.ExecuteEvents.Execute(b.gameObject, data,
                UnityEngine.EventSystems.ExecuteEvents.pointerEnterHandler);
            // 注意：**不要**再 SetSelectedGameObject —— 那会把状态从 Highlighted 改成 Selected，
            // 而 Selected 的 tint 取 ColorBlock.selectedColor（本项目没设）⇒ 实测悬停色不出现（本片第一轮踩到）。
            _hovered = b;
            AppendLine("hover", "enter " + goName + " interactable=" + b.IsInteractable());
        }

        /// <summary>把当前游戏画面存成 PNG（<c>shot=</c> / <c>burst=</c> 字段）。</summary>
        private static void Shot(string name)
        {
            try
            {
                if (!Directory.Exists(Paths.Shots)) Directory.CreateDirectory(Paths.Shots);
                var p = Path.Combine(Paths.Shots, name + ".png");
                UnityEngine.ScreenCapture.CaptureScreenshot(p);
                AppendLine("shot", p);
            }
            catch (Exception e) { AppendLine("shot.fail", name + " :: " + e.Message); }
        }

        internal void Sample(string tag)
        {
            var mod = Module();
            if (mod == null) { AppendLine(tag, "matchModule=null"); return; }
            var match = mod.Match;
            if (match == null) { AppendLine(tag, "match=null"); return; }
            AppendLine(tag, "running=" + match.IsRunning + " paused=" + match.IsPaused +
                            " phase=" + match.Phase + " round=" + match.RoundNumber);
            var actors = match.Actors;
            for (var i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                if (a == null) continue;
                var av = FindView(a.Id);
                var anim = "";
                if (av != null)
                {
                    var an = av.GetComponent<Animator>();
                    if (an != null)
                    {
                        var si = an.GetCurrentAnimatorStateInfo(0);
                        var ci = an.GetCurrentAnimatorClipInfo(0);
                        var clip = (ci != null && ci.Length > 0 && ci[0].clip != null) ? ci[0].clip.name : "?";
                        anim = clip + "@" + si.normalizedTime.ToString("F2", CultureInfo.InvariantCulture) +
                               " w=" + si.speed.ToString("F2", CultureInfo.InvariantCulture);
                    }
                    else anim = "<noAnimator>";
                }
                else anim = "<noView>";

                AppendLine(tag + ".actor",
                    "id=" + a.Id + " bot=" + a.IsBot + " team=" + a.Team + " alive=" + a.IsAlive +
                    " pos=" + a.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                              a.Position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                              a.Position.z.ToString("F2", CultureInfo.InvariantCulture) +
                    " vel=" + a.Velocity.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                              a.Velocity.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                              a.Velocity.z.ToString("F2", CultureInfo.InvariantCulture) +
                    " ground=" + a.OnGround + " crouch=" + a.IsCrouching + " walk=" + a.IsWalking +
                    " hp=" + a.Health + " anim=" + anim);
            }
        }

        /// <summary>
        /// 矩形区域逐点向下打射线（Unity 物理 = 运行时真源）。
        ///
        /// <para><b>为什么要它</b>：离线 oracle（<c>tools/probes/geom-check.py</c>）只看几何三角面的法线，
        /// 而 Unity 的 <c>Physics.Raycast</c> 默认 <c>queriesHitBackfaces=false</c> ⇒ **朝下的顶板打不中**。
        /// 两者对同一格给出不同答案时，必须由运行时说话（这正是"运行期 × 离线"这一对交叉项的意义）。
        /// 所以这里对每个采样点**打两遍**：`front`（默认）与 `both`（<c>queriesHitBackfaces=true</c>），
        /// 两面结果不一致 = 该处有"背面朝上"的面（内翻/空心）。</para>
        ///
        /// <para>状态字段：<c>rect=x0,z0,x1,z1,step[,fromY[,maxDist]]</c></para>
        /// </summary>
        internal string RunRectProbe()
        {
            var spec = Get("rect");
            if (spec == null) { AppendLine("rect.fail", "state.txt 缺 rect=x0,z0,x1,z1,step[,fromY[,maxDist]]"); return "no rect"; }
            var p = spec.Split(',');
            if (p.Length < 5) { AppendLine("rect.fail", "rect 至少需要 x0,z0,x1,z1,step"); return "bad rect"; }
            var x0 = F(p[0], 0f); var z0 = F(p[1], 0f);
            var x1 = F(p[2], 0f); var z1 = F(p[3], 0f);
            var st = F(p[4], 0.5f);
            var fromY = p.Length > 5 ? F(p[5], 60f) : 60f;
            var maxD = p.Length > 6 ? F(p[6], 160f) : 160f;
            if (st <= 0.01f) st = 0.5f;

            AppendLine("rect.header",
                "rect=" + spec + " fromY=" + fromY.ToString("F2", CultureInfo.InvariantCulture) +
                " maxDist=" + maxD.ToString("F1", CultureInfo.InvariantCulture));

            var prevBack = Physics.queriesHitBackfaces;
            var n = 0; var hitF = 0; var hitB = 0; var diff = 0;
            for (var x = x0; x <= x1 + 1e-4f; x += st)
            {
                for (var z = z0; z <= z1 + 1e-4f; z += st)
                {
                    n++;
                    Physics.queriesHitBackfaces = false;
                    var okF = Physics.Raycast(new Vector3(x, fromY, z), Vector3.down, out var hf, maxD, ~0, QueryTriggerInteraction.Ignore);
                    Physics.queriesHitBackfaces = true;
                    var okB = Physics.Raycast(new Vector3(x, fromY, z), Vector3.down, out var hb, maxD, ~0, QueryTriggerInteraction.Ignore);
                    Physics.queriesHitBackfaces = prevBack;

                    if (okF) hitF++;
                    if (okB) hitB++;
                    var yf = okF ? hf.point.y : float.NaN;
                    var yb = okB ? hb.point.y : float.NaN;
                    var flipped = okF && okB && Mathf.Abs(yf - yb) > 0.01f;
                    if (flipped) diff++;
                    AppendLine("rect",
                        "x=" + x.ToString("F2", CultureInfo.InvariantCulture) +
                        " z=" + z.ToString("F2", CultureInfo.InvariantCulture) +
                        " front=" + (okF ? (yf.ToString("F3", CultureInfo.InvariantCulture) + "/" + hf.collider.gameObject.name) : "-") +
                        " both=" + (okB ? (yb.ToString("F3", CultureInfo.InvariantCulture) + "/" + hb.collider.gameObject.name) : "-") +
                        (flipped ? " FLIPPED" : ""));
                }
            }
            AppendLine("rect.summary",
                "points=" + n + " hitFront=" + hitF + " hitBoth=" + hitB + " flipped=" + diff);
            return "rect points=" + n + " flipped=" + diff;
        }

        /// <summary>横向射线（判"这一格能不能走过去"的水平阻挡；与 <c>CsMap</c> 的下探不同口径，仅作旁证）。</summary>
        internal string RunRayProbe()
        {
            var spec = Get("ray");
            if (spec == null) { AppendLine("ray.fail", "state.txt 缺 ray=x0,y0,z0,x1,y1,z1[,len]"); return "no ray"; }
            var p = spec.Split(',');
            if (p.Length < 6) { AppendLine("ray.fail", "ray 需要 x0,y0,z0,x1,y1,z1[,maxDist]"); return "bad ray"; }
            var from = new Vector3(F(p[0], 0f), F(p[1], 0f), F(p[2], 0f));
            var to = new Vector3(F(p[3], 0f), F(p[4], 0f), F(p[5], 0f));
            var dir = to - from;
            var len = p.Length > 6 ? F(p[6], 60f) : 60f;
            var ok = Physics.Raycast(from, dir.normalized, out var hit, len, ~0, QueryTriggerInteraction.Ignore);
            AppendLine("ray",
                "from=" + from.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                           from.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                           from.z.ToString("F2", CultureInfo.InvariantCulture) +
                " dir=" + dir.normalized.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          dir.normalized.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          dir.normalized.z.ToString("F3", CultureInfo.InvariantCulture) +
                " hit=" + ok +
                (ok ? (" dist=" + hit.distance.ToString("F3", CultureInfo.InvariantCulture) +
                       " y=" + hit.point.y.ToString("F3", CultureInfo.InvariantCulture) +
                       " normalY=" + hit.normal.y.ToString("F3", CultureInfo.InvariantCulture) +
                       " go=" + hit.collider.gameObject.name)
                    : ""));
            return "ray hit=" + ok;
        }

        private static ActorView FindView(long actorId)
        {
            var all = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].ActorId == actorId) return all[i];
            }
            return null;
        }

        /// <summary>把所有人的名字牌/血条可见性也报出来（表现类证据）。</summary>
        internal void SampleViews(string tag)
        {
            var all = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            for (var i = 0; i < all.Length; i++)
            {
                var v = all[i];
                if (v == null) continue;
                var an = v.GetComponent<Animator>();
                var clip = "?";
                var norm = 0f;
                var speed = 0f;
                if (an != null)
                {
                    var si = an.GetCurrentAnimatorStateInfo(0);
                    var ci = an.GetCurrentAnimatorClipInfo(0);
                    clip = (ci != null && ci.Length > 0 && ci[0].clip != null) ? ci[0].clip.name : "?";
                    norm = si.normalizedTime;
                    speed = si.speed;
                }
                var smrCount = 0;
                var enabledRenderers = 0;
                var rs = v.GetComponentsInChildren<Renderer>(true);
                for (var r = 0; r < rs.Length; r++)
                {
                    if (rs[r] is SkinnedMeshRenderer) smrCount++;
                    if (rs[r] != null && rs[r].enabled) enabledRenderers++;
                }
                AppendLine(tag + ".view",
                    "id=" + v.ActorId + " shown=" + v.IsShown + " local=" + v.IsLocal +
                    " proxies=" + v.Proxies.Length + " smr=" + smrCount + " renderersOn=" + enabledRenderers +
                    " animator=" + (an != null) + " ctrl=" + (an != null && an.runtimeAnimatorController != null) +
                    " ctrlName=" + (an != null && an.runtimeAnimatorController != null ? an.runtimeAnimatorController.name : "-") +
                    " speed=" + (an != null ? an.speed.ToString("F2", CultureInfo.InvariantCulture) : "-") +
                    " clip=" + clip + "@" + norm.ToString("F3", CultureInfo.InvariantCulture) +
                    " stateSpeed=" + speed.ToString("F3", CultureInfo.InvariantCulture) +
                    " rootScale=" + v.transform.localScale.x.ToString("F3", CultureInfo.InvariantCulture) +
                    " bodyScale=" + (v.transform.Find("Body") != null
                        ? v.transform.Find("Body").localScale.y.ToString("F4", CultureInfo.InvariantCulture) : "-"));
            }
        }
    }

    public static class Entry
    {
        /// <summary>挂载驱动（幂等）。返回一句结果。</summary>
        /// <remarks>
        /// ⚠️ 必须**按类型名**找现存组件，不能用 <c>GetComponent&lt;Cs16Driver&gt;()</c>：
        /// run_script 每次调用都编译出一个**新程序集**，同名类型是**不同的 Type 对象**，
        /// 于是 GetComponent 会看不见上一次挂上去的那个实例（实测：Snap 报 "driver not mounted"
        /// 而同一实例的心跳还在涨）。跨程序集一律走反射。
        /// </remarks>
        public static string Setup()
        {
            if (FindLive() != null) return "driver already mounted (reused)";
            var go = Cs16Driver.Go("Cs16Driver");
            if (go == null)
            {
                go = new GameObject("Cs16Driver");
                UnityEngine.Object.DontDestroyOnLoad(go);
            }
            go.AddComponent<Cs16Driver>();
            return "driver mounted";
        }

        /// <summary>按**类型名**取当前活着的驱动组件（跨临时程序集唯一可靠的办法）。</summary>
        private static Component FindLive()
        {
            var go = Cs16Driver.Go("Cs16Driver");
            if (go == null) return null;
            var comps = go.GetComponents<Component>();
            for (var i = 0; i < comps.Length; i++)
            {
                if (comps[i] != null && comps[i].GetType().Name == "Cs16Driver") return comps[i];
            }
            return null;
        }

        /// <summary>强制重挂（改了驱动源码后用；会把旧实例干掉再建一个新的）。</summary>
        public static string Remount()
        {
            var go = Cs16Driver.Go("Cs16Driver");
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
            var ngo = new GameObject("Cs16Driver");
            UnityEngine.Object.DontDestroyOnLoad(ngo);
            ngo.AddComponent<Cs16Driver>();
            return "driver remounted";
        }

        /// <summary>
        /// 贴地采样探针：对一组点，分别从"玩家当前高度"与"高空"向下取地面，
        /// 用来判定"玩家 y 是否真的跟随真实几何"（CsMap.TrySampleGround 走 Physics.Raycast）。
        /// </summary>
        /// <summary>矩形区域向下打射线（状态字段 <c>rect=</c>）—— 运行时地面/顶面真源，见 RunRectProbe 注释。</summary>
        public static string ProbeRect() { var d = Temp(); d.LoadForceFromState(); return d.RunRectProbe(); }

        /// <summary>单根横向射线（状态字段 <c>ray=</c>）。</summary>
        public static string ProbeRay() { var d = Temp(); d.LoadForceFromState(); return d.RunRayProbe(); }

        public static string ProbeGround()
        {
            Cs16.Module.Map.ICsMap map = null;
            var mm = Cs16.Module.Match.MatchModule.Instance;
            // 【切片R 起：本处已无反射】MatchModule.Map 本来就是 public 属性 ⇒ 直接读。
            if (mm != null) map = mm.Map;
            var cm = UnityEngine.Object.FindAnyObjectByType<Cs16.Module.Map.CsMapModule>();
            if (map == null && cm != null) map = cm.Map;
            Cs16Driver.AppendLine("ground", "mapFacade=" + (map != null) + " matchModule=" + (mm != null) +
                                            " csMapModule=" + (cm != null));

            var xs = new[] { 17.3f, -8.7f, 2.5f, -30.0f, 39.0f, -29.7f, 6.0f, -41.0f };
            var zs = new[] { 29.4f, -48.8f, -11.0f, -24.0f, 34.5f, 39.3f, 8.0f, 4.0f };
            var names = new[] { "CTspawn", "Tspawn", "midS", "Bcorridor", "Asite", "Bsite", "middoor", "BtunnelW" };

            // ① 纯物理：从高空与"玩家高度"各打一条向下的射线（不依赖地图门面）
            Cs16Driver.AppendLine("ground", "== Physics.Raycast（layerMask=~0, Ignore triggers）==");
            var names2 = names;
            for (var i = 0; i < xs.Length; i++)
            {
                RaycastReport(names2[i], xs[i], zs[i], 20f, 80f, "high");
                RaycastReport(names2[i], xs[i], zs[i], -3.25f, 8f, "playerY");
            }

            // ② 地图门面（地面高度真源）
            Cs16Driver.AppendLine("ground", "== ICsMap.TrySampleGround ==");
            if (map != null)
            {
                for (var i = 0; i < xs.Length; i++)
                {
                    ProbeOne(map, names[i], xs[i], zs[i], -3.25f);
                    ProbeOne(map, names[i], xs[i], zs[i], 20f);
                }
            }
            else Cs16Driver.AppendLine("ground", "map=null（门面拿不到，只有物理射线结论）");
            return "probed " + xs.Length + " points";
        }

        private static void RaycastReport(string name, float x, float z, float fromY, float maxDist, string tag)
        {
            var origin = new Vector3(x, fromY, z);
            var ok = Physics.Raycast(origin, Vector3.down, out var hit, maxDist, ~0, QueryTriggerInteraction.Ignore);
            Cs16Driver.AppendLine("ground",
                tag + " " + name + " fromY=" + fromY.ToString("F2", CultureInfo.InvariantCulture) +
                " hit=" + ok +
                (ok ? (" hitY=" + hit.point.y.ToString("F2", CultureInfo.InvariantCulture) +
                       " normalY=" + hit.normal.y.ToString("F3", CultureInfo.InvariantCulture) +
                       " collider=" + hit.collider.gameObject.name +
                       " layer=" + LayerMask.LayerToName(hit.collider.gameObject.layer))
                    : " (no hit)"));
        }

        private static void ProbeOne(Cs16.Module.Map.ICsMap map, string name, float x, float z, float fromY)
        {
            var p = new Vector3(x, fromY, z);
            Vector3 pt, nrm;
            var ok = map.TrySampleGround(p, out pt, out nrm);
            Cs16Driver.AppendLine("ground",
                name + " (" + x.ToString("F1", CultureInfo.InvariantCulture) + "," + z.ToString("F1", CultureInfo.InvariantCulture) + ")" +
                " fromY=" + fromY.ToString("F2", CultureInfo.InvariantCulture) +
                " hit=" + ok +
                " groundY=" + (ok ? pt.y.ToString("F2", CultureInfo.InvariantCulture) : "-") +
                " normalY=" + (ok ? nrm.y.ToString("F3", CultureInfo.InvariantCulture) : "-") +
                " mapLoaded=" + map.IsLoaded);
        }

        /// <summary>
        /// 角色模型逐子网格探针：每个 SkinnedMeshRenderer 的 mesh 顶点/三角数、包围盒（判"头部有没有几何"）、
        /// 材质名与 _Mode（判 cutout）、贴图尺寸与是否有 alpha。
        /// </summary>
        public static string ProbeModel()
        {
            var views = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            var done = 0;
            for (var i = 0; i < views.Length && done < 3; i++)
            {
                var v = views[i];
                if (v == null || v.IsLocal) continue;
                done++;
                Cs16Driver.AppendLine("model", "=== ActorView id=" + v.ActorId + " go=" + v.name + " ===");
                var smrs = v.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                for (var s = 0; s < smrs.Length; s++)
                {
                    var r = smrs[s];
                    var mesh = r.sharedMesh;
                    var b = mesh != null ? mesh.bounds : new Bounds();
                    var mat = r.sharedMaterial;
                    var tex = mat != null ? mat.mainTexture as Texture2D : null;
                    var mode = mat != null ? mat.GetFloat("_Mode") : -1f;
                    var cutoff = mat != null ? mat.GetFloat("_Cutoff") : -1f;
                    Cs16Driver.AppendLine("model",
                        "  node=" + r.gameObject.name +
                        " mesh=" + (mesh != null ? mesh.name : "<null>") +
                        " verts=" + (mesh != null ? mesh.vertexCount : -1) +
                        " tris=" + (mesh != null ? mesh.triangles.Length / 3 : -1) +
                        " boundsY=[" + b.min.y.ToString("F3", CultureInfo.InvariantCulture) + ".." +
                                          b.max.y.ToString("F3", CultureInfo.InvariantCulture) + "]" +
                        " boundsX=[" + b.min.x.ToString("F2", CultureInfo.InvariantCulture) + ".." +
                                          b.max.x.ToString("F2", CultureInfo.InvariantCulture) + "]" +
                        " mat=" + (mat != null ? mat.name : "<null>") +
                        " mode=" + mode.ToString("F0", CultureInfo.InvariantCulture) +
                        " cutoff=" + cutoff.ToString("F2", CultureInfo.InvariantCulture) +
                        " tex=" + (tex != null ? tex.name + " " + tex.width + "x" + tex.height + " fmt=" + tex.format
                                                + " alpha=" + tex.alphaIsTransparency : "<null>") +
                        " enabled=" + r.enabled + " active=" + r.gameObject.activeInHierarchy);
                }
                // 骨骼里头部那几根
                var bones = v.GetComponentsInChildren<Transform>(true);
                var headChain = "";
                for (var k = 0; k < bones.Length; k++)
                {
                    var n = bones[k].name;
                    if (n.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Neck", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        headChain += n + "(" + bones[k].position.y.ToString("F2", CultureInfo.InvariantCulture) + ") ";
                    }
                }
                Cs16Driver.AppendLine("model", "  headBones=" + (headChain.Length > 0 ? headChain : "<none>"));
            }
            return "probed " + done + " actor views";
        }

        /// <summary>每帧把取证相机摆到目标角色正前方（供 cam= 状态字段用）。</summary>
        /// <summary>固定机位取证相机：cam 字段给 7 个值时 = camX,camY,camZ,lookX,lookY,lookZ,fov。</summary>
        internal static void UpdateProbeCamFixed(float cx, float cy, float cz, float lx, float ly, float lz, float fov)
        {
            var go = Cs16Driver.Go("Cs16ProbeCam");
            if (go == null) go = new GameObject("Cs16ProbeCam");
            if (!go.activeSelf) go.SetActive(true);
            var cam = go.GetComponent<Camera>();
            if (cam == null) cam = go.AddComponent<Camera>();
            cam.depth = 100f;
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);
            go.transform.position = new Vector3(cx, cy, cz);
            go.transform.LookAt(new Vector3(lx, ly, lz));
        }

        internal static void UpdateProbeCam(float dist, float height, int idx)
        {
            var views = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            ActorView target = null;
            var n = 0;
            for (var i = 0; i < views.Length; i++)
            {
                if (views[i] == null || views[i].IsLocal || !views[i].IsShown) continue;
                if (n == idx) { target = views[i]; break; }
                n++;
            }
            var go = Cs16Driver.Go("Cs16ProbeCam");
            if (go == null) go = new GameObject("Cs16ProbeCam");
            if (!go.activeSelf) go.SetActive(true);
            var cam = go.GetComponent<Camera>();
            if (cam == null) cam = go.AddComponent<Camera>();
            cam.depth = 100f;
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);

            if (target == null) return;
            var t = target.transform;
            var center = t.position + Vector3.up * 0.95f;
            go.transform.position = new Vector3(center.x, t.position.y + height, center.z - dist);
            go.transform.LookAt(center);
        }

        /// <summary>
        /// 建/更新一台**取证相机** Cs16ProbeCam：正对第 idx 个非本地 ActorView，
        /// 距离 dist、高度 height、俯角朝模型中心。depth=100 ⇒ 覆盖 FPS 相机。
        /// 之后用 capture_game_view --camera Cs16ProbeCam --source camera 取图（不含 HUD）。
        /// </summary>
        public static string ProbeCam(float dist, float height, int idx)
        {
            var views = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            ActorView target = null;
            var n = 0;
            for (var i = 0; i < views.Length; i++)
            {
                if (views[i] == null || views[i].IsLocal) continue;
                if (n == idx) { target = views[i]; break; }
                n++;
            }
            if (target == null) return "ERROR: no non-local ActorView #" + idx;

            var go = Cs16Driver.Go("Cs16ProbeCam");
            if (go == null) go = new GameObject("Cs16ProbeCam");
            var cam = go.GetComponent<Camera>();
            if (cam == null) cam = go.AddComponent<Camera>();
            cam.depth = 100f;
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f, 1f);

            var center = target.transform.position + Vector3.up * 0.95f;
            var pos = new Vector3(center.x, target.transform.position.y + height, center.z - dist);
            go.transform.position = pos;
            go.transform.LookAt(center);
            Cs16Driver.AppendLine("proc", "ProbeCam -> actorId=" + target.ActorId + " dist=" + dist +
                                          " height=" + height + " targetPos=" +
                                          target.transform.position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                          target.transform.position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                                          target.transform.position.z.ToString("F2", CultureInfo.InvariantCulture));
            return "probe cam on actorId=" + target.ActorId;
        }

        // 【切片Q】删掉了旧的 `CallLive(method, arg)` —— 它用**反射**去调本驱动自己的
        // internal 方法（Sample / SampleViews），而它**没有任何调用点**（全文件 0 处引用）。
        // 死代码 + 反射，一并去掉；本驱动要反射自己的方法时改走 public 直调。

        // ---- 零参入口（PowerShell 传 --args 会把 ["x"] 的引号吃掉，所以一律不带参数）----
        // 需要"字符串参数"的（点哪个按钮 / 写什么备注）一律走 state.txt 的 click= / note= 字段。

        /// <summary>开局：4 v 4（用于人物模型姿态舞台）。</summary>
        public static string StartBots() => EmitLaunch(4, CsTeam.CT);

        /// <summary>开局：只打真人、无 bot（用于中门 / B 通 / 坡道 / 开枪的干净走位测试）。</summary>
        public static string StartSolo() => EmitLaunch(0, CsTeam.CT);

        /// <summary>
        /// 开局：4 v 4 且**本地玩家是 T**（切片P 的 C4 取证用）。
        /// 为什么不用 <see cref="StartShortSoloT"/> / <see cref="StartSolo"/>：本工程里
        /// 「一队只有本地玩家」= 该队人数为 0 侧被立刻判定全歼 ⇒ 回合**秒结束**
        /// （切片P 实测：solo T 局在 15:44:54 就已 `phase=RoundEnd round=2 score=CT2:T0` ⇒
        /// 到达 `MatchEnd`、模拟停摆，`SetUseHeld` 因 `!_running` 直接返回 ⇒ 下包永远不发生）。
        /// 所以下包取证必须**两边都有人**（4 v 4）。
        /// </summary>
        public static string StartMatchT() => EmitLaunch(4, CsTeam.T);

        private static string EmitLaunch(int bots, CsTeam team)
        {
            var bus = Game.Event;
            if (bus == null) return "ERROR: Game.Event null";
            var cfg = new CsMatchConfig();
            cfg.MapName = CsConst.MapDust2;
            cfg.BotsPerTeam = bots;
            cfg.BotDifficulty = CsBotDifficulty.Normal;
            cfg.PlayerTeam = team;
            bus.Emit<CsMatchConfig>(Events.LaunchMatch, cfg);
            return "LaunchMatch emitted bots=" + bots + " team=" + team;
        }

        /// <summary>
        /// 发一次暂停请求（= AppFlow 里 ESC 键那条链发的事件）。
        /// ⛔ 不能用 <c>match.SetPaused(true)</c> 代替：那只是把模拟暂停，**不切 FSM**，
        /// 于是 PausePanel 根本不开（切片I 第一轮踩到：pause=1 之后截图里还是在局内）。
        /// </summary>
        public static string PauseRequest()
        {
            var bus = Game.Event;
            if (bus == null) return "ERROR: Game.Event null";
            bus.Emit(Events.RequestPause);
            return "RequestPause emitted";
        }

        /// <summary>选阵营 → 真正进 Stage。</summary>
        public static string ChooseCT() => EmitTeam(CsTeam.CT);

        public static string ChooseT() => EmitTeam(CsTeam.T);

        private static string EmitTeam(CsTeam team)
        {
            var bus = Game.Event;
            if (bus == null) return "ERROR: Game.Event null";
            bus.Emit<CsTeam>(Events.TeamChosen, team);
            return "TeamChosen emitted " + team;
        }

        /// <summary>
        /// 取一个**属于本次编译程序集**的驱动实例（不靠 GameObject.Find + 反射）。
        ///
        /// <para><b>为什么不用 FindLive()</b>（2026-09-20 收尾C 实测）：<c>run_script</c> 每次调用
        /// 都编译出一个新程序集，同名类型是不同 Type；FindLive 走 <c>GetType().Name</c> 匹配虽然
        /// 能跨程序集看见旧实例，但**旧程序集被卸载后**那个组件的类型解析不出来，于是
        /// <c>GetType().Name</c> 不再等于 "Cs16Driver" ⇒ 后续所有 entry 都报
        /// "driver not mounted"，而同实例的心跳还在涨（实测：Snap 之后 SweepAll 就开始报，
        /// 但 driver.heartbeat 又继续了 5 拍）。⇒ 本片改成"每次取个新实例"，
        /// 用 <c>AddComponent</c> 拿本程序集的类型，**不依赖任何跨程序集句柄**。</para>
        /// </summary>
        internal static Cs16Driver Temp()
        {
            var go = Cs16Driver.Go("Cs16DriverTmp");
            if (go == null)
            {
                go = new GameObject("Cs16DriverTmp");
                UnityEngine.Object.DontDestroyOnLoad(go);
            }
            var comps = go.GetComponents<Component>();
            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                if (c.GetType().Name == "Cs16Driver") UnityEngine.Object.DestroyImmediate(c);
            }
            return go.AddComponent<Cs16Driver>();
        }

        /// <summary>把目标角色的 Animator 钉在 state.txt 的 forceState/forceTime 上，并把整具骨架 dump 一次。</summary>
        public static string ForceDump()
        {
            var d = Temp();
            d.LoadForceFromState();
            d.ForceOnTarget(true);
            return "force dump ok";
        }

        /// <summary>同帧对拍全表：**一次调用**把 21 个采样点全部强制 + dump（不逐项进出 Play）。</summary>
        public static string SweepAll()
        {
            var d = Temp();
            d.LoadForceFromState();          // 让 state.txt 里的 forceCtrl / forceActor 生效（目标模型可控）
            return "sweep poses dumped = " + d.RunSweep();
        }

        /// <summary>局面采样（各 actor 位置/状态 + animator）。</summary>
        public static string Snap()
        {
            var d = Temp();
            d.Sample("snap");
            d.SampleViews("snap");
            return "snapped";
        }

        /// <summary>视图侧采样（animator 状态 / 渲染器 / 缩放）。</summary>
        public static string SnapViews()
        {
            Temp().SampleViews("snap");
            return "snapped views";
        }


        /// <summary>把所有 AudioSource 的状态写进日志（BGM / UI 点击音是否真的在播）。</summary>
        public static string ProbeAudio()
        {
            var all = UnityEngine.Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None);
            Cs16Driver.AppendLine("probe", "audioSourceCount=" + all.Length);
            for (var i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (a == null) continue;
                Cs16Driver.AppendLine("probe.src",
                    "go=" + a.gameObject.name + " clip=" + (a.clip != null ? a.clip.name : "<null>") +
                    " playing=" + a.isPlaying + " loop=" + a.loop + " vol=" + a.volume.ToString("F2", CultureInfo.InvariantCulture) +
                    " time=" + a.time.ToString("F2", CultureInfo.InvariantCulture) +
                    " muted=" + a.mute + " spatial=" + a.spatialBlend.ToString("F2", CultureInfo.InvariantCulture));
            }
            return "probed audio " + all.Length;
        }

        /// <summary>把所有打开面板里的 UI 文本逐条写进日志（选角界面文本验收的运行时节点树证据）。</summary>
        public static string DumpTexts()
        {
            var all = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Text>(FindObjectsSortMode.None);
            Cs16Driver.AppendLine("probe", "uiTextCount=" + all.Length);
            for (var i = 0; i < all.Length; i++)
            {
                var t = all[i];
                if (t == null) continue;
                var path = t.gameObject.name;
                var p = t.transform.parent;
                var depth = 0;
                while (p != null && depth < 4) { path = p.name + "/" + path; p = p.parent; depth++; }
                Cs16Driver.AppendLine("probe.text",
                    "path=" + path + " active=" + t.gameObject.activeInHierarchy +
                    " value=" + t.text.Replace("\n", "\\n").Replace("\t", " "));
            }
            return "dumped texts " + all.Length;
        }

        /// <summary>列出当前所有按钮名（找可用名字）。</summary>
        public static string ListButtons()
        {
            var all = UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None);
            Cs16Driver.AppendLine("probe", "buttonCount=" + all.Length);
            for (var i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                var label = "";
                var txt = b.GetComponentInChildren<UnityEngine.UI.Text>();
                if (txt != null) label = txt.text;
                var path = b.gameObject.name;
                var p = b.transform.parent;
                var depth = 0;
                while (p != null && depth < 3) { path = p.name + "/" + path; p = p.parent; depth++; }
                Cs16Driver.AppendLine("probe.btn",
                    "path=" + path + " label=" + label.Replace("\n", "\\n") +
                    " active=" + b.gameObject.activeInHierarchy + " interactable=" + b.IsInteractable());
            }
            return "listed " + all.Length + " buttons";
        }

        /// <summary>
        /// 姿态取证探针（②人物模型）：对每个非本地 ActorView 打印
        /// <list type="bullet">
        /// <item>Animator 当前 state 的 clip 名 / normalizedTime / speed / 权重（判"序列是否真的在播"）；</item>
        /// <item><c>Body</c> 的 localScale / localPosition（判缩放与贴地）；</item>
        /// <item>关键骨骼（头 / 双手 / 双脚 / 肩）在 **Body 局部空间**的世界位置 ——
        ///       这一栏是能直接与 <c>.cs16anim</c> 离线复算值对拍的量（Body 局部 = 模型空间，
        ///       因为 <c>Body</c> 的 localScale 已量过、<c>_modelLift</c> 已算进去）。</item>
        /// </list>
        /// 与 <c>ProbeModel</c>（网格/材质）配套：那个判"贴图绑没绑上"，这个判"姿态对不对"。
        /// </summary>
        public static string ProbePose()
        {
            var views = UnityEngine.Object.FindObjectsByType<ActorView>(FindObjectsSortMode.None);
            var n = 0;
            for (var i = 0; i < views.Length; i++)
            {
                var v = views[i];
                if (v == null || v.IsLocal || !v.IsShown) continue;
                n++;
                var an = v.GetComponent<Animator>();
                var clip = "?";
                var norm = 0f;
                var spd = 0f;
                var w = 0f;
                var stHash = 0;
                if (an != null)
                {
                    var si = an.GetCurrentAnimatorStateInfo(0);
                    var ci = an.GetCurrentAnimatorClipInfo(0);
                    clip = (ci != null && ci.Length > 0 && ci[0].clip != null) ? ci[0].clip.name : "?";
                    norm = si.normalizedTime;
                    spd = si.speed;
                    w = an.GetLayerWeight(0);
                    stHash = si.fullPathHash;
                }
                var body = v.transform.Find("Body");
                Cs16Driver.AppendLine("pose.view",
                    "id=" + v.ActorId + " team=" + v.Team +
                    " viewPos=" + v.transform.position.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                  v.transform.position.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                                  v.transform.position.z.ToString("F3", CultureInfo.InvariantCulture) +
                    " yaw=" + v.transform.eulerAngles.y.ToString("F2", CultureInfo.InvariantCulture) +
                    " animSpeed=" + (an != null ? an.speed.ToString("F2", CultureInfo.InvariantCulture) : "-") +
                    " clip=" + clip + " norm=" + norm.ToString("F4", CultureInfo.InvariantCulture) +
                    " stateSpeed=" + spd.ToString("F3", CultureInfo.InvariantCulture) +
                    " weight=" + w.ToString("F3", CultureInfo.InvariantCulture) +
                    " stateHash=" + stHash +
                    " bodyScale=" + (body != null ? body.localScale.y.ToString("F5", CultureInfo.InvariantCulture) : "-") +
                    " bodyPosY=" + (body != null ? body.localPosition.y.ToString("F5", CultureInfo.InvariantCulture) : "-"));

                if (body == null) continue;
                var all = v.GetComponentsInChildren<Transform>(true);
                for (var k = 0; k < all.Length; k++)
                {
                    var t = all[k];
                    var nm = t.name;
                    if (nm.IndexOf("Bip01", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (nm.IndexOf("Head", StringComparison.OrdinalIgnoreCase) < 0 &&
                        nm.IndexOf("Hand", StringComparison.OrdinalIgnoreCase) < 0 &&
                        nm.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) < 0 &&
                        nm.IndexOf("Spine", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var lp = body.InverseTransformPoint(t.position);
                    Cs16Driver.AppendLine("pose.bone",
                        "id=" + v.ActorId + " name=" + nm +
                        " bodyLocal=" + lp.x.ToString("F4", CultureInfo.InvariantCulture) + "," +
                                           lp.y.ToString("F4", CultureInfo.InvariantCulture) + "," +
                                           lp.z.ToString("F4", CultureInfo.InvariantCulture));
                }
            }
            return "probed pose of " + n + " view(s)";
        }

        /// <summary>报告摄像机（画面证据的机位）。</summary>
        public static string DumpCameras()
        {
            var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (var i = 0; i < cams.Length; i++)
            {
                var c = cams[i];
                if (c == null) continue;
                Cs16Driver.AppendLine("probe.cam",
                    "go=" + c.gameObject.name + " active=" + c.gameObject.activeInHierarchy +
                    " enabled=" + c.enabled + " pos=" + c.transform.position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                              c.transform.position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                              c.transform.position.z.ToString("F2", CultureInfo.InvariantCulture) +
                    " rot=" + c.transform.eulerAngles.x.ToString("F1", CultureInfo.InvariantCulture) + "," +
                              c.transform.eulerAngles.y.ToString("F1", CultureInfo.InvariantCulture) + "," +
                              c.transform.eulerAngles.z.ToString("F1", CultureInfo.InvariantCulture) +
                    " fov=" + c.fieldOfView.ToString("F1", CultureInfo.InvariantCulture) +
                    " rect=" + c.pixelWidth + "x" + c.pixelHeight);
            }
            return "dumped " + cams.Length + " cameras";
        }

        /// <summary>
        /// 强制结束本回合（`43_roundend.png` 取证）：走 <c>ICsMatch.ForceEndRound</c>（对外契约，非另造）。
        /// 原因取 <c>TimeExpired</c>（= H12 判据的「时间到」文案），胜方 CT。
        /// </summary>
        public static string ForceEndRound()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            m.ForceEndRound(CsTeam.CT, CsRoundEndReason.TimeExpired);
            Cs16Driver.AppendLine("round", "ForceEndRound(CT, TimeExpired) 已发出");
            return "ForceEndRound ok";
        }

        /// <summary>
        /// 短赛制单人 T 开局（`42_matchend.png` 取证）：<c>RoundsPerHalf=1</c> ⇒ 胜场阈值
        /// = <c>RoundsPerHalf + 1</c>（见 <c>CsRound.cs</c>）⇒ 两次 <c>ForceEndRound(T)</c>
        /// 就会走到**真实**的 <c>MatchEnded</c>（⛔ 不是直接发事件）。
        /// </summary>
        public static string StartShortSoloT()
        {
            var bus = Game.Event;
            if (bus == null) return "ERROR: Game.Event null";
            var cfg = new CsMatchConfig();
            cfg.MapName = CsConst.MapDust2;
            cfg.BotsPerTeam = 0;
            cfg.BotDifficulty = CsBotDifficulty.Normal;
            cfg.PlayerTeam = CsTeam.T;
            cfg.RoundsPerHalf = 1;
            bus.Emit<CsMatchConfig>(Events.LaunchMatch, cfg);
            return "LaunchMatch short(RoundsPerHalf=1) solo T emitted";
        }

        /// <summary>强制 CT 赢下本回合（供短赛制走 MatchEnd 用）。</summary>
        public static string EndRoundCT()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            m.ForceEndRound(CsTeam.CT, CsRoundEndReason.TimeExpired);
            Cs16Driver.AppendLine("round", "ForceEndRound(CT, TimeExpired)");
            return "end round CT ok";
        }

        /// <summary>强制 T 赢下本回合（供短赛制走 MatchEnd 用）。</summary>
        public static string EndRoundT()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            m.ForceEndRound(CsTeam.T, CsRoundEndReason.TimeExpired);
            Cs16Driver.AppendLine("round", "ForceEndRound(T, TimeExpired)");
            return "end round T ok";
        }

        // ==============================================================
        // 片AE：`42_matchend.png` 的**非平局**全场结算入口（测试入口，供离线取证驱动使用）
        // ==============================================================
        // 为什么要先 "关掉换边"：生产配置 RoundsPerHalf=15 / MaxRounds=30 / HalfTimeSwap=true ⇒
        //   第 16 回合 SwapHalves() 把阵营**与比分**一起互换（"比分跟着人走"）⇒ 打满 30 回合
        //   最终必为 15:15，EndMatchInternal 只能给 Draw!（实测：ac-recapture6 连喂 34 次
        //   ForceEndRound(CT) 采到的就是 `Draw! CT 15 : 15 T 共 30 回合`）。
        // 本批 = 两件套：① 关掉**本局**换边（走业务侧类型化测试入口
        //   `CsMatch.SetHalfTimeSwapForTest`，⛔ 不是反射、不是改 ICsMatch 签名）；② 按比分反馈把
        //   本局 30 个回合的胜方依次喂给 `ICsMatch.ForceEndRound`（只在**买枪期**结算 ⇒ 不会
        //   出现"机器人自己打完"的意外回合）。比赛本身仍走菜单流程的正常开局（生产 15/30）。

        /// <summary>
        /// 测试入口，供离线取证驱动使用：关掉**本局**的半场换边
        /// （经**具体类型** `CsMatch` 调 `SetHalfTimeSwapForTest` —— ⛔ 不改 `ICsMatch` 签名）。
        /// </summary>
        public static string DisableHalfTimeSwapForTest()
        {
            var mm = MatchModule.Instance;
            if (mm == null) return "ERROR: MatchModule.Instance null";
            var m = mm.Match as CsMatch;
            if (m == null) return "ERROR: Match is not CsMatch";
            m.SetHalfTimeSwapForTest(false);
            return "HalfTimeSwap=false (this match)";
        }

        /// <summary>
        /// 测试入口，供离线取证驱动使用：把**本局**的半场回合数改小（= 1 ⇒ 胜负阈值 2）并关掉换边
        /// （经**具体类型** `CsMatch` 调 `SetShortMatchForTest` —— ⛔ 不改 `ICsMatch` 签名）。
        /// 于是"任意一方先赢 2 回合"就走到**真实** `EndMatchInternal`，给出**非平局**结算，
        /// 且 2 个回合即可收场（生产 15/30 真打要 30 回合约 4 分钟，取证窗口内打不完 —— 见 片AE 实测）。
        /// </summary>
        public static string SetShortMatchForTest()
        {
            var mm = MatchModule.Instance;
            if (mm == null) return "ERROR: MatchModule.Instance null";
            var m = mm.Match as CsMatch;
            if (m == null) return "ERROR: Match is not CsMatch";
            m.SetShortMatchForTest(1, false);
            return "short match: RoundsPerHalf=1 (win target 2) HalfTimeSwap=false";
        }

        /// <summary>测试入口，供离线取证驱动使用：回报当前比赛读数（驱动脚本按状态推进用）。</summary>
        public static string MatchStateForTest()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            return "phase=" + m.Phase + " round=" + m.RoundNumber +
                   " scoreCT=" + m.ScoreCT + " scoreT=" + m.ScoreT +
                   " left=" + m.PhaseTimeLeft.ToString("F1", CultureInfo.InvariantCulture) +
                   " running=" + m.IsRunning;
        }

        /// <summary>
        /// 测试入口，供离线取证驱动使用：按**预置回合剧本**把本局推进到**非平局**的 MatchEnd。
        /// 每次调用只在"当前回合处于 **Freeze（买枪期）**"时结算一次（其余阶段空转，与
        /// `CsRound.EndRound` 的阶段闸门一致；买枪期没有交战 ⇒ ⛔ 不会有"机器人自己打完"的意外回合）。
        /// 胜方按**比分反馈**取（抗意外）：CT 未到 15 就先喂 CT；CT 到了 15 且 T 未到 14 且回合数还有余
        /// 就喂 T；否则喂 CT（最后一回合 = CT 的第 16 胜）。
        /// 换边已关（比分不互换）⇒ 第 30 回合结算后 = **CT 16 : 14 T**：
        /// 16 胜阈值**正好落在第 30 回合**（第 29 回合后 CT 15 : T 14，两边都没到 16）⇒ ⛔ 不会提前结束。
        /// ⛔ 只用业务对外 API（`ICsMatch.ForceEndRound` + 只读 `Phase/RoundNumber/Score*`），不新增业务行为。
        /// </summary>
        public static string DriveMatchEndForTest()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            if (m.Phase == CsRoundPhase.MatchEnd) return "already MatchEnd";
            // 只在能让 `CsRound.EndRound` 生效的两个阶段结算（与它的阶段闸门同一口径）：
            // 优先在**买枪期**（没有交战 ⇒ 不会被机器人抢走回合），买枪期没赶上时也可在**交战期**
            // 结算。评分策略本身**抗意外回合**（见下），所以偶发一次"机器人自己打完"不会毁掉剧本。
            if (m.Phase != CsRoundPhase.Freeze && m.Phase != CsRoundPhase.Live)
                return "wait " + MatchStateForTest();
            var rn = m.RoundNumber;
            CsTeam winner;
            if (m.ScoreCT < 15) winner = CsTeam.CT;                          // 先把 CT 喂到 15
            else if (m.ScoreT < 14 && rn < CsConst.MaxRounds) winner = CsTeam.T;  // 再把 T 喂到 14
            else winner = CsTeam.CT;                                        // 最后一回合：CT 取第 16 胜
            m.ForceEndRound(winner, CsRoundEndReason.TimeExpired);
            Cs16Driver.AppendLine("matchscript", "round=" + rn + " -> " + winner +
                " (score CT " + m.ScoreCT + " : " + m.ScoreT + " T, phase=" + m.Phase + ")");
            return "round " + rn + " -> " + winner + " (CT " + m.ScoreCT + " : " + m.ScoreT + " T)";
        }

        // ══════════════ 切片J：H2「游戏内表现」批次入口 ══════════════
        // 全部走**业务自己的 API**（不是另造一套假面板）：
        //   面板 = Game.UI.Open<T>()（与 HudPanel 按键路由里那一行同一调用）、
        //   买卖 = CsMatch.TryBuyFor、切槽 = CsMatch.SwitchSlot、阵亡 = CsDamage.ApplyBombExplosion。
        // 图之外的每个数字都由 SnapHud() 的运行时读数兜住（数值类证据，见 策划/验收表.md 的类别口径）。

        /// <summary>比赛门面（<c>ICsMatch</c> 就是"业务对外那一面"，买卖/切槽/切人全在它上面）。</summary>
        private static ICsMatch LiveMatch()
        {
            var mm = MatchModule.Instance;
            return mm != null ? mm.Match : null;
        }

        private static CsActor LocalActor()
        {
            var m = LiveMatch();
            return m != null ? m.LocalPlayer : null;
        }

        public static string OpenBuyMenu()
        {
            Game.UI.Open<Cs16.UI.BuyMenuPanel>();
            return "BuyMenuPanel opened";
        }

        public static string OpenHMenu()
        {
            Game.UI.Open<Cs16.UI.HMenuPanel>();
            return "HMenuPanel opened";
        }

        public static string OpenRadioB()
        {
            // CsRadioGroup 是 UI 层的 top-level enum（UI/InGame/RadioMenuPanel.cs:9）⇒ 全限定。
            Game.UI.Open<Cs16.UI.RadioMenuPanel>(Cs16.UI.CsRadioGroup.B);
            return "RadioMenuPanel(B) opened";
        }

        public static string OpenConsole()
        {
            Game.UI.Open<Cs16.UI.ConsolePanel>();
            return "ConsolePanel opened";
        }

        public static string OpenScoreboard()
        {
            Game.UI.Open<Cs16.UI.ScoreboardPanel>();
            return "ScoreboardPanel opened";
        }

        /// <summary>
        /// 关掉「回合结算 / 比赛结束」两个结果面板（切片P 重开一局前用）。
        /// 为什么需要：一局打完 MatchEndPanel 会留在屏上，**下一局的取证帧会被它盖住**
        /// （实测：重开一局后 MatchEndPanel 仍在场 ⇒ 采到的图全是结算画面）。
        /// 关的是业务自己的面板门面 `Game.UI.Close&lt;T&gt;()`，⛔ 没有另造通路。
        /// </summary>
        public static string CloseEndPanels()
        {
            var ui = Game.UI;
            if (ui == null) return "ERROR: Game.UI null";
            ui.Close<Cs16.UI.RoundEndPanel>();
            ui.Close<Cs16.UI.MatchEndPanel>();
            ui.Close<Cs16.UI.PausePanel>();
            return "end panels closed";
        }

        public static string CloseOverlays()
        {
            var ui = Game.UI;
            if (ui == null) return "ERROR: Game.UI null";
            ui.Close<Cs16.UI.BuyMenuPanel>();
            ui.Close<Cs16.UI.HMenuPanel>();
            ui.Close<Cs16.UI.RadioMenuPanel>();
            ui.Close<Cs16.UI.ConsolePanel>();
            ui.Close<Cs16.UI.ScoreboardPanel>();
            return "overlays closed";
        }

        /// <summary>
        /// 请求换弹（R 键那条链的业务入口 <c>ICsMatch.RequestReload()</c>）——
        /// 切片P 的交叉帧「跑步换弹」要用它：驱动没有键盘层，而 `R` 是
        /// <c>CombatModule.FillInput</c> 里的 `GetKeyDown`，只能由真实键盘触发。
        /// ⛔ 没有给游戏加任何东西：调的就是 CombatModule 在 R 分支上调的同一个方法。
        /// </summary>
        public static string RequestReload()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            m.RequestReload();
            Cs16Driver.AppendLine("reload", "RequestReload 已发出（与 CombatModule 的 R 分支同一入口）");
            return "RequestReload ok";
        }

        /// <summary>
        /// 清掉引擎的日志限频记录（<c>CloverEngine.LogThrottle.Reset()</c>）。
        /// **为什么要它**：<c>CsModuleLog.Info</c> 是**降频**的（同一 key 首次必打、之后每 N 次一条），
        /// 而编辑器进程一直活着 ⇒ 上一轮进图已经花掉了 `hitwall.sand` 等的"首次"，
        /// 取证那一刻的弹着分类行就**不会落到控制台**（实测：切片P 第一次采弹痕帧时整段 15:43:0x 没有 [Combat] 行）。
        /// ⛔ 只清计数表，不改任何判定/游戏逻辑（引擎自带的排障入口，出处见 CsModuleLog 类注释）。
        /// </summary>
        public static string ResetLogs()
        {
            CloverEngine.LogThrottle.Reset();
            Cs16Driver.AppendLine("log.reset", "LogThrottle.Reset()：限频记录清空（同类首次重新必打）");
            return "LogThrottle.Reset ok";
        }

        /// <summary>余额：买得起的一侧（= <c>CsConst.MaxMoney</c>，出处随包 <c>server.cfg</c> 的 mp_startmoney 上限）。</summary>
        public static string Money16000()
        {
            var a = LocalActor();
            if (a == null) return "ERROR: no local";
            a.Money = CsConst.MaxMoney;
            Cs16Driver.AppendLine("money", "local money=" + a.Money + "（= CsConst.MaxMoney）");
            return "money=" + a.Money;
        }

        /// <summary>余额：买不起的一侧（与原版截图同一形态：<c>$150</c> ⇒ 金额显示用 MoneyNormal 色）。</summary>
        public static string Money150()
        {
            var a = LocalActor();
            if (a == null) return "ERROR: no local";
            a.Money = 150;
            Cs16Driver.AppendLine("money", "local money=" + a.Money);
            return "money=" + a.Money;
        }

        /// <summary>买一把主武器 + 一颗雷 + 一把手枪（切槽帧要用"身上真有这把枪"）。</summary>
        public static string BuyLoadout()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            var a = m.LocalPlayer;
            if (a == null) return "ERROR: no local";
            a.Money = CsConst.MaxMoney;
            string r1, r2, r3;
            var ok1 = m.TryBuyFor(a.Id, CsWeapons.Ak47, out r1);
            var ok2 = m.TryBuyFor(a.Id, CsWeapons.HeGrenade, out r2);
            var ok3 = m.TryBuyFor(a.Id, CsWeapons.Deagle, out r3);
            Cs16Driver.AppendLine("buy",
                "TryBuyFor(ak47)=" + ok1 + " (" + r1 + ") | TryBuyFor(hegrenade)=" + ok2 + " (" + r2 + ")" +
                " | TryBuyFor(deagle)=" + ok3 + " (" + r3 + ") | money=" + a.Money);
            return "buy ak=" + ok1 + " he=" + ok2 + " deagle=" + ok3;
        }

        /// <summary>
        /// 弹药只读快照（**测试入口：不写任何游戏状态**，照既有 *ForTest 入口的形状）。
        ///
        /// **为什么要它**：切片P 的交叉帧「朝墙弹痕」（F-05）没拿到新弹痕 —— 根因是**买枪错过了买枪窗**：
        /// 买枪门 = <c>CsMatch.CanBuyTime</c>（<c>mp_buytime</c> = 15 s，出处 <c>server.cfg:42</c>）
        /// 外加 <c>a.InBuyZone</c>。<c>p-play.ps1</c> 旧序是"点 CT → 睡 16 s → 才买" ⇒ 窗口刚关，
        /// 三次 <c>TryBuyFor</c> 全 false ⇒ 手里还是默认 USP、且它的**弹匣读数是 0**（现场 <c>USP .45 0 / 71</c>）
        /// ⇒ <c>fireheld=1</c> 在空弹匣上**一条射线都不发** ⇒ 墙上当然没有新弹痕。
        /// 所以"开火前"必须先断言 <c>mag &gt; 0</c>；⛔ 不许静默地在空弹匣上"开火"（那会产出一个假的"没有弹痕"结论）。
        ///
        /// ⛔ **只读**：直接查 <c>CsActor.Ammo</c> 字典，**不调 <c>GetAmmo()</c>**
        /// （后者在缺项时会把初始弹药**写回**字典 —— 见 <c>CsTypes.cs:74-83</c>；
        /// <c>ViewModelRig.cs:407</c> 正是为此特意避开它）。⛔ 未改业务实现、未改 <c>ICsMatch</c> 签名。
        /// </summary>
        public static string AmmoSnapshot()
        {
            var a = LocalActor();
            if (a == null) return "ERROR: no local";
            var weapon = a.ActiveWeapon ?? "-";
            int mag = -1;
            int reserve = -1;
            if (a.Ammo.TryGetValue(weapon, out var ammo)) { mag = ammo.inMag; reserve = ammo.reserve; }
            var line = "slot=" + weapon + " mag=" + mag.ToString(CultureInfo.InvariantCulture) +
                       " reserve=" + reserve.ToString(CultureInfo.InvariantCulture) +
                       " money=" + a.Money.ToString(CultureInfo.InvariantCulture) +
                       " inBuyZone=" + (a.InBuyZone ? "1" : "0");
            Cs16Driver.AppendLine("ammo", line);
            return line;
        }

        public static string Slot1() => SwitchTo(1);
        public static string Slot2() => SwitchTo(2);
        public static string Slot3() => SwitchTo(3);
        public static string Slot4() => SwitchTo(4);

        /// <summary>5 号槽（C4）：先把包交给本地玩家（<c>CsActor.HasBomb</c>），再切槽。</summary>
        public static string Slot5C4()
        {
            var a = LocalActor();
            if (a == null) return "ERROR: no local";
            a.HasBomb = true;
            Cs16Driver.AppendLine("c4", "HasBomb=true（本地玩家持包）");
            return SwitchTo(5);
        }

        private static string SwitchTo(int slot)
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            m.SwitchSlot(slot);
            var a = m.LocalPlayer;
            var ammo = a != null ? a.GetAmmo(a.ActiveWeapon) : default((int inMag, int reserve));
            Cs16Driver.AppendLine("slot",
                "SwitchSlot(" + slot + ") activeWeapon=" + (a != null ? a.ActiveWeapon : "?") +
                " mag=" + ammo.inMag.ToString(CultureInfo.InvariantCulture) +
                " reserve=" + ammo.reserve.ToString(CultureInfo.InvariantCulture));
            return "SwitchSlot(" + slot + ")";
        }

        /// <summary>
        /// 让本地玩家阵亡并进观战（G9 / `23_thirdperson.png`）：
        /// 走**业务自己的伤害落地** <c>CsDamage.ApplyBombExplosion</c>（C4 在本地脚下爆），
        /// ⛔ 不是直接置 <c>IsAlive=false</c> —— 后者绕过 <c>CsMatch.OnKilled</c>，
        /// 而"本地玩家阵亡 → 进入观战"那条日志与观战接管都在 OnKilled 里面。
        ///
        /// 【切片R 起：改为**类型化的测试入口**，本处已无反射】
        /// 旧做法是反射取 <c>CsMatch.Damage</c>（internal 字段）。按 skill §0.6 第 3 条，
        /// 游戏侧新增了 public <c>CsMatch.ApplyBombExplosionForTest(center)</c>（内部调同一个
        /// <c>Damage.ApplyBombExplosion</c>），本驱动改用它。⛔ <c>ICsMatch</c> 契约未动
        /// ⇒ 这里经 <c>m is CsMatch</c> 取具体类型（接口上没有这个测试入口）。
        /// </summary>
        public static string KillLocal()
        {
            var m = LiveMatch();
            if (m == null) return "ERROR: no match";
            var a = m.LocalPlayer;
            if (a == null) return "ERROR: no local";
            if (!(m is CsMatch cm)) return "ERROR: live match is not CsMatch";
            Cs16Driver.AppendLine("kill",
                "ApplyBombExplosion at local pos=" + a.Position.x.ToString("F2", CultureInfo.InvariantCulture) + "," +
                a.Position.y.ToString("F2", CultureInfo.InvariantCulture) + "," +
                a.Position.z.ToString("F2", CultureInfo.InvariantCulture));
            cm.ApplyBombExplosionForTest(a.Position);
            return "bomb exploded at local pos";
        }

        /// <summary>
        /// HUD 快照读数（数值类证据）：图上每个数字都要能对回这里的一行。
        /// 快照 = UI 与业务之间的唯一数据契约（<c>Core/CsHudSnapshot.cs</c>），UI 只读它 ⇒ 它是什么，画面就该是什么。
        /// </summary>
        public static string SnapHud()
        {
            Cs16Driver.AppendLine("hud",
                "valid=" + CsHudSnapshot.Valid + " alive=" + CsHudSnapshot.IsAlive +
                " spectating=" + CsHudSnapshot.IsSpectating + " spectate=" + (CsHudSnapshot.SpectateName ?? "-") +
                " hp=" + CsHudSnapshot.Health + " armor=" + CsHudSnapshot.Armor +
                " money=" + CsHudSnapshot.Money +
                " weapon=" + (CsHudSnapshot.WeaponId ?? "-") + "/" + (CsHudSnapshot.WeaponName ?? "-") +
                " mag=" + CsHudSnapshot.Mag + " reserve=" + CsHudSnapshot.Reserve +
                " phase=" + CsHudSnapshot.Phase + " round=" + CsHudSnapshot.RoundNumber +
                " roundTime=" + CsHudSnapshot.PhaseTimeLeft.ToString("F1", CultureInfo.InvariantCulture) +
                " score=CT" + CsHudSnapshot.ScoreCT + ":T" + CsHudSnapshot.ScoreT +
                " buyZone=" + CsHudSnapshot.InBuyZone + " canBuy=" + CsHudSnapshot.CanBuyNow +
                " planted=" + CsHudSnapshot.BombPlanted +
                " hitMarker=" + CsHudSnapshot.HitMarkerTime.ToString("F3", CultureInfo.InvariantCulture) +
                " hmHead=" + CsHudSnapshot.HitMarkerHeadshot +
                " killFeed=" + CsHudSnapshot.KillFeed.Count + " radar=" + CsHudSnapshot.Radar.Count);
            for (var i = 0; i < CsHudSnapshot.KillFeed.Count && i < 4; i++)
            {
                var k = CsHudSnapshot.KillFeed[i];
                Cs16Driver.AppendLine("hud.kill",
                    "[" + i + "] " + k.KillerName + "(" + k.KillerTeam + ") " + k.WeaponId +
                    (k.Headshot ? " HS" : "") + " -> " + k.VictimName + "(" + k.VictimTeam + ")");
            }
            return "hud snapshot dumped";
        }

        /// <summary>把当前游戏画面的截图存到 client 内（screen 合成源用 shot.ps1，这里只作备份路径）。</summary>
        public static string Screen() => "use .ai-tmp/drivers/shot.ps1 instead";
    }
}
