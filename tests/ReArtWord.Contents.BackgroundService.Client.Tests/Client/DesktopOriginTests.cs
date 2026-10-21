using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.ClientHost.Desktop;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Client.Tests.Client;

/// <summary>閲覧端末・中継Host・Coreの所在地と、信頼する転送ヘッダーの境界を検証します。</summary>
public sealed class DesktopOriginTests
{
    /// <summary>ClientHostとCoreの同居だけでは、自動消音やキー反復抑止を有効にしません。</summary>
    [Theory]
    [InlineData("192.0.2.20", "192.0.2.10", false)]
    [InlineData("192.0.2.20", "127.0.0.1", false)]
    [InlineData("192.0.2.20", "192.0.2.20", true)]
    [InlineData("127.0.0.1", "192.0.2.10", true)]
    [InlineData("192.0.2.10", "127.0.0.1", true)]
    [InlineData("::1", "127.0.0.1", true)]
    [InlineData("::ffff:192.0.2.20", "192.0.2.20", true)]
    [InlineData("192.0.2.20", "::ffff:192.0.2.20", true)]
    [InlineData(null, "127.0.0.1", false)]
    public void ViewerAndCoreMustShareTheHost(string? viewer, string core, bool expected)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = viewer is null ? null
                                                            : IPAddress.Parse(viewer);
        Assert.Equal(expected, DesktopRoutes.IsLocalViewer(context, IPAddress.Parse(core), [IPAddress.Parse("192.0.2.10")]));
    }

    /// <summary>loopback Tunnelからだけ接続元を復元し、欠測・不正値を同一PC扱いしません。</summary>
    /// <returns>実際のHTTP middlewareを通した所在地の確認です。</returns>
    [Theory]
    [InlineData("127.0.0.1", "198.51.100.20", "198.51.100.20", false)]
    [InlineData("::1", "198.51.100.20", "198.51.100.20", false)]
    [InlineData("::ffff:127.0.0.1", "198.51.100.20", "198.51.100.20", false)]
    [InlineData("127.0.0.1", "192.0.2.10", "192.0.2.10", true)]
    [InlineData("127.0.0.1", "", null, false)]
    [InlineData("127.0.0.1", "invalid", null, false)]
    [InlineData("127.0.0.1", "127.0.0.1", null, false)]
    [InlineData("198.51.100.20", "127.0.0.1", "198.51.100.20", false)]
    [InlineData("127.0.0.1", null, "127.0.0.1", true)]
    [InlineData("::ffff:127.0.0.1", null, "127.0.0.1", true)]
    public async Task OnlyTrustedProxyRestoresViewerAsync(string transport, string? forwarded, string? expected, bool local)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        ClientHttpPolicy.Configure(app);
        IPAddress? observed = null;
        bool sameHost = false;
        app.Run(context =>
        {
            observed = ClientHttpPolicy.ViewerAddress(context);
            sameHost = DesktopRoutes.IsLocalViewer(context, IPAddress.Parse("192.0.2.10"), [IPAddress.Parse("192.0.2.10")]);
            return Task.CompletedTask;
        });
        DefaultHttpContext request = new();
        request.Connection.RemoteIpAddress = IPAddress.Parse(transport);
        request.Request.Scheme = "http";
        request.Request.Host = new("localhost");
        if (forwarded is not null)
        {
            request.Request.Headers["X-Forwarded-Proto"] = "https";
            request.Request.Headers["X-Forwarded-Host"] = "contents.example";
            request.Request.Headers["X-Forwarded-For"] = forwarded;
        }
        await ((IApplicationBuilder)app).Build()(request).ConfigureAwait(true);
        Assert.Equal(expected, observed?.ToString());
        Assert.Equal(local, sameHost);
        IPAddress transportAddress = IPAddress.Parse(transport);
        bool trusted = IPAddress.IsLoopback(transportAddress) || transportAddress.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(transportAddress.MapToIPv4());
        bool valid = string.IsNullOrEmpty(forwarded) || IPAddress.TryParse(forwarded, out _);
        if (trusted && valid && forwarded is not null)
        {
            Assert.Equal("https", request.Request.Scheme);
            Assert.Equal("contents.example", request.Request.Host.Value);
        }
        else
        {
            Assert.Equal("http", request.Request.Scheme);
            Assert.Equal("localhost", request.Request.Host.Value);
        }
    }
}
