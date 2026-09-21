// ============================================================================
// 判据资产：CsActorSeparation 的**离线断言**（用户报「人物和人物能重合」的机器判据）。
//
// 为什么是 C# 而不是 Python：被断言的是**真代码**（Cs16.Module.Map.CsActorSeparation）。
// 用 Python 复刻一遍公式只会得到"两份实现互相印证"的假证据 —— 判据必须跑在被判的对象上。
// 这里跑的是编译后的工程程序集（`run_script` 把本文件编进编辑器内存并调用静态入口），
// 不起 Play、不建场景，秒级。
//
// 用法（每条命令都带 --project-path，或在 client/ 里跑）：
//   unity command run_script --file <本文件绝对路径> --entry ActorSeparationCheck.Run --format tsv
//
// 输出：一行 `PASS n / FAIL m` + 逐条断言 + 明细 ⇒ 同时写盘到
//   <项目根>/.ai-tmp/test/actor-separation-check.txt（供 tools/probes/enumerate-entities.py 读）。
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;
using Cs16.Core;
using Cs16.Module.Map;

public static class ActorSeparationCheck
{
    // 切片R：报告是**判据资产**（D9 行离线断言的证据，删了就不能复核同一件事）⇒ 落 tools/probes/，
    // 不再放一次性目录 .ai-tmp/test/（skill §8）。读取方 tools/probes/geom-check.py 的口径同步。
    private const string OutPath =
        @"C:\Work\Server\f-v2\clover-project-cs16\tools\probes\actor-separation-check.txt";

    private static readonly StringBuilder Sb = new StringBuilder();
    private static int _pass;
    private static int _fail;

    private static void Check(bool ok, string name, string detail)
    {
        if (ok) { _pass++; Sb.AppendLine("PASS  " + name + "  " + detail); }
        else { _fail++; Sb.AppendLine("FAIL  " + name + "  " + detail); }
    }

    private static float Dist(float ax, float az, float bx, float bz)
    {
        var dx = ax - bx; var dz = az - bz;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    private static string F(float v) => v.ToString("F4", CultureInfo.InvariantCulture);

    public static string Run()
    {
        Sb.Clear();
        _pass = 0;
        _fail = 0;

        // 半径/最小间距的口径来自工程常量（⛔ 不写死 0.72：常量改了断言要跟着改）
        var r = CsConst.PlayerRadius;
        var minSep = r * 2f;
        Sb.AppendLine("# CsActorSeparation 离线断言（半径口径 = CsConst.PlayerRadius = " + F(r) +
                      " ⇒ 最小间距 " + F(minSep) + "）");
        Sb.AppendLine("# 判定：两个角色水平间距 < 2xPlayerRadius 即「能重合」（原版不允许）");

        // ---- ① 完全重合（用户看到的那种）：应被推到 ≥ 最小间距 ----
        {
            var others = new[]
            {
                new CsActorSeparation.ActorCircle { Id = 12, X = 0f, Z = 0f }
            };
            var ok = CsActorSeparation.TryResolve(0f, 0f, r, others, 1, out var x, out var z);
            var d = Dist(x, z, 0f, 0f);
            Check(ok && d >= minSep - 1e-3f, "① 同位被推开",
                  "mover(0,0) vs other(0,0) → (" + F(x) + "," + F(z) + ") 间距 " + F(d) + " ≥ " + F(minSep));
        }

        // ---- ② 微小重叠（差 2 cm）：也要推出到 ≥ 最小间距 ----
        {
            var others = new[]
            {
                new CsActorSeparation.ActorCircle { Id = 12, X = 0.70f, Z = 0f }
            };
            var ok = CsActorSeparation.TryResolve(0f, 0f, r, others, 1, out var x, out var z);
            var d = Dist(x, z, 0.70f, 0f);
            Check(ok && d >= minSep - 1e-3f, "② 微小重叠被推出",
                  "间距 0.7000 → " + F(d) + " ≥ " + F(minSep));
        }

        // ---- ③ 推开方向 = 远离对方（不许反而推过去）----
        {
            var others = new[]
            {
                new CsActorSeparation.ActorCircle { Id = 12, X = 0.10f, Z = 0f }
            };
            CsActorSeparation.TryResolve(0f, 0f, r, others, 1, out var x, out var z);
            var d = Dist(x, z, 0.10f, 0f);
            // 判据 = 最终**间距**达标 且 人往 -x 侧（远离对方）挪，不是"x 必须超过最小间距"：
            // 对方本来就在 0.10 处，只要推到"刚好相切 + 1mm"就够（推过头反而会让被推的人漂移过大）。
            Check(d >= minSep - 1e-3f && x < 0f, "③ 方向 = 远离对方",
                  "对方在 +x=0.10 ⇒ 推到 x=" + F(x) + "，最终间距 " + F(d) + " ≥ " + F(minSep));
        }

        // ---- ④ 本来就不重叠：位置不许被改动 ----
        {
            var others = new[]
            {
                new CsActorSeparation.ActorCircle { Id = 12, X = 1.5f, Z = 0f }
            };
            var ok = CsActorSeparation.TryResolve(0.25f, 0.5f, r, others, 1, out var x, out var z);
            Check(ok && Math.Abs(x - 0.25f) < 1e-6f && Math.Abs(z - 0.5f) < 1e-6f, "④ 无重叠不动",
                  "→ (" + F(x) + "," + F(z) + ") 应 = (0.2500,0.5000)");
        }

        // ---- ⑤ 三人堆在一点：对**每个**他人的间距都要达标 ----
        {
            var others = new[]
            {
                new CsActorSeparation.ActorCircle { Id = 3, X = 0f, Z = 0f },
                new CsActorSeparation.ActorCircle { Id = 4, X = 0f, Z = 0f }
            };
            var ok = CsActorSeparation.TryResolve(0f, 0f, r, others, 2, out var x, out var z);
            var d0 = Dist(x, z, 0f, 0f);
            var d1 = Dist(x, z, 0f, 0f);
            Check(ok && d0 >= minSep - 1e-3f && d1 >= minSep - 1e-3f, "⑤ 人堆里推出",
                  "两人都在原点 → (" + F(x) + "," + F(z) + ") 间距 " + F(d0) + "/" + F(d1));
        }

        // ---- ⑥ 确定性：同一输入连跑两次结果逐位相同（⛔ 不许用随机方向）----
        {
            var others = new[]
            {
                new CsActorSeparation.ActorCircle { Id = 9, X = 0f, Z = 0f }
            };
            CsActorSeparation.TryResolve(0f, 0f, r, others, 1, out var x1, out var z1);
            CsActorSeparation.TryResolve(0f, 0f, r, others, 1, out var x2, out var z2);
            Check(x1 == x2 && z1 == z2, "⑥ 确定性", "两次 = (" + F(x1) + "," + F(z1) + ") / (" + F(x2) + "," + F(z2) + ")");
        }

        // ---- ⑦ 恒等：没有"他人"时位置原样 ----
        {
            var ok = CsActorSeparation.TryResolve(1.25f, -3.5f, r, null, 0, out var x, out var z);
            Check(ok && x == 1.25f && z == -3.5f, "⑦ 无他人恒等", "→ (" + F(x) + "," + F(z) + ")");
        }

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
