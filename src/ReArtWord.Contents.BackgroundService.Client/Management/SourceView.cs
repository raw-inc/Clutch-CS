using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Client.Management;

/// <summary>一つの実待受と有限入力の状態を分離します。</summary>
internal sealed record SourceView(string WorkflowId, string NodeId, NodeSourceStatus Status, Guid? Id = null, Guid? PeerInstanceId = null,
    string? PeerId = null, DateTimeOffset? StartedAt = null)
{
    /// <summary>開始元を確認できる未終了の待受だけを操作対象にします。</summary>
    internal bool CanStop => Id is not null && Id != Guid.Empty && PeerInstanceId is not null && PeerInstanceId != Guid.Empty
        && Status.State is ESourceState.Starting or ESourceState.Listening or ESourceState.Unavailable;
}
