namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Streams;

/// <summary>起動中の同じCore・同じNICだけで有効な要求配送の受付です。</summary>
internal sealed record PeerStreamTicket(int Port, Guid Token, Guid InstanceId, int MaximumBytes)
{
    /// <summary>本文の受信枠を確保する前に、要求・保存・結果照会を区別できる受付です。</summary>
    public bool PrioritizedFrames
    {
        get; init;
    }

    internal static string Capability => "cluster.request-stream.v2";

    /// <summary>参加通知と大容量同期はそれぞれ既存の専用経路に維持します。</summary>
    /// <returns>同じ実行・保存受付へ渡せる要求かです。</returns>
    internal static bool Handles(string kind) => kind is "request" or "inspect" or "receipt.query"
        or "prepare" or "prepare.brotli" or "prepare.query" or "commit" or "abort" or "query" or "replication.recovery";
}
