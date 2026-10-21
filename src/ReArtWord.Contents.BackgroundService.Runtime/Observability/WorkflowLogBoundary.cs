using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>記録系の障害を本来処理へ伝播させない共通境界です。</summary>
public static class WorkflowLogBoundary
{
    /// <summary>秘密の除去と保持はログ実装に委ね、障害だけをここで隔離します。</summary>
    /// <returns>非阻害な受付です。</returns>
    // 抑制理由: 任意ログ実装の障害を主処理へ伝播させない要件の境界です。
    [SuppressMessage("Design", "CA1031", Justification = "ログの故障が処理結果を変えないようにします。状態はログ実装の診断に残します。")]
    public static async ValueTask WriteAsync(IWorkflowLog? log, WorkflowLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (log is null)
        {
            return;
        }
        try
        {
            await log.WriteAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 記録の欠落状況はログ実装の診断表示で確認します。
        }
    }
}
