using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>トレース無効時も一件全体の失敗と原因ノードを必ずログ境界へ渡します。</summary>
public sealed class WorkflowFailureLoggingTests
{
    /// <summary>設定ノードの正常な未実行が多数あっても、入力不足の原因を省略しません。</summary>
    /// <returns>WFを失敗させた未実行が件数と詳細へ含まれる確認です。</returns>
    [Fact(Skip = "旧仕様: WF終了ログの説明を常に省略。例外診断対応後はWorkflowExceptionLoggingTestsで説明保持と入力非収集を検証します。")]
    public async Task MissingInputCauseIsNotHiddenByExpectedSkipsAsync()
    {
        RecordingLog log = new();
        NodeResult missing = NodeResult.Failure("input.unavailable", "private-input-detail", EOutcome.NotExecuted)
            with { Outputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("private-output") } };
        const string sending = "send";
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((context, _) =>
        {
            if (string.Equals(context.NodeId, sending, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(missing);
            }
            return ValueTask.FromResult(NodeResult.Failure("value.configuration", "Configuration only", EOutcome.NotExecuted));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        NodeDefinition[] nodes = [.. Enumerable.Range(0, 32).Select(index => WorkflowFixture.Definition("configuration-" + index)), WorkflowFixture.Definition("send")];
        ExecutionReport report = await engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, new("workflow", "logging", nodes, []))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        System.Text.Json.JsonElement data = entry.Data!.Json;
        Assert.Equal(1, data.GetProperty("unsuccessfulNodeCount").GetInt32());
        Assert.Equal(0, data.GetProperty("omittedNodeCount").GetInt32());
        System.Text.Json.JsonElement node = Assert.Single(data.GetProperty("nodes").EnumerateArray());
        Assert.Equal("send", node.GetProperty("nodeId").GetString());
        Assert.Equal("input.unavailable", Assert.Single(node.GetProperty("targets").EnumerateArray()).GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("private-input-detail", data.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-output", data.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>件数上限を保ち、省略数と対象別の本当の原因を優先します。</summary>
    /// <returns>ノード・対象の正常な省略が診断を押し出さない確認です。</returns>
    [Fact]
    public async Task LimitsKeepActualTargetFailuresFirstAsync()
    {
        RecordingLog log = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Peers.Others = Enumerable.Range(0, 12).Select(index => new PeerInfo("skipped-" + index, Guid.NewGuid(), "host-" + index,
            "127.0.0.1", 0, EPeerStatus.Ready, 0, now, now)).ToArray();
        fixture.Dispatcher.Handler = (request, _) =>
        {
            if (string.Equals(request.TargetPeerId, fixture.Peers.LocalPeerId, StringComparison.Ordinal))
            {
                return Task.FromResult(NodeResult.Failure("actual.failure", "failed"));
            }
            return Task.FromResult(NodeResult.Failure(ExecutionResultReasons.BranchNotSelected, "Not selected", EOutcome.NotExecuted));
        };
        string[] peers = [.. fixture.Peers.Others.Select(peer => peer.PeerId), fixture.Peers.LocalPeerId];
        NodeDefinition[] nodes = Enumerable.Range(0, 20).Select(index => WorkflowFixture.Definition("node-" + index) with { Target = new(EExecutionScope.All, peers) }).ToArray();
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        _ = await engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, new("workflow", "logging", nodes, []))).ConfigureAwait(true);
        System.Text.Json.JsonElement data = Assert.Single(log.Entries).Data!.Json;
        Assert.Equal(20, data.GetProperty("unsuccessfulNodeCount").GetInt32());
        Assert.Equal(4, data.GetProperty("omittedNodeCount").GetInt32());
        Assert.Equal(16, data.GetProperty("nodes").GetArrayLength());
        foreach (System.Text.Json.JsonElement node in data.GetProperty("nodes").EnumerateArray())
        {
            System.Text.Json.JsonElement values = node.GetProperty("targets");
            Assert.Equal(8, values.GetArrayLength());
            Assert.Equal("a", values[0].GetProperty("peerId").GetString());
            Assert.Equal("actual.failure", values[0].GetProperty("reasonCode").GetString());
        }
    }

    /// <summary>失敗、結果不明、未実行、明示取消を成功と分けて一回記録します。</summary>
    /// <returns>実行結果と因果IDを持つ診断の検証です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, "ERROR")]
    [InlineData(EOutcome.Unknown, "ERROR")]
    [InlineData(EOutcome.NotExecuted, "ERROR")]
    [InlineData(EOutcome.Cancelled, "WARN")]
    public async Task NonSuccessIsLoggedWithoutTraceAsync(EOutcome outcome, string level)
    {
        RecordingLog log = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Failure("fixture.failure", "safe detail", outcome))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, log);
        fixture.Dispatcher.Handler = executor.ExecuteAsync;
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("workflow", "logging", [WorkflowFixture.Definition("node")], [])).Workflow!;
        ExecutionReport report = await engine.ExecuteAsync(plan);
        WorkflowLogEntry entry = Assert.Single(log.Entries, item => item.NodeId is null);
        Assert.Equal(report.Outcome, outcome);
        Assert.Equal(report.Id, entry.CausationId);
        Assert.Equal("workflow", entry.WorkflowId);
        Assert.Equal(level, entry.Level);
        Assert.Contains("fixture.failure", entry.Data!.Json.GetRawText(), StringComparison.Ordinal);
        if (outcome is EOutcome.Failed or EOutcome.Unknown)
        {
            WorkflowLogEntry node = Assert.Single(log.Entries, item => item.NodeId is not null);
            Assert.Contains("safe detail", node.Data!.Json.GetRawText(), StringComparison.Ordinal);
        }
    }

    /// <summary>成功は失敗ログを増やさず、ログ保存側の故障も実行結果を変更しません。</summary>
    /// <returns>ログ障害の隔離を確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoggingDoesNotChangeOutcomeAsync(bool fail)
    {
        RecordingLog log = new() { Throw = true };
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(fail ? NodeResult.Failure("fixture.failure", "failed")
                                                                                                         : NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("workflow", "logging", [WorkflowFixture.Definition("node")], [])).Workflow!;
        ExecutionReport report = await engine.ExecuteAsync(plan);
        Assert.Equal(fail ? EOutcome.Failed
                          : EOutcome.Succeeded, report.Outcome);
        Assert.Equal(fail ? 1
                          : 0, log.Entries.Count);
    }

    /// <summary>実行基盤からの例外も一回記録し、元の例外本文をログへ流しません。</summary>
    /// <returns>ノード結果が作れない中断経路の検証です。</returns>
    [Fact(Skip = "旧仕様: 実行基盤の例外本文を常に省略。例外診断対応後はWorkflowExceptionLoggingTestsで本文・スタック保持と秘密保護を検証します。")]
    public async Task UnhandledExecutionExceptionIsLoggedAsync()
    {
        RecordingLog log = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("workflow", "logging", [WorkflowFixture.Definition("node")], [])).Workflow!;
        WorkflowDebugArm arm = new(Guid.NewGuid(), "workflow", fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(engine.Debug.Prepare(arm));
        engine.Debug.Claim = (_, _, _) => throw new NotSupportedException("private-exception-message");
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.ExecuteAsync(plan));
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("workflow.unsuccessful", entry.Message);
        Assert.Contains("NotSupportedException", entry.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-exception-message", entry.Data.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(engine.Active());
    }

    /// <summary>反復内の失敗を一件全体の終了ログへ集約します。</summary>
    /// <returns>内部実行が増えても終了ログが重複しない確認です。</returns>
    [Fact]
    public async Task IterationFailureHasOneWorkflowTerminationLogAsync()
    {
        RecordingLog log = new();
        WorkflowFixture fixture = IterationTests.Create((_, _) => ValueTask.FromResult(NodeResult.Failure("fixture.bodyFailed", "failed")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        ExecutionReport report = await engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition(end: false)));
        Assert.Equal(EOutcome.Failed, report.Outcome);
        Assert.Equal(report.Id, Assert.Single(log.Entries).CausationId);
    }

    /// <summary>実行の取消済みトークンによって終了のログまで消さないことを確認します。</summary>
    /// <returns>取消後の記録受付です。</returns>
    [Fact]
    public async Task ExecutionCancellationDoesNotCancelLogAdmissionAsync()
    {
        RecordingLog log = new();
        using CancellationTokenSource cancellation = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode(async (_, _) =>
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            return NodeResult.Failure("fixture.cancelled", "cancelled", EOutcome.Cancelled);
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        CompiledWorkflow plan = fixture.Compiler.Compile(new WorkflowDefinition("workflow", "logging", [WorkflowFixture.Definition("node")], [])).Workflow!;
        ExecutionReport report = await engine.ExecuteAsync(plan, cancellationToken: cancellation.Token);
        Assert.Equal(EOutcome.Cancelled, report.Outcome);
        Assert.Equal("workflow.cancelled", Assert.Single(log.Entries).Message);
    }

    /// <summary>記録要求と記録不能を観測する試験用ログです。</summary>
    private sealed class RecordingLog : IWorkflowLog
    {
        /// <summary>受け付けた記録です。</summary>
        internal List<WorkflowLogEntry> Entries
        {
            get;
        } = [];

        /// <summary>保存側の障害を再現します。</summary>
        internal bool Throw
        {
            get; init;
        }

        /// <summary>終了後でも有効な記録要求を受け付けます。</summary>
        /// <returns>受付結果です。</returns>
        public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            if (Throw)
            {
                throw new IOException("log unavailable");
            }
            return ValueTask.CompletedTask;
        }
    }
}
