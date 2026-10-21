using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;

/// <summary>失敗ノード・例外本文・呼出し履歴を詳細追跡なしで辿れることを検証します。</summary>
public sealed class WorkflowExceptionLoggingTests
{
    /// <summary>実行前の検証例外も、分散要求の共通失敗へ変換される前に記録します。</summary>
    /// <returns>処理を開始せず、元の例外とノード情報を保持する確認です。</returns>
    [Fact]
    public async Task AdmissionExceptionKeepsNodeIdentityAsync()
    {
        RecordingWorkflowLog log = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        NodeExecutor executor = new(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, log)
        {
            AdmissionCheck = () => throw new IOException("admission failed"),
        };
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "workflow", "revision", WorkflowFixture.Definition("node"),
            new Dictionary<string, NodeValue>(), fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(request));
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal(request.CausationId, entry.CausationId);
        Assert.Equal("node", entry.NodeId);
        Assert.Equal("execution.boundaryException", entry.Message);
        Assert.Contains("admission failed", entry.Data!.Json.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(executor.Running);
    }

    /// <summary>通常成功時は例外診断や終了ERRORを追加しません。</summary>
    /// <returns>通常実行に記録量を増やさない確認です。</returns>
    [Fact]
    public async Task SuccessfulExecutionDoesNotAddLogsAsync()
    {
        RecordingWorkflowLog log = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        fixture.Dispatcher.Handler = new NodeExecutor(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, log).ExecuteAsync;
        ExecutionReport report = await engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, new("workflow", "success", [WorkflowFixture.Definition("node")], []))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        Assert.Empty(log.Entries);
    }

    /// <summary>並列反復の各例外は同じWFに結び付き、個別のノード実行IDを持ちます。</summary>
    /// <returns>反復開始ノードだけでなくボディの原因を追える確認です。</returns>
    [Fact]
    public async Task ParallelIterationKeepsBodyFailuresDistinctAsync()
    {
        RecordingWorkflowLog log = new();
        WorkflowFixture fixture = IterationTests.Create((context, _) => throw new IOException("body failed: " + context.ExecutionId));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        fixture.Dispatcher.Handler = new NodeExecutor(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, log).ExecuteAsync;
        ExecutionReport report = await engine.ExecuteAsync(IterationTests.Compile(fixture, IterationTests.Definition(parallel: true))).ConfigureAwait(true);
        Assert.NotEqual(EOutcome.Succeeded, report.Outcome);
        WorkflowLogEntry[] nodes = log.Entries.Where(entry => entry.Message is "execution.exception").ToArray();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, entry => Assert.Equal(report.Id, entry.CausationId));
        Assert.Equal(nodes.Length, nodes.Select(entry => entry.Data!.Json.GetProperty("executionId").GetGuid()).Distinct().Count());
        Assert.All(nodes, entry => Assert.Contains("body failed", entry.Data!.Json.GetRawText(), StringComparison.Ordinal));
        Assert.Single(log.Entries, entry => entry.Message is "workflow.unsuccessful");
    }

    /// <summary>別の実行を混ぜず、ログ障害時も実行結果を変更しません。</summary>
    /// <returns>並列実行と例外境界の記録です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NodeExceptionsKeepStacksAndExecutionIdentityAsync(bool logFailure)
    {
        RecordingWorkflowLog log = new() { Throw = logFailure };
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((context, _) => throw new InvalidOperationException("failure-" + context.CausationId,
            new IOException("nested failure"))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        fixture.Dispatcher.Handler = new NodeExecutor(fixture.Registry, fixture.Peers, fixture.Sources, fixture.Safety, log).ExecuteAsync;
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, new("workflow", "failure", [WorkflowFixture.Definition("node")], []));
        ExecutionReport[] reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => engine.ExecuteAsync(plan))).ConfigureAwait(true);
        foreach (ExecutionReport report in reports)
        {
            Assert.Equal(EOutcome.Unknown, report.Outcome);
            WorkflowLogEntry node = Assert.Single(log.Entries, entry => entry.CausationId == report.Id && entry.NodeId is not null);
            Assert.Equal("node", node.NodeId);
            Assert.Equal("workflow", node.WorkflowId);
            Assert.Equal("a", node.PeerId);
            Assert.Equal("ERROR", node.Level);
            JsonElement diagnostic = node.Data!.Json.GetProperty("exception");
            Assert.Equal(typeof(InvalidOperationException).FullName, diagnostic.GetProperty("Type").GetString());
            Assert.Equal("failure-" + report.Id, diagnostic.GetProperty("Message").GetString());
            Assert.Contains(nameof(WorkflowExceptionLoggingTests), diagnostic.GetProperty("StackTrace").GetString(), StringComparison.Ordinal);
            Assert.Equal("nested failure", diagnostic.GetProperty("InnerExceptions")[0].GetProperty("Message").GetString());
            WorkflowLogEntry summary = Assert.Single(log.Entries, entry => entry.CausationId == report.Id && entry.NodeId is null);
            Assert.Contains("execution.exception", summary.Data!.Json.GetRawText(), StringComparison.Ordinal);
        }
        Assert.Empty(engine.Active());
    }

    /// <summary>正常な設定ノードを省き、未実行の原因説明だけを終了ログへ含めます。</summary>
    /// <returns>省略なしの診断と、入力・出力を収集しないことの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingInputDescriptionRespectsPrivacyAsync(bool personal)
    {
        RecordingWorkflowLog log = new();
        string sending = "send";
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((context, _) =>
        {
            if (string.Equals(context.NodeId, sending, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(NodeResult.Failure("input.unavailable", "missing destination", EOutcome.NotExecuted)
                    with { Outputs = new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("never-copy-output") } });
            }
            return ValueTask.FromResult(NodeResult.Failure("value.configuration", "Configuration only", EOutcome.NotExecuted));
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        NodeDefinition[] nodes = [.. Enumerable.Range(0, 32).Select(index => WorkflowFixture.Definition("configuration-" + index)), WorkflowFixture.Definition("send")];
        ExecutionReport report = await engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, new("workflow", "logging", nodes, [], ContainsPersonalData: personal))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, report.Outcome);
        JsonElement data = Assert.Single(log.Entries).Data!.Json;
        Assert.Equal(1, data.GetProperty("unsuccessfulNodeCount").GetInt32());
        Assert.Equal(0, data.GetProperty("omittedNodeCount").GetInt32());
        JsonElement node = Assert.Single(data.GetProperty("nodes").EnumerateArray());
        Assert.Equal("send", node.GetProperty("nodeId").GetString());
        JsonElement target = Assert.Single(node.GetProperty("targets").EnumerateArray());
        Assert.Equal("input.unavailable", target.GetProperty("reasonCode").GetString());
        string? expected = null;
        if (!personal)
        {
            expected = "missing destination";
        }
        Assert.Equal(expected, target.GetProperty("message").GetString());
        Assert.DoesNotContain("never-copy-output", data.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>ノード結果が作れない実行基盤の例外にも本文とスタックを残します。</summary>
    /// <returns>呼出元には元の例外を返し、記録だけを追加する確認です。</returns>
    [Fact]
    public async Task EngineExceptionPreservesOriginalFailureAsync()
    {
        RecordingWorkflowLog log = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        CompiledWorkflow plan = ValueMergeFixture.Compile(fixture, new("workflow", "failure", [WorkflowFixture.Definition("node")], []));
        WorkflowDebugArm arm = new(Guid.NewGuid(), "workflow", fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(engine.Debug.Prepare(arm));
        engine.Debug.Claim = (_, _, _) => throw new NotSupportedException("diagnostic admission failed");
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.ExecuteAsync(plan));
        WorkflowLogEntry entry = Assert.Single(log.Entries);
        Assert.Equal("workflow.unsuccessful", entry.Message);
        JsonElement error = entry.Data!.Json.GetProperty("exception");
        Assert.Equal("diagnostic admission failed", error.GetProperty("Message").GetString());
        Assert.Contains(nameof(EngineExceptionPreservesOriginalFailureAsync), error.GetProperty("StackTrace").GetString(), StringComparison.Ordinal);
        Assert.Empty(engine.Active());
    }

    /// <summary>配送経路の障害にもNodeと実行先を保持します。</summary>
    /// <returns>通信例外を成功へ変えない診断です。</returns>
    [Fact]
    public async Task DispatchExceptionIdentifiesNodeAndTargetAsync()
    {
        RecordingWorkflowLog log = new();
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success())));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        fixture.Dispatcher.Handler = (_, _) => throw new IOException("transport closed");
        WorkflowEngine engine = new(fixture.Registry, fixture.Peers, fixture.Dispatcher, log: log);
        ExecutionReport report = await engine.ExecuteAsync(ValueMergeFixture.Compile(fixture, new("workflow", "failure", [WorkflowFixture.Definition("node")], []))).ConfigureAwait(true);
        Assert.Equal(EOutcome.Unknown, report.Outcome);
        WorkflowLogEntry entry = Assert.Single(log.Entries, item => item.NodeId is not null);
        Assert.Equal(report.Id, entry.CausationId);
        Assert.Equal("node", entry.NodeId);
        Assert.Equal("a", entry.Data!.Json.GetProperty("targetPeerId").GetString());
        Assert.Equal("transport closed", entry.Data.Json.GetProperty("exception").GetProperty("Message").GetString());
    }
}
