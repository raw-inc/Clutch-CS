using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;

/// <summary>編集画面の診断に必要な成否だけを保持し、入力・出力・自由記述メッセージは含めません。</summary>
/// <param name="WorkflowId">実行したワークフローです。</param>
/// <param name="Revision">実行時の設定版です。</param>
/// <param name="NodeId">実行したノードです。</param>
/// <param name="PeerId">実行先です。</param>
/// <param name="Outcome">確認した成否です。</param>
/// <param name="ReasonCode">安定した理由識別子です。</param>
/// <param name="CompletedAt">元の実行が完了した時刻です。</param>
public sealed record NodeExecutionObservation(string WorkflowId, string Revision, string NodeId, string PeerId, EOutcome Outcome, string? ReasonCode, DateTimeOffset CompletedAt)
{
    /// <summary>同じ完了結果の再読込みを識別する原実行IDです。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ExecutionId
    {
        get; init;
    }

    /// <summary>元の実行が完了してからの経過時間です。元の記録日時は変更しません。</summary>
    [JsonIgnore]
    public ObservationAge? CompletionAge
    {
        get; init;
    }

    /// <summary>応答時点の経過時間です。受信後は受信側の単調時計で続きを測ります。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompletionAgeTicks
    {
        get; init;
    }

    /// <summary>完了順序を採番したCore実体です。別実体の連番とは比較しません。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SourceInstanceId
    {
        get; init;
    }

    /// <summary>同じ実体で完了を確認した順序です。通信時間の差で新旧が逆転するのを防ぎます。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompletionSequence
    {
        get; init;
    }
}
