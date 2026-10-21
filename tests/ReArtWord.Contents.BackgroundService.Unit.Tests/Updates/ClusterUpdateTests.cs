using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Events;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Updates;

/// <summary>全体更新の補償対象と不明結果の非再実行を確認します。</summary>
public sealed class ClusterUpdateTests
{
    /// <summary>通知が増えても共有更新履歴の祖先を失わず、途中状態から再同期できます。</summary>
    /// <returns>実repository間の進捗取込みと通知のローカル保持です。</returns>
    [Fact]
    public async Task UpdateNotificationJournalDoesNotCreateSharedHistoryConflictsAsync()
    {
        using UpdateTestScope scope = new();
        await File.WriteAllBytesAsync(Path.Combine(scope.Root, "app.exe"), []).ConfigureAwait(true);
        ReleaseManifest manifest = new("new", "win-x64", "app.exe", [new("app.exe", 0, Convert.ToHexString(SHA256.HashData([])))], ["old"]);
        ClusterUpdateRequest request = new(Guid.NewGuid(), manifest, scope.Root, ["a", "b"], EUpdateFailurePolicy.KeepCompatibleMixed);
        JsonFileRepository source = new(Path.Combine(scope.Root, "source.json"));
        JsonFileRepository replica = new(Path.Combine(scope.Root, "replica.json"));
        FakePeers transport = new(false)
        {
            BeforeDispatch = async token => Assert.True(await replica.ImportAsync(await source.ExportAsync(token).ConfigureAwait(true), token).ConfigureAwait(true)),
        };
        await new ClusterUpdateCoordinator(source, transport, "owner").ExecuteAsync(request).ConfigureAwait(true);
        RepositorySnapshot exported = await source.ExportAsync().ConfigureAwait(true);
        Assert.True(await replica.ImportAsync(exported).ConfigureAwait(true));
        Assert.Empty(await replica.GetConflictsAsync("updates").ConfigureAwait(true));
        Assert.NotEmpty(await source.ListAsync("peer:owner:update-events").ConfigureAwait(true));
        Assert.Empty(await replica.ListAsync("peer:owner:update-events").ConfigureAwait(true));
    }

    /// <summary>独立更新の保存済み進行をHookへ渡し、照会・再起動で再発行しません。</summary>
    /// <returns>更新失敗と通知再送境界の確認です。</returns>
    [Fact]
    public async Task UpdateHooksPreserveOperationResultsWithoutReplayingAsync()
    {
        using UpdateTestScope scope = new();
        await File.WriteAllBytesAsync(Path.Combine(scope.Root, "app.exe"), []).ConfigureAwait(true);
        ReleaseManifest manifest = new("new", "win-x64", "app.exe", [new("app.exe", 0, Convert.ToHexString(SHA256.HashData([])))], ["old"]);
        ClusterUpdateRequest request = new(Guid.NewGuid(), manifest, scope.Root, ["a", "b"], EUpdateFailurePolicy.KeepCompatibleMixed);
        JsonFileRepository repository = new(Path.Combine(scope.Root, "repository.json"));
        FakePeers transport = new(false);
        ClusterUpdateCoordinator coordinator = new(repository, transport, "owner");
        ClusterUpdateStatus result = await coordinator.ExecuteAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        WorkflowEventHub events = new("owner", Guid.NewGuid());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        IAsyncEnumerator<WorkflowEvent> reader = events.SubscribeAsync(timeout.Token).GetAsyncEnumerator();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable readerLifetime = reader.ConfigureAwait(true);
        Task<bool> first = reader.MoveNextAsync().AsTask();
        UpdateEventPump pump = new(repository, events, "owner");
        await pump.DrainAsync(timeout.Token).ConfigureAwait(true);
        Assert.True(await first.ConfigureAwait(true));
        List<WorkflowEvent> observed = [reader.Current];
        while (observed.Count < events.Statistics.Published)
        {
            Assert.True(await reader.MoveNextAsync().ConfigureAwait(true));
            observed.Add(reader.Current);
        }
        Assert.Equal("update.started", observed[0].Name);
        Assert.Contains(observed, item => string.Equals(item.Name, "update.peerChanged", StringComparison.Ordinal));
        WorkflowEvent failed = Assert.Single(observed, item => string.Equals(item.Name, "update.failed", StringComparison.Ordinal));
        Assert.Equal(EOutcome.Failed, failed.Data.Json.Deserialize<UpdateEventData>(ContractJson.Options)!.Outcome);
        Assert.All(observed, item => Assert.Equal(request.OperationId, item.CausationId));
        _ = await coordinator.ExecuteAsync(request, timeout.Token).ConfigureAwait(true);
        await new UpdateEventPump(repository, events, "owner").DrainAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(observed.Count, events.Statistics.Published);
        Assert.Equal(0, pump.Pending);
    }

