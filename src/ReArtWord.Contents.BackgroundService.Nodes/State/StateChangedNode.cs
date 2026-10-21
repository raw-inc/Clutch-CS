using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Nodes.State;

/// <summary>一つの永続Stateが変わるたび、変更時の値を独立した実行へ渡します。</summary>
internal sealed class StateChangedNode(INodeStateStore store) : ISourceNode
{
    /// <summary>待受対象と変更値を公開します。開始時の既存値では発火しません。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("state.changed", "状態の変更を待つ", "State",
        """{"type":"object","required":["scope"],"properties":{"scope":{"type":"string","enum":["workflow","peer","cluster"],"default":"workflow"},"key":{"type":"string","minLength":1}}}""",
        [new("key", OverridesSetting: "key")],
        [new("value"), new("key") { DataType = new("text") }, new("version") { DataType = new("text", Optional: true) }, new("exists") { DataType = new("boolean") }],
        ["success"], ENodeKind.Source) with { Location = EExecutionLocation.ExplicitPeer };

    /// <summary>設定と接続可能な入力を検証します。</summary>
    /// <returns>実行前に修正する設定問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSupport.Validate(definition, Descriptor);

    /// <summary>初期値を発火せず、その後の値変更と削除だけを順に通知します。</summary>
    /// <returns>正常停止・取消・待受不能を区別する終了結果です。</returns>
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        if (store is not IObservableNodeStateStore observable)
        {
            return NodeResult.Failure("state.watch.unsupported", "状態の変更通知を利用できません。", EOutcome.NotExecuted);
        }
        NodeSettings settings = new(context.Initial.Settings);
        string keyPort = "key";
        string? key = settings.ReadText(keyPort);
        if (context.Initial.Inputs.TryGetValue(keyPort, out NodeValue? supplied))
        {
            key = supplied.GetString();
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            return NodeResult.Failure("state.key.required", "状態キーを文字列で指定してください。", EOutcome.NotExecuted);
        }
        string scope = NodeStateScope.Resolve(settings.ReadText("scope"), context.Initial.PeerId, context.Initial.WorkflowId);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        try
        {
            await foreach (StateChange change in observable.WatchAsync(scope, key, linked.Token).ConfigureAwait(false))
            {
                if (change.Initial)
                {
                    await context.ReportStatusAsync(new(ESourceState.Listening), linked.Token).ConfigureAwait(false);
                    continue;
                }
                if (context.IsStopRequested)
                {
                    break;
                }
                NodeResult result = new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                {
                    ["value"] = change.Value?.Copy() ?? NodeValue.Null,
                    ["key"] = NodeValue.FromObject(change.Key),
                    ["version"] = NodeValue.FromObject(change.Version),
                    ["exists"] = NodeValue.FromObject(change.Value is not null),
                });
                await context.EmitAsync(context.BeginItem(), result, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // 通常の待受停止とWFの取消は終了結果で区別します。
        }
        catch (Exception exception) when (exception is IOException or ChannelClosedException or NotSupportedException)
        {
            return NodeResult.Failure("state.watch.failed", exception.InnerException?.Message ?? exception.Message);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return NodeResult.Failure("state.watch.cancelled", "状態の待受を取り消しました。", EOutcome.Cancelled);
        }
        return NodeResult.Success();
    }
}
