using System.Text.Json;
using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Replication;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>業務本文を含めない復旧照会を検査し、省略値を保存成功と解釈しません。</summary>
internal sealed record ReplicationRecoveryProbe(
    [property: JsonRequired] string Protocol,
    [property: JsonRequired] DurableReceipt? Operation,
    [property: JsonRequired] IReadOnlyList<PeerInfo> Members,
    [property: JsonRequired] DurableReceipt? Stored,
    [property: JsonRequired] bool Busy)
{
    /// <summary>内部判断を扱える照会契約です。</summary>
    internal static string ProtocolName => "prepared-recovery-v1";

    /// <summary>不完全な応答と明示された結果不明を区別します。</summary>
    /// <returns>必要な識別と結果が明記された照会です。</returns>
    internal static ReplicationRecoveryProbe Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(nameof(Operation), out JsonElement operation) || !ValidReceipt(operation)
            || !body.TryGetProperty(nameof(Stored), out JsonElement stored) || !ValidReceipt(stored))
        {
            throw new InvalidDataException("保存準備の復旧応答が不完全です。");
        }
        ReplicationRecoveryProbe result = body.Deserialize<ReplicationRecoveryProbe>() ?? throw new InvalidDataException("保存準備の復旧応答がありません。");
        if (!string.Equals(result.Protocol, ProtocolName, StringComparison.Ordinal) || !ValidMembers(result.Members))
        {
            throw new InvalidDataException("保存準備の復旧契約または参加者が不正です。");
        }
        return result;
    }

    /// <summary>元参加者はPeerとHostの対で一意に識別し、再起動後も同じHostだけを対象にします。</summary>
    /// <returns>省略・重複のない参加者集合かです。</returns>
    internal static bool ValidMembers(IReadOnlyList<PeerInfo>? members)
        => members is not null && members.All(peer => peer is not null && !string.IsNullOrWhiteSpace(peer.PeerId)
            && !string.IsNullOrWhiteSpace(peer.HostId) && peer.InstanceId != Guid.Empty)
            && members.Select(peer => peer.PeerId).Distinct(StringComparer.Ordinal).Count() == members.Count;

    /// <summary>省略された数値enumの既定値を成功として読まず、本文なしの照会結果だけを受けます。</summary>
    /// <returns>明示された元操作・結果・空の本文集合かです。</returns>
    private static bool ValidReceipt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        return value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(nameof(DurableReceipt.OperationId), out JsonElement id) && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out Guid operation) && operation != Guid.Empty
            && value.TryGetProperty(nameof(DurableReceipt.Fingerprint), out JsonElement fingerprint) && fingerprint.ValueKind == JsonValueKind.String
            && value.TryGetProperty(nameof(DurableReceipt.Outcome), out JsonElement outcome) && outcome.ValueKind == JsonValueKind.Number && outcome.TryGetInt32(out int code) && Enum.IsDefined((EOutcome)code)
            && value.TryGetProperty(nameof(DurableReceipt.Entries), out JsonElement entries) && entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == 0;
    }

    /// <summary>不在や期限経過から推測せず、同一操作と元参加Hostに対する判断だけを採用します。</summary>
    /// <returns>確定、取消、または確認不能です。</returns>
    internal bool? Decision(Guid id, string fingerprint, PeerInfo? local)
    {
        if (!string.Equals(Protocol, ProtocolName, StringComparison.Ordinal) || Busy || !ValidMembers(Members))
        {
            return null;
        }
        if (Stored is { Outcome: EOutcome.Succeeded } stored && stored.OperationId == id && string.Equals(stored.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return true;
        }
        DurableReceipt? result = Operation;
        if (result?.OperationId != id || !string.Equals(result.Fingerprint, fingerprint, StringComparison.Ordinal)
            || local is null || !Members.Any(peer => string.Equals(peer.PeerId, local.PeerId, StringComparison.Ordinal) && string.Equals(peer.HostId, local.HostId, StringComparison.Ordinal)))
        {
            return null;
        }
        if (result.Outcome == EOutcome.Succeeded || result.Outcome == EOutcome.Unknown && string.Equals(result.Reason, ReplicationJournal.CommitDecided, StringComparison.Ordinal))
        {
            return true;
        }
        if (result.Outcome == EOutcome.Failed || result.Outcome == EOutcome.Unknown && string.Equals(result.Reason, ReplicationJournal.AbortDecided, StringComparison.Ordinal))
        {
            return false;
        }
        return null;
    }
}
