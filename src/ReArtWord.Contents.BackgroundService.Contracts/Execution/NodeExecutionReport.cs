using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution;

/// <summary>一回のノード実行の集約結果と対象別結果です。</summary>
/// <param name="NodeId">ノード識別子。</param>
/// <param name="Outcome">全対象を含む一回の結果。</param>
/// <param name="Targets">開始時に固定した各対象の結果。</param>
/// <param name="FileTransaction">全対象補償を選んだ場合の準備・実行・復元・解放結果。</param>
/// <param name="Iteration">反復開始ノードの要素別結果。</param>
/// <param name="PcSelection">実行PCの候補とリソース比較に使った観測。</param>
public sealed record NodeExecutionReport(
    string NodeId,
    EOutcome Outcome,
    IReadOnlyList<TargetNodeResult> Targets,
    FileTransactionResult? FileTransaction = null,
    IterationExecutionSummary? Iteration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PcSelectionDecision? PcSelection = null);
