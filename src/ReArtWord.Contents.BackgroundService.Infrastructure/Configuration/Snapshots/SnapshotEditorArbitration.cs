using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>各Peerの一時編集を確認し、復元する設定範囲の確定を支援します。</summary>
internal sealed class SnapshotEditorArbitration
{
    private readonly PeerCluster cluster;
    private readonly SnapshotEndpoint endpoint;
    private readonly SnapshotEditProtection protection;
    private readonly IDurableRepository repository;

    /// <summary>確認は非永続照会、取消は既存の要求操作記録に登録します。</summary>
    public SnapshotEditorArbitration(PeerCluster cluster, SnapshotEndpoint endpoint, SnapshotEditProtection protection, IDurableRepository repository)
    {
        this.cluster = cluster;
        this.endpoint = endpoint;
        this.protection = protection;
        this.repository = repository;
        cluster.RegisterInspection("snapshot.editor.inspect", InspectLocalAsync);
        cluster.RegisterHandler("snapshot.editor.resolve", ResolveLocalAsync);
    }

    /// <summary>未保存編集の実装を接続します。</summary>
    public ISnapshotEditor? Editor
    {
        get; set;
    }

    /// <summary>元要求の範囲を各通常参加Peerの編集状態と照合します。</summary>
    /// <returns>開始前の確認結果です。</returns>
    public async Task<SnapshotRestorePreparation> PrepareAsync(SnapshotRestoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = ContractJson.Copy(request);
        SnapshotTarget[] targets = request.Selections.Where(selection => !selection.Excluded).Select(selection => selection.Target).ToArray();
        SnapshotEditLock[] scopes = Scopes(request);
        List<SnapshotEditorState> editors = [];
        string? reason = null;
        foreach (PeerInfo peer in cluster.GetPeers().Where(peer => peer.Status != EPeerStatus.Detached))
        {
            if (!PeerStatusRules.CanParticipate(peer.Status))
            {
                reason = "Snapshot.EditorPeerUnavailable";
                continue;
            }
            foreach (SnapshotEditLock scope in scopes.Where(scope => !scope.IsConfigurationParticipant))
            {
                ClusterRequest command = new(Guid.NewGuid(), "snapshot.editor.inspect", peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddMinutes(1), NodeValue.FromObject(scope));
                ClusterResponse response = await cluster.InspectAsync(command, cancellationToken).ConfigureAwait(false);
                SnapshotEditorState? state = response.Value?.Json.Deserialize<SnapshotEditorState>(ContractJson.Options);
                if (response.Outcome != EOutcome.Succeeded || state is null)
                {
                    reason = "Snapshot.EditorStateUnknown";
                    continue;
                }
                editors.Add(state);
                if (state.Dirty || state.Presence.Any(item => item.Presence.Dirty))
                {
                    reason = "Snapshot.UnsavedEdits";
                }
            }
        }
        IReadOnlyList<SnapshotEditLock> locks = await protection.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (locks.Any(item => item.OperationId != request.OperationId && scopes.Any(scope => scope.IsConfigurationParticipant == item.IsConfigurationParticipant && string.Equals(scope.WorkflowId, item.WorkflowId, StringComparison.Ordinal))))
        {
            reason = "Snapshot.ScopeLocked";
        }
        return new SnapshotRestorePreparation(request.OperationId, ContractJson.Hash(request), targets, editors, locks, reason is null, reason);
    }

    /// <summary>指定Peerと元要求を固定して調停を配送します。</summary>
    /// <returns>受理結果です。</returns>
    public async Task<ConfigurationPhaseResult> ResolveAsync(SnapshotDraftResolution request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PeerInfo? peer = cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, request.PeerId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status));
        if (peer is null)
        {
            return new ConfigurationPhaseResult(EOutcome.NotExecuted, "Snapshot.EditorPeerUnavailable");
        }
        Guid id = WorkflowStore.PhaseId(request.Request.OperationId, "draft:" + ContractJson.Hash(request));
        ClusterRequest command = new(id, "snapshot.editor.resolve", peer.PeerId, peer.InstanceId, DateTimeOffset.UtcNow.AddMinutes(5), NodeValue.FromObject(request));
        string scope = "peer:" + cluster.LocalPeerId + ":snapshot-draft-requests";
        DurableEntry? original = await repository.ReadAsync(scope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        if (original is null)
        {
            DurableReceipt saved = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(id, "request"), [new DurableMutation(scope, id.ToString("N"), null, NodeValue.FromObject(command))], cancellationToken).ConfigureAwait(false);
            if (saved.Outcome != EOutcome.Succeeded)
            {
                return new ConfigurationPhaseResult(saved.Outcome, saved.Reason);
            }
        }
        else
        {
            command = original.Value!.Json.Deserialize<ClusterRequest>(ContractJson.Options)!;
        }
        ClusterResponse response = await cluster.RequestAsync(command, cancellationToken).ConfigureAwait(false);
        return new ConfigurationPhaseResult(response.Outcome, response.Reason);
    }

    /// <summary>同ワークフローの部分範囲を合併し全体指定があれば全体を保ちます。</summary>
    /// <returns>編集確認範囲です。</returns>
    private SnapshotEditLock[] Scopes(SnapshotRestoreRequest request) => request.Selections.Where(selection => !selection.Excluded).Select(selection => endpoint.EditLock(request.OperationId, selection.Target)).OfType<SnapshotEditLock>().GroupBy(item => (item.WorkflowId, item.IsConfigurationParticipant)).Select(group => new SnapshotEditLock(request.OperationId, group.Key.WorkflowId, Union(group), group.Key.IsConfigurationParticipant)).ToArray();

    /// <summary>全体指定を空集合へ置換しません。</summary>
    /// <returns>合併した部分ノードまたは全体のnullです。</returns>
    private static string[]? Union(IEnumerable<SnapshotEditLock> scopes)
    {
        SnapshotEditLock[] values = scopes.ToArray();
        if (values.Any(item => item.NodeIds is null))
        {
            return null;
        }
        return values.SelectMany(item => item.NodeIds!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>照会では下書きを変更しません。</summary>
    /// <returns>ローカル編集状態です。</returns>
    private async Task<ClusterResponse> InspectLocalAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotEditLock scope = request.Payload.Json.Deserialize<SnapshotEditLock>(ContractJson.Options)!;
        SnapshotEditorState state = new(cluster.LocalPeerId, scope.WorkflowId, scope.NodeIds, null, false, []);
        if (Editor is not null)
        {
            state = await Editor.InspectAsync(scope, cancellationToken).ConfigureAwait(false);
        }
        return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(state));
    }

    /// <summary>元要求外のワークフローを取消対象へ加えません。</summary>
    /// <returns>ローカル調停の実行結果です。</returns>
    private async Task<ClusterResponse> ResolveLocalAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotDraftResolution resolution = request.Payload.Json.Deserialize<SnapshotDraftResolution>(ContractJson.Options)!;
        SnapshotEditLock? scope = Scopes(resolution.Request).FirstOrDefault(item => !item.IsConfigurationParticipant && string.Equals(item.WorkflowId, resolution.WorkflowId, StringComparison.Ordinal));
        if (Editor is null || scope is null || !string.Equals(resolution.PeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.EditorScopeInvalid");
        }
        ConfigurationPhaseResult result = await Editor.ResolveAsync(resolution, scope, cancellationToken).ConfigureAwait(false);
        return new ClusterResponse(result.Outcome, Reason: result.Reason);
    }
}
