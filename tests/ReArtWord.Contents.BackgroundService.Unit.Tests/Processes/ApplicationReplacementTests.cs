using System.Diagnostics;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>外部の監督プロセス相当から新PIDを起動し、自律再起動の引継ぎを実アプリで確認します。</summary>
public sealed class ApplicationReplacementTests
{
    /// <summary>設定に応じて引数一致を要求し、Coreが追加起動せず新PIDへ監視を移します。</summary>
    /// <returns>引用符・空引数・変更された引数の照合です。</returns>
    [Theory]
    [InlineData(EProcessRestartMatch.ExecutableAndArguments, false, false)]
    [InlineData(EProcessRestartMatch.ExecutableAndArguments, false, true)]
    [InlineData(EProcessRestartMatch.ExecutableOnly, true, true)]
    public async Task UniqueSuccessorIsAdoptedWithoutAnotherCoreStartAsync(EProcessRestartMatch match, bool changed, bool arguments)
    {
        using ApplicationProcessFixture app = new(exitCode: 17, withArguments: arguments);
        using ProcessTestScope scope = new();
        WorkflowEventHub events = new();
        RecordingWorkflowLog log = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock, events: events, log: log);
        ProcessDefinition definition = app.Definition with { RestartDetection = new(10000, match), Recovery = new(DelayMilliseconds: 0) };
        await service.RegisterAsync(definition);
        ProcessOperationResult initial = await service.StartAsync(definition.Id);
        ProcessOperationResult waiting = await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        Assert.Null(waiting.Status!.ExitedAt);
        Assert.NotNull(waiting.Status.ReplacementDeadline);
        Assert.Equal(0, waiting.Status.FailureCount);
        Assert.Equal(1, events.Statistics.Published);
        Assert.DoesNotContain(log.Entries, entry => entry.Message is "Application.Stopped" or "Application.Restarted");
        await ApplicationRecoveryAttemptTests.SeedAttemptsAsync(scope, definition.Id);
        using Process successor = StartSuccessor(app, changed);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        ProcessOperationResult observed = await WaitAsync(service, definition.Id,
            value => value.Status?.Identity?.ProcessId == successor.Id && app.Starts().Length == 2, timeout.Token);
        Assert.True(observed.Status!.Running);
        Assert.Null(observed.Status.ReplacementDeadline);
        Assert.NotEqual(initial.Status!.InstanceId, observed.Status.InstanceId);
        Assert.Equal(0, observed.Status.FailureCount);
        Assert.Equal(0, observed.Status.RecoveryAttempts);
        Assert.False(observed.Status.RecoveryExhausted);
        Assert.Equal(2, events.Statistics.Published);
        Assert.Equal(successor.Id, (await service.RecoverAsync(definition.Id)).Status!.Identity!.ProcessId);
        Assert.Equal(2, app.Starts().Length);
        WorkflowLogEntry restarted = Assert.Single(log.Entries, entry => entry.Message is "Application.Restarted");
        Assert.Equal("INFO", restarted.Level);
        Assert.Equal(successor.Id, restarted.Data!.Json.GetProperty("pid").GetInt32());
    }

    /// <summary>厳密照合では引数が異なる実体を引き継がず、確認期限後だけ終了を確定します。</summary>
    /// <returns>終了確定とCore復旧までの遅延を分離した確認です。</returns>
    [Fact]
    public async Task ChangedArgumentsDoNotMatchStrictModeAndTimeoutConfirmsExitAsync()
    {
        using ApplicationProcessFixture app = new(exitCode: 9, withArguments: true);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessDefinition definition = app.Definition with { RestartDetection = new(1500), Recovery = new(DelayMilliseconds: 500) };
        await service.RegisterAsync(definition);
        ProcessOperationResult original = await service.StartAsync(definition.Id);
        ProcessOperationResult waiting = await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        using Process unrelated = StartSuccessor(app, true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        while (app.Starts().Length != 2)
        {
            await Task.Delay(10, timeout.Token);
        }
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        ProcessOperationResult confirming = await service.ObserveAsync(definition.Id, timeout.Token);
        Assert.Null(confirming.Status!.ExitedAt);
        Assert.Equal(original.Status!.Identity, confirming.Status.Identity);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        ProcessOperationResult stopped = await service.ObserveAsync(definition.Id, timeout.Token);
        Assert.Equal(original.Status!.Identity, stopped.Status!.Identity);
        Assert.Equal(waiting.Status!.ReplacementDeadline, stopped.Status.ExitedAt);
        Assert.Equal(9, stopped.Status.ExitCode);
        Assert.Equal(1, stopped.Status.FailureCount);
        Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
        Assert.Equal(stopped.Status.ExitedAt!.Value.AddMilliseconds(500), stopped.Status.NextRecoveryAt);
        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        ProcessOperationResult recovered = await service.RecoverAsync(definition.Id);
        scope.Track(recovered);
        Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
        Assert.NotEqual(unrelated.Id, recovered.Status!.Identity!.ProcessId);
    }

    /// <summary>候補が複数なら確認期限後も勝手に選ばず、自動復旧による第三の起動を抑止します。</summary>
    /// <returns>重複候補に対する安全な保留です。</returns>
    [Fact]
    public async Task AmbiguousSuccessorsBlockRecoveryInsteadOfStartingAgainAsync()
    {
        using ApplicationProcessFixture app = new(exitCode: 3);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessDefinition definition = app.Definition with { RestartDetection = new(500, EProcessRestartMatch.ExecutableOnly), Recovery = new(DelayMilliseconds: 0) };
        await service.RegisterAsync(definition);
        await service.StartAsync(definition.Id);
        await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        using Process one = StartSuccessor(app, false);
        using Process two = StartSuccessor(app, true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (app.Starts().Length != 3)
        {
            await Task.Delay(10, timeout.Token);
        }
        ProcessOperationResult observed = await WaitAsync(service, definition.Id, value => value.Status?.ReplacementProblem is "process.replacementAmbiguous", timeout.Token);
        Assert.Equal("process.replacementAmbiguous", observed.Status!.ReplacementProblem);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        ProcessOperationResult recovery = await service.RecoverAsync(definition.Id);
        Assert.NotEqual(EOutcome.Succeeded, recovery.Outcome);
        Assert.Equal(0, recovery.Status!.RecoveryAttempts);
        Assert.Null(recovery.Status.ExitedAt);
        Assert.Equal(3, app.Starts().Length);
    }

    /// <summary>後継のない確認待ちが期限で終了し、既定0では追加待機を行いません。</summary>
    /// <returns>任意設定の後方互換と待ち時間です。</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(250)]
    public async Task MissingSuccessorConfirmsExitAfterConfiguredWindowAsync(int milliseconds)
    {
        using ApplicationProcessFixture app = new();
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        await service.RegisterAsync(app.Definition with { RestartDetection = new(milliseconds, EProcessRestartMatch.ExecutableOnly) });
        await service.StartAsync(app.Definition.Id);
        ProcessOperationResult first = await ApplicationRecoveryTests.WaitForExitAsync(service, app.Definition.Id);
        if (milliseconds == 0)
        {
            Assert.NotNull(first.Status!.ExitedAt);
        }
        else
        {
            Assert.Null(first.Status!.ExitedAt);
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds - 1));
            Assert.Null((await service.ObserveAsync(app.Definition.Id)).Status!.ExitedAt);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            ProcessOperationResult final = await service.ObserveAsync(app.Definition.Id);
            Assert.Equal(first.Status.ReplacementDeadline, final.Status!.ExitedAt);
        }
    }

    /// <summary>Coreを経由しない監督プロセスからの起動を再現します。</summary>
    /// <returns>試験所有者が終了させる後継プロセスです。</returns>
    internal static Process StartSuccessor(ApplicationProcessFixture app, bool changed)
    {
        File.WriteAllText(Path.Combine(app.Root, "application-fixture.json"), "{\"exitCode\":0,\"delayMs\":-1}");
        ProcessStartInfo start = new(app.Definition.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = app.Root };
        foreach (string argument in app.Definition.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (changed)
        {
            start.ArgumentList.Add("--different-generation");
        }
        return Process.Start(start) ?? throw new IOException("試験用の後継を起動できません。");
    }

    /// <summary>固定sleepだけで成立したと見なさず、有限の実観測で状態を確認します。</summary>
    /// <returns>条件を満たした現在状態です。</returns>
    internal static async Task<ProcessOperationResult> WaitAsync(ProcessService service, string id, Func<ProcessOperationResult, bool> expected, CancellationToken cancellationToken = default)
    {
        ProcessOperationResult result = await service.ObserveAsync(id, cancellationToken).ConfigureAwait(false);
        while (!expected(result))
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            result = await service.ObserveAsync(id, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }
}
