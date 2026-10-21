using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>日時契機と別PCの値生成を、Toggle越しに逐次・並列反復へ渡します。</summary>
public sealed class RuntimeToggleValueProcessTests
{
    private static readonly int[] elements = [10, 20, 30];

    /// <summary>本番QRの開始・日付選択・反復を、外部APIや実ファイル書込みなしで確認します。</summary>
    /// <returns>二つの実Core間で解決した日時と、全要素の完了です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScheduledIterationReadsRemoteDateThroughToggleAsync(bool parallel)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer owner = new(cluster, "a", port, true, "host-a", 900);
        CorePeer values = new(cluster, "b", port, false, "host-b", 100);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = owner.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = values.ConfigureAwait(true);
        await owner.StartAsync().ConfigureAwait(true);
        await values.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement first = await owner.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            JsonElement second = await values.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return Ready(first) && Ready(second);
        }).ConfigureAwait(true);
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        string loopType = parallel ? "control.parallelForEach"
                                   : "control.forEach";
        WorkflowDefinition definition = new("scheduled-toggle", "Scheduled date through toggle", [
            new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "once", at = DateTimeOffset.UtcNow.AddSeconds(1), timeZoneId = "UTC" }), StartOnLoad: true),
            new("date", "scheduling.now", JsonSerializer.SerializeToElement(new { timeZoneId = "Asia/Tokyo", format = "yyyy-MM-dd" }), Target: new(EExecutionScope.One, Peers: ["b"])),
            new("convert", "data.convert", JsonSerializer.SerializeToElement(new { targetType = "string" })),
            new("toggle", "control.toggleButton", empty),
            new("loop", loopType, empty, new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(elements) }),
            new("consumer", "control.pass", empty), new("end", "control.forEach.end", empty),
        ], [new("timer", "value", "loop", "trigger"), new("date", "value", "convert", "value"),
            new("convert", "value", "toggle", "offValue"), new("toggle", "value", "consumer", "value"), new("loop", "start", "consumer", "trigger"),
            new("loop", "loop", "end", "loop"), new("consumer", "value", "end", "value")], Enabled: true);
        WorkflowSaveResult saved = await owner.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        ExecutionReport? report = null;
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage page = await owner.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            report = page.Entries.Select(entry => entry.Report).FirstOrDefault(entry => entry?.WorkflowId is "scheduled-toggle"
                && entry.Nodes.Any(node => node.NodeId is "timer" && node.Targets.Any(target => target.Result.Outputs.ContainsKey("scheduledAt"))));
            return report is not null;
        }).ConfigureAwait(true);
        Assert.NotNull(report);
        Assert.Equal(EOutcome.Succeeded, report.Outcome);
        TargetNodeResult remote = Assert.Single(report.Nodes.Single(node => node.NodeId is "date").Targets);
        Assert.Equal("b", remote.PeerId);
        string date = Assert.IsType<string>(remote.Result.Outputs["value"].GetString());
        Assert.Matches("^[0-9]{4}-[0-9]{2}-[0-9]{2}$", date);
        JsonElement output = Assert.Single(report.Nodes.Single(node => node.NodeId is "end").Targets).Result.Outputs["value"].Json;
        Assert.Equal(3, output.GetArrayLength());
        Assert.All(output.EnumerateArray(), item => Assert.Equal(date, item.GetString()));
    }

    /// <summary>両Peerの通常参加を確認してから保存・日時発火を開始します。</summary>
    /// <returns>相互にReadyな状態です。</returns>
    private static bool Ready(JsonElement status)
        => status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
}
