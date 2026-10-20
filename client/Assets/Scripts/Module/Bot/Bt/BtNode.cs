using System;

namespace Cs16.Module.Bot.Bt
{
    /// <summary>
    /// 行为树节点：<c>Tick</c> 返回本帧结果，<c>Reset</c> 清掉"上次跑到哪里"的记忆。
    /// <see cref="Name"/> 只用于诊断日志与自检输出。
    /// </summary>
    public abstract class BtNode
    {
        public string Name { get; protected set; }

        public abstract BtStatus Tick(CsBotBrain brain, float dt, float now);

        /// <summary>清记忆。复合节点在下一次"从 Failure / Success 重新进入"前调用它。</summary>
        public virtual void Reset() { }

        public override string ToString() => Name ?? GetType().Name;
    }

    /// <summary>叶子：把一段现成的决策包成委托（返回 <see cref="BtStatus"/>）。</summary>
    public sealed class BtAction : BtNode
    {
        private readonly Func<CsBotBrain, float, float, BtStatus> _fn;

        public BtAction(string name, Func<CsBotBrain, float, float, BtStatus> fn)
        {
            Name = name;
            _fn = fn;
        }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now) => _fn(brain, dt, now);
    }
}
