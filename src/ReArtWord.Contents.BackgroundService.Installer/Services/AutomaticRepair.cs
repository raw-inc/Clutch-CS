using ReArtWord.Contents.BackgroundService.Contracts.Cluster.Conflicts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>外部状態を確認できる範囲で競合解消と再参加を試行します。</summary>
internal static class AutomaticRepair
{
    /// <summary>異なるデータの破棄を推測せず、同一候補を統合して通常参加を再確認します。</summary>
    /// <returns>Readyの実確認または残った障害です。</returns>
    public static async Task<SetupResult> RunAsync(ObservedTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Guid operation = Guid.NewGuid();
        if (target.Peer is null)
        {
            return new(operation, EOutcome.NotExecuted, "offline", "Select Reinstall to restore and start this offline installation.", target.Label);
        }
        UpdateObserver observer = new(target.Binding, target.ClusterId);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        PeerInfo peer = await observer.FindAsync(target.PeerId, target.Peer.InstanceId, cancellationToken).ConfigureAwait(false);
        string? cursor = null;
        List<ConflictListSummary> conflicts = [];
        do
        {
            ManagementReply listed = await observer.SendAsync(peer.PeerId, peer.InstanceId, "conflicts.list", UpdateObserver.Payload(new ConflictListQuery(Cursor: cursor)), true, cancellationToken).ConfigureAwait(false);
            ConflictListPage page = Read<ConflictListPage>(listed);
            conflicts.AddRange(page.Entries);
            cursor = page.Cursor;
        }
        while (cursor is not null);
        int unresolved = 0;
        foreach (ConflictListSummary summary in conflicts)
        {
            ManagementReply detailed = await observer.SendAsync(peer.PeerId, peer.InstanceId, "conflicts.detail", UpdateObserver.Payload(new ConflictDetailRequest(summary.Id)), true, cancellationToken).ConfigureAwait(false);
            StateConflict? conflict = Read<ConflictDetail>(detailed).Conflict;

            // JSON本文だけではBinary・型情報・削除を区別できないため、候補値全体を比較します。
            if (conflict is null || conflict.ValuesHidden || !conflict.Resolvable || conflict.Candidates.Count == 0 || conflict.Candidates.Select(candidate => ContractJson.Hash(candidate.Value)).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                unresolved++;
                continue;
            }
            ConflictResolveRequest resolve = new(Guid.NewGuid(), conflict.Id, conflict.Candidates.Select(candidate => candidate.Version).ToArray(), conflict.Candidates[0].Version);
            ManagementReply resolved = await observer.SendAsync(peer.PeerId, peer.InstanceId, "conflicts.resolve", UpdateObserver.Payload(resolve), false, cancellationToken).ConfigureAwait(false);
            if (resolved.Outcome != EOutcome.Succeeded)
            {
                unresolved++;
            }
        }
        if (peer.Status == EPeerStatus.Detached)
        {
            _ = await observer.SendAsync(peer.PeerId, peer.InstanceId, "cluster.rejoin", null, false, cancellationToken).ConfigureAwait(false);
        }
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(InstallationDefaults.StopTimeout);
        string detail = "";
        do
        {
            ManagementReply health = await observer.SendAsync(peer.PeerId, peer.InstanceId, "health", null, true, cancellationToken).ConfigureAwait(false);
            if (health.Value is not null)
            {
                JsonElement body = health.Value.Json;
                string ready = "ready";
                if (body.TryGetProperty(ready, out JsonElement flag) && flag.ValueKind == JsonValueKind.True)
                {
                    return new(operation, EOutcome.Succeeded, "ready", "The peer is Ready.", target.Label);
                }
                detail = body.GetRawText();
            }
            await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);
        return new(operation, EOutcome.Failed, "needs-attention", $"Ready was not confirmed. Unresolved conflicts: {unresolved}. Open the client to compare conflicting values, or choose Reinstall. {detail}", target.Label);
    }

    /// <summary>管理操作の失敗を空の成功結果へ変えません。</summary>
    /// <returns>検証できた応答です。</returns>
    private static T Read<T>(ManagementReply reply)
    {
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new IOException(reply.Reason ?? "The management request could not be confirmed.");
        }
        return reply.Value.Json.Deserialize<T>(ContractJson.Options) ?? throw new InvalidDataException("The peer returned an empty response.");
    }
}
