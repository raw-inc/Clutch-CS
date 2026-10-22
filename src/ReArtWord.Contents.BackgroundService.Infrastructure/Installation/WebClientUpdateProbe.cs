using System.Diagnostics;
using System.Net.Http.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Client;
using ReArtWord.Contents.BackgroundService.Infrastructure.Processes;
using ReArtWord.Contents.BackgroundService.Infrastructure.Updates.Local;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Installation;

/// <summary>HTTP応答だけで別のプロセスを更新対象にせず、導入記録とOS実体を照合します。</summary>
public static class WebClientUpdateProbe
{
    /// <summary>既存の管理対象を読み、実行ファイルと版が一致する稼働実体を取得します。</summary>
    /// <returns>照合済みの導入と実体です。</returns>
    public static async Task<WebClientUpdateInfo> ReadAsync(WebClientInstallation selected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        WebClientInstallation current = await WebClientCatalog.ReadAsync(selected.Root, cancellationToken).ConfigureAwait(false);
        if (current != selected || !current.AutomaticStartup || current.ProgramDirectory is null)
        {
            throw new InvalidDataException("Select the current managed web client installation.");
        }
        using HttpClient http = new() { BaseAddress = ClientLaunchFiles.GetUrl(current.Port), Timeout = InstallationDefaults.RequestTimeout };
        ClientUpdateHealth health = await http.GetFromJsonAsync<ClientUpdateHealth>(new Uri("client/health", UriKind.Relative), ContractJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The web client did not return its running identity.");
        VerifyProcess(current, health.Process);
        Contracts.Updates.Releases.ReleaseManifest installed = await LocalUpdateExecutor.ReadManifestAsync(WebClientCatalog.ProgramRoot(current), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(installed.EntryPoint, current.EntryPoint, StringComparison.Ordinal)
            || !string.Equals(installed.ExpectedBuildVersion, health.BuildVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The running web client does not match its installed release.");
        }
        return new(current, health);
    }

    /// <summary>PIDの再利用や同じポートの別アプリを、更新対象の停止直前にも拒否します。</summary>
    public static void VerifyProcess(WebClientInstallation client, Contracts.Processes.ProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(identity);
        using Process process = Process.GetProcessById(identity.ProcessId);
        if (ProcessOs.Identity(process) != identity || !string.Equals(ProcessExecutable.GetPath(process),
            Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The web client process changed. Select it again.");
        }
    }
}
