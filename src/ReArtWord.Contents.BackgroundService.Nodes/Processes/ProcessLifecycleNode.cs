using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Processes;

/// <summary>登録アプリケーションの起動・終了・再起動を独立した開始信号へ変換します。</summary>
internal sealed class ProcessLifecycleNode(IProcessService service, IWorkflowEvents events) : ISourceNode
{
    private static readonly string[] eventNames = ["application.started", "application.stopped", "application.restarted"];

    /// <summary>登録IDは維持し、エディタでは論理名から選択します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("process.lifecycle", "Application lifecycle", "Processes",
        """{"type":"object","required":["processId"],"properties":{"processId":{"type":"string","minLength":1}}}""",
        [], [new("value"), new("pid", EPortValueKind.Json) { DataType = new("integer", Optional: true) }, new("started"), new("stopped"), new("restarted")], kind: ENodeKind.Source) with
    {
        Location = EExecutionLocation.ExplicitPeer,
        Description = "登録アプリケーションの起動・停止・再起動を監視し、対応するSignalと終了コードを出力します。監視開始時の現在状態では発火しません。",
    };

    /// <summary>他Peerの登録へ照会せず、保存値の構造を検証します。</summary>
    /// <returns>設定の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => NodeSchemaValidator.Validate(definition, Descriptor);

    /// <summary>購読を確立してから受信を公開し、監視と購読の両方を所有者の停止時に解放します。</summary>
    /// <returns>待ち受けの終了結果です。</returns>
    // 抑制理由: 購読と観測は並行開始し、finallyで取消し後に両方を必ずawaitします。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "購読と観測はこの待受が所有し、finallyで終了確認してから資源を解放します。")]
    public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context.Initial, this);
        if (invalid is not null)
        {
            return invalid;
        }
        string id = new NodeSettings(context.Initial.Settings).ReadText("processId")!;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.StopToken);
        IAsyncEnumerator<WorkflowEvent> subscription = events.SubscribeAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable subscriptionLifetime = subscription.ConfigureAwait(false);
        Task<bool>? reading = null;
        Task? observing = null;
        try
        {
            reading = subscription.MoveNextAsync().AsTask();
            ProcessOperationResult initial = await service.ObserveAsync(id, lifetime.Token).ConfigureAwait(false);
            if (initial.Outcome != EOutcome.Succeeded)
            {
                return ProcessActionNode.Result(initial);
            }
            observing = ObserveAsync(id, lifetime.Token);
            await context.ReportStatusAsync(new(ESourceState.Listening), lifetime.Token).ConfigureAwait(false);
            while (!lifetime.IsCancellationRequested)
            {
                await Task.WhenAny(reading, observing).ConfigureAwait(false);
                if (observing.IsCompleted)
                {
                    await observing.ConfigureAwait(false);
                    break;
                }
                if (!await reading.ConfigureAwait(false))
                {
                    break;
                }
                WorkflowEvent item = subscription.Current;
                if (string.Equals(item.PeerId, context.Initial.PeerId, StringComparison.Ordinal) && eventNames.Contains(item.Name, StringComparer.Ordinal))
                {
                    ProcessLifecycleEvent? change = item.Data.Json.Deserialize<ProcessLifecycleEvent>(ContractJson.Options);
                    if (change is not null && string.Equals(change.Id, id, StringComparison.Ordinal))
                    {
                        await context.EmitAsync(context.BeginItem(), new(EOutcome.Succeeded, "success", new Dictionary<string, NodeValue>(StringComparer.Ordinal)
                        {
                            ["value"] = item.Data.Copy(),
                            ["pid"] = NodeValue.FromObject(change.Identity?.ProcessId),
                            [change.Kind] = NodeValue.FromObject(true),
                        }), lifetime.Token).ConfigureAwait(false);
                    }
                }
                reading = subscription.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 正常停止と取消しを最後に分けます。
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(reading ?? Task.CompletedTask, observing ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // 保留中の購読と観測が終了してから購読資源を解放します。
            }
        }
        return cancellationToken.IsCancellationRequested ? NodeResult.Failure("process.watchCancelled", "アプリケーション監視を取り消しました。", EOutcome.Cancelled)
                                                         : NodeResult.Success();
    }

    /// <summary>登録の自動復旧が無効でも、実プロセスの終了を検知します。</summary>
    /// <returns>購読寿命に従う観測作業です。</returns>
    private async Task ObserveAsync(string id, CancellationToken cancellationToken = default)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(service.Options.PollMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await service.ObserveAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }
}
