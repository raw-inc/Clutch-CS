using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;

/// <summary>回復するファイル操作の元要求と派生IDの生成方式を保持します。</summary>
internal static class FileTransactionRecord
{
    /// <summary>結果や参加集合の形を保ち、旧版が新方式の要求を再開することを防ぎます。</summary>
    /// <returns>段階記録へ保存する値です。</returns>
    internal static NodeValue Write(FileTransactionOperation operation)
    {
        JsonElement request = DerivedIdentityPayload.Write(JsonSerializer.SerializeToElement(operation.Request), operation.IdentityFormat);
        return NodeValue.FromObject(new Stored(operation.Fingerprint, request, operation.Members, operation.OwnerPeerId,
            operation.OwnerInstanceId, operation.Lease, operation.Result, operation.ReservationConfirmed));
    }

    /// <summary>方式のない既存記録も読み、元の要求ID・対象・本文を引き継ぎます。</summary>
    /// <returns>記録に固定された生成方式を持つ操作です。</returns>
    internal static FileTransactionOperation Read(JsonElement value)
    {
        Stored record = value.Deserialize<Stored>() ?? throw new InvalidDataException("ファイル操作の記録を読み取れません。");
        (EDerivedIdFormat format, JsonElement payload) = DerivedIdentityPayload.Read(record.Request);
        FileTransactionRequest request = payload.Deserialize<FileTransactionRequest>() ?? throw new InvalidDataException("ファイル操作の元要求を読み取れません。");
        return new(record.Fingerprint, request, record.Members, record.OwnerPeerId, record.OwnerInstanceId, record.Lease, record.Result,
            record.ReservationConfirmed) { IdentityFormat = format };
    }

    /// <summary>要求以外の配置を維持し、保持処理も従来どおり終了状態と予約を確認できます。</summary>
    private sealed record Stored(string Fingerprint, JsonElement Request, IReadOnlyList<PeerInfo> Members, string OwnerPeerId,
        Guid OwnerInstanceId, FileLease? Lease, FileTransactionResult Result, bool ReservationConfirmed);
}
