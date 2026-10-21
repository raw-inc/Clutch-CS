using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>元の待受を固定して、ノードからの進行記録操作を保存先へ渡します。</summary>
internal sealed class SourceScheduleAccess(ISourceScheduleStore store, RemoteExecutionRequest source, Func<SourceExecutionInfo> information) : INodeScheduleCursor
{
    /// <summary>担当PCの都合で基準時刻を作り直さず、元の時間指定を照会します。</summary>
    /// <returns>現在の進行記録。</returns>
    public ValueTask<ScheduleCursor> ReadAsync(string timelineKey, DateTimeOffset initialAnchor, CancellationToken cancellationToken = default)
        => store.ReadAsync(source, timelineKey, initialAnchor, cancellationToken);

    /// <summary>通知する一回の位置を、元の待受の実行区分に記録します。</summary>
    /// <returns>受付記録の確認結果。</returns>
    public ValueTask<ScheduleCursor> AdvanceAsync(ScheduleCursor cursor, Nodes.NodeEmission emission, CancellationToken cancellationToken = default)
        => store.AdvanceAsync(source, cursor, ScheduleInputSnapshot.Capture(information(), emission), cancellationToken);

    /// <summary>実行予定がない待受も、元の時間指定の終了として記録します。</summary>
    /// <returns>終了記録の確認結果。</returns>
    public ValueTask<ScheduleCursor> FinishAsync(ScheduleCursor cursor, CancellationToken cancellationToken = default)
        => store.FinishAsync(source, cursor, cancellationToken);
}
