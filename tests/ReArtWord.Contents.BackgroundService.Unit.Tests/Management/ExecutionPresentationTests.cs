using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.History;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Workflows;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>巨大な実値と診断要約を区別し、ページ境界でも成否を保持します。</summary>
public sealed class ExecutionPresentationTests
{
    /// <summary>深い実行結果をキャッシュから消さず、履歴へ確定要約を残します。</summary>
    /// <returns>実Engineと保持境界の確認です。</returns>
    [Fact]
    public async Task DeepResultRemainsInExecutionHistory()
    {
        using JsonDocument document = JsonDocument.Parse(new string('[', 60) + "42" + new string(']', 60));
        NodeValue value = NodeValue.FromJson(document.RootElement);
        WorkflowFixture fixture = new(new WorkflowFixture.ActionNode((_, _) => ValueTask.FromResult(NodeResult.Success(value))));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        WorkflowDefinition definition = new("deep", "deep", [WorkflowFixture.Definition("node")], []);
        ExecutionReport actual = await fixture.Engine.ExecuteAsync(fixture.Compiler.Compile(definition).Workflow!).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, actual.Outcome);
        ExecutionResultView retained = Assert.Single(fixture.Engine.RecentViews());
        Assert.Null(retained.Report);
        Assert.Equal(EOutcome.Succeeded, retained.Summary!.Outcome);
    }

    /// <summary>通常の小さい出力は欠落させません。</summary>
    [Fact]
    public void SmallOutputIsPreserved()
    {
        ExecutionReport report = Report(NodeValue.FromObject(42));
        ExecutionResultView view = ExecutionReportPresentation.Bound(report);
        Assert.Same(report, view.Report);
        Assert.Null(view.Summary);
        Assert.Equal(42, view.Report!.Nodes[0].Targets[0].Result.Outputs["value"].Json.GetInt32());
    }

    /// <summary>大容量バイナリでも確定した失敗とPeer理由は保存用要約へ残します。</summary>
    [Fact]
    public void LargeOutputRetainsConfirmedOutcomeAndPeerReason()
    {
        ExecutionReport report = Report(NodeValue.FromBytes(new byte[3 * 1024 * 1024]));
        ExecutionResultView view = ExecutionReportPresentation.Bound(report);
        Assert.Null(view.Report);
        Assert.Equal(EOutcome.Failed, view.Summary!.Outcome);
        ExecutionTargetSummary target = Assert.Single(view.Summary.Targets);
        Assert.Equal("peer", target.PeerId);
        Assert.Equal("known.failure", target.ReasonCode);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(view, ContractJson.Options).Length < 131072);
    }

    /// <summary>深いJSONの変換不能を実行Unknownへ変えません。</summary>
    [Fact]
    public void DeepResultHasSerializableSummary()
    {
        using JsonDocument document = JsonDocument.Parse(new string('[', 55) + "42" + new string(']', 55));
        ExecutionResultView view = ExecutionReportPresentation.Bound(Report(NodeValue.FromJson(document.RootElement)));
        Assert.Null(view.Report);
        Assert.Equal(EOutcome.Failed, view.Summary!.Outcome);
        Assert.Equal("execution.details.serialization", view.DetailReason);
        _ = ContractJson.Copy(view);
    }

    /// <summary>件数以内でもバイトで区切り、同時刻の全実行を重複なく読み切ります。</summary>
    [Fact]
    public void BytePagesAdvanceWithoutDroppingResults()
    {
        ExecutionResultView[] reports = Enumerable.Range(0, 18).Select(_ => ExecutionReportPresentation.Bound(Report(NodeValue.FromObject(new string('x', 6000))))).ToArray();
        List<Guid> actual = [];
        string? cursor = null;
        int pages = 0;
        do
        {
            ExecutionHistoryPage page = ExecutionHistory.Page(reports, new(100, cursor, 16384));
            Assert.NotEmpty(page.Entries);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(page, ContractJson.Options).Length <= 16384);
            actual.AddRange(page.Entries.Select(static item => item.Report?.Id ?? item.Summary!.Id));
            cursor = page.Cursor;
            pages++;
        }
        while (cursor is not null && pages < 30);
        Assert.Null(cursor);
        Assert.True(1 < pages);
        Assert.Equal(18, actual.Count);
        Assert.Equal(18, actual.Distinct().Count());
    }

    /// <summary>対象ごとの既知結果を持つ一件を生成します。</summary>
    /// <returns>内容だけが異なる実行結果です。</returns>
    private static ExecutionReport Report(NodeValue value) => new(Guid.NewGuid(), "workflow", "revision", EOutcome.Failed,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [new("node", EOutcome.Failed,
            [new("peer", new(EOutcome.Failed, "failure", new Dictionary<string, NodeValue> { ["value"] = value }, "known.failure", "確認済みの失敗"))])]);
}
