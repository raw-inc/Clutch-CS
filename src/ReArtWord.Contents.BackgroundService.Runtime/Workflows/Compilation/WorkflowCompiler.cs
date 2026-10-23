using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Serialization;
using ReArtWord.Contents.BackgroundService.Contracts.Targets;
using ReArtWord.Contents.BackgroundService.Contracts.Values;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Files;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;
using ReArtWord.Contents.BackgroundService.Runtime.Targets;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

/// <summary>実行前にノード設定、接続、必須入力、実行場所と循環を検証します。</summary>
public sealed class WorkflowCompiler
{
    private readonly NodeRegistry registry;
    private static string TriggerPort => "trigger";
    private static string ResultPort => "result";
    private static string SourcePort => "source";
    private static string FileTransactionPort => "fileTransaction";
    private static HashSet<string> CommonRoutes
    {
        get;
    } = new(["*", "success", "failure", "notExecuted", "unknown", "cancelled"], StringComparer.Ordinal);

    private static HashSet<string> SourceRoutes
    {
        get;
    } = new(["starting", "listening", "completed"], StringComparer.Ordinal);

    /// <summary>ノードの実カタログを使用して検証します。</summary>
    public WorkflowCompiler(NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        this.registry = registry;
    }

    /// <summary>保存済み配置を実行せずに読み取り用レイアウトへ展開します。</summary>
    /// <returns>実行時と同じ配置識別子を持つ内部グラフです。</returns>
    public IReadOnlyList<WorkflowRecipeInstance> InspectRecipes(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return SubworkflowExpansion.Inspect(definition, registry);
    }

