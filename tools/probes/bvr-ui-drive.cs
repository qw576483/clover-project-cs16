// 判据资产（tools/probes/，片BV-R 新增）：UI 流程态驱动用的**业务门面**调用（⛔ 不另造通路）。
//
// 为什么需要它：16 态枚举里有两个态的"退出"没有可按的按钮路径可复用 ——
//   * Options 站点：面板只用 Ok/Cancel/Apply 关自己（OptionsPanel.cs:633 起的 BindDialogButtons），
//     按名字点按钮容易点错；这里调的就是 Cancel 处理器里的同一行 `Game.UI.Close<OptionsPanel>()`。
//   * Pause 站点：`SetPaused(true)` 不切 FSM（见 cs16-play-driver.cs:1552 的注释），恢复必须发
//     `Events.Resume`（= AppFlow 订阅的那个 Resume 事件，AppFlow.cs:158）。
// 用 run_script 调（--file <本文件> --entry Entry.X）。只调业务对外门面，⛔ 不改任何游戏代码。
using CloverEngine;
using Cs16.Core;

public static class Entry
{
    public static string Resume()
    {
        var bus = Game.Event;
        if (bus == null) return "ERROR: Game.Event null";
        bus.Emit(Events.Resume);
        return "Resume emitted";
    }

    public static string CloseOptions()
    {
        var ui = Game.UI;
        if (ui == null) return "ERROR: Game.UI null";
        ui.Close<Cs16.UI.OptionsPanel>();
        return "OptionsPanel closed";
    }

    public static string FsmState()
    {
        return "fsm=" + (Game.Fsm != null ? (Game.Fsm.Current ?? "<null>") : "<Game.Fsm null>");
    }
}
