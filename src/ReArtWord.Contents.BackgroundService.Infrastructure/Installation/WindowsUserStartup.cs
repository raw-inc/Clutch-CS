using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Runtime.Processes.Commands;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>既存の通常権限用タスクを使い、サインイン時のClientHost起動を登録します。</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsUserStartup
{
    /// <summary>実行ポリシーを変更せず、固定スクリプトへデータとして引数を渡します。</summary>
    /// <returns>登録・開始または解除の完了です。</returns>
    internal static async Task ConfigureAsync(WebClientInstallation client, bool remove, CancellationToken cancellationToken = default)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        string json = JsonSerializer.Serialize(new
        {
            ExecutablePath = Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint), StateDirectory = WebClientStartup.StateDirectory(client),
            User = identity.Name, Listen = ClientLaunchFiles.GetUrl(client.Port).OriginalString, client.InstallationId, Remove = remove,
            StartScriptPath = Path.Combine(client.Root, "Start-Client.ps1"),
            StopTimeoutMilliseconds = (int)InstallationDefaults.StopTimeout.TotalMilliseconds,
            PollIntervalMilliseconds = (int)InstallationDefaults.PollInterval.TotalMilliseconds,
        });
        using Stream resource = typeof(WindowsUserStartup).Assembly.GetManifestResourceStream("Clutch-CS.RegisterClientStartup")!;
        using StreamReader reader = new(resource);
        string script = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        string command = "$clientStartupInput = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            + "')) | ConvertFrom-Json; $clientStartupParameters = @{}; foreach ($clientStartupProperty in $clientStartupInput.PSObject.Properties) { $clientStartupParameters[$clientStartupProperty.Name] = $clientStartupProperty.Value }; & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + "')))) @clientStartupParameters";
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InstallationDefaults.ReadyTimeout);
        _ = await BoundedProcessRunner.RunAsync(executable, ["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))], timeout.Token).ConfigureAwait(false);
    }
}
