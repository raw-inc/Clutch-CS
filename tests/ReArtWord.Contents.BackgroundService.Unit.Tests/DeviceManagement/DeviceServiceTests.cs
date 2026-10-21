using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Hosts;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Unit.Tests.Processes;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.DeviceManagement;

/// <summary>永続記録・Host排他・不明結果をOS境界と分離して確認します。</summary>
public sealed class DeviceServiceTests
{
    /// <summary>再送でOSを再実行せず、異なる本文と古い版を拒否します。</summary>
    /// <returns>一度だけ反映したことの確認です。</returns>
    [Fact]
    public async Task RejectsChangedTargetsAndReplaysOriginalResultAsync()
    {
        using ProcessTestScope files = new();
        Platform platform = new();
        Guid instance = Guid.NewGuid();
        using DeviceManagementService service = Service(files, platform, instance);
        DeviceChangeRequest request = Request(platform, instance);
        Assert.Equal(EOutcome.NotExecuted, (await service.ApplyAsync(request with { InstanceId = Guid.NewGuid() })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await service.ApplyAsync(request with { HostId = "different-host" })).Outcome);
        DeviceChangeResult first = await service.ApplyAsync(request);
        Assert.Equal(EOutcome.Succeeded, first.Outcome);
        Assert.Equal(first, await service.ApplyAsync(request));
        Assert.Equal(1, platform.Calls);
        Assert.Equal(EOutcome.NotExecuted, (await service.ApplyAsync(request with { Change = DeviceSnapshots.Create(new DeviceHostsChange("10.0.0.9 other\n")).Data })).Outcome);
        Assert.Equal(EOutcome.NotExecuted, (await service.ApplyAsync(request with { OperationId = Guid.NewGuid() })).Outcome);
        Assert.Equal(1, platform.Calls);
    }

    /// <summary>OS変更の途中にCoreを作り直しても不明を再実行しません。</summary>
    /// <returns>永続した開始記録と再実行防止の確認です。</returns>
    [Fact]
    public async Task PersistsUnknownBeforeChangingOsAndNeverRetriesAsync()
    {
        using ProcessTestScope files = new();
        Platform platform = new() { FailAfterStart = true };
        Guid instance = Guid.NewGuid();
        DeviceChangeRequest request = Request(platform, instance);
        using DeviceManagementService service = Service(files, platform, instance);
        Assert.Equal(EOutcome.Unknown, (await service.ApplyAsync(request)).Outcome);
        using DeviceManagementService restarted = Service(files, platform, instance);
        Assert.Equal(EOutcome.Unknown, (await restarted.ReadOperationAsync(request.OperationId))!.Outcome);
        Assert.Equal(EOutcome.Unknown, (await restarted.ApplyAsync(request)).Outcome);
        Assert.Equal(1, platform.Calls);
    }

    /// <summary>別Peerの同じPC操作を開始前に拒否し、最初の開始記録を参照できます。</summary>
    /// <returns>ファイル排他の実競合と解除の確認です。</returns>
    [Fact]
    public async Task SerializesDifferentPeersOnOneHostAsync()
    {
        using ProcessTestScope files = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Platform platform = new() { Release = release.Task };
        Guid instance = Guid.NewGuid();
        using DeviceManagementService first = Service(files, platform, instance), second = Service(files, platform, instance, "second");
        DeviceChangeRequest request = Request(platform, instance);
        Task<DeviceChangeResult> running = first.ApplyAsync(request);
        try
        {
            await platform.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EOutcome.Unknown, (await first.ReadOperationAsync(request.OperationId))!.Outcome);
            DeviceChangeResult blocked = await second.ApplyAsync(request with { OperationId = Guid.NewGuid() });
            Assert.Equal(EOutcome.NotExecuted, blocked.Outcome);
            Assert.Equal("Device.HostBusy", blocked.Reason);
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal(EOutcome.Succeeded, (await running.ConfigureAwait(true)).Outcome);
        }
        Assert.Equal(1, platform.Calls);
    }

    /// <summary>OSコマンドが成功を返しても、実読取りが一致しなければ成功にしません。</summary>
    /// <returns>反映不能の検出です。</returns>
    [Fact]
    public async Task RequiresReadBackBeforeSuccessAsync()
    {
        using ProcessTestScope files = new();
        Platform platform = new() { IgnoreChange = true };
        Guid instance = Guid.NewGuid();
        using DeviceManagementService service = Service(files, platform, instance);
        DeviceChangeResult result = await service.ApplyAsync(Request(platform, instance));
        Assert.Equal(EOutcome.Unknown, result.Outcome);
        Assert.Equal("Device.VerificationUnconfirmed", result.Reason);
    }

    /// <summary>同一PCを表す排他ファイルを共有します。</summary>
    /// <returns>隔離した永続記録を使用するサービスです。</returns>
    private static DeviceManagementService Service(ProcessTestScope files, Platform platform, Guid instance, string peer = "peer") =>
        new("host", instance, peer, files.Repository, platform, Path.Combine(files.Root, "lock"));

    /// <summary>現在版のhosts変更を作ります。</summary>
    /// <returns>対象を固定した要求です。</returns>
    private static DeviceChangeRequest Request(Platform platform, Guid instance) => new(Guid.NewGuid(), "host", instance, EDeviceAction.Hosts,
        new(EDeviceArea.Hosts), platform.State.Revision, DeviceSnapshots.Create(new DeviceHostsChange("10.0.0.2 display\n")).Data);

    /// <summary>OSへ渡した回数と途中失敗だけを再現します。</summary>
    private sealed class Platform : IDeviceSettingsPlatform
    {
        /// <summary>現在の読取り内容です。</summary>
        internal DeviceSnapshot State
        {
            get; private set;
        } = DeviceSnapshots.Create(new DeviceHostsState("127.0.0.1 localhost\n"));

        /// <summary>OS呼出回数です。</summary>
        internal int Calls
        {
            get; private set;
        }

        /// <summary>OS開始後に障害を起こすかです。</summary>
        internal bool FailAfterStart
        {
            get; init;
        }

        /// <summary>成功応答と実状態が一致しない場合を再現します。</summary>
        internal bool IgnoreChange
        {
            get; init;
        }

        /// <summary>並行試験のOS開始通知です。</summary>
        internal TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>同時実行を確認するまで反映を待ちます。</summary>
        internal Task Release
        {
            get; init;
        } = Task.CompletedTask;

        /// <summary>hostsのみに対応します。</summary>
        /// <returns>試験用能力です。</returns>
        public DeviceCapabilities Capabilities() => new("Windows", "host", true, [EDeviceArea.Hosts], []);

        /// <summary>実変更の前後で同じ状態を読みます。</summary>
        /// <returns>現在の設定です。</returns>
        public Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(State);

        /// <summary>待機・失敗・実反映をOS境界の外側から観測できるようにします。</summary>
        /// <returns>試験条件に応じた反映結果です。</returns>
        public async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, DeviceSnapshot before, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (FailAfterStart)
            {
                throw new IOException("Interrupted after dispatch.");
            }
            if (!IgnoreChange)
            {
                State = DeviceSnapshots.Create(new DeviceHostsState(DeviceSnapshots.Read<DeviceHostsChange>(request.Change).Content));
            }
            return new(request.OperationId, EOutcome.Succeeded);
        }
    }
}
