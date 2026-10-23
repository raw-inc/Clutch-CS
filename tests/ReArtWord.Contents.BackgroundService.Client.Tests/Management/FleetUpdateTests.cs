using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Fleet;
using System.Runtime.InteropServices;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates;
using ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>host順序、残存ピア、再試行と中断時の副作用境界を検証します。</summary>
public sealed class FleetUpdateTests
{
    /// <summary>全体結果が進んでも、変わらない対象結果の通知IDを変えません。</summary>
    [Fact]
    public void HookNotificationPreservesOperationAndAttemptWithoutPaths()
    {
        FleetUpdateStatus initial = Status([Target("a", "one"), Target("b", "one")]);
        Guid attempt = Guid.NewGuid();
        UpdatePeerResult result = new(attempt, "a", EUpdatePhase.Completed, EOutcome.Succeeded, "new", BackupPath: "PRIVATE-BACKUP", PayloadPath: "PRIVATE-PAYLOAD");
        FleetUpdatePeerProgress peer = initial.Peers[0] with { State = "Completed", Attempts = [new(attempt, result, initial.Peers[0].Target.Peer.InstanceId)] };
        initial = initial with { Peers = [peer, initial.Peers[1]] };
        UpdateNotification first = Assert.Single(FleetUpdateNotifications.Capture(initial, "one"), notification => string.Equals(notification.Name, "update.peerChanged", StringComparison.Ordinal));
        Assert.Equal(initial.Request.OperationId, first.Data.OperationId);
        Assert.Equal(attempt, first.Data.AttemptOperationId);
        Assert.Equal("one", first.Data.ClusterId);
        Assert.DoesNotContain("PRIVATE-", System.Text.Json.JsonSerializer.Serialize(first), StringComparison.Ordinal);
        FleetUpdateStatus completed = initial with { Completed = true, Outcome = EOutcome.Succeeded, UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1) };
        IReadOnlyList<UpdateNotification> notifications = FleetUpdateNotifications.Capture(completed, "one");
        Assert.Contains(notifications, notification => notification.Id == first.Id);
        Assert.Contains(notifications, notification => string.Equals(notification.Name, "update.completed", StringComparison.Ordinal));
    }

    /// <summary>同じPCの複数クラスタを先に処理し、復帰確認前に別PCを止めません。</summary>
    /// <returns>保存順序と更新順序の検証です。</returns>
    [Fact]
    public async Task HostsAreUpdatedTogetherAndEveryPeerReturnsBeforeTheNextDispatch()
    {
        FleetUpdateStatus input = Status([Target("a", "one"), Target("a", "two"), Target("b", "one"), Target("b", "two")]);
        input = input with { Request = input.Request with { Release = input.Request.Release with { MaximumParallelism = 1 } } };
        FleetUpdateTestRemote remote = new(input.Request.Targets);
        FleetUpdateStatus? saved = null;
        remote.BeforeDispatch = () =>
        {
            Assert.Contains(saved!.Peers, peer => string.Equals(peer.State, "Updating", StringComparison.Ordinal));
            Assert.All(remote.Observations.Values, peer => Assert.True(peer.Ready));
        };
        FleetUpdateStatus result = await new FleetUpdateRunner(remote).RunAsync(input, (value, _) =>
        {
            saved = value;
            return Task.CompletedTask;
        });
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        string[] expected = ["dispatch:a/one", "dispatch:a/two", "dispatch:b/one", "dispatch:b/two"];
        Assert.Equal(expected, remote.Events.Where(value => value.StartsWith("dispatch:", StringComparison.Ordinal)));
        Assert.All(result.Peers, peer => Assert.Single(peer.Attempts));
        Assert.Equal(4, remote.Disposals);
    }

    /// <summary>一台だけ所属するクラスタでは一時切断を許容します。</summary>
    /// <returns>全体停止保留を行わない更新完了です。</returns>
    [Fact]
    public async Task SingleHostClusterCanUpdate()
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets);
        FleetUpdateStatus result = await RunAsync(input, remote);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Single(remote.Events, value => value.StartsWith("dispatch:", StringComparison.Ordinal));
    }

    /// <summary>複数host構成の残り一台を誤って停止しません。</summary>
    /// <returns>Readyを失った相手がある場合の保留です。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRemainingHostDefersDispatchIncludingLossDuringStaging(bool duringStaging)
    {
        FleetUpdateStatus input = Status([Target("a", "one"), Target("b", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets);
        void LosePeer() => remote.Observations[("one", "b")] = remote.Observations[("one", "b")] with { Ready = false };
        if (duringStaging)
        {
            remote.BeforeStage = _ => LosePeer();
        }
        else
        {
            LosePeer();
        }
        FleetUpdateStatus result = await RunAsync(input, remote);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.All(result.Peers, peer => Assert.Equal("Deferred", peer.State));
        Assert.DoesNotContain(remote.Events, value => value.StartsWith("dispatch:", StringComparison.Ordinal));
    }

    /// <summary>停止前の転送失敗だけを最大回数まで試し、失敗した試行のIDも残します。</summary>
    /// <returns>三回目の準備成功と一度だけの停止です。</returns>
    [Theory]
    [InlineData(2, EOutcome.Succeeded, 1)]
    [InlineData(3, EOutcome.Failed, 0)]
    public async Task PreparationRetriesAreBounded(int failures, EOutcome expected, int dispatches)
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets) { StageFailures = failures };
        FleetUpdateStatus result = await RunAsync(input, remote);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(3, Assert.Single(result.Peers).Attempts.Count);
        Assert.Equal(3, result.Peers[0].Attempts.Select(attempt => attempt.OperationId).Distinct().Count());
        Assert.Equal(dispatches, remote.Events.Count(value => value.StartsWith("dispatch:", StringComparison.Ordinal)));
        Assert.Equal(3, remote.Disposals);
        Assert.All(result.Peers[0].Attempts.Take(failures), attempt =>
        {
            Assert.StartsWith("stage: IOException.", attempt.Problem, StringComparison.Ordinal);
            Assert.DoesNotContain("Transfer failed.", attempt.Problem, StringComparison.Ordinal);
        });
    }

    /// <summary>準備が後で成功しても、以前の操作名と未確認状態を保持します。</summary>
    /// <returns>一回だけの更新と、失敗した試行の診断です。</returns>
    [Fact]
    public async Task SuccessfulRetryRetainsTheOriginalProtocolFailure()
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets) { StageFailures = 1, StageProtocolFailure = true };
        FleetUpdateStatus result = await RunAsync(input, remote);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal("stage: seal.Unknown", result.Peers[0].Attempts[0].Problem);
        Assert.Null(result.Peers[0].Attempts[1].Problem);
        Assert.Single(remote.Events, value => value.StartsWith("dispatch:", StringComparison.Ordinal));
    }

    /// <summary>旧版復帰を確認できた場合だけ、新しい実体を固定して次の試行へ進みます。</summary>
    /// <returns>復元後の再試行成功です。</returns>
    [Theory]
    [InlineData(EUpdatePhase.RolledBack)]
    [InlineData(EUpdatePhase.Failed)]
    public async Task ConfirmedRollbackCanBeRetried(EUpdatePhase restoredPhase)
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets)
        {
            DispatchPhase = attempt => attempt switch { 1 => restoredPhase, _ => EUpdatePhase.Completed },
            FailureReason = "update.failedOldReleaseRestored"
        };
        FleetUpdateStatus result = await RunAsync(input, remote);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, result.Peers[0].Attempts.Count);
        Assert.NotEqual(result.Peers[0].Attempts[0].PreviousInstanceId, result.Peers[0].Attempts[1].PreviousInstanceId);
        Assert.Equal(2, remote.Events.Count(value => value.StartsWith("release:", StringComparison.Ordinal)));
    }

    /// <summary>結果不明時は全体を止め、再確認は更新を再送せず元結果だけを使用します。</summary>
    /// <returns>応答欠落後の確認と未着手hostの保持です。</returns>
    [Fact]
    public async Task UnknownDispatchStopsTheFleetAndReconciliationNeverDispatches()
    {
        FleetUpdateStatus input = Status([Target("a", "one"), Target("b", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets) { DispatchPhase = _ => EUpdatePhase.Interrupted };
        FleetUpdateStatus result = await RunAsync(input, remote);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Empty(result.Peers[1].Attempts);
        Guid operation = result.Peers[0].Attempts[0].OperationId;
        remote.Results[operation] = remote.Results[operation] with { Phase = EUpdatePhase.Completed, Outcome = EOutcome.Succeeded };
        FleetUpdateStatus reconciled = await new FleetUpdateRecovery(remote).ReconcileAsync(result);
        Assert.Equal(EOutcome.Failed, reconciled.Outcome);
        Assert.Equal("Completed", reconciled.Peers[0].State);
        Assert.Equal("Deferred", reconciled.Peers[1].State);
        Assert.Single(remote.Events, value => value.StartsWith("dispatch:", StringComparison.Ordinal));
    }

    /// <summary>復元後の別実体へ固定した再試行も、元試行の記録から確認します。</summary>
    /// <returns>復元後の試行実体を使用した結果確認です。</returns>
    [Fact]
    public async Task ReconciliationUsesTheInstancePinnedAfterRollback()
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets)
        {
            DispatchPhase = attempt => attempt switch { 1 => EUpdatePhase.RolledBack, _ => EUpdatePhase.Interrupted }
        };
        FleetUpdateStatus result = await RunAsync(input, remote);
        FleetUpdateAttempt attempt = result.Peers[0].Attempts[^1];
        remote.Results[attempt.OperationId] = new(attempt.OperationId, "only", EUpdatePhase.Failed, EOutcome.NotExecuted, Reason: "update.drainNotCompleted");
        FleetPeerObservation current = remote.Observations[("one", "only")];
        remote.Observations[("one", "only")] = current with { Peer = current.Peer with { InstanceId = attempt.PreviousInstanceId!.Value }, BuildVersion = "old" };
        FleetUpdateStatus reconciled = await new FleetUpdateRecovery(remote).ReconcileAsync(result);
        Assert.Equal(EOutcome.Failed, reconciled.Outcome);
        Assert.Equal("Failed", reconciled.Peers[0].State);
        Assert.Equal(2, remote.Events.Count(value => value.StartsWith("dispatch:", StringComparison.Ordinal)));
    }

    /// <summary>自動復帰の結果は新しい実体が確定した場合だけ元操作の保留を解除します。</summary>
    /// <returns>確認済み復帰と不明結果の副作用境界です。</returns>
    [Theory]
    [InlineData(EOutcome.Failed, "update.failedOldReleaseRestored", "new", true)]
    [InlineData(EOutcome.Unknown, "update.failedOldReleaseRestored", "new", false)]
    [InlineData(EOutcome.Failed, "update.rollbackHealthUnconfirmed", "new", false)]
    [InlineData(EOutcome.Failed, "update.failedOldReleaseRestored", "same", false)]
    [InlineData(EOutcome.Failed, "update.failedOldReleaseRestored", "empty", false)]
    [InlineData(EOutcome.Failed, "update.failedOldReleaseRestored", "missing", false)]
    public async Task ReconciliationOnlyReleasesConfirmedAutomaticRestoration(EOutcome outcome, string reason, string identity, bool confirmed)
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTarget target = input.Request.Targets[0];
        FleetUpdateTestRemote remote = new(input.Request.Targets);
        Guid operation = Guid.NewGuid();
        Guid? instance = identity switch
        {
            "same" => target.Peer.InstanceId,
            "empty" => Guid.Empty,
            "missing" => null,
            _ => Guid.NewGuid()
        };
        remote.Results[operation] = new(operation, "only", EUpdatePhase.Failed, outcome, "old", InstanceId: instance, Reason: reason);
        remote.Observations[("one", "only")] = new(target.Peer with { InstanceId = instance ?? Guid.NewGuid() }, false, "old");
        input = input with { Completed = true, Peers = [input.Peers[0] with
        {
            State = "Unconfirmed", Attempts = [new(operation, PreviousInstanceId: target.Peer.InstanceId)]
        }] };
        FleetUpdateStatus result = await new FleetUpdateRecovery(remote).ReconcileAsync(input);
        Assert.Equal(confirmed ? EOutcome.Failed
                               : EOutcome.Unknown, result.Outcome);
        Assert.Equal(confirmed ? "Failed"
                               : "Unconfirmed", result.Peers[0].State);
        Assert.Equal(operation, result.Peers[0].Attempts[0].Result!.OperationId);
        if (confirmed)
        {
            Assert.Equal("release:only/one", Assert.Single(remote.Events));
        }
        else
        {
            Assert.Empty(remote.Events);
        }
    }

    /// <summary>進捗保存の失敗より後へ停止・入替を進めません。</summary>
    /// <returns>更新開始前の保存失敗の境界です。</returns>
    [Fact]
    public async Task FailedJournalWriteCannotStartAnUpdater()
    {
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateTestRemote remote = new(input.Request.Targets);
        await Assert.ThrowsAsync<IOException>(() => new FleetUpdateRunner(remote).RunAsync(input, (_, _) => throw new IOException("Disk failure.")));
        Assert.Empty(remote.Events);
    }

    /// <summary>Host再起動後は固定入力と試行IDを維持し、副作用を再送しません。</summary>
    /// <returns>永続化と中断復元の確認です。</returns>
    [Fact]
    public async Task StoreRecoversInterruptedOperationsWithoutLosingTheirIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-fleet-store-" + Guid.NewGuid().ToString("N"));
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateStore store = new(directory);
        FleetUpdateAttempt attempt = new(Guid.NewGuid(), PreviousInstanceId: input.Request.Targets[0].Peer.InstanceId, Problem: "stage: seal.Unknown");
        input = input with { Peers = [input.Peers[0] with { Attempts = [attempt] }] };
        await store.SaveAsync(input);
        await new FleetUpdateStore(directory).RecoverAsync();
        FleetUpdateStatus result = (await store.ReadAsync(input.Request.OperationId))!;
        Assert.True(result.Completed);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal(input.Request.OperationId, result.Request.OperationId);
        Assert.Equal(input.Request.Targets[0], Assert.Single(result.Request.Targets));
        Assert.Equal(attempt, Assert.Single(result.Peers[0].Attempts));
        Assert.Single(await store.ListAsync());
    }

    /// <summary>予定したClientHost再起動では、生存中helperを中断扱いせず後続の確定結果を復元します。</summary>
    /// <returns>同じCore試行を保持した引渡しと結果の確認です。</returns>
    [Fact]
    public async Task StoreFollowsIndependentClientUpdaterAcrossRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-client-store-" + Guid.NewGuid().ToString("N"));
        FleetUpdateStatus input = Status([Target("only", "one")]);
        FleetUpdateStore store = new(directory);
        Contracts.Processes.ProcessIdentity identity = Infrastructure.Updates.Client.ClientUpdateFiles.Health("test").Process;
        input = input with { Client = new("Updating", false, EOutcome.Unknown, identity, DateTimeOffset.UtcNow) };
        await store.SaveAsync(input);
        ClientUpdateJob job = new(input.Request.OperationId, input.Request.Release.Manifest, directory + "-installed", directory,
            new Uri("http://127.0.0.1:5081/"), identity);
        Infrastructure.Updates.Client.ClientUpdateFiles files = new(job);
        Directory.CreateDirectory(files.Operation);
        await files.SaveAsync("Starting", false, EOutcome.Unknown);
        await store.RecoverAsync();
        FleetUpdateStatus running = (await store.ReadAsync(input.Request.OperationId))!;
        Assert.False(running.Completed);
        Assert.Equal("Starting", running.Client!.State);
        await files.SaveAsync("Completed", true, EOutcome.Succeeded);
        FleetUpdateStatus result = (await new FleetUpdateStore(directory).ReadAsync(input.Request.OperationId))!;
        Assert.True(result.Completed);
        Assert.Equal(EOutcome.Succeeded, result.Outcome);
        Assert.Equal(Contracts.Serialization.ContractJson.Hash(input.Peers), Contracts.Serialization.ContractJson.Hash(result.Peers));
    }

    /// <summary>helper起動前の中断と受付後の記録消失は、再実行せず結果未確認として復元します。</summary>
    /// <returns>失われたhelperを永久実行中や成功にしない確認です。</returns>
    [Theory]
    [InlineData("Launching")]
    [InlineData("Updating")]
    public async Task StoreDoesNotAssumeSuccessWhenClientHandoffIsMissing(string state)
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-client-store-" + Guid.NewGuid().ToString("N"));
        FleetUpdateStatus input = Status([Target("only", "one")]) with
        {
            Client = new(state, false, EOutcome.Unknown, null, DateTimeOffset.UtcNow)
        };
        FleetUpdateStore store = new(directory);
        await store.SaveAsync(input);
        await store.RecoverAsync();
        FleetUpdateStatus result = (await store.ReadAsync(input.Request.OperationId))!;
        Assert.True(result.Completed);
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal(input.Request.OperationId, result.Request.OperationId);
    }

    /// <summary>既存Updaterに渡す固定要求を構成します。</summary>
    /// <returns>試験の開始状態です。</returns>
    private static FleetUpdateStatus Status(FleetUpdateTarget[] targets)
    {
        ReleaseManifest manifest = new("new", RuntimeInformation.RuntimeIdentifier, "app", [new("app", 0, new string('0', 64))], ["old"], "new");
        FleetUpdateRequest request = new(Guid.NewGuid(), new(manifest, Path.GetTempPath(), true, PeerTimeoutMilliseconds: 1000), targets);
        return new(request, false, EOutcome.Unknown, targets.Select(target => new FleetUpdatePeerProgress(target, "Queued", [])).ToArray(), DateTimeOffset.UtcNow);
    }

    /// <summary>異なるhostとクラスタを作ります。</summary>
    /// <returns>開始時の固定対象です。</returns>
    private static FleetUpdateTarget Target(string host, string cluster) => new(cluster,
        new(host, Guid.NewGuid(), host, "127.0.0.1", 1234, EPeerStatus.Ready, 0, null, DateTimeOffset.UtcNow),
        new("loopback", "127.0.0.1", "127.255.255.255", 1234));

    /// <summary>副作用を持たない保存先で制御を実行します。</summary>
    /// <returns>全体結果です。</returns>
    private static Task<FleetUpdateStatus> RunAsync(FleetUpdateStatus input, IFleetUpdateRemote remote, CancellationToken cancellationToken = default)
        => new FleetUpdateRunner(remote).RunAsync(input, (_, _) => Task.CompletedTask, cancellationToken);
}
