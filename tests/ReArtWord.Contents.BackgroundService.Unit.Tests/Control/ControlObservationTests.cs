using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>共通事象・ログ境界への実際の呼び出しを検証します。</summary>
public sealed class ControlObservationTests
{
    /// <summary>公開された事象が購読ノードの値として流れます。</summary>
    /// <returns>購読と一件通知の検証の完了。</returns>
    [Fact]
    public async Task PublishFeedsMatchingHookAndCompletesNormally()
    {
        ObservationSink sink = new();
        EventHookNode hook = new(sink);
        NodeExecutionContext item = NodeTestContext.Create("""{"name":"ready","count":1}""");
        List<NodeEmission> emissions = [];
        NodeSourceContext source = new(item, () => item, (emission, _) =>
        {
            emissions.Add(emission);
            return ValueTask.CompletedTask;
        }, () => false);
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(2));
        Task<NodeResult> subscription = hook.RunAsync(source, limit.Token);
        PublishEventNode publish = new(sink);
        await publish.ExecuteAsync(NodeTestContext.Create("""{"name":"ready"}""", NodeValue.FromObject(42)), limit.Token);
        Assert.Equal(EOutcome.Succeeded, (await subscription.ConfigureAwait(true)).Outcome);
        Assert.Single(emissions);
        Assert.Equal(42, emissions[0].Result.Outputs["value"].Json.GetInt32());
    }

    /// <summary>ログ境界へ因果情報と個人値指定を渡します。</summary>
    /// <returns>記録要求の検証の完了。</returns>
    [Fact]
    public async Task LogPreservesCausationAndPrivacyDesignation()
    {
        ObservationSink sink = new();
        NodeExecutionContext item = NodeTestContext.Create("""{"level":"WARN","message":"condition","personal":true}""", NodeValue.FromObject("private"));
        NodeResult result = await new WriteLogNode(sink).ExecuteAsync(item);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Single(sink.Logs);
        Assert.True(sink.Logs[0].ContainsPersonalData);
        Assert.Equal(item.CausationId, sink.Logs[0].CausationId);
    }
}

/// <summary>ノード間で事象とログを受け渡す試験用境界です。</summary>
internal sealed class ObservationSink : IWorkflowEvents, IWorkflowLog
{
    private readonly Channel<WorkflowEvent> channel = Channel.CreateUnbounded<WorkflowEvent>();

    /// <summary>記録要求を保持します。</summary>
    internal List<WorkflowLogEntry> Logs
    {
        get;
    } = [];

    /// <summary>事象を購読ストリームへ渡します。</summary>
    /// <returns>受付の完了です。</returns>
    public ValueTask PublishAsync(WorkflowEvent item, CancellationToken cancellationToken = default)
    {
        return channel.Writer.WriteAsync(item, cancellationToken);
    }

    /// <summary>購読後の事象を返します。</summary>
    /// <returns>事象ストリームです。</returns>
    public IAsyncEnumerable<WorkflowEvent> SubscribeAsync(CancellationToken cancellationToken = default)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }

    /// <summary>ログの記録要求を保持します。</summary>
    /// <returns>受付の完了です。</returns>
    public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Logs.Add(entry);
        return ValueTask.CompletedTask;
    }
}
