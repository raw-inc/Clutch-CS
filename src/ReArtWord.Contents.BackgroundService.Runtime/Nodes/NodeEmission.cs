using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Nodes;

/// <summary>取得開始時の設定版を保った継続処理の一件結果です。</summary>
/// <param name="Context">取得開始時点の文脈。</param>
/// <param name="Result">一件自身の結果。</param>
/// <param name="IsTerminal">一件入力ではなく待受自体の一度だけの終了通知か。</param>
public sealed record NodeEmission(NodeExecutionContext Context, NodeResult Result, bool IsTerminal = false)
{
    /// <summary>通信結果ではなく、一件の正常入力を示す開始信号を実行グラフへ供給します。</summary>
    /// <returns>待受終了・失敗には開始信号を含めない実行用の結果です。</returns>
    internal NodeResult WorkflowResult()
    {
        Dictionary<string, NodeValue> outputs = new(Result.Outputs, StringComparer.Ordinal);
        outputs.Remove(StartSignalPorts.Output);
        if (!IsTerminal && Result.Outcome == EOutcome.Succeeded)
        {
            outputs[StartSignalPorts.Output] = NodeValue.FromObject(true);
        }
        return Result with { Outputs = outputs };
    }
}
