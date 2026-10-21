using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor;
using ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Pages.Overview;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Pages;

/// <summary>対象を固定した保存・実行・管理操作を要求します。</summary>
public sealed partial class WorkflowEditorPage
{
    private ConfigurationImpactPreview? activationImpact;
    private string? pendingRunCluster;
    private bool forceSaveOpen;

    /// <summary>強制保存の確認結果を通常の操作表示と保存一覧へ反映します。</summary>
    /// <returns>画面の更新です。</returns>
    private async Task ForceSavedAsync(WorkflowSaveResult result, CancellationToken cancellationToken = default)
    {
        message = () => Ui.Format("Operation {0} · Saved: {1} / Shared: {2} / Snapshot: {3} / Active: {4}", result.OperationId,
            Ui[result.Saved.Outcome.ToString()], Ui[result.Shared.Outcome.ToString()], Ui[result.Snapshot.Outcome.ToString()], Ui[result.Activation.Outcome.ToString()]);
        if (result.Saved.Outcome == EOutcome.Succeeded && result.Shared.Outcome == EOutcome.Succeeded && string.Equals(result.Revision, Model.Draft.BaseRevision, StringComparison.Ordinal))
        {
            await EditorSavedCache.WriteAsync(Storage, Model.ClusterId, Model.PeerId, new StoredWorkflow(Model.Draft.Base, result.Revision!, DateTimeOffset.UtcNow, activation), cancellationToken).ConfigureAwait(true);
            await RefreshListAsync().ConfigureAwait(true);
        }
    }

