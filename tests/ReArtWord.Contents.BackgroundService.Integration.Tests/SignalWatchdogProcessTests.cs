using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Preview;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>二つの実Coreで、監視・遠隔待受・受付済み処理の寿命を分離します。</summary>
public sealed class SignalWatchdogProcessTests
{
    /// <summary>初回未受信、復帰、遅延した開始拒否、再欠落、WF無効化を実UDPで確認します。</summary>
    /// <returns>別Peerへ分散した監視とその所有資源の回収です。</returns>
    [Fact]
    public async Task RemoteRecoveryStopsOnlyOwnedListenersAndDrainsAcceptedWorkAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await first.StartAsync(timeout.Token).ConfigureAwait(true);
        await second.StartAsync(timeout.Token).ConfigureAwait(true);
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
            return status.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 2
                && (await first.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null;
        }, timeout.Token).ConfigureAwait(true);
        int mainPort = CorePeer.AvailablePort(), backupPort = CorePeer.AvailablePort();
        WorkflowDefinition definition = Definition(first, mainPort, backupPort);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Activation.Outcome);
        SourceExecutionInfo monitor = await WaitSourceAsync(second, "watch", ESourceState.Listening, timeout.Token).ConfigureAwait(true);
        SourceExecutionInfo backup = await WaitSourceAsync(first, "backup", ESourceState.Listening, timeout.Token).ConfigureAwait(true);
        SourceExecutionInfo independent = await WaitSourceAsync(first, "independent", ESourceState.Listening, timeout.Token).ConfigureAwait(true);
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        await sender.SendAsync(new byte[] { 9, 8, 7 }, new IPEndPoint(IPAddress.Loopback, backupPort), timeout.Token).ConfigureAwait(true);
        await WaitPreviewAsync(second, "accepted", timeout.Token).ConfigureAwait(true);
        await WithHeartbeatsAsync(mainPort, async token =>
        {
            SourceExecutionInfo stopped = await WaitSourceAsync(first, "backup", ESourceState.Completed, token).ConfigureAwait(false);
            Assert.Equal(backup.Id, stopped.Id);
            Assert.Equal(EOutcome.Succeeded, stopped.Result?.Outcome);
            await WaitPreviewAsync(second, "drained", token).ConfigureAwait(false);
            SourceExecutionInfo late = await WaitSourceAsync(first, "late", ESourceState.Completed, token).ConfigureAwait(false);
            Assert.Equal("source.scope.closed", late.Result?.ReasonCode);
            Assert.Equal(independent.Id, (await WaitSourceAsync(first, "independent", ESourceState.Listening, token).ConfigureAwait(false)).Id);
            Assert.Equal(monitor.Id, (await WaitSourceAsync(second, "watch", ESourceState.Listening, token).ConfigureAwait(false)).Id);
            NodePreviewHistoryEntry[] values = await second.GetAsync<NodePreviewHistoryEntry[]>("previews.history", new NodePreviewHistoryQuery(definition.Id, "drained"), token).ConfigureAwait(false);
            Assert.Single(values);
        }, timeout.Token).ConfigureAwait(true);
        SourceExecutionInfo restarted = await WaitSourceAsync(first, "backup", ESourceState.Listening, timeout.Token).ConfigureAwait(true);
        Assert.NotEqual(backup.Id, restarted.Id);
        Assert.Equal(monitor.Id, (await WaitSourceAsync(second, "watch", ESourceState.Listening, timeout.Token).ConfigureAwait(true)).Id);
        WorkflowSaveResult disabled = await first.PostAsync<WorkflowSaveResult>("workflows.save",
            new WorkflowSaveRequest(Guid.NewGuid(), definition with { Enabled = false }, saved.Revision), timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, disabled.Activation.Outcome);
        _ = await WaitSourceAsync(second, "watch", ESourceState.Completed, timeout.Token).ConfigureAwait(true);
        _ = await WaitSourceAsync(first, "backup", ESourceState.Completed, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>監視PCと待受PCを分け、無関係な待受と遅れて開始する待受も配置します。</summary>
    /// <returns>外部APIや本番ポートを使用しない定義です。</returns>
    private static WorkflowDefinition Definition(CorePeer first, int mainPort, int backupPort)
    {
        JsonElement preview = JsonSerializer.SerializeToElement(new { mediaType = "text/plain" });
        TargetDefinition local = new(EExecutionScope.One, ["a"]), remote = new(EExecutionScope.One, ["b"]);
        NodeDefinition Receive(string id, int input, bool automatic = false) => new(id, "udp.receive",
            JsonSerializer.SerializeToElement(new { interfaceId = first.InterfaceId, localAddress = "127.0.0.1", localPort = input }), Target: local, StartOnLoad: automatic);
        return new("watchdog", "Watchdog recovery", [
            Receive("main", mainPort, true),
            new("watch", "control.signalWatchdog", JsonSerializer.SerializeToElement(new { timeoutMs = 1200 }), Target: remote, StartOnLoad: true),
            Receive("backup", backupPort), Receive("independent", CorePeer.AvailablePort(), true), Receive("late", CorePeer.AvailablePort()),
            new("accepted", "control.preview", preview, Target: remote),
            new("delay", "control.delay", JsonSerializer.SerializeToElement(new { milliseconds = 3500 }), Target: remote),
            new("drained", "control.preview", preview, Target: remote)],
            [new("main", "start", "watch", "trigger"), new("watch", "start", "backup", "trigger"),
                new("backup", "value", "accepted", "value"), new("backup", "value", "delay", "value"),
                new("delay", "value", "drained", "value"), new("delay", CompletionPorts.Signal, "late", "trigger")], Enabled: true);
    }

    /// <summary>古い終了済み履歴ではなく、そのNodeの現在の待受状態を確認します。</summary>
    /// <returns>条件が成立した待受実体です。</returns>
    private static async Task<SourceExecutionInfo> WaitSourceAsync(CorePeer peer, string node, ESourceState expected, CancellationToken cancellationToken = default)
    {
        SourceExecutionInfo? observed = null;
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
            observed = status.GetProperty("sources").Deserialize<SourceExecutionInfo[]>(Contracts.Serialization.ContractJson.Options)!
                .Where(source => string.Equals(source.NodeId, node, StringComparison.Ordinal)).MaxBy(source => source.StartedAt);
            return observed?.Status.State == expected;
        }, cancellationToken).ConfigureAwait(false);
        return observed!;
    }

    /// <summary>受信処理の到達を実管理APIから確認します。</summary>
    /// <returns>対象ノードの実行確認です。</returns>
    private static Task WaitPreviewAsync(CorePeer peer, string node, CancellationToken cancellationToken = default)
        => CorePeer.WaitAsync(async token => (await peer.GetAsync<NodePreviewHistoryEntry[]>("previews.history",
            new NodePreviewHistoryQuery("watchdog", node), token).ConfigureAwait(false)).Length != 0, cancellationToken);

    /// <summary>検証の間だけ生存信号を送信し、失敗時にも送信タスクを回収します。</summary>
    /// <returns>生存中の検証と送信停止です。</returns>
    private static async Task WithHeartbeatsAsync(int port, Func<CancellationToken, Task> verify, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task pump = PumpAsync(port, sending.Token);
        try
        {
            await verify(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await sending.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sending.IsCancellationRequested)
            {
                // この試験が所有する周期送信だけを終了します。
            }
        }
    }

    /// <summary>TCP管理通信とは別の実UDP信号を周期的に投入します。</summary>
    /// <returns>取消されるまでの送信です。</returns>
    private static async Task PumpAsync(int port, CancellationToken cancellationToken = default)
    {
        using UdpClient sender = new(new IPEndPoint(IPAddress.Loopback, 0));
        while (!cancellationToken.IsCancellationRequested)
        {
            await sender.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, port), cancellationToken).ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }
}
