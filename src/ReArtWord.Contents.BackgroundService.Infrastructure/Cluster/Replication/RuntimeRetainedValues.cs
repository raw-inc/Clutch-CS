using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Cluster;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Cluster.Replication;

/// <summary>受信ごとの保持値をCoordinatorのメモリで比較更新し、設定や明示状態の永続CASとは分離します。</summary>
public sealed class RuntimeRetainedValues : INodeStateStore
{
    private readonly PeerCluster cluster;
    private readonly INodeStateStore durable;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Scope, string Key), VersionedState> values = [];
    private long bytes;
    private long epoch = -1;
    private bool usedMemory;
    private static string ReadKind => "runtime.retained.read";
    private static string WriteKind => "runtime.retained.write";

    /// <summary>値だけの短い操作を登録し、Peerの通常設定同期は従来の保存先へ残します。</summary>
    public RuntimeRetainedValues(PeerCluster cluster, INodeStateStore durable)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(durable);
        this.cluster = cluster;
        this.durable = durable;
        cluster.RegisterInspection(ReadKind, ReadRequestAsync);
        cluster.RegisterRuntimeHandler(WriteKind, WriteRequestAsync);
        cluster.EnableBufferedRuntime();
    }

    /// <summary>同じ担当の現在値だけを参照し、未保存だった過去実行の値を再起動後に復活させません。</summary>
    /// <returns>現在の値、またはこの担当世代では未供給のnullです。</returns>
    public async ValueTask<VersionedState?> ReadAsync(string scope, string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(scope, key);
        if (UseDurable())
        {
            return await durable.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        }
        PeerInfo owner = Owner();
        if (owner.InstanceId == cluster.LocalInstanceId)
        {
            return ReadCurrent(scope, key);
        }
        ClusterRequest request = new(Guid.CreateVersion7(), ReadKind, owner.PeerId, owner.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(new StateUpdate(scope, key, null, null)));
        ClusterResponse response = await cluster.InspectAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Outcome != EOutcome.Succeeded)
        {
            throw new IOException("実行中の保持値を現在の担当から確認できません。");
        }
        return response.Value?.Json.Deserialize<VersionedState>();
    }

    /// <summary>同じCore内では直接比較更新し、遠隔担当へは固定実体の要求を一度渡します。</summary>
    /// <returns>メモリ上で確認した比較結果です。WF全体の診断記録は別処理です。</returns>
    public async ValueTask<StateWriteResult> CompareExchangeAsync(string scope, string key, string? expectedVersion, NodeValue? value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(scope, key);
        if (UseDurable())
        {
            return await durable.CompareExchangeAsync(scope, key, expectedVersion, value, cancellationToken).ConfigureAwait(false);
        }
        PeerInfo owner = Owner();
        StateUpdate update = new(scope, key, expectedVersion, value);
        if (owner.InstanceId == cluster.LocalInstanceId)
        {
            return WriteCurrent(update);
        }
        ClusterRequest request = new(Guid.CreateVersion7(), WriteKind, owner.PeerId, owner.InstanceId, DateTimeOffset.UtcNow.AddSeconds(30), NodeValue.FromObject(update));
        ClusterResponse response = await cluster.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Value?.Json.Deserialize<StateWriteResult>() ?? new(false, null, response.Outcome, response.Reason);
    }

    /// <summary>旧Peerが参加している更新途中と明示離脱では既存保存境界を維持します。</summary>
    /// <returns>メモリ方式の使用前に限って互換経路を使う場合です。</returns>
    private bool UseDurable()
    {
        if (cluster.IsDetached)
        {
            return true;
        }
        bool supported = cluster.GetPeers().Where(peer => PeerStatusRules.CanParticipate(peer.Status)).All(peer => peer.BufferedRuntime);
        using (gate.EnterScope())
        {
            if (supported)
            {
                usedMemory = true;
                return false;
            }
            if (usedMemory)
            {
                throw new IOException("旧実行方式のPeerが再参加したため、過去の保持値へ自動で戻しません。");
            }
            return true;
        }
    }

    /// <summary>固定して照会する通常参加中の担当を取得します。</summary>
    /// <returns>現在の担当。欠測を古い担当への再配送で補いません。</returns>
    private PeerInfo Owner() => cluster.GetPeers().FirstOrDefault(peer => string.Equals(peer.PeerId, cluster.CoordinatorId, StringComparison.Ordinal) && PeerStatusRules.CanParticipate(peer.Status))
        ?? throw new IOException("実行中の保持値の担当を確認できません。");

    /// <summary>参照専用の内部要求です。</summary>
    /// <returns>現世代の値です。</returns>
    private Task<ClusterResponse> ReadRequestAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StateUpdate query = request.Payload.Json.Deserialize<StateUpdate>() ?? throw new InvalidDataException("保持値の照会が不正です。");
        Validate(query.Scope, query.Key);
        return Task.FromResult(new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(ReadCurrent(query.Scope, query.Key))));
    }

    /// <summary>永続保存と通信を含めない短い排他の中で、各開始世代を比較します。</summary>
    /// <returns>新しい世代の値、または具体的な比較・容量拒否です。</returns>
    private Task<ClusterResponse> WriteRequestAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StateUpdate update = request.Payload.Json.Deserialize<StateUpdate>() ?? throw new InvalidDataException("保持値の更新が不正です。");
        Validate(update.Scope, update.Key);
        StateWriteResult result = WriteCurrent(update);
        return Task.FromResult(new ClusterResponse(result.Outcome, NodeValue.FromObject(result), result.Reason));
    }

    /// <summary>同じCore内では通信や応答記録を作らず、遠隔要求と同じ世代・容量・比較条件で更新します。</summary>
    /// <returns>保持値から所有権を分けた現在値と、比較更新の結果です。</returns>
    private StateWriteResult WriteCurrent(StateUpdate update)
    {
        StateWriteResult result;
        using (gate.EnterScope())
        {
            EnsureOwner();
            (string, string) address = (update.Scope, update.Key);
            VersionedState? old = values.GetValueOrDefault(address);
            long before = old?.Value.PayloadSize() ?? 0, after = update.Value?.PayloadSize() ?? 0;
            if (!string.Equals(old?.Version, update.ExpectedVersion, StringComparison.Ordinal))
            {
                result = new(false, old, EOutcome.Failed, "Persistence.VersionConflict");
            }
            else
            {
                if (4194304 < after || 67108864 < bytes - before + after || old is null && 4096 <= values.Count)
                {
                    result = new(false, old, EOutcome.NotExecuted, "State.RuntimeCapacity");
                }
                else
                {
                    VersionedState? current = null;
                    values.Remove(address);
                    if (update.Value is not null)
                    {
                        current = new(Guid.NewGuid().ToString("N"), update.Value.Copy());
                        values.Add(address, current);
                    }
                    bytes += after - before;
                    result = new(true, current, EOutcome.Succeeded);
                }
            }
        }
        return result.Current is null ? result
                                      : result with { Current = result.Current with { Value = result.Current.Value.Copy() } };
    }

    /// <summary>同じ世代の値だけをローカルで取得します。</summary>
    /// <returns>保持済みの所有値です。</returns>
    private VersionedState? ReadCurrent(string scope, string key)
    {
        using (gate.EnterScope())
        {
            EnsureOwner();
            VersionedState? value = values.GetValueOrDefault((scope, key));
            return value is null ? null
                                 : value with { Value = value.Value.Copy() };
        }
    }

    /// <summary>担当を一度失った実体が古いメモリ値を再び供給しないようにします。</summary>
    private void EnsureOwner()
    {
        long current = cluster.CoordinatorRevision;
        if (epoch != current)
        {
            epoch = current;
            values.Clear();
            bytes = 0;
        }
        if (!string.Equals(cluster.CoordinatorId, cluster.LocalPeerId, StringComparison.Ordinal)
            || !cluster.GetPeers().Any(peer => peer.InstanceId == cluster.LocalInstanceId && PeerStatusRules.CanParticipate(peer.Status)))
        {
            throw new IOException("実行中の保持値の担当が交代しました。");
        }
    }

    /// <summary>設定や明示状態の保存を、この揮発的な値経路へ混入させません。</summary>
    private static void Validate(string scope, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        string workflow = "workflow:", runtime = "runtime:node-output:";
        if (!scope.StartsWith(workflow, StringComparison.Ordinal) || !key.StartsWith(runtime, StringComparison.Ordinal))
        {
            throw new ArgumentException("実行中のノード保持値だけを指定してください。", nameof(key));
        }
    }
}
