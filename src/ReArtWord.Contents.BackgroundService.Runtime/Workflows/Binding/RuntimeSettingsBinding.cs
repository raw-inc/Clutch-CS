using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;
using ReArtWord.Contents.BackgroundService.Runtime.Nodes;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Binding;

/// <summary>動的な操作設定を対象Peerごとに固定し、許可検査や補償準備より先に検証します。</summary>
internal static class RuntimeSettingsBinding
{
    /// <summary>動的設定と項目間の制約は供給後に検証し、上書きされる保存値で判定しません。</summary>
    /// <returns>未接続の必須設定・型・範囲は開始前に検証し、固有検証は解決後かつ配送前に必ず行います。</returns>
    internal static IReadOnlyList<ValidationIssue> Validate(NodeDefinition node, INode implementation, IReadOnlyList<WireDefinition> deferred)
    {
        HashSet<string> paths = deferred.Select(wire => "/" + SettingPorts.Property(wire.ToPort)).ToHashSet(StringComparer.Ordinal);
        bool Keep(ValidationIssue issue) => issue.Path is null || !paths.Any(path => string.Equals(issue.Path, path, StringComparison.Ordinal) || issue.Path.StartsWith(path + "/", StringComparison.Ordinal));
        List<ValidationIssue> issues = NodeSchemaValidator.Validate(node, implementation.Descriptor).Where(Keep).ToList();
        if (issues.Count == 0 && deferred.Count == 0)
        {
            issues.AddRange(implementation.Validate(node));
        }
        return issues;
    }

    /// <summary>未供給の接続を保存値へ戻さず、一回の処理用設定へ重ねます。</summary>
    /// <returns>解決済みノードと、副作用開始前の問題です。</returns>
    internal static (NodeDefinition Node, NodeResult? Error) Resolve(NodeDefinition node, INode implementation,
        IReadOnlyList<WireDefinition> wires, IReadOnlyDictionary<string, NodeValue> inputs)
    {
        WireDefinition[] settings = wires.Where(wire => SettingsBinding.IsPort(wire.ToPort)).ToArray();
        if (settings.Length == 0)
        {
            return (node with { Settings = OptionalSettingValues.Normalize(node.Settings, implementation.Descriptor.SettingsSchema) }, null);
        }
        List<ValidationIssue> issues = [];
        NodeDefinition resolved = SettingsBinding.Overlay(node, implementation.Descriptor, settings, inputs, issues);
        if (issues.Count == 0)
        {
            issues.AddRange(Validate(resolved, implementation, []));
        }
        NodeResult? error = null;
        if (issues.Count != 0)
        {
            error = NodeResult.Failure(issues[0].Code, issues[0].Message, EOutcome.NotExecuted);
        }
        return (resolved, error);
    }
}
