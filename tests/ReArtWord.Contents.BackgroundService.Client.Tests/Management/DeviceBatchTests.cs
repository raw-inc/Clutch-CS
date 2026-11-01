using ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.EnvironmentVariables;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Management;

/// <summary>一括操作の中断・重複・PC間の障害分離を確認します。</summary>
public sealed class DeviceBatchTests
{
    /// <summary>進捗更新中の照会で、受理済み操作が一時的に読めなくならないことを確認します。</summary>
    /// <returns>実ファイルの置換と複数読取りの競合確認です。</returns>
    [Fact]
    public async Task ConcurrentProgressReadsRemainAvailableAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-device-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using DeviceBatchStore store = new(directory);
            DeviceBatchState state = new(new(Guid.NewGuid(), [Change("host", "PATH")]), DateTimeOffset.UtcNow, true, [null]);
            await store.SaveAsync(state);
            Task writer = Task.Run(async () =>
            {
                for (int index = 0; index < 100; index++)
                {
                    await store.SaveAsync(state).ConfigureAwait(false);
                }
            });
            Task reader = Task.Run(async () =>
            {
                for (int index = 0; index < 200; index++)
                {
                    DeviceBatchState? current = await store.ReadAsync(state.Request.OperationId).ConfigureAwait(false);
                    Assert.NotNull(current);
                    Assert.Equal(state.Request.OperationId, current.Request.OperationId);
                    Assert.Single(await store.ReadAllAsync().ConfigureAwait(false));
                }
            });
            await Task.WhenAll(writer, reader);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>同じスイッチIDの表記違いを別資源として二重削除しません。</summary>
    [Fact]
    public void RejectsDuplicateSwitchIdsWithDifferentFormatting()
    {
        DevicePlannedChange source = Change("host", "PATH");
        Guid switchId = Guid.NewGuid();
        DeviceChangeRequest request = source.Request with { Action = EDeviceAction.VirtualSwitchDelete, Read = new(EDeviceArea.Network, "switch/" + switchId.ToString("D")),
            Change = DeviceSnapshots.Create(new Contracts.DeviceManagement.Network.DeviceVirtualSwitchChange()).Data, AcknowledgeDisconnect = true };
        DevicePlannedChange first = source with { Request = request };
        DevicePlannedChange duplicate = source with { Request = request with { OperationId = Guid.NewGuid(), Read = request.Read with { ResourceId = "switch/" + switchId.ToString("B").ToUpperInvariant() } } };
        DeviceBatches.Validate(new(Guid.NewGuid(), [first]));
        Assert.Throws<ArgumentException>(() => DeviceBatches.Validate(new(Guid.NewGuid(), [first, duplicate])));
    }

