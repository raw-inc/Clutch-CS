using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Secrets;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Observability.Logging;

/// <summary>保存、保持、転送、検索を本来処理から独立して運用します。</summary>
public sealed class WorkflowLogService : IWorkflowLog, IAsyncDisposable
{
    private readonly LogStore store;
    private readonly LogRedactor redactor;
    private readonly LogQueueProcessor queue;
    private readonly LogForwarder transport;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim settings = new(1, 1);
    private readonly ConcurrentDictionary<string, string> failures = new(StringComparer.Ordinal);
    private LogPolicy policy;
    private Task processing = Task.CompletedTask;
    private Task listening = Task.CompletedTask;
    private Task maintaining = Task.CompletedTask;
    private Task forwarding = Task.CompletedTask;
    private string? lastFailure;
    private int searchAvailable;
    private int started;
    private int disposed;
    private volatile bool retentionHeld;

    /// <summary>共有方針を確認するまで削除・圧縮を保留します。記録・検索・転送は継続します。</summary>
    public bool RetentionHeld
    {
        get => retentionHeld;
        set => retentionHeld = value;
    }

    /// <summary>Hostの保存先、既存リポジトリと秘密保管を使用します。</summary>
    public WorkflowLogService(LogOptions options, IDurableRepository repository, SecretStore secrets, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(secrets);
        policy = new(options);
        clock = timeProvider ?? TimeProvider.System;
        store = new(policy.Options, repository);
        redactor = new(secrets);
        queue = new(store, Current, code => RecordFailure("queue", code));
        transport = new(store, redactor, Current, AcceptAsync, RecordFailure);
    }

    /// <summary>実際に現在使用している非秘密設定です。</summary>
    public LogOptions Options => Current().Options;

    /// <summary>秘密を除去したローカルログを通知する共通Hook。</summary>
    public IWorkflowEvents? Events
    {
        get; set;
    }

    /// <summary>欠落と元ファイルが残る未転送・検索不能を区別する統計です。</summary>
    public LogStatistics Statistics => new(queue.QueuedBytes, queue.DroppedEntries, queue.DroppedBytes, store.EntryCount, transport.PendingBytes,
        Volatile.Read(ref searchAvailable) != 0, transport.Endpoint, Volatile.Read(ref lastFailure), new Dictionary<string, string>(failures, StringComparer.Ordinal));

