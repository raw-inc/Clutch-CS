using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>グループ定義・所属・操作位置を、同じローカル媒体で比較保存します。</summary>
internal sealed class ProcessGroupStore(IDurableRepository repository, string peerId)
{
    private readonly string definitions = "peer:" + peerId + ":processGroups";
    private readonly string states = "peer:" + peerId + ":processGroupStates";
    private readonly string memberships = "peer:" + peerId + ":processGroupMembers";

    /// <summary>保存済みの全定義を現在版とともに読みます。</summary>
    /// <returns>削除済みを除いた定義です。</returns>
    internal async ValueTask<IReadOnlyList<(string Version, ProcessGroupDefinition Definition)>> ListAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DurableEntry> entries = await repository.ListAsync(definitions, cancellationToken).ConfigureAwait(false);
        return entries.Where(entry => entry.Value is not null).Select(entry =>
            (entry.Version, entry.Value!.Json.Deserialize<ProcessGroupDefinition>(ContractJson.Options) ?? throw new InvalidDataException("Invalid application group definition."))).ToArray();
    }

    /// <summary>未受付状態と保存済みの操作段階を区別して取得します。</summary>
    /// <returns>比較版と操作状態です。</returns>
    internal async ValueTask<(string? Version, ProcessGroupRun Run)> ReadRunAsync(string id, CancellationToken cancellationToken = default)
    {
        DurableEntry? entry = await repository.ReadAsync(states, id, cancellationToken).ConfigureAwait(false);
        ProcessGroupRun run = entry?.Value?.Json.Deserialize<ProcessGroupRun>(ContractJson.Options) ?? new();
        return (entry?.Version, run);
    }

    /// <summary>進行中の一回を期待版へ保存します。</summary>
    /// <returns>保存の確定結果です。</returns>
    internal async ValueTask<EOutcome> SaveRunAsync(string id, string? version, ProcessGroupRun run, CancellationToken cancellationToken = default)
        => (await repository.ApplyBatchAsync(Guid.NewGuid(), [new(states, id, version, NodeValue.FromObject(run))], cancellationToken).ConfigureAwait(false)).Outcome;

    /// <summary>所属を二つのグループで同時に取得できないよう、定義と同じ取引で予約します。</summary>
    /// <returns>定義・所属・削除時の操作状態をまとめた保存結果です。</returns>
    internal async ValueTask<EOutcome> RegisterAsync(ProcessGroupDefinition? definition, string id, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        DurableEntry? current = await repository.ReadAsync(definitions, id, cancellationToken).ConfigureAwait(false);
        ProcessGroupDefinition? previous = current?.Value?.Json.Deserialize<ProcessGroupDefinition>(ContractJson.Options);
        HashSet<string> next = (definition?.Members ?? []).Select(member => member.ApplicationId).ToHashSet(StringComparer.Ordinal);
        string[] applications = next.Concat((previous?.Members ?? []).Select(member => member.ApplicationId)).Distinct(StringComparer.Ordinal).ToArray();
        List<DurableMutation> mutations = [new(definitions, id, expectedVersion, definition is null ? null
                                                                                                    : NodeValue.FromObject(definition))];
        foreach (string application in applications)
        {
            DurableEntry? membership = await repository.ReadAsync(memberships, application, cancellationToken).ConfigureAwait(false);
            string? owner = membership?.Value?.GetString();
            if (owner is not null && !string.Equals(owner, id, StringComparison.Ordinal))
            {
                return EOutcome.NotExecuted;
            }
            mutations.Add(new(memberships, application, membership?.Version, next.Contains(application) ? NodeValue.FromObject(id)
                                                                                                        : null));
        }
        if (definition is null)
        {
            (string? version, _) = await ReadRunAsync(id, cancellationToken).ConfigureAwait(false);
            mutations.Add(new(states, id, version, null));
        }
        return (await repository.ApplyBatchAsync(Guid.NewGuid(), mutations, cancellationToken).ConfigureAwait(false)).Outcome;
    }
}
