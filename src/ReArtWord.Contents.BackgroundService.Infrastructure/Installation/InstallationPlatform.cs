using System.Diagnostics;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>実サービス登録、正常起動確認と対象processの停止を扱います。</summary>
public sealed class InstallationPlatform : IInstallationPlatform
{
    private JsonElement preflight;

    /// <summary>Windows のサービス配置を保護し、ユーザー導入では本人の書込み権限を保ちます。</summary>
    public void PrepareDirectory(string path, bool perUser, bool privateData = false, bool recursive = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(InstallationFiles.Root(path));
            return;
        }
        if (recursive)
        {
            WindowsInstallationSecurity.ProtectTree(path, perUser, privateData);
            return;
        }
        WindowsInstallationSecurity.PrepareDirectory(path, perUser, privateData);
    }

    /// <summary>利用可能なクライアントを照会します。</summary>
    /// <returns>既存の導入です。</returns>
    public Task<WebClientInstallation?> FindWebClientAsync(CancellationToken cancellationToken = default) => WebClientCatalog.FindAsync(cancellationToken);

    /// <summary>クライアントをPC共通または現在のユーザーの領域へ配置します。</summary>
    /// <returns>クライアントの導入先です。</returns>
    public string WebClientRoot(bool perUser) => WebClientCatalog.DefaultRoot(perUser);

    /// <summary>所有を確認したクライアントだけを停止します。</summary>
    /// <returns>停止完了です。</returns>
    public Task StopWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return WebClientStartup.StopAsync(client, cancellationToken);
    }

    /// <summary>OS実体とHTTP応答を導入記録に照合します。</summary>
    /// <returns>現在動作している対象です。</returns>
    public Task<WebClientUpdateInfo> InspectWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
        => WebClientUpdateProbe.ReadAsync(client, cancellationToken);

    /// <summary>この利用者の登録を更新し、元の起動方式で開始します。</summary>
    /// <returns>起動とHTTP応答確認です。</returns>
    public async Task StartWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        await RegisterWebClientAsync(client, cancellationToken).ConfigureAwait(false);
        if (!client.RunAsService)
        {
            await WebClientStartup.RegisterAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>クライアントの独立した導入を記録します。</summary>
    /// <returns>導入記録と自動起動の登録です。</returns>
    public async Task RegisterWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.RunAsService)
        {
            PrepareDirectory(client.Root, perUser: false, recursive: true);
        }
        WebClientCatalog.Register(client);
        if (client.RunAsService)
        {
            await WebClientStartup.RegisterAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>指定したクライアントの記録だけを解除します。</summary>
    /// <returns>起動登録と導入記録の解除です。</returns>
    public async Task UnregisterWebClientAsync(WebClientInstallation client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (client.RunAsService)
        {
            await WebClientStartup.UnregisterAsync(client, cancellationToken).ConfigureAwait(false);
        }
        WebClientCatalog.Remove(client);
    }

    /// <summary>配置に対応するOS操作を行います。</summary>
    /// <returns>OS側で確認した完了です。</returns>
    public Task RegisterAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (OperatingSystem.IsWindows())
        {
            return WindowsInstallation.RegisterAsync(record, cancellationToken);
        }
        return UnixInstallation.RegisterAsync(StartupRegistration.ForCore(record), cancellationToken);
    }

    /// <summary>配置に対応するOS操作を行います。</summary>
    /// <returns>OS側で確認した完了です。</returns>
    public async Task StartAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        preflight = await VerifyProcessAsync(record, false, cancellationToken).ConfigureAwait(false);
        if (!record.Settings.PerUser)
        {
            if (OperatingSystem.IsWindows())
            {
                await WindowsInstallation.StartAsync(record, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UnixInstallation.StartAsync(StartupRegistration.ForCore(record), cancellationToken).ConfigureAwait(false);
            }
            return;
        }
        ProcessStartInfo start = new(Path.Combine(record.Settings.Root, "core", record.CoreEntryPoint))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.Combine(record.Settings.Root, "core"),
        };
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(Path.Combine(record.Settings.Root, "config", "contents.toml"));
        using Process process = Process.Start(start) ?? throw new IOException("The installed service could not start.");
    }

    /// <summary>配置に対応するOS操作を行います。</summary>
    /// <returns>OS側で確認した完了です。</returns>
    public async Task StopAsync(InstallationRecord record, ProcessIdentity? process, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (process is not null)
        {
            EOutcome stopped = await ProcessOs.StopAsync(process, EProcessStopPolicy.WaitForManual, (int)InstallationDefaults.StopTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            if (stopped != EOutcome.Succeeded)
            {
                throw new IOException("The original process has not stopped. Files were retained.");
            }
        }
        if (!record.Settings.PerUser)
        {
            if (OperatingSystem.IsWindows())
            {
                await WindowsInstallation.StopAsync(record, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UnixInstallation.StopAsync(StartupRegistration.ForCore(record), cancellationToken).ConfigureAwait(false);
            }
        }

        // 配置元が一致するprocessが残る場合、未知のPIDを強制終了せず入替を中止します。
        string expectedExecutable = Path.Combine(record.Settings.Root, "core", record.CoreEntryPoint);
        foreach (Process candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(record.CoreEntryPoint)))
        {
            using (candidate)
            {
                if (string.Equals(ProcessExecutable.GetPath(candidate), expectedExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("A process still uses this installation. Close it and retry Repair.");
                }
            }
        }
        await InstallationProcesses.StopClientAsync(record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>配置に対応するOS操作を行います。</summary>
    /// <returns>OS側で確認した完了です。</returns>
    public Task UnregisterAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (OperatingSystem.IsWindows())
        {
            return WindowsInstallation.UnregisterAsync(record, cancellationToken);
        }
        return UnixInstallation.UnregisterAsync(StartupRegistration.ForCore(record), cancellationToken);
    }

    /// <summary>配置に対応するOS操作を行います。</summary>
    /// <returns>OS側で確認した完了です。</returns>
    public async Task VerifyAsync(InstallationRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = await VerifyProcessAsync(record, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>既存Updaterの同一検証を登録前後で使用します。</summary>
    /// <returns>検証結果の固定入力です。</returns>
    private async Task<JsonElement> VerifyProcessAsync(InstallationRecord record, bool ready, CancellationToken cancellationToken = default)
    {
        string request = Path.Combine(record.Settings.Root, "logs", "verify-" + Guid.NewGuid().ToString("N") + ".json");
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["configurationPath"] = Path.Combine(record.Settings.Root, "config", "contents.toml"),
            ["manifestPath"] = Path.Combine(record.Settings.Root, "core", "release-manifest.json"),
            ["checkReady"] = ready, ["timeoutMilliseconds"] = (int)InstallationDefaults.ReadyTimeout.TotalMilliseconds,
        };
        if (ready)
        {
            body["configurationHash"] = preflight.GetProperty("configurationHash").GetString();
            body["manifestHash"] = preflight.GetProperty("manifestHash").GetString();
            body["startedAfter"] = preflight.GetProperty("startedAfter").GetDateTimeOffset();
        }
        await InstallationFiles.WriteAsync(request, body, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.ReadyTimeout + InstallationDefaults.StopTimeout);
        string output = await BoundedProcessRunner.RunAsync(Path.Combine(record.Settings.Root, "updater", record.UpdaterEntryPoint), ["--verify-install", request], timeout.Token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.ChangeExtension(request, ".result.json"), output, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<JsonElement>(output, ContractJson.Options);
    }
}
