using ReArtWord.Contents.BackgroundService.Contracts.Persistence;
using ReArtWord.Contents.BackgroundService.Contracts.Persistence.InternalData;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Availability;

/// <summary>内部状態だけのキャッシュ操作を外部副作用経路と分離します。</summary>
internal static class InternalDataRoutes
{
    /// <summary>同じ明示クラスタ・Peer ヘッダーを使うローカル経路です。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapPost("/client/internal-data/read", (HttpContext context, InternalDataClientTarget target, InternalDataBridge bridge, CancellationToken cancellation) =>
            bridge.ReadAsync(Header(context, "X-Contents-Cluster"), Header(context, "X-Contents-Peer"), target, cancellation));
        app.MapPost("/client/internal-data/stage", (HttpContext context, InternalDataClientChange change, InternalDataBridge bridge, CancellationToken cancellation) =>
            bridge.StageAsync(Header(context, "X-Contents-Cluster"), Header(context, "X-Contents-Peer"), change, cancellation));
        app.MapPost("/client/internal-data/reconcile", (HttpContext context, InternalDataClientTarget target, InternalDataBridge bridge, CancellationToken cancellation) =>
            bridge.ReconcileAsync(Header(context, "X-Contents-Cluster"), Header(context, "X-Contents-Peer"), target, cancellation));
        app.MapPost("/client/internal-data/query", (HttpContext context, InternalDataClientTarget target, InternalDataBridge bridge, CancellationToken cancellation) =>
            bridge.QueryAsync(Header(context, "X-Contents-Cluster"), Header(context, "X-Contents-Peer"), target, cancellation));
        app.MapPost("/client/internal-data/discard", (HttpContext context, InternalDataClientTarget target, InternalDataBridge bridge, CancellationToken cancellation) =>
            bridge.DiscardAsync(Header(context, "X-Contents-Cluster"), Header(context, "X-Contents-Peer"), target, cancellation));
    }

    /// <summary>既定の別接続先へ暗黙代替しません。</summary>
    /// <returns>明示された接続識別子です。</returns>
    private static string Header(HttpContext context, string name)
    {
        string value = context.Request.Headers[name].ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }
}
