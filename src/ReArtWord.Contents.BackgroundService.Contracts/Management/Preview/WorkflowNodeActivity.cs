namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>値本体を含まない、一つのWFの処理中ノードを現在のPeer実体に結び付けます。</summary>
public sealed record WorkflowNodeActivity(string WorkflowId, string PeerId, Guid InstanceId, IReadOnlyList<NodeValueObservation> Nodes);
