using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Contracts.Nodes;

/// <summary>有限処理の完了通知と成否を、通常の値・結果経路から分けます。</summary>
public static class CompletionPorts
{
    /// <summary>確認できた処理終了時に供給する開始信号です。</summary>
    public static string Signal => "$completed";

    /// <summary>成功true・確定失敗false・未確定nullを返す出力です。</summary>
    public static string Success => "$succeeded";

    /// <summary>コンパイル時の純粋値を、実行完了と取り違えないための識別です。</summary>
    /// <returns>実行時だけ供給する共通出力かです。</returns>
    public static bool IsPort(string port) => port is "$completed" or "$succeeded";

    /// <summary>完了待ちを明示した有限処理だけに共通出力を公開します。</summary>
    /// <returns>同期的な値計算や条件分岐にはfalseです。</returns>
    public static bool Supports(NodeDescriptor? descriptor) => descriptor is { Kind: ENodeKind.Action, HasAsyncCompletion: true, IsTerminal: false };

    /// <summary>固有の値出力を維持して、共通出力を一度だけ追加します。</summary>
    /// <returns>有限処理の共通出力を含む宣言です。</returns>
    public static NodeDescriptor? WithOutputs(NodeDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return null;
        }
        if (!Supports(descriptor))
        {
            if (!descriptor.Outputs.Any(port => IsPort(port.Name)))
            {
                return descriptor;
            }
            return descriptor with { Outputs = descriptor.Outputs.Where(port => !IsPort(port.Name)).ToArray() };
        }
        List<PortDescriptor> outputs = [.. descriptor!.Outputs];
        foreach (string name in new[] { Signal, Success })
        {
            if (!outputs.Any(port => string.Equals(port.Name, name, StringComparison.Ordinal)))
            {
                outputs.Add(new(name, EPortValueKind.Json));
            }
        }
        return descriptor with { Outputs = outputs.ToArray() };
    }

    /// <summary>受信値の同名プロパティと共通完了信号を混同しません。</summary>
    /// <returns>共通完了信号の出力かです。</returns>
    public static bool IsSignal(NodeDescriptor? descriptor, string port) => Supports(descriptor) && string.Equals(port, Signal, StringComparison.Ordinal);

    /// <summary>成否出力だけに未確定nullを含む真偽型を付けます。</summary>
    /// <returns>成否型、または対象外ならnullです。</returns>
    public static Values.ValueTypeDefinition? PortType(NodeDescriptor? descriptor, string port)
        => Supports(descriptor) && string.Equals(port, Success, StringComparison.Ordinal) ? new("boolean", Optional: true)
                                                                                          : null;

    /// <summary>処理中や結果不明を失敗または成功に読み替えません。</summary>
    /// <returns>確認済みの真偽、またはnullです。</returns>
    public static bool? Status(EOutcome? outcome) => outcome switch
    {
        EOutcome.Succeeded => true,
        EOutcome.Failed => false,
        _ => null,
    };
}
