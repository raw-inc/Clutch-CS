using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Releases;
using System.Text.Json;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>利用可能なWebクライアントを、実プロセスの有無とは独立して見つけます。</summary>
public static class WebClientCatalog
{
    /// <summary>Webクライアント自身の所有記録です。</summary>
    public static string RecordName => "web-client.json";

    /// <summary>設定を保持した削除済み導入の所有記録です。</summary>
    public static string RemovedRecordName => "uninstalled-web-client.json";

    private static string RegistryPath => @"Software\ReArtWord\Clutch-CS\WebClients";

    /// <summary>更新で入れ替えるプログラムの配置先です。</summary>
    /// <returns>導入記録や表示設定とは独立した配置先です。</returns>
    public static string ProgramRoot(WebClientInstallation client)
    {
        ArgumentNullException.ThrowIfNull(client);
        string root = InstallationFiles.Root(client.Root);
        string program = InstallationFiles.Root(client.ProgramDirectory ?? root);
        if (!string.Equals(program, root, StringComparison.OrdinalIgnoreCase) && !UpdateLayout.Contains(root, program))
        {
            throw new InvalidDataException("The web client program folder is outside its installation.");
        }
        return program;
    }

    /// <summary>クラスターを削除しても残るクライアントの標準配置先です。</summary>
    /// <returns>PC共通またはユーザー単位の導入先です。</returns>
    public static string DefaultRoot(bool perUser)
    {
        string parent = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (OperatingSystem.IsWindows() && !perUser)
        {
            parent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }
        return Path.Combine(parent, "Clutch-CS", "WebClient");
    }

    /// <summary>登録されたクライアントと旧形式の導入を照会します。</summary>
    /// <returns>利用できる最初の導入、またはnullです。</returns>
    public static Task<WebClientInstallation?> FindAsync(CancellationToken cancellationToken = default) => FindAsync(false, cancellationToken);

