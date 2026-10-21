using CommunityToolkit.Mvvm.ComponentModel;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

namespace ReArtWord.Contents.BackgroundService.Installer.ViewModels;

/// <summary>このPCの管理画面の有無とHTTP待受を選択します。</summary>
internal sealed partial class ComponentsViewModel : ObservableObject
{
    /// <summary>クラスターのサービスを削除対象に含めます。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall))]
    public partial bool RemoveService
    {
        get; set;
    } = true;

    /// <summary>このPCのクライアントを明示して削除します。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUninstall))]
    public partial bool RemoveWebClient
    {
        get; set;
    }

    /// <summary>削除対象が一つ以上選択されています。</summary>
    public bool CanUninstall => RemoveService || RemoveWebClient;

    /// <summary>新たにクライアントを導入する選択です。</summary>
    [ObservableProperty]
    public partial bool Install
    {
        get; set;
    } = true;

    /// <summary>新規導入時のHTTP portです。</summary>
    [ObservableProperty]
    public partial decimal Port
    {
        get; set;
    } = WebClientSettings.DefaultPort;

    /// <summary>明示した場合だけPC共通のサービスとして起動します。</summary>
    [ObservableProperty]
    public partial bool RunAsService
    {
        get; set;
    }

    /// <summary>既にこのPCで利用できるクライアントです。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasExisting))]
    public partial WebClientInstallation? Existing
    {
        get; set;
    }

    /// <summary>重複導入を省く場合です。</summary>
    public bool HasExisting => Existing is not null;

    /// <summary>導入完了時に確定したWebクライアントです。</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInstalledClient)), NotifyPropertyChangedFor(nameof(ClientUrl))]
    public partial WebClientInstallation? InstalledClient
    {
        get; set;
    }

    /// <summary>完了画面で案内できるクライアントがあるかです。</summary>
    public bool HasInstalledClient => InstalledClient is not null;

    /// <summary>実際に導入または再利用されたクライアントのURLです。</summary>
    public string ClientUrl
    {
        get
        {
            if (InstalledClient is null)
            {
                return string.Empty;
            }
            return ClientLaunchFiles.GetUrl(InstalledClient.Port).OriginalString;
        }
    }

    /// <summary>導入結果から既存または新規の管理画面を開きます。</summary>
    /// <returns>起動要求の完了です。</returns>
    public Task OpenAsync(Services.IInstallerInteraction interaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        cancellationToken.ThrowIfCancellationRequested();
        if (InstalledClient is { } client)
        {
            interaction.OpenClient(client.Root);
        }
        return Task.CompletedTask;
    }

    /// <summary>副作用を行わず既存導入を調べます。</summary>
    /// <returns>初期選択の更新です。</returns>
    public async Task RefreshAsync(bool forRemoval, CancellationToken cancellationToken = default)
    {
        Existing = await WebClientCatalog.FindAsync(forRemoval, cancellationToken).ConfigureAwait(true);
        if (HasExisting)
        {
            Install = false;
        }
        else
        {
            RemoveWebClient = false;
        }
    }

    /// <summary>確認した選択を実行入力へ固定します。</summary>
    /// <returns>新規、既存利用または導入なしの設定です。</returns>
    public WebClientSettings Capture(bool japanese)
    {
        if (Existing is not null)
        {
            return new(false, Existing.Port, Existing.Root, Existing.RunAsService);
        }
        if (!Install)
        {
            return new(false, WebClientSettings.DefaultPort);
        }
        if (Port <= System.Net.IPEndPoint.MinPort || System.Net.IPEndPoint.MaxPort < Port || decimal.Truncate(Port) != Port)
        {
            throw new InvalidDataException(new Localization.InstallerText(japanese)["WebClientPortError"]);
        }
        return new(Install, (int)Port, RunAsService: RunAsService);
    }
}
