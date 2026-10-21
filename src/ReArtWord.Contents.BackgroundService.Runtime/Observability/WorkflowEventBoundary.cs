using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>観測通知の障害で、既に確認できた処理結果を変更しないための境界です。</summary>
public static class WorkflowEventBoundary
{
    /// <summary>発行先があれば通知を試みます。欠落の表示は配信実装が担当します。</summary>
    /// <returns>今回の発行試行の完了。</returns>
    public static async ValueTask PublishAsync(IWorkflowEvents? events, WorkflowEvent item, CancellationToken cancellationToken = default)
    {
        if (events is null)
        {
            return;
        }
        try
        {
            await events.PublishAsync(item, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // 通知の成否を元の処理の成否へ読み替えません。
        }
    }
}
