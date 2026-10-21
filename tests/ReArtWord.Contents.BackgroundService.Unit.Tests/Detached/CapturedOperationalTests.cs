using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Nodes.Control;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Detached;

/// <summary>receiverに競合がなくても要求自身の未解消項目を確認します。</summary>
public sealed class CapturedOperationalTests
{
    /// <summary>時刻条件は開始前に拒否し、同じ設定版の非依存ノードは通常実行します。</summary>
    /// <returns>実ノード呼出し境界の検証です。</returns>
    [Fact]
    public async Task ReceiverChecksRequestSnapshotWithoutLocalConflictSetAsync()
    {
        int calls = 0;
        WorkflowFixture fixture = new(new ConditionNode(), new WorkflowFixture.ActionNode((_, _) =>
        {
            calls++;
            return ValueTask.FromResult(NodeResult.Success());
        }));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = fixture.ConfigureAwait(true);
        ClusterOperationalSnapshot operational = new(new("UTC", EFileConflictPolicy.Fail, EFileInternalFailurePolicy.Keep, new Dictionary<string, IReadOnlyList<string>>()), "version", DateTimeOffset.UnixEpoch, UnresolvedFields: ["TimeZoneId"]);
        RemoteExecutionRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "w", "revision", WorkflowFixture.Definition("n", "control.condition", """{"operation":"timeWindow","start":"00:00:00","end":"23:59:59"}"""),
            new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(true) }, fixture.Peers.LocalPeerId, fixture.Peers.LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), Operational: operational);
        NodeResult rejected = await fixture.Executor.ExecuteAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
        Assert.Equal("Conflict.OperationalDependency", rejected.Message);
        NodeResult independent = await fixture.Executor.ExecuteAsync(request with { Id = Guid.NewGuid(), Node = WorkflowFixture.Definition("independent") }).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, independent.Outcome);
        Assert.Equal(1, calls);
    }
}
