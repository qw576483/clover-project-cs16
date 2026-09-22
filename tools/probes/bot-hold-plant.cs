// ============================================================================
// 判据资产 · 切片BH：**AI 运行时证据**（守点分布 / 换位 / T 下包）—— 逐帧只读快照。
//
// 为什么必须进 Play：被判的三件事都只存在于**运行中的模拟**里 ——
//   `CsBotBrain`（守位表 / 换位）与 `CsMatch`（下包结算）只在 Play 的 Tick 里跑；
//   离线脚本能判"守位表能不能构造出来"（`tools/probes/bot-hold-plant-check.py` 的 A/B 段），
//   但判不了"这一回合里 N 个 CT 到底走到了哪、换没换位、T 有没有真的把包放下"。
//
// 本探针**只读**（不写 state.txt、不下输入、不改任何 actor / 赛局状态）：
//   ① 每帧快照（采样 20 Hz）每个 actor 的位置 / 阵营 / 是否持包 / 下包进度 / 到 A·B 包点标记的距离；
//   ② **原样转发程序自己写的 L3 标记**（`Game.Logger.Info` 的 `守点换位：…` / `守位表就绪：…` / `[C4] …`）
//      —— 证据分级见 skill §4.7：L3（被测程序运行时自己写的标记）> L2（脚本采集产物）> L1（文字）；
//      日志行**由业务代码自己写**，本探针只负责把它连同 frame/t 记下来（⛔ 不重新解释、不加工）；
//   ③ 由 ① 派生的三个显式事件（都带秒数，离线断言直接读数字）：
//      `E CTSITE`      某 CT 首次进入某包点判定区（= 守点分布的分母/分子）
//      `E TARRIVE`     T 携 C4 首次进入包点判定区
//      `E TPLANTSTART` 该持包者 `UseProgress` 首次 > 0（= 开始下包）
//      `E TPLANTED`    全局 `BombPlanted` 翻真（= 回合内真的下了包）
//
// 包点判定区口径（⛔ 与产品同源，不另立定义）：到该包点**任一**标记点的水平距离
//   ≤ `CsMarkers.BombsiteRadius`（`Module/Map/ICsMap.cs:83`）—— 即 `CsBomb.IsInBombsite`
//   （`Module/Match/CsBomb.cs`）与 `CsBotBrain.NearestBombsitePoint` 用的同一个判据。
//
// 用法（在 client/ 里跑；需要先进入 Play 且驱动已开一局）：
//   unity command run_script --file <本文件绝对路径> --entry BotHoldPlant.Begin
//   ... 让游戏自己跑（逐回合）...
//   unity command run_script --file <本文件绝对路径> --entry BotHoldPlant.Stop   （可选，落盘收尾）
// 产物：
//   <项目根>/.ai-tmp/test/bh-hold-plant.tsv        逐帧 actor 快照 + 事件行
//   <项目根>/.ai-tmp/test/bh-hold-plant-log.tsv    程序自己写的日志行（原样转发，L3 载体）
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CloverEngine;
using Cs16.Core;
using Cs16.Module.Map;
using Cs16.Module.Match;
using UnityEngine;

public static class BotHoldPlant
{
    public const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\bh-hold-plant.tsv";
    public const string LogPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\.ai-tmp\test\bh-hold-plant-log.tsv";

    public static string Begin()
    {
        var go = new GameObject("BotHoldPlantTick");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.AddComponent<BotHoldPlantTick>();
        return "BotHoldPlant started (order=200) -> " + OutPath;
    }

    public static string Stop()
    {
        var t = UnityEngine.Object.FindAnyObjectByType<BotHoldPlantTick>(FindObjectsInactive.Include);
        if (t == null) return "BotHoldPlant: 没有在跑的探针";
        t.FinishProbe();
        return "BotHoldPlant stopped -> " + OutPath;
    }
}

/// <summary>order 200：在所有业务 Tick（order 0 / PlayerModule −200 / 驱动 −150）之后快照 ⇒ 采到的是**本帧结算后**的状态。</summary>
[DefaultExecutionOrder(200)]
public sealed class BotHoldPlantTick : MonoBehaviour
{
    private const string Tag = "bh-holdplant";
    private const int SampleEveryFrames = 3;      // 60 fps ⇒ 20 Hz
    private const float SiteEps = 1e-3f;

    private StreamWriter _out;
    private StreamWriter _log;
    private int _frame;
    private int _lastRound = -1;
    private CsRoundPhase _lastPhase = CsRoundPhase.None;

    private MatchModule _mod;
    private ICsMatch _match;
    private ICsMap _map;

    private Vector3[] _aPts;
    private Vector3[] _bPts;
    private float _siteRadius = CsMarkers.BombsiteRadius;

