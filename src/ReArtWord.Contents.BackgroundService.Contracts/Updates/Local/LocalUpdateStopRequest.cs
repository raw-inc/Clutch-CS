namespace ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;

/// <summary>固定ジョブと元管理操作記録を対応づける自身の停止要求です。</summary>
/// <param name="OperationId">更新ジョブの識別子です。</param>
/// <param name="Fingerprint">固定ジョブ全体の指紋です。</param>
/// <param name="RequestId">外側管理要求と同じ識別子です。</param>
/// <param name="ExpiresAt">外側管理要求と同じ受付期限です。</param>
public sealed record LocalUpdateStopRequest(Guid OperationId, string Fingerprint, Guid RequestId, DateTimeOffset ExpiresAt);
