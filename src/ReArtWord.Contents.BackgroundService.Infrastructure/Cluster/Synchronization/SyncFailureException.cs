using System.Text.Json;
namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;

/// <summary>秘密値やローカルパスを含めず同期の拒否理由を伝えます。</summary>
internal sealed class SyncFailureException : IOException
{
    private static HashSet<string> Codes
    {
        get;
    } = new(["Sync.TransferUnavailable", "Sync.ExportCapacity", "Sync.ImportCapacity", "Sync.SnapshotCapacity", "Sync.Range", "Sync.SessionUnavailable",
        "Sync.ChunkUnconfirmed", "Sync.ChunkMismatch", "Sync.HashMismatch", "Sync.InvalidSnapshot", "Sync.ManifestMismatch", "Sync.SessionExpired",
        "Sync.SourceInstanceUnavailable", "Sync.RemoteImportUnconfirmed", "Sync.ResponseUnconfirmed", "Sync.ProtocolMissing", "Sync.ManifestMissing",
        "Sync.Conflict", "Sync.Interrupted", "Sync.RemoteRejected", "Sync.InventoryMismatch", "Sync.HistoryUpgradeRequired", "Sync.SnapshotChanged",
        "Sync.PendingOperations", "Sync.HistoryUnavailable", "Sync.HistoryConflict", "Sync.HistoryRecordCapacity"], StringComparer.Ordinal);

    /// <summary>固定された理由コードだけを保持します。</summary>
    public SyncFailureException(string code) : base(Normalize(code))
    {
        Code = Normalize(code);
    }

    /// <summary>一般化した確認不能理由を使用します。</summary>
    public SyncFailureException() : this("Sync.TransferUnavailable")
    {
    }

    /// <summary>原因を内部保持しても公開表現は理由コードへ限定します。</summary>
    public SyncFailureException(string code, Exception innerException) : base(Normalize(code), innerException)
    {
        Code = Normalize(code);
    }

    /// <summary>相手が返した任意本文をローカル診断へそのまま出力しません。</summary>
    /// <returns>既知の小さいコードまたは一般化した拒否です。</returns>
    internal static string Normalize(string code)
    {
        if (Codes.Contains(code))
        {
            return code;
        }
        return "Sync.RemoteRejected";
    }

    /// <summary>例外本文やstackを同期応答へそのまま出力しません。</summary>
    /// <returns>固定理由です。</returns>
    public override string ToString() => Code;

    /// <summary>外部へ返せる固定理由です。</summary>
    internal string Code
    {
        get;
    }

    /// <summary>同期で一般化する障害だけを分類します。</summary>
    /// <returns>確認不能として扱う障害か。</returns>
    internal static bool IsExpected(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException or InvalidOperationException or System.Net.Sockets.SocketException;

    /// <summary>外部値を含む例外本文を理由として返しません。</summary>
    /// <returns>固定された理由コードです。</returns>
    internal static string CodeFor(Exception exception)
    {
        if (exception is SyncFailureException failure)
        {
            return failure.Code;
        }
        if (exception is OperationCanceledException)
        {
            return "Sync.Interrupted";
        }
        if (exception is Persistence.SnapshotChangedException)
        {
            return "Sync.SnapshotChanged";
        }
        if (exception is InvalidDataException)
        {
            return "Sync.InvalidSnapshot";
        }
        return "Sync.TransferUnavailable";
    }

}
