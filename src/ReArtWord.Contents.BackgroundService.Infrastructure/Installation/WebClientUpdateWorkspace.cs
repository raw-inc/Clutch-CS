using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Webクライアント更新の入力と転送先を固定し、既存のTCP転送へ接続します。</summary>
public sealed class WebClientUpdateWorkspace(WebClientInstallation installation, UpdatesOptions network, string peerId, Guid instanceId)
{
    /// <summary>プログラムと設定の外に、クライアント所有の操作領域を用意します。</summary>
    /// <returns>転送とhelperで共通に使う検証済み配置です。</returns>
    public UpdateLayout Layout(Guid operationId) => new(Options(), operationId);

    /// <summary>一回の要求、導入記録、稼働実体を改変できない入力として保存します。</summary>
    /// <returns>Updaterへ渡す固定ジョブです。</returns>
    public async Task<WebClientUpdateJob> PrepareAsync(WebClientUpdateRequest request, WebClientUpdateInfo current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(current);
        ClientUpdateExecutor.ValidateRelease(request.Manifest);
        if (request.InstallationId != installation.InstallationId || current.Installation != installation)
        {
            throw new InvalidDataException("The selected web client installation changed.");
        }
        UpdateLayout layout = Layout(request.OperationId);
        layout.CreateDirectories();
        WebClientUpdateJob job = new(request.OperationId, installation, current.Health.Process, request.Manifest, layout.PayloadRoot);
        if (File.Exists(layout.JobPath))
        {
            WebClientUpdateJob existing = await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ContractJson.Hash(existing), ContractJson.Hash(job), StringComparison.Ordinal))
            {
                throw new InvalidDataException("This web client update already has different preparation data.");
            }
            return existing;
        }
        LocalUpdateJob transfer = new(request.OperationId, peerId, instanceId, current.Health.Process, request.Manifest, layout.PayloadRoot, Options(), []);
        using FileStream once = new(layout.JobPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(once, job, ContractJson.Options, cancellationToken).ConfigureAwait(false);
        await once.FlushAsync(cancellationToken).ConfigureAwait(false);
        await InstallationFiles.WriteAsync(Path.Combine(layout.OperationRoot, "transfer.json"), transfer, cancellationToken).ConfigureAwait(false);
        return job;
    }

    /// <summary>同じ対象の保存入力だけを読み、転送先を外部指定で置き換えません。</summary>
    /// <returns>保存済みの更新入力です。</returns>
    public async Task<WebClientUpdateJob> ReadAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        UpdateLayout layout = Layout(operationId);
        layout.Validate(layout.JobPath);
        WebClientUpdateJob job = JsonSerializer.Deserialize<WebClientUpdateJob>(await File.ReadAllTextAsync(layout.JobPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("The web client update input is missing.");
        if (job.OperationId != operationId || job.Installation.InstallationId != installation.InstallationId
            || !string.Equals(job.Installation.Root, installation.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(job.PayloadRoot, layout.PayloadRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The web client update scope changed.");
        }
        return job;
    }

    /// <summary>Core更新と同じTCP転送・ハッシュ検証に渡す固定情報を読みます。</summary>
    /// <returns>この操作にだけ属する転送入力です。</returns>
    public async Task<LocalUpdateJob> ReadTransferAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        WebClientUpdateJob job = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        UpdateLayout layout = Layout(operationId);
        string path = Path.Combine(layout.OperationRoot, "transfer.json");
        layout.Validate(path);
        LocalUpdateJob transfer = JsonSerializer.Deserialize<LocalUpdateJob>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
            ?? throw new InvalidDataException("The web client transfer input is missing.");
        if (transfer.OperationId != operationId || transfer.PreviousInstanceId != instanceId
            || !string.Equals(ContractJson.Hash(transfer.Manifest), ContractJson.Hash(job.Manifest), StringComparison.Ordinal)
            || !string.Equals(ContractJson.Hash(transfer.Options), ContractJson.Hash(Options()), StringComparison.Ordinal)
            || !string.Equals(transfer.PayloadRoot, layout.PayloadRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The web client transfer scope changed.");
        }
        return transfer;
    }

    /// <summary>転送内容の検証後にだけ、同梱された独立Updaterを配置します。</summary>
    /// <returns>固定した操作を実行するhelperです。</returns>
    public async Task<string> PrepareHelperAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        WebClientUpdateJob job = await ReadAsync(operationId, cancellationToken).ConfigureAwait(false);
        UpdateLayout layout = Layout(operationId);
        await ReleaseVerifier.VerifyAsync(job.Manifest, job.PayloadRoot, cancellationToken).ConfigureAwait(false);
        await LocalUpdateExecutor.WriteManifestAsync(job.Manifest, job.PayloadRoot, cancellationToken).ConfigureAwait(false);
        return await BundledUpdater.ResolveAsync(job.PayloadRoot, Path.Combine(layout.OperationRoot, "helper"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The web client release has no bundled updater.");
    }

    /// <summary>ネットワーク条件だけを引き継ぎ、Coreの入替先を使用しません。</summary>
    /// <returns>Webクライアント専用の更新境界です。</returns>
    private UpdatesOptions Options() => network with
    {
        InstallationRoot = WebClientCatalog.ProgramRoot(installation), StagingRoot = Path.Combine(installation.Root, "update-staging"),
        BackupRoot = Path.Combine(installation.Root, "update-backups"), ServiceKind = EUpdateServiceKind.DirectProcess,
    };
}
