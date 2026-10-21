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

/// <summary>OFFとONの値を選択し、明示された切替信号だけで共有状態を反転します。</summary>
public sealed class ToggleButtonNode(INodeStateStore store) : IActionNode
{
    /// <summary>二つの候補値と表示文字列、選択値・状態・切替Signalを公開します。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe("control.toggleButton", "Toggle button", "Control", """{"type":"object","properties":{"$toggleSource":{"type":"object","required":["workflowId","nodeId"],"properties":{"workflowId":{"type":"string","minLength":1},"nodeId":{"type":"string","minLength":1}},"x-runtime-metadata":true,"x-connectable":false}}}""",
        inputs: [new("offValue"), new("onValue"), new("label", EPortValueKind.Json) { DataType = new("string", Optional: true) }],
        outputs: [new("value"), new("isOn", EPortValueKind.Json), new("start", EPortValueKind.Json)]) with
    {
        ContractVersion = 2, CompatibleContractVersions = [1],
        Description = "Select the OFF or ON input value. Press the button or supply a start signal to toggle; reading the selected value never toggles it.",
    };

    /// <summary>一件の切替を複数PCへ重複配送せず、共有状態を一箇所で比較更新します。</summary>
    /// <returns>設定と実行先の問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        if (definition.PcExecution == EPcExecutionMode.All || definition.Target?.Scope == EExecutionScope.All)
        {
            issues.Add(new("toggle.singleTarget", "Select one execution PC for a shared toggle button.", definition.Id));
        }
        return issues;
    }

    /// <summary>参照時は読み取りだけを行い、切替の保存確認後に一回だけSignalを返します。</summary>
    /// <returns>選択値と現在状態、保存が確認された切替だけのSignalです。</returns>
    public async ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return invalid;
        }
        ToggleButtonSource? source = ToggleButtonSource.Read(context.Settings);
        string scope = NodeStateScope.Shared(source?.WorkflowId ?? context.WorkflowId), key = ToggleButtonState.Key(source?.NodeId ?? context.NodeId);
        VersionedState? previous = await store.ReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        VersionedState? legacy = null;
        if (previous is null && source is not null)
        {
            // LEGACY-FORMAT: recipe-toggle-state-v1 — 定義側の状態が確定するまでは配置別の旧選択を保持します。
            legacy = await store.ReadAsync(NodeStateScope.Shared(context.WorkflowId), ToggleButtonState.Key(context.NodeId), cancellationToken).ConfigureAwait(false);
        }
        NodeValue? trigger = NodeSupport.Input(context, "trigger");
        bool toggle = !context.IsValueRead && trigger?.Kind == EValueKind.Json && trigger.Json.ValueKind == JsonValueKind.True;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            if (!ToggleButtonState.TryRead(previous?.Value ?? legacy?.Value, out ToggleButtonState state))
            {
                return NodeResult.Failure("toggle.stateInvalid", "The saved toggle state is invalid; it was not replaced.", EOutcome.NotExecuted);
            }
            if (!toggle || state.Events.Contains(context.CausationId))
            {
                return Result(context, state.IsOn, false);
            }
            ToggleButtonState next = new(!state.IsOn, [.. state.Events.TakeLast(127), context.CausationId]);
            StateWriteResult saved = await store.CompareExchangeAsync(scope, key, previous?.Version, ContractJson.ToValue(next), cancellationToken).ConfigureAwait(false);
            if (saved.Applied && saved.Outcome == EOutcome.Succeeded)
            {
                return Result(context, next.IsOn, true);
            }
            if (!StateWriteConflicts.IsConflict(saved))
            {
                return NodeResult.Failure("toggle.stateUnconfirmed", "The toggle change could not be confirmed; no change signal was emitted.", saved.Outcome);
            }
            previous = saved.Current;
        }
        return NodeResult.Failure("toggle.contention", "The toggle remained busy; no change signal was emitted.", EOutcome.NotExecuted);
    }

    /// <summary>候補値を変換せずに選択し、読み取り結果へ切替Signalを混ぜません。</summary>
    /// <returns>現在値と、実際に反転した場合だけのSignalです。</returns>
    private static NodeResult Result(NodeExecutionContext context, bool isOn, bool changed)
    {
        string port = isOn ? "onValue"
                           : "offValue";
        Dictionary<string, NodeValue> outputs = new(StringComparer.Ordinal)
        {
            ["isOn"] = NodeValue.FromObject(isOn),
        };
        if (NodeSupport.Input(context, port) is { } value)
        {
            outputs["value"] = value.Copy();
        }
        if (changed)
        {
            outputs["start"] = NodeValue.FromObject(true);
        }
        return new(EOutcome.Succeeded, "success", outputs);
    }
}
