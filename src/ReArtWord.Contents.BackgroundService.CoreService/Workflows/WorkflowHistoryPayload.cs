namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>展開前の上限と、展開後の内容照合を持つ圧縮詳細です。</summary>
/// <param name="Sequence">対応するヘッダーの保存世代です。</param>
/// <param name="ExecutionId">取り違えを拒否する実行IDです。</param>
/// <param name="CompletedAt">元の実行が完了した時刻です。</param>
/// <param name="Format">保存形式の識別子です。</param>
/// <param name="Length">展開するJSONの正確な長さです。</param>
/// <param name="Hash">展開後本文のSHA-256です。</param>
/// <param name="Data">圧縮済みの本文です。</param>
internal sealed record WorkflowHistoryPayload(long Sequence, Guid ExecutionId, DateTimeOffset CompletedAt, string Format, int Length, string Hash, byte[] Data);
