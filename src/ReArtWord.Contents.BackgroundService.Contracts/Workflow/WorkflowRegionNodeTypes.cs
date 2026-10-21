namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>反復と同時実行制限で共通の実行区間の接続契約です。</summary>
public static class WorkflowRegionNodeTypes
{
    /// <summary>区間を開始する種類を判定します。</summary>
    /// <returns>反復または同時実行制限の開始ならtrueです。</returns>
    public static bool IsStart(string type) => IterationNodeTypes.IsStart(type) || ConcurrencyNodeTypes.IsStart(type);

    /// <summary>区間を終了する種類を判定します。</summary>
    /// <returns>反復または同時実行制限の終了ならtrueです。</returns>
    public static bool IsEnd(string type) => IterationNodeTypes.IsEnd(type) || ConcurrencyNodeTypes.IsEnd(type);

    /// <summary>開始と終了を対応付けるポートを返します。</summary>
    /// <returns>同時実行制限はscope、反復はloopです。</returns>
    public static string PairPort(string type) => ConcurrencyNodeTypes.IsStart(type) || ConcurrencyNodeTypes.IsEnd(type) ? ConcurrencyNodeTypes.ScopePort
                                                                                                                         : IterationNodeTypes.LoopPort;

    /// <summary>区間内を開始する出力と、区間完了後の出力を区別します。</summary>
    /// <returns>区間内へ値や開始信号を渡す出力ならtrueです。</returns>
    public static bool IsBodyPort(string type, string port) => ConcurrencyNodeTypes.IsStart(type) ? port is "value" or "start" || Nodes.PropertyPorts.Parse(port) is { Output: "value" }
                                                                                                  : IterationNodeTypes.IsElementPort(port);

    /// <summary>異なる種類の開始・終了を誤って対応付けません。</summary>
    /// <returns>同じ種類の開始・終了ならtrueです。</returns>
    public static bool IsPair(string start, string end) => ConcurrencyNodeTypes.IsStart(start) ? ConcurrencyNodeTypes.IsEnd(end)
                                                                                               : IterationNodeTypes.IsStart(start) && IterationNodeTypes.IsEnd(end);
}
