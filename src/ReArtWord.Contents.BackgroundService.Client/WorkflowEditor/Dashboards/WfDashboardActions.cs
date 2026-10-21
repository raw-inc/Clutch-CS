using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Management;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Dashboards;

/// <summary>カード操作を元WFの保存・実行APIへ一度だけ渡します。</summary>
internal sealed class WfDashboardActions(ManagementApi api, OperationJournal journal)
{
    /// <summary>表示した保存版と有効版が一致するボタンだけを一回押します。</summary>
    /// <returns>実行の受付結果です。完了確認とは区別します。</returns>
    internal async Task<WorkflowRunStatus> PressAsync(string cluster, string peer, WfDashboardNodeState state, CancellationToken cancellationToken = default)
    {
        EnsureTarget(cluster, peer);
        string[] buttons = ["control.button", "control.toggleButton"];
        if (!state.Active || state.Revision is null || state.Problem is not null || !buttons.Contains(state.Reference.NodeType, StringComparer.Ordinal))
        {
            throw UiError.InvalidOperation("Save and activate the source workflow before using this control.");
        }
        IReadOnlyDictionary<string, NodeValue>? inputs = null;
        string toggle = "control.toggleButton";
        if (string.Equals(state.Reference.NodeType, toggle, StringComparison.Ordinal))
        {
            inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["trigger"] = NodeValue.FromObject(true) };
        }
        WorkflowRunRequest request = new(Guid.NewGuid(), state.Reference.WorkflowId, state.Revision, state.Reference.NodeId, inputs);
        JsonElement response = await journal.StartAsync(request.OperationId, "WFDashboard button", "/api/runs", "/api/runs/" + request.OperationId,
            request, targetPeer: peer, targetCluster: cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
        return response.Deserialize<WorkflowRunStatus>(ContractJson.Options) ?? throw UiError.InvalidOperation("The control response could not be confirmed. Check operation history before trying again.");
    }

    /// <summary>入力を固定型で検証し、同じ元WF版を条件とする通常保存へ渡します。</summary>
    /// <returns>保存・共有・反映の確認結果です。</returns>
    internal async Task<WorkflowSaveResult> SaveValueAsync(string cluster, string peer, WfDashboardNodeState state, string revision, string text, CancellationToken cancellationToken = default)
    {
        EnsureTarget(cluster, peer);
        WfDashboardValueRequest edit = new(Guid.NewGuid(), state.Reference, revision, text);
        WfDashboardValuePreparation prepared = await api.PostForPeerAsync<WfDashboardValuePreparation>("/api/dashboards/value-prepare", edit, peer, cluster, cancellationToken: cancellationToken).ConfigureAwait(true)
            ?? throw UiError.InvalidOperation("The source workflow could not be checked. Nothing was saved.");
        EnsureTarget(cluster, peer);
        if (prepared.Request is null)
        {
            string message = "The Value could not be validated.";
            if (prepared.Issues.Count != 0)
            {
                message = prepared.Issues[0].Message;
            }
            throw UiError.InvalidOperation(message);
        }
        if (prepared.Request.OperationId != edit.OperationId || !string.Equals(prepared.Request.Definition.Id, state.Reference.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(prepared.Request.ExpectedRevision, revision, StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The source workflow changed. Nothing was saved.");
        }
        JsonElement response = await journal.StartAsync(edit.OperationId, "WFDashboard Value", "/api/workflows/save", "/api/workflows/operations/" + edit.OperationId,
            prepared.Request, targetPeer: peer, targetCluster: cluster, cancellationToken: cancellationToken).ConfigureAwait(true);
        return response.Deserialize<WorkflowSaveResult>(ContractJson.Options) ?? throw UiError.InvalidOperation("The save response could not be confirmed. Check operation history before trying again.");
    }

    /// <summary>画面が切り替わった後の入力を別クラスタ・Peerへ送信しません。</summary>
    private void EnsureTarget(string cluster, string peer)
    {
        if (!string.Equals(api.SelectedClusterId, cluster, StringComparison.Ordinal) || !string.Equals(api.SelectedPeerId, peer, StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The connection changed. Nothing was sent.");
        }
    }
}
