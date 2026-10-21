using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>継続待受が例外で終了したときもノードと呼出し履歴を残します。</summary>
public sealed class SourceExceptionLoggingTests
{
    /// <summary>待受の非同期失敗を有限処理と同じ形式で記録します。</summary>
    /// <returns>Sourceの失敗状態と永続化へ渡す診断です。</returns>
    [Fact]
    public async Task SourceFailureKeepsExceptionDetailsAsync()
    {
        RecordingWorkflowLog log = new();
        WorkflowFixture fixture = new(new FailingSource());
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable fixtureLifetime = fixture.ConfigureAwait(true);
        SourceSupervisor sources = new(fixture.Registry, fixture.Peers, fixture.Engine, fixture.Safety, log: log);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = sources.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, new("workflow", "source", [WorkflowFixture.Definition("source", "test.failureSource") with { StartOnLoad = true }], [], true));
        await sources.ApplyAsync(plan, timeout.Token).ConfigureAwait(true);
        string failure = "source.failed";
        while (!log.Entries.Any(entry => string.Equals(entry.Message, failure, StringComparison.Ordinal)))
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
        Contracts.Observability.Logging.WorkflowLogEntry entry = Assert.Single(log.Entries, item => item.Message is "source.failed");
        Assert.Equal("source", entry.NodeId);
        Assert.Equal("workflow", entry.WorkflowId);
        Assert.NotNull(entry.CausationId);
        Assert.Equal("listener failed", entry.Data!.Json.GetProperty("exception").GetProperty("Message").GetString());
        Assert.Contains(nameof(FailingSource), entry.Data.Json.GetProperty("exception").GetProperty("StackTrace").GetString(), StringComparison.Ordinal);
        Assert.Equal(ESourceState.Failed, Assert.Single(sources.GetSources()).Status.State);
    }

    /// <summary>待受の開始後に例外となる試験用Sourceです。</summary>
    private sealed class FailingSource : ISourceNode
    {
        /// <summary>開始可能な継続入力を公開します。</summary>
        public NodeDescriptor Descriptor
        {
            get;
        } = NodeSupport.Describe("test.failureSource", "Failure source", "Test", "{}", [], kind: ENodeKind.Source);

        /// <summary>設定に追加制約を設けません。</summary>
        /// <returns>検証成功です。</returns>
        public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition) => [];

        /// <summary>待受の状態を通知した後、非同期の例外を再現します。</summary>
        /// <returns>必ず失敗する待受です。</returns>
        public async Task<NodeResult> RunAsync(NodeSourceContext context, CancellationToken cancellationToken = default)
        {
            await context.ReportStatusAsync(new(ESourceState.Listening), cancellationToken).ConfigureAwait(false);
            throw new IOException("listener failed");
        }
    }
}
