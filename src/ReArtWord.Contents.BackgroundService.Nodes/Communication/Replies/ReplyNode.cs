using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Communication.Replies;

/// <summary>ワークフローで決めた応答を元の受付Peerから一度送信します。</summary>
internal sealed class ReplyNode : IActionNode
{
    private readonly string protocol;
    private readonly CommunicationReplyRegistry replies;

    /// <summary>sourceと共有する応答registryを注入します。</summary>
    internal ReplyNode(string protocol, CommunicationReplyRegistry replies)
    {
        this.protocol = protocol;
        this.replies = replies;
        Descriptor = new(protocol + ".reply", protocol + " reply", "Communication", "request handleの所有Peerから一回だけ応答します。", ENodeKind.Action, EExecutionLocation.ExplicitPeer,
            [new("request", EPortValueKind.Json, true), new("value", EPortValueKind.Any, true)], [new("value")], ["success"], NodeSettings.Schema("""{"type":"object","properties":{}}""")) { HasAsyncCompletion = true };
    }

    /// <summary>ハンドルと返信値を要求します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>実行先の環境を解決せず設定を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        return NodeSchemaValidator.Validate(definition, Descriptor);
    }

    /// <summary>opaque ハンドルの寿命内で実際の返信を確認します。</summary>
    /// <returns>未実行と送信後不明を区別した結果です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        string requestName = "request";
        string valueName = "value";
        if (!context.Inputs.TryGetValue(requestName, out NodeValue? request) || !context.Inputs.TryGetValue(valueName, out NodeValue? value))
        {
            return NodeResult.Failure("reply.input.missing", "requestとvalueの入力が必要です。", EOutcome.NotExecuted);
        }
        return await replies.ReplyAsync(context.PeerId, protocol, request, value, cancellationToken).ConfigureAwait(false);
    }
}
