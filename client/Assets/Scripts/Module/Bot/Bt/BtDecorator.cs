using System;

namespace Cs16.Module.Bot.Bt
{
    /// <summary>前置条件：谓词为真 ⇒ Success，否则 Failure（不 Tick 任何子节点）。</summary>
    public sealed class BtCondition : BtNode
    {
        private readonly Func<CsBotBrain, bool> _predicate;

        public BtCondition(string name, Func<CsBotBrain, bool> predicate)
        {
            Name = name;
            _predicate = predicate;
        }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
            => _predicate(brain) ? BtStatus.Success : BtStatus.Failure;
    }

    /// <summary>单子节点装饰器基类。</summary>
    public abstract class BtDecorator : BtNode
    {
        protected readonly BtNode Child;

        protected BtDecorator(string name, BtNode child)
        {
            Name = name;
            Child = child;
        }

        public override void Reset()
        {
            Child.Reset();
        }

        public override string ToString() => $"{Name}({Child})";
    }

    /// <summary>取反：Success ↔ Failure，Running 原样透传。</summary>
    public sealed class BtInverter : BtDecorator
    {
        public BtInverter(string name, BtNode child) : base(name, child) { }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            var status = Child.Tick(brain, dt, now);
            if (status == BtStatus.Success) return BtStatus.Failure;
            if (status == BtStatus.Failure) return BtStatus.Success;
            return BtStatus.Running;
        }
    }

    /// <summary>
    /// 冷却：子节点 Success 之后 <c>seconds</c> 秒内直接 Failure（不 Tick 子节点）。
    /// <para>到期时刻按 <c>now</c> 记住，<see cref="Reset"/> 不清它 —— 只清子节点记忆，
    /// 否则每次重新进入都会把冷却重新起算。</para>
    /// </summary>
    public sealed class BtCooldown : BtDecorator
    {
        private readonly float _seconds;
        private float _readyAt = float.NegativeInfinity;

        public BtCooldown(string name, float seconds, BtNode child) : base(name, child)
        {
            _seconds = seconds;
        }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            if (now < _readyAt) return BtStatus.Failure;

            var status = Child.Tick(brain, dt, now);
            if (status == BtStatus.Success) _readyAt = now + _seconds;
            return status;
        }
    }

    /// <summary>超时：子节点连续 Running 超过 <c>seconds</c> 秒 ⇒ 清子节点记忆并返回 Failure。</summary>
    public sealed class BtTimeout : BtDecorator
    {
        private readonly float _seconds;
        private float _startedAt = -1f;

        public BtTimeout(string name, float seconds, BtNode child) : base(name, child)
        {
            _seconds = seconds;
        }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            var status = Child.Tick(brain, dt, now);
            if (status != BtStatus.Running)
            {
                _startedAt = -1f;
                return status;
            }

            if (_startedAt < 0f) _startedAt = now;
            if (now - _startedAt <= _seconds) return BtStatus.Running;

            Child.Reset();
            _startedAt = -1f;
            return BtStatus.Failure;
        }

        public override void Reset()
        {
            base.Reset();
            _startedAt = -1f;
        }
    }

    /// <summary>反复 Tick 子节点直到它 Failure：子节点 Success 时本节点返回 Running。</summary>
    public sealed class BtUntilFail : BtDecorator
    {
        public BtUntilFail(string name, BtNode child) : base(name, child) { }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            var status = Child.Tick(brain, dt, now);
            return status == BtStatus.Failure ? BtStatus.Failure : BtStatus.Running;
        }
    }
}
