// ============================================================================
// 判据资产 · 切片BK：**"物理走不动"的逐帧证据**（只读）。
//
// 为什么要它：片BJ 的运行时复验显示机器人仍走不到包点（CT 进点 0、能动的 actor
//   只有 5 个且位移 ≤9.9m），而片BJ 只给出一个**未证伪的假设**：
//   "单层 2D 位图 + 多层几何的层间落差 ⇒ 机器人物理上走不动"。
//   本探针把那个假设变成**数字**：某一帧、某个 bot、站在哪一格、
//   脚下地面 y 是多少、法线 y 是多少、朝目标迈一步 ResolveMove 实际走了多远、
//   8 个方向里有几个迈得动（= 是不是被围死在口袋里）。
//
// 本探针**只读**（不写 state.txt / 不下输入 / 不改任何 actor 或赛局状态）；
//   唯一"非只读"的动作是反射读取 CsBotBrain 的私有目标点（读，不写）。
//
// 逐帧（每 15 帧 = 4 Hz）每个 bot 一行 `B` 行；"卡住"（2 s 内位移 < 0.15m）
//   首次发生时补一行 `E STUCK`（带完整诊断），每个 bot 每回合最多一次。
//
// 列（TSV，全部是数字或短标记，分析脚本直接读）：
//   B <frame> <t> <name> <id> <team> <state> \
//     <x> <y> <z> <goalX> <goalY> <goalZ> <goalValid> <distXZ> <goalDy> \
//     <canStandPos> <groundY> <normalY> <posMinusGround> \
//     <dirsMovable> <goalStepLen> <goalStepLenYFollow> \
//     <wantGroundY> <wantNormalY> <wantCanStand> <wantStepDy> <reason> \
//     <routeEndY> <rwp> <planRoute> <holdSite> <holdSlot>
//
// 口径出处（⛔ 全部复用产品同源判据，不另立定义）：
//   可站       CsMap.CanStand(pos, CsConst.PlayerRadius)            Module/Map/CsMap.cs:290
//   本地解算   CsMap.ResolveMove(from, to, CsConst.PlayerRadius)     Module/Map/CsMap.cs:431
//   地面+法线  CsMap.TrySampleGround(pos, out point, out normal)     Module/Map/CsMap.cs:542
//   台阶高     CsConst.StepUpHeight                                  CsMap.cs:522
//   陡坡       CsConst.MaxStandableSlopeNormalZ (0.70)               CsMap.cs:521
//
// 用法（在 client/ 里跑；需先进入 Play 且驱动已开一局）：
//   unity command run_script --file <本文件绝对路径> --entry BotPhys.Begin
//   ... 让模拟自己跑 ...
//   unity command run_script --file <本文件绝对路径> --entry BotPhys.Stop
// 产物：<项目根>/.ai-tmp/test/bk-bot-phys.tsv
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

public static class BotPhys
{
    public const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\bk-bot-phys.tsv";

    public static string Begin()
    {
        var go = new GameObject("BotPhysTick");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<BotPhysTick>();
        return "BotPhys started (order=201) -> " + OutPath;
    }

    public static string Stop()
    {
        var t = UnityEngine.Object.FindAnyObjectByType<BotPhysTick>(FindObjectsInactive.Include);
        if (t == null) return "BotPhys: 没有在跑的探针";
        t.FinishProbe();
        return "BotPhys stopped -> " + OutPath;
    }
}

/// <summary>order 201：在所有业务 Tick（0 / −200 / −150 / 200）之后 → 采到本帧结算后的状态。</summary>
[DefaultExecutionOrder(201)]
public sealed class BotPhysTick : MonoBehaviour
{
    private const string Tag = "bk-botphys";
    private const int SampleEveryFrames = 15;      // 60 fps ⇒ 4 Hz
    private const float Horizon = 1.0f;            // 迈一步的探测水平距离（米）
    private const float MovableEps = 0.2f;         // 位移 > 0.2m 才算"这个方向迈得动"
    private const float StuckEps = 0.15f;          // 2 s 内位移 < 0.15m ⇒ 卡住
    private const float StuckWindow = 2.0f;

    private static readonly float[] DirX = { 1f, 0.7071f, 0f, -0.7071f, -1f, -0.7071f, 0f, 0.7071f };
    private static readonly float[] DirZ = { 0f, 0.7071f, 1f, 0.7071f, 0f, -0.7071f, -1f, -0.7071f };

