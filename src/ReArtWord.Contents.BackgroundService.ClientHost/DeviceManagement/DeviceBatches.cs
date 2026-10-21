using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>ブラウザーから独立して一括変更を保持し、再接続時も元の対象を維持します。</summary>
internal sealed class DeviceBatches(DeviceBatchStore store, IDeviceRemote remote) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Channel<DeviceBatchState> pending = Channel.CreateBounded<DeviceBatchState>(1);
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool active;

    /// <summary>固定した変更一覧を永続保存してから処理へ渡します。</summary>
    /// <returns>元の一括要求の進捗です。</returns>
    internal async Task<DeviceBatchState> EnqueueAsync(DeviceBatchRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeviceBatchState? existing = await store.ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(ContractJson.Hash(existing.Request), ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    throw new ArgumentException("Device.OperationMismatch", nameof(request));
                }
                return existing;
            }
            IReadOnlyList<DeviceBatchState> previous = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> blocked = previous.SelectMany(state => state.Results.Select((result, index) =>
                result?.Outcome == EOutcome.Unknown ? state.Request.Changes[index].Target.HostId
                                                    : null)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (active || (!request.AcknowledgeUnconfirmed && request.Changes.Any(change => blocked.Contains(change.Target.HostId))))
            {
                throw new InvalidOperationException("Device.PreviousChangeUnconfirmed");
            }
            DeviceBatchRequest owned = ContractJson.Copy(request);
            DeviceBatchState state = new(owned, DateTimeOffset.UtcNow, true, new DeviceChangeResult?[owned.Changes.Count]);
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (!pending.Writer.TryWrite(state))
            {
                throw new InvalidOperationException("Device.WorkerUnavailable");
            }
            active = true;
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>変更を送信せず、Coreが保持する元IDの結果だけを取り直します。</summary>
    /// <returns>未確認結果を更新した保存状態です。</returns>
    internal async Task<DeviceBatchState> ReconcileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active)
            {
                throw new InvalidOperationException("Device.BatchRunning");
            }
            DeviceBatchState state = await store.ReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new ArgumentException("Device.BatchMissing", nameof(id));
            DeviceChangeResult?[] results = state.Results.ToArray();
            for (int index = 0; index < results.Length; index++)
            {
                if (results[index]?.Outcome == EOutcome.Unknown)
                {
                    DeviceChangeResult? result = await remote.ResultAsync(state.Request.Changes[index], cancellationToken).ConfigureAwait(false);
                    if (result is not null)
                    {
                        results[index] = result;
                    }
                }
            }
            state = state with { Results = results };
            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>最近の操作を画面へ復元します。</summary>
    /// <returns>最新20件の保存状態です。</returns>
    internal async Task<IReadOnlyList<DeviceBatchState>> ListAsync(CancellationToken cancellationToken = default)
    {
        await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return (await store.ReadAllAsync(cancellationToken).ConfigureAwait(false)).Take(20).ToArray();
    }

    /// <summary>表示件数から落ちた古い履歴も含め、不明結果が残るPCを確認します。</summary>
    /// <returns>新規変更前の明示確認が必要なHostです。</returns>
    internal async Task<IReadOnlyList<string>> UnconfirmedHostsAsync(CancellationToken cancellationToken = default)
    {
        await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DeviceBatchState> states = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return states.SelectMany(state => state.Results.Select((result, index) => result?.Outcome == EOutcome.Unknown ? state.Request.Changes[index].Target.HostId
                                                                                                                      : null)).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>復旧後に受け付けた計画だけを処理します。</summary>
    /// <returns>Host終了までの処理です。</returns>
    // 抑制理由: 永続状態の保存不能をHostへ通知し、受付不能のWorkerを正常動作として残しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "受付待機へ障害を通知した後、元の永続化障害でHostを終了します。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.RecoverAsync(stoppingToken).ConfigureAwait(false);
            initialized.TrySetResult();
            await foreach (DeviceBatchState state in pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await new DeviceBatchRunner(remote, store).RunAsync(state, stoppingToken).ConfigureAwait(false);
                await gate.WaitAsync(stoppingToken).ConfigureAwait(false);
                active = false;
                gate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            initialized.TrySetException(new InvalidOperationException("Device.WorkerUnavailable"));
            pending.Writer.TryComplete(exception);
            if (!stoppingToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    /// <summary>同一PC内でPeerを混ぜず、一資源を重複変更しません。</summary>
    internal static void Validate(DeviceBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.Changes.Count is < 1 or > 256
            || request.Changes.Select(change => change.Request.OperationId).Distinct().Count() != request.Changes.Count)
        {
            throw new ArgumentException("Device.InvalidBatch", nameof(request));
        }
        HashSet<(string Host, EDeviceArea Area, string? Resource, string Scope, string? User)> resources = [];
        foreach (DevicePlannedChange change in request.Changes)
        {
            DeviceChangeRules.Validate(change.Request);
            ArgumentException.ThrowIfNullOrWhiteSpace(change.Target.ClusterId);
            ArgumentException.ThrowIfNullOrWhiteSpace(change.Target.PeerId);
            string? resource = change.Request.Read.ResourceId;
            if (change.Request.Read.Area == EDeviceArea.Environment)
            {
                resource = resource?.ToUpperInvariant();
            }
            if (change.Request.Read.Area == EDeviceArea.Network && Guid.TryParse(resource, out Guid interfaceId))
            {
                resource = interfaceId.ToString("D");
            }
            string switchPrefix = "switch/";
            if (change.Request.Read.Area == EDeviceArea.Network && resource?.StartsWith(switchPrefix, StringComparison.Ordinal) == true
                && Guid.TryParse(resource[switchPrefix.Length..], out Guid switchId))
            {
                resource = switchPrefix + switchId.ToString("D");
            }
            if (change.Target.InstanceId != change.Request.InstanceId || !string.Equals(change.Target.HostId, change.Request.HostId, StringComparison.Ordinal)
                || !resources.Add((change.Target.HostId, change.Request.Read.Area, resource, change.Request.Read.Scope, change.Request.Read.UserSid)))
            {
                throw new ArgumentException("Device.InvalidBatch", nameof(request));
            }
        }
        if (request.Changes.GroupBy(change => change.Target.HostId, StringComparer.Ordinal).Any(group => group.Select(change => change.Target).Distinct().Count() != 1))
        {
            throw new ArgumentException("Device.DuplicateHost", nameof(request));
        }
    }

    /// <summary>所有した受付資源を終了します。</summary>
    public override void Dispose()
    {
        pending.Writer.TryComplete();
        gate.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
