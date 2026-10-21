using ReArtWord.Contents.BackgroundService.Contracts.Observability;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>観測した事象の配信と購読を提供します。</summary>
public interface IWorkflowEvents
{
    /// <summary>新しい事象を購読者へ通知します。</summary>
    /// <returns>受付の完了です。</returns>
    public ValueTask PublishAsync(WorkflowEvent item, CancellationToken cancellationToken = default);

    /// <summary>事象を購読します。個別条件は購読側で評価します。</summary>
    /// <returns>購読開始以降の事象です。</returns>
    public IAsyncEnumerable<WorkflowEvent> SubscribeAsync(CancellationToken cancellationToken = default);
}

