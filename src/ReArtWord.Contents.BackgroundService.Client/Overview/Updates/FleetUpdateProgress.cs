using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Updates;
using ReArtWord.Contents.BackgroundService.Contracts.Updates.Fleet;

namespace ReArtWord.Contents.BackgroundService.Client.Overview.Updates;

/// <summary>Dashboardの更新表示と詳細パネルへ同じ取得結果を渡します。</summary>
internal sealed class FleetUpdateProgress(ManagementApi api)
{
    private readonly List<FleetUpdateView> updates = [];
    private readonly List<FleetUpdateActivity> activities = [];
    private readonly List<Guid> stale = [];
    private bool refreshing;

    /// <summary>直近に取得できた更新状態です。</summary>
    internal IReadOnlyList<FleetUpdateView> Updates => updates;

    /// <summary>別ClientHostの更新も操作IDでまとめた共通表示です。</summary>
    internal IReadOnlyList<FleetUpdateActivity> Activities => activities;

    /// <summary>所有者から新しい通知を確認できていない操作です。</summary>
    internal IReadOnlyList<Guid> StaleOperations => stale;

    /// <summary>直近の照会が失敗したため過去値を表示しているかです。</summary>
    internal bool Unavailable
    {
        get; private set;
    }

    /// <summary>照会不能時も進行中の更新を消さず、再送なしで次の照会を待ちます。</summary>
    /// <returns>同時照会をまとめた読取りです。</returns>
    internal async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (refreshing)
        {
            return;
        }
        refreshing = true;
        try
        {
            FleetUpdateOverview? current;
            try
            {
                current = await api.GetAsync<FleetUpdateOverview>("/client/updates/visible", cancellationToken).ConfigureAwait(true);
            }
            catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                FleetUpdateView[]? legacy = await api.GetAsync<FleetUpdateView[]>("/client/updates", cancellationToken).ConfigureAwait(true);
                current = null;
                if (legacy is not null)
                {
                    current = new(legacy, []);
                }
            }
            if (current is null)
            {
                throw new JsonException("Missing update progress.");
            }
            updates.Clear();
            updates.AddRange(current.Owned);
            Dictionary<Guid, FleetUpdateActivity> combined = current.Owned.Select(FleetUpdateActivity.From).ToDictionary(view => view.OperationId);
            stale.Clear();
            foreach (FleetUpdateObservation observation in current.Observed)
            {
                if (combined.TryAdd(observation.Activity.OperationId, observation.Activity) && observation.Stale)
                {
                    stale.Add(observation.Activity.OperationId);
                }
            }
            activities.Clear();
            activities.AddRange(combined.Values.OrderByDescending(view => view.UpdatedAt));
            Unavailable = current.Unavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Unavailable = true;
        }
        finally
        {
            refreshing = false;
        }
    }
}
