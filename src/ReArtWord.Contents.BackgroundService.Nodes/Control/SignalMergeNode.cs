using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.State;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.State;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control;

/// <summary>別の受信事象から届いた開始信号を、クラスタ共有の一巡として合流します。</summary>
public sealed class SignalMergeNode : IActionNode
{
    private readonly INodeStateStore store;
    private readonly string mode;

    /// <summary>都度・先着・全入力の方式と、原子的な共有保存先を固定します。</summary>
    public SignalMergeNode(string mode, INodeStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.mode = mode switch { "each" or "first" or "all" => mode, _ => throw new ArgumentOutOfRangeException(nameof(mode)) };
        this.store = store;
        string schema = """{"type":"object","properties":{"$signalInputs":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":64,"x-connectable":false,"x-runtime-metadata":true}}}""";
        string first = "first";
        if (string.Equals(mode, first, StringComparison.Ordinal))
        {
            schema = """{"type":"object","properties":{"scope":{"type":"string","enum":["execution","events"],"default":"execution","x-connectable":false},"$signalInputs":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":64,"x-connectable":false,"x-runtime-metadata":true}}}""";
        }
        Descriptor = NodeSupport.Describe("control.signal." + mode, "Start signal merge", "Control", schema,
            inputs: Enumerable.Range(1, ConcatPorts.Maximum).Select(index => new PortDescriptor(ConcatPorts.Name(index), EPortValueKind.Json)).ToArray(),
            outputs: [new("start", EPortValueKind.Json)]);
    }

    /// <summary>信号専用の可変入力口と一つの開始信号です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>同じ信号の合流を複数PCへ重複配送しません。</summary>
    /// <returns>保存する構成の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (definition.PcExecution == EPcExecutionMode.All || definition.Target?.Scope == EExecutionScope.All
            || definition.Inputs?.Keys.Any(port => 0 < SignalMergePorts.Index(port)) == true)
        {
            issues.Add(new("signal.connections", "Use connected start signals and one execution peer for a signal merge.", definition.Id));
        }
        return issues;
    }

    /// <summary>受信一件を一度だけ記録し、保存結果不明で信号を再発行しません。</summary>
    /// <returns>発火した開始信号、待機、または保存の失敗です。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        string inputsKey = "$signalInputs", eventKey = "$signalEvent";
        if (!context.Settings.TryGetProperty(inputsKey, out JsonElement declared) || declared.ValueKind != JsonValueKind.Array)
        {
            return NodeResult.Failure("signal.connections", "The connected signal inputs are not fixed.", EOutcome.NotExecuted);
        }
        string scopeKey = "scope", events = "events", firstModeName = "first";
        if (string.Equals(mode, firstModeName, StringComparison.Ordinal)
            && !(context.Settings.TryGetProperty(scopeKey, out JsonElement configured) && string.Equals(configured.GetString(), events, StringComparison.Ordinal)))
        {
            return Result(context.Inputs.Any(pair => 0 < SignalMergePorts.Index(pair.Key) && pair.Value.Kind == EValueKind.Json && pair.Value.Json.ValueKind == JsonValueKind.True));
        }
        string[] names = declared.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        string[] arrived = context.Inputs.Where(pair => 0 < SignalMergePorts.Index(pair.Key) && pair.Value.Kind == EValueKind.Json && pair.Value.Json.ValueKind == JsonValueKind.True).Select(pair => pair.Key).ToArray();
        if (names.Length is < 1 or > 64 || names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(name => SignalMergePorts.Index(name) == 0)
            || arrived.Length != 1 || !names.Contains(arrived[0], StringComparer.Ordinal)
            || !context.Inputs.TryGetValue(eventKey, out NodeValue? eventValue) || eventValue.Kind != EValueKind.Json || eventValue.Json.ValueKind != JsonValueKind.String
            || !Guid.TryParse(eventValue.GetString(), out Guid eventId) || eventId == Guid.Empty)
        {
            return NodeResult.Failure("signal.input", "One confirmed signal event is required.", EOutcome.NotExecuted);
        }
        string each = "each";
        if (string.Equals(mode, each, StringComparison.Ordinal))
        {
            return Result(true);
        }
        string scope = NodeStateScope.Shared(context.WorkflowId), key = "runtime:signal-merge:" + context.NodeId;
        VersionedState? previous = await store.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            SignalRound[] versions = previous?.Value.Json.Deserialize<SignalRound[]>(ContractJson.Options) ?? [];
            SignalRound current = versions.FirstOrDefault(value => string.Equals(value.Revision, context.Revision, StringComparison.Ordinal)) ?? new(context.Revision, [], []);
            if (current.Events.Contains(eventId))
            {
                return Result(false);
            }
            HashSet<string> seen = new(current.Seen, StringComparer.Ordinal);
            bool first = seen.Count == 0;
            seen.Add(arrived[0]);
            bool complete = names.All(seen.Contains);
            bool firstMode = mode is "first";
            bool emit = firstMode ? first
                                  : complete;
            SignalRound next = new(context.Revision, complete ? []
                                                              : seen.ToArray(), [.. current.Events.TakeLast(127), eventId]);
            SignalRound[] updated = [.. versions.Where(value => !string.Equals(value.Revision, context.Revision, StringComparison.Ordinal)).TakeLast(3), next];
            StateWriteResult saved = await store.CompareExchangeAsync(scope, key, previous?.Version, ContractJson.ToValue(updated), cancellationToken).ConfigureAwait(false);
            if (saved.Applied)
            {
                return Result(emit);
            }
            if (!StateWriteConflicts.IsConflict(saved))
            {
                return NodeResult.Failure("signal.stateUnconfirmed", "Signal state could not be confirmed. No output signal was emitted.", saved.Outcome);
            }
            previous = saved.Current;
        }
        return NodeResult.Failure("signal.contention", "Signal state remained busy; no output signal was emitted.", EOutcome.NotExecuted);
    }

    /// <summary>待機中も受信自体は成功とし、開始信号の有無で後続を制御します。</summary>
    /// <returns>信号を発火する場合だけstartを含む結果です。</returns>
    private static NodeResult Result(bool emit) => new(EOutcome.Succeeded, "success", emit ? new Dictionary<string, NodeValue>(StringComparer.Ordinal) { ["start"] = NodeValue.FromObject(true) }
                                                                                           : new Dictionary<string, NodeValue>(StringComparer.Ordinal));

    /// <summary>入力の到着状態と直近の重複識別だけを保存します。</summary>
    private sealed record SignalRound(string Revision, IReadOnlyList<string> Seen, IReadOnlyList<Guid> Events);
}
