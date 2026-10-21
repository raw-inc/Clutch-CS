using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Files.Transactions;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの更新・旧版再参加を通して、現役予約と新しいファイル書込みの互換性を確認します。</summary>
public sealed class FileReservationRollingTests(ITestOutputHelper output)
{
    /// <summary>比較用実行物が指定された場合は旧版→新規版→旧版、未指定時は現在版の再起動を検証します。</summary>
    /// <returns>二つのCoreプロセスと一つの予約所有Peerによる、実ファイル・保持予約の確認です。</returns>
    [Fact]
    public async Task HeldReservationSurvivesCoreUpgradeAndLegacyRejoinAsync()
    {
        string? previous = Environment.GetEnvironmentVariable("CONTENTS_FILE_COMPAT_CORE");
        string id = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer initial = new(id, "a", port, true, hostId: "host-a", priority: 10, executablePath: previous);
        CorePeer current = new(id, "b", port, false, hostId: "host-b", priority: 1);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable initialLifetime = initial.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable currentLifetime = current.ConfigureAwait(true);
        string controllerRoot = Path.Combine(Path.GetDirectoryName(initial.DirectoryPath)!, "controller");
        Directory.CreateDirectory(controllerRoot);
        Directory.CreateDirectory(Path.Combine(initial.DirectoryPath, "content"));
        Directory.CreateDirectory(Path.Combine(current.DirectoryPath, "content"));
        SqliteRepository repository = new(Path.Combine(controllerRoot, "state.db"));
        PeerCluster controller = new(new(id, "controller", "host-controller", "127.0.0.1", "127.255.255.255", port, 0, false, controllerRoot), repository)
        {
            ReadinessCheck = _ => ValueTask.FromResult(true),
        };
        using ClusterFileGate gate = new(controller, repository);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable controllerLifetime = controller.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));
        await initial.StartAsync(timeout.Token).ConfigureAwait(true);
        await current.StartAsync(timeout.Token).ConfigureAwait(true);
        await controller.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(initial, current, controller, timeout.Token).ConfigureAwait(true);
        output.WriteLine(JsonSerializer.Serialize(new { previousExecutableSupplied = previous is not null,
            previous = (await initial.GetAsync<JsonElement>("health", timeout.Token).ConfigureAwait(true)).GetProperty("buildVersion").GetString() }));
        PeerInfo target = controller.GetPeers().Single(peer => peer.PeerId is "b");
        FileFootprint footprint = new(target.PeerId, target.InstanceId, target.HostId, Path.Combine(current.DirectoryPath, "content", "held.bin"), true);
        FileLeaseResult held = await gate.AcquireAsync(Guid.CreateVersion7(), [footprint], EFileConflictPolicy.Fail, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, held.Outcome);
        await WriteAsync(initial, current, "mixed", timeout.Token).ConfigureAwait(true);
        if (previous is not null)
        {
            Assert.False(await SlottedAsync(repository, timeout.Token).ConfigureAwait(true));
        }
        await initial.StopAsync(timeout.Token).ConfigureAwait(true);
        CorePeer upgraded = new(id, "a", port, false, hostId: "host-a", priority: 10);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable upgradedLifetime = upgraded.ConfigureAwait(true);
        await upgraded.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(upgraded, current, controller, timeout.Token).ConfigureAwait(true);
        await WriteAsync(upgraded, current, "upgraded", timeout.Token).ConfigureAwait(true);
        Assert.True(await SlottedAsync(repository, timeout.Token).ConfigureAwait(true));
        Assert.Equal("File.Overlap", (await gate.AcquireAsync(Guid.CreateVersion7(), [footprint], EFileConflictPolicy.Fail, timeout.Token).ConfigureAwait(true)).Reason);
        await upgraded.StopAsync(timeout.Token).ConfigureAwait(true);
        CorePeer restored = new(id, "a", port, false, hostId: "host-a", priority: 10, executablePath: previous);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable restoredLifetime = restored.ConfigureAwait(true);
        await restored.StartAsync(timeout.Token).ConfigureAwait(true);
        await ReadyAsync(restored, current, controller, timeout.Token).ConfigureAwait(true);
        if (previous is not null)
        {
            await CorePeer.WaitAsync(async token => !await SlottedAsync(repository, token).ConfigureAwait(true), timeout.Token).ConfigureAwait(true);
        }
        Assert.Equal("File.Overlap", (await gate.AcquireAsync(Guid.CreateVersion7(), [footprint], EFileConflictPolicy.Fail, timeout.Token).ConfigureAwait(true)).Reason);
        await WriteAsync(restored, current, "restored", timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, (await gate.ReleaseAsync(held.Lease!, timeout.Token).ConfigureAwait(true)).Outcome);
        FileLeaseResult next = await gate.AcquireAsync(Guid.CreateVersion7(), [footprint], EFileConflictPolicy.Fail, timeout.Token).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, next.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await gate.ReleaseAsync(next.Lease!, timeout.Token).ConfigureAwait(true)).Outcome);
        output.WriteLine(initial.DirectoryPath);
    }

    /// <summary>毎回新しい操作で書込み、保存結果と実ファイルの両方を確認します。</summary>
    /// <returns>更新段階ごとの実ファイル書込みです。</returns>
    private static async Task WriteAsync(CorePeer owner, CorePeer target, string phase, CancellationToken cancellationToken = default)
    {
        string path = Path.Combine(target.DirectoryPath, "content", phase + ".txt");
        WorkflowDefinition definition = new("file-rolling-" + phase, "File reservation compatibility", [
            new("start", "control.button", JsonSerializer.SerializeToElement(new { }), new Dictionary<string, NodeValue> { ["value"] = NodeValue.FromObject(phase) }, new(EExecutionScope.One, ["a"])),
            new("write", "file.upsert", JsonSerializer.SerializeToElement(new { path }), Target: new(EExecutionScope.One, ["b"]))],
            [new("start", "value", "write", "value")]);
        WorkflowSaveResult saved = await owner.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.CreateVersion7(), definition, null), cancellationToken).ConfigureAwait(false);
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        WorkflowRunStatus result = await owner.PostAsync<WorkflowRunStatus>("runs.start", new WorkflowRunRequest(Guid.CreateVersion7(), definition.Id, saved.Revision, "start"), cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token =>
        {
            result = await owner.GetAsync<WorkflowRunStatus>("runs.read", result.OperationId.ToString("D"), token).ConfigureAwait(false);
            return !result.Running;
        }, cancellationToken).ConfigureAwait(false);
        Assert.True(result.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(result));
        Assert.Equal(phase, await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>旧実体のReady表示を使わず、新しい実体と全参加者の相互観測まで待ちます。</summary>
    /// <returns>保持予約を同期済みの3実体です。</returns>
    private static async Task ReadyAsync(CorePeer first, CorePeer second, PeerCluster controller, CancellationToken cancellationToken = default)
    {
        await CorePeer.WaitAsync(async token =>
        {
            JsonElement a = await first.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
            JsonElement b = await second.GetAsync<JsonElement>("status", token).ConfigureAwait(false);
            Guid instance = a.GetProperty("peers").EnumerateArray().Single(peer => peer.GetProperty("peerId").GetString() is "a").GetProperty("instanceId").GetGuid();
            return new[] { a, b }.All(state => state.GetProperty("peers").EnumerateArray().Count(peer => peer.GetProperty("status").GetString() is "ready") == 3)
                && controller.GetPeers().Count(peer => peer.Status == EPeerStatus.Ready) == 3 && controller.GetPeers().Any(peer => peer.InstanceId == instance)
                && controller.CoordinatorId is "a";
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>復旧の対象となる現在の保存形式だけを観測します。</summary>
    /// <returns>個別スロットが有効かです。</returns>
    private static async Task<bool> SlottedAsync(SqliteRepository repository, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("file:reservations", "active", cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.TryGetProperty("Format", out _) == true;
    }
}
