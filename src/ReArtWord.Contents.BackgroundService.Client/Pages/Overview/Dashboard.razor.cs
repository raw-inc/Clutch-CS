using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Operations.Resources;
using ReArtWord.Contents.BackgroundService.Client.Overview;
using ReArtWord.Contents.BackgroundService.Client.Overview.Updates;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.History;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Pages.Overview;

/// <summary>Dashboardの表示切替と各管理パネルへの操作を調整します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class Dashboard : IAsyncDisposable
{
    private static string MatchAllClusters => "All clusters";
    private static string MatchPeerList => "Peer list";
    private static string MatchSelectedCluster => "Selected cluster";
    private static string MatchApplications => "Applications";
    private static string MatchExecutionProcesses => "Execution & processes";

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private ConnectionState Connection
    {
        get;
        set;
    } = null!;

    [Inject]
    private OperationJournal Journal
    {
        get;
        set;
    } = null!;

    private readonly List<WorkflowListSummary> workflows = [];

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<ExecutionResultView> executions = [];

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<string> secretNames = [];
    private string message = string.Empty, secretName = string.Empty, secretValue = string.Empty;
    private JsonElement? result;
    private WorkflowListSummary? activation;
    private bool secretOpen, membershipOpen;
    private string view = "All clusters";
    private bool HasSelection => Api.SelectedClusterId is not null && Api.SelectedPeerId is not null;

    private AllClustersOverview? allOverview;
    private PeerList? peerList;
    [Inject]
    private FleetUpdateProgress? fleetUpdates
    {
        get; set;
    }

    /// <summary>Dashboardの表示タブを切り替えて必要な状態を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectViewAsync(string selected)
    {
        view = selected;
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>未取得の版を区別して識別子を省略表示します。</summary>
    /// <returns>版の短縮表示、未反映ならその状態の翻訳です。</returns>
    private string ShortRevision(string? revision) => revision is null ? Ui["Not activated"]
                                                                       : revision[..Math.Min(12, revision.Length)];
    private string? workflowCursor, executionCursor, workflowPageCursor, executionPageCursor;
    private string? listCluster, listPeer;
    private Guid? listInstance;
    private bool ListIsCurrent => listCluster is not null && SameTarget(listCluster, listPeer) && listInstance == Connection.Current?.InstanceId;

    /// <summary>現在の管理接続と取得時の対象が一致するか判定します。</summary>
    /// <returns>指定した条件を満たすかどうかです。</returns>
    private bool SameTarget(string? cluster, string? peer) => string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(peer, Api.SelectedPeerId, StringComparison.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private ProcessPanel? processPanel;
    private ResourcePanel? resourcePanel;
    private DashboardOverview? overview;

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override async Task OnInitializedAsync()
    {
        await GuardAsync(() => Journal.LoadAsync(cancellationToken: lifetime.Token)).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        polling = PollAsync();
    }

    /// <summary>操作の例外を確認不能の表示へ変換します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or JSException or OperationCanceledException)
        {
            message = exception is InvalidOperationException ? exception.Message
                                                             : "This API result is unconfirmed. Query recorded requests from Operations.";
        }
    }

    /// <summary>表示対象の最新状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RefreshAsync() => GuardAsync(async () =>
    {
        if (fleetUpdates is not null)
        {
            await fleetUpdates.RefreshAsync(lifetime.Token).ConfigureAwait(true);
        }
        if (string.Equals(view, MatchAllClusters, StringComparison.Ordinal))
        {
            message = string.Empty;
            if (allOverview is not null)
            {
                await allOverview.RefreshAsync(lifetime.Token).ConfigureAwait(true);
            }

            return;
        }

        if (string.Equals(view, MatchPeerList, StringComparison.Ordinal))
        {
            message = string.Empty;
            if (peerList is not null)
            {
                await peerList.RefreshAsync(lifetime.Token).ConfigureAwait(true);
            }

            return;
        }

        await Connection.RefreshAsync(lifetime.Token).ConfigureAwait(true);
        string? cluster = Api.SelectedClusterId, peer = Api.SelectedPeerId;
        Guid? instance = Connection.Current?.InstanceId;
        if (cluster is null || peer is null || instance is null)
        {
            return;
        }

        if (!string.Equals(view, MatchSelectedCluster, StringComparison.Ordinal))
        {
            return;
        }

        if (!ListIsCurrent)
        {
            workflowPageCursor = null;
            executionPageCursor = null;
        }

        if (overview is not null)
        {
            await overview.RefreshAsync(lifetime.Token).ConfigureAwait(true);
        }

        WorkflowListPage? workflowPage = await Api.PostForPeerAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(Cursor: workflowPageCursor), peer, cluster, cancellationToken: lifetime.Token).ConfigureAwait(true);
        ExecutionHistoryPage? executionPage = await Api.PostForPeerAsync<ExecutionHistoryPage>("/api/executions/query", new ExecutionHistoryQuery(Cursor: executionPageCursor), peer, cluster, cancellationToken: lifetime.Token).ConfigureAwait(true);
        if (!SameTarget(cluster, peer) || Connection.Current?.InstanceId != instance)
        {
            return;
        }

        workflows.Clear();
        workflows.AddRange(workflowPage?.Entries ?? []);
        workflowCursor = workflowPage?.Cursor;
        executions = executionPage?.Entries ?? [];
        executionCursor = executionPage?.Cursor;
        listCluster = cluster;
        listPeer = peer;
        listInstance = instance;
        message = string.Join("\n", new[] { Connection.Current?.StartupProblem, Connection.Current?.SynchronizationProblem }.Where(item => !string.IsNullOrWhiteSpace(item)));
    });

    /// <summary>同じ対象のワークフロー一覧を次ページへ進めます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task NextWorkflowsAsync() => GuardAsync(async () =>
    {
        if (workflowCursor is null || !ListIsCurrent)
        {
            return;
        }

        string? cluster = listCluster, peer = listPeer;
        workflowPageCursor = workflowCursor;
        WorkflowListPage? page = await Api.PostForPeerAsync<WorkflowListPage>("/api/workflows/query", new WorkflowListQuery(Cursor: workflowPageCursor), peer, cluster, cancellationToken: lifetime.Token).ConfigureAwait(true);
        if (!SameTarget(cluster, peer))
        {
            return;
        }

        if (page is null)
        {
            throw new InvalidOperationException("The next page is unconfirmed.");
        }

        workflows.Clear();
        workflows.AddRange(page.Entries);
        workflowCursor = page.Cursor;
    });

    /// <summary>同じ対象の実行結果一覧を次ページへ進めます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task NextExecutionsAsync() => GuardAsync(async () =>
    {
        if (executionCursor is null || !ListIsCurrent)
        {
            return;
        }

        string? cluster = listCluster, peer = listPeer;
        executionPageCursor = executionCursor;
        ExecutionHistoryPage? page = await Api.PostForPeerAsync<ExecutionHistoryPage>("/api/executions/query", new ExecutionHistoryQuery(Cursor: executionPageCursor), peer, cluster, cancellationToken: lifetime.Token).ConfigureAwait(true);
        if (!SameTarget(cluster, peer))
        {
            return;
        }

        if (page is null)
        {
            throw new InvalidOperationException("The next page is unconfirmed.");
        }

        executions = page.Entries;
        executionCursor = page.Cursor;
    });

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer? timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true))
            {
                await RefreshAsync().ConfigureAwait(true);
                if (string.Equals(view, MatchApplications, StringComparison.Ordinal) && processPanel is not null)
                {
                    await processPanel.RefreshAsync(lifetime.Token).ConfigureAwait(true);
                }

                if (string.Equals(view, MatchExecutionProcesses, StringComparison.Ordinal) && resourcePanel is not null)
                {
                    await resourcePanel.RefreshAsync().ConfigureAwait(true);
                }

                await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>画面が所有する継続処理を終了し、保持する参照と資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
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
}
