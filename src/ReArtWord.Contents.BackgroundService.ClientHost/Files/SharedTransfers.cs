using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Management;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Files;
using ReArtWord.Contents.BackgroundService.Contracts.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Files;

/// <summary>転送を実行せず、ピアが所有するキューの追加と全PC向けの集約を行います。</summary>
internal sealed class SharedTransfers(ObserverConnection connection, PeerManagementClient remote, ClientCommandLedger ledger, TimeProvider? timeProvider = null) : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<FileTransferPeer, Snapshot> snapshots = new();
    private int unconfirmed, total;
    private long interestStarted = long.MinValue;
    private long order;

    /// <summary>同じ実体・パスの固定要求を転送先Coreの共有キューへ一度送ります。</summary>
    /// <returns>Coreが保存したジョブです。</returns>
    // 抑制理由: ObserverはObserverConnectionが所有し、別の操作とも共有するため、ここでは破棄しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "ObserverConnectionが所有する共有Observerを借用します。")]
    internal async Task<FileTransferJob> EnqueueAsync(FileTransferRequest request, CancellationToken cancellationToken = default)
    {
        PeerInfo source = Resolve(request.Source), destination = Resolve(request.Destination);
        request = request with
        {
            Source = request.Source with
            {
                Port = source.Port
            },
            Destination = request.Destination with
            {
                Port = destination.Port
            }
        };
        string operation = "transfer.queue.enqueue";
        UdpObserver observer = connection.FindObserver(request.Destination.ClusterId, request.Destination.PeerId)
            ?? throw new IOException("The original destination route is unavailable.");
        ManagementCommand command = await ledger.PrepareTimedAsync(request.Id, request.Destination.ClusterId, request.Destination.PeerId, operation,
            NodeValue.FromJson(JsonSerializer.SerializeToElement(request, ContractJson.Options)), destination, observer.PrepareClockAsync, cancellationToken).ConfigureAwait(false);
        ManagementReply reply = await observer.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new IOException("The destination Core did not confirm queue acceptance. Inspect its shared queue before repeating the request.");
        }
        FileTransferJob job = reply.Value.Json.Deserialize<FileTransferJob>(ContractJson.Options) ?? throw new IOException("The shared queue response is invalid.");
        long received = Interlocked.Increment(ref order);
        snapshots.AddOrUpdate(request.Destination, _ => new([job], clock.GetTimestamp(), true, received, 0), (_, previous) =>
        {
            // 再照会された受付結果には、キューで既に観測した転送の進捗は含まれません。
            if (previous.Jobs.Any(item => item.Request.Id == job.Request.Id))
            {
                return previous;
            }
            return previous with { Jobs = previous.Jobs.Append(job).TakeLast(133).ToArray(), Changed = Math.Max(previous.Changed, received), Seen = clock.GetTimestamp() };
        });
        return job;
    }

    /// <summary>一画面でしか存在しないキューを作らず、Coreの観測を共有します。</summary>
    /// <returns>保持済み進捗と今回の確認範囲です。</returns>
    internal SharedTransferQueueState Read(int offset = 0, bool progress = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Interlocked.Exchange(ref interestStarted, clock.GetTimestamp());
        IEnumerable<FileTransferJob> observed = snapshots.Values.SelectMany(value => value.Jobs);
        FileTransferJob[] jobs = progress ? observed.OrderByDescending(job => job.State is "Queued" or "Copying").ThenByDescending(job => job.UpdatedAt).ToArray()
                                          : observed.OrderByDescending(job => job.UpdatedAt).ToArray();
        if (jobs.Length <= offset)
        {
            offset = 0;
        }
        int limit = progress ? 8
                             : 128;
        return new(jobs.Skip(offset).Take(limit).ToArray(), Volatile.Read(ref unconfirmed), Volatile.Read(ref total), jobs.Length, offset);
    }

    /// <summary>全履歴を画面へ送らず、進行中と待機中の総数を共有します。</summary>
    internal int ActiveCount => snapshots.Values.Sum(value => value.Jobs.Count(job => job.State is "Queued" or "Copying"));

    /// <summary>ブラウザー数と独立した並列数で共有キューを確認します。</summary>
    /// <returns>ClientHost停止までの参照だけの処理です。</returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(2));
        do
        {
            FileTransferPeer[] peers = connection.GetClusters().SelectMany(cluster => cluster.Peers.Select(peer =>
                new FileTransferPeer(cluster.ClusterId, peer.PeerId, peer.InstanceId, peer.Port))).Take(256).ToArray();
            int failed = 0;
            await Parallel.ForEachAsync(peers, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = stoppingToken }, async (peer, token) =>
            {
                if (!await RefreshPeerAsync(peer, token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref failed);
                }
            }).ConfigureAwait(false);
            foreach (FileTransferPeer key in snapshots.Where(pair => TimeSpan.FromHours(1) < clock.GetElapsedTime(pair.Value.Seen))
                .Select(pair => pair.Key).Concat(snapshots.OrderByDescending(pair => pair.Value.Seen).Skip(512).Select(pair => pair.Key)).ToArray())
            {
                snapshots.TryRemove(key, out _);
            }
            Volatile.Write(ref unconfirmed, failed + snapshots.Keys.Count(key => !peers.Any(peer => string.Equals(peer.ClusterId, key.ClusterId, StringComparison.Ordinal)
                && string.Equals(peer.PeerId, key.PeerId, StringComparison.Ordinal))));
            Volatile.Write(ref total, peers.Length);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>一ピアの障害を他ピアの共有キューへ波及させません。</summary>
    /// <returns>今回の読取りを確認できたかです。</returns>
    internal async Task<bool> RefreshPeerAsync(FileTransferPeer peer, CancellationToken cancellationToken = default)
    {
        long now = clock.GetTimestamp();
        long interest = Volatile.Read(ref interestStarted);
        if ((interest == long.MinValue || TimeSpan.FromSeconds(5) < clock.GetElapsedTime(interest, now)) && snapshots.TryGetValue(peer, out Snapshot? previous)
            && previous.Confirmed && clock.GetElapsedTime(previous.Seen, now) < TimeSpan.FromSeconds(10)
            && previous.Jobs.All(IsSettled))
        {
            return true;
        }
        long poll = Interlocked.Increment(ref order);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            FileTransferJob[] jobs = await remote.SendAsync<FileTransferJob[]>(peer, "transfer.queue.list", new { }, true, timeout.Token).ConfigureAwait(false);
            _ = Resolve(peer);
            foreach (FileTransferPeer old in snapshots.Keys.Where(key => key.InstanceId != peer.InstanceId && string.Equals(key.ClusterId, peer.ClusterId, StringComparison.Ordinal)
                && string.Equals(key.PeerId, peer.PeerId, StringComparison.Ordinal)))
            {
                snapshots.TryRemove(old, out _);
            }
            Snapshot refreshed = snapshots.AddOrUpdate(peer, _ => new(jobs, clock.GetTimestamp(), true, poll, poll), (_, known) =>
            {
                if (poll < known.Poll)
                {
                    return known;
                }
                if (poll < known.Changed)
                {
                    // 照会中に届いた受付結果との前後は決められないため、既知の進捗を保って次の照会で確認します。
                    FileTransferJob[] combined = known.Jobs.Concat(jobs).DistinctBy(job => job.Request.Id).Take(133).ToArray();
                    return known with { Jobs = combined, Poll = poll, Confirmed = false };
                }
                return new(jobs, clock.GetTimestamp(), true, poll, poll);
            });
            return refreshed.Confirmed;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            if (snapshots.TryGetValue(peer, out Snapshot? failed) && failed.Poll <= poll)
            {
                snapshots.TryUpdate(peer, failed with { Confirmed = false, Poll = poll }, failed);
            }
            return false;
        }
    }

    /// <summary>同じピアで開始した照会と追加受付の順序を、受信したジョブの日時と分けて保持します。</summary>
    private sealed record Snapshot(FileTransferJob[] Jobs, long Seen, bool Confirmed, long Changed, long Poll);

    /// <summary>結果不明は停止中の転送とみなさず、通常周期で再確認します。</summary>
    /// <returns>確定した終了状態かです。</returns>
    private static bool IsSettled(FileTransferJob job) => job.State is "Succeeded" or "Failed" or "Cancelled";

    /// <summary>画面が渡したポートを信頼せず、実発見した同じ対象を使用します。</summary>
    /// <returns>固定した実体だけです。</returns>
    private PeerInfo Resolve(FileTransferPeer target)
    {
        PeerInfo[] matches = connection.GetClusters().Where(cluster => string.Equals(cluster.ClusterId, target.ClusterId, StringComparison.Ordinal))
            .SelectMany(cluster => cluster.Peers).Where(peer => string.Equals(peer.PeerId, target.PeerId, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].InstanceId != target.InstanceId || connection.FindObserver(target.ClusterId, target.PeerId) is null)
        {
            throw new InvalidOperationException("The selected peer instance or network changed.");
        }
        return matches[0];
    }
}
