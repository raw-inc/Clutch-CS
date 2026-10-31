using System.Runtime.CompilerServices;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>現在値だけの一致で履歴欠損を隠さず、同じ照合を再実行しないことを確認します。</summary>
public sealed class SyncInventoryTests
{
    /// <summary>格納順序によらず同じ状態を照合し、同一本文の転送を省きます。</summary>
    [Fact]
    public void MatchingHistoryProducesAnEmptyDifference()
    {
        RepositorySnapshot first = Snapshot();
        RepositorySnapshot reordered = first with { Entries = first.Entries.Reverse().ToArray(), Receipts = first.Receipts.Reverse().ToArray() };
        string[] before = SyncInventory.Describe(first);
        string[] after = SyncInventory.Describe(reordered);
        Assert.Equal(before, after);
        RepositorySnapshot difference = SyncInventory.Difference(first, before, after);
        Assert.Empty(difference.Entries);
        Assert.Empty(difference.Receipts);
    }

    /// <summary>現在値が同じでも、欠けた操作結果を差分として配送します。</summary>
    [Fact]
    public void MatchingCurrentValuesStillTransferMissingReceipts()
    {
        RepositorySnapshot complete = Snapshot();
        RepositorySnapshot missing = complete with { Receipts = [] };
        RepositorySnapshot difference = SyncInventory.Difference(complete, SyncInventory.Describe(complete), SyncInventory.Describe(missing));
        Assert.Equal(complete.Receipts, difference.Receipts);
    }

    /// <summary>内容が変わった同じ操作IDを、一致した結果として省略しません。</summary>
    [Fact]
    public void ChangedReceiptContentsAreNotTreatedAsKnown()
    {
        RepositorySnapshot complete = Snapshot();
        RepositorySnapshot changed = complete with { Receipts = [complete.Receipts[0] with { Outcome = EOutcome.Failed }] };
        RepositorySnapshot difference = SyncInventory.Difference(complete, SyncInventory.Describe(complete), SyncInventory.Describe(changed));
        Assert.Contains(complete.Receipts[0], difference.Receipts);
    }

