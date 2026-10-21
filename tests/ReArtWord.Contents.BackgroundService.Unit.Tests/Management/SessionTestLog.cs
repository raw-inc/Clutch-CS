using System.Collections.Concurrent;
using ReArtWord.Contents.BackgroundService.Contracts.Observability.Logging;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Management;

/// <summary>接続処理の診断を複数の非同期経路から安全に受け取ります。</summary>
internal sealed class SessionTestLog : IWorkflowLog
{
    /// <summary>受け付けた診断だけを保持します。</summary>
    internal ConcurrentQueue<WorkflowLogEntry> Entries
    {
        get;
    } = new();

    /// <summary>試験で指定した記録不能を再現します。</summary>
    internal Exception? Failure
    {
        get; set;
    }

    /// <summary>ログの例外本文や秘密値が含まれないことを確認するために記録します。</summary>
    /// <returns>即時の受付、または指定された失敗です。</returns>
    public ValueTask WriteAsync(WorkflowLogEntry entry, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            return ValueTask.FromException(Failure);
        }
        Entries.Enqueue(entry);
        return ValueTask.CompletedTask;
    }
}
