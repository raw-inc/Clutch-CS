using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>観測条件と対象選択を管理します。</summary>
internal sealed partial class DiscoveryViewModel : ObservableObject
{
    private readonly InstallationViewModel settings;
    private readonly Func<ClientNetworkBinding, CancellationToken, Task<IReadOnlyList<ObservedTarget>>> scan;
    private readonly Func<CancellationToken, Task<Contracts.Installation.WebClientInstallation?>> findWebClient;

    /// <summary>実NICを列挙し、利用者の選択を待ちます。</summary>
    public DiscoveryViewModel(InstallationViewModel settings, Func<ClientNetworkBinding, CancellationToken, Task<IReadOnlyList<ObservedTarget>>>? scan = null,
        Func<CancellationToken, Task<Contracts.Installation.WebClientInstallation?>>? findWebClient = null)
    {
        this.settings = settings;
        this.scan = scan ?? ((binding, token) => InstallerDiscovery.ScanAsync(binding, settings.Operation == EInstallerOperation.Update, token));
        this.findWebClient = findWebClient ?? (token => WebClientCatalog.FindAsync(false, token));
        foreach (ClientNetworkInterface network in ObserverNetwork.GetInterfaces().Where(network => network.Available))
        {
            foreach (ClientNetworkAddress address in network.Addresses)
            {
                Networks.Add(new(new(network.InterfaceId, address.Address, address.BroadcastAddress, InstallationDefaults.ManagementPort), network.Name + " · " + address.Address));
            }
        }
    }

    /// <summary>選択可能な通信経路です。</summary>
    public ObservableCollection<NetworkChoice> Networks
    {
        get;
    } = [];

    /// <summary>走査した対象です。</summary>
    public ObservableCollection<TargetChoiceViewModel> Targets
    {
        get;
    } = [];

    /// <summary>このPCと接続先で関連付けを確認できたWebクライアントです。</summary>
    public ObservableCollection<WebClientChoiceViewModel> WebClients
    {
        get;
    } = [];

    /// <summary>観測したクラスタ名です。</summary>
    public ObservableCollection<ClusterChoice> Clusters
    {
        get;
    } = [];

    /// <summary>利用者が明示した一つのNIC・アドレスです。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedNetwork))]
    public partial NetworkChoice? SelectedNetwork
    {
        get; set;
    }

    /// <summary>検索を開始できる選択があるかです。</summary>
    public bool HasSelectedNetwork => SelectedNetwork is not null;

    /// <summary>全体、クラスタ、ピアの範囲です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsClusterScope)), NotifyPropertyChangedFor(nameof(IsPeerScope))]
    public partial int Scope
    {
        get; set;
    }

    /// <summary>選択したクラスタです。</summary>
    [ObservableProperty]
    public partial ClusterChoice? SelectedCluster
    {
        get; set;
    }

    /// <summary>受信したクラスター候補の数です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasClusters))]
    public partial int ClusterCount
    {
        get; set;
    }

    /// <summary>既存クラスターへの参加候補があるかです。</summary>
    public bool HasClusters => ClusterCount != 0;

    /// <summary>選択したネットワークの受信確認が正常終了したかです。</summary>
    [ObservableProperty]
    public partial bool SearchCompleted
    {
        get; set;
    }

    /// <summary>観測数を表示します。</summary>
    [ObservableProperty]
    public partial int Count
    {
        get; set;
    }

    /// <summary>クラスタ選択を表示するかです。</summary>
    public bool IsClusterScope => (ETargetScope)Scope == ETargetScope.Cluster;

    /// <summary>個別のチェックを許可するかです。</summary>
    public bool IsPeerScope => (ETargetScope)Scope == ETargetScope.Peers;

    /// <summary>選択した一つのネットワークを設定へ固定します。</summary>
    public void CaptureNetwork()
    {
        if (SelectedNetwork is null || !Networks.Contains(SelectedNetwork))
        {
            throw new InvalidDataException(settings.Text("Select the network to use with Clutch-CS.", "Clutch-CSで使用するネットワークを選択してください。"));
        }
        settings.Networks.Clear();
        settings.Networks.Add(SelectedNetwork.Binding);
    }

