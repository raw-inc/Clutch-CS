using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>設定世代を取得した入力と、その入力の完了を別々に制御します。</summary>
internal sealed class SettingsSourceProbe : ISourceNode
{
    /// <summary>実Sourceが固定した文脈です。</summary>
    internal Channel<NodeExecutionContext> Contexts
    {
        get;
    } = Channel.CreateUnbounded<NodeExecutionContext>();

    /// <summary>取得済み入力を完了させる値です。</summary>
    internal Channel<NodeValue> Items
    {
        get;
    } = Channel.CreateUnbounded<NodeValue>();

    /// <summary>設定ポートを必須とするSourceです。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("test.settingSource", "Settings source", "Test",
        """{"type":"object","required":["port"],"properties":{"port":{"type":"integer","minimum":1,"maximum":100}}}""", inputs: [], kind: ENodeKind.Source);

    /// <summary>実行に渡す設定を検証します。</summary>
    /// <returns>設定の不一致です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSchemaValidator.Validate(definition, Descriptor);

    /// <summary>受付済み入力は正常Stopで取り消さず、元文脈のまま完了させます。</summary>
    /// <returns>正常停止または明示取消です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        while (!context.IsStopRequested)
        {
            NodeExecutionContext item = context.BeginItem();
            await Contexts.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            NodeValue value = await Items.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            await context.EmitAsync(item, NodeResult.Success(value), cancellationToken).ConfigureAwait(false);
        }
        return NodeResult.Success();
    }
}
