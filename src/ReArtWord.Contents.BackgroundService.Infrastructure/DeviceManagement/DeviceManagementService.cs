using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.Network;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.DeviceManagement;

/// <summary>対象・変更前の版・開始記録を確認し、一度だけOS設定へ反映します。</summary>
public sealed class DeviceManagementService : IDisposable
{
    private readonly string hostId;
    private readonly Guid instanceId;
    private readonly string scope;
    private readonly string lockDirectory;
    private readonly IDurableRepository repository;
    private readonly IDeviceSettingsPlatform platform;

    /// <summary>このサービスが所有するOS設定キャッシュを終了します。</summary>
    public void Dispose()
    {
        if (platform is IDisposable resource)
        {
            resource.Dispose();
        }
    }

    /// <summary>Coreの実体、保存先とOSの固定操作を接続します。</summary>
    // 抑制理由: platformの所有権を委譲先コンストラクターへ移し、このサービスのDisposeで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "platformフィールドへ所有権を移し、Disposeで解放します。")]
    public DeviceManagementService(string hostId, Guid instanceId, string peerId, IDurableRepository repository, string dataDirectory, Display.IDeviceDisplayAccess? display = null)
        : this(hostId, instanceId, peerId, repository, new DeviceSettingsPlatform(dataDirectory, display),
            DeviceHostStorage.DirectoryPath)
    {
    }

    /// <summary>OS境界と同一PC排他の場所を隔離した検証へ接続します。</summary>
    internal DeviceManagementService(string hostId, Guid instanceId, string peerId, IDurableRepository repository, IDeviceSettingsPlatform platform, string lockDirectory)
    {
        this.hostId = hostId;
        this.instanceId = instanceId;
        this.repository = repository;
        this.platform = platform;
        this.lockDirectory = lockDirectory;
        scope = "peer:" + peerId + ":host-manager";
    }

    /// <summary>未対応OSと権限を推測せず照会します。</summary>
    /// <returns>現在の実行能力です。</returns>
    public DeviceCapabilities Capabilities() => platform.Capabilities();

    /// <summary>変更を開始せず、有限の設定値と内容版を読みます。</summary>
    /// <returns>利用者が確認する設定です。</returns>
    public Task<DeviceSnapshot> ReadAsync(DeviceReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DeviceChangeRules.ValidateRead(request);
        if (!platform.Capabilities().Areas.Contains(request.Area))
        {
            throw new InvalidOperationException("Device.UnsupportedPlatform");
        }
        return platform.ReadAsync(request, cancellationToken);
    }

