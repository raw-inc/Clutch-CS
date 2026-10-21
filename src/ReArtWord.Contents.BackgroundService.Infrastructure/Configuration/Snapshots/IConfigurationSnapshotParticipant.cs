using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>共有編集保護の所有操作を維持して設定を復元する参加者です。</summary>
public interface IConfigurationSnapshotParticipant : IRuntimeSnapshotParticipant
{
    /// <summary>外側復元操作が所有する保護の下で変更します。</summary>
    /// <returns>共有と実復元の結果です。</returns>
    public Task<ConfigurationPhaseResult> RestoreAsync(Guid restoreOperationId, NodeValue snapshot, CancellationToken cancellationToken = default);
}
