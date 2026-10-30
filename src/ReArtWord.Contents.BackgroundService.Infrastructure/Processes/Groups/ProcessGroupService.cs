using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>Peerローカルのアプリグループの登録・操作・照会を提供します。</summary>
public sealed class ProcessGroupService : IDisposable
{
    private readonly SemaphoreSlim catalog = new(1, 1);
    private readonly ConcurrentDictionary<string, ProcessGroupSession> sessions = new(StringComparer.Ordinal);
    private readonly List<ProcessGroupSession> retired = [];
    private readonly ProcessGroupStore store;
    private readonly ProcessService processes;
    private readonly ProcessGroupOwnership ownership;
    private readonly TimeProvider clock;
    private readonly string peerId;
    private readonly IWorkflowLog? log;
    private volatile bool initialized;
    private ApplicationSnapshot snapshot = new(-1, []);

    /// <summary>プロセスサービスと同じ保存先・所有者・時計を使用します。</summary>
    internal ProcessGroupService(IDurableRepository repository, string peerId, ProcessService processes, ProcessGroupOwnership ownership, TimeProvider clock, IWorkflowLog? log)
    {
        store = new(repository, peerId);
        this.processes = processes;
        this.ownership = ownership;
        this.clock = clock;
        this.peerId = peerId;
        this.log = log;
    }

    /// <summary>設定と現在の操作状態を返します。</summary>
    /// <returns>このPCに登録されたグループです。</returns>
    // 抑制理由: 設定編集では全グループの所属を同じ時点の一覧として参照します。
    [SuppressMessage("SourceQuality", "RAW160", Justification = "列挙途中に登録変更を混ぜない、有限の設定スナップショットを返します。")]
    public async ValueTask<IReadOnlyList<RegisteredProcessGroup>> ListAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return sessions.Values.Select(session => session.Registration).OrderBy(entry => entry.Definition.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>管理対象の所属を確認してグループを登録します。</summary>
    /// <returns>期待版に対する登録結果です。</returns>
    public async ValueTask<EOutcome> RegisterAsync(ProcessGroupDefinition definition, string? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await catalog.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<RegisteredProcess> registrations = await processes.ListAsync(cancellationToken).ConfigureAwait(false);
            if (ProcessGroupRules.Validate(definition, registrations) is not null)
            {
                return EOutcome.NotExecuted;
            }
            ProcessGroupDefinition owned = ContractJson.Copy(definition);
            if (sessions.TryGetValue(definition.Id, out ProcessGroupSession? existing))
            {
                return expectedVersion is null ? EOutcome.NotExecuted
                                               : await existing.UpdateAsync(owned, expectedVersion, cancellationToken).ConfigureAwait(false);
            }
            EOutcome result = await store.RegisterAsync(owned, owned.Id, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (result == EOutcome.Succeeded)
            {
                (string version, ProcessGroupDefinition saved) = (await store.ListAsync(cancellationToken).ConfigureAwait(false)).Single(item => string.Equals(item.Definition.Id, definition.Id, StringComparison.Ordinal));
                await AddAsync(version, saved, cancellationToken).ConfigureAwait(false);
            }
            return result;
        }
        finally
        {
            catalog.Release();
        }
    }

    /// <summary>所属と設定を削除し、実行中のアプリはそのまま維持します。</summary>
    /// <returns>削除の確定結果です。</returns>
    public async ValueTask<EOutcome> RemoveAsync(string id, string expectedVersion, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await catalog.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sessions.TryGetValue(id, out ProcessGroupSession? session))
            {
                return EOutcome.NotExecuted;
            }
            EOutcome result = await session.UpdateAsync(null, expectedVersion, cancellationToken).ConfigureAwait(false);
            if (result == EOutcome.Succeeded)
            {
                sessions.TryRemove(id, out _);
                retired.Add(session);
            }
            return result;
        }
        finally
        {
            catalog.Release();
        }
    }

    /// <summary>一回の操作を受け付けます。進行と完了は一覧から確認できます。</summary>
    /// <returns>受付結果と操作識別子です。</returns>
    public async ValueTask<ProcessGroupOperationResult> CommandAsync(ProcessGroupCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return sessions.TryGetValue(request.Id, out ProcessGroupSession? session) ? await session.CommandAsync(request.Command, request.ExpectedVersion, cancellationToken).ConfigureAwait(false)
                                                                                  : new(EOutcome.NotExecuted, Reason: "processGroup.notRegistered");
    }

    /// <summary>単体自動復旧へ二重に渡さない登録かです。</summary>
    /// <returns>グループが自動復旧を所有する場合はtrueです。</returns>
    internal bool Managed(string id) => ownership.Managed(id);

    /// <summary>単体の副作用が始まる前に、保存されたグループ所有状態を読み込みます。</summary>
    /// <returns>初回の読取り完了です。</returns>
    internal async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }
        await catalog.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!initialized)
            {
                foreach ((string version, ProcessGroupDefinition definition) in await store.ListAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!sessions.ContainsKey(definition.Id))
                    {
                        await AddAsync(version, definition, cancellationToken).ConfigureAwait(false);
                    }
                }
                initialized = true;
            }
        }
        finally
        {
            catalog.Release();
        }
    }

    /// <summary>登録変更時だけ一覧を読み、監視対象の固定スナップショットを返します。</summary>
    /// <returns>現在のグループとアプリ登録です。</returns>
    internal async ValueTask<(IReadOnlyList<ProcessGroupSession> Sessions, IReadOnlyList<RegisteredProcess> Applications)> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        long revision = processes.RegistrationRevision;
        if (revision != snapshot.Revision)
        {
            snapshot = new(revision, await processes.ListAsync(cancellationToken).ConfigureAwait(false));
        }
        return (sessions.Values.ToArray(), snapshot.Applications);
    }

    /// <summary>全ての監視タスクが終了してから排他資源を破棄します。</summary>
    public void Dispose()
    {
        foreach (ProcessGroupSession session in sessions.Values.Concat(retired))
        {
            session.Dispose();
        }
        catalog.Dispose();
    }

    /// <summary>保存済みの一登録を監視所有者へ接続します。</summary>
    /// <returns>操作状態を読んでからの登録完了です。</returns>
    private async ValueTask AddAsync(string version, ProcessGroupDefinition definition, CancellationToken cancellationToken = default)
    {
        (string? stateVersion, ProcessGroupRun run) = await store.ReadRunAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        sessions[definition.Id] = new(store, processes, ownership, clock, peerId, log, version, definition, stateVersion, run);
    }

    /// <summary>登録版とその不変一覧を一緒に保持します。</summary>
    private sealed record ApplicationSnapshot(long Revision, IReadOnlyList<RegisteredProcess> Applications);
}
