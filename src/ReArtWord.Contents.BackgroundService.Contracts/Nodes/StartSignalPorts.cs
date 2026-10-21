namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>条件ゲートと継続入力が供給する開始信号の契約を共有します。</summary>
public static class StartSignalPorts
{
    /// <summary>後続の開始入力へ接続する出力名です。</summary>
    public static string Output => "start";

    /// <summary>同名の一般データ出力を開始信号と誤認しません。</summary>
    /// <returns>入力の存在を開始条件として扱う出力かです。</returns>
    public static bool IsOutput(NodeDescriptor? descriptor, string port)
        => CompletionPorts.IsSignal(descriptor, port) || descriptor?.Type is "control.if" && port is "trueStart" or "falseStart"
            || descriptor?.Type is "process.lifecycle" && port is "started" or "stopped" or "restarted"
            || string.Equals(port, Output, StringComparison.Ordinal)
            && (descriptor?.Kind == ENodeKind.Source || descriptor?.Type is "control.ifStart" or "control.ifNotStart" or "control.button" or "control.toggleButton"
                || descriptor is not null && (Workflow.WorkflowRegionNodeTypes.IsStart(descriptor.Type) || EventMergePorts.IsNode(descriptor.Type)));

    /// <summary>ノード固有の受信データを保ち、共通開始信号を一度だけ公開します。</summary>
    /// <returns>Sourceだけに共通出力を追加した宣言です。</returns>
    public static NodeDescriptor? WithSourceOutput(NodeDescriptor? descriptor)
    {
        if (descriptor?.Kind != ENodeKind.Source || descriptor.Outputs.Any(port => string.Equals(port.Name, Output, StringComparison.Ordinal)))
        {
            return descriptor;
        }
        return descriptor with { Outputs = [.. descriptor.Outputs, new(Output, EPortValueKind.Json)] };
    }
}
