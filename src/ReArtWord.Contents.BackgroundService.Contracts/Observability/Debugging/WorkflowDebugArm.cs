namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;

/// <summary>次の一件を予約する実体固定のチケットです。</summary>
public sealed record WorkflowDebugArm(Guid Id, string WorkflowId, string OwnerPeerId, Guid OwnerInstanceId, DateTimeOffset ExpiresAt)
{
    /// <summary>予約した入力を待つ時間です。</summary>
    public static TimeSpan Lifetime => TimeSpan.FromMinutes(10);

    /// <summary>予約期限後に取得済みの記録を参照できる時間です。</summary>
    public static TimeSpan Retention => TimeSpan.FromMinutes(20);
}
