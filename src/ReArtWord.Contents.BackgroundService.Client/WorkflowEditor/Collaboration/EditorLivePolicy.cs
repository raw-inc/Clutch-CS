using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Collaboration;

/// <summary>ブラウザー固有の表示選択とは分けて、クラスタ共通の反映方針を扱います。</summary>
internal sealed class EditorLivePolicy(ManagementApi api, OperationJournal journal)
{
    private string? cluster, peer, workflow;
    private DateTimeOffset nextRead;
    private bool observedEnabled;

    /// <summary>保存候補と実有効版です。</summary>
    internal ClusterOperationalState? State
    {
        get; private set;
    }

    /// <summary>既定は明示反映です。確認した有効版だけを表示します。</summary>
    internal bool Enabled => string.Equals(cluster, api.SelectedClusterId, StringComparison.Ordinal) && observedEnabled;

    /// <summary>自動保存の元操作と段階結果です。</summary>
    internal WorkflowSaveResult? Result
    {
        get; private set;
    }

    /// <summary>結果を表示するWF識別子です。</summary>
    internal string? ResultWorkflow => workflow;

    /// <summary>クラスタ共通設定を保存中かです。</summary>
    internal bool Busy
    {
        get; private set;
    }

    /// <summary>別の未反映設定を、このチェック操作で一緒に適用しません。</summary>
    internal bool CanChange => string.Equals(cluster, api.SelectedClusterId, StringComparison.Ordinal) && State?.Saved is not null && string.Equals(State.Saved.Revision, State.Effective?.Revision, StringComparison.Ordinal);

    /// <summary>同じクラスタのCoordinatorから低頻度で確認します。</summary>
    /// <returns>共有設定と最新の自動反映結果です。</returns>
    internal async Task RefreshAsync(string clusterId, string peerId, string workflowId, bool readSettings = true, bool? knownEnabled = null, CancellationToken cancellationToken = default)
    {
        bool same = string.Equals(cluster, clusterId, StringComparison.Ordinal) && string.Equals(peer, peerId, StringComparison.Ordinal)
            && string.Equals(workflow, workflowId, StringComparison.Ordinal);
        if (knownEnabled is bool enabled)
        {
            observedEnabled = enabled;
        }
        if (Busy || same && api.UtcNow < nextRead && (!readSettings || State is not null))
        {
            return;
        }
        if (!same)
        {
            State = null;
            Result = null;
        }
        cluster = clusterId;
        peer = peerId;
        workflow = workflowId;
        nextRead = api.UtcNow.AddSeconds(3);
        if (readSettings)
        {
            ClusterOperationalState? state = await api.GetForPeerAsync<ClusterOperationalState>("/api/configuration", peerId, clusterId, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(api.SelectedClusterId, clusterId, StringComparison.Ordinal))
            {
                return;
            }
            State = state;
            observedEnabled = state?.Effective?.Settings.LiveWorkflowUpdates == true;
        }
        if (Enabled)
        {
            Result = await api.GetForPeerAsync<WorkflowSaveResult>("/api/collaboration/" + Uri.EscapeDataString(workflowId) + "/apply-result", peerId, clusterId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>他項目を保持した一回のCAS保存を記録し、不明な要求は再送しません。</summary>
    /// <returns>共有保存の段階結果です。</returns>
    internal async Task<ClusterOperationalSaveResult?> ChangeAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (Busy || cluster is null || peer is null || !string.Equals(cluster, api.SelectedClusterId, StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The connection or draft changed. Review the overwrite again.");
        }
        string targetCluster = cluster, targetPeer = peer;
        Busy = true;
        try
        {
            ClusterOperationalState? state = await api.GetForPeerAsync<ClusterOperationalState>("/api/configuration", targetPeer, targetCluster, cancellationToken).ConfigureAwait(false);
            if (state?.Saved is null || !string.Equals(state.Saved.Revision, state.Effective?.Revision, StringComparison.Ordinal))
            {
                throw UiError.InvalidOperation("Apply or resolve pending cluster settings before changing live workflow updates.");
            }
            if (!string.Equals(targetCluster, api.SelectedClusterId, StringComparison.Ordinal))
            {
                throw UiError.InvalidOperation("The connection or draft changed. Review the overwrite again.");
            }
            Guid operation = Guid.NewGuid();
            ClusterOperationalSaveRequest request = new(operation, state.Saved.Settings with { LiveWorkflowUpdates = enabled }, state.Saved.Revision);
            JsonElement reply = await journal.StartAsync(operation, "Save workflow editing policy", "/api/configuration/save", "/api/configuration/operations/" + operation,
                request, targetPeer: targetPeer, targetCluster: targetCluster, cancellationToken: cancellationToken).ConfigureAwait(false);
            ClusterOperationalSaveResult? result = reply.Deserialize<ClusterOperationalSaveResult>(ContractJson.Options);
            State = await api.GetForPeerAsync<ClusterOperationalState>("/api/configuration", targetPeer, targetCluster, cancellationToken).ConfigureAwait(false);
            observedEnabled = State?.Effective?.Settings.LiveWorkflowUpdates == true;
            nextRead = default;
            return result;
        }
        finally
        {
            Busy = false;
        }
    }
}
