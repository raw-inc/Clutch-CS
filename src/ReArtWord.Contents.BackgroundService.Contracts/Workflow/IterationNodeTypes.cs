namespace ReArtWord.Contents.BackgroundService.Contracts.Workflow;

/// <summary>反復範囲を接続する組込みノードとポートの共通識別子です。</summary>
public static class IterationNodeTypes
{
    /// <summary>逐次反復の種類です。</summary>
    public static string ForEach => "control.forEach";

    /// <summary>制限付き並列反復の種類です。</summary>
    public static string ParallelForEach => "control.parallelForEach";

    /// <summary>任意の反復終了境界です。</summary>
    public static string End => "control.forEach.end";

    /// <summary>開始と終了を対応させる専用接続です。</summary>
    public static string LoopPort => "loop";

    /// <summary>今回の残りを省略して次の要素へ進む信号入力です。</summary>
    public static string ContinuePort => "continue";

    /// <summary>未開始の要素を止めて反復範囲を終了する信号入力です。</summary>
    public static string BreakPort => "break";

    /// <summary>値入力と反復の制御信号を区別します。</summary>
    /// <returns>continueまたはbreakのポートかです。</returns>
    public static bool IsControlPort(string port) => string.Equals(port, ContinuePort, StringComparison.Ordinal) || string.Equals(port, BreakPort, StringComparison.Ordinal);

    /// <summary>真の信号だけを受理し、同時に揃った場合はbreakを優先します。</summary>
    /// <returns>要求されたポート名、信号がなければnullです。</returns>
    public static string? RequestedControl(IReadOnlyDictionary<string, Nodes.NodeValue> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (string port in new[] { BreakPort, ContinuePort })
        {
            if (inputs.TryGetValue(port, out Nodes.NodeValue? value) && value.Kind == Nodes.EValueKind.Json && value.Json.ValueKind == System.Text.Json.JsonValueKind.True)
            {
                return port;
            }
        }
        return null;
    }

    /// <summary>一件内の有限反復を開始する種類を判定します。</summary>
    /// <returns>逐次または並列の開始ノードかです。</returns>
    public static bool IsStart(string type) => string.Equals(type, ForEach, StringComparison.Ordinal) || string.Equals(type, ParallelForEach, StringComparison.Ordinal);

    /// <summary>反復終了ノードを判定します。</summary>
    /// <returns>終了境界かです。</returns>
    public static bool IsEnd(string type) => string.Equals(type, End, StringComparison.Ordinal);

    /// <summary>要素ごとに下流を開始する出力を判定します。</summary>
    /// <returns>要素・添字・総数・要素開始信号のいずれかです。</returns>
    public static bool IsElementPort(string port) => port is "item" or "index" or "count" or "start" || Nodes.PropertyPorts.Parse(port) is { Output: "item" };
}
