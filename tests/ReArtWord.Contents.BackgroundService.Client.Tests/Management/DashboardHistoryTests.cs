using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>再読込みで使用する活動履歴の保持範囲と容量を確認します。</summary>
public sealed class DashboardHistoryTests
{
    /// <summary>ノード診断は出力本文を保存せず、最新の成功で失敗を置き換えます。</summary>
    [Fact]
    public void NodeResultsKeepLatestBoundedOutcomesWithoutValues()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeResult result = NodeResult.Failure("failure", "private-message") with { Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["value"] = NodeValue.FromObject("private-output") } };
        ExecutionReport report = new(Guid.NewGuid(), "workflow", "r", EOutcome.Failed, now, now, [new("node", EOutcome.Failed, [new("peer", result)])]);
        IReadOnlyList<NodeExecutionObservation> first = DashboardHistory.NodeResults(null, new([new(report)], null), "owner", now);
        Assert.Equal(EOutcome.Failed, Assert.Single(first).Outcome);
        string serialized = System.Text.Json.JsonSerializer.Serialize(first, ContractJson.Options);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        NodeExecutionObservation success = first[0] with { Outcome = EOutcome.Succeeded, CompletedAt = now.AddSeconds(1) };
        IReadOnlyList<NodeExecutionObservation> recovered = DashboardHistory.NodeResults([.. first, success], null, "owner", now.AddSeconds(1));
        Assert.Equal(EOutcome.Succeeded, Assert.Single(recovered).Outcome);
        Assert.Empty(DashboardHistory.NodeResults(first, null, "owner", now.AddMinutes(6)));
        NodeExecutionObservation[] many = Enumerable.Range(0, 600).Select(index => success with { NodeId = "node-" + index }).ToArray();
        Assert.Equal(512, DashboardHistory.NodeResults(many, null, "owner", now.AddSeconds(2)).Count);
    }

    /// <summary>履歴をIDで統合し、古い結果と実出力を保持しません。</summary>
    [Fact]
    public void ActivityRetainsOnlyBoundedRecentSummaries()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExecutionResultView[] entries = Enumerable.Range(0, 2100).Select(_ => new ExecutionResultView(new(Guid.NewGuid(), "workflow", "revision", EOutcome.Succeeded, now.AddSeconds(-5), now, []))).ToArray();
        ExecutionHistoryPage previous = new(entries, "more");
        ExecutionResultView expired = new(new(Guid.NewGuid(), "expired", "r", EOutcome.Failed, now.AddMinutes(-2), now.AddMinutes(-2), []));
        ExecutionHistoryPage current = new([entries[0], expired], "more");
        ExecutionHistoryPage result = DashboardHistory.Activity(previous, current, now)!;
        Assert.Equal(2000, result.Entries.Count);
        Assert.Equal(result.Entries.Count, result.Entries.Select(entry => entry.Summary!.Id).Distinct().Count());
        Assert.All(result.Entries, entry =>
        {
            Assert.Null(entry.Report);
            Assert.Equal("workflow", entry.Summary!.WorkflowId);
            Assert.Empty(entry.Summary.Targets);
        });
        Assert.Equal("more", result.Cursor);
        Assert.Null(DashboardHistory.Activity(previous, null, now));
    }
}
