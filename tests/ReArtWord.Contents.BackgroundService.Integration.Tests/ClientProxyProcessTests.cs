using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Integration.Tests;

/// <summary>Coreを起動せず、実ClientHostのproxy・キャッシュ境界をHTTPで検証します。</summary>
public sealed class ClientProxyProcessTests
{
    /// <summary>同名資材は再検証し、回復ページと管理応答は保存しません。</summary>
    /// <returns>実ClientHostのHTTP cache方針です。</returns>
    [Fact]
    public async Task MutableUiAndRecoveryPageHaveExplicitCachePoliciesAsync()
    {
        ClientHostProcess host = new(Path.Combine(Path.GetTempPath(), "contents-assets-" + Guid.NewGuid().ToString("N")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)host).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token).ConfigureAwait(true);
        using HttpClient client = new() { BaseAddress = host.Address };
        using HttpResponseMessage asset = await client.GetAsync(new Uri("browser.js?ui=test-version", UriKind.Relative), timeout.Token).ConfigureAwait(true);
        asset.EnsureSuccessStatusCode();
        Assert.True(asset.Headers.CacheControl!.NoCache);
        Assert.True(asset.Headers.CacheControl.MustRevalidate);
        using HttpResponseMessage recovery = await client.GetAsync(new Uri("ui-update.html", UriKind.Relative), timeout.Token).ConfigureAwait(true);
        recovery.EnsureSuccessStatusCode();
        Assert.True(recovery.Headers.CacheControl!.NoStore);
        using HttpResponseMessage health = await client.GetAsync(new Uri("client/health", UriKind.Relative), timeout.Token).ConfigureAwait(true);
        Assert.True(health.Headers.CacheControl!.NoStore);
    }

    /// <summary>信頼したloopback proxyだけが外部HTTPSを復元でき、別originは拒否します。</summary>
    /// <returns>originの拒否と、信頼した接続への理由付き終了通知です。</returns>
    [Theory]
    [InlineData("127.0.0.1", "https://dashboard.example", 101)]
    [InlineData("127.0.0.1", "https://different.example", 403)]
    [InlineData("127.0.0.2", "https://dashboard.example", 403)]
    public async Task ForwardedHttpsRequiresTrustedProxyAndMatchingOriginAsync(string source, string origin, int expected)
    {
        ClientHostProcess host = new(Path.Combine(Path.GetTempPath(), "contents-proxy-" + Guid.NewGuid().ToString("N")));
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = ((IAsyncDisposable)host).ConfigureAwait(true);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await host.StartAsync(timeout.Token).ConfigureAwait(true);
        using SocketsHttpHandler handler = new()
        {
            ConnectCallback = async (context, token) =>
            {
                Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                bool transferred = false;
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
                    await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                    NetworkStream stream = new(socket, ownsSocket: true);
                    transferred = true;
                    return stream;
                }
                finally
                {
                    if (!transferred)
                    {
                        socket.Dispose();
                    }
                }
            }
        };
        using HttpClient client = new(handler, disposeHandler: false) { BaseAddress = host.Address };
        using HttpRequestMessage request = new(HttpMethod.Get, "client/desktop?host=absent-test-host");
        request.Headers.Host = "internal.example";
        request.Headers.Add("X-Forwarded-Host", "dashboard.example");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "198.51.100.20");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Connection", "Upgrade");
        request.Headers.Add("Upgrade", "websocket");
        request.Headers.Add("Sec-WebSocket-Version", "13");
        request.Headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(true);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Contains("X-Contents-Cluster", response.Headers.Vary);
        Assert.Contains("X-Contents-Peer", response.Headers.Vary);
        Assert.True(Guid.TryParse(Assert.Single(response.Headers.GetValues("X-Contents-Host-Instance")), out _));
        if (response.StatusCode == HttpStatusCode.SwitchingProtocols)
        {
            using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(true);
            using WebSocket socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false });
            ValueWebSocketReceiveResult close = await socket.ReceiveAsync(new byte[128].AsMemory(), timeout.Token).ConfigureAwait(true);
            Assert.Equal(WebSocketMessageType.Close, close.MessageType);
            Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.CloseStatus);
            Assert.Equal("Desktop.HostUnavailable", socket.CloseStatusDescription);
        }
    }
}
