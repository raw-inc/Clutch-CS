using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Client.Pages.Logs;

/// <summary>取得先を保持したログページと、画面の寿命に限定した自動再取得を管理します。</summary>
// 抑制理由: Razorが生成する公開コンポーネント型です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開型です。")]
public sealed partial class LogViewer : IAsyncDisposable
{
    [Inject]
    private NavigationManager Navigation
    {
        get;
        set;
    } = null!;

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    private static string EnterKey => "Enter";
    private static string SpaceKey => " ";
    private static readonly int[] refreshIntervals = [0, 5, 10, 30, 60];
    private readonly CancellationTokenSource lifetime = new();

    // 抑制理由: SearchAsyncのusingが所有し、ここは進行中の検索を取り消す参照だけです。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "SearchAsyncのusingで所有・破棄します。")]
    private CancellationTokenSource? pending;
    private Task polling = Task.CompletedTask;
    private string? from, until;
    private string level = string.Empty, peer = string.Empty, workflow = string.Empty, node = string.Empty, text = string.Empty, causation = string.Empty, message = string.Empty;
    private bool allClusters = true, uncompressedOnly = true;
    private bool hasResults, searchComplete, hasMore, loading, traceOpen, operationsOpen;
    private int generation, healthGeneration, refreshSeconds;
    private DateTimeOffset nextRefresh, nextHealth;
    private DateTimeOffset? lastRefresh;
    private Func<string>? inputProblem;
    private LogQuery? query;
    private LogPageHistory? session;
    private ScopedLogEntry? selected;
    private LogStatistics? statistics;
    private bool HasSelection => !string.IsNullOrWhiteSpace(Api.SelectedClusterId) && !string.IsNullOrWhiteSpace(Api.SelectedPeerId);
    private bool SearchAllClusters => allClusters || !HasSelection;

    // 抑制理由: 取得済みの不変ページを交換し、表示中のコレクションを変更しません。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得済みページの不変参照を交換します。")]
    private IReadOnlyList<ScopedLogEntry> entries = [];

    // 抑制理由: 取得先の部分失敗を不変の一覧として交換します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "不変の取得先一覧を交換します。")]
    private IReadOnlyList<string> unavailablePeers = [];

    private string ResultStatus => !hasResults ? string.Empty
                                               : Ui.Format("{0} logs · {1}", entries.Count, searchComplete ? Ui["Search complete"]
                                                                                                           : Ui["Some ranges are unconfirmed"])
                                                 + (unavailablePeers.Count == 0 ? string.Empty
                                                                                : " · " + Ui.Format("log.viewer.unconfirmed", string.Join(", ", unavailablePeers)));

    /// <summary>表示中の接続先ではなく、取得元のクラスタへ移動します。</summary>
    /// <returns>記録されたWF・ノードの移動先です。</returns>
    private static string? LogLocation(ScopedLogEntry row)
        => string.IsNullOrWhiteSpace(row.Entry.WorkflowId) ? null
                                                           : new EditorWorkflowLocation(row.Entry.WorkflowId, row.Entry.NodeId, row.Source.ClusterId, row.Source.PeerId).ToUri();

    /// <summary>記録元へダブルクリックで移動します。</summary>
    private void OpenLog(ScopedLogEntry row)
    {
        if (LogLocation(row) is { } uri)
        {
            Navigation.NavigateTo(uri);
        }
    }

    /// <summary>キーボードから同じ取得元の詳細を選択します。</summary>
    private void SelectLog(KeyboardEventArgs args, ScopedLogEntry row)
    {
        if (string.Equals(args.Key, EnterKey, StringComparison.Ordinal) || string.Equals(args.Key, SpaceKey, StringComparison.Ordinal))
        {
            selected = row;
        }
    }

    /// <summary>関連ログの絞込みを解除します。</summary>
    /// <returns>再検索の完了です。</returns>
    // 抑制理由: UIコールバックの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの固定署名です。")]
    private Task ClearRelatedAsync()
    {
        causation = string.Empty;
        return SearchAsync(0);
    }

