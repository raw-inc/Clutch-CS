using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>入力を発生させた待受の、新しい入力受付だけを正常終了します。</summary>
public sealed class EndSourceNode(Func<SourceExecutionInfo, CancellationToken, ValueTask<NodeResult>> stop) : IActionNode
{
    /// <summary>Sourceのsource出力を条件分岐経由で受け付けます。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.source.end", "待受の正常終了", "Control", """{"type":"object","properties":{}}""",
        [new("source", EPortValueKind.Json, Required: true)]) with
    {
        HasAsyncCompletion = true,
        Description = "Sourceのsource出力を接続し、元の待受に新規入力の受付終了を要求します。条件分岐の選択経路につなぐと、その条件が成立したときだけ終了できます。受付済みの処理は継続し、待受の最終結果はcompleted経路へ出力します。",
    };

    /// <summary>設定と必須入力を共通の規則で検証します。</summary>
    /// <returns>開始前に修正する項目。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>元の待受IDとPeer起動IDを含む入力だけを停止先へ渡します。</summary>
    /// <returns>受付終了の要求結果。受付済み処理の完了とは別です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeValue? input = NodeSupport.Input(context, "source");
        SourceExecutionInfo? source = null;
        try
        {
            if (input?.Kind == EValueKind.Json)
            {
                source = input.Json.Deserialize<SourceExecutionInfo>(ContractJson.Options);
            }
        }
        catch (JsonException)
        {
            return NodeResult.Failure("source.referenceInvalid", "Sourceのsource出力を接続してください。", EOutcome.NotExecuted);
        }
        if (source is null || source.Id == Guid.Empty || source.PeerInstanceId is null || source.PeerInstanceId == Guid.Empty
            || string.IsNullOrWhiteSpace(source.WorkflowId) || string.IsNullOrWhiteSpace(source.NodeId) || string.IsNullOrWhiteSpace(source.PeerId))
        {
            return NodeResult.Failure("source.identity", "元の待受IDとPeerの起動IDを含むsource出力が必要です。", EOutcome.NotExecuted);
        }
        return await stop(source, cancellationToken).ConfigureAwait(false);
    }
}
