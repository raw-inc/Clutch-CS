namespace ReArtWord.Contents.BackgroundService.Runtime.Scheduling;

/// <summary>一つの時間指定ノードから、元の待受に対応する進行記録を使用します。</summary>
public interface INodeScheduleCursor
{
    /// <summary>同じ時間指定の基準時刻と、既に受け付けた予定位置を読みます。</summary>
    /// <returns>確認結果を含む進行記録。</returns>
    public ValueTask<ScheduleCursor> ReadAsync(string timelineKey, DateTimeOffset initialAnchor, CancellationToken cancellationToken = default);

    /// <summary>通知する前に、その回の受付を比較更新で記録します。</summary>
    /// <returns>更新後の記録。確認不能なら新しい入力を発生させません。</returns>
    public ValueTask<ScheduleCursor> AdvanceAsync(ScheduleCursor cursor, Nodes.NodeEmission emission, CancellationToken cancellationToken = default);

    /// <summary>対象となる予定がない場合も、終了を一度だけ記録します。</summary>
    /// <returns>終了を記録した位置、または保存できなかった理由。</returns>
    public ValueTask<ScheduleCursor> FinishAsync(ScheduleCursor cursor, CancellationToken cancellationToken = default);
}