    /// <summary>logger所有の処理を開始し、保存不能でもHost起動を停止しません。</summary>
    /// <returns>処理開始の受付です。</returns>
    // 抑制理由: 長寿命タスクを各フィールドで所有し、DisposeAsyncが全タスクをawaitしてから資源を解放します。
    [SuppressMessage("SourceQuality", "RAW511", Justification = "所有する4taskはDisposeAsyncで全てawaitします。")]
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Interlocked.CompareExchange(ref started, 1, 0) == 0)
        {
            processing = Task.Run(() => queue.RunAsync(lifetime.Token), CancellationToken.None);
            listening = Task.Run(() => transport.ListenAsync(lifetime.Token), CancellationToken.None);
            maintaining = Task.Run(() => MaintainLoopAsync(lifetime.Token), CancellationToken.None);
            forwarding = Task.Run(() => ForwardLoopAsync(lifetime.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    /// <summary>秘密を除去してからバイト上限内へ受付します。</summary>
    /// <returns>保存待ち受付または欠落表示の完了です。</returns>
    public async ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _ = await EnqueueAsync(entry, true, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>この呼出し以前に受付済みのログ保存を待ちます。</summary>
    /// <returns>保存境界の確認です。</returns>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref started) == 0)
        {
            throw new InvalidOperationException("ログ処理を開始してからflushしてください。");
        }
        if (!await queue.FlushAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("ログ保存境界を確認できません。");
        }
    }

    /// <summary>保持中のログを再度伏字にして検索します。</summary>
    /// <returns>取得できた結果と検索の完全性です。</returns>
    public async Task<LogQueryResult> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        try
        {
            await store.InitializeAsync(query.UncompressedOnly, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> patterns = await redactor.ReadPatternsAsync(cancellationToken).ConfigureAwait(false);
            LogQueryResult result = await LogSearch.QueryAsync(store, query, patterns, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref searchAvailable, Convert.ToInt32(result.Complete));
            string? code = null;
            if (!result.Complete)
            {
                code = "log.search.incomplete";
            }
            RecordFailure("search", code);
            return result;
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            RecordFailure("search", "log.search.unavailable");
            Interlocked.Exchange(ref searchAvailable, 0);
            return new([], null, false);
        }
    }

    /// <summary>保持変更の既存適用を明示して設定を更新します。</summary>
    /// <returns>設定切替の完了です。</returns>
    public async Task UpdateOptionsAsync(LogOptions options, LogRetentionChangeOptions choice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(choice);
        LogPolicy next = new(options);
        await settings.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LogPolicy before = Current();
            if (!string.Equals(before.Options.PeerId, next.Options.PeerId, StringComparison.Ordinal) || !string.Equals(before.Options.Directory, next.Options.Directory, StringComparison.Ordinal))
            {
                throw new ArgumentException("Peer識別子とログ保存先の変更にはservice再作成が必要です。", nameof(options));
            }
            await store.ProtectExistingAsync(before, next, choice, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref policy, next);
            try
            {
                if (!RetentionHeld)
                {
                    await store.MaintainAsync(next, clock.GetUtcNow(), choice.ExpandExistingCompressedOnRawExtension, cancellationToken).ConfigureAwait(false);
                }
                RecordFailure("retention", null);
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                RecordFailure("retention", "log.retention.unavailable");
            }
        }
        finally
        {
            settings.Release();
        }
        WorkflowLogEntry audit = new(Guid.NewGuid(), clock.GetUtcNow(), "INFO", next.Options.PeerId, "ログ保持設定を更新しました。",
            Data: NodeValue.FromObject(new { target = "log-retention", host = Environment.MachineName, outcome = "succeeded" }));
        await WriteAsync(audit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Coordinator変更等の転送先変更を保持変更から分離します。</summary>
    /// <returns>新しい接続から使う宛先の切替です。</returns>
    public async Task UpdateForwardTargetsAsync(IReadOnlyList<LogForwardTarget> targets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        await settings.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref policy, new LogPolicy(Current().Options with { ForwardTargets = targets.ToArray() }));
        }
        finally
        {
            settings.Release();
        }
    }

    /// <summary>受付を閉じ、有限時間のdrain後に全所有タスクを終了します。</summary>
    /// <returns>資源解放の完了です。</returns>
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        return DisposeAsync(CancellationToken.None);
    }

    /// <summary>指定時はgraceful待機だけを短縮し、所有資源は必ず終了します。</summary>
    /// <returns>全所有タスクと資源の終了です。</returns>
    public async ValueTask DisposeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        using LogStore ownedStore = store;
        using SemaphoreSlim ownedSettings = settings;
        using CancellationTokenSource ownedLifetime = lifetime;
        queue.Complete();
        try
        {
            await processing.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or InvalidDataException or IOException or InvalidOperationException or ArgumentException or System.Data.Common.DbException or ZstdSharp.ZstdException)
        {
            RecordFailure("queue", "log.shutdown.incomplete");
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(processing, listening, maintaining, forwarding).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidDataException or IOException or InvalidOperationException or ArgumentException or System.Data.Common.DbException or ZstdSharp.ZstdException)
        {
            // 所有タスクの終了と障害は統計へ反映します。
        }
        queue.DiscardRemaining();

    }

    /// <summary>転送元へ永続受付できたときだけACKします。</summary>
    /// <returns>永続保存が確認できたかです。</returns>
    private async Task<bool> AcceptAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<bool> receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!await EnqueueAsync(entry, false, receipt, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }
        return await receipt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>各受付で現在の秘密値を確認し、不能なら生値を通しません。</summary>
    /// <returns>安全なキュー受付結果です。</returns>
    private async Task<bool> EnqueueAsync(WorkflowLogEntry entry, bool forward, TaskCompletionSource<bool>? receipt, CancellationToken cancellationToken = default)
    {
        try
        {
            if (entry.Id == Guid.Empty || Volatile.Read(ref disposed) != 0)
            {
                queue.Drop(0);
                return false;
            }
            string level = LogPolicy.NormalizeLevel(entry.Level);
            string debug = "DEBUG";
            bool keepPersonal = !entry.ContainsPersonalData || string.Equals(level, debug, StringComparison.Ordinal);
            if (keepPersonal && entry.Data is { Kind: EValueKind.Binary } binary && Current().Options.QueueBytes < (binary.Binary.Length * 4L / 3) + 256)
            {
                queue.Drop((binary.Binary.Length * 4L / 3) + 256);
                return false;
            }
            IReadOnlyList<string> patterns = await redactor.ReadPatternsAsync(cancellationToken).ConfigureAwait(false);
            WorkflowLogEntry safe = LogRedactor.Redact(entry, patterns);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(safe);
            RecordFailure("redaction", null);
            bool accepted = queue.TryEnqueue(new(bytes, forward, receipt));
            if (accepted && forward)
            {
                await WorkflowEventBoundary.PublishAsync(Events, new WorkflowEvent(safe.Id, "log.written", safe.Time, safe.PeerId,
                    Contracts.Serialization.ContractJson.ToValue(safe), safe.WorkflowId, safe.NodeId, safe.CausationId, Hooks: safe.Hooks), CancellationToken.None).ConfigureAwait(false);
            }
            return accepted;
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            queue.Drop(0);
            RecordFailure("redaction", "log.redaction.unavailable");
            receipt?.TrySetResult(false);
            return false;
        }
    }

    /// <summary>保持と索引の復旧を本来処理とは独立して進めます。</summary>
    /// <returns>終了までの保守処理です。</returns>
    private async Task MaintainLoopAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await settings.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!RetentionHeld)
                {
                    await store.MaintainAsync(Current(), clock.GetUtcNow(), false, cancellationToken).ConfigureAwait(false);
                }
                RecordFailure("retention", null);
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or ZstdSharp.ZstdException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                RecordFailure("retention", "log.retention.unavailable");
            }
            finally
            {
                settings.Release();
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>未転送元ファイルを使って転送経路の回復を試みます。</summary>
    /// <returns>終了までの転送処理です。</returns>
    private async Task ForwardLoopAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await transport.ForwardAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>開始済み処理が一つの設定版を保持します。</summary>
    /// <returns>現在の設定版です。</returns>
    private LogPolicy Current()
    {
        return Volatile.Read(ref policy);
    }

    /// <summary>失敗経路へ再帰的にログを書かず別表示用状態を保持します。</summary>
    private void RecordFailure(string stage, string? code)
    {
        if (code is null)
        {
            failures.TryRemove(stage, out _);
        }
        else
        {
            failures[stage] = code;
            Volatile.Write(ref lastFailure, code);
        }
    }
}
