using System.Text.Json;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Scheduling;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Scheduling;

/// <summary>実行枠より多い予定入力を保持しても、未開始対象は最新の一回だけへ進むことを確認します。</summary>
public sealed class ScheduledPendingInputsTests
{
    /// <summary>待機中の80回を順に配送せず、PC復帰後に最新回だけを新しい実体へ送ります。</summary>
    /// <returns>実Sourceキュー、対象待ち、配送要求の確認。</returns>
    [Fact]
    public async Task MoreThanAnExecutionWindowOfMissedInputsKeepsOnlyLatestAsync()
    {
        BurstSource source = new();
        WorkflowFixture fixture = new(source, new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success()), true));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fixtureLifetime = fixture.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        PeerInfo absent = new("b", Guid.NewGuid(), "host-b", "127.0.0.1", 1234, EPeerStatus.Unavailable, 0, null, DateTimeOffset.UtcNow);
        fixture.Peers.Others = [absent];
        Channel<RemoteExecutionRequest> observed = Channel.CreateUnbounded<RemoteExecutionRequest>();
        fixture.Dispatcher.Handler = (request, _) =>
        {
            observed.Writer.TryWrite(request);
            return Task.FromResult(NodeResult.Success(request.Inputs["value"]));
        };
        WorkflowDefinition definition = new("missed-burst", "Latest scheduled input", [
            new("timer", "test.scheduledBurst", JsonSerializer.SerializeToElement(new { }), StartOnLoad: true),
            WorkflowFixture.Definition("action") with { Target = new(EExecutionScope.One, ["b"]) },
        ], [new("timer", "value", "action", "value")], Enabled: true);
        await fixture.Sources.ApplyAsync(fixture.Compiler.Compile(definition).Workflow!, timeout.Token).ConfigureAwait(true);
        await source.Emitted.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        Assert.False(observed.Reader.TryRead(out _));
        Guid returned = Guid.NewGuid();
        fixture.Peers.Others = [absent with { InstanceId = returned, Status = EPeerStatus.Ready, ReadySince = DateTimeOffset.UtcNow }];
        RemoteExecutionRequest actual = await observed.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(80, actual.Inputs["value"].Json.GetInt32());
        Assert.Equal(returned, actual.TargetInstanceId);
        while (fixture.Sources.Statistics.Waiting != 0 || fixture.Sources.Statistics.Running != 0)
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        Assert.False(observed.Reader.TryRead(out _));
        Assert.Equal(0, fixture.Sources.Statistics.RetainedValueBytes);
    }

    /// <summary>同じ時間指定の予定入力を、実キューへ短時間に渡すSourceです。</summary>
    private sealed class BurstSource : ISourceNode
    {
        /// <summary>すべての入力がキューに受け付けられたことを通知します。</summary>
        internal TaskCompletionSource Emitted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>値を出力する試験用の時間指定Source。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.scheduledBurst", "予定入力", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>試験用Sourceには追加設定を要求しません。</summary>
        /// <returns>追加の検証問題はありません。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>通常の受付と同じ取得時点の文脈に、予定時刻を設定します。</summary>
        /// <returns>全入力を受け付けた後の正常終了。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            for (int index = 1; index <= 80; index++)
            {
                NodeExecutionContext item = context.BeginItem() with { Schedule = new("timeline", "timer", context.Initial.ExecutionId,
                    DateTimeOffset.UnixEpoch.AddSeconds(index), EScheduleMissedPolicy.Delay, TimeSpan.Zero) };
                await context.EmitAsync(item, NodeResult.Success(NodeValue.FromObject(index)), cancellationToken).ConfigureAwait(false);
            }
            Emitted.TrySetResult();
            return NodeResult.Success();
        }
    }
}
