using System.Collections.Concurrent;
using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Archives;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Archives;

/// <summary>画面やCLIが切断された後も、受け付けた書き出し・復元を実行し、結果を保存します。</summary>
public sealed class ClusterArchiveService : IDisposable
{
    private readonly ArchiveWorkspace workspace;
    private readonly ClusterArchiveState state;
    private readonly ArchiveImporter importer;
    private readonly WorkflowLogService log;
    private readonly PeerCluster cluster;
    private readonly Channel<Guid> jobs = Channel.CreateBounded<Guid>(1);
    private readonly ConcurrentDictionary<Guid, byte> running = [];
    private readonly SemaphoreSlim commands = new(1, 1);

    /// <summary>設定とログを操作するサービスを受け取り、アーカイブ用の保存先を設定します。</summary>
    public ClusterArchiveService(string directory, IDurableRepository repository, PeerCluster cluster, WorkflowStore workflows,
        ClusterConfigurationStore operational, WorkflowLogService log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workflows);
        ArgumentNullException.ThrowIfNull(operational);
        this.log = log;
        this.cluster = cluster;
        workspace = new(Path.Combine(directory, "cluster-archives"));
        state = new(cluster, workflows, operational);
        importer = new(repository, cluster, workflows, operational, log);
        importer.Register();
        ArchiveExport.Register(cluster, log);
    }

    /// <summary>書き出し条件を保存し、実行キューへ追加します。</summary>
    /// <returns>受付結果。同じ操作IDで再送された場合は、保存済みの結果。</returns>
    public async Task<ArchiveOperationResult> ExportAsync(ArchiveExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveExportRequest? known = await workspace.ReadAsync<ArchiveExportRequest>(request.OperationId, "export-request.json", cancellationToken).ConfigureAwait(false);
            if (known is not null)
            {
                RequireSame(known, request);
                return await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Archive result is missing.");
            }
            string upload = workspace.FileFor(request.OperationId, "upload.json");
            if (File.Exists(upload))
            {
                throw new InvalidOperationException("The operation belongs to an archive upload.");
            }
            RequireIdle();
            await workspace.WriteAsync(request.OperationId, "export-request.json", request, cancellationToken).ConfigureAwait(false);
            return await EnqueueAsync(request.OperationId, "queued-export", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>アップロードするファイルのサイズとSHA-256を登録します。</summary>
    /// <returns>転送を続ける位置を示す受信済みバイト数。</returns>
    public async Task<long> BeginUploadAsync(ArchiveTransfer request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string export = workspace.FileFor(request.OperationId, "export-request.json");
            if (File.Exists(export))
            {
                throw new InvalidOperationException("The operation belongs to an archive export.");
            }
            return await workspace.BeginAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>転送データを検証し、ファイルに追記します。</summary>
    /// <returns>受信済みのバイト数。</returns>
    public async Task<long> UploadAsync(ArchiveChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await workspace.AppendAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>アーカイブと復元先の設定を検証し、復元計画を保存します。</summary>
    /// <returns>追加・上書き・削除されるWFと確認トークン。</returns>
    public async Task<ArchiveImportPreview> PreviewAsync(ArchivePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireIdle();
            workspace.RequireNotStarted(request.OperationId);
            ArchiveImportPlan plan = await importer.PreviewAsync(request, workspace, cancellationToken).ConfigureAwait(false);
            await workspace.WriteAsync(request.OperationId, "plan.json", plan, cancellationToken).ConfigureAwait(false);
            await workspace.WriteAsync(request.OperationId, "preview.json", plan.Preview, cancellationToken).ConfigureAwait(false);
            return plan.Preview;
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>確認済みの計画で復元を開始します。同じ操作IDで再送された場合は、元の結果を返します。</summary>
    /// <returns>復元の受付結果、または保存済みの結果。</returns>
    public async Task<ArchiveOperationResult> ImportAsync(ArchiveImportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveImportRequest? known = await workspace.ReadAsync<ArchiveImportRequest>(request.OperationId, "import-request.json", cancellationToken).ConfigureAwait(false);
            if (known is not null)
            {
                RequireSame(known, request);
                return await ReadAsync(request.OperationId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Archive result is missing.");
            }
            RequireIdle();
            ArchiveImportPlan plan = await workspace.ReadAsync<ArchiveImportPlan>(request.OperationId, "plan.json", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Preview the archive before importing it.");
            if (!string.Equals(request.Confirmation, plan.Preview.Confirmation, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The archive confirmation does not match the preview.");
            }
            await workspace.WriteAsync(request.OperationId, "import-request.json", request, cancellationToken).ConfigureAwait(false);
            return await EnqueueAsync(request.OperationId, "queued-import", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>操作の状態を読み取ります。Coreの再起動で処理が中断していた場合は、結果不明として返します。</summary>
    /// <returns>操作の状態。記録がない場合はnull。</returns>
    public async Task<ArchiveOperationResult?> ReadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // 読取り中に完了した処理を中断扱いにしないよう、読取り前の稼働状態も保持します。
        bool active = running.ContainsKey(id);
        ArchiveOperationResult? result = await workspace.ReadAsync<ArchiveOperationResult>(id, "result.json", cancellationToken).ConfigureAwait(false);
        active |= running.ContainsKey(id);
        if (result is { Completed: false } && !active)
        {
            return result with { Completed = true, Outcome = EOutcome.Unknown, Reason = "Archive.Interrupted" };
        }
        string marker = workspace.FileFor(id, "started");
        if (result is null && File.Exists(marker))
        {
            if (active)
            {
                return new(id, "running", false, EOutcome.Unknown);
            }
            return new(id, "interrupted", true, EOutcome.Unknown, "Archive.ResultUnconfirmed");
        }
        string archive = workspace.FileFor(id, "archive.zip");
        if (result?.File is not null && !File.Exists(archive))
        {
            return result with { File = null, Reason = "Archive.ExportExpired" };
        }
        return result;
    }

    /// <summary>書き出し済みZIPから、指定位置のデータを読み取ります。</summary>
    /// <returns>転送1回分のデータ。</returns>
    public async Task<ArchiveChunk> ReadChunkAsync(ArchiveReadRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await workspace.ReadChunkAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>中断した復元の保存結果と反映状態を再確認します。</summary>
    /// <returns>再確認後の操作結果。</returns>
    public async Task<ArchiveOperationResult> ReconcileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireIdle();
            ArchiveOperationResult previous = await ReadAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Unknown archive operation.");
            if (previous.Outcome != EOutcome.Unknown)
            {
                return previous;
            }
            ArchiveImportPlan plan = await workspace.ReadAsync<ArchiveImportPlan>(id, "plan.json", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("This operation is not an archive import.");
            ArchiveOperationResult result = await importer.ReconcileAsync(plan, previous, cancellationToken).ConfigureAwait(false);
            await workspace.WriteAsync(id, "result.json", result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            commands.Release();
        }
    }

    /// <summary>受け付けた操作を実行します。再起動前の未完了操作は自動で再開しません。</summary>
    /// <returns>CoreServiceの終了まで続くバックグラウンド処理。</returns>
    // 抑制理由: アーカイブ処理の失敗でCoreService全体が停止しないよう、例外を記録して操作を終了します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "補助処理の最上位境界で全障害を診断し、元の未完了記録を保持します。")]
    public async Task RunAsync(Action<Exception> diagnose, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnose);
        await foreach (Guid id in jobs.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            ArchiveOperationResult? latest = null;
            try
            {
                latest = await workspace.ReadAsync<ArchiveOperationResult>(id, "result.json", cancellationToken).ConfigureAwait(false);
                ArchiveExportRequest? export = await workspace.ReadAsync<ArchiveExportRequest>(id, "export-request.json", cancellationToken).ConfigureAwait(false);
                ArchiveOperationResult result;
                if (export is not null)
                {
                    result = await ArchiveExport.WriteAsync(export, state, log, cluster, workspace, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    ArchiveImportPlan plan = await workspace.ReadAsync<ArchiveImportPlan>(id, "plan.json", cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Archive plan is missing.");
                    result = await importer.ApplyAsync(plan, workspace, async (value, token) =>
                    {
                        await workspace.WriteAsync(id, "result.json", value, token).ConfigureAwait(false);
                        latest = value;
                    }, cancellationToken).ConfigureAwait(false);
                }
                await workspace.WriteAsync(id, "result.json", result, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                diagnose(exception);
                try
                {
                    EOutcome outcome = latest?.Stage switch { "queued-export" or "queued-import" => EOutcome.NotExecuted, _ => EOutcome.Unknown };
                    await workspace.WriteAsync(id, "result.json", new ArchiveOperationResult(id, latest?.Stage ?? "interrupted", true, outcome,
                        "Archive." + exception.GetType().Name, Steps: latest?.Steps), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception storageFailure)
                {
                    // 結果の保存にも失敗した場合は診断を残し、CoreServiceの稼働を続けます。
                    diagnose(storageFailure);
                }
            }
            finally
            {
                running.TryRemove(id, out _);
            }
            try
            {
                await commands.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await workspace.CleanAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    commands.Release();
                }
            }
            catch (Exception cleanupFailure)
            {
                diagnose(cleanupFailure);
            }
        }
    }

    /// <summary>受付処理に使用したセマフォを解放します。</summary>
    public void Dispose() => commands.Dispose();

    /// <summary>開始済みの記録を作成してから、操作を実行キューへ追加します。</summary>
    /// <returns>操作の受付結果。</returns>
    private async Task<ArchiveOperationResult> EnqueueAsync(Guid id, string stage, CancellationToken cancellationToken = default)
    {
        workspace.Claim(id);
        ArchiveOperationResult result = new(id, stage, false, EOutcome.Unknown);
        running.TryAdd(id, 0);
        bool queued = false;
        try
        {
            await workspace.WriteAsync(id, "result.json", result, cancellationToken).ConfigureAwait(false);
            queued = jobs.Writer.TryWrite(id);
            if (!queued)
            {
                throw new InvalidOperationException("Archive worker is unavailable.");
            }
            return result;
        }
        finally
        {
            if (!queued)
            {
                running.TryRemove(id, out _);
            }
        }
    }

    /// <summary>別のアーカイブ操作が実行中なら、新しい操作を拒否します。</summary>
    private void RequireIdle()
    {
        if (!running.IsEmpty)
        {
            throw new InvalidOperationException("Another archive operation is running.");
        }
    }

    /// <summary>同じ操作IDで異なる内容が送られた場合は拒否します。</summary>
    private static void RequireSame<T>(T known, T request)
    {
        if (!string.Equals(ContractJson.Hash(known), ContractJson.Hash(request), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The archive operation was changed.");
        }
    }
}
