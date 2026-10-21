using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Health;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Runtime.Updates;

namespace ReArtWord.Contents.BackgroundService.CoreService.Updates;

/// <summary>全体更新の固定入力と開始意図を保存して独立helperへ渡します。</summary>
internal sealed class ClusterUpdateLauncher : IUpdateScheduleLauncher
{
    private readonly ServiceRuntime runtime;
    private readonly LocalUpdateAdapter adapter;
    private UpdatesOptions Options => adapter.Options;

    /// <summary>現在のHostと明示更新設定を接続します。</summary>
    public ClusterUpdateLauncher(ServiceRuntime runtime, LocalUpdateAdapter adapter)
    {
        this.runtime = runtime;
        this.adapter = adapter;
    }

    /// <summary>一度だけ外部helperを起動し受理を最終成功にしません。</summary>
    /// <returns>保存済み結果、または最終確認待ちです。</returns>
    public Task<ClusterUpdateStatus> StartAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default)
        => StartCoreAsync(request, null, cancellationToken);

    /// <summary>予約時の実体を、新しいPeer実体へ自動で差し替えません。</summary>
    /// <returns>元更新の受付または確認結果です。</returns>
    public Task<ClusterUpdateStatus> StartScheduledAsync(ClusterUpdateRequest request, IReadOnlyDictionary<string, Guid> instances, CancellationToken cancellationToken = default)
        => StartCoreAsync(request, instances, cancellationToken);

    /// <summary>起動前の検証を予約登録にも共用します。</summary>
    /// <returns>今回確認した対象実体です。</returns>
    // 抑制理由: 全配布物と全対象の検証が揃う前に、部分的な対象集合を予約へ渡せません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW160", Justification = "全体の検証完了を示す固定スナップショットであり、逐次列挙ではありません。")]
    public async Task<IReadOnlyDictionary<string, Guid>> ValidateAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default)
    {
        string launchScope = "peer:" + runtime.Cluster.LocalPeerId + ":update-launch";
        string operationKey = request.OperationId.ToString("N");
        if (await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false) is not null
            || await runtime.Repository.ReadAsync(launchScope, operationKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidDataException("開始済み更新IDを新しい予約へ再利用できません。");
        }
        (string _, Dictionary<string, Guid> instances) = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        return instances;
    }

    /// <summary>準備済みjobと開始意図を保存して、一つのhelperへ渡します。</summary>
    /// <returns>受付を最終成功にしない更新状態です。</returns>
    private async Task<ClusterUpdateStatus> StartCoreAsync(ClusterUpdateRequest request, IReadOnlyDictionary<string, Guid>? expectedInstances, CancellationToken cancellationToken = default)
    {
        ReleaseSelector.Validate(request);
        ClusterUpdateStatus? existing = await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(ContractJson.Hash(existing.Request), ContractJson.Hash(request), StringComparison.Ordinal))
            {
                throw new InvalidDataException("同じ更新操作の入力が異なります。");
            }
            return existing;
        }
        UpdateLayout layout = new(Options, request.OperationId);
        (string helper, Dictionary<string, Guid> endpoints) = await InspectAsync(request, cancellationToken).ConfigureAwait(false);
        if (expectedInstances is not null && (expectedInstances.Count != endpoints.Count || expectedInstances.Any(peer => endpoints.GetValueOrDefault(peer.Key) != peer.Value)))
        {
            return new(request, EOutcome.NotExecuted, true, [], "update.scheduleTargetChanged");
        }
        string repositoryName = "state.db";
        string storageBackend = "sqlite";
        PersistenceOptions persistence = runtime.Configuration.Persistence;
        EStorageBackend backend = persistence.Purposes?.GetValueOrDefault(EStoragePurpose.Runtime, persistence.DefaultBackend) ?? persistence.DefaultBackend;
        if (backend == EStorageBackend.Json)
        {
            repositoryName = "state.json";
            storageBackend = "json";
        }
        ClusterUpdateJob job = new(ContractJson.Copy(request), endpoints, UpdateObserver.Binding(Options), Options.ClusterId ?? runtime.Configuration.Cluster.Id, Path.Combine(runtime.Configuration.DataDirectory, repositoryName), storageBackend, runtime.Cluster.LocalPeerId);
        layout.CreateDirectories();
        string jobPath = Path.Combine(layout.OperationRoot, "cluster-job.json");
        layout.Validate(jobPath);
        if (File.Exists(jobPath))
        {
            ClusterUpdateJob prior = JsonSerializer.Deserialize<ClusterUpdateJob>(await File.ReadAllTextAsync(jobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("保存jobが不正です。");
            if (!string.Equals(ContractJson.Hash(prior), ContractJson.Hash(job), StringComparison.Ordinal))
            {
                throw new InvalidDataException("保存jobの入力が異なります。");
            }
        }
        else
        {
            using FileStream output = new(jobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(output, job, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        }
        string scope = "peer:" + runtime.Cluster.LocalPeerId + ":update-launch";
        DurableReceipt reserved = await runtime.Repository.ApplyBatchAsync(Guid.CreateVersion7(), [
            new DurableMutation(scope, request.OperationId.ToString("N"), null, NodeValue.FromObject(ContractJson.Hash(job))),
            new DurableMutation("peer:" + runtime.Cluster.LocalPeerId + ":update-hook-owner", request.OperationId.ToString("N"), null, NodeValue.FromObject(request.OperationId)),
        ], cancellationToken).ConfigureAwait(false);
        if (reserved.Outcome != EOutcome.Succeeded)
        {
            return new ClusterUpdateStatus(request, EOutcome.Unknown, false, [], "update.dispatchAlreadyReserved.manualInspectionRequired");
        }
        ProcessStartInfo start = new(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add("--cluster-job");
        start.ArgumentList.Add(jobPath);
        using Process process = Process.Start(start) ?? throw new IOException("Cluster updaterを開始できません。");
        return new ClusterUpdateStatus(request, EOutcome.Unknown, false, [], "update.accepted.awaitFinalConfirmation");
    }

    /// <summary>配布物・所有領域・現在の参加実体を検証し、更新開始は行いません。</summary>
    /// <returns>検証済みhelperと固定対象です。</returns>
    private async Task<(string Helper, Dictionary<string, Guid> Instances)> InspectAsync(ClusterUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ReleaseSelector.Validate(request);
        if (request.OperationId == Guid.Empty || request.PeerIds is null || request.PeerIds.Count == 0 || request.PeerIds.Distinct(StringComparer.Ordinal).Count() != request.PeerIds.Count)
        {
            throw new InvalidDataException("更新操作IDと固有の対象Peerが必要です。");
        }
        if (!Enum.IsDefined(request.FailurePolicy) || !Enum.IsDefined(request.RollbackFailurePolicy) || request.PeerTimeoutMilliseconds <= 0)
        {
            throw new InvalidDataException("更新失敗方針と有限の確認期限を指定してください。");
        }
        UpdateLayout layout = new(Options, request.OperationId);
        string helper = await BundledUpdater.ResolveAsync(Options, request.OperationId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Updater実行物が未設定です。");
        if (!Path.IsPathFullyQualified(helper) || !File.Exists(helper) || UpdateLayout.Contains(layout.InstallationRoot, helper))
        {
            throw new InvalidDataException("installation外の明示Updater実行物が必要です。");
        }
        if (!UpdateLayout.Contains(layout.StagingRoot, request.PayloadRoot) || UpdateLayout.Contains(layout.InstallationRoot, runtime.Configuration.DataDirectory))
        {
            throw new InvalidDataException("payloadはstaging内、永続状態はinstallation外に必要です。");
        }
        if (request.Variants is null)
        {
            await ReleaseVerifier.VerifyAsync(request.Manifest, request.PayloadRoot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (ReleaseVariant variant in request.Variants)
            {
                ClusterUpdateRequest selected = ReleaseSelector.Select(request, variant.Manifest.Platform);
                await ReleaseVerifier.VerifyAsync(selected.Manifest, selected.PayloadRoot, cancellationToken).ConfigureAwait(false);
            }
        }
        Dictionary<string, Guid> endpoints = new(StringComparer.Ordinal);
        foreach (string peerId in request.PeerIds)
        {
            Guid endpoint = runtime.Cluster.GetPeers().Single(peer => string.Equals(peer.PeerId, peerId, StringComparison.Ordinal)).InstanceId;
            endpoints.Add(peerId, endpoint);
        }
        return (helper, endpoints);
    }

    /// <summary>最終結果を永続状態から読み、helperを再起動しません。</summary>
    /// <returns>保存された全体結果です。</returns>
    public async Task<ClusterUpdateStatus?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await runtime.Repository.ReadAsync("updates", operationId.ToString("N"), cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<ClusterUpdateStatus>(ContractJson.Options);
    }
}
