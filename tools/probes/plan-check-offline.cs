// 判据资产（tools/probes/）：**离线**断言 CsBotPlans（4 槽位路线计划表）真的把同队 4 只 bot 岔开了
// —— 差异 #67 后半（用户 2026-09-24「为什么每个机器人的操作，路线都是相同的…为什么没有分工？？？」）。
//
// ⛔ 这是**离线**判据：不需要进 Play、不碰对局状态（CsBotPlans.SelfCheckWithNegativeControl 是纯函数 +
//    反射调用 BotSelfTest 的字符串入口）。同一份代码同时供运行时使用（口径同源，不另写一套表）。
//
// 判据：正控（未注入）必须 `RESULT-PLAN: PASS`，负控（注入"槽位 3 与槽位 0 同路同点" = 旧实现的撞车）
//       必须 `RESULT-PLAN: FAIL`；两条都对才 `RESULT-PLAN-NEGCTL: PASS`。
//
// 用法（编辑器在 Edit 模式即可）：
//   unity command eval_file --file tools/probes/plan-check-offline.cs --project-path <项目根>\client
return Cs16.Module.Bot.BotSelfTest.RoutePlanReport();
