namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>一度固定した共有保存状態と、転送可能な元の実体を表します。</summary>
internal sealed record SyncManifest(Guid Id, string SourcePeerId, Guid SourceInstanceId, string TargetPeerId, Guid TargetInstanceId,
    long Length, string Hash, DateTimeOffset ExpiresAt, SyncStreamTicket? Stream = null, string? Phase = null)
{
    /// <summary>現在状態と操作存在証明を先行して確定する転送です。</summary>
    internal static string CurrentPhase => "current-v1";

    /// <summary>現在値へ再適用しない過去本文だけの転送です。</summary>
    internal static string HistoryPhase => "history-v1";
}