    /// <summary>同じUDP要求の再送は同じ差分を返し、明示解放後も再作成しません。</summary>
    /// <returns>実UDPで観測した一回性と解放境界です。</returns>
    [Fact]
    public async Task RepeatedInventoryOwnsOneImmutableOfferAsync()
    {
        SyncRecoveryPeer peer = new(Snapshot());
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (peer.Target.GetPeers().Count < 2)
        {
            await peer.AnnounceAsync("different", EPeerStatus.Syncing, timeout.Token).ConfigureAwait(true);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        SyncInventoryRequest request = new(Guid.NewGuid(), SyncInventory.Describe(Snapshot()), DateTimeOffset.UtcNow.AddSeconds(20));
        SyncResponse first = await peer.SendAsync("sync.inventory", request, timeout.Token).ConfigureAwait(true);
        SyncInventoryOffer offer = Assert.IsType<SyncInventoryOffer>(first.Inventory);
        SyncResponse repeated = await peer.SendAsync("sync.inventory", request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(offer.Manifest, repeated.Inventory!.Manifest);
        _ = await peer.SendAsync("sync.close", new SyncCloseRequest(offer.Manifest.Id, Guid.Empty), timeout.Token).ConfigureAwait(true);
        SyncResponse late = await peer.SendAsync("sync.inventory", request, timeout.Token).ConfigureAwait(true);
        Assert.Equal(offer.Manifest, late.Inventory!.Manifest);
        SyncResponse unavailable = await peer.SendAsync("sync.read", new SyncReadRequest(offer.Manifest.Id, 0, 1), timeout.Token).ConfigureAwait(true);
        Assert.Equal("Sync.SessionUnavailable", unavailable.Reason);
        string[] changed = request.Hashes.ToArray();
        changed[0] = new string('0', 64);
        SyncResponse mismatch = await peer.SendAsync("sync.inventory", request with { Hashes = changed }, timeout.Token).ConfigureAwait(true);
        Assert.Equal("Sync.InventoryMismatch", mismatch.Reason);
    }

    /// <summary>照合応答を失った送信元も、元の照合IDだけで作業枠を解放できます。</summary>
    /// <returns>有限枠を超える連続中断と、遅延再配送を拒否する確認です。</returns>
    [Fact]
    public async Task AbandonedInventoryReleasesCapacityWithoutKnowingExportIdAsync()
    {
        SyncRecoveryPeer peer = new(Snapshot());
        await using ConfiguredAsyncDisposable lifetime = peer.ConfigureAwait(true);
        await peer.StartAsync().ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (peer.Target.GetPeers().Count < 2)
        {
            await peer.AnnounceAsync("different", EPeerStatus.Syncing, timeout.Token).ConfigureAwait(true);
            await Task.Delay(20, timeout.Token).ConfigureAwait(true);
        }
        SyncInventoryRequest? abandoned = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await peer.AnnounceAsync("different", EPeerStatus.Syncing, timeout.Token).ConfigureAwait(true);
            SyncInventoryRequest request = new(Guid.NewGuid(), SyncInventory.Describe(Snapshot()), DateTimeOffset.UtcNow.AddSeconds(20));
            SyncResponse response = await peer.SendAsync("sync.inventory", request, timeout.Token).ConfigureAwait(true);
            SyncInventoryOffer offer = Assert.IsType<SyncInventoryOffer>(response.Inventory);
            abandoned = request;
            _ = await peer.SendAsync("sync.close", new SyncCloseRequest(Guid.Empty, Guid.NewGuid()), timeout.Token).ConfigureAwait(true);
            Assert.NotNull((await peer.SendAsync("sync.read", new SyncReadRequest(offer.Manifest.Id, 0, 1), timeout.Token).ConfigureAwait(true)).Chunk);

            // 応答を失った実送信元と同じく、相手が生成したExportIdは終了要求へ渡しません。
            SyncCloseRequest close = new(Guid.Empty, request.Id);
            _ = await peer.SendAsync("sync.close", close, timeout.Token).ConfigureAwait(true);
            _ = await peer.SendAsync("sync.close", close, timeout.Token).ConfigureAwait(true);
            SyncResponse unavailable = await peer.SendAsync("sync.read", new SyncReadRequest(offer.Manifest.Id, 0, 1), timeout.Token).ConfigureAwait(true);
            Assert.Equal("Sync.SessionUnavailable", unavailable.Reason);
        }
        SyncResponse late = await peer.SendAsync("sync.inventory", abandoned!, timeout.Token).ConfigureAwait(true);
        Assert.Equal("Sync.SessionUnavailable", late.Reason);
        Assert.Null(late.Inventory);
    }

    /// <summary>書出し作成中に終了要求を受けても、作成後の枠を取り残しません。</summary>
    /// <param name="failExport">作成途中の失敗も同じ寿命管理で扱います。</param>
    /// <returns>作成完了、失敗、遅延要求、別実体の解放要求を検証します。</returns>
    [Theory(Skip = "旧仕様: 終了要求で作成を取り消さず、元の書出し完了を待つ。取消と非協力的I/Oの寿命は別の現行試験で検証する。")]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingPendingInventoryObservesItsOwnedCreationAsync(bool failExport)
        => VerifyClosingCreationAsync(failExport, false);

    /// <summary>取消に協力しないI/Oも終了まで所有し、作成後の枠を取り残しません。</summary>
    /// <param name="failExport">作成途中の失敗も同じ寿命管理で扱います。</param>
    /// <returns>作成完了、失敗、遅延要求、別実体の解放要求を検証します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ClosingNonCooperativeInventoryStillObservesItsOwnedCreationAsync(bool failExport)
        => VerifyClosingCreationAsync(failExport, true);

    /// <summary>作成を保持したまま終了し、完了・失敗時に同じ資源が回収されるかを観測します。</summary>
    /// <returns>作成を完了させる旧期待を保存した共通の検証です。</returns>
    private static async Task VerifyClosingCreationAsync(bool failExport, bool ignoreCancellation, CancellationToken cancellationToken = default)
    {
        SyncRecoveryPeer peer = new(Snapshot());
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        await peer.StartAsync(cancellationToken).ConfigureAwait(true);
        PausedExportRepository repository = new(failExport, ignoreCancellation);
        PeerInfo local = peer.Target.GetPeers().Single(item => item.InstanceId == peer.Target.LocalInstanceId);
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".")
        {
            MaximumSyncSessions = 1,
        };
        string network = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(System.Net.IPAddress.Loopback))).Id;
        SyncExportStore exports = new(peer.Target, repository, options, TimeProvider.System, () => new(network, local.Address));
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        SyncInventoryExchange inventory = new(peer.Target, exports, options, TimeProvider.System, CancellationToken.None);
        await using ConfiguredAsyncDisposable inventoryLifetime = inventory.ConfigureAwait(true);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        SyncInventoryRequest request = new(Guid.NewGuid(), SyncInventory.Describe(Snapshot()), DateTimeOffset.UtcNow.AddSeconds(20));
        Task<SyncResponse> first = inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token);
        Task? close = null;
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            await inventory.CloseAsync(request.Id, local.PeerId, Guid.NewGuid(), timeout.Token).ConfigureAwait(true);
            close = inventory.CloseAsync(request.Id, local.PeerId, local.InstanceId, timeout.Token);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            repository.Release.TrySetResult();
            await Task.WhenAll(first, close ?? Task.CompletedTask).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        if (failExport)
        {
            _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await first.ConfigureAwait(false)).ConfigureAwait(true);
        }
        else
        {
            Assert.Equal("Sync.SessionUnavailable", (await first.ConfigureAwait(true)).Reason);
        }
        await close!.WaitAsync(timeout.Token).ConfigureAwait(true);
        SyncResponse late = await inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true);
        Assert.Equal("Sync.SessionUnavailable", late.Reason);
        SyncInventoryRequest next = request with { Id = Guid.NewGuid() };
        Assert.NotNull((await inventory.ReceiveAsync(next, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true)).Inventory);
        Assert.Equal(2, repository.Exports);
        await inventory.CloseAsync(next.Id, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true);
    }

    /// <summary>通信期限より長い照合準備も即座に受付応答し、再照会で書出しを増やしません。</summary>
    /// <returns>同じ要求の処理中・完了・解放と、旧契約の待機を区別します。</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingInventoryRetainsOneExportAsync(bool allowPending)
    {
        SyncRecoveryPeer peer = new(Snapshot());
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        PausedExportRepository repository = new(false);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".");
        string network = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(System.Net.IPAddress.Loopback))).Id;
        SyncExportStore exports = new(peer.Target, repository, options, TimeProvider.System, () => new(network, local.Address));
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        SyncInventoryExchange inventory = new(peer.Target, exports, options, TimeProvider.System, CancellationToken.None);
        await using ConfiguredAsyncDisposable inventoryLifetime = inventory.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        SyncInventoryRequest request = new(Guid.NewGuid(), SyncInventory.Describe(Snapshot()), DateTimeOffset.UtcNow.AddSeconds(20), AllowPending: allowPending);
        Task<SyncResponse> first = inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token);
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            if (allowPending)
            {
                Assert.True((await first.WaitAsync(timeout.Token).ConfigureAwait(true)).Pending);
                for (int index = 0; index < 3; index++)
                {
                    Assert.True((await inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true)).Pending);
                }
            }
            else
            {
                Assert.False(first.IsCompleted);
            }
            Assert.Equal(1, repository.Exports);
        }
        finally
        {
            repository.Release.TrySetResult();
            await ((Task)first).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        _ = await first.ConfigureAwait(true);
        SyncResponse result;
        do
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
            result = await inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true);
        } while (result.Pending);
        Assert.NotNull(result.Inventory);
        Assert.Equal(1, repository.Exports);
        Assert.Equal(result.Inventory.Manifest, (await inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true)).Inventory!.Manifest);
        await inventory.CloseAsync(request.Id, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true);
        Assert.Equal("Sync.SessionUnavailable", (await inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true)).Reason);
    }

    /// <summary>終了要求は作成中の読取りも取消し、元期限を待たず次の要求へ枠を返します。</summary>
    /// <returns>取消、重複終了、再配送拒否と新しい要求の成功です。</returns>
    [Fact]
    public async Task ClosingPendingInventoryCancelsCreationAndReleasesItsSlotAsync()
    {
        SyncRecoveryPeer peer = new(Snapshot());
        await using ConfiguredAsyncDisposable peerLifetime = peer.ConfigureAwait(true);
        PausedExportRepository repository = new(false);
        PeerInfo local = Assert.Single(peer.Target.GetPeers());
        ClusterOptions options = new(peer.Target.LocalClusterId, local.PeerId, local.HostId, local.Address, "127.255.255.255", local.Port, 1, true, ".") { MaximumSyncSessions = 1 };
        string network = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(System.Net.IPAddress.Loopback))).Id;
        SyncExportStore exports = new(peer.Target, repository, options, TimeProvider.System, () => new(network, local.Address));
        await using ConfiguredAsyncDisposable exportLifetime = exports.ConfigureAwait(true);
        SyncInventoryExchange inventory = new(peer.Target, exports, options, TimeProvider.System, CancellationToken.None);
        await using ConfiguredAsyncDisposable inventoryLifetime = inventory.ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        SyncInventoryRequest request = new(Guid.NewGuid(), SyncInventory.Describe(Snapshot()), DateTimeOffset.UtcNow.AddMinutes(5));
        Task<SyncResponse> first = inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token);
        Task close = Task.CompletedTask;
        try
        {
            await repository.Entered.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
            close = inventory.CloseAsync(request.Id, local.PeerId, local.InstanceId, timeout.Token);
            await close.WaitAsync(timeout.Token).ConfigureAwait(true);
            Assert.Equal("Sync.SessionUnavailable", (await first.ConfigureAwait(true)).Reason);
            Assert.False(repository.Release.Task.IsCompleted);
            await inventory.CloseAsync(request.Id, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true);
            Assert.Equal("Sync.SessionUnavailable", (await inventory.ReceiveAsync(request, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true)).Reason);
            SyncInventoryRequest next = request with { Id = Guid.NewGuid() };
            Assert.NotNull((await inventory.ReceiveAsync(next, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true)).Inventory);
            Assert.Equal(2, repository.Exports);
            await inventory.CloseAsync(next.Id, local.PeerId, local.InstanceId, timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            repository.Release.TrySetResult();
            await timeout.CancelAsync().ConfigureAwait(true);
            await Task.WhenAll(first, close).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    /// <summary>最初の書出しだけを保留し、終了要求との交差を再現します。</summary>
    private sealed class PausedExportRepository(bool failExport, bool ignoreCancellation = false) : DurableRepository
    {
        private int exports;

        /// <summary>最初の書出し開始を通知します。</summary>
        internal TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>試験側が最初の書出しを解放します。</summary>
        internal TaskCompletionSource Release
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>旧要求で再作成されなかったことを確認する回数です。</summary>
        internal int Exports => Volatile.Read(ref exports);

        /// <summary>最初の作成だけが保留・失敗し、その後は通常の内容を返します。</summary>
        /// <returns>試験用の固定書出しです。</returns>
        public override async ValueTask<RepositorySnapshot> ExportAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref exports) == 1)
            {
                Entered.TrySetResult();
                if (ignoreCancellation)
                {
                    await Release.Task.ConfigureAwait(false);
                }
                else
                {
                    await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                if (failExport)
                {
                    throw new InvalidDataException("Expected export failure.");
                }
            }
            return Snapshot();
        }

        /// <summary>書出し以外の保存操作を試験中に暗黙に許可しません。</summary>
        /// <returns>想定外の操作を示す例外です。</returns>
        private protected override T Access<T>(Func<RepositoryDocument, T> operation, bool write) => throw new InvalidOperationException("Unexpected repository operation.");
    }

    /// <summary>共有の現在値と、その祖先を含む確定変更を用意します。</summary>
    /// <returns>少量の固定試験データです。</returns>
    private static RepositorySnapshot Snapshot()
    {
        DurableEntry previous = new("workflow:inventory", "value", "previous", NodeValue.FromObject(1));
        DurableEntry current = new("workflow:inventory", "value", "current", NodeValue.FromObject(2), "previous");
        DurableEntry other = new("workflow:inventory", "other", "other", NodeValue.FromObject("retained"));
        DurableReceipt receipt = new(new Guid("180a6035-2a93-4f66-966e-dabf85d0631a"), "fingerprint", EOutcome.Succeeded, [previous]);
        return new([current, other], [receipt]);
    }
}