    /// <summary>開始済み変更を再実行せずに元の結果を読みます。</summary>
    /// <returns>確認済み結果。元の開始がない場合はnullです。</returns>
    public async Task<DeviceChangeResult?> ReadOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DeviceOperationRecord? record = await RecordAsync(operationId, cancellationToken).ConfigureAwait(false);
        return record?.Result;
    }

    /// <summary>排他内で古い版を拒否し、永続開始記録の成功後だけOSへ渡します。</summary>
    /// <returns>副作用を再試行せず確認した結果です。</returns>
    public async Task<DeviceChangeResult> ApplyAsync(DeviceChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            DeviceChangeRules.Validate(request);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.InvalidRequest");
        }
        if (!string.Equals(hostId, request.HostId, StringComparison.Ordinal) || instanceId != request.InstanceId)
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.TargetChanged");
        }
        if (!platform.Capabilities().Areas.Contains(request.Read.Area))
        {
            return new(request.OperationId, EOutcome.NotExecuted, "Device.UnsupportedPlatform");
        }
        string fingerprint = ContractJson.Hash(request);
        bool startedOperation = false;
        try
        {
            using FileStream? lease = DeviceHostLease.TryAcquire(lockDirectory);
            if (lease is null)
            {
                return new(request.OperationId, EOutcome.NotExecuted, "Device.HostBusy");
            }
            DeviceOperationRecord? existing = await RecordAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal) ? existing.Result
                                                                                                  : new(request.OperationId, EOutcome.NotExecuted, "Device.OperationMismatch");
            }
            DeviceSnapshot before = await ReadAsync(request.Read, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(request.ExpectedRevision, before.Revision, StringComparison.Ordinal))
            {
                return new(request.OperationId, EOutcome.NotExecuted, "Device.SettingsChanged");
            }
            if (request.Action == EDeviceAction.Network)
            {
                DeviceNetworks.ValidateCurrent(DeviceSnapshots.Read<DeviceNetworkChange>(request.Change), DeviceSnapshots.Read<DeviceNetworkInterface>(before.Data));
            }
            if (request.Action == EDeviceAction.NetworkProtocols)
            {
                DeviceSnapshots.Read<DeviceNetworkProtocols>(request.Change).Validate(DeviceSnapshots.Read<DeviceNetworkInterface>(before.Data));
            }
            DeviceChangeResult pending = new(request.OperationId, EOutcome.Unknown, "Device.Started");
            DurableMutation mutation = new(scope, request.OperationId.ToString("D"), null, NodeValue.FromObject(new DeviceOperationRecord(fingerprint, pending)));
            DurableReceipt started = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [mutation], cancellationToken).ConfigureAwait(false);
            if (started.Outcome != EOutcome.Succeeded)
            {
                return new(request.OperationId, EOutcome.NotExecuted, "Device.RecordUnavailable");
            }
            startedOperation = true;
            return await CompleteAsync(request, before, fingerprint, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            EOutcome outcome = startedOperation ? EOutcome.Unknown
                                                : EOutcome.NotExecuted;
            return new(request.OperationId, outcome, "Device.PermissionDenied");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or InvalidOperationException or OperationCanceledException)
        {
            EOutcome outcome = startedOperation ? EOutcome.Unknown
                                                : EOutcome.NotExecuted;
            return new(request.OperationId, outcome, "Device.PreparationFailed");
        }
    }

    /// <summary>開始後の例外は不明に留め、元操作の結果保存を試みます。</summary>
    /// <returns>永続化できた結果、または再照会が必要な不明状態です。</returns>
    private async Task<DeviceChangeResult> CompleteAsync(DeviceChangeRequest request, DeviceSnapshot before, string fingerprint, CancellationToken cancellationToken = default)
    {
        DeviceChangeResult result;
        try
        {
            result = await platform.ApplyAsync(request, before, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == EOutcome.Succeeded)
            {
                DeviceSnapshot after = await platform.ReadAsync(request.Read, cancellationToken).ConfigureAwait(false);
                result = DeviceChangeConfirmation.Matches(request, after) ? result with { Revision = after.Revision }
                                                                          : result with { Outcome = EOutcome.Unknown, Reason = "Device.VerificationUnconfirmed" };
            }
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            result = new(request.OperationId, EOutcome.Unknown, "Device.ResultUnconfirmed");
        }
        try
        {
            using CancellationTokenSource save = new(TimeSpan.FromSeconds(10));
            EOutcome stored = await repository.WriteLocalSnapshotAsync(scope, request.OperationId.ToString("D"), NodeValue.FromObject(new DeviceOperationRecord(fingerprint, result)), save.Token).ConfigureAwait(false);
            if (stored == EOutcome.Succeeded)
            {
                return result;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            // 開始記録は残り、次の照会でも不明な変更を再実行しません。
        }
        return new(request.OperationId, EOutcome.Unknown, "Device.ResultSaveUnconfirmed");
    }

    /// <summary>元の開始記録を厳密に読みます。</summary>
    /// <returns>未開始はnull、破損は取得不能です。</returns>
    private async Task<DeviceOperationRecord?> RecordAsync(Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(scope, id.ToString("D"), cancellationToken).ConfigureAwait(false);
        return entry is null ? null
                             : DeviceSnapshots.Read<DeviceOperationRecord>(entry.Value!.Json);
    }
}
