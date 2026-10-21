namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>一回の退避経路が開始する待受を、別の回やPeer再起動から区別します。</summary>
public sealed record SourceScope(Guid Id, string WorkflowId, string PeerId, Guid PeerInstanceId);
