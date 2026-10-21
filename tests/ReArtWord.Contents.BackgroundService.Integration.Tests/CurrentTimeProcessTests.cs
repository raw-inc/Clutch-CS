using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>接続で指定した地域・書式を使い、別Coreの現在時刻を文字列として受け取ります。</summary>
public sealed class CurrentTimeProcessTests
{
    /// <summary>コンパイル時の定数にせず、実行時に取得した時刻だけを後続へ渡します。</summary>
    /// <returns>二つの実Core間の設定入力と日時出力の確認です。</returns>
    [Fact]
    public async Task ReadsRemoteCurrentTimeWithConnectedFormatAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string destination = Path.Combine(first.DirectoryPath, "content", "time.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2;
        }).ConfigureAwait(true);
        WorkflowDefinition definition = new("current-time", "Current time", [
            new("format", "value.constant", JsonSerializer.SerializeToElement(new { type = "string", text = "YYYY-MM-DD'T'HH:mm:ss.fffzzz" })),
            new("now", "scheduling.now", JsonSerializer.SerializeToElement(new { timeZoneId = "Asia/Tokyo" }), Target: new(EExecutionScope.One, ["b"])),
            new("write", "file.create", JsonSerializer.SerializeToElement(new { path = destination }), Target: new(EExecutionScope.One, ["a"])),
        ], [new("format", "value", "now", "$settings.format"), new("now", "value", "write", "value")]);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        DateTimeOffset before = DateTimeOffset.UtcNow;
        Guid operation = Guid.NewGuid();
        WorkflowRunStatus run = await first.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(operation, definition.Id, saved.Revision)).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            run = await first.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(true);
            return !run.Running;
        }).ConfigureAwait(true);
        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.True(run.Outcome is EOutcome.Succeeded, JsonSerializer.Serialize(run));
        string text = await File.ReadAllTextAsync(destination).ConfigureAwait(true);
        DateTimeOffset actual = DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        Assert.Equal(TimeSpan.FromHours(9), actual.Offset);
        Assert.InRange(actual, before.AddMilliseconds(-1), after);
        Assert.Contains(run.Report!.Nodes, node => node.Targets.Any(target => string.Equals(target.PeerId, "b", StringComparison.Ordinal) && target.Result.Outputs.Values.Any(value => string.Equals(value.GetString(), text, StringComparison.Ordinal))));
    }
}
