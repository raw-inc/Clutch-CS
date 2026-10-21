using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Execution.Concurrency;

/// <summary>担当が交代しても占有数を失わない、入力本文を持たない現役の所有記録です。</summary>
/// <param name="Request">元の実行と取得の識別。</param>
/// <param name="AdmittedAt">枠を確定した時刻。</param>
/// <param name="Forced">期限後に上限を超えて受け付けたか。</param>
internal sealed record ConcurrencyPermit(WorkflowConcurrencyRequest Request, DateTimeOffset AdmittedAt, bool Forced);
