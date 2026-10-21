using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Observability;

/// <summary>並列実行の記録を観測し、ログ保存側の故障を注入します。</summary>
internal sealed class RecordingWorkflowLog : IWorkflowLog
{
    /// <summary>受付順に保持する試験用記録です。</summary>
    internal ConcurrentQueue<WorkflowLogEntry> Entries
    {
        get;
    } = new();

    /// <summary>記録受付の例外を再現します。</summary>
    internal bool Throw
    {
        get; init;
    }

    /// <summary>一件のログと、取消に依存しない受付を確認します。</summary>
    /// <returns>受付結果です。</returns>
    public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entries.Enqueue(entry);
        if (Throw)
        {
            throw new IOException("Test log unavailable");
        }
        return ValueTask.CompletedTask;
    }
}
