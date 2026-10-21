using System.Collections.Concurrent;
using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Snapshots;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Infrastructure.Cluster;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.ManagementTransport.Datagrams;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Snapshots;

/// <summary>このPeerに属する状態の準備・変更・比較を担当します。</summary>
internal sealed class SnapshotEndpoint
{
    private readonly PeerCluster cluster;
    private readonly WorkflowStore workflows;
    private readonly ConcurrentDictionary<SnapshotTarget, Guid> locks = [];
    private readonly ConcurrentDictionary<string, IRuntimeSnapshotParticipant> participants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IRuntimeSnapshotParticipant> configurations = new(StringComparer.Ordinal);

    /// <summary>通常管理と同じ要求操作記録経路へ接続します。</summary>
    public SnapshotEndpoint(PeerCluster cluster, WorkflowStore workflows)
    {
        this.cluster = cluster;
        this.workflows = workflows;
        cluster.RegisterHandler("snapshot.capture", CaptureAsync);
        cluster.RegisterHandler("snapshot.prepare", PrepareAsync);
        cluster.RegisterHandler("snapshot.apply", ApplyAsync);
        cluster.RegisterHandler("snapshot.compare", CompareAsync);
        cluster.RegisterHandler("snapshot.release", ReleaseAsync);
    }

    /// <summary>宣言した状態だけを復元可能にします。</summary>
    public void Register(IRuntimeSnapshotParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!participants.TryAdd(participant.Id, participant))
        {
            throw new ArgumentException("runtime状態は登録済みです。", nameof(participant));
        }
    }

    /// <summary>ワークフロー以外の設定領域を設定スナップショットとして登録します。</summary>
    public void RegisterConfiguration(IRuntimeSnapshotParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!configurations.TryAdd(participant.Id, participant))
        {
            throw new ArgumentException("設定領域は登録済みです。", nameof(participant));
        }
        workflows.ReserveConfigurationId(participant.Id);
    }

    /// <summary>共有設定の通常編集が確認する復元保護です。</summary>
    /// <returns>当該設定が保護中か。</returns>
    public bool IsConfigurationLocked(string id) => locks.Keys.Any(target => target.Kind == ESnapshotKind.Configuration && string.Equals(target.Id, id, StringComparison.Ordinal));

    /// <summary>ノード状態は対応ワークフロー全体、設定は選択範囲を保護します。</summary>
    /// <returns>共有編集の保護対象です。</returns>
    public SnapshotEditLock? EditLock(Guid operationId, SnapshotTarget target)
    {
        if (target.Kind == ESnapshotKind.Configuration)
        {
            return new SnapshotEditLock(operationId, target.Id, target.NodeIds, Participant(target) is not null);
        }
        if (Participant(target) is NodeStateSnapshotParticipant state)
        {
            return new SnapshotEditLock(operationId, state.WorkflowId, null);
        }
        return null;
    }

    /// <summary>設定と状態の組合せを実変更より前に検証します。</summary>
    /// <returns>宣言された状態の互換性を確認できるか。</returns>
    private async Task<bool> CompatibleAsync(SnapshotStep step, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CompatibleCoreAsync(step, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>選択組合せまたは現在値を契約と照合します。</summary>
    /// <returns>状態の利用条件が一致するか。</returns>
    private async Task<bool> CompatibleCoreAsync(SnapshotStep step, CancellationToken cancellationToken = default)
    {
        foreach (NodeStateSnapshotParticipant state in participants.Values.OfType<NodeStateSnapshotParticipant>())
        {
            bool selectedState = step.Target.Kind == ESnapshotKind.Runtime && string.Equals(state.Id, step.Target.Id, StringComparison.Ordinal);
            bool selectedConfiguration = step.Target.Kind == ESnapshotKind.Configuration && string.Equals(state.WorkflowId, step.Target.Id, StringComparison.Ordinal);
            if (!selectedState && !selectedConfiguration)
            {
                continue;
            }
            NodeValue? value = step.RuntimeStates?.GetValueOrDefault(state.Id);
            if (value is null)
            {
                value = step.Value;
                if (!selectedState)
                {
                    value = await state.CaptureAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            NodeValue? configuration = step.Configurations?.GetValueOrDefault(state.WorkflowId);
            ConfigurationSnapshotState? candidate = configuration?.Json.Deserialize<ConfigurationSnapshotState>(ContractJson.Options);
            SnapshotTarget? configurationTarget = step.ConfigurationTargets?.GetValueOrDefault(state.WorkflowId);
            if (configuration is not null && configurationTarget?.NodeIds is not null)
            {
                candidate = await CandidateAsync(step with { Target = configurationTarget, Value = configuration }, cancellationToken).ConfigureAwait(false);
            }
            if (value is null || !state.IsCompatible(value, candidate?.Effective, configuration is not null))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>明示登録された設定領域またはruntime領域を選びます。</summary>
    /// <returns>対応する参加者、またはワークフロー設定のnull。</returns>
    private IRuntimeSnapshotParticipant? Participant(SnapshotTarget target)
    {
        if (target.Kind == ESnapshotKind.Configuration)
        {
            return configurations.GetValueOrDefault(target.Id);
        }
        return participants.GetValueOrDefault(target.Id);
    }

    /// <summary>実状態を読みます。</summary>
    /// <returns>取得結果。</returns>
    private async Task<ClusterResponse> CaptureAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotStep step = Read(request);
        IRuntimeSnapshotParticipant? registered = Participant(step.Target);
        if (registered is not null)
        {
            if (step.Target.Kind == ESnapshotKind.Configuration && await workflows.GetAsync(step.Target.Id, cancellationToken).ConfigureAwait(false) is not null)
            {
                return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.TargetAmbiguous");
            }
            return new ClusterResponse(EOutcome.Succeeded, await registered.CaptureAsync(cancellationToken).ConfigureAwait(false));
        }
        if (step.Target.Kind == ESnapshotKind.Configuration)
        {
            ConfigurationSnapshotState? state = await workflows.CaptureConfigurationAsync(step.Target.Id, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.ConfigurationMissing");
            }
            string captureKind = "snapshot.capture";
            if (string.Equals(request.Kind, captureKind, StringComparison.Ordinal) && step.Target.NodeIds is not null && step.Target.NodeIds.Any(id => !state.Saved.Definition.Nodes.Any(node => string.Equals(node.Id, id, StringComparison.Ordinal))))
            {
                return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.NodeMissing");
            }
            return new ClusterResponse(EOutcome.Succeeded, NodeValue.FromObject(state));
        }
        if (!participants.TryGetValue(step.Target.Id, out IRuntimeSnapshotParticipant? participant))
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.RuntimeUndeclared");
        }
        return new ClusterResponse(EOutcome.Succeeded, await participant.CaptureAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>影響対象だけを保護し、現在処理を止めず準備可能性を確認します。</summary>
    /// <returns>準備結果と開始前値。</returns>
    private async Task<ClusterResponse> PrepareAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotStep step = Read(request);
        if (!locks.TryAdd(step.Target, step.OperationId) && locks[step.Target] != step.OperationId)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.ScopeLocked");
        }
        if (step.Value is not null)
        {
            if (!await CompatibleAsync(step, cancellationToken).ConfigureAwait(false))
            {
                return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.IncompatibleStateContract");
            }
            IReadOnlyList<ValidationIssue> issues;
            IRuntimeSnapshotParticipant? registered = Participant(step.Target);
            if (registered is not null)
            {
                issues = await registered.ValidateAsync(step.Value, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (step.Target.Kind == ESnapshotKind.Configuration)
                {
                    ConfigurationSnapshotState state = await CandidateAsync(step, cancellationToken).ConfigureAwait(false);
                    issues = workflows.Validate(state.Saved.Definition);
                    if (state.Effective is not null)
                    {
                        issues = issues.Concat(workflows.Validate(state.Effective)).ToArray();
                    }
                    if (!string.Equals(state.Saved.Definition.Id, step.Target.Id, StringComparison.Ordinal)
                        || (state.Effective is not null && !string.Equals(state.Effective.Id, step.Target.Id, StringComparison.Ordinal)))
                    {
                        return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.TargetMismatch");
                    }
                }
                else
                {
                    if (!participants.TryGetValue(step.Target.Id, out IRuntimeSnapshotParticipant? participant))
                    {
                        return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.RuntimeUndeclared");
                    }
                    issues = await participant.ValidateAsync(step.Value, cancellationToken).ConfigureAwait(false);
                }
            }
            if (issues.Count != 0)
            {
                return new ClusterResponse(EOutcome.NotExecuted, NodeValue.FromObject(issues), "Snapshot.PreparationFailed");
            }
        }
        return await CaptureAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>保護した対象だけを変更します。</summary>
    /// <returns>変更結果。</returns>
    private async Task<ClusterResponse> ApplyAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotStep step = Read(request);
        if (!locks.TryGetValue(step.Target, out Guid owner) || owner != step.OperationId || step.Value is null)
        {
            return new ClusterResponse(EOutcome.NotExecuted, Reason: "Snapshot.PreparationMissing");
        }
        ConfigurationPhaseResult result;
        IRuntimeSnapshotParticipant? registered = Participant(step.Target);
        if (registered is not null)
        {
            if (registered is IConfigurationSnapshotParticipant configuration)
            {
                result = await configuration.RestoreAsync(step.OperationId, step.Value, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await registered.RestoreAsync(step.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            if (step.Target.Kind == ESnapshotKind.Configuration)
            {
                ConfigurationSnapshotState state = await CandidateAsync(step, cancellationToken).ConfigureAwait(false);
                result = await workflows.RestoreConfigurationAsync(request.Id, state, step.OperationId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await participants[step.Target.Id].RestoreAsync(step.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        return new ClusterResponse(result.Outcome, Reason: result.Reason);
    }

    /// <summary>取得できない状態を一致済みにしません。</summary>
    /// <returns>比較結果。</returns>
    private async Task<ClusterResponse> CompareAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotStep step = Read(request);
        if (step.Value is null)
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "Snapshot.CompareValueMissing");
        }
        bool matches;
        IRuntimeSnapshotParticipant? registered = Participant(step.Target);
        if (registered is not null)
        {
            matches = await registered.MatchesAsync(step.Value, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (step.Target.Kind == ESnapshotKind.Configuration)
            {
                ConfigurationSnapshotState expected = step.Value.Json.Deserialize<ConfigurationSnapshotState>() ?? throw new InvalidDataException("設定snapshotが不正です。");
                ConfigurationSnapshotState? actual = await workflows.CaptureConfigurationAsync(step.Target.Id, cancellationToken).ConfigureAwait(false);
                matches = actual is not null && string.Equals(SnapshotNodeSelection.Hash(actual.Saved.Definition, step.Target.NodeIds), SnapshotNodeSelection.Hash(expected.Saved.Definition, step.Target.NodeIds), StringComparison.Ordinal) && string.Equals(SnapshotNodeSelection.Hash(actual.Effective, step.Target.NodeIds), SnapshotNodeSelection.Hash(expected.Effective, step.Target.NodeIds), StringComparison.Ordinal);
            }
            else
            {
                matches = await participants[step.Target.Id].MatchesAsync(step.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        if (!matches)
        {
            return new ClusterResponse(EOutcome.Failed, Reason: "Snapshot.StateMismatch");
        }
        return new ClusterResponse(EOutcome.Succeeded);
    }

    /// <summary>同じ復元操作のスコープ保護だけを解除します。</summary>
    /// <returns>解除結果。</returns>
    private Task<ClusterResponse> ReleaseAsync(ClusterRequest request, CancellationToken cancellationToken = default)
    {
        SnapshotStep step = Read(request);
        if (locks.TryGetValue(step.Target, out Guid owner) && owner == step.OperationId)
        {
            locks.TryRemove(step.Target, out _);
        }
        return Task.FromResult(new ClusterResponse(EOutcome.Succeeded));
    }

    /// <summary>別Peerの対象をローカル対象へ黙って置換しません。</summary>
    /// <returns>検証した段階要求。</returns>
    private SnapshotStep Read(ClusterRequest request)
    {
        SnapshotStep step = request.Payload.Json.Deserialize<SnapshotStep>() ?? throw new InvalidDataException("snapshot要求が不正です。");
        if (!string.Equals(step.Target.PeerId, cluster.LocalPeerId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("snapshot対象Peerが一致しません。");
        }
        if (!SnapshotNodeSelection.Valid(step.Target) || (step.Target.NodeIds is not null && Participant(step.Target) is not null))
        {
            throw new InvalidDataException("Node部分指定はWorkflow設定だけで利用できます。");
        }
        return step;
    }

    /// <summary>現在の対象外設定を維持した候補を作ります。</summary>
    /// <returns>全体または選択範囲の復元候補です。</returns>
    private async Task<ConfigurationSnapshotState> CandidateAsync(SnapshotStep step, CancellationToken cancellationToken = default)
    {
        ConfigurationSnapshotState snapshot = step.Value!.Json.Deserialize<ConfigurationSnapshotState>(ContractJson.Options) ?? throw new InvalidDataException("設定snapshotが不正です。");
        if (!string.Equals(snapshot.Saved.Definition.Id, step.Target.Id, StringComparison.Ordinal) || (snapshot.Effective is not null && !string.Equals(snapshot.Effective.Id, step.Target.Id, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("snapshot内容のWorkflowが対象と一致しません。");
        }
        if (step.Target.NodeIds is null)
        {
            return snapshot;
        }
        ConfigurationSnapshotState current = await workflows.CaptureConfigurationAsync(step.Target.Id, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("部分復元の現在設定がありません。");
        return SnapshotNodeSelection.Merge(current, snapshot, step.Target.NodeIds);
    }
}
