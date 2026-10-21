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
    private readonly ConcurrentDictionary<FileTransferPeer, (FileTransferJob[] Jobs, DateTimeOffset Seen, bool Confirmed)> snapshots = new();
    private int unconfirmed, total;
    private long interestedUntil;

    /// <summary>同じ実体・パスの固定要求を転送先Coreの共有キューへ一度送ります。</summary>
    /// <returns>Coreが保存したジョブです。</returns>
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
        ManagementCommand command = await ledger.PrepareAsync(request.Id, request.Destination.ClusterId, request.Destination.PeerId, operation,
            NodeValue.FromJson(JsonSerializer.SerializeToElement(request, ContractJson.Options)), destination, cancellationToken).ConfigureAwait(false);
        ManagementReply reply = await (connection.FindObserver(request.Destination.ClusterId, request.Destination.PeerId)
            ?? throw new IOException("The original destination route is unavailable.")).ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (reply.Outcome != EOutcome.Succeeded || reply.Value is null)
        {
            throw new IOException("The destination Core did not confirm queue acceptance. Inspect its shared queue before repeating the request.");
        }
        FileTransferJob job = reply.Value.Json.Deserialize<FileTransferJob>(ContractJson.Options) ?? throw new IOException("The shared queue response is invalid.");
        snapshots.AddOrUpdate(request.Destination, _ => ([job], clock.GetUtcNow(), true), (_, previous) =>
        {
            if (previous.Jobs.Any(item => item.Request.Id == job.Request.Id && job.UpdatedAt < item.UpdatedAt))
            {
                return previous;
            }
            return (previous.Jobs.Where(item => item.Request.Id != job.Request.Id).Append(job).TakeLast(133).ToArray(), clock.GetUtcNow(), true);
        });
        return job;
    }

    /// <summary>一画面でしか存在しないキューを作らず、Coreの観測を共有します。</summary>
    /// <returns>保持済み進捗と今回の確認範囲です。</returns>
    internal SharedTransferQueueState Read(int offset = 0, bool progress = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Interlocked.Exchange(ref interestedUntil, clock.GetUtcNow().AddSeconds(5).UtcTicks);
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
            foreach (FileTransferPeer key in snapshots.Where(pair => TimeSpan.FromHours(1) < clock.GetUtcNow() - pair.Value.Seen)
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
        DateTimeOffset now = clock.GetUtcNow();
        if (Volatile.Read(ref interestedUntil) < now.UtcTicks && snapshots.TryGetValue(peer, out (FileTransferJob[] Jobs, DateTimeOffset Seen, bool Confirmed) previous)
            && previous.Confirmed && previous.Seen <= now && now - previous.Seen < TimeSpan.FromSeconds(10)
            && previous.Jobs.All(IsSettled))
        {
            return true;
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            FileTransferJob[] jobs = await remote.SendAsync<FileTransferJob[]>(peer, "transfer.queue.list", new { }, true, timeout.Token).ConfigureAwait(false);
            foreach (FileTransferPeer old in snapshots.Keys.Where(key => key.InstanceId != peer.InstanceId && string.Equals(key.ClusterId, peer.ClusterId, StringComparison.Ordinal)
                && string.Equals(key.PeerId, peer.PeerId, StringComparison.Ordinal)))
            {
                snapshots.TryRemove(old, out _);
            }
            snapshots.AddOrUpdate(peer, _ => (jobs, clock.GetUtcNow(), true), (_, known) =>
            {
                FileTransferJob[] current = jobs;
                if (now < known.Seen)
                {
                    current = jobs.Concat(known.Jobs).GroupBy(job => job.Request.Id).Select(group => group.MaxBy(job => job.UpdatedAt)!)
                        .OrderByDescending(job => job.UpdatedAt).Take(133).ToArray();
                }
                return (current, clock.GetUtcNow(), true);
            });
            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            if (snapshots.TryGetValue(peer, out (FileTransferJob[] Jobs, DateTimeOffset Seen, bool Confirmed) failed))
            {
                snapshots.TryUpdate(peer, (failed.Jobs, failed.Seen, false), failed);
            }
            return false;
        }
    }

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
