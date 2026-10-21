using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReArtWord.Contents.BackgroundService.Installer.Models;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Installer.Services;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>実行開始前のウィザード入力です。実行時には契約へ複製します。</summary>
internal sealed partial class InstallationViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// <summary>新規クラスターの既定名です。</summary>
    private static string DefaultClusterName => "clutch-cluster";

    /// <summary>このPCのWebクライアントの導入設定です。</summary>
    public ComponentsViewModel Components
    {
        get;
    } = new();

    /// <summary>トップ画面から閲覧するサードパーティライセンスです。</summary>
    public LicensesViewModel Licenses
    {
        get;
    } = new();

    /// <summary>ライセンス画面にも選択言語を反映します。</summary>
    partial void OnJapaneseChanged(bool value) => Licenses.Japanese = value;

    /// <summary>日本語表示を選択したかです。</summary>
    [ObservableProperty]
    public partial bool Japanese
    {
        get; set;
    }

    /// <summary>Install / Update / Repair / Uninstallです。</summary>
    [ObservableProperty]
    public partial EInstallerOperation Operation
    {
        get; set;
    } = EInstallerOperation.Install;

    /// <summary>同意の状態です。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LicenseConfirmed))]
    public partial bool Accepted
    {
        get; set;
    }

    /// <summary>ライセンス末尾まで表示したかです。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LicenseConfirmed))]
    public partial bool LicenseRead
    {
        get; set;
    }

    /// <summary>末尾の表示と明示同意が揃っているかです。</summary>
    public bool LicenseConfirmed => LicenseRead && Accepted;

    /// <summary>Viewが通知したライセンス末尾の表示を記録します。</summary>
    [RelayCommand]
    private void ReadLicense() => LicenseRead = true;

    /// <summary>radioの非選択通知を入力へ戻さず、利用者が選んだ参加方式を反映します。</summary>
    [RelayCommand]
    private void ChooseClusterMode(bool create) => NewCluster = create;

    /// <summary>選択した修復方式を反映します。</summary>
    [RelayCommand]
    private void ChooseRepairMode(bool reinstall) => Reinstall = reinstall;

    /// <summary>再配置時のデータ保持方式を反映します。</summary>
    [RelayCommand]
    private void ChooseDatabaseRetention(bool keep) => KeepDatabase = keep;

    /// <summary>新規クラスタを作成するかです。</summary>
    [ObservableProperty]
    public partial bool NewCluster
    {
        get; set;
    } = true;

    /// <summary>クラスタ名です。</summary>
    [ObservableProperty]
    public partial string Cluster
    {
        get; set;
    } = DefaultClusterName;

    /// <summary>新規クラスターで使う管理portです。参加時は受信した値を使います。</summary>
    [ObservableProperty]
    public partial decimal Port
    {
        get; set;
    } = InstallationDefaults.ManagementPort;

    /// <summary>ホスト名です。</summary>
    [ObservableProperty]
    public partial string Host
    {
        get; set;
    } = Environment.MachineName;

    /// <summary>ピア名です。</summary>
    [ObservableProperty]
    public partial string Peer
    {
        get; set;
    } = DefaultPeerName(Environment.MachineName, DefaultClusterName);

    /// <summary>順位の自動計算を使用するかです。</summary>
    [ObservableProperty]
    public partial bool AutomaticPriority
    {
        get; set;
    } = true;

    /// <summary>手動の優先順位です。</summary>
    [ObservableProperty]
    public partial int Priority
    {
        get; set;
    } = InstallationDefaults.InitialPriority;

    /// <summary>高度な設定を表示するかです。</summary>
    [ObservableProperty]
    public partial bool Advanced
    {
        get; set;
    }

    /// <summary>ユーザー権限で導入するかです。</summary>
    [ObservableProperty]
    public partial bool PerUser
    {
        get; set;
    }

    /// <summary>導入先です。</summary>
    [ObservableProperty]
    public partial string Root
    {
        get; set;
    } = InstallationPolicy.DefaultRoot(DefaultClusterName, false);

    /// <summary>オフライン配布物の親フォルダーです。</summary>
    [ObservableProperty]
    public partial string Package
    {
        get; set;
    } = BundledRelease.Root();

    /// <summary>Webクライアントの表示設定です。</summary>
    public AppearanceViewModel Appearance
    {
        get;
    } = new();

    /// <summary>操作する通信範囲です。</summary>
    public List<ClientNetworkBinding> Networks
    {
        get;
    } = [];

    /// <summary>最後の走査結果です。</summary>
    public List<ObservedTarget> Observed
    {
        get;
    } = [];

    /// <summary>確認済み実行対象です。</summary>
    public List<ObservedTarget> Selected
    {
        get;
    } = [];

    /// <summary>全体・クラスタ・ピアの範囲です。</summary>
    [ObservableProperty]
    public partial int Scope
    {
        get; set;
    }

    /// <summary>自動修復か再インストールかです。</summary>
    [ObservableProperty]
    public partial bool Reinstall
    {
        get; set;
    }

    /// <summary>DB保持を選択したかです。</summary>
    [ObservableProperty]
    public partial bool KeepDatabase
    {
        get; set;
    } = true;

    /// <summary>設定保持を選択したかです。</summary>
    [ObservableProperty]
    public partial bool KeepConfiguration
    {
        get; set;
    } = true;

    /// <summary>稼働中更新の影響を確認したかです。</summary>
    [ObservableProperty]
    public partial bool AllowActive
    {
        get; set;
    }

    /// <summary>破壊的操作の最終警告を確認したかです。</summary>
    [ObservableProperty]
    public partial bool Confirmed
    {
        get; set;
    }

    /// <summary>実行結果の表示です。</summary>
    [ObservableProperty]
    public partial string Result
    {
        get; set;
    } = string.Empty;

    /// <summary>通常参加まで確認できた初回導入かです。</summary>
    [ObservableProperty]
    public partial bool Installed
    {
        get; set;
    }

    /// <summary>表示言語で短い文言を選びます。</summary>
    /// <returns>現在の表示文言です。</returns>
    public string Text(string english, string japanese)
    {
        if (Japanese)
        {
            return japanese;
        }
        return english;
    }

    /// <summary>設定欄に対応するfolderまたは画像を選択します。</summary>
    /// <returns>選択したpathの反映完了です。</returns>
    public async Task BrowseAsync(IInstallerInteraction interaction, string field, CancellationToken cancellationToken = default)
    {
        string? path = field switch
        {
            "Icon" or "Favicon" => await interaction.PickImageAsync(cancellationToken).ConfigureAwait(true),
            _ => await interaction.PickFolderAsync(cancellationToken).ConfigureAwait(true),
        };
        if (path is not null)
        {
            switch (field)
            {
                case "Root": Root = path; break;
                case "Icon": Appearance.Icon = path; break;
                case "Favicon": Appearance.Favicon = path; break;
            }
        }
    }

    /// <summary>現在の入力から実行用の固定設定を作ります。</summary>
    /// <returns>画面から独立した導入設定です。</returns>
    public InstallationSettings Settings()
    {
        if (Networks.Count != 1)
        {
            throw new InvalidDataException(Text("Select a network first.", "通信に使用するネットワークを選んでください。"));
        }
        ClientNetworkBinding network = Networks.Single();
        if (NewCluster)
        {
            if (Port <= System.Net.IPEndPoint.MinPort || System.Net.IPEndPoint.MaxPort < Port || decimal.Truncate(Port) != Port)
            {
                throw new InvalidDataException(Text("Enter a port between 1 and 65535.", "ポート番号は1〜65535の整数を指定してください。"));
            }
            network = network with { Port = (int)Port };
        }
        int priority = Priority;
        if (AutomaticPriority)
        {
            priority = InstallationPolicy.AutomaticPriority(Observed.Where(target => string.Equals(target.ClusterId, Cluster, StringComparison.Ordinal) && target.Binding.Port == network.Port).Select(target => target.Peer).OfType<Contracts.Cluster.PeerInfo>().ToArray());
        }
        ClientAppearance appearance = new();
        if (Advanced)
        {
            appearance = Appearance.Capture();
        }
        WebClientSettings client = Components.Capture(Japanese);
        if (!Advanced && client.ExistingRoot is null)
        {
            client = client with { RunAsService = false };
        }
        return new(Root, Package, Cluster, Host, Peer, priority, NewCluster, Advanced && PerUser, network, appearance, LicenseConfirmed, client);
    }

    /// <summary>クラスター名に合わせて、未編集の登録名と配置先を更新します。</summary>
    partial void OnClusterChanged(string oldValue, string newValue)
    {
        string defaultPeer = DefaultPeerName(Host, oldValue);
        if (string.Equals(Peer, defaultPeer, StringComparison.Ordinal))
        {
            Peer = DefaultPeerName(Host, newValue);
        }
        string defaultRoot = InstallationPolicy.DefaultRoot(oldValue, PerUser);
        if (string.Equals(Root, defaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            Root = InstallationPolicy.DefaultRoot(newValue, PerUser);
        }
    }

    /// <summary>ホスト変更時も、手入力された登録名は維持します。</summary>
    partial void OnHostChanged(string oldValue, string newValue)
    {
        string previous = DefaultPeerName(oldValue, Cluster);
        if (string.Equals(Peer, previous, StringComparison.Ordinal))
        {
            Peer = DefaultPeerName(newValue, Cluster);
        }
    }

    /// <summary>ホスト名とクラスター名から既定のピア名を作成します。</summary>
    /// <returns>ホストとクラスターに対応するピア名です。</returns>
    private static string DefaultPeerName(string host, string cluster) => host + "-" + cluster + "-peer";

    /// <summary>ユーザー権限では書込み可能な既定先へ切り替えます。</summary>
    partial void OnPerUserChanged(bool value)
    {
        string previous = InstallationPolicy.DefaultRoot(Cluster, !value);
        if (string.Equals(Root, previous, StringComparison.OrdinalIgnoreCase))
        {
            Root = InstallationPolicy.DefaultRoot(Cluster, value);
        }
    }

}
