using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Loading;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.CoreService.Files.Transfers;

/// <summary>共通キュー所有Core自身の明示NICだけを使う受動監視です。</summary>
internal sealed class TransferConnections(CommonConfiguration configuration) : IAsyncDisposable
{
    private readonly List<UdpObserver> observers = [];
    private readonly List<UdpObserver> temporary = [];

    /// <summary>転送対象への唯一の実受信経路を返します。</summary>
    /// <returns>曖昧でない経路だけです。</returns>
    internal UdpObserver? Find(string cluster, string peer) => ObserverRouting.Find(observers, cluster, peer);

    /// <summary>共通キューの開始前に所有する監視群を起動します。</summary>
    /// <returns>すべての明示監視の開始です。</returns>
    // 抑制理由: 作成したObserverを一覧で所有し、DisposeAsyncで解放します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "作成した監視の所有権をフィールドへ渡します。")]
    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ClientNetworkBinding[] bindings;
        if (configuration.TransferQueueNetworksFile is { } path)
        {
            bindings = JsonSerializer.Deserialize<ClientNetworkBinding[]>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)
                ?? throw new InvalidDataException("Transfer queue networks are missing.");
        }
        else
        {
            string interfaceId = configuration.Cluster.InterfaceId ?? ObserverNetwork.GetInterfaces()
                .Single(network => network.Addresses.Any(address => string.Equals(address.Address, configuration.Cluster.BindAddress, StringComparison.Ordinal))).InterfaceId;
            bindings = [new(interfaceId, configuration.Cluster.BindAddress, configuration.Cluster.BroadcastAddress, configuration.Cluster.Port)];
        }
        if (bindings.Length is < 1 or > 32 || bindings.DistinctBy(binding => (binding.InterfaceId, binding.Port)).Count() != bindings.Length)
        {
            throw new InvalidDataException("Transfer queue requires 1 to 32 unique network bindings.");
        }
        foreach (ClientNetworkBinding binding in bindings)
        {
            UdpObserver observer = new(new ObserverOptions(Guid.NewGuid().ToString("N"), binding));
            observers.Add(observer);
            await observer.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>実発見した別ポートも同じ明示NICで観測し、元実体の存在を確認します。</summary>
    /// <returns>一件の対象が実確認できるまでの準備です。</returns>
    // 抑制理由: 一件用の監視をtemporaryとobserversで所有し、ReleaseTemporaryAsyncで回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "一件用監視をフィールドで所有します。")]
    internal async Task PrepareAsync(Contracts.Files.Transfers.FileTransferRequest request, CancellationToken cancellationToken = default)
    {
        ClientNetworkBinding[] bindings = observers.Select(observer => observer.CaptureBindingStatus().Binding).DistinctBy(binding => binding.InterfaceId).ToArray();
        foreach (int port in new[] { request.Source.Port, request.Destination.Port }.Distinct())
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            foreach (ClientNetworkBinding binding in bindings)
            {
                if (observers.Any(observer => observer.CaptureBindingStatus().Binding is { } existing && existing.Port == port && string.Equals(existing.InterfaceId, binding.InterfaceId, StringComparison.Ordinal)))
                {
                    continue;
                }
                UdpObserver observer = new(new ObserverOptions(Guid.NewGuid().ToString("N"), binding with
                {
                    Port = port
                }));
                temporary.Add(observer);
                observers.Add(observer);
                await observer.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(200));
        while (Find(request.Source.ClusterId, request.Source.PeerId) is null || Find(request.Destination.ClusterId, request.Destination.PeerId) is null)
        {
            await timer.WaitForNextTickAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>終了したコピーのためだけに追加したポート監視を回収します。</summary>
    /// <returns>一件用監視の解放です。</returns>
    // 抑制理由: 一件用としてフィールドで保持したObserverを終了後に回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW140", Justification = "保持していた一件用監視の寿命を終了します。")]
    internal async Task ReleaseTemporaryAsync(CancellationToken cancellationToken = default)
    {
        foreach (UdpObserver observer in temporary)
        {
            observers.Remove(observer);
            await ((IAsyncDisposable)observer).DisposeAsync().ConfigureAwait(false);
        }
        temporary.Clear();
    }

    /// <summary>キュー停止後に通信資源をすべて解放します。</summary>
    /// <returns>所有監視の終了です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        foreach (UdpObserver observer in observers)
        {
            await ((IAsyncDisposable)observer).DisposeAsync().ConfigureAwait(false);
        }
        observers.Clear();
        temporary.Clear();
    }
}
