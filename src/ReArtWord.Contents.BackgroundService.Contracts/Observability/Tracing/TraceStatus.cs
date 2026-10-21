namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>保存方針・一時操作・現在のローカル反映を区別します。</summary>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="Saved">保存した通常追跡方針。</param>
/// <param name="Effective">現在読み込んだ実効方針。</param>
/// <param name="Temporary">一時追跡中か。</param>
/// <param name="Version">一時操作行の版。</param>
/// <param name="Loaded">保存状態を確認できたか。</param>
/// <param name="Reason">確認不能な理由。</param>
/// <param name="ActiveNormal">現在有効な定義の通常方針。保存のみの最新変更と区別します。</param>
public sealed record TraceStatus(string WorkflowId, TracePolicy Saved, TracePolicy Effective, bool Temporary, string? Version, bool Loaded, string? Reason = null, TracePolicy? ActiveNormal = null);
