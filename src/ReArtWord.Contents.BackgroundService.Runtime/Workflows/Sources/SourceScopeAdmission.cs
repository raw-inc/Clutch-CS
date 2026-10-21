using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

/// <summary>開始時は所有元へ登録し、稼働中は範囲ごとの低頻度照会で終了通知の取りこぼしを補います。</summary>
internal sealed class SourceScopeAdmission(TimeProvider clock) : IAsyncDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SourceScope, Observation> observations = new();
    private readonly Lock gate = new();
    private readonly CancellationTokenSource stopping = new();
    private Task polling = Task.CompletedTask;
    private long? lastPoll;

    /// <summary>通常のSourceには照会を追加せず、範囲付きの開始だけを確認します。</summary>
    /// <returns>開始拒否、または許可された場合はnullです。</returns>
    internal static async ValueTask<NodeResult?> CheckStartAsync(ISourceScopeControl? control, RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SourceScopes is not { Count: > 0 } scopes)
        {
            return null;
        }
        if (control is null || 16 < scopes.Count || scopes.Any(scope => scope.Id == Guid.Empty || scope.PeerInstanceId == Guid.Empty
            || !string.Equals(scope.WorkflowId, request.WorkflowId, StringComparison.Ordinal)))
        {
            return NodeResult.Failure("source.scope.invalid", "The source lifetime scope is unavailable or invalid.", EOutcome.NotExecuted);
        }
        foreach (SourceScope scope in scopes)
        {
            bool? active = await control.CheckAsync(scope, true, cancellationToken).ConfigureAwait(false);
            if (active != true)
            {
                string reason = "source.scope.closed";
                if (active is null)
                {
                    reason = "source.scope.unconfirmed";
                }
                return NodeResult.Failure(reason, "The owner did not confirm that this source lifetime is still active.", EOutcome.NotExecuted);
            }
        }
        return null;
    }

    /// <summary>既存待受の範囲だけを保持し、終了確認済みと一時的な通信不能を区別します。</summary>
    /// <returns>正常終了を要求すべき範囲です。</returns>
    // 抑制理由: 遅い所有元照会を全WFの保存・自動起動から切り離し、一つのpollingを次の完了確認とDisposeAsyncでawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "一件だけのpollingを保持し、完了時またはDisposeAsyncで必ずawaitします。"),
     System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW141", Justification = "Taskの共有参照だけを読み、WaitHandleを作らず完了済みTaskをawaitします。共有Taskをここでは破棄しません。")]
    internal async ValueTask<IReadOnlySet<SourceScope>> EndedAsync(ISourceScopeControl? control, IEnumerable<SourceSession> sessions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<SourceScope> current = sessions.Where(static session => !session.Completion.IsCompleted)
            .SelectMany(static session => session.Request.SourceScopes ?? []).ToHashSet();
        foreach (SourceScope expired in observations.Keys.Where(scope => !current.Contains(scope)).ToArray())
        {
            observations.TryRemove(expired, out _);
        }
        HashSet<SourceScope> ended = [];
        if (control is null)
        {
            return ended;
        }
        Task finished = Task.CompletedTask;
        using (gate.EnterScope())
        {
            if (polling.IsCompleted && !stopping.IsCancellationRequested)
            {
                finished = polling;
                polling = Task.CompletedTask;
                long now = clock.GetTimestamp();
                if (current.Count != 0 && (lastPoll is null || TimeSpan.FromSeconds(2) <= clock.GetElapsedTime(lastPoll.Value, now)))
                {
                    lastPoll = now;
                    polling = ObserveAsync(control, current, stopping.Token);
                }
            }
        }
        await finished.ConfigureAwait(false);
        foreach (SourceScope scope in current)
        {
            if (observations.TryGetValue(scope, out Observation observed) && observed.Active == false)
            {
                ended.Add(scope);
            }
        }
        return ended;
    }

    /// <summary>生存照会の遅延を全体の反映処理へ伝えず、同時通信数を有限にします。</summary>
    /// <returns>現在の一組の観測だけを完了させる処理です。</returns>
    private Task ObserveAsync(ISourceScopeControl control, IReadOnlySet<SourceScope> current, CancellationToken cancellationToken = default)
        => Parallel.ForEachAsync(current, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (scope, token) =>
        {
            observations[scope] = new(await control.CheckAsync(scope, false, token).ConfigureAwait(false));
        });

    /// <summary>残っている一組の照会を取り消し、所有タスクを残しません。</summary>
    /// <returns>照会処理と取消資源の回収です。</returns>
    // 抑制理由: ロック内で取得した所有Taskを、ロック外のtry内で必ずawaitします。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW511", Justification = "ロック内で取得したpendingを直後のtry内でawaitします。")]
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        Task pending;
        using (gate.EnterScope())
        {
            pending = polling;
        }
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // 所有している照会の通常終了です。
        }
        stopping.Dispose();
    }

    /// <summary>元の待受IDを使い、同名で再開済みの別の回へ停止を波及させません。</summary>
    /// <returns>この範囲の元待受がすべて終了したかです。</returns>
    internal static async ValueTask<bool> EndAsync(SourceScope scope, IEnumerable<SourceSession> sessions,
        Func<SourceSession, CancellationToken, Task<NodeResult>> stop, CancellationToken cancellationToken = default)
    {
        // 登録直後で実Taskが設定される前のSourceも含めます。停止側は開始と同じ変更境界を通り、元のIDを再確認します。
        SourceSession[] owned = sessions.Where(session => session.Request.SourceScopes?.Contains(scope) == true).ToArray();
        bool[] stopped = await Task.WhenAll(owned.Select(async session =>
        {
            NodeResult result = await stop(session, cancellationToken).ConfigureAwait(false);
            return result.Outcome == EOutcome.Succeeded || session.Completion.IsCompleted;
        })).ConfigureAwait(false);
        return stopped.All(static result => result);
    }

    /// <summary>範囲一つにつき一件だけ保持する観測です。</summary>
    private readonly record struct Observation(bool? Active);
}