    /// <summary>部分失敗後は指定した方針だけを適用し再照会で再実行しません。</summary>
    /// <returns>検証が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData(EUpdateFailurePolicy.KeepCompatibleMixed, false, EUpdateRollbackFailurePolicy.ExcludeFailed)]
    [InlineData(EUpdateFailurePolicy.RollbackSuccessful, false, EUpdateRollbackFailurePolicy.ExcludeFailed)]
    [InlineData(EUpdateFailurePolicy.RollbackSuccessful, true, EUpdateRollbackFailurePolicy.StopCluster)]
    public async Task PartialFailureAppliesOnlyExplicitCompensation(EUpdateFailurePolicy policy, bool rollbackFails, EUpdateRollbackFailurePolicy failurePolicy)
    {
        using UpdateTestScope scope = new();
        await File.WriteAllBytesAsync(Path.Combine(scope.Root, "app.exe"), []).ConfigureAwait(true);
        ReleaseManifest manifest = new("new", "win-x64", "app.exe", [new("app.exe", 0, Convert.ToHexString(SHA256.HashData([])))], ["old"]);
        ClusterUpdateRequest request = new(Guid.NewGuid(), manifest, scope.Root, ["a", "b", "c"], policy, failurePolicy);
        FakePeers transport = new(rollbackFails);
        ClusterUpdateCoordinator coordinator = new(new JsonFileRepository(Path.Combine(scope.Root, "repository.json")), transport);
        ClusterUpdateStatus result = await coordinator.ExecuteAsync(request).ConfigureAwait(true);
        Assert.Equal(EOutcome.Failed, result.Outcome);
        Assert.True(result.Completed);
        if (policy == EUpdateFailurePolicy.RollbackSuccessful)
        {
            Assert.Equal("a,b", string.Join(",", transport.Dispatched));
            Assert.Equal("a", string.Join(",", transport.RolledBack));
        }
        else
        {
            Assert.Equal("a,b,c", string.Join(",", transport.Dispatched));
            Assert.Empty(transport.RolledBack);
        }
        int expectedHolds = 1;
        if (rollbackFails)
        {
            expectedHolds = 2;
        }
        Assert.Equal(expectedHolds, transport.HoldCount);
        int before = transport.Dispatched.Count;
        ClusterUpdateStatus replay = await coordinator.ExecuteAsync(request).ConfigureAwait(true);
        Assert.Equal(result.Outcome, replay.Outcome);
        Assert.Equal(before, transport.Dispatched.Count);
        ClusterUpdateStatus mismatch = await coordinator.ExecuteAsync(request with { PeerIds = ["a"] }).ConfigureAwait(true);
        Assert.Equal(EOutcome.NotExecuted, mismatch.Outcome);
    }

