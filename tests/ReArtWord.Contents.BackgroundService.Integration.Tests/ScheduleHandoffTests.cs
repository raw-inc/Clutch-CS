using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>時間指定を担当した実プロセスが替わっても、同じ回の副作用を重ねないことを確認します。</summary>
public sealed class ScheduleHandoffTests
{
    /// <summary>対象不在でまだ配送していない回は、入力元PCの喪失後も保持した内容で一度だけ開始できます。</summary>
    /// <returns>三台の実Coreを使った、未開始入力の引継ぎ確認。</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task QueuedOccurrenceHandoffDoesNotReplayStartedWorkAsync(bool originReturns, bool startedWork, bool cancel)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", 900);
        CorePeer standby = new(cluster, "c", port, false, "host-c", 800);
        CorePeer target = new(cluster, "b", port, false, "host-b", 100);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable standbyLifetime = standby.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable targetLifetime = target.ConfigureAwait(true);
        string root = Path.Combine(target.DirectoryPath, "content");
        Directory.CreateDirectory(root);
        string output = Path.Combine(root, "retained-once.txt");
        await File.WriteAllTextAsync(output, string.Empty).ConfigureAwait(true);
        string firstRoot = Path.Combine(first.DirectoryPath, "content");
        Directory.CreateDirectory(firstRoot);
        string firstOutput = Path.Combine(firstRoot, "started.txt");
        await File.WriteAllTextAsync(firstOutput, string.Empty).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await standby.StartAsync().ConfigureAwait(true);
        await target.StartAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Count(peer => string.Equals(peer.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 3).ConfigureAwait(true);
        await target.StopAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("peers").EnumerateArray()
            .Any(peer => string.Equals(peer.GetProperty("peerId").GetString(), "b", StringComparison.Ordinal)
                && string.Equals(peer.GetProperty("status").GetString(), "unavailable", StringComparison.Ordinal))).ConfigureAwait(true);
        List<NodeDefinition> nodes = [
            new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(new { mode = "once", at = DateTimeOffset.UtcNow.AddSeconds(1) }), StartOnLoad: true),
            Append("write", output, "b"),
        ];
        List<WireDefinition> wires = [new("timer", "value", "write", "trigger")];
        if (startedWork)
        {
            nodes.Add(Append("first", firstOutput, "a"));
            wires = [new("timer", "value", "first", "trigger"), new("first", "value", "write", "trigger")];
        }
        WorkflowDefinition definition = new("retained-once", "Retained scheduled input", nodes, wires, Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sourceQueue").GetProperty("running").GetInt32() != 0).ConfigureAwait(true);
        if (startedWork)
        {
            await CorePeer.WaitAsync(async token => string.Equals(await FileObservation.ReadFirstAppendAsync(firstOutput, token).ConfigureAwait(true), "tick\n", StringComparison.Ordinal)).ConfigureAwait(true);

            // ファイル追記後の予約解放を終えてから、担当Coreを停止します。
            await CorePeer.WaitAsync(async token => (await first.GetAsync<JsonElement>("status", token).ConfigureAwait(true))
                .GetProperty("runningNodes").GetArrayLength() == 0).ConfigureAwait(true);
        }
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(output).ConfigureAwait(true));
        Guid? cancelledRun = null;
        if (cancel)
        {
            ActiveRunsPage? active = null;

            // タイマーの終了通知が完了し、配送待ちの一件だけになるまで待ちます。
            await CorePeer.WaitAsync(async token =>
            {
                active = await first.GetAsync<ActiveRunsPage>("runs.active", token).ConfigureAwait(true);
                return active.Items.Count == 1;
            }).ConfigureAwait(true);
            WorkflowRunStatus run = Assert.Single(active!.Items);
            cancelledRun = run.OperationId;
            WorkflowCancelResult cancelled = await first.PostAsync<WorkflowCancelResult>("runs.cancel", new WorkflowCancelRequest(Guid.NewGuid(), run.OperationId, run.OwnerInstanceId!.Value)).ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, cancelled.Outcome);
            await WaitForCancellationAsync(first, run.OperationId).ConfigureAwait(true);
        }
        await first.StopAsync().ConfigureAwait(true);
        await CorePeer.WaitAsync(async token => string.Equals((await standby.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("coordinator").GetString(), "c", StringComparison.Ordinal)).ConfigureAwait(true);
        if (startedWork)
        {
            await CorePeer.WaitAsync(async token => string.Equals((await standby.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("scheduleProblem").GetString(), "schedule.ownerResultUnavailable", StringComparison.Ordinal)).ConfigureAwait(true);
        }
        else
        {
            if (cancelledRun is Guid id)
            {
                // 担当の選出後に予定入力を整理するため、取消意図に基づく終端記録を待ちます。
                await WaitForCancellationAsync(standby, id).ConfigureAwait(true);
                Assert.Equal(EOutcome.Cancelled, (await standby.GetAsync<WorkflowRunStatus>("runs.read", id.ToString("D")).ConfigureAwait(true)).Outcome);
            }
            else
            {
                await CorePeer.WaitAsync(async token => (await standby.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("schedules").GetProperty("locallyManagedInputs").GetInt32() == 1).ConfigureAwait(true);
            }
        }
        if (originReturns)
        {
            await first.StartAsync().ConfigureAwait(true);
            await ReadyAsync(first).ConfigureAwait(true);
            Assert.Equal(1, (await standby.GetAsync<JsonElement>("status").ConfigureAwait(true)).GetProperty("schedules").GetProperty("locallyManagedInputs").GetInt32());
        }
        await target.StartAsync().ConfigureAwait(true);
        await ReadyAsync(target).ConfigureAwait(true);
        if (!startedWork && !cancel)
        {
            await CorePeer.WaitAsync(async token => string.Equals(await FileObservation.ReadFirstAppendAsync(output, token).ConfigureAwait(true), "tick\n", StringComparison.Ordinal)).ConfigureAwait(true);
        }
        await CorePeer.WaitAsync(async token => (await standby.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("sourceQueue").GetProperty("running").GetInt32() == 0).ConfigureAwait(true);
        string expected = "tick\n";
        if (startedWork || cancel)
        {
            expected = string.Empty;
            if (startedWork)
            {
                Assert.Equal("tick\n", await File.ReadAllTextAsync(firstOutput).ConfigureAwait(true));
            }
        }
        Assert.Equal(expected, await File.ReadAllTextAsync(output).ConfigureAwait(true));
    }

    /// <summary>優先度の高いPCが後から参加しても、一回の発火と正常終了経路を繰り返しません。</summary>
    /// <returns>実ファイルへの追記回数と、新担当の受付済み位置の確認。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedOnceDoesNotRepeatWhenHigherPriorityPeerJoinsAsync(bool explicitZone)
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true, "host-a", 100);
        CorePeer second = new(cluster, "b", port, false, "host-b", 900);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        string root = Path.Combine(first.DirectoryPath, "content");
        Directory.CreateDirectory(root);
        string tick = Path.Combine(root, "tick.txt");
        string completed = Path.Combine(root, "completed.txt");
        await File.WriteAllTextAsync(tick, string.Empty).ConfigureAwait(true);
        await File.WriteAllTextAsync(completed, string.Empty).ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        Dictionary<string, object> settings = new() { ["mode"] = "once", ["at"] = DateTimeOffset.UtcNow.AddSeconds(1) };
        if (explicitZone)
        {
            settings["timeZoneId"] = "Asia/Tokyo";
            settings["at"] = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddSeconds(1), TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }
        WorkflowDefinition definition = new("scheduled-once", "Once across coordinator change", [
            new("timer", "scheduling.timer", JsonSerializer.SerializeToElement(settings), StartOnLoad: true),
            Append("tick", tick), Append("complete", completed),
        ], [new("timer", "value", "tick", "trigger"), new("timer", "source", "complete", "trigger", When: "completed")], Enabled: true);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        ExecutionReport[] reports = [];
        await CorePeer.WaitAsync(async token =>
        {
            ExecutionHistoryPage history = await first.GetAsync<ExecutionHistoryPage>("executions.list", token).ConfigureAwait(true);
            reports = history.Entries.Select(entry => entry.Report).OfType<ExecutionReport>()
                .Where(report => string.Equals(report.WorkflowId, definition.Id, StringComparison.Ordinal)).ToArray();
            return 2 <= reports.Length;
        }).ConfigureAwait(true);
        Assert.Equal(2, reports.Length);
        Assert.All(reports, report => Assert.Equal(EOutcome.Succeeded, report.Outcome));
        Assert.Equal("tick\n", await File.ReadAllTextAsync(tick).ConfigureAwait(true));
        Assert.Equal("tick\n", await File.ReadAllTextAsync(completed).ConfigureAwait(true));
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(second).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
            return string.Equals(status.GetProperty("coordinator").GetString(), "b", StringComparison.Ordinal)
                && status.GetProperty("sources").EnumerateArray().Any(source => string.Equals(source.GetProperty("nodeId").GetString(), "timer", StringComparison.Ordinal)
                    && source.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object
                    && string.Equals(result.GetProperty("route").GetString(), "alreadyCompleted", StringComparison.Ordinal))
                && status.GetProperty("sourceQueue").GetProperty("running").GetInt32() == 0
                && status.GetProperty("sourceQueue").GetProperty("waiting").GetInt32() == 0;
        }).ConfigureAwait(true);
        Assert.Equal("tick\n", await File.ReadAllTextAsync(tick).ConfigureAwait(true));
        Assert.Equal("tick\n", await File.ReadAllTextAsync(completed).ConfigureAwait(true));
    }

    /// <summary>取消要求の受付と、処理・確定保存の完了を区別して確認します。</summary>
    /// <returns>担当を停止できる取消完了の確認です。</returns>
    private static async Task WaitForCancellationAsync(CorePeer peer, Guid operation, CancellationToken cancellationToken = default)
    {
        await CorePeer.WaitAsync(async token =>
        {
            WorkflowRunStatus status = await peer.GetAsync<WorkflowRunStatus>("runs.read", operation.ToString("D"), token).ConfigureAwait(false);
            string unconfirmed = "schedule.resultUnconfirmed";
            return status.Outcome == EOutcome.Cancelled && !status.Running && !string.Equals(status.Reason, unconfirmed, StringComparison.Ordinal);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>元PCの許可された場所へ、実行のたびに一行を追加します。</summary>
    /// <returns>回数がファイルから確認できる処理。</returns>
    private static NodeDefinition Append(string id, string path, string peer = "a") => new(id, "file.append", JsonSerializer.SerializeToElement(new { path }),
        new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject("tick\n") }, Target: new(EExecutionScope.One, [peer]));

    /// <summary>共通設定を使用できる通常参加状態を待ちます。</summary>
    /// <returns>初期化または同期の完了確認。</returns>
    private static Task ReadyAsync(CorePeer peer) => CorePeer.WaitAsync(async token =>
        string.Equals((await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true)).GetProperty("status").GetString(), "ready", StringComparison.Ordinal)
        && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(true)).Effective is not null);
}
