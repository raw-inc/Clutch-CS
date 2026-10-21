using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>ノード自身の結果です。後続処理の成否を含めません。</summary>
/// <param name="Outcome">処理の実施結果。</param>
/// <param name="Route">正常な分岐、または共通の失敗経路。</param>
/// <param name="Outputs">渡すポートと値。</param>
/// <param name="ReasonCode">安定した理由種類。</param>
/// <param name="Message">人向け説明。</param>
public sealed record NodeResult(
    EOutcome Outcome,
    string Route,
    IReadOnlyDictionary<string, NodeValue> Outputs,
    string? ReasonCode = null,
    string? Message = null)
{
    /// <summary>外部サービスの成否とは別に、申告したファイル範囲へのアクセスが終了したことを示します。未指定時は終了を推測しません。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FileAccessCompleted
    {
        get; init;
    }

    /// <summary>正常に取得した値を返します。</summary>
    /// <returns>ノード自身の正常結果です。</returns>
    public static NodeResult Success(NodeValue? value = null, string route = "success")
    {
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal);
        if (value is not null)
        {
            outputs.Add("value", value.Copy());
        }
        return new NodeResult(EOutcome.Succeeded, route, outputs);
    }

    /// <summary>実施結果と原因を持つ共通の非成功結果を返します。</summary>
    /// <returns>明示経路へ配線できる結果です。</returns>
    public static NodeResult Failure(string code, string message, EOutcome outcome = EOutcome.Failed)
    {
        string route = outcome switch
        {
            EOutcome.NotExecuted => "notExecuted",
            EOutcome.Unknown => "unknown",
            EOutcome.Cancelled => "cancelled",
            _ => "failure",
        };
        return new NodeResult(outcome, route, System.Collections.Frozen.FrozenDictionary<string, NodeValue>.Empty, code, message);
    }
}
