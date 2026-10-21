namespace ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;

/// <summary>保存通常方針を編集せず一時追跡を開始・解除する要求です。</summary>
/// <param name="OperationId">再送時も同じ操作識別子。</param>
/// <param name="WorkflowId">対象ワークフロー。</param>
/// <param name="Enabled">trueで一時開始、falseで保存通常方針へ戻します。</param>
/// <param name="ExpectedVersion">取得した一時操作の版。</param>
/// <param name="Selection">値の取得範囲、未指定時は全入力・出力。</param>
public sealed record TraceChange(Guid OperationId, string WorkflowId, bool Enabled, string? ExpectedVersion, TraceSelection? Selection = null);
