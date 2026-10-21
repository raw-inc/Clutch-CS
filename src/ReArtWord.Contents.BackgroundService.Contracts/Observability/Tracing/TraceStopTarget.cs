namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>停止操作を受け付ける元のPeer実体です。</summary>
/// <param name="PeerId">論理Peer。</param>
/// <param name="InstanceId">操作開始時の実体。</param>
public sealed record TraceStopTarget(string PeerId, Guid InstanceId);
