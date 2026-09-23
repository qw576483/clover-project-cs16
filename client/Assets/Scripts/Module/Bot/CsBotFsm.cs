using System;
using System.Collections.Generic;
using CloverEngine;
using Cs16.Module.Match;   // CsBotState（叶子状态枚举）定义在 Module/Match/CsTypes.cs

namespace Cs16.Module.Bot
{
    /// <summary>
    /// 机器人战术层状态机（**实现引擎的状态机接口** <see cref="IFsm"/>）。
    ///
    /// <para><b>为什么不是直接用 <c>Game.Fsm</c></b>（片BU 落纸复核，⛔ 不是猜测）：</para>
    /// <list type="number">
    /// <item>引擎里**只有一份**全局 FSM：<c>Runtime/Core/Game.cs:169</c> 的 <c>public static IFsm Fsm</c>，
    /// 在 <c>Game.cs:383</c> 由 <c>Fsm = new Fsm()</c> 建立、<c>Game.cs:849</c> 的 <c>InitFsm()</c> 注册的是
    /// **游戏流程**状态（Launching / CheckingUpdate / Logging / MainCity / Battle…）。它是**应用级单例**。</item>
    /// <item>实现类 <c>Runtime/Core/Fsm.cs:70</c> 是 <c>internal class Fsm</c> ⇒ 跨程序集（<c>Cs16.asmdef</c>）**不可实例化**，
    /// 引擎也没有"多实例工厂"（全仓 <c>IFsm|CreateFsm|NewFsm</c> 命中仅 <c>Game.cs</c> 的这一个属性与 <c>Fsm.cs</c> 的定义）。</item>
    /// <item>若把 8 个 bot 的状态注册到那一份 <c>Game.Fsm</c> 上，8 个脑会**共用同一个 <c>Current</c>**、
    /// 互相覆盖（`RegisterState` 同名还会告警覆盖回调）⇒ 结构上不成立。</item>
    /// </list>
    ///
    /// <para><b>因此本类的做法</b>：**实现引擎自己的 <see cref="IFsm"/> 接口**，并**逐条复刻引擎
    /// <c>Runtime/Core/Fsm.cs</c> 的实现语义**（自环忽略 / 回调内再转换排队补执行 / 连锁转换上限 8 /
    /// 未注册状态报 Error / 未注册触发器告警 / OnTick 异常隔离）——
    /// 业务侧由此拿到"每个 bot 一份、语义与引擎一致"的状态机，而不是另造一套框架。
    /// ⛔ 这不是行为树：本工程没有、也不需要行为树（全仓 <c>BehaviorTree|BehaviourTree|btree</c> = 0 命中）。</para>
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
        /// <summary>一次外部转换调用允许的连锁转换上限（**逐值复刻** <c>Runtime/Core/Fsm.cs:77</c> 的
        /// <c>MaxChainedSwitches = 8</c>）：回调链自激到此上限即报错停止，避免栈溢出。</summary>
        private const int MaxChainedSwitches = 8;

        private readonly Dictionary<string, StateInfo> _states = new Dictionary<string, StateInfo>(8);
        private readonly Dictionary<string, string> _transitions = new Dictionary<string, string>(8);
        private readonly List<Action<string, string>> _changeHandlers = new List<Action<string, string>>(1);
        private readonly List<Action<string, string>> _changeBuffer = new List<Action<string, string>>(1);

        private string _current;
        private bool _switching;
        private string _pendingState;
        private bool _hasPending;

        /// <summary>本状态机的名字（日志用；多实例时能分辨是哪一个 bot 的哪一层）。</summary>
        private readonly string _label;

        /// <summary>累计转换次数（自检/日志用）。</summary>
        public long TransitionCount { get; private set; }

        public CsBotFsm(string label)
        {
            _label = string.IsNullOrEmpty(label) ? "bot-fsm" : label;
        }

        public string Current => _current;

        public void RegisterState(string state, Action onEnter = null, Action<float> onTick = null, Action onExit = null)
        {
            if (_states.ContainsKey(state))
            {
                Game.Logger?.Warn("Bot.Fsm",
                    $"[{_label}] 状态 '{state}' 重复注册：旧 OnEnter/OnTick/OnExit 被整体替换（与引擎 Fsm.cs:109 同语义）");
            }

            _states[state] = new StateInfo { OnEnter = onEnter, OnTick = onTick, OnExit = onExit };
            if (_current == null) _current = state;   // 首个注册的状态即初始状态（引擎的 Game.InitFsm 也是先注册 Launching）
        }

        public void AddTransition(string trigger, string toState)
        {
            _transitions[trigger] = toState;
        }

        public void Transition(string toState)
        {
            if (toState == _current) return;   // 自环忽略（引擎 Fsm.cs:139）
            SwitchTo(toState);
        }

