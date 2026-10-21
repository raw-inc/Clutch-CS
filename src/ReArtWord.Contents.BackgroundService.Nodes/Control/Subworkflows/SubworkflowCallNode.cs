using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;

/// <summary>再利用レシピの公開契約と、配置を介さない直接実行の境界です。</summary>
internal sealed class SubworkflowCallNode : IActionNode
{
    private readonly SubworkflowCatalog owner;
    private readonly string target;

    /// <summary>公開項目だけをレシピへ出し、固定された内部定義は利用者向け設定に表示しません。</summary>
    internal SubworkflowCallNode(SubworkflowCatalog owner, string target, WorkflowDefinition? definition)
    {
        this.owner = owner;
        this.target = target;
        Descriptor = NodeSupport.Describe(SubworkflowInterface.CallType(target), definition?.Name ?? target, "SubWorkflows",
            """{"type":"object","properties":{"$subworkflowDefinition":{"type":"object","x-runtime-metadata":true,"x-connectable":false}}}""",
            inputs: [], outputs: []) with { Description = "Reuse this node group in the containing workflow." };
        if (definition is not null)
        {
            Descriptor = SubworkflowRecipeDescriptor.Create(definition);
        }
    }

    /// <summary>同じクラスタの反映済み公開契約です。</summary>
    public NodeDescriptor Descriptor
    {
        get;
    }

    /// <summary>保存された呼出し先と、内部で固定した定義の同一性を確認します。</summary>
    /// <returns>呼出し前に検出した問題です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(NodeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            WorkflowDefinition? captured = SubworkflowInterface.Captured(definition);
            if (captured is null && owner.Current(target) is null)
            {
                return [new("subworkflow.target.unavailable", "The SubWorkflow has not been activated in this cluster.", definition.Id)];
            }
            if (captured is not null && !string.Equals(captured.Id, target, StringComparison.Ordinal))
            {
                return [new("subworkflow.target.mismatch", "The captured definition belongs to another SubWorkflow.", definition.Id)];
            }
            return NodeSupport.Validate(definition, Descriptor);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return [new("subworkflow.definition.invalid", "The SubWorkflow definition is invalid.", definition.Id)];
        }
    }

    /// <summary>通常のグラフ展開を経由しない直接実行を拒否します。</summary>
    /// <returns>配置先を指定するための未実行結果です。</returns>
    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(NodeResult.Failure("subworkflow.inlineRequired", "Place the recipe in a workflow before executing it.", EOutcome.NotExecuted));
    }
}
