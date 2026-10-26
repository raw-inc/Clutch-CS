using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Logs;

/// <summary>本文を保存せず、一回限りの出力受付・取消・進捗だけを有限期間保持します。</summary>
internal sealed class LogExportJobs : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly Func<IReadOnlyList<DiscoveredCluster>> discover;
    private readonly Func<FileTransferPeer, LogQuery, CancellationToken, Task<LogQueryResult?>> read;
    private readonly Func<FileTransferPeer, LogNamesQuery, CancellationToken, Task<LogNames>> names;
    private readonly Lock gate = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<Guid, Job> jobs = [];
    private CancellationToken stopping;

    /// <summary>既存の管理通信で読み取り、追加のポートや一時ファイルを使用しません。</summary>
    public LogExportJobs(ObserverConnection connection, PeerManagementClient remote)
        : this(connection.GetClusters,
            async (peer, query, token) => await remote.SendAsync<LogQueryResult>(peer, "logs.query", query, true, token).ConfigureAwait(false),
            (peer, query, token) => remote.SendAsync<LogNames>(peer, "logs.names", query, true, token))
    {
    }

    /// <summary>試験でも同じ寿命管理を使い、観測先だけを隔離できます。</summary>
    internal LogExportJobs(Func<IReadOnlyList<DiscoveredCluster>> discover,
        Func<FileTransferPeer, LogQuery, CancellationToken, Task<LogQueryResult?>> read,
        Func<FileTransferPeer, LogNamesQuery, CancellationToken, Task<LogNames>> names, TimeProvider? timeProvider = null)
    {
        this.discover = discover;
        this.read = read;
        this.names = names;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>検索条件・上限時刻・取得先を固定し、ダウンロード接続を最大2分待ちます。</summary>
    /// <returns>一回の送信だけに使える識別子です。</returns>
    // 抑制理由: 受付成功後のJobは辞書が所有し、期限切れまたは停止時に回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "取消資源を所有辞書へ移します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "辞書へ所有権を移し、期限切れまたは停止時に破棄します。")]
    internal LogExportStatus Create(LogExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        if (!Enum.IsDefined(request.Format))
        {
            throw new ArgumentException("Unsupported log export format.", nameof(request));
        }
        _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId);
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset until = request.Query.Until ?? now;
        if (now < until)
        {
            until = now;
        }
        if (until < request.Query.From)
        {
            throw new ArgumentException("Invalid log export time range.", nameof(request));
        }
        Dictionary<LogSearchSource, FileTransferPeer> targets = discover().SelectMany(cluster => cluster.Peers.Select(peer =>
            new KeyValuePair<LogSearchSource, FileTransferPeer>(new(cluster.ClusterId, peer.PeerId), new(cluster.ClusterId, peer.PeerId, peer.InstanceId))))
            .Where(pair => request.Source is null || pair.Key == request.Source).DistinctBy(pair => pair.Key).ToDictionary();
        if (targets.Count is 0 or > 256)
        {
            throw new ArgumentException("Select between 1 and 256 observed peers.", nameof(request));
        }
        LogExportRequest fixedRequest = request with { Query = request.Query with { Until = until, Cursor = null, Limit = 1000, MaximumBytes = 1048576 } };
        using (gate.EnterScope())
        {
            stopping.ThrowIfCancellationRequested();
            Trim();
            if (8 <= jobs.Count || jobs.Values.Any(job => IsActive(job.Status) || (job.Started && !job.Done.Task.IsCompleted)))
            {
                throw new InvalidOperationException("Log export capacity is in use.");
            }
            Guid id = Guid.NewGuid();
            string extension = request.Format switch { ELogExportFormat.Log => ".log", _ => ".zip" };
            string name = "logs-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + id.ToString("N")[..8] + extension;
            Job job = new(new(id, "queued", 0, false, null, name, now.AddMinutes(2)) { Format = request.Format }, fixedRequest, targets);
            jobs.Add(id, job);
            return job.Status;
        }
    }

    /// <summary>本文を返さず、進捗と不足範囲の有無だけを取得します。</summary>
    /// <returns>保持期限内の出力状態です。</returns>
    internal LogExportStatus? Read(Guid id)
    {
        using (gate.EnterScope())
        {
            if (jobs.TryGetValue(id, out Job? job) && clock.GetUtcNow() < job.Status.ExpiresAt)
            {
                return job.Status;
            }
            return null;
        }
    }

    /// <summary>一件の検索・圧縮・送信をまとめて取り消します。</summary>
    /// <returns>取消通知の完了です。</returns>
    internal Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using (gate.EnterScope())
        {
            if (!jobs.TryGetValue(id, out Job? job) || !IsActive(job.Status))
            {
                return Task.CompletedTask;
            }
            job.Status = job.Status with { State = "cancelled", Complete = false, Problem = "log.export.cancelled", ExpiresAt = clock.GetUtcNow().AddMinutes(15) };
            return job.Cancel.CancelAsync();
        }
    }

    /// <summary>ブラウザーへの送信中だけ検索し、切断や取消では後続ページを取得しません。</summary>
    /// <returns>ファイル末尾までの送信です。途中の失敗は呼出元が接続を中断します。</returns>
    // 抑制理由: 一出力の非致命的障害を状態へ記録した後、HTTP境界へ再送出します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "全失敗を出力状態へ記録して再送出し、成功扱いを避けます。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "元の例外を維持してHTTP境界へ通知し、不完全なZIPの送信を中断します。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "辞書が所有するJobを借用し、送信中は破棄しません。")]
    internal async Task WriteAsync(Guid id, Stream output, CancellationToken cancellationToken = default)
    {
        Job job;
        using (gate.EnterScope())
        {
            stopping.ThrowIfCancellationRequested();
            string queued = "queued";
            if (!jobs.TryGetValue(id, out Job? found) || !string.Equals(found.Status.State, queued, StringComparison.Ordinal) || found.Status.ExpiresAt <= clock.GetUtcNow())
            {
                throw new InvalidOperationException("Log export is no longer available.");
            }
            job = found;
            job.Started = true;
            job.Status = job.Status with { State = "running", ExpiresAt = clock.GetUtcNow().AddMinutes(30) };
        }
        try
        {
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping, cancellationToken, job.Cancel.Token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(30));
            lifetime.Token.ThrowIfCancellationRequested();
            using LogExportStream stream = new(output, lifetime.Token);
            (long Entries, bool Complete) result;
            if (job.Request.Format == ELogExportFormat.Log)
            {
                result = await LogExportText.WriteAsync(stream, job.Request, job.Targets.Keys.ToArray(),
                    (source, query, token) => read(job.Targets[source], query, token),
                    (source, query, token) => names(job.Targets[source], query, token),
                    count => Progress(job, count), lifetime.Token).ConfigureAwait(false);
            }
            else
            {
                result = await LogExportArchive.WriteAsync(stream, job.Request, job.Targets.Keys.ToArray(),
                    (source, query, token) => read(job.Targets[source], query, token),
                    (source, query, token) => names(job.Targets[source], query, token),
                    count => Progress(job, count), lifetime.Token).ConfigureAwait(false);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            Finish(job, "completed", result.Complete, null, result.Entries);
        }
        catch (OperationCanceledException)
        {
            Finish(job, "cancelled", false, "log.export.cancelled");
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Finish(job, "failed", false, "log.export.failed");
            throw;
        }
        finally
        {
            job.Done.TrySetResult();
        }
    }

    /// <summary>期限切れの受付を回収し、サービス停止時は送信処理の終了を待ちます。</summary>
    /// <returns>全出力が停止してからの終了です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                using (gate.EnterScope())
                {
                    Trim();
                }
            }
        }
        finally
        {
            Task[] running;
            using (gate.EnterScope())
            {
                running = jobs.Values.Where(job => job.Started).Select(job => job.Done.Task).ToArray();
            }
            await Task.WhenAll(running).ConfigureAwait(false);
            Clear();
        }
    }

    /// <summary>進捗を原子的に交換します。</summary>
    private void Progress(Job job, long count)
    {
        using (gate.EnterScope())
        {
            job.Status = job.Status with { Entries = count };
        }
    }

    /// <summary>送信終了の状態だけを残し、本文やZIPは保持しません。</summary>
    private void Finish(Job job, string state, bool complete, string? problem, long? count = null)
    {
        using (gate.EnterScope())
        {
            if (job.Cancel.IsCancellationRequested)
            {
                state = "cancelled";
                complete = false;
                problem = "log.export.cancelled";
            }
            job.Status = job.Status with { State = state, Complete = complete, Problem = problem, Entries = count ?? job.Status.Entries,
                ExpiresAt = clock.GetUtcNow().AddMinutes(15) };
        }
    }

    /// <summary>未開始または終了済みの期限切れ受付だけを解放します。</summary>
    private void Trim()
    {
        foreach (Guid id in jobs.Where(pair => (!pair.Value.Started || pair.Value.Done.Task.IsCompleted)
            && (pair.Value.Status.ExpiresAt <= clock.GetUtcNow() || (!IsActive(pair.Value.Status) && 8 <= jobs.Count))).Select(pair => pair.Key).ToArray())
        {
            using (jobs[id])
            {
                jobs.Remove(id);
            }
        }
    }

    /// <summary>停止済みの所有資源を破棄します。</summary>
    private void Clear()
    {
        using (gate.EnterScope())
        {
            foreach (Job job in jobs.Values)
            {
                using (job)
                {
                }
            }
            jobs.Clear();
        }
    }

    /// <summary>開始前のHost破棄でも受付を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        if (ExecuteTask is null)
        {
            Clear();
        }
    }

    /// <summary>受付待ちと送信中だけを同時実行制限へ数えます。</summary>
    /// <returns>本文処理がまだ終わっていないかです。</returns>
    private static bool IsActive(LogExportStatus status) => status.State is "queued" or "running";

    /// <summary>有限件数の受付情報と取消資源だけを所有します。</summary>
    private sealed class Job(LogExportStatus status, LogExportRequest request, Dictionary<LogSearchSource, FileTransferPeer> targets) : IDisposable
    {
        internal LogExportStatus Status
        {
            get; set;
        } = status;

        internal LogExportRequest Request
        {
            get;
        } = request;

        internal Dictionary<LogSearchSource, FileTransferPeer> Targets
        {
            get;
        } = targets;

        internal CancellationTokenSource Cancel
        {
            get;
        } = new();

        internal TaskCompletionSource Done
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Started
        {
            get; set;
        }

        /// <summary>本文を含めず識別子だけを表示します。</summary>
        /// <returns>出力識別子です。</returns>
        public override string ToString() => Status.Id.ToString("D");

        /// <summary>通信が終了した取消資源を解放します。</summary>
        public void Dispose() => Cancel.Dispose();
    }
}
