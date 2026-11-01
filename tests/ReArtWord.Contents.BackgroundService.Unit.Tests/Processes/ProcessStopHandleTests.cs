using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Media;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>停止前のハンドルが、PID不在や終了コードの公開後も維持されることを確認します。</summary>
public sealed class ProcessStopHandleTests
{
    private const string ClientPath = "client";
    private const string MonitorPath = "monitor";

    /// <summary>強制終了直後の監視・通常停止・ClientHost更新で、資源解放前の成功を返しません。</summary>
    /// <param name="path">終了確認を行う製品の経路です。</param>
    /// <returns>終了シグナルと保持ファイルの解放の確認です。</returns>
    [Theory]
    [InlineData("stop")]
    [InlineData(MonitorPath)]
    [InlineData(ClientPath)]
    public async Task ExitConfirmationKeepsTheOriginalHandleAsync(string path)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        string configuration = Path.Combine(files.Root, "held.txt");
        using Process process = await ProcessFileFixture.StartAsync(configuration, cancellationToken: limit.Token).ConfigureAwait(true);
        ProcessIdentity identity = ProcessOs.Identity(process);
        using Process? captured = ProcessOs.Open(identity);
        Assert.NotNull(captured);
        using ProcessHandleMonitor monitor = new(new DefaultProcessPlatform());
        Assert.True(monitor.TryAdopt("held", identity));
        try
        {
            process.Kill();
            if (string.Equals(path, ClientPath, StringComparison.Ordinal))
            {
                Assert.True(await ClientUpdateExecutor.WaitForExitAsync(captured, 10000, limit.Token).ConfigureAwait(true));
            }
            else if (string.Equals(path, MonitorPath, StringComparison.Ordinal))
            {
                Assert.Equal(EOutcome.Succeeded, await monitor.StopAsync("held", identity, EProcessStopPolicy.WaitForManual, 10000, limit.Token).ConfigureAwait(true));
            }
            else
            {
                Assert.Equal(EOutcome.Succeeded, await ProcessOs.StopAsync(captured, identity, EProcessStopPolicy.WaitForManual, 10000,
                    new DefaultProcessPlatform(), limit.Token).ConfigureAwait(true));
            }
            AssertReleased(process, files.Root);
        }
        finally
        {
            await ProcessFileFixture.StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>Installerが停止要求を公開する前に、CoreとWebClientの実体を確保します。</summary>
    /// <param name="client">同じ実行物を列挙して停止するWebClientの経路を使うかです。</param>
    /// <returns>公開直後に終了しても、ファイル解放まで待つことの確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallerCapturesTheHandleBeforePublishingStopAsync(bool client)
    {
        using MediaTestFiles files = new();
        using ApplicationProcessFixture application = new();
        File.Delete(Path.Combine(application.Root, "application-fixture.json"));
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        string configuration = Path.Combine(files.Root, "held.txt");
        using Process process = await ProcessFileFixture.StartAsync(configuration, application.Definition.ExecutablePath, limit.Token).ConfigureAwait(true);
        ProcessIdentity identity = ProcessOs.Identity(process);
        try
        {
            Task RequestStop(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                process.Kill();
                return Task.CompletedTask;
            }
            if (client)
            {
                await InstallationProcesses.StopExecutableAsync(application.Definition.ExecutablePath, RequestStop, limit.Token).ConfigureAwait(true);
            }
            else
            {
                InstallationSettings settings = new(files.Root, files.Root, "cluster", "host", "peer", 1000, true, true,
                    new("loopback", "127.0.0.1", "127.255.255.255", 47740), new(), true);
                InstallationRecord record = new(Guid.NewGuid(), settings, "fixture", "unused-core.exe", "unused-updater.exe", "unused-client.exe", DateTimeOffset.UtcNow);
                await new InstallationPlatform().StopAsync(record, identity, RequestStop, limit.Token).ConfigureAwait(true);
            }
            AssertReleased(process, files.Root);
        }
        finally
        {
            await ProcessFileFixture.StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>開始時刻が違う同じPIDへの停止を拒否し、待機の取消でも強制終了しません。</summary>
    /// <returns>所有と取消の境界の確認です。</returns>
    [Fact]
    public async Task WrongIdentityAndCancellationDoNotStopTheProcessAsync()
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        using Process process = await ProcessFileFixture.StartAsync(Path.Combine(files.Root, "held.txt"), cancellationToken: limit.Token).ConfigureAwait(true);
        ProcessIdentity identity = ProcessOs.Identity(process);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync().ConfigureAwait(true);
        try
        {
            ProcessIdentity wrong = identity with { StartedAt = identity.StartedAt.AddSeconds(1) };
            using Process? rejected = ProcessOs.Open(wrong);
            Assert.Null(rejected);
            Assert.Equal(EOutcome.Unknown, await ProcessOs.StopAsync(process, wrong, EProcessStopPolicy.Force, 10000, new DefaultProcessPlatform(), limit.Token).ConfigureAwait(true));
            Assert.Equal(EOutcome.Unknown, await ProcessOs.StopAsync(process, identity, EProcessStopPolicy.Force, 10000, new DefaultProcessPlatform(), cancelled.Token).ConfigureAwait(true));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientUpdateExecutor.WaitForExitAsync(process, 10000, cancelled.Token)).ConfigureAwait(true);
            Assert.False(ProcessCompletion.HasExited(process));
        }
        finally
        {
            await ProcessFileFixture.StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>試験の回収処理も、終了コードが公開された実体のハンドルを失いません。</summary>
    /// <param name="own">開始時のハンドルを引き取るか、稼働照会からハンドルを取得するかです。</param>
    /// <returns>回収完了後の資源解放の確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixtureCleanupKeepsObservedAndOwnedHandlesAsync(bool own)
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        using Process process = await ProcessFileFixture.StartAsync(Path.Combine(files.Root, "held.txt"), cancellationToken: limit.Token).ConfigureAwait(true);
        using TestProcessHandles handles = new();
        ProcessIdentity identity = ProcessOs.Identity(process);
        try
        {
            if (own)
            {
                handles.Own(Process.GetProcessById(process.Id));
            }
            else
            {
                handles.Capture(identity);
            }
            Assert.Equal(identity, Assert.Single(handles.Identities));
            process.Kill();
            handles.Stop();
            Assert.Empty(handles.Identities);
            AssertReleased(process, files.Root);
        }
        finally
        {
            await ProcessFileFixture.StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>OSの終了確認後は、繰り返しの停止や取消済みトークンで成功を失いません。</summary>
    /// <returns>停止操作の冪等性の確認です。</returns>
    [Fact]
    public async Task AlreadyCompletedProcessKeepsTheConfirmedResultAsync()
    {
        using MediaTestFiles files = new();
        using CancellationTokenSource limit = new(TimeSpan.FromSeconds(30));
        string configuration = Path.Combine(files.Root, "held.txt");
        using Process process = await ProcessFileFixture.StartAsync(configuration, cancellationToken: limit.Token).ConfigureAwait(true);
        ProcessIdentity identity = ProcessOs.Identity(process);
        try
        {
            await File.WriteAllTextAsync(configuration + ".release", string.Empty, limit.Token).ConfigureAwait(true);
            await ProcessCompletion.WaitAsync(process, limit.Token).ConfigureAwait(true);
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync().ConfigureAwait(true);
            Assert.Equal(EOutcome.Succeeded, await ProcessOs.StopAsync(identity, EProcessStopPolicy.Force, 10000, cancelled.Token).ConfigureAwait(true));
            Assert.Equal(EOutcome.Succeeded, await ProcessOs.StopAsync(process, identity, EProcessStopPolicy.Force, 10000, new DefaultProcessPlatform(), cancelled.Token).ConfigureAwait(true));
            Assert.True(await ClientUpdateExecutor.WaitForExitAsync(process, 10000, cancelled.Token).ConfigureAwait(true));
            AssertReleased(process, files.Root);
        }
        finally
        {
            await ProcessFileFixture.StopAsync(process).ConfigureAwait(true);
        }
    }

    /// <summary>実体の終了シグナルだけでなく、開いていた全ファイルを再取得します。</summary>
    private static void AssertReleased(Process process, string directory)
    {
        Assert.True(ProcessCompletion.HasExited(process));
        string[] paths = Directory.GetFiles(directory, "*.lock");
        Assert.NotEmpty(paths);
        foreach (string path in paths)
        {
            using FileStream reopened = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
