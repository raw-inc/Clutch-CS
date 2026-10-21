using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>導入IDに所有されたWindows Firewallルールだけを設定・削除します。</summary>
[SupportedOSPlatform("windows")]
public static class WindowsFirewall
{
    /// <summary>選択NICの管理通信と受動発見を許可し、適用結果を確認します。</summary>
    /// <returns>設定または削除の確認完了です。</returns>
    public static async Task ConfigureAsync(InstallationRecord record, bool remove, Guid? resultOperation = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (IPAddress.IsLoopback(IPAddress.Parse(record.Settings.Network.BindAddress)))
        {
            return;
        }
        string specification = await WindowsFirewallFiles.WriteAsync(record, remove, resultOperation, cancellationToken).ConfigureAwait(false);
        string script = Path.ChangeExtension(specification, ".ps1");
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string[] arguments = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-SpecificationPath", specification];
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(InstallationDefaults.ReadyTimeout);
            _ = await BoundedProcessRunner.RunAsync(executable, arguments, timeout.Token).ConfigureAwait(false);
            return;
        }
        ProcessStartInfo start = new(executable) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new IOException("Windows Firewall configuration could not start.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string detail = "Windows Firewall configuration failed.";
            string errorPath = specification + ".error.txt";
            if (File.Exists(errorPath))
            {
                detail = await File.ReadAllTextAsync(errorPath, cancellationToken).ConfigureAwait(false);
            }
            throw new IOException(detail);
        }
    }
}
