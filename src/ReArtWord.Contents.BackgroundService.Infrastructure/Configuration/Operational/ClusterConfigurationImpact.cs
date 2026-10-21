using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;

/// <summary>共通運用設定の実差分と確認対象版を構成します。</summary>
internal static class ClusterConfigurationImpact
{
    /// <summary>候補・有効版・共有反映希望・Peerの組合せを確認識別子にします。</summary>
    /// <returns>反映直前に再計算する識別子です。</returns>
    internal static string Token(ClusterOperationalSnapshot candidate, ClusterOperationalSnapshot? effective, string? marker, string peer, bool unsaved = false)
        => ContractJson.Hash(new { candidate.Revision, effective, marker, peer, unsaved });

    /// <summary>未保存候補と確認済み保存版を区別して比較します。</summary>
    /// <returns>確認範囲と実差分です。</returns>
    internal static async Task<ConfigurationImpactPreview> InspectAsync(ClusterConfigurationStore store, IDurableRepository repository, string peer, CancellationToken cancellationToken = default)
    {
        ClusterConfigurationStored? unsaved = store.Availability?.TryGetCandidate(EStoragePurpose.Configuration, ClusterConfigurationStore.Id)?.Value?.Json.Deserialize<ClusterConfigurationStored>(ContractJson.Options);
        ClusterOperationalState state = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        ClusterOperationalSnapshot candidate = unsaved?.Snapshot ?? state.Saved ?? throw new KeyNotFoundException();
        DurableEntry? marker = await repository.ReadAsync(ClusterConfigurationStore.ActivationScope, ClusterConfigurationStore.Id, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<ConfigurationDifference> differences, bool truncated) = ConfigurationDifferences.Compare(state.Effective?.Settings, candidate.Settings);
        return new(ClusterConfigurationStore.Id, candidate.Revision, state.Effective?.Revision,
            Token(candidate, state.Effective, marker?.Version, peer, unsaved is not null), differences,
            ["Applies to new inputs; already started inputs retain their configuration."],
            ["External applications, devices and physical side effects cannot be predicted from configuration alone."], truncated, unsaved is not null);
    }
}
