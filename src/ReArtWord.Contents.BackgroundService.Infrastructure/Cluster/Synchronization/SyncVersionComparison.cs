namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>版一致と、書込み中のため次の通知で再照合する状態を区別します。</summary>
/// <param name="MatchingVersion">今回の照合で一致を確認できた共有版です。</param>
/// <param name="RetryLater">有限の猶予内で版が変化中の場合だけtrueです。</param>
internal sealed record SyncVersionComparison(string? MatchingVersion = null, bool RetryLater = false)
{
    /// <summary>一致または一時的な再照合待ち以外は、実データで確認します。</summary>
    internal bool RequiresSynchronization => MatchingVersion is null && !RetryLater;
}
