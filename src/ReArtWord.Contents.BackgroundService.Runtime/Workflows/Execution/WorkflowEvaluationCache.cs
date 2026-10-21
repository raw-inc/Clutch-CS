using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>同じ一件の再実行を防ぎつつ、終了した反復要素の中間値を保持し続けません。</summary>
internal sealed class WorkflowEvaluationCache
{
    /// <summary>要素とその子処理が全て終了した時点を、進捗の寿命へ通知します。</summary>
    internal Action<Guid>? Released
    {
        get; set;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Scope> scopes = [];
    private readonly HashSet<Guid> completed = [];
    private int valueCount;
    private int signalCount;
    private long bytes;

    /// <summary>同じ範囲・入力の一回だけを公開し、上限超過では副作用を開始しません。</summary>
    /// <returns>初回または既存の実行です。受付上限ではnullです。</returns>
    internal Lazy<Task<ExecutionFrame>>? GetOrAdd(Guid id, string key, bool signal, Lazy<Task<ExecutionFrame>> proposed)
    {
        using (gate.EnterScope())
        {
            Scope scope = Find(id);
            Dictionary<string, Lazy<Task<ExecutionFrame>>> values = signal ? scope.Signals
                                                                           : scope.Values;
            if (values.TryGetValue(key, out Lazy<Task<ExecutionFrame>>? known))
            {
                return known;
            }
            int count = signal ? signalCount
                               : valueCount;
            if (16384 <= count)
            {
                return null;
            }
            values.Add(key, proposed);
            if (signal)
            {
                signalCount++;
            }
            else
            {
                valueCount++;
            }
            return proposed;
        }
    }

    /// <summary>今も利用する値だけを保持上限へ数え、確定した副作用を保持失敗から再実行しません。</summary>
    /// <returns>実成否と、保持できる範囲の出力です。</returns>
    internal ExecutionFrame Retain(Guid id, ExecutionFrame result)
    {
        long size = result.Targets.Sum(target => target.Result.Outputs.Values.Sum(value => value.PayloadSize()));
        using (gate.EnterScope())
        {
            Scope scope = Find(id);
            if (size <= 67108864 - bytes)
            {
                bytes += size;
                scope.Bytes += size;
                return result;
            }
        }
        return result with { Targets = result.Targets.Select(target => target with { Result = target.Result with
        {
            Outputs = new Dictionary<string, NodeValue>(StringComparer.Ordinal), ReasonCode = target.Result.ReasonCode ?? "value.shareLimit",
            Message = "The operation finished, but its output exceeded the execution's shared-value limit. The operation will not be repeated.",
        } }).ToArray() };
    }

    /// <summary>反復から発火した子実行が参照している間は、その要素の値を維持します。</summary>
    internal void Child(Guid id, bool started, EOutcome outcome)
    {
        if (id == Guid.Empty)
        {
            return;
        }
        using (gate.EnterScope())
        {
            Scope scope = Find(id);
            if (started)
            {
                scope.Children++;
            }
            else
            {
                scope.Children--;
                scope.Outcome = ExecutionOutcomes.Aggregate([scope.Outcome, outcome]);
                if (scope.Children < 0)
                {
                    throw new InvalidOperationException("反復要素の参照寿命が一致しません。");
                }
                Release(id, scope);
            }
        }
    }

    /// <summary>ボディの全タスクが終わった要素を閉じ、子側の終了後に保持枠も戻します。</summary>
    internal void Complete(Guid id)
    {
        if (id == Guid.Empty)
        {
            return;
        }
        using (gate.EnterScope())
        {
            Scope scope = Find(id);
            scope.Finished = true;
            Release(id, scope);
        }
    }

    /// <summary>入力が同じでも別要素へ結果を混ぜず、終了範囲からの再評価を拒否します。</summary>
    /// <returns>この一件に固定した保持範囲です。</returns>
    private Scope Find(Guid id)
    {
        if (completed.Contains(id))
        {
            throw new InvalidOperationException("終了した反復要素を再評価できません。");
        }
        if (!scopes.TryGetValue(id, out Scope? scope))
        {
            scope = new();
            scopes.Add(id, scope);
        }
        return scope;
    }

    /// <summary>最後の利用者が終わった要素だけを除去し、小さい再実行防止IDだけを残します。</summary>
    private void Release(Guid id, Scope scope)
    {
        if (scope.Finished && scope.Children == 0)
        {
            bytes -= scope.Bytes;
            scopes.Remove(id);
            completed.Add(id);
            scope.Completion?.TrySetResult(scope.Outcome);
            Released?.Invoke(id);
        }
    }

    /// <summary>全実行・子処理の終了を待った所有者から、残るルート範囲も解放します。</summary>
    internal void Clear()
    {
        using (gate.EnterScope())
        {
            scopes.Clear();
            completed.Clear();
            bytes = 0;
        }
    }

    /// <summary>子実行が値を参照し終わるまで、区間の終了を確認します。</summary>
    /// <returns>ボディと開始済みの子処理が全て終了した時点です。</returns>
    // 抑制理由: 待機を取消すと開始済みの子処理より先に同時実行枠を解放するため、所有範囲の実終了を待ちます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW510", Justification = "区間の解放に使う終了待ちは取消さず、開始済みの子処理を必ず回収します。")]
    internal Task<EOutcome> CompleteAndWaitAsync(Guid id)
    {
        using (gate.EnterScope())
        {
            Scope scope = Find(id);
            scope.Finished = true;
            scope.Completion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<EOutcome> completion = scope.Completion.Task;
            Release(id, scope);
            return completion;
        }
    }

    /// <summary>同じ反復要素の実行・値と、参照中の子処理だけを所有します。</summary>
    private sealed record Scope
    {
        internal EOutcome Outcome
        {
            get; set;
        } = EOutcome.Succeeded;

        internal TaskCompletionSource<EOutcome>? Completion
        {
            get; set;
        }

        internal Dictionary<string, Lazy<Task<ExecutionFrame>>> Values
        {
            get;
        } = new(StringComparer.Ordinal);

        internal Dictionary<string, Lazy<Task<ExecutionFrame>>> Signals
        {
            get;
        } = new(StringComparer.Ordinal);

        internal long Bytes
        {
            get; set;
        }

        internal int Children
        {
            get; set;
        }

        internal bool Finished
        {
            get; set;
        }
    }
}
