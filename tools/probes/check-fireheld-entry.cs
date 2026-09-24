// 判据探针（tools/probes/，engine 内 eval_file 跑）：确认驱动取证所依赖的**类型化测试入口**
// `CombatModule.SetFireHeldForTest(bool)` 真的存在于**已加载的游戏程序集**里。
//
// 为什么需要它：
//   而 `unity command run_script` 编译的是**已加载**的 Assembly-CSharp —— 若编辑器中还没重编译
//   `CombatModule.cs`（recompile_status 说 idle 也可能是旧程序集），驱动会整条链 FAIL。
//   本探针用**反射查方法**（不直接调），所以哪怕入口不存在它也能编译，从而能明确回答
//   "是入口缺失，还是别的问题"。
//
// 用法（在 client/ 目录下跑）：
//   unity command eval_file --file <本文件绝对路径> --format tsv
// 期望结果：PASS typed entry present | assembly=Cs16 | public=True | sig=Void SetFireHeldForTest(Boolean)
//   ⇒ 若显示 FAIL ... missing，先在编辑器里跑 `unity command recompile` 并轮询 recompile_status。
//
// 只读，不改进程状态。
var t = typeof(Cs16.Module.Combat.CombatModule);
var m = t.GetMethod("SetFireHeldForTest");
var f = t.GetField("_fireRequested",
    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
var sb = new System.Text.StringBuilder();
sb.Append(m == null ? "FAIL: CombatModule.SetFireHeldForTest missing (editor did not recompile?)"
                    : "PASS typed entry present");
sb.Append(" | assembly=");
sb.Append(t.Assembly.GetName().Name);
if (m != null)
{
    sb.Append(" | public=");
    sb.Append(m.IsPublic);
    sb.Append(" | sig=");
    sb.Append(m.ToString());
}
sb.Append(" | private field _fireRequested still exists=");
sb.Append(f != null);
return sb.ToString();
