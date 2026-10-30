using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes;

/// <summary>アプリケーション登録と故障状態をPeerローカルへ比較保存します。</summary>
internal sealed class ProcessRegistry
{
    private readonly IDurableRepository repository;
    private readonly string definitionsScope;
    private readonly string statesScope;

    /// <summary>既存共通リポジトリのローカル用途を使用します。</summary>
    internal ProcessRegistry(IDurableRepository repository, string peerId, Func<SystemBootSession?>? boot = null)
    {
        this.repository = repository;
        definitionsScope = "peer:" + peerId + ":processDefinitions";
        statesScope = "peer:" + peerId + ":processStates";
        Schedules = new(repository, "peer:" + peerId + ":processSchedules", boot ?? SystemBootObservation.Read);
    }

    /// <summary>予定の受付位置を同じローカル保存先で管理します。</summary>
    internal ProcessScheduleStore Schedules
    {
        get;
    }

    /// <summary>公開編集用の版付き登録一覧を取得します。</summary>
    /// <returns>現在値が存在する登録一覧です。</returns>
    internal async ValueTask<IReadOnlyList<RegisteredProcess>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync(definitionsScope, cancellationToken).ConfigureAwait(false);
        return entries.Where(entry => entry.Value is not null).Select(entry => new RegisteredProcess(entry.Version, entry.Value!.Json.Deserialize<ProcessDefinition>(ContractJson.Options)!)).ToArray();
    }

    /// <summary>未登録アプリケーションへの操作を防ぐため現在定義を解決します。</summary>
    /// <returns>登録済みの定義、または未登録のnullです。</returns>
    internal async ValueTask<ProcessDefinition?> DefinitionAsync(string id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(definitionsScope, id, cancellationToken).ConfigureAwait(false);
        return entry?.Value?.Json.Deserialize<ProcessDefinition>(ContractJson.Options);
    }

    /// <summary>未開始の予定へ古い登録設定を使用しないため、排他内で確認します。</summary>
    /// <returns>現在も同じ登録版である場合はtrueです。</returns>
    internal async ValueTask<bool> MatchesAsync(string id, string version, CancellationToken cancellationToken = default)
        => string.Equals((await repository.ReadAsync(definitionsScope, id, cancellationToken).ConfigureAwait(false))?.Version, version, StringComparison.Ordinal);

    /// <summary>確認済み登録版だけを変更します。</summary>
    /// <returns>保存の実行結果です。</returns>
    internal async ValueTask<EOutcome> RegisterAsync(ProcessDefinition definition, string? expectedVersion, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        List<DurableMutation> mutations = [new DurableMutation(definitionsScope, definition.Id, expectedVersion, Encode(definition))];
        ProcessDefinition? previous = await DefinitionAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        mutations.AddRange(await Schedules.RegistrationAsync(definition, previous, now, cancellationToken).ConfigureAwait(false));
        if (definition.ExistingProcess is not null && definition.ExistingProcess != previous?.ExistingProcess)
        {
            (string? stateVersion, ProcessStateRecord state) = await ReadStateAsync(definition, cancellationToken).ConfigureAwait(false);
            ProcessStateRecord changed = state with { InstanceId = Guid.NewGuid(), Identity = definition.ExistingProcess, LaunchPending = false,
                Ready = null, Healthy = null, ExitCounted = false, ExitCode = null, ExitedAt = null, NextRecoveryAt = null, ExitUnobserved = false, Replacement = null, ReplacementProblem = null, RecoveryReplacement = null };
            mutations.Add(new DurableMutation(statesScope, definition.Id, stateVersion, Encode(changed)));
        }
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false);
        return receipt.Outcome;
    }

    /// <summary>登録・監視状態・起動予定の受付位置を同じ比較保存で削除します。</summary>
    /// <returns>確認した登録版に対する削除結果です。</returns>
    internal async ValueTask<EOutcome> RemoveAsync(string id, string expectedVersion, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ProcessDefinition? previous = await DefinitionAsync(id, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return EOutcome.NotExecuted;
        }
        List<DurableMutation> mutations = [new(definitionsScope, id, expectedVersion, null)];
        DurableEntry? state = await repository.ReadAsync(statesScope, id, cancellationToken).ConfigureAwait(false);
        if (state?.Value is not null)
        {
            mutations.Add(new(statesScope, id, state.Version, null));
        }
        mutations.AddRange(await Schedules.RegistrationAsync(previous with { Schedules = [] }, previous, now, cancellationToken).ConfigureAwait(false));
        return (await repository.ApplyBatchAsync(Guid.CreateVersion7(), mutations, cancellationToken).ConfigureAwait(false)).Outcome;
    }

    /// <summary>故障状態と比較保存用の版を読みます。</summary>
    /// <returns>保存済み状態です。未作成時は既存プロセス登録から初期化します。</returns>
    internal async ValueTask<(string? Version, ProcessStateRecord State)> ReadStateAsync(ProcessDefinition definition, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(statesScope, definition.Id, cancellationToken).ConfigureAwait(false);
        ProcessStateRecord? state = entry?.Value?.Json.Deserialize<ProcessStateRecord>(ContractJson.Options);
        return (entry?.Version, state ?? new ProcessStateRecord(Guid.NewGuid(), definition.ExistingProcess));
    }

    /// <summary>副作用の前後で状態の保存を確認します。</summary>
    /// <returns>成功以外を暗黙に再試行しない保存結果です。</returns>
    internal async ValueTask<EOutcome> SaveStateAsync(string id, string? expectedVersion, ProcessStateRecord state, CancellationToken cancellationToken = default)
    {
        DurableReceipt receipt = await repository.ApplyBatchAsync(Guid.CreateVersion7(), [new DurableMutation(statesScope, id, expectedVersion, Encode(state))], cancellationToken).ConfigureAwait(false);
        return receipt.Outcome;
    }

    /// <summary>公開契約と同じJSON規則で保存値を作ります。</summary>
    /// <returns>所有権を分離した保存値です。</returns>
    private static NodeValue Encode<T>(T value)
    {
        return NodeValue.FromJson(JsonSerializer.SerializeToElement(value, ContractJson.Options));
    }
}
