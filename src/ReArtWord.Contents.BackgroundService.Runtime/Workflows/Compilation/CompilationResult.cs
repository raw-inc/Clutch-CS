using ReArtWord.Contents.BackgroundService.Contracts.Configuration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>ワークフロー定義の検証結果です。</summary>
/// <param name="Workflow">検証成功時の実行計画。</param>
/// <param name="Issues">訂正が必要な設定・接続。</param>
public sealed record CompilationResult(CompiledWorkflow? Workflow, IReadOnlyList<ValidationIssue> Issues);
