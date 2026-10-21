using ReArtWord.Contents.BackgroundService.Contracts.Workflow;

namespace ReArtWord.Contents.BackgroundService.Contracts.Configuration.Workflows;

/// <summary>有効版から残るすべての変更に最も厳しい反映条件を適用します。</summary>
public static class WorkflowActivationPolicy
{
    /// <summary>旧方針を同時に緩める変更でも、その変更自身の確認条件を失いません。</summary>
    /// <returns>自動より明示、明示より確認付きが優先される反映方針です。</returns>
    public static EActivationPolicy Resolve(WorkflowDefinition? effective, WorkflowDefinition candidate, EActivationPolicy requested)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        EActivationPolicy result = requested;
        foreach (EWorkflowChangeKind kind in WorkflowChangeInspection.Kinds(effective, candidate))
        {
            if (effective?.ActivationRules?.TryGetValue(kind, out EActivationPolicy previous) == true && result < previous)
            {
                result = previous;
            }
            if (candidate.ActivationRules?.TryGetValue(kind, out EActivationPolicy next) == true && result < next)
            {
                result = next;
            }
        }
        return result;
    }
}
