using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;
using Xunit;
using Xunit.Abstractions;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Execution;

/// <summary>稼働操作の受付順と、一時続行だけの寿命を検証します。</summary>
public sealed class ManualSafetyServiceTests(ITestOutputHelper output)
{
    /// <summary>受付処理が応答期限を超えても、元操作の照会で結果を確認します。</summary>
    /// <returns>初回の結果不明と、再実施されない一件の確定結果です。</returns>
    [Fact]
    public async Task DelayedAcceptanceIsConfirmedWithoutRepeatingTheChangeAsync()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(true);
        await fixture.StartAsync("a", true).ConfigureAwait(true);
        await fixture.StartAsync("b").ConfigureAwait(true);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]).ConfigureAwait(true);
        await fixture.WaitForStatesAsync("b", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]).ConfigureAwait(true);
        using ManualResetEventSlim release = new(false);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int checks = 0;
        fixture.Safety["a"].AutomaticStopCheck = (_, _) =>
        {
            Interlocked.Increment(ref checks);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
            return true;
        };
        fixture.Safety["b"].AutomaticStopCheck = (_, _) => true;
        ManualSafetyChange request = new(Guid.NewGuid(), "workflow", "node", false, Continued: true);
        Task<DurableReceipt> pending = fixture.Safety["b"].ChangeAsync(request, timeout.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            DurableReceipt initial = await pending.WaitAsync(timeout.Token).ConfigureAwait(true);
            output.WriteLine(JsonSerializer.Serialize(initial));
            Assert.Equal(EOutcome.Unknown, initial.Outcome);
            Assert.False(timeout.IsCancellationRequested);
            Assert.Empty(await fixture.Repositories["a"].ListAsync("execution:safety", timeout.Token).ConfigureAwait(true));
            release.Set();
            DurableReceipt confirmed = await fixture.ConfirmAsync("b", initial, timeout.Token).ConfigureAwait(true);
            Assert.True(confirmed.Outcome == EOutcome.Succeeded, JsonSerializer.Serialize(confirmed));
            Assert.Equal(request.OperationId, confirmed.OperationId);
            Assert.Equal(1, Volatile.Read(ref checks));
            Assert.Single(await fixture.Repositories["a"].ListAsync("execution:safety:operations", timeout.Token).ConfigureAwait(true));
            Assert.True(Assert.Single(fixture.Safety["b"].Current).Continued);
        }
        finally
        {
            release.Set();
            await pending.ConfigureAwait(true);
        }
    }

    /// <summary>全Peer停止後は一時続行を再適用せず、人為停止と元受付結果を保持します。</summary>
    /// <returns>単体クラスタ全停止の寿命検証です。</returns>
    [Fact]
    public async Task WholeClusterRestartDoesNotReactivateContinuation()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        ManualSafetyChange request = new(Guid.NewGuid(), "workflow", "node", false, Continued: true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", request)).Outcome);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Continued);
        await fixture.ChangeAndConfirmAsync("a", new(Guid.NewGuid(), "workflow", "manual", true));
        await fixture.StopAsync("a");
        await fixture.StartAsync("a");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready)]);
        await fixture.Safety["a"].RefreshAsync();
        ManualSafetyStatus continued = fixture.Safety["a"].Current.Single(item => string.Equals(item.Scope, "node", StringComparison.Ordinal));
        Assert.False(continued.Continued);
        Assert.True(continued.RequestedContinued);
        Assert.Equal("safety.continuationSurvivorUnconfirmed", continued.ContinuationReason);
        Assert.True(fixture.Safety["a"].Current.Single(item => string.Equals(item.Scope, "manual", StringComparison.Ordinal)).Stopped);
        Assert.Equal(EOutcome.Succeeded, (await fixture.Safety["a"].GetOperationAsync(request.OperationId))!.Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", request)).Outcome);
        Assert.False(fixture.Safety["a"].Current.Single(item => string.Equals(item.Scope, "node", StringComparison.Ordinal)).Continued);
    }

    /// <summary>配送記録だけ残った中断は操作記録照会に限定し、副作用本体を再送しません。</summary>
    /// <returns>開始不明要求の非再実行確認です。</returns>
    [Fact]
    public async Task MissingReceiptDoesNotResendOriginalMutation()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        ManualSafetyChange request = new(Guid.NewGuid(), "workflow", "node", true);
        ClusterRequest command = new(request.OperationId, "safety.change", "a", fixture.Peers["a"].LocalInstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(request));
        await fixture.Repositories["a"].ApplyBatchAsync(Guid.NewGuid(), [new DurableMutation("peer:a:safety-requests", request.OperationId.ToString("N"), null, NodeValue.FromObject(command))]);
        Assert.Equal(EOutcome.Unknown, (await fixture.Safety["a"].ChangeAsync(request)).Outcome);
        Assert.Empty(await fixture.Repositories["a"].ListAsync("execution:safety"));
        Assert.Empty(await fixture.Repositories["a"].ListAsync("receipts:a"));
        Assert.Equal(EOutcome.Unknown, (await fixture.Safety["a"].GetOperationAsync(request.OperationId))!.Outcome);
        Assert.Null(await fixture.Safety["a"].GetOperationAsync(Guid.NewGuid()));
    }

    /// <summary>別Peerの古い表示版を新操作の拒否に使わず、同ID再送では状態を戻しません。</summary>
    /// <returns>受付順と元結果の確認です。</returns>
    [Fact]
    public async Task NewOperationsUseLatestStateAndDuplicateKeepsOriginalResult()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await fixture.StartAsync("a", true);
        await fixture.StartAsync("b");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]);
        ManualSafetyChange stop = new(Guid.NewGuid(), "workflow", "node", true);
        DurableReceipt first = await fixture.ChangeAndConfirmAsync("b", stop);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        ManualSafetyChange resume = new(Guid.NewGuid(), "workflow", "node", false, "stale-ui-version", Continued: true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", resume)).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("b", stop)).Outcome);
        Assert.False(Assert.Single(fixture.Safety["b"].Current).Stopped);
        Assert.False(Assert.Single(fixture.Safety["b"].Current).Continued);
        Assert.Equal(resume.OperationId, Assert.Single(fixture.Safety["b"].Current).OperationId);
        Assert.Equal(EOutcome.Failed, (await fixture.Safety["b"].ChangeAsync(stop with { Scope = "different" })).Outcome);
        ManualSafetyChange one = new(Guid.NewGuid(), "workflow", "node", true, "old");
        ManualSafetyChange two = new(Guid.NewGuid(), "workflow", "node", false, "old");
        DurableReceipt[] accepted = await Task.WhenAll(fixture.ChangeAndConfirmAsync("a", one), fixture.ChangeAndConfirmAsync("b", two));
        Assert.All(accepted, result => Assert.Equal(EOutcome.Succeeded, result.Outcome));
        DurableReceipt last = accepted.MaxBy(receipt => receipt.Entries[0].Value!.Json.GetProperty("Acceptance").GetProperty("Sequence").GetInt64())!;
        await fixture.Safety["a"].RefreshAsync();
        Assert.Equal(last.OperationId, Assert.Single(fixture.Safety["a"].Current).OperationId);
    }

    /// <summary>部分再起動では元の実体が残る間だけ続行し、全元の実体入替後は人為停止だけを維持します。</summary>
    /// <returns>未知条件・部分再起動・全入替の区別です。</returns>
    [Fact]
    public async Task ContinuationNeedsAnOriginalSurvivorAndNeverRestoresAfterAllInstancesChange()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await StartDegradedAsync(fixture);
        ManualSafetyChange continued = new(Guid.NewGuid(), "workflow", "node", false, Continued: true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", continued)).Outcome);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Continued);
        await fixture.ChangeAndConfirmAsync("a", new(Guid.NewGuid(), "workflow", "manual", true));
        await fixture.StopAsync("b");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Unavailable), ("c", EPeerStatus.Unavailable)]);
        await fixture.StartAsync("b", policyKnown: false);
        await fixture.WaitForStatesAsync("b", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready), ("c", EPeerStatus.Unavailable)]);
        await fixture.Safety["b"].RefreshAsync();
        ManualSafetyStatus unknown = fixture.Safety["b"].Current.Single(item => string.Equals(item.Scope, "node", StringComparison.Ordinal));
        Assert.False(unknown.Continued);
        Assert.Equal("safety.automaticStateUnknown", unknown.ContinuationReason);
        NodeSafetyGate gate = new(fixture.Peers["b"]);
        fixture.Safety["b"].AutomaticStopCheck = (workflow, scope) => gate.IsAutomaticallyStopped(ManualSafetyFixture.Node);
        await fixture.Safety["b"].RefreshAsync();
        Assert.True(fixture.Safety["b"].Current.Single(item => string.Equals(item.Scope, "node", StringComparison.Ordinal)).Continued);
        await fixture.StopAsync("a");
        await fixture.WaitForStatesAsync("b", [("a", EPeerStatus.Unavailable), ("b", EPeerStatus.Ready), ("c", EPeerStatus.Unavailable)]);
        await fixture.StartAsync("a");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready)]);
        await fixture.Safety["a"].RefreshAsync();
        Assert.False(fixture.Safety["a"].Current.Single(item => string.Equals(item.Scope, "node", StringComparison.Ordinal)).Continued);
        Assert.True(fixture.Safety["a"].Current.Single(item => string.Equals(item.Scope, "manual", StringComparison.Ordinal)).Stopped);
    }

    /// <summary>元障害の回復で失効し、新障害の新しい続行を古い失効で消しません。</summary>
    /// <returns>元操作だけへの失効記録の確認です。</returns>
    [Fact]
    public async Task RecoveryEndsOnlyOriginalContinuationAndLaterSameScopeOperationWins()
    {
        ManualSafetyFixture fixture = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = fixture.ConfigureAwait(false);
        await StartDegradedAsync(fixture);
        ManualSafetyChange old = new(Guid.NewGuid(), "workflow", "node", false, Continued: true);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", old)).Outcome);
        await fixture.StartAsync("c");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready), ("c", EPeerStatus.Ready)]);
        Assert.False(Assert.Single(fixture.Safety["a"].Current).Continued);
        await fixture.StopAsync("c");
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready), ("c", EPeerStatus.Unavailable)]);
        Assert.False(Assert.Single(fixture.Safety["a"].Current).Continued);
        ManualSafetyChange renewed = old with { OperationId = Guid.NewGuid() };
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", renewed)).Outcome);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Continued);
        Assert.NotNull(await fixture.Repositories["a"].ReadAsync("execution:safety-expiry", old.OperationId.ToString("N")));
        Assert.Null(await fixture.Repositories["a"].ReadAsync("execution:safety-expiry", renewed.OperationId.ToString("N")));
        await fixture.ChangeAndConfirmAsync("b", new(Guid.NewGuid(), "workflow", "node", true));
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", renewed with { OperationId = Guid.NewGuid() })).Outcome);
        Assert.False(Assert.Single(fixture.Safety["a"].Current).Stopped);
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Continued);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("b", new(Guid.NewGuid(), "workflow", "node", true))).Outcome);
        await fixture.Safety["a"].RefreshAsync();
        Assert.True(Assert.Single(fixture.Safety["a"].Current).Stopped);
        Assert.False(Assert.Single(fixture.Safety["a"].Current).Continued);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", new(Guid.NewGuid(), "workflow", "node", false))).Outcome);
        Assert.Equal(EOutcome.Succeeded, (await fixture.ChangeAndConfirmAsync("a", renewed with { OperationId = Guid.NewGuid() })).Outcome);
    }

    /// <summary>二つの生存Peerと一つの実際に失われたPeerを作ります。</summary>
    /// <returns>縮退観測の完了です。</returns>
    private static async Task StartDegradedAsync(ManualSafetyFixture fixture)
    {
        await fixture.StartAsync("a", true).ConfigureAwait(false);
        await fixture.StartAsync("b").ConfigureAwait(false);
        await fixture.StartAsync("c").ConfigureAwait(false);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready), ("c", EPeerStatus.Ready)]).ConfigureAwait(false);
        await fixture.StopAsync("c").ConfigureAwait(false);
        await fixture.WaitForStatesAsync("a", [("a", EPeerStatus.Ready), ("b", EPeerStatus.Ready), ("c", EPeerStatus.Unavailable)]).ConfigureAwait(false);
    }
}