    /// <summary>入力内容の保存を要求し、現時点の結果を画面へ反映します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task SaveAsync(IReadOnlyDictionary<string, EEditResolution>? resolutions = null)
    {
        if (busy)
        {
            return;
        }

        int generation = Model.Generation;
        await GuardAsync(async () =>
        {
            if (invalid || Model.HasInvalidInputs)
            {
                throw new InvalidOperationException(Ui["Correct the invalid input values before continuing."]);
            }

            busy = true;
            try
            {
                await Collaboration.WaitForIdleAsync(lifetime.Token).ConfigureAwait(true);
                if (!LocalContext)
                {
                    await Collaboration.SynchronizeAsync(true, Connection.Current?.PeerId ?? "browser", lifetime.Token).ConfigureAwait(true);
                    Collaboration.EnsureConfirmed();
                }

                if (Model.Draft.LocalOnly && !Detached)
                {
                    throw new InvalidOperationException(Ui["Review and reconcile local changes in Dashboard, then reload the saved version."]);
                }

                if (generation != Model.Generation)
                {
                    throw new InvalidOperationException(Ui["The selected workflow changed. Nothing was saved."]);
                }

                EditorDraft? draft = Model.Draft;
                string? checkedCluster = Model.ClusterId;
                await Recipes.SaveDependenciesAsync(draft.Document, lifetime.Token).ConfigureAwait(true);
                Guid id = Guid.NewGuid();
                WorkflowSaveResult? result;
                WorkflowDefinition definition = draft.Document;
                WorkflowListSummary? current = draft.BaseRevision is null ? null
                                                                          : await WorkflowReader.ReadSummaryAsync(Api, definition.Id, lifetime.Token).ConfigureAwait(true);
                if (draft.BaseRevision is not null && !string.Equals(current?.SavedRevision, draft.BaseRevision, StringComparison.Ordinal))
                {
                    ValidationView? check = await Api.PostAsync<ValidationView>("/api/workflows/validate", draft.Document, lifetime.Token).ConfigureAwait(true);
                    if (check is not null && string.Equals(checkedCluster, Model.ClusterId, StringComparison.Ordinal))
                    {
                        Diagnostics.SetValidation(checkedCluster, draft.Document, Model.Draft.Document, check.Issues);
                    }
                    if (check?.Valid != true)
                    {
                        message = () => string.Join("\n", check?.Issues.Select(issue => Ui[issue.Message]) ?? []);
                        return;
                    }

                    WorkflowMergeRequest? request = new WorkflowMergeRequest(id, draft.BaseRevision, definition, resolutions ?? new Dictionary<string, EEditResolution>(), activation);
                    JsonElement response = await Journal.StartAsync(id, Ui["Resolve workflow save conflict"], "/api/workflows/merge", "/api/workflows/operations/" + id, request, cancellationToken: lifetime.Token).ConfigureAwait(true);
                    WorkflowMergeResult? merged = response.Deserialize<WorkflowMergeResult>(ContractJson.Options);
                    if (merged?.Conflicts.Count != 0)
                    {
                        conflicts = merged?.Conflicts ?? [];
                        saveConflict = true;
                        conflictOpen = true;
                        return;
                    }

                    result = merged?.Save;
                    definition = merged?.Candidate ?? definition;
                }
                else
                {
                    WorkflowSaveRequest? request = new WorkflowSaveRequest(id, definition, draft.BaseRevision, activation);
                    JsonElement response = await Journal.StartAsync(id, Ui["Save workflow"], "/api/workflows/save", "/api/workflows/operations/" + id, request, cancellationToken: lifetime.Token).ConfigureAwait(true);
                    result = response.Deserialize<WorkflowSaveResult>(ContractJson.Options);
                }

                // 通常保存ではCoreの保存時検証を採用し、同じ定義の事前検証・転送を重ねません。
                if (result is not null && string.Equals(checkedCluster, Model.ClusterId, StringComparison.Ordinal))
                {
                    Diagnostics.SetValidation(checkedCluster, definition, Model.Draft.Document, result.Validation);
                    if (result.Validation.Count != 0)
                    {
                        message = () => string.Join("\n", result.Validation.Select(issue => Ui[issue.Message]));
                        return;
                    }
                }

                message = () => Ui.Format("Operation {0} · Saved: {1} / Shared: {2} / Snapshot: {3} / Active: {4}", id, Ui[result?.Saved.Outcome.ToString() ?? "Unconfirmed"], Ui[result?.Shared.Outcome.ToString() ?? "Unconfirmed"], Ui[result?.Snapshot.Outcome.ToString() ?? "Unconfirmed"], Ui[result?.Activation.Outcome.ToString() ?? "Unconfirmed"]) + (result?.LocalOnly == true ? " · " + Ui["Saved locally · not shared"]
                                                                                                                                                                                                                                                                                                                                                                                          : string.Empty)
                    + BeforeSnapshotMessage(result);
                if (result?.Saved.Outcome == EOutcome.Succeeded && (result.Shared.Outcome == EOutcome.Succeeded || result.LocalOnly && result.Snapshot.Outcome == EOutcome.Succeeded))
                {
                    if (generation != Model.Generation)
                    {
                        return;
                    }

                    Model.Saved(result.Revision!, definition, result.LocalOnly);
                    await EditorSavedCache.WriteAsync(Storage, Api.SelectedClusterId, Api.SelectedPeerId, new StoredWorkflow(definition, result.Revision!, DateTimeOffset.UtcNow, activation, result.LocalOnly), lifetime.Token).ConfigureAwait(true);
                    bool changesCatalog = SubworkflowInterface.IsSubworkflow(definition) || SubworkflowInterface.IsSubworkflow(draft.Base)
                        || definition.Nodes.Any(node => SubworkflowInterface.Target(node.Type) is not null);
                    await RefreshListAsync(changesCatalog).ConfigureAwait(true);
                }
            }
            finally
            {
                busy = false;
            }
        }).ConfigureAwait(true);
    }

    /// <summary>現在の草稿を先頭から一回実行する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RunAsync() => RunCoreAsync(null);

    /// <summary>選択ノードを開始点にした実行を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private Task RunSelectedAsync()
    {
        documentToolsOpen = false;
        return RunCoreAsync(Model.SelectedNode);
    }

