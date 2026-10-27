using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>Coreとは別の実ClientHostプロセスを所有する試験用境界です。</summary>
internal sealed class ClientHostProcess : IAsyncDisposable
{
    private readonly string executable;
    private readonly string stateDirectory;
    private readonly HttpClient client;
    private Process? process;

    /// <summary>試験で所有するHostの同一origin接続先です。</summary>
    internal Uri Address => client.BaseAddress!;

    /// <summary>専用の保存先とローカル待受を割り当てます。</summary>
    public ClientHostProcess(string stateDirectory)
    {
        this.stateDirectory = stateDirectory;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        string suffix = string.Empty;
        if (OperatingSystem.IsWindows())
        {
            suffix = ".exe";
        }
        executable = Path.Combine(root, "src", "ReArtWord.Contents.BackgroundService.ClientHost", "bin", configuration, "net10.0", "Clutch-CS.ClientHost" + suffix);
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{CorePeer.AvailablePort(tcp: true)}/"), Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>画面HostのNIC APIが応答するまで待ちます。</summary>
    /// <returns>ローカルHostの起動確認です。</returns>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "--listen", client.BaseAddress!.AbsoluteUri, "--state", stateDirectory })
        {
            start.ArgumentList.Add(argument);
        }
        process = Process.Start(start) ?? throw new IOException("ClientHostを起動できません。");
        await CorePeer.WaitAsync(async token =>
        {
            if (process.HasExited)
            {
                throw new IOException("ClientHostが起動中に終了しました。");
            }
            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri("client/network", UriKind.Relative), token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // HTTP一回の期限だけでは、起動確認全体を終了しません。
                return false;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>選択対象と元要求IDを各要求に明示します。</summary>
    /// <returns>画面Hostが実UDPで受信した結果です。</returns>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? clusterId = null, string? peerId = null, Guid? requestId = null, CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = new(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ContractJson.Options);
        }
        if (clusterId is not null)
        {
            request.Headers.Add("X-Contents-Cluster", clusterId);
        }
        if (peerId is not null)
        {
            request.Headers.Add("X-Contents-Peer", peerId);
        }
        if (requestId is not null)
        {
            request.Headers.Add("X-Contents-Request", requestId.Value.ToString("D"));
        }
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>所有ClientHostだけを終了します。</summary>
    /// <returns>実プロセスの停止です。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (process is not null)
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            process.Dispose();
            process = null;
        }
    }

    /// <summary>試験で所有するGUI HostとHTTP資源を解放します。</summary>
    /// <returns>所有資源の停止です。</returns>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        client.Dispose();
    }
}
