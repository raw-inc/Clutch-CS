using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Networking;

/// <summary>loopback proxyの外部originと、管理応答のキャッシュ境界を統一します。</summary>
internal static class ClientHttpPolicy
{
    private static readonly string instance = Guid.NewGuid().ToString("D");

    /// <summary>ローカルTunnelだけを信頼し、外部HTTPSを接続元検証へ引き継ぎます。</summary>
    internal static void Configure(WebApplication app)
    {
        ForwardedHeadersOptions options = new() { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost, ForwardLimit = 1 };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        app.UseForwardedHeaders(options);
        app.Use((HttpContext context, RequestDelegate next) => ApplyAsync(context, next, context.RequestAborted));
    }

    /// <summary>信頼済みproxyで復元した閲覧端末を返し、接続元不明の中継をloopback端末と誤認しません。</summary>
    /// <returns>直接接続または復元済みの接続元です。不明な中継ではnullです。</returns>
    internal static IPAddress? ViewerAddress(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        IHeaderDictionary headers = context.Request.Headers;
        bool proxy = headers.ContainsKey("X-Original-For") || headers.ContainsKey("X-Original-Proto") || headers.ContainsKey("X-Original-Host")
            || headers.ContainsKey("X-Forwarded-For") || headers.ContainsKey("X-Forwarded-Proto") || headers.ContainsKey("X-Forwarded-Host");
        if (IPAddress.IsLoopback(address) && proxy)
        {
            return null;
        }
        return address;
    }

    /// <summary>同じURLでも対象ヘッダーで異なる管理応答を、ブラウザ・中継で再利用させません。</summary>
    /// <returns>管理要求の処理です。</returns>
    private static Task ApplyAsync(HttpContext context, RequestDelegate next, CancellationToken cancellationToken = default)
    {
        string health = "/health", client = "/client", api = "/api";
        string updater = "/ui-update.html", entry = "/", startup = "/ui-start.html";
        bool management = context.Request.Path.StartsWithSegments(client, StringComparison.OrdinalIgnoreCase) || context.Request.Path.StartsWithSegments(api, StringComparison.OrdinalIgnoreCase) || context.Request.Path == health;
        if (!management)
        {
            string policy = "no-cache, max-age=0, must-revalidate";
            if (context.Request.Path == updater || context.Request.Path == entry || context.Request.Path == startup)
            {
                policy = "no-store";
            }
            context.Response.OnStarting(() =>
            {
                // 同名の起動資材は再検証し、PWA内の版ごとのoffline cacheと区別します。
                context.Response.Headers.CacheControl = policy;
                return Task.CompletedTask;
            });
        }
        if (management)
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Append("Vary", "X-Contents-Cluster, X-Contents-Peer");
                context.Response.Headers["X-Contents-Observed-At"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Contents-Host-Instance"] = instance;
                return Task.CompletedTask;
            });
        }
        return next(context);
    }
}