    /// <summary>選択した記録と同じ因果IDを、現在の検索範囲で追跡します。</summary>
    /// <returns>関連ログの検索です。</returns>
    // 抑制理由: UIコールバックの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの固定署名です。")]
    private Task SearchCausationAsync(Guid? id)
    {
        if (id is null)
        {
            return Task.CompletedTask;
        }
        text = level = peer = workflow = node = string.Empty;
        from = until = null;
        causation = id.Value.ToString();
        return SearchAsync(0);
    }

    /// <summary>検索範囲を切り替え、以前のカーソルを持ち越しません。</summary>
    /// <returns>新しい範囲の先頭ページです。</returns>
    // 抑制理由: UIコールバックの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの固定署名です。")]
    private Task ScopeChangedAsync(ChangeEventArgs args)
    {
        allClusters = args.Value?.ToString() is "all";
        statistics = null;
        return SearchAsync(0);
    }

    /// <summary>圧縮履歴を含める範囲を変更し、以前のページや自動再取得の条件を引き継ぎません。</summary>
    /// <returns>変更後の検索条件による先頭ページです。</returns>
    // 抑制理由: UIコールバックの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの固定署名です。")]
    private Task UncompressedOnlyChangedAsync(ChangeEventArgs args)
    {
        uncompressedOnly = args.Value is true;
        return SearchAsync(0);
    }

    /// <summary>ページ内だけの自動取得間隔を変更します。</summary>
    /// <returns>有効化時の先頭ページ取得です。</returns>
    // 抑制理由: UIコールバックの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの固定署名です。")]
    private Task RefreshChangedAsync(ChangeEventArgs args)
    {
        if (!int.TryParse(args.Value?.ToString(), out int seconds) || !refreshIntervals.Contains(seconds))
        {
            return Task.CompletedTask;
        }
        refreshSeconds = seconds;
        nextRefresh = Api.UtcNow.AddSeconds(seconds);
        return seconds == 0 ? Task.CompletedTask
                            : SearchAsync(0, refresh: query is not null);
    }

    /// <summary>初期の記録状態を取得し、所有する監視だけを開始します。</summary>
    /// <returns>初期化の完了です。</returns>
    // 抑制理由: Blazorの固定署名で、pollingはDisposeAsyncで回収します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "Blazorの固定署名です。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "pollingで所有しDisposeAsyncで回収します。")]
    protected override async Task OnInitializedAsync()
    {
        await HealthAsync().ConfigureAwait(true);
        polling = PollAsync();
    }

    /// <summary>画面で確定した検索条件だけを組み立てます。</summary>
    /// <returns>表示タイムゾーンをUTCへ戻した検索条件です。</returns>
    private LogQuery ReadQuery()
    {
        DateTimeOffset? start = FormInput.ZonedTime(from, Ui.Format("display.timezone.from", DisplayTimeZone.Id), DisplayTimeZone);
        DateTimeOffset? end = FormInput.ZonedTime(until, Ui.Format("display.timezone.until", DisplayTimeZone.Id), DisplayTimeZone);
        if (end < start)
        {
            throw new FormatException();
        }
        Guid? id = string.IsNullOrWhiteSpace(causation) ? null
                                                        : Guid.Parse(causation);
        return new(start, end, string.IsNullOrWhiteSpace(level) ? null
                                                                : [level], string.IsNullOrWhiteSpace(peer) ? null
                                                                                                           : [peer], Optional(workflow), Optional(node), Optional(text), CausationId: id, UncompressedOnly: uncompressedOnly);
    }

    /// <summary>空の文字列フィルターだけを省略します。</summary>
    /// <returns>指定済みの文字列、またはnullです。</returns>
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null
                                                                                      : value;

