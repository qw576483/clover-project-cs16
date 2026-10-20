namespace Cs16.Module.Bot.Bt
{
    /// <summary>
    /// 复合节点基类：持有子节点数组。
    /// <para><b>每 tick 从第一个子节点全量求值</b>（不记"上次停在哪个 Running 子节点"）：
    /// 树的每层都夹着"每帧必须跑"的前置动作（感知、卡住恢复、任务/分支推导），
    /// 一旦从 Running 子节点续跑就会把它们整帧跳过，导致感知与任务冻结数秒。</para>
    /// </summary>
    public abstract class BtComposite : BtNode
    {
        protected readonly BtNode[] Children;

        protected BtComposite(string name, params BtNode[] children)
        {
            Name = name;
            Children = children;
        }

        public override void Reset()
        {
            for (var i = 0; i < Children.Length; i++) Children[i].Reset();
        }
    }

    /// <summary>依次 Tick：第一个 Success 即整条 Success；全 Failure 才 Failure；Running 的子节点下帧先续它。</summary>
    public sealed class BtSelector : BtComposite
    {
        public BtSelector(string name, params BtNode[] children) : base(name, children) { }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            for (var i = 0; i < Children.Length; i++)
            {
                var status = Children[i].Tick(brain, dt, now);
                if (status == BtStatus.Running) return BtStatus.Running;
                if (status == BtStatus.Success) return BtStatus.Success;
            }

            return BtStatus.Failure;
        }
    }

    /// <summary>依次 Tick：任一 Failure 即 Failure；全部 Success 才 Success；Running 的子节点下帧先续它。</summary>
    public sealed class BtSequence : BtComposite
    {
        public BtSequence(string name, params BtNode[] children) : base(name, children) { }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            for (var i = 0; i < Children.Length; i++)
            {
                var status = Children[i].Tick(brain, dt, now);
                if (status == BtStatus.Failure) return BtStatus.Failure;
                if (status == BtStatus.Running) return BtStatus.Running;
            }

            return BtStatus.Success;
        }
    }

    /// <summary>每帧 Tick 全部子节点：任一 Failure 即 Failure；全部结束（无 Running）即 Success。</summary>
    public sealed class BtParallel : BtComposite
    {
        private readonly bool[] _finished;

        public BtParallel(string name, params BtNode[] children) : base(name, children)
        {
            _finished = new bool[children.Length];
        }

        public override void Reset()
        {
            base.Reset();
            for (var i = 0; i < _finished.Length; i++) _finished[i] = false;
        }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            var allDone = true;
            for (var i = 0; i < Children.Length; i++)
            {
                if (_finished[i]) continue;

                var status = Children[i].Tick(brain, dt, now);
                if (status == BtStatus.Failure)
                {
                    Reset();
                    return BtStatus.Failure;
                }

                if (status == BtStatus.Success) _finished[i] = true;
                else allDone = false;
            }

            if (!allDone) return BtStatus.Running;
            Reset();
            return BtStatus.Success;
        }
    }
}
