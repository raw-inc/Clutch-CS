using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Client.Localization;
using ReArtWord.Contents.BackgroundService.Client.Management;
using ReArtWord.Contents.BackgroundService.Client.Operations.Journal;
using ReArtWord.Contents.BackgroundService.Client.Services;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Client.WorkflowEditor.Documents;

/// <summary>共有草稿の照合を迂回し、確認した最新保存版へ手元の定義を一度だけ保存します。</summary>
internal sealed class EditorForceSave(EditorDocument model, ManagementApi api, BrowserStorage storage, OperationJournal journal)
{
    /// <summary>退避する草稿と、上書き確認した保存版・接続先です。</summary>
    internal sealed record Preview(EditorDraft Draft, StoredWorkflow? Saved, string? Cluster, string? Peer, int Generation,
        IReadOnlyList<ConfigurationDifference> Differences, bool Truncated);

    /// <summary>利用者が確認する固定した上書き対象です。</summary>
    internal Preview? Review
    {
        get; private set;
    }

    /// <summary>結果不明でも再送せず照会する元操作IDです。</summary>
    internal Guid? OperationId
    {
        get; private set;
    }

    /// <summary>保存・共有・反映を区別する確認結果です。</summary>
    internal WorkflowSaveResult? Result
    {
        get; private set;
    }

