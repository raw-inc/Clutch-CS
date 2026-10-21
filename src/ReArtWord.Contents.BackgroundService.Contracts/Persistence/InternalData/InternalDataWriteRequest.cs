using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

/// <summary>内部値の明示した一回の更新です。</summary>
public sealed record InternalDataWriteRequest(Guid OperationId, InternalDataTarget Target, string? ExpectedRevision, NodeValue Value);
