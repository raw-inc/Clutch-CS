using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

/// <summary>単調時計の受付期限を持つ要求の結果を、旧来の日時境界から分離して保存します。</summary>
internal static class BoundedRequestReceipts
{
    internal static string Prefix => "cluster:bounded-request-receipts:";

    /// <summary>旧ハッシュ由来のGUIDを時刻付き要求と誤認せず、新形式で明示したノード受付だけを識別します。</summary>
    /// <returns>外側の要求と、ノード本文に固定した受付IDが一致するかです。</returns>
    internal static bool HasBoundedNodeReceipt(ClusterRequest request)
    {
        string kind = "node.execute", property = nameof(RemoteExecutionRequest.ReceiptId);
        return string.Equals(request.Kind, kind, StringComparison.Ordinal) && request.Payload.Kind == Contracts.Nodes.EValueKind.Json
            && request.Payload.Json.ValueKind == System.Text.Json.JsonValueKind.Object && request.Payload.Json.TryGetProperty(property, out System.Text.Json.JsonElement value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String && value.TryGetGuid(out Guid id) && id != Guid.Empty && id == request.Id;
    }

    /// <summary>旧版の受付記録とは別の用途に保存し、旧日時境界を新しい結果へ適用しません。</summary>
    /// <returns>有限期限の契約を持ち、受信時に期限換算済みのUUIDv7要求だけに使う保存先です。</returns>
    internal static string Select(string legacyScope, ClusterRequest request, bool bounded)
        => bounded && request.Admission is not null && OperationIdentity.TryGetIssuedAt(request.Id, out _) ? Prefix + legacyScope
                                                                                                           : legacyScope;

    /// <summary>結果を削除しても、固定実体と元の受付期限によって新規開始を拒否できる形式か確認します。</summary>
    /// <returns>永続的な個別の退役記録を必要としない、期限付き要求の結果かです。</returns>
    internal static bool IsBounded(string? scope, string key)
        => scope?.StartsWith(Prefix, StringComparison.Ordinal) == true && Guid.TryParse(key, out Guid id) && OperationIdentity.TryGetIssuedAt(id, out _);

    /// <summary>保存済みの旧記録を優先し、同じIDの結果を別用途へ作り直しません。</summary>
    /// <returns>旧保存先または期限付き要求の保存先にある元の結果です。</returns>
    internal static async ValueTask<DurableEntry?> ReadAsync(IDurableRepository repository, string legacyScope, Guid id, CancellationToken cancellationToken = default)
    {
        string key = id.ToString("N");
        DurableEntry? legacy = await repository.ReadAsync(legacyScope, key, cancellationToken).ConfigureAwait(false);
        if (legacy is not null || !OperationIdentity.TryGetIssuedAt(id, out _))
        {
            return legacy;
        }
        return await repository.ReadAsync(Prefix + legacyScope, key, cancellationToken).ConfigureAwait(false);
    }
}