    /// <summary>検索条件と取得先を固定し、古い応答は画面へ反映しません。</summary>
    /// <returns>前後のページ、または最新の先頭ページです。</returns>
    // 抑制理由: UIコールバックの固定署名で、取消は要求ごとと画面寿命で管理します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの固定署名です。")]
    private async Task SearchAsync(int direction, bool refresh = false)
    {
        bool moving = direction != 0;
        if (((moving || refresh) && loading) || (moving && (session is null || session.PageIndex + direction < 0 || (0 < direction && !hasMore))))
        {
            return;
        }
        int current = ++generation;
        using Task cancelling = pending?.CancelAsync() ?? Task.CompletedTask;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        pending = cancellation;
        loading = true;
        try
        {
            await cancelling.ConfigureAwait(true);
            if (!moving && !refresh)
            {
                inputProblem = null;
                query = ReadQuery();
                selected = null;
            }
            if (moving)
            {
                refreshSeconds = 0;
            }
            LogPageHistory? reader = session;
            if (!moving)
            {
                if (!refresh)
                {
                    session = null;
                    hasMore = false;
                }
                reader = await LogPageHistory.CreateAsync(Api, query ?? ReadQuery(), SearchAllClusters, cancellation.Token).ConfigureAwait(true);
            }
            int index = moving ? reader!.PageIndex + direction
                               : 0;
            LogSearchPage page = await reader!.ReadAsync(index, cancellation.Token).ConfigureAwait(true);
            if (current != generation)
            {
                return;
            }
            unavailablePeers = page.UnconfirmedSources;
            searchComplete = page.Complete;
            if (refresh && page.RetrievedSources == 0)
            {
                message = "Logs could not be retrieved. Check the time range, related log ID, and connection.";
                return;
            }
            session = reader;
            entries = page.Entries;
            inputProblem = null;
            selected = null;
            hasMore = page.HasMore;
            hasResults = true;
            lastRefresh = Api.UtcNow;
            message = page.Sources == 0 ? "log.viewer.noSources"
                                        : string.Empty;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or FormatException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (current == generation)
            {
                inputProblem = Ui.DeferredException(exception);
                message = "Logs could not be retrieved. Check the time range, related log ID, and connection.";
            }
        }
        finally
        {
            if (current == generation)
            {
                loading = false;
                pending = null;
                nextRefresh = Api.UtcNow.AddSeconds(refreshSeconds);
            }
        }
    }

    /// <summary>選択中クラスタの記録状態だけを確認し、全クラスタの統計とは扱いません。</summary>
    /// <returns>記録状態の更新です。</returns>
    // 抑制理由: 画面所有の取消を使用するUIメソッドです。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面所有の取消を使用します。")]
    private async Task HealthAsync()
    {
        if (SearchAllClusters)
        {
            return;
        }
        int current = ++healthGeneration;
        try
        {
            LogStatistics? observed = await Api.GetAsync<LogStatistics>("/api/logs/statistics", lifetime.Token).ConfigureAwait(true);
            if (current == healthGeneration && !SearchAllClusters)
            {
                statistics = observed;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            statistics = null;
        }
    }

    /// <summary>遅い取得を積み重ねず、完了後に次の間隔を待ちます。</summary>
    /// <returns>画面終了までの監視です。</returns>
    // 抑制理由: 画面所有の取消を使用するUIメソッドです。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "画面所有の取消を使用します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true))
            {
                bool changed = false;
                if (!SearchAllClusters && nextHealth <= Api.UtcNow)
                {
                    await HealthAsync().ConfigureAwait(true);
                    nextHealth = Api.UtcNow.AddSeconds(5);
                    changed = true;
                }
                if (0 < refreshSeconds && query is not null && !loading && nextRefresh <= Api.UtcNow)
                {
                    await SearchAsync(0, refresh: true).ConfigureAwait(true);
                    changed = true;
                }
                if (changed)
                {
                    await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    /// <summary>画面を離れたら照会と監視を終了します。</summary>
    /// <returns>所有する監視の終了です。</returns>
    // 抑制理由: IAsyncDisposableの固定署名です。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "IAsyncDisposableの固定署名です。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            await polling.ConfigureAwait(true);
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>開始日時の未確定入力を保持します。</summary>
    private void FromChanged(ChangeEventArgs args) => from = args.Value?.ToString();

    /// <summary>終了日時の未確定入力を保持します。</summary>
    private void UntilChanged(ChangeEventArgs args) => until = args.Value?.ToString();
}
