using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>隔離DBの前回OS起動記録から、実Coreが現在の起動を一度だけ受け付けることを検証します。</summary>
public sealed class ApplicationBootProcessTests
{
    /// <summary>Core停止中にアプリが終了しても、同じPC起動の予定を再実行しません。</summary>
    /// <returns>実OSの起動識別、管理通信、引数、Core再起動をまたぐ確認です。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RealCoreClaimsCurrentBootOnceAcrossRestartAsync(bool arguments, bool exitWhileOffline)
    {
        using ApplicationProcessFixture app = new(delayMilliseconds: -1, withArguments: arguments);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        await ReadyAsync(peer, timeout.Token);
        await peer.StopHostOnlyAsync(timeout.Token);
        string directory = Path.Combine(peer.DirectoryPath, "state");
        Directory.CreateDirectory(directory);
        SqliteRepository repository = new(Path.Combine(directory, "state.db"));
        ProcessDefinition definition = app.Definition with { AutomaticMonitoring = false, Schedules = [new("boot", "boot")] };
        using (ProcessService registration = new(repository, "a", new(), bootSession: () => new SystemBootSession("previous-isolated-boot", TimeSpan.Zero)))
        {
            Assert.Equal(EOutcome.Succeeded, await registration.RegisterAsync(definition, cancellationToken: timeout.Token));
        }
        await ReadyAsync(peer, timeout.Token);
        await CorePeer.WaitAsync(_ => Task.FromResult(app.Starts().Length == 1), timeout.Token);
        int pid = int.Parse(Assert.Single(app.Starts())[0], CultureInfo.InvariantCulture);
        Assert.Equal(app.Definition.Arguments, Assert.Single(app.Starts()).Skip(2));
        ApplicationView before = default;
        await CorePeer.WaitAsync(async token =>
        {
            before = Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", token).ConfigureAwait(false));
            return Assert.Single(before.Schedules).Outcome is not (null or EOutcome.Unknown);
        }, timeout.Token);
        ProcessScheduleStatus completed = Assert.Single(before.Schedules);
        Assert.True(completed.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(completed));
        Assert.Equal(pid, before.Observed.Status!.Identity!.ProcessId);
        Assert.Null(completed.NextAt);
        DurableEntry receipt = Assert.Single(await repository.ListAsync("peer:a:processSchedules", timeout.Token));
        string? boot = receipt.Value!.Json.GetProperty("bootId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(boot));
        Assert.NotEqual("previous-isolated-boot", boot);
        await peer.StopHostOnlyAsync(timeout.Token);
        using Process process = Process.GetProcessById(pid);
        Assert.False(process.HasExited);
        if (exitWhileOffline)
        {
            app.Release();
            await process.WaitForExitAsync(timeout.Token);
        }
        await ReadyAsync(peer, timeout.Token);
        ApplicationView after = Assert.Single(await peer.GetAsync<ApplicationView[]>("processes.list", timeout.Token));
        Assert.Equal(pid, after.Observed.Status!.Identity!.ProcessId);
        Assert.Equal(!exitWhileOffline, after.Observed.Status.Running);
        Assert.Null(Assert.Single(after.Schedules).NextAt);
        DurableEntry afterReceipt = Assert.Single(await repository.ListAsync("peer:a:processSchedules", timeout.Token));
        Assert.True(string.Equals(receipt.Version, afterReceipt.Version, StringComparison.Ordinal),
            JsonSerializer.Serialize(new { before = receipt, after = afterReceipt, starts = app.Starts() }));
        Assert.Single(app.Starts());
    }

    /// <summary>クラスタ初期化の完了後に次の停止・起動へ進みます。</summary>
    /// <returns>実Coreの運用準備完了です。</returns>
    private static async Task ReadyAsync(CorePeer peer, CancellationToken cancellationToken = default)
    {
        await peer.StartAsync(cancellationToken).ConfigureAwait(false);
        await CorePeer.WaitAsync(async token => (await peer.GetAsync<JsonElement>("status", token).ConfigureAwait(false)).GetProperty("status").GetString() is "ready"
            && (await peer.GetAsync<ClusterOperationalState>("configuration.read", token).ConfigureAwait(false)).Effective is not null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>画面と同じ公開管理応答を比較保存版・定義とともに読みます。</summary>
    private readonly record struct ApplicationView(string Version, ProcessDefinition Definition, ProcessOperationResult Observed, IReadOnlyList<ProcessScheduleStatus> Schedules);
}
