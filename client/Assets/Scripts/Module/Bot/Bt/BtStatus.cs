namespace Cs16.Module.Bot.Bt
{
    /// <summary>一次 <see cref="BtNode.Tick"/> 的结果。三分量语义与主流行为树一致：
    /// <c>Success</c> = 做完了、<c>Failure</c> = 做不了、<c>Running</c> = 还没做完（下帧继续）。</summary>
    public enum BtStatus
    {
        Failure,
        Success,
        Running
    }
}
