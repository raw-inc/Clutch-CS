using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.History;

/// <summary>本文を削除した旧形式操作の、再実行を拒否する最小情報です。</summary>
/// <param name="Id">元の操作IDです。</param>
/// <param name="Fingerprint">異なる内容での同じIDの再利用を区別します。</param>
/// <param name="Shared">共有操作として同期するかです。</param>
/// <param name="Outcome">本文を捨てる前の確定結果です。未確定の既定値から成功を推測しません。</param>
/// <param name="Reason">予約解放に必要な取消などの理由です。</param>
public sealed record RetiredOperation(Guid Id, string Fingerprint, bool Shared, EOutcome Outcome = EOutcome.Unknown, string? Reason = null);