    // 本回合的派生状态（每回合重置）
    private readonly HashSet<long> _ctArrived = new HashSet<long>();
    private readonly Dictionary<long, float> _ctArriveT = new Dictionary<long, float>();
    private readonly Dictionary<long, string> _ctSiteLabel = new Dictionary<long, string>();
    private long _carrierId = -1;
    private float _carrierArriveT = -1f;
    private bool _plantedSeenThisRound;
    private bool _bombWasPlanted;

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private void Awake()
    {
        Application.runInBackground = true;
        try
        {
            var dir = Path.GetDirectoryName(BotHoldPlant.OutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _out = new StreamWriter(BotHoldPlant.OutPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            _log = new StreamWriter(BotHoldPlant.LogPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception e) { Game.Logger?.Error(Tag, "打不开输出文件: " + e.Message); }

        Application.logMessageReceived += OnLog;
        Say("E\tPROBE\tBEGIN\tframe=0\tt=" + F(Time.realtimeSinceStartup) +
            "\torder=200\tsampleEvery=" + SampleEveryFrames);
    }

    private void OnDestroy()
    {
        Application.logMessageReceived -= OnLog;
        try { _out?.Flush(); _out?.Dispose(); _log?.Flush(); _log?.Dispose(); } catch { }
        _out = null;
        _log = null;
    }

    /// <summary>外部 `BotHoldPlant.Stop` 调：写一条收尾行（⛔ 不删文件，产物留给人复核）。</summary>
    public void FinishProbe()
    {
        Say("E\tPROBE\tSTOP\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup));
        try { _out?.Flush(); _log?.Flush(); } catch { }
    }

    private void Say(string line)
    {
        try { _out?.WriteLine(line); } catch { }
    }

    // ── L3 转发：业务代码自己写的日志，原样记下（含 frame/t），不做任何加工 ──────────
    private void OnLog(string msg, string stack, LogType type)
    {
        try { _log?.WriteLine(_frame + "\t" + F(Time.realtimeSinceStartup) + "\t" + type + "\t" + msg); } catch { }

        if (msg == null) return;
        if (msg.IndexOf("守点换位", StringComparison.Ordinal) >= 0)
            Say("E\tSWAP\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) + "\tmsg=" + msg);
        else if (msg.IndexOf("守位表就绪", StringComparison.Ordinal) >= 0)
            Say("E\tHOLDTABLE\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) + "\tmsg=" + msg);
        else if (msg.IndexOf("[C4]", StringComparison.Ordinal) >= 0)
            Say("E\tC4LOG\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) + "\tmsg=" + msg);
    }

    private void Update()
    {
        _frame++;
        if (_mod == null)
        {
            _mod = MatchModule.Instance;
            if (_mod == null) return;
        }
        _match = _match ?? _mod.Match;
        _map = _map ?? _mod.Map;
        if (_match == null || _map == null) return;

        LoadSitePoints();

        var round = _match.RoundNumber;
        var phase = _match.Phase;

        if (round != _lastRound)
        {
            _lastRound = round;
            ResetRound();
            Say("E\tROUND\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) + "\tround=" + round +
                "\tphase=" + phase + "\tmapLoaded=" + _map.IsLoaded);
        }
        if (phase != _lastPhase)
        {
            _lastPhase = phase;
            Say("E\tPHASE\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                "\tround=" + round + "\tphase=" + phase + "\tphaseLeft=" + F(_match.PhaseTimeLeft) +
                "\tscoreCT=" + _match.ScoreCT + "\tscoreT=" + _match.ScoreT);
        }

        var planted = _match.BombPlanted;
        if (planted && !_bombWasPlanted)
        {
            _bombWasPlanted = true;
            var bp = _match.BombPosition;
            var dt = _carrierArriveT >= 0f ? Time.realtimeSinceStartup - _carrierArriveT : -1f;
            Say("E\tTPLANTED\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                "\tround=" + round + "\tcarrier=" + _carrierId + "\tarriveT=" + F(_carrierArriveT) +
                "\tarriveToPlantedSec=" + F(dt) + "\tbombPos=" + F(bp.x) + "," + F(bp.y) + "," + F(bp.z));
        }

        var actors = _match.Actors;
        var doSample = (_frame % SampleEveryFrames) == 0;

        for (var i = 0; i < actors.Count; i++)
        {
            var a = actors[i];
            if (a == null) continue;

            var dA = NearestXZ(a.Position, _aPts);
            var dB = NearestXZ(a.Position, _bPts);
            var inA = dA <= _siteRadius + SiteEps;
            var inB = dB <= _siteRadius + SiteEps;
            var label = inA && inB ? (dA <= dB ? "A" : "B") : (inA ? "A" : (inB ? "B" : "-"));

            if (a.Team == CsTeam.CT && a.IsAlive && label != "-")
            {
                if (!_ctArrived.Contains(a.Id))
                {
                    _ctArrived.Add(a.Id);
                    _ctArriveT[a.Id] = Time.realtimeSinceStartup;
                    Say("E\tCTSITE\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                        "\tround=" + round + "\tactor=" + a.Name + "\tid=" + a.Id +
                        "\tsite=" + label + "\tphase=" + phase +
                        "\tdistA=" + F(dA) + "\tdistB=" + F(dB));
                }
                if (!_ctSiteLabel.TryGetValue(a.Id, out var prev) || prev != label)
                {
                    _ctSiteLabel[a.Id] = label;
                    Say("E\tCTSITECHANGE\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                        "\tround=" + round + "\tactor=" + a.Name + "\tid=" + a.Id +
                        "\tfrom=" + (prev ?? "?") + "\tto=" + label);
                }
            }

            if (a.HasBomb)
            {
                if (_carrierId != a.Id)
                {
                    _carrierId = a.Id;
                    Say("E\tCARRIER\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                        "\tround=" + round + "\tactor=" + a.Name + "\tid=" + a.Id +
                        "\tlive=" + (a.IsAlive) + "\tdistA=" + F(dA) + "\tdistB=" + F(dB));
                }
                if (a.IsAlive && label != "-" && _carrierArriveT < 0f)
                {
                    _carrierArriveT = Time.realtimeSinceStartup;
                    Say("E\tTARRIVE\tframe=" + _frame + "\tt=" + F(_carrierArriveT) +
                        "\tround=" + round + "\tactor=" + a.Name + "\tid=" + a.Id +
                        "\tsite=" + label + "\tphase=" + phase + "\tdistA=" + F(dA) + "\tdistB=" + F(dB));
                }
                if (a.IsAlive && a.UseProgress > 0f && !_plantedSeenThisRound)
                {
                    _plantedSeenThisRound = true;
                    var dt = _carrierArriveT >= 0f ? Time.realtimeSinceStartup - _carrierArriveT : -1f;
                    Say("E\tTPLANTSTART\tframe=" + _frame + "\tt=" + F(Time.realtimeSinceStartup) +
                        "\tround=" + round + "\tactor=" + a.Name + "\tid=" + a.Id +
                        "\tuseProgress=" + F(a.UseProgress) +
                        "\tarriveT=" + F(_carrierArriveT) + "\tarriveToPlantStartSec=" + F(dt));
                }
            }

            if (!doSample) continue;
            Say("A\t" + _frame + "\t" + F(Time.realtimeSinceStartup) + "\t" + round + "\t" + phase + "\t" +
                a.Name + "\t" + a.Id + "\t" + a.Team + "\t" + (a.IsBot ? 1 : 0) + "\t" + (a.IsAlive ? 1 : 0) + "\t" +
                a.Health + "\t" + F(a.Position.x) + "\t" + F(a.Position.y) + "\t" + F(a.Position.z) + "\t" +
                F(a.Yaw) + "\t" + (a.HasBomb ? 1 : 0) + "\t" + F(a.UseProgress) + "\t" +
                F(dA) + "\t" + F(dB) + "\t" + label + "\t" + (planted ? 1 : 0) + "\t" +
                (a.Team == CsTeam.CT && _ctArriveT.TryGetValue(a.Id, out var ct0) && ct0 > 0f ? F(ct0) : "-1.000"));
        }
    }

    private void ResetRound()
    {
        _ctArrived.Clear();
        _ctArriveT.Clear();
        _ctSiteLabel.Clear();
        _carrierId = -1;
        _carrierArriveT = -1f;
        _plantedSeenThisRound = false;
        _bombWasPlanted = _match != null && _match.BombPlanted;
    }

    private void LoadSitePoints()
    {
        if (_aPts != null && _bPts != null) return;
        _aPts = _map.Points(CsMarkers.BombsiteA) ?? new Vector3[0];
        _bPts = _map.Points(CsMarkers.BombsiteB) ?? new Vector3[0];
        Say("E\tSITEPTS\tframe=" + _frame + "\tA=" + _aPts.Length + "\tB=" + _bPts.Length +
            "\tradius=" + F(_siteRadius) + "\tmapLoaded=" + _map.IsLoaded + "\tmap=" + _map.MapName);
    }

    private static float NearestXZ(Vector3 p, Vector3[] pts)
    {
        if (pts == null || pts.Length == 0) return float.MaxValue;
        var best = float.MaxValue;
        for (var i = 0; i < pts.Length; i++)
        {
            var dx = pts[i].x - p.x;
            var dz = pts[i].z - p.z;
            var d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < best) best = d;
        }
        return best;
    }
}
