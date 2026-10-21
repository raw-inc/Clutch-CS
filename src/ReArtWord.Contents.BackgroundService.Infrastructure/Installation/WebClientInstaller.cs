using ReArtWord.Contents.BackgroundService.Contracts.Updates.Releases;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>WebクライアントをPCの独立した導入として扱い、重複コピーを避けます。</summary>
public sealed class WebClientInstaller
{
    private readonly IInstallationPlatform platform;

    /// <summary>OS索引への登録を受け取ります。</summary>
    public WebClientInstaller(IInstallationPlatform platform) => this.platform = platform;

    /// <summary>確認後に別の導入が完了した場合も既存クライアントを使います。</summary>
    /// <returns>実際に使用する導入選択です。</returns>
    public async Task<InstallationSettings> ResolveAsync(InstallationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.WebClient is { Install: true } && await platform.FindWebClientAsync(cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return settings with { WebClient = new(false, existing.Port, existing.Root, existing.RunAsService) };
        }
        return settings;
    }

    /// <summary>既存の設定を保持し、未対応の旧版には自動起動に必要なプログラムを配置します。</summary>
    /// <returns>利用するクライアント、または導入なしのnullです。</returns>
    public async Task<WebClientInstallation?> InstallAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        WebClientSettings? selection = record.Settings.WebClient;
        if (selection is null)
        {
            return null;
        }
        if (!selection.Install)
        {
            if (selection.ExistingRoot is null)
            {
                return null;
            }
            return await WebClientCatalog.ReadAsync(selection.ExistingRoot, cancellationToken).ConfigureAwait(false);
        }
        string source = Path.Combine(record.Settings.PackageRoot, "client");
        Contracts.Updates.Releases.ReleaseManifest manifest = await LocalUpdateExecutor.ReadManifestAsync(source, cancellationToken).ConfigureAwait(false);
        bool perUser = !selection.RunAsService;
        string root = InstallationFiles.Root(selection.ExistingRoot ?? platform.WebClientRoot(perUser));
        string parent = Path.GetDirectoryName(root)!;
        if (!Directory.Exists(parent))
        {
            platform.PrepareDirectory(parent, perUser);
        }
        using FileStream clientLock = WebClientCatalog.AcquireLock(root);
        WebClientInstallation? retained = null;
        if (Directory.Exists(root))
        {
            if (!File.Exists(Path.Combine(root, WebClientCatalog.RemovedRecordName)))
            {
                WebClientInstallation existing = await WebClientCatalog.ReadAsync(root, false, cancellationToken).ConfigureAwait(false);
                string executable = Path.Combine(WebClientCatalog.ProgramRoot(existing), existing.EntryPoint);
                if (File.Exists(executable) && existing.AutomaticStartup)
                {
                    await ClientLaunchFiles.WriteClientAsync(existing, cancellationToken).ConfigureAwait(false);
                    await platform.RegisterWebClientAsync(existing, cancellationToken).ConfigureAwait(false);
                    return existing;
                }
                retained = existing;
                await platform.StopWebClientAsync(existing, cancellationToken).ConfigureAwait(false);
                await platform.UnregisterWebClientAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                retained = await WebClientCatalog.ReadRemovedAsync(root, cancellationToken).ConfigureAwait(false);
            }
        }
        string pending = root + "-" + record.InstallationId.ToString("N");
        string backup = pending + "-previous";
        try
        {
            platform.PrepareDirectory(pending, retained?.PerUser ?? perUser);
            string program = Path.Combine(pending, "app");
            await ReleaseVerifier.CopyAsync(manifest, source, program, cancellationToken).ConfigureAwait(false);
            await LocalUpdateExecutor.WriteManifestAsync(manifest, program, cancellationToken).ConfigureAwait(false);
            WebClientInstallation client = new(retained?.InstallationId ?? record.InstallationId, root, manifest.EntryPoint, retained?.Port ?? selection.Port, retained?.PerUser ?? perUser,
                AutomaticStartup: true, RunAsService: selection.RunAsService, ProgramDirectory: Path.Combine(root, "app"));
            ClientAppearance appearance = record.Settings.Appearance;
            string savedAppearance = Path.Combine(root, "config", "appearance.json");
            if (retained is { AutomaticStartup: false } && !File.Exists(savedAppearance))
            {
                savedAppearance = Path.Combine(Path.GetDirectoryName(root)!, "config", "appearance.json");
            }
            if (retained is not null && File.Exists(savedAppearance))
            {
                appearance = JsonSerializer.Deserialize<ClientAppearance>(await File.ReadAllTextAsync(savedAppearance, cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
            }
            await WriteAppearanceAsync(pending, client, appearance, record, cancellationToken).ConfigureAwait(false);
            await InstallationFiles.WriteAsync(Path.Combine(pending, WebClientCatalog.RecordName), client, cancellationToken).ConfigureAwait(false);
            if (retained is not null)
            {
                Directory.Move(root, backup);
            }
            Directory.Move(pending, root);
            await ClientLaunchFiles.WriteClientAsync(client, cancellationToken).ConfigureAwait(false);
            await platform.RegisterWebClientAsync(client, cancellationToken).ConfigureAwait(false);
            return client;
        }
        finally
        {
            if (Directory.Exists(backup) && !Directory.Exists(root))
            {
                Directory.Move(backup, root);
            }
            if (Directory.Exists(pending))
            {
                InstallationFiles.DeleteDirectory(parent, Path.GetFileName(pending));
            }
        }
    }

    /// <summary>旧配置のクライアントを、クラスター削除後も起動できる形で保持します。</summary>
    /// <returns>保持するクライアントです。</returns>
    public async Task<WebClientInstallation> RetainAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        string root = Path.Combine(record.Settings.Root, "client");
        using FileStream clientLock = WebClientCatalog.AcquireLock(root);
        WebClientInstallation client = await WebClientCatalog.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        string marker = Path.Combine(client.Root, WebClientCatalog.RecordName);
        if (!File.Exists(marker))
        {
            ClientAppearance appearance = new();
            string path = Path.Combine(record.Settings.Root, "config", "appearance.json");
            if (File.Exists(path))
            {
                appearance = JsonSerializer.Deserialize<ClientAppearance>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
            }
            await SaveAsync(client, appearance, record, cancellationToken).ConfigureAwait(false);
        }

        // 削除時に旧ClientHostを更新したり、新たな起動方法へ変更したりしません。
        if (client.AutomaticStartup)
        {
            await platform.RegisterWebClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        await ClientLaunchFiles.WriteLinkAsync(record.Settings.Root, client, cancellationToken).ConfigureAwait(false);
        return client;
    }

    /// <summary>再インストール時に既存クライアントの起動登録を補います。</summary>
    /// <returns>明示削除されたクライアントを復元せず、残っている導入だけを確認します。</returns>
    public async Task RefreshStartupAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.WebClient is not { AutomaticStartup: true, RunAsService: true } client || !File.Exists(Path.Combine(client.Root, WebClientCatalog.RecordName)))
        {
            return;
        }
        using FileStream clientLock = WebClientCatalog.AcquireLock(client.Root);
        WebClientInstallation current = await WebClientCatalog.ReadAsync(client.Root, cancellationToken).ConfigureAwait(false);
        if (current.InstallationId != client.InstallationId)
        {
            throw new InvalidDataException("The web client installation has changed. Select it again.");
        }
        await ClientLaunchFiles.WriteClientAsync(current, cancellationToken).ConfigureAwait(false);
        await platform.RegisterWebClientAsync(current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>クライアント自身が使う表示設定と起動入口を保存します。</summary>
    /// <returns>独立した導入記録の保存です。</returns>
    private static async Task SaveAsync(WebClientInstallation client, ClientAppearance appearance, InstallationRecord record, CancellationToken cancellationToken = default)
    {
        await WriteAppearanceAsync(client.Root, client, appearance, record, cancellationToken).ConfigureAwait(false);
        await InstallationFiles.WriteAsync(Path.Combine(client.Root, WebClientCatalog.RecordName), client, cancellationToken).ConfigureAwait(false);
        await ClientLaunchFiles.WriteClientAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>一時配置中の画像参照も、確定後の配置先へ揃えます。</summary>
    /// <returns>クライアント所有の表示設定の保存です。</returns>
    private static async Task WriteAppearanceAsync(string destination, WebClientInstallation client, ClientAppearance appearance, InstallationRecord record, CancellationToken cancellationToken = default)
    {
        string config = Path.Combine(destination, "config");
        Directory.CreateDirectory(config);
        InstallationRecord owner = record with { Settings = record.Settings with { Root = destination, Appearance = appearance } };
        ClientAppearance copied = await InstallationImages.CopyAsync(owner, cancellationToken).ConfigureAwait(false);
        if (copied.IconPath is not null)
        {
            copied = copied with { IconPath = Path.Combine(client.Root, "config", Path.GetFileName(copied.IconPath)) };
        }
        if (copied.FaviconPath is not null)
        {
            copied = copied with { FaviconPath = Path.Combine(client.Root, "config", Path.GetFileName(copied.FaviconPath)) };
        }
        await InstallationFiles.WriteAsync(Path.Combine(config, "appearance.json"), copied, cancellationToken).ConfigureAwait(false);
        await InstallationFiles.WriteAsync(Path.Combine(config, "network.json"), record.Settings.Network, cancellationToken).ConfigureAwait(false);
    }
}
