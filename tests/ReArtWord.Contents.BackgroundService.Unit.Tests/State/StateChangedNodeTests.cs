using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.State;
using ReArtWord.Contents.BackgroundService.Nodes.State;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Data;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.State;

/// <summary>State待受が初期値で発火せず、独立した値と実行文脈を通知することを確認します。</summary>
public sealed class StateChangedNodeTests
{
    /// <summary>継続更新と正常停止・取消を、保存値の寿命とは独立して扱います。</summary>
    /// <returns>待受の開始、二件の発火と停止後の永続値です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmitsIndependentChangesAndReleasesWaitingAsync(bool cancel)
    {
        using MediaTestFiles files = new();
        SqliteRepository repository = new(Path.Combine(files.Root, "state.db"));
        NodeStateStore store = new(repository, "peer");
        ISourceNode node = Assert.IsAssignableFrom<ISourceNode>(StateNodeRegistration.Create(store).Single(item => item.Descriptor.Type is "state.changed"));
        const string scope = "peer:peer:workflow:workflow";
        await store.CompareExchangeAsync(scope, "cue", null, NodeValue.FromObject(10)).ConfigureAwait(true);
        using CancellationTokenSource stop = new(), cancellation = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously), delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<NodeEmission> emissions = new();
        NodeExecutionContext initial = NodeTestContext.Create("""{"scope":"peer","key":"cue"}""");
        NodeSourceContext source = new(initial, () => initial with { ExecutionId = Guid.NewGuid() }, (emission, _) =>
        {
            emissions.Enqueue(emission);
            if (emissions.Count == 2)
            {
                delivered.TrySetResult();
            }
            return ValueTask.CompletedTask;
        }, () => stop.IsCancellationRequested, (_, _) =>
        {
            ready.TrySetResult();
            return ValueTask.CompletedTask;
        }, stop.Token);
        Task<NodeResult> running = node.RunAsync(source, cancellation.Token);
        await ready.Task.WaitAsync(cancellation.Token).ConfigureAwait(true);
        Assert.Empty(emissions);
        foreach (int value in new[] { 11, 12 })
        {
            string version = (await store.ReadAsync(scope, "cue", cancellation.Token).ConfigureAwait(true))!.Version;
            Assert.True((await store.CompareExchangeAsync(scope, "cue", version, NodeValue.FromObject(value), cancellation.Token).ConfigureAwait(true)).Applied);
        }
        await delivered.Task.WaitAsync(cancellation.Token).ConfigureAwait(true);
        Assert.Equal([11, 12], emissions.Select(item => item.Result.Outputs["value"].Json.GetInt32()));
        Assert.Equal(2, emissions.Select(item => item.Context.ExecutionId).Distinct().Count());
        if (cancel)
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
        }
        else
        {
            await stop.CancelAsync().ConfigureAwait(true);
        }
        EOutcome expected = EOutcome.Succeeded;
        if (cancel)
        {
            expected = EOutcome.Cancelled;
        }
        Assert.Equal(expected, (await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true)).Outcome);
        NodeStateStore reopened = new(new SqliteRepository(Path.Combine(files.Root, "state.db")), "peer");
        Assert.Equal(12, (await reopened.ReadAsync(scope, "cue").ConfigureAwait(true))!.Value.Json.GetInt32());
    }
}
