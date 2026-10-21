using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.Operations.Snapshots;

/// <summary>スナップショットに含めるワークフロー・ノード・保存先を選択します。</summary>
// 抑制理由: Razor が生成する public partial の公開範囲を維持します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razor が生成する公開 partial 宣言と同じコンポーネント型を構成するため。")]
public sealed partial class SnapshotScopePicker
{
    private static string MatchPeer => "peer";
    private static string MatchHost => "host";
    private static string MatchGroup => "group";

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

    /// <summary>選択した対象を親画面へ通知します。</summary>
    [Parameter]
    public EventCallback<SnapshotScopeRequest> Selected
    {
        get;
        set;
    }

    private WorkflowView? selectedWorkflow;

    // 抑制理由: 取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW170", Justification = "取得したスナップショットの参照を交換し、子画面や開始済み処理が保持する旧コレクションを変更しないため。")]
    private IReadOnlyDictionary<string, IReadOnlyList<string>> groups = new Dictionary<string, IReadOnlyList<string>>();
    private string workflow = string.Empty, destination = "peer";
    private Func<string>? message = () => string.Empty;
    private bool allAssigned = true;
    private readonly HashSet<(string Kind, string Id)> chosen = [];
    private readonly HashSet<string> nodes = new(StringComparer.Ordinal);
    private IEnumerable<string> Destinations => destination switch
    {
        "host" => Connection.Current?.Peers.Select(peer => peer.HostId).Distinct() ?? [],
        "group" => groups.Keys,
        _ => Connection.Current?.Peers.Select(peer => peer.PeerId) ?? []
    };

    /// <summary>表示対象の一覧または状態を取得して画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task LoadAsync()
    {
        try
        {
            ClusterOperationalState? configuration = await Api.GetAsync<ClusterOperationalState>("/api/configuration", cancellationToken: UiCancellation).ConfigureAwait(true);
            groups = configuration?.Effective?.Settings.HostGroups ?? new Dictionary<string, IReadOnlyList<string>>();
            message = Ui.Deferred("Effective candidates were retrieved. Review the expanded targets before using them.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            message = Ui.Deferred("Effective workflows and shared groups could not be retrieved.");
        }
    }

    /// <summary>対象ワークフローを選択して範囲候補を取得します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectWorkflowAsync(WorkflowListSummary summary)
    {
        workflow = summary.Id!;
        nodes.Clear();
        selectedWorkflow = null;
        try
        {
            selectedWorkflow = await WorkflowReader.ReadAsync(Api, workflow, cancellationToken: UiCancellation).ConfigureAwait(true);
            if (selectedWorkflow?.Effective is null)
            {
                message = Ui.Deferred("The complete effective revision is unavailable. Individual node selection is disabled.{0}", selectedWorkflow?.DetailReason);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            message = Ui.Deferred("Effective node details could not be retrieved.");
        }
    }

    /// <summary>対象に含める保存先の選択を更新します。</summary>
    private void ChooseDestination(string id, bool selected)
    {
        if (selected)
        {
            chosen.Add((destination, id));
        }
        else
        {
            chosen.Remove((destination, id));
        }
    }

    /// <summary>対象に含めるノードの選択を更新します。</summary>
    private void ChooseNode(string id, bool selected)
    {
        if (selected)
        {
            nodes.Add(id);
        }
        else
        {
            nodes.Remove(id);
        }
    }

    /// <summary>指定した対象を選択し、関連する表示を更新します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SelectAsync()
    {
        string[]? ids = chosen.Where(item => string.Equals(item.Kind, destination, StringComparison.Ordinal)).Select(item => item.Id).ToArray();
        if (string.IsNullOrWhiteSpace(workflow) || ids.Length == 0 || !allAssigned && nodes.Count == 0)
        {
            message = Ui.Deferred("Specify a workflow, destinations, and selected nodes or assignment expansion.");
            return;
        }

        SnapshotScopeRequest? request = new SnapshotScopeRequest(workflow, string.Equals(destination, MatchPeer, StringComparison.Ordinal) ? ids
                                                                                                                                           : null, string.Equals(destination, MatchHost, StringComparison.Ordinal) ? ids
                                                                                                                                                                                                                   : null, string.Equals(destination, MatchGroup, StringComparison.Ordinal) ? ids
                                                                                                                                                                                                                                                                                            : null, allAssigned ? null
                                                                                                                                                                                                                                                                                                                : nodes.ToArray());
        await Selected.InvokeAsync(request).ConfigureAwait(true);
    }
}
