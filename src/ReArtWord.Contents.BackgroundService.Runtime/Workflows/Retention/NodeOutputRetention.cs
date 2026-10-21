using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Runtime.State;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Retention;

/// <summary>開始信号で更新する最新値を共有保存し、遅い旧実行で新しい結果を上書きしません。</summary>
internal sealed class NodeOutputRetention(INodeStateStore? store, NodeOutputRetentionQueue queue)
{
    /// <summary>新しい信号の受理を一世代として保存します。</summary>
    /// <returns>保存した世代。保存不能ならnull、保存先未設定の組込み利用では空IDです。</returns>
    internal async Task<Generation?> BeginAsync(CompiledWorkflow workflow, string node, CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return new(Guid.Empty, null);
        }
        Guid generation = Guid.NewGuid();
        using IDisposable lease = await queue.EnterAsync(workflow.Definition.Id, node, cancellationToken).ConfigureAwait(false);
        VersionedState? old = await store.ReadAsync(NodeStateScope.Shared(workflow.Definition.Id), Key(node), cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            RetainedOutput[] entries = [.. Read(old).Where(entry => !string.Equals(entry.Revision, workflow.Revision, StringComparison.Ordinal)).TakeLast(3), new(workflow.Revision, generation, null)];
            StateWriteResult written = await store.CompareExchangeAsync(NodeStateScope.Shared(workflow.Definition.Id), Key(node), old?.Version,
                ContractJson.ToValue(entries), cancellationToken).ConfigureAwait(false);
            if (written.Applied)
            {
                return new(generation, written.Current);
            }
            if (!StateWriteConflicts.IsConflict(written))
            {
                return null;
            }
            old = written.Current;
        }
        return null;
    }

    /// <summary>同じ世代が最新の場合だけ結果を固定し、別信号の結果を待ち直しません。</summary>
    /// <returns>保存または新世代への交代を確認できたかです。</returns>
    internal async Task<bool> CompleteAsync(CompiledWorkflow workflow, string node, Generation generation, ExecutionFrame result, CancellationToken cancellationToken = default)
    {
        if (store is null || generation.Id == Guid.Empty)
        {
            return true;
        }
        long bytes = result.Targets.Sum(target => target.Result.Outputs.Values.Sum(value => value.PayloadSize()));
        ExecutionFrame retained = result;
        if (1048576 < bytes)
        {
            retained = new(node, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("value.retentionLimit", "The completed output exceeds the retained-value limit.", EOutcome.NotExecuted))]);
        }

        // 開始保存の確定版から比較します。遅延したローカル複製を「新世代への交代」と誤認しません。
        using IDisposable lease = await queue.EnterAsync(workflow.Definition.Id, node, cancellationToken).ConfigureAwait(false);
        VersionedState? old = generation.State;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            RetainedOutput[] entries = Read(old);
            if (!entries.Any(entry => entry.Generation == generation.Id && string.Equals(entry.Revision, workflow.Revision, StringComparison.Ordinal)))
            {
                return true;
            }
            StateWriteResult written = await store.CompareExchangeAsync(NodeStateScope.Shared(workflow.Definition.Id), Key(node), old!.Version,
                ContractJson.ToValue(entries.Select(entry => entry.Generation == generation.Id ? entry with { Result = retained }
                                                                                               : entry).ToArray()), cancellationToken).ConfigureAwait(false);
            if (written.Applied)
            {
                return true;
            }
            if (!StateWriteConflicts.IsConflict(written))
            {
                return false;
            }
            old = written.Current;
        }
        return false;
    }

    /// <summary>他の入力事象からも同じ設定版の最後の結果を読み、処理中は値を供給しません。</summary>
    /// <returns>再実行を伴わない値、または未確定です。</returns>
    internal async Task<ExecutionFrame?> GetAsync(CompiledWorkflow workflow, string node, CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return null;
        }
        RetainedOutput? retained;
        try
        {
            retained = Read(await store.ReadAsync(NodeStateScope.Shared(workflow.Definition.Id), Key(node), cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(entry => string.Equals(entry.Revision, workflow.Revision, StringComparison.Ordinal));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Data.Common.DbException)
        {
            return new(node, EOutcome.NotExecuted, [new(string.Empty, NodeResult.Failure("value.retentionUnavailable", "The retained value could not be read. The operation was not repeated.", EOutcome.NotExecuted))], ConfigurationOnly: true, RetainedValue: true);
        }
        if (retained?.Result is null)
        {
            return null;
        }
        if (!string.Equals(retained.Result.NodeId, node, StringComparison.Ordinal) || retained.Result.Targets is null
            || retained.Result.Targets.Any(target => target?.Result?.Outputs is null))
        {
            return null;
        }
        return retained.Result with { ConfigurationOnly = true, RetainedValue = true };
    }

    /// <summary>利用者の通常キーと衝突しにくい、ノード一つにつき一つの固定キーです。</summary>
    /// <returns>版ごとにキーを増やさない内部用途名です。</returns>
    private static string Key(string node) => "runtime:node-output:" + node;

    /// <summary>保存値の形を確認し、不正な値を成功結果として使いません。</summary>
    /// <returns>直近4版までの状態です。別版の遅延結果で現在版を置換しません。</returns>
    private static RetainedOutput[] Read(VersionedState? state)
    {
        try
        {
            if (state?.Value.Kind == EValueKind.Json && state.Value.Json.ValueKind == JsonValueKind.Array && state.Value.Json.GetArrayLength() <= 4)
            {
                RetainedOutput[] entries = state.Value.Json.Deserialize<RetainedOutput[]>(ContractJson.Options) ?? [];
                return entries.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Revision) && entry.Generation != Guid.Empty).ToArray();
            }
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>処理中はResultがnullで、確定結果と開始世代を分離します。</summary>
    private sealed record RetainedOutput(string Revision, Guid Generation, ExecutionFrame? Result);

    /// <summary>開始保存の確認済み版を完了更新へ引き継ぎます。</summary>
    internal sealed record Generation(Guid Id, VersionedState? State);
}
