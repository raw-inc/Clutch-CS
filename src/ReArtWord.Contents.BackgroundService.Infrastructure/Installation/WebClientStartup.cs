using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>Webクライアントの自動起動とHTTPの起動確認を扱います。</summary>
public static class WebClientStartup
{
    /// <summary>Coreとは独立した起動登録名です。</summary>
    /// <returns>導入IDごとに固定した名前です。</returns>
    public static string ServiceName(WebClientInstallation client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return "Clutch-CS-WebClient-" + client.InstallationId.ToString("N");
    }

    /// <summary>PC共通の導入では共通領域を使い、ユーザー導入では従来の保存先を維持します。</summary>
    /// <returns>管理画面のデータ保存先です。</returns>
    public static string StateDirectory(WebClientInstallation client)
    {
        ArgumentNullException.ThrowIfNull(client);
        string id = client.InstallationId.ToString("N");
        if (client.RunAsService)
        {
            string parent = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!OperatingSystem.IsWindows())
            {
                parent = "/var/lib";
            }
            return Path.Combine(parent, "Clutch-CS", "WebClient", id);
        }
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReArtWord", "Contents", id);
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "Clutch-CS", id);
    }

    /// <summary>起動登録を補い、その場で起動してURLへの応答を確認します。</summary>
    /// <returns>登録と起動確認の完了です。</returns>
    public static async Task RegisterAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await WebClientProfile.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsWindows() && !client.RunAsService)
        {
            await WindowsUserStartup.ConfigureAsync(client, false, cancellationToken).ConfigureAwait(false);
            await VerifyAsync(client, cancellationToken).ConfigureAwait(false);
            return;
        }
        StartupRegistration registration = StartupRegistration.ForClient(client);
        if (OperatingSystem.IsWindows())
        {
            await new WindowsStartup().RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await UnixInstallation.RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        if (!client.RunAsService)
        {
            StartUser(registration);
        }
        else
        {
            if (OperatingSystem.IsWindows())
            {
                await new WindowsStartup().StartAsync(registration, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UnixInstallation.StartAsync(registration, cancellationToken).ConfigureAwait(false);
            }
        }
        await VerifyAsync(client, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>サービスの自動再起動を避けてから対象プロセスを停止します。</summary>
    /// <returns>停止確認完了です。</returns>
    public static async Task StopAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        string executable = Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint);
        await InstallationProcesses.StopExecutableAsync(executable, token => StopServiceAsync(client, token), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>サービス管理下の実体を、異常終了として自動復旧させず停止します。</summary>
    /// <returns>サービス管理機構による停止確認です。</returns>
    private static async Task StopServiceAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        if (client.AutomaticStartup && client.RunAsService)
        {
            StartupRegistration registration = StartupRegistration.ForClient(client);
            if (OperatingSystem.IsWindows())
            {
                await new WindowsStartup().StopAsync(registration, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UnixInstallation.StopAsync(registration, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>このクライアントの起動登録だけを解除します。</summary>
    /// <returns>解除完了です。</returns>
    public static Task UnregisterAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.AutomaticStartup)
        {
            return Task.CompletedTask;
        }
        StartupRegistration registration = StartupRegistration.ForClient(client);
        if (OperatingSystem.IsWindows())
        {
            if (!client.RunAsService)
            {
                return WindowsUserStartup.ConfigureAsync(client, true, cancellationToken);
            }
            return new WindowsStartup().UnregisterAsync(registration, cancellationToken);
        }
        return UnixInstallation.UnregisterAsync(registration, cancellationToken);
    }

    /// <summary>同じ実行物が動作中なら二重起動しません。</summary>
    private static void StartUser(StartupRegistration registration)
    {
        bool running = false;
        foreach (Process candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(registration.Executable)))
        {
            using (candidate)
            {
                running |= string.Equals(ProcessExecutable.GetPath(candidate), registration.Executable, StringComparison.OrdinalIgnoreCase);
            }
        }
        if (running)
        {
            return;
        }
        ProcessStartInfo start = new(registration.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(registration.Executable)! };
        foreach (string argument in registration.Arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new IOException("The web client could not start.");
    }

    /// <summary>配布物のバージョンと実行ファイルが一致するHTTP応答を待ちます。</summary>
    /// <returns>対象クライアントの応答確認です。</returns>
    private static async Task VerifyAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        using HttpClient http = new() { BaseAddress = ClientLaunchFiles.GetUrl(client.Port), Timeout = InstallationDefaults.RequestTimeout };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.ReadyTimeout);
        string expected = Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint);
        string? version = (await LocalUpdateExecutor.ReadManifestAsync(WebClientCatalog.ProgramRoot(client), cancellationToken).ConfigureAwait(false)).ExpectedBuildVersion;
        while (!timeout.IsCancellationRequested)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                ClientUpdateHealth? result = await http.GetFromJsonAsync<ClientUpdateHealth>(new Uri("client/health", UriKind.Relative), ContractJson.Options, timeout.Token).ConfigureAwait(false);
                if (result is not null && string.Equals(version, result.BuildVersion, StringComparison.Ordinal))
                {
                    using Process process = Process.GetProcessById(result.Process.ProcessId);
                    if (ProcessOs.Identity(process) == result.Process && string.Equals(expected, ProcessExecutable.GetPath(process), StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception || (exception is OperationCanceledException && !timeout.IsCancellationRequested))
            {
                // 起動中の接続失敗は制限時間内で再確認します。
            }
            await Task.Delay(InstallationDefaults.PollInterval, timeout.Token).ConfigureAwait(false);
        }
        timeout.Token.ThrowIfCancellationRequested();
    }
}
