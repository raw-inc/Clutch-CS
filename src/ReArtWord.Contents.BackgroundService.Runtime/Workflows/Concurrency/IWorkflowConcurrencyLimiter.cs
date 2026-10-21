using ReArtWord.Contents.BackgroundService.Contracts.Execution.Concurrency;

namespace ReArtWord.Contents.BackgroundService.Runtime.Workflows.Concurrency;

/// <summary>同じワークフロー・ノードの実行枠をクラスタ全体で共有する境界です。</summary>
public interface IWorkflowConcurrencyLimiter
{
    /// <summary>空き枠または指定した待機期限まで待ち、一回の区間へ所有権を渡します。</summary>
    /// <returns>区間終了時に必ず解放する枠、または具体的な拒否理由です。</returns>
    public Task<WorkflowConcurrencyAdmission> AcquireAsync(WorkflowConcurrencyRequest request, CancellationToken cancellationToken = default);
}
