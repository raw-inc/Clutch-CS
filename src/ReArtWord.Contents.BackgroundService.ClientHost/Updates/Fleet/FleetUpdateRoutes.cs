using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.ClientHost.Updates.Fleet;

/// <summary>全クラスタのhost順更新と永続進捗をローカル画面へ公開します。</summary>
internal static class FleetUpdateRoutes
{
    /// <summary>プレビュー・一度だけの受付・結果照会を別APIとして登録します。</summary>
    internal static void Map(WebApplication app)
    {
        app.MapGet("/client/updates/events", (FleetUpdateEvents events) => new { events.Problem });
        app.MapPost("/client/updates/preview", (FleetReleaseSelection selection, FleetUpdatePlanner planner) => planner.Create(selection));
        app.MapPost("/client/updates", (FleetUpdateRequest request, FleetUpdates updates, CancellationToken token) => updates.EnqueueAsync(request, token));
        app.MapGet("/client/updates", (FleetUpdateStore store, CancellationToken token) => store.ListViewsAsync(token));
        app.MapGet("/client/updates/visible", (FleetUpdateVisibility visibility, CancellationToken token) => visibility.ReadAsync(token));
        app.MapGet("/client/updates/{id:guid}", (Guid id, FleetUpdateStore store, CancellationToken token) => store.ReadAsync(id, token));
        app.MapPost("/client/updates/{id:guid}/reconcile", (Guid id, FleetUpdates updates, CancellationToken token) => updates.ReconcileAsync(id, token));
    }
}