    private StreamWriter _out;
    private int _frame;

    private MatchModule _mod;
    private ICsMatch _match;
    private ICsMap _map;
    private BotModule _bots;

    // reflection: CsBotBrain 的私有计划字段（只读）
    private FieldInfo _fGoalPos, _fGoalValid, _fPlanRoute, _fNav, _fHoldSite, _fHoldSlot;
    private FieldInfo _fBrains;
    private bool _reflectDone;
    private bool _reflectWarned;

    // ---- 片BL：环境自诊断（`E ENV` 行）----
    // 片BK 的教训：`goalValid==1` 为 0/10656 时，**无法区分**是"实例没找到 / _brains 为空 /
    // 字段名不对 / 反射根本没跑"。反射的字段缓存原本只在"已经拿到 brain"时才建立（见 EnsureFields
    // 的调用点），而 brain 又只能从 ReadBrains 拿 —— 于是 `_fBrains==null ⇒ ReadBrains 返回 null
    // ⇒ brain==null ⇒ EnsureFields 永不调用 ⇒ _fBrains 永远 null`，**死循环恒返回 null**。
    // 现在：① 反射在 Update 开头就建（不再依赖 brain）② 每次 env 变化往 tsv 落一行 E ENV。
    private string _envKey;
    private int _envLines;

    // 卡住在哪一格（每个 bot 每回合一次）
    private readonly Dictionary<long, float> _stuckAtT = new Dictionary<long, float>();
    private readonly Dictionary<long, Vector3> _stuckRef = new Dictionary<long, Vector3>();
    private int _lastRound = -1;

    private static string F(float v)
    {
        if (float.IsNegativeInfinity(v) || float.IsPositiveInfinity(v) || float.IsNaN(v)) return "na";
        return v.ToString("F3", CultureInfo.InvariantCulture);
    }

