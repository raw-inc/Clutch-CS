using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;

/// <summary>取得を確認できた枠と、未開始理由を区別する受付結果です。</summary>
/// <param name="Lease">取得済みの場合、区間と子処理の終了時に解放する所有権。</param>
/// <param name="Rejection">取得できなかった理由。</param>
/// <param name="Forced">期限後に上限を超えて進行したか。</param>
public sealed record WorkflowConcurrencyAdmission(IAsyncDisposable? Lease, NodeResult? Rejection = null, bool Forced = false);
