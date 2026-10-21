namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Streams;

/// <summary>検証済み更新ジョブへの一回のデータ転送を要求します。</summary>
/// <param name="OperationId">prepare済みの固定更新識別子です。</param>
/// <param name="SourcePeerId">選択NIC上で観測した送信元Peerです。</param>
public sealed record UpdateStreamRequest(Guid OperationId, string SourcePeerId);
