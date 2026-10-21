using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Networking;

/// <summary>明示した複数NICの受動監視と、管理対象の一意な通信経路を所有します。</summary>
internal sealed class ObserverConnection : IAsyncDisposable
{
    private readonly string bindingPath;
    private readonly string legacyPath;
    private readonly SemaphoreSlim changes = new(1, 1);

    // 抑制理由: 読取中の集合を変更せず、全監視のスナップショット参照をVolatile/Interlockedで原子的に交換します。
    [SuppressMessage("SourceQuality", "RAW170", Justification = "読取側との競合を避けるため、公開済み配列を変更せず参照全体を交換します。")]
    private KeyValuePair<ClientNetworkBinding, UdpObserver>[] observers = [];
    private bool disposed;

    /// <summary>秘密値を含まないNIC選択の保存先を指定します。</summary>
    public ObserverConnection(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        bindingPath = Path.Combine(stateDirectory, "networks.json");
        legacyPath = Path.Combine(stateDirectory, "network.json");
    }

    /// <summary>明示された割当だけを復元し、旧単一NIC設定も読み込めます。</summary>
    /// <returns>保存済み監視の開始です。</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(bindingPath))
        {
            ClientNetworkBinding[] bindings = JsonSerializer.Deserialize<ClientNetworkBinding[]>(await File.ReadAllTextAsync(bindingPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
                ?? throw new InvalidDataException("保存したNIC選択を読めません。");
            await SelectManyAsync(bindings, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (File.Exists(legacyPath))
            {
                ClientNetworkBinding binding = JsonSerializer.Deserialize<ClientNetworkBinding>(await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false), ContractJson.Options)
                    ?? throw new InvalidDataException("保存したNIC選択を読めません。");
                await SelectAsync(binding, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>先頭の編集対象と、全監視の個別の成立状態を返します。</summary>
    /// <returns>NIC障害を他の監視の障害へ拡大しない状態です。</returns>
    public ClientNetworkState GetState()
    {
        ClientNetworkStatus[] statuses = Volatile.Read(ref observers).Select(item => item.Value.CaptureBindingStatus()).ToArray();
        ClientNetworkStatus? first = statuses.FirstOrDefault();
        return new(first?.Binding, ObserverNetwork.GetInterfaces(), first?.Listening ?? false, first?.Problem, statuses);
    }

    /// <summary>同じクラスタの観測をまとめ、異なるPeer実体とCoordinator候補は保持します。</summary>
    /// <returns>明示した全NICから実受信した一覧です。</returns>
    public IReadOnlyList<DiscoveredCluster> GetClusters() => Volatile.Read(ref observers)
        .SelectMany(item => item.Value.GetClusters()).GroupBy(cluster => cluster.ClusterId, StringComparer.Ordinal)
        .Select(group => new DiscoveredCluster(group.Key,
            group.SelectMany(cluster => cluster.Peers).GroupBy(peer => peer.InstanceId).Select(peers => peers.MaxBy(peer => peer.LastSeen)!).ToArray(),
            group.SelectMany(cluster => cluster.CoordinatorCandidates).Distinct(StringComparer.Ordinal).ToArray(), group.Max(cluster => cluster.LastSeen)))
        .OrderBy(cluster => cluster.ClusterId, StringComparer.Ordinal).ToArray();

    /// <summary>対象を観測した一経路だけを返し、複数経路・複数実体の曖昧さを拒否します。</summary>
    /// <returns>通信可能な一意の経路。不能時は別NICへ代替しません。</returns>
    public UdpObserver? FindObserver(string clusterId, string peerId) => ObserverRouting.Find(Volatile.Read(ref observers).Select(item => item.Value), clusterId, peerId);

    /// <summary>旧APIの明示的な単一NIC選択は全監視をその一件に置換します。</summary>
    /// <returns>保存した監視の状態です。</returns>
    public Task<ClientNetworkState> SelectAsync(ClientNetworkBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return SelectManyAsync([binding], cancellationToken);
    }

    /// <summary>全割当を検証・保存して交換し、変化しない監視と開始済み要求を維持します。</summary>
    /// <returns>保存後の全監視状態です。</returns>
    // 抑制理由: 生成した監視処理は交換成功時にフィールドへ所有権を渡し、失敗時だけこのスコープで解放します。
    [SuppressMessage("SourceQuality", "RAW141", Justification = "同一割当は再利用し、新規監視の所有権だけを成功時にfieldへ渡します。"),
     SuppressMessage("SourceQuality", "RAW140", Justification = "永続化・交換の成否で解放対象を区別し、全対象の非同期停止を待ちます。")]
    public async Task<ClientNetworkState> SelectManyAsync(IReadOnlyList<ClientNetworkBinding> bindings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ClientNetworkBinding[] requested = bindings.ToArray();
        if (32 < requested.Length || requested.Any(binding => binding is null)
            || requested.DistinctBy(binding => (binding.InterfaceId, binding.Port)).Count() != requested.Length)
        {
            throw new ArgumentException("監視は32件まで、同一NICとUDP portの重複指定はできません。", nameof(bindings));
        }
        await changes.WaitAsync(cancellationToken).ConfigureAwait(false);
        List<UdpObserver> created = [];
        bool exchanged = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            KeyValuePair<ClientNetworkBinding, UdpObserver>[] before = Volatile.Read(ref observers);
            List<KeyValuePair<ClientNetworkBinding, UdpObserver>> next = [];
            foreach (ClientNetworkBinding binding in requested)
            {
                UdpObserver? observer = before.FirstOrDefault(item => item.Key == binding).Value;
                if (observer is null)
                {
                    observer = new UdpObserver(new ObserverOptions(Guid.NewGuid().ToString("N"), binding));
                    created.Add(observer);
                    await observer.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                next.Add(new(binding, observer));
            }
            string pending = bindingPath + ".pending";
            await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(requested, ContractJson.Options), cancellationToken).ConfigureAwait(false);
            File.Move(pending, bindingPath, true);
            Volatile.Write(ref observers, next.ToArray());
            exchanged = true;
            foreach (KeyValuePair<ClientNetworkBinding, UdpObserver> previous in before.Where(item => !next.Any(current => ReferenceEquals(current.Value, item.Value))))
            {
                await ((IAsyncDisposable)previous.Value).DisposeAsync().ConfigureAwait(false);
            }
            return GetState();
        }
        finally
        {
            if (!exchanged)
            {
                foreach (UdpObserver observer in created)
                {
                    await ((IAsyncDisposable)observer).DisposeAsync().ConfigureAwait(false);
                }
            }
            changes.Release();
        }
    }

    /// <summary>所有する全監視を停止します。</summary>
    /// <returns>選択変更と競合しない通信終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await changes.WaitAsync().ConfigureAwait(false);
        try
        {
            disposed = true;
            KeyValuePair<ClientNetworkBinding, UdpObserver>[] before = Interlocked.Exchange(ref observers, []);
            foreach (KeyValuePair<ClientNetworkBinding, UdpObserver> item in before)
            {
                await ((IAsyncDisposable)item.Value).DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            changes.Release();
        }
        changes.Dispose();
    }
}
