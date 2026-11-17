using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>派生IDの保存・配送・再起動後の照会を、複数の実Coreで確認します。</summary>
public sealed class DerivedIdentityProcessTests
{
    /// <summary>旧Coreからの取得にも対応し、形式を照会できた初回要求だけを元の記録で再送します。</summary>
    /// <returns>旧形式のCoreを含む設定保存と遠隔Snapshot取得です。</returns>
    [IdentityCompatibilityFact]
    public async Task OlderPeerCanCaptureWithoutRewritingTheSavedRequestAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true);
        CorePeer second = new(cluster, "b", port, false, executablePath: Environment.GetEnvironmentVariable("CONTENTS_IDENTITY_COMPAT_CORE"));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        WorkflowDefinition definition = new("compatible", "Mixed versions", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }))], []);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", new WorkflowSaveRequest(Guid.NewGuid(), definition, null)).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
        SnapshotCaptureRequest request = new(Guid.NewGuid(), new(definition.Id, "b", ESnapshotKind.Configuration));
        SnapshotCaptureResult captured = await first.PostAsync<SnapshotCaptureResult>("snapshots.capture", request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, captured.Result.Outcome);
        Assert.Equal("b", captured.Snapshot!.PeerId);
        SnapshotCaptureResult repeated = await first.PostAsync<SnapshotCaptureResult>("snapshots.capture", request).ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(captured), ContractJson.Hash(repeated));
    }

    /// <summary>旧方式では時刻付きIDになる保存要求も、新方式で一度だけ共有し、同じ記録を再照会します。</summary>
    /// <returns>実プロセス・UDP・保存媒体を通した確認です。</returns>
    [Fact]
    public async Task SharedSnapshotKeepsItsOpaqueIdAfterCoordinatorRestartAsync()
    {
        string cluster = Guid.NewGuid().ToString("N");
        int port = CorePeer.AvailablePort();
        CorePeer first = new(cluster, "a", port, true), second = new(cluster, "b", port, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable firstLifetime = first.ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable secondLifetime = second.ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await second.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        WorkflowDefinition definition = new("identity", "Derived identity", [new("pass", "control.pass", JsonSerializer.SerializeToElement(new { }))], []);
        Guid operation = Guid.Parse("00000000-0000-0000-0000-00000000025f");
        WorkflowSaveRequest request = new(operation, definition, null);
        WorkflowSaveResult saved = await first.PostAsync<WorkflowSaveResult>("workflows.save", request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Succeeded, saved.Saved.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Shared.Outcome);
        Assert.Equal(EOutcome.Succeeded, saved.Snapshot.Outcome);
        string snapshotId = "b1a15c0033108c77ac8e51522b813554";
        await CorePeer.WaitAsync(async token =>
        {
            SnapshotListPage page = await second.GetAsync<SnapshotListPage>("snapshots.list", cancellationToken: token).ConfigureAwait(true);
            return page.Entries.Any(item => string.Equals(item.Id, snapshotId, StringComparison.Ordinal));
        }).ConfigureAwait(true);
        await first.StopAsync().ConfigureAwait(true);
        await first.StartAsync().ConfigureAwait(true);
        await ReadyAsync(first).ConfigureAwait(true);
        WorkflowSaveResult repeated = await first.PostAsync<WorkflowSaveResult>("workflows.save", request).ConfigureAwait(true);
        Assert.Equal(ContractJson.Hash(saved), ContractJson.Hash(repeated));
        SnapshotListPage after = await first.GetAsync<SnapshotListPage>("snapshots.list").ConfigureAwait(true);
        Assert.Single(after.Entries, item => string.Equals(item.Id, snapshotId, StringComparison.Ordinal));
    }

    /// <summary>両実体の準備完了を確認してから、固定集合へ設定を保存します。</summary>
    /// <returns>クラスタ試験共通の観測期限内での準備確認です。</returns>
    private static Task ReadyAsync(CorePeer peer) => CorePeer.WaitAsync(async token =>
    {
        JsonElement status = await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(true);
        return status.GetProperty("peers").EnumerateArray().Count(item => string.Equals(item.GetProperty("status").GetString(), "ready", StringComparison.Ordinal)) == 2;
    });
}
