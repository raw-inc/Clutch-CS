using System.Text.Json;
using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>クラスタのPC一覧から選択し、未検出になった保存済みPCも編集可能に保ちます。</summary>
// 抑制理由: Razorが生成する公開宣言と、同じコンポーネント型を構成します。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開宣言に合わせるため。")]
public sealed partial class PcListField
{
    [Inject]
    private ConnectionState Connection
    {
        get; set;
    } = null!;

    [Inject]
    private ManagementApi Api
    {
        get; set;
    } = null!;

    [Inject]
    private EditorDocument Model
    {
        get; set;
    } = null!;

    /// <summary>選択条件を編集するノード。</summary>
    [Parameter]
    public string NodeId
    {
        get; set;
    } = string.Empty;

    /// <summary>保存されているPC識別子の配列。</summary>
    [Parameter]
    public JsonElement Value
    {
        get; set;
    }

    /// <summary>設定が接続値や編集保護によって変更できないか。</summary>
    [Parameter]
    public bool Disabled
    {
        get; set;
    }

    /// <summary>選択の変更を通常の設定編集へ渡します。</summary>
    [Parameter]
    public EventCallback<JsonElement> ValueChanged
    {
        get; set;
    }

    private bool open;
    private string? cluster;
    private int generation;
    private string message = string.Empty;
    private bool CanEdit => !Disabled && generation == Model.Generation && string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal)
        && !Model.Locks.Node(Model.Draft.Document.Id, NodeId);
    private HashSet<string> Selected
    {
        get
        {
            if (Value.ValueKind == JsonValueKind.Array)
            {
                return Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            }
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>選択先クラスタと編集世代を固定して一覧を開きます。</summary>
    /// <returns>一覧の更新完了。</returns>
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cluster = Api.SelectedClusterId;
        generation = Model.Generation;
        open = true;
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>明示操作時だけPC一覧を更新し、接続不能時は保存値を残します。</summary>
    /// <returns>一覧の取得と状態表示の更新完了。</returns>
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Connection.RefreshAsync(cancellationToken).ConfigureAwait(true);
        message = Connection.Connected ? "Select PCs in the current cluster. Missing saved PCs remain listed."
                                       : "Offline: saved selections are retained. PC availability is unconfirmed.";
    }

    /// <summary>同じクラスタ・編集世代の選択だけを反映します。</summary>
    /// <returns>一回の設定編集としての反映完了。</returns>
    private async Task ChangeAsync(string host, bool selected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanEdit)
        {
            return;
        }
        HashSet<string> hosts = Selected;
        if (selected)
        {
            hosts.Add(host);
        }
        else
        {
            hosts.Remove(host);
        }
        await ValueChanged.InvokeAsync(JsonSerializer.SerializeToElement(hosts.Order(StringComparer.Ordinal), ContractJson.Options)).ConfigureAwait(true);
    }

    /// <summary>同じPCの複数Peerを一行にまとめ、保存済みの未検出PCも残します。</summary>
    /// <returns>識別子と現在確認できた状態。</returns>
    private (string Id, string Status)[] Choices()
    {
        Dictionary<string, string> observed = Connection.Current?.Peers.GroupBy(peer => peer.HostId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(peer => Contracts.Cluster.PeerStatusRules.CanParticipate(peer.Status)).First().Status.ToString(), StringComparer.Ordinal) ?? [];
        foreach (string host in Selected)
        {
            observed.TryAdd(host, "Not observed");
        }
        return observed.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)).ToArray();
    }
}
