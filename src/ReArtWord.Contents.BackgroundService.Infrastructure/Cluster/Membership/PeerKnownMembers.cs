using System.Data.Common;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Membership;

/// <summary>再起動で失われたPeerを忘れず、過去のReadyは再利用しません。</summary>
internal sealed class PeerKnownMembers(ClusterOptions options, IDurableRepository repository, PeerMembership membership) : IDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private long savedRevision = -1;
    private string Scope => "peer:" + options.PeerId + ":membership";

    /// <summary>通常参加より前にローカルの既知集合を戻します。</summary>
    /// <returns>保存状態の確認完了です。</returns>
    public async Task InitializeAsync(bool previouslyInitialized, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(Scope, "known", cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            if (previouslyInitialized)
            {
                throw new InvalidDataException("再起動前の既知Peer集合がありません。旧集合不明のまま通常参加できません。");
            }
            return;
        }
        KnownState? state = entry.Value?.Json.Deserialize<KnownState>();
        if (state is null || state.Format != 1 || state.Peers is null || state.Peers.Any(peer => peer is null || string.IsNullOrWhiteSpace(peer.PeerId) || string.IsNullOrWhiteSpace(peer.HostId) || peer.InstanceId == Guid.Empty || string.Equals(peer.PeerId, options.PeerId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("保存された既知Peer集合を検証できません。");
        }
        if (state.Peers.Select(peer => peer.PeerId).Distinct(StringComparer.Ordinal).Count() != state.Peers.Count)
        {
            throw new InvalidDataException("既知Peer識別子が重複しています。");
        }
        membership.RestoreKnown(state.Peers);
        savedRevision = membership.KnownRevision;
    }

    /// <summary>変化した集合だけを原子的に置換し、失敗時は通常参加へ進めません。</summary>
    /// <returns>現在集合の保存が確認できたか。</returns>
    public async ValueTask<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref savedRevision) == membership.KnownRevision)
            {
                return true;
            }
            return await SaveAsync(cancellationToken).ConfigureAwait(false) && Volatile.Read(ref savedRevision) == membership.KnownRevision;
        }
        finally
        {
            writes.Release();
        }
    }

    /// <summary>ハートビート時刻を除いた一件を履歴・操作記録なしで保存します。</summary>
    /// <returns>ローカル置換が成立したか。</returns>
    private async Task<bool> SaveAsync(CancellationToken cancellationToken = default)
    {
        (long revision, PeerInfo[] peers) = membership.ExportKnown();
        try
        {
            EOutcome result = await repository.WriteLocalSnapshotAsync(Scope, "known", NodeValue.FromObject(new KnownState(1, peers)), cancellationToken).ConfigureAwait(false);
            if (result != EOutcome.Succeeded)
            {
                return false;
            }
            Volatile.Write(ref savedRevision, revision);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or DbException)
        {
            return false;
        }
    }

    /// <summary>共有スナップショットから除外するローカル保存形式です。</summary>
    private sealed record KnownState(int Format, IReadOnlyList<PeerInfo> Peers);

    /// <summary>ローカル置換の同期資源を解放します。</summary>
    public void Dispose() => writes.Dispose();
}
