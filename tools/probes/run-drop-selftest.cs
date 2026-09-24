// 差异 #75 数值判据的入口（离线跑，不进 Play）。
// 用法：cd <项目根>/client && unity command eval_file --file <本文件绝对路径> --format tsv
// 只跑 #75 这一组（RunDropOnly），不走 RunAll —— RunAll 会撞 eval_file 的主线程 5 s 上限（实测过）。
return Cs16.Module.Combat.CombatSelfTest.RunDropOnly();
