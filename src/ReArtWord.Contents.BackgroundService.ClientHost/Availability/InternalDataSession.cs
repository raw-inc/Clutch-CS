using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.Availability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Availability;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Availability;

/// <summary>一つのクラスタ・Peer実体の有限内部キャッシュを所有します。</summary>
internal sealed class InternalDataSession : IDisposable
{
    /// <summary>既存の可用性方針とキャッシュ実装をそのまま利用します。</summary>
    internal InternalDataSession(string peerId, AvailabilityPolicy policy, IDurableRepository journal, string directory)
    {
        Policy = policy;
        Availability = new(peerId, new(new Dictionary<EStoragePurpose, AvailabilityPolicy> { [EStoragePurpose.Runtime] = policy }), journal, directory);
    }

    internal AvailabilityPolicy Policy
    {
        get;
    }

    internal AvailabilityService Availability
    {
        get;
    }

    internal SemaphoreSlim Gate
    {
        get;
    } = new(1, 1);

    internal ConcurrentDictionary<string, InternalDataObservation> Observations
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>保持本文が有限キャッシュから外れたメタデータを残し続けません。</summary>
    internal void Prune()
    {
        HashSet<string> retained = Availability.GetStates().Select(static state => state.DatasetId).ToHashSet(StringComparer.Ordinal);
        foreach (string key in Observations.Keys.Where(key => !retained.Contains(key)).ToArray())
        {
            Observations.TryRemove(key, out _);
        }
    }

    /// <summary>Host終了時に所有資源を解放します。</summary>
    public void Dispose()
    {
        Availability.Dispose();
        Gate.Dispose();
    }
}
