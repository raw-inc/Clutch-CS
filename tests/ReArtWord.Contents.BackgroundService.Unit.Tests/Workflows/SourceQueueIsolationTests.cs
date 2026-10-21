using System.Collections.Concurrent;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>停止入力の保持が無関係な一件を占有しないことを検証します。</summary>
public sealed class SourceQueueIsolationTests
{
    /// <summary>少数でも大きい入力にはバイト上限を適用し、別ワークフローを進めます。</summary>
    /// <returns>保持サイズの拒否と解放の確認です。</returns>
    [Fact]
    public async Task LargeStoppedInputsRespectByteCapacityAndReleaseAfterDrain()
    {
        BurstSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(true);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("large", EStoppedInputPolicy.All)).Workflow!, timeout.Token).ConfigureAwait(true);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("free", EStoppedInputPolicy.Drop)).Workflow!, timeout.Token).ConfigureAwait(true);
        fixture.Safety.SetStopped("large", "source", true);
        NodeValue payload = NodeValue.FromBytes(new byte[12 * 1024 * 1024]);
        await source.EmitValueAsync("large", payload, timeout.Token).ConfigureAwait(true);
        await source.EmitValueAsync("large", payload, timeout.Token).ConfigureAwait(true);
        await source.EmitValueAsync("large", payload, timeout.Token).ConfigureAwait(true);
        Assert.Equal(2, fixture.Sources.Statistics.Held);
        Assert.Contains(fixture.Sources.Failures.Values, result => string.Equals(result.ReasonCode, "source.payloadCapacity", StringComparison.Ordinal));
        await source.EmitAsync("free", 42, timeout.Token).ConfigureAwait(true);
        Assert.Equal("free", (await observed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true)).WorkflowId);
        Assert.InRange(fixture.Sources.Statistics.RetainedValueBytes, 24L * 1024 * 1024, 25L * 1024 * 1024);
        fixture.Safety.SetStopped("large", "source", false);
        _ = await observed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        _ = await observed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        while (fixture.Sources.Statistics.RetainedValueBytes != 0)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
    }

    /// <summary>128件を超える停止入力の後でも別ワークフローを実行できます。</summary>
    /// <returns>実タスクの検証完了。</returns>
    [Fact]
    public async Task FullStoppedBufferDoesNotBlockIndependentWorkflow()
    {
        BurstSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("held", EStoppedInputPolicy.All)).Workflow!, timeout.Token);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("free", EStoppedInputPolicy.Drop)).Workflow!, timeout.Token);
        fixture.Safety.SetStopped("held", "source", true);
        for (int index = 0; index < 200; index++)
        {
            await source.EmitAsync("held", index, timeout.Token);
        }
        await source.EmitAsync("free", 999, timeout.Token);
        NodeExecutionContext actual = await observed.Reader.ReadAsync(timeout.Token);
        Assert.Equal("free", actual.WorkflowId);
        Assert.Equal(999, actual.Inputs["value"].Json.GetInt32());
        Assert.Contains(fixture.Sources.Failures.Values, result => string.Equals(result.ReasonCode, "safety.capacity", StringComparison.Ordinal));
    }

    /// <summary>下流の安全停止で64実行が待機しても別ワークフローは進行します。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task StoppedDownstreamDoesNotBlockIndependentWorkflow()
    {
        BurstSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition blocked = Definition("held", EStoppedInputPolicy.Drop);
        blocked = blocked with { Nodes = [blocked.Nodes[0], blocked.Nodes[1] with { Safety = new NodeSafetyPolicy(false, false, EStoppedInputPolicy.All) }] };
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(blocked).Workflow!, timeout.Token);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("free", EStoppedInputPolicy.Drop)).Workflow!, timeout.Token);
        fixture.Safety.SetStopped("held", "sink", true);
        for (int index = 0; index < 600; index++)
        {
            await source.EmitAsync("held", index, timeout.Token);
        }
        await source.EmitAsync("free", 999, timeout.Token);
        Assert.Equal("free", (await observed.Reader.ReadAsync(timeout.Token)).WorkflowId);
    }

    /// <summary>停止中の大量入力を実行枠なしで最新一件へ置き換えます。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task LatestReplacesBeforeExecutionSlots()
    {
        BurstSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition definition = Definition("latest", EStoppedInputPolicy.Latest);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(definition).Workflow!, timeout.Token);
        fixture.Safety.SetStopped("latest", "source", true);
        for (int index = 0; index < 200; index++)
        {
            await source.EmitAsync("latest", index, timeout.Token);
        }
        fixture.Safety.SetStopped("latest", "source", false);
        NodeExecutionContext actual = await observed.Reader.ReadAsync(timeout.Token);
        Assert.Equal(199, actual.Inputs["value"].Json.GetInt32());
        Assert.False(observed.Reader.TryRead(out _));
        Assert.Contains(fixture.Sources.Failures.Values, result => string.Equals(result.ReasonCode, "safety.replaced", StringComparison.Ordinal));
    }

    /// <summary>AllからLatestへ切替えても旧All入力を保持し、新Latest同士だけを置換します。</summary>
    /// <returns>入力ごとの設定版と保持方針を確認します。</returns>
    [Fact]
    public async Task AllToLatestKeepsPreviouslyCapturedAllInputsAsync()
    {
        BurstSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition all = Definition("change", EStoppedInputPolicy.All);
        all = all with { Nodes = [all.Nodes[0], WorkflowFixture.Definition("sink", settings: "{\"version\":1}")] };
        CompiledWorkflow oldPlan = fixture.Compiler.Compile(all).Workflow!;
        await fixture.Sources.ApplyAsync(oldPlan, timeout.Token);
        fixture.Safety.SetStopped("change", "source", true);
        await source.EmitAsync("change", 1, timeout.Token);
        await source.EmitAsync("change", 2, timeout.Token);
        WorkflowDefinition latest = all with { Nodes = [all.Nodes[0] with { Safety = all.Nodes[0].Safety! with { Inputs = EStoppedInputPolicy.Latest } }, WorkflowFixture.Definition("sink", settings: "{\"version\":2}")] };
        CompiledWorkflow newPlan = fixture.Compiler.Compile(latest).Workflow!;
        await fixture.Sources.ApplyAsync(newPlan, timeout.Token);
        await source.EmitAsync("change", 3, timeout.Token);
        await source.EmitAsync("change", 4, timeout.Token);
        Assert.Equal(3, fixture.Sources.Statistics.Held);
        Assert.Single(fixture.Sources.Failures.Values, result => string.Equals(result.ReasonCode, "safety.replaced", StringComparison.Ordinal));
        fixture.Safety.SetStopped("change", "source", false);
        Dictionary<int, NodeExecutionContext> actual = [];
        for (int index = 0; index < 3; index++)
        {
            NodeExecutionContext item = await observed.Reader.ReadAsync(timeout.Token);
            actual.Add(item.Inputs["value"].Json.GetInt32(), item);
        }
        Assert.Equal([1, 2, 4], actual.Keys.Order());
        Assert.Equal(oldPlan.Revision, actual[1].Revision);
        Assert.Equal(oldPlan.Revision, actual[2].Revision);
        Assert.Equal(1, actual[1].Settings.GetProperty("version").GetInt32());
        Assert.Equal(newPlan.Revision, actual[4].Revision);
        Assert.Equal(2, actual[4].Settings.GetProperty("version").GetInt32());
    }

    /// <summary>旧Allだけで満杯の場合、Latestへの変更で上限超過や旧入力破棄をしません。</summary>
    /// <returns>明示容量拒否と旧入力保持を確認します。</returns>
    [Fact]
    public async Task FullAllBufferRejectsNewLatestWithoutEvictingOldInputAsync()
    {
        BurstSource source = new();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success()), true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition all = Definition("full", EStoppedInputPolicy.All);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(all).Workflow!, timeout.Token);
        fixture.Safety.SetStopped("full", "source", true);
        for (int index = 0; index < 128; index++)
        {
            await source.EmitAsync("full", index, timeout.Token);
        }
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(Definition("full", EStoppedInputPolicy.Latest)).Workflow!, timeout.Token);
        await source.EmitAsync("full", 999, timeout.Token);
        Assert.Equal(128, fixture.Sources.Statistics.Held);
        Assert.Equal("safety.capacity", Assert.Single(fixture.Sources.Failures.Values).ReasonCode);
    }

    /// <summary>待受終了通知は停止中入力と別に一度だけ後続へ届きます。</summary>
    /// <returns>検証完了。</returns>
    [Fact]
    public async Task TerminalNotificationBypassesStoppedInputBuffer()
    {
        BurstSource source = new();
        Channel<NodeExecutionContext> observed = Channel.CreateUnbounded<NodeExecutionContext>();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode(async (context, token) =>
        {
            await observed.Writer.WriteAsync(context, token).ConfigureAwait(false);
            return NodeResult.Success();
        }, true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        WorkflowDefinition definition = Definition("stopped", EStoppedInputPolicy.All) with { Wires = [new WireDefinition("source", "result", "sink", "value", "completed")] };
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(definition).Workflow!, timeout.Token);
        fixture.Safety.SetStopped("stopped", "source", true);
        await source.EmitAsync("stopped", 1, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Sources.StopAsync("stopped", "source", cancellationToken: timeout.Token)).Outcome);
        Assert.Equal("stopped", (await observed.Reader.ReadAsync(timeout.Token)).WorkflowId);
        Assert.False(observed.Reader.TryRead(out _));
    }

    /// <summary>安全停止は人の操作だけで指定する試験定義です。</summary>
    /// <returns>継続入力と有限下流。</returns>
    private static WorkflowDefinition Definition(string id, EStoppedInputPolicy policy)
    {
        NodeDefinition source = WorkflowFixture.Definition("source", "test.burst") with { StartOnLoad = true, Safety = new NodeSafetyPolicy(false, false, policy) };
        return new WorkflowDefinition(id, id, [source, WorkflowFixture.Definition("sink")], [new WireDefinition("source", "value", "sink", "value")], Enabled: true);
    }

    /// <summary>待受の文脈から試験入力を明示発生します。</summary>
    private sealed class BurstSource : ISourceNode
    {
        private readonly ConcurrentDictionary<string, NodeSourceContext> contexts = new(StringComparer.Ordinal);

        /// <summary>継続入力の試験契約です。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.burst", "入力", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>追加制約はありません。</summary>
        /// <returns>問題なし。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>登録後、停止またはHost取消まで待ちます。</summary>
        /// <returns>正常終了または取消。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            contexts[context.Initial.WorkflowId] = context;
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.StopToken, cancellationToken);
            await context.ReportStatusAsync(new NodeSourceStatus(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.IsStopRequested && !cancellationToken.IsCancellationRequested)
            {
                return NodeResult.Success();
            }
            return NodeResult.Success();
        }

        /// <summary>一件ごとに設定を取得して通知します。</summary>
        /// <returns>受付完了。</returns>
        public ValueTask EmitAsync(string workflowId, int value, CancellationToken cancellationToken = default)
        {
            return EmitValueAsync(workflowId, NodeValue.FromObject(value), cancellationToken);
        }

        /// <summary>バイト容量を持つ入力も同じ受付で検証します。</summary>
        /// <returns>受付完了です。</returns>
        public ValueTask EmitValueAsync(string workflowId, NodeValue value, CancellationToken cancellationToken = default)
        {
            NodeSourceContext context = contexts[workflowId];
            return context.EmitAsync(context.BeginItem(), NodeResult.Success(value), cancellationToken);
        }
    }
}
