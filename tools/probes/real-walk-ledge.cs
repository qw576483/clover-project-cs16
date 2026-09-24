// 判据资产（tools/probes/）：差异 #66 的**实机那一半** —— 匪家那块「0.90 m 台沿」（扶手）
// 到底"走过去会不会卡"、"是不是概率卡"、"跳不跳得过去"。
//
// 为什么非要实机（离线判不了的部分，写在 bm-step-audit.py 的口径里）：
//   `CanStand` 第一层 `BitmapClear(pos, radius)` 要**中心 + 8 向采样全可走**，
//   第二层才是几何兜底 `BodyHeightClear`。**谁先命中**决定了"擦着边过不过得去"，
//   这一层是半径/采样的运行时行为，离线只算得出"硬边界"，算不出"概率"。
//
// 台沿几何（来自 tools/probes/bm-step-audit.py 的逐格高度，离线可复算）：
//   高台：cell(x=21..26, z=27..30)  h = 3.25 m   （世界 z ∈ [-42,-41] 那一行是 z=30）
//   低台：cell(x=21..26, z=31..35)  h = 2.35 m
//   ⇒ 高差 Δ = 0.90 m = 2× StepUpHeight(0.45) ⇒ **离线判据说纯走过不去**；
//     而 0.90 ≤ 跳跃可达 1.1445（JumpSpeed 6.82 / Gravity 20.32）⇒ 离线判据说跳能上去。
//   ⚠️ 本片（2026-09-24）实测：**这条离线结论在实机上不成立** —— 见下方 LANE 循环，
//      在 x=-39.5 车道上人在低处 y=1.200（离线写 2.35）、高处 y=2.907（离线写 3.25），
//      且 y 随 z **连续**爬升 = 那是一段**斜坡**，不是台沿。故本支改为**扫 3 条车道**
//      （cell x=21/23/25 ⇒ 世界 x=-41.5/-39.5/-37.5），每道 5 次纯走 + 3 次带跳，
//      以判"这块地方到底有没有一条纯走过不去的沿"。
//
// 判定（写死在输出末行，判据是可证伪的；⛔ 期望值由**实测几何**给出，不用离线假设）：
//   期望的前置事实：本批三条车道 `SampleGround(终) − SampleGround(起)` = **1.750 m > StepUpHeight(0.45)**
//   ⇒ 它们是**坡/阶**，正常情况纯走**应该**上得去（`GEOM-BASIS` 行逐轮打印这条）。
//   `RESULT-LEDGE: PASS`            每条车道 walk 5/5 上得去 且 jump 3/3
//   `RESULT-LEDGE: PROBABILISTIC`   任一车道 0 < walk < 5 ⇒ **用户报的"概率卡住"成立**
//   `RESULT-LEDGE: FAIL`            任一车道 jump 0/3（跳跃闸门比常量小）或每条车道 walk 0/5（全都上不去）
//   `RESULT-LEDGE: MIXED`           各车道结论不一致
//   ⚠️ 2026-09-24 片LEDGE-ROOT **作废旧口径**：旧版写"PASS = 每条车道 walk **0/5**"，
//      那是拿离线 `bm-step-audit.py` 的"这里有一道 0.90 m 台沿"当期望；该假设已被实机证伪
//      （runtime 是连续斜坡）⇒ 旧口径会在**修好之后**判 FAIL（自相矛盾）。详见 Finish() 注释① ②。
//
// 用法（编辑器在 Play、且已有一局）：unity command run_script --file tools/probes/real-walk-ledge.cs --entry RealWalkLedge.Begin
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;

public static class RealWalkLedge
{
    public const string StatePath = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\drivers\state.txt";
    public const string OutPath = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\r3-ledge.txt";

    /// <summary>
    /// 差异 #66 的**受控实验**开关：这个文件里写一个整数 = 把 <c>Application.targetFrameRate</c>
    /// 压到该值（⛔ 只在取证时用）。
    /// <para><b>为什么要有它</b>：两轮同车道同输入的实测结果不同，唯一显著差异是**帧率**
    /// （失败轮 dt≈0.018 ⇒ ~58 fps；成功轮 dt≈0.0074 ⇒ ~135 fps）。要把它从"相关"变成"因果"，
    /// 必须有**唯一的自变量** ⇒ 用帧率上限人为造出低帧率轮。</para>
    /// <para><b>为什么用文件而不是 <c>eval --code</c> 设字段</b>：探针是 <c>run_script</c> 现编译的，
    /// 编译顺序不保证"先 eval 设字段、再 run_script 读得到该类型"；文件没有这个时序问题。
    /// 文件不存在 / 内容 ≤0 ⇒ 什么都不动（保持引擎默认）。</para>
    /// </summary>
    public const string FpsPath = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\drivers\ledge-fps.txt";

