using System.Runtime.Versioning;
using Microsoft.Win32;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>所有するWindowsサービスとサインイン時起動を登録・解除します。</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsStartup
{
    private readonly Func<bool, string, string?> read;
    private readonly Action<string, string?> writeUser;
    private readonly Func<string[], CancellationToken, Task<string>> service;

    /// <summary>レジストリとSCMを使用します。</summary>
    internal WindowsStartup() : this(ReadCommand, WriteUserCommand, ScAsync)
    {
    }

    /// <summary>OS操作の境界を受け取ります。</summary>
    internal WindowsStartup(Func<bool, string, string?> read, Action<string, string?> writeUser, Func<string[], CancellationToken, Task<string>> service)
    {
        this.read = read;
        this.writeUser = writeUser;
        this.service = service;
    }

    /// <summary>既存の所有を確認し、自動起動と障害時の再起動を設定します。</summary>
    /// <returns>登録完了です。</returns>
    internal async Task RegisterAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        bool exists = Owns(registration);
        if (registration.PerUser)
        {
            writeUser(registration.Name, registration.Command);
            return;
        }
        if (exists)
        {
            await service(["config", registration.Name, "start=", "auto"], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await service(["create", registration.Name, "binPath=", registration.Command, "start=", "auto", "DisplayName=", registration.DisplayName], cancellationToken).ConfigureAwait(false);
        }
        await service(["failure", registration.Name, "reset=", InstallationDefaults.WindowsRecoveryReset.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "actions=", InstallationDefaults.WindowsRecoveryActions], cancellationToken).ConfigureAwait(false);
        await service(["failureflag", registration.Name, "1"], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>更新前の登録が一致する場合だけ実行物を入れ替えます。</summary>
    /// <returns>登録更新完了です。</returns>
    internal async Task ReplaceAsync(StartupRegistration previous, StartupRegistration replacement, CancellationToken cancellationToken = default)
    {
        bool exists = Owns(previous);
        if (previous.PerUser)
        {
            writeUser(replacement.Name, replacement.Command);
            return;
        }
        if (!exists)
        {
            throw new IOException("The installation's Windows service is missing. Use Repair first.");
        }
        await service(["config", replacement.Name, "binPath=", replacement.Command], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>開始済みのサービスを重複して開始しません。</summary>
    /// <returns>開始要求完了です。</returns>
    internal async Task StartAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        if (!Owns(registration))
        {
            await RegisterAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        string state = await service(["query", registration.Name], cancellationToken).ConfigureAwait(false);
        string running = "RUNNING";
        string pending = "START_PENDING";
        if (!state.Contains(running, StringComparison.Ordinal) && !state.Contains(pending, StringComparison.Ordinal))
        {
            await service(["start", registration.Name], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>所有するサービスの停止を待ちます。</summary>
    /// <returns>停止確認完了です。</returns>
    internal async Task StopAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        if (!Owns(registration))
        {
            return;
        }
        string state = await service(["query", registration.Name], cancellationToken).ConfigureAwait(false);
        string stopped = "STOPPED";
        string pending = "STOP_PENDING";
        if (!state.Contains(stopped, StringComparison.Ordinal) && !state.Contains(pending, StringComparison.Ordinal))
        {
            await service(["stop", registration.Name], cancellationToken).ConfigureAwait(false);
        }
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.StopTimeout);
        while (!state.Contains(stopped, StringComparison.Ordinal))
        {
            await Task.Delay(InstallationDefaults.PollInterval, timeout.Token).ConfigureAwait(false);
            state = await service(["query", registration.Name], timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>別の導入に属する同名登録は削除しません。</summary>
    /// <returns>登録解除完了です。</returns>
    internal async Task UnregisterAsync(StartupRegistration registration, CancellationToken cancellationToken = default)
    {
        if (!Owns(registration))
        {
            return;
        }
        if (registration.PerUser)
        {
            writeUser(registration.Name, null);
            return;
        }
        await service(["delete", registration.Name], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>登録済みの起動コマンドで所有を確認します。</summary>
    /// <returns>自分の登録が存在するかです。</returns>
    internal bool Owns(StartupRegistration registration)
    {
        string? command = read(registration.PerUser, registration.Name);
        if (command is null)
        {
            return false;
        }
        if (!string.Equals(command, registration.Command, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The startup registration belongs to another installation.");
        }
        return true;
    }

    /// <summary>起動時のコマンドを参照します。</summary>
    /// <returns>未登録ならnullです。</returns>
    private static string? ReadCommand(bool perUser, string name)
    {
        if (perUser)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return key?.GetValue(name)?.ToString();
        }
        using RegistryKey? serviceKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return serviceKey?.GetValue("ImagePath")?.ToString();
    }

    /// <summary>現在のユーザーの起動登録だけを変更します。</summary>
    private static void WriteUserCommand(string name, string? command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (command is null)
        {
            key.DeleteValue(name, false);
            return;
        }
        key.SetValue(name, command);
    }

    /// <summary>SCMへの要求時間を制限します。</summary>
    /// <returns>状態確認用の出力です。</returns>
    private static async Task<string> ScAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.StopTimeout);
        return await BoundedProcessRunner.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"), arguments, timeout.Token).ConfigureAwait(false);
    }
}
