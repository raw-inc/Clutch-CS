using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Control;

/// <summary>遅い購読先、重複通知、大きな値が他の購読を妨げないことを確認します。</summary>
public sealed class WorkflowEventHubTests
{
    /// <summary>エンジンの例外も不明結果として通知し、既存の呼出元へ元の例外を維持します。</summary>
    /// <returns>通知の追加が実行失敗を成功へ変えない確認。</returns>
    [Fact]
    public async Task UnexpectedExecutionFailurePublishesUnknownAndPreservesTheExceptionAsync()
    {
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fixtureLifetime = fixture.ConfigureAwait(true);
        WorkflowEventHub events = new();
        fixture.Engine.Events = events;
        fixture.Dispatcher.Handler = (_, _) => throw new InvalidOperationException("test execution failure");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        IAsyncEnumerator<WorkflowEvent> reader = events.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        Runtime.Workflows.Compilation.CompiledWorkflow plan = fixture.Compiler.Compile(new("failure", "Failure event", [WorkflowFixture.Definition("action")], [])).Workflow!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.ExecuteAsync(plan, cancellationToken: timeout.Token)).ConfigureAwait(true);
        Assert.True(await waiting.ConfigureAwait(true));
        Assert.Equal("workflow.started", reader.Current.Name);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("workflow.completed", reader.Current.Name);
        Assert.Equal("unknown", reader.Current.Data.Json.GetProperty("report").GetProperty("outcome").GetString());
        Assert.Equal("workflow.resultUnknown", reader.Current.Data.Json.GetProperty("detailReason").GetString());
    }

    /// <summary>一つの購読キューが満杯でも、別の購読へ配信を続けて欠落数を示します。</summary>
    /// <returns>容量超過と独立した購読の継続確認。</returns>
    [Fact]
    public async Task SlowSubscriberDoesNotBlockPublisherOrOtherSubscriberAsync()
    {
        WorkflowEventHub hub = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        IAsyncEnumerator<WorkflowEvent> slow = hub.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        IAsyncEnumerator<WorkflowEvent> fast = hub.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable slowLifetime = slow.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fastLifetime = fast.ConfigureAwait(true);
        Task<bool> waiting = slow.MoveNextAsync().AsTask();
        Task<bool> ready = fast.MoveNextAsync().AsTask();
        await hub.PublishAsync(Event(0), timeout.Token).ConfigureAwait(true);
        Assert.True(await waiting.ConfigureAwait(true));
        Assert.True(await ready.ConfigureAwait(true));
        for (int index = 1; index <= 300; index++)
        {
            await hub.PublishAsync(Event(index), timeout.Token).ConfigureAwait(true);
            Assert.True(await fast.MoveNextAsync().ConfigureAwait(true));
            Assert.Equal(index, fast.Current.Data.Json.GetInt32());
        }
        Assert.Equal(301, hub.Statistics.Published);
        Assert.Equal(44, hub.Statistics.DroppedDeliveries);
        Assert.Equal("event.subscriberFull", hub.Statistics.LastProblem);
    }

    /// <summary>同じIDは一度だけ流し、省略した値を理由付きで表示します。</summary>
    /// <returns>重複除外と値省略の確認。</returns>
    [Fact]
    public async Task DuplicatesAndOversizedValuesRemainExplicitAsync()
    {
        WorkflowEventHub hub = new("actual-peer", Guid.NewGuid());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        IAsyncEnumerator<WorkflowEvent> reader = hub.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
        Task<bool> waiting = reader.MoveNextAsync().AsTask();
        WorkflowEvent original = Event(1);
        await hub.PublishAsync(original, timeout.Token).ConfigureAwait(true);
        Assert.True(await waiting.ConfigureAwait(true));
        Assert.Equal("actual-peer", reader.Current.PeerId);
        Assert.NotNull(reader.Current.PeerInstanceId);
        await hub.PublishAsync(original, timeout.Token).ConfigureAwait(true);
        WorkflowEvent large = Event(2) with { Data = NodeValue.FromObject(new string('x', 100000)) };
        await hub.PublishAsync(large, timeout.Token).ConfigureAwait(true);
        Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal(large.Id, reader.Current.Id);
        Assert.Equal("event.dataOmitted", reader.Current.DetailReason);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, reader.Current.Data.Json.ValueKind);
        Assert.Equal(2, hub.Statistics.Published);
        Assert.Equal(1, hub.Statistics.OmittedValues);
    }

    /// <summary>内容の順序を確認する小さな事象を作ります。</summary>
    /// <returns>独立したIDを持つ事象。</returns>
    private static WorkflowEvent Event(int index) => new(Guid.NewGuid(), "test", DateTimeOffset.UtcNow, "test-peer", NodeValue.FromObject(index));
}
