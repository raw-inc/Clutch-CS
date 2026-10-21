using ReArtWord.Contents.BackgroundService.Contracts.Observability;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;

namespace ReArtWord.Contents.BackgroundService.Runtime.Observability;

/// <summary>秘密値・個人情報・保持規則を適用して記録する境界です。</summary>
public interface IWorkflowLog
{
    /// <summary>ログを受け付けます。保存不能だけで本来処理を停止しません。</summary>
    /// <returns>受付の完了です。</returns>
    public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default);
}

