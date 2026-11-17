namespace ReArtWord.Contents.BackgroundService.Unit.Tests.Resources;

/// <summary>OSの時計を変えず、観測の日時と経過時間を独立して進めます。</summary>
internal sealed class ResourceSelectionClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long elapsed;

    /// <summary>標本と比較時刻に使う試験日時です。</summary>
    /// <returns>時計補正を含む日時です。</returns>
    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>日時の補正とは独立した経過時間です。</summary>
    /// <returns>試験開始からのtick数です。</returns>
    public override long GetTimestamp() => elapsed;

    /// <summary>TimeSpanと同じ単位で経過時間を指定します。</summary>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>時計の進みだけを変更し、実際の経過時間には加算しません。</summary>
    internal void Adjust(TimeSpan offset) => now += offset;

    /// <summary>試験中の実経過を進めます。</summary>
    internal void Advance(TimeSpan duration)
    {
        now += duration;
        elapsed += duration.Ticks;
    }
}
