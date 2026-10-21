using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.AI;

/// <summary>外部課金なしで公式APIの接続線を観測するlocalhostサーバーです。</summary>
internal sealed class AiWireFixture : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Func<AiWireRequest, CancellationToken, Task<AiWireResponse>> respond;
    private readonly Task run;

    /// <summary>ループバックの明示NICと一時ポートで試験用の構成を開始します。</summary>
    internal AiWireFixture(Func<AiWireRequest, CancellationToken, Task<AiWireResponse>> respond)
    {
        this.respond = respond;
        using TcpListener reservation = new(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        BaseUri = new Uri("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
        listener.Prefixes.Add(BaseUri.AbsoluteUri);
        listener.Start();
        InterfaceId = NetworkInterface.GetAllNetworkInterfaces().First(item => item.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(IPAddress.Loopback))).Id;
        run = RunAsync(stop.Token);
    }

    /// <summary>試験用の構成の接続先です。</summary>
    internal Uri BaseUri
    {
        get;
    }

    /// <summary>実HTTP ソケットにbindするNICです。</summary>
    internal string InterfaceId
    {
        get;
    }

    /// <summary>受信済みの要求です。</summary>
    internal ConcurrentQueue<AiWireRequest> Requests
    {
        get;
    } = new();

    /// <summary>listenerと進行中応答を停止して全資源を解放します。</summary>
    /// <returns>試験用の構成終了の完了です。</returns>
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        listener.Close();
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            // 試験用の構成の終了で中断された受信・応答は期待した終了です。
        }
        stop.Dispose();
    }

    /// <summary>一件ずつ受信し、試験で指定した終端応答を返します。</summary>
    /// <returns>listener終了までの非同期処理です。</returns>
    private async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            AiWireRequest request = new(context.Request.HttpMethod, context.Request.Url!.AbsolutePath, body, context.Request.Headers["Authorization"], context.Request.Headers["X-DashScope-Async"], context.Request.Headers["xi-api-key"], context.Request.Url.Query);
            Requests.Enqueue(request);
            AiWireResponse response = await respond(request, cancellationToken).ConfigureAwait(false);
            context.Response.StatusCode = response.Status;
            context.Response.ContentType = response.ContentType;
            context.Response.ContentLength64 = response.Body.Length;
            if (response.Headers is not null)
            {
                foreach ((string name, string value) in response.Headers)
                {
                    context.Response.Headers[name] = value;
                }
            }
            try
            {
                await context.Response.OutputStream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // タイムアウト試験で先に切断されたクライアントへの書込失敗を許します。
            }
            context.Response.Close();
        }
    }
}

/// <summary>試験用の構成が観測したAPI要求です。</summary>
/// <param name="Method">HTTP methodです。</param>
/// <param name="Path">endpoint パスです。</param>
/// <param name="Body">要求JSONです。</param>
/// <param name="Authorization">試験用認証値です。</param>
/// <param name="AsyncHeader">非同期生成の明示ヘッダーです。</param>
/// <param name="ApiKey">ElevenLabsの試験用APIキーです。</param>
/// <param name="Query">要求URLの照会です。</param>
internal sealed record AiWireRequest(string Method, string Path, string Body, string? Authorization, string? AsyncHeader, string? ApiKey = null, string? Query = null);

/// <summary>試験用の構成が返すAPI応答です。</summary>
/// <param name="Status">HTTP statusです。</param>
/// <param name="ContentType">MIME種別です。</param>
/// <param name="Body">実際に送信する本文です。</param>
internal sealed record AiWireResponse(int Status, string ContentType, byte[] Body)
{
    /// <summary>redirect等の通信境界を試験する追加応答ヘッダーです。</summary>
    internal IReadOnlyDictionary<string, string>? Headers
    {
        get;
        init;
    }

    /// <summary>JSON応答をUTF8で構築します。</summary>
    /// <returns>接続線へ送信する応答です。</returns>
    internal static AiWireResponse Json(string json, int status = 200)
    {
        return new AiWireResponse(status, "application/json", Encoding.UTF8.GetBytes(json));
    }
}
