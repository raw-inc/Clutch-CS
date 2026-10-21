using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;

/// <summary>SubWorkflowの一引数または一返却値を、同じ型・デフォルト規則で検証します。</summary>
public sealed class SubworkflowBoundaryNode(bool output) : IActionNode
{
    /// <summary>公開契約はノード内で定義し、呼出しごとの値を通常入力から受け取ります。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    } = NodeSupport.Describe(output ? SubworkflowInterface.OutputType
                                    : SubworkflowInterface.InputType,
        output ? "SubWorkflow output"
               : "SubWorkflow input", "SubWorkflows",
        """{"type":"object","required":["name","valueType"],"properties":{"name":{"type":"string","minLength":1,"maxLength":128,"x-connectable":false},"valueType":{"type":"object","x-type-definition":true,"x-connectable":false,"default":{"type":"string"}},"hasDefault":{"type":"boolean","default":false,"x-connectable":false},"defaultValue":{"x-visibleWhen":{"property":"hasDefault","values":[true]},"x-connectable":false}}}""",
        inputs: [new("value") { IsLegacy = !output }], outputs: [new("value") { IsLegacy = output }]) with { IsValueOperation = !output, IsTerminal = output };

    /// <summary>型定義とデフォルト値を保存時にも検証します。</summary>
    /// <returns>公開項目の具体的な問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<ValidationIssue> issues = [.. NodeSupport.Validate(definition, Descriptor)];
        try
        {
            SubworkflowField field = SubworkflowInterface.ReadField(definition);
            SubworkflowValues.ValidateType(field.Type);
            if (field.DefaultValue is not null)
            {
                SubworkflowValues.ValidateValue(field.DefaultValue, field.Type);
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            issues.Add(new("subworkflow.field.invalid", "Choose a unique public name, a valid type, and a matching default value when enabled.", definition.Id));
        }
        return issues;
    }

    /// <summary>供給されたnullをデフォルトへ置換せず、入力自体の欠落だけを補います。</summary>
    /// <returns>型に適合した値、または未供給・型不適合です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        NodeResult? invalid = NodeSupport.Invalid(context, this);
        if (invalid is not null)
        {
            return ValueTask.FromResult(invalid);
        }
        try
        {
            SubworkflowField field = SubworkflowInterface.ReadField(new(context.NodeId, Descriptor.Type, context.Settings));
            NodeValue? value = context.Inputs.GetValueOrDefault("value") ?? field.DefaultValue;
            if (value is null)
            {
                return ValueTask.FromResult(NodeResult.Failure("subworkflow.input.required", "The required public value was not supplied.", EOutcome.NotExecuted));
            }
            SubworkflowValues.ValidateValue(value, field.Type);
            return ValueTask.FromResult(NodeResult.Success(PathTypeMetadata.Apply(value, field.Type)));
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return ValueTask.FromResult(NodeResult.Failure("subworkflow.value.type", "The value does not match the public type."));
        }
    }
}
