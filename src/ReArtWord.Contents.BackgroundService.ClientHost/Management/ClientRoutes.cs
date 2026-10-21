using ReArtWord.Contents.BackgroundService.ClientHost.Management.Overview;
using ReArtWord.Contents.BackgroundService.ClientHost.Networking;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Discovery;
using ReArtWord.Contents.BackgroundService.Contracts.Management.Overview;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Observers;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Management;

/// <summary>クライアント自身の接続設定と、元管理要求の照会を公開します。</summary>
internal static class ClientRoutes
{
    /// <summary>Coreの一覧とクライアント側のNIC一覧を取り違えず取得します。</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/client/network", (ObserverConnection connection) => connection.GetState());
        app.MapPost("/client/network", (ClientNetworkBinding binding, ObserverConnection connection, CancellationToken cancellation) => connection.SelectAsync(binding, cancellation));
        app.MapPut("/client/networks", (ClientNetworkBinding[] bindings, ObserverConnection connection, CancellationToken cancellation) => connection.SelectManyAsync(bindings, cancellation));
        app.MapGet("/client/clusters", (ObserverConnection connection) => connection.GetClusters());
        app.MapGet("/client/dashboard/hosts", (string? cluster, DashboardHostStore hosts, CancellationToken cancellation) => hosts.ReadAsync(cluster, cancellation));
        app.MapPost("/client/dashboard/hosts/remove", async (DashboardHostRemoval request, DashboardHostStore hosts, CancellationToken cancellation) =>
            Results.Ok(new { removed = await hosts.RemoveAsync(request.HostId, cancellation).ConfigureAwait(false) }));
        app.MapGet("/client/commands/{id:guid}", (Guid id, ClientCommandLedger ledger, CancellationToken cancellation) => ledger.ReadAsync(id, cancellation));
        app.MapGet("/client/commands/{id:guid}/result", async (Guid id, ClientCommandLedger ledger, ObserverConnection connection, CancellationToken cancellation) =>
        {
            ClientCommandRecord? record = await ledger.ReadAsync(id, cancellation).ConfigureAwait(false);
            if (record is null)
            {
                return Results.NotFound();
            }
            UdpObserver? observer = connection.FindObserver(record.ClusterId, record.TargetPeerId);
            if (observer is null)
            {
                return Results.Ok(new ManagementQueryResult(false, Reason: "Observer.ReceiptTargetUnavailable"));
            }
            ManagementReceiptQuery query = new(record.RequestId, record.ClusterId, record.TargetPeerId, record.RequestFingerprint);
            return Results.Ok(await observer.QueryAsync(query, cancellation).ConfigureAwait(false));
        });
    }
}