    private void Awake()
    {
        Application.runInBackground = true;
        try
        {
            var dir = Path.GetDirectoryName(BotPhys.OutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _out = new StreamWriter(BotPhys.OutPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception e) { Game.Logger?.Error(Tag, "打不开输出文件: " + e.Message); }

        Say("E\tPROBE\tBEGIN\tframe=0\tt=" + F(Time.realtimeSinceStartup) +
            "\tsampleEvery=" + SampleEveryFrames + "\thorizon=" + F(Horizon));
    }

    private void OnDestroy()
    {
        try { _out?.Flush(); _out?.Dispose(); } catch { }
        _out = null;
    }

    public void FinishProbe()
    {
        Say("E\tPROBE\tSTOP\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup));
        try { _out?.Flush(); } catch { }
    }

    private void Say(string line)
    {
        try { _out?.WriteLine(line); } catch { }
    }

    /// <summary>
    /// 片BL：环境自诊断（值变化时落一行 <c>E ENV</c>，首行必落），**直接写进同一个 tsv** ——
    /// 片BK 试过只写 <c>Game.Logger</c>，结果 0 命中（日志被别处刷掉 / 不在采集窗口）。
    /// 列：bots / fBrains / brainsCount / module / mode / match / map / fields / envLines。
    /// </summary>
    private void EmitEnv()
    {
        var brainCount = -1;
        var moduleName = "-";
        if (_bots != null)
        {
            try { brainCount = _bots.BrainCount; } catch { brainCount = -2; }
            moduleName = _bots.GetType().FullName ?? _bots.GetType().Name;
        }

        var key = (_bots != null ? "1" : "0") + "|" + (_fBrains != null ? "1" : "0") + "|" + brainCount + "|" +
                  (_match != null ? "1" : "0") + "|" + (_map != null ? "1" : "0") + "|" + moduleName;
        if (key == _envKey) return;
        _envKey = key;
        _envLines++;

        Say("E\tENV\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
            "\tbots=" + (_bots != null ? "found" : "null") +
            "\tfBrains=" + (_fBrains != null ? "ok" : "null") +
            "\tbrainsCount=" + brainCount +
            "\tmodule=" + moduleName +
            "\tmode=" + (Application.isPlaying ? "Play" : "null") +
            "\tmatch=" + (_match != null ? "ok" : "null") +
            "\tmap=" + (_map != null ? "ok" : "null") +
            "\tfields=" +
                (_fGoalPos != null ? "goalPos" : "-") + "," +
                (_fGoalValid != null ? "goalValid" : "-") + "," +
                (_fPlanRoute != null ? "planRoute" : "-") + "," +
                (_fNav != null ? "nav" : "-") + "," +
                (_fHoldSite != null ? "holdSite" : "-") + "," +
                (_fHoldSlot != null ? "holdSlot" : "-") +
            "\tenvLines=" + _envLines);
    }

    /// <summary>反射缓存：字段名固定，类型用**运行时类型**（不靠 Assembly-CSharp 字符串名）。</summary>
    private void EnsureFields(Type brainT)
    {
        if (_reflectDone) return;
        _reflectDone = true;
        try
        {
            const BindingFlags F1 = BindingFlags.Instance | BindingFlags.NonPublic;
            _fGoalPos = brainT.GetField("_goalPos", F1);
            _fGoalValid = brainT.GetField("_goalValid", F1);
            _fPlanRoute = brainT.GetField("_planRoute", F1);
            _fNav = brainT.GetField("_nav", F1);
            _fHoldSite = brainT.GetField("_holdSiteMarker", F1);
            _fHoldSlot = brainT.GetField("_holdSlot", F1);
            _fBrains = typeof(BotModule).GetField("_brains", F1);
            if (_fGoalPos == null || _fGoalValid == null)
                Game.Logger?.Warn(Tag, "反射没拿到 _goalPos/_goalValid → 目标点列留空（其余列照采）");
        }
        catch (Exception e)
        {
            Game.Logger?.Warn(Tag, "反射失败（目标点列留空）：" + e.Message);
        }
    }

    private void Update()
    {
        _frame++;
        if (_mod == null) _mod = MatchModule.Instance;
        if (_mod != null)
        {
            _match = _match ?? _mod.Match;
            _map = _map ?? _mod.Map;
        }

        // 片BL：BotModule 的两条取法 —— ① MatchModule 同级组件（BotModule.cs:81 的 GetComponent<MatchModule>()
        // 就是靠"同一个 GameObject"装配的，所以反向也走得通）② 场景里任意一个（含未激活）。
        if (_bots == null)
        {
            if (_mod != null) _bots = _mod.GetComponent<BotModule>();
            if (_bots == null) _bots = UnityEngine.Object.FindAnyObjectByType<BotModule>(FindObjectsInactive.Include);
        }

        // 片BL：反射**必须在 ReadBrains 之前**建立 —— 否则 _fBrains 恒 null ⇒ ReadBrains 恒返回 null。
        EnsureFields(typeof(CsBotBrain));
        EmitEnv();

        if (_mod == null) return;
        if (_match == null || _map == null) return;
        if (_frame % SampleEveryFrames != 0) return;

        var round = _match.RoundNumber;
        if (round != _lastRound)
        {
            _lastRound = round;
            _stuckAtT.Clear();
            _stuckRef.Clear();
            Say("E\tROUND\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                "\tround=" + round + "\tmapLoaded=" + _map.IsLoaded);
        }

        var brains = ReadBrains();

        var actors = _match.Actors;
        for (var i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            if (a == null || !a.IsBot) continue;

            var pos = a.Position;

            // ---- 目标（反射；拿不到就留空）----
            object brain = null;
            brains?.TryGetValue(a.Id, out brain);
            var hasGoal = false;
            var goal = Vector3.zero;
            var planRoute = "-";
            var holdSite = "-";
            var holdSlot = "-";
            float routeEndY = float.NegativeInfinity;
            var rwp = -1;
            var state = "-";
            if (brain != null)
            {
                try
                {
                    EnsureFields(brain.GetType());
                    if (_fGoalPos != null) goal = (Vector3)_fGoalPos.GetValue(brain);
                    if (_fGoalValid != null) hasGoal = (bool)_fGoalValid.GetValue(brain);
                    if (_fPlanRoute != null) planRoute = (string)_fPlanRoute.GetValue(brain) ?? "-";
                    if (_fHoldSite != null) holdSite = (string)_fHoldSite.GetValue(brain) ?? "-";
                    if (_fHoldSlot != null) holdSlot = _fHoldSlot.GetValue(brain)?.ToString() ?? "-";
                    var nav = _fNav?.GetValue(brain);
                    if (nav != null)
                    {
                        var navT = nav.GetType();
                        var pRwp = navT.GetProperty("RemainingWaypoints");
                        if (pRwp != null) rwp = (int)pRwp.GetValue(nav);
                        var pEnd = navT.GetProperty("RouteEnd");
                        if (pEnd != null) routeEndY = ((Vector3)pEnd.GetValue(nav)).y;
                    }
                    var pState = brain.GetType().GetProperty("State");
                    if (pState != null) state = pState.GetValue(brain)?.ToString() ?? "-";
                }
                catch (Exception e)
                {
                    if (!_reflectWarned) { _reflectWarned = true; Game.Logger?.Warn(Tag, "读 brain 字段失败：" + e.Message); }
                }
            }

            // ---- 脚下地面 / 法线（产品同源口径）----
            var canStand = _map.CanStand(pos, CsConst.PlayerRadius);
            var groundY = _map.SampleGround(pos);
            var hasN = _map.TrySampleGround(pos, out _, out var nrm);
            var normalY = hasN ? nrm.y : float.NegativeInfinity;

            // ---- 8 方向"迈得动吗" ----
            var movable = 0;
            for (var d = 0; d < 8; d++)
            {
                var want = new Vector3(pos.x + DirX[d] * Horizon, pos.y, pos.z + DirZ[d] * Horizon);
                var res = _map.ResolveMove(pos, want, CsConst.PlayerRadius);
                var len = Mathf.Sqrt((res.x - pos.x) * (res.x - pos.x) + (res.z - pos.z) * (res.z - pos.z));
                if (len > MovableEps) movable++;
            }

            // ---- 朝目标迈一步 ----
            var goalStepLen = 0f;
            var goalStepLenYFollow = 0f;
            var wantGroundY = float.NegativeInfinity;
            var wantNormalY = float.NegativeInfinity;
            var wantCanStand = true;
            var wantStepDy = float.NegativeInfinity;
            var reason = "-";
            var distXZ = float.NegativeInfinity;
            var goalDy = float.NegativeInfinity;
            if (hasGoal)
            {
                var dx = goal.x - pos.x;
                var dz = goal.z - pos.z;
                distXZ = Mathf.Sqrt(dx * dx + dz * dz);
                goalDy = goal.y - pos.y;
                var l = distXZ > 1e-4f ? distXZ : 1e-4f;
                var step = Mathf.Min(Horizon, distXZ);
                var ux = dx / l * step;
                var uz = dz / l * step;
                var want = new Vector3(pos.x + ux, pos.y, pos.z + uz);
                var wantY = new Vector3(pos.x + ux, goal.y, pos.z + uz);

                var res = _map.ResolveMove(pos, want, CsConst.PlayerRadius);
                goalStepLen = Mathf.Sqrt((res.x - pos.x) * (res.x - pos.x) + (res.z - pos.z) * (res.z - pos.z));
                var resY = _map.ResolveMove(pos, wantY, CsConst.PlayerRadius);
                var dzY = resY.z - pos.z;
                goalStepLenYFollow = Mathf.Sqrt((resY.x - pos.x) * (resY.x - pos.x) + dzY * dzY);

                wantCanStand = _map.CanStand(want, CsConst.PlayerRadius);
                var hasWG = _map.TrySampleGround(want, out var wp, out var wn);
                if (hasWG)
                {
                    wantGroundY = wp.y;
                    wantNormalY = wn.y;
                    wantStepDy = wp.y - pos.y;
                }

                reason = "ok";
                if (goalStepLen <= MovableEps)
                {
                    if (!wantCanStand) reason = "want-not-standable";
                    else if (wantStepDy > CsConst.StepUpHeight) reason = "step-too-high";
                    else reason = "resolved-blocked";
                }
                else if (!hasWG) reason = "want-no-ground";
            }
            else
            {
                reason = "no-goal";
            }
            if (hasN && normalY < CsConst.MaxStandableSlopeNormalZ) reason += "+steep-here";

            Say("B\t" + _frame + "\t" + F(Time.realtimeSinceStartup) + "\t" + a.Name + "\t" + a.Id + "\t" +
                a.Team + "\t" + state + "\t" +
                F(pos.x) + "\t" + F(pos.y) + "\t" + F(pos.z) + "\t" +
                F(goal.x) + "\t" + F(goal.y) + "\t" + F(goal.z) + "\t" + (hasGoal ? 1 : 0) + "\t" +
                F(distXZ) + "\t" + F(goalDy) + "\t" +
                (canStand ? 1 : 0) + "\t" + F(groundY) + "\t" + F(normalY) + "\t" + F(pos.y - groundY) + "\t" +
                movable + "\t" + F(goalStepLen) + "\t" + F(goalStepLenYFollow) + "\t" +
                F(wantGroundY) + "\t" + F(wantNormalY) + "\t" + (wantCanStand ? 1 : 0) + "\t" + F(wantStepDy) + "\t" +
                reason + "\t" + F(routeEndY) + "\t" + rwp + "\t" + planRoute + "\t" + holdSite + "\t" + holdSlot);

            // ---- 卡住检测（2 s 内位移 < 0.15m）----
            var now = Time.realtimeSinceStartup;
            if (!_stuckAtT.TryGetValue(a.Id, out var t0) ||
                now - t0 > StuckWindow)
            {
                if (_stuckAtT.ContainsKey(a.Id))
                {
                    var p0 = _stuckRef[a.Id];
                    var moved = Mathf.Sqrt((pos.x - p0.x) * (pos.x - p0.x) + (pos.z - p0.z) * (pos.z - p0.z));
                    if (moved < StuckEps)
                    {
                        Say("E\tSTUCK\tframe=" + _frame + "\tt=" + F(now) + "\tround=" + round +
                            "\tname=" + a.Name + "\tid=" + a.Id + "\tteam=" + a.Team + "\tstate=" + state +
                            "\tpos=" + F(pos.x) + "," + F(pos.y) + "," + F(pos.z) +
                            "\tgoal=" + F(goal.x) + "," + F(goal.y) + "," + F(goal.z) +
                            "\tgoalValid=" + (hasGoal ? 1 : 0) + "\tdistXZ=" + F(distXZ) + "\tgoalDy=" + F(goalDy) +
                            "\tcanStand=" + (canStand ? 1 : 0) + "\tgroundY=" + F(groundY) + "\tnormalY=" + F(normalY) +
                            "\tposMinusGround=" + F(pos.y - groundY) +
                            "\tdirsMovable=" + movable + "\tgoalStepLen=" + F(goalStepLen) +
                            "\tgoalStepLenYFollow=" + F(goalStepLenYFollow) + "\treason=" + reason +
                            "\tplanRoute=" + planRoute + "\trwp=" + rwp + "\tholdSite=" + holdSite + "\tholdSlot=" + holdSlot);
                    }
                }
                _stuckAtT[a.Id] = now;
                _stuckRef[a.Id] = pos;
            }
        }
    }

    private Dictionary<long, object> ReadBrains()
    {
        // 片BL：每一条失败路径都往 tsv 落一行 `E REFLECT`（不再只写 Game.Logger）——
        // 否则"整段 brain 读取返回 null"这件事在产物里没有任何锚点（片BK 的困境）。
        if (_bots == null)
        {
            Once("E\tREFLECT\treason=botmodule-null\tfBrains=" + (_fBrains != null ? "ok" : "null"));
            return null;
        }
        if (_fBrains == null)
        {
            Once("E\tREFLECT\treason=field-brains-null\ttype=" + _bots.GetType().FullName);
            return null;
        }
        try
        {
            var d = _fBrains.GetValue(_bots) as IDictionary;
            if (d == null)
            {
                Once("E\tREFLECT\treason=brains-not-idictionary\tvalue=" +
                     (_fBrains.GetValue(_bots)?.GetType().FullName ?? "null"));
                return null;
            }
            var res = new Dictionary<long, object>(d.Count);
            foreach (DictionaryEntry e in d) res[(long)e.Key] = e.Value;
            return res;
        }
        catch (Exception e)
        {
            Once("E\tREFLECT\treason=brains-get-threw\tex=" + e.GetType().Name + ":" + e.Message);
            return null;
        }
    }

    // 同一个原因只落一行（但不同原因各落一行 —— 早期"模块还没起"不该吃掉后面真正的失败）
    private readonly HashSet<string> _loggedReasons = new HashSet<string>();
    private void Once(string line)
    {
        var parts = line.Split('\t');
        var key = parts.Length > 2 ? parts[2] : line;     // 形如 "reason=botmodule-null"
        if (!_loggedReasons.Add(key)) return;
        Say(line);
    }
}
