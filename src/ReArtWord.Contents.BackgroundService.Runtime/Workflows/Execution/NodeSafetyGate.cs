using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>明示した安全停止と人の停止に、入力の破棄・最新保持・全件待機を適用します。</summary>
public sealed class NodeSafetyGate
{
    private readonly IPeerDirectory peers;
    private readonly ConcurrentDictionary<(string Workflow, string Scope), bool> manual = new();
    private readonly ConcurrentDictionary<(string Workflow, string Node), long> newest = new();
    private readonly ConcurrentDictionary<(string Workflow, string Node), int> waiting = new();
    private readonly TimeProvider clock;
    private long sequence;
    private static TimeSpan RecheckInterval => TimeSpan.FromMilliseconds(100);

    /// <summary>共有保存から取り込んだ人の停止を確認します。</summary>
    public Func<string, NodeDefinition, bool>? ManualStopCheck
    {
        get; set;
    }

    /// <summary>人が明示した、自動停止に対する一時続行を確認します。</summary>
    public Func<string, NodeDefinition, bool>? ManualContinueCheck
    {
        get; set;
    }

    /// <summary>参加状態と共通時刻を参照します。</summary>
    public NodeSafetyGate(IPeerDirectory peers, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        this.peers = peers;
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>ワークフロー内のノードまたは安全Groupの手動停止状態を変更します。</summary>
    public void SetStopped(string workflowId, string scope, bool stopped)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (stopped)
        {
            manual[(workflowId, scope)] = true;
        }
        else
        {
            manual.TryRemove((workflowId, scope), out _);
        }
    }

    /// <summary>指定しない通常ノードは、Peer数だけを理由に停止しません。</summary>
    /// <returns>現在、新しい副作用を停止するかを示します。</returns>
    public bool IsStopped(string workflowId, NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (ManualStopCheck?.Invoke(workflowId, node) == true || manual.GetValueOrDefault((workflowId, node.Id))
            || (node.Safety?.Group is not null && manual.GetValueOrDefault((workflowId, node.Safety.Group))))
        {
            return true;
        }
        if (ManualContinueCheck?.Invoke(workflowId, node) == true)
        {
            return false;
        }
        return IsAutomaticallyStopped(node);
    }

    /// <summary>一時操作を除く、現在の通常の停止判断です。</summary>
    /// <returns>一時操作の復旧後に適用する自動判断です。</returns>
    public bool IsAutomaticallyStopped(NodeDefinition node)
    {
        return GetAutomaticStopReason(node) is not null;
    }

    /// <summary>一時操作の対象Scopeに属するノードから通常の停止判断を求めます。</summary>
    /// <returns>未取得または対象なしはnull、対象がある場合だけ停止判断を返します。</returns>
    public bool? IsScopeAutomaticallyStopped(WorkflowDefinition? definition, string scope)
    {
        if (definition is null)
        {
            return null;
        }
        NodeDefinition[] nodes = definition.Nodes.Where(node => string.Equals(node.Id, scope, StringComparison.Ordinal)
            || string.Equals(node.Safety?.Group, scope, StringComparison.Ordinal)).ToArray();
        if (nodes.Length == 0)
        {
            return null;
        }
        return nodes.Any(IsAutomaticallyStopped);
    }

    /// <summary>一時操作前の確認で、停止理由を状態の推測から分離します。</summary>
    /// <returns>自動停止理由、通常続行ならnullです。</returns>
    public string? GetAutomaticStopReason(NodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Safety is null)
        {
            return null;
        }
        PeerInfo[] known = peers.GetPeers().ToArray();
        if (known.Any(peer => string.Equals(peer.PeerId, peers.LocalPeerId, StringComparison.Ordinal) && peer.Status == EPeerStatus.Detached))
        {
            return null;
        }
        int ready = known.Count(static peer => PeerStatusRules.CanParticipate(peer.Status));
        if (ready <= 1)
        {
            if (node.Safety.StopWhenSingleton ?? true)
            {
                return "safety.singleton";
            }
            return null;
        }
        int participating = known.Count(static peer => peer.Status != EPeerStatus.Detached);
        if (ready < participating && (node.Safety.StopWhenDegraded ?? false))
        {
            return "safety.degraded";
        }
        return null;
    }

    /// <summary>停止中入力の方針を適用し、再開または未実行の確定を待ちます。</summary>
    /// <returns>実行可能ならnull、破棄・置換なら具体的な未実行理由です。</returns>
    public async ValueTask<NodeResult?> WaitAsync(string workflowId, NodeDefinition node, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsStopped(workflowId, node))
        {
            return null;
        }
        EStoppedInputPolicy policy = node.Safety?.Inputs ?? EStoppedInputPolicy.Drop;
        if (policy == EStoppedInputPolicy.Drop)
        {
            return NodeResult.Failure("safety.dropped", "停止中の入力を設定された方針で破棄しました。", EOutcome.NotExecuted);
        }
        (string Workflow, string Node) key = (workflowId, node.Id);
        long ticket = Interlocked.Increment(ref sequence);
        int count = waiting.AddOrUpdate(key, 1, static (_, value) => value + 1);
        if (128 < count)
        {
            waiting.AddOrUpdate(key, 0, static (_, value) => Math.Max(0, value - 1));
            return NodeResult.Failure("safety.capacity", "停止中入力の保持上限に達したため未実行として拒否しました。", EOutcome.NotExecuted);
        }
        try
        {
            if (policy == EStoppedInputPolicy.Latest)
            {
                newest[key] = ticket;
            }
            while (IsStopped(workflowId, node))
            {
                if (policy == EStoppedInputPolicy.Latest && newest.GetValueOrDefault(key) != ticket)
                {
                    return NodeResult.Failure("safety.replaced", "停止中に到着した新しい入力へ置き換えました。", EOutcome.NotExecuted);
                }
                await Task.Delay(RecheckInterval, clock, cancellationToken).ConfigureAwait(false);
            }
            if (policy == EStoppedInputPolicy.Latest && newest.GetValueOrDefault(key) != ticket)
            {
                return NodeResult.Failure("safety.replaced", "停止中に到着した新しい入力へ置き換えました。", EOutcome.NotExecuted);
            }
            return null;
        }
        finally
        {
            waiting.AddOrUpdate(key, 0, static (_, value) => Math.Max(0, value - 1));
            waiting.TryRemove(new KeyValuePair<(string Workflow, string Node), int>(key, 0));
            newest.TryRemove(new KeyValuePair<(string Workflow, string Node), long>(key, ticket));
        }
    }
}