    /// <summary>定義を検証し、独立した設定版の実行計画を作成します。</summary>
    /// <returns>計画または具体的な検証エラーです。</returns>
    public CompilationResult Compile(WorkflowDefinition definition)
        => Compile(definition, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));

    /// <summary>一括取込み後の参照集合で検証し、稼働中のレシピカタログを変更しません。</summary>
    /// <returns>指定した集合だけで解決した計画または問題です。</returns>
    public CompilationResult CompileInContext(WorkflowDefinition definition, IReadOnlyDictionary<string, WorkflowDefinition>? definitions)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definitions is not null && definition.Nodes is not null && definition.Wires is not null
            && definition.Nodes.Any(node => node?.Type is not null && SubworkflowInterface.Target(node.Type) is not null))
        {
            return SubworkflowExpansion.Compile(definition, registry, Compile, definitions);
        }
        return Compile(definition);
    }

    /// <summary>展開した公開入力を反復内の配置へ引き継いで検証します。</summary>
    /// <returns>通常の配線とレシピの反復範囲を照合した計画です。</returns>
    private CompilationResult Compile(WorkflowDefinition definition, IReadOnlyDictionary<string, IReadOnlyList<string>> recipeInputs)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.IsDashboard)
        {
            return WfDashboardCompiler.Compile(definition);
        }
        if (definition.Nodes is not null && definition.Wires is not null
            && definition.Nodes.Any(node => node is not null && node.Type is not null && SubworkflowInterface.Target(node.Type) is not null))
        {
            return SubworkflowExpansion.Compile(definition, registry, Compile);
        }
        List<ValidationIssue> issues = [];
        if (definition.ActivationRules?.Any(rule => !Enum.IsDefined(rule.Key) || !Enum.IsDefined(rule.Value)) == true)
        {
            issues.Add(new ValidationIssue("workflow.activationRules", "変更種類または反映方針が不正です。"));
        }
        if (definition.TraceSelection?.RawByteLimit is <= 0)
        {
            issues.Add(new ValidationIssue("trace.limit", "通信内容の保存上限は正数、または全内容を表す未指定にしてください。"));
        }
        if (string.IsNullOrWhiteSpace(definition.Id) || definition.Nodes is null || definition.Wires is null)
        {
            return new CompilationResult(null, [new ValidationIssue("workflow.required", "識別子、ノード一覧、接続一覧が必要です。")]);
        }
        Dictionary<string, NodeDefinition> nodes = new(StringComparer.Ordinal);
        issues.AddRange(registry.Dynamic?.Validate(definition) ?? []);
        WorkflowSettingValues settings = new(definition);
        foreach (NodeDefinition node in definition.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Type))
            {
                issues.Add(new ValidationIssue("node.required", "ノードの識別子と種類が必要です。"));
                continue;
            }
            NodeDefinition configured = settings.Resolve(node);
            if (!nodes.TryAdd(node.Id, configured))
            {
                issues.Add(new ValidationIssue("node.duplicate", "ノード識別子が重複しています。", node.Id));
                continue;
            }
            ValidateNode(configured, issues);
        }
        foreach (NodeDefinition node in nodes.Values)
        {
            if (node.Safety?.Group is string group && nodes.ContainsKey(group))
            {
                issues.Add(new ValidationIssue("safety.scopeCollision", "安全Group名とNode識別子は異なる名前にしてください。", node.Id));
            }
        }
        NodeDescriptor[] descriptors = nodes.Values.Where(node => registry.Find(node.Type) is not null).Select(node =>
        {
            INode implementation = registry.Find(node.Type)!;
            NodeDescriptor descriptor = implementation.Descriptor;
            if (implementation is ICompensableFileNode && !descriptor.Outputs.Any(port => string.Equals(port.Name, FileTransactionPort, StringComparison.Ordinal)))
            {
                descriptor = descriptor with { Outputs = [.. descriptor.Outputs, new(FileTransactionPort, EPortValueKind.Json)] };
            }
            return descriptor;
        }).DistinctBy(descriptor => descriptor.Type).ToArray();
        WorkflowConnectionInspection inspection = new(definition, descriptors);
        Dictionary<string, List<WireDefinition>> incoming = nodes.Keys.ToDictionary(static id => id, static _ => new List<WireDefinition>(), StringComparer.Ordinal);
        foreach (WireDefinition wire in definition.Wires)
        {
            ValidateWire(wire, nodes, incoming, issues, inspection);
        }
        ValidateRequiredInputs(nodes, incoming, issues);
        List<NodeDefinition> ordered = Sort(nodes, incoming);
        if (ordered.Count != nodes.Count)
        {
            issues.Add(new ValidationIssue("wire.cycle", "一件の処理内に循環があります。繰返しはタイマーやイベント入力で構成してください。"));
        }
        if (issues.Count != 0)
        {
            foreach (NodeDefinition node in nodes.Values)
            {
                NodeDescriptor? descriptor = ConfiguredNodePorts.Resolve(registry.Find(node.Type)?.Descriptor, node);
                bool connected = (node.Inputs?.Keys.Any(SettingsBinding.IsPort) ?? false)
                    || definition.Wires.Any(wire => wire is not null && string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal) && wire.ToPort is not null && SettingsBinding.IsPort(wire.ToPort));
                if (descriptor is not null && !connected && NodeBehaviorContracts.Validate(node, descriptor) is null)
                {
                    issues.AddRange(NodeSchemaValidator.Validate(node, descriptor));
                }
            }
            return new CompilationResult(null, issues.AsReadOnly());
        }

        WorkflowDefinition copied = ContractJson.Copy(definition);
        Dictionary<string, NodeDefinition> copies = copied.Nodes.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        Dictionary<string, IReadOnlyList<WireDefinition>> connections = copied.Nodes.ToDictionary(
            static node => node.Id,
            node => (IReadOnlyList<WireDefinition>)Array.AsReadOnly(copied.Wires.Where(wire => string.Equals(wire.ToNode, node.Id, StringComparison.Ordinal)).ToArray()),
            StringComparer.Ordinal);
        CompiledWorkflow workflow = new(copied, ContractJson.Hash(copied), ordered.Select(node => copies[node.Id]).ToArray(), connections);
        CompilationResult bound = ValueBindingCompiler.Bind(workflow, registry);
        if (bound.Workflow is null)
        {
            return bound;
        }
        foreach (WireDefinition wire in definition.Wires)
        {
            if (!WorkflowConnectionTypes.CanConnect(inspection, wire))
            {
                issues.Add(new("wire.valueType", "The known output type does not match the destination input. Use an explicit conversion.", wire.ToNode, wire.ToPort));
            }
        }
        if (issues.Count != 0)
        {
            return new(null, issues);
        }
        return IterationCompiler.Bind(bound.Workflow, registry, recipeInputs);
    }

    /// <summary>ノード固有の設定と場所指定を検証します。</summary>
    private void ValidateNode(NodeDefinition node, List<ValidationIssue> issues)
    {
        INode? implementation = registry.Find(node.Type);
        if (implementation is null)
        {
            issues.Add(new ValidationIssue("node.unknown", "登録されていないノード種類です。", node.Id));
            return;
        }
        if (NodeBehaviorContracts.Validate(node, implementation.Descriptor) is { } incompatible)
        {
            issues.Add(incompatible);
        }
        if (node.Settings.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            issues.Add(new ValidationIssue("settings.object", "設定はオブジェクトで指定してください。", node.Id));
        }
        if (implementation.Descriptor.Inputs.Any(port => SettingsBinding.IsPort(port.Name)))
        {
            issues.Add(new ValidationIssue("settings.port.reserved", "設定接続の予約prefixを通常入力に宣言できません。", node.Id));
        }
        if (!Enum.IsDefined(node.Overlap))
        {
            issues.Add(new ValidationIssue("node.overlap", "重複実行方針が定義されていません。", node.Id));
        }
        if (node.Safety is not null && (!Enum.IsDefined(node.Safety.Inputs) || (node.Safety.Confirmation is not null && !Enum.IsDefined(node.Safety.Confirmation.Value))))
        {
            issues.Add(new ValidationIssue("node.safety", "安全停止または操作前確認の方針が定義されていません。", node.Id));
        }
        if (node.StartOnLoad && implementation.Descriptor.Kind != ENodeKind.Source)
        {
            issues.Add(new ValidationIssue("node.startOnLoad", "自動開始はSourceに指定し、有限処理は起動や事象のSourceから接続してください。", node.Id));
        }
        WorkflowTargetValidation.Validate(node, issues);
        if (node.Inputs is not null)
        {
            NodeDescriptor configured = ConfiguredNodePorts.ResolveExecution(implementation.Descriptor, node)!;
            foreach (KeyValuePair<string, NodeValue> constant in node.Inputs)
            {
                string port = constant.Key;
                if (!HasInput(configured, port))
                {
                    issues.Add(new ValidationIssue("input.unknown", "登録されていない入力ポートです。", node.Id, port));
                }
                PortDescriptor? descriptor = configured.Inputs.FirstOrDefault(item => string.Equals(item.Name, port, StringComparison.Ordinal));
                if (PcSelectionPorts.AllowsMultiple(implementation.Descriptor, port))
                {
                    issues.Add(new("selection.constant", "PC選択は定数入力ではなく選択ノードから接続してください。", node.Id, port));
                }
                if (constant.Value is null || (descriptor?.ValueKind == EPortValueKind.Binary && constant.Value.Kind != EValueKind.Binary)
                    || ((descriptor?.ValueKind == EPortValueKind.Json || SettingsBinding.IsPort(port)) && constant.Value.Kind != EValueKind.Json))
                {
                    issues.Add(new ValidationIssue("input.type", "定数の種類と入力ポートの種類が一致しません。", node.Id, port));
                }
            }
        }
    }

    /// <summary>値を一つの供給元だけから受け取る接続を検証します。</summary>
    private void ValidateWire(WireDefinition wire, Dictionary<string, NodeDefinition> nodes, Dictionary<string, List<WireDefinition>> incoming, List<ValidationIssue> issues, WorkflowConnectionInspection inspection)
    {
        if (wire is null || string.IsNullOrWhiteSpace(wire.FromNode) || string.IsNullOrWhiteSpace(wire.ToNode) || string.IsNullOrWhiteSpace(wire.FromPort) || string.IsNullOrWhiteSpace(wire.ToPort)
            || !nodes.TryGetValue(wire.FromNode, out NodeDefinition? from) || !nodes.TryGetValue(wire.ToNode, out NodeDefinition? to))
        {
            issues.Add(new ValidationIssue("wire.node", "接続先または接続元ノードが存在しません。"));
            return;
        }
        incoming[to.Id].Add(wire);
        NodeDescriptor? output = inspection.GetPorts(from.Id);
        NodeDescriptor? input = inspection.GetPorts(to.Id);
        if (output is null || input is null)
        {
            return;
        }
        if (!string.Equals(wire.FromPort, ResultPort, StringComparison.Ordinal)
            && !(registry.Find(from.Type) is ICompensableFileNode && string.Equals(wire.FromPort, FileTransactionPort, StringComparison.Ordinal))
            && !(output.Kind == ENodeKind.Source && string.Equals(wire.FromPort, SourcePort, StringComparison.Ordinal))
            && !output.Outputs.Any(port => string.Equals(port.Name, wire.FromPort, StringComparison.Ordinal)))
        {
            issues.Add(new ValidationIssue("wire.output", "出力ポートが存在しません。", from.Id, wire.FromPort));
        }
        if (!HasInput(input, wire.ToPort))
        {
            issues.Add(new ValidationIssue("wire.input", "入力ポートが存在しません。", to.Id, wire.ToPort));
        }
        if (wire.When is null || (!CommonRoutes.Contains(wire.When) && !output.Routes.Contains(wire.When, StringComparer.Ordinal)
            && !(output.Kind == ENodeKind.Source && SourceRoutes.Contains(wire.When))))
        {
            issues.Add(new ValidationIssue("wire.route", "出力ノードに存在しない結果経路です。", from.Id, wire.FromPort));
        }
        PortDescriptor? outputPort = output.Outputs.FirstOrDefault(port => string.Equals(port.Name, wire.FromPort, StringComparison.Ordinal));
        PortDescriptor? inputPort = input.Inputs.FirstOrDefault(port => string.Equals(port.Name, wire.ToPort, StringComparison.Ordinal));
        if (wire.Path is null && outputPort is not null && inputPort is not null
            && outputPort.ValueKind != EPortValueKind.Any && inputPort.ValueKind != EPortValueKind.Any && outputPort.ValueKind != inputPort.ValueKind)
        {
            issues.Add(new ValidationIssue("wire.type", "接続する値の種類が一致しません。変換ノードを使用してください。", to.Id, wire.ToPort));
        }
        if ((to.Inputs?.ContainsKey(wire.ToPort) ?? false) || (!PcSelectionPorts.AllowsMultiple(input, wire.ToPort)
            && 1 < incoming[to.Id].Count(item => string.Equals(item.ToPort, wire.ToPort, StringComparison.Ordinal)))
            || 1 < incoming[to.Id].Count(item => item == wire))
        {
            issues.Add(new ValidationIssue("input.multiple", "一つの入力には定数または一つの接続だけを指定してください。", to.Id, wire.ToPort));
        }
        if (wire.Path is not null && !JsonPointer.IsValid(wire.Path))
        {
            issues.Add(new ValidationIssue("wire.path", "抽出パスはJSON Pointerで指定してください。", to.Id, wire.ToPort));
        }
    }

    /// <summary>必須入力が定数または接続から供給されることを検証します。</summary>
    private void ValidateRequiredInputs(Dictionary<string, NodeDefinition> nodes, Dictionary<string, List<WireDefinition>> incoming, List<ValidationIssue> issues)
    {
        foreach (NodeDefinition node in nodes.Values)
        {
            NodeDescriptor? descriptor = ConfiguredNodePorts.ResolveExecution(registry.Find(node.Type)?.Descriptor, node);
            if (descriptor is null)
            {
                continue;
            }
            foreach (PortDescriptor port in descriptor.Inputs.Where(static port => port.Required))
            {
                if (!(node.Inputs?.ContainsKey(port.Name) ?? false) && !incoming[node.Id].Any(wire => string.Equals(wire.ToPort, port.Name, StringComparison.Ordinal)))
                {
                    issues.Add(new ValidationIssue("input.required", "必須入力の供給元がありません。", node.Id, port.Name));
                }
            }
        }
    }

    /// <summary>共通の実行契機ポートと固有入力を判定します。</summary>
    /// <returns>宣言された入力かを示します。</returns>
    private static bool HasInput(NodeDescriptor descriptor, string name)
    {
        if (SettingsBinding.IsPort(name))
        {
            return SettingPorts.IsDeclared(descriptor, name);
        }
        return string.Equals(name, TriggerPort, StringComparison.Ordinal)
            || string.Equals(name, PcSelectionPorts.Execution, StringComparison.Ordinal)
            || descriptor.Inputs.Any(port => string.Equals(port.Name, name, StringComparison.Ordinal));
    }

    /// <summary>同じ親から複数入力があっても一度だけ依存を数えます。</summary>
    /// <returns>循環部分を除いた依存順です。</returns>
    private static List<NodeDefinition> Sort(Dictionary<string, NodeDefinition> nodes, Dictionary<string, List<WireDefinition>> incoming)
    {
        Dictionary<string, int> remaining = incoming.ToDictionary(static item => item.Key, static item => item.Value.Select(static wire => wire.FromNode).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        Queue<string> ready = new(remaining.Where(static item => item.Value == 0).Select(static item => item.Key));
        List<NodeDefinition> ordered = [];
        while (ready.TryDequeue(out string? id))
        {
            ordered.Add(nodes[id]);
            foreach (KeyValuePair<string, List<WireDefinition>> child in incoming)
            {
                if (child.Value.Any(wire => string.Equals(wire.FromNode, id, StringComparison.Ordinal)))
                {
                    remaining[child.Key]--;
                    if (remaining[child.Key] == 0)
                    {
                        ready.Enqueue(child.Key);
                    }
                }
            }
        }
        return ordered;
    }
}
