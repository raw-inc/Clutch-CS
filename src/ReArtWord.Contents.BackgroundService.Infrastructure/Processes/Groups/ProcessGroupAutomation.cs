using System.Diagnostics.CodeAnalysis;
using ReArtWord.Contents.BackgroundService.Contracts.Nodes;
using ReArtWord.Contents.BackgroundService.Contracts.Processes;
using ReArtWord.Contents.BackgroundService.Runtime.Observability;

namespace ReArtWord.Contents.BackgroundService.Infrastructure.Processes.Groups;

/// <summary>一つのグループの停止待ちから、他グループとCoreの寿命を分離します。</summary>
public sealed class ProcessGroupAutomation(ProcessService service, string peerId, IWorkflowLog? log = null)
{
    private readonly Dictionary<ProcessGroupSession, Task> pending = [];
    private readonly Dictionary<string, string> failures = new(StringComparer.Ordinal);
    private readonly Lock failureGate = new();

    /// <summary>開始した全作業を所有し、終了要求時には実停止を待ちます。</summary>
    /// <returns>監視所有者の終了までの作業です。</returns>
    public async Task RunAsync(Func<bool> canOperate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canOperate);
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(service.Options.PollMilliseconds));
        try
        {
            do
            {
                if (canOperate())
                {
                    await DispatchAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await Task.WhenAll(pending.Values).ConfigureAwait(false);
        }
    }

    /// <summary>同じグループへ巡回を重ねずに、新しい確認を開始します。</summary>
    /// <returns>現在の対象への開始完了です。</returns>
    // 抑制理由: 読取り失敗を隔離します。開始したTaskはpendingへ保存し、RunAsyncのfinallyでawaitした後に所有者がSessionを破棄します。
    [SuppressMessage("Design", "CA1031", Justification = "登録データの破損や一時I/O失敗からCoreの常駐寿命を分離します。"),
     SuppressMessage("SourceQuality", "RAW511", Justification = "実行中の全タスクを監視所有者の終了まで追跡します。"),
     SuppressMessage("Reliability", "CA2025", Justification = "列挙中のSessionをここでは破棄せず、実行中Taskの終了を所有者が待ちます。")]
    private async ValueTask DispatchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (ProcessGroupSession session in pending.Keys.ToArray())
            {
                if (pending[session].IsCompleted)
                {
                    await pending[session].ConfigureAwait(false);
                    pending.Remove(session);
                }
            }
            (IReadOnlyList<ProcessGroupSession> sessions, IReadOnlyList<RegisteredProcess> applications) = await service.Groups.CaptureAsync(cancellationToken).ConfigureAwait(false);
            foreach (ProcessGroupSession session in sessions)
            {
                if (!pending.ContainsKey(session))
                {
                    pending[session] = CheckAsync(session, applications, cancellationToken);
                }
            }
            using (failureGate.EnterScope())
            {
                failures.Remove(string.Empty);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException && !cancellationToken.IsCancellationRequested)
        {
            await ReportAsync(string.Empty, exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>一グループの確認不能な操作は、保存済みの位置から次回照合します。</summary>
    /// <returns>そのグループだけの確認完了です。</returns>
    // 抑制理由: 非致命的な例外はグループ単位で隔離し、他グループの操作を止めません。
    [SuppressMessage("Design", "CA1031", Justification = "補助処理の例外でCore全体を終了しません。未確定操作は永続受付から照合します。")]
    private async Task CheckAsync(ProcessGroupSession session, IReadOnlyList<RegisteredProcess> applications, CancellationToken cancellationToken = default)
    {
        string id = session.Registration.Definition.Id;
        try
        {
            await session.TickAsync(applications, cancellationToken).ConfigureAwait(false);
            using (failureGate.EnterScope())
            {
                failures.Remove(id);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException && !cancellationToken.IsCancellationRequested)
        {
            await ReportAsync(id, exception.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>同じ障害を毎巡回記録せず、登録IDと例外種別を残します。</summary>
    /// <returns>非阻害の診断記録です。</returns>
    private ValueTask ReportAsync(string id, string reason, CancellationToken cancellationToken = default)
    {
        using (failureGate.EnterScope())
        {
            if (failures.TryGetValue(id, out string? previous) && string.Equals(previous, reason, StringComparison.Ordinal))
            {
                return ValueTask.CompletedTask;
            }
            failures[id] = reason;
        }
        return WorkflowLogBoundary.WriteAsync(log, new(Guid.NewGuid(), DateTimeOffset.UtcNow, "WARN", peerId, "ApplicationGroup.Automation",
            Data: NodeValue.FromObject(new { groupId = id, reason })), cancellationToken);
    }
}
