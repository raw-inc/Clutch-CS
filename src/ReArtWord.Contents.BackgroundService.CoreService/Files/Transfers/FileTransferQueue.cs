using System.Threading.Channels;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.CoreService.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>宛先ピアの共有FIFOを所有し、受付をCoreの保存先へ記録します。</summary>
internal sealed class FileTransferQueue(FileCopyWorker worker, CommonConfiguration configuration, TransferConnections connections, Infrastructure.Cluster.PeerCluster cluster) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Lock sync = new();
    private readonly SemaphoreSlim changes = new(1, 1);
    private readonly Dictionary<Guid, FileTransferJob> jobs = [];
    private readonly Channel<Guid> pending = Channel.CreateBounded<Guid>(32);
    private readonly FileTransferJournal journal = new(Path.Combine(configuration.DataDirectory, "transfer-queue"));
    private bool ready;

    /// <summary>キューの所有設定と準備状態を公開し、追加は元要求の永続受付を経由します。</summary>
    internal void Register(ManagementRegistry registry)
    {
        registry.ReadValue("transfer.queue.info", _ => new FileTransferQueueInfo(configuration.TransferQueueEnabled, Volatile.Read(ref ready)));
        registry.ReadValue("transfer.queue.list", _ =>
        {
            if (configuration.TransferQueueEnabled && !Volatile.Read(ref ready))
            {
                throw new InvalidOperationException("The shared queue could not confirm its storage or networks.");
            }
            return Read();
        });
        registry.Write("transfer.queue.enqueue", async (value, token) => await EnqueueAsync(ManagementRegistry.Body<FileTransferRequest>(value), token).ConfigureAwait(false));
    }

    /// <summary>受理した対象を固定し、保存できた要求だけをFIFOへ追加します。</summary>
    /// <returns>全画面が参照できる受付済みジョブです。</returns>
    internal async Task<FileTransferJob> EnqueueAsync(FileTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationDirectory);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.SourcePath.Length, 4096);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.DestinationDirectory.Length, 4096);
        if (8192 < System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, Contracts.Serialization.ContractJson.Options).Length)
        {
            throw new ArgumentException("The encoded transfer request exceeds 8 KiB.", nameof(request));
        }
        if (!string.Equals(request.Destination.ClusterId, cluster.LocalClusterId, StringComparison.Ordinal)
            || !string.Equals(request.Destination.PeerId, cluster.LocalPeerId, StringComparison.Ordinal) || request.Destination.InstanceId != cluster.LocalInstanceId)
        {
            throw new ArgumentException("The shared queue must belong to the original destination peer.", nameof(request));
        }
        if (request.Id == Guid.Empty || request.Source.InstanceId == Guid.Empty || request.Destination.InstanceId == Guid.Empty)
        {
            throw new ArgumentException("Fixed job and peer identities are required.", nameof(request));
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Volatile.Read(ref ready))
            {
                throw new InvalidOperationException("Shared transfer queue is not ready.");
            }
            FileTransferJob job;
            using (sync.EnterScope())
            {
                Trim();
                if (jobs.TryGetValue(request.Id, out FileTransferJob? existing))
                {
                    if (existing.Request != request)
                    {
                        throw new ArgumentException("Job ID already has another request.", nameof(request));
                    }
                    return existing;
                }
                if (32 <= pending.Reader.Count)
                {
                    throw new InvalidOperationException("The shared queue is full (32 pending copies).");
                }
                job = new(request, "Queued", 0, 0, DateTimeOffset.UtcNow);
                jobs.Add(request.Id, job);
            }
            bool saved = false;
            try
            {
                await journal.SaveAsync(Read(), cancellationToken).ConfigureAwait(false);
                saved = true;
            }
            finally
            {
                if (!saved)
                {
                    using (sync.EnterScope())
                    {
                        jobs.Remove(request.Id);
                    }
                }
            }
            if (!pending.Writer.TryWrite(request.Id))
            {
                throw new InvalidOperationException("The shared queue stopped accepting work.");
            }
            return job;
        }
        finally
        {
            changes.Release();
        }
    }

    /// <summary>全PCへ同じ有限のキューと進捗を返します。</summary>
    /// <returns>進行中・待機中・完了履歴です。</returns>
    internal FileTransferJob[] Read()
    {
        using (sync.EnterScope())
        {
            Trim();
            return jobs.Values.OrderBy(job => job.UpdatedAt).ToArray();
        }
    }

    /// <summary>ClientHostと独立して保存済みキューを実行します。</summary>
    /// <returns>Core停止までの実行です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            Volatile.Write(ref ready, false);
        }
    }

    /// <summary>保存境界を確認できたキューだけを実行し、障害はこの機能へ限定します。</summary>
    /// <returns>共有キューの実行期間です。</returns>
    private async Task RunAsync(CancellationToken stoppingToken = default)
    {
        if (!configuration.TransferQueueEnabled)
        {
            return;
        }
        await connections.InitializeAsync(stoppingToken).ConfigureAwait(false);
        FileTransferJob[] restored = await journal.LoadAsync(stoppingToken).ConfigureAwait(false);
        using (sync.EnterScope())
        {
            foreach (FileTransferJob job in restored)
            {
                jobs.Add(job.Request.Id, job);
            }
            Trim();
            foreach (FileTransferJob job in restored.Where(job => job.State is "Queued"))
            {
                if (!pending.Writer.TryWrite(job.Request.Id))
                {
                    throw new InvalidDataException("The saved queue exceeds its limit.");
                }
            }
        }
        await journal.SaveAsync(Read(), stoppingToken).ConfigureAwait(false);
        Volatile.Write(ref ready, true);
        try
        {
            await foreach (Guid id in pending.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await changes.WaitAsync(stoppingToken).ConfigureAwait(false);
                FileTransferJob job;
                try
                {
                    using (sync.EnterScope())
                    {
                        job = jobs[id] with
                        {
                            State = "Copying",
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        jobs[id] = job;
                    }
                    await journal.SaveAsync(Read(), stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    changes.Release();
                }
                FileTransferJob result;
                try
                {
                    await connections.PrepareAsync(job.Request, stoppingToken).ConfigureAwait(false);
                    result = await worker.CopyAsync(job, Update, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ArgumentException)
                {
                    result = job with
                    {
                        State = "Failed",
                        Problem = "The queue Core could not discover the original transfer peers on its configured networks.",
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                }
                finally
                {
                    await connections.ReleaseTemporaryAsync(CancellationToken.None).ConfigureAwait(false);
                }
                await changes.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    Update(result);
                    await journal.SaveAsync(Read(), CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    changes.Release();
                }
            }
        }
        finally
        {
            Volatile.Write(ref ready, false);
        }
    }

    /// <summary>各転送片の進捗を不変の値へ交換します。</summary>
    private void Update(FileTransferJob job)
    {
        using (sync.EnterScope())
        {
            jobs[job.Request.Id] = job;
        }
    }

    /// <summary>完了履歴を100件・1時間に制限します。</summary>
    private void Trim()
    {
        FileTransferJob[] finished = jobs.Values.Where(job => job.State is "Succeeded" or "Failed" or "Unknown").OrderByDescending(job => job.UpdatedAt).ToArray();
        foreach (FileTransferJob job in finished.Where((job, index) => 100 <= index || TimeSpan.FromHours(1) < DateTimeOffset.UtcNow - job.UpdatedAt))
        {
            jobs.Remove(job.Request.Id);
        }
    }

    /// <summary>Coreの停止完了後に同期資源を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        changes.Dispose();
    }
}