    /// <summary>受動走査し、表示collectionを更新します。</summary>
    /// <returns>走査完了です。</returns>
    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        CaptureNetwork();
        ClientNetworkBinding binding = settings.Networks.Single();
        ClusterChoice? previousCluster = SelectedCluster;
        ClearObserved();
        IReadOnlyList<ObservedTarget> observed = await scan(binding, cancellationToken).ConfigureAwait(true);
        settings.Observed.AddRange(observed);
        foreach (ObservedTarget target in observed)
        {
            Targets.Add(new(target, settings.Japanese));
            AddCluster(target);
        }
        if (settings.Operation == EInstallerOperation.Update)
        {
            settings.Components.Existing = await findWebClient(cancellationToken).ConfigureAwait(true);
            AddWebClients(observed);
        }
        int previousScope = Scope;
        IEnumerable<string> localRoots = InstallationCatalog.GetRoots();
        if (settings.Operation is EInstallerOperation.Install or EInstallerOperation.Update)
        {
            localRoots = [];
        }
        foreach (string root in localRoots)
        {
            string marker = Path.Combine(root, InstallationFiles.RecordName);
            if (File.Exists(marker))
            {
                Contracts.Installation.InstallationRecord record = await InstallationFiles.ReadAsync(root, cancellationToken).ConfigureAwait(true);
                if (SameNetwork(record.Settings.Network))
                {
                    AddLocal(record);
                }
            }
        }
        Scope = previousScope;
        SelectedCluster = Clusters.FirstOrDefault(cluster => cluster == previousCluster) ?? Clusters.FirstOrDefault();
        settings.NewCluster = SelectedCluster is null;
        Count = Targets.Count;
        ApplyScope();
        SearchCompleted = true;
    }

    /// <summary>停止中のローカル配置を選択して一覧へ追加します。</summary>
    /// <returns>選択と配置検証の完了です。</returns>
    public async Task BrowseLocalAsync(IInstallerInteraction interaction, CancellationToken cancellationToken = default)
    {
        string? path = await interaction.PickFolderAsync(cancellationToken).ConfigureAwait(true);
        if (path is not null)
        {
            await AddLocalAsync(path, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>停止したローカル配置を追加します。</summary>
    /// <returns>所有確認と表示更新の完了です。</returns>
    public async Task AddLocalAsync(string root, CancellationToken cancellationToken = default)
    {
        Contracts.Installation.InstallationRecord record = await InstallationFiles.ReadAsync(root, cancellationToken).ConfigureAwait(true);
        if (!SameNetwork(record.Settings.Network))
        {
            throw new InvalidDataException(settings.Text("This installation uses a different network. Go back and select its network.", "このインストール先は別のネットワークを使用しています。戻ってネットワークを選び直してください。"));
        }
        AddLocal(record);
    }

    /// <summary>選択したネットワーク上の停止済み配置を候補へ追加します。</summary>
    private void AddLocal(Contracts.Installation.InstallationRecord record)
    {
        ObservedTarget target = new(record.Settings.ClusterId, record.Settings.PeerId, record.Settings.Network, null, record);
        if (Targets.Any(item => item.Target.Binding.Port == target.Binding.Port && string.Equals(item.Target.ClusterId, target.ClusterId, StringComparison.Ordinal) && string.Equals(item.Target.PeerId, target.PeerId, StringComparison.Ordinal)))
        {
            return;
        }
        settings.Observed.Add(target);
        Scope = (int)ETargetScope.Peers;
        Targets.Add(new(target, settings.Japanese) { Selected = true });
        AddCluster(target);
        Count = Targets.Count;
    }

    /// <summary>表示で確認した対象集合を実行入力へ固定します。</summary>
    public void CaptureTargets()
    {
        if (!SearchCompleted)
        {
            throw new InvalidDataException(settings.Text("Search for clusters before continuing.", "クラスターを検索してから次へ進んでください。"));
        }
        CaptureNetwork();
        if (settings.Operation == EInstallerOperation.Install && !settings.NewCluster)
        {
            ClusterChoice cluster = SelectedCluster ?? throw new InvalidDataException(settings.Text("Select an existing cluster.", "参加するクラスターを選択してください。"));
            if (!Clusters.Contains(cluster))
            {
                throw new InvalidDataException(settings.Text("Search again and select a cluster.", "再検索してクラスターを選択してください。"));
            }
            settings.Cluster = cluster.Name;
            settings.Networks[0] = settings.Networks[0] with { Port = cluster.Port };
        }
        settings.Selected.Clear();
        settings.Selected.AddRange(Targets.Where(item => item.Selected).Select(item => item.Target));
        settings.SelectedWebClients.Clear();
        foreach (WebClientChoiceViewModel item in WebClients.Where(item => item.Selected))
        {
            ObservedWebClient client = item.Target;
            ObservedTarget? selectedCore = settings.Selected.FirstOrDefault(core => client.Core is not null
                && string.Equals(core.Peer?.HostId, client.Core.Peer?.HostId, StringComparison.Ordinal) && core.Installation?.WebClient?.InstallationId == client.Installation.InstallationId);
            if (selectedCore is not null)
            {
                client = client with { Core = selectedCore };
            }
            settings.SelectedWebClients.Add(client);
        }
    }

    /// <summary>範囲が変わったときに対象を再選択します。</summary>
    partial void OnScopeChanged(int value) => ApplyScope();

    /// <summary>クラスタ選択を初回参加先と保守範囲へ反映します。</summary>
    partial void OnSelectedClusterChanged(ClusterChoice? value)
    {
        if (value is not null)
        {
            settings.Cluster = value.Name;
            settings.NewCluster = false;
        }
        ApplyScope();
    }

    /// <summary>チェック状態を選択した範囲へ揃えます。</summary>
    private void ApplyScope()
    {
        foreach (TargetChoiceViewModel target in Targets)
        {
            target.Selected = (ETargetScope)Scope == ETargetScope.All || ((ETargetScope)Scope == ETargetScope.Cluster && SelectedCluster is not null
                && string.Equals(target.Target.ClusterId, SelectedCluster.Name, StringComparison.Ordinal) && target.Target.Binding.Port == SelectedCluster.Port);
        }
    }

    /// <summary>別NICの候補を選び直したネットワークへ持ち越しません。</summary>
    partial void OnSelectedNetworkChanged(NetworkChoice? value)
    {
        settings.Networks.Clear();
        settings.NewCluster = true;
        ClearObserved();
    }

    /// <summary>再検索・通信経路変更の前に以前の対象を無効化します。</summary>
    private void ClearObserved()
    {
        settings.Observed.Clear();
        settings.Selected.Clear();
        settings.SelectedWebClients.Clear();
        WebClients.Clear();
        SelectedCluster = null;
        Targets.Clear();
        Clusters.Clear();
        Count = 0;
        ClusterCount = 0;
        SearchCompleted = false;
    }

    /// <summary>同じPCで共有するクライアントを一度だけ表示します。</summary>
    private void AddWebClients(IReadOnlyList<ObservedTarget> observed)
    {
        Contracts.Installation.WebClientInstallation? local = settings.Components.Existing;
        if (local is not null)
        {
            WebClients.Add(new(new(local), settings.Japanese));
        }
        HashSet<(string Host, Guid Installation)> seen = [];
        foreach (ObservedTarget target in observed)
        {
            if (target.Installation?.WebClient is not { } client || target.Peer is not { } peer)
            {
                continue;
            }
            if (local?.InstallationId == client.InstallationId && string.Equals(peer.Address, target.Binding.BindAddress, StringComparison.Ordinal))
            {
                continue;
            }
            if (seen.Add((peer.HostId, client.InstallationId)))
            {
                WebClients.Add(new(new(client, target), settings.Japanese));
            }
        }
    }

    /// <summary>クラスター名とportで候補を区別します。</summary>
    private void AddCluster(ObservedTarget target)
    {
        ClusterChoice cluster = new(target.ClusterId, target.Binding.Port);
        if (!Clusters.Contains(cluster))
        {
            Clusters.Add(cluster);
            ClusterCount = Clusters.Count;
        }
    }

    /// <summary>停止中の配置も選択されたNICとIPに限定します。</summary>
    /// <returns>ネットワークの一致です。</returns>
    private bool SameNetwork(ClientNetworkBinding binding) => SelectedNetwork is not null
        && string.Equals(binding.InterfaceId, SelectedNetwork.Binding.InterfaceId, StringComparison.Ordinal)
        && string.Equals(binding.BindAddress, SelectedNetwork.Binding.BindAddress, StringComparison.Ordinal);
}
