using ReArtWord.Contents.BackgroundService.Contracts.Execution;
using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Workflow;
using ReArtWord.Contents.BackgroundService.Runtime.Cluster;
using ReArtWord.Contents.BackgroundService.Runtime.Configuration;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;

/// <summary>一回の実行に固定した設定・所有者で区間の枠を取得します。</summary>
internal sealed class WorkflowConcurrencyExecution(IWorkflowConcurrencyLimiter? limiter, IPeerDirectory directory,
    string workflowId, Guid runId, Action abort)
{
    /// <summary>同じ区間への到達ごとに固有番号を作り、別の要素や受信を混ぜません。</summary>
    /// <returns>取得済みの枠、または未開始理由です。</returns>
    internal Task<WorkflowConcurrencyAdmission> AcquireAsync(NodeDefinition node, CancellationToken cancellationToken = default)
    {
        if (limiter is null)
        {
            return Task.FromResult(new WorkflowConcurrencyAdmission(null, NodeResult.Failure("concurrency.unavailable", "Cluster concurrency coordination is unavailable.")));
        }
        NodeSettings settings = new(node.Settings);
        string proceed = "proceed";
        WorkflowConcurrencyRequest request = new(Guid.CreateVersion7(), workflowId, node.Id, runId, directory.LocalPeerId, directory.LocalInstanceId,
            settings.ReadInt32("maxConcurrency", 1), settings.ReadInt32("waitTimeoutMs", 30000), string.Equals(settings.ReadText("timeoutMode", "fail"), proceed, StringComparison.Ordinal));
        return limiter.AcquireAsync(request, cancellationToken);
    }

    /// <summary>待機失敗ではこの実行の未開始処理を止め、開始済み処理は所有元が回収します。</summary>
    internal void Abort() => abort();
}
