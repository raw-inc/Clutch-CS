using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Sources;

namespace ReArtWord.Contents.BackgroundService.CoreService.Workflows;

/// <summary>退避経路の所有元を実体に固定し、登録済みPeerだけへ待受の終了を配送します。</summary>
internal sealed class SourceScopeBinding(PeerCluster cluster) : ISourceScopeControl
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, Entry> active = [];
    private readonly Dictionary<SourceScope, NodeResult> completed = [];
    private readonly Queue<SourceScope> recent = [];
    private readonly ConcurrentDictionary<Guid, bool> capable = new();
    private SourceSupervisor? sources;

    /// <summary>空の所有範囲を作成し、本文や実行履歴は保持しません。</summary>
    /// <returns>この起動中だけ有効な識別子です。</returns>
    public SourceScope Create(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        using (gate.EnterScope())
        {
            if (1024 <= active.Count)
            {
                throw new InvalidOperationException("The active source scope limit was reached.");
            }
            SourceScope scope = new(Guid.CreateVersion7(), workflowId, cluster.LocalPeerId, cluster.LocalInstanceId);
            active.Add(scope.Id, new(scope));
            return scope;
        }
    }

    /// <summary>終了した所有範囲には新しい待受を参加させません。</summary>
    /// <returns>生存の確定値、または照会結果不明です。</returns>
    public async ValueTask<bool?> CheckAsync(SourceScope scope, bool enroll, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.PeerInstanceId == cluster.LocalInstanceId && string.Equals(scope.PeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            return CheckLocal(scope, enroll, cluster.LocalPeerId, cluster.LocalInstanceId);
        }
        if (!Available(scope.PeerId, scope.PeerInstanceId))
        {
            return false;
        }
        string operation = "source.scope.check";
        NodeValue payload = ContractJson.ToValue(scope);
        if (enroll)
        {
            operation = "source.scope.enroll";
            payload = ContractJson.ToValue(new Enrollment(scope, cluster.LocalPeerId, cluster.LocalInstanceId));
        }
        ClusterResponse response;
        if (enroll)
        {
            response = await RequestAsync(operation, scope.PeerId, scope.PeerInstanceId, payload, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            response = await InspectAsync(operation, scope.PeerId, scope.PeerInstanceId, payload, cancellationToken).ConfigureAwait(false);
        }
        return ConfirmedBoolean(response);
    }

    /// <summary>先に開始許可を閉じてから停止し、同名Nodeの別の退避回には影響させません。</summary>
    /// <returns>全参加先の待受終了、または確認できなかった結果です。</returns>
    public async ValueTask<NodeResult> EndAsync(SourceScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Entry entry;
        KeyValuePair<string, Guid>[]? targets = null;
        using (gate.EnterScope())
        {
            if (completed.TryGetValue(scope, out NodeResult? previous))
            {
                return previous;
            }
            if (!active.TryGetValue(scope.Id, out entry!) || entry.Scope != scope)
            {
                return NodeResult.Failure("source.scope.missing", "The source scope does not belong to this peer instance.", EOutcome.NotExecuted);
            }
            if (entry.Open)
            {
                entry.Open = false;
                targets = entry.Peers.ToArray();
            }
        }
        if (targets is null)
        {
            return await entry.Ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        NodeResult result = NodeResult.Failure("source.scope.stopUnconfirmed", "Some scoped listeners did not confirm shutdown. Already accepted work may continue.", EOutcome.Unknown);
        try
        {
            bool[] stopped = await Task.WhenAll(targets.Select(target => StopPeerAsync(scope, target.Key, target.Value, cancellationToken))).ConfigureAwait(false);
            if (stopped.All(static value => value))
            {
                result = NodeResult.Success();
            }
            return result;
        }
        finally
        {
            using (gate.EnterScope())
            {
                active.Remove(scope.Id);
                completed[scope] = result;
                recent.Enqueue(scope);
                while (128 < recent.Count)
                {
                    completed.Remove(recent.Dequeue());
                }
            }
            entry.Ended.TrySetResult(result);
        }
    }

    /// <summary>旧Coreが所有情報を無視して待受を開始することを防ぎます。</summary>
    /// <returns>配送拒否、または対応確認済みならnullです。</returns>
    internal async ValueTask<NodeResult?> CheckTargetAsync(RemoteExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.TargetInstanceId == cluster.LocalInstanceId || capable.ContainsKey(request.TargetInstanceId))
        {
            return null;
        }
        ClusterResponse response = await InspectAsync("source.scope.capabilities", request.TargetPeerId, request.TargetInstanceId, NodeValue.Null, cancellationToken).ConfigureAwait(false);
        if (ConfirmedBoolean(response) != true)
        {
            return NodeResult.Failure("source.scope.unsupported", "Update the target Core before starting a scoped fallback path.", EOutcome.NotExecuted);
        }
        HashSet<Guid> current = cluster.GetPeers().Select(static peer => peer.InstanceId).ToHashSet();
        foreach (Guid old in capable.Keys.Where(id => !current.Contains(id)))
        {
            capable.TryRemove(old, out _);
        }
        capable[request.TargetInstanceId] = true;
        return null;
    }

    /// <summary>永続履歴や通常の管理画面へ追加せず、実体間の短い制御要求を登録します。</summary>
    internal void Register(SourceSupervisor supervisor)
    {
        sources = supervisor;
        cluster.RegisterInspection("source.scope.capabilities", (_, _) => Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(true))));
        cluster.RegisterInspection("source.scope.check", (request, _) =>
        {
            SourceScope? scope = request.Payload.Json.Deserialize<SourceScope>(ContractJson.Options);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(scope is not null && CheckLocal(scope, false, string.Empty, Guid.Empty))));
        });
        cluster.RegisterHandler("source.scope.enroll", (request, _) =>
        {
            Enrollment? enrollment = request.Payload.Json.Deserialize<Enrollment>(ContractJson.Options);
            bool accepted = enrollment is not null && Available(enrollment.PeerId, enrollment.PeerInstanceId)
                && CheckLocal(enrollment.Scope, true, enrollment.PeerId, enrollment.PeerInstanceId);
            return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(accepted)));
        });
        cluster.RegisterHandler("source.scope.end", async (request, token) =>
        {
            SourceScope? scope = request.Payload.Json.Deserialize<SourceScope>(ContractJson.Options);
            bool stopped = scope is not null && await supervisor.EndScopeAsync(scope, token).ConfigureAwait(false);
            return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(stopped));
        });
    }

    /// <summary>登録と終了を同じロックで直列化し、終了通知より後の開始を防ぎます。</summary>
    /// <returns>登録可能な範囲が存在するかです。</returns>
    private bool CheckLocal(SourceScope scope, bool enroll, string peer, Guid instance)
    {
        using (gate.EnterScope())
        {
            if (!active.TryGetValue(scope.Id, out Entry? entry) || entry.Scope != scope || !entry.Open)
            {
                return false;
            }
            if (enroll)
            {
                entry.Peers[peer] = instance;
            }
            return true;
        }
    }

    /// <summary>待受開始時と同じPeer実体だけを停止対象にします。</summary>
    /// <returns>現在の実体と参加状態が一致するかです。</returns>
    private bool Available(string peer, Guid instance) => cluster.GetPeers().Any(item => item.InstanceId == instance
        && string.Equals(item.PeerId, peer, StringComparison.Ordinal) && (PeerStatusRules.CanParticipate(item.Status) || item.Status == EPeerStatus.Detached));

    /// <summary>待受の実終了を確認し、過去のPeer実体へは再配送しません。</summary>
    /// <returns>受付ではなく待受終了の確認です。</returns>
    private async Task<bool> StopPeerAsync(SourceScope scope, string peer, Guid instance, CancellationToken cancellationToken = default)
    {
        if (instance == cluster.LocalInstanceId && string.Equals(peer, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                return sources is not null && await sources.EndScopeAsync(scope, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }
        if (!Available(peer, instance))
        {
            return false;
        }
        ClusterResponse response = await RequestAsync("source.scope.end", peer, instance, ContractJson.ToValue(scope), cancellationToken).ConfigureAwait(false);
        return ConfirmedBoolean(response) == true;
    }

    /// <summary>元の実体へ有限期限で制御を届け、結果不明を再実行へ読み替えません。</summary>
    /// <returns>元要求に対応した応答です。</returns>
    private Task<ClusterResponse> RequestAsync(string operation, string peer, Guid instance, NodeValue payload, CancellationToken cancellationToken = default)
        => ExchangeAsync(operation, peer, instance, payload, false, cancellationToken);

    /// <summary>継続監視と能力照会では操作台帳を書き込まず、読み取り専用経路を使用します。</summary>
    /// <returns>保存を伴わない照会結果です。</returns>
    private Task<ClusterResponse> InspectAsync(string operation, string peer, Guid instance, NodeValue payload, CancellationToken cancellationToken = default)
        => ExchangeAsync(operation, peer, instance, payload, true, cancellationToken);

    /// <summary>相手の受付期限だけでなく、通信自体も有限時間で終了させます。</summary>
    /// <returns>確認した応答、または内部期限超過による結果不明です。</returns>
    private async Task<ClusterResponse> ExchangeAsync(string operation, string peer, Guid instance, NodeValue payload, bool inspection, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        ClusterRequest request = new(Guid.CreateVersion7(), operation, peer, instance, DateTimeOffset.UtcNow.AddSeconds(5), payload);
        try
        {
            if (inspection)
            {
                return await cluster.InspectAsync(request, timeout.Token).ConfigureAwait(false);
            }
            return await cluster.RequestAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(EOutcome.Unknown, Reason: "source.scope.timeout");
        }
    }

    /// <summary>結果不明や不正な応答を、停止済みや稼働中へ決め付けません。</summary>
    /// <returns>確認できた真偽だけを返します。</returns>
    private static bool? ConfirmedBoolean(ClusterResponse response)
    {
        if (response.Outcome == EOutcome.Succeeded && response.Value?.Kind == EValueKind.Json
            && response.Value.Json.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return response.Value.Json.GetBoolean();
        }
        return null;
    }

    /// <summary>範囲と、これから待受を開始するPeerの対応です。</summary>
    private sealed record Enrollment(SourceScope Scope, string PeerId, Guid PeerInstanceId);

    /// <summary>一回の範囲の有効状態と、参加実体だけを保持します。</summary>
    private sealed class Entry(SourceScope scope)
    {
        internal SourceScope Scope
        {
            get;
        } = scope;

        internal bool Open
        {
            get; set;
        } = true;

        internal Dictionary<string, Guid> Peers
        {
            get;
        } = new(StringComparer.Ordinal);

        internal TaskCompletionSource<NodeResult> Ended
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>診断には範囲識別子だけを使用します。</summary>
        /// <returns>一回の範囲IDです。</returns>
        public override string ToString() => Scope.Id.ToString();
    }
}