    public static string Begin()
    {
        var go = new GameObject("RealWalkLedgeTick");
        go.AddComponent<RealWalkLedgeTick>();
        return "RealWalkLedge started -> " + OutPath;
    }
}

/// <summary>order -160：必须**早于**驱动（-150），否则驱动先读到旧状态 ⇒ 输入晚一帧生效。</summary>
[DefaultExecutionOrder(-160)]
public sealed class RealWalkLedgeTick : MonoBehaviour
{
    // ---- 车道（世界 x；由 bm-step-audit 点名的 6 列 cell x=21..26 里取 3 条）----
    //   cell x = 世界 x + 63 ⇒ x=-41.5 ⇒ cell 21 / x=-39.5 ⇒ cell 23 / x=-37.5 ⇒ cell 25
    private static readonly float[] LaneXs = { -41.5f, -39.5f, -37.5f };
    // 每条车道：5 次纯走 + 3 次带跳（先把"概率"这一半测满）
    private static readonly bool[] CasePattern = { false, false, false, false, false, true, true, true };

    private const float StartZ = -36.5f;     // cell(·,35) 格心（低侧，7 m 跑道）
    private const float GoalZ = -43.5f;      // cell(·,28) 格心（越过高侧）
    private const float LineZ = -41f;        // 离线判据点名的那条"沿"所在的 z

    private const float ReachZ = 0.6f;       // 到目标的 z 接受半径
    private const float ReachY = 0.45f;      // 到目标的高度接受半径（放宽到 ≈ 一步台阶）
    private const float ClampEps = 0.02f;    // 单帧水平位移 < 此值 ⇒ 计"被钳住"
    private const int MaxFrames = 900;
    private const float MaxSeconds = 22f;
    private const int SettleFrames = 5;

    private int _phase;      // 0 等地图 / 1 SETUP / 2 等 Live+落地 / 3 走 / 4 完成
    private int _case;
    private int _frame;
    private int _clamped;
    private int _settle;
    private float _maxY;
    private float _startY;
    private float _endY;
    private bool _done;
    private string _verdict = "(running)";

    // ---- 帧率诊断（差异 #66：同一个起点/同一份输入为何两种结果？先钉"是不是 dt 决定的"）----
    // 为什么必须记 dt：本探针跑在**真实游戏循环**里 ⇒ 每帧步长是 `Time.deltaTime`（可变）。
    // 实测：同一条车道同样只前进，成功例每帧约 0.058 m、失败例每帧约 0.112 m ⇒ 帧率差 ~2 倍。
    private float _dtMin;
    private float _dtMax;
    private float _dtSum;
    private int _dtN;
    private float _vyMin;
    private float _gyStart;      // 起点处**实测**地面高度（SampleGround）
    private float _gyEnd;        // 终点处实测地面高度
    private int _stepUpStart;    // 本例外起始的"抬台阶探测接住"计数（差异 #66 修法是否真被走到）

    private readonly int[] _walkTopped = new int[LaneXs.Length];
    private readonly int[] _jumpTopped = new int[LaneXs.Length];
    private readonly int[] _walkTried = new int[LaneXs.Length];
    private readonly int[] _jumpTried = new int[LaneXs.Length];
    // 每道**实测**起/终地面高度（`SampleGround(maxDrop:40)`）—— 期望值的唯一合法来源（见 Finish() 注释①）
    private readonly float[] _laneGyStart = new float[LaneXs.Length];
    private readonly float[] _laneGyEnd = new float[LaneXs.Length];

    private Vector3 _prev;
    private float _startTime;
    private StreamWriter _out;

    private CsMap _map;
    private MatchModule _mod;
    private ICsMatch _match;

    private static int LaneOf(int c) => c / CasePattern.Length;
    private static float LaneXOf(int c) => LaneXs[c / CasePattern.Length];
    private static bool JumpOf(int c) => CasePattern[c % CasePattern.Length];
    private static int CaseCount => LaneXs.Length * CasePattern.Length;

