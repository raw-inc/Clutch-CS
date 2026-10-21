using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Diagnostics;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Graph;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Navigation;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Pages;

/// <summary>草稿の編集・保存・実行と接続先の切替を調整します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class WorkflowEditorPage : IAsyncDisposable
{
    [Inject]
    private Navigation.EditorNoticeHub Notices
    {
        get; set;
    } = null!;

    private Func<string>? displayedMessage;

    [Inject]
    private EditorToggleStates ToggleStates
    {
        get; set;
    } = null!;

    private static string MatchProperties => "properties";
    private static string MatchDefault => "default";

    [Inject]
    private EditorPreviews Previews
    {
        get; set;
    } = null!;

    [Inject]
    private EditorRuntimeDiagnostics RuntimeDiagnostics
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDiagnostics Diagnostics
    {
        get; set;
    } = null!;

    [Inject]
    private ManagementApi Api
    {
        get;
        set;
    } = null!;

    [Inject]
    private BrowserStorage Storage
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
    private EditorDocument Model
    {
        get;
        set;
    } = null!;

    [Inject]
    private EditorCollaboration Collaboration
    {
        get;
        set;
    } = null!;

    [Inject]
    private IJSRuntime Script
    {
        get;
        set;
    } = null!;

    [Inject]
    private NavigationManager Navigation
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

    private bool initializing = true;
    private readonly List<NodeDescriptor> catalog = [];
    private readonly List<WorkflowListSummary> workflows = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task polling = Task.CompletedTask;
    private OperationRecord? pendingRun;
    private Task persistence = Task.CompletedTask;
    private bool persistenceRequested;
    private bool busy, dragging, invalid, importOpen, conflictOpen, activationOpen, saveConflict, operationsOpen;
    private string importText = string.Empty;
    private Func<string>? message;

    private double zoom = .9;
    private bool catalogOpen, inspectorOpen, showDetails = true;
    private bool documentsOpen, documentToolsOpen, saveOptionsOpen, collaborationOpen, viewOptionsOpen, compatibilityOpen, debugOpen;
    private GraphViewOptions viewOptions = new();
    private string? viewProblem;
    private ElementReference canvasContainer;
    private NodeGraph? graph;
    private NodeDescriptor? catalogDrag;
    private int catalogDragGeneration;
    private EActivationPolicy activation = EActivationPolicy.Auto;
    private bool referencesOffline = true;
    private string? workflowCursor;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyList<EditConflict> conflicts = [];
    private bool Detached => Connection.Current?.Status == EPeerStatus.Detached;
    private bool LocalContext => Detached || Model.Draft.LocalOnly;

    /// <summary>操作結果の変更だけを通知し、通常の再描画で同じ結果を出し直しません。</summary>
    private void PublishNotice()
    {
        if (!ReferenceEquals(message, displayedMessage))
        {
            displayedMessage = message;
            Notices.Set("operation", message);
        }
    }

    /// <summary>現在の接続先に対応する草稿の所有先へ切り替えます。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task EnsureTargetAsync() => Api.SelectedClusterId is not null && Api.SelectedPeerId is not null && (!string.Equals(Model.ClusterId, Api.SelectedClusterId, StringComparison.Ordinal) || !string.Equals(Model.PeerId, Api.SelectedPeerId, StringComparison.Ordinal)) ? Model.SwitchTargetAsync(Api.SelectedClusterId, Api.SelectedPeerId, lifetime.Token)
                                                                                                                                                                                                                                                                                : Task.CompletedTask;
    private IReadOnlyList<string> AllRoutes => catalog.SelectMany(item => item.Routes).Concat(["success", "failure", "notExecuted", "unknown", "cancelled", "*", "starting", "listening", "completed"]).Distinct().ToArray();

    /// <summary>初回表示に必要なデータを取得し、画面の状態を準備します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    // 抑制理由: 開始した監視Taskはフィールドで所有し、画面終了時に完了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。"), System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "継続監視Taskはpollingで保持し、DisposeAsyncで取消後にawaitします。")]
    protected override async Task OnInitializedAsync()
    {
        ToggleStates.Reset();
        Model.Changed += Changed;
        await ReadViewAsync().ConfigureAwait(true);
        await GuardAsync(async () =>
        {
            await Model.InitializeAsync(lifetime.Token).ConfigureAwait(true);
            EditorWorkflowLocation? location = EditorWorkflowLocation.Read(Navigation.Uri);
            await EnsureTargetAsync().ConfigureAwait(true);
            EditorReferenceData? cached = await EditorReferenceData.ReadAsync(Storage, Api.SelectedClusterId, Api.SelectedPeerId, lifetime.Token).ConfigureAwait(true);
            catalog.AddRange(cached?.Catalog ?? []);
            await Recipes.RefreshAsync(lifetime.Token).ConfigureAwait(true);
            catalog.AddRange(Recipes.Catalog.Where(item => !catalog.Any(existing => string.Equals(existing.Type, item.Type, StringComparison.Ordinal))));
            workflows.AddRange(cached?.Workflows ?? []);
            await GuardAsync(RefreshReferencesAsync).ConfigureAwait(true);
            if (location is { } target)
            {
                initialWorkflow = target.WorkflowId;
                initialNode = target.NodeId;
                message = Ui.Deferred(await EditorWorkflowLoader.OpenAsync(Model, Api, Storage, target.WorkflowId, lifetime.Token).ConfigureAwait(true));
                Navigation.NavigateTo(EditorWorkflowLocation.WithoutTarget(Navigation), replace: true);
            }
            documentsOpen = false;
            catalogOpen = Model.Draft.Document.Nodes.Count == 0;
        }).ConfigureAwait(true);
        if (Model.HasDocument)
        {
            Diagnostics.UpdateDraft(Model.ClusterId, Model.Draft);
        }
        initializing = false;
        polling = PollAsync();
    }

    /// <summary>草稿の変更をローカル保存し、編集状態を再描画へ反映します。</summary>
    // 抑制理由: 連続編集を一つの保存Taskへまとめ、DisposeAsyncで終了を確認します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "保存Taskはpersistenceで保持し、画面終了時にawaitします。")]
    private void Changed()
    {
        if (lifetime.IsCancellationRequested)
        {
            return;
        }

        if (Model.HasDocument)
        {
            Diagnostics.UpdateDraft(Model.ClusterId, Model.Draft);
        }
        persistenceRequested = true;
        if (persistence.IsCompletedSuccessfully)
        {
            persistence = PersistChangesAsync(lifetime.Token);
        }

        RequestRenderFromEvent();
    }

    /// <summary>前の保存中に届いた編集を次の一回へまとめ、保存結果を描画へ通知します。</summary>
    /// <returns>要求済みのローカル保存または画面終了による取消しの完了です。</returns>
    private async Task PersistChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            do
            {
                persistenceRequested = false;
                await Model.PersistAsync(cancellationToken).ConfigureAwait(true);
                RequestRenderFromEvent();
            }
            while (persistenceRequested && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 画面終了時の取消しは、新しい保存失敗として通知しません。
        }
    }

    /// <summary>画面の存続期間だけ状態を定期確認し、取消要求で終了します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task PollAsync()
    {
        using PeriodicTimer? timer = new PeriodicTimer(TimeSpan.FromMilliseconds(150));
        int presenceTicks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(true))
            {
                try
                {
                    if (deleteOpen || forceSaveOpen || busy || !Model.HasDocument)
                    {
                        continue;
                    }
                    bool sharedContext = !LocalContext && string.Equals(Model.ClusterId, Api.SelectedClusterId, StringComparison.Ordinal)
                        && string.Equals(Model.PeerId, Api.SelectedPeerId, StringComparison.Ordinal);
                    await Activity.RefreshAsync(Model.ClusterId, Model.Draft, Connection.Current?.Peers ?? [], EffectiveView.HighlightRunning && sharedContext && !Model.Draft.Document.IsDashboard, lifetime.Token).ConfigureAwait(true);
                    if (++presenceTicks < 10)
                    {
                        if (sharedContext)
                        {
                            await Collaboration.SynchronizePresenceAsync(Connection.Current?.PeerId ?? "browser", lifetime.Token).ConfigureAwait(true);
                        }
                        continue;
                    }
                    presenceTicks = 0;
                    bool editing = await Script.InvokeAsync<bool>("contentsBrowser.editing", lifetime.Token).ConfigureAwait(true);
                    if (referencesOffline && Connection.Connected && !editing && !dragging && !busy)
                    {
                        await GuardAsync(RefreshReferencesAsync).ConfigureAwait(true);
                    }

                    if (sharedContext && !busy)
                    {
                        Collaboration.AllowUnfocusedUpdates = editing && !dragging && !invalid && !Model.HasInvalidInputs && !conflictOpen;
                        await Collaboration.SynchronizeAsync(!busy && !dragging && !invalid && !Model.HasInvalidInputs && !editing && !conflictOpen, Connection.Current?.PeerId ?? "browser", lifetime.Token).ConfigureAwait(true);
                    }

                    if (Connection.Connected && string.Equals(Model.ClusterId, Api.SelectedClusterId, StringComparison.Ordinal) && Connection.Current is { } observed)
                    {
                        await LivePolicy.RefreshAsync(Model.ClusterId!, observed.Coordinator ?? observed.PeerId, Model.Draft.Document.Id, readSettings: ExperimentalEnabled && viewOptionsOpen, knownEnabled: observed.LiveWorkflowUpdates, cancellationToken: lifetime.Token).ConfigureAwait(true);
                        if (!Model.Draft.Document.IsDashboard)
                        {
                            await RuntimeDiagnostics.RefreshAsync(Model.ClusterId!, observed.Peers, lifetime.Token).ConfigureAwait(true);
                            await Previews.RefreshAsync(Model.ClusterId!, Model.Draft.Document, observed.Peers, lifetime.Token).ConfigureAwait(true);
                            await ConcurrencyQueues.RefreshAsync(Model.ClusterId!, Model.PeerId!, Model.Draft.Document, lifetime.Token).ConfigureAwait(true);
                            await ToggleStates.RefreshAsync(Model.ClusterId!, observed.Coordinator ?? observed.PeerId, Model.Draft.Document, lifetime.Token).ConfigureAwait(true);
                        }
                    }
                    else
                    {
                        ToggleStates.Reset();
                        ConcurrencyQueues.Reset();
                    }
                    await GuardAsync(RefreshRunAsync).ConfigureAwait(true);
                    await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                }
                catch (Exception exception) when (!lifetime.IsCancellationRequested && exception is HttpRequestException or JsonException or InvalidOperationException or JSException or FormatException or OperationCanceledException or TimeoutException)
                {
                    message = Ui.Deferred("Editor status refresh failed. Your draft is retained; retrying.");
                    await InvokeAsync(StateHasChanged).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or JSException or FormatException or OperationCanceledException or TimeoutException)
        {
            message = exception is HttpRequestException ? Ui.Deferred("The response could not be confirmed. Look up recorded requests in operation history.")
                                                        : Ui.DeferredException(exception);
        }
    }

    /// <summary>草稿変更の購読を解除し、監視とローカル保存の終了を確認して資源を解放します。</summary>
    /// <returns>所有する処理と参照の解放完了です。</returns>
    // 抑制理由: BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "BlazorまたはIAsyncDisposableの固定署名を実装し、取消しは画面が所有するトークンで制御します。")]
    public override async ValueTask DisposeAsync()
    {
        try
        {
            Model.Changed -= Changed;
            Notices.Set("operation", null);
            Activity.Reset();
            await lifetime.CancelAsync().ConfigureAwait(true);
            await polling.ConfigureAwait(true);
            await persistence.ConfigureAwait(true);
        }
        finally
        {
            lifetime.Dispose();
            await base.DisposeAsync().ConfigureAwait(true);
            GC.SuppressFinalize(this);
        }
    }
}