    /// <summary>古い不明結果は成功へ書き替えず、明示確認した新しい変更だけを受け付けます。</summary>
    /// <returns>自動再試行と手動の次操作を分離した確認です。</returns>
    [Fact]
    public async Task RequiresAcknowledgementBeforeNewChangesOnUnconfirmedHostAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-device-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using DeviceBatchStore store = new(directory);
            Remote remote = new();
            DevicePlannedChange original = Change("host", "PATH");
            DeviceBatchState previous = new(new(Guid.NewGuid(), [original]), DateTimeOffset.UtcNow, false, [new(original.Request.OperationId, EOutcome.Unknown)]);
            await store.SaveAsync(previous);
            using DeviceBatches batches = new(store, remote);
            await batches.StartAsync(CancellationToken.None);
            try
            {
                DeviceBatchRequest next = new(Guid.NewGuid(), [Change("host", "TEMP")]);
                await Assert.ThrowsAsync<InvalidOperationException>(() => batches.EnqueueAsync(next));
                DeviceBatchState accepted = await batches.EnqueueAsync(next with { AcknowledgeUnconfirmed = true });
                Assert.NotEqual(previous.Request.OperationId, accepted.Request.OperationId);
            }
            finally
            {
                await batches.StopAsync(CancellationToken.None);
            }
            Assert.Equal(EOutcome.Unknown, (await store.ReadAsync(previous.Request.OperationId))!.Results[0]!.Outcome);
            Assert.Single(await batches.UnconfirmedHostsAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>画面が以前のファイルを読んでいても、元内容を保ったまま次の状態へ置換します。</summary>
    /// <returns>Windowsの開いたハンドルとの原子的置換の確認です。</returns>
    [Fact]
    public async Task SavesProgressWhilePreviousSnapshotIsOpenAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-device-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using DeviceBatchStore store = new(directory);
            DeviceBatchState state = new(new(Guid.NewGuid(), [Change("host", "PATH")]), DateTimeOffset.UtcNow, true, [null]);
            await store.SaveAsync(state);
            using FileStream reading = new(Path.Combine(directory, "host-manager", state.Request.OperationId.ToString("N") + ".json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            await store.SaveAsync(state with { Running = false });
            Assert.False((await store.ReadAsync(state.Request.OperationId))!.Running);
            using StreamReader reader = new(reading);
            DeviceBatchState original = System.Text.Json.JsonSerializer.Deserialize<DeviceBatchState>(await reader.ReadToEndAsync(), Contracts.Serialization.ContractJson.Options)!;
            Assert.True(original.Running);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>不明なPCの後続だけ止め、別PCは一度だけ変更します。</summary>
    /// <returns>固定された順序と結果保存の確認です。</returns>
    [Fact]
    public async Task UnknownBlocksOnlyTheSameHostAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-device-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using DeviceBatchStore store = new(directory);
            Remote remote = new();
            DevicePlannedChange first = Change("first", "PATH"), second = first with { Request = first.Request with { OperationId = Guid.NewGuid(), Read = new(EDeviceArea.Environment, "TEMP") } };
            DeviceBatchState state = new(new(Guid.NewGuid(), [first, second, Change("other", "PATH")]), DateTimeOffset.UtcNow, true, [null, null, null]);
            DeviceBatches.Validate(state.Request);
            await store.SaveAsync(state);
            DeviceBatchState result = await new DeviceBatchRunner(remote, store).RunAsync(state);
            Assert.Equal([EOutcome.Unknown, EOutcome.NotExecuted, EOutcome.Succeeded], result.Results.Select(item => item!.Outcome));
            Assert.Equal(["first", "other"], remote.Sent);
            Assert.False((await store.ReadAsync(state.Request.OperationId))!.Running);
            await store.RecoverAsync();
            Assert.Equal(2, remote.Sent.Count);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>再起動時に送信済みを不明、残りを未実行とし、キューを復元しません。</summary>
    /// <returns>同じIDの結果が保存され続ける確認です。</returns>
    [Fact]
    public async Task RestartPreservesUnknownAndDoesNotResumeQueuedChangesAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "contents-device-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            using DeviceBatchStore store = new(directory);
            DevicePlannedChange first = Change("first", "PATH"), second = Change("other", "PATH");
            DeviceBatchState state = new(new(Guid.NewGuid(), [first, second]), DateTimeOffset.UtcNow, true, [new(first.Request.OperationId, EOutcome.Unknown, "Device.Dispatching"), null]);
            await store.SaveAsync(state);
            using DeviceBatchStore restarted = new(directory);
            await restarted.RecoverAsync();
            DeviceBatchState recovered = (await store.ReadAsync(state.Request.OperationId))!;
            Assert.False(recovered.Running);
            Assert.Equal(EOutcome.Unknown, recovered.Results[0]!.Outcome);
            Assert.Equal(EOutcome.NotExecuted, recovered.Results[1]!.Outcome);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>同じPCへ異なるPeer実体を混ぜる要求と同じ資源の重複を拒否します。</summary>
    [Fact]
    public void RejectsDuplicateTargetsAndPeerSwitches()
    {
        DevicePlannedChange first = Change("host", "PATH"), second = Change("host", "TEMP");
        Assert.Throws<ArgumentException>(() => DeviceBatches.Validate(new(Guid.NewGuid(), [first, second])));
        second = first with { Request = first.Request with { OperationId = Guid.NewGuid() } };
        Assert.Throws<ArgumentException>(() => DeviceBatches.Validate(new(Guid.NewGuid(), [first, second])));
        second = first with { Request = first.Request with { OperationId = Guid.NewGuid(), Read = first.Request.Read with { ResourceId = "Path" } } };
        Assert.Throws<ArgumentException>(() => DeviceBatches.Validate(new(Guid.NewGuid(), [first, second])));
    }

    /// <summary>機械環境変数の固定要求を作ります。</summary>
    /// <returns>別の実体へ流用しない変更です。</returns>
    private static DevicePlannedChange Change(string host, string variable)
    {
        Guid instance = Guid.NewGuid();
        return new(new(host, "cluster", "peer", instance), new(Guid.NewGuid(), host, instance, EDeviceAction.Environment,
            new(EDeviceArea.Environment, variable), new string('a', 64), DeviceSnapshots.Create(new DeviceEnvironmentChange("desired")).Data));
    }

    /// <summary>実PCを変更せず、不明を返す一つの送信を再現します。</summary>
    private sealed class Remote : IDeviceRemote
    {
        /// <summary>送信されたPCの順序です。</summary>
        internal List<string> Sent
        {
            get;
        } = [];

        /// <summary>試験で使わない設定の参照を拒否します。</summary>
        /// <returns>未使用です。</returns>
        public Task<DeviceSnapshot> ReadAsync(DeviceTargetRead request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        /// <summary>最初のPCだけ不明とします。</summary>
        /// <returns>実送信なしの結果です。</returns>
        public Task<DeviceChangeResult> ApplyAsync(DevicePlannedChange change, CancellationToken cancellationToken = default)
        {
            Sent.Add(change.Target.HostId);
            EOutcome outcome = Sent.Count == 1 ? EOutcome.Unknown
                                               : EOutcome.Succeeded;
            return Task.FromResult(new DeviceChangeResult(change.Request.OperationId, outcome));
        }

        /// <summary>結果がまだ不明であることを返します。</summary>
        /// <returns>未確認です。</returns>
        public Task<DeviceChangeResult?> ResultAsync(DevicePlannedChange change, CancellationToken cancellationToken = default) => Task.FromResult<DeviceChangeResult?>(null);
    }
}
