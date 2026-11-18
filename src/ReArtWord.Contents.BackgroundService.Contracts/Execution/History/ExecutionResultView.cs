using System.Text.Json.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Contracts.Execution.History;

/// <summary>小さい実行結果と、大きい結果の要約を明確に区別します。</summary>
/// <param name="Report">完全な結果。省略時はnull。</param>
/// <param name="Summary">省略時にも保持する確定結果。</param>
/// <param name="DetailReason">詳細を含めない理由。省略がなければnull。</param>
public sealed record ExecutionResultView(ExecutionReport? Report, ExecutionReportSummary? Summary = null, string? DetailReason = null)
{
    /// <summary>実行元で完了を確認してからの経過時間です。中継や再読込みで計測をやり直しません。</summary>
    [JsonIgnore]
    public ObservationAge? CompletionAge
    {
        get; init;
    }

    /// <summary>同じCore実体で完了を確認した順序です。日時補正で前後しない直近表示に使い、永続履歴へは含めません。</summary>
    [JsonIgnore]
    public long? CompletionOrder
    {
        get; init;
    }

    /// <summary>再起動前の履歴では完了時の時計を復元できないため、再読込からの経過時間を古さの下限としてだけ使います。</summary>
    [JsonIgnore]
    public bool CompletionAgeIsLowerBound
    {
        get; init;
    }

    /// <summary>読取り応答時点の完了からの経過時間です。受信側は通信時間と受信後の経過時間を加えます。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CompletionAgeTicks
    {
        get; init;
    }

    /// <summary>観測応答内の完了順序です。ObservationInstanceIdが同じ応答間でだけ比較します。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ObservationSequence
    {
        get; init;
    }
}
