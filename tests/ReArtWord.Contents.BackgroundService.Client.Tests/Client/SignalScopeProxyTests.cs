using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.ClientHost.SignalScope;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>一つのHTTP入口で、信頼するローカルプロキシの外側UDPも識別します。</summary>
public sealed class SignalScopeProxyTests
{
    /// <summary>実TCP接続の所有PIDを使い、転送ヘッダー上の利用者IPを所有者と誤認しません。</summary>
    /// <returns>復元したoriginと、実際に所有するUDP端点です。</returns>
    [Fact]
    public async Task ForwardedHttpsKeepsOneOriginAndFindsProxyUdpEndpointsAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanup = app.ConfigureAwait(true);
        SignalScopeTransport.RememberPeer(app);
        ClientHttpPolicy.Configure(app);
        app.MapGet("/client/signal-scope", (HttpContext context) =>
        {
            SignalScopeTransport transport = new(context, "127.0.0.1", 60001);
            byte[] frame = transport.Read()!;
            using JsonDocument metadata = JsonDocument.Parse(frame.AsMemory(1));
            return Results.Json(new { origin = context.Request.Scheme + "://" + context.Request.Host, transport = metadata.RootElement.Clone() });
        });
        try
        {
            await app.StartAsync(timeout.Token).ConfigureAwait(true);
            using UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
            int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            using HttpClient client = new() { BaseAddress = new(app.Urls.Single()) };
            using HttpRequestMessage request = new(HttpMethod.Get, "/client/signal-scope");
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "scope.example.test");
            request.Headers.Add("X-Forwarded-For", "192.0.2.20");
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token).ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(true));
            Assert.Equal("https://scope.example.test", body.RootElement.GetProperty("origin").GetString());
            JsonElement transportBody = body.RootElement.GetProperty("transport");
            Assert.Contains(transportBody.GetProperty("excludedEndpoints").EnumerateArray(), endpoint => endpoint.GetProperty("transport").GetString() is "udp" && endpoint.GetProperty("address").GetString() is "127.0.0.1" && endpoint.GetProperty("port").GetInt32() == udpPort);
            Assert.Contains(transportBody.GetProperty("endpoints").EnumerateArray(), endpoint => endpoint.GetProperty("port").GetInt32() == client.BaseAddress.Port);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }
}
