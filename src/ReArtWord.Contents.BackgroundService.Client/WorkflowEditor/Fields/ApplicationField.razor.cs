using Microsoft.AspNetCore.Components;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Fields;

/// <summary>実行先ホストに登録されたアプリケーションを論理名で検索し、安定IDだけを保存します。</summary>
// 抑制理由: Razorが生成する公開コンポーネントと同じ宣言です。
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515", Justification = "Razorが生成する公開コンポーネントです。")]
public sealed partial class ApplicationField
{
    [Inject]
    private ManagementApi Api
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
    private SettingChoiceProvider Choices
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

    /// <summary>設定対象のノードです。</summary>
    [Parameter]
    public string NodeId
    {
        get;
        set;
    } = string.Empty;

    /// <summary>保存済みの登録識別子です。</summary>
    [Parameter]
    public string Value
    {
        get;
        set;
    } = string.Empty;

    /// <summary>接続値や編集ロックによる変更禁止です。</summary>
    [Parameter]
    public bool Disabled
    {
        get;
        set;
    }

    /// <summary>選択した識別子だけを設定欄へ返します。</summary>
    [Parameter]
    public EventCallback<string> ValueChanged
    {
        get;
        set;
    }

    private readonly List<ApplicationChoice> candidates = [];
    private bool open, busy;
    private int generation;
    private string? cluster, targetKey, problem;
    private string search = string.Empty;

    private bool CanEdit => !Disabled && generation == Model.Generation && string.Equals(cluster, Api.SelectedClusterId, StringComparison.Ordinal)
        && string.Equals(targetKey, string.Join("|", Targets().Select(peer => peer.PeerId)), StringComparison.Ordinal) && !Model.Locks.Node(Model.Draft.Document.Id, NodeId);

    /// <summary>固定実行先を優先し、動的指定の場合は候補ホストだけを一覧にします。</summary>
    /// <returns>観測できる登録所有Peerです。</returns>
    private PeerInfo[] Targets()
    {
        NodeDefinition? node = Model.Draft.Document.Nodes.FirstOrDefault(item => string.Equals(item.Id, NodeId, StringComparison.Ordinal));
        if (node is null || !Connection.Connected)
        {
            return [];
        }
        IReadOnlyList<PeerInfo> peers = Connection.Current?.Peers ?? [];
        string? peer = Choices.ResolvePeer(node);
        if (peer is not null)
        {
            return peers.Where(item => string.Equals(item.PeerId, peer, StringComparison.Ordinal)).ToArray();
        }
        IReadOnlyList<string> hosts = Choices.NetworkHosts(node);
        return peers.Where(item => hosts.Contains(item.HostId, StringComparer.Ordinal)).OrderBy(item => item.PeerId, StringComparer.Ordinal).ToArray();
    }

    /// <summary>選択操作時だけ登録を照会し、ノード描画ごとの全ホスト照会を避けます。</summary>
    /// <returns>取得できた論理名と所有先です。</returns>
    private async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cluster = Api.SelectedClusterId;
        generation = Model.Generation;
        PeerInfo[] targets = Targets();
        targetKey = string.Join("|", targets.Select(peer => peer.PeerId));
        candidates.Clear();
        search = string.Empty;
        open = true;
        busy = true;
        problem = null;
        try
        {
            foreach (PeerInfo target in targets)
            {
                try
                {
                    List<ProcessView>? values = await Api.GetForPeerAsync<List<ProcessView>>("/api/processes", target.PeerId, cluster, cancellationToken).ConfigureAwait(true);
                    if (CanEdit && values is not null)
                    {
                        candidates.AddRange(values.Select(item => new ApplicationChoice(item.Definition.Id, item.Definition.DisplayName, target.HostId, target.PeerId)));
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    problem = "Some application registrations could not be verified. The saved selection is retained.";
                }
            }
            if (targets.Length == 0)
            {
                problem = "Select an execution host to choose its registered applications.";
            }
        }
        finally
        {
            busy = false;
        }
    }

    /// <summary>取得中に別WFや別ホストへ移動した場合は古い候補を反映しません。</summary>
    /// <returns>現在の編集先への設定反映です。</returns>
    private async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CanEdit)
        {
            await ValueChanged.InvokeAsync(id).ConfigureAwait(true);
            open = false;
        }
    }

    /// <summary>画面に必要な名前と所有先だけを保持します。</summary>
    private sealed record ApplicationChoice(string Id, string Name, string Host, string Peer);
}
