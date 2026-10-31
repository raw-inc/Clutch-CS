using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>隔離した実Core・実アプリケーションと管理通信で、自動監視とWFへの通知を確認します。</summary>
public sealed class ApplicationLifecycleProcessTests
{
    /// <summary>スケジュールから始まった自動復旧でも、同じユーザーと非表示指定を使用します。</summary>
    /// <returns>予定起動と自動復旧で起動設定を保持し、明示停止で検証を終える確認です。</returns>
    [ApplicationDesktopFact]
    public async Task InteractiveRecoveryAndScheduleKeepLaunchOptionsAsync()
    {
        using ApplicationProcessFixture app = new(exitCode: 17, delayMilliseconds: 100, withArguments: true);
        await File.WriteAllTextAsync(Path.Combine(app.Root, "application-fixture.json"), "{\"exitCode\":17,\"delayMs\":100,\"window\":true}").ConfigureAwait(true);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(75));
        await ReadyAsync(peer, timeout.Token).ConfigureAwait(true);
        ProcessDefinition definition = app.Definition with
        {
            Launch = new(Window: EProcessWindowMode.NoWindow), Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 500),
            Schedules = [new("initial", "once", At: DateTimeOffset.UtcNow.AddSeconds(2).ToString("O", CultureInfo.InvariantCulture))],
        };
        await RegisterAsync(peer, definition, timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(_ => Task.FromResult(2 <= app.Starts().Length && app.Starts().Take(2).All(start => File.Exists(Path.Combine(app.Root, start[0] + ".window.json")))), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await ActionAsync(peer, "process.stop", definition.Id, timeout.Token).ConfigureAwait(true)).Outcome);
        string[][] starts = app.Starts().OrderBy(item => item[1], StringComparer.Ordinal).Take(2).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.True(TimeSpan.FromMilliseconds(500) <= DateTimeOffset.Parse(starts[1][1], CultureInfo.InvariantCulture) - DateTimeOffset.Parse(starts[0][1], CultureInfo.InvariantCulture));
        foreach (string[] start in starts)
        {
            Assert.Equal(definition.Arguments, start.Skip(2));
            JsonElement window = JsonSerializer.Deserialize<JsonElement>(await PublishedFixtureFile.ReadAllTextAsync(Path.Combine(app.Root, start[0] + ".window.json"), timeout.Token).ConfigureAwait(true));
            Assert.False(window.GetProperty("Visible").GetBoolean());
            Assert.False(window.GetProperty("HasConsole").GetBoolean());
            Assert.NotEqual(0, window.GetProperty("SessionId").GetInt32());
            Assert.NotEqual("S-1-5-18", window.GetProperty("UserId").GetString());
        }
    }

    /// <summary>実CoreからGUIをユーザーセッションへ起動し、Core再起動後も同じ実体へ通常終了を送ります。</summary>
    /// <returns>SYSTEMで実行した場合はSession 0からデスクトップへの横断も確認します。</returns>
    [ApplicationDesktopFact]
    public async Task CoreLaunchesAndClosesInteractiveWindowAfterRestartAsync()
    {
        await VerifyInteractiveWindowAsync(EProcessWindowMode.Default).ConfigureAwait(true);
        await VerifyInteractiveWindowAsync(EProcessWindowMode.NoWindow).ConfigureAwait(true);
    }

    /// <summary>表示方法ごとに実Core・アプリケーション・保存先を分離します。</summary>
    /// <returns>起動、表示、監視再接続と停止の一巡です。</returns>
    private static async Task VerifyInteractiveWindowAsync(EProcessWindowMode mode, CancellationToken cancellationToken = default)
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1, withArguments: true);
        await File.WriteAllTextAsync(Path.Combine(app.Root, "application-fixture.json"), "{\"exitCode\":0,\"delayMs\":-1,\"window\":true}", cancellationToken).ConfigureAwait(false);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        await ReadyAsync(peer, timeout.Token).ConfigureAwait(false);
        ProcessDefinition definition = app.Definition with { Launch = new(Window: mode) };
        await RegisterAsync(peer, definition, timeout.Token).ConfigureAwait(false);
        NodeResult started = await ActionAsync(peer, "process.start", definition.Id, timeout.Token).ConfigureAwait(false);
        Assert.True(started.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(started));
        int pid = started.Outputs["pid"].Json.GetInt32();
        string path = Path.Combine(app.Root, pid + ".window.json");
        await CorePeer.WaitAsync(_ => Task.FromResult(File.Exists(path)), timeout.Token).ConfigureAwait(false);
        JsonElement window = JsonSerializer.Deserialize<JsonElement>(await PublishedFixtureFile.ReadAllTextAsync(path, timeout.Token).ConfigureAwait(false));
        Assert.NotEqual(0, window.GetProperty("SessionId").GetInt32());
        Assert.NotEqual("S-1-5-18", window.GetProperty("UserId").GetString());
        Assert.Equal(mode == EProcessWindowMode.Default, window.GetProperty("Visible").GetBoolean());
        string? expectedUser = Environment.GetEnvironmentVariable("CONTENTS_TEST_APPLICATION_USER_SID");
        if (expectedUser is not null)
        {
            Assert.Equal(expectedUser, window.GetProperty("UserId").GetString());
        }
        await peer.StopHostOnlyAsync(timeout.Token).ConfigureAwait(false);
        await ReadyAsync(peer, timeout.Token).ConfigureAwait(false);
        ApplicationView application = Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", timeout.Token).ConfigureAwait(false));
        Assert.Equal(pid, application.Observed.Status!.Identity!.ProcessId);
        Assert.True(application.Observed.Status.Running);
        Assert.Single(app.Starts());
        if (mode == EProcessWindowMode.NoWindow)
        {
            Assert.False(window.GetProperty("HasConsole").GetBoolean());
            app.Release();
        }
        JsonElement status = await peer.GetAsync<JsonElement>("status", timeout.Token).ConfigureAwait(false);
        Guid operation = Guid.NewGuid();
        NodeDefinition stop = new("stop", "process.stop", JsonSerializer.SerializeToElement(new { processId = definition.Id, failurePolicy = "waitForManual", gracefulTimeoutMs = 5000 }), Target: new(EExecutionScope.One, ["a"]));
        NodeResult stopped = await peer.PostAsync<NodeResult>("nodes.execute", new RemoteExecutionRequest(operation, operation, "test", "test", stop,
            new Dictionary<string, NodeValue>(), "a", status.GetProperty("instanceId").GetGuid(), DateTimeOffset.UtcNow.AddSeconds(20)), timeout.Token).ConfigureAwait(false);
        Assert.True(stopped.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(stopped));
        Assert.Equal(0, Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", timeout.Token).ConfigureAwait(false)).Observed.Status!.ExitCode);
    }

    /// <summary>ブラウザやWFがなくても、引数を保って遅延後だけ再起動し、指定回数で止まります。</summary>
    /// <returns>正常・異常の自明終了から永続した試行上限までの確認です。</returns>
    [Theory(Skip = "起動成功後も試行を累積する旧仕様です。ApplicationRecoveryProcessTestsで起動成功時のリセットと復旧遅延を検証します。")]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(17, false)]
    [InlineData(17, true)]
    public async Task CoreAutomaticallyRestartsRealApplicationAfterConfiguredDelayAsync(int exitCode, bool arguments)
    {
        using ApplicationProcessFixture app = new(exitCode, 100, arguments);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(75));
        await ReadyAsync(peer, timeout.Token);
        await RegisterAsync(peer, app.Definition with { Recovery = new(MaximumAttempts: 2, DelayMilliseconds: 750) }, timeout.Token);
        NodeResult initial = await ActionAsync(peer, "process.start", app.Definition.Id, timeout.Token);
        Assert.True(initial.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(initial));
        ApplicationView observed = default;
        await CorePeer.WaitAsync(async token =>
        {
            observed = Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", token).ConfigureAwait(false));
            return observed.Observed.Status?.RecoveryExhausted == true;
        }, timeout.Token);
        Assert.Equal(exitCode, observed.Observed.Status!.ExitCode);
        string[][] starts = app.Starts().OrderBy(entry => entry[1], StringComparer.Ordinal).ToArray();
        Assert.Equal(3, starts.Length);
        Assert.All(starts, entry => Assert.Equal(app.Definition.Arguments, entry.Skip(2)));
        for (int index = 1; index < starts.Length; index++)
        {
            TimeSpan difference = DateTimeOffset.Parse(starts[index][1], CultureInfo.InvariantCulture) - DateTimeOffset.Parse(starts[index - 1][1], CultureInfo.InvariantCulture);
            Assert.True(TimeSpan.FromMilliseconds(750) <= difference, difference.ToString());
        }
        Assert.Equal(2, observed.Observed.Status.RecoveryAttempts);
    }

    /// <summary>Coreだけを停止してもアプリを残し、復帰後の同じ起動回へ再接続します。</summary>
    /// <returns>停止中の終了を推測して復旧しないことも実Coreで確認します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreRestartReattachesLiveProcessAndDoesNotReplayOfflineExitAsync(bool exitWhileOffline)
    {
        using ApplicationProcessFixture app = new(exitCode: 31, delayMilliseconds: -1, withArguments: true);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await ReadyAsync(peer, timeout.Token);
        await RegisterAsync(peer, app.Definition with { Recovery = new(DelayMilliseconds: 250) }, timeout.Token);
        NodeResult start = await ActionAsync(peer, "process.start", app.Definition.Id, timeout.Token);
        int pid = start.Outputs["pid"].Json.GetInt32();
        await CorePeer.WaitAsync(_ => Task.FromResult(app.Starts().Length == 1), timeout.Token);
        using Process process = Process.GetProcessById(pid);
        await peer.StopHostOnlyAsync(timeout.Token);
        Assert.False(process.HasExited);
        if (exitWhileOffline)
        {
            app.Release();
            await ProcessCompletion.WaitAsync(process, timeout.Token);
        }
        await ReadyAsync(peer, timeout.Token);
        ApplicationView observed = Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", timeout.Token));
        Assert.Equal(pid, observed.Observed.Status!.Identity!.ProcessId);
        Assert.Equal(!exitWhileOffline, observed.Observed.Status.Running);
        Assert.Equal(exitWhileOffline, observed.Observed.Status.ExitUnobserved);
        if (!exitWhileOffline)
        {
            process.Kill();
            await ProcessCompletion.WaitAsync(process, timeout.Token);
            await CorePeer.WaitAsync(_ => Task.FromResult(app.Starts().Length == 2), timeout.Token);
        }
        else
        {
            await Task.Delay(1000, timeout.Token);
            Assert.Single(app.Starts());
        }
    }

    /// <summary>Coreのスケジュールが起動し、起動・再起動・停止SignalとPIDがWF後続へ届きます。</summary>
    /// <returns>別Peerのアプリを誤操作せず、実管理通信と実WFを経由した確認です。</returns>
    [Fact]
    public async Task ScheduledStartAndLifecycleSignalsAreScopedToRegisteredPeerAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        using ApplicationProcessFixture app = new(delayMilliseconds: -1);
        CorePeer peer = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable otherLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await ReadyAsync(peer, timeout.Token);
        await ReadyAsync(second, timeout.Token);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("peers").EnumerateArray()
            .Count(value => value.GetProperty("status").GetString() is "ready") == 2, timeout.Token);
        await RegisterAsync(peer, app.Definition, timeout.Token);
        Assert.Empty(await second.GetAsync<ApplicationView[]>("processes.list", timeout.Token));
        JsonElement empty = JsonSerializer.SerializeToElement(new { });
        WorkflowDefinition workflow = new("application-events", "Application lifecycle", [
            new("watch", "process.lifecycle", JsonSerializer.SerializeToElement(new { processId = app.Definition.Id }), Target: new(EExecutionScope.One, ["a"]), StartOnLoad: true),
            new("started", "control.preview", empty, Target: new(EExecutionScope.One, ["b"])),
            new("stopped", "control.preview", empty, Target: new(EExecutionScope.One, ["b"])),
            new("restarted", "control.preview", empty, Target: new(EExecutionScope.One, ["b"]))],
            [new("watch", "started", "started", "trigger"), new("watch", "pid", "started", "value"),
             new("watch", "stopped", "stopped", "trigger"), new("watch", "pid", "stopped", "value"),
             new("watch", "restarted", "restarted", "trigger"), new("watch", "pid", "restarted", "value")], Enabled: true);
        WorkflowSaveResult save = await peer.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), workflow, null), timeout.Token);
        Assert.Equal(EOutcome.Succeeded, save.Activation.Outcome);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("sources").EnumerateArray()
            .Any(source => source.GetProperty("nodeId").GetString() is "watch" && source.GetProperty("status").GetProperty("state").GetString() is "listening"), timeout.Token);
        ApplicationView registration = Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", timeout.Token));
        ProcessSchedule schedule = new("start", "once", At: DateTimeOffset.UtcNow.AddSeconds(2).ToString("O", CultureInfo.InvariantCulture));
        JsonElement registered = await peer.PostAsync<JsonElement>("processes.register", new ProcessRegistrationRequest(app.Definition with { Schedules = [schedule] }, registration.Version), timeout.Token);
        Assert.Equal("succeeded", registered.GetProperty("outcome").GetString());
        await CorePeer.WaitAsync(_ => Task.FromResult(app.Starts().Length == 1), timeout.Token);
        int first = int.Parse(Assert.Single(app.Starts())[0], CultureInfo.InvariantCulture);
        await PreviewAsync(second, workflow.Id, "started", first, timeout.Token);
        NodeResult restart = await ActionAsync(peer, "process.restart", app.Definition.Id, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, restart.Outcome);
        int next = restart.Outputs["pid"].Json.GetInt32();
        Assert.NotEqual(first, next);
        await PreviewAsync(second, workflow.Id, "restarted", next, timeout.Token);
        await PreviewAsync(second, workflow.Id, "stopped", first, timeout.Token);
        Assert.Equal(EOutcome.Succeeded, (await ActionAsync(peer, "process.stop", app.Definition.Id, timeout.Token)).Outcome);
        await PreviewAsync(second, workflow.Id, "stopped", next, timeout.Token);
        await Task.Delay(500, timeout.Token);
        Assert.False(Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", timeout.Token)).Observed.Status!.Running);
        Assert.Single(await second.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery(workflow.Id, "started"), timeout.Token));
        Assert.Single(await second.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery(workflow.Id, "restarted"), timeout.Token));
        Assert.Equal(2, (await second.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery(workflow.Id, "stopped"), timeout.Token)).Length);
    }

    /// <summary>試験Coreの準備完了を実観測します。</summary>
    /// <returns>運用開始可能な状態です。</returns>
    internal static async Task ReadyAsync(CorePeer peer, CancellationToken cancellationToken = default)
    {
        await peer.StartAsync(cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("status").GetString() is "ready"
            && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>明示Peerへ実登録を送信します。</summary>
    /// <returns>比較保存済みの登録です。</returns>
    internal static async Task RegisterAsync(CorePeer peer, ProcessDefinition definition, CancellationToken cancellationToken = default)
    {
        JsonElement result = await peer.PostAsync<JsonElement>("processes.register", new ProcessRegistrationRequest(definition, null), cancellationToken).ConfigureAwait(false);
        Assert.Equal("succeeded", result.GetProperty("outcome").GetString());
    }

    /// <summary>実管理経路から登録対象の操作Nodeを一度だけ実行します。</summary>
    /// <returns>PID出力を含む実結果です。</returns>
    internal static async Task<NodeResult> ActionAsync(CorePeer peer, string type, string id, CancellationToken cancellationToken = default)
    {
        JsonElement status = await peer.GetAsync<JsonElement>("status", cancellationToken).ConfigureAwait(false);
        string peerId = status.GetProperty("peerId").GetString()!;
        Guid operation = Guid.NewGuid();
        JsonElement settings = JsonSerializer.SerializeToElement(new { processId = id });
        string stop = "process.stop", restart = "process.restart";
        if (string.Equals(type, stop, StringComparison.Ordinal) || string.Equals(type, restart, StringComparison.Ordinal))
        {
            settings = JsonSerializer.SerializeToElement(new { processId = id, failurePolicy = "force", gracefulTimeoutMs = 1 });
        }
        NodeDefinition node = new("action", type, settings, Target: new(EExecutionScope.One, [peerId]));
        return await peer.PostAsync<NodeResult>("nodes.execute", new RemoteExecutionRequest(operation, operation, "test", "test", node,
            new Dictionary<string, NodeValue>(), peerId, status.GetProperty("instanceId").GetGuid(), DateTimeOffset.UtcNow.AddSeconds(20)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signalの接続先だけに指定PIDが表示されたことを確認します。</summary>
    /// <returns>独立した実行と遠隔Previewへの配送です。</returns>
    private static Task PreviewAsync(CorePeer peer, string workflow, string node, int pid, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token => (await peer.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery(workflow, node), token).ConfigureAwait(false))
            .Any(value => string.Equals(value.Text, pid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)), cancellationToken);

    /// <summary>公開APIから比較保存版と観測状態を読み取ります。</summary>
    private readonly record struct ApplicationView(string Version, ProcessDefinition Definition, ProcessOperationResult Observed, IReadOnlyList<ProcessScheduleStatus> Schedules);

    /// <summary>実ウィンドウを作る検証は、Windowsでの明示実行に限定します。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class ApplicationDesktopFactAttribute : FactAttribute
    {
        /// <summary>CIや非対応OSで実機検証済みと数えません。</summary>
        public ApplicationDesktopFactAttribute()
        {
            string variable = "CONTENTS_TEST_APPLICATION_LAUNCH";
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
            {
                Skip = "Set CONTENTS_TEST_APPLICATION_LAUNCH on Windows to create isolated test windows.";
            }
        }
    }
}
