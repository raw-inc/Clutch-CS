using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

/// <summary>同じ操作を再適用しないための保存済み結果です。</summary>
/// <param name="OperationId">操作識別子。</param>
/// <param name="Fingerprint">要求内容の指紋。</param>
/// <param name="Outcome">確定した結果。</param>
/// <param name="Entries">適用した版。</param>
/// <param name="Reason">非成功理由。</param>
public sealed record DurableReceipt(Guid OperationId, string Fingerprint, EOutcome Outcome, IReadOnlyList<DurableEntry> Entries, string? Reason = null);