    /// <summary>編集内容と最新保存版を固定します。共同編集の成立を前提にしません。</summary>
    /// <returns>上書き前の実差分です。</returns>
    internal async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (OperationId is null)
        {
            await journal.LoadAsync(cancellationToken).ConfigureAwait(false);
            string unknown = "unknown", kind = "Force save workflow";
            OperationRecord? pending = journal.Items.FirstOrDefault(item => string.Equals(item.Kind, kind, StringComparison.Ordinal)
                && string.Equals(item.ClusterId, api.SelectedClusterId, StringComparison.Ordinal) && string.Equals(item.PeerId, api.SelectedPeerId, StringComparison.Ordinal)
                && string.Equals(OperationJournal.Outcome(item.Result), unknown, StringComparison.Ordinal)
                && string.Equals(item.Request.Deserialize<WorkflowSaveRequest>(ContractJson.Options)?.Definition.Id, model.Draft.Document.Id, StringComparison.Ordinal));
            OperationId = pending?.OperationId;
        }
        if (OperationId is not null && (Result is null || Result.Saved.Outcome == EOutcome.Unknown || Result.Shared.Outcome == EOutcome.Unknown
            || Result.Snapshot.Outcome == EOutcome.Unknown || Result.Activation.Outcome == EOutcome.Unknown))
        {
            throw UiError.InvalidOperation("Check the original force-save operation before starting another save.");
        }
        Review = null;
        if (model.HasInvalidInputs || model.Draft.LocalOnly)
        {
            throw UiError.InvalidOperation("Correct invalid inputs and reconcile detached local changes before force saving.");
        }
        EditorDraft draft = ContractJson.Copy(model.Draft);
        string? cluster = api.SelectedClusterId, peer = api.SelectedPeerId;
        int generation = model.Generation;
        StoredWorkflow? current = await ReadAsync(draft.Document.Id, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<ConfigurationDifference> differences, bool truncated) = ConfigurationDifferences.Compare(current?.Definition, draft.Document, hideValues: true);
        Preview preview = new(draft, current, cluster, peer, generation, differences, truncated);
        EnsureCurrent(preview);
        Review = preview;
        OperationId = null;
        Result = null;
    }

    /// <summary>比較した草稿を退避し、最新保存版のCASと通常検証を通して一回だけ送信します。</summary>
    /// <returns>元の保存要求の確認結果です。</returns>
    internal async Task<WorkflowSaveResult?> SaveAsync(EActivationPolicy activation, CancellationToken cancellationToken = default)
    {
        Preview preview = Review ?? throw UiError.InvalidOperation("Review the latest saved version before force saving.");
        if (OperationId is not null)
        {
            throw UiError.InvalidOperation("Check the original force-save operation before starting another save.");
        }
        EnsureCurrent(preview);
        StoredWorkflow? current = await ReadAsync(preview.Draft.Document.Id, cancellationToken).ConfigureAwait(false);
        EnsureCurrent(preview);
        if (!string.Equals(current?.Revision, preview.Saved?.Revision, StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The saved version changed. Review the overwrite again.");
        }
        ValidationView? validation = await api.PostForPeerAsync<ValidationView>("/api/workflows/validate", preview.Draft.Document, preview.Peer, preview.Cluster, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (validation?.Valid != true)
        {
            throw UiError.InvalidOperation("The draft is invalid. Force saving does not bypass workflow validation.");
        }
        EnsureCurrent(preview);
        Guid operation = Guid.NewGuid();
        await storage.WriteAsync("contents.blazor.force-save.backup", new { operation, preview.Cluster, preview.Peer, preview.Draft, preview.Saved }, cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureCurrent(preview);
        OperationId = operation;
        WorkflowSaveRequest request = new(operation, preview.Draft.Document, preview.Saved?.Revision, activation);
        JsonElement response = await journal.StartAsync(operation, "Force save workflow", "/api/workflows/save", "/api/workflows/operations/" + operation,
            request, targetPeer: preview.Peer, targetCluster: preview.Cluster, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Accept(response);
    }

    /// <summary>不明な要求を作り直さず、元操作の結果だけを照会します。</summary>
    /// <returns>確認済みの保存結果です。</returns>
    internal async Task<WorkflowSaveResult?> LookupAsync(CancellationToken cancellationToken = default)
    {
        if (OperationId is not Guid operation)
        {
            return null;
        }
        OperationRecord record = journal.Items.Single(item => item.OperationId == operation);
        int generation = model.Generation;
        bool reconciled = Result?.Saved.Outcome == EOutcome.Succeeded && Result.Shared.Outcome == EOutcome.Succeeded;
        await journal.ReadAsync(record, cancellationToken).ConfigureAwait(false);
        WorkflowSaveResult? result = journal.LastResult is { } response ? Accept(response)
                                                                        : null;
        if (!reconciled && Review is null && generation == model.Generation && result?.Saved.Outcome == EOutcome.Succeeded && result.Shared.Outcome == EOutcome.Succeeded
            && result.Revision is { } revision && string.Equals(record.ClusterId, model.ClusterId, StringComparison.Ordinal)
            && string.Equals(record.PeerId, model.PeerId, StringComparison.Ordinal) && string.Equals(record.ClusterId, api.SelectedClusterId, StringComparison.Ordinal)
            && string.Equals(record.PeerId, api.SelectedPeerId, StringComparison.Ordinal))
        {
            WorkflowSaveRequest saved = record.Request.Deserialize<WorkflowSaveRequest>(ContractJson.Options)!;
            if (string.Equals(saved.Definition.Id, model.Draft.Document.Id, StringComparison.Ordinal))
            {
                model.Saved(revision, saved.Definition, force: true);
            }
        }
        return result;
    }

    /// <summary>別画面の草稿や送信後の追加編集を、保存した古い本文へ戻しません。</summary>
    /// <returns>保存応答です。</returns>
    private WorkflowSaveResult? Accept(JsonElement response)
    {
        Result = response.Deserialize<WorkflowSaveResult>(ContractJson.Options);
        if (Result is null || Result.OperationId != OperationId)
        {
            throw UiError.InvalidOperation("The original operation ID does not match the request body.");
        }
        Preview? preview = Review;
        if (preview is not null && SameTarget(preview) && Result.Saved.Outcome == EOutcome.Succeeded && Result.Shared.Outcome == EOutcome.Succeeded && Result.Revision is { } revision)
        {
            model.Saved(revision, preview.Draft.Document, force: true);
        }
        return Result;
    }

    /// <summary>確認した接続先・草稿が変われば再確認を求めます。</summary>
    private void EnsureCurrent(Preview preview)
    {
        if (!SameTarget(preview) || model.HasInvalidInputs || model.Draft.LocalOnly
            || !string.Equals(ContractJson.Hash(model.Draft.Document), ContractJson.Hash(preview.Draft.Document), StringComparison.Ordinal))
        {
            throw UiError.InvalidOperation("The connection or draft changed. Review the overwrite again.");
        }
    }

    /// <summary>接続先切替後に遅延応答を適用しません。</summary>
    /// <returns>確認した表示対象かです。</returns>
    private bool SameTarget(Preview preview) => preview.Generation == model.Generation
        && string.Equals(preview.Cluster, api.SelectedClusterId, StringComparison.Ordinal)
        && string.Equals(preview.Peer, api.SelectedPeerId, StringComparison.Ordinal)
        && string.Equals(preview.Cluster, model.ClusterId, StringComparison.Ordinal)
        && string.Equals(preview.Peer, model.PeerId, StringComparison.Ordinal)
        && string.Equals(preview.Draft.Document.Id, model.Draft.Document.Id, StringComparison.Ordinal);

    /// <summary>新規の404と、不明な接続・本文取得失敗を区別します。</summary>
    /// <returns>確認した保存版、またはまだ保存されていない場合はnullです。</returns>
    private async Task<StoredWorkflow?> ReadAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await WorkflowReader.ReadAsync(api, id, cancellationToken).ConfigureAwait(false)).RequireSaved();
        }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
