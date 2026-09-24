// ============================================================================
// 从 B 楼梯底走到顶，采**逐帧运行时日志**，对照纯函数基准
//
// 为什么必须进 Play（差异 #66 的收口依据）：
//   被判的是 **输入 → PlayerMotor → CsMatch.UpdateLocalPlayer → StepActorPhysics** 这条真链，
//   "本地玩家输入层能不能把这台人送上去"这一格没采过 —— 本探针就是采这一格。
//
// 输入注入口径（**复用**既有驱动，不另造通道）：
//   本组件 [DefaultExecutionOrder(-160)] **先于**驱动跑，改写
//   `.ai-tmp/drivers/cs16-play-driver.cs` 的输入状态文件 `.ai-tmp/drivers/state.txt` 的 `input=` 行；
//   驱动（order -150）随后在 Apply() 里把它写进 `match.SetLocalInput`。
//   本组件**不自己调** `SetLocalInput`（两条注入路径会互相覆盖，是驱动注释里记过的坑）。
//   不写 `input=` 行时，PlayerModule（order -200）每帧写的"无按键"输入生效 ⇒ 角色停下。
//
// 三个 case（一轮 Play 采齐）：
//   1) T 侧走廊路径（A* 复算，与 bstairs-walkline.cs 同口径）逐帧改 yaw 朝下一路点 + 按住前进
//   2) CT 侧走廊路径，同上
//   3) T 侧「直线复现」：**只设一次 yaw 朝目标**、之后不再转向、按住前进
//
// 探针的写权限：只**写输入**、只**读位置**。唯一的写坐标动作 = 每个 case 开始把角色摆到楼梯底
//   （日志里逐条 `SETUP` 标注 ⇒ 那不是"走上去"的证据；证据 = 其后的逐帧位移）。
//
// 用法（在 client/ 里跑）：
//   unity command run_script --file <本文件绝对路径> --entry RealWalkBStairs.Begin
//   <项目根>/.ai-tmp/test/bf-realwalk.txt
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

public static class RealWalkBStairs
{
    public const string StatePath = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\drivers\state.txt";
    public const string OutPath = @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\bf-realwalk.txt";

    public static string Begin()
    {
        var go = new GameObject("RealWalkBStairsTick");
        go.AddComponent<RealWalkBStairsTick>();
        return "RealWalkBStairs started -> " + OutPath;
    }
}

/// <summary>
/// order -160：必须**早于**驱动（-150），否则驱动先读到旧状态 ⇒ 输入晚一帧生效。
/// </summary>
[DefaultExecutionOrder(-160)]
public sealed class RealWalkBStairsTick : MonoBehaviour
{
    private const float Reach = 0.35f;        // 到路点的接受半径（格 1.0 m）
    private const float ClampEps = 0.02f;     // 单帧水平位移 < 此值 ⇒ 计"被钳住"
    private const int MaxFramesPerCase = 1200;
    private const float MaxSecondsPerCase = 30f;
    private const int SettleFrames = 4;       // 落地稳定帧数
    private const float PathCell = 1.0f;      // 与 bstairs-walkline.cs 同口径（格心 = 标记点）
    private const int PathMaxNodes = 20000;

    private static readonly string[][] Cases =
    {
        new[] { "T-corridor",  "Route_T_To_B" },
        new[] { "CT-corridor", "Route_CT_To_B" },
        new[] { "T-straight",  "Route_T_To_B" },
    };

    private static readonly int[][] N8 =
    {
        new[] { 1, 0 }, new[] { -1, 0 }, new[] { 0, 1 }, new[] { 0, -1 },
        new[] { 1, 1 }, new[] { 1, -1 }, new[] { -1, 1 }, new[] { -1, -1 }
    };

    private int _phase;          // 0 等地图 / 1 SETUP / 2 等 Live / 3 走 / 4 完成
    private int _case;
    private int _frame;
    private int _clampedFrames;
    private int _slideFrames;
    private int _settle;
    private int _maxFramesReported;
    private bool _done;
    private string _verdict = "(running)";

    private Vector3 _lo, _hi, _prev, _start;
    private float _startTime;
    private float _prevY;
    private float _lastLog;
    private float _sumMoved;
    private bool _topped;
    private bool _stuck;
    private int _stuckFrame = -1;
    private Vector3 _stuckPos;
    private List<int[]> _path;
    private int _wp;
    private float _fixedYaw;
    private bool _straight;
    private readonly List<float> _recentMoved = new List<float>();
    private CsMap _map;
    private MatchModule _mod;
    private ICsMatch _match;
    private StreamWriter _out;
    private bool _phase6Logged;

