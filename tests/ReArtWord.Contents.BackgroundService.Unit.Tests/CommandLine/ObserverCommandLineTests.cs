using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReArtWord.Contents.BackgroundService.CommandLine;
using Xunit;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.CommandLine;

/// <summary>CLIのObserver入口と要求受付境界を実HTTPで確認します。</summary>
public sealed class ObserverCommandLineTests
{
    /// <summary>NIC選択は対象Peerを仮定せず、要求IDを一度送ります。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task NetworkSelectionUsesLocalFacadeAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapPost("/client/network", (HttpContext context) =>
        {
            Assert.False(context.Request.Headers.ContainsKey("X-Contents-Peer"));
            Assert.False(context.Request.Headers.ContainsKey("X-Contents-Cluster"));
            Assert.True(Guid.TryParse(context.Request.Headers["X-Contents-Request"], out _));
            return Results.Ok(new { listening = true });
        });
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(0, await RunAsync(["--url", app.Urls.Single(), "network", "set", "--body", "-"]).ConfigureAwait(true));
    }

    /// <summary>Core直結と対象未選択を副作用送信前に拒否します。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task DirectCoreAndMissingTargetNeverSendAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        int sent = 0;
        app.MapPost("/api/runs", () =>
        {
            sent++;
            return Results.Ok(new { outcome = "succeeded" });
        });
        await app.StartAsync().ConfigureAwait(true);
        string[] command = ["--url", app.Urls.Single(), "workflow", "run", "--body", "-"];
        Assert.Equal(2, await RunAsync(command).ConfigureAwait(true));
        Assert.Equal(2, await RunAsync([.. command, "--cluster", "a", "--peer", "b"]).ConfigureAwait(true));
        Assert.Equal(0, sent);
        Assert.Equal(2, await RunAsync(["--url", "http://192.0.2.1:5080", "network", "get"]).ConfigureAwait(true));
    }

    /// <summary>bridgeの受付成功を内側の業務完了と混同しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Theory]
    [InlineData("unknown", 5)]
    [InlineData("failed", 4)]
    [InlineData("succeeded", 0)]
    public async Task ReceiptClassifiesDomainResultAsync(string outcome, int expected)
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapGet("/client/commands/{id}/result", () => Results.Ok(new { running = false, reply = new { outcome = "succeeded", value = new { json = new { outcome } } } }));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(expected, await RunAsync(["--url", app.Urls.Single(), "request", "status", "--id", Guid.NewGuid().ToString("D")]).ConfigureAwait(true));
    }

    /// <summary>bridge応答の内側が別の操作IDなら確定しません。</summary>
    /// <returns>試験が終了するまでの非同期処理。</returns>
    [Fact]
    public async Task ReceiptWithAnotherDomainIdentityStaysUnknownAsync()
    {
        WebApplication app = Create();
        await using ConfiguredAsyncDisposable lifetime = app.ConfigureAwait(true);
        app.MapGet("/client/commands/{id}/result", () => Results.Ok(new { running = false, reply = new { outcome = "succeeded", value = new { json = new { operationId = Guid.NewGuid(), outcome = "succeeded" } } } }));
        await app.StartAsync().ConfigureAwait(true);
        Assert.Equal(5, await RunAsync(["--url", app.Urls.Single(), "request", "status", "--id", Guid.NewGuid().ToString("D")]).ConfigureAwait(true));
    }

    /// <summary>任意ポートのローカルHTTP 試験用の構成を作ります。</summary>
    /// <returns>呼出元が破棄するアプリです。</returns>
    private static WebApplication Create()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        return builder.Build();
    }

    /// <summary>引数を補完せず一回実行します。</summary>
    /// <returns>実終了コードです。</returns>
    private static async Task<int> RunAsync(string[] args)
    {
        using StringReader input = new("{}");
        using StringWriter output = new(CultureInfo.InvariantCulture);
        using StringWriter error = new(CultureInfo.InvariantCulture);
        return await CommandLineApplication.RunAsync(args, input, output, error).ConfigureAwait(true);
    }
}
