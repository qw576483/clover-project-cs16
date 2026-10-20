namespace Cs16.Module.Bot.Bt
{
    /// <summary>
    /// 复合节点基类：持有子节点数组与"上次停在哪个 Running 子节点"的记忆（<c>-1</c> = 无）。
    /// <para>记忆语义：某个子节点返回 <see cref="BtStatus.Running"/> 时记住它的下标，下一帧从它开始 Tick；
    /// 该子节点返回非 Running 时清下标（整棵子树重新从第一个子节点开始）。</para>
    /// </summary>
    public abstract class BtComposite : BtNode
    {
        protected readonly BtNode[] Children;

        /// <summary>上次返回 Running 的子节点下标；<c>-1</c> = 本节点不在"续跑"状态。</summary>
        protected int RunningIndex = -1;

        protected BtComposite(string name, params BtNode[] children)
        {
            Name = name;
            Children = children;
        }

        public override void Reset()
        {
            RunningIndex = -1;
            for (var i = 0; i < Children.Length; i++) Children[i].Reset();
        }

        /// <summary>从头开始时清一遍子节点记忆（只清这一次，不是每帧）。</summary>
        protected void ResetChildrenOnEntry()
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
            var start = RunningIndex >= 0 ? RunningIndex : 0;
            if (RunningIndex < 0) ResetChildrenOnEntry();

            for (var i = start; i < Children.Length; i++)
            {
                var status = Children[i].Tick(brain, dt, now);
                if (status == BtStatus.Running)
                {
                    RunningIndex = i;
                    return BtStatus.Running;
                }

                if (status == BtStatus.Success)
                {
                    RunningIndex = -1;
                    return BtStatus.Success;
                }
            }

            RunningIndex = -1;
            return BtStatus.Failure;
        }
    }

    /// <summary>依次 Tick：任一 Failure 即 Failure；全部 Success 才 Success；Running 的子节点下帧先续它。</summary>
    public sealed class BtSequence : BtComposite
    {
        public BtSequence(string name, params BtNode[] children) : base(name, children) { }

        public override BtStatus Tick(CsBotBrain brain, float dt, float now)
        {
            var start = RunningIndex >= 0 ? RunningIndex : 0;
            if (RunningIndex < 0) ResetChildrenOnEntry();

            for (var i = start; i < Children.Length; i++)
            {
                var status = Children[i].Tick(brain, dt, now);
                if (status == BtStatus.Failure)
                {
                    RunningIndex = -1;
                    return BtStatus.Failure;
                }

                if (status == BtStatus.Running)
                {
                    RunningIndex = i;
                    return BtStatus.Running;
                }
            }

            RunningIndex = -1;
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
