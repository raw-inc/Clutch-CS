using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Editing;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration;

/// <summary>保存とスナップショット操作で再送時の段階結果を共用します。</summary>
internal sealed class ConfigurationOperationJournal(IDurableRepository repository)
{
    /// <summary>明示反映の元要求と結果を保持し、再送で比較・反映をやり直しません。</summary>
    /// <returns>元の結果、または元IDで照会する必要がある確認不能です。</returns>
    internal async Task<ConfigurationPhaseResult> ActivateAsync(ActivationRequest request, Func<ActivationRequest, CancellationToken, Task<ConfigurationPhaseResult>> apply,
        CancellationToken cancellationToken = default)
    {
        string kind = "workflow-activate";
        try
        {
            ConfigurationOperation? existing = await ReadAsync(kind, request.OperationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.Fingerprint, ContractJson.Hash(request), StringComparison.Ordinal))
                {
                    return new(EOutcome.NotExecuted, "Activation.OperationMismatch");
                }
                return existing.Result?.Deserialize<ConfigurationPhaseResult>() ?? new(EOutcome.Unknown, "Activation.InProgress");
            }
            if (!await BeginAsync(kind, request.OperationId, request, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return new(EOutcome.Unknown, "Activation.BeginUnconfirmed");
            }
            ConfigurationPhaseResult result = await apply(request, cancellationToken).ConfigureAwait(false);
            await CompleteAsync(kind, request.OperationId, result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Data.Common.DbException or OperationCanceledException)
        {
            return new(EOutcome.Unknown, "Activation.ResultUnconfirmed");
        }
    }

    /// <summary>保存または離脱統合の元結果を読みます。</summary>
    /// <returns>保存段階の記録です。</returns>
    public async Task<WorkflowSaveResult?> ReadSaveAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation? operation = await ReadAsync("save", operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            operation = await ReadAsync("detached.resolve", operationId, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                return null;
            }
            return operation.Result?.Deserialize<WorkflowMergeResult>()?.Save ?? WorkflowStore.UnknownSave(operationId, EOutcome.Unknown, "Detached.OriginalResolutionUnknown");
        }
        return operation.Result?.Deserialize<WorkflowSaveResult>() ?? WorkflowStore.UnknownSave(operationId, EOutcome.Unknown, "Workflow.OperationIncomplete");
    }

    /// <summary>実施済み操作の条件と結果を読みます。</summary>
    /// <returns>操作記録。</returns>
    public async Task<ConfigurationOperation?> ReadAsync(string kind, Guid id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync("cluster:configuration-operations:" + kind, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<ConfigurationOperation>();
    }

    /// <summary>一度だけ操作開始と固定条件を保存します。</summary>
    /// <returns>開始記録を作成できたか。</returns>
    public async Task<bool> BeginAsync<T>(string kind, Guid id, T request, JsonElement? context = null, CancellationToken cancellationToken = default)
    {
        ConfigurationOperation operation = new(ContractJson.Hash(request), DateTimeOffset.UtcNow, context, null);
        DurableReceipt receipt = await repository.ApplyBatchAsync(WorkflowStore.PhaseId(id, kind + ".begin"), [new DurableMutation("cluster:configuration-operations:" + kind, id.ToString("N"), null, NodeValue.FromObject(operation))], cancellationToken).ConfigureAwait(false);
        return receipt.Outcome == EOutcome.Succeeded;
    }

    /// <summary>操作の既知結果を開始記録と同じ項目へ保存します。</summary>
    /// <returns>記録完了。</returns>
    public async Task CompleteAsync<T>(string kind, Guid id, T result, CancellationToken cancellationToken = default)
    {
        string scope = "cluster:configuration-operations:" + kind;
        DurableEntry? entry = await repository.ReadAsync(scope, id.ToString("N"), cancellationToken).ConfigureAwait(false);
        ConfigurationOperation? operation = entry?.Value?.Json.Deserialize<ConfigurationOperation>();
        if (operation is null)
        {
            throw new InvalidDataException("操作の開始記録がありません。");
        }
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(scope, id.ToString("N"), entry!.Version, NodeValue.FromObject(operation with { Result = JsonSerializer.SerializeToElement(result) }))], cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            throw new InvalidDataException("操作結果を保存できません。");
        }
    }
}
