using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Synchronization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Documents;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Sqlite;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.ClusterSynchronization;

/// <summary>実UDPの断片応答だけを制御し、版一致と完了通知の障害境界を再現します。</summary>
internal sealed class SyncRecoveryPeer : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "contents-sync-recovery-" + Guid.NewGuid().ToString("N"));
    private readonly string clusterId = Guid.NewGuid().ToString("N");
    private readonly Guid instance = Guid.NewGuid();
    private readonly UdpTransport source;
    private readonly UdpClient fragments = new(new IPEndPoint(IPAddress.Loopback, 0)) { EnableBroadcast = true };
    private readonly int port = LargeSynchronizationTests.Port();
    private byte[] payload;
    private readonly ConcurrentDictionary<Guid, SyncManifest> manifests = [];
    private long sequence;
    private int opens;
    private int versionProbes;
    private int completed;
    private string? announcedVersion;
    private Guid? droppedRequest;
    private readonly Lock reads = new();

    /// <summary>短い配送期限で一つの実Peerと制御可能な相手を接続します。</summary>
    internal SyncRecoveryPeer(RepositorySnapshot snapshot, TimeSpan? requestTimeout = null)
    {
        Directory.CreateDirectory(root);
        Repository = new(Path.Combine(root, "state.db"));
        Target = new(new ClusterOptions(clusterId, "target", "target", "127.0.0.1", "127.255.255.255", port, 1, true, root)
        { HeartbeatInterval = TimeSpan.FromMilliseconds(50), RequestTimeout = requestTimeout ?? TimeSpan.FromMilliseconds(400) }, Repository)
        { ReadinessCheck = _ => ValueTask.FromResult(true) };
        source = new(new("source", "127.0.0.1", "127.255.255.255", port, clusterId) { RequestTimeout = TimeSpan.FromSeconds(2) }, instance, TimeProvider.System);
        source.Initialize(NetworkInterface.GetAllNetworkInterfaces().Single(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id);
        source.Received = ReceiveAsync;
        payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);
    }

    /// <summary>検証対象の実参加状態です。</summary>
    internal PeerCluster Target
    {
        get;
    }

    /// <summary>取込とhistoryを観測する実保存です。</summary>
    internal SqliteRepository Repository
    {
        get;
    }

    /// <summary>明示解除するまで全断片応答を失わせます。</summary>
    internal bool DropReads
    {
        get; set;
    }

    /// <summary>最初の配送IDとその再送だけを失わせ、次の同一断片照会には応答します。</summary>
    internal bool DropFirstReadRequest
    {
        get; set;
    }

    /// <summary>一度に届く応答断片数を制限し、大きい送信の末尾欠測を再現します。</summary>
    internal int? MaximumReplyFragments
    {
        get; set;
    }

    /// <summary>特定断片の返信待ちを制御して、往復待ちと応答順序の入替を再現します。</summary>
    internal Func<SyncReadRequest, CancellationToken, Task>? BeforeRead
    {
        get; set;
    }

    /// <summary>全スナップショットの開始回数です。</summary>
    internal int Opens => Volatile.Read(ref opens);

    /// <summary>通常参加後の版照会にだけ、一時差分または永続差分を返します。</summary>
    internal Func<CancellationToken, Task<string>>? ReadVersion
    {
        get; set;
    }

    /// <summary>履歴転送前に実際に照会された版の回数です。</summary>
    internal int VersionProbes => Volatile.Read(ref versionProbes);

    /// <summary>検証対象が最後に実通信で通知した現在版です。</summary>
    internal string? AnnouncedVersion => Volatile.Read(ref announcedVersion);

    /// <summary>相手が双方向同期を完了したと通知した回数です。</summary>
    internal int Completed => Volatile.Read(ref completed);

    /// <summary>進行中の転送がない区切りで、次回の転送対象を変更します。</summary>
    internal void SetSnapshot(RepositorySnapshot snapshot) => payload = JsonSerializer.SerializeToUtf8Bytes(snapshot);

    /// <summary>現在値と履歴の初回確認を済ませ、通常参加後の試験を開始できるようにします。</summary>
    /// <returns>同じ実体との初回同期の完了です。</returns>
    internal async Task EstablishAsync(CancellationToken cancellationToken = default)
    {
        while (Completed == 0)
        {
            await AnnounceAsync(await Repository.GetSharedVersionAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>相手側で初回同期の終了処理が残っていても、生存通知を続けて版比較の開始を確認します。</summary>
    /// <returns>実通信で版照会を受け取った時点です。</returns>
    internal async Task AnnounceUntilVersionProbeAsync(string digest, CancellationToken cancellationToken = default)
    {
        int previous = VersionProbes;
        do
        {
            await AnnounceAsync(digest, cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
        while (VersionProbes == previous);
    }

    /// <summary>実通信を開始します。</summary>
    /// <returns>起動完了です。</returns>
    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Target.StartAsync(cancellationToken).ConfigureAwait(false);
        source.Start();
    }

    /// <summary>相手の現在の版・状態を通知します。</summary>
    /// <returns>実UDPへの送信です。</returns>
    internal Task AnnounceAsync(string digest, EPeerStatus status = EPeerStatus.Ready, CancellationToken cancellationToken = default)
    {
        PeerInfo peer = new("source", instance, "source", "127.0.0.1", port, status, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return source.SendAsync(new(Guid.NewGuid(), clusterId, "source", instance, "hello", null, null,
            JsonSerializer.SerializeToElement(new PeerAnnouncement(peer, digest, null, null, Interlocked.Increment(ref sequence)))), cancellationToken);
    }

    /// <summary>元の実体を固定した同期専用要求を送ります。</summary>
    /// <returns>同期応答です。</returns>
    internal async Task<SyncResponse> SendAsync<T>(string kind, T body, CancellationToken cancellationToken = default)
    {
        WireMessage? reply = await source.RequestAsync(new(Guid.NewGuid(), clusterId, "source", instance, kind, "target", null,
            JsonSerializer.SerializeToElement(body), Target.LocalInstanceId), Target.LocalInstanceId, cancellationToken).ConfigureAwait(false);
        return reply?.Body.Deserialize<SyncResponse>() ?? throw new IOException("The fixture did not receive a synchronization response.");
    }

    /// <summary>相手からの同期を開始する固定マニフェストです。</summary>
    /// <returns>全内容のハッシュを持つマニフェストです。</returns>
    internal SyncManifest Offer()
    {
        SyncManifest manifest = new(Guid.NewGuid(), "source", instance, "target", Target.LocalInstanceId, payload.LongLength,
            Convert.ToHexString(SHA256.HashData(payload)), DateTimeOffset.UtcNow.AddSeconds(30));
        manifests[manifest.Id] = manifest;
        return manifest;
    }

    /// <summary>読取りだけに故障を注入し、open/closeの実経路を維持します。</summary>
    /// <returns>返信処理です。</returns>
    private async Task ReceiveAsync(WireMessage message, string address, CancellationToken cancellationToken = default)
    {
        SyncResponse response;
        switch (message.Kind)
        {
            case "hello":
                Volatile.Write(ref announcedVersion, message.Body.Deserialize<PeerAnnouncement>()?.Digest);
                return;
            case "sync.version" when ReadVersion is not null:
                Interlocked.Increment(ref versionProbes);
                response = new(Version: await ReadVersion(cancellationToken).ConfigureAwait(false));
                break;
            case "sync.open":
                Interlocked.Increment(ref opens);
                response = new(Offer());
                break;
            case "sync.read":
                using (reads.EnterScope())
                {
                    if (DropFirstReadRequest)
                    {
                        droppedRequest ??= message.Id;
                    }
                    if (DropReads || (DropFirstReadRequest && droppedRequest == message.Id))
                    {
                        return;
                    }
                }
                SyncReadRequest request = message.Body.Deserialize<SyncReadRequest>()!;
                if (!manifests.ContainsKey(request.Id))
                {
                    response = new(Reason: "Sync.SessionUnavailable");
                    break;
                }
                if (BeforeRead is not null)
                {
                    await BeforeRead(request, cancellationToken).ConfigureAwait(false);
                }
                response = new(Chunk: new(request.Id, request.Offset, payload.AsSpan(checked((int)request.Offset), request.Length).ToArray()));
                break;
            case "sync.close":
                if (message.Body.Deserialize<SyncCloseRequest>()?.Completed == true)
                {
                    Interlocked.Increment(ref completed);
                }
                response = new();
                break;
            default:
                return;
        }

        // 応答IDを毎回変える旧版との混在状態も検証できるよう、この相手は旧形式を維持します。
        WireMessage reply = new(Guid.NewGuid(), clusterId, "source", instance, "reply", "target", message.Id,
            JsonSerializer.SerializeToElement(response), Target.LocalInstanceId);
        if (MaximumReplyFragments is int maximum)
        {
            foreach (byte[] packet in DatagramEncoding.Encode(reply, 4194304).Take(maximum))
            {
                await fragments.SendAsync(packet, new IPEndPoint(IPAddress.Parse("127.255.255.255"), port), cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await source.SendAsync(reply, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>所有プロセス内通信を停止してからローカル試験データを解放します。</summary>
    /// <returns>終了確認です。</returns>
    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)Target).DisposeAsync().ConfigureAwait(false);
        await ((IAsyncDisposable)source).DisposeAsync().ConfigureAwait(false);
        fragments.Dispose();
        Directory.Delete(root, true);
    }
}
