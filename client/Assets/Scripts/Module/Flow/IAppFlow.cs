using Cs16.Core;

namespace Cs16.Module.Flow
{
    /// <summary>
    /// 启动与菜单流程编排的**唯一对外门面**（App 只持有它，不碰内部实现）。
    ///
    /// <para>
    /// 站点顺序（本单机版）：
    /// <c>Boot(启动画面) → MainMenu(主菜单) → [ServerList / Options / NewGame] → Loading(读条) →
    /// TeamSelect(选阵营) → Stage(比赛) ⇄ Pause(暂停) → MainMenu</c>。
    /// </para>
    ///
    /// <para>依赖方向是 <c>App → Module → Core</c>：App 构造它并注入依赖，它不反向找 App。</para>
    /// </summary>
    public interface IAppFlow
    {
        /// <summary>进入流程（进启动画面）。<c>Bootstrap</c> 在装配完之后调用一次。</summary>
        void Enter();

        /// <summary>当前站点名（<c>Game.Fsm.Current</c>），用于日志与自检。</summary>
        string CurrentState { get; }

        /// <summary>
        /// 读条并进图：加载 <c>StageDust2</c> + 地图数据，读完进选阵营，选完开打。
        /// （<c>Events.LaunchMatch</c> 也会走到这里。）
        /// </summary>
        void GoStage(CsMatchConfig cfg);

        /// <summary>回主菜单：按清场清单收拾干净，再单加载 Menu 场景（可反复进出）。</summary>
        void GoMainMenu();
    }
}
