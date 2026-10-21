using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>一件の中でのみ合流できる対象別ノード結果です。</summary>
/// <param name="NodeId">出力元ノード。</param>
/// <param name="Outcome">全対象の結果。</param>
/// <param name="Targets">固定対象ごとの結果。</param>
/// <param name="SourceActivation">一件結果ではなく待受開始の結果か。</param>
/// <param name="SourceCompletion">一件結果ではなく待受終了の結果か。</param>
/// <param name="FileTransaction">元実行と分離した全対象補償の段階結果。</param>
/// <param name="ConfigurationOnly">実行は開始せず、コンパイル済みの純粋値だけを供給できるか。</param>
/// <param name="Iteration">要素ごとの有限な反復結果。</param>
/// <param name="RetainedValue">開始信号で確定した過去の値を参照しているか。</param>
/// <param name="SignalId">同じ結果の再参照を新しい信号にしない識別子。</param>
/// <param name="ElementStart">反復全体の完了ではなく要素の開始を供給しているか。</param>
/// <param name="PcSelection">この一回の入力から解決したPC候補と選択根拠です。</param>
/// <param name="AwaitingEvent">この評価では未到着で、合流事象側が後続を開始するか。</param>
internal sealed record ExecutionFrame(string NodeId, EOutcome Outcome, IReadOnlyList<TargetNodeResult> Targets, bool SourceActivation = false, bool SourceCompletion = false, FileTransactionResult? FileTransaction = null, bool ConfigurationOnly = false, IterationExecutionSummary? Iteration = null, bool RetainedValue = false, Guid SignalId = default, bool ElementStart = false, Contracts.Targets.PcSelectionDecision? PcSelection = null, bool AwaitingEvent = false);