    /// <summary>草稿と開始点を固定して一回の実行を記録・要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RunCoreAsync(string? startNode) => await GuardAsync(async () =>
    {
        if (Model.Draft.Dirty || Model.Draft.BaseRevision is null || Model.HasInvalidInputs)
        {
            throw new InvalidOperationException(Ui["Save and activate the workflow before running it."]);
        }

        int generation = Model.Generation;
        string workflow = Model.Draft.Document.Id;
        string revision = Model.Draft.BaseRevision;
        string? cluster = Model.ClusterId, peer = Model.PeerId;
        WorkflowDefinition document = Model.Draft.Document;
        WorkflowView? current = await WorkflowReader.ReadAsync(Api, workflow, lifetime.Token).ConfigureAwait(true);
        if (generation != Model.Generation || !ReferenceEquals(document, Model.Draft.Document) || Model.HasInvalidInputs
            || !string.Equals(cluster, Model.ClusterId, StringComparison.Ordinal) || !string.Equals(peer, Model.PeerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(Ui["The selected workflow changed. Nothing was started."]);
        }
        if (!string.Equals(current?.EffectiveRevision, revision, StringComparison.Ordinal))
        {
            activationOpen = true;
            return;
        }

        IReadOnlyDictionary<string, NodeValue>? inputs = null;
        bool toggle = document.Nodes.FirstOrDefault(node => string.Equals(node.Id, startNode, StringComparison.Ordinal))?.Type is "control.toggleButton";
        if (toggle)
        {
            inputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["trigger"] = NodeValue.FromObject(true) };
            ToggleStates.Invalidate(startNode!);
        }
        WorkflowRunRequest? request = new WorkflowRunRequest(Guid.NewGuid(), workflow, revision, startNode, inputs);
        pendingRunCluster = cluster;
        JsonElement response = await Journal.StartAsync(request.OperationId, Ui["Manual run"], "/api/runs", "/api/runs/" + request.OperationId, request, targetPeer: peer, targetCluster: cluster, cancellationToken: lifetime.Token).ConfigureAwait(true);
        WorkflowRunStatus? result = response.Deserialize<WorkflowRunStatus>(ContractJson.Options);
        pendingRun = result?.Running == true ? Journal.Items.First(item => item.OperationId == request.OperationId)
                                             : null;
        if (result is not null)
        {
            Diagnostics.SetRun(pendingRunCluster, result);
        }
        message = () => RunMessage(request.OperationId, result);
    }).ConfigureAwait(true);

    /// <summary>実行要求の現時点の結果を通知文へ変換します。</summary>
    /// <returns>同じ操作IDの実行中・完了・未確認を区別した通知です。</returns>
    private string RunMessage(Guid id, WorkflowRunStatus? result) => result?.Running == true ? Ui.Format("Running · {0}", id)
                                                                                             : Ui.Format("Run: {0} · {1}", Ui[result?.Outcome.ToString() ?? "Unconfirmed"], id);

    /// <summary>未確認の実行を元の操作IDで再照会します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task RefreshRunAsync()
    {
        if (pendingRun is not { } operation)
        {
            return;
        }

        await Journal.ReadAsync(operation, lifetime.Token).ConfigureAwait(true);
        WorkflowRunStatus? result = Journal.Items.First(item => item.OperationId == operation.OperationId).Result?.Deserialize<WorkflowRunStatus>(ContractJson.Options);
        if (result is not null)
        {
            Diagnostics.SetRun(pendingRunCluster, result);
        }
        message = () => RunMessage(operation.OperationId, result);
        if (result?.Running != true)
        {
            pendingRun = null;
        }
    }

    /// <summary>保存した版を将来の入力へ反映する操作を要求します。</summary>
    /// <returns>要求処理と現時点の画面反映の完了です。</returns>
    // 抑制理由: UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "UIコールバックの呼出し署名を維持し、取消可能なI/Oには画面が所有するトークンを渡します。")]
    private async Task ActivateAsync() => await GuardAsync(async () =>
    {
        if (activationImpact is null || !string.Equals(activationImpact.TargetId, Model.Draft.Document.Id, StringComparison.Ordinal)
            || !string.Equals(activationImpact.SavedRevision, Model.Draft.BaseRevision, StringComparison.Ordinal))
        {
            return;
        }
        ConfigurationPhaseResult? result = await Api.PostAsync<ConfigurationPhaseResult>("/api/workflows/activate", new ActivationRequest(Guid.NewGuid(), Model.Draft.Document.Id, Model.Draft.BaseRevision!, true, activationImpact.Token), lifetime.Token).ConfigureAwait(true);
        message = () => Ui.Format("Activation: {0} · {1}", Ui[result?.Outcome.ToString() ?? "Unconfirmed"], result?.Reason);
        activationOpen = false;
    }).ConfigureAwait(true);

    /// <summary>変更前保護を保存後の取得結果と分けて表示します。</summary>
    /// <returns>保護が不要なら空、取得した場合はその確認結果です。</returns>
    private string BeforeSnapshotMessage(WorkflowSaveResult? result)
    {
        if (result?.BeforeSnapshot is not { } before)
        {
            return string.Empty;
        }
        return " · " + Ui.Format("Before-change snapshot: {0}", Ui[before.Outcome.ToString()]);
    }
}