    private static string F3(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static string V3(Vector3 v) => "(" + F3(v.x) + "," + F3(v.y) + "," + F3(v.z) + ")";

    private void Awake()
    {
        Application.runInBackground = true;
        try
        {
            _out = new StreamWriter(RealWalkBStairs.OutPath, false, new UTF8Encoding(false));
            _out.AutoFlush = true;
        }
        catch (Exception e) { Debug.LogError("[realwalk] 打不开输出文件: " + e.Message); }
        Say("BEGIN\torder=-160\tstate=" + RealWalkBStairs.StatePath);
    }

    private void OnDestroy()
    {
        try { if (_out != null) { Say("DESTROY\tverdict=" + _verdict); _out.Flush(); _out.Dispose(); } }
        catch { }
    }

    private void Say2(string s)
    {
        Debug.Log("[realwalk] " + s);
    }

    private void Say(string s)
    {
        Say2(s);
        try { if (_out != null) _out.WriteLine(s); } catch { }
    }

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

    private string _lastThrottle = "";
    private int _throttleCount;

    private void Throttled(string msg)
    {
        _throttleCount++;
        if (msg == _lastThrottle && _throttleCount % 120 != 0) return;
        _lastThrottle = msg;
        Say("WAIT\t" + msg + "\tt=" + F3(Time.realtimeSinceStartup));
    }

    // ---- SETUP：**先等 Live 相位**，再把角色摆到本 case 起点（Route_*_To_B 倒数第 2 个标记点）----
    //  为什么必须等 Live 之后才摆：回合开始（Freeze 起点）会**把玩家重生回出生点** ⇒
    //  在 Freeze 期摆好的位置会被重生覆盖，"走上去"这条证据就从出生点起算了（错误起点）。
    private void PrepareCase()
    {
        var side = Cases[_case][0];
        var route = Cases[_case][1];
        var a0 = _match.LocalPlayer;
        if (a0 == null) { Throttled("等 LocalPlayer"); return; }
        if (_match.Phase != CsRoundPhase.Live) { Throttled("等 Live 相位（Freeze 期禁止移动、且会重生）phase=" + _match.Phase); return; }
        if (!a0.IsAlive) { Throttled("本地玩家已阵亡，等复活"); return; }

        var pts = _map.Points(route);
        if (pts == null || pts.Length < 2)
        {
            Say("FAIL\t" + side + "\t标记点不足 route=" + route);
            Finish();
            return;
        }
        _lo = pts[pts.Length - 2];
        _hi = pts[pts.Length - 1];
        var a = _match.LocalPlayer;
        if (a == null)
        {
            Say("FAIL\t" + side + "\tLocalPlayer=null（需先开局；-200 PlayerModule 每帧写零）");
            Finish();
            return;
        }

        // 路径（同 bstairs-walkline.cs：8 邻接 A*，直 10 / 斜 14，对角要求两侧正交格可走）
        _path = null;
        _straight = side == "T-straight";
        if (_straight)
        {
            _path = new List<int[]>();   // 直线模式不用路径（只留空列表占位）
        }
        else
        {
            var pathWhy = "";
            _path = AStar(_map, _lo, new[] { 0, 0 },
                new[] { Mathf.RoundToInt((_hi.x - _lo.x) / PathCell), Mathf.RoundToInt((_hi.z - _lo.z) / PathCell) },
                out pathWhy);
            if (_path == null)
            {
                Say("FAIL\t" + side + "\tA* 求不出走廊路径: " + pathWhy);
                Finish();
                return;
            }
        }

        a.Position = new Vector3(_lo.x, _lo.y + 0.4f, _lo.z);   // SETUP（⛔ 不是走上去的证据）
        a.Velocity = Vector3.zero;
        a.OnGround = false;
        _prev = a.Position;
        _prevY = a.Position.y;
        _wp = 0;
        _settle = 0;
        _frame = 0;
        _clampedFrames = 0;
        _slideFrames = 0;
        _sumMoved = 0f;
        _topped = false;
        _stuck = false;
        _stuckFrame = -1;
        _phase6Logged = false;
        _maxFramesReported = 0;

        if (_straight)
        {
            var d = new Vector3(_hi.x - _lo.x, 0f, _hi.z - _lo.z);
            _fixedYaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }
        else
        {
            _fixedYaw = float.NaN;
        }
        _recentMoved.Clear();

        Say("");
        Say("=== CASE " + (_case + 1) + "/" + Cases.Length + "  " + side + "  route=" + route +
            "  bottom=" + V3(_lo) + " -> top=" + V3(_hi) +
            "  |dxz|=" + F3(Horiz(_lo, _hi)) + " rise=" + F3(_hi.y - _lo.y) + " ===");
        Say("SETUP\t" + side + "\t目标：" + (_straight ? ("直线（只设一次 yaw=" + F3(_fixedYaw) + "，之后不再转向）")
                                                          : ("走廊路径 " + _path.Count + " 格")) +
            "\t起点由本组件摆放（⛔ 非证据）");
        WriteState(mode: _straight ? "straight" : "path", move: false, yaw: 0f);
        _phase = 2;
        _settle = 0;
    }

    /// <summary>等角色落地稳定（SETUP 之后、按前进键之前）。</summary>
    private void Settle(float now)
    {
        var a = _match.LocalPlayer;
        if (a == null) { Throttled("等 LocalPlayer"); return; }
        if (!a.IsAlive) { Throttled("本地玩家已阵亡，等复活"); return; }
        if (_match.Phase != CsRoundPhase.Live) { Throttled("相位离开 Live（phase=" + _match.Phase + "）⇒ 重摆起点"); _phase = 1; return; }
        var grounded = a.OnGround;
        var dy = Mathf.Abs(a.Position.y - _prevY);
        _prevY = a.Position.y;
        if (grounded && dy < 0.03f) _settle++; else _settle = 0;
        if (!_phase6Logged || _settle % 30 == 0)
        {
            Say("SETTLE\t" + Cases[_case][0] + "\tpos=" + V3(a.Position) + "\tonGround=" + grounded +
                "\tphase=" + _match.Phase + "\talive=" + a.IsAlive + "\tsettle=" + _settle +
                "\t离起点=" + F3(Horiz(_lo, a.Position)));
            _phase6Logged = true;
        }
        if (_settle < SettleFrames) return;

        _start = a.Position;
        _prev = a.Position;
        _sumMoved = 0f;
        _startTime = now;
        Say("WALK-BEGIN\t" + Cases[_case][0] + "\tpos=" + V3(_start) + "\tyaw0=" + F3(a.Yaw) +
            "\tphase=" + _match.Phase + "\tround=" + _match.RoundNumber + "\tt=" + F3(now));
        _phase = 3;
    }

    private void Walk(float now)
    {
        var a = _match.LocalPlayer;
        if (a == null || !a.IsAlive)
        {
            Say("ABORT\t" + Cases[_case][0] + "\t玩家没了（alive=" + (a != null && a.IsAlive) + "）");
            Finish();
            return;
        }
        _frame++;
        var pos = a.Position;
        var toGoal = new Vector3(_hi.x - pos.x, 0f, _hi.z - pos.z);
        var dGoal = toGoal.magnitude;

        float yaw;
        string wpTag;
        if (_straight)
        {
            yaw = _fixedYaw;                       // 直线复现：一次设定、不再转向
            wpTag = "fixed";
        }
        else
        {
            while (_wp < _path.Count && Horiz(pos, CellCenter(_lo, _path[_wp][0], _path[_wp][1])) < Reach) _wp++;
            if (_wp >= _path.Count)
            {
                FinishCase(pos, now, true);
                return;
            }
            var t = CellCenter(_lo, _path[_wp][0], _path[_wp][1]);
            yaw = Mathf.Atan2(t.x - pos.x, t.z - pos.z) * Mathf.Rad2Deg;
            wpTag = _wp + "/" + _path.Count;
        }
        WriteState(mode: _straight ? "straight" : "path", move: true, yaw: yaw);

        var moved = Horiz(_prev, pos);
        var heading = Mathf.Atan2(pos.x - _prev.x, pos.z - _prev.z) * Mathf.Rad2Deg;
        var dYaw = Mathf.Abs(Mathf.DeltaAngle(yaw, heading));
        var clamped = moved < ClampEps;
        var sliding = !clamped && moved < 0.02f * 4f && dYaw > 25f;
        if (clamped) _clampedFrames++;
        if (sliding) _slideFrames++;
        _sumMoved += moved;

        Say("F\t" + _frame + "\t" + Cases[_case][0] + "\tpos=" + V3(pos) + "\tcell=(" +
            Mathf.FloorToInt(pos.x + 63f) + "," + Mathf.FloorToInt(pos.z + 72f) + ")" +
            "\twp=" + wpTag + "\tdGoal=" + F3(dGoal) + "\tdy=" + F3(_hi.y - pos.y) +
            "\tmoved=" + F3(moved) + "\tyaw=" + F3(yaw) + "\thead=" + F3(heading) + "\tdYaw=" + F3(dYaw) +
            "\tground=" + a.OnGround + "\tspeed=" + F3(CsInventory.MovementSpeed(a)) +
            "\t" + (clamped ? "CLAMPED" : (sliding ? "SLIDE" : "ok")));
        _prev = pos;

        // 滑动窗口：最近 60 帧的总位移（判"卡死"用）
        _recentMoved.Add(moved);
        if (_recentMoved.Count > 60) _recentMoved.RemoveAt(0);

        // 到顶判据：水平残差 ≤ Reach 且 高度差 ≤ 0.35（走廊路径终点 = 顶标记点）
        if (dGoal <= Reach && Mathf.Abs(_hi.y - pos.y) <= 0.35f)
        {
            FinishCase(pos, now, true);
            return;
        }
        // 卡死：最近 60 帧总位移 < 0.3 m（≈5 m/s 下 1 秒的 6%）
        if (_frame > 60 && SumRecent() < 0.3f)
        {
            _stuck = true;
            _stuckFrame = _frame;
            _stuckPos = pos;
            Say("STUCK\t" + Cases[_case][0] + "\tframe=" + _frame + "\tpos=" + V3(pos) + "\tdGoal=" + F3(dGoal) +
                "\t已走=" + F3(Horiz(_start, pos)) + "\t剩余=" + F3(dGoal) + "\tclampedFrames=" + _clampedFrames);
            FinishCase(pos, now, false);
            return;
        }
        if (_frame >= MaxFramesPerCase || (now - _startTime) > MaxSecondsPerCase)
        {
            Say("TIMEOUT\t" + Cases[_case][0] + "\tframe=" + _frame + "\tt=" + F3(now - _startTime));
            FinishCase(pos, now, false);
        }
    }

    private float SumRecent()
    {
        var s = 0f;
        for (var i = 0; i < _recentMoved.Count; i++) s += _recentMoved[i];
        return s;
    }

    private void FinishCase(Vector3 pos, float now, bool topped)
    {
        _topped = topped;
        var side = Cases[_case][0];
        var res = Horiz(pos, _hi);
        Say("CASE-SUMMARY\t" + side + "\tframes=" + _frame + "\t被钳住帧=" + _clampedFrames +
            "\t贴墙滑行帧=" + _slideFrames + "\t到顶=" + topped + "\t终点=" + V3(pos) +
            "\t目标=" + V3(_hi) + "\t平面残差=" + F3(res) + "\t高度差=" + F3(pos.y - _hi.y) +
            "\t已走=" + F3(Horiz(_start, pos)) + "\twall=" + F3(now - _startTime) + "s" +
            (_stuckFrame > 0 ? "\t卡在第 " + _stuckFrame + " 帧" : ""));
        WriteState(mode: "idle", move: false, yaw: 0f);
        _case++;
        if (_case >= Cases.Length) { Finish(); return; }
        _phase = 1;
    }

    private void Finish()
    {
        _done = true;
        _verdict = "DONE cases=" + _case;
        Say("");
        Say("SUMMARY\t" + _verdict + "\t（逐 case 见上面的 CASE-SUMMARY 行）");
        try { if (_out != null) _out.Flush(); } catch { }
    }

    private void WriteState(string mode, bool move, float yaw)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("# written by tools/probes/real-walk-bstairs.cs (order -160) -- mode=").Append(mode).Append('\n');
            sb.Append("godmode=1\n");
            sb.Append("track=1\n");
            if (move)
            {
                // 9 字段：moveX,moveY,yaw,pitch,jump,crouch,walk,fire,zoom —— moveY=1 = 按住前进
                sb.Append("input=0,1,").Append(yaw.ToString("F3", CultureInfo.InvariantCulture))
                  .Append(",0,0,0,0,0,0\n");
            }
            File.WriteAllText(RealWalkBStairs.StatePath, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e) { Say("WARN\t写 state.txt 失败: " + e.Message); }
    }

    // ── 走廊路径：与 bstairs-walkline.cs **同一份口径**（8 邻接 / 直 10 斜 14 / 对角要两侧正交格可走）──
    private static Vector3 CellCenter(Vector3 lo, int i, int j)
        => new Vector3(lo.x + i * PathCell, lo.y, lo.z + j * PathCell);

    private static bool WalkableCell(CsMap map, Vector3 lo, int i, int j)
        => map.WalkableAt(lo.x + i * PathCell, lo.z + j * PathCell);

    private static float Horiz(Vector3 a, Vector3 b)
    {
        var dx = b.x - a.x; var dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static int H(int[] a, int[] b)
    {
        var dx = Mathf.Abs(a[0] - b[0]); var dy = Mathf.Abs(a[1] - b[1]);
        var lo = Mathf.Min(dx, dy); var hi2 = Mathf.Max(dx, dy);
        return 10 * hi2 + 4 * lo;
    }

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
}
