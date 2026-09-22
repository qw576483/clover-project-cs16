// ============================================================================
// 判据资产：CsBotProfile（机器人 3 档难度参数）的**离线断言** —— 验收表 B1~B3 行的
//           机器锚点（这些行 declare 的期望值就是"反应时间区间 / 瞄准误差上限"）。
//
// 为什么是 C# 而不是 Python：被断言的是**真代码**
//   （Cs16.Module.Match.CsBotProfile.For，源 = client/Assets/Scripts/Module/Match/CsTypes.cs:148）。
//   用 Python 复刻一遍常量只会得到"两份实现互相印证"的假证据 —— 判据必须跑在被判的对象上。
// `run_script` 编译的是**已加载**的 Assembly-CSharp 并在编辑器内存里执行静态入口，
//   不起 Play、不建场景、不写业务状态，秒级。
//
// 用法（在 client/ 目录里跑；端口以 `unity status` 实测为准）：
//   unity command run_script --file <本文件绝对路径> --entry BotProfileCheck.Run --format tsv
// 输出：逐条 PASS/FAIL + `SUMMARY PASS=n FAIL=m`，并落盘到
//   <项目根>/tools/probes/bot-profile-check.txt（验收表 B2/B3 行的 anchor 指向它）。
//
// 期望区间出处：策划/策划案/CS1.6单机参考规格.md:117-121（§2.4 三档表）
//   Easy  0.5~0.8s / ±6°    Normal 0.25~0.4s / ±3°    Hard 0.1~0.2s / ±1.2°
// ⛔ 本探针只读；不改任何业务状态。
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Cs16.Core;
using Cs16.Module.Match;

public static class BotProfileCheck
{
    private const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\bot-profile-check.txt";

    private static readonly StringBuilder Sb = new StringBuilder();
    private static int _pass;
    private static int _fail;

    private static void Check(bool ok, string name, string detail)
    {
        if (ok) { _pass++; Sb.AppendLine("PASS  " + name + "  " + detail); }
        else { _fail++; Sb.AppendLine("FAIL  " + name + "  " + detail); }
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // 断言：CsBotProfile.For(d) 的反应时间落在规格区间内、瞄准误差不超过规格上限。
    private static void Tier(CsBotDifficulty d, float rtMin, float rtMax, float aimMax)
    {
        var p = CsBotProfile.For(d);
        var rtOk = p.ReactionTime >= rtMin && p.ReactionTime <= rtMax;
        var aimOk = p.AimErrorDegrees <= aimMax + 1e-4f;
        Check(rtOk && aimOk, d + " 档参数",
              "反应=" + F(p.ReactionTime) + "s in [" + F(rtMin) + "," + F(rtMax) + "]  " +
              "瞄准误差=+/-" + F(p.AimErrorDegrees) + "deg <= +/-" + F(aimMax) + "deg");
    }

    public static string Run()
    {
        Sb.Clear();
        _pass = 0;
        _fail = 0;

        Sb.AppendLine("# CsBotProfile 离线断言（规格 S2.4 三档表 -> CsTypes.cs:148 CsBotProfile.For）");
        Sb.AppendLine("# 区间出处：策划/策划案/CS1.6单机参考规格.md:117-121");

        // ---- ① 三档各自落在规格区间内（B1/B2/B3 的判据本体）----
        Tier(CsBotDifficulty.Easy, 0.5f, 0.8f, 6.0f);
        Tier(CsBotDifficulty.Normal, 0.25f, 0.4f, 3.0f);
        Tier(CsBotDifficulty.Hard, 0.1f, 0.2f, 1.2f);

        // ---- ② 过程判据：难度单调（Hard 反应更快、瞄得更准 <- Normal <- Easy）----
        //      ⛔ 只判"数据在区间内"不够 —— 三档若同值也能过区间断言；这一条堵住那个假绿。
        var easy = CsBotProfile.For(CsBotDifficulty.Easy);
        var normal = CsBotProfile.For(CsBotDifficulty.Normal);
        var hard = CsBotProfile.For(CsBotDifficulty.Hard);
        Check(hard.ReactionTime < normal.ReactionTime && normal.ReactionTime < easy.ReactionTime,
              "反应时间单调递减",
              "Hard=" + F(hard.ReactionTime) + " < Normal=" + F(normal.ReactionTime) +
              " < Easy=" + F(easy.ReactionTime));
        Check(hard.AimErrorDegrees < normal.AimErrorDegrees && normal.AimErrorDegrees < easy.AimErrorDegrees,
              "瞄准误差单调递减",
              "Hard=+/-" + F(hard.AimErrorDegrees) + " < Normal=+/-" + F(normal.AimErrorDegrees) +
              " < Easy=+/-" + F(easy.AimErrorDegrees));

        var head = "SUMMARY PASS=" + _pass + " FAIL=" + _fail;
        Sb.AppendLine(head);

        try
        {
            var dir = Path.GetDirectoryName(OutPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(OutPath, Sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception e)
        {
            Sb.AppendLine("WARN 写盘失败 " + OutPath + " : " + e.Message);
        }

        return head + " | " + OutPath;
    }
}
