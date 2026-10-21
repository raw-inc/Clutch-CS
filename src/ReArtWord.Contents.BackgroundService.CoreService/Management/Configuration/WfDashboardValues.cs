using System.Text.Json;
using System.Text.Json.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow.Dashboards;
using ReArtWord.Contents.BackgroundService.Infrastructure.Configuration.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management.Configuration;

/// <summary>元WFの型・配線を維持したValue編集を、既存の保存・共有・反映へ接続します。</summary>
internal sealed class WfDashboardValues(WorkflowStore workflows, NodeRegistry registry)
{
    private static string ValueNodeType => "value.constant";
    private static string ValuePort => "value";
    private static string TextSetting => "text";
    private static string StringType => "string";
    private static string AnyType => "any";

    /// <summary>外部処理を実行せず、既知の固定型と編集用表記だけを取得します。</summary>
    /// <returns>編集できる値、動的な設定や不正値ならnullです。</returns>
    internal ValueState? Read(WorkflowDefinition workflow, NodeDefinition node)
    {
        if (!string.Equals(node.Type, ValueNodeType, StringComparison.Ordinal) || registry.Find(node.Type) is not IValueNode implementation)
        {
            return null;
        }
        WorkflowSettingValues literals = new(workflow);
        if (literals.HasInput(node, SettingPorts.Name(TextSetting)))
        {
            return null;
        }
        Dictionary<string, NodeValue> inputs = new(StringComparer.Ordinal);
        string typePort = "type", inputTypePort = "type";
        if (literals.HasInput(node, SettingPorts.Name(typePort)))
        {
            typePort = SettingPorts.Name(typePort);
        }
        if (literals.HasInput(node, typePort))
        {
            NodeValue? input = literals.Input(node, typePort);
            if (input is null)
            {
                return null;
            }
            inputs.Add(inputTypePort, input);
        }
        NodeResult evaluated = implementation.Evaluate(node, inputs);
        if (evaluated.Outcome != EOutcome.Succeeded || !evaluated.Outputs.TryGetValue(ValuePort, out NodeValue? value))
        {
            return null;
        }
        ValueTypeDefinition? declared = null;
        if (inputs.TryGetValue(inputTypePort, out NodeValue? suppliedType))
        {
            declared = ValueTypeInference.Descriptor(suppliedType);
        }
        else
        {
            if (node.Settings.TryGetProperty(inputTypePort, out JsonElement configuredType) && configuredType.GetString() is { } typeName && ValueTypeInference.KnownName(typeName))
            {
                declared = new(typeName);
            }
        }
        bool inferred = declared is null || string.Equals(declared.Type, AnyType, StringComparison.Ordinal);
        ValueTypeDefinition? type = declared;
        if (inferred)
        {
            type = Type(value);
        }
        if (type is null || string.Equals(type.Type, AnyType, StringComparison.Ordinal))
        {
            return null;
        }
        string text = node.Settings.GetProperty(TextSetting).GetString()!;
        bool automatic = !inputs.ContainsKey(inputTypePort) && (!node.Settings.TryGetProperty(inputTypePort, out JsonElement kind) || kind.GetString() is "auto" or "any");
        if (automatic && string.Equals(type.Type, StringType, StringComparison.Ordinal))
        {
            text = value.GetString()!;
        }
        return 16384 < text.Length ? null
                                   : new(text, type, automatic, inferred, inputs);
    }

    /// <summary>取得版が変わった場合や型が変わる場合は、元WFへ書き込みません。</summary>
    /// <returns>通常の保存APIへ渡す比較版付き要求です。この処理自体は書き込みません。</returns>
    internal async Task<WfDashboardValuePreparation> PrepareAsync(WfDashboardValueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request.Reference);
        if (request.Text is null || 16384 < request.Text.Length || !string.Equals(request.Reference.NodeType, ValueNodeType, StringComparison.Ordinal))
        {
            return Rejected("dashboard.value.invalid", "Enter a value in the existing type.");
        }
        StoredWorkflow? stored = await workflows.GetAsync(request.Reference.WorkflowId, cancellationToken).ConfigureAwait(false);
        if (stored is null || !string.Equals(stored.Revision, request.Revision, StringComparison.Ordinal))
        {
            return Rejected("Workflow.RevisionConflict", "The source workflow changed. Refresh it before saving this value.");
        }
        NodeDefinition? node = request.Reference.Find(stored.Definition);
        ValueState? current = node is null ? null
                                           : Read(stored.Definition, node);
        if (node is null || current is null)
        {
            return Rejected("dashboard.value.unavailable", "This Value is unavailable or its type cannot be fixed. Edit it in the source workflow.");
        }
        string text = request.Text;
        if (current.Automatic && string.Equals(current.Type.Type, StringType, StringComparison.Ordinal))
        {
            text = JsonSerializer.Serialize(text);
        }
        JsonObject settings = JsonNode.Parse(node.Settings.GetRawText())!.AsObject();
        settings[TextSetting] = text;
        NodeDefinition edited = node with { Settings = JsonSerializer.SerializeToElement(settings) };
        NodeResult evaluated = ((IValueNode)registry.Find(node.Type)!).Evaluate(edited, current.Inputs);
        if (evaluated.Outcome != EOutcome.Succeeded || !evaluated.Outputs.TryGetValue(ValuePort, out NodeValue? value)
            || current.Inferred && !ValueTypeInference.Same(current.Type, Type(value)))
        {
            return Rejected("dashboard.value.type", "WFDashboard cannot change the Value type. Enter a value in the existing type.");
        }
        WorkflowDefinition definition = stored.Definition with
        {
            Nodes = stored.Definition.Nodes.Select(item => string.Equals(item.Id, node.Id, StringComparison.Ordinal) ? edited
                                                                                                                     : item).ToArray(),
        };
        return new(new(request.OperationId, definition, stored.Revision, stored.ActivationPolicy), []);
    }

    /// <summary>any指定も現在の実値で固定し、明示された整数幅などは維持します。</summary>
    /// <returns>保存前後で比較する型です。</returns>
    private static ValueTypeDefinition? Type(NodeValue value)
    {
        if (value.DataType is { } type && !string.Equals(type.Type, AnyType, StringComparison.Ordinal))
        {
            return type;
        }
        return ValueTypeInference.FromJson(value.Json, 0);
    }

    /// <summary>未確認を成功にせず、保存前の拒否を一つの結果で返します。</summary>
    /// <returns>副作用がない検証結果です。</returns>
    private static WfDashboardValuePreparation Rejected(string code, string message) => new(null, [new(code, message)]);

    /// <summary>入力開始時に固定する型と、元設定の解釈条件です。</summary>
    internal sealed record ValueState(string Text, ValueTypeDefinition Type, bool Automatic, bool Inferred, IReadOnlyDictionary<string, NodeValue> Inputs);
}
