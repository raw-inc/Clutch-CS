using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Debugging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Debugging;

/// <summary>一つの要求と派生実行だけを有限メモリで追跡し、診断の都合で本体を再実行しません。</summary>
public sealed class WorkflowDebugCapture
{
    private readonly Lock gate = new();
    private readonly WorkflowDebugArm arm;
    private readonly CompiledWorkflow workflow;
    private readonly NodeRegistry registry;
    private readonly TimeProvider clock;
    private readonly string peer;
    private readonly Guid instance;
    private readonly Guid root;
    private readonly List<WorkflowDebugNode> nodes = [];
    private readonly Dictionary<Guid, Guid> parents = [];
    private EWorkflowDebugState state = EWorkflowDebugState.Recording;
    private string? reason;
    private long bytes;
    private bool truncated;
    private EOutcome? overall;

    /// <summary>開始時の設定版と所有実体を固定します。</summary>
    internal WorkflowDebugCapture(WorkflowDebugArm arm, CompiledWorkflow workflow, NodeRegistry registry, Guid root, string peer, Guid instance, TimeProvider clock)
    {
        this.arm = arm;
        this.workflow = workflow;
        this.registry = registry;
        this.root = root;
        this.peer = peer;
        this.instance = instance;
        this.clock = clock;
    }

    /// <summary>合流の子実行を元の要求へ対応付けます。</summary>
    internal void Child(Guid parent, Guid child)
    {
        using (gate.EnterScope())
        {
            if (parents.Count < 4096)
            {
                parents.TryAdd(child, parent);
            }
        }
    }

    /// <summary>生データを残さず、元入力や合流の到着結果を記録します。</summary>
    internal void Source(ExecutionFrame frame, Guid execution)
    {
        if (Accepting())
        {
            Add(Finished(frame, execution, "source", Guid.NewGuid(), clock.GetUtcNow(), new Dictionary<string, string>(StringComparer.Ordinal), []));
        }
    }

    /// <summary>現在の入力を記録して通常処理を一度だけ呼びます。</summary>
    /// <returns>診断前と同じ実行結果です。</returns>
    // 抑制理由: 診断は例外の種類だけを記録し、既存の実行境界へ同じ例外を伝えます。
    [System.Diagnostics.CodeAnalysis.SuppressMessage("SourceQuality", "RAW330", Justification = "本体の例外を診断のために置換または成功化しません。")]
    internal async Task<ExecutionFrame> ExecuteAsync(NodeDefinition node, ExecutionFrame[] frames, Guid execution,
        IReadOnlyDictionary<string, NodeValue>? initial, bool entry, bool valueRead, Func<Task<ExecutionFrame>> execute, CancellationToken cancellationToken = default)
    {
        if (!Accepting())
        {
            return await execute().ConfigureAwait(false);
        }
        if (!entry)
        {
            initial = null;
        }
        Guid invocation = Guid.NewGuid();
        DateTimeOffset started = clock.GetUtcNow();
        Dictionary<string, ExecutionFrame> supplied = frames.ToDictionary(frame => frame.NodeId, StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        List<WorkflowDebugConnection> connections = [];
        foreach (WireDefinition wire in workflow.Incoming[node.Id].Take(64))
        {
            ExecutionFrame parent = supplied[wire.FromNode];
            connections.Add(new(wire.FromNode, wire.FromPort, wire.ToPort, wire.When, false, parent.ConfigurationOnly || parent.RetainedValue, parent.SignalId, wire.Path));
        }
        try
        {
            IReadOnlyDictionary<string, NodeValue> values = PureValueInputs.Create(registry, node, string.Empty, workflow, supplied, initial,
                ignoreTrigger: node.Type is "control.toggleButton" && (valueRead || entry)).Inputs;
            inputs = WorkflowDebugValues.Summarize(values);
            for (int connectionIndex = 0; connectionIndex < connections.Count; connectionIndex++)
            {
                WireDefinition wire = workflow.Incoming[node.Id][connectionIndex];
                IReadOnlyDictionary<string, NodeValue> routed = PureValueInputs.Create(registry, node with { Inputs = null }, string.Empty, workflow, supplied, null,
                    ignoreTrigger: node.Type is "control.toggleButton" && (valueRead || entry), incoming: [wire]).Inputs;
                connections[connectionIndex] = connections[connectionIndex] with { Available = routed.ContainsKey(wire.ToPort) };
            }
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["…"] = "Input summary unavailable: " + error.GetType().Name };
        }
        int index = Add(new(0, invocation, execution, Parent(execution), node.Id, node.Type, "node", started, null, null, null, inputs,
            new Dictionary<string, string>(StringComparer.Ordinal), connections));
        try
        {
            ExecutionFrame result = await execute().ConfigureAwait(false);
            Replace(index, Finished(result, execution, "node", invocation, started, inputs, connections));
            return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Replace(index, new(0, invocation, execution, Parent(execution), node.Id, node.Type, "node", started, clock.GetUtcNow(), EOutcome.Unknown,
                error.GetType().Name, inputs, new Dictionary<string, string>(StringComparer.Ordinal), connections));
            throw;
        }
    }

