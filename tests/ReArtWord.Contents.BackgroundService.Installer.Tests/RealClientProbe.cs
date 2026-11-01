using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Installation;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Installation;
using Xunit;
using ReArtWord.Contents.BackgroundService.Runtime.Processes;

namespace ReArtWord.Contents.BackgroundService.Installer.Tests;

/// <summary>導入したClientHostの実HTTPで表示設定を確認します。</summary>
internal static class RealClientProbe
{
    /// <summary>導入先の表示設定が独立ClientHostへ渡ることを検証します。</summary>
    /// <returns>表示設定と終了の確認完了です。</returns>
    public static async Task CheckAsync(string root, CancellationToken cancellationToken = default)
    {
        Contracts.Installation.InstallationRecord record = await InstallationFiles.ReadAsync(root, cancellationToken).ConfigureAwait(false);
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        await ProbeAsync(Path.Combine(root, "client", record.ClientEntryPoint), Path.Combine(root, "config", "appearance.json"), Path.Combine(root, "client-state"), port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>独立clientを、指定された実portと自身の表示設定で起動します。</summary>
    /// <returns>実HTTPの検証完了です。</returns>
    public static Task CheckAsync(Contracts.Installation.WebClientInstallation client, string stateRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        return ProbeAsync(Path.Combine(WebClientCatalog.ProgramRoot(client), client.EntryPoint), Path.Combine(client.Root, "config", "appearance.json"), stateRoot, client.Port, cancellationToken);
    }

    /// <summary>localhostの実HTTP応答とprocess終了を確認します。</summary>
    /// <returns>検証と終了の完了です。</returns>
    private static async Task ProbeAsync(string executable, string appearance, string stateRoot, int port, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stateRoot);
        ClientAppearance saved = JsonSerializer.Deserialize<ClientAppearance>(await File.ReadAllTextAsync(appearance, cancellationToken).ConfigureAwait(false), ContractJson.Options)!;
        await InstallationFiles.WriteAsync(Path.Combine(stateRoot, "branding.json"), new ClientBranding(saved.Title!, HeaderLogo: saved.IconPath, Favicon: saved.FaviconPath, Subtitle: saved.Subtitle!), cancellationToken).ConfigureAwait(false);
        Uri address = new("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "--state", stateRoot, "--listen", address.ToString() })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        try
        {
            using HttpClient client = new() { BaseAddress = address, Timeout = InstallationDefaults.RequestTimeout };
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(InstallationDefaults.ReadyTimeout);
            bool received = false;
            while (!received)
            {
                try
                {
                    string text = await client.GetStringAsync(new Uri("/client/branding", UriKind.Relative), timeout.Token).ConfigureAwait(false);
                    using JsonDocument document = JsonDocument.Parse(text);
                    Assert.Equal("Fixture title", document.RootElement.GetProperty("contentsName").GetString());
                    Assert.Equal("Fixture subtitle", document.RootElement.GetProperty("subtitle").GetString());
                    Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("headerLogo").ValueKind);
                    received = true;
                }
                catch (Exception exception) when (exception is HttpRequestException
                    || exception is OperationCanceledException && !timeout.IsCancellationRequested)
                {
                    Assert.False(process.HasExited);
                    await Task.Delay(InstallationDefaults.PollInterval, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(false);
            }
            await ProcessCompletion.WaitAsync(process, CancellationToken.None).ConfigureAwait(false);
        }
    }
}
