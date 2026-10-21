using System.Text.Json;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement;
using ReArtWord.Contents.BackgroundService.Contracts.DeviceManagement.WakeOnLan;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.ClientHost.DeviceManagement;

/// <summary>稼働中にNICと選択を学習し、内容の変化時だけ停止後用の情報を保存します。</summary>
internal sealed class WakeHostCache : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly string path;
    private readonly Func<IReadOnlyList<DiscoveredCluster>> discover;
    private readonly IDeviceRemote remote;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, HostWakeProfile> profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> observedOrder = new(StringComparer.Ordinal);
    private long readSequence;
    private bool loaded;
    private bool persistenceFailed;

    /// <summary>読み取り開始順を固定し、設定変更後に届いた古い返信で戻しません。</summary>
    /// <returns>このClientHost内だけで使う順序です。</returns>
    internal long BeginRead() => Interlocked.Increment(ref readSequence);

    /// <summary>本ClientHostの保存先と、発見済みの固定対象へ接続します。</summary>
    public WakeHostCache(ClientHostOptions options, ObserverConnection connection, IDeviceRemote remote)
        : this(options.StateDirectory, connection.GetClusters, remote)
    {
    }

    /// <summary>本番のNIC・レジストリへ触れず、停止・再起動・永続化を検証する境界です。</summary>
    internal WakeHostCache(string directory, Func<IReadOnlyList<DiscoveredCluster>> discover, IDeviceRemote remote)
    {
        path = Path.Combine(directory, "wake-hosts.json");
        this.discover = discover;
        this.remote = remote;
    }

    /// <summary>停止中のPCについても最後に保存した選択を返します。</summary>
    /// <returns>呼出元が変更しないプロフィールのスナップショットです。</returns>
    internal async Task<IReadOnlyList<HostWakeProfile>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (persistenceFailed)
            {
                throw new IOException("wake.unavailable");
            }
            return profiles.Values.ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>HostManagerで選択を読んだ直後にも、同じ確認済み値を永続化します。</summary>
    /// <returns>内容が変わった場合だけの保存です。</returns>
    internal async Task ObserveAsync(DeviceHostTarget target, DeviceSnapshot snapshot, long readOrder, CancellationToken cancellationToken = default)
    {
        DeviceWakeState state = DeviceSnapshots.Read<DeviceWakeState>(snapshot.Data);
        state.Validate();
        if (snapshot.Revision is null || snapshot.Revision.Length != 64 || !snapshot.Revision.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("wake.invalidInventory");
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            profiles.TryGetValue(target.HostId, out HostWakeProfile? before);
            if (before is null && 1024 <= profiles.Count)
            {
                throw new IOException("wake.cacheCapacity");
            }
            if (readOrder <= observedOrder.GetValueOrDefault(target.HostId))
            {
                return;
            }
            observedOrder[target.HostId] = readOrder;
            string[] clusters = discover().Where(cluster => cluster.Peers.Any(peer => string.Equals(peer.HostId, target.HostId, StringComparison.Ordinal)))
                .Select(cluster => cluster.ClusterId).Concat(before?.Clusters ?? []).Append(target.ClusterId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (!persistenceFailed && before is not null && string.Equals(before.Revision, snapshot.Revision, StringComparison.Ordinal) && before.Clusters.SequenceEqual(clusters, StringComparer.Ordinal))
            {
                return;
            }
            HostWakeProfile changed = new(target.HostId, snapshot.Revision, state, clusters, DateTimeOffset.UtcNow);
            HostWakeProfile[] saved = profiles.Values.Where(value => !string.Equals(value.HostId, target.HostId, StringComparison.Ordinal)).Append(changed).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string pending = path + ".pending";
            persistenceFailed = true;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(saved, ContractJson.Options);
            if (8 * 1024 * 1024 < bytes.Length)
            {
                throw new InvalidDataException("wake.cacheCapacity");
            }
            await File.WriteAllBytesAsync(pending, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(pending, path, true);
            profiles[target.HostId] = changed;
            persistenceFailed = false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>破損した保存値から新しいMACや既定選択を作りません。</summary>
    /// <returns>最初の一回だけの保存値読取りです。</returns>
    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (loaded)
        {
            return;
        }
        if (File.Exists(path))
        {
            if (8 * 1024 * 1024 < new FileInfo(path).Length)
            {
                throw new InvalidDataException("wake.cacheCapacity");
            }
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            HostWakeProfile[] saved = JsonSerializer.Deserialize<HostWakeProfile[]>(bytes, ContractJson.Options) ?? throw new InvalidDataException("wake.invalidInventory");
            if (1024 < saved.Length || saved.Any(profile => profile is null || string.IsNullOrWhiteSpace(profile.HostId) || profile.Clusters is null || profile.Revision is null || profile.State is null))
            {
                throw new InvalidDataException("wake.invalidInventory");
            }
            foreach (HostWakeProfile profile in saved)
            {
                profile.State.Validate();
            }
            profiles.Clear();
            foreach (HostWakeProfile profile in saved)
            {
                profiles[profile.HostId] = profile;
            }
        }
        loaded = true;
    }

    /// <summary>同一PCの複数Coreへ重複照会せず、毎分・最大2件並列で学習します。</summary>
    /// <returns>サービス寿命内の読取りです。</returns>
    // 抑制理由: BackgroundServiceの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BackgroundServiceの停止トークンを全I/Oへ渡します。")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>旧版・到達不能は保持済みの設定を消さず、次の周期で再確認します。</summary>
    /// <returns>今回の有限個数の照会です。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DeviceHostTarget[] targets = discover().SelectMany(cluster => cluster.Peers.Where(peer => now - peer.LastSeen < TimeSpan.FromSeconds(15)
            && peer.Status is EPeerStatus.Ready or EPeerStatus.Syncing && !string.IsNullOrWhiteSpace(peer.HostId))
            .Select(peer => new DeviceHostTarget(peer.HostId!, cluster.ClusterId, peer.PeerId, peer.InstanceId)))
            .GroupBy(target => target.HostId, StringComparer.Ordinal).Select(group => group.OrderBy(target => target.ClusterId, StringComparer.Ordinal).ThenBy(target => target.PeerId, StringComparer.Ordinal).First()).Take(128).ToArray();
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, async (target, cancellation) =>
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                long order = BeginRead();
                DeviceSnapshot snapshot = await remote.ReadAsync(new(target, new(EDeviceArea.WakeOnLan)), timeout.Token).ConfigureAwait(false);
                await ObserveAsync(target, snapshot, order, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
            }
        }).ConfigureAwait(false);
    }

    /// <summary>サービス終了後にプロフィールの排他を解放します。</summary>
    public override void Dispose()
    {
        base.Dispose();
        gate.Dispose();
    }
}
