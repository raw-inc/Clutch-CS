using System.Globalization;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Tests.Shared;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;

/// <summary>起動しなかった場合と起動成功時の復旧回数を、実プロセスと保存状態で確認します。</summary>
public sealed class ApplicationRecoveryAttemptTests
{
    /// <summary>正常・異常終了後も起動成功で回数を戻し、最初の遅延と引数は維持します。</summary>
    /// <returns>成功した再起動を累積する旧仕様に代わる実プロセスの確認です。</returns>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(23, false)]
    [InlineData(23, true)]
    public async Task SuccessfulSelfExitsRespectDelayWithoutAccruingAttemptsAsync(int exitCode, bool arguments)
    {
        using ApplicationProcessFixture app = new(exitCode, withArguments: arguments);
        using ProcessTestScope scope = new();
        LogTestClock clock = new();
        using ProcessService service = new(scope.Repository, "peer", scope.Options, clock);
        ProcessDefinition definition = app.Definition with { Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 1000) };
        await service.RegisterAsync(definition);
        ProcessOperationResult current = await service.StartAsync(definition.Id);
        for (int index = 0; index < 3; index++)
        {
            ProcessOperationResult stopped = await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
            Assert.Equal(exitCode, stopped.Status!.ExitCode);
            Assert.Equal(0, stopped.Status.RecoveryAttempts);
            Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
            clock.Advance(TimeSpan.FromMilliseconds(999));
            Assert.Equal("process.recoveryDelay", (await service.RecoverAsync(definition.Id)).Reason);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            ProcessOperationResult next = await service.RecoverAsync(definition.Id);
            scope.Track(next);
            Assert.True(next.Outcome == EOutcome.Succeeded, System.Text.Json.JsonSerializer.Serialize(next));
            Assert.NotEqual(current.Status!.InstanceId, next.Status!.InstanceId);
            Assert.Equal(0, next.Status.RecoveryAttempts);
            Assert.False(next.Status.RecoveryExhausted);
            current = next;
        }
        await ApplicationRecoveryTests.WaitForExitAsync(service, definition.Id);
        Assert.Equal(4, app.Starts().Length);
        Assert.All(app.Starts(), entry => Assert.Equal(definition.Arguments, entry.Skip(2)));
    }

    /// <summary>起動ユーザーを利用できない間は、不調なアプリを再起動目的で停止しません。</summary>
    /// <returns>未ログインの事前確認でプロセスと回数を維持する確認です。</returns>
    [Fact]
    public async Task MissingLaunchUserDoesNotStopARunningApplicationAsync()
    {
        using ApplicationBootFixture fixture = new();
        ProcessDefinition definition = fixture.Definition with { Recovery = new(RecoverWhenUnhealthy: true, UnhealthyStopPolicy: EProcessStopPolicy.Force, DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult started = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await fixture.Service.SignalAsync(definition.Id, new(started.Status!.InstanceId, true, false));
        fixture.UserAvailable = false;
        ProcessOperationResult unavailable = await fixture.Service.RecoverAsync(definition.Id);
        Assert.Equal("process.interactiveUserUnavailable", unavailable.Reason);
        Assert.True(unavailable.Status!.Running);
        Assert.Equal(started.Status.Identity, unavailable.Status.Identity);
        Assert.Equal(0, unavailable.Status.RecoveryAttempts);
        Assert.Equal(1, fixture.StartAttempts);
    }

    /// <summary>実行物の欠落と起動前の拒否では、自動復旧の回数を消費しません。</summary>
    /// <returns>繰り返し確認しても未実行を試行済みと扱わない確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedLaunchDoesNotConsumeRecoveryAttemptsAsync(bool missingExecutable)
    {
        using ApplicationBootFixture fixture = new();
        ProcessDefinition definition = fixture.Definition with { Recovery = new(MaximumAttempts: 2, DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult started = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        string unavailable = definition.ExecutablePath + ".unavailable";
        if (missingExecutable)
        {
            File.Move(definition.ExecutablePath, unavailable);
        }
        try
        {
            for (int index = 0; index < 3; index++)
            {
                fixture.RejectNextStart = !missingExecutable;
                ProcessOperationResult result = await fixture.Service.RecoverAsync(definition.Id);
                Assert.Equal(EOutcome.NotExecuted, result.Outcome);
                Assert.Equal(missingExecutable ? "process.executableUnavailable"
                                               : "process.interactiveUserUnavailable", result.Reason);
                Assert.Equal(0, result.Status!.RecoveryAttempts);
                Assert.False(result.Status.RecoveryExhausted);
                Assert.Single(fixture.App.Starts());
            }
        }
        finally
        {
            if (missingExecutable)
            {
                File.Move(unavailable, definition.ExecutablePath);
            }
        }
        ProcessOperationResult recovered = await fixture.Service.RecoverAsync(definition.Id);
        fixture.Storage.Track(recovered);
        Assert.Equal(EOutcome.Succeeded, recovered.Outcome);
        await fixture.WaitForStartsAsync(2);
        Assert.Equal(0, recovered.Status!.RecoveryAttempts);
    }

    /// <summary>自動復旧も起動成功で回数をリセットし、次の終了を新しい起動回として扱います。</summary>
    /// <returns>起動成功を挟んだ終了で過去の試行が累積しない確認です。</returns>
    [Fact]
    public async Task EachSuccessfulAutomaticStartResetsRecoveryAttemptsAsync()
    {
        using ApplicationBootFixture fixture = new();
        ProcessDefinition definition = fixture.Definition with { Recovery = new(MaximumAttempts: 1, DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult current = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        for (int index = 0; index < 3; index++)
        {
            await ProcessTestScope.CrashAsync(current.Status!.Identity!);
            await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
            current = await fixture.Service.RecoverAsync(definition.Id);
            fixture.Storage.Track(current);
            Assert.Equal(EOutcome.Succeeded, current.Outcome);
            await fixture.WaitForStartsAsync(index + 2);
            Assert.Equal(0, current.Status!.RecoveryAttempts);
            Assert.False(current.Status.RecoveryExhausted);
        }
    }

    /// <summary>結果不明の起動は再送せず、未起動の確認後に起動が成功すると回数を戻します。</summary>
    /// <returns>Core再起動を越える未確定状態と、その後の明示起動の確認です。</returns>
    [Fact]
    public async Task UnconfirmedLaunchKeepsItsAttemptUntilAConfirmedStartAsync()
    {
        using ApplicationBootFixture fixture = new();
        ProcessDefinition definition = fixture.Definition with { Recovery = new(MaximumAttempts: 3, DelayMilliseconds: 0) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult started = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        fixture.UnknownStart = true;
        ProcessOperationResult unknown = await fixture.Service.RecoverAsync(definition.Id);
        Assert.Equal(EOutcome.Unknown, unknown.Outcome);
        Assert.Equal(1, unknown.Status!.RecoveryAttempts);
        fixture.RestartCore();
        for (int index = 0; index < 3; index++)
        {
            ProcessOperationResult blocked = await fixture.Service.RecoverAsync(definition.Id);
            Assert.Equal(EOutcome.NotExecuted, blocked.Outcome);
            Assert.Equal(1, blocked.Status!.RecoveryAttempts);
        }
        Assert.Equal(2, fixture.StartAttempts);
        fixture.UnknownStart = false;
        await fixture.Service.ResolveLaunchAsync(definition.Id, null);
        ProcessOperationResult confirmed = await fixture.Service.StartAsync(definition.Id);
        fixture.Storage.Track(confirmed);
        Assert.Equal(EOutcome.Succeeded, confirmed.Outcome);
        await fixture.WaitForStartsAsync(2);
        Assert.Equal(0, confirmed.Status!.RecoveryAttempts);
        Assert.False(confirmed.Status.RecoveryExhausted);
    }

    /// <summary>保存済みの上限到達状態も、手動または予定による実起動の成功で解除します。</summary>
    /// <returns>起動済みの照会では消さず、新しい起動でだけ回数を戻す確認です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRestartClearsPersistedAttemptsAsync(bool scheduled)
    {
        using ApplicationBootFixture fixture = new();
        ProcessSchedule schedule = new("once", "once", At: fixture.Clock.GetUtcNow().AddSeconds(1).ToString("O", CultureInfo.InvariantCulture), AlreadyRunning: EProcessRunningPolicy.Restart,
            StopPolicy: EProcessStopPolicy.Force, StopTimeoutMilliseconds: 1000);
        await fixture.RegisterAsync(schedule);
        await fixture.Automation.TickAsync(true);
        ProcessOperationResult started = await fixture.Service.StartAsync(fixture.Definition.Id);
        await fixture.WaitForStartsAsync(1);
        await SeedAttemptsAsync(fixture.Storage, fixture.Definition.Id);
        ProcessOperationResult repeated = await fixture.Service.StartAsync(fixture.Definition.Id);
        Assert.Equal(started.Status!.Identity, repeated.Status!.Identity);
        Assert.Equal(3, repeated.Status.RecoveryAttempts);
        if (scheduled)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            await fixture.Automation.TickAsync(true);
        }
        else
        {
            Assert.Equal(EOutcome.Succeeded, (await fixture.Service.RestartAsync(fixture.Definition.Id, EProcessStopPolicy.Force, 1000)).Outcome);
        }
        await fixture.WaitForStartsAsync(2);
        ProcessOperationResult observed = await fixture.Service.ObserveAsync(fixture.Definition.Id);
        Assert.NotEqual(started.Status.Identity, observed.Status!.Identity);
        Assert.Equal(0, observed.Status.RecoveryAttempts);
        Assert.False(observed.Status.RecoveryExhausted);
    }

    /// <summary>実体を確認して結果不明の起動を解決した場合も、成功済みの試行を残しません。</summary>
    /// <returns>保存済みの未確定状態から起動成功を確定する確認です。</returns>
    [Fact]
    public async Task ConfirmedLaunchIdentityClearsPendingAttemptsAsync()
    {
        using ApplicationBootFixture fixture = new();
        await fixture.Service.RegisterAsync(fixture.Definition);
        ProcessOperationResult started = await fixture.Service.StartAsync(fixture.Definition.Id);
        await fixture.WaitForStartsAsync(1);
        await SeedAttemptsAsync(fixture.Storage, fixture.Definition.Id, launchPending: true);
        Assert.Equal(EOutcome.Unknown, (await fixture.Service.ObserveAsync(fixture.Definition.Id)).Outcome);
        ProcessOperationResult resolved = await fixture.Service.ResolveLaunchAsync(fixture.Definition.Id, started.Status!.Identity);
        Assert.Equal(EOutcome.Succeeded, resolved.Outcome);
        Assert.True(resolved.Status!.Running);
        Assert.Equal(0, resolved.Status.RecoveryAttempts);
        Assert.False(resolved.Status.RecoveryExhausted);
        Assert.Equal(1, fixture.StartAttempts);
    }

    /// <summary>起動済みの実体に、従来版から引き継ぐ試行履歴を保存します。</summary>
    /// <returns>本番と同じ保存形式から状態を再取得するための準備です。</returns>
    internal static async Task SeedAttemptsAsync(ProcessTestScope storage, string id, bool launchPending = false, CancellationToken cancellationToken = default)
    {
        string scope = "peer:peer:processStates";
        DurableEntry? stored = await storage.Repository.ReadAsync(scope, id, cancellationToken).ConfigureAwait(false);
        JsonObject state = JsonNode.Parse(stored!.Value!.Json.GetRawText())!.AsObject();
        state["recoveryAttempts"] = 3;
        state["recoveryExhausted"] = true;
        if (launchPending)
        {
            state["launchPending"] = true;
            state["identity"] = null;
        }
        await storage.Repository.WriteLocalSnapshotAsync(scope, id, NodeValue.FromObject(state), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>実際のOS起動要求が失敗した場合は回数を保持し、遅延と上限を適用します。</summary>
    /// <returns>未実行の事前確認と失敗した起動試行を区別する確認です。</returns>
    [Fact]
    public async Task RejectedOsAttemptsKeepDelayAndLimitAcrossCoreRestartAsync()
    {
        using ApplicationBootFixture fixture = new();
        ProcessDefinition definition = fixture.Definition with { Recovery = new(MaximumAttempts: 2, DelayMilliseconds: 1000) };
        await fixture.Service.RegisterAsync(definition);
        ProcessOperationResult started = await fixture.Service.StartAsync(definition.Id);
        await fixture.WaitForStartsAsync(1);
        await ProcessTestScope.CrashAsync(started.Status!.Identity!);
        await ApplicationRecoveryTests.WaitForExitAsync(fixture.Service, definition.Id);
        fixture.RejectAfterLaunchAttempt = true;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Assert.Equal("process.recoveryDelay", (await fixture.Service.RecoverAsync(definition.Id)).Reason);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            fixture.RejectNextStart = true;
            ProcessOperationResult rejected = await fixture.Service.RecoverAsync(definition.Id);
            Assert.Equal(EOutcome.NotExecuted, rejected.Outcome);
            Assert.Equal("process.interactiveLaunchRejected", rejected.Reason);
            Assert.Equal(attempt, rejected.Status!.RecoveryAttempts);
            Assert.False(rejected.Status.RecoveryExhausted);
        }
        fixture.RestartCore();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        ProcessOperationResult exhausted = await fixture.Service.RecoverAsync(definition.Id);
        Assert.Equal("process.recoveryExhausted", exhausted.Reason);
        Assert.Equal(2, exhausted.Status!.RecoveryAttempts);
        Assert.True(exhausted.Status.RecoveryExhausted);
        Assert.Equal(3, fixture.StartAttempts);
        Assert.Single(fixture.App.Starts());
    }
}