    private static string F3(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string F4(float v) => v.ToString("F4", CultureInfo.InvariantCulture);
    private static string V3(Vector3 v) => "(" + F3(v.x) + "," + F3(v.y) + "," + F3(v.z) + ")";

    private void Awake()
    {
        Application.runInBackground = true;

        // ---- 差异 #66 受控实验：按文件把帧率压到指定值（详见 RealWalkLedge.FpsPath）----
        var fpsNote = "未压帧（引擎默认）";
        try
        {
            if (System.IO.File.Exists(RealWalkLedge.FpsPath))
            {
                var txt = System.IO.File.ReadAllText(RealWalkLedge.FpsPath).Trim();
                int cap;
                if (int.TryParse(txt, out cap) && cap > 0)
                {
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = cap;
                    fpsNote = "已压帧 targetFrameRate=" + cap + "（vSyncCount=0）";
                }
                else
                {
                    fpsNote = "文件在、值=\"" + txt + "\" ⇒ 不压帧";
                }
            }
        }
        catch (Exception e) { fpsNote = "读 FpsPath 失败：" + e.Message; }

        try
        {
            _out = new StreamWriter(RealWalkLedge.OutPath, false, new UTF8Encoding(false));
            _out.AutoFlush = true;
        }
        catch (Exception e) { Debug.LogError("[ledge] 打不开输出文件: " + e.Message); }
        Say("BEGIN\torder=-160\tstate=" + RealWalkLedge.StatePath);
        Say("FPS-CAP\t" + fpsNote + "\t实际 targetFrameRate=" + Application.targetFrameRate +
            "\tvSync=" + QualitySettings.vSyncCount + "\tFpsPath=" + RealWalkLedge.FpsPath);
        Say("GEOM\t车道数=" + LaneXs.Length + "（世界 x=" + LaneXs[0] + "/" + LaneXs[1] + "/" + LaneXs[2] +
            " ⇒ cell x=21/23/25） 每道 " + CasePattern.Length + " 例（5 纯走 + 3 带跳）" +
            "\t起点=z " + F3(StartZ) + " cell(·,35)\t目标=z " + F3(GoalZ) + " cell(·,28)" +
            "\t离线点名的那条沿在 z=" + F3(LineZ));
    }

    private void OnDestroy()
    {
        try { if (_out != null) { Say("DESTROY\tverdict=" + _verdict); _out.Flush(); _out.Dispose(); } }
        catch { }
    }

    private void Say(string s)
    {
        Debug.Log("[ledge] " + s);
        try { if (_out != null) _out.WriteLine(s); } catch { }
    }

    private void Throttled(string msg)
    {
        if (_tick++ % 120 != 0) return;
        Say("WAIT\t" + msg + "\tt=" + F3(Time.realtimeSinceStartup));
    }
    private int _tick;

    private void Update()
    {
        if (_done) return;
        var now = Time.realtimeSinceStartup;

        if (_phase == 0)
        {
            _mod = MatchModule.Instance;
            if (_mod == null) { Throttled("等 MatchModule（从 Boot.unity 进 Play 才有）"); return; }
            _match = _mod.Match;
            _map = _mod.Map as CsMap;
            if (_match == null || _map == null) { Throttled("等 Match/Map 门面"); return; }
            if (!_map.IsLoaded) { Throttled("等地图加载 IsLoaded=false"); return; }
            Say("MAP-READY\tIsLoaded=" + _map.IsLoaded + "\tphase=" + _match.Phase + "\tround=" + _match.RoundNumber);
            _case = 0;
            _phase = 1;
            return;
        }
        if (_phase == 1) { PrepareCase(); return; }
        if (_phase == 2) { Settle(now); return; }
        if (_phase == 3) { Walk(now); return; }
    }

    private void PrepareCase()
    {
        var a = _match.LocalPlayer;
        if (a == null) { Throttled("等 LocalPlayer"); return; }
        if (_match.Phase != CsRoundPhase.Live) { Throttled("等 Live 相位（Freeze 期禁止移动、且会重生）phase=" + _match.Phase); return; }
        if (!a.IsAlive) { Throttled("本地玩家已阵亡，等复活"); return; }

        a.Position = new Vector3(LaneXOf(_case), 30f, StartZ);   // SETUP：从高处落下（⛔ 不是走上去的证据）
        a.Velocity = Vector3.zero;
        a.OnGround = false;
        _prev = a.Position;
        _frame = 0;
        _clamped = 0;
        _settle = 0;
        _maxY = 0f;
        _startY = 0f;
        _endY = 0f;
        _dtMin = float.MaxValue;
        _dtMax = 0f;
        _dtSum = 0f;
        _dtN = 0;
        _vyMin = 0f;
        // ⚠️ 这两处用 maxDrop=40：默认 8 m 的采样从 y=30 往下探**够不到**地面的 y≈1.2
        //    ⇒ 早期版本这一列恒为 -Infinity（看着像"样本被摆在空中"，其实是参数没给够）。
        _stepUpStart = CsMatch.StepUpProbeHits;
        _gyStart = _map.SampleGround(new Vector3(LaneXOf(_case), 30f, StartZ), 40f);
        _gyEnd = _map.SampleGround(new Vector3(LaneXOf(_case), 30f, GoalZ), 40f);
        _laneGyStart[LaneOf(_case)] = _gyStart;
        _laneGyEnd[LaneOf(_case)] = _gyEnd;
        _startTime = Time.realtimeSinceStartup;

        Say("");
        Say("=== CASE " + (_case + 1) + "/" + CaseCount + "  lane=" + LaneOf(_case) +
            " (x=" + F3(LaneXOf(_case)) + ")  " + (JumpOf(_case) ? "JUMP" : "WALK") + " ===");
        Say("SETUP\tcase=" + (_case + 1) + "\t车道x=" + F3(LaneXOf(_case)) +
            "\t模式=" + (JumpOf(_case) ? "前进+跳" : "只前进") +
            "\t起点由本组件摆放（⛔ 非证据）\tpos=" + V3(a.Position));
        WriteState(move: false, jump: false);
        _phase = 2;
    }

    private void Settle(float now)
    {
        var a = _match.LocalPlayer;
        if (a == null) { Throttled("等 LocalPlayer"); return; }
        if (!a.IsAlive) { Throttled("本地玩家已阵亡，等复活"); return; }
        if (_match.Phase != CsRoundPhase.Live) { Throttled("相位离开 Live ⇒ 重摆起点"); _phase = 1; return; }
        var dy = Mathf.Abs(a.Velocity.y);
        if (a.OnGround && dy < 0.05f) _settle++; else _settle = 0;
        if (_settle >= SettleFrames)
        {
            _startY = a.Position.y;
            Say("SETTLE\tcase=" + (_case + 1) + "\tpos=" + V3(a.Position) + "\tonGround=" + a.OnGround +
                "\t离起点=" + F3(HorizZ(a.Position, StartZ)));
            _phase = 3;
            _frame = 0;
            _prev = a.Position;
            _startTime = Time.realtimeSinceStartup;
        }
    }

    private void Walk(float now)
    {
        var a = _match.LocalPlayer;
        if (a == null) { Throttled("等 LocalPlayer"); return; }
        if (!a.IsAlive) { FinishCase(a.Position, false, "阵亡"); return; }
        if (_match.Phase != CsRoundPhase.Live) { FinishCase(a.Position, false, "相位离开 Live"); return; }

        _frame++;
        // 朝 -z 走：yaw = atan2(dx, dz)（dx=0, dz<0 ⇒ 180°）
        WriteState(move: true, jump: JumpOf(_case));

        var pos = a.Position;
        if (pos.y > _maxY) _maxY = pos.y;
        var dt = Time.deltaTime;
        if (dt < _dtMin) _dtMin = dt;
        if (dt > _dtMax) _dtMax = dt;
        _dtSum += dt;
        _dtN++;
        if (a.Velocity.y < _vyMin) _vyMin = a.Velocity.y;
        var dz = Mathf.Abs(pos.z - GoalZ);
        var moved = HorizZ(pos, _prev.z) + Mathf.Abs(pos.x - _prev.x);
        var clamped = moved < ClampEps;
        if (clamped) _clamped++;
        if (_frame % 10 == 0 || clamped)
            Say("F\t" + _frame + "\t" + (JumpOf(_case) ? "JUMP" : "WALK") +
                "\tlane=" + LaneOf(_case) +
                "\tpos=" + V3(pos) + "\tcell=(" + Mathf.FloorToInt(pos.x + 63f) + "," + Mathf.FloorToInt(pos.z + 72f) + ")" +
                "\tdz=" + F3(dz) + "\tmoved=" + F3(moved) +
                "\tground=" + a.OnGround + "\tmaxY=" + F3(_maxY) + "\tclamped=" + _clamped +
                "\tdt=" + F4(dt) + "\tvy=" + F3(a.Velocity.y) +
                "\tgy=" + F3(_map.SampleGround(pos)) +
                "\t" + (clamped ? "CLAMPED" : "ok"));
        _prev = pos;

        var topped = Mathf.Abs(pos.z - GoalZ) <= ReachZ && pos.y >= _startY + 0.6f;
        if (topped) { FinishCase(pos, true, "到顶"); return; }
        if (_frame >= MaxFrames) { FinishCase(pos, false, "帧数上限"); return; }
        if (now - _startTime >= MaxSeconds) { FinishCase(pos, false, "时间上限"); return; }
    }

    private void FinishCase(Vector3 pos, bool topped, string why)
    {
        var lane = LaneOf(_case);
        var jump = JumpOf(_case);
        _endY = pos.y;
        if (jump) { _jumpTried[lane]++; if (topped) _jumpTopped[lane]++; }
        else { _walkTried[lane]++; if (topped) _walkTopped[lane]++; }

        Say("CASE-SUMMARY\tcase=" + (_case + 1) + "\tlane=" + lane + " (x=" + F3(LaneXOf(_case)) + ")" +
            "\t模式=" + (jump ? "JUMP" : "WALK") +
            "\t到顶=" + topped + "\t原因=" + why +
            "\t帧=" + _frame + "\tclamped帧=" + _clamped +
            "\t起点y=" + F3(_startY) + "\t终点=" + V3(pos) +
            "\t爬升=" + F3(pos.y - _startY) + "\t最高y=" + F3(_maxY) +
            "\t实测地面y(起/终)=" + F3(_gyStart) + "/" + F3(_gyEnd) +
            "\t帧率诊断 dt[min/avg/max]=" + F4(_dtMin) + "/" +
                F4(_dtN > 0 ? _dtSum / _dtN : 0f) + "/" + F4(_dtMax) +
            "\tvy最小=" + F3(_vyMin) +
            "\t抬台阶接住=" + (CsMatch.StepUpProbeHits - _stepUpStart) +
            "\t终点cell=(" + Mathf.FloorToInt(pos.x + 63f) + "," + Mathf.FloorToInt(pos.z + 72f) + ")");

        _case++;
        if (_case >= CaseCount) { Finish(); return; }
        WriteState(move: false, jump: false);
        _phase = 1;
    }

    private void Finish()
    {
        _done = true;
        var lanesAll5 = 0; var lanesAll0 = 0; var lanesProb = 0; var lanesJumpBad = 0;
        for (var i = 0; i < LaneXs.Length; i++)
        {
            Say("LANE-TALLY\tlane=" + i + " (x=" + F3(LaneXs[i]) + ")\twalk=" + _walkTopped[i] + "/" + _walkTried[i] +
                "\tjump=" + _jumpTopped[i] + "/" + _jumpTried[i] +
                "\t该道起/终地面y=" + F3(_laneGyStart[i]) + "/" + F3(_laneGyEnd[i]) +
                "\t地面抬升=" + F3(_laneGyEnd[i] - _laneGyStart[i]));
            if (_walkTried[i] > 0 && _walkTopped[i] == _walkTried[i]) lanesAll5++;
            else if (_walkTopped[i] == 0) lanesAll0++;
            else lanesProb++;
            if (_jumpTried[i] > 0 && _jumpTopped[i] < _jumpTried[i]) lanesJumpBad++;
        }

        // ---- 口径（2026-09-24 片LEDGE-ROOT 修正；⛔ 旧版口径是错的，见注释②）----
        // ① 期望值必须由**实测几何**给出，而不是由离线清单的假设给出：本探针每道都记
        //    `该道起/终地面y`（`SampleGround(maxDrop:40)`，硬事实）⇒ 终比起高 **> 一个台阶** 即
        //    "这是一段**坡/阶**，纯走理应上得去" ⇒ 期望 **walk 5/5**；
        //    若那道终比起只高 ≤ 一个台阶，它本来就没有可测的"上不去"，本探针不构成判据（记为 n/a）。
        // ② 旧口径（"PASS = 每条车道 walk **0/5** 上得去"）是拿**离线假设**当期望：`bm-step-audit.py` 的 A 清单
        //    说这里有 0.90 m 台沿 ⇒ 于是把"纯走 5/5"当成了 FAIL。那个假设已被**实机证伪**
        //    （片LEDGE：同一处 runtime 是**连续斜坡**，成功例 y 逐帧 1.200→2.8 平滑上升、无台阶突变；
        //      离线把"每格全部候选朝上面"里的一对当成了"该格的可走地面"）。
        //    口径若继续钉在错误期望上，就会出现"修好了反而判 FAIL"这种自相矛盾的输出。
        var slopeLanes = 0;
        for (var i = 0; i < LaneXs.Length; i++)
            if (_laneGyEnd[i] - _laneGyStart[i] > CsConst.StepUpHeight) slopeLanes++;

        string verdict;
        if (lanesJumpBad > 0) verdict = "FAIL";                 // 跳跃闸门坏了（比常量小）→ 硬红
        else if (lanesProb > 0) verdict = "PROBABILISTIC";      // 部分上得去 = 用户报的"概率卡住"成立
        else if (lanesAll5 == LaneXs.Length && slopeLanes == LaneXs.Length) verdict = "PASS";
        else if (lanesAll5 == LaneXs.Length) verdict = "PASS";  // 全上得去；坡判据见 GEOM-BASIS 行
        else if (lanesAll0 == LaneXs.Length) verdict = "FAIL";  // 全都上不去
        else verdict = "MIXED";
        _verdict = verdict;

        Say("");
        Say("TALLY\t车道数=" + LaneXs.Length + "\t每道走例=" + (CasePattern.Length - 3) + "/跳例=3" +
            "\t每道全上得去=" + lanesAll5 + "\t每道全上不去=" + lanesAll0 + "\t概率车道=" + lanesProb +
            "\t跳例失败车道=" + lanesJumpBad + "\t判为坡的车道=" + slopeLanes + "/" + LaneXs.Length);
        Say("GEOM-BASIS\t判坡判据=该道 SampleGround(终)-SampleGround(起) > StepUpHeight(" + F3(CsConst.StepUpHeight) + ")" +
            "\t本批实测=" + F3(_laneGyEnd[0] - _laneGyStart[0]) + " ⇒ " + (slopeLanes == LaneXs.Length ? "三条都是坡（期望纯走 5/5）" : "有车道不是坡"));
        Say("RESULT-LEDGE: " + verdict +
            "\t口径：PASS = 每条车道 walk 5/5 上得去 且 jump 3/3（本批三条车道实测都是坡，见 GEOM-BASIS）；" +
            "PROBABILISTIC = 任一车道 0 < 纯走上得去 < 5（用户报的『概率卡住』成立）；" +
            "FAIL = 任一车道 jump 0/3（跳跃闸门比常量小）或每条车道纯走 0/5（全都上不去）；" +
            "MIXED = 各车道结论不一致。⛔ 旧口径把『5/5』当 FAIL，已作废（详见 Finish() 注释②）");
        Say("SUMMARY\tDONE cases=" + _case + "\tverdict=" + verdict);
        try { if (_out != null) { _out.Flush(); } } catch { }
    }

    private void WriteState(bool move, bool jump)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("# written by tools/probes/real-walk-ledge.cs (order -160)\n");
            sb.Append("godmode=1\n");
            sb.Append("track=1\n");
            if (move)
            {
                // 9 字段：moveX,moveY,yaw,pitch,jump,crouch,walk,fire,zoom —— moveY=1 = 按住前进；yaw=180 = 朝 -z
                sb.Append("input=0,1,180.000,0,").Append(jump ? "1" : "0").Append(",0,0,0,0\n");
            }
            File.WriteAllText(RealWalkLedge.StatePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e) { Say("WARN\t写 state.txt 失败: " + e.Message); }
    }

    private static float HorizZ(Vector3 a, float z) => Mathf.Abs(a.z - z);
    private static float HorizZ(Vector3 a, Vector3 b) => Mathf.Abs(a.z - b.z);
}
