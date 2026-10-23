using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Operational;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Tracing;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Files.Transfers;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Targets;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

/// <summary>一ノードの入力・実行先を確定し、配送と補償の既存境界へ渡します。</summary>
internal sealed class WorkflowNodeEvaluation(NodeRegistry registry, IPeerDirectory directory, WorkflowNodeDispatch delivery,
    WorkflowExecutionOwnership ownership, TimeProvider timeProvider, TimeZoneInfo timeZone, TimeSpan admissionLifetime)
{
    private static string RollbackPolicy => "rollbackAll";

    /// <summary>同じ一件の必須入力だけで各固定対象を実行します。</summary>
    /// <returns>全対象を含むノード結果です。</returns>
    internal async Task<ExecutionFrame> ExecuteAsync(
        CompiledWorkflow workflow,
        NodeDefinition node,
        TargetResolution target,
        PcSelectionResolver selection,
        Task<ExecutionFrame>[] parents,
        bool entry,
        IReadOnlyDictionary<string, NodeValue>? entryInputs,
        bool valueRead,
        Guid causationId,
        ClusterOperationalSnapshot? operational,
        TracePolicy trace,
        Guid iterationScope,
        CancellationToken cancellationToken = default)
    {
        ExecutionFrame[] upstream = await Task.WhenAll(parents).ConfigureAwait(false);

        // リンク先tokenへの通知より先に上流の取消結果が届くことがあるため、入力不足を判定する前に呼出元の取消も確認します。
        if (cancellationToken.IsCancellationRequested)
        {
            return new(node.Id, EOutcome.Cancelled, [new(string.Empty,
                NodeResult.Failure("execution.cancelledBeforeStart", "The execution was cancelled before this node started.", EOutcome.Cancelled))]);
        }
        NodeDescriptor descriptor = ConfiguredNodePorts.ResolveExecution(registry.Find(node.Type)!.Descriptor, node)!;
        if (descriptor.Outputs.Any(port => port.ValueKind == EPortValueKind.PcSelection) && workflow.ConfigurationValues?.ContainsKey(node.Id) == true)
        {
            return Skipped(node.Id, "selection.configuration", "PC選択の条件は実行計画に反映済みです。") with { ConfigurationOnly = true };
        }
        if (!entry && workflow.ConfigurationValues?.ContainsKey(node.Id) == true && upstream.All(frame => frame.ConfigurationOnly))
        {
            return Skipped(node.Id, "value.configuration", "この一件では実行せず、設定版の純粋値だけを供給します。") with { ConfigurationOnly = true };
        }
        Dictionary<string, ExecutionFrame> frames = upstream.ToDictionary(static frame => frame.NodeId, StringComparer.Ordinal);
        if (workflow.DynamicPcSelections.Contains(node.Id))
        {
            target = RuntimePcSelection.Resolve(node, workflow, registry, frames, selection);
        }
        if (target.Error is not null)
        {
            NodeResult error = entry ? target.Error
                                     : PureValueInputs.CheckActivation(registry, node, workflow, frames) ?? target.Error;
            return new ExecutionFrame(node.Id, error.Outcome, [new TargetNodeResult(string.Empty, error)], PcSelection: target.Selection);
        }
        if (!entry && upstream.Length == 0)
        {
            return Skipped(node.Id, "input.inactive", "この一件で開始された入口ではありません。");
        }
        List<Task<TargetNodeResult>> executions = [];
        List<RemoteExecutionRequest> requests = [];
        foreach (Contracts.Cluster.PeerInfo peer in target.Peers)
        {
            IReadOnlyDictionary<string, NodeValue>? initial = null;
            if (entry)
            {
                initial = entryInputs;
            }
            IReadOnlyDictionary<string, NodeValue> inputs;
            bool receivedWire;
            bool missingValue;
            try
            {
                (inputs, receivedWire, missingValue) = PureValueInputs.Create(registry, node, peer.PeerId, workflow, frames, initial, requireConnected: !entry,
                    ignoreTrigger: node.Type is "control.toggleButton" && (valueRead || entry));
            }
            catch (FormatException)
            {
                executions.Add(Task.FromResult(new TargetNodeResult(peer.PeerId,
                    NodeResult.Failure("input.path", "入力の型と抽出パスが一致しません。", EOutcome.NotExecuted))));
                continue;
            }
            bool missing = missingValue || descriptor.Inputs.Any(port => port.Required && !inputs.ContainsKey(port.Name));
            if (missing || (!entry && !receivedWire))
            {
                NodeResult unavailable = PureValueInputs.MissingResult(node, workflow, inputs, frames, entry, receivedWire, peer.PeerId);
                executions.Add(Task.FromResult(new TargetNodeResult(peer.PeerId, unavailable)));
                continue;
            }
            (NodeDefinition executable, NodeResult? settingsError) = RuntimeSettingsBinding.Resolve(node, registry.Find(node.Type)!, workflow.Incoming[node.Id], inputs);
            if (settingsError is not null)
            {
                executions.Add(Task.FromResult(new TargetNodeResult(peer.PeerId, settingsError)));
                continue;
            }
            (executable, NodeResult? sourceError) = FileTransferHostBinding.Resolve(executable, directory, timeProvider.GetUtcNow());
            if (sourceError is not null)
            {
                executions.Add(Task.FromResult(new TargetNodeResult(peer.PeerId, sourceError)));
                continue;
            }
            RemoteExecutionRequest request = new(Guid.CreateVersion7(), causationId, workflow.Definition.Id, workflow.Revision, executable, inputs, peer.PeerId, peer.InstanceId,
                timeProvider.GetUtcNow() + admissionLifetime, trace.Enabled, workflow.ExecutionDefinition.ContainsPersonalData, operational?.Settings.TimeZoneId ?? timeZone.Id, operational, trace.Selection, ownership.Find(causationId)?.Hooks, IsValueRead: valueRead)
            {
                SourceScopes = ownership.Find(causationId)?.SourceScopes,
            };
            string progress = "control.progress";
            if (string.Equals(node.Type, progress, StringComparison.Ordinal) && ownership.Find(causationId)?.Progress is { } lifetime)
            {
                Guid scope = Guid.Empty;
                string? grouping = new NodeSettings(executable.Settings).ReadText("progressScope", "workflow");
                string iteration = "iteration";
                if (string.Equals(grouping, iteration, StringComparison.Ordinal))
                {
                    scope = iterationScope;
                }
                request = request with { Progress = lifetime.Register(scope, peer) };
            }
            requests.Add(request);
        }
        string? failurePolicy = new NodeSettings(node.Settings).ReadText("failurePolicy", "keep");
        bool compensate = registry.Find(node.Type) is ICompensableFileNode && string.Equals(failurePolicy, RollbackPolicy, StringComparison.Ordinal);
        if (compensate)
        {
            if (executions.Count != 0)
            {
                return Skipped(node.Id, "file.inputs", "全対象の入力が揃う前にファイル操作を開始できません。");
            }
            if (delivery.FileTransactions is null)
            {
                return Skipped(node.Id, "file.transactionMissing", "全対象のファイル補償が構成されていません。");
            }
            return (await delivery.FilesAsync(node.Id, causationId, requests, cancellationToken).ConfigureAwait(false)) with { SignalId = Guid.NewGuid(), PcSelection = target.Selection };
        }
        executions.AddRange(requests.Select(request => delivery.ExecuteAsync(request, cancellationToken)));
        TargetNodeResult[] results = await Task.WhenAll(executions).ConfigureAwait(false);
        EOutcome outcome = ExecutionOutcomes.Aggregate(results.Select(static targetResult => targetResult.Result.Outcome));
        if (outcome == EOutcome.Succeeded && results.Any(static result => result.Result.Outcome != EOutcome.Succeeded))
        {
            outcome = EOutcome.Failed;
        }
        bool awaitingEvent = results.Length != 0 && results.All(result => result.Result.ReasonCode is "input.eventPending");
        return new ExecutionFrame(node.Id, outcome, results, descriptor.Kind == ENodeKind.Source, SignalId: Guid.NewGuid(), PcSelection: target.Selection, AwaitingEvent: awaitingEvent);
    }

    /// <summary>未実行の理由を結果に残します。</summary>
    /// <returns>副作用を発生させない結果です。</returns>
    private static ExecutionFrame Skipped(string nodeId, string code, string message)
    {
        NodeResult result = NodeResult.Failure(code, message, EOutcome.NotExecuted);
        return new ExecutionFrame(nodeId, EOutcome.NotExecuted, [new TargetNodeResult(string.Empty, result)]);
    }
}
