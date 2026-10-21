namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>元の実行識別子と開始期限を保持した明示取消要求です。</summary>
/// <param name="ExecutionId">取消する元のノード実行識別子です。</param>
/// <param name="StartBy">元要求の開始期限です。未開始の取消意図をこの時刻まで保持します。</param>
public sealed record NodeCancellationRequest(Guid ExecutionId, DateTimeOffset StartBy);
