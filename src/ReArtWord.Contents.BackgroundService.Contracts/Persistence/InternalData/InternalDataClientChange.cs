namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>端末内に保持する内部状態の未確定変更です。</summary>
public sealed record InternalDataClientChange(InternalDataClientTarget Target, string? ExpectedRevision, ReArtWord.Contents.BackgroundService.Contracts.Nodes.NodeValue Value, Guid OperationId);