    /// <summary>削除時は実行物が欠損した導入も、所有記録から選択可能にします。</summary>
    /// <returns>登録された導入、またはnullです。</returns>
    public static async Task<WebClientInstallation?> FindAsync(bool includeUnavailable, CancellationToken cancellationToken = default)
    {
        List<string> candidates = [DefaultRoot(true), DefaultRoot(false)];
        if (OperatingSystem.IsWindows())
        {
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using RegistryKey? key = hive.OpenSubKey(RegistryPath);
                if (key is not null)
                {
                    foreach (string name in key.GetValueNames())
                    {
                        if (key.GetValue(name) is string root)
                        {
                            candidates.Add(root);
                        }
                    }
                }
            }
        }
        candidates.AddRange(InstallationCatalog.GetRoots().Select(root => Path.Combine(root, "client")));
        foreach (string root in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return await ReadAsync(root, !includeUnavailable, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                // 消失した導入や他ユーザーの読めない導入を利用可能とは表示しません。
            }
        }
        return null;
    }

    /// <summary>実行物と記録の所有範囲を検証し、旧配置のportも引き継ぎます。</summary>
    /// <returns>実在するクライアントの情報です。</returns>
    public static Task<WebClientInstallation> ReadAsync(string root, CancellationToken cancellationToken = default) => ReadAsync(root, true, cancellationToken);

    /// <summary>削除では失われた実行物を再起動せず、残った所有fileだけを扱います。</summary>
    /// <returns>検証した所有記録です。</returns>
    public static async Task<WebClientInstallation> ReadAsync(string root, bool requireExecutable, CancellationToken cancellationToken = default)
    {
        string canonical = InstallationFiles.Root(root);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException("The web client folder is missing.");
        }
        string path = Path.Combine(canonical, RecordName);
        WebClientInstallation client;
        if (File.Exists(path))
        {
            client = JsonSerializer.Deserialize<WebClientInstallation>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), ContractJson.Options) ?? throw new InvalidDataException("The web client record is missing.");
        }
        else
        {
            string removed = Path.Combine(canonical, RemovedRecordName);
            if (File.Exists(removed))
            {
                throw new FileNotFoundException("The web client has already been uninstalled.");
            }
            string parent = Path.GetDirectoryName(canonical)!;
            InstallationRecord legacy = await InstallationFiles.ReadAsync(parent, cancellationToken).ConfigureAwait(false);
            if (legacy.Settings.WebClient is not null)
            {
                throw new InvalidDataException("The web client installation is incomplete.");
            }
            string expected = Path.Combine(parent, "client");
            if (!string.Equals(canonical, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The web client does not belong to this installation.");
            }
            client = new(legacy.InstallationId, canonical, legacy.ClientEntryPoint, LegacyPort(legacy.InstallationId), legacy.Settings.PerUser);
        }
        return Validate(client, canonical, requireExecutable);
    }

    /// <summary>再導入時にだけ、保持された設定の所有記録を取得します。</summary>
    /// <returns>同じ配置先に属する削除済み導入です。</returns>
    public static async Task<WebClientInstallation> ReadRemovedAsync(string root, CancellationToken cancellationToken = default)
    {
        string canonical = InstallationFiles.Root(root);
        string text = await File.ReadAllTextAsync(Path.Combine(canonical, RemovedRecordName), cancellationToken).ConfigureAwait(false);
        WebClientInstallation client = JsonSerializer.Deserialize<WebClientInstallation>(text, ContractJson.Options) ?? throw new InvalidDataException("The retained web client record is missing.");
        return Validate(client, canonical, false);
    }

    /// <summary>OS索引と利用者入力から取得した記録を同じ条件で検証します。</summary>
    /// <returns>検証済みの導入です。</returns>
    private static WebClientInstallation Validate(WebClientInstallation client, string canonical, bool requireExecutable)
    {
        if (client.InstallationId == Guid.Empty || !string.Equals(canonical, InstallationFiles.Root(client.Root), StringComparison.OrdinalIgnoreCase)
            || !ReleaseVerifier.IsRelativePath(client.EntryPoint) || (requireExecutable && !File.Exists(Path.Combine(ProgramRoot(client), client.EntryPoint))))
        {
            throw new InvalidDataException("The web client executable or ownership is invalid.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(client.Port, System.Net.IPEndPoint.MinPort + 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(client.Port, System.Net.IPEndPoint.MaxPort);
        return client;
    }

    /// <summary>同じクライアントの配置変更を一度に一つへ限定します。</summary>
    /// <returns>呼出側が操作完了まで所有するlockです。</returns>
    public static FileStream AcquireLock(string root) => new(InstallationFiles.Root(root) + ".setup.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    /// <summary>旧版の生成規則を保持し、既存クライアントのURLを変えません。</summary>
    /// <returns>旧配置のHTTP portです。</returns>
    public static int LegacyPort(Guid id) => InstallationDefaults.ClientPortStart + BitConverter.ToUInt16(id.ToByteArray(), 0) % InstallationDefaults.ClientPortCount;

    /// <summary>クライアントを独立した導入として記録します。</summary>
    public static void Register(WebClientInstallation client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (OperatingSystem.IsWindows())
        {
            RegistryKey hive = Registry.LocalMachine;
            if (client.PerUser)
            {
                hive = Registry.CurrentUser;
            }
            using RegistryKey key = hive.CreateSubKey(RegistryPath);
            key.SetValue(client.InstallationId.ToString("N"), client.Root);
        }
    }

    /// <summary>指定したクライアントの索引だけを削除します。</summary>
    public static void Remove(WebClientInstallation client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (OperatingSystem.IsWindows())
        {
            RegistryKey hive = Registry.LocalMachine;
            if (client.PerUser)
            {
                hive = Registry.CurrentUser;
            }
            using RegistryKey? key = hive.OpenSubKey(RegistryPath, true);
            key?.DeleteValue(client.InstallationId.ToString("N"), false);
        }
    }
}
