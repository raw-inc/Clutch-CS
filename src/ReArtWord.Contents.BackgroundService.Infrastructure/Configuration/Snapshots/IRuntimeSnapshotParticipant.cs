using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>保存・復元・比較できる実行時状態を明示します。</summary>
public interface IRuntimeSnapshotParticipant
{
    /// <summary>このPeer内で一意の状態識別子です。</summary>
    public string Id
    {
        get;
    }

    /// <summary>実際に取得できた状態を保存用に返します。</summary>
    /// <returns>取得状態。</returns>
    public Task<NodeValue> CaptureAsync(CancellationToken cancellationToken = default);

    /// <summary>現在稼働を維持したまま復元準備可能か確認します。</summary>
    /// <returns>開始前に修正が必要な条件。</returns>
    public Task<IReadOnlyList<ValidationIssue>> ValidateAsync(NodeValue snapshot, CancellationToken cancellationToken = default);

    /// <summary>宣言した状態だけを復元します。</summary>
    /// <returns>実変更結果。</returns>
    public Task<ConfigurationPhaseResult> RestoreAsync(NodeValue snapshot, CancellationToken cancellationToken = default);

    /// <summary>宣言した成功状態と実状態を比較します。</summary>
    /// <returns>比較一致。</returns>
    public Task<bool> MatchesAsync(NodeValue snapshot, CancellationToken cancellationToken = default);
}
