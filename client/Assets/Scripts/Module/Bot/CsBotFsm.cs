using System;
using CloverEngine;
using Cs16.Module.Match;   // CsBotState（叶子状态枚举）定义在 Module/Match/CsTypes.cs

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人战术层状态机 —— **每个 bot 一棵**，实例由引擎 <see cref="Game.NewFsm"/> 提供。
    ///
    /// <para><b>引擎能力（本类只做壳，⛔ 不再复刻 <c>Fsm</c>）</b>：</para>
    /// <list type="number">
    /// <item><c>Game.NewFsm()</c>（<c>Runtime/Core/Game.cs:194</c>）返回一棵**独立**的
    /// <see cref="IFsm"/>：它有自己的 <c>_states/_transitions/_current</c>，与**应用级单例**
    /// <see cref="Game.Fsm"/>（<c>Game.cs:172</c>，由 <c>Game.cs:910</c> 的 <c>Force("Launching")</c> 起步、
    /// 由 <c>Game.Tick</c> 驱动）完全不共享。所以"每个 bot 各自一份状态"结构上成立：
    /// 注册到单例上的做法会让 8 个脑共用同一个 <see cref="IFsm.Current"/> 并互相覆盖。</item>
    /// <item>实现类 <c>Runtime/Core/Fsm.cs:100</c> 本轮已由 <c>internal</c> 提升为 <c>public</c> 并开放
    /// <c>NewFsm()</c> 工厂 ⇒ 转换语义**只有一份实现**：自环忽略（<c>Fsm.cs:296</c> 的
    /// <c>SwitchTo</c> + <c>:304</c> 的守卫 —— <c>Trigger</c> / <c>Force</c> 也走这条路）、回调内再转换排队补执行（<c>Fsm.cs:306-312</c>）、连锁转换上限 8
    /// （<c>Fsm.cs:107</c> 的 <c>MaxChainedSwitches</c>，<c>Fsm.cs:327-334</c> 中止并报错）、
    /// 未注册状态报 Error（<c>Fsm.cs:298-302</c>）、未注册触发器告警并忽略（<c>Fsm.cs:178-185</c>）、
    /// OnTick / OnChange 异常隔离（<c>Fsm.cs:204-214</c> / <c>Fsm.cs:365-372</c>）。
    /// ⛔ 本类不许再留第二份这五条语义。</item>
    /// <item><see cref="IFsm.Reset"/>（<c>Fsm.cs:276</c>）清状态表 / 触发器表 / <c>Current</c> 与
    /// "转换执行中 / 待补执行目标"标记（**不销毁实例**、**保留 OnChange 订阅**）—— 每回合复用同一棵树走它。</item>
    /// </list>
    ///
    /// <para><b>两条本类自己要保证的事</b>：</para>
    /// <list type="bullet">
    /// <item><b>初始状态</b>：引擎的 <see cref="IFsm"/> 注册完状态后 <see cref="IFsm.Current"/> 仍是
    /// <c>null</c>（引擎自身用法 = 注册完再 <c>Force</c> 首状态，见 <c>Game.cs:896-910</c> 的
    /// <c>InitFsm</c>）。本类把这步收进 <see cref="RegisterState"/>：首个注册的状态即为初始状态
    /// （等价于旧实现那句 <c>if (_current == null) _current = state</c>，调用方 <c>CsBotBrain.InitFsm</c>
    /// 只注册不 Force —— 换成引擎实例后必须补上这一步，否则 <c>Current</c> 一直是 null）。</item>
    /// <item><b><see cref="TransitionCount"/></b>：引擎 <c>Fsm</c> **不提供**转换计数出口（只有
    /// <see cref="IFsm.Current"/> 与 <see cref="IFsm.OnChange"/>）—— 这是裁决：由业务自己数。
    /// 本类在构造时挂一个内部 <see cref="IFsm.OnChange"/> 计数器（每个真实状态切换恰好回调一次：
    /// <c>Fsm.cs:347-357</c> 里 <c>ApplySwitch</c> 同状态直接 return、链式补执行也只对真实切换回调）。</item>
    /// </list>
    ///
    /// <para><b>三层结构（语义 = 规格 <c>策划/策划案/CS1.6单机参考规格.md:117-131</c> 那条"行为树"）</b>：
    /// <code>
    /// 顶层（每 bot 一份 <see cref="CsBotFsm"/>，<see cref="CsBotFsmStates"/>）：
    ///     Idle / Patrol / Engage / Objective
    /// 目标层（每 bot 一份，<see cref="CsBotObjectiveStates"/>）：
    ///     T  = Approach → Plant        （去包点 / 下包）
    ///     CT = Approach → Hold / Defuse（去守卫点驻守 / 拆包）
    /// </code>
    /// 大小写与命名只用于日志；转换由调用方（<see cref="CsBotBrain"/>）按带出处的守卫提交。</para>
    /// </summary>
    public sealed class CsBotFsm : IFsm
    {
        /// <summary>引擎提供的状态机实例（语义唯一真源；本类只转发 + 记初始状态 + 计数）。</summary>
        private readonly IFsm _fsm;

        /// <summary>本状态机的名字（日志用；多实例时能分辨是哪一个 bot 的哪一层）。</summary>
        private readonly string _label;

        /// <summary>累计转换次数（自检 / 日志用）。**引擎不提供该计数** ⇒ 本类自己数（见类注释）。</summary>
        public long TransitionCount { get; private set; }

        public CsBotFsm(string label)
        {
            _label = string.IsNullOrEmpty(label) ? "bot-fsm" : label;
            _fsm = Game.NewFsm();

            // 自己数转换次数：引擎 Fsm 只对外给 Current / OnChange（没有计数出口）。
            // 传具名 lambda 常量？不需要 —— 这里只挂一次，且 OnChange 会挡住同一引用的重复注册。
            _fsm.OnChange((from, to) => TransitionCount++);
        }

        /// <summary>本状态机的名字（日志用）。</summary>
        public string Label => _label;

        public string Current => _fsm.Current;

        public void RegisterState(string state, Action onEnter = null, Action<float> onTick = null, Action onExit = null)
        {
            _fsm.RegisterState(state, onEnter, onTick, onExit);

            // 首个注册的状态即初始状态（等价旧实现；引擎侧需要显式 Force，见类注释与 Game.cs:896-910）。
            // ⛔ 只做一次：Current 非 null 后不再 Force（重复 Force 会打日志/触发回调）。
            if (_fsm.Current == null) _fsm.Force(state);
        }

        public void AddTransition(string trigger, string toState)
        {
            _fsm.AddTransition(trigger, toState);
        }

        public void Transition(string toState)
        {
            _fsm.Transition(toState);
        }

        public void Trigger(string trigger)
        {
            _fsm.Trigger(trigger);
        }

        public void Force(string state)
        {
            _fsm.Force(state);
        }

        public void Tick(float dt)
        {
            _fsm.Tick(dt);
        }

        public void OnChange(Action<string, string> handler)
        {
            _fsm.OnChange(handler);
        }

        public void OffChange(Action<string, string> handler)
        {
            _fsm.OffChange(handler);
        }

        /// <summary>恢复到未初始化（回合开始/重开比赛时调用）：清空状态表与当前状态，等调用方重新注册。
        /// 语义 = 引擎 <c>Fsm.Reset</c>（<c>Runtime/Core/Fsm.cs:276</c>：保留 OnChange 订阅、不销毁实例）。</summary>
        public void Reset()
        {
            _fsm.Reset();
        }
    }

    /// <summary>顶层战术状态名（= 规格那条决策链的骨架；<see cref="CsBotFsm"/> 的状态表键）。</summary>
    public static class CsBotFsmStates
    {
        /// <summary>待机（冻结期 / 回合结束 / 死亡）：不动、不开火。</summary>
        public const string Idle = "Idle";

        /// <summary>巡逻 / 推进：沿路线朝目标点走。</summary>
        public const string Patrol = "Patrol";

        /// <summary>交战：停/蹲 → 瞄准 → 射击 → 走位。</summary>
        public const string Engage = "Engage";

        /// <summary>目标层：按阵营做目标（T 下包 / CT 守卫或拆包）。</summary>
        public const string Objective = "Objective";
    }

    /// <summary>目标层（Objective 子层）状态名；由 <see cref="CsBotObjectiveStates.For"/> 按阵营取。</summary>
    public static class CsBotObjectiveStates
    {
        /// <summary>去目标点（两个阵营共用）。</summary>
        public const string Approach = "Approach";

        /// <summary>T：在包点内停住 + 按住 E 下包。</summary>
        public const string Plant = "Plant";

        /// <summary>CT：在包点守位表里驻守 + 定时换位。</summary>
        public const string Hold = "Hold";

        /// <summary>CT：冲到 C4 旁按住 E 拆包。</summary>
        public const string Defuse = "Defuse";

        /// <summary>三档难度共用同一套状态，只换 <c>CsBotProfile</c> 的参数（规格 <c>…参考规格.md:117-131</c>）。</summary>
        public static string For(CsBotState leaf)
        {
            switch (leaf)
            {
                case CsBotState.Plant: return Plant;
                case CsBotState.Defuse: return Defuse;
                case CsBotState.Camp: return Hold;
                default: return Approach;
            }
        }
    }
}
