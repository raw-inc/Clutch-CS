using ReArtWord.Contents.BackgroundService.Contracts.Execution;

namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>待受の実行区分を保って、時間指定の進行記録を読み書きします。</summary>
public interface ISourceScheduleStore
{
    /// <summary>初回だけ基準時刻を記録し、担当変更後は既存の位置を返します。</summary>
    /// <returns>確認結果を含む進行記録。</returns>
    public ValueTask<ScheduleCursor> ReadAsync(RemoteExecutionRequest source, string timelineKey, DateTimeOffset initialAnchor, CancellationToken cancellationToken = default);

    /// <summary>確認した位置の続きとして、一回だけ受付記録を進めます。</summary>
    /// <returns>更新後の記録、または更新できなかった理由。</returns>
    public ValueTask<ScheduleCursor> AdvanceAsync(RemoteExecutionRequest source, ScheduleCursor cursor, ScheduleInputSnapshot input, CancellationToken cancellationToken = default);

    /// <summary>待受終了後に、受付済み予定の取消を記録します。</summary>
    /// <returns>元の待受が保持した予定について、取消記録を確認できたか。</returns>
    public ValueTask<bool> CancelAsync(RemoteExecutionRequest source, CancellationToken cancellationToken = default);

    /// <summary>発生対象がなかった時間指定の終了を比較更新します。</summary>
    /// <returns>終了を確定した進行記録。</returns>
    public ValueTask<ScheduleCursor> FinishAsync(RemoteExecutionRequest source, ScheduleCursor cursor, CancellationToken cancellationToken = default);
}
