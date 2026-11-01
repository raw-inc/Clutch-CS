using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>試験用プロセスを別の実体と取り違えず、他の試験へ影響させないことを確認します。</summary>
public sealed class ProcessFixtureIsolationTests
{
    /// <summary>PIDが同じでも起動時刻が異なる実体を、試験の異常終了操作で停止しません。</summary>
    /// <returns>不一致の拒否と、正しい実体だけの終了確認です。</returns>
    [Fact]
    public async Task CrashRejectsAnIdentityFromAnotherProcessLifetimeAsync()
    {
        using ProcessTestScope scope = new();
        using ProcessService service = scope.CreateService();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(scope.Definition, cancellationToken: timeout.Token).ConfigureAwait(true));
        ProcessOperationResult started = await service.StartAsync(scope.Definition.Id, timeout.Token).ConfigureAwait(true);
        scope.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        ProcessIdentity identity = Assert.IsType<ProcessIdentity>(started.Status!.Identity);
        using Process process = Process.GetProcessById(identity.ProcessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessTestScope.CrashAsync(identity with { StartedAt = identity.StartedAt.AddTicks(1) }, timeout.Token)).ConfigureAwait(true);
        Assert.False(process.HasExited);
        await ProcessTestScope.CrashAsync(identity, timeout.Token).ConfigureAwait(true);
        Assert.True(process.HasExited);
    }

    /// <summary>登録の照合条件を試験ごとに分け、他の試験を後継候補へ含めません。</summary>
    /// <returns>別の保存先に属する実プロセスを採用しない照合結果です。</returns>
    [Fact]
    public async Task AnotherScopeDoesNotSupplyAReplacementAsync()
    {
        using ProcessTestScope first = new();
        using ProcessTestScope second = new();
        using ProcessService service = second.CreateService();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(EOutcome.Succeeded, await service.RegisterAsync(second.Definition, cancellationToken: timeout.Token).ConfigureAwait(true));
        ProcessOperationResult started = await service.StartAsync(second.Definition.Id, timeout.Token).ConfigureAwait(true);
        second.Track(started);
        Assert.Equal(EOutcome.Succeeded, started.Outcome);
        ProcessReplacementWindow window = new(DateTimeOffset.UtcNow.AddMinutes(1), notBefore, first.Definition.ExecutablePath,
            ContractJson.Hash(first.Definition.Arguments), EProcessRestartMatch.ExecutableAndArguments);
        (ProcessIdentity? identity, string? problem) = await new ProcessReplacementProbe().FindAsync(first.Definition.Id, window, timeout.Token).ConfigureAwait(true);
        Assert.Null(identity);
        Assert.Null(problem);
        Assert.True((await service.ObserveAsync(second.Definition.Id, timeout.Token).ConfigureAwait(true)).Status!.Running);
    }
}
