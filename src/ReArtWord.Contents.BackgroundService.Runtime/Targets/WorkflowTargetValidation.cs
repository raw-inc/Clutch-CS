using ReArtWord.Contents.BackgroundService.Contracts.Configuration;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Runtime.Targets;

/// <summary>従来のPeer・Host Group指定の意味を保ち、矛盾する対象指定を拒否します。</summary>
internal static class WorkflowTargetValidation
{
    /// <summary>実行回数とPeer指定の組合せを検証します。</summary>
    internal static void Validate(NodeDefinition node, List<ValidationIssue> issues)
    {
        TargetDefinition? target = node.Target;
        if (target is not null)
        {
            if (!Enum.IsDefined(target.Scope) || (target.Peers?.Any(string.IsNullOrWhiteSpace) ?? false) || (target.Groups?.Any(string.IsNullOrWhiteSpace) ?? false))
            {
                issues.Add(new ValidationIssue("target.invalid", "有効な実行範囲と空ではない識別子を指定してください。", node.Id));
            }
            int count = (target.Peers?.Count ?? 0) + (target.Groups?.Count ?? 0);
            if (target.SourcePeer && (target.Scope != EExecutionScope.One || count != 0))
            {
                issues.Add(new ValidationIssue("target.source", "SourcePeerはOneと組み合わせ、Peer/Group一覧を併用しないでください。", node.Id));
            }
            if ((!target.SourcePeer && count == 0) || (target.Peers?.Count != 0 && target.Groups?.Count != 0 && target.Peers is not null && target.Groups is not null))
            {
                issues.Add(new ValidationIssue("target.invalid", "Peer集合またはHost Group集合の一方を指定してください。", node.Id));
            }
            if (!target.SourcePeer && target.Scope == EExecutionScope.One && (target.Peers?.Count != 1 || target.Groups?.Count is > 0))
            {
                issues.Add(new ValidationIssue("target.one", "Oneは一つのPeerを指定してください。", node.Id));
            }
        }
    }
}
