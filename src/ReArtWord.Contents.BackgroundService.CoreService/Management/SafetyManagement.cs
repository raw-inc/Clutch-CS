using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Safety;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting;
using ReArtWord.Contents.BackgroundService.CoreService.Hosting.Composition;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence;
using ReArtWord.Contents.BackgroundService.Infrastructure.Persistence.Receipts;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows;
using ReArtWord.Contents.BackgroundService.Runtime.Workflows.Compilation;

namespace ReArtWord.Contents.BackgroundService.CoreService.Management;

/// <summary>個別ノードと事前定義Groupの停止・一時続行・通常判断への復旧を扱います。</summary>
internal static class SafetyManagement
{
    /// <summary>通常の人為操作履歴を生成せず、現在状態と結果確認を公開します。</summary>
    public static void Register(ManagementRegistry registry, ServiceRuntime runtime, IWorkflowLog log)
    {
        registry.ReadValue("safety.read", _ => new { loaded = runtime.ManualSafety.Loaded, states = runtime.ManualSafety.Current });
        registry.Read("safety.operation", async (value, cancellation) => await runtime.ManualSafety.GetOperationAsync(ManagementRegistry.OperationId(value), cancellation).ConfigureAwait(false));
        registry.ReadValue("safety.preview", value => Preview(ManagementRegistry.Body<ManualSafetyChange>(value), runtime));
        registry.Write("safety.change", (value, cancellation) => ChangeAsync(ManagementRegistry.Body<ManualSafetyChange>(value), runtime, log, cancellation));
    }

    /// <summary>確認方針は共通既定をノード設定で上書きし、Group操作は全対象を評価します。</summary>
    /// <returns>追加確認の要否と、この時点の実対象です。</returns>
    private static ManualSafetyPreview Preview(ManualSafetyChange request, ServiceRuntime runtime)
    {
        if (request.Stopped && request.Continued)
        {
            throw new ArgumentException("一時停止と一時続行を同時に指定できません。", nameof(request));
        }
        CompiledWorkflow plan = runtime.Sources.GetEffective(request.WorkflowId) ?? throw new KeyNotFoundException();
        NodeDefinition[] nodes = plan.OrderedNodes.Where(node => string.Equals(node.Id, request.Scope, StringComparison.Ordinal) || string.Equals(node.Safety?.Group, request.Scope, StringComparison.Ordinal)).ToArray();
        if (nodes.Length == 0)
        {
            throw new KeyNotFoundException();
        }
        ESafetyConfirmation common = (runtime.Operational.Capture() ?? throw new InvalidOperationException("共通運用設定を確認できません。")).Settings.SafetyConfirmation;
        bool confirmation = nodes.Any(node =>
        {
            ESafetyConfirmation policy = node.Safety?.Confirmation ?? common;
            return ((request.Stopped || request.Continued) && policy == ESafetyConfirmation.Always)
                || (request.Continued && policy == ESafetyConfirmation.Continue);
        });
        ManualSafetyTarget[] targets = nodes.Select(node => Describe(request.WorkflowId, node, runtime)).ToArray();
        return new(request.WorkflowId, request.Scope, plan.Revision, confirmation, targets,
            "新規入力と未開始の待機入力へ適用します。開始済み処理や外部機器の実停止・復旧は、この操作だけでは確認できません。別のNode・Groupの人為停止は一時続行で解除されません。");
    }

    /// <summary>続行による上書き中も、通常なら停止する理由を保持します。</summary>
    /// <returns>一ノードの現在判断です。</returns>
    private static ManualSafetyTarget Describe(string workflowId, NodeDefinition node, ServiceRuntime runtime)
    {
        bool stopped = runtime.Safety.IsStopped(workflowId, node);
        bool manual = runtime.ManualSafety.IsStopped(workflowId, node);
        string? automatic = runtime.Safety.GetAutomaticStopReason(node);
        string? reason = null;
        if (stopped)
        {
            reason = automatic;
            if (manual)
            {
                reason = "safety.manual";
            }
        }
        return new(node.Id, node.Type, stopped, automatic is not null, manual, runtime.ManualSafety.IsContinued(workflowId, node), reason, automatic);
    }

    /// <summary>設定された確認だけを要求し、既定では追加確認なしに実行します。</summary>
    /// <returns>保存確認とローカル実効判断です。</returns>
    private static async Task<object?> ChangeAsync(ManualSafetyChange request, ServiceRuntime runtime, IWorkflowLog log, CancellationToken cancellationToken = default)
    {
        ManualSafetyPreview preview = Preview(request, runtime);
        if (preview.ConfirmationRequired && (!request.ImpactConfirmed || !string.Equals(request.ExpectedWorkflowRevision, preview.WorkflowRevision, StringComparison.Ordinal)))
        {
            return new { request.OperationId, outcome = EOutcome.NotExecuted, reason = "safety.confirmationRequired", preview };
        }
        DurableReceipt receipt = await runtime.ManualSafety.ChangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (receipt.Outcome != EOutcome.Succeeded)
        {
            await WorkflowLogBoundary.WriteAsync(log, new WorkflowLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "ERROR", runtime.Cluster.LocalPeerId,
                "人為的一時操作の保存・共有を確認できません。元要求の結果と現在状態を確認してください。", request.WorkflowId), CancellationToken.None).ConfigureAwait(false);
        }
        return new
        {
            operationId = request.OperationId, receipt.Outcome, receipt.Reason, applied = runtime.ManualSafety.Current,
            current = Preview(request, runtime), message = "新規入力の判断を保存しました。復旧は現在の通常判断へ戻す操作であり、常に再開する意味ではありません。",
        };
    }
}