    /// <summary>異RIDの全選択とチェックサムを確認する前に転送や停止を始めません。</summary>
    /// <returns>異RID選択と不足・破損時の事前拒否を確認します。</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MixedPlatformsAreSelectedBeforeAnyStage(bool missing, bool corrupted)
    {
        using UpdateTestScope scope = new();
        string windows = Path.Combine(scope.Root, "windows");
        string linux = Path.Combine(scope.Root, "linux");
        Directory.CreateDirectory(windows);
        Directory.CreateDirectory(linux);
        await File.WriteAllBytesAsync(Path.Combine(windows, "app.exe"), [1]).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(linux, "app"), [2]).ConfigureAwait(true);
        ReleaseManifest win = new("new", "win-x64", "app.exe", [new("app.exe", 1, Convert.ToHexString(SHA256.HashData([1])))], ["old"]);
        ReleaseManifest lin = new("new", "linux-arm64", "app", [new("app", 1, Convert.ToHexString(SHA256.HashData([2])))], ["old"]);
        List<ReleaseVariant> variants = [new(win, "windows")];
        if (!missing)
        {
            variants.Add(new(lin, "linux"));
        }
        if (corrupted)
        {
            await File.WriteAllBytesAsync(Path.Combine(linux, "app"), [3]).ConfigureAwait(true);
        }
        ClusterUpdateRequest request = new(Guid.NewGuid(), win, scope.Root, ["a", "b"], EUpdateFailurePolicy.KeepCompatibleMixed, Variants: variants);
        FakePeers transport = new(false);
        transport.Platforms.Add("b", "linux-arm64");
        ClusterUpdateStatus result = await new ClusterUpdateCoordinator(new JsonFileRepository(Path.Combine(scope.Root, "repository.json")), transport).ExecuteAsync(request).ConfigureAwait(true);
        if (missing || corrupted)
        {
            Assert.Equal(EOutcome.NotExecuted, result.Outcome);
            Assert.Empty(transport.Staged);
            Assert.Empty(transport.Dispatched);
            Assert.Equal(0, transport.HoldCount);
        }
        else
        {
            Assert.Equal("win-x64,linux-arm64", string.Join(",", transport.Staged.Select(value => value.Manifest.Platform)));
            Assert.Equal(windows, transport.Staged[0].PayloadRoot);
            Assert.Equal(linux, transport.Staged[1].PayloadRoot);
            Assert.All(transport.Staged, value => Assert.Null(value.Variants));
        }
    }

    /// <summary>異なるリリースや互換集合を同じbundleへ紛れ込ませません。</summary>
    [Fact]
    public void VariantPolicyMustRemainCommon()
    {
        ReleaseManifest manifest = new("new", "win-x64", "app", [new("app", 0, new string('0', 64))], ["old"]);
        ClusterUpdateRequest request = new(Guid.NewGuid(), manifest, Path.GetTempPath(), ["a"], EUpdateFailurePolicy.KeepCompatibleMixed);
        Assert.Throws<InvalidDataException>(() => ReleaseSelector.Validate(request with { Variants = [new(manifest with { ReleaseId = "other" }, "linux")] }));
        Assert.Throws<InvalidDataException>(() => ReleaseSelector.Validate(request with { Variants = [new(manifest with { CompatibleReleaseIds = [] }, "linux")] }));
        Assert.Throws<InvalidDataException>(() => ReleaseSelector.Validate(request with { Variants = [new(manifest, "../escape")] }));
        Assert.Throws<InvalidDataException>(() => ReleaseSelector.Validate(request with { Variants = [new(manifest, "one"), new(manifest, "two")] }));
    }

    /// <summary>外部状態だけをモデル化して再送や補償対象を観測します。</summary>
    private sealed class FakePeers(bool rollbackFails) : IUpdatePeerTransport
    {
        /// <summary>実peer停止に相当する時点の同期を試験します。</summary>
        public Func<CancellationToken, Task>? BeforeDispatch
        {
            get; init;
        }

        public Dictionary<string, string> Platforms
        {
            get;
        } = new(StringComparer.Ordinal);

        public List<ClusterUpdateRequest> Staged
        {
            get;
        } = [];

        public List<string> Dispatched
        {
            get;
        } = [];

        public List<string> RolledBack
        {
            get;
        } = [];

        public int HoldCount
        {
            get; private set;
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public Task<UpdatePeerResult> StageAsync(ClusterUpdateRequest request, string peerId, CancellationToken cancellationToken = default)
        {
            Staged.Add(request);
            return Task.FromResult(new UpdatePeerResult(request.OperationId, peerId, EUpdatePhase.Prepared, EOutcome.Succeeded, "old"));
        }

        /// <summary>対象ごとの異なる実RIDを模擬します。</summary>
        /// <returns>対象Peerに割り当てたRIDです。</returns>
        public Task<string> GetPlatformAsync(string peerId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Platforms.GetValueOrDefault(peerId, "win-x64"));
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public async Task<UpdatePeerResult> DispatchAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            if (BeforeDispatch is not null)
            {
                await BeforeDispatch(cancellationToken).ConfigureAwait(true);
            }
            Dispatched.Add(peerId);
            string failed = "b";
            if (string.Equals(peerId, failed, StringComparison.Ordinal))
            {
                return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Failed, EOutcome.Failed, "old", InstanceId: Guid.NewGuid());
            }
            return new UpdatePeerResult(operationId, peerId, EUpdatePhase.Completed, EOutcome.Succeeded, "new", InstanceId: Guid.NewGuid());
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public Task<UpdatePeerResult> ReadAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Terminal results must not be polled.");
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public Task<UpdatePeerResult> RollbackAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            RolledBack.Add(peerId);
            EOutcome outcome = EOutcome.Succeeded;
            if (rollbackFails)
            {
                outcome = EOutcome.Unknown;
            }
            return Task.FromResult(new UpdatePeerResult(Guid.NewGuid(), peerId, EUpdatePhase.RolledBack, outcome, "old", InstanceId: Guid.NewGuid()));
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public Task ExcludeAsync(string peerId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public Task StopClusterAsync(Guid operationId, IReadOnlyList<string> peerIds, CancellationToken cancellationToken = default)
        {
            HoldCount++;
            return Task.CompletedTask;
        }

        /// <summary>明示した試験応答と操作記録を返します。</summary>
        /// <returns>試験で固定した操作結果です。</returns>
        public Task ReleaseAsync(Guid operationId, string peerId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
