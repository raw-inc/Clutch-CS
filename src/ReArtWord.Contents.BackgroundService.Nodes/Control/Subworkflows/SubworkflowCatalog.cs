using System.Text.Json;
using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Nodes.Values;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.Nodes.Control.Subworkflows;

/// <summary>このクラスタの反映済みSubWorkflowを再利用レシピとして公開します。</summary>
public sealed class SubworkflowCatalog(NodeRegistry registry, Func<IReadOnlyList<CompiledWorkflow>> effective) : IDynamicNodeCatalog
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, (string Revision, SubworkflowCallNode Node)> cached = new(StringComparer.Ordinal);
    private readonly SubworkflowPortNode port = new();

    /// <summary>配置先のコンパイラーへ同じ有効版のレシピを渡します。</summary>
    /// <returns>再利用定義または未反映です。</returns>
    public WorkflowDefinition? Definition(string workflowId) => Current(workflowId);

    /// <summary>版が変わらなければ同じ公開契約を再利用します。</summary>
    public IReadOnlyList<INode> Nodes
    {
        get
        {
            CompiledWorkflow[] workflows = effective().Where(workflow => SubworkflowInterface.IsSubworkflow(workflow.Definition)).ToArray();
            using (gate.EnterScope())
            {
                foreach (string id in cached.Keys.Where(id => workflows.All(workflow => !string.Equals(workflow.Definition.Id, id, StringComparison.Ordinal))).ToArray())
                {
                    cached.Remove(id);
                }
                return workflows.Select(GetNode).Cast<INode>().ToArray();
            }
        }
    }

    /// <summary>固定済み子定義は、元Workflowが後で削除されても完了まで解決できます。</summary>
    /// <returns>組込みとは衝突しない呼出し実装です。</returns>
    public INode? Find(string type)
    {
        if (string.Equals(type, SubworkflowInterface.PortType, StringComparison.Ordinal))
        {
            return port;
        }
        string? id = SubworkflowInterface.Target(type);
        if (id is null)
        {
            return null;
        }
        IReadOnlyList<CompiledWorkflow> workflows = effective();
        CompiledWorkflow? workflow = workflows.FirstOrDefault(item => string.Equals(item.Definition.Id, id, StringComparison.Ordinal) && SubworkflowInterface.IsSubworkflow(item.Definition));
        using (gate.EnterScope())
        {
            if (workflows.Count < cached.Count)
            {
                HashSet<string> ids = workflows.Select(item => item.Definition.Id).ToHashSet(StringComparer.Ordinal);
                foreach (string stale in cached.Keys.Where(key => !ids.Contains(key)).ToArray())
                {
                    cached.Remove(stale);
                }
            }
            if (workflow is null)
            {
                cached.Remove(id);
            }
            return workflow is null ? new SubworkflowCallNode(this, id, null)
                                    : GetNode(workflow);
        }
    }

    /// <summary>公開項目の重複、自己・相互再帰を保存前に検出します。</summary>
    /// <returns>内部処理の実行を伴わない問題一覧です。</returns>
    public IReadOnlyList<ValidationIssue> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            Dictionary<string, WorkflowDefinition> available = Available();
            available[definition.Id] = definition;
            if (SubworkflowInterface.IsSubworkflow(definition))
            {
                _ = SubworkflowSnapshot.Capture(definition, available, registry);
                foreach (NodeDefinition input in definition.Nodes.Where(node => string.Equals(node.Type, SubworkflowInterface.InputType, StringComparison.Ordinal)))
                {
                    if (definition.Wires.Any(wire => string.Equals(wire.ToNode, input.Id, StringComparison.Ordinal)))
                    {
                        return [new("subworkflow.input.wiring", "Public inputs receive their values from the caller; do not wire their input side.", input.Id)];
                    }
                }
            }
            foreach (NodeDefinition node in definition.Nodes.Where(node => SubworkflowInterface.Target(node.Type) is not null))
            {
                string id = SubworkflowInterface.Target(node.Type)!;
                WorkflowDefinition? target = SubworkflowInterface.Captured(node) ?? available.GetValueOrDefault(id);
                if (target is not null)
                {
                    _ = SubworkflowSnapshot.Capture(target, available, registry);
                }
            }
            return [];
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return [new("subworkflow.definition.invalid", "SubWorkflow names, available references or non-recursive expansion limits are invalid.")];
        }
    }

    /// <summary>現在の反映済み定義だけを取得します。</summary>
    /// <returns>未反映・削除済み・通常Workflowならnullです。</returns>
    internal WorkflowDefinition? Current(string id) => effective().FirstOrDefault(workflow => string.Equals(workflow.Definition.Id, id, StringComparison.Ordinal)
        && workflow.DependencyIssues.Count == 0 && SubworkflowInterface.IsSubworkflow(workflow.Definition))?.Definition;

    /// <summary>現在のクラスタの定義一覧を一回分だけ固定します。</summary>
    /// <returns>公開境界を持つ反映済み定義です。</returns>
    private Dictionary<string, WorkflowDefinition> Available() => effective().Where(workflow => SubworkflowInterface.IsSubworkflow(workflow.Definition))
        .ToDictionary(workflow => workflow.Definition.Id, workflow => workflow.Definition, StringComparer.Ordinal);

    /// <summary>呼出し名と公開契約を同じ有効版に揃えます。</summary>
    /// <returns>版が変わった場合だけ新しく作る実装です。</returns>
    private SubworkflowCallNode GetNode(CompiledWorkflow workflow)
    {
        string id = workflow.Definition.Id;
        if (!cached.TryGetValue(id, out (string Revision, SubworkflowCallNode Node) existing) || !string.Equals(existing.Revision, workflow.Revision, StringComparison.Ordinal))
        {
            existing = (workflow.Revision, new SubworkflowCallNode(this, id, workflow.Definition));
            cached[id] = existing;
        }
        return existing.Node;
    }
}
