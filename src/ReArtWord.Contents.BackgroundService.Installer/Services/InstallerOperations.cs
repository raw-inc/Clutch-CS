using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Json;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Installer.Services;

/// <summary>確認済み対象の配置操作と既存分散Updaterを接続します。</summary>
internal static class InstallerOperations
{
    /// <summary>Webクライアントを、Coreとは別の権限で導入します。</summary>
    /// <returns>起動確認済みの導入結果です。</returns>
    public static async Task<SetupResult> InstallClientAsync(WebClientSetupJob job, string updaterRoot, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        WebClientSettings selection = job.Settings.WebClient ?? throw new InvalidDataException("Select a web client.");
        bool perUser = !selection.RunAsService;
        if (selection.ExistingRoot is not null)
        {
            perUser = (await WebClientCatalog.ReadAsync(selection.ExistingRoot, cancellationToken).ConfigureAwait(false)).PerUser;
        }
        progress?.Invoke(new("prepare-helper"));
        string helper = await SetupWorkspace.PrepareAsync(job.OperationId, job, updaterRoot, cancellationToken).ConfigureAwait(false);
        SetupResult result = await RunHelperAsync(job.OperationId, job.Settings.Root, helper, OperatingSystem.IsWindows() && !perUser, "--setup-web-client", progress, cancellationToken).ConfigureAwait(false);
        if (result.Outcome == EOutcome.Succeeded && result.WebClient is { RunAsService: false } client)
        {
            return await FinishClientAsync(result, client, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>ローカルの一操作を独立helperで完了させます。</summary>
    /// <returns>保存された最終結果です。</returns>
    public static async Task<SetupResult> LocalAsync(SetupJob job, string updaterRoot, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        progress?.Invoke(new("prepare-helper"));
        string helper = await SetupWorkspace.PrepareAsync(job, updaterRoot, cancellationToken).ConfigureAwait(false);
        return await RunHelperAsync(job.OperationId, job.Settings.Root, helper, OperatingSystem.IsWindows() && !job.Settings.PerUser, "--setup-job", progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>このPCのクライアントだけを独立helperで削除します。</summary>
    /// <returns>保存された最終結果です。</returns>
    public static async Task<SetupResult> RemoveClientAsync(WebClientRemovalJob job, string updaterRoot, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        WebClientInstallation current = await WebClientCatalog.ReadAsync(job.Installation.Root, false, cancellationToken).ConfigureAwait(false);
        if (current != job.Installation)
        {
            throw new InvalidDataException("The web client installation has changed. Select it again.");
        }
        progress?.Invoke(new("prepare-helper"));
        string helper = await SetupWorkspace.PrepareAsync(job.OperationId, job, updaterRoot, cancellationToken).ConfigureAwait(false);
        if (!job.Installation.RunAsService)
        {
            await WebClientStartup.UnregisterAsync(job.Installation, cancellationToken).ConfigureAwait(false);
        }
        return await RunHelperAsync(job.OperationId, job.Installation.Root, helper, OperatingSystem.IsWindows() && !job.Installation.PerUser, "--remove-web-client", progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>利用者の起動登録まで確認してから、画面と保存結果を確定します。</summary>
    /// <returns>登録の失敗を含む最終結果です。</returns>
    private static async Task<SetupResult> FinishClientAsync(SetupResult result, WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        try
        {
            await WebClientStartup.RegisterAsync(client, cancellationToken).ConfigureAwait(false);
            result = result with { Stage = "ready", Detail = "The web client is ready." };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            result = result with { Outcome = EOutcome.Failed, Stage = "web-client-startup", Detail = exception.Message };
        }
        return await new SetupOperationJournal(result.OperationId, SetupWorkspace.DirectoryFor(result.OperationId), result.Root).CompleteAsync(result, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>helperの進捗を読み、終了後に実結果だけを返します。</summary>
    /// <returns>確認できた最終結果です。</returns>
    private static async Task<SetupResult> RunHelperAsync(Guid operationId, string root, string helper, bool elevate, string option, Action<OperationProgress>? progress, CancellationToken cancellationToken = default)
    {
        string workspace = SetupWorkspace.DirectoryFor(operationId);
        progress?.Invoke(new("launch-helper"));
        using Process process = SetupWorkspace.Launch(helper, Path.Combine(workspace, "job.json"), elevate, option);
        while (!process.HasExited)
        {
            OperationProgress? current = await SetupProgressJournal.ReadAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                progress?.Invoke(current);
            }
            await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        OperationProgress? final = await SetupProgressJournal.ReadAsync(workspace, cancellationToken).ConfigureAwait(false);
        if (final is not null)
        {
            progress?.Invoke(final);
        }
        string result = Path.Combine(workspace, "result.json");
        if (!File.Exists(result))
        {
            return new(operationId, EOutcome.Unknown, "helper", "No final result was saved. Inspect " + workspace, root);
        }
        return JsonSerializer.Deserialize<SetupResult>(await File.ReadAllTextAsync(result, cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
    }

    /// <summary>既存の一Peerに管理対象の配置情報を照会します。</summary>
    /// <returns>稼働中のPeerから取得した配置記録です。</returns>
    public static async Task<InstallationRecord> InspectAsync(ObservedTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Installation is not null)
        {
            return target.Installation;
        }
        UpdateObserver observer = new(target.Binding, target.ClusterId);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        Contracts.Cluster.PeerInfo peer = await observer.FindAsync(target.PeerId, target.Peer?.InstanceId, cancellationToken).ConfigureAwait(false);
        Contracts.Management.ManagementReply reply = await observer.SendAsync(target.PeerId, peer.InstanceId, "setup.info", null, true, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new IOException("This peer has no compatible installer management endpoint. Update it or select its local installation folder.");
        }
        return reply.Value.Json.Deserialize<InstallationRecord>(ContractJson.Options)!;
    }

    /// <summary>遠隔の一操作を開始し、Core停止後はhelperの結果だけを照会します。</summary>
    /// <returns>実結果または確認不能です。</returns>
    public static async Task<SetupResult> RemoteAsync(ObservedTarget target, RemoteSetupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(request);
        UpdateObserver observer = new(target.Binding, target.ClusterId);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = observer.ConfigureAwait(false);
        Contracts.Cluster.PeerInfo peer = await observer.FindAsync(target.PeerId, target.Peer?.InstanceId, cancellationToken).ConfigureAwait(false);
        SetupResultChannel results = new(target.Binding, target.ClusterId, request.OperationId, false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable resultLifetime = results.ConfigureAwait(false);
        Contracts.Management.ManagementReply reply = await observer.SendAsync(target.PeerId, peer.InstanceId, "setup.start", UpdateObserver.Payload(request), false, cancellationToken).ConfigureAwait(false);
        SetupResult? accepted = reply.Value?.Json.Deserialize<SetupResult>(ContractJson.Options);
        if (accepted is { Finished: true })
        {
            return accepted;
        }
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(InstallationDefaults.RemoteTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            SetupResult? result = await results.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result is not null && result.OperationId == request.OperationId)
            {
                return result;
            }
            await Task.Delay(InstallationDefaults.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        return new(request.OperationId, EOutcome.Unknown, "remote", "The final result could not be confirmed. Do not retry automatically; inspect the target PC's SetupOperations log.", target.Label);
    }

    /// <summary>既存の全検証・個別切り戻し・耐久結果を維持して更新します。</summary>
    /// <returns>全対象の確定または未確認状態です。</returns>
    public static async Task<ClusterUpdateStatus> UpdateAsync(IReadOnlyList<ObservedTarget> targets, string payload, bool allowActive, Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0 || targets.Any(target => target.Peer is null) || targets.Select(target => (target.ClusterId, target.Binding)).Distinct().Count() != 1)
        {
            throw new InvalidDataException("Select observed peers in one cluster.");
        }
        Guid operation = Guid.NewGuid();
        string journal = SetupWorkspace.DirectoryFor(operation);
        Directory.CreateDirectory(journal);
        ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(payload, cancellationToken).ConfigureAwait(false);
        ClusterUpdateRequest request = new(operation, manifest, payload, targets.Select(target => target.PeerId).ToArray(), EUpdateFailurePolicy.KeepCompatibleMixed,
            AllowWhileActive: allowActive, RemainingOperationConfirmed: allowActive);
        ClusterUpdateJob job = new(request, targets.ToDictionary(target => target.PeerId, target => target.Peer!.InstanceId, StringComparer.Ordinal), targets[0].Binding, targets[0].ClusterId, Path.Combine(journal, "updates.json"), "json", "installer");
        await InstallationFiles.WriteAsync(Path.Combine(journal, "cluster-job.json"), job, cancellationToken).ConfigureAwait(false);

        UdpUpdatePeerTransport transport = new(job);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = transport.ConfigureAwait(false);
        InstallerUpdateProgress observed = new(transport, request.PeerIds, progress);
        transport.TransferProgress = observed.Transfer;
        return await new ClusterUpdateCoordinator(new JsonFileRepository(job.RepositoryPath), observed).ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
