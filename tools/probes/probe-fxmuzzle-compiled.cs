// 为什么要它：本轮踩过坑 —— 编辑脚本时 Unity 正在 Play，编译被推迟，于是 eval 打到的还是
// **旧签名**的 MuzzleFlash（报 "Number of parameters specified does not match the expected number"）。
// 判据：CsCombatTuning 找到 + 三个偏移常量取得到值 + CombatEffects.MuzzleFlash 的参数个数 == 3。
var sb = new System.Text.StringBuilder();
System.Type tune = null, fx = null;
var asms = System.AppDomain.CurrentDomain.GetAssemblies();
for (var i = 0; i < asms.Length; i++)
{
    if (tune == null) tune = asms[i].GetType("Cs16.Module.Combat.CsCombatTuning", false);
    if (fx == null) fx = asms[i].GetType("Cs16.Module.Combat.CombatEffects", false);
}
sb.Append("CsCombatTuning=").Append(tune != null ? tune.FullName : "NOT FOUND");
if (tune != null)
{
    foreach (var k in new[] { "MuzzleOffsetRight", "MuzzleOffsetUp", "MuzzleOffsetForward", "MuzzleFlashSize" })
    {
        var f = tune.GetField(k, System.Reflection.BindingFlags.Public |
                                 System.Reflection.BindingFlags.NonPublic |
                                 System.Reflection.BindingFlags.Static);
        sb.Append('\n').Append("  ").Append(k).Append("=")
          .Append(f == null ? "MISSING" : System.Convert.ToString(f.GetValue(null)));
    }
}
sb.Append('\n').Append("CombatEffects=").Append(fx != null ? fx.FullName : "NOT FOUND");
if (fx != null)
{
    var ms = fx.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
    for (var i = 0; i < ms.Length; i++)
    {
        if (ms[i].Name != "MuzzleFlash") continue;
        var ps = ms[i].GetParameters();
        sb.Append('\n').Append("  MuzzleFlash 参数个数=").Append(ps.Length).Append(" [");
        for (var j = 0; j < ps.Length; j++) sb.Append(ps[j].ParameterType.Name).Append(j + 1 < ps.Length ? "," : "");
        sb.Append(']');
    }
    sb.Append('\n').Append("  有 _sprFlashCross 字段=")
      .Append(fx.GetField("_sprFlashCross", System.Reflection.BindingFlags.NonPublic |
                                               System.Reflection.BindingFlags.Instance) != null);
}
return sb.ToString();