        public void Trigger(string trigger)
        {
            if (!_transitions.TryGetValue(trigger, out var toState))
            {
                // 与引擎 Fsm.cs:150 同语义：拼错触发器**告警并忽略**，不静默 return
                Game.Logger?.Warn("Bot.Fsm",
                    $"[{_label}] 未注册的触发器 '{trigger}' 被忽略；已注册：{string.Join(", ", _transitions.Keys)}");
                return;
            }

            SwitchTo(toState);
        }

        public void Force(string state)
        {
            SwitchTo(state);
        }

        public void Tick(float dt)
        {
            if (_current == null || !_states.TryGetValue(_current, out var info)) return;

            try
            {
                info.OnTick?.Invoke(dt);
            }
            catch (Exception ex)
            {
                // 与引擎 Fsm.cs:183 同语义：单个状态回调抛异常不许中断整个 Tick
                Game.Logger?.Error("Bot.Fsm", $"[{_label}] OnTick 异常 [{_current}]：{ex.Message}", ex);
            }
        }

        public void OnChange(Action<string, string> handler)
        {
            if (handler == null) return;
            if (_changeHandlers.Contains(handler))
            {
                Game.Logger?.Warn("Bot.Fsm", $"[{_label}] 同一个 OnChange 回调重复注册被忽略");
                return;
            }

            _changeHandlers.Add(handler);
        }

        public void OffChange(Action<string, string> handler)
        {
            for (var i = _changeHandlers.Count - 1; i >= 0; i--)
            {
                if (_changeHandlers[i] == handler) _changeHandlers.RemoveAt(i);
            }
        }

        /// <summary>恢复到未初始化（回合开始/重开比赛时调用）：清空状态表，等调用方重新注册。</summary>
        public void Reset()
        {
            _states.Clear();
            _transitions.Clear();
            _current = null;
            _switching = false;
            _hasPending = false;
            _pendingState = null;
        }

        /// <summary>
        /// 状态转换唯一入口（Transition / Trigger / Force 与链式补执行共用）。
        /// **逐条复刻**引擎 <c>Runtime/Core/Fsm.cs:229-277</c> 的 <c>SwitchTo</c>：未注册状态报 Error 并忽略、
        /// 自环忽略、回调内再转换改为排队（最后意图为准）、连锁上限 8。
        /// </summary>
        private void SwitchTo(string toState)
        {
            if (toState != null && !_states.ContainsKey(toState))
            {
                Game.Logger?.Error("Bot.Fsm", $"[{_label}] 状态未注册：{toState}（转换被忽略）");
                return;
            }

            if (toState == _current) return;

            if (_switching)
            {
                _pendingState = toState;
                _hasPending = true;
                return;
            }

            _switching = true;
            try
            {
                var target = toState;
                var chained = 0;
                while (true)
                {
                    ApplySwitch(target);
                    TransitionCount++;

                    if (!_hasPending) break;

                    _hasPending = false;
                    chained++;
                    if (chained > MaxChainedSwitches)
                    {
                        Game.Logger?.Error("Bot.Fsm",
                            $"[{_label}] 状态回调链超过 {MaxChainedSwitches} 次连锁转换，已中止" +
                            $"（疑似回调自激）；待执行目标 '{_pendingState}' 被丢弃");
                        break;
                    }

                    target = _pendingState;
                }
            }
            finally
            {
                _switching = false;
                _hasPending = false;
                _pendingState = null;
            }
        }

        private void ApplySwitch(string toState)
        {
            var from = _current;
            if (toState == from) return;

            _current = toState;
            InvokeStateCallback(from, true);
            InvokeStateCallback(toState, false);

            _changeBuffer.Clear();
            _changeBuffer.AddRange(_changeHandlers);
            for (var i = 0; i < _changeBuffer.Count; i++)
            {
                try
                {
                    _changeBuffer[i]?.Invoke(from, toState);
                }
                catch (Exception ex)
                {
                    Game.Logger?.Error("Bot.Fsm", $"[{_label}] OnChange 回调异常：{ex.Message}", ex);
                }
            }
        }

        private void InvokeStateCallback(string state, bool onExit)
        {
            if (state == null || !_states.TryGetValue(state, out var info)) return;

            try
            {
                if (onExit) info.OnExit?.Invoke();
                else info.OnEnter?.Invoke();
            }
            catch (Exception ex)
            {
                Game.Logger?.Error("Bot.Fsm",
                    $"[{_label}] {(onExit ? "OnExit" : "OnEnter")} 异常 [{state}]：{ex.Message}", ex);
            }
        }

        private class StateInfo
        {
            public Action OnEnter;
            public Action<float> OnTick;
            public Action OnExit;
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