    /// <summary>未実行の枝や反復の集約も残し、データがない理由を見えるようにします。</summary>
    internal void Results(Guid execution, IEnumerable<ExecutionFrame> frames)
    {
        foreach (ExecutionFrame frame in frames)
        {
            if (!Accepting())
            {
                return;
            }
            bool missing;
            using (gate.EnterScope())
            {
                missing = !nodes.Any(node => node.ExecutionId == execution && string.Equals(node.NodeId, frame.NodeId, StringComparison.Ordinal));
            }
            if (missing || frame.Iteration is not null)
            {
                Add(Finished(frame, execution, "summary", Guid.NewGuid(), clock.GetUtcNow(), new Dictionary<string, string>(StringComparer.Ordinal), []));
            }
        }
    }

    /// <summary>実処理の完了・取消と記録停止を別々に扱います。</summary>
    public void Complete(string? problem = null, EOutcome? outcome = null)
    {
        using (gate.EnterScope())
        {
            if (state != EWorkflowDebugState.Recording)
            {
                return;
            }
            state = EWorkflowDebugState.Completed;
            reason = problem;
            overall = outcome;
        }
    }

    /// <summary>通信上限を超えないページに分け、記録本体は一時領域へ保持します。</summary>
    /// <returns>同じ記録の一ページです。</returns>
    public WorkflowDebugView Read(WorkflowDebugQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        using (gate.EnterScope())
        {
            int offset = Math.Clamp(query.Offset, 0, nodes.Count);
            List<WorkflowDebugNode> page = [];
            long size = 0;
            foreach (WorkflowDebugNode node in nodes.Skip(offset).Take(Math.Clamp(query.Limit, 1, 50)))
            {
                size += JsonSerializer.SerializeToUtf8Bytes(node).Length;
                if (131072 < size && page.Count != 0)
                {
                    break;
                }
                page.Add(node);
            }
            int? next = null;
            if (offset + page.Count < nodes.Count)
            {
                next = offset + page.Count;
            }
            return new(arm, state, root, workflow.Revision, peer, instance, reason, page, nodes.Count, next, truncated, overall);
        }
    }

    /// <summary>親識別子を処理中にも同じ排他で参照します。</summary>
    /// <returns>元実行はnull、合流からの実行は親の識別子です。</returns>
    private Guid? Parent(Guid execution)
    {
        using (gate.EnterScope())
        {
            return parents.TryGetValue(execution, out Guid parent) ? parent
                                                                   : null;
        }
    }

    /// <summary>複数PCの成否を隠さず、各出力を短い値へ変換します。</summary>
    /// <returns>実結果を保持した診断行です。</returns>
    private WorkflowDebugNode Finished(ExecutionFrame frame, Guid execution, string stage, Guid invocation, DateTimeOffset started,
        IReadOnlyDictionary<string, string> inputs, IReadOnlyList<WorkflowDebugConnection> connections)
    {
        Dictionary<string, string> outputs = new(StringComparer.Ordinal);
        foreach (TargetNodeResult target in frame.Targets.Take(16))
        {
            outputs[target.PeerId + ":result"] = target.Result.Outcome + " / " + WorkflowDebugValues.Text(target.Result.ReasonCode ?? target.Result.Route, 256)
                + " / " + WorkflowDebugValues.Value(NodeValue.FromObject(target.Result.Message));
            foreach ((string key, string value) in WorkflowDebugValues.Summarize(target.Result.Outputs))
            {
                if (outputs.Count < 32)
                {
                    outputs[target.PeerId + ":" + key] = value;
                }
            }
        }
        string? code = frame.Targets.Select(target => target.Result.ReasonCode).FirstOrDefault(value => value is not null);
        return new(0, invocation, execution, Parent(execution), frame.NodeId, workflow.FindNode(frame.NodeId)!.Type, stage, started, clock.GetUtcNow(), frame.Outcome, code, inputs, outputs, connections);
    }

    /// <summary>一要求の保持件数とバイト数を制限し、超過を明示します。</summary>
    /// <returns>更新対象の位置、保持できない場合は-1です。</returns>
    private int Add(WorkflowDebugNode node)
    {
        using (gate.EnterScope())
        {
            long size = JsonSerializer.SerializeToUtf8Bytes(node).Length;
            bool recording = state == EWorkflowDebugState.Recording;
            if (!recording)
            {
                return -1;
            }
            if (4096 <= nodes.Count || 4194304 < bytes + size)
            {
                truncated = true;
                return -1;
            }
            bytes += size;
            nodes.Add(node with { Sequence = nodes.Count + 1 });
            return nodes.Count - 1;
        }
    }

    /// <summary>停止・上限後は要約作成自体も省略し、業務への負荷を増やしません。</summary>
    /// <returns>追加の値要約が必要かです。</returns>
    private bool Accepting()
    {
        using (gate.EnterScope())
        {
            return state == EWorkflowDebugState.Recording && !truncated;
        }
    }

    /// <summary>処理中の同じ行を完了へ変え、上限超過で実行結果を捏造しません。</summary>
    private void Replace(int index, WorkflowDebugNode node)
    {
        using (gate.EnterScope())
        {
            if (index < 0)
            {
                return;
            }
            long size = JsonSerializer.SerializeToUtf8Bytes(node).Length;
            long previous = JsonSerializer.SerializeToUtf8Bytes(nodes[index]).Length;
            if (4194304 < bytes - previous + size)
            {
                truncated = true;
                node = node with { Outputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["…"] = "Capture size limit" } };
                size = JsonSerializer.SerializeToUtf8Bytes(node).Length;
            }
            bytes += size - previous;
            nodes[index] = node with { Sequence = index + 1 };
        }
    }
}
