using System.Globalization;
using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>実Coreの自動復旧が、起動成功で回数を戻しながら設定した遅延を守ることを確認します。</summary>
public sealed class ApplicationRecoveryProcessTests
{
    /// <summary>実Coreが復旧待機中に現れた後継へ監視を移し、元の期限に追加起動しません。</summary>
    /// <returns>0msの終了確認と15秒の復旧待ちを通した実通信・実プロセスの確認です。</returns>
    [Fact]
    public async Task SuccessorDuringRecoveryDelayPreventsAnotherCoreLaunchAsync()
    {
        using ApplicationProcessFixture app = new(exitCode: 17, delayMilliseconds: 100);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        await ApplicationLifecycleProcessTests.ReadyAsync(peer, deadline.Token);
        ProcessDefinition definition = app.Definition with { RestartDetection = new(0), Recovery = new(DelayMilliseconds: 15000) };
        await ApplicationLifecycleProcessTests.RegisterAsync(peer, definition, deadline.Token);
        Assert.Equal(EOutcome.Succeeded, (await ApplicationLifecycleProcessTests.ActionAsync(peer, "process.start", definition.Id, deadline.Token)).Outcome);
        ProcessStatus? stopped = null;
        await CorePeer.WaitAsync(async token =>
        {
            stopped = Assert.Single(await peer.GetAsync<ApplicationSnapshot[]>("processes.list", token).ConfigureAwait(false)).Observed.Status;
            return stopped?.ExitedAt is not null;
        }, deadline.Token);
        DateTimeOffset originalDue = stopped!.NextRecoveryAt!.Value;
        await File.WriteAllTextAsync(Path.Combine(app.Root, "application-fixture.json"), "{\"exitCode\":0,\"delayMs\":-1}", deadline.Token);
        using Process successor = Process.Start(new ProcessStartInfo(definition.ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = app.Root,
        })!;
        await CorePeer.WaitAsync(async token =>
        {
            ProcessStatus? current = Assert.Single(await peer.GetAsync<ApplicationSnapshot[]>("processes.list", token).ConfigureAwait(false)).Observed.Status;
            return current?.Identity?.ProcessId == successor.Id && current.Running == true && current.NextRecoveryAt is null;
        }, deadline.Token);
        TimeSpan remaining = originalDue - DateTimeOffset.UtcNow;
        if (TimeSpan.Zero < remaining)
        {
            await Task.Delay(remaining, deadline.Token);
        }
        ApplicationSnapshot final = Assert.Single(await peer.GetAsync<ApplicationSnapshot[]>("processes.list", deadline.Token));
        Assert.Equal(successor.Id, final.Observed.Status!.Identity!.ProcessId);
        Assert.True(final.Observed.Status.Running);
        Assert.Equal(2, app.Starts().Length);
    }

    /// <summary>正常・異常終了を繰り返しても成功済みの試行を累積せず、明示停止では復旧を終えます。</summary>
    /// <returns>実通信・実起動・起動引数・遅延と停止方針の確認です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(17, false)]
    [InlineData(17, true)]
    public async Task SuccessfulRecoveryResetsAttemptsAndPreservesDelayAsync(int exitCode, bool arguments)
    {
        using ApplicationProcessFixture app = new(exitCode, 100, arguments);
        CorePeer peer = new(Guid.NewGuid().ToString("N"), "a", CorePeer.AvailablePort(), true, redirectErrors: false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(90));
        await ApplicationLifecycleProcessTests.ReadyAsync(peer, deadline.Token);
        ProcessDefinition definition = app.Definition with { Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 750) };
        await ApplicationLifecycleProcessTests.RegisterAsync(peer, definition, deadline.Token);
        Assert.Equal(EOutcome.Succeeded, (await ApplicationLifecycleProcessTests.ActionAsync(peer, "process.start", definition.Id, deadline.Token)).Outcome);
        await CorePeer.WaitAsync(_ => Task.FromResult(4 <= app.Starts().Length), deadline.Token);
        Assert.Equal(EOutcome.Succeeded, (await ApplicationLifecycleProcessTests.ActionAsync(peer, "process.stop", definition.Id, deadline.Token)).Outcome);
        ApplicationSnapshot application = Assert.Single(await peer.GetAsync<ApplicationSnapshot[]>("processes.list", deadline.Token));
        Assert.False(application.Observed.Status!.Running);
        Assert.True(application.Observed.Status.PlannedStop);
        Assert.Equal(0, application.Observed.Status.RecoveryAttempts);
        Assert.False(application.Observed.Status.RecoveryExhausted);
        string[][] starts = app.Starts().OrderBy(entry => entry[1], StringComparer.Ordinal).ToArray();
        Assert.True(4 <= starts.Length);
        Assert.All(starts, entry => Assert.Equal(definition.Arguments, entry.Skip(2)));
        for (int index = 1; index < starts.Length; index++)
        {
            TimeSpan delay = DateTimeOffset.Parse(starts[index][1], CultureInfo.InvariantCulture) - DateTimeOffset.Parse(starts[index - 1][1], CultureInfo.InvariantCulture);
            Assert.True(TimeSpan.FromMilliseconds(750) <= delay, delay.ToString());
        }
        await peer.StopHostOnlyAsync(deadline.Token);
        await ApplicationLifecycleProcessTests.ReadyAsync(peer, deadline.Token);
        ApplicationSnapshot restored = Assert.Single(await peer.GetAsync<ApplicationSnapshot[]>("processes.list", deadline.Token));
        Assert.True(restored.Observed.Status!.PlannedStop);
        Assert.Equal(0, restored.Observed.Status.RecoveryAttempts);
        Assert.Equal(starts.Length, app.Starts().Length);
    }

    /// <summary>管理APIから受け取る状態の確認に必要な項目です。</summary>
    private readonly record struct ApplicationSnapshot(string Version, ProcessDefinition Definition, ProcessOperationResult Observed, IReadOnlyList<ProcessScheduleStatus> Schedules);
}
